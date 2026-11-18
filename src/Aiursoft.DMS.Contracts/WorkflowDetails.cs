namespace Aiursoft.DMS.Contracts;

public sealed record WorkflowDetails(
    Guid WorkflowId, Guid RootWorkflowId, Guid? ParentWorkflowId,
    LockboxId? LockboxId, Guid? MachineId, string Definition,
    RunState State, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt, string Result,
    WorkflowOrigin Origin = WorkflowOrigin.Manual, WorkflowTrigger Trigger = WorkflowTrigger.ManualRequest,
    string? ScheduleId = null, string? ScheduleExpression = null, DateTime? ScheduledAt = null);
