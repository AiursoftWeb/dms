using System.CommandLine;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;

namespace Aiursoft.DMS.Client;

public static partial class DmsCommandApp
{
    private static ActionCommand NewFilesystemLsCommand()
    {
        var path = new Option<string>("--path") { Required = true };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
        var depth = new Option<int>("--depth") { DefaultValueFactory = _ => 1 };
        return StandardCommand("ls",
            OperationsWorkflows.FilesystemLs,
            p => new ListPathInput(p.GetValue(path)!, p.GetValue(limit), p.GetValue(depth)),
            path,
            limit,
            depth);
    }

    private static ActionCommand NewFilesystemStatCommand()
    {
        var path = new Option<string>("--path") { Required = true };
        return StandardCommand("stat",
            OperationsWorkflows.FilesystemStat,
            p => new PathInput(p.GetValue(path)!),
            path);
    }

    private static ActionCommand NewFilesystemCatCommand()
    {
        var path = new Option<string>("--path") { Required = true };
        var lines = new Option<int>("--lines") { DefaultValueFactory = _ => 100 };
        var maxBytes = new Option<int>("--max-bytes") { DefaultValueFactory = _ => 65536 };
        return StandardCommand("cat",
            OperationsWorkflows.FilesystemCat,
            p => new TextFileInput(p.GetValue(path)!, p.GetValue(lines), p.GetValue(maxBytes)),
            path,
            lines,
            maxBytes);
    }

    private static ActionCommand NewFilesystemTailCommand()
    {
        var path = new Option<string>("--path") { Required = true };
        var lines = new Option<int>("--lines") { DefaultValueFactory = _ => 100 };
        var maxBytes = new Option<int>("--max-bytes") { DefaultValueFactory = _ => 65536 };
        return StandardCommand("tail",
            OperationsWorkflows.FilesystemTail,
            p => new TextFileInput(p.GetValue(path)!, p.GetValue(lines), p.GetValue(maxBytes)),
            path,
            lines,
            maxBytes);
    }

    private static ActionCommand NewFilesystemFindCommand()
    {
        var path = new Option<string>("--path") { Required = true };
        var name = new Option<string?>("--name") { DefaultValueFactory = _ => null };
        var minBytes = new Option<long?>("--min-bytes") { DefaultValueFactory = _ => null };
        var modifiedAfter = new Option<DateTimeOffset?>("--modified-after") { DefaultValueFactory = _ => null };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
        var depth = new Option<int>("--depth") { DefaultValueFactory = _ => 5 };
        return StandardCommand("find",
            OperationsWorkflows.FilesystemFind,
            p => new FindFilesInput(p.GetValue(path)!,
                p.GetValue(name),
                p.GetValue(minBytes),
                p.GetValue(modifiedAfter),
                p.GetValue(limit),
                p.GetValue(depth)),
            path,
            name,
            minBytes,
            modifiedAfter,
            limit,
            depth);
    }

    private static ActionCommand NewFilesystemMkdirCommand()
    {
        var path = new Option<string>("--path") { Required = true };
        var parents = new Option<bool>("--parents") { DefaultValueFactory = _ => false };
        var mode = new Option<int>("--mode") { DefaultValueFactory = _ => 448 };
        return StandardCommand("mkdir",
            OperationsWorkflows.FilesystemMkdir,
            p => new MakeDirectoryInput(p.GetValue(path)!, p.GetValue(parents), p.GetValue(mode)),
            path,
            parents,
            mode);
    }

    private static ActionCommand NewFilesystemCpCommand()
    {
        var source = new Option<string>("--source") { Required = true };
        var destination = new Option<string>("--destination") { Required = true };
        var recursive = new Option<bool>("--recursive") { DefaultValueFactory = _ => false };
        var overwrite = new Option<bool>("--overwrite") { DefaultValueFactory = _ => false };
        return StandardCommand("cp",
            OperationsWorkflows.FilesystemCp,
            p => new CopyPathInput(p.GetValue(source)!,
                p.GetValue(destination)!,
                p.GetValue(recursive),
                p.GetValue(overwrite)),
            source,
            destination,
            recursive,
            overwrite);
    }

    private static ActionCommand NewFilesystemMvCommand()
    {
        var source = new Option<string>("--source") { Required = true };
        var destination = new Option<string>("--destination") { Required = true };
        var recursive = new Option<bool>("--recursive") { DefaultValueFactory = _ => false };
        var overwrite = new Option<bool>("--overwrite") { DefaultValueFactory = _ => false };
        return StandardCommand("mv",
            OperationsWorkflows.FilesystemMv,
            p => new CopyPathInput(p.GetValue(source)!,
                p.GetValue(destination)!,
                p.GetValue(recursive),
                p.GetValue(overwrite)),
            source,
            destination,
            recursive,
            overwrite);
    }

