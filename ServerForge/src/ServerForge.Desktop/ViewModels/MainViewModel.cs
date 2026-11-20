using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.Core.Validation;
using ServerForge.Desktop.Services;
using ServerForge.Infrastructure.Persistence;

namespace ServerForge.Desktop.ViewModels;

public sealed record NavSectionItem(string Key, string Title, string ShortcutHint, string Description);

/// <summary>
/// Full MVVM ViewModel driving all 13 native WPF ServerForge Desktop screens with
/// persisted SQLite state, live Agent reconnection, forms, validation, and operational actions.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ServerForgeStore _store;
    private readonly IGitHubClient _github;
    private readonly AgentTransportService _agentTransport;
    private readonly ISqlServerManager _sqlManager;
    private readonly IPostgreSqlManager _pgManager;

    public MainViewModel(
        ServerForgeStore store,
        IGitHubClient github,
        AgentTransportService agentTransport,
        ISqlServerManager sqlManager,
        IPostgreSqlManager pgManager)
    {
        _store = store;
        _github = github;
        _agentTransport = agentTransport;
        _sqlManager = sqlManager;
        _pgManager = pgManager;

        NavigationItems =
        [
            new("Overview", "Overview", "Ctrl+1", "Connected Windows Servers, application counts, recent deployments, and live OS telemetry."),
            new("Projects", "Projects", "Ctrl+2", "Group related frontends, APIs, workers, and services under multi-service projects."),
            new("Applications", "Applications", "Ctrl+3", "Create, configure, deploy, inspect, and safely remove managed applications across 9 runtimes."),
            new("GitHub", "GitHub", "Ctrl+4", "Import repositories, inspect commits, select branches, and authorize autonomous deploy-on-push."),
            new("Deployments", "Deployments", "Ctrl+5", "12-state release pipeline history, verified artifact deploys, health checks, and safe rollbacks."),
            new("Domains", "Domains & SSL", "Ctrl+6", "Custom domains, DNS verification, manual DNS instructions, IIS conflicts, and win-acme SSL."),
            new("EnvVars", "Environment Variables", "Ctrl+7", "Isolated Dev/Staging/Production DPAPI-encrypted secrets, .env import, and Agent sync."),
            new("Databases", "Databases", "Ctrl+8", "Standalone SQL Server and local/external PostgreSQL (Neon) connections, inventory, and backups."),
            new("Servers", "Servers", "Ctrl+9", "Paired Windows Server Agents, mTLS pairing, cert rotation, and read-only IIS site adoption."),
            new("Monitoring", "Monitoring", "Ctrl+M", "Real Windows PerformanceCounter CPU, RAM, disk, service status, and certificate expiration."),
            new("Logs", "Logs", "Ctrl+L", "Searchable deployment, agent, IIS, Windows Service, and audit logs."),
            new("Backups", "Backups", "Ctrl+B", "Backup schedules, retention policies, on-demand backups, and approval-gated recovery."),
            new("Settings", "Settings", "Ctrl+,", "DPAPI GitHub credentials, Fluent Dark/Light theme, local security, and isolated Demo Mode.")
        ];

        SelectedSection = NavigationItems[0];
    }

    public IReadOnlyList<NavSectionItem> NavigationItems { get; }
    public IReadOnlyList<ApplicationRuntimeType> AvailableRuntimes { get; } = Enum.GetValues<ApplicationRuntimeType>();
    public IReadOnlyList<EnvironmentTier> AvailableEnvTiers { get; } = Enum.GetValues<EnvironmentTier>();
    public IReadOnlyList<DatabaseEngineType> AvailableDbEngines { get; } = Enum.GetValues<DatabaseEngineType>();

    [ObservableProperty] private NavSectionItem _selectedSection;
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private bool _isDemoMode;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Live Production Mode — Connected to local SQLite vault.";
    [ObservableProperty] private string _errorMessage = string.Empty;

    // Live Telemetry & Diagnostics
    [ObservableProperty] private ServerTelemetrySnapshot? _latestTelemetry;
    [ObservableProperty] private ServerDiagnosticsSnapshot? _latestDiagnostics;

    // Collections for all 13 screens
    public ObservableCollection<ProjectGroup> Projects { get; } = [];
    public ObservableCollection<ManagedServer> Servers { get; } = [];
    public ObservableCollection<ManagedApplication> Applications { get; } = [];
    public ObservableCollection<DeploymentRecord> Deployments { get; } = [];
    public ObservableCollection<GitHubRepositoryInfo> Repositories { get; } = [];
    public ObservableCollection<GitHubCommitInfo> RecentCommits { get; } = [];
    public ObservableCollection<DomainBindingRecord> Domains { get; } = [];
    public ObservableCollection<EnvironmentVariableRecord> EnvironmentVariables { get; } = [];
    public ObservableCollection<DatabaseServerRecord> DatabaseServers { get; } = [];
    public ObservableCollection<BackupJobRecord> BackupJobs { get; } = [];
    public ObservableCollection<AuditLogRecord> AuditLogs { get; } = [];
    public ObservableCollection<AuditLogRecord> FilteredAuditLogs { get; } = [];
    public ObservableCollection<DiscoveredIisSiteDto> DiscoveredIisSites { get; } = [];

    // Selected Entities
    [ObservableProperty] private ProjectGroup? _selectedProject;
    [ObservableProperty] private ManagedServer? _selectedServer;
    [ObservableProperty] private ManagedApplication? _selectedApplication;
    [ObservableProperty] private DeploymentRecord? _selectedDeployment;
    [ObservableProperty] private EnvironmentVariableRecord? _selectedEnvVar;
    [ObservableProperty] private GitHubRepositoryInfo? _selectedRepository;

    // Form State — Projects
    [ObservableProperty] private string _newProjectName = string.Empty;
    [ObservableProperty] private string _newProjectDescription = string.Empty;

    // Form State — New Application
    [ObservableProperty] private string _newAppName = string.Empty;
    [ObservableProperty] private string _newAppSlug = string.Empty;
    [ObservableProperty] private ApplicationRuntimeType _newAppRuntime = ApplicationRuntimeType.NextJsSsr;
    [ObservableProperty] private string _newAppRepo = string.Empty;
    [ObservableProperty] private string _newAppBranch = "main";
    [ObservableProperty] private string _newAppWorkingDir = ".";
    [ObservableProperty] private int _newAppPort = 3100;
    [ObservableProperty] private string _newAppDomain = string.Empty;
    [ObservableProperty] private bool _newAppAuthorizeRepo = true;
    [ObservableProperty] private bool _newAppAutoDeploy = true;

    // Form State — Manual Verified Deployment Trigger
    [ObservableProperty] private string _deployCommitSha = string.Empty;
    [ObservableProperty] private string _deployBranch = "main";
    [ObservableProperty] private string _deployArtifactPath = string.Empty;
    [ObservableProperty] private string _deployArtifactSha256 = string.Empty;

    // Form State — Pair New Server
    [ObservableProperty] private string _pairServerName = "Windows Server";
    [ObservableProperty] private string _pairServerHost = "10.0.0.10";
    [ObservableProperty] private int _pairServerPort = 9443;
    [ObservableProperty] private string _pairOneTimeCode = string.Empty;
    [ObservableProperty] private string _pairThumbprint = string.Empty;

    // Form State — Domains & SSL
    [ObservableProperty] private string _newDomainHostname = string.Empty;
    [ObservableProperty] private bool _newDomainHttps = true;
    [ObservableProperty] private bool _newDomainWinAcme = true;
    [ObservableProperty] private string _dnsGuidanceOutput = string.Empty;

    // Form State — Environment Variables
    [ObservableProperty] private EnvironmentTier _selectedEnvTier = EnvironmentTier.Production;
    [ObservableProperty] private string _newEnvKey = string.Empty;
    [ObservableProperty] private string _newEnvValue = string.Empty;
    [ObservableProperty] private bool _newEnvIsSecret = true;
    [ObservableProperty] private string _dotEnvImportText = string.Empty;

    // Form State — Databases
    [ObservableProperty] private string _newDbName = string.Empty;
    [ObservableProperty] private DatabaseEngineType _newDbEngine = DatabaseEngineType.SqlServer;
    [ObservableProperty] private string _newDbHost = "localhost";
    [ObservableProperty] private int _newDbPort = 1433;
    [ObservableProperty] private string _newDbConnectionString = string.Empty;
    [ObservableProperty] private bool _newDbExternalManaged;
    [ObservableProperty] private string _dbTestResultText = string.Empty;

    // Form State — Backups, Logs, Settings & GitHub
    [ObservableProperty] private string _newBackupTarget = string.Empty;
    [ObservableProperty] private string _newBackupDestination = @"C:\ProgramData\ServerForge\Backups";
    [ObservableProperty] private string _newBackupCron = "0 2 * * *";
    [ObservableProperty] private string _logFilterQuery = string.Empty;
    [ObservableProperty] private string _gitHubSearchQuery = string.Empty;
    [ObservableProperty] private string _settingsGitHubPat = string.Empty;

    public int ConnectedServerCount => Servers.Count(s => s.Status == ServerConnectionStatus.Connected);
    public int TotalApplicationCount => Applications.Count;
    public int SuccessfulDeploymentCount => Deployments.Count(d => d.State == DeploymentState.Successful);

    partial void OnSelectedApplicationChanged(ManagedApplication? value)
    {
        if (value is not null)
        {
            DeployBranch = value.Branch;
            if (!IsDemoMode)
                _ = LoadSelectedAppEnvVarsAsync();
        }
    }

    partial void OnSelectedEnvTierChanged(EnvironmentTier value)
    {
        if (SelectedApplication is not null && !IsDemoMode)
            _ = LoadSelectedAppEnvVarsAsync();
    }

    partial void OnNewDbEngineChanged(DatabaseEngineType value)
    {
        NewDbPort = value == DatabaseEngineType.SqlServer ? 1433 : 5432;
        if (value == DatabaseEngineType.PostgreSqlExternalManaged)
            NewDbExternalManaged = true;
    }

    partial void OnLogFilterQueryChanged(string value) => ApplyAuditLogFilter();

    [RelayCommand]
    private void NavigateTo(string sectionKey)
    {
        var match = NavigationItems.FirstOrDefault(n => n.Key.Equals(sectionKey, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            SelectedSection = match;
    }

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        IsDarkTheme = !IsDarkTheme;
        ThemeManager.ApplyTheme(IsDarkTheme);

        try
        {
            var admin = await _store.GetLocalAdminAsync();
            if (admin is not null)
            {
                admin.DarkThemeEnabled = IsDarkTheme;
                await _store.SaveLocalAdminAsync(admin);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Theme applied but failed to save preference: {ex.Message}";
            return;
        }

        StatusMessage = IsDarkTheme ? "Fluent Dark theme active and saved." : "Fluent Light theme active and saved.";
    }

    [RelayCommand]
    private async Task RefreshLiveDataAsync()
    {
        if (IsDemoMode) return;
        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            Projects.Clear();
            foreach (var p in await _store.GetProjectsAsync()) Projects.Add(p);
            SelectedProject ??= Projects.FirstOrDefault();

            Servers.Clear();
            foreach (var s in await _store.GetServersAsync()) Servers.Add(s);
            SelectedServer ??= Servers.FirstOrDefault();

            Applications.Clear();
            foreach (var a in await _store.GetApplicationsAsync()) Applications.Add(a);
            SelectedApplication ??= Applications.FirstOrDefault();

            Deployments.Clear();
            foreach (var d in await _store.GetDeploymentHistoryAsync()) Deployments.Add(d);
            SelectedDeployment ??= Deployments.FirstOrDefault();

            Domains.Clear();
            foreach (var dom in await _store.GetDomainsAsync()) Domains.Add(dom);

            DatabaseServers.Clear();
            foreach (var db in await _store.GetDatabaseServersAsync()) DatabaseServers.Add(db);

            BackupJobs.Clear();
            foreach (var b in await _store.GetBackupJobsAsync()) BackupJobs.Add(b);

            AuditLogs.Clear();
            foreach (var log in await _store.GetRecentAuditLogsAsync(200)) AuditLogs.Add(log);
            ApplyAuditLogFilter();

            if (SelectedApplication is not null)
                await LoadSelectedAppEnvVarsAsync();

            // Automatically reconnect to persisted paired servers on startup
            foreach (var srv in Servers)
            {
                try
                {
                    var telemetry = await _agentTransport.FetchTelemetryAsync(srv, CancellationToken.None);
                    if (telemetry is not null)
                    {
                        srv.Status = ServerConnectionStatus.Connected;
                        srv.LastSeenUtc = DateTimeOffset.UtcNow;
                        await _store.UpsertServerAsync(srv, "DesktopAutoReconnect");
                        if (srv == SelectedServer) LatestTelemetry = telemetry;
                    }
                }
                catch
                {
                    srv.Status = ServerConnectionStatus.Disconnected;
                }
            }

            OnPropertyChanged(nameof(ConnectedServerCount));
            OnPropertyChanged(nameof(TotalApplicationCount));
            OnPropertyChanged(nameof(SuccessfulDeploymentCount));
            StatusMessage = $"Live state synced from SQLite ({Servers.Count} paired server(s), {Applications.Count} app(s)).";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateProjectAsync()
    {
        if (string.IsNullOrWhiteSpace(NewProjectName))
        {
            ErrorMessage = "Project name is required.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var slug = NormalizeSlug(NewProjectName);
            var proj = new ProjectGroup { Name = NewProjectName.Trim(), Slug = slug, Description = NewProjectDescription.Trim() };
            await _store.UpsertProjectAsync(proj, Environment.UserName);
            if (SelectedServer is not null && !IsDemoMode)
            {
                try { await _agentTransport.SyncProjectAsync(SelectedServer, proj, CancellationToken.None); }
                catch { /* Persisted locally; synced when Agent reconnects */ }
            }
            Projects.Add(proj);
            SelectedProject = proj;
            NewProjectName = string.Empty;
            NewProjectDescription = string.Empty;
            StatusMessage = $"Created project '{proj.Name}'.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to create project: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateApplicationAsync()
    {
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var slug = string.IsNullOrWhiteSpace(NewAppSlug)
                ? NormalizeSlug(NewAppName)
                : NewAppSlug.Trim().ToLowerInvariant();

            var project = SelectedProject ?? Projects.FirstOrDefault();
            if (project is null)
            {
                project = await _store.EnsureDefaultProjectAsync(Environment.UserName);
                Projects.Add(project);
                SelectedProject = project;
            }

            var serverId = SelectedServer?.Id ?? Guid.Empty;

            var app = new ManagedApplication
            {
                ProjectGroupId = project.Id,
                ServerId = serverId,
                Name = NewAppName.Trim(),
                Slug = slug,
                RuntimeType = NewAppRuntime,
                ManagementState = NewAppRuntime == ApplicationRuntimeType.ClassicAsp
                    ? ResourceManagementState.ReadOnlyUnmanaged
                    : ResourceManagementState.ServerForgeCreated,
                RepositoryFullName = NewAppRepo.Trim(),
                Branch = NewAppBranch.Trim(),
                WorkingDirectory = NewAppWorkingDir.Trim(),
                InternalPort = NewAppPort,
                PrimaryDomain = NewAppDomain.Trim(),
                RepositoryExplicitlyAuthorized = NewAppAuthorizeRepo,
                AutoDeployEnabled = NewAppAutoDeploy,
                IisSiteName = $"SF-{slug}",
                IisAppPoolName = $"SF-{slug}-Pool",
                WindowsServiceName = $"SF_{slug.Replace('-', '_')}",
                ReleasesRootPath = $@"C:\ServerForgeApps\{slug}",
                PersistentDataPath = $@"C:\ServerForgeApps\{slug}\data"
            };

            var check = ApplicationConfigValidator.Validate(app);
            if (!check.IsValid)
            {
                ErrorMessage = string.Join(" ", check.Errors);
                return;
            }

            await _store.UpsertApplicationAsync(app, Environment.UserName);
            var agentSyncNote = "Saved locally.";
            if (SelectedServer is not null && !IsDemoMode)
            {
                try
                {
                    await _agentTransport.SyncProjectAsync(SelectedServer, project, CancellationToken.None);
                    var req = new UpsertApplicationRequest(
                        app.Id, app.ProjectGroupId, app.Name, app.Slug, app.RuntimeType, app.RepositoryFullName,
                        app.Branch, app.WorkingDirectory, app.AutoDeployEnabled, app.RepositoryExplicitlyAuthorized,
                        app.SourceMode, app.GitHubWorkflowFileName, app.BuildCommand, app.StartCommand,
                        app.RuntimeVersion, app.InternalPort, app.IisSiteName, app.IisAppPoolName,
                        app.WindowsServiceName, app.PrimaryDomain, app.HealthCheckPath, app.HealthCheckTimeoutSeconds,
                        app.RetainHealthyReleasesCount, app.AutoRollbackOnFailure);
                    await _agentTransport.RegisterOrUpdateApplicationAsync(SelectedServer, req, CancellationToken.None);
                    agentSyncNote = $"Synced to Agent '{SelectedServer.DisplayName}'.";
                }
                catch (Exception syncEx)
                {
                    agentSyncNote = $"Saved locally; Agent sync deferred ({syncEx.Message}).";
                }
            }

            Applications.Add(app);
            SelectedApplication = app;
            NewAppName = string.Empty;
            NewAppSlug = string.Empty;
            OnPropertyChanged(nameof(TotalApplicationCount));
            StatusMessage = $"Registered '{app.Name}' ({app.RuntimeType}) under '{project.Name}'. {agentSyncNote}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to register application: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedApplicationFromManagementAsync()
    {
        if (SelectedApplication is null)
        {
            ErrorMessage = "Select an application to remove from management.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var target = SelectedApplication;
            await _store.RemoveApplicationFromManagementAsync(target.Id, Environment.UserName);
            if (SelectedServer is not null && !IsDemoMode)
            {
                try { await _agentTransport.RemoveApplicationFromManagementAsync(SelectedServer, target.Id, CancellationToken.None); }
                catch { /* Offline agent will reconcile on next sync */ }
            }
            Applications.Remove(target);
            SelectedApplication = Applications.FirstOrDefault();
            OnPropertyChanged(nameof(TotalApplicationCount));
            StatusMessage = $"Removed '{target.Name}' from ServerForge management. Hosted files, IIS site, and databases were NOT deleted.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to remove application: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadGitHubRepositoriesAsync()
    {
        ErrorMessage = string.Empty;
        var pat = await _store.GetVaultSecretAsync("github:pat");
        if (string.IsNullOrWhiteSpace(pat))
        {
            ErrorMessage = "Save a valid GitHub Fine-Grained PAT in Settings first.";
            return;
        }
        IsBusy = true;
        try
        {
            var repos = await _github.ListRepositoriesAsync(pat, GitHubSearchQuery, CancellationToken.None);
            Repositories.Clear();
            foreach (var r in repos) Repositories.Add(r);
            StatusMessage = $"Loaded {Repositories.Count} accessible GitHub repository(ies).";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"GitHub API error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadSelectedRepoCommitsAsync()
    {
        if (SelectedRepository is null)
        {
            ErrorMessage = "Select a GitHub repository first.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var pat = await _store.GetVaultSecretAsync("github:pat");
            if (string.IsNullOrWhiteSpace(pat))
            {
                ErrorMessage = "Save a valid GitHub Fine-Grained PAT in Settings first.";
                return;
            }

            var commits = await _github.GetRecentCommitsAsync(pat, SelectedRepository.FullName, SelectedRepository.DefaultBranch, 15, CancellationToken.None);
            RecentCommits.Clear();
            foreach (var c in commits) RecentCommits.Add(c);
            NewAppRepo = SelectedRepository.FullName;
            NewAppBranch = SelectedRepository.DefaultBranch;
            if (RecentCommits.Count > 0)
                DeployCommitSha = RecentCommits[0].Sha;
            StatusMessage = $"Loaded {RecentCommits.Count} commit(s) from {SelectedRepository.FullName}@{SelectedRepository.DefaultBranch}.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load commits: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ImportSelectedRepository()
    {
        if (SelectedRepository is null)
        {
            ErrorMessage = "Select a GitHub repository to import first.";
            return;
        }
        ErrorMessage = string.Empty;
        var repoShortName = SelectedRepository.FullName.Contains('/')
            ? SelectedRepository.FullName.Split('/')[^1]
            : SelectedRepository.FullName;
        NewAppRepo = SelectedRepository.FullName;
        NewAppBranch = SelectedRepository.DefaultBranch;
        NewAppName = repoShortName;
        NewAppSlug = NormalizeSlug(repoShortName);
        NewAppAuthorizeRepo = true;
        NavigateTo("Applications");
        StatusMessage = $"Imported '{SelectedRepository.FullName}' into the New Application form. Review runtime/port and click Register Application.";
    }

    [RelayCommand]
    private async Task PairNewServerAsync()
    {
        if (string.IsNullOrWhiteSpace(PairServerHost) ||
            string.IsNullOrWhiteSpace(PairOneTimeCode) ||
            string.IsNullOrWhiteSpace(PairThumbprint))
        {
            ErrorMessage = "Enter VPN/private IP, one-time pairing code, and pinned server certificate thumbprint.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var resp = await _agentTransport.PairWithAgentAsync(
                PairServerHost.Trim(), PairServerPort, PairOneTimeCode.Trim(), PairThumbprint.Trim(), CancellationToken.None);

            if (!resp.Accepted)
            {
                ErrorMessage = resp.FailureReason ?? "Agent rejected pairing.";
                return;
            }

            var srv = new ManagedServer
            {
                AgentId = resp.AgentId,
                DisplayName = string.IsNullOrWhiteSpace(PairServerName) ? resp.Hostname : PairServerName.Trim(),
                HostAddress = PairServerHost.Trim(),
                AgentPort = PairServerPort,
                PinnedServerCertThumbprint = resp.AgentCertificateThumbprint,
                ClientCertThumbprint = _agentTransport.GetCurrentClientThumbprint(),
                Status = ServerConnectionStatus.Connected,
                LastSeenUtc = DateTimeOffset.UtcNow
            };
            await _store.UpsertServerAsync(srv, Environment.UserName);
            Servers.Add(srv);
            SelectedServer = srv;
            PairOneTimeCode = string.Empty;
            OnPropertyChanged(nameof(ConnectedServerCount));
            StatusMessage = $"Paired & persisted '{srv.DisplayName}' ({srv.HostAddress}:{srv.AgentPort}).";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Pairing failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task InspectSelectedServerIisAsync()
    {
        if (SelectedServer is null)
        {
            ErrorMessage = "Select a paired server first.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            LatestDiagnostics = await _agentTransport.FetchDiagnosticsAsync(SelectedServer, CancellationToken.None);
            DiscoveredIisSites.Clear();
            if (LatestDiagnostics is not null)
            {
                foreach (var s in LatestDiagnostics.ExistingIisSites) DiscoveredIisSites.Add(s);
                StatusMessage = $"Inspected {DiscoveredIisSites.Count} IIS site(s) on {SelectedServer.DisplayName} (pre-existing sites protected read-only).";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Diagnostics error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RotateSelectedServerCertificateAsync()
    {
        if (SelectedServer is null)
        {
            ErrorMessage = "Select a paired server first.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var res = await _agentTransport.RotateDesktopCertificateAsync(SelectedServer, "Operator initiated rotation", CancellationToken.None);
            if (res.Rotated)
            {
                SelectedServer.ClientCertThumbprint = res.NewPinnedThumbprint;
                await _store.UpsertServerAsync(SelectedServer, Environment.UserName);
                StatusMessage = $"Rotated Desktop mTLS certificate for {SelectedServer.DisplayName}.";
            }
            else
            {
                ErrorMessage = $"Certificate rotation failed: {res.FailureReason}";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Certificate rotation error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TriggerSelectedApplicationDeploymentAsync()
    {
        if (SelectedApplication is null || SelectedServer is null)
        {
            ErrorMessage = "Select both a paired server and a managed application to start a deployment.";
            return;
        }
        if (string.IsNullOrWhiteSpace(DeployCommitSha) || DeployCommitSha.Trim().Length < 40)
        {
            ErrorMessage = "Enter the full 40-character Git commit SHA for the verified build artifact.";
            return;
        }
        var isManualPackage = SelectedApplication.SourceMode == DeploymentSourceMode.ManualSignedPackage;
        if (isManualPackage && (string.IsNullOrWhiteSpace(DeployArtifactPath) || string.IsNullOrWhiteSpace(DeployArtifactSha256)))
        {
            ErrorMessage = "ManualSignedPackage mode requires both the verified local package path and its 64-character SHA-256 digest.";
            return;
        }

        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var req = new TriggerDeploymentRequest(
                ApplicationId: SelectedApplication.Id,
                CommitSha: DeployCommitSha.Trim(),
                Branch: string.IsNullOrWhiteSpace(DeployBranch) ? SelectedApplication.Branch : DeployBranch.Trim(),
                CommitMessage: "Manual verified deployment from ServerForge Desktop",
                CommitAuthor: Environment.UserName,
                ArtifactDownloadUrl: isManualPackage ? DeployArtifactPath.Trim() : null,
                ExpectedArtifactSha256: string.IsNullOrWhiteSpace(DeployArtifactSha256) ? null : DeployArtifactSha256.Trim(),
                TriggeredAutomatically: false,
                RequestedBy: Environment.UserName,
                ArtifactTrustSource: isManualPackage ? "ManualSignedPackage" : null);

            var rec = await _agentTransport.TriggerDeploymentAsync(SelectedServer, req, CancellationToken.None);
            if (rec is not null)
            {
                await _store.RecordDeploymentAsync(rec);
                Deployments.Insert(0, rec);
                SelectedDeployment = rec;
                OnPropertyChanged(nameof(SuccessfulDeploymentCount));
                StatusMessage = rec.State == DeploymentState.Successful
                    ? $"Deployment succeeded for '{SelectedApplication.Name}' ({rec.CommitSha[..8]})."
                    : $"Deployment finished with state {rec.State}: {rec.ErrorSummary}";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Deployment trigger failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddDomainBindingAsync()
    {
        if (SelectedApplication is null)
        {
            ErrorMessage = "Select an application first.";
            return;
        }
        var check = DomainValidator.ValidateHostname(NewDomainHostname, allowLocalTestHostname: true);
        if (!check.IsValid)
        {
            ErrorMessage = string.Join(" ", check.Errors);
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var rec = new DomainBindingRecord
            {
                ApplicationId = SelectedApplication.Id,
                ServerId = SelectedApplication.ServerId,
                Hostname = NewDomainHostname.Trim(),
                HttpsEnabled = NewDomainHttps,
                UseWinAcmeLetsEncrypt = NewDomainWinAcme
            };
            await _store.UpsertDomainAsync(rec, Environment.UserName);
            var syncNote = "Saved locally.";
            if (SelectedServer is not null && !IsDemoMode)
            {
                try
                {
                    await _agentTransport.ConfigureDomainAsync(
                        SelectedServer,
                        SelectedApplication.Id,
                        new ConfigureDomainRequest(rec.Hostname, rec.HttpPort, rec.HttpsPort, rec.HttpsEnabled, rec.UseWinAcmeLetsEncrypt),
                        CancellationToken.None);
                    syncNote = $"Synced to Agent '{SelectedServer.DisplayName}'.";
                }
                catch (Exception syncEx)
                {
                    syncNote = $"Saved locally; Agent sync deferred ({syncEx.Message}).";
                }
            }
            Domains.Add(rec);
            DnsGuidanceOutput = DomainValidator.BuildManualDnsInstructions(rec.Hostname, SelectedServer?.HostAddress ?? "<SERVER_PUBLIC_IP>");
            NewDomainHostname = string.Empty;
            StatusMessage = $"Saved domain binding '{rec.Hostname}'. {syncNote} Public DNS must be configured at your DNS provider.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save domain binding: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadSelectedAppEnvVarsAsync()
    {
        EnvironmentVariables.Clear();
        if (SelectedApplication is null) return;
        try
        {
            var list = await _store.GetEnvironmentVariablesAsync(SelectedApplication.Id, SelectedEnvTier);
            foreach (var item in list) EnvironmentVariables.Add(item);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load environment variables: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveEnvVarAsync()
    {
        if (SelectedApplication is null || string.IsNullOrWhiteSpace(NewEnvKey))
        {
            ErrorMessage = "Select an application and enter an environment variable key.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var key = NewEnvKey.Trim();
            var val = NewEnvValue;
            var isSecret = NewEnvIsSecret;
            await _store.SaveEnvironmentVariableAsync(
                SelectedApplication.Id, SelectedEnvTier, key, val, isSecret, false, Environment.UserName);

            var syncNote = string.Empty;
            if (SelectedServer is not null && !IsDemoMode)
            {
                try
                {
                    await _agentTransport.ConfigureEnvVarsAsync(
                        SelectedServer,
                        SelectedApplication.Id,
                        new ConfigureEnvVarsRequest(SelectedEnvTier, [new EnvVarItemDto(key, val, isSecret, false)]),
                        CancellationToken.None);
                    syncNote = " Synced to paired Agent.";
                }
                catch (Exception syncEx)
                {
                    syncNote = $" Agent sync deferred ({syncEx.Message}).";
                }
            }

            NewEnvKey = string.Empty;
            NewEnvValue = string.Empty;
            await LoadSelectedAppEnvVarsAsync();
            StatusMessage = $"Environment variable '{key}' encrypted with Windows DPAPI and saved.{syncNote}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save environment variable: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedEnvVarAsync()
    {
        if (SelectedEnvVar is null)
        {
            ErrorMessage = "Select an environment variable to delete.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var key = SelectedEnvVar.Key;
            await _store.DeleteEnvironmentVariableAsync(SelectedEnvVar.Id, Environment.UserName);
            await LoadSelectedAppEnvVarsAsync();
            StatusMessage = $"Deleted environment variable '{key}'.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to delete environment variable: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportDotEnvAsync()
    {
        if (SelectedApplication is null || string.IsNullOrWhiteSpace(DotEnvImportText))
        {
            ErrorMessage = "Select an application and paste .env key=value lines to import.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var parsed = EnvironmentFileParser.ParseDotEnv(DotEnvImportText);
            var dtoItems = new List<EnvVarItemDto>();
            foreach (var (k, v) in parsed)
            {
                await _store.SaveEnvironmentVariableAsync(SelectedApplication.Id, SelectedEnvTier, k, v, isSecret: true, false, Environment.UserName);
                dtoItems.Add(new EnvVarItemDto(k, v, true, false));
            }
            if (SelectedServer is not null && !IsDemoMode && dtoItems.Count > 0)
            {
                try
                {
                    await _agentTransport.ConfigureEnvVarsAsync(
                        SelectedServer, SelectedApplication.Id,
                        new ConfigureEnvVarsRequest(SelectedEnvTier, dtoItems), CancellationToken.None);
                }
                catch { /* Synced locally; can retry when Agent reconnects */ }
            }
            DotEnvImportText = string.Empty;
            await LoadSelectedAppEnvVarsAsync();
            StatusMessage = $"Imported {parsed.Count} variable(s) into {SelectedEnvTier} (DPAPI encrypted).";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to import .env variables: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAndTestDatabaseAsync()
    {
        if (string.IsNullOrWhiteSpace(NewDbName) || string.IsNullOrWhiteSpace(NewDbConnectionString))
        {
            ErrorMessage = "Database server display name and connection string are required.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var rec = new DatabaseServerRecord
            {
                DisplayName = NewDbName.Trim(),
                Engine = NewDbEngine,
                HostOrInstance = NewDbHost.Trim(),
                Port = NewDbPort,
                IsExternalManagedInfrastructure = NewDbExternalManaged || NewDbEngine == DatabaseEngineType.PostgreSqlExternalManaged,
                ExposePublicly = false
            };
            await _store.UpsertDatabaseServerAsync(rec, NewDbConnectionString.Trim(), Environment.UserName);
            DatabaseServers.Add(rec);

            var health = rec.Engine == DatabaseEngineType.SqlServer
                ? await _sqlManager.TestConnectionAsync(rec, NewDbConnectionString.Trim(), CancellationToken.None)
                : await _pgManager.TestConnectionAsync(rec, NewDbConnectionString.Trim(), CancellationToken.None);

            DbTestResultText = health.Reachable
                ? $"Connected to {rec.DisplayName} ({health.LatencyMs:F1} ms)."
                : $"Saved encrypted connection; probe returned: {health.ErrorMessage}";
            NewDbConnectionString = string.Empty;
            StatusMessage = DbTestResultText;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Database save/test error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveBackupJobAsync()
    {
        if (string.IsNullOrWhiteSpace(NewBackupTarget))
        {
            ErrorMessage = "Backup target name is required.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var job = new BackupJobRecord
            {
                TargetName = NewBackupTarget.Trim(),
                DestinationPath = NewBackupDestination.Trim(),
                ScheduleCron = NewBackupCron.Trim(),
                RetentionDays = 14
            };
            await _store.UpsertBackupJobAsync(job, Environment.UserName);
            BackupJobs.Add(job);
            NewBackupTarget = string.Empty;
            StatusMessage = $"Scheduled backup job for '{job.TargetName}'.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to save backup schedule: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncAgentAuditLogsAsync()
    {
        if (SelectedServer is null)
        {
            ErrorMessage = "Select a paired server to fetch remote Agent audit logs.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var remoteLogs = await _agentTransport.FetchAuditLogsAsync(SelectedServer, CancellationToken.None);
            var localLogs = await _store.GetRecentAuditLogsAsync(200);
            var combined = localLogs.Concat(remoteLogs)
                .OrderByDescending(l => l.TimestampUtc)
                .Take(300)
                .ToList();

            AuditLogs.Clear();
            foreach (var entry in combined) AuditLogs.Add(entry);
            ApplyAuditLogFilter();
            StatusMessage = $"Loaded {remoteLogs.Count} remote Agent audit event(s) from '{SelectedServer.DisplayName}'.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to fetch Agent audit logs: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveGitHubPatInSettingsAsync()
    {
        if (string.IsNullOrWhiteSpace(SettingsGitHubPat))
        {
            ErrorMessage = "Enter a GitHub Fine-Grained Personal Access Token.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var token = SettingsGitHubPat.Trim();
            var res = await _github.ValidateTokenAsync(token, CancellationToken.None);
            if (!res.IsValid)
            {
                ErrorMessage = res.ErrorMessage ?? "Invalid GitHub token.";
                return;
            }
            await _store.SaveVaultSecretAsync("github:pat", token, Environment.UserName);
            if (SelectedServer is not null)
            {
                try { await _agentTransport.SyncGitHubPatAsync(SelectedServer, token, CancellationToken.None); }
                catch { /* Synced locally; will sync to agent when online */ }
            }
            SettingsGitHubPat = string.Empty;
            StatusMessage = $"Verified & DPAPI-encrypted GitHub PAT for @{res.Login}.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"GitHub token verification failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RollbackSelectedApplicationAsync()
    {
        if (SelectedApplication is null || SelectedServer is null)
        {
            ErrorMessage = "Select both a paired server and a managed application to roll back.";
            return;
        }
        ErrorMessage = string.Empty;
        IsBusy = true;
        try
        {
            var rec = await _agentTransport.RollbackApplicationAsync(SelectedServer, SelectedApplication.Id, CancellationToken.None);
            if (rec is not null)
            {
                await _store.RecordDeploymentAsync(rec);
                Deployments.Insert(0, rec);
                SelectedDeployment = rec;
                StatusMessage = $"Rolled back '{SelectedApplication.Name}' to previous healthy release. Database schema untouched.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Rollback failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleDemoModeAsync()
    {
        IsDemoMode = !IsDemoMode;
        if (IsDemoMode)
        {
            LoadExplicitDemoData();
            StatusMessage = "DEMO MODE ACTIVE (Simulated UI dataset — strictly isolated from SQLite and live Windows Servers).";
        }
        else
        {
            await RefreshLiveDataAsync();
        }
    }

    private void ApplyAuditLogFilter()
    {
        FilteredAuditLogs.Clear();
        var q = LogFilterQuery?.Trim() ?? string.Empty;
        foreach (var entry in AuditLogs)
        {
            if (string.IsNullOrEmpty(q) ||
                entry.Action.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                entry.Actor.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                entry.TargetResource.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                entry.Details.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                FilteredAuditLogs.Add(entry);
            }
        }
    }

    private static string NormalizeSlug(string raw)
    {
        var chars = raw.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var collapsed = new string(chars);
        while (collapsed.Contains("--", StringComparison.Ordinal))
            collapsed = collapsed.Replace("--", "-", StringComparison.Ordinal);
        return collapsed.Trim('-');
    }

    private void LoadExplicitDemoData()
    {
        Projects.Clear();
        Applications.Clear();
        Deployments.Clear();
        DiscoveredIisSites.Clear();

        var demoProj = new ProjectGroup { Name = "[DEMO] Commerce Platform", Slug = "demo-commerce", Description = "Simulated multi-service project" };
        Projects.Add(demoProj);
        SelectedProject = demoProj;
        Applications.Add(new ManagedApplication
        {
            ProjectGroupId = demoProj.Id,
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
        });
        DiscoveredIisSites.Add(new DiscoveredIisSiteDto(
            1, "LegacyBillingClassicASP", "Started", @"C:\inetpub\legacy-billing", "LegacyClassicAspPool",
            ["http://*:80:billing.internal"], ContainsClassicAspIndicators: true, ResourceManagementState.ReadOnlyUnmanaged));
    }
}
