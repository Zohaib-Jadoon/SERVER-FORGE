using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ServerForge.Agent.Configuration;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.Infrastructure.Persistence;

namespace ServerForge.Agent.Workers;

/// <summary>
/// Autonomous background Windows Service worker that polls authorized GitHub repositories using
/// conditional ETag requests while strictly decoupling branch-head tracking from durable per-application
/// workflow/artifact readiness state in SQLite.
/// <para>
/// Key guarantees (Phase 12.2):
/// 1. An unresolved commit continues polling for workflow &amp; artifact readiness even when subsequent
///    branch-head requests return HTTP 304 Not Modified.
/// 2. Pending candidates survive Agent restarts via SQLite (<c>AutonomousDeploymentCandidates</c>).
/// 3. A newer commit safely supersedes a pending older commit.
/// 4. Successful commits, in-flight deployments, and terminally failed deployments are never duplicated
///    or retried endlessly.
/// 5. Transient artifact-pending, API errors, and GitHub rate limits use bounded backoff and respect
///    <c>Retry-After</c> / <c>X-RateLimit-Reset</c>.
/// 6. Server-side artifact provenance verification remains mandatory.
/// </para>
/// </summary>
public sealed class GitHubAutoDeployWorker : BackgroundService
{
    public const int MaxArtifactWaitAttempts = 40;
    public const int MaxConsecutiveTransientErrors = 12;
    public static readonly TimeSpan MaxCandidatePendingAge = TimeSpan.FromHours(2);

    private readonly ServerForgeStore _store;
    private readonly IGitHubClient _github;
    private readonly IDeploymentEngine _deploymentEngine;
    private readonly ISecretProtector _secretProtector;
    private readonly AgentIdentityStore _identityStore;
    private readonly AgentOptions _options;
    private readonly ILogger<GitHubAutoDeployWorker> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, string> _etagCache = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _appPollLocks = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _rateLimitUntilUtc = new();

