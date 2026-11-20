using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.RegularExpressions;
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
/// Durable deployment engine enforcing:
/// 1. Exact commit SHA, branch, and repository authorization checks.
/// 2. Trusted artifact provenance & SHA-256 verification (never succeeds on missing/empty/corrupt/untrusted artifacts).
/// 3. Runtime-specific staged application-file validation before cutover.
/// 4. Per-application SemaphoreSlim serialization with concurrent independent deployments.
/// 5. Persisted state transitions for Agent-restart crash recovery & duplicate-commit prevention.
/// 6. Safe LastKnownGoodReleasePath tracking across first deploy, upgrades, failed updates, and rollbacks.
/// </summary>
public sealed partial class DeploymentOrchestrator : IDeploymentEngine
{
    private readonly IReadOnlyDictionary<ApplicationRuntimeType, IAppDeploymentAdapter> _adapters;
    private readonly IPathBoundaryGuard _pathGuard;
    private readonly ISecretProtector _secretProtector;
    private readonly HttpClient _healthHttpClient;
    private readonly ILogger<DeploymentOrchestrator> _logger;
    private readonly IDeploymentStatePersistence? _persistence;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _appLocks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeCancellations = new();

    [GeneratedRegex(@"^[0-9a-fA-F]{40}$", RegexOptions.Compiled)]
    private static partial Regex FullGitShaRegex();

