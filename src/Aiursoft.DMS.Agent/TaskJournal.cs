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
    public Queue<WorkflowEvent> Pending { get; set; } = new();
    public AgentRelease? ScheduledUpgradeRelease { get; set; }
    public LockboxContext? Lockbox { get; set; }

    // Rebuilt from the checkpoint and records on open; never serialized into either.
    internal long FileLength;
    internal bool HasRecords;
    internal long ReclaimableBytes;
    internal long PendingBytes;
    internal readonly Queue<int> EventSizes = new();

    internal JournalTask Copy() => (JournalTask)MemberwiseClone();

    internal void AddEvent(WorkflowEvent item, int size)
    {
        if (item.Sequence != checked(Sequence + 1)) throw new InvalidDataException("Discontinuous Agent journal sequence.");
        Pending.Enqueue(item);
        EventSizes.Enqueue(size);
        PendingBytes += size;
        Sequence = item.Sequence;
        if (!item.IsTrace && item.WorkflowId == Id) State = item.State;
    }

    internal void Confirm(long sequence)
    {
        while (Pending.TryPeek(out var item) && item.Sequence <= sequence)
        {
            Pending.Dequeue();
            var size = EventSizes.Dequeue();
            PendingBytes -= size;
            ReclaimableBytes += size;
        }
    }
}

public sealed partial class TaskJournal : IDisposable
{
    private const int CaughtUpJournalBytes = 64 * 1024;
    private const int CheckpointThresholdBytes = 256 * 1024;
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
        try
        {
            var identityFile = Path.Combine(_path, "installation-id");
            if (File.Exists(identityFile)) InstallationId = Guid.Parse(File.ReadAllText(identityFile));
            else
            {
                InstallationId = Guid.NewGuid();
                using var stream = new FileStream(identityFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(System.Text.Encoding.UTF8.GetBytes(InstallationId.ToString()));
                stream.Flush(flushToDisk: true);
                SyncDirectory();
            }
            _tasks = Directory.EnumerateFiles(_path, "*.json").Select(ReadTask).ToDictionary(t => t.Id);
            foreach (var task in _tasks.Values.Where(t => t.State is RunState.Running or RunState.CancelRequested))
            {
                if (task.State == RunState.Running && task.Definition == ScheduledAgentUpgradeWorkflow.DefinitionName && task.Schedule != null &&
                    task.ScheduledUpgradeRelease == null)
                {
                    SaveState(task, RunState.Accepted);
                    continue;
                }
                Append(task, new WorkflowEvent { WorkflowId = task.Id, Definition = task.Definition, State = RunState.NeedsVerification });
            }
        }
        catch { _lock.Dispose(); throw; }
    }

    public IReadOnlyList<JournalTask> Snapshot(bool activeOnly = false) => Snapshot(activeOnly, int.MaxValue);

    internal IReadOnlyList<JournalTask> Snapshot(bool activeOnly, int pendingLimit)
    {
        lock (_gate)
            return _tasks.Values.Where(t => !activeOnly || !t.State.Terminal() || t.Pending.Count > 0).Select(task =>
            {
                var copy = task.Copy();
                if (copy.Pending.Count > pendingLimit) copy.Pending = new Queue<WorkflowEvent>(task.Pending.Take(pendingLimit));
                return JsonSerializer.Deserialize<JournalTask>(JsonSerializer.SerializeToUtf8Bytes(copy))!;
            }).ToList();
    }

    internal (int Active, int Pending) Counts()
    {
        lock (_gate) return (_tasks.Values.Count(t => !t.State.Terminal()), _tasks.Values.Sum(t => t.Pending.Count));
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
            var checkpoint = task.Copy();
            checkpoint.ScheduledUpgradeRelease = release;
            Save(task, checkpoint);
            task.ScheduledUpgradeRelease = release;
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
            var task = new JournalTask { Id = message.TaskId, Definition = message.Definition, Input = message.Input, Version = message.Version, Lockbox = message.Lockbox };
            Save(task); _tasks.Add(task.Id, task);
        }
    }

    public bool RejectBeforeStart(AgentMessage message, string reason)
    {
        lock (_gate)
        {
            var task = FindExistingTask(message);
            if (task != null && task.State != RunState.Accepted) return false;
            task ??= new JournalTask { Id = message.TaskId, Definition = message.Definition, Input = message.Input, Version = message.Version, Lockbox = message.Lockbox };
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
            SaveState(task, RunState.Running); // Persist before any side effect.
            return task;
        }
    }

    public Task ReportAsync(Guid taskId, WorkflowEvent item)
    {
        lock (_gate) Append(_tasks[taskId], item);
        return Task.CompletedTask;
    }

    private void Append(JournalTask task, WorkflowEvent item)
    {
        item.Sequence = checked(task.Sequence + 1);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(item);
        if (task.PendingBytes + bytes.Length > 32L * 1024 * 1024)
        {
            // Allow each distinct root outcome once, including recovery of an oversized legacy task.
            var outcome = !item.IsTrace && item.WorkflowId == task.Id && (item.State.Terminal() || item.State == RunState.NeedsVerification);
            if (!outcome || bytes.Length > WebSocketJson.MaximumMessageBytes ||
                task.Pending.Any(e => !e.IsTrace && e.WorkflowId == task.Id && e.State == item.State))
                throw new IOException("Agent task journal exceeds 32 MiB of unacknowledged events.");
        }
        var stored = JsonSerializer.Deserialize<WorkflowEvent>(bytes)!;
        if (task.FileLength == 0)
        {
            // A rejected first offer and its failure must appear in the same initial checkpoint.
            task.AddEvent(stored, bytes.Length);
            Save(task);
            return;
        }
        AppendRecord(task, bytes);
        task.AddEvent(stored, bytes.Length);
    }

    public void Acknowledge(Guid id, long sequence)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var task) || sequence > task.Sequence ||
                !task.Pending.TryPeek(out var first) || first.Sequence > sequence) return;
            // A busy, connected task can catch up after every line. Reuse its current file
            // until compaction is worthwhile, and always release finished tasks' bodies.
            if (sequence == task.Sequence && (task.State != RunState.Running || task.FileLength >= CaughtUpJournalBytes))
            {
                var checkpoint = task.Copy();
                checkpoint.Pending = new Queue<WorkflowEvent>();
                Save(task, checkpoint);
                task.Confirm(sequence);
                task.ReclaimableBytes = 0;
                return;
            }
            var ack = JsonSerializer.SerializeToUtf8Bytes(sequence);
            AppendRecord(task, ack);
            task.Confirm(sequence);
            task.ReclaimableBytes += ack.Length + 2;
            // Bound obsolete prefixes without rewriting a large suffix on every acknowledgement.
            if (task.ReclaimableBytes >= CheckpointThresholdBytes && task.ReclaimableBytes >= task.PendingBytes) Save(task);
        }
    }

    public bool CancelBeforeStart(Guid id)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(id, out var task) || task.State != RunState.Accepted) return false;
            Append(task, new WorkflowEvent { WorkflowId = id, Definition = task.Definition, State = RunState.Cancelled });
            return true;
        }
    }

    private void SaveState(JournalTask task, RunState state)
    {
        var checkpoint = task.Copy();
        checkpoint.State = state;
        Save(task, checkpoint);
        task.State = state;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (!_faulted)
                    foreach (var task in _tasks.Values.Where(t => t.HasRecords)) Save(task);
            }
            finally { _disposed = true; _lock.Dispose(); }
        }
    }
}
