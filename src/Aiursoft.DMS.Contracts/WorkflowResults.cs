namespace Aiursoft.DMS.Contracts;

public sealed record ProcessResult(int ExitCode, string Output = "", string ErrorOutput = "");
public sealed record PerformanceResult(string Hostname, int Processors, string Memory, string Load);
public sealed record AgentStatusResult(Guid InstallationId, string Version, int ActiveTasks, int PendingEvents);
public sealed record MachineSummary(Guid Id, string Name, string AgentVersion, DateTime? LastSeen, bool Disabled);
