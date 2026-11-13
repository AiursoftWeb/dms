using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class AccessService(TemplateDbContext db)
{
    public Task<ClusterGrant?> GrantAsync(string user, Guid cluster, RiskLevel risk) =>
        db.ClusterGrants.AsNoTracking().NotCacheable().SingleOrDefaultAsync(g => g.UserId == user && g.ClusterId == cluster && g.Risk == risk);

    public async Task RequireViewAsync(string user, Guid cluster)
    {
        if (!await db.ClusterGrants.AsNoTracking().NotCacheable().AnyAsync(g => g.UserId == user && g.ClusterId == cluster && (g.CanRequest || g.CanApprove)))
            throw new DmsException(403, "Access to this cluster is denied.");
    }

    public Task<List<Guid>> VisibleClustersAsync(string user) => db.ClusterGrants.AsNoTracking().NotCacheable()
        .Where(g => g.UserId == user && (g.CanRequest || g.CanApprove)).Select(g => g.ClusterId).Distinct().ToListAsync();

    public void Audit(string actor, string action, Guid? id = null, string detail = "") =>
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, Action = action, ResourceId = id, Detail = detail });
}
