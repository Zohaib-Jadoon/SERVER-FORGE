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
- **Run #11 (`44739965264`)** — `1ae32e9b4224373c8cc568aa965ab3b3954cac6f`: **FAILED AT C# TEST COMPILATION (19 ERRORS)** — fixed prior to Run #12.
- **Run #12 (`44745867909`)**: **COMPILE PASSED (0 ERRORS), 47 / 48 xUnit TESTS PASSED, 1 UNIT TEST FAILED (INSTALLERS & SMOKE SKIPPED)**
  - Native `.NET 10` Windows compilation (`ServerForge.sln` + `ServerForge.SampleApp.csproj`): **PASSED (0 errors)**.
  - Automated xUnit suite: **47 / 48 PASSED** (all `17/17` Agent tests including all 3 new Phase 12.2 autonomous polling tests, `1/1` Integration, `29/30` Unit).
  - Exact single failure: `GitHubApiClient_ResolvesMissingWorkflowPathByWorkflowId_AndRejectsUnmatchedOrMissingWorkflowIdentity` (`ServerForgeUnitTests.cs:1562`, `Assert.True(resolvedRes.Found)`, actual `false`).
  - Root cause: The test HTTP mock used `if (uri.Contains("/artifacts"))`, which also matched `GET /repos/owner/repo/actions/artifacts/556/zip`, causing the mock to return artifact-list JSON bytes instead of `validZipBytes` so `GitHubApiClient`'s genuine SHA-256 verification rejected the download.

## Run #13 Exact Mock-Routing Fix Completed on Disk — Awaiting Windows CI Run #13 & Disposable IIS VM Execution
1. **Exact Test Mock Repair (`ServerForge/tests/ServerForge.UnitTests/ServerForgeUnitTests.cs`) — Zero Production Files Modified**:
   - Replaced `uri.Contains("/artifacts")` in `GitHubApiClient_ResolvesMissingWorkflowPathByWorkflowId_AndRejectsUnmatchedOrMissingWorkflowIdentity` with exact `req.RequestUri.AbsolutePath` routing:
     - `GET /repos/owner/repo/actions/workflows/555` -> returns authorized workflow path `.github/workflows/serverforge-deploy.yml`
     - `GET /repos/owner/repo/actions/runs/911/artifacts` -> returns artifact metadata JSON
     - `GET /repos/owner/repo/actions/artifacts/556/zip` -> returns `validZipBytes`
   - Added endpoint invocation assertions and `ResolutionStatus` + `ErrorMessage` assertion diagnostics plus `ComputedSha256` verification.
2. **Phase 12.2 Verified Production Changes (Compiled with 0 Errors & 47/48 Passing in Run #12)**:
   - Decoupled autonomous polling ETag vs durable SQLite schema v3 candidate state (`GitHubAutoDeployWorker.cs` + `AutonomousDeploymentCandidates` — all `17/17` Agent tests PASSED in Run #12).
   - Strict workflow & artifact identity, safe redirects, size caps, and archive integrity hardening (`GitHubApiClient.cs` — untouched; all production provenance checks preserved verbatim).
   - `48` enabled xUnit tests (`30` Unit + `17` Agent + `1` Integration; `0` skipped) ready for Run #13.
   - Left `/.github/workflows/build-windows-installers.yml` and `/.github/workflows/e2e-disposable-iis.yml` untouched to preserve the verified Run #10 green pipeline.

## Signing & Environment Transparency
- **Installers are UNSIGNED**: No Authenticode certificate is configured in CI; verify `SHA256SUMS.txt` or sign with an operator-owned certificate before distribution.
- **Google AI Studio Container**: Linux x64 (`gVisor`) without `.NET 10` Windows SDK, IIS, `ISCC.exe`, or GitHub push credentials. Push to `Zohaib-Jadoon/SERVER-FORGE` to trigger **Windows CI Run #13**, then execute TEST A and TEST B on a disposable Windows Server VM with IIS + `.NET 10` Hosting Bundle (`DISPOSABLE_IIS_TEST_GUIDE.md`).

## Next Steps to Trigger Windows CI Run #13
```powershell
git add ServerForge/tests/ServerForge.UnitTests/ServerForgeUnitTests.cs `
        ServerForge/MEMORY.md ServerForge/BUILD_REPORT.md `
        ServerForge/TEST_REPORT.md ServerForge/DEVELOPMENT_STATUS.md `
        ServerForge/PRODUCTION_READINESS_REPORT.md `
        ServerForge/E2E_ACCEPTANCE_REPORT.md `
        ServerForge/WINDOWS_DESKTOP_ACCEPTANCE.md
git commit -m "Run #13: fix artifact ZIP mock routing in workflow_id resolution unit test"
git push origin main
```
