using System.Security.Claims;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Models.DmsViewModels;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class DmsController(TemplateDbContext db, AccessService access, WorkflowService workflows, TraceDatabase traces, WorkflowInvestigation investigation) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [RenderInNavBar(NavGroupName = "DMS", NavGroupOrder = 0, CascadedLinksGroupName = "Operations", CascadedLinksIcon = "server", LinkText = "Machines", LinkOrder = 1)]
    public async Task<IActionResult> Index()
    {
        var clusters = await access.VisibleClustersAsync(Actor);
        return this.StackView(new DmsViewModel
        {
            Clusters = await db.Clusters.Where(c => clusters.Contains(c.Id)).ToListAsync(),
            Machines = await db.Machines.Where(m => clusters.Contains(m.ClusterId)).OrderBy(m => m.Name).Take(1000).ToListAsync()
        });
    }

    [RenderInNavBar(NavGroupName = "DMS", NavGroupOrder = 0, CascadedLinksGroupName = "Operations", CascadedLinksIcon = "server", LinkText = "Investigation", LinkOrder = 5)]
    public async Task<IActionResult> Investigation([FromQuery] WorkflowAuditQuery query)
    {
        var visible = await access.VisibleClustersAsync(Actor);
        if (query.ClusterId != Guid.Empty && !visible.Contains(query.ClusterId)) return StatusCode(403);
        var clusters = await db.Clusters.Where(c => visible.Contains(c.Id)).OrderBy(c => c.Name).ThenBy(c => c.Id).ToListAsync();
        if (query.ClusterId == Guid.Empty && clusters.Count > 0) query = query with { ClusterId = clusters[0].Id };
        var model = new DmsViewModel { PageTitle = "Investigation", Clusters = clusters, AuditQuery = query };
        if (clusters.Count == 0) return this.StackView(model);
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
    public async Task<IActionResult> Definitions(Guid? clusterId = null)
    {
        var visible = await access.VisibleClustersAsync(Actor);
        if (clusterId != null && !visible.Contains(clusterId.Value)) return StatusCode(403);
        var clusters = await db.Clusters.Where(c => visible.Contains(c.Id)).OrderBy(c => c.Name).ThenBy(c => c.Id).ToListAsync();
        clusterId ??= clusters.FirstOrDefault()?.Id;
        try
        {
            return this.StackView(new DmsViewModel
            {
                PageTitle = "Standard workflows", Clusters = clusters, ClusterId = clusterId,
                Catalog = clusterId == null ? null : await workflows.ListDefinitionsAsync(Actor, clusterId.Value)
            });
        }
        catch (DmsException exception) { return StatusCode(exception.StatusCode); }
    }

    [RenderInNavBar(NavGroupName = "DMS", NavGroupOrder = 0, CascadedLinksGroupName = "Operations", CascadedLinksIcon = "server", LinkText = "Lockboxes", LinkOrder = 2)]
    public async Task<IActionResult> Lockboxes(int page = 0)
    {
        page = Math.Clamp(page, 0, 100000);
        var clusters = await access.VisibleClustersAsync(Actor);
        var items = await db.Lockboxes.Where(l => clusters.Contains(l.ClusterId)).OrderByDescending(l => l.CreatedAt).Skip(page * 50).Take(51).ToListAsync();
        return this.StackView(new DmsViewModel { Lockboxes = items.Take(50).ToList(), Page = page, HasNextPage = items.Count > 50 });
    }

    public async Task<IActionResult> Lockbox(Guid id)
    {
        try { return this.StackView(new DmsViewModel { Lockbox = await workflows.GetLockboxAsync(Actor, id), Notice = TempData["DmsNotice"] as string }); }
        catch (DmsException exception) { return StatusCode(exception.StatusCode); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(Guid id, bool approve, string reason = "")
    {
        try { await workflows.DecideAsync(Actor, id, approve, reason); }
        catch (DmsException exception) { TempData["DmsNotice"] = exception.Message; }
        return RedirectToAction(nameof(Lockbox), new { id });
    }

    [RenderInNavBar(NavGroupName = "DMS", NavGroupOrder = 0, CascadedLinksGroupName = "Operations", CascadedLinksIcon = "server", LinkText = "Workflow history", LinkOrder = 3)]
    public async Task<IActionResult> Operations(int page = 0, Guid? machineId = null, string? definition = null, Guid? clusterId = null, bool clusterWorkflowsOnly = false)
    {
        page = Math.Clamp(page, 0, 100000);
        var clusters = await access.VisibleClustersAsync(Actor);
        if (clusterId != null && !clusters.Contains(clusterId.Value)) return StatusCode(403);
        var query = db.WorkflowRuns.Where(r => clusters.Contains(r.ClusterId));
        if (clusterId != null) query = query.Where(r => r.ClusterId == clusterId);
        if (clusterWorkflowsOnly) query = query.Where(r => r.MachineId == null);
        if (machineId != null) query = query.Where(r => r.MachineId == machineId);
        if (!string.IsNullOrWhiteSpace(definition)) query = query.Where(r => r.Definition == definition);
        var items = await query.OrderByDescending(r => r.CreatedAt).Skip(page * 50).Take(51).ToListAsync();
        return this.StackView(new DmsViewModel
        {
            Runs = items.Take(50).ToList(), Page = page, HasNextPage = items.Count > 50,
            MachineId = machineId, Definition = definition, ClusterId = clusterId, ClusterWorkflowsOnly = clusterWorkflowsOnly,
            Clusters = await db.Clusters.Where(c => clusters.Contains(c.Id)).OrderBy(c => c.Name).ToListAsync()
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
