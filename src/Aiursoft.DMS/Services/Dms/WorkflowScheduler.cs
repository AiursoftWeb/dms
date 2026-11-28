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
            var launch = await db.WorkflowLaunches.FindAsync([run.RootId], token);
            if (launch == null)
            {
                Fail(run, "Workflow launch snapshot is missing.");
                continue;
            }
            if (workflows.HasContinuation(run.Definition))
            {
                run.State = RunState.Running; run.StartedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(token);
                runtime.Start(run.Id);
                continue;
            }
            run.StartedAt = DateTime.UtcNow;
            try
            {
                var workflow = run.Definition == WorkflowNames.Batch ? new MachineBatchWorkflow()
                    : workflows.CreateTransactional(run.Definition, launch.Version)
                        ?? throw new InvalidOperationException("The approved cluster workflow version is no longer available.");
                var lockbox = await ResolveLockboxContextAsync(db, run, launch, token);
                await workflow.ExecuteAsync(new ClusterWorkflowContext(db, run, launch, catalog.Find(launch.Definition), lockbox));
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or InvalidDataException or ArgumentException)
            { Fail(run, exception.Message); }
            if (run.State.Terminal()) run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(token);
        }
        foreach (var upgrade in await db.WorkflowRuns.Where(r => (r.Definition == WorkflowNames.AgentUpgrade || r.Definition == OperationsWorkflows.SystemReboot.Name)
            && (r.State == RunState.Running || r.State == RunState.CancelRequested) && r.StartedAt < DateTime.UtcNow.AddMinutes(-20)).ToListAsync(token))
        {
            upgrade.State = RunState.NeedsVerification;
            upgrade.Result = JsonSerializer.Serialize(new { Error = "Agent lifecycle confirmation timed out. Do not repeat automatically." });
        }
        var pending = await db.WorkflowRuns.Where(r => (r.IsMachineTask && (r.State == RunState.Queued || r.State == RunState.Offered)) || (r.MachineId != null && r.State == RunState.CancelRequested)).ToListAsync(token);
        foreach (var task in pending)
        {
            var machine = await db.Machines.SingleAsync(m => m.Id == task.MachineId, token);
            var session = sessions.Get(machine.Id);
            if (session?.Ready != true) continue;
            if (machine.Disabled || session.KeyHash != machine.KeyHash) { session.Abort(); continue; }
            var cancel = task.State == RunState.CancelRequested;
            WorkflowLaunch? launch = null;
            if (!cancel)
            {
                if (task.Origin == WorkflowOrigin.Automatic) continue; // Agent-local schedules are never offered by the Controller.
                launch = await db.WorkflowLaunches.FindAsync([task.RootId], token);
                if (launch == null || !launch.ReadTargets().Contains(machine.Id))
                {
                    Fail(task, "Machine is outside the workflow launch snapshot.");
                    continue;
                }
            }
            if (!cancel) task.State = RunState.Offered;
            await db.SaveChangesAsync(token); // Persist the offer before sending; reconnect repeats the same ID.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var owner = task;
                while (cancel && !owner.IsMachineTask && owner.ParentId != null)
                    owner = await db.WorkflowRuns.SingleAsync(r => r.Id == owner.ParentId, token);
                var lockboxContext = cancel ? null : await ResolveLockboxContextAsync(db, task, launch, token);
                await session.SendAsync(new AgentMessage { Kind = cancel ? "cancel" : "offer", TaskId = owner.Id, TargetWorkflowId = cancel ? task.Id : null, Definition = task.Definition, Input = task.Input, Version = task.Version, Lockbox = lockboxContext }, timeout.Token);
            }
            catch (Exception exception) when (!token.IsCancellationRequested) { session.Abort(); logger.LogWarning(exception, "Agent delivery interrupted for workflow {WorkflowId}.", task.Id); }
        }
        var active = await db.WorkflowRuns.Where(r => !r.IsMachineTask && r.MachineId == null && r.State != RunState.Succeeded && r.State != RunState.Failed && r.State != RunState.Denied && r.State != RunState.Cancelled).OrderByDescending(r => r.CreatedAt).ToListAsync(token);
        foreach (var run in active)
        {
            if (workflows.HasContinuation(run.Definition))
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
                var launch = await db.WorkflowLaunches.FindAsync([run.RootId], token);
                if (launch == null)
                {
                    Fail(run, "Workflow launch snapshot is missing.");
                    continue;
                }
                var request = launch.ReadParameters();
                LegacyClusterWorkflowInput.Normalize(request, WorkflowNames.Kill);
                var targetId = ClusterWorkflows.Cancel.ReadInput(request.Input).TargetWorkflowId;
                var target = await db.WorkflowRuns.SingleAsync(r => r.Id == targetId, token);
                if (target.State.Terminal()) { run.State = RunState.Succeeded; run.FinishedAt = DateTime.UtcNow; }
            }
            var children = await db.WorkflowRuns.Where(r => r.ParentId == run.Id).ToListAsync(token);
            if (children.Count == 0) continue;
            run.State = Aggregate(children);
            if (run.Definition == WorkflowNames.Batch && (run.State is RunState.Succeeded or RunState.NeedsVerification) &&
                children is [var child] && child.Definition == OperationsWorkflows.SshDisable.Name)
                run.Result = child.Result;
            run.StartedAt ??= DateTime.UtcNow;
            if (run.State.Terminal()) run.FinishedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(token);
    }

    internal static async Task<LockboxContext?> ResolveLockboxContextAsync(
        TemplateDbContext db, WorkflowRun run, WorkflowLaunch? launch, CancellationToken token)
    {
        if (run.LockboxId == null) return null;
        launch ??= await db.WorkflowLaunches.FindAsync([run.RootId], token);
        if (launch == null) return null;
        var requester = string.IsNullOrEmpty(launch.RequesterId) ? null : await db.Users.FindAsync([launch.RequesterId], token);
        var requesterName = requester == null ? null : AccessService.PersonName(requester.DisplayName, requester.UserName);
        var reason = string.IsNullOrWhiteSpace(launch.Parameters) ? null : launch.ReadParameters().Reason;
        return new LockboxContext(
            run.LockboxId.Value,
            launch.RequesterId,
            requesterName,
            reason,
            launch.Risk,
            DateTime.SpecifyKind(launch.DecidedAt ?? run.CreatedAt, DateTimeKind.Utc));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await runtime.StopAsync(cancellationToken);
    }

    private static void Fail(WorkflowRun run, string reason)
    {
        run.State = RunState.Failed;
        run.Result = JsonSerializer.Serialize(new { Error = reason });
        run.FinishedAt = DateTime.UtcNow;
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
