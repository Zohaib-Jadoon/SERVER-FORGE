using Microsoft.EntityFrameworkCore;
using ServerForge.Core.Entities;

namespace ServerForge.Infrastructure.Persistence;

public sealed class ServerForgeDbContext : DbContext
{
    public ServerForgeDbContext(DbContextOptions<ServerForgeDbContext> options) : base(options) { }

    public DbSet<LocalAdminProfile> AdminProfiles => Set<LocalAdminProfile>();
    public DbSet<ProjectGroup> Projects => Set<ProjectGroup>();
    public DbSet<ManagedServer> Servers => Set<ManagedServer>();
    public DbSet<ManagedApplication> Applications => Set<ManagedApplication>();
    public DbSet<DeploymentRecord> Deployments => Set<DeploymentRecord>();
    public DbSet<EnvironmentVariableRecord> EnvironmentVariables => Set<EnvironmentVariableRecord>();
    public DbSet<DomainBindingRecord> DomainBindings => Set<DomainBindingRecord>();
    public DbSet<DatabaseServerRecord> DatabaseServers => Set<DatabaseServerRecord>();
    public DbSet<BackupJobRecord> BackupJobs => Set<BackupJobRecord>();
    public DbSet<AuditLogRecord> AuditLogs => Set<AuditLogRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LocalAdminProfile>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
        });

        modelBuilder.Entity<ProjectGroup>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasMany(x => x.Applications).WithOne().HasForeignKey(a => a.ProjectGroupId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ManagedServer>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AgentId).IsUnique();
        });

        modelBuilder.Entity<ManagedApplication>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ServerId, x.Slug }).IsUnique();
        });

        modelBuilder.Entity<DeploymentRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ApplicationId, x.QueuedAtUtc });
        });

        modelBuilder.Entity<EnvironmentVariableRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ApplicationId, x.Tier, x.Key }).IsUnique();
        });

        modelBuilder.Entity<DomainBindingRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ServerId, x.Hostname, x.HttpPort });
        });

        modelBuilder.Entity<DatabaseServerRecord>(e =>
        {
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<BackupJobRecord>(e =>
        {
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<AuditLogRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TimestampUtc);
        });
    }

    public async Task InitializeSqliteAsync(CancellationToken ct = default)
    {
        await Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
        // Enable Write-Ahead Logging for concurrent Desktop/Agent read/write reliability
        await Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;", ct).ConfigureAwait(false);
    }
}
