using System.Collections.Concurrent;
using System.Globalization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class IncidentDiagnosticsTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-17T15:00:00Z", CultureInfo.InvariantCulture);
    private static async Task<(OperationResult Result, string Trace)> RunAsync<T>(MachineWorkflowDefinition<T, OperationResult> definition, T input, OperationHost host)
    {
        var events = new ConcurrentQueue<WorkflowEvent>();
        var agent = new AgentEnvironment(Guid.NewGuid(), "fixture", 0, 0, Operations: host);
        var registry = new MachineWorkflowRegistry();
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, e => { events.Enqueue(e); return Task.CompletedTask; }, agent, registry);
        var result = await context.RunAsync(registry.Create(definition.Name, definition.Serialize(input), agent), CancellationToken.None);
        return (definition.ReadResult(result), string.Join("", events.Where(e => e.IsTrace).Select(e => e.Message)));
    }
    private static string Sysstat(string fields) => """
        {"sysstat":{"hosts":[{"number-of-cpus":8,"file-date":"2026-11-17","statistics":[
        {"timestamp":{"date":"2026-11-17","time":"15:10:00","tz":"UTC","interval":600},
        """ + fields + "}]}]}}";

    [TestMethod]
    public void HistoryLimitsValidateResolvedWindowsAndRejectExpressionInputs()
    {
        Assert.Throws<ArgumentException>(() => new HistoryInput(Since: DateTimeOffset.UtcNow.AddDays(-8)).Validate());
        Assert.Throws<ArgumentException>(() => new HistoryInput(Start, Start).Validate());
        Assert.Throws<ArgumentException>(() => new HistoryInput(IntervalSeconds: 0).Validate());
        Assert.Throws<ArgumentException>(() => new HistoryInput(Resource: "sda;reboot").Validate());
        Assert.Throws<ArgumentException>(() => new PcpHistoryInput("../../secret", ["kernel.all.load"]).Validate());
        Assert.Throws<ArgumentException>(() => new PcpHistoryInput(new string('a', 24), ["load=kernel.all.load*2"]).Validate());
        Assert.Throws<ArgumentException>(() => new PcpHistoryInput(new string('a', 24), ["--help"]).Validate());
        new PcpHistoryInput(new string('a', 24), ["kernel.all.load"], Instance: "1 minute").Validate();
        Assert.Throws<ArgumentException>(() => new JournalInput(Kernel: true, Service: "test.service").Validate());
        Assert.Throws<ArgumentException>(() => new KernelEventsInput(Categories: ["arbitrary-regex"]).Validate());
        Assert.Throws<ArgumentException>(() => new KernelEventsInput(BeforeCursor: "x\ny").Validate());
    }
    [TestMethod]
    public void SysstatCpuKeepsIoWaitSeparateAndRetainsNativeIntervals()
    {
        var text = Sysstat("\"cpu-load\":[{\"cpu\":\"all\",\"user\":2,\"system\":4,\"iowait\":94,\"idle\":0},{\"cpu\":\"0\",\"user\":3}]");
        var rows = HistoryOperations.ParseSysstat(text, "cpu", Start, Start.AddHours(1)).Points;
        Assert.HasCount(1, rows); Assert.AreEqual(94, rows[0].Values["iowait"]); Assert.AreEqual(600, rows[0].NativeIntervalSeconds);
        Assert.AreEqual(8, rows[0].Values["logical-cpus"]); Assert.AreEqual("%", rows[0].Units["user"]);
        Assert.HasCount(1, HistoryOperations.ParseSysstat(text, "cpu", Start, Start.AddHours(1), "0").Points);
        Assert.HasCount(0, HistoryOperations.ParseSysstat(text, "cpu", Start.AddHours(2), Start.AddHours(3)).Points);
    }
    [TestMethod]
    public void DiskAndMemoryAndNetworkKeepTheirUnitsAndResourcesSeparate()
    {
        var disk = HistoryOperations.ParseSysstat(Sysstat("\"disk\":[{\"disk-device\":\"sdc\",\"rkB\":80000,\"await\":8304,\"aqu-sz\":607.8}]"), "disk", Start, Start.AddHours(1), "sdc").Points.Single();
        Assert.AreEqual("ms", disk.Units["await"]); Assert.AreEqual("KiB/s", disk.Units["rkB"]); Assert.AreEqual(607.8, disk.Values["aqu-sz"]);
        var memory = HistoryOperations.ParseSysstat(Sysstat("\"memory\":{\"avail\":1024,\"swpused\":32},\"swap-pages\":{\"pswpin\":2}"), "memory", Start, Start.AddHours(1)).Points;
        Assert.HasCount(2, memory); Assert.AreEqual("KiB", memory[0].Units["avail"]); Assert.AreEqual("pages/s", memory[1].Units["pswpin"]);
        var network = HistoryOperations.ParseSysstat(Sysstat("\"network\":{\"net-dev\":[{\"iface\":\"eth0\",\"txkB\":100000},{\"iface\":\"veth0\",\"txkB\":100000}],\"net-edev\":[{\"iface\":\"eth0\",\"rxerr\":1}]}"), "network", Start, Start.AddHours(1)).Points;
        Assert.HasCount(3, network); Assert.AreEqual("eth0:errors", network[2].Resource);
    }
    [TestMethod]
    public void SysstatCrossMidnightUsesAbsoluteTimesAndRejectsUnknownTimezone()
    {
        var text = Sysstat("\"queue\":{\"ldavg-1\":470.99,\"blocked\":463}");
        var point = HistoryOperations.ParseSysstat(text, "load", Start.AddHours(-16), Start.AddHours(2)).Points.Single();
        Assert.AreEqual(Start.AddMinutes(10), point.At); Assert.AreEqual(463, point.Values["blocked"]);
        Assert.Throws<InvalidDataException>(() => HistoryOperations.ParseSysstat(text.Replace("\"tz\":\"UTC\"", "\"tz\":\"CST\""), "load", Start, Start.AddHours(1)));
    }
    [TestMethod]
    public void PcpCsvPreservesUnknownValuesLiteralInstancesAndDescriptorUnits()
    {
        var at = Start.ToUnixTimeSeconds();
        var rows = HistoryOperations.ParsePcpCsv($"Time,\"kernel.all.load-1 minute\",\"instance,with comma\"\n{at},3.25,N/A\n{at+60},nan,4.5\n", "kernel.all.load", "Data Type: float\nSemantics: instant  Units: none", Start, Start.AddHours(1)).ToArray();
        Assert.HasCount(4, rows); Assert.AreEqual(3.25, rows[0].Values["kernel.all.load"]); Assert.IsNull(rows[1].Values["kernel.all.load"]);
        Assert.IsNull(rows[2].Values["kernel.all.load"]); Assert.AreEqual("none", rows[0].Units["kernel.all.load"]);
        StringAssert.Contains(rows[1].Resource, "instance,with comma");
    }
    [TestMethod]
    public void DiskRatesUseActualElapsedTimeAndIncludeQueueAndReadLatency()
    {
        var old = new DiskCounters(10, 1024, 100, 5, 512, 20, 0, 100, 1000);
        var current = new DiskCounters(12, 3072, 16708, 6, 1536, 30, 7, 1100, 21000);
        var value = ProcessDiagnostics.DiskDelta("sdc", old, current, 2);
        Assert.AreEqual("sdc", value.Device); Assert.AreEqual(2048, value.ReadBytes); Assert.AreEqual(1024, value.WriteBytes);
        Assert.AreEqual(1000, value.BusyMilliseconds); Assert.AreEqual(2, value.ElapsedSeconds);
        Assert.AreEqual(512, value.WriteBytesPerSecond); Assert.AreEqual(0.5, value.WriteIops); Assert.AreEqual(10, value.WriteAwaitMilliseconds);
        Assert.AreEqual(1024, value.ReadBytesPerSecond); Assert.AreEqual(1, value.ReadIops);
        Assert.AreEqual(8304, value.ReadAwaitMilliseconds); Assert.AreEqual(10, value.AverageQueueDepth);
        Assert.AreEqual(50, value.BusyPercent); Assert.AreEqual(7, value.Inflight); Assert.IsNull(value.MissingReason);
        Assert.IsNull(ProcessDiagnostics.DiskDelta("sdc", current, old, 2).ReadIops);
        Assert.IsNotNull(ProcessDiagnostics.DiskDelta("sdc", null, current, 2).MissingReason);
        Assert.IsNull(ProcessDiagnostics.DiskDelta("sdc", current, current, 2).AwaitMilliseconds);
    }
    [TestMethod]
    public void JournalPaginationUsesCursorsWithoutSplittingMultilineRecords()
    {
        const string text = "{\"__CURSOR\":\"c3\",\"MESSAGE\":\"one\\ntwo\"}\n{\"__CURSOR\":\"c2\",\"MESSAGE\":\"two\"}\n{\"__CURSOR\":\"c1\",\"MESSAGE\":\"three\"}\n";
        var first = JournalDiagnostics.ParseRecords(text, 2); Assert.IsTrue(first.HasMore); Assert.AreEqual("c2", first.NextCursor);
        Assert.AreEqual("one\ntwo", first.Records[0].GetProperty("MESSAGE").GetString());
        var next = JournalDiagnostics.ParseRecords(text.Split('\n', 2)[1], 2, "c2");
        Assert.HasCount(1, next.Records); Assert.IsFalse(next.HasMore); Assert.AreEqual("c1", next.NextCursor);
    }
    [TestMethod]
    public async Task KernelEventFilteringIsNativeBeforeLimitAndDoesNotForceCurrentBoot()
    {
        NativeCommand? observed = null;
        var host = new OperationHost(native: (command, _) => { observed = command; return Task.FromResult(new NativeResult(0, "{\"__CURSOR\":\"x\",\"__REALTIME_TIMESTAMP\":\"1794929400000000\",\"MESSAGE\":\"blocked for more than 120 seconds\\nsecond line\"}\n", "")); });
        var result = await RunAsync(OperationsWorkflows.SystemKernelEvents, new KernelEventsInput(Start, Start.AddHours(1), Categories: ["hung-task"]), host);
        Assert.IsTrue(result.Result.Complete); Assert.IsNotNull(observed);
        CollectionAssert.Contains(observed.Arguments.ToArray(), "_TRANSPORT=kernel");
        CollectionAssert.Contains(observed.Arguments.ToArray(), "--lines=201"); Assert.DoesNotContain("--dmesg", observed.Arguments);
        Assert.IsTrue(observed.Arguments.Any(a => a.StartsWith("--grep=", StringComparison.Ordinal) && a.Contains("hung", StringComparison.Ordinal)));
        StringAssert.Contains(result.Trace, "second line");
    }
    [TestMethod]
    public async Task JournalPermissionErrorsAreNotSuccessfulEmptyResults()
    {
        var host = new OperationHost(native: (_, _) => Task.FromResult(new NativeResult(1, "", "Permission denied")));
        await Assert.ThrowsAsync<IOException>(() => RunAsync(OperationsWorkflows.SystemKernelEvents, new KernelEventsInput(), host));
    }
    [TestMethod]
    public async Task MissingHistoryToolsProduceWarningWithoutExecutingInstallers()
    {
        var root = Directory.CreateTempSubdirectory("dms-history-missing-").FullName;
        try
        {
            var calls = new List<string>();
            var host = new OperationHost(root, (c, _) => { calls.Add(c.Executable); throw new NotSupportedException("Required tool is not installed."); });
            var result = await RunAsync(OperationsWorkflows.PerformanceHistoryCpu, new HistoryInput(Start, Start.AddHours(1)), host);
            Assert.IsFalse(result.Result.Complete); StringAssert.Contains(result.Trace, "NoSamples");
            Assert.IsTrue(calls.All(c => c == "sadf")); Assert.DoesNotContain("apt", string.Join(',', calls));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task SysstatReadsArchiveDatesAndReturnsPartialCoverageWithoutInventingSamples()
    {
        var root = Directory.CreateTempSubdirectory("dms-history-sysstat-").FullName;
        try
        {
            Directory.CreateDirectory(root + "/var/log/sysstat"); await File.WriteAllTextAsync(root + "/var/log/sysstat/sa17", "fixture");
            var host = new OperationHost(root, (c, _) => Task.FromResult(new NativeResult(0,
                c.Arguments.Contains("-V") ? "sysstat 12.7" : c.Arguments.Contains("-H") ? "{\"sysstat\":{\"hosts\":[{\"file-date\":\"2026-11-17\"}]}}" :
                Sysstat("\"queue\":{\"ldavg-1\":470.99,\"blocked\":463}"), "")));
            var result = await RunAsync(OperationsWorkflows.PerformanceHistoryLoad, new HistoryInput(Start, Start.AddHours(1), 1), host);
            Assert.IsFalse(result.Result.Complete); StringAssert.Contains(result.Trace, "470.99"); StringAssert.Contains(result.Trace, "without averaging or interpolation");
            Assert.HasCount(1, result.Result.Facts!.Value.GetProperty("Samples").EnumerateArray().ToArray());
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public void ArchiveDiscoveryRejectsSymlinkMembersAndNeverReturnsArbitraryFiles()
    {
        var root = Directory.CreateTempSubdirectory("dms-history-links-").FullName;
        try
        {
            Directory.CreateDirectory(root + "/var/log/sysstat"); Directory.CreateDirectory(root + "/var/log/pcp/pmlogger/host");
            File.WriteAllText(root + "/var/log/sysstat/not-an-archive", "secret");
            File.CreateSymbolicLink(root + "/var/log/sysstat/sa17", "/etc/passwd");
            File.WriteAllText(root + "/var/log/pcp/pmlogger/host/example.meta", "fixture");
            File.CreateSymbolicLink(root + "/var/log/pcp/pmlogger/host/example.0", "/etc/passwd");
            Assert.HasCount(0, HistoryOperations.Discover(new OperationHost(root)));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task BlockedThreadsIncludeWaitAndContainerWithoutReadingCommandLine()
    {
        var root = Directory.CreateTempSubdirectory("dms-blocked-").FullName;
        try
        {
            var container = new string('a', 64); var task = root + "/proc/123/task/124"; Directory.CreateDirectory(task);
            await File.WriteAllTextAsync(root + "/proc/123/cgroup", "0::/docker/" + container);
            await File.WriteAllTextAsync(root + "/proc/123/status", "Uid:\t1000\t1000\t1000\t1000\n");
            await File.WriteAllTextAsync(task + "/stat", "124 (reader thread) D 1 2 3");
            await File.WriteAllTextAsync(task + "/wchan", "folio_wait_bit_common");
            var result = await RunAsync(OperationsWorkflows.PerformanceBlockedProcesses, new BlockedProcessesInput(Container: container), new OperationHost(root));
            StringAssert.Contains(result.Trace, "folio_wait_bit_common"); StringAssert.Contains(result.Trace, container);
            StringAssert.Contains(result.Trace, "reader thread"); Assert.IsTrue(result.Result.Complete);
            File.Delete(task + "/wchan");
            var unavailable = await RunAsync(OperationsWorkflows.PerformanceBlockedProcesses, new BlockedProcessesInput(), new OperationHost(root));
            StringAssert.Contains(unavailable.Trace, "MissingReason"); Assert.IsFalse(unavailable.Result.Complete);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task HistoryContinuationDoesNotLoseResourcesWithEqualTimestamps()
    {
        var root = Directory.CreateTempSubdirectory("dms-history-pages-").FullName;
        try
        {
            Directory.CreateDirectory(root + "/var/log/sysstat"); await File.WriteAllTextAsync(root + "/var/log/sysstat/sa17", "fixture");
            var host = new OperationHost(root, (c, _) => Task.FromResult(new NativeResult(0,
                c.Arguments.Contains("-V") ? "sysstat 12.7" : c.Arguments.Contains("-H") ? "{\"sysstat\":{\"hosts\":[{\"file-date\":\"2026-11-17\"}]}}" :
                Sysstat("\"disk\":[{\"disk-device\":\"sda\",\"await\":2},{\"disk-device\":\"sdb\",\"await\":3},{\"disk-device\":\"sdc\",\"await\":4}]"), "")));
            var input = new HistoryInput(Start, Start.AddHours(1), Limit: 1);
            var first = await RunAsync(OperationsWorkflows.PerformanceHistoryDisk, input, host);
            var firstFacts = first.Result.Facts!.Value;
            var cursor = firstFacts.GetProperty("NextCursor").GetString(); Assert.IsNotNull(cursor);
            Assert.AreEqual("sda", firstFacts.GetProperty("Samples")[0].GetProperty("Resource").GetString());
            var second = await RunAsync(OperationsWorkflows.PerformanceHistoryDisk, input with { After = cursor }, host);
            Assert.AreEqual("sdb", second.Result.Facts!.Value.GetProperty("Samples")[0].GetProperty("Resource").GetString());
            Assert.Throws<ArgumentException>(() => new HistoryInput(After: "not a cursor").Validate());
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod]
    public async Task PcpRejectsNonNumericDescriptorsBeforeReadingValues()
    {
        var root = Directory.CreateTempSubdirectory("dms-pcp-type-").FullName;
        try
        {
            Directory.CreateDirectory(root + "/var/log/pcp/pmlogger/host");
            await File.WriteAllTextAsync(root + "/var/log/pcp/pmlogger/host/example.meta", "fixture");
            var calls = new List<NativeCommand>();
            var host = new OperationHost(root, (c, _) => { calls.Add(c); return Task.FromResult(new NativeResult(0,
                c.Executable == "pminfo" ? "Data Type: string\nUnits: none" : "pmrep 7.1.1", "")); });
            var id = HistoryOperations.Discover(host).Single().Id;
            await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(OperationsWorkflows.PerformancePcpHistory, new PcpHistoryInput(id, ["kernel.uname.nodename"]), host));
            Assert.HasCount(1, calls.Where(c => c.Executable == "pmrep").ToArray());
            Assert.IsTrue(calls.Single(c => c.Executable == "pmrep").Arguments.Contains("-V"));
        }
        finally { Directory.Delete(root, true); }
    }

}
