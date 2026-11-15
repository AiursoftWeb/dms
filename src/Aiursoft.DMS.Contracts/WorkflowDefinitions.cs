using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aiursoft.DMS.Contracts;

public enum WorkflowLocation { Cluster, Machine }
public sealed record EmptyInput;
public sealed partial record ServiceInput(string Service)
{
    public static bool ValidService(string? service) => service != null && ServiceName().IsMatch(service);
    public void Validate()
    {
        if (!ValidService(Service)) throw new ArgumentException("A concrete .service unit name is required.");
    }
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_.@-]{0,190}\.service\z")]
    private static partial Regex ServiceName();
}

internal static class WorkflowJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public abstract record WorkflowDefinition(string Name, RiskLevel? PublicRisk, string Version)
{
    public abstract WorkflowLocation Location { get; }
    public abstract bool RequiresTargets { get; }
    public abstract void Validate(string input);
    public abstract string Normalize(string input);
}

public abstract record WorkflowDefinition<TInput, TResult>(string Name, RiskLevel? PublicRisk, string Version,
    Action<TInput>? ValidateInput) : WorkflowDefinition(Name, PublicRisk, Version)
{
    public string Serialize(TInput input)
    {
        if (input == null) throw new ArgumentException("Workflow input cannot be null.");
        ValidateInput?.Invoke(input);
        return JsonSerializer.Serialize(input);
    }
    public TInput ReadInput(string input)
    {
        var value = JsonSerializer.Deserialize<TInput>(input, WorkflowJson.Options)
            ?? throw new InvalidDataException("Workflow input cannot be null.");
        ValidateInput?.Invoke(value);
        return value;
    }
    public override void Validate(string input) => ReadInput(input);
    public override string Normalize(string input) => Serialize(ReadInput(input));
    public TResult ReadResult(string result) => JsonSerializer.Deserialize<TResult>(result, WorkflowJson.Options)
        ?? throw new InvalidDataException("Workflow returned a null result.");
    public OperationRequest Request(Guid cluster, TInput input, string reason, IEnumerable<Guid>? machines = null,
        bool allMachines = false, Guid? requestId = null) => new()
    {
        RequestId = requestId ?? Guid.NewGuid(), ClusterId = cluster, Workflow = Name, Input = Serialize(input),
        MachineIds = machines?.ToList() ?? [], AllMachines = allMachines, Reason = reason
    };
}

public sealed record MachineWorkflowDefinition<TInput, TResult>(string Name, RiskLevel? PublicRisk = null,
    string Version = WorkflowNames.Version, Action<TInput>? ValidateInput = null)
    : WorkflowDefinition<TInput, TResult>(Name, PublicRisk, Version, ValidateInput)
{
    public override WorkflowLocation Location => WorkflowLocation.Machine;
    public override bool RequiresTargets => true;
}

public sealed record ClusterWorkflowDefinition<TInput, TResult>(string Name, RiskLevel? PublicRisk = null,
    string Version = WorkflowNames.Version, Action<TInput>? ValidateInput = null, bool SelectMachines = false)
    : WorkflowDefinition<TInput, TResult>(Name, PublicRisk, Version, ValidateInput)
{
    public override WorkflowLocation Location => WorkflowLocation.Cluster;
    public override bool RequiresTargets => SelectMachines;
}

// Definitions are shared contracts. Only explicit execution-factory registration makes them runnable.
public static class MachineWorkflows
{
    public static readonly MachineWorkflowDefinition<DockerPsInput, ProcessResult> DockerPs = new("machine.docker.ps", RiskLevel.Low, "1", input => input.Validate());
    public static readonly MachineWorkflowDefinition<DockerLogsInput, ProcessResult> DockerLogs = new("machine.docker.logs", RiskLevel.Low, "1", input => input.Validate());
    public static readonly MachineWorkflowDefinition<DockerExecInput, ProcessResult> DockerExec = new("machine.docker.exec", RiskLevel.VeryHigh, "1", input => input.Validate());
    public static readonly MachineWorkflowDefinition<DockerContainerInput, ProcessResult> DockerKill = new("machine.docker.kill", RiskLevel.High, "1", input => input.Validate());
    public static readonly MachineWorkflowDefinition<EmptyInput, DiskUsageResult> DiskUsage = new("machine.filesystem.disk-usage", Version: "1");
    public static readonly MachineWorkflowDefinition<PullerForceUpdateInput, PullerForceUpdateResult> PullerForceUpdate = new(WorkflowNames.PullerForceUpdate, RiskLevel.High, "2", input => input.Validate());
    public static readonly MachineWorkflowDefinition<EmptyInput, PerformanceResult> Performance = new(WorkflowNames.Performance, RiskLevel.Safe);
    public static readonly MachineWorkflowDefinition<ServiceInput, ProcessResult> Restart = new(WorkflowNames.Restart, RiskLevel.Medium, ValidateInput: input => input.Validate());
    public static readonly MachineWorkflowDefinition<ServiceInput, ProcessResult> ServiceStatus = new(WorkflowNames.ServiceStatus, RiskLevel.Low, ValidateInput: input => input.Validate());
    public static readonly MachineWorkflowDefinition<EmptyInput, ProcessResult> ListBlockDevices = new(WorkflowNames.ListBlockDevices, RiskLevel.Safe);
    public static readonly MachineWorkflowDefinition<EmptyInput, ProcessResult> DiskFree = new(WorkflowNames.DiskFree, RiskLevel.Safe);
    public static readonly MachineWorkflowDefinition<AgentUpgradeInput, AgentUpgradeResult> AgentUpgrade = new(WorkflowNames.AgentUpgrade, RiskLevel.High, "1", input => input.Validate());
    public static readonly MachineWorkflowDefinition<EmptyInput, AgentStatusResult> AgentStatus = new(WorkflowNames.AgentStatus, RiskLevel.Safe);
}

public static class ClusterWorkflows
{
    public static readonly ClusterWorkflowDefinition<EmptyInput, ClusterDiskUsageResult> DiskUsage = new("cluster.disk-usage", RiskLevel.Safe, "1", SelectMachines: true);
    public static readonly ClusterWorkflowDefinition<EmptyInput, List<MachineSummary>> GetMachines = new(WorkflowNames.ListMachines, RiskLevel.Safe);
}
