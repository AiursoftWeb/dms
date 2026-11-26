using System.Security.Claims;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
                401 or 403 => Code.Unauthorized, 404 => Code.NotFound, 409 => Code.Conflict, 503 => Code.RemoteNotAccessible, _ => Code.UnknownError
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
    public Task<IActionResult> JoinKeys() => Handle(async () => await enrollment.KeysAsync(Actor));

    [HttpDelete("access/join-keys/{id:guid}"), Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> RevokeJoinKey(Guid id) => Handle(async () => await enrollment.RevokeAsync(Actor, id));

    [HttpGet("access/enrollments"), Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> Enrollments() => Handle(async () => await enrollment.ListAsync(Actor));

    [HttpGet("access"), Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> Access() => Handle(async () => await management.SnapshotAsync(Actor));

    [HttpPost("access/machines/{id:guid}/key"), Authorize(Policy = "CanManageDmsAccess")]
    public Task<IActionResult> IssueAgentKey(Guid id) => Handle(async () => await management.AgentKeyAsync(Actor, id));

    [HttpPost("lockboxes")]
    public Task<IActionResult> Submit([FromBody] OperationRequest? request) => Handle(async () =>
    {
        if (request == null || !ModelState.IsValid) throw new DmsException(400, "Invalid operation request.");
        var box = await workflows.SubmitAsync(Actor, request);
        return new LockboxReceipt(box.Id, box.WorkflowId);
    });

    [HttpGet("workflow-definitions")]
    public Task<IActionResult> Definitions() => Handle(async () => await workflows.ListDefinitionsAsync(Actor));

    [HttpGet("audit-events")]
    public Task<IActionResult> AuditEvents([FromQuery] WorkflowAuditQuery query) => Handle(async () =>
    {
        await access.RequireViewAsync(Actor);
        if (!ModelState.IsValid) throw new DmsException(400, "Invalid investigation filters.");
        return await investigation.ReadAsync(Actor, query);
    });

    [HttpGet("lockbox-history")]
    public Task<IActionResult> LockboxHistory([FromQuery] HistoryQuery query) => Handle(async () =>
    {
        if (!ModelState.IsValid) throw new DmsException(400, "Invalid history filters.");
        return await investigation.ReadHistoryAsync(Actor, lockboxScope: true, query);
    });

    [HttpGet("workflow-history")]
    public Task<IActionResult> WorkflowHistory([FromQuery] HistoryQuery query) => Handle(async () =>
    {
        if (!ModelState.IsValid) throw new DmsException(400, "Invalid history filters.");
        return await investigation.ReadHistoryAsync(Actor, lockboxScope: false, query);
    });

    [HttpGet("lockboxes")]
    public Task<IActionResult> Lockboxes(int page = 0, DecisionState? decision = null) => Handle(async () =>
    {
        if (!ModelState.IsValid) throw new DmsException(400, "Invalid Lockbox filters.");
        return await workflows.ListLockboxesAsync(Actor, page, decision);
    });

    [HttpGet("lockboxes/review")]
    public Task<IActionResult> ReviewQueue(int page = 0) => Handle(async () =>
    {
        if (!ModelState.IsValid) throw new DmsException(400, "Invalid Lockbox page.");
        return await workflows.ListReviewQueueAsync(Actor, page);
    });

    [HttpGet("lockboxes/review/history")]
    public Task<IActionResult> ReviewHistory(int page = 0) => Handle(async () =>
    {
        if (!ModelState.IsValid) throw new DmsException(400, "Invalid Lockbox page.");
        return await workflows.ListReviewHistoryAsync(Actor, page);
    });

    [HttpGet("lockboxes/review/updates")]
    public Task<IActionResult> ReviewUpdates(DateTimeOffset? after, CancellationToken token) => Handle(async () =>
    {
        if (!ModelState.IsValid) throw new DmsException(400, "Invalid Lockbox update cursor.");
        var auth = await HttpContext.AuthenticateAsync(DmsKeyAuthenticationHandler.SchemeName);
        if (auth.Properties?.Items.TryGetValue(DmsKeyAuthenticationHandler.PersonalKeyIdProperty, out var value) != true ||
            !Guid.TryParse(value, out var keyId))
            throw new DmsException(401, "A personal API Key is required.");
        return await workflows.WaitForReviewUpdateAsync(Actor, keyId, after?.UtcDateTime, token);
    });

    [HttpGet("lockboxes/{id:guid}")]
    [HttpGet("lockboxes/lb/{id:guid}")]
    public Task<IActionResult> Get(Guid id) => Handle(async () => (await workflows.GetLockboxAsync(Actor, id)).Details());

    [HttpGet("lockboxes/{id:guid}/status")]
    [HttpGet("lockboxes/lb/{id:guid}/status")]
    public Task<IActionResult> LockboxStatus(Guid id, bool includeDefinition = false) =>
        Handle(async () => await workflows.GetLockboxStatusAsync(Actor, id, includeDefinition));

    [HttpGet("lockboxes/{id:guid}/result")]
    [HttpGet("lockboxes/lb/{id:guid}/result")]
    public Task<IActionResult> OpenResult(Guid id, CancellationToken token) =>
        Handle(async () => await workflows.WaitForOpenResultAsync(Actor, id, token));

    [HttpGet("lockboxes/{id:guid}/box-secret-metadata")]
    [HttpGet("lockboxes/lb/{id:guid}/box-secret-metadata")]
    public Task<IActionResult> BoxSecretMetadata(Guid id, CancellationToken token) =>
        Handle(async () => await workflows.WaitForBoxSecretMetadataAsync(Actor, id, token));

    [HttpGet("lockboxes/{id:guid}/clickhouse-result")]
    [HttpGet("lockboxes/lb/{id:guid}/clickhouse-result")]
    public Task<IActionResult> ClickHouseResult(Guid id, CancellationToken token) =>
        Handle(async () => await workflows.WaitForClickHouseResultAsync(Actor, id, token));

    public sealed class DecisionInput
    {
        public bool Approve { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    [HttpPost("lockboxes/{id:guid}/decision")]
    [HttpPost("lockboxes/lb/{id:guid}/decision")]
    public Task<IActionResult> Decide(Guid id, [FromBody] DecisionInput? input) => Handle(async () =>
    {
        if (input == null || !ModelState.IsValid) throw new DmsException(400, "Invalid approval decision.");
        return (await workflows.DecideAsync(Actor, id, input.Approve, input.Reason)).Details();
    });

    [HttpPost("lockboxes/{id:guid}/withdraw")]
    [HttpPost("lockboxes/lb/{id:guid}/withdraw")]
    public Task<IActionResult> Withdraw(Guid id) => Handle(async () => (await workflows.WithdrawAsync(Actor, id)).Details());

    [HttpGet("operations/{id:guid}")]
    public Task<IActionResult> Workflow(Guid id) => Handle(async () => await workflows.GetWorkflowAsync(Actor, id));

    [HttpGet("operations/{id:guid}/result")]
    public Task<IActionResult> WorkflowResult(Guid id, [FromServices] FilesystemListResults results, CancellationToken token) =>
        Handle(async () => await results.ReadAsync(Actor, id, token));

    [HttpGet("operations/{id:guid}/callstack")]
    public Task<IActionResult> Callstack(Guid id) => Handle(async () => (await workflows.CallstackAsync(Actor, id, includeMachineNames: true)).Select(run => new
    {
        run.Id, run.RootId, run.ParentId, LockboxId = (LockboxId?)run.LockboxId, run.MachineId,
        run.Definition, run.State, run.CreatedAt, run.StartedAt, run.FinishedAt, run.Result,
        run.Input, run.Version, run.IsMachineTask, run.LastSequence, run.Origin, run.Trigger,
        run.ScheduleId, run.ScheduleExpression, run.ScheduledAt, run.MachineName
    }).ToList());

    [HttpGet("operations/{id:guid}/trace")]
    public Task<IActionResult> Trace(Guid id, int skip = 0, bool recursive = false) => Handle(async () =>
    {
        if (!ModelState.IsValid) throw new DmsException(400, "Invalid trace options.");
        if (skip < 0) throw new DmsException(400, "Offset cannot be negative.");
        var scope = await workflows.TraceScopeAsync(Actor, id, recursive);
        if (!traces.Enabled) throw new DmsException(503, "ClickHouse is not configured. Trace remains in the durable backlog.");
        try
        {
            await traces.InitializeAsync();
            var entries = await traces.ReadAsync(scope.Keys.ToArray(), skip);
            foreach (var entry in entries)
                entry.WorkflowDefinition = scope[entry.WorkflowId];
            return new { Entries = entries, PendingArchiveEvents = spool.PendingCount, NextOffset = (long)skip + TraceDatabase.PageSize };
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Trace query is unavailable for workflow {WorkflowId}.", id);
            throw new DmsException(503, "Trace archive is temporarily unavailable. Retry later; pending events remain in durable storage.");
        }
    });
}
