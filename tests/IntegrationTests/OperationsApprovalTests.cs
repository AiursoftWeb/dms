using System.Security.Cryptography;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Dms;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class OperationsApprovalTests : TestBase
{
    private static IEnumerable<(WorkflowDefinition Definition, object Input, RiskLevel Risk, string Command)> Cases(
        ArtifactReference artifact)
    {
        yield return (OperationsWorkflows.PerformanceHistorySources, new HistoryInput(), RiskLevel.Low,
            "machine performance history-sources");
        yield return (OperationsWorkflows.PerformanceHistoryCpu, new HistoryInput(), RiskLevel.Low,
            "machine performance history cpu");
        yield return (OperationsWorkflows.PerformanceHistoryLoad, new HistoryInput(), RiskLevel.Low,
            "machine performance history load");
        yield return (OperationsWorkflows.PerformanceHistoryMemory, new HistoryInput(), RiskLevel.Low,
            "machine performance history memory");
        yield return (OperationsWorkflows.PerformanceHistoryDisk, new HistoryInput(), RiskLevel.Low,
            "machine performance history disk");
        yield return (OperationsWorkflows.PerformanceHistoryNetwork, new HistoryInput(), RiskLevel.Low,
            "machine performance history network");
        yield return (OperationsWorkflows.PerformancePcpHistory,
            new PcpHistoryInput(new string('a', 24), ["kernel.all.load"]), RiskLevel.Medium,
            "machine performance pcp-history");
        yield return (OperationsWorkflows.PerformanceBlockedProcesses, new BlockedProcessesInput(), RiskLevel.Medium,
            "machine performance blocked-processes");
        yield return (OperationsWorkflows.SystemKernelEvents, new KernelEventsInput(), RiskLevel.Medium,
            "machine system kernel-events");
        yield return (OperationsWorkflows.FilesystemLs, new ListPathInput("/tmp/dms-test"), RiskLevel.Low,
            "machine filesystem ls");
        yield return (OperationsWorkflows.FilesystemStat, new PathInput("/tmp/dms-test"), RiskLevel.Low,
            "machine filesystem stat");
        yield return (OperationsWorkflows.FilesystemCat, new TextFileInput("/tmp/dms-test/file"), RiskLevel.High,
            "machine filesystem cat");
        yield return (OperationsWorkflows.FilesystemTail, new TextFileInput("/tmp/dms-test/file"), RiskLevel.High,
            "machine filesystem tail");
        yield return (OperationsWorkflows.FilesystemFind, new FindFilesInput("/tmp/dms-test"), RiskLevel.Low,
            "machine filesystem find");
        yield return (OperationsWorkflows.FilesystemMkdir, new MakeDirectoryInput("/tmp/dms-test/new"), RiskLevel.High,
            "machine filesystem mkdir");
        yield return (OperationsWorkflows.FilesystemCp,
            new CopyPathInput("/tmp/dms-test/source", "/tmp/dms-test/destination"), RiskLevel.VeryHigh,
            "machine filesystem cp");
        yield return (OperationsWorkflows.FilesystemMv,
            new CopyPathInput("/tmp/dms-test/source", "/tmp/dms-test/destination"), RiskLevel.VeryHigh,
            "machine filesystem mv");
        yield return (OperationsWorkflows.FilesystemExport, new ExportPathInput("/tmp/dms-test/source"), RiskLevel.High,
            "machine filesystem export");
        yield return (OperationsWorkflows.FilesystemImport, new ImportPathInput(artifact, "/tmp/dms-test/destination"),
            RiskLevel.VeryHigh, "machine filesystem import");
        yield return (OperationsWorkflows.LayoutFdiskQuery, new EmptyInput(), RiskLevel.Safe,
            "machine layout fdisk-query");
        yield return (OperationsWorkflows.LayoutMounts, new EmptyInput(), RiskLevel.Low, "machine layout mounts");
        yield return (OperationsWorkflows.LayoutInodes, new EmptyInput(), RiskLevel.Safe, "machine layout inodes");
        yield return (OperationsWorkflows.LayoutOpenDeleted, new EmptyInput(), RiskLevel.Low,
            "machine layout open-deleted");
        yield return (OperationsWorkflows.LayoutSmart, new DeviceInput("/dev/dms-fixture"), RiskLevel.Low,
            "machine layout smart");
        yield return (OperationsWorkflows.LayoutSmartTest, new SmartTestInput("/dev/dms-fixture", 0), RiskLevel.Medium,
            "machine layout smart-test");
        yield return (OperationsWorkflows.LayoutBcacheInfo, new EmptyInput(), RiskLevel.Low,
            "machine layout bcache-info");
        yield return (OperationsWorkflows.LayoutBcacheAudit, new BaselineInput(), RiskLevel.Low,
            "machine layout bcache-audit");
        yield return (OperationsWorkflows.PerformanceProcesses, new ProcessListInput(), RiskLevel.Low,
            "machine performance processes");
        yield return (OperationsWorkflows.PerformanceCpuSample, new SampleInput(1), RiskLevel.Low,
            "machine performance cpu-sample");
        yield return (OperationsWorkflows.PerformanceMemoryBreakdown, new EmptyInput(), RiskLevel.Low,
            "machine performance memory-breakdown");
        yield return (OperationsWorkflows.PerformanceIoSample, new SampleInput(1), RiskLevel.Low,
            "machine performance io-sample");
        yield return (OperationsWorkflows.PerformanceDstatSample, new SampleInput(1), RiskLevel.Low,
            "machine performance dstat-sample");
        yield return (OperationsWorkflows.PerformancePressure, new EmptyInput(), RiskLevel.Safe,
            "machine performance pressure");
        yield return (OperationsWorkflows.PerformanceOomHistory, new EventWindowInput(), RiskLevel.Medium,
            "machine performance oom-history");
        yield return (OperationsWorkflows.NvidiaOverview, new EmptyInput(), RiskLevel.Low, "machine nvidia overview");
        yield return (OperationsWorkflows.NvidiaSmi, new EmptyInput(), RiskLevel.Safe, "machine nvidia smi");
        yield return (OperationsWorkflows.NvidiaProcesses, new EmptyInput(), RiskLevel.Low, "machine nvidia processes");
        yield return (OperationsWorkflows.NvidiaSample, new SampleInput(1), RiskLevel.Low, "machine nvidia sample");
        yield return (OperationsWorkflows.NvidiaErrors, new EmptyInput(), RiskLevel.Low, "machine nvidia errors");
        yield return (OperationsWorkflows.NvidiaTopology, new EmptyInput(), RiskLevel.Low, "machine nvidia topology");
        yield return (OperationsWorkflows.NvidiaDriverInfo, new EmptyInput(), RiskLevel.Low,
            "machine nvidia driver-info");
        yield return (OperationsWorkflows.DockerInspect, new DockerContainerInput(new string('1', 64)), RiskLevel.Low,
            "machine docker inspect");
        yield return (OperationsWorkflows.DockerStatsSample, new SampleInput(1), RiskLevel.Low,
            "machine docker stats-sample");
        yield return (OperationsWorkflows.DockerEvents, new ContainerEventsInput(new string('1', 64)), RiskLevel.Low,
            "machine docker events");
        yield return (OperationsWorkflows.DockerDiagnoseStartup, new DockerContainerInput(new string('1', 64)),
            RiskLevel.Medium, "machine docker diagnose-startup");
        yield return (OperationsWorkflows.DockerStop, new ContainerActionInput(new string('1', 64)), RiskLevel.High,
            "machine docker stop");
        yield return (OperationsWorkflows.DockerStart, new DockerContainerInput(new string('1', 64)), RiskLevel.High,
            "machine docker start");
        yield return (OperationsWorkflows.DockerRestart, new ContainerActionInput(new string('1', 64)), RiskLevel.High,
            "machine docker restart");
        yield return (OperationsWorkflows.DockerFilesystemExport,
            new ContainerExportInput(new string('1', 64), "/tmp/file"), RiskLevel.High,
            "machine docker filesystem export");
        yield return (OperationsWorkflows.DockerFilesystemCpTo,
            new ContainerCopyInput(new string('1', 64), "/tmp/source", "/tmp/destination"), RiskLevel.VeryHigh,
            "machine docker filesystem cp-to");
        yield return (OperationsWorkflows.DockerFilesystemCpFrom,
            new ContainerCopyInput(new string('1', 64), "/tmp/source", "/tmp/destination"), RiskLevel.VeryHigh,
            "machine docker filesystem cp-from");
        yield return (OperationsWorkflows.SwarmServiceList, new EmptyInput(), RiskLevel.Low,
            "machine swarm service list");
        yield return (OperationsWorkflows.SwarmServiceInspect, new SwarmServiceInput("dms-fixture"), RiskLevel.Low,
            "machine swarm service inspect");
        yield return (OperationsWorkflows.SwarmServicePs, new SwarmServiceInput("dms-fixture"), RiskLevel.Low,
            "machine swarm service ps");
        yield return (OperationsWorkflows.SwarmServiceLogs, new SwarmLogsInput("dms-fixture"), RiskLevel.Medium,
            "machine swarm service logs");
        yield return (OperationsWorkflows.SwarmServiceUpdateStatus, new SwarmServiceInput("dms-fixture"), RiskLevel.Low,
            "machine swarm service rollout-status");
        yield return (OperationsWorkflows.SwarmServiceStop, new SwarmChangeInput("dms-fixture", 1, ["node-one"]),
            RiskLevel.High, "machine swarm service stop");
        yield return (OperationsWorkflows.SwarmServiceStart, new SwarmChangeInput("dms-fixture", 1, ["node-one"]),
            RiskLevel.High, "machine swarm service start");
        yield return (OperationsWorkflows.SwarmServiceUpdate,
            new SwarmChangeInput("dms-fixture", 1, ["node-one"], "example:latest"), RiskLevel.High,
            "machine swarm service update");
        yield return (OperationsWorkflows.SwarmServiceRollback, new SwarmChangeInput("dms-fixture", 1, ["node-one"]),
            RiskLevel.High, "machine swarm service rollback");
        yield return (OperationsWorkflows.SystemInfo, new EmptyInput(), RiskLevel.Safe, "machine system info");
        yield return (OperationsWorkflows.SystemUptime, new EmptyInput(), RiskLevel.Safe, "machine system uptime");
        yield return (OperationsWorkflows.SystemctlList, new ServiceListInput(), RiskLevel.Low,
            "machine systemctl list");
        yield return (OperationsWorkflows.SystemctlStart, new ServiceInput("dms-fixture.service"), RiskLevel.Medium,
            "machine systemctl start");
        yield return (OperationsWorkflows.SystemctlStop, new ServiceInput("dms-fixture.service"), RiskLevel.High,
            "machine systemctl stop");
        yield return (OperationsWorkflows.SystemJournal, new JournalInput(Service: "dms-fixture.service"),
            RiskLevel.Medium, "machine system journal");
        yield return (OperationsWorkflows.SystemTimers, new EmptyInput(), RiskLevel.Medium, "machine system timers");
        yield return (OperationsWorkflows.AptInspect, new EmptyInput(), RiskLevel.Low, "machine apt inspect");
        yield return (OperationsWorkflows.AptInstall, new PackageInput(["fixture-package=1.0"]), RiskLevel.High,
            "machine apt install");
        yield return (OperationsWorkflows.AptUpgrade, new PackageUpgradeInput(["fixture-package=1.0"]), RiskLevel.High,
            "machine apt upgrade");
        yield return (OperationsWorkflows.SystemReboot, new RebootInput(), RiskLevel.High, "machine system reboot");
        yield return (OperationsWorkflows.SystemExecuteScript, new ScriptInput("bash", artifact), RiskLevel.VeryHigh,
            "machine system execute-script");
        yield return (OperationsWorkflows.DatabaseDiscover, new EmptyInput(), RiskLevel.Low,
            "machine database discover");
        yield return (OperationsWorkflows.DatabaseListDatabases, new DatabaseInput("fixture-read"), RiskLevel.Low,
            "machine database list-databases");
        yield return (OperationsWorkflows.DatabaseListTables, new DatabaseNameInput("fixture-read", "fixture"),
            RiskLevel.Low, "machine database list-tables");
        yield return (OperationsWorkflows.DatabaseDescribeTable,
            new DatabaseTableInput("fixture-read", "fixture", "events"), RiskLevel.Low,
            "machine database describe-table");
        yield return (OperationsWorkflows.DatabaseQueryTable,
            new DatabaseQueryInput("fixture-read", "fixture", "events", ["id"]), RiskLevel.Medium,
            "machine database query-table");
        yield return (OperationsWorkflows.DatabaseQuerySql, new DatabaseSqlInput("fixture-read", artifact),
            RiskLevel.High, "machine database query-sql");
        yield return (OperationsWorkflows.DatabaseExecuteSql, new DatabaseSqlInput("fixture-read", artifact),
            RiskLevel.VeryHigh, "machine database execute-sql");
        yield return (OperationsWorkflows.DatabaseDiagnostics, new DatabaseInput("fixture-read"), RiskLevel.Medium,
            "machine database diagnostics");
        yield return (OperationsWorkflows.NetworkingInterfaces, new EmptyInput(), RiskLevel.Low,
            "machine networking interfaces");
        yield return (OperationsWorkflows.NetworkingRoutes, new EmptyInput(), RiskLevel.Low,
            "machine networking routes");
        yield return (OperationsWorkflows.NetworkingSockets, new EmptyInput(), RiskLevel.Low,
            "machine networking sockets");
        yield return (OperationsWorkflows.NetworkingDnsQuery, new DnsQueryInput("localhost"), RiskLevel.Low,
            "machine networking dns-query");
        yield return (OperationsWorkflows.NetworkingProbe, new NetworkProbeInput("localhost"), RiskLevel.Low,
            "machine networking probe");
        yield return (OperationsWorkflows.NetworkingTrafficSample, new SampleInput(1), RiskLevel.Medium,
            "machine networking traffic-sample");
        yield return (OperationsWorkflows.NetworkingFirewallInfo, new EmptyInput(), RiskLevel.Low,
            "machine networking firewall-info");
        yield return (OperationsWorkflows.NetworkingTlsCheck, new NetworkProbeInput("localhost"), RiskLevel.Low,
            "machine networking tls-check");
        yield return (OperationsWorkflows.SecuritySecureBoot, new EmptyInput(), RiskLevel.Safe,
            "machine security secure-boot");
        yield return (OperationsWorkflows.SecurityDiskEncryption, new EmptyInput(), RiskLevel.Low,
            "machine security disk-encryption");
        yield return (OperationsWorkflows.SecuritySshAudit, new SshAuditInput(), RiskLevel.Low,
            "machine security ssh-audit");
        yield return (OperationsWorkflows.SshDisable, new EmptyInput(), RiskLevel.Medium,
            "machine ssh disable");
        yield return (OperationsWorkflows.SecurityFail2banStatus, new EmptyInput(), RiskLevel.Low,
            "machine security fail2ban-status");
        yield return (OperationsWorkflows.SecurityCleanTrafficStatus, new EmptyInput(), RiskLevel.Low,
            "machine security clean-traffic-status");
        yield return (OperationsWorkflows.SecurityAuthEvents, new EventWindowInput(), RiskLevel.Medium,
            "machine security auth-events");
        yield return (OperationsWorkflows.SecurityAccountsAudit, new EmptyInput(), RiskLevel.Medium,
            "machine security accounts-audit");
        yield return (OperationsWorkflows.SecurityBaselineAudit, new BaselineInput(), RiskLevel.Medium,
            "machine security baseline-audit");
    }

    [TestMethod]
    public async Task NvidiaOverviewRequiresLowPermissionWhileSmiAcceptsSafePermission()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var user = new User { UserName = "nvidia-" + Guid.NewGuid().ToString("N"), DisplayName = "Requester" };
        var machine = new Machine { Name = "GPU fixture", KeyHash = "fixture" };
        db.Users.Add(user);
        db.Machines.Add(machine);
        DmsTestPermissions.Risk(db, user.Id, RiskLevel.Safe, request: true);
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var overview = OperationsWorkflows.NvidiaOverview.Request(new EmptyInput(), "GPU details", [machine.Id]);
        var failure = await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(user.Id, overview));
        Assert.AreEqual(403, failure.StatusCode);

        var smi = OperationsWorkflows.NvidiaSmi.Request(new EmptyInput(), "GPU snapshot", [machine.Id]);
        var box = await service.SubmitAsync(user.Id, smi);
        Assert.AreEqual(RiskLevel.Safe, box.Risk);
        Assert.AreEqual(DecisionState.Pending, box.Decision);
    }

    [TestMethod]
    public async Task AllOperationsFreezeTheirBusinessInputAndRiskWithoutStartingBeforeApproval()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var user = new User { UserName = "ops-" + Guid.NewGuid().ToString("N"), DisplayName = "Operations requester" };
        db.Users.Add(user);
        foreach (var risk in Enum.GetValues<RiskLevel>()) DmsTestPermissions.Risk(db, user.Id, risk, request: true);
        var machine = new Machine { Name = "Approved fixture", KeyHash = "fixture" };
        db.Machines.Add(machine);
        await db.SaveChangesAsync();
        var bytes = System.Text.Encoding.UTF8.GetBytes("SELECT 1");
        var artifact = new ArtifactReference(Guid.NewGuid(),
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            bytes.Length,
            Name: "input.txt");
        var store = Server.Services.GetRequiredService<DmsArtifactStore>();
        await store.BeginUploadAsync(artifact, user.Id, null, CancellationToken.None);
        await store.AppendUploadAsync(artifact.Id, 0, new MemoryStream(bytes), CancellationToken.None);
        await store.CompleteUploadAsync(artifact.Id, CancellationToken.None);
        var cases = Cases(artifact).ToArray();
        Assert.HasCount(97, cases);
        Assert.AreEqual(97, cases.Select(c => c.Definition.Name).Distinct().Count());
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var allMachines = OperationsWorkflows.SshDisable.Request(new EmptyInput(), "Verify target scope", allMachines: true);
        var multipleMachines = OperationsWorkflows.SshDisable.Request(new EmptyInput(), "Verify target scope", [machine.Id, Guid.NewGuid()]);
        foreach (var request in new[] { allMachines, multipleMachines })
        {
            var exception = await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(user.Id, request));
            Assert.AreEqual(400, exception.StatusCode);
            Assert.AreEqual("This workflow requires a single machine.", exception.Message);
        }
        List<Guid> boxes = [];
        foreach (var item in cases)
        {
            var serialized = JsonSerializer.Serialize(item.Input, item.Input.GetType());
            var request = new OperationRequest
            {
                Workflow = item.Definition.Name, MachineIds = [machine.Id], Reason = "Approval contract acceptance",
                Input = serialized
            };
            var box = await service.SubmitAsync(user.Id, request);
            boxes.Add(box.Id);
            Assert.AreEqual(item.Risk, box.Risk, item.Command);
            Assert.AreEqual(DecisionState.Pending, box.Decision, item.Command);
            Assert.IsNull(box.WorkflowId, item.Command);
            var frozen = JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!;
            Assert.AreEqual(item.Definition.Normalize(serialized), frozen.Input, item.Command);
            CollectionAssert.AreEqual(new[] { machine.Id },
                JsonSerializer.Deserialize<Guid[]>(box.TargetMachines)!,
                item.Command);
            Assert.Throws<JsonException>(() => item.Definition.Validate("{\"UnapprovedCommand\":\"/bin/bash\"}"));
            var help = await Client.DmsCommandApp.Create().TestRunAsync([.. item.Command.Split(' '), "--help"]);
            Assert.AreEqual(0, help.ProgramReturn, item.Command + ": " + help.StdErr);
            StringAssert.Contains(help.StdOut, "--reason", item.Command);
            StringAssert.Contains(help.StdOut, "--machine-id", item.Command);
        }

        var runs = await db.WorkflowRuns.Where(r => r.LockboxId != null && boxes.Contains(r.LockboxId.Value)).ToListAsync();
        Assert.HasCount(0, runs);
        var rolloutHelp = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "swarm", "service", "rollout-status", "--help"]);
        var previousHelp = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "swarm", "service", "update-status", "--help"]);
        Assert.AreEqual(0, previousHelp.ProgramReturn, previousHelp.StdErr);
        Assert.AreEqual(rolloutHelp.StdOut, previousHelp.StdOut);
    }
}
