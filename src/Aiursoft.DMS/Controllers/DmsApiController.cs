using System.Security.Claims;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Controllers;

[Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsHuman")]
[Route("api/v1/dms")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class DmsApiController(WorkflowService workflows, TemplateDbContext db, AccessService access, TraceDatabase traces, TraceStore spool, ILogger<DmsApiController> logger) : ControllerBase
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private async Task<IActionResult> Handle(Func<Task<object>> action)
    {
        try { return Ok(new DmsResponse { Code = Code.ResultShown, Message = "Request completed.", Data = await action() }); }
        catch (DmsException exception)
        {
            if (exception.StatusCode == 400)
                return BadRequest(new AiurCollection<string>([exception.Message]) { Code = Code.InvalidInput, Message = exception.Message });
            return StatusCode(exception.StatusCode, new DmsResponse { Code = exception.StatusCode switch
            {
                403 => Code.Unauthorized, 404 => Code.NotFound, 409 => Code.Conflict, 503 => Code.RemoteNotAccessible, _ => Code.UnknownError
            }, Message = exception.Message });
        }
    }

    [HttpPost("lockboxes")]
    public Task<IActionResult> Submit([FromBody] OperationRequest? request) => Handle(async () =>
    {
        if (request == null || !ModelState.IsValid) throw new DmsException(400, "Invalid operation request.");
        return await workflows.SubmitAsync(Actor, request);
    });

    [HttpGet("workflow-definitions")]
    public Task<IActionResult> Definitions(Guid clusterId) => Handle(async () => await workflows.ListDefinitionsAsync(Actor, clusterId));

    [HttpGet("lockboxes/{id:guid}")]
    public Task<IActionResult> Get(Guid id) => Handle(async () => await workflows.GetLockboxAsync(Actor, id));

    public sealed class DecisionInput
    {
        public bool Approve { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    [HttpPost("lockboxes/{id:guid}/decision")]
    public Task<IActionResult> Decide(Guid id, [FromBody] DecisionInput? input) => Handle(async () =>
    {
        if (input == null || !ModelState.IsValid) throw new DmsException(400, "Invalid approval decision.");
        return await workflows.DecideAsync(Actor, id, input.Approve, input.Reason);
    });

    [HttpGet("operations/{id:guid}")]
    public Task<IActionResult> Workflow(Guid id) => Handle(async () => await workflows.GetWorkflowAsync(Actor, id));

    [HttpGet("operations/{id:guid}/callstack")]
    public Task<IActionResult> Callstack(Guid id) => Handle(async () => await workflows.CallstackAsync(Actor, id));

    [HttpGet("operations/{id:guid}/trace")]
    public Task<IActionResult> Trace(Guid id, int skip = 0) => Handle(async () =>
    {
        if (skip < 0) throw new DmsException(400, "Offset cannot be negative.");
        var run = await db.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id) ?? throw new DmsException(404, "Workflow not found.");
        await access.RequireViewAsync(Actor, run.ClusterId);
        access.Audit(Actor, "operation.trace", id); await db.SaveChangesAsync();
        if (!traces.Enabled) throw new DmsException(503, "ClickHouse is not configured. Trace remains in the durable backlog.");
        try { return new { Entries = await traces.ReadAsync(id, skip), PendingArchiveEvents = spool.PendingCount, NextOffset = skip + 200 }; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Trace query is unavailable for workflow {WorkflowId}.", id);
            throw new DmsException(503, "Trace archive is temporarily unavailable. Retry later; pending events remain in durable storage.");
        }
    });
}
