namespace Aiursoft.DMS.Contracts;

public sealed record ClusterSummary(Guid Id, string Name);
public sealed record AccessMachine(Guid Id, Guid ClusterId, string Name, bool Disabled);
public sealed record RiskPermission(RiskLevel Risk, bool CanRequest, bool AutoApprove, bool CanApprove);
public sealed record AccessSnapshot(List<ClusterSummary> Clusters, List<AccessMachine> Machines);
public sealed record CurrentUser(string Id, string UserName, bool CanApproveOwnRequests, List<string> Roles, List<RiskPermission> RiskPermissions, List<string> Permissions);
public sealed record CreateClusterInput(Guid Id, string Name);
public sealed record ClusterNameInput(string Name);
public sealed record AgentKeyResult(Guid MachineId, string Key);
