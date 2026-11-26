using System.Text.Json;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class AgentJournalStorageTests
{
    [TestMethod]
    public async Task InterruptedAppendsRecoverTheCommittedPrefixAndKeepSequenceNumbers()
    {
        var directory = Directory.CreateTempSubdirectory();
        var recovery = directory.CreateSubdirectory("recovery");
        var id = Guid.NewGuid();
        var name = $"{id:N}.json";
        var copy = Path.Combine(recovery.FullName, name);
        try
        {
            using var journal = new TaskJournal(directory.FullName);
            journal.Accept(new AgentMessage { TaskId = id, Definition = "fixture" });
            await journal.ReportAsync(id, new WorkflowEvent { WorkflowId = id, IsTrace = true, Message = "已收到\n1" });
            var first = File.ReadAllBytes(Path.Combine(directory.FullName, name));
            await journal.ReportAsync(id, new WorkflowEvent { WorkflowId = id, IsTrace = true, Message = "second" });
            var second = File.ReadAllBytes(Path.Combine(directory.FullName, name));
            Assert.IsTrue(second.AsSpan().StartsWith(first));
            journal.Acknowledge(id, 1);
            var ack = File.ReadAllBytes(Path.Combine(directory.FullName, name));
            Assert.IsTrue(ack.AsSpan().StartsWith(second));
            foreach (var end in new[] { first.Length + 1, second.Length - 1, second.Length, ack.Length - 1, ack.Length })
            {
                File.WriteAllBytes(copy, ack[..end]);
                using var recovered = new TaskJournal(recovery.FullName);
                var task = recovered.Snapshot().Single();
                var committed = end < second.Length ? 1L : 2L;
                Assert.AreEqual(committed, task.Sequence);
                var pending = end == ack.Length ? new long[] { 2 } : Enumerable.Range(1, (int)committed).Select(n => (long)n).ToArray();
                CollectionAssert.AreEqual(pending, task.Pending.Select(e => e.Sequence).ToArray());
                recovered.Acknowledge(id, 99); // Future acknowledgements cannot remove these events.
                await recovered.ReportAsync(id, new WorkflowEvent { WorkflowId = id, IsTrace = true, Message = "next" });
                Assert.AreEqual(committed + 1, recovered.Snapshot().Single().Pending.Last().Sequence);
            }
            var compact = JsonSerializer.Deserialize<JournalTask>(File.ReadAllBytes(copy))!;
            Assert.AreEqual(3L, compact.Sequence);
            CollectionAssert.AreEqual(new long[] { 2, 3 }, compact.Pending.Select(e => e.Sequence).ToArray());
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    public async Task CommittedCorruptionFailsClosedAndReleasesTheJournalLock()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(directory.FullName, $"{id:N}.json");
            using (var journal = new TaskJournal(directory.FullName))
                journal.Accept(new AgentMessage { TaskId = id });
            var checkpoint = File.ReadAllBytes(path);
            byte[] broken = [.. checkpoint, 0x1e, .. "99\n"u8.ToArray()];
            await File.WriteAllBytesAsync(path, broken);
            Assert.Throws<InvalidDataException>(() => { using var journal = new TaskJournal(directory.FullName); });
            CollectionAssert.AreEqual(broken, await File.ReadAllBytesAsync(path));
            await File.WriteAllBytesAsync(path, checkpoint);
            using var reopened = new TaskJournal(directory.FullName);
            Assert.AreEqual(id, reopened.Snapshot().Single().Id);
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    public async Task AcknowledgedPrefixesCompactBeforeTheQueueEmptiesAndSnapshotsKeepTheirOwnEvents()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(directory.FullName, $"{id:N}.json");
            using var journal = new TaskJournal(directory.FullName);
            journal.Accept(new AgentMessage { TaskId = id });
            var item = new WorkflowEvent { WorkflowId = id, IsTrace = true, Message = new string('a', 8192) };
            for (var n = 1; n <= 100; n++) await journal.ReportAsync(id, item);
            var before = new FileInfo(path).Length;
            var first = journal.Snapshot(activeOnly: true, pendingLimit: 64).Single();
            Assert.HasCount(64, first.Pending);
            first.Pending.Peek().Message = "changed by reader";
            Assert.AreEqual(100, journal.Counts().Pending);
            journal.Acknowledge(id, 75);
            journal.Acknowledge(id, 74);
            journal.Acknowledge(id, 101);
            var suffix = JsonSerializer.Deserialize<JournalTask>(await File.ReadAllBytesAsync(path))!;
            Assert.IsTrue(new FileInfo(path).Length < before / 2);
            Assert.HasCount(25, suffix.Pending);
            Assert.AreEqual(76L, suffix.Pending.Peek().Sequence);
            Assert.AreEqual(item.Message, suffix.Pending.Peek().Message);
            journal.Acknowledge(id, 100);
            Assert.IsEmpty(JsonSerializer.Deserialize<JournalTask>(await File.ReadAllBytesAsync(path))!.Pending);
            Assert.AreEqual(0, journal.Counts().Pending);
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    public async Task FailedCheckpointKeepsThePreviousStateAndPendingEventsForRetry()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            var temporary = Path.Combine(directory.FullName, $"{id:N}.json.tmp");
            using var journal = new TaskJournal(directory.FullName);
            journal.Accept(new AgentMessage { TaskId = id });
            Directory.CreateDirectory(temporary);
            Func<Guid, JournalTask?> start = journal.Start;
            Assert.Throws<UnauthorizedAccessException>(() => start(id));
            Assert.AreEqual(RunState.Accepted, journal.Snapshot().Single().State);
            Directory.Delete(temporary);
            Assert.IsNotNull(journal.Start(id));
            await journal.ReportAsync(id, new WorkflowEvent { WorkflowId = id, State = RunState.Succeeded });
            Directory.CreateDirectory(temporary);
            Action<Guid, long> acknowledge = journal.Acknowledge;
            Assert.Throws<UnauthorizedAccessException>(() => acknowledge(id, 1));
            Assert.AreEqual(1, journal.Counts().Pending);
            Directory.Delete(temporary);
            journal.Acknowledge(id, 1);
            Assert.AreEqual(0, journal.Counts().Pending);
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    public async Task SerializedReportDataCountsTowardsQuotaAndTheFinalFailureCanStillBeReported()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var id = Guid.NewGuid();
            using var journal = new TaskJournal(directory.FullName);
            journal.Accept(new AgentMessage { TaskId = id });
            journal.Start(id);
            var report = new WorkflowEvent { WorkflowId = id, IsTrace = true, Result = new string('r', 1024 * 1024) };
            var charged = 0;
            for (var i = 0; i < 31; i++)
            {
                await journal.ReportAsync(id, report);
                charged += JsonSerializer.SerializeToUtf8Bytes(report).Length;
            }
            await Assert.ThrowsAsync<IOException>(() => journal.ReportAsync(id, report));
            var last = new WorkflowEvent { WorkflowId = id, IsTrace = true, Sequence = 32 };
            last.Message = new string('x', 32 * 1024 * 1024 - charged - JsonSerializer.SerializeToUtf8Bytes(last).Length);
            await journal.ReportAsync(id, last);
            var failure = new WorkflowEvent { WorkflowId = id, State = RunState.Failed, Result = "journal full" };
            await journal.ReportAsync(id, failure);
            await Assert.ThrowsAsync<IOException>(() => journal.ReportAsync(id, failure));
            Assert.AreEqual(33, journal.Counts().Pending);
            journal.Acknowledge(id, 33);
            Assert.AreEqual(RunState.Failed, journal.Snapshot().Single().State);
            Assert.IsNull(journal.Start(id));
        }
        finally { directory.Delete(true); }
    }
}
