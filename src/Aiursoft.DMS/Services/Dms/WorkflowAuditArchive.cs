using Aiursoft.DMS.Entities;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class WorkflowAuditArchive(TemplateDbContext db, TraceDatabase database)
{
    public int Pending { get; private set; }

    public async Task FlushAsync(CancellationToken token)
    {
        Pending = await db.WorkflowAuditOutbox.NotCacheable().CountAsync(token);
        if (!database.Enabled || Pending == 0) return;
        var batch = await db.WorkflowAuditOutbox.AsNoTracking().NotCacheable().OrderBy(e => e.Id).Take(100).ToListAsync(token);
        await database.InitializeAuditAsync(token);
        foreach (var row in batch)
        {
            await database.InsertAuditAsync(row, token);
            // A crash here repeats the same immutable EventId; ClickHouse FINAL deduplicates it.
            await db.WorkflowAuditOutbox.Where(e => e.Id == row.Id && e.EventId == row.EventId).ExecuteDeleteAsync(token);
            Pending--;
        }
    }
}
