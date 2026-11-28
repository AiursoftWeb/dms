using System.Diagnostics.CodeAnalysis;
using Aiursoft.DbTools;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Entities;

[ExcludeFromCodeCoverage]

public abstract partial class TemplateDbContext(DbContextOptions options) : IdentityDbContext<User>(options), ICanMigrate
{
    public DbSet<GlobalSetting> GlobalSettings => Set<GlobalSetting>();

    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<PersonalApiKey> PersonalApiKeys => Set<PersonalApiKey>();
    public DbSet<PersonalSshKey> PersonalSshKeys => Set<PersonalSshKey>();
    public DbSet<JoinKey> JoinKeys => Set<JoinKey>();
    public DbSet<AgentEnrollment> AgentEnrollments => Set<AgentEnrollment>();
    public DbSet<LockboxRequest> Lockboxes => Set<LockboxRequest>();
    public DbSet<WorkflowLaunch> WorkflowLaunches => Set<WorkflowLaunch>();
    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<WorkflowAuditOutbox> WorkflowAuditOutbox => Set<WorkflowAuditOutbox>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<WorkflowAuditOutbox>().HasIndex(x => x.EventId).IsUnique();
        builder.Entity<PersonalApiKey>().HasIndex(x => x.KeyHash).IsUnique();
        builder.Entity<PersonalSshKey>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        builder.Entity<PersonalSshKey>().HasIndex(x => new { x.UserId, x.Slot }).IsUnique();
        builder.Entity<Machine>().HasIndex(x => x.KeyHash).IsUnique();
        builder.Entity<Machine>().HasIndex(x => x.InstallationId).IsUnique();
        builder.Entity<JoinKey>().HasIndex(x => x.KeyHash).IsUnique();
        builder.Entity<JoinKey>().HasOne(x => x.Issuer).WithMany().OnDelete(DeleteBehavior.Restrict);
        builder.Entity<AgentEnrollment>().HasIndex(x => x.ReceiptHash).IsUnique();
        builder.Entity<AgentEnrollment>().HasIndex(x => x.AgentKeyHash).IsUnique();
        builder.Entity<AgentEnrollment>().HasIndex(x => x.LockboxId).IsUnique();
        builder.Entity<AgentEnrollment>().HasIndex(x => x.InstallationId);
        builder.Entity<AgentEnrollment>().HasOne(x => x.JoinKey).WithMany().OnDelete(DeleteBehavior.Restrict);
        builder.Entity<AgentEnrollment>().HasOne(x => x.Lockbox).WithMany().OnDelete(DeleteBehavior.Restrict);
        builder.Entity<AgentEnrollment>().HasOne(x => x.Machine).WithMany().OnDelete(DeleteBehavior.Restrict);
        builder.Entity<LockboxRequest>().Property(x => x.Decision).IsConcurrencyToken();
        builder.Entity<LockboxRequest>().HasIndex(x => new { x.RequesterId, x.ClientRequestId }).IsUnique();
        builder.Entity<WorkflowRun>().HasIndex(x => x.RootId);
        builder.Entity<WorkflowRun>().HasIndex(x => x.CreatedAt);
        builder.Entity<WorkflowRun>().HasIndex(x => new { x.MachineId, x.CreatedAt });
        builder.Entity<WorkflowRun>().HasIndex(x => new { x.RequesterId, x.CreatedAt });
        builder.Entity<AuditEvent>().HasIndex(x => new { x.ResourceId, x.Time });
    }

    public virtual  Task MigrateAsync(CancellationToken cancellationToken) =>
        Database.MigrateAsync(cancellationToken);

    public virtual  Task<bool> CanConnectAsync() =>
        Database.CanConnectAsync();
}
