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
        DmsTestPermissions.DeactivatePreviousMachines(db);
        var first = new Machine { Name = "First machine" }; var second = new Machine { Name = "Second machine" };
        db.Machines.AddRange(first, second);
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
    public async Task WebLockboxLinksAndDecisionFormRoundTripTheSlashPrefix()
    {
        var setup = await ArrangeAsync(DmsPermissions.Request(RiskLevel.Safe));
        using var scope = Server!.Services.CreateScope();
        var box = await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User,
            MachineWorkflows.AgentStatus.Request(new EmptyInput(), "Web approval fixture", [setup.First]));
        await LoginAsAdmin();
        var listing = await Http.GetStringAsync("/Dms/Lockboxes");
        var link = System.Text.RegularExpressions.Regex.Matches(listing, "href=\"([^\"]+)\"")
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).First(url => url.Contains(box.Id.ToString(), StringComparison.OrdinalIgnoreCase));
        var detail = await Http.GetAsync(link); Assert.AreEqual(HttpStatusCode.OK, detail.StatusCode);
        var html = await detail.Content.ReadAsStringAsync();
        StringAssert.Contains(html, new LockboxId(box.Id).ToString());
        Assert.DoesNotContain("/Dms/Operation/", html);
        var response = await PostForm("/Dms/Decide", new Dictionary<string, string>
        {
            ["id"] = new LockboxId(box.Id).ToString(), ["approve"] = "false", ["reason"] = "Review without an execution"
        }, tokenUrl: link);
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await Http.GetAsync(response.Headers.Location)).StatusCode);
        using var verify = Server.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var denied = await db.Lockboxes.SingleAsync(b => b.Id == box.Id);
        Assert.AreEqual(DecisionState.Denied, denied.Decision); Assert.IsNull(denied.WorkflowId);
        Assert.AreEqual(0, await db.WorkflowRuns.CountAsync(r => r.LockboxId == box.Id));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PrefixedLockboxCanBeListedInspectedAndDecidedWithoutAnApprovalWorkflow(bool approve)
    {
        var setup = await ArrangeAsync(AppPermissionNames.CanReadDmsOperations, DmsPermissions.Request(RiskLevel.Safe),
            DmsPermissions.Approve(RiskLevel.Safe), AppPermissionNames.CanApproveOwnWorkflow);
        async Task<JToken> Cli(params string[] command)
        {
            var result = await Client.DmsCommandApp.Create().TestRunAsync([.. command, "--server", Http.BaseAddress!.ToString(),
                "--api-key", setup.Key, "--output-format", "json"]);
            Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
            return JObject.Parse(result.StdOut)["Data"]!;
        }
        var receipt = await Cli("machine", "agent", "status", "--machine-id", setup.First.ToString(), "--reason", "Independent approval fixture");
        Assert.HasCount(2, (JObject)receipt);
        var id = receipt["LockboxId"]!.Value<string>()!;
        StringAssert.StartsWith(id, "lb/"); Assert.AreEqual(JTokenType.Null, receipt["WorkflowId"]!.Type);
        var parsed = LockboxId.Parse(id);
        var detail = await Cli("lockbox", "get", "--id", id);
        Assert.AreEqual(id, detail["LockboxId"]!.Value<string>());
        var page = await Cli("lockbox", "list", "--decision", "Pending");
        Assert.IsTrue(page["Entries"]!.Any(b => b["LockboxId"]!.Value<string>() == id));
        foreach (var format in new[] { "table", "csv" })
        {
            var list = await Client.DmsCommandApp.Create().TestRunAsync(["lockbox", "list", "--decision", "Pending",
                "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key, "--output-format", format]);
            Assert.AreEqual(0, list.ProgramReturn, list.StdErr); StringAssert.Contains(list.StdOut, id);
            if (format == "table") StringAssert.Contains(list.StdOut, "Pending");
            else { StringAssert.Contains(list.StdOut.Split('\n')[0], "LockboxId"); Assert.DoesNotContain("EventId", list.StdOut.Split('\n')[0]); }
        }
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(0, await db.WorkflowRuns.CountAsync(r => r.LockboxId == parsed.Value));
        var decision = await Cli("lockbox", approve ? "approve" : "deny", "--id", id, "--reason", "Reviewed fixture");
        Assert.AreEqual((int)(approve ? DecisionState.Approved : DecisionState.Denied), decision["Decision"]!.Value<int>());
        if (approve)
        {
            var workflow = Guid.Parse(decision["WorkflowId"]!.Value<string>()!);
            var root = await db.WorkflowRuns.SingleAsync(r => r.Id == workflow);
            Assert.AreEqual(root.Id, root.RootId); Assert.IsNull(root.ParentId);
            Assert.AreEqual(WorkflowNames.Batch, root.Definition);
            Assert.IsFalse(await db.WorkflowRuns.AnyAsync(r => r.LockboxId == parsed.Value && r.Definition == WorkflowNames.Lockbox));
            var metadata = await Cli("operation", "get-workflow", "--id", workflow.ToString());
            Assert.AreEqual(id, metadata["LockboxId"]!.Value<string>());
        }
        else
        {
            Assert.AreEqual(JTokenType.Null, decision["WorkflowId"]!.Type);
            Assert.AreEqual(0, await db.WorkflowRuns.CountAsync(r => r.LockboxId == parsed.Value));
        }
        var audit = (await db.WorkflowAuditOutbox.ToListAsync()).Select(row => row.Read()).Where(e => e.LockboxId == parsed).ToList();
        var submission = audit.Single(e => e.Kind == WorkflowAuditKind.LockboxSubmitted);
        Assert.IsNull(submission.WorkflowId); Assert.IsNull(submission.RootWorkflowId); Assert.IsNull(submission.Location);
        Assert.AreEqual(DecisionState.Pending, submission.Decision);
        Assert.HasCount(1, audit.Where(e => e.Kind == (approve ? WorkflowAuditKind.LockboxApproved : WorkflowAuditKind.LockboxDenied)));
        var repeated = await Client.DmsCommandApp.Create().TestRunAsync(["lockbox", "approve", "--id", id,
            "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key]);
        Assert.AreNotEqual(0, repeated.ProgramReturn);
        Assert.AreEqual(approve ? 1 : 0, await db.WorkflowRuns.CountAsync(r => r.LockboxId == parsed.Value && r.ParentId == null));
    }

    [TestMethod]
    public async Task UnassignedPermissionsDenyRequestsAndWhoamiNeedsNoReadPermission()
    {
        var setup = await ArrangeAsync();
        var identity = await WhoamiAsync(setup.Key);
        Assert.IsFalse(identity.CanApproveOwnRequests);
        Assert.HasCount(5, identity.RiskPermissions);
        Assert.IsTrue(identity.RiskPermissions.All(r => !r.CanRequest && !r.AutoApprove && !r.CanApprove));
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        foreach (var machine in new[] { setup.First, setup.Second })
        {
            var request = MachineWorkflows.AgentStatus.Request(new EmptyInput(), "Must deny", machines: [machine]);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.PostAsJsonAsync("/api/v1/dms/lockboxes", request)).StatusCode);
        }
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/v1/dms/workflow-definitions")).StatusCode);
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
        OperationRequest Input(Guid machine) => risk switch
        {
            RiskLevel.Safe => MachineWorkflows.AgentStatus.Request(new EmptyInput(), "Role test", machines: [machine]),
            RiskLevel.Low => MachineWorkflows.ServiceStatus.Request(new ServiceInput("dms-test.service"), "Role test", machines: [machine]),
            RiskLevel.Medium => MachineWorkflows.Restart.Request(new ServiceInput("dms-test.service"), "Role test", machines: [machine]),
            RiskLevel.High => MachineWorkflows.PullerForceUpdate.Request(new PullerForceUpdateInput(), "Role test", machines: [machine]),
            RiskLevel.VeryHigh => MachineWorkflows.DockerExec.Request(new DockerExecInput(new string('a', 64), ["/start.sh"]), "Role test", machines: [machine]),
            _ => throw new ArgumentOutOfRangeException(nameof(risk))
        };
        using var scope = Server!.Services.CreateScope();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        Assert.IsFalse((await WhoamiAsync(setup.Key)).RiskPermissions.Single(r => r.Risk == risk).AutoApprove);
        Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => workflows.SubmitAsync(setup.User, Input(setup.First)))).StatusCode);
        await SetRoleAsync(setup.Role, DmsPermissions.Request(risk), true);
        foreach (var machine in new[] { setup.First, setup.Second })
        {
            var request = await workflows.SubmitAsync(setup.User, Input(machine));
            Assert.AreEqual(DecisionState.Approved, request.Decision); Assert.IsTrue(request.Automatic); Assert.IsNull(request.ApproverId);
            var catalog = await workflows.ListDefinitionsAsync(setup.User);
            Assert.IsTrue(catalog.Definitions.Single(d => d.Name == Input(machine).Workflow).AutoApprove);
        }
        var identity = await WhoamiAsync(setup.Key);
        Assert.IsTrue(identity.RiskPermissions.Where(r => r.Risk != risk).All(r => !r.CanRequest && !r.AutoApprove && !r.CanApprove));
        await SetRoleAsync(setup.Role, DmsPermissions.Request(risk), false);
        Assert.IsFalse((await WhoamiAsync(setup.Key)).RiskPermissions.Single(r => r.Risk == risk).CanRequest);
        Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => workflows.SubmitAsync(setup.User, Input(setup.Second)))).StatusCode);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task OrdinaryRoleCanRequestEveryBoxStageAtHighRiskButNeedsAnotherReviewer(int stage)
    {
        var setup = await ArrangeAsync(AppPermissionNames.CanReadDmsOperations, DmsPermissions.Request(RiskLevel.High));
        var cli = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "box", "deploy", "--machine-id", setup.First.ToString(),
            "--stage", stage.ToString(), "--filter", "dms", "--reason", "Deploy reviewed Box configuration", "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key, "--output-format", "json"]);
        Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var box = await db.Lockboxes.AsNoTracking().SingleAsync(l => l.RequesterId == setup.User);
        Assert.AreEqual(MachineWorkflows.BoxDeploy.Name, box.Definition);
        Assert.AreEqual(RiskLevel.High, box.Risk);
        Assert.AreEqual(DecisionState.Pending, box.Decision);
        Assert.IsFalse(box.Automatic); Assert.IsNull(box.WorkflowId);
        var request = System.Text.Json.JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!;
        Assert.AreEqual(new BoxDeployInput(stage, "dms"), MachineWorkflows.BoxDeploy.ReadInput(request.Input));
        CollectionAssert.AreEqual(new[] { setup.First }, System.Text.Json.JsonSerializer.Deserialize<Guid[]>(box.TargetMachines));
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var definition = (await workflows.ListDefinitionsAsync(setup.User)).Definitions.Single(d => d.Name == MachineWorkflows.BoxDeploy.Name);
        Assert.IsTrue(definition.CanRequest); Assert.IsFalse(definition.AutoApprove || definition.CanApprove);
        Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => workflows.DecideAsync(setup.User, box.Id, true, "Self"))).StatusCode);
        var reviewer = new User { UserName = "boxreviewer" + Guid.NewGuid().ToString("N"), DisplayName = "Box reviewer" };
        db.Users.Add(reviewer);
        DmsTestPermissions.Risk(db, reviewer.Id, RiskLevel.Medium, approve: true);
        await db.SaveChangesAsync();
        Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => workflows.DecideAsync(reviewer.Id, box.Id, true, "Wrong risk"))).StatusCode);
        DmsTestPermissions.Risk(db, reviewer.Id, RiskLevel.High, approve: true);
        await db.SaveChangesAsync();
        var approved = await workflows.DecideAsync(reviewer.Id, box.Id, true, "Reviewed stage and filter scope");
        Assert.AreEqual(DecisionState.Approved, approved.Decision);
        Assert.AreEqual(reviewer.Id, approved.ApproverId);
        Assert.IsNotNull(approved.WorkflowId);
    }

    [TestMethod]
    [DataRow("table", false)]
    [DataRow("json", false)]
    [DataRow("csv", false)]
    [DataRow("table", true)]
    [DataRow("json", true)]
    [DataRow("csv", true)]
    public async Task BoxInspectCliOnlySubmitsASafeWorkflowAndLeavesInspectionDetailsForTrace(string format, bool automatic)
    {
        var setup = await ArrangeAsync(AppPermissionNames.CanReadDmsOperations, DmsPermissions.Request(RiskLevel.Safe));
        if (automatic) await SetRoleAsync(setup.Role, DmsPermissions.AutoApprove(RiskLevel.Safe), true);
        var cli = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "box", "inspect", "--machine-id", setup.First.ToString(),
            "--reason", "Inspect Box before deployment", "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key, "--output-format", format]);
        Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var box = await db.Lockboxes.SingleAsync(l => l.RequesterId == setup.User);
        Assert.AreEqual(MachineWorkflows.BoxInspect.Name, box.Definition);
        Assert.AreEqual(RiskLevel.Safe, box.Risk);
        Assert.AreEqual(automatic ? DecisionState.Approved : DecisionState.Pending, box.Decision);
        if (!automatic) Assert.IsNull(box.WorkflowId);
        StringAssert.Contains(cli.StdOut, new LockboxId(box.Id).ToString());
        Assert.AreEqual(automatic, await db.WorkflowRuns.AnyAsync(r => r.LockboxId == box.Id));
        if (format == "json") Assert.HasCount(2, (JObject)JObject.Parse(cli.StdOut)["Data"]!);
        if (format == "csv") Assert.AreEqual("LockboxId,WorkflowId", cli.StdOut.Split('\n')[0].Trim());
        Assert.DoesNotContain("Branch", cli.StdOut);
        Assert.DoesNotContain("Origin", cli.StdOut);
        Assert.DoesNotContain("Result", cli.StdOut);
        Assert.AreEqual("{}", System.Text.Json.JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!.Input);
        if (!automatic) return;
        var metadata = await Client.DmsCommandApp.Create().TestRunAsync(["operation", "get-workflow", "--id", box.WorkflowId!.Value.ToString(),
            "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key, "--output-format", "json"]);
        Assert.AreEqual(0, metadata.ProgramReturn, metadata.StdErr);
        Assert.AreEqual(new LockboxId(box.Id).ToString(), JObject.Parse(metadata.StdOut)["Data"]!["LockboxId"]!.ToString());
    }

    [TestMethod]
    [DataRow("du", RiskLevel.Low)]
    [DataRow("docker", RiskLevel.Low)]
    [DataRow("git", RiskLevel.High)]
    [DataRow("rm", RiskLevel.High)]
    public async Task MachineMaintenanceCliFreezesTargetsAndTypedInputsAndRequiresTheCorrectRisk(string operation, RiskLevel risk)
    {
        var setup = await ArrangeAsync(DmsPermissions.Request(risk));
        string[] command = operation switch
        {
            "du" => ["machine", "filesystem", "du", "--path", "/swarm-vol", "--depth", "2"],
            "docker" => ["machine", "docker", "system", "df"],
            "rm" => ["machine", "filesystem", "rm", "--path", "/tmp/reviewed cleanup", "--recursive"],
            _ => ["machine", "box", "configure-git", "--identity-file", "/home/anduin/.ssh/id_ed25519", "--known-hosts-file", "/home/anduin/.ssh/known_hosts"]
        };
        var cli = await Client.DmsCommandApp.Create().TestRunAsync([.. command, "--machine-id", setup.First.ToString(),
            "--reason", "Approved machine maintenance scope", "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key, "--output-format", "json"]);
        Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var box = await db.Lockboxes.SingleAsync(l => l.RequesterId == setup.User);
        Assert.AreEqual(risk, box.Risk);
        Assert.AreEqual(DecisionState.Pending, box.Decision);
        Assert.IsNull(box.WorkflowId);
        Assert.HasCount(2, (JObject)JObject.Parse(cli.StdOut)["Data"]!);
        Assert.AreEqual(JTokenType.Null, JObject.Parse(cli.StdOut)["Data"]!["WorkflowId"]!.Type);
        Assert.AreEqual(new LockboxId(box.Id).ToString(), JObject.Parse(cli.StdOut)["Data"]!["LockboxId"]!.ToString());
        var request = System.Text.Json.JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!;
        CollectionAssert.AreEqual(new[] { setup.First }, request.MachineIds);
        if (operation == "du") Assert.AreEqual(new DirectoryUsageInput("/swarm-vol", 2), MachineWorkflows.DirectoryUsage.ReadInput(request.Input));
        if (operation == "docker") Assert.AreEqual("{}", request.Input);
        if (operation == "rm") Assert.AreEqual(new RemovePathInput("/tmp/reviewed cleanup", true), MachineWorkflows.RemovePath.ReadInput(request.Input));
        if (operation == "git") Assert.AreEqual(new BoxGitInput("/home/anduin/.ssh/id_ed25519", "/home/anduin/.ssh/known_hosts"), MachineWorkflows.BoxConfigureGit.ReadInput(request.Input));
    }

    [TestMethod]
    public async Task BoxDeployCliDefaultsAreFrozenAndInvalidParametersNeverCreateRequests()
    {
        var setup = await ArrangeAsync(DmsPermissions.Request(RiskLevel.High));
        foreach (var extra in new[] { new[] { "--stage", "5" }, new[] { "--filter", "dms;id" } })
        {
            var invalid = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "box", "deploy", "--machine-id", setup.First.ToString(),
                "--reason", "Invalid deployment", "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key, .. extra]);
            Assert.AreNotEqual(0, invalid.ProgramReturn);
        }
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(0, await db.Lockboxes.CountAsync(l => l.RequesterId == setup.User));
        var cli = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "box", "deploy", "--machine-id", setup.First.ToString(),
            "--reason", "Deploy all stage-4 stacks", "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key, "--output-format", "json"]);
        Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
        var box = await db.Lockboxes.SingleAsync(l => l.RequesterId == setup.User);
        var request = System.Text.Json.JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!;
        Assert.AreEqual(new BoxDeployInput(), MachineWorkflows.BoxDeploy.ReadInput(request.Input));
        Assert.AreEqual(DecisionState.Pending, box.Decision);
    }

    [TestMethod]
    public async Task ManualApprovalRequiresRiskPermissionAndOwnPermissionIndependently()
    {
        var setup = await ArrangeAsync(DmsPermissions.Request(RiskLevel.Safe), AppPermissionNames.CanApproveOwnWorkflow);
        using var scope = Server!.Services.CreateScope();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var input = ClusterWorkflows.GetMachines.Request(new EmptyInput(), "Manual");
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
        var pending = await workflows.SubmitAsync(setup.User, ClusterWorkflows.GetMachines.Request(new EmptyInput(), "Other reviewer"));
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
            ClusterWorkflows.GetMachines.Request(new EmptyInput(), "Web permission test"));
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
