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
- **Run #11 (`44739965264`)** — `1ae32e9b4224373c8cc568aa965ab3b3954cac6f`: **FAILED AT C# TEST COMPILATION (19 ERRORS)** — No Phase 12.2 tests executed and no new installers were generated.
  - `3 × CS0246` (`AutonomousDeploymentAndRollbackTests.cs` lines 849, 993, 1109): referenced nonexistent `LoopbackSampleIisHost` instead of existing `LiveLoopbackTargetHost.StartAsync()`.
  - `4 × CS0103` + `8 × CS8130` (`AutonomousDeploymentAndRollbackTests.cs` lines 854, 998, 1114, 1115): called nonexistent `CreateSampleAspNetCoreArtifactAsync(..., unhealthy: false)` instead of existing `CreateAspNetCoreSampleArtifactAsync(..., commitSha, bakeUnhealthyWebConfig: false)`, cascading into 8 tuple-deconstruction inference errors.
  - `3 × CS1061` (`AutonomousDeploymentAndRollbackTests.cs` lines 869, 1013, 1130): called nonexistent `AgentIdentityStore.UpdateState(...)` instead of existing `AgentIdentityStore.StoreProtectedGitHubPat(...)`.
  - `1 × CS9007` (`ServerForgeUnitTests.cs` line 1491, col 236): `$$"""..."""` interpolated raw JSON literal contained literal consecutive `}}` closing braces after `"repository":{"full_name":"owner/repo"}`.

## Phase 12.2 + Run #12 Repair Completed on Disk — Awaiting Windows CI Run #12 & Disposable IIS VM Execution
1. **Run #12 Exact Test Compilation Repairs (All 19 Errors Fixed)**:
   - `AutonomousDeploymentAndRollbackTests.cs`: switched all 3 Phase 12.2 Agent tests to `await LiveLoopbackTargetHost.StartAsync()`, `CreateAspNetCoreSampleArtifactAsync(..., CommitV*, bakeUnhealthyWebConfig: false)`, `TestDbContextFactory`, and `identityStore.StoreProtectedGitHubPat(protector.Protect(..., "agent:github-pat"))`; verified DPAPI PAT survival across a reloaded `new AgentIdentityStore(agentOpts)` in the restart test; verified `targetHost.ActivePhysicalPath` and live HTTP `commitSha` responses.
   - `ServerForgeUnitTests.cs`: added `using System.Text.Json;` and replaced all raw interpolated JSON payloads in the 3 Phase 12.2 `GitHubApiClient` unit tests with strongly-structured `JsonSerializer.Serialize(...)` calls.
2. **Priority 1 — Autonomous GitHub Polling ETag / Artifact-Readiness Fix (`GitHubAutoDeployWorker.cs` + SQLite Schema v3)**:
   - Decoupled conditional branch-head ETag tracking (`LastBranchHeadETag`) from durable per-application workflow/artifact readiness state stored in SQLite (`AutonomousDeploymentCandidates` table, `SqliteSchemaMigrator.LatestSchemaVersion = 3` with automatic pre-upgrade `.bak` backup on v0/v1/v2 → v3 migrations).
   - Pending commits detected before GitHub Actions finishes keep checking workflow/artifact readiness even under **HTTP 304 Not Modified**, survive Agent restarts (`RecoverInterruptedAutonomousCandidatesAsync`), allow newer commits to supersede older pending commits, apply bounded backoff + rate-limit pauses (`Retry-After` / `X-RateLimit-Reset`), prevent duplicate/in-flight/endless-failed retries, keep artifact provenance mandatory, scrub credentials from logs/status, and expose `GET /api/v1/applications/{id}/autonomous-status`.
3. **Priority 2 — Strict Workflow & Artifact Identity, Safe Redirects & Archive Hardening (`GitHubApiClient.cs`)**:
   - Resolves missing run `path` via `GET repos/{repo}/actions/workflows/{workflow_id}`, enforces exact repo/branch/40-char SHA/workflow path, classifies run/artifact/rate-limit states explicitly, fails closed on ambiguous multi-artifact runs, strips `Authorization: Bearer <PAT>` across validated HTTPS redirects (`AllowAutoRedirect = false`), enforces `512 MB` compressed / `2 GB` uncompressed size caps + `Content-Length` truncation retries, verifies SHA-256 **before** ZIP parsing, and runs `ValidateDownloadedZipArchive`.
4. **48 Enabled Automated xUnit Tests (`42 -> 48`, `0` Skipped)** & **Single Safe Disposable IIS Entry Point (`run-disposable-iis-acceptance.ps1`)**:
   - `30` Unit + `17` Agent + `1` Integration = `48` total tests ready for Run #12.
   - Left `/.github/workflows/build-windows-installers.yml` and `/.github/workflows/e2e-disposable-iis.yml` untouched to preserve the verified Run #10 green pipeline.

## Signing & Environment Transparency
- **Installers are UNSIGNED**: No Authenticode certificate is configured in CI; verify `SHA256SUMS.txt` or sign with an operator-owned certificate before distribution.
- **Google AI Studio Container**: Linux x64 (`gVisor`) without `.NET 10` Windows SDK, IIS, `ISCC.exe`, or GitHub push credentials. Push to `Zohaib-Jadoon/SERVER-FORGE` to trigger **Windows CI Run #12**, then execute TEST A and TEST B on a disposable Windows Server VM with IIS + `.NET 10` Hosting Bundle (`DISPOSABLE_IIS_TEST_GUIDE.md`).

## Next Steps to Trigger Windows CI Run #12
```powershell
git add ServerForge/tests/ServerForge.AgentTests/AutonomousDeploymentAndRollbackTests.cs `
        ServerForge/tests/ServerForge.UnitTests/ServerForgeUnitTests.cs `
        ServerForge/MEMORY.md ServerForge/BUILD_REPORT.md `
        ServerForge/TEST_REPORT.md ServerForge/DEVELOPMENT_STATUS.md `
        ServerForge/PRODUCTION_READINESS_REPORT.md `
        ServerForge/E2E_ACCEPTANCE_REPORT.md ServerForge/INSTALLATION.md
git commit -m "Run #12: fix 19 Phase 12.2 test compilation errors (LiveLoopbackTargetHost, artifact helper, StoreProtectedGitHubPat, JsonSerializer)"
git push origin main
```
