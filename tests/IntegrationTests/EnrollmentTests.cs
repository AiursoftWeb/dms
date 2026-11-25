using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class EnrollmentTests : TestBase
{
    private async Task<(string User, string Key)> ArrangeAsync(bool automatic = false)
    {
        using var scope = Server!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var suffix = Guid.NewGuid().ToString("N");
        var user = new User { UserName = "join" + suffix, Email = "join" + suffix + "@example.test", DisplayName = "Enrollment operator" };
        Assert.IsTrue((await users.CreateAsync(user)).Succeeded);
        Assert.IsTrue((await users.AddClaimAsync(user, new System.Security.Claims.Claim(AppPermissions.Type, "CanManageDmsAccess"))).Succeeded);
        var raw = DmsKeyAuthenticationHandler.CreateKey(false);
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        DmsTestPermissions.DeactivatePreviousMachines(db);
        db.PersonalApiKeys.Add(new PersonalApiKey { UserId = user.Id, Name = "Enrollment fixture", KeyHash = DmsKeyAuthenticationHandler.Hash(raw), ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        DmsTestPermissions.Risk(db, user.Id, RiskLevel.Medium, request: true, automatic: automatic, approve: true);
        await db.SaveChangesAsync();
        return (user.Id, raw);
    }

    private async Task SetSelfApprovalAsync(string user)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        DmsTestPermissions.Set(db, user, AppPermissionNames.CanApproveOwnWorkflow);
        await db.SaveChangesAsync();
    }

    private async Task<T> CliAsync<T>(string key, params string[] args)
    {
        var result = await Client.DmsCommandApp.Create().TestRunAsync([.. args, "--dms-server-endpoint", Http.BaseAddress!.ToString(), "--api-key", key, "--output-format", "json"]);
        Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
        return JObject.Parse(result.StdOut)["Data"]!.ToObject<T>()!;
    }

    private async Task<(HttpStatusCode Status, JObject Body)> RequestAsync(string key, HttpMethod method, string path, object? data = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (data != null) request.Content = JsonContent.Create(data);
        using var response = await Http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType == "application/json" ? JObject.Parse(content) : new JObject());
    }

    private static EnrollmentInput Input(string name = "node") => new(Guid.NewGuid(), Guid.NewGuid(), name,
        "dmsr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), DmsKeyAuthenticationHandler.CreateKey(true));
    private async Task<EnrollmentStatus> SubmitAsync(string key, EnrollmentInput input)
    {
        var result = await RequestAsync(key, HttpMethod.Post, "/api/v1/agent/enrollment", input);
        Assert.AreEqual(HttpStatusCode.OK, result.Status, result.Body.ToString());
        return result.Body["Data"]!.ToObject<EnrollmentStatus>()!;
    }
    private async Task<EnrollmentStatus> StatusAsync(string receipt)
    {
        var result = await RequestAsync(receipt, HttpMethod.Get, "/api/v1/agent/enrollment");
        Assert.AreEqual(HttpStatusCode.OK, result.Status);
        return result.Body["Data"]!.ToObject<EnrollmentStatus>()!;
    }
    private async Task<EnrollmentStatus> JoinedAsync(string receipt)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await StatusAsync(receipt);
            if (status.State == "Joined") return status;
            await Task.Delay(50);
        }
        throw new AssertFailedException("The approved enrollment did not complete.");
    }

    [TestMethod]
    public async Task JoinKeyUsesIssuersCurrentRequestPermission()
    {
        var setup = await ArrangeAsync();
        var issued = await CliAsync<IssuedJoinKey>(setup.Key, "access", "create-join-key", "--name", "Permission revocation");
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            DmsTestPermissions.Set(db, setup.User, DmsPermissions.Request(RiskLevel.Medium), false);
            await db.SaveChangesAsync();
        }
        var input = Input();
        Assert.AreEqual(HttpStatusCode.Forbidden, (await RequestAsync(issued.Key, HttpMethod.Post, "/api/v1/agent/enrollment", input)).Status);
        using var check = Server!.Services.CreateScope();
        var database = check.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.IsFalse(await database.AgentEnrollments.AnyAsync(e => e.InstallationId == input.InstallationId));
    }

    [TestMethod]
    public async Task JoinKeysAndReceiptsCannotUseHumanOrAgentCapabilities()
    {
        var setup = await ArrangeAsync();
        var issued = await CliAsync<IssuedJoinKey>(setup.Key, "access", "create-join-key", "--name", "Bootstrap", "--days", "1");
        var input = Input(); var submitted = await SubmitAsync(issued.Key, input);
        Assert.AreEqual("PendingApproval", submitted.State); Assert.IsNull(submitted.MachineId);
        Assert.AreEqual(input.Id, submitted.Id); Assert.AreEqual(input.MachineName, submitted.MachineName);
        Assert.AreEqual("Bootstrap", issued.Details.Name);
        Assert.IsFalse(issued.Details.Revoked); Assert.IsTrue(issued.Details.ExpiresAt > DateTime.UtcNow);
        foreach (var key in new[] { issued.Key, input.ReceiptKey })
        {
            foreach (var path in new[] { "/api/v1/dms/access/identity", "/api/v1/dms/access", "/api/agent/channel" })
                Assert.AreEqual(HttpStatusCode.Forbidden, (await RequestAsync(key, HttpMethod.Get, path)).Status);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await RequestAsync(key, HttpMethod.Post, $"/api/v1/dms/lockboxes/{submitted.LockboxId}/decision", new { Approve = true, Reason = "Forbidden" })).Status);
        }
        Assert.AreEqual(HttpStatusCode.Forbidden, (await RequestAsync(setup.Key, HttpMethod.Post, "/api/v1/agent/enrollment", input)).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await RequestAsync(input.ReceiptKey, HttpMethod.Post, "/api/v1/agent/enrollment", input)).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await RequestAsync(issued.Key, HttpMethod.Get, "/api/v1/agent/enrollment")).Status);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await RequestAsync(input.AgentKey, HttpMethod.Get, "/api/agent/channel")).Status);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await RequestAsync(issued.Key, HttpMethod.Post, "/api/v1/agent/enrollment", Input(""))).Status);
        var listed = await CliAsync<List<JoinKeySummary>>(setup.Key, "access", "list-join-keys");
        Assert.AreEqual(issued.Details.Id, listed.Single(k => k.Id == issued.Details.Id).Id); Assert.AreEqual(setup.User, listed.Single(k => k.Id == issued.Details.Id).IssuerId);
        Assert.DoesNotContain(issued.Key, Newtonsoft.Json.JsonConvert.SerializeObject(listed));
    }

    [TestMethod]
    public async Task EnrollmentIsIdempotentAndCannotCreateAMachineBeforeIndependentApproval()
    {
        var setup = await ArrangeAsync();
        var issued = await CliAsync<IssuedJoinKey>(setup.Key, "access", "create-join-key", "--name", "Reusable");
        var input = Input(); var submitted = await SubmitAsync(issued.Key, input);
        Assert.AreEqual(submitted, await SubmitAsync(issued.Key, input));
        Assert.AreEqual(HttpStatusCode.Conflict, (await RequestAsync(issued.Key, HttpMethod.Post, "/api/v1/agent/enrollment", input with { MachineName = "tampered" })).Status);
        Assert.AreEqual(HttpStatusCode.Conflict, (await RequestAsync(issued.Key, HttpMethod.Post, "/api/v1/agent/enrollment", Input() with { InstallationId = input.InstallationId })).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await RequestAsync(setup.Key, HttpMethod.Post, $"/api/v1/dms/lockboxes/{submitted.LockboxId}/decision", new { Approve = true, Reason = "No self approval" })).Status);
        using (var before = Server!.Services.CreateScope())
        {
            var db = before.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(0, await db.Machines.CountAsync(m => m.InstallationId == input.InstallationId));
            Assert.AreEqual(1, await db.Lockboxes.CountAsync(b => b.RequesterId == setup.User));
            Assert.AreEqual(DmsKeyAuthenticationHandler.Hash(issued.Key), (await db.JoinKeys.SingleAsync(k => k.Id == issued.Details.Id)).KeyHash);
        }
        await SetSelfApprovalAsync(setup.User);
        await CliAsync<object>(setup.Key, "lockbox", "approve", "--id", submitted.LockboxId.ToString(), "--reason", "Explicit root attribute fixture");
        var joined = await JoinedAsync(input.ReceiptKey);
        Assert.AreEqual(input.InstallationId, joined.InstallationId); Assert.IsNotNull(joined.MachineId);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await RequestAsync(input.AgentKey, HttpMethod.Get, "/api/v1/dms/access/identity")).Status);
        using var check = Server!.Services.CreateScope(); var verify = check.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var machine = await verify.Machines.SingleAsync(m => m.Id == joined.MachineId);
        Assert.AreEqual(input.InstallationId, machine.InstallationId); Assert.AreEqual(DmsKeyAuthenticationHandler.Hash(input.AgentKey), machine.KeyHash);
        Assert.IsFalse(await verify.AuditEvents.AnyAsync(a => a.Detail.Contains(issued.Key) || a.Detail.Contains(input.AgentKey) || a.Detail.Contains(input.ReceiptKey)));
    }

    [TestMethod]
    public async Task RevokedAndExpiredKeysBlockNewOrPendingEnrollmentButNotApprovedMachines()
    {
        var setup = await ArrangeAsync(true);
        var issued = await CliAsync<IssuedJoinKey>(setup.Key, "access", "create-join-key", "--name", "Approved nodes");
        var first = Input(); await SubmitAsync(issued.Key, first); var joined = await JoinedAsync(first.ReceiptKey);
        await CliAsync<JoinKeySummary>(setup.Key, "access", "revoke-join-key", "--id", issued.Details.Id.ToString());
        Assert.AreEqual("Joined", (await StatusAsync(first.ReceiptKey)).State);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await RequestAsync(issued.Key, HttpMethod.Post, "/api/v1/agent/enrollment", Input())).Status);
        var pendingKey = await CliAsync<IssuedJoinKey>(setup.Key, "access", "create-join-key", "--name", "Pending nodes");
        using (var permissionScope = Server!.Services.CreateScope())
        {
            var permissionDb = permissionScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            DmsTestPermissions.Set(permissionDb, setup.User, DmsPermissions.AutoApprove(RiskLevel.Medium), false);
            await permissionDb.SaveChangesAsync();
        }
        var pending = Input(); var request = await SubmitAsync(pendingKey.Key, pending);
        await CliAsync<JoinKeySummary>(setup.Key, "access", "revoke-join-key", "--id", pendingKey.Details.Id.ToString());
        Assert.AreEqual("Unavailable", (await StatusAsync(pending.ReceiptKey)).State);
        await SetSelfApprovalAsync(setup.User);
        await CliAsync<object>(setup.Key, "lockbox", "approve", "--id", request.LockboxId.ToString(), "--reason", "Late approval must not activate revoked enrollment");
        var expired = "dmsj_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.JoinKeys.Add(new JoinKey { IssuerId = setup.User, Name = "Expired fixture",
                KeyHash = DmsKeyAuthenticationHandler.Hash(expired), ExpiresAt = DateTime.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync();
        }
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await RequestAsync(expired, HttpMethod.Post, "/api/v1/agent/enrollment", Input())).Status);
        await Task.Delay(1500);
        using var check = Server!.Services.CreateScope(); var verify = check.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(1, await verify.Machines.CountAsync(m => m.InstallationId == first.InstallationId));
        Assert.AreEqual(joined.MachineId, (await verify.Machines.SingleAsync(m => m.InstallationId == first.InstallationId)).Id);
    }

    [TestMethod]
    public async Task JoinedAgentRewritesLegacyConfigurationWhilePreservingIdentityAndJournal()
    {
        var state = Directory.CreateTempSubdirectory("DmsExistingIdentity-").FullName;
        try
        {
            Guid installation; var task = Guid.NewGuid(); var machine = Guid.NewGuid();
            using (var journal = new TaskJournal(Path.Combine(state, "journal")))
            {
                installation = journal.InstallationId;
                journal.Accept(new AgentMessage { TaskId = task, Definition = WorkflowNames.AgentStatus, Version = MachineWorkflows.AgentStatus.Version });
                Assert.IsNotNull(journal.Start(task));
            }
            var configuration = new AgentConfiguration("http://controller.invalid", Guid.NewGuid(), installation, "existing-node", "", "dmsa_existing", machine);
            var json = System.Text.Json.JsonSerializer.SerializeToNode(configuration)!;
            json["ClusterName"] = "former-resource";
            await File.WriteAllTextAsync(Path.Combine(state, "connection.json"), json.ToJsonString());
            var result = await AgentCommandApp.Create().TestRunAsync(["join", "--controller-url", configuration.Controller,
                "--api-key", "not-used-for-an-already-joined-installation", "--state-path", state]);
            Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
            Assert.AreEqual(configuration, AgentConfiguration.Load(state));
            Assert.DoesNotContain("ClusterName", await File.ReadAllTextAsync(Path.Combine(state, "connection.json")));
            using var recovered = new TaskJournal(Path.Combine(state, "journal"));
            Assert.AreEqual(installation, recovered.InstallationId);
            Assert.AreEqual(task, recovered.Snapshot().Single().Id);
            Assert.AreEqual(RunState.NeedsVerification, recovered.Snapshot().Single().State);
            Assert.IsNull(recovered.Start(task));
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task InstalledAgentReportsInvalidStateWithoutReplacingItsIdentity()
    {
        var state = Directory.CreateTempSubdirectory("DmsInvalidState-").FullName;
        try
        {
            var args = new[] { "run", "--state-path", state };
            var missing = await AgentCommandApp.Create().TestRunAsync(args);
            Assert.AreEqual(1, missing.ProgramReturn);
            Assert.AreEqual("Error: Run dms-agent join first." + Environment.NewLine, missing.StdErr);
            Assert.AreEqual("", missing.StdOut);
            var file = Path.Combine(state, "connection.json");
            await File.WriteAllTextAsync(file, "{");
            var corrupt = await AgentCommandApp.Create().TestRunAsync(args);
            Assert.AreEqual(1, corrupt.ProgramReturn);
            Assert.AreEqual("Error: Invalid Agent state. Restore the existing state before restarting." + Environment.NewLine, corrupt.StdErr);
            Assert.AreEqual("{", await File.ReadAllTextAsync(file));
            using (new TaskJournal(Path.Combine(state, "journal")))
                new AgentConfiguration("http://localhost:5000", Guid.NewGuid(), Guid.Empty,
                    "fixture-node", "unused-receipt", "unused-key").Save(state);
            var original = await File.ReadAllTextAsync(file);
            var mismatch = await AgentCommandApp.Create().TestRunAsync(args);
            Assert.AreEqual(1, mismatch.ProgramReturn);
            Assert.AreEqual("Error: The Agent configuration and journal identities differ." + Environment.NewLine, mismatch.StdErr);
            Assert.AreEqual(original, await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(state, true); }
    }

    [TestMethod]
    public async Task AgentJoinPersistsPrivateIdentityAndResumesWithoutPersistingTheJoinKey()
    {
        var setup = await ArrangeAsync();
        var issued = await CliAsync<IssuedJoinKey>(setup.Key, "access", "create-join-key", "--name", "Agent command");
        var state = Directory.CreateTempSubdirectory("DmsJoin-").FullName;
        try
        {
            var args = new[] { "join", "--controller-url", Http.BaseAddress!.ToString(), "--api-key", issued.Key, "--state-path", state };
            // Simulate an interruption after private state was saved but before the Controller received the request.
            using (var journal = new TaskJournal(Path.Combine(state, "journal")))
                new AgentConfiguration(AgentConfiguration.ControllerUrl(Http.BaseAddress.ToString()), Guid.NewGuid(), journal.InstallationId,
                    "resumed-node", "dmsr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), DmsKeyAuthenticationHandler.CreateKey(true)).Save(state);
            var result = await AgentCommandApp.Create().TestRunAsync(args); Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
            var config = AgentConfiguration.Load(state)!;
            Assert.IsNotNull(config); Assert.IsNull(config.MachineId);
            Assert.DoesNotContain(issued.Key, File.ReadAllText(Path.Combine(state, "connection.json")));
            Assert.DoesNotContain(issued.Key, result.StdOut + result.StdErr);
            var retry = await AgentCommandApp.Create().TestRunAsync(args); Assert.AreEqual(0, retry.ProgramReturn, retry.StdErr);
            Assert.AreEqual(config, AgentConfiguration.Load(state));
            var pending = await StatusAsync(config.ReceiptKey);
            await SetSelfApprovalAsync(setup.User);
            await CliAsync<object>(setup.Key, "lockbox", "deny", "--id", pending.LockboxId.ToString(), "--reason", "Retry enrollment fixture");
            var newAttempt = await AgentCommandApp.Create().TestRunAsync(args); Assert.AreEqual(0, newAttempt.ProgramReturn, newAttempt.StdErr);
            var next = AgentConfiguration.Load(state)!; Assert.AreEqual(config.InstallationId, next.InstallationId); Assert.AreNotEqual(config.EnrollmentId, next.EnrollmentId);
            await SetSelfApprovalAsync(setup.User);
            await CliAsync<object>(setup.Key, "lockbox", "approve", "--id", (await StatusAsync(next.ReceiptKey)).LockboxId.ToString(), "--reason", "Approve retry");
            await JoinedAsync(next.ReceiptKey);
            var active = await AgentCommandApp.Create().TestRunAsync(args); Assert.AreEqual(0, active.ProgramReturn, active.StdErr);
            StringAssert.Contains(active.StdOut, "Already joined");
            var final = AgentConfiguration.Load(state)!; Assert.AreEqual(config.InstallationId, final.InstallationId); Assert.IsNotNull(final.MachineId); Assert.AreEqual("", final.ReceiptKey);
            Assert.IsTrue(File.Exists(Path.Combine(state, "joined")));
            if (!OperatingSystem.IsWindows()) Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(state, "connection.json")));
        }
        finally { Directory.Delete(state, true); }
    }
}
