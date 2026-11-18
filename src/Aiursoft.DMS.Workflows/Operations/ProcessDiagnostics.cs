using System.Globalization;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public sealed record DiskCounters(
    long ReadOperations,
    long ReadBytes,
    long ReadMilliseconds,
    long WriteOperations,
    long WriteBytes,
    long WriteMilliseconds,
    long Inflight,
    long BusyMilliseconds,
    long WeightedMilliseconds);

public sealed record DiskIoObservation(
    string Device,
    long? ReadBytes,
    long? WriteBytes,
    long? BusyMilliseconds,
    double ElapsedSeconds,
    double? ReadBytesPerSecond,
    double? WriteBytesPerSecond,
    double? ReadIops,
    double? WriteIops,
    double? ReadAwaitMilliseconds,
    double? WriteAwaitMilliseconds,
    double? AwaitMilliseconds,
    double? AverageQueueDepth,
    double? BusyPercent,
    long Inflight,
    string? MissingReason);

public static class ProcessDiagnostics
{
    public static async Task<Dictionary<string, DiskCounters>> ReadDiskCountersAsync(OperationHost host,
        CancellationToken token)
    {
        var result = new Dictionary<string, DiskCounters>(StringComparer.Ordinal);
        foreach (var line in (await host.ReadAsync("/proc/diskstats", token)).Split('\n',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 14) continue;
            long N(int i) => long.Parse(f[i], CultureInfo.InvariantCulture);
            result[f[2]] = new DiskCounters(N(3),
                checked(N(5) * 512),
                N(6),
                N(7),
                checked(N(9) * 512),
                N(10),
                N(11),
                N(12),
                N(13));
        }

        return result;
    }

    public static DiskIoObservation DiskDelta(string device,
        DiskCounters? previous,
        DiskCounters current,
        double elapsed)
    {
        string? reason = previous == null
            ? "Device was not present at the preceding observation."
            : elapsed <= 0
                ? "Elapsed time is unavailable."
                : current.ReadOperations < previous.ReadOperations ||
                  current.WriteOperations < previous.WriteOperations ||
                  current.ReadBytes < previous.ReadBytes || current.WriteBytes < previous.WriteBytes ||
                  current.ReadMilliseconds < previous.ReadMilliseconds ||
                  current.WriteMilliseconds < previous.WriteMilliseconds ||
                  current.BusyMilliseconds < previous.BusyMilliseconds ||
                  current.WeightedMilliseconds < previous.WeightedMilliseconds
                    ? "Device counters reset; no rate was calculated across the reset."
                    : null;
        if (reason != null)
            return new DiskIoObservation(device,
                null,
                null,
                null,
                elapsed,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                current.Inflight,
                reason);
        var read = current.ReadOperations - previous!.ReadOperations;
        var write = current.WriteOperations - previous.WriteOperations;
        var readMs = current.ReadMilliseconds - previous.ReadMilliseconds;
        var writeMs = current.WriteMilliseconds - previous.WriteMilliseconds;
        var readBytes = current.ReadBytes - previous.ReadBytes;
        var writeBytes = current.WriteBytes - previous.WriteBytes;
        var busy = current.BusyMilliseconds - previous.BusyMilliseconds;
        return new DiskIoObservation(device,
            readBytes,
            writeBytes,
            busy,
            elapsed,
            readBytes / elapsed,
            writeBytes / elapsed,
            read / elapsed,
            write / elapsed,
            read > 0 ? (double)readMs / read : null,
            write > 0 ? (double)writeMs / write : null,
            read + write > 0 ? (double)(readMs + writeMs) / (read + write) : null,
            (current.WeightedMilliseconds - previous.WeightedMilliseconds) / (elapsed * 1000),
            busy / (elapsed * 10),
            current.Inflight,
            null);
    }

    public static async Task<OperationResult> BlockedProcessesAsync(MachineWorkflowContext context,
        BlockedProcessesInput input,
        AgentEnvironment agent,
        CancellationToken token)
    {
        input.Validate();
        var host = OperationOutput.Host(agent);
        var rows = new List<object>();
        var skipped = 0;
        var scanned = 0;
        var unavailableWaitSymbols = 0;
        var truncated = false;
        var proc = host.FilePath("/proc");
        foreach (var directory in Directory.EnumerateDirectories(proc))
        {
            token.ThrowIfCancellationRequested();
            if (!int.TryParse(Path.GetFileName(directory), out var pid) ||
                input.Pid != null && pid != input.Pid) continue;
            string cgroup;
            string? container;
            string uid;
            try
            {
                cgroup = await host.ReadAsync($"/proc/{pid}/cgroup", token, 65536);
                container = System.Text.RegularExpressions.Regex.Match(cgroup,
                    "[0-9a-f]{64}",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant).Value;
                if (container.Length == 0) container = null;
                if (input.Container != null && input.Container != container) continue;
                var status =
                    PerformanceOperations.ParseColonValues(await host.ReadAsync($"/proc/{pid}/status", token, 65536));
                uid = status.GetValueOrDefault("Uid", "unknown").Split(' ', '\t')[0];
                var task = host.FilePath($"/proc/{pid}/task");
                foreach (var thread in Directory.EnumerateDirectories(task))
                {
                    if (++scanned > 10000)
                    {
                        truncated = true;
                        break;
                    }

                    if (!int.TryParse(Path.GetFileName(thread), out var tid)) continue;
                    try
                    {
                        var stat = await host.ReadAsync($"/proc/{pid}/task/{tid}/stat", token, 65536);
                        var end = stat.LastIndexOf(')');
                        var start = stat.IndexOf('(');
                        if (start < 0 || end <= start || stat.Length < end + 4 || stat[end + 2] != 'D') continue;
                        string? wait = null;
                        string? missing = null;
                        try
                        {
                            wait = (await host.ReadAsync($"/proc/{pid}/task/{tid}/wchan", token, 4096)).Trim();
                            if (wait is "0" or "")
                            {
                                wait = null;
                                missing = "Kernel wait symbol is hidden or unavailable.";
                            }
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            missing = "Wait symbol could not be read; thread may have exited or access was denied.";
                        }

                        if (missing != null) unavailableWaitSymbols++;
                        if (rows.Count == input.Limit)
                        {
                            truncated = true;
                            break;
                        }

                        rows.Add(new
                        {
                            Pid = pid, Tid = tid, Name = stat[(start + 1)..end], UserId = uid, State = "D",
                            WaitChannel = wait,
                            Cgroup = cgroup.Trim(), ContainerId = container, MissingReason = missing
                        });
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        skipped++;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }

            if (truncated) break;
        }

        return await OperationOutput.ReportAsync(context,
            "Blocked thread snapshot collected; exited historical processes cannot be reconstructed.",
            new
            {
                At = DateTimeOffset.UtcNow, Threads = rows, ScannedThreads = scanned, SkippedOrUnreadable = skipped,
                UnavailableWaitSymbols = unavailableWaitSymbols, Truncated = truncated,
                Visibility =
                    "D state means uninterruptible wait; the wait symbol is evidence, " +
                    "not proof of a particular failed device. Command lines and " +
                    "environment are not read."
            },
            !truncated && skipped == 0 && unavailableWaitSymbols == 0);
    }
}
