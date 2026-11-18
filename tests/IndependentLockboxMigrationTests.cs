using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class IndependentLockboxMigrationTests
{
    [TestMethod]
    public async Task SqliteUpgradePreservesPendingDeniedAndExecutedHistory()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:"); await connection.OpenAsync();
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite(connection).Options);
        await VerifyUpgradeAsync(db);
    }

    // Also run against an isolated MySQL database during release acceptance.
    public static async Task VerifyUpgradeAsync(TemplateDbContext db)
    {
        var migrator = db.GetService<IMigrator>();
        var previous = db.Database.GetMigrations().TakeWhile(id => !id.EndsWith("_IndependentLockbox", StringComparison.Ordinal)).Last();
        await migrator.MigrateAsync(previous);
        var history = new List<(Guid Box, Guid Legacy, Guid? Execution, DecisionState Decision)>();
        foreach (var decision in new[] { DecisionState.Pending, DecisionState.Denied, DecisionState.Approved })
        {
            var id = Guid.NewGuid(); var wrapper = Guid.NewGuid();
            Guid? execution = decision == DecisionState.Approved ? Guid.NewGuid() : null;
            history.Add((id, wrapper, execution, decision));
            var at = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Lockboxes (Id, ClientRequestId, RequesterId, Definition, Version, Risk, Parameters, TargetMachines, Reason, CreatedAt, Decision, Automatic, DecisionReason, WorkflowId, OperationId) VALUES ({id}, {Guid.NewGuid()}, {"requester"}, {WorkflowNames.ListMachines}, {WorkflowNames.Version}, {0}, {"{}"}, {"[]"}, {"Preserve"}, {at}, {(int)decision}, {false}, {"Original decision"}, {wrapper}, {execution})");
            foreach (var run in new (Guid Id, Guid? Parent, string Name)[] { (wrapper, null, WorkflowNames.Lockbox), (execution ?? Guid.Empty, wrapper, WorkflowNames.ListMachines) }.Where(r => r.Id != Guid.Empty))
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WorkflowRuns (Id, RootId, ParentId, LockboxId, Definition, State, CreatedAt, Result, Input, Version, IsMachineTask, LastSequence) VALUES ({run.Id}, {wrapper}, {run.Parent}, {id}, {run.Name}, {(int)(execution == null ? RunState.WaitingApproval : RunState.Succeeded)}, {at}, {"Historical output"}, {"{}"}, {WorkflowNames.Version}, {false}, {7L})");
        }
        await migrator.MigrateAsync(); await migrator.MigrateAsync();
        foreach (var original in history)
        {
            var box = await db.Lockboxes.SingleAsync(b => b.Id == original.Box);
            Assert.AreEqual(original.Legacy, box.LegacyApprovalWorkflowId);
            Assert.AreEqual(original.Execution, box.WorkflowId); Assert.AreEqual(original.Decision, box.Decision);
            var runs = await db.WorkflowRuns.Where(r => r.LockboxId == original.Box).ToListAsync();
            Assert.HasCount(original.Execution == null ? 1 : 2, runs);
            Assert.IsTrue(runs.All(r => r.RootId == original.Legacy && r.LastSequence == 7 && r.Result == "Historical output"));
            if (original.Execution != null) Assert.AreEqual(original.Legacy, runs.Single(r => r.Id == original.Execution).ParentId);
        }
        Assert.AreEqual(0, await db.WorkflowAuditOutbox.CountAsync(), "Migration must not fabricate new audit facts.");
    }
}
