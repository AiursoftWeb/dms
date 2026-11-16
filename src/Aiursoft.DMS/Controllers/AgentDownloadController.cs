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
    public IActionResult Installer()
    {
        var file = Path.Combine(environment.ContentRootPath, "agent-assets", "install.sh");
        if (!System.IO.File.Exists(file)) return NotFound();
        var origin = new Uri($"{Request.Scheme}://{Request.Host}").GetLeftPart(UriPartial.Authority);
        var literal = "'" + origin.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return Content(System.IO.File.ReadAllText(file).Replace("controller=''", "controller=" + literal, StringComparison.Ordinal), "text/x-shellscript");
    }

    [HttpGet("download/{architecture}")]
    public IActionResult Download(string architecture) => architecture is "linux-x64" or "linux-arm64"
        ? Asset(architecture + ".tar.gz", "application/gzip") : NotFound();

    [HttpGet("package")]
    public IActionResult Package() => Asset("agent.nupkg", "application/octet-stream");

    private IActionResult Asset(string name, string contentType)
    {
        var file = Path.Combine(environment.ContentRootPath, "agent-assets", name);
        return System.IO.File.Exists(file) ? PhysicalFile(file, contentType, name) : NotFound();
    }
}
