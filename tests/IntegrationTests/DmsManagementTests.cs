using System.Net;
using System.Net.Http.Headers;
using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.SDK;
using Aiursoft.DMS.Services.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class DmsManagementTests : TestBase
{
    private async Task<(string Manager, string ManagerKey, string Employee, string EmployeeKey)> ArrangeAsync()
    {
        using var scope = Server!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var suffix = Guid.NewGuid().ToString("N");
        var manager = new User { UserName = "manager" + suffix, Email = "manager" + suffix + "@example.test", DisplayName = "Manager" };
        var employee = new User { UserName = "employee" + suffix, Email = "employee" + suffix + "@example.test", DisplayName = "Employee" };
        var createdManager = await users.CreateAsync(manager); var createdEmployee = await users.CreateAsync(employee);
        Assert.IsTrue(createdManager.Succeeded, string.Join("; ", createdManager.Errors.Select(e => e.Description)));
        Assert.IsTrue(createdEmployee.Succeeded, string.Join("; ", createdEmployee.Errors.Select(e => e.Description)));
        Assert.IsTrue((await users.AddClaimAsync(manager, new System.Security.Claims.Claim(AppPermissions.Type, "CanManageDmsAccess"))).Succeeded);
        Assert.IsTrue((await users.AddClaimAsync(employee, new System.Security.Claims.Claim(AppPermissions.Type, AppPermissionNames.CanReadUsers))).Succeeded);
        var managerKey = DmsKeyAuthenticationHandler.CreateKey(false); var employeeKey = DmsKeyAuthenticationHandler.CreateKey(false);
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        db.PersonalApiKeys.AddRange(
            new PersonalApiKey { UserId = manager.Id, Name = "Manager", KeyHash = DmsKeyAuthenticationHandler.Hash(managerKey), ExpiresAt = DateTime.UtcNow.AddHours(1) },
            new PersonalApiKey { UserId = employee.Id, Name = "Employee", KeyHash = DmsKeyAuthenticationHandler.Hash(employeeKey), ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        return (manager.Id, managerKey, employee.Id, employeeKey);
    }

    private async Task<T> CliAsync<T>(string key, params string[] arguments)
    {
        var cli = await Client.DmsCommandApp.Create().TestRunAsync([.. arguments, "--server", Http.BaseAddress!.ToString(), "--api-key", key, "--output-format", "json"]);
        Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
        return JObject.Parse(cli.StdOut)["Data"]!.ToObject<T>()!;
    }

    [TestMethod]
    public async Task WebAndApiShareClusterValidation()
    {
        var setup = await ArrangeAsync();
        await LoginAsAdmin();
        var name = "Web-" + Guid.NewGuid();
        Assert.AreEqual(HttpStatusCode.Found, (await PostForm("/DmsAccess/CreateCluster", new Dictionary<string, string> { ["name"] = name }, "/DmsAccess")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await PostForm("/DmsAccess/CreateCluster", new Dictionary<string, string> { ["name"] = name }, "/DmsAccess")).StatusCode);
        Assert.IsTrue((await CliAsync<List<ClusterSummary>>(setup.ManagerKey, "cluster", "list")).Any(c => c.Name == name));
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.ManagerKey);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Http.PostAsJsonAsync("/api/v1/dms/clusters", new CreateClusterInput(Guid.NewGuid(), name))).StatusCode);
    }

    [TestMethod]
    public async Task ManagementCliCreatesListsRenamesAndDropsClustersWithoutImplicitBusinessGrants()
    {
        var setup = await ArrangeAsync();
        var identity = await CliAsync<CurrentUser>(setup.ManagerKey, "access", "whoami");
        Assert.AreEqual(setup.Manager, identity.Id); Assert.IsFalse(identity.CanApproveOwnRequests);
        StringAssert.StartsWith(identity.UserName, "manager");
        CollectionAssert.Contains(identity.Permissions, "CanManageDmsAccess");
        var id = Guid.NewGuid();
        var name = "Production-" + id;
        var created = await CliAsync<ClusterSummary>(setup.ManagerKey, "cluster", "create", "--id", id.ToString(), "--name", "  " + name + "  ");
        Assert.AreEqual(id, created.Id); Assert.AreEqual(name, created.Name);
        var retry = await CliAsync<ClusterSummary>(setup.ManagerKey, "cluster", "create", "--id", id.ToString(), "--name", name);
        Assert.AreEqual(id, retry.Id);
        Assert.IsTrue((await CliAsync<List<ClusterSummary>>(setup.ManagerKey, "cluster", "list")).Any(c => c.Id == id));
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.EmployeeKey);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/v1/dms/clusters")).StatusCode);
        var snapshot = await CliAsync<AccessSnapshot>(setup.ManagerKey, "access", "get");
        Assert.AreEqual(name, snapshot.Clusters.Single(c => c.Id == id).Name);
        Assert.IsFalse(snapshot.Machines.Any(m => m.ClusterId == id));
        foreach (var format in new[] { "table", "csv" })
        {
            var cli = await Client.DmsCommandApp.Create().TestRunAsync(["cluster", "list", "--server", Http.BaseAddress!.ToString(), "--api-key", setup.ManagerKey, "--output-format", format]);
            Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr); StringAssert.Contains(cli.StdOut, id.ToString()); StringAssert.Contains(cli.StdOut, name);
        }
        var renamed = await CliAsync<ClusterSummary>(setup.ManagerKey, "cluster", "rename", "--cluster-id", id.ToString(), "--name", "Primary-" + id);
        Assert.AreEqual("Primary-" + id, renamed.Name);
        await CliAsync<ClusterSummary>(setup.ManagerKey, "cluster", "drop", "--cluster-id", id.ToString());
        Assert.IsFalse((await CliAsync<List<ClusterSummary>>(setup.ManagerKey, "cluster", "list")).Any(c => c.Id == id));
        using var check = Server!.Services.CreateScope(); var db = check.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(0, await db.Lockboxes.CountAsync(l => l.ClusterId == id)); Assert.AreEqual(0, await db.WorkflowRuns.CountAsync(r => r.ClusterId == id));
        foreach (var action in new[] { "access.identity", "access.inspect", "access.cluster.create", "access.cluster.rename", "access.cluster.drop" })
            Assert.IsTrue(await db.AuditEvents.AnyAsync(a => a.ActorId == setup.Manager && a.Action == action));
    }

    [TestMethod]
    public async Task ReadPermissionAndAgentKeysCannotMutateControllerAccess()
    {
        var setup = await ArrangeAsync();
        int clustersBefore, grantsBefore;
        using (var before = Server!.Services.CreateScope())
        {
            var beforeDb = before.ServiceProvider.GetRequiredService<TemplateDbContext>();
            clustersBefore = await beforeDb.Clusters.CountAsync(); grantsBefore = await beforeDb.ClusterGrants.CountAsync();
        }
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.EmployeeKey);
        foreach (var request in new[]
        {
            new HttpRequestMessage(HttpMethod.Get, "/api/v1/dms/access"),
            new HttpRequestMessage(HttpMethod.Post, "/api/v1/dms/clusters") { Content = JsonContent.Create(new CreateClusterInput(Guid.NewGuid(), "Forbidden")) },
            new HttpRequestMessage(HttpMethod.Put, $"/api/v1/dms/clusters/{Guid.NewGuid()}") { Content = JsonContent.Create(new ClusterNameInput("Forbidden")) },
            new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/dms/clusters/{Guid.NewGuid()}"),
            new HttpRequestMessage(HttpMethod.Post, $"/api/v1/dms/access/machines/{Guid.NewGuid()}/key")
        })
        {
            using (request) using (var response = await Http.SendAsync(request)) Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }
        var own = await CliAsync<CurrentUser>(setup.EmployeeKey, "access", "whoami"); Assert.AreEqual(setup.Employee, own.Id);
        using var scope = Server!.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(clustersBefore, await db.Clusters.CountAsync()); Assert.AreEqual(grantsBefore, await db.ClusterGrants.CountAsync());
        Assert.IsFalse((await db.Users.SingleAsync(u => u.Id == setup.Employee)).CanApproveOwnRequests);
    }

    [TestMethod]
    public async Task KeyRotationDoesNotExposeStoredSecrets()
    {
        var setup = await ArrangeAsync();
        var cluster = await CliAsync<ClusterSummary>(setup.ManagerKey, "cluster", "create", "--name", "Operations");
        Guid machine;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>(); var row = new Machine { ClusterId = cluster.Id, Name = "Registered fixture" };
            db.Machines.Add(row); await db.SaveChangesAsync(); machine = row.Id;
        }
        var first = await CliAsync<AgentKeyResult>(setup.ManagerKey, "access", "issue-agent-key", "--machine-id", machine.ToString());
        var second = await CliAsync<AgentKeyResult>(setup.ManagerKey, "access", "issue-agent-key", "--machine-id", machine.ToString());
        Assert.AreNotEqual(first.Key, second.Key); Assert.AreEqual(machine, second.MachineId);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Key);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/agent/channel")).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.Key);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/v1/dms/access/identity")).StatusCode);
        var snapshotWithMachine = await CliAsync<AccessSnapshot>(setup.ManagerKey, "access", "get");
        var machineInfo = snapshotWithMachine.Machines.Single(m => m.Id == machine);
        Assert.AreEqual("Registered fixture", machineInfo.Name); Assert.IsFalse(machineInfo.Disabled);
        var snapshotJson = Newtonsoft.Json.JsonConvert.SerializeObject(snapshotWithMachine);
        Assert.DoesNotContain("KeyHash", snapshotJson); Assert.DoesNotContain(second.Key, snapshotJson);
        using var check = Server!.Services.CreateScope(); var verify = check.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(DmsKeyAuthenticationHandler.Hash(second.Key), (await verify.Machines.SingleAsync(m => m.Id == machine)).KeyHash);
        Assert.IsFalse(await verify.AuditEvents.AnyAsync(a => a.Detail.Contains(second.Key) || a.Detail.Contains(first.Key)));
        Assert.AreEqual(0, await verify.Lockboxes.CountAsync(l => l.ClusterId == cluster.Id));
    }

    [TestMethod]
    public async Task ConflictingNamesInvalidInputsAndNonemptyClusterDeletionAreRejected()
    {
        var setup = await ArrangeAsync();
        using var provider = new ServiceCollection().AddLogging().AddAiurProtocolClient().BuildServiceProvider();
        var client = new DmsApiClient(provider.GetRequiredService<AiurProtocolClient>(), Http.BaseAddress!.ToString(), setup.ManagerKey);
        var cluster = await CliAsync<ClusterSummary>(setup.ManagerKey, "cluster", "create", "--name", "Retained");
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.ManagerKey);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Http.PostAsJsonAsync("/api/v1/dms/clusters", new CreateClusterInput(Guid.NewGuid(), "retained"))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Http.PostAsJsonAsync("/api/v1/dms/clusters", new CreateClusterInput(cluster.Id, "Different"))).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.PostAsJsonAsync("/api/v1/dms/clusters", new CreateClusterInput(Guid.Empty, "Empty ID"))).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.PostAsJsonAsync("/api/v1/dms/clusters", new CreateClusterInput(Guid.NewGuid(), "  "))).StatusCode);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var run = new WorkflowRun { ClusterId = cluster.Id, Definition = WorkflowNames.Lockbox, State = RunState.Succeeded }; run.RootId = run.Id;
            db.WorkflowRuns.Add(run); await db.SaveChangesAsync();
        }
        Assert.AreEqual(HttpStatusCode.Conflict, (await Http.DeleteAsync($"/api/v1/dms/clusters/{cluster.Id}")).StatusCode);
        var rows = JObject.FromObject(await client.ListClustersAsync())["Data"]!.ToObject<List<ClusterSummary>>()!;
        Assert.IsTrue(rows.Any(c => c.Id == cluster.Id));
    }
}
