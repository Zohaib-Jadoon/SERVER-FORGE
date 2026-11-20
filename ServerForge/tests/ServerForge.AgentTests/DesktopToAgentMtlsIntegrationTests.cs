using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ServerForge.Agent;
using ServerForge.Agent.Configuration;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Desktop.Services;
using ServerForge.Infrastructure.Persistence;
using ServerForge.Security.Certificates;
using ServerForge.Security.Secrets;
using Xunit;

namespace ServerForge.AgentTests;

/// <summary>
/// Live Desktop-to-Agent HTTPS + Mutual-TLS integration tests using real Kestrel loopback servers,
/// real ECDSA P-384 certificates, real thumbprint pinning, real one-time pairing codes, and SQLite persistence.
/// </summary>
public sealed class DesktopToAgentMtlsIntegrationTests
{
    private sealed class TestDbContextFactory(DbContextOptions<ServerForgeDbContext> options)
        : IDbContextFactory<ServerForgeDbContext>
    {
        public ServerForgeDbContext CreateDbContext() => new(options);
    }

    private static async Task<ServerForgeStore> CreateDesktopStoreAsync(string desktopDir)
    {
        var dbPath = Path.Combine(desktopDir, "desktop-test.db");
        var opts = new DbContextOptionsBuilder<ServerForgeDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var factory = new TestDbContextFactory(opts);
        await using (var db = await factory.CreateDbContextAsync())
            await db.InitializeSqliteAsync();
        return new ServerForgeStore(factory, new DpapiSecretProtector(DataProtectionScope.CurrentUser));
    }

    private static async Task<AgentHostHandle> StartLiveAgentAsync(string dataRoot, string appsRoot, int port = 0)
    {
        var handle = await AgentHostFactory.CreateAndInitializeAsync(
            args: [],
            overrideOptions: new AgentOptions
            {
                ServiceName = "ServerForgeAgentTest",
                BindAddress = "127.0.0.1",
                Port = port,
                DataRoot = dataRoot,
                AppsRoot = appsRoot
            },
            enableWindowsService: false,
            enableAutoDeployWorker: false,
            configureSerilog: false);

        await handle.App.StartAsync();
        return handle;
    }

    [Fact]
    public async Task LiveMtls_PairingCode_SucceedsOnce_RejectsReplay_AndRejectsWrongServerThumbprint()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sf-mtls-pair-{Guid.NewGuid():N}");
        var agentData = Path.Combine(root, "agent-data");
        var agentApps = Path.Combine(root, "agent-apps");
        var desktopData = Path.Combine(root, "desktop-data");

        try
        {
            await using var agent = await StartLiveAgentAsync(agentData, agentApps);
            var port = agent.GetBoundPort();
            var pairingCode = agent.InitialPairingCode!;
            var thumbprint = agent.ServerCertificate.Thumbprint;

            Assert.False(string.IsNullOrWhiteSpace(pairingCode));
            Assert.True(File.Exists(Path.Combine(agentData, "pairing-code.txt")));

            var desktopId = new DesktopIdentityService(desktopData);
            var transport = new AgentTransportService(desktopId);

            // 1. Wrong pinned server certificate thumbprint MUST fail closed during TLS handshake
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                transport.PairWithAgentAsync("127.0.0.1", port, pairingCode, "DEADBEEF00001111222233334444555566667777", CancellationToken.None));

            // 2. Wrong pairing code MUST be rejected
            var badCodeResp = await transport.PairWithAgentAsync("127.0.0.1", port, "0000-0000-0000-0000-0000", thumbprint, CancellationToken.None);
            Assert.False(badCodeResp.Accepted);

            // 3. Correct one-time code + pinned thumbprint MUST succeed
            var pairResp = await transport.PairWithAgentAsync("127.0.0.1", port, pairingCode, thumbprint, CancellationToken.None);
            Assert.True(pairResp.Accepted);
            Assert.Equal(agent.IdentityStore.State.AgentId, pairResp.AgentId);
            Assert.True(agent.IdentityStore.State.IsPaired);

            // 4. Replaying the same pairing code MUST be rejected
            var replayResp = await transport.PairWithAgentAsync("127.0.0.1", port, pairingCode, thumbprint, CancellationToken.None);
            Assert.False(replayResp.Accepted);

