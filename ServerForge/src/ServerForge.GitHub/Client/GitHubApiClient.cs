using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerForge.Core.Interfaces;
using ServerForge.Security.Execution;

namespace ServerForge.GitHub.Client;

/// <summary>
/// Typed GitHub REST API client supporting Fine-Grained PATs, conditional ETag polling,
/// repository search, commit inspection, and provenance-verified GitHub Actions artifact downloads.
/// </summary>
public sealed class GitHubApiClient : IGitHubClient
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubApiClient> _logger;

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
        using var req = CreateRequest(
            HttpMethod.Get,
            $"repos/{repositoryFullName}/commits/{Uri.EscapeDataString(branch)}",
            personalAccessToken);

        if (!string.IsNullOrWhiteSpace(lastETag))
            req.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(lastETag));

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var remaining = ParseIntHeader(resp, "X-RateLimit-Remaining");
        var newETag = resp.Headers.ETag?.ToString() ?? lastETag;

        if (resp.StatusCode == HttpStatusCode.NotModified)
            return new GitHubHeadCheckResult(false, null, null, null, newETag, remaining);

        resp.EnsureSuccessStatusCode();
        var item = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        var sha = item.GetProperty("sha").GetString();
        var commitObj = item.GetProperty("commit");
        var message = commitObj.GetProperty("message").GetString()?.Split('\n')[0];
        var author = commitObj.GetProperty("author").GetProperty("name").GetString();

        return new GitHubHeadCheckResult(true, sha, message, author, newETag, remaining);
    }

    public async Task<GitHubArtifactDownloadResult> DownloadVerifiedWorkflowArtifactAsync(
        string personalAccessToken,
        string repositoryFullName,
        string branch,
        string commitSha,
        string? expectedWorkflowFileName,
        string destinationZipPath,
        CancellationToken ct)
    {
        // 1. Locate completed successful workflow run matching exact commit SHA, branch, and workflow file
        using var runsReq = CreateRequest(
            HttpMethod.Get,
            $"repos/{repositoryFullName}/actions/runs?head_sha={Uri.EscapeDataString(commitSha)}&branch={Uri.EscapeDataString(branch)}&status=completed&per_page=20",
            personalAccessToken);

        using var runsResp = await _http.SendAsync(runsReq, ct).ConfigureAwait(false);
        if (!runsResp.IsSuccessStatusCode)
        {
            return new GitHubArtifactDownloadResult(false, null, null, null, null, null, null, null,
                $"Unable to query GitHub Actions workflow runs (HTTP {(int)runsResp.StatusCode}).");
        }

        var runsJson = await runsResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        long? matchingRunId = null;
        if (runsJson.TryGetProperty("workflow_runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            foreach (var run in runs.EnumerateArray())
            {
                var conclusion = run.TryGetProperty("conclusion", out var c) ? c.GetString() : null;
                var runSha = run.TryGetProperty("head_sha", out var s) ? s.GetString() : null;
                var runBranch = run.TryGetProperty("head_branch", out var b) ? b.GetString() : null;
                var runPath = run.TryGetProperty("path", out var p) ? p.GetString() : null;

                if (!string.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(runSha, commitSha, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(runBranch, branch, StringComparison.Ordinal)) continue;
                if (!string.IsNullOrWhiteSpace(expectedWorkflowFileName) &&
                    !string.IsNullOrWhiteSpace(runPath) &&
                    !runPath.EndsWith(expectedWorkflowFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matchingRunId = run.GetProperty("id").GetInt64();
                break;
            }
        }

        if (matchingRunId is null)
        {
            return new GitHubArtifactDownloadResult(false, null, null, null, null, null, null, null,
                $"No completed successful GitHub Actions workflow run verified for {repositoryFullName}@{branch} ({commitSha[..Math.Min(7, commitSha.Length)]}).");
        }

        // 2. Inspect workflow artifacts for identity, non-empty size, and GitHub-reported SHA-256 digest
        using var artReq = CreateRequest(
            HttpMethod.Get,
            $"repos/{repositoryFullName}/actions/runs/{matchingRunId}/artifacts",
            personalAccessToken);

        using var artResp = await _http.SendAsync(artReq, ct).ConfigureAwait(false);
        artResp.EnsureSuccessStatusCode();
        var artJson = await artResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);

        string? downloadUrl = null;
        long? artifactId = null;
        string? artifactName = null;
        string? trustedDigest = null;

        if (artJson.TryGetProperty("artifacts", out var artifacts) && artifacts.ValueKind == JsonValueKind.Array)
        {
            foreach (var art in artifacts.EnumerateArray())
            {
                var expired = art.TryGetProperty("expired", out var ex) && ex.GetBoolean();
                var sizeBytes = art.TryGetProperty("size_in_bytes", out var sz) ? sz.GetInt64() : 0;
                if (expired || sizeBytes <= 0) continue;

                artifactId = art.GetProperty("id").GetInt64();
                artifactName = art.TryGetProperty("name", out var nm) ? nm.GetString() : $"artifact-{artifactId}";
                downloadUrl = art.GetProperty("archive_download_url").GetString();

                // GitHub Actions Artifacts REST API exposes `digest: "sha256:<hex>"`
                if (art.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String)
                {
                    var rawDigest = dg.GetString() ?? "";
                    trustedDigest = rawDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? rawDigest[7..].Trim().ToLowerInvariant()
                        : rawDigest.Trim().ToLowerInvariant();
                }
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl) || artifactId is null)
        {
            return new GitHubArtifactDownloadResult(false, null, null, null, matchingRunId, null, null, null,
                $"Workflow run {matchingRunId} succeeded but produced no non-expired, non-empty deployment artifact.");
        }

        // 3. Download archive and verify computed SHA-256 against GitHub API's trusted digest
        Directory.CreateDirectory(Path.GetDirectoryName(destinationZipPath)!);
        using var dlReq = CreateRequest(HttpMethod.Get, downloadUrl, personalAccessToken);
        using var dlResp = await _http.SendAsync(dlReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        dlResp.EnsureSuccessStatusCode();

        await using (var fs = File.Create(destinationZipPath))
        {
            await dlResp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
        }

        var fileInfo = new FileInfo(destinationZipPath);
        if (!fileInfo.Exists || fileInfo.Length == 0)
        {
            return new GitHubArtifactDownloadResult(false, null, null, trustedDigest, matchingRunId, artifactId, artifactName, null,
                "Downloaded artifact archive was empty (0 bytes).");
        }

        var computedSha256 = await SafeProcessRunner.ComputeFileSha256Async(destinationZipPath, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(trustedDigest))
        {
            // Refuse unsigned/unverified artifact without trusted provenance digest
            File.Delete(destinationZipPath);
            return new GitHubArtifactDownloadResult(false, null, computedSha256, null, matchingRunId, artifactId, artifactName, null,
                $"GitHub Actions artifact '{artifactName}' (ID {artifactId}) lacked a server-reported SHA-256 digest. Refusing untrusted artifact.");
        }

        if (!computedSha256.Equals(trustedDigest, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destinationZipPath);
            return new GitHubArtifactDownloadResult(false, null, computedSha256, trustedDigest, matchingRunId, artifactId, artifactName, null,
                $"Artifact SHA-256 integrity mismatch! GitHub API digest={trustedDigest}, downloaded file={computedSha256}.");
        }

        _logger.LogInformation(
            "Verified GitHub Actions artifact '{Name}' (ID {ArtId}, Run {RunId}) for {Repo}@{Sha}: SHA-256 matched GitHub API digest {Hash}",
            artifactName, artifactId, matchingRunId, repositoryFullName, commitSha[..Math.Min(7, commitSha.Length)], computedSha256[..12]);

        return new GitHubArtifactDownloadResult(
            Found: true,
            LocalZipPath: destinationZipPath,
            ComputedSha256: computedSha256,
            TrustedApiDigestSha256: trustedDigest,
            WorkflowRunId: matchingRunId,
            ArtifactId: artifactId,
            ArtifactName: artifactName,
            TrustSource: $"GitHubActionsRun:{matchingRunId}/Artifact:{artifactId}",
            ErrorMessage: null);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string relativeOrAbsoluteUri, string pat)
    {
        var req = new HttpRequestMessage(method, relativeOrAbsoluteUri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("ServerForge", "1.0"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    private static int ParseIntHeader(HttpResponseMessage resp, string name)
        => resp.Headers.TryGetValues(name, out var vals) && int.TryParse(vals.FirstOrDefault(), out var v) ? v : -1;

    private static long ParseLongHeader(HttpResponseMessage resp, string name)
        => resp.Headers.TryGetValues(name, out var vals) && long.TryParse(vals.FirstOrDefault(), out var v) ? v : 0;
}
