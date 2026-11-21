using System.Security.Claims;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Models.DmsViewModels;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Controllers;

[Authorize(Policy = AppPermissionNames.CanReadDmsOperations)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class DmsController(TemplateDbContext db, AccessService access, WorkflowService workflows, TraceDatabase traces, WorkflowInvestigation investigation,
    DmsArtifactAccess artifacts, DmsArtifactStore artifactStore) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        try { await access.RequireViewAsync(Actor); }
        catch (DmsException exception) { context.Result = StatusCode(exception.StatusCode); return; }
        await next();
    }

    [RenderInNavBar(NavGroupName = "DMS", NavGroupOrder = 0, CascadedLinksGroupName = "Operations", CascadedLinksIcon = "server", LinkText = "Machines", LinkOrder = 1)]
    public async Task<IActionResult> Index()
    {
        return this.StackView(new DmsViewModel
        {
            Machines = await db.Machines.OrderBy(m => m.Name).Take(1000).ToListAsync()
        });
    }

    [HttpGet]
    public async Task<IActionResult> Machine(Guid id, [FromServices] IAuthorizationService authorization)
    {
        var machine = await db.Machines.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id);
        if (machine == null) return NotFound();
        return this.StackView(new DmsMachineViewModel
        {
            Machine = machine,
            PageTitle = machine.Name,
            CanRotateAgentKey = !machine.Disabled && (await authorization.AuthorizeAsync(User, AppPermissionNames.CanManageDmsAccess)).Succeeded
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

    [RenderInNavBar(NavGroupName = "DMS", NavGroupOrder = 0, CascadedLinksGroupName = "Operations", CascadedLinksIcon = "server", LinkText = "Lockboxes", LinkOrder = 2)]
    public async Task<IActionResult> Lockboxes(int page = 0)
    {
        page = Math.Clamp(page, 0, 100000);
        var items = await db.Lockboxes.OrderByDescending(l => l.CreatedAt).Skip(page * 50).Take(51).ToListAsync();
        return this.StackView(new DmsViewModel { Lockboxes = items.Take(50).ToList(), Page = page, HasNextPage = items.Count > 50 });
    }

    [HttpGet("/Dms/Lockbox", Order = 0)]
    [HttpGet("/Dms/Lockbox/{id:guid}", Order = 1)]
    public async Task<IActionResult> Lockbox(LockboxId id)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest();
            var item = await workflows.GetLockboxAsync(Actor, id.Value);
            return this.StackView(new DmsViewModel
            {
                Lockbox = item, Notice = TempData["DmsNotice"] as string, InputArtifacts = artifacts.Inputs(item),
                CanDecide = DmsPermissions.CanDecide(await access.PrincipalAsync(Actor), item.Risk, item.RequesterId == Actor)
            });
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
    public async Task<IActionResult> Operations(int page = 0, Guid? machineId = null, string? definition = null, bool clusterWorkflowsOnly = false, WorkflowOrigin? origin = null)
    {
        page = Math.Clamp(page, 0, 100000);
        if (origin != null && !Enum.IsDefined(origin.Value)) return BadRequest();
        var query = db.WorkflowRuns.AsQueryable();
        if (origin != null) query = query.Where(r => r.Origin == origin);
        if (clusterWorkflowsOnly) query = query.Where(r => r.MachineId == null);
        if (machineId != null) query = query.Where(r => r.MachineId == machineId);
        if (!string.IsNullOrWhiteSpace(definition)) query = query.Where(r => r.Definition == definition);
        var items = await query.OrderByDescending(r => r.CreatedAt).Skip(page * 50).Take(51).ToListAsync();
        return this.StackView(new DmsViewModel
        {
            Origin = origin, Runs = items.Take(50).ToList(), Page = page, HasNextPage = items.Count > 50,
            MachineId = machineId, Definition = definition, ClusterWorkflowsOnly = clusterWorkflowsOnly
        });
    }

    public async Task<IActionResult> Operation(Guid id)
    {
        try
        {
            var runs = await workflows.CallstackAsync(Actor, id);
            var model = new DmsViewModel { Runs = runs, Run = runs.Single(r => r.Id == id) };
            if (traces.Enabled)
            {
                try { model.Trace = await traces.ReadAsync(id, 0); }
                catch (Exception) { model.Notice = "The trace archive is temporarily unavailable. Refresh to try again."; }
            }
            else model.Notice = "ClickHouse is not configured. Trace remains in the durable backlog.";
            return this.StackView(model);
        }
        catch (DmsException exception) { return StatusCode(exception.StatusCode); }
    }
}
