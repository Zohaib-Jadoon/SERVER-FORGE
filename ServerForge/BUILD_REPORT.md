# ServerForge Build Report

## Latest Verified Windows CI Run (Run #4 — `44684923525`)
- **Repository**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE`
- **GitHub Actions Run**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44684923525`
- **Branch / Commit**: `main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`
- **Workflow Trigger**: **SUCCEEDED** (`/.github/workflows/build-windows-installers.yml` on `windows-latest`)
- **Verified Outcomes from Run `44684923525`**:
  - Native `.NET 10` Windows solution compilation (`ServerForge.sln`): **PASSED**
  - NuGet vulnerability audit: **PASSED** (zero reported advisories)
  - Automated xUnit test suite: **24 / 24 PASSED**
  - Self-contained `win-x64` Desktop & Agent publishing: **PASSED**
  - Inno Setup 6 installer packaging: **PASSED**
    - `ServerForge-Desktop-Setup.exe`: **GENERATED** (`~62.92 MB`, unsigned)
    - `ServerForge-Agent-Setup.exe`: **GENERATED** (`~46.64 MB`, unsigned)
    - `SHA256SUMS.txt`: **GENERATED**
  - GitHub Actions artifact upload (`serverforge-windows-x64-installers`): **PASSED**

## Phase 11 Changes Added After Run `44684923525` (Awaiting CI Run #5)
1. **Desktop First-Launch & 13-Screen Runtime Hardening**:
   - Fixed fatal WPF first-launch `ShutdownMode="OnMainWindowClose"` bug -> `ShutdownMode="OnExplicitShutdown"` with close-to-tray + explicit tray exit.
   - Added global WPF crash handlers (`DispatcherUnhandledException`, `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`) with Serilog logging & secret redaction (`ISecretProtector.RedactSecrets` optional `knownSecrets`).
   - Added `ThemeManager` dynamic Dark/Light brush switching persisted to SQLite `LocalAdminProfile.DarkThemeEnabled`.
   - Hardened `SetupWizardWindow` / `SetupWizardViewModel` (Confirm Password validation, `AddServerNow` skip toggle, `ServerDisplayName`, step-aware action label, `try/catch`).
   - Audited & repaired all 13 WPF screens (`MainWindow` `Ctrl+,` shortcut, `GitHubView` Import to New App, `DeploymentsView` manual verified deploy + details pane, `LogsView` live `FilteredAuditLogs` + remote Agent audit sync, `DomainsSslView`/`EnvironmentVariablesView` Agent sync + delete, `DatabasesView` auto-port switching).
2. **Windows Schannel mTLS Persistence & Live Kestrel Testability**:
   - Extracted `AgentHostFactory` from `ServerForge.Agent/Program.cs` with configurable `AgentOptions.ServiceName`.
   - Fixed Windows Schannel ephemeral private key persistence in `AgentIdentityStore` and `DesktopIdentityService` (`PersistKeySet | Exportable` PFX reload) and set `ClientCertificateOption.Manual` in `AgentTransportService`.
   - Added 5 live Kestrel loopback HTTPS/mTLS integration tests + 3 Desktop startup/ViewModel tests (**32 total xUnit tests**).
3. **Automated Windows Installer Smoke Tests & Release Quality**:
   - Hardened `ServerForge-Agent.iss` (`SetupLogging=yes`, `/SERVICENAME=...` persisted to `{app}\service-name.txt` for uninstall, silent-safe `SuppressibleMsgBox` when IIS is absent) and `ServerForge-Desktop.iss` (`SetupLogging=yes`).
   - Added `/ServerForge/scripts/smoke-test-installers.ps1` (8 Desktop + 10 Agent checks) and added `smoke-test-windows-installers` CI job in `/.github/workflows/build-windows-installers.yml`.
   - Added disposable E2E sample app (`/ServerForge/samples/disposable-test-app/`) and `/ServerForge/scripts/verify-e2e-deployment.ps1`.

## Signing & Environment Transparency
- **Installers are UNSIGNED**: No Authenticode code-signing certificate is configured in CI; Windows SmartScreen warnings are expected until verified via `SHA256SUMS.txt` or signed by the operator.
- **Google AI Studio Container**: Linux x64 (`gVisor`) without `.NET 10` Windows SDK, `ISCC.exe`, or GitHub push credentials. All Phase 11 `.NET` compilation, `32/32` xUnit execution, and installer smoke tests must execute on GitHub Actions `windows-latest` (Run #5).

## Next Steps to Trigger Windows CI Run #5
```powershell
git checkout -b feat/phase11-runtime-verification
# Extract updated archive into repository root, preserving /.github/workflows/build-windows-installers.yml
git status
git add .github/workflows/build-windows-installers.yml ServerForge/
git commit -m "Phase 11: Desktop/mTLS runtime hardening, live Kestrel mTLS tests, automated installer smoke tests, and disposable E2E sample"
git push -u origin feat/phase11-runtime-verification
```
