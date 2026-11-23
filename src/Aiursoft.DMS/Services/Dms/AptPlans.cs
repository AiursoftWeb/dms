using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

internal static class AptPlans
{
    internal static bool IsApply(string definition) =>
        definition == OperationsWorkflows.AptUpgradeAll.Name || definition == OperationsWorkflows.AptAutoremove.Name;

    internal static MachineWorkflowDefinition<AptApplyInput, OperationResult> ApplyDefinition(string definition) =>
        definition == OperationsWorkflows.AptUpgradeAll.Name ? OperationsWorkflows.AptUpgradeAll : OperationsWorkflows.AptAutoremove;

    internal static async Task<IReadOnlyList<Guid>> SelectPreviewTargetsAsync(
        TemplateDbContext db, string user, string definition, AptApplyInput input)
    {
        var launch = await db.WorkflowLaunches.AsNoTracking().NotCacheable()
            .SingleOrDefaultAsync(item => item.RootId == input.PlanId && item.RequesterId == user &&
                item.Definition == OperationsWorkflows.AptPlan.Name && item.Version == OperationsWorkflows.AptPlan.Version)
            ?? throw new DmsException(404, "APT preview not found. Use the workflow ID of your own preview.");
        var operation = definition == OperationsWorkflows.AptUpgradeAll.Name ? AptMaintenance.UpgradeAll : AptMaintenance.Autoremove;
        var preview = OperationsWorkflows.AptPlan.ReadInput(launch.ReadParameters().Input);
        if (preview != new AptPlanInput(operation, input.Purge))
            throw new DmsException(400, "The APT preview must match the requested operation and purge option.");
        var state = await db.WorkflowRuns.AsNoTracking().NotCacheable().Where(run => run.Id == launch.RootId)
            .Select(run => (RunState?)run.State).SingleOrDefaultAsync();
        if (state == null || !state.Value.Terminal())
            throw new DmsException(409, "The APT preview has not finished. Check its workflow status.");

        return launch.ReadTargets();
    }

    internal static async Task<Dictionary<Guid, AptPlanResult>> ReadAsync(
        TemplateDbContext db, Guid previewId, IReadOnlyList<Guid> targets)
    {
        // Agent events cannot replace terminal results. The Lockbox pins the preview ID, its options and the exact target IDs.
        var runs = await db.WorkflowRuns.AsNoTracking().NotCacheable()
            .Where(run => run.ParentId == previewId && run.Definition == OperationsWorkflows.AptPlan.Name &&
                run.Version == OperationsWorkflows.AptPlan.Version && run.State == RunState.Succeeded &&
                run.MachineId.HasValue && targets.Contains(run.MachineId.Value))
            .Select(run => new { Machine = run.MachineId!.Value, run.Result }).ToListAsync();
        return runs.ToDictionary(run => run.Machine, run => OperationsWorkflows.AptPlan.ReadResult(run.Result));
    }

    internal static async Task RequireCompletedTargetsAsync(TemplateDbContext db, Guid previewId,
        IReadOnlyCollection<Guid> previewTargets, IReadOnlyList<Guid> targets)
    {
        if (targets.Any(id => !previewTargets.Contains(id)))
            throw new DmsException(409, "The selected machines differ from the APT preview. Preview the new targets or select machines from the original preview.");
        if ((await ReadAsync(db, previewId, targets)).Count != targets.Count)
            throw new DmsException(409, "Every selected machine needs a successful APT preview before approval.");
    }
}
