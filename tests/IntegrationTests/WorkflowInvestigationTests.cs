using Aiursoft.DMS.Authorization;
using System.Net;
using System.Net.Http.Headers;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.EntityFrameworkCore;

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

    private async Task<(Guid Cluster, Guid Hidden, string Key, string AgentKey)> ArrangeAsync()
    {
        return await DatabaseAsync(async db =>
        {
            var user = new User { UserName = "requester" + Guid.NewGuid().ToString("N"), Email = "requester@example.test", DisplayName = "Requester" }; db.Users.Add(user);
            var cluster = new Cluster { Name = "Investigation <script>unsafe()</script>" }; var hidden = new Cluster { Name = "Hidden investigation cluster" };
            var key = DmsKeyAuthenticationHandler.CreateKey(false); var agentKey = DmsKeyAuthenticationHandler.CreateKey(true);
            db.Clusters.AddRange(cluster, hidden);
            db.Machines.Add(new Machine { ClusterId = cluster.Id, Name = "Agent", KeyHash = DmsKeyAuthenticationHandler.Hash(agentKey) });
            db.PersonalApiKeys.Add(new PersonalApiKey { UserId = user.Id, Name = "Investigation", KeyHash = DmsKeyAuthenticationHandler.Hash(key), ExpiresAt = DateTime.UtcNow.AddHours(1) });
            DmsTestPermissions.Risk(db, user.Id, RiskLevel.Safe, request: true, automatic: false, approve: false);
            await db.SaveChangesAsync(); return (cluster.Id, hidden.Id, key, agentKey);
        });
    }

    [TestMethod]
    public async Task ArchiveRequiresHumanKeyAndReadPermissionBeforeAvailabilityOrFilters()
    {
        var setup = await ArrangeAsync(); var route = $"/api/v1/dms/audit-events?ClusterId={setup.Cluster}";
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Http.GetAsync(route)).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.AgentKey);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync(route)).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        foreach (var id in new[] { setup.Hidden, Guid.Empty, Guid.NewGuid() })
        {
            var response = await Http.GetAsync($"/api/v1/dms/audit-events?ClusterId={id}&State=bad-value");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode); Assert.DoesNotContain("ClickHouse", await response.Content.ReadAsStringAsync());
        }
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.GetAsync("/api/v1/dms/audit-events")).StatusCode);
        await DatabaseAsync(async db => { DmsTestPermissions.Set(db, (await db.PersonalApiKeys.SingleAsync(k => k.KeyHash == DmsKeyAuthenticationHandler.Hash(setup.Key))).UserId, AppPermissionNames.CanReadDmsOperations, false); await db.SaveChangesAsync(); return true; });
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync(route)).StatusCode);
    }

    [TestMethod]
    [DataRow("Page=-1")]
    [DataRow("Page=10001")]
    [DataRow("State=999")]
    [DataRow("Kind=999")]
    [DataRow("State=not-a-state")]
    [DataRow("MachineId=not-a-guid")]
    [DataRow("From=2026-11-14T00:00:00Z&Until=2026-11-14T00:00:00Z")]
    [DataRow("From=2026-01-01T00:00:00Z&Until=2026-03-01T00:00:00Z")]
    [DataRow("From=0001-01-01T00:00:00Z&Until=0001-01-02T00:00:00Z")]
    public async Task InvalidFiltersAreRejectedWithoutAttemptingAnArchiveQuery(string filter)
    {
        var setup = await ArrangeAsync(); Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.GetAsync($"/api/v1/dms/audit-events?ClusterId={setup.Cluster}&{filter}")).StatusCode);
        Assert.AreEqual(0, await DatabaseAsync(db => db.AuditEvents.CountAsync(e => e.Action == "operation.audit-query" && e.ResourceId == setup.Cluster)));
    }

    [TestMethod]
    public async Task UnconfiguredArchiveIsAuditedAndExplicitlyUnavailableWithoutCreatingWork()
    {
        var setup = await ArrangeAsync(); Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        var response = await Http.GetAsync($"/api/v1/dms/audit-events?ClusterId={setup.Cluster}&From=2026-11-14T08:00:00%2B08:00&Until=2026-11-15T08:00:00%2B08:00");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "durable backlog");
        var audit = await DatabaseAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "operation.audit-query" && e.ResourceId == setup.Cluster));
        StringAssert.Contains(audit.Detail, "From=2026-11-14T00:00:00.0000000+00:00");
        Assert.AreEqual(0, await DatabaseAsync(db => db.Lockboxes.CountAsync(l => l.ClusterId == setup.Cluster)));
        Assert.AreEqual(0, await DatabaseAsync(db => db.WorkflowRuns.CountAsync(r => r.ClusterId == setup.Cluster)));
    }

    [TestMethod]
    public async Task WebInvestigationUsesVisibleClustersAndShowsAnOutageRatherThanAnEmptyResult()
    {
        var setup = await ArrangeAsync(); Assert.AreEqual(HttpStatusCode.Found, (await Http.GetAsync("/Dms/Investigation")).StatusCode);
        await LoginAsAdmin(); var response = await Http.GetAsync($"/Dms/Investigation?ClusterId={setup.Cluster}");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode); var html = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "unavailable or not configured"); StringAssert.Contains(html, "&lt;script&gt;");
        Assert.DoesNotContain("<script>unsafe()", html); StringAssert.Contains(html, "Hidden investigation cluster");
        Assert.DoesNotContain("No archived events match", html);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await Http.GetAsync($"/Dms/Investigation?ClusterId={setup.Hidden}")).StatusCode);
        await RegisterAndLoginAsync();
        Assert.AreEqual(HttpStatusCode.Found, (await Http.GetAsync("/Dms/Investigation")).StatusCode);
    }
}
