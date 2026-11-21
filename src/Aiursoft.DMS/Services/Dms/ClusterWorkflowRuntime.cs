using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Workflows;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public abstract class ClusterWorkflow<TInput, TResult>
{
    public abstract Task<TResult> ExecuteAsync(WorkflowExecutionContext context, TInput input, CancellationToken token);
}

// Registration is compiled application code. Registration alone never exposes a Lockbox API entry point.
public sealed class ClusterWorkflowRegistry
{
    private readonly Dictionary<string, (WorkflowDefinition Definition, Func<WorkflowExecutionContext, string, CancellationToken, Task<string>> Execute)> _definitions = new(StringComparer.Ordinal);

    public void Register<TInput, TResult>(ClusterWorkflowDefinition<TInput, TResult> definition,
        Func<ClusterWorkflow<TInput, TResult>> factory) => _definitions.Add(definition.Name, (definition, async (context, input, token) =>
            JsonSerializer.Serialize(await factory().ExecuteAsync(context,
                definition.ReadInput(input), token))));

    public ClusterWorkflowRegistry()
    {
        Register(ClusterWorkflows.GetMachines, () => new GetMachinesWorkflow());
        Register(ClusterWorkflows.DiskUsage, () => new ClusterDiskUsageWorkflow());
        Register(ClusterWorkflows.AgentHealth, () => new ClusterAgentHealthWorkflow());
    }

    public WorkflowDefinition? FindDefinition(string name) => _definitions.TryGetValue(name, out var entry) ? entry.Definition : null;
    public IEnumerable<WorkflowDefinition> Definitions => _definitions.Values.Select(entry => entry.Definition);
    public bool Contains(string name) => _definitions.ContainsKey(name);
    public Task<string> ExecuteAsync(string name, WorkflowExecutionContext context, string input, CancellationToken token) =>
        _definitions.TryGetValue(name, out var execute) ? execute.Execute(context, input, token)
            : throw new InvalidDataException("Unsupported cluster workflow definition.");
}

public sealed class WorkflowExecutionContext(ClusterWorkflowRuntime runtime, Guid id, CancellationToken executionToken)
{
    public Guid Id => id;
    internal bool HasWarnings { get; private set; }
    internal HashSet<Guid> ObservedProbeChildren { get; } = [];
    internal int MaximumResultLength { get; private set; } = 32_768;

    internal async Task<IReadOnlyList<MachineProbe<TResult>>> ProbeMachinesAsync<TResult>(
        MachineWorkflowDefinition<EmptyInput, TResult> definition, EmptyInput input, CancellationToken token) where TResult : class
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, executionToken);
        var rows = await runtime.ProbeMachinesAsync(id, definition, input, cancellation.Token);
        // A typed overview can contain up to 1,000 rows. Machine results still use the Agent payload limit.
        MaximumResultLength = 2 * 1024 * 1024;
        ObservedProbeChildren.UnionWith(rows.Select(row => row.WorkflowId).OfType<Guid>());
        if (rows.Any(row => row.Status != MachineProbeStatus.Succeeded)) HasWarnings = true;
        return rows;
    }

    public Task<IReadOnlyList<Guid>> GetApprovedMachinesAsync(CancellationToken token) => runtime.ApprovedMachinesAsync(id, token);
    public Task<T> WithInventoryAsync<T>(Func<TemplateDbContext, Task<T>> action, CancellationToken token) => runtime.WithInventoryAsync(id, action, token);
    public Task TraceAsync(string message, CancellationToken token) => runtime.TraceAsync(id, message, token);
    public Task WarningAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        HasWarnings = true;
        return Task.CompletedTask;
    }

    public async Task<TResult> CallAsync<TInput, TResult>(ClusterWorkflowDefinition<TInput, TResult> definition,
        TInput input, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, executionToken);
        return definition.ReadResult(await runtime.CallAsync(id, definition.Name, null, definition.Serialize(input), cancellation.Token));
    }

    public async Task<TResult> CallMachineAsync<TInput, TResult>(MachineWorkflowDefinition<TInput, TResult> definition,
        Guid machine, TInput input, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, executionToken);
        return definition.ReadResult(await runtime.CallAsync(id, definition.Name, machine, definition.Serialize(input), cancellation.Token));
    }
}

