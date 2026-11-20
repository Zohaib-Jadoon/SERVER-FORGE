# ServerForge Development Status

## Verified GitHub Actions Milestones
- **Run #1 (`44678399756`)** (`main` @ `f2261702b0f4dfd8a0a1bb6d4d0c20aa056cd732`): Confirmed root workflow `/.github/workflows/build-windows-installers.yml` triggers on `windows-latest`; surfaced WPF `CS0104` and NuGet advisories.
- **Run #2 (`44679766730`)** (`main` @ `21da7f890ab7dddd0452adcadf282468df6c73da`): `.NET 10` compilation PASSED, NuGet advisories RESOLVED, `17/18` xUnit tests passed; surfaced SQLite FK `ProjectGroupId` test failure.
- **Run #3 (`44681873469`)** (`main` @ `e4ab9076b67ca039279b6b71d10408fd503e1d0e`): `.NET 10` compilation PASSED, SQLite FK PASSED, `19/20` xUnit tests passed; surfaced SQLite `DateTimeOffset` `ORDER BY` failure.
- **Run #4 (`44684923525`)** (`main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`): **FULL GREEN BUILD & INSTALLER PACKAGING** — `24/24` xUnit tests PASSED; unsigned self-contained `win-x64` Desktop (`~62.92 MB`) & Agent (`~46.64 MB`) installers + `SHA256SUMS.txt` generated & uploaded.
- **Run #5 (`44688053844`)** (`d3a7a7d6d11e4c1a355e31a8f2995c28104bd859`): Failed at `ServerForge.AgentTests` compile (82 errors) — fixed for Run #6.
- **Run #6 (`44689338386`)** (`4ad417c383ae524ece90861a77e10e92f03c56f3`): Compile PASSED, `30/32` xUnit tests PASSED (2 `SQLite Error 14` fixture failures — fixed for Run #7).
- **Run #7 (`44690875542`)** (`main` @ `0aef82a8ed92c47a2b03663912026e66b8545a92`): Job 1 GREEN (`32/32` tests + installers); Job 2 failed at Desktop uninstall relative `/LOG=` (fixed for Run #8).
- **Run #8 (`44699230392`)** (`main` @ `2207141082e1e8024c0ff2036995d81a63ec1caa`): **100% GREEN ACROSS BOTH WINDOWS CI JOBS** (`32/32` xUnit tests, installers, `50/50` smoke assertions).
- **Run #9 (`44715138404`)** (`main` @ `b97cdfd56c5605a598620fee9683ac5ea56ea6f2`): **100% GREEN ACROSS BOTH WINDOWS CI JOBS (VERIFIED PHASE 12 BASELINE)**
  - Native `.NET 10` compilation (`ServerForge.sln` + `ServerForge.SampleApp.csproj`) PASS, **38 / 38** xUnit tests PASS (`25` Unit, `12` Agent, `1` Integration), both self-contained `win-x64` Inno Setup 6 installers built & SHA-256 verified, and `smoke-test-windows-installers` achieved **50 PASSED, 0 FAILED** across full Desktop & Agent Service lifecycles.

## Phase 12.1 Additions Completed on Disk (Pending CI Run #10 & Disposable IIS VM Execution)
- **Server-Side GitHub Artifact Provenance**: Added `VerifiedGitHubArtifact`, `IGitHubArtifactProvenanceVerifier`, `AgentGitHubProvenanceVerifier`, `GitHubApiClient.IsTrustedGitHubArtifactUrl`, and `EnqueueAndExecuteWithVerifiedArtifactAsync`; hardened `AgentHostFactory` (`/api/v1/deployments/trigger`) and `DeploymentOrchestrator` so `GitHubActionsArtifact` mode rejects caller-supplied local paths, untrusted URLs, forged run/artifact IDs, digest mismatches, and spoofed `TriggeredAutomatically = true` while preserving explicit `ManualSignedPackage` (`LOCAL TEST ARTIFACT`).
- **Security & Provenance Tests**: Added `+4` xUnit tests (`38 -> 42` total, `0` skipped) across `ServerForgeUnitTests.cs` (`25 -> 27`) and `AutonomousDeploymentAndRollbackTests.cs` (`12 -> 14`).
- **Hardened Optional E2E Workflow**: Hardened `/.github/workflows/e2e-disposable-iis.yml` (`workflow_dispatch`-only, all inputs passed via `env:`, strict regex/port/runner validation, `windows-latest` removed, `disposable-windows-iis-e2e` environment approval gate, positive disposable VM marker check). Left `/.github/workflows/build-windows-installers.yml` untouched.
- **Honest TEST A & TEST B Harness Split + Real-World Fixes**:
  - **TEST A** (`run-deployment-tests.ps1`, `verify-rollback.ps1`): Explicitly `ManualSignedPackage` (`LOCAL TEST ARTIFACT`), adds live negative provenance probe `TESTA.SEC.1`, on-disk `web.config` check, and Agent restart resilience.
  - **TEST B** (`run-github-autonomous-test.ps1`): Complete 18-step real GitHub Actions autonomous deploy/update/rollback/non-retry/recovery harness with Desktop closed and ZERO calls to `/api/v1/deployments/trigger`.
  - Fixed PowerShell 5.1 certificate pinning (`E2ECommon.psm1` `WebRequestHandler` fallback + `[string]::Empty` `X509Certificate2` overload) and fixed nested zip-inside-zip upload in `samples/disposable-test-app/.github/workflows/serverforge-deploy.yml` (flat `./publish-out/**` + `release-config.json`).

