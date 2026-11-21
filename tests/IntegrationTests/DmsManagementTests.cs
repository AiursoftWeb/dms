using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
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
        DmsTestPermissions.DeactivatePreviousMachines(db);
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
    public async Task ManagementIdentityDoesNotImplicitlyGrantOperationalPermissions()
    {
        var setup = await ArrangeAsync();
        var identity = await CliAsync<CurrentUser>(setup.ManagerKey, "access", "whoami");
        Assert.AreEqual(setup.Manager, identity.Id);
        StringAssert.StartsWith(identity.UserName, "manager");
        Assert.IsFalse(identity.CanApproveOwnRequests);
        Assert.IsTrue(identity.RiskPermissions.All(p => !p.CanRequest && !p.AutoApprove && !p.CanApprove));
        CollectionAssert.Contains(identity.Permissions, "CanManageDmsAccess");
        var snapshot = await CliAsync<AccessSnapshot>(setup.ManagerKey, "access", "get");
        Assert.IsNotNull(snapshot.Machines);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.ManagerKey);
        var request = ClusterWorkflows.GetMachines.Request(new EmptyInput(), "Must require Safe permission");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.PostAsJsonAsync("/api/v1/dms/lockboxes", request)).StatusCode);
    }

    [TestMethod]
    public async Task RemovedResourceRoutesAndCliArgumentsAreUnavailable()
    {
        var setup = await ArrangeAsync();
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.ManagerKey);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync("/api/v1/dms/clusters")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.PostAsJsonAsync("/api/v1/dms/clusters", new { Name = "Removed" })).StatusCode);
        foreach (var args in new[] { new[] { "cluster", "list" }, new[] { "operation", "list-definitions", "--cluster-id", Guid.NewGuid().ToString() } })
        {
            var result = await Client.DmsCommandApp.Create().TestRunAsync(args);
            Assert.AreNotEqual(0, result.ProgramReturn);
        }
        await LoginAsAdmin();
        Assert.DoesNotContain("Create cluster", await Http.GetStringAsync("/DmsAccess"));
    }

    [TestMethod]
    public async Task WebKeyReplacementRequiresReviewOfTheCurrentMachineAndCannotBeRepeated()
    {
        await LoginAsAdmin();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var oldKey = DmsKeyAuthenticationHandler.CreateKey(true);
        var machine = new Machine { Name = "Demo Global", KeyHash = DmsKeyAuthenticationHandler.Hash(oldKey) };
        var other = new Machine { Name = "Demo GX10", KeyHash = DmsKeyAuthenticationHandler.Hash(DmsKeyAuthenticationHandler.CreateKey(true)) };
        db.Machines.AddRange(machine, other);
        await db.SaveChangesAsync();

        var page = await Http.GetStringAsync("/DmsAccess");
        StringAssert.Contains(page, "Replace the Agent connection key");
        StringAssert.Contains(page, "View replacement steps");
        Assert.DoesNotContain("name=\"machineId\"", page);
        Assert.DoesNotContain("confirmationToken", page);
        var detail = await Http.GetStringAsync($"/Dms/Machine/{machine.Id}");
        StringAssert.Contains(detail, "Workflow history");
        var path = $"/DmsAccess/RotateAgentKey/{machine.Id}";
        StringAssert.Contains(detail, path);
        var review = await Http.GetStringAsync(path);
        StringAssert.Contains(review, machine.Name);
        StringAssert.Contains(review, "This immediately invalidates the current key.");
        Assert.DoesNotContain(oldKey, review);
        Assert.DoesNotContain(machine.KeyHash, review);
        var token = Regex.Match(review, """name="confirmationToken" value="([^"]+)""").Groups[1].Value;
        Assert.IsFalse(string.IsNullOrWhiteSpace(token));

        Dictionary<string, string> Confirmation(string name, string value) => new()
        {
            ["confirmationName"] = name,
            ["confirmationToken"] = value
        };
        var wrongName = await PostForm(path, Confirmation(other.Name, token), path);
        StringAssert.Contains(await wrongName.Content.ReadAsStringAsync(), "The machine name did not match.");
        var wrongMachine = await PostForm($"/DmsAccess/RotateAgentKey/{other.Id}", Confirmation(other.Name, token), path);
        StringAssert.Contains(await wrongMachine.Content.ReadAsStringAsync(), "This confirmation expired");
        var withoutReview = await PostForm(path, Confirmation(machine.Name, ""), path);
        StringAssert.Contains(await withoutReview.Content.ReadAsStringAsync(), "This confirmation expired");
        Assert.AreEqual(HttpStatusCode.NotFound, (await PostForm("/DmsAccess/AgentKey", new() { ["machineId"] = machine.Id.ToString() }, path)).StatusCode);
        Assert.AreEqual(0, await db.AuditEvents.CountAsync(a => a.Action == "key.agent.rotate" && a.ResourceId == machine.Id));

        var replaced = await PostForm(path, Confirmation(machine.Name, token), path);
        Assert.AreEqual(HttpStatusCode.OK, replaced.StatusCode);
        var result = await replaced.Content.ReadAsStringAsync();
        StringAssert.Contains(result, "Reconnect the Agent");
        var newKey = Regex.Match(result, @"dmsa_[0-9A-F]{64}").Value;
        Assert.IsFalse(string.IsNullOrWhiteSpace(newKey));
        Assert.IsTrue(replaced.Headers.CacheControl?.NoStore);
        var repeated = await PostForm(path, Confirmation(machine.Name, token), path);
        var repeatedOutput = await repeated.Content.ReadAsStringAsync();
        StringAssert.Contains(repeatedOutput, "This confirmation expired");
        Assert.DoesNotContain(newKey, repeatedOutput);
        Assert.DoesNotContain(newKey, await Http.GetStringAsync("/DmsAccess/Keys"));

        Assert.AreEqual(1, await db.AuditEvents.CountAsync(a => a.Action == "key.agent.rotate" && a.ResourceId == machine.Id));
        var saved = await db.Machines.AsNoTracking().SingleAsync(m => m.Id == machine.Id);
        Assert.IsTrue(DmsKeyAuthenticationHandler.Hash(newKey) == saved.KeyHash);
        var untouched = await db.Machines.AsNoTracking().SingleAsync(m => m.Id == other.Id);
        Assert.IsTrue(other.KeyHash == untouched.KeyHash);

        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldKey);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/agent/channel")).StatusCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {newKey}");
        await socket.ConnectAsync(new UriBuilder(Http.BaseAddress!) { Scheme = "ws", Path = "/api/agent/channel" }.Uri, timeout.Token);
        var installationId = Guid.NewGuid();
        await WebSocketJson.SendAsync(socket, new AgentMessage { Kind = "hello", Version = "fixture", InstallationId = installationId }, timeout.Token);
        while (!await db.Machines.AsNoTracking().AnyAsync(m => m.Id == machine.Id && m.InstallationId == installationId, timeout.Token))
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    [TestMethod]
    public async Task ReadPermissionAndAgentKeysCannotMutateControllerAccess()
    {
        var setup = await ArrangeAsync();
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.EmployeeKey);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/v1/dms/access")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.PostAsJsonAsync("/api/v1/dms/access/join-keys", new JoinKeyInput("Forbidden"))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.PostAsJsonAsync($"/api/v1/dms/access/machines/{Guid.NewGuid()}/key", new { })).StatusCode);
        var own = await CliAsync<CurrentUser>(setup.EmployeeKey, "access", "whoami");
        Assert.AreEqual(setup.Employee, own.Id);
        Assert.IsFalse(own.CanApproveOwnRequests);
    }

    [TestMethod]
    public async Task KeyRotationDoesNotExposeStoredSecrets()
    {
        var setup = await ArrangeAsync();
        Guid machine;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>(); var row = new Machine { Name = "Registered fixture" };
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
        Assert.AreEqual(0, await verify.Lockboxes.CountAsync(l => l.RequesterId == setup.Manager));
    }

}
