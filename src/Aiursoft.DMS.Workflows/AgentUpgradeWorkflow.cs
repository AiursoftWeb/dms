using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed class AgentUpgradeWorkflow(AgentUpgradeInput input, AgentEnvironment environment)
    : MachineWorkflow<AgentUpgradeInput, AgentUpgradeResult>(MachineWorkflows.AgentUpgrade, input)
{
    public override Task<AgentUpgradeResult> ExecuteAsync(MachineWorkflowContext context, AgentUpgradeInput value, CancellationToken token) =>
        (environment.Upgrade ?? throw new InvalidOperationException("Agent upgrades require an installed systemd Agent."))(context, value, token);
}

[WorkflowRisk(RiskLevel.High)]
[RunCron("0 */8 * * *")]
public sealed class ScheduledAgentUpgradeWorkflow : MachineWorkflow
{
    public const string DefinitionName = "machine.agent.scheduled-upgrade";
    public const string DefinitionVersion = "1";

    public override string Name => DefinitionName;
    public override string Version => DefinitionVersion;

    public override Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token) =>
        context.Environment.ScheduledUpgrade is { } upgrade
            ? upgrade(context, token)
            : Task.FromResult(JsonSerializer.Serialize(new OperationResult("Scheduled upgrades require an installed Agent.")));
}