    private static ActionCommand NewFilesystemExportCommand()
    {
        var path = new Option<string>("--path") { Required = true };
        var recursive = new Option<bool>("--recursive") { DefaultValueFactory = _ => false };
        var maxBytes = new Option<long>("--max-bytes") { DefaultValueFactory = _ => 268435456 };
        return StandardCommand("export",
            OperationsWorkflows.FilesystemExport,
            p => new ExportPathInput(p.GetValue(path)!, p.GetValue(recursive), p.GetValue(maxBytes)),
            path,
            recursive,
            maxBytes);
    }

    private static ActionCommand NewFilesystemImportCommand()
    {
        var artifactId = new Option<Guid>("--artifact-id") { Required = true };
        var path = new Option<string>("--path") { Required = true };
        var overwrite = new Option<bool>("--overwrite") { DefaultValueFactory = _ => false };
        return PreparedOperation("import",
            OperationsWorkflows.FilesystemImport,
            async (client, p, _) => new ImportPathInput(await client.Artifacts.GetAsync(p.GetValue(artifactId)),
                p.GetValue(path)!,
                p.GetValue(overwrite)),
            artifactId,
            path,
            overwrite);
    }

    private static ActionCommand NewLayoutFdiskQueryCommand()
    {
        return StandardCommand("fdisk-query",
            OperationsWorkflows.LayoutFdiskQuery,
            _ => new EmptyInput());
    }

    private static ActionCommand NewLayoutMountsCommand()
    {
        return StandardCommand("mounts",
            OperationsWorkflows.LayoutMounts,
            _ => new EmptyInput());
    }

    private static ActionCommand NewLayoutInodesCommand()
    {
        return StandardCommand("inodes",
            OperationsWorkflows.LayoutInodes,
            _ => new EmptyInput());
    }

    private static ActionCommand NewLayoutOpenDeletedCommand()
    {
        return StandardCommand("open-deleted",
            OperationsWorkflows.LayoutOpenDeleted,
            _ => new EmptyInput());
    }

    private static ActionCommand NewLayoutSmartCommand()
    {
        var device = new Option<string>("--device") { Required = true };
        return StandardCommand("smart",
            OperationsWorkflows.LayoutSmart,
            p => new DeviceInput(p.GetValue(device)!),
            device);
    }

    private static ActionCommand NewLayoutSmartTestCommand()
    {
        var device = new Option<string>("--device") { Required = true };
        var waitSeconds = new Option<int>("--wait-seconds") { DefaultValueFactory = _ => 120 };
        return StandardCommand("smart-test",
            OperationsWorkflows.LayoutSmartTest,
            p => new SmartTestInput(p.GetValue(device)!, p.GetValue(waitSeconds)),
            device,
            waitSeconds);
    }

    private static ActionCommand NewLayoutBcacheInfoCommand()
    {
        return StandardCommand("bcache-info",
            OperationsWorkflows.LayoutBcacheInfo,
            _ => new EmptyInput());
    }

    private static ActionCommand NewLayoutBcacheAuditCommand()
    {
        var baselinePath = new Option<string?>("--baseline-path") { DefaultValueFactory = _ => null };
        return StandardCommand("bcache-audit",
            OperationsWorkflows.LayoutBcacheAudit,
            p => new BaselineInput(p.GetValue(baselinePath)),
            baselinePath);
    }

