using System.Security.Claims;
using System.Text.Json;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Controllers;

public abstract class DmsArtifactControllerBase : ControllerBase
{
    protected async Task<IActionResult> Handle(Func<Task<IActionResult>> operation)
    {
        try { return await operation(); }
        catch (DmsException exception) { return StatusCode(exception.StatusCode, new DmsResponse { Code = exception.StatusCode == 404 ? Code.NotFound : exception.StatusCode == 403 ? Code.Unauthorized : Code.InvalidInput, Message = exception.Message }); }
        catch (ArgumentException exception) { return BadRequest(new DmsResponse { Code = Code.InvalidInput, Message = exception.Message }); }
    }
    protected IActionResult Metadata(ArtifactReference reference) => Ok(new ArtifactResponse { Code = Code.ResultShown, Message = "Artifact metadata.", Data = reference });
    protected IActionResult Content(DmsArtifactStore store, ArtifactReference reference)
    {
        Response.Headers.ETag = "\"" + reference.Sha256 + "\"";
        return File(store.Open(reference.Id), "application/octet-stream", reference.Name, enableRangeProcessing: true);
    }
}

[Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsHuman")]
[Route("api/v1/dms/artifacts")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed partial class DmsArtifactsController(DmsArtifactStore store, AccessService access, TemplateDbContext db, DmsArtifactAccess artifacts) : DmsArtifactControllerBase
{
    private string Actor => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private Task<StoredDmsArtifact> RequireAsync(Guid id) => artifacts.RequireHumanAsync(Actor, id);
    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid id) => Handle(async () => { var artifact = await RequireAsync(id); return Metadata(artifact.Reference); });
    [HttpGet("{id:guid}/content")]
    public Task<IActionResult> Download(Guid id) => Handle(async () =>
    {
        var artifact = await RequireAsync(id); access.Audit(Actor, "artifact.download", id, JsonSerializer.Serialize(artifact.Reference)); await db.SaveChangesAsync();
        return Content(store, artifact.Reference);
    });
}

[Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsAgent")]
[Route("api/agent/artifacts")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed partial class AgentArtifactsController(DmsArtifactStore store, TemplateDbContext db, AccessService access, DmsArtifactAccess artifacts) : DmsArtifactControllerBase
{
    private async Task<LockboxRequest> RequireTaskAsync(Guid taskId)
    {
        var machine = Guid.Parse(User.FindFirst("DmsMachine")!.Value);
        var run = await db.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == taskId && r.MachineId == machine && r.IsMachineTask);
        if (run == null || run.State is not (RunState.Running or RunState.NeedsVerification)) throw new DmsException(403, "Artifact transport requires this Agent's already-started machine task.");
        var request = await db.Lockboxes.AsNoTracking().SingleAsync(l => l.Id == run.LockboxId);
        if (request.Decision != DecisionState.Approved) throw new DmsException(403, "The machine task has no approved request.");
        return request;
    }
    private async Task<StoredDmsArtifact> RequireArtifactAsync(Guid taskId, Guid id)
    {
        var request = await RequireTaskAsync(taskId); var artifact = store.Get(id);
        if (artifact.OwnerId != request.RequesterId) throw new DmsException(403, "Artifact belongs to another requester.");
        if (artifact.MachineTaskId != taskId && !artifacts.Inputs(request).Any(frozen => DmsArtifactAccess.Matches(frozen, artifact.Reference)))
            throw new DmsException(403, "Artifact is not an input or output of this approved machine task.");
        return artifact;
    }
    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(Guid id, [FromQuery] Guid taskId) => Handle(async () => Metadata((await RequireArtifactAsync(taskId, id)).Reference));
    [HttpGet("{id:guid}/content")]
    public Task<IActionResult> Download(Guid id, [FromQuery] Guid taskId) => Handle(async () =>
    {
        var artifact = await RequireArtifactAsync(taskId, id);
        access.Audit(artifact.OwnerId, "artifact.machine-import", id, "Task " + taskId); await db.SaveChangesAsync();
        return Content(store, artifact.Reference);
    });
}
