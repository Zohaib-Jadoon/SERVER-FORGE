# ServerForge — Engineering Memory & Verified Baseline

## Verified Native Platform Baseline (DO NOT REGRESS)
- **Repository:** `https://github.com/Zohaib-Jadoon/SERVER-FORGE`
- **Architecture:** Native `.NET 10 LTS` Windows WPF Desktop (`ServerForge.Desktop`, 13 native WPF screens + WinForms `NotifyIcon`) + Windows Service Agent (`ServerForge.Agent`, loopback/VPN mTLS) + SQLite EF Core + Inno Setup 6 installers.
- **Latest Verified Windows CI Run #10 (`44720053575` @ commit `5226d810789413e01775c9e9f4e0348d080e6b61`):**
  - Native `.NET 10` solution build + sample app build: **PASS**
  - Automated xUnit tests: **42 / 42 PASS** (`27` Unit, `14` Agent, `1` Integration; `0` skipped), including server-side GitHub provenance rejection tests
  - Desktop & Agent self-contained publishing + Inno Setup 6 installers + SHA-256 verification: **PASS**
  - `smoke-test-windows-installers`: **50 / 50 PASS**
  - Both Windows CI jobs: **GREEN**

## Phase 12.2 Autonomous Polling, Strict Provenance & Safe Entry Point (Pending CI Run #11)
1. **Decoupled ETag vs Durable Artifact-Readiness State (`GitHubAutoDeployWorker.cs` + SQLite Schema v3):**
   - Added `AutonomousCandidateState`, `AutonomousDeploymentCandidate` entity, `AutonomousDeploymentCandidates` SQLite table (schema v3 with automatic pre-upgrade `.bak` backup on v0/v1/v2 → v3 migrations), and `ServerForgeStore` persistence + restart recovery (`RecoverInterruptedAutonomousCandidatesAsync`).
   - Unresolved commits now continue polling GitHub Actions workflow/artifact readiness even when all subsequent branch-head checks return **HTTP 304 Not Modified**, survive Agent restarts, allow newer commits to supersede older pending commits, apply bounded backoff + respect `Retry-After` / `X-RateLimit-Reset`, prevent duplicate/in-flight/endless-failed retries, keep artifact provenance mandatory, and scrub credentials from logs/status.
   - Added read-only mTLS endpoint `GET /api/v1/applications/{id}/autonomous-status`.
2. **Strict Workflow & Artifact Identity, Safe Redirects & Archive Hardening (`GitHubApiClient.cs`):**
   - Enforces exact authorized `owner/repo`, exact branch + 40-char commit SHA, and exact non-blank workflow path — explicitly resolving `GET repos/{repo}/actions/workflows/{workflow_id}` when `path` is absent from workflow run list responses (never silently accepting missing `path` or suffix spoofing).
   - Classifies `WorkflowNotStarted`, `WorkflowRunning`, `WorkflowSucceededArtifactPending`, `WorkflowFailedOrCancelled`, `RateLimited`, `TransientApiError`, and `ProvenanceRejected`.
   - Strict artifact selection (`SelectAuthorizedArtifact`) fails closed on ambiguous multi-artifact runs instead of blindly picking the first artifact.
   - Configured `AllowAutoRedirect = false` on `IGitHubClient`; validates HTTPS redirect hosts (`*.githubusercontent.com`, `*.blob.core.windows.net`) while stripping `Authorization: Bearer <PAT>` on redirects; enforces `512 MB` compressed / `2 GB` uncompressed size caps, `Content-Length` truncation retries, SHA-256 verification **before** ZIP parsing, and `ValidateDownloadedZipArchive` (blocks corrupt ZIPs, empty ZIPs, Zip Slip, and zip bombs).
3. **Corrected Runbook & Single Safe Disposable IIS Entry Point:**
   - Added `ServerForge/scripts/e2e/run-disposable-iis-acceptance.ps1` (single PowerShell 5.1/7 orchestration entry point requiring `-AgentInstallerPath`, `-ConfirmDisposableHost`, and exact marker `SERVERFORGE_DISPOSABLE_E2E_HOST=true`, with guaranteed owned-only cleanup in `finally`).
   - Updated `DISPOSABLE_IIS_TEST_GUIDE.md` so every documented manual script invocation includes all mandatory parameters (`-RunId`, `-AgentInstallerPath`, `-ManifestPath`, `-GitHubRepo`, `-WorkspaceRoot`) and exact marker value `SERVERFORGE_DISPOSABLE_E2E_HOST=true`.
4. **Expanded Automated xUnit Suite (`42 → 48` tests, `0` skipped):**
   - `+3` Unit tests (`27 → 30`): workflow path ID resolution/rejection, strict artifact selection/size/ZIP/redirect hardening, and run/rate-limit state classification (+ legacy v0→v3 migration assertion).
   - `+3` Agent tests (`14 → 17`): HTTP 304 artifact-readiness continuation, Agent restart candidate persistence/resume under HTTP 304, and commit superseding + failed-workflow terminal stop + rate-limit backoff.

## Remaining External Gates (Must Stay Honestly Reported)
1. **Windows CI Run #11:** Push Phase 12.2 commit and verify `48/48` xUnit + `50/50` smoke checks pass.
2. **TEST A on Disposable Windows IIS VM:** `BLOCKED` until executed on a real disposable Windows Server IIS VM.
3. **TEST B on Disposable Windows IIS VM + Disposable GitHub Repo:** `BLOCKED` until executed with a real disposable GitHub sample repo.
4. **Interactive Windows 11 WPF Desktop Acceptance:** `NOT VERIFIED` until completed on Windows 11 (`WINDOWS_DESKTOP_ACCEPTANCE.md`).
