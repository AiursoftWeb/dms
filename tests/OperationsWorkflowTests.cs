using Aiursoft.DMS.Workflows.Operations;
using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class OperationsWorkflowTests
{
    private static AgentEnvironment EnvironmentFor(OperationHost? host = null, IAgentArtifactTransport? artifacts = null) => new(Guid.NewGuid(), "test", 0, 0, Artifacts: artifacts, Operations: host);
    private static async Task<(OperationResult Result, WorkflowEvent[] Events)> RunAsync<T>(MachineWorkflowDefinition<T, OperationResult> definition, T input, AgentEnvironment? environment = null)
    {
        var events = new ConcurrentQueue<WorkflowEvent>(); var registry = new MachineWorkflowRegistry();
        var agent = environment ?? EnvironmentFor();
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, e => { events.Enqueue(e); return Task.CompletedTask; }, agent, registry);
        var value = await context.RunAsync(registry.Create(definition.Name, definition.Serialize(input), agent), CancellationToken.None);
        return (definition.ReadResult(value), events.ToArray());
    }
    private static string Trace(IEnumerable<WorkflowEvent> events) => string.Join("", events.Where(e => e.IsTrace).Select(e => e.Message));

    [TestMethod]
    public async Task FailedInodeQueryRetainsReadableRowsAndExplainsMountFailures()
    {
        const string output = "Filesystem Inodes IUsed IFree IUse% Mounted on\n/dev/root 100 47 53 47% /\n";
        const string error = "df: /root/.gvfs: Transport endpoint is not connected\n";
        var host = new OperationHost(native: (_, _) => Task.FromResult(new NativeResult(1, output, error)));
        var events = new ConcurrentQueue<WorkflowEvent>(); var agent = EnvironmentFor(host);
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, e => { events.Enqueue(e); return Task.CompletedTask; }, agent);
        var registry = new MachineWorkflowRegistry();
        await Assert.ThrowsAsync<IOException>(() => context.RunAsync(registry.Create(OperationsWorkflows.LayoutInodes.Name, "{}", agent), CancellationToken.None));
        var trace = Trace(events);
        StringAssert.Contains(trace, output); StringAssert.Contains(trace, error);
        StringAssert.Contains(trace, "exit_code=1; elapsed_ms=");
        StringAssert.Contains(trace, "df reports overall failure when any inspected mount cannot be read");
        StringAssert.Contains(trace, "partial evidence");
        Assert.AreEqual(RunState.Failed, events.Last(e => !e.IsTrace).State);
    }

    [TestMethod]
    public async Task NativeTimeoutExplainsTheLimitWithoutClaimingBufferedOutputWasRetained()
    {
        var events = new ConcurrentQueue<WorkflowEvent>(); var agent = EnvironmentFor();
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, e => { events.Enqueue(e); return Task.CompletedTask; }, agent);
        var workflow = new OperationWorkflow<EmptyInput>(OperationsWorkflows.LayoutInodes, new EmptyInput(), async (c, _, environment, token) =>
        {
            await OperationOutput.CommandAsync(c, environment, "sleep", ["5"], token, seconds: 1);
            return new OperationResult("Unexpected completion.");
        }, agent);
        await Assert.ThrowsAsync<OperationCanceledException>(() => context.RunAsync(workflow, CancellationToken.None));
        var trace = Trace(events);
        StringAssert.Contains(trace, "its 1-second native process limit was reached");
        StringAssert.Contains(trace, "Buffered native output may be unavailable");
        Assert.AreEqual(RunState.Failed, events.Last(e => !e.IsTrace).State);
    }

    [TestMethod]
    public async Task CompletionSummaryDoesNotExposeHiddenOutputArgumentsInputOrEnvironment()
    {
        const string secret = "diagnostics-must-not-expose-this";
        var host = new OperationHost(native: (_, _) => Task.FromResult(new NativeResult(0, secret, secret)));
        var events = new ConcurrentQueue<WorkflowEvent>(); var agent = EnvironmentFor(host);
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, e => { events.Enqueue(e); return Task.CompletedTask; }, agent);
        var workflow = new OperationWorkflow<EmptyInput>(OperationsWorkflows.LayoutInodes, new EmptyInput(), async (c, _, environment, token) =>
        {
            await OperationOutput.CommandAsync(c, environment, "fixture", [secret], token, stdin: secret,
                variables: new Dictionary<string, string> { ["PASSWORD"] = secret }, traceOutput: false);
            return new OperationResult("A safe summary of the collected evidence.");
        }, agent);
        await context.RunAsync(workflow, CancellationToken.None);
        var trace = Trace(events);
        StringAssert.Contains(trace, "Starting operation " + OperationsWorkflows.LayoutInodes.Name);
        StringAssert.Contains(trace, "Operation completed: A safe summary of the collected evidence.");
        StringAssert.Contains(trace, "exit_code=0; elapsed_ms=");
        StringAssert.Contains(trace, "raw_output_traced=False");
        Assert.DoesNotContain(secret, trace);
        Assert.AreEqual(RunState.Succeeded, events.Last(e => !e.IsTrace).State);
    }

    [TestMethod]
    public async Task FilesystemCommandsOperateOnLiteralPathsAndDoNotExpandShellText()
    {
        var directory = Directory.CreateTempSubdirectory("dms-ops-files-").FullName;
        try
        {
            var source = Path.Combine(directory, "source;$(touch nope)");
            await File.WriteAllTextAsync(source, "first\nsecond\nthird\n");
            var list = await RunAsync(OperationsWorkflows.FilesystemLs, new ListPathInput(directory));
            StringAssert.Contains(Trace(list.Events), source);
            var stat = await RunAsync(OperationsWorkflows.FilesystemStat, new PathInput(source));
            StringAssert.Contains(Trace(stat.Events), "Bytes");
            var cat = await RunAsync(OperationsWorkflows.FilesystemCat, new TextFileInput(source));
            StringAssert.Contains(Trace(cat.Events), "first\nsecond\nthird");
            var tail = await RunAsync(OperationsWorkflows.FilesystemTail, new TextFileInput(source, Lines: 1));
            StringAssert.Contains(Trace(tail.Events), "third"); Assert.DoesNotContain("first", Trace(tail.Events));
            var find = await RunAsync(OperationsWorkflows.FilesystemFind, new FindFilesInput(directory, Name: "source*"));
            StringAssert.Contains(Trace(find.Events), source);
            var targetDirectory = Path.Combine(directory, "created");
            await RunAsync(OperationsWorkflows.FilesystemMkdir, new MakeDirectoryInput(targetDirectory));
            Assert.IsTrue(Directory.Exists(targetDirectory));
            var copied = Path.Combine(targetDirectory, "copy");
            await RunAsync(OperationsWorkflows.FilesystemCp, new CopyPathInput(source, copied));
            Assert.AreEqual(await File.ReadAllTextAsync(source), await File.ReadAllTextAsync(copied));
            var moved = Path.Combine(targetDirectory, "moved");
            await RunAsync(OperationsWorkflows.FilesystemMv, new CopyPathInput(copied, moved));
            Assert.IsFalse(File.Exists(copied)); Assert.IsTrue(File.Exists(moved));
            Assert.IsFalse(File.Exists(Path.Combine(directory, "nope")));
            await Assert.ThrowsAsync<IOException>(() => RunAsync(OperationsWorkflows.FilesystemCp, new CopyPathInput(source, moved)));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task ExportImportVerifiesBytesAndSafelyRoundTripsADirectory()
    {
        var directory = Directory.CreateTempSubdirectory("dms-ops-transfer-").FullName;
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(directory, "source")).FullName;
            Directory.CreateDirectory(Path.Combine(source, "empty"));
            await File.WriteAllTextAsync(Path.Combine(source, "a file"), "artifact payload");
            var transport = new MemoryArtifacts(); var environment = EnvironmentFor(artifacts: transport);
            var exported = await RunAsync(OperationsWorkflows.FilesystemExport, new ExportPathInput(source, true), environment);
            Assert.IsNotNull(exported.Result.Artifact); Assert.AreEqual("directory-tar", exported.Result.Artifact.Kind);
            Assert.DoesNotContain("artifact payload", Trace(exported.Events));
            var target = Path.Combine(directory, "target");
            await RunAsync(OperationsWorkflows.FilesystemImport, new ImportPathInput(exported.Result.Artifact, target), environment);
            Assert.AreEqual("artifact payload", await File.ReadAllTextAsync(Path.Combine(target, "a file")));
            Assert.IsTrue(Directory.Exists(Path.Combine(target, "empty")));
            transport.Corrupt = true;
            var failedTarget = Path.Combine(directory, "corrupt");
            await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(OperationsWorkflows.FilesystemImport, new ImportPathInput(exported.Result.Artifact, failedTarget), environment));
            Assert.IsFalse(Directory.Exists(failedTarget));
        }
        finally { Directory.Delete(directory, true); }
    }
    [TestMethod]
    public async Task ExportRefusesSymlinksAndArchivesCannotEscapeOrCreateLinks()
    {
        var directory = Directory.CreateTempSubdirectory("dms-ops-tar-").FullName;
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(directory, "source")).FullName;
            File.CreateSymbolicLink(Path.Combine(source, "link"), "/etc/passwd");
            await Assert.ThrowsAsync<IOException>(() => RunAsync(OperationsWorkflows.FilesystemExport, new ExportPathInput(source, true), EnvironmentFor(artifacts: new MemoryArtifacts())));
            var archive = Path.Combine(directory, "bad.tar");
            await using (var file = File.Create(archive))
            await using (var writer = new TarWriter(file))
            { await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, "escape") { LinkName = "/etc/passwd" }); }
            var destination = Directory.CreateDirectory(Path.Combine(directory, "destination")).FullName;
            await Assert.ThrowsAsync<InvalidDataException>(() => FilesystemOperations.ExtractDirectoryAsync(archive, destination, 10000, CancellationToken.None));
            Assert.IsEmpty(Directory.GetFileSystemEntries(destination));
        }
        finally { Directory.Delete(directory, true); }
    }
    [TestMethod]
    public async Task FileListingLimitIsAVisibleWarningInsteadOfAnUnqualifiedSuccess()
    {
        var directory = Directory.CreateTempSubdirectory("dms-ops-list-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "a"), "a"); await File.WriteAllTextAsync(Path.Combine(directory, "b"), "b");
            var result = await RunAsync(OperationsWorkflows.FilesystemLs, new ListPathInput(directory, Limit: 1));
            Assert.IsFalse(result.Result.Complete);
            Assert.AreEqual(RunState.Warning, result.Events.Last(e => !e.IsTrace).State);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string ContainerJson(string id, bool running = false, string? swarm = null) => JsonSerializer.Serialize(new[]
    {
        new
        {
            Id = id, Name = "/fixture", Image = "sha256:" + new string('a', 64), RestartCount = 2,
            Config = new { Image = "example:latest", Env = new[] { "PASSWORD=must-never-appear-in-inspect" }, Labels = swarm == null ? new Dictionary<string,string>() : new Dictionary<string,string> { ["com.docker.swarm.service.id"] = swarm } },
            State = new { Status = running ? "running" : "exited", Running = running, ExitCode = running ? 0 : 137, OOMKilled = !running, Error = "", StartedAt = "2026-11-17T00:00:00Z", FinishedAt = "2026-11-17T00:01:00Z" },
            Mounts = Array.Empty<object>()
        }
    });
    [TestMethod]
    public async Task DockerInspectProjectsRuntimeEvidenceWithoutCredentialEnvironment()
    {
        var id = new string('1', 64); var calls = new List<NativeCommand>();
        var host = new OperationHost(native: (command, _) => { calls.Add(command); return Task.FromResult(new NativeResult(0, ContainerJson(id), "")); });
        var result = await RunAsync(OperationsWorkflows.DockerInspect, new DockerContainerInput(id), EnvironmentFor(host));
        var all = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("must-never-appear", all); StringAssert.Contains(Trace(result.Events), "OomKilled");
        CollectionAssert.AreEqual(new[] { "--host", "unix:///var/run/docker.sock", "container", "inspect", id }, calls.Single().Arguments.ToArray());
    }
    [TestMethod]
    public async Task DockerStopRejectsASwarmTaskBeforeAnyMutation()
    {
        var id = new string('2', 64); var calls = new List<NativeCommand>();
        var host = new OperationHost(native: (command, _) => { calls.Add(command); return Task.FromResult(new NativeResult(0, ContainerJson(id, true, "service-id"), "")); });
        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(OperationsWorkflows.DockerStop, new ContainerActionInput(id), EnvironmentFor(host)));
        Assert.HasCount(1, calls); Assert.DoesNotContain("stop", calls[0].Arguments);
    }
    [TestMethod]
    public async Task InterruptedContainerMutationRequiresVerificationAndIsNotRetried()
    {
        var id = new string('3', 64); var mutations = 0;
        var host = new OperationHost(native: (command, _) =>
        {
            if (command.Arguments.Contains("restart")) { mutations++; return Task.FromResult(new NativeResult(1, "", "lost connection")); }
            return Task.FromResult(new NativeResult(0, ContainerJson(id, true), ""));
        });
        await Assert.ThrowsAsync<WorkflowNeedsVerificationException>(() => RunAsync(OperationsWorkflows.DockerRestart, new ContainerActionInput(id), EnvironmentFor(host)));
        Assert.AreEqual(1, mutations);
    }
    [TestMethod]
    public async Task StartupDiagnosisUsesIndependentTypedChildrenAndRetainsEvidence()
    {
        var id = new string('4', 64);
        var host = new OperationHost(native: (command, _) => Task.FromResult(new NativeResult(0, command.Arguments.Contains("inspect") ? ContainerJson(id) : "", "")));
        // Existing DockerLogs uses its compiled ProcessWorkflow. A missing Docker socket creates a caught child failure.
        var result = await RunAsync(OperationsWorkflows.DockerDiagnoseStartup, new DockerContainerInput(id), EnvironmentFor(host));
        var children = result.Events.Where(e => !e.IsTrace && e.State == RunState.Running && e.ParentId != null).ToArray();
        Assert.IsTrue(children.Any(e => e.Definition == OperationsWorkflows.DockerInspect.Name));
        Assert.IsTrue(children.Any(e => e.Definition == OperationsWorkflows.DockerEvents.Name));
        Assert.IsTrue(children.Any(e => e.Definition == MachineWorkflows.DockerLogs.Name));
        Assert.AreEqual(children.Length, children.Select(e => e.WorkflowId).Distinct().Count());
        StringAssert.Contains(Trace(result.Events), "ExitCode");
    }

    [TestMethod]
    [DataRow("SELECT count(*) FROM events WHERE name='DROP TABLE x'")]
    [DataRow("WITH t AS (SELECT 1 AS n) SELECT sum(n) FROM t")]
    [DataRow("SELECT name FROM events WHERE name IN ('a','b') ORDER BY name LIMIT 5;")]
    public void ReadOnlySqlAllowsCommonReadQueries(string sql) => Assert.IsFalse(string.IsNullOrWhiteSpace(ReadOnlySql.Validate(sql)));
    [TestMethod]
    [DataRow("SELECT 1; DROP TABLE events")]
    [DataRow("WITH t AS (DELETE FROM events RETURNING *) SELECT * FROM t")]
    [DataRow("SELECT * FROM url('https://example.test/')")]
    [DataRow("SELECT LOAD_FILE('/etc/passwd')")]
    [DataRow("SELECT * FROM file('/etc/passwd')")]
    [DataRow("SELECT pg_read_file('/etc/passwd')")]
    [DataRow("SELECT 1 SETTINGS readonly=0")]
    [DataRow("SELECT 1 INTO OUTFILE '/tmp/escape'")]
    [DataRow("SELECT /*!50000 SLEEP(5) */ 1")]
    [DataRow("SELECT evil.count(*) FROM events")]
    [DataRow("SELECT 1\n\\! touch /tmp/escape")]
    public void ReadOnlySqlRefusesMutationAndExternalExecutionConstructs(string sql) => Assert.Throws<ArgumentException>(() => ReadOnlySql.Validate(sql));

    [TestMethod]
    [DataRow("SELECT count() FROM logs.events", RiskLevel.Low)]
    [DataRow("-- change investigation\nWITH t AS (SELECT 'DELETE; ALTER' AS verb) SELECT verb FROM t;", RiskLevel.Low)]
    [DataRow("INSERT INTO logs.events (id, message) VALUES (1, 'read; write');", RiskLevel.High)]
    [DataRow("UPDATE logs.events SET message = 'updated' WHERE id = 1", RiskLevel.High)]
    [DataRow("DELETE FROM logs.events WHERE id = 1", RiskLevel.High)]
    [DataRow("ALTER TABLE logs.events UPDATE message = 'updated' WHERE id = 1", RiskLevel.High)]
    [DataRow("ALTER TABLE logs.events ADD COLUMN labels Array(String)", RiskLevel.High)]
    public void ClickHouseSqlClassifiesOneBoundedStatement(string sql, RiskLevel risk) =>
        Assert.AreEqual(risk, ClickHouseSql.Classify(sql));

    [TestMethod]
    [DataRow("SELECT 1; INSERT INTO logs.events VALUES (1)")]
    [DataRow("INSERT INTO logs.events VALUES (1); SELECT 1")]
    [DataRow("SELECT count() FROM remote('example.test', 'logs', 'events')")]
    [DataRow("ALTER TABLE logs.events ON CLUSTER fixture ADD COLUMN n UInt8")]
    [DataRow("ALTER TABLE logs.events DELETE WHERE id = 1, DROP PARTITION '202611'")]
    [DataRow("ALTER TABLE logs.events ADD COLUMN n UInt8, DROP PARTITION '202611'")]
    [DataRow("DROP TABLE logs.events")]
    [DataRow("GRANT SELECT ON logs.events TO example")]
    public void ClickHouseSqlRejectsUnsupportedOrMixedStatements(string sql) =>
        Assert.Throws<ArgumentException>(() => ClickHouseSql.Classify(sql));

    [TestMethod]
    public async Task ClickHouseMetadataFindsUniqueMirroredContainerAndReturnsPrivateRows()
    {
        var id = new string('a', 64);
        var commands = new List<NativeCommand>();
        var host = new OperationHost(native: (command, _) =>
        {
            commands.Add(command);
            return Task.FromResult(commands.Count == 1
                ? new NativeResult(0, $"{id}\tregistry.test/clickhouse/clickhouse-server:latest\tlogs\n", "")
                : new NativeResult(0, "{\"name\":\"log_table\"}\n", ""));
        });
        var (result, events) = await RunAsync(ClickHouseWorkflows.ListTables,
            new ClickHouseDatabaseInput("logs"), EnvironmentFor(host));
        Assert.HasCount(2, commands);
        CollectionAssert.Contains(commands[1].Arguments.ToArray(), id);
        CollectionAssert.Contains(commands[1].Arguments.ToArray(), "clickhouse-client");
        CollectionAssert.Contains(commands[1].Arguments.ToArray(), "--readonly=1");
        Assert.AreEqual("SHOW TABLES FROM `logs`", commands[1].StandardInput);
        Assert.AreEqual("log_table", result.Facts!.Value[0].GetProperty("name").GetString());
        Assert.DoesNotContain("log_table", Trace(events));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public async Task ClickHouseContainerDiscoveryFailsForZeroOrMultipleMatches(int count)
    {
        var commands = new List<NativeCommand>();
        var rows = string.Join('\n', Enumerable.Range(0, count)
            .Select(i => $"{new string((char)('a' + i), 64)}\tclickhouse/clickhouse-server:latest\tfixture-{i}"));
        var host = new OperationHost(native: (command, _) =>
        {
            commands.Add(command);
            return Task.FromResult(new NativeResult(0, rows, ""));
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAsync(ClickHouseWorkflows.ListDatabases, new EmptyInput(), EnvironmentFor(host)));
        StringAssert.Contains(error.Message, count == 0 ? "No running ClickHouse" : "Exactly one is required");
        Assert.HasCount(1, commands);
    }

    [TestMethod]
    public void TypedDatabaseFilterValuesCannotBecomeSqlSyntax()
    {
        var filter = new DatabaseFilter("name", "eq", JsonSerializer.SerializeToElement("x'; DROP TABLE data; --"));
        foreach (var engine in new[] { "mysql", "postgresql" })
        {
            var sql = DatabaseOperations.FilterSql(engine, filter);
            Assert.DoesNotContain("DROP TABLE", sql); Assert.DoesNotContain("x'; DROP", sql);
        }
        Assert.Throws<ArgumentException>(() => DatabaseOperations.Quote("mysql", "a`;DROP TABLE x"));
    }
    [TestMethod]
    public void PostgreSqlResultLimitsPreserveMultilineQuotedValuesAsOneRow()
    {
        var rows = DatabaseOperations.ResultRows("id,name\n1,\"first\nsecond \"\"quoted\"\" line\"\n2,last\n", true);
        Assert.HasCount(3, rows); StringAssert.Contains(rows[1], "first\nsecond");
        StringAssert.Contains(rows[1], "\"\"quoted\"\""); Assert.AreEqual("2,last", rows[2]);
        Assert.Throws<InvalidDataException>(() => DatabaseOperations.ResultRows("id,name\n1,\"unfinished", true));
    }

    [TestMethod]
    public void SwarmChangesRejectMissingTargetsAndIrrelevantParametersBeforeApproval()
    {
        var scope = new SwarmChangeInput("service", 1, ["node"]);
        Assert.Throws<ArgumentException>(() => OperationsWorkflows.SwarmServiceUpdate.Serialize(scope));
        Assert.Throws<ArgumentException>(() => OperationsWorkflows.SwarmServiceStop.Serialize(scope with { Image = "unexpected:image" }));
        Assert.Throws<ArgumentException>(() => OperationsWorkflows.SwarmServiceStart.Serialize(scope with { Replicas = 0 }));
        OperationsWorkflows.SwarmServiceUpdate.Serialize(scope with { Image = "approved:image" });
        OperationsWorkflows.SwarmServiceStart.Serialize(scope with { Replicas = 3 });
    }
    [TestMethod]
    public async Task BoundedCpuDstatAndPressureCollectRealKernelEvidence()
    {
        var sample = new SampleInput(1);
        var cpu = await RunAsync(OperationsWorkflows.PerformanceCpuSample, sample); StringAssert.Contains(Trace(cpu.Events), "BusyPercent");
        var dstat = await RunAsync(OperationsWorkflows.PerformanceDstatSample, sample); StringAssert.Contains(Trace(dstat.Events), "Network"); StringAssert.Contains(Trace(dstat.Events), "Memory");
        var pressure = await RunAsync(OperationsWorkflows.PerformancePressure, new EmptyInput()); StringAssert.Contains(Trace(pressure.Events), "avg10");
        var uptime = await RunAsync(OperationsWorkflows.SystemUptime, new EmptyInput()); StringAssert.Contains(Trace(uptime.Events), "BootId");
    }
    [TestMethod]
    public async Task MissingSecureBootEvidenceRemainsUnknown()
    {
        var directory = Directory.CreateTempSubdirectory("dms-ops-efi-").FullName;
        try
        {
            var result = await RunAsync(OperationsWorkflows.SecuritySecureBoot, new EmptyInput(), EnvironmentFor(new OperationHost(directory)));
            Assert.IsFalse(result.Result.Complete); StringAssert.Contains(Trace(result.Events), "Unknown");
            Assert.AreEqual(RunState.Warning, result.Events.Last(e => !e.IsTrace).State);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SecureBootReportsTheFirmwareFlagRatherThanAssumingEnabled(bool enabled)
    {
        var directory = Directory.CreateTempSubdirectory("dms-secure-boot-").FullName;
        try
        {
            var variables = Directory.CreateDirectory(Path.Combine(directory, "sys/firmware/efi/efivars")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(variables, "SecureBoot-fixture"), [7, 0, 0, 0, enabled ? (byte)1 : (byte)0]);
            var result = await RunAsync(OperationsWorkflows.SecuritySecureBoot, new EmptyInput(), EnvironmentFor(new OperationHost(directory)));
            Assert.IsTrue(result.Result.Complete); Assert.AreEqual(enabled, result.Result.Facts!.Value.GetProperty("Enabled").GetBoolean());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task EncryptionIsEvaluatedForEachMountInsteadOfInferringFromOneLuksDevice()
    {
        const string topology = "{\"blockdevices\":[{\"name\":\"/dev/nvme0n1\",\"type\":\"disk\",\"children\":[{\"name\":\"/dev/nvme0n1p1\",\"type\":\"part\",\"children\":[{\"name\":\"/dev/mapper/root\",\"type\":\"crypt\",\"mountpoints\":[\"/\"]}]},{\"name\":\"/dev/nvme0n1p2\",\"type\":\"part\",\"mountpoints\":[\"/data\"]}]}]}";
        var host = new OperationHost(native: (_, _) => Task.FromResult(new NativeResult(0, topology, "")));
        var result = await RunAsync(OperationsWorkflows.SecurityDiskEncryption, new EmptyInput(), EnvironmentFor(host));
        var mounts = result.Result.Facts!.Value.GetProperty("Mounts").EnumerateArray().ToDictionary(m => m.GetProperty("Mount").GetString()!);
        Assert.IsTrue(mounts["/"].GetProperty("Encrypted").GetBoolean()); Assert.IsFalse(mounts["/data"].GetProperty("Encrypted").GetBoolean());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Fail2banReportsJailEvidenceAndWarnsIfAJailCannotBeRead(bool readable)
    {
        var host = new OperationHost(native: (command, _) => Task.FromResult(command.Arguments.Count == 1
            ? new NativeResult(0, "Status\n|- Number of jail: 1\n`- Jail list: sshd\n", "")
            : new NativeResult(readable ? 0 : 1, readable ? "Status for the jail: sshd\nCurrently banned: 2\n" : "", "")));
        var result = await RunAsync(OperationsWorkflows.SecurityFail2banStatus, new EmptyInput(), EnvironmentFor(host));
        Assert.AreEqual(readable, result.Result.Complete); Assert.IsTrue(result.Result.Facts!.Value.GetProperty("Running").GetBoolean());
        if (readable) StringAssert.Contains(Trace(result.Events), "Currently banned");
    }

    [TestMethod]
    [DataRow("writeback", true)]
    [DataRow("writethrough", false)]
    public async Task BcacheAuditComparesTheSpecifiedVersionedBaseline(string expectedMode, bool compliant)
    {
        var directory = Directory.CreateTempSubdirectory("dms-bcache-baseline-").FullName;
        try
        {
            var bcache = Directory.CreateDirectory(Path.Combine(directory, "sys/block/bcache0/bcache")).FullName;
            await File.WriteAllTextAsync(Path.Combine(bcache, "cache_mode"), "writethrough [writeback] writearound none");
            await File.WriteAllTextAsync(Path.Combine(bcache, "running"), "1");
            Directory.CreateSymbolicLink(Path.Combine(bcache, "cache"), "/sys/fs/bcache/fixture-cache");
            await File.WriteAllTextAsync(Path.Combine(directory, "baseline.json"), JsonSerializer.Serialize(new LayoutOperations.BcacheBaseline("fixture-v1", expectedMode)));
            var result = await RunAsync(OperationsWorkflows.LayoutBcacheAudit, new BaselineInput("/baseline.json"), EnvironmentFor(new OperationHost(directory)));
            Assert.AreEqual(compliant, result.Result.Complete); StringAssert.Contains(Trace(result.Events), "fixture-v1");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CleanTrafficRequiresLiveSetsAndAttachedRulesRatherThanJustInstallationFiles(bool attached)
    {
        var directory = Directory.CreateTempSubdirectory("dms-clean-traffic-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "etc/ufw")); Directory.CreateDirectory(Path.Combine(directory, "etc/cron.daily"));
            await File.WriteAllTextAsync(Path.Combine(directory, "etc/ufw/after.init"), "fixture");
            await File.WriteAllTextAsync(Path.Combine(directory, "etc/cron.daily/auto-blacklist-update"), "https://example.test/list");
            var host = new OperationHost(directory, (command, _) =>
            {
                var output = "Name: fixture\nNumber of entries: 10\n";
                if (command.Arguments.Contains("--list-rules"))
                {
                    var parent = command.Arguments.Last(); var target = parent == "INPUT" ? "ufw-blocklist-input" : "ufw-blocklist-forward";
                    var set = command.Executable == "iptables" ? "ufw-blocklist-ipsum" : "ufw-blocklist-ipsum-ipv6";
                    output = attached ? $"-A {parent} -m set --match-set {set} src -j {target}\n" : "";
                }
                return Task.FromResult(new NativeResult(0, output, ""));
            });
            var result = await RunAsync(OperationsWorkflows.SecurityCleanTrafficStatus, new EmptyInput(), EnvironmentFor(host));
            Assert.AreEqual(attached, result.Result.Complete); Assert.AreEqual(attached, result.Result.Facts!.Value.GetProperty("Running").GetBoolean());
        }
        finally { Directory.Delete(directory, true); }
    }
    [TestMethod]
    public async Task SshAuditUsesEffectiveMatchContextAndSeparatesKeysFromCertificates()
    {
        NativeCommand? observed = null;
        var host = new OperationHost(native: (command, _) => { observed = command; return Task.FromResult(new NativeResult(0, "passwordauthentication no\nkbdinteractiveauthentication no\npermitrootlogin prohibit-password\npubkeyauthentication yes\ntrustedusercakeys none\n", "")); });
        var result = await RunAsync(OperationsWorkflows.SecuritySshAudit, new SshAuditInput("engineer", "192.0.2.1", "server.example.test"), EnvironmentFor(host));
        Assert.IsNotNull(observed); StringAssert.Contains(string.Join(' ', observed.Arguments), "user=engineer,addr=192.0.2.1,host=server.example.test");
        var facts = result.Result.Facts!.Value; Assert.IsTrue(facts.GetProperty("PasswordDisabled").GetBoolean());
        Assert.IsFalse(facts.GetProperty("RootDisabled").GetBoolean()); Assert.IsTrue(facts.GetProperty("PublicKeysEnabled").GetBoolean()); Assert.IsFalse(facts.GetProperty("CertificateCaConfigured").GetBoolean());
    }

    [TestMethod]
    [DataRow("ata", "passed", true)]
    [DataRow("ata", "failed", false)]
    [DataRow("ata", "running", false)]
    [DataRow("nvme", "passed", true)]
    [DataRow("nvme", "failed", false)]
    public async Task SmartShortTestRequiresANewCompletedResult(string engine, string outcome, bool complete)
    {
        static string Ata(int hours, string state) => JsonSerializer.Serialize(new
        {
            ata_smart_self_test_log = new { standard = new { table = new[] { new
            {
                lifetime_hours = hours, type = new { value = 1 },
                status = new { passed = state == "passed", remaining_percent = state == "running" ? 90 : 0 }
            } } } }
        });
        static string Nvme(int hours, int result) => JsonSerializer.Serialize(new
        {
            nvme_self_test_log = new { table = new[] { new { power_on_hours = hours, self_test_code = new { value = 1 }, self_test_result = new { value = result } } } }
        });
        var reads = 0; var starts = 0;
        var host = new OperationHost(native: (command, _) =>
        {
            if (command.Arguments.Contains("--test=short")) { starts++; return Task.FromResult(new NativeResult(0, "{}", "")); }
            reads++;
            var json = engine == "ata" ? Ata(reads, reads == 1 ? "passed" : outcome) : Nvme(reads, reads == 1 || outcome == "passed" ? 0 : 5);
            return Task.FromResult(new NativeResult(0, json, ""));
        });
        var result = await RunAsync(OperationsWorkflows.LayoutSmartTest, new SmartTestInput("/dev/nvme0", 0), EnvironmentFor(host));
        Assert.AreEqual(1, starts); Assert.AreEqual(complete, result.Result.Complete);
        Assert.AreEqual(complete ? RunState.Succeeded : RunState.Warning, result.Events.Last(e => !e.IsTrace).State);
    }

    [TestMethod]
    public async Task NvidiaWorkflowsKeepUnsupportedFieldsAndHandleGraphicsProcessesWithoutXmlExternalReads()
    {
        const string xml = "<!DOCTYPE nvidia_smi_log SYSTEM 'file:///etc/shadow'><nvidia_smi_log><driver_version>fixture</driver_version><cuda_version>13.0</cuda_version><gpu><uuid>GPU-fixture</uuid><processes><process_info><pid>424242</pid><process_name>graphics fixture</process_name><type>G</type><used_memory>N/A</used_memory></process_info></processes><ecc_errors>N/A</ecc_errors></gpu></nvidia_smi_log>";
        var directory = Directory.CreateTempSubdirectory("dms-nvidia-fixture-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "proc"));
            var host = new OperationHost(directory, (command, _) => Task.FromResult(new NativeResult(0,
                command.Executable == "journalctl" ? "NVRM: Xid 79 GPU fallen off bus" : command.Executable == "docker" ? "{}" : command.Arguments.Contains("--xml-format") ? xml : "0, GPU-fixture, NVIDIA fixture, fixture, N/A, N/A, 25, N/A, 48, N/A", "")));
            var environment = EnvironmentFor(host);
            var smi = await RunAsync(OperationsWorkflows.NvidiaSmi, new EmptyInput(), environment); StringAssert.Contains(Trace(smi.Events), "N/A");
            var processes = await RunAsync(OperationsWorkflows.NvidiaProcesses, new EmptyInput(), environment); StringAssert.Contains(Trace(processes.Events), "graphics fixture");
            var sample = await RunAsync(OperationsWorkflows.NvidiaSample, new SampleInput(1), environment); StringAssert.Contains(Trace(sample.Events), "N/A");
            var errors = await RunAsync(OperationsWorkflows.NvidiaErrors, new EmptyInput(), environment); StringAssert.Contains(Trace(errors.Events), "Xid 79");
            await RunAsync(OperationsWorkflows.NvidiaTopology, new EmptyInput(), environment);
            var driver = await RunAsync(OperationsWorkflows.NvidiaDriverInfo, new EmptyInput(), environment); StringAssert.Contains(Trace(driver.Events), "13.0");
            Assert.DoesNotContain("root:", JsonSerializer.Serialize(new[] { processes, errors, driver }));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task CatchingAnUnknownChildOutcomeCannotMakeTheParentSuccessful()
    {
        var events = new ConcurrentQueue<WorkflowEvent>();
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, e => { events.Enqueue(e); return Task.CompletedTask; });
        await Assert.ThrowsAsync<WorkflowNeedsVerificationException>(() => context.RunAsync(new CatchUnknownWorkflow(), CancellationToken.None));
        var terminals = events.Where(e => !e.IsTrace && e.State != RunState.Running).ToArray();
        Assert.HasCount(2, terminals); Assert.IsTrue(terminals.All(e => e.State == RunState.NeedsVerification));
        Assert.AreEqual(1, terminals.Count(e => e.ParentId == null));
    }

    private sealed class UnknownWorkflow : MachineWorkflow
    {
        public override string Name => "fixture.unknown-effect";
        public override Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token) => throw new WorkflowNeedsVerificationException(context.Id);
    }
    private sealed class CatchUnknownWorkflow : MachineWorkflow
    {
        public override string Name => "fixture.catch-unknown";
        public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
        {
            try { await context.CallAsync(new UnknownWorkflow(), token); }
            catch (WorkflowNeedsVerificationException) { }
            return "{}";
        }
    }

    private sealed class MemoryArtifacts : IAgentArtifactTransport
    {
        private readonly Dictionary<Guid, byte[]> _data = [];
        public bool Corrupt { get; set; }
        public async Task<ArtifactReference> UploadAsync(Guid workflowId, string file, string name, string kind, CancellationToken token)
        {
            var bytes = await File.ReadAllBytesAsync(file, token); var id = Guid.NewGuid(); _data[id] = bytes;
            return new ArtifactReference(id, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, kind, name);
        }
        public Task DownloadAsync(ArtifactReference artifact, string file, CancellationToken token) => File.WriteAllBytesAsync(file, Corrupt ? [0] : _data[artifact.Id], token);
    }
}
