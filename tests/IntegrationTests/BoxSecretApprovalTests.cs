using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public sealed class BoxSecretApprovalTests : TestBase
{
    [TestMethod]
    public async Task TransferUsesIndependentApprovalAndDisclosesOnceToRequester()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var requester = new User { UserName = "box-secret-requester", DisplayName = "Box requester" };
        var approver = new User { UserName = "box-secret-approver", DisplayName = "Box approver" };
        db.Users.AddRange(requester, approver);
        DmsTestPermissions.Risk(db, requester.Id, RiskLevel.VeryHigh, request: true, automatic: true, approve: true);
        DmsTestPermissions.Set(db, requester.Id, AppPermissionNames.CanApproveOwnWorkflow);
        DmsTestPermissions.Risk(db, approver.Id, RiskLevel.VeryHigh, request: true, approve: true);
        var actorKey = PersonalKey(db, requester);
        var approverKey = PersonalKey(db, approver);
        var agent = DmsKeyAuthenticationHandler.CreateKey(machine: true);
        var otherAgent = DmsKeyAuthenticationHandler.CreateKey(machine: true);
        var target = new Machine { Name = "Box secret target", KeyHash = DmsKeyAuthenticationHandler.Hash(agent) };
        db.Machines.AddRange(target, new Machine { Name = "Other agent", KeyHash = DmsKeyAuthenticationHandler.Hash(otherAgent) });
        await db.SaveChangesAsync();

        var definition = MachineWorkflows.BoxGetSecretRaw;
        var key = new BoxSecretInput("CLOUDFLARE_API_TOKEN");
        const string fixture = "synthetic-value-$;=&";
        var box = await workflows.SubmitAsync(requester.Id, definition.Request(key, "Test one-time reveal", [target.Id]));
        Assert.AreEqual(DecisionState.Pending, box.Decision);
        var selfApproval = await Assert.ThrowsAsync<DmsException>(() => workflows.DecideAsync(requester.Id, box.Id, true, "No"));
        Assert.AreEqual(403, selfApproval.StatusCode);
        box = await workflows.DecideAsync(approver.Id, box.Id, true, "Approved fixture");
        var task = FixtureTask(box, target.Id);
        db.WorkflowRuns.Add(task);
        await db.SaveChangesAsync();

        var publish = $"/api/agent/box-secrets/{task.Id:D}/publish-raw";
        var reveal = $"/api/v1/dms/box-secrets/{box.Id:D}/reveal";
        using var foreignAgent = await PostAsync(publish, otherAgent, new BoxSecretValue(fixture));
        Assert.AreEqual(HttpStatusCode.Forbidden, foreignAgent.StatusCode);
        using var insecure = await PostAsync(publish, agent, new BoxSecretValue(fixture), secure: false);
        Assert.AreEqual(HttpStatusCode.Forbidden, insecure.StatusCode);
        using var published = await PostAsync(publish, agent, new BoxSecretValue(fixture));
        Assert.AreEqual(HttpStatusCode.NoContent, published.StatusCode);
        using var early = await PostAsync(reveal, actorKey);
        Assert.AreEqual(HttpStatusCode.Conflict, early.StatusCode);
        using var outsider = await PostAsync(reveal, approverKey);
        Assert.AreEqual(HttpStatusCode.NotFound, outsider.StatusCode);

        task.State = RunState.Succeeded;
        var root = await db.WorkflowRuns.SingleAsync(r => r.Id == task.RootId);
        root.State = RunState.Succeeded;
        await db.SaveChangesAsync();

        using var first = await PostAsync(reveal, actorKey);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.IsTrue(first.Headers.CacheControl?.NoStore == true);
        using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.AreEqual(fixture, json.RootElement.GetProperty("Data").GetProperty("Value").GetString());
        using var again = await PostAsync(reveal, actorKey);
        Assert.AreEqual(HttpStatusCode.Gone, again.StatusCode);
        Assert.IsFalse(box.Parameters.Contains(fixture, StringComparison.Ordinal));
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(x => x.ResourceId == box.Id && x.Action == "box.secret.reveal");
        Assert.AreEqual(key.Name, audit.Detail);

    }

    [TestMethod]
    public async Task SetKeepsTheValueOutOfPersistentRequestsAndTheAgentConsumesOnce()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var user = new User { UserName = "box-write-requester", DisplayName = "Box writer" };
        db.Users.Add(user);
        DmsTestPermissions.Risk(db, user.Id, RiskLevel.VeryHigh, request: true, automatic: true);
        var human = PersonalKey(db, user);
        var agent = DmsKeyAuthenticationHandler.CreateKey(machine: true);
        var target = new Machine { Name = "Box write target", KeyHash = DmsKeyAuthenticationHandler.Hash(agent) };
        db.Machines.Add(target);
        await db.SaveChangesAsync();

        var definition = MachineWorkflows.BoxSetSecret;
        var key = new BoxSecretInput("POSTGRES_PASSWORD");
        const string fixture = "literal-fixture-value-=$";
        await Assert.ThrowsAsync<DmsException>(() => workflows.SubmitAsync(user.Id, definition.Request(key, "Generic submit", [target.Id])));
        const string set = "/api/v1/dms/box-secrets/set";
        using var invalid = await PostAsync(set, human, new BoxSecretSubmission(target.Id, "Test invalid name", new BoxSecretInput("bad name"), fixture));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var accepted = await PostAsync(set, human, new BoxSecretSubmission(target.Id, "Test fixture", key, fixture));
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        var box = await db.Lockboxes.SingleAsync(b => b.Definition == definition.Name);
        Assert.AreEqual(DecisionState.Approved, box.Decision);
        Assert.IsFalse(box.Parameters.Contains(fixture, StringComparison.Ordinal));
        var launch = await db.WorkflowLaunches.SingleAsync(x => x.RootId == box.WorkflowId);
        Assert.IsFalse(launch.Parameters.Contains(fixture, StringComparison.Ordinal));
        var task = FixtureTask(box, target.Id);
        db.WorkflowRuns.Add(task);
        await db.SaveChangesAsync();
        var endpoint = $"/api/agent/box-secrets/{task.Id:D}/take-set";
        using var first = await PostAsync(endpoint, agent);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(new BoxSecretValue(fixture), await first.Content.ReadFromJsonAsync<BoxSecretValue>());
        using var again = await PostAsync(endpoint, agent);
        Assert.AreEqual(HttpStatusCode.Gone, again.StatusCode);
    }

    private static string PersonalKey(TemplateDbContext db, User user)
    {
        var key = DmsKeyAuthenticationHandler.CreateKey(machine: false);
        db.PersonalApiKeys.Add(new PersonalApiKey
        {
            UserId = user.Id, Name = "Fixture", KeyHash = DmsKeyAuthenticationHandler.Hash(key),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        return key;
    }

    private static WorkflowRun FixtureTask(LockboxRequest box, Guid machine) => new()
    {
        ParentId = box.WorkflowId,
        RootId = box.WorkflowId!.Value,
        LockboxId = box.Id,
        MachineId = machine,
        IsMachineTask = true,
        Definition = box.Definition,
        Version = box.Version,
        State = RunState.Running,
        Input = JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!.Input
    };

    private async Task<HttpResponseMessage> PostAsync(string path, string key, object? value = null, bool secure = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (secure) request.Headers.Add("X-Forwarded-Proto", "https");
        if (value != null) request.Content = JsonContent.Create(value);
        return await Http.SendAsync(request);
    }
}
