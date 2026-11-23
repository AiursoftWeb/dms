using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class AgentUpgradeSchedulingTests : TestBase
{
    private sealed class ProbeWorkflow(string name, TaskCompletionSource started, Task release) : MachineWorkflow
    {
        public override string Name => name;
        public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
        {
            started.TrySetResult();
            await release.WaitAsync(token);
            return JsonSerializer.Serialize(new EmptyInput());
        }
    }

    [TestMethod]
    public async Task UpgradeDrainsRunningWorkAndResumesQueuedWorkWhenPreparationFails()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var following = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new MachineWorkflowDefinition<EmptyInput, EmptyInput>("test.upgrade-hold", RiskLevel.Safe, PublicEntry: true);
        var after = new MachineWorkflowDefinition<EmptyInput, EmptyInput>("test.upgrade-after", RiskLevel.Safe, PublicEntry: true);
        var registry = new MachineWorkflowRegistry();
        foreach (var target in new[] { registry, Server!.Services.GetRequiredService<MachineWorkflowRegistry>() })
        {
            target.Register(hold, (_, _) => new ProbeWorkflow(hold.Name, started, release.Task));
            target.Register(after, (_, _) => new ProbeWorkflow(after.Name, following, Task.CompletedTask));
        }
        var path = Path.Combine(Path.GetTempPath(), "dms-upgrade-drain-" + Guid.NewGuid());
        var assets = Path.Combine(Server.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "agent-assets");
        Directory.CreateDirectory(assets);
        var metadata = Path.Combine(assets, "releases.json");
        Assert.IsFalse(File.Exists(metadata));
        var payload = new AgentRelease("linux-x64", "1.0.16.0", new string('a', 64), new string('b', 64));
        var input = new AgentUpgradeInput([payload, payload with { Architecture = "linux-arm64" }]);
        File.WriteAllText(metadata, JsonSerializer.Serialize(input.Releases));
        var key = DmsKeyAuthenticationHandler.CreateKey(true);
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
             Guid machineId; string userId;
            using (var scope = Server.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
                var user = new User { Id = Guid.NewGuid().ToString(), UserName = Guid.NewGuid().ToString(), DisplayName = "Drain operator" }; userId = user.Id;

                var machine = new Machine { Name = "Drain node", KeyHash = DmsKeyAuthenticationHandler.Hash(key) }; machineId = machine.Id;
                db.Users.Add(user); db.Machines.Add(machine);
                foreach (var risk in new[] { RiskLevel.Safe, RiskLevel.Medium })
                    DmsTestPermissions.Risk(db, user.Id, risk, request: true, automatic: true, approve: false);
                await db.SaveChangesAsync();
                await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(userId, hold.Request(new EmptyInput(), "Hold an existing task", [machineId]));
            }
            using var journal = new TaskJournal(path);
            var agent = new AgentClient(Http.BaseAddress!, key, journal, registry, installedState: path).RunAsync(stopping.Token);
            try
            {
                await started.Task.WaitAsync(stopping.Token);
                using (var scope = Server.Services.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(userId, MachineWorkflows.AgentUpgrade.Request(input, "Drain before updating", [machineId]));
                while (!journal.Snapshot().Any(t => t.Definition == WorkflowNames.AgentUpgrade && t.State == RunState.Running)) await Task.Delay(50, stopping.Token);
                using (var scope = Server.Services.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(userId, after.Request(new EmptyInput(), "Queue during maintenance", [machineId]));
                await Task.Delay(1500, stopping.Token);
                Assert.IsFalse(following.Task.IsCompleted, "New work must not start while the Agent is draining.");
                Assert.AreEqual(RunState.Running, journal.Snapshot().Single(t => t.Definition == hold.Name).State);
                release.TrySetResult();
                await following.Task.WaitAsync(stopping.Token);
                while (!journal.Snapshot().Any(t => t.Definition == WorkflowNames.AgentUpgrade && t.State == RunState.Failed)) await Task.Delay(50, stopping.Token);
                Assert.AreEqual(RunState.Succeeded, journal.Snapshot().Single(t => t.Definition == hold.Name).State);
                Assert.IsTrue(journal.Snapshot().Single(t => t.Definition == WorkflowNames.AgentUpgrade).Pending.All(e => e.State != RunState.Succeeded));
            }
            finally
            {
                release.TrySetResult();
                await stopping.CancelAsync();
                try { await agent; } catch (OperationCanceledException) { }
            }
        }
        finally { File.Delete(metadata); if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [TestMethod]
    public async Task ScheduledUpgradeLetsWorkRunAndResetsTheTenSecondWindowAfterNewWork()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var briefStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new MachineWorkflowRegistry();
        var hold = new MachineWorkflowDefinition<EmptyInput, EmptyInput>("test.scheduled-upgrade-hold", RiskLevel.Safe, PublicEntry: true);
        var brief = new MachineWorkflowDefinition<EmptyInput, EmptyInput>("test.scheduled-upgrade-brief", RiskLevel.Safe, PublicEntry: true);
        foreach (var target in new[] { registry, Server!.Services.GetRequiredService<MachineWorkflowRegistry>() })
        {
            target.Register(hold, (_, _) => new ProbeWorkflow(hold.Name, started, release.Task));
            target.Register(brief, (_, _) => new ProbeWorkflow(brief.Name, briefStarted, Task.CompletedTask));
        }

        var path = Directory.CreateTempSubdirectory("dms-scheduled-upgrade-").FullName;
        var assets = Path.Combine(Server!.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "agent-assets");
        Directory.CreateDirectory(assets);
        var metadata = Path.Combine(assets, "releases.json");
        Assert.IsFalse(File.Exists(metadata));
        var version = new Version(Version.Parse(AgentUpgrade.Version).Major + 1, 0, 0, 0).ToString();
        var candidate = new AgentRelease("linux-x64", version, new string('a', 64), new string('b', 64));
        var releases = new List<AgentRelease> { candidate, candidate with { Architecture = "linux-arm64" } };
        var key = DmsKeyAuthenticationHandler.CreateKey(true);
        var secondKey = DmsKeyAuthenticationHandler.CreateKey(true);
        var disabledKey = DmsKeyAuthenticationHandler.CreateKey(true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        using var scope = Server.Services.CreateScope();
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var machine = new Machine { Name = "Scheduled upgrade test", KeyHash = DmsKeyAuthenticationHandler.Hash(key) };
            var second = new Machine { Name = "Another managed machine", KeyHash = DmsKeyAuthenticationHandler.Hash(secondKey) };
            var disabled = new Machine { Name = "Disabled machine", KeyHash = DmsKeyAuthenticationHandler.Hash(disabledKey), Disabled = true };
            var user = new User { Id = Guid.NewGuid().ToString(), UserName = Guid.NewGuid().ToString(), DisplayName = "Test operator" };
            db.Machines.AddRange(machine, second, disabled);
            db.Users.Add(user);
            DmsTestPermissions.Risk(db, user.Id, RiskLevel.Safe, request: true, automatic: true, approve: false);
            await db.SaveChangesAsync();
            using var anonymous = await Http.GetAsync("/api/agent/scheduled-upgrade", stop.Token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using var beforeRelease = new HttpRequestMessage(HttpMethod.Get, "/api/agent/scheduled-upgrade");
            beforeRelease.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secondKey);
            using var unavailable = await Http.SendAsync(beforeRelease, stop.Token);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);

            File.WriteAllText(metadata, JsonSerializer.Serialize(releases));
            foreach (var (machineKey, status) in new[] { (secondKey, HttpStatusCode.OK), (disabledKey, HttpStatusCode.Unauthorized) })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/agent/scheduled-upgrade");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", machineKey);
                using var result = await Http.SendAsync(request, stop.Token);
                Assert.AreEqual(status, result.StatusCode);
            }

            var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
            await workflows.SubmitAsync(user.Id, hold.Request(new EmptyInput(), "Simulate active work", [machine.Id]));
            using var journal = new TaskJournal(path);
            var agent = new AgentClient(Http.BaseAddress!, key, journal, registry, installedState: path);
            var running = agent.RunAsync(stop.Token);
            try
            {
                await started.Task.WaitAsync(stop.Token);
                var utc = DateTime.UtcNow;
                var scheduledAt = utc.Date.AddHours(utc.Hour - utc.Hour % 8);
                agent.TickSchedules(scheduledAt);
                JournalTask? Scheduled() => journal.Snapshot().SingleOrDefault(task => task.Definition == ScheduledAgentUpgradeWorkflow.DefinitionName);
                while (Scheduled()?.State != RunState.Running) await Task.Delay(50, stop.Token);
                var waiting = Scheduled()!;
                Assert.IsNull(waiting.ScheduledUpgradeRelease, "Active work must prevent reserving maintenance.");
                var nextOccurrence = waiting.Schedule! with { ScheduledAt = scheduledAt.AddHours(8) };
                Assert.IsNull(journal.AcceptScheduled(waiting.Definition, waiting.Version, nextOccurrence),
                    "A later occurrence must not start a competing upgrade while one is waiting.");

                release.TrySetResult();
                while (!journal.Snapshot().Any(task => task.Definition == hold.Name && task.State == RunState.Succeeded))
                    await Task.Delay(50, stop.Token);
                await Task.Delay(TimeSpan.FromSeconds(3), stop.Token);
                await workflows.SubmitAsync(user.Id, brief.Request(new EmptyInput(), "Interrupt the idle window", [machine.Id]));
                await briefStarted.Task.WaitAsync(stop.Token);
                Assert.IsNull(Scheduled()!.ScheduledUpgradeRelease, "New work must still start during the idle window.");
                while (Scheduled()?.State != RunState.Failed) await Task.Delay(50, stop.Token);
                var scheduled = Scheduled()!;
                Assert.AreEqual(releases.Single(item => item.Architecture == AgentUpgrade.Architecture), scheduled.ScheduledUpgradeRelease);

                // The test executable cannot activate systemd. Its lifecycle preflight fails
                // immediately after reservation, giving us the real wait without installing an Agent.
                var briefId = journal.Snapshot().Single(task => task.Definition == brief.Name).Id;
                using var records = Server.Services.CreateScope();
                var live = records.ServiceProvider.GetRequiredService<TemplateDbContext>();
                while (await live.WorkflowRuns.AsNoTracking().CountAsync(run =>
                    (run.Id == briefId || run.Id == scheduled.Id) && run.FinishedAt.HasValue, stop.Token) != 2)
                    await Task.Delay(50, stop.Token);
                var completed = await live.WorkflowRuns.AsNoTracking().SingleAsync(run => run.Id == briefId, stop.Token);
                var upgrade = await live.WorkflowRuns.AsNoTracking().SingleAsync(run => run.Id == scheduled.Id, stop.Token);
                Assert.IsTrue(upgrade.FinishedAt >= completed.FinishedAt!.Value.AddSeconds(10));
                Assert.AreEqual(WorkflowOrigin.Automatic, upgrade.Origin);
                Assert.AreEqual(WorkflowTrigger.Cron, upgrade.Trigger);
                Assert.IsNull(upgrade.ParentId);
                Assert.IsNull(upgrade.LockboxId);
            }
            finally
            {
                release.TrySetResult();
                await stop.CancelAsync();
                try { await running; } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            File.Delete(metadata);
            Directory.Delete(path, true);
        }
    }
}
