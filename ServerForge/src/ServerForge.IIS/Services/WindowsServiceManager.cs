using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ServerForge.Contracts.Dtos;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.Security.Execution;

namespace ServerForge.IIS.Services;

/// <summary>
/// Manages WinSW-wrapped native Windows Services for Node.js, Next.js, NestJS, Express, and Python workloads.
/// </summary>
public sealed class WindowsServiceManager : IWindowsServiceManager
{
    private readonly ILogger<WindowsServiceManager> _logger;
    private readonly string _winSwExecutablePath;

    public WindowsServiceManager(ILogger<WindowsServiceManager> logger, string winSwExecutablePath = @"C:\ProgramData\ServerForge\tools\winsw-x64.exe")
    {
        _logger = logger;
        _winSwExecutablePath = winSwExecutablePath;
    }

    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(false);

        return Task.FromResult(ServiceController.GetServices().Any(
            s => s.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task InstallOrUpdateWinSwServiceAsync(
        ManagedApplication app,
        string releasePath,
        IReadOnlyDictionary<string, string> envVars,
        CancellationToken ct)
    {
        var serviceDir = Path.Combine(app.ReleasesRootPath, "service-host");
        Directory.CreateDirectory(serviceDir);
        var logsDir = Path.Combine(app.ReleasesRootPath, "logs");
        Directory.CreateDirectory(logsDir);

        var wrapperExe = Path.Combine(serviceDir, $"{app.WindowsServiceName}.exe");
        var wrapperXml = Path.Combine(serviceDir, $"{app.WindowsServiceName}.xml");

        if (File.Exists(_winSwExecutablePath) && !File.Exists(wrapperExe))
            File.Copy(_winSwExecutablePath, wrapperExe, overwrite: false);

        var xmlContent = BuildWinSwConfigurationXml(app, releasePath, logsDir, envVars);
        await File.WriteAllTextAsync(wrapperXml, xmlContent, ct).ConfigureAwait(false);

        if (await ServiceExistsAsync(app.WindowsServiceName, ct).ConfigureAwait(false))
        {
            await StopServiceAsync(app.WindowsServiceName, ct).ConfigureAwait(false);
            await SafeProcessRunner.RunAsync(wrapperExe, ["refresh"], serviceDir, cancellationToken: ct).ConfigureAwait(false);
        }
        else if (File.Exists(wrapperExe))
        {
            var install = await SafeProcessRunner.RunAsync(wrapperExe, ["install"], serviceDir, cancellationToken: ct).ConfigureAwait(false);
            if (install.ExitCode != 0)
                throw new InvalidOperationException($"WinSW install failed for '{app.WindowsServiceName}': {install.StandardError}");
        }

        _logger.LogInformation("Configured Windows Service {ServiceName} targeting {ReleasePath}", app.WindowsServiceName, releasePath);
    }

    [SupportedOSPlatform("windows")]
    public Task StartServiceAsync(string serviceName, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Service control requires Windows.");

        using var sc = new ServiceController(serviceName);
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    public Task StopServiceAsync(string serviceName, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Service control requires Windows.");

        using var sc = new ServiceController(serviceName);
        if (sc.Status != ServiceControllerStatus.Stopped && sc.Status != ServiceControllerStatus.StopPending)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    public async Task RestartServiceAsync(string serviceName, CancellationToken ct)
    {
        await StopServiceAsync(serviceName, ct).ConfigureAwait(false);
        await StartServiceAsync(serviceName, ct).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    public Task<ServiceStatusDto?> GetServiceStatusAsync(string serviceName, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<ServiceStatusDto?>(null);

        var match = ServiceController.GetServices()
            .FirstOrDefault(s => s.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            return Task.FromResult<ServiceStatusDto?>(null);

        using (match)
        {
            return Task.FromResult<ServiceStatusDto?>(new ServiceStatusDto(
                match.ServiceName,
                match.DisplayName,
                match.Status.ToString(),
                0,
                null));
        }
    }

    public static string BuildWinSwConfigurationXml(
        ManagedApplication app,
        string releasePath,
        string logsDirectory,
        IReadOnlyDictionary<string, string> envVars)
    {
        var (exe, args) = SplitExecutableAndArgs(app.StartCommand);
        var serviceEl = new XElement("service",
            new XElement("id", app.WindowsServiceName),
            new XElement("name", $"ServerForge App — {app.Name}"),
            new XElement("description", $"Managed by ServerForge ({app.RuntimeType}) on internal port {app.InternalPort}"),
            new XElement("executable", exe),
            new XElement("arguments", args),
            new XElement("workingdirectory", releasePath),
            new XElement("logpath", logsDirectory),
            new XElement("log",
                new XAttribute("mode", "roll-by-size"),
                new XElement("sizeThreshold", "10240"),
                new XElement("keepFiles", "8")),
            new XElement("onfailure", new XAttribute("action", "restart"), new XAttribute("delay", "5 sec")),
            new XElement("env", new XAttribute("name", "PORT"), new XAttribute("value", app.InternalPort.ToString())),
            new XElement("env", new XAttribute("name", "HOST"), new XAttribute("value", "127.0.0.1")));

        foreach (var (key, value) in envVars.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            serviceEl.Add(new XElement("env", new XAttribute("name", key), new XAttribute("value", value)));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), serviceEl).ToString();
    }

    private static (string Executable, string Arguments) SplitExecutableAndArgs(string command)
    {
        var trimmed = command.Trim();
        var spaceIdx = trimmed.IndexOf(' ');
        return spaceIdx < 0 ? (trimmed, string.Empty) : (trimmed[..spaceIdx], trimmed[(spaceIdx + 1)..].Trim());
    }
}
