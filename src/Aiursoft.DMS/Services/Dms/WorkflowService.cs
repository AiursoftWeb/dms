using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;

namespace Aiursoft.DMS.Services.Dms;

public sealed class WorkflowService(TemplateDbContext db, AccessService access, ControllerGate gate, WorkflowCatalog catalog, AgentReleaseStore releases, DmsArtifactStore artifacts, PersonalSshKeys sshKeys, IDataProtectionProvider protection)
{
    public Task<WorkflowCatalogResult> ListDefinitionsAsync(string user) => gate.RunAsync(async () =>
    {
        await access.RequireViewAsync(user);
        var principal = await access.PrincipalAsync(user);
        var definitions = new List<WorkflowDefinitionSummary>();
        foreach (var definition in catalog.PublicDefinitions())
        {
            var risk = definition.Risk;
            var grant = DmsPermissions.ForRisk(principal, risk);
            definitions.Add(new WorkflowDefinitionSummary(definition.Name, definition.Version, definition.Location,
                risk, definition.RequiresTargets, grant.CanRequest, grant.AutoApprove, grant.CanApprove));
        }
        var selfApproval = DmsPermissions.CanApproveOwn(principal);
        access.Audit(user, "workflow.definitions");
        await db.SaveChangesAsync();
        return new WorkflowCatalogResult(selfApproval, definitions);
    });

    public Task<LockboxRequest> SubmitAsync(string user, OperationRequest request) => gate.RunAsync(() => SubmitCoreAsync(user, request));

    internal static AgentUpgradeInput ReadRequestedAgentUpgrade(LockboxRequest item) =>
        MachineWorkflows.AgentUpgrade.ReadInput(JsonSerializer.Deserialize<OperationRequest>(item.Parameters)!.Input);

    internal Task<LockboxRequest> ResubmitAgentUpgradeAsync(string user, Guid id, Guid requestId, string releaseIdentity) =>
        gate.RunAsync(async () =>
        {
            var original = await db.Lockboxes.AsNoTracking().NotCacheable()
                .SingleOrDefaultAsync(box => box.Id == id && box.RequesterId == user)
                ?? throw new DmsException(404, "Lockbox not found.");
            if (original.Decision != DecisionState.Pending || original.Definition != WorkflowNames.AgentUpgrade ||
                original.Version != MachineWorkflows.AgentUpgrade.Version)
                throw new DmsException(409, "This Agent upgrade cannot be resubmitted.");

            var current = releases.Read();
            if (!string.Equals(AgentReleaseStore.Identity(current), releaseIdentity, StringComparison.Ordinal))
                throw new DmsException(409, AgentReleaseStore.PreviewChangedMessage);
            if (AgentReleaseStore.Matches(ReadRequestedAgentUpgrade(original).Releases, current))
                throw new DmsException(409, AgentReleaseStore.OriginalAvailableMessage);
            var targets = JsonSerializer.Deserialize<List<Guid>>(original.TargetMachines);
            if (targets is not { Count: > 0 })
                throw new DmsException(409, "The original Agent upgrade has no target machines.");

            return await SubmitCoreAsync(user, MachineWorkflows.AgentUpgrade.Request(
                new AgentUpgradeInput(current), original.Reason, targets, requestId: requestId));
        });

