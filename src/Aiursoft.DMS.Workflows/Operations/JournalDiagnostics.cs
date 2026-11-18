using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public static class JournalDiagnostics
{
    private static readonly Dictionary<string, string> Patterns = new(StringComparer.Ordinal)
    {
        ["oom"] = "oom-kill|out of memory|killed process|oom_reaper",
        ["hung-task"] = "blocked for more than|hung task|hung_task",
        ["io-error"] = "I/O error|blk_update_request|Buffer I/O|critical medium error",
        ["filesystem"] = "EXT4-fs error|BTRFS.*(error|corrupt)|XFS.*(error|corrupt)|filesystem.*error",
        ["reset"] = "reset.*(controller|device|link)|hard resetting link|nvme.*timeout|ata[0-9].*failed"
    };

    public static Task<OperationResult> KernelEventsAsync(MachineWorkflowContext context,
        KernelEventsInput input,
        AgentEnvironment agent,
        CancellationToken token)
    {
        input.Validate();
        var categories = input.Categories ?? KernelEventsInput.AllowedCategories;
        return ReadAsync(context,
            new JournalInput(Boot: input.Boot,
                Since: input.Since,
                Until: input.Until,
                Limit: input.Limit,
                Kernel: true,
                BeforeCursor: input.BeforeCursor),
            agent,
            token,
            string.Join('|', categories.Select(c => "(" + Patterns[c] + ")")),
            categories);
    }

    public static async Task<OperationResult> ReadAsync(MachineWorkflowContext context,
        JournalInput input,
        AgentEnvironment agent,
        CancellationToken token,
        string? pattern = null,
        string[]? categories = null)
    {
        input.Validate();
        var end = input.Until ?? DateTimeOffset.UtcNow;
        var start = input.Since ?? end.AddHours(-24);
        var args = SystemOperations.JournalArguments(start, end, input.Limit);
        // Newest first, with a look-ahead row; resume before the last cursor, including equal timestamps safely.
        args.RemoveAll(a => a.StartsWith("--lines=", StringComparison.Ordinal));
        args.Add("--reverse");
        args.Add("--lines=" + (input.Limit + (input.BeforeCursor == null ? 1 : 2)));
        if (input.Service != null) args.Add("--unit=" + input.Service);
        if (input.Boot != null) args.Add("--boot=" + input.Boot);
        if (input.Level != null) args.Add("--priority=" + input.Level);
        if (input.Kernel)
            args.Add("_TRANSPORT=kernel"); // Unlike -k, this does not silently restrict the query to the current boot.
        if (input.BeforeCursor != null)
        {
            // journalctl forbids --since together with --cursor.
            // Apply the lower bound to the bounded returned page instead.
            args.RemoveAll(a => a.StartsWith("--since=", StringComparison.Ordinal));
            args.Add("--cursor=" + input.BeforeCursor);
        }

        if (pattern != null)
        {
            args.Add("--grep=" + pattern);
            args.Add("--case-sensitive=no");
        }

        var host = OperationOutput.Host(agent);
        // Native matching happens before --lines. Both process time and output are independently bounded.
        var result = await host.RunAsync(new NativeCommand("journalctl", args, MaxCharacters: 2097152), token);
        if (result.ExitCode is not (0 or 1))
            throw new IOException($"journalctl failed with exit code {result.ExitCode}.");
        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Output) &&
            !string.IsNullOrWhiteSpace(result.Error))
            throw new IOException("Journal query was refused or its cursor/window is unavailable.");
        var parsed = ParseRecords(result.Output, input.Limit, input.BeforeCursor, start, end);
        var diagnostics = result.Error.Trim();
        return await OperationOutput.ReportAsync(context,
            pattern == null
                ? "Bounded journal records collected."
                : "Matching kernel events collected within the requested window.",
            new
            {
                RequestedStart = start, RequestedEnd = end, input.Boot, input.Kernel, Categories = categories,
                parsed.Records, Truncated = parsed.HasMore, parsed.HasMore, parsed.NextCursor,
                ObservedStart = parsed.Records.LastOrDefault().GetPropertyOrDefault("__REALTIME_TIMESTAMP"),
                ObservedEnd = parsed.Records.FirstOrDefault().GetPropertyOrDefault("__REALTIME_TIMESTAMP"),
                Diagnostics = diagnostics,
                Coverage =
                    "Only retained, readable journal records are searched. No matches " +
                    "does not prove absence of an incident; journal retention may be " +
                    "incomplete."
            },
            !parsed.HasMore && diagnostics.Length == 0);
    }

    public static (JsonElement[] Records, bool HasMore, string? NextCursor) ParseRecords(string output,
        int limit,
        string? beforeCursor = null,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null)
    {
        var records = new List<JsonElement>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid journal record.");
            if (start != null || end != null)
            {
                if (!json.RootElement.TryGetProperty("__REALTIME_TIMESTAMP", out var timestamp) ||
                    !long.TryParse(timestamp.GetString(), out var micros))
                    throw new InvalidDataException("Journal record has no valid realtime timestamp.");
                var at = DateTimeOffset.FromUnixTimeMilliseconds(micros / 1000);
                if (start != null && at < start || end != null && at > end) continue;
            }

            if (beforeCursor != null && json.RootElement.TryGetProperty("__CURSOR", out var current) &&
                current.GetString() == beforeCursor) continue;
            records.Add(json.RootElement.Clone());
            if (records.Count > limit) break;
        }

        var more = records.Count > limit;
        var rows = records.Take(limit).ToArray();
        return (rows, more,
            rows.LastOrDefault().ValueKind == JsonValueKind.Object &&
            rows[^1].TryGetProperty("__CURSOR", out var cursor)
                ? cursor.GetString()
                : null);
    }

    private static string? GetPropertyOrDefault(this JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var field) ? field.ToString() : null;
}
