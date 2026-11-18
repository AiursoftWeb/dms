namespace Aiursoft.DMS.Contracts;

// Compiled business operations. Each public definition is a separate Lockbox capability.
public static class OperationsWorkflows
{
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<HistoryInput, OperationResult> PerformanceHistorySources =
        new("machine.performance.history-sources",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformanceHistorySources)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<HistoryInput, OperationResult> PerformanceHistoryCpu =
        new("machine.performance.history.cpu",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformanceHistoryCpu)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<HistoryInput, OperationResult> PerformanceHistoryLoad =
        new("machine.performance.history.load",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformanceHistoryLoad)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<HistoryInput, OperationResult> PerformanceHistoryMemory =
        new("machine.performance.history.memory",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformanceHistoryMemory)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<HistoryInput, OperationResult> PerformanceHistoryDisk =
        new("machine.performance.history.disk",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformanceHistoryDisk)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<HistoryInput, OperationResult> PerformanceHistoryNetwork =
        new("machine.performance.history.network",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformanceHistoryNetwork)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<PcpHistoryInput, OperationResult> PerformancePcpHistory =
        new("machine.performance.pcp-history",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformancePcpHistory)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<BlockedProcessesInput, OperationResult>
        PerformanceBlockedProcesses = new("machine.performance.blocked-processes",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformanceBlockedProcesses)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<KernelEventsInput, OperationResult> SystemKernelEvents =
        new("machine.system.kernel-events",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SystemKernelEvents)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<ListPathInput, OperationResult> FilesystemLs =
        new("machine.filesystem.ls", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(FilesystemLs)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<PathInput, OperationResult> FilesystemStat =
        new("machine.filesystem.stat", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(FilesystemStat)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<TextFileInput, OperationResult> FilesystemCat =
        new("machine.filesystem.cat", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(FilesystemCat)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<TextFileInput, OperationResult> FilesystemTail =
        new("machine.filesystem.tail",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(FilesystemTail)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<FindFilesInput, OperationResult> FilesystemFind =
        new("machine.filesystem.find", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(FilesystemFind)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<MakeDirectoryInput, OperationResult> FilesystemMkdir =
        new("machine.filesystem.mkdir",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(FilesystemMkdir)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<CopyPathInput, OperationResult> FilesystemCp =
        new("machine.filesystem.cp",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(FilesystemCp)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<CopyPathInput, OperationResult> FilesystemMv =
        new("machine.filesystem.mv",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(FilesystemMv)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<ExportPathInput, OperationResult> FilesystemExport =
        new("machine.filesystem.export",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(FilesystemExport)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<ImportPathInput, OperationResult> FilesystemImport =
        new("machine.filesystem.import",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(FilesystemImport)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> LayoutFdiskQuery =
        new("machine.layout.fdisk-query", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(LayoutFdiskQuery)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> LayoutMounts =
        new("machine.layout.mounts", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(LayoutMounts)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> LayoutInodes =
        new("machine.layout.inodes", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(LayoutInodes)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> LayoutOpenDeleted =
        new("machine.layout.open-deleted", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(LayoutOpenDeleted)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<DeviceInput, OperationResult> LayoutSmart =
        new("machine.layout.smart", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(LayoutSmart)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<SmartTestInput, OperationResult> LayoutSmartTest =
        new("machine.layout.smart-test",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(LayoutSmartTest)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> LayoutBcacheInfo =
        new("machine.layout.bcache-info", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(LayoutBcacheInfo)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<BaselineInput, OperationResult> LayoutBcacheAudit =
        new("machine.layout.bcache-audit",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(LayoutBcacheAudit)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<ProcessListInput, OperationResult> PerformanceProcesses =
        new("machine.performance.processes",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformanceProcesses)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<SampleInput, OperationResult> PerformanceCpuSample =
        new("machine.performance.cpu-sample",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformanceCpuSample)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> PerformanceMemoryBreakdown =
        new("machine.performance.memory-breakdown", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformanceMemoryBreakdown)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<SampleInput, OperationResult> PerformanceIoSample =
        new("machine.performance.io-sample",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformanceIoSample)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<SampleInput, OperationResult> PerformanceDstatSample =
        new("machine.performance.dstat-sample",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformanceDstatSample)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> PerformancePressure =
        new("machine.performance.pressure", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformancePressure)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<EventWindowInput, OperationResult> PerformanceOomHistory =
        new("machine.performance.oom-history",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(PerformanceOomHistory)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> NvidiaSmi =
        new("machine.nvidia.smi", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NvidiaSmi)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> NvidiaProcesses =
        new("machine.nvidia.processes", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NvidiaProcesses)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<SampleInput, OperationResult> NvidiaSample =
        new("machine.nvidia.sample", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NvidiaSample)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> NvidiaErrors =
        new("machine.nvidia.errors", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NvidiaErrors)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> NvidiaTopology =
        new("machine.nvidia.topology", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NvidiaTopology)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> NvidiaDriverInfo =
        new("machine.nvidia.driver-info", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NvidiaDriverInfo)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<DockerContainerInput, OperationResult> DockerInspect =
        new("machine.docker.inspect", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DockerInspect)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<SampleInput, OperationResult> DockerStatsSample =
        new("machine.docker.stats-sample",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DockerStatsSample)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<ContainerEventsInput, OperationResult> DockerEvents =
        new("machine.docker.events", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DockerEvents)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<DockerContainerInput, OperationResult> DockerDiagnoseStartup =
        new("machine.docker.diagnose-startup",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DockerDiagnoseStartup)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<ContainerActionInput, OperationResult> DockerStop =
        new("machine.docker.stop", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DockerStop)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<DockerContainerInput, OperationResult> DockerStart =
        new("machine.docker.start", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DockerStart)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<ContainerActionInput, OperationResult> DockerRestart =
        new("machine.docker.restart", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DockerRestart)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<ContainerExportInput, OperationResult> DockerFilesystemExport =
        new("machine.docker.filesystem.export",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DockerFilesystemExport)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<ContainerCopyInput, OperationResult> DockerFilesystemCpTo =
        new("machine.docker.filesystem.cp-to",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DockerFilesystemCpTo)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<ContainerCopyInput, OperationResult> DockerFilesystemCpFrom =
        new("machine.docker.filesystem.cp-from",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DockerFilesystemCpFrom)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> SwarmServiceList =
        new("machine.swarm.service.list", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SwarmServiceList)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<SwarmServiceInput, OperationResult> SwarmServiceInspect =
        new("machine.swarm.service.inspect",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SwarmServiceInspect)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<SwarmServiceInput, OperationResult> SwarmServicePs =
        new("machine.swarm.service.ps",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SwarmServicePs)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<SwarmLogsInput, OperationResult> SwarmServiceLogs =
        new("machine.swarm.service.logs",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SwarmServiceLogs)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<SwarmServiceInput, OperationResult> SwarmServiceUpdateStatus =
        new("machine.swarm.service.update-status",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SwarmServiceUpdateStatus)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<SwarmChangeInput, OperationResult> SwarmServiceStop =
        new("machine.swarm.service.stop",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SwarmServiceStop)),
            "1",
            ValidateInput: input => input.ValidateFor("stop"),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<SwarmChangeInput, OperationResult> SwarmServiceStart =
        new("machine.swarm.service.start",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SwarmServiceStart)),
            "1",
            ValidateInput: input => input.ValidateFor("start"),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<SwarmChangeInput, OperationResult> SwarmServiceUpdate =
        new("machine.swarm.service.update",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SwarmServiceUpdate)),
            "1",
            ValidateInput: input => input.ValidateFor("update"),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<SwarmChangeInput, OperationResult> SwarmServiceRollback =
        new("machine.swarm.service.rollback",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SwarmServiceRollback)),
            "1",
            ValidateInput: input => input.ValidateFor("rollback"),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> SystemInfo =
        new("machine.system.info", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SystemInfo)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> SystemUptime =
        new("machine.system.uptime", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SystemUptime)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<ServiceListInput, OperationResult> SystemctlList =
        new("machine.systemctl.list", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SystemctlList)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<ServiceInput, OperationResult> SystemctlStart =
        new("machine.systemctl.start",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SystemctlStart)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<ServiceInput, OperationResult> SystemctlStop =
        new("machine.systemctl.stop", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SystemctlStop)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<JournalInput, OperationResult> SystemJournal =
        new("machine.system.journal",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SystemJournal)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> SystemTimers =
        new("machine.system.timers", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SystemTimers)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> AptInspect =
        new("machine.apt.inspect", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(AptInspect)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<PackageInput, OperationResult> AptInstall =
        new("machine.apt.install", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(AptInstall)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<PackageUpgradeInput, OperationResult> AptUpgrade =
        new("machine.apt.upgrade", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(AptUpgrade)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<RebootInput, OperationResult> SystemReboot =
        new("machine.system.reboot", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SystemReboot)), "1", ValidateInput: input => input.Validate(), PublicEntry: true);

    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<ScriptInput, OperationResult> SystemExecuteScript =
        new("machine.system.execute-script",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SystemExecuteScript)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> DatabaseDiscover =
        new("machine.database.discover", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DatabaseDiscover)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<DatabaseInput, OperationResult> DatabaseListDatabases =
        new("machine.database.list-databases",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DatabaseListDatabases)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<DatabaseNameInput, OperationResult> DatabaseListTables =
        new("machine.database.list-tables",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DatabaseListTables)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<DatabaseTableInput, OperationResult> DatabaseDescribeTable =
        new("machine.database.describe-table",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DatabaseDescribeTable)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<DatabaseQueryInput, OperationResult> DatabaseQueryTable =
        new("machine.database.query-table",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DatabaseQueryTable)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<DatabaseSqlInput, OperationResult> DatabaseQuerySql =
        new("machine.database.query-sql",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DatabaseQuerySql)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<DatabaseSqlInput, OperationResult> DatabaseExecuteSql =
        new("machine.database.execute-sql",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DatabaseExecuteSql)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<DatabaseInput, OperationResult> DatabaseDiagnostics =
        new("machine.database.diagnostics",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(DatabaseDiagnostics)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> NetworkingInterfaces =
        new("machine.networking.interfaces", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NetworkingInterfaces)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> NetworkingRoutes =
        new("machine.networking.routes", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NetworkingRoutes)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> NetworkingSockets =
        new("machine.networking.sockets", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NetworkingSockets)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<DnsQueryInput, OperationResult> NetworkingDnsQuery =
        new("machine.networking.dns-query",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NetworkingDnsQuery)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<NetworkProbeInput, OperationResult> NetworkingProbe =
        new("machine.networking.probe",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NetworkingProbe)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<SampleInput, OperationResult> NetworkingTrafficSample =
        new("machine.networking.traffic-sample",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NetworkingTrafficSample)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> NetworkingFirewallInfo =
        new("machine.networking.firewall-info", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NetworkingFirewallInfo)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<NetworkProbeInput, OperationResult> NetworkingTlsCheck =
        new("machine.networking.tls-check",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(NetworkingTlsCheck)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Safe)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> SecuritySecureBoot =
        new("machine.security.secure-boot", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SecuritySecureBoot)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> SecurityDiskEncryption =
        new("machine.security.disk-encryption", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SecurityDiskEncryption)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<SshAuditInput, OperationResult> SecuritySshAudit =
        new("machine.security.ssh-audit",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SecuritySshAudit)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> SecurityFail2banStatus =
        new("machine.security.fail2ban-status", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SecurityFail2banStatus)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> SshDisable =
        new("machine.ssh.disable", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SshDisable)), "1", PublicEntry: true)
        { SingleMachineOnly = true };

    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<SshOpenInput, OperationResult> SshOpen =
        new("machine.ssh.temp-allow-ssh", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SshOpen)), "1",
            ValidateInput: input => input.Validate(), PublicEntry: true) { SingleMachineOnly = true };

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<SshRecoveryInput, OperationResult> SshRecover =
        new("internal.ssh.recover-lease", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SshRecover)), "1",
            ValidateInput: input =>
            {
                if (input.ExpectedLeaseId == Guid.Empty)
                    throw new ArgumentException("SSH lease recovery requires an expected lease ID.");
            });

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> SecurityCleanTrafficStatus =
        new("machine.security.clean-traffic-status", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SecurityCleanTrafficStatus)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<EventWindowInput, OperationResult> SecurityAuthEvents =
        new("machine.security.auth-events",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SecurityAuthEvents)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> SecurityAccountsAudit =
        new("machine.security.accounts-audit", WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SecurityAccountsAudit)), "1", PublicEntry: true);

    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly MachineWorkflowDefinition<BaselineInput, OperationResult> SecurityBaselineAudit =
        new("machine.security.baseline-audit",
            WorkflowMetadata.Risk(typeof(OperationsWorkflows), nameof(SecurityBaselineAudit)),
            "1",
            ValidateInput: input => input.Validate(),
            PublicEntry: true);
}
