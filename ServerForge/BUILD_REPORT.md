# ServerForge Build Report

## Verified Windows CI Run History
- **Run #4 (`44684923525`)** — `main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`: **PASSED**
  - `.NET 10` compilation: PASSED, zero NuGet advisories, `24/24` xUnit tests PASSED.
  - Unsigned self-contained `win-x64` installers generated & uploaded (`ServerForge-Desktop-Setup.exe` ~62.92 MB, `ServerForge-Agent-Setup.exe` ~46.64 MB, `SHA256SUMS.txt`).
- **Run #5 (`44688053844`)** — `d3a7a7d6d11e4c1a355e31a8f2995c28104bd859`: **FAILED AT COMPILE (82 errors in `ServerForge.AgentTests`)** — fixed for Run #6.
- **Run #6 (`44689338386`)** — `4ad417c383ae524ece90861a77e10e92f03c56f3`: **COMPILE PASSED, 30/32 xUnit TESTS PASSED** (2 `SQLite Error 14` fixture failures — fixed for Run #7).
- **Run #7 (`44690875542`)** — `main` @ `0aef82a8ed92c47a2b03663912026e66b8545a92`: Job 1 (`32/32` tests + installers) GREEN; Job 2 failed at Desktop uninstall relative `/LOG=` (fixed for Run #8).
- **Run #8 (`44699230392`)** — `main` @ `2207141082e1e8024c0ff2036995d81a63ec1caa`: **100% GREEN ACROSS BOTH WINDOWS CI JOBS** (`32/32` xUnit tests, installers, `50/50` smoke assertions).
- **Run #9 (`44715138404`)** — `main` @ `b97cdfd56c5605a598620fee9683ac5ea56ea6f2`: **100% GREEN ACROSS BOTH WINDOWS CI JOBS** (`38/38` xUnit tests, installers, `50/50` smoke assertions).
- **Run #10 (`44720053575`)** — `main` @ `5226d810789413e01775c9e9f4e0348d080e6b61`: **100% GREEN ACROSS BOTH WINDOWS CI JOBS (VERIFIED PHASE 12.1 BASELINE)**
  - Native `.NET 10` Windows compilation (`ServerForge.sln` + `ServerForge.SampleApp.csproj`): **PASSED** (0 errors, 0 NuGet advisories).
  - Automated xUnit test suite: **42 / 42 PASSED** (`27/27` Unit, `14/14` Agent including live server-side GitHub provenance rejection tests, `1/1` Integration).
  - Self-contained `win-x64` publishing & Inno Setup 6 compilation: **PASSED** (`ServerForge-Desktop-Setup.exe`, `ServerForge-Agent-Setup.exe`, `SHA256SUMS.txt` verified & uploaded).
  - `smoke-test-windows-installers`: **50 PASSED, 0 FAILED** — Desktop install/upgrade/uninstall, Agent Windows Service install/restart/upgrade/uninstall, identity/cert persistence, loopback-only management endpoint (`127.0.0.1:9443`), and hosted-file preservation all verified on real Windows.

## Phase 12.2 Deliverables Added After Run #10 (`44720053575`) — Awaiting Windows CI Run #11 & Disposable IIS VM Execution
1. **Priority 1 — Autonomous GitHub Polling ETag / Artifact-Readiness Fix (`GitHubAutoDeployWorker.cs` + SQLite Schema v3)**:
   - Decoupled conditional branch-head ETag tracking (`LastBranchHeadETag`) from durable per-application workflow/artifact readiness state stored in SQLite (`AutonomousDeploymentCandidates` table, `SqliteSchemaMigrator.LatestSchemaVersion = 3` with automatic pre-upgrade `.bak` backup on v0/v1/v2 → v3 migrations).
   - Pending commits detected before GitHub Actions finishes now keep checking workflow/artifact readiness even when all subsequent branch-head requests return **HTTP 304 Not Modified**, survive Agent restarts (`RecoverInterruptedAutonomousCandidatesAsync`), allow newer commits to supersede older pending commits, apply bounded backoff + rate-limit pauses (`Retry-After` / `X-RateLimit-Reset`), prevent duplicate/in-flight/endless-failed retries, keep artifact provenance mandatory, scrub credentials from logs/status, and expose read-only mTLS endpoint `GET /api/v1/applications/{id}/autonomous-status`.
