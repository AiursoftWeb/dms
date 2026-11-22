using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

internal static class MachineInventory
{
    public static Task<List<MachineSummary>> ReadAsync(TemplateDbContext db, CancellationToken token = default) =>
        db.Machines.AsNoTracking().NotCacheable()
            .OrderBy(m => m.Name).ThenBy(m => m.Id)
            .Select(m => new MachineSummary(m.Id, m.Name, m.AgentVersion, m.LastSeen, m.Disabled)).ToListAsync(token);
}
