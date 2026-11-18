using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public sealed record HistorySource(string Id, string Kind, string Path);
public sealed record HistoryPoint(DateTimeOffset At, double? NativeIntervalSeconds, string Resource,
    Dictionary<string, double?> Values, Dictionary<string, string> Units);

public static partial class HistoryOperations
{
    private const int MaximumArchives = 64;
    private const int MaximumOutput = 2097152;
    private static readonly string[] SysstatRoots = ["/var/log/sysstat", "/var/log/sa"];
    private const string PcpRoot = "/var/log/pcp/pmlogger";

    // Inputs select discovered IDs, never a path or a remote PCP endpoint.
    public static IReadOnlyList<HistorySource> Discover(OperationHost host)
    {
        var found = new List<HistorySource>();
        foreach (var root in SysstatRoots)
        {
            if (!SafeDirectory(host.FilePath(root))) continue;
            foreach (var file in Directory.EnumerateFiles(host.FilePath(root)).Order(StringComparer.Ordinal).Take(128))
                if (SysstatName().IsMatch(Path.GetFileName(file)) && SafeFile(file))
                    found.Add(Source("sysstat", file));
        }
        var pcp = host.FilePath(PcpRoot);
        if (SafeDirectory(pcp))
            foreach (var directory in Directory.EnumerateDirectories(pcp).Order(StringComparer.Ordinal).Take(32).Prepend(pcp))
            {
                if (!SafeDirectory(directory)) continue;
                foreach (var meta in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).Take(512))
                {
                    var match = PcpMetaName().Match(meta);
                    if (!match.Success || !SafeFile(meta)) continue;
                    var archive = match.Groups[1].Value;
                    // Reject symlinked index/volumes as well as metadata. PCP may open any member.
                    var members = Directory.EnumerateFiles(directory, Path.GetFileName(archive) + ".*").Take(129).ToArray();
                    if (members.Length > 128 || members.Any(f => !SafeFile(f))) continue;
                    found.Add(Source("pcp", archive));
                }
            }
        return found.DistinctBy(f => f.Id).OrderBy(f => f.Kind).ThenBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }
    private static HistorySource Source(string kind, string path) => new(
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + path)))[..24].ToLowerInvariant(), kind, path);
    private static bool SafeFile(string path) => new FileInfo(path) is { LinkTarget: null, Attributes: var attributes }
        && (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;
    private static bool SafeDirectory(string path)
    {
        if (!Directory.Exists(path)) return false;
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if (directory.LinkTarget != null || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    private static async Task<NativeResult> NativeAsync(MachineWorkflowContext context, OperationHost host,
        string tool, IReadOnlyList<string> arguments, CancellationToken token, int maximum = MaximumOutput)
    {
        await context.TraceAsync($"Reading existing history with {tool}; timeout_seconds=60; output_character_limit={maximum}.");
        var result = await host.RunAsync(new NativeCommand(tool, arguments,
            Environment: new Dictionary<string, string> { ["TZ"] = "UTC", ["S_TIME_FORMAT"] = "ISO" }, MaxCharacters: maximum), token);
        await context.TraceAsync($"History reader {tool} finished; exit_code={result.ExitCode}; stdout_characters={result.Output.Length}.");
        if (result.ExitCode != 0) throw new IOException($"{tool} could not read this archive (exit {result.ExitCode}). Its format or requested metrics may not be supported.");
        return result;
    }
    private static async Task<string?> VersionAsync(MachineWorkflowContext context, OperationHost host, string tool, List<string> warnings, CancellationToken token)
    {
        try
        {
            var result = await NativeAsync(context, host, tool, ["-V"], token, 8192);
            return (result.Output + result.Error).Trim();
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        { warnings.Add(exception.Message); return null; }
    }
    public static async Task<OperationResult> HistorySourcesAsync(MachineWorkflowContext context, HistoryInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var host = OperationOutput.Host(agent); var warnings = new List<string>();
        var sources = Discover(host); var versions = new Dictionary<string, string?>(); var archives = new List<object>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            foreach (var tool in new[] { "sadf", "pmrep", "pminfo", "pmdumplog" })
                versions[tool] = await VersionAsync(context, host, tool, warnings, deadline.Token);
            foreach (var source in sources.Take(Math.Min(input.Limit, MaximumArchives)))
            {
                object? metadata = null; string[]? metrics = null;
                try
                {
                    if (source.Kind == "sysstat" && versions.GetValueOrDefault("sadf") != null)
                    {
                        var result = await NativeAsync(context, host, "sadf", ["-j", "-H", source.Path], deadline.Token, 65536);
                        using var json = JsonDocument.Parse(result.Output);
                        metadata = json.RootElement.GetProperty("sysstat").GetProperty("hosts")[0].Clone();
                    }
                    if (source.Kind == "pcp" && versions.GetValueOrDefault("pmdumplog") != null)
                    {
                        metadata = (await NativeAsync(context, host, "pmdumplog", ["-L", source.Path], deadline.Token, 65536)).Output;
                        if (versions.GetValueOrDefault("pminfo") != null)
                            metrics = (await NativeAsync(context, host, "pminfo", ["-a", source.Path], deadline.Token, 262144)).Output
                                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(m => MetricName().IsMatch(m)).Take(100).ToArray();
                    }
                }
                catch (Exception exception) when (exception is IOException or NotSupportedException)
                { warnings.Add($"Archive {source.Id}: {exception.Message}"); }
                archives.Add(new { source.Id, source.Kind, Name = Path.GetFileName(source.Path), Metadata = metadata, Metrics = metrics,
                    MetricsLimit = source.Kind == "pcp" ? 100 : (int?)null });
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { warnings.Add("History source discovery reached its 60-second budget; results are partial."); }
        var window = input.Window();
        return await OperationOutput.ReportAsync(context, "Existing history sources inspected; no collector was installed or started.",
            new { RequestedStart = window.Start, RequestedEnd = window.End, Versions = versions, Archives = archives, Warnings = warnings,
                Truncated = sources.Count > archives.Count, Coverage = "Sysstat headers identify archive dates; query each series for actual intervals. PCP labels describe its stored window." },
            warnings.Count == 0 && sources.Count == archives.Count && sources.Count > 0);
    }
    public static Task<OperationResult> CpuHistoryAsync(MachineWorkflowContext c, HistoryInput i, AgentEnvironment a, CancellationToken t) => SysstatAsync(c, i, a, "cpu", t);
    public static Task<OperationResult> LoadHistoryAsync(MachineWorkflowContext c, HistoryInput i, AgentEnvironment a, CancellationToken t) => SysstatAsync(c, i, a, "load", t);
    public static Task<OperationResult> MemoryHistoryAsync(MachineWorkflowContext c, HistoryInput i, AgentEnvironment a, CancellationToken t) => SysstatAsync(c, i, a, "memory", t);
    public static Task<OperationResult> DiskHistoryAsync(MachineWorkflowContext c, HistoryInput i, AgentEnvironment a, CancellationToken t) => SysstatAsync(c, i, a, "disk", t);
    public static Task<OperationResult> NetworkHistoryAsync(MachineWorkflowContext c, HistoryInput i, AgentEnvironment a, CancellationToken t) => SysstatAsync(c, i, a, "network", t);

    private static async Task<OperationResult> SysstatAsync(MachineWorkflowContext context, HistoryInput input, AgentEnvironment agent, string series, CancellationToken token)
    {
        input.Validate(); var host = OperationOutput.Host(agent); var window = input.Window();
        input = input with { Since = window.Start, Until = window.End };
        var warnings = new List<string>(); var points = new List<HistoryPoint>(); var restarts = new List<JsonElement>();
        var sources = Discover(host).Where(s => s.Kind == "sysstat").ToArray(); var read = new List<string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        string? version = null;
        try
        {
            version = await VersionAsync(context, host, "sadf", warnings, deadline.Token);
            if (version != null)
                foreach (var source in sources.Take(MaximumArchives))
                {
                    // Verify the internal date: saDD filenames are reused each month.
                    var header = await NativeAsync(context, host, "sadf", ["-j", "-H", source.Path], deadline.Token, 65536);
                    using var headerJson = JsonDocument.Parse(header.Output);
                    var dateText = headerJson.RootElement.GetProperty("sysstat").GetProperty("hosts")[0].GetProperty("file-date").GetString();
                    if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        throw new InvalidDataException("Sysstat archive date is not in the supported ISO format.");
                    if (date < DateOnly.FromDateTime(window.Start.UtcDateTime).AddDays(-1) || date > DateOnly.FromDateTime(window.End.UtcDateTime)) continue;
                    string[] flags = series switch
                    {
                        "cpu" => input.Resource == null || input.Resource == "all" ? ["-u", "ALL"] : ["-u", "ALL", "-P", input.Resource], "load" => ["-q"], "memory" => ["-r", "ALL", "-S", "-W"],
                        "disk" => ["-d"], _ => ["-n", "DEV,EDEV"]
                    };
                    // Restrict a same-day query natively too, then enforce absolute UTC bounds after parsing.
                    // A cross-midnight window is filtered by absolute timestamps, not wrapping time-of-day comparisons.
                    var args = new List<string> { "-j" };
                    if (window.Start.UtcDateTime.Date == window.End.UtcDateTime.Date)
                        args.AddRange(["-s", window.Start.ToString("HH:mm:ss", CultureInfo.InvariantCulture), "-e", window.End.ToString("HH:mm:ss", CultureInfo.InvariantCulture)]);
                    args.AddRange([source.Path, "--", .. flags]);
                    var result = await NativeAsync(context, host, "sadf", args, deadline.Token);
                    var parsed = ParseSysstat(result.Output, series, window.Start, window.End, input.Resource);
                    points.AddRange(parsed.Points); restarts.AddRange(parsed.Restarts); read.Add(source.Id);
                }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { warnings.Add("History query reached its 60-second budget; earlier archives are retained."); }
        catch (Exception exception) when (exception is IOException or NotSupportedException) { warnings.Add(exception.Message); }
        if (sources.Length > MaximumArchives) warnings.Add("Archive discovery exceeded the query archive limit.");
        return await ReportSeriesAsync(context, agent, series, version, input, points, restarts, read, warnings, token);
    }

    public static (List<HistoryPoint> Points, List<JsonElement> Restarts) ParseSysstat(string text, string series, DateTimeOffset start, DateTimeOffset end, string? resource = null)
    {
        using var json = JsonDocument.Parse(text);
        var points = new List<HistoryPoint>(); var restarts = new List<JsonElement>();
        foreach (var host in json.RootElement.GetProperty("sysstat").GetProperty("hosts").EnumerateArray())
        {
            if (host.TryGetProperty("restarts", out var events))
                foreach (var entry in events.EnumerateArray())
                {
                    var stamp = entry.TryGetProperty("timestamp", out var time) ? time : entry;
                    if (stamp.TryGetProperty("date", out var day) && stamp.TryGetProperty("time", out var clock) &&
                        DateTimeOffset.TryParse(day.GetString() + "T" + clock.GetString() + "+00:00", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) && when >= start && when <= end)
                        restarts.Add(entry.Clone());
                }
            if (!host.TryGetProperty("statistics", out var statistics)) continue;
            foreach (var sample in statistics.EnumerateArray())
            {
                if (!sample.TryGetProperty("timestamp", out var stamp)) continue;
                if (stamp.GetProperty("tz").GetString() != "UTC") throw new InvalidDataException("History reader requires UTC sysstat timestamps.");
                var at = DateTimeOffset.ParseExact(stamp.GetProperty("date").GetString() + "T" + stamp.GetProperty("time").GetString() + "+00:00",
                    "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
                if (at < start || at > end) continue;
                var interval = Number(stamp, "interval");
                var key = series switch { "cpu" => "cpu-load", "load" => "queue", "memory" => "memory", "disk" => "disk", _ => "network" };
                if (!sample.TryGetProperty(key, out var section)) continue;
                if (series == "network")
                {
                    foreach (var name in new[] { "net-dev", "net-edev" })
                        if (section.TryGetProperty(name, out var interfaces))
                            foreach (var item in interfaces.EnumerateArray()) Add(item, item.GetProperty("iface").GetString()!, name);
                }
                else if (section.ValueKind == JsonValueKind.Array)
                    foreach (var item in section.EnumerateArray()) Add(item, item.GetProperty(series == "cpu" ? "cpu" : "disk-device").GetString()!, series);
                else
                {
                    Add(section, "all", series);
                    if (series == "memory" && sample.TryGetProperty("swap-pages", out var swap)) Add(swap, "swap-pages", "swap-pages");
                }
                continue;
                void Add(JsonElement item, string name, string category)
                {
                    if (resource != null && resource != name || series == "cpu" && resource == null && name != "all") return;
                    var values = new Dictionary<string, double?>(); var units = new Dictionary<string, string>();
                    foreach (var field in item.EnumerateObject())
                    {
                        if (field.Value.ValueKind != JsonValueKind.Number) continue;
                        values[field.Name] = field.Value.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;
                        units[field.Name] = Unit(series, category, field.Name);
                    }
                    if (series is "cpu" or "load") { values["logical-cpus"] = Number(host, "number-of-cpus"); units["logical-cpus"] = "count"; }
                    points.Add(new HistoryPoint(at, interval, name + (category == "net-edev" ? ":errors" : ""), values, units));
                }
            }
        }
        return (points, restarts);
    }
    private static string Unit(string series, string category, string field) =>
        field.Contains("percent", StringComparison.Ordinal) || series == "cpu" ? "%" :
        field is "await" or "r_await" or "w_await" ? "ms" :
        field is "aqu-sz" or "avgqu-sz" ? "requests" :
        field is "rkB" or "wkB" or "dkB" or "rxkB" or "txkB" ? "KiB/s" :
        field is "tps" or "r/s" or "w/s" or "d/s" ? "operations/s" :
        series == "memory" && category != "swap-pages" ? "KiB" :
        category == "swap-pages" ? "pages/s" :
        series == "network" ? "events/s" :
        field is "rd_sec" or "wr_sec" or "dc_sec" ? "sectors/s" :
        field is "areq-sz" ? "KiB/request" : field is "avgrq-sz" ? "sectors/request" : "count";
    private static double? Number(JsonElement item, string name) => item.TryGetProperty(name, out var element) && element.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;

    private static async Task<OperationResult> ReportSeriesAsync(MachineWorkflowContext context, AgentEnvironment agent, string source, string? version, HistoryInput input,
        List<HistoryPoint> points, List<JsonElement> restarts, List<string> archives, List<string> warnings, CancellationToken token)
    {
        var window = input.Window(); var ordered = points.DistinctBy(p => (p.At, p.Resource)).OrderBy(p => p.At).ThenBy(p => p.Resource, StringComparer.Ordinal).ToArray();
        var previous = new Dictionary<string, DateTimeOffset>(); var selected = new List<HistoryPoint>(); var gaps = new List<object>();
        foreach (var point in ordered)
        {
            if (previous.TryGetValue(point.Resource, out var last))
            {
                if (point.NativeIntervalSeconds is > 0 && (point.At - last).TotalSeconds > point.NativeIntervalSeconds.Value * 1.5)
                    gaps.Add(new { point.Resource, Start = last, End = point.At, Reason = "No stored sample covers this interval; no interpolation was performed." });
            }
            previous[point.Resource] = point.At;
        }
        var selectedBuckets = new Dictionary<string, long>();
        foreach (var point in ordered)
        {
            var bucket = (long)Math.Floor((point.At - window.Start).TotalSeconds / input.IntervalSeconds);
            if (!selectedBuckets.TryGetValue(point.Resource, out var previousBucket) || bucket > previousBucket)
            { selected.Add(point); selectedBuckets[point.Resource] = bucket; }
        }
        if (ordered.Length > 0 && ordered.All(p => p.Values.Values.All(v => v == null))) warnings.Add("NotRecorded: all returned values are unavailable, not zero.");
        if (ordered.Length == 0) warnings.Add("NoSamples: this series, resource or time window was not recorded in the available archives.");
        if (ordered.Length > 0)
        {
            var first = ordered[0]; var last = ordered[^1];
            if (first.At - TimeSpan.FromSeconds(first.NativeIntervalSeconds ?? 0) > window.Start)
                gaps.Add(new { Resource = "all", window.Start, End = first.At, Reason = "Requested start predates the observed coverage." });
            if (last.At + TimeSpan.FromSeconds(last.NativeIntervalSeconds ?? 0) < window.End)
                gaps.Add(new { Resource = "all", Start = last.At, window.End, Reason = "Requested end exceeds the observed coverage." });
        }
        var after = input.After == null ? null : HistoryCursor.Decode(input.After);
        var remaining = selected.Where(p => after == null || p.At > after.At || p.At == after.At && string.CompareOrdinal(p.Resource, after.Resource) > 0).ToArray();
        var truncated = remaining.Length > input.Limit; var rows = remaining.Take(input.Limit).ToArray();
        var data = new { Source = source, ToolVersion = version, RequestedStart = window.Start, RequestedEnd = window.End,
                ObservedStart = ordered.FirstOrDefault()?.At, ObservedEnd = ordered.LastOrDefault()?.At,
                RequestedIntervalSeconds = input.IntervalSeconds, Selection = "First original sample per resource in each requested-start-aligned interval bucket; without averaging or interpolation.",
                Samples = rows, Restarts = restarts, Archives = archives, MissingIntervals = gaps, Warnings = warnings, Truncated = truncated,
                NextCursor = truncated && rows.Length > 0 ? new HistoryCursor(rows[^1].At, rows[^1].Resource).Encode() : null, input.Limit,
                ResourceAccounting = "Interfaces and stacked/block devices are separate observations; do not sum overlapping resources." };
        var complete = warnings.Count == 0 && gaps.Count == 0 && restarts.Count == 0 && !truncated;
        ArtifactReference? artifact = null;
        var json = JsonSerializer.Serialize(data);
        if (json.Length > 8192 && agent.Artifacts != null)
        {
            var file = Path.Combine(Path.GetTempPath(), "dms-history-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                await File.WriteAllTextAsync(file, json, token);
                artifact = await agent.Artifacts.UploadAsync(context.Id, file, "performance-history.json", "file", token);
            }
            finally { File.Delete(file); }
        }
        return await OperationOutput.ReportAsync(context, "Stored performance samples queried; native intervals are retained and gaps are not interpolated.", data, complete, artifact);
    }

    public static async Task<OperationResult> PcpHistoryAsync(MachineWorkflowContext context, PcpHistoryInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var host = OperationOutput.Host(agent); var warnings = new List<string>(); var points = new List<HistoryPoint>();
        var window = new HistoryInput(input.Since, input.Until, input.IntervalSeconds, input.Limit, After: input.After);
        var (start, end) = window.Window(); window = window with { Since = start, Until = end }; var archive = Discover(host).SingleOrDefault(s => s.Kind == "pcp" && s.Id.Equals(input.ArchiveId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Archive ID is unavailable or unsafe; refresh history-sources.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        string? version = null;
        try
        {
            version = await VersionAsync(context, host, "pmrep", warnings, deadline.Token);
            if (version != null)
            {
                foreach (var metric in input.Metrics.Distinct(StringComparer.Ordinal))
                {
                    // Descriptor inspection excludes string/event/aggregate values before they can enter trace output.
                    var description = await NativeAsync(context, host, "pminfo", ["-a", archive.Path, "-d", metric], deadline.Token, 65536);
                    if (!NumericDescriptor().IsMatch(description.Output)) throw new ArgumentException("PCP history only accepts numeric scalar or numeric instance metrics.");
                    var replayStart = input.After == null ? start : start.AddSeconds(Math.Floor((HistoryCursor.Decode(input.After).At - start).TotalSeconds / input.IntervalSeconds) * input.IntervalSeconds);
                    if (replayStart < start || replayStart > end) throw new ArgumentException("History cursor is outside the requested window.");
                    var args = new List<string> { "-a", archive.Path, "-S", replayStart.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
                        "-T", end.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), "-t", input.IntervalSeconds.ToString(CultureInfo.InvariantCulture),
                        "-u", "-r", "-o", "csv", "-p", "-f", "%s", "-s", (input.Limit + 1).ToString(CultureInfo.InvariantCulture) };
                    if (input.Instance != null) args.AddRange(["-i", "^" + Regex.Escape(input.Instance) + "$"]);
                    args.Add(metric);
                    var output = await NativeAsync(context, host, "pmrep", args, deadline.Token);
                    points.AddRange(ParsePcpCsv(output.Output, metric, description.Output, start, end));
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { warnings.Add("PCP replay reached its 60-second budget; previous metrics are retained."); }
        catch (Exception exception) when (exception is IOException or NotSupportedException) { warnings.Add(exception.Message); }
        return await ReportSeriesAsync(context, agent, "pcp (raw stored numeric values)", version, window, points, [], [archive.Id], warnings, token);
    }
    public static IEnumerable<HistoryPoint> ParsePcpCsv(string text, string metric, string descriptor, DateTimeOffset start, DateTimeOffset end)
    {
        string[]? names = null;
        var unitMatch = Regex.Match(descriptor, @"Units:\s*([^\r\n]+)", RegexOptions.CultureInvariant);
        var units = unitMatch.Success ? unitMatch.Groups[1].Value.Trim() : "unknown";
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = CsvFields(line.TrimEnd('\r'));
            if (fields.Length < 2) continue;
            if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) { names ??= fields; continue; }
            if (!double.IsFinite(seconds) || seconds < 0 || seconds > 253402300799) continue;
            var at = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
            if (at < start || at > end) continue;
            for (var i = 1; i < fields.Length; i++)
            {
                double? value = double.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
                var name = names != null && i < names.Length ? names[i] : i.ToString(CultureInfo.InvariantCulture);
                yield return new HistoryPoint(at, null, metric + ":" + name,
                    new Dictionary<string, double?> { [metric] = value }, new Dictionary<string, string> { [metric] = units });
            }
        }
    }
    private static string[] CsvFields(string line)
    {
        var fields = new List<string>(); var value = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { fields.Add(value.ToString()); value.Clear(); }
            else value.Append(c);
        }
        if (quoted) throw new InvalidDataException("PCP CSV contains an unterminated field.");
        fields.Add(value.ToString()); return fields.ToArray();
    }
    [GeneratedRegex(@"\Asa(?:[0-9]{2}|[0-9]{8})\z")]
    private static partial Regex SysstatName();
    [GeneratedRegex(@"\A(.+)\.meta(?:\.(?:xz|zst|gz|bz2))?\z")]
    private static partial Regex PcpMetaName();
    [GeneratedRegex(@"\A[a-zA-Z][a-zA-Z0-9_]*(?:\.[a-zA-Z][a-zA-Z0-9_]*)+\z")]
    private static partial Regex MetricName();
    [GeneratedRegex(@"Data type:\s*(?:32-bit|64-bit|float|double|unsigned|signed)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumericDescriptor();
}
