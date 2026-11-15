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
    private async Task<(string User, string Key, Guid Cluster)> ArrangeAsync(bool automatic = false)
    {
        using var scope = Server!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var suffix = Guid.NewGuid().ToString("N");
        var user = new User { UserName = "join" + suffix, Email = "join" + suffix + "@example.test", DisplayName = "Enrollment operator" };
        Assert.IsTrue((await users.CreateAsync(user)).Succeeded);
        Assert.IsTrue((await users.AddClaimAsync(user, new System.Security.Claims.Claim(AppPermissions.Type, "CanManageDmsAccess"))).Succeeded);
        var raw = DmsKeyAuthenticationHandler.CreateKey(false);
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        db.PersonalApiKeys.Add(new PersonalApiKey { UserId = user.Id, Name = "Enrollment fixture", KeyHash = DmsKeyAuthenticationHandler.Hash(raw), ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        var cluster = await CliAsync<ClusterSummary>(raw, "cluster", "create", "--name", "Join-" + suffix);
        var flags = automatic ? new[] { "--can-request", "--can-approve", "--auto-approve" } : ["--can-request", "--can-approve"];
        await CliAsync<RiskGrant>(raw, ["access", "set-grant", "--cluster-id", cluster.Id.ToString(), "--user-id", user.Id, "--risk", "Medium", .. flags]);
        return (user.Id, raw, cluster.Id);
    }

    private async Task<T> CliAsync<T>(string key, params string[] args)
    {
        var result = await Client.DmsCommandApp.Create().TestRunAsync([.. args, "--server", Http.BaseAddress!.ToString(), "--api-key", key, "--output-format", "json"]);
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

    private static EnrollmentInput Input(string name = "node") => new(Guid.NewGuid(), Guid.NewGuid(), name, "fixture-node",
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
    public async Task JoinKeysAndReceiptsCannotUseHumanOrAgentCapabilities()
    {
        var setup = await ArrangeAsync();
        var cluster = (await CliAsync<List<ClusterSummary>>(setup.Key, "cluster", "list")).Single(c => c.Id == setup.Cluster);
        var issued = await CliAsync<IssuedJoinKey>(setup.Key, "access", "create-join-key", "--cluster-id", setup.Cluster.ToString(), "--name", "Bootstrap", "--days", "1");
        var input = Input(cluster.Name); var submitted = await SubmitAsync(issued.Key, input);
        Assert.AreEqual("PendingApproval", submitted.State); Assert.IsNull(submitted.MachineId);
        Assert.AreEqual(input.Id, submitted.Id); Assert.AreEqual(setup.Cluster, submitted.ClusterId); Assert.AreEqual(input.MachineName, submitted.MachineName);
        Assert.AreEqual(setup.Cluster, issued.Details.ClusterId); Assert.AreEqual("Bootstrap", issued.Details.Name);
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
        Assert.AreEqual(HttpStatusCode.Conflict, (await RequestAsync(issued.Key, HttpMethod.Post, "/api/v1/agent/enrollment", Input("wrong cluster"))).Status);
        var listed = await CliAsync<List<JoinKeySummary>>(setup.Key, "access", "list-join-keys", "--cluster-id", setup.Cluster.ToString());
        Assert.AreEqual(issued.Details.Id, listed.Single().Id); Assert.AreEqual(setup.User, listed.Single().IssuerId);
        Assert.DoesNotContain(issued.Key, Newtonsoft.Json.JsonConvert.SerializeObject(listed));
        Assert.AreEqual(HttpStatusCode.Conflict, (await RequestAsync(setup.Key, HttpMethod.Delete, $"/api/v1/dms/clusters/{setup.Cluster}")).Status);
    }

    [TestMethod]
    public async Task EnrollmentIsIdempotentAndCannotCreateAMachineBeforeIndependentApproval()
    {
        var setup = await ArrangeAsync();
        var cluster = (await CliAsync<List<ClusterSummary>>(setup.Key, "cluster", "list")).Single(c => c.Id == setup.Cluster);
        var issued = await CliAsync<IssuedJoinKey>(setup.Key, "access", "create-join-key", "--cluster-id", setup.Cluster.ToString(), "--name", "Reusable");
        var input = Input(cluster.Name); var submitted = await SubmitAsync(issued.Key, input);
        Assert.AreEqual(submitted, await SubmitAsync(issued.Key, input));
        Assert.AreEqual(HttpStatusCode.Conflict, (await RequestAsync(issued.Key, HttpMethod.Post, "/api/v1/agent/enrollment", input with { MachineName = "tampered" })).Status);
        Assert.AreEqual(HttpStatusCode.Conflict, (await RequestAsync(issued.Key, HttpMethod.Post, "/api/v1/agent/enrollment", Input(cluster.Name) with { InstallationId = input.InstallationId })).Status);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await RequestAsync(setup.Key, HttpMethod.Post, $"/api/v1/dms/lockboxes/{submitted.LockboxId}/decision", new { Approve = true, Reason = "No self approval" })).Status);
        using (var before = Server!.Services.CreateScope())
        {
            var db = before.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(0, await db.Machines.CountAsync(m => m.ClusterId == setup.Cluster));
            Assert.AreEqual(1, await db.Lockboxes.CountAsync(b => b.ClusterId == setup.Cluster));
            Assert.AreEqual(DmsKeyAuthenticationHandler.Hash(issued.Key), (await db.JoinKeys.SingleAsync(k => k.Id == issued.Details.Id)).KeyHash);
        }
        await CliAsync<SelfApprovalInput>(setup.Key, "access", "set-self-approval", "--user-id", setup.User, "--enabled");
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
        var cluster = (await CliAsync<List<ClusterSummary>>(setup.Key, "cluster", "list")).Single(c => c.Id == setup.Cluster);
        var issued = await CliAsync<IssuedJoinKey>(setup.Key, "access", "create-join-key", "--cluster-id", setup.Cluster.ToString(), "--name", "Approved nodes");
        var first = Input(cluster.Name); await SubmitAsync(issued.Key, first); var joined = await JoinedAsync(first.ReceiptKey);
        await CliAsync<JoinKeySummary>(setup.Key, "access", "revoke-join-key", "--id", issued.Details.Id.ToString());
        Assert.AreEqual("Joined", (await StatusAsync(first.ReceiptKey)).State);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await RequestAsync(issued.Key, HttpMethod.Post, "/api/v1/agent/enrollment", Input(cluster.Name))).Status);
        var pendingKey = await CliAsync<IssuedJoinKey>(setup.Key, "access", "create-join-key", "--cluster-id", setup.Cluster.ToString(), "--name", "Pending nodes");
        await CliAsync<RiskGrant>(setup.Key, "access", "set-grant", "--cluster-id", setup.Cluster.ToString(), "--user-id", setup.User, "--risk", "Medium", "--can-request", "--can-approve");
        var pending = Input(cluster.Name); var request = await SubmitAsync(pendingKey.Key, pending);
        await CliAsync<JoinKeySummary>(setup.Key, "access", "revoke-join-key", "--id", pendingKey.Details.Id.ToString());
        Assert.AreEqual("Unavailable", (await StatusAsync(pending.ReceiptKey)).State);
        await CliAsync<SelfApprovalInput>(setup.Key, "access", "set-self-approval", "--user-id", setup.User, "--enabled");
        await CliAsync<object>(setup.Key, "lockbox", "approve", "--id", request.LockboxId.ToString(), "--reason", "Late approval must not activate revoked enrollment");
        var expired = "dmsj_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.JoinKeys.Add(new JoinKey { ClusterId = setup.Cluster, IssuerId = setup.User, Name = "Expired fixture",
                KeyHash = DmsKeyAuthenticationHandler.Hash(expired), ExpiresAt = DateTime.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync();
        }
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await RequestAsync(expired, HttpMethod.Post, "/api/v1/agent/enrollment", Input(cluster.Name))).Status);
        await Task.Delay(1500);
        using var check = Server!.Services.CreateScope(); var verify = check.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(1, await verify.Machines.CountAsync(m => m.ClusterId == setup.Cluster));
        Assert.AreEqual(joined.MachineId, (await verify.Machines.SingleAsync(m => m.ClusterId == setup.Cluster)).Id);
    }

    [TestMethod]
    public async Task AgentJoinPersistsPrivateIdentityAndResumesWithoutPersistingTheJoinKey()
    {
        var setup = await ArrangeAsync();
        var cluster = (await CliAsync<List<ClusterSummary>>(setup.Key, "cluster", "list")).Single(c => c.Id == setup.Cluster);
        var issued = await CliAsync<IssuedJoinKey>(setup.Key, "access", "create-join-key", "--cluster-id", setup.Cluster.ToString(), "--name", "Agent command");
        var state = Directory.CreateTempSubdirectory("DmsJoin-").FullName;
        try
        {
            var args = new[] { "join", "--controller-url", Http.BaseAddress!.ToString(), "--api-key", issued.Key, "--cluster-name", cluster.Name, "--state-path", state };
            // Simulate an interruption after private state was saved but before the Controller received the request.
            using (var journal = new TaskJournal(Path.Combine(state, "journal")))
                new AgentConfiguration(AgentConfiguration.ControllerUrl(Http.BaseAddress.ToString()), cluster.Name, Guid.NewGuid(), journal.InstallationId,
                    "resumed-node", "dmsr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), DmsKeyAuthenticationHandler.CreateKey(true)).Save(state);
            var result = await AgentCommandApp.Create().TestRunAsync(args); Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
            var config = AgentConfiguration.Load(state)!;
            Assert.IsNotNull(config); Assert.IsNull(config.MachineId);
            Assert.DoesNotContain(issued.Key, File.ReadAllText(Path.Combine(state, "connection.json")));
            Assert.DoesNotContain(issued.Key, result.StdOut + result.StdErr);
            var retry = await AgentCommandApp.Create().TestRunAsync(args); Assert.AreEqual(0, retry.ProgramReturn, retry.StdErr);
            Assert.AreEqual(config, AgentConfiguration.Load(state));
            var pending = await StatusAsync(config.ReceiptKey);
            await CliAsync<SelfApprovalInput>(setup.Key, "access", "set-self-approval", "--user-id", setup.User, "--enabled");
            await CliAsync<object>(setup.Key, "lockbox", "deny", "--id", pending.LockboxId.ToString(), "--reason", "Retry enrollment fixture");
            var newAttempt = await AgentCommandApp.Create().TestRunAsync(args); Assert.AreEqual(0, newAttempt.ProgramReturn, newAttempt.StdErr);
            var next = AgentConfiguration.Load(state)!; Assert.AreEqual(config.InstallationId, next.InstallationId); Assert.AreNotEqual(config.EnrollmentId, next.EnrollmentId);
            await CliAsync<SelfApprovalInput>(setup.Key, "access", "set-self-approval", "--user-id", setup.User, "--enabled");
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
