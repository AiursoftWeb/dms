using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class ScheduledWorkflowMigrationTests
{
    [TestMethod]
    public async Task ExistingManualHistorySurvivesAndAutomaticRunsNeedNoApprovalEntity()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:"); await connection.OpenAsync();
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite(connection).Options);
        await VerifyUpgradeAsync(db);
    }

    public static async Task VerifyUpgradeAsync(TemplateDbContext db)
    {
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(db.Database.GetMigrations().TakeWhile(id => !id.EndsWith("_ScheduledWorkflows", StringComparison.Ordinal)).Last());
        var root = Guid.NewGuid(); var child = Guid.NewGuid(); var box = Guid.NewGuid(); var at = DateTime.UtcNow;
        foreach (var (id, parent) in new (Guid, Guid?)[] { (root, null), (child, root) })
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WorkflowRuns (Id,RootId,ParentId,LockboxId,Definition,State,CreatedAt,Result,Input,Version,IsMachineTask,LastSequence) VALUES ({id},{root},{parent},{box},{"manual"},{(int)RunState.Succeeded},{at},{"original"},{"{}"},{"1"},{false},{1L})");
        await migrator.MigrateAsync(); await migrator.MigrateAsync();
        var history = await db.WorkflowRuns.AsNoTracking().OrderBy(r => r.ParentId).ToListAsync();
        Assert.HasCount(2, history);
        Assert.IsTrue(history.All(r => r.LockboxId == box && r.Origin == WorkflowOrigin.Manual && r.Result == "original" && r.ScheduleId == null));
        Assert.AreEqual(WorkflowTrigger.ManualRequest, history.Single(r => r.Id == root).Trigger);
        Assert.AreEqual(WorkflowTrigger.Subworkflow, history.Single(r => r.Id == child).Trigger);
        var automatic = new WorkflowRun { Id = Guid.NewGuid(), Origin = WorkflowOrigin.Automatic, Trigger = WorkflowTrigger.Cron,
            Definition = "machine.agent.scheduled-status", ScheduleId = "machine.agent.scheduled-status",
            ScheduleExpression = "*/5 * * * *", ScheduledAt = at, State = RunState.Succeeded };
        automatic.RootId = automatic.Id; db.WorkflowRuns.Add(automatic); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.IsNull((await db.WorkflowRuns.SingleAsync(r => r.Id == automatic.Id)).LockboxId);
        var fact = (await db.WorkflowAuditOutbox.SingleAsync()).Read();
        Assert.AreEqual(WorkflowOrigin.Automatic, fact.Origin); Assert.IsNull(fact.LockboxId); Assert.IsNull(fact.RequesterId);
        Assert.AreEqual(0, await db.Lockboxes.CountAsync());
    }
}
