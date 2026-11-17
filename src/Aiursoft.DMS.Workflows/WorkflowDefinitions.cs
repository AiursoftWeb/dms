using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed record AgentEnvironment(Guid InstallationId, string Version, int ActiveTasks, int PendingEvents,
    Func<MachineWorkflowContext, AgentUpgradeInput, CancellationToken, Task<AgentUpgradeResult>>? Upgrade = null);

public sealed class MachineWorkflowRegistry
{
    private readonly Dictionary<string, (WorkflowDefinition Definition, Func<string, AgentEnvironment, MachineWorkflow> Factory)> _definitions = new(StringComparer.Ordinal);

    public MachineWorkflowRegistry()
    {
        Register(MachineWorkflows.DockerPs, (input, _) => new DockerPsWorkflow(input));
        Register(MachineWorkflows.DockerLogs, (input, _) => new DockerLogsWorkflow(input));
        Register(MachineWorkflows.DockerExec, (input, _) => new DockerExecWorkflow(input));
        Register(MachineWorkflows.DockerKill, (input, _) => new DockerKillWorkflow(input));
        Register(MachineWorkflows.DiskUsage, (input, _) => new MachineDiskUsageWorkflow(input));
        Register(MachineWorkflows.PullerForceUpdate, (input, _) => new BoxPullerForceUpdateWorkflow(input));
        Register(MachineWorkflows.BoxInspect, (_, _) => new BoxInspectWorkflow());
        Register(MachineWorkflows.BoxConfigureGit, (input, _) => new BoxConfigureGitWorkflow(input));
        Register(MachineWorkflows.DirectoryUsage, (input, _) => new DirectoryUsageWorkflow(input));
        Register(MachineWorkflows.RemovePath, (input, _) => new RemovePathWorkflow(input));
        Register(MachineWorkflows.DockerDiskUsage, (_, _) => new DockerDiskUsageWorkflow());
        Register(MachineWorkflows.BoxDeploy, (input, _) => new BoxDeployWorkflow(input));
        Register(MachineWorkflows.Performance, (_, _) => new PerformanceInfoWorkflow());
        Register(MachineWorkflows.Restart, (input, _) => new RestartServiceWorkflow(input.Service));
        Register(MachineWorkflows.ServiceStatus, (input, _) => new ServiceStatusWorkflow(input.Service));
        Register(MachineWorkflows.ListBlockDevices, (_, _) => new ListBlockDevicesWorkflow());
        Register(MachineWorkflows.DiskFree, (_, _) => new DiskFreeWorkflow());
        Register(MachineWorkflows.AgentUpgrade, (input, agent) => new AgentUpgradeWorkflow(input, agent));
        Register(MachineWorkflows.AgentStatus, (_, agent) => new AgentStatusWorkflow(agent.InstallationId, agent.Version, agent.ActiveTasks, agent.PendingEvents));
    }

    public void Register<TInput, TResult>(MachineWorkflowDefinition<TInput, TResult> definition,
        Func<TInput, AgentEnvironment, MachineWorkflow> factory)
    {
        if (!Enum.IsDefined(definition.Risk)) throw new ArgumentOutOfRangeException(nameof(definition));
        _definitions.Add(definition.Name, (definition, (input, environment) => new ValidatedWorkflow(definition,
            factory(definition.ReadInput(input), environment), result => { definition.ReadResult(result); })));
    }

    private sealed class ValidatedWorkflow(WorkflowDefinition definition, MachineWorkflow workflow, Action<string> validate) : MachineWorkflow
    {
        public override string Name => definition.Name;
        public override string Version => definition.Version;
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
        _definitions.TryGetValue(name, out var factory) ? factory.Factory(input, environment)
            : throw new InvalidDataException("Unsupported machine workflow definition.");

    public void Validate(string name, string input) => Create(name, input, new AgentEnvironment(Guid.Empty, "", 0, 0));

    public WorkflowDefinition? FindDefinition(string name) => _definitions.TryGetValue(name, out var entry) ? entry.Definition : null;

    public IEnumerable<WorkflowDefinition> Definitions => _definitions.Values.Select(entry => entry.Definition);

}

public sealed class ChildWorkflowException(Guid workflowId, string definition, Guid? machineId, RunState state, string result)
    : Exception($"Child workflow {workflowId} ({definition}) ended as {state}: {result}")
{
    public Guid WorkflowId { get; } = workflowId;
    public string Definition { get; } = definition;
    public Guid? MachineId { get; } = machineId;
    public RunState State { get; } = state;
    public string Result { get; } = result;
}

public sealed class WorkflowNeedsVerificationException(Guid workflowId)
    : Exception($"Workflow {workflowId} needs verification; its execution outcome is unknown.")
{
    public Guid WorkflowId { get; } = workflowId;
}
