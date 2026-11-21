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

## Phase 12.2 Autonomous Polling, Strict Provenance & Run #13 Test-Mock Repair (Pending CI Run #13)
- **Windows CI Run #11 (`44739965264` @ `1ae32e9b4224373c8cc568aa965ab3b3954cac6f`):** FAILED during C# compilation with **19 errors** in the newly added Phase 12.2 tests — all 19 fixed prior to Run #12.
- **Windows CI Run #12 (`44745867909`):**
  - Native `.NET 10` Windows compilation: **PASSED (0 errors)**!
  - Automated xUnit tests: **47 / 48 PASSED** (all `17/17` Agent tests including all 3 new autonomous HTTP 304 / restart / superseding / rate-limit tests, `1/1` Integration, `29/30` Unit).
  - **Exact Single Failure:** `GitHubApiClient_ResolvesMissingWorkflowPathByWorkflowId_AndRejectsUnmatchedOrMissingWorkflowIdentity` (`ServerForgeUnitTests.cs:1562`, `Assert.True(resolvedRes.Found)` was `false`). Root cause: the HTTP mock used `uri.Contains("/artifacts")`, which also matched `GET /repos/owner/repo/actions/artifacts/556/zip` and returned artifact-list JSON bytes instead of `validZipBytes`, so real SHA-256 verification rightly rejected it. Installer packaging and smoke tests were skipped.
- **Run #13 Mock-Routing Fix Completed on Disk (Zero Production Weakening):**
  - Updated `GitHubApiClient_ResolvesMissingWorkflowPathByWorkflowId_AndRejectsUnmatchedOrMissingWorkflowIdentity` in `ServerForge/tests/ServerForge.UnitTests/ServerForgeUnitTests.cs` to route by exact `RequestUri.AbsolutePath`:
    1. `GET /repos/owner/repo/actions/workflows/555` -> authorized workflow path `.github/workflows/serverforge-deploy.yml`
    2. `GET /repos/owner/repo/actions/runs/911/artifacts` -> artifact metadata JSON
    3. `GET /repos/owner/repo/actions/artifacts/556/zip` -> `validZipBytes`
  - Added endpoint-invocation assertions (`resolvedWorkflowEndpointCalled`, `artifactListEndpointCalled`, `artifactDownloadEndpointCalled`) and detailed failure diagnostics (`ResolutionStatus`, `ErrorMessage`) plus `ComputedSha256` verification. Zero production files (`GitHubApiClient.cs`, etc.) modified.
1. **Decoupled ETag vs Durable Artifact-Readiness State (`GitHubAutoDeployWorker.cs` + SQLite Schema v3):**
   - Verified in Run #12 (`17/17` Agent tests PASS): `AutonomousDeploymentCandidates` SQLite table (schema v3 with automatic pre-upgrade `.bak` backup), HTTP 304 continuation, Agent restart recovery (`RecoverInterruptedAutonomousCandidatesAsync`), commit superseding, bounded backoff + `Retry-After` / `X-RateLimit-Reset`, duplicate/in-flight/endless-failed retry guards, credential scrubbing, and `GET /api/v1/applications/{id}/autonomous-status`.
2. **Strict Workflow & Artifact Identity, Safe Redirects & Archive Hardening (`GitHubApiClient.cs`):**
   - Enforces exact authorized `owner/repo`, exact branch + 40-char commit SHA, and exact non-blank workflow path — resolving `GET repos/{repo}/actions/workflows/{workflow_id}` when `path` is absent.
   - Classifies run/artifact/rate-limit states, fails closed on ambiguous multi-artifact runs, strips `Authorization: Bearer <PAT>` across validated HTTPS redirects (`AllowAutoRedirect = false`), enforces `512 MB` compressed / `2 GB` uncompressed caps + `Content-Length` truncation retries, verifies SHA-256 **before** ZIP parsing, and validates ZIP archive integrity.
3. **Corrected Runbook & Single Safe Disposable IIS Entry Point:**
   - Preserved `ServerForge/scripts/e2e/run-disposable-iis-acceptance.ps1` (mandatory `-AgentInstallerPath`, `-ConfirmDisposableHost`, exact marker `SERVERFORGE_DISPOSABLE_E2E_HOST=true`, guaranteed owned-only cleanup).
4. **Expanded Automated xUnit Suite (`42 → 48` tests, `0` skipped):**
   - `30` Unit + `17` Agent + `1` Integration = `48` total enabled tests (`47/48` already passed in Run #12; fixed final unit test mock pending **Windows CI Run #13**).

## Remaining External Gates (Must Stay Honestly Reported)
1. **Windows CI Run #13:** Push Run #13 mock-routing fix and verify `48/48` xUnit + installers + `50/50` smoke checks pass.
2. **TEST A on Disposable Windows IIS VM:** `BLOCKED` until executed on a real disposable Windows Server IIS VM.
3. **TEST B on Disposable Windows IIS VM + Disposable GitHub Repo:** `BLOCKED` until executed with a real disposable GitHub sample repo.
4. **Interactive Windows 11 WPF Desktop Acceptance:** `NOT VERIFIED` until completed on Windows 11 (`WINDOWS_DESKTOP_ACCEPTANCE.md`).
