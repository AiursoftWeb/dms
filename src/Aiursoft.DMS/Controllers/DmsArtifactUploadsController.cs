using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.DMS.Controllers;

public sealed partial class DmsArtifactsController
{
    private IActionResult UploadMetadata(ArtifactUploadStatus value) => Ok(new ArtifactUploadResponse { Code = Code.ResultShown, Message = "Upload position recorded.", Data = value });
    private async Task RequireUploadAsync(Guid id)
    {
        await access.RequireViewAsync(Actor);
        var record = store.UploadRecord(id);
        if (record.OwnerId != Actor || record.MachineTaskId != null) throw new DmsException(403, "This upload belongs to a different owner or machine task.");
    }
    [HttpPost("uploads"), RequestSizeLimit(16384)]
    public Task<IActionResult> BeginUpload([FromBody] ArtifactReference? reference) => Handle(async () =>
    {
        if (!ModelState.IsValid || reference == null) throw new DmsException(400, "A valid artifact manifest is required.");
        await access.RequireViewAsync(Actor);
        var status = await store.BeginUploadAsync(reference, Actor, null, HttpContext.RequestAborted);
        access.Audit(Actor, "artifact.upload-start", reference.Id, System.Text.Json.JsonSerializer.Serialize(reference)); await db.SaveChangesAsync();
        return UploadMetadata(status);
    });
    [HttpGet("uploads/{id:guid}")]
    public Task<IActionResult> UploadStatus(Guid id) => Handle(async () => { await RequireUploadAsync(id); return UploadMetadata(store.UploadStatus(id)); });
    [HttpPut("uploads/{id:guid}"), RequestSizeLimit(DmsArtifactStore.ChunkBytes)]
    public Task<IActionResult> AppendUpload(Guid id, [FromQuery] long offset) => Handle(async () =>
    {
        if (!ModelState.IsValid || !Request.Query.ContainsKey("offset")) throw new DmsException(400, "A numeric chunk offset is required.");
        await RequireUploadAsync(id);
        return UploadMetadata(await store.AppendUploadAsync(id, offset, Request.Body, HttpContext.RequestAborted));
    });
    [HttpPost("uploads/{id:guid}/complete")]
    public Task<IActionResult> CompleteUpload(Guid id) => Handle(async () =>
    {
        await RequireUploadAsync(id); var result = await store.CompleteUploadAsync(id, HttpContext.RequestAborted);
        access.Audit(Actor, "artifact.upload-complete", id, System.Text.Json.JsonSerializer.Serialize(result.Artifact)); await db.SaveChangesAsync();
        return UploadMetadata(result);
    });
}

public sealed partial class AgentArtifactsController
{
    private IActionResult UploadMetadata(ArtifactUploadStatus value) => Ok(new ArtifactUploadResponse { Code = Code.ResultShown, Message = "Upload position recorded.", Data = value });
    private async Task RequireUploadAsync(Guid taskId, Guid id)
    {
        var box = await RequireTaskAsync(taskId); var record = store.UploadRecord(id);
        if (record.OwnerId != box.RequesterId || record.MachineTaskId != taskId) throw new DmsException(403, "Upload does not belong to this approved task.");
    }
    [HttpPost("uploads"), RequestSizeLimit(16384)]
    public Task<IActionResult> BeginUpload([FromBody] ArtifactReference? reference, [FromQuery] Guid taskId) => Handle(async () =>
    {
        if (!ModelState.IsValid || reference == null) throw new DmsException(400, "A valid artifact manifest and machine task are required.");
        var box = await RequireTaskAsync(taskId);
        var status = await store.BeginUploadAsync(reference, box.RequesterId, taskId, HttpContext.RequestAborted);
        access.Audit(box.RequesterId, "artifact.machine-export-start", reference.Id, "Task " + taskId); await db.SaveChangesAsync(); return UploadMetadata(status);
    });
    [HttpGet("uploads/{id:guid}")]
    public Task<IActionResult> UploadStatus(Guid id, [FromQuery] Guid taskId) => Handle(async () => { await RequireUploadAsync(taskId, id); return UploadMetadata(store.UploadStatus(id)); });
    [HttpPut("uploads/{id:guid}"), RequestSizeLimit(DmsArtifactStore.ChunkBytes)]
    public Task<IActionResult> AppendUpload(Guid id, [FromQuery] Guid taskId, [FromQuery] long offset) => Handle(async () =>
    {
        if (!ModelState.IsValid || !Request.Query.ContainsKey("offset")) throw new DmsException(400, "A numeric chunk offset and machine task are required.");
        await RequireUploadAsync(taskId, id); return UploadMetadata(await store.AppendUploadAsync(id, offset, Request.Body, HttpContext.RequestAborted));
    });
    [HttpPost("uploads/{id:guid}/complete")]
    public Task<IActionResult> CompleteUpload(Guid id, [FromQuery] Guid taskId) => Handle(async () =>
    {
        await RequireUploadAsync(taskId, id); var value = await store.CompleteUploadAsync(id, HttpContext.RequestAborted);
        var box = await RequireTaskAsync(taskId);
        access.Audit(box.RequesterId, "artifact.machine-export-complete", id, "Task " + taskId + "; " + System.Text.Json.JsonSerializer.Serialize(value.Artifact)); await db.SaveChangesAsync();
        return UploadMetadata(value);
    });
}
