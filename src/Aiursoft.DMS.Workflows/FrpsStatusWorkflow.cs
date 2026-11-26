using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows.Operations;
using static Aiursoft.DMS.Workflows.FrpsLayout;

namespace Aiursoft.DMS.Workflows;

public sealed partial class FrpsStatusWorkflow(EmptyInput input, AgentEnvironment agent)
    : MachineWorkflow<EmptyInput, FrpsStatusResult>(MachineWorkflows.FrpsStatus, input)
{
    private const int JournalMinutes = 60;
    private const int JournalLimit = 250;
    private readonly OperationHost _host = agent.Operations ?? OperationHost.Default;

    public override async Task<FrpsStatusResult> ExecuteAsync(MachineWorkflowContext context, EmptyInput input, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("FRPS status requires Linux with systemd.");
        var services = new List<FrpsServiceStatus>();
        var standard = await InspectAsync(Unit, false, token);
        if (standard is not null) services.Add(standard);
        var wrapped = await InspectAsync(WrappedUnit, true, token);
        if (wrapped is not null) services.Add(wrapped);
        await context.TraceAsync($"FRPS inspection completed. Found {services.Count} service units. Versions, service states, listeners and journal counts are in the workflow result.");
        return new FrpsStatusResult(DateTimeOffset.UtcNow, services);
    }

    private async Task<FrpsServiceStatus?> InspectAsync(string unit, bool wrapped, CancellationToken token)
    {
        var state = await ReadAsync("systemctl", ["show", "-p",
            "LoadState,ActiveState,SubState,UnitFileState,MainPID,ExecMainStartTimestamp,NRestarts,ExecStart", unit], token);
        var values = (state?.Output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2)).Where(fields => fields.Length == 2)
            .ToDictionary(fields => fields[0], fields => fields[1], StringComparer.Ordinal);
        string Property(string name) => values.GetValueOrDefault(name, "");
        var loadState = Property("LoadState");
        var matches = ExecStart().Matches(state?.Output ?? "");
        var exec = matches.Count == 1 ? matches[0] : Match.Empty;
        var executable = exec.Groups["binary"].Value;
        var config = exec.Groups["config"].Value;
        var supported = matches.Count == 1 && executable == exec.Groups["argv"].Value &&
            (wrapped ? executable == Wrapper && config == WrappedConfig : Path.GetFileName(executable) == "frps");
        if (wrapped && !supported && !_host.Exists(WrappedBinary)) return null;
        if (!wrapped && loadState == "not-found") return null;

        var diagnostics = new List<string>();
        if (!supported) diagnostics.Add("The unit's configured FRPS service layout could not be identified.");
        var binary = supported ? wrapped ? WrappedBinary : executable : null;
        var mainPid = Number(Property("MainPID"));
        var pid = mainPid is > 0 ? mainPid : null;
        var canonical = binary is null ? null : (await ReadAsync("realpath", ["-e", "--", binary], token))?.Output.Trim();
        var version = canonical is null ? null : await ReadVersionAsync(canonical, wrapped, token);
        if (binary is not null && version is null) diagnostics.Add("The installed FRPS version could not be verified.");
        var runningPath = pid is null ? null :
            (await ReadAsync("readlink", [$"/proc/{pid}/exe"], token))?.Output.Trim();
        var runningVersion = canonical is not null && runningPath == canonical ? version : null;
        if (pid is not null && runningVersion is null) diagnostics.Add("The running executable could not be matched to the installed version.");
        var descriptor = wrapped && pid is not null && binary is not null
            ? await ReadAsync("readlink", [$"/proc/{pid}/fd/3"], token)
            : null;
        var inMemory = descriptor is null ? (bool?)null : descriptor.Output.Trim() == "/memfd:turn.toml (deleted)";
        var listeners = pid is null ? null : await ReadListenersAsync(pid.Value, token);
        if (pid is not null && listeners is null) diagnostics.Add("FRPS listening sockets could not be read.");
        FrpsJournalSummary? journal;
        try { journal = await ReadJournalAsync(unit, token); }
        catch (JsonException) { journal = null; }
        if (journal is null) diagnostics.Add("The bounded FRPS journal sample could not be read.");

        var started = DateTimeOffset.TryParseExact(Property("ExecMainStartTimestamp"), "ddd yyyy-MM-dd HH:mm:ss 'UTC'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : (DateTimeOffset?)null;
        return new FrpsServiceStatus(unit, wrapped ? "encrypted-wrapper" : "systemd", loadState,
            Property("ActiveState"), Property("SubState"), Property("UnitFileState"), pid, started,
            Number(Property("NRestarts")), binary, version, runningVersion, runningPath,
            supported ? config : null, supported ? _host.Exists(config) : null, inMemory, listeners, journal, diagnostics);
    }

    private async Task<string?> ReadVersionAsync(string path, bool wrapped, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path) || !(wrapped ? path == WrappedBinary : Path.GetFileName(path) == "frps"))
            return null;

        var paths = new List<string> { path };
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            paths.Add(parent);
        var args = new[] { "-L", "--format=%u:%a:%F", "--" }.Concat(paths).ToArray();
        var metadata = await ReadAsync("stat", args, token);
        var lines = metadata?.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (lines.Length != paths.Count || !lines.Select((line, i) => TrustedMode(line, i == 0)).All(trusted => trusted))
            return null;
        var version = (await ReadAsync(path, ["--version"], token))?.Output.Trim();
        return version is not null && VersionPattern().IsMatch(version) ? version : null;
    }

    private static bool TrustedMode(string metadata, bool executable)
    {
        var parts = metadata.Split(':', 3);
        var mode = parts.Length == 3 ? parts[1] : "";
        if (parts.Length != 3 || parts[0] != "0" || mode.Length is < 3 or > 4 || mode.Any(d => d is < '0' or > '7'))
            return false;
        if (((mode[^2] - '0') & 2) != 0 || ((mode[^1] - '0') & 2) != 0) return false;
        return executable ? parts[2] is "regular file" or "regular empty file" : parts[2] == "directory";
    }

    private async Task<IReadOnlyList<FrpsListener>?> ReadListenersAsync(int pid, CancellationToken token)
    {
        var sockets = await ReadAsync("ss", ["-H", "-lntup"], token);
        return sockets?.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split((char[]?)null, 7, StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length == 7 && fields[0] is "tcp" or "udp" &&
                fields[6].Contains($"pid={pid},", StringComparison.Ordinal))
            .Select(fields => new FrpsListener(fields[0], fields[4]))
            .Distinct().OrderBy(row => row.Protocol, StringComparer.Ordinal)
            .ThenBy(row => row.LocalEndpoint, StringComparer.Ordinal).ToArray();
    }

    private async Task<FrpsJournalSummary?> ReadJournalAsync(string unit, CancellationToken token)
    {
        var journal = await ReadAsync("journalctl", ["-u", unit, "--since", "60 minutes ago", "-n",
            JournalLimit.ToString(CultureInfo.InvariantCulture), "--no-pager", "-o", "json",
            "--output-fields=MESSAGE,PRIORITY"], token);
        if (journal is null) return null;
        var entries = 0;
        var warnings = 0;
        var errors = 0;
        var logins = 0;
        var replacements = 0;
        var starts = 0;
        var closes = 0;
        var workFailures = 0;
        var poolFull = 0;
        foreach (var line in journal.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var parsed = JsonDocument.Parse(line);
            var row = parsed.RootElement;
            entries++;
            var value = row.TryGetProperty("MESSAGE", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString() ?? ""
                : "";
            var severity = row.TryGetProperty("PRIORITY", out var priority) && priority.ValueKind == JsonValueKind.String
                ? Number(priority.GetString() ?? "")
                : null;
            var messageLevel = MessageLevel().Match(value).Groups[1].Value;
            if (severity is <= 3 || messageLevel is "E" or "F") errors++;
            else if (severity == 4 || messageLevel == "W") warnings++;
            bool Has(string text) => value.Contains(text, StringComparison.OrdinalIgnoreCase);
            if (Has("client login info")) logins++;
            if (Has("replaced by client")) replacements++;
            if (Has("new proxy") && Has("success")) starts++;
            if (Has("proxy closing")) closes++;
            if (Has("failed to get work connection") || Has("no work connections available")) workFailures++;
            if (Has("work connection pool is full")) poolFull++;
        }
        return new FrpsJournalSummary(JournalMinutes, JournalLimit, entries, entries >= JournalLimit, warnings,
            errors, logins, replacements, starts, closes, workFailures, poolFull);
    }

    private async Task<NativeResult?> ReadAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        try
        {
            var result = await _host.RunAsync(new NativeCommand(executable, arguments, TimeoutSeconds: 8,
                Environment: new Dictionary<string, string> { ["TZ"] = "UTC" }, MaxCharacters: 524288), token);
            return result.ExitCode == 0 ? result : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception e) when (e is IOException or NotSupportedException or Win32Exception) { return null; }
    }

    private static int? Number(string value) => int.TryParse(value, CultureInfo.InvariantCulture, out var number) ? number : null;

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.+-]+)?\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^\s*\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)? \[([EWF])\] ")]
    private static partial Regex MessageLevel();
}