            await agent.App.StopAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LiveMtls_AuthenticatedRequests_Succeed_AndRogueOrAnonymousClientRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sf-mtls-auth-{Guid.NewGuid():N}");
        var agentData = Path.Combine(root, "agent-data");
        var agentApps = Path.Combine(root, "agent-apps");
        var desktopData = Path.Combine(root, "desktop-data");
        var rogueData = Path.Combine(root, "rogue-desktop-data");

        try
        {
            await using var agent = await StartLiveAgentAsync(agentData, agentApps);
            var port = agent.GetBoundPort();
            var thumbprint = agent.ServerCertificate.Thumbprint;

            var desktopId = new DesktopIdentityService(desktopData);
            var transport = new AgentTransportService(desktopId);

            var pair = await transport.PairWithAgentAsync("127.0.0.1", port, agent.InitialPairingCode!, thumbprint, CancellationToken.None);
            Assert.True(pair.Accepted);

            var server = new ManagedServer
            {
                AgentId = pair.AgentId,
                DisplayName = "Loopback Test Agent",
                HostAddress = "127.0.0.1",
                AgentPort = port,
                PinnedServerCertThumbprint = pair.AgentCertificateThumbprint,
                ClientCertThumbprint = transport.GetCurrentClientThumbprint(),
                Status = ServerConnectionStatus.Connected
            };

            // 1. Authenticated mTLS calls succeed
            var telemetry = await transport.FetchTelemetryAsync(server, CancellationToken.None);
            Assert.NotNull(telemetry);
            Assert.Equal(pair.AgentId, telemetry!.AgentId);

            var project = new ProjectGroup { Name = "Integration Suite", Slug = "integration-suite", Description = "mTLS verified" };
            await transport.SyncProjectAsync(server, project, CancellationToken.None);

            var registered = await transport.RegisterOrUpdateApplicationAsync(server, new UpsertApplicationRequest(
                Id: null,
                ProjectGroupId: project.Id,
                Name: "Sample API",
                Slug: "sample-api",
                RuntimeType: ApplicationRuntimeType.AspNetCore,
                RepositoryFullName: "owner/sample-api",
                Branch: "main",
                WorkingDirectory: ".",
                AutoDeployEnabled: false,
                RepositoryExplicitlyAuthorized: true,
                SourceMode: DeploymentSourceMode.GitHubActionsArtifact,
                GitHubWorkflowFileName: "deploy.yml",
                BuildCommand: "",
                StartCommand: "",
                RuntimeVersion: "10.0",
                InternalPort: 5100,
                IisSiteName: "SF-sample-api",
                IisAppPoolName: "SF-sample-api-Pool",
                WindowsServiceName: "SF_sample_api",
                PrimaryDomain: "sample.local",
                HealthCheckPath: "/health",
                HealthCheckTimeoutSeconds: 30,
                RetainHealthyReleasesCount: 5,
                AutoRollbackOnFailure: true), CancellationToken.None);

            Assert.NotNull(registered);
            Assert.Equal("sample-api", registered!.Slug);

            var auditLogs = await transport.FetchAuditLogsAsync(server, CancellationToken.None);
            Assert.NotEmpty(auditLogs);

            // 2. Rogue Desktop with a different client certificate MUST be rejected (HTTP 403/401)
            var rogueTransport = new AgentTransportService(new DesktopIdentityService(rogueData));
            var rogueEx = await Assert.ThrowsAsync<HttpRequestException>(() =>
                rogueTransport.FetchTelemetryAsync(server, CancellationToken.None));
            Assert.True(
                rogueEx.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
                $"Expected 403 or 401 for rogue client cert, got {rogueEx.StatusCode}.");

            // 3. Anonymous client without any client certificate MUST be rejected
            using var anonHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                    cert is not null && MutualTlsCertificateManager.VerifyPinnedCertificateThumbprint(cert, thumbprint)
            };
            using var anonHttp = new HttpClient(anonHandler) { BaseAddress = new Uri($"https://127.0.0.1:{port}/") };
            using var anonResp = await anonHttp.GetAsync("api/v1/telemetry");
            Assert.True(
                anonResp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
                $"Expected 403 or 401 for anonymous request, got {anonResp.StatusCode}.");

