using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.DMS.Contracts;

public sealed record JoinKeyInput(string Name, int Days = 7);
public sealed record JoinKeySummary(Guid Id, string Name, string IssuerId, DateTime ExpiresAt, bool Revoked);
public sealed record IssuedJoinKey(JoinKeySummary Details, string Key);
public sealed record EnrollmentInput(Guid Id, Guid InstallationId, string MachineName, string ReceiptKey, string AgentKey);
public sealed record EnrollmentStatus(Guid Id, Guid InstallationId, string MachineName, LockboxId LockboxId, Guid? WorkflowId, Guid? MachineId, string State);
public sealed class EnrollmentResponse : AiurResponse
{
    public EnrollmentStatus? Data { get; set; }
}
