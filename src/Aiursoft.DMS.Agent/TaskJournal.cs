using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Agent;

public sealed class JournalTask
{
    public ScheduledInvocation? Schedule { get; set; }
    public Guid Id { get; set; }
    public string Definition { get; set; } = string.Empty;
    public string Input { get; set; } = "{}";
    public string Version { get; set; } = WorkflowNames.Version;
    public RunState State { get; set; } = RunState.Accepted;
    public long Sequence { get; set; }
    public List<WorkflowEvent> Pending { get; set; } = [];
    public AgentRelease? ScheduledUpgradeRelease { get; set; }
}

public sealed class TaskJournal : IDisposable
{
    public Guid InstallationId { get; }
    private readonly string _path;
    private readonly object _gate = new();
    private readonly FileStream _lock;
    private readonly Dictionary<Guid, JournalTask> _tasks;

    public TaskJournal(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(_path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _lock = new FileStream(Path.Combine(_path, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var identityFile = Path.Combine(_path, "installation-id");
        if (File.Exists(identityFile)) InstallationId = Guid.Parse(File.ReadAllText(identityFile));
        else
        {
            InstallationId = Guid.NewGuid();
            using var stream = new FileStream(identityFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(InstallationId.ToString())); stream.Flush(true);
        }
        _tasks = Directory.EnumerateFiles(_path, "*.json").Select(f => JsonSerializer.Deserialize<JournalTask>(File.ReadAllText(f))
            ?? throw new InvalidDataException("Invalid task journal.")).ToDictionary(t => t.Id);
        foreach (var task in _tasks.Values.Where(t => t.State is RunState.Running or RunState.CancelRequested))
        {
            if (task.State == RunState.Running && task.Definition == ScheduledAgentUpgradeWorkflow.DefinitionName && task.Schedule != null &&
                task.ScheduledUpgradeRelease == null)
            {
                task.State = RunState.Accepted;
                Save(task);
                continue;
            }
            task.State = RunState.NeedsVerification;
            Append(task, new WorkflowEvent { WorkflowId = task.Id, Definition = task.Definition, State = RunState.NeedsVerification });
        }
    }

    public IReadOnlyList<JournalTask> Snapshot(bool activeOnly = false)
    {
        lock (_gate) return _tasks.Values.Where(t => !activeOnly || !t.State.Terminal() || t.Pending.Count > 0).Select(t => JsonSerializer.Deserialize<JournalTask>(JsonSerializer.Serialize(t))!).ToList();
    }

    internal bool BlocksAgentUpgrade(Guid except)
    {
        // Historical verification survives the Agent restart. A lifecycle helper can
        // still act outside the Agent process, so its uncertain outcome blocks maintenance.
        lock (_gate) return _tasks.Values.Any(task => task.Id != except &&
            (task.State is RunState.Running or RunState.CancelRequested ||
             task.State == RunState.NeedsVerification &&
             (task.Definition is WorkflowNames.AgentUpgrade or ScheduledAgentUpgradeWorkflow.DefinitionName ||
              task.Definition == OperationsWorkflows.SystemReboot.Name || task.Definition == OperationsWorkflows.SystemPoweroff.Name)));
    }

    internal void SetScheduledUpgradeRelease(Guid taskId, AgentRelease release)
    {
        lock (_gate)
        {
            var task = _tasks[taskId];
            if (task.Definition != ScheduledAgentUpgradeWorkflow.DefinitionName || task.Schedule == null || task.State != RunState.Running ||
                task.ScheduledUpgradeRelease != null)
                throw new InvalidOperationException("The scheduled upgrade is no longer waiting for a release.");
            task.ScheduledUpgradeRelease = release;
            Save(task);
        }
    }

    public JournalTask? AcceptScheduled(string definition, string version, ScheduledInvocation occurrence, bool reconcileAfterInterruption = false)
    {
        lock (_gate)
        {
            var previous = _tasks.Values.Where(t => t.Schedule?.ScheduleId == occurrence.ScheduleId).ToArray();
            if (previous.Any(t => !t.State.Terminal() && !(reconcileAfterInterruption && t.State == RunState.NeedsVerification))) return null;
            // A startup run consumes its minute too. Repeated ticks/restarts never replay that occurrence.
            if (previous.Any(t => t.Schedule!.ScheduledAt >= occurrence.ScheduledAt)) return null;
            var task = new JournalTask { Id = Guid.NewGuid(), Definition = definition, Version = version, Schedule = occurrence };
            Save(task); _tasks.Add(task.Id, task);
            return task;
        }
    }

    public void Accept(AgentMessage message)
    {
        lock (_gate)
        {
            if (FindExistingTask(message) != null) return;
            var task = new JournalTask { Id = message.TaskId, Definition = message.Definition, Input = message.Input, Version = message.Version };
            Save(task); _tasks.Add(task.Id, task);
        }
    }

    public bool RejectBeforeStart(AgentMessage message, string reason)
    {
        lock (_gate)
        {
            var task = FindExistingTask(message);
            if (task != null && task.State != RunState.Accepted) return false;
            task ??= new JournalTask { Id = message.TaskId, Definition = message.Definition, Input = message.Input, Version = message.Version };
            task.State = RunState.Failed;
            Append(task, new WorkflowEvent
            {
                WorkflowId = task.Id, Definition = task.Definition, Version = task.Version,
                State = RunState.Failed,
                Result = JsonSerializer.Serialize(new { ErrorCode = "UnsupportedWorkflow", Error = reason })
            });
            _tasks.TryAdd(task.Id, task);
            return true;
        }
    }

    private JournalTask? FindExistingTask(AgentMessage message)
    {
        if (!_tasks.TryGetValue(message.TaskId, out var task)) return null;
        if (task.Definition != message.Definition || task.Input != message.Input || task.Version != message.Version)
            throw new InvalidDataException("Task ID changed parameters.");
        return task;
    }

    public JournalTask? Start(Guid id)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var task) || task.State != RunState.Accepted) return null;
            task.State = RunState.Running;
            Save(task); // Persist before any side effect. A crash afterwards requires verification.
            return task;
        }
    }

    public Task ReportAsync(Guid taskId, WorkflowEvent item)
    {
        lock (_gate)
        {
            var task = _tasks[taskId];
            if (item.WorkflowId == taskId && !item.IsTrace) task.State = item.State;
            Append(task, item);
        }
        return Task.CompletedTask;
    }

    private void Append(JournalTask task, WorkflowEvent item)
    {
        // Stop execution on exhausted durable backlog; never drop trace entries silently.
        if (task.Pending.Sum(e => e.Message.Length) > 32 * 1024 * 1024) throw new IOException("Trace backlog is full.");
        item.Sequence = ++task.Sequence;
        task.Pending.Add(item);
        Save(task);
    }

    public void Acknowledge(Guid id, long sequence)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var task) || sequence > task.Sequence) return;
            if (task.Pending.RemoveAll(e => e.Sequence <= sequence) > 0) Save(task);
        }
    }

    public bool CancelBeforeStart(Guid id)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var task) || task.State != RunState.Accepted) return false;
            task.State = RunState.Cancelled;
            Append(task, new WorkflowEvent { WorkflowId = id, Definition = task.Definition, State = RunState.Cancelled });
            return true;
        }
    }

    private void Save(JournalTask task)
    {
        var file = Path.Combine(_path, $"{task.Id:N}.json");
        var temporary = file + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, task);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, file, overwrite: true);
    }

    public void Dispose() => _lock.Dispose();
}
