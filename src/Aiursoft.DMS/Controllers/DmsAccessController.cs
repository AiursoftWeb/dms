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
public class DmsAccessController(TemplateDbContext db, AccessService access, LockboxPages lockboxPages) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [RenderInNavBar(NavGroupName = "Settings", NavGroupOrder = 9998, CascadedLinksGroupName = "Personal", CascadedLinksIcon = "user", LinkText = "API Keys", LinkOrder = 2)]
    public async Task<IActionResult> Keys() => this.StackView(new DmsViewModel
    {
        Keys = await db.PersonalApiKeys.Where(k => k.UserId == Actor).ToListAsync(), NewKey = TempData["DmsNewKey"] as string
    });

    [RenderInNavBar(NavGroupName = LockboxText.Name, NavGroupOrder = 1, CascadedLinksGroupName = LockboxText.Name, CascadedLinksIcon = "lock", LinkText = "My requests", LinkOrder = 1)]
    public async Task<IActionResult> MyLockboxes(int page = 0, string? scope = null, string? q = null)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest();
            return this.StackView(await lockboxPages.ListAsync(Actor, ownRequests: true, scope, q, page, TempData["DmsNotice"] as string));
        }
        catch (DmsException exception) { return StatusCode(exception.StatusCode); }
    }

    [HttpGet("/DmsAccess/MyLockbox", Order = 0)]
    [HttpGet("/DmsAccess/MyLockbox/{id:guid}", Order = 1)]
    public async Task<IActionResult> MyLockbox(LockboxId id)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest();
            var model = await lockboxPages.DetailAsync(Actor, id, ownRequestView: true, TempData["DmsNotice"] as string);
            return this.StackView(model, "~/Views/Dms/Lockbox.cshtml");
        }
        catch (DmsException exception) { return StatusCode(exception.StatusCode); }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(LockboxId id, [FromServices] WorkflowService workflows, bool fromDetail = false)
    {
        if (!ModelState.IsValid) return BadRequest();
        try { await workflows.WithdrawAsync(Actor, id.Value); }
        catch (DmsException exception)
        {
            if (exception.StatusCode != 409) return StatusCode(exception.StatusCode);
            TempData["DmsNotice"] = exception.Message;
        }
        return fromDetail ? RedirectToAction(nameof(MyLockbox), new { id }) : RedirectToAction(nameof(MyLockboxes));
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
