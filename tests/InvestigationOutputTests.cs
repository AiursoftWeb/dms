using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Client;
using Aiursoft.DMS.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class InvestigationOutputTests
{
    [TestMethod]
    public void HistoryTableMustNeutralizeControlCharactersInDisplayNames()
    {
        var at = DateTime.UtcNow;
        var fact = new WorkflowAuditEntry(WorkflowAuditKind.LockboxSubmitted, at, at, null, null, null, Guid.NewGuid(),
            null, null, null, "machine.frps.status", "1", "engineer", null, null, RiskLevel.Low,
            "machine.frps.status", "1", DecisionState.Pending, null, null, null);
        var page = new HistoryPage([new HistoryRow(Guid.NewGuid(), 1, fact, "Alice\u001b[2J", null, null)], new HistoryQuery(), false);
        using var output = new StringWriter();
        DmsOutputWriter.Write(output, new DmsResponse { Data = page }, OutputFormat.Table, false, false, history: true, lockboxHistory: true);
        Assert.DoesNotContain("\u001b", output.ToString(), "Untrusted display names must not emit terminal escape sequences.");
    }

    [TestMethod]
    public void InvestigationFormatsRetainIdsHistoricalStatesAndFlatCsvFacts()
    {
        var workflow = Guid.NewGuid(); var at = DateTime.UtcNow;
        var fact = new WorkflowAuditEntry(WorkflowAuditKind.WorkflowStateChanged, at, at, workflow, workflow, null, Guid.NewGuid(),
            WorkflowLocation.Machine, Guid.NewGuid(), Guid.NewGuid(), "internal.process", "1", "engineer", "reviewer", false, RiskLevel.Low,
            "example,\"quoted\"\nrequest", "1", DecisionState.Approved, at, RunState.Running, RunState.NeedsVerification);
        var row = new WorkflowAuditRow(Guid.NewGuid(), 42, fact);
        var result = new DmsResponse { Message = "Request completed.", Data = new WorkflowAuditPage(new WorkflowAuditQuery { From = at.AddHours(-1), Until = at }, [row], true) };
        using var table = new StringWriter(); DmsOutputWriter.Write(table, result, OutputFormat.Table, false, false, audit: true);
        StringAssert.Contains(table.ToString(), workflow.ToString()); StringAssert.Contains(table.ToString(), "NeedsVerification");
        StringAssert.Contains(table.ToString(), "not a current snapshot"); Assert.DoesNotContain("\u001b", table.ToString());
        using var json = new StringWriter(); DmsOutputWriter.Write(json, result, OutputFormat.Json, false, false, audit: true);
        Assert.AreEqual(row.EventId.ToString(), JObject.Parse(json.ToString())["Data"]!["Entries"]![0]!["EventId"]!.Value<string>());
        using var csvOutput = new StringWriter(); DmsOutputWriter.Write(csvOutput, result, OutputFormat.Csv, false, false, audit: true);
        using var csv = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csvOutput.ToString())); csv.SetDelimiters(",");
        var headers = csv.ReadFields()!; var values = csv.ReadFields()!;
        Assert.AreEqual(fact.RequestedDefinition, values[Array.IndexOf(headers, "RequestedDefinition")]);
        Assert.AreEqual(workflow.ToString(), values[Array.IndexOf(headers, "WorkflowId")]);
        Assert.AreEqual("reviewer", values[Array.IndexOf(headers, "ApproverId")]);
        Assert.IsTrue(csv.EndOfData); Assert.DoesNotContain("Archive window", csvOutput.ToString());
        using var empty = new StringWriter();
        DmsOutputWriter.Write(empty, new DmsResponse { Message = "Request completed.", Data = new WorkflowAuditPage(new WorkflowAuditQuery(), [], false) }, OutputFormat.Csv, false, false, audit: true);
        StringAssert.Contains(empty.ToString(), "WorkflowId"); StringAssert.Contains(empty.ToString(), "RequesterId");
    }

    [TestMethod]
    public void HistoryFormatsShowReadableNamesAndScopeColumns()
    {
        var at = DateTime.UtcNow; var lockbox = Guid.NewGuid();
        var approval = new WorkflowAuditEntry(WorkflowAuditKind.LockboxApproved, at, at, null, null, null, lockbox,
            null, null, null, "machine.frps.deploy", "1", "engineer-id", "reviewer-id", false, RiskLevel.Medium,
            "machine.frps.deploy", "1", DecisionState.Approved, at, null, null);
        var page = new HistoryPage([new HistoryRow(Guid.NewGuid(), 7, approval, "Engineer", "Reviewer", "gx10-02")],
            new HistoryQuery { From = at.AddDays(-7), Until = at }, false);
        var result = new DmsResponse { Message = "Request completed.", Data = page };
        using var table = new StringWriter();
        DmsOutputWriter.Write(table, result, OutputFormat.Table, false, false, history: true, lockboxHistory: true);
        var text = table.ToString();
        StringAssert.Contains(text, "Engineer"); StringAssert.Contains(text, "Reviewer"); StringAssert.Contains(text, "gx10-02");
        StringAssert.Contains(text, "LockboxApproved"); StringAssert.Contains(text, "Approved"); StringAssert.Contains(text, lockbox.ToString());
        StringAssert.Contains(text, "Archive window"); Assert.DoesNotContain("\u001b", text);
        Assert.DoesNotContain("engineer-id", text); Assert.DoesNotContain(text, "Origin");
        using var json = new StringWriter(); DmsOutputWriter.Write(json, result, OutputFormat.Json, false, false, history: true);
        Assert.AreEqual("Engineer", JObject.Parse(json.ToString())["Data"]!["Entries"]![0]!["RequesterName"]!.Value<string>());
        using var csvOutput = new StringWriter(); DmsOutputWriter.Write(csvOutput, result, OutputFormat.Csv, false, false, history: true);
        using var csv = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csvOutput.ToString())); csv.SetDelimiters(",");
        var headers = csv.ReadFields()!; var values = csv.ReadFields()!;
        StringAssert.Contains(string.Join(',', headers), "RequesterName"); StringAssert.Contains(string.Join(',', headers), "MachineName");
        StringAssert.Contains(string.Join(',', headers), "ApproverName");
        Assert.AreEqual("Engineer", values[Array.IndexOf(headers, "RequesterName")]);
        Assert.AreEqual("Reviewer", values[Array.IndexOf(headers, "ApproverName")]);
        Assert.AreEqual("gx10-02", values[Array.IndexOf(headers, "MachineName")]);
        Assert.IsTrue(csv.EndOfData);
        using var workflow = new StringWriter();
        var runFact = new WorkflowAuditEntry(WorkflowAuditKind.WorkflowStateChanged, at, at, lockbox, lockbox, null, null,
            WorkflowLocation.Machine, Guid.NewGuid(), null, "machine.disk.free", "1", "engineer-id", null, null, RiskLevel.Safe,
            null, null, null, null, RunState.Running, RunState.Failed, WorkflowOrigin.Automatic);
        var runPage = new DmsResponse { Message = "Request completed.", Data = new HistoryPage([new HistoryRow(Guid.NewGuid(), 9, runFact, "Engineer", null, "gx10-02")], new HistoryQuery(), true) };
        DmsOutputWriter.Write(workflow, runPage, OutputFormat.Table, false, false, 220, history: true);
        StringAssert.Contains(workflow.ToString(), "Failed"); StringAssert.Contains(workflow.ToString(), "Automatic");
        StringAssert.Contains(workflow.ToString(), "machine.disk.free");
        using var empty = new StringWriter();
        DmsOutputWriter.Write(empty, new DmsResponse { Message = "Request completed.", Data = new HistoryPage([], new HistoryQuery(), false) }, OutputFormat.Csv, false, false, history: true);
        StringAssert.Contains(empty.ToString(), "WorkflowId"); StringAssert.Contains(empty.ToString(), "RequesterName");
    }

    [TestMethod]
    public void TraceLogWritesOnePlainLinePerEvent()
    {
        var data = new
        {
            Entries = new[]
            {
                new { Time = DateTime.UnixEpoch, Source = "engine", Message = "Starting operation." },
                new
                {
                    Time = DateTime.UnixEpoch.AddMilliseconds(5), Source = "stdout",
                    Message = "GPU, \"ready\"\r\nnext\n"
                },
                new { Time = DateTime.UnixEpoch.AddSeconds(1), Source = "stderr", Message = "Tool notice." },
                new { Time = DateTime.UnixEpoch.AddSeconds(2), Source = "warning", Message = "Partial output." },
                new { Time = DateTime.UnixEpoch.AddSeconds(3), Source = "error", Message = "Operation failed." }
            },
            PendingArchiveEvents = 3,
            NextOffset = 200
        };
        var response = new DmsResponse { Message = "Request completed.", Data = data };
        using var output = new StringWriter();
        DmsOutputWriter.Write(output, response, OutputFormat.Log, false, true);

        using var lines = new StringReader(output.ToString());
        Assert.AreEqual("1970-01-01T00:00:00.0000000Z [INFO] Starting operation.", lines.ReadLine());
        Assert.AreEqual("1970-01-01T00:00:00.0050000Z [INFO] GPU, \"ready\"\\nnext\\n", lines.ReadLine());
        Assert.AreEqual("1970-01-01T00:00:01.0000000Z [WARN] Tool notice.", lines.ReadLine());
        Assert.AreEqual("1970-01-01T00:00:02.0000000Z [WARN] Partial output.", lines.ReadLine());
        Assert.AreEqual("1970-01-01T00:00:03.0000000Z [ERROR] Operation failed.", lines.ReadLine());
        Assert.IsNull(lines.ReadLine());

        using var empty = new StringWriter();
        response.Data = new { Entries = Array.Empty<object>(), PendingArchiveEvents = 3, NextOffset = 400 };
        DmsOutputWriter.Write(empty, response, OutputFormat.Log, false, true);
        Assert.AreEqual(string.Empty, empty.ToString());
    }

    [TestMethod]
    public async Task EmptyRecursiveTracePointsToStatusWithoutWritingToStdout()
    {
        var id = Guid.NewGuid();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapGet("/api/v1/dms/operations/{workflowId:guid}/trace", (Guid workflowId, bool recursive) =>
        {
            if (workflowId != id || !recursive) return Results.BadRequest();
            var reply = new DmsResponse
            {
                Code = Code.ResultShown, Message = "Request completed.",
                Data = new { Entries = Array.Empty<object>(), PendingArchiveEvents = 3, NextOffset = 200 }
            };
            return Results.Text(JsonConvert.SerializeObject(reply), "application/json");
        });
        await app.StartAsync();

        string[] args = ["workflow", "trace", id.ToString(), "--dms-server-endpoint", app.Urls.Single(), "--api-key", "fixture-key"];
        var log = await DmsCommandApp.Create().TestRunAsync(args);
        Assert.AreEqual(0, log.ProgramReturn, log.StdErr);
        Assert.AreEqual(string.Empty, log.StdOut);
        StringAssert.Contains(log.StdErr, "No archived trace entries on this page.");
        StringAssert.Contains(log.StdErr, "still waiting to be archived");
        StringAssert.Contains(log.StdErr, $"dms workflow status {id}");

        var json = await DmsCommandApp.Create().TestRunAsync([.. args, "--output-format", "json"]);
        Assert.AreEqual(0, json.ProgramReturn, json.StdErr);
        Assert.AreEqual(string.Empty, json.StdErr);
        Assert.AreEqual(0, ((JArray)JObject.Parse(json.StdOut)["Data"]!["Entries"]!).Count);
    }

    [TestMethod]
    public async Task InvestigationCliRequiresAuthenticationInsteadOfAnOperationId()
    {
        var result = await DmsCommandApp.Create().TestRunAsync(["operation", "query-audit"]);
        Assert.AreNotEqual(0, result.ProgramReturn); StringAssert.Contains(result.StdErr, "dms login");
        Assert.DoesNotContain("--id' is required", result.StdErr);
    }
}
