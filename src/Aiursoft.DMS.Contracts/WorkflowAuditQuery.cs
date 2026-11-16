namespace Aiursoft.DMS.Contracts;

public sealed record WorkflowAuditQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? Until { get; init; }
    public Guid? MachineId { get; init; }
    public Guid? WorkflowId { get; init; }
    public Guid? RootWorkflowId { get; init; }
    public Guid? LockboxId { get; init; }
    public string? RequesterId { get; init; }
    public string? ApproverId { get; init; }
    public string? Definition { get; init; }
    public string? RequestedDefinition { get; init; }
    public WorkflowAuditKind? Kind { get; init; }
    public RunState? State { get; init; }
    public bool? AutomaticApproval { get; init; }
    public int Page { get; init; }
}

public sealed record WorkflowAuditRow(Guid EventId, long ControllerSequence, WorkflowAuditEntry Fact);
public sealed record WorkflowAuditPage(WorkflowAuditQuery Query, IReadOnlyList<WorkflowAuditRow> Entries, bool HasNextPage);
