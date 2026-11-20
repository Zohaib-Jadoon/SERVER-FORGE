using System.Net;
using System.Text.RegularExpressions;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;

namespace ServerForge.Core.Validation;

public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ValidationResult Success() => new(true, []);
    public static ValidationResult Failure(params string[] errors) => new(false, errors);
}

public static partial class DomainValidator
{
    [GeneratedRegex(@"^(?=.{1,253}$)(?!-)(?:[a-zA-Z0-9-]{1,63}(?<!-)\.)+[a-zA-Z]{2,63}$", RegexOptions.Compiled)]
    private static partial Regex FqdnRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9-]{0,62}(\.local|\.localhost|\.internal)?$", RegexOptions.Compiled)]
    private static partial Regex LocalHostnameRegex();

    public static ValidationResult ValidateHostname(string? hostname, bool allowLocalTestHostname = true)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return ValidationResult.Failure("Hostname is required.");

        var trimmed = hostname.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal) || trimmed.Contains('/', StringComparison.Ordinal))
            return ValidationResult.Failure("Enter only the hostname (e.g., app.example.com), without protocol or path.");

        if (IPAddress.TryParse(trimmed, out _))
            return ValidationResult.Failure("Raw IP addresses are not valid custom domain names.");

        if (FqdnRegex().IsMatch(trimmed))
            return ValidationResult.Success();

        if (allowLocalTestHostname && (trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase) || LocalHostnameRegex().IsMatch(trimmed)))
            return ValidationResult.Success();

        return ValidationResult.Failure($"'{trimmed}' is not a valid domain or hostname.");
    }

    public static string BuildManualDnsInstructions(string hostname, string targetServerIp)
    {
        var isApex = hostname.Split('.').Length == 2;
        var recordType = isApex ? "A" : "A / CNAME";
        var hostLabel = isApex ? "@" : hostname[..hostname.IndexOf('.')];

        return $"""
                DNS Configuration Required (ServerForge does not modify public DNS records automatically):
                • Record Type: {recordType}
                • Host / Name: {hostLabel}
                • Target Value: {targetServerIp}
                • TTL: 300 seconds (or provider default)

                Wait for DNS propagation and run "Verify DNS Resolution" before requesting a Let's Encrypt certificate.
                """;
    }
}

public static partial class ApplicationConfigValidator
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{1,48}[a-z0-9]$", RegexOptions.Compiled)]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled)]
    private static partial Regex RepoRegex();

    public static ValidationResult Validate(ManagedApplication app)
    {
        var errors = new List<string>();

        if (app.ProjectGroupId == Guid.Empty)
            errors.Add("Application must belong to a valid ProjectGroup (ProjectGroupId cannot be empty).");

        if (string.IsNullOrWhiteSpace(app.Name))
            errors.Add("Application name is required.");

        if (string.IsNullOrWhiteSpace(app.Slug) || !SlugRegex().IsMatch(app.Slug))
            errors.Add("Application slug must be 3–50 lowercase alphanumeric characters or hyphens.");

        if (!string.IsNullOrWhiteSpace(app.RepositoryFullName) && !RepoRegex().IsMatch(app.RepositoryFullName))
            errors.Add("GitHub repository must follow 'owner/repository' format.");

        if (app.AutoDeployEnabled && !app.RepositoryExplicitlyAuthorized)
            errors.Add("Automatic deployment requires explicit repository authorization.");

        if (string.IsNullOrWhiteSpace(app.Branch) || app.Branch.Contains("..", StringComparison.Ordinal) || app.Branch.Contains(' '))
            errors.Add("Deployment branch name is invalid.");

        if (app.WorkingDirectory.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(app.WorkingDirectory))
            errors.Add("Working directory must be a relative path inside the repository without '..' traversal.");

        if (RequiresInternalPort(app.RuntimeType) && (app.InternalPort < 1024 || app.InternalPort > 65535))
            errors.Add($"Runtime '{app.RuntimeType}' requires an internal loopback port between 1024 and 65535.");

        if (!string.IsNullOrWhiteSpace(app.PrimaryDomain))
        {
            var domainResult = DomainValidator.ValidateHostname(app.PrimaryDomain, allowLocalTestHostname: true);
            if (!domainResult.IsValid)
                errors.AddRange(domainResult.Errors);
        }

        if (app.RuntimeType == ApplicationRuntimeType.ClassicAsp &&
            app.ManagementState == ResourceManagementState.ReadOnlyUnmanaged)
        {
            errors.Add("Pre-existing Classic ASP sites are read-only and must be explicitly adopted before deployment.");
        }

        return errors.Count == 0 ? ValidationResult.Success() : new ValidationResult(false, errors);
    }

    public static bool RequiresInternalPort(ApplicationRuntimeType runtime) => runtime is
        ApplicationRuntimeType.NextJsSsr or
        ApplicationRuntimeType.NestJsApi or
        ApplicationRuntimeType.ExpressApi or
        ApplicationRuntimeType.NodeJsGeneric or
        ApplicationRuntimeType.PythonFastApi;
}

public static class EnvironmentFileParser
{
    public static IReadOnlyDictionary<string, string> ParseDotEnv(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex <= 0)
                continue;

            var key = trimmed[..eqIndex].Trim();
            var value = trimmed[(eqIndex + 1)..].Trim();

            if (value.Length >= 2 &&
                ((value.StartsWith('"') && value.EndsWith('"')) ||
                 (value.StartsWith('\'') && value.EndsWith('\''))))
            {
                value = value[1..^1];
            }

            result[key] = value;
        }

        return result;
    }

    public static string ExportDotEnvWithoutSecrets(IEnumerable<EnvironmentVariableRecord> records)
    {
        var lines = new List<string>
        {
            "# Exported by ServerForge (Secret values redacted by default)",
            $"# Timestamp: {DateTimeOffset.UtcNow:O}"
        };

        foreach (var rec in records.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            lines.Add(rec.IsSecret
                ? $"{rec.Key}=<REDACTED_SECRET>"
                : $"{rec.Key}={rec.ProtectedValue}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
