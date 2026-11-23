using System.Globalization;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;
using static Aiursoft.DMS.Workflows.DisableSshWorkflow;

namespace Aiursoft.DMS.Workflows.Operations;

internal static partial class SshHostStatus
{
    internal static SshHostObservation Closed() =>
        new(DateTimeOffset.UtcNow, OrdinaryOpen: false, LeaseKnown: true) { BaselineReady = true };

    internal static async Task<SshHostObservation> ObserveAsync(OperationHost host, SshLeaseStore store,
        bool alreadyLocked, CancellationToken token)
    {
        var observedAt = DateTimeOffset.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var probe = deadline.Token;
            await RequireRootAsync(host, probe);
            await using var guard = alreadyLocked ? null : await store.LockAsync(probe);
            observedAt = DateTimeOffset.UtcNow;
            var state = store.ReadState();
            var lease = state?.Lease;
            lease?.Validate();
            var socketState = await RunAsync(host, "systemctl", ["show", "--property=ActiveState,SubState", "ssh.socket"],
                probe, allowNoMatches: true);
            var socketProperties = socketState.Output.Split('\n', StringSplitOptions.TrimEntries);
            var socketOpen = socketProperties.Contains("ActiveState=active") &&
                (socketProperties.Contains("SubState=listening") || socketProperties.Contains("SubState=running"));

            var leasePid = lease == null ? null : await LeasePidAsync(host, lease, probe);
            var sockets = await RunAsync(host, "ss", ["-Hltnp"], probe);
            var sshPids = Listeners().Matches(sockets.Output)
                .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();

            bool? ordinaryOpen = socketOpen || sshPids.Any(listenerPid => listenerPid != leasePid);
            if (!socketOpen && lease != null && leasePid == null && sshPids.Count > 0) ordinaryOpen = null;
            if (state?.Healthy == true && lease?.ExpiresAtUtc is { } expiry && expiry > DateTimeOffset.UtcNow &&
                leasePid is { } pid && sshPids.Contains(pid) && !await SshLeaseStore.ShouldRecoverAsync(host, state, lease, probe))
                return new SshHostObservation(observedAt, ordinaryOpen, LeaseKnown: true, lease.Id, expiry);

            bool? baselineReady = lease != null ? null : state?.Healthy == true && ordinaryOpen == false &&
                await UnitsMaskedAndStoppedAsync(host, probe) && !await HasOpenSshConnectionsAsync(host, probe);
            return new SshHostObservation(observedAt, ordinaryOpen, LeaseKnown: lease == null && state?.Healthy != false)
            {
                BaselineReady = baselineReady
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new SshHostObservation(observedAt, OrdinaryOpen: null, LeaseKnown: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or
            System.ComponentModel.Win32Exception or InvalidDataException)
        {
            return new SshHostObservation(observedAt, OrdinaryOpen: null, LeaseKnown: false);
        }
    }

    private static async Task<int?> LeasePidAsync(OperationHost host, SshLease lease, CancellationToken token)
    {
        var status = await RunAsync(host, "systemctl", ["show", "--property=ActiveState,MainPID", lease.Unit], token, allowNoMatches: true);
        var properties = status.Output.Split('\n', StringSplitOptions.TrimEntries);
        var pid = properties.FirstOrDefault(line => line.StartsWith("MainPID=", StringComparison.Ordinal));
        return properties.Contains("ActiveState=active") && pid != null &&
            int.TryParse(pid.AsSpan("MainPID=".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number : null;
    }

    [GeneratedRegex("\\(\"sshd(?:-session|-auth)?\",pid=([0-9]{1,9}),", RegexOptions.CultureInvariant)]
    private static partial Regex Listeners();
}
