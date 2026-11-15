using System.Security.Claims;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.DMS.Controllers;

[Route("api/v1/agent/enrollment")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class EnrollmentController(EnrollmentService enrollment) : ControllerBase
{
    [HttpPost, Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsJoin")]
    public Task<IActionResult> Submit([FromBody] EnrollmentInput? input) => Handle(async () =>
    {
        if (input == null || !ModelState.IsValid) throw new DmsException(400, "Invalid enrollment request.");
        return await enrollment.SubmitAsync(Guid.Parse(User.FindFirstValue("DmsJoinKey")!), input);
    });

    [HttpGet, Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsEnrollment")]
    public Task<IActionResult> Status() => Handle(() => enrollment.StatusAsync(Guid.Parse(User.FindFirstValue("DmsEnrollment")!)));

    private async Task<IActionResult> Handle(Func<Task<EnrollmentStatus>> action)
    {
        try { return Ok(new EnrollmentResponse { Code = Code.ResultShown, Message = "Request completed.", Data = await action() }); }
        catch (DmsException exception)
        {
            if (exception.StatusCode == 400) return BadRequest(new AiurCollection<string>([exception.Message]) { Code = Code.InvalidInput, Message = exception.Message });
            return StatusCode(exception.StatusCode, new EnrollmentResponse { Code = exception.StatusCode == 409 ? Code.Conflict : Code.Unauthorized, Message = exception.Message });
        }
    }
}
