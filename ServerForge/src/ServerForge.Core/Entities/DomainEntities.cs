using ServerForge.Contracts.Enums;

namespace ServerForge.Core.Entities;

public sealed class LocalAdminProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public bool DarkThemeEnabled { get; set; } = true;
    public bool DemoModeEnabled { get; set; } = false;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ProjectGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<ManagedApplication> Applications { get; set; } = [];
}

public sealed class ManagedServer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AgentId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string HostAddress { get; set; } = string.Empty;
    public int AgentPort { get; set; } = 9443;
    public bool RequirePrivateNetworkOrVpn { get; set; } = true;
    public string PinnedServerCertThumbprint { get; set; } = string.Empty;
    public string ClientCertThumbprint { get; set; } = string.Empty;
    public ServerConnectionStatus Status { get; set; } = ServerConnectionStatus.Unpaired;
    public string? OsVersion { get; set; }
    public string? IisVersion { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ManagedApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectGroupId { get; set; }
    public Guid ServerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public ApplicationRuntimeType RuntimeType { get; set; }
    public ResourceManagementState ManagementState { get; set; } = ResourceManagementState.ServerForgeCreated;

    // GitHub & Source Configuration
    public string RepositoryFullName { get; set; } = string.Empty; // owner/repo
    public string Branch { get; set; } = "main";
    public string WorkingDirectory { get; set; } = ".";
    public bool AutoDeployEnabled { get; set; } = true;
    public bool RepositoryExplicitlyAuthorized { get; set; } = false;
    public DeploymentSourceMode SourceMode { get; set; } = DeploymentSourceMode.GitHubActionsArtifact;
    public string? GitHubWorkflowFileName { get; set; } = "serverforge-deploy.yml";

    // Runtime & Build Commands
    public string BuildCommand { get; set; } = string.Empty;
    public string StartCommand { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public int InternalPort { get; set; }
    public string ServiceAccountName { get; set; } = "NT SERVICE\\ServerForgeApp";

    // Hosting, Routing & Health
    public string IisSiteName { get; set; } = string.Empty;
    public string IisAppPoolName { get; set; } = string.Empty;
    public string WindowsServiceName { get; set; } = string.Empty;
    public string PrimaryDomain { get; set; } = string.Empty;
    public string HealthCheckPath { get; set; } = "/health";
    public int HealthCheckTimeoutSeconds { get; set; } = 30;
    public int RetainHealthyReleasesCount { get; set; } = 5;
    public bool AutoRollbackOnFailure { get; set; } = true;

    // Directories (enforced under agent boundary root)
    public string ReleasesRootPath { get; set; } = string.Empty;
    public string PersistentDataPath { get; set; } = string.Empty;
    public string? ActiveReleasePath { get; set; }
    public string? LastKnownGoodReleasePath { get; set; }
    public string? LastDeployedCommitSha { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DeploymentRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public Guid ServerId { get; set; }
    public DeploymentState State { get; set; } = DeploymentState.Queued;
    public string CommitSha { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string CommitAuthor { get; set; } = string.Empty;
    public string CommitMessage { get; set; } = string.Empty;
    public string? ArtifactSha256 { get; set; }
    public string? ReleaseDirectory { get; set; }
    public string? PreviousReleaseDirectory { get; set; }
    public bool TriggeredAutomatically { get; set; }
    public bool ZeroDowntimeAchieved { get; set; }
    public string? ZeroDowntimeNote { get; set; }
    public string? ErrorSummary { get; set; }
    public string TriggeredBy { get; set; } = string.Empty;
    public DateTimeOffset QueuedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

public sealed class EnvironmentVariableRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public EnvironmentTier Tier { get; set; } = EnvironmentTier.Production;
    public string Key { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
    public bool IsRequired { get; set; }
    /// <summary>
    /// For non-secrets, plaintext value; for secrets, DPAPI-encrypted Base64 blob (never plaintext).
    /// </summary>
    public string ProtectedValue { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DomainBindingRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationId { get; set; }
    public Guid ServerId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public bool HttpsEnabled { get; set; }
    public bool UseWinAcmeLetsEncrypt { get; set; }
    public string? CertificateThumbprint { get; set; }
    public DateTimeOffset? CertificateExpiresUtc { get; set; }
    public bool DnsVerified { get; set; }
    public string? ResolvedIpAddresses { get; set; }
    public DateTimeOffset? LastDnsCheckUtc { get; set; }
}

public sealed class DatabaseServerRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ServerId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public DatabaseEngineType Engine { get; set; }
    public string HostOrInstance { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool IsExternalManagedInfrastructure { get; set; }
    public bool ExposePublicly { get; set; } = false;
    public string ProtectedConnectionString { get; set; } = string.Empty;
    public ResourceManagementState ManagementState { get; set; } = ResourceManagementState.ReadOnlyUnmanaged;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BackupJobRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? DatabaseServerId { get; set; }
    public Guid? ApplicationId { get; set; }
    public string TargetName { get; set; } = string.Empty;
    public string BackupType { get; set; } = "DatabaseFull"; // DatabaseFull, AppConfig, PersistentData
    public string DestinationPath { get; set; } = string.Empty;
    public string ScheduleCron { get; set; } = "0 2 * * *";
    public int RetentionDays { get; set; } = 14;
    public DateTimeOffset? LastRunUtc { get; set; }
    public string? LastStatus { get; set; }
    public long? LastSizeBytes { get; set; }
}

public sealed class AuditLogRecord
{
    public long Id { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string TargetResource { get; set; } = string.Empty;
    public string Outcome { get; set; } = "Succeeded";
    public string Details { get; set; } = string.Empty;
    public Guid? ServerId { get; set; }
    public Guid? ApplicationId { get; set; }
}
