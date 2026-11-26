using System.Data.Common;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Dms;
using ClickHouse.Client.ADO;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class TraceStorageTests
{
    [TestMethod]
    public void RestartCountsExistingFilesAndRejectsAnEventThatWouldExceedTheQuota()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var config = Configuration(directory.FullName);
            var id = Guid.NewGuid();
            var item = new WorkflowEvent { WorkflowId = id, Sequence = 1, Message = "已确认" };
            new TraceStore(config).Enqueue(id, item);
            using (var file = File.Create(Path.Combine(directory.FullName, $"{Guid.NewGuid():N}-00000000000000000001.json")))
                file.SetLength(256L * 1024 * 1024 - 8192);
            var store = new TraceStore(config);
            Assert.AreEqual(2, store.PendingCount);
            store.Enqueue(id, item);
            item.Sequence++;
            item.Message = new string('界', 2000);
            Assert.Throws<IOException>(() => store.Enqueue(id, item));
            item.Message = "fits the last block";
            store.Enqueue(id, item);
            item.Sequence++;
            Assert.Throws<IOException>(() => store.Enqueue(id, item));
            Assert.AreEqual(3, store.PendingCount);
            Assert.HasCount(3, directory.GetFiles());
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    public async Task ArchiveMigratesRetentionAndReplaysFailedBatchesWithoutDuplicateReports()
    {
        var endpoint = Environment.GetEnvironmentVariable("DMS_TEST_CLICKHOUSE_CONNECTION");
        if (string.IsNullOrWhiteSpace(endpoint))
            Assert.Inconclusive("Set DMS_TEST_CLICKHOUSE_CONNECTION to a disposable ClickHouse test server.");
        var name = $"dms_trace_test_{Guid.NewGuid():N}";
        await using var admin = new ClickHouseConnection(endpoint);
        await admin.OpenAsync();
        async Task ExecuteAsync(string sql)
        {
            await using var command = admin.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            await ExecuteAsync($"CREATE DATABASE {name}");
            var table = $"{name}.DmsWorkflowTrace";
            await ExecuteAsync($"CREATE TABLE {table} (EventId String, WorkflowId String, Time DateTime64(3, 'UTC'), Source String, Message String) ENGINE = ReplacingMergeTree ORDER BY (WorkflowId, EventId)");
            await ExecuteAsync($"INSERT INTO {table} VALUES ('old','legacy',now() - INTERVAL 121 DAY,'data','expired'), ('old-log','legacy',now() - INTERVAL 121 DAY,'stdout','expired'), ('recent','legacy',now() - INTERVAL 119 DAY,'data','retained report'), ('recent-log','legacy',now() - INTERVAL 119 DAY,'stdout','retained log')");
            var connection = new DbConnectionStringBuilder { ConnectionString = endpoint, ["Database"] = name }.ConnectionString;
            var config = Configuration(directory.FullName, connection);
            await using var archive = new TraceDatabase(config);
            await archive.InitializeAsync();
            await ExecuteAsync($"ALTER TABLE {table} MATERIALIZE TTL SETTINGS mutations_sync = 1");
            var legacy = await archive.ReadAsync(["legacy"], 0);
            CollectionAssert.AreEquivalent(new[] { "retained report", "retained log" }, legacy.Select(e => e.Message).ToArray());

            var id = Guid.NewGuid();
            var store = new TraceStore(config);
            var run = new WorkflowRun { Origin = WorkflowOrigin.Automatic, Trigger = WorkflowTrigger.Cron, ScheduleId = "fixture" };
            const string message = "多行\n'quoted'\\trace";
            var time = DateTime.UtcNow.Date.AddMilliseconds(123);
            var item = new WorkflowEvent { WorkflowId = id, Time = time, Source = "data", Message = message };
            for (var i = 1; i <= 101; i++)
            {
                item.Sequence = i;
                store.Enqueue(id, item, run);
            }
            await ExecuteAsync($"ALTER TABLE {table} ADD CONSTRAINT reject_fixture CHECK Source != 'data'");
            await Assert.ThrowsAsync<ClickHouse.Client.ClickHouseServerException>(() => store.FlushAsync(archive));
            Assert.AreEqual(101, store.PendingCount);
            await ExecuteAsync($"ALTER TABLE {table} DROP CONSTRAINT reject_fixture");
            Assert.IsTrue(await store.FlushAsync(archive));
            Assert.AreEqual(1, store.PendingCount);
            Assert.IsFalse(await store.FlushAsync(archive));
            store.Enqueue(id, item, run); // Retry after ClickHouse committed the original insert.
            Assert.IsFalse(await store.FlushAsync(archive));
            Assert.AreEqual(0, new TraceStore(config).PendingCount);
            var entries = await archive.ReadAsync([id.ToString()], 0);
            Assert.HasCount(101, entries);
            Assert.IsTrue(entries.All(e => e.Message == message && e.Source == "data" && e.Time == time && e.ScheduleId == "fixture"
                && e.Origin == WorkflowOrigin.Automatic && e.Trigger == WorkflowTrigger.Cron));
            Assert.HasCount(101, await archive.ReadDataAsync(id, 200));

            config["Dms:Clickhouse:RetentionDays"] = "240";
            await using var extended = new TraceDatabase(config);
            await extended.InitializeAsync();
            config["Dms:Clickhouse:RetentionDays"] = "0";
            await using var unmanaged = new TraceDatabase(config);
            await unmanaged.InitializeAsync();
            await using var ddl = admin.CreateCommand();
            ddl.CommandText = $"SHOW CREATE TABLE {table}";
            StringAssert.Contains((string)(await ddl.ExecuteScalarAsync())!, "toIntervalDay(240)");
            await ExecuteAsync($"DROP TABLE {table} SYNC");
            await unmanaged.InitializeAsync();
            Assert.DoesNotContain("TTL", (string)(await ddl.ExecuteScalarAsync())!);
        }
        finally
        {
            directory.Delete(true);
            await ExecuteAsync($"DROP DATABASE IF EXISTS {name} SYNC");
        }
    }

    private static IConfiguration Configuration(string path, string? connection = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dms:StatePath"] = path, ["Dms:Clickhouse:Enabled"] = (connection != null).ToString(),
            ["Dms:Clickhouse:ConnectionString"] = connection
        }).Build();
}
