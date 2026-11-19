namespace ServerForge.Contracts.Enums;

public enum ApplicationRuntimeType
{
    NextJsSsr = 0,
    ReactViteStatic = 1,
    NestJsApi = 2,
    ExpressApi = 3,
    NodeJsGeneric = 4,
    AspNetCore = 5,
    ClassicAsp = 6,
    PythonFastApi = 7,
    StaticHtml = 8
}

public enum DeploymentState
{
    Queued = 0,
    Fetching = 1,
    Building = 2,
    Testing = 3,
    Packaging = 4,
    Staging = 5,
    Deploying = 6,
    Verifying = 7,
    Successful = 8,
    Failed = 9,
    RolledBack = 10,
    Cancelled = 11
}

public enum DeploymentSourceMode
{
    GitHubActionsArtifact = 0,
    ServerLocalBuildExplicitOptIn = 1,
    ManualSignedPackage = 2
}

public enum EnvironmentTier
{
    Development = 0,
    Staging = 1,
    Production = 2
}

public enum ResourceManagementState
{
    ReadOnlyUnmanaged = 0,
    AdoptedManaged = 1,
    ServerForgeCreated = 2
}

public enum DatabaseEngineType
{
    SqlServer = 0,
    PostgreSqlLocal = 1,
    PostgreSqlExternalManaged = 2
}

public enum ServerConnectionStatus
{
    Unpaired = 0,
    PairingPending = 1,
    Connected = 2,
    Degraded = 3,
    Disconnected = 4,
    AuthenticationFailed = 5
}

public enum LogCategory
{
    Deployment = 0,
    ApplicationStdout = 1,
    ApplicationStderr = 2,
    Agent = 3,
    Iis = 4,
    WindowsService = 5,
    HealthCheck = 6,
    GitHub = 7,
    Audit = 8
}
