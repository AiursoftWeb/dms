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
    public async Task OmittedKeyFreezesOnlyTheRequestersVerifiedKeysAndRevocationStopsExecution()
    {
        await LoginAsAdmin();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var keys = scope.ServiceProvider.GetRequiredService<PersonalSshKeys>();
        var requester = new User { UserName = "all-keys-requester", DisplayName = "All-keys requester" };
        var approver = new User { UserName = "all-keys-approver", DisplayName = "All-keys approver" };
        db.Users.AddRange(requester, approver);
        DmsTestPermissions.Risk(db, requester.Id, RiskLevel.VeryHigh, request: true);
        DmsTestPermissions.Risk(db, approver.Id, RiskLevel.VeryHigh, approve: true);
        var apiKey = DmsKeyAuthenticationHandler.CreateKey(machine: false);
        db.PersonalApiKeys.Add(new PersonalApiKey
        {
            UserId = requester.Id, Name = "CLI", KeyHash = DmsKeyAuthenticationHandler.Hash(apiKey),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        var agent = DmsKeyAuthenticationHandler.CreateKey(machine: true);
        var target = new Machine { Name = "All-keys target", KeyHash = DmsKeyAuthenticationHandler.Hash(agent) };
        db.Machines.Add(target);
        await db.SaveChangesAsync();

        var definition = OperationsWorkflows.SshOpen;
        const string reason = "Open SSH with my keys";
        var none = definition.Request(new SshOpenInput(), reason, [target.Id]);
        var missing = await Assert.ThrowsAsync<DmsException>(() => workflows.SubmitAsync(requester.Id, none));
        Assert.AreEqual(409, missing.StatusCode);

        var first = NewKey(1, 1, requester.Id);
        var second = NewKey(2, 2, requester.Id);
        var pending = NewKey(3, 0, requester.Id);
        var revoked = NewKey(4, null, requester.Id);
        var foreign = NewKey(5, 1, approver.Id);
        db.PersonalSshKeys.AddRange(first, second, pending, revoked, foreign);
        await db.SaveChangesAsync();

        var requestId = Guid.NewGuid();
        var cli = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "ssh", "temp-allow-ssh",
            "--machine-id", target.Id.ToString(), "--reason", reason, "--request-id", requestId.ToString(),
            "--server", Http.BaseAddress!.ToString(), "--api-key", apiKey]);
        Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
        var item = db.Lockboxes.Single(box => box.RequesterId == requester.Id && box.ClientRequestId == requestId);
        Assert.AreEqual(DecisionState.Pending, item.Decision);
        var approvedInput = JsonSerializer.Deserialize<OperationRequest>(item.Parameters)!.Input;
        var input = definition.ReadInput(approvedInput);
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, input.ApprovedKeys().Select(key => key.Id).ToArray());
        var page = WebUtility.HtmlDecode(await Http.GetStringAsync($"/Dms/Lockbox/{item.Id}"));
        StringAssert.Contains(page, "SSH key fingerprints");
        StringAssert.Contains(page, first.Fingerprint);
        StringAssert.Contains(page, second.Fingerprint);
        Assert.IsFalse(page.Contains(foreign.Fingerprint, StringComparison.Ordinal));

        var injected = definition.Request(new SshOpenInput { Keys = [new SshApprovedKey(first.Id, first.Fingerprint)] }, reason, [target.Id]);
        Assert.AreEqual(400, (await Assert.ThrowsAsync<DmsException>(() => workflows.SubmitAsync(requester.Id, injected))).StatusCode);
        var invalid = definition.Request(new SshOpenInput(), reason, [target.Id]);
        invalid.Input = """{"Keys":[null]}""";
        Assert.AreEqual(400, (await Assert.ThrowsAsync<DmsException>(() => workflows.SubmitAsync(requester.Id, invalid))).StatusCode);
        var different = definition.Request(new SshOpenInput(first.Id), reason, [target.Id], requestId: requestId);
        Assert.AreEqual(409, (await Assert.ThrowsAsync<DmsException>(() => workflows.SubmitAsync(requester.Id, different))).StatusCode);

        db.PersonalSshKeys.Add(NewKey(6, 3, requester.Id));
        await db.SaveChangesAsync();
        var repeat = definition.Request(new SshOpenInput(), reason, [target.Id], requestId: requestId);
        Assert.AreEqual(item.Parameters, (await workflows.SubmitAsync(requester.Id, repeat)).Parameters);

        item = await workflows.DecideAsync(approver.Id, item.Id, approve: true, "Approve this key selection");
        var task = new WorkflowRun
        {
            ParentId = item.WorkflowId, RootId = item.WorkflowId!.Value, LockboxId = item.Id, MachineId = target.Id,
            IsMachineTask = true, Definition = definition.Name, Version = definition.Version,
            Input = approvedInput, State = RunState.Running
        };
        db.WorkflowRuns.Add(task);
        await db.SaveChangesAsync();

        using var reply = await AuthorizeAsync(task.Id, agent);
        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        var authorization = await reply.Content.ReadFromJsonAsync<SshLeaseAuthorization>();
        Assert.AreEqual(requester.Id, authorization!.RequesterId);
        CollectionAssert.AreEqual(
            new[] { new SshAuthorizedKey(first.Id, first.PublicKey), new SshAuthorizedKey(second.Id, second.PublicKey) },
            authorization.Keys);

        await keys.RevokeAsync(requester.Id, second.Id);
        using var after = await AuthorizeAsync(task.Id, agent);
        Assert.AreEqual(HttpStatusCode.Conflict, after.StatusCode);

        static PersonalSshKey NewKey(byte suffix, int? slot, string user)
        {
            var blob = Convert.FromBase64String("AAAAC3NzaC1lZDI1NTE5AAAAIHpyp+C8Ia2B6RVM1UCr5HG+EtGR2xz5OF05P9kPz84D");
            blob[^1] = suffix;
            var (key, fingerprint) = SshPublicKey.Parse("ssh-ed25519 " + Convert.ToBase64String(blob));
            return new PersonalSshKey
            {
                UserId = user, Name = $"Key {suffix}", PublicKey = key, Fingerprint = fingerprint, Slot = slot,
                VerifiedAt = slot == 0 ? null : DateTime.UtcNow, RevokedAt = slot == null ? DateTime.UtcNow : null
            };
        }
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
