using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ServerForge.Core.Interfaces;
using ServerForge.Security.Execution;

namespace ServerForge.GitHub.Client;

/// <summary>
/// Typed GitHub REST API client supporting Fine-Grained PATs, conditional ETag polling,
/// repository search, commit inspection, and strictly provenance-verified GitHub Actions artifact downloads.
/// </summary>
public sealed partial class GitHubApiClient : IGitHubClient
{
    public const long DefaultMaxArtifactDownloadBytes = 512L * 1024 * 1024; // 512 MB compressed archive cap
    public const long DefaultMaxUncompressedArtifactBytes = 2L * 1024 * 1024 * 1024; // 2 GB zip-bomb guard
    private const int MaxRedirects = 2;
    private const int MaxDownloadAttempts = 3;

    private static readonly string[] CanonicalArtifactExactNames =
    [
        "serverforge-release",
        "serverforge-deploy",
        "app-release",
        "release-artifact"
    ];

    private readonly HttpClient _http;
    private readonly ILogger<GitHubApiClient> _logger;

    /// <summary>
    /// Configurable maximum compressed artifact download size (defaults to 512 MB).
    /// </summary>
    public long MaxArtifactDownloadBytes { get; set; } = DefaultMaxArtifactDownloadBytes;

    /// <summary>
    /// Configurable maximum uncompressed archive total size (defaults to 2 GB).
    /// </summary>
    public long MaxUncompressedArtifactBytes { get; set; } = DefaultMaxUncompressedArtifactBytes;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled)]
    private static partial Regex RepoFullNameRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{40}$", RegexOptions.Compiled)]
    private static partial Regex FullGitShaRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.Compiled)]
    private static partial Regex Sha256HexRegex();

    public GitHubApiClient(HttpClient httpClient, ILogger<GitHubApiClient> logger)
    {
        _http = httpClient;
        _logger = logger;
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("https://api.github.com/");
    }

    public async Task<GitHubTokenValidationResult> ValidateTokenAsync(string personalAccessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(personalAccessToken))
            return new GitHubTokenValidationResult(false, null, null, 0, DateTimeOffset.UtcNow, [], "Personal Access Token is empty.");

        using var req = CreateRequest(HttpMethod.Get, "user", personalAccessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);

        var remaining = ParseIntHeader(resp, "X-RateLimit-Remaining");
        var resetEpoch = ParseLongHeader(resp, "X-RateLimit-Reset");
        var resetUtc = resetEpoch > 0 ? DateTimeOffset.FromUnixTimeSeconds(resetEpoch) : DateTimeOffset.UtcNow.AddHours(1);

        if (!resp.IsSuccessStatusCode)
        {
            return new GitHubTokenValidationResult(
                false, null, null, remaining, resetUtc, [],
                $"GitHub rejected credentials (HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}). Verify token expiration and repository permissions.");
        }

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        var login = doc.TryGetProperty("login", out var l) ? l.GetString() : null;
        var name = doc.TryGetProperty("name", out var n) ? n.GetString() : login;

        var scopes = resp.Headers.TryGetValues("X-OAuth-Scopes", out var scopeVals)
            ? scopeVals.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList()
            : ["fine-grained-pat"];

        return new GitHubTokenValidationResult(true, login, name, remaining, resetUtc, scopes, null);
    }

    public async Task<IReadOnlyList<GitHubRepositoryInfo>> ListRepositoriesAsync(
        string personalAccessToken,
        string? searchQuery,
        CancellationToken ct)
    {
        using var req = CreateRequest(HttpMethod.Get, "user/repos?per_page=100&sort=updated&affiliation=owner,collaborator,organization_member", personalAccessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var items = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        var repos = new List<GitHubRepositoryInfo>();
        if (items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var fullName = item.GetProperty("full_name").GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(searchQuery) &&
                    !fullName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                repos.Add(new GitHubRepositoryInfo(
                    Id: item.GetProperty("id").GetInt64(),
                    FullName: fullName,
                    DefaultBranch: item.TryGetProperty("default_branch", out var b) ? b.GetString() ?? "main" : "main",
                    IsPrivate: item.TryGetProperty("private", out var p) && p.GetBoolean(),
                    HtmlUrl: item.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                    Description: item.TryGetProperty("description", out var d) ? d.GetString() : null,
                    UpdatedAtUtc: item.TryGetProperty("updated_at", out var u) ? u.GetDateTimeOffset() : DateTimeOffset.UtcNow));
            }
        }

        return repos;
    }

    public async Task<IReadOnlyList<GitHubCommitInfo>> GetRecentCommitsAsync(
        string personalAccessToken,
        string repositoryFullName,
        string branch,
        int count,
        CancellationToken ct)
    {
        if (!IsValidRepositoryFullName(repositoryFullName))
            throw new ArgumentException($"Invalid GitHub repository '{repositoryFullName}'.", nameof(repositoryFullName));

        var safeCount = Math.Clamp(count, 1, 50);
        using var req = CreateRequest(
            HttpMethod.Get,
            $"repos/{repositoryFullName}/commits?sha={Uri.EscapeDataString(branch)}&per_page={safeCount}",
            personalAccessToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var arr = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        var commits = new List<GitHubCommitInfo>();
        if (arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                var sha = item.GetProperty("sha").GetString() ?? "";
                var commitObj = item.GetProperty("commit");
                var msg = commitObj.GetProperty("message").GetString() ?? "";
                var authorObj = commitObj.GetProperty("author");
                var author = authorObj.GetProperty("name").GetString() ?? "unknown";
                var date = authorObj.GetProperty("date").GetDateTimeOffset();

                commits.Add(new GitHubCommitInfo(
                    Sha: sha,
                    ShortSha: sha.Length >= 7 ? sha[..7] : sha,
                    Message: msg.Split('\n')[0],
                    AuthorName: author,
                    CommittedAtUtc: date));
            }
        }

        return commits;
    }

    public async Task<GitHubHeadCheckResult> CheckBranchHeadConditionalAsync(
        string personalAccessToken,
        string repositoryFullName,
        string branch,
        string? lastETag,
        CancellationToken ct)
    {
        if (!IsValidRepositoryFullName(repositoryFullName))
            throw new ArgumentException($"Invalid GitHub repository '{repositoryFullName}'.", nameof(repositoryFullName));

        using var req = CreateRequest(
            HttpMethod.Get,
            $"repos/{repositoryFullName}/commits/{Uri.EscapeDataString(branch)}",
            personalAccessToken);

        if (!string.IsNullOrWhiteSpace(lastETag) && EntityTagHeaderValue.TryParse(lastETag, out var parsedTag))
            req.Headers.IfNoneMatch.Add(parsedTag);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var remaining = ParseIntHeader(resp, "X-RateLimit-Remaining");
        var resetEpoch = ParseLongHeader(resp, "X-RateLimit-Reset");
        DateTimeOffset? resetUtc = resetEpoch > 0 ? DateTimeOffset.FromUnixTimeSeconds(resetEpoch) : null;
        var retryAfterSec = ParseRetryAfterSeconds(resp);
        var newETag = resp.Headers.ETag?.ToString() ?? lastETag;

        if (resp.StatusCode == HttpStatusCode.NotModified)
            return new GitHubHeadCheckResult(false, null, null, null, newETag, remaining, resetUtc, retryAfterSec, IsRateLimited: false);

        if (IsRateLimitResponse(resp, remaining))
            return new GitHubHeadCheckResult(false, null, null, null, newETag, Math.Max(0, remaining), resetUtc, retryAfterSec, IsRateLimited: true);

        resp.EnsureSuccessStatusCode();
        var item = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        var sha = item.GetProperty("sha").GetString();
        var commitObj = item.GetProperty("commit");
        var message = commitObj.GetProperty("message").GetString()?.Split('\n')[0];
        var author = commitObj.GetProperty("author").GetProperty("name").GetString();

        return new GitHubHeadCheckResult(true, sha, message, author, newETag, remaining, resetUtc, retryAfterSec, IsRateLimited: false);
    }

    public Task<GitHubArtifactDownloadResult> DownloadVerifiedWorkflowArtifactAsync(
        string personalAccessToken,
        string repositoryFullName,
        string branch,
        string commitSha,
        string? expectedWorkflowFileName,
        string destinationZipPath,
        CancellationToken ct)
        => DownloadVerifiedWorkflowArtifactAsync(
            personalAccessToken,
            repositoryFullName,
            branch,
            commitSha,
            expectedWorkflowFileName,
            expectedArtifactName: null,
            destinationZipPath,
            ct);

    public async Task<GitHubArtifactDownloadResult> DownloadVerifiedWorkflowArtifactAsync(
        string personalAccessToken,
        string repositoryFullName,
        string branch,
        string commitSha,
        string? expectedWorkflowFileName,
        string? expectedArtifactName,
        string destinationZipPath,
        CancellationToken ct)
    {
        // Strict input validation — fail closed before issuing any API calls
        if (!IsValidRepositoryFullName(repositoryFullName))
        {
            return RejectProvenance($"Invalid authorized GitHub repository identifier '{repositoryFullName}'.");
        }

        if (string.IsNullOrWhiteSpace(branch))
        {
            return RejectProvenance("Target branch is required for GitHub Actions artifact provenance.");
        }

        if (!FullGitShaRegex().IsMatch(commitSha ?? string.Empty))
        {
            return RejectProvenance($"Exact 40-character commit SHA is required (received '{commitSha}').");
        }

        if (string.IsNullOrWhiteSpace(expectedWorkflowFileName))
        {
            return RejectProvenance("Expected GitHub Actions workflow filename is required; refusing unverified arbitrary workflows.");
        }

        if (string.IsNullOrWhiteSpace(destinationZipPath))
        {
            return RejectProvenance("Destination artifact path is required.");
        }

        // 1. Query ALL workflow runs for exact commit SHA + branch (not status=completed only)
        //    so we accurately distinguish WorkflowNotStarted, WorkflowRunning, WorkflowFailedOrCancelled, and Completed/Success.
        using var runsReq = CreateRequest(
            HttpMethod.Get,
            $"repos/{repositoryFullName}/actions/runs?head_sha={Uri.EscapeDataString(commitSha)}&branch={Uri.EscapeDataString(branch)}&per_page=30",
            personalAccessToken);

        using var runsResp = await _http.SendAsync(runsReq, ct).ConfigureAwait(false);
        var remaining = ParseIntHeader(runsResp, "X-RateLimit-Remaining");
        var retryAfter = ParseRetryAfterSeconds(runsResp);

        if (IsRateLimitResponse(runsResp, remaining))
        {
            return new GitHubArtifactDownloadResult(
                false, null, null, null, null, null, null, null,
                $"GitHub Actions API rate limited while querying workflow runs (HTTP {(int)runsResp.StatusCode}).",
                GitHubArtifactResolutionStatus.RateLimited,
                retryAfter,
                Math.Max(0, remaining));
        }

        if ((int)runsResp.StatusCode >= 500 || runsResp.StatusCode == HttpStatusCode.RequestTimeout)
        {
            return new GitHubArtifactDownloadResult(
                false, null, null, null, null, null, null, null,
                $"Transient GitHub Actions API failure querying workflow runs (HTTP {(int)runsResp.StatusCode}).",
                GitHubArtifactResolutionStatus.TransientApiError,
                retryAfter,
                remaining >= 0 ? remaining : null);
        }

        if (!runsResp.IsSuccessStatusCode)
        {
            return new GitHubArtifactDownloadResult(
                false, null, null, null, null, null, null, null,
                $"Unable to query GitHub Actions workflow runs (HTTP {(int)runsResp.StatusCode}).",
                GitHubArtifactResolutionStatus.ProvenanceRejected,
                null,
                remaining >= 0 ? remaining : null);
        }

        var runsJson = await runsResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        long? matchingRunId = null;
        var hasInProgressAuthorizedRun = false;
        string? failedAuthorizedConclusion = null;
        var workflowPathCache = new Dictionary<long, string?>();

        if (runsJson.TryGetProperty("workflow_runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            foreach (var run in runs.EnumerateArray())
            {
                var runSha = run.TryGetProperty("head_sha", out var s) ? s.GetString() : null;
                var runBranch = run.TryGetProperty("head_branch", out var b) ? b.GetString() : null;
                var runStatus = run.TryGetProperty("status", out var st) ? st.GetString() : null;
                var conclusion = run.TryGetProperty("conclusion", out var c) ? c.GetString() : null;
                var runPath = run.TryGetProperty("path", out var p) ? p.GetString() : null;

                if (!string.Equals(runSha, commitSha, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(runBranch, branch, StringComparison.Ordinal)) continue;

                // If GitHub includes repository metadata, strictly verify exact repository match
                if (run.TryGetProperty("repository", out var repoObj) &&
                    repoObj.ValueKind == JsonValueKind.Object &&
                    repoObj.TryGetProperty("full_name", out var repoNameProp))
                {
                    var runRepoFullName = repoNameProp.GetString();
                    if (!string.Equals(runRepoFullName, repositoryFullName, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // STRICT WORKFLOW IDENTITY: Never accept missing path metadata silently!
                // If `path` is absent in the list response, explicitly query the workflow resource by `workflow_id`.
                if (string.IsNullOrWhiteSpace(runPath))
                {
                    if (run.TryGetProperty("workflow_id", out var wfIdProp) && wfIdProp.TryGetInt64(out var workflowId) && workflowId > 0)
                    {
                        if (!workflowPathCache.TryGetValue(workflowId, out runPath))
                        {
                            runPath = await ResolveWorkflowPathAsync(personalAccessToken, repositoryFullName, workflowId, ct).ConfigureAwait(false);
                            workflowPathCache[workflowId] = runPath;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(runPath) || !IsExactWorkflowPathMatch(runPath, expectedWorkflowFileName))
                {
                    continue;
                }

                if (string.Equals(runStatus, "completed", StringComparison.OrdinalIgnoreCase) ||
                    (string.IsNullOrWhiteSpace(runStatus) && !string.IsNullOrWhiteSpace(conclusion)))
                {
                    if (string.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase))
                    {
                        matchingRunId = run.GetProperty("id").GetInt64();
                        break;
                    }

                    failedAuthorizedConclusion ??= conclusion ?? "failed";
                }
                else
                {
                    hasInProgressAuthorizedRun = true;
                }
            }
        }

        if (matchingRunId is null)
        {
            if (hasInProgressAuthorizedRun)
            {
                return new GitHubArtifactDownloadResult(
                    false, null, null, null, null, null, null, null,
                    $"Authorized GitHub Actions workflow '{expectedWorkflowFileName}' is still running for {repositoryFullName}@{branch} ({commitSha[..7]}).",
                    GitHubArtifactResolutionStatus.WorkflowRunning,
                    null,
                    remaining >= 0 ? remaining : null);
            }

            if (failedAuthorizedConclusion is not null)
            {
                return new GitHubArtifactDownloadResult(
                    false, null, null, null, null, null, null, null,
                    $"Authorized GitHub Actions workflow '{expectedWorkflowFileName}' ended with conclusion '{failedAuthorizedConclusion}' for {repositoryFullName}@{branch} ({commitSha[..7]}).",
                    GitHubArtifactResolutionStatus.WorkflowFailedOrCancelled,
                    null,
                    remaining >= 0 ? remaining : null);
            }

            return new GitHubArtifactDownloadResult(
                false, null, null, null, null, null, null, null,
                $"No completed successful GitHub Actions workflow run matching '{expectedWorkflowFileName}' verified for {repositoryFullName}@{branch} ({commitSha[..7]}).",
                GitHubArtifactResolutionStatus.WorkflowNotStarted,
                null,
                remaining >= 0 ? remaining : null);
        }

        // 2. Inspect workflow artifacts for exact run ownership, non-expired status, secure selection policy, size cap, and SHA-256 digest
        using var artReq = CreateRequest(
            HttpMethod.Get,
            $"repos/{repositoryFullName}/actions/runs/{matchingRunId}/artifacts",
            personalAccessToken);

        using var artResp = await _http.SendAsync(artReq, ct).ConfigureAwait(false);
        var artRemaining = ParseIntHeader(artResp, "X-RateLimit-Remaining");
        var artRetryAfter = ParseRetryAfterSeconds(artResp);

        if (IsRateLimitResponse(artResp, artRemaining))
        {
            return new GitHubArtifactDownloadResult(
                false, null, null, null, matchingRunId, null, null, null,
                $"GitHub Actions API rate limited while listing artifacts for run {matchingRunId}.",
                GitHubArtifactResolutionStatus.RateLimited,
                artRetryAfter,
                Math.Max(0, artRemaining));
        }

        if ((int)artResp.StatusCode >= 500 || artResp.StatusCode == HttpStatusCode.RequestTimeout)
        {
            return new GitHubArtifactDownloadResult(
                false, null, null, null, matchingRunId, null, null, null,
                $"Transient GitHub Actions API failure listing artifacts for run {matchingRunId} (HTTP {(int)artResp.StatusCode}).",
                GitHubArtifactResolutionStatus.TransientApiError,
                artRetryAfter,
                artRemaining >= 0 ? artRemaining : null);
        }

        if (!artResp.IsSuccessStatusCode)
        {
            return RejectProvenance($"Unable to list artifacts for workflow run {matchingRunId} (HTTP {(int)artResp.StatusCode}).", matchingRunId);
        }

        var artJson = await artResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        var candidates = new List<ArtifactCandidate>();

        if (artJson.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array)
        {
            foreach (var art in artifacts.EnumerateArray())
            {
                var expired = art.TryGetProperty("expired", out var ex) && ex.GetBoolean();
                var sizeBytes = art.TryGetProperty("size_in_bytes", out var sz) ? sz.GetInt64() : 0;
                if (expired || sizeBytes <= 0) continue;

                // Verify artifact's workflow_run.id matches matchingRunId when present
                if (art.TryGetProperty("workflow_run", out var wfRunObj) &&
                    wfRunObj.ValueKind == JsonValueKind.Object &&
                    wfRunObj.TryGetProperty("id", out var artRunIdProp) &&
                    artRunIdProp.TryGetInt64(out var artRunId) &&
                    artRunId != matchingRunId.Value)
                {
                    continue;
                }

                var id = art.GetProperty("id").GetInt64();
                var name = art.TryGetProperty("name", out var nm) ? (nm.GetString() ?? string.Empty) : string.Empty;
                var url = art.TryGetProperty("archive_download_url", out var dlUrlProp) ? dlUrlProp.GetString() : null;

                string? digest = null;
                if (art.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String)
                {
                    var rawDigest = dg.GetString() ?? "";
                    digest = rawDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? rawDigest[7..].Trim().ToLowerInvariant()
                        : rawDigest.Trim().ToLowerInvariant();
                }

                candidates.Add(new ArtifactCandidate(id, name, url, digest, sizeBytes));
            }
        }

        if (candidates.Count == 0)
        {
            return new GitHubArtifactDownloadResult(
                false, null, null, null, matchingRunId, null, null, null,
                $"Workflow run {matchingRunId} succeeded but no non-expired, non-empty artifact is available yet.",
                GitHubArtifactResolutionStatus.WorkflowSucceededArtifactPending,
                null,
                artRemaining >= 0 ? artRemaining : null);
        }

        var (selected, selectionError) = SelectAuthorizedArtifact(candidates, expectedArtifactName);
        if (selected is null)
        {
            return RejectProvenance(selectionError ?? "Unable to unambiguously select authorized deployment artifact.", matchingRunId);
        }

        if (selected.SizeBytes > MaxArtifactDownloadBytes)
        {
            return RejectProvenance(
                $"Artifact '{selected.Name}' (ID {selected.Id}) size ({selected.SizeBytes} bytes) exceeds maximum allowed limit ({MaxArtifactDownloadBytes} bytes).",
                matchingRunId, selected.Id, selected.Name, selected.TrustedDigest);
        }

        if (!IsTrustedGitHubArtifactUrl(selected.DownloadUrl))
        {
            return RejectProvenance(
                $"Refusing untrusted artifact download URL '{selected.DownloadUrl}'. Only HTTPS GitHub API artifact URLs are permitted.",
                matchingRunId, selected.Id, selected.Name, selected.TrustedDigest);
        }

        if (string.IsNullOrWhiteSpace(selected.TrustedDigest) || !Sha256HexRegex().IsMatch(selected.TrustedDigest))
        {
            return RejectProvenance(
                $"GitHub Actions artifact '{selected.Name}' (ID {selected.Id}) lacked a valid 64-character server-reported SHA-256 digest. Refusing untrusted artifact.",
                matchingRunId, selected.Id, selected.Name, selected.TrustedDigest);
        }

        // 3. Download archive safely with strict redirect host validation, PAT stripping on redirects,
        //    streaming size cap, incomplete-download detection, bounded retries, ZIP integrity check, and SHA-256 verification.
        var destDir = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrWhiteSpace(destDir))
            Directory.CreateDirectory(destDir);

        var tempZipPath = $"{destinationZipPath}.{Guid.NewGuid():N}.download.tmp";
        try
        {
            var downloadOutcome = await DownloadToTempFileWithRetriesAsync(
                selected.DownloadUrl!,
                personalAccessToken,
                tempZipPath,
                ct).ConfigureAwait(false);

            if (!downloadOutcome.Succeeded)
            {
                SafeDeleteFile(tempZipPath);
                return new GitHubArtifactDownloadResult(
                    false, null, null, selected.TrustedDigest, matchingRunId, selected.Id, selected.Name, null,
                    downloadOutcome.ErrorMessage ?? "Artifact download failed.",
                    downloadOutcome.FailureStatus,
                    downloadOutcome.RetryAfterSeconds,
                    artRemaining >= 0 ? artRemaining : null);
            }

            // Verify cryptographic SHA-256 integrity BEFORE passing archive bytes into the ZIP parser
            var computedSha256 = await SafeProcessRunner.ComputeFileSha256Async(tempZipPath, ct).ConfigureAwait(false);
            if (!computedSha256.Equals(selected.TrustedDigest, StringComparison.OrdinalIgnoreCase))
            {
                SafeDeleteFile(tempZipPath);
                return new GitHubArtifactDownloadResult(
                    false, null, computedSha256, selected.TrustedDigest, matchingRunId, selected.Id, selected.Name, null,
                    $"Artifact SHA-256 integrity mismatch! GitHub API digest={selected.TrustedDigest}, downloaded file={computedSha256}.",
                    GitHubArtifactResolutionStatus.ProvenanceRejected);
            }

            // Validate archive integrity (valid ZIP, >= 1 file entry, no Zip Slip entries, uncompressed cap)
            var (zipValid, zipError) = ValidateDownloadedZipArchive(tempZipPath, MaxUncompressedArtifactBytes);
            if (!zipValid)
            {
                SafeDeleteFile(tempZipPath);
                return RejectProvenance(
                    $"Downloaded artifact '{selected.Name}' failed archive integrity validation: {zipError}",
                    matchingRunId, selected.Id, selected.Name, selected.TrustedDigest);
            }

            File.Move(tempZipPath, destinationZipPath, overwrite: true);

            _logger.LogInformation(
                "Verified GitHub Actions artifact '{Name}' (ID {ArtId}, Run {RunId}) for {Repo}@{Sha}: SHA-256 matched GitHub API digest {Hash}",
                selected.Name, selected.Id, matchingRunId, repositoryFullName, commitSha[..7], computedSha256[..12]);

            return new GitHubArtifactDownloadResult(
                Found: true,
                LocalZipPath: destinationZipPath,
                ComputedSha256: computedSha256,
                TrustedApiDigestSha256: selected.TrustedDigest,
                WorkflowRunId: matchingRunId,
                ArtifactId: selected.Id,
                ArtifactName: selected.Name,
                TrustSource: $"GitHubActionsRun:{matchingRunId}/Artifact:{selected.Id}",
                ErrorMessage: null,
                ResolutionStatus: GitHubArtifactResolutionStatus.ArtifactVerified,
                RetryAfterSeconds: null,
                RateLimitRemaining: artRemaining >= 0 ? artRemaining : null);
        }
        catch
        {
            SafeDeleteFile(tempZipPath);
            throw;
        }
    }

    private async Task<string?> ResolveWorkflowPathAsync(
        string personalAccessToken,
        string repositoryFullName,
        long workflowId,
        CancellationToken ct)
    {
        try
        {
            using var wfReq = CreateRequest(HttpMethod.Get, $"repos/{repositoryFullName}/actions/workflows/{workflowId}", personalAccessToken);
            using var wfResp = await _http.SendAsync(wfReq, ct).ConfigureAwait(false);
            if (!wfResp.IsSuccessStatusCode)
                return null;

            var wfJson = await wfResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
            return wfJson.TryGetProperty("path", out var pathProp) ? pathProp.GetString() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<DownloadAttemptOutcome> DownloadToTempFileWithRetriesAsync(
        string initialDownloadUrl,
        string personalAccessToken,
        string tempZipPath,
        CancellationToken ct)
    {
        DownloadAttemptOutcome? lastOutcome = null;

        for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            SafeDeleteFile(tempZipPath);
            try
            {
                var outcome = await ExecuteSingleValidatedDownloadAsync(initialDownloadUrl, personalAccessToken, tempZipPath, ct).ConfigureAwait(false);
                if (outcome.Succeeded)
                    return outcome;

                lastOutcome = outcome;
                if (outcome.FailureStatus != GitHubArtifactResolutionStatus.TransientApiError || attempt == MaxDownloadAttempts)
                    return outcome;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                lastOutcome = new DownloadAttemptOutcome(
                    false,
                    GitHubArtifactResolutionStatus.TransientApiError,
                    $"Transient download failure on attempt {attempt}/{MaxDownloadAttempts}: {ex.Message}",
                    null);
                if (attempt == MaxDownloadAttempts)
                    return lastOutcome;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
        }

        return lastOutcome ?? new DownloadAttemptOutcome(false, GitHubArtifactResolutionStatus.TransientApiError, "Artifact download failed.", null);
    }

    private async Task<DownloadAttemptOutcome> ExecuteSingleValidatedDownloadAsync(
        string initialDownloadUrl,
        string personalAccessToken,
        string tempZipPath,
        CancellationToken ct)
    {
        var currentUrl = initialDownloadUrl;
        var attachAuthHeader = true;

        for (var redirectHop = 0; redirectHop <= MaxRedirects; redirectHop++)
        {
            using var dlReq = attachAuthHeader
                ? CreateRequest(HttpMethod.Get, currentUrl, personalAccessToken)
                : CreateUnauthenticatedDownloadRequest(currentUrl);

            using var dlResp = await _http.SendAsync(dlReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // Handle explicit redirects safely: validate target host & NEVER forward GitHub PAT to redirected blob/CDN URLs
            if (IsRedirectStatusCode(dlResp.StatusCode))
            {
                if (redirectHop == MaxRedirects)
                {
                    return new DownloadAttemptOutcome(
                        false,
                        GitHubArtifactResolutionStatus.ProvenanceRejected,
                        $"Artifact download exceeded maximum allowed redirect hops ({MaxRedirects}).",
                        null);
                }

                var location = dlResp.Headers.Location;
                if (location is null)
                {
                    return new DownloadAttemptOutcome(
                        false,
                        GitHubArtifactResolutionStatus.ProvenanceRejected,
                        "Artifact download redirect did not include a Location header.",
                        null);
                }

                var resolvedUri = location.IsAbsoluteUri
                    ? location
                    : new Uri(new Uri(currentUrl, UriKind.Absolute), location);

                if (!IsTrustedGitHubRedirectHost(resolvedUri))
                {
                    return new DownloadAttemptOutcome(
                        false,
                        GitHubArtifactResolutionStatus.ProvenanceRejected,
                        $"Refusing untrusted artifact download redirect target '{resolvedUri.Scheme}://{resolvedUri.Host}'.",
                        null);
                }

                currentUrl = resolvedUri.AbsoluteUri;
                // Only send PAT to api.github.com / github.com; never forward PAT to storage/CDN redirect hosts
                attachAuthHeader = IsTrustedGitHubArtifactUrl(currentUrl);
                continue;
            }

            var remaining = ParseIntHeader(dlResp, "X-RateLimit-Remaining");
            var retryAfter = ParseRetryAfterSeconds(dlResp);
            if (IsRateLimitResponse(dlResp, remaining))
            {
                return new DownloadAttemptOutcome(
                    false,
                    GitHubArtifactResolutionStatus.RateLimited,
                    $"GitHub rate limited artifact download (HTTP {(int)dlResp.StatusCode}).",
                    retryAfter);
            }

            if ((int)dlResp.StatusCode >= 500 || dlResp.StatusCode == HttpStatusCode.RequestTimeout)
            {
                return new DownloadAttemptOutcome(
                    false,
                    GitHubArtifactResolutionStatus.TransientApiError,
                    $"Transient HTTP {(int)dlResp.StatusCode} downloading artifact.",
                    retryAfter);
            }

            if (!dlResp.IsSuccessStatusCode)
            {
                return new DownloadAttemptOutcome(
                    false,
                    GitHubArtifactResolutionStatus.ProvenanceRejected,
                    $"Artifact download rejected with HTTP {(int)dlResp.StatusCode}.",
                    null);
            }

            var expectedLength = dlResp.Content.Headers.ContentLength;
            if (expectedLength.HasValue && expectedLength.Value > MaxArtifactDownloadBytes)
            {
                return new DownloadAttemptOutcome(
                    false,
                    GitHubArtifactResolutionStatus.ProvenanceRejected,
                    $"Artifact Content-Length ({expectedLength.Value} bytes) exceeds maximum limit ({MaxArtifactDownloadBytes} bytes).",
                    null);
            }

            long totalRead = 0;
            await using (var sourceStream = await dlResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fs = File.Create(tempZipPath))
            {
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await sourceStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    totalRead += bytesRead;
                    if (totalRead > MaxArtifactDownloadBytes)
                    {
                        return new DownloadAttemptOutcome(
                            false,
                            GitHubArtifactResolutionStatus.ProvenanceRejected,
                            $"Streamed artifact size exceeded maximum limit ({MaxArtifactDownloadBytes} bytes).",
                            null);
                    }

                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                }
            }

            if (totalRead == 0)
            {
                return new DownloadAttemptOutcome(
                    false,
                    GitHubArtifactResolutionStatus.TransientApiError,
                    "Downloaded artifact archive was empty (0 bytes).",
                    null);
            }

            if (expectedLength.HasValue && totalRead != expectedLength.Value)
            {
                return new DownloadAttemptOutcome(
                    false,
                    GitHubArtifactResolutionStatus.TransientApiError,
                    $"Incomplete artifact download: expected {expectedLength.Value} bytes from Content-Length, received {totalRead} bytes.",
                    null);
            }

            return new DownloadAttemptOutcome(true, GitHubArtifactResolutionStatus.ArtifactVerified, null, null);
        }

        return new DownloadAttemptOutcome(false, GitHubArtifactResolutionStatus.ProvenanceRejected, "Artifact download failed redirect validation.", null);
    }

    private static (ArtifactCandidate? Selected, string? ErrorMessage) SelectAuthorizedArtifact(
        List<ArtifactCandidate> candidates,
        string? expectedArtifactName)
    {
        if (!string.IsNullOrWhiteSpace(expectedArtifactName))
        {
            var exactMatches = candidates
                .Where(c => string.Equals(c.Name, expectedArtifactName.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exactMatches.Count == 1)
                return (exactMatches[0], null);

            if (exactMatches.Count == 0)
                return (null, $"Expected artifact '{expectedArtifactName}' was not produced by the authorized workflow run.");

            return (null, $"Ambiguous workflow run: {exactMatches.Count} artifacts matched expected name '{expectedArtifactName}'.");
        }

        // Single valid non-expired artifact on an authorized workflow run is unambiguous
        if (candidates.Count == 1)
            return (candidates[0], null);

        // Multiple artifacts present: apply strict canonical deployment artifact naming policy (never blindly pick first!)
        var canonical = candidates
            .Where(c =>
                CanonicalArtifactExactNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase) ||
                c.Name.StartsWith("serverforge-", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (canonical.Count == 1)
            return (canonical[0], null);

        var names = string.Join(", ", candidates.Select(c => $"'{c.Name}'"));
        return (null,
            $"Ambiguous multi-artifact workflow run ({candidates.Count} artifacts: {names}). " +
            "Specify an expected artifact name or publish a single canonical 'serverforge-*' deployment artifact.");
    }

    public static (bool IsValid, string? ErrorMessage) ValidateDownloadedZipArchive(string zipPath, long maxUncompressedBytes)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var fileEntries = 0;
            long totalUncompressed = 0;

            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/');
                if (normalized.StartsWith('/') ||
                    normalized.Contains("../", StringComparison.Ordinal) ||
                    normalized.Equals("..", StringComparison.Ordinal) ||
                    normalized.EndsWith("/..", StringComparison.Ordinal) ||
                    (normalized.Length >= 2 && normalized[1] == ':'))
                {
                    return (false, $"Unsafe archive entry path detected ('{entry.FullName}').");
                }

                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                fileEntries++;
                totalUncompressed += entry.Length;
                if (totalUncompressed > maxUncompressedBytes)
                    return (false, $"Uncompressed archive size exceeds safety limit ({maxUncompressedBytes} bytes).");
            }

            if (fileEntries == 0)
                return (false, "Artifact ZIP archive contains zero files.");

            return (true, null);
        }
        catch (InvalidDataException ex)
        {
            return (false, $"File is not a valid ZIP archive: {ex.Message}");
        }
    }

    public static bool IsValidRepositoryFullName(string? repo)
        => !string.IsNullOrWhiteSpace(repo) &&
           !repo.Contains("..", StringComparison.Ordinal) &&
           RepoFullNameRegex().IsMatch(repo.Trim());

    public static bool IsExactWorkflowPathMatch(string? actualWorkflowPath, string expectedWorkflowFileName)
    {
        if (string.IsNullOrWhiteSpace(actualWorkflowPath) || string.IsNullOrWhiteSpace(expectedWorkflowFileName))
            return false;

        var actual = actualWorkflowPath.Trim().Replace('\\', '/').TrimStart('/');
        var expected = expectedWorkflowFileName.Trim().Replace('\\', '/').TrimStart('/');

        if (expected.Contains('/', StringComparison.Ordinal))
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

        return string.Equals(actual, $".github/workflows/{expected}", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTrustedGitHubArtifactUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        return uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTrustedGitHubRedirectHost(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (IPAddress.TryParse(uri.Host, out _))
            return false;

        var host = uri.Host.ToLowerInvariant();
        return host == "api.github.com" ||
               host == "github.com" ||
               host.EndsWith(".githubusercontent.com", StringComparison.Ordinal) ||
               host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal);
    }

    private static GitHubArtifactDownloadResult RejectProvenance(
        string reason,
        long? runId = null,
        long? artifactId = null,
        string? artifactName = null,
        string? trustedDigest = null)
        => new(false, null, null, trustedDigest, runId, artifactId, artifactName, null, reason, GitHubArtifactResolutionStatus.ProvenanceRejected);

    private static HttpRequestMessage CreateRequest(HttpMethod method, string relativeOrAbsoluteUri, string pat)
    {
        var req = new HttpRequestMessage(method, relativeOrAbsoluteUri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("ServerForge", "1.0"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    private static HttpRequestMessage CreateUnauthenticatedDownloadRequest(string absoluteUri)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, absoluteUri);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("ServerForge", "1.0"));
        return req;
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsRateLimitResponse(HttpResponseMessage resp, int rateLimitRemaining)
        => resp.StatusCode == (HttpStatusCode)429 ||
           (resp.StatusCode == HttpStatusCode.Forbidden && rateLimitRemaining == 0);

    private static int ParseIntHeader(HttpResponseMessage resp, string name)
        => resp.Headers.TryGetValues(name, out var vals) && int.TryParse(vals.FirstOrDefault(), out var v) ? v : -1;

    private static long ParseLongHeader(HttpResponseMessage resp, string name)
        => resp.Headers.TryGetValues(name, out var vals) && long.TryParse(vals.FirstOrDefault(), out var v) ? v : 0;

    private static int? ParseRetryAfterSeconds(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is { } delta)
            return Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds));
        if (resp.Headers.RetryAfter?.Date is { } date)
            return Math.Max(1, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
        return null;
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of temp download file
        }
    }

    private sealed record ArtifactCandidate(
        long Id,
        string Name,
        string? DownloadUrl,
        string? TrustedDigest,
        long SizeBytes);

    private sealed record DownloadAttemptOutcome(
        bool Succeeded,
        GitHubArtifactResolutionStatus FailureStatus,
        string? ErrorMessage,
        int? RetryAfterSeconds);
}
