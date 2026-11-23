using System.Globalization;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class FilesystemListResults(TemplateDbContext db, AccessService access, TraceStore spool,
    TraceDatabase archive, ILogger<FilesystemListResults> logger)
{
    // A listing is limited to 1000 entries. Bound archive reads even if the stored data is invalid.
    private const int MaxChunks = 16384;

    public async Task<FilesystemListResult> ReadAsync(string actor, Guid id, CancellationToken token)
    {
        await access.RequireViewAsync(actor);
        var run = await db.WorkflowRuns.AsNoTracking().NotCacheable().SingleOrDefaultAsync(item => item.Id == id, token)
            ?? throw new DmsException(404, "Workflow not found.");
        var launch = await db.WorkflowLaunches.AsNoTracking().NotCacheable()
            .SingleOrDefaultAsync(item => item.RootId == run.RootId, token);
        var definition = OperationsWorkflows.FilesystemLs;
        var isChild = run.Definition == definition.Name && run.IsMachineTask;
        if (!isChild && (run.Definition != WorkflowNames.Batch || launch?.Definition != definition.Name))
            throw new DmsException(400, "This result view requires a filesystem ls workflow.");
        var input = definition.ReadInput(isChild ? run.Input : launch!.ReadParameters().Input);

        var children = await (from child in db.WorkflowRuns.AsNoTracking()
                              where (isChild ? child.Id == id : child.ParentId == id) &&
                                    child.Definition == definition.Name && child.IsMachineTask && child.MachineId != null
                              join machine in db.Machines.AsNoTracking() on child.MachineId equals machine.Id into machines
                              from machine in machines.DefaultIfEmpty()
                              select new { Run = child, Name = machine == null ? null : machine.Name }).NotCacheable().ToListAsync(token);
        var results = new List<MachineFilesystemListResult>();
        foreach (var child in children.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Run.MachineId))
        {
            var task = child.Run;
            var machineId = task.MachineId!.Value;
            OperationResult? result = null;
            IReadOnlyList<FilesystemEntry>? entries = null;
            if (task.State is RunState.Succeeded or RunState.Warning && task.Result.Length > 0)
            {
                result = definition.ReadResult(task.Result);
                entries = result.Facts is { ValueKind: JsonValueKind.Array } facts
                    ? facts.Deserialize<FilesystemEntry[]>()
                    : await ReadLargeResultAsync(task.Id, token);
            }
            results.Add(new MachineFilesystemListResult(machineId, child.Name ?? machineId.ToString(),
                task.Id, task.State, result?.Complete, entries));
        }
        access.Audit(actor, "operation.result", id);
        await db.SaveChangesAsync(token);
        return new FilesystemListResult(id, run.State, input.Path, results);
    }

    private async Task<FilesystemEntry[]?> ReadLargeResultAsync(Guid id, CancellationToken token)
    {
        // Read pending entries first, so even entries flushed while reading remain covered by one source.
        var pending = await spool.ReadPendingDataAsync(id, MaxChunks + 1, token);
        var entries = Decode(pending);
        if (entries != null || !archive.Enabled) return entries;
        try
        {
            await archive.InitializeAsync();
            var archived = await archive.ReadDataAsync(id, MaxChunks + 1);
            return Decode(pending.Concat(archived));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Directory results in the trace archive could not be read for {WorkflowId}.", id);
            return null;
        }
    }

    private static FilesystemEntry[]? Decode(IEnumerable<TraceEntry> chunks)
    {
        var ordered = chunks.DistinctBy(entry => entry.EventId)
            .OrderBy(entry => entry.EventId, StringComparer.Ordinal).ToArray();
        if (ordered.Length is 0 or > MaxChunks || !ordered[0].Message.StartsWith('[') || !ordered[^1].Message.EndsWith(']'))
            return null;
        long? previous = null;
        foreach (var entry in ordered)
        {
            if (!long.TryParse(entry.EventId.AsSpan(entry.EventId.LastIndexOf(':') + 1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var sequence) || previous != null && sequence - 1 != previous)
                return null;
            previous = sequence;
        }
        try { return JsonSerializer.Deserialize<FilesystemEntry[]>(string.Concat(ordered.Select(entry => entry.Message))); }
        catch (JsonException) { return null; }
    }
}
