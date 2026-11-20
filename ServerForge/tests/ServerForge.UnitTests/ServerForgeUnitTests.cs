using System.IO.Compression;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.Core.StateMachines;
using ServerForge.Core.Validation;
using ServerForge.Databases.PostgreSql;
using ServerForge.Databases.SqlServer;
using ServerForge.Deployments.Adapters;
using ServerForge.Deployments.Engine;
using ServerForge.GitHub.Client;
using ServerForge.IIS.Management;
using ServerForge.Infrastructure.Persistence;
using ServerForge.Security.Certificates;
using ServerForge.Security.Execution;
using ServerForge.Security.Secrets;
using Xunit;

namespace ServerForge.UnitTests;

public sealed class SecurityAndValidationTests
{
    [Fact]
    public void OneTimePairingToken_ValidatesOnce_AndRejectsReplayOrExpiry()
    {
        var service = new OneTimePairingTokenService();
        var (code, expires) = service.GeneratePairingCode(TimeSpan.FromMinutes(10));

        Assert.True(service.TryValidateAndConsume(code, expires.AddMinutes(-1)));
        Assert.False(service.TryValidateAndConsume(code, expires.AddMinutes(-1)));

        var (code2, expires2) = service.GeneratePairingCode(TimeSpan.FromMinutes(1));
        Assert.False(service.TryValidateAndConsume(code2, expires2.AddSeconds(1)));
    }

    [Fact]
    public void MutualTlsThumbprintVerification_FailsClosedOnMismatch()
    {
        using var cert = MutualTlsCertificateManager.CreateSelfSignedIdentityCertificate("desktop-test", isServerCertificate: false);
        Assert.True(MutualTlsCertificateManager.VerifyPinnedCertificateThumbprint(cert, cert.Thumbprint));
        Assert.False(MutualTlsCertificateManager.VerifyPinnedCertificateThumbprint(cert, "DEADBEEF00000000000000000000000000000000"));
        Assert.False(MutualTlsCertificateManager.VerifyPinnedCertificateThumbprint(null, cert.Thumbprint));
    }

