using Aiursoft.DMS.Authorization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class AgentUpgradeIntegrationTests : TestBase
{
    [TestMethod]
    public async Task WebShowsSupersededPackageOutsideTheQueueAndLetsOnlyTheRequesterResubmit()
    {
        var environment = Server!.Services.GetRequiredService<IWebHostEnvironment>();
        var directory = Directory.CreateDirectory(Path.Combine(environment.ContentRootPath, "agent-assets"));
        var metadata = Path.Combine(directory.FullName, "releases.json");
        var release = new AgentRelease("linux-x64", "1.10.1.0", new string('a', 64), new string('b', 64));
        var input = new AgentUpgradeInput([release, release with { Architecture = "linux-arm64" }]);
        var replacement = input.Releases.Select(r => r with { ArchiveSha256 = new string('c', 64) }).ToList();
        await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(input.Releases));
        try
        {
            using var scope = Server.Services.CreateScope();
            var services = scope.ServiceProvider;
            var db = services.GetRequiredService<TemplateDbContext>();
            var users = services.GetRequiredService<UserManager<User>>();
            var workflows = services.GetRequiredService<WorkflowService>();
            var requester = new User
            {
                UserName = "upgrade-requester", DisplayName = "Upgrade requester",
                Email = "upgrade-requester@example.test", EmailConfirmed = true
            };
            const string password = "Operator@123456!";
            Assert.IsTrue((await users.CreateAsync(requester, password)).Succeeded);
            DmsTestPermissions.Set(db, requester.Id, DmsPermissions.Request(RiskLevel.Medium));
            var machine = new Machine { Name = "Upgrade target" };
            db.Machines.Add(machine);
            await db.SaveChangesAsync();
            var old = await workflows.SubmitAsync(requester.Id,
                MachineWorkflows.AgentUpgrade.Request(input, "Review the canary upgrade", [machine.Id]));
            var id = new LockboxId(old.Id).ToString();
            var queryId = Uri.EscapeDataString(id);
            var detail = "/Dms/Lockbox?id=" + queryId;
            var myDetail = "/DmsAccess/MyLockbox?id=" + queryId;
            const string approvalQueue = "/Dms/Lockboxes?scope=review";
            const string replacementPath = "/DmsAccess/ResubmitAgentUpgrade";

            await LoginAsAdmin();
            var token = await GetAntiCsrfToken(detail);
            StringAssert.Contains(await Http.GetStringAsync(detail), "action=\"/Dms/Decide\"");
            StringAssert.Contains(Uri.UnescapeDataString(await Http.GetStringAsync(approvalQueue)), id);

            File.Delete(metadata);
            StringAssert.Contains(await Http.GetStringAsync(detail), "The Agent package is temporarily unavailable.");
            StringAssert.Contains(Uri.UnescapeDataString(await Http.GetStringAsync(approvalQueue)), id);

            await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(replacement));
            using var staleClick = await PostForm("/Dms/Decide",
                new Dictionary<string, string> { ["id"] = id, ["approve"] = "true", ["__RequestVerificationToken"] = token });
            AssertRedirect(staleClick, "/Dms/Lockbox", exact: false);
            var stale = await Http.GetStringAsync(detail);
            StringAssert.Contains(stale, "Upgrade the DMS Agent on Upgrade target");
            StringAssert.Contains(stale, "Needs a new request");
            StringAssert.Contains(stale, "The upgrade has not started.");
            Assert.DoesNotContain("Awaiting approval", stale);
            Assert.DoesNotContain("action=\"/Dms/Decide\"", stale);
            Assert.DoesNotContain(replacementPath, stale);
            Assert.DoesNotContain(id, Uri.UnescapeDataString(await Http.GetStringAsync(approvalQueue)));
            Assert.IsFalse(await db.WorkflowRuns.AnyAsync(run => run.LockboxId == old.Id));
            using var forbidden = await PostForm(replacementPath, new Dictionary<string, string>
            {
                ["id"] = id, ["requestId"] = Guid.NewGuid().ToString(),
                ["releaseIdentity"] = new string('a', 64), ["__RequestVerificationToken"] = token
            });
            Assert.AreEqual(HttpStatusCode.NotFound, forbidden.StatusCode);

            using var logout = await Http.GetAsync("/Account/LogOff");
            Assert.AreEqual(HttpStatusCode.Found, logout.StatusCode);
            using var login = await PostForm("/Account/Login", new Dictionary<string, string>
            {
                ["EmailOrUserName"] = requester.UserName!, ["Password"] = password
            });
            Assert.AreEqual(HttpStatusCode.Found, login.StatusCode);
            var history = await Http.GetStringAsync("/DmsAccess/MyLockboxes");
            StringAssert.Contains(Uri.UnescapeDataString(history), id);
            StringAssert.Contains(history, "Needs a new request");
            var current = await Http.GetStringAsync(myDetail);
            StringAssert.Contains(current, "Currently available Agent package");
            StringAssert.Contains(current, "Submit a new upgrade request");
            var form = new Dictionary<string, string>
            {
                ["id"] = id,
                ["requestId"] = Regex.Match(current, "name=\"requestId\" value=\"([^\"]+)\"").Groups[1].Value,
                ["releaseIdentity"] = Regex.Match(current, "name=\"releaseIdentity\" value=\"([^\"]+)\"").Groups[1].Value
            };
            Assert.AreEqual(64, form["releaseIdentity"].Length);
            await File.WriteAllTextAsync(metadata,
                JsonSerializer.Serialize(replacement.Select(r => r with { ArchiveSha256 = new string('d', 64) })));
            using var anotherChange = await PostForm(replacementPath, form, myDetail);
            AssertRedirect(anotherChange, "/DmsAccess/MyLockbox", exact: false);
            StringAssert.Contains(await Http.GetStringAsync(myDetail), "The current Agent package changed again.");
            Assert.AreEqual(1, await db.Lockboxes.CountAsync(box => box.RequesterId == requester.Id));

            await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(replacement));
            using var submitted = await PostForm(replacementPath, form, myDetail);
            AssertRedirect(submitted, "/DmsAccess/MyLockbox", exact: false);
            using var retry = await PostForm(replacementPath, form, myDetail);
            Assert.AreEqual(submitted.Headers.Location, retry.Headers.Location);
            var newRequest = await db.Lockboxes.AsNoTracking().SingleAsync(box => box.Id != old.Id && box.RequesterId == requester.Id);
            Assert.AreEqual(DecisionState.Pending, newRequest.Decision);
            Assert.AreEqual(old.TargetMachines, newRequest.TargetMachines);
            Assert.AreEqual(old.Reason, newRequest.Reason);
            Assert.IsNull(newRequest.WorkflowId);
            var submittedInput = JsonSerializer.Deserialize<OperationRequest>(newRequest.Parameters)!.Input;
            CollectionAssert.AreEqual(replacement, MachineWorkflows.AgentUpgrade.ReadInput(submittedInput).Releases);

            using var requesterLogout = await Http.GetAsync("/Account/LogOff");
            Assert.AreEqual(HttpStatusCode.Found, requesterLogout.StatusCode);
            await LoginAsAdmin();
            var newId = new LockboxId(newRequest.Id).ToString();
            var queue = Uri.UnescapeDataString(await Http.GetStringAsync(approvalQueue));
            StringAssert.Contains(queue, newId);
            Assert.DoesNotContain(id, queue);
            var newDetail = "/Dms/Lockbox?id=" + Uri.EscapeDataString(newId);
            using var approve = await PostForm("/Dms/Decide",
                new Dictionary<string, string> { ["id"] = newId, ["approve"] = "true" }, newDetail);
            AssertRedirect(approve, "/Dms/Lockbox", exact: false);
            StringAssert.Contains(await Http.GetStringAsync(newDetail), "View workflow execution");
        }
        finally { File.Delete(metadata); }
    }

    [TestMethod]
    public async Task FailedBatchShowsCompletedMachinesAndIndividualUpgradeResults()
    {
        using var scope = Server!.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<TemplateDbContext>();
        var admin = await services.GetRequiredService<UserManager<User>>().FindByEmailAsync("admin@default.com");
        var updated = new Machine { Name = "Updated machine" };
        var blocked = new Machine { Name = "Blocked machine" };
        var root = new WorkflowRun { Definition = WorkflowNames.Batch, State = RunState.Failed };
        root.RootId = root.Id;
        var box = new LockboxRequest
        {
            ClientRequestId = Guid.NewGuid(), RequesterId = admin!.Id, Definition = WorkflowNames.AgentUpgrade,
            Version = MachineWorkflows.AgentUpgrade.Version, Risk = RiskLevel.Medium, WorkflowId = root.Id,
            Decision = DecisionState.Approved, TargetMachines = JsonSerializer.Serialize(new[] { updated.Id, blocked.Id })
        };
        root.LockboxId = box.Id;
        var successful = new WorkflowRun
        {
            RootId = root.Id, ParentId = root.Id, LockboxId = box.Id, MachineId = updated.Id, IsMachineTask = true,
            Definition = WorkflowNames.AgentUpgrade, State = RunState.Succeeded,
            Result = JsonSerializer.Serialize(new AgentUpgradeResult("1.11.0.0", "1.31.0.0", "linux-x64", new string('a', 64), Guid.NewGuid()))
        };
        var failed = new WorkflowRun
        {
            RootId = root.Id, ParentId = root.Id, LockboxId = box.Id, MachineId = blocked.Id, IsMachineTask = true,
            Definition = WorkflowNames.AgentUpgrade, State = RunState.Failed,
            Result = JsonSerializer.Serialize(new { Error = "Agent download timed out." })
        };
        db.Machines.AddRange(updated, blocked);
        db.Lockboxes.Add(box);
        db.WorkflowRuns.AddRange(root, successful, failed);
        await db.SaveChangesAsync();

        await LoginAsAdmin();
        var queue = await Http.GetStringAsync("/Dms/Lockboxes?scope=approved");
        StringAssert.Contains(queue, "1 of 2 machines completed successfully");
        var operation = await Http.GetStringAsync($"/Dms/Operation/{root.Id}");
        StringAssert.Contains(operation, "Updated machine");
        StringAssert.Contains(operation, "Upgraded from 1.11.0.0 to 1.31.0.0");
        StringAssert.Contains(operation, "Blocked machine");
        StringAssert.Contains(operation, "Agent download timed out.");
    }

    [TestMethod]
    public async Task BootstrapIncludesControllerOriginAndServesOnlyThePackagedTool()
    {
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync("/agent/install.sh")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync("/agent/package")).StatusCode);
        var directory = Path.Combine(Server!.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "agent-assets");
        Directory.CreateDirectory(directory);
        var installer = Path.Combine(directory, "install.sh");
        var package = Path.Combine(directory, "agent.nupkg");
        try
        {
            await File.WriteAllTextAsync(installer, "#!/bin/bash\ncontroller=''\n");
            await File.WriteAllBytesAsync(package, [1, 2, 3]);
            var response = await Http.GetAsync("/agent/install.sh");
            response.EnsureSuccessStatusCode();
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "controller='" + Http.BaseAddress!.GetLeftPart(UriPartial.Authority) + "'");
            Assert.IsTrue(response.Headers.CacheControl!.NoStore);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await Http.GetByteArrayAsync("/agent/package"));
            Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync("/agent/download/unsupported")).StatusCode);
        }
        finally { File.Delete(installer); File.Delete(package); }
    }

    [TestMethod]
    public async Task CliFreezesReleaseAndMediumRiskApprovalRejectsAssetChanges()
    {
        var environment = Server!.Services.GetRequiredService<IWebHostEnvironment>();
        var directory = Path.Combine(environment.ContentRootPath, "agent-assets");
        Directory.CreateDirectory(directory);
        var metadata = Path.Combine(directory, "releases.json");
        Assert.IsFalse(File.Exists(metadata));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await Http.GetAsync("/agent/releases")).StatusCode);
        var release = new AgentRelease("linux-x64", "1.0.16.0", new string('a', 64), new string('b', 64));
        var input = new AgentUpgradeInput([release, release with { Architecture = "linux-arm64" }]);
        File.WriteAllText(metadata, JsonSerializer.Serialize(input.Releases));
        var key = DmsKeyAuthenticationHandler.CreateKey(false);
        Guid machineId; string userId;
        try
        {
            using (var scope = Server.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
                var user = new User { Id = Guid.NewGuid().ToString(), UserName = "upgrade-" + Guid.NewGuid().ToString("N"), DisplayName = "Upgrade operator" };
                db.Users.Add(user);
                userId = user.Id;

                var machine = new Machine { Name = "Upgrade target" }; machineId = machine.Id;
                db.Machines.Add(machine);
                db.PersonalApiKeys.Add(new PersonalApiKey { UserId = user.Id, Name = "Upgrade CLI", KeyHash = DmsKeyAuthenticationHandler.Hash(key), ExpiresAt = DateTime.UtcNow.AddHours(1) });
                DmsTestPermissions.Risk(db, user.Id, RiskLevel.Medium, request: true, automatic: false, approve: true);
                await db.SaveChangesAsync();
            }
            var cli = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "agent", "upgrade",
                "--machine-id", machineId.ToString(), "--reason", "Upgrade to the frozen Controller release", "--server", Http.BaseAddress!.ToString(), "--api-key", key]);
            Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
            using var inspection = Server.Services.CreateScope();
            var database = inspection.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var request = await database.Lockboxes.SingleAsync(l => l.RequesterId == userId);
            Assert.AreEqual(RiskLevel.Medium, request.Risk);
            Assert.AreEqual(DecisionState.Pending, request.Decision);
            CollectionAssert.AreEqual(new List<Guid> { machineId }, JsonSerializer.Deserialize<List<Guid>>(request.TargetMachines)!);
            var parameters = JsonSerializer.Deserialize<OperationRequest>(request.Parameters)!;
            CollectionAssert.AreEqual(input.Releases, MachineWorkflows.AgentUpgrade.ReadInput(parameters.Input).Releases);
            var service = inspection.ServiceProvider.GetRequiredService<WorkflowService>();
            Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => service.DecideAsync(userId, request.Id, true, "No implicit self approval"))).StatusCode);
            DmsTestPermissions.Set(database, userId, AppPermissionNames.CanApproveOwnWorkflow);
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
            Assert.AreEqual(WorkflowNames.Batch, root.Definition); Assert.IsNull(root.ParentId);
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
