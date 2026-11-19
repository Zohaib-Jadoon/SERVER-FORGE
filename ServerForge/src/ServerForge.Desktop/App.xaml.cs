using System.IO;
using System.Security.Cryptography;
using System.Windows;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        var services = new ServiceCollection();
        ConfigureServices(services, localDataRoot);
        _serviceProvider = services.BuildServiceProvider();

        // Initialize local SQLite metadata store (no external database required for ServerForge itself)
        var dbFactory = _serviceProvider.GetRequiredService<IDbContextFactory<ServerForgeDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.InitializeSqliteAsync();
        }

        InitializeSystemTray();

        var store = _serviceProvider.GetRequiredService<ServerForgeStore>();
        var hasAdmin = await store.HasLocalAdminAsync();

        if (!hasAdmin)
        {
            var wizard = _serviceProvider.GetRequiredService<SetupWizardWindow>();
            var completed = wizard.ShowDialog() == true;
            if (!completed)
            {
                Shutdown();
                return;
            }
        }

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
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
        menu.Items.Add("Open ServerForge", null, (_, _) =>
        {
            MainWindow?.Show();
            if (MainWindow is not null) MainWindow.WindowState = WindowState.Normal;
            MainWindow?.Activate();
        });
        menu.Items.Add("Exit Desktop (Agents keep running)", null, (_, _) => Shutdown());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) =>
        {
            MainWindow?.Show();
            MainWindow?.Activate();
        };
    }

    public void ShowDesktopNotification(string title, string message)
    {
        _trayIcon?.ShowBalloonTip(4000, title, message, System.Windows.Forms.ToolTipIcon.Info);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _serviceProvider?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
