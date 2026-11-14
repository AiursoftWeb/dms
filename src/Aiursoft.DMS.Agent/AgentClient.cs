using System.Collections.Concurrent;
using System.Net.WebSockets;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Agent;

public sealed class AgentClient(Uri controller, string key, TaskJournal journal)
{
    private readonly ConcurrentDictionary<Guid, MachineWorkflowContext> _running = new();

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
            foreach (var task in journal.Snapshot().Where(t => !t.State.Terminal() || t.Pending.Count > 0))
            {
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
                    if (message.Version != WorkflowNames.Version || !WorkflowNames.IsMachineWorkflow(message.Definition))
                        throw new InvalidDataException("Unsupported workflow version or definition.");
                    journal.Accept(message);
                    break;
                case "start":
                    var task = journal.Start(message.TaskId);
                    if (task != null) _ = ExecuteAsync(task);
                    break;
                case "cancel":
                    if (_running.TryGetValue(message.TaskId, out var running)) await running.CancelAsync(message.TargetWorkflowId ?? message.TaskId);
                    else if (message.TargetWorkflowId == null || message.TargetWorkflowId == message.TaskId) journal.CancelBeforeStart(message.TaskId);
                    break;
                case "ack": journal.Acknowledge(message.TaskId, message.Sequence); break;
            }
        }
    }

    private async Task ExecuteAsync(JournalTask task)
    {
        var context = new MachineWorkflowContext(task.Id, null, item => journal.ReportAsync(task.Id, item));
        _running[task.Id] = context;
        try
        {
            MachineWorkflow workflow = task.Definition switch
            {
                WorkflowNames.Performance => new PerformanceInfoWorkflow(),
                WorkflowNames.Restart => new RestartServiceWorkflow(task.Service),
                WorkflowNames.ServiceStatus => new ServiceStatusWorkflow(task.Service),
                WorkflowNames.ListBlockDevices => new ListBlockDevicesWorkflow(),
                WorkflowNames.DiskFree => new DiskFreeWorkflow(),
                WorkflowNames.AgentStatus => new AgentStatusWorkflow(journal.InstallationId,
                    typeof(AgentClient).Assembly.GetName().Version!.ToString(),
                    journal.Snapshot().Count(t => !t.State.Terminal()), journal.Snapshot().Sum(t => t.Pending.Count)),
                _ => throw new InvalidDataException("Unsupported workflow.")
            };
            await context.RunAsync(workflow, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Workflow {task.Id}: {exception.GetType().Name}. Inspect the workflow trace and journal.");
        }
        finally { _running.TryRemove(task.Id, out _); }
    }
}