    public DeploymentOrchestrator(
        IEnumerable<IAppDeploymentAdapter> adapters,
        IPathBoundaryGuard pathGuard,
        ISecretProtector secretProtector,
        HttpClient healthHttpClient,
        ILogger<DeploymentOrchestrator> logger,
        IDeploymentStatePersistence? persistence = null)
    {
        _adapters = adapters.ToDictionary(a => a.SupportedRuntime);
        _pathGuard = pathGuard;
        _secretProtector = secretProtector;
        _healthHttpClient = healthHttpClient;
        _logger = logger;
        _persistence = persistence;
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

        if (!application.RepositoryExplicitlyAuthorized)
            throw new UnauthorizedAccessException($"Repository '{application.RepositoryFullName}' is not explicitly authorized for deployment.");

        if (!string.Equals(request.Branch, application.Branch, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Deployment branch mismatch: expected '{application.Branch}', received '{request.Branch}'.");

        if (!FullGitShaRegex().IsMatch(request.CommitSha ?? string.Empty))
            throw new InvalidOperationException($"Deployment requires an exact 40-character Git commit SHA. Received: '{request.CommitSha}'.");

        if (!_adapters.TryGetValue(application.RuntimeType, out var adapter))
            throw new NotSupportedException($"No deployment adapter registered for runtime '{application.RuntimeType}'.");

        var deployment = new DeploymentRecord
        {
            Id = Guid.NewGuid(),
            ApplicationId = application.Id,
            ServerId = application.ServerId,
            State = DeploymentState.Queued,
            CommitSha = request.CommitSha!,
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

        async Task EmitAsync(DeploymentState nextState, string msg)
        {
            if (deployment.State != nextState)
                deployment.State = DeploymentLifecycleStateMachine.TransitionOrThrow(deployment.State, nextState);

            var safeMsg = _secretProtector.RedactSecrets(msg, knownSecrets);
            _logger.LogInformation("[Deploy {DeploymentId} | {App}] {State}: {Message}",
                deployment.Id, application.Slug, deployment.State, safeMsg);

            if (_persistence is not null)
                await _persistence.SaveDeploymentSnapshotAsync(deployment, CancellationToken.None).ConfigureAwait(false);

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
        var cutoverAttempted = false;
        // Capture pre-deployment active release cleanly before any mutation
        var rollbackCandidateDir = !string.IsNullOrWhiteSpace(application.ActiveReleasePath) && Directory.Exists(application.ActiveReleasePath)
            ? application.ActiveReleasePath
            : (!string.IsNullOrWhiteSpace(application.LastKnownGoodReleasePath) && Directory.Exists(application.LastKnownGoodReleasePath)
                ? application.LastKnownGoodReleasePath
                : null);

        try
        {
            if (request.TriggeredAutomatically && _persistence is not null)
            {
                if (await _persistence.HasAttemptedAutonomousDeploymentForCommitAsync(application.Id, deployment.CommitSha, linkedCts.Token))
                {
                    deployment.ErrorSummary = $"Commit {deployment.CommitSha[..8]} was already deployed, is in progress, or previously failed autonomous deployment; skipping to prevent duplicate/endless retry loops.";
                    deployment.CompletedAtUtc = DateTimeOffset.UtcNow;
                    await EmitAsync(DeploymentState.Cancelled, deployment.ErrorSummary);
                    return deployment;
                }
            }

            deployment.StartedAtUtc = DateTimeOffset.UtcNow;
            var shortSha = deployment.CommitSha[..8];
            var releaseStamp = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{shortSha}";
            stagedDir = _pathGuard.EnsureWithinRoot(
                application.ReleasesRootPath,
                Path.Combine("releases", releaseStamp));

            Directory.CreateDirectory(stagedDir);
            deployment.ReleaseDirectory = stagedDir;

            // Step 1: Fetching & Strict Provenance Verification (Problem C)
            await EmitAsync(DeploymentState.Fetching, $"Verifying release source for {application.RepositoryFullName}@{shortSha}");

            if (application.SourceMode is DeploymentSourceMode.GitHubActionsArtifact or DeploymentSourceMode.ManualSignedPackage)
            {
                if (string.IsNullOrWhiteSpace(request.ArtifactDownloadUrl) || !File.Exists(request.ArtifactDownloadUrl))
                {
                    throw new FileNotFoundException(
                        "Deployment aborted: required release artifact archive is missing on disk. ServerForge never declares success without a verified artifact.");
                }

                var zipInfo = new FileInfo(request.ArtifactDownloadUrl);
                if (zipInfo.Length == 0)
                {
                    throw new InvalidDataException("Deployment aborted: release artifact archive is empty (0 bytes).");
                }

                if (string.IsNullOrWhiteSpace(request.ExpectedArtifactSha256))
                {
                    throw new UnauthorizedAccessException(
                        "Deployment aborted: trusted expected SHA-256 provenance digest is required. Post-download self-hashing alone is never accepted as proof of trust.");
                }

                if (application.SourceMode == DeploymentSourceMode.GitHubActionsArtifact &&
                    (!request.VerifiedWorkflowRunId.HasValue || !request.VerifiedArtifactId.HasValue))
                {
                    throw new UnauthorizedAccessException(
                        "Deployment aborted: GitHub Actions workflow run ID and artifact identity must be verified prior to deployment.");
                }

                var actualHash = await SafeProcessRunner.ComputeFileSha256Async(request.ArtifactDownloadUrl, linkedCts.Token);
                deployment.ArtifactSha256 = actualHash;

                if (!actualHash.Equals(request.ExpectedArtifactSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Artifact SHA-256 integrity mismatch! Expected trusted digest {request.ExpectedArtifactSha256}, computed {actualHash}.");
                }

                // Step 2: Safe Zip-Slip extraction into immutable directory
                await EmitAsync(DeploymentState.Staging,
                    $"Extracting verified artifact ({request.ArtifactTrustSource ?? "TrustedDigest"}, SHA-256: {actualHash[..12]}...) to immutable release {releaseStamp}");

                var extractedFileCount = ExtractZipSafely(request.ArtifactDownloadUrl, stagedDir);
                if (extractedFileCount == 0)
                {
                    throw new InvalidDataException("Deployment aborted: artifact archive contained zero files.");
                }
            }
            else if (application.SourceMode == DeploymentSourceMode.ServerLocalBuildExplicitOptIn)
            {
                if (string.IsNullOrWhiteSpace(application.BuildCommand))
                {
                    throw new InvalidOperationException(
                        "Server-local build mode is enabled, but no BuildCommand is configured.");
                }

                await EmitAsync(DeploymentState.Building, $"Running explicitly opted-in server-local build: {application.BuildCommand}");
                var buildRes = await SafeProcessRunner.RunAsync(
                    OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                    OperatingSystem.IsWindows() ? ["/c", application.BuildCommand] : ["-c", application.BuildCommand],
                    workingDirectory: stagedDir,
                    environmentVariables: resolvedEnvironmentVariables,
                    cancellationToken: linkedCts.Token).ConfigureAwait(false);

                if (buildRes.ExitCode != 0)
                    throw new InvalidOperationException($"Server-local build failed (exit {buildRes.ExitCode}): {buildRes.StandardError}");

                await EmitAsync(DeploymentState.Staging, $"Staged server-local build output in {releaseStamp}");
            }

            // Step 3: Validate required runtime application files before touching IIS/Windows Services
            ValidateStagedApplicationFiles(application.RuntimeType, stagedDir);

            // Preserve persistent data directory untouched
            if (!string.IsNullOrWhiteSpace(application.PersistentDataPath))
                Directory.CreateDirectory(application.PersistentDataPath);

            // Step 4: Deploying via stack-specific adapter
            cutoverAttempted = true;
            await EmitAsync(DeploymentState.Deploying, $"Executing {application.RuntimeType} adapter. {adapter.ZeroDowntimeExplanation}");
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

            // Step 5: Verifying readiness via HTTP health check (missing health endpoint fails cleanly)
            await EmitAsync(DeploymentState.Verifying, $"Probing readiness endpoint '{application.HealthCheckPath}'");
            var healthy = await VerifyHealthAsync(application, linkedCts.Token).ConfigureAwait(false);
            if (!healthy)
                throw new InvalidOperationException($"Health check failed for '{application.Name}' at '{application.HealthCheckPath}'.");

            // Step 6: Promote release ONLY AFTER health verification passes (Problem G)
            if (!string.IsNullOrWhiteSpace(rollbackCandidateDir))
                application.LastKnownGoodReleasePath = rollbackCandidateDir;
            else
                application.LastKnownGoodReleasePath = stagedDir; // First healthy release becomes baseline LKG

            application.ActiveReleasePath = stagedDir;
            application.LastDeployedCommitSha = deployment.CommitSha;
            application.UpdatedAtUtc = DateTimeOffset.UtcNow;

            PruneOldReleases(application);

            deployment.CompletedAtUtc = DateTimeOffset.UtcNow;
            await EmitAsync(DeploymentState.Successful, $"Release {releaseStamp} verified healthy and promoted.");
            return deployment;
        }
        catch (OperationCanceledException)
        {
            deployment.ErrorSummary = "Deployment cancelled by operator.";
            deployment.CompletedAtUtc = DateTimeOffset.UtcNow;
            if (!DeploymentLifecycleStateMachine.IsTerminal(deployment.State))
                await EmitAsync(DeploymentState.Cancelled, "Deployment cancelled; production release untouched.");
            if (!cutoverAttempted)
                CleanupFailedStagingDirectory(stagedDir, application.ActiveReleasePath);
            return deployment;
        }
        catch (Exception ex)
        {
            deployment.ErrorSummary = _secretProtector.RedactSecrets(ex.Message, knownSecrets);
            deployment.CompletedAtUtc = DateTimeOffset.UtcNow;

            if (DeploymentLifecycleStateMachine.CanTransition(deployment.State, DeploymentState.Failed))
                await EmitAsync(DeploymentState.Failed, $"Deployment failed: {deployment.ErrorSummary}");

            // Automatic application rollback if cutover was attempted and a previous healthy release exists
            if (cutoverAttempted &&
                application.AutoRollbackOnFailure &&
                !string.IsNullOrWhiteSpace(rollbackCandidateDir) &&
                Directory.Exists(rollbackCandidateDir))
            {
                try
                {
                    await adapter.RollbackReleaseAsync(
                        application,
                        rollbackCandidateDir,
                        line => _logger.LogWarning("[Rollback {App}] {Line}", application.Slug, line),
                        CancellationToken.None).ConfigureAwait(false);

                    application.ActiveReleasePath = rollbackCandidateDir;
                    application.LastKnownGoodReleasePath = rollbackCandidateDir;
                    await EmitAsync(DeploymentState.RolledBack,
                        "Application rolled back to last known good release. Database schema was intentionally NOT altered.");
                }
                catch (Exception rbEx)
                {
                    deployment.ErrorSummary += $" | ROLLBACK FAILED: {_secretProtector.RedactSecrets(rbEx.Message, knownSecrets)}";
                    if (_persistence is not null)
                        await _persistence.SaveDeploymentSnapshotAsync(deployment, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogError(rbEx, "Rollback failed for {App}; manual recovery required.", application.Slug);
                }
            }
            else if (!cutoverAttempted)
            {
                // Failed before touching IIS/services (e.g. missing/corrupt artifact or first deployment failure)
                CleanupFailedStagingDirectory(stagedDir, application.ActiveReleasePath);
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
            if (_persistence is not null)
                await _persistence.SaveDeploymentSnapshotAsync(record, cancellationToken).ConfigureAwait(false);
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

    public static void ValidateStagedApplicationFiles(ApplicationRuntimeType runtime, string stagedDir)
    {
        var files = Directory.GetFiles(stagedDir, "*", SearchOption.AllDirectories);
        if (files.Length == 0)
            throw new InvalidDataException("Staged release directory contains no application files.");

        var hasRequired = runtime switch
        {
            ApplicationRuntimeType.NextJsSsr =>
                files.Any(f => Path.GetFileName(f).Equals("server.js", StringComparison.OrdinalIgnoreCase) ||
                               Path.GetFileName(f).Equals("package.json", StringComparison.OrdinalIgnoreCase)),
            ApplicationRuntimeType.NestJsApi or ApplicationRuntimeType.ExpressApi or ApplicationRuntimeType.NodeJsGeneric =>
                files.Any(f => Path.GetFileName(f).Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)),
            ApplicationRuntimeType.ReactViteStatic or ApplicationRuntimeType.StaticHtml =>
                files.Any(f => Path.GetFileName(f).Equals("index.html", StringComparison.OrdinalIgnoreCase)),
            ApplicationRuntimeType.AspNetCore =>
                files.Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                               Path.GetFileName(f).Equals("web.config", StringComparison.OrdinalIgnoreCase)),
            ApplicationRuntimeType.ClassicAsp =>
                files.Any(f => f.EndsWith(".asp", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
                               f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)),
            ApplicationRuntimeType.PythonFastApi =>
                files.Any(f => f.EndsWith(".py", StringComparison.OrdinalIgnoreCase)),
            _ => true
        };

        if (!hasRequired)
        {
            throw new InvalidDataException(
                $"Staged release failed application-file validation for runtime '{runtime}': expected runtime entry files were not found in the extracted artifact.");
        }
    }

    private async Task<bool> VerifyHealthAsync(ManagedApplication app, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.HealthCheckPath))
            return false; // Require an explicit readiness path rather than blindly assuming healthy

        var probeUrl = ResolveHealthCheckUrl(app);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(app.HealthCheckTimeoutSeconds, 3, 180));
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
                // Retry until timeout expires
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }

        return false;
    }

    public static string ResolveHealthCheckUrl(ManagedApplication app)
    {
        var path = app.HealthCheckPath.TrimStart('/');
        if (ApplicationConfigValidator.RequiresInternalPort(app.RuntimeType))
            return $"http://127.0.0.1:{app.InternalPort}/{path}";

        if (app.InternalPort is >= 1024 and <= 65535)
        {
            if (string.IsNullOrWhiteSpace(app.PrimaryDomain) ||
                app.PrimaryDomain.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return $"http://127.0.0.1:{app.InternalPort}/{path}";
            }

            return $"http://{app.PrimaryDomain.Trim()}:{app.InternalPort}/{path}";
        }

        return $"http://{app.PrimaryDomain.Trim()}/{path}";
    }

    private int ExtractZipSafely(string zipPath, string destinationRoot)
    {
        var count = 0;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var targetPath = _pathGuard.EnsureWithinRoot(destinationRoot, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
            count++;
        }
        return count;
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
