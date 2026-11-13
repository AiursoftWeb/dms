using System.Text.Json;
using Aiursoft.ClickhouseSdk;
using Aiursoft.ClickhouseSdk.Abstractions;
using Aiursoft.DMS.Contracts;
using ClickHouse.Client.Utility;

namespace Aiursoft.DMS.Services.Dms;

public sealed class TraceEntry
{
    public string EventId { get; set; } = string.Empty;
    public string WorkflowId { get; set; } = string.Empty;
    public DateTime Time { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class TraceDatabase(IConfiguration configuration) : ClickhouseDbContext(
    configuration.GetSection("Dms:Clickhouse").Get<ClickhouseOptions>() ?? new ClickhouseOptions())
{
    public override Task SaveChangesAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        var connection = await GetConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS DmsWorkflowTrace (EventId String, WorkflowId String, Time DateTime64(3, 'UTC'), Source String, Message String) ENGINE = ReplacingMergeTree ORDER BY (WorkflowId, EventId)";
        await command.ExecuteNonQueryAsync();
    }

    public async Task InsertAsync(TraceEntry item)
    {
        var connection = await GetConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO DmsWorkflowTrace (EventId, WorkflowId, Time, Source, Message) VALUES ({event:String}, {workflow:String}, {time:DateTime64(3)}, {source:String}, {message:String})";
        command.AddParameter("event", item.EventId); command.AddParameter("workflow", item.WorkflowId);
        command.AddParameter("time", item.Time); command.AddParameter("source", item.Source); command.AddParameter("message", item.Message);
        await command.ExecuteNonQueryAsync();
    }

    public Task<IReadOnlyList<TraceEntry>> ReadAsync(Guid id, int skip) => QueryAsync(
        "SELECT EventId, WorkflowId, Time, Source, Message FROM DmsWorkflowTrace FINAL WHERE WorkflowId = {workflow:String} ORDER BY Time, EventId LIMIT 200 OFFSET {skip:UInt32}",
        r => new TraceEntry { EventId = r.GetString(0), WorkflowId = r.GetString(1), Time = r.GetDateTime(2), Source = r.GetString(3), Message = r.GetString(4) },
        new Dictionary<string, object?> { ["workflow"] = id.ToString(), ["skip"] = skip });
}

public sealed class TraceStore(IConfiguration configuration)
{
    private readonly string _path = Path.GetFullPath(configuration["Dms:StatePath"] ?? "dms-state");
    private readonly object _gate = new();

    public void Enqueue(Guid taskId, WorkflowEvent item)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var entry = new TraceEntry { EventId = $"{taskId:N}:{item.Sequence:D20}", WorkflowId = item.WorkflowId.ToString(), Time = item.Time, Source = item.Source, Message = item.Message };
            var file = Path.Combine(_path, $"{taskId:N}-{item.Sequence:D20}.json");
            if (File.Exists(file)) return;
            if (Directory.EnumerateFiles(_path, "*.json").Sum(f => new FileInfo(f).Length) > 256L * 1024 * 1024)
                throw new IOException("Controller trace backlog is full; Agent must retain unacknowledged events.");
            using (var stream = new FileStream(file + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, entry); stream.Flush(flushToDisk: true);
            }
            File.Move(file + ".tmp", file);
        }
    }

    public async Task FlushAsync(TraceDatabase database)
    {
        if (!database.Enabled || !Directory.Exists(_path)) return;
        await database.InitializeAsync();
        foreach (var file in Directory.EnumerateFiles(_path, "*.json").Take(100))
        {
            var entry = JsonSerializer.Deserialize<TraceEntry>(await File.ReadAllTextAsync(file)) ?? throw new InvalidDataException("Invalid trace spool.");
            await database.InsertAsync(entry);
            File.Delete(file); // Repeated INSERT after a crash is collapsed by the query's FINAL.
        }
    }

    public int PendingCount => Directory.Exists(_path) ? Directory.EnumerateFiles(_path, "*.json").Count() : 0;
}
