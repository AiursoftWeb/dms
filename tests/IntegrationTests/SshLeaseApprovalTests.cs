using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public sealed class SshLeaseApprovalTests : TestBase
{
    [TestMethod]
    public async Task OnlyTheApprovedRunningAgentCanGetTheSelectedKeyAndRevocationStillWins()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var keys = scope.ServiceProvider.GetRequiredService<PersonalSshKeys>();
        var requester = new User { UserName = "lease-requester", DisplayName = "Lease requester" };
        var approver = new User { UserName = "lease-approver", DisplayName = "Lease approver" };
        db.Users.AddRange(requester, approver);
        DmsTestPermissions.Risk(db, requester.Id, RiskLevel.VeryHigh, request: true);
        DmsTestPermissions.Risk(db, approver.Id, RiskLevel.VeryHigh, approve: true);
        var expectedAgent = DmsKeyAuthenticationHandler.CreateKey(machine: true);
        var otherAgent = DmsKeyAuthenticationHandler.CreateKey(machine: true);
        var target = new Machine { Name = "Lease target", KeyHash = DmsKeyAuthenticationHandler.Hash(expectedAgent) };
        db.Machines.AddRange(target, new Machine { Name = "Second agent", KeyHash = DmsKeyAuthenticationHandler.Hash(otherAgent) });
        var (publicKey, fingerprint) = SshPublicKey.Parse(
            "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHpyp+C8Ia2B6RVM1UCr5HG+EtGR2xz5OF05P9kPz84D");
        var key = new PersonalSshKey
        {
            UserId = requester.Id, Slot = 1, VerifiedAt = DateTime.UtcNow, Name = "Fixture key",
            PublicKey = publicKey, Fingerprint = fingerprint
        };
        db.PersonalSshKeys.Add(key);
        await db.SaveChangesAsync();

        var definition = OperationsWorkflows.SshOpen;
        var request = definition.Request(new SshOpenInput(key.Id), "Test an approved lease", [target.Id]);
        var item = await workflows.SubmitAsync(requester.Id, request);
        Assert.AreEqual(DecisionState.Pending, item.Decision);
        Assert.AreEqual(RiskLevel.VeryHigh, item.Risk);
        Assert.IsNull(item.WorkflowId);
        var approved = JsonSerializer.Deserialize<OperationRequest>(item.Parameters)!.Input;
        Assert.AreEqual(fingerprint, definition.ReadInput(approved).Fingerprint);

        item = await workflows.DecideAsync(approver.Id, item.Id, approve: true, "Test approval");
        var task = new WorkflowRun
        {
            ParentId = item.WorkflowId, RootId = item.WorkflowId!.Value, LockboxId = item.Id, MachineId = target.Id,
            IsMachineTask = true, Definition = definition.Name, Version = definition.Version,
            Input = approved, State = RunState.Offered
        };
        db.WorkflowRuns.Add(task);
        await db.SaveChangesAsync();

        using var before = await AuthorizeAsync(task.Id, expectedAgent);
        Assert.AreEqual(HttpStatusCode.Forbidden, before.StatusCode);
        task.State = RunState.Running;
        await db.SaveChangesAsync();
        using var foreign = await AuthorizeAsync(task.Id, otherAgent);
        Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var reply = await AuthorizeAsync(task.Id, expectedAgent);
        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        Assert.AreEqual(new SshLeaseAuthorization(key.Id, requester.Id, publicKey),
            await reply.Content.ReadFromJsonAsync<SshLeaseAuthorization>());

        await keys.RevokeAsync(requester.Id, key.Id);
        var retry = definition.Request(new SshOpenInput(key.Id), request.Reason, [target.Id], requestId: request.RequestId);
        Assert.AreEqual(item.Id, (await workflows.SubmitAsync(requester.Id, retry)).Id);
        using var after = await AuthorizeAsync(task.Id, expectedAgent);
        Assert.AreEqual(HttpStatusCode.Conflict, after.StatusCode);
    }

    private async Task<HttpResponseMessage> AuthorizeAsync(Guid task, string key)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/ssh-leases/{task:D}/authorize");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await Http.SendAsync(message);
    }
}
