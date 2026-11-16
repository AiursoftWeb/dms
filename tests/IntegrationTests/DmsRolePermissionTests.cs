using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class DmsRolePermissionTests : TestBase
{
    private async Task<(string User, string Key, string Role, Guid First, Guid Second)> ArrangeAsync(params string[] permissions)
    {
        using var scope = Server!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var user = new User { UserName = "operator" + Guid.NewGuid().ToString("N"), Email = Guid.NewGuid().ToString("N") + "@example.test", DisplayName = "Operator" };
        Assert.IsTrue((await users.CreateAsync(user)).Succeeded);
        var role = new IdentityRole("operators" + Guid.NewGuid().ToString("N"));
        Assert.IsTrue((await roles.CreateAsync(role)).Succeeded);
        foreach (var permission in permissions)
            Assert.IsTrue((await roles.AddClaimAsync(role, new Claim(AppPermissions.Type, permission))).Succeeded);
        Assert.IsTrue((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var first = new Cluster { Name = "First" }; var second = new Cluster { Name = "Second" };
        db.Clusters.AddRange(first, second);
        db.Machines.AddRange(new Machine { ClusterId = first.Id, Name = "First machine" }, new Machine { ClusterId = second.Id, Name = "Second machine" });
        // Neither legacy data source may grant any authority after the switch.
        user.CanApproveOwnRequests = true;
        foreach (var risk in Enum.GetValues<RiskLevel>())
            db.ClusterGrants.Add(new ClusterGrant { UserId = user.Id, ClusterId = first.Id, Risk = risk, CanRequest = true, AutoApprove = true, CanApprove = true });
        var key = DmsKeyAuthenticationHandler.CreateKey(false);
        db.PersonalApiKeys.Add(new PersonalApiKey { UserId = user.Id, Name = "Role permissions", KeyHash = DmsKeyAuthenticationHandler.Hash(key), ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        return (user.Id, key, role.Id, first.Id, second.Id);
    }

    private async Task<CurrentUser> WhoamiAsync(string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dms/access/identity");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return JObject.Parse(await response.Content.ReadAsStringAsync())["Data"]!.ToObject<CurrentUser>()!;
    }

    private async Task SetRoleAsync(string roleId, string permission, bool enabled)
    {
        using var scope = Server!.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var role = (await roles.FindByIdAsync(roleId))!;
        var claim = new Claim(AppPermissions.Type, permission);
        Assert.IsTrue((enabled ? await roles.AddClaimAsync(role, claim) : await roles.RemoveClaimAsync(role, claim)).Succeeded);
    }

    [TestMethod]
    public async Task LegacyGrantsAndSelfFlagDoNotAuthorizeAndWhoamiNeedsNoReadPermission()
    {
        var setup = await ArrangeAsync();
        var identity = await WhoamiAsync(setup.Key);
        Assert.IsFalse(identity.CanApproveOwnRequests);
        Assert.HasCount(5, identity.RiskPermissions);
        Assert.IsTrue(identity.RiskPermissions.All(r => !r.CanRequest && !r.AutoApprove && !r.CanApprove));
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        foreach (var cluster in new[] { setup.First, setup.Second })
        {
            var request = ClusterWorkflows.GetMachines.Request(cluster, new EmptyInput(), "Must deny");
            Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.PostAsJsonAsync("/api/v1/dms/lockboxes", request)).StatusCode);
        }
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/v1/dms/clusters")).StatusCode);
        using var scope = Server!.Services.CreateScope();
        Assert.AreEqual(0, await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().Lockboxes.CountAsync(l => l.RequesterId == setup.User));
    }

    [TestMethod]
    [DataRow(RiskLevel.Safe)]
    [DataRow(RiskLevel.Low)]
    [DataRow(RiskLevel.Medium)]
    [DataRow(RiskLevel.High)]
    [DataRow(RiskLevel.VeryHigh)]
    public async Task AllRiskLevelsUseIndependentGlobalRolePermissions(RiskLevel risk)
    {
        var setup = await ArrangeAsync(AppPermissionNames.CanReadDmsOperations, DmsPermissions.AutoApprove(risk));
        OperationRequest Input(Guid cluster) => risk switch
        {
            RiskLevel.Safe => MachineWorkflows.AgentStatus.Request(cluster, new EmptyInput(), "Role test", allMachines: true),
            RiskLevel.Low => MachineWorkflows.ServiceStatus.Request(cluster, new ServiceInput("dms-test.service"), "Role test", allMachines: true),
            RiskLevel.Medium => MachineWorkflows.Restart.Request(cluster, new ServiceInput("dms-test.service"), "Role test", allMachines: true),
            RiskLevel.High => MachineWorkflows.PullerForceUpdate.Request(cluster, new PullerForceUpdateInput(), "Role test", allMachines: true),
            RiskLevel.VeryHigh => MachineWorkflows.DockerExec.Request(cluster, new DockerExecInput(new string('a', 64), ["/start.sh"]), "Role test", allMachines: true),
            _ => throw new ArgumentOutOfRangeException(nameof(risk))
        };
        using var scope = Server!.Services.CreateScope();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        Assert.IsFalse((await WhoamiAsync(setup.Key)).RiskPermissions.Single(r => r.Risk == risk).AutoApprove);
        Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => workflows.SubmitAsync(setup.User, Input(setup.First)))).StatusCode);
        await SetRoleAsync(setup.Role, DmsPermissions.Request(risk), true);
        foreach (var cluster in new[] { setup.First, setup.Second })
        {
            var request = await workflows.SubmitAsync(setup.User, Input(cluster));
            Assert.AreEqual(DecisionState.Approved, request.Decision); Assert.IsTrue(request.Automatic); Assert.IsNull(request.ApproverId);
            var catalog = await workflows.ListDefinitionsAsync(setup.User, cluster);
            Assert.IsTrue(catalog.Definitions.Single(d => d.Name == Input(cluster).Workflow).AutoApprove);
        }
        var identity = await WhoamiAsync(setup.Key);
        Assert.IsTrue(identity.RiskPermissions.Where(r => r.Risk != risk).All(r => !r.CanRequest && !r.AutoApprove && !r.CanApprove));
        await SetRoleAsync(setup.Role, DmsPermissions.Request(risk), false);
        Assert.IsFalse((await WhoamiAsync(setup.Key)).RiskPermissions.Single(r => r.Risk == risk).CanRequest);
        Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => workflows.SubmitAsync(setup.User, Input(setup.Second)))).StatusCode);
    }

    [TestMethod]
    public async Task ManualApprovalRequiresRiskPermissionAndOwnPermissionIndependently()
    {
        var setup = await ArrangeAsync(DmsPermissions.Request(RiskLevel.Safe), AppPermissionNames.CanApproveOwnWorkflow);
        using var scope = Server!.Services.CreateScope();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var input = ClusterWorkflows.GetMachines.Request(setup.First, new EmptyInput(), "Manual");
        var request = await workflows.SubmitAsync(setup.User, input);
        Assert.AreEqual(request.Id, (await workflows.SubmitAsync(setup.User, input)).Id);
        Assert.AreEqual(DecisionState.Pending, request.Decision);
        Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => workflows.DecideAsync(setup.User, request.Id, true, "Self alone"))).StatusCode);
        await SetRoleAsync(setup.Role, AppPermissionNames.CanApproveOwnWorkflow, false);
        await SetRoleAsync(setup.Role, DmsPermissions.Approve(RiskLevel.Safe), true);
        Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => workflows.DecideAsync(setup.User, request.Id, false, "Risk alone"))).StatusCode);
        await SetRoleAsync(setup.Role, AppPermissionNames.CanApproveOwnWorkflow, true);
        Assert.AreEqual(DecisionState.Approved, (await workflows.DecideAsync(setup.User, request.Id, true, "Both permissions")).Decision);
        var other = await ArrangeAsync(DmsPermissions.Approve(RiskLevel.Safe));
        var pending = await workflows.SubmitAsync(setup.User, ClusterWorkflows.GetMachines.Request(setup.Second, new EmptyInput(), "Other reviewer"));
        Assert.AreEqual(DecisionState.Denied, (await workflows.DecideAsync(other.User, pending.Id, false, "Independent reviewer")).Decision);
    }

    [TestMethod]
    public async Task WebDecisionsAndReadRevocationUseCurrentRolePermissions()
    {
        var setup = await ArrangeAsync(AppPermissionNames.CanReadDmsOperations,
            DmsPermissions.Request(RiskLevel.Safe), DmsPermissions.Approve(RiskLevel.Safe));
        using var scope = Server!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await users.FindByIdAsync(setup.User))!;
        Assert.IsTrue((await users.AddPasswordAsync(user, "Operator@123456!")).Succeeded);
        Assert.AreEqual(HttpStatusCode.Found, (await PostForm("/Account/Login", new Dictionary<string, string>
        {
            ["EmailOrUserName"] = user.UserName!, ["Password"] = "Operator@123456!"
        })).StatusCode);
        var request = await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User,
            ClusterWorkflows.GetMachines.Request(setup.First, new EmptyInput(), "Web permission test"));
        var path = "/Dms/Lockbox/" + request.Id;
        Assert.DoesNotContain("action=\"/Dms/Decide\"", await Http.GetStringAsync(path));
        await SetRoleAsync(setup.Role, AppPermissionNames.CanApproveOwnWorkflow, true);
        StringAssert.Contains(await Http.GetStringAsync(path), "action=\"/Dms/Decide\"");
        await SetRoleAsync(setup.Role, AppPermissionNames.CanReadDmsOperations, false);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/Dms")).StatusCode);
    }

    [TestMethod]
    public async Task ExistingRoleEditorUpdatesSameApiKeyAndWhoamiFormats()
    {
        var setup = await ArrangeAsync();
        await LoginAsAdmin();
        var html = await Http.GetStringAsync("/Roles/Edit/" + setup.Role);
        StringAssert.Contains(html, AppPermissionNames.CanApproveMediumRiskWorkflow);
        var form = new Dictionary<string, string>
        {
            ["Id"] = setup.Role, ["RoleName"] = (await WhoamiAsync(setup.Key)).Roles.Single(),
            ["Claims[0].Key"] = AppPermissionNames.CanRequestMediumRiskWorkflow, ["Claims[0].IsSelected"] = "true",
            ["Claims[1].Key"] = AppPermissionNames.CanApproveHighRiskWorkflow, ["Claims[1].IsSelected"] = "true",
            ["Claims[2].Key"] = AppPermissionNames.CanApproveOwnWorkflow, ["Claims[2].IsSelected"] = "true"
        };
        for (var i = 0; i < 3; i++)
        {
            var descriptor = AppPermissions.GetAllPermissions().Single(p => p.Key == form[$"Claims[{i}].Key"]);
            form[$"Claims[{i}].Name"] = descriptor.Name;
            form[$"Claims[{i}].Description"] = descriptor.Description;
        }
        Assert.AreEqual(HttpStatusCode.Found, (await PostForm("/Roles/Edit/" + setup.Role, form, "/Roles/Edit/" + setup.Role)).StatusCode);
        var identity = await WhoamiAsync(setup.Key);
        Assert.IsTrue(identity.RiskPermissions.Single(r => r.Risk == RiskLevel.Medium).CanRequest);
        Assert.IsTrue(identity.RiskPermissions.Single(r => r.Risk == RiskLevel.High).CanApprove);
        Assert.IsTrue(identity.CanApproveOwnRequests);
        foreach (var format in new[] { "table", "json", "csv" })
        {
            var result = await Client.DmsCommandApp.Create().TestRunAsync(["access", "whoami", "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key, "--output-format", format]);
            Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
            StringAssert.Contains(result.StdOut, "CanRequest"); StringAssert.Contains(result.StdOut, "AutoApprove"); StringAssert.Contains(result.StdOut, "CanApprove");
            StringAssert.Contains(result.StdOut, "CanApproveOwnRequests");
            if (format == "table") { StringAssert.Contains(result.StdOut, "Medium"); StringAssert.Contains(result.StdOut, "VeryHigh"); }
            if (format == "csv") Assert.AreEqual(6, result.StdOut.Trim().Split('\n').Length);
        }
        foreach (var path in new[] { "access/grants", "access/self-approval" })
            Assert.AreEqual(HttpStatusCode.NotFound, (await Http.PutAsJsonAsync("/api/v1/dms/" + path, new { })).StatusCode);
    }
}
