namespace Aiursoft.DMS.Contracts;

public sealed record WorkflowDetails(
    Guid WorkflowId, Guid RootWorkflowId, Guid? ParentWorkflowId,
    Guid LockboxId, Guid ClusterId, Guid? MachineId, string Definition,
    RunState State, DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt, string Result);
