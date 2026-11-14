using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed record EmptyInput;
public sealed record ServiceInput(string Service);
public sealed record AgentEnvironment(Guid InstallationId, string Version, int ActiveTasks, int PendingEvents);

internal static class WorkflowJson
{
    internal static readonly JsonSerializerOptions Options = new() { RespectRequiredConstructorParameters = true };
}

public abstract record WorkflowDefinition<TInput, TResult>(string Name)
{
    public string Serialize(TInput input) => JsonSerializer.Serialize(input);
    public TInput ReadInput(string input) => JsonSerializer.Deserialize<TInput>(input, WorkflowJson.Options)
        ?? throw new InvalidDataException("Workflow input cannot be null.");
    public TResult ReadResult(string result) => JsonSerializer.Deserialize<TResult>(result, WorkflowJson.Options)
        ?? throw new InvalidDataException("Workflow returned a null result.");
}

public sealed record MachineWorkflowDefinition<TInput, TResult>(string Name) : WorkflowDefinition<TInput, TResult>(Name);
public sealed record ClusterWorkflowDefinition<TInput, TResult>(string Name) : WorkflowDefinition<TInput, TResult>(Name);

// These are compiled entry points, never executable names supplied by a caller.
public static class MachineWorkflows
{
    public static readonly MachineWorkflowDefinition<EmptyInput, PerformanceResult> Performance = new(WorkflowNames.Performance);
    public static readonly MachineWorkflowDefinition<ServiceInput, ProcessResult> Restart = new(WorkflowNames.Restart);
    public static readonly MachineWorkflowDefinition<ServiceInput, ProcessResult> ServiceStatus = new(WorkflowNames.ServiceStatus);
    public static readonly MachineWorkflowDefinition<EmptyInput, ProcessResult> ListBlockDevices = new(WorkflowNames.ListBlockDevices);
    public static readonly MachineWorkflowDefinition<EmptyInput, ProcessResult> DiskFree = new(WorkflowNames.DiskFree);
    public static readonly MachineWorkflowDefinition<EmptyInput, AgentStatusResult> AgentStatus = new(WorkflowNames.AgentStatus);
}

public sealed class MachineWorkflowRegistry
{
    private readonly Dictionary<string, Func<string, AgentEnvironment, MachineWorkflow>> _definitions = new(StringComparer.Ordinal);

    public MachineWorkflowRegistry()
    {
        Register(MachineWorkflows.Performance, (_, _) => new PerformanceInfoWorkflow());
        Register(MachineWorkflows.Restart, (input, _) => new RestartServiceWorkflow(ValidateService(input)));
        Register(MachineWorkflows.ServiceStatus, (input, _) => new ServiceStatusWorkflow(ValidateService(input)));
        Register(MachineWorkflows.ListBlockDevices, (_, _) => new ListBlockDevicesWorkflow());
        Register(MachineWorkflows.DiskFree, (_, _) => new DiskFreeWorkflow());
        Register(MachineWorkflows.AgentStatus, (_, agent) => new AgentStatusWorkflow(agent.InstallationId, agent.Version, agent.ActiveTasks, agent.PendingEvents));
    }

    public void Register<TInput, TResult>(MachineWorkflowDefinition<TInput, TResult> definition,
        Func<TInput, AgentEnvironment, MachineWorkflow> factory) =>
        _definitions.Add(definition.Name, (input, environment) => new ValidatedWorkflow(factory(definition.ReadInput(input), environment),
            result => { definition.ReadResult(result); }));

    private sealed class ValidatedWorkflow(MachineWorkflow workflow, Action<string> validate) : MachineWorkflow
    {
        public override string Name => workflow.Name;
        public override string Input => workflow.Input;
        public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
        {
            var result = await workflow.ExecuteAsync(context, token);
            validate(result);
            return result;
        }
    }

    public bool Contains(string name) => _definitions.ContainsKey(name);
    public MachineWorkflow Create(string name, string input, AgentEnvironment environment) =>
        _definitions.TryGetValue(name, out var factory) ? factory(input, environment)
            : throw new InvalidDataException("Unsupported machine workflow definition.");

    public void Validate(string name, string input) => Create(name, input, new AgentEnvironment(Guid.Empty, "", 0, 0));

    public static string OperationInput(OperationRequest request) => request.Workflow is WorkflowNames.Restart or WorkflowNames.ServiceStatus
        ? JsonSerializer.Serialize(new ServiceInput(request.Service)) : JsonSerializer.Serialize(new EmptyInput());

    private static string ValidateService(ServiceInput input) => RestartServiceWorkflow.ValidService(input.Service)
        ? input.Service : throw new ArgumentException("A concrete .service unit name is required.");
}

public sealed class ChildWorkflowException(Guid workflowId, string definition, Guid? machineId, RunState state, string result)
    : Exception($"Child workflow {workflowId} ({definition}) ended as {state}: {result}")
{
    public Guid WorkflowId { get; } = workflowId;
    public string Definition { get; } = definition;
    public Guid? MachineId { get; } = machineId;
    public RunState State { get; } = state;
}

public sealed class WorkflowNeedsVerificationException(Guid workflowId)
    : Exception($"Workflow {workflowId} needs verification; its execution outcome is unknown.")
{
    public Guid WorkflowId { get; } = workflowId;
}
