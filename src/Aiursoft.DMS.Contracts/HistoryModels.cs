using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.DMS.Contracts;

// History reads are open to every authenticated human key. Facts deliberately exclude
// parameters, results, free-text reasons and credentials; requesters, approvers and
// machines may be picked by readable name and are resolved by the Controller.
public sealed record HistoryQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? Until { get; init; }
    public string? Requester { get; init; }
    public string? Approver { get; init; }
    public string? Machine { get; init; }
    public string? Definition { get; init; }
    public RiskLevel? Risk { get; init; }
    public DecisionState? Decision { get; init; }
    public bool? AutomaticApproval { get; init; }
    public RunState? State { get; init; }
    public WorkflowOrigin? Origin { get; init; }
    public WorkflowTrigger? Trigger { get; init; }
    public LockboxId? LockboxId { get; init; }
    public Guid? WorkflowId { get; init; }
    public Guid? RootWorkflowId { get; init; }
    public int Page { get; init; }
}

[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "Public JSON response properties are consumed by API and SDK clients.")]
public sealed record HistoryRow(Guid EventId, long ControllerSequence, WorkflowAuditEntry Fact,
    string? RequesterName, string? ApproverName, string? MachineName);

[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "Public JSON response properties are consumed by API and SDK clients.")]
public sealed record HistoryPage(IReadOnlyList<HistoryRow> Entries, HistoryQuery Echo, bool HasNextPage);
