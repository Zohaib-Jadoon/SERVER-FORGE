using System.IO.Compression;
using System.Net;
using System.Text.Json;
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
            ProjectGroupId = Guid.NewGuid(),
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
    public void ApplicationConfigValidator_RejectsEmptyProjectGroupId()
    {
        var app = new ManagedApplication
        {
            ProjectGroupId = Guid.Empty,
            Name = "Orders API",
            Slug = "orders-api",
            RuntimeType = ApplicationRuntimeType.NestJsApi,
            RepositoryFullName = "owner/orders-api",
            Branch = "main",
            WorkingDirectory = ".",
            InternalPort = 4010,
            RepositoryExplicitlyAuthorized = true,
            AutoDeployEnabled = true
        };

        var result = ApplicationConfigValidator.Validate(app);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ProjectGroupId", StringComparison.OrdinalIgnoreCase));
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

            // 1. GitHubActionsArtifact without a server-side IGitHubArtifactProvenanceVerifier MUST fail closed
            var ghApp = CreateStaticApp(tempRoot);
            var unverifiedGhRes = await orchestrator.EnqueueAndExecuteAsync(
                ghApp,
                new TriggerDeploymentRequest(ghApp.Id, Commit1, "main", "Missing verifier", "dev", null, null, false, "test"),
                new Dictionary<string, string>());
            Assert.Equal(DeploymentState.Failed, unverifiedGhRes.State);
            Assert.Contains("IGitHubArtifactProvenanceVerifier", unverifiedGhRes.ErrorSummary ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Null(ghApp.ActiveReleasePath);

            // 2. ManualSignedPackage with missing local archive MUST fail closed, never succeed
            var manualApp = CreateStaticApp(tempRoot);
            manualApp.SourceMode = DeploymentSourceMode.ManualSignedPackage;
            var missingRes = await orchestrator.EnqueueAndExecuteAsync(
                manualApp,
                new TriggerDeploymentRequest(manualApp.Id, Commit1, "main", "Missing art", "dev", null, null, false, "test"),
                new Dictionary<string, string>());

            Assert.Equal(DeploymentState.Failed, missingRes.State);
            Assert.Contains("ManualSignedPackage", missingRes.ErrorSummary ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Null(manualApp.ActiveReleasePath);
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

            var provenanceMock = new Mock<IGitHubArtifactProvenanceVerifier>();
            provenanceMock.Setup(v => v.ResolveAndVerifyArtifactAsync(
                    It.IsAny<ManagedApplication>(), "main", It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((ManagedApplication _, string _, string sha, long? reqRun, long? reqArt, CancellationToken _) =>
                    new VerifiedGitHubArtifact(
                        Verified: true,
                        LocalZipPath: zipPath,
                        TrustedSha256: trustedDigest,
                        WorkflowRunId: reqRun ?? 1001,
                        ArtifactId: reqArt ?? 5001,
                        ArtifactName: $"rel-{sha[..6]}",
                        TrustSource: $"GitHubActionsRun:{reqRun ?? 1001}/Artifact:{reqArt ?? 5001}",
                        FailureReason: null));

            var healthStatus = HttpStatusCode.OK;
            var http = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(healthStatus)));
            var adapter = new StaticSiteDeploymentAdapter(ApplicationRuntimeType.StaticHtml, iisMock.Object);
            var orchestrator = new DeploymentOrchestrator(
                [adapter], new PathBoundaryGuard(), new DpapiSecretProtector(), http, NullLogger<DeploymentOrchestrator>.Instance,
                provenanceVerifier: provenanceMock.Object);

            var app = CreateStaticApp(tempRoot);

            // 1. First deployment succeeds via server-side provenance verifier (no caller-supplied local path)
            var first = await orchestrator.EnqueueAndExecuteAsync(
                app,
                new TriggerDeploymentRequest(app.Id, Commit1, "main", "v1", "dev", null, trustedDigest, false, "test", 1001, 5001, "rel-1", "GitHubActions"),
                new Dictionary<string, string>());
            Assert.Equal(DeploymentState.Successful, first.State);
            var release1 = app.ActiveReleasePath!;
            Assert.Equal(release1, app.LastKnownGoodReleasePath);

            // 2. Second deployment succeeds & updates LKG -> release1
            await Task.Delay(1100);
            var second = await orchestrator.EnqueueAndExecuteAsync(
                app,
                new TriggerDeploymentRequest(app.Id, Commit2, "main", "v2", "dev", null, trustedDigest, false, "test", 1002, 5002, "rel-2", "GitHubActions"),
                new Dictionary<string, string>());
            Assert.Equal(DeploymentState.Successful, second.State);
            var release2 = app.ActiveReleasePath!;
            Assert.NotEqual(release1, release2);
            Assert.Equal(release1, app.LastKnownGoodReleasePath);

            // 3. Third deployment fails health check -> rolls back cleanly to release2
            healthStatus = HttpStatusCode.ServiceUnavailable;
            var third = await orchestrator.EnqueueAndExecuteAsync(
                app,
                new TriggerDeploymentRequest(app.Id, Commit3, "main", "v3 broken", "dev", null, trustedDigest, false, "test", 1003, 5003, "rel-3", "GitHubActions"),
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
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-sqlite-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbFile = Path.Combine(tempRoot, "serverforge.db");
        var hostedReleaseDir = Path.Combine(tempRoot, "hosted-apps", "orders-api", "releases", "r1");
        Directory.CreateDirectory(hostedReleaseDir);
        var hostedMarkerFile = Path.Combine(hostedReleaseDir, "index.js");
        await File.WriteAllTextAsync(hostedMarkerFile, "// simulated hosted release file");

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

            // Verify SQLite foreign-key integrity remains enabled on connections
            await using (var fkCheckDb = new ServerForgeDbContext(opts))
            {
                await fkCheckDb.Database.OpenConnectionAsync();
                await using var cmd = fkCheckDb.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "PRAGMA foreign_keys;";
                var fkEnabled = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                Assert.Equal(1L, fkEnabled);
            }

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

            // Create and persist a valid ProjectGroup before registering applications
            var project = new ProjectGroup
            {
                Name = "Commerce Platform",
                Slug = "commerce-platform",
                Description = "Relational fixture project group"
            };
            await store.UpsertProjectAsync(project, "unit-test");

            // Problem G: Interrupted deployment crash recovery
            var app = new ManagedApplication
            {
                ProjectGroupId = project.Id,
                Name = "Orders API",
                Slug = "orders-api",
                RuntimeType = ApplicationRuntimeType.NestJsApi,
                RepositoryFullName = "owner/orders-api",
                RepositoryExplicitlyAuthorized = true,
                InternalPort = 4010,
                ReleasesRootPath = Path.Combine(tempRoot, "hosted-apps", "orders-api"),
                ActiveReleasePath = hostedReleaseDir
            };
            await store.UpsertApplicationAsync(app, "unit-test");

            // Updating an existing application preserves identity and persists changes
            app.Branch = "release";
            app.InternalPort = 4011;
            await store.UpsertApplicationAsync(app, "unit-test");
            var reloadedApp = await store.GetApplicationByIdAsync(app.Id);
            Assert.NotNull(reloadedApp);
            Assert.Equal("release", reloadedApp!.Branch);
            Assert.Equal(4011, reloadedApp.InternalPort);

            await store.RecordDeploymentAsync(new DeploymentRecord
            {
                ApplicationId = app.Id,
                CommitSha = Commit1,
                Branch = "release",
                State = DeploymentState.Deploying
            });

            var recoveredCount = await store.RecoverInterruptedDeploymentsAsync();
            Assert.Equal(1, recoveredCount);

            var history = await store.GetDeploymentHistoryAsync(app.Id);
            Assert.Single(history);
            Assert.Equal(DeploymentState.Failed, history[0].State);
            Assert.Contains("interrupted", history[0].ErrorSummary ?? "", StringComparison.OrdinalIgnoreCase);

            // Problem D: Remove from management preserves hosted files on disk & records audit
            Assert.True(await store.RemoveApplicationFromManagementAsync(app.Id, "unit-test"));
            Assert.Empty(await store.GetApplicationsAsync());
            Assert.True(File.Exists(hostedMarkerFile), "Removing an app from management must NEVER delete hosted files.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteStore_EnforcesProjectGroupForeignKeys_AndRejectsInvalidDefaultGuids()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-fk-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbFile = Path.Combine(tempRoot, "fk-test.db");

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

            // 1. Guid.Empty ProjectGroupId must be rejected cleanly
            var emptyProjectApp = new ManagedApplication
            {
                ProjectGroupId = Guid.Empty,
                Name = "Orphan App",
                Slug = "orphan-app",
                RuntimeType = ApplicationRuntimeType.StaticHtml,
                RepositoryFullName = "owner/orphan"
            };
            await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertApplicationAsync(emptyProjectApp, "unit-test"));

            // 2. Nonexistent ProjectGroupId must be rejected without silently creating duplicate projects
            var missingProjectApp = new ManagedApplication
            {
                ProjectGroupId = Guid.NewGuid(),
                Name = "Unlinked App",
                Slug = "unlinked-app",
                RuntimeType = ApplicationRuntimeType.StaticHtml,
                RepositoryFullName = "owner/unlinked"
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertApplicationAsync(missingProjectApp, "unit-test"));
            Assert.Empty(await store.GetProjectsAsync());

            // 3. EnsureDefaultProjectAsync creates exactly one default project idempotently
            var def1 = await store.EnsureDefaultProjectAsync("unit-test");
            var def2 = await store.EnsureDefaultProjectAsync("unit-test");
            Assert.Equal(def1.Id, def2.Id);
            Assert.Single(await store.GetProjectsAsync());

            // 4. Valid app under default project saves cleanly
            missingProjectApp.ProjectGroupId = def1.Id;
            await store.UpsertApplicationAsync(missingProjectApp, "unit-test");
            Assert.Single(await store.GetApplicationsAsync());

            // 5. Direct EF Core insert with nonexistent ProjectGroupId still fails SQLite FK enforcement
            await using (var rawDb = new ServerForgeDbContext(opts))
            {
                rawDb.Applications.Add(new ManagedApplication
                {
                    ProjectGroupId = Guid.NewGuid(),
                    Name = "Direct FK Violation",
                    Slug = "direct-fk-violation",
                    RuntimeType = ApplicationRuntimeType.StaticHtml
                });
                await Assert.ThrowsAsync<DbUpdateException>(() => rawDb.SaveChangesAsync());
            }

            // 6. Other persistence methods reject Guid.Empty foreign keys cleanly
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.SaveEnvironmentVariableAsync(Guid.Empty, EnvironmentTier.Production, "KEY", "VAL", false, false, "unit-test"));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.UpsertDomainAsync(new DomainBindingRecord { ApplicationId = Guid.Empty, Hostname = "app.local" }, "unit-test"));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.RecordDeploymentAsync(new DeploymentRecord { ApplicationId = Guid.Empty, CommitSha = Commit1, Branch = "main" }));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteStore_DeploymentHistory_HandlesEmptySingleMultipleOffsetsSameTimestampPaginationAndReopen()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-deploy-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbFile = Path.Combine(tempRoot, "deploy-history.db");

        try
        {
            static ServerForgeStore CreateStore(string path, out DbContextOptions<ServerForgeDbContext> options)
            {
                options = new DbContextOptionsBuilder<ServerForgeDbContext>()
                    .UseSqlite($"Data Source={path}")
                    .Options;
                var localOpts = options;
                var factoryMock = new Mock<IDbContextFactory<ServerForgeDbContext>>();
                factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => new ServerForgeDbContext(localOpts));
                return new ServerForgeStore(factoryMock.Object, new DpapiSecretProtector());
            }

            var store = CreateStore(dbFile, out var opts);
            await using (var init = new ServerForgeDbContext(opts))
                await init.InitializeSqliteAsync();

            var project = await store.EnsureDefaultProjectAsync("unit-test");
            var app = new ManagedApplication
            {
                ProjectGroupId = project.Id,
                Name = "Billing API",
                Slug = "billing-api",
                RuntimeType = ApplicationRuntimeType.NestJsApi,
                RepositoryFullName = "owner/billing-api",
                RepositoryExplicitlyAuthorized = true,
                InternalPort = 4020
            };
            await store.UpsertApplicationAsync(app, "unit-test");

            // 1. Empty deployment history
            Assert.Empty(await store.GetDeploymentHistoryAsync(app.Id));
            Assert.Empty(await store.GetDeploymentHistoryAsync(app.Id, take: 0));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetDeploymentHistoryAsync(app.Id, take: -1));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetDeploymentHistoryAsync(app.Id, skip: -1, take: 5));

            // 2. Single deployment
            var depEarliest = new DeploymentRecord
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000010"),
                ApplicationId = app.Id,
                CommitSha = Commit1,
                Branch = "main",
                State = DeploymentState.Successful,
                // 2026-11-19T10:00:00Z expressed in UTC+05:30
                QueuedAtUtc = new DateTimeOffset(2026, 11, 19, 15, 30, 0, TimeSpan.FromMinutes(330))
            };
            await store.RecordDeploymentAsync(depEarliest);
            var single = await store.GetDeploymentHistoryAsync(app.Id);
            Assert.Single(single);
            Assert.Equal(depEarliest.Id, single[0].Id);
            Assert.Equal(TimeSpan.Zero, single[0].QueuedAtUtc.Offset);

            // 3. Multiple deployments with different UTC offsets + exact same-timestamp tie-break
            var sameInstantUtc = new DateTimeOffset(2026, 11, 19, 11, 0, 0, TimeSpan.Zero);
            var depTieLow = new DeploymentRecord
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000021"),
                ApplicationId = app.Id,
                CommitSha = Commit2,
                Branch = "main",
                State = DeploymentState.Successful,
                // 2026-11-19T11:00:00Z expressed in UTC-05:00
                QueuedAtUtc = sameInstantUtc.ToOffset(TimeSpan.FromHours(-5))
            };
            var depTieHigh = new DeploymentRecord
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000022"),
                ApplicationId = app.Id,
                CommitSha = Commit2,
                Branch = "main",
                State = DeploymentState.Successful,
                // Same UTC instant expressed in UTC+09:00
                QueuedAtUtc = sameInstantUtc.ToOffset(TimeSpan.FromHours(9))
            };
            var depLatest = new DeploymentRecord
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000030"),
                ApplicationId = app.Id,
                CommitSha = Commit3,
                Branch = "main",
                State = DeploymentState.Successful,
                // 2026-11-19T12:15:30.2500000Z expressed in UTC-07:00
                QueuedAtUtc = new DateTimeOffset(2026, 11, 19, 5, 15, 30, 250, TimeSpan.FromHours(-7))
            };
            var depMiddle = new DeploymentRecord
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000015"),
                ApplicationId = app.Id,
                CommitSha = Commit1,
                Branch = "main",
                State = DeploymentState.Failed,
                // 2026-11-19T10:30:00Z
                QueuedAtUtc = new DateTimeOffset(2026, 11, 19, 10, 30, 0, TimeSpan.Zero)
            };

            // Insert intentionally out of chronological order
            await store.RecordDeploymentAsync(depTieLow);
            await store.RecordDeploymentAsync(depLatest);
            await store.RecordDeploymentAsync(depMiddle);
            await store.RecordDeploymentAsync(depTieHigh);

            var expectedOrder = new[] { depLatest.Id, depTieHigh.Id, depTieLow.Id, depMiddle.Id, depEarliest.Id };

            var all = await store.GetDeploymentHistoryAsync(app.Id, take: 50);
            Assert.Equal(expectedOrder, all.Select(d => d.Id).ToArray());

            // 4. Pagination limits (take & skip/take)
            var topTwo = await store.GetDeploymentHistoryAsync(app.Id, take: 2);
            Assert.Equal(new[] { depLatest.Id, depTieHigh.Id }, topTwo.Select(d => d.Id).ToArray());

            var page2 = await store.GetDeploymentHistoryAsync(app.Id, skip: 2, take: 2);
            Assert.Equal(new[] { depTieLow.Id, depMiddle.Id }, page2.Select(d => d.Id).ToArray());

            var page3 = await store.GetDeploymentHistoryAsync(app.Id, skip: 4, take: 2);
            Assert.Equal(new[] { depEarliest.Id }, page3.Select(d => d.Id).ToArray());

            // 5. Cancellation token honored
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetDeploymentHistoryAsync(app.Id, take: 10, cts.Token));

            // 6. Persistence after closing pools and reopening SQLite
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var reopenedStore = CreateStore(dbFile, out _);
            var afterReopen = await reopenedStore.GetDeploymentHistoryAsync(app.Id, take: 10);
            Assert.Equal(expectedOrder, afterReopen.Select(d => d.Id).ToArray());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteStore_RecentAuditLogs_AndLocalAdminRetrieval_OrderDeterministicallyAcrossOffsetsAndReopen()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-audit-admin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var dbFile = Path.Combine(tempRoot, "audit-admin.db");

        try
        {
            static ServerForgeStore CreateStore(string path, out DbContextOptions<ServerForgeDbContext> options)
            {
                options = new DbContextOptionsBuilder<ServerForgeDbContext>()
                    .UseSqlite($"Data Source={path}")
                    .Options;
                var localOpts = options;
                var factoryMock = new Mock<IDbContextFactory<ServerForgeDbContext>>();
                factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => new ServerForgeDbContext(localOpts));
                return new ServerForgeStore(factoryMock.Object, new DpapiSecretProtector());
            }

            var store = CreateStore(dbFile, out var opts);
            await using (var init = new ServerForgeDbContext(opts))
                await init.InitializeSqliteAsync();

            // Local admin retrieval: earliest CreatedAtUtc across mixed offsets + deterministic Id tie-break
            var earliestAdminInstant = new DateTimeOffset(2026, 1, 10, 8, 0, 0, TimeSpan.Zero);
            var adminLater = new LocalAdminProfile
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000003"),
                DisplayName = "Later Admin",
                Username = "admin-later",
                PasswordHash = "hash3",
                PasswordSalt = "salt3",
                // 2026-01-10T09:00:00Z expressed in UTC-04:00
                CreatedAtUtc = new DateTimeOffset(2026, 1, 10, 5, 0, 0, TimeSpan.FromHours(-4))
            };
            var adminTieHigh = new LocalAdminProfile
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
                DisplayName = "Tie High",
                Username = "admin-tie-high",
                PasswordHash = "hash2",
                PasswordSalt = "salt2",
                CreatedAtUtc = earliestAdminInstant.ToOffset(TimeSpan.FromHours(5))
            };
            var adminTieLow = new LocalAdminProfile
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                DisplayName = "Primary Admin",
                Username = "admin-primary",
                PasswordHash = "hash1",
                PasswordSalt = "salt1",
                CreatedAtUtc = earliestAdminInstant.ToOffset(TimeSpan.FromHours(-8))
            };

            await store.SaveLocalAdminAsync(adminLater);
            await store.SaveLocalAdminAsync(adminTieHigh);
            await store.SaveLocalAdminAsync(adminTieLow);

            var resolvedAdmin = await store.GetLocalAdminAsync();
            Assert.NotNull(resolvedAdmin);
            Assert.Equal(adminTieLow.Id, resolvedAdmin!.Id);
            Assert.Equal("admin-primary", resolvedAdmin.Username);

            // Recent audit logs: mixed offsets, same-timestamp deterministic Id descending, pagination
            var sharedAuditTime = new DateTimeOffset(2026, 11, 19, 14, 0, 0, TimeSpan.Zero);
            await store.RecordAuditAsync(new AuditLogRecord
            {
                Actor = "op",
                Action = "Audit.Oldest",
                TargetResource = "r1",
                // 2026-11-19T13:00:00Z in UTC+06:00
                TimestampUtc = new DateTimeOffset(2026, 11, 19, 19, 0, 0, TimeSpan.FromHours(6))
            });
            await store.RecordAuditAsync(new AuditLogRecord
            {
                Actor = "op",
                Action = "Audit.TieFirstInserted",
                TargetResource = "r2",
                TimestampUtc = sharedAuditTime.ToOffset(TimeSpan.FromHours(-3))
            });
            await store.RecordAuditAsync(new AuditLogRecord
            {
                Actor = "op",
                Action = "Audit.TieSecondInserted",
                TargetResource = "r3",
                TimestampUtc = sharedAuditTime.ToOffset(TimeSpan.FromHours(4))
            });
            await store.RecordAuditAsync(new AuditLogRecord
            {
                Actor = "op",
                Action = "Audit.Newest",
                TargetResource = "r4",
                // 2026-11-19T15:00:00Z in UTC-05:00
                TimestampUtc = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.FromHours(-5))
            });

            var expectedActions = new[] { "Audit.Newest", "Audit.TieSecondInserted", "Audit.TieFirstInserted", "Audit.Oldest" };
            var recent = await store.GetRecentAuditLogsAsync(take: 10);
            Assert.Equal(expectedActions, recent.Select(a => a.Action).ToArray());

            var paged = await store.GetRecentAuditLogsAsync(skip: 1, take: 2);
            Assert.Equal(new[] { "Audit.TieSecondInserted", "Audit.TieFirstInserted" }, paged.Select(a => a.Action).ToArray());
            Assert.Empty(await store.GetRecentAuditLogsAsync(take: 0));

            // Reopen SQLite and confirm persistence & ordering
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var reopenedStore = CreateStore(dbFile, out _);
            var reopenedAdmin = await reopenedStore.GetLocalAdminAsync();
            Assert.Equal(adminTieLow.Id, reopenedAdmin!.Id);
            var reopenedAudit = await reopenedStore.GetRecentAuditLogsAsync(take: 10);
            Assert.Equal(expectedActions, reopenedAudit.Select(a => a.Action).ToArray());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteSchemaMigrator_FreshInstall_AndLegacyUpgrade_PreservesAllMetadataAndCreatesBackup()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-migration-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var freshDbFile = Path.Combine(tempRoot, "fresh.db");
        var legacyDbFile = Path.Combine(tempRoot, "legacy.db");

        try
        {
            // 1. Fresh installation
            var freshOpts = new DbContextOptionsBuilder<ServerForgeDbContext>()
                .UseSqlite($"Data Source={freshDbFile}")
                .Options;
            await using (var freshDb = new ServerForgeDbContext(freshOpts))
            {
                var freshResult = await freshDb.InitializeSqliteAsync();
                Assert.True(freshResult.WasFreshDatabase);
                Assert.Equal(0, freshResult.PreviousVersion);
                Assert.Equal(SqliteSchemaMigrator.LatestSchemaVersion, freshResult.CurrentVersion);
                Assert.Null(freshResult.BackupFilePath);

                // Second call on fresh DB is idempotent and does not re-migrate
                var secondCall = await freshDb.InitializeSqliteAsync();
                Assert.False(secondCall.WasFreshDatabase);
                Assert.Empty(secondCall.AppliedVersions);
                Assert.Null(secondCall.BackupFilePath);
            }

            // 2. Construct a genuine legacy pre-migration SQLite database (no __ServerForgeSchemaVersions,
            //    missing BackupJobs table, and legacy EF Core DateTimeOffset strings with mixed UTC offsets)
            var protector = new DpapiSecretProtector();
            var encryptedPat = protector.Protect("ghp_legacy_secret_value", "vault:github:pat");
            var projectId = Guid.NewGuid();
            var serverId = Guid.NewGuid();
            var appId = Guid.NewGuid();
            var adminId = Guid.NewGuid();
            var envVarId = Guid.NewGuid();
            var depOlderId = Guid.Parse("00000000-0000-0000-0000-000000000101");
            var depNewerId = Guid.Parse("00000000-0000-0000-0000-000000000102");
            var encryptedEnvVal = protector.Protect("super-db-pass", $"env:{appId}:Production:DB_PASSWORD");

            await using (var rawConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={legacyDbFile};Pooling=False"))
            {
                await rawConn.OpenAsync();
                await using var cmd = rawConn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE "AdminProfiles" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "DisplayName" TEXT NOT NULL,
                        "Username" TEXT NOT NULL,
                        "PasswordHash" TEXT NOT NULL,
                        "PasswordSalt" TEXT NOT NULL,
                        "DarkThemeEnabled" INTEGER NOT NULL,
                        "DemoModeEnabled" INTEGER NOT NULL,
                        "CreatedAtUtc" TEXT NOT NULL
                    );
                    CREATE TABLE "VaultSecrets" (
                        "Key" TEXT NOT NULL PRIMARY KEY,
                        "ProtectedValue" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL
                    );
                    CREATE TABLE "Projects" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "Name" TEXT NOT NULL,
                        "Slug" TEXT NOT NULL,
                        "Description" TEXT NOT NULL,
                        "CreatedAtUtc" TEXT NOT NULL
                    );
                    CREATE TABLE "Servers" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "AgentId" TEXT NOT NULL,
                        "DisplayName" TEXT NOT NULL,
                        "HostAddress" TEXT NOT NULL,
                        "AgentPort" INTEGER NOT NULL,
                        "RequirePrivateNetworkOrVpn" INTEGER NOT NULL,
                        "PinnedServerCertThumbprint" TEXT NOT NULL,
                        "ClientCertThumbprint" TEXT NOT NULL,
                        "Status" INTEGER NOT NULL,
                        "OsVersion" TEXT NULL,
                        "IisVersion" TEXT NULL,
                        "LastSeenUtc" TEXT NULL,
                        "CreatedAtUtc" TEXT NOT NULL
                    );
                    CREATE TABLE "Applications" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "ProjectGroupId" TEXT NOT NULL,
                        "ServerId" TEXT NOT NULL,
                        "Name" TEXT NOT NULL,
                        "Slug" TEXT NOT NULL,
                        "RuntimeType" INTEGER NOT NULL,
                        "ManagementState" INTEGER NOT NULL,
                        "RepositoryFullName" TEXT NOT NULL,
                        "Branch" TEXT NOT NULL,
                        "WorkingDirectory" TEXT NOT NULL,
                        "AutoDeployEnabled" INTEGER NOT NULL,
                        "RepositoryExplicitlyAuthorized" INTEGER NOT NULL,
                        "SourceMode" INTEGER NOT NULL,
                        "GitHubWorkflowFileName" TEXT NULL,
                        "BuildCommand" TEXT NOT NULL,
                        "StartCommand" TEXT NOT NULL,
                        "RuntimeVersion" TEXT NOT NULL,
                        "InternalPort" INTEGER NOT NULL,
                        "ServiceAccountName" TEXT NOT NULL,
                        "IisSiteName" TEXT NOT NULL,
                        "IisAppPoolName" TEXT NOT NULL,
                        "WindowsServiceName" TEXT NOT NULL,
                        "PrimaryDomain" TEXT NOT NULL,
                        "HealthCheckPath" TEXT NOT NULL,
                        "HealthCheckTimeoutSeconds" INTEGER NOT NULL,
                        "RetainHealthyReleasesCount" INTEGER NOT NULL,
                        "AutoRollbackOnFailure" INTEGER NOT NULL,
                        "ReleasesRootPath" TEXT NOT NULL,
                        "PersistentDataPath" TEXT NOT NULL,
                        "ActiveReleasePath" TEXT NULL,
                        "LastKnownGoodReleasePath" TEXT NULL,
                        "LastDeployedCommitSha" TEXT NULL,
                        "CreatedAtUtc" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL,
                        CONSTRAINT "FK_Applications_Projects_ProjectGroupId" FOREIGN KEY ("ProjectGroupId") REFERENCES "Projects" ("Id") ON DELETE RESTRICT
                    );
                    CREATE TABLE "Deployments" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "ApplicationId" TEXT NOT NULL,
                        "ServerId" TEXT NOT NULL,
                        "State" INTEGER NOT NULL,
                        "CommitSha" TEXT NOT NULL,
                        "Branch" TEXT NOT NULL,
                        "CommitAuthor" TEXT NOT NULL,
                        "CommitMessage" TEXT NOT NULL,
                        "ArtifactSha256" TEXT NULL,
                        "ReleaseDirectory" TEXT NULL,
                        "PreviousReleaseDirectory" TEXT NULL,
                        "TriggeredAutomatically" INTEGER NOT NULL,
                        "ZeroDowntimeAchieved" INTEGER NOT NULL,
                        "ZeroDowntimeNote" TEXT NULL,
                        "ErrorSummary" TEXT NULL,
                        "TriggeredBy" TEXT NOT NULL,
                        "QueuedAtUtc" TEXT NOT NULL,
                        "StartedAtUtc" TEXT NULL,
                        "CompletedAtUtc" TEXT NULL
                    );
                    CREATE TABLE "EnvironmentVariables" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "ApplicationId" TEXT NOT NULL,
                        "Tier" INTEGER NOT NULL,
                        "Key" TEXT NOT NULL,
                        "IsSecret" INTEGER NOT NULL,
                        "IsRequired" INTEGER NOT NULL,
                        "ProtectedValue" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL
                    );
                    CREATE TABLE "AuditLogs" (
                        "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        "TimestampUtc" TEXT NOT NULL,
                        "Actor" TEXT NOT NULL,
                        "Action" TEXT NOT NULL,
                        "TargetResource" TEXT NOT NULL,
                        "Outcome" TEXT NOT NULL,
                        "Details" TEXT NOT NULL,
                        "ServerId" TEXT NULL,
                        "ApplicationId" TEXT NULL
                    );
                    """;
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = """
                    INSERT INTO "AdminProfiles" VALUES ($adminId, 'Legacy Operator', 'legacy-admin', 'h', 's', 1, 0, '2026-05-01 12:00:00+02:00');
                    INSERT INTO "VaultSecrets" VALUES ('github:pat', $encPat, '2026-05-01 12:05:00+02:00');
                    INSERT INTO "Projects" VALUES ($projectId, 'Legacy Group', 'legacy-group', 'Pre-migration project', '2026-05-01 12:10:00+02:00');
                    INSERT INTO "Servers" VALUES ($serverId, 'agent-legacy-01', 'Win2022-Prod', '10.20.0.10', 9443, 1, 'PINNED_SERVER_THUMB', 'PINNED_CLIENT_THUMB', 2, 'Win2022', '10.0', '2026-05-02 09:00:00+00:00', '2026-05-01 12:15:00+02:00');
                    INSERT INTO "Applications" VALUES ($appId, $projectId, $serverId, 'Legacy API', 'legacy-api', 2, 2, 'owner/legacy-api', 'main', '.', 1, 1, 0, 'deploy.yml', '', '', '20', 4050, 'NT SERVICE\SF', 'SF-legacy', 'SF-legacy-Pool', 'SF_legacy', 'legacy.local', '/health', 30, 5, 1, 'C:\Apps\legacy', 'C:\Apps\legacy\data', 'C:\Apps\legacy\r1', 'C:\Apps\legacy\r1', '1111111111111111111111111111111111111111', '2026-05-01 12:20:00+02:00', '2026-05-01 12:25:00+02:00');
                    INSERT INTO "EnvironmentVariables" VALUES ($envId, $appId, $envTier, 'DB_PASSWORD', 1, 1, $encEnvVal, '2026-05-01 12:30:00+02:00');
                    -- depOlder: 2026-11-19T10:00:00Z written as +05:00 (lexicographically "15:00" looks larger than "11:00" before UTC normalization!)
                    INSERT INTO "Deployments" VALUES ($depOlderId, $appId, $serverId, $deployState, '1111111111111111111111111111111111111111', 'main', 'dev', 'older deploy', NULL, NULL, NULL, 1, 1, NULL, NULL, 'ci', '2026-11-19 15:00:00+05:00', '2026-11-19 15:00:05+05:00', '2026-11-19 15:01:00+05:00');
                    -- depNewer: 2026-11-19T11:00:00Z written as -05:00 (lexicographically "06:00" looks smaller before UTC normalization!)
                    INSERT INTO "Deployments" VALUES ($depNewerId, $appId, $serverId, $deployState, '2222222222222222222222222222222222222222', 'main', 'dev', 'newer deploy', NULL, NULL, NULL, 1, 1, NULL, NULL, 'ci', '2026-11-19 06:00:00-05:00', '2026-11-19 06:00:05-05:00', '2026-11-19 06:01:00-05:00');
                    INSERT INTO "AuditLogs" ("TimestampUtc", "Actor", "Action", "TargetResource", "Outcome", "Details") VALUES ('2026-11-19 15:00:00+05:00', 'legacy-admin', 'Legacy.Earlier', 'legacy-api', 'Succeeded', 'earlier');
                    INSERT INTO "AuditLogs" ("TimestampUtc", "Actor", "Action", "TargetResource", "Outcome", "Details") VALUES ('2026-11-19 06:30:00-05:00', 'legacy-admin', 'Legacy.Later', 'legacy-api', 'Succeeded', 'later');
                    """;
                cmd.Parameters.AddWithValue("$adminId", adminId.ToString().ToUpperInvariant());
                cmd.Parameters.AddWithValue("$encPat", encryptedPat);
                cmd.Parameters.AddWithValue("$projectId", projectId.ToString().ToUpperInvariant());
                cmd.Parameters.AddWithValue("$serverId", serverId.ToString().ToUpperInvariant());
                cmd.Parameters.AddWithValue("$appId", appId.ToString().ToUpperInvariant());
                cmd.Parameters.AddWithValue("$envId", envVarId.ToString().ToUpperInvariant());
                cmd.Parameters.AddWithValue("$envTier", (int)EnvironmentTier.Production);
                cmd.Parameters.AddWithValue("$deployState", (int)DeploymentState.Successful);
                cmd.Parameters.AddWithValue("$encEnvVal", encryptedEnvVal);
                cmd.Parameters.AddWithValue("$depOlderId", depOlderId.ToString().ToUpperInvariant());
                cmd.Parameters.AddWithValue("$depNewerId", depNewerId.ToString().ToUpperInvariant());
                await cmd.ExecuteNonQueryAsync();
            }

            // 3. Upgrade legacy database safely
            var legacyOpts = new DbContextOptionsBuilder<ServerForgeDbContext>()
                .UseSqlite($"Data Source={legacyDbFile}")
                .Options;

            SqliteMigrationResult migrationResult;
            await using (var migrateDb = new ServerForgeDbContext(legacyOpts))
            {
                migrationResult = await migrateDb.InitializeSqliteAsync();
                Assert.False(migrationResult.WasFreshDatabase);
                Assert.Equal(0, migrationResult.PreviousVersion);
                Assert.Equal(SqliteSchemaMigrator.LatestSchemaVersion, migrationResult.CurrentVersion);
                Assert.Equal(new[] { 1, 2, 3 }, migrationResult.AppliedVersions);
                Assert.True(migrationResult.NormalizedTimestampCount >= 10);
                Assert.NotNull(migrationResult.BackupFilePath);
                Assert.True(File.Exists(migrationResult.BackupFilePath!));
                Assert.True(new FileInfo(migrationResult.BackupFilePath!).Length > 0);
            }

            // 4. Verify EVERY pre-existing record was preserved and sorts accurately in UTC
            var factoryMock = new Mock<IDbContextFactory<ServerForgeDbContext>>();
            factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ServerForgeDbContext(legacyOpts));
            var store = new ServerForgeStore(factoryMock.Object, protector);

            var admin = await store.GetLocalAdminAsync();
            Assert.NotNull(admin);
            Assert.Equal("legacy-admin", admin!.Username);

            Assert.Equal("ghp_legacy_secret_value", await store.GetVaultSecretAsync("github:pat"));

            var servers = await store.GetServersAsync();
            Assert.Single(servers);
            Assert.Equal("agent-legacy-01", servers[0].AgentId);
            Assert.Equal("PINNED_SERVER_THUMB", servers[0].PinnedServerCertThumbprint);
            Assert.Equal("PINNED_CLIENT_THUMB", servers[0].ClientCertThumbprint);

            var apps = await store.GetApplicationsAsync();
            Assert.Single(apps);
            Assert.Equal("legacy-api", apps[0].Slug);

            var decryptedEnv = await store.ResolveDecryptedEnvVarsAsync(appId, EnvironmentTier.Production);
            Assert.Equal("super-db-pass", decryptedEnv["DB_PASSWORD"]);

            var deployments = await store.GetDeploymentHistoryAsync(appId);
            Assert.Equal(new[] { depNewerId, depOlderId }, deployments.Select(d => d.Id).ToArray());

            var audits = await store.GetRecentAuditLogsAsync(10);
            Assert.Equal(new[] { "Legacy.Later", "Legacy.Earlier" }, audits.Select(a => a.Action).ToArray());

            // Missing BackupJobs table was safely added during v1 baseline upgrade
            await store.UpsertBackupJobAsync(new BackupJobRecord
            {
                ApplicationId = appId,
                TargetName = "legacy-api-data",
                DestinationPath = @"C:\Backups"
            }, "unit-test");
            Assert.Single(await store.GetBackupJobsAsync());

            // Phase 12.2 AutonomousDeploymentCandidates table was safely added during v3 upgrade
            await store.UpsertAutonomousCandidateAsync(new AutonomousDeploymentCandidate
            {
                ApplicationId = appId,
                CommitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Branch = "main",
                LastBranchHeadETag = "\"legacy-etag\"",
                State = AutonomousCandidateState.WorkflowRunning
            });
            var upgradedCandidate = await store.GetAutonomousCandidateAsync(appId);
            Assert.NotNull(upgradedCandidate);
            Assert.Equal(AutonomousCandidateState.WorkflowRunning, upgradedCandidate!.State);

            // Re-running InitializeSqliteAsync after upgrade is idempotent (no new backup)
            await using (var recheckDb = new ServerForgeDbContext(legacyOpts))
            {
                var recheck = await recheckDb.InitializeSqliteAsync();
                Assert.Empty(recheck.AppliedVersions);
                Assert.Null(recheck.BackupFilePath);
                Assert.Equal(SqliteSchemaMigrator.LatestSchemaVersion, await SqliteSchemaMigrator.GetSchemaVersionAsync(recheckDb));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SqliteSchemaMigrator_FailsSafelyAndRollsBackOnCorruptLegacyTimestamp()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-migration-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var corruptDbFile = Path.Combine(tempRoot, "corrupt-legacy.db");

        try
        {
            await using (var rawConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={corruptDbFile};Pooling=False"))
            {
                await rawConn.OpenAsync();
                await using var cmd = rawConn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE "Servers" (
                        "Id" TEXT NOT NULL PRIMARY KEY,
                        "AgentId" TEXT NOT NULL,
                        "DisplayName" TEXT NOT NULL,
                        "HostAddress" TEXT NOT NULL,
                        "AgentPort" INTEGER NOT NULL,
                        "RequirePrivateNetworkOrVpn" INTEGER NOT NULL,
                        "PinnedServerCertThumbprint" TEXT NOT NULL,
                        "ClientCertThumbprint" TEXT NOT NULL,
                        "Status" INTEGER NOT NULL,
                        "OsVersion" TEXT NULL,
                        "IisVersion" TEXT NULL,
                        "LastSeenUtc" TEXT NULL,
                        "CreatedAtUtc" TEXT NOT NULL
                    );
                    INSERT INTO "Servers" VALUES ('00000000-0000-0000-0000-000000000201', 'agent-ok', 'Valid Server', '10.0.0.1', 9443, 1, 'THUMB1', 'CLIENT1', 2, NULL, NULL, NULL, '2026-05-01 10:00:00+02:00');
                    INSERT INTO "Servers" VALUES ('00000000-0000-0000-0000-000000000202', 'agent-corrupt', 'Corrupt Row', '10.0.0.2', 9443, 1, 'THUMB2', 'CLIENT2', 2, NULL, NULL, NULL, 'NOT-A-VALID-TIMESTAMP');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var opts = new DbContextOptionsBuilder<ServerForgeDbContext>()
                .UseSqlite($"Data Source={corruptDbFile}")
                .Options;

            await using (var db = new ServerForgeDbContext(opts))
            {
                var ex = await Assert.ThrowsAsync<SqliteMigrationException>(() => db.InitializeSqliteAsync());
                Assert.NotNull(ex.BackupFilePath);
                Assert.True(File.Exists(ex.BackupFilePath!));
                Assert.True(new FileInfo(ex.BackupFilePath!).Length > 0);
                // Schema version must remain 0 because the transaction rolled back cleanly
                Assert.Equal(0, await SqliteSchemaMigrator.GetSchemaVersionAsync(db));
            }

            // Original DB must still exist and row 1 must remain rolled back to its original pre-migration text
            Assert.True(File.Exists(corruptDbFile));
            await using (var verifyConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={corruptDbFile};Pooling=False"))
            {
                await verifyConn.OpenAsync();
                await using var checkCmd = verifyConn.CreateCommand();
                checkCmd.CommandText = "SELECT \"CreatedAtUtc\" FROM \"Servers\" WHERE \"AgentId\" = 'agent-ok';";
                Assert.Equal("2026-05-01 10:00:00+02:00", (string?)await checkCmd.ExecuteScalarAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
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

    [Fact]
    public async Task AspNetCoreDeploymentAdapter_AppliesEnvVarsToWebConfig_AndPreservesPreviousReleaseOnRollback()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-aspnetcore-webcfg-{Guid.NewGuid():N}");
        var release1 = Path.Combine(tempRoot, "releases", "r1");
        var release2 = Path.Combine(tempRoot, "releases", "r2");
        Directory.CreateDirectory(release1);
        Directory.CreateDirectory(release2);
        await File.WriteAllTextAsync(Path.Combine(release1, "ServerForge.SampleApp.exe"), "stub-exe");
        await File.WriteAllTextAsync(Path.Combine(release2, "ServerForge.SampleApp.exe"), "stub-exe");

        try
        {
            var iisMock = new Mock<IIisManager>();
            var activePath = string.Empty;
            iisMock.Setup(m => m.EnsureManagedSiteAndPoolAsync(It.IsAny<ManagedApplication>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            iisMock.Setup(m => m.SwitchSitePhysicalPathAsync(It.IsAny<ManagedApplication>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<ManagedApplication, string, CancellationToken>((_, p, _) => activePath = p)
                .Returns(Task.CompletedTask);

            var adapter = new AspNetCoreDeploymentAdapter(iisMock.Object);
            var app = new ManagedApplication
            {
                ProjectGroupId = Guid.NewGuid(),
                Name = "Sample E2E",
                Slug = "sample-e2e",
                RuntimeType = ApplicationRuntimeType.AspNetCore,
                ManagementState = ResourceManagementState.ServerForgeCreated,
                IisSiteName = "ServerForge_E2E_01",
                IisAppPoolName = "ServerForge_E2E_01_Pool",
                InternalPort = 5199,
                PrimaryDomain = "localhost",
                ReleasesRootPath = tempRoot,
                PersistentDataPath = Path.Combine(tempRoot, "data")
            };

            var res1 = await adapter.DeployStagedReleaseAsync(
                app,
                release1,
                new Dictionary<string, string> { ["APP_VERSION"] = "1.0.0", ["SIMULATE_UNHEALTHY"] = "false" },
                _ => { },
                CancellationToken.None);
            Assert.True(res1.Succeeded);
            Assert.Equal(release1, activePath);

            var r1Config = await File.ReadAllTextAsync(Path.Combine(release1, "web.config"));
            Assert.Contains("AspNetCoreModuleV2", r1Config);
            Assert.Contains("name=\"APP_VERSION\" value=\"1.0.0\"", r1Config);
            Assert.Contains("name=\"PERSISTENT_DATA_DIR\"", r1Config);

            var res2 = await adapter.DeployStagedReleaseAsync(
                app,
                release2,
                new Dictionary<string, string> { ["APP_VERSION"] = "1.0.1-bad", ["SIMULATE_UNHEALTHY"] = "true" },
                _ => { },
                CancellationToken.None);
            Assert.True(res2.Succeeded);
            Assert.Equal(release2, activePath);
            Assert.Contains("name=\"SIMULATE_UNHEALTHY\" value=\"true\"", await File.ReadAllTextAsync(Path.Combine(release2, "web.config")));

            // Rollback switches IIS back to immutable release1 whose web.config still holds healthy 1.0.0 settings
            await adapter.RollbackReleaseAsync(app, release1, _ => { }, CancellationToken.None);
            Assert.Equal(release1, activePath);
            var r1ConfigAfterRollback = await File.ReadAllTextAsync(Path.Combine(release1, "web.config"));
            Assert.Contains("name=\"APP_VERSION\" value=\"1.0.0\"", r1ConfigAfterRollback);
            Assert.Contains("name=\"SIMULATE_UNHEALTHY\" value=\"false\"", r1ConfigAfterRollback);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void IisAdministrationService_ResolveSiteBinding_AndHealthProbe_UseIsolatedLoopbackPort()
    {
        var disposableApp = new ManagedApplication
        {
            RuntimeType = ApplicationRuntimeType.AspNetCore,
            PrimaryDomain = "localhost",
            InternalPort = 5199,
            HealthCheckPath = "/health"
        };
        Assert.Equal("http/127.0.0.1:5199:", IisAdministrationService.ResolveSiteBinding(disposableApp));
        Assert.Equal("http://127.0.0.1:5199/health", DeploymentOrchestrator.ResolveHealthCheckUrl(disposableApp));

        var domainApp = new ManagedApplication
        {
            RuntimeType = ApplicationRuntimeType.AspNetCore,
            PrimaryDomain = "sample.example.com",
            InternalPort = 0,
            HealthCheckPath = "/health"
        };
        Assert.Equal("http/*:80:sample.example.com", IisAdministrationService.ResolveSiteBinding(domainApp));
        Assert.Equal("http://sample.example.com/health", DeploymentOrchestrator.ResolveHealthCheckUrl(domainApp));

        var proxyApp = new ManagedApplication
        {
            RuntimeType = ApplicationRuntimeType.NestJsApi,
            PrimaryDomain = "api.example.com",
            InternalPort = 4010,
            HealthCheckPath = "/health"
        };
        Assert.Equal("http/*:80:api.example.com", IisAdministrationService.ResolveSiteBinding(proxyApp));
        Assert.Equal("http://127.0.0.1:4010/health", DeploymentOrchestrator.ResolveHealthCheckUrl(proxyApp));
    }

    [Fact]
    public async Task GitHubApiClient_RejectsBadWorkflowConclusion_ExpiredArtifact_HashMismatch_AndApiOutage()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-gh-neg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            // 1. Unavailable GitHub API (HTTP 503)
            var outageClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))),
                NullLogger<GitHubApiClient>.Instance);
            var outageRes = await outageClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", Path.Combine(tempRoot, "outage.zip"), CancellationToken.None);
            Assert.False(outageRes.Found);
            Assert.Contains("503", outageRes.ErrorMessage ?? "");

            // 2. Bad workflow conclusion ("failure")
            var badConclusionClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"workflow_runs":[{"id":901,"conclusion":"failure","head_sha":"{{Commit1}}","head_branch":"main","path":".github/workflows/serverforge-deploy.yml"}]}""")
                })),
                NullLogger<GitHubApiClient>.Instance);
            var badConclusionRes = await badConclusionClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", Path.Combine(tempRoot, "bad.zip"), CancellationToken.None);
            Assert.False(badConclusionRes.Found);

            // 3. Expired artifact
            var expiredClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(req =>
                {
                    var uri = req.RequestUri!.AbsoluteUri;
                    if (uri.Contains("/actions/runs?"))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"workflow_runs":[{"id":902,"conclusion":"success","head_sha":"{{Commit1}}","head_branch":"main","path":".github/workflows/serverforge-deploy.yml"}]}""") };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"artifacts":[{"id":77,"name":"app","expired":true,"size_in_bytes":2048,"digest":"sha256:aaaa","archive_download_url":"https://api.github.com/dl/77"}]}""") };
                })),
                NullLogger<GitHubApiClient>.Instance);
            var expiredRes = await expiredClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", Path.Combine(tempRoot, "exp.zip"), CancellationToken.None);
            Assert.False(expiredRes.Found);
            Assert.Contains("non-expired", expiredRes.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);

            // 4. Artifact SHA-256 hash mismatch -> deleted & rejected
            var mismatchZipPath = Path.Combine(tempRoot, "mismatch.zip");
            var mismatchClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(req =>
                {
                    var uri = req.RequestUri!.AbsoluteUri;
                    if (uri.Contains("/actions/runs?"))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"workflow_runs":[{"id":903,"conclusion":"success","head_sha":"{{Commit1}}","head_branch":"main","path":".github/workflows/serverforge-deploy.yml"}]}""") };
                    if (uri.Contains("/artifacts"))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"artifacts":[{"id":88,"name":"app","expired":false,"size_in_bytes":16,"digest":"sha256:0000000000000000000000000000000000000000000000000000000000000000","archive_download_url":"https://api.github.com/dl/88"}]}""") };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4, 5, 6]) };
                })),
                NullLogger<GitHubApiClient>.Instance);
            var mismatchRes = await mismatchClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", mismatchZipPath, CancellationToken.None);
            Assert.False(mismatchRes.Found);
            Assert.Contains("mismatch", mismatchRes.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(mismatchZipPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DeploymentOrchestrator_GitHubActionsMode_RejectsLocalPath_UntrustedUrl_ForgedIds_AndDigestMismatch()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-prov-sec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var zipPath = Path.Combine(tempRoot, "attacker-local.zip");
        CreateSampleStaticZip(zipPath, "<html><body>forged</body></html>");
        var localHash = await SafeProcessRunner.ComputeFileSha256Async(zipPath);

        try
        {
            var iisMock = new Mock<IIisManager>();
            var adapter = new StaticSiteDeploymentAdapter(ApplicationRuntimeType.StaticHtml, iisMock.Object);
            var okHttp = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

            var verifierMock = new Mock<IGitHubArtifactProvenanceVerifier>();
            verifierMock.Setup(v => v.ResolveAndVerifyArtifactAsync(
                    It.IsAny<ManagedApplication>(), "main", Commit1, 999999, 888888, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VerifiedGitHubArtifact(false, null, null, null, null, null, null, "Workflow run provenance mismatch."));
            verifierMock.Setup(v => v.ResolveAndVerifyArtifactAsync(
                    It.IsAny<ManagedApplication>(), "main", Commit2, 2001, 3001, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VerifiedGitHubArtifact(true, zipPath, localHash, 2001, 3001, "pkg", "GitHubActionsRun:2001/Artifact:3001", null));

            var orchestrator = new DeploymentOrchestrator(
                [adapter], new PathBoundaryGuard(), new DpapiSecretProtector(), okHttp, NullLogger<DeploymentOrchestrator>.Instance,
                provenanceVerifier: verifierMock.Object);

            var ghApp = CreateStaticApp(tempRoot);
            ghApp.SourceMode = DeploymentSourceMode.GitHubActionsArtifact;

            // 1. Caller supplies local file path in GitHubActionsArtifact mode -> MUST fail closed
            var localPathAttempt = await orchestrator.EnqueueAndExecuteAsync(
                ghApp,
                new TriggerDeploymentRequest(ghApp.Id, Commit1, "main", "forged local", "attacker", zipPath, localHash, false, "api", 120001, 120001, "fake", "GitHubActions"),
                new Dictionary<string, string>());
            Assert.Equal(DeploymentState.Failed, localPathAttempt.State);
            Assert.Contains("rejects caller-supplied", localPathAttempt.ErrorSummary ?? "", StringComparison.OrdinalIgnoreCase);

            // 2. Caller supplies untrusted remote URL in GitHubActionsArtifact mode -> MUST fail closed
            var untrustedUrlAttempt = await orchestrator.EnqueueAndExecuteAsync(
                ghApp,
                new TriggerDeploymentRequest(ghApp.Id, Commit1, "main", "forged url", "attacker", "https://evil.example.com/payload.zip", localHash, false, "api", 120001, 120001, "fake", "GitHubActions"),
                new Dictionary<string, string>());
            Assert.Equal(DeploymentState.Failed, untrustedUrlAttempt.State);

            // 3. Caller supplies forged workflow/artifact IDs -> verifier rejects -> MUST fail closed
            var forgedIdsAttempt = await orchestrator.EnqueueAndExecuteAsync(
                ghApp,
                new TriggerDeploymentRequest(ghApp.Id, Commit1, "main", "forged ids", "attacker", null, localHash, false, "api", 999999, 888888, "fake", "GitHubActions"),
                new Dictionary<string, string>());
            Assert.Equal(DeploymentState.Failed, forgedIdsAttempt.State);
            Assert.Contains("provenance", forgedIdsAttempt.ErrorSummary ?? "", StringComparison.OrdinalIgnoreCase);

            // 4. Caller supplies mismatched SHA-256 against verified GitHub API digest -> MUST fail closed
            var badDigestAttempt = await orchestrator.EnqueueAndExecuteAsync(
                ghApp,
                new TriggerDeploymentRequest(ghApp.Id, Commit2, "main", "bad digest", "attacker", null, new string('0', 64), false, "api", 2001, 3001, "pkg", "GitHubActions"),
                new Dictionary<string, string>());
            Assert.Equal(DeploymentState.Failed, badDigestAttempt.State);
            Assert.Contains("does not match", badDigestAttempt.ErrorSummary ?? "", StringComparison.OrdinalIgnoreCase);

            // 5. ManualSignedPackage rejects remote URLs and non-64-char hashes, succeeds only for hash-verified local package
            var manualApp = CreateStaticApp(tempRoot);
            manualApp.SourceMode = DeploymentSourceMode.ManualSignedPackage;
            var remoteManualAttempt = await orchestrator.EnqueueAndExecuteAsync(
                manualApp,
                new TriggerDeploymentRequest(manualApp.Id, Commit1, "main", "remote manual", "op", "https://evil.example.com/pkg.zip", localHash, false, "op"),
                new Dictionary<string, string>());
            Assert.Equal(DeploymentState.Failed, remoteManualAttempt.State);

            var shortHashAttempt = await orchestrator.EnqueueAndExecuteAsync(
                manualApp,
                new TriggerDeploymentRequest(manualApp.Id, Commit1, "main", "short hash", "op", zipPath, "deadbeef", false, "op"),
                new Dictionary<string, string>());
            Assert.Equal(DeploymentState.Failed, shortHashAttempt.State);

            iisMock.Verify(m => m.SwitchSitePhysicalPathAsync(It.IsAny<ManagedApplication>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GitHubApiClient_RejectsUntrustedArtifactDownloadHost_AndNonHttpsUrls()
    {
        Assert.True(GitHubApiClient.IsTrustedGitHubArtifactUrl("https://api.github.com/repos/owner/repo/actions/artifacts/42/zip"));
        Assert.False(GitHubApiClient.IsTrustedGitHubArtifactUrl("http://api.github.com/repos/owner/repo/actions/artifacts/42/zip"));
        Assert.False(GitHubApiClient.IsTrustedGitHubArtifactUrl("https://evil.example.com/repos/owner/repo/actions/artifacts/42/zip"));
        Assert.False(GitHubApiClient.IsTrustedGitHubArtifactUrl("file:///C:/temp/payload.zip"));
        Assert.False(GitHubApiClient.IsTrustedGitHubArtifactUrl(@"C:\temp\payload.zip"));

        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-gh-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var evilHostClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(req =>
                {
                    var uri = req.RequestUri!.AbsoluteUri;
                    if (uri.Contains("/actions/runs?"))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"workflow_runs":[{"id":904,"conclusion":"success","head_sha":"{{Commit1}}","head_branch":"main","path":".github/workflows/serverforge-deploy.yml"}]}""") };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"artifacts":[{"id":99,"name":"app","expired":false,"size_in_bytes":64,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","archive_download_url":"https://evil.example.com/dl/99"}]}""") };
                })),
                NullLogger<GitHubApiClient>.Instance);

            var destPath = Path.Combine(tempRoot, "blocked.zip");
            var res = await evilHostClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", destPath, CancellationToken.None);
            Assert.False(res.Found);
            Assert.Contains("untrusted", res.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(destPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GitHubApiClient_ResolvesMissingWorkflowPathByWorkflowId_AndRejectsUnmatchedOrMissingWorkflowIdentity()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-gh-wfid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var validZip = Path.Combine(tempRoot, "valid-source.zip");
        CreateSampleStaticZip(validZip, "<html><body>v1</body></html>");
        var validZipBytes = await File.ReadAllBytesAsync(validZip);
        var validSha = await SafeProcessRunner.ComputeFileSha256Async(validZip);

        try
        {
            // 1. Blank expectedWorkflowFileName -> fail closed immediately
            var blankWfClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(_ => throw new InvalidOperationException("Should not call API"))),
                NullLogger<GitHubApiClient>.Instance);
            var blankRes = await blankWfClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "", Path.Combine(tempRoot, "blank.zip"), CancellationToken.None);
            Assert.False(blankRes.Found);
            Assert.Equal(GitHubArtifactResolutionStatus.ProvenanceRejected, blankRes.ResolutionStatus);

            // 2. Missing `path` on run list AND no workflow_id -> MUST NOT silently accept!
            var missingPathNoIdClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        workflow_runs = new[]
                        {
                            new
                            {
                                id = 910L,
                                status = "completed",
                                conclusion = "success",
                                head_sha = Commit1,
                                head_branch = "main"
                            }
                        }
                    }))
                })),
                NullLogger<GitHubApiClient>.Instance);
            var missingNoIdRes = await missingPathNoIdClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", Path.Combine(tempRoot, "noid.zip"), CancellationToken.None);
            Assert.False(missingNoIdRes.Found);

            // 3. Suffix-spoofed workflow path (`evil-serverforge-deploy.yml`) -> MUST fail closed
            Assert.False(GitHubApiClient.IsExactWorkflowPathMatch(".github/workflows/evil-serverforge-deploy.yml", "serverforge-deploy.yml"));
            Assert.True(GitHubApiClient.IsExactWorkflowPathMatch(".github/workflows/serverforge-deploy.yml", "serverforge-deploy.yml"));

            // 4. Missing `path` on run list WITH `workflow_id` -> explicitly resolves `/actions/workflows/{id}` and succeeds!
            var resolvedWorkflowEndpointCalled = false;
            var artifactListEndpointCalled = false;
            var artifactDownloadEndpointCalled = false;
            var resolveByIdClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(req =>
                {
                    var uri = req.RequestUri!.AbsoluteUri;
                    var path = req.RequestUri.AbsolutePath;
                    if (uri.Contains("/actions/runs?", StringComparison.Ordinal))
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                workflow_runs = new[]
                                {
                                    new
                                    {
                                        id = 911L,
                                        workflow_id = 555L,
                                        status = "completed",
                                        conclusion = "success",
                                        head_sha = Commit1,
                                        head_branch = "main",
                                        repository = new { full_name = "owner/repo" }
                                    }
                                }
                            }))
                        };
                    if (path.Equals("/repos/owner/repo/actions/workflows/555", StringComparison.Ordinal))
                    {
                        resolvedWorkflowEndpointCalled = true;
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                id = 555L,
                                path = ".github/workflows/serverforge-deploy.yml"
                            }))
                        };
                    }
                    if (path.Equals("/repos/owner/repo/actions/runs/911/artifacts", StringComparison.Ordinal))
                    {
                        artifactListEndpointCalled = true;
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                artifacts = new[]
                                {
                                    new
                                    {
                                        id = 556L,
                                        name = "serverforge-release",
                                        expired = false,
                                        size_in_bytes = validZipBytes.Length,
                                        digest = $"sha256:{validSha}",
                                        workflow_run = new { id = 911L },
                                        archive_download_url = "https://api.github.com/repos/owner/repo/actions/artifacts/556/zip"
                                    }
                                }
                            }))
                        };
                    }
                    if (path.Equals("/repos/owner/repo/actions/artifacts/556/zip", StringComparison.Ordinal))
                    {
                        artifactDownloadEndpointCalled = true;
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(validZipBytes) };
                    }
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent($"Unexpected mock request: {req.Method} {uri}")
                    };
                })),
                NullLogger<GitHubApiClient>.Instance);

            var resolvedDest = Path.Combine(tempRoot, "resolved.zip");
            var resolvedRes = await resolveByIdClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", resolvedDest, CancellationToken.None);
            Assert.True(resolvedWorkflowEndpointCalled, "Expected GET /repos/owner/repo/actions/workflows/555 to be invoked.");
            Assert.True(artifactListEndpointCalled, "Expected GET /repos/owner/repo/actions/runs/911/artifacts to be invoked.");
            Assert.True(artifactDownloadEndpointCalled, "Expected GET /repos/owner/repo/actions/artifacts/556/zip to be invoked.");
            Assert.True(
                resolvedRes.Found,
                $"Expected verified artifact download to succeed, but got ResolutionStatus={resolvedRes.ResolutionStatus}, ErrorMessage='{resolvedRes.ErrorMessage}'.");
            Assert.Equal(GitHubArtifactResolutionStatus.ArtifactVerified, resolvedRes.ResolutionStatus);
            Assert.Equal(911L, resolvedRes.WorkflowRunId);
            Assert.Equal(556L, resolvedRes.ArtifactId);
            Assert.Equal(validSha, resolvedRes.ComputedSha256);
            Assert.True(File.Exists(resolvedDest));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GitHubApiClient_EnforcesStrictArtifactSelectionPolicy_SizeLimit_ZipValidation_AndSafeRedirects()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-gh-strict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var validZip = Path.Combine(tempRoot, "valid-source.zip");
        CreateSampleStaticZip(validZip, "<html><body>redirected</body></html>");
        var validZipBytes = await File.ReadAllBytesAsync(validZip);
        var validSha = await SafeProcessRunner.ComputeFileSha256Async(validZip);

        try
        {
            // 1. Ambiguous multi-artifact run without expected name or canonical serverforge-* name -> fail closed!
            var ambiguousClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(req =>
                {
                    var uri = req.RequestUri!.AbsoluteUri;
                    if (uri.Contains("/actions/runs?"))
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                workflow_runs = new[]
                                {
                                    new
                                    {
                                        id = 920L,
                                        status = "completed",
                                        conclusion = "success",
                                        head_sha = Commit1,
                                        head_branch = "main",
                                        path = ".github/workflows/serverforge-deploy.yml"
                                    }
                                }
                            }))
                        };
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        {
                            artifacts = new[]
                            {
                                new
                                {
                                    id = 1L,
                                    name = "coverage-report",
                                    expired = false,
                                    size_in_bytes = 100,
                                    digest = $"sha256:{validSha}",
                                    archive_download_url = "https://api.github.com/dl/1"
                                },
                                new
                                {
                                    id = 2L,
                                    name = "debug-logs",
                                    expired = false,
                                    size_in_bytes = 100,
                                    digest = $"sha256:{validSha}",
                                    archive_download_url = "https://api.github.com/dl/2"
                                }
                            }
                        }))
                    };
                })),
                NullLogger<GitHubApiClient>.Instance);
            var ambRes = await ambiguousClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", Path.Combine(tempRoot, "amb.zip"), CancellationToken.None);
            Assert.False(ambRes.Found);
            Assert.Equal(GitHubArtifactResolutionStatus.ProvenanceRejected, ambRes.ResolutionStatus);
            Assert.Contains("Ambiguous", ambRes.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);

            // 2. Trusted 302 redirect to *.githubusercontent.com strips Authorization header and downloads cleanly
            bool? redirectHadAuthHeader = null;
            var redirectClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(req =>
                {
                    var uri = req.RequestUri!.AbsoluteUri;
                    if (uri.Contains("/actions/runs?"))
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                workflow_runs = new[]
                                {
                                    new
                                    {
                                        id = 921L,
                                        status = "completed",
                                        conclusion = "success",
                                        head_sha = Commit1,
                                        head_branch = "main",
                                        path = ".github/workflows/serverforge-deploy.yml"
                                    }
                                }
                            }))
                        };
                    if (uri.Contains("/artifacts") && !uri.EndsWith("/zip"))
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                artifacts = new[]
                                {
                                    new
                                    {
                                        id = 20L,
                                        name = "coverage",
                                        expired = false,
                                        size_in_bytes = 50,
                                        digest = $"sha256:{validSha}",
                                        archive_download_url = "https://api.github.com/repos/owner/repo/actions/artifacts/20/zip"
                                    },
                                    new
                                    {
                                        id = 21L,
                                        name = "serverforge-sample-app-win-x64",
                                        expired = false,
                                        size_in_bytes = validZipBytes.Length,
                                        digest = $"sha256:{validSha}",
                                        archive_download_url = "https://api.github.com/repos/owner/repo/actions/artifacts/21/zip"
                                    }
                                }
                            }))
                        };
                    if (uri.StartsWith("https://api.github.com/repos/owner/repo/actions/artifacts/21/zip"))
                    {
                        var r = new HttpResponseMessage(HttpStatusCode.Found);
                        r.Headers.Location = new Uri("https://pipelinesghubeus1.actions.githubusercontent.com/sas/pkg.zip?sig=test");
                        return r;
                    }
                    redirectHadAuthHeader = req.Headers.Authorization is not null;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(validZipBytes) };
                })),
                NullLogger<GitHubApiClient>.Instance);
            var redirDest = Path.Combine(tempRoot, "redir.zip");
            var redirRes = await redirectClient.DownloadVerifiedWorkflowArtifactAsync(
                "secret-pat-never-forward", "owner/repo", "main", Commit1, "serverforge-deploy.yml", redirDest, CancellationToken.None);
            Assert.True(redirRes.Found);
            Assert.Equal(21L, redirRes.ArtifactId);
            Assert.False(redirectHadAuthHeader);

            // 3. Untrusted 302 redirect target (evil host or IP literal) -> fail closed!
            var evilRedirectClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(req =>
                {
                    var uri = req.RequestUri!.AbsoluteUri;
                    if (uri.Contains("/actions/runs?"))
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                workflow_runs = new[]
                                {
                                    new
                                    {
                                        id = 922L,
                                        status = "completed",
                                        conclusion = "success",
                                        head_sha = Commit1,
                                        head_branch = "main",
                                        path = ".github/workflows/serverforge-deploy.yml"
                                    }
                                }
                            }))
                        };
                    if (uri.Contains("/artifacts") && !uri.EndsWith("/zip"))
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                artifacts = new[]
                                {
                                    new
                                    {
                                        id = 22L,
                                        name = "serverforge-release",
                                        expired = false,
                                        size_in_bytes = validZipBytes.Length,
                                        digest = $"sha256:{validSha}",
                                        archive_download_url = "https://api.github.com/repos/owner/repo/actions/artifacts/22/zip"
                                    }
                                }
                            }))
                        };
                    var r = new HttpResponseMessage(HttpStatusCode.Found);
                    r.Headers.Location = new Uri("https://evil.example.com/malicious.zip");
                    return r;
                })),
                NullLogger<GitHubApiClient>.Instance);
            var evilRedirRes = await evilRedirectClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", Path.Combine(tempRoot, "evil-redir.zip"), CancellationToken.None);
            Assert.False(evilRedirRes.Found);
            Assert.Equal(GitHubArtifactResolutionStatus.ProvenanceRejected, evilRedirRes.ResolutionStatus);
            Assert.Contains("redirect", evilRedirRes.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);

            // 4. Oversized artifact exceeding MaxArtifactDownloadBytes -> fail closed!
            var oversizeClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(req =>
                {
                    var uri = req.RequestUri!.AbsoluteUri;
                    if (uri.Contains("/actions/runs?"))
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                workflow_runs = new[]
                                {
                                    new
                                    {
                                        id = 923L,
                                        status = "completed",
                                        conclusion = "success",
                                        head_sha = Commit1,
                                        head_branch = "main",
                                        path = ".github/workflows/serverforge-deploy.yml"
                                    }
                                }
                            }))
                        };
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        {
                            artifacts = new[]
                            {
                                new
                                {
                                    id = 23L,
                                    name = "serverforge-release",
                                    expired = false,
                                    size_in_bytes = 999999,
                                    digest = $"sha256:{validSha}",
                                    archive_download_url = "https://api.github.com/repos/owner/repo/actions/artifacts/23/zip"
                                }
                            }
                        }))
                    };
                })),
                NullLogger<GitHubApiClient>.Instance)
            {
                MaxArtifactDownloadBytes = 1024
            };
            var oversizeRes = await oversizeClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", Path.Combine(tempRoot, "oversize.zip"), CancellationToken.None);
            Assert.False(oversizeRes.Found);
            Assert.Equal(GitHubArtifactResolutionStatus.ProvenanceRejected, oversizeRes.ResolutionStatus);
            Assert.Contains("exceeds maximum", oversizeRes.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);

            // 5. Non-ZIP payload whose SHA-256 matches digest -> rejected by archive integrity check!
            var nonZipBytes = System.Text.Encoding.UTF8.GetBytes("not a zip archive at all");
            var nonZipTemp = Path.Combine(tempRoot, "raw.bin");
            await File.WriteAllBytesAsync(nonZipTemp, nonZipBytes);
            var nonZipSha = await SafeProcessRunner.ComputeFileSha256Async(nonZipTemp);
            var nonZipClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(req =>
                {
                    var uri = req.RequestUri!.AbsoluteUri;
                    if (uri.Contains("/actions/runs?"))
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                workflow_runs = new[]
                                {
                                    new
                                    {
                                        id = 924L,
                                        status = "completed",
                                        conclusion = "success",
                                        head_sha = Commit1,
                                        head_branch = "main",
                                        path = ".github/workflows/serverforge-deploy.yml"
                                    }
                                }
                            }))
                        };
                    if (uri.Contains("/artifacts") && !uri.EndsWith("/zip"))
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                artifacts = new[]
                                {
                                    new
                                    {
                                        id = 24L,
                                        name = "serverforge-release",
                                        expired = false,
                                        size_in_bytes = nonZipBytes.Length,
                                        digest = $"sha256:{nonZipSha}",
                                        archive_download_url = "https://api.github.com/repos/owner/repo/actions/artifacts/24/zip"
                                    }
                                }
                            }))
                        };
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(nonZipBytes) };
                })),
                NullLogger<GitHubApiClient>.Instance);
            var nonZipDest = Path.Combine(tempRoot, "corrupt.zip");
            var nonZipRes = await nonZipClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", nonZipDest, CancellationToken.None);
            Assert.False(nonZipRes.Found);
            Assert.Equal(GitHubArtifactResolutionStatus.ProvenanceRejected, nonZipRes.ResolutionStatus);
            Assert.False(File.Exists(nonZipDest));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GitHubApiClient_ClassifiesRunning_PendingArtifact_FailedWorkflow_AndRateLimitStatesAccurately()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"sf-gh-states-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            // 1. In-progress workflow run -> WorkflowRunning
            var runningClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        workflow_runs = new[]
                        {
                            new
                            {
                                id = 930L,
                                status = "in_progress",
                                conclusion = (string?)null,
                                head_sha = Commit1,
                                head_branch = "main",
                                path = ".github/workflows/serverforge-deploy.yml"
                            }
                        }
                    }))
                })),
                NullLogger<GitHubApiClient>.Instance);
            var runningRes = await runningClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", Path.Combine(tempRoot, "run.zip"), CancellationToken.None);
            Assert.False(runningRes.Found);
            Assert.Equal(GitHubArtifactResolutionStatus.WorkflowRunning, runningRes.ResolutionStatus);

            // 2. Completed success run with empty artifact list -> WorkflowSucceededArtifactPending
            var pendingArtClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(req =>
                {
                    if (req.RequestUri!.AbsoluteUri.Contains("/actions/runs?"))
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(new
                            {
                                workflow_runs = new[]
                                {
                                    new
                                    {
                                        id = 931L,
                                        status = "completed",
                                        conclusion = "success",
                                        head_sha = Commit1,
                                        head_branch = "main",
                                        path = ".github/workflows/serverforge-deploy.yml"
                                    }
                                }
                            }))
                        };
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new { artifacts = Array.Empty<object>() }))
                    };
                })),
                NullLogger<GitHubApiClient>.Instance);
            var pendingRes = await pendingArtClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", Path.Combine(tempRoot, "pend.zip"), CancellationToken.None);
            Assert.False(pendingRes.Found);
            Assert.Equal(GitHubArtifactResolutionStatus.WorkflowSucceededArtifactPending, pendingRes.ResolutionStatus);

            // 3. Completed failed run -> WorkflowFailedOrCancelled
            var failedClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        workflow_runs = new[]
                        {
                            new
                            {
                                id = 932L,
                                status = "completed",
                                conclusion = "cancelled",
                                head_sha = Commit1,
                                head_branch = "main",
                                path = ".github/workflows/serverforge-deploy.yml"
                            }
                        }
                    }))
                })),
                NullLogger<GitHubApiClient>.Instance);
            var failedRes = await failedClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", Path.Combine(tempRoot, "fail.zip"), CancellationToken.None);
            Assert.False(failedRes.Found);
            Assert.Equal(GitHubArtifactResolutionStatus.WorkflowFailedOrCancelled, failedRes.ResolutionStatus);

            // 4. Rate limited HTTP 429 with Retry-After: 45
            var rlClient = new GitHubApiClient(
                new HttpClient(new StubHttpHandler(_ =>
                {
                    var r = new HttpResponseMessage((HttpStatusCode)429);
                    r.Headers.TryAddWithoutValidation("Retry-After", "45");
                    r.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                    return r;
                })),
                NullLogger<GitHubApiClient>.Instance);
            var headRl = await rlClient.CheckBranchHeadConditionalAsync("pat", "owner/repo", "main", "\"etag-1\"", CancellationToken.None);
            Assert.True(headRl.IsRateLimited);
            Assert.Equal(45, headRl.RetryAfterSeconds);

            var artRl = await rlClient.DownloadVerifiedWorkflowArtifactAsync(
                "pat", "owner/repo", "main", Commit1, "serverforge-deploy.yml", Path.Combine(tempRoot, "rl.zip"), CancellationToken.None);
            Assert.False(artRl.Found);
            Assert.Equal(GitHubArtifactResolutionStatus.RateLimited, artRl.ResolutionStatus);
            Assert.Equal(45, artRl.RetryAfterSeconds);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static ManagedApplication CreateStaticApp(string tempRoot) => new()
    {
        ProjectGroupId = Guid.NewGuid(),
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
