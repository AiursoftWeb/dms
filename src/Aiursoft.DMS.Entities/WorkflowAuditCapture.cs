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
                if (entry.State == EntityState.Added)
                    await CaptureAsync(null, box, WorkflowAuditKind.LockboxSubmitted, box.CreatedAt, null, null).ConfigureAwait(false);
                if ((entry.State == EntityState.Added || entry.Property(b => b.Decision).OriginalValue != box.Decision) && box.Decision != DecisionState.Pending)
                    await CaptureAsync(null, box, box.Decision switch
                    {
                        DecisionState.Approved => WorkflowAuditKind.LockboxApproved,
                        DecisionState.Denied => WorkflowAuditKind.LockboxDenied,
                        DecisionState.Withdrawn => WorkflowAuditKind.LockboxWithdrawn,
                        DecisionState.Expired => WorkflowAuditKind.LockboxExpired,
                        _ => throw new InvalidOperationException("Unknown Lockbox decision.")
                    },
                        box.DecidedAt ?? recorded, null, null).ConfigureAwait(false);
            }
            foreach (var entry in runs)
            {
                var run = entry.Entity;
                var launch = run.Origin == WorkflowOrigin.Automatic ? null :
                    await WorkflowLaunches.FindAsync([run.RootId], cancellationToken).ConfigureAwait(false);
                var created = entry.State == EntityState.Added;
                var occurred = created ? run.CreatedAt : run.State.Terminal() ? run.FinishedAt ?? recorded
                    : run.State == RunState.Running && entry.Property(r => r.StartedAt).OriginalValue != run.StartedAt
                        ? run.StartedAt ?? recorded : recorded;
                await CaptureAsync(run, null, created ? WorkflowAuditKind.WorkflowCreated : WorkflowAuditKind.WorkflowStateChanged,
                    occurred, created ? null : entry.Property(r => r.State).OriginalValue, run.State, launch).ConfigureAwait(false);
            }
            WorkflowAuditOutbox.AddRange(captured);
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);

            async Task CaptureAsync(WorkflowRun? run, LockboxRequest? box, WorkflowAuditKind kind, DateTime occurred, RunState? previous, RunState? state, WorkflowLaunch? launch = null)
            {
                Machine? machine = null;
                if (run?.MachineId != null)
                    machine = Machines.Local.FirstOrDefault(m => m.Id == run.MachineId)
                        ?? await Machines.FindAsync([run.MachineId.Value], cancellationToken).ConfigureAwait(false);
                var submitted = kind == WorkflowAuditKind.LockboxSubmitted;
                var workflow = run?.Id ?? (submitted ? null : box?.WorkflowId);
                var item = new WorkflowAuditEntry(kind, occurred, recorded, workflow, run?.RootId ?? workflow,
                    run?.ParentId, run?.LockboxId ?? box?.Id,
                    run == null ? null : run.MachineId == null ? WorkflowLocation.Cluster : WorkflowLocation.Machine,
                    run?.MachineId, machine?.InstallationId, run?.Definition ?? box!.Definition, run?.Version ?? box!.Version,
                    launch?.RequesterId ?? box?.RequesterId, submitted ? null : launch?.ApproverId ?? box?.ApproverId,
                    submitted || box?.Decision is DecisionState.Withdrawn or DecisionState.Expired ? null : launch?.Automatic ?? box?.Automatic,
                    launch?.Risk ?? box?.Risk, launch?.Definition ?? box?.Definition, launch?.Version ?? box?.Version,
                    submitted ? DecisionState.Pending : launch != null ? DecisionState.Approved : box?.Decision,
                    submitted ? null : launch?.DecidedAt ?? box?.DecidedAt, previous, state,
                    run?.Origin ?? WorkflowOrigin.Manual,
                    run?.ParentId != null ? WorkflowTrigger.Subworkflow : run?.Trigger ?? WorkflowTrigger.ManualRequest,
                    run?.ScheduleId, run?.ScheduleExpression, run?.ScheduledAt,
                    box == null ? null : JsonSerializer.Deserialize<Guid[]>(box.TargetMachines));
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
