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
        var box = new LockboxRequest { RequesterId = "engineer", ClientRequestId = Guid.NewGuid(),
            Definition = "example.operation", Risk = RiskLevel.Medium, Decision = decision, Automatic = decision == DecisionState.Approved,
            Parameters = "never-archive-parameters", Reason = "never-archive-reason", DecisionReason = "never-archive-decision" };
        var root = new WorkflowRun { RootId = Guid.NewGuid(),
            LockboxId = box.Id, Definition = WorkflowNames.Lockbox, State = RunState.WaitingApproval };
        root.Id = root.RootId; box.WorkflowId = root.Id;
        db.Lockboxes.Add(box); db.WorkflowRuns.Add(root);
        return (box, root);
    }

    [TestMethod]
    public async Task ApprovalWithoutAnExecutionArchivesIndependentLockboxFacts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Open(connection); await db.Database.EnsureCreatedAsync();
        var box = new LockboxRequest { RequesterId = "requester", Definition = "future.non-execution-approval" };
        db.Lockboxes.Add(box); await db.SaveChangesAsync();
        box.Decision = DecisionState.Approved; box.ApproverId = "reviewer"; box.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Assert.AreEqual(0, await db.WorkflowRuns.CountAsync());
        var facts = (await db.WorkflowAuditOutbox.OrderBy(e => e.Id).ToListAsync()).Select(e => e.Read()).ToList();
        CollectionAssert.AreEqual(new[] { WorkflowAuditKind.LockboxSubmitted, WorkflowAuditKind.LockboxApproved }, facts.Select(e => e.Kind).ToArray());
        Assert.IsTrue(facts.All(e => e.WorkflowId == null && e.RootWorkflowId == null && e.State == null && e.LockboxId == box.Id));
    }

    [TestMethod]
    public async Task FailedApprovalSaveCannotLeaveAnApprovedRequestOrExecutionRoot()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Open(connection); await db.Database.EnsureCreatedAsync();
        var box = new LockboxRequest { RequesterId = "requester", Definition = WorkflowNames.ListMachines };
        db.Lockboxes.Add(box); await db.SaveChangesAsync();
        var root = new WorkflowRun { LockboxId = box.Id, Definition = box.Definition, State = RunState.Queued };
        root.RootId = root.Id; box.WorkflowId = root.Id; box.Decision = DecisionState.Approved; box.DecidedAt = DateTime.UtcNow;
        db.WorkflowRuns.Add(root);
        db.WorkflowAuditOutbox.Add(new WorkflowAuditOutbox { EventId = (await db.WorkflowAuditOutbox.SingleAsync()).EventId });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        await using var read = Open(connection);
        var persisted = await read.Lockboxes.SingleAsync();
        Assert.AreEqual(DecisionState.Pending, persisted.Decision); Assert.IsNull(persisted.WorkflowId);
        Assert.AreEqual(0, await read.WorkflowRuns.CountAsync()); Assert.AreEqual(1, await read.WorkflowAuditOutbox.CountAsync());
    }

    [TestMethod]
    [DataRow(DecisionState.Approved)]
    [DataRow(DecisionState.Denied)]
    [DataRow(DecisionState.Withdrawn)]
    public async Task StaleDecisionsRollBackTheirRootSnapshotAndAudit(DecisionState winner)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var first = Open(connection);
        await first.Database.EnsureCreatedAsync();
        var box = new LockboxRequest { RequesterId = "engineer", ClientRequestId = Guid.NewGuid() };
        first.Lockboxes.Add(box);
        await first.SaveChangesAsync();

        await using var stale = Open(connection);
        var sameBox = await stale.Lockboxes.SingleAsync();
        void Stage(SqliteContext db, LockboxRequest request, DecisionState decision)
        {
            request.Decision = decision;
            request.DecidedAt = DateTime.UtcNow;
            if (decision != DecisionState.Approved) return;
            var run = new WorkflowRun { LockboxId = request.Id, State = RunState.Queued };
            request.WorkflowId = run.RootId = run.Id;
            db.WorkflowRuns.Add(run);
            db.WorkflowLaunches.Add(WorkflowLaunch.FromApproval(request, run.Id));
        }
        Stage(first, box, winner);
        Stage(stale, sameBox, winner == DecisionState.Approved ? DecisionState.Withdrawn : DecisionState.Approved);
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        await using var read = Open(connection);
        Assert.AreEqual(winner, (await read.Lockboxes.SingleAsync()).Decision);
        var approved = winner == DecisionState.Approved;
        Assert.AreEqual(approved ? 1 : 0, await read.WorkflowRuns.CountAsync());
        Assert.AreEqual(approved ? 1 : 0, await read.WorkflowLaunches.CountAsync());
        Assert.AreEqual(approved ? 3 : 2, await read.WorkflowAuditOutbox.CountAsync());
    }

    [TestMethod]
    public async Task AutomaticApprovalAndMachineCreationCaptureOnlySafeMetadata()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = Open(connection); await db.Database.EnsureCreatedAsync();
        var (box, root) = Request(db, DecisionState.Approved); box.DecidedAt = DateTime.UtcNow; root.State = RunState.Running;
        db.WorkflowLaunches.Add(WorkflowLaunch.FromApproval(box, root.Id));
        var machine = new Machine { InstallationId = Guid.NewGuid(), KeyHash = "never-archive-key" };
        var child = new WorkflowRun { RootId = root.Id, ParentId = root.Id, LockboxId = box.Id,
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
        Assert.AreEqual(RiskLevel.Medium, local.RequestedRisk); Assert.AreEqual(new LockboxId(box.Id), local.LockboxId);
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
