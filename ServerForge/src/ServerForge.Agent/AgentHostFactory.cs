using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using ServerForge.Agent.Configuration;
using ServerForge.Agent.Workers;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.Core.Validation;
using ServerForge.Databases.PostgreSql;
using ServerForge.Databases.SqlServer;
using ServerForge.Deployments.Adapters;
using ServerForge.Deployments.Engine;
using ServerForge.GitHub.Client;
using ServerForge.IIS.Certificates;
using ServerForge.IIS.Management;
using ServerForge.IIS.Services;
using ServerForge.Infrastructure.Persistence;
using ServerForge.Monitoring;
using ServerForge.Security.Certificates;
using ServerForge.Security.Execution;
using ServerForge.Security.Secrets;

namespace ServerForge.Agent;

public sealed class AgentHostHandle : IAsyncDisposable
{
    public AgentHostHandle(
        WebApplication app,
        AgentOptions options,
        AgentIdentityStore identityStore,
        OneTimePairingTokenService pairingService,
        X509Certificate2 serverCertificate,
        string? initialPairingCode)
    {
        App = app;
        Options = options;
        IdentityStore = identityStore;
        PairingService = pairingService;
        ServerCertificate = serverCertificate;
        InitialPairingCode = initialPairingCode;
    }

    public WebApplication App { get; }
    public AgentOptions Options { get; }
    public AgentIdentityStore IdentityStore { get; }
    public OneTimePairingTokenService PairingService { get; }
    public X509Certificate2 ServerCertificate { get; }
    public string? InitialPairingCode { get; }

    public int GetBoundPort()
    {
        var server = App.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is not null)
        {
            foreach (var addr in addresses)
            {
                if (Uri.TryCreate(addr, UriKind.Absolute, out var uri) && uri.Port > 0)
                    return uri.Port;
            }
        }
        return Options.Port;
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync().ConfigureAwait(false);
        ServerCertificate.Dispose();
    }
}

