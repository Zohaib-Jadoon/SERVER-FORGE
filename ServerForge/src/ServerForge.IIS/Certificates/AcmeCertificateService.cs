using System.Net;
using System.Security.Cryptography.X509Certificates;
using ServerForge.Contracts.Dtos;
using ServerForge.Core.Validation;
using ServerForge.Security.Execution;

namespace ServerForge.IIS.Certificates;

/// <summary>
/// Verifies DNS resolution and invokes win-acme (wacs.exe) for automated Let's Encrypt HTTPS certificates on IIS.
/// Never claims IIS binding creation configures public DNS records.
/// </summary>
public sealed class AcmeCertificateService
{
    private readonly string _wacsPath;

    public AcmeCertificateService(string wacsPath = @"C:\ProgramData\ServerForge\tools\win-acme\wacs.exe")
    {
        _wacsPath = wacsPath;
    }

    public async Task<(bool MatchesExpectedIp, IReadOnlyList<string> ResolvedIps, string Guidance)> VerifyDnsAsync(
        string hostname,
        string expectedServerPublicIp,
        CancellationToken ct)
    {
        var validation = DomainValidator.ValidateHostname(hostname, allowLocalTestHostname: true);
        if (!validation.IsValid)
            return (false, [], string.Join(" ", validation.Errors));

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname, ct).ConfigureAwait(false);
            var ipStrings = addresses.Select(a => a.ToString()).Distinct().ToList();
            var matched = ipStrings.Contains(expectedServerPublicIp, StringComparer.OrdinalIgnoreCase);

            var guidance = matched
                ? $"DNS verified: {hostname} resolves to {expectedServerPublicIp}."
                : DomainValidator.BuildManualDnsInstructions(hostname, expectedServerPublicIp);

            return (matched, ipStrings, guidance);
        }
        catch (Exception ex)
        {
            return (false, [],
                $"DNS lookup failed for '{hostname}': {ex.Message}\n\n{DomainValidator.BuildManualDnsInstructions(hostname, expectedServerPublicIp)}");
        }
    }

    public async Task<SafeProcessResult> RequestLetsEncryptCertificateAsync(
        string hostname,
        long iisSiteId,
        string contactEmail,
        bool dryRunStaging,
        CancellationToken ct)
    {
        if (!File.Exists(_wacsPath))
        {
            throw new FileNotFoundException(
                $"win-acme (wacs.exe) was not found at '{_wacsPath}'. Install win-acme or configure its path in Settings.");
        }

        var args = new List<string>
        {
            "--target", "manual",
            "--host", hostname,
            "--installation", "iis",
            "--installationsiteid", iisSiteId.ToString(),
            "--validation", "selfhosting",
            "--emailaddress", contactEmail,
            "--accepttos",
            "--closeonfinish"
        };

        if (dryRunStaging)
            args.Add("--test");

        return await SafeProcessRunner.RunAsync(_wacsPath, args, cancellationToken: ct).ConfigureAwait(false);
    }

    public IReadOnlyList<CertificateStatusDto> InspectLocalMachineWebHostingCertificates()
    {
        var results = new List<CertificateStatusDto>();
        if (!OperatingSystem.IsWindows())
            return results;

        foreach (var storeName in new[] { "WebHosting", "My" })
        {
            try
            {
                using var store = new X509Store(storeName, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly);
                foreach (var cert in store.Certificates)
                {
                    var days = (int)Math.Floor((cert.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays);
                    results.Add(new CertificateStatusDto(
                        Subject: cert.Subject,
                        Thumbprint: cert.Thumbprint,
                        Hostname: cert.GetNameInfo(X509NameType.DnsName, false) ?? cert.Subject,
                        NotAfterUtc: cert.NotAfter.ToUniversalTime(),
                        DaysRemaining: days,
                        ManagedByWinAcme: cert.FriendlyName.Contains("win-acme", StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch
            {
                // Non-privileged inspection fallback
            }
        }

        return results.DistinctBy(c => c.Thumbprint).OrderBy(c => c.DaysRemaining).ToList();
    }
}
