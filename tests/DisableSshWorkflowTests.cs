using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class DisableSshWorkflowTests
{
    [TestMethod]
    public async Task ConcurrentDisablesSerializeAndAReplayLeavesTheNextLeaseAlone()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("SSH disable runs on Linux.");
        using var fixture = new Fixture();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await Task.WhenAll(fixture.RunAsync(first), fixture.RunAsync(second));
        Assert.AreEqual(2, fixture.MaskCount);

        var nextLease = Guid.NewGuid();
        fixture.SeedLease(nextLease);
        var replay = await fixture.RunAsync(first);
        Assert.IsTrue(replay.Facts!.Value.GetProperty("Replayed").GetBoolean());
        Assert.AreEqual(2, fixture.MaskCount);
        Assert.AreEqual(nextLease, fixture.State().GetProperty("Lease").GetProperty("Id").GetGuid());
    }

    [TestMethod]
    public async Task FailedCleanupKeepsAnUnhealthyLeaseForANewDisableToRecover()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("SSH disable runs on Linux.");
        using var fixture = new Fixture();
        var lease = Guid.NewGuid();
        fixture.SeedLease(lease);
        fixture.FailFirewall = true;
        var workflow = Guid.NewGuid();
        await Assert.ThrowsAsync<WorkflowNeedsVerificationException>(() => fixture.RunAsync(workflow));
        Assert.IsFalse(fixture.State().GetProperty("Healthy").GetBoolean());
        Assert.AreEqual(lease, fixture.State().GetProperty("Lease").GetProperty("Id").GetGuid());
        Assert.IsTrue(fixture.Events.Any(item => item.IsTrace &&
            item.Message.Contains("remove lease firewall rules", StringComparison.Ordinal)));

        fixture.FailFirewall = false;
        await Assert.ThrowsAsync<WorkflowNeedsVerificationException>(() => fixture.RunAsync(workflow));
        await fixture.RunAsync(Guid.NewGuid());
        Assert.IsTrue(fixture.State().GetProperty("Healthy").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, fixture.State().GetProperty("Lease").ValueKind);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task IncompleteClosureReportsListenersAndConnectionsSeparately(bool listener, bool connection)
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("SSH disable runs on Linux.");
        using var fixture = new Fixture
        {
            RemainingListener = listener ? "LISTEN 0 128 0.0.0.0:2222 0.0.0.0:* users:((\"sshd\",pid=101,fd=3))" : "",
            RemainingConnection = connection ? "0 0 192.0.2.10:22 192.0.2.11:42000 users:((\"sshd-session\",pid=102,fd=4))" : ""
        };
        var workflow = Guid.NewGuid();
        await Assert.ThrowsAsync<WorkflowNeedsVerificationException>(() => fixture.RunAsync(workflow));

        var outcome = fixture.Events.Single(item => item.WorkflowId == workflow && item.State == RunState.NeedsVerification);
        var result = JsonSerializer.Deserialize<OperationResult>(outcome.Result)!;
        Assert.IsFalse(result.Complete);
        Assert.IsFalse(fixture.State().GetProperty("Healthy").GetBoolean());
        StringAssert.Contains(result.Summary, "masked and stopped");
        var check = result.Facts!.Value.Deserialize<SshClosureCheck>()!;
        Assert.AreEqual(listener ? 1 : 0, check.ListenerCount);
        Assert.AreEqual(connection ? 1 : 0, check.EstablishedConnectionCount);
        if (listener) StringAssert.Contains(check.ListenerSamples.Single(), ":2222");
        if (connection) StringAssert.Contains(check.EstablishedConnectionSamples.Single(), "192.0.2.11:42000");
    }

    [TestMethod]
    public async Task DelayedRecoveryCannotCloseANewerLease()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("SSH disable runs on Linux.");
        using var fixture = new Fixture();
        var next = Guid.NewGuid();
        fixture.SeedLease(next);
        var result = await fixture.RunAsync(Guid.NewGuid(), expiredLease: Guid.NewGuid());
        StringAssert.Contains(result.Summary, "no longer requires recovery");
        Assert.AreEqual(0, fixture.MaskCount);
        Assert.AreEqual(next, fixture.State().GetProperty("Lease").GetProperty("Id").GetGuid());
    }

    [TestMethod]
    public async Task BaselineRepairsAnUnleasedSshListenerWhileFiveMinuteCheckLeavesItAlone()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("SSH baseline runs on Linux.");
        using var fixture = new Fixture { SshListening = true };

        var periodic = await fixture.CheckAsync(new SshLeaseCheckInput { ReportStatus = true });
        Assert.IsNull(periodic.SshClosed);
        Assert.IsTrue(periodic.Observation?.OrdinaryOpen == true);
        Assert.IsTrue(periodic.Observation?.LeaseKnown == true);
        Assert.IsNull(periodic.Observation?.LeaseId);
        Assert.IsTrue(fixture.SshListening);

        var baseline = await fixture.CheckAsync(new SshLeaseCheckInput(CloseSshWhenNoLease: true));
        Assert.AreEqual(true, baseline.SshClosed);
        Assert.IsFalse(fixture.SshListening);
        Assert.AreEqual(1, fixture.MaskCount);

        var repeated = await fixture.CheckAsync(new SshLeaseCheckInput(CloseSshWhenNoLease: true));
        Assert.AreEqual(true, repeated.SshClosed);
        Assert.AreEqual(1, fixture.MaskCount);

        fixture.SshListening = true;
        await fixture.CheckAsync(new SshLeaseCheckInput(CloseSshWhenNoLease: true));
        Assert.IsFalse(fixture.SshListening);
        Assert.AreEqual(2, fixture.MaskCount);
    }

    [TestMethod]
    public async Task SshObservationDistinguishesLeaseListenerSocketActivationAndMissingListener()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("SSH observation runs on Linux.");
        using var fixture = new Fixture { SshListening = true, LeasePid = 123 };
        var lease = Guid.NewGuid();
        var expiry = DateTimeOffset.UtcNow.AddMinutes(60);
        fixture.SeedLease(lease, expiry);
        var report = new SshLeaseCheckInput { ReportStatus = true };

        var temporary = (await fixture.CheckAsync(report)).Observation!;
        Assert.AreEqual(false, temporary.OrdinaryOpen);
        Assert.AreEqual(lease, temporary.LeaseId);
        Assert.AreEqual(expiry, temporary.ExpiresAtUtc);

        fixture.SocketListening = true;
        var both = (await fixture.CheckAsync(report)).Observation!;
        Assert.AreEqual(true, both.OrdinaryOpen);
        Assert.AreEqual(lease, both.LeaseId);

        fixture.SshListening = false;
        var uncertain = (await fixture.CheckAsync(report)).Observation!;
        Assert.AreEqual(true, uncertain.OrdinaryOpen);
        Assert.IsFalse(uncertain.LeaseKnown);
        Assert.IsNull(uncertain.LeaseId);
        Assert.AreEqual(0, fixture.MaskCount);
    }

    [TestMethod]
    public async Task BaselinePreservesValidLeaseAndPeriodicCheckClosesExpiredLease()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("SSH baseline runs on Linux.");
        using var fixture = new Fixture { SshListening = true };
        var lease = Guid.NewGuid();
        fixture.SeedLease(lease, DateTimeOffset.UtcNow.AddHours(1));

        var result = await fixture.CheckAsync(new SshLeaseCheckInput(CloseSshWhenNoLease: true));
        Assert.AreEqual(false, result.SshClosed);
        Assert.AreEqual(lease, fixture.State().GetProperty("Lease").GetProperty("Id").GetGuid());
        Assert.IsTrue(fixture.SshListening);
        Assert.AreEqual(0, fixture.MaskCount);

        fixture.SeedLease(lease, DateTimeOffset.UtcNow.AddHours(-1));
        var periodic = await fixture.CheckAsync(new SshLeaseCheckInput());
        Assert.AreEqual(true, periodic.SshClosed);
        Assert.IsFalse(fixture.SshListening);
        Assert.AreEqual(JsonValueKind.Null, fixture.State().GetProperty("Lease").ValueKind);
    }

    [TestMethod]
    public async Task ScheduledApplicationSkipsUnconfiguredMachinesAndDefersUfwForAnActiveLease()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("SSH baseline runs on Linux.");
        using var fixture = new Fixture { SshListening = true };
        var settings = new SecurityBaselinePolicy("test", UfwEnabled: true);
        var unconfigured = await fixture.ApplyBaselineAsync(null);
        StringAssert.Contains(unconfigured.Summary, "no scheduled security baseline");
        Assert.AreEqual(0, fixture.MaskCount);

        var lease = Guid.NewGuid();
        fixture.SeedLease(lease, DateTimeOffset.UtcNow.AddHours(1));
        var active = await fixture.ApplyBaselineAsync(settings);
        Assert.IsFalse(active.Complete);
        Assert.IsTrue(active.Facts!.Value.GetProperty("UfwDeferred").GetBoolean());
        Assert.IsFalse(active.Facts!.Value.GetProperty("Ssh").GetProperty("SshClosed").GetBoolean());
        Assert.IsTrue(fixture.SshListening);
        Assert.AreEqual(0, fixture.MaskCount);
    }

    [TestMethod]
    public async Task UfwReturnsToItsPreviousStateWhenTheControllerBecomesUnreachable()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("SSH baseline runs on Linux.");
        using var fixture = new Fixture();
        var policy = new SecurityBaselinePolicy("test", UfwEnabled: true);
        var result = await fixture.ApplyBaselineAsync(policy);
        Assert.IsTrue(result.Complete);
        Assert.IsTrue(fixture.UfwActive);

        fixture.BlockPolicyReadAfterUfwChange = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.ApplyBaselineAsync(policy with { UfwEnabled = false }));
        Assert.IsTrue(fixture.UfwActive);
        Assert.IsTrue(fixture.Events.Any(item => item.IsTrace &&
            item.Message.Contains("previous UFW state was restored", StringComparison.Ordinal)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dms-ssh-test-");
        private readonly MachineWorkflowRegistry _registry = new();
        private int _inMask;
        public int MaskCount { get; private set; }
        public bool SshListening { get; set; }
        public bool SocketListening { get; set; }
        public int? LeasePid { get; set; }
        public bool UfwActive { get; private set; }
        public bool BlockPolicyReadAfterUfwChange { get; set; }
        private bool _policyBlocked;
        public bool FailFirewall { get; set; }
        public string RemainingListener { get; init; } = "";
        public string RemainingConnection { get; init; } = "";
        public System.Collections.Concurrent.ConcurrentQueue<WorkflowEvent> Events { get; } = new();
        private string StatePath => Path.Combine(_root.FullName, "var/lib/aiursoft-dms-ssh/state.json");

        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(Path.GetDirectoryName(StatePath)!,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.Combine(_root.FullName, "etc"));
            File.WriteAllText(Path.Combine(_root.FullName, "etc/passwd"), "");
            Directory.CreateDirectory(Path.Combine(_root.FullName, "proc/self"));
            File.WriteAllText(Path.Combine(_root.FullName, "proc/self/mountinfo"), "");
        }

        public void SeedLease(Guid id, DateTimeOffset? expires = null) => File.WriteAllText(StatePath, JsonSerializer.Serialize(new
        {
            Healthy = true,
            Lease = new { Id = id, Uid = 2400, Port = 22, Ipv4Rule = true, Ipv6Rule = false, ExpiresAtUtc = expires }
        }));

        public async Task<SshLeaseCheckResult> CheckAsync(SshLeaseCheckInput input)
        {
            var definition = OperationsWorkflows.SshCheckLease;
            return definition.ReadResult(await InvokeAsync(definition.Name, definition.Serialize(input)));
        }

        public async Task<OperationResult> ApplyBaselineAsync(SecurityBaselinePolicy? policy) =>
            JsonSerializer.Deserialize<OperationResult>(await InvokeAsync("machine.security.apply-baseline", "{}", policy))!;

        private async Task<string> InvokeAsync(string name, string input, SecurityBaselinePolicy? policy = null)
        {
            var host = new OperationHost(_root.FullName, NativeAsync);
            var agent = new AgentEnvironment(Guid.NewGuid(), "test", 0, 0, Operations: host,
                GetSecurityBaselinePolicy: _ => _policyBlocked
                    ? Task.FromException<SecurityBaselinePolicy?>(new IOException("Controller unavailable."))
                    : Task.FromResult(policy));
            var context = new MachineWorkflowContext(Guid.NewGuid(), null, item =>
            {
                Events.Enqueue(item);
                return Task.CompletedTask;
            }, agent, _registry);
            return await context.RunAsync(_registry.Create(name, input, agent), CancellationToken.None);
        }

        public JsonElement State() => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(StatePath));

        public async Task<OperationResult> RunAsync(Guid workflowId, Guid? expiredLease = null)
        {
            var host = new OperationHost(_root.FullName, NativeAsync);
            var agent = new AgentEnvironment(Guid.NewGuid(), "test", 0, 0, Operations: host);
            var context = new MachineWorkflowContext(workflowId, null, item =>
            {
                Events.Enqueue(item);
                return Task.CompletedTask;
            }, agent, _registry);
            if (expiredLease is { } expected)
            {
                var recovery = OperationsWorkflows.SshRecover;
                return recovery.ReadResult(await context.RunAsync(_registry.Create(recovery.Name,
                    recovery.Serialize(new SshRecoveryInput(expected)), agent), CancellationToken.None));
            }
            var disable = OperationsWorkflows.SshDisable;
            return disable.ReadResult(await context.RunAsync(_registry.Create(disable.Name, "{}", agent), CancellationToken.None));
        }

        private async Task<NativeResult> NativeAsync(NativeCommand command, CancellationToken token)
        {
            switch (command.Executable)
            {
                case "id":
                    return new NativeResult(0, "0", "");
                case "systemctl" when command.Arguments[0] == "mask":
                    Assert.AreEqual(1, Interlocked.Increment(ref _inMask));
                    await Task.Delay(240, token);
                    MaskCount++;
                    SshListening = false;
                    Interlocked.Decrement(ref _inMask);
                    return new NativeResult(0, "", "");
                case "systemctl" when command.Arguments.Contains("--property=ActiveState,MainPID"):
                    return new NativeResult(0, LeasePid is { } pid ? $"ActiveState=active\nMainPID={pid}\n" : "ActiveState=inactive\nMainPID=0\n", "");
                case "systemctl" when command.Arguments.Contains("--property=ActiveState,SubState"):
                    return new NativeResult(0, SocketListening ? "ActiveState=active\nSubState=listening\n" : "ActiveState=inactive\nSubState=dead\n", "");
                case "systemctl":
                    return new NativeResult(0, SshListening
                        ? "LoadState=loaded\nActiveState=active\n"
                        : "LoadState=masked\nActiveState=inactive\n", "");
                case "ss" when command.Arguments[0] == "-Hltnp":
                    return new NativeResult(0, SshListening ? "LISTEN users:((\"sshd\",pid=123,fd=3))" : RemainingListener, "");
                case "ss":
                    return new NativeResult(0, RemainingConnection, "");
                case "loginctl":
                    return new NativeResult(0, "", "");
                case "pkill":
                    return new NativeResult(1, "", "");
                case "dpkg-query" when command.Arguments[^1] == "ufw":
                    return new NativeResult(0, "install ok installed", "");
                case "ufw" when command.Arguments[0] == "status":
                    return new NativeResult(0, UfwActive ? "Status: active" : "Status: inactive", "");
                case "ufw" when command.Arguments[0] == "--dry-run":
                    return new NativeResult(0, "", "");
                case "ufw" when command.Arguments[0] == "--force":
                    UfwActive = command.Arguments[1] == "enable";
                    _policyBlocked = BlockPolicyReadAfterUfwChange;
                    return new NativeResult(0, "", "");
                case "iptables":
                    return FailFirewall ? new NativeResult(2, "", "Fixture firewall error.") : new NativeResult(0, "", "");
                default:
                    throw new InvalidOperationException($"Unexpected command {command.Executable}.");
            }
        }

        public void Dispose() => _root.Delete(recursive: true);
    }
}
