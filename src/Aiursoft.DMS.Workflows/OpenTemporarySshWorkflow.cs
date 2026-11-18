using System.Globalization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows.Operations;
using static Aiursoft.DMS.Workflows.DisableSshWorkflow;

namespace Aiursoft.DMS.Workflows;

public sealed class OpenTemporarySshWorkflow(SshOpenInput input, AgentEnvironment environment)
    : MachineWorkflow<SshOpenInput, OperationResult>(OperationsWorkflows.SshOpen, input)
{
    private const int SshPort = 22;

    public override async Task<OperationResult> ExecuteAsync(MachineWorkflowContext context, SshOpenInput value, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        token = deadline.Token;
        var host = OperationOutput.Host(environment);
        await RequireRootAsync(host, token);
        var store = new SshLeaseStore(host);
        await using var guard = await store.LockAsync(token);

        var receipt = store.ReadOpening(context.Id);
        if (receipt?.ExpiresAtUtc is { } previousExpiry)
            return await ReportAsync(context, value, previousExpiry, replayed: true);
        if (receipt != null)
            throw new WorkflowNeedsVerificationException(context.Id);

        var state = store.ReadState();
        if (state?.Lease is { } current)
            throw new InvalidOperationException($"Lease {current.Id} belongs to {current.RequesterId} and expires at {current.ExpiresAtUtc:u}. Close it before opening another lease.");
        if (state is not { Healthy: true })
            throw new InvalidOperationException("Run the SSH disable workflow successfully before opening a temporary lease.");

        await VerifyUnitsAsync(host, token);
        await VerifyConnectionsAsync(host, token);
        var boot = await SshLeaseStore.BootIdAsync(host, token);
        var lease = new SshLease(context.Id, await FindAvailableUidAsync(host, token), SshPort, Ipv4Rule: false, Ipv6Rule: false, BootId: boot);
        foreach (var path in new[] { lease.Home, lease.ConfigPath, lease.AuthorizedKeyPath, SudoPath(lease) })
        {
            var file = new FileInfo(host.FilePath(path));
            if (file.Exists || file.LinkTarget != null || Directory.Exists(file.FullName))
                throw new IOException($"The planned SSH lease path already exists: {path}");
        }

        var confirm = environment.AuthorizeSshLease
            ?? throw new InvalidOperationException("This Agent cannot verify approved SSH keys.");
        var authorization = await confirm(context.Id, token);
        var (key, fingerprint) = SshPublicKey.Parse(authorization.PublicKey);
        if (authorization.KeyId != value.KeyId || fingerprint != value.Fingerprint || string.IsNullOrWhiteSpace(authorization.RequesterId))
            throw new InvalidDataException("The Controller returned a different SSH key than the approved selection.");
        lease = lease with { RequesterId = authorization.RequesterId };

        store.SaveOpening(context.Id, new SshOpening(null));
        store.SaveState(new SshLeaseState(lease, Healthy: false));

        var step = "prepare the temporary account";
        try
        {
            await RunAsync(host, "useradd", ["--uid", lease.UserId, "--home-dir", lease.Home, "--create-home",
                "--user-group", "--shell", "/bin/bash", "--", lease.User], token);
            if (value.Sudo)
            {
                const UnixFileMode sudoMode = UnixFileMode.UserRead | UnixFileMode.GroupRead;
                await WriteNewAsync(host.FilePath(SudoPath(lease)), $"{lease.User} ALL=(ALL:ALL) NOPASSWD: ALL\n", sudoMode, token);
                await RunAsync(host, "visudo", ["-c", "-f", SudoPath(lease)], token);
            }

            step = "validate the temporary SSH configuration";
            const UnixFileMode publicKeyMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            await WriteNewAsync(host.FilePath(lease.AuthorizedKeyPath), key + "\n", publicKeyMode, token);
            const UnixFileMode configMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await WriteNewAsync(host.FilePath(lease.ConfigPath), Configuration(lease), configMode, token);

            step = "start and verify the temporary SSH listener";
            var expires = DateTimeOffset.UtcNow.AddMinutes(value.Minutes);
            lease = lease with { ExpiresAtUtc = expires };
            store.SaveState(new SshLeaseState(lease, Healthy: false));
            await RunAsync(host, "systemd-run", ["--unit", lease.Unit, "--property=Type=exec", "--property=KillMode=control-group",
                "--property=RuntimeDirectory=sshd", "--property=RuntimeDirectoryMode=0755",
                $"--property=ExecStartPre=/usr/sbin/sshd -t -f {lease.ConfigPath}",
                "--", "/usr/sbin/sshd", "-D", "-e", "-f", lease.ConfigPath], token);
            await ConfirmListenerAsync(host, lease, token);
            token.ThrowIfCancellationRequested();
            store.SaveState(new SshLeaseState(lease, Healthy: true));
            store.SaveOpening(context.Id, new SshOpening(expires));
        }
        catch (Exception error)
        {
            await context.TraceAsync($"Could not {step}. SSH lease {lease.Id} requires recovery. {error.Message}", "error");
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await StopLeaseUnitAsync(host, lease, stop.Token);
            }
            catch (Exception stopError) { await context.TraceAsync($"Temporary SSH stop needs verification. {stopError.Message}", "error"); }
            store.SaveState(new SshLeaseState(lease, Healthy: false));
            throw new WorkflowNeedsVerificationException(context.Id);
        }

        return await ReportAsync(context, value, lease.ExpiresAtUtc!.Value, replayed: false);
    }

    private static async Task<int> FindAvailableUidAsync(OperationHost host, CancellationToken token)
    {
        var passwd = await RunAsync(host, "getent", ["passwd"], token);
        var assigned = passwd.Output.Split('\n').Select(line => line.Split(':'))
            .Where(parts => parts.Length > 2).Select(parts => parts[2]).ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(50000, 10000).First(uid => !assigned.Contains(uid.ToString(CultureInfo.InvariantCulture)));
    }

    private static string SudoPath(SshLease lease) => "/etc/sudoers.d/" + lease.FirewallTag;

    private static async Task WriteNewAsync(string path, string value, UnixFileMode mode, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        await using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, UnixCreateMode = mode
        });
        // Apply the intended public-key readability even under a restrictive Agent umask.
        File.SetUnixFileMode(path, mode);
        await using var writer = new StreamWriter(file);
        await writer.WriteAsync(value.AsMemory(), token);
    }

    private static async Task ConfirmListenerAsync(OperationHost host, SshLease lease, CancellationToken token)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await RunAsync(host, "systemctl", ["is-active", "--quiet", lease.Unit], token);
            var sockets = await RunAsync(host, "ss", ["-Hltnp", "sport", "=", $":{SshPort}"], token);
            if (sockets.Output.Contains("(\"sshd\",", StringComparison.Ordinal)) return;
            await Task.Delay(100, token);
        }
        throw new IOException("The temporary SSH service did not start listening.");
    }

    private static string Configuration(SshLease lease) => $"""
        Port {SshPort}
        PidFile none
        UsePAM yes
        StrictModes yes
        PermitRootLogin no
        PermitEmptyPasswords no
        PubkeyAuthentication yes
        PasswordAuthentication no
        KbdInteractiveAuthentication no
        AuthenticationMethods publickey
        AllowUsers {lease.User}
        AuthorizedKeysFile {lease.AuthorizedKeyPath}
        DisableForwarding yes
        PermitUserEnvironment no
        PermitUserRC no
        LogLevel VERBOSE

        """;

    private static Task<OperationResult> ReportAsync(MachineWorkflowContext context, SshOpenInput value,
        DateTimeOffset expiry, bool replayed)
    {
        var user = SshLease.UserName(context.Id);
        return OperationOutput.ReportAsync(context, replayed ? "Returning the recorded opening result." :
                $"The temporary SSH lease is open. Connect with ssh -p {SshPort} {user}@<machine-address>.",
            new { LeaseId = context.Id, User = user, Port = SshPort, ExpiresAtUtc = expiry, value.Sudo, value.Fingerprint, Replayed = replayed });
    }
}
