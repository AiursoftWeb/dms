using System.Security.Claims;
using Aiursoft.DMS.Models.DmsViewModels;
using Aiursoft.DMS.Services;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.DMS.Controllers;

[Authorize]
[AutoValidateAntiforgeryToken]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class SshKeysController(PersonalSshKeys keys) : Controller
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet]
    [RenderInNavBar(NavGroupName = "Settings", NavGroupOrder = 9998, CascadedLinksGroupName = "Personal", CascadedLinksIcon = "user", LinkText = "SSH public keys", LinkOrder = 3)]
    public async Task<IActionResult> Index() => this.StackView(new SshKeysViewModel
    {
        Keys = await keys.ListAsync(Actor),
        PendingId = (await keys.GetPendingAsync(Actor))?.Id,
        Notice = TempData["SshKeyNotice"] as string
    });

    [HttpGet]
    public async Task<IActionResult> Add()
    {
        var pending = await keys.GetPendingAsync(Actor);
        if (pending != null) return RedirectToAction(nameof(Verify), new { id = pending.Id });
        return this.StackView(new AddSshKeyViewModel
        {
            VerificationUnavailable = TempData["SshKeyVerificationUnavailable"] is true
        });
    }

    [HttpPost]
    public async Task<IActionResult> Register(string name, string publicKey)
    {
        try
        {
            var pending = await keys.BeginAsync(Actor, name, publicKey);
            return RedirectToAction(nameof(Verify), new { id = pending.Id });
        }
        catch (DmsException error)
        {
            var pending = await keys.GetPendingAsync(Actor);
            if (pending != null) return RedirectToAction(nameof(Verify), new { id = pending.Id });
            return this.StackView(new AddSshKeyViewModel
            {
                KeyName = name,
                PublicKey = publicKey,
                Notice = error.Message
            }, nameof(Add));
        }
    }

    [HttpGet]
    public Task<IActionResult> Verify(Guid id) => ShowVerificationAsync(id);

    [HttpPost]
    public async Task<IActionResult> Verify(Guid id, string signature, CancellationToken token)
    {
        try
        {
            await keys.VerifyAsync(Actor, id, signature, token);
            return RedirectToAction(nameof(Success), new { id });
        }
        catch (DmsException)
        {
            return await ShowVerificationAsync(id, signatureInvalid: true);
        }
    }

    [HttpPost]
    public async Task<IActionResult> Abandon(Guid id)
    {
        await keys.AbandonAsync(Actor, id);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Success(Guid id)
    {
        var added = (await keys.ListAsync(Actor)).SingleOrDefault(key => key.Id == id);
        if (added == null) return RedirectToAction(nameof(Index));
        return this.StackView(new SshKeyVerificationViewModel { Key = added, PageTitle = "SSH public key added" });
    }

    [HttpPost]
    public async Task<IActionResult> Revoke(Guid id)
    {
        try { await keys.RevokeAsync(Actor, id); }
        catch (DmsException ex) { TempData["SshKeyNotice"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> ShowVerificationAsync(Guid id, bool signatureInvalid = false)
    {
        var pending = await keys.GetPendingAsync(Actor, id);
        if (pending != null)
            return this.StackView(new SshKeyVerificationViewModel { Key = pending, SignatureInvalid = signatureInvalid }, nameof(Verify));
        if ((await keys.ListAsync(Actor)).Any(key => key.Id == id))
            return RedirectToAction(nameof(Success), new { id });
        TempData["SshKeyVerificationUnavailable"] = true;
        return RedirectToAction(nameof(Add));
    }
}
