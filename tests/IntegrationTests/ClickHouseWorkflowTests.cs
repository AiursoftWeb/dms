using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.SDK;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class ClickHouseWorkflowTests : TestBase
{
    [TestMethod]
    public async Task ControllerClassifiesOwnedSqlAndKeepsWritesPendingDespiteHighAutoApproval()
    {
        var (user, key, machine) = await ArrangeAsync();
        var directory = Directory.CreateTempSubdirectory("dms-clickhouse-test-");
        try
        {
            var file = Path.Combine(directory.FullName, "statement.sql");
            var artifacts = new DmsArtifactClient(Http.BaseAddress!.ToString(), key);
            using var scope = Server!.Services.CreateScope();
            var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();

            async Task<ArtifactReference> Upload(string sql)
            {
                await File.WriteAllTextAsync(file, sql);
                return await artifacts.UploadAsync(file, "statement.sql", "file");
            }

            OperationRequest Submission(ArtifactReference artifact, string? containerId = null)
            {
                var request = ClickHouseWorkflows.Query.Request(new ClickHouseSqlInput(artifact, containerId),
                    "Fixture approval", [machine]);
                request.Workflow = ClickHouseWorkflows.SqlSubmission;
                return request;
            }

            var read = await Upload("SELECT count() FROM logs.events");
            var readRequest = Submission(read);
            var readBox = await workflows.SubmitAsync(user, readRequest);
            Assert.AreEqual(ClickHouseWorkflows.Query.Name, readBox.Definition);
            Assert.AreEqual(RiskLevel.Low, readBox.Risk);
            Assert.AreEqual(DecisionState.Approved, readBox.Decision);
            Assert.IsTrue(readBox.Automatic);

            var write = await Upload("INSERT INTO logs.events VALUES (5)");
            var unpinned = await Assert.ThrowsAsync<DmsException>(() => workflows.SubmitAsync(user, Submission(write)));
            Assert.AreEqual(400, unpinned.StatusCode);
            var container = new string('a', 64);
            var wrongWorkflow = ClickHouseWorkflows.Query.Request(new ClickHouseSqlInput(write, container),
                "Supply mismatched SQL", [machine]);
            var mismatch = await Assert.ThrowsAsync<DmsException>(() => workflows.SubmitAsync(user, wrongWorkflow));
            Assert.AreEqual(400, mismatch.StatusCode);
            var operation = Submission(write, container);
            var box = await workflows.SubmitAsync(user, operation);
            Assert.AreEqual(ClickHouseWorkflows.Execute.Name, box.Definition);
            Assert.AreEqual(RiskLevel.High, box.Risk);
            Assert.AreEqual(DecisionState.Pending, box.Decision);
            Assert.IsNull(box.WorkflowId);
            var frozen = JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!;
            Assert.AreEqual(container, ClickHouseWorkflows.Execute.ReadInput(frozen.Input).ContainerId);

            operation.Workflow = ClickHouseWorkflows.SqlSubmission;
            Assert.AreEqual(box.Id, (await workflows.SubmitAsync(user, operation)).Id);
            var definitions = await workflows.ListDefinitionsAsync(user);
            Assert.IsFalse(definitions.Definitions.Single(d => d.Name == box.Definition).AutoApprove);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/dms/lockboxes/{box.Id}/clickhouse-result");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var result = await Http.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Conflict, result.StatusCode);
            StringAssert.Contains(JObject.Parse(await result.Content.ReadAsStringAsync())["Message"]!.ToString(), "no approved execution");
        }
        finally { directory.Delete(recursive: true); }
    }

    private async Task<(string User, string Key, Guid Machine)> ArrangeAsync()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var user = new User { UserName = "clickhouse-" + Guid.NewGuid().ToString("N"), DisplayName = "ClickHouse fixture" };
        var machine = new Machine { Name = "ClickHouse fixture" };
        var key = DmsKeyAuthenticationHandler.CreateKey(false);
        db.Users.Add(user);
        db.Machines.Add(machine);
        db.PersonalApiKeys.Add(new PersonalApiKey
        {
            UserId = user.Id, Name = "ClickHouse fixture", KeyHash = DmsKeyAuthenticationHandler.Hash(key),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        DmsTestPermissions.Risk(db, user.Id, RiskLevel.Low, request: true, automatic: true);
        DmsTestPermissions.Risk(db, user.Id, RiskLevel.High, request: true, automatic: true);
        await db.SaveChangesAsync();
        return (user.Id, key, machine.Id);
    }
}
