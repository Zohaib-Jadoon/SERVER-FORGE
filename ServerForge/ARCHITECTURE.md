# ServerForge Architecture

## 1. Two-Component Topology

- **ServerForge Desktop (Local Windows 11 PC)**:
  - Genuine native WPF (`net10.0-windows`, `UseWPF=true`, .NET 10 Fluent `ThemeMode="Dark"` default + Light toggle).
  - *UI Framework Decision*: WPF on .NET 10 LTS was selected to fulfill the explicit WPF + self-contained Inno Setup `ServerForge-Desktop-Setup.exe` deliverables cleanly without MSIX/Windows App SDK unpackaged bootstrapper friction. Because presentation is strictly separated via MVVM (`CommunityToolkit.Mvvm`), a WinUI 3 shell can bind to the exact same `ViewModels` and services if desired.
  - Local SQLite (`%LOCALAPPDATA%\ServerForge\serverforge-desktop.db`) in WAL mode — zero cloud account or external metadata database required.
  - Local credentials protected via Windows DPAPI (`DataProtectionScope.CurrentUser`).

- **ServerForge Agent (Remote Windows Server)**:
  - Runs 24/7 as the `ServerForgeAgent` Windows Service (`Microsoft.Extensions.Hosting.WindowsServices`).
  - Autonomous `GitHubAutoDeployWorker` polls authorized repositories with conditional `ETag` headers, downloads verified GitHub Actions artifacts, verifies SHA-256 digests, stages immutable releases, executes health checks, and performs automatic rollback — **without requiring ServerForge Desktop to stay open**.
  - Binds its Kestrel mTLS API to `127.0.0.1` or a private VPN interface (WireGuard/Tailscale/ZeroTier) on port `9443`. Never exposed to the public internet by default.

## 2. Deployment Lifecycle (12-State Machine)

`Queued -> Fetching -> [Building -> Testing -> Packaging] -> Staging -> Deploying -> Verifying -> Successful | Failed -> RolledBack | Cancelled`

- Per-application `SemaphoreSlim` locks prevent overlapping deployments to the same application while permitting independent applications to deploy concurrently.
- Immutable timestamped release directories (`C:\ServerForgeApps\<slug>\releases\yyyyMMddHHmmss-<sha>`) ensure production files are never mutated in place during staging.
- Persistent storage (`C:\ServerForgeApps\<slug>\data`) is preserved across releases and rollbacks.
- Rollbacks restore application binaries/services/IIS physical path only — destructive database schema rollbacks are never executed automatically.

## 3. Existing Server Protection Model

Every pre-existing IIS website, Application Pool, Classic ASP workload, and SQL Server database discovered on the server is classified as `ResourceManagementState.ReadOnlyUnmanaged`. All mutating methods enforce `IisAdministrationService.EnsureMutationAuthorized(...)`, throwing `UnauthorizedAccessException` unless the operator explicitly adopts the resource after reviewing a `DryRunPlanDto`.
