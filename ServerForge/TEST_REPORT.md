# ServerForge Test Report

> **Strict Honesty Policy**: Only checks executed on real Windows runners are marked **PASSED**. New or modified Phase 11 tests and smoke tests awaiting Run #5 are marked **PENDING WINDOWS CI RUN #5**. Interactive Windows 11 WPF GUI checks are marked **NOT VERIFIED**, and live Windows Server VM IIS deployment/rollback checks are marked **BLOCKED (Requires Disposable Windows Server VM)**.

## Verified Windows CI Run #4 Baseline (`44684923525`)
- **Repository / Commit**: `Zohaib-Jadoon/SERVER-FORGE` (`main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`)
- **Run URL**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44684923525`
- **Compilation**: **PASSED** (`.NET 10` solution & WPF XAML compiled cleanly; zero NuGet advisories).
- **Test Results**: **24 / 24 PASSED** (`22/22` Unit, `1/1` Agent, `1/1` Integration).
- **Installer Packaging**: **PASSED** (`ServerForge-Desktop-Setup.exe` ~62.92 MB, `ServerForge-Agent-Setup.exe` ~46.64 MB, `SHA256SUMS.txt`).

## Phase 11 Automated Test & Verification Matrix (Post-Phase 11 Total: 32 xUnit Tests + 18 Installer Smoke Checks)

| Test Suite / Verification Area | Run `44684923525` | Current Status | Notes |
|---|---|---|---|
| Full `.NET 10` solution & WPF XAML compilation (`ServerForge.sln`) | **PASSED** | **PENDING CI RUN #5** | Re-verification required after Phase 11 Desktop & Agent edits. |
| NuGet transitive vulnerability audit | **PASSED** | **VERIFIED** | Zero advisories (`SQLitePCLRaw.lib.e_sqlite3`, `System.Security.Cryptography.Pkcs`). |
| `SecurityAndValidationTests` (8 unit tests) | **8/8 PASSED** | **PASSED (Run `44684923525`)** | Pairing tokens, mTLS pinning, path guard, redaction, validators, `.env`, `Guid.Empty` `ProjectGroupId`. |
| `IisAndDatabaseSafetyTests` (4 unit tests) | **4/4 PASSED** | **PASSED (Run `44684923525`)** | Unmanaged IIS read-only lock, binding conflicts, SQL/PG identifiers, approval-gated restore. |
| `DeploymentAndPersistenceTests` (10 unit tests) | **10/10 PASSED** | **PASSED (Run `44684923525`)** | Provenance, rollback, SQLite FK, `DateTimeOffset` UTC ordering, pagination, versioned migrations & backup. |
| `AgentIdentityAndPairingTests` (1 test) | **1/1 PASSED** | **PASSED (Run `44684923525`)** | Server cert generation, pairing persistence, Desktop cert rotation, rogue instance rejection. |
| `WindowsPlatformIntegrationTests` (1 test) | **1/1 PASSED** | **PASSED (Run `44684923525`)** | Real Windows DPAPI `CurrentUser` round-trip on `windows-latest`. |
| `DesktopToAgentMtlsIntegrationTests` (**5 NEW live Kestrel mTLS tests**) | **NEW** | **PENDING CI RUN #5** | Live loopback HTTPS+mTLS: one-time pairing & replay rejection, wrong thumbprint pinning fail-closed, authenticated calls + rogue/anonymous 403/401 rejection, Agent & Desktop restart persistence + connection loss recovery, live cert rotation invalidating old cert, and multi-server pairing. |
| `DesktopStartupAndViewModelTests` (**3 NEW Desktop startup/ViewModel tests**) | **NEW** | **PENDING CI RUN #5** | SetupWizard password validation + DPAPI self-test + skip-server jump + PBKDF2 persistence; MainViewModel 13-screen routing, theme persistence, SQLite CRUD, log filtering; isolated Demo Mode. |
| Automated Windows Installer Smoke Tests (`smoke-test-installers.ps1` — 8 Desktop + 10 Agent checks) | **NEW** | **PENDING CI RUN #5** | Runs in new `smoke-test-windows-installers` CI job: SHA-256 check, silent install, self-contained runtime, uninstall registry, `ServerForgeAgentSmoke` Windows Service running/auto/recovery, loopback-only `127.0.0.1:9443`, restart identity preservation, upgrade config preservation, clean uninstall preserving `C:\ServerForgeApps` & `%LOCALAPPDATA%\ServerForge`. |
| Interactive Windows 11 WPF GUI Rendering & Tray UX | **NOT RUN** | **NOT VERIFIED** | Unattended CI does NOT verify interactive WPF GUI; requires manual Windows 11 checklist (`INSTALLATION.md` §5). |
| Live Disposable Windows Server VM E2E (IIS deploy, autonomous push deploy while Desktop closed, rollback with `SIMULATE_UNHEALTHY=true`, unmanaged IIS untouched) | **NOT RUN** | **BLOCKED (Requires Windows Server VM)** | Reproducible sample app & 20-step runbook provided at `/ServerForge/samples/disposable-test-app/`. |

## Local Linux Container Static & Companion Checks
- Static audit performed on all C#, XAML, Inno Setup `.iss` (Pascal Script), PowerShell `.ps1`, and YAML CI workflow files.
- `src/utils/solutionTree.test.ts` (`npx --yes tsx --test`): Verifies the read-only source explorer companion utilities only — NEVER counted as native Windows `.NET` runtime verification.
