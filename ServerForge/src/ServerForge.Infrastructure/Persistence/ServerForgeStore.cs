using Microsoft.EntityFrameworkCore;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;

namespace ServerForge.Infrastructure.Persistence;

public sealed class ServerForgeStore : IDeploymentStatePersistence
{
    private readonly IDbContextFactory<ServerForgeDbContext> _factory;
    private readonly ISecretProtector _secretProtector;

    public ServerForgeStore(IDbContextFactory<ServerForgeDbContext> factory, ISecretProtector secretProtector)
    {
        _factory = factory;
        _secretProtector = secretProtector;
    }

    // ---- Local Admin & Encrypted Vault Secrets (Problem E) ----

    public async Task<bool> HasLocalAdminAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.AdminProfiles.AnyAsync(ct);
    }

    public async Task<LocalAdminProfile?> GetLocalAdminAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.AdminProfiles
            .OrderBy(a => a.CreatedAtUtc)
            .ThenBy(a => a.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SaveLocalAdminAsync(LocalAdminProfile profile, CancellationToken ct = default)
    {
        if (profile.Id == Guid.Empty)
            profile.Id = Guid.NewGuid();

        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.AdminProfiles.FindAsync([profile.Id], ct);
        if (existing is null)
            db.AdminProfiles.Add(profile);
        else
            db.Entry(existing).CurrentValues.SetValues(profile);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveVaultSecretAsync(string key, string plaintextValue, string actor, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var protectedBlob = _secretProtector.Protect(plaintextValue, $"vault:{key}");
        var existing = await db.VaultSecrets.FindAsync([key], ct);
        if (existing is null)
        {
            db.VaultSecrets.Add(new VaultSecretRecord
            {
                Key = key,
                ProtectedValue = protectedBlob,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.ProtectedValue = protectedBlob;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        db.AuditLogs.Add(new AuditLogRecord
        {
            Actor = actor,
            Action = "Vault.SecretUpdated",
            TargetResource = key,
            Details = "Encrypted via DPAPI; plaintext never persisted or logged."
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetVaultSecretAsync(string key, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var record = await db.VaultSecrets.FindAsync([key], ct);
        if (record is null || string.IsNullOrWhiteSpace(record.ProtectedValue))
            return null;

        return _secretProtector.Unprotect(record.ProtectedValue, $"vault:{key}");
    }

    // ---- Paired Servers Persistence (Problem E) ----

    public async Task<IReadOnlyList<ManagedServer>> GetServersAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Servers.OrderBy(s => s.DisplayName).ToListAsync(ct);
    }

    public async Task UpsertServerAsync(ManagedServer server, string actor, CancellationToken ct = default)
    {
        if (server.Id == Guid.Empty)
            server.Id = Guid.NewGuid();

        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.Servers.FirstOrDefaultAsync(
            s => s.Id == server.Id || (!string.IsNullOrEmpty(server.AgentId) && s.AgentId == server.AgentId), ct);

        if (existing is null)
        {
            db.Servers.Add(server);
        }
        else
        {
            server.Id = existing.Id;
            db.Entry(existing).CurrentValues.SetValues(server);
        }

        db.AuditLogs.Add(new AuditLogRecord
        {
            Actor = actor,
            Action = existing is null ? "Server.Paired" : "Server.Updated",
            TargetResource = $"{server.DisplayName} ({server.HostAddress}:{server.AgentPort})",
            ServerId = server.Id,
            Details = $"Status={server.Status}; PinnedCert={server.PinnedServerCertThumbprint[..Math.Min(12, server.PinnedServerCertThumbprint.Length)]}"
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteServerAsync(Guid serverId, string actor, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var server = await db.Servers.FindAsync([serverId], ct);
        if (server is null) return;

        db.Servers.Remove(server);
        db.AuditLogs.Add(new AuditLogRecord
        {
            Actor = actor,
            Action = "Server.Unpaired",
            TargetResource = server.DisplayName,
            ServerId = serverId,
            Details = "Removed server connection record from local ServerForge Desktop store."
        });
        await db.SaveChangesAsync(ct);
    }

    // ---- Projects ----

    public const string DefaultProjectSlug = "default";

    public async Task<IReadOnlyList<ProjectGroup>> GetProjectsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Projects.Include(p => p.Applications).OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<ProjectGroup?> GetProjectByIdAsync(Guid id, CancellationToken ct = default)
    {
        if (id == Guid.Empty) return null;
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Projects.FindAsync([id], ct);
    }

    /// <summary>
    /// Explicitly resolves or creates the single authorized default ProjectGroup (slug "default")
    /// without ever creating duplicate or unrelated project groups.
    /// </summary>
    public async Task<ProjectGroup> EnsureDefaultProjectAsync(string actor, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.Projects.FirstOrDefaultAsync(p => p.Slug == DefaultProjectSlug, ct);
        if (existing is not null)
            return existing;

        var defaultProject = new ProjectGroup
        {
            Id = Guid.NewGuid(),
            Name = "Default Project",
            Slug = DefaultProjectSlug,
            Description = "Authorized default ServerForge project group"
        };

        db.Projects.Add(defaultProject);
        db.AuditLogs.Add(new AuditLogRecord
        {
            Actor = actor,
            Action = "Project.DefaultCreated",
            TargetResource = $"{defaultProject.Name} ({defaultProject.Slug})",
            Details = defaultProject.Description
        });
        await db.SaveChangesAsync(ct);
        return defaultProject;
    }

    public async Task UpsertProjectAsync(ProjectGroup project, string actor, CancellationToken ct = default)
    {
        if (project.Id == Guid.Empty)
            project.Id = Guid.NewGuid();
        if (string.IsNullOrWhiteSpace(project.Name))
            throw new ArgumentException("ProjectGroup.Name is required.", nameof(project));
        if (string.IsNullOrWhiteSpace(project.Slug))
            throw new ArgumentException("ProjectGroup.Slug is required.", nameof(project));

        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.Projects.FindAsync([project.Id], ct);
        if (existing is null)
            db.Projects.Add(project);
        else
            db.Entry(existing).CurrentValues.SetValues(project);

        db.AuditLogs.Add(new AuditLogRecord
        {
            Actor = actor,
            Action = existing is null ? "Project.Created" : "Project.Updated",
            TargetResource = $"{project.Name} ({project.Slug})",
            Details = project.Description
        });
        await db.SaveChangesAsync(ct);
    }

    // ---- Applications ----

    public async Task<IReadOnlyList<ManagedApplication>> GetApplicationsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Applications.OrderBy(a => a.Name).ToListAsync(ct);
    }

    public async Task<ManagedApplication?> GetApplicationByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Applications.FindAsync([id], ct);
    }

    public async Task UpsertApplicationAsync(ManagedApplication app, string actor, CancellationToken ct = default)
    {
        if (app.Id == Guid.Empty)
            app.Id = Guid.NewGuid();

        if (app.ProjectGroupId == Guid.Empty)
        {
            throw new ArgumentException(
                "ManagedApplication.ProjectGroupId cannot be Guid.Empty. Assign an existing ProjectGroup or call EnsureDefaultProjectAsync first.",
                nameof(app));
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        app.UpdatedAtUtc = DateTimeOffset.UtcNow;

        if (!await db.Projects.AnyAsync(p => p.Id == app.ProjectGroupId, ct))
        {
            throw new InvalidOperationException(
                $"Referenced ProjectGroup '{app.ProjectGroupId}' does not exist. Persist the ProjectGroup before saving ManagedApplication '{app.Slug}'.");
        }

        var existing = await db.Applications.FindAsync([app.Id], ct);
        if (existing is null)
            db.Applications.Add(app);
        else
            db.Entry(existing).CurrentValues.SetValues(app);

        db.AuditLogs.Add(new AuditLogRecord
        {
            Actor = actor,
            Action = existing is null ? "Application.Created" : "Application.Updated",
            TargetResource = $"{app.Name} ({app.Slug})",
            ApplicationId = app.Id,
            ServerId = app.ServerId,
            Details = $"Runtime={app.RuntimeType}; Repo={app.RepositoryFullName}; Branch={app.Branch}; AutoDeploy={app.AutoDeployEnabled}"
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Removes an application from ServerForge management WITHOUT deleting hosted files, IIS sites, or databases.
    /// </summary>
    public async Task<bool> RemoveApplicationFromManagementAsync(Guid applicationId, string actor, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.Applications.FindAsync([applicationId], ct);
        if (existing is null) return false;

        db.Applications.Remove(existing);
        db.AuditLogs.Add(new AuditLogRecord
        {
            Actor = actor,
            Action = "Application.RemovedFromManagement",
            TargetResource = $"{existing.Name} ({existing.Slug})",
            ApplicationId = applicationId,
            Details = "Removed from ServerForge management registry only; hosted release files, IIS sites, and databases were NOT deleted."
        });

        await db.SaveChangesAsync(ct);
        return true;
    }

    // ---- Environment Variables ----

    public async Task<IReadOnlyList<EnvironmentVariableRecord>> GetEnvironmentVariablesAsync(
        Guid applicationId,
        EnvironmentTier? tier = null,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.EnvironmentVariables.Where(v => v.ApplicationId == applicationId);
        if (tier.HasValue) q = q.Where(v => v.Tier == tier.Value);
        return await q.OrderBy(v => v.Key).ToListAsync(ct);
    }

    public async Task SaveEnvironmentVariableAsync(
        Guid applicationId,
        EnvironmentTier tier,
        string key,
        string plaintextValue,
        bool isSecret,
        bool isRequired,
        string actor,
        CancellationToken ct = default)
    {
        if (applicationId == Guid.Empty)
            throw new ArgumentException("ApplicationId cannot be Guid.Empty when saving an environment variable.", nameof(applicationId));

        await using var db = await _factory.CreateDbContextAsync(ct);
        var storedValue = isSecret
            ? _secretProtector.Protect(plaintextValue, $"env:{applicationId}:{tier}:{key}")
            : plaintextValue;

        var existing = await db.EnvironmentVariables
            .FirstOrDefaultAsync(v => v.ApplicationId == applicationId && v.Tier == tier && v.Key == key, ct);

        if (existing is null)
        {
            db.EnvironmentVariables.Add(new EnvironmentVariableRecord
            {
                ApplicationId = applicationId,
                Tier = tier,
                Key = key,
                IsSecret = isSecret,
                IsRequired = isRequired,
                ProtectedValue = storedValue,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.IsSecret = isSecret;
            existing.IsRequired = isRequired;
            existing.ProtectedValue = storedValue;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        db.AuditLogs.Add(new AuditLogRecord
        {
            Actor = actor,
            Action = "EnvVar.Upserted",
            TargetResource = $"{tier}/{key}",
            ApplicationId = applicationId,
            Details = isSecret ? "Secret value updated (DPAPI encrypted, never logged)." : "Non-secret value updated."
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteEnvironmentVariableAsync(Guid envVarId, string actor, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.EnvironmentVariables.FindAsync([envVarId], ct);
        if (existing is null) return;

        db.EnvironmentVariables.Remove(existing);
        db.AuditLogs.Add(new AuditLogRecord
        {
            Actor = actor,
            Action = "EnvVar.Deleted",
            TargetResource = $"{existing.Tier}/{existing.Key}",
            ApplicationId = existing.ApplicationId,
            Details = "Removed environment variable."
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<Dictionary<string, string>> ResolveDecryptedEnvVarsAsync(
        Guid applicationId,
        EnvironmentTier tier,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var vars = await db.EnvironmentVariables
            .Where(v => v.ApplicationId == applicationId && v.Tier == tier)
            .ToListAsync(ct);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in vars)
        {
            result[v.Key] = v.IsSecret
                ? _secretProtector.Unprotect(v.ProtectedValue, $"env:{applicationId}:{tier}:{v.Key}")
                : v.ProtectedValue;
        }

        return result;
    }

    // ---- Domains, Databases, Backups ----

    public async Task<IReadOnlyList<DomainBindingRecord>> GetDomainsAsync(Guid? applicationId = null, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.DomainBindings.AsQueryable();
        if (applicationId.HasValue) q = q.Where(d => d.ApplicationId == applicationId.Value);
        return await q.OrderBy(d => d.Hostname).ToListAsync(ct);
    }

    public async Task UpsertDomainAsync(DomainBindingRecord domain, string actor, CancellationToken ct = default)
    {
        if (domain.Id == Guid.Empty)
            domain.Id = Guid.NewGuid();
        if (domain.ApplicationId == Guid.Empty)
            throw new ArgumentException("DomainBindingRecord.ApplicationId cannot be Guid.Empty.", nameof(domain));

        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.DomainBindings.FindAsync([domain.Id], ct);
        if (existing is null) db.DomainBindings.Add(domain);
        else db.Entry(existing).CurrentValues.SetValues(domain);

        db.AuditLogs.Add(new AuditLogRecord
        {
            Actor = actor,
            Action = "Domain.Configured",
            TargetResource = domain.Hostname,
            ApplicationId = domain.ApplicationId,
            ServerId = domain.ServerId,
            Details = $"HTTPS={domain.HttpsEnabled}; WinAcme={domain.UseWinAcmeLetsEncrypt}; DnsVerified={domain.DnsVerified}"
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DatabaseServerRecord>> GetDatabaseServersAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.DatabaseServers.OrderBy(d => d.DisplayName).ToListAsync(ct);
    }

    public async Task UpsertDatabaseServerAsync(DatabaseServerRecord record, string? plaintextConnectionString, string actor, CancellationToken ct = default)
    {
        if (record.Id == Guid.Empty)
            record.Id = Guid.NewGuid();

        await using var db = await _factory.CreateDbContextAsync(ct);
        if (!string.IsNullOrWhiteSpace(plaintextConnectionString))
            record.ProtectedConnectionString = _secretProtector.Protect(plaintextConnectionString, $"db:{record.Id}");

        var existing = await db.DatabaseServers.FindAsync([record.Id], ct);
        if (existing is null) db.DatabaseServers.Add(record);
        else db.Entry(existing).CurrentValues.SetValues(record);

        db.AuditLogs.Add(new AuditLogRecord
        {
            Actor = actor,
            Action = existing is null ? "DatabaseServer.Added" : "DatabaseServer.Updated",
            TargetResource = $"{record.DisplayName} ({record.Engine})",
            Details = $"ExternalManaged={record.IsExternalManagedInfrastructure}; PublicPortExposed={record.ExposePublicly}"
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<BackupJobRecord>> GetBackupJobsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.BackupJobs.OrderBy(b => b.TargetName).ToListAsync(ct);
    }

    public async Task UpsertBackupJobAsync(BackupJobRecord job, string actor, CancellationToken ct = default)
    {
        if (job.Id == Guid.Empty)
            job.Id = Guid.NewGuid();

        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.BackupJobs.FindAsync([job.Id], ct);
        if (existing is null) db.BackupJobs.Add(job);
        else db.Entry(existing).CurrentValues.SetValues(job);

        db.AuditLogs.Add(new AuditLogRecord
        {
            Actor = actor,
            Action = "BackupJob.Saved",
            TargetResource = job.TargetName,
            Details = $"Type={job.BackupType}; Schedule={job.ScheduleCron}; RetentionDays={job.RetentionDays}"
        });
        await db.SaveChangesAsync(ct);
    }

    // ---- Deployments & Crash Recovery (Problem G) ----

    public async Task RecordDeploymentAsync(DeploymentRecord record, CancellationToken ct = default)
    {
        if (record.Id == Guid.Empty)
            record.Id = Guid.NewGuid();
        if (record.ApplicationId == Guid.Empty)
            throw new ArgumentException("DeploymentRecord.ApplicationId cannot be Guid.Empty.", nameof(record));

        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.Deployments.FindAsync([record.Id], ct);
        if (existing is null)
            db.Deployments.Add(record);
        else
            db.Entry(existing).CurrentValues.SetValues(record);
        await db.SaveChangesAsync(ct);
    }

    public Task SaveDeploymentSnapshotAsync(DeploymentRecord record, CancellationToken ct = default)
        => RecordDeploymentAsync(record, ct);

    public async Task<bool> HasActiveOrSucceededDeploymentForCommitAsync(Guid applicationId, string commitSha, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Deployments.AnyAsync(d =>
            d.ApplicationId == applicationId &&
            d.CommitSha == commitSha &&
            (d.State == DeploymentState.Successful ||
             d.State == DeploymentState.Queued ||
             d.State == DeploymentState.Fetching ||
             d.State == DeploymentState.Building ||
             d.State == DeploymentState.Staging ||
             d.State == DeploymentState.Deploying ||
             d.State == DeploymentState.Verifying), ct);
    }

    /// <summary>
    /// Marks any non-terminal deployments left behind by an unexpected Agent restart as Failed
    /// so operators see accurate recovery status and the queue is never stuck.
    /// </summary>
    public async Task<int> RecoverInterruptedDeploymentsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var interrupted = await db.Deployments
            .Where(d => d.State != DeploymentState.Successful &&
                        d.State != DeploymentState.Failed &&
                        d.State != DeploymentState.RolledBack &&
                        d.State != DeploymentState.Cancelled)
            .ToListAsync(ct);

        foreach (var d in interrupted)
        {
            d.State = DeploymentState.Failed;
            d.CompletedAtUtc = DateTimeOffset.UtcNow;
            d.ErrorSummary = "Deployment was interrupted by an unexpected ServerForge Agent restart. Previous healthy release preserved.";
            db.AuditLogs.Add(new AuditLogRecord
            {
                Actor = "ServerForge.Agent.Recovery",
                Action = "Deployment.InterruptedRecovery",
                TargetResource = d.Id.ToString("N"),
                ApplicationId = d.ApplicationId,
                Details = d.ErrorSummary
            });
        }

        if (interrupted.Count > 0)
            await db.SaveChangesAsync(ct);

        return interrupted.Count;
    }

    public Task<IReadOnlyList<DeploymentRecord>> GetDeploymentHistoryAsync(Guid? applicationId = null, int take = 50, CancellationToken ct = default)
        => GetDeploymentHistoryAsync(applicationId, skip: 0, take: take, ct);

    public async Task<IReadOnlyList<DeploymentRecord>> GetDeploymentHistoryAsync(Guid? applicationId, int skip, int take, CancellationToken ct = default)
    {
        if (skip < 0)
            throw new ArgumentOutOfRangeException(nameof(skip), "Skip cannot be negative.");
        if (take < 0)
            throw new ArgumentOutOfRangeException(nameof(take), "Take cannot be negative.");

        ct.ThrowIfCancellationRequested();
        if (take == 0)
            return [];

        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.Deployments.AsNoTracking().AsQueryable();
        if (applicationId.HasValue)
            query = query.Where(d => d.ApplicationId == applicationId.Value);

        var ordered = query
            .OrderByDescending(d => d.QueuedAtUtc)
            .ThenByDescending(d => d.Id);

        var paged = skip > 0 ? ordered.Skip(skip).Take(take) : ordered.Take(take);
        return await paged.ToListAsync(ct);
    }

    public async Task RecordAuditAsync(AuditLogRecord entry, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.AuditLogs.Add(entry);
        await db.SaveChangesAsync(ct);
    }

    public Task<IReadOnlyList<AuditLogRecord>> GetRecentAuditLogsAsync(int take = 100, CancellationToken ct = default)
        => GetRecentAuditLogsAsync(skip: 0, take: take, ct);

    public async Task<IReadOnlyList<AuditLogRecord>> GetRecentAuditLogsAsync(int skip, int take, CancellationToken ct = default)
    {
        if (skip < 0)
            throw new ArgumentOutOfRangeException(nameof(skip), "Skip cannot be negative.");
        if (take < 0)
            throw new ArgumentOutOfRangeException(nameof(take), "Take cannot be negative.");

        ct.ThrowIfCancellationRequested();
        if (take == 0)
            return [];

        await using var db = await _factory.CreateDbContextAsync(ct);
        var ordered = db.AuditLogs
            .AsNoTracking()
            .OrderByDescending(a => a.TimestampUtc)
            .ThenByDescending(a => a.Id);

        var paged = skip > 0 ? ordered.Skip(skip).Take(take) : ordered.Take(take);
        return await paged.ToListAsync(ct);
    }
}
