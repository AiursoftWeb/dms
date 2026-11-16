using System.Security.Claims;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Authorization;
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
public class DmsApiController(WorkflowService workflows, TemplateDbContext db, AccessService access, TraceDatabase traces, TraceStore spool, WorkflowInvestigation investigation, ILogger<DmsApiController> logger, DmsManagementService management, EnrollmentService enrollment) : ControllerBase
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

    [HttpGet("access/identity")]
    public Task<IActionResult> Identity() => Handle(async () =>
    {
        var identity = DmsPermissions.Describe(await access.PrincipalAsync(Actor));
        access.Audit(Actor, "access.identity"); await db.SaveChangesAsync();
        return identity;
    });

    [HttpPost("access/join-keys"), Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> IssueJoinKey([FromBody] JoinKeyInput? input) => Handle(async () =>
    {
        if (input == null || !ModelState.IsValid) throw new DmsException(400, "Invalid join key request.");
        return await enrollment.IssueAsync(Actor, input);
    });

    [HttpGet("access/join-keys"), Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> JoinKeys(Guid clusterId) => Handle(async () => await enrollment.KeysAsync(Actor, clusterId));

    [HttpDelete("access/join-keys/{id:guid}"), Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> RevokeJoinKey(Guid id) => Handle(async () => await enrollment.RevokeAsync(Actor, id));

    [HttpGet("access/enrollments"), Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> Enrollments(Guid clusterId) => Handle(async () => await enrollment.ListAsync(Actor, clusterId));

    [HttpGet("clusters")]
    public Task<IActionResult> Clusters() => Handle(async () => await management.ClustersAsync(Actor, User.HasClaim(AppPermissions.Type, "CanManageDmsAccess")));

    [HttpGet("access"), Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> Access() => Handle(async () => await management.SnapshotAsync(Actor));

    [HttpPost("clusters"), Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> CreateCluster([FromBody] CreateClusterInput? input) => Handle(async () =>
    {
        if (input == null || !ModelState.IsValid) throw new DmsException(400, "Invalid cluster creation request.");
        return await management.CreateAsync(Actor, input);
    });

    [HttpPut("clusters/{id:guid}"), Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> RenameCluster(Guid id, [FromBody] ClusterNameInput? input) => Handle(async () =>
    {
        if (input == null || !ModelState.IsValid) throw new DmsException(400, "Invalid cluster name.");
        return await management.RenameAsync(Actor, id, input.Name);
    });

    [HttpDelete("clusters/{id:guid}"), Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> DropCluster(Guid id) => Handle(async () => await management.DropAsync(Actor, id));

    [HttpPost("access/machines/{id:guid}/key"), Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> IssueAgentKey(Guid id) => Handle(async () => await management.AgentKeyAsync(Actor, id));

    [HttpPost("lockboxes")]
    public Task<IActionResult> Submit([FromBody] OperationRequest? request) => Handle(async () =>
    {
        if (request == null || !ModelState.IsValid) throw new DmsException(400, "Invalid operation request.");
        return await workflows.SubmitAsync(Actor, request);
    });

    [HttpGet("workflow-definitions")]
    public Task<IActionResult> Definitions(Guid clusterId) => Handle(async () => await workflows.ListDefinitionsAsync(Actor, clusterId));

    [HttpGet("audit-events")]
    public Task<IActionResult> AuditEvents([FromQuery] WorkflowAuditQuery query) => Handle(async () =>
    {
        await access.RequireViewAsync(Actor);
        if (!ModelState.IsValid) throw new DmsException(400, "Invalid investigation filters.");
        return await investigation.ReadAsync(Actor, query);
    });

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
        if (!await db.WorkflowRuns.AnyAsync(r => r.Id == id)) throw new DmsException(404, "Workflow not found.");
        await access.RequireViewAsync(Actor);
        access.Audit(Actor, "operation.trace", id); await db.SaveChangesAsync();
        if (!traces.Enabled) throw new DmsException(503, "ClickHouse is not configured. Trace remains in the durable backlog.");
        try
        {
            await traces.InitializeAsync();
            return new { Entries = await traces.ReadAsync(id, skip), PendingArchiveEvents = spool.PendingCount, NextOffset = skip + 200 };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Trace query is unavailable for workflow {WorkflowId}.", id);
            throw new DmsException(503, "Trace archive is temporarily unavailable. Retry later; pending events remain in durable storage.");
        }
    });
}
