using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public abstract class MachineWorkflow
{
    public abstract string Name { get; }
    public virtual string Input => "{}";
    public virtual string Version => WorkflowNames.Version;
    public abstract Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token);
}

public abstract class MachineWorkflow<TInput, TResult>(MachineWorkflowDefinition<TInput, TResult> definition, TInput input) : MachineWorkflow
{
    public sealed override string Name => definition.Name;
    public sealed override string Version => definition.Version;
    public sealed override string Input => definition.Serialize(input);
    public sealed override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token) =>
        JsonSerializer.Serialize(await ExecuteAsync(context, input, token));
    public abstract Task<TResult> ExecuteAsync(MachineWorkflowContext context, TInput input, CancellationToken token);
}

public sealed class MachineWorkflowContext
{
    private readonly Guid _id;
    private CancellationToken _executionToken;
    private readonly AgentEnvironment _environment;
    private readonly MachineWorkflowRegistry _workflows;
    private readonly Guid? _parentId;
    private readonly Func<WorkflowEvent, Task> _report;
    private bool _warning;
    private bool _needsVerification;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations;

    public MachineWorkflowContext(Guid id, Guid? parentId, Func<WorkflowEvent, Task> report,
        AgentEnvironment? environment = null, MachineWorkflowRegistry? workflows = null)
        : this(id, parentId, report, new ConcurrentDictionary<Guid, CancellationTokenSource>(),
            environment ?? new AgentEnvironment(Guid.Empty, "", 0, 0), workflows ?? new MachineWorkflowRegistry()) { }

    private MachineWorkflowContext(Guid id, Guid? parentId, Func<WorkflowEvent, Task> report,
        ConcurrentDictionary<Guid, CancellationTokenSource> cancellations, AgentEnvironment environment, MachineWorkflowRegistry workflows)
    {
        _id = id; _parentId = parentId; _report = report; _cancellations = cancellations; _environment = environment; _workflows = workflows;
    }

    public Guid Id => _id;
    internal AgentEnvironment Environment => _environment;
    public Guid? ParentId => _parentId;
    public bool HasWarnings => _warning;

    public async Task CancelAsync(Guid target)
    {
        if (_cancellations.TryGetValue(target, out var cancellation))
        {
            try { await cancellation.CancelAsync(); }
            catch (ObjectDisposedException) { } // Completion may race an approved cancellation.
        }
    }

    public Task TraceAsync(string message, string source = "engine") => _report(new WorkflowEvent
    {
        WorkflowId = _id, ParentId = _parentId, IsTrace = true, Message = message, Source = source
    });

    public async Task WarningAsync(string message)
    {
        _warning = true;
        await TraceAsync(message, "warning");
    }

    public async Task<string> RunAsync(MachineWorkflow workflow, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = cancellation.Token;
        _executionToken = token;
        _cancellations[_id] = cancellation;
        try
        {
            await _report(new WorkflowEvent { WorkflowId = _id, ParentId = _parentId, Definition = workflow.Name, Version = workflow.Version, Input = workflow.Input, State = RunState.Running });
            token.ThrowIfCancellationRequested();
            var result = await workflow.ExecuteAsync(this, token);
            if (_needsVerification) throw new WorkflowNeedsVerificationException(_id);
            token.ThrowIfCancellationRequested();
            await _report(new WorkflowEvent { WorkflowId = _id, ParentId = _parentId, Definition = workflow.Name, Version = workflow.Version, State = _warning ? RunState.Warning : RunState.Succeeded, Result = result });
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await _report(new WorkflowEvent { WorkflowId = _id, ParentId = _parentId, Definition = workflow.Name, Version = workflow.Version, State = RunState.Cancelled });
            throw;
        }
        catch (WorkflowNeedsVerificationException exception)
        {
            await _report(new WorkflowEvent
            {
                WorkflowId = _id, ParentId = _parentId, Definition = workflow.Name, Version = workflow.Version,
                State = RunState.NeedsVerification, Result = exception.WorkflowId == _id ? exception.Result : ""
            });
            throw;
        }
        catch (Exception exception)
        {
            await TraceAsync(exception.Message, "error");
            await _report(new WorkflowEvent { WorkflowId = _id, ParentId = _parentId, Definition = workflow.Name, Version = workflow.Version, State = RunState.Failed, Result = JsonSerializer.Serialize(new { Error = exception.Message[..Math.Min(exception.Message.Length, 2000)] }) });
            throw;
        }
        finally { _cancellations.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(_id, cancellation)); }
    }

    public async Task<string> CallAsync(MachineWorkflow workflow, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _executionToken);
        token = cancellation.Token;
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        try { return await new MachineWorkflowContext(id, _id, _report, _cancellations, _environment, _workflows).RunAsync(workflow, token); }
        catch (OperationCanceledException) { throw; }
        catch (WorkflowNeedsVerificationException) { _needsVerification = true; throw; }
        catch (Exception exception) { throw new ChildWorkflowException(id, workflow.Name, null, RunState.Failed, exception.Message); }
    }

    public async Task<TResult> CallAsync<TInput, TResult>(MachineWorkflowDefinition<TInput, TResult> definition,
        TInput input, CancellationToken token)
    {
        var workflow = _workflows.Create(definition.Name, definition.Serialize(input), _environment);
        return definition.ReadResult(await CallAsync(workflow, token));
    }
}

