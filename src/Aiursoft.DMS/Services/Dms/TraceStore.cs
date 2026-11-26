using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aiursoft.ClickhouseSdk;
using Aiursoft.ClickhouseSdk.Abstractions;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using ClickHouse.Client.Copy;

namespace Aiursoft.DMS.Services.Dms;

public sealed class TraceEntry
{
    public WorkflowOrigin Origin { get; set; }
    public WorkflowTrigger Trigger { get; set; }
    public string? ScheduleId { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string WorkflowId { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowDefinition { get; set; }
    public DateTime Time { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed partial class TraceDatabase : ClickhouseDbContext
{
    private readonly int _retentionDays;

    public TraceDatabase(IConfiguration configuration) : this(
        configuration.GetSection("Dms:Clickhouse").Get<ClickhouseOptions>() ?? new ClickhouseOptions()) { }

    private TraceDatabase(ClickhouseOptions options) : base(options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.RetentionDays);
        _retentionDays = options.RetentionDays;
    }

    public override Task SaveChangesAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        var connection = await GetConnection();
        await using var command = connection.CreateCommand();
        var ttl = $"toDateTime(Time) + toIntervalDay({_retentionDays.ToString(CultureInfo.InvariantCulture)})";
        command.CommandText = """
            SELECT extract(engine_full, ' TTL (.*?)(?: SETTINGS |$)'),
                (SELECT count() FROM system.columns WHERE database = currentDatabase() AND table = 'DmsWorkflowTrace'
                    AND name IN ('Origin', 'Trigger', 'ScheduleId'))
            FROM system.tables WHERE database = currentDatabase() AND name = 'DmsWorkflowTrace'
            """;
        string? currentTtl = null;
        ulong metadataColumns = 0;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                currentTtl = reader.GetString(0);
                metadataColumns = reader.GetFieldValue<ulong>(1);
            }
        }
        if (currentTtl == null)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS DmsWorkflowTrace (EventId String, WorkflowId String, Time DateTime64(3, 'UTC'),
                    Source String, Message String, Origin String DEFAULT 'Manual', Trigger String DEFAULT 'ManualRequest', ScheduleId Nullable(String))
                ENGINE = ReplacingMergeTree ORDER BY (WorkflowId, EventId)
                """ + (_retentionDays > 0 ? $" TTL {ttl}" : "");
            await command.ExecuteNonQueryAsync();
            return;
        }
        if (metadataColumns < 3)
        {
            command.CommandText = "ALTER TABLE DmsWorkflowTrace ADD COLUMN IF NOT EXISTS Origin String DEFAULT 'Manual', ADD COLUMN IF NOT EXISTS Trigger String DEFAULT 'ManualRequest', ADD COLUMN IF NOT EXISTS ScheduleId Nullable(String)";
            await command.ExecuteNonQueryAsync();
        }
        if (_retentionDays > 0 && currentTtl != ttl)
        {
            command.CommandText = $"ALTER TABLE DmsWorkflowTrace MODIFY TTL {ttl}";
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task InsertAsync(IReadOnlyList<TraceEntry> entries, CancellationToken token)
    {
        var connection = await GetConnection();
        using var copy = new ClickHouseBulkCopy(connection)
        {
            DestinationTableName = "DmsWorkflowTrace",
            ColumnNames = ["EventId", "WorkflowId", "Time", "Source", "Message", "Origin", "Trigger", "ScheduleId"],
            BatchSize = entries.Count
        };
        await copy.InitAsync();
        await copy.WriteToServerAsync(entries.Select(item => new[] {
            item.EventId, item.WorkflowId, item.Time, item.Source, item.Message, item.Origin.ToString(),
            item.Trigger.ToString(), (object?)item.ScheduleId ?? DBNull.Value
        }), token);
    }

    public const int PageSize = 200;

    public Task<IReadOnlyList<TraceEntry>> ReadAsync(string[] workflowIds, int skip, int limit = PageSize) => QueryAsync(
        """
        SELECT EventId, WorkflowId, Time, Source, Message, Origin, Trigger, ScheduleId
        FROM DmsWorkflowTrace FINAL
        WHERE WorkflowId IN {workflows:Array(String)}
        ORDER BY Time, EventId, WorkflowId
        LIMIT {limit:UInt32} OFFSET {skip:UInt32}
        """,
        r => new TraceEntry { EventId = r.GetString(0), WorkflowId = r.GetString(1), Time = r.GetDateTime(2), Source = r.GetString(3), Message = r.GetString(4),
            Origin = Enum.Parse<WorkflowOrigin>(r.GetString(5)), Trigger = Enum.Parse<WorkflowTrigger>(r.GetString(6)), ScheduleId = r.IsDBNull(7) ? null : r.GetString(7) },
        new Dictionary<string, object?> { ["workflows"] = workflowIds, ["limit"] = limit, ["skip"] = skip });

    public Task<IReadOnlyList<TraceEntry>> ReadDataAsync(Guid workflowId, int limit) => QueryAsync(
        """
        SELECT EventId, Message
        FROM DmsWorkflowTrace FINAL
        WHERE WorkflowId = {workflow:String} AND Source = 'data'
        ORDER BY EventId
        LIMIT {limit:UInt32}
        """,
        reader => new TraceEntry { EventId = reader.GetString(0), Message = reader.GetString(1) },
        new Dictionary<string, object?> { ["workflow"] = workflowId.ToString(), ["limit"] = limit });
}

public sealed class TraceStore(IConfiguration configuration)
{
    private const int BatchSize = 100;
    private const long MaximumPendingBytes = 256L * 1024 * 1024;
    // Each pending event has its own file, including short or empty messages.
    private const long SpoolBlockBytes = 4096;
    private readonly string _path = Path.GetFullPath(configuration["Dms:StatePath"] ?? "dms-state");
    private readonly object _gate = new();
    private long _pendingBytes;
    private int _pendingCount = -1;

    private static long SpoolBytes(long length) =>
        Math.Max(1, (length + SpoolBlockBytes - 1) / SpoolBlockBytes) * SpoolBlockBytes;

    private void LoadBacklog()
    {
        if (_pendingCount >= 0) return;
        long bytes = 0;
        var count = 0;
        var directory = new DirectoryInfo(_path);
        if (directory.Exists)
            foreach (var file in directory.EnumerateFiles("*.json"))
            {
                bytes += SpoolBytes(file.Length);
                count++;
            }
        _pendingBytes = bytes;
        _pendingCount = count;
    }

    public void Enqueue(Guid taskId, WorkflowEvent item, WorkflowRun? run = null)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var entry = new TraceEntry { EventId = $"{taskId:N}:{item.Sequence:D20}", WorkflowId = item.WorkflowId.ToString(), Time = item.Time, Source = item.Source, Message = item.Message,
                Origin = run?.Origin ?? WorkflowOrigin.Manual, Trigger = item.ParentId != null ? WorkflowTrigger.Subworkflow : run?.Trigger ?? WorkflowTrigger.ManualRequest,
                ScheduleId = run?.ScheduleId };
            var file = Path.Combine(_path, $"{taskId:N}-{item.Sequence:D20}.json");
            if (File.Exists(file)) return;
            LoadBacklog();
            var payload = JsonSerializer.SerializeToUtf8Bytes(entry);
            var bytes = SpoolBytes(payload.Length);
            if (_pendingBytes + bytes > MaximumPendingBytes)
                throw new IOException("Controller trace backlog is full; Agent must retain unacknowledged events.");
            using (var stream = new FileStream(file + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }
            File.Move(file + ".tmp", file);
            _pendingBytes += bytes;
            _pendingCount++;
        }
    }

    public async Task<bool> FlushAsync(TraceDatabase database, CancellationToken token = default)
    {
        if (!database.Enabled || PendingCount == 0) return false;
        var entries = new List<TraceEntry>();
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_path, "*.json").Take(BatchSize))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var entry = await JsonSerializer.DeserializeAsync<TraceEntry>(stream, cancellationToken: token);
                entries.Add(entry ?? throw new InvalidDataException("Invalid trace spool."));
                files.Add(file);
            }
            catch (FileNotFoundException) { } // A concurrent flush already archived this event.
            catch (Exception ex) when (entries.Count > 0 && ex is JsonException or InvalidDataException)
            {
                // Deliver the readable prefix. The invalid file stays on disk for the next pass.
                break;
            }
        }
        if (entries.Count == 0) return false;
        await database.InitializeAsync();
        await database.InsertAsync(entries, token);
        // Only release durable files after ClickHouse confirms the entire batch.
        lock (_gate)
        {
            foreach (var path in files)
            {
                var file = new FileInfo(path);
                if (!file.Exists) continue;
                var bytes = SpoolBytes(file.Length);
                file.Delete();
                _pendingBytes -= bytes;
                _pendingCount--;
            }
            return entries.Count == BatchSize && _pendingCount > 0;
        }
    }

    public async Task<List<TraceEntry>> ReadPendingDataAsync(Guid workflowId, int limit, CancellationToken token)
    {
        var entries = new List<TraceEntry>();
        if (!Directory.Exists(_path)) return entries;
        foreach (var file in Directory.EnumerateFiles(_path, $"{workflowId:N}-*.json"))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var entry = await JsonSerializer.DeserializeAsync<TraceEntry>(stream, cancellationToken: token);
                if (entry?.Source != "data" || entry.WorkflowId != workflowId.ToString()) continue;
                entries.Add(entry);
                if (entries.Count >= limit) break;
            }
            catch (FileNotFoundException) { } // A concurrent flush has archived this entry.
        }
        return entries;
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                LoadBacklog();
                return _pendingCount;
            }
        }
    }
}
