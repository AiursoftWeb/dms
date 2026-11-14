using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class WorkflowAuditTests
{
    private static SqliteContext Open(SqliteConnection connection) => new(new DbContextOptionsBuilder<SqliteContext>().UseSqlite(connection).Options);
    private static (LockboxRequest Box, WorkflowRun Root) Request(SqliteContext db, DecisionState decision = DecisionState.Pending)
    {
        var box = new LockboxRequest { RequesterId = "engineer", ClientRequestId = Guid.NewGuid(), ClusterId = Guid.NewGuid(),
            Definition = "example.operation", Risk = RiskLevel.Medium, Decision = decision, Automatic = decision == DecisionState.Approved,
            Parameters = "never-archive-parameters", Reason = "never-archive-reason", DecisionReason = "never-archive-decision" };
        var root = new WorkflowRun { RootId = box.WorkflowId = Guid.NewGuid(), ClusterId = box.ClusterId,
            LockboxId = box.Id, Definition = WorkflowNames.Lockbox, State = RunState.WaitingApproval };
        root.Id = root.RootId;
        db.Lockboxes.Add(box); db.WorkflowRuns.Add(root);
        return (box, root);
    }

    [TestMethod]
    public async Task AutomaticApprovalAndMachineCreationCaptureOnlySafeMetadata()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Open(connection); await db.Database.EnsureCreatedAsync();
        var (box, root) = Request(db, DecisionState.Approved); box.DecidedAt = DateTime.UtcNow; root.State = RunState.Running;
        var machine = new Machine { ClusterId = box.ClusterId, InstallationId = Guid.NewGuid(), KeyHash = "never-archive-key" };
        var child = new WorkflowRun { ClusterId = box.ClusterId, RootId = root.Id, ParentId = root.Id, LockboxId = box.Id,
            MachineId = machine.Id, Definition = "internal.process", Version = "process-1", State = RunState.Queued,
            Input = "never-archive-input", Result = "never-archive-output" };
        db.Machines.Add(machine); db.WorkflowRuns.Add(child); db.SaveChanges();
        var rows = await db.WorkflowAuditOutbox.OrderBy(e => e.Id).ToListAsync();
        Assert.HasCount(4, rows); Assert.AreEqual(4, rows.Select(r => r.EventId).Distinct().Count());
        Assert.IsTrue(rows.All(r => !r.Metadata.Contains("never-archive", StringComparison.Ordinal)));
        var events = rows.Select(r => r.Read()).ToList();
        Assert.HasCount(1, events.Where(e => e.Kind == WorkflowAuditKind.LockboxSubmitted));
        var approval = events.Single(e => e.Kind == WorkflowAuditKind.LockboxApproved);
        Assert.AreEqual("engineer", approval.RequesterId); Assert.IsNull(approval.ApproverId); Assert.IsTrue(approval.AutomaticApproval);
        Assert.AreEqual(box.DecidedAt, approval.DecidedAt); Assert.IsNull(approval.State);
        var local = events.Single(e => e.WorkflowId == child.Id);
        Assert.AreEqual(root.Id, local.RootWorkflowId); Assert.AreEqual(root.Id, local.ParentWorkflowId);
        Assert.AreEqual(machine.Id, local.MachineId); Assert.AreEqual(machine.InstallationId, local.InstallationId);
        Assert.AreEqual("process-1", local.Version); Assert.AreEqual(WorkflowLocation.Machine, local.Location);
        Assert.AreEqual(RiskLevel.Medium, local.RequestedRisk); Assert.AreEqual(box.Id, local.LockboxId);
        Assert.IsTrue(events.Where(e => e.WorkflowId == root.Id).All(e => e.MachineId == null && e.InstallationId == null));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ManualApprovalOrDenialIsRecordedOnceWithOriginalRequester(bool approve)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Open(connection); await db.Database.EnsureCreatedAsync();
        var (box, root) = Request(db); await db.SaveChangesAsync();
        box.Decision = approve ? DecisionState.Approved : DecisionState.Denied;
        box.ApproverId = "reviewer"; box.DecidedAt = DateTime.UtcNow; root.State = approve ? RunState.Running : RunState.Denied;
        await db.SaveChangesAsync(); var count = await db.WorkflowAuditOutbox.CountAsync();
        await db.SaveChangesAsync(); Assert.AreEqual(count, await db.WorkflowAuditOutbox.CountAsync());
        var decision = (await db.WorkflowAuditOutbox.ToListAsync()).Select(r => r.Read()).Single(e => e.Kind == (approve ? WorkflowAuditKind.LockboxApproved : WorkflowAuditKind.LockboxDenied));
        Assert.AreEqual("engineer", decision.RequesterId); Assert.AreEqual("reviewer", decision.ApproverId);
        Assert.AreEqual(box.Definition, decision.RequestedDefinition); Assert.AreEqual(box.Version, decision.RequestedVersion);
        Assert.IsFalse(decision.AutomaticApproval); Assert.AreEqual(box.Decision, decision.Decision);
    }

    [TestMethod]
    public async Task StateHistoryPreservesUnknownAndLateSuccessWithoutLoggingHeartbeatsOrResults()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Open(connection); await db.Database.EnsureCreatedAsync();
        var (_, root) = Request(db); await db.SaveChangesAsync();
        foreach (var state in new[] { RunState.Running, RunState.NeedsVerification, RunState.Running, RunState.NeedsVerification, RunState.Succeeded })
        {
            root.State = state; if (state == RunState.Running) root.StartedAt ??= DateTime.UtcNow;
            if (state == RunState.Succeeded) root.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            root.LastSequence++; root.Result = "never-archive-output"; await db.SaveChangesAsync();
        }
        var history = (await db.WorkflowAuditOutbox.OrderBy(e => e.Id).ToListAsync()).Select(r => r.Read())
            .Where(e => e.Kind == WorkflowAuditKind.WorkflowStateChanged).ToList();
        CollectionAssert.AreEqual(new[] { RunState.Running, RunState.NeedsVerification, RunState.Running, RunState.NeedsVerification, RunState.Succeeded }, history.Select(e => e.State!.Value).ToArray());
        CollectionAssert.AreEqual(new[] { RunState.WaitingApproval, RunState.Running, RunState.NeedsVerification, RunState.Running, RunState.NeedsVerification }, history.Select(e => e.PreviousState!.Value).ToArray());
        Assert.AreEqual(root.FinishedAt, history[^1].OccurredAt);
        Assert.AreEqual(history[2].RecordedAt, history[2].OccurredAt);
    }

    [TestMethod]
    public async Task RolledBackWorkflowAndApprovalLeaveNoInvestigationFacts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using (var db = Open(connection))
        {
            await db.Database.EnsureCreatedAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            Request(db, DecisionState.Approved); await db.SaveChangesAsync();
            Assert.AreEqual(3, await db.WorkflowAuditOutbox.CountAsync());
            await transaction.RollbackAsync();
        }
        await using var read = Open(connection);
        Assert.AreEqual(0, await read.WorkflowRuns.CountAsync()); Assert.AreEqual(0, await read.Lockboxes.CountAsync());
        Assert.AreEqual(0, await read.WorkflowAuditOutbox.CountAsync());
    }

    [TestMethod]
    public async Task AuditStorageFailureRollsBackStateAndRetryProducesOneTransition()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Open(connection); await db.Database.EnsureCreatedAsync();
        var (_, root) = Request(db); await db.SaveChangesAsync(); var initial = await db.WorkflowAuditOutbox.CountAsync();
        var duplicate = new WorkflowAuditOutbox { EventId = (await db.WorkflowAuditOutbox.FirstAsync()).EventId, Metadata = "{}" };
        db.WorkflowAuditOutbox.Add(duplicate); root.State = RunState.Failed;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        await using (var read = Open(connection))
        {
            Assert.AreEqual(RunState.WaitingApproval, (await read.WorkflowRuns.SingleAsync()).State);
            Assert.AreEqual(initial, await read.WorkflowAuditOutbox.CountAsync());
        }
        db.Entry(duplicate).State = EntityState.Detached;
        await db.SaveChangesAsync();
        var history = (await db.WorkflowAuditOutbox.ToListAsync()).Select(r => r.Read()).Where(e => e.Kind == WorkflowAuditKind.WorkflowStateChanged).ToList();
        Assert.HasCount(1, history); Assert.AreEqual(RunState.WaitingApproval, history[0].PreviousState); Assert.AreEqual(RunState.Failed, history[0].State);
    }

    [TestMethod]
    public async Task SaveWithoutAcceptingThenAcceptingChangesDoesNotDuplicateFacts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Open(connection); await db.Database.EnsureCreatedAsync();
        var (_, root) = Request(db); await db.SaveChangesAsync(false); db.ChangeTracker.AcceptAllChanges();
        Assert.AreEqual(2, await db.WorkflowAuditOutbox.CountAsync());
        root.State = RunState.Running; db.SaveChanges(false); db.ChangeTracker.AcceptAllChanges();
        await db.SaveChangesAsync(); Assert.AreEqual(3, await db.WorkflowAuditOutbox.CountAsync());
    }
}
