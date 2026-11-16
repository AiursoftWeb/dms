using Aiursoft.DMS.Authorization;
using System.Net;
using System.Net.Http.Headers;
using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.SDK;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.Workflows;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class WorkflowDiscoveryTests : TestBase
{
    private Task<T> DatabaseAsync<T>(Func<TemplateDbContext, Task<T>> action) =>
        Server!.Services.GetRequiredService<ControllerGate>().RunAsync(async () =>
        {
            using var scope = Server.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<TemplateDbContext>());
        });

    private async Task<(string User, string Key, string AgentKey)> ArrangeAsync()
    {
        AuthoringExamples.RegisterMachines(Server!.Services.GetRequiredService<MachineWorkflowRegistry>());
        AuthoringExamples.RegisterCluster(Server.Services.GetRequiredService<ClusterWorkflowRegistry>());
        return await DatabaseAsync(async db =>
        {
            DmsTestPermissions.DeactivatePreviousMachines(db);
            var user = new User { UserName = "catalog" + Guid.NewGuid().ToString("N"), Email = "catalog@example.test", DisplayName = "Catalog" }; db.Users.Add(user);
            var key = DmsKeyAuthenticationHandler.CreateKey(false);
            var agentKey = DmsKeyAuthenticationHandler.CreateKey(true);
            db.Machines.Add(new Machine { Name = "Agent", KeyHash = DmsKeyAuthenticationHandler.Hash(agentKey) });
            db.PersonalApiKeys.Add(new PersonalApiKey { UserId = user.Id, Name = "Catalog", KeyHash = DmsKeyAuthenticationHandler.Hash(key), ExpiresAt = DateTime.UtcNow.AddHours(1) });
            DmsTestPermissions.Risk(db, user.Id, RiskLevel.Safe, request: true, automatic: true);
            DmsTestPermissions.Risk(db, user.Id, RiskLevel.Low, request: true);
            DmsTestPermissions.Risk(db, user.Id, RiskLevel.Medium, approve: true);
            await db.SaveChangesAsync();
            return (user.Id, key, agentKey);
        });
    }

    private async Task<WorkflowCatalogResult> ReadAsync(string key)
    {
        using var provider = new ServiceCollection().AddLogging().AddAiurProtocolClient().BuildServiceProvider();
        var client = new DmsApiClient(provider.GetRequiredService<AiurProtocolClient>(), Http.BaseAddress!.ToString(), key);
        var response = await client.ListDefinitionsAsync();
        return JObject.FromObject(response.Data!).ToObject<WorkflowCatalogResult>()!;
    }

    [TestMethod]
    public async Task CatalogDiscoversRegisteredPublicDefinitionsAndGlobalIndependentPermissions()
    {
        var setup = await ArrangeAsync();
        var first = await ReadAsync(setup.Key);
        Assert.IsFalse(first.CanApproveOwnRequests);
        Assert.AreEqual(first.Definitions.Count, first.Definitions.Select(d => d.Name).Distinct().Count());
        CollectionAssert.AreEqual(first.Definitions.Select(d => d.Name).Order(StringComparer.Ordinal).ToList(), first.Definitions.Select(d => d.Name).ToList());
        Assert.IsFalse(first.Definitions.Any(d => d.Name is "internal.process" or "example.unit" or "machine.filesystem.disk-usage" or WorkflowNames.AddMachine or WorkflowNames.Kill));
        var machine = first.Definitions.Single(d => d.Name == AuthoringExamples.Repeat.Name);
        Assert.AreEqual("repeat-1", machine.Version); Assert.AreEqual(WorkflowLocation.Machine, machine.Location);
        Assert.IsTrue(machine.RequiresTargets && machine.CanRequest); Assert.IsFalse(machine.AutoApprove || machine.CanApprove);
        var cluster = first.Definitions.Single(d => d.Name == AuthoringExamples.Total.Name);
        Assert.AreEqual("total-1", cluster.Version); Assert.AreEqual(WorkflowLocation.Cluster, cluster.Location);
        Assert.AreEqual(RiskLevel.Medium, cluster.Risk); Assert.IsFalse(cluster.CanRequest || cluster.AutoApprove); Assert.IsTrue(cluster.CanApprove);
        Assert.IsFalse(first.Definitions.Single(d => d.Name == ClusterWorkflows.GetMachines.Name).RequiresTargets);
        var second = await ReadAsync(setup.Key);
        CollectionAssert.AreEqual(first.Definitions.ToList(), second.Definitions.ToList());
        await DatabaseAsync(async db => { DmsTestPermissions.Set(db, setup.User, AppPermissionNames.CanApproveOwnWorkflow); await db.SaveChangesAsync(); return true; });
        Assert.IsTrue((await ReadAsync(setup.Key)).CanApproveOwnRequests);
        Assert.AreEqual(0, await DatabaseAsync(db => db.Lockboxes.CountAsync(l => l.RequesterId == setup.User)));
        Assert.AreEqual(0, await DatabaseAsync(db => db.WorkflowRuns.CountAsync(r => db.Lockboxes.Any(l => l.Id == r.LockboxId && l.RequesterId == setup.User))));
        Assert.AreEqual(3, await DatabaseAsync(db => db.AuditEvents.CountAsync(e => e.ActorId == setup.User && e.Action == "workflow.definitions")));
    }

    [TestMethod]
    public async Task CatalogRequiresHumanAuthenticationAndGlobalReadPermission()
    {
        var setup = await ArrangeAsync();
        const string route = "/api/v1/dms/workflow-definitions";
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Http.GetAsync(route)).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.AgentKey);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync(route)).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        Assert.AreEqual(HttpStatusCode.OK, (await Http.GetAsync(route)).StatusCode);
        await DatabaseAsync(async db => { DmsTestPermissions.Set(db, setup.User, AppPermissionNames.CanReadDmsOperations, false); await db.SaveChangesAsync(); return true; });
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync(route)).StatusCode);
    }

    [TestMethod]
    public async Task CliCatalogUsesAllFormatsWithoutCreatingWork()
    {
        var setup = await ArrangeAsync();
        var expectedCount = (await ReadAsync(setup.Key)).Definitions.Count;
        foreach (var format in new[] { "table", "json", "csv" })
        {
            var cli = await Client.DmsCommandApp.Create().TestRunAsync(["operation", "list-definitions", "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key, "--output-format", format]);
            Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
            StringAssert.Contains(cli.StdOut, "example.repeat"); StringAssert.Contains(cli.StdOut, "repeat-1");
            Assert.DoesNotContain("example.unit", cli.StdOut); Assert.DoesNotContain("\u001b", cli.StdOut);
            if (format == "table") { StringAssert.Contains(cli.StdOut, "Machine"); StringAssert.Contains(cli.StdOut, "Medium"); }
            if (format == "json") Assert.HasCount(expectedCount, JObject.Parse(cli.StdOut)["Data"]!["Definitions"]!);
            if (format == "csv")
            {
                using var csv = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(cli.StdOut));
                csv.SetDelimiters(","); csv.TrimWhiteSpace = false;
                var headers = csv.ReadFields()!; var rows = new List<string[]>();
                while (!csv.EndOfData) rows.Add(csv.ReadFields()!);
                Assert.HasCount(expectedCount, rows);
                Assert.DoesNotContain("ClusterId", headers);
                Assert.IsTrue(rows.All(r => r[Array.IndexOf(headers, "CanApproveOwnRequests")] == "false"));
                Assert.IsTrue(rows.Any(r => r[Array.IndexOf(headers, "Version")] == "repeat-1"));
            }
        }

        Assert.AreEqual(0, await DatabaseAsync(db => db.Lockboxes.CountAsync(l => l.RequesterId == setup.User)));
        Assert.AreEqual(0, await DatabaseAsync(db => db.WorkflowRuns.CountAsync(r => db.Lockboxes.Any(l => l.Id == r.LockboxId && l.RequesterId == setup.User))));
    }

    [TestMethod]
    public async Task WebCatalogShowsWorkflowLocationsWithoutResourceSelectors()
    {
        await ArrangeAsync();
        Assert.AreEqual(HttpStatusCode.Found, (await Http.GetAsync("/Dms/Definitions")).StatusCode);
        await LoginAsAdmin();
        var page = await Http.GetAsync("/Dms/Definitions");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "example.total"); StringAssert.Contains(html, "repeat-1");
        StringAssert.Contains(html, "May approve own requests"); StringAssert.Contains(html, "Cluster");
        StringAssert.Contains(html, "definition=example.total");
        Assert.DoesNotContain("name=\"clusterId\"", html);
        Assert.DoesNotContain("example.unit", html);
        await RegisterAndLoginAsync();
        Assert.AreEqual(HttpStatusCode.Found, (await Http.GetAsync("/Dms/Definitions")).StatusCode);
    }

    [TestMethod]
    public void CrossLocationNameCollisionIsRejectedEvenForInternalDefinitions()
    {
        var machines = new MachineWorkflowRegistry(); var clusters = new ClusterWorkflowRegistry();
        clusters.Register(new ClusterWorkflowDefinition<EmptyInput, List<MachineSummary>>(MachineWorkflows.Performance.Name, RiskLevel.Safe), () => new GetMachinesWorkflow());
        Assert.Throws<InvalidOperationException>(() => new WorkflowCatalog(machines, clusters).PublicDefinitions());
    }
}
