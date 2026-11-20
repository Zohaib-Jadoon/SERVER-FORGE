using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ServerForge.Core.Interfaces;
using ServerForge.Databases.PostgreSql;
using ServerForge.Databases.SqlServer;
using ServerForge.Desktop.Services;
using ServerForge.Desktop.ViewModels;
using ServerForge.Desktop.Views;
using ServerForge.GitHub.Client;
using ServerForge.Infrastructure.Persistence;
using ServerForge.Security.Execution;
using ServerForge.Security.Secrets;
using Application = System.Windows.Application;

namespace ServerForge.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isExplicitExit;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalExceptionHandlers();

        var localDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ServerForge");
        Directory.CreateDirectory(localDataRoot);
        Directory.CreateDirectory(Path.Combine(localDataRoot, "logs"));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(localDataRoot, "logs", "desktop-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services, localDataRoot);
            _serviceProvider = services.BuildServiceProvider();

            // Initialize & safely migrate local SQLite metadata store (no external database required)
            var dbFactory = _serviceProvider.GetRequiredService<IDbContextFactory<ServerForgeDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var migration = await db.InitializeSqliteAsync();
                if (migration.AppliedVersions.Count > 0)
                {
                    Log.Information(
                        "SQLite metadata initialized/migrated from v{Prev} to v{Curr} (fresh={Fresh}, normalized={Normalized}, backup={Backup}).",
                        migration.PreviousVersion, migration.CurrentVersion, migration.WasFreshDatabase,
                        migration.NormalizedTimestampCount, migration.BackupFilePath ?? "none");
                }
            }

            InitializeSystemTray();

            var store = _serviceProvider.GetRequiredService<ServerForgeStore>();
            var existingAdmin = await store.GetLocalAdminAsync();

            if (existingAdmin is null)
            {
                var wizard = _serviceProvider.GetRequiredService<SetupWizardWindow>();
                var completed = wizard.ShowDialog() == true;
                if (!completed)
                {
                    ExitApplication();
                    return;
                }
                existingAdmin = await store.GetLocalAdminAsync();
            }

            var darkTheme = existingAdmin?.DarkThemeEnabled ?? true;
            ThemeManager.ApplyTheme(darkTheme);

            var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
            mainViewModel.IsDarkTheme = darkTheme;

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.Closing += (_, args) =>
            {
                if (!_isExplicitExit)
                {
                    args.Cancel = true;
                    mainWindow.Hide();
                    ShowDesktopNotification(
                        "ServerForge Minimized to Tray",
                        "ServerForge Desktop is still available in the system tray. Remote Agents keep deploying even if Desktop exits.");
                }
            };

            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            var protector = new DpapiSecretProtector();
            var safeMessage = protector.RedactSecrets(ex.Message);
            Log.Fatal(ex, "ServerForge Desktop failed during startup: {Message}", safeMessage);
            System.Windows.MessageBox.Show(
                $"ServerForge Desktop encountered a startup error:\n\n{safeMessage}\n\nDiagnostic logs were written to:\n{Path.Combine(localDataRoot, "logs")}",
                "ServerForge Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ExitApplication();
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var protector = new DpapiSecretProtector();
        var safeMsg = protector.RedactSecrets(e.Exception.Message);
        Log.Error(e.Exception, "Unhandled UI exception: {Message}", safeMsg);
        System.Windows.MessageBox.Show(
            $"An unexpected error occurred in ServerForge:\n\n{safeMsg}\n\nYour SQLite data and paired servers remain safe. Check %LOCALAPPDATA%\\ServerForge\\logs for details.",
            "ServerForge Error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            var protector = new DpapiSecretProtector();
            Log.Fatal(ex, "Fatal unhandled domain exception (IsTerminating={Term}): {Message}",
                e.IsTerminating, protector.RedactSecrets(ex.Message));
            Log.CloseAndFlush();
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var protector = new DpapiSecretProtector();
        Log.Error(e.Exception, "Unobserved background task exception: {Message}",
            protector.RedactSecrets(e.Exception.Message));
        e.SetObserved();
    }

    private static void ConfigureServices(IServiceCollection services, string localDataRoot)
    {
        var dbPath = Path.Combine(localDataRoot, "serverforge-desktop.db");
        services.AddLogging(b => b.AddSerilog(dispose: true));
        services.AddDbContextFactory<ServerForgeDbContext>(opts => opts.UseSqlite($"Data Source={dbPath}"));

        services.AddSingleton<ISecretProtector>(_ => new DpapiSecretProtector(DataProtectionScope.CurrentUser));
        services.AddSingleton<IPathBoundaryGuard, PathBoundaryGuard>();
        services.AddSingleton<ServerForgeStore>();
        services.AddSingleton<DesktopIdentityService>(_ => new DesktopIdentityService(localDataRoot));
        services.AddSingleton<AgentTransportService>();
        services.AddHttpClient<IGitHubClient, GitHubApiClient>();
        services.AddSingleton<ISqlServerManager, SqlServerManagementService>();
        services.AddSingleton<IPostgreSqlManager, PostgreSqlManagementService>();

        services.AddTransient<SetupWizardViewModel>();
        services.AddTransient<SetupWizardWindow>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private void InitializeSystemTray()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "ServerForge Desktop — Native Windows Deployment Platform",
            Visible = true,
            Icon = System.Drawing.SystemIcons.Shield
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open ServerForge", null, (_, _) => RestoreMainWindow());
        menu.Items.Add("Exit Desktop (Agents keep running)", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreMainWindow();
    }

    private void RestoreMainWindow()
    {
        if (MainWindow is null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void ExitApplication()
    {
        _isExplicitExit = true;
        Shutdown();
    }

    public void ShowDesktopNotification(string title, string message)
    {
        _trayIcon?.ShowBalloonTip(4000, title, message, System.Windows.Forms.ToolTipIcon.Info);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _serviceProvider?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
