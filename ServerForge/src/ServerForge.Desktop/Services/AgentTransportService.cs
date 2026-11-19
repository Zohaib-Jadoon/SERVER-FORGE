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
    private readonly string _idPath;

    public DesktopIdentityService(string localDataRoot)
    {
        Directory.CreateDirectory(localDataRoot);
        _pfxPath = Path.Combine(localDataRoot, "desktop-client.pfx");
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

    private string CreateAndSaveInstanceId()
    {
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(_idPath, id);
        return id;
    }
}

/// <summary>
/// Mutual-TLS HTTP client communicating securely with paired ServerForge Agents over VPN/private networks.
/// Enforces pinned server certificate thumbprint verification (fail-closed).
/// </summary>
public sealed class AgentTransportService
{
    private readonly DesktopIdentityService _identity;

    public AgentTransportService(DesktopIdentityService identity) => _identity = identity;

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
