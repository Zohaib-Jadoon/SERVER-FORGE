using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ServerForge.Security.Certificates;

namespace ServerForge.Agent.Configuration;

public sealed class AgentOptions
{
    public string ServiceName { get; set; } = "ServerForgeAgent";
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9443;
    public string DataRoot { get; set; } = @"C:\ProgramData\ServerForge\Agent";
    public string AppsRoot { get; set; } = @"C:\ServerForgeApps";
    public int GitHubPollIntervalSeconds { get; set; } = 60;
    public bool RequireVpnOrPrivateNetwork { get; set; } = true;
}

public sealed class AgentIdentityState
{
    public string AgentId { get; set; } = Guid.NewGuid().ToString("N");
    public string? PairedDesktopInstanceId { get; set; }
    public string? PairedDesktopCertThumbprint { get; set; }
    public string? PreviousDesktopCertThumbprint { get; set; }
    public string? ProtectedGitHubPat { get; set; }
    public DateTimeOffset? PairedAtUtc { get; set; }
    public DateTimeOffset? LastCertRotatedAtUtc { get; set; }
    public bool IsPaired => !string.IsNullOrWhiteSpace(PairedDesktopCertThumbprint);
}

public sealed class AgentIdentityStore
{
    private readonly string _statePath;
    private readonly string _certPfxPath;
    private readonly object _lock = new();

    public AgentIdentityStore(AgentOptions options)
    {
        Directory.CreateDirectory(options.DataRoot);
        _statePath = Path.Combine(options.DataRoot, "agent-identity.json");
        _certPfxPath = Path.Combine(options.DataRoot, "agent-server.pfx");
        State = LoadOrCreateState();
    }

    public AgentIdentityState State { get; private set; }

    public X509Certificate2 LoadOrCreateServerCertificate()
    {
        const X509KeyStorageFlags storageFlags =
            X509KeyStorageFlags.PersistKeySet |
            X509KeyStorageFlags.Exportable;

        if (File.Exists(_certPfxPath))
            return X509CertificateLoader.LoadPkcs12FromFile(_certPfxPath, password: null, storageFlags);

        using var ephemeralCert = MutualTlsCertificateManager.CreateSelfSignedIdentityCertificate(
            commonName: Environment.MachineName,
            isServerCertificate: true,
            subjectAlternativeDnsNames: [Environment.MachineName, "localhost"],
            subjectAlternativeIps: [IPAddress.Loopback, IPAddress.IPv6Loopback]);

        File.WriteAllBytes(_certPfxPath, ephemeralCert.Export(X509ContentType.Pfx));
        return X509CertificateLoader.LoadPkcs12FromFile(_certPfxPath, password: null, storageFlags);
    }

    public void CompletePairing(string desktopInstanceId, string desktopCertThumbprint)
    {
        lock (_lock)
        {
            State.PairedDesktopInstanceId = desktopInstanceId;
            State.PairedDesktopCertThumbprint = MutualTlsCertificateManager.NormalizeThumbprint(desktopCertThumbprint);
            State.PairedAtUtc = DateTimeOffset.UtcNow;
            PersistState();
        }
    }

    public string RotateDesktopCertificate(string desktopInstanceId, string newDesktopCertThumbprint)
    {
        lock (_lock)
        {
            if (!string.Equals(State.PairedDesktopInstanceId, desktopInstanceId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Desktop instance ID does not match the paired desktop.");

            var normalized = MutualTlsCertificateManager.NormalizeThumbprint(newDesktopCertThumbprint);
            State.PreviousDesktopCertThumbprint = State.PairedDesktopCertThumbprint;
            State.PairedDesktopCertThumbprint = normalized;
            State.LastCertRotatedAtUtc = DateTimeOffset.UtcNow;
            PersistState();
            return normalized;
        }
    }

    public void StoreProtectedGitHubPat(string protectedPat)
    {
        lock (_lock)
        {
            State.ProtectedGitHubPat = protectedPat;
            PersistState();
        }
    }

    private AgentIdentityState LoadOrCreateState()
    {
        if (File.Exists(_statePath))
        {
            var json = File.ReadAllText(_statePath);
            var parsed = JsonSerializer.Deserialize<AgentIdentityState>(json);
            if (parsed is not null) return parsed;
        }

        var fresh = new AgentIdentityState();
        File.WriteAllText(_statePath, JsonSerializer.Serialize(fresh, new JsonSerializerOptions { WriteIndented = true }));
        return fresh;
    }

    private void PersistState()
    {
        File.WriteAllText(_statePath, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
    }
}