    [Fact]
    public void PathBoundaryGuard_BlocksTraversalEscape()
    {
        var guard = new PathBoundaryGuard();
        var root = Path.Combine(Path.GetTempPath(), "sf-boundary-test");
        var safe = guard.EnsureWithinRoot(root, Path.Combine("releases", "20261119-abc"));
        Assert.StartsWith(Path.GetFullPath(root), safe, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<UnauthorizedAccessException>(() =>
            guard.EnsureWithinRoot(root, @"..\..\Windows\System32\cmd.exe"));
    }

    [Fact]
    public void SecretRedactor_ScrubsGitHubTokensPasswordsAndKnownSecrets()
    {
        var protector = new DpapiSecretProtector();
        var raw = "Connecting with github_pat_11ABCDEF12345678901234567890 and Password=SuperSecret99; custom=my_api_key_42";
        var cleaned = protector.RedactSecrets(raw, ["my_api_key_42"]);

        Assert.DoesNotContain("github_pat_", cleaned);
        Assert.DoesNotContain("SuperSecret99", cleaned);
        Assert.DoesNotContain("my_api_key_42", cleaned);
        Assert.Contains("[REDACTED]", cleaned);
    }

    [Fact]
    public void DomainValidator_AcceptsFqdnAndLocalhost_RejectsProtocolsAndRawIps()
    {
        Assert.True(DomainValidator.ValidateHostname("api.contoso.com").IsValid);
        Assert.True(DomainValidator.ValidateHostname("app.local").IsValid);
        Assert.False(DomainValidator.ValidateHostname("https://api.contoso.com").IsValid);
        Assert.False(DomainValidator.ValidateHostname("192.168.1.50").IsValid);
    }

    [Fact]
    public void ApplicationConfigValidator_RequiresExplicitRepoAuthAndProtectsUnmanagedClassicAsp()
    {
        var app = new ManagedApplication
        {
            Name = "Legacy Portal",
            Slug = "legacy-portal",
            RuntimeType = ApplicationRuntimeType.ClassicAsp,
            ManagementState = ResourceManagementState.ReadOnlyUnmanaged,
            RepositoryFullName = "owner/legacy",
            Branch = "main",
            WorkingDirectory = ".",
            AutoDeployEnabled = true,
            RepositoryExplicitlyAuthorized = false
        };

        var result = ApplicationConfigValidator.Validate(app);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("explicit repository authorization", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, e => e.Contains("Classic ASP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DotEnvParser_ExportsSecretsRedactedByDefault()
    {
        var parsed = EnvironmentFileParser.ParseDotEnv("PORT=3000\nAPI_KEY=\"secret_123\"\n# comment\nNODE_ENV=production");
        Assert.Equal("3000", parsed["PORT"]);
        Assert.Equal("secret_123", parsed["API_KEY"]);

        var exported = EnvironmentFileParser.ExportDotEnvWithoutSecrets([
            new EnvironmentVariableRecord { Key = "PORT", IsSecret = false, ProtectedValue = "3000" },
            new EnvironmentVariableRecord { Key = "API_KEY", IsSecret = true, ProtectedValue = "encrypted-blob" }
        ]);
        Assert.Contains("PORT=3000", exported);
        Assert.Contains("API_KEY=<REDACTED_SECRET>", exported);
        Assert.DoesNotContain("encrypted-blob", exported);
    }
}

public sealed class IisAndDatabaseSafetyTests
{
    [Fact]
    public void IisSafetyGuard_BlocksMutationOfPreExistingUnmanagedSite()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            IisAdministrationService.EnsureMutationAuthorized("Default Web Site", ResourceManagementState.ReadOnlyUnmanaged));
    }

    [Fact]
    public void IisConflictDetector_DetectsDuplicateHostnameAndPort()
    {
        var discovered = new[]
        {
            new DiscoveredIisSiteDto(1, "LegacyBilling", "Started", @"C:\inetpub\billing", "LegacyPool",
                ["http://*:80:billing.example.com"], true, ResourceManagementState.ReadOnlyUnmanaged)
        };

        var conflicts = IisAdministrationService.FindBindingConflicts(discovered, "billing.example.com", 80, "NewApp");
        Assert.Single(conflicts);
        Assert.Contains("LegacyBilling", conflicts[0]);
    }

    [Fact]
    public void SqlAndPostgresIdentifierValidation_BlocksInjectionCharacters()
    {
        SqlServerManagementService.ValidateIdentifier("App_Prod_2026", "db");
        PostgreSqlManagementService.ValidateIdentifier("app_prod_2026", "db");

        Assert.Throws<ArgumentException>(() => SqlServerManagementService.ValidateIdentifier("db; DROP TABLE users;--", "db"));
        Assert.Throws<ArgumentException>(() => PostgreSqlManagementService.ValidateIdentifier("db\"; DROP DATABASE x;--", "db"));
    }

    [Fact]
    public async Task SqlServerRestore_RequiresExplicitOperatorApproval()
    {
        var svc = new SqlServerManagementService(NullLogger<SqlServerManagementService>.Instance);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.RestoreDatabaseAsync("Server=localhost;", "OrdersDb", "backup.bak", explicitOperatorApproval: false, CancellationToken.None));
    }
}

public sealed class DeploymentAndPersistenceTests
{
    private const string Commit1 = "1111111111111111111111111111111111111111";
    private const string Commit2 = "2222222222222222222222222222222222222222";
    private const string Commit3 = "3333333333333333333333333333333333333333";

    [Fact]
    public void DeploymentLifecycleStateMachine_PreventsIllegalJumps()
    {
        Assert.True(DeploymentLifecycleStateMachine.CanTransition(DeploymentState.Queued, DeploymentState.Fetching));
        Assert.False(DeploymentLifecycleStateMachine.CanTransition(DeploymentState.Queued, DeploymentState.Successful));
        Assert.True(DeploymentLifecycleStateMachine.IsTerminal(DeploymentState.RolledBack));
    }

