using System.CommandLine;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Client;

public static partial class DmsCommandApp
{
    private static ActionCommand NewFilesystemLsCommand()
    {
        var path = new Option<string>("--path") { Required = true, Description = "Absolute directory path to list." };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 200, Description = "Maximum number of entries to return." };
        var depth = new Option<int>("--depth")
            { DefaultValueFactory = _ => 1, Description = "Maximum number of directory levels to list." };
        return StandardCommand("ls",
            "Request a directory listing on the selected machines. The receipt shows how to follow approval and view the listing.",
            OperationsWorkflows.FilesystemLs,
            p => new ListPathInput(p.GetValue(path)!, p.GetValue(limit), p.GetValue(depth)),
            path,
            limit,
            depth);
    }

    private static ActionCommand NewFilesystemStatCommand()
    {
        var path = new Option<string>("--path")
            { Required = true, Description = "Absolute path of the file or directory to inspect." };
        return StandardCommand("stat",
            "Read a file's type, size, ownership, permissions, and timestamps.",
            OperationsWorkflows.FilesystemStat,
            p => new PathInput(p.GetValue(path)!),
            path);
    }

    private static ActionCommand NewFilesystemCatCommand(bool asRoot = false)
    {
        var path = new Option<string>("--path")
            { Required = true, Description = "Absolute path on the selected machine." };
        var lines = new Option<int>("--lines")
            { DefaultValueFactory = _ => 100, Description = "Maximum number of lines to read from the beginning." };
        var maxBytes = new Option<int>("--max-bytes")
            { DefaultValueFactory = _ => 65536, Description = "Maximum number of file bytes to read." };
        return StandardCommand(asRoot ? "cat-as-root" : "cat",
            asRoot ? "Read the first lines of a text file with Agent privileges." : "Read the first lines of a text file readable by other users.",
            asRoot ? OperationsWorkflows.FilesystemCatAsRoot : OperationsWorkflows.FilesystemCat,
            p => new TextFileInput(p.GetValue(path)!, p.GetValue(lines), p.GetValue(maxBytes)),
            path,
            lines,
            maxBytes);
    }

    private static ActionCommand NewFilesystemTailCommand()
    {
        var path = new Option<string>("--path")
            { Required = true, Description = "Absolute path on the selected machine." };
        var lines = new Option<int>("--lines")
            { DefaultValueFactory = _ => 100, Description = "Maximum number of lines to read from the end." };
        var maxBytes = new Option<int>("--max-bytes")
            { DefaultValueFactory = _ => 65536, Description = "Maximum number of file bytes to read." };
        return StandardCommand("tail",
            "Read the last lines of a text file.",
            OperationsWorkflows.FilesystemTail,
            p => new TextFileInput(p.GetValue(path)!, p.GetValue(lines), p.GetValue(maxBytes)),
            path,
            lines,
            maxBytes);
    }

    private static ActionCommand NewFilesystemFindCommand()
    {
        var path = new Option<string>("--path")
            { Required = true, Description = "Absolute directory path where the search begins." };
        var name = new Option<string?>("--name")
            { DefaultValueFactory = _ => null, Description = "Case-sensitive name pattern, such as *.log." };
        var minBytes = new Option<long?>("--min-bytes")
            { DefaultValueFactory = _ => null, Description = "Minimum file size in bytes." };
        var modifiedAfter = new Option<DateTimeOffset?>("--modified-after")
        {
            DefaultValueFactory = _ => null,
            Description = "Only files modified after this ISO 8601 time with a UTC offset."
        };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 200, Description = "Maximum number of matches to return." };
        var depth = new Option<int>("--depth")
            { DefaultValueFactory = _ => 5, Description = "Maximum directory depth to search." };
        return StandardCommand("find",
            "Find files by name, size, or modification time.",
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
        var path = new Option<string>("--path")
            { Required = true, Description = "Absolute path for the new directory." };
        var parents = new Option<bool>("--parents")
            { DefaultValueFactory = _ => false, Description = "Create missing parent directories." };
        var mode = new Option<int>("--mode")
            { DefaultValueFactory = _ => 448, Description = "Directory mode in decimal (448 is octal 0700)." };
        return StandardCommand("mkdir",
            "Create a directory on a machine.",
            OperationsWorkflows.FilesystemMkdir,
            p => new MakeDirectoryInput(p.GetValue(path)!, p.GetValue(parents), p.GetValue(mode)),
            path,
            parents,
            mode);
    }

    private static ActionCommand NewFilesystemCpCommand()
    {
        var source = new Option<string>("--source")
            { Required = true, Description = "Absolute source path on the machine." };
        var destination = new Option<string>("--destination")
            { Required = true, Description = "Absolute destination path on the machine." };
        var recursive = new Option<bool>("--recursive")
            { DefaultValueFactory = _ => false, Description = "Include directory contents." };
        var overwrite = new Option<bool>("--overwrite")
            { DefaultValueFactory = _ => false, Description = "Replace an existing destination." };
        return StandardCommand("cp",
            "Copy a file or directory on the same machine.",
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
        var source = new Option<string>("--source")
            { Required = true, Description = "Absolute source path on the machine." };
        var destination = new Option<string>("--destination")
            { Required = true, Description = "Absolute destination path on the machine." };
        var recursive = new Option<bool>("--recursive")
            { DefaultValueFactory = _ => false, Description = "Include directory contents." };
        var overwrite = new Option<bool>("--overwrite")
            { DefaultValueFactory = _ => false, Description = "Replace an existing destination." };
        return StandardCommand("mv",
            "Move or rename a file or directory on the same machine.",
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
        var path = new Option<string>("--path")
            { Required = true, Description = "Absolute path of the machine file or directory to export." };
        var recursive = new Option<bool>("--recursive")
            { DefaultValueFactory = _ => false, Description = "Include directory contents." };
        var maxBytes = new Option<long>("--max-bytes")
            { DefaultValueFactory = _ => 268435456, Description = "Maximum size of the exported artifact in bytes." };
        return StandardCommand("export",
            "Export a machine file or directory as a downloadable artifact.",
            OperationsWorkflows.FilesystemExport,
            p => new ExportPathInput(p.GetValue(path)!, p.GetValue(recursive), p.GetValue(maxBytes)),
            path,
            recursive,
            maxBytes);
    }

    private static ActionCommand NewFilesystemImportCommand()
    {
        var artifactId = new Option<Guid>("--artifact-id")
            { Required = true, Description = "ID returned by dms artifact upload." };
        var path = new Option<string>("--path")
            { Required = true, Description = "Absolute destination path on the selected machine." };
        var overwrite = new Option<bool>("--overwrite")
            { DefaultValueFactory = _ => false, Description = "Replace an existing destination." };
        return PreparedOperation("import",
            "Write an uploaded artifact to a path on the machine.",
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
            "Read partition tables and disk geometry.",
            OperationsWorkflows.LayoutFdiskQuery,
            _ => new EmptyInput());
    }

    private static ActionCommand NewLayoutMountsCommand()
    {
        return StandardCommand("mounts",
            "Show mounted filesystems, sources, and mount options.",
            OperationsWorkflows.LayoutMounts,
            _ => new EmptyInput());
    }

    private static ActionCommand NewLayoutInodesCommand()
    {
        return StandardCommand("inodes",
            "Show used and available inodes on mounted filesystems.",
            OperationsWorkflows.LayoutInodes,
            _ => new EmptyInput());
    }

    private static ActionCommand NewLayoutOpenDeletedCommand()
    {
        return StandardCommand("open-deleted",
            "Find deleted files that processes still hold open.",
            OperationsWorkflows.LayoutOpenDeleted,
            _ => new EmptyInput());
    }

    private static ActionCommand NewLayoutSmartCommand()
    {
        var device = new Option<string>("--device")
            { Required = true, Description = "Device path, such as /dev/sda or /dev/nvme0." };
        return StandardCommand("smart",
            "Read a disk's SMART health and error information.",
            OperationsWorkflows.LayoutSmart,
            p => new DeviceInput(p.GetValue(device)!),
            device);
    }

    private static ActionCommand NewLayoutSmartTestCommand()
    {
        var device = new Option<string>("--device")
            { Required = true, Description = "Device path, such as /dev/sda or /dev/nvme0." };
        var waitSeconds = new Option<int>("--wait-seconds")
            { DefaultValueFactory = _ => 120, Description = "How long to wait for the self-test result, in seconds." };
        return StandardCommand("smart-test",
            "Start a short SMART self-test and check its progress.",
            OperationsWorkflows.LayoutSmartTest,
            p => new SmartTestInput(p.GetValue(device)!, p.GetValue(waitSeconds)),
            device,
            waitSeconds);
    }

    private static ActionCommand NewLayoutBcacheInfoCommand()
    {
        return StandardCommand("bcache-info",
            "Show bcache devices, modes, and counters.",
            OperationsWorkflows.LayoutBcacheInfo,
            _ => new EmptyInput());
    }

    private static ActionCommand NewLayoutBcacheAuditCommand()
    {
        var baselinePath = new Option<string?>("--baseline-path")
        {
            DefaultValueFactory = _ => null,
            Description = "Absolute path to the baseline file on the selected machine."
        };
        return StandardCommand("bcache-audit",
            "Compare the machine's bcache configuration with a baseline.",
            OperationsWorkflows.LayoutBcacheAudit,
            p => new BaselineInput(p.GetValue(baselinePath)),
            baselinePath);
    }

    private static ActionCommand NewPerformanceProcessesCommand()
    {
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 50, Description = "Maximum number of processes to return." };
        var sort = new Option<string>("--sort")
            { DefaultValueFactory = _ => "cpu", Description = "Sort by cpu, memory, or pid." };
        return StandardCommand("processes",
            "List processes sorted by CPU, memory, or process ID.",
            OperationsWorkflows.PerformanceProcesses,
            p => new ProcessListInput(p.GetValue(limit), p.GetValue(sort)!),
            limit,
            sort);
    }

    private static ActionCommand NewPerformanceCpuSampleCommand()
    {
        var seconds = new Option<int>("--seconds")
            { DefaultValueFactory = _ => 10, Description = "Total sampling time in seconds (1 to 300)." };
        var interval = new Option<int>("--interval")
        {
            DefaultValueFactory = _ => 1,
            Description = "Seconds between samples. Use at least 1 and at most the total sampling time."
        };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 50, Description = "Compatibility option. All available cores are sampled." };
        return StandardCommand("cpu-sample",
            "Sample CPU utilization, I/O wait, and steal time by core.",
            OperationsWorkflows.PerformanceCpuSample,
            p => new SampleInput(p.GetValue(seconds), p.GetValue(interval), p.GetValue(limit)),
            seconds,
            interval,
            limit);
    }

    private static ActionCommand NewPerformanceMemoryBreakdownCommand()
    {
        return StandardCommand("memory-breakdown",
            "Break down RAM, swap, and memory usage by process.",
            OperationsWorkflows.PerformanceMemoryBreakdown,
            _ => new EmptyInput());
    }

    private static ActionCommand NewPerformanceIoSampleCommand()
    {
        var seconds = new Option<int>("--seconds")
            { DefaultValueFactory = _ => 10, Description = "Total sampling time in seconds (1 to 300)." };
        var interval = new Option<int>("--interval")
        {
            DefaultValueFactory = _ => 1,
            Description = "Seconds between samples. Use at least 1 and at most the total sampling time."
        };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 50, Description = "Maximum number of processes ranked by I/O per sample." };
        return StandardCommand("io-sample",
            "Sample block-device I/O over time.",
            OperationsWorkflows.PerformanceIoSample,
            p => new SampleInput(p.GetValue(seconds), p.GetValue(interval), p.GetValue(limit)),
            seconds,
            interval,
            limit);
    }

    private static ActionCommand NewPerformanceDstatSampleCommand()
    {
        var seconds = new Option<int>("--seconds")
            { DefaultValueFactory = _ => 10, Description = "Total sampling time in seconds (1 to 300)." };
        var interval = new Option<int>("--interval")
        {
            DefaultValueFactory = _ => 1,
            Description = "Seconds between samples. Use at least 1 and at most the total sampling time."
        };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 50, Description = "Compatibility option. All disks and interfaces are sampled." };
        return StandardCommand("dstat-sample",
            "Sample CPU, memory, disk, and network activity together.",
            OperationsWorkflows.PerformanceDstatSample,
            p => new SampleInput(p.GetValue(seconds), p.GetValue(interval), p.GetValue(limit)),
            seconds,
            interval,
            limit);
    }

    private static ActionCommand NewPerformancePressureCommand()
    {
        return StandardCommand("pressure",
            "Read CPU, memory, and I/O stall metrics.",
            OperationsWorkflows.PerformancePressure,
            _ => new EmptyInput());
    }

    private static ActionCommand NewPerformanceOomHistoryCommand()
    {
        var since = new Option<DateTimeOffset?>("--since")
        {
            DefaultValueFactory = _ => null,
            Description = "Beginning of the time window in ISO 8601 with a UTC offset."
        };
        var until = new Option<DateTimeOffset?>("--until")
            { DefaultValueFactory = _ => null, Description = "End of the time window in ISO 8601 with a UTC offset." };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 200, Description = "Maximum number of OOM events to return." };
        return StandardCommand("oom-history",
            "Find recent out-of-memory and OOM-kill events.",
            OperationsWorkflows.PerformanceOomHistory,
            p => new EventWindowInput(p.GetValue(since), p.GetValue(until), p.GetValue(limit)),
            since,
            until,
            limit);
    }

    private static ActionCommand NewNvidiaSmiCommand()
    {
        return StandardCommand("smi",
            "Show NVIDIA GPU usage, memory, temperature, and power.",
            OperationsWorkflows.NvidiaSmi,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNvidiaOverviewCommand()
    {
        return StandardCommand("overview",
            "Show NVIDIA GPUs, driver, memory, active processes, and topology in one trace.",
            OperationsWorkflows.NvidiaOverview,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNvidiaProcessesCommand()
    {
        return StandardCommand("processes",
            "List processes using NVIDIA GPUs.",
            OperationsWorkflows.NvidiaProcesses,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNvidiaSampleCommand()
    {
        var seconds = new Option<int>("--seconds")
            { DefaultValueFactory = _ => 10, Description = "Total sampling time in seconds (1 to 300)." };
        var interval = new Option<int>("--interval")
        {
            DefaultValueFactory = _ => 1,
            Description = "Seconds between samples. Use at least 1 and at most the total sampling time."
        };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 50, Description = "Compatibility option. All detected GPUs are sampled." };
        return StandardCommand("sample",
            "Sample NVIDIA GPU utilization and memory over time.",
            OperationsWorkflows.NvidiaSample,
            p => new SampleInput(p.GetValue(seconds), p.GetValue(interval), p.GetValue(limit)),
            seconds,
            interval,
            limit);
    }

    private static ActionCommand NewNvidiaErrorsCommand()
    {
        return StandardCommand("errors",
            "Read NVIDIA driver and GPU error events.",
            OperationsWorkflows.NvidiaErrors,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNvidiaTopologyCommand()
    {
        return StandardCommand("topology",
            "Show NVIDIA GPU interconnects and topology.",
            OperationsWorkflows.NvidiaTopology,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNvidiaDriverInfoCommand()
    {
        return StandardCommand("driver-info",
            "Check NVIDIA driver, CUDA, and container runtime information.",
            OperationsWorkflows.NvidiaDriverInfo,
            _ => new EmptyInput());
    }

    private static ActionCommand NewDockerInspectCommand()
    {
        var container = new Option<string>("--container")
            { Required = true, Description = "Full Docker container ID from dms machine docker ps." };
        return StandardCommand("inspect",
            "Show container image, state, health, and mounts.",
            OperationsWorkflows.DockerInspect,
            p => new DockerContainerInput(p.GetValue(container)!),
            container);
    }

    private static ActionCommand NewDockerStatsSampleCommand()
    {
        var seconds = new Option<int>("--seconds")
            { DefaultValueFactory = _ => 10, Description = "Total sampling time in seconds (1 to 300)." };
        var interval = new Option<int>("--interval")
        {
            DefaultValueFactory = _ => 1,
            Description = "Seconds between samples. Use at least 1 and at most the total sampling time."
        };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 50, Description = "Maximum containers per sample." };
        return StandardCommand("stats-sample",
            "Sample Docker container CPU, memory, and I/O usage.",
            OperationsWorkflows.DockerStatsSample,
            p => new SampleInput(p.GetValue(seconds), p.GetValue(interval), p.GetValue(limit)),
            seconds,
            interval,
            limit);
    }

    private static ActionCommand NewDockerEventsCommand()
    {
        var container = new Option<string>("--container")
            { Required = true, Description = "Full Docker container ID from dms machine docker ps." };
        var since = new Option<DateTimeOffset?>("--since")
        {
            DefaultValueFactory = _ => null,
            Description = "Beginning of the time window in ISO 8601 with a UTC offset."
        };
        var until = new Option<DateTimeOffset?>("--until")
            { DefaultValueFactory = _ => null, Description = "End of the time window in ISO 8601 with a UTC offset." };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 200, Description = "Maximum number of Docker events to return." };
        return StandardCommand("events",
            "Read recent Docker events for a container.",
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
        var container = new Option<string>("--container")
            { Required = true, Description = "Full Docker container ID from dms machine docker ps." };
        return StandardCommand("diagnose-startup",
            "Gather a container's state, recent logs, and startup events.",
            OperationsWorkflows.DockerDiagnoseStartup,
            p => new DockerContainerInput(p.GetValue(container)!),
            container);
    }

    private static ActionCommand NewDockerStopCommand()
    {
        var container = new Option<string>("--container")
            { Required = true, Description = "Full Docker container ID from dms machine docker ps." };
        var timeoutSeconds = new Option<int>("--timeout-seconds")
        {
            DefaultValueFactory = _ => 30,
            Description = "Grace period in seconds before Docker force-stops the container."
        };
        return StandardCommand("stop",
            "Gracefully stop a standalone container.",
            OperationsWorkflows.DockerStop,
            p => new ContainerActionInput(p.GetValue(container)!, p.GetValue(timeoutSeconds)),
            container,
            timeoutSeconds);
    }

    private static ActionCommand NewDockerStartCommand()
    {
        var container = new Option<string>("--container")
            { Required = true, Description = "Full Docker container ID from dms machine docker ps." };
        return StandardCommand("start",
            "Start an existing standalone container.",
            OperationsWorkflows.DockerStart,
            p => new DockerContainerInput(p.GetValue(container)!),
            container);
    }

    private static ActionCommand NewDockerRestartCommand()
    {
        var container = new Option<string>("--container")
            { Required = true, Description = "Full Docker container ID from dms machine docker ps." };
        var timeoutSeconds = new Option<int>("--timeout-seconds")
        {
            DefaultValueFactory = _ => 30,
            Description = "Grace period in seconds for stopping the container before restart."
        };
        return StandardCommand("restart",
            "Restart a standalone container.",
            OperationsWorkflows.DockerRestart,
            p => new ContainerActionInput(p.GetValue(container)!, p.GetValue(timeoutSeconds)),
            container,
            timeoutSeconds);
    }

    private static ActionCommand NewDockerFilesystemExportCommand()
    {
        var container = new Option<string>("--container")
            { Required = true, Description = "Full Docker container ID from dms machine docker ps." };
        var path = new Option<string>("--path")
            { Required = true, Description = "Absolute path inside the container." };
        var recursive = new Option<bool>("--recursive")
            { DefaultValueFactory = _ => false, Description = "Include directory contents." };
        var maxBytes = new Option<long>("--max-bytes")
            { DefaultValueFactory = _ => 268435456, Description = "Maximum size of the exported artifact in bytes." };
        return StandardCommand("export",
            "Export a container file or directory as a downloadable artifact.",
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
        var container = new Option<string>("--container")
            { Required = true, Description = "Full Docker container ID from dms machine docker ps." };
        var source = new Option<string>("--source")
            { Required = true, Description = "Absolute source path on the Docker host." };
        var destination = new Option<string>("--destination")
            { Required = true, Description = "Absolute destination path inside the container." };
        var recursive = new Option<bool>("--recursive")
            { DefaultValueFactory = _ => false, Description = "Include directory contents." };
        var overwrite = new Option<bool>("--overwrite")
            { DefaultValueFactory = _ => false, Description = "Replace an existing destination." };
        return StandardCommand("cp-to",
            "Copy a file or directory from the host into a container.",
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
        var container = new Option<string>("--container")
            { Required = true, Description = "Full Docker container ID from dms machine docker ps." };
        var source = new Option<string>("--source")
            { Required = true, Description = "Absolute source path inside the container." };
        var destination = new Option<string>("--destination")
            { Required = true, Description = "Absolute destination path on the Docker host." };
        var recursive = new Option<bool>("--recursive")
            { DefaultValueFactory = _ => false, Description = "Include directory contents." };
        var overwrite = new Option<bool>("--overwrite")
            { DefaultValueFactory = _ => false, Description = "Replace an existing destination." };
        return StandardCommand("cp-from",
            "Copy a file or directory from a container to its host.",
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
            "List Swarm services, images, and replica counts.",
            OperationsWorkflows.SwarmServiceList,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSwarmServiceInspectCommand()
    {
        var service = new Option<string>("--service") { Required = true, Description = "Swarm service name or ID." };
        return StandardCommand("inspect",
            "Show a Swarm service's configuration and current version.",
            OperationsWorkflows.SwarmServiceInspect,
            p => new SwarmServiceInput(p.GetValue(service)!),
            service);
    }

    private static ActionCommand NewSwarmServicePsCommand()
    {
        var service = new Option<string>("--service") { Required = true, Description = "Swarm service name or ID." };
        return StandardCommand("ps",
            "Show Swarm service tasks, assigned nodes, and scheduling errors.",
            OperationsWorkflows.SwarmServicePs,
            p => new SwarmServiceInput(p.GetValue(service)!),
            service);
    }

    private static ActionCommand NewSwarmServiceLogsCommand()
    {
        var service = new Option<string>("--service") { Required = true, Description = "Swarm service name or ID." };
        var lines = new Option<int>("--lines")
            { DefaultValueFactory = _ => 100, Description = "Maximum recent log lines to request from the service." };
        var since = new Option<DateTimeOffset?>("--since")
        {
            DefaultValueFactory = _ => null,
            Description = "Beginning of the time window in ISO 8601 with a UTC offset."
        };
        return StandardCommand("logs",
            "Read recent logs from a Swarm service.",
            OperationsWorkflows.SwarmServiceLogs,
            p => new SwarmLogsInput(p.GetValue(service)!, p.GetValue(lines), p.GetValue(since)),
            service,
            lines,
            since);
    }

    private static ActionCommand NewSwarmServiceRolloutStatusCommand()
    {
        var service = new Option<string>("--service") { Required = true, Description = "Swarm service name or ID." };
        var command = StandardCommand("rollout-status",
            "Check a Swarm rollout's image, replicas, and task state.",
            OperationsWorkflows.SwarmServiceUpdateStatus,
            p => new SwarmServiceInput(p.GetValue(service)!),
            service);
        command.CommandAliases = ["update-status"];
        return command;
    }

    private static ActionCommand NewSwarmServiceStopCommand()
    {
        var service = new Option<string>("--service") { Required = true, Description = "Swarm service name or ID." };
        var expectedVersion = new Option<long>("--expected-version")
            { Required = true, Description = "Current Swarm service version from service inspect." };
        var nodeId = new Option<string[]>("--node-id")
        {
            Required = true, AllowMultipleArgumentsPerToken = true,
            Description = "IDs of all Swarm nodes affected by this change."
        };
        var timeoutSeconds = new Option<int>("--timeout-seconds")
            { DefaultValueFactory = _ => 120, Description = "Seconds to wait for the service to converge (1 to 600)." };
        return StandardCommand("stop",
            "Scale a replicated Swarm service to zero.",
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
        var service = new Option<string>("--service") { Required = true, Description = "Swarm service name or ID." };
        var expectedVersion = new Option<long>("--expected-version")
            { Required = true, Description = "Current Swarm service version from service inspect." };
        var nodeId = new Option<string[]>("--node-id")
        {
            Required = true, AllowMultipleArgumentsPerToken = true,
            Description = "IDs of all Swarm nodes affected by this change."
        };
        var replicas = new Option<int>("--replicas")
        {
            DefaultValueFactory = _ => 1,
            Description = "Desired replica count for the replicated service (1 to 1000)."
        };
        var timeoutSeconds = new Option<int>("--timeout-seconds")
            { DefaultValueFactory = _ => 120, Description = "Seconds to wait for the service to converge (1 to 600)." };
        return StandardCommand("start",
            "Scale a replicated Swarm service to the requested replica count.",
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
        var service = new Option<string>("--service") { Required = true, Description = "Swarm service name or ID." };
        var expectedVersion = new Option<long>("--expected-version")
            { Required = true, Description = "Current Swarm service version from service inspect." };
        var nodeId = new Option<string[]>("--node-id")
        {
            Required = true, AllowMultipleArgumentsPerToken = true,
            Description = "IDs of all Swarm nodes affected by this change."
        };
        var image = new Option<string>("--image")
            { Required = true, Description = "New image reference, including its tag or digest." };
        var timeoutSeconds = new Option<int>("--timeout-seconds")
            { DefaultValueFactory = _ => 120, Description = "Seconds to wait for the service to converge (1 to 600)." };
        return StandardCommand("update",
            "Update a Swarm service to the specified image.",
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
        var service = new Option<string>("--service") { Required = true, Description = "Swarm service name or ID." };
        var expectedVersion = new Option<long>("--expected-version")
            { Required = true, Description = "Current Swarm service version from service inspect." };
        var nodeId = new Option<string[]>("--node-id")
        {
            Required = true, AllowMultipleArgumentsPerToken = true,
            Description = "IDs of all Swarm nodes affected by this change."
        };
        var timeoutSeconds = new Option<int>("--timeout-seconds")
            { DefaultValueFactory = _ => 120, Description = "Seconds to wait for the service to converge (1 to 600)." };
        return StandardCommand("rollback",
            "Roll a Swarm service back to its previous configuration.",
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
            "Show operating system, kernel, architecture, and time settings.",
            OperationsWorkflows.SystemInfo,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSystemUptimeCommand()
    {
        return StandardCommand("uptime",
            "Show how long the machine has been running and its boot ID.",
            OperationsWorkflows.SystemUptime,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSystemctlListCommand()
    {
        var failedOnly = new Option<bool>("--failed-only")
            { DefaultValueFactory = _ => false, Description = "Show only units in the failed state." };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 200, Description = "Maximum number of units to return." };
        return StandardCommand("list",
            "List systemd services and their states.",
            OperationsWorkflows.SystemctlList,
            p => new ServiceListInput(p.GetValue(failedOnly), p.GetValue(limit)),
            failedOnly,
            limit);
    }

    private static ActionCommand NewSystemctlStartCommand()
    {
        var service = new Option<string>("--service")
            { Required = true, Description = "systemd unit name, such as nginx.service." };
        return StandardCommand("start",
            "Start a systemd service.",
            OperationsWorkflows.SystemctlStart,
            p => new ServiceInput(p.GetValue(service)!),
            service);
    }

    private static ActionCommand NewSystemctlStopCommand()
    {
        var service = new Option<string>("--service")
            { Required = true, Description = "systemd unit name, such as nginx.service." };
        return StandardCommand("stop",
            "Stop a systemd service.",
            OperationsWorkflows.SystemctlStop,
            p => new ServiceInput(p.GetValue(service)!),
            service);
    }

    private static ActionCommand NewSystemJournalCommand()
    {
        var service = new Option<string?>("--service")
            { Description = "Filter by systemd unit name, such as nginx.service." };
        var boot = new Option<string?>("--boot")
            { Description = "Use 0 for the current boot, -1 for the previous boot, or a full boot ID." };
        var level = new Option<string?>("--level")
            { Description = "Minimum priority to include, from emerg through debug." };
        var since = new Option<DateTimeOffset?>("--since")
            { Description = "Beginning of the time window in ISO 8601 with a UTC offset." };
        var until = new Option<DateTimeOffset?>("--until")
            { Description = "End of the time window in ISO 8601 with a UTC offset." };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 200, Description = "Maximum number of journal entries to return." };
        var kernel = new Option<bool>("--kernel") { Description = "Read kernel messages. Use without --service." };
        var cursor = new Option<string?>("--before-cursor")
            { Description = "Continue before the last returned cursor; results are newest first." };
        return StandardCommand("journal",
            "Read systemd journal entries by unit, boot, or priority.",
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
            "List systemd timers and scheduled cron jobs.",
            OperationsWorkflows.SystemTimers,
            _ => new EmptyInput());
    }

    private static ActionCommand NewAptInspectCommand()
    {
        return StandardCommand("inspect",
            "Show installed packages and available APT updates.",
            OperationsWorkflows.AptInspect,
            _ => new EmptyInput());
    }

    private static ActionCommand NewAptInstallCommand()
    {
        var package = new Option<string[]>("--package")
        {
            Required = true, AllowMultipleArgumentsPerToken = true,
            Description = "Exact APT package names, optionally written as name=version."
        };
        return StandardCommand("install",
            "Install the requested APT packages.",
            OperationsWorkflows.AptInstall,
            p => new PackageInput(p.GetValue(package)!),
            package);
    }

    private static ActionCommand NewAptUpgradeCommand()
    {
        var package = new Option<string[]>("--package")
        {
            Required = true, AllowMultipleArgumentsPerToken = true,
            Description = "Exact target versions, each written as name=version."
        };
        return StandardCommand("upgrade",
            "Upgrade the specified installed APT packages.",
            OperationsWorkflows.AptUpgrade,
            p => new PackageUpgradeInput(p.GetValue(package)!),
            package);
    }

    private static ActionCommand NewAptUpdateCommand() =>
        StandardCommand("update", "Refresh package indexes from the configured APT sources.",
            OperationsWorkflows.AptUpdate, _ => new EmptyInput());

    private static ActionCommand NewAptMaintenanceCommand(AptMaintenance operation)
    {
        var autoremove = operation == AptMaintenance.Autoremove;
        var definition = autoremove ? OperationsWorkflows.AptAutoremove : OperationsWorkflows.AptUpgradeAll;
        var preview = new Option<bool>("--dry-run")
            { Description = "Preview per-machine package changes without applying them (default without --plan)." };
        var plan = new Option<Guid?>("--plan")
            { Description = "Workflow ID of a successful preview. Submit its package changes for approval." };
        var purge = new Option<bool>("--purge")
            { Description = "Remove the unused packages and their configuration files." };

        return new ActionCommand(autoremove ? "autoremove" : "upgrade-all",
            autoremove ? "Preview or request removal of unused APT dependencies."
                : "Preview or request upgrades to each machine's APT candidate versions.",
            definition.Name, definition, options: autoremove ? [preview, plan, purge] : [preview, plan],
            selectOperation: parse =>
            {
                var planId = parse.GetValue(plan);
                var purgeFiles = autoremove && parse.GetValue(purge);
                if (planId == null)
                    return (OperationsWorkflows.AptPlan.Name,
                        OperationsWorkflows.AptPlan.Serialize(new AptPlanInput(operation, purgeFiles)));
                if (parse.GetValue(preview)) throw new ArgumentException("Choose either --dry-run or --plan.");
                return (definition.Name, definition.Serialize(new AptApplyInput(planId.Value, purgeFiles)));
            }, targetDescription: "Target machine IDs. With --plan, omit target flags to reuse the preview's targets.");
    }

    private static async Task<DmsResponse> ReadAptPlanAsync(DmsApiClient client, Guid previewId)
    {
        var response = await client.CallstackAsync(previewId);
        var nodes = (response.Data as JArray)?.OfType<JObject>()
            .Where(node => node.Value<string>("ParentId") == previewId.ToString() &&
                node.Value<string>("Definition") == OperationsWorkflows.AptPlan.Name).ToArray();
        if (nodes is not { Length: > 0 })
            throw new ArgumentException("No APT preview results found. Check that you supplied the preview's root workflow ID and it has started.");

        var rows = new JArray();
        foreach (var node in nodes.OrderBy(node => node.Value<string>("MachineId"), StringComparer.Ordinal))
        {
            var machine = node.Value<string>("MachineId")!;
            var state = (RunState)node.Value<int>("State");
            if (state != RunState.Succeeded)
            {
                AddRow(machine, state.ToString());
                continue;
            }

            var plan = OperationsWorkflows.AptPlan.ReadResult(node.Value<string>("Result")!);
            if (plan.Changes.Length == 0) AddRow(machine, "No changes planned");
            foreach (var change in plan.Changes)
                AddRow(machine, "Planned", change);
            foreach (var change in plan.AdditionalUpgradesRequiringReview)
                AddRow(machine, "Excluded (removals)", change);
        }

        response.Data = rows;
        return response;

        void AddRow(string machine, string state, AptPackageChange? change = null) =>
            rows.Add(JObject.FromObject(new
            {
                Machine = machine, State = state, Action = change?.Action.ToString(),
                PackageName = change?.Package, From = change?.FromVersion, To = change?.ToVersion
            }));
    }

    private static ActionCommand NewSystemRebootCommand()
    {
        var delaySeconds = new Option<int>("--delay-seconds")
            { DefaultValueFactory = _ => 15, Description = "Seconds to wait before rebooting the machine." };
        return StandardCommand("reboot",
            "Reboot the machine and verify that the Agent returns.",
            OperationsWorkflows.SystemReboot,
            p => new RebootInput(p.GetValue(delaySeconds)),
            delaySeconds);
    }

    private static ActionCommand NewSystemExecuteScriptCommand()
    {
        var language = new Option<string>("--language")
            { Required = true, Description = "Use the bash or python interpreter." };
        var scriptFile = new Option<string>("--script-file")
            { Required = true, Description = "Path to the local script. Its contents are uploaded for review." };
        var timeoutSeconds = new Option<int>("--timeout-seconds")
            { DefaultValueFactory = _ => 60, Description = "Maximum script runtime in seconds (1 to 3600)." };
        return PreparedOperation("execute-script",
            "Run an uploaded Bash or Python script on a machine.",
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
            "Find database services and configured connection names on a machine.",
            OperationsWorkflows.DatabaseDiscover,
            _ => new EmptyInput());
    }

    private static ActionCommand NewDatabaseListDatabasesCommand()
    {
        var connection = new Option<string>("--connection")
            { Required = true, Description = "Connection name from dms machine database discover." };
        return StandardCommand("list-databases",
            "List databases available through a configured connection.",
            OperationsWorkflows.DatabaseListDatabases,
            p => new DatabaseInput(p.GetValue(connection)!),
            connection);
    }

    private static ActionCommand NewDatabaseListTablesCommand()
    {
        var connection = new Option<string>("--connection")
            { Required = true, Description = "Connection name from dms machine database discover." };
        var database = new Option<string>("--database")
            { Required = true, Description = "Database name from list-databases." };
        return StandardCommand("list-tables",
            "List tables in the specified database.",
            OperationsWorkflows.DatabaseListTables,
            p => new DatabaseNameInput(p.GetValue(connection)!, p.GetValue(database)!),
            connection,
            database);
    }

    private static ActionCommand NewDatabaseDescribeTableCommand()
    {
        var connection = new Option<string>("--connection")
            { Required = true, Description = "Connection name from dms machine database discover." };
        var database = new Option<string>("--database")
            { Required = true, Description = "Database name from list-databases." };
        var table = new Option<string>("--table") { Required = true, Description = "Table name from list-tables." };
        return StandardCommand("describe-table",
            "Show a database table's columns and types.",
            OperationsWorkflows.DatabaseDescribeTable,
            p => new DatabaseTableInput(p.GetValue(connection)!, p.GetValue(database)!, p.GetValue(table)!),
            connection,
            database,
            table);
    }

    private static ActionCommand NewDatabaseQueryTableCommand()
    {
        var connection = new Option<string>("--connection")
            { Required = true, Description = "Connection name from dms machine database discover." };
        var database = new Option<string>("--database")
            { Required = true, Description = "Database name from list-databases." };
        var table = new Option<string>("--table") { Required = true, Description = "Table name from list-tables." };
        var column = new Option<string[]>("--column")
        {
            Required = true, AllowMultipleArgumentsPerToken = true,
            Description = "Column names to return. Specify at least one."
        };
        var filters = new Option<string?>("--filters")
        {
            DefaultValueFactory = _ => null,
            Description = "JSON array of filters with Column, Operator, and Value fields."
        };
        var sortBy = new Option<string?>("--sort-by")
            { DefaultValueFactory = _ => null, Description = "Column to sort by before returning the page." };
        var descending = new Option<bool>("--descending")
            { DefaultValueFactory = _ => false, Description = "Sort from highest to lowest." };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 100, Description = "Maximum number of rows to return." };
        var offset = new Option<int>("--offset")
            { DefaultValueFactory = _ => 0, Description = "Number of rows to skip before returning the page." };
        return StandardCommand("query-table",
            "Read selected table columns with optional filters and sorting.",
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
        var connection = new Option<string>("--connection")
            { Required = true, Description = "Connection name from dms machine database discover." };
        var sqlFile = new Option<string>("--sql-file")
            { Required = true, Description = "Path to a local SQL file. Its contents are uploaded for review." };
        var timeoutSeconds = new Option<int>("--timeout-seconds")
            { DefaultValueFactory = _ => 30, Description = "Maximum query runtime in seconds." };
        var maxRows = new Option<int>("--max-rows")
            { DefaultValueFactory = _ => 1000, Description = "Maximum number of result rows to return." };
        return PreparedOperation("query-sql",
            "Run a read-only SQL query from a local file.",
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
        var connection = new Option<string>("--connection")
            { Required = true, Description = "Connection name from dms machine database discover." };
        var sqlFile = new Option<string>("--sql-file")
            { Required = true, Description = "Path to a local SQL file. Its contents are uploaded for review." };
        var timeoutSeconds = new Option<int>("--timeout-seconds")
            { DefaultValueFactory = _ => 30, Description = "Maximum query runtime in seconds." };
        var maxRows = new Option<int>("--max-rows")
            { DefaultValueFactory = _ => 1000, Description = "Maximum number of result rows to return." };
        return PreparedOperation("execute-sql",
            "Execute SQL from a file, including writes or schema changes.",
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
        var connection = new Option<string>("--connection")
            { Required = true, Description = "Connection name from dms machine database discover." };
        return StandardCommand("diagnostics",
            "Inspect a database's active queries, connections, and resource usage.",
            OperationsWorkflows.DatabaseDiagnostics,
            p => new DatabaseInput(p.GetValue(connection)!),
            connection);
    }

    private static ActionCommand NewNetworkingInterfacesCommand()
    {
        return StandardCommand("interfaces",
            "Show network interfaces, addresses, and link counters.",
            OperationsWorkflows.NetworkingInterfaces,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNetworkingRoutesCommand()
    {
        return StandardCommand("routes",
            "Show routing tables and policy routing rules.",
            OperationsWorkflows.NetworkingRoutes,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNetworkingSocketsCommand()
    {
        return StandardCommand("sockets",
            "Show listening sockets, connections, and their processes.",
            OperationsWorkflows.NetworkingSockets,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNetworkingDnsQueryCommand()
    {
        var host = new Option<string>("--host")
            { Required = true, Description = "Hostname to resolve from the selected machine." };
        return StandardCommand("dns-query",
            "Resolve a hostname from the selected machine.",
            OperationsWorkflows.NetworkingDnsQuery,
            p => new DnsQueryInput(p.GetValue(host)!),
            host);
    }

    private static ActionCommand NewNetworkingProbeCommand()
    {
        var host = new Option<string>("--host")
            { Required = true, Description = "Remote hostname or IP address to reach." };
        var port = new Option<int>("--port") { DefaultValueFactory = _ => 443, Description = "Destination TCP port." };
        var url = new Option<string?>("--url")
        {
            DefaultValueFactory = _ => null,
            Description = "Optional HTTP(S) URL on the same host and port to check with HEAD."
        };
        var timeoutSeconds = new Option<int>("--timeout-seconds")
            { DefaultValueFactory = _ => 10, Description = "Maximum time to wait, in seconds." };
        return StandardCommand("probe",
            "Test TCP connectivity and optionally check an HTTP endpoint.",
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
        var seconds = new Option<int>("--seconds")
            { DefaultValueFactory = _ => 10, Description = "Total sampling time in seconds (1 to 300)." };
        var interval = new Option<int>("--interval")
        {
            DefaultValueFactory = _ => 1,
            Description = "Seconds between samples. Use at least 1 and at most the total sampling time."
        };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 50, Description = "Maximum process owners per sample." };
        return StandardCommand("traffic-sample",
            "Sample interface traffic and active network connections.",
            OperationsWorkflows.NetworkingTrafficSample,
            p => new SampleInput(p.GetValue(seconds), p.GetValue(interval), p.GetValue(limit)),
            seconds,
            interval,
            limit);
    }

    private static ActionCommand NewNetworkingFirewallInfoCommand()
    {
        return StandardCommand("firewall-info",
            "Read firewall rules and packet counters.",
            OperationsWorkflows.NetworkingFirewallInfo,
            _ => new EmptyInput());
    }

    private static ActionCommand NewNetworkingTlsCheckCommand()
    {
        var host = new Option<string>("--host")
            { Required = true, Description = "Remote hostname or IP address used for TLS verification." };
        var port = new Option<int>("--port") { DefaultValueFactory = _ => 443, Description = "Destination TCP port." };
        var url = new Option<string?>("--url")
        {
            DefaultValueFactory = _ => null,
            Description = "Compatibility option. TLS checks the supplied --host and --port."
        };
        var timeoutSeconds = new Option<int>("--timeout-seconds")
            { DefaultValueFactory = _ => 10, Description = "Maximum time to wait, in seconds." };
        return StandardCommand("tls-check",
            "Check an endpoint's TLS handshake and certificate.",
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
            "Check whether UEFI Secure Boot is enabled.",
            OperationsWorkflows.SecuritySecureBoot,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSecurityDiskEncryptionCommand()
    {
        return StandardCommand("disk-encryption",
            "Show encryption status for disks and mounted filesystems.",
            OperationsWorkflows.SecurityDiskEncryption,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSshOpenCommand()
    {
        var key = new Option<Guid>(MachineWorkflowCli.KeyIdOption)
            { Required = true, Description = "Verified personal key ID from the DMS SSH public keys page." };
        var minutes = new Option<int>(MachineWorkflowCli.MinutesOption)
            { DefaultValueFactory = _ => 30, Description = "Lease duration in minutes, 30 to 270." };
        var sudo = new Option<bool>(MachineWorkflowCli.SudoOption)
            { Description = "Grant the temporary account passwordless sudo." };
        return StandardCommand(MachineWorkflowCli.SshOpen.Name,
            "Grant time-limited SSH access using a verified personal key.",
            MachineWorkflowCli.SshOpen.Definition,
            p => new SshOpenInput(p.GetValue(key), p.GetValue(minutes), p.GetValue(sudo)), key, minutes, sudo);
    }

    private static ActionCommand NewSecuritySshAuditCommand()
    {
        var user = new Option<string>("--user")
            { DefaultValueFactory = _ => "root", Description = "Login account to evaluate in OpenSSH Match rules." };
        var address = new Option<string>("--address")
        {
            DefaultValueFactory = _ => "127.0.0.1",
            Description = "Client address to evaluate in OpenSSH Match rules."
        };
        var host = new Option<string>("--host")
        {
            DefaultValueFactory = _ => "localhost",
            Description = "Client hostname to evaluate in OpenSSH Match rules."
        };
        return StandardCommand("ssh-audit",
            "Check OpenSSH settings for a given user and client address.",
            OperationsWorkflows.SecuritySshAudit,
            p => new SshAuditInput(p.GetValue(user)!, p.GetValue(address)!, p.GetValue(host)!),
            user,
            address,
            host);
    }

    private static ActionCommand NewSecurityFail2banStatusCommand()
    {
        return StandardCommand("fail2ban-status",
            "Check Fail2ban status, jails, and active bans.",
            OperationsWorkflows.SecurityFail2banStatus,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSecurityCleanTrafficStatusCommand()
    {
        return StandardCommand("clean-traffic-status",
            "Check Clean Traffic service status and recent activity.",
            OperationsWorkflows.SecurityCleanTrafficStatus,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSecurityAuthEventsCommand()
    {
        var since = new Option<DateTimeOffset?>("--since")
        {
            DefaultValueFactory = _ => null,
            Description = "Beginning of the time window in ISO 8601 with a UTC offset."
        };
        var until = new Option<DateTimeOffset?>("--until")
            { DefaultValueFactory = _ => null, Description = "End of the time window in ISO 8601 with a UTC offset." };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 200, Description = "Maximum number of authentication events to return." };
        return StandardCommand("auth-events",
            "Read recent login, authentication, and privilege events.",
            OperationsWorkflows.SecurityAuthEvents,
            p => new EventWindowInput(p.GetValue(since), p.GetValue(until), p.GetValue(limit)),
            since,
            until,
            limit);
    }

    private static ActionCommand NewSecurityAccountsAuditCommand()
    {
        return StandardCommand("accounts-audit",
            "Inspect local accounts, login shells, and privileged groups.",
            OperationsWorkflows.SecurityAccountsAudit,
            _ => new EmptyInput());
    }

    private static ActionCommand NewSecurityBaselineAuditCommand()
    {
        var baselinePath = new Option<string?>("--baseline-path")
        {
            DefaultValueFactory = _ => null,
            Description = "Absolute path to the baseline file on the selected machine."
        };
        return StandardCommand("baseline-audit",
            "Compare security settings with a baseline.",
            OperationsWorkflows.SecurityBaselineAudit,
            p => new BaselineInput(p.GetValue(baselinePath)),
            baselinePath);
    }

    private static ActionCommand PreparedOperation<TInput>(string name,
        string description,
        WorkflowDefinition<TInput, OperationResult> definition,
        Func<DmsApiClient, ParseResult, Guid, Task<TInput>> prepare,
        params Option[] options) =>
        new(name,
            description,
            definition.Name,
            definition,
            options: options,
            preparedInput: async (client, context, requestId) =>
                definition.Serialize(await prepare(client, context, requestId)),
            readResult: definition.Risk == RiskLevel.Open ? result => definition.ReadResult(result) : null);

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
