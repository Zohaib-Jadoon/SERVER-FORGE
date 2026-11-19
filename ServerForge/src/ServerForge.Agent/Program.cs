using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Serilog;
using ServerForge.Agent.Configuration;
using ServerForge.Agent.Workers;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
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

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(opts => opts.ServiceName = "ServerForgeAgent");

var agentOptions = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();
Directory.CreateDirectory(agentOptions.DataRoot);
Directory.CreateDirectory(agentOptions.AppsRoot);
Directory.CreateDirectory(Path.Combine(agentOptions.DataRoot, "logs"));

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(agentOptions.DataRoot, "logs", "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

builder.Host.UseSerilog();

var identityStore = new AgentIdentityStore(agentOptions);
var serverCert = identityStore.LoadOrCreateServerCertificate();
var pairingService = new OneTimePairingTokenService();

// Support CLI `--pair` ceremony invoked by Agent Installer or local Server Admin
if (args.Contains("--generate-pairing-code", StringComparer.OrdinalIgnoreCase) || !identityStore.State.IsPaired)
{
    var (code, expires) = pairingService.GeneratePairingCode(TimeSpan.FromMinutes(30));
    var pairingBannerPath = Path.Combine(agentOptions.DataRoot, "pairing-code.txt");
    File.WriteAllText(pairingBannerPath,
        $"ServerForge Agent Pairing Code: {code}{Environment.NewLine}" +
        $"Expires (UTC): {expires:O}{Environment.NewLine}" +
        $"Server Cert Thumbprint: {serverCert.Thumbprint}{Environment.NewLine}");
    Log.Information("Agent initial pairing code generated and written to {Path} (expires {Expires:O}).", pairingBannerPath, expires);
}

// Configure Kestrel with mandatory TLS 1.2/1.3 and client certificate support (never exposed publicly by default)
builder.WebHost.ConfigureKestrel(kestrel =>
{
    var bindIp = IPAddress.TryParse(agentOptions.BindAddress, out var parsedIp) ? parsedIp : IPAddress.Loopback;
    kestrel.Listen(bindIp, agentOptions.Port, listenOptions =>
    {
        listenOptions.UseHttps(httpsOptions =>
        {
            httpsOptions.ServerCertificate = serverCert;
            httpsOptions.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            httpsOptions.ClientCertificateValidation = (_, _, _) => true; // Validated fail-closed in auth handler
        });
    });
});

// Fail-closed Mutual TLS authentication validating the pinned Desktop client certificate thumbprint
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

// Register core services & SQLite local state
var dbPath = Path.Combine(agentOptions.DataRoot, "agent-state.db");
builder.Services.AddDbContextFactory<ServerForgeDbContext>(opts => opts.UseSqlite($"Data Source={dbPath}"));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddSingleton(identityStore);
builder.Services.AddSingleton(pairingService);
builder.Services.AddSingleton<ISecretProtector>(_ => new DpapiSecretProtector(DataProtectionScope.LocalMachine));
builder.Services.AddSingleton<IPathBoundaryGuard, PathBoundaryGuard>();
builder.Services.AddSingleton<ServerForgeStore>();
builder.Services.AddSingleton<IIisManager, IisAdministrationService>();
builder.Services.AddSingleton<IWindowsServiceManager, WindowsServiceManager>();
builder.Services.AddSingleton<AcmeCertificateService>();
builder.Services.AddSingleton<ISqlServerManager, SqlServerManagementService>();
builder.Services.AddSingleton<IPostgreSqlManager, PostgreSqlManagementService>();
builder.Services.AddHttpClient<IGitHubClient, GitHubApiClient>();
builder.Services.AddHttpClient<IServerTelemetryProvider, WindowsTelemetryService>();
builder.Services.AddHttpClient<DeploymentOrchestrator>();

// Register all 9 application runtime deployment adapters
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
builder.Services.AddSingleton<IDeploymentEngine, DeploymentOrchestrator>();

builder.Services.AddHostedService<GitHubAutoDeployWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ServerForgeDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.InitializeSqliteAsync();
}

app.UseAuthentication();
app.UseAuthorization();

// Unauthenticated ONLY for initial one-time pairing ceremony while unpaired
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

// All operational endpoints strictly require Mutual TLS client certificate authentication
var api = app.MapGroup("/api/v1").RequireAuthorization();

api.MapGet("/diagnostics", async (IServerTelemetryProvider telemetry, CancellationToken ct) =>
    Results.Ok(await telemetry.CollectDiagnosticsAsync(identityStore.State.AgentId, ct)));

api.MapGet("/telemetry", async (IServerTelemetryProvider telemetry, ServerForgeStore store, CancellationToken ct) =>
{
    var apps = await store.GetApplicationsAsync(ct);
    return Results.Ok(await telemetry.CollectTelemetryAsync(identityStore.State.AgentId, apps, [], ct));
});

api.MapGet("/iis/sites", async (IIisManager iis, CancellationToken ct) =>
    Results.Ok(await iis.DiscoverSitesAsync(ct)));

api.MapPost("/deployments/trigger", async (
    TriggerDeploymentRequest request,
    ServerForgeStore store,
    IDeploymentEngine engine,
    CancellationToken ct) =>
{
    var apps = await store.GetApplicationsAsync(ct);
    var target = apps.FirstOrDefault(a => a.Id == request.ApplicationId);
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
    var apps = await store.GetApplicationsAsync(ct);
    var target = apps.FirstOrDefault(a => a.Id == applicationId);
    if (target is null) return Results.NotFound();

    var record = await engine.RollbackToLastHealthyReleaseAsync(target, user.Identity?.Name ?? "DesktopAdmin", ct);
    await store.RecordDeploymentAsync(record, ct);
    return Results.Ok(record);
});

api.MapGet("/audit", async (ServerForgeStore store, CancellationToken ct) =>
    Results.Ok(await store.GetRecentAuditLogsAsync(200, ct)));

await app.RunAsync();
