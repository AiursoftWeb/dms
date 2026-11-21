namespace Aiursoft.DMS.Contracts;

public sealed record ProcessResult(int ExitCode, string Output = "", string ErrorOutput = "");
public sealed record PerformanceResult(string Hostname, int Processors, string Memory, string Load);
public sealed record AgentStatusResult(Guid InstallationId, string Version, int ActiveTasks, int PendingEvents);
public sealed record MachineSummary(Guid Id, string Name, string AgentVersion, DateTime? LastSeen, bool Disabled);

public sealed record DiskUsageResult(Guid WorkflowId, long TotalBytes, long UsedBytes, long AvailableBytes, int Filesystems);
// Historical version-1 Controller result shape. Keep it available to SDK history readers.
public sealed record MachineDiskUsage(Guid MachineId, DiskUsageResult Usage);
public sealed record ClusterDiskUsageResult(long TotalBytes, long UsedBytes, long AvailableBytes, int MachineCount, List<MachineDiskUsage> Machines);

public sealed record DiskOverviewRow(Guid MachineId, string Name, string Status,
    Guid? WorkflowId, long? TotalBytes, long? UsedBytes, long? AvailableBytes, decimal? UsedPercent);
public sealed record DiskOverviewResult(int TargetMachines, int MeasuredMachines, long? TotalBytes,
    long? UsedBytes, long? AvailableBytes, List<DiskOverviewRow> Machines);
public sealed record AgentHealthRow(Guid MachineId, string Name, bool Online, string Status,
    Guid? WorkflowId, string? Version, int? ActiveTasks, int? PendingEvents);

// Inspection details belong in the trace archive, not the workflow result or submission receipt.
public sealed record BoxInspectionResult;
