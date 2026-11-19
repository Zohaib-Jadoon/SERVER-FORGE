using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ServerForge.Core.Interfaces;

namespace ServerForge.Security.Secrets;

/// <summary>
/// Windows DPAPI secret protector using CurrentUser (Desktop) or LocalMachine (Agent service identity with ACL-locked store).
/// </summary>
public sealed partial class DpapiSecretProtector : ISecretProtector
{
    private readonly DataProtectionScope _scope;

    public DpapiSecretProtector(DataProtectionScope scope = DataProtectionScope.CurrentUser)
    {
        _scope = scope;
    }

    [SupportedOSPlatform("windows")]
    public string Protect(string plaintext, string purposeContext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(purposeContext);

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ServerForge DPAPI secret storage requires Windows.");

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes($"ServerForge::DPAPI::{purposeContext}"));
        var protectedBytes = ProtectedData.Protect(plainBytes, entropy, _scope);
        CryptographicOperations.ZeroMemory(plainBytes);
        return Convert.ToBase64String(protectedBytes);
    }

    [SupportedOSPlatform("windows")]
    public string Unprotect(string protectedBase64, string purposeContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedBase64);
        ArgumentException.ThrowIfNullOrWhiteSpace(purposeContext);

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ServerForge DPAPI secret storage requires Windows.");

        var protectedBytes = Convert.FromBase64String(protectedBase64);
        var entropy = SHA256.HashData(Encoding.UTF8.GetBytes($"ServerForge::DPAPI::{purposeContext}"));
        var plainBytes = ProtectedData.Unprotect(protectedBytes, entropy, _scope);
        try
        {
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    public string RedactSecrets(string input, IEnumerable<string> knownSecrets)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var sanitized = input;
        foreach (var secret in knownSecrets.Where(s => !string.IsNullOrEmpty(s) && s.Length >= 4))
        {
            sanitized = sanitized.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        // Redact GitHub PATs, bearer tokens, and SQL/PG password fields defensively
        sanitized = GitHubTokenRegex().Replace(sanitized, "[REDACTED_GITHUB_TOKEN]");
        sanitized = BearerTokenRegex().Replace(sanitized, "Bearer [REDACTED]");
        sanitized = ConnectionPasswordRegex().Replace(sanitized, "$1=[REDACTED]");
        return sanitized;
    }

    [GeneratedRegex(@"\b(?:ghp|gho|ghu|ghs|ghr|github_pat)_[A-Za-z0-9_]{20,}\b", RegexOptions.Compiled)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(?i)(Password|Pwd|Secret|ApiKey|Token)\s*=\s*[^;\r\n\s]+", RegexOptions.Compiled)]
    private static partial Regex ConnectionPasswordRegex();
}

/// <summary>
/// PBKDF2-SHA512 password hasher for local administrator profile authentication.
/// </summary>
public static class LocalPasswordHasher
{
    private const int SaltSize = 32;
    private const int KeySize = 64;
    private const int Iterations = 210_000;

    public static (string HashBase64, string SaltBase64) HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, KeySize);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool VerifyPassword(string password, string hashBase64, string saltBase64)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(hashBase64) || string.IsNullOrWhiteSpace(saltBase64))
            return false;

        var salt = Convert.FromBase64String(saltBase64);
        var expectedHash = Convert.FromBase64String(hashBase64);
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA512, KeySize);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
