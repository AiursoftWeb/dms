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
        if (result.SshClosed == null && result.Observation is { BaselineReady: false } &&
            context.Environment.GetSecurityBaselinePolicy is { } readPolicy &&
            await IsAutomaticManagementEnabledAsync(context, readPolicy, token))
            result = await context.CallAsync(OperationsWorkflows.SshCheckLease,
                new SshLeaseCheckInput(CloseSshWhenNoLease: true) { ReportStatus = true }, token);
        return JsonSerializer.Serialize(new OperationResult(result.Summary));
    }

    private static async Task<bool> IsAutomaticManagementEnabledAsync(MachineWorkflowContext context,
        Func<CancellationToken, Task<SecurityBaselinePolicy?>> readPolicy, CancellationToken token)
    {
        try { return (await readPolicy(token))?.AutomaticallyManageSsh == true; }
        catch (Exception error) when (!token.IsCancellationRequested &&
            error is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            await context.WarningAsync("Could not read the Controller's automatic SSH policy. DMS will check again on the next schedule.");
            return false;
        }
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
