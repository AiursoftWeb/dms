using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

[WorkflowRisk(RiskLevel.Medium)]
[RunCron("*/5 * * * *", RunOnStartup = true, ReconcileAfterInterruption = true)]
public sealed class ReconcileSshLeaseWorkflow : MachineWorkflow
{
    public override string Name => "machine.ssh.reconcile-lease";
    public override string Version => "1";

    public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
    {
        var result = await context.CallAsync(OperationsWorkflows.SshCheckLease, new SshLeaseCheckInput { ReportStatus = true }, token);
        await context.TraceAsync(result.Summary);
        return JsonSerializer.Serialize(new OperationResult(result.Summary));
    }
}

internal sealed class RecoverSshLeaseWorkflow(SshRecoveryInput input, AgentEnvironment environment)
    : MachineWorkflow<SshRecoveryInput, OperationResult>(OperationsWorkflows.SshRecover, input)
{
    public override Task<OperationResult> ExecuteAsync(MachineWorkflowContext context, SshRecoveryInput value, CancellationToken token) =>
        DisableSshWorkflow.RevokeAsync(context, environment, value.ExpectedLeaseId, token);
}

internal sealed class CheckSshLeaseWorkflow(SshLeaseCheckInput input, AgentEnvironment environment)
    : MachineWorkflow<SshLeaseCheckInput, SshLeaseCheckResult>(OperationsWorkflows.SshCheckLease, input)
{
    public override Task<SshLeaseCheckResult> ExecuteAsync(MachineWorkflowContext context, SshLeaseCheckInput value, CancellationToken token) =>
        DisableSshWorkflow.CheckLeaseAsync(context, environment, value, token);
}
