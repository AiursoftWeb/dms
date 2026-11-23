using Aiursoft.DMS.Workflows.Operations;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed partial class MachineWorkflowRegistry
{
    private void RegisterOperations()
    {
        Register(OperationsWorkflows.PerformanceHistorySources,
            (input, agent) => new OperationWorkflow<HistoryInput>(OperationsWorkflows.PerformanceHistorySources,
                input,
                HistoryOperations.HistorySourcesAsync,
                agent));
        Register(OperationsWorkflows.PerformanceHistoryCpu,
            (input, agent) => new OperationWorkflow<HistoryInput>(OperationsWorkflows.PerformanceHistoryCpu,
                input,
                HistoryOperations.CpuHistoryAsync,
                agent));
        Register(OperationsWorkflows.PerformanceHistoryLoad,
            (input, agent) => new OperationWorkflow<HistoryInput>(OperationsWorkflows.PerformanceHistoryLoad,
                input,
                HistoryOperations.LoadHistoryAsync,
                agent));
        Register(OperationsWorkflows.PerformanceHistoryMemory,
            (input, agent) => new OperationWorkflow<HistoryInput>(OperationsWorkflows.PerformanceHistoryMemory,
                input,
                HistoryOperations.MemoryHistoryAsync,
                agent));
        Register(OperationsWorkflows.PerformanceHistoryDisk,
            (input, agent) => new OperationWorkflow<HistoryInput>(OperationsWorkflows.PerformanceHistoryDisk,
                input,
                HistoryOperations.DiskHistoryAsync,
                agent));
        Register(OperationsWorkflows.PerformanceHistoryNetwork,
            (input, agent) => new OperationWorkflow<HistoryInput>(OperationsWorkflows.PerformanceHistoryNetwork,
                input,
                HistoryOperations.NetworkHistoryAsync,
                agent));
        Register(OperationsWorkflows.PerformancePcpHistory,
            (input, agent) => new OperationWorkflow<PcpHistoryInput>(OperationsWorkflows.PerformancePcpHistory,
                input,
                HistoryOperations.PcpHistoryAsync,
                agent));
        Register(OperationsWorkflows.PerformanceBlockedProcesses,
            (input, agent) => new OperationWorkflow<BlockedProcessesInput>(
                OperationsWorkflows.PerformanceBlockedProcesses,
                input,
                ProcessDiagnostics.BlockedProcessesAsync,
                agent));
        Register(OperationsWorkflows.SystemKernelEvents,
            (input, agent) => new OperationWorkflow<KernelEventsInput>(OperationsWorkflows.SystemKernelEvents,
                input,
                JournalDiagnostics.KernelEventsAsync,
                agent));
        Register(OperationsWorkflows.FilesystemLs,
            (input, agent) => new OperationWorkflow<ListPathInput>(OperationsWorkflows.FilesystemLs,
                input,
                FilesystemOperations.LsAsync,
                agent));
        Register(OperationsWorkflows.FilesystemStat,
            (input, agent) => new OperationWorkflow<PathInput>(OperationsWorkflows.FilesystemStat,
                input,
                FilesystemOperations.StatAsync,
                agent));
        Register(OperationsWorkflows.FilesystemCat,
            (input, agent) => new OperationWorkflow<TextFileInput>(OperationsWorkflows.FilesystemCat,
                input,
                FilesystemOperations.CatAsync,
                agent));
        Register(OperationsWorkflows.FilesystemTail,
            (input, agent) => new OperationWorkflow<TextFileInput>(OperationsWorkflows.FilesystemTail,
                input,
                FilesystemOperations.TailAsync,
                agent));
        Register(OperationsWorkflows.FilesystemFind,
            (input, agent) => new OperationWorkflow<FindFilesInput>(OperationsWorkflows.FilesystemFind,
                input,
                FilesystemOperations.FindAsync,
                agent));
        Register(OperationsWorkflows.FilesystemMkdir,
            (input, agent) => new OperationWorkflow<MakeDirectoryInput>(OperationsWorkflows.FilesystemMkdir,
                input,
                FilesystemOperations.MkdirAsync,
                agent));
        Register(OperationsWorkflows.FilesystemCp,
            (input, agent) => new OperationWorkflow<CopyPathInput>(OperationsWorkflows.FilesystemCp,
                input,
                FilesystemOperations.CpAsync,
                agent));
        Register(OperationsWorkflows.FilesystemMv,
            (input, agent) => new OperationWorkflow<CopyPathInput>(OperationsWorkflows.FilesystemMv,
                input,
                FilesystemOperations.MvAsync,
                agent));
        Register(OperationsWorkflows.FilesystemExport,
            (input, agent) => new OperationWorkflow<ExportPathInput>(OperationsWorkflows.FilesystemExport,
                input,
                FilesystemOperations.ExportAsync,
                agent));
        Register(OperationsWorkflows.FilesystemImport,
            (input, agent) => new OperationWorkflow<ImportPathInput>(OperationsWorkflows.FilesystemImport,
                input,
                FilesystemOperations.ImportAsync,
                agent));
        Register(OperationsWorkflows.LayoutFdiskQuery,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.LayoutFdiskQuery,
                input,
                LayoutOperations.FdiskQueryAsync,
                agent));
        Register(OperationsWorkflows.LayoutMounts,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.LayoutMounts,
                input,
                LayoutOperations.MountsAsync,
                agent));
        Register(OperationsWorkflows.LayoutInodes,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.LayoutInodes,
                input,
                LayoutOperations.InodesAsync,
                agent));
        Register(OperationsWorkflows.LayoutOpenDeleted,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.LayoutOpenDeleted,
                input,
                LayoutOperations.OpenDeletedAsync,
                agent));
        Register(OperationsWorkflows.LayoutSmart,
            (input, agent) => new OperationWorkflow<DeviceInput>(OperationsWorkflows.LayoutSmart,
                input,
                LayoutOperations.SmartAsync,
                agent));
        Register(OperationsWorkflows.LayoutSmartTest,
            (input, agent) => new OperationWorkflow<SmartTestInput>(OperationsWorkflows.LayoutSmartTest,
                input,
                LayoutOperations.SmartTestAsync,
                agent));
        Register(OperationsWorkflows.LayoutBcacheInfo,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.LayoutBcacheInfo,
                input,
                LayoutOperations.BcacheInfoAsync,
                agent));
        Register(OperationsWorkflows.LayoutBcacheAudit,
            (input, agent) => new OperationWorkflow<BaselineInput>(OperationsWorkflows.LayoutBcacheAudit,
                input,
                LayoutOperations.BcacheAuditAsync,
                agent));
        Register(OperationsWorkflows.PerformanceProcesses,
            (input, agent) => new OperationWorkflow<ProcessListInput>(OperationsWorkflows.PerformanceProcesses,
                input,
                PerformanceOperations.ProcessesAsync,
                agent));
        Register(OperationsWorkflows.PerformanceCpuSample,
            (input, agent) => new OperationWorkflow<SampleInput>(OperationsWorkflows.PerformanceCpuSample,
                input,
                PerformanceOperations.CpuSampleAsync,
                agent));
        Register(OperationsWorkflows.PerformanceMemoryBreakdown,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.PerformanceMemoryBreakdown,
                input,
                PerformanceOperations.MemoryBreakdownAsync,
                agent));
        Register(OperationsWorkflows.PerformanceIoSample,
            (input, agent) => new OperationWorkflow<SampleInput>(OperationsWorkflows.PerformanceIoSample,
                input,
                PerformanceOperations.IoSampleAsync,
                agent));
        Register(OperationsWorkflows.PerformanceDstatSample,
            (input, agent) => new OperationWorkflow<SampleInput>(OperationsWorkflows.PerformanceDstatSample,
                input,
                PerformanceOperations.DstatSampleAsync,
                agent));
        Register(OperationsWorkflows.PerformancePressure,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.PerformancePressure,
                input,
                PerformanceOperations.PressureAsync,
                agent));
        Register(OperationsWorkflows.PerformanceOomHistory,
            (input, agent) => new OperationWorkflow<EventWindowInput>(OperationsWorkflows.PerformanceOomHistory,
                input,
                PerformanceOperations.OomHistoryAsync,
                agent));
        Register(OperationsWorkflows.NvidiaSmi,
            (input, agent) =>
                new OperationWorkflow<EmptyInput>(OperationsWorkflows.NvidiaSmi,
                    input,
                    NvidiaOperations.SmiAsync,
                    agent));
        Register(OperationsWorkflows.NvidiaProcesses,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.NvidiaProcesses,
                input,
                NvidiaOperations.ProcessesAsync,
                agent));
        Register(OperationsWorkflows.NvidiaSample,
            (input, agent) => new OperationWorkflow<SampleInput>(OperationsWorkflows.NvidiaSample,
                input,
                NvidiaOperations.SampleAsync,
                agent));
        Register(OperationsWorkflows.NvidiaErrors,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.NvidiaErrors,
                input,
                NvidiaOperations.ErrorsAsync,
                agent));
        Register(OperationsWorkflows.NvidiaTopology,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.NvidiaTopology,
                input,
                NvidiaOperations.TopologyAsync,
                agent));
        Register(OperationsWorkflows.NvidiaDriverInfo,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.NvidiaDriverInfo,
                input,
                NvidiaOperations.DriverInfoAsync,
                agent));
        Register(OperationsWorkflows.DockerInspect,
            (input, agent) => new OperationWorkflow<DockerContainerInput>(OperationsWorkflows.DockerInspect,
                input,
                DockerOperations.InspectAsync,
                agent));
        Register(OperationsWorkflows.DockerStatsSample,
            (input, agent) => new OperationWorkflow<SampleInput>(OperationsWorkflows.DockerStatsSample,
                input,
                DockerOperations.StatsSampleAsync,
                agent));
        Register(OperationsWorkflows.DockerEvents,
            (input, agent) => new OperationWorkflow<ContainerEventsInput>(OperationsWorkflows.DockerEvents,
                input,
                DockerOperations.EventsAsync,
                agent));
        Register(OperationsWorkflows.DockerDiagnoseStartup,
            (input, agent) => new OperationWorkflow<DockerContainerInput>(OperationsWorkflows.DockerDiagnoseStartup,
                input,
                DockerOperations.DiagnoseStartupAsync,
                agent));
        Register(OperationsWorkflows.DockerStop,
            (input, agent) => new OperationWorkflow<ContainerActionInput>(OperationsWorkflows.DockerStop,
                input,
                DockerOperations.StopAsync,
                agent));
        Register(OperationsWorkflows.DockerStart,
            (input, agent) => new OperationWorkflow<DockerContainerInput>(OperationsWorkflows.DockerStart,
                input,
                DockerOperations.StartAsync,
                agent));
        Register(OperationsWorkflows.DockerRestart,
            (input, agent) => new OperationWorkflow<ContainerActionInput>(OperationsWorkflows.DockerRestart,
                input,
                DockerOperations.RestartAsync,
                agent));
        Register(OperationsWorkflows.DockerFilesystemExport,
            (input, agent) => new OperationWorkflow<ContainerExportInput>(OperationsWorkflows.DockerFilesystemExport,
                input,
                DockerOperations.FilesystemExportAsync,
                agent));
        Register(OperationsWorkflows.DockerFilesystemCpTo,
            (input, agent) => new OperationWorkflow<ContainerCopyInput>(OperationsWorkflows.DockerFilesystemCpTo,
                input,
                DockerOperations.FilesystemCpToAsync,
                agent));
        Register(OperationsWorkflows.DockerFilesystemCpFrom,
            (input, agent) => new OperationWorkflow<ContainerCopyInput>(OperationsWorkflows.DockerFilesystemCpFrom,
                input,
                DockerOperations.FilesystemCpFromAsync,
                agent));
        Register(OperationsWorkflows.SwarmServiceList,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.SwarmServiceList,
                input,
                SwarmOperations.ServiceListAsync,
                agent));
        Register(OperationsWorkflows.SwarmServiceInspect,
            (input, agent) => new OperationWorkflow<SwarmServiceInput>(OperationsWorkflows.SwarmServiceInspect,
                input,
                SwarmOperations.ServiceInspectAsync,
                agent));
        Register(OperationsWorkflows.SwarmServicePs,
            (input, agent) => new OperationWorkflow<SwarmServiceInput>(OperationsWorkflows.SwarmServicePs,
                input,
                SwarmOperations.ServicePsAsync,
                agent));
        Register(OperationsWorkflows.SwarmServiceLogs,
            (input, agent) => new OperationWorkflow<SwarmLogsInput>(OperationsWorkflows.SwarmServiceLogs,
                input,
                SwarmOperations.ServiceLogsAsync,
                agent));
        Register(OperationsWorkflows.SwarmServiceUpdateStatus,
            (input, agent) => new OperationWorkflow<SwarmServiceInput>(OperationsWorkflows.SwarmServiceUpdateStatus,
                input,
                SwarmOperations.ServiceUpdateStatusAsync,
                agent));
        Register(OperationsWorkflows.SwarmServiceStop,
            (input, agent) => new OperationWorkflow<SwarmChangeInput>(OperationsWorkflows.SwarmServiceStop,
                input,
                SwarmOperations.ServiceStopAsync,
                agent));
        Register(OperationsWorkflows.SwarmServiceStart,
            (input, agent) => new OperationWorkflow<SwarmChangeInput>(OperationsWorkflows.SwarmServiceStart,
                input,
                SwarmOperations.ServiceStartAsync,
                agent));
        Register(OperationsWorkflows.SwarmServiceUpdate,
            (input, agent) => new OperationWorkflow<SwarmChangeInput>(OperationsWorkflows.SwarmServiceUpdate,
                input,
                SwarmOperations.ServiceUpdateAsync,
                agent));
        Register(OperationsWorkflows.SwarmServiceRollback,
            (input, agent) => new OperationWorkflow<SwarmChangeInput>(OperationsWorkflows.SwarmServiceRollback,
                input,
                SwarmOperations.ServiceRollbackAsync,
                agent));
        Register(OperationsWorkflows.SystemInfo,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.SystemInfo,
                input,
                SystemOperations.SystemInfoAsync,
                agent));
        Register(OperationsWorkflows.SystemUptime,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.SystemUptime,
                input,
                SystemOperations.SystemUptimeAsync,
                agent));
        Register(OperationsWorkflows.SystemctlList,
            (input, agent) => new OperationWorkflow<ServiceListInput>(OperationsWorkflows.SystemctlList,
                input,
                SystemOperations.SystemctlListAsync,
                agent));
        Register(OperationsWorkflows.SystemctlStart,
            (input, agent) => new OperationWorkflow<ServiceInput>(OperationsWorkflows.SystemctlStart,
                input,
                SystemOperations.SystemctlStartAsync,
                agent));
        Register(OperationsWorkflows.SystemctlStop,
            (input, agent) => new OperationWorkflow<ServiceInput>(OperationsWorkflows.SystemctlStop,
                input,
                SystemOperations.SystemctlStopAsync,
                agent));
        Register(OperationsWorkflows.SystemJournal,
            (input, agent) => new OperationWorkflow<JournalInput>(OperationsWorkflows.SystemJournal,
                input,
                SystemOperations.SystemJournalAsync,
                agent));
        Register(OperationsWorkflows.SystemTimers,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.SystemTimers,
                input,
                SystemOperations.SystemTimersAsync,
                agent));
        Register(OperationsWorkflows.AptInspect,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.AptInspect,
                input,
                SystemOperations.AptInspectAsync,
                agent));
        Register(OperationsWorkflows.AptInstall,
            (input, agent) => new OperationWorkflow<PackageInput>(OperationsWorkflows.AptInstall,
                input,
                SystemOperations.AptInstallAsync,
                agent));
        Register(OperationsWorkflows.AptUpgrade,
            (input, agent) => new OperationWorkflow<PackageUpgradeInput>(OperationsWorkflows.AptUpgrade,
                input,
                SystemOperations.AptUpgradeAsync,
                agent));
        Register(OperationsWorkflows.AptUpdate,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.AptUpdate, input, AptMaintenanceOperations.UpdateAsync, agent));
        Register(OperationsWorkflows.AptPlan, (input, agent) => new AptPlanWorkflow(input, agent));
        Register(OperationsWorkflows.AptUpgradeAll,
            (input, agent) => new OperationWorkflow<AptApplyInput>(OperationsWorkflows.AptUpgradeAll, input, AptMaintenanceOperations.UpgradeAsync, agent));
        Register(OperationsWorkflows.AptAutoremove,
            (input, agent) => new OperationWorkflow<AptApplyInput>(OperationsWorkflows.AptAutoremove, input, AptMaintenanceOperations.AutoremoveAsync, agent));
        Register(OperationsWorkflows.SystemReboot,
            (input, agent) => new OperationWorkflow<RebootInput>(OperationsWorkflows.SystemReboot,
                input,
                SystemOperations.SystemRebootAsync,
                agent));
        Register(OperationsWorkflows.SystemExecuteScript,
            (input, agent) => new OperationWorkflow<ScriptInput>(OperationsWorkflows.SystemExecuteScript,
                input,
                SystemOperations.SystemExecuteScriptAsync,
                agent));
        Register(OperationsWorkflows.DatabaseDiscover,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.DatabaseDiscover,
                input,
                DatabaseOperations.DiscoverAsync,
                agent));
        Register(OperationsWorkflows.DatabaseListDatabases,
            (input, agent) => new OperationWorkflow<DatabaseInput>(OperationsWorkflows.DatabaseListDatabases,
                input,
                DatabaseOperations.ListDatabasesAsync,
                agent));
        Register(OperationsWorkflows.DatabaseListTables,
            (input, agent) => new OperationWorkflow<DatabaseNameInput>(OperationsWorkflows.DatabaseListTables,
                input,
                DatabaseOperations.ListTablesAsync,
                agent));
        Register(OperationsWorkflows.DatabaseDescribeTable,
            (input, agent) => new OperationWorkflow<DatabaseTableInput>(OperationsWorkflows.DatabaseDescribeTable,
                input,
                DatabaseOperations.DescribeTableAsync,
                agent));
        Register(OperationsWorkflows.DatabaseQueryTable,
            (input, agent) => new OperationWorkflow<DatabaseQueryInput>(OperationsWorkflows.DatabaseQueryTable,
                input,
                DatabaseOperations.QueryTableAsync,
                agent));
        Register(OperationsWorkflows.DatabaseQuerySql,
            (input, agent) => new OperationWorkflow<DatabaseSqlInput>(OperationsWorkflows.DatabaseQuerySql,
                input,
                DatabaseOperations.QuerySqlAsync,
                agent));
        Register(OperationsWorkflows.DatabaseExecuteSql,
            (input, agent) => new OperationWorkflow<DatabaseSqlInput>(OperationsWorkflows.DatabaseExecuteSql,
                input,
                DatabaseOperations.ExecuteSqlAsync,
                agent));
        Register(OperationsWorkflows.DatabaseDiagnostics,
            (input, agent) => new OperationWorkflow<DatabaseInput>(OperationsWorkflows.DatabaseDiagnostics,
                input,
                DatabaseOperations.DiagnosticsAsync,
                agent));
        Register(OperationsWorkflows.NetworkingInterfaces,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.NetworkingInterfaces,
                input,
                NetworkingOperations.InterfacesAsync,
                agent));
        Register(OperationsWorkflows.NetworkingRoutes,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.NetworkingRoutes,
                input,
                NetworkingOperations.RoutesAsync,
                agent));
        Register(OperationsWorkflows.NetworkingSockets,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.NetworkingSockets,
                input,
                NetworkingOperations.SocketsAsync,
                agent));
        Register(OperationsWorkflows.NetworkingDnsQuery,
            (input, agent) => new OperationWorkflow<DnsQueryInput>(OperationsWorkflows.NetworkingDnsQuery,
                input,
                NetworkingOperations.DnsQueryAsync,
                agent));
        Register(OperationsWorkflows.NetworkingProbe,
            (input, agent) => new OperationWorkflow<NetworkProbeInput>(OperationsWorkflows.NetworkingProbe,
                input,
                NetworkingOperations.ProbeAsync,
                agent));
        Register(OperationsWorkflows.NetworkingTrafficSample,
            (input, agent) => new OperationWorkflow<SampleInput>(OperationsWorkflows.NetworkingTrafficSample,
                input,
                NetworkingOperations.TrafficSampleAsync,
                agent));
        Register(OperationsWorkflows.NetworkingFirewallInfo,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.NetworkingFirewallInfo,
                input,
                NetworkingOperations.FirewallInfoAsync,
                agent));
        Register(OperationsWorkflows.NetworkingTlsCheck,
            (input, agent) => new OperationWorkflow<NetworkProbeInput>(OperationsWorkflows.NetworkingTlsCheck,
                input,
                NetworkingOperations.TlsCheckAsync,
                agent));
        Register(OperationsWorkflows.SecuritySecureBoot,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.SecuritySecureBoot,
                input,
                SecurityOperations.SecureBootAsync,
                agent));
        Register(OperationsWorkflows.SecurityDiskEncryption,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.SecurityDiskEncryption,
                input,
                SecurityOperations.DiskEncryptionAsync,
                agent));
        Register(OperationsWorkflows.SecuritySshAudit,
            (input, agent) => new OperationWorkflow<SshAuditInput>(OperationsWorkflows.SecuritySshAudit,
                input,
                SecurityOperations.SshAuditAsync,
                agent));
        Register(OperationsWorkflows.SshDisable,
            (input, agent) => new DisableSshWorkflow(input, agent));
        Register(OperationsWorkflows.SshOpen,
            (input, agent) => new OpenTemporarySshWorkflow(input, agent));
        Register(OperationsWorkflows.SshRecover,
            (input, agent) => new RecoverSshLeaseWorkflow(input, agent));
        Register(OperationsWorkflows.SshCheckLease,
            (input, agent) => new CheckSshLeaseWorkflow(input, agent));
        Register(OperationsWorkflows.SecurityFail2banStatus,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.SecurityFail2banStatus,
                input,
                SecurityOperations.Fail2banStatusAsync,
                agent));
        Register(OperationsWorkflows.SecurityCleanTrafficStatus,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.SecurityCleanTrafficStatus,
                input,
                SecurityOperations.CleanTrafficStatusAsync,
                agent));
        Register(OperationsWorkflows.SecurityAuthEvents,
            (input, agent) => new OperationWorkflow<EventWindowInput>(OperationsWorkflows.SecurityAuthEvents,
                input,
                SecurityOperations.AuthEventsAsync,
                agent));
        Register(OperationsWorkflows.SecurityAccountsAudit,
            (input, agent) => new OperationWorkflow<EmptyInput>(OperationsWorkflows.SecurityAccountsAudit,
                input,
                SecurityOperations.AccountsAuditAsync,
                agent));
        Register(OperationsWorkflows.SecurityBaselineAudit,
            (input, agent) => new OperationWorkflow<BaselineInput>(OperationsWorkflows.SecurityBaselineAudit,
                input,
                SecurityOperations.BaselineAuditAsync,
                agent));
    }
}
