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
public class DmsAccessController(TemplateDbContext db, AccessService access, DmsManagementService management) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [Authorize(Policy = "CanManageDmsAccess")]
    [RenderInNavBar(NavGroupName = "Settings", NavGroupOrder = 9998, CascadedLinksGroupName = "DMS", CascadedLinksIcon = "shield", LinkText = "DMS access", LinkOrder = 1)]
    public async Task<IActionResult> Index() => this.StackView(new DmsViewModel
    {
        Clusters = await db.Clusters.ToListAsync(),
        Machines = await db.Machines.ToListAsync(),
        NewKey = TempData["DmsNewKey"] as string, Notice = TempData["DmsNotice"] as string
    });

    private async Task<IActionResult> ManageAsync(Func<Task<object>> action)
    {
        try { await action(); return RedirectToAction(nameof(Index)); }
        catch (DmsException exception) { return StatusCode(exception.StatusCode, exception.Message); }
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> CreateCluster(string name) => ManageAsync(async () =>
        await management.CreateAsync(Actor, new CreateClusterInput(Guid.NewGuid(), name)));

    [HttpPost, ValidateAntiForgeryToken, Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> AgentKey(Guid machineId) => ManageAsync(async () =>
    {
        var result = await management.AgentKeyAsync(Actor, machineId);
        TempData["DmsNewKey"] = result.Key; return result;
    });

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
