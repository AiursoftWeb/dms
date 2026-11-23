using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aiursoft.DMS.Contracts;

public enum WorkflowLocation { Cluster, Machine }
public sealed record EmptyInput;
public sealed record EmptyResult;
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
    public bool SingleMachineOnly { get; init; }
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
    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<ProcessInput, ProcessResult> Process = new(WorkflowNames.Process, WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(Process)));
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<DockerPsInput, ProcessResult> DockerPs = new("machine.docker.ps", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(DockerPs)), "1", input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<DockerLogsInput, ProcessResult> DockerLogs = new("machine.docker.logs", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(DockerLogs)), "1", input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<DockerExecInput, ProcessResult> DockerExec = new("machine.docker.exec", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(DockerExec)), "1", input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<DockerContainerInput, ProcessResult> DockerKill = new("machine.docker.kill", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(DockerKill)), "1", input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly MachineWorkflowDefinition<EmptyInput, DiskUsageResult> DiskUsage = new("machine.filesystem.disk-usage", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(DiskUsage)), Version: "1");
    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<PullerForceUpdateInput, PullerForceUpdateResult> PullerForceUpdate = new(WorkflowNames.PullerForceUpdate, WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(PullerForceUpdate)), "2", input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly MachineWorkflowDefinition<EmptyInput, BoxInspectionResult> BoxInspect = new("machine.box.inspect", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(BoxInspect)), "1", PublicEntry: true);
    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<BoxGitInput, BoxInspectionResult> BoxConfigureGit = new("machine.box.configure-git", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(BoxConfigureGit)), "1", input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<DirectoryUsageInput, ProcessResult> DirectoryUsage = new("machine.filesystem.du", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(DirectoryUsage)), "1", input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<RemovePathInput, ProcessResult> RemovePath = new("machine.filesystem.rm", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(RemovePath)), "1", input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, ProcessResult> DockerDiskUsage = new("machine.docker.system.df", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(DockerDiskUsage)), "1", PublicEntry: true);
    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<BoxDeployInput, BoxDeployResult> BoxDeploy = new("machine.box.deploy", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(BoxDeploy)), "1", input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, BoxSecretsResult> BoxListSecret =
        new("machine.box.list-secret", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(BoxListSecret)), "1", PublicEntry: true) { SingleMachineOnly = true };
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<BoxSecretInput, BoxSecretSummary> BoxGetSecretSummary =
        new("machine.box.get-secret-summary", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(BoxGetSecretSummary)), "1", input => input.Validate(), PublicEntry: true) { SingleMachineOnly = true };
    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<BoxSecretInput, BoxSecretResult> BoxSetSecret =
        new("machine.box.set-secret", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(BoxSetSecret)), "1", input => input.Validate(), PublicEntry: true) { SingleMachineOnly = true };
    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<BoxSecretInput, BoxSecretResult> BoxGetSecretRaw =
        new("machine.box.get-secret-raw", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(BoxGetSecretRaw)), "1", input => input.Validate(), PublicEntry: true) { SingleMachineOnly = true };
    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly MachineWorkflowDefinition<EmptyInput, PerformanceResult> Performance = new(WorkflowNames.Performance, WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(Performance)), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<ServiceInput, ProcessResult> Restart = new(WorkflowNames.Restart, WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(Restart)), ValidateInput: input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<ServiceInput, ProcessResult> ServiceStatus = new(WorkflowNames.ServiceStatus, WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(ServiceStatus)), ValidateInput: input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly MachineWorkflowDefinition<EmptyInput, ProcessResult> ListBlockDevices = new(WorkflowNames.ListBlockDevices, WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(ListBlockDevices)), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly MachineWorkflowDefinition<EmptyInput, ProcessResult> DiskFree = new(WorkflowNames.DiskFree, WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(DiskFree)), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<AgentUpgradeInput, AgentUpgradeResult> AgentUpgrade = new(WorkflowNames.AgentUpgrade, WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(AgentUpgrade)), "1", input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<FrpsDeployInput, FrpsDeployResult> FrpsDeploy =
        new("machine.frps.deploy", WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(FrpsDeploy)),
            "1", input => input.Validate(), PublicEntry: true) { SingleMachineOnly = true };
    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly MachineWorkflowDefinition<EmptyInput, AgentStatusResult> AgentStatus = new(WorkflowNames.AgentStatus, WorkflowMetadata.Risk(typeof(MachineWorkflows), nameof(AgentStatus)), PublicEntry: true);
}

public static class ClusterWorkflows
{
    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly ClusterWorkflowDefinition<EmptyInput, ClusterDiskUsageResult> DiskUsage = new("cluster.disk-usage", WorkflowMetadata.Risk(typeof(ClusterWorkflows), nameof(DiskUsage)), "1", SelectMachines: true, PublicEntry: true);
    [WorkflowRisk(RiskLevel.Open)]
    public static readonly ClusterWorkflowDefinition<EmptyInput, List<MachineSummary>> GetMachines = new(WorkflowNames.ListMachines, WorkflowMetadata.Risk(typeof(ClusterWorkflows), nameof(GetMachines)), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Open)]
    public static readonly ClusterWorkflowDefinition<ArtifactListInput, List<ArtifactListItem>> ListArtifacts =
        new(WorkflowNames.ListArtifacts, WorkflowMetadata.Risk(typeof(ClusterWorkflows), nameof(ListArtifacts)),
            ValidateInput: input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly ClusterWorkflowDefinition<AddMachineInput, AddMachineResult> AddMachine =
        new(WorkflowNames.AddMachine, WorkflowMetadata.Risk(typeof(ClusterWorkflows), nameof(AddMachine)),
            ValidateInput: input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly ClusterWorkflowDefinition<CancelWorkflowInput, CancelWorkflowResult> Cancel =
        new(WorkflowNames.Kill, WorkflowMetadata.Risk(typeof(ClusterWorkflows), nameof(Cancel)),
            ValidateInput: input => input.Validate(), PublicEntry: true);
}

public sealed record AddMachineInput(string Name)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100)
            throw new ArgumentException("Machine name must contain 1 to 100 characters.");
    }
}

public sealed record AddMachineResult(Guid Id, string Name);

public sealed record CancelWorkflowInput(Guid TargetWorkflowId)
{
    public void Validate()
    {
        if (TargetWorkflowId == Guid.Empty)
            throw new ArgumentException("A target workflow ID is required.");
    }
}

public sealed record CancelWorkflowResult(Guid TargetWorkflowId, bool CancellationRequested, bool SideEffectsRolledBack);
