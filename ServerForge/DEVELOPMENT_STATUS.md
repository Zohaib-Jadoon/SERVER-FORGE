# ServerForge Development Status

## Verified GitHub Actions Milestones
- **Run #1 (`44678399756`)** (`main` @ `f2261702b0f4dfd8a0a1bb6d4d0c20aa056cd732`): Confirmed root workflow `/.github/workflows/build-windows-installers.yml` triggers on `windows-latest`; surfaced WPF `CS0104` and NuGet advisories.
- **Run #2 (`44679766730`)** (`main` @ `21da7f890ab7dddd0452adcadf282468df6c73da`): `.NET 10` compilation PASSED, NuGet advisories RESOLVED, `17/18` xUnit tests passed; surfaced SQLite FK `ProjectGroupId` test failure.
- **Run #3 (`44681873469`)** (`main` @ `e4ab9076b67ca039279b6b71d10408fd503e1d0e`): `.NET 10` compilation PASSED, SQLite FK PASSED, `19/20` xUnit tests passed; surfaced SQLite `DateTimeOffset` `ORDER BY` failure.
- **Run #4 (`44684923525`)** (`main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`): **FULL GREEN BUILD & INSTALLER PACKAGING** — `24/24` xUnit tests PASSED; unsigned self-contained `win-x64` Desktop (`~62.92 MB`) & Agent (`~46.64 MB`) installers + `SHA256SUMS.txt` generated & uploaded.
- **Run #5 (`44688053844`)** (`d3a7a7d6d11e4c1a355e31a8f2995c28104bd859`): Failed at `ServerForge.AgentTests` compile (82 errors) — fixed for Run #6.
- **Run #6 (`44689338386`)** (`4ad417c383ae524ece90861a77e10e92f03c56f3`): Compile PASSED, `30/32` xUnit tests PASSED (2 `SQLite Error 14` fixture failures — fixed for Run #7).
- **Run #7 (`44690875542`)** (`main` @ `0aef82a8ed92c47a2b03663912026e66b8545a92`): **JOB 1 (`build-test-package-windows`) 100% GREEN; JOB 2 (`smoke-test-windows-installers`) PARTIAL**
  - **32 / 32 xUnit tests PASSED** (`22/22` Unit, `9/9` Agent including all 5 live Kestrel HTTPS/mTLS tests and all 3 Desktop SetupWizard/ViewModel tests, `1/1` Integration).
  - Native WPF Desktop & Agent compiled, self-contained `win-x64` executables published, both Inno Setup 6 installers compiled, SHA-256 verified, and artifacts uploaded.
  - `smoke-test-windows-installers` passed Desktop install, runtime check, Uninstall registry check, in-place upgrade preserving `%LOCALAPPDATA%\ServerForge`, and canary process survival (checks 1–7c), then failed at `Desktop.8a_CleanUninstallSucceeded` (`ExitCode=1`, missing `desktop-uninstall.log`) because relative `-LogOutputDir` failed when Inno Setup's uninstaller re-launched from `%TEMP%`. Agent smoke tests were not reached.

## Repairs Completed for Windows CI Run #8
- Normalized `InstallerDir`, `LogOutputDir`, and `DisposableRoot` to strict absolute paths in `smoke-test-installers.ps1` and passed workspace-rooted absolute paths from `/.github/workflows/build-windows-installers.yml`.
- Added `Resolve-RegisteredUninstaller` (Windows Uninstall registry `UninstallString` / `QuietUninstallString` with fallback) and `Invoke-InnoExecutable` (explicit `-WorkingDirectory`, guaranteed absolute `/LOG=...`, command/exit-code/log-size diagnostics, and log tailing on failure).
- Extended Desktop & Agent uninstall verification to check `ExitCode=0`, non-empty uninstall log, binary removal, and Uninstall registry removal while verifying user/hosted data preservation and canary process survival.
- Extended Agent `StopExistingAgentService` wait loop in `ServerForge-Agent.iss` to 5 seconds and added Application + System SCM Event Log capture on failure.

## 8-Stage Independent Verification Status (Never Collapsed Into One Percentage)

| Stage | Status | Evidence |
|---|---|---|
| 1. Source implementation completed | **COMPLETED** | All 11 `.NET 10` projects, 13 WPF screens, 32 xUnit tests, Inno scripts, Desktop+Agent smoke suite, and disposable sample app implemented. |
| 2. Windows CI compilation verified | **VERIFIED (Run #7 `44690875542`)** | Native `.NET 10` WPF Desktop, Agent, and all 32 tests compiled cleanly with zero NuGet advisories. |
| 3. Installer packaging verified | **VERIFIED (Run #7 `44690875542`)** | Unsigned self-contained `win-x64` Desktop & Agent Inno Setup 6 installers + `SHA256SUMS.txt` generated, verified, and uploaded. |
| 4. Installer execution verified (install/service/upgrade/uninstall) | **PARTIAL — DESKTOP 1–7c PASSED IN RUN #7; UNINSTALL + AGENT PENDING CI RUN #8** | Desktop install, upgrade, data preservation, and canary survival passed in Run #7; relative `/LOG=` uninstall bug fixed on disk for Run #8. |
| 5. Native desktop interactive GUI verified (Windows 11) | **NOT VERIFIED** | Requires manual interactive Windows 11 checklist (`INSTALLATION.md` §5); CI does NOT test WPF GUI rendering. |
| 6. Agent mTLS & IIS integration verified | **PARTIAL — 5/5 LIVE mTLS PASSED IN RUN #7; LIVE IIS BLOCKED** | All 5 real Windows Schannel Kestrel HTTPS/mTLS integration tests passed in Run #7; live IIS hosting requires disposable Windows Server VM. |
| 7. Autonomous GitHub deployment verified | **BLOCKED (Requires Disposable Windows Server VM)** | Unit provenance checks passed in Run #7; live push-to-IIS deployment while Desktop is closed requires disposable VM runbook. |
| 8. Rollback verified | **PARTIAL — UNIT PASSED (Run #7); LIVE VM BLOCKED** | `DeploymentOrchestrator` auto-rollback unit tests passed in Run #7; live VM rollback with `SIMULATE_UNHEALTHY=true` requires disposable VM. |

## Current Project Verdict
**RUN #7 ACHIEVED A 100% GREEN BUILD, 32/32 xUNIT TESTS (INCLUDING ALL 5 LIVE WINDOWS mTLS TESTS), AND VERIFIED INSTALLER ARTIFACTS; DESKTOP UNINSTALL RELATIVE `/LOG=` SMOKE BUG FIXED ON DISK — PENDING WINDOWS CI RUN #8 FOR FULL DESKTOP + AGENT INSTALLER SMOKE VERIFICATION; INTERACTIVE WINDOWS 11 GUI & LIVE WINDOWS SERVER IIS E2E NOT YET VERIFIED.**
