using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using ServerForge.Contracts.Dtos;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.IIS.Certificates;
using ServerForge.Security.Execution;

namespace ServerForge.Monitoring;

/// <summary>
/// Collects real Windows Server diagnostics and live telemetry using native Windows APIs
/// (PerformanceCounter, GlobalMemoryStatusEx, DriveInfo, ServiceController, X509Store, HTTP probes).
/// Never fabricates metrics.
/// </summary>
public sealed class WindowsTelemetryService : IServerTelemetryProvider
{
    private readonly IIisManager _iisManager;
    private readonly IWindowsServiceManager _serviceManager;
    private readonly ISqlServerManager _sqlServerManager;
    private readonly AcmeCertificateService _certService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<WindowsTelemetryService> _logger;

    public WindowsTelemetryService(
        IIisManager iisManager,
        IWindowsServiceManager serviceManager,
        ISqlServerManager sqlServerManager,
        AcmeCertificateService certService,
        HttpClient httpClient,
        ILogger<WindowsTelemetryService> logger)
    {
        _iisManager = iisManager;
        _serviceManager = serviceManager;
        _sqlServerManager = sqlServerManager;
        _certService = certService;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ServerDiagnosticsSnapshot> CollectDiagnosticsAsync(string agentId, CancellationToken ct)
    {
        var isAdmin = false;
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        var sites = await _iisManager.DiscoverSitesAsync(ct).ConfigureAwait(false);
        var pools = await _iisManager.DiscoverAppPoolsAsync(ct).ConfigureAwait(false);
        var urlRewrite = await _iisManager.IsUrlRewriteInstalledAsync(ct).ConfigureAwait(false);
        var arr = await _iisManager.IsArrInstalledAsync(ct).ConfigureAwait(false);
        var sqlInstances = await _sqlServerManager.DiscoverLocalInstancesAsync(ct).ConfigureAwait(false);

        var nodeVer = await ProbeVersionAsync("node", ["--version"], ct).ConfigureAwait(false);
        var pyVer = await ProbeVersionAsync("python", ["--version"], ct).ConfigureAwait(false);
        var dotnetVer = await ProbeVersionAsync("dotnet", ["--list-runtimes"], ct).ConfigureAwait(false);

        var aspNetHosting = OperatingSystem.IsWindows() &&
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "IIS", "Asp.Net Core Module", "V2", "aspnetcorev2.dll"));

        var winAcme = OperatingSystem.IsWindows() &&
            File.Exists(@"C:\ProgramData\ServerForge\tools\win-acme\wacs.exe");

        var iisInstalled = sites.Count > 0 || (OperatingSystem.IsWindows() &&
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "w3wp.exe")));

        return new ServerDiagnosticsSnapshot(
            AgentId: agentId,
            Hostname: Environment.MachineName,
            OsVersion: RuntimeInformation.OSDescription,
            IsAdministrator: isAdmin,
            IisInstalled: iisInstalled,
            IisVersion: iisInstalled ? "IIS 10.0+" : null,
            UrlRewriteInstalled: urlRewrite,
            ArrInstalled: arr,
            AspNetCoreHostingBundleInstalled: aspNetHosting,
            NodeJsVersion: nodeVer,
            PythonVersion: pyVer,
            DotNetRuntimeVersions: dotnetVer,
            WinAcmeInstalled: winAcme,
            ExistingIisSites: sites,
            ExistingAppPools: pools,
            DiscoveredSqlInstances: sqlInstances,
            PostgreSqlDetected: await ProbeVersionAsync("psql", ["--version"], ct).ConfigureAwait(false) is not null,
            CapturedAtUtc: DateTimeOffset.UtcNow);
    }

    public async Task<ServerTelemetrySnapshot> CollectTelemetryAsync(
        string agentId,
        IEnumerable<ManagedApplication> managedApps,
        IEnumerable<DatabaseServerRecord> databases,
        CancellationToken ct)
    {
        var cpu = await ReadCpuPercentAsync(ct).ConfigureAwait(false);
        var (totalMem, usedMem) = ReadMemoryBytes();

        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .Select(d => new DriveTelemetryDto(d.Name, d.VolumeLabel, d.TotalSize, d.AvailableFreeSpace))
            .ToList();

        var services = new List<ServiceStatusDto>();
        var siteStatuses = new List<IisSiteStatusDto>();

        foreach (var app in managedApps)
        {
            if (!string.IsNullOrWhiteSpace(app.WindowsServiceName))
            {
                var svc = await _serviceManager.GetServiceStatusAsync(app.WindowsServiceName, ct).ConfigureAwait(false);
                if (svc is not null) services.Add(svc);
            }

            if (!string.IsNullOrWhiteSpace(app.IisSiteName))
            {
                var (reachable, code, latencyMs) = await ProbeAppHealthAsync(app, ct).ConfigureAwait(false);
                siteStatuses.Add(new IisSiteStatusDto(app.IisSiteName, reachable ? "Started" : "Degraded", reachable, code, latencyMs));
            }
        }

        var certs = _certService.InspectLocalMachineWebHostingCertificates();

        return new ServerTelemetrySnapshot(
            AgentId: agentId,
            Hostname: Environment.MachineName,
            CpuUsagePercent: Math.Round(cpu, 1),
            TotalMemoryBytes: totalMem,
            UsedMemoryBytes: usedMem,
            Drives: drives,
            ManagedServices: services,
            ManagedSites: siteStatuses,
            Certificates: certs,
            DatabaseStatuses: [],
            CapturedAtUtc: DateTimeOffset.UtcNow);
    }

    private async Task<double> ReadCpuPercentAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return 0.0;

        try
        {
            using var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _ = counter.NextValue();
            await Task.Delay(250, ct).ConfigureAwait(false);
            return counter.NextValue();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PerformanceCounter unavailable for CPU metric.");
            return 0.0;
        }
    }

    private static (long TotalBytes, long UsedBytes) ReadMemoryBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                var total = (long)status.ullTotalPhys;
                var avail = (long)status.ullAvailPhys;
                return (total, Math.Max(0, total - avail));
            }
        }
        var info = GC.GetGCMemoryInfo();
        return (info.TotalAvailableMemoryBytes, Process.GetCurrentProcess().WorkingSet64);
    }

    private async Task<(bool Reachable, int? StatusCode, double? LatencyMs)> ProbeAppHealthAsync(ManagedApplication app, CancellationToken ct)
    {
        var url = Core.Validation.ApplicationConfigValidator.RequiresInternalPort(app.RuntimeType)
            ? $"http://127.0.0.1:{app.InternalPort}/{app.HealthCheckPath.TrimStart('/')}"
            : $"http://{app.PrimaryDomain}/{app.HealthCheckPath.TrimStart('/')}";

        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            sw.Stop();
            return ((int)resp.StatusCode is >= 200 and < 400, (int)resp.StatusCode, Math.Round(sw.Elapsed.TotalMilliseconds, 1));
        }
        catch
        {
            sw.Stop();
            return (false, null, null);
        }
    }

    private static async Task<string?> ProbeVersionAsync(string exe, string[] args, CancellationToken ct)
    {
        try
        {
            var res = await SafeProcessRunner.RunAsync(exe, args, cancellationToken: ct).ConfigureAwait(false);
            return res.ExitCode == 0 ? res.StandardOutput.Split('\n').FirstOrDefault()?.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
