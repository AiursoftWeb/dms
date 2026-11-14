using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;

namespace Aiursoft.DMS.Services.Dms;

public sealed class WorkflowInvestigation(TemplateDbContext db, AccessService access, TraceDatabase database, ILogger<WorkflowInvestigation> logger)
{
    public async Task<WorkflowAuditPage> ReadAsync(string actor, WorkflowAuditQuery query)
    {
        // Check the selected cluster before revealing archive availability or accepting other filters.
        await access.RequireViewAsync(actor, query.ClusterId);
        var until = query.Until?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var from = query.From?.ToUniversalTime() ?? until.AddDays(-1);
        if (from < DateTimeOffset.UnixEpoch || until.Year > 2261 || from >= until || until - from > TimeSpan.FromDays(31) || query.Page is < 0 or > 10000 ||
            (query.Kind != null && !Enum.IsDefined(query.Kind.Value)) || (query.State != null && !Enum.IsDefined(query.State.Value)) ||
            new[] { query.RequesterId, query.ApproverId, query.Definition, query.RequestedDefinition }.Any(s => s?.Length > 200))
            throw new DmsException(400, "Choose a valid time range of at most 31 days, page, and investigation filters.");
        query = query with { From = from, Until = until };
        access.Audit(actor, "operation.audit-query", query.ClusterId, $"From={from:O}; Until={until:O}; Page={query.Page}");
        await db.SaveChangesAsync();
        if (!database.Enabled) throw new DmsException(503, "ClickHouse is not configured. Investigation events remain in the durable backlog.");
        try
        {
            await database.InitializeAuditAsync();
            var rows = await database.ReadAuditAsync(query);
            return new WorkflowAuditPage(query, rows.Take(50).ToList(), rows.Count > 50);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Investigation archive query is unavailable for cluster {ClusterId}.", query.ClusterId);
            throw new DmsException(503, "The investigation archive is temporarily unavailable. Retry later; pending events remain durable.");
        }
    }
}
