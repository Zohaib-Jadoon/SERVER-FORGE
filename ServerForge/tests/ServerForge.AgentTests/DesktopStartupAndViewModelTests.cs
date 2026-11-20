using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.Desktop.Services;
using ServerForge.Desktop.ViewModels;
using ServerForge.Infrastructure.Persistence;
using ServerForge.Security.Secrets;
using Xunit;

namespace ServerForge.AgentTests;

/// <summary>
/// Automated non-GUI verification of Desktop first-launch SQLite initialization, SetupWizard validation,
/// DPAPI self-test, 13-screen navigation, theme persistence, CRUD persistence, log filtering, and isolated Demo Mode.
/// Note: Does NOT claim to validate interactive WPF rendering or visual layout (covered by manual Windows 11 checklist).
/// </summary>
public sealed class DesktopStartupAndViewModelTests
{
    private sealed class TestDbContextFactory(DbContextOptions<ServerForgeDbContext> options)
        : IDbContextFactory<ServerForgeDbContext>
    {
        public ServerForgeDbContext CreateDbContext() => new(options);
    }

    private static async Task<(ServerForgeStore Store, DpapiSecretProtector Protector, AgentTransportService Transport)> CreateDesktopEnvironmentAsync(string tempDir)
    {
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "serverforge-desktop.db");
        var opts = new DbContextOptionsBuilder<ServerForgeDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var factory = new TestDbContextFactory(opts);
        await using (var db = await factory.CreateDbContextAsync())
            await db.InitializeSqliteAsync();

