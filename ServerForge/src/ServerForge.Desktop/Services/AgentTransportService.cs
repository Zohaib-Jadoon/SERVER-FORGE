using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using ServerForge.Contracts.Dtos;
using ServerForge.Core.Entities;
using ServerForge.Security.Certificates;

namespace ServerForge.Desktop.Services;

public sealed class DesktopIdentityService
{
    private readonly string _pfxPath;
    private readonly string _backupPfxPath;
    private readonly string _idPath;

    public DesktopIdentityService(string localDataRoot)
    {
        Directory.CreateDirectory(localDataRoot);
        _pfxPath = Path.Combine(localDataRoot, "desktop-client.pfx");
        _backupPfxPath = Path.Combine(localDataRoot, "desktop-client.prev.pfx");
        _idPath = Path.Combine(localDataRoot, "desktop-instance-id.txt");

        InstanceId = File.Exists(_idPath)
            ? File.ReadAllText(_idPath).Trim()
            : CreateAndSaveInstanceId();
    }

    public string InstanceId { get; }

    public X509Certificate2 LoadOrCreateClientCertificate()
    {
        if (File.Exists(_pfxPath))
            return X509CertificateLoader.LoadPkcs12FromFile(_pfxPath, password: null);

        var cert = MutualTlsCertificateManager.CreateSelfSignedIdentityCertificate(
            commonName: $"ServerForgeDesktop-{Environment.MachineName}",
            isServerCertificate: false);

        File.WriteAllBytes(_pfxPath, cert.Export(X509ContentType.Pfx));
        return cert;
    }

    /// <summary>
    /// Creates a new ECDSA P-384 Desktop client certificate and stages it without deleting the previous certificate until rotation succeeds.
    /// </summary>
    public X509Certificate2 CreateReplacementClientCertificate()
        => MutualTlsCertificateManager.CreateSelfSignedIdentityCertificate(
            commonName: $"ServerForgeDesktop-{Environment.MachineName}",
            isServerCertificate: false);

    public void CommitRotatedClientCertificate(X509Certificate2 newCert)
    {
        if (File.Exists(_pfxPath))
            File.Copy(_pfxPath, _backupPfxPath, overwrite: true);
        File.WriteAllBytes(_pfxPath, newCert.Export(X509ContentType.Pfx));
    }

    private string CreateAndSaveInstanceId()
    {
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(_idPath, id);
        return id;
    }
}

/// <summary>
/// Full Mutual-TLS HTTP client communicating securely with paired ServerForge Agents over VPN/private networks.
/// Enforces pinned server certificate thumbprint verification (fail-closed) on every request.
/// </summary>
public sealed class AgentTransportService
{
    private readonly DesktopIdentityService _identity;

    public AgentTransportService(DesktopIdentityService identity) => _identity = identity;

    public string GetCurrentClientThumbprint()
    {
        using var cert = _identity.LoadOrCreateClientCertificate();
        return MutualTlsCertificateManager.NormalizeThumbprint(cert.Thumbprint);
    }

    public async Task<PairingInitResponse> PairWithAgentAsync(
        string hostAddress,
        int port,
        string oneTimePairingCode,
        string expectedAgentThumbprint,
        CancellationToken ct)
    {
        using var clientCert = _identity.LoadOrCreateClientCertificate();
        using var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(clientCert);
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            cert is not null &&
            MutualTlsCertificateManager.VerifyPinnedCertificateThumbprint(
                new X509Certificate2(cert), expectedAgentThumbprint);

        using var http = new HttpClient(handler) { BaseAddress = new Uri($"https://{hostAddress}:{port}/"), Timeout = TimeSpan.FromSeconds(15) };
        var request = new PairingInitRequest(
            DesktopInstanceId: _identity.InstanceId,
            DesktopDisplayName: Environment.MachineName,
            DesktopClientCertPem: clientCert.ExportCertificatePem(),
            OneTimePairingCode: oneTimePairingCode.Trim(),
            RequestedAtUtc: DateTimeOffset.UtcNow);

        using var resp = await http.PostAsJsonAsync("api/v1/pairing/complete", request, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return new PairingInitResponse(false, "", hostAddress, "", "",
                $"Pairing failed (HTTP {(int)resp.StatusCode}). Confirm the pairing code has not expired and the agent is reachable over VPN.");
        }

        return await resp.Content.ReadFromJsonAsync<PairingInitResponse>(cancellationToken: ct).ConfigureAwait(false)
               ?? new PairingInitResponse(false, "", hostAddress, "", "", "Empty pairing response.");
    }

