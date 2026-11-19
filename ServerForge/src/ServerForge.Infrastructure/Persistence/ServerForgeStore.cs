using Microsoft.EntityFrameworkCore;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;

namespace ServerForge.Infrastructure.Persistence;

public sealed class ServerForgeStore
{
    private readonly IDbContextFactory<ServerForgeDbContext> _factory;
    private readonly ISecretProtector _secretProtector;

    public ServerForgeStore(IDbContextFactory<ServerForgeDbContext> factory, ISecretProtector secretProtector)
    {
        _factory = factory;
        _secretProtector = secretProtector;
    }

    public async Task<bool> HasLocalAdminAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.AdminProfiles.AnyAsync(ct);
    }

    public async Task SaveLocalAdminAsync(LocalAdminProfile profile, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.AdminProfiles.Add(profile);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ManagedApplication>> GetApplicationsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Applications.OrderBy(a => a.Name).ToListAsync(ct);
    }

    public async Task UpsertApplicationAsync(ManagedApplication app, string actor, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        app.UpdatedAtUtc = DateTimeOffset.UtcNow;
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

    public async Task SaveEnvironmentVariableAsync(
        Guid applicationId,
        Contracts.Enums.EnvironmentTier tier,
        string key,
        string plaintextValue,
        bool isSecret,
        bool isRequired,
        string actor,
        CancellationToken ct = default)
    {
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

    public async Task<Dictionary<string, string>> ResolveDecryptedEnvVarsAsync(
        Guid applicationId,
        Contracts.Enums.EnvironmentTier tier,
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

    public async Task RecordDeploymentAsync(DeploymentRecord record, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var existing = await db.Deployments.FindAsync([record.Id], ct);
        if (existing is null)
            db.Deployments.Add(record);
        else
            db.Entry(existing).CurrentValues.SetValues(record);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DeploymentRecord>> GetDeploymentHistoryAsync(Guid? applicationId = null, int take = 50, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.Deployments.AsQueryable();
        if (applicationId.HasValue)
            query = query.Where(d => d.ApplicationId == applicationId.Value);

        return await query.OrderByDescending(d => d.QueuedAtUtc).Take(take).ToListAsync(ct);
    }

    public async Task RecordAuditAsync(AuditLogRecord entry, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.AuditLogs.Add(entry);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AuditLogRecord>> GetRecentAuditLogsAsync(int take = 100, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.AuditLogs.OrderByDescending(a => a.TimestampUtc).Take(take).ToListAsync(ct);
    }
}
