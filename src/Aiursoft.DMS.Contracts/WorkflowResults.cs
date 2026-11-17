namespace Aiursoft.DMS.Contracts;

public sealed record ProcessResult(int ExitCode, string Output = "", string ErrorOutput = "");
public sealed record PerformanceResult(string Hostname, int Processors, string Memory, string Load);
public sealed record AgentStatusResult(Guid InstallationId, string Version, int ActiveTasks, int PendingEvents);
public sealed record MachineSummary(Guid Id, string Name, string AgentVersion, DateTime? LastSeen, bool Disabled);

public sealed record DiskUsageResult(Guid WorkflowId, long TotalBytes, long UsedBytes, long AvailableBytes, int Filesystems);
public sealed record MachineDiskUsage(Guid MachineId, DiskUsageResult Usage);
public sealed record ClusterDiskUsageResult(long TotalBytes, long UsedBytes, long AvailableBytes, int MachineCount, List<MachineDiskUsage> Machines);

// Inspection details belong in the trace archive, not the workflow result or submission receipt.
public sealed record BoxInspectionResult;
