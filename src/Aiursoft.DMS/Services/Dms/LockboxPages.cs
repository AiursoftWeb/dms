using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Models.DmsViewModels;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class LockboxPages(TemplateDbContext db, AccessService access, WorkflowService workflows, DmsArtifactAccess artifacts)
{
    public async Task<LockboxQueueViewModel> ListAsync(string actor, bool ownRequests, string? scope, string? query, int page, string? notice = null)
    {
        const int pageSize = 50;
        var principal = await access.PrincipalAsync(actor);
        var risks = Enum.GetValues<RiskLevel>().Where(risk => DmsPermissions.CanDecide(principal, risk, ownRequest: false)).ToArray();
        var canReview = !ownRequests && risks.Length != 0;
        var selfApproval = DmsPermissions.CanApproveOwn(principal);
        scope ??= ownRequests ? "all" : "review";
        page = Math.Clamp(page, 0, 100_000);
        var search = query?.Trim() ?? "";
        if (search.Length > 200) throw new DmsException(400, "Search is limited to 200 characters.");

        var requests = db.Lockboxes.AsNoTracking();
        if (ownRequests) requests = requests.Where(box => box.RequesterId == actor);
        requests = scope switch
        {
            "all" => requests,
            "review" when !ownRequests => canReview
                ? requests.Where(box => box.Decision == DecisionState.Pending &&
                    risks.Contains(box.Risk) && (selfApproval || box.RequesterId != actor))
                : requests.Where(_ => false),
            "pending" => requests.Where(box => box.Decision == DecisionState.Pending),
            "approved" => requests.Where(box => box.Decision == DecisionState.Approved),
            "denied" => requests.Where(box => box.Decision == DecisionState.Denied),
            "withdrawn" => requests.Where(box => box.Decision == DecisionState.Withdrawn),
            _ => throw new DmsException(400, "Invalid Lockbox filter.")
        };
        if (search.Length != 0)
        {
            if (LockboxId.TryParse(search, out var id)) requests = requests.Where(box => box.Id == id.Value);
            else
            {
                var term = search.ToUpperInvariant();
                var matchingPeople = db.Users.Where(user => user.DisplayName.ToUpper().Contains(term) ||
                    (user.UserName != null && user.UserName.ToUpper().Contains(term))).Select(user => user.Id);
                requests = requests.Where(box => box.Definition.ToUpper().Contains(term) || box.Reason.ToUpper().Contains(term) ||
                    matchingPeople.Contains(box.RequesterId));
            }
        }
        var rows = await requests.OrderByDescending(box => box.CreatedAt).ThenBy(box => box.Id)
            .Skip(page * pageSize).Take(pageSize + 1).NotCacheable().ToListAsync();
        var visible = rows.Take(pageSize).Select(box => (Item: box, Targets: LockboxSnapshot.ReadTargets(box))).ToList();
        var people = visible.Select(row => row.Item.RequesterId).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(user => people.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => AccessService.PersonName(user.DisplayName, user.UserName));
        var machines = await MachineNamesAsync(visible.SelectMany(row => row.Targets.Take(3)).Distinct().ToArray());
        var executions = await ExecutionStatesAsync(visible.Select(row => row.Item));

        return new LockboxQueueViewModel
        {
            PageTitle = ownRequests ? LockboxText.MyRequests : LockboxText.Name,
            Entries = visible.Select(row => new LockboxQueueEntry(row.Item,
                names.GetValueOrDefault(row.Item.RequesterId, "Unknown user"),
                row.Targets.Take(3).Select(id => machines.GetValueOrDefault(id, id.ToString())).ToList(),
                row.Targets.Length, canReview && row.Item.Decision == DecisionState.Pending &&
                    DmsPermissions.CanDecide(principal, row.Item.Risk, row.Item.RequesterId == actor),
                executions.GetValueOrDefault(row.Item.Id))).ToList(),
            OwnRequests = ownRequests, Scope = scope, Search = search,
            Page = page, HasNextPage = rows.Count > pageSize, Notice = notice
        };
    }

    public async Task<LockboxViewModel> DetailAsync(string actor, LockboxId id, bool ownRequestView, string? notice)
    {
        var item = ownRequestView
            ? await workflows.GetOwnedLockboxAsync(actor, id.Value)
            : await workflows.GetLockboxAsync(actor, id.Value);
        var principal = await access.PrincipalAsync(actor);
        var canViewOperations = principal.HasClaim(AppPermissions.Type, AppPermissionNames.CanReadDmsOperations);
        var ownRequest = item.RequesterId == actor;
        var snapshot = LockboxSnapshot.Read(item);
        return new LockboxViewModel
        {
            PageTitle = LockboxText.Name,
            Item = item,
            Status = await workflows.DescribeLockboxAsync(item),
            Snapshot = snapshot,
            MachineNames = await MachineNamesAsync(snapshot.Targets),
            Notice = notice,
            InputArtifacts = canViewOperations ? artifacts.Inputs(item) : [],
            CanWithdraw = item.Decision == DecisionState.Pending && ownRequest,
            CanDecide = canViewOperations && item.Decision == DecisionState.Pending && DmsPermissions.CanDecide(principal, item.Risk, ownRequest),
            OwnRequestView = ownRequestView,
            CanViewOperations = canViewOperations,
            ExecutionState = (await ExecutionStatesAsync([item])).GetValueOrDefault(item.Id)
        };
    }

    private Task<Dictionary<Guid, string>> MachineNamesAsync(IReadOnlyList<Guid> ids) => db.Machines.AsNoTracking()
        .Where(machine => ids.Contains(machine.Id)).ToDictionaryAsync(machine => machine.Id, machine => machine.Name);

    private async Task<Dictionary<Guid, RunState?>> ExecutionStatesAsync(IEnumerable<LockboxRequest> requests)
    {
        var approved = requests.Where(box => box.Decision == DecisionState.Approved && box.WorkflowId.HasValue)
            .ToDictionary(box => box.Id, box => box.WorkflowId!.Value);
        if (approved.Count == 0) return [];
        var ids = approved.Values.ToArray();
        var states = await db.WorkflowRuns.AsNoTracking().NotCacheable()
            .Where(run => ids.Contains(run.Id) && run.LockboxId != null && run.ParentId == null)
            .Select(run => new { run.Id, run.LockboxId, run.State }).ToListAsync();
        return states.Where(run => approved.GetValueOrDefault(run.LockboxId!.Value) == run.Id)
            .ToDictionary(run => run.LockboxId!.Value, run => (RunState?)run.State);
    }
}
