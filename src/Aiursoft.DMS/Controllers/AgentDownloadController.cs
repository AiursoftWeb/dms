using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.DMS.Controllers;

[AllowAnonymous]
[Route("agent")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentDownloadController(IWebHostEnvironment environment) : ControllerBase
{
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
