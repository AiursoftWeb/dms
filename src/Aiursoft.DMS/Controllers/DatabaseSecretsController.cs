using System.Security.Claims;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EFCoreSecondLevelCacheInterceptor;

namespace Aiursoft.DMS.Controllers;

[Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsHuman")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/dms/database-secrets")]
public sealed class DatabaseSecretsController(WorkflowService workflows) : ControllerBase
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpPost("upsert"), RequestSizeLimit(131072)]
    public async Task<IActionResult> Upsert([FromBody] DatabaseUpsertSubmission? input)
    {
        if (!Request.IsHttps) return StatusCode(403);
        if (input == null || !ModelState.IsValid) return BadRequest();
        try
        {
            var box = await workflows.SubmitDatabaseUpsertAsync(Actor, input);
            return Ok(new DmsResponse
            {
                Code = Code.ResultShown,
                Message = "Database upsert requested.",
                Data = new LockboxReceipt(box.Id, box.WorkflowId)
            });
        }
        catch (DmsException exception)
        {
            return StatusCode(exception.StatusCode, new DmsResponse { Message = exception.Message });
        }
    }
}

[Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsAgent")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/agent/database-secrets/{taskId:guid}")]
public sealed class AgentDatabaseSecretsController(TemplateDbContext db, DatabaseSecretTransfers transfers) : ControllerBase
{
    [HttpPost("take")]
    public async Task<IActionResult> Take(Guid taskId, CancellationToken token)
    {
        if (!Request.IsHttps) return StatusCode(403);
        var machine = Guid.Parse(User.FindFirst("DmsMachine")!.Value);
        var definition = OperationsWorkflows.DatabaseUpsert;
        var task = await db.WorkflowRuns.AsNoTracking().NotCacheable().SingleOrDefaultAsync(t =>
            t.Id == taskId && t.MachineId == machine &&
            t.IsMachineTask && t.Origin == WorkflowOrigin.Manual &&
            t.Definition == definition.Name && t.Version == definition.Version, token);
        if (task == null) return StatusCode(403);
        var launch = await db.WorkflowLaunches.AsNoTracking().NotCacheable()
            .SingleOrDefaultAsync(x => x.RootId == task.RootId, token);
        if (launch == null || launch.Definition != definition.Name || launch.Version != definition.Version ||
            launch.Risk != definition.Risk || !launch.ReadTargets().SequenceEqual([machine]) ||
            launch.ReadParameters().Input != task.Input)
            return StatusCode(403);
        if (task.State is RunState.Offered or RunState.Accepted) return Conflict();
        if (task.State != RunState.Running) return StatusCode(403);
        var request = launch.ReadParameters().RequestId;
        var secrets = transfers.Take(launch.RequesterId, request);
        return secrets == null ? StatusCode(410) : Ok(new DatabaseUpsertSecretsPayload(secrets));
    }
}
