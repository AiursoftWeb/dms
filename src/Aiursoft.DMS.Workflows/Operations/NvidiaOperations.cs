using System.Xml;
using System.Xml.Linq;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public static class NvidiaOperations
{
    private static readonly string[] SnapshotArguments = ["--query-gpu=index,uuid,name,driver_version,memory.total,memory.used,utilization.gpu,utilization.memory,temperature.gpu,power.draw", "--format=csv,noheader,nounits"];
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
        await OperationOutput.CommandAsync(context, agent, "nvidia-smi", ["topo", "--matrix"], token);
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
