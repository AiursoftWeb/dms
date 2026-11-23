using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Workflows.Operations;
using System.Text.Json;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class AptMaintenanceTests
{
    private const string Upgrade = """
        1 upgraded, 1 newly installed, 0 to remove and 1 not upgraded.
        Inst example:amd64 [1.0] (2.0 Debian stable [amd64])
        Inst dependency (1.0 Debian stable [all])
        Conf dependency (1.0 Debian stable [all])
        Conf example (2.0 Debian stable [amd64])
        """;

    private const string Full = """
        Summary:
          Upgrading: 2, Installing: 1, Removing: 1, Not Upgrading: 0
        Inst example [1.0] (2.0 Debian stable [amd64])
        Inst with-conflict [1.0] (2.0 Debian stable [amd64])
        Inst dependency (1.0 Debian stable [all])
        Remv old-conflict [1.0]
        """;

    [TestMethod]
    public async Task PreviewListsSafeChangesAndSeparatesUpgradesFromTheRemovingPlan()
    {
        var calls = new List<NativeCommand>();
        var (context, agent, registry, _) = Arrange(command =>
        {
            calls.Add(command);
            if (command.Executable == "dpkg") return new NativeResult(0, "amd64\n", "");
            return new NativeResult(0, command.Arguments.Contains("dist-upgrade") ? Full : Upgrade, "");
        });
        var definition = OperationsWorkflows.AptPlan;
        var json = await context.RunAsync(registry.Create(definition.Name, definition.Serialize(new AptPlanInput(AptMaintenance.UpgradeAll)), agent), default);
        var result = definition.ReadResult(json);
        CollectionAssert.AreEqual(new[]
        {
            new AptPackageChange(AptPackageAction.Install, "dependency", null, "1.0"),
            new AptPackageChange(AptPackageAction.Upgrade, "example", "1.0", "2.0")
        }, result.Changes);
        Assert.AreEqual("with-conflict", result.AdditionalUpgradesRequiringReview.Single().Package);
        Assert.IsTrue(calls.Where(command => command.Executable == "apt-get").All(command => command.Arguments.Contains("--simulate")));
    }

    [TestMethod]
    public async Task DriftStopsOneMachineBeforeMutationAndNamesBothVersions()
    {
        var calls = new List<NativeCommand>();
        var (context, agent, registry, events) = Arrange(command =>
        {
            calls.Add(command);
            return new NativeResult(0, command.Executable == "dpkg" ? "amd64\n" : Upgrade.Replace("2.0", "3.1"), "");
        });
        var input = new AptApplyInput(Guid.NewGuid(), Changes:
        [
            new AptPackageChange(AptPackageAction.Install, "dependency", null, "1.0"),
            new AptPackageChange(AptPackageAction.Upgrade, "example", "1.0", "2.0")
        ]);
        var definition = OperationsWorkflows.AptUpgradeAll;
        await Assert.ThrowsAsync<IOException>(() => context.RunAsync(registry.Create(definition.Name, definition.Serialize(input), agent), default));
        Assert.HasCount(2, calls);
        var trace = string.Concat(events.Where(item => item.Source == "plan-difference").Select(item => item.Message));
        StringAssert.Contains(trace, "Approved only: Upgrade example (1.0 -> 2.0)");
        StringAssert.Contains(trace, "Current only: Upgrade example (1.0 -> 3.1)");
    }

    [TestMethod]
    public async Task PurgeUsesTheAptTransactionHookAndPassesTheFrozenPlan()
    {
        const string simulation = "0 upgraded, 0 newly installed, 1 to remove and 0 not upgraded.\nPurg orphan:i386 [1:2.0-1]";
        var (context, agent, registry, _) = Arrange(command =>
        {
            if (command.Executable == "dpkg") return new NativeResult(0, "amd64", "");
            if (command.Arguments.Contains("--simulate")) return new NativeResult(0, simulation, "");
            Assert.Contains("APT::Get::Purge=true", command.Arguments);
            Assert.Contains("DPkg::Tools::Options::/fixture/agent::Version=3", command.Arguments);
            var expected = command.Environment![AptTransactionGuard.ExpectedEnvironment];
            using var payload = new StringReader("VERSION 3\nDPkg::Fixture=value\n\norphan 1:2.0-1 i386 same > - - none **REMOVE**\n");
            using var diagnostics = new StringWriter();
            Assert.AreEqual(0, AptTransactionGuard.Verify(payload, diagnostics, expected, "amd64"));
            return new NativeResult(0, "", diagnostics.ToString().ReplaceLineEndings("\n"));
        });
        var definition = OperationsWorkflows.AptAutoremove;
        var input = new AptApplyInput(Guid.NewGuid(), Purge: true, Changes: [new(AptPackageAction.Purge, "orphan:i386", "1:2.0-1", null)]);
        var json = await context.RunAsync(registry.Create(definition.Name, definition.Serialize(input), agent), default);
        StringAssert.Contains(definition.ReadResult(json).Summary, "completed");
    }

    [TestMethod]
    public void TransactionHookRejectsRacesAndActionsOutsideTheApprovedTransaction()
    {
        var expected = JsonSerializer.Serialize(new[] { new AptPackageChange(AptPackageAction.Upgrade, "example", "1", "2") });
        using var drift = new StringReader("VERSION 3\nAPT::Example=value\n\nexample 1 amd64 none < 3 amd64 none /tmp/example_3_amd64.deb\n");
        using var output = new StringWriter();
        Assert.AreEqual(1, AptTransactionGuard.Verify(drift, output, expected, "amd64"));
        StringAssert.Contains(output.ToString(), "Current only: Upgrade example (1 -> 3)");

        using var configure = new StringReader("VERSION 3\n\nexample 1 amd64 none < 2 amd64 none /tmp/example_2_amd64.deb\nother 1 amd64 none = 1 amd64 none **CONFIGURE**\n");
        Assert.AreEqual(1, AptTransactionGuard.Verify(configure, output, expected, "amd64"));
        StringAssert.Contains(output.ToString(), "configure a package outside the approved plan");
    }

    [TestMethod]
    public async Task UpdateFailsVisiblyIfAnySourceFails()
    {
        var (context, agent, registry, events) = Arrange(command =>
        {
            CollectionAssert.AreEqual(new[] { "update", "--error-on=any" }, command.Arguments.ToArray());
            return new NativeResult(100, "", "E: Failed to fetch https://packages.example.test/dists/stable/InRelease");
        });
        await Assert.ThrowsAsync<IOException>(() => context.RunAsync(registry.Create(OperationsWorkflows.AptUpdate.Name, "{}", agent), default));
        Assert.AreEqual(RunState.Failed, events.Last().State);
        Assert.IsTrue(events.Any(item => item.Source == "stderr" && item.Message.Contains("packages.example.test", StringComparison.Ordinal)));
    }

    private static (MachineWorkflowContext Context, AgentEnvironment Agent, MachineWorkflowRegistry Registry,
        List<WorkflowEvent> Events) Arrange(Func<NativeCommand, NativeResult> run)
    {
        var events = new List<WorkflowEvent>();
        var agent = new AgentEnvironment(Guid.NewGuid(), "test", 0, 0,
            Operations: new OperationHost(native: (command, _) => Task.FromResult(run(command))),
            AptTransactionHook: new AptTransactionHook("/fixture/agent", "/fixture/agent --verify-apt-transaction"));
        var registry = new MachineWorkflowRegistry();
        return (new MachineWorkflowContext(Guid.NewGuid(), null, item =>
        {
            events.Add(item);
            return Task.CompletedTask;
        }, agent, registry), agent, registry, events);
    }
}
