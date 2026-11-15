using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed class AgentUpgradeWorkflow(AgentUpgradeInput input, AgentEnvironment environment)
    : MachineWorkflow<AgentUpgradeInput, AgentUpgradeResult>(MachineWorkflows.AgentUpgrade, input)
{
    public override Task<AgentUpgradeResult> ExecuteAsync(MachineWorkflowContext context, AgentUpgradeInput value, CancellationToken token) =>
        (environment.Upgrade ?? throw new InvalidOperationException("Agent upgrades require an installed systemd Agent."))(context, value, token);
}
