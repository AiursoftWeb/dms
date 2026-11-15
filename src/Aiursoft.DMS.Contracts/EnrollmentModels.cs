using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.DMS.Contracts;

public sealed record JoinKeyInput(Guid ClusterId, string Name, int Days = 7);
public sealed record JoinKeySummary(Guid Id, Guid ClusterId, string Name, string IssuerId, DateTime ExpiresAt, bool Revoked);
public sealed record IssuedJoinKey(JoinKeySummary Details, string Key);
public sealed record EnrollmentInput(Guid Id, Guid InstallationId, string ClusterName, string MachineName, string ReceiptKey, string AgentKey);
public sealed record EnrollmentStatus(Guid Id, Guid InstallationId, Guid ClusterId, string MachineName, Guid LockboxId, Guid WorkflowId, Guid? MachineId, string State);
public sealed class EnrollmentResponse : AiurResponse
{
    public EnrollmentStatus? Data { get; set; }
}
