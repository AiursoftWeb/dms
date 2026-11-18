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

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dms-ssh-test-");
        private readonly MachineWorkflowRegistry _registry = new();
        private int _inMask;
        public int MaskCount { get; private set; }
        public bool FailFirewall { get; set; }
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

        public void SeedLease(Guid id) => File.WriteAllText(StatePath, JsonSerializer.Serialize(new
        {
            Healthy = true, Lease = new { Id = id, Uid = 2400, Port = 22, Ipv4Rule = true, Ipv6Rule = false }
        }));

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
                    Interlocked.Decrement(ref _inMask);
                    return new NativeResult(0, "", "");
                case "systemctl":
                    return new NativeResult(0, "LoadState=masked\nActiveState=inactive\n", "");
                case "ss" or "loginctl":
                    return new NativeResult(0, "", "");
                case "pkill":
                    return new NativeResult(1, "", "");
                case "iptables":
                    return FailFirewall ? new NativeResult(2, "", "Fixture firewall error.") : new NativeResult(0, "", "");
                default:
                    throw new InvalidOperationException($"Unexpected command {command.Executable}.");
            }
        }

        public void Dispose() => _root.Delete(recursive: true);
    }
}
