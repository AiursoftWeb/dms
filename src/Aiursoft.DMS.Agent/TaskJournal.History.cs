using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Agent;

public sealed partial class TaskJournal
{
    internal const int HistoryBatchSize = 256;
    private const string HistoryFormat = "scheduled-history.json";
    private const string BatchPrefix = "scheduled-";
    private readonly HashSet<JournalTask> _readyToArchive = [];
    private readonly Dictionary<Guid, ArchivedCheckpoint> _archived = [];
    private readonly record struct ArchivedCheckpoint(string File, long Sequence);

    private static bool CanArchive(JournalTask task) => task.Schedule?.ScheduleId != null && task.State.Terminal() &&
        task.Sequence > 0 && task.Pending is { Count: 0 };

    private void LoadTasks()
    {
        var format = Path.Combine(_path, HistoryFormat);
        var hasFormat = File.Exists(format);
        if (hasFormat && File.ReadAllText(format) != "1") throw new InvalidDataException("Unknown Agent scheduled history format.");
        foreach (var file in Directory.EnumerateFiles(_path, "*.json"))
            if (file != format) TrackTask(ReadTask(file));

        // A crash after publishing a batch can leave any subset of the original files.
        // A later report can also create a newer loose checkpoint for an archived task.
        var duplicates = new List<JournalTask>();
        var files = Directory.GetFiles(_path, BatchPrefix + "*.json.gz");
        foreach (var file in files)
        {
            if (!hasFormat) throw new InvalidDataException("Missing Agent scheduled history format.");
            foreach (var task in ReadBatch(file))
            {
                RememberOccurrence(task.Schedule);
                if (_tasks.TryGetValue(task.Id, out var loose))
                {
                    if (loose.Definition != task.Definition || loose.Version != task.Version || loose.Input != task.Input ||
                        loose.Schedule != task.Schedule || loose.Sequence < task.Sequence)
                        throw new InvalidDataException("Loose task conflicts with its scheduled history.");
                    if (loose.Sequence == task.Sequence)
                    {
                        if (!CanArchive(loose) || loose.State != task.State || loose.ScheduledUpgradeRelease != task.ScheduledUpgradeRelease)
                            throw new InvalidDataException("Loose task conflicts with its archived acknowledgement.");
                        duplicates.Add(loose);
                    }
                }
                if (_archived.TryGetValue(task.Id, out var previous))
                {
                    if (task.Sequence == previous.Sequence) throw new InvalidDataException("Duplicate scheduled history checkpoint.");
                    if (task.Sequence < previous.Sequence) continue;
                }
                _archived[task.Id] = new ArchivedCheckpoint(file, task.Sequence);
            }
        }
        foreach (var task in duplicates)
        {
            File.Delete(TaskFile(task));
            _tasks.Remove(task.Id);
            _readyToArchive.Remove(task);
        }
        // A second batch can supersede every checkpoint in an older one after late reports.
        var obsolete = files.Except(_archived.Values.Select(entry => entry.File)).ToArray();
        foreach (var file in obsolete) File.Delete(file);
        if (duplicates.Count > 0 || obsolete.Length > 0) SyncDirectory();
    }

    private JournalTask? Lookup(Guid id) => _tasks.TryGetValue(id, out var task) ? task :
        _archived.TryGetValue(id, out var archived) ? ReadArchived(id, archived) : null;

    private static JournalTask ReadArchived(Guid id, ArchivedCheckpoint archived) => ReadBatch(archived.File).Single(task => task.Id == id);

    private IEnumerable<JournalTask> ArchivedTasks()
    {
        foreach (var file in _archived.Values.Select(entry => entry.File).Distinct())
            foreach (var task in ReadBatch(file))
                if (_archived[task.Id].File == file && !_tasks.ContainsKey(task.Id)) yield return task;
    }

    private static string BatchName(byte[] bytes) => BatchPrefix + Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".json.gz";

    private static JournalTask[] ReadBatch(string file)
    {
        var bytes = File.ReadAllBytes(file);
        if (Path.GetFileName(file) != BatchName(bytes)) throw new InvalidDataException("Agent scheduled history checksum mismatch.");
        using var buffer = new MemoryStream(bytes);
        using var gzip = new GZipStream(buffer, CompressionMode.Decompress);
        var batch = JsonSerializer.Deserialize<JournalTask[]>(gzip);
        if (batch is not { Length: > 0 and <= HistoryBatchSize } || batch.Any(task => task.Id == Guid.Empty || !CanArchive(task)) ||
            batch.Select(task => task.Id).Distinct().Count() != batch.Length)
            throw new InvalidDataException("Invalid Agent scheduled history batch.");
        return batch;
    }

    internal void CompactScheduledHistory()
    {
        lock (_gate)
        {
            // Bound the work in a startup, scheduler tick or acknowledgement, including on first upgrade.
            if (_readyToArchive.Count < HistoryBatchSize) return;
            EnsureWritable();
            var batch = _readyToArchive.Take(HistoryBatchSize).ToArray();
            foreach (var task in batch) VerifyLength(task, new FileInfo(TaskFile(task)).Length);
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) JsonSerializer.Serialize(gzip, batch);
            var bytes = buffer.ToArray();
            var file = Path.Combine(_path, BatchName(bytes));
            var format = Path.Combine(_path, HistoryFormat);
            // Old Agents reject this versioned journal instead of forgetting archived task IDs.
            if (!File.Exists(format)) ReplaceFile(format, format + ".tmp", stream => stream.Write("1"u8));
            ReplaceFile(file, Path.Combine(_path, "scheduled-history.tmp"), stream => stream.Write(bytes));
            var replaced = new HashSet<string>();
            foreach (var task in batch)
            {
                if (_archived.TryGetValue(task.Id, out var previous)) replaced.Add(previous.File);
                _archived[task.Id] = new ArchivedCheckpoint(file, task.Sequence);
            }
            try
            {
                foreach (var task in batch) File.Delete(TaskFile(task));
                foreach (var oldFile in replaced)
                    if (ReadBatch(oldFile).All(task => _archived[task.Id].File != oldFile)) File.Delete(oldFile);
                SyncDirectory();
            }
            catch { _faulted = true; throw; }
            foreach (var task in batch)
            {
                _readyToArchive.Remove(task);
                _tasks.Remove(task.Id);
            }
        }
    }
}
