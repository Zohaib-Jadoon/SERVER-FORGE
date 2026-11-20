# ServerForge Development Status

## Completed Code Repairs (Continuation Pass)

| Area | Status (Source Code) | Notes |
|---|---|---|
| **Problem A — Root GitHub Actions CI** | Repaired | Moved workflow to repository root `/.github/workflows/build-windows-installers.yml`; removed misplaced `ServerForge/.github/workflows/build-windows-installers.yml`. |
| **Problem B — Script Failure Validation** | Repaired | `build-windows.ps1` and `build-installers.ps1` now validate `$LASTEXITCODE` on every native command and verify non-empty output binaries before reporting success. |
| **Problem C — Artifact Provenance & Validation** | Repaired | `DeploymentOrchestrator` and `GitHubApiClient` enforce exact 40-char commit SHA, authorized repo/branch match, GitHub Actions workflow run/artifact identity, trusted API SHA-256 digest matching, Zip-Slip extraction, and runtime application-file validation. Missing/empty/untrusted artifacts fail immediately. |
| **Problem D — Agent Application Management API** | Implemented | Added mTLS endpoints for create/update/list/remove-from-management applications, auto-deploy toggle, masked/encrypted env vars, domains, releases, IIS site adoption, DB test/backup, and Desktop cert rotation. |
| **Problem E — Pairing Persistence & Cert Rotation** | Repaired | `SetupWizardViewModel`, `MainViewModel`, and `ServerForgeStore` persist paired servers, pinned cert thumbprints, and DPAPI GitHub PAT in SQLite, automatically reconnecting on startup. |
| **Problem F — Native WPF 13-Screen UI** | Implemented | Replaced monolithic shared grid with 13 dedicated WPF `UserControl` XAML views (`Overview`, `Projects`, `Applications`, `GitHub`, `Deployments`, `Domains & SSL`, `Environment Variables`, `Databases`, `Servers`, `Monitoring`, `Logs`, `Backups`, `Settings`) routed via `MainWindow.xaml`. |
| **Problem G — Durability & Rollback** | Repaired | Added persisted state transitions, startup crash recovery (`RecoverInterruptedDeploymentsAsync`), duplicate-commit prevention, and safe `LastKnownGoodReleasePath` tracking without auto-reverting DB schemas. |
| **Problem H — Installer Reliability** | Repaired | Scoped `CloseApplicationsFilter` strictly to ServerForge binaries; upgraded `ServerForge-Agent.iss` with safe in-place service stop/config/start/verify Pascal Script logic preserving `appsettings.json` and `C:\ServerForgeApps`. |

## Current Project Verdict

**INCOMPLETE — BLOCKED ON WINDOWS COMPILATION & LIVE E2E VERIFICATION.**
Because this workspace runs Linux x64 without the .NET 10 Windows Desktop SDK, Inno Setup (`ISCC.exe`), or Git push credentials, Windows compilation, xUnit execution, installer generation, and live Windows Server E2E acceptance remain **UNVERIFIED / BLOCKED** until executed on Windows 11 or GitHub Actions `windows-latest`.
