using System.Collections.Concurrent;
using System.Net.WebSockets;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Agent;

public sealed class AgentClient(Uri controller, string key, TaskJournal journal, MachineWorkflowRegistry? workflows = null, string? installedState = null)
{
    private readonly object _startGate = new();
    private Guid? _maintenance;
    private readonly ConcurrentDictionary<Guid, MachineWorkflowContext> _running = new();
    private readonly MachineWorkflowRegistry _workflows = workflows ?? new MachineWorkflowRegistry();

    public async Task RunAsync(CancellationToken stopping)
    {
        var delay = 1;
        while (!stopping.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            var endpoint = new UriBuilder(controller) { Scheme = controller.Scheme == "https" ? "wss" : "ws", Path = "/api/agent/channel" }.Uri;
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(endpoint, connect.Token);
                delay = 1;
                using var session = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                var send = SendLoop(socket, session.Token);
                var receive = ReceiveLoop(socket, session.Token);
                try { await await Task.WhenAny(send, receive); }
                finally
                {
                    await session.CancelAsync();
                    socket.Abort();
                    try { await Task.WhenAll(send, receive); }
                    catch (Exception exception)
                    {
                        if (!stopping.IsCancellationRequested)
                            Console.Error.WriteLine($"Agent channel closed: {exception.GetType().Name}.");
                    }
                }
            }
            catch (Exception exception) when (!stopping.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Controller connection interrupted: {exception.GetType().Name}. Reconnecting in {delay}s.");
            }
            if (!stopping.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(delay), stopping);
            delay = Math.Min(delay * 2, 60);
        }
    }

    private async Task SendLoop(WebSocket socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await WebSocketJson.SendAsync(socket, new AgentMessage { Kind = "hello", InstallationId = journal.InstallationId, Version = typeof(AgentClient).Assembly.GetName().Version!.ToString() }, token);
            Guid? maintenance;
            lock (_startGate) maintenance = _maintenance;
            foreach (var task in journal.Snapshot().Where(t => !t.State.Terminal() || t.Pending.Count > 0))
            {
                if (task.State != RunState.Accepted || maintenance == null || maintenance == task.Id)
                    await WebSocketJson.SendAsync(socket, new AgentMessage { Kind = "status", TaskId = task.Id, State = task.State }, token);
                foreach (var item in task.Pending.Take(64))
                    await WebSocketJson.SendAsync(socket, new AgentMessage { Kind = "event", TaskId = task.Id, Event = item }, token);
            }
            await Task.Delay(500, token);
        }
    }

    private async Task ReceiveLoop(WebSocket socket, CancellationToken token)
    {
        while (await WebSocketJson.ReceiveAsync(socket, token) is { } message)
        {
            switch (message.Kind)
            {
                case "offer":
                    if (_workflows.FindDefinition(message.Definition)?.Version != message.Version || !_workflows.Contains(message.Definition))
                    {
                        journal.RejectBeforeStart(message,
                            $"Agent {typeof(AgentClient).Assembly.GetName().Version} cannot run workflow '{message.Definition}' version '{message.Version}'. " +
                            $"Supported version: {_workflows.FindDefinition(message.Definition)?.Version ?? "not installed"}. Upgrade the Agent before submitting this workflow again.");
                        break;
                    }
                    journal.Accept(message);
                    break;
                case "start":
                    lock (_startGate)
                    {
                        if (_maintenance != null && _maintenance != message.TaskId) break;
                        var task = journal.Start(message.TaskId);
                        if (task != null) _ = ExecuteAsync(task);
                    }
                    break;
                case "cancel":
                    if (_running.TryGetValue(message.TaskId, out var running)) await running.CancelAsync(message.TargetWorkflowId ?? message.TaskId);
                    else if (message.TargetWorkflowId == null || message.TargetWorkflowId == message.TaskId) journal.CancelBeforeStart(message.TaskId);
                    break;
                case "ack": journal.Acknowledge(message.TaskId, message.Sequence); break;
            }
        }
    }

    private async Task<AgentUpgradeResult> UpgradeAsync(Guid taskId, MachineWorkflowContext context, AgentUpgradeInput input, CancellationToken token)
    {
        lock (_startGate)
        {
            if (_maintenance != null) throw new InvalidOperationException("An Agent upgrade is already running.");
            _maintenance = taskId;
        }
        try
        {
            await context.TraceAsync("Pausing new machine tasks and waiting for running tasks to finish.");
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(token);
            drain.CancelAfter(TimeSpan.FromMinutes(1));
            while (true)
            {
                var others = journal.Snapshot().Where(t => t.Id != taskId).ToList();
                if (others.Any(t => t.State is RunState.NeedsVerification or RunState.CancelRequested))
                    throw new InvalidOperationException("Reconcile uncertain machine tasks before upgrading the Agent.");
                if (others.All(t => t.State != RunState.Running)) break;
                await Task.Delay(250, drain.Token);
            }
            return await new AgentUpgrade(installedState!, journal).PrepareAsync(taskId, context, input, token);
        }
        catch
        {
            lock (_startGate) { if (_maintenance == taskId) _maintenance = null; }
            throw;
        }
    }

    private async Task ExecuteAsync(JournalTask task)
    {
        var environment = new AgentEnvironment(journal.InstallationId, typeof(AgentClient).Assembly.GetName().Version!.ToString(),
            journal.Snapshot().Count(t => !t.State.Terminal()), journal.Snapshot().Sum(t => t.Pending.Count),
            installedState == null ? null : (context, input, token) => UpgradeAsync(task.Id, context, input, token));
        var context = new MachineWorkflowContext(task.Id, null, item => journal.ReportAsync(task.Id, item), environment, _workflows);
        _running[task.Id] = context;
        try
        {
            var workflow = _workflows.Create(task.Definition, task.Input,
                environment);
            await context.RunAsync(workflow, CancellationToken.None);
        }
        catch (Exception exception)
        {
            if (journal.Snapshot().Single(t => t.Id == task.Id).State == RunState.Running)
                await journal.ReportAsync(task.Id, new WorkflowEvent { WorkflowId = task.Id, Definition = task.Definition,
                    State = RunState.Failed, Result = System.Text.Json.JsonSerializer.Serialize(new { Error = exception.Message }) });
            Console.Error.WriteLine($"Workflow {task.Id}: {exception.GetType().Name}. Inspect the workflow trace and journal.");
        }
        finally
        {
            _running.TryRemove(task.Id, out _);
            lock (_startGate) { if (_maintenance == task.Id) _maintenance = null; }
        }
    }
}