## 8-Stage Independent Verification Status (Never Collapsed Into One Percentage)

| Stage | Status | Evidence |
|---|---|---|
| 1. Source implementation completed | **COMPLETED** | All 11 `.NET 10` projects, 13 WPF screens, 42 xUnit tests, server-side provenance verifier, Inno scripts, 50-check installer smoke suite, TEST A + TEST B disposable IIS E2E harness, hardened optional E2E workflow, and sample app implemented. |
| 2. Windows CI compilation verified | **VERIFIED (Run #9 `44715138404`); RE-VERIFY RUN #10** | Native `.NET 10` WPF Desktop, Agent, SampleApp, and 38 tests compiled cleanly; Run #10 will verify Phase 12.1 provenance changes & 4 new tests (42 total). |
| 3. Installer packaging verified | **VERIFIED (Run #9 `44715138404`)** | Unsigned self-contained `win-x64` Desktop & Agent Inno Setup 6 installers + `SHA256SUMS.txt` generated, verified, and uploaded. |
| 4. Installer execution verified (install/service/upgrade/uninstall) | **VERIFIED IN GITHUB ACTIONS (Run #9 `44715138404`: 50/50 PASSED)** | Full Desktop & Agent Windows Service install, restart, upgrade, loopback-only binding, hosted-file preservation, and clean uninstall passed with 0 failures. |
| 5. Native desktop interactive GUI verified (Windows 11) | **NOT VERIFIED** | Requires manual interactive Windows 11 execution of all 13 screens (`ServerForge/WINDOWS_DESKTOP_ACCEPTANCE.md`); CI does NOT test interactive WPF rendering. |
| 6. Agent mTLS & IIS integration verified (**TEST A**) | **PARTIAL — LOOPBACK mTLS VERIFIED (Run #9); REAL IIS TEST A BLOCKED (Requires Disposable IIS VM)** | Live loopback Kestrel mTLS + provenance enforcement unit/Agent tests verified/pending Run #10; real IIS (`w3wp.exe`) TEST A execution requires disposable IIS VM (`DISPOSABLE_IIS_TEST_GUIDE.md`). |
| 7. Autonomous GitHub deployment verified (**TEST B**) | **PARTIAL — UNIT/WORKER TESTS VERIFIED (Run #9) + PROVENANCE TESTS PENDING RUN #10; REAL GITHUB-TO-IIS TEST B BLOCKED** | Real GitHub Actions autonomous push-to-IIS deployment while Desktop is closed requires disposable GitHub repo + `E2E_DISPOSABLE_GITHUB_PAT` + disposable Windows Server VM (`run-github-autonomous-test.ps1`). |
| 8. Rollback verified (TEST A & TEST B) | **PARTIAL — LOOPBACK & INTERRUPTED-RESTART TESTS VERIFIED (Run #9); REAL IIS ROLLBACK BLOCKED** | Real IIS `w3wp.exe` unhealthy HTTP 503 auto-rollback, previous `web.config` restoration, and Agent restart non-retry require executing TEST A (`verify-rollback.ps1`) and TEST B (`run-github-autonomous-test.ps1`) on a disposable IIS VM. |

## Current Project Verdict
**NOT PRODUCTION-READY YET.** Run #9 (`44715138404`) verified 100% green `.NET 10` build, `38/38` xUnit tests, installers, and `50/50` Windows installer/service smoke checks. Phase 12.1 server-side GitHub provenance hardening, `+4` security tests (`42` total), hardened optional E2E workflow, and honest TEST A / TEST B harnesses are complete on disk — pending **CI Run #10**, real disposable IIS **TEST A**, real GitHub autonomous **TEST B**, and interactive **Windows 11 WPF Desktop** acceptance.
