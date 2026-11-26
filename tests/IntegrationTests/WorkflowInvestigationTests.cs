using Aiursoft.DMS.Authorization;
using System.Net;
using System.Net.Http.Headers;
using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.SDK;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class WorkflowInvestigationTests : TestBase
{
    private Task<T> DatabaseAsync<T>(Func<TemplateDbContext, Task<T>> action) =>
        Server!.Services.GetRequiredService<ControllerGate>().RunAsync(async () =>
        {
            using var scope = Server.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<TemplateDbContext>());
        });

    private async Task<(string User, string Key, string AgentKey)> ArrangeAsync()
    {
        return await DatabaseAsync(async db =>
        {
            DmsTestPermissions.DeactivatePreviousMachines(db);
            var user = new User { UserName = "requester" + Guid.NewGuid().ToString("N"), Email = "requester@example.test", DisplayName = "Requester" }; db.Users.Add(user);
            var key = DmsKeyAuthenticationHandler.CreateKey(false); var agentKey = DmsKeyAuthenticationHandler.CreateKey(true);

            db.Machines.Add(new Machine { Name = "Agent", KeyHash = DmsKeyAuthenticationHandler.Hash(agentKey) });
            db.PersonalApiKeys.Add(new PersonalApiKey { UserId = user.Id, Name = "Investigation", KeyHash = DmsKeyAuthenticationHandler.Hash(key), ExpiresAt = DateTime.UtcNow.AddHours(1) });
            DmsTestPermissions.Risk(db, user.Id, RiskLevel.Safe, request: true, automatic: false, approve: false);
            await db.SaveChangesAsync(); return (user.Id, key, agentKey);
        });
    }

    [TestMethod]
    public async Task ArchiveRequiresHumanKeyAndReadPermissionBeforeAvailabilityOrFilters()
    {
        var setup = await ArrangeAsync(); var route = $"/api/v1/dms/audit-events";
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Http.GetAsync(route)).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.AgentKey);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync(route)).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        foreach (var state in new[] { "bad-value", "999" })
        {
            var response = await Http.GetAsync($"/api/v1/dms/audit-events?State={state}");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode); Assert.DoesNotContain("ClickHouse", await response.Content.ReadAsStringAsync());
        }
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await Http.GetAsync("/api/v1/dms/audit-events")).StatusCode);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await Http.GetAsync("/api/v1/dms/audit-events?LockboxId=lb%2F" + Guid.NewGuid())).StatusCode);
        await DatabaseAsync(async db => { DmsTestPermissions.Set(db, (await db.PersonalApiKeys.SingleAsync(k => k.KeyHash == DmsKeyAuthenticationHandler.Hash(setup.Key))).UserId, AppPermissionNames.CanReadDmsOperations, false); await db.SaveChangesAsync(); return true; });
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync(route)).StatusCode);
    }

    [TestMethod]
    [DataRow("Page=-1")]
    [DataRow("Page=10001")]
    [DataRow("State=999")]
    [DataRow("Origin=999")]
    [DataRow("Origin=not-an-origin")]
    [DataRow("Kind=999")]
    [DataRow("State=not-a-state")]
    [DataRow("MachineId=not-a-guid")]
    [DataRow("LockboxId=lb/not-a-guid")]
    [DataRow("From=2026-11-14T00:00:00Z&Until=2026-11-14T00:00:00Z")]
    [DataRow("From=2026-01-01T00:00:00Z&Until=2026-03-01T00:00:00Z")]
    [DataRow("From=0001-01-01T00:00:00Z&Until=0001-01-02T00:00:00Z")]
    public async Task InvalidFiltersAreRejectedWithoutAttemptingAnArchiveQuery(string filter)
    {
        var setup = await ArrangeAsync(); Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.GetAsync($"/api/v1/dms/audit-events?{filter}")).StatusCode);
        Assert.AreEqual(0, await DatabaseAsync(db => db.AuditEvents.CountAsync(e => e.Action == "operation.audit-query" && e.ActorId == setup.User)));
    }

    [TestMethod]
    public async Task UnconfiguredArchiveIsAuditedAndExplicitlyUnavailableWithoutCreatingWork()
    {
        var setup = await ArrangeAsync(); Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        var response = await Http.GetAsync($"/api/v1/dms/audit-events?From=2026-11-14T08:00:00%2B08:00&Until=2026-11-15T08:00:00%2B08:00");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "durable backlog");
        var audit = await DatabaseAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "operation.audit-query" && e.ActorId == setup.User));
        StringAssert.Contains(audit.Detail, "From=2026-11-14T00:00:00.0000000+00:00");
        Assert.AreEqual(0, await DatabaseAsync(db => db.Lockboxes.CountAsync(l => l.RequesterId == setup.User)));
        Assert.AreEqual(0, await DatabaseAsync(db => db.WorkflowRuns.CountAsync(r => db.Lockboxes.Any(l => l.Id == r.LockboxId && l.RequesterId == setup.User))));
    }

    [TestMethod]
    public async Task WebInvestigationShowsAnOutageRatherThanAnEmptyResult()
    {
        await ArrangeAsync(); Assert.AreEqual(HttpStatusCode.Found, (await Http.GetAsync("/Dms/Investigation")).StatusCode);
        await LoginAsAdmin(); var response = await Http.GetAsync($"/Dms/Investigation");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode); var html = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "unavailable or not configured");
        Assert.DoesNotContain("name=\"ClusterId\"", html);
        Assert.DoesNotContain("No archived events match", html);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await Http.GetAsync($"/Dms/Investigation")).StatusCode);
        await RegisterAndLoginAsync();
        Assert.AreEqual(HttpStatusCode.Found, (await Http.GetAsync("/Dms/Investigation")).StatusCode);
    }

    [TestMethod]
    [DataRow("Page=-1")]
    [DataRow("Page=10001")]
    [DataRow("Risk=999")]
    [DataRow("Risk=not-a-risk")]
    [DataRow("Decision=999")]
    [DataRow("State=not-a-state")]
    [DataRow("Trigger=999")]
    [DataRow("Origin=not-an-origin")]
    [DataRow("LockboxId=lb/not-a-guid")]
    [DataRow("WorkflowId=not-a-guid")]
    [DataRow("From=2026-11-14T00:00:00Z&Until=2026-11-14T00:00:00Z")]
    [DataRow("From=2026-01-01T00:00:00Z&Until=2026-03-01T00:00:00Z")]
    [DataRow("From=0001-01-01T00:00:00Z&Until=0001-01-02T00:00:00Z")]
    public async Task InvalidHistoryFiltersAreRejectedWithoutAttemptingAnArchiveQuery(string filter)
    {
        var setup = await ArrangeAsync(); Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        foreach (var route in new[] { "/api/v1/dms/lockbox-history", "/api/v1/dms/workflow-history" })
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.GetAsync($"{route}?{filter}")).StatusCode);
            Assert.AreEqual(0, await DatabaseAsync(db => db.AuditEvents.CountAsync(e =>
                (e.Action == "lockbox.history-query" || e.Action == "workflow.history-query") && e.ActorId == setup.User)));
        }
    }

    [TestMethod]
    public async Task HistoryIsOpenToEveryHumanKeyWithoutReadPermission()
    {
        var setup = await ArrangeAsync();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/v1/dms/lockbox-history")).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.AgentKey);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/v1/dms/workflow-history")).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        await DatabaseAsync(async db =>
        {
            DmsTestPermissions.Set(db, (await db.PersonalApiKeys.SingleAsync(k => k.KeyHash == DmsKeyAuthenticationHandler.Hash(setup.Key))).UserId,
                AppPermissionNames.CanReadDmsOperations, false);
            await db.SaveChangesAsync(); return true;
        });
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/v1/dms/audit-events")).StatusCode);
        foreach (var route in new[] { "/api/v1/dms/lockbox-history", "/api/v1/dms/workflow-history" })
        {
            var response = await Http.GetAsync(route);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "durable backlog");
        }
        Assert.AreEqual(1, await DatabaseAsync(db => db.AuditEvents.CountAsync(e => e.Action == "lockbox.history-query" && e.ActorId == setup.User)));
        Assert.AreEqual(1, await DatabaseAsync(db => db.AuditEvents.CountAsync(e => e.Action == "workflow.history-query" && e.ActorId == setup.User)));
        var record = await DatabaseAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "workflow.history-query" && e.ActorId == setup.User));
        var parts = record.Detail.Split("; ").Select(part => part.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
        Assert.AreEqual(7, (DateTime.Parse(parts["Until"]) - DateTime.Parse(parts["From"])).TotalDays);
    }

    [TestMethod]
    public async Task HistoryResolvesReadableNamesBeforeTouchingTheArchive()
    {
        var setup = await ArrangeAsync(); Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        var username = await DatabaseAsync(async db => await db.Users.Where(u => u.Id == setup.User).Select(u => u.UserName).SingleAsync()) ?? string.Empty;
        var soloMachine = "History Solo " + Guid.NewGuid().ToString("N");
        var sharedMachine = "History Shared " + Guid.NewGuid().ToString("N");
        await DatabaseAsync(async db =>
        {
            db.Machines.Add(new Machine { Name = soloMachine, KeyHash = DmsKeyAuthenticationHandler.Hash(setup.AgentKey) });
            await db.SaveChangesAsync(); return true;
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.GetAsync("/api/v1/dms/lockbox-history?Requester=nobody-here")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.GetAsync("/api/v1/dms/workflow-history?Machine=missing-machine")).StatusCode);
        foreach (var route in new[] { "/api/v1/dms/lockbox-history", "/api/v1/dms/workflow-history" })
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await Http.GetAsync($"{route}?Requester={Uri.EscapeDataString(username)}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await Http.GetAsync($"{route}?Machine={Uri.EscapeDataString(soloMachine)}")).StatusCode);
        }
        await DatabaseAsync(async db =>
        {
            db.Users.Add(new User { UserName = "history-twin" + Guid.NewGuid().ToString("N"), Email = "twin@example.test", DisplayName = username });
            db.Machines.Add(new Machine { Name = sharedMachine, KeyHash = DmsKeyAuthenticationHandler.Hash(setup.AgentKey) });
            db.Machines.Add(new Machine { Name = sharedMachine, KeyHash = DmsKeyAuthenticationHandler.Hash(setup.AgentKey) });
            await db.SaveChangesAsync(); return true;
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.GetAsync("/api/v1/dms/lockbox-history?Approver=" + Uri.EscapeDataString(username))).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.GetAsync("/api/v1/dms/workflow-history?Machine=" + Uri.EscapeDataString(sharedMachine))).StatusCode);
    }

    [TestMethod]
    public async Task SdkHistoryClientSerializesFiltersAndSurfacesArchiveAvailability()
    {
        var setup = await ArrangeAsync();
        var username = await DatabaseAsync(async db => await db.Users.Where(u => u.Id == setup.User).Select(u => u.UserName).SingleAsync()) ?? string.Empty;
        using var provider = new ServiceCollection().AddLogging().AddAiurProtocolClient().BuildServiceProvider();
        var client = new DmsApiClient(provider.GetRequiredService<AiurProtocolClient>(), Http.BaseAddress!.ToString(), setup.Key);
        var unavailable = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(
            () => client.LockboxHistoryAsync(new HistoryQuery { Requester = username, LockboxId = new LockboxId(Guid.NewGuid()) }));
        StringAssert.Contains(unavailable.Response.Message, "durable backlog");
        var unknown = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(
            () => client.WorkflowHistoryAsync(new HistoryQuery { Machine = "missing-machine" }));
        StringAssert.Contains(unknown.Response.Message, "No machine named");
        var invalid = await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(
            () => client.WorkflowHistoryAsync(new HistoryQuery { Page = -1 }));
        StringAssert.Contains(invalid.Response.Message, "valid time range");
    }
}
