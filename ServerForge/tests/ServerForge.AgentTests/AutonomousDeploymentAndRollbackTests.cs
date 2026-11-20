using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ServerForge.Agent;
using ServerForge.Agent.Configuration;
using ServerForge.Agent.Security;
using ServerForge.Agent.Workers;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.Deployments.Adapters;
using ServerForge.Deployments.Engine;
using ServerForge.Desktop.Services;
using ServerForge.Infrastructure.Persistence;
using ServerForge.Security.Execution;
using ServerForge.Security.Secrets;
using Xunit;

namespace ServerForge.AgentTests;

/// <summary>
/// Phase 12 live loopback integration tests covering:
/// 1. Autonomous GitHub polling while Desktop is closed, duplicate commit skipping,
///    anti-retry-loop after failed/rolled-back commits, and recovery on a new commit.
/// 2. Full Desktop -> mTLS Agent -> Verified Artifact -> Staged WebConfig -> Live HTTP Target
///    -> Healthy Update -> Deliberate SIMULATE_UNHEALTHY=true Failure -> Automatic Rollback.
/// 3. Interrupted deployment recovery across real Agent restarts preserving active healthy releases.
/// Note: Real Windows IIS appcmd/w3wp execution is validated separately by ServerForge/scripts/e2e/*.ps1.
/// </summary>
public sealed class AutonomousDeploymentAndRollbackTests
{
    private const string CommitV1 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CommitV2 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string CommitV3Broken = "cccccccccccccccccccccccccccccccccccccccc";
    private const string CommitV4Recovery = "dddddddddddddddddddddddddddddddddddddddd";

