using System.Text;
using System.Text.Json;
using Aiursoft.DMS.Client;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Workflows.Operations;
using Microsoft.AspNetCore.DataProtection;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class DatabaseEnhancedOperationsTests
{
    private sealed class StubDatabaseSecretTransport(Dictionary<string, string> secrets) : IDatabaseSecretTransport
    {
        public Guid? RequestedWorkflowId { get; private set; }

        public Task<Dictionary<string, string>> TakeUpsertSecretsAsync(Guid workflowId, CancellationToken token)
        {
            RequestedWorkflowId = workflowId;
            return Task.FromResult(secrets);
        }
    }

    private static async Task<(OperationResult Result, List<WorkflowEvent> Events)> RunAsync<TInput>(
        MachineWorkflowDefinition<TInput, OperationResult> definition,
        TInput input,
        OperationHost host,
        IDatabaseSecretTransport? secretTransport = null)
    {
        var registry = new MachineWorkflowRegistry();
        var agent = new AgentEnvironment(
            Guid.NewGuid(),
            "1.0.0",
            0,
            0,
            Operations: host,
            DatabaseSecrets: secretTransport);
        var workflow = registry.Create(definition.Name, definition.Serialize(input), agent);
        List<WorkflowEvent> events = [];
        var context = new MachineWorkflowContext(
            Guid.NewGuid(),
            null,
            e =>
            {
                events.Add(e);
                return Task.CompletedTask;
            },
            agent,
            registry);
        var raw = await context.RunAsync(workflow, CancellationToken.None);
        return (definition.ReadResult(raw), events);
    }

    [TestMethod]
    public void DatabaseTargetValidationRequiresExactlyOneOfConnectionOrContainer()
    {
        var validContainer = new string('a', 64);

        Assert.Throws<ArgumentException>(() => new DatabaseInput(null, null).Validate());
        Assert.Throws<ArgumentException>(() => new DatabaseInput("prod", validContainer).Validate());
        Assert.Throws<ArgumentException>(() => new DatabaseInput(null, "short-id").Validate());

        new DatabaseInput("prod").Validate();
        new DatabaseInput(null, validContainer).Validate();
        new DatabaseNameInput(null, "appdb", validContainer).Validate();
        new DatabaseTableInput(null, "appdb", "users", validContainer).Validate();
        new DatabaseQueryInput(null, "appdb", "users", ["id"], Container: validContainer).Validate();

        var artifact = new ArtifactReference(Guid.NewGuid(), new string('b', 64), 32, "file", "q.sql");
        new DatabaseSqlInput(null, artifact, Container: validContainer, Database: "appdb").Validate();
        Assert.Throws<ArgumentException>(() =>
            new DatabaseSqlInput(null, artifact, Container: validContainer, Database: "bad;db").Validate());
    }

    [TestMethod]
    public async Task DiscoverReturnsStructuredSwarmMetadataAndNeverLeaksPasswords()
    {
        var mysqlId = new string('1', 64);
        var pgId = new string('2', 64);
        var inspectJson = JsonSerializer.Serialize(new object[]
        {
            new
            {
                Id = mysqlId,
                Name = "/newblogs_db.1.abc",
                State = new { Running = true },
                Config = new
                {
                    Image = "mariadb:11.4",
                    Labels = new Dictionary<string, string>
                    {
                        ["com.docker.swarm.service.name"] = "newblogs_db",
                        ["com.docker.stack.namespace"] = "newblogs"
                    },
                    Env = new[]
                    {
                        "MARIADB_DATABASE=blogs",
                        "MARIADB_ROOT_PASSWORD=super-secret-mariadb-pw"
                    }
                }
            },
            new
            {
                Id = pgId,
                Name = "/analytics_pg.1.xyz",
                State = new { Running = true },
                Config = new
                {
                    Image = "postgres:16-alpine",
                    Labels = new Dictionary<string, string>
                    {
                        ["com.docker.swarm.service.name"] = "analytics_pg",
                        ["com.docker.stack.namespace"] = "analytics"
                    },
                    Env = new[]
                    {
                        "POSTGRES_DB=metrics",
                        "POSTGRES_PASSWORD=super-secret-pg-pw"
                    }
                }
            }
        });

        var tempRoot = Directory.CreateTempSubdirectory("dms-db-discover-").FullName;
        try
        {
            var host = new OperationHost(root: tempRoot, native: (cmd, _) =>
            {
                if (cmd.Arguments.Contains("ps"))
                    return Task.FromResult(new NativeResult(0,
                        $"{mysqlId}\tmariadb:11.4\tnewblogs_db.1.abc\n{pgId}\tpostgres:16-alpine\tanalytics_pg.1.xyz\n{new string('3', 64)}\tnginx:alpine\tweb\n",
                        ""));
                if (cmd.Arguments.Contains("inspect"))
                    return Task.FromResult(new NativeResult(0, inspectJson, ""));
                throw new InvalidOperationException("Unexpected command: " + string.Join(' ', cmd.Arguments));
            });

            var (result, events) = await RunAsync(OperationsWorkflows.DatabaseDiscover, new EmptyInput(), host);
            Assert.IsTrue(result.Complete);

            var factsJson = result.Facts!.Value.GetRawText();
            var allTraces = string.Join('\n', events.Select(e => e.Message ?? ""));
            Assert.IsFalse(factsJson.Contains("super-secret-mariadb-pw", StringComparison.Ordinal));
            Assert.IsFalse(factsJson.Contains("super-secret-pg-pw", StringComparison.Ordinal));
            Assert.IsFalse(allTraces.Contains("super-secret-mariadb-pw", StringComparison.Ordinal));
            Assert.IsFalse(allTraces.Contains("super-secret-pg-pw", StringComparison.Ordinal));

            var containers = result.Facts.Value.GetProperty("Containers");
            Assert.AreEqual(2, containers.GetArrayLength());
            Assert.AreEqual("newblogs_db", containers[0].GetProperty("SwarmService").GetString());
            Assert.AreEqual("newblogs", containers[0].GetProperty("SwarmStack").GetString());
            Assert.AreEqual("blogs", containers[0].GetProperty("DefaultDatabase").GetString());
            Assert.AreEqual("mysql", containers[0].GetProperty("Engine").GetString());
            Assert.IsTrue(containers[0].GetProperty("HasEnvCredentials").GetBoolean());

            Assert.AreEqual("postgresql", containers[1].GetProperty("Engine").GetString());
            Assert.AreEqual("metrics", containers[1].GetProperty("DefaultDatabase").GetString());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ContainerTargetUsesMariaDbStatementTimeAndPostgresReadOnlyWithoutHostSecrets()
    {
        var mariaId = new string('4', 64);
        var pgId = new string('5', 64);
        var nginxId = new string('6', 64);
        List<NativeCommand> executed = [];

        var host = new OperationHost(native: (cmd, _) =>
        {
            executed.Add(cmd);
            if (cmd.Arguments.Contains("inspect"))
            {
                var targetId = cmd.Arguments[^1];
                var image = targetId == mariaId ? "mariadb:11.4" : targetId == pgId ? "postgres:16" : "nginx:latest";
                var env = targetId == mariaId
                    ? new[] { "MARIADB_DATABASE=blogs", "MARIADB_ROOT_PASSWORD=never-on-host" }
                    : new[] { "POSTGRES_DB=app", "POSTGRES_PASSWORD=never-on-host" };
                var json = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Id = targetId,
                        Name = "/db_container",
                        State = new { Running = true },
                        Config = new { Image = image, Env = env }
                    }
                });
                return Task.FromResult(new NativeResult(0, json, ""));
            }

            if (cmd.Arguments.Contains("exec"))
                return Task.FromResult(new NativeResult(0, "DatabaseName\nblogs\n", ""));

            throw new InvalidOperationException("Unexpected docker command.");
        });

        var (mariaRes, _) = await RunAsync(
            OperationsWorkflows.DatabaseListDatabases,
            new DatabaseInput(null, mariaId),
            host);
        Assert.IsTrue(mariaRes.Complete);
        var mariaExec = executed.Single(c => c.Arguments.Contains("exec"));
        StringAssert.Contains(mariaExec.StandardInput!, "SET SESSION max_statement_time=30;");
        StringAssert.Contains(mariaExec.StandardInput!, "START TRANSACTION READ ONLY;");
        Assert.IsNull(mariaExec.Environment);
        Assert.IsFalse(string.Join(' ', mariaExec.Arguments).Contains("never-on-host", StringComparison.Ordinal));

        executed.Clear();
        var (pgRes, _) = await RunAsync(
            OperationsWorkflows.DatabaseListDatabases,
            new DatabaseInput(null, pgId),
            host);
        Assert.IsTrue(pgRes.Complete);
        var pgExec = executed.Single(c => c.Arguments.Contains("exec"));
        StringAssert.Contains(pgExec.StandardInput!, "\\gexec");
        var decodedHex = Encoding.UTF8.GetString(Convert.FromHexString(
            pgExec.StandardInput!.Split('\'')[1]));
        StringAssert.Contains(decodedHex, "BEGIN READ ONLY; SET LOCAL statement_timeout=30000;");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            RunAsync(OperationsWorkflows.DatabaseListDatabases, new DatabaseInput(null, nginxId), host));
    }

    [TestMethod]
    public void BuildUpsertSqlGeneratesInjectionSafeSqlForMysqlAndPostgres()
    {
        var values = new Dictionary<string, JsonElement>
        {
            ["SettingKey"] = JsonSerializer.SerializeToElement("Translation.Provider"),
            ["Enabled"] = JsonSerializer.SerializeToElement(true),
            ["RetryCount"] = JsonSerializer.SerializeToElement(3)
        };
        var input = new DatabaseUpsertInput(
            "prod",
            "appdb",
            "Settings",
            ["SettingKey"],
            values,
            SecretColumns: ["ApiKey"]);

        var injectionSecret = "sk-live'; DROP TABLE Settings; --";
        var mysqlSql = DatabaseOperations.BuildUpsertSql("mysql", input, new Dictionary<string, string>
        {
            ["ApiKey"] = injectionSecret
        });
        StringAssert.StartsWith(mysqlSql, "INSERT INTO `appdb`.`Settings` (`Enabled`,`RetryCount`,`SettingKey`,`ApiKey`) VALUES ");
        StringAssert.Contains(mysqlSql, "ON DUPLICATE KEY UPDATE `Enabled`=TRUE, `RetryCount`=3, `ApiKey`=CONVERT(0x");
        Assert.IsFalse(mysqlSql.Contains("DROP TABLE", StringComparison.Ordinal));
        StringAssert.Contains(mysqlSql, Convert.ToHexString(Encoding.UTF8.GetBytes(injectionSecret)));

        var pgSql = DatabaseOperations.BuildUpsertSql("postgresql", input, new Dictionary<string, string>
        {
            ["ApiKey"] = injectionSecret
        });
        StringAssert.StartsWith(pgSql, "INSERT INTO public.\"Settings\" (\"Enabled\",\"RetryCount\",\"SettingKey\",\"ApiKey\") VALUES ");
        StringAssert.Contains(pgSql, "ON CONFLICT (\"SettingKey\") DO UPDATE SET \"Enabled\"=EXCLUDED.\"Enabled\", \"RetryCount\"=EXCLUDED.\"RetryCount\", \"ApiKey\"=EXCLUDED.\"ApiKey\";");
        Assert.IsFalse(pgSql.Contains("DROP TABLE", StringComparison.Ordinal));

        // Duplicate column across Values and SecretColumns must fail validation.
        var badOverlap = new DatabaseUpsertInput(
            "prod", "appdb", "Settings", ["SettingKey"],
            new Dictionary<string, JsonElement>
            {
                ["SettingKey"] = JsonSerializer.SerializeToElement("k"),
                ["ApiKey"] = JsonSerializer.SerializeToElement("plain")
            },
            SecretColumns: ["ApiKey"]);
        Assert.Throws<ArgumentException>(() => badOverlap.Validate());
    }

    [TestMethod]
    public async Task UpsertAsyncFetchesSecretsOutOfBandAndNeverLeaksThemToTraceOrFacts()
    {
        Assert.AreEqual(RiskLevel.High, OperationsWorkflows.DatabaseUpsert.Risk);
        Assert.IsTrue(OperationsWorkflows.DatabaseUpsert.SingleMachineOnly);

        var containerId = new string('7', 64);
        const string secretToken = "top-secret-api-token-987654321";
        string? capturedStdin = null;

        var host = new OperationHost(native: (cmd, _) =>
        {
            if (cmd.Arguments.Contains("inspect"))
            {
                var json = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Id = containerId,
                        Name = "/moonglade_db.1.abc",
                        State = new { Running = true },
                        Config = new
                        {
                            Image = "mysql:8.0",
                            Env = new[] { "MYSQL_DATABASE=moonglade", "MYSQL_ROOT_PASSWORD=rootpw" }
                        }
                    }
                });
                return Task.FromResult(new NativeResult(0, json, ""));
            }

            if (cmd.Arguments.Contains("exec"))
            {
                capturedStdin = cmd.StandardInput;
                // Simulate a server response echoing the secret to ensure scrubbing works.
                return Task.FromResult(new NativeResult(0, $"status\nok:{secretToken}\n", ""));
            }

            throw new InvalidOperationException("Unexpected docker command.");
        });

        var transport = new StubDatabaseSecretTransport(new Dictionary<string, string>
        {
            ["ApiKey"] = secretToken
        });
        var input = new DatabaseUpsertInput(
            null,
            "moonglade",
            "BlogConfiguration",
            ["CfgKey"],
            new Dictionary<string, JsonElement>
            {
                ["CfgKey"] = JsonSerializer.SerializeToElement("AiTranslator"),
                ["Enabled"] = JsonSerializer.SerializeToElement(true)
            },
            SecretColumns: ["ApiKey"],
            Container: containerId);

        var (result, events) = await RunAsync(OperationsWorkflows.DatabaseUpsert, input, host, transport);
        Assert.IsTrue(result.Complete);
        Assert.IsNotNull(transport.RequestedWorkflowId);
        Assert.IsNotNull(capturedStdin);
        StringAssert.Contains(capturedStdin, Convert.ToHexString(Encoding.UTF8.GetBytes(secretToken)));

        var serializedResult = JsonSerializer.Serialize(result);
        Assert.IsFalse(serializedResult.Contains(secretToken, StringComparison.Ordinal));
        foreach (var ev in events)
        {
            Assert.IsFalse((ev.Message ?? "").Contains(secretToken, StringComparison.Ordinal));
            Assert.IsFalse((ev.Result ?? "").Contains(secretToken, StringComparison.Ordinal));
        }

        var secretCols = result.Facts!.Value.GetProperty("SecretColumns").EnumerateArray().Select(e => e.GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "ApiKey" }, secretCols);
    }

    [TestMethod]
    public void DatabaseSecretTransfersIsSingleUseAndBlocksDuplicateStaging()
    {
        using var store = new DatabaseSecretTransfers(new EphemeralDataProtectionProvider());
        var reqId = Guid.NewGuid();
        var secrets = new Dictionary<string, string> { ["ApiKey"] = "secret-value-1" };

        Assert.IsTrue(store.Stage("alice", reqId, secrets));
        Assert.IsFalse(store.Stage("alice", reqId, secrets));

        // Wrong requester cannot take it.
        Assert.IsNull(store.Take("bob", reqId));

        var taken = store.Take("alice", reqId);
        Assert.IsNotNull(taken);
        Assert.AreEqual("secret-value-1", taken["ApiKey"]);

        // Single-use: second take returns null.
        Assert.IsNull(store.Take("alice", reqId));
    }

    [TestMethod]
    public async Task DatabaseCliHelpExposesContainerFlagAndUpsertCommand()
    {
        var cli = DmsCommandApp.Create();

        var groupHelp = await cli.TestRunAsync(["machine", "database", "--help"]);
        Assert.AreEqual(0, groupHelp.ProgramReturn, groupHelp.StdErr);
        StringAssert.Contains(groupHelp.StdOut, "upsert");

        var listHelp = await cli.TestRunAsync(["machine", "database", "list-databases", "--help"]);
        Assert.AreEqual(0, listHelp.ProgramReturn, listHelp.StdErr);
        StringAssert.Contains(listHelp.StdOut, "--container");
        StringAssert.Contains(listHelp.StdOut, "--connection");

        var querySqlHelp = await cli.TestRunAsync(["machine", "database", "query-sql", "--help"]);
        Assert.AreEqual(0, querySqlHelp.ProgramReturn, querySqlHelp.StdErr);
        StringAssert.Contains(querySqlHelp.StdOut, "--container");
        StringAssert.Contains(querySqlHelp.StdOut, "--database");

        var upsertHelp = await cli.TestRunAsync(["machine", "database", "upsert", "--help"]);
        Assert.AreEqual(0, upsertHelp.ProgramReturn, upsertHelp.StdErr);
        StringAssert.Contains(upsertHelp.StdOut, "--match");
        StringAssert.Contains(upsertHelp.StdOut, "--set");
        StringAssert.Contains(upsertHelp.StdOut, "--values-json");
        StringAssert.Contains(upsertHelp.StdOut, "--secret-column");
        StringAssert.Contains(upsertHelp.StdOut, "--secret-stdin");

        var parsed = DmsCommandApp.ParseDatabaseUpsertValues(["Key=hello=world"], "{\"Count\":42,\"Flag\":true}");
        Assert.AreEqual("hello=world", parsed["Key"].GetString());
        Assert.AreEqual(42, parsed["Count"].GetInt32());
        Assert.IsTrue(parsed["Flag"].GetBoolean());
        Assert.Throws<ArgumentException>(() =>
            DmsCommandApp.ParseDatabaseUpsertValues(["Count=1"], "{\"Count\":2}"));
    }
}
