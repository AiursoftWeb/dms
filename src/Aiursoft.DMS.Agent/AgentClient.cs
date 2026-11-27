using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Agent;

public sealed class AgentClient(Uri controller, string key, TaskJournal journal, MachineWorkflowRegistry? workflows = null, string? installedState = null)
{
    private readonly object _startGate = new();
    private Guid? _maintenance;
    private readonly ConcurrentDictionary<Guid, MachineWorkflowContext> _running = new();
    private readonly MachineWorkflowRegistry _workflows = workflows ?? new MachineWorkflowRegistry();
    private CancellationToken _stopping;
    private readonly HashSet<Task> _executions = [];
    private DateTime? _lastScheduleMinute;
    private long _workflowStarts;

    public async Task RunAsync(CancellationToken stopping)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _stopping = lifetime.Token;
        var schedules = ScheduleLoopAsync(lifetime.Token);
        try { await ConnectAsync(lifetime.Token); }
        finally
        {
            await lifetime.CancelAsync();
            try { await schedules; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            Task[] executions;
            lock (_startGate) executions = _executions.ToArray();
            await Task.WhenAll(executions);
        }
    }

    private async Task ScheduleLoopAsync(CancellationToken token)
    {
        var startup = true;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try { TickSchedules(DateTime.UtcNow, startup); }
            catch (Exception exception) { Console.Error.WriteLine($"Workflow scheduling failed: {exception.GetType().Name}. Durable tasks remain available for investigation."); }
            startup = false;
        } while (await timer.WaitForNextTickAsync(token));
    }

    public void TickSchedules(DateTime now, bool startup = false)
    {
        if (now.Kind != DateTimeKind.Utc) throw new ArgumentException("Schedule time must be UTC.", nameof(now));
        var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        lock (_startGate)
        {
            if (_maintenance != null || _stopping.IsCancellationRequested) return;
            foreach (var accepted in journal.Snapshot(activeOnly: true, pendingLimit: 0).Where(t => t.Schedule != null && t.State == RunState.Accepted))
                if (journal.Start(accepted.Id) is { } recovered) Launch(recovered);
            if (!startup && _lastScheduleMinute == minute) return;
            var missedMinute = _lastScheduleMinute is { } previous && minute > previous.AddMinutes(1);
            _lastScheduleMinute = minute;
            foreach (var schedule in _workflows.Schedules)
            {
                var onStartup = startup && schedule.RunOnStartup;
                var occurrence = minute;
                if (schedule.Id == ScheduledAgentUpgradeWorkflow.DefinitionName && (startup || missedMinute))
                {
                    // Catch up only the latest cron slot after downtime or maintenance. The journal
                    // deduplicates its original UTC time across restarts.
                    occurrence = schedule.Cron.GetOccurrences(minute.AddDays(-1), minute, TimeZoneInfo.Utc, toInclusive: true).Last();
                }
                else if (!onStartup && !schedule.Matches(minute)) continue;
                var task = journal.AcceptScheduled(schedule.Definition.Name, schedule.Definition.Version,
                    new ScheduledInvocation(schedule.Id, schedule.Expression, occurrence,
                        onStartup ? WorkflowTrigger.AgentStartup : WorkflowTrigger.Cron), schedule.ReconcileAfterInterruption);
                if (task != null && journal.Start(task.Id) is { } started) Launch(started);
            }
        }
    }

    // Called under _startGate. Keep live executions until graceful shutdown has drained them.
    private void Launch(JournalTask task)
    {
        _workflowStarts++;
        _executions.RemoveWhere(t => t.IsCompleted);
        _executions.Add(ExecuteAsync(task));
    }

    private async Task ConnectAsync(CancellationToken stopping)
    {
        var delay = 1;
        while (!stopping.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
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
            foreach (var task in journal.Snapshot(activeOnly: true, pendingLimit: 64))
            {
                if (task.Schedule != null)
                    await WebSocketJson.SendAsync(socket, new AgentMessage { Kind = "scheduled", TaskId = task.Id,
                        Definition = task.Definition, Version = task.Version, Schedule = task.Schedule }, token);
                if (task.State != RunState.Accepted || maintenance == null || maintenance == task.Id)
                    await WebSocketJson.SendAsync(socket, new AgentMessage { Kind = "status", TaskId = task.Id, State = task.State }, token);
                foreach (var item in task.Pending)
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
                        if (task != null) Launch(task);
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
            try
            {
                while (_running.Keys.Any(id => id != taskId) || journal.BlocksAgentUpgrade(taskId))
                    await Task.Delay(250, drain.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new InvalidOperationException("The Agent could not start upgrading. A machine workflow is still running or a previous Agent upgrade, reboot, or poweroff needs verification.");
            }
            return await new AgentUpgrade(installedState!, journal).PrepareAsync(taskId, context, input, token);
        }
        catch
        {
            lock (_startGate) { if (_maintenance == taskId) _maintenance = null; }
            throw;
        }
    }

    private async Task<string> ScheduledUpgradeAsync(Guid taskId, MachineWorkflowContext context, CancellationToken token)
    {
        var input = await AgentUpgradeManifest.GetAsync(controller, key, context, token);
        var release = input.Releases.Single(item => item.Architecture == AgentUpgrade.Architecture);
        if (Version.Parse(release.Version) <= Version.Parse(AgentUpgrade.Version))
        {
            await context.TraceAsync($"No newer Agent release is available. Installed version={AgentUpgrade.Version}, Controller version={release.Version}.");
            return JsonSerializer.Serialize(new OperationResult("No newer Agent release is available."));
        }

        await context.TraceAsync("Waiting for all other workflows to exit and for 10 seconds of continuous idle time.");
        if (!await TryReserveUpgradeWindowAsync(taskId, token))
        {
            await context.TraceAsync("Scheduled Agent upgrade was skipped because another Agent maintenance operation took precedence.");
            return JsonSerializer.Serialize(new OperationResult("Another Agent maintenance operation took precedence."));
        }

        var current = await AgentUpgradeManifest.GetAsync(controller, key, context, token);
        var selected = current.Releases.Single(item => item.Architecture == AgentUpgrade.Architecture);
        if (Version.Parse(selected.Version) <= Version.Parse(AgentUpgrade.Version))
        {
            await context.TraceAsync($"No newer Agent release is available after waiting. Installed version={AgentUpgrade.Version}, Controller version={selected.Version}.");
            return JsonSerializer.Serialize(new OperationResult("No newer Agent release is available."));
        }
        if (selected != release)
            await context.TraceAsync($"The Controller published Agent {selected.Version} while waiting. Using the current release.");

        journal.SetScheduledUpgradeRelease(taskId, selected);
        await context.TraceAsync("The Agent has been idle for 10 seconds. Starting the scheduled upgrade.");
        var result = await new AgentUpgrade(installedState!, journal).PrepareAsync(taskId, context, current, token);
        return JsonSerializer.Serialize(result);
    }

    private async Task<bool> TryReserveUpgradeWindowAsync(Guid taskId, CancellationToken token)
    {
        long? idleSince = null;
        var startsAtIdle = 0L;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            lock (_startGate)
            {
                if (_maintenance != null) return false;
                if (_running.Keys.Any(id => id != taskId) || journal.BlocksAgentUpgrade(taskId))
                {
                    idleSince = null;
                }
                else if (idleSince == null || startsAtIdle != _workflowStarts)
                {
                    idleSince = Stopwatch.GetTimestamp();
                    startsAtIdle = _workflowStarts;
                }
                else if (Stopwatch.GetElapsedTime(idleSince.Value) >= TimeSpan.FromSeconds(10))
                {
                    _maintenance = taskId;
                    return true;
                }
            }
            await Task.Delay(250, token);
        }
    }

    private async Task<OperationResult> RebootAsync(Guid taskId, MachineWorkflowContext context, RebootInput input, CancellationToken token)
    {
        lock (_startGate)
        {
            if (_maintenance != null) throw new InvalidOperationException("An Agent lifecycle operation is already running.");
            _maintenance = taskId;
        }
        try
        {
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(token); drain.CancelAfter(TimeSpan.FromMinutes(1));
            await context.TraceAsync("Pausing new work and waiting for other machine tasks before reboot.");
            while (true)
            {
                var others = journal.Snapshot(activeOnly: true, pendingLimit: 0).Where(t => t.Id != taskId).ToArray();
                if (others.Any(t => t.State is RunState.NeedsVerification or RunState.CancelRequested)) throw new InvalidOperationException("Reconcile uncertain tasks before rebooting.");
                if (others.All(t => t.State != RunState.Running)) break;
                await Task.Delay(250, drain.Token);
            }
            return await new AgentReboot(installedState!, journal).PrepareAsync(taskId, context, input, token);
        }
        catch
        {
            lock (_startGate) { if (_maintenance == taskId) _maintenance = null; }
            throw;
        }
    }

    private async Task<OperationResult> PoweroffAsync(Guid taskId, MachineWorkflowContext context, PoweroffInput input, CancellationToken token)
    {
        lock (_startGate)
        {
            if (_maintenance != null) throw new InvalidOperationException("An Agent lifecycle operation is already running.");
            _maintenance = taskId;
        }
        try
        {
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(token); drain.CancelAfter(TimeSpan.FromMinutes(1));
            await context.TraceAsync("Pausing new work and waiting for other machine tasks before poweroff.");
            while (true)
            {
                var others = journal.Snapshot(activeOnly: true, pendingLimit: 0).Where(t => t.Id != taskId).ToArray();
                if (others.Any(t => t.State is RunState.NeedsVerification or RunState.CancelRequested)) throw new InvalidOperationException("Reconcile uncertain tasks before powering off.");
                if (others.All(t => t.State != RunState.Running)) break;
                await Task.Delay(250, drain.Token);
            }
            return await new AgentPoweroff(installedState!, journal).PrepareAsync(taskId, context, input, token);
        }
        catch
        {
            lock (_startGate) { if (_maintenance == taskId) _maintenance = null; }
            throw;
        }
    }

    private async Task ExecuteAsync(JournalTask task)
    {
        var counts = journal.Counts();
        var environment = new AgentEnvironment(journal.InstallationId, typeof(AgentClient).Assembly.GetName().Version!.ToString(),
            counts.Active, counts.Pending,
            installedState == null ? null : (context, input, token) => UpgradeAsync(task.Id, context, input, token),
            Artifacts: new AgentArtifactTransport(controller, key, task.Id),
            Reboot: installedState == null ? null : (context, input, token) => RebootAsync(task.Id, context, input, token),
            Poweroff: installedState == null ? null : (context, input, token) => PoweroffAsync(task.Id, context, input, token),
            AuthorizeSshLease: (id, token) => AgentSshLeaseAuthorization.GetAsync(controller, key, id, token),
            GetSecurityBaselinePolicy: token => AgentSecurityBaselinePolicy.GetAsync(controller, key, token),
            ScheduledUpgrade: installedState == null ? null : (context, token) => ScheduledUpgradeAsync(task.Id, context, token),
            AptTransactionHook: AgentAptTransaction.Hook,
            GetFrpsDeployToken: (id, token) => AgentFrpsDeployToken.GetAsync(controller, key, id, token),
            BoxSecrets: new AgentBoxSecretTransport(controller, key),
            DatabaseSecrets: new AgentDatabaseSecretTransport(controller, key));
        var context = new MachineWorkflowContext(task.Id, null, item => journal.ReportAsync(task.Id, item), environment, _workflows);
        _running[task.Id] = context;
        try
        {
            var workflow = _workflows.Create(task.Definition, task.Input,
                environment);
            await context.RunAsync(workflow, _stopping);
        }
        catch (Exception exception)
        {
            if (journal.Find(task.Id)?.State == RunState.Running)
                await journal.ReportAsync(task.Id, new WorkflowEvent { WorkflowId = task.Id, Definition = task.Definition,
                    State = RunState.Failed, Result = JsonSerializer.Serialize(new { Error = exception.Message }) });
            Console.Error.WriteLine($"Workflow {task.Id}: {exception.GetType().Name}. Inspect the workflow trace and journal.");
        }
        finally
        {
            _running.TryRemove(task.Id, out _);
            lock (_startGate) { if (_maintenance == task.Id) _maintenance = null; }
        }
    }
}