        var protector = new DpapiSecretProtector(DataProtectionScope.CurrentUser);
        var store = new ServerForgeStore(factory, protector);
        var transport = new AgentTransportService(new DesktopIdentityService(tempDir));
        return (store, protector, transport);
    }

    [Fact]
    public async Task SetupWizard_ValidatesAdminPassword_VerifiesDpapi_AndPersistsProfileToSqlite()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sf-wizard-test-{Guid.NewGuid():N}");
        try
        {
            var (store, protector, transport) = await CreateDesktopEnvironmentAsync(tempDir);
            var githubMock = new Mock<IGitHubClient>();

            var wizard = new SetupWizardViewModel(store, protector, githubMock.Object, transport);
            bool? completionResult = null;
            wizard.WizardCompleted += (_, ok) => completionResult = ok;

            // Step 1 -> Step 2
            Assert.Equal(1, wizard.CurrentStep);
            await wizard.NextStepCommand.ExecuteAsync(null);
            Assert.Equal(2, wizard.CurrentStep);

            // Short password rejected
            wizard.AdminDisplayName = "Local Operator";
            wizard.AdminUsername = "operator";
            wizard.AdminPassword = "short";
            wizard.AdminPasswordConfirm = "short";
            await wizard.NextStepCommand.ExecuteAsync(null);
            Assert.Equal(2, wizard.CurrentStep);
            Assert.Contains("8 characters", wizard.StatusBanner);

            // Mismatched confirm password rejected
            wizard.AdminPassword = "StrongPassword123!";
            wizard.AdminPasswordConfirm = "DifferentPassword123!";
            await wizard.NextStepCommand.ExecuteAsync(null);
            Assert.Equal(2, wizard.CurrentStep);
            Assert.Contains("do not match", wizard.StatusBanner);

            // Valid matching password advances to Step 3 (DPAPI verification)
            wizard.AdminPasswordConfirm = "StrongPassword123!";
            await wizard.NextStepCommand.ExecuteAsync(null);
            Assert.Equal(3, wizard.CurrentStep);

            // Step 3 triggers real Windows DPAPI round-trip verification and advances to Step 4
            await wizard.NextStepCommand.ExecuteAsync(null);
            Assert.True(wizard.DpapiVerified);
            Assert.Equal(4, wizard.CurrentStep);

            // Step 4 -> Step 5 (skip GitHub PAT for now)
            await wizard.NextStepCommand.ExecuteAsync(null);
            Assert.Equal(5, wizard.CurrentStep);

            // Step 5 with AddServerNow = false jumps directly to Step 10
            wizard.AddServerNow = false;
            await wizard.NextStepCommand.ExecuteAsync(null);
            Assert.Equal(10, wizard.CurrentStep);
            Assert.Equal("Finish & Open Dashboard", wizard.PrimaryActionLabel);

            // Completing Step 10 persists PBKDF2-SHA512 admin profile to SQLite and fires WizardCompleted(true)
            await wizard.NextStepCommand.ExecuteAsync(null);
            Assert.True(completionResult);

            var savedAdmin = await store.GetLocalAdminAsync();
            Assert.NotNull(savedAdmin);
            Assert.Equal("operator", savedAdmin!.Username);
            Assert.Equal("Local Operator", savedAdmin.DisplayName);
            Assert.True(savedAdmin.DarkThemeEnabled);
            Assert.True(LocalPasswordHasher.VerifyPassword("StrongPassword123!", savedAdmin.PasswordHash, savedAdmin.PasswordSalt));
            Assert.False(LocalPasswordHasher.VerifyPassword("WrongPassword", savedAdmin.PasswordHash, savedAdmin.PasswordSalt));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MainViewModel_All13NavigationScreens_ThemePersistence_CrudAndLogFiltering_WorkAgainstSqlite()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sf-mainvm-test-{Guid.NewGuid():N}");
        try
        {
            var (store, _, transport) = await CreateDesktopEnvironmentAsync(tempDir);

            // Seed local admin profile for theme persistence verification
            var (hash, salt) = LocalPasswordHasher.HashPassword("AdminPass123!");
            await store.SaveLocalAdminAsync(new LocalAdminProfile
            {
                DisplayName = "Admin",
                Username = "admin",
                PasswordHash = hash,
                PasswordSalt = salt,
                DarkThemeEnabled = true
            });

            var githubMock = new Mock<IGitHubClient>();
            var sqlMock = new Mock<ISqlServerManager>();
            sqlMock.Setup(m => m.TestConnectionAsync(It.IsAny<DatabaseServerRecord>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((DatabaseServerRecord r, string _, CancellationToken _) =>
                    new DatabaseHealthDto(r.Id, r.DisplayName, r.Engine, Reachable: true, LatencyMs: 4.2, DatabaseCount: 2, ErrorMessage: null));
            var pgMock = new Mock<IPostgreSqlManager>();

            var vm = new MainViewModel(store, githubMock.Object, transport, sqlMock.Object, pgMock.Object);

            // 1. Verify all 13 native navigation screens route cleanly
            Assert.Equal(13, vm.NavigationItems.Count);
            string[] expectedKeys =
            [
                "Overview", "Projects", "Applications", "GitHub", "Deployments",
                "Domains", "EnvVars", "Databases", "Servers", "Monitoring",
                "Logs", "Backups", "Settings"
            ];
            foreach (var key in expectedKeys)
            {
                vm.NavigateToCommand.Execute(key);
                Assert.Equal(key, vm.SelectedSection.Key);
            }

            // 2. Theme toggle updates IsDarkTheme and persists to SQLite LocalAdminProfile
            Assert.True(vm.IsDarkTheme);
            await vm.ToggleThemeCommand.ExecuteAsync(null);
            Assert.False(vm.IsDarkTheme);
            var persistedAdmin = await store.GetLocalAdminAsync();
            Assert.False(persistedAdmin!.DarkThemeEnabled);

            // 3. Create Project, Register Application, Save Env Var, Add Domain, Save DB, Save Backup
            vm.NewProjectName = "Core Platform";
            vm.NewProjectDescription = "Primary services";
            await vm.CreateProjectCommand.ExecuteAsync(null);
            Assert.Single(vm.Projects);

            vm.NewAppName = "Orders API";
            vm.NewAppSlug = "orders-api";
            vm.NewAppRuntime = ApplicationRuntimeType.AspNetCore;
            vm.NewAppRepo = "acme/orders-api";
            vm.NewAppBranch = "main";
            vm.NewAppPort = 5200;
            vm.NewAppDomain = "orders.local";
            await vm.CreateApplicationCommand.ExecuteAsync(null);
            Assert.Single(vm.Applications);
            Assert.Equal(string.Empty, vm.ErrorMessage);

            vm.NewEnvKey = "STRIPE_KEY";
            vm.NewEnvValue = "sk_test_123456";
            vm.NewEnvIsSecret = true;
            await vm.SaveEnvVarCommand.ExecuteAsync(null);
            Assert.Single(vm.EnvironmentVariables);

            vm.NewDomainHostname = "orders.example.internal";
            await vm.AddDomainBindingCommand.ExecuteAsync(null);
            Assert.Single(vm.Domains);
            Assert.Contains("orders.example.internal", vm.DnsGuidanceOutput);

            vm.NewDbEngine = DatabaseEngineType.PostgreSqlExternalManaged;
            Assert.Equal(5432, vm.NewDbPort);
            Assert.True(vm.NewDbExternalManaged);

            vm.NewDbEngine = DatabaseEngineType.SqlServer;
            Assert.Equal(1433, vm.NewDbPort);
            vm.NewDbName = "Prod SQL";
            vm.NewDbConnectionString = "Server=localhost;Database=master;Trusted_Connection=True;";
            await vm.SaveAndTestDatabaseCommand.ExecuteAsync(null);
            Assert.Single(vm.DatabaseServers);
            Assert.Contains("Connected to Prod SQL", vm.DbTestResultText);

            vm.NewBackupTarget = "OrdersDB";
            await vm.SaveBackupJobCommand.ExecuteAsync(null);
            Assert.Single(vm.BackupJobs);

            // 4. Reload fresh MainViewModel from the SAME SQLite file and verify persistence + log filtering
            var vmReloaded = new MainViewModel(store, githubMock.Object, transport, sqlMock.Object, pgMock.Object);
            await vmReloaded.RefreshLiveDataCommand.ExecuteAsync(null);
            Assert.Single(vmReloaded.Projects);
            Assert.Single(vmReloaded.Applications);
            Assert.Single(vmReloaded.Domains);
            Assert.Single(vmReloaded.DatabaseServers);
            Assert.Single(vmReloaded.BackupJobs);
            Assert.NotEmpty(vmReloaded.AuditLogs);
            Assert.Equal(vmReloaded.AuditLogs.Count, vmReloaded.FilteredAuditLogs.Count);

            vmReloaded.LogFilterQuery = "OrdersDB";
            Assert.NotEmpty(vmReloaded.FilteredAuditLogs);
            Assert.All(vmReloaded.FilteredAuditLogs, l =>
                Assert.True(
                    l.Action.Contains("OrdersDB", StringComparison.OrdinalIgnoreCase) ||
                    l.TargetResource.Contains("OrdersDB", StringComparison.OrdinalIgnoreCase) ||
                    l.Details.Contains("OrdersDB", StringComparison.OrdinalIgnoreCase)));

            vmReloaded.LogFilterQuery = "definitely-no-match-xyz";
            Assert.Empty(vmReloaded.FilteredAuditLogs);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MainViewModel_DemoMode_StaysStrictlyIsolatedFromSqlite()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sf-demo-test-{Guid.NewGuid():N}");
        try
        {
            var (store, _, transport) = await CreateDesktopEnvironmentAsync(tempDir);
            var vm = new MainViewModel(
                store,
                new Mock<IGitHubClient>().Object,
                transport,
                new Mock<ISqlServerManager>().Object,
                new Mock<IPostgreSqlManager>().Object);

            Assert.False(vm.IsDemoMode);
            await vm.ToggleDemoModeCommand.ExecuteAsync(null);
            Assert.True(vm.IsDemoMode);
            Assert.NotEmpty(vm.Projects);
            Assert.NotEmpty(vm.Applications);

            // SQLite MUST remain untouched by Demo Mode
            Assert.Empty(await store.GetProjectsAsync());
            Assert.Empty(await store.GetApplicationsAsync());

            // Exiting Demo Mode reloads clean SQLite state
            await vm.ToggleDemoModeCommand.ExecuteAsync(null);
            Assert.False(vm.IsDemoMode);
            Assert.Empty(vm.Projects);
            Assert.Empty(vm.Applications);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
