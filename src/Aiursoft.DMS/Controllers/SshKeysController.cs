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
        Keys = await keys.ListAsync(Actor), Notice = TempData["SshKeyNotice"] as string
    });

    [HttpPost]
    public Task<IActionResult> Register(string name, string publicKey) =>
        ChangeAsync(() => keys.BeginAsync(Actor, name, publicKey));

    [HttpPost]
    public Task<IActionResult> Verify(Guid id, string signature, CancellationToken token) =>
        ChangeAsync(() => keys.VerifyAsync(Actor, id, signature, token));

    [HttpPost]
    public Task<IActionResult> Revoke(Guid id) => ChangeAsync(() => keys.RevokeAsync(Actor, id));

    private async Task<IActionResult> ChangeAsync(Func<Task> change)
    {
        try { await change(); }
        catch (DmsException ex) { TempData["SshKeyNotice"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }
}
