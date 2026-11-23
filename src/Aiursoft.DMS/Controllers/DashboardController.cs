using Aiursoft.DMS.Models.DashboardViewModels;
using Aiursoft.DMS.Services;
using Aiursoft.UiStack.Layout;
using Aiursoft.UiStack.Navigation;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.DMS.Controllers;

[Authorize]
[LimitPerMin]
public class DashboardController : Controller
{
    [RenderInNavBar(
        NavGroupName = "Features",
        NavGroupOrder = 2,
        CascadedLinksGroupName = "Home",
        CascadedLinksIcon = "home",
        CascadedLinksOrder = 1,
        LinkText = "Index",
        LinkOrder = 1)]
    public IActionResult Index()
    {
        return this.StackView(new IndexViewModel());
    }

    [HttpGet]
    public IActionResult CliSetup() => this.StackView(new UiStackLayoutViewModel
    {
        PageTitle = "Set up DMS CLI"
    });
}
