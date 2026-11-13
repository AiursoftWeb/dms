using System.Security.Claims;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Models.DmsViewModels;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Controllers;

[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class DmsAccessController(TemplateDbContext db, AccessService access, ControllerGate gate) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [Authorize(Policy = "CanManageDmsAccess")]
    [RenderInNavBar(NavGroupName = "Settings", NavGroupOrder = 9998, CascadedLinksGroupName = "DMS", CascadedLinksIcon = "shield", LinkText = "Cluster access", LinkOrder = 1)]
    public async Task<IActionResult> Index() => this.StackView(new DmsViewModel
    {
        Clusters = await db.Clusters.ToListAsync(), Grants = await db.ClusterGrants.ToListAsync(),
        Users = await db.Users.OrderBy(u => u.UserName).ToListAsync(), Machines = await db.Machines.ToListAsync(),
        NewKey = TempData["DmsNewKey"] as string, Notice = TempData["DmsNotice"] as string
    });

    [HttpPost, ValidateAntiForgeryToken, Authorize(Policy = "CanManageDmsAccess")]
    public async Task<IActionResult> CreateCluster(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return BadRequest();
        await gate.RunAsync(async () =>
        {
            var cluster = new Cluster { Name = name.Trim() };
            db.Clusters.Add(cluster); access.Audit(Actor, "access.cluster.create", cluster.Id);
            await db.SaveChangesAsync(); return true;
        });
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Policy = "CanManageDmsAccess")]
    public async Task<IActionResult> Grant(Guid clusterId, string userId, RiskLevel risk, bool canRequest, bool autoApprove, bool canApprove)
    {
        if (!Enum.IsDefined(risk) || !await db.Clusters.AnyAsync(c => c.Id == clusterId) || !await db.Users.AnyAsync(u => u.Id == userId)) return BadRequest();
        await gate.RunAsync(async () =>
        {
            var grant = await db.ClusterGrants.SingleOrDefaultAsync(g => g.ClusterId == clusterId && g.UserId == userId && g.Risk == risk);
            if (grant == null) { grant = new ClusterGrant { ClusterId = clusterId, UserId = userId, Risk = risk }; db.ClusterGrants.Add(grant); }
            grant.CanRequest = canRequest; grant.AutoApprove = autoApprove; grant.CanApprove = canApprove;
            access.Audit(Actor, "access.grant", clusterId, $"User={userId}; Risk={risk}; Request={canRequest}; Auto={autoApprove}; Approve={canApprove}");
            await db.SaveChangesAsync(); return true;
        });
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Policy = "CanManageDmsAccess")]
    public async Task<IActionResult> SelfApproval(string userId, bool enabled)
    {
        await gate.RunAsync(async () =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.CanApproveOwnRequests = enabled;
            access.Audit(Actor, "access.self-approval", detail: $"User={userId}; Enabled={enabled}");
            await db.SaveChangesAsync(); return true;
        });
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Policy = "CanManageDmsAccess")]
    public async Task<IActionResult> AgentKey(Guid machineId)
    {
        var key = DmsKeyAuthenticationHandler.CreateKey(true);
        await gate.RunAsync(async () =>
        {
            var machine = await db.Machines.SingleAsync(m => m.Id == machineId);
            machine.KeyHash = DmsKeyAuthenticationHandler.Hash(key);
            access.Audit(Actor, "key.agent.rotate", machineId); await db.SaveChangesAsync(); return true;
        });
        TempData["DmsNewKey"] = key;
        return RedirectToAction(nameof(Index));
    }

    [RenderInNavBar(NavGroupName = "Settings", NavGroupOrder = 9998, CascadedLinksGroupName = "Personal", CascadedLinksIcon = "user", LinkText = "API Keys", LinkOrder = 2)]
    public async Task<IActionResult> Keys() => this.StackView(new DmsViewModel
    {
        Keys = await db.PersonalApiKeys.Where(k => k.UserId == Actor).ToListAsync(), NewKey = TempData["DmsNewKey"] as string
    });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateKey(string name, int days = 30)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || days is < 1 or > 366) return BadRequest();
        var key = DmsKeyAuthenticationHandler.CreateKey(false);
        db.PersonalApiKeys.Add(new PersonalApiKey { UserId = Actor, Name = name.Trim(), KeyHash = DmsKeyAuthenticationHandler.Hash(key), ExpiresAt = DateTime.UtcNow.AddDays(days) });
        access.Audit(Actor, "key.personal.create"); await db.SaveChangesAsync();
        TempData["DmsNewKey"] = key; return RedirectToAction(nameof(Keys));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeKey(Guid id)
    {
        var key = await db.PersonalApiKeys.SingleOrDefaultAsync(k => k.Id == id && k.UserId == Actor);
        if (key == null) return NotFound();
        db.PersonalApiKeys.Remove(key); access.Audit(Actor, "key.personal.revoke", id); await db.SaveChangesAsync();
        return RedirectToAction(nameof(Keys));
    }
}
