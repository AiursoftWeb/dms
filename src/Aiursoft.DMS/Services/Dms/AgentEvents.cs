using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class AgentEvents(TemplateDbContext db, ControllerGate gate, TraceStore traces, MachineWorkflowRegistry workflows)
{
    public Task<AgentMessage?> HandleAsync(Guid machineId, AgentSession session, AgentMessage message) => gate.RunAsync<AgentMessage?>(async () =>
    {
        var machine = await db.Machines.SingleAsync(m => m.Id == machineId);
        if (machine.Disabled || machine.KeyHash != session.KeyHash) throw new DmsException(403, "Agent Key was revoked.");
        if (message.Kind == "hello")
        {
            if (message.Version.Length > 100) throw new DmsException(400, "Invalid Agent version.");
            if (message.InstallationId == Guid.Empty || (machine.InstallationId != null && machine.InstallationId != message.InstallationId))
                throw new DmsException(409, "Agent state directory belongs to a different installation. Reconcile pending tasks before replacement.");
            machine.InstallationId ??= message.InstallationId;
            machine.LastSeen = DateTime.UtcNow; machine.AgentVersion = message.Version;
            await db.SaveChangesAsync(); session.Ready = true; return null;
        }
        if (!session.Ready) throw new DmsException(400, "Agent handshake is required.");
        if (message.Kind == "scheduled")
        {
            var invocation = message.Schedule ?? throw new DmsException(400, "Missing schedule metadata.");
            var schedule = workflows.Schedules.SingleOrDefault(s => s.Id == invocation.ScheduleId);
            if (message.TaskId == Guid.Empty || schedule == null || schedule.Definition.Name != message.Definition
                || schedule.Definition.Version != message.Version || schedule.Expression != invocation.Expression
                || invocation.ScheduledAt.Kind != DateTimeKind.Utc || invocation.ScheduledAt < DateTime.UnixEpoch
                || invocation.ScheduledAt > DateTime.UtcNow.AddMinutes(1)
                || invocation.ScheduledAt.Ticks % TimeSpan.TicksPerMinute != 0
                || (invocation.Trigger == WorkflowTrigger.Cron ? !schedule.Matches(invocation.ScheduledAt)
                    : invocation.Trigger != WorkflowTrigger.AgentStartup || !schedule.RunOnStartup))
                throw new DmsException(403, "This scheduled invocation is not declared by the installed workflow catalog.");
            var existing = await db.WorkflowRuns.SingleOrDefaultAsync(r => r.Id == message.TaskId);
            if (existing != null)
            {
                if (existing.MachineId != machineId || existing.RootId != existing.Id || existing.Origin != WorkflowOrigin.Automatic
                    || existing.Definition != message.Definition || existing.Version != message.Version
                    || existing.ScheduleId != invocation.ScheduleId || existing.ScheduledAt != invocation.ScheduledAt
                    || existing.Trigger != invocation.Trigger || existing.ScheduleExpression != invocation.Expression)
                    throw new DmsException(409, "Scheduled workflow identity changed.");
                return null;
            }
            if (await db.WorkflowRuns.AnyAsync(r => r.MachineId == machineId && r.ParentId == null
                && r.ScheduleId == invocation.ScheduleId && r.ScheduledAt == invocation.ScheduledAt))
                throw new DmsException(409, "This scheduled occurrence already has a workflow ID.");
            db.WorkflowRuns.Add(new WorkflowRun
            {
                Id = message.TaskId, RootId = message.TaskId, MachineId = machineId, IsMachineTask = true,
                Definition = message.Definition, Version = message.Version, State = RunState.Accepted,
                CreatedAt = invocation.ScheduledAt, Origin = WorkflowOrigin.Automatic, Trigger = invocation.Trigger,
                ScheduleId = invocation.ScheduleId, ScheduleExpression = invocation.Expression, ScheduledAt = invocation.ScheduledAt
            });
            await db.SaveChangesAsync();
            return null;
        }
        var task = await db.WorkflowRuns.SingleOrDefaultAsync(r => r.Id == message.TaskId && r.MachineId == machineId && r.IsMachineTask);
        if (task == null) throw new DmsException(403, "Task does not belong to this Agent.");
        if (message.Kind == "status")
        {
            if (task.State is RunState.CancelRequested or RunState.Cancelled) return new AgentMessage { Kind = "cancel", TaskId = task.Id };
            if (message.State == RunState.Accepted && task.Origin == WorkflowOrigin.Manual && !task.State.Terminal())
            {
                task.State = RunState.Running; task.StartedAt ??= DateTime.UtcNow;
                await db.SaveChangesAsync();
                return new AgentMessage { Kind = "start", TaskId = task.Id };
            }
            if (message.State == RunState.NeedsVerification && !task.State.Terminal()) task.State = RunState.NeedsVerification;
            if (message.State == RunState.Running && task.State == RunState.NeedsVerification) task.State = RunState.Running;
            await db.SaveChangesAsync(); return null;
        }
        if (message.Kind != "event" || message.Event == null) throw new DmsException(400, "Invalid Agent event.");
        var item = message.Event;
        if (item.Sequence <= task.LastSequence) return new AgentMessage { Kind = "ack", TaskId = task.Id, Sequence = task.LastSequence };
        if (item.Sequence != task.LastSequence + 1) throw new DmsException(409, "Agent events must be delivered in order.");
        var run = await db.WorkflowRuns.SingleOrDefaultAsync(r => r.Id == item.WorkflowId);
        if (run == null)
        {
            var parent = await db.WorkflowRuns.SingleOrDefaultAsync(r => r.Id == item.ParentId);
            if (parent == null || parent.MachineId != machineId || parent.RootId != task.RootId || (item.Definition != WorkflowNames.Process && workflows.FindDefinition(item.Definition)?.Version != item.Version) || !await BelongsToTaskAsync(parent, task.Id))
                throw new DmsException(403, "Invalid machine subworkflow parent or definition.");
            run = new WorkflowRun { Id = item.WorkflowId, ParentId = parent.Id, RootId = task.RootId, LockboxId = task.LockboxId, MachineId = machineId, Definition = item.Definition, Input = item.Input, Version = item.Version,
                CreatedAt = item.Time, Origin = task.Origin, Trigger = WorkflowTrigger.Subworkflow,
                ScheduleId = task.ScheduleId, ScheduleExpression = task.ScheduleExpression, ScheduledAt = task.ScheduledAt };
            db.WorkflowRuns.Add(run);
        }
        if (run.RootId != task.RootId || run.MachineId != machineId || !await BelongsToTaskAsync(run, task.Id))
            throw new DmsException(403, "Event is outside this task's subtree.");
        if (item.Message.Length > 32768 || item.Result.Length > 32768 || item.Input.Length > 24000) throw new DmsException(400, "Event exceeds the size limit.");
        if (item.IsTrace) traces.Enqueue(task.Id, item, run);
        else
        {
            if (item.State is not (RunState.Running or RunState.Succeeded or RunState.Failed or RunState.Cancelled or RunState.Warning or RunState.NeedsVerification))
                throw new DmsException(400, "Invalid execution state.");
            if (!run.State.Terminal())
            {
                // A cancellation request remains visible until the process reports termination.
                if (run.State != RunState.CancelRequested || item.State != RunState.Running) run.State = item.State;
                // A rejected offer or a cancellation before start never received execution permission.
                if (!run.IsMachineTask || item.State == RunState.Running) run.StartedAt ??= item.Time;
                if (item.State.Terminal()) run.FinishedAt = item.Time;
                run.Result = item.Result;
            }
            if (run.Id == task.Id && item.State == RunState.NeedsVerification)
                foreach (var child in await db.WorkflowRuns.Where(r => r.RootId == task.RootId && r.MachineId == machineId && r.State == RunState.Running).ToListAsync())
                    if (await BelongsToTaskAsync(child, task.Id)) child.State = RunState.NeedsVerification;
        }
        task.LastSequence = item.Sequence;
        await db.SaveChangesAsync();
        return new AgentMessage { Kind = "ack", TaskId = task.Id, Sequence = item.Sequence };
    });

    private async Task<bool> BelongsToTaskAsync(WorkflowRun run, Guid taskId)
    {
        var seen = new HashSet<Guid>();
        while (seen.Add(run.Id))
        {
            if (run.Id == taskId) return true;
            if (run.IsMachineTask || run.ParentId == null) return false;
            var parent = await db.WorkflowRuns.SingleOrDefaultAsync(r => r.Id == run.ParentId);
            if (parent == null) return false;
            run = parent;
        }
        return false;
    }

    public Task<bool> DisconnectedAsync(Guid machine) => gate.RunAsync(async () =>
    {
        foreach (var task in await db.WorkflowRuns.Where(r => r.MachineId == machine && r.State == RunState.Running).ToListAsync())
            if (task.Definition != WorkflowNames.AgentUpgrade) task.State = RunState.NeedsVerification;
        await db.SaveChangesAsync(); return true;
    });
}
