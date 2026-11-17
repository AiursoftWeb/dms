using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aiursoft.DMS.Contracts;

public enum WorkflowLocation { Cluster, Machine }
public sealed record EmptyInput;
public sealed record ProcessInput(string Executable, IReadOnlyList<string> Arguments, bool CaptureOutput);
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

public abstract record WorkflowDefinition(string Name, RiskLevel Risk, string Version, bool PublicEntry)
{
    public abstract WorkflowLocation Location { get; }
    public abstract bool RequiresTargets { get; }
    public abstract void Validate(string input);
    public abstract string Normalize(string input);
    public virtual IEnumerable<ArtifactReference> Artifacts(string input) => [];
    public virtual bool RetainInputArtifactsForAudit => false;
}

public abstract record WorkflowDefinition<TInput, TResult>(string Name, RiskLevel Risk, string Version,
    Action<TInput>? ValidateInput, bool PublicEntry) : WorkflowDefinition(Name, Risk, Version, PublicEntry)
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
    public override IEnumerable<ArtifactReference> Artifacts(string input) => ReadInput(input) is IArtifactOperationInput value ? [value.Artifact] : [];
    public override bool RetainInputArtifactsForAudit => typeof(IAuditTextInput).IsAssignableFrom(typeof(TInput));
    public TResult ReadResult(string result) => JsonSerializer.Deserialize<TResult>(result, WorkflowJson.Options)
        ?? throw new InvalidDataException("Workflow returned a null result.");
    public OperationRequest Request(TInput input, string reason, IEnumerable<Guid>? machines = null,
        bool allMachines = false, Guid? requestId = null) => new()
    {
        RequestId = requestId ?? Guid.NewGuid(), Workflow = Name, Input = Serialize(input),
        MachineIds = machines?.ToList() ?? [], AllMachines = allMachines, Reason = reason
    };
}

public sealed record MachineWorkflowDefinition<TInput, TResult>(string Name, RiskLevel Risk,
    string Version = WorkflowNames.Version, Action<TInput>? ValidateInput = null, bool PublicEntry = false)
    : WorkflowDefinition<TInput, TResult>(Name, Risk, Version, ValidateInput, PublicEntry)
{
    public override WorkflowLocation Location => WorkflowLocation.Machine;
    public override bool RequiresTargets => true;
}

public sealed record ClusterWorkflowDefinition<TInput, TResult>(string Name, RiskLevel Risk,
    string Version = WorkflowNames.Version, Action<TInput>? ValidateInput = null, bool SelectMachines = false, bool PublicEntry = false)
    : WorkflowDefinition<TInput, TResult>(Name, Risk, Version, ValidateInput, PublicEntry)
{
    public override WorkflowLocation Location => WorkflowLocation.Cluster;
    public override bool RequiresTargets => SelectMachines;
}

// Definitions are shared contracts. Only explicit execution-factory registration makes them runnable.
public static class MachineWorkflows
{
    // Metadata for the compiled local helper; deliberately absent from remote dispatch registration.
    public static readonly MachineWorkflowDefinition<ProcessInput, ProcessResult> Process = new(WorkflowNames.Process, RiskLevel.VeryHigh);
    public static readonly MachineWorkflowDefinition<DockerPsInput, ProcessResult> DockerPs = new("machine.docker.ps", RiskLevel.Low, "1", input => input.Validate(), PublicEntry: true);
    public static readonly MachineWorkflowDefinition<DockerLogsInput, ProcessResult> DockerLogs = new("machine.docker.logs", RiskLevel.Low, "1", input => input.Validate(), PublicEntry: true);
    public static readonly MachineWorkflowDefinition<DockerExecInput, ProcessResult> DockerExec = new("machine.docker.exec", RiskLevel.VeryHigh, "1", input => input.Validate(), PublicEntry: true);
    public static readonly MachineWorkflowDefinition<DockerContainerInput, ProcessResult> DockerKill = new("machine.docker.kill", RiskLevel.High, "1", input => input.Validate(), PublicEntry: true);
    public static readonly MachineWorkflowDefinition<EmptyInput, DiskUsageResult> DiskUsage = new("machine.filesystem.disk-usage", RiskLevel.Safe, Version: "1");
    public static readonly MachineWorkflowDefinition<PullerForceUpdateInput, PullerForceUpdateResult> PullerForceUpdate = new(WorkflowNames.PullerForceUpdate, RiskLevel.High, "2", input => input.Validate(), PublicEntry: true);
    public static readonly MachineWorkflowDefinition<EmptyInput, BoxInspectionResult> BoxInspect = new("machine.box.inspect", RiskLevel.Safe, "1", PublicEntry: true);
    public static readonly MachineWorkflowDefinition<BoxGitInput, BoxInspectionResult> BoxConfigureGit = new("machine.box.configure-git", RiskLevel.High, "1", input => input.Validate(), PublicEntry: true);
    public static readonly MachineWorkflowDefinition<DirectoryUsageInput, ProcessResult> DirectoryUsage = new("machine.filesystem.du", RiskLevel.Low, "1", input => input.Validate(), PublicEntry: true);
    public static readonly MachineWorkflowDefinition<RemovePathInput, ProcessResult> RemovePath = new("machine.filesystem.rm", RiskLevel.High, "1", input => input.Validate(), PublicEntry: true);
    public static readonly MachineWorkflowDefinition<EmptyInput, ProcessResult> DockerDiskUsage = new("machine.docker.system.df", RiskLevel.Low, "1", PublicEntry: true);
    public static readonly MachineWorkflowDefinition<BoxDeployInput, BoxDeployResult> BoxDeploy = new("machine.box.deploy", RiskLevel.High, "1", input => input.Validate(), PublicEntry: true);
    public static readonly MachineWorkflowDefinition<EmptyInput, PerformanceResult> Performance = new(WorkflowNames.Performance, RiskLevel.Safe, PublicEntry: true);
    public static readonly MachineWorkflowDefinition<ServiceInput, ProcessResult> Restart = new(WorkflowNames.Restart, RiskLevel.Medium, ValidateInput: input => input.Validate(), PublicEntry: true);
    public static readonly MachineWorkflowDefinition<ServiceInput, ProcessResult> ServiceStatus = new(WorkflowNames.ServiceStatus, RiskLevel.Low, ValidateInput: input => input.Validate(), PublicEntry: true);
    public static readonly MachineWorkflowDefinition<EmptyInput, ProcessResult> ListBlockDevices = new(WorkflowNames.ListBlockDevices, RiskLevel.Safe, PublicEntry: true);
    public static readonly MachineWorkflowDefinition<EmptyInput, ProcessResult> DiskFree = new(WorkflowNames.DiskFree, RiskLevel.Safe, PublicEntry: true);
    public static readonly MachineWorkflowDefinition<AgentUpgradeInput, AgentUpgradeResult> AgentUpgrade = new(WorkflowNames.AgentUpgrade, RiskLevel.High, "1", input => input.Validate(), PublicEntry: true);
    public static readonly MachineWorkflowDefinition<EmptyInput, AgentStatusResult> AgentStatus = new(WorkflowNames.AgentStatus, RiskLevel.Safe, PublicEntry: true);
}

public static class ClusterWorkflows
{
    public static readonly ClusterWorkflowDefinition<EmptyInput, ClusterDiskUsageResult> DiskUsage = new("cluster.disk-usage", RiskLevel.Safe, "1", SelectMachines: true, PublicEntry: true);
    public static readonly ClusterWorkflowDefinition<EmptyInput, List<MachineSummary>> GetMachines = new(WorkflowNames.ListMachines, RiskLevel.Safe, PublicEntry: true);
}