public sealed class PerformanceInfoWorkflow : MachineWorkflow
{
    public override string Name => WorkflowNames.Performance;
    public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
    {
        var memory = await File.ReadAllTextAsync("/proc/meminfo", token);
        var load = await File.ReadAllTextAsync("/proc/loadavg", token);
        var result = JsonSerializer.Serialize(new { Hostname = Environment.MachineName, Processors = Environment.ProcessorCount, Memory = memory, Load = load.Trim() });
        await context.TraceAsync(result);
        return result;
    }
}

public sealed class RestartServiceWorkflow(string service) : MachineWorkflow
{
    public override string Name => WorkflowNames.Restart;
    public override string Input => JsonSerializer.Serialize(new ServiceInput(service));
    public static bool ValidService(string service) => ServiceInput.ValidService(service);

    public override Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
    {
        if (!ValidService(service)) throw new ArgumentException("A concrete .service unit name is required.");
        return context.CallAsync(new ProcessWorkflow("/usr/bin/systemctl", ["restart", "--", service]), token);
    }
}

// Only compiled workflows construct processes; this class is not an external dispatch target.
public sealed class ProcessWorkflow(string executable, IReadOnlyList<string> arguments, bool captureOutput = false) : MachineWorkflow
{
    private readonly ProcessInput _input = new(executable, arguments, captureOutput);
    public override string Name => MachineWorkflows.Process.Name;
    public override string Input => MachineWorkflows.Process.Serialize(_input);
    public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
    {
        var tool = Path.GetFileName(_input.Executable);
        await context.TraceAsync($"Starting compiled process: tool={tool}.");
        var elapsed = Stopwatch.StartNew();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(_input.Executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in _input.Arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var registration = token.Register(static state => Kill((Process)state!), process);
        var output = _input.CaptureOutput ? new StringBuilder() : null;
        var errorOutput = _input.CaptureOutput ? new StringBuilder() : null;
        var stdout = Drain(process.StandardOutput, "stdout", process, context, output);
        var stderr = Drain(process.StandardError, "stderr", process, context, errorOutput);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(CancellationToken.None));
        await context.TraceAsync($"Process finished: tool={tool}; exit_code={process.ExitCode}; elapsed_ms={elapsed.ElapsedMilliseconds}; stdout_characters={await stdout}; stderr_characters={await stderr}; cancellation_requested={token.IsCancellationRequested}.");
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            await context.TraceAsync("The process did not confirm success. Already emitted stdout/stderr remains in this workflow trace; stdout may contain useful partial results and must not be treated as a complete inventory.", "error");
            throw new InvalidOperationException($"Process exited with code {process.ExitCode}.");
        }
        if (!_input.CaptureOutput) return JsonSerializer.Serialize(new { process.ExitCode });
        var result = JsonSerializer.Serialize(new ProcessResult(process.ExitCode, output!.ToString(), errorOutput!.ToString()));
        if (result.Length > 24000) throw new InvalidOperationException("Query result exceeds the serialized output limit.");
        return result;
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } // The process may have exited while cancellation was delivered.
    }

    private static async Task<long> Drain(StreamReader reader, string source, Process process, MachineWorkflowContext context, StringBuilder? captured)
    {
        var buffer = new char[4096];
        long characters = 0;
        int read;
        try
        {
            while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
            {
                await context.TraceAsync(new string(buffer, 0, read), source);
                characters += read;
                if (captured == null) continue;
                if (captured.Length + read > 8192) throw new InvalidOperationException("Query output exceeds the 8192 character stream limit.");
                captured.Append(buffer, 0, read);
            }
        }
        catch { Kill(process); throw; }
        return characters;
    }
}
