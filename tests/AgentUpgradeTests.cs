using System.Security.Cryptography;
using System.Text.Json;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class AgentUpgradeTests
{
    [TestMethod]
    [DataRow("installed", true, 10, true)]
    [DataRow("prepared", true, -1, false)]
    [DataRow("installing", true, -1, false)]
    [DataRow("installed", false, 10, false)]
    [DataRow("installed", true, -1, false)]
    public async Task OnlyMatchingInstalledAgentConfirmsOriginalWorkflow(string phase, bool correctHash, int minutes, bool success)
    {
        var state = Path.Combine(Path.GetTempPath(), "dms-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var id = Guid.NewGuid();
        Guid installation;
        var machine = Guid.NewGuid();
        using var assembly = File.OpenRead(typeof(AgentUpgrade).Assembly.Location);
        var release = new AgentRelease(AgentUpgrade.Architecture, AgentUpgrade.Version, new string('a', 64),
            correctHash ? Convert.ToHexStringLower(SHA256.HashData(assembly)) : new string('b', 64));
        var input = new AgentUpgradeInput([release, release with { Architecture = AgentUpgrade.Architecture == "linux-x64" ? "linux-arm64" : "linux-x64" }]);
        try
        {
            using (var journal = new TaskJournal(Path.Combine(state, "journal")))
            {
                installation = journal.InstallationId;
                new AgentConfiguration("http://controller", "cluster", Guid.NewGuid(), installation, "test", "", "dmsa_test", machine).Save(state);
                journal.Accept(new AgentMessage { TaskId = id, Definition = WorkflowNames.AgentUpgrade, Version = MachineWorkflows.AgentUpgrade.Version, Input = MachineWorkflows.AgentUpgrade.Serialize(input) });
                Assert.IsNotNull(journal.Start(id));
                File.WriteAllText(Path.Combine(state, "upgrade.json"), JsonSerializer.Serialize(new UpgradeHandoff(id, id, null,
                    installation, machine, release, "1.0.15.0", DateTime.UtcNow.AddMinutes(minutes), phase)));
            }
            using var recovered = new TaskJournal(Path.Combine(state, "journal"));
            await new AgentUpgrade(state, recovered).ConfirmAsync();
            var task = recovered.Snapshot().Single();
            Assert.AreEqual(id, task.Id);
            Assert.AreEqual(installation, recovered.InstallationId);
            Assert.AreEqual(success ? RunState.Succeeded : RunState.NeedsVerification, task.State);
            Assert.IsNull(recovered.Start(id));
            if (success)
            {
                var result = JsonSerializer.Deserialize<AgentUpgradeResult>(task.Pending.Single(e => e.State == RunState.Succeeded).Result)!;
                Assert.AreEqual(installation, result.InstallationId);
                Assert.AreEqual(release.ArchiveSha256, result.ArchiveSha256);
                Assert.AreEqual(AgentUpgrade.Version, result.Version);
            }
            var sequence = task.Sequence;
            await new AgentUpgrade(state, recovered).ConfirmAsync();
            Assert.AreEqual(sequence, recovered.Snapshot().Single().Sequence, "Confirmation must not be replayed.");
            Assert.AreEqual(machine, AgentConfiguration.Load(state)!.MachineId);
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task RecoveryWaitsForTheInFlightUpdaterWithoutStartingAnyTask()
    {
        var state = Path.Combine(Path.GetTempPath(), "dms-upgrade-wait-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            using var journal = new TaskJournal(Path.Combine(state, "journal"));
            var taskId = Guid.NewGuid();
            journal.Accept(new AgentMessage { TaskId = taskId, Definition = WorkflowNames.AgentStatus });
            var marker = Path.Combine(state, "upgrade.json");
            var release = new AgentRelease("linux-x64", "1.0.16.0", new string('a', 64), new string('b', 64));
            var handoff = new UpgradeHandoff(Guid.NewGuid(), Guid.NewGuid(), null, journal.InstallationId, Guid.NewGuid(), release,
                "1.0.15.0", DateTime.UtcNow.AddMinutes(1), "stopping");
            File.WriteAllText(marker, JsonSerializer.Serialize(handoff));
            var recovery = new AgentUpgrade(state, journal).ConfirmAsync();
            await Task.Delay(100);
            Assert.IsFalse(recovery.IsCompleted, "Do not reconnect and admit new tasks while the updater may still stop the Agent.");
            Assert.AreEqual(RunState.Accepted, journal.Snapshot().Single().State);
            File.WriteAllText(marker, JsonSerializer.Serialize(handoff with { Phase = "installed" }));
            await recovery.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual(RunState.Accepted, journal.Snapshot().Single().State);
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task UpgradeWithoutInstalledLifecycleCannotRunAndRemainsHighRisk()
    {
        var release = new AgentRelease("linux-x64", "1.0.16.0", new string('a', 64), new string('b', 64));
        var input = new AgentUpgradeInput([release, release with { Architecture = "linux-arm64" }]);
        var registry = new MachineWorkflowRegistry();
        var workflow = registry.Create(WorkflowNames.AgentUpgrade, MachineWorkflows.AgentUpgrade.Serialize(input), new AgentEnvironment(Guid.NewGuid(), "old", 0, 0));
        Assert.AreEqual(RiskLevel.High, registry.FindDefinition(workflow.Name)!.PublicRisk);
        var events = new List<WorkflowEvent>();
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, item => { events.Add(item); return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.RunAsync(workflow, CancellationToken.None));
        Assert.IsTrue(events.Any(e => e.State == RunState.Failed));
        Assert.IsFalse(events.Any(e => e.State == RunState.Succeeded));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.AgentUpgrade.Serialize(new AgentUpgradeInput([release])));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.AgentUpgrade.Serialize(new AgentUpgradeInput([release, release])));
    }
}
