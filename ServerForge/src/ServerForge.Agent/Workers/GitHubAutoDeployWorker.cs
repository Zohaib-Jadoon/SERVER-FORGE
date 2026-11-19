using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ServerForge.Agent.Configuration;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Interfaces;
using ServerForge.Infrastructure.Persistence;

namespace ServerForge.Agent.Workers;

/// <summary>
/// Autonomous background Windows Service worker that polls authorized GitHub repositories using
/// conditional ETag requests and executes staged deployments even when ServerForge Desktop is closed.
/// </summary>
public sealed class GitHubAutoDeployWorker : BackgroundService
{
    private readonly ServerForgeStore _store;
    private readonly IGitHubClient _github;
    private readonly IDeploymentEngine _deploymentEngine;
    private readonly ISecretProtector _secretProtector;
    private readonly AgentIdentityStore _identityStore;
    private readonly AgentOptions _options;
    private readonly ILogger<GitHubAutoDeployWorker> _logger;
    private readonly ConcurrentDictionary<Guid, string> _etagCache = new();

    public GitHubAutoDeployWorker(
        ServerForgeStore store,
        IGitHubClient github,
        IDeploymentEngine deploymentEngine,
        ISecretProtector secretProtector,
        AgentIdentityStore identityStore,
        IOptions<AgentOptions> options,
        ILogger<GitHubAutoDeployWorker> logger)
    {
        _store = store;
        _github = github;
        _deploymentEngine = deploymentEngine;
        _secretProtector = secretProtector;
        _identityStore = identityStore;
        _options = options.Value;
        _logger = logger;
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
            _etagCache.TryGetValue(app.Id, out var lastETag);
            var head = await _github.CheckBranchHeadConditionalAsync(pat, app.RepositoryFullName, app.Branch, lastETag, ct);

            if (!string.IsNullOrWhiteSpace(head.ETag))
                _etagCache[app.Id] = head.ETag;

            if (!head.Modified || string.IsNullOrWhiteSpace(head.LatestCommitSha))
                continue;

            if (string.Equals(app.LastDeployedCommitSha, head.LatestCommitSha, StringComparison.OrdinalIgnoreCase))
                continue;

            _logger.LogInformation("Detected new commit {Sha} on {Repo}:{Branch} for {App}. Obtaining verified build artifact.",
                head.LatestCommitSha[..Math.Min(7, head.LatestCommitSha.Length)], app.RepositoryFullName, app.Branch, app.Name);

            string? localArtifactZip = null;
            string? artifactSha = null;

            if (app.SourceMode == DeploymentSourceMode.GitHubActionsArtifact)
            {
                var artifactPath = Path.Combine(_options.DataRoot, "artifacts", app.Slug, $"{head.LatestCommitSha}.zip");
                var dl = await _github.DownloadVerifiedWorkflowArtifactAsync(pat, app.RepositoryFullName, head.LatestCommitSha, artifactPath, ct);
                if (!dl.Found)
                {
                    _logger.LogInformation("Waiting for GitHub Actions artifact for {App}@{Sha}: {Reason}",
                        app.Slug, head.LatestCommitSha[..Math.Min(7, head.LatestCommitSha.Length)], dl.ErrorMessage);
                    continue;
                }

                localArtifactZip = dl.LocalZipPath;
                artifactSha = dl.ComputedSha256;
            }

            var envVars = await _store.ResolveDecryptedEnvVarsAsync(app.Id, EnvironmentTier.Production, ct);
            var trigger = new TriggerDeploymentRequest(
                ApplicationId: app.Id,
                CommitSha: head.LatestCommitSha,
                Branch: app.Branch,
                CommitMessage: head.CommitMessage,
                CommitAuthor: head.CommitAuthor,
                ArtifactDownloadUrl: localArtifactZip,
                ExpectedArtifactSha256: artifactSha,
                TriggeredAutomatically: true,
                RequestedBy: "ServerForge.Agent.AutoDeploy");

            var record = await _deploymentEngine.EnqueueAndExecuteAsync(app, trigger, envVars, cancellationToken: ct);
            await _store.RecordDeploymentAsync(record, ct);
            await _store.UpsertApplicationAsync(app, "ServerForge.Agent.AutoDeploy", ct);
        }
    }
}