    public GitHubAutoDeployWorker(
        ServerForgeStore store,
        IGitHubClient github,
        IDeploymentEngine deploymentEngine,
        ISecretProtector secretProtector,
        AgentIdentityStore identityStore,
        IOptions<AgentOptions> options,
        ILogger<GitHubAutoDeployWorker> logger,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _github = github;
        _deploymentEngine = deploymentEngine;
        _secretProtector = secretProtector;
        _identityStore = identityStore;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ServerForge autonomous GitHub Auto-Deploy worker started (Poll interval: {Interval}s).",
            _options.GitHubPollIntervalSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(_options.GitHubPollIntervalSeconds, 15, 600)));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PollAuthorizedApplicationsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during autonomous GitHub poll cycle.");
            }
        }
    }

    public async Task PollAuthorizedApplicationsAsync(CancellationToken ct)
    {
        var protectedPat = _identityStore.State.ProtectedGitHubPat;
        if (string.IsNullOrWhiteSpace(protectedPat))
            return;

        string pat;
        try
        {
            pat = _secretProtector.Unprotect(protectedPat, "agent:github-pat");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to unprotect Agent GitHub PAT; skipping poll cycle.");
            return;
        }

        var apps = await _store.GetApplicationsAsync(ct).ConfigureAwait(false);
        var eligible = apps.Where(a =>
            a.AutoDeployEnabled &&
            a.RepositoryExplicitlyAuthorized &&
            !string.IsNullOrWhiteSpace(a.RepositoryFullName)).ToList();

        foreach (var app in eligible)
        {
            var appLock = _appPollLocks.GetOrAdd(app.Id, _ => new SemaphoreSlim(1, 1));
            if (!await appLock.WaitAsync(0, ct).ConfigureAwait(false))
            {
                _logger.LogDebug("Skipping overlapping autonomous poll for {App}; a poll or deployment is already in flight.", app.Slug);
                continue;
            }

            try
            {
                await PollSingleApplicationAsync(app, pat, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var safeError = _secretProtector.RedactSecrets(ex.Message, [pat]);
                _logger.LogWarning("Autonomous poll failed safely for {App} ({Repo}:{Branch}): {Reason}; continuing remaining applications.",
                    app.Slug, app.RepositoryFullName, app.Branch, safeError);
            }
            finally
            {
                appLock.Release();
            }
        }
    }

    private async Task PollSingleApplicationAsync(ManagedApplication app, string pat, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();

        // Respect active per-application GitHub rate-limit window
        if (_rateLimitUntilUtc.TryGetValue(app.Id, out var rateLimitUntil) && now < rateLimitUntil)
        {
            _logger.LogInformation("Deferring GitHub poll for {App} until {UntilUtc:O} due to GitHub rate limit.",
                app.Slug, rateLimitUntil);
            return;
        }

        // Step 1: Load durable per-application candidate state (survives Agent restarts)
        var candidate = await _store.GetAutonomousCandidateAsync(app.Id, ct).ConfigureAwait(false);
        if (!_etagCache.TryGetValue(app.Id, out var lastETag) && !string.IsNullOrWhiteSpace(candidate?.LastBranchHeadETag))
        {
            lastETag = candidate.LastBranchHeadETag;
            _etagCache[app.Id] = lastETag;
        }

        // Step 2: Check branch HEAD conditionally (decoupled from artifact readiness!)
        var head = await _github.CheckBranchHeadConditionalAsync(pat, app.RepositoryFullName, app.Branch, lastETag, ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(head.ETag))
            _etagCache[app.Id] = head.ETag;

        if (head.IsRateLimited || head.RateLimitRemaining == 0)
        {
            var delaySeconds = ResolveRateLimitBackoffSeconds(head.RetryAfterSeconds, head.RateLimitResetUtc, now);
            var until = now.AddSeconds(delaySeconds);
            _rateLimitUntilUtc[app.Id] = until;

            if (candidate is not null && IsPendingState(candidate.State))
            {
                candidate.LastBranchHeadETag = head.ETag ?? candidate.LastBranchHeadETag;
                candidate.LastCheckedAtUtc = now;
                candidate.NextEligibleCheckUtc = until;
                candidate.LastStatusMessage = $"GitHub API rate limit reached while checking branch HEAD; retrying after {delaySeconds}s.";
                await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
            }

            _logger.LogWarning("GitHub rate limit reached for {App} ({Repo}:{Branch}); pausing polls for {Delay}s.",
                app.Slug, app.RepositoryFullName, app.Branch, delaySeconds);
            return;
        }

        // Step 3: Update or supersede durable candidate when a new commit SHA is discovered
        if (head.Modified && !string.IsNullOrWhiteSpace(head.LatestCommitSha))
        {
            var isNewCandidate = candidate is null ||
                                 !string.Equals(candidate.CommitSha, head.LatestCommitSha, StringComparison.OrdinalIgnoreCase) ||
                                 !string.Equals(candidate.Branch, app.Branch, StringComparison.Ordinal);

            if (isNewCandidate)
            {
                var previousSha = candidate?.CommitSha;
                candidate = new AutonomousDeploymentCandidate
                {
                    ApplicationId = app.Id,
                    CommitSha = head.LatestCommitSha,
                    Branch = app.Branch,
                    CommitMessage = head.CommitMessage,
                    CommitAuthor = head.CommitAuthor,
                    LastBranchHeadETag = head.ETag ?? lastETag,
                    State = AutonomousCandidateState.CommitDetected,
                    AttemptCount = 0,
                    ConsecutiveTransientErrors = 0,
                    DetectedAtUtc = now,
                    LastCheckedAtUtc = now,
                    NextEligibleCheckUtc = null,
                    LastStatusMessage = !string.IsNullOrWhiteSpace(previousSha)
                        ? $"New commit {ShortSha(head.LatestCommitSha)} superseded previous candidate {ShortSha(previousSha)}."
                        : $"Detected new commit {ShortSha(head.LatestCommitSha)} on {app.RepositoryFullName}:{app.Branch}."
                };

                await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
                _logger.LogInformation("Detected candidate commit {Sha} on {Repo}:{Branch} for {App}.",
                    ShortSha(candidate.CommitSha), app.RepositoryFullName, app.Branch, app.Slug);
            }
            else if (!string.IsNullOrWhiteSpace(head.ETag) && candidate is not null &&
                     !string.Equals(candidate.LastBranchHeadETag, head.ETag, StringComparison.Ordinal))
            {
                candidate.LastBranchHeadETag = head.ETag;
                await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
            }
        }
        else if (candidate is not null && !string.IsNullOrWhiteSpace(head.ETag) &&
                 !string.Equals(candidate.LastBranchHeadETag, head.ETag, StringComparison.Ordinal))
        {
            candidate.LastBranchHeadETag = head.ETag;
            await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
        }

        // Step 4: Even when branch HEAD returned HTTP 304 Not Modified, evaluate any unresolved durable candidate!
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.CommitSha))
            return;

        if (!string.Equals(candidate.Branch, app.Branch, StringComparison.Ordinal))
            return;

        if (IsTerminalState(candidate.State))
            return;

        if (candidate.State == AutonomousCandidateState.DeploymentRunning)
        {
            _logger.LogDebug("Autonomous deployment already running for {App}@{Sha}; skipping duplicate launch.",
                app.Slug, ShortSha(candidate.CommitSha));
            return;
        }

        // Guard: Never redeploy an already deployed commit
        if (string.Equals(app.LastDeployedCommitSha, candidate.CommitSha, StringComparison.OrdinalIgnoreCase))
        {
            candidate.State = AutonomousCandidateState.DeploymentSucceeded;
            candidate.LastCheckedAtUtc = now;
            candidate.NextEligibleCheckUtc = null;
            candidate.LastStatusMessage = $"Commit {ShortSha(candidate.CommitSha)} is already the active deployed release.";
            await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
            return;
        }

        // Guard: Persistent duplicate-commit & terminal failure anti-retry-loop check across restarts
        if (await _store.HasAttemptedAutonomousDeploymentForCommitAsync(app.Id, candidate.CommitSha, ct).ConfigureAwait(false))
        {
            candidate.State = AutonomousCandidateState.DeploymentFailedTerminal;
            candidate.LastCheckedAtUtc = now;
            candidate.NextEligibleCheckUtc = null;
            candidate.LastStatusMessage = $"Commit {ShortSha(candidate.CommitSha)} already has an active, succeeded, or terminally failed autonomous deployment; skipping retry.";
            await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
            return;
        }

        // Guard: Respect bounded backoff window on unresolved candidates
        if (candidate.NextEligibleCheckUtc.HasValue && now < candidate.NextEligibleCheckUtc.Value)
        {
            _logger.LogDebug("Backoff active for {App}@{Sha} ({State}) until {NextUtc:O}.",
                app.Slug, ShortSha(candidate.CommitSha), candidate.State, candidate.NextEligibleCheckUtc.Value);
            return;
        }

        // Guard: Bounded lifetime & attempt caps so an abandoned commit never polls forever
        if (candidate.AttemptCount >= MaxArtifactWaitAttempts ||
            candidate.ConsecutiveTransientErrors >= MaxConsecutiveTransientErrors ||
            (now - candidate.DetectedAtUtc) > MaxCandidatePendingAge)
        {
            candidate.State = AutonomousCandidateState.DeploymentFailedTerminal;
            candidate.LastCheckedAtUtc = now;
            candidate.NextEligibleCheckUtc = null;
            candidate.LastStatusMessage =
                $"Terminal retry policy applied for {ShortSha(candidate.CommitSha)} after {candidate.AttemptCount} artifact check(s) and {candidate.ConsecutiveTransientErrors} transient error(s).";
            await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
            _logger.LogWarning("Stopping autonomous polling for {App}@{Sha}: {Reason}",
                app.Slug, ShortSha(candidate.CommitSha), candidate.LastStatusMessage);
            return;
        }

        // Step 5: Resolve and verify GitHub Actions workflow artifact provenance (mandatory for GitHubActionsArtifact mode)
        VerifiedGitHubArtifact? verifiedArtifact = null;
        if (app.SourceMode == DeploymentSourceMode.GitHubActionsArtifact)
        {
            var artifactPath = Path.Combine(_options.DataRoot, "artifacts", app.Slug, $"{candidate.CommitSha}.zip");
            GitHubArtifactDownloadResult dl;
            try
            {
                dl = await _github.DownloadVerifiedWorkflowArtifactAsync(
                    pat,
                    app.RepositoryFullName,
                    app.Branch,
                    candidate.CommitSha,
                    app.GitHubWorkflowFileName,
                    artifactPath,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var safeReason = _secretProtector.RedactSecrets(ex.Message, [pat]);
                await RecordTransientFailureAsync(candidate, now, $"Transient GitHub API error: {safeReason}", null, ct).ConfigureAwait(false);
                _logger.LogWarning("Transient GitHub artifact lookup error for {App}@{Sha}: {Reason}",
                    app.Slug, ShortSha(candidate.CommitSha), safeReason);
                return;
            }

            if (dl.Found &&
                !string.IsNullOrWhiteSpace(dl.LocalZipPath) &&
                !string.IsNullOrWhiteSpace(dl.TrustedApiDigestSha256) &&
                dl.WorkflowRunId.HasValue &&
                dl.ArtifactId.HasValue)
            {
                verifiedArtifact = new VerifiedGitHubArtifact(
                    Verified: true,
                    LocalZipPath: dl.LocalZipPath,
                    TrustedSha256: dl.TrustedApiDigestSha256,
                    WorkflowRunId: dl.WorkflowRunId,
                    ArtifactId: dl.ArtifactId,
                    ArtifactName: dl.ArtifactName,
                    TrustSource: dl.TrustSource ?? $"GitHubActionsRun:{dl.WorkflowRunId}/Artifact:{dl.ArtifactId}",
                    FailureReason: null);

                candidate.State = AutonomousCandidateState.ArtifactVerified;
                candidate.WorkflowRunId = dl.WorkflowRunId;
                candidate.ArtifactId = dl.ArtifactId;
                candidate.ArtifactName = dl.ArtifactName;
                candidate.ConsecutiveTransientErrors = 0;
                candidate.LastCheckedAtUtc = now;
                candidate.NextEligibleCheckUtc = null;
                candidate.LastStatusMessage = $"Verified GitHub Actions artifact '{dl.ArtifactName}' (Run {dl.WorkflowRunId}, Artifact {dl.ArtifactId}).";
                await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
            }
            else
            {
                var safeReason = _secretProtector.RedactSecrets(dl.ErrorMessage ?? "Verified GitHub Actions artifact not yet ready.", [pat]);
                await HandleArtifactNotReadyOrRejectedAsync(app, candidate, dl, safeReason, now, ct).ConfigureAwait(false);
                return;
            }
        }
        else if (app.SourceMode == DeploymentSourceMode.ManualSignedPackage)
        {
            // ManualSignedPackage apps are never deployed autonomously without an explicit operator package
            candidate.State = AutonomousCandidateState.DeploymentFailedTerminal;
            candidate.LastCheckedAtUtc = now;
            candidate.LastStatusMessage = "ManualSignedPackage mode does not support autonomous GitHub artifact deployment.";
            await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
            return;
        }

        // Step 6: Persist DeploymentRunning before invoking the deployment engine (crash/restart safety)
        candidate.State = AutonomousCandidateState.DeploymentRunning;
        candidate.LastCheckedAtUtc = now;
        candidate.NextEligibleCheckUtc = null;
        candidate.LastStatusMessage = $"Launching autonomous deployment for {ShortSha(candidate.CommitSha)}.";
        await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);

        var envVars = await _store.ResolveDecryptedEnvVarsAsync(app.Id, EnvironmentTier.Production, ct).ConfigureAwait(false);
        var trigger = new TriggerDeploymentRequest(
            ApplicationId: app.Id,
            CommitSha: candidate.CommitSha,
            Branch: app.Branch,
            CommitMessage: candidate.CommitMessage,
            CommitAuthor: candidate.CommitAuthor,
            ArtifactDownloadUrl: null,
            ExpectedArtifactSha256: verifiedArtifact?.TrustedSha256,
            TriggeredAutomatically: true,
            RequestedBy: "ServerForge.Agent.AutoDeploy",
            VerifiedWorkflowRunId: verifiedArtifact?.WorkflowRunId,
            VerifiedArtifactId: verifiedArtifact?.ArtifactId,
            VerifiedArtifactName: verifiedArtifact?.ArtifactName,
            ArtifactTrustSource: verifiedArtifact?.TrustSource);

        var record = verifiedArtifact is not null
            ? await _deploymentEngine.EnqueueAndExecuteWithVerifiedArtifactAsync(app, trigger, envVars, verifiedArtifact, cancellationToken: ct).ConfigureAwait(false)
            : await _deploymentEngine.EnqueueAndExecuteAsync(app, trigger, envVars, cancellationToken: ct).ConfigureAwait(false);

        await _store.RecordDeploymentAsync(record, ct).ConfigureAwait(false);
        await _store.UpsertApplicationAsync(app, "ServerForge.Agent.AutoDeploy", ct).ConfigureAwait(false);

        // Step 7: Finalize durable candidate state
        candidate.DeploymentId = record.Id;
        candidate.LastCheckedAtUtc = _timeProvider.GetUtcNow();
        candidate.NextEligibleCheckUtc = null;
        if (record.State == DeploymentState.Successful)
        {
            candidate.State = AutonomousCandidateState.DeploymentSucceeded;
            candidate.LastStatusMessage = $"Autonomously deployed commit {ShortSha(candidate.CommitSha)} (Deployment {record.Id:N}).";
        }
        else
        {
            candidate.State = AutonomousCandidateState.DeploymentFailedTerminal;
            var safeSummary = _secretProtector.RedactSecrets(record.ErrorSummary ?? $"Deployment ended in state {record.State}.", [pat]);
            candidate.LastStatusMessage = $"Terminal failure ({record.State}): {safeSummary}";
        }

        await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
    }

    private async Task HandleArtifactNotReadyOrRejectedAsync(
        ManagedApplication app,
        AutonomousDeploymentCandidate candidate,
        GitHubArtifactDownloadResult dl,
        string safeReason,
        DateTimeOffset now,
        CancellationToken ct)
    {
        candidate.WorkflowRunId = dl.WorkflowRunId ?? candidate.WorkflowRunId;
        candidate.ArtifactId = dl.ArtifactId ?? candidate.ArtifactId;
        candidate.ArtifactName = dl.ArtifactName ?? candidate.ArtifactName;
        candidate.LastCheckedAtUtc = now;

        switch (dl.ResolutionStatus)
        {
            case GitHubArtifactResolutionStatus.WorkflowFailedOrCancelled:
                candidate.State = AutonomousCandidateState.WorkflowFailedOrCancelled;
                candidate.NextEligibleCheckUtc = null;
                candidate.LastStatusMessage = safeReason;
                await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
                _logger.LogWarning("GitHub workflow failed/cancelled for {App}@{Sha}; stopping polls for this commit until a newer commit arrives: {Reason}",
                    app.Slug, ShortSha(candidate.CommitSha), safeReason);
                return;

            case GitHubArtifactResolutionStatus.ProvenanceRejected:
                candidate.State = AutonomousCandidateState.DeploymentFailedTerminal;
                candidate.NextEligibleCheckUtc = null;
                candidate.LastStatusMessage = $"Provenance verification rejected artifact: {safeReason}";
                await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
                _logger.LogWarning("GitHub artifact provenance rejected for {App}@{Sha}; terminal policy applied: {Reason}",
                    app.Slug, ShortSha(candidate.CommitSha), safeReason);
                return;

            case GitHubArtifactResolutionStatus.RateLimited:
                var rlDelay = ResolveRateLimitBackoffSeconds(dl.RetryAfterSeconds, null, now);
                _rateLimitUntilUtc[app.Id] = now.AddSeconds(rlDelay);
                await RecordTransientFailureAsync(candidate, now, $"GitHub API rate limited: {safeReason}", rlDelay, ct).ConfigureAwait(false);
                _logger.LogWarning("GitHub rate limited while checking artifact for {App}@{Sha}; retrying in {Delay}s.",
                    app.Slug, ShortSha(candidate.CommitSha), rlDelay);
                return;

            case GitHubArtifactResolutionStatus.TransientApiError:
                await RecordTransientFailureAsync(candidate, now, safeReason, dl.RetryAfterSeconds, ct).ConfigureAwait(false);
                _logger.LogWarning("Transient GitHub API error checking artifact for {App}@{Sha} (attempt {Errors}): {Reason}",
                    app.Slug, ShortSha(candidate.CommitSha), candidate.ConsecutiveTransientErrors, safeReason);
                return;

            case GitHubArtifactResolutionStatus.WorkflowRunning:
            case GitHubArtifactResolutionStatus.WorkflowSucceededArtifactPending:
            case GitHubArtifactResolutionStatus.WorkflowNotStarted:
            default:
                candidate.AttemptCount++;
                candidate.ConsecutiveTransientErrors = 0;
                candidate.State = dl.ResolutionStatus switch
                {
                    GitHubArtifactResolutionStatus.WorkflowRunning => AutonomousCandidateState.WorkflowRunning,
                    GitHubArtifactResolutionStatus.WorkflowSucceededArtifactPending => AutonomousCandidateState.WorkflowSucceededArtifactPending,
                    _ => AutonomousCandidateState.WorkflowNotStarted
                };

                if (candidate.AttemptCount >= MaxArtifactWaitAttempts)
                {
                    candidate.State = AutonomousCandidateState.DeploymentFailedTerminal;
                    candidate.NextEligibleCheckUtc = null;
                    candidate.LastStatusMessage = $"Timed out waiting for verified artifact after {candidate.AttemptCount} checks: {safeReason}";
                }
                else
                {
                    var backoffSec = ComputeArtifactPendingBackoffSeconds(candidate.AttemptCount);
                    candidate.NextEligibleCheckUtc = backoffSec > 0 ? now.AddSeconds(backoffSec) : null;
                    candidate.LastStatusMessage = safeReason;
                }

                await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
                _logger.LogInformation("Waiting for verified GitHub Actions artifact for {App}@{Sha} (State={State}, Check={Check}): {Reason}",
                    app.Slug, ShortSha(candidate.CommitSha), candidate.State, candidate.AttemptCount, safeReason);
                return;
        }
    }

    private async Task RecordTransientFailureAsync(
        AutonomousDeploymentCandidate candidate,
        DateTimeOffset now,
        string safeReason,
        int? explicitRetryAfterSeconds,
        CancellationToken ct)
    {
        candidate.ConsecutiveTransientErrors++;
        candidate.LastCheckedAtUtc = now;

        if (candidate.ConsecutiveTransientErrors >= MaxConsecutiveTransientErrors)
        {
            candidate.State = AutonomousCandidateState.DeploymentFailedTerminal;
            candidate.NextEligibleCheckUtc = null;
            candidate.LastStatusMessage = $"Terminal retry policy applied after {candidate.ConsecutiveTransientErrors} consecutive transient errors: {safeReason}";
        }
        else
        {
            var backoffSec = explicitRetryAfterSeconds.HasValue
                ? Math.Clamp(explicitRetryAfterSeconds.Value, 15, 900)
                : Math.Min(300, 15 * (1 << Math.Min(4, candidate.ConsecutiveTransientErrors - 1)));
            candidate.NextEligibleCheckUtc = now.AddSeconds(backoffSec);
            candidate.LastStatusMessage = safeReason;
        }

        await _store.UpsertAutonomousCandidateAsync(candidate, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// First 3 pending checks run at the worker's normal poll cadence (0 extra delay) so fast CI builds
    /// deploy promptly; longer builds use bounded exponential backoff (15s, 30s, 60s, capped at 120s).
    /// </summary>
    public static int ComputeArtifactPendingBackoffSeconds(int attemptCount)
    {
        if (attemptCount <= 3) return 0;
        var step = Math.Min(3, attemptCount - 4);
        return Math.Min(120, 15 * (1 << step));
    }

    private static int ResolveRateLimitBackoffSeconds(int? retryAfterSeconds, DateTimeOffset? resetUtc, DateTimeOffset now)
    {
        if (retryAfterSeconds.HasValue && retryAfterSeconds.Value > 0)
            return Math.Clamp(retryAfterSeconds.Value, 15, 900);

        if (resetUtc.HasValue && resetUtc.Value > now)
            return Math.Clamp((int)Math.Ceiling((resetUtc.Value - now).TotalSeconds), 15, 900);

        return 60;
    }

    private static bool IsTerminalState(AutonomousCandidateState state)
        => state is AutonomousCandidateState.DeploymentSucceeded
            or AutonomousCandidateState.WorkflowFailedOrCancelled
            or AutonomousCandidateState.DeploymentFailedTerminal;

    private static bool IsPendingState(AutonomousCandidateState state)
        => state is AutonomousCandidateState.CommitDetected
            or AutonomousCandidateState.WorkflowNotStarted
            or AutonomousCandidateState.WorkflowRunning
            or AutonomousCandidateState.WorkflowSucceededArtifactPending
            or AutonomousCandidateState.ArtifactVerified;

    private static string ShortSha(string sha)
        => sha.Length >= 7 ? sha[..7] : sha;
}
