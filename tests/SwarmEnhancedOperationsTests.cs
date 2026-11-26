using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aiursoft.DMS.Client;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class SwarmEnhancedOperationsTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }

    private static async Task<(OperationResult Result, List<WorkflowEvent> Events)> RunAsync<TInput>(
        MachineWorkflowDefinition<TInput, OperationResult> definition,
        TInput input,
        OperationHost host)
    {
        var registry = new MachineWorkflowRegistry();
        var agent = new AgentEnvironment(Guid.NewGuid(), "1.0.0", 0, 0, Operations: host);
        var workflow = registry.Create(definition.Name, definition.Serialize(input), agent);
        List<WorkflowEvent> events = [];
        var context = new MachineWorkflowContext(Guid.NewGuid(), null,
            e => { events.Add(e); return Task.CompletedTask; }, agent, registry);
        var raw = await context.RunAsync(workflow, CancellationToken.None);
        return (definition.ReadResult(raw), events);
    }

    private static string ServiceJson(string id, string name, long version, long forceUpdate, string? updateOrder = "start-first",
        string? updateState = null, string[]? env = null)
    {
        var spec = new JsonObject
        {
            ["Name"] = name,
            ["Mode"] = new JsonObject { ["Replicated"] = new JsonObject { ["Replicas"] = 1 } },
            ["TaskTemplate"] = new JsonObject
            {
                ["ForceUpdate"] = forceUpdate,
                ["ContainerSpec"] = new JsonObject
                {
                    ["Image"] = "hub.aiursoft.com/aiursoft/news:latest",
                    ["Env"] = env != null ? new JsonArray(env.Select(e => JsonValue.Create(e)).ToArray()) : new JsonArray()
                }
            }
        };
        if (updateOrder != null)
            spec["UpdateConfig"] = new JsonObject { ["Order"] = updateOrder };
        var service = new JsonObject
        {
            ["ID"] = id,
            ["Version"] = new JsonObject { ["Index"] = version },
            ["Spec"] = spec
        };
        if (updateState != null)
            service["UpdateStatus"] = new JsonObject { ["State"] = updateState };
        return new JsonArray(service).ToJsonString();
    }

    private static string TasksJson(params (string Id, string Desired, string State, long ForceUpdate, string? ContainerId)[] tasks)
    {
        var arr = new JsonArray();
        foreach (var t in tasks)
        {
            var status = new JsonObject { ["State"] = t.State };
            if (t.ContainerId != null)
                status["ContainerStatus"] = new JsonObject { ["ContainerID"] = t.ContainerId };
            arr.Add(new JsonObject
            {
                ["ID"] = t.Id,
                ["NodeID"] = "node-1",
                ["DesiredState"] = t.Desired,
                ["Status"] = status,
                ["Spec"] = new JsonObject
                {
                    ["ForceUpdate"] = t.ForceUpdate,
                    ["ContainerSpec"] = new JsonObject { ["Image"] = "hub.aiursoft.com/aiursoft/news:latest" }
                }
            });
        }
        return arr.ToJsonString();
    }

    [TestMethod]
    public async Task RollingRestartGuardRefusesStopFirstWithoutAllowDowntime()
    {
        var posted = false;
        var host = new OperationHost(
            native: (_, _) => Task.FromResult(new NativeResult(0, ServiceJson("svc1", "news_web", 10, 0, updateOrder: "stop-first"), "")),
            dockerHttp: () => new HttpClient(new StubHandler(req =>
            {
                if (req.Method == HttpMethod.Post) posted = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(TasksJson(("old-task", "running", "running", 0, null)), Encoding.UTF8, "application/json")
                });
            })) { BaseAddress = new Uri("http://docker/") });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            RunAsync(OperationsWorkflows.SwarmServiceRollingRestart, new SwarmRollingRestartInput("news_web"), host));
        StringAssert.Contains(ex.Message, "stop-first");
        StringAssert.Contains(ex.Message, "--allow-downtime");
        Assert.IsFalse(posted);
    }

    [TestMethod]
    public async Task RollingRestartIncrementsForceUpdateAndWaitsForReplacementTask()
    {
        Assert.AreEqual(RiskLevel.Medium, OperationsWorkflows.SwarmServiceRollingRestart.Risk);
        var inspectCount = 0;
        long? postedForceUpdate = null;
        string? postedUri = null;
        var pollCount = 0;

        var host = new OperationHost(
            native: (_, _) =>
            {
                inspectCount++;
                // First inspect before POST: version=10, ForceUpdate=2; after POST: version=11, ForceUpdate=3
                var json = inspectCount == 1
                    ? ServiceJson("svc1", "news_web", 10, 2, updateOrder: "start-first")
                    : ServiceJson("svc1", "news_web", 11, 3, updateOrder: "start-first", updateState: pollCount == 0 ? "updating" : "completed");
                return Task.FromResult(new NativeResult(0, json, ""));
            },
            dockerHttp: () => new HttpClient(new StubHandler(async req =>
            {
                if (req.Method == HttpMethod.Post)
                {
                    postedUri = req.RequestUri!.PathAndQuery;
                    var body = JsonNode.Parse(await req.Content!.ReadAsStringAsync())!;
                    postedForceUpdate = body["TaskTemplate"]!["ForceUpdate"]!.GetValue<long>();
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                }
                if (req.RequestUri!.AbsolutePath.EndsWith("/tasks", StringComparison.Ordinal))
                {
                    string tasks;
                    if (postedForceUpdate == null)
                    {
                        tasks = TasksJson(("old-task-1", "running", "running", 2, "c1"));
                    }
                    else if (pollCount++ == 0)
                    {
                        // Still same old task running with ForceUpdate=2 -> MUST NOT report converged!
                        tasks = TasksJson(("old-task-1", "running", "running", 2, "c1"));
                    }
                    else
                    {
                        tasks = TasksJson(
                            ("old-task-1", "shutdown", "shutdown", 2, "c1"),
                            ("new-task-2", "running", "running", 3, "c2"));
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(tasks, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            })) { BaseAddress = new Uri("http://docker/") });

        var (result, _) = await RunAsync(OperationsWorkflows.SwarmServiceRollingRestart,
            new SwarmRollingRestartInput("news_web", AllowDowntime: false, TimeoutSeconds: 30), host);

        Assert.AreEqual("/services/svc1/update?version=10", postedUri);
        Assert.AreEqual(3L, postedForceUpdate);
        Assert.IsTrue(result.Complete);
        Assert.AreEqual(2, pollCount);
        Assert.AreEqual(3L, result.Facts!.Value.GetProperty("TargetForceUpdate").GetInt64());
    }

    [TestMethod]
    public async Task RollingRestartNeedsVerificationIfSwarmRollsBack()
    {
        var inspectCount = 0;
        var host = new OperationHost(
            native: (_, _) =>
            {
                inspectCount++;
                var json = inspectCount == 1
                    ? ServiceJson("svc1", "news_web", 10, 0, updateOrder: "stop-first")
                    : ServiceJson("svc1", "news_web", 11, 0, updateOrder: "stop-first", updateState: "rollback_completed");
                return Task.FromResult(new NativeResult(0, json, ""));
            },
            dockerHttp: () => new HttpClient(new StubHandler(req =>
            {
                if (req.Method == HttpMethod.Post)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(TasksJson(("old-1", "running", "running", 0, "c1")), Encoding.UTF8, "application/json")
                });
            })) { BaseAddress = new Uri("http://docker/") });

        await Assert.ThrowsAsync<WorkflowNeedsVerificationException>(() =>
            RunAsync(OperationsWorkflows.SwarmServiceRollingRestart,
                new SwarmRollingRestartInput("news_web", AllowDowntime: true, TimeoutSeconds: 30), host));
    }

    [TestMethod]
    public async Task EnvSummaryRedactsSensitiveKeysAndCredentialValuesWithoutLeakingSecrets()
    {
        Assert.AreEqual(RiskLevel.Low, OperationsWorkflows.SwarmServiceEnvSummary.Risk);
        const string secretPassword = "SuperSecretDbPassword123!";
        const string secretToken = "glpat-AbCdEfGhIjKlMnOpQrSt";
        const string secretConnStr = "Server=db;Database=app;Uid=root;Password=InlineConnSecret99;";
        const string secretUri = "https://oauth2:mysecretpass@gitlab.aiursoft.com/aiursoft/repo.git";

        var specEnv = new[]
        {
            "ASPNETCORE_ENVIRONMENT=Production",
            "PrBot__LocalizationTargetLanguages__27=hr-HR",
            $"MYSQL_ROOT_PASSWORD={secretPassword}",
            $"CUSTOM_ENDPOINT={secretUri}",
            $"APP_DSN={secretConnStr}"
        };
        var runtimeEnv = new[]
        {
            "ASPNETCORE_ENVIRONMENT=Production",
            $"GITLAB_PAT_VALUE={secretToken}"
        };

        var host = new OperationHost(
            native: (_, _) => Task.FromResult(new NativeResult(0, ServiceJson("svc1", "prbot_app", 5, 0, env: specEnv), "")),
            dockerHttp: () => new HttpClient(new StubHandler(req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("/tasks", StringComparison.Ordinal))
                {
                    var tasks = TasksJson(
                        ("t-local", "running", "running", 0, "local-container-1"),
                        ("t-remote", "running", "running", 0, "remote-container-2"));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(tasks, Encoding.UTF8, "application/json")
                    });
                }
                if (req.RequestUri.AbsolutePath.Contains("local-container-1", StringComparison.Ordinal))
                {
                    var body = new JsonObject
                    {
                        ["Config"] = new JsonObject
                        {
                            ["Env"] = new JsonArray(runtimeEnv.Select(e => JsonValue.Create(e)).ToArray())
                        }
                    }.ToJsonString();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json")
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            })) { BaseAddress = new Uri("http://docker/") });

        var (result, events) = await RunAsync(OperationsWorkflows.SwarmServiceEnvSummary, new SwarmServiceInput("prbot_app"), host);
        var fullTrace = string.Join('\n', events.Select(e => e.Message));
        var fullFacts = result.Facts!.Value.GetRawText();

        foreach (var secret in new[] { secretPassword, secretToken, "InlineConnSecret99", "mysecretpass" })
        {
            Assert.DoesNotContain(secret, fullTrace);
            Assert.DoesNotContain(secret, fullFacts);
        }

        StringAssert.Contains(fullFacts, $"[redacted len={secretPassword.Length}]");
        StringAssert.Contains(fullFacts, $"[redacted len={secretToken.Length}]");
        StringAssert.Contains(fullFacts, "hr-HR");
        StringAssert.Contains(fullFacts, "\"LocalContainerInspected\":true");
        StringAssert.Contains(fullFacts, "\"LocalContainerInspected\":false");
    }

    [TestMethod]
    public async Task SwarmCliHelpExposesRollingRestartAndEnvSummary()
    {
        var cli = DmsCommandApp.Create();
        var restartHelp = await cli.TestRunAsync(["machine", "swarm", "service", "rolling-restart", "--help"]);
        Assert.AreEqual(0, restartHelp.ProgramReturn, restartHelp.StdErr);
        StringAssert.Contains(restartHelp.StdOut, "--allow-downtime");
        StringAssert.Contains(restartHelp.StdOut, "--timeout-seconds");

        var envHelp = await cli.TestRunAsync(["machine", "swarm", "service", "env-summary", "--help"]);
        Assert.AreEqual(0, envHelp.ProgramReturn, envHelp.StdErr);
        StringAssert.Contains(envHelp.StdOut, "redacted");
    }
}
