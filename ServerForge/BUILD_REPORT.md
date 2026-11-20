# ServerForge Build Report

## Verified Windows CI Run History
- **Run #4 (`44684923525`)** — `main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`: **PASSED**
  - `.NET 10` compilation: PASSED, zero NuGet advisories, `24/24` xUnit tests PASSED.
  - Unsigned self-contained `win-x64` installers generated & uploaded (`ServerForge-Desktop-Setup.exe` ~62.92 MB, `ServerForge-Agent-Setup.exe` ~46.64 MB, `SHA256SUMS.txt`).
- **Run #5 (`44688053844`)** — `d3a7a7d6d11e4c1a355e31a8f2995c28104bd859`: **FAILED AT COMPILE (82 errors in `ServerForge.AgentTests`)** — fixed for Run #6.
- **Run #6 (`44689338386`)** — `4ad417c383ae524ece90861a77e10e92f03c56f3`: **COMPILE PASSED, 30/32 xUnit TESTS PASSED** (2 `SQLite Error 14` fixture failures — fixed for Run #7).
- **Run #7 (`44690875542`)** — `main` @ `0aef82a8ed92c47a2b03663912026e66b8545a92`: **JOB 1 (`build-test-package-windows`) 100% GREEN; JOB 2 (`smoke-test-windows-installers`) FAILED AT `Desktop.8a_CleanUninstallSucceeded`**
  - Native `.NET 10` Windows compilation (`ServerForge.sln`): **PASSED** (0 errors; WPF Desktop, Agent, and all 32 tests compiled cleanly).
  - NuGet vulnerability audit: **PASSED** (zero advisories).
  - Automated xUnit test suite: **32 / 32 PASSED** (`22/22` Unit, `9/9` Agent, `1/1` Integration — including all 5 live Windows Schannel Kestrel HTTPS/mTLS tests and all 3 Desktop SetupWizard/13-screen/Theme/SQLite/Demo tests).
  - Self-contained `win-x64` publishing & Inno Setup 6 compilation: **PASSED** (`ServerForge-Desktop-Setup.exe`, `ServerForge-Agent-Setup.exe`, `SHA256SUMS.txt` verified & uploaded).
  - `smoke-test-windows-installers`: Desktop install, binary/runtime validation, uninstaller existence, Uninstall registry check, in-place upgrade preserving `%LOCALAPPDATA%\ServerForge`, and unrelated canary process survival all **PASSED** (`desktop-install.log` and `desktop-upgrade.log` produced). Failed at `Desktop.8a_CleanUninstallSucceeded` (`ExitCode=1`) with `desktop-uninstall.log` missing. Agent smoke tests were not reached.

## Root Cause & Repairs Applied After Run #7 (`44690875542`) — Awaiting Windows CI Run #8
1. **Exact Root Cause of `Desktop.8a_CleanUninstallSucceeded` (`ExitCode=1`, missing `desktop-uninstall.log`)**:
   - `/.github/workflows/build-windows-installers.yml` passed relative `-LogOutputDir 'ServerForge/artifacts/smoke-logs'`.
   - `ServerForge-Desktop-Setup.exe` (install & upgrade) inherited the runner's CWD (`D:\a\SERVER-FORGE\SERVER-FORGE`), where `ServerForge\artifacts\smoke-logs` existed, so `desktop-install.log` and `desktop-upgrade.log` succeeded.
   - Inno Setup 6's `unins000.exe` copies itself into `%TEMP%\_iu*.tmp` and re-launches with `CWD = %TEMP%` so `{app}` is not locked during deletion. Passing a relative `/LOG="ServerForge\artifacts\smoke-logs\desktop-uninstall.log"` caused Inno Setup to try creating `%TEMP%\ServerForge\artifacts\smoke-logs\desktop-uninstall.log` (parent directory missing), aborting immediately with `ExitCode=1` and zero log file.
2. **Fixes Applied**:
   - **`ServerForge/scripts/smoke-test-installers.ps1`**:
     - Added `Resolve-AbsolutePath` to normalize `InstallerDir`, `LogOutputDir`, and `DisposableRoot` to strict absolute paths at startup.
     - Added `Resolve-RegisteredUninstaller` to resolve the authoritative post-upgrade uninstaller from Windows Uninstall registry (`UninstallString` / `QuietUninstallString`) for both Desktop and Agent (with `unins*.exe` fallback) and log full registry metadata.
     - Added `Invoke-InnoExecutable` with explicit `-WorkingDirectory` (`$InstallerDir` for setups, `$env:TEMP` for uninstallers), guaranteed absolute `/LOG="<absPath>"`, command-line/exit-code/log-size diagnostics, and last-40-line log tailing on any non-zero exit code.
     - Preserved all strict assertions (non-zero exit codes always fail) and extended verification to poll for both binary removal and Uninstall registry key removal while confirming `%LOCALAPPDATA%\ServerForge` and `C:\ServerForgeApps` data preservation and canary process survival.
     - Added System SCM + Application Event Log capture in the `finally` block alongside Agent runtime logs before exception-safe fallback cleanup.
   - **`/.github/workflows/build-windows-installers.yml`**: Updated `smoke-test-windows-installers` to pass workspace-rooted absolute paths (`Join-Path $env:GITHUB_WORKSPACE ...`) for `-InstallerDir` and `-LogOutputDir`.
   - **`ServerForge/installers/Agent/ServerForge-Agent.iss`**: Increased `StopExistingAgentService` stop-wait loop from `6` to `10` iterations (`5s` total) so Kestrel & SQLite reliably release file handles before upgrade/uninstall.
   - **No C#/.NET application code modified**: All `32/32` xUnit tests and both `.NET 10` builds/publishes are already green from Run #7.

## Signing & Environment Transparency
- **Installers are UNSIGNED**: No Authenticode certificate is configured in CI; verify `SHA256SUMS.txt` or sign with an operator-owned certificate before broad distribution.
- **Google AI Studio Container**: Linux x64 (`gVisor`) without `.NET 10` Windows SDK, `ISCC.exe`, or GitHub push credentials. Push to `Zohaib-Jadoon/SERVER-FORGE` to trigger Windows CI Run #8.

## Next Steps to Trigger Windows CI Run #8
```powershell
git checkout -b fix/phase11-ci-run8-uninstall-smoke
# Extract updated archive into repository root, preserving /.github/workflows/build-windows-installers.yml
git add .github/workflows/build-windows-installers.yml `
        ServerForge/scripts/smoke-test-installers.ps1 `
        ServerForge/installers/Agent/ServerForge-Agent.iss `
        ServerForge/INSTALLATION.md ServerForge/MEMORY.md `
        ServerForge/BUILD_REPORT.md ServerForge/TEST_REPORT.md `
        ServerForge/DEVELOPMENT_STATUS.md ServerForge/PRODUCTION_READINESS_REPORT.md
git commit -m "Fix Inno Setup uninstaller relative /LOG path in Windows installer smoke tests and resolve registered uninstallers"
git push -u origin fix/phase11-ci-run8-uninstall-smoke
```
