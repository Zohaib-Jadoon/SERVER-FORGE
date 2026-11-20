# ServerForge Development Status

## Verified GitHub Actions Milestones
- **Run #1 (`44678399756`)** (`main` @ `f2261702b0f4dfd8a0a1bb6d4d0c20aa056cd732`): Confirmed root workflow `/.github/workflows/build-windows-installers.yml` triggers on `windows-latest`; surfaced WPF `CS0104` and NuGet advisories.
- **Run #2 (`44679766730`)** (`main` @ `21da7f890ab7dddd0452adcadf282468df6c73da`): `.NET 10` compilation PASSED, NuGet advisories RESOLVED, `17/18` xUnit tests passed; surfaced SQLite FK `ProjectGroupId` test failure.
- **Run #3 (`44681873469`)** (`main` @ `e4ab9076b67ca039279b6b71d10408fd503e1d0e`): `.NET 10` compilation PASSED, SQLite FK PASSED, `19/20` xUnit tests passed; surfaced SQLite `DateTimeOffset` `ORDER BY` failure.
- **Run #4 (`44684923525`)** (`main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`): **FULL GREEN BUILD & INSTALLER PACKAGING** — `24/24` xUnit tests PASSED; unsigned self-contained `win-x64` Desktop (`~62.92 MB`) & Agent (`~46.64 MB`) installers + `SHA256SUMS.txt` generated & uploaded.
- **Run #5 (`44688053844`)** (`d3a7a7d6d11e4c1a355e31a8f2995c28104bd859`): Failed at `ServerForge.AgentTests` compile (82 errors) — fixed for Run #6.
- **Run #6 (`44689338386`)** (`4ad417c383ae524ece90861a77e10e92f03c56f3`): **COMPILE PASSED & 30 / 32 xUnit TESTS PASSED**:
  - `22/22` Unit tests PASSED, `1/1` Integration test PASSED, `7/9` Agent tests PASSED (including live Windows Schannel Kestrel HTTPS/mTLS pairing, pinning, replay rejection, rogue/anonymous rejection, live cert rotation, and all 3 Desktop SetupWizard/13-screen/Theme/SQLite/Demo tests).
  - 2 remaining mTLS tests (`LiveMtls_PersistsAcrossAgentAndDesktopRestarts_AndRecoversAfterConnectionLoss`, `LiveMtls_MultipleServers_PairPersistAndCommunicateIndependently`) failed in fixture setup with `SQLite Error 14: unable to open database file` because `CreateDesktopStoreAsync` omitted `Directory.CreateDirectory(desktopDir)`.

## Repairs Completed for Windows CI Run #7
- Added `Directory.CreateDirectory(desktopDir);` to `DesktopToAgentMtlsIntegrationTests.CreateDesktopStoreAsync`.
- Audited all production and test SQLite database initializers across the entire solution to confirm parent directory creation and test isolation.
- Hardened `smoke-test-installers.ps1` with pre-flight production safety guards, unique per-run disposable paths & service names, deterministic `Wait-Until` loops for service start/loopback bind/Inno async uninstall, and Windows Application Event Log failure capture.

## 8-Stage Independent Verification Status (Never Collapsed Into One Percentage)

| Stage | Status | Evidence |
|---|---|---|
| 1. Source implementation completed | **COMPLETED** | All 11 `.NET 10` projects, 13 WPF screens, 32 xUnit tests, Inno scripts, 18-check smoke suite, and disposable sample app implemented. |
| 2. Windows CI compilation verified | **VERIFIED (Run #6 `44689338386`)** | Native `.NET 10` WPF Desktop, Agent, and all 32 tests compiled cleanly with zero NuGet advisories; re-verify in Run #7. |
| 3. Installer packaging verified | **VERIFIED (Run #4 `44684923525`); RE-VERIFY RUN #7** | Desktop (`~62.92 MB`) + Agent (`~46.64 MB`) + `SHA256SUMS.txt` verified in Run #4; updated `.iss` scripts await Run #7 once `32/32` tests pass. |
| 4. Installer execution verified (install/service/upgrade/uninstall) | **PENDING CI RUN #7** | Automated `smoke-test-windows-installers` job (18 checks) executes automatically after packaging succeeds. |
| 5. Native desktop interactive GUI verified (Windows 11) | **NOT VERIFIED** | Requires manual interactive Windows 11 checklist (`INSTALLATION.md` §5); CI does NOT test WPF GUI rendering. |
| 6. Agent mTLS & IIS integration verified | **PARTIAL — 3/5 LIVE mTLS PASSED IN RUN #6 (2 FIXED FOR RUN #7); LIVE IIS BLOCKED** | Real Windows Schannel Kestrel mTLS verified for pairing/auth/rotation in Run #6; restart & multi-server mTLS await Run #7; live IIS requires Windows Server VM. |
| 7. Autonomous GitHub deployment verified | **BLOCKED (Requires Disposable Windows Server VM)** | Unit provenance checks passed in Run #6; live push-to-IIS deployment requires disposable VM runbook. |
| 8. Rollback verified | **PARTIAL — UNIT PASSED (Run #6); LIVE VM BLOCKED** | `DeploymentOrchestrator` auto-rollback unit tests passed; live VM rollback with `SIMULATE_UNHEALTHY=true` requires disposable VM. |

## Current Project Verdict
**RUN #6 COMPILED CLEANLY & PASSED 30/32 TESTS (INCLUDING LIVE WINDOWS mTLS & DESKTOP VIEWMODEL TESTS); 2 `SQLite Error 14` FIXTURE FAILURES FIXED ON DISK — PENDING WINDOWS CI RUN #7 (`32/32` TESTS + BOTH INSTALLERS + 18-CHECK SMOKE JOB); INTERACTIVE WINDOWS 11 GUI & LIVE WINDOWS SERVER IIS E2E NOT YET VERIFIED.**
