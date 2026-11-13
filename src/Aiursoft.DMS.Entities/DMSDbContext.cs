using System.Diagnostics.CodeAnalysis;
using Aiursoft.DbTools;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Entities;

[ExcludeFromCodeCoverage]

public abstract class TemplateDbContext(DbContextOptions options) : IdentityDbContext<User>(options), ICanMigrate
{
    public DbSet<GlobalSetting> GlobalSettings => Set<GlobalSetting>();

    public DbSet<Cluster> Clusters => Set<Cluster>();
    public DbSet<ClusterGrant> ClusterGrants => Set<ClusterGrant>();
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<PersonalApiKey> PersonalApiKeys => Set<PersonalApiKey>();
    public DbSet<LockboxRequest> Lockboxes => Set<LockboxRequest>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<ClusterGrant>().HasIndex(x => new { x.ClusterId, x.UserId, x.Risk }).IsUnique();
        builder.Entity<PersonalApiKey>().HasIndex(x => x.KeyHash).IsUnique();
        builder.Entity<Machine>().HasIndex(x => x.KeyHash).IsUnique();
        builder.Entity<LockboxRequest>().HasIndex(x => new { x.RequesterId, x.ClientRequestId }).IsUnique();
        builder.Entity<WorkflowRun>().HasIndex(x => x.RootId);
        builder.Entity<WorkflowRun>().HasIndex(x => new { x.ClusterId, x.CreatedAt });
        builder.Entity<WorkflowRun>().HasIndex(x => new { x.MachineId, x.CreatedAt });
        builder.Entity<AuditEvent>().HasIndex(x => new { x.ResourceId, x.Time });
    }

    public virtual  Task MigrateAsync(CancellationToken cancellationToken) =>
        Database.MigrateAsync(cancellationToken);

    public virtual  Task<bool> CanConnectAsync() =>
        Database.CanConnectAsync();
}