// Owns only live C# continuations. The persisted instance tree is authoritative after a restart.
public sealed class ClusterWorkflowRuntime(IServiceScopeFactory scopes, ControllerGate gate,
    ClusterWorkflowRegistry workflows, MachineWorkflowRegistry machines, TraceStore traces, AgentSessions sessions)
{
    private sealed record Execution(CancellationTokenSource Cancellation, Task Task);
    private readonly ConcurrentDictionary<Guid, Execution> _running = new();
    private readonly CancellationTokenSource _stopping = new();

    public async Task RecoverAsync(TemplateDbContext db, CancellationToken token)
    {
        foreach (var run in await db.WorkflowRuns.Where(r => r.MachineId != null && r.State == RunState.Running).ToListAsync(token))
            run.State = RunState.NeedsVerification;
        foreach (var run in await db.WorkflowRuns.Where(r => r.MachineId == null && (r.State == RunState.Running || r.State == RunState.CancelRequested)).ToListAsync(token))
            if (workflows.Contains(run.Definition)) run.State = RunState.NeedsVerification;
        await db.SaveChangesAsync(token);
    }

    public bool IsRunning(Guid id) => _running.ContainsKey(id);

    // Called under the Controller gate after Running has been committed. Execution acquires its own scopes.
    public void Start(Guid id)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        var task = Task.Run(() => ExecuteAsync(id, cancellation.Token), CancellationToken.None);
        if (!_running.TryAdd(id, new Execution(cancellation, task))) throw new InvalidOperationException("Workflow already has a live execution.");
    }

    public async Task ObserveAsync()
    {
        foreach (var pair in _running.ToArray())
        {
            if (!pair.Value.Task.IsCompleted) continue;
            if (!_running.TryRemove(pair.Key, out var execution)) continue;
            try { await execution.Task; } // Persistence failures must reach the hosted scheduler's logger.
            finally { execution.Cancellation.Dispose(); }
        }
    }

    public void Cancel(Guid id)
    {
        if (_running.TryGetValue(id, out var execution)) execution.Cancellation.Cancel();
    }

    public async Task StopAsync(CancellationToken token)
    {
        await _stopping.CancelAsync();
        await Task.WhenAll(_running.Values.Select(e => e.Task)).WaitAsync(token);
        await ObserveAsync();
    }

    private Task<T> DatabaseAsync<T>(Func<TemplateDbContext, Task<T>> action) => gate.RunAsync(async () =>
    {
        using var scope = scopes.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TemplateDbContext>());
    });

    private static async Task<WorkflowRun> ParentAsync(TemplateDbContext db, Guid id, CancellationToken token)
    {
        var run = await db.WorkflowRuns.SingleAsync(r => r.Id == id, token);
        if (run.State != RunState.Running)
        {
            if (run.State == RunState.NeedsVerification) throw new WorkflowNeedsVerificationException(id);
            throw new OperationCanceledException("The parent workflow is no longer executing.", token);
        }
        return run;
    }

    public Task<IReadOnlyList<Guid>> ApprovedMachinesAsync(Guid id, CancellationToken token) => DatabaseAsync<IReadOnlyList<Guid>>(async db =>
    {
        var parent = await ParentAsync(db, id, token);
        var launch = await db.WorkflowLaunches.SingleAsync(l => l.RootId == parent.RootId, token);
        return launch.ReadTargets();
    });

    public Task<T> WithInventoryAsync<T>(Guid id, Func<TemplateDbContext, Task<T>> action, CancellationToken token) => DatabaseAsync(async db =>
    {
        await ParentAsync(db, id, token);
        var result = await action(db);
        await db.SaveChangesAsync(token);
        return result;
    });

    public Task TraceAsync(Guid id, string message, CancellationToken token) => DatabaseAsync(async db =>
    {
        var parent = await ParentAsync(db, id, token);
        if (message.Length > 32768) throw new ArgumentException("Trace exceeds the size limit.");
        traces.Enqueue(id, new WorkflowEvent { WorkflowId = id, ParentId = parent.ParentId, Sequence = ++parent.LastSequence, IsTrace = true, Message = message });
        await db.SaveChangesAsync(token);
        return true;
    });

    public async Task<string> CallAsync(Guid parentId, string definition, Guid? machine, string input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (input.Length > 24000) throw new ArgumentException("Workflow input exceeds the size limit.");
        if (machine != null) machines.Validate(definition, input);
        else if (!workflows.Contains(definition)) throw new InvalidDataException("Unsupported cluster workflow definition.");
        var id = await DatabaseAsync(async db =>
        {
            token.ThrowIfCancellationRequested();
            var parent = await ParentAsync(db, parentId, token);
            var launch = await db.WorkflowLaunches.SingleAsync(l => l.RootId == parent.RootId, token);
            if (machine != null && (!launch.ReadTargets().Contains(machine.Value)
                || !await db.Machines.AnyAsync(m => m.Id == machine && !m.Disabled, token)))
                throw new InvalidOperationException("Machine is outside the approved active target snapshot.");
            var child = QueueChild(db, parent, machine, input,
                (machine == null ? workflows.FindDefinition(definition) : machines.FindDefinition(definition))!);
            await db.SaveChangesAsync(token);
            return child.Id;
        });
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var child = await DatabaseAsync(async db =>
            {
                await ParentAsync(db, parentId, token);
                return await db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == id, token);
            });
            if (child.State == RunState.NeedsVerification)
            {
                await MarkUncertainAsync(parentId);
                throw new WorkflowNeedsVerificationException(id);
            }
            if (child.State is RunState.Succeeded or RunState.Warning) return child.Result;
            if (child.State.Terminal()) throw new ChildWorkflowException(id, child.Definition, child.MachineId, child.State, child.Result);
            await Task.Delay(100, token);
        }
    }

    private static WorkflowRun QueueChild(TemplateDbContext db, WorkflowRun parent, Guid? machine, string input, WorkflowDefinition definition)
    {
        var child = new WorkflowRun
        {
            RootId = parent.RootId, ParentId = parent.Id, LockboxId = parent.LockboxId,
            Origin = parent.Origin, Trigger = WorkflowTrigger.Subworkflow,
            MachineId = machine, Definition = definition.Name, Input = input, Version = definition.Version,
            IsMachineTask = machine != null, State = RunState.Queued
        };
        db.WorkflowRuns.Add(child);
        return child;
    }

    private bool IsOnline(Machine machine) =>
        !machine.Disabled && sessions.Get(machine.Id) is { Ready: true, IsOpen: true } session
        && session.KeyHash == machine.KeyHash;

    internal async Task<IReadOnlyList<MachineProbe<TResult>>> ProbeMachinesAsync<TResult>(Guid parentId,
        MachineWorkflowDefinition<EmptyInput, TResult> definition, EmptyInput input, CancellationToken token) where TResult : class
    {
        // A bounded observation may return while an already-started child is still reconciling.
        // Restrict it to compiled read-only probes with no side effects to verify.
        if (definition.Name != MachineWorkflows.DiskUsage.Name && definition.Name != MachineWorkflows.AgentStatus.Name)
            throw new InvalidOperationException("Only disk usage and Agent status can be run as bounded machine probes.");

        var parameters = definition.Serialize(input);
        machines.Validate(definition.Name, parameters);
        var elapsed = Stopwatch.StartNew();
        List<MachineProbe<TResult>> rows = [];
        var pending = await DatabaseAsync(async db =>
        {
            var parent = await ParentAsync(db, parentId, token);
            var launch = await db.WorkflowLaunches.SingleAsync(l => l.RootId == parent.RootId, token);
            var targetIds = launch.ReadTargets();
            var targets = await db.Machines.AsNoTracking().NotCacheable().Where(m => targetIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, token);
            var children = new Dictionary<Guid, (Guid MachineId, string Name)>();
            foreach (var id in targetIds)
            {
                if (!targets.TryGetValue(id, out var machine) || machine.Disabled)
                    rows.Add(new(id, machine?.Name ?? id.ToString(), false, MachineProbeStatus.Unavailable, null, null));
                else if (!IsOnline(machine))
                    rows.Add(new(id, machine.Name, false, MachineProbeStatus.Offline, null, null));
                else
                    children.Add(QueueChild(db, parent, id, parameters, definition).Id, (id, machine.Name));
            }
            await db.SaveChangesAsync(token);
            return children;
        });

        while (pending.Count > 0)
        {
            await DatabaseAsync(async db =>
            {
                await ParentAsync(db, parentId, token);
                var expired = elapsed.Elapsed >= TimeSpan.FromSeconds(6);
                var children = await db.WorkflowRuns.NotCacheable().Where(r => r.ParentId == parentId && pending.Keys.Contains(r.Id)).ToListAsync(token);
                var machineIds = children.Select(child => child.MachineId!.Value).ToArray();
                var targets = await db.Machines.AsNoTracking().NotCacheable().Where(m => machineIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, token);
                foreach (var child in children)
                {
                    if (!(child.State.Terminal() || child.State == RunState.NeedsVerification || expired)) continue;
                    var (id, name) = pending[child.Id];
                    var available = targets.TryGetValue(id, out var target) && !target.Disabled;
                    var online = target != null && IsOnline(target);
                    TResult? result = null;
                    var status = child.State switch
                    {
                        RunState.Succeeded => MachineProbeStatus.Succeeded,
                        RunState.Warning => MachineProbeStatus.Warning,
                        RunState.NeedsVerification => MachineProbeStatus.NeedsVerification,
                        _ when child.State.Terminal() => MachineProbeStatus.Failed,
                        _ when !available => MachineProbeStatus.Unavailable,
                        _ when !online => MachineProbeStatus.Offline,
                        _ => MachineProbeStatus.TimedOut
                    };
                    if (status is MachineProbeStatus.Succeeded or MachineProbeStatus.Warning)
                    {
                        try { result = definition.ReadResult(child.Result); }
                        catch (Exception exception) when (exception is JsonException or InvalidDataException) { status = MachineProbeStatus.Failed; }
                    }
                    if (!child.State.Terminal() && child.State != RunState.NeedsVerification)
                        CancelWorkflow.RequestCancellation(child);
                    rows.Add(new(id, name, online, status, child.Id, result));
                    pending.Remove(child.Id);
                }
                await db.SaveChangesAsync(token);
                return true;
            });
            if (pending.Count > 0) await Task.Delay(200, token);
        }
        return rows;
    }

    private Task MarkUncertainAsync(Guid id) => DatabaseAsync(async db =>
    {
        var run = await db.WorkflowRuns.SingleAsync(r => r.Id == id);
        if (!run.State.Terminal()) run.State = RunState.NeedsVerification;
        await db.SaveChangesAsync();
        return true;
    });

    private async Task ExecuteAsync(Guid id, CancellationToken token)
    {
        var state = RunState.Succeeded;
        var result = "";
        WorkflowExecutionContext? context = null;
        try
        {
            var run = await DatabaseAsync(async db => await db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == id));
            context = new WorkflowExecutionContext(this, id, token);
            if (workflows.FindDefinition(run.Definition)?.Version != run.Version) throw new InvalidOperationException("The approved workflow version is no longer available.");
            result = await workflows.ExecuteAsync(run.Definition, context, run.Input, token);
            if (result.Length > context.MaximumResultLength) throw new InvalidDataException("Workflow result exceeds the size limit.");
        }
        catch (WorkflowNeedsVerificationException exception) { state = RunState.NeedsVerification; result = JsonSerializer.Serialize(new { Error = exception.Message }); }
        catch (OperationCanceledException) { state = _stopping.IsCancellationRequested ? RunState.NeedsVerification : RunState.Cancelled; }
        catch (Exception exception) { state = RunState.Failed; result = JsonSerializer.Serialize(new { Error = exception.Message[..Math.Min(2000, exception.Message.Length)] }); }
        await DatabaseAsync(async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(r => r.Id == id);
            if (run.State.Terminal()) return false;
            var children = await db.WorkflowRuns.Where(r => r.ParentId == id).ToListAsync();
            // Only bounded read-only probes can report an unknown child as a row in a completed overview.
            if (run.State == RunState.NeedsVerification || children.Any(c => c.State == RunState.NeedsVerification
                && context?.ObservedProbeChildren.Contains(c.Id) != true)) state = RunState.NeedsVerification;
            else if (run.State == RunState.CancelRequested)
                state = children.Any(c => !c.State.Terminal()) ? RunState.CancelRequested : RunState.Cancelled;
            else if (context?.HasWarnings == true && state == RunState.Succeeded) state = RunState.Warning;
            run.State = state; run.Result = result;
            if (state.Terminal()) run.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return true;
        });
    }
}


internal enum MachineProbeStatus { Succeeded, Warning, Offline, TimedOut, Failed, NeedsVerification, Unavailable }
internal sealed record MachineProbe<TResult>(Guid MachineId, string Name, bool Online, MachineProbeStatus Status,
    Guid? WorkflowId, TResult? Result);
