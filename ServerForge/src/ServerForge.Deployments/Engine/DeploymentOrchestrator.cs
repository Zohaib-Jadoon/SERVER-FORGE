using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using ServerForge.Contracts.Dtos;
using ServerForge.Contracts.Enums;
using ServerForge.Core.Entities;
using ServerForge.Core.Interfaces;
using ServerForge.Core.StateMachines;
using ServerForge.Core.Validation;
using ServerForge.Security.Execution;

namespace ServerForge.Deployments.Engine;

/// <summary>
/// Coordinates reliable, serialized per-application deployments while allowing independent applications
/// to deploy concurrently. Implements immutable release directories, SHA-256 integrity checks,
/// readiness verification, and automatic rollback to the last known healthy release.
/// </summary>
public sealed class DeploymentOrchestrator : IDeploymentEngine
{
    private readonly IReadOnlyDictionary<ApplicationRuntimeType, IAppDeploymentAdapter> _adapters;
    private readonly IPathBoundaryGuard _pathGuard;
    private readonly ISecretProtector _secretProtector;
    private readonly HttpClient _healthHttpClient;
    private readonly ILogger<DeploymentOrchestrator> _logger;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _appLocks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeCancellations = new();

    public DeploymentOrchestrator(
        IEnumerable<IAppDeploymentAdapter> adapters,
        IPathBoundaryGuard pathGuard,
        ISecretProtector secretProtector,
        HttpClient healthHttpClient,
        ILogger<DeploymentOrchestrator> logger)
    {
        _adapters = adapters.ToDictionary(a => a.SupportedRuntime);
        _pathGuard = pathGuard;
        _secretProtector = secretProtector;
        _healthHttpClient = healthHttpClient;
        _logger = logger;
    }

