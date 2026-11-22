using Aiursoft.DMS.Configuration;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Services;
using Aiursoft.DMS.Services.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.DMS.Controllers;

[Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsAgent")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentSecurityBaselineController(GlobalSettingsService settings) : ControllerBase
{
    [HttpGet("api/agent/security-baseline")]
    public async Task<IActionResult> GetPolicy()
    {
        var machine = Guid.Parse(User.FindFirst("DmsMachine")!.Value);
        var configuration = ScheduledSecurityBaselineSettings.Parse(
            await settings.GetSettingValueAsync(SettingsMap.ScheduledSecurityBaseline));
        return configuration != null && configuration.MachineIds.Contains(machine)
            ? Ok(configuration.Policy)
            : NoContent();
    }
}