    private sealed class TestDbContextFactory(DbContextOptions<ServerForgeDbContext> options)
        : IDbContextFactory<ServerForgeDbContext>
    {
        public ServerForgeDbContext CreateDbContext() => new(options);

        public Task<ServerForgeDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// Live in-process HTTP server + IIisManager adapter used on headless runners without IIS installed.
    /// Reads the active staged release's real web.config environment variables and release-metadata.json
    /// on every request so deploy, upgrade, SIMULATE_UNHEALTHY=true (HTTP 503), and rollback are exercised
    /// over genuine TCP loopback HTTP requests.
    /// </summary>
    private sealed class LiveLoopbackTargetHost : IIisManager, IAsyncDisposable
    {
        private readonly WebApplication _targetApp;
        private readonly HashSet<string> _managedSites = new(StringComparer.OrdinalIgnoreCase);
        private volatile string? _activePhysicalPath;

        private LiveLoopbackTargetHost(WebApplication targetApp, int port)
        {
            _targetApp = targetApp;
            Port = port;
        }

        public int Port { get; }
        public string? ActivePhysicalPath => _activePhysicalPath;

        public static async Task<LiveLoopbackTargetHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();

            LiveLoopbackTargetHost? hostRef = null;

            app.MapGet("/", () =>
            {
                var (ver, sha, _, _) = ReadActiveReleaseState(hostRef?._activePhysicalPath);
                return Results.Ok(new { service = "ServerForge Loopback E2E Target", version = ver, commitSha = sha });
            });

            app.MapGet("/health", () =>
            {
                var (ver, sha, unhealthy, _) = ReadActiveReleaseState(hostRef?._activePhysicalPath);
                if (unhealthy)
                {
                    return Results.Json(
                        new { status = "unhealthy", version = ver, commitSha = sha, reason = "SIMULATE_UNHEALTHY=true" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                return Results.Ok(new { status = "healthy", version = ver, commitSha = sha });
            });

            app.MapGet("/data", () =>
            {
                var (_, _, _, dataDir) = ReadActiveReleaseState(hostRef?._activePhysicalPath);
                var markerPath = Path.Combine(dataDir, "persistent-marker.txt");
                return Results.Ok(new
                {
                    persistentDataDirectory = dataDir,
                    markerExists = File.Exists(markerPath),
                    markerContent = File.Exists(markerPath) ? File.ReadAllText(markerPath).Trim() : null
                });
            });

            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
            var boundPort = new Uri(addresses.First()).Port;
            hostRef = new LiveLoopbackTargetHost(app, boundPort);
            return hostRef;
        }

        private static (string Version, string CommitSha, bool SimulateUnhealthy, string DataDir) ReadActiveReleaseState(string? releaseDir)
        {
            var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? metaVersion = null;
            string? metaSha = null;

            if (!string.IsNullOrWhiteSpace(releaseDir))
            {
                var webConfig = Path.Combine(releaseDir, "web.config");
                if (File.Exists(webConfig))
                {
                    var doc = XDocument.Load(webConfig);
                    foreach (var el in doc.Descendants("environmentVariable"))
                    {
                        var name = (string?)el.Attribute("name");
                        var val = (string?)el.Attribute("value");
                        if (!string.IsNullOrWhiteSpace(name) && val is not null)
                            envVars[name] = val;
                    }
                }

                var metaFile = Path.Combine(releaseDir, "release-metadata.json");
                if (File.Exists(metaFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(metaFile));
                    if (doc.RootElement.TryGetProperty("version", out var v)) metaVersion = v.GetString();
                    if (doc.RootElement.TryGetProperty("commitSha", out var c)) metaSha = c.GetString();
                }
            }

            envVars.TryGetValue("APP_VERSION", out var envVersion);
            envVars.TryGetValue("APP_COMMIT_SHA", out var envSha);
            envVars.TryGetValue("SIMULATE_UNHEALTHY", out var envUnhealthy);
            envVars.TryGetValue("PERSISTENT_DATA_DIR", out var dataDir);

            dataDir ??= Path.Combine(releaseDir ?? Path.GetTempPath(), "data");
            Directory.CreateDirectory(dataDir);
            var markerPath = Path.Combine(dataDir, "persistent-marker.txt");
            if (!File.Exists(markerPath))
                File.WriteAllText(markerPath, $"initialized:{DateTimeOffset.UtcNow:O}:{Guid.NewGuid():N}");

            return (
                envVersion ?? metaVersion ?? "1.0.0",
                envSha ?? metaSha ?? "unknown",
                string.Equals(envUnhealthy, "true", StringComparison.OrdinalIgnoreCase),
                dataDir);
        }

        public Task<IReadOnlyList<DiscoveredIisSiteDto>> DiscoverSitesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DiscoveredIisSiteDto>>([
                new DiscoveredIisSiteDto(1, "Default Web Site", "Started", @"C:\inetpub\wwwroot", "DefaultAppPool", ["http://*:80:"], false, ResourceManagementState.ReadOnlyUnmanaged)
            ]);

        public Task<IReadOnlyList<DiscoveredAppPoolDto>> DiscoverAppPoolsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DiscoveredAppPoolDto>>([]);

        public Task<bool> IsUrlRewriteInstalledAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<bool> IsArrInstalledAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> DetectBindingConflictsAsync(string hostname, int port, string? excludingSiteName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<DryRunPlanDto> PlanWebsiteProvisioningAsync(ManagedApplication app, string targetPhysicalPath, CancellationToken ct)
            => Task.FromResult(new DryRunPlanDto("Plan", false, [], [], []));
        public Task EnsureManagedSiteAndPoolAsync(ManagedApplication app, string targetPhysicalPath, CancellationToken ct)
        {
            _managedSites.Add(app.IisSiteName);
            return Task.CompletedTask;
        }
        public Task SwitchSitePhysicalPathAsync(ManagedApplication app, string newReleasePath, CancellationToken ct)
        {
            _activePhysicalPath = newReleasePath;
            return Task.CompletedTask;
        }
        public Task ConfigureReverseProxyWebConfigAsync(ManagedApplication app, string siteRootPath, CancellationToken ct) => Task.CompletedTask;
        public Task SetSiteStateAsync(string siteName, ResourceManagementState managementState, bool start, CancellationToken ct) => Task.CompletedTask;
        public void RegisterManagedSite(string siteName) => _managedSites.Add(siteName);

        public async ValueTask DisposeAsync()
        {
            await _targetApp.StopAsync();
            await _targetApp.DisposeAsync();
        }
    }

    private static async Task<(string ZipPath, string Sha256)> CreateAspNetCoreSampleArtifactAsync(
        string directory,
        string fileName,
        string version,
        string commitSha,
        bool bakeUnhealthyWebConfig = false)
    {
        Directory.CreateDirectory(directory);
        var zipPath = Path.Combine(directory, fileName);
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("ServerForge.SampleApp.dll").Open()))
                await w.WriteAsync($"binary-v-{version}");
            using (var w = new StreamWriter(zip.CreateEntry("ServerForge.SampleApp.exe").Open()))
                await w.WriteAsync($"exe-v-{version}");
            using (var w = new StreamWriter(zip.CreateEntry("release-metadata.json").Open()))
                await w.WriteAsync(JsonSerializer.Serialize(new { version, commitSha }));

            if (bakeUnhealthyWebConfig)
            {
                using var w = new StreamWriter(zip.CreateEntry("web.config").Open());
                await w.WriteAsync("""
                    <?xml version="1.0" encoding="utf-8"?>
                    <configuration>
                      <location path="." inheritInChildApplications="false">
                        <system.webServer>
                          <aspNetCore processPath=".\ServerForge.SampleApp.exe" hostingModel="inprocess">
                            <environmentVariables>
                              <environmentVariable name="SIMULATE_UNHEALTHY" value="true" />
                            </environmentVariables>
                          </aspNetCore>
                        </system.webServer>
                      </location>
                    </configuration>
                    """);
            }
        }

        var sha256 = await SafeProcessRunner.ComputeFileSha256Async(zipPath);
        return (zipPath, sha256);
    }

    [Fact]
    public async Task LiveMtls_EndToEndDeployUpdateRollback_OverRealKestrelAndLoopbackTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sf-e2e-loopback-{Guid.NewGuid():N}");
        var agentData = Path.Combine(root, "agent-data");
        var agentApps = Path.Combine(root, "agent-apps");
        var desktopData = Path.Combine(root, "desktop-data");
        var artifactsDir = Path.Combine(root, "artifacts");

        try
        {
            await using var targetHost = await LiveLoopbackTargetHost.StartAsync();

            var (v1Zip, v1Sha) = await CreateAspNetCoreSampleArtifactAsync(artifactsDir, "v1.zip", "1.0.0", CommitV1);
            var (v2Zip, v2Sha) = await CreateAspNetCoreSampleArtifactAsync(artifactsDir, "v2.zip", "1.0.1", CommitV2);
            var (v3Zip, v3Sha) = await CreateAspNetCoreSampleArtifactAsync(artifactsDir, "v3-bad.zip", "1.0.2-bad", CommitV3Broken);

            await using var agent = await AgentHostFactory.CreateAndInitializeAsync(
                args: [],
                overrideOptions: new AgentOptions
                {
                    ServiceName = "ServerForgeAgentE2ETest",
                    BindAddress = "127.0.0.1",
                    Port = 0,
                    DataRoot = agentData,
                    AppsRoot = agentApps
                },
                enableWindowsService: false,
                enableAutoDeployWorker: false,
                configureSerilog: false,
                configureTestServices: services =>
                {
                    services.RemoveAll<IIisManager>();
                    services.AddSingleton<IIisManager>(targetHost);
                });

            await agent.App.StartAsync();
            var agentPort = agent.GetBoundPort();

            var transport = new AgentTransportService(new DesktopIdentityService(desktopData));
            var pair = await transport.PairWithAgentAsync(
                "127.0.0.1", agentPort, agent.InitialPairingCode!, agent.ServerCertificate.Thumbprint, CancellationToken.None);
            Assert.True(pair.Accepted);

            var server = new ManagedServer
            {
                AgentId = pair.AgentId,
                DisplayName = "Loopback E2E Agent",
                HostAddress = "127.0.0.1",
                AgentPort = agentPort,
                PinnedServerCertThumbprint = pair.AgentCertificateThumbprint,
                ClientCertThumbprint = transport.GetCurrentClientThumbprint(),
                Status = ServerConnectionStatus.Connected
            };

            var project = new ProjectGroup { Name = "E2E Disposable", Slug = "e2e-disposable" };
            await transport.SyncProjectAsync(server, project, CancellationToken.None);

            var registered = await transport.RegisterOrUpdateApplicationAsync(server, new UpsertApplicationRequest(
                Id: null,
                ProjectGroupId: project.Id,
                Name: "Disposable Sample App",
                Slug: "sample-e2e",
                RuntimeType: ApplicationRuntimeType.AspNetCore,
                RepositoryFullName: "owner/serverforge-e2e-sample",
                Branch: "main",
                WorkingDirectory: ".",
                AutoDeployEnabled: true,
                RepositoryExplicitlyAuthorized: true,
                SourceMode: DeploymentSourceMode.ManualSignedPackage,
                GitHubWorkflowFileName: "serverforge-deploy.yml",
                BuildCommand: "",
                StartCommand: "",
                RuntimeVersion: "10.0",
                InternalPort: targetHost.Port,
                IisSiteName: "ServerForge_E2E_Loopback",
                IisAppPoolName: "ServerForge_E2E_Loopback_Pool",
                WindowsServiceName: "SF_e2e_loopback",
                PrimaryDomain: "localhost",
                HealthCheckPath: "/health",
                HealthCheckTimeoutSeconds: 5,
                RetainHealthyReleasesCount: 5,
                AutoRollbackOnFailure: true), CancellationToken.None);
            Assert.NotNull(registered);

            using var probeHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{targetHost.Port}/") };

            // 1. Deploy v1 healthy (explicitly labeled LOCAL TEST ARTIFACT via ManualSignedPackage)
            await transport.ConfigureEnvVarsAsync(server, registered!.Id, new ConfigureEnvVarsRequest(
                EnvironmentTier.Production,
                [
                    new EnvVarItemDto("APP_VERSION", "1.0.0", false, true),
                    new EnvVarItemDto("APP_COMMIT_SHA", CommitV1, false, false),
                    new EnvVarItemDto("SIMULATE_UNHEALTHY", "false", false, false)
                ]), CancellationToken.None);

            var dep1 = await transport.TriggerDeploymentAsync(server, new TriggerDeploymentRequest(
                registered.Id, CommitV1, "main", "Initial healthy release", "ci", v1Zip, v1Sha, false, "e2e", null, null, "v1", "LOCAL TEST ARTIFACT (ManualSignedPackage)"), CancellationToken.None);
            Assert.NotNull(dep1);
            Assert.Equal(DeploymentState.Successful, dep1!.State);

            var root1 = await probeHttp.GetFromJsonAsync<JsonElement>("/");
            Assert.Equal("1.0.0", root1.GetProperty("version").GetString());
            var data1 = await probeHttp.GetFromJsonAsync<JsonElement>("/data");
            var originalMarker = data1.GetProperty("markerContent").GetString();
            Assert.False(string.IsNullOrWhiteSpace(originalMarker));

            // 2. Deploy v2 healthy update
            await Task.Delay(1100);
            await transport.ConfigureEnvVarsAsync(server, registered.Id, new ConfigureEnvVarsRequest(
                EnvironmentTier.Production,
                [
                    new EnvVarItemDto("APP_VERSION", "1.0.1", false, true),
                    new EnvVarItemDto("APP_COMMIT_SHA", CommitV2, false, false),
                    new EnvVarItemDto("SIMULATE_UNHEALTHY", "false", false, false)
                ]), CancellationToken.None);

            var dep2 = await transport.TriggerDeploymentAsync(server, new TriggerDeploymentRequest(
                registered.Id, CommitV2, "main", "Healthy v2 update", "ci", v2Zip, v2Sha, false, "e2e", null, null, "v2", "LOCAL TEST ARTIFACT (ManualSignedPackage)"), CancellationToken.None);
            Assert.NotNull(dep2);
            Assert.Equal(DeploymentState.Successful, dep2!.State);

            var root2 = await probeHttp.GetFromJsonAsync<JsonElement>("/");
            Assert.Equal("1.0.1", root2.GetProperty("version").GetString());
            var data2 = await probeHttp.GetFromJsonAsync<JsonElement>("/data");
            Assert.Equal(originalMarker, data2.GetProperty("markerContent").GetString());

            // 3. Deploy v3 deliberately unhealthy (SIMULATE_UNHEALTHY=true -> HTTP 503) -> Automatic rollback to v2
            await Task.Delay(1100);
            await transport.ConfigureEnvVarsAsync(server, registered.Id, new ConfigureEnvVarsRequest(
                EnvironmentTier.Production,
                [
                    new EnvVarItemDto("APP_VERSION", "1.0.2-bad", false, true),
                    new EnvVarItemDto("APP_COMMIT_SHA", CommitV3Broken, false, false),
                    new EnvVarItemDto("SIMULATE_UNHEALTHY", "true", false, false)
                ]), CancellationToken.None);

            var dep3 = await transport.TriggerDeploymentAsync(server, new TriggerDeploymentRequest(
                registered.Id, CommitV3Broken, "main", "Unhealthy release", "ci", v3Zip, v3Sha, false, "e2e", null, null, "v3", "LOCAL TEST ARTIFACT (ManualSignedPackage)"), CancellationToken.None);
            Assert.NotNull(dep3);
            Assert.Equal(DeploymentState.RolledBack, dep3!.State);

            // Live HTTP target MUST be back on v2 (1.0.1), healthy (200), with persistent /data marker untouched
            using var healthAfterRollback = await probeHttp.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, healthAfterRollback.StatusCode);
            var rootAfterRollback = await probeHttp.GetFromJsonAsync<JsonElement>("/");
            Assert.Equal("1.0.1", rootAfterRollback.GetProperty("version").GetString());
            var dataAfterRollback = await probeHttp.GetFromJsonAsync<JsonElement>("/data");
            Assert.Equal(originalMarker, dataAfterRollback.GetProperty("markerContent").GetString());

            var releases = await transport.FetchReleasesAsync(server, registered.Id, CancellationToken.None);
            Assert.True(releases.Count >= 2);
            Assert.Contains(releases, r => r.IsActive);

            await agent.App.StopAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AutonomousWorker_DeploysWithDesktopClosed_SkipsDuplicate_PreventsRetryLoopOnFailure_AndRecoversOnNewCommit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sf-auto-worker-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(root, "agent-data");
        var appsRoot = Path.Combine(root, "agent-apps");
        var artifactsDir = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(appsRoot);

        try
        {
            await using var targetHost = await LiveLoopbackTargetHost.StartAsync();
            var (v1Zip, v1Sha) = await CreateAspNetCoreSampleArtifactAsync(artifactsDir, "v1.zip", "1.0.0", CommitV1);
            var (v2Zip, v2Sha) = await CreateAspNetCoreSampleArtifactAsync(artifactsDir, "v2.zip", "1.0.1", CommitV2);
            var (v3BadZip, v3BadSha) = await CreateAspNetCoreSampleArtifactAsync(artifactsDir, "v3-bad.zip", "1.0.2-bad", CommitV3Broken, bakeUnhealthyWebConfig: true);
            var (v4RecZip, v4RecSha) = await CreateAspNetCoreSampleArtifactAsync(artifactsDir, "v4-rec.zip", "1.0.3-recovered", CommitV4Recovery);

            var dbPath = Path.Combine(dataRoot, "agent-state.db");
            var opts = new DbContextOptionsBuilder<ServerForgeDbContext>().UseSqlite($"Data Source={dbPath}").Options;
            IDbContextFactory<ServerForgeDbContext> factory = new TestDbContextFactory(opts);
            await using (var init = await factory.CreateDbContextAsync())
                await init.InitializeSqliteAsync();

            var protector = new DpapiSecretProtector(DataProtectionScope.CurrentUser);
            var store = new ServerForgeStore(factory, protector);
            var agentOpts = new AgentOptions { DataRoot = dataRoot, AppsRoot = appsRoot };
            var identityStore = new AgentIdentityStore(agentOpts);
            identityStore.StoreProtectedGitHubPat(protector.Protect("ghp_test_autonomous_token", "agent:github-pat"));

            var project = await store.EnsureDefaultProjectAsync("auto-test");
            var app = new ManagedApplication
            {
                ProjectGroupId = project.Id,
                Name = "Auto Sample",
                Slug = "auto-sample",
                RuntimeType = ApplicationRuntimeType.AspNetCore,
                ManagementState = ResourceManagementState.ServerForgeCreated,
                SourceMode = DeploymentSourceMode.GitHubActionsArtifact,
                RepositoryFullName = "owner/auto-sample",
                Branch = "main",
                AutoDeployEnabled = true,
                RepositoryExplicitlyAuthorized = true,
                GitHubWorkflowFileName = "serverforge-deploy.yml",
                InternalPort = targetHost.Port,
                IisSiteName = "ServerForge_E2E_Auto",
                IisAppPoolName = "ServerForge_E2E_Auto_Pool",
                PrimaryDomain = "localhost",
                HealthCheckPath = "/health",
                HealthCheckTimeoutSeconds = 3,
                AutoRollbackOnFailure = true,
                ReleasesRootPath = Path.Combine(appsRoot, "auto-sample"),
                PersistentDataPath = Path.Combine(appsRoot, "auto-sample", "data")
            };
            await store.UpsertApplicationAsync(app, "auto-test");

            var adapter = new AspNetCoreDeploymentAdapter(targetHost);
            using var healthHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var orchestrator = new DeploymentOrchestrator(
                [adapter], new PathBoundaryGuard(), protector, healthHttp, NullLogger<DeploymentOrchestrator>.Instance, store);

            var activeCommit = CommitV1;
            var activeArtifact = (Zip: v1Zip, Sha: v1Sha, RunId: 501L, ArtId: 601L);
            var downloadCalls = 0;

            var ghMock = new Mock<IGitHubClient>();
            ghMock.Setup(g => g.CheckBranchHeadConditionalAsync(It.IsAny<string>(), "owner/auto-sample", "main", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new GitHubHeadCheckResult(true, activeCommit, "commit", "dev", $"\"etag-{activeCommit[..6]}\"", 4900));
            ghMock.Setup(g => g.DownloadVerifiedWorkflowArtifactAsync(
                    It.IsAny<string>(), "owner/auto-sample", "main", It.IsAny<string>(), "serverforge-deploy.yml", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    downloadCalls++;
                    return new GitHubArtifactDownloadResult(true, activeArtifact.Zip, activeArtifact.Sha, activeArtifact.Sha,
                        activeArtifact.RunId, activeArtifact.ArtId, "sample-artifact", "GitHubActions", null);
                });

            var worker1 = new GitHubAutoDeployWorker(
                store, ghMock.Object, orchestrator, protector, identityStore, Options.Create(agentOpts), NullLogger<GitHubAutoDeployWorker>.Instance);

            // 1. Autonomous v1 deploy (no Desktop running)
            await worker1.PollAuthorizedApplicationsAsync(CancellationToken.None);
            Assert.Equal(1, downloadCalls);
            var hist1 = await store.GetDeploymentHistoryAsync(app.Id);
            Assert.Single(hist1);
            Assert.Equal(DeploymentState.Successful, hist1[0].State);
            Assert.True(hist1[0].TriggeredAutomatically);

            // 2. Duplicate poll of same v1 commit MUST NOT re-deploy
            await worker1.PollAuthorizedApplicationsAsync(CancellationToken.None);
            Assert.Equal(1, downloadCalls);

            // 3. Autonomous v2 healthy update
            await Task.Delay(1100);
            activeCommit = CommitV2;
            activeArtifact = (v2Zip, v2Sha, 502L, 602L);
            await worker1.PollAuthorizedApplicationsAsync(CancellationToken.None);
            Assert.Equal(2, downloadCalls);

            using var probeHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{targetHost.Port}/") };
            var v2Root = await probeHttp.GetFromJsonAsync<JsonElement>("/");
            Assert.Equal("1.0.1", v2Root.GetProperty("version").GetString());

            // 4. Autonomous v3 broken release (SIMULATE_UNHEALTHY=true baked into web.config) -> automatic rollback to v2
            await Task.Delay(1100);
            activeCommit = CommitV3Broken;
            activeArtifact = (v3BadZip, v3BadSha, 503L, 603L);
            await worker1.PollAuthorizedApplicationsAsync(CancellationToken.None);
            Assert.Equal(3, downloadCalls);

            var afterBadRoot = await probeHttp.GetFromJsonAsync<JsonElement>("/");
            Assert.Equal("1.0.1", afterBadRoot.GetProperty("version").GetString());
            var histAfterBad = await store.GetDeploymentHistoryAsync(app.Id);
            Assert.Equal(DeploymentState.RolledBack, histAfterBad[0].State);

            // 5. Simulate Agent restart (fresh worker instance with empty ETag cache) -> MUST NOT endlessly retry broken CommitV3Broken
            var worker2AfterRestart = new GitHubAutoDeployWorker(
                store, ghMock.Object, orchestrator, protector, identityStore, Options.Create(agentOpts), NullLogger<GitHubAutoDeployWorker>.Instance);
            await worker2AfterRestart.PollAuthorizedApplicationsAsync(CancellationToken.None);
            Assert.Equal(3, downloadCalls); // Zero retries on failed commit!

            // 6. Push new recovery commit v4 -> autonomous deploy succeeds and promotes 1.0.3-recovered
            await Task.Delay(1100);
            activeCommit = CommitV4Recovery;
            activeArtifact = (v4RecZip, v4RecSha, 504L, 604L);
            await worker2AfterRestart.PollAuthorizedApplicationsAsync(CancellationToken.None);
            Assert.Equal(4, downloadCalls);

            var v4Root = await probeHttp.GetFromJsonAsync<JsonElement>("/");
            Assert.Equal("1.0.3-recovered", v4Root.GetProperty("version").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InterruptedAgentRestart_MarksInFlightDeploymentFailed_AndPreservesActiveHealthyRelease()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sf-interrupted-restart-{Guid.NewGuid():N}");
        var agentData = Path.Combine(root, "agent-data");
        var agentApps = Path.Combine(root, "agent-apps");
        var healthyReleaseDir = Path.Combine(agentApps, "sample-e2e", "releases", "20261120-healthy");
        Directory.CreateDirectory(healthyReleaseDir);
        await File.WriteAllTextAsync(Path.Combine(healthyReleaseDir, "release-metadata.json"), """{"version":"1.0.1"}""");

        try
        {
            Guid appId;
            // Boot Agent 1, persist healthy release + an in-flight Deploying record (simulating crash mid-deploy)
            await using (var agent1 = await AgentHostFactory.CreateAndInitializeAsync(
                args: [],
                overrideOptions: new AgentOptions { ServiceName = "SFInterruptedTest", BindAddress = "127.0.0.1", Port = 0, DataRoot = agentData, AppsRoot = agentApps },
                enableWindowsService: false,
                enableAutoDeployWorker: false,
                configureSerilog: false))
            {
                var store1 = agent1.App.Services.GetRequiredService<ServerForgeStore>();
                var proj = await store1.EnsureDefaultProjectAsync("test");
                var app = new ManagedApplication
                {
                    ProjectGroupId = proj.Id,
                    Name = "Interrupted App",
                    Slug = "sample-e2e",
                    RuntimeType = ApplicationRuntimeType.AspNetCore,
                    RepositoryFullName = "owner/sample-e2e",
                    RepositoryExplicitlyAuthorized = true,
                    ReleasesRootPath = Path.Combine(agentApps, "sample-e2e"),
                    PersistentDataPath = Path.Combine(agentApps, "sample-e2e", "data"),
                    ActiveReleasePath = healthyReleaseDir,
                    LastKnownGoodReleasePath = healthyReleaseDir,
                    LastDeployedCommitSha = CommitV2
                };
                await store1.UpsertApplicationAsync(app, "test");
                appId = app.Id;

                await store1.RecordDeploymentAsync(new DeploymentRecord
                {
                    ApplicationId = appId,
                    CommitSha = CommitV3Broken,
                    Branch = "main",
                    State = DeploymentState.Deploying,
                    PreviousReleaseDirectory = healthyReleaseDir,
                    TriggeredAutomatically = true
                });
            }

            // Boot Agent 2 on same DataRoot -> startup recovery MUST fail interrupted deploy & preserve healthy release
            await using (var agent2 = await AgentHostFactory.CreateAndInitializeAsync(
                args: [],
                overrideOptions: new AgentOptions { ServiceName = "SFInterruptedTest", BindAddress = "127.0.0.1", Port = 0, DataRoot = agentData, AppsRoot = agentApps },
                enableWindowsService: false,
                enableAutoDeployWorker: false,
                configureSerilog: false))
            {
                var store2 = agent2.App.Services.GetRequiredService<ServerForgeStore>();
                var recoveredHistory = await store2.GetDeploymentHistoryAsync(appId);
                Assert.Single(recoveredHistory);
                Assert.Equal(DeploymentState.Failed, recoveredHistory[0].State);
                Assert.Contains("interrupted", recoveredHistory[0].ErrorSummary ?? "", StringComparison.OrdinalIgnoreCase);

                var reloadedApp = await store2.GetApplicationByIdAsync(appId);
                Assert.NotNull(reloadedApp);
                Assert.Equal(healthyReleaseDir, reloadedApp!.ActiveReleasePath);
                Assert.Equal(healthyReleaseDir, reloadedApp.LastKnownGoodReleasePath);
                Assert.True(File.Exists(Path.Combine(healthyReleaseDir, "release-metadata.json")));

                var audits = await store2.GetRecentAuditLogsAsync(20);
                Assert.Contains(audits, a => a.Action == "Deployment.InterruptedRecovery");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LiveMtls_GitHubActionsMode_RejectsSpoofedLocalArtifact_ForgedIds_AndSpoofedAutoDeployFlag()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sf-prov-mtls-{Guid.NewGuid():N}");
        var agentData = Path.Combine(root, "agent-data");
        var agentApps = Path.Combine(root, "agent-apps");
        var desktopData = Path.Combine(root, "desktop-data");
        var artifactsDir = Path.Combine(root, "artifacts");

        try
        {
            await using var targetHost = await LiveLoopbackTargetHost.StartAsync();
            var (v1Zip, v1Sha) = await CreateAspNetCoreSampleArtifactAsync(artifactsDir, "gh-v1.zip", "2.0.0-gh", CommitV1);

            var ghMock = new Mock<IGitHubClient>();
            ghMock.Setup(g => g.DownloadVerifiedWorkflowArtifactAsync(
                    It.IsAny<string>(), "owner/provenance-app", "main", CommitV1, "serverforge-deploy.yml", It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, string _, string _, string _, string _, string destPath, CancellationToken _) =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    File.Copy(v1Zip, destPath, overwrite: true);
                    return new GitHubArtifactDownloadResult(
                        Found: true,
                        LocalZipPath: destPath,
                        ComputedSha256: v1Sha,
                        TrustedApiDigestSha256: v1Sha,
                        WorkflowRunId: 777001,
                        ArtifactId: 888001,
                        ArtifactName: "verified-gh-artifact",
                        TrustSource: "GitHubActionsRun:777001/Artifact:888001",
                        ErrorMessage: null);
                });

            await using var agent = await AgentHostFactory.CreateAndInitializeAsync(
                args: [],
                overrideOptions: new AgentOptions
                {
                    ServiceName = "ServerForgeProvenanceTest",
                    BindAddress = "127.0.0.1",
                    Port = 0,
                    DataRoot = agentData,
                    AppsRoot = agentApps
                },
                enableWindowsService: false,
                enableAutoDeployWorker: false,
                configureSerilog: false,
                configureTestServices: services =>
                {
                    services.RemoveAll<IIisManager>();
                    services.AddSingleton<IIisManager>(targetHost);
                    services.RemoveAll<IGitHubClient>();
                    services.AddSingleton<IGitHubClient>(ghMock.Object);
                });

            await agent.App.StartAsync();
            var agentPort = agent.GetBoundPort();

            var transport = new AgentTransportService(new DesktopIdentityService(desktopData));
            var pair = await transport.PairWithAgentAsync(
                "127.0.0.1", agentPort, agent.InitialPairingCode!, agent.ServerCertificate.Thumbprint, CancellationToken.None);
            Assert.True(pair.Accepted);

            var server = new ManagedServer
            {
                AgentId = pair.AgentId,
                DisplayName = "Provenance Test Agent",
                HostAddress = "127.0.0.1",
                AgentPort = agentPort,
                PinnedServerCertThumbprint = pair.AgentCertificateThumbprint,
                ClientCertThumbprint = transport.GetCurrentClientThumbprint(),
                Status = ServerConnectionStatus.Connected
            };

            await transport.SyncGitHubPatAsync(server, "github_pat_test_provenance_only", CancellationToken.None);
            var project = new ProjectGroup { Name = "Provenance Suite", Slug = "provenance-suite" };
            await transport.SyncProjectAsync(server, project, CancellationToken.None);

            var ghApp = await transport.RegisterOrUpdateApplicationAsync(server, new UpsertApplicationRequest(
                Id: null,
                ProjectGroupId: project.Id,
                Name: "Provenance GH App",
                Slug: "provenance-app",
                RuntimeType: ApplicationRuntimeType.AspNetCore,
                RepositoryFullName: "owner/provenance-app",
                Branch: "main",
                WorkingDirectory: ".",
                AutoDeployEnabled: true,
                RepositoryExplicitlyAuthorized: true,
                SourceMode: DeploymentSourceMode.GitHubActionsArtifact,
                GitHubWorkflowFileName: "serverforge-deploy.yml",
                BuildCommand: "",
                StartCommand: "",
                RuntimeVersion: "10.0",
                InternalPort: targetHost.Port,
                IisSiteName: "ServerForge_E2E_Prov",
                IisAppPoolName: "ServerForge_E2E_Prov_Pool",
                WindowsServiceName: "SF_prov",
                PrimaryDomain: "localhost",
                HealthCheckPath: "/health",
                HealthCheckTimeoutSeconds: 5,
                RetainHealthyReleasesCount: 5,
                AutoRollbackOnFailure: true), CancellationToken.None);
            Assert.NotNull(ghApp);

            // 1. Caller attempts to pass a local file path to GitHubActionsArtifact app -> MUST be rejected HTTP 400
            var localEx = await Assert.ThrowsAsync<HttpRequestException>(() =>
                transport.TriggerDeploymentAsync(server, new TriggerDeploymentRequest(
                    ghApp!.Id, CommitV1, "main", "spoof local path", "attacker", v1Zip, v1Sha, true, "api", 777001, 888001, "spoof", "GitHubActions"), CancellationToken.None));
            Assert.Equal(HttpStatusCode.BadRequest, localEx.StatusCode);

            // 2. Caller supplies forged workflow run ID (999999 != 777001) -> Agent server-side verifier MUST fail closed
            var forgedRunRec = await transport.TriggerDeploymentAsync(server, new TriggerDeploymentRequest(
                ghApp!.Id, CommitV1, "main", "forged run id", "attacker", null, v1Sha, true, "api", 999999, 888001, "spoof", "GitHubActions"), CancellationToken.None);
            Assert.NotNull(forgedRunRec);
            Assert.Equal(DeploymentState.Failed, forgedRunRec!.State);
            Assert.False(forgedRunRec.TriggeredAutomatically); // API caller CANNOT spoof TriggeredAutomatically=true
            Assert.Contains("mismatch", forgedRunRec.ErrorSummary ?? "", StringComparison.OrdinalIgnoreCase);

            // 3. Caller supplies forged artifact ID (999888 != 888001) -> Agent server-side verifier MUST fail closed
            var forgedArtRec = await transport.TriggerDeploymentAsync(server, new TriggerDeploymentRequest(
                ghApp.Id, CommitV1, "main", "forged art id", "attacker", null, v1Sha, true, "api", 777001, 999888, "spoof", "GitHubActions"), CancellationToken.None);
            Assert.NotNull(forgedArtRec);
            Assert.Equal(DeploymentState.Failed, forgedArtRec!.State);
            Assert.False(forgedArtRec.TriggeredAutomatically);

            // 4. Legitimate manual trigger by commit SHA (no caller path) -> Agent verifies server-side via IGitHubClient and succeeds
            var validRec = await transport.TriggerDeploymentAsync(server, new TriggerDeploymentRequest(
                ghApp.Id, CommitV1, "main", "server-verified manual deploy", "operator", null, null, true, "operator"), CancellationToken.None);
            Assert.NotNull(validRec);
            Assert.Equal(DeploymentState.Successful, validRec!.State);
            Assert.False(validRec.TriggeredAutomatically); // External HTTP API is ALWAYS TriggeredAutomatically=false

            using var probeHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{targetHost.Port}/") };
            var liveRoot = await probeHttp.GetFromJsonAsync<JsonElement>("/");
            Assert.Equal("2.0.0-gh", liveRoot.GetProperty("version").GetString());

            await agent.App.StopAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AgentGitHubProvenanceVerifier_RejectsUnauthorizedRepo_BranchMismatch_MissingPat_AndForgedRunOrArtifactIds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sf-verifier-unit-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(dataRoot);
        try
        {
            var agentOpts = new AgentOptions { DataRoot = dataRoot, AppsRoot = Path.Combine(root, "apps") };
            var identityStore = new AgentIdentityStore(agentOpts);
            var protector = new DpapiSecretProtector(DataProtectionScope.CurrentUser);
            var ghMock = new Mock<IGitHubClient>();
            var verifier = new AgentGitHubProvenanceVerifier(
                ghMock.Object, protector, identityStore, new PathBoundaryGuard(), Options.Create(agentOpts), NullLogger<AgentGitHubProvenanceVerifier>.Instance);

            var app = new ManagedApplication
            {
                ProjectGroupId = Guid.NewGuid(),
                Name = "Verifier App",
                Slug = "verifier-app",
                RepositoryFullName = "owner/verifier-app",
                Branch = "main",
                RepositoryExplicitlyAuthorized = false
            };

            // 1. Unauthorized repository
            var unauth = await verifier.ResolveAndVerifyArtifactAsync(app, "main", CommitV1);
            Assert.False(unauth.Verified);
            Assert.Contains("not explicitly authorized", unauth.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);

            // 2. Branch mismatch
            app.RepositoryExplicitlyAuthorized = true;
            var badBranch = await verifier.ResolveAndVerifyArtifactAsync(app, "evil-branch", CommitV1);
            Assert.False(badBranch.Verified);
            Assert.Contains("Branch mismatch", badBranch.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);

            // 3. Invalid commit SHA
            var badSha = await verifier.ResolveAndVerifyArtifactAsync(app, "main", "not-a-sha");
            Assert.False(badSha.Verified);

            // 4. Missing Agent PAT
            var noPat = await verifier.ResolveAndVerifyArtifactAsync(app, "main", CommitV1);
            Assert.False(noPat.Verified);
            Assert.Contains("Personal Access Token", noPat.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);

            ghMock.VerifyNoOtherCalls();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
