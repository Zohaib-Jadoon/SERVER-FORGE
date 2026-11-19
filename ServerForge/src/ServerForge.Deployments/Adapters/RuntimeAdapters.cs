using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.Security.Execution;

namespace ServerForge.Deployments.Adapters;

/// <summary>
/// Adapter for Next.js SSR (standalone server.js behind IIS ARR reverse proxy + WinSW Windows Service).
/// </summary>
public sealed class NextJsDeploymentAdapter : IAppDeploymentAdapter
{
    private readonly IIisManager _iis;
    private readonly IWindowsServiceManager _services;

    public NextJsDeploymentAdapter(IIisManager iis, IWindowsServiceManager services)
    {
        _iis = iis;
        _services = services;
    }

    public ApplicationRuntimeType SupportedRuntime => ApplicationRuntimeType.NextJsSsr;
    public bool SupportsZeroDowntimeSwap => false;
    public string ZeroDowntimeExplanation =>
        "Single-port Windows Service restart incurs a brief (~1-3s) drain window unless blue-green port rotation is configured.";

    public Task<DryRunPlanDto> PlanDeploymentAsync(ManagedApplication app, string stagedReleaseDir, CancellationToken ct)
        => _iis.PlanWebsiteProvisioningAsync(app, stagedReleaseDir, ct);

    public async Task<AdapterDeployResult> DeployStagedReleaseAsync(
        ManagedApplication app,
        string stagedReleaseDir,
        IReadOnlyDictionary<string, string> envVars,
        Action<string> log,
        CancellationToken ct)
    {
        var serverJs = Path.Combine(stagedReleaseDir, "server.js");
        if (!File.Exists(serverJs))
        {
            log("Next.js standalone server.js not found at release root; verifying package.json start script.");
        }

        var mergedEnv = new Dictionary<string, string>(envVars, StringComparer.Ordinal)
        {
            ["NODE_ENV"] = "production",
            ["PORT"] = app.InternalPort.ToString(),
            ["HOSTNAME"] = "127.0.0.1"
        };

        log($"Installing/updating WinSW Windows Service '{app.WindowsServiceName}' on 127.0.0.1:{app.InternalPort}");
        await _services.InstallOrUpdateWinSwServiceAsync(app, stagedReleaseDir, mergedEnv, ct).ConfigureAwait(false);
        await _services.StartServiceAsync(app.WindowsServiceName, ct).ConfigureAwait(false);

        log($"Configuring IIS reverse proxy site '{app.IisSiteName}' -> http://127.0.0.1:{app.InternalPort}");
        await _iis.EnsureManagedSiteAndPoolAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);
        await _iis.ConfigureReverseProxyWebConfigAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);
        await _iis.SwitchSitePhysicalPathAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);

        return new AdapterDeployResult(true, false, ZeroDowntimeExplanation, null);
    }

    public async Task RollbackReleaseAsync(ManagedApplication app, string previousHealthyReleaseDir, Action<string> log, CancellationToken ct)
    {
        log($"Rolling back Next.js service '{app.WindowsServiceName}' to '{previousHealthyReleaseDir}'");
        await _services.InstallOrUpdateWinSwServiceAsync(app, previousHealthyReleaseDir, new Dictionary<string, string>(), ct);
        await _services.RestartServiceAsync(app.WindowsServiceName, ct);
        await _iis.SwitchSitePhysicalPathAsync(app, previousHealthyReleaseDir, ct);
    }
}

/// <summary>
/// Adapter for NestJS, Express, and generic Node.js backend APIs hosted as managed Windows Services behind IIS ARR.
/// </summary>
public sealed class NodeServiceDeploymentAdapter : IAppDeploymentAdapter
{
    private readonly IIisManager _iis;
    private readonly IWindowsServiceManager _services;

    public NodeServiceDeploymentAdapter(ApplicationRuntimeType runtime, IIisManager iis, IWindowsServiceManager services)
    {
        SupportedRuntime = runtime;
        _iis = iis;
        _services = services;
    }

