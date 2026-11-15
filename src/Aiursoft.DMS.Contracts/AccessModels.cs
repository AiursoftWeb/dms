namespace Aiursoft.DMS.Contracts;

public sealed record ClusterSummary(Guid Id, string Name);
public sealed record AccessUser(string Id, string UserName, string DisplayName, bool CanApproveOwnRequests);
public sealed record AccessMachine(Guid Id, Guid ClusterId, string Name, bool Disabled);
public sealed record RiskGrant(Guid ClusterId, string UserId, RiskLevel Risk, bool CanRequest, bool AutoApprove, bool CanApprove);
public sealed record AccessSnapshot(List<ClusterSummary> Clusters, List<AccessUser> Users, List<RiskGrant> Grants, List<AccessMachine> Machines);
public sealed record CurrentUser(string Id, string UserName, bool CanApproveOwnRequests, List<string> Permissions);
public sealed record CreateClusterInput(Guid Id, string Name);
public sealed record ClusterNameInput(string Name);
public sealed record SelfApprovalInput(string UserId, bool Enabled);
public sealed record AgentKeyResult(Guid MachineId, string Key);
