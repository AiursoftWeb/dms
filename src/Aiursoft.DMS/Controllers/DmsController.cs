using System.Security.Claims;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Models.DmsViewModels;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.Services;
using Aiursoft.UiStack.Navigation;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Controllers;

[Authorize(Policy = AppPermissionNames.CanReadDmsOperations)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class DmsController(TemplateDbContext db, AccessService access, WorkflowService workflows, TraceDatabase traces, WorkflowInvestigation investigation,
    DmsArtifactAccess artifacts, DmsArtifactStore artifactStore, LockboxPages lockboxPages) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        try { await access.RequireViewAsync(Actor); }
        catch (DmsException exception) { context.Result = StatusCode(exception.StatusCode); return; }
        await next();
    }

    [RenderInNavBar(NavGroupName = "DMS", NavGroupOrder = 0, CascadedLinksGroupName = "Operations", CascadedLinksIcon = "server", LinkText = "Machines", LinkOrder = 1)]
    public async Task<IActionResult> Index(string? q = null, MachineConnection? connection = null, int page = 0)
    {
        if (!ModelState.IsValid || (connection.HasValue && !Enum.IsDefined(connection.Value)) || q?.Length > 100)
            return BadRequest();
        const int pageSize = 50;
        page = Math.Clamp(page, 0, 100000);
        var search = q?.Trim() ?? string.Empty;
        var machines = MachineRows();
        if (connection != null) machines = machines.Where(machine => machine.Connection == connection);

        var ordered = machines.OrderBy(machine => machine.Name).ThenBy(machine => machine.Id).NotCacheable();
        var skip = page * pageSize;
        var rows = search.Length == 0
            ? await ordered.Skip(skip).Take(pageSize + 1).ToListAsync(HttpContext.RequestAborted)
            : await ordered.AsAsyncEnumerable()
                .Where(machine => machine.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                .Skip(skip).Take(pageSize + 1).ToListAsync(HttpContext.RequestAborted);
        return this.StackView(new DmsMachinesViewModel
        {
            PageTitle = "Machines", Machines = rows.Take(pageSize).ToList(), Search = search,
            Connection = connection, Page = page, HasNextPage = rows.Count > pageSize
        });
    }

    [HttpGet]
    public async Task<IActionResult> Machine(Guid id)
    {
        var machine = await MachineRows().NotCacheable().SingleOrDefaultAsync(m => m.Id == id);
        if (machine == null) return NotFound();
        var baseline = await db.WorkflowRuns.AsNoTracking()
            .Where(run => run.MachineId == id && run.Definition == WorkflowNames.ApplySecurityBaseline &&
                run.ParentId == null && run.Origin == WorkflowOrigin.Automatic)
            .OrderByDescending(run => run.CreatedAt).ThenByDescending(run => run.Id)
            .NotCacheable().FirstOrDefaultAsync();
        return this.StackView(new DmsMachineViewModel
        {
            Machine = machine, BaselineRun = baseline,
            PageTitle = machine.Name
        });
    }

    private IQueryable<DmsMachineRow> MachineRows()
    {
        var onlineAfter = DateTime.UtcNow.AddSeconds(-45);
        return db.Machines.AsNoTracking().Select(machine => new DmsMachineRow
        {
            Id = machine.Id, Name = machine.Name, AgentVersion = machine.AgentVersion, LastSeen = machine.LastSeen,
            Connection = machine.Disabled ? MachineConnection.Disabled
                : machine.LastSeen == null ? MachineConnection.NeverConnected
                : machine.LastSeen > onlineAfter ? MachineConnection.Online : MachineConnection.Offline
        });
    }

    [RenderInNavBar(NavGroupName = "DMS", NavGroupOrder = 0, CascadedLinksGroupName = "Operations", CascadedLinksIcon = "server", LinkText = "Investigation", LinkOrder = 5)]
    public async Task<IActionResult> Investigation([FromQuery] WorkflowAuditQuery query)
    {
        var model = new DmsViewModel { PageTitle = "Investigation", AuditQuery = query };
        try
        {
            if (!ModelState.IsValid) throw new DmsException(400, "Invalid investigation filters.");
            model.AuditPage = await investigation.ReadAsync(Actor, query);
            model.AuditQuery = model.AuditPage.Query;
        }
        catch (DmsException exception)
        {
            if (exception.StatusCode == 403) return StatusCode(403);
            Response.StatusCode = model.AuditError = exception.StatusCode;
        }
        return this.StackView(model);
    }

    [RenderInNavBar(NavGroupName = "DMS", NavGroupOrder = 0, CascadedLinksGroupName = "Operations", CascadedLinksIcon = "server", LinkText = "Standard workflows", LinkOrder = 4)]
    public async Task<IActionResult> Definitions()
    {
        try
        {
            return this.StackView(new DmsViewModel
            {
                PageTitle = "Standard workflows",
                Catalog = await workflows.ListDefinitionsAsync(Actor)
            });
        }
        catch (DmsException exception) { return StatusCode(exception.StatusCode); }
    }

    [RenderInNavBar(NavGroupName = LockboxText.Name, NavGroupOrder = 1, CascadedLinksGroupName = LockboxText.Name, CascadedLinksIcon = "lock", LinkText = "My approvals", LinkOrder = 2)]
    public async Task<IActionResult> Lockboxes(int page = 0, string? scope = null, string? q = null)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest();
            return this.StackView(await lockboxPages.ListAsync(Actor, ownRequests: false, scope, q, page));
        }
        catch (DmsException exception) { return StatusCode(exception.StatusCode); }
    }

    [HttpGet("/Dms/Lockbox", Order = 0)]
    [HttpGet("/Dms/Lockbox/{id:guid}", Order = 1)]
    public async Task<IActionResult> Lockbox(LockboxId id)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest();
            return this.StackView(await lockboxPages.DetailAsync(Actor, id, ownRequestView: false, TempData["DmsNotice"] as string));
        }
        catch (DmsException exception) { return StatusCode(exception.StatusCode); }
    }

    public async Task<IActionResult> Artifact(Guid id)
    {
        try
        {
            var artifact = await artifacts.RequireHumanAsync(Actor, id);
            var model = new DmsViewModel { PageTitle = "Artifact", Artifact = artifact };
            if (artifact.Reference.Kind == "file" && artifact.Reference.Length <= 262144)
            {
                using var stream = artifactStore.Open(id);
                using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true));
                try { var text = await reader.ReadToEndAsync(); if (!text.Contains('\0')) model.ArtifactText = text; }
                catch (System.Text.DecoderFallbackException) { }
            }
            access.Audit(Actor, "artifact.preview", id, System.Text.Json.JsonSerializer.Serialize(artifact.Reference)); await db.SaveChangesAsync();
            return this.StackView(model);
        }
        catch (DmsException exception) { return StatusCode(exception.StatusCode); }
    }

    public async Task<IActionResult> DownloadArtifact(Guid id)
    {
        try
        {
            var artifact = await artifacts.RequireHumanAsync(Actor, id);
            access.Audit(Actor, "artifact.download", id, System.Text.Json.JsonSerializer.Serialize(artifact.Reference)); await db.SaveChangesAsync();
            return File(artifactStore.Open(id), "application/octet-stream", artifact.Reference.Name, enableRangeProcessing: true);
        }
        catch (DmsException exception) { return StatusCode(exception.StatusCode); }
    }

    [HttpPost("/Dms/Decide"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(LockboxId id, bool approve, string reason = "")
    {
        if (!ModelState.IsValid) return BadRequest();
        try { await workflows.DecideAsync(Actor, id.Value, approve, reason); }
        catch (DmsException exception) { TempData["DmsNotice"] = exception.Message; }
        return RedirectToAction(nameof(Lockbox), new { id });
    }

    [RenderInNavBar(NavGroupName = "DMS", NavGroupOrder = 0, CascadedLinksGroupName = "Operations", CascadedLinksIcon = "server", LinkText = "Workflow history", LinkOrder = 3)]
    public async Task<IActionResult> Operations(int page = 0, Guid? machineId = null, string? definition = null,
        bool clusterWorkflowsOnly = false, WorkflowOrigin? origin = null, RunState? state = null, bool rootOnly = false)
    {
        if (!ModelState.IsValid || (origin.HasValue && !Enum.IsDefined(origin.Value)) ||
            (state.HasValue && !Enum.IsDefined(state.Value)) || definition?.Length > 200) return BadRequest();
        page = Math.Clamp(page, 0, 100000);
        var query = db.WorkflowRuns.AsNoTracking();
        if (origin != null) query = query.Where(run => run.Origin == origin);
        if (state != null) query = query.Where(run => run.State == state);
        if (rootOnly) query = query.Where(run => run.ParentId == null);
        if (clusterWorkflowsOnly) query = query.Where(run => run.MachineId == null);
        if (machineId != null) query = query.Where(run => run.MachineId == machineId);
        if (!string.IsNullOrWhiteSpace(definition))
        {
            var matching = db.Lockboxes.Where(box => box.Definition == definition).Select(box => box.Id);
            query = query.Where(run => run.Definition == definition ||
                (run.Definition == WorkflowNames.Batch && run.LockboxId.HasValue && matching.Contains(run.LockboxId.Value)));
        }
        var items = await query.OrderByDescending(run => run.CreatedAt).ThenBy(run => run.Id)
            .Skip(page * 50).Take(51).NotCacheable().ToListAsync();
        var runs = items.Take(50).ToList();
        var boxIds = runs.Where(run => run.Definition == WorkflowNames.Batch).Select(run => run.LockboxId).OfType<Guid>().Distinct().ToArray();
        var requested = await db.Lockboxes.AsNoTracking().Where(box => boxIds.Contains(box.Id))
            .ToDictionaryAsync(box => box.Id, box => box.Definition);
        return this.StackView(new DmsViewModel
        {
            PageTitle = "Workflow history",
            Origin = origin, State = state, RootOnly = rootOnly, Runs = runs, Page = page, HasNextPage = items.Count > 50,
            MachineId = machineId, Definition = definition, ClusterWorkflowsOnly = clusterWorkflowsOnly,
            MachineNames = await MachineNamesAsync(runs.Select(run => run.MachineId).Append(machineId)),
            RequestedDefinitions = requested
        });
    }

    public async Task<IActionResult> Operation(Guid id, int tracePage = 0)
    {
        if (!ModelState.IsValid) return BadRequest();
        tracePage = Math.Clamp(tracePage, 0, 100000);
        try
        {
            var runs = await workflows.CallstackAsync(Actor, id);
            var run = runs.Single(item => item.Id == id);
            var box = run.Definition == WorkflowNames.Batch && run.LockboxId is { } lockboxId
                ? await db.Lockboxes.AsNoTracking().SingleOrDefaultAsync(item => item.Id == lockboxId)
                : null;
            var targets = box == null ? [] : LockboxSnapshot.ReadTargets(box);
            var model = new OperationViewModel
            {
                PageTitle = box?.Definition ?? run.Definition, Run = run, Steps = runs, TracePage = tracePage,
                RequestedDefinition = box?.Definition, Targets = targets,
                MachineNames = await MachineNamesAsync(runs.Select(item => item.MachineId).Concat(targets.Select(target => (Guid?)target)))
            };
            if (traces.Enabled)
            {
                try
                {
                    var entries = await traces.ReadAsync([id.ToString()], tracePage * TraceDatabase.PageSize, TraceDatabase.PageSize + 1);
                    model.HasNextTracePage = entries.Count > TraceDatabase.PageSize;
                    model.Trace = entries.Take(TraceDatabase.PageSize).ToList();
                }
                catch (Exception) { model.TraceUnavailable = true; }
            }
            else model.TraceUnavailable = true;
            return this.StackView(model);
        }
        catch (DmsException exception) { return StatusCode(exception.StatusCode); }
    }

    private Task<Dictionary<Guid, string>> MachineNamesAsync(IEnumerable<Guid?> machineIds)
    {
        var ids = machineIds.OfType<Guid>().Distinct().ToArray();
        return db.Machines.AsNoTracking().Where(machine => ids.Contains(machine.Id))
            .ToDictionaryAsync(machine => machine.Id, machine => machine.Name);
    }
}
