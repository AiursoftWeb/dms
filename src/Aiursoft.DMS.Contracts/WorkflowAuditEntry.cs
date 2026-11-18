namespace Aiursoft.DMS.Contracts;

public enum WorkflowAuditKind { LockboxSubmitted, LockboxApproved, LockboxDenied, WorkflowCreated, WorkflowStateChanged }

// Deliberately excludes parameters, results, free-text reasons and credentials.
public sealed record WorkflowAuditEntry(
    WorkflowAuditKind Kind, DateTime OccurredAt, DateTime RecordedAt,
    Guid? WorkflowId, Guid? RootWorkflowId, Guid? ParentWorkflowId, LockboxId LockboxId,
    WorkflowLocation? Location, Guid? MachineId, Guid? InstallationId, string Definition, string Version,
    string? RequesterId, string? ApproverId, bool? AutomaticApproval, RiskLevel? RequestedRisk,
    string? RequestedDefinition, string? RequestedVersion,
    DecisionState? Decision, DateTime? DecidedAt, RunState? PreviousState, RunState? State);