    [Fact]
    public async Task DeploymentOrchestrator_FailsWhenArtifactMissingOrUntrusted()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-missing-art-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var iisMock = new Mock<IIisManager>();
            var adapter = new StaticSiteDeploymentAdapter(ApplicationRuntimeType.StaticHtml, iisMock.Object);
            var okHttp = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var orchestrator = new DeploymentOrchestrator(
                [adapter], new PathBoundaryGuard(), new DpapiSecretProtector(), okHttp, NullLogger<DeploymentOrchestrator>.Instance);

            var app = CreateStaticApp(tempRoot);

            // Problem C: missing artifact MUST fail, never succeed
            var missingRes = await orchestrator.EnqueueAndExecuteAsync(
                app,
                new TriggerDeploymentRequest(app.Id, Commit1, "main", "Missing art", "dev", null, null, false, "test"),
                new Dictionary<string, string>());

            Assert.Equal(DeploymentState.Failed, missingRes.State);
            Assert.Contains("missing", missingRes.ErrorSummary ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Null(app.ActiveReleasePath);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DeploymentOrchestrator_HandlesFirstDeploy_SecondDeploy_FailedUpdate_AndRollback()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-durability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var zipPath = Path.Combine(tempRoot, "valid-static.zip");
        CreateSampleStaticZip(zipPath, "<html><body>v1</body></html>");
        var trustedDigest = await SafeProcessRunner.ComputeFileSha256Async(zipPath);

        try
        {
            var iisMock = new Mock<IIisManager>();
            var activeIisPath = string.Empty;
            iisMock.Setup(m => m.EnsureManagedSiteAndPoolAsync(It.IsAny<ManagedApplication>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            iisMock.Setup(m => m.SwitchSitePhysicalPathAsync(It.IsAny<ManagedApplication>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<ManagedApplication, string, CancellationToken>((_, path, _) => activeIisPath = path)
                .Returns(Task.CompletedTask);

            var healthStatus = HttpStatusCode.OK;
            var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(healthStatus)));
            var adapter = new StaticSiteDeploymentAdapter(ApplicationRuntimeType.StaticHtml, iisMock.Object);
            var orchestrator = new DeploymentOrchestrator(
                [adapter], new PathBoundaryGuard(), new DpapiSecretProtector(), http, NullLogger<DeploymentOrchestrator>.Instance);

            var app = CreateStaticApp(tempRoot);

            // 1. First deployment succeeds
            var first = await orchestrator.EnqueueAndExecuteAsync(
                app,
                new TriggerDeploymentRequest(app.Id, Commit1, "main", "v1", "dev", zipPath, trustedDigest, false, "test", 1001, 5001, "rel-1", "GitHubActions"),
                new Dictionary<string, string>());
            Assert.Equal(DeploymentState.Successful, first.State);
            var release1 = app.ActiveReleasePath!;
            Assert.Equal(release1, app.LastKnownGoodReleasePath);

            // 2. Second deployment succeeds & updates LKG -> release1
            await Task.Delay(1100);
            var second = await orchestrator.EnqueueAndExecuteAsync(
                app,
                new TriggerDeploymentRequest(app.Id, Commit2, "main", "v2", "dev", zipPath, trustedDigest, false, "test", 1002, 5002, "rel-2", "GitHubActions"),
                new Dictionary<string, string>());
            Assert.Equal(DeploymentState.Successful, second.State);
            var release2 = app.ActiveReleasePath!;
            Assert.NotEqual(release1, release2);
            Assert.Equal(release1, app.LastKnownGoodReleasePath);

            // 3. Third deployment fails health check -> rolls back cleanly to release2
            healthStatus = HttpStatusCode.ServiceUnavailable;
            var third = await orchestrator.EnqueueAndExecuteAsync(
                app,
                new TriggerDeploymentRequest(app.Id, Commit3, "main", "v3 broken", "dev", zipPath, trustedDigest, false, "test", 1003, 5003, "rel-3", "GitHubActions"),
                new Dictionary<string, string>());
            Assert.Equal(DeploymentState.RolledBack, third.State);
            Assert.Equal(release2, app.ActiveReleasePath);
            Assert.Equal(release2, activeIisPath);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteStore_PersistsPairedServer_RecoversInterruptedDeploys_AndRemovesManagementOnly()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), $"sf-sqlite-{Guid.NewGuid():N}.db");
        try
        {
            var opts = new DbContextOptionsBuilder<ServerForgeDbContext>()
                .UseSqlite($"Data Source={dbFile}")
                .Options;

            await using (var init = new ServerForgeDbContext(opts))
                await init.InitializeSqliteAsync();

            var factoryMock = new Mock<IDbContextFactory<ServerForgeDbContext>>();
            factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ServerForgeDbContext(opts));

            var store = new ServerForgeStore(factoryMock.Object, new DpapiSecretProtector());

            // Problem E: Pairing persistence across restarts
            await store.UpsertServerAsync(new ManagedServer
            {
                AgentId = "agent-prod-01",
                DisplayName = "Prod Win2022",
                HostAddress = "10.10.0.5",
                AgentPort = 9443,
                PinnedServerCertThumbprint = "AABBCCDD",
                Status = ServerConnectionStatus.Connected
            }, "unit-test");

            var servers = await store.GetServersAsync();
            Assert.Single(servers);
            Assert.Equal("agent-prod-01", servers[0].AgentId);

            // Problem G: Interrupted deployment crash recovery
            var app = new ManagedApplication
            {
                Name = "Orders API",
                Slug = "orders-api",
                RuntimeType = ApplicationRuntimeType.NestJsApi,
                RepositoryFullName = "owner/orders-api",
                RepositoryExplicitlyAuthorized = true,
                InternalPort = 4010
            };
            await store.UpsertApplicationAsync(app, "unit-test");

            await store.RecordDeploymentAsync(new DeploymentRecord
            {
                ApplicationId = app.Id,
                CommitSha = Commit1,
                Branch = "main",
                State = DeploymentState.Deploying
            });

            var recoveredCount = await store.RecoverInterruptedDeploymentsAsync();
            Assert.Equal(1, recoveredCount);

            var history = await store.GetDeploymentHistoryAsync(app.Id);
            Assert.Equal(DeploymentState.Failed, history[0].State);
            Assert.Contains("interrupted", history[0].ErrorSummary ?? "", StringComparison.OrdinalIgnoreCase);

            // Problem D: Remove from management preserves files & records audit
            Assert.True(await store.RemoveApplicationFromManagementAsync(app.Id, "unit-test"));
            Assert.Empty(await store.GetApplicationsAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(dbFile)) File.Delete(dbFile);
        }
    }