    public async Task<RotateDesktopCertificateResponse> RotateDesktopCertificateAsync(
        ManagedServer server,
        string reason,
        CancellationToken ct)
    {
        using var newCert = _identity.CreateReplacementClientCertificate();
        using var http = CreatePinnedClient(server);
        var req = new RotateDesktopCertificateRequest(_identity.InstanceId, newCert.ExportCertificatePem(), reason);
        using var resp = await http.PostAsJsonAsync("api/v1/security/rotate-desktop-cert", req, ct).ConfigureAwait(false);
        var payload = await resp.Content.ReadFromJsonAsync<RotateDesktopCertificateResponse>(cancellationToken: ct).ConfigureAwait(false)
                      ?? new RotateDesktopCertificateResponse(false, "", DateTimeOffset.UtcNow, $"HTTP {(int)resp.StatusCode}");

        if (resp.IsSuccessStatusCode && payload.Rotated)
            _identity.CommitRotatedClientCertificate(newCert);

        return payload;
    }

    public async Task SyncGitHubPatAsync(ManagedServer server, string pat, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        using var resp = await http.PostAsJsonAsync("api/v1/github/credentials", new SyncGitHubCredentialRequest(pat), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ServerDiagnosticsSnapshot?> FetchDiagnosticsAsync(ManagedServer server, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        return await http.GetFromJsonAsync<ServerDiagnosticsSnapshot>("api/v1/diagnostics", ct).ConfigureAwait(false);
    }

    public async Task<ServerTelemetrySnapshot?> FetchTelemetryAsync(ManagedServer server, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        return await http.GetFromJsonAsync<ServerTelemetrySnapshot>("api/v1/telemetry", ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiscoveredIisSiteDto>> FetchIisSitesAsync(ManagedServer server, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        return await http.GetFromJsonAsync<List<DiscoveredIisSiteDto>>("api/v1/iis/sites", ct).ConfigureAwait(false) ?? [];
    }

    public async Task AdoptIisSiteAsync(ManagedServer server, string siteName, string note, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        using var resp = await http.PostAsJsonAsync("api/v1/iis/sites/adopt",
            new AdoptIisSiteRequest(siteName, OperatorReviewedDryRun: true, note), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task SyncProjectAsync(ManagedServer server, ProjectGroup project, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        using var resp = await http.PostAsJsonAsync("api/v1/projects", project, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ManagedApplication?> RegisterOrUpdateApplicationAsync(ManagedServer server, UpsertApplicationRequest req, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        HttpResponseMessage resp;
        if (req.Id.HasValue)
        {
            resp = await http.PutAsJsonAsync($"api/v1/applications/{req.Id.Value}", req, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                resp.Dispose();
                resp = await http.PostAsJsonAsync("api/v1/applications", req, ct).ConfigureAwait(false);
            }
        }
        else
        {
            resp = await http.PostAsJsonAsync("api/v1/applications", req, ct).ConfigureAwait(false);
        }

        using (resp)
        {
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<ManagedApplication>(cancellationToken: ct).ConfigureAwait(false);
        }
    }

    public async Task RemoveApplicationFromManagementAsync(ManagedServer server, Guid applicationId, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        using var resp = await http.DeleteAsync($"api/v1/applications/{applicationId}", ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ConfigureEnvVarsAsync(ManagedServer server, Guid applicationId, ConfigureEnvVarsRequest req, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        using var resp = await http.PutAsJsonAsync($"api/v1/applications/{applicationId}/env", req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ConfigureDomainAsync(ManagedServer server, Guid applicationId, ConfigureDomainRequest req, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        using var resp = await http.PutAsJsonAsync($"api/v1/applications/{applicationId}/domains", req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ReleaseSummaryDto>> FetchReleasesAsync(ManagedServer server, Guid applicationId, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        return await http.GetFromJsonAsync<List<ReleaseSummaryDto>>($"api/v1/applications/{applicationId}/releases", ct).ConfigureAwait(false) ?? [];
    }

    public async Task<DeploymentRecord?> TriggerDeploymentAsync(ManagedServer server, TriggerDeploymentRequest req, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        using var resp = await http.PostAsJsonAsync("api/v1/deployments/trigger", req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<DeploymentRecord>(cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<DeploymentRecord?> RollbackApplicationAsync(ManagedServer server, Guid applicationId, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        using var resp = await http.PostAsync($"api/v1/deployments/{applicationId}/rollback", content: null, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<DeploymentRecord>(cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditLogRecord>> FetchAuditLogsAsync(ManagedServer server, CancellationToken ct)
    {
        using var http = CreatePinnedClient(server);
        return await http.GetFromJsonAsync<List<AuditLogRecord>>("api/v1/audit", ct).ConfigureAwait(false) ?? [];
    }

    private HttpClient CreatePinnedClient(ManagedServer server)
    {
        var clientCert = _identity.LoadOrCreateClientCertificate();
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(clientCert);
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            cert is not null &&
            MutualTlsCertificateManager.VerifyPinnedCertificateThumbprint(
                new X509Certificate2(cert), server.PinnedServerCertThumbprint);

        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{server.HostAddress}:{server.AgentPort}/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }
}
