using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class ClusterWorkflowContext(TemplateDbContext db, WorkflowRun run, LockboxRequest lockbox)
{
    public TemplateDbContext Inventory => db;
    public WorkflowRun Run => run;
    public LockboxRequest Lockbox => lockbox;
    public OperationRequest Parameters => JsonSerializer.Deserialize<OperationRequest>(lockbox.Parameters)!;

    public void CallMachine(string definition, Guid machine) => db.WorkflowRuns.Add(new WorkflowRun
    {
        RootId = run.RootId, ParentId = run.Id, LockboxId = run.LockboxId, ClusterId = run.ClusterId,
        MachineId = machine, Definition = definition, IsMachineTask = true, State = RunState.Queued
    });
}

public abstract class ClusterWorkflow
{
    public abstract Task ExecuteAsync(ClusterWorkflowContext context);
}

public sealed class LockboxWorkflow : ClusterWorkflow
{
    public override Task ExecuteAsync(ClusterWorkflowContext context)
    {
        if (context.Lockbox.Decision != DecisionState.Approved || context.Lockbox.OperationId != null)
            throw new InvalidOperationException("Lockbox must be approved exactly once before creating its operation.");
        var child = new WorkflowRun
        {
            RootId = context.Run.Id, ParentId = context.Run.Id, LockboxId = context.Lockbox.Id,
            ClusterId = context.Lockbox.ClusterId, State = RunState.Queued,
            Definition = WorkflowNames.IsMachineWorkflow(context.Lockbox.Definition)
                ? WorkflowNames.Batch : context.Lockbox.Definition
        };
        context.Inventory.WorkflowRuns.Add(child);
        context.Lockbox.OperationId = child.Id;
        context.Run.State = RunState.Running;
        context.Run.StartedAt = DateTime.UtcNow;
        return Task.CompletedTask;
    }
}

public sealed class MachineBatchWorkflow : ClusterWorkflow
{
    public override async Task ExecuteAsync(ClusterWorkflowContext context)
    {
        var targets = JsonSerializer.Deserialize<List<Guid>>(context.Lockbox.TargetMachines)!;
        if (context.Lockbox.Version != WorkflowNames.Version) throw new InvalidOperationException("The approved workflow version is no longer available.");
        foreach (var id in targets)
        {
            if (!await context.Inventory.Machines.AnyAsync(m => m.Id == id && m.ClusterId == context.Run.ClusterId && !m.Disabled))
                throw new InvalidOperationException("An approved machine is no longer available in this cluster.");
        }
        foreach (var id in targets) context.CallMachine(context.Lockbox.Definition, id);
        context.Run.State = RunState.Running;
    }
}

public sealed class GetMachinesWorkflow : ClusterWorkflow
{
    public override async Task ExecuteAsync(ClusterWorkflowContext context)
    {
        context.Run.Result = JsonSerializer.Serialize(await context.Inventory.Machines.AsNoTracking()
            .Where(m => m.ClusterId == context.Run.ClusterId).Select(m => new { m.Id, m.Name, m.AgentVersion, m.LastSeen, m.Disabled }).ToListAsync());
        context.Run.State = RunState.Succeeded;
    }
}

public sealed class AddMachineWorkflow : ClusterWorkflow
{
    public override Task ExecuteAsync(ClusterWorkflowContext context)
    {
        var machine = new Machine { ClusterId = context.Run.ClusterId, Name = context.Parameters.MachineName,
            KeyHash = DmsKeyAuthenticationHandler.Hash(DmsKeyAuthenticationHandler.CreateKey(true)) };
        context.Inventory.Machines.Add(machine);
        context.Run.Result = JsonSerializer.Serialize(new { machine.Id, machine.Name, machine.ClusterId });
        context.Run.State = RunState.Succeeded;
        return Task.CompletedTask;
    }
}

public sealed class CancelWorkflow : ClusterWorkflow
{
    public override async Task ExecuteAsync(ClusterWorkflowContext context)
    {
        var target = await context.Inventory.WorkflowRuns.SingleAsync(r => r.Id == context.Parameters.TargetWorkflowId);
        var tree = await context.Inventory.WorkflowRuns.Where(r => r.RootId == target.RootId).ToListAsync();
        var selected = new HashSet<Guid> { target.Id };
        while (true)
        {
            var count = selected.Count;
            foreach (var child in tree.Where(r => r.ParentId != null && selected.Contains(r.ParentId.Value))) selected.Add(child.Id);
            if (count == selected.Count) break;
        }
        foreach (var run in tree.Where(r => selected.Contains(r.Id) && !r.State.Terminal()))
        {
            if (run.State is RunState.Queued or RunState.WaitingApproval)
            {
                run.State = RunState.Cancelled; run.FinishedAt = DateTime.UtcNow;
                if (run.Definition == WorkflowNames.Lockbox)
                {
                    var lockbox = await context.Inventory.Lockboxes.SingleAsync(l => l.Id == run.LockboxId);
                    lockbox.Decision = DecisionState.Denied; lockbox.DecidedAt = DateTime.UtcNow;
                    lockbox.DecisionReason = $"Cancelled by approved workflow {context.Run.Id}.";
                }
            }
            else run.State = RunState.CancelRequested;
        }
        context.Run.State = tree.Where(r => selected.Contains(r.Id)).All(r => r.State.Terminal()) ? RunState.Succeeded : RunState.Running;
        context.Run.Result = JsonSerializer.Serialize(new { TargetWorkflowId = target.Id, CancellationRequested = true, SideEffectsRolledBack = false });
    }
}
