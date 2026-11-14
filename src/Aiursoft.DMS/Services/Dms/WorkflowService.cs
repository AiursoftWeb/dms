using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class WorkflowService(TemplateDbContext db, AccessService access, ControllerGate gate, WorkflowCatalog catalog)
{
    public Task<WorkflowCatalogResult> ListDefinitionsAsync(string user, Guid cluster) => gate.RunAsync(async () =>
    {
        await access.RequireViewAsync(user, cluster);
        var definitions = new List<WorkflowDefinitionSummary>();
        foreach (var definition in catalog.PublicDefinitions())
        {
            var risk = definition.PublicRisk!.Value;
            var grant = await access.GrantAsync(user, cluster, risk);
            definitions.Add(new WorkflowDefinitionSummary(definition.Name, definition.Version, definition.Location,
                risk, definition.RequiresTargets, grant?.CanRequest == true, grant?.AutoApprove == true, grant?.CanApprove == true));
        }
        var selfApproval = await db.Users.Where(u => u.Id == user).Select(u => u.CanApproveOwnRequests).SingleAsync();
        access.Audit(user, "workflow.definitions", cluster);
        await db.SaveChangesAsync();
        return new WorkflowCatalogResult(cluster, selfApproval, definitions);
    });

    public Task<LockboxRequest> SubmitAsync(string user, OperationRequest request) => gate.RunAsync(async () =>
    {
        if (request.Reason == null || request.MachineName == null || request.MachineIds == null || request.Workflow == null || request.Input == null)
            throw new DmsException(400, "Request fields cannot be null.");
        if (request.Input.Length > 24000) throw new DmsException(400, "Workflow input exceeds the size limit.");
        var prior = await db.Lockboxes.SingleOrDefaultAsync(l => l.RequesterId == user && l.ClientRequestId == request.RequestId);
        if (prior != null)
        {
            await access.RequireViewAsync(user, prior.ClusterId);
            if (prior.Parameters != JsonSerializer.Serialize(request)) throw new DmsException(409, "Request ID already belongs to different parameters.");
            return prior;
        }
        if (request.RequestId == Guid.Empty || request.Reason.Length is < 1 or > 2000)
            throw new DmsException(400, "A request ID and a reason of at most 2000 characters are required.");
        var definition = catalog.Find(request.Workflow);
        var risk = definition != null
            ? definition.PublicRisk ?? throw new DmsException(400, "This workflow is not available as an external operation.")
            : request.Workflow is WorkflowNames.AddMachine or WorkflowNames.Kill ? RiskLevel.Medium
            : throw new DmsException(400, "This workflow is not available as an external operation.");
        try { definition?.Validate(request.Input); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        { throw new DmsException(400, "Invalid workflow input: " + exception.Message); }
        if (!await db.Clusters.AnyAsync(c => c.Id == request.ClusterId)) throw new DmsException(404, "Cluster not found.");
        var grant = await access.GrantAsync(user, request.ClusterId, risk);
        if (grant?.CanRequest != true) throw new DmsException(403, "You cannot request this risk level in this cluster.");
        var targets = new List<Guid>();
        if (definition?.RequiresTargets == true)
        {
            if (request.AllMachines && request.MachineIds.Count != 0) throw new DmsException(400, "Choose explicit machines or all machines, not both.");
            targets = request.AllMachines
                ? await db.Machines.Where(m => m.ClusterId == request.ClusterId && !m.Disabled).Select(m => m.Id).ToListAsync()
                : request.MachineIds.Distinct().ToList();
            if (targets.Count is < 1 or > 1000 || await db.Machines.CountAsync(m => targets.Contains(m.Id) && m.ClusterId == request.ClusterId && !m.Disabled) != targets.Count)
                throw new DmsException(400, "Select between 1 and 1000 active machines in this cluster.");
        }
        else if (request.AllMachines || request.MachineIds.Count != 0)
            throw new DmsException(400, "This workflow does not accept machine targets.");
        if (request.Workflow == WorkflowNames.AddMachine && (string.IsNullOrWhiteSpace(request.MachineName) || request.MachineName.Length > 100))
            throw new DmsException(400, "Machine name must contain 1 to 100 characters.");
        if (request.Workflow == WorkflowNames.Kill && !await db.WorkflowRuns.AnyAsync(r => r.Id == request.TargetWorkflowId && r.ClusterId == request.ClusterId))
            throw new DmsException(404, "Target workflow not found in this cluster.");
        var lockbox = new LockboxRequest
        {
            ClientRequestId = request.RequestId, ClusterId = request.ClusterId, RequesterId = user,
            Definition = request.Workflow, Risk = risk, Version = definition?.Version ?? WorkflowNames.Version, Parameters = JsonSerializer.Serialize(request),
            TargetMachines = JsonSerializer.Serialize(targets.Order()), Reason = request.Reason,
            WorkflowId = Guid.NewGuid()
        };
        db.Lockboxes.Add(lockbox);
        db.WorkflowRuns.Add(new WorkflowRun
        {
            Id = lockbox.WorkflowId, RootId = lockbox.WorkflowId, LockboxId = lockbox.Id,
            ClusterId = lockbox.ClusterId, Definition = WorkflowNames.Lockbox, State = RunState.WaitingApproval
        });
        access.Audit(user, "lockbox.submit", lockbox.Id, $"Risk={risk}; Targets={targets.Count}");
        if (grant.AutoApprove) await ApproveAsync(lockbox, null, "Automatic approval by the cluster risk policy.");
        await db.SaveChangesAsync();
        return lockbox;
    });

    public Task<LockboxRequest> DecideAsync(string user, Guid id, bool approve, string reason) => gate.RunAsync(async () =>
    {
        if (reason == null || reason.Length > 2000) throw new DmsException(400, "Decision reason is too long.");
        var item = await db.Lockboxes.SingleOrDefaultAsync(l => l.Id == id) ?? throw new DmsException(404, "Lockbox not found.");
        var grant = await access.GrantAsync(user, item.ClusterId, item.Risk);
        if (grant?.CanApprove != true) throw new DmsException(403, "You cannot approve this risk level in this cluster.");
        var actor = await db.Users.SingleAsync(u => u.Id == user);
        if (item.RequesterId == user && !actor.CanApproveOwnRequests) throw new DmsException(403, "Self approval is not allowed.");
        if (item.Decision != DecisionState.Pending) throw new DmsException(409, "This request has already been decided.");
        if (approve) await ApproveAsync(item, user, reason);
        else
        {
            item.Decision = DecisionState.Denied; item.ApproverId = user; item.DecidedAt = DateTime.UtcNow; item.DecisionReason = reason;
            var root = await db.WorkflowRuns.SingleAsync(r => r.Id == item.WorkflowId);
            root.State = RunState.Denied; root.FinishedAt = DateTime.UtcNow;
            access.Audit(user, "lockbox.deny", item.Id, reason);
        }
        await db.SaveChangesAsync();
        return item;
    });

    private async Task ApproveAsync(LockboxRequest item, string? user, string reason)
    {
        var definition = catalog.Find(item.Definition);
        if ((definition == null && item.Definition is not (WorkflowNames.AddMachine or WorkflowNames.Kill))
            || (definition != null && (definition.Version != item.Version || definition.PublicRisk != item.Risk)))
            throw new DmsException(409, "The requested workflow definition has changed; submit a new Lockbox request.");
        item.Decision = DecisionState.Approved; item.ApproverId = user; item.Automatic = user == null;
        item.DecidedAt = DateTime.UtcNow; item.DecisionReason = reason;
        var root = db.WorkflowRuns.Local.FirstOrDefault(r => r.Id == item.WorkflowId)
            ?? await db.WorkflowRuns.SingleAsync(r => r.Id == item.WorkflowId);
        await new LockboxWorkflow().ExecuteAsync(new ClusterWorkflowContext(db, root, item, catalog.Find(item.Definition)));
        access.Audit(user ?? "system", "lockbox.approve", item.Id, reason);
    }

    public Task<LockboxRequest> GetLockboxAsync(string user, Guid id) => gate.RunAsync(async () =>
    {
        var item = await db.Lockboxes.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id) ?? throw new DmsException(404, "Lockbox not found.");
        await access.RequireViewAsync(user, item.ClusterId);
        access.Audit(user, "lockbox.get", id); await db.SaveChangesAsync(); return item;
    });

    public Task<WorkflowDetails> GetWorkflowAsync(string user, Guid id) => gate.RunAsync(async () =>
    {
        var run = await db.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id)
            ?? throw new DmsException(404, "Workflow not found.");
        await access.RequireViewAsync(user, run.ClusterId);
        access.Audit(user, "operation.get", id);
        await db.SaveChangesAsync();
        return new WorkflowDetails(run.Id, run.RootId, run.ParentId, run.LockboxId, run.ClusterId,
            run.MachineId, run.Definition, run.State, run.CreatedAt, run.StartedAt, run.FinishedAt, run.Result);
    });

    public Task<List<WorkflowRun>> CallstackAsync(string user, Guid id) => gate.RunAsync(async () =>
    {
        var run = await db.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id) ?? throw new DmsException(404, "Workflow not found.");
        await access.RequireViewAsync(user, run.ClusterId);
        var all = await db.WorkflowRuns.AsNoTracking().Where(r => r.RootId == run.RootId).OrderBy(r => r.CreatedAt).ToListAsync();
        var selected = new HashSet<Guid> { id };
        while (true)
        {
            var count = selected.Count;
            foreach (var child in all.Where(r => r.ParentId != null && selected.Contains(r.ParentId.Value))) selected.Add(child.Id);
            if (count == selected.Count) break;
        }
        access.Audit(user, "operation.callstack", id); await db.SaveChangesAsync();
        return all.Where(r => selected.Contains(r.Id)).ToList();
    });
}
