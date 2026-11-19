using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.Desktop.Services;
using ServerForge.Infrastructure.Persistence;

namespace ServerForge.Desktop.ViewModels;

public sealed record NavSectionItem(string Key, string Title, string ShortcutHint, string Description);

/// <summary>
/// Primary MVVM ViewModel driving all 12 native ServerForge Desktop navigation sections,
/// Dark/Light Fluent Theme switching, and explicit Live vs Demo mode separation.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ServerForgeStore _store;
    private readonly IGitHubClient _github;
    private readonly AgentTransportService _agentTransport;

    public MainViewModel(
        ServerForgeStore store,
        IGitHubClient github,
        AgentTransportService agentTransport)
    {
        _store = store;
        _github = github;
        _agentTransport = agentTransport;

        NavigationItems =
        [
            new("Overview", "Overview", "Ctrl+1", "Real-time summary of paired Windows Servers, active applications, and recent deployments."),
            new("Applications", "Applications", "Ctrl+2", "Manage projects, Next.js/Node/ASP.NET/Python/Static/Classic ASP services, and deployment adapters."),
            new("Deployments", "Deployments", "Ctrl+3", "12-state release pipeline history, commit SHAs, health verification, and instant rollbacks."),
            new("GitHub", "GitHub Repositories", "Ctrl+4", "Import personal/org repositories, authorize branches, inspect commits, and configure deploy-on-push."),
            new("Domains", "Domains & SSL", "Ctrl+5", "Custom domains, DNS verification, IIS bindings, binding conflict checks, and win-acme Let's Encrypt."),
            new("EnvVars", "Environment Variables", "Ctrl+6", "Isolated Dev/Staging/Production configs with DPAPI encryption, .env import, and redacted export."),
            new("Databases", "Databases", "Ctrl+7", "Standalone Microsoft SQL Server and PostgreSQL (local or external Neon) administration & backups."),
            new("Servers", "Servers", "Ctrl+8", "Paired Windows Server Agents, mTLS status, read-only IIS inventory, and explicit resource adoption."),
            new("Monitoring", "Monitoring", "Ctrl+9", "Live Windows PerformanceCounter CPU, RAM, disk, service status, and certificate expiry."),
            new("Logs", "Logs", "Ctrl+0", "Unified log viewer for deployments, stdout/stderr, IIS, Agent, health checks, and audit trails."),
            new("Backups", "Backups", "Ctrl+B", "Scheduled SQL/PostgreSQL backups, config snapshots, retention policies, and approval-gated restores."),
            new("Settings", "Settings", "Ctrl+,", "Theme mode, DPAPI credential vault, local admin profile, and explicitly separated Demo Mode.")
        ];

        SelectedSection = NavigationItems[0];
    }

    public IReadOnlyList<NavSectionItem> NavigationItems { get; }

    [ObservableProperty] private NavSectionItem _selectedSection;
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private bool _isDemoMode;
    [ObservableProperty] private string _statusMessage = "Live Production Mode — No fabricated deployments shown.";
    [ObservableProperty] private string _searchQuery = string.Empty;

    public ObservableCollection<ManagedApplication> Applications { get; } = [];
    public ObservableCollection<DeploymentRecord> Deployments { get; } = [];
    public ObservableCollection<GitHubRepositoryInfo> Repositories { get; } = [];
    public ObservableCollection<ManagedServer> Servers { get; } = [];
    public ObservableCollection<AuditLogRecord> AuditLogs { get; } = [];
    public ObservableCollection<DiscoveredIisSiteDto> DiscoveredIisSites { get; } = [];

    [RelayCommand]
    private void NavigateTo(string sectionKey)
    {
        var match = NavigationItems.FirstOrDefault(n => n.Key.Equals(sectionKey, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            SelectedSection = match;
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        System.Windows.Application.Current.ThemeMode = IsDarkTheme ? ThemeMode.Dark : ThemeMode.Light;
        StatusMessage = IsDarkTheme ? "Switched to Windows 11 Fluent Dark theme." : "Switched to Windows 11 Fluent Light theme.";
    }

    [RelayCommand]
    private async Task ToggleDemoModeAsync()
    {
        IsDemoMode = !IsDemoMode;
        if (IsDemoMode)
        {
            LoadExplicitDemoData();
            StatusMessage = "DEMO MODE ACTIVE (Simulated UI dataset — isolated from live SQLite and remote Windows Servers).";
        }
        else
        {
            await RefreshLiveDataAsync();
            StatusMessage = "Live Production Mode restored — showing real local SQLite & paired Agent state only.";
        }
    }

    [RelayCommand]
    private async Task RefreshLiveDataAsync()
    {
        if (IsDemoMode) return;

        Applications.Clear();
        Deployments.Clear();
        AuditLogs.Clear();

        var apps = await _store.GetApplicationsAsync();
        foreach (var app in apps) Applications.Add(app);

        var deploys = await _store.GetDeploymentHistoryAsync();
        foreach (var d in deploys) Deployments.Add(d);

        var audits = await _store.GetRecentAuditLogsAsync();
        foreach (var a in audits) AuditLogs.Add(a);
    }

    [RelayCommand]
    private async Task TriggerManualRedeployAsync(ManagedApplication? app)
    {
        if (app is null) return;
        if (IsDemoMode)
        {
            StatusMessage = $"[DEMO MODE] Simulated deployment queued for '{app.Name}'. Switch to Live Mode to deploy to a real server.";
            return;
        }

        var server = Servers.FirstOrDefault(s => s.Id == app.ServerId);
        if (server is null)
        {
            StatusMessage = $"Cannot deploy '{app.Name}': no paired Windows Server Agent is connected.";
            return;
        }

        var req = new TriggerDeploymentRequest(
            app.Id,
            app.LastDeployedCommitSha ?? "HEAD",
            app.Branch,
            "Manual redeploy from ServerForge Desktop",
            Environment.UserName,
            null,
            null,
            TriggeredAutomatically: false,
            RequestedBy: Environment.UserName);

        var result = await _agentTransport.TriggerDeploymentAsync(server, req, CancellationToken.None);
        if (result is not null)
        {
            Deployments.Insert(0, result);
            StatusMessage = $"Deployment {result.Id:N} completed with state: {result.State}.";
        }
    }

    private void LoadExplicitDemoData()
    {
        Applications.Clear();
        Deployments.Clear();
        DiscoveredIisSites.Clear();

        var demoServerId = Guid.NewGuid();
        var demoProjectId = Guid.NewGuid();
        var demoApp = new ManagedApplication
        {
            ProjectGroupId = demoProjectId,
            ServerId = demoServerId,
            Name = "[DEMO] Storefront Web (Next.js SSR)",
            Slug = "demo-storefront-web",
            RuntimeType = ApplicationRuntimeType.NextJsSsr,
            RepositoryFullName = "example-org/demo-storefront",
            Branch = "main",
            InternalPort = 3100,
            PrimaryDomain = "demo.local",
            IisSiteName = "SF-Demo-Storefront",
            WindowsServiceName = "SF_Demo_Storefront",
            AutoDeployEnabled = true,
            RepositoryExplicitlyAuthorized = true
        };
        Applications.Add(demoApp);

        DiscoveredIisSites.Add(new DiscoveredIisSiteDto(
            1, "LegacyBillingClassicASP", "Started", @"C:\inetpub\legacy-billing", "LegacyClassicAspPool",
            ["http://*:80:billing.internal"], ContainsClassicAspIndicators: true, ResourceManagementState.ReadOnlyUnmanaged));
    }
}
