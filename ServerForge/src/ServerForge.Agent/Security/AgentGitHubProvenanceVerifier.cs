using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServerForge.Agent.Configuration;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;

namespace ServerForge.Agent.Security;

/// <summary>
/// Server-side GitHub Actions artifact provenance verifier.
/// Independently queries the Agent's authorized GitHub API client using the Agent's DPAPI-protected PAT,
/// verifies repository authorization, branch, exact commit SHA, successful workflow run, artifact membership,
/// non-expired status, and server-reported SHA-256 digest before downloading into an isolated Agent path.
/// Never trusts caller-supplied local file paths, untrusted URLs, or unverified numeric run/artifact IDs.
/// </summary>
public sealed partial class AgentGitHubProvenanceVerifier : IGitHubArtifactProvenanceVerifier
{
    private readonly IGitHubClient _github;
    private readonly ISecretProtector _secretProtector;
    private readonly AgentIdentityStore _identityStore;
    private readonly IPathBoundaryGuard _pathGuard;
    private readonly AgentOptions _options;
    private readonly ILogger<AgentGitHubProvenanceVerifier> _logger;

    [GeneratedRegex(@"^[0-9a-fA-F]{40}$", RegexOptions.Compiled)]
    private static partial Regex FullGitShaRegex();

    public AgentGitHubProvenanceVerifier(
        IGitHubClient github,
        ISecretProtector secretProtector,
        AgentIdentityStore identityStore,
        IPathBoundaryGuard pathGuard,
        IOptions<AgentOptions> options,
        ILogger<AgentGitHubProvenanceVerifier> logger)
    {
        _github = github;
        _secretProtector = secretProtector;
        _identityStore = identityStore;
        _pathGuard = pathGuard;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<VerifiedGitHubArtifact> ResolveAndVerifyArtifactAsync(
        ManagedApplication application,
        string branch,
        string commitSha,
        long? requestedWorkflowRunId = null,
        long? requestedArtifactId = null,
        CancellationToken ct = default)
    {
        if (!application.RepositoryExplicitlyAuthorized)
        {
            return Fail($"Repository '{application.RepositoryFullName}' is not explicitly authorized for deployment.");
        }

        if (string.IsNullOrWhiteSpace(application.RepositoryFullName) || !application.RepositoryFullName.Contains('/'))
        {
            return Fail("Application has no valid authorized GitHub repository ('owner/repo') configured.");
        }

        if (!string.Equals(branch, application.Branch, StringComparison.Ordinal))
        {
            return Fail($"Branch mismatch: application is authorized for '{application.Branch}', not '{branch}'.");
        }

        if (!FullGitShaRegex().IsMatch(commitSha ?? string.Empty))
        {
            return Fail($"GitHub artifact provenance requires an exact 40-character commit SHA (received '{commitSha}').");
        }

        var protectedPat = _identityStore.State.ProtectedGitHubPat;
        if (string.IsNullOrWhiteSpace(protectedPat))
        {
            return Fail("No GitHub Personal Access Token is configured on the Agent; cannot independently verify GitHub Actions artifact provenance.");
        }

        string pat;
        try
        {
            pat = _secretProtector.Unprotect(protectedPat, "agent:github-pat");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unprotect Agent GitHub PAT during artifact provenance verification.");
            return Fail("Unable to decrypt Agent GitHub credential for server-side provenance verification.");
        }

        var safeArtifactPath = _pathGuard.EnsureWithinRoot(
            _options.DataRoot,
            Path.Combine("artifacts", application.Slug, $"{commitSha.ToLowerInvariant()}.zip"));

        var dl = await _github.DownloadVerifiedWorkflowArtifactAsync(
            pat,
            application.RepositoryFullName,
            application.Branch,
            commitSha,
            application.GitHubWorkflowFileName,
            safeArtifactPath,
            ct).ConfigureAwait(false);

        if (!dl.Found ||
            string.IsNullOrWhiteSpace(dl.LocalZipPath) ||
            string.IsNullOrWhiteSpace(dl.TrustedApiDigestSha256) ||
            !dl.WorkflowRunId.HasValue ||
            !dl.ArtifactId.HasValue)
        {
            return Fail(dl.ErrorMessage ?? "GitHub Actions artifact provenance could not be verified.");
        }

        // If the caller asserted a specific workflow run ID or artifact ID, verify it matches GitHub API truth
        if (requestedWorkflowRunId.HasValue && requestedWorkflowRunId.Value != dl.WorkflowRunId.Value)
        {
            return Fail(
                $"Workflow run provenance mismatch: caller supplied run ID {requestedWorkflowRunId.Value}, " +
                $"but GitHub API verified run ID {dl.WorkflowRunId.Value} for {application.RepositoryFullName}@{commitSha[..8]}.");
        }

        if (requestedArtifactId.HasValue && requestedArtifactId.Value != dl.ArtifactId.Value)
        {
            return Fail(
                $"Artifact provenance mismatch: caller supplied artifact ID {requestedArtifactId.Value}, " +
                $"but GitHub API verified artifact ID {dl.ArtifactId.Value} on run {dl.WorkflowRunId.Value}.");
        }

        var trustSource = string.IsNullOrWhiteSpace(dl.TrustSource)
            ? $"GitHubActionsRun:{dl.WorkflowRunId.Value}/Artifact:{dl.ArtifactId.Value}"
            : dl.TrustSource;

        return new VerifiedGitHubArtifact(
            Verified: true,
            LocalZipPath: dl.LocalZipPath,
            TrustedSha256: dl.TrustedApiDigestSha256,
            WorkflowRunId: dl.WorkflowRunId.Value,
            ArtifactId: dl.ArtifactId.Value,
            ArtifactName: dl.ArtifactName,
            TrustSource: trustSource,
            FailureReason: null);
    }

    private static VerifiedGitHubArtifact Fail(string reason)
        => new(false, null, null, null, null, null, null, reason);
}
