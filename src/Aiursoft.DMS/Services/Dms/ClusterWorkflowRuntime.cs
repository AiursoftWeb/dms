using System.Collections.Concurrent;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Workflows;
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

    public ClusterWorkflowRegistry() => Register(ClusterWorkflows.GetMachines, () => new GetMachinesWorkflow());

    public WorkflowDefinition? FindDefinition(string name) => _definitions.TryGetValue(name, out var entry) ? entry.Definition : null;
    public bool Contains(string name) => _definitions.ContainsKey(name);
    public Task<string> ExecuteAsync(string name, WorkflowExecutionContext context, string input, CancellationToken token) =>
        _definitions.TryGetValue(name, out var execute) ? execute.Execute(context, input, token)
            : throw new InvalidDataException("Unsupported cluster workflow definition.");
}

public sealed class WorkflowExecutionContext(ClusterWorkflowRuntime runtime, Guid id, Guid clusterId, CancellationToken executionToken)
{
    public Guid Id => id;
    public Guid ClusterId => clusterId;
    internal bool HasWarnings { get; private set; }
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
    ClusterWorkflowRegistry workflows, MachineWorkflowRegistry machines, TraceStore traces)
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
        var box = await db.Lockboxes.SingleAsync(l => l.Id == run.LockboxId, token);
        if (box.Decision != DecisionState.Approved || box.ClusterId != run.ClusterId)
            throw new InvalidOperationException("The root Lockbox does not authorize this execution.");
        return run;
    }

    public Task<IReadOnlyList<Guid>> ApprovedMachinesAsync(Guid id, CancellationToken token) => DatabaseAsync<IReadOnlyList<Guid>>(async db =>
    {
        var parent = await ParentAsync(db, id, token);
        var box = await db.Lockboxes.SingleAsync(l => l.Id == parent.LockboxId, token);
        return JsonSerializer.Deserialize<List<Guid>>(box.TargetMachines)!.AsReadOnly();
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
            var box = await db.Lockboxes.SingleAsync(l => l.Id == parent.LockboxId, token);
            if (machine != null && (!JsonSerializer.Deserialize<List<Guid>>(box.TargetMachines)!.Contains(machine.Value)
                || !await db.Machines.AnyAsync(m => m.Id == machine && m.ClusterId == parent.ClusterId && !m.Disabled, token)))
                throw new InvalidOperationException("Machine is outside the approved active target snapshot.");
            var child = new WorkflowRun
            {
                RootId = parent.RootId, ParentId = parent.Id, LockboxId = parent.LockboxId, ClusterId = parent.ClusterId,
                MachineId = machine, Definition = definition, Input = input, Version = (machine == null ? workflows.FindDefinition(definition) : machines.FindDefinition(definition))!.Version,
                IsMachineTask = machine != null, State = RunState.Queued
            };
            db.WorkflowRuns.Add(child);
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
            context = new WorkflowExecutionContext(this, id, run.ClusterId, token);
            if (workflows.FindDefinition(run.Definition)?.Version != run.Version) throw new InvalidOperationException("The approved workflow version is no longer available.");
            result = await workflows.ExecuteAsync(run.Definition, context, run.Input, token);
            if (result.Length > 32768) throw new InvalidDataException("Workflow result exceeds the size limit.");
        }
        catch (WorkflowNeedsVerificationException exception) { state = RunState.NeedsVerification; result = JsonSerializer.Serialize(new { Error = exception.Message }); }
        catch (OperationCanceledException) { state = _stopping.IsCancellationRequested ? RunState.NeedsVerification : RunState.Cancelled; }
        catch (Exception exception) { state = RunState.Failed; result = JsonSerializer.Serialize(new { Error = exception.Message[..Math.Min(2000, exception.Message.Length)] }); }
        await DatabaseAsync(async db =>
        {
            var run = await db.WorkflowRuns.SingleAsync(r => r.Id == id);
            if (run.State.Terminal()) return false;
            var children = await db.WorkflowRuns.Where(r => r.ParentId == id).ToListAsync();
            // Unknown outcomes remain sticky even if business code catches their exception.
            if (run.State == RunState.NeedsVerification || children.Any(c => c.State == RunState.NeedsVerification)) state = RunState.NeedsVerification;
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
