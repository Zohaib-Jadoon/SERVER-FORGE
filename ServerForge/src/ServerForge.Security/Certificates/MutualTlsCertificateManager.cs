using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ServerForge.Security.Certificates;

/// <summary>
/// Manages mutual-TLS (mTLS) X.509 certificates and expiring one-time pairing codes between Desktop and Agent.
/// </summary>
public static class MutualTlsCertificateManager
{
    public static X509Certificate2 CreateSelfSignedIdentityCertificate(
        string commonName,
        bool isServerCertificate,
        IEnumerable<string>? subjectAlternativeDnsNames = null,
        IEnumerable<IPAddress>? subjectAlternativeIps = null,
        int validityYears = 3)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var subject = new X500DistinguishedName($"CN={commonName}, O=ServerForge, OU={(isServerCertificate ? "Agent" : "Desktop")}");
        var request = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA384);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));

        var eku = new OidCollection
        {
            new Oid(isServerCertificate ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") // ServerAuth or ClientAuth
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, critical: true));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(commonName);
        if (subjectAlternativeDnsNames is not null)
        {
            foreach (var dns in subjectAlternativeDnsNames.Where(d => !string.IsNullOrWhiteSpace(d)))
                sanBuilder.AddDnsName(dns);
        }
        if (subjectAlternativeIps is not null)
        {
            foreach (var ip in subjectAlternativeIps)
                sanBuilder.AddIpAddress(ip);
        }
        request.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore.AddYears(validityYears);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    public static bool VerifyPinnedCertificateThumbprint(X509Certificate2? presentedCertificate, string expectedThumbprint)
    {
        if (presentedCertificate is null || string.IsNullOrWhiteSpace(expectedThumbprint))
            return false; // Fail-closed

        var now = DateTime.UtcNow;
        if (now < presentedCertificate.NotBefore.ToUniversalTime() || now > presentedCertificate.NotAfter.ToUniversalTime())
            return false;

        var actual = NormalizeThumbprint(presentedCertificate.Thumbprint);
        var expected = NormalizeThumbprint(expectedThumbprint);

        return actual.Length == expected.Length &&
               CryptographicOperations.FixedTimeEquals(
                   Encoding.ASCII.GetBytes(actual),
                   Encoding.ASCII.GetBytes(expected));
    }

    public static string NormalizeThumbprint(string thumbprint)
        => thumbprint.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
}

public sealed class OneTimePairingTokenService
{
    private readonly object _lock = new();
    private string? _activeTokenHashHex;
    private DateTimeOffset _expiresAtUtc;
    private bool _consumed;

    public (string PlaintextCode, DateTimeOffset ExpiresAtUtc) GeneratePairingCode(TimeSpan? lifetime = null)
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        var rawHex = Convert.ToHexString(bytes);
        var formatted = $"{rawHex[..4]}-{rawHex[4..8]}-{rawHex[8..12]}-{rawHex[12..16]}-{rawHex[16..20]}";
        var expires = DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(15));

        lock (_lock)
        {
            _activeTokenHashHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(formatted)));
            _expiresAtUtc = expires;
            _consumed = false;
        }

        return (formatted, expires);
    }

    public bool TryValidateAndConsume(string candidateCode, DateTimeOffset? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(candidateCode))
            return false;

        var normalized = candidateCode.Trim().ToUpperInvariant();
        var candidateHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

        lock (_lock)
        {
            if (_consumed || string.IsNullOrEmpty(_activeTokenHashHex))
                return false;

            if ((nowUtc ?? DateTimeOffset.UtcNow) > _expiresAtUtc)
                return false;

            var matched = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(candidateHash),
                Encoding.ASCII.GetBytes(_activeTokenHashHex));

            if (!matched)
                return false;

            _consumed = true;
            return true;
        }
    }
}
