using System.Text.Json;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.Workflows;

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
            Guid clusterId; Guid machineId; string userId;
            using (var scope = Server.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
                var user = new User { Id = Guid.NewGuid().ToString(), UserName = Guid.NewGuid().ToString(), DisplayName = "Drain operator" }; userId = user.Id;
                var cluster = new Cluster { Name = "Upgrade drain" }; clusterId = cluster.Id;
                var machine = new Machine { ClusterId = cluster.Id, Name = "Drain node", KeyHash = DmsKeyAuthenticationHandler.Hash(key) }; machineId = machine.Id;
                db.Users.Add(user); db.Clusters.Add(cluster); db.Machines.Add(machine);
                foreach (var risk in new[] { RiskLevel.Safe, RiskLevel.High })
                    DmsTestPermissions.Risk(db, user.Id, risk, request: true, automatic: true, approve: false);
                await db.SaveChangesAsync();
                await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(userId, hold.Request(clusterId, new EmptyInput(), "Hold an existing task", [machineId]));
            }
            using var journal = new TaskJournal(path);
            var agent = new AgentClient(Http.BaseAddress!, key, journal, registry, installedState: path).RunAsync(stopping.Token);
            try
            {
                await started.Task.WaitAsync(stopping.Token);
                using (var scope = Server.Services.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(userId, MachineWorkflows.AgentUpgrade.Request(clusterId, input, "Drain before updating", [machineId]));
                while (!journal.Snapshot().Any(t => t.Definition == WorkflowNames.AgentUpgrade && t.State == RunState.Running)) await Task.Delay(50, stopping.Token);
                using (var scope = Server.Services.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(userId, after.Request(clusterId, new EmptyInput(), "Queue during maintenance", [machineId]));
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
}
