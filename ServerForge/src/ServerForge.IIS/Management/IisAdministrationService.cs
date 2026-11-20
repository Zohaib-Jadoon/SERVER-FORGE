using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.Security.Execution;

namespace ServerForge.IIS.Management;

/// <summary>
/// Manages Microsoft IIS via supported IISAdministration PowerShell module and appcmd.exe.
/// Strictly protects pre-existing unmanaged IIS websites and application pools as read-only.
/// </summary>
public sealed class IisAdministrationService : IIisManager
{
    private readonly ILogger<IisAdministrationService> _logger;
    private readonly IPathBoundaryGuard _pathGuard;
    private readonly HashSet<string> _managedSiteNames;

    public IisAdministrationService(
        ILogger<IisAdministrationService> logger,
        IPathBoundaryGuard pathGuard,
        IEnumerable<string>? initiallyManagedSites = null)
    {
        _logger = logger;
        _pathGuard = pathGuard;
        _managedSiteNames = new HashSet<string>(initiallyManagedSites ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public void RegisterManagedSite(string siteName) => _managedSiteNames.Add(siteName);

    public async Task<IReadOnlyList<DiscoveredIisSiteDto>> DiscoverSitesAsync(CancellationToken ct)
    {
        const string script = """
            Import-Module IISAdministration -ErrorAction Stop
            Get-IISSite | ForEach-Object {
                $rootApp = $_.Applications['/']
                $physPath = if ($rootApp) { $rootApp.VirtualDirectories['/'].PhysicalPath } else { '' }
                $pool = if ($rootApp) { $rootApp.ApplicationPoolName } else { '' }
                $hasAsp = $false
                if ($physPath -and (Test-Path $physPath)) {
                    $hasAsp = @(Get-ChildItem -Path $physPath -Filter '*.asp' -Depth 2 -ErrorAction SilentlyContinue | Select-Object -First 1).Count -gt 0
                }
                [PSCustomObject]@{
                    SiteId = $_.Id
                    Name = $_.Name
                    State = $_.State.ToString()
                    PhysicalPath = $physPath
                    AppPoolName = $pool
                    Bindings = @($_.Bindings | ForEach-Object { "$($_.Protocol)://$($_.BindingInformation)" })
                    ContainsClassicAspIndicators = $hasAsp
                }
            } | ConvertTo-Json -Depth 4 -Compress
            """;

        var result = await RunPowerShellAsync(script, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            return [];

        return ParseDiscoveredSitesJson(result.StandardOutput, _managedSiteNames);
    }

    public async Task<IReadOnlyList<DiscoveredAppPoolDto>> DiscoverAppPoolsAsync(CancellationToken ct)
    {
        const string script = """
            Import-Module IISAdministration -ErrorAction Stop
            $sm = Get-IISServerManager
            $sm.ApplicationPools | ForEach-Object {
                [PSCustomObject]@{
                    Name = $_.Name
                    State = $_.State.ToString()
                    ManagedRuntimeVersion = $_.ManagedRuntimeVersion
                    PipelineMode = $_.ManagedPipelineMode.ToString()
                    IdentityType = $_.ProcessModel.IdentityType.ToString()
                }
            } | ConvertTo-Json -Depth 3 -Compress
            """;

        var result = await RunPowerShellAsync(script, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            return [];

        var list = new List<DiscoveredAppPoolDto>();
        using var doc = JsonDocument.Parse(result.StandardOutput);
        var elements = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
            : new[] { doc.RootElement }.AsEnumerable();

        foreach (var el in elements)
        {
            var name = el.GetProperty("Name").GetString() ?? "";
            list.Add(new DiscoveredAppPoolDto(
                Name: name,
                State: el.GetProperty("State").GetString() ?? "Unknown",
                ManagedRuntimeVersion: el.GetProperty("ManagedRuntimeVersion").GetString() ?? "",
                PipelineMode: el.GetProperty("PipelineMode").GetString() ?? "Integrated",
                IdentityType: el.GetProperty("IdentityType").GetString() ?? "ApplicationPoolIdentity",
                ManagementState: _managedSiteNames.Contains(name)
                    ? ResourceManagementState.ServerForgeCreated
                    : ResourceManagementState.ReadOnlyUnmanaged));
        }

        return list;
    }

    public async Task<bool> IsUrlRewriteInstalledAsync(CancellationToken ct)
    {
        var result = await RunPowerShellAsync(
            "Test-Path \"$env:SystemRoot\\System32\\inetsrv\\rewrite.dll\"", ct).ConfigureAwait(false);
        return result.ExitCode == 0 && result.StandardOutput.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> IsArrInstalledAsync(CancellationToken ct)
    {
        var result = await RunPowerShellAsync(
            "Test-Path \"$env:ProgramFiles\\IIS\\Application Request Routing\\requestRouter.dll\"", ct).ConfigureAwait(false);
        return result.ExitCode == 0 && result.StandardOutput.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<string>> DetectBindingConflictsAsync(
        string hostname,
        int port,
        string? excludingSiteName,
        CancellationToken ct)
    {
        var sites = await DiscoverSitesAsync(ct).ConfigureAwait(false);
        return FindBindingConflicts(sites, hostname, port, excludingSiteName);
    }

    public async Task<DryRunPlanDto> PlanWebsiteProvisioningAsync(
        ManagedApplication app,
        string targetPhysicalPath,
        CancellationToken ct)
    {
        var steps = new List<string>();
        var warnings = new List<string>();
        var affected = new List<string> { $"IIS Site: {app.IisSiteName}", $"AppPool: {app.IisAppPoolName}", $"Directory: {targetPhysicalPath}" };

        var modifiesUnmanaged = app.ManagementState == ResourceManagementState.ReadOnlyUnmanaged;
        if (modifiesUnmanaged)
        {
            warnings.Add($"BLOCKED: '{app.IisSiteName}' is a pre-existing unmanaged IIS site. Explicit operator adoption is required before any changes.");
        }

        if (app.RuntimeType == ApplicationRuntimeType.ClassicAsp)
        {
            warnings.Add("Classic ASP safety mode active: existing application pool 32-bit/pipeline settings and ASP parent paths will be preserved untouched.");
        }

        var conflicts = await DetectBindingConflictsAsync(app.PrimaryDomain, 80, app.IisSiteName, ct).ConfigureAwait(false);
        foreach (var c in conflicts)
            warnings.Add($"Binding conflict detected: {c}");

        steps.Add($"Verify physical directory '{targetPhysicalPath}' exists inside ServerForge managed root.");
        steps.Add($"Create or verify dedicated AppPool '{app.IisAppPoolName}' with No Managed Code & ApplicationPoolIdentity.");
        steps.Add($"Create or update managed IIS Site '{app.IisSiteName}' pointing to '{targetPhysicalPath}'.");
        if (Core.Validation.ApplicationConfigValidator.RequiresInternalPort(app.RuntimeType))
            steps.Add($"Generate IIS URL Rewrite / ARR reverse proxy web.config forwarding to http://127.0.0.1:{app.InternalPort}.");
        steps.Add($"Verify HTTP readiness check against '{app.HealthCheckPath}'.");

        return new DryRunPlanDto(
            OperationTitle: $"Provision IIS Site '{app.IisSiteName}'",
            ModifiesUnmanagedResource: modifiesUnmanaged,
            PlannedSteps: steps,
            SafetyWarnings: warnings,
            AffectedResources: affected);
    }

    public async Task EnsureManagedSiteAndPoolAsync(ManagedApplication app, string targetPhysicalPath, CancellationToken ct)
    {
        EnsureMutationAuthorized(app.IisSiteName, app.ManagementState);
        var safePath = _pathGuard.EnsureWithinRoot(app.ReleasesRootPath, targetPhysicalPath);

        var appCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "appcmd.exe");
        // Create dedicated AppPool if absent (never modifies unrelated pools)
        var poolCheck = await SafeProcessRunner.RunAsync(appCmd, ["list", "apppool", app.IisAppPoolName], cancellationToken: ct);
        if (poolCheck.ExitCode != 0)
        {
            var runtimeArg = app.RuntimeType == ApplicationRuntimeType.ClassicAsp ? "" : "/managedRuntimeVersion:";
            var args = new List<string> { "add", "apppool", $"/name:{app.IisAppPoolName}", "/managedPipelineMode:Integrated" };
            if (!string.IsNullOrEmpty(runtimeArg)) args.Add(runtimeArg);
            var addPool = await SafeProcessRunner.RunAsync(appCmd, args, cancellationToken: ct);
            if (addPool.ExitCode != 0)
                throw new InvalidOperationException($"Failed to create IIS AppPool '{app.IisAppPoolName}': {addPool.StandardError}");
        }

        var siteCheck = await SafeProcessRunner.RunAsync(appCmd, ["list", "site", app.IisSiteName], cancellationToken: ct);
        if (siteCheck.ExitCode != 0)
        {
            var binding = ResolveSiteBinding(app);
            var addSite = await SafeProcessRunner.RunAsync(
                appCmd,
                ["add", "site", $"/name:{app.IisSiteName}", $"/bindings:{binding}", $"/physicalPath:{safePath}"],
                cancellationToken: ct);
            if (addSite.ExitCode != 0)
                throw new InvalidOperationException($"Failed to create IIS site '{app.IisSiteName}': {addSite.StandardError}");

            await SafeProcessRunner.RunAsync(
                appCmd,
                ["set", "app", $"{app.IisSiteName}/", $"/applicationPool:{app.IisAppPoolName}"],
                cancellationToken: ct);

            await SafeProcessRunner.RunAsync(
                appCmd,
                ["start", "site", app.IisSiteName],
                cancellationToken: ct);
        }

        _managedSiteNames.Add(app.IisSiteName);
        _logger.LogInformation("Verified managed IIS website {Site} and AppPool {Pool}", app.IisSiteName, app.IisAppPoolName);
    }

    public static string ResolveSiteBinding(ManagedApplication app)
    {
        var directPort = !Core.Validation.ApplicationConfigValidator.RequiresInternalPort(app.RuntimeType) &&
                         app.InternalPort is >= 1024 and <= 65535
            ? app.InternalPort
            : 80;

        if (string.IsNullOrWhiteSpace(app.PrimaryDomain) ||
            app.PrimaryDomain.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return directPort == 80 ? "http/*:80:" : $"http/127.0.0.1:{directPort}:";
        }

        return $"http/*:{directPort}:{app.PrimaryDomain.Trim()}";
    }

    public async Task SwitchSitePhysicalPathAsync(ManagedApplication app, string newReleasePath, CancellationToken ct)
    {
        EnsureMutationAuthorized(app.IisSiteName, app.ManagementState);
        var safePath = _pathGuard.EnsureWithinRoot(app.ReleasesRootPath, newReleasePath);
        var appCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "appcmd.exe");

        var res = await SafeProcessRunner.RunAsync(
            appCmd,
            ["set", "vdir", $"{app.IisSiteName}/", $"/physicalPath:{safePath}"],
            cancellationToken: ct);

        if (res.ExitCode != 0)
            throw new InvalidOperationException($"Failed switching IIS physical path for '{app.IisSiteName}': {res.StandardError}");

        if (app.RuntimeType == ApplicationRuntimeType.AspNetCore && !string.IsNullOrWhiteSpace(app.IisAppPoolName))
        {
            // Trigger graceful overlapped AppPool recycle so AspNetCoreModuleV2 loads the new release + web.config immediately
            await SafeProcessRunner.RunAsync(
                appCmd,
                ["recycle", "apppool", $"/apppool.name:{app.IisAppPoolName}"],
                cancellationToken: ct);
        }
    }

    public async Task ConfigureReverseProxyWebConfigAsync(ManagedApplication app, string siteRootPath, CancellationToken ct)
    {
        EnsureMutationAuthorized(app.IisSiteName, app.ManagementState);
        var webConfigXml = BuildReverseProxyWebConfig(app.InternalPort, app.Slug);
        var targetFile = Path.Combine(siteRootPath, "web.config");
        await File.WriteAllTextAsync(targetFile, webConfigXml, ct).ConfigureAwait(false);
    }

    public async Task SetSiteStateAsync(string siteName, ResourceManagementState managementState, bool start, CancellationToken ct)
    {
        EnsureMutationAuthorized(siteName, managementState);
        var appCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "appcmd.exe");
        var verb = start ? "start" : "stop";
        var res = await SafeProcessRunner.RunAsync(appCmd, [verb, "site", siteName], cancellationToken: ct);
        if (res.ExitCode != 0)
            throw new InvalidOperationException($"Failed to {verb} IIS site '{siteName}': {res.StandardError}");
    }

    public static void EnsureMutationAuthorized(string resourceName, ResourceManagementState state)
    {
        if (state == ResourceManagementState.ReadOnlyUnmanaged)
        {
            throw new UnauthorizedAccessException(
                $"Safety Lock: Resource '{resourceName}' is an unmanaged pre-existing IIS resource and cannot be modified until explicitly adopted.");
        }
    }

    public static IReadOnlyList<DiscoveredIisSiteDto> ParseDiscoveredSitesJson(string json, IReadOnlySet<string> managedNames)
    {
        var list = new List<DiscoveredIisSiteDto>();
        using var doc = JsonDocument.Parse(json);
        var elements = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
            : new[] { doc.RootElement }.AsEnumerable();

        foreach (var el in elements)
        {
            var name = el.GetProperty("Name").GetString() ?? "";
            var bindings = new List<string>();
            if (el.TryGetProperty("Bindings", out var bArr) && bArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in bArr.EnumerateArray())
                    if (b.GetString() is { } s) bindings.Add(s);
            }

            list.Add(new DiscoveredIisSiteDto(
                SiteId: el.GetProperty("SiteId").GetInt64(),
                Name: name,
                State: el.GetProperty("State").GetString() ?? "Unknown",
                PhysicalPath: el.GetProperty("PhysicalPath").GetString() ?? "",
                AppPoolName: el.GetProperty("AppPoolName").GetString() ?? "",
                Bindings: bindings,
                ContainsClassicAspIndicators: el.TryGetProperty("ContainsClassicAspIndicators", out var asp) && asp.GetBoolean(),
                ManagementState: managedNames.Contains(name)
                    ? ResourceManagementState.AdoptedManaged
                    : ResourceManagementState.ReadOnlyUnmanaged));
        }

        return list;
    }

    public static IReadOnlyList<string> FindBindingConflicts(
        IEnumerable<DiscoveredIisSiteDto> sites,
        string hostname,
        int port,
        string? excludingSiteName)
    {
        var conflicts = new List<string>();
        var targetSuffix = $":{port}:{hostname}".ToLowerInvariant();
        foreach (var site in sites)
        {
            if (!string.IsNullOrWhiteSpace(excludingSiteName) &&
                site.Name.Equals(excludingSiteName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var binding in site.Bindings)
            {
                if (binding.EndsWith(targetSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add($"Site '{site.Name}' ({site.ManagementState}) already uses binding '{binding}'.");
                }
            }
        }
        return conflicts;
    }

    public static string BuildReverseProxyWebConfig(int internalPort, string appSlug)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("configuration",
                new XElement("system.webServer",
                    new XElement("rewrite",
                        new XElement("rules",
                            new XElement("rule",
                                new XAttribute("name", $"ServerForgeReverseProxy_{appSlug}"),
                                new XAttribute("stopProcessing", "true"),
                                new XElement("match", new XAttribute("url", "(.*)")),
                                new XElement("action",
                                    new XAttribute("type", "Rewrite"),
                                    new XAttribute("url", $"http://127.0.0.1:{internalPort}/{{R:1}}")),
                                new XElement("serverVariables",
                                    new XElement("set", new XAttribute("name", "HTTP_X_FORWARDED_PROTO"), new XAttribute("value", "{HTTPS}")),
                                    new XElement("set", new XAttribute("name", "HTTP_X_FORWARDED_HOST"), new XAttribute("value", "{HTTP_HOST}")))))),
                    new XElement("httpErrors", new XAttribute("existingResponse", "PassThrough")))));

        return doc.ToString();
    }

    private static Task<SafeProcessResult> RunPowerShellAsync(string script, CancellationToken ct)
        => SafeProcessRunner.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
            cancellationToken: ct);
}