    [Fact]
    public async Task GitHubApiClient_RespectsConditionalETag304NotModified()
    {
        var handler = new StubHttpHandler(req =>
        {
            if (req.Headers.IfNoneMatch.Any(t => t.Tag == "\"etag-v1\""))
            {
                var notMod = new HttpResponseMessage(HttpStatusCode.NotModified);
                notMod.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-v1\"");
                notMod.Headers.Add("X-RateLimit-Remaining", "4999");
                return notMod;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var client = new GitHubApiClient(new HttpClient(handler), NullLogger<GitHubApiClient>.Instance);
        var check = await client.CheckBranchHeadConditionalAsync("pat_test", "owner/repo", "main", "\"etag-v1\"", CancellationToken.None);

        Assert.False(check.Modified);
        Assert.Equal(4999, check.RateLimitRemaining);
    }

    private static ManagedApplication CreateStaticApp(string tempRoot) => new()
    {
        Name = "Marketing Static",
        Slug = "marketing-static",
        RuntimeType = ApplicationRuntimeType.StaticHtml,
        ManagementState = ResourceManagementState.ServerForgeCreated,
        SourceMode = DeploymentSourceMode.GitHubActionsArtifact,
        RepositoryFullName = "owner/marketing",
        RepositoryExplicitlyAuthorized = true,
        Branch = "main",
        PrimaryDomain = "marketing.local",
        IisSiteName = "SF-Marketing",
        IisAppPoolName = "SF-Marketing-Pool",
        ReleasesRootPath = tempRoot,
        PersistentDataPath = Path.Combine(tempRoot, "data"),
        HealthCheckPath = "/health",
        HealthCheckTimeoutSeconds = 3,
        AutoRollbackOnFailure = true
    };

    private static void CreateSampleStaticZip(string zipPath, string indexHtml)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("index.html");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(indexHtml);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