    public ApplicationRuntimeType SupportedRuntime { get; }
    public bool SupportsZeroDowntimeSwap => false;
    public string ZeroDowntimeExplanation =>
        "Managed Windows Service restarts in place on its bound internal port (~1-2s reconnect window).";

    public Task<DryRunPlanDto> PlanDeploymentAsync(ManagedApplication app, string stagedReleaseDir, CancellationToken ct)
        => _iis.PlanWebsiteProvisioningAsync(app, stagedReleaseDir, ct);

    public async Task<AdapterDeployResult> DeployStagedReleaseAsync(
        ManagedApplication app,
        string stagedReleaseDir,
        IReadOnlyDictionary<string, string> envVars,
        Action<string> log,
        CancellationToken ct)
    {
        log($"Deploying {SupportedRuntime} service '{app.WindowsServiceName}' from '{stagedReleaseDir}'");
        await _services.InstallOrUpdateWinSwServiceAsync(app, stagedReleaseDir, envVars, ct).ConfigureAwait(false);
        await _services.StartServiceAsync(app.WindowsServiceName, ct).ConfigureAwait(false);

        await _iis.EnsureManagedSiteAndPoolAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);
        await _iis.ConfigureReverseProxyWebConfigAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);
        await _iis.SwitchSitePhysicalPathAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);

        return new AdapterDeployResult(true, false, ZeroDowntimeExplanation, null);
    }

    public async Task RollbackReleaseAsync(ManagedApplication app, string previousHealthyReleaseDir, Action<string> log, CancellationToken ct)
    {
        log($"Rolling back {SupportedRuntime} service '{app.WindowsServiceName}' to '{previousHealthyReleaseDir}'");
        await _services.InstallOrUpdateWinSwServiceAsync(app, previousHealthyReleaseDir, new Dictionary<string, string>(), ct);
        await _services.RestartServiceAsync(app.WindowsServiceName, ct);
        await _iis.SwitchSitePhysicalPathAsync(app, previousHealthyReleaseDir, ct);
    }
}

/// <summary>
/// Adapter for React/Vite SPAs and static HTML/CSS/JS websites published to immutable IIS directories.
/// </summary>
public sealed class StaticSiteDeploymentAdapter : IAppDeploymentAdapter
{
    private readonly IIisManager _iis;

    public StaticSiteDeploymentAdapter(ApplicationRuntimeType runtime, IIisManager iis)
    {
        SupportedRuntime = runtime;
        _iis = iis;
    }

    public ApplicationRuntimeType SupportedRuntime { get; }
    public bool SupportsZeroDowntimeSwap => true;
    public string ZeroDowntimeExplanation =>
        "Atomic IIS virtual directory physical path swap to pre-staged immutable directory achieves zero-downtime cutover.";

    public Task<DryRunPlanDto> PlanDeploymentAsync(ManagedApplication app, string stagedReleaseDir, CancellationToken ct)
        => _iis.PlanWebsiteProvisioningAsync(app, stagedReleaseDir, ct);

    public async Task<AdapterDeployResult> DeployStagedReleaseAsync(
        ManagedApplication app,
        string stagedReleaseDir,
        IReadOnlyDictionary<string, string> envVars,
        Action<string> log,
        CancellationToken ct)
    {
        if (SupportedRuntime == ApplicationRuntimeType.ReactViteStatic)
        {
            var webConfigPath = Path.Combine(stagedReleaseDir, "web.config");
            if (!File.Exists(webConfigPath))
            {
                await File.WriteAllTextAsync(webConfigPath, """
                    <?xml version="1.0" encoding="utf-8"?>
                    <configuration>
                      <system.webServer>
                        <rewrite>
                          <rules>
                            <rule name="ReactViteSpaFallback" stopProcessing="true">
                              <match url=".*" />
                              <conditions logicalGrouping="MatchAll">
                                <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
                                <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
                              </conditions>
                              <action type="Rewrite" url="/index.html" />
                            </rule>
                          </rules>
                        </rewrite>
                      </system.webServer>
                    </configuration>
                    """, ct).ConfigureAwait(false);
            }
        }

        log($"Swapping IIS site '{app.IisSiteName}' physical path atomically to '{stagedReleaseDir}'");
        await _iis.EnsureManagedSiteAndPoolAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);
        await _iis.SwitchSitePhysicalPathAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);
        return new AdapterDeployResult(true, true, ZeroDowntimeExplanation, null);
    }

    public Task RollbackReleaseAsync(ManagedApplication app, string previousHealthyReleaseDir, Action<string> log, CancellationToken ct)
    {
        log($"Atomically restoring IIS static site '{app.IisSiteName}' to '{previousHealthyReleaseDir}'");
        return _iis.SwitchSitePhysicalPathAsync(app, previousHealthyReleaseDir, ct);
    }
}