    // Enrollment uses the same approval path while holding ControllerGate and a database transaction.
    internal async Task<LockboxRequest> SubmitCoreAsync(string user, OperationRequest request, Guid? enrollmentId = null)
    {
        if (request.Reason == null || request.MachineName == null || request.MachineIds == null || request.Workflow == null || request.Input == null)
            throw new DmsException(400, "Request fields cannot be null.");
        if (request.Input.Length > 24000) throw new DmsException(400, "Workflow input exceeds the size limit.");
        var definition = catalog.Find(request.Workflow);
        try
        {
            LegacyClusterWorkflowInput.Normalize(request, request.Workflow);
            request.Input = definition?.Normalize(request.Input) ?? request.Input;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        { throw new DmsException(400, "Invalid workflow input: " + exception.Message); }
        if (request.Input.Length > 24000) throw new DmsException(400, "Workflow input exceeds the size limit.");
        FrpsDeployInput? frpsInput = null;
        if (request.Workflow == MachineWorkflows.FrpsDeploy.Name)
        {
            frpsInput = MachineWorkflows.FrpsDeploy.ReadInput(request.Input);
            if (frpsInput.ProtectedToken != null)
                throw new DmsException(400, "Submit the FRPS token through the workflow token option.");
            frpsInput = frpsInput with { FrpsToken = string.IsNullOrEmpty(frpsInput.FrpsToken) ? null : frpsInput.FrpsToken };
            request.Input = MachineWorkflows.FrpsDeploy.Serialize(frpsInput);
        }
        var prior = await db.Lockboxes.SingleOrDefaultAsync(l => l.RequesterId == user && l.ClientRequestId == request.RequestId);
        if (prior != null)
        {
            if (enrollmentId.HasValue) throw new DmsException(409, "Enrollment ID already belongs to another request.");
            var principal = await access.PrincipalAsync(user);
            if (prior.Risk == RiskLevel.Open && !DmsPermissions.ForRisk(principal, RiskLevel.Open).CanRequest)
                throw new DmsException(403, "Run open workflows permission is required.");
            // Returning the caller's own submission receipt does not require global log-reading permission.
            // A retry keeps the original inferred scope, even if inventory changes.
            var frozen = JsonSerializer.Deserialize<OperationRequest>(prior.Parameters)!;
            LegacyClusterWorkflowInput.Normalize(frozen, prior.Definition);
            if (!request.AllMachines && request.MachineIds.Count == 0) request.MachineIds = frozen.MachineIds.ToList();
            if (request.Workflow == OperationsWorkflows.SshOpen.Name && frozen.Workflow == request.Workflow)
            {
                var open = OperationsWorkflows.SshOpen.ReadInput(request.Input);
                var approved = OperationsWorkflows.SshOpen.ReadInput(frozen.Input);
                request.Input = OperationsWorkflows.SshOpen.Serialize(open with { Fingerprint = open.Fingerprint ?? approved.Fingerprint });
            }
            if (frpsInput != null && frozen.Workflow == request.Workflow)
            {
                var approved = MachineWorkflows.FrpsDeploy.ReadInput(frozen.Input);
                var approvedToken = approved.ProtectedToken == null ? null :
                    protection.CreateProtector(FrpsDeployInput.ProtectionPurpose).Unprotect(approved.ProtectedToken);
                if (approvedToken != frpsInput.FrpsToken)
                    throw new DmsException(409, "Request ID already belongs to different parameters.");
                request.Input = frozen.Input;
            }
            if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(frozen), JsonSerializer.SerializeToNode(request))) throw new DmsException(409, "Request ID already belongs to different parameters.");
            return prior;
        }
        if (definition is not { PublicEntry: true })
            throw new DmsException(400, "This workflow is not available as an external operation.");
        var risk = definition.Risk;
        if (request.RequestId == Guid.Empty || request.Reason.Length > 2000 ||
            (risk != RiskLevel.Open && request.Reason.Length == 0))
            throw new DmsException(400, risk == RiskLevel.Open
                ? "A request ID is required. The optional reason must be at most 2000 characters."
                : "A request ID and a reason of at most 2000 characters are required.");
        var grant = DmsPermissions.ForRisk(await access.PrincipalAsync(user), risk);
        if (!grant.CanRequest) throw new DmsException(403, risk == RiskLevel.Open
            ? "Run open workflows permission is required." : "You cannot request this risk level.");
        AptApplyInput? apt = null;
        IReadOnlyList<Guid>? previewTargets = null;
        if (AptPlans.IsApply(request.Workflow))
        {
            apt = AptPlans.ApplyDefinition(request.Workflow).ReadInput(request.Input);
            if (apt.Changes != null) throw new DmsException(400, "APT package plans must come from a completed preview.");
            previewTargets = await AptPlans.SelectPreviewTargetsAsync(db, user, request.Workflow, apt);
            if (!request.AllMachines && request.MachineIds.Count == 0)
                request.MachineIds = previewTargets.ToList();
        }
        var targets = new List<Guid>();
        if (definition.RequiresTargets)
        {
            if (definition.SingleMachineOnly && (request.AllMachines || request.MachineIds.Count > 1))
                throw new DmsException(400, "This workflow requires a single machine.");
            if (request.AllMachines && request.MachineIds.Count != 0) throw new DmsException(400, "Choose explicit machines or all machines, not both.");
            if (!request.AllMachines && request.MachineIds.Count == 0)
            {
                var candidates = await db.Machines.Where(m => !m.Disabled).Select(m => m.Id).Take(2).ToListAsync();
                if (candidates.Count != 1) throw new DmsException(400, definition.SingleMachineOnly
                    ? "Specify --machine-id to choose one active machine."
                    : "Specify --machine-id or --all-machines: exactly one active machine is required to omit the target.");
                request.MachineIds = candidates;
            }
            targets = request.AllMachines
                ? await db.Machines.Where(m => !m.Disabled).Select(m => m.Id).ToListAsync()
                : request.MachineIds.Distinct().ToList();
            if (targets.Count is < 1 or > 1000 || await db.Machines.CountAsync(m => targets.Contains(m.Id) && !m.Disabled) != targets.Count)
                throw new DmsException(400, "Select between 1 and 1000 active machines.");
        }
        else if (request.AllMachines || request.MachineIds.Count != 0)
            throw new DmsException(400, "This workflow does not accept machine targets.");
        if (apt != null) await AptPlans.RequireCompletedTargetsAsync(db, apt.PlanId, previewTargets!, targets);
        if (request.Workflow == WorkflowNames.Kill)
        {
            var target = ClusterWorkflows.Cancel.ReadInput(request.Input).TargetWorkflowId;
            if (!await db.WorkflowRuns.AnyAsync(r => r.Id == target)) throw new DmsException(404, "Target workflow not found.");
        }
        if (request.Workflow == WorkflowNames.AgentUpgrade) releases.RequireCurrent(MachineWorkflows.AgentUpgrade.ReadInput(request.Input));
        if (request.Workflow == OperationsWorkflows.SshOpen.Name)
        {
            var input = OperationsWorkflows.SshOpen.ReadInput(request.Input);
            var key = await sshKeys.RequireVerifiedAsync(user, input.KeyId, input.Fingerprint, CancellationToken.None);
            request.Input = OperationsWorkflows.SshOpen.Serialize(input with { Fingerprint = key.Fingerprint });
        }
        if (frpsInput?.FrpsToken is { } frpsToken)
        {
            request.Input = MachineWorkflows.FrpsDeploy.Serialize(new FrpsDeployInput(ProtectedToken:
                protection.CreateProtector(FrpsDeployInput.ProtectionPurpose).Protect(frpsToken)));
        }
        await RequireArtifactsAsync(definition, request.Input, user);
        var lockbox = new LockboxRequest
        {
            ClientRequestId = request.RequestId, RequesterId = user,
            Definition = request.Workflow, Risk = risk, Version = definition.Version, Parameters = JsonSerializer.Serialize(request),
            TargetMachines = JsonSerializer.Serialize(targets.Order()), Reason = request.Reason
        };
        db.Lockboxes.Add(lockbox);
        access.Audit(user, "lockbox.submit", lockbox.Id, $"Risk={risk}; Targets={targets.Count}");
        if (grant.AutoApprove)
            await ApproveAsync(lockbox, null, risk == RiskLevel.Open
                ? "Automatic approval by the Open workflow policy."
                : "Automatic approval by the requester's role permissions.", enrollmentId);
        await SaveDecisionAsync();
        return lockbox;
    }

    public Task<LockboxRequest> DecideAsync(string user, Guid id, bool approve, string reason) => gate.RunAsync(async () =>
    {
        if (reason == null || reason.Length > 2000) throw new DmsException(400, "Decision reason is too long.");
        var item = await db.Lockboxes.SingleOrDefaultAsync(l => l.Id == id) ?? throw new DmsException(404, "Lockbox not found.");
        var principal = await access.PrincipalAsync(user);
        if (!DmsPermissions.CanDecide(principal, item.Risk, item.RequesterId == user))
            throw new DmsException(403, "Approval permission for this risk level is required; your own Lockbox requests also require self-approval permission.");
        if (item.Decision != DecisionState.Pending) throw new DmsException(409, "This Lockbox request has already been decided.");
        if (approve) await ApproveAsync(item, user, reason);
        else
        {
            item.Decision = DecisionState.Denied; item.ApproverId = user; item.DecidedAt = DateTime.UtcNow; item.DecisionReason = reason;
            access.Audit(user, "lockbox.deny", item.Id, reason);
        }
        await SaveDecisionAsync();
        return item;
    });

    public Task<LockboxRequest> WithdrawAsync(string user, Guid id) => gate.RunAsync(async () =>
    {
        await access.PrincipalAsync(user);
        async Task<LockboxRequest> OwnedAsync() => await db.Lockboxes.SingleOrDefaultAsync(l => l.Id == id && l.RequesterId == user)
            ?? throw new DmsException(404, "Lockbox not found.");

        var item = await OwnedAsync();
        if (item.Decision == DecisionState.Pending)
        {
            item.Decision = DecisionState.Withdrawn;
            item.DecidedAt = DateTime.UtcNow;
            item.DecisionReason = "Withdrawn by the requester.";
            access.Audit(user, "lockbox.withdraw", id);
            try { await SaveDecisionAsync(); }
            catch (DmsException exception) when (exception.StatusCode == 409) { item = await OwnedAsync(); }
        }
        if (item.Decision == DecisionState.Withdrawn) return item;
        throw new DmsException(409, item.WorkflowId is { } workflow
            ? $"Already approved. To stop the workflow, request dms operation kill-workflow --id {workflow}."
            : $"Cannot withdraw a {item.Decision} Lockbox.");
    });

    private async Task ApproveAsync(LockboxRequest item, string? user, string reason, Guid? enrollmentId = null)
    {
        var definition = catalog.Find(item.Definition);
        if (definition is not { PublicEntry: true } || definition.Risk != item.Risk || definition.Version != item.Version)
            throw new DmsException(409, "The requested workflow definition has changed; submit a new Lockbox request.");
        var request = JsonSerializer.Deserialize<OperationRequest>(item.Parameters)!;
        LegacyClusterWorkflowInput.Normalize(request, item.Definition);
        var input = request.Input;
        if (item.Definition == WorkflowNames.AgentUpgrade)
            releases.RequireCurrent(MachineWorkflows.AgentUpgrade.ReadInput(input));
        if (AptPlans.IsApply(item.Definition))
        {
            var plan = AptPlans.ApplyDefinition(item.Definition).ReadInput(input);
            var scope = await AptPlans.SelectPreviewTargetsAsync(db, item.RequesterId, item.Definition, plan);
            var targets = JsonSerializer.Deserialize<Guid[]>(item.TargetMachines)!;
            await AptPlans.RequireCompletedTargetsAsync(db, plan.PlanId, scope, targets);
        }
        await RequireArtifactsAsync(definition, input, item.RequesterId);
        item.Decision = DecisionState.Approved; item.ApproverId = user; item.Automatic = user == null;
        item.DecidedAt = DateTime.UtcNow; item.DecisionReason = reason;
        var root = new WorkflowRun
        {
            LockboxId = item.Id, State = RunState.Queued,
            Definition = definition.Location == WorkflowLocation.Machine ? WorkflowNames.Batch : item.Definition,
            Input = input, Version = item.Version
        };
        root.RootId = root.Id;
        db.WorkflowRuns.Add(root);
        if (item.Definition == WorkflowNames.AddMachine && enrollmentId == null)
            enrollmentId = await db.AgentEnrollments.Where(e => e.LockboxId == item.Id).Select(e => (Guid?)e.Id).SingleOrDefaultAsync();
        db.WorkflowLaunches.Add(WorkflowLaunch.FromApproval(item, root.Id, enrollmentId));
        item.WorkflowId = root.Id;
        access.Audit(user ?? "system", "lockbox.approve", item.Id, reason);
    }

    // SaveChanges atomically commits the decision, execution root and audit outbox.
    // The decision concurrency token also protects against a stale second approver.
    private async Task SaveDecisionAsync()
    {
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw new DmsException(409, "This Lockbox request has already been decided.");
        }
        catch { db.ChangeTracker.Clear(); throw; }
    }

    public Task<LockboxPage> ListLockboxesAsync(string user, int page = 0, DecisionState? decision = null) => gate.RunAsync(async () =>
    {
        await access.RequireViewAsync(user);
        if (page is < 0 or > 10000 || (decision != null && !Enum.IsDefined(decision.Value)))
            throw new DmsException(400, "Invalid Lockbox page or decision filter.");
        var query = db.Lockboxes.AsNoTracking();
        if (decision != null) query = query.Where(b => b.Decision == decision);
        var items = await query.OrderByDescending(b => b.CreatedAt).ThenBy(b => b.Id).Skip(page * 50).Take(51).ToListAsync();
        access.Audit(user, "lockbox.list"); await db.SaveChangesAsync();
        return new LockboxPage(items.Take(50).Select(b => b.Details()).ToList(), page, items.Count > 50);
    });

    private async Task RequireArtifactsAsync(WorkflowDefinition? definition, string input, string owner)
    {
        foreach (var reference in definition?.Artifacts(input) ?? [])
        {
            var stored = artifacts.Get(reference.Id);
            if (stored.OwnerId != owner) throw new DmsException(403, "Workflow input artifacts must belong to the requester.");
            if (!DmsArtifactAccess.Matches(stored.Reference, reference))
                throw new DmsException(409, "Input artifact differs from its immutable uploaded metadata.");
            if (definition?.RetainInputArtifactsForAudit == true) await artifacts.RetainAuditInputAsync(reference.Id, owner);
        }
    }

    public Task<LockboxRequest> GetLockboxAsync(string user, Guid id) => ReadLockboxAsync(user, id, ownRequestOnly: false);

    internal Task<LockboxRequest> GetOwnedLockboxAsync(string user, Guid id) => ReadLockboxAsync(user, id, ownRequestOnly: true);

    private Task<LockboxRequest> ReadLockboxAsync(string user, Guid id, bool ownRequestOnly) => gate.RunAsync(async () =>
    {
        if (ownRequestOnly) await access.PrincipalAsync(user);
        else await access.RequireViewAsync(user);
        var item = await db.Lockboxes.AsNoTracking().NotCacheable()
            .SingleOrDefaultAsync(box => box.Id == id && (!ownRequestOnly || box.RequesterId == user))
            ?? throw new DmsException(404, "Lockbox not found.");
        access.Audit(user, "lockbox.get", id);
        await db.SaveChangesAsync();
        return item;
    });

    public Task<LockboxStatus> GetLockboxStatusAsync(string user, Guid id) => gate.RunAsync(async () =>
    {
        var item = await db.Lockboxes.AsNoTracking().SingleOrDefaultAsync(box => box.Id == id)
            ?? throw new DmsException(404, "Lockbox not found.");
        if (item.RequesterId == user) await access.PrincipalAsync(user);
        else await access.RequireViewAsync(user);

        var status = await DescribeLockboxAsync(item);
        access.Audit(user, "lockbox.get", id);
        await db.SaveChangesAsync();
        return status;
    });

    internal async Task<LockboxStatus> DescribeLockboxAsync(LockboxRequest item)
    {
        var people = await db.Users.AsNoTracking()
            .Where(person => person.Id == item.RequesterId || person.Id == item.ApproverId)
            .Select(person => new { person.Id, person.DisplayName, person.UserName })
            .ToDictionaryAsync(person => person.Id, person => AccessService.PersonName(person.DisplayName, person.UserName));
        var requester = people.GetValueOrDefault(item.RequesterId, "Unknown user");
        var decidedBy = item.Decision == DecisionState.Withdrawn ? requester :
            item.ApproverId == null ? null : people.GetValueOrDefault(item.ApproverId, "Unknown user");
        var approvers = await access.EligibleApproversAsync(item.Risk, item.RequesterId);
        return new LockboxStatus(item.Id, requester, DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Utc), item.Decision,
            item.DecidedAt is { } decidedAt ? DateTime.SpecifyKind(decidedAt, DateTimeKind.Utc) : null,
            decidedBy, item.Automatic, approvers, item.WorkflowId);
    }

    public async Task<OpenWorkflowResult> WaitForOpenResultAsync(string user, Guid id, CancellationToken token)
    {
        if (!DmsPermissions.ForRisk(await access.PrincipalAsync(user), RiskLevel.Open).CanRequest)
            throw new DmsException(403, "Run open workflows permission is required.");
        var box = await db.Lockboxes.AsNoTracking().NotCacheable()
            .SingleOrDefaultAsync(item => item.Id == id && item.RequesterId == user && item.Risk == RiskLevel.Open, token)
            ?? throw new DmsException(404, "Open Lockbox not found.");
        if (box.Decision != DecisionState.Approved || box.WorkflowId is not { } workflowId)
            throw new DmsException(409, "The Lockbox has no approved execution.");

        // The scheduler can finish while this request waits. Use fresh snapshots outside ControllerGate.
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var run = await db.WorkflowRuns.AsNoTracking().NotCacheable()
                .Where(item => item.Id == workflowId && item.LockboxId == id && item.ParentId == null)
                .Select(item => new { item.State, item.Result }).SingleOrDefaultAsync(token)
                ?? throw new DmsException(404, "Workflow not found.");
            if (run.State.Terminal() || run.State == RunState.NeedsVerification ||
                elapsed.Elapsed >= TimeSpan.FromSeconds(10))
            {
                var succeeded = run.State is RunState.Succeeded or RunState.Warning;
                access.Audit(user, "lockbox.result", id);
                await db.SaveChangesAsync(token);
                return new OpenWorkflowResult(workflowId, run.State, succeeded ? run.Result : null);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200), token);
        }
    }

    public Task<WorkflowDetails> GetWorkflowAsync(string user, Guid id) => gate.RunAsync(async () =>
    {
        var run = await db.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id)
            ?? throw new DmsException(404, "Workflow not found.");
        await access.RequireViewAsync(user);
        access.Audit(user, "operation.get", id);
        await db.SaveChangesAsync();
        return new WorkflowDetails(run.Id, run.RootId, run.ParentId, run.LockboxId,
            run.MachineId, run.Definition, run.State, run.CreatedAt, run.StartedAt, run.FinishedAt, InvestigationResult(run),
            run.Origin, run.ParentId == null ? run.Trigger : WorkflowTrigger.Subworkflow,
            run.ScheduleId, run.ScheduleExpression, run.ScheduledAt);
    });

    public Task<List<WorkflowRun>> CallstackAsync(string user, Guid id) => gate.RunAsync(async () =>
    {
        var run = await db.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id) ?? throw new DmsException(404, "Workflow not found.");
        await access.RequireViewAsync(user);
        var all = await db.WorkflowRuns.AsNoTracking().NotCacheable().Where(r => r.RootId == run.RootId).OrderBy(r => r.CreatedAt).ToListAsync();
        var selected = SubtreeIds(id, all.Select(r => (r.Id, r.ParentId)));
        access.Audit(user, "operation.callstack", id); await db.SaveChangesAsync();
        var tree = all.Where(r => selected.Contains(r.Id)).ToList();
        foreach (var item in tree) item.Result = InvestigationResult(item);
        return tree;
    });

    // The owner's Open Lockbox result endpoint is the only reader of this private payload.
    private static string InvestigationResult(WorkflowRun run) =>
        run.Definition == WorkflowNames.ListArtifacts ? string.Empty : run.Result;

    public async Task<IReadOnlyDictionary<string, string>> TraceScopeAsync(string user, Guid id, bool recursive)
    {
        var run = await db.WorkflowRuns.AsNoTracking().NotCacheable().Where(r => r.Id == id)
            .Select(r => new { r.RootId, r.Definition }).SingleOrDefaultAsync()
            ?? throw new DmsException(404, "Workflow not found.");
        await access.RequireViewAsync(user);

        var scope = new Dictionary<string, string> { [id.ToString()] = run.Definition };
        if (recursive)
        {
            var all = await db.WorkflowRuns.AsNoTracking().NotCacheable().Where(r => r.RootId == run.RootId)
                .Select(r => new { r.Id, r.ParentId, r.Definition }).ToListAsync();
            var selected = SubtreeIds(id, all.Select(r => (r.Id, r.ParentId)));
            foreach (var descendant in all.Where(r => selected.Contains(r.Id)))
                scope[descendant.Id.ToString()] = descendant.Definition;
        }

        access.Audit(user, "operation.trace", id);
        await db.SaveChangesAsync();
        return scope;
    }

    private static HashSet<Guid> SubtreeIds(Guid id, IEnumerable<(Guid Id, Guid? ParentId)> runs)
    {
        var childrenByParent = runs.ToLookup(r => r.ParentId, r => r.Id);
        var selected = new HashSet<Guid> { id };
        var pending = new Queue<Guid>();
        pending.Enqueue(id);
        while (pending.TryDequeue(out var parent))
        {
            foreach (var child in childrenByParent[parent])
            {
                if (selected.Add(child)) pending.Enqueue(child);
            }
        }
        return selected;
    }
}