/// <summary>
/// Builds and initializes the ServerForge Agent HTTPS/mTLS WebApplication host.
/// Shared by both the production Windows Service entry point and live Kestrel mTLS integration tests.
/// </summary>
public static class AgentHostFactory
{
    public static async Task<AgentHostHandle> CreateAndInitializeAsync(
        string[] args,
        AgentOptions? overrideOptions = null,
        bool enableWindowsService = true,
        bool enableAutoDeployWorker = true,
        bool configureSerilog = true,
        Action<IServiceCollection>? configureTestServices = null,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = enableWindowsService && WindowsServiceHelpers.IsWindowsService()
                ? AppContext.BaseDirectory
                : default
        });

        var agentOptions = overrideOptions
            ?? builder.Configuration.GetSection("Agent").Get<AgentOptions>()
            ?? new AgentOptions();

        if (string.IsNullOrWhiteSpace(agentOptions.ServiceName))
            agentOptions.ServiceName = "ServerForgeAgent";

        if (enableWindowsService)
            builder.Host.UseWindowsService(opts => opts.ServiceName = agentOptions.ServiceName);

        Directory.CreateDirectory(agentOptions.DataRoot);
        Directory.CreateDirectory(agentOptions.AppsRoot);
        Directory.CreateDirectory(Path.Combine(agentOptions.DataRoot, "logs"));

        if (configureSerilog)
        {
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration)
                .WriteTo.Console()
                .WriteTo.File(
                    Path.Combine(agentOptions.DataRoot, "logs", "agent-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14)
                .CreateLogger();

            builder.Host.UseSerilog();
        }

        var identityStore = new AgentIdentityStore(agentOptions);
        var serverCert = identityStore.LoadOrCreateServerCertificate();
        var pairingService = new OneTimePairingTokenService();
        string? initialPairingCode = null;

        if (args.Contains("--generate-pairing-code", StringComparer.OrdinalIgnoreCase) || !identityStore.State.IsPaired)
        {
            var (code, expires) = pairingService.GeneratePairingCode(TimeSpan.FromMinutes(30));
            initialPairingCode = code;
            var pairingBannerPath = Path.Combine(agentOptions.DataRoot, "pairing-code.txt");
            File.WriteAllText(pairingBannerPath,
                $"ServerForge Agent Pairing Code: {code}{Environment.NewLine}" +
                $"Expires (UTC): {expires:O}{Environment.NewLine}" +
                $"Server Cert Thumbprint: {serverCert.Thumbprint}{Environment.NewLine}");

            if (configureSerilog)
                Log.Information("Agent initial pairing code generated and written to {Path} (expires {Expires:O}).", pairingBannerPath, expires);
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            var bindIp = IPAddress.TryParse(agentOptions.BindAddress, out var parsedIp) ? parsedIp : IPAddress.Loopback;
            kestrel.Listen(bindIp, agentOptions.Port, listenOptions =>
            {
                listenOptions.UseHttps(httpsOptions =>
                {
                    httpsOptions.ServerCertificate = serverCert;
                    httpsOptions.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                    httpsOptions.ClientCertificateValidation = (_, _, _) => true; // Verified fail-closed below
                });
            });
        });

        builder.Services.AddAuthentication(CertificateAuthenticationDefaults.AuthenticationScheme)
            .AddCertificate(options =>
            {
                options.AllowedCertificateTypes = CertificateTypes.All;
                options.RevocationMode = X509RevocationMode.NoCheck;
                options.Events = new CertificateAuthenticationEvents
                {
                    OnCertificateValidated = context =>
                    {
                        if (!identityStore.State.IsPaired ||
                            !MutualTlsCertificateManager.VerifyPinnedCertificateThumbprint(
                                context.ClientCertificate,
                                identityStore.State.PairedDesktopCertThumbprint!))
                        {
                            context.Fail("Client certificate does not match paired ServerForge Desktop identity.");
                            return Task.CompletedTask;
                        }

                        var claims = new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, identityStore.State.PairedDesktopInstanceId ?? "desktop"),
                            new Claim(ClaimTypes.Name, context.ClientCertificate.Subject)
                        };
                        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, context.Scheme.Name));
                        context.Success();
                        return Task.CompletedTask;
                    }
                };
            });

        builder.Services.AddAuthorization();

        var dbPath = Path.Combine(agentOptions.DataRoot, "agent-state.db");
        builder.Services.AddDbContextFactory<ServerForgeDbContext>(opts => opts.UseSqlite($"Data Source={dbPath}"));
        builder.Services.Configure<AgentOptions>(opts =>
        {
            opts.ServiceName = agentOptions.ServiceName;
            opts.BindAddress = agentOptions.BindAddress;
            opts.Port = agentOptions.Port;
            opts.DataRoot = agentOptions.DataRoot;
            opts.AppsRoot = agentOptions.AppsRoot;
            opts.GitHubPollIntervalSeconds = agentOptions.GitHubPollIntervalSeconds;
            opts.RequireVpnOrPrivateNetwork = agentOptions.RequireVpnOrPrivateNetwork;
        });
        builder.Services.AddSingleton(identityStore);
        builder.Services.AddSingleton(pairingService);
        builder.Services.AddSingleton<ISecretProtector>(_ => new DpapiSecretProtector(
            enableWindowsService ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser));
        builder.Services.AddSingleton<IPathBoundaryGuard, PathBoundaryGuard>();
        builder.Services.AddSingleton<ServerForgeStore>();
        builder.Services.AddSingleton<IDeploymentStatePersistence>(sp => sp.GetRequiredService<ServerForgeStore>());
        builder.Services.AddSingleton<IIisManager, IisAdministrationService>();
        builder.Services.AddSingleton<IWindowsServiceManager, WindowsServiceManager>();
        builder.Services.AddSingleton<AcmeCertificateService>();
        builder.Services.AddSingleton<ISqlServerManager, SqlServerManagementService>();
        builder.Services.AddSingleton<IPostgreSqlManager, PostgreSqlManagementService>();
        builder.Services.AddHttpClient<IGitHubClient, GitHubApiClient>();
        builder.Services.AddHttpClient<IServerTelemetryProvider, WindowsTelemetryService>();
        builder.Services.AddHttpClient("DeploymentHealth", c => c.Timeout = TimeSpan.FromSeconds(15));

        builder.Services.AddSingleton<IAppDeploymentAdapter, NextJsDeploymentAdapter>();
        builder.Services.AddSingleton<IAppDeploymentAdapter>(sp =>
            new NodeServiceDeploymentAdapter(ApplicationRuntimeType.NestJsApi, sp.GetRequiredService<IIisManager>(), sp.GetRequiredService<IWindowsServiceManager>()));
        builder.Services.AddSingleton<IAppDeploymentAdapter>(sp =>
            new NodeServiceDeploymentAdapter(ApplicationRuntimeType.ExpressApi, sp.GetRequiredService<IIisManager>(), sp.GetRequiredService<IWindowsServiceManager>()));
        builder.Services.AddSingleton<IAppDeploymentAdapter>(sp =>
            new NodeServiceDeploymentAdapter(ApplicationRuntimeType.NodeJsGeneric, sp.GetRequiredService<IIisManager>(), sp.GetRequiredService<IWindowsServiceManager>()));
        builder.Services.AddSingleton<IAppDeploymentAdapter>(sp =>
            new StaticSiteDeploymentAdapter(ApplicationRuntimeType.ReactViteStatic, sp.GetRequiredService<IIisManager>()));
        builder.Services.AddSingleton<IAppDeploymentAdapter>(sp =>
            new StaticSiteDeploymentAdapter(ApplicationRuntimeType.StaticHtml, sp.GetRequiredService<IIisManager>()));
        builder.Services.AddSingleton<IAppDeploymentAdapter, AspNetCoreDeploymentAdapter>();
        builder.Services.AddSingleton<IAppDeploymentAdapter, ClassicAspDeploymentAdapter>();
        builder.Services.AddSingleton<IAppDeploymentAdapter, PythonFastApiDeploymentAdapter>();
        builder.Services.AddSingleton<IDeploymentEngine>(sp => new DeploymentOrchestrator(
            sp.GetServices<IAppDeploymentAdapter>(),
            sp.GetRequiredService<IPathBoundaryGuard>(),
            sp.GetRequiredService<ISecretProtector>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("DeploymentHealth"),
            sp.GetRequiredService<ILogger<DeploymentOrchestrator>>(),
            sp.GetRequiredService<IDeploymentStatePersistence>()));

        if (enableAutoDeployWorker)
            builder.Services.AddHostedService<GitHubAutoDeployWorker>();

        configureTestServices?.Invoke(builder.Services);

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ServerForgeDbContext>>();
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await db.InitializeSqliteAsync(cancellationToken);

            // Crash recovery: cleanly fail any deployments interrupted by an Agent restart
            var store = scope.ServiceProvider.GetRequiredService<ServerForgeStore>();
            var recovered = await store.RecoverInterruptedDeploymentsAsync(cancellationToken);
            if (recovered > 0 && configureSerilog)
                Log.Warning("Recovered {Count} interrupted deployment(s) after Agent restart; previous healthy releases preserved.", recovered);
        }

        app.UseAuthentication();
        app.UseAuthorization();

        MapAgentEndpoints(app, agentOptions, identityStore, serverCert);

        return new AgentHostHandle(app, agentOptions, identityStore, pairingService, serverCert, initialPairingCode);
    }

    private static void MapAgentEndpoints(
        WebApplication app,
        AgentOptions agentOptions,
        AgentIdentityStore identityStore,
        X509Certificate2 serverCert)
    {
        // Unauthenticated ONLY while unpaired via single-use expiring code
        app.MapPost("/api/v1/pairing/complete", (
            PairingInitRequest req,
            OneTimePairingTokenService tokens,
            AgentIdentityStore store) =>
        {
            if (store.State.IsPaired)
                return Results.Conflict(new PairingInitResponse(false, store.State.AgentId, Environment.MachineName, "", "", "Agent is already paired."));

            if (!tokens.TryValidateAndConsume(req.OneTimePairingCode))
                return Results.Unauthorized();

            using var desktopCert = X509Certificate2.CreateFromPem(req.DesktopClientCertPem);
            store.CompletePairing(req.DesktopInstanceId, desktopCert.Thumbprint);

            return Results.Ok(new PairingInitResponse(
                Accepted: true,
                AgentId: store.State.AgentId,
                Hostname: Environment.MachineName,
                AgentServerCertPem: serverCert.ExportCertificatePem(),
                AgentCertificateThumbprint: serverCert.Thumbprint,
                FailureReason: null));
        });

        // ---- All operational routes require Mutual TLS authentication ----
        var api = app.MapGroup("/api/v1").RequireAuthorization();

        api.MapPost("/security/rotate-desktop-cert", async (
            RotateDesktopCertificateRequest req,
            AgentIdentityStore idStore,
            ServerForgeStore sfStore,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            try
            {
                using var newCert = X509Certificate2.CreateFromPem(req.NewDesktopClientCertPem);
                var thumbprint = idStore.RotateDesktopCertificate(req.DesktopInstanceId, newCert.Thumbprint);
                await sfStore.RecordAuditAsync(new AuditLogRecord
                {
                    Actor = user.Identity?.Name ?? "DesktopAdmin",
                    Action = "Security.DesktopCertRotated",
                    TargetResource = req.DesktopInstanceId,
                    Details = $"Rotated pinned client certificate thumbprint to {thumbprint[..12]}... Reason: {req.Reason}"
                }, ct);
                return Results.Ok(new RotateDesktopCertificateResponse(true, thumbprint, DateTimeOffset.UtcNow, null));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new RotateDesktopCertificateResponse(false, "", DateTimeOffset.UtcNow, ex.Message));
            }
        });

        api.MapPost("/github/credentials", async (
            SyncGitHubCredentialRequest req,
            ISecretProtector protector,
            AgentIdentityStore idStore,
            ServerForgeStore sfStore,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.PersonalAccessToken))
                return Results.BadRequest("Personal Access Token is required.");

            var encrypted = protector.Protect(req.PersonalAccessToken.Trim(), "agent:github-pat");
            idStore.StoreProtectedGitHubPat(encrypted);
            await sfStore.RecordAuditAsync(new AuditLogRecord
            {
                Actor = user.Identity?.Name ?? "DesktopAdmin",
                Action = "GitHub.AgentCredentialSynced",
                TargetResource = "agent:github-pat",
                Details = "GitHub PAT encrypted with DPAPI inside ACL-protected Agent store."
            }, ct);
            return Results.Ok(new { synced = true });
        });

        api.MapGet("/projects", async (ServerForgeStore store, CancellationToken ct) =>
            Results.Ok(await store.GetProjectsAsync(ct)));

        api.MapPost("/projects", async (
            ProjectGroup project,
            ServerForgeStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(project.Name) || string.IsNullOrWhiteSpace(project.Slug))
                return Results.BadRequest(new { errors = new[] { "Project name and slug are required." } });

            await store.UpsertProjectAsync(project, user.Identity?.Name ?? "DesktopAdmin", ct);
            return Results.Created($"/api/v1/projects/{project.Id}", project);
        });

        api.MapGet("/applications", async (ServerForgeStore store, CancellationToken ct) =>
            Results.Ok(await store.GetApplicationsAsync(ct)));

        api.MapPost("/applications", async (
            UpsertApplicationRequest req,
            ServerForgeStore store,
            IPathBoundaryGuard pathGuard,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var actor = user.Identity?.Name ?? "DesktopAdmin";
            Guid resolvedProjectGroupId;
            if (req.ProjectGroupId == Guid.Empty)
            {
                var defaultProject = await store.EnsureDefaultProjectAsync(actor, ct);
                resolvedProjectGroupId = defaultProject.Id;
            }
            else
            {
                var existingProject = await store.GetProjectByIdAsync(req.ProjectGroupId, ct);
                if (existingProject is null)
                {
                    return Results.BadRequest(new
                    {
                        errors = new[] { $"ProjectGroup '{req.ProjectGroupId}' does not exist on this agent. Register the ProjectGroup first or omit ProjectGroupId to use the authorized default project." }
                    });
                }
                resolvedProjectGroupId = existingProject.Id;
            }

            var appRoot = pathGuard.EnsureWithinRoot(agentOptions.AppsRoot, req.Slug);
            var entity = new ManagedApplication
            {
                Id = req.Id ?? Guid.NewGuid(),
                ProjectGroupId = resolvedProjectGroupId,
                ServerId = Guid.Empty,
                Name = req.Name.Trim(),
                Slug = req.Slug.Trim().ToLowerInvariant(),
                RuntimeType = req.RuntimeType,
                ManagementState = req.RuntimeType == ApplicationRuntimeType.ClassicAsp
                    ? ResourceManagementState.ReadOnlyUnmanaged
                    : ResourceManagementState.ServerForgeCreated,
                RepositoryFullName = req.RepositoryFullName.Trim(),
                Branch = req.Branch.Trim(),
                WorkingDirectory = string.IsNullOrWhiteSpace(req.WorkingDirectory) ? "." : req.WorkingDirectory.Trim(),
                AutoDeployEnabled = req.AutoDeployEnabled,
                RepositoryExplicitlyAuthorized = req.RepositoryExplicitlyAuthorized,
                SourceMode = req.SourceMode,
                GitHubWorkflowFileName = req.GitHubWorkflowFileName,
                BuildCommand = req.BuildCommand ?? string.Empty,
                StartCommand = req.StartCommand ?? string.Empty,
                RuntimeVersion = req.RuntimeVersion ?? string.Empty,
                InternalPort = req.InternalPort,
                IisSiteName = string.IsNullOrWhiteSpace(req.IisSiteName) ? $"SF-{req.Slug}" : req.IisSiteName.Trim(),
                IisAppPoolName = string.IsNullOrWhiteSpace(req.IisAppPoolName) ? $"SF-{req.Slug}-Pool" : req.IisAppPoolName.Trim(),
                WindowsServiceName = string.IsNullOrWhiteSpace(req.WindowsServiceName) ? $"SF_{req.Slug.Replace('-', '_')}" : req.WindowsServiceName.Trim(),
                PrimaryDomain = req.PrimaryDomain?.Trim() ?? string.Empty,
                HealthCheckPath = string.IsNullOrWhiteSpace(req.HealthCheckPath) ? "/health" : req.HealthCheckPath.Trim(),
                HealthCheckTimeoutSeconds = Math.Clamp(req.HealthCheckTimeoutSeconds, 5, 180),
                RetainHealthyReleasesCount = Math.Clamp(req.RetainHealthyReleasesCount, 2, 20),
                AutoRollbackOnFailure = req.AutoRollbackOnFailure,
                ReleasesRootPath = appRoot,
                PersistentDataPath = Path.Combine(appRoot, "data")
            };

            var validation = ApplicationConfigValidator.Validate(entity);
            if (!validation.IsValid)
                return Results.BadRequest(new { errors = validation.Errors });

            await store.UpsertApplicationAsync(entity, actor, ct);
            return Results.Created($"/api/v1/applications/{entity.Id}", entity);
        });

        api.MapPut("/applications/{id:guid}", async (
            Guid id,
            UpsertApplicationRequest req,
            ServerForgeStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var existing = await store.GetApplicationByIdAsync(id, ct);
            if (existing is null) return Results.NotFound();

            if (req.ProjectGroupId != Guid.Empty && req.ProjectGroupId != existing.ProjectGroupId)
            {
                var targetProject = await store.GetProjectByIdAsync(req.ProjectGroupId, ct);
                if (targetProject is null)
                {
                    return Results.BadRequest(new
                    {
                        errors = new[] { $"ProjectGroup '{req.ProjectGroupId}' does not exist on this agent." }
                    });
                }
                existing.ProjectGroupId = targetProject.Id;
            }

            existing.Name = req.Name.Trim();
            existing.RuntimeType = req.RuntimeType;
            existing.RepositoryFullName = req.RepositoryFullName.Trim();
            existing.Branch = req.Branch.Trim();
            existing.WorkingDirectory = string.IsNullOrWhiteSpace(req.WorkingDirectory) ? "." : req.WorkingDirectory.Trim();
            existing.AutoDeployEnabled = req.AutoDeployEnabled;
            existing.RepositoryExplicitlyAuthorized = req.RepositoryExplicitlyAuthorized;
            existing.SourceMode = req.SourceMode;
            existing.GitHubWorkflowFileName = req.GitHubWorkflowFileName;
            existing.BuildCommand = req.BuildCommand ?? string.Empty;
            existing.StartCommand = req.StartCommand ?? string.Empty;
            existing.RuntimeVersion = req.RuntimeVersion ?? string.Empty;
            existing.InternalPort = req.InternalPort;
            existing.PrimaryDomain = req.PrimaryDomain?.Trim() ?? string.Empty;
            existing.HealthCheckPath = string.IsNullOrWhiteSpace(req.HealthCheckPath) ? "/health" : req.HealthCheckPath.Trim();
            existing.AutoRollbackOnFailure = req.AutoRollbackOnFailure;

            var validation = ApplicationConfigValidator.Validate(existing);
            if (!validation.IsValid)
                return Results.BadRequest(new { errors = validation.Errors });

            await store.UpsertApplicationAsync(existing, user.Identity?.Name ?? "DesktopAdmin", ct);
            return Results.Ok(existing);
        });

        api.MapPost("/applications/{id:guid}/autodeploy", async (
            Guid id,
            ToggleAutoDeployRequest req,
            ServerForgeStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var existing = await store.GetApplicationByIdAsync(id, ct);
            if (existing is null) return Results.NotFound();

            if (req.Enabled && !req.RepositoryExplicitlyAuthorized)
                return Results.BadRequest("Enabling auto-deploy requires explicit repository authorization.");

            existing.AutoDeployEnabled = req.Enabled;
            existing.RepositoryExplicitlyAuthorized = req.RepositoryExplicitlyAuthorized;
            await store.UpsertApplicationAsync(existing, user.Identity?.Name ?? "DesktopAdmin", ct);
            return Results.Ok(existing);
        });

        api.MapGet("/applications/{id:guid}/env", async (Guid id, EnvironmentTier? tier, ServerForgeStore store, CancellationToken ct) =>
        {
            var vars = await store.GetEnvironmentVariablesAsync(id, tier, ct);
            var masked = vars.Select(v => new EnvVarItemDto(
                v.Key,
                v.IsSecret ? "••••••••" : v.ProtectedValue,
                v.IsSecret,
                v.IsRequired)).ToList();
            return Results.Ok(masked);
        });

        api.MapPut("/applications/{id:guid}/env", async (
            Guid id,
            ConfigureEnvVarsRequest req,
            ServerForgeStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var appRec = await store.GetApplicationByIdAsync(id, ct);
            if (appRec is null) return Results.NotFound();

            foreach (var v in req.Variables)
            {
                if (string.IsNullOrWhiteSpace(v.Key)) continue;
                await store.SaveEnvironmentVariableAsync(id, req.Tier, v.Key.Trim(), v.Value, v.IsSecret, v.IsRequired, user.Identity?.Name ?? "DesktopAdmin", ct);
            }
            return Results.Ok(new { updated = req.Variables.Count });
        });

        api.MapPut("/applications/{id:guid}/domains", async (
            Guid id,
            ConfigureDomainRequest req,
            ServerForgeStore store,
            IIisManager iis,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var appRec = await store.GetApplicationByIdAsync(id, ct);
            if (appRec is null) return Results.NotFound();

            var check = DomainValidator.ValidateHostname(req.Hostname, allowLocalTestHostname: true);
            if (!check.IsValid) return Results.BadRequest(new { errors = check.Errors });

            var conflicts = await iis.DetectBindingConflictsAsync(req.Hostname, req.HttpPort, appRec.IisSiteName, ct);
            if (conflicts.Count > 0)
                return Results.Conflict(new { conflicts });

            var binding = new DomainBindingRecord
            {
                ApplicationId = id,
                Hostname = req.Hostname.Trim(),
                HttpPort = req.HttpPort,
                HttpsPort = req.HttpsPort,
                HttpsEnabled = req.HttpsEnabled,
                UseWinAcmeLetsEncrypt = req.UseWinAcmeLetsEncrypt
            };

            appRec.PrimaryDomain = binding.Hostname;
            await store.UpsertDomainAsync(binding, user.Identity?.Name ?? "DesktopAdmin", ct);
            await store.UpsertApplicationAsync(appRec, user.Identity?.Name ?? "DesktopAdmin", ct);
            return Results.Ok(binding);
        });

        api.MapGet("/applications/{id:guid}/deployments", async (Guid id, ServerForgeStore store, CancellationToken ct) =>
            Results.Ok(await store.GetDeploymentHistoryAsync(id, 100, ct)));

        api.MapGet("/applications/{id:guid}/releases", async (Guid id, ServerForgeStore store, CancellationToken ct) =>
        {
            var appRec = await store.GetApplicationByIdAsync(id, ct);
            if (appRec is null) return Results.NotFound();

            var releasesDir = Path.Combine(appRec.ReleasesRootPath, "releases");
            if (!Directory.Exists(releasesDir))
                return Results.Ok(Array.Empty<ReleaseSummaryDto>());

            var releases = new DirectoryInfo(releasesDir)
                .GetDirectories()
                .OrderByDescending(d => d.CreationTimeUtc)
                .Select(d => new ReleaseSummaryDto(
                    ReleaseId: d.Name,
                    FullPath: d.FullName,
                    IsActive: d.FullName.Equals(appRec.ActiveReleasePath, StringComparison.OrdinalIgnoreCase),
                    IsLastKnownGood: d.FullName.Equals(appRec.LastKnownGoodReleasePath, StringComparison.OrdinalIgnoreCase),
                    CreatedAtUtc: d.CreationTimeUtc,
                    SizeBytes: d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)))
                .ToList();

            return Results.Ok(releases);
        });

        api.MapDelete("/applications/{id:guid}", async (Guid id, ServerForgeStore store, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var removed = await store.RemoveApplicationFromManagementAsync(id, user.Identity?.Name ?? "DesktopAdmin", ct);
            return removed
                ? Results.Ok(new { removed = true, hostedFilesPreserved = true })
                : Results.NotFound();
        });

        api.MapGet("/diagnostics", async (IServerTelemetryProvider telemetry, CancellationToken ct) =>
            Results.Ok(await telemetry.CollectDiagnosticsAsync(identityStore.State.AgentId, ct)));

        api.MapGet("/telemetry", async (IServerTelemetryProvider telemetry, ServerForgeStore store, CancellationToken ct) =>
        {
            var apps = await store.GetApplicationsAsync(ct);
            var dbs = await store.GetDatabaseServersAsync(ct);
            return Results.Ok(await telemetry.CollectTelemetryAsync(identityStore.State.AgentId, apps, dbs, ct));
        });

        api.MapGet("/iis/sites", async (IIisManager iis, CancellationToken ct) =>
            Results.Ok(await iis.DiscoverSitesAsync(ct)));

        api.MapPost("/iis/sites/adopt", async (
            AdoptIisSiteRequest req,
            IIisManager iis,
            ServerForgeStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (!req.OperatorReviewedDryRun || string.IsNullOrWhiteSpace(req.SiteName))
                return Results.BadRequest("Explicit dry-run review confirmation and site name are required to adopt a pre-existing IIS site.");

            iis.RegisterManagedSite(req.SiteName.Trim());
            await store.RecordAuditAsync(new AuditLogRecord
            {
                Actor = user.Identity?.Name ?? "DesktopAdmin",
                Action = "IIS.SiteAdopted",
                TargetResource = req.SiteName.Trim(),
                Details = $"Operator explicitly adopted pre-existing IIS site. Note: {req.ConfirmationNote}"
            }, ct);
            return Results.Ok(new { adopted = true, siteName = req.SiteName.Trim() });
        });

        api.MapPost("/deployments/trigger", async (
            TriggerDeploymentRequest request,
            ServerForgeStore store,
            IDeploymentEngine engine,
            CancellationToken ct) =>
        {
            var target = await store.GetApplicationByIdAsync(request.ApplicationId, ct);
            if (target is null)
                return Results.NotFound($"Application {request.ApplicationId} is not registered on this agent.");

            var env = await store.ResolveDecryptedEnvVarsAsync(target.Id, EnvironmentTier.Production, ct);
            var record = await engine.EnqueueAndExecuteAsync(target, request, env, cancellationToken: ct);
            await store.RecordDeploymentAsync(record, ct);
            await store.UpsertApplicationAsync(target, request.RequestedBy, ct);
            return Results.Ok(record);
        });

        api.MapPost("/deployments/{applicationId:guid}/rollback", async (
            Guid applicationId,
            ServerForgeStore store,
            IDeploymentEngine engine,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var target = await store.GetApplicationByIdAsync(applicationId, ct);
            if (target is null) return Results.NotFound();

            var record = await engine.RollbackToLastHealthyReleaseAsync(target, user.Identity?.Name ?? "DesktopAdmin", ct);
            await store.RecordDeploymentAsync(record, ct);
            await store.UpsertApplicationAsync(target, user.Identity?.Name ?? "DesktopAdmin", ct);
            return Results.Ok(record);
        });

        api.MapPost("/databases/test", async (
            DatabaseConnectionTestRequest req,
            ISqlServerManager sql,
            IPostgreSqlManager pg,
            CancellationToken ct) =>
        {
            var rec = new DatabaseServerRecord { Id = req.DatabaseServerId, DisplayName = req.DisplayName, Engine = req.Engine };
            var health = req.Engine == DatabaseEngineType.SqlServer
                ? await sql.TestConnectionAsync(rec, req.ConnectionString, ct)
                : await pg.TestConnectionAsync(rec, req.ConnectionString, ct);
            return Results.Ok(health);
        });

        api.MapPost("/databases/backup", async (
            DatabaseBackupRequest req,
            ISqlServerManager sql,
            IPostgreSqlManager pg,
            ServerForgeStore store,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var file = req.Engine == DatabaseEngineType.SqlServer
                ? await sql.BackupDatabaseAsync(req.ConnectionString, req.DatabaseName, req.BackupDirectory, ct)
                : await pg.BackupDatabaseAsync(req.ConnectionString, req.DatabaseName, req.BackupDirectory, ct);

            await store.RecordAuditAsync(new AuditLogRecord
            {
                Actor = user.Identity?.Name ?? "DesktopAdmin",
                Action = "Database.BackupCompleted",
                TargetResource = $"{req.Engine}/{req.DatabaseName}",
                Details = $"Backup saved to {file}"
            }, ct);
            return Results.Ok(new { backupFile = file });
        });

        api.MapGet("/audit", async (ServerForgeStore store, CancellationToken ct) =>
            Results.Ok(await store.GetRecentAuditLogsAsync(200, ct)));
    }
}