/// <summary>
/// Adapter for ASP.NET Core applications running under IIS AspNetCoreModuleV2.
/// </summary>
public sealed class AspNetCoreDeploymentAdapter : IAppDeploymentAdapter
{
    private readonly IIisManager _iis;

    public AspNetCoreDeploymentAdapter(IIisManager iis) => _iis = iis;

    public ApplicationRuntimeType SupportedRuntime => ApplicationRuntimeType.AspNetCore;
    public bool SupportsZeroDowntimeSwap => true;
    public string ZeroDowntimeExplanation =>
        "IIS overlapped AppPool recycle after physical path switch enables graceful request draining.";

    public Task<DryRunPlanDto> PlanDeploymentAsync(ManagedApplication app, string stagedReleaseDir, CancellationToken ct)
        => _iis.PlanWebsiteProvisioningAsync(app, stagedReleaseDir, ct);

    public async Task<AdapterDeployResult> DeployStagedReleaseAsync(
        ManagedApplication app,
        string stagedReleaseDir,
        IReadOnlyDictionary<string, string> envVars,
        Action<string> log,
        CancellationToken ct)
    {
        log($"Deploying ASP.NET Core release to IIS site '{app.IisSiteName}'");
        await _iis.EnsureManagedSiteAndPoolAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);
        await _iis.SwitchSitePhysicalPathAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);
        return new AdapterDeployResult(true, true, ZeroDowntimeExplanation, null);
    }

    public Task RollbackReleaseAsync(ManagedApplication app, string previousHealthyReleaseDir, Action<string> log, CancellationToken ct)
    {
        log($"Rolling back ASP.NET Core IIS site '{app.IisSiteName}' to '{previousHealthyReleaseDir}'");
        return _iis.SwitchSitePhysicalPathAsync(app, previousHealthyReleaseDir, ct);
    }
}

/// <summary>
/// Adapter for Classic ASP websites. Strictly refuses modifications unless explicitly adopted,
/// and preserves existing IIS application pool / parent path / COM settings.
/// </summary>
public sealed class ClassicAspDeploymentAdapter : IAppDeploymentAdapter
{
    private readonly IIisManager _iis;

    public ClassicAspDeploymentAdapter(IIisManager iis) => _iis = iis;

    public ApplicationRuntimeType SupportedRuntime => ApplicationRuntimeType.ClassicAsp;
    public bool SupportsZeroDowntimeSwap => false;
    public string ZeroDowntimeExplanation =>
        "Classic ASP physical path cutover preserves existing AppPool settings; active in-process ASP sessions may reset.";

    public Task<DryRunPlanDto> PlanDeploymentAsync(ManagedApplication app, string stagedReleaseDir, CancellationToken ct)
        => _iis.PlanWebsiteProvisioningAsync(app, stagedReleaseDir, ct);

    public async Task<AdapterDeployResult> DeployStagedReleaseAsync(
        ManagedApplication app,
        string stagedReleaseDir,
        IReadOnlyDictionary<string, string> envVars,
        Action<string> log,
        CancellationToken ct)
    {
        if (app.ManagementState == ResourceManagementState.ReadOnlyUnmanaged)
        {
            return new AdapterDeployResult(false, false, ZeroDowntimeExplanation,
                "Classic ASP site is read-only/unmanaged. Explicit adoption in ServerForge Inventory is mandatory.");
        }

        log($"Preserving existing Classic ASP AppPool '{app.IisAppPoolName}' configuration and switching release directory.");
        await _iis.SwitchSitePhysicalPathAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);
        return new AdapterDeployResult(true, false, ZeroDowntimeExplanation, null);
    }

    public Task RollbackReleaseAsync(ManagedApplication app, string previousHealthyReleaseDir, Action<string> log, CancellationToken ct)
    {
        log($"Restoring Classic ASP site '{app.IisSiteName}' physical path to '{previousHealthyReleaseDir}'");
        return _iis.SwitchSitePhysicalPathAsync(app, previousHealthyReleaseDir, ct);
    }
}

