# ServerForge Test Report

> **Strict Honesty Policy**: Only checks executed on real Windows runners are marked **PASSED**. Run #7 (`44690875542`) passed **32 / 32** xUnit tests, built and verified both installers, and passed Desktop smoke checks 1–7c before failing at `Desktop.8a_CleanUninstallSucceeded` (`ExitCode=1`) due to a relative `/LOG=` path when Inno Setup's uninstaller re-launched from `%TEMP%`. That defect is fixed on disk and awaits **Windows CI Run #8** to verify Desktop clean uninstall and the full Agent installer smoke suite. Interactive Windows 11 WPF GUI checks remain **NOT VERIFIED**, and live Windows Server VM IIS deployment/rollback checks remain **BLOCKED**.

## Verified Windows CI Run #7 Evidence (`44690875542`)
- **Repository / Commit**: `Zohaib-Jadoon/SERVER-FORGE` @ `0aef82a8ed92c47a2b03663912026e66b8545a92`
- **Run URL**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44690875542`
- **Job 1 (`build-test-package-windows`)**: **100% PASSED**
  - Compilation (`ServerForge.sln`): **PASSED** (0 errors across all 11 projects + 3 test projects; zero NuGet advisories).
  - Automated xUnit Results: **32 / 32 PASSED**
    - `ServerForge.UnitTests`: **22 / 22 PASSED**
    - `ServerForge.IntegrationTests`: **1 / 1 PASSED**
    - `ServerForge.AgentTests`: **9 / 9 PASSED** (`AgentIdentityAndPairingTests` 1/1, `DesktopStartupAndViewModelTests` 3/3, and all **5 / 5** live Windows Schannel Kestrel HTTPS/mTLS integration tests including restart persistence, connection-loss recovery, and independent multi-server pairing).
  - Publishing & Inno Setup 6 Packaging: **PASSED** (both self-contained `win-x64` installers compiled, SHA-256 verified, and uploaded).
- **Job 2 (`smoke-test-windows-installers`)**: **FAILED AT `Desktop.8a_CleanUninstallSucceeded` (`ExitCode=1`)**
  - **PASSED on real Windows**: Pre-flight safety guards, Desktop silent install (`desktop-install.log`), `ServerForge.Desktop.exe` + self-contained `.NET 10` runtime files, uninstaller presence, Uninstall registry key, in-place upgrade (`desktop-upgrade.log`) preserving `%LOCALAPPDATA%\ServerForge` marker, and unrelated canary process survival.
  - **FAILED**: `Desktop.8a_CleanUninstallSucceeded` exited `1` with missing `desktop-uninstall.log` because relative `-LogOutputDir` was resolved against `%TEMP%` when Inno Setup's uninstaller cloned itself into `%TEMP%\_iu*.tmp`.
  - **NOT REACHED**: Agent installer smoke tests (`Agent.9`–`Agent.18`).

## Automated Test & Verification Matrix (Total: 32 xUnit Tests + Desktop & Agent Installer Smoke Suite)

| Test Suite / Verification Area | Run #7 (`44690875542`) | Current Status | Notes |
|---|---|---|---|
| Full `.NET 10` solution & WPF XAML compilation (`ServerForge.sln`) | **PASSED** | **VERIFIED (Run #7)** | Zero compile errors and zero NuGet advisories. |
| `SecurityAndValidationTests` (8 unit tests) | **8 / 8 PASSED** | **VERIFIED (Run #7)** | Pairing tokens, mTLS pinning, path guard, redaction, validators, `.env`, `Guid.Empty` `ProjectGroupId`. |
| `IisAndDatabaseSafetyTests` (4 unit tests) | **4 / 4 PASSED** | **VERIFIED (Run #7)** | Unmanaged IIS read-only lock, binding conflicts, SQL/PG identifiers, approval-gated restore. |
| `DeploymentAndPersistenceTests` (10 unit tests) | **10 / 10 PASSED** | **VERIFIED (Run #7)** | Provenance, rollback, SQLite FK, `DateTimeOffset` UTC ordering, pagination, versioned migrations & backup. |
| `WindowsPlatformIntegrationTests` (1 test) | **1 / 1 PASSED** | **VERIFIED (Run #7)** | Real Windows DPAPI `CurrentUser` round-trip. |
| `AgentIdentityAndPairingTests` (1 test) | **1 / 1 PASSED** | **VERIFIED (Run #7)** | Server cert generation, pairing persistence, cert rotation, rogue instance rejection. |
| `DesktopStartupAndViewModelTests` (3 tests) | **3 / 3 PASSED** | **VERIFIED (Run #7)** | SetupWizard password validation + DPAPI self-test + skip-server + PBKDF2 persistence; 13-screen routing, theme persistence, SQLite CRUD, log filtering; isolated Demo Mode. |
| `DesktopToAgentMtlsIntegrationTests` (5 live Kestrel mTLS tests) | **5 / 5 PASSED** | **VERIFIED (Run #7)** | Real Windows Schannel TLS + mTLS pairing, replay rejection, thumbprint pinning, rogue/anonymous 403/401 rejection, live cert rotation, restart persistence + recovery, and multi-server independence. |
| Desktop Installer Smoke Checks 1–7c (Install, Runtime, Registry, Upgrade, Data Preservation, Canary Survival) | **PASSED** | **VERIFIED (Run #7); RE-VERIFY RUN #8** | Confirmed `desktop-install.log` and `desktop-upgrade.log` succeeded and `%LOCALAPPDATA%\ServerForge` survived upgrade. |
| Desktop Installer Clean Uninstall (`Desktop.8a`–`8e`) | **FAILED (`ExitCode=1`)** | **FIXED — PENDING CI RUN #8** | Normalized all paths to absolute paths, resolved authoritative uninstaller from Uninstall registry, set `-WorkingDirectory $env:TEMP`, and verified log + binary + registry removal. |
| Agent Installer Smoke Suite (`Agent.9`–`Agent.18`: Install, Service Auto/Recovery, First-Boot Artifacts, `127.0.0.1:9443`, Restart Persistence, Upgrade Config Preservation, Clean Uninstall Preserving `C:\ServerForgeApps`) | Not reached | **PENDING CI RUN #8** | Uses the same absolute-path & registered-uninstaller helpers plus extended 5s service-stop wait in `ServerForge-Agent.iss`. |
| Interactive Windows 11 WPF GUI Rendering & Tray UX | NOT RUN | **NOT VERIFIED** | Manual checklist in `INSTALLATION.md` §5; never claimed by unattended CI. |
| Live Disposable Windows Server VM E2E (IIS deploy, autonomous push deploy, rollback) | NOT RUN | **BLOCKED (Requires Windows Server VM)** | Reproducible sample app & 20-step runbook at `/ServerForge/samples/disposable-test-app/`. |

## Local Linux Container Static & Companion Checks
- Audited `ServerForge/scripts/smoke-test-installers.ps1`, `ServerForge/installers/Desktop/ServerForge-Desktop.iss`, `ServerForge/installers/Agent/ServerForge-Agent.iss`, and `/.github/workflows/build-windows-installers.yml` for absolute paths, PowerShell 5.1/7 compatibility, balanced syntax, StrictMode safety, and exception-safe cleanup.
- `src/utils/solutionTree.test.ts` (`npx --yes tsx --test`): Verifies the read-only source explorer companion utilities only — never counted as Windows `.NET` verification.