2. **Priority 2 — Strict Workflow & Artifact Identity, Safe Redirects & Archive Hardening (`GitHubApiClient.cs`)**:
   - Enforces exact authorized `owner/repo`, exact branch + 40-char commit SHA, and exact non-blank workflow path — explicitly calling `GET repos/{repo}/actions/workflows/{workflow_id}` when `path` is absent from workflow run list responses (never silently accepting missing `path` or suffix spoofing).
   - Explicitly classifies `WorkflowNotStarted`, `WorkflowRunning`, `WorkflowSucceededArtifactPending`, `WorkflowFailedOrCancelled`, `RateLimited`, `TransientApiError`, and `ProvenanceRejected`.
   - Enforces strict artifact selection (`SelectAuthorizedArtifact`) that fails closed on ambiguous multi-artifact runs instead of blindly selecting the first artifact.
   - Configures `AllowAutoRedirect = false` on `IGitHubClient`, validates HTTPS redirects (`*.githubusercontent.com`, `*.blob.core.windows.net`) while stripping `Authorization: Bearer <PAT>` on redirects, enforces `512 MB` compressed / `2 GB` uncompressed size caps, retries truncated `Content-Length` downloads cleanly, verifies SHA-256 **before** ZIP parsing, and runs `ValidateDownloadedZipArchive` (rejects corrupt ZIPs, empty ZIPs, Zip Slip, and zip bombs).
3. **6 New Phase 12.2 Regression xUnit Tests (`42 -> 48` Total, `0` Skipped)**:
   - Added `+3` Unit tests in `ServerForgeUnitTests.cs` (`27 -> 30`) and `+3` Agent polling tests in `AutonomousDeploymentAndRollbackTests.cs` (`14 -> 17` Agent tests; `1` Integration test).
4. **Priority 3 — Corrected Disposable IIS Runbook & Single Safe Entry Point**:
   - Added `ServerForge/scripts/e2e/run-disposable-iis-acceptance.ps1` requiring `-AgentInstallerPath`, `-ConfirmDisposableHost`, and exact marker `SERVERFORGE_DISPOSABLE_E2E_HOST=true`, executing setup, TEST A, rollback verification, optional `-IncludeTestB`, and guaranteed owned-only cleanup in `finally`.
   - Updated `DISPOSABLE_IIS_TEST_GUIDE.md` so all manual commands pass every exact mandatory script parameter and use `SERVERFORGE_DISPOSABLE_E2E_HOST=true`.
   - Left `/.github/workflows/build-windows-installers.yml` and `/.github/workflows/e2e-disposable-iis.yml` untouched to preserve the verified Run #10 green pipeline.

## Signing & Environment Transparency
- **Installers are UNSIGNED**: No Authenticode certificate is configured in CI; verify `SHA256SUMS.txt` or sign with an operator-owned certificate before distribution.
- **Google AI Studio Container**: Linux x64 (`gVisor`) without `.NET 10` Windows SDK, IIS, `ISCC.exe`, or GitHub push credentials. Push to `Zohaib-Jadoon/SERVER-FORGE` to trigger Windows CI Run #11, then execute TEST A and TEST B on a disposable Windows Server VM with IIS + `.NET 10` Hosting Bundle (`DISPOSABLE_IIS_TEST_GUIDE.md`).

## Next Steps to Trigger Windows CI Run #11
```powershell
git checkout -b feat/phase12-2-autonomous-polling-and-strict-provenance
# Extract updated archive into repository root, preserving /.github/workflows/
git add ServerForge/src/ `
        ServerForge/tests/ `
        ServerForge/scripts/e2e/run-disposable-iis-acceptance.ps1 `
        ServerForge/DISPOSABLE_IIS_TEST_GUIDE.md `
        ServerForge/E2E_ACCEPTANCE_REPORT.md `
        ServerForge/WINDOWS_DESKTOP_ACCEPTANCE.md `
        ServerForge/INSTALLATION.md ServerForge/MEMORY.md `
        ServerForge/BUILD_REPORT.md ServerForge/TEST_REPORT.md `
        ServerForge/DEVELOPMENT_STATUS.md ServerForge/PRODUCTION_READINESS_REPORT.md
git commit -m "Phase 12.2: fix autonomous GitHub ETag/artifact polling, harden workflow/artifact identity, add single E2E entry point"
git push -u origin feat/phase12-2-autonomous-polling-and-strict-provenance
```
