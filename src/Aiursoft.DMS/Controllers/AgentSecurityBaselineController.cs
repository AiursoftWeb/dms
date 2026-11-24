using System.Text.Json;
using Aiursoft.DMS.Configuration;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Services;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.DMS.Controllers;

[Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsAgent")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentSecurityBaselineController(GlobalSettingsService settings, AgentSessions sessions,
    ILogger<AgentSecurityBaselineController> logger) : ControllerBase
{
    private static readonly SecurityBaselinePolicy DefaultPolicy = new("managed-ssh-v1") { AutomaticallyManageSsh = true };

    [HttpGet("api/agent/security-baseline")]
    public async Task<IActionResult> GetPolicy()
    {
        var machine = Guid.Parse(User.FindFirst("DmsMachine")!.Value);
        if (sessions.Get(machine)?.Ready != true)
            return StatusCode(StatusCodes.Status503ServiceUnavailable);

        var value = await settings.GetSettingValueAsync(SettingsMap.ScheduledSecurityBaseline);
        ScheduledSecurityBaselineSettings? additional;
        try { additional = ScheduledSecurityBaselineSettings.Parse(value); }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            logger.LogWarning("Ignoring invalid additional security baseline settings. The default SSH policy applies.");
            return Ok(DefaultPolicy);
        }

        return Ok(additional != null && additional.MachineIds.Contains(machine)
            ? additional.Policy with { AutomaticallyManageSsh = true }
            : DefaultPolicy);
    }
}
