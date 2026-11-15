using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.DMS.Controllers;

[AllowAnonymous]
[Route("agent")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentDownloadController(IWebHostEnvironment environment, AgentReleaseStore releases) : ControllerBase
{
    [HttpGet("releases")]
    public IActionResult Releases()
    {
        try { return Ok(new AgentReleasesResponse { Data = releases.Read(), Message = "Available Agent releases." }); }
        catch (DmsException exception)
        { return StatusCode(exception.StatusCode, new AgentReleasesResponse { Code = Code.RemoteNotAccessible, Message = exception.Message }); }
    }

    [HttpGet("install.sh")]
    public IActionResult Installer() => Asset("install.sh", "text/x-shellscript");

    [HttpGet("download/{architecture}")]
    public IActionResult Download(string architecture) => architecture is "linux-x64" or "linux-arm64"
        ? Asset(architecture + ".tar.gz", "application/gzip") : NotFound();

    private IActionResult Asset(string name, string contentType)
    {
        var file = Path.Combine(environment.ContentRootPath, "agent-assets", name);
        return System.IO.File.Exists(file) ? PhysicalFile(file, contentType, name) : NotFound();
    }
}
