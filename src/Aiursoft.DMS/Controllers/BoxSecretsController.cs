using System.Security.Claims;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Authorization;
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
[Route("api/v1/dms/box-secrets")]
public sealed class BoxSecretsController(
    WorkflowService workflows, TemplateDbContext db, AccessService access, ControllerGate gate, BoxSecretTransfers transfers) : ControllerBase
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpPost("set"), RequestSizeLimit(32768)]
    public async Task<IActionResult> Set([FromBody] BoxSecretSubmission? input)
    {
        if (!Request.IsHttps) return StatusCode(403);
        if (input == null || !ModelState.IsValid) return BadRequest();
        try
        {
            var box = await workflows.SubmitBoxSecretAsync(Actor, input);
            return Ok(new DmsResponse { Code = Code.ResultShown, Message = "Box secret update requested.", Data = new LockboxReceipt(box.Id, box.WorkflowId) });
        }
        catch (DmsException exception)
        {
            return StatusCode(exception.StatusCode, new DmsResponse { Message = exception.Message });
        }
    }

    [HttpPost("{id:guid}/reveal")]
    public Task<IActionResult> Reveal(Guid id, CancellationToken token) => gate.RunAsync<IActionResult>(async () =>
    {
        if (!Request.IsHttps) return StatusCode(403);
        var definition = MachineWorkflows.BoxGetSecretRaw;
        var box = await db.Lockboxes.AsNoTracking().NotCacheable().SingleOrDefaultAsync(b => b.Id == id && b.RequesterId == Actor
            && b.Definition == definition.Name && b.Version == definition.Version && b.Risk == definition.Risk, token);
        if (box == null) return NotFound();
        if (!DmsPermissions.ForRisk(await access.PrincipalAsync(Actor), definition.Risk).CanRequest)
            return StatusCode(403);
        if (!BoxSecretTransfers.IndependentlyApproved(box) || box.WorkflowId is not { } workflow ||
            !await db.WorkflowRuns.AsNoTracking().NotCacheable().AnyAsync(r => r.Id == workflow && r.LockboxId == box.Id && r.State == RunState.Succeeded, token))
            return Conflict();
        var raw = transfers.Take(Actor, box.ClientRequestId, reveal: true);
        if (raw == null) return StatusCode(410);
        var request = System.Text.Json.JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!;
        var input = definition.ReadInput(request.Input);
        access.Audit(Actor, "box.secret.reveal", box.Id, input.Name);
        await db.SaveChangesAsync(token);
        return Ok(new DmsResponse { Code = Code.ResultShown, Message = "Box secret revealed.", Data = new BoxSecretValue(raw) });
    });
}

[Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsAgent")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/agent/box-secrets/{taskId:guid}")]
public sealed class AgentBoxSecretsController(TemplateDbContext db, BoxSecretTransfers transfers) : ControllerBase
{
    [HttpPost("take-set")]
    public Task<IActionResult> TakeSet(Guid taskId, CancellationToken token) => TransferAsync(taskId, null, token);

    [HttpPost("publish-raw"), RequestSizeLimit(32768)]
    public Task<IActionResult> Publish(Guid taskId, [FromBody] BoxSecretValue? input, CancellationToken token) =>
        input == null || !ModelState.IsValid ? Task.FromResult<IActionResult>(BadRequest()) : TransferAsync(taskId, input, token);

    private async Task<IActionResult> TransferAsync(Guid taskId, BoxSecretValue? value, CancellationToken token)
    {
        if (!Request.IsHttps) return StatusCode(403);
        var machine = Guid.Parse(User.FindFirst("DmsMachine")!.Value);
        var definition = value == null ? MachineWorkflows.BoxSetSecret : MachineWorkflows.BoxGetSecretRaw;
        var task = await db.WorkflowRuns.AsNoTracking().NotCacheable().SingleOrDefaultAsync(t => t.Id == taskId && t.MachineId == machine &&
            t.IsMachineTask && t.Origin == WorkflowOrigin.Manual && t.Definition == definition.Name && t.Version == definition.Version, token);
        if (task == null) return StatusCode(403);
        var launch = await db.WorkflowLaunches.AsNoTracking().NotCacheable().SingleOrDefaultAsync(x => x.RootId == task.RootId, token);
        if (launch == null || launch.Definition != definition.Name || launch.Version != definition.Version ||
            launch.Risk != definition.Risk || !launch.ReadTargets().SequenceEqual([machine]) ||
            launch.ReadParameters().Input != task.Input)
            return StatusCode(403);
        if (task.State is RunState.Offered or RunState.Accepted) return Conflict();
        if (task.State != RunState.Running) return StatusCode(403);
        var request = launch.ReadParameters().RequestId;
        if (value == null)
        {
            var raw = transfers.Take(launch.RequesterId, request, reveal: false);
            return raw == null ? StatusCode(410) : Ok(new BoxSecretValue(raw));
        }
        if (launch.Automatic || launch.ApproverId == null || launch.ApproverId == launch.RequesterId) return StatusCode(403);
        try { value.Validate(); }
        catch (ArgumentException) { return BadRequest(); }
        return transfers.Stage(launch.RequesterId, request, reveal: true, value.Value) ? NoContent() : StatusCode(410);
    }
}
