using ServerForge.Contracts.Enums;

namespace ServerForge.Contracts.Dtos;

public sealed record PairingInitRequest(
    string DesktopInstanceId,
    string DesktopDisplayName,
    string DesktopClientCertPem,
    string OneTimePairingCode,
    DateTimeOffset RequestedAtUtc);

public sealed record PairingInitResponse(
    bool Accepted,
    string AgentId,
    string Hostname,
    string AgentServerCertPem,
    string AgentCertificateThumbprint,
    string? FailureReason);

public sealed record ServerDiagnosticsSnapshot(
    string AgentId,
    string Hostname,
    string OsVersion,
    bool IsAdministrator,
    bool IisInstalled,
    string? IisVersion,
    bool UrlRewriteInstalled,
    bool ArrInstalled,
    bool AspNetCoreHostingBundleInstalled,
    string? NodeJsVersion,
    string? PythonVersion,
    string? DotNetRuntimeVersions,
    bool WinAcmeInstalled,
    IReadOnlyList<DiscoveredIisSiteDto> ExistingIisSites,
    IReadOnlyList<DiscoveredAppPoolDto> ExistingAppPools,
    IReadOnlyList<string> DiscoveredSqlInstances,
    bool PostgreSqlDetected,
    DateTimeOffset CapturedAtUtc);

public sealed record DiscoveredIisSiteDto(
    long SiteId,
    string Name,
    string State,
    string PhysicalPath,
    string AppPoolName,
    IReadOnlyList<string> Bindings,
    bool ContainsClassicAspIndicators,
    ResourceManagementState ManagementState);

public sealed record DiscoveredAppPoolDto(
    string Name,
    string State,
    string ManagedRuntimeVersion,
    string PipelineMode,
    string IdentityType,
    ResourceManagementState ManagementState);

public sealed record ServerTelemetrySnapshot(
    string AgentId,
    string Hostname,
    double CpuUsagePercent,
    long TotalMemoryBytes,
    long UsedMemoryBytes,
    IReadOnlyList<DriveTelemetryDto> Drives,
    IReadOnlyList<ServiceStatusDto> ManagedServices,
    IReadOnlyList<IisSiteStatusDto> ManagedSites,
    IReadOnlyList<CertificateStatusDto> Certificates,
    IReadOnlyList<DatabaseHealthDto> DatabaseStatuses,
    DateTimeOffset CapturedAtUtc);

public sealed record DriveTelemetryDto(
    string Name,
    string Label,
    long TotalBytes,
    long FreeBytes);

public sealed record ServiceStatusDto(
    string ServiceName,
    string DisplayName,
    string Status,
    long WorkingSetBytes,
    int? ProcessId);

public sealed record IisSiteStatusDto(
    string SiteName,
    string State,
    bool HealthEndpointReachable,
    int? LastHttpStatusCode,
    double? ResponseTimeMs);

public sealed record CertificateStatusDto(
    string Subject,
    string Thumbprint,
    string Hostname,
    DateTimeOffset NotAfterUtc,
    int DaysRemaining,
    bool ManagedByWinAcme);

public sealed record DatabaseHealthDto(
    Guid DatabaseServerId,
    string Name,
    DatabaseEngineType Engine,
    bool Reachable,
    double LatencyMs,
    string? ErrorMessage);

public sealed record TriggerDeploymentRequest(
    Guid ApplicationId,
    string CommitSha,
    string Branch,
    string? CommitMessage,
    string? CommitAuthor,
    string? ArtifactDownloadUrl,
    string? ExpectedArtifactSha256,
    bool TriggeredAutomatically,
    string RequestedBy);

public sealed record DeploymentStatusUpdate(
    Guid DeploymentId,
    Guid ApplicationId,
    DeploymentState State,
    string CommitSha,
    string Message,
    bool ZeroDowntimeSupported,
    string? ActiveReleasePath,
    string? PreviousHealthyReleasePath,
    DateTimeOffset TimestampUtc);

public sealed record DryRunPlanDto(
    string OperationTitle,
    bool ModifiesUnmanagedResource,
    IReadOnlyList<string> PlannedSteps,
    IReadOnlyList<string> SafetyWarnings,
    IReadOnlyList<string> AffectedResources);

public sealed record UnifiedLogEntryDto(
    long Id,
    DateTimeOffset TimestampUtc,
    LogCategory Category,
    string Severity,
    string Source,
    string Message,
    string? TroubleshootingHint,
    Guid? ApplicationId,
    Guid? DeploymentId);
