using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public sealed class DatabaseUpsertApprovalTests : TestBase
{
    [TestMethod]
    public async Task DatabaseUpsertWithSecretColumnsKeepsSecretsOutOfPersistenceAndConsumesOnce()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var user = new User { UserName = "db-upsert-requester", DisplayName = "DB Upsert Requester" };
        db.Users.Add(user);
        DmsTestPermissions.Risk(db, user.Id, RiskLevel.High, request: true, automatic: true);
        var human = PersonalKey(db, user);
        var agent = DmsKeyAuthenticationHandler.CreateKey(machine: true);
        var otherAgent = DmsKeyAuthenticationHandler.CreateKey(machine: true);
        var target = new Machine { Name = "DB Upsert Target", KeyHash = DmsKeyAuthenticationHandler.Hash(agent) };
        db.Machines.AddRange(
            target,
            new Machine { Name = "Foreign Agent", KeyHash = DmsKeyAuthenticationHandler.Hash(otherAgent) });
        await db.SaveChangesAsync();

        var containerId = new string('a', 64);
        var upsert = new DatabaseUpsertInput(
            null,
            "appdb",
            "Settings",
            ["CfgKey"],
            new Dictionary<string, JsonElement>
            {
                ["CfgKey"] = JsonSerializer.SerializeToElement("TranslationApi"),
                ["Enabled"] = JsonSerializer.SerializeToElement(true)
            },
            SecretColumns: ["ApiKey"],
            Container: containerId);

        const string secretValue = "sk-live-secret-upsert-value-42";

        // Submitting SecretColumns through the regular Lockbox endpoint must be rejected.
        var genericReject = await Assert.ThrowsAsync<DmsException>(() =>
            workflows.SubmitAsync(user.Id, OperationsWorkflows.DatabaseUpsert.Request(upsert, "Bypass attempt", [target.Id])));
        Assert.AreEqual(400, genericReject.StatusCode);

        const string endpoint = "/api/v1/dms/database-secrets/upsert";

        // Non-HTTPS request is rejected.
        using var insecure = await PostAsync(endpoint, human,
            new DatabaseUpsertSubmission(target.Id, "Insecure test", OperationsWorkflows.DatabaseUpsert.Serialize(upsert), new Dictionary<string, string> { ["ApiKey"] = secretValue }),
            secure: false);
        Assert.AreEqual(HttpStatusCode.Forbidden, insecure.StatusCode);

        // Missing secret column value is rejected.
        using var invalid = await PostAsync(endpoint, human,
            new DatabaseUpsertSubmission(target.Id, "Missing secret", OperationsWorkflows.DatabaseUpsert.Serialize(upsert), new Dictionary<string, string>()));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);

        // Valid HTTPS submission succeeds and never persists the secret.
        using var accepted = await PostAsync(endpoint, human,
            new DatabaseUpsertSubmission(target.Id, "Update translation API key", OperationsWorkflows.DatabaseUpsert.Serialize(upsert), new Dictionary<string, string> { ["ApiKey"] = secretValue }));
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);

        var box = await db.Lockboxes.SingleAsync(b => b.Definition == OperationsWorkflows.DatabaseUpsert.Name);
        Assert.AreEqual(DecisionState.Approved, box.Decision);
        Assert.IsFalse(box.Parameters.Contains(secretValue, StringComparison.Ordinal));
        var launch = await db.WorkflowLaunches.SingleAsync(x => x.RootId == box.WorkflowId);
        Assert.IsFalse(launch.Parameters.Contains(secretValue, StringComparison.Ordinal));

        var task = new WorkflowRun
        {
            ParentId = box.WorkflowId,
            RootId = box.WorkflowId!.Value,
            LockboxId = box.Id,
            MachineId = target.Id,
            IsMachineTask = true,
            Definition = box.Definition,
            Version = box.Version,
            State = RunState.Running,
            Input = JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!.Input
        };
        db.WorkflowRuns.Add(task);
        await db.SaveChangesAsync();

        var takeUrl = $"/api/agent/database-secrets/{task.Id:D}/take";

        // Foreign agent cannot steal the secret.
        using var foreign = await PostAsync(takeUrl, otherAgent);
        Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);

        // Target agent consumes it once over HTTPS.
        using var first = await PostAsync(takeUrl, agent);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        var payload = await first.Content.ReadFromJsonAsync<DatabaseUpsertSecretsPayload>();
        Assert.IsNotNull(payload);
        Assert.AreEqual(secretValue, payload.Secrets["ApiKey"]);

        // Replay returns 410 Gone.
        using var again = await PostAsync(takeUrl, agent);
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

    private async Task<HttpResponseMessage> PostAsync(string path, string key, object? value = null, bool secure = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (secure) request.Headers.Add("X-Forwarded-Proto", "https");
        if (value != null) request.Content = JsonContent.Create(value);
        return await Http.SendAsync(request);
    }
}
