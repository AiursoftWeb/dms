using Aiursoft.DMS.Configuration;
using Aiursoft.DMS.Services;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.DMS.Controllers;

[Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsAgent")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentScheduledUpgradeController(GlobalSettingsService settings, AgentReleaseStore releases) : ControllerBase
{
    [HttpGet("api/agent/scheduled-upgrade")]
    public async Task<IActionResult> GetRelease()
    {
        var machine = Guid.Parse(User.FindFirst("DmsMachine")!.Value);
        var enabledMachines = ScheduledAgentUpgradeSettings.Parse(
            await settings.GetSettingValueAsync(SettingsMap.ScheduledAgentUpgrade));
        return enabledMachines.Contains(machine) ? Ok(releases.Read()) : NoContent();
    }
}
