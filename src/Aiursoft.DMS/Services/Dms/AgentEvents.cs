using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class AgentEvents(TemplateDbContext db, ControllerGate gate, TraceStore traces)
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
        var task = await db.WorkflowRuns.SingleOrDefaultAsync(r => r.Id == message.TaskId && r.MachineId == machineId && r.IsMachineTask);
        if (task == null) throw new DmsException(403, "Task does not belong to this Agent.");
        if (message.Kind == "status")
        {
            if (task.State == RunState.CancelRequested) return new AgentMessage { Kind = "cancel", TaskId = task.Id };
            if (message.State == RunState.Accepted && !task.State.Terminal())
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
            if (parent == null || parent.MachineId != machineId || parent.RootId != task.RootId || (item.Definition != WorkflowNames.Process && !WorkflowNames.IsMachineWorkflow(item.Definition)) || !await BelongsToTaskAsync(parent, task.Id))
                throw new DmsException(403, "Invalid machine subworkflow parent or definition.");
            run = new WorkflowRun { Id = item.WorkflowId, ParentId = parent.Id, RootId = task.RootId, LockboxId = task.LockboxId, ClusterId = task.ClusterId, MachineId = machineId, Definition = item.Definition };
            db.WorkflowRuns.Add(run);
        }
        if (run.RootId != task.RootId || run.MachineId != machineId || !await BelongsToTaskAsync(run, task.Id))
            throw new DmsException(403, "Event is outside this task's subtree.");
        if (item.Message.Length > 32768 || item.Result.Length > 32768) throw new DmsException(400, "Event exceeds the size limit.");
        if (item.IsTrace) traces.Enqueue(task.Id, item);
        else
        {
            if (item.State is not (RunState.Running or RunState.Succeeded or RunState.Failed or RunState.Cancelled or RunState.Warning or RunState.NeedsVerification))
                throw new DmsException(400, "Invalid execution state.");
            if (!run.State.Terminal())
            {
                // A cancellation request remains visible until the process reports termination.
                if (run.State != RunState.CancelRequested || item.State != RunState.Running) run.State = item.State;
                run.StartedAt ??= item.Time;
                if (item.State.Terminal()) run.FinishedAt = item.Time;
                run.Result = item.Result;
            }
            if (run.Id == task.Id && item.State == RunState.NeedsVerification)
                foreach (var child in await db.WorkflowRuns.Where(r => r.ParentId == task.Id && r.State == RunState.Running).ToListAsync()) child.State = RunState.NeedsVerification;
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
            task.State = RunState.NeedsVerification;
        await db.SaveChangesAsync(); return true;
    });
}
