# ServerForge Test Report

> **Strict Honesty Policy**: Only checks executed on real Windows runners are marked **PASSED**. Run #6 (`44689338386`) compiled cleanly and passed **30 / 32** xUnit tests on Windows. The 2 `SQLite Error 14` fixture failures are fixed on disk and await **Windows CI Run #7**, along with the first execution of `smoke-test-windows-installers`. Interactive Windows 11 WPF GUI checks remain **NOT VERIFIED**, and live Windows Server VM IIS deployment/rollback checks remain **BLOCKED**.

## Verified Windows CI Run #6 Evidence (`44689338386`)
- **Repository / Commit**: `Zohaib-Jadoon/SERVER-FORGE` @ `4ad417c383ae524ece90861a77e10e92f03c56f3`
- **Run URL**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44689338386`
- **Compilation**: **PASSED** (0 errors across all 11 projects + 3 test projects; zero NuGet advisories).
- **Automated xUnit Results**: **30 / 32 PASSED**
  - `ServerForge.UnitTests`: **22 / 22 PASSED**
  - `ServerForge.IntegrationTests`: **1 / 1 PASSED**
  - `ServerForge.AgentTests`: **7 / 9 PASSED**
    - **PASSED on real Windows**: `AgentIdentityAndPairingTests` (1/1), `DesktopStartupAndViewModelTests` (3/3), and 3 live Kestrel HTTPS+mTLS tests (`LiveMtls_PairingCode...`, `LiveMtls_AuthenticatedRequests...`, `LiveMtls_CertificateRotation...`).
    - **FAILED (2/9)**: `LiveMtls_PersistsAcrossAgentAndDesktopRestarts_AndRecoversAfterConnectionLoss` and `LiveMtls_MultipleServers_PairPersistAndCommunicateIndependently` threw `SQLite Error 14: unable to open database file` inside `CreateDesktopStoreAsync(string desktopDir)` because `Directory.CreateDirectory(desktopDir)` was omitted before opening SQLite.
- **Installer Packaging & Smoke-Test Jobs**: Not reached in Run #6 due to those 2 test failures.

## Automated Test & Verification Matrix (Total: 32 xUnit Tests + 18 Installer Smoke Checks)

| Test Suite / Verification Area | Run #6 (`44689338386`) | Post-Fix Status | Notes |
|---|---|---|---|
| Full `.NET 10` solution & WPF XAML compilation (`ServerForge.sln`) | **PASSED** | **VERIFIED (Run #6); RE-VERIFY RUN #7** | Zero compile errors and zero NuGet advisories. |
| `SecurityAndValidationTests` (8 unit tests) | **8 / 8 PASSED** | **PASSED (Run #6)** | Pairing tokens, mTLS pinning, path guard, redaction, validators, `.env`, `Guid.Empty` `ProjectGroupId`. |
| `IisAndDatabaseSafetyTests` (4 unit tests) | **4 / 4 PASSED** | **PASSED (Run #6)** | Unmanaged IIS read-only lock, binding conflicts, SQL/PG identifiers, approval-gated restore. |
| `DeploymentAndPersistenceTests` (10 unit tests) | **10 / 10 PASSED** | **PASSED (Run #6)** | Provenance, rollback, SQLite FK, `DateTimeOffset` UTC ordering, pagination, versioned migrations & backup. |
| `WindowsPlatformIntegrationTests` (1 test) | **1 / 1 PASSED** | **PASSED (Run #6)** | Real Windows DPAPI `CurrentUser` round-trip. |
| `AgentIdentityAndPairingTests` (1 test) | **1 / 1 PASSED** | **PASSED (Run #6)** | Server cert generation, pairing persistence, cert rotation, rogue instance rejection. |
| `DesktopStartupAndViewModelTests` (3 tests) | **3 / 3 PASSED** | **PASSED (Run #6)** | SetupWizard password validation + DPAPI self-test + skip-server + PBKDF2 persistence; 13-screen routing, theme persistence, SQLite CRUD, log filtering; isolated Demo Mode. |
| `DesktopToAgentMtlsIntegrationTests` — 3 pairing/auth/rotation live Kestrel mTLS tests | **3 / 3 PASSED** | **PASSED (Run #6)** | Real Windows Schannel TLS + mTLS pairing, replay rejection, thumbprint pinning fail-closed, rogue/anonymous 403/401 rejection, live cert rotation revoking old cert. |
| `DesktopToAgentMtlsIntegrationTests` — 2 restart/multi-server live Kestrel mTLS tests | **0 / 2 FAILED (`SQLite Error 14`)** | **FIXED — PENDING CI RUN #7** | Added `Directory.CreateDirectory(desktopDir);` in `CreateDesktopStoreAsync`. |
| Automated Windows Installer Smoke Tests (`smoke-test-installers.ps1` — 18 checks) | Not reached | **PENDING CI RUN #7** | Hardened with production safety guards, unique per-run paths/service names, async Inno uninstaller & service `Wait-Until` polling, and Event Log capture. |
| Interactive Windows 11 WPF GUI Rendering & Tray UX | NOT RUN | **NOT VERIFIED** | Manual checklist in `INSTALLATION.md` §5; never claimed by unattended CI. |
| Live Disposable Windows Server VM E2E (IIS deploy, autonomous push deploy, rollback) | NOT RUN | **BLOCKED (Requires Windows Server VM)** | Reproducible sample app & 20-step runbook at `/ServerForge/samples/disposable-test-app/`. |

## Local Linux Container Static & Companion Checks
- Audited every `UseSqlite` / `SqliteConnection` call in `ServerForge/src` and `ServerForge/tests` confirming parent directory creation and isolation from production metadata paths.
- Audited `smoke-test-installers.ps1` for unique disposable names/paths, production guards, exception-safe cleanup, and PowerShell StrictMode safety.
- `src/utils/solutionTree.test.ts` (`npx --yes tsx --test`): Verifies the read-only source explorer companion utilities only — never counted as Windows `.NET` verification.
