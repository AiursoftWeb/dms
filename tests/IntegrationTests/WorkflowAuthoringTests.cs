using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.SDK;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class WorkflowAuthoringTests : TestBase
{
    private Task<T> DatabaseAsync<T>(Func<TemplateDbContext, Task<T>> action) =>
        Server!.Services.GetRequiredService<ControllerGate>().RunAsync(async () =>
        {
            using var scope = Server.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<TemplateDbContext>());
        });

    private async Task<(Guid Cluster, string User, string Approver, Guid[] Machines, string[] Keys, string Key)> ArrangeAsync()
    {
        AuthoringExamples.RegisterMachines(Server!.Services.GetRequiredService<MachineWorkflowRegistry>());
        AuthoringExamples.RegisterCluster(Server.Services.GetRequiredService<ClusterWorkflowRegistry>());
        return await DatabaseAsync(async db =>
        {
            var user = new User { UserName = "requester" + Guid.NewGuid().ToString("N"), Email = "requester@example.test", DisplayName = "Requester" }; db.Users.Add(user);
            var approver = new User { Id = Guid.NewGuid().ToString(), UserName = "reviewer", DisplayName = "Reviewer" };
            var cluster = new Cluster { Name = "Authoring" };
            var keys = Enumerable.Range(0, 2).Select(_ => DmsKeyAuthenticationHandler.CreateKey(true)).ToArray();
            var machines = keys.Select((key, i) => new Machine { ClusterId = cluster.Id, Name = $"example-{i}", KeyHash = DmsKeyAuthenticationHandler.Hash(key) }).ToArray();
            var personal = DmsKeyAuthenticationHandler.CreateKey(false);
            db.Users.Add(approver); db.Clusters.Add(cluster); db.Machines.AddRange(machines);
            db.PersonalApiKeys.Add(new PersonalApiKey { UserId = user.Id, Name = "Authoring", KeyHash = DmsKeyAuthenticationHandler.Hash(personal), ExpiresAt = DateTime.UtcNow.AddHours(1) });
            foreach (var risk in Enum.GetValues<RiskLevel>())
            {
                DmsTestPermissions.Risk(db, user.Id, risk, request: true, automatic: false, approve: false);
                DmsTestPermissions.Risk(db, approver.Id, risk, request: false, automatic: false, approve: true);
            }
            await db.SaveChangesAsync();
            return (cluster.Id, user.Id, approver.Id, machines.Select(m => m.Id).ToArray(), keys, personal);
        });
    }

    private async Task<List<WorkflowRun>> WaitForTreeAsync(Guid root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var tree = await DatabaseAsync(db => db.WorkflowRuns.AsNoTracking().Where(r => r.RootId == root).ToListAsync(timeout.Token));
            if (tree.Single(r => r.Id == root).State.Terminal()) return tree;
            await Task.Delay(80, timeout.Token);
        }
    }

    [TestMethod]
    public async Task NewWorkflowUsesTypedSdkApprovalFrozenInputAndTwoAgentsWithoutEngineChanges()
    {
        var setup = await ArrangeAsync();
        using var provider = new ServiceCollection().AddLogging().AddAiurProtocolClient().BuildServiceProvider();
        var client = new DmsApiClient(provider.GetRequiredService<AiurProtocolClient>(), Http.BaseAddress!.ToString(), setup.Key);
        var input = new RepeatInput(7, 3, ProbeFailure: true);
        var requestId = Guid.NewGuid();
        var response = await client.SubmitAsync(AuthoringExamples.Total, setup.Cluster, input, "Typed authoring example", allMachines: true, requestId: requestId);
        Assert.AreEqual(0, (int)response.Code);
        var box = await DatabaseAsync(db => db.Lockboxes.AsNoTracking().SingleAsync(l => l.ClientRequestId == requestId));
        Assert.AreEqual(DecisionState.Pending, box.Decision); Assert.AreEqual(RiskLevel.Medium, box.Risk);
        Assert.AreEqual(AuthoringExamples.Total.Version, box.Version); Assert.IsNull(box.OperationId);
        Assert.AreEqual(input, JsonSerializer.Deserialize<RepeatInput>(JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!.Input));
        await DatabaseAsync(async db => { db.Machines.Add(new Machine { ClusterId = setup.Cluster, Name = "Late", KeyHash = "late" }); await db.SaveChangesAsync(); return true; });
        using (var scope = Server!.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
            var changed = AuthoringExamples.Total.Request(setup.Cluster, input with { Copies = 4 }, "Typed authoring example", allMachines: true, requestId: requestId);
            Assert.AreEqual(409, (await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, changed))).StatusCode);
            await service.DecideAsync(setup.Approver, box.Id, true, "Only the two original machines");
        }
        var paths = setup.Machines.Select(_ => Directory.CreateTempSubdirectory("dms-authoring-").FullName).ToArray();
        var journals = paths.Select(path => new TaskJournal(path)).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var agents = setup.Keys.Select((key, i) =>
        {
            var registry = new MachineWorkflowRegistry(); AuthoringExamples.RegisterMachines(registry);
            return new AgentClient(Http.BaseAddress!, key, journals[i], registry).RunAsync(stop.Token);
        }).ToArray();
        try
        {
            var tree = await WaitForTreeAsync(box.WorkflowId);
            var parent = tree.Single(r => r.Definition == AuthoringExamples.Total.Name);
            Assert.AreEqual(RunState.Succeeded, parent.State, parent.Result);
            Assert.AreEqual(AuthoringExamples.Total.Serialize(input), parent.Input);
            var result = AuthoringExamples.Total.ReadResult(parent.Result);
            Assert.AreEqual(42L, result.Units); Assert.AreEqual(2, result.Machines);
            CollectionAssert.AreEquivalent(journals.Select(j => j.InstallationId).ToList(), result.Installations);
            Assert.HasCount(1, await DatabaseAsync(db => db.Lockboxes.Where(l => l.ClusterId == setup.Cluster).ToListAsync()));
            Assert.AreEqual(box.WorkflowId, parent.RootId); Assert.AreEqual(box.WorkflowId, parent.ParentId);
            Assert.AreEqual(tree.Count, tree.Select(r => r.Id).Distinct().Count()); Assert.IsTrue(tree.All(r => r.Id != Guid.Empty));
            var failed = tree.Where(r => r.State == RunState.Failed).ToList(); Assert.HasCount(2, failed);
            Assert.IsTrue(failed.All(r => r.Definition == AuthoringExamples.Unit.Name && r.ParentId == parent.Id));
            var remote = tree.Where(r => r.Definition == AuthoringExamples.Repeat.Name).ToList(); Assert.HasCount(2, remote);
            Assert.IsTrue(remote.All(r => setup.Machines.Contains(r.MachineId!.Value) && r.ParentId == parent.Id && r.Version == AuthoringExamples.Repeat.Version));
            var local = tree.Where(r => r.Definition == AuthoringExamples.Unit.Name && r.State == RunState.Succeeded).ToList(); Assert.HasCount(6, local);
            Assert.IsTrue(local.All(r => remote.Any(p => p.Id == r.ParentId && p.MachineId == r.MachineId) && r.Version == AuthoringExamples.Unit.Version));
            foreach (var format in new[] { "table", "json", "csv" })
            {
                var cli = await Client.DmsCommandApp.Create().TestRunAsync(["operation", "get-callstack", "--id", box.WorkflowId.ToString(), "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key, "--output-format", format]);
                Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
                foreach (var run in tree) StringAssert.Contains(cli.StdOut, run.Id.ToString());
            }
        }
        finally
        {
            await stop.CancelAsync(); try { await Task.WhenAll(agents); } catch (OperationCanceledException) { }
            foreach (var journal in journals) journal.Dispose();
            foreach (var path in paths) Directory.Delete(path, true);
        }
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("{\"Units\":7,\"Copies\":0}")]
    [DataRow("{\"Units\":7,\"Copies\":2,\"Unknown\":true}")]
    [DataRow("{\"Units\":\"seven\",\"Copies\":2}")]
    public async Task InvalidStandardInputCannotCreateLockbox(string input)
    {
        var setup = await ArrangeAsync();
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        var request = new OperationRequest { ClusterId = setup.Cluster, Workflow = AuthoringExamples.Total.Name, AllMachines = true, Input = input, Reason = "Invalid input" };
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.PostAsJsonAsync("/api/v1/dms/lockboxes", request)).StatusCode);
        Assert.AreEqual(0, await DatabaseAsync(db => db.Lockboxes.CountAsync(l => l.ClusterId == setup.Cluster)));
    }

    [TestMethod]
    [DataRow("internal.process")]
    [DataRow("example.unit")]
    [DataRow("unregistered")]
    public async Task InternalAndUnknownDefinitionsCannotBeSubmittedEvenWithAllRiskGrants(string name)
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var request = new OperationRequest { ClusterId = setup.Cluster, Workflow = name, MachineIds = [setup.Machines[0]], Input = "{\"Units\":1}", Reason = "Cannot expose internal execution" };
        Assert.AreEqual(400, (await Assert.ThrowsAsync<DmsException>(() => scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User, request))).StatusCode);
        Assert.AreEqual(0, await DatabaseAsync(db => db.Lockboxes.CountAsync(l => l.ClusterId == setup.Cluster)));
    }

    [TestMethod]
    public async Task ApprovalDoesNotSilentlySwitchTheSubmittedDefinitionVersion()
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var box = await service.SubmitAsync(setup.User,
            AuthoringExamples.Repeat.Request(setup.Cluster, new RepeatInput(9, 2), "Versioned request", [setup.Machines[0]]));
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        box.Version = "previous-version";
        await db.SaveChangesAsync();
        var failure = await Assert.ThrowsAsync<DmsException>(() => service.DecideAsync(setup.Approver, box.Id, true, "Must not switch versions"));
        Assert.AreEqual(409, failure.StatusCode);
        Assert.AreEqual(DecisionState.Pending, box.Decision); Assert.IsNull(box.OperationId);
    }

    [TestMethod]
    public async Task PublicMachineDefinitionSuppliesLowRiskAndApprovedInput()
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var box = await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User,
            AuthoringExamples.Repeat.Request(setup.Cluster, new RepeatInput(9, 2), "Machine entry", [setup.Machines[0]]));
        Assert.AreEqual(RiskLevel.Low, box.Risk); Assert.AreEqual("repeat-1", box.Version);
        Assert.AreEqual(AuthoringExamples.Repeat.Serialize(new RepeatInput(9, 2)), JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!.Input);
    }
}
