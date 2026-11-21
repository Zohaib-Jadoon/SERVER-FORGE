# ServerForge — End-to-End Acceptance Report (Phase 12.2 — Run #13 Mock Repair)

**Last Updated:** 2026-11-20  
**Last Verified Fully Green Windows CI Baseline:** Commit `5226d810789413e01775c9e9f4e0348d080e6b61` — GitHub Actions Run `44720053575` (**PASS**: `42/42` xUnit tests including server-side GitHub provenance rejection, Desktop & Agent installers built, SHA-256 verified, `50/50` installer smoke assertions)  
**Latest Windows CI Run #12:** Run `44745867909` (**COMPILE PASS, 47/48 xUnit PASS**: all `17/17` Agent tests passed; 1 unit test failed at `ServerForgeUnitTests.cs:1562` due to `uri.Contains("/artifacts")` mock collision with `/actions/artifacts/556/zip`; installers & smoke skipped)  
**Current Run #13 Working State:** Exact `AbsolutePath` HTTP mock routing fixed in `ServerForgeUnitTests.cs` (`48` enabled xUnit tests, `0` skipped; zero production changes), single safe disposable IIS entry point (`run-disposable-iis-acceptance.ps1`) verified — **Pending Windows CI Run #13**

---

## Independent Four-Gate Acceptance Status

| Gate | Scope | Current Status | Evidence / Blocker |
| --- | --- | --- | --- |
| **Gate 1 — Windows Build, Automated Tests & Installers** | Native `.NET 10` WPF + Agent build, xUnit suite, Inno Setup installers, SHA-256 checksums, 50/50 installer smoke tests | **VERIFIED AT RUN #10 (`42/42` + `50/50`); RUN #12 COMPILE PASS + `47/48` PASS — 1 MOCK FIX PENDING CI RUN #13** | Run `44720053575` GREEN; Run `44745867909` passed `.NET 10` compile + `47/48` tests; single mock-routing bug fixed on disk for Run #13 (`48` tests) |
| **Gate 2 — TEST A: Locally Built Sample Artifact on Actual Windows IIS** | Real IIS site/AppPool (`appcmd`, `w3wp.exe`, `AspNetCoreModuleV2`), `ManualSignedPackage` (`LOCAL TEST ARTIFACT`), v1 → v2 → HTTP 503 auto-rollback → Agent restart resilience | **BLOCKED — NO DISPOSABLE WINDOWS IIS VM ATTACHED** | Cannot execute IIS inside Linux container; never run against production IIS. Run `run-disposable-iis-acceptance.ps1` (or step scripts with exact required flags) on a disposable Windows Server IIS VM marked `SERVERFORGE_DISPOSABLE_E2E_HOST=true` |
| **Gate 3 — TEST B: Genuine Autonomous GitHub Actions Deployment & Rollback** | Dedicated disposable GitHub repo, root `.github/workflows/serverforge-deploy.yml`, test-only PAT, Desktop closed, ZERO `/api/v1/deployments/trigger` calls, real `GitHubAutoDeployWorker` v1 → v2 → unhealthy v3 rollback → non-retry after restart → v4 recovery | **BLOCKED — NO DISPOSABLE WINDOWS IIS VM / DISPOSABLE GITHUB REPO EXECUTED YET** | `run-disposable-iis-acceptance.ps1 -IncludeTestB` / `run-github-autonomous-test.ps1` exercises the Phase 12.2 HTTP 304 decoupled polling worker; awaiting execution on a disposable Windows IIS VM with a disposable test repo |
| **Gate 4 — Interactive Windows 11 WPF Desktop Acceptance** | Real Windows 11 GUI session across all 13 native WPF screens, setup wizard, pairing, backups, themes, close-to-tray, reopen, exit | **NOT VERIFIED** | Structured checklist ready in `ServerForge/WINDOWS_DESKTOP_ACCEPTANCE.md`; requires real Windows 11 interactive desktop testing |

