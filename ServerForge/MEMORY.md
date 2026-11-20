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

## Phase 12.2 Autonomous Polling, Strict Provenance & Run #12 Compile Repair (Pending CI Run #12)
- **Windows CI Run #11 (`44739965264` @ `1ae32e9b4224373c8cc568aa965ab3b3954cac6f`):** FAILED during C# compilation with **19 errors** in the newly added Phase 12.2 tests (`3 × CS0246` `LoopbackSampleIisHost`, `4 × CS0103` `CreateSampleAspNetCoreArtifactAsync` + `8 × CS8130` tuple deconstruction cascades, `3 × CS1061` `AgentIdentityStore.UpdateState`, and `1 × CS9007` interpolated raw JSON string literal at `ServerForgeUnitTests.cs:1491`).
- **Run #12 Compiler Fixes Completed on Disk:**
  - Replaced `LoopbackSampleIisHost` with existing `await LiveLoopbackTargetHost.StartAsync()` and asserted `targetHost.ActivePhysicalPath` across all 3 new Agent polling tests.
  - Replaced `CreateSampleAspNetCoreArtifactAsync(..., unhealthy: false)` with existing `CreateAspNetCoreSampleArtifactAsync(..., CommitV*, bakeUnhealthyWebConfig: false)` using 40-char commit constants (`CommitV1`, `CommitV2`, `CommitV4Recovery`).
  - Replaced `identityStore.UpdateState(...)` with existing `identityStore.StoreProtectedGitHubPat(protector.Protect(..., "agent:github-pat"))` and verified credential reload via a fresh `new AgentIdentityStore(agentOpts)` in the restart-persistence test.
  - Replaced malformed `$$"""..."""` raw JSON strings in all 3 Phase 12.2 unit tests with strongly-structured `JsonSerializer.Serialize(...)` (with `using System.Text.Json;`).
1. **Decoupled ETag vs Durable Artifact-Readiness State (`GitHubAutoDeployWorker.cs` + SQLite Schema v3):**
   - Added `AutonomousCandidateState`, `AutonomousDeploymentCandidate` entity, `AutonomousDeploymentCandidates` SQLite table (schema v3 with automatic pre-upgrade `.bak` backup on v0/v1/v2 → v3 migrations), and `ServerForgeStore` persistence + restart recovery (`RecoverInterruptedAutonomousCandidatesAsync`).
   - Unresolved commits continue polling GitHub Actions workflow/artifact readiness even when all subsequent branch-head checks return **HTTP 304 Not Modified**, survive Agent restarts, allow newer commits to supersede older pending commits, apply bounded backoff + respect `Retry-After` / `X-RateLimit-Reset`, prevent duplicate/in-flight/endless-failed retries, keep artifact provenance mandatory, and scrub credentials from logs/status.
   - Added read-only mTLS endpoint `GET /api/v1/applications/{id}/autonomous-status`.
2. **Strict Workflow & Artifact Identity, Safe Redirects & Archive Hardening (`GitHubApiClient.cs`):**
   - Enforces exact authorized `owner/repo`, exact branch + 40-char commit SHA, and exact non-blank workflow path — explicitly resolving `GET repos/{repo}/actions/workflows/{workflow_id}` when `path` is absent from workflow run list responses.
   - Classifies `WorkflowNotStarted`, `WorkflowRunning`, `WorkflowSucceededArtifactPending`, `WorkflowFailedOrCancelled`, `RateLimited`, `TransientApiError`, and `ProvenanceRejected`; fails closed on ambiguous multi-artifact runs; strips `Authorization: Bearer <PAT>` across validated HTTPS redirects (`AllowAutoRedirect = false`); enforces `512 MB` compressed / `2 GB` uncompressed caps + `Content-Length` truncation retries; verifies SHA-256 **before** ZIP parsing and validates ZIP archive integrity.
3. **Corrected Runbook & Single Safe Disposable IIS Entry Point:**
   - Preserved `ServerForge/scripts/e2e/run-disposable-iis-acceptance.ps1` (mandatory `-AgentInstallerPath`, `-ConfirmDisposableHost`, exact marker `SERVERFORGE_DISPOSABLE_E2E_HOST=true`, guaranteed owned-only cleanup).
4. **Expanded Automated xUnit Suite (`42 → 48` tests, `0` skipped):**
   - `30` Unit + `17` Agent + `1` Integration = `48` total enabled tests, pending Windows CI Run #12.

## Remaining External Gates (Must Stay Honestly Reported)
1. **Windows CI Run #12:** Push Run #12 test-compile fixes and verify `48/48` xUnit + installers + `50/50` smoke checks pass.
2. **TEST A on Disposable Windows IIS VM:** `BLOCKED` until executed on a real disposable Windows Server IIS VM.
3. **TEST B on Disposable Windows IIS VM + Disposable GitHub Repo:** `BLOCKED` until executed with a real disposable GitHub sample repo.
4. **Interactive Windows 11 WPF Desktop Acceptance:** `NOT VERIFIED` until completed on Windows 11 (`WINDOWS_DESKTOP_ACCEPTANCE.md`).
