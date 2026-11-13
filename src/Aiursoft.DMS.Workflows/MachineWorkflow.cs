using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public abstract class MachineWorkflow
{
    public abstract string Name { get; }
    public abstract Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token);
}

public sealed class MachineWorkflowContext
{
    private readonly Guid _id;
    private readonly Guid? _parentId;
    private readonly Func<WorkflowEvent, Task> _report;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations;

    public MachineWorkflowContext(Guid id, Guid? parentId, Func<WorkflowEvent, Task> report)
        : this(id, parentId, report, new ConcurrentDictionary<Guid, CancellationTokenSource>()) { }

    private MachineWorkflowContext(Guid id, Guid? parentId, Func<WorkflowEvent, Task> report,
        ConcurrentDictionary<Guid, CancellationTokenSource> cancellations)
    {
        _id = id; _parentId = parentId; _report = report; _cancellations = cancellations;
    }

    public Guid Id => _id;

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

    public async Task<string> RunAsync(MachineWorkflow workflow, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = cancellation.Token;
        _cancellations[_id] = cancellation;
        try
        {
            await _report(new WorkflowEvent { WorkflowId = _id, ParentId = _parentId, Definition = workflow.Name, State = RunState.Running });
            token.ThrowIfCancellationRequested();
            var result = await workflow.ExecuteAsync(this, token);
            await _report(new WorkflowEvent { WorkflowId = _id, ParentId = _parentId, Definition = workflow.Name, State = RunState.Succeeded, Result = result });
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await _report(new WorkflowEvent { WorkflowId = _id, ParentId = _parentId, Definition = workflow.Name, State = RunState.Cancelled });
            throw;
        }
        catch (Exception exception)
        {
            await TraceAsync(exception.Message, "error");
            await _report(new WorkflowEvent { WorkflowId = _id, ParentId = _parentId, Definition = workflow.Name, State = RunState.Failed });
            throw;
        }
        finally { _cancellations.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(_id, cancellation)); }
    }

    public Task<string> CallAsync(MachineWorkflow workflow, CancellationToken token) =>
        new MachineWorkflowContext(Guid.NewGuid(), _id, _report, _cancellations).RunAsync(workflow, token);
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

public sealed partial class RestartServiceWorkflow(string service) : MachineWorkflow
{
    public override string Name => WorkflowNames.Restart;
    public static bool ValidService(string service) => ServiceName().IsMatch(service);
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_.@-]{0,190}\.service\z")]
    private static partial Regex ServiceName();

    public override Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
    {
        if (!ValidService(service)) throw new ArgumentException("A concrete .service unit name is required.");
        return context.CallAsync(new ProcessWorkflow("/usr/bin/systemctl", ["restart", "--", service]), token);
    }
}

// Only compiled workflows construct processes; this class is not an external dispatch target.
public sealed class ProcessWorkflow(string executable, IReadOnlyList<string> arguments) : MachineWorkflow
{
    public override string Name => WorkflowNames.Process;
    public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var registration = token.Register(static state => Kill((Process)state!), process);
        await Task.WhenAll(Drain(process.StandardOutput, "stdout", process, context), Drain(process.StandardError, "stderr", process, context), process.WaitForExitAsync(CancellationToken.None));
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException($"Process exited with code {process.ExitCode}.");
        return JsonSerializer.Serialize(new { process.ExitCode });
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } // The process may have exited while cancellation was delivered.
    }

    private static async Task Drain(StreamReader reader, string source, Process process, MachineWorkflowContext context)
    {
        var buffer = new char[4096];
        int read;
        try
        {
            while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
                await context.TraceAsync(new string(buffer, 0, read), source);
        }
        catch { Kill(process); throw; }
    }
}