**Production Readiness Determination:** **NOT PRODUCTION-READY** until Gates 1 (CI Run #13), 2 (TEST A), 3 (TEST B), and 4 (Windows 11 GUI) all record real passing evidence.

---

## Phase 12.2 Critical Fixes & Acceptance Improvements

### Priority 1 — Decoupled Autonomous GitHub ETag vs Durable Artifact-Readiness State
- Fixed the real-world polling deadlock in `GitHubAutoDeployWorker.cs` where caching a new branch HEAD ETag before GitHub Actions finished caused subsequent `HTTP 304 Not Modified` responses to skip artifact-readiness checks indefinitely.
- Introduced durable SQLite schema v3 (`AutonomousDeploymentCandidates` table, `SqliteSchemaMigrator.LatestSchemaVersion = 3` with automatic `.bak` pre-upgrade snapshots on v0/v1/v2 → v3 upgrades) storing per-application `CommitSha`, `Branch`, `LastBranchHeadETag`, `AutonomousCandidateState`, `WorkflowRunId`, `ArtifactId`, `ArtifactName`, `DeploymentId`, `AttemptCount`, `ConsecutiveTransientErrors`, `NextEligibleCheckUtc`, and scrubbed `LastStatusMessage`.
- Pending commits keep polling workflow/artifact readiness even when all subsequent branch-head requests return **HTTP 304 Not Modified**, survive Agent restarts (`RecoverInterruptedAutonomousCandidatesAsync` preserves pending artifact waits while marking interrupted `DeploymentRunning` candidates terminal), allow newer commits to supersede older pending commits, apply bounded backoff & respect `Retry-After`/`X-RateLimit-Reset`, prevent duplicate/in-flight/endless retries, and expose read-only mTLS endpoint `GET /api/v1/applications/{id}/autonomous-status`.

### Priority 2 — Strict Workflow & Artifact Identity, Safe Redirects & Archive Validation (`GitHubApiClient.cs`)
- Enforces exact authorized `owner/repo`, exact branch + 40-char commit SHA, and exact non-blank workflow path — explicitly resolving `GET repos/{repo}/actions/workflows/{workflow_id}` whenever `path` is omitted from workflow run list responses (never silently accepting missing `path` or suffix spoofing).
- Classifies `WorkflowNotStarted`, `WorkflowRunning`, `WorkflowSucceededArtifactPending`, `WorkflowFailedOrCancelled`, `RateLimited`, `TransientApiError`, and `ProvenanceRejected`.
- Strict artifact selection (`SelectAuthorizedArtifact`) fails closed on ambiguous multi-artifact runs instead of blindly selecting the first artifact.
- Configures `AllowAutoRedirect = false` on `IGitHubClient`, validates HTTPS redirects against `*.githubusercontent.com` / `*.blob.core.windows.net` while stripping `Authorization: Bearer <PAT>` on redirects, enforces `512 MB` compressed / `2 GB` uncompressed caps, handles truncated `Content-Length` downloads with bounded transient retries, verifies SHA-256 **before** ZIP parsing, and validates ZIP archive integrity (`ValidateDownloadedZipArchive` rejects corrupt ZIPs, empty ZIPs, Zip Slip `..`, and zip bombs).

### Priority 3 — Single Safe Disposable IIS Entry Point & Corrected Runbook
- Added `ServerForge/scripts/e2e/run-disposable-iis-acceptance.ps1` (PowerShell 5.1/7 compatible): requires `-AgentInstallerPath` and `-ConfirmDisposableHost`, enforces exact host marker `SERVERFORGE_DISPOSABLE_E2E_HOST=true`, executes `setup-disposable-iis.ps1` (including `preflight.ps1`), `run-deployment-tests.ps1`, `verify-rollback.ps1`, optional `-IncludeTestB` (`run-github-autonomous-test.ps1`), collects sanitized diagnostics on failure, and guarantees owned-only `cleanup-disposable-iis.ps1` in `finally`.
- Updated `ServerForge/DISPOSABLE_IIS_TEST_GUIDE.md` so every manual step command passes all exact mandatory parameters (`-RunId`, `-AgentInstallerPath`, `-ManifestPath`, `-GitHubRepo`, `-WorkspaceRoot`) and uses exact marker value `SERVERFORGE_DISPOSABLE_E2E_HOST=true`.

### Preserved Phase 12.1 Controls (Verified in CI Run #10 `44720053575`)
- **TEST A (`run-deployment-tests.ps1` + `verify-rollback.ps1`)** stays strictly `ManualSignedPackage` (`LOCAL TEST ARTIFACT`, `TriggeredAutomatically = false`, `gitHubLivePollTested = false`) with live negative provenance probe `TESTA.SEC.1`, on-disk `web.config` check, and Agent restart resilience.
- **TEST B (`run-github-autonomous-test.ps1`)** makes ZERO calls to `/api/v1/deployments/trigger` and drives real GitHub Actions runs (`v1 → v2 → v3-unhealthy → v4-recovery`) with Desktop closed.
- **Hardened Optional Workflow (`/.github/workflows/e2e-disposable-iis.yml`)** remains `workflow_dispatch`-only with zero `${{ inputs.* }}` shell interpolation, `disposable-windows-iis-e2e` environment approval, and `SERVERFORGE_DISPOSABLE_E2E_HOST=true` verification.

---

## Real IIS Execution Evidence Template (To Be Filled Only After Genuine Disposable VM Execution)

- **TEST A (`deployment-evidence.json` + `rollback-evidence.json`):** `BLOCKED — NOT YET EXECUTED ON DISPOSABLE WINDOWS IIS VM`
- **TEST B (`github-autonomous-evidence.json`):** `BLOCKED — NOT YET EXECUTED ON DISPOSABLE WINDOWS IIS VM`
- **Interactive Windows 11 Desktop Acceptance:** `NOT VERIFIED`
