using Aiursoft.DMS.Authorization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class DmsWorkflowTests : TestBase
{
    private sealed class UnavailableWorkflow(string name) : MachineWorkflow
    {
        public override string Name => name;
        public override Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token) =>
            throw new AssertFailedException("An unsupported workflow must never execute.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnsupportedOffersFailVisiblyAndTheSameAgentConnectionContinues(bool mismatchedVersion)
    {
        var setup = await ArrangeAsync(true);
        var key = DmsKeyAuthenticationHandler.CreateKey(true);
        var definition = new MachineWorkflowDefinition<EmptyInput, EmptyInput>("test.unavailable", RiskLevel.Low, "next", PublicEntry: true);
        Server!.Services.GetRequiredService<MachineWorkflowRegistry>().Register(definition, (_, _) => new UnavailableWorkflow(definition.Name));
        var registry = new MachineWorkflowRegistry();
        if (mismatchedVersion)
            registry.Register(definition with { Version = "previous" }, (_, _) => new UnavailableWorkflow(definition.Name));
        using (var scope = Server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.Machines.SingleAsync(m => m.Id == setup.Machine)).KeyHash = DmsKeyAuthenticationHandler.Hash(key);
            await db.SaveChangesAsync();
        }
        var path = Path.Combine(Path.GetTempPath(), "dms-unsupported-agent-" + Guid.NewGuid());
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = stopping.Token;
        try
        {
            using var journal = new Agent.TaskJournal(path);
            var agent = new Agent.AgentClient(Http.BaseAddress!, key, journal, registry).RunAsync(stopping.Token);
            try
            {
                var sessions = Server.Services.GetRequiredService<AgentSessions>();
                while (sessions.Get(setup.Machine)?.Ready != true) await Task.Delay(50, stopping.Token);
                var connection = sessions.Get(setup.Machine);
                LockboxRequest rejected;
                using (var scope = Server.Services.CreateScope())
                    rejected = await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User,
                        definition.Request(new EmptyInput(), "Report an unsupported offer", [setup.Machine]));
                var failed = await WaitForMachineAsync(rejected.WorkflowId!.Value, RunState.Failed);
                Assert.IsNull(failed.StartedAt);
                Assert.IsNotNull(failed.FinishedAt);
                StringAssert.Contains(failed.Result, "UnsupportedWorkflow");
                StringAssert.Contains(failed.Result, "next");
                StringAssert.Contains(failed.Result, mismatchedVersion ? "previous" : "not installed");
                Assert.IsNull(journal.Start(failed.Id));
                while (true)
                {
                    using var scope = Server.Services.CreateScope();
                    var tree = await scope.ServiceProvider.GetRequiredService<WorkflowService>().CallstackAsync(setup.User, rejected.WorkflowId!.Value);
                    if (tree.All(r => r.State == RunState.Failed)) break;
                    await Task.Delay(50, stopping.Token);
                }

                LockboxRequest supported;
                using (var scope = Server.Services.CreateScope())
                    supported = await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User,
                        MachineWorkflows.AgentStatus.Request(new EmptyInput(), "Continue after rejection", [setup.Machine]));
                var succeeded = await WaitForMachineAsync(supported.WorkflowId!.Value, RunState.Succeeded);
                StringAssert.Contains(succeeded.Result, journal.InstallationId.ToString());
                Assert.AreSame(connection, sessions.Get(setup.Machine));
            }
            finally
            {
                await stopping.CancelAsync();
                try { await agent; } catch (OperationCanceledException) { }
            }
        }
        finally { Directory.Delete(path, true); }

        async Task<WorkflowRun> WaitForMachineAsync(Guid root, RunState state)
        {
            while (true)
            {
                using var scope = Server.Services.CreateScope();
                var run = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().WorkflowRuns.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.RootId == root && r.IsMachineTask, token);
                if (run?.State == state) return run;
                await Task.Delay(50, token);
            }
        }
    }

    [TestMethod]
    [DataRow(RunState.Offered, false, RunState.Cancelled)]
    [DataRow(RunState.CancelRequested, false, RunState.Cancelled)]
    [DataRow(RunState.Running, true, RunState.CancelRequested)]
    [DataRow(RunState.NeedsVerification, true, RunState.CancelRequested)]
    [DataRow(RunState.Offered, true, RunState.CancelRequested)]
    public async Task ApprovedCancellationCompletesUnstartedOffersWithoutGrantingLateStart(RunState initial, bool started, RunState expected)
    {
        var setup = await ArrangeAsync(true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Guid operation;
        using (var scope = Server!.Services.CreateScope())
            operation = (await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User, Request(setup.Machine))).WorkflowId!.Value;
        WorkflowRun? machine = null;
        while (machine == null)
        {
            using var scope = Server.Services.CreateScope();
            machine = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().WorkflowRuns.AsNoTracking()
                .SingleOrDefaultAsync(r => r.ParentId == operation && r.IsMachineTask, timeout.Token);
            if (machine == null) await Task.Delay(50, timeout.Token);
        }
        var startedAt = started ? DateTime.UtcNow.AddMinutes(-1) : (DateTime?)null;
        LockboxRequest cancellation;
        using (var scope = Server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var task = await db.WorkflowRuns.SingleAsync(r => r.Id == machine.Id, timeout.Token);
            task.State = initial; task.StartedAt = startedAt;
            await db.SaveChangesAsync(timeout.Token);
            cancellation = await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User,
                new OperationRequest { Workflow = WorkflowNames.Kill, TargetWorkflowId = machine.Id, Reason = "Cancel the original approved task" });
        }
        while (true)
        {
            using var scope = Server.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var kill = await db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == cancellation.WorkflowId, timeout.Token);
            if (kill.State is RunState.Running or RunState.Succeeded) break;
            await Task.Delay(50, timeout.Token);
        }
        using var check = Server.Services.CreateScope();
        var database = check.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var cancelled = await database.WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == machine.Id, timeout.Token);
        Assert.AreEqual(expected, cancelled.State);
        Assert.AreEqual(startedAt, cancelled.StartedAt);
        Assert.AreEqual(expected == RunState.Cancelled, cancelled.FinishedAt != null);
        var node = await database.Machines.AsNoTracking().SingleAsync(m => m.Id == setup.Machine, timeout.Token);
        using var session = new AgentSession(new ClientWebSocket(), node.KeyHash) { Ready = true };
        var reply = await check.ServiceProvider.GetRequiredService<AgentEvents>().HandleAsync(setup.Machine, session,
            new AgentMessage { Kind = "status", TaskId = machine.Id, State = RunState.Accepted });
        Assert.AreEqual("cancel", reply?.Kind);
        var unchanged = await database.WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == machine.Id, timeout.Token);
        Assert.AreEqual(expected, unchanged.State);
        Assert.AreEqual(startedAt, unchanged.StartedAt);
    }

    [TestMethod]
    public async Task CliReportsAuthenticationAndBusinessFailuresWithoutStackTracesOrStandardOutput()
    {
        var setup = await ArrangeAsync();
        var key = DmsKeyAuthenticationHandler.CreateKey(false);
        var run = new WorkflowRun { Definition = WorkflowNames.Lockbox, State = RunState.Succeeded };
        run.RootId = run.Id;
        int before;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        DmsTestPermissions.DeactivatePreviousMachines(db);
            db.PersonalApiKeys.Add(new PersonalApiKey { UserId = setup.User, Name = "CLI errors", KeyHash = DmsKeyAuthenticationHandler.Hash(key), ExpiresAt = DateTime.UtcNow.AddHours(1) });
            db.WorkflowRuns.Add(run);
            await db.SaveChangesAsync();
            before = await db.Lockboxes.CountAsync();
        }
        foreach (var format in new[] { "table", "json", "csv" })
        {
            foreach (var (arguments, credential, expected) in new (string[], string, string)[]
            {
                (["operation", "get-workflow", "--id", Guid.NewGuid().ToString()], key, "Workflow not found."),
                (["operation", "get-trace", "--id", run.Id.ToString()], key, "ClickHouse is not configured."),
                (["operation", "query-audit"], key, "ClickHouse is not configured."),
                (["operation", "get-workflow", "--id", Guid.NewGuid().ToString()], DmsKeyAuthenticationHandler.CreateKey(false), "Authentication failed."),
                (["machine", "systemctl", "status", "--machine-id", setup.Machine.ToString(), "--reason", "Invalid input must not submit", "--service", "*.service"], key, "A concrete .service unit name is required."),
                (["machine", "box", "puller", "force-update", "--machine-id", setup.Machine.ToString(), "--reason", "Invalid image must not submit", "--image", "aiursoft/dms;id"], key, "Supply one concrete image name"),
                (["machine", "box", "puller", "force-update", "--machine-id", setup.Machine.ToString(), "--reason", "Invalid Puller must not submit", "--image", "aiursoft/dms", "--puller", "all"], key, "Select a Puller"),
                (["machine", "list", "--reason", new string('a', 2001)], key, "A request ID and a reason of at most 2000 characters are required.")
            })
            {
                var cli = await Client.DmsCommandApp.Create().TestRunAsync([.. arguments, "--server", Http.BaseAddress!.ToString(), "--api-key", credential, "--output-format", format]);
                Assert.AreEqual(1, cli.ProgramReturn, cli.StdErr);
                Assert.AreEqual(string.Empty, cli.StdOut);
                StringAssert.StartsWith(cli.StdErr, "Error: ");
                StringAssert.Contains(cli.StdErr, expected);
                Assert.DoesNotContain("Exception", cli.StdErr);
                Assert.DoesNotContain("   at ", cli.StdErr);
                Assert.DoesNotContain(credential, cli.StdErr);
            }
        }
        using var check = Server!.Services.CreateScope();
        Assert.AreEqual(before, await check.ServiceProvider.GetRequiredService<TemplateDbContext>().Lockboxes.CountAsync());
    }

    [TestMethod]
    public async Task WorkflowInspectionShowsRootAndParentAndDoesNotCreateWork()
    {
        var setup = await ArrangeAsync();
        var key = DmsKeyAuthenticationHandler.CreateKey(false);
        var agentKey = DmsKeyAuthenticationHandler.CreateKey(true);
        var root = new WorkflowRun { Definition = WorkflowNames.Lockbox, State = RunState.Succeeded };
        root.RootId = root.Id;
        var parent = new WorkflowRun { RootId = root.Id, ParentId = root.Id, Definition = WorkflowNames.Batch, State = RunState.Succeeded };
        var child = new WorkflowRun { RootId = root.Id, ParentId = parent.Id, MachineId = setup.Machine, Definition = WorkflowNames.DiskFree, State = RunState.Succeeded, Result = JsonSerializer.Serialize(new { ExitCode = 0, Output = "disk, \"quoted\"\nsecond line", ErrorOutput = "" }) };
        var sibling = new WorkflowRun { RootId = root.Id, ParentId = root.Id, Definition = WorkflowNames.ListMachines, State = RunState.Failed };

        var lockboxId = Guid.NewGuid();
        foreach (var run in new[] { root, parent, child, sibling }) run.LockboxId = lockboxId;
        var hiddenRun = new WorkflowRun { Definition = WorkflowNames.Lockbox, State = RunState.Succeeded, LockboxId = Guid.NewGuid() };
        hiddenRun.RootId = hiddenRun.Id;
        int lockboxes;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.PersonalApiKeys.Add(new PersonalApiKey { UserId = setup.User, Name = "Inspection", KeyHash = DmsKeyAuthenticationHandler.Hash(key), ExpiresAt = DateTime.UtcNow.AddHours(1) });
            (await db.Machines.SingleAsync(m => m.Id == setup.Machine)).KeyHash = DmsKeyAuthenticationHandler.Hash(agentKey);
            db.WorkflowRuns.AddRange(root, parent, child, sibling, hiddenRun);
            await db.SaveChangesAsync(); lockboxes = await db.Lockboxes.CountAsync();
        }
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        foreach (var run in new[] { root, parent, child })
        {
            var response = await Http.GetAsync($"/api/v1/dms/operations/{run.Id}");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = Newtonsoft.Json.Linq.JObject.Parse(await response.Content.ReadAsStringAsync());
            var details = body.GetValue("Data", StringComparison.OrdinalIgnoreCase)!.ToObject<WorkflowDetails>()!;
            Assert.AreEqual(run.Id, details.WorkflowId); Assert.AreEqual(root.Id, details.RootWorkflowId);
            Assert.AreEqual(run.ParentId, details.ParentWorkflowId);
            Assert.AreEqual(new LockboxId(run.LockboxId), details.LockboxId);
            Assert.AreEqual(run.MachineId, details.MachineId); Assert.AreEqual(run.Definition, details.Definition);
            Assert.AreEqual(run.State, details.State); Assert.AreEqual(run.Result, details.Result);
            Assert.AreEqual(run.CreatedAt, details.CreatedAt); Assert.AreEqual(run.StartedAt, details.StartedAt);
            Assert.AreEqual(run.FinishedAt, details.FinishedAt);
        }
        var cli = await Client.DmsCommandApp.Create().TestRunAsync(["operation", "get-workflow", "--id", child.Id.ToString(), "--server", Http.BaseAddress!.ToString(), "--api-key", key, "--output-format", "json"]);
        Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
        StringAssert.Contains(cli.StdOut, "RootWorkflowId"); StringAssert.Contains(cli.StdOut, "ParentWorkflowId");
        StringAssert.Contains(cli.StdOut, child.Id.ToString()); StringAssert.Contains(cli.StdOut, parent.Id.ToString());
        var table = await Client.DmsCommandApp.Create().TestRunAsync(["operation", "get-workflow", "--id", child.Id.ToString(), "--server", Http.BaseAddress!.ToString(), "--api-key", key]);
        Assert.AreEqual(0, table.ProgramReturn, table.StdErr);
        StringAssert.Contains(table.StdOut, "Property"); StringAssert.Contains(table.StdOut, "Value");
        StringAssert.Contains(table.StdOut, "Succeeded"); StringAssert.Contains(table.StdOut, "Output");
        StringAssert.Contains(table.StdOut, "disk, \"quoted\""); StringAssert.Contains(table.StdOut, "second line");
        Assert.DoesNotContain("\\n", table.StdOut); Assert.DoesNotContain("ProtocolVersion", table.StdOut);
        StringAssert.Contains(table.StdOut, child.Id.ToString()); StringAssert.Contains(table.StdOut, root.Id.ToString());
        var csv = await Client.DmsCommandApp.Create().TestRunAsync(["operation", "get-workflow", "--id", child.Id.ToString(), "--server", Http.BaseAddress!.ToString(), "--api-key", key, "--output-format", "CsV"]);
        Assert.AreEqual(0, csv.ProgramReturn, csv.StdErr);
        using (var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csv.StdOut)))
        {
            parser.SetDelimiters(","); parser.TrimWhiteSpace = false;
            var headers = parser.ReadFields()!; var values = parser.ReadFields()!;
            Assert.AreEqual(child.Id.ToString(), values[Array.IndexOf(headers, "WorkflowId")]);
            Assert.AreEqual(parent.Id.ToString(), values[Array.IndexOf(headers, "ParentWorkflowId")]);
            Assert.AreEqual(child.Result, values[Array.IndexOf(headers, "Result")]);
            Assert.IsTrue(parser.EndOfData);
        }
        var tree = await Client.DmsCommandApp.Create().TestRunAsync(["operation", "get-callstack", "--id", root.Id.ToString(), "--server", Http.BaseAddress!.ToString(), "--api-key", key]);
        Assert.AreEqual(0, tree.ProgramReturn, tree.StdErr);
        StringAssert.Contains(tree.StdOut, "├── "); StringAssert.Contains(tree.StdOut, "│   └── ");
        foreach (var id in new[] { root.Id, parent.Id, child.Id, sibling.Id }) StringAssert.Contains(tree.StdOut, id.ToString());
        var subtree = await Client.DmsCommandApp.Create().TestRunAsync(["operation", "get-callstack", "--id", parent.Id.ToString(), "--server", Http.BaseAddress!.ToString(), "--api-key", key, "--output-format", "json"]);
        Assert.AreEqual(0, subtree.ProgramReturn, subtree.StdErr);
        StringAssert.Contains(subtree.StdOut, parent.Id.ToString()); StringAssert.Contains(subtree.StdOut, child.Id.ToString());
        Assert.DoesNotContain(sibling.Id.ToString(), subtree.StdOut); Assert.DoesNotContain("└──", subtree.StdOut);
        Assert.AreEqual(HttpStatusCode.OK, (await Http.GetAsync($"/api/v1/dms/operations/{hiddenRun.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await Http.GetAsync($"/api/v1/dms/operations/{hiddenRun.Id}/callstack")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync($"/api/v1/dms/operations/{Guid.NewGuid()}")).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentKey);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync($"/api/v1/dms/operations/{child.Id}")).StatusCode);
        using var check = Server!.Services.CreateScope();
        var checkedDb = check.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(lockboxes, await checkedDb.Lockboxes.CountAsync());
        Assert.IsTrue(await checkedDb.AuditEvents.AnyAsync(a => a.ActorId == setup.User && a.Action == "operation.get" && a.ResourceId == child.Id));
    }

    [TestMethod]
    public void CallstackTreePreservesIdsHierarchyAndStateWithOrWithoutColors()
    {
        var root = Guid.NewGuid(); var a = Guid.NewGuid(); var leaf = Guid.NewGuid(); var b = Guid.NewGuid();
        var nodes = Newtonsoft.Json.Linq.JArray.FromObject(new[]
        {
            new { Id = leaf, ParentId = (Guid?)a, Definition = "leaf", State = RunState.Warning },
            new { Id = root, ParentId = (Guid?)null, Definition = "root", State = RunState.Running },
            new { Id = a, ParentId = (Guid?)root, Definition = "a", State = RunState.Succeeded },
            new { Id = b, ParentId = (Guid?)root, Definition = "b", State = RunState.Failed }
        });
        var plain = Client.WorkflowTreeRenderer.Render(nodes, false).ToArray();
        Assert.HasCount(4, plain);
        StringAssert.StartsWith(plain[0], root.ToString());
        StringAssert.StartsWith(plain[1], "├── " + a);
        StringAssert.StartsWith(plain[2], "│   └── " + leaf);
        StringAssert.StartsWith(plain[3], "└── " + b);
        Assert.IsTrue(plain.All(line => !line.Contains('\u001b')));
        var colored = Client.WorkflowTreeRenderer.Render(nodes, true).ToArray();
        StringAssert.Contains(colored[0], "\u001b[34m" + root);
        StringAssert.Contains(colored[1], "\u001b[32m" + a);
        StringAssert.Contains(colored[2], "\u001b[33m" + leaf);
        StringAssert.Contains(colored[3], "\u001b[31m" + b);
        var subtree = Client.WorkflowTreeRenderer.Render(new Newtonsoft.Json.Linq.JArray(nodes[0].DeepClone(), nodes[2].DeepClone()), false).ToArray();
        StringAssert.StartsWith(subtree[0], a.ToString());
        StringAssert.StartsWith(subtree[1], "└── " + leaf);
    }

    [TestMethod]
    public void HumanOutputExpandsInventoryAndKeepsLargeCountsAndIds()
    {
        var id = Guid.NewGuid();
        var result = JsonSerializer.Serialize(new[] { new { Id = id, Name = "demo-node", TotalBytes = 9007199254740991L, State = 1 } });
        var response = new DmsResponse { Message = "Request completed.", Data = new { WorkflowId = id, State = RunState.Succeeded, Result = result } };
        using var output = new StringWriter();
        Client.DmsOutputWriter.Write(output, response, Client.OutputFormat.Table, false, false);
        var table = output.ToString();
        StringAssert.Contains(table, "Succeeded"); StringAssert.Contains(table, "TotalBytes");
        StringAssert.Contains(table, "9007199254740991"); StringAssert.Contains(table, id.ToString());
        Assert.DoesNotContain("Queued", table);
        Assert.DoesNotContain("\"", table);
        using var narrow = new StringWriter();
        Client.DmsOutputWriter.Write(narrow, response, Client.OutputFormat.Table, false, false, 60);
        StringAssert.Contains(narrow.ToString(), id.ToString()); StringAssert.Contains(narrow.ToString(), "demo-node");
        using var json = new StringWriter();
        Client.DmsOutputWriter.Write(json, response, Client.OutputFormat.Json, false, true);
        var data = Newtonsoft.Json.Linq.JObject.Parse(json.ToString())["Data"]!;
        Assert.AreEqual(5, data["State"]!.ToObject<int>());
        Assert.AreEqual(result, data["Result"]!.ToObject<string>());
    }

    [TestMethod]
    public void TraceCsvPreservesQuotesNewlinesAndEmptyPageHeaders()
    {
        var id = Guid.NewGuid(); var message = "disk, \"quoted\"\nsecond line";
        var response = new DmsResponse { Message = "Request completed.", Data = new { Entries = new[] { new { EventId = "one", WorkflowId = id, Time = DateTime.UnixEpoch, Source = "stdout", Message = message } }, PendingArchiveEvents = 3, NextOffset = 200 } };
        using var csv = new StringWriter();
        Client.DmsOutputWriter.Write(csv, response, Client.OutputFormat.Csv, false, true);
        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csv.ToString()));
        parser.SetDelimiters(","); parser.TrimWhiteSpace = false;
        var headers = parser.ReadFields()!; var values = parser.ReadFields()!;
        Assert.AreEqual(message, values[Array.IndexOf(headers, "Message")]);
        Assert.AreEqual(id.ToString(), values[Array.IndexOf(headers, "WorkflowId")]);
        Assert.IsTrue(parser.EndOfData); Assert.DoesNotContain("\u001b", csv.ToString());
        using var empty = new StringWriter();
        Client.DmsOutputWriter.Write(empty, new DmsResponse { Message = "Request completed.", Data = new { Entries = Array.Empty<object>(), PendingArchiveEvents = 0, NextOffset = 200 } }, Client.OutputFormat.Csv, false, false);
        Assert.AreEqual(string.Join(',', headers) + "\r\n", empty.ToString());
        using var table = new StringWriter();
        Client.DmsOutputWriter.Write(table, response, Client.OutputFormat.Table, false, false);
        StringAssert.Contains(table.ToString(), "PendingArchiveEvents"); StringAssert.Contains(table.ToString(), "NextOffset");
        StringAssert.Contains(table.ToString(), "second line");
    }

    [TestMethod]
    public async Task WebHistoryFiltersWorkflowLocationsAndKeepsFiltersWhenPaging()
    {
        var setup = await ArrangeAsync();
        var definition = "history." + Guid.NewGuid().ToString("N");
        var runs = Enumerable.Range(0, 51).Select(i => new WorkflowRun
        {
            Definition = definition,
            State = RunState.Succeeded, CreatedAt = DateTime.UtcNow.AddMinutes(-i)
        }).ToList();
        var machineRun = new WorkflowRun { MachineId = setup.Machine, Definition = WorkflowNames.Performance, State = RunState.Succeeded };
        var otherRun = new WorkflowRun { Definition = "other." + definition, State = RunState.Succeeded };
        var hiddenRun = new WorkflowRun { Definition = "other." + definition, State = RunState.Succeeded };
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();

            DmsTestPermissions.Risk(db, setup.User, RiskLevel.Safe, request: true, automatic: false, approve: false);
            db.WorkflowRuns.AddRange(runs);
            db.WorkflowRuns.AddRange(machineRun, otherRun, hiddenRun);
            await db.SaveChangesAsync();
        }
        await LoginAsAdmin();
        var path = $"/Dms/Operations?clusterWorkflowsOnly=true&definition={definition}";
        var first = await Http.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        var html = await first.Content.ReadAsStringAsync();
        StringAssert.Contains(html, runs[0].Id.ToString());
        Assert.DoesNotContain(runs[50].Id.ToString(), html);
        Assert.DoesNotContain(otherRun.Id.ToString(), html);
        Assert.DoesNotContain(hiddenRun.Id.ToString(), html);
        var next = System.Text.RegularExpressions.Regex.Matches(html, "href=\"([^\"]+)\"")
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
            .Single(link => link.Contains("page=1", StringComparison.Ordinal));
        StringAssert.Contains(next, $"definition={definition}");
        Assert.IsTrue(next.Contains("clusterWorkflowsOnly=True", StringComparison.OrdinalIgnoreCase));
        var second = await Http.GetAsync(next);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        var secondHtml = await second.Content.ReadAsStringAsync();
        StringAssert.Contains(secondHtml, runs[50].Id.ToString());
        Assert.DoesNotContain(runs[0].Id.ToString(), secondHtml);
        var clusterOnly = await Http.GetStringAsync($"/Dms/Operations?clusterWorkflowsOnly=true");
        Assert.DoesNotContain(machineRun.Id.ToString(), clusterOnly);
        var machineOnly = await Http.GetStringAsync($"/Dms/Operations?machineId={setup.Machine}");
        StringAssert.Contains(machineOnly, machineRun.Id.ToString());
        Assert.DoesNotContain(runs[0].Id.ToString(), machineOnly);
    }

    [TestMethod]
    public async Task DockerCliFreezesLiteralArgumentsAndRequiresVeryHighPermissionForExec()
    {
        var setup = await ArrangeAsync();
        var key = DmsKeyAuthenticationHandler.CreateKey(false);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        db.PersonalApiKeys.Add(new PersonalApiKey { UserId = setup.User, Name = "Docker CLI", KeyHash = DmsKeyAuthenticationHandler.Hash(key), ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        var container = new string('a', 64);
        string[] common = ["--machine-id", setup.Machine.ToString(), "--reason", "Docker command",
            "--server", Http.BaseAddress!.ToString(), "--api-key", key, "--output-format", "json"];
        string[] exec = ["machine", "docker", "exec", .. common, "--container", container, "--", "/start.sh", "a b", "$(id)", "--server", "unchanged"];
        var denied = await Client.DmsCommandApp.Create().TestRunAsync(exec);
        Assert.AreEqual(1, denied.ProgramReturn);
        Assert.AreEqual(0, await db.Lockboxes.CountAsync(l => l.RequesterId == setup.User));
        DmsTestPermissions.Risk(db, setup.User, RiskLevel.VeryHigh, request: true, automatic: false, approve: false);
        await db.SaveChangesAsync();
        var accepted = await Client.DmsCommandApp.Create().TestRunAsync(exec);
        Assert.AreEqual(0, accepted.ProgramReturn, accepted.StdErr);
        var box = await db.Lockboxes.SingleAsync(l => l.RequesterId == setup.User);
        Assert.AreEqual(RiskLevel.VeryHigh, box.Risk); Assert.AreEqual(DecisionState.Pending, box.Decision); Assert.IsNull(box.WorkflowId);
        var request = JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!;
        var input = MachineWorkflows.DockerExec.ReadInput(request.Input);
        Assert.AreEqual(container, input.Container);
        CollectionAssert.AreEqual(new[] { "/start.sh", "a b", "$(id)", "--server", "unchanged" }, input.Command);
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        request.Input = MachineWorkflows.DockerExec.Serialize(input with { Command = ["/different.sh"] });
        Assert.AreEqual(409, (await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, request))).StatusCode);

        var ps = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "docker", "ps", .. common, "--all", "--grep-name", "nugetninja.worker"]);
        Assert.AreEqual(0, ps.ProgramReturn, ps.StdErr);
        var psBox = await db.Lockboxes.SingleAsync(l => l.RequesterId == setup.User && l.Definition == "machine.docker.ps");
        var psInput = MachineWorkflows.DockerPs.ReadInput(JsonSerializer.Deserialize<OperationRequest>(psBox.Parameters)!.Input);
        Assert.IsTrue(psInput.All); Assert.AreEqual("nugetninja.worker", psInput.GrepName);
        var logs = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "docker", "logs", .. common, "--container", container]);
        Assert.AreEqual(0, logs.ProgramReturn, logs.StdErr);
        var logsBox = await db.Lockboxes.SingleAsync(l => l.RequesterId == setup.User && l.Definition == "machine.docker.logs");
        Assert.AreEqual(100, MachineWorkflows.DockerLogs.ReadInput(JsonSerializer.Deserialize<OperationRequest>(logsBox.Parameters)!.Input).Tail);
        var kill = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "docker", "kill", .. common, "--container", container]);
        Assert.AreEqual(0, kill.ProgramReturn, kill.StdErr);
        var killBox = await db.Lockboxes.SingleAsync(l => l.RequesterId == setup.User && l.Definition == "machine.docker.kill");
        Assert.AreEqual(RiskLevel.High, killBox.Risk);
        Assert.IsNull(killBox.WorkflowId);
    }

    private async Task<(string User, string Approver, Guid Machine)> ArrangeAsync(bool automatic = false)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        DmsTestPermissions.DeactivatePreviousMachines(db);
        var user = new User { UserName = Guid.NewGuid().ToString("N"), Email = "requester@example.test", DisplayName = "Requester" }; db.Users.Add(user);
        var approver = new User { DisplayName = "Approver", UserName = "approver", Id = Guid.NewGuid().ToString() };
        db.Users.Add(approver);

        var machine = new Machine { Name = "Test machine", KeyHash = DmsKeyAuthenticationHandler.Hash(DmsKeyAuthenticationHandler.CreateKey(true)) };
        db.Machines.Add(machine);
        foreach (var risk in new[] { RiskLevel.Safe, RiskLevel.Low, RiskLevel.Medium, RiskLevel.High })
        {
            DmsTestPermissions.Risk(db, user.Id, risk, request: true, automatic: automatic, approve: true);
            DmsTestPermissions.Risk(db, approver.Id, risk, request: false, automatic: false, approve: true);
        }
        await db.SaveChangesAsync();
        return (user.Id, approver.Id, machine.Id);
    }

    private OperationRequest Request(Guid machine) => new()
    {
        Workflow = WorkflowNames.Restart, MachineIds = [machine], Input = JsonSerializer.Serialize(new ServiceInput("dms-test.service")), Reason = "Business acceptance"
    };

    [TestMethod]
    public async Task ApprovalRequiresAnotherUserAndCreatesOneChild()
    {
        var setup = await ArrangeAsync();
        Guid id;
        using (var scope = Server!.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
            var item = await service.SubmitAsync(setup.User, Request(setup.Machine));
            Assert.AreEqual(DecisionState.Pending, item.Decision); Assert.IsNull(item.WorkflowId); id = item.Id;
            var forbidden = await Assert.ThrowsAsync<DmsException>(() => service.DecideAsync(setup.User, id, true, "Self approval"));
            Assert.AreEqual(403, forbidden.StatusCode);
        }
        using (var scope = Server!.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
            var item = await service.DecideAsync(setup.Approver, id, true, "Approved maintenance");
            Assert.AreEqual(DecisionState.Approved, item.Decision); Assert.IsNotNull(item.WorkflowId);
            var tree = await service.CallstackAsync(setup.User, item.WorkflowId!.Value);
            Assert.IsTrue(tree.Any(r => r.Id == item.WorkflowId && r.ParentId == null && r.RootId == r.Id));
            var conflict = await Assert.ThrowsAsync<DmsException>(() => service.DecideAsync(setup.Approver, id, false, "Late rejection"));
            Assert.AreEqual(409, conflict.StatusCode);
        }
    }

    [TestMethod]
    public async Task AutoApprovalDoesNotNeedSelfApprovalAndTargetsStayFrozen()
    {
        var setup = await ArrangeAsync(true);
        using var scope = Server!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var request = Request(setup.Machine); request.AllMachines = true; request.MachineIds.Clear();
        var item = await service.SubmitAsync(setup.User, request);
        Assert.IsTrue(item.Automatic); Assert.AreEqual(DecisionState.Approved, item.Decision); Assert.IsNull(item.ApproverId);
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        db.Machines.Add(new Machine { Name = "Added after submission", KeyHash = "another" }); await db.SaveChangesAsync();
        Assert.AreEqual(item.Id, (await service.SubmitAsync(setup.User, request)).Id);
        var targets = JsonSerializer.Deserialize<List<Guid>>(item.TargetMachines)!;
        CollectionAssert.AreEqual(new[] { setup.Machine }, targets);
        request.Input = JsonSerializer.Serialize(new ServiceInput("changed.service"));
        var conflict = await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, request));
        Assert.AreEqual(409, conflict.StatusCode);
    }

    [TestMethod]
    public async Task DenialStartsNoOperationAndExplicitSelfApprovalWorks()
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var item = await service.SubmitAsync(setup.User, Request(setup.Machine));
        await service.DecideAsync(setup.Approver, item.Id, false, "Maintenance window closed");
        Assert.IsNull(item.WorkflowId);
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        DmsTestPermissions.Set(db, setup.User, AppPermissionNames.CanApproveOwnWorkflow); await db.SaveChangesAsync();
        var second = await service.SubmitAsync(setup.User, Request(setup.Machine));
        await service.DecideAsync(setup.User, second.Id, true, "Explicit self approval grant");
        Assert.AreEqual(DecisionState.Approved, second.Decision);
    }

    [TestMethod]
    public async Task UnavailableMachinesAndInternalWorkflowRequestsAreRejected()
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var denied = await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, Request(Guid.NewGuid())));
        Assert.AreEqual(400, denied.StatusCode);
        var request = Request(setup.Machine); request.Workflow = WorkflowNames.Process;
        var invalid = await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, request)); Assert.AreEqual(400, invalid.StatusCode);
    }

    [TestMethod]
    public async Task AgentKeyCannotInvokeHumanApiAndPersonalKeyCanSubmit()
    {
        var setup = await ArrangeAsync(true);
        var agentKey = DmsKeyAuthenticationHandler.CreateKey(true);
        var personal = DmsKeyAuthenticationHandler.CreateKey(false);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.Machines.SingleAsync(m => m.Id == setup.Machine)).KeyHash = DmsKeyAuthenticationHandler.Hash(agentKey);
            db.PersonalApiKeys.Add(new PersonalApiKey { UserId = setup.User, KeyHash = DmsKeyAuthenticationHandler.Hash(personal), Name = "test", ExpiresAt = DateTime.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
        }
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentKey);
        var denied = await Http.PostAsJsonAsync("/api/v1/dms/lockboxes", Request(setup.Machine));
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", personal);
        var accepted = await Http.PostAsJsonAsync("/api/v1/dms/lockboxes", Request(setup.Machine));
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        var websocket = await Http.GetAsync("/api/agent/channel"); Assert.AreEqual(HttpStatusCode.Forbidden, websocket.StatusCode);
        var cli = await Client.DmsCommandApp.Create().TestRunAsync([
            "machine", "performance", "info", "--server", Http.BaseAddress!.ToString(), "--api-key", personal,
            "--machine-id", setup.Machine.ToString(), "--reason", "CLI acceptance", "--output-format", "json"]);
        Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
        StringAssert.Contains(cli.StdOut, "WorkflowId");
        foreach (var command in new[] { new[] { "agent", "status" }, new[] { "systemctl", "status" }, new[] { "layout", "lsblk" }, new[] { "layout", "df" } })
        {
            var args = new List<string> { "machine", command[0], command[1], "--server", Http.BaseAddress!.ToString(), "--api-key", personal,
                "--machine-id", setup.Machine.ToString(), "--reason", "Read only CLI", "--output-format", "json" };
            if (command[0] == "systemctl") args.AddRange(["--service", "dms-test.service"]);
            var result = await Client.DmsCommandApp.Create().TestRunAsync(args.ToArray());
            Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
            StringAssert.Contains(result.StdOut, "WorkflowId");
        }
        int beforeInvalidFormat;
        using (var countScope = Server!.Services.CreateScope()) beforeInvalidFormat = await countScope.ServiceProvider.GetRequiredService<TemplateDbContext>().Lockboxes.CountAsync();
        foreach (var format in new[] { "yaml", "999" })
        {
            var badFormat = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "performance", "info", "--server", Http.BaseAddress!.ToString(), "--api-key", personal,
                "--machine-id", setup.Machine.ToString(), "--reason", "Must not submit", "--output-format", format]);
            Assert.AreNotEqual(0, badFormat.ProgramReturn);
        }
        using (var countScope = Server!.Services.CreateScope()) Assert.AreEqual(beforeInvalidFormat, await countScope.ServiceProvider.GetRequiredService<TemplateDbContext>().Lockboxes.CountAsync());
        var invalidRequest = Request(setup.Machine);
        invalidRequest.Input = JsonSerializer.Serialize(new ServiceInput("*.service"));
        var invalid = await Http.PostAsJsonAsync("/api/v1/dms/lockboxes", invalidRequest);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        var error = await invalid.Content.ReadFromJsonAsync<Aiursoft.AiurProtocol.Models.AiurCollection<string>>();
        Assert.IsNotNull(error?.Items);
        Assert.HasCount(1, error.Items);
        var empty = await Http.PostAsJsonAsync<OperationRequest?>("/api/v1/dms/lockboxes", null);
        Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [TestMethod]
    public async Task ConcurrentApprovalsCreateOnlyOneOperation()
    {
        var setup = await ArrangeAsync();
        Guid id;
        using (var scope = Server!.Services.CreateScope())
            id = (await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User, Request(setup.Machine))).Id;
        async Task<bool> Decide()
        {
            using var scope = Server.Services.CreateScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<WorkflowService>().DecideAsync(setup.Approver, id, true, "Concurrent review");
                return true;
            }
            catch (DmsException exception) when (exception.StatusCode == 409) { return false; }
        }
        var results = await Task.WhenAll(Decide(), Decide());
        Assert.AreEqual(1, results.Count(success => success));
        using var check = Server.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var item = await db.Lockboxes.SingleAsync(l => l.Id == id);
        Assert.AreEqual(1, await db.WorkflowRuns.CountAsync(r => r.Id == item.WorkflowId && r.ParentId == null));
    }

    [TestMethod]
    public async Task ManualApprovalDispatchesExactlyTheTenSubmittedMachines()
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        for (var i = 1; i < 10; i++) db.Machines.Add(new Machine { Name = $"Machine {i}", KeyHash = Guid.NewGuid().ToString() });
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var request = Request(setup.Machine); request.AllMachines = true; request.MachineIds.Clear();
        var item = await service.SubmitAsync(setup.User, request);
        var late = new Machine { Name = "Eleventh machine", KeyHash = Guid.NewGuid().ToString() };
        db.Machines.Add(late); await db.SaveChangesAsync();
        await service.DecideAsync(setup.Approver, item.Id, true, "Ten machines approved");
        var operation = await db.WorkflowRuns.SingleAsync(r => r.Id == item.WorkflowId);
        await new MachineBatchWorkflow().ExecuteAsync(new ClusterWorkflowContext(db, operation, item, Server!.Services.GetRequiredService<WorkflowCatalog>().Find(item.Definition)));
        var dispatched = db.WorkflowRuns.Local.Where(r => r.ParentId == operation.Id).ToList();
        Assert.HasCount(10, dispatched);
        Assert.IsFalse(dispatched.Any(r => r.MachineId == late.Id));
    }

    [TestMethod]
    public async Task CorePagesRenderWithAuthorizedUser()
    {
        await ArrangeAsync(); await LoginAsAdmin();
        foreach (var route in new[] { "/Dms", "/Dms/Lockboxes", "/Dms/Operations", "/DmsAccess", "/DmsAccess/Keys" })
        {
            var response = await Http.GetAsync(route);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, route);
        }
    }
    [TestMethod]
    public async Task OfflineMachineKeepsApprovedReadOnlyWorkQueued()
    {
        var setup = await ArrangeAsync(true);
        Guid operationId;
        using (var scope = Server!.Services.CreateScope())
        {
            var input = Request(setup.Machine); input.Workflow = WorkflowNames.AgentStatus; input.Input = JsonSerializer.Serialize(new EmptyInput());
            var item = await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User, input);
            operationId = item.WorkflowId!.Value;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var scope = Server!.Services.CreateScope();
            var task = await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().WorkflowRuns.AsNoTracking()
                .SingleOrDefaultAsync(r => r.ParentId == operationId && r.IsMachineTask, timeout.Token);
            if (task != null)
            {
                Assert.AreEqual(RunState.Queued, task.State);
                Assert.IsNull(task.StartedAt); Assert.IsNull(task.FinishedAt);
                break;
            }
            await Task.Delay(100, timeout.Token);
        }
    }

    [TestMethod]
    [DataRow("main")]
    [DataRow("cn")]
    public async Task PullerCliFreezesImageAndTargetsInHighRiskLockbox(string puller)
    {
        var setup = await ArrangeAsync();
        var key = DmsKeyAuthenticationHandler.CreateKey(false);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        db.PersonalApiKeys.Add(new PersonalApiKey { UserId = setup.User, Name = "Puller CLI", KeyHash = DmsKeyAuthenticationHandler.Hash(key), ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        foreach (var format in new[] { "table", "json", "csv" })
        {
            var request = Guid.NewGuid();
            var cli = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "box", "puller", "force-update",
                "--machine-id", setup.Machine.ToString(), "--image", "aiursoft/dms:latest", "--puller", puller, "--reason", "Update DMS mirror", "--request-id", request.ToString(),
                "--server", Http.BaseAddress!.ToString(), "--api-key", key, "--output-format", format]);
            Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
            var box = await db.Lockboxes.AsNoTracking().SingleAsync(l => l.ClientRequestId == request);
            Assert.AreEqual(WorkflowNames.PullerForceUpdate, box.Definition); Assert.AreEqual("2", box.Version);
            Assert.AreEqual(RiskLevel.High, box.Risk); Assert.AreEqual(DecisionState.Pending, box.Decision); Assert.IsNull(box.WorkflowId);
            var input = JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!;
            var frozen = MachineWorkflows.PullerForceUpdate.ReadInput(input.Input);
            Assert.AreEqual("aiursoft/dms:latest", frozen.Image);
            Assert.AreEqual(puller, frozen.Puller);
            CollectionAssert.AreEqual(new[] { setup.Machine }, JsonSerializer.Deserialize<Guid[]>(box.TargetMachines));
            StringAssert.Contains(cli.StdOut, box.WorkflowId.ToString());
        }
    }

    [TestMethod]
    [DataRow("table")]
    [DataRow("json")]
    [DataRow("csv")]
    public async Task MinimalPullerCliFreezesAllConfiguredImagesAndUniqueTargets(string format)
    {
        var setup = await ArrangeAsync();
        var key = DmsKeyAuthenticationHandler.CreateKey(false);
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        db.PersonalApiKeys.Add(new PersonalApiKey { UserId = setup.User, Name = "Default CLI", KeyHash = DmsKeyAuthenticationHandler.Hash(key), ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        var request = Guid.NewGuid();
        var cli = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "box", "puller", "force-update", "--reason", "Refresh configured mirrors", "--request-id", request.ToString(),
            "--server", Http.BaseAddress!.ToString(), "--api-key", key, "--output-format", format]);
        Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
        var box = await db.Lockboxes.AsNoTracking().SingleAsync(l => l.ClientRequestId == request);
        Assert.AreEqual(RiskLevel.High, box.Risk); Assert.AreEqual(DecisionState.Pending, box.Decision);
        var frozen = JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!;
        CollectionAssert.AreEqual(new[] { setup.Machine }, frozen.MachineIds);
        var selection = MachineWorkflows.PullerForceUpdate.ReadInput(frozen.Input);
        Assert.IsNull(selection.Image); Assert.AreEqual("main", selection.Puller); Assert.AreEqual("AllConfiguredImages", selection.ImageScope);
        StringAssert.Contains(frozen.Input, "AllConfiguredImages");
        CollectionAssert.AreEqual(new[] { setup.Machine }, JsonSerializer.Deserialize<Guid[]>(box.TargetMachines));
    }

    [TestMethod]
    [DataRow(WorkflowNames.PullerForceUpdate)]
    [DataRow(WorkflowNames.AgentStatus)]
    public async Task ImplicitScopeIsFrozenAndRetriesDoNotRetarget(string workflow)
    {
        var setup = await ArrangeAsync();
        var requestId = Guid.NewGuid();
        OperationRequest RequestDefault() => new() { RequestId = requestId, Workflow = workflow, Reason = "Frozen defaults" };
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var first = await service.SubmitAsync(setup.User, RequestDefault());
        var saved = JsonSerializer.Deserialize<OperationRequest>(first.Parameters)!;
        CollectionAssert.AreEqual(new[] { setup.Machine }, saved.MachineIds);
        if (workflow == WorkflowNames.PullerForceUpdate) StringAssert.Contains(saved.Input, "AllConfiguredImages");
        db.Machines.Add(new Machine { Name = "Added after approval scope was selected" });

        DmsTestPermissions.Risk(db, setup.User, RiskLevel.Safe, request: true, automatic: false, approve: false);
        await db.SaveChangesAsync();
        // MySQL JSON_REMOVE may reformat persisted parameters. Retries compare the same JSON values.
        first.Parameters = JsonSerializer.Serialize(JsonSerializer.Deserialize<OperationRequest>(first.Parameters), new JsonSerializerOptions { WriteIndented = true });
        await db.SaveChangesAsync();
        var retry = await service.SubmitAsync(setup.User, RequestDefault());
        Assert.AreEqual(first.Id, retry.Id);
        CollectionAssert.AreEqual(new[] { setup.Machine }, JsonSerializer.Deserialize<Guid[]>(retry.TargetMachines));
        Assert.AreEqual(1, await db.Lockboxes.CountAsync(l => l.RequesterId == setup.User));
        var changed = RequestDefault(); changed.Reason = "Different request";
        var conflict = await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, changed));
        StringAssert.Contains(conflict.Message, "different parameters");
    }

    [TestMethod]
    public async Task DefaultsRejectAmbiguityAndNeverExpandToAllMachines()
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var extra = new Machine { Name = "Second active machine" }; db.Machines.Add(extra);
        await db.SaveChangesAsync();
        var ambiguous = await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, new OperationRequest { Workflow = WorkflowNames.PullerForceUpdate, Reason = "Choose a machine" }));
        StringAssert.Contains(ambiguous.Message, "--machine-id or --all-machines");
        Assert.AreEqual(0, await db.Lockboxes.CountAsync(l => l.RequesterId == setup.User));

        DmsTestPermissions.Risk(db, setup.User, RiskLevel.Safe, request: true, automatic: false, approve: false);
        await db.SaveChangesAsync();
        Assert.AreEqual(0, await db.Lockboxes.CountAsync(l => l.RequesterId == setup.User));
        var explicitAll = await service.SubmitAsync(setup.User, new OperationRequest { Workflow = WorkflowNames.PullerForceUpdate, AllMachines = true, Reason = "Explicit batch" });
        CollectionAssert.AreEquivalent(new[] { setup.Machine, extra.Id }, JsonSerializer.Deserialize<Guid[]>(explicitAll.TargetMachines));
        Assert.AreEqual(RiskLevel.High, explicitAll.Risk);
    }

    [TestMethod]
    public async Task DefaultSelectionDoesNotBypassRequestPermissionsOrSelectDisabledMachines()
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        DmsTestPermissions.Set(db, setup.User, DmsPermissions.Request(RiskLevel.High), false);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, new OperationRequest { Workflow = WorkflowNames.PullerForceUpdate, Reason = "No high permission" }));
        DmsTestPermissions.Set(db, setup.User, DmsPermissions.Request(RiskLevel.High));
        (await db.Machines.SingleAsync(m => m.Id == setup.Machine)).Disabled = true;
        await db.SaveChangesAsync();
        var missing = await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, new OperationRequest { Workflow = WorkflowNames.PullerForceUpdate, Reason = "No active target" }));
        StringAssert.Contains(missing.Message, "--machine-id or --all-machines");
        Assert.AreEqual(0, await db.Lockboxes.CountAsync(l => l.RequesterId == setup.User));
    }

    [TestMethod]
    [DataRow(WorkflowNames.AgentStatus, RiskLevel.Safe)]
    [DataRow(WorkflowNames.ServiceStatus, RiskLevel.Low)]
    [DataRow(WorkflowNames.ListBlockDevices, RiskLevel.Safe)]
    [DataRow(WorkflowNames.DiskFree, RiskLevel.Safe)]
    [DataRow(WorkflowNames.PullerForceUpdate, RiskLevel.High)]
    [DataRow("machine.docker.ps", RiskLevel.Low)]
    [DataRow("machine.docker.logs", RiskLevel.Low)]
    [DataRow("machine.docker.exec", RiskLevel.VeryHigh)]
    [DataRow("machine.docker.kill", RiskLevel.High)]
    public async Task MachineCommandsUseRiskGrantsAndApprovedFrozenBatch(string definition, RiskLevel risk)
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        if (risk == RiskLevel.VeryHigh)
        {
            DmsTestPermissions.Risk(db, setup.User, risk, request: true, automatic: false, approve: false);
            DmsTestPermissions.Risk(db, setup.Approver, risk, request: false, automatic: false, approve: true);
            await db.SaveChangesAsync();
        }
        DmsTestPermissions.Set(db, setup.User, DmsPermissions.Request(risk), false); await db.SaveChangesAsync();
        var input = Request(setup.Machine); input.Workflow = definition;
        input.Input = definition switch
        {
            WorkflowNames.ServiceStatus => input.Input,
            WorkflowNames.PullerForceUpdate => MachineWorkflows.PullerForceUpdate.Serialize(new PullerForceUpdateInput("aiursoft/dms:latest")),
            "machine.docker.ps" => MachineWorkflows.DockerPs.Serialize(new DockerPsInput(GrepName: "worker.1")),
            "machine.docker.logs" => MachineWorkflows.DockerLogs.Serialize(new DockerLogsInput(new string('a', 64))),
            "machine.docker.exec" => MachineWorkflows.DockerExec.Serialize(new DockerExecInput(new string('a', 64), ["/start.sh"])),
            "machine.docker.kill" => MachineWorkflows.DockerKill.Serialize(new DockerContainerInput(new string('a', 64))),
            _ => JsonSerializer.Serialize(new EmptyInput())
        };
        var denied = await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, input));
        Assert.AreEqual(403, denied.StatusCode);
        DmsTestPermissions.Set(db, setup.User, DmsPermissions.Request(risk)); await db.SaveChangesAsync();
        input.AllMachines = true; input.MachineIds.Clear();
        var item = await service.SubmitAsync(setup.User, input);
        Assert.AreEqual(risk, item.Risk); Assert.AreEqual(DecisionState.Pending, item.Decision);
        Assert.IsNull(item.WorkflowId);
        db.Machines.Add(new Machine { Name = "Late target", KeyHash = "late" });
        await db.SaveChangesAsync();
        await service.DecideAsync(setup.Approver, item.Id, true, "Read only approval");
        var operation = await db.WorkflowRuns.SingleAsync(r => r.Id == item.WorkflowId);
        Assert.AreEqual(WorkflowNames.Batch, operation.Definition);
        await new MachineBatchWorkflow().ExecuteAsync(new ClusterWorkflowContext(db, operation, item, Server!.Services.GetRequiredService<WorkflowCatalog>().Find(item.Definition)));
        var child = db.WorkflowRuns.Local.Single(r => r.ParentId == operation.Id);
        Assert.AreEqual(setup.Machine, child.MachineId); Assert.AreEqual(definition, child.Definition);
    }

    [TestMethod]
    [DataRow(WorkflowNames.Performance, "Processors")]
    [DataRow(WorkflowNames.AgentStatus, "InstallationId")]
    [DataRow(WorkflowNames.ListBlockDevices, "blockdevices")]
    [DataRow(WorkflowNames.DiskFree, "Filesystem")]
    public async Task ActualAgentExecutesApprovedWorkflowAndReturnsTrace(string definition, string expected)
    {
        var setup = await ArrangeAsync(true);
        var key = DmsKeyAuthenticationHandler.CreateKey(true);
        LockboxRequest request;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.Machines.SingleAsync(m => m.Id == setup.Machine)).KeyHash = DmsKeyAuthenticationHandler.Hash(key);
            await db.SaveChangesAsync();
            var input = Request(setup.Machine); input.Workflow = definition; if (definition != WorkflowNames.ServiceStatus) input.Input = JsonSerializer.Serialize(new EmptyInput());
            request = await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User, input);
        }
        var path = Path.Combine(Path.GetTempPath(), "dms-real-agent-" + Guid.NewGuid());
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using var journal = new Agent.TaskJournal(path);
            var agent = new Agent.AgentClient(Http.BaseAddress!, key, journal).RunAsync(stopping.Token);
            try
            {
                WorkflowRun? operation = null;
                while (!stopping.IsCancellationRequested)
                {
                    using var scope = Server!.Services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
                    operation = await db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == request.WorkflowId);
                    if (operation.State.Terminal()) break;
                    await Task.Delay(100, stopping.Token);
                }
                Assert.IsNotNull(operation); Assert.AreEqual(RunState.Succeeded, operation.State);
                using var check = Server!.Services.CreateScope();
                var tree = await check.ServiceProvider.GetRequiredService<WorkflowService>().CallstackAsync(setup.User, request.WorkflowId!.Value);
                var machine = tree.Single(r => r.IsMachineTask);
                Assert.AreEqual(RunState.Succeeded, machine.State);
                if (definition == WorkflowNames.DiskFree)
                    Assert.AreEqual(0, JsonSerializer.Deserialize<ProcessResult>(machine.Result)!.ExitCode);
                else StringAssert.Contains(machine.Result, expected);
                Assert.IsTrue(machine.LastSequence >= 3);
                Assert.IsTrue(Server.Services.GetRequiredService<TraceStore>().PendingCount > 0);
            }
            finally
            {
                await stopping.CancelAsync();
                try { await agent; } catch (OperationCanceledException) { }
            }
        }
        finally { Directory.Delete(path, true); }
    }

}
