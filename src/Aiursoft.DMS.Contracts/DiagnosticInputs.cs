using System.Text.RegularExpressions;

namespace Aiursoft.DMS.Contracts;

public sealed record HistoryInput(DateTimeOffset? Since = null, DateTimeOffset? Until = null,
    int IntervalSeconds = 60, int Limit = 200, string? Resource = null, string? After = null) : IOperationInput
{
    public (DateTimeOffset Start, DateTimeOffset End) Window()
    {
        var end = Until ?? DateTimeOffset.UtcNow;
        var start = Since ?? end.AddHours(-1);
        OperationValidation.Window(start, end);
        return (start.ToUniversalTime(), end.ToUniversalTime());
    }
    public void Validate()
    {
        _ = Window(); OperationValidation.Limit(Limit);
        if (IntervalSeconds is < 1 or > 86400) throw new ArgumentException("History interval must be 1 to 86400 seconds.");
        if (Resource != null) OperationValidation.Name(Resource);
        if (After != null) _ = HistoryCursor.Decode(After);
    }
}

public sealed partial record PcpHistoryInput(string ArchiveId, string[] Metrics,
    DateTimeOffset? Since = null, DateTimeOffset? Until = null, int IntervalSeconds = 60,
    int Limit = 200, string? Instance = null, string? After = null) : IOperationInput
{
    public void Validate()
    {
        new HistoryInput(Since, Until, IntervalSeconds, Limit, After: After).Validate();
        ArgumentNullException.ThrowIfNull(ArchiveId);
        if (ArchiveId.Length != 24 || !ArchiveId.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Select an archive ID returned by history-sources.");
        ArgumentNullException.ThrowIfNull(Metrics);
        if (Metrics.Length is < 1 or > 16 || Metrics.Any(m => string.IsNullOrEmpty(m) || !MetricName().IsMatch(m)))
            throw new ArgumentException("Select 1 to 16 literal metric names; expressions are not accepted.");
        if (Instance != null && (Instance.Length is < 1 or > 128 || Instance.Any(char.IsControl)))
            throw new ArgumentException("Use one bounded literal instance name.");
    }
    [GeneratedRegex(@"\A[a-zA-Z][a-zA-Z0-9_]*(?:\.[a-zA-Z][a-zA-Z0-9_]*)+\z")]
    private static partial Regex MetricName();
}

public sealed record BlockedProcessesInput(int? Pid = null, string? Container = null, int Limit = 50) : IOperationInput
{
    public void Validate()
    {
        OperationValidation.Limit(Limit);
        if (Pid is <= 0) throw new ArgumentException("PID must be positive.");
        if (Container != null) new DockerContainerInput(Container).Validate();
    }
}

public sealed record KernelEventsInput(DateTimeOffset? Since = null, DateTimeOffset? Until = null,
    int Limit = 200, string[]? Categories = null, string? Boot = null, string? BeforeCursor = null) : IOperationInput
{
    public static readonly string[] AllowedCategories = ["oom", "hung-task", "io-error", "filesystem", "reset"];
    public void Validate()
    {
        new JournalInput(Boot: Boot, Since: Since, Until: Until, Limit: Limit, Kernel: true, BeforeCursor: BeforeCursor).Validate();
        OperationValidation.Limit(Limit);
        if (Categories is { Length: 0 or > 5 } || Categories?.Any(c => !AllowedCategories.Contains(c, StringComparer.Ordinal)) == true)
            throw new ArgumentException("Select oom, hung-task, io-error, filesystem or reset.");
    }
}

public sealed record HistoryCursor(DateTimeOffset At, string Resource)
{
    public string Encode() => Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this));
    public static HistoryCursor Decode(string text)
    {
        if (text.Length > 1024) throw new ArgumentException("Invalid history continuation cursor.");
        try
        {
            var cursor = System.Text.Json.JsonSerializer.Deserialize<HistoryCursor>(Convert.FromBase64String(text));
            if (cursor == null || string.IsNullOrEmpty(cursor.Resource) || cursor.Resource.Length > 512 || cursor.Resource.Any(char.IsControl))
                throw new ArgumentException("Invalid history continuation cursor.");
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException)
        { throw new ArgumentException("Invalid history continuation cursor.", exception); }
    }
}