    private static ActionCommand NewPerformanceProcessesCommand()
    {
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 50 };
        var sort = new Option<string>("--sort") { DefaultValueFactory = _ => "cpu" };
        return StandardCommand("processes",
            OperationsWorkflows.PerformanceProcesses,
            p => new ProcessListInput(p.GetValue(limit), p.GetValue(sort)!),
            limit,
            sort);
    }

    private static ActionCommand NewPerformanceCpuSampleCommand()
    {
        var seconds = new Option<int>("--seconds") { DefaultValueFactory = _ => 10 };
        var interval = new Option<int>("--interval") { DefaultValueFactory = _ => 1 };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 50 };
        return StandardCommand("cpu-sample",
            OperationsWorkflows.PerformanceCpuSample,
            p => new SampleInput(p.GetValue(seconds), p.GetValue(interval), p.GetValue(limit)),
            seconds,
            interval,
            limit);
    }

    private static ActionCommand NewPerformanceMemoryBreakdownCommand()
    {
        return StandardCommand("memory-breakdown",
            OperationsWorkflows.PerformanceMemoryBreakdown,
            _ => new EmptyInput());
    }

    private static ActionCommand NewPerformanceIoSampleCommand()
    {
        var seconds = new Option<int>("--seconds") { DefaultValueFactory = _ => 10 };
        var interval = new Option<int>("--interval") { DefaultValueFactory = _ => 1 };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 50 };
        return StandardCommand("io-sample",
            OperationsWorkflows.PerformanceIoSample,
            p => new SampleInput(p.GetValue(seconds), p.GetValue(interval), p.GetValue(limit)),
            seconds,
            interval,
            limit);
    }

    private static ActionCommand NewPerformanceDstatSampleCommand()
    {
        var seconds = new Option<int>("--seconds") { DefaultValueFactory = _ => 10 };
        var interval = new Option<int>("--interval") { DefaultValueFactory = _ => 1 };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 50 };
        return StandardCommand("dstat-sample",
            OperationsWorkflows.PerformanceDstatSample,
            p => new SampleInput(p.GetValue(seconds), p.GetValue(interval), p.GetValue(limit)),
            seconds,
            interval,
            limit);
    }

    private static ActionCommand NewPerformancePressureCommand()
    {
        return StandardCommand("pressure",
            OperationsWorkflows.PerformancePressure,
            _ => new EmptyInput());
    }

    private static ActionCommand NewPerformanceOomHistoryCommand()
    {
        var since = new Option<DateTimeOffset?>("--since") { DefaultValueFactory = _ => null };
        var until = new Option<DateTimeOffset?>("--until") { DefaultValueFactory = _ => null };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
        return StandardCommand("oom-history",
            OperationsWorkflows.PerformanceOomHistory,
            p => new EventWindowInput(p.GetValue(since), p.GetValue(until), p.GetValue(limit)),
            since,
            until,
            limit);
    }

    private static ActionCommand NewNvidiaSmiCommand()
    {
        return StandardCommand("smi",
            OperationsWorkflows.NvidiaSmi,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNvidiaProcessesCommand()
    {
        return StandardCommand("processes",
            OperationsWorkflows.NvidiaProcesses,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNvidiaSampleCommand()
    {
        var seconds = new Option<int>("--seconds") { DefaultValueFactory = _ => 10 };
        var interval = new Option<int>("--interval") { DefaultValueFactory = _ => 1 };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 50 };
        return StandardCommand("sample",
            OperationsWorkflows.NvidiaSample,
            p => new SampleInput(p.GetValue(seconds), p.GetValue(interval), p.GetValue(limit)),
            seconds,
            interval,
            limit);
    }

    private static ActionCommand NewNvidiaErrorsCommand()
    {
        return StandardCommand("errors",
            OperationsWorkflows.NvidiaErrors,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNvidiaTopologyCommand()
    {
        return StandardCommand("topology",
            OperationsWorkflows.NvidiaTopology,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNvidiaDriverInfoCommand()
    {
        return StandardCommand("driver-info",
            OperationsWorkflows.NvidiaDriverInfo,
            _ => new EmptyInput());
    }

    private static ActionCommand NewDockerInspectCommand()
    {
        var container = new Option<string>("--container") { Required = true };
        return StandardCommand("inspect",
            OperationsWorkflows.DockerInspect,
            p => new DockerContainerInput(p.GetValue(container)!),
            container);
    }

    private static ActionCommand NewDockerStatsSampleCommand()
    {
        var seconds = new Option<int>("--seconds") { DefaultValueFactory = _ => 10 };
        var interval = new Option<int>("--interval") { DefaultValueFactory = _ => 1 };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 50 };
        return StandardCommand("stats-sample",
            OperationsWorkflows.DockerStatsSample,
            p => new SampleInput(p.GetValue(seconds), p.GetValue(interval), p.GetValue(limit)),
            seconds,
            interval,
            limit);
    }

    private static ActionCommand NewDockerEventsCommand()
    {
        var container = new Option<string>("--container") { Required = true };
        var since = new Option<DateTimeOffset?>("--since") { DefaultValueFactory = _ => null };
        var until = new Option<DateTimeOffset?>("--until") { DefaultValueFactory = _ => null };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
        return StandardCommand("events",
            OperationsWorkflows.DockerEvents,
            p => new ContainerEventsInput(p.GetValue(container)!,
                p.GetValue(since),
                p.GetValue(until),
                p.GetValue(limit)),
            container,
            since,
            until,
            limit);
    }

    private static ActionCommand NewDockerDiagnoseStartupCommand()
    {
        var container = new Option<string>("--container") { Required = true };
        return StandardCommand("diagnose-startup",
            OperationsWorkflows.DockerDiagnoseStartup,
            p => new DockerContainerInput(p.GetValue(container)!),
            container);
    }

    private static ActionCommand NewDockerStopCommand()
    {
        var container = new Option<string>("--container") { Required = true };
        var timeoutSeconds = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 30 };
        return StandardCommand("stop",
            OperationsWorkflows.DockerStop,
            p => new ContainerActionInput(p.GetValue(container)!, p.GetValue(timeoutSeconds)),
            container,
            timeoutSeconds);
    }

    private static ActionCommand NewDockerStartCommand()
    {
        var container = new Option<string>("--container") { Required = true };
        return StandardCommand("start",
            OperationsWorkflows.DockerStart,
            p => new DockerContainerInput(p.GetValue(container)!),
            container);
    }

    private static ActionCommand NewDockerRestartCommand()
    {
        var container = new Option<string>("--container") { Required = true };
        var timeoutSeconds = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 30 };
        return StandardCommand("restart",
            OperationsWorkflows.DockerRestart,
            p => new ContainerActionInput(p.GetValue(container)!, p.GetValue(timeoutSeconds)),
            container,
            timeoutSeconds);
    }

    private static ActionCommand NewDockerFilesystemExportCommand()
    {
        var container = new Option<string>("--container") { Required = true };
        var path = new Option<string>("--path") { Required = true };
        var recursive = new Option<bool>("--recursive") { DefaultValueFactory = _ => false };
        var maxBytes = new Option<long>("--max-bytes") { DefaultValueFactory = _ => 268435456 };
        return StandardCommand("export",
            OperationsWorkflows.DockerFilesystemExport,
            p => new ContainerExportInput(p.GetValue(container)!,
                p.GetValue(path)!,
                p.GetValue(recursive),
                p.GetValue(maxBytes)),
            container,
            path,
            recursive,
            maxBytes);
    }

    private static ActionCommand NewDockerFilesystemCpToCommand()
    {
        var container = new Option<string>("--container") { Required = true };
        var source = new Option<string>("--source") { Required = true };
        var destination = new Option<string>("--destination") { Required = true };
        var recursive = new Option<bool>("--recursive") { DefaultValueFactory = _ => false };
        var overwrite = new Option<bool>("--overwrite") { DefaultValueFactory = _ => false };
        return StandardCommand("cp-to",
            OperationsWorkflows.DockerFilesystemCpTo,
            p => new ContainerCopyInput(p.GetValue(container)!,
                p.GetValue(source)!,
                p.GetValue(destination)!,
                p.GetValue(recursive),
                p.GetValue(overwrite)),
            container,
            source,
            destination,
            recursive,
            overwrite);
    }

    private static ActionCommand NewDockerFilesystemCpFromCommand()
    {
        var container = new Option<string>("--container") { Required = true };
        var source = new Option<string>("--source") { Required = true };
        var destination = new Option<string>("--destination") { Required = true };
        var recursive = new Option<bool>("--recursive") { DefaultValueFactory = _ => false };
        var overwrite = new Option<bool>("--overwrite") { DefaultValueFactory = _ => false };
        return StandardCommand("cp-from",
            OperationsWorkflows.DockerFilesystemCpFrom,
            p => new ContainerCopyInput(p.GetValue(container)!,
                p.GetValue(source)!,
                p.GetValue(destination)!,
                p.GetValue(recursive),
                p.GetValue(overwrite)),
            container,
            source,
            destination,
            recursive,
            overwrite);
    }

    private static ActionCommand NewSwarmServiceListCommand()
    {
        return StandardCommand("list",
            OperationsWorkflows.SwarmServiceList,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSwarmServiceInspectCommand()
    {
        var service = new Option<string>("--service") { Required = true };
        return StandardCommand("inspect",
            OperationsWorkflows.SwarmServiceInspect,
            p => new SwarmServiceInput(p.GetValue(service)!),
            service);
    }

    private static ActionCommand NewSwarmServicePsCommand()
    {
        var service = new Option<string>("--service") { Required = true };
        return StandardCommand("ps",
            OperationsWorkflows.SwarmServicePs,
            p => new SwarmServiceInput(p.GetValue(service)!),
            service);
    }

    private static ActionCommand NewSwarmServiceLogsCommand()
    {
        var service = new Option<string>("--service") { Required = true };
        var lines = new Option<int>("--lines") { DefaultValueFactory = _ => 100 };
        var since = new Option<DateTimeOffset?>("--since") { DefaultValueFactory = _ => null };
        return StandardCommand("logs",
            OperationsWorkflows.SwarmServiceLogs,
            p => new SwarmLogsInput(p.GetValue(service)!, p.GetValue(lines), p.GetValue(since)),
            service,
            lines,
            since);
    }

    private static ActionCommand NewSwarmServiceUpdateStatusCommand()
    {
        var service = new Option<string>("--service") { Required = true };
        return StandardCommand("update-status",
            OperationsWorkflows.SwarmServiceUpdateStatus,
            p => new SwarmServiceInput(p.GetValue(service)!),
            service);
    }

    private static ActionCommand NewSwarmServiceStopCommand()
    {
        var service = new Option<string>("--service") { Required = true };
        var expectedVersion = new Option<long>("--expected-version") { Required = true };
        var nodeId = new Option<string[]>("--node-id") { Required = true, AllowMultipleArgumentsPerToken = true };
        var timeoutSeconds = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 120 };
        return StandardCommand("stop",
            OperationsWorkflows.SwarmServiceStop,
            p => new SwarmChangeInput(p.GetValue(service)!,
                p.GetValue(expectedVersion),
                p.GetValue(nodeId)!,
                TimeoutSeconds: p.GetValue(timeoutSeconds)),
            service,
            expectedVersion,
            nodeId,
            timeoutSeconds);
    }

    private static ActionCommand NewSwarmServiceStartCommand()
    {
        var service = new Option<string>("--service") { Required = true };
        var expectedVersion = new Option<long>("--expected-version") { Required = true };
        var nodeId = new Option<string[]>("--node-id") { Required = true, AllowMultipleArgumentsPerToken = true };
        var replicas = new Option<int>("--replicas") { DefaultValueFactory = _ => 1 };
        var timeoutSeconds = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 120 };
        return StandardCommand("start",
            OperationsWorkflows.SwarmServiceStart,
            p => new SwarmChangeInput(p.GetValue(service)!,
                p.GetValue(expectedVersion),
                p.GetValue(nodeId)!,
                Replicas: p.GetValue(replicas),
                TimeoutSeconds: p.GetValue(timeoutSeconds)),
            service,
            expectedVersion,
            nodeId,
            replicas,
            timeoutSeconds);
    }

    private static ActionCommand NewSwarmServiceUpdateCommand()
    {
        var service = new Option<string>("--service") { Required = true };
        var expectedVersion = new Option<long>("--expected-version") { Required = true };
        var nodeId = new Option<string[]>("--node-id") { Required = true, AllowMultipleArgumentsPerToken = true };
        var image = new Option<string>("--image") { Required = true };
        var timeoutSeconds = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 120 };
        return StandardCommand("update",
            OperationsWorkflows.SwarmServiceUpdate,
            p => new SwarmChangeInput(p.GetValue(service)!,
                p.GetValue(expectedVersion),
                p.GetValue(nodeId)!,
                Image: p.GetValue(image),
                TimeoutSeconds: p.GetValue(timeoutSeconds)),
            service,
            expectedVersion,
            nodeId,
            image,
            timeoutSeconds);
    }

    private static ActionCommand NewSwarmServiceRollbackCommand()
    {
        var service = new Option<string>("--service") { Required = true };
        var expectedVersion = new Option<long>("--expected-version") { Required = true };
        var nodeId = new Option<string[]>("--node-id") { Required = true, AllowMultipleArgumentsPerToken = true };
        var timeoutSeconds = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 120 };
        return StandardCommand("rollback",
            OperationsWorkflows.SwarmServiceRollback,
            p => new SwarmChangeInput(p.GetValue(service)!,
                p.GetValue(expectedVersion),
                p.GetValue(nodeId)!,
                TimeoutSeconds: p.GetValue(timeoutSeconds)),
            service,
            expectedVersion,
            nodeId,
            timeoutSeconds);
    }

    private static ActionCommand NewSystemInfoCommand()
    {
        return StandardCommand("info",
            OperationsWorkflows.SystemInfo,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSystemUptimeCommand()
    {
        return StandardCommand("uptime",
            OperationsWorkflows.SystemUptime,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSystemctlListCommand()
    {
        var failedOnly = new Option<bool>("--failed-only") { DefaultValueFactory = _ => false };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
        return StandardCommand("list",
            OperationsWorkflows.SystemctlList,
            p => new ServiceListInput(p.GetValue(failedOnly), p.GetValue(limit)),
            failedOnly,
            limit);
    }

    private static ActionCommand NewSystemctlStartCommand()
    {
        var service = new Option<string>("--service") { Required = true };
        return StandardCommand("start",
            OperationsWorkflows.SystemctlStart,
            p => new ServiceInput(p.GetValue(service)!),
            service);
    }

    private static ActionCommand NewSystemctlStopCommand()
    {
        var service = new Option<string>("--service") { Required = true };
        return StandardCommand("stop",
            OperationsWorkflows.SystemctlStop,
            p => new ServiceInput(p.GetValue(service)!),
            service);
    }

    private static ActionCommand NewSystemJournalCommand()
    {
        var service = new Option<string?>("--service");
        var boot = new Option<string?>("--boot");
        var level = new Option<string?>("--level");
        var since = new Option<DateTimeOffset?>("--since");
        var until = new Option<DateTimeOffset?>("--until");
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
        var kernel = new Option<bool>("--kernel");
        var cursor = new Option<string?>("--before-cursor")
            { Description = "Continue before the last returned cursor; results are newest first." };
        return StandardCommand("journal",
            OperationsWorkflows.SystemJournal,
            p => new JournalInput(p.GetValue(service),
                p.GetValue(boot),
                p.GetValue(level),
                p.GetValue(since),
                p.GetValue(until),
                p.GetValue(limit),
                p.GetValue(kernel),
                p.GetValue(cursor)),
            service,
            boot,
            level,
            since,
            until,
            limit,
            kernel,
            cursor);
    }

    private static ActionCommand NewSystemTimersCommand()
    {
        return StandardCommand("timers",
            OperationsWorkflows.SystemTimers,
            _ => new EmptyInput());
    }

    private static ActionCommand NewAptInspectCommand()
    {
        return StandardCommand("inspect",
            OperationsWorkflows.AptInspect,
            _ => new EmptyInput());
    }

    private static ActionCommand NewAptInstallCommand()
    {
        var package = new Option<string[]>("--package") { Required = true, AllowMultipleArgumentsPerToken = true };
        return StandardCommand("install",
            OperationsWorkflows.AptInstall,
            p => new PackageInput(p.GetValue(package)!),
            package);
    }

    private static ActionCommand NewAptUpgradeCommand()
    {
        var package = new Option<string[]>("--package") { Required = true, AllowMultipleArgumentsPerToken = true };
        return StandardCommand("upgrade",
            OperationsWorkflows.AptUpgrade,
            p => new PackageUpgradeInput(p.GetValue(package)!),
            package);
    }

    private static ActionCommand NewSystemRebootCommand()
    {
        var delaySeconds = new Option<int>("--delay-seconds") { DefaultValueFactory = _ => 15 };
        return StandardCommand("reboot",
            OperationsWorkflows.SystemReboot,
            p => new RebootInput(p.GetValue(delaySeconds)),
            delaySeconds);
    }

    private static ActionCommand NewSystemExecuteScriptCommand()
    {
        var language = new Option<string>("--language") { Required = true };
        var scriptFile = new Option<string>("--script-file") { Required = true };
        var timeoutSeconds = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 60 };
        return PreparedOperation("execute-script",
            OperationsWorkflows.SystemExecuteScript,
            async (client, p, requestId) => new ScriptInput(p.GetValue(language)!,
                await PrepareOperationTextAsync(client, p.GetValue(scriptFile)!, 262144, requestId),
                p.GetValue(timeoutSeconds)),
            language,
            scriptFile,
            timeoutSeconds);
    }

    private static ActionCommand NewDatabaseDiscoverCommand()
    {
        return StandardCommand("discover",
            OperationsWorkflows.DatabaseDiscover,
            _ => new EmptyInput());
    }

    private static ActionCommand NewDatabaseListDatabasesCommand()
    {
        var connection = new Option<string>("--connection") { Required = true };
        return StandardCommand("list-databases",
            OperationsWorkflows.DatabaseListDatabases,
            p => new DatabaseInput(p.GetValue(connection)!),
            connection);
    }

    private static ActionCommand NewDatabaseListTablesCommand()
    {
        var connection = new Option<string>("--connection") { Required = true };
        var database = new Option<string>("--database") { Required = true };
        return StandardCommand("list-tables",
            OperationsWorkflows.DatabaseListTables,
            p => new DatabaseNameInput(p.GetValue(connection)!, p.GetValue(database)!),
            connection,
            database);
    }

    private static ActionCommand NewDatabaseDescribeTableCommand()
    {
        var connection = new Option<string>("--connection") { Required = true };
        var database = new Option<string>("--database") { Required = true };
        var table = new Option<string>("--table") { Required = true };
        return StandardCommand("describe-table",
            OperationsWorkflows.DatabaseDescribeTable,
            p => new DatabaseTableInput(p.GetValue(connection)!, p.GetValue(database)!, p.GetValue(table)!),
            connection,
            database,
            table);
    }

    private static ActionCommand NewDatabaseQueryTableCommand()
    {
        var connection = new Option<string>("--connection") { Required = true };
        var database = new Option<string>("--database") { Required = true };
        var table = new Option<string>("--table") { Required = true };
        var column = new Option<string[]>("--column") { Required = true, AllowMultipleArgumentsPerToken = true };
        var filters = new Option<string?>("--filters") { DefaultValueFactory = _ => null };
        var sortBy = new Option<string?>("--sort-by") { DefaultValueFactory = _ => null };
        var descending = new Option<bool>("--descending") { DefaultValueFactory = _ => false };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 100 };
        var offset = new Option<int>("--offset") { DefaultValueFactory = _ => 0 };
        return StandardCommand("query-table",
            OperationsWorkflows.DatabaseQueryTable,
            p => new DatabaseQueryInput(p.GetValue(connection)!,
                p.GetValue(database)!,
                p.GetValue(table)!,
                p.GetValue(column)!,
                ReadFilters(p.GetValue(filters)),
                p.GetValue(sortBy),
                p.GetValue(descending),
                p.GetValue(limit),
                p.GetValue(offset)),
            connection,
            database,
            table,
            column,
            filters,
            sortBy,
            descending,
            limit,
            offset);
    }

    private static ActionCommand NewDatabaseQuerySqlCommand()
    {
        var connection = new Option<string>("--connection") { Required = true };
        var sqlFile = new Option<string>("--sql-file") { Required = true };
        var timeoutSeconds = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 30 };
        var maxRows = new Option<int>("--max-rows") { DefaultValueFactory = _ => 1000 };
        return PreparedOperation("query-sql",
            OperationsWorkflows.DatabaseQuerySql,
            async (client, p, requestId) => new DatabaseSqlInput(p.GetValue(connection)!,
                await PrepareOperationTextAsync(client, p.GetValue(sqlFile)!, 131072, requestId),
                p.GetValue(timeoutSeconds),
                p.GetValue(maxRows)),
            connection,
            sqlFile,
            timeoutSeconds,
            maxRows);
    }

    private static ActionCommand NewDatabaseExecuteSqlCommand()
    {
        var connection = new Option<string>("--connection") { Required = true };
        var sqlFile = new Option<string>("--sql-file") { Required = true };
        var timeoutSeconds = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 30 };
        var maxRows = new Option<int>("--max-rows") { DefaultValueFactory = _ => 1000 };
        return PreparedOperation("execute-sql",
            OperationsWorkflows.DatabaseExecuteSql,
            async (client, p, requestId) => new DatabaseSqlInput(p.GetValue(connection)!,
                await PrepareOperationTextAsync(client, p.GetValue(sqlFile)!, 131072, requestId),
                p.GetValue(timeoutSeconds),
                p.GetValue(maxRows)),
            connection,
            sqlFile,
            timeoutSeconds,
            maxRows);
    }

    private static ActionCommand NewDatabaseDiagnosticsCommand()
    {
        var connection = new Option<string>("--connection") { Required = true };
        return StandardCommand("diagnostics",
            OperationsWorkflows.DatabaseDiagnostics,
            p => new DatabaseInput(p.GetValue(connection)!),
            connection);
    }

    private static ActionCommand NewNetworkingInterfacesCommand()
    {
        return StandardCommand("interfaces",
            OperationsWorkflows.NetworkingInterfaces,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNetworkingRoutesCommand()
    {
        return StandardCommand("routes",
            OperationsWorkflows.NetworkingRoutes,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNetworkingSocketsCommand()
    {
        return StandardCommand("sockets",
            OperationsWorkflows.NetworkingSockets,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNetworkingDnsQueryCommand()
    {
        var host = new Option<string>("--host") { Required = true };
        return StandardCommand("dns-query",
            OperationsWorkflows.NetworkingDnsQuery,
            p => new DnsQueryInput(p.GetValue(host)!),
            host);
    }

    private static ActionCommand NewNetworkingProbeCommand()
    {
        var host = new Option<string>("--host") { Required = true };
        var port = new Option<int>("--port") { DefaultValueFactory = _ => 443 };
        var url = new Option<string?>("--url") { DefaultValueFactory = _ => null };
        var timeoutSeconds = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 10 };
        return StandardCommand("probe",
            OperationsWorkflows.NetworkingProbe,
            p => new NetworkProbeInput(p.GetValue(host)!,
                p.GetValue(port),
                p.GetValue(url),
                p.GetValue(timeoutSeconds)),
            host,
            port,
            url,
            timeoutSeconds);
    }

    private static ActionCommand NewNetworkingTrafficSampleCommand()
    {
        var seconds = new Option<int>("--seconds") { DefaultValueFactory = _ => 10 };
        var interval = new Option<int>("--interval") { DefaultValueFactory = _ => 1 };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 50 };
        return StandardCommand("traffic-sample",
            OperationsWorkflows.NetworkingTrafficSample,
            p => new SampleInput(p.GetValue(seconds), p.GetValue(interval), p.GetValue(limit)),
            seconds,
            interval,
            limit);
    }

    private static ActionCommand NewNetworkingFirewallInfoCommand()
    {
        return StandardCommand("firewall-info",
            OperationsWorkflows.NetworkingFirewallInfo,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNetworkingTlsCheckCommand()
    {
        var host = new Option<string>("--host") { Required = true };
        var port = new Option<int>("--port") { DefaultValueFactory = _ => 443 };
        var url = new Option<string?>("--url") { DefaultValueFactory = _ => null };
        var timeoutSeconds = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 10 };
        return StandardCommand("tls-check",
            OperationsWorkflows.NetworkingTlsCheck,
            p => new NetworkProbeInput(p.GetValue(host)!,
                p.GetValue(port),
                p.GetValue(url),
                p.GetValue(timeoutSeconds)),
            host,
            port,
            url,
            timeoutSeconds);
    }

    private static ActionCommand NewSecuritySecureBootCommand()
    {
        return StandardCommand("secure-boot",
            OperationsWorkflows.SecuritySecureBoot,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSecurityDiskEncryptionCommand()
    {
        return StandardCommand("disk-encryption",
            OperationsWorkflows.SecurityDiskEncryption,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSecuritySshAuditCommand()
    {
        var user = new Option<string>("--user") { DefaultValueFactory = _ => "root" };
        var address = new Option<string>("--address") { DefaultValueFactory = _ => "127.0.0.1" };
        var host = new Option<string>("--host") { DefaultValueFactory = _ => "localhost" };
        return StandardCommand("ssh-audit",
            OperationsWorkflows.SecuritySshAudit,
            p => new SshAuditInput(p.GetValue(user)!, p.GetValue(address)!, p.GetValue(host)!),
            user,
            address,
            host);
    }

    private static ActionCommand NewSecurityFail2banStatusCommand()
    {
        return StandardCommand("fail2ban-status",
            OperationsWorkflows.SecurityFail2banStatus,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSecurityCleanTrafficStatusCommand()
    {
        return StandardCommand("clean-traffic-status",
            OperationsWorkflows.SecurityCleanTrafficStatus,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSecurityAuthEventsCommand()
    {
        var since = new Option<DateTimeOffset?>("--since") { DefaultValueFactory = _ => null };
        var until = new Option<DateTimeOffset?>("--until") { DefaultValueFactory = _ => null };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
        return StandardCommand("auth-events",
            OperationsWorkflows.SecurityAuthEvents,
            p => new EventWindowInput(p.GetValue(since), p.GetValue(until), p.GetValue(limit)),
            since,
            until,
            limit);
    }

    private static ActionCommand NewSecurityAccountsAuditCommand()
    {
        return StandardCommand("accounts-audit",
            OperationsWorkflows.SecurityAccountsAudit,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSecurityBaselineAuditCommand()
    {
        var baselinePath = new Option<string?>("--baseline-path") { DefaultValueFactory = _ => null };
        return StandardCommand("baseline-audit",
            OperationsWorkflows.SecurityBaselineAudit,
            p => new BaselineInput(p.GetValue(baselinePath)),
            baselinePath);
    }

    private static ActionCommand PreparedOperation<TInput>(string name,
        WorkflowDefinition<TInput, OperationResult> definition,
        Func<DmsApiClient, ParseResult, Guid, Task<TInput>> prepare,
        params Option[] options) =>
        new(name,
            definition.Name,
            definition,
            options: options,
            preparedInput: async (client, context, requestId) =>
                definition.Serialize(await prepare(client, context, requestId)));

    private static async Task<ArtifactReference> PrepareOperationTextAsync(DmsApiClient client,
        string file,
        int maximum,
        Guid requestId)
    {
        if (new FileInfo(file).Length > maximum)
            throw new ArgumentException("The input file exceeds its operation limit.");
        return await client.Artifacts.UploadAsync(file, Path.GetFileName(file), "file", requestId: requestId);
    }

    private static DatabaseFilter[]? ReadFilters(string? json) => json == null
        ? null
        : JsonSerializer.Deserialize<DatabaseFilter[]>(json)
          ?? throw new ArgumentException("Filters must be a JSON array of typed column/operator/value objects.");
}
