using System.Net;
using System.Net.Http.Json;
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
    public void AlreadyCurrentRequiresTheApprovedAssemblyAndInstallation()
    {
        var machine = Guid.NewGuid();
        var installation = Guid.NewGuid();
        var config = new AgentConfiguration("http://controller", Guid.NewGuid(), installation, "", "", "dmsa_test", machine);
        var toolHash = new string('c', 64);
        var release = new AgentRelease(AgentUpgrade.Architecture, AgentUpgrade.Version, new string('a', 64), new string('b', 64));
        var handoff = new UpgradeHandoff(Guid.NewGuid(), Guid.NewGuid(), null, installation, machine, release,
            "1.11.0.0", DateTime.UtcNow, "confirmed", toolHash);

        Assert.IsTrue(AgentUpgrade.IsCurrentRelease(release, config, null, release.AssemblySha256));
        Assert.IsTrue(AgentUpgrade.IsCurrentRelease(release, config, handoff, toolHash));
        Assert.IsFalse(AgentUpgrade.IsCurrentRelease(release, config, null, toolHash));
        Assert.IsFalse(AgentUpgrade.IsCurrentRelease(release, config, handoff with { Phase = "failed" }, toolHash));
        Assert.IsFalse(AgentUpgrade.IsCurrentRelease(release, config, handoff with { MachineId = Guid.NewGuid() }, toolHash));
        Assert.IsFalse(AgentUpgrade.IsCurrentRelease(release, config, handoff, new string('d', 64)));
        Assert.IsFalse(AgentUpgrade.IsCurrentRelease(release with { ArchiveSha256 = new string('e', 64) }, config, handoff, toolHash));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.ServiceUnavailable, true)]
    [DataRow(HttpStatusCode.NotFound, false)]
    [DataRow(null, true)]
    public async Task UpgradeRetriesTransientDownloadsOnly(HttpStatusCode? firstStatus, bool retry)
    {
        var state = Directory.CreateTempSubdirectory("dms-upgrade-download-");
        var archive = Path.Combine(state.FullName, "agent.tar.gz");
        var events = new List<WorkflowEvent>();
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, item => { events.Add(item); return Task.CompletedTask; });
        using var handler = new ReleaseDownload(firstStatus);
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var download = AgentUpgrade.DownloadAsync(client, new Uri("https://controller/asset"), archive, context,
                TimeSpan.FromMilliseconds(250), TimeSpan.Zero, cancellation.Token);
            if (retry)
            {
                await download;
                Assert.AreEqual("approved archive", await File.ReadAllTextAsync(archive, cancellation.Token));
                Assert.AreEqual(2, handler.Requests);
                Assert.AreEqual("warning", events.Single().Source);
            }
            else
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => download);
                Assert.AreEqual(1, handler.Requests);
                Assert.IsFalse(File.Exists(archive));
            }
        }
        finally { state.Delete(true); }
    }

    private sealed class ReleaseDownload(HttpStatusCode? firstStatus) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++Requests != 1) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("approved archive") };
            if (firstStatus is { } status) return new HttpResponseMessage(status);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The first response must time out.");
        }
    }

    [TestMethod]
    [DataRow(HttpStatusCode.GatewayTimeout, true)]
    [DataRow(HttpStatusCode.Unauthorized, false)]
    public async Task ScheduledUpgradeRetriesTemporaryManifestFailureButNotRejectedAuthentication(HttpStatusCode initialStatus, bool retries)
    {
        var release = new AgentRelease("linux-x64", "1.32.0.0", new string('a', 64), new string('b', 64));
        var releases = new[] { release, release with { Architecture = "linux-arm64" } };
        var events = new List<WorkflowEvent>();
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, item => { events.Add(item); return Task.CompletedTask; });
        using var handler = new ManifestDownload(initialStatus, releases);
        using var client = new HttpClient(handler);
        var request = AgentUpgradeManifest.GetAsync(client, new Uri("https://controller"), "test-agent", context,
            TimeSpan.FromSeconds(3), TimeSpan.Zero, CancellationToken.None);
        if (retries)
        {
            CollectionAssert.AreEqual(releases, (await request).Releases);
            Assert.AreEqual(2, handler.Requests);
            Assert.AreEqual("warning", events.Single().Source);
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => request);
            Assert.AreEqual(1, handler.Requests);
            Assert.IsEmpty(events);
        }
    }

    private sealed class ManifestDownload(HttpStatusCode initialStatus, AgentRelease[] releases) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("/api/agent/scheduled-upgrade", request.RequestUri!.AbsolutePath);
            Assert.AreEqual("test-agent", request.Headers.Authorization?.Parameter);
            return Task.FromResult(++Requests == 1 ? new HttpResponseMessage(initialStatus) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(releases) });
        }
    }

    [TestMethod]
    [DataRow("installed", true, 10, true, true)]
    [DataRow("prepared", true, -6, true, false)]
    [DataRow("installing", true, -6, true, false)]
    [DataRow("installed", false, 10, true, false)]
    [DataRow("installed", true, -1, true, false)]
    [DataRow("installed", true, 10, false, false)]
    public async Task OnlyMatchingInstalledAgentConfirmsOriginalWorkflow(string phase, bool correctHash, int minutes, bool toolIdentity, bool success)
    {
        var state = Path.Combine(Path.GetTempPath(), "dms-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var id = Guid.NewGuid();
        Guid installation;
        var machine = Guid.NewGuid();
        using var assembly = File.OpenRead(typeof(AgentUpgrade).Assembly.Location);
        var toolHash = correctHash ? Convert.ToHexStringLower(SHA256.HashData(assembly)) : new string('c', 64);
        var release = new AgentRelease(AgentUpgrade.Architecture, AgentUpgrade.Version, new string('a', 64), new string('b', 64));
        var input = new AgentUpgradeInput([release, release with { Architecture = AgentUpgrade.Architecture == "linux-x64" ? "linux-arm64" : "linux-x64" }]);
        try
        {
            using (var journal = new TaskJournal(Path.Combine(state, "journal")))
            {
                installation = journal.InstallationId;
                new AgentConfiguration("http://controller", Guid.NewGuid(), installation, "test", "", "dmsa_test", machine).Save(state);
                journal.Accept(new AgentMessage { TaskId = id, Definition = WorkflowNames.AgentUpgrade, Version = MachineWorkflows.AgentUpgrade.Version, Input = MachineWorkflows.AgentUpgrade.Serialize(input) });
                Assert.IsNotNull(journal.Start(id));
                File.WriteAllText(Path.Combine(state, "upgrade.json"), JsonSerializer.Serialize(new UpgradeHandoff(id, id, null,
                    installation, machine, release, "1.0.15.0", DateTime.UtcNow.AddMinutes(minutes), phase,
                    toolIdentity ? toolHash : null)));
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
    [DataRow(true)]
    [DataRow(false)]
    public async Task ScheduledUpgradeRestartsIdleWaitingButConfirmsOnlyTheRecordedRelease(bool matches)
    {
        var state = Directory.CreateTempSubdirectory("dms-scheduled-upgrade-recovery-").FullName;
        var journalPath = Path.Combine(state, "journal");
        var machine = Guid.NewGuid();
        var release = new AgentRelease(AgentUpgrade.Architecture, AgentUpgrade.Version, new string('a', 64), new string('b', 64));
        var scheduled = new ScheduledInvocation(ScheduledAgentUpgradeWorkflow.DefinitionName, "0 */8 * * *",
            new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc), WorkflowTrigger.Cron);
        Guid id;
        try
        {
            using (var journal = new TaskJournal(journalPath))
            {
                id = journal.AcceptScheduled(ScheduledAgentUpgradeWorkflow.DefinitionName,
                    ScheduledAgentUpgradeWorkflow.DefinitionVersion, scheduled)!.Id;
                Assert.IsNotNull(journal.Start(id));
            }
            using (var waiting = new TaskJournal(journalPath))
            {
                Assert.AreEqual(RunState.Accepted, waiting.Snapshot().Single().State);
                var task = waiting.Start(id)!;
                // The durable task and updater handoff are the only state the new process sees.
                task.ScheduledUpgradeRelease = matches ? release : release with { ArchiveSha256 = new string('c', 64) };
                await waiting.ReportAsync(id, new WorkflowEvent { WorkflowId = id, IsTrace = true });
                new AgentConfiguration("http://controller", Guid.NewGuid(), waiting.InstallationId, "test", "", "dmsa_test", machine).Save(state);
                using var assembly = File.OpenRead(typeof(AgentUpgrade).Assembly.Location);
                var hash = Convert.ToHexStringLower(SHA256.HashData(assembly));
                File.WriteAllText(Path.Combine(state, "upgrade.json"), JsonSerializer.Serialize(new UpgradeHandoff(id, id, null,
                    waiting.InstallationId, machine, release, "1.10.1.0", DateTime.UtcNow.AddMinutes(10), "installed", hash)));
            }
            using var recovered = new TaskJournal(journalPath);
            Assert.AreEqual(RunState.NeedsVerification, recovered.Snapshot().Single().State);
            await new AgentUpgrade(state, recovered).ConfirmAsync();
            var result = recovered.Snapshot().Single();
            Assert.AreEqual(matches ? RunState.Succeeded : RunState.NeedsVerification, result.State);
            var final = result.Pending.Last();
            Assert.AreEqual(id, final.WorkflowId);
            Assert.AreEqual(ScheduledAgentUpgradeWorkflow.DefinitionName, final.Definition);
            Assert.AreEqual(ScheduledAgentUpgradeWorkflow.DefinitionVersion, final.Version);
            Assert.IsNull(final.ParentId);
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
    public async Task UpgradeWithoutInstalledLifecycleCannotRunAndRequiresMediumRisk()
    {
        var release = new AgentRelease("linux-x64", "1.0.16.0", new string('a', 64), new string('b', 64));
        var input = new AgentUpgradeInput([release, release with { Architecture = "linux-arm64" }]);
        var registry = new MachineWorkflowRegistry();
        var workflow = registry.Create(WorkflowNames.AgentUpgrade, MachineWorkflows.AgentUpgrade.Serialize(input), new AgentEnvironment(Guid.NewGuid(), "old", 0, 0));
        Assert.AreEqual(RiskLevel.Medium, registry.FindDefinition(workflow.Name)!.Risk);
        var events = new List<WorkflowEvent>();
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, item => { events.Add(item); return Task.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.RunAsync(workflow, CancellationToken.None));
        Assert.IsTrue(events.Any(e => e.State == RunState.Failed));
        Assert.IsFalse(events.Any(e => e.State == RunState.Succeeded));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.AgentUpgrade.Serialize(new AgentUpgradeInput([release])));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.AgentUpgrade.Serialize(new AgentUpgradeInput([release, release])));
    }
}
