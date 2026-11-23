using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.DMS.Controllers;

[Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsAgent")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentScheduledUpgradeController(AgentReleaseStore releases) : ControllerBase
{
    [HttpGet("api/agent/scheduled-upgrade")]
    public IActionResult GetRelease()
    {
        try { return Ok(releases.Read()); }
        catch (DmsException error) { return Problem(statusCode: error.StatusCode, detail: error.Message); }
    }
}