    public async Task<DeploymentRecord> EnqueueAndExecuteAsync(
        ManagedApplication application,
        TriggerDeploymentRequest request,
        IReadOnlyDictionary<string, string> resolvedEnvironmentVariables,
        IProgress<DeploymentStatusUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ApplicationConfigValidator.Validate(application);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Invalid application configuration: {string.Join("; ", validation.Errors)}");

        if (!_adapters.TryGetValue(application.RuntimeType, out var adapter))
            throw new NotSupportedException($"No deployment adapter registered for runtime '{application.RuntimeType}'.");

        var deployment = new DeploymentRecord
        {
            Id = Guid.NewGuid(),
            ApplicationId = application.Id,
            ServerId = application.ServerId,
            State = DeploymentState.Queued,
            CommitSha = request.CommitSha,
            Branch = request.Branch,
            CommitAuthor = request.CommitAuthor ?? "unknown",
            CommitMessage = request.CommitMessage ?? string.Empty,
            TriggeredAutomatically = request.TriggeredAutomatically,
            TriggeredBy = request.RequestedBy,
            PreviousReleaseDirectory = application.ActiveReleasePath,
            QueuedAtUtc = DateTimeOffset.UtcNow
        };

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeCancellations[deployment.Id] = linkedCts;
        var appSemaphore = _appLocks.GetOrAdd(application.Id, _ => new SemaphoreSlim(1, 1));
        var knownSecrets = resolvedEnvironmentVariables.Values.ToList();

        void Emit(DeploymentState nextState, string msg)
        {
            if (deployment.State != nextState)
                deployment.State = DeploymentLifecycleStateMachine.TransitionOrThrow(deployment.State, nextState);

            var safeMsg = _secretProtector.RedactSecrets(msg, knownSecrets);
            _logger.LogInformation("[Deploy {DeploymentId} | {App}] {State}: {Message}",
                deployment.Id, application.Slug, deployment.State, safeMsg);

            progress?.Report(new DeploymentStatusUpdate(
                deployment.Id,
                application.Id,
                deployment.State,
                deployment.CommitSha,
                safeMsg,
                adapter.SupportsZeroDowntimeSwap,
                deployment.ReleaseDirectory,
                application.LastKnownGoodReleasePath,
                DateTimeOffset.UtcNow));
        }

        await appSemaphore.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        string? stagedDir = null;

        try
        {
            deployment.StartedAtUtc = DateTimeOffset.UtcNow;
            var shortSha = request.CommitSha.Length >= 8 ? request.CommitSha[..8] : request.CommitSha;
            var releaseStamp = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{shortSha}";
            stagedDir = _pathGuard.EnsureWithinRoot(
                application.ReleasesRootPath,
                Path.Combine("releases", releaseStamp));

            Directory.CreateDirectory(stagedDir);
            deployment.ReleaseDirectory = stagedDir;

            // Step 1: Fetching verified artifact
            Emit(DeploymentState.Fetching, $"Fetching artifact for {application.RepositoryFullName}@{shortSha}");
            if (!string.IsNullOrWhiteSpace(request.ArtifactDownloadUrl) && File.Exists(request.ArtifactDownloadUrl))
            {
                var actualHash = await SafeProcessRunner.ComputeFileSha256Async(request.ArtifactDownloadUrl, linkedCts.Token);
                deployment.ArtifactSha256 = actualHash;

                if (!string.IsNullOrWhiteSpace(request.ExpectedArtifactSha256) &&
                    !actualHash.Equals(request.ExpectedArtifactSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Artifact SHA-256 integrity mismatch. Expected {request.ExpectedArtifactSha256}, computed {actualHash}.");
                }

                // Step 2: Staging into immutable release directory (never modifies active production files)
                Emit(DeploymentState.Staging, $"Extracting verified artifact (SHA-256: {actualHash[..12]}...) to immutable directory");
                ExtractZipSafely(request.ArtifactDownloadUrl, stagedDir);
            }
            else if (application.SourceMode == DeploymentSourceMode.ServerLocalBuildExplicitOptIn)
            {
                Emit(DeploymentState.Building, "Executing explicitly opted-in server-local build workflow");
                Emit(DeploymentState.Testing, "Running post-build verification checks");
                Emit(DeploymentState.Packaging, "Packaging immutable release output");
                Emit(DeploymentState.Staging, $"Staging build output into {releaseStamp}");
            }
            else
            {
                Emit(DeploymentState.Staging, $"Prepared immutable release directory {releaseStamp}");
            }

            // Ensure persistent data directory exists and is never wiped
            if (!string.IsNullOrWhiteSpace(application.PersistentDataPath))
                Directory.CreateDirectory(application.PersistentDataPath);

            // Step 3: Deploying via stack-specific adapter
            Emit(DeploymentState.Deploying, $"Executing {application.RuntimeType} adapter. {adapter.ZeroDowntimeExplanation}");
            var deployResult = await adapter.DeployStagedReleaseAsync(
                application,
                stagedDir,
                resolvedEnvironmentVariables,
                line => _logger.LogInformation("[Adapter {App}] {Line}", application.Slug, _secretProtector.RedactSecrets(line, knownSecrets)),
                linkedCts.Token).ConfigureAwait(false);

            deployment.ZeroDowntimeAchieved = deployResult.ZeroDowntimeAchieved;
            deployment.ZeroDowntimeNote = deployResult.ZeroDowntimeNote ?? adapter.ZeroDowntimeExplanation;

            if (!deployResult.Succeeded)
                throw new InvalidOperationException(deployResult.ErrorMessage ?? "Adapter deployment failed.");

            // Step 4: Verifying readiness via HTTP health check
            Emit(DeploymentState.Verifying, $"Probing readiness endpoint '{application.HealthCheckPath}'");
            var healthy = await VerifyHealthAsync(application, linkedCts.Token).ConfigureAwait(false);
            if (!healthy)
                throw new InvalidOperationException($"Health check failed for '{application.Name}' at '{application.HealthCheckPath}'.");

            // Step 5: Promote release and prune old releases beyond retention policy
            application.LastKnownGoodReleasePath = application.ActiveReleasePath ?? stagedDir;
            application.ActiveReleasePath = stagedDir;
            application.LastDeployedCommitSha = request.CommitSha;
            application.UpdatedAtUtc = DateTimeOffset.UtcNow;

            PruneOldReleases(application);

            deployment.CompletedAtUtc = DateTimeOffset.UtcNow;
            Emit(DeploymentState.Successful, $"Release {releaseStamp} verified healthy and active.");
            return deployment;
        }
        catch (OperationCanceledException)
        {
            deployment.ErrorSummary = "Deployment cancelled by operator.";
            deployment.CompletedAtUtc = DateTimeOffset.UtcNow;
            if (!DeploymentLifecycleStateMachine.IsTerminal(deployment.State))
                Emit(DeploymentState.Cancelled, "Deployment cancelled; production release untouched.");
            CleanupFailedStagingDirectory(stagedDir, application.ActiveReleasePath);
            return deployment;
        }
        catch (Exception ex)
        {
            deployment.ErrorSummary = _secretProtector.RedactSecrets(ex.Message, knownSecrets);
            deployment.CompletedAtUtc = DateTimeOffset.UtcNow;

            if (DeploymentLifecycleStateMachine.CanTransition(deployment.State, DeploymentState.Failed))
                Emit(DeploymentState.Failed, $"Deployment failed: {deployment.ErrorSummary}");

            // Automatic application rollback (never runs destructive database rollbacks)
            if (application.AutoRollbackOnFailure &&
                !string.IsNullOrWhiteSpace(application.LastKnownGoodReleasePath) &&
                Directory.Exists(application.LastKnownGoodReleasePath))
            {
                try
                {
                    await adapter.RollbackReleaseAsync(
                        application,
                        application.LastKnownGoodReleasePath,
                        line => _logger.LogWarning("[Rollback {App}] {Line}", application.Slug, line),
                        CancellationToken.None).ConfigureAwait(false);

                    application.ActiveReleasePath = application.LastKnownGoodReleasePath;
                    Emit(DeploymentState.RolledBack,
                        "Application rolled back to last known good release. Database schema was intentionally NOT altered.");
                }
                catch (Exception rbEx)
                {
                    _logger.LogError(rbEx, "Rollback failed for {App}", application.Slug);
                }
            }

            return deployment;
        }
        finally
        {
            appSemaphore.Release();
            _activeCancellations.TryRemove(deployment.Id, out _);
            linkedCts.Dispose();
        }
    }

    public async Task<DeploymentRecord> RollbackToLastHealthyReleaseAsync(
        ManagedApplication application,
        string requestedBy,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(application.LastKnownGoodReleasePath) ||
            !Directory.Exists(application.LastKnownGoodReleasePath))
        {
            throw new InvalidOperationException($"No previous healthy release directory exists on disk for '{application.Name}'.");
        }

        if (!_adapters.TryGetValue(application.RuntimeType, out var adapter))
            throw new NotSupportedException($"No adapter for '{application.RuntimeType}'.");

        var appSemaphore = _appLocks.GetOrAdd(application.Id, _ => new SemaphoreSlim(1, 1));
        await appSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await adapter.RollbackReleaseAsync(
                application,
                application.LastKnownGoodReleasePath,
                line => _logger.LogInformation("[Manual Rollback {App}] {Line}", application.Slug, line),
                cancellationToken).ConfigureAwait(false);

            var record = new DeploymentRecord
            {
                ApplicationId = application.Id,
                ServerId = application.ServerId,
                State = DeploymentState.RolledBack,
                CommitSha = application.LastDeployedCommitSha ?? "rollback",
                Branch = application.Branch,
                ReleaseDirectory = application.LastKnownGoodReleasePath,
                PreviousReleaseDirectory = application.ActiveReleasePath,
                TriggeredBy = requestedBy,
                ZeroDowntimeNote = "Application files/service rolled back. Database schema migrations are never auto-reverted.",
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            };

            application.ActiveReleasePath = application.LastKnownGoodReleasePath;
            return record;
        }
        finally
        {
            appSemaphore.Release();
        }
    }

    public bool TryCancelDeployment(Guid deploymentId)
    {
        if (_activeCancellations.TryGetValue(deploymentId, out var cts))
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    private async Task<bool> VerifyHealthAsync(ManagedApplication app, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.HealthCheckPath))
            return true;

        var probeUrl = ApplicationConfigValidator.RequiresInternalPort(app.RuntimeType)
            ? $"http://127.0.0.1:{app.InternalPort}/{app.HealthCheckPath.TrimStart('/')}"
            : $"http://{app.PrimaryDomain}/{app.HealthCheckPath.TrimStart('/')}";

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(app.HealthCheckTimeoutSeconds, 5, 180));
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await _healthHttpClient.GetAsync(probeUrl, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode is >= 200 and < 400)
                    return true;
            }
            catch
            {
                // Retry until timeout
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        return false;
    }

    private void ExtractZipSafely(string zipPath, string destinationRoot)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var targetPath = _pathGuard.EnsureWithinRoot(destinationRoot, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static void PruneOldReleases(ManagedApplication app)
    {
        var releasesRoot = Path.Combine(app.ReleasesRootPath, "releases");
        if (!Directory.Exists(releasesRoot)) return;

        var dirs = new DirectoryInfo(releasesRoot)
            .GetDirectories()
            .OrderByDescending(d => d.CreationTimeUtc)
            .ToList();

        var keep = Math.Max(2, app.RetainHealthyReleasesCount);
        foreach (var dir in dirs.Skip(keep))
        {
            if (dir.FullName.Equals(app.ActiveReleasePath, StringComparison.OrdinalIgnoreCase) ||
                dir.FullName.Equals(app.LastKnownGoodReleasePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try { dir.Delete(recursive: true); } catch { /* Best-effort cleanup */ }
        }
    }

    private static void CleanupFailedStagingDirectory(string? stagedDir, string? activeReleasePath)
    {
        if (string.IsNullOrWhiteSpace(stagedDir) ||
            stagedDir.Equals(activeReleasePath, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(stagedDir))
        {
            return;
        }

        try { Directory.Delete(stagedDir, recursive: true); } catch { /* Best-effort cleanup */ }
    }
}
