using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Workflows;

public sealed class DisableSshWorkflow(EmptyInput input, AgentEnvironment environment)
    : MachineWorkflow<EmptyInput, OperationResult>(OperationsWorkflows.SshDisable, input)
{
    private static readonly string[] SshUnits = ["ssh.socket", "ssh.service"];

    public override Task<OperationResult> ExecuteAsync(MachineWorkflowContext context, EmptyInput value, CancellationToken token) =>
        RevokeAsync(context, environment, null, token);

    internal static async Task<OperationResult> RevokeAsync(MachineWorkflowContext context, AgentEnvironment environment,
        Guid? expectedLeaseId, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        token = deadline.Token;
        var host = OperationOutput.Host(environment);
        await RequireRootAsync(host, token);

        var store = new SshLeaseStore(host);
        await using var guard = await store.LockAsync(token);
        var previous = store.ReadRevocation(context.Id);
        if (previous is { Complete: true })
            return await ReportAsync(context, previous, replayed: true);
        if (previous != null)
            throw new WorkflowNeedsVerificationException(context.Id);

        var state = store.ReadState();
        var lease = state?.Lease;
        lease?.Validate();
        if (expectedLeaseId != null && (lease?.Id != expectedLeaseId ||
            !await SshLeaseStore.ShouldRecoverAsync(host, state!, lease, token)))
        {
            await context.TraceAsync($"SSH recovery skipped. Lease {expectedLeaseId} is no longer due for recovery.");
            return new OperationResult("The expected SSH lease no longer requires recovery.");
        }
        var revocation = new SshRevocation(lease?.Id, Complete: false);
        store.SaveRevocation(context.Id, revocation);
        store.SaveState(new SshLeaseState(lease, Healthy: false));

        var step = "stop SSH service and socket";
        try
        {
            await context.TraceAsync($"Disabling SSH. LeaseId={lease?.Id}, workflow_id={context.Id}, previous_baseline_healthy={state?.Healthy}.");
            if (lease?.BootId != null)
            {
                step = "stop the temporary SSH service";
                await StopLeaseUnitAsync(host, lease, token);
            }
            step = "stop SSH service and socket";
            await RunAsync(host, "systemctl", ["mask", "--now", ..SshUnits], token);
            await VerifyUnitsAsync(host, token);

            if (lease != null)
            {
                step = "verify the lease owns its temporary account";
                var accountExists = await VerifyAccountAsync(host, lease, token);
                step = "terminate lease sessions and processes";
                await TerminateSessionsAsync(host, lease, accountExists, token);
                step = "remove lease firewall rules";
                if (lease.Ipv4Rule) await RemoveFirewallRuleAsync(host, "iptables", lease, token);
                if (lease.Ipv6Rule) await RemoveFirewallRuleAsync(host, "ip6tables", lease, token);
                step = "remove lease sudo authorization, account and home";
                await RemoveAccountAsync(host, lease, accountExists, token);
                if (lease.BootId != null)
                {
                    step = "remove the temporary SSH configuration and public key";
                    File.Delete(host.FilePath(lease.ConfigPath));
                    File.Delete(host.FilePath(lease.AuthorizedKeyPath));
                }
            }

            step = "verify SSH listeners and established connections";
            await VerifyUnitsAsync(host, token);
            await VerifyConnectionsAsync(host, token);
            token.ThrowIfCancellationRequested();
            step = "persist the verified state";
            store.SaveState(new SshLeaseState(null, Healthy: true));
            revocation = revocation with { Complete = true };
            store.SaveRevocation(context.Id, revocation);
        }
        catch (Exception exception)
        {
            await context.TraceAsync($"Could not {step}. The SSH baseline requires verification. {exception.Message}", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }

        return await ReportAsync(context, revocation, replayed: false);
    }

    private static Task<OperationResult> ReportAsync(MachineWorkflowContext context, SshRevocation revocation, bool replayed) =>
        OperationOutput.ReportAsync(context, replayed
            ? "Returning the recorded SSH disable result."
            : "SSH service and socket are disabled, and lease resources have been removed.",
            new { revocation.LeaseId, Replayed = replayed });

    internal static async Task RequireRootAsync(OperationHost host, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || (await RunAsync(host, "id", ["-u"], token)).Output.Trim() != "0")
            throw new NotSupportedException("SSH lease management requires a Linux Agent running as root.");
    }

    internal static async Task StopLeaseUnitAsync(OperationHost host, SshLease lease, CancellationToken token)
    {
        var state = await RunAsync(host, "systemctl", ["show", "--property=LoadState,ActiveState", lease.Unit], token, allowNoMatches: true);
        var properties = state.Output.Split('\n', StringSplitOptions.TrimEntries);
        if (properties.Contains("LoadState=not-found") && properties.Contains("ActiveState=inactive")) return;
        await RunAsync(host, "systemctl", ["stop", lease.Unit], token);
    }

    internal static async Task VerifyUnitsAsync(OperationHost host, CancellationToken token)
    {
        foreach (var unit in SshUnits)
        {
            var state = await RunAsync(host, "systemctl",
                ["show", "--property=LoadState,ActiveState", unit], token);
            var properties = state.Output.Split('\n', StringSplitOptions.TrimEntries);
            if (!properties.Contains("LoadState=masked") ||
                (!properties.Contains("ActiveState=inactive") && !properties.Contains("ActiveState=failed")))
                throw new IOException($"{unit} is not masked and stopped. {state.Output.Trim()}");
        }
    }

    internal static async Task VerifyConnectionsAsync(OperationHost host, CancellationToken token)
    {
        foreach (var args in new[] { new[] { "-Hltnp" }, ["-Htnp", "state", "established"] })
        {
            var sockets = await RunAsync(host, "ss", args, token);
            if (sockets.Output.Contains("(\"sshd\",", StringComparison.Ordinal) ||
                sockets.Output.Contains("(\"sshd-session\",", StringComparison.Ordinal) ||
                sockets.Output.Contains("(\"sshd-auth\",", StringComparison.Ordinal))
                throw new IOException("An OpenSSH listener or established connection remains.");
        }
    }

    private static async Task<bool> VerifyAccountAsync(OperationHost host, SshLease lease, CancellationToken token)
    {
        var passwd = await host.ReadAsync("/etc/passwd", token);
        var matching = passwd.Split('\n').Select(line => line.Split(':'))
            .Where(fields => fields.Length == 7 && (fields[0] == lease.User || fields[2] == lease.UserId))
            .ToArray();
        if (matching.Length == 0) return false;
        if (matching.Length != 1 || matching[0][0] != lease.User || matching[0][2] != lease.UserId || matching[0][5] != lease.Home)
            throw new IOException("The temporary account name, UID or home no longer matches the recorded lease.");
        return true;
    }

    private static async Task TerminateSessionsAsync(OperationHost host, SshLease lease, bool accountExists, CancellationToken token)
    {
        var sessions = await RunAsync(host, "loginctl", ["list-sessions", "--no-legend", "--no-pager"], token);
        if (!accountExists)
        {
            if (HasSessions(sessions.Output, lease.UserId))
                throw new IOException("The recorded UID has sessions but its lease account no longer exists.");
            return;
        }
        if (HasSessions(sessions.Output, lease.UserId))
            await RunAsync(host, "loginctl", ["terminate-user", lease.UserId], token);
        await RunAsync(host, "pkill", ["--signal", "KILL", "--uid", lease.UserId], token, allowNoMatches: true);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            sessions = await RunAsync(host, "loginctl", ["list-sessions", "--no-legend", "--no-pager"], token);
            if (!HasSessions(sessions.Output, lease.UserId)) return;
            await Task.Delay(100, token);
        }
        throw new IOException("The lease still has a login session.");
    }

    private static bool HasSessions(string output, string uid) => output.Split('\n')
        .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Any(columns => columns.Length > 1 && columns[1] == uid);

    private static async Task RemoveFirewallRuleAsync(OperationHost host, string tool, SshLease lease, CancellationToken token)
    {
        string[] rule = ["INPUT", "-p", "tcp", "-m", "tcp", "--dport", lease.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-m", "comment", "--comment", lease.FirewallTag, "-j", "ACCEPT"];
        var expected = "-A " + string.Join(' ', rule);
        var listed = await RunAsync(host, tool, ["-w", "3", "-S", "INPUT"], token);
        var owned = listed.Output.Split('\n').Select(line => line.Replace("\"", "", StringComparison.Ordinal))
            .Where(line => line.Split(' ').Contains(lease.FirewallTag)).ToArray();
        if (owned.Any(line => line != expected))
            throw new IOException($"The {tool} rule carrying the lease tag does not match its recorded scope.");
        foreach (var _ in owned)
            await RunAsync(host, tool, ["-w", "3", "-D", ..rule], token);
        var verified = await RunAsync(host, tool, ["-w", "3", "-S", "INPUT"], token);
        if (verified.Output.Contains(lease.FirewallTag, StringComparison.Ordinal))
            throw new IOException($"The lease firewall rule is still present in {tool}.");
    }

    private static async Task RemoveAccountAsync(OperationHost host, SshLease lease, bool accountExists, CancellationToken token)
    {
        var home = new DirectoryInfo(host.FilePath(lease.Home));
        var mounts = await host.ReadAsync("/proc/self/mountinfo", token);
        if (home.LinkTarget != null || home.Parent?.LinkTarget != null || mounts.Split('\n').Any(line =>
                line.Split(' ') is [_, _, _, _, var path, ..] &&
                (path == lease.Home || path.StartsWith(lease.Home + "/", StringComparison.Ordinal))))
            throw new IOException("The lease home is a symbolic link or contains a mount.");
        if (File.Exists(home.FullName))
            throw new IOException("The lease home is not a directory.");

        var sudo = new FileInfo(host.FilePath("/etc/sudoers.d/" + lease.FirewallTag));
        if (sudo.Exists || sudo.LinkTarget != null) sudo.Delete();
        if (accountExists)
            await RunAsync(host, "userdel", ["--", lease.User], token);
        if (home.Exists) home.Delete(recursive: true);
        if (await VerifyAccountAsync(host, lease, token) || Directory.Exists(home.FullName) ||
            File.Exists(sudo.FullName))
            throw new IOException("An account, home or sudo grant belonging to the lease remains.");
    }

    internal static async Task<NativeResult> RunAsync(OperationHost host, string tool, string[] args,
        CancellationToken token, bool allowNoMatches = false)
    {
        var result = await host.RunAsync(new NativeCommand(tool, args, TimeoutSeconds: 20), token);
        if (result.ExitCode != 0 && !(allowNoMatches && result.ExitCode == 1))
            throw new IOException($"{tool} exited with code {result.ExitCode}. {result.Error[..Math.Min(1000, result.Error.Length)].Trim()}");
        return result;
    }
}
