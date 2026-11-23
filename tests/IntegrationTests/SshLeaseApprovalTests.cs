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

    [TestMethod]
    public async Task LeasePagesShowReadinessAndExplainTheHistoricalBaselineFailure()
    {
        await LoginAsAdmin();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var now = DateTimeOffset.UtcNow;
        var machine = new Machine { Name = "gx10-test", KeyHash = "lease-fixture", LastSeen = now.UtcDateTime };
        var user = new User { UserName = "readiness-owner", DisplayName = "SSH requester" };
        db.Machines.Add(machine);
        db.Users.Add(user);
        var definition = OperationsWorkflows.SshOpen;
        var request = definition.Request(new SshOpenInput(Guid.NewGuid()), "Check readiness", [machine.Id]);
        var box = new LockboxRequest
        {
            Definition = definition.Name, Version = definition.Version, Risk = RiskLevel.VeryHigh,
            RequesterId = user.Id, ClientRequestId = request.RequestId, Reason = request.Reason,
            Parameters = JsonSerializer.Serialize(request), TargetMachines = JsonSerializer.Serialize(request.MachineIds)
        };
        db.Lockboxes.Add(box);
        var observation = new WorkflowRun
        {
            Definition = OperationsWorkflows.SshCheckLease.Name, MachineId = machine.Id, State = RunState.Succeeded,
            CreatedAt = now.UtcDateTime, FinishedAt = now.UtcDateTime
        };
        db.WorkflowRuns.Add(observation);
        string Report(bool? ready, DateTimeOffset observedAt) =>
            JsonSerializer.Serialize(new SshLeaseCheckResult("SSH poll", null)
            {
                Observation = new SshHostObservation(observedAt, OrdinaryOpen: false, LeaseKnown: true)
                {
                    BaselineReady = ready
                }
            });
        async Task<string> Page(string route) => WebUtility.HtmlDecode(await Http.GetStringAsync(route));

        var machinePage = $"/Dms/Machine/{machine.Id}";
        var approvalPage = $"/Dms/Lockbox/{box.Id}";
        (bool? Ready, DateTimeOffset Time, string Heading)[] cases =
        [
            (false, now, "This machine needs SSH setup"),
            (true, now, "Temporary SSH is ready to request"),
            (null, now, "Temporary SSH readiness is awaiting confirmation"),
            (true, now.AddMinutes(-7), "Temporary SSH readiness is awaiting confirmation")
        ];
        foreach (var (ready, time, heading) in cases)
        {
            observation.Result = Report(ready, time);
            await db.SaveChangesAsync();
            StringAssert.Contains(await Page(machinePage), heading);
            StringAssert.Contains(await Page(approvalPage), heading);
        }

        var run = new WorkflowRun { Definition = WorkflowNames.Batch, State = RunState.Failed, LockboxId = box.Id };
        run.RootId = run.Id;
        var failed = new WorkflowRun
        {
            Definition = definition.Name, State = RunState.Failed, MachineId = machine.Id, LockboxId = box.Id,
            RootId = run.Id, ParentId = run.Id, IsMachineTask = true,
            Result = JsonSerializer.Serialize(new { Error = SshOpenInput.BaselineRequiredError })
        };
        box.WorkflowId = run.Id;
        box.Decision = DecisionState.Approved;
        box.DecidedAt = now.UtcDateTime;
        db.WorkflowRuns.AddRange(run, failed);
        await db.SaveChangesAsync();

        foreach (var route in new[] { approvalPage, $"/Dms/Operation/{run.Id}", $"/Dms/Operation/{failed.Id}" })
        {
            var page = await Page(route);
            StringAssert.Contains(page, "gx10-test is not ready for temporary SSH");
            StringAssert.Contains(page, "Submit a new request once the machine is ready.");
        }

        var childPage = await Page($"/Dms/Operation/{failed.Id}");
        StringAssert.Contains(childPage, SshOpenInput.BaselineRequiredError);
        Assert.IsTrue(childPage.IndexOf("gx10-test is not ready for temporary SSH", StringComparison.Ordinal) <
            childPage.IndexOf(SshOpenInput.BaselineRequiredError, StringComparison.Ordinal));
    }

    private async Task<HttpResponseMessage> AuthorizeAsync(Guid task, string key)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/ssh-leases/{task:D}/authorize");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await Http.SendAsync(message);
    }
}