            await agent.App.StopAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LiveMtls_PersistsAcrossAgentAndDesktopRestarts_AndRecoversAfterConnectionLoss()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sf-mtls-restart-{Guid.NewGuid():N}");
        var agentData = Path.Combine(root, "agent-data");
        var agentApps = Path.Combine(root, "agent-apps");
        var desktopData = Path.Combine(root, "desktop-data");

        try
        {
            int boundPort;
            string agentId;

            // Phase 1: Pair initial Agent and persist server record to Desktop SQLite
            await using (var agent1 = await StartLiveAgentAsync(agentData, agentApps, port: 0))
            {
                boundPort = agent1.GetBoundPort();
                var desktopStore1 = await CreateDesktopStoreAsync(desktopData);
                var transport1 = new AgentTransportService(new DesktopIdentityService(desktopData));

                var pair = await transport1.PairWithAgentAsync(
                    "127.0.0.1", boundPort, agent1.InitialPairingCode!, agent1.ServerCertificate.Thumbprint, CancellationToken.None);
                Assert.True(pair.Accepted);
                agentId = pair.AgentId;

                await desktopStore1.UpsertServerAsync(new ManagedServer
                {
                    AgentId = pair.AgentId,
                    DisplayName = "Restart Test Server",
                    HostAddress = "127.0.0.1",
                    AgentPort = boundPort,
                    PinnedServerCertThumbprint = pair.AgentCertificateThumbprint,
                    ClientCertThumbprint = transport1.GetCurrentClientThumbprint(),
                    Status = ServerConnectionStatus.Connected,
                    LastSeenUtc = DateTimeOffset.UtcNow
                }, "TestAdmin");

                await agent1.App.StopAsync();
            }

            // Phase 2: Simulate Desktop restart while Agent is OFFLINE (connection loss check)
            var desktopStore2 = await CreateDesktopStoreAsync(desktopData);
            var persistedServers = await desktopStore2.GetServersAsync();
            var reloadedServer = Assert.Single(persistedServers);
            Assert.Equal(agentId, reloadedServer.AgentId);

            var transport2 = new AgentTransportService(new DesktopIdentityService(desktopData));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                transport2.FetchTelemetryAsync(reloadedServer, CancellationToken.None));

