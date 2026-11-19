using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;

namespace ServerForge.Core.Interfaces;

public interface ISecretProtector
{
    string Protect(string plaintext, string purposeContext);
    string Unprotect(string protectedBase64, string purposeContext);
    string RedactSecrets(string input, IEnumerable<string> knownSecrets);
}

public interface IPathBoundaryGuard
{
    string EnsureWithinRoot(string allowedRoot, string candidateRelativeOrFullPath);
    bool IsValidWorkingDirectory(string workingDirectory);
}

public interface IDeploymentEngine
{
    Task<DeploymentRecord> EnqueueAndExecuteAsync(
        ManagedApplication application,
        TriggerDeploymentRequest request,
        IReadOnlyDictionary<string, string> resolvedEnvironmentVariables,
        IProgress<DeploymentStatusUpdate>? progress = null,
        CancellationToken cancellationToken = default);

    Task<DeploymentRecord> RollbackToLastHealthyReleaseAsync(
        ManagedApplication application,
        string requestedBy,
        CancellationToken cancellationToken = default);

    bool TryCancelDeployment(Guid deploymentId);
}

public interface IAppDeploymentAdapter
{
    ApplicationRuntimeType SupportedRuntime { get; }
    bool SupportsZeroDowntimeSwap { get; }
    string ZeroDowntimeExplanation { get; }

    Task<DryRunPlanDto> PlanDeploymentAsync(ManagedApplication app, string stagedReleaseDir, CancellationToken ct);
    Task<AdapterDeployResult> DeployStagedReleaseAsync(
        ManagedApplication app,
        string stagedReleaseDir,
        IReadOnlyDictionary<string, string> envVars,
        Action<string> log,
        CancellationToken ct);
    Task RollbackReleaseAsync(
        ManagedApplication app,
        string previousHealthyReleaseDir,
        Action<string> log,
        CancellationToken ct);
}

public sealed record AdapterDeployResult(
    bool Succeeded,
    bool ZeroDowntimeAchieved,
    string? ZeroDowntimeNote,
    string? ErrorMessage);

public interface IIisManager
{
    Task<IReadOnlyList<DiscoveredIisSiteDto>> DiscoverSitesAsync(CancellationToken ct);
    Task<IReadOnlyList<DiscoveredAppPoolDto>> DiscoverAppPoolsAsync(CancellationToken ct);
    Task<bool> IsUrlRewriteInstalledAsync(CancellationToken ct);
    Task<bool> IsArrInstalledAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> DetectBindingConflictsAsync(string hostname, int port, string? excludingSiteName, CancellationToken ct);
    Task<DryRunPlanDto> PlanWebsiteProvisioningAsync(ManagedApplication app, string targetPhysicalPath, CancellationToken ct);
    Task EnsureManagedSiteAndPoolAsync(ManagedApplication app, string targetPhysicalPath, CancellationToken ct);
    Task SwitchSitePhysicalPathAsync(ManagedApplication app, string newReleasePath, CancellationToken ct);
    Task ConfigureReverseProxyWebConfigAsync(ManagedApplication app, string siteRootPath, CancellationToken ct);
    Task SetSiteStateAsync(string siteName, ResourceManagementState managementState, bool start, CancellationToken ct);
}

public interface IWindowsServiceManager
{
    Task<bool> ServiceExistsAsync(string serviceName, CancellationToken ct);
    Task InstallOrUpdateWinSwServiceAsync(ManagedApplication app, string releasePath, IReadOnlyDictionary<string, string> envVars, CancellationToken ct);
    Task StartServiceAsync(string serviceName, CancellationToken ct);
    Task StopServiceAsync(string serviceName, CancellationToken ct);
    Task RestartServiceAsync(string serviceName, CancellationToken ct);
    Task<ServiceStatusDto?> GetServiceStatusAsync(string serviceName, CancellationToken ct);
}

public interface IGitHubClient
{
    Task<GitHubTokenValidationResult> ValidateTokenAsync(string personalAccessToken, CancellationToken ct);
    Task<IReadOnlyList<GitHubRepositoryInfo>> ListRepositoriesAsync(string personalAccessToken, string? searchQuery, CancellationToken ct);
    Task<IReadOnlyList<GitHubCommitInfo>> GetRecentCommitsAsync(string personalAccessToken, string repositoryFullName, string branch, int count, CancellationToken ct);
    Task<GitHubHeadCheckResult> CheckBranchHeadConditionalAsync(string personalAccessToken, string repositoryFullName, string branch, string? lastETag, CancellationToken ct);
    Task<GitHubArtifactDownloadResult> DownloadVerifiedWorkflowArtifactAsync(
        string personalAccessToken,
        string repositoryFullName,
        string commitSha,
        string destinationZipPath,
        CancellationToken ct);
}

public sealed record GitHubTokenValidationResult(
    bool IsValid,
    string? Login,
    string? AccountName,
    int RateLimitRemaining,
    DateTimeOffset RateLimitResetUtc,
    IReadOnlyList<string> GrantedScopesOrPermissions,
    string? ErrorMessage);

public sealed record GitHubRepositoryInfo(
    long Id,
    string FullName,
    string DefaultBranch,
    bool IsPrivate,
    string HtmlUrl,
    string? Description,
    DateTimeOffset UpdatedAtUtc);

public sealed record GitHubCommitInfo(
    string Sha,
    string ShortSha,
    string Message,
    string AuthorName,
    DateTimeOffset CommittedAtUtc);

public sealed record GitHubHeadCheckResult(
    bool Modified,
    string? LatestCommitSha,
    string? CommitMessage,
    string? CommitAuthor,
    string? ETag,
    int RateLimitRemaining);

public sealed record GitHubArtifactDownloadResult(
    bool Found,
    string? LocalZipPath,
    string? ComputedSha256,
    long? WorkflowRunId,
    string? ErrorMessage);

public interface ISqlServerManager
{
    Task<IReadOnlyList<string>> DiscoverLocalInstancesAsync(CancellationToken ct);
    Task<DatabaseHealthDto> TestConnectionAsync(DatabaseServerRecord server, string decryptedConnString, CancellationToken ct);
    Task<IReadOnlyList<string>> ListDatabasesAsync(string decryptedConnString, CancellationToken ct);
    Task CreateDatabaseAsync(string decryptedConnString, string databaseName, string? scopedLoginName, string? scopedPassword, CancellationToken ct);
    Task<string> BackupDatabaseAsync(string decryptedConnString, string databaseName, string backupDirectory, CancellationToken ct);
    Task RestoreDatabaseAsync(string decryptedConnString, string databaseName, string backupFilePath, bool explicitOperatorApproval, CancellationToken ct);
}

public interface IPostgreSqlManager
{
    Task<DatabaseHealthDto> TestConnectionAsync(DatabaseServerRecord server, string decryptedConnString, CancellationToken ct);
    Task<IReadOnlyList<string>> ListDatabasesAsync(string decryptedConnString, CancellationToken ct);
    Task CreateDatabaseAsync(DatabaseServerRecord server, string decryptedConnString, string databaseName, string? ownerRole, CancellationToken ct);
    Task<string> BackupDatabaseAsync(string decryptedConnString, string databaseName, string backupDirectory, CancellationToken ct);
}

public interface IServerTelemetryProvider
{
    Task<ServerDiagnosticsSnapshot> CollectDiagnosticsAsync(string agentId, CancellationToken ct);
    Task<ServerTelemetrySnapshot> CollectTelemetryAsync(
        string agentId,
        IEnumerable<ManagedApplication> managedApps,
        IEnumerable<DatabaseServerRecord> databases,
        CancellationToken ct);
}
