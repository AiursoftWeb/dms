using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class AgentUpgradeIntegrationTests : TestBase
{
    [TestMethod]
    public async Task CliFreezesReleaseAndHighRiskApprovalRejectsAssetChanges()
    {
        var environment = Server!.Services.GetRequiredService<IWebHostEnvironment>();
        var directory = Path.Combine(environment.ContentRootPath, "agent-assets");
        Directory.CreateDirectory(directory);
        var metadata = Path.Combine(directory, "releases.json");
        Assert.IsFalse(File.Exists(metadata));
        Assert.AreEqual(System.Net.HttpStatusCode.ServiceUnavailable, (await Http.GetAsync("/agent/releases")).StatusCode);
        var release = new AgentRelease("linux-x64", "1.0.16.0", new string('a', 64), new string('b', 64));
        var input = new AgentUpgradeInput([release, release with { Architecture = "linux-arm64" }]);
        File.WriteAllText(metadata, JsonSerializer.Serialize(input.Releases));
        var key = DmsKeyAuthenticationHandler.CreateKey(false);
        Guid clusterId; Guid machineId; string userId;
        try
        {
            using (var scope = Server.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
                var user = new User { Id = Guid.NewGuid().ToString(), UserName = "upgrade-" + Guid.NewGuid().ToString("N"), DisplayName = "Upgrade operator" };
                db.Users.Add(user);
                userId = user.Id; user.CanApproveOwnRequests = false;
                var cluster = new Cluster { Name = "Upgrade approval" }; clusterId = cluster.Id;
                var machine = new Machine { ClusterId = cluster.Id, Name = "Upgrade target" }; machineId = machine.Id;
                db.Clusters.Add(cluster); db.Machines.Add(machine);
                db.PersonalApiKeys.Add(new PersonalApiKey { UserId = user.Id, Name = "Upgrade CLI", KeyHash = DmsKeyAuthenticationHandler.Hash(key), ExpiresAt = DateTime.UtcNow.AddHours(1) });
                db.ClusterGrants.Add(new ClusterGrant { UserId = user.Id, ClusterId = cluster.Id, Risk = RiskLevel.High, CanRequest = true, CanApprove = true });
                await db.SaveChangesAsync();
            }
            var cli = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "agent", "upgrade", "--cluster-id", clusterId.ToString(),
                "--machine-id", machineId.ToString(), "--reason", "Upgrade to the frozen Controller release", "--server", Http.BaseAddress!.ToString(), "--api-key", key]);
            Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
            using var inspection = Server.Services.CreateScope();
            var database = inspection.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var request = await database.Lockboxes.SingleAsync(l => l.ClusterId == clusterId);
            Assert.AreEqual(RiskLevel.High, request.Risk);
            Assert.AreEqual(DecisionState.Pending, request.Decision);
            CollectionAssert.AreEqual(new List<Guid> { machineId }, JsonSerializer.Deserialize<List<Guid>>(request.TargetMachines)!);
            var parameters = JsonSerializer.Deserialize<OperationRequest>(request.Parameters)!;
            CollectionAssert.AreEqual(input.Releases, MachineWorkflows.AgentUpgrade.ReadInput(parameters.Input).Releases);
            var service = inspection.ServiceProvider.GetRequiredService<WorkflowService>();
            Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => service.DecideAsync(userId, request.Id, true, "No implicit self approval"))).StatusCode);
            (await database.Users.SingleAsync(u => u.Id == userId)).CanApproveOwnRequests = true;
            await database.SaveChangesAsync();
            File.WriteAllText(metadata, JsonSerializer.Serialize(input.Releases.Select(r => r with { ArchiveSha256 = new string('c', 64) })));
            Assert.AreEqual(409, (await Assert.ThrowsAsync<DmsException>(() => service.DecideAsync(userId, request.Id, true, "Assets changed"))).StatusCode);
            Assert.AreEqual(DecisionState.Pending, request.Decision);
            Assert.AreEqual(request.Id, (await service.SubmitAsync(userId, parameters)).Id, "A same-ID retry retains the original release.");
            File.WriteAllText(metadata, JsonSerializer.Serialize(input.Releases));
            var approved = await service.DecideAsync(userId, request.Id, true, "Approved exact release");
            Assert.AreEqual(DecisionState.Approved, approved.Decision);
            var root = await database.WorkflowRuns.SingleAsync(r => r.Id == approved.WorkflowId);
            Assert.AreEqual(root.Id, root.RootId);
            Assert.AreEqual(WorkflowNames.Lockbox, root.Definition);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            WorkflowRun? machineTask;
            do
            {
                machineTask = await database.WorkflowRuns.SingleOrDefaultAsync(r => r.LockboxId == approved.Id && r.IsMachineTask, timeout.Token);
                if (machineTask == null) await Task.Delay(50, timeout.Token);
            } while (machineTask == null);
            machineTask.State = RunState.Running;
            machineTask.StartedAt = DateTime.UtcNow.AddMinutes(-21);
            await database.SaveChangesAsync(timeout.Token);
            while ((await service.GetWorkflowAsync(userId, root.Id)).State != RunState.NeedsVerification) await Task.Delay(100, timeout.Token);
            using var final = Server.Services.CreateScope();
            var uncertain = await final.ServiceProvider.GetRequiredService<TemplateDbContext>().WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == machineTask.Id, timeout.Token);
            Assert.AreEqual(RunState.NeedsVerification, uncertain.State);
            Assert.IsNull(uncertain.FinishedAt);
            Assert.AreEqual(root.Id, uncertain.RootId);
        }
        finally { File.Delete(metadata); }
    }
}
