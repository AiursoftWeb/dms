using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class ClusterWorkflowContext(TemplateDbContext db, WorkflowRun run, WorkflowLaunch launch, WorkflowDefinition? definition = null)
{
    public WorkflowDefinition? Definition => definition;
    public TemplateDbContext Inventory => db;
    public WorkflowRun Run => run;
    public WorkflowLaunch Launch => launch;
    public OperationRequest Parameters => launch.ReadParameters();

    public void CallMachine(string name, Guid machine) => db.WorkflowRuns.Add(new WorkflowRun
    {
        RootId = run.RootId, ParentId = run.Id, LockboxId = run.LockboxId,
                Origin = run.Origin, Trigger = WorkflowTrigger.Subworkflow,
        MachineId = machine, Definition = name, Input = Parameters.Input,
        Version = launch.Version, IsMachineTask = true, State = RunState.Queued
    });
}

public abstract class ClusterWorkflow
{
    public abstract Task ExecuteAsync(ClusterWorkflowContext context);
}

public sealed class MachineBatchWorkflow : ClusterWorkflow
{
    public override async Task ExecuteAsync(ClusterWorkflowContext context)
    {
        if (context.Definition?.Location != WorkflowLocation.Machine || context.Definition.Version != context.Launch.Version)
            throw new InvalidOperationException("The approved machine workflow version is no longer available.");
        var targets = context.Launch.ReadTargets();
        foreach (var id in targets)
        {
            if (!await context.Inventory.Machines.AnyAsync(m => m.Id == id && !m.Disabled))
                throw new InvalidOperationException("An approved machine is no longer available.");
        }
        foreach (var id in targets) context.CallMachine(context.Launch.Definition, id);
        context.Run.State = RunState.Running;
    }
}

public sealed class GetMachinesWorkflow : ClusterWorkflow<EmptyInput, List<MachineSummary>>
{
    public override Task<List<MachineSummary>> ExecuteAsync(WorkflowExecutionContext context, EmptyInput input, CancellationToken token) =>
        context.WithInventoryAsync(db => db.Machines.AsNoTracking()
            .Select(m => new MachineSummary(m.Id, m.Name, m.AgentVersion, m.LastSeen, m.Disabled)).ToListAsync(token), token);
}

public sealed class AddMachineWorkflow : ClusterWorkflow
{
    public override async Task ExecuteAsync(ClusterWorkflowContext context)
    {
        var enrollment = context.Launch.EnrollmentId is { } id
            ? await context.Inventory.AgentEnrollments.Include(e => e.JoinKey).SingleOrDefaultAsync(e => e.Id == id)
                ?? throw new InvalidOperationException("Enrollment no longer exists.")
            : null;
        if (enrollment != null && (enrollment.JoinKey.Revoked || enrollment.JoinKey.ExpiresAt <= DateTime.UtcNow
            || await context.Inventory.Machines.AnyAsync(m => m.InstallationId == enrollment.InstallationId)))
            throw new InvalidOperationException("Enrollment is unavailable or this installation has already joined.");
        var machine = new Machine { Name = context.Parameters.MachineName,
            KeyHash = enrollment?.AgentKeyHash ?? DmsKeyAuthenticationHandler.Hash(DmsKeyAuthenticationHandler.CreateKey(true)), InstallationId = enrollment?.InstallationId };
        context.Inventory.Machines.Add(machine);
        if (enrollment != null) enrollment.MachineId = machine.Id;
        context.Run.Result = JsonSerializer.Serialize(new { machine.Id, machine.Name });
        context.Run.State = RunState.Succeeded;
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
            // Start permission is persisted before sending it. An unstarted offer can be
            // cancelled here even if the Agent never recognized or journaled that offer.
            if (run.State is RunState.Queued or RunState.WaitingApproval
                || (run.IsMachineTask && run.StartedAt == null && run.State is RunState.Offered or RunState.CancelRequested))
            {
                run.State = RunState.Cancelled; run.FinishedAt = DateTime.UtcNow;

            }
            else run.State = RunState.CancelRequested;
        }
        context.Run.State = tree.Where(r => selected.Contains(r.Id)).All(r => r.State.Terminal()) ? RunState.Succeeded : RunState.Running;
        context.Run.Result = JsonSerializer.Serialize(new { TargetWorkflowId = target.Id, CancellationRequested = true, SideEffectsRolledBack = false });
    }
}