            // Phase 3: Restart Agent on the SAME DataRoot and SAME port — Desktop MUST reconnect without re-pairing
            await using (var agent2 = await StartLiveAgentAsync(agentData, agentApps, port: boundPort))
            {
                Assert.True(agent2.IdentityStore.State.IsPaired);
                Assert.Equal(agentId, agent2.IdentityStore.State.AgentId);
                Assert.Null(agent2.InitialPairingCode); // Already paired — does not emit unauthenticated pairing code

                var telemetry = await transport2.FetchTelemetryAsync(reloadedServer, CancellationToken.None);
                Assert.NotNull(telemetry);
                Assert.Equal(agentId, telemetry!.AgentId);

                await agent2.App.StopAsync();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LiveMtls_CertificateRotation_InvalidatesOldCert_AndAuthenticatesWithNewCert()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sf-mtls-rotate-{Guid.NewGuid():N}");
        var agentData = Path.Combine(root, "agent-data");
        var agentApps = Path.Combine(root, "agent-apps");
        var desktopData = Path.Combine(root, "desktop-data");
        var oldDesktopSnapshotDir = Path.Combine(root, "old-desktop-snapshot");

        try
        {
            await using var agent = await StartLiveAgentAsync(agentData, agentApps);
            var port = agent.GetBoundPort();
            var thumbprint = agent.ServerCertificate.Thumbprint;

            var desktopId = new DesktopIdentityService(desktopData);
            var transport = new AgentTransportService(desktopId);

            var pair = await transport.PairWithAgentAsync("127.0.0.1", port, agent.InitialPairingCode!, thumbprint, CancellationToken.None);
            Assert.True(pair.Accepted);

            var oldThumbprint = transport.GetCurrentClientThumbprint();
            var server = new ManagedServer
            {
                AgentId = pair.AgentId,
                DisplayName = "Rotation Test Server",
                HostAddress = "127.0.0.1",
                AgentPort = port,
                PinnedServerCertThumbprint = pair.AgentCertificateThumbprint,
                ClientCertThumbprint = oldThumbprint
            };

            // Copy old client cert/id to snapshot folder before rotation
            Directory.CreateDirectory(oldDesktopSnapshotDir);
            File.Copy(Path.Combine(desktopData, "desktop-client.pfx"), Path.Combine(oldDesktopSnapshotDir, "desktop-client.pfx"));
            File.Copy(Path.Combine(desktopData, "desktop-instance-id.txt"), Path.Combine(oldDesktopSnapshotDir, "desktop-instance-id.txt"));

            // Perform live mTLS certificate rotation
            var rotResp = await transport.RotateDesktopCertificateAsync(server, "Scheduled security rotation", CancellationToken.None);
            Assert.True(rotResp.Rotated);
            Assert.NotEqual(oldThumbprint, rotResp.NewPinnedThumbprint);
            Assert.Equal(rotResp.NewPinnedThumbprint, transport.GetCurrentClientThumbprint());

            // Requests with NEW rotated certificate succeed
            var telemetry = await transport.FetchTelemetryAsync(server, CancellationToken.None);
            Assert.NotNull(telemetry);

            // Requests with OLD revoked certificate MUST be rejected
            var oldTransport = new AgentTransportService(new DesktopIdentityService(oldDesktopSnapshotDir));
            var oldEx = await Assert.ThrowsAsync<HttpRequestException>(() =>
                oldTransport.FetchTelemetryAsync(server, CancellationToken.None));
            Assert.True(oldEx.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);

            await agent.App.StopAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LiveMtls_MultipleServers_PairPersistAndCommunicateIndependently()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sf-mtls-multi-{Guid.NewGuid():N}");
        var desktopData = Path.Combine(root, "desktop-data");

        try
        {
            await using var agentA = await StartLiveAgentAsync(Path.Combine(root, "agent-a-data"), Path.Combine(root, "agent-a-apps"));
            await using var agentB = await StartLiveAgentAsync(Path.Combine(root, "agent-b-data"), Path.Combine(root, "agent-b-apps"));

            var portA = agentA.GetBoundPort();
            var portB = agentB.GetBoundPort();
            Assert.NotEqual(portA, portB);

            var store = await CreateDesktopStoreAsync(desktopData);
            var transport = new AgentTransportService(new DesktopIdentityService(desktopData));

            var pairA = await transport.PairWithAgentAsync("127.0.0.1", portA, agentA.InitialPairingCode!, agentA.ServerCertificate.Thumbprint, CancellationToken.None);
            var pairB = await transport.PairWithAgentAsync("127.0.0.1", portB, agentB.InitialPairingCode!, agentB.ServerCertificate.Thumbprint, CancellationToken.None);
            Assert.True(pairA.Accepted);
            Assert.True(pairB.Accepted);
            Assert.NotEqual(pairA.AgentId, pairB.AgentId);

            var srvA = new ManagedServer
            {
                AgentId = pairA.AgentId,
                DisplayName = "Server A",
                HostAddress = "127.0.0.1",
                AgentPort = portA,
                PinnedServerCertThumbprint = pairA.AgentCertificateThumbprint,
                ClientCertThumbprint = transport.GetCurrentClientThumbprint()
            };
            var srvB = new ManagedServer
            {
                AgentId = pairB.AgentId,
                DisplayName = "Server B",
                HostAddress = "127.0.0.1",
                AgentPort = portB,
                PinnedServerCertThumbprint = pairB.AgentCertificateThumbprint,
                ClientCertThumbprint = transport.GetCurrentClientThumbprint()
            };

            await store.UpsertServerAsync(srvA, "Admin");
            await store.UpsertServerAsync(srvB, "Admin");

            var saved = await store.GetServersAsync();
            Assert.Equal(2, saved.Count);

            var telA = await transport.FetchTelemetryAsync(srvA, CancellationToken.None);
            var telB = await transport.FetchTelemetryAsync(srvB, CancellationToken.None);
            Assert.Equal(pairA.AgentId, telA!.AgentId);
            Assert.Equal(pairB.AgentId, telB!.AgentId);

            await agentA.App.StopAsync();
            await agentB.App.StopAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
