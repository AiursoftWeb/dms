using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

// Web and API management entry points require CanManageDmsAccess before calling this service.
public sealed class DmsManagementService(TemplateDbContext db, AccessService access, ControllerGate gate)
{
    private static string Name(string? name) => string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || name.Any(char.IsControl)
        ? throw new DmsException(400, "A name of between 1 and 100 characters is required.") : name.Trim();

    public Task<List<ClusterSummary>> ClustersAsync(string actor, bool manager) => gate.RunAsync(async () =>
    {
        var query = db.Clusters.AsNoTracking().NotCacheable();
        if (!manager)
        {
            var visible = await access.VisibleClustersAsync(actor);
            query = query.Where(c => visible.Contains(c.Id));
        }
        var rows = await query.OrderBy(c => c.Name).Select(c => new ClusterSummary(c.Id, c.Name)).ToListAsync();
        access.Audit(actor, "access.cluster.list"); await db.SaveChangesAsync(); return rows;
    });

    public Task<AccessSnapshot> SnapshotAsync(string actor) => gate.RunAsync(async () =>
    {
        var result = new AccessSnapshot(
            await db.Clusters.AsNoTracking().NotCacheable().OrderBy(c => c.Name).Select(c => new ClusterSummary(c.Id, c.Name)).ToListAsync(),
            await db.Users.AsNoTracking().NotCacheable().OrderBy(u => u.UserName).Select(u => new AccessUser(u.Id, u.UserName ?? "", u.DisplayName, u.CanApproveOwnRequests)).ToListAsync(),
            await db.ClusterGrants.AsNoTracking().NotCacheable().Select(g => new RiskGrant(g.ClusterId, g.UserId, g.Risk, g.CanRequest, g.AutoApprove, g.CanApprove)).ToListAsync(),
            await db.Machines.AsNoTracking().NotCacheable().OrderBy(m => m.Name).Select(m => new AccessMachine(m.Id, m.ClusterId, m.Name, m.Disabled)).ToListAsync());
        access.Audit(actor, "access.inspect"); await db.SaveChangesAsync(); return result;
    });

    public Task<ClusterSummary> CreateAsync(string actor, CreateClusterInput input) => gate.RunAsync(async () =>
    {
        var name = Name(input.Name);
        if (input.Id == Guid.Empty) throw new DmsException(400, "A cluster ID is required. Reuse it when retrying the same creation.");
        var prior = await db.Clusters.NotCacheable().SingleOrDefaultAsync(c => c.Id == input.Id);
        if (prior != null)
        {
            if (prior.Name != name) throw new DmsException(409, "This cluster ID already belongs to a different name.");
            access.Audit(actor, "access.cluster.create.retry", prior.Id); await db.SaveChangesAsync();
            return new ClusterSummary(prior.Id, prior.Name);
        }
        if (await db.Clusters.NotCacheable().AnyAsync(c => c.Name.ToUpper() == name.ToUpper())) throw new DmsException(409, "A cluster with this name already exists.");
        var cluster = new Cluster { Id = input.Id, Name = name };
        db.Clusters.Add(cluster); access.Audit(actor, "access.cluster.create", cluster.Id);
        await db.SaveChangesAsync(); return new ClusterSummary(cluster.Id, cluster.Name);
    });

    public Task<ClusterSummary> RenameAsync(string actor, Guid id, string name) => gate.RunAsync(async () =>
    {
        name = Name(name);
        var cluster = await db.Clusters.NotCacheable().SingleOrDefaultAsync(c => c.Id == id) ?? throw new DmsException(404, "Cluster not found.");
        if (await db.Clusters.NotCacheable().AnyAsync(c => c.Id != id && c.Name.ToUpper() == name.ToUpper())) throw new DmsException(409, "A cluster with this name already exists.");
        cluster.Name = name; access.Audit(actor, "access.cluster.rename", id); await db.SaveChangesAsync();
        return new ClusterSummary(cluster.Id, cluster.Name);
    });

    public Task<ClusterSummary> DropAsync(string actor, Guid id) => gate.RunAsync(async () =>
    {
        var cluster = await db.Clusters.NotCacheable().SingleOrDefaultAsync(c => c.Id == id) ?? throw new DmsException(404, "Cluster not found.");
        if (await db.Machines.NotCacheable().AnyAsync(m => m.ClusterId == id) || await db.Lockboxes.NotCacheable().AnyAsync(l => l.ClusterId == id) || await db.WorkflowRuns.NotCacheable().AnyAsync(r => r.ClusterId == id))
            throw new DmsException(409, "Only an empty cluster with no operation history can be deleted.");
        db.ClusterGrants.RemoveRange(await db.ClusterGrants.NotCacheable().Where(g => g.ClusterId == id).ToListAsync());
        db.Clusters.Remove(cluster); access.Audit(actor, "access.cluster.drop", id); await db.SaveChangesAsync();
        return new ClusterSummary(cluster.Id, cluster.Name);
    });

    public Task<RiskGrant> GrantAsync(string actor, RiskGrant input) => gate.RunAsync(async () =>
    {
        if (!Enum.IsDefined(input.Risk) || !await db.Clusters.NotCacheable().AnyAsync(c => c.Id == input.ClusterId) || !await db.Users.NotCacheable().AnyAsync(u => u.Id == input.UserId))
            throw new DmsException(400, "Select an existing cluster, user and risk level.");
        var grant = await db.ClusterGrants.NotCacheable().SingleOrDefaultAsync(g => g.ClusterId == input.ClusterId && g.UserId == input.UserId && g.Risk == input.Risk);
        if (grant == null) { grant = new ClusterGrant { ClusterId = input.ClusterId, UserId = input.UserId, Risk = input.Risk }; db.ClusterGrants.Add(grant); }
        grant.CanRequest = input.CanRequest; grant.AutoApprove = input.AutoApprove; grant.CanApprove = input.CanApprove;
        access.Audit(actor, "access.grant", input.ClusterId, $"User={input.UserId}; Risk={input.Risk}; Request={input.CanRequest}; Auto={input.AutoApprove}; Approve={input.CanApprove}");
        await db.SaveChangesAsync(); return input;
    });

    public Task<SelfApprovalInput> SelfApprovalAsync(string actor, SelfApprovalInput input) => gate.RunAsync(async () =>
    {
        var user = await db.Users.NotCacheable().SingleOrDefaultAsync(u => u.Id == input.UserId) ?? throw new DmsException(404, "User not found.");
        user.CanApproveOwnRequests = input.Enabled;
        access.Audit(actor, "access.self-approval", detail: $"User={input.UserId}; Enabled={input.Enabled}");
        await db.SaveChangesAsync(); return input;
    });

    public Task<AgentKeyResult> AgentKeyAsync(string actor, Guid id) => gate.RunAsync(async () =>
    {
        var machine = await db.Machines.NotCacheable().SingleOrDefaultAsync(m => m.Id == id) ?? throw new DmsException(404, "Machine not found.");
        if (machine.Disabled) throw new DmsException(409, "The machine is disabled.");
        var key = DmsKeyAuthenticationHandler.CreateKey(true); machine.KeyHash = DmsKeyAuthenticationHandler.Hash(key);
        access.Audit(actor, "key.agent.rotate", id); await db.SaveChangesAsync(); return new AgentKeyResult(id, key);
    });
}
