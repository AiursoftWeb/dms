using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public static class LayoutOperations
{
    public static async Task<OperationResult> FdiskQueryAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        await OperationOutput.CommandAsync(context, agent, "fdisk", ["--list", "--bytes"], token);
        return new OperationResult("Read-only partition table query completed.");
    }
    public static async Task<OperationResult> MountsAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        await OperationOutput.CommandAsync(context, agent, "findmnt", ["--json", "--bytes", "--output", "TARGET,SOURCE,FSTYPE,OPTIONS,MAJ:MIN"], token);
        return new OperationResult("Mount sources, options and device relationships collected.");
    }
    public static async Task<OperationResult> InodesAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        await context.TraceAsync(FilesystemUsageDiagnostics.Scope);
        try
        {
            await OperationOutput.CommandAsync(context, agent, "df", ["--inodes", "--local", "--exclude-type=overlay", "--exclude-type=tmpfs", "--exclude-type=devtmpfs"], token);
        }
        catch (IOException)
        {
            await context.TraceAsync(FilesystemUsageDiagnostics.Failure, "error");
            throw;
        }
        return new OperationResult("Local filesystem inode usage collected.");
    }
    public static async Task<OperationResult> OpenDeletedAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var result = await OperationOutput.CommandAsync(context, agent, "lsof", ["-nP", "+L1", "-F", "pcnfDsi"], token, traceOutput: false, allowedExitCodes: [0, 1]);
        await OperationOutput.TraceTextAsync(context, result.Output);
        return await OperationOutput.ReportAsync(context, "Open unlinked files collected; the same inode may appear in several processes and must be deduplicated.", new { result.ExitCode, RecordFormat = "lsof tagged records: pid, command, fd, device, size, inode, name.", Empty = result.Output.Length == 0 });
    }
    public static async Task<OperationResult> SmartAsync(MachineWorkflowContext context, DeviceInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var result = await ReadSmartAsync(context, input.Device, agent, token);
        var readFailure = (result.ExitCode & 7) != 0;
        return await OperationOutput.ReportAsync(context, readFailure ? "SMART data is incomplete or unavailable." : "SMART/NVMe health evidence collected.",
            new { result.ExitCode, HealthWarningBits = result.ExitCode & 248, Data = JsonSerializer.Deserialize<JsonElement>(result.Output) }, complete: result.ExitCode == 0);
    }
    private static Task<NativeResult> ReadSmartAsync(MachineWorkflowContext context, string device, AgentEnvironment agent, CancellationToken token) =>
        OperationOutput.CommandAsync(context, agent, "smartctl", ["--json", "--all", "--", device], token, traceOutput: false, allowedExitCodes: Enumerable.Range(0, 256).ToArray());

    public static async Task<OperationResult> SmartTestAsync(MachineWorkflowContext context, SmartTestInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var initial = await ReadSmartAsync(context, input.Device, agent, token);
        if ((initial.ExitCode & 7) != 0) throw new IOException("Cannot read this device's self-test state.");
        var start = await OperationOutput.CommandAsync(context, agent, "smartctl", ["--json", "--test=short", "--", input.Device], token,
            change: true, traceOutput: false, allowedExitCodes: Enumerable.Range(0, 256).Where(n => (n & 7) == 0).ToArray());
        await OperationOutput.TraceTextAsync(context, start.Output, "data");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(input.WaitSeconds);
        var previousLog = SelfTestLog(initial.Output);
        do
        {
            token.ThrowIfCancellationRequested();
            var current = await ReadSmartAsync(context, input.Device, agent, token);
            var log = SelfTestLog(current.Output);
            if (log is { Completed: true } && log.Evidence != previousLog?.Evidence)
                return await OperationOutput.ReportAsync(context, "A new completed short self-test result was observed.", JsonSerializer.Deserialize<JsonElement>(current.Output), complete: current.ExitCode == 0 && log.Passed);
            if (DateTimeOffset.UtcNow >= deadline) break;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalSeconds))), token);
        } while (true);
        return await OperationOutput.ReportAsync(context, "Short self-test was requested; no new completed result was confirmed within the wait window.", new { input.Device, State = "PendingVerification", input.WaitSeconds }, false);
    }
    private sealed record SelfTestEvidence(string Evidence, bool Completed, bool Passed);
    private static SelfTestEvidence? SelfTestLog(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("ata_smart_self_test_log", out var ata))
        {
            var log = ata.TryGetProperty("extended", out var extended) ? extended : ata.TryGetProperty("standard", out var standard) ? standard : default;
            if (log.ValueKind == JsonValueKind.Object && log.TryGetProperty("table", out var entries) && entries.GetArrayLength() > 0)
            {
                var first = entries[0]; var shortTest = first.TryGetProperty("type", out var type) && type.TryGetProperty("value", out var code) && code.GetInt32() == 1;
                var status = first.GetProperty("status");
                var completed = shortTest && status.TryGetProperty("passed", out var passed) && passed.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && (!status.TryGetProperty("remaining_percent", out var remaining) || remaining.GetInt32() == 0);
                return new SelfTestEvidence(entries.GetRawText(), completed, completed && status.GetProperty("passed").GetBoolean());
            }
        }
        if (document.RootElement.TryGetProperty("nvme_self_test_log", out var nvme) && nvme.TryGetProperty("table", out var table) && table.GetArrayLength() > 0)
        {
            var first = table[0];
            var shortTest = first.TryGetProperty("self_test_code", out var code) && code.GetProperty("value").GetInt32() == 1;
            var result = first.GetProperty("self_test_result").GetProperty("value").GetInt32();
            return new SelfTestEvidence(table.GetRawText(), shortTest && result is >= 0 and <= 8, result == 0);
        }
        return null;
    }

    public static async Task<IReadOnlyList<Dictionary<string, string>>> ReadBcacheAsync(OperationHost host, CancellationToken token)
    {
        List<Dictionary<string, string>> devices = [];
        var directory = host.FilePath("/sys/block");
        foreach (var path in Directory.EnumerateDirectories(directory, "bcache*").Take(100))
        {
            var name = Path.GetFileName(path); var row = new Dictionary<string, string> { ["Device"] = name };
            foreach (var field in new[] { "cache_mode", "state", "dirty_data", "sequential_cutoff", "writeback_percent", "writeback_running", "running", "stats_total/cache_hit_ratio", "stats_total/cache_hits", "stats_total/cache_misses" })
            {
                try { row[field] = (await host.ReadAsync($"/sys/block/{name}/bcache/{field}", token)).Trim(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { row[field] = "unavailable"; }
            }
            var backing = new DirectoryInfo(host.FilePath($"/sys/block/{name}/slaves"));
            row["Slaves"] = backing.Exists ? string.Join(',', backing.EnumerateFileSystemInfos().Select(f => f.Name)) : "unavailable";
            var cache = host.FilePath($"/sys/block/{name}/bcache/cache");
            row["CacheSet"] = new DirectoryInfo(cache).LinkTarget ?? "not-attached";
            devices.Add(row);
        }
        return devices;
    }
    public static async Task<OperationResult> BcacheInfoAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var devices = await ReadBcacheAsync(OperationOutput.Host(agent), token);
        return await OperationOutput.ReportAsync(context, devices.Count == 0 ? "No bcache block devices are present." : "Bcache device state collected.", devices);
    }
    public static async Task<OperationResult> BcacheAuditAsync(MachineWorkflowContext context, BaselineInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var devices = await ReadBcacheAsync(OperationOutput.Host(agent), token);
        if (input.BaselinePath == null)
            return await OperationOutput.ReportAsync(context, "Bcache compliance is unknown: no baseline was specified.", new { Status = "Unknown", Devices = devices }, false);
        SafeOperationPaths.Check(OperationOutput.Host(agent).FilePath(input.BaselinePath));
        var baseline = JsonSerializer.Deserialize<BcacheBaseline>(await OperationOutput.Host(agent).ReadAsync(input.BaselinePath, token)) ?? throw new InvalidDataException("Invalid bcache baseline.");
        if (string.IsNullOrWhiteSpace(baseline.Version) || baseline.ExpectedCacheMode is not ("writethrough" or "writeback" or "writearound" or "none")) throw new InvalidDataException("Baseline needs a version and expected cache mode.");
        var results = devices.Select(d => new
        {
            Device = d["Device"], Mode = d["cache_mode"], CacheSet = d["CacheSet"],
            Compliant = d["cache_mode"].Contains("[" + baseline.ExpectedCacheMode + "]", StringComparison.Ordinal)
                && (!baseline.RequireAttachedCache || d["CacheSet"] != "not-attached")
                && (!baseline.RequireRunning || d["running"] == "1")
        }).ToArray();
        return await OperationOutput.ReportAsync(context, "Bcache baseline comparison completed.", new { baseline.Version, Results = results, Devices = devices }, complete: results.Length > 0 && results.All(r => r.Compliant));
    }
    public sealed record BcacheBaseline(string Version, string ExpectedCacheMode, bool RequireAttachedCache = true, bool RequireRunning = true);
}
