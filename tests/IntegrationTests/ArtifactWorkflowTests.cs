using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.SDK;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class ArtifactWorkflowTests : TestBase
{
    [TestMethod]
    public async Task ScriptSubmissionRetriesReuseFrozenBytesAndTheWebReviewerCanInspectThem()
    {
        var setup = await SetupAsync(); var directory = Directory.CreateTempSubdirectory("dms-script-review-").FullName;
        try
        {
            var source = Path.Combine(directory, "approved.sh"); var original = "printf '%s\\n' '<script>alert(1)</script>'\n";
            await File.WriteAllTextAsync(source, original); var requestId = Guid.NewGuid();
            string[] args = ["machine", "system", "execute-script", "--language", "bash", "--script-file", source,
                "--machine-id", setup.Machine.ToString(), "--request-id", requestId.ToString(), "--reason", "Inspect the exact frozen source",
                "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key];
            Assert.AreEqual(0, (await Client.DmsCommandApp.Create().TestRunAsync(args)).ProgramReturn);
            Assert.AreEqual(0, (await Client.DmsCommandApp.Create().TestRunAsync(args)).ProgramReturn);
            var box = await DatabaseAsync(db => db.Lockboxes.SingleAsync(l => l.ClientRequestId == requestId));
            var request = JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!;
            var input = OperationsWorkflows.SystemExecuteScript.ReadInput(request.Input);
            using (var scope = Server!.Services.CreateScope())
            {
                var record = scope.ServiceProvider.GetRequiredService<DmsArtifactStore>().Get(input.Artifact.Id);
                Assert.IsTrue(record.AuditInput); Assert.IsTrue(record.ExpiresAt > DateTime.UtcNow.AddYears(1));
            }
            await File.WriteAllTextAsync(source, "printf 'changed after submission'\n");
            Assert.AreNotEqual(0, (await Client.DmsCommandApp.Create().TestRunAsync(args)).ProgramReturn);
            Assert.AreEqual(1, await DatabaseAsync(db => db.Lockboxes.CountAsync(l => l.ClientRequestId == requestId)));
            Assert.AreEqual(0, await DatabaseAsync(db => db.WorkflowRuns.CountAsync(r => r.IsMachineTask && r.LockboxId == box.Id)));
            await LoginAsAdmin();
            var lockbox = await Http.GetAsync("/Dms/Lockbox?id=" + box.Id); Assert.AreEqual(HttpStatusCode.OK, lockbox.StatusCode);
            StringAssert.Contains(await lockbox.Content.ReadAsStringAsync(), input.Artifact.Id.ToString());
            var preview = await Http.GetAsync("/Dms/Artifact?id=" + input.Artifact.Id); Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode);
            var html = await preview.Content.ReadAsStringAsync(); StringAssert.Contains(html, "&lt;script&gt;");
            Assert.DoesNotContain("<script>alert(1)</script>", html); Assert.DoesNotContain("changed after submission", html);
            var download = await Http.GetAsync("/Dms/DownloadArtifact?id=" + input.Artifact.Id);
            Assert.AreEqual(HttpStatusCode.OK, download.StatusCode); Assert.AreEqual(original, await download.Content.ReadAsStringAsync());
            Assert.AreEqual("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task InvalidArtifactManifestsAndOffsetsReturnBadRequest()
    {
        var setup = await SetupAsync(); Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.Key);
        foreach (var json in new[] { "null", "{", "{}", "{\"Length\":-1}" })
        {
            using var response = await Http.PostAsync("/api/v1/dms/artifacts/uploads", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, json);
        }
        foreach (var query in new[] { "", "?offset=invalid", "?offset=-1" })
        {
            using var response = await Http.PutAsync("/api/v1/dms/artifacts/uploads/" + Guid.NewGuid() + query, new ByteArrayContent([]));
            Assert.IsTrue(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
        }
    }
    private Task<T> DatabaseAsync<T>(Func<TemplateDbContext, Task<T>> action) => Server!.Services.GetRequiredService<ControllerGate>().RunAsync(async () =>
    {
        using var scope = Server.Services.CreateScope(); return await action(scope.ServiceProvider.GetRequiredService<TemplateDbContext>());
    });
    private async Task<(string User, string Reviewer, string Key, string ReviewerKey, string StrangerKey, Guid Machine, string AgentKey)> SetupAsync()
    {
        return await DatabaseAsync(async db =>
        {
            var users = Enumerable.Range(0, 3).Select(i => new User { UserName = "artifact-" + i + "-" + Guid.NewGuid().ToString("N"), DisplayName = "Artifact fixture" }).ToArray();
            var keys = users.Select(_ => DmsKeyAuthenticationHandler.CreateKey(false)).ToArray(); var agent = DmsKeyAuthenticationHandler.CreateKey(true);
            var machine = new Machine { Name = "Artifact test", KeyHash = DmsKeyAuthenticationHandler.Hash(agent) }; db.Machines.Add(machine); db.Users.AddRange(users);
            for (var i = 0; i < users.Length; i++)
            {
                db.PersonalApiKeys.Add(new PersonalApiKey { UserId = users[i].Id, Name = "Fixture", KeyHash = DmsKeyAuthenticationHandler.Hash(keys[i]), ExpiresAt = DateTime.UtcNow.AddHours(1) });
                foreach (var risk in Enum.GetValues<RiskLevel>()) DmsTestPermissions.Risk(db, users[i].Id, risk, request: i != 1, approve: i == 1);
            }
            await db.SaveChangesAsync(); return (users[0].Id, users[1].Id, keys[0], keys[1], keys[2], machine.Id, agent);
        });
    }
    private async Task<LockboxRequest> SubmitAsync<T>(string user, MachineWorkflowDefinition<T, OperationResult> definition, T input, Guid machine)
    {
        using var scope = Server!.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(user, definition.Request(input, "Artifact acceptance", [machine]));
    }
    [TestMethod]
    public async Task RealHttpChunkUploadDownloadAndReviewerAccessFollowFrozenRequestOwnership()
    {
        var setup = await SetupAsync(); var path = Directory.CreateTempSubdirectory("dms-artifact-http-").FullName;
        try
        {
            var source = Path.Combine(path, "large.bin"); var bytes = RandomNumberGenerator.GetBytes(17 * 1024 * 1024 + 31); await File.WriteAllBytesAsync(source, bytes);
            var artifacts = new DmsArtifactClient(Http.BaseAddress!.ToString(), setup.Key);
            var reference = await artifacts.UploadAsync(source, "large.bin", "file"); Assert.AreEqual(bytes.Length, reference.Length);
            var downloaded = Path.Combine(path, "downloaded.bin"); await artifacts.DownloadAsync(reference, downloaded);
            Assert.AreEqual(reference.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(downloaded))));
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.StrangerKey);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/v1/dms/artifacts/" + reference.Id + "/content")).StatusCode);
            var import = new ImportPathInput(reference, Path.Combine(path, "unapproved-target"));
            Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => SubmitAsync(setup.Reviewer, OperationsWorkflows.FilesystemImport, import, setup.Machine))).StatusCode);
            using (var provider = new ServiceCollection().AddLogging().AddAiurProtocolClient().BuildServiceProvider())
            {
                var other = new DmsApiClient(provider.GetRequiredService<AiurProtocolClient>(), Http.BaseAddress.ToString(), setup.StrangerKey);
                await Assert.ThrowsAsync<AiurUnexpectedServerResponseException>(() => other.SubmitAsync(OperationsWorkflows.FilesystemImport, import, "Cannot steal another user's artifact", [setup.Machine]));
            }
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.ReviewerKey);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/v1/dms/artifacts/" + reference.Id + "/content")).StatusCode);
            var box = await SubmitAsync(setup.User, OperationsWorkflows.FilesystemImport, import, setup.Machine); Assert.AreEqual(DecisionState.Pending, box.Decision);
            var review = await Http.GetAsync("/api/v1/dms/artifacts/" + reference.Id + "/content"); Assert.AreEqual(HttpStatusCode.OK, review.StatusCode);
            Assert.AreEqual(reference.Length, (await review.Content.ReadAsByteArrayAsync()).LongLength);
            Assert.IsFalse(File.Exists(import.Path));
            Assert.AreEqual(0, await DatabaseAsync(db => db.WorkflowRuns.CountAsync(r => r.LockboxId == box.Id && r.IsMachineTask)));
        }
        finally { Directory.Delete(path, true); }
    }
    [TestMethod]
    public async Task ApprovedExportUsesActualAgentOutboundHttpAndPreservesWorkflowIdentity()
    {
        var setup = await SetupAsync(); var path = Directory.CreateTempSubdirectory("dms-artifact-agent-").FullName;
        var journalPath = Path.Combine(path, "journal"); var journal = new TaskJournal(journalPath);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45)); Task? running = null;
        try
        {
            var source = Path.Combine(path, "source.bin"); var bytes = RandomNumberGenerator.GetBytes(9 * 1024 * 1024 + 7); await File.WriteAllBytesAsync(source, bytes);
            var box = await SubmitAsync(setup.User, OperationsWorkflows.FilesystemExport, new ExportPathInput(source), setup.Machine);
            using (var scope = Server!.Services.CreateScope())
            {
                box = await scope.ServiceProvider.GetRequiredService<WorkflowService>().DecideAsync(setup.Reviewer, box.Id, true, "Export exactly this fixture file");
            }
            running = new AgentClient(Http.BaseAddress!, setup.AgentKey, journal).RunAsync(stop.Token);
            List<WorkflowRun> tree;
            while (true)
            {
                tree = await DatabaseAsync(db => db.WorkflowRuns.AsNoTracking().Where(r => r.RootId == box.WorkflowId).ToListAsync(stop.Token));
                if (tree.Single(r => r.Id == box.WorkflowId).State.Terminal()) break;
                await Task.Delay(100, stop.Token);
            }
            Assert.AreEqual(RunState.Succeeded, tree.Single(r => r.Id == box.WorkflowId).State, JsonSerializer.Serialize(tree.Select(r => new { r.Definition, r.State, r.Result })));
            var machine = tree.Single(r => r.Definition == OperationsWorkflows.FilesystemExport.Name);
            var result = OperationsWorkflows.FilesystemExport.ReadResult(machine.Result); Assert.IsNotNull(result.Artifact);
            Assert.AreEqual(bytes.Length, result.Artifact.Length); Assert.AreEqual(box.WorkflowId, machine.RootId); Assert.IsNotNull(machine.ParentId);
            var destination = Path.Combine(path, "download.bin"); await new DmsArtifactClient(Http.BaseAddress!.ToString(), setup.Key).DownloadAsync(result.Artifact, destination);
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(destination))));
            Assert.IsTrue(tree.All(r => r.State == RunState.Succeeded));
            var imported = Path.Combine(path, "imported.bin");
            var importBox = await SubmitAsync(setup.User, OperationsWorkflows.FilesystemImport, new ImportPathInput(result.Artifact, imported), setup.Machine);
            Assert.IsFalse(File.Exists(imported));
            using (var scope = Server!.Services.CreateScope())
            {
                importBox = await scope.ServiceProvider.GetRequiredService<WorkflowService>().DecideAsync(setup.Reviewer, importBox.Id, true, "Import only the frozen exported bytes");
            }
            while (true)
            {
                var state = await DatabaseAsync(db => db.WorkflowRuns.Where(r => r.Id == importBox.WorkflowId).Select(r => r.State).SingleAsync(stop.Token));
                if (state.Terminal()) { Assert.AreEqual(RunState.Succeeded, state); break; }
                await Task.Delay(100, stop.Token);
            }
            Assert.AreEqual(result.Artifact.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(imported))));
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.AgentKey);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/agent/artifacts/" + result.Artifact.Id + "/content?taskId=" + Guid.NewGuid())).StatusCode);
        }
        finally
        {
            await stop.CancelAsync(); if (running != null) { try { await running; } catch (OperationCanceledException) { } }
            journal.Dispose(); Directory.Delete(path, true);
        }
    }
}
