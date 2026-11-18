using System.Globalization;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public sealed record ProcessObservation(int Pid, string Name, string UserId, long ResidentBytes,
    long CpuTicks, long StartTicks, long? ReadBytes, long? WriteBytes, string Cgroup, string? ContainerId);

public static partial class PerformanceOperations
{
    public static async Task<IReadOnlyList<ProcessObservation>> ReadProcessesAsync(OperationHost host, CancellationToken token)
    {
        List<ProcessObservation> result = [];
        foreach (var directory in Directory.EnumerateDirectories(host.FilePath("/proc")))
        {
            token.ThrowIfCancellationRequested();
            if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;
            try
            {
                var stat = await host.ReadAsync($"/proc/{pid}/stat", token, 65536);
                var end = stat.LastIndexOf(')'); var start = stat.IndexOf('(');
                if (end < start || start < 0) continue;
                var fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 22) continue;
                var status = await host.ReadAsync($"/proc/{pid}/status", token, 65536);
                var values = ParseColonValues(status);
                var cgroup = await host.ReadAsync($"/proc/{pid}/cgroup", token, 65536);
                var io = new Dictionary<string, string>();
                try { io = ParseColonValues(await host.ReadAsync($"/proc/{pid}/io", token, 65536)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                var container = ContainerIdPattern().Match(cgroup);
                result.Add(new ProcessObservation(pid, stat[(start + 1)..end], values.GetValueOrDefault("Uid", "unknown").Split('\t', ' ')[0],
                    Kilobytes(values.GetValueOrDefault("VmRSS", "0")), Number(fields[11]) + Number(fields[12]), Number(fields[19]),
                    io.TryGetValue("read_bytes", out var read) ? Number(read) : null, io.TryGetValue("write_bytes", out var written) ? Number(written) : null,
                    cgroup.Trim(), container.Success ? container.Value : null));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                // /proc is a live view: a process may exit or deny access between reads.
            }
        }
        return result;
    }

    public static async Task<OperationResult> ProcessesAsync(MachineWorkflowContext context, ProcessListInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var host = OperationOutput.Host(agent);
        var before = (await ReadProcessesAsync(host, token)).ToDictionary(p => (p.Pid, p.StartTicks));
        var counters = await ReadCpuAsync(host, token); var cpuBefore = counters["cpu"].Sum();
        var cpuCount = Math.Max(1, counters.Count - 1);
        await Task.Delay(1000, token);
        var after = await ReadProcessesAsync(host, token);
        var cpuDelta = Math.Max(1, (await ReadCpuAsync(host, token))["cpu"].Sum() - cpuBefore);
        var observations = after.Select(p => new
        {
            p.Pid, p.Name, p.UserId, p.ResidentBytes, p.Cgroup, p.ContainerId,
            CpuPercent = before.TryGetValue((p.Pid, p.StartTicks), out var previous) ? Math.Max(0, p.CpuTicks - previous.CpuTicks) * 100.0 * cpuCount / cpuDelta : (double?)null
        });
        var sorted = input.Sort switch
        {
            "memory" => observations.OrderByDescending(p => p.ResidentBytes),
            "pid" => observations.OrderBy(p => p.Pid),
            _ => observations.OrderByDescending(p => p.CpuPercent)
        };
        return await OperationOutput.ReportAsync(context, "Process observations collected; command-line arguments and environments are excluded.", new { ObservedProcesses = after.Count, Top = sorted.Take(input.Limit).ToArray(), input.Limit, ProcessVisibility = "Processes readable in this Agent's namespaces at sample time." });
    }

    public static async Task<Dictionary<string, long[]>> ReadCpuAsync(OperationHost host, CancellationToken token) =>
        (await host.ReadAsync("/proc/stat", token)).Split('\n').Where(l => l.StartsWith("cpu", StringComparison.Ordinal))
        .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToDictionary(f => f[0], f => f.Skip(1).Take(8).Select(Number).ToArray(), StringComparer.Ordinal);

    public static async Task<OperationResult> CpuSampleAsync(MachineWorkflowContext context, SampleInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var host = OperationOutput.Host(agent); var previous = await ReadCpuAsync(host, token);
        return await SampleAsync(context, input, async sampling =>
        {
            var current = await ReadCpuAsync(host, sampling);
            var rows = current.Select(pair =>
            {
                previous.TryGetValue(pair.Key, out var old); old ??= new long[pair.Value.Length];
                var delta = pair.Value.Zip(old, (value, before) => Math.Max(0, value - before)).ToArray();
                var total = Math.Max(1, delta.Sum());
                return new { Cpu = pair.Key, BusyPercent = (total - delta[3] - delta[4]) * 100.0 / total, IoWaitPercent = delta[4] * 100.0 / total, StealPercent = delta[7] * 100.0 / total };
            }).ToArray(); previous = current; return rows;
        }, token);
    }
    public static async Task<OperationResult> MemoryBreakdownAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var host = OperationOutput.Host(agent);
        var memory = ParseColonValues(await host.ReadAsync("/proc/meminfo", token)).ToDictionary(p => p.Key, p => Kilobytes(p.Value));
        var processes = (await ReadProcessesAsync(host, token)).OrderByDescending(p => p.ResidentBytes).Take(50).ToArray();
        List<object> top = []; var pssObserved = 0;
        foreach (var process in processes)
        {
            long? pss = null;
            try
            {
                var values = ParseColonValues(await host.ReadAsync($"/proc/{process.Pid}/smaps_rollup", token));
                if (values.TryGetValue("Pss", out var value)) { pss = Kilobytes(value); pssObserved++; }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            var cgroupMemory = new Dictionary<string, string>();
            var relative = process.Cgroup.Split('\n').FirstOrDefault(l => l.StartsWith("0::/", StringComparison.Ordinal))?[3..];
            if (relative != null && !relative.Split('/').Contains(".."))
            {
                foreach (var item in new[] { "memory.current", "memory.max", "memory.swap.current", "memory.events" })
                    try { cgroupMemory[item] = (await host.ReadAsync("/sys/fs/cgroup" + relative.TrimEnd('/') + "/" + item, token)).Trim(); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
            top.Add(new { process.Pid, process.Name, process.ResidentBytes, ProportionalSetBytes = pss, process.Cgroup, process.ContainerId, CgroupMemory = cgroupMemory });
        }
        return await OperationOutput.ReportAsync(context, "Memory components and largest visible processes collected; RSS includes shared pages and is not additive.", new { Bytes = memory, LargestProcesses = top, PssUnavailable = pssObserved == 0, PssObservedProcesses = pssObserved });
    }

    public static async Task<Dictionary<string, long[]>> ReadDisksAsync(OperationHost host, CancellationToken token) =>
        (await host.ReadAsync("/proc/diskstats", token)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(f => f.Length >= 14)
        .ToDictionary(f => f[2], f => new[] { Number(f[5]) * 512, Number(f[9]) * 512, Number(f[12]) }, StringComparer.Ordinal);

    public static async Task<OperationResult> IoSampleAsync(MachineWorkflowContext context, SampleInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var host = OperationOutput.Host(agent); var clock = System.Diagnostics.Stopwatch.StartNew();
        var disks = await ProcessDiagnostics.ReadDiskCountersAsync(host, token); var preceding = clock.Elapsed.TotalSeconds;
        var processes = (await ReadProcessesAsync(host, token)).ToDictionary(p => (p.Pid, p.StartTicks));
        return await SampleAsync(context, input, async sampling =>
        {
            var nextDisks = await ProcessDiagnostics.ReadDiskCountersAsync(host, sampling); var now = clock.Elapsed.TotalSeconds; var elapsed = now - preceding;
            var nextProcesses = await ReadProcessesAsync(host, sampling);
            var devices = nextDisks.Select(d => ProcessDiagnostics.DiskDelta(d.Key, disks.GetValueOrDefault(d.Key), d.Value, elapsed)).ToArray();
            var disappeared = disks.Keys.Except(nextDisks.Keys, StringComparer.Ordinal).ToArray();
            var top = nextProcesses.Select(p => new { p.Pid, p.Name, p.ContainerId,
                ReadBytes = processes.TryGetValue((p.Pid, p.StartTicks), out var previous) ? IoDelta(p.ReadBytes, previous.ReadBytes) : null,
                WriteBytes = processes.TryGetValue((p.Pid, p.StartTicks), out var old) ? IoDelta(p.WriteBytes, old.WriteBytes) : null })
                .OrderByDescending(p => p.ReadBytes + p.WriteBytes).Take(input.Limit).ToArray();
            disks = nextDisks; processes = nextProcesses.ToDictionary(p => (p.Pid, p.StartTicks)); preceding = now;
            return new { Devices = devices, Processes = top, DisappearedDevices = disappeared, ActualIntervalSeconds = elapsed,
                ProcessIoVisibility = "New, unreadable or reset per-process counters are unknown, not zero. Device busy percent is not a multi-queue saturation verdict." };
        }, token);
    }
    private static long? IoDelta(long? current, long? previous) => current.HasValue && previous.HasValue && current.Value >= previous.Value ? current.Value - previous.Value : null;
    public static async Task<OperationResult> DstatSampleAsync(MachineWorkflowContext context, SampleInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var host = OperationOutput.Host(agent); var cpu = (await ReadCpuAsync(host, token))["cpu"];
        var disks = await ReadDisksAsync(host, token); var network = await ReadNetworkAsync(host, token);
        return await SampleAsync(context, input, async sampling =>
        {
            var nextCpu = (await ReadCpuAsync(host, sampling))["cpu"]; var total = Math.Max(1, nextCpu.Sum() - cpu.Sum());
            var nextDisks = await ReadDisksAsync(host, sampling); var nextNetwork = await ReadNetworkAsync(host, sampling);
            var observation = new
            {
                CpuBusyPercent = (total - Math.Max(0, nextCpu[3] - cpu[3]) - Math.Max(0, nextCpu[4] - cpu[4])) * 100.0 / total,
                IoWaitPercent = Math.Max(0, nextCpu[4] - cpu[4]) * 100.0 / total,
                Memory = ParseColonValues(await host.ReadAsync("/proc/meminfo", sampling)).ToDictionary(p => p.Key, p => Kilobytes(p.Value)),
                Load = (await host.ReadAsync("/proc/loadavg", sampling)).Trim(),
                Disks = nextDisks.Select(d => new { Device = d.Key, ReadBytes = Math.Max(0, d.Value[0] - disks.GetValueOrDefault(d.Key, d.Value)[0]), WriteBytes = Math.Max(0, d.Value[1] - disks.GetValueOrDefault(d.Key, d.Value)[1]) }).ToArray(),
                Network = nextNetwork.Select(n => new { Interface = n.Key, ReceivedBytes = Math.Max(0, n.Value[0] - network.GetValueOrDefault(n.Key, n.Value)[0]), SentBytes = Math.Max(0, n.Value[1] - network.GetValueOrDefault(n.Key, n.Value)[1]) }).ToArray()
            };
            cpu = nextCpu; disks = nextDisks; network = nextNetwork; return observation;
        }, token);
    }
    public static async Task<Dictionary<string, long[]>> ReadNetworkAsync(OperationHost host, CancellationToken token) =>
        (await host.ReadAsync("/proc/net/dev", token)).Split('\n').Where(l => l.Contains(':')).Select(l => l.Split(':', 2))
        .ToDictionary(f => f[0].Trim(), f => { var values = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries); return new[] { Number(values[0]), Number(values[8]) }; }, StringComparer.Ordinal);

    public static async Task<OperationResult> PressureAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var host = OperationOutput.Host(agent); var values = new Dictionary<string, string>();
        foreach (var resource in new[] { "cpu", "memory", "io" })
            values[resource] = await host.ReadAsync("/proc/pressure/" + resource, token);
        return await OperationOutput.ReportAsync(context, "Linux pressure stall information collected.", values);
    }
    public static async Task<OperationResult> OomHistoryAsync(MachineWorkflowContext context, EventWindowInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var args = SystemOperations.JournalArguments(input.Since, input.Until, input.Limit); args.Add("--dmesg");
        var result = await OperationOutput.CommandAsync(context, agent, "journalctl", args, token, traceOutput: false);
        var matches = result.Output.Split('\n').Where(l => OomPattern().IsMatch(l)).ToArray();
        return await OperationOutput.ReportAsync(context, "OOM evidence within the bounded kernel journal window.", new { Records = matches, ScannedLimit = input.Limit });
    }
    public static async Task<OperationResult> SampleAsync(MachineWorkflowContext context, SampleInput input, Func<CancellationToken, Task<object>> sample, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow; var count = 0; var clock = System.Diagnostics.Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(input.Seconds + 5));
        try
        {
            for (var due = input.Interval; due <= input.Seconds; due += input.Interval)
            {
                var delay = TimeSpan.FromSeconds(due) - clock.Elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, deadline.Token);
                if (clock.Elapsed.TotalSeconds > input.Seconds + 1) break;
                await OperationOutput.ReportAsync(context, "Sample recorded.", new { At = DateTimeOffset.UtcNow, NominalIntervalSeconds = input.Interval, ElapsedSeconds = clock.Elapsed.TotalSeconds, Values = await sample(deadline.Token) }); count++;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { await context.WarningAsync("Sampling hit its total deadline; collected samples are partial."); }
        if (count < input.Seconds / input.Interval)
        { await context.WarningAsync("Some requested sampling intervals could not be collected."); return new OperationResult($"Recorded {count} partial samples from {started:O} to {DateTimeOffset.UtcNow:O}.", false); }
        return new OperationResult($"Recorded {count} samples from {started:O} to {DateTimeOffset.UtcNow:O}.");
    }
    public static Dictionary<string, string> ParseColonValues(string content) => content.Split('\n').Where(l => l.Contains(':')).Select(l => l.Split(':', 2)).ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.Ordinal);
    public static long Kilobytes(string value) => Number(value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]) * 1024;
    public static long Number(string value) => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    [GeneratedRegex("[0-9a-f]{64}")]
    private static partial Regex ContainerIdPattern();
    [GeneratedRegex("oom|out of memory|killed process", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OomPattern();
}
