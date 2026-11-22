using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Workflows;

[WorkflowRisk(RiskLevel.High)]
[RunCron("0 3 * * *", ReconcileAfterInterruption = true)]
public sealed class ApplySecurityBaselineWorkflow : MachineWorkflow
{
    public override string Name => "machine.security.apply-baseline";
    public override string Version => "1";

    public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
    {
        var readPolicy = context.Environment.GetSecurityBaselinePolicy
            ?? throw new NotSupportedException("The installed Agent must obtain the scheduled security baseline from its Controller.");
        var policy = await readPolicy(token);
        if (policy == null)
            return JsonSerializer.Serialize(new OperationResult("This machine has no scheduled security baseline."));

        policy.Validate();
        await context.TraceAsync($"Applying scheduled security baseline version {policy.Version}.");
        var ssh = await context.CallAsync(OperationsWorkflows.SshCheckLease, new SshLeaseCheckInput(CloseSshWhenNoLease: true), token);
        await context.TraceAsync(ssh.Summary);

        if (policy.RequireFail2ban)
            await EnsurePackageAsync(context, "fail2ban", token);

        var ufwDeferred = policy.UfwEnabled is { } enabled &&
            !await ApplyUfwAsync(context, readPolicy, policy, enabled, token);
        return JsonSerializer.Serialize(await OperationOutput.ReportAsync(context,
            ufwDeferred ? "UFW application was deferred while an SSH lease was present." : "Scheduled security baseline applied.",
            new { policy.Version, Ssh = ssh, policy.RequireFail2ban, policy.UfwEnabled, UfwDeferred = ufwDeferred },
            complete: !ufwDeferred && ssh.SshClosed.HasValue));
    }

    private static async Task EnsurePackageAsync(MachineWorkflowContext context, string package, CancellationToken token)
    {
        var host = OperationOutput.Host(context.Environment);
        if (await IsInstalledAsync(host, package, token)) return;
        await context.CallAsync(OperationsWorkflows.AptInstall, new PackageInput([package]), token);
        if (!await IsInstalledAsync(host, package, token))
            throw new IOException($"Could not verify installation of {package}.");
    }

    private static async Task<bool> IsInstalledAsync(OperationHost host, string package, CancellationToken token)
    {
        var query = await host.RunAsync(new NativeCommand("dpkg-query", ["-W", "-f=${Status}", "--", package]), token);
        if (query.ExitCode is not (0 or 1))
            throw new IOException($"Could not query installation status of {package}.");
        return query.ExitCode == 0 && query.Output.Trim() == "install ok installed";
    }

    private static async Task<bool> ApplyUfwAsync(MachineWorkflowContext context,
        Func<CancellationToken, Task<SecurityBaselinePolicy?>> readPolicy, SecurityBaselinePolicy policy, bool enabled,
        CancellationToken token)
    {
        var host = OperationOutput.Host(context.Environment);
        var store = new SshLeaseStore(host);
        await using var guard = await store.LockAsync(token);
        if (store.ReadState()?.Lease != null) return false;
        if (!await IsInstalledAsync(host, "ufw", token))
        {
            if (!enabled) return true;
            throw new NotSupportedException("Install UFW and configure the host's firewall rules before enabling UFW through the security baseline.");
        }

        var previous = await UfwEnabledAsync(host, token);
        if (previous == enabled) return true;

        var action = enabled ? "enable" : "disable";
        await DisableSshWorkflow.RunAsync(host, "ufw", ["--dry-run", action], token);
        if (await readPolicy(token) != policy)
            throw new IOException("The Controller's applicable policy changed before firewall application.");
        try
        {
            await DisableSshWorkflow.RunAsync(host, "ufw", ["--force", action], token);
            if (await UfwEnabledAsync(host, token) != enabled)
                throw new IOException("UFW did not reach the requested state.");
            if (await readPolicy(token) != policy)
                throw new IOException("The Controller's applicable policy changed during firewall application.");
        }
        catch
        {
            try
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                if (await UfwEnabledAsync(host, recovery.Token) != previous)
                    await DisableSshWorkflow.RunAsync(host, "ufw", ["--force", previous ? "enable" : "disable"], recovery.Token);
                if (await UfwEnabledAsync(host, recovery.Token) != previous)
                    throw new IOException("UFW did not return to the original state.");
            }
            catch (Exception ex)
            {
                await context.TraceAsync($"The previous UFW state could not be verified: {ex.Message}", "error");
                throw new WorkflowNeedsVerificationException(context.Id);
            }
            await context.TraceAsync("The previous UFW state was restored after the failed change.", "warning");
            throw;
        }
        return true;
    }

    private static async Task<bool> UfwEnabledAsync(OperationHost host, CancellationToken token)
    {
        var result = await DisableSshWorkflow.RunAsync(host, "ufw", ["status"], token);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line == "Status: active") return true;
            if (line == "Status: inactive") return false;
        }
        throw new IOException("Could not determine whether UFW is active.");
    }
}
