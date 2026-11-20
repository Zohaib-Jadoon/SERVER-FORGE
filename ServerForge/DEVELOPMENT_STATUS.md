# ServerForge Development Status

## Verified GitHub Actions Milestones
- **Run `44678399756`** (`main` @ `f2261702b0f4dfd8a0a1bb6d4d0c20aa056cd732`): Confirmed root workflow `/.github/workflows/build-windows-installers.yml` triggers on `windows-latest`; surfaced WPF `CS0104` and NuGet advisories.
- **Run `44679766730`** (`main` @ `21da7f890ab7dddd0452adcadf282468df6c73da`): `.NET 10` compilation PASSED, NuGet advisories RESOLVED, `17/18` xUnit tests passed; surfaced SQLite FK `ProjectGroupId` test failure.
- **Run `44681873469`** (`main` @ `e4ab9076b67ca039279b6b71d10408fd503e1d0e`): `.NET 10` compilation PASSED, SQLite FK PASSED, `19/20` xUnit tests passed; surfaced SQLite `DateTimeOffset` `ORDER BY` failure.
- **Run `44684923525`** (`main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`): **FULL GREEN BUILD & INSTALLER PACKAGING**:
  - Native `.NET 10` Windows compilation: **PASSED**.
  - Automated xUnit tests: **24 / 24 PASSED**.
  - Self-contained `win-x64` Desktop (`~62.92 MB`) & Agent (`~46.64 MB`) Inno Setup 6 installers + `SHA256SUMS.txt`: **GENERATED & UPLOADED**.

## Phase 11 Enhancements Implemented (Pending Windows CI Run #5)
- **Phase A — Automated Windows Installer Smoke Tests**:
  - Added `/ServerForge/scripts/smoke-test-installers.ps1` (8 Desktop installer checks + 10 Agent installer/service/loopback/upgrade/uninstall checks) and `smoke-test-windows-installers` CI job in `/.github/workflows/build-windows-installers.yml`.
  - Hardened `ServerForge-Agent.iss` (`SetupLogging=yes`, `/SERVICENAME=...` persisted for uninstall, silent-safe `SuppressibleMsgBox` when IIS is absent) and `ServerForge-Desktop.iss` (`SetupLogging=yes`).
- **Phase B & E — Desktop First-Launch & 13-Screen WPF Audit**:
  - Fixed fatal WPF first-launch bug (`App.xaml` `ShutdownMode="OnExplicitShutdown"` so closing `SetupWizardWindow` transitions cleanly to `MainWindow`).
  - Added global WPF crash handlers (`DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`) with Serilog + secret redaction, close-to-tray + tray exit, `ThemeManager` dynamic Dark/Light brush switching persisted to SQLite, `SetupWizard` Confirm Password + skip-server toggle, `Ctrl+,` shortcut, and audited all 13 WPF screens (`GitHubView` Import to New App, `DeploymentsView` manual verified deploy + detail view, `LogsView` live filtering + remote Agent audit sync, `DomainsSslView`/`EnvironmentVariablesView` Agent sync + delete, `DatabasesView` port auto-switching).
  - Added `DesktopStartupAndViewModelTests` (`3` new automated ViewModel/SQLite/DPAPI/13-screen tests).
- **Phase C — Live Desktop-to-Agent HTTPS/mTLS Integration Tests**:
  - Extracted `AgentHostFactory` from `ServerForge.Agent/Program.cs`.
  - Fixed Windows Schannel ephemeral private key persistence in `AgentIdentityStore` and `DesktopIdentityService` (`PersistKeySet | Exportable` PFX reload) and set `ClientCertificateOption.Manual` in `AgentTransportService`.
  - Added `DesktopToAgentMtlsIntegrationTests` (`5` live Kestrel loopback HTTPS/mTLS tests covering pairing, replay rejection, thumbprint pinning rejection, rogue/anonymous rejection, Agent & Desktop restarts, connection recovery, cert rotation, and multi-server management).
- **Phase D & F — Reproducible Disposable E2E Sample App & Production Protection**:
  - Added `/ServerForge/samples/disposable-test-app/` (`.NET 10` app with `/`, `/health` supporting `SIMULATE_UNHEALTHY=true` rollback testing, `/data` persistent marker, GitHub Actions artifact workflow, and 20-step E2E runbook) + `/ServerForge/scripts/verify-e2e-deployment.ps1`.

## 8-Stage Independent Verification Status (Never Collapsed Into One Percentage)

| Stage | Status | Evidence |
|---|---|---|
| 1. Source implementation completed | **COMPLETED** | All 11 `.NET 10` projects, 13 WPF screens, Inno scripts, smoke tests, mTLS tests, and disposable sample app implemented. |
| 2. Windows CI compilation verified | **VERIFIED (Run `44684923525`); RE-VERIFY PENDING RUN #5** | Run #4 compiled cleanly; Phase 11 source changes await CI Run #5. |
| 3. Installer packaging verified | **VERIFIED (Run `44684923525`); RE-VERIFY PENDING RUN #5** | Desktop (`~62.92 MB`) + Agent (`~46.64 MB`) + `SHA256SUMS.txt` produced in Run #4; updated `.iss` files await CI Run #5. |
| 4. Installer execution verified (install/service/upgrade/uninstall) | **PENDING CI RUN #5** | Automated `smoke-test-windows-installers` job added to CI workflow. |
| 5. Native desktop interactive GUI verified (Windows 11) | **NOT VERIFIED** | Requires manual interactive Windows 11 checklist (`INSTALLATION.md` §5); CI does NOT test WPF GUI rendering. |
| 6. Agent mTLS & IIS integration verified | **PARTIAL — mTLS PENDING CI RUN #5; LIVE IIS BLOCKED** | Live loopback Kestrel mTLS tests run in CI Run #5; live IIS site/AppPool provisioning requires Windows Server VM. |
| 7. Autonomous GitHub deployment verified | **BLOCKED (Requires Disposable Windows Server VM)** | Unit provenance checks passed in Run #4; live push-to-IIS deployment requires disposable VM runbook. |
| 8. Rollback verified | **PARTIAL — UNIT PASSED (Run `44684923525`); LIVE VM BLOCKED** | `DeploymentOrchestrator` auto-rollback unit tests passed; live VM rollback with `SIMULATE_UNHEALTHY=true` requires disposable VM. |

## Current Project Verdict
**BUILD & INSTALLER PACKAGING VERIFIED (RUN #4); PHASE 11 RUNTIME HARDENING, LIVE mTLS TESTS & INSTALLER SMOKE TESTS PENDING WINDOWS CI RUN #5; INTERACTIVE WINDOWS 11 GUI & LIVE WINDOWS SERVER IIS E2E NOT YET VERIFIED.**
