using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Workflows;

[WorkflowRisk(RiskLevel.Medium)]
[RunCron("*/5 * * * *", RunOnStartup = true, ReconcileAfterInterruption = true)]
public sealed class ReconcileSshLeaseWorkflow : MachineWorkflow
{
    public override string Name => "machine.ssh.reconcile-lease";
    public override string Version => "1";

    public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
    {
        var host = OperationOutput.Host(context.Environment);
        var state = new SshLeaseStore(host).ReadState();
        if (state?.Lease is not { } lease || !await SshLeaseStore.ShouldRecoverAsync(host, state, lease, token))
            return JsonSerializer.Serialize(new OperationResult("No SSH lease needs recovery."));

        await context.TraceAsync($"Reconciling SSH lease {lease.Id}. Healthy={state.Healthy}, expires_at={lease.ExpiresAtUtc:u}.");
        var result = await context.CallAsync(OperationsWorkflows.SshRecover, new SshRecoveryInput(lease.Id), token);
        return JsonSerializer.Serialize(result);
    }
}

internal sealed class RecoverSshLeaseWorkflow(SshRecoveryInput input, AgentEnvironment environment)
    : MachineWorkflow<SshRecoveryInput, OperationResult>(OperationsWorkflows.SshRecover, input)
{
    public override Task<OperationResult> ExecuteAsync(MachineWorkflowContext context, SshRecoveryInput value, CancellationToken token) =>
        DisableSshWorkflow.RevokeAsync(context, environment, value.ExpectedLeaseId, token);
}
