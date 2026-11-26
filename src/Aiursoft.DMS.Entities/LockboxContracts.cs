using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Entities;

public static class LockboxContracts
{
    public static LockboxDetails Details(this LockboxRequest box) => new(box.Id, box.ClientRequestId,
        box.RequesterId, box.Definition, box.Version, box.Risk, box.Parameters, box.TargetMachines,
        box.Reason, box.CreatedAt, box.Decision, box.ApproverId, box.Automatic, box.DecidedAt,
        box.DecisionReason, box.WorkflowId);
}
