using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public async Task OpenMachineListRequiresItsOwnPermissionAndReturnsOnlyTheInventory()
    {
        var setup = await ArrangeAsync(DmsPermissions.Request(RiskLevel.Safe), DmsPermissions.AutoApprove(RiskLevel.Safe));
        var requestId = Guid.NewGuid();
        async Task<Aiursoft.CommandFramework.Models.TestResult> ListAsync(string format, params string[] options) =>
            await Client.DmsCommandApp.Create().TestRunAsync(["machine", "list",
                "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key,
                "--request-id", requestId.ToString(), "--output-format", format, ..options]);

        var denied = await ListAsync("json");
        Assert.AreEqual(1, denied.ProgramReturn);
        Assert.AreEqual(string.Empty, denied.StdOut);
        StringAssert.Contains(denied.StdErr, "Run open workflows permission is required.");
        var deniedReceipt = await ListAsync("json", "--show-lockbox-id");
        Assert.AreEqual(1, deniedReceipt.ProgramReturn);
        Assert.AreEqual(string.Empty, deniedReceipt.StdOut);
        StringAssert.Contains(deniedReceipt.StdErr, "Run open workflows permission is required.");

        var unsupported = await Client.DmsCommandApp.Create().TestRunAsync(
            ["machine", "layout", "df", "--show-lockbox-id", "--reason", "Safe command"]);
        Assert.AreNotEqual(0, unsupported.ProgramReturn);
        StringAssert.Contains(unsupported.StdErr, "--show-lockbox-id");

        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.IsFalse(await db.Lockboxes.AnyAsync(box => box.RequesterId == setup.User));

        await SetRoleAsync(setup.Role, AppPermissionNames.CanRequestOpenRiskWorkflow, true);
        var identity = await WhoamiAsync(setup.Key);
        Assert.AreEqual(new RiskPermission(RiskLevel.Open, true, true, false), identity.RiskPermissions[0]);
        var table = await ListAsync("table");
        Assert.AreEqual(0, table.ProgramReturn, table.StdErr);
        StringAssert.Contains(table.StdOut, "First machine");
        Assert.DoesNotContain("LockboxId", table.StdOut);

        var json = await ListAsync("json");
        Assert.AreEqual(0, json.ProgramReturn, json.StdErr);
        var machines = JArray.Parse(json.StdOut).ToObject<List<MachineSummary>>()!;
        Assert.IsTrue(machines.Any(machine => machine.Id == setup.First && machine.Name == "First machine"));
        Assert.DoesNotContain("KeyHash", json.StdOut);

        var csv = await ListAsync("csv");
        Assert.AreEqual(0, csv.ProgramReturn, csv.StdErr);
        StringAssert.StartsWith(csv.StdOut, "AgentVersion,Disabled,Id,LastSeen,Name");
        StringAssert.Contains(csv.StdOut, "First machine");

        var box = await db.Lockboxes.AsNoTracking().SingleAsync(item => item.RequesterId == setup.User);
        Assert.AreEqual(RiskLevel.Open, box.Risk);
        Assert.AreEqual(DecisionState.Approved, box.Decision);
        Assert.IsTrue(box.Automatic);
        Assert.IsNull(box.ApproverId);
        Assert.AreEqual(string.Empty, box.Reason);
        StringAssert.Contains(box.DecisionReason, "Open workflow policy");
        var workflow = await db.WorkflowRuns.AsNoTracking().SingleAsync(run => run.LockboxId == box.Id);
        Assert.AreEqual(workflow.Id, box.WorkflowId);
        Assert.AreEqual(RunState.Succeeded, workflow.State);
        var approval = (await db.WorkflowAuditOutbox.ToListAsync()).Select(row => row.Read())
            .Single(entry => entry.LockboxId?.Value == box.Id && entry.Kind == WorkflowAuditKind.LockboxApproved);
        Assert.AreEqual(RiskLevel.Open, approval.RequestedRisk);

        var resultPath = $"/api/v1/dms/lockboxes/{new LockboxId(box.Id)}/result";
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/v1/dms/access")).StatusCode);
        await SetRoleAsync(setup.Role, AppPermissionNames.CanManageDmsAccess, true);
        using (var access = await Http.GetAsync("/api/v1/dms/access"))
        {
            Assert.AreEqual(HttpStatusCode.OK, access.StatusCode);
            var data = JObject.Parse(await access.Content.ReadAsStringAsync())["Data"]!;
            CollectionAssert.AreEqual(machines.Select(m => new AccessMachine(m.Id, m.Name, m.Disabled)).ToList(),
                data.ToObject<AccessSnapshot>()!.Machines);
            Assert.DoesNotContain("AgentVersion", data.ToString());
        }
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync($"/api/v1/dms/operations/{workflow.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.PostAsJsonAsync("/api/v1/dms/lockboxes",
            MachineWorkflows.AgentStatus.Request(new EmptyInput(), "", [setup.First]))).StatusCode);

        var other = await ArrangeAsync(AppPermissionNames.CanRequestOpenRiskWorkflow);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.Key);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync(resultPath)).StatusCode);

        await SetRoleAsync(setup.Role, AppPermissionNames.CanRequestOpenRiskWorkflow, false);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync(resultPath)).StatusCode);
        Assert.AreEqual(1, (await ListAsync("json")).ProgramReturn);
    }

    [TestMethod]
    [DataRow(null, RunState.Succeeded, "json")]
    [DataRow("", RunState.Succeeded, "table")]
    [DataRow("null", RunState.Warning, "csv")]
    public async Task OpenWorkflowWithoutResultPrintsCompletionInTheSelectedFormat(string? payload, RunState state, string format)
    {
        var setup = await ArrangeAsync(AppPermissionNames.CanRequestOpenRiskWorkflow);
        var request = ClusterWorkflows.GetMachines.Request(new EmptyInput(), "");
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var box = new LockboxRequest
        {
            RequesterId = setup.User, ClientRequestId = request.RequestId, Definition = request.Workflow,
            Risk = RiskLevel.Open, Decision = DecisionState.Approved, Automatic = true,
            Parameters = System.Text.Json.JsonSerializer.Serialize(request)
        };
        var run = new WorkflowRun { LockboxId = box.Id, Definition = box.Definition, State = state, Result = payload ?? "" };
        run.RootId = run.Id;
        box.WorkflowId = run.Id;
        db.AddRange(box, run);
        await db.SaveChangesAsync();

        var result = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "list",
            "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key, "--request-id", request.RequestId.ToString(),
            "--output-format", format]);
        Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
        if (format == "json")
        {
            var completion = JObject.Parse(result.StdOut);
            Assert.AreEqual(2, completion.Count);
            Assert.AreEqual(run.Id, completion["WorkflowId"]!.ToObject<Guid>());
            Assert.AreEqual(state.ToString(), completion["State"]!.ToObject<string>());
        }
        else
        {
            StringAssert.Contains(result.StdOut, run.Id.ToString());
            StringAssert.Contains(result.StdOut, state.ToString());
            if (format == "csv") StringAssert.StartsWith(result.StdOut, "State,WorkflowId");
        }
        if (state == RunState.Warning) StringAssert.Contains(result.StdErr, "reported warnings.");
        else Assert.AreEqual("", result.StdErr);
        Assert.AreEqual(1, await db.Lockboxes.CountAsync(l => l.RequesterId == setup.User));
    }

    [TestMethod]
    public async Task OpenMachineListReceiptReturnsEvenWhenTheWorkflowResultFails()
    {
        var setup = await ArrangeAsync(AppPermissionNames.CanRequestOpenRiskWorkflow);
        var request = ClusterWorkflows.GetMachines.Request(new EmptyInput(), "");
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var box = new LockboxRequest
        {
            RequesterId = setup.User, ClientRequestId = request.RequestId, Definition = request.Workflow,
            Risk = RiskLevel.Open, Decision = DecisionState.Approved, Automatic = true,
            Parameters = System.Text.Json.JsonSerializer.Serialize(request)
        };
        var run = new WorkflowRun { LockboxId = box.Id, Definition = box.Definition, State = RunState.Failed, Result = "private diagnostic" };
        run.RootId = run.Id;
        box.WorkflowId = run.Id;
        db.AddRange(box, run);
        await db.SaveChangesAsync();

        string[] args = ["machine", "list", "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key,
            "--request-id", request.RequestId.ToString()];
        foreach (var format in new[] { "table", "json", "csv" })
        {
            var receipt = await Client.DmsCommandApp.Create().TestRunAsync([..args, "--show-lockbox-id", "--output-format", format]);
            Assert.AreEqual(0, receipt.ProgramReturn, receipt.StdErr);
            Assert.AreEqual(string.Empty, receipt.StdErr);
            StringAssert.Contains(receipt.StdOut, new LockboxId(box.Id).ToString());
            StringAssert.Contains(receipt.StdOut, run.Id.ToString());
            Assert.DoesNotContain("private diagnostic", receipt.StdOut);
            if (format == "json")
            {
                var data = (JObject)JObject.Parse(receipt.StdOut)["Data"]!;
                Assert.HasCount(2, data);
                Assert.AreEqual(new LockboxId(box.Id), LockboxId.Parse(data["LockboxId"]!.Value<string>()!));
                Assert.AreEqual(run.Id.ToString(), data["WorkflowId"]!.Value<string>());
            }
            else if (format == "csv") StringAssert.StartsWith(receipt.StdOut, "LockboxId,WorkflowId");
        }

        var result = await Client.DmsCommandApp.Create().TestRunAsync([..args, "--output-format", "json"]);
        Assert.AreEqual(1, result.ProgramReturn);
        Assert.AreEqual(string.Empty, result.StdOut);
        StringAssert.Contains(result.StdErr, "Workflow state is Failed.");
        StringAssert.Contains(result.StdErr, run.Id.ToString());
        StringAssert.Contains(result.StdErr, request.RequestId.ToString());
        Assert.DoesNotContain("private diagnostic", result.StdErr);
        Assert.AreEqual(DecisionState.Approved, (await db.Lockboxes.AsNoTracking().SingleAsync(item => item.Id == box.Id)).Decision);
    }

    [TestMethod]
    public async Task LockboxPageShowsFrozenTargetsCommandAndLiveApprovers()
    {
        var setup = await ArrangeAsync(DmsPermissions.Request(RiskLevel.High), DmsPermissions.Approve(RiskLevel.High));
        using var scope = Server!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var reviewer = new User
        {
            UserName = "lockbox-reviewer" + Guid.NewGuid().ToString("N"),
            DisplayName = "Sam Reviewer",
            Email = Guid.NewGuid().ToString("N") + "@example.test"
        };
        Assert.IsTrue((await users.CreateAsync(reviewer)).Succeeded);
        Assert.IsTrue((await users.AddClaimAsync(reviewer,
            new Claim(AppPermissions.Type, DmsPermissions.Approve(RiskLevel.High)))).Succeeded);

        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var box = await workflows.SubmitAsync(setup.User, MachineWorkflows.PullerForceUpdate.Request(
            new PullerForceUpdateInput("aiursoft/dms:latest"), "Refresh today's image", allMachines: true));
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        db.Machines.Add(new Machine { Name = "Machine enrolled later" });
        await db.SaveChangesAsync();

        var page = $"/Dms/Lockbox?id={Uri.EscapeDataString(new LockboxId(box.Id).ToString())}";
        using (var signedOut = await Http.GetAsync(page))
            Assert.AreEqual(HttpStatusCode.Found, signedOut.StatusCode);
        await LoginAsAdmin();
        var pending = WebUtility.HtmlDecode(await Http.GetStringAsync(page));
        foreach (var expected in new[] { "Operator", "data-utc-now", "First machine", "Second machine",
                     "dms machine box puller force-update", "--image 'aiursoft/dms:latest'", "Sam Reviewer",
                     "action=\"/Dms/Decide\"", "__RequestVerificationToken" })
            StringAssert.Contains(pending, expected);
        StringAssert.Contains(pending, "Refresh today'\"'\"'s image");
        Assert.DoesNotContain("Machine enrolled later", pending);
        Assert.DoesNotContain("Operator (@", pending[pending.IndexOf("lockbox-people", StringComparison.Ordinal)..]);

        await workflows.DecideAsync(reviewer.Id, box.Id, false, "Reviewed frozen inputs");
        var decided = WebUtility.HtmlDecode(await Http.GetStringAsync(page));
        StringAssert.Contains(decided, "Sam Reviewer");
        StringAssert.Contains(decided, "Reviewed frozen inputs");
        Assert.DoesNotContain("action=\"/Dms/Decide\"", decided);
    }

    [TestMethod]
    [DataRow(false, "Test")]
    [DataRow(true, "--help")]
    [DataRow(false, "@lockbox-preview.rsp")]
    public async Task HistoricalAgentStatusPreviewSubmitsTheSameFrozenOperation(bool allMachines, string reason)
    {
        var setup = await ArrangeAsync(DmsPermissions.Request(RiskLevel.Safe));
        using var scope = Server!.Services.CreateScope();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var original = await workflows.SubmitAsync(setup.User, MachineWorkflows.AgentStatus.Request(
            new EmptyInput(), reason, allMachines ? [] : [setup.First], allMachines));

        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        db.Machines.Add(new Machine { Name = "Machine enrolled later" });
        await db.SaveChangesAsync();

        await LoginAsAdmin();
        var id = Uri.EscapeDataString(new LockboxId(original.Id).ToString());
        var page = await Http.GetStringAsync($"/Dms/Lockbox?id={id}");
        var preview = Regex.Match(page, @"<code data-command[^>]*><span>([^<]+)</span>([^<]*)</code>");
        Assert.IsTrue(preview.Success);
        StringAssert.Contains(page, "Equivalent DMS command");
        StringAssert.Contains(preview.Groups[1].Value, "dms machine agent status");
        Assert.DoesNotContain("--all-machines", preview.Value);

        // These cases use single-word reasons so the literal shell quoting can
        // be removed before passing the resulting argument list to the CLI.
        var arguments = WebUtility.HtmlDecode(preview.Groups[1].Value + preview.Groups[2].Value)
            .ReplaceLineEndings("\n").Replace(" \\\n  ", " ").Replace("'", "").Split(' ');
        var result = await Client.DmsCommandApp.Create().TestRunAsync(
            [.. arguments.Skip(1), "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key]);
        Assert.AreEqual(0, result.ProgramReturn, result.StdErr);

        var replay = await db.Lockboxes.AsNoTracking().SingleAsync(
            box => box.Id != original.Id && box.RequesterId == setup.User);
        Assert.AreEqual(original.Definition, replay.Definition);
        Assert.AreEqual(original.Version, replay.Version);
        Assert.AreEqual(original.TargetMachines, replay.TargetMachines);
        Assert.AreEqual(original.Reason, replay.Reason);
        Assert.AreEqual(JsonSerializer.Deserialize<OperationRequest>(original.Parameters)!.Input,
            JsonSerializer.Deserialize<OperationRequest>(replay.Parameters)!.Input);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnmappedWorkflowShowsParametersAndKeepsTheEndpointInAuditDetails(bool unknownVersion)
    {
        var setup = await ArrangeAsync(DmsPermissions.Request(RiskLevel.Low), DmsPermissions.Request(RiskLevel.Safe));
        using var scope = Server!.Services.CreateScope();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var request = unknownVersion
            ? MachineWorkflows.AgentStatus.Request(new EmptyInput(), "Check Agent", [setup.First])
            : MachineWorkflows.ServiceStatus.Request(new ServiceInput("dms-example.service"), "Review service", [setup.First]);
        var box = await workflows.SubmitAsync(setup.User, request);
        if (unknownVersion)
        {
            box.Version = "99";
            await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().SaveChangesAsync();
        }

        await LoginAsAdmin();
        var id = Uri.EscapeDataString(new LockboxId(box.Id).ToString());
        var page = await Http.GetStringAsync($"/Dms/Lockbox?id={id}");

        Assert.DoesNotContain("data-command", page);
        var audit = page.IndexOf("<details class=\"card lockbox-audit\">", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, audit);
        Assert.DoesNotContain("POST /api/v1/dms/lockboxes", page[..audit]);
        StringAssert.Contains(page[..audit], unknownVersion ? "This workflow takes no parameters." : "dms-example.service");
        StringAssert.Contains(page[audit..], "POST /api/v1/dms/lockboxes");
    }

    [TestMethod]
    public async Task RequesterCanFollowOwnWebExecutionWithoutGlobalReadPermission()
    {
        var setup = await ArrangeAsync(DmsPermissions.Request(RiskLevel.High));
        using var scope = Server!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var requester = (await users.FindByIdAsync(setup.User))!;
        var admin = (await users.FindByEmailAsync("admin@default.com"))!;
        Assert.IsTrue((await users.AddPasswordAsync(requester, "Operator@123456!")).Succeeded);

        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var input = new PullerForceUpdateInput("aiursoft/dms:latest");
        var own = await workflows.SubmitAsync(requester.Id,
            MachineWorkflows.PullerForceUpdate.Request(input, "Own refresh request", [setup.First]));
        var foreign = await workflows.SubmitAsync(admin.Id,
            MachineWorkflows.PullerForceUpdate.Request(input, "Another user's request", [setup.First]));
        var approved = await workflows.DecideAsync(admin.Id, own.Id, true, "Reviewed for rollout");
        var workflowId = approved.WorkflowId!.Value;
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var run = await db.WorkflowRuns.SingleAsync(entry => entry.Id == workflowId);
        run.State = RunState.Failed;
        run.Result = "Private failure diagnostic";
        await db.SaveChangesAsync();

        using var login = await PostForm("/Account/Login", new Dictionary<string, string>
        {
            ["EmailOrUserName"] = requester.UserName!, ["Password"] = "Operator@123456!"
        });
        Assert.AreEqual(HttpStatusCode.Found, login.StatusCode);
        var ownPage = "/DmsAccess/MyLockbox?id=" + Uri.EscapeDataString(new LockboxId(own.Id).ToString());
        var foreignPage = "/DmsAccess/MyLockbox?id=" + Uri.EscapeDataString(new LockboxId(foreign.Id).ToString());
        var list = await Http.GetStringAsync("/DmsAccess/MyLockboxes");
        StringAssert.Contains(list, "Own refresh request");
        StringAssert.Contains(list, "data-run-state=\"Failed\"");
        Assert.DoesNotContain("Another user's request", WebUtility.HtmlDecode(list));
        var detail = await Http.GetStringAsync(ownPage);
        StringAssert.Contains(detail, "Reviewed for rollout");
        StringAssert.Contains(detail, "data-run-state=\"Failed\"");
        Assert.DoesNotContain("Private failure diagnostic", detail);
        Assert.DoesNotContain("href=\"/Dms/Operation", detail);
        using var other = await Http.GetAsync(foreignPage);
        Assert.AreEqual(HttpStatusCode.NotFound, other.StatusCode);
        using var execution = await Http.GetAsync("/Dms/Operation/" + workflowId);
        Assert.AreEqual(HttpStatusCode.Found, execution.StatusCode);
        StringAssert.Contains(execution.Headers.Location?.OriginalString, "/Error/Code403");
    }

    [TestMethod]
    public async Task LockboxStatusUsesLiveApproverNamesAndSupportsPositionalWorkflowIds()
    {
        var approval = DmsPermissions.Approve(RiskLevel.Safe);
        var setup = await ArrangeAsync(DmsPermissions.Request(RiskLevel.Safe), approval);
        using var scope = Server!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var reviewerRole = new IdentityRole("status-reviewers");
        Assert.IsTrue((await roles.CreateAsync(reviewerRole)).Succeeded);
        Assert.IsTrue((await roles.AddClaimAsync(reviewerRole, new Claim(AppPermissions.Type, approval))).Succeeded);

        async Task<User> CreatePerson(string name, string permission, bool locked = false)
        {
            var person = new User { UserName = name, Email = $"{name}@example.test", DisplayName = name, LockoutEnabled = locked };
            if (locked) person.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1);
            Assert.IsTrue((await users.CreateAsync(person)).Succeeded);
            Assert.IsTrue((await users.AddClaimAsync(person, new Claim(AppPermissions.Type, permission))).Succeeded);
            return person;
        }

        var reviewer = await CreatePerson("Sam", approval);
        Assert.IsTrue((await users.AddToRoleAsync(reviewer, reviewerRole.Name!)).Succeeded);
        await CreatePerson("LockedReviewer", approval, locked: true);
        await CreatePerson("AutomaticOnly", DmsPermissions.AutoApprove(RiskLevel.Safe));
        await CreatePerson("DifferentRisk", DmsPermissions.Approve(RiskLevel.Low));

        Task<LockboxRequest> SubmitAsync() => workflows.SubmitAsync(setup.User,
            MachineWorkflows.AgentStatus.Request(new EmptyInput(), "Approval status fixture", [setup.First]));
        async Task<string> Cli(string key, params string[] command)
        {
            var result = await Client.DmsCommandApp.Create().TestRunAsync(
                [.. command, "--server", Http.BaseAddress!.ToString(), "--api-key", key]);
            Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
            return result.StdOut;
        }

        var box = await SubmitAsync();
        var id = new LockboxId(box.Id).ToString();
        var pending = await Cli(setup.Key, "lockbox", "status", id);
        foreach (var expected in new[] { id, "Operator", "Requested at", "UTC", "Awaiting approval", "Eligible approvers now", "Sam" })
            StringAssert.Contains(pending, expected);
        foreach (var hidden in new[] { "Workflow ID", "ClientRequestId", "RequesterId", "LockedReviewer", "AutomaticOnly", "DifferentRisk" })
            Assert.DoesNotContain(hidden, pending);
        var json = await Cli(setup.Key, "lockbox", "status", id, "--output-format", "json");
        var summary = JObject.Parse(json)["Data"]!.ToObject<LockboxStatus>()!;
        Assert.IsNull(summary.WorkflowId);
        Assert.IsNull(summary.DecidedAt);
        Assert.AreEqual(1, summary.EligibleApprovers.Count(name => name == "Sam"));
        Assert.IsFalse(summary.EligibleApprovers.Contains(summary.RequestedBy));

        await SetRoleAsync(setup.Role, AppPermissionNames.CanApproveOwnWorkflow, true);
        json = await Cli(setup.Key, "lockbox", "status", id, "--output-format", "json");
        summary = JObject.Parse(json)["Data"]!.ToObject<LockboxStatus>()!;
        Assert.IsTrue(summary.EligibleApprovers.Contains(summary.RequestedBy));

        var approved = await workflows.DecideAsync(reviewer.Id, box.Id, true, "Manual approval");
        var approvedText = await Cli(setup.Key, "lockbox", "status", id);
        StringAssert.Contains(approvedText, "Approved at");
        StringAssert.Contains(approvedText, "Approved by");
        StringAssert.Contains(approvedText, approved.WorkflowId!.Value.ToString());
        Assert.AreEqual("Sam", JObject.Parse(await Cli(setup.Key, "lockbox", "status", id, "--output-format", "json"))["Data"]!["DecidedBy"]!.Value<string>());

        var denied = await SubmitAsync();
        await workflows.DecideAsync(reviewer.Id, denied.Id, false, "Rejected");
        StringAssert.Contains(await Cli(setup.Key, "lockbox", "status", denied.Id.ToString()), "Rejected by");
        var withdrawn = await SubmitAsync();
        await workflows.WithdrawAsync(setup.User, withdrawn.Id);
        StringAssert.Contains(await Cli(setup.Key, "lockbox", "status", withdrawn.Id.ToString()), "Withdrawn by");

        await SetRoleAsync(setup.Role, AppPermissionNames.CanReadDmsOperations, true);
        await SetRoleAsync(setup.Role, DmsPermissions.AutoApprove(RiskLevel.Safe), true);
        var automatic = await SubmitAsync();
        StringAssert.Contains(await Cli(setup.Key, "lockbox", "status", automatic.Id.ToString()), "System (automatic approval)");
        StringAssert.Contains(await Cli(setup.Key, "lockbox", "get", "--id", id), "ClientRequestId");
        StringAssert.Contains(await Cli(setup.Key, "workflow", "status", approved.WorkflowId.Value.ToString()), "WorkflowId");
        var workflowId = approved.WorkflowId.Value.ToString();
        StringAssert.Contains(await Cli(setup.Key, "workflow", "callstack", workflowId), workflowId);
        StringAssert.Contains(await Cli(setup.Key, "operation", "get-callstack", workflowId), workflowId);

        var other = await ArrangeAsync(DmsPermissions.Request(RiskLevel.Safe));
        var unauthorized = await Client.DmsCommandApp.Create().TestRunAsync(
            ["lockbox", "status", id, "--server", Http.BaseAddress!.ToString(), "--api-key", other.Key]);
        Assert.AreEqual(1, unauthorized.ProgramReturn);
        Assert.DoesNotContain("Sam", unauthorized.StdOut);
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
        Assert.HasCount(6, identity.RiskPermissions);
        Assert.AreEqual(RiskLevel.Open, identity.RiskPermissions[0].Risk);
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
        var input = MachineWorkflows.AgentStatus.Request(new EmptyInput(), "Manual", [setup.First]);
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
        var pending = await workflows.SubmitAsync(setup.User, MachineWorkflows.AgentStatus.Request(new EmptyInput(), "Other reviewer", [other.First]));
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
            MachineWorkflows.AgentStatus.Request(new EmptyInput(), "Web permission test", [setup.First]));
        var path = "/Dms/Lockbox/" + request.Id;
        Assert.DoesNotContain("action=\"/Dms/Decide\"", await Http.GetStringAsync(path));
        const string review = "/Dms/Lockboxes?scope=review&q=Web%20permission%20test";
        var id = new LockboxId(request.Id).ToString();
        Assert.DoesNotContain(id, Uri.UnescapeDataString(await Http.GetStringAsync(review)));
        await SetRoleAsync(setup.Role, AppPermissionNames.CanApproveOwnWorkflow, true);
        StringAssert.Contains(await Http.GetStringAsync(path), "action=\"/Dms/Decide\"");
        StringAssert.Contains(Uri.UnescapeDataString(await Http.GetStringAsync(review)), id);
        await SetRoleAsync(setup.Role, DmsPermissions.Approve(RiskLevel.Safe), false);
        var myApprovals = await Http.GetStringAsync("/Dms/Lockboxes");
        Assert.DoesNotContain(id, Uri.UnescapeDataString(myApprovals));
        var pending = await Http.GetStringAsync("/Dms/Lockboxes?scope=pending&q=Web%20permission%20test");
        StringAssert.Contains(Uri.UnescapeDataString(pending), id);
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
        StringAssert.Contains(html, AppPermissionNames.CanRequestOpenRiskWorkflow);
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
            if (format == "csv") Assert.AreEqual(Enum.GetValues<RiskLevel>().Length + 1, result.StdOut.Trim().Split('\n').Length);
        }
        foreach (var path in new[] { "access/grants", "access/self-approval" })
            Assert.AreEqual(HttpStatusCode.NotFound, (await Http.PutAsJsonAsync("/api/v1/dms/" + path, new { })).StatusCode);
    }
}