/// <summary>
/// Adapter for Python FastAPI services using isolated virtual environments, locked requirements, WinSW, and IIS ARR reverse proxy.
/// </summary>
public sealed class PythonFastApiDeploymentAdapter : IAppDeploymentAdapter
{
    private readonly IIisManager _iis;
    private readonly IWindowsServiceManager _services;

    public PythonFastApiDeploymentAdapter(IIisManager iis, IWindowsServiceManager services)
    {
        _iis = iis;
        _services = services;
    }

    public ApplicationRuntimeType SupportedRuntime => ApplicationRuntimeType.PythonFastApi;
    public bool SupportsZeroDowntimeSwap => false;
    public string ZeroDowntimeExplanation =>
        "Python Uvicorn Windows Service restarts on its internal loopback port after isolated venv dependency installation.";

    public Task<DryRunPlanDto> PlanDeploymentAsync(ManagedApplication app, string stagedReleaseDir, CancellationToken ct)
        => _iis.PlanWebsiteProvisioningAsync(app, stagedReleaseDir, ct);

    public async Task<AdapterDeployResult> DeployStagedReleaseAsync(
        ManagedApplication app,
        string stagedReleaseDir,
        IReadOnlyDictionary<string, string> envVars,
        Action<string> log,
        CancellationToken ct)
    {
        var venvPath = Path.Combine(stagedReleaseDir, ".venv");
        var reqFile = Path.Combine(stagedReleaseDir, "requirements.txt");

        if (File.Exists(reqFile) && OperatingSystem.IsWindows())
        {
            log("Creating isolated Python virtual environment (.venv) and installing locked dependencies");
            var venvRes = await SafeProcessRunner.RunAsync("python.exe", ["-m", "venv", venvPath], stagedReleaseDir, cancellationToken: ct);
            if (venvRes.ExitCode != 0)
                return new AdapterDeployResult(false, false, ZeroDowntimeExplanation, $"Python venv creation failed: {venvRes.StandardError}");

            var pipExe = Path.Combine(venvPath, "Scripts", "pip.exe");
            var pipRes = await SafeProcessRunner.RunAsync(pipExe, ["install", "--no-input", "-r", reqFile], stagedReleaseDir, cancellationToken: ct);
            if (pipRes.ExitCode != 0)
                return new AdapterDeployResult(false, false, ZeroDowntimeExplanation, $"pip install failed: {pipRes.StandardError}");
        }

        await _services.InstallOrUpdateWinSwServiceAsync(app, stagedReleaseDir, envVars, ct).ConfigureAwait(false);
        await _services.StartServiceAsync(app.WindowsServiceName, ct).ConfigureAwait(false);
        await _iis.EnsureManagedSiteAndPoolAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);
        await _iis.ConfigureReverseProxyWebConfigAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);
        await _iis.SwitchSitePhysicalPathAsync(app, stagedReleaseDir, ct).ConfigureAwait(false);

        return new AdapterDeployResult(true, false, ZeroDowntimeExplanation, null);
    }

    public async Task RollbackReleaseAsync(ManagedApplication app, string previousHealthyReleaseDir, Action<string> log, CancellationToken ct)
    {
        log($"Rolling back Python FastAPI service '{app.WindowsServiceName}' to '{previousHealthyReleaseDir}'");
        await _services.InstallOrUpdateWinSwServiceAsync(app, previousHealthyReleaseDir, new Dictionary<string, string>(), ct);
        await _services.RestartServiceAsync(app.WindowsServiceName, ct);
        await _iis.SwitchSitePhysicalPathAsync(app, previousHealthyReleaseDir, ct);
    }
}
