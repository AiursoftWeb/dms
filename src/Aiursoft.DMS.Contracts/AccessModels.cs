namespace Aiursoft.DMS.Contracts;

public sealed record AccessMachine(Guid Id, string Name, bool Disabled);
public sealed record RiskPermission(RiskLevel Risk, bool CanRequest, bool AutoApprove, bool CanApprove);
public sealed record AccessSnapshot(List<AccessMachine> Machines);
public sealed record CurrentUser(string Id, string UserName, bool CanApproveOwnRequests, List<string> Roles, List<RiskPermission> RiskPermissions, List<string> Permissions);
public sealed record AgentKeyResult(Guid MachineId, string Key);
