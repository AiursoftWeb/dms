using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class WorkflowScheduler(IServiceScopeFactory scopes, ControllerGate gate, AgentSessions sessions, ClusterWorkflowRuntime runtime, ClusterWorkflowRegistry workflows, WorkflowCatalog catalog, ILogger<WorkflowScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var recovered = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runtime.ObserveAsync();
                await gate.RunAsync(async () =>
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
                    if (!recovered)
                    {
                        await runtime.RecoverAsync(db, stoppingToken);
                        recovered = true;
                    }
                    await TickAsync(db, stoppingToken);
                    return true;
                });
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested) { logger.LogError(exception, "Workflow scheduling failed; persisted work will be revisited."); }
            await Task.Delay(1000, stoppingToken);
        }
    }

    public async Task TickAsync(TemplateDbContext db, CancellationToken token)
    {
        var queued = await db.WorkflowRuns.Where(r => r.MachineId == null && r.State == RunState.Queued).ToListAsync(token);
        foreach (var run in queued)
        {
            var lockbox = await db.Lockboxes.SingleAsync(l => l.Id == run.LockboxId, token);
            if (lockbox.Decision != DecisionState.Approved) throw new InvalidOperationException("Unapproved workflow cannot execute.");
            if (workflows.Contains(run.Definition))
            {
                run.State = RunState.Running; run.StartedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(token);
                runtime.Start(run.Id);
                continue;
            }
            ClusterWorkflow workflow = run.Definition switch
            {
                WorkflowNames.Batch => new MachineBatchWorkflow(),
                WorkflowNames.AddMachine => new AddMachineWorkflow(),
                WorkflowNames.Kill => new CancelWorkflow(),
                _ => throw new InvalidOperationException("Unknown cluster workflow.")
            };
            run.StartedAt = DateTime.UtcNow;
            try { await workflow.ExecuteAsync(new ClusterWorkflowContext(db, run, lockbox, catalog.Find(lockbox.Definition))); }
            catch (InvalidOperationException exception) { run.State = RunState.Failed; run.Result = JsonSerializer.Serialize(new { Error = exception.Message }); }
            if (run.State.Terminal()) run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(token);
        }
        var pending = await db.WorkflowRuns.Where(r => (r.IsMachineTask && (r.State == RunState.Queued || r.State == RunState.Offered)) || (r.MachineId != null && r.State == RunState.CancelRequested)).ToListAsync(token);
        foreach (var task in pending)
        {
            var machine = await db.Machines.SingleAsync(m => m.Id == task.MachineId, token);
            var session = sessions.Get(machine.Id);
            if (session?.Ready != true) continue;
            if (machine.Disabled || session.KeyHash != machine.KeyHash) { session.Abort(); continue; }
            var lockbox = await db.Lockboxes.SingleAsync(l => l.Id == task.LockboxId, token);
            if (lockbox.Decision != DecisionState.Approved || machine.ClusterId != lockbox.ClusterId) continue;
            var cancel = task.State == RunState.CancelRequested;
            if (!cancel) task.State = RunState.Offered;
            await db.SaveChangesAsync(token); // Persist the offer before sending; reconnect repeats the same ID.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var owner = task;
                while (cancel && !owner.IsMachineTask && owner.ParentId != null)
                    owner = await db.WorkflowRuns.SingleAsync(r => r.Id == owner.ParentId, token);
                await session.SendAsync(new AgentMessage { Kind = cancel ? "cancel" : "offer", TaskId = owner.Id, TargetWorkflowId = cancel ? task.Id : null, Definition = task.Definition, Input = task.Input, Version = task.Version }, timeout.Token);
            }
            catch (Exception exception) when (!token.IsCancellationRequested) { session.Abort(); logger.LogWarning(exception, "Agent delivery interrupted for workflow {WorkflowId}.", task.Id); }
        }
        var active = await db.WorkflowRuns.Where(r => !r.IsMachineTask && r.MachineId == null && r.State != RunState.Succeeded && r.State != RunState.Failed && r.State != RunState.Denied && r.State != RunState.Cancelled).OrderByDescending(r => r.CreatedAt).ToListAsync(token);
        foreach (var run in active)
        {
            if (workflows.Contains(run.Definition))
            {
                if (run.State == RunState.CancelRequested)
                {
                    runtime.Cancel(run.Id);
                    var cancellingChildren = await db.WorkflowRuns.Where(r => r.ParentId == run.Id).ToListAsync(token);
                    if (!runtime.IsRunning(run.Id) && cancellingChildren.All(c => c.State.Terminal()))
                    { run.State = RunState.Cancelled; run.FinishedAt = DateTime.UtcNow; }
                }
                if (run.State == RunState.Running && !runtime.IsRunning(run.Id)) run.State = RunState.NeedsVerification;
                continue;
            }
            if (run.Definition == WorkflowNames.Kill && run.State == RunState.Running)
            {
                var item = await db.Lockboxes.SingleAsync(l => l.Id == run.LockboxId, token);
                var request = JsonSerializer.Deserialize<OperationRequest>(item.Parameters)!;
                var target = await db.WorkflowRuns.SingleAsync(r => r.Id == request.TargetWorkflowId, token);
                if (target.State.Terminal()) { run.State = RunState.Succeeded; run.FinishedAt = DateTime.UtcNow; }
            }
            var children = await db.WorkflowRuns.Where(r => r.ParentId == run.Id).ToListAsync(token);
            if (children.Count == 0) continue;
            run.State = Aggregate(children);
            run.StartedAt ??= DateTime.UtcNow;
            if (run.State.Terminal()) run.FinishedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(token);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await runtime.StopAsync(cancellationToken);
    }

    private static RunState Aggregate(List<WorkflowRun> children)
    {
        if (children.Any(c => c.State == RunState.CancelRequested)) return RunState.CancelRequested;
        if (children.Any(c => c.State == RunState.NeedsVerification)) return RunState.NeedsVerification;
        if (children.Any(c => !c.State.Terminal())) return RunState.Running;
        if (children.Any(c => c.State == RunState.Failed)) return RunState.Failed;
        if (children.Any(c => c.State == RunState.Cancelled)) return RunState.Cancelled;
        if (children.Any(c => c.State == RunState.Warning)) return RunState.Warning;
        return RunState.Succeeded;
    }
}
