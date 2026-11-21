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
public class DmsAccessController(TemplateDbContext db, AccessService access) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [RenderInNavBar(NavGroupName = "Settings", NavGroupOrder = 9998, CascadedLinksGroupName = "Personal", CascadedLinksIcon = "user", LinkText = "API Keys", LinkOrder = 2)]
    public async Task<IActionResult> Keys() => this.StackView(new DmsViewModel
    {
        Keys = await db.PersonalApiKeys.Where(k => k.UserId == Actor).ToListAsync(), NewKey = TempData["DmsNewKey"] as string
    });

    [RenderInNavBar(NavGroupName = "DMS", NavGroupOrder = 0, CascadedLinksGroupName = "Operations", CascadedLinksIcon = "server", LinkText = "My Lockboxes", LinkOrder = 6)]
    public async Task<IActionResult> MyLockboxes(int page = 0)
    {
        page = Math.Clamp(page, 0, 10000);
        var items = await db.Lockboxes.AsNoTracking().Where(l => l.RequesterId == Actor)
            .OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id).Skip(page * 50).Take(51).ToListAsync();
        return this.StackView(new DmsViewModel
        {
            PageTitle = "My Lockboxes", Lockboxes = items.Take(50).ToList(), Page = page,
            HasNextPage = items.Count > 50, Notice = TempData["DmsNotice"] as string
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(LockboxId id, [FromServices] WorkflowService workflows)
    {
        if (!ModelState.IsValid) return BadRequest();
        try { await workflows.WithdrawAsync(Actor, id.Value); }
        catch (DmsException exception)
        {
            if (exception.StatusCode != 409) return StatusCode(exception.StatusCode);
            TempData["DmsNotice"] = exception.Message;
        }
        return RedirectToAction(nameof(MyLockboxes));
    }

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
