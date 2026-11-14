using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Entities;

public abstract partial class TemplateDbContext
{
    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var captured = new List<WorkflowAuditOutbox>();
        try
        {
            ChangeTracker.DetectChanges();
            var boxes = ChangeTracker.Entries<LockboxRequest>().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList();
            var runs = ChangeTracker.Entries<WorkflowRun>().Where(e => e.State == EntityState.Added ||
                (e.State == EntityState.Modified && e.Property(r => r.State).OriginalValue != e.Entity.State)).ToList();
            var recorded = DateTime.UtcNow;
            foreach (var entry in boxes)
            {
                var box = entry.Entity;
                var root = WorkflowRuns.Local.FirstOrDefault(r => r.Id == box.WorkflowId)
                    ?? await WorkflowRuns.FindAsync([box.WorkflowId], cancellationToken).ConfigureAwait(false);
                if (root == null) throw new InvalidDataException("A Lockbox audit event requires its root workflow.");
                if (entry.State == EntityState.Added)
                    await CaptureAsync(root, box, WorkflowAuditKind.LockboxSubmitted, box.CreatedAt, null, null).ConfigureAwait(false);
                if ((entry.State == EntityState.Added || entry.Property(b => b.Decision).OriginalValue != box.Decision) && box.Decision != DecisionState.Pending)
                    await CaptureAsync(root, box, box.Decision == DecisionState.Approved ? WorkflowAuditKind.LockboxApproved : WorkflowAuditKind.LockboxDenied,
                        box.DecidedAt ?? recorded, null, null).ConfigureAwait(false);
            }
            foreach (var entry in runs)
            {
                var run = entry.Entity;
                var box = Lockboxes.Local.FirstOrDefault(b => b.Id == run.LockboxId)
                    ?? await Lockboxes.FindAsync([run.LockboxId], cancellationToken).ConfigureAwait(false);
                var created = entry.State == EntityState.Added;
                var occurred = created ? run.CreatedAt : run.State.Terminal() ? run.FinishedAt ?? recorded
                    : run.State == RunState.Running && entry.Property(r => r.StartedAt).OriginalValue != run.StartedAt
                        ? run.StartedAt ?? recorded : recorded;
                await CaptureAsync(run, box, created ? WorkflowAuditKind.WorkflowCreated : WorkflowAuditKind.WorkflowStateChanged,
                    occurred, created ? null : entry.Property(r => r.State).OriginalValue, run.State).ConfigureAwait(false);
            }
            WorkflowAuditOutbox.AddRange(captured);
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);

            async Task CaptureAsync(WorkflowRun run, LockboxRequest? box, WorkflowAuditKind kind, DateTime occurred, RunState? previous, RunState? state)
            {
                Machine? machine = null;
                if (run.MachineId != null)
                    machine = Machines.Local.FirstOrDefault(m => m.Id == run.MachineId)
                        ?? await Machines.FindAsync([run.MachineId.Value], cancellationToken).ConfigureAwait(false);
                var item = new WorkflowAuditEntry(kind, occurred, recorded, run.Id, run.RootId, run.ParentId, run.LockboxId,
                    run.ClusterId, run.MachineId == null ? WorkflowLocation.Cluster : WorkflowLocation.Machine,
                    run.MachineId, machine?.InstallationId, run.Definition, run.Version, box?.RequesterId, box?.ApproverId,
                    box?.Automatic, box?.Risk, box?.Definition, box?.Version, box?.Decision, box?.DecidedAt, previous, state);
                captured.Add(new WorkflowAuditOutbox { Metadata = JsonSerializer.Serialize(item) });
            }
        }
        catch
        {
            // Failed transactions must not leave staged audit rows to pollute a later retry.
            foreach (var item in captured) Entry(item).State = EntityState.Detached;
            throw;
        }
    }
}
