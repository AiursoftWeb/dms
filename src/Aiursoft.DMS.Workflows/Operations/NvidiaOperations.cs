using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public static partial class NvidiaOperations
{
    private static readonly string[] SnapshotArguments = ["--query-gpu=index,uuid,name,driver_version,memory.total,memory.used,utilization.gpu,utilization.memory,temperature.gpu,power.draw", "--format=csv,noheader,nounits"];
    private static readonly string[] TopologyArguments = ["topo", "--matrix"];

    public static async Task<OperationResult> SmiAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        await OperationOutput.CommandAsync(context, agent, "nvidia-smi", SnapshotArguments, token);
        return new OperationResult("GPU snapshot collected; N/A fields are unsupported or unavailable, not zero.");
    }
    private static async Task<XDocument> ReadXmlAsync(MachineWorkflowContext context, AgentEnvironment agent, CancellationToken token)
    {
        var result = await OperationOutput.CommandAsync(context, agent, "nvidia-smi", ["--query", "--xml-format"], token, traceOutput: false);
        using var text = new StringReader(result.Output);
        using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 4194304 });
        return XDocument.Load(reader);
    }
    public static async Task<OperationResult> OverviewAsync(MachineWorkflowContext context, EmptyInput input,
        AgentEnvironment agent, CancellationToken token)
    {
        var document = await ReadXmlAsync(context, agent, token);
        var root = document.Root;
        if (root is null || root.Name != "nvidia_smi_log")
            throw new InvalidDataException("nvidia-smi returned an unexpected GPU snapshot.");

        await context.TraceAsync($"NVIDIA driver {Field(root, "driver_version")} | " +
            $"CUDA compatibility {Field(root, "cuda_version")}");
        var gpus = root.Elements("gpu").ToArray();
        if (gpus.Length == 0)
            return new OperationResult("nvidia-smi reported no GPUs in the snapshot.", Complete: false);

        for (var index = 0; index < gpus.Length; index++)
            await TraceGpuAsync(context, $"GPU {index}", gpus[index], token);

        if (gpus.Any(gpu => Field(gpu, "product_name").StartsWith("NVIDIA GB10", StringComparison.Ordinal)))
            await TraceSharedMemoryAsync(context, OperationOutput.Host(agent), token);

        await TraceTopologyAsync(context, agent, token);
        return new OperationResult($"NVIDIA overview collected for {gpus.Length} GPU(s). " +
            "N/A fields are unavailable.");
    }

    private static async Task TraceGpuAsync(MachineWorkflowContext context, string label, XElement gpu,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var memory = gpu.Element("fb_memory_usage");
        var utilization = gpu.Element("utilization");
        var power = gpu.Element("gpu_power_readings") ?? gpu.Element("power_readings");
        var powerDraw = power?.Element("instant_power_draw") != null
            ? Field(power, "instant_power_draw")
            : Field(power, "power_draw");

        await context.TraceAsync($"{label} {Field(gpu, "product_name")} ({Field(gpu, "uuid")}) | " +
            $"GPU utilization {Field(utilization, "gpu_util")} | " +
            $"memory utilization {Field(utilization, "memory_util")} | " +
            $"temperature {Field(gpu.Element("temperature"), "gpu_temp")} | power {powerDraw}");
        await context.TraceAsync($"{label} framebuffer memory | used {Field(memory, "used")} | " +
            $"total {Field(memory, "total")} | free {Field(memory, "free")}");

        var processes = gpu.Descendants("process_info").ToArray();
        if (processes.Length == 0)
        {
            if (gpu.Element("processes")?.Value.Trim() is "" or "None")
                await context.TraceAsync($"{label} processes | none reported by the driver");
            else
                await context.WarningAsync($"{label} process list unavailable.");
        }

        foreach (var process in processes)
        {
            token.ThrowIfCancellationRequested();
            await context.TraceAsync($"{label} process | PID {Field(process, "pid")} | " +
                $"type {Field(process, "type")} | {Field(process, "process_name")} | " +
                $"GPU context memory {Field(process, "used_memory")}");
        }
    }

    private static async Task TraceSharedMemoryAsync(MachineWorkflowContext context, OperationHost host,
        CancellationToken token)
    {
        try
        {
            var memory = PerformanceOperations.ParseColonValues(await host.ReadAsync("/proc/meminfo", token, 65536));
            if (memory.TryGetValue("MemTotal", out var total) && memory.TryGetValue("MemAvailable", out var available))
            {
                const double bytesPerGib = 1L << 30;
                var totalGiB = PerformanceOperations.Kilobytes(total) / bytesPerGib;
                var availableGiB = PerformanceOperations.Kilobytes(available) / bytesPerGib;
                var usedGiB = Math.Max(0, totalGiB - availableGiB);
                var usage = FormattableString.Invariant(
                    $"used (estimated) {usedGiB:F1} GiB | total {totalGiB:F1} GiB | available {availableGiB:F1} GiB");
                await context.TraceAsync($"Host unified memory shared by CPU and GPU | {usage} (host-wide)");
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            await context.WarningAsync($"Host unified memory unavailable: {exception.Message}");
            return;
        }
        await context.WarningAsync("Host unified memory total or available value is missing.");
    }

    private static async Task TraceTopologyAsync(MachineWorkflowContext context, AgentEnvironment agent,
        CancellationToken token)
    {
        try
        {
            var result = await OperationOutput.CommandAsync(context, agent, "nvidia-smi", TopologyArguments, token,
                traceOutput: false);
            if (string.IsNullOrWhiteSpace(result.Output))
            {
                await context.WarningAsync("GPU topology unavailable: nvidia-smi returned no matrix.");
                return;
            }

            await context.TraceAsync("GPU topology (nvidia-smi topo -m)");
            using var reader = new StringReader(result.Output);
            while (reader.ReadLine() is { } line)
            {
                token.ThrowIfCancellationRequested();
                var formatted = FormatTopologyLine(line);
                if (formatted.Length > 0)
                    await OperationOutput.TraceTextAsync(context, formatted);
            }
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException ||
                                          exception is OperationCanceledException && !token.IsCancellationRequested)
        {
            await context.WarningAsync($"GPU topology unavailable: {exception.Message}");
        }
    }

    private static string FormatTopologyLine(string line)
    {
        const int tabWidth = 8;
        var cells = AnsiStyle().Replace(line, string.Empty).Split('\t');
        var result = new StringBuilder(cells[0]);
        foreach (var cell in cells.Skip(1))
        {
            result.Append(' ', tabWidth - result.Length % tabWidth);
            result.Append(cell);
        }
        return result.ToString().TrimEnd();
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial Regex AnsiStyle();

    private static string Field(XElement? element, string name) =>
        element?.Element(name)?.Value.Trim() is { Length: > 0 } value ? value : "N/A";

    public static async Task<OperationResult> ProcessesAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var document = await ReadXmlAsync(context, agent, token);
        var processes = (await PerformanceOperations.ReadProcessesAsync(OperationOutput.Host(agent), token)).ToDictionary(p => p.Pid);
        var rows = document.Descendants("gpu").SelectMany(g => g.Descendants("process_info").Select(p =>
        {
            var pid = int.TryParse(p.Element("pid")?.Value, out var value) ? value : -1; processes.TryGetValue(pid, out var owner);
            return new { Gpu = g.Element("uuid")?.Value, Pid = pid, Name = p.Element("process_name")?.Value, Type = p.Element("type")?.Value,
                UsedMemory = p.Element("used_memory")?.Value, owner?.ContainerId, owner?.Cgroup };
        })).ToArray();
        return await OperationOutput.ReportAsync(context, "GPU compute/graphics process evidence collected for driver-supported fields.", rows);
    }
    public static async Task<OperationResult> SampleAsync(MachineWorkflowContext context, SampleInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        // Test availability before waiting for the first interval.
        await OperationOutput.CommandAsync(context, agent, "nvidia-smi", SnapshotArguments, token, traceOutput: false);
        return await PerformanceOperations.SampleAsync(context, input, async sampling =>
        {
            var result = await OperationOutput.CommandAsync(context, agent, "nvidia-smi", SnapshotArguments, sampling, traceOutput: false);
            var processes = await OperationOutput.CommandAsync(context, agent, "nvidia-smi", ["--query-compute-apps=gpu_uuid,pid,process_name,used_memory", "--format=csv,noheader,nounits"], sampling, traceOutput: false);
            return new { Gpus = result.Output, ComputeProcesses = processes.Output, Units = "memory MiB, utilization %, temperature C, power W; N/A remains unavailable" };
        }, token);
    }
    public static async Task<OperationResult> ErrorsAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var document = await ReadXmlAsync(context, agent, token);
        var devices = document.Descendants("gpu").Select(g => new
        {
            Uuid = g.Element("uuid")?.Value,
            Ecc = g.Element("ecc_errors")?.ToString(SaveOptions.DisableFormatting),
            RetiredPages = g.Element("retired_pages")?.ToString(SaveOptions.DisableFormatting),
            Remapping = g.Element("row_remapper")?.ToString(SaveOptions.DisableFormatting),
            RecoveryAction = g.Element("gpu_recovery_action")?.Value
        }).ToArray();
        var args = SystemOperations.JournalArguments(null, null, 1000); args.Add("--dmesg");
        var journal = await OperationOutput.CommandAsync(context, agent, "journalctl", args, token, traceOutput: false);
        var events = journal.Output.Split('\n').Where(l => l.Contains("NVRM", StringComparison.OrdinalIgnoreCase) || l.Contains("Xid", StringComparison.OrdinalIgnoreCase) || l.Contains("nvidia", StringComparison.OrdinalIgnoreCase)).ToArray();
        return await OperationOutput.ReportAsync(context, "GPU hardware error counters and bounded kernel driver events collected.", new { Devices = devices, KernelEvents = events, MissingCounter = "Unsupported/unknown; not evidence of zero errors." });
    }
    public static async Task<OperationResult> TopologyAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        await OperationOutput.CommandAsync(context, agent, "nvidia-smi", TopologyArguments, token);
        return new OperationResult("Driver-reported GPU/PCIe/NVLink topology collected.");
    }
    public static async Task<OperationResult> DriverInfoAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var document = await ReadXmlAsync(context, agent, token);
        var host = OperationOutput.Host(agent);
        var version = host.Exists("/proc/driver/nvidia/version") ? await host.ReadAsync("/proc/driver/nvidia/version", token, 65536) : "unavailable";
        object runtime;
        try
        {
            var result = await OperationOutput.CommandAsync(context, agent, "docker", DockerOperations.Arguments("info", "--format", "{{json .Runtimes}}"), token, traceOutput: false);
            runtime = new { State = "Observed", Runtimes = result.Output };
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException) { runtime = new { State = "Unavailable" }; }
        return await OperationOutput.ReportAsync(context, "Driver, CUDA compatibility and available container runtime state collected.", new
        {
            DriverVersion = document.Root?.Element("driver_version")?.Value,
            CudaCompatibilityVersion = document.Root?.Element("cuda_version")?.Value,
            KernelModule = version, ContainerRuntime = runtime,
            Note = "The driver's CUDA compatibility version does not prove an application CUDA toolkit is installed."
        });
    }
}
