<p align="left">
  <img src="assets/serverforge-icon.png" alt="ServerForge Icon" width="72" height="72" />
</p>

# ServerForge — Native Windows Self-Hosted Deployment Platform

ServerForge is a genuine native Windows (.NET 10 LTS / WPF + Windows Worker Service) self-hosted deployment platform for managing Windows Servers, Microsoft IIS, WinSW/Windows Services, GitHub automated deployments, domains/SSL, environment variables, SQL Server, and PostgreSQL.

## Core Deliverables

1. **`ServerForge-Desktop-Setup.exe`** — Native WPF Windows 11 Fluent desktop application (`net10.0-windows`, self-contained `win-x64`).
2. **`ServerForge-Agent-Setup.exe`** — Autonomous .NET 10 Windows Service agent (`ServerForgeAgent`) with fail-closed mutual TLS (mTLS) API and background GitHub Auto-Deploy worker.

## Verified Baseline & Personal Release

- **Verified Windows CI Run #13 (`44870755690` @ `f10ae9218ce0ddcc8f3453b0330cae05fe979242`):** Native `.NET 10` compilation PASSED, **48/48** xUnit tests PASSED, **50/50** automated Windows installer & service smoke checks PASSED.
- **Complete User Guide (Bundled in Release ZIP):** [`release/personal-v1.0.0/README.md`](release/personal-v1.0.0/README.md)
- **Quick-Start Checklist:** [`release/personal-v1.0.0/START_HERE.txt`](release/personal-v1.0.0/START_HERE.txt)
- **Non-Developer Installation Guide:** [`release/personal-v1.0.0/INSTALLATION_GUIDE.md`](release/personal-v1.0.0/INSTALLATION_GUIDE.md)

## Quick Build & Personal ZIP Packaging on Windows 11

```powershell
# Prerequisites: .NET 10 LTS SDK + Inno Setup 6 (winget install JRSoftware.InnoSetup)
cd ServerForge
.\scripts\build-installers.ps1
.\scripts\package-personal-release.ps1 -InstallerDir .\artifacts\installers
```

Outputs:
- `artifacts\installers\ServerForge-Desktop-Setup.exe`
- `artifacts\installers\ServerForge-Agent-Setup.exe`
- `artifacts\release\ServerForge-Personal-Windows-x64-v1.0.0.zip`

## Solution Map

| Project | Role |
|---|---|
| `ServerForge.Desktop` | Native WPF Windows 11 Fluent MVVM app, Setup Wizard, 13 navigation views, system tray, SQLite + DPAPI |
| `ServerForge.Agent` | Autonomous Windows Service, Kestrel mTLS API (loopback/VPN default), background GitHub Auto-Deploy worker (durable SQLite v3 + HTTP 304 recovery) |
| `ServerForge.Contracts` | Shared DTOs, enums, pairing ceremony messages, telemetry & dry-run models |
| `ServerForge.Core` | Domain entities, 12-state deployment state machine, validators, testable interfaces |
| `ServerForge.Security` | Windows DPAPI protector, log secret redactor, mTLS X.509 certificate & pairing token managers, `PathBoundaryGuard`, `SafeProcessRunner` |
| `ServerForge.Infrastructure` | SQLite EF Core WAL-mode persistence, automatic pre-upgrade `.bak` snapshots & audit trail |
| `ServerForge.GitHub` | Typed `HttpClient` for Fine-Grained PATs, conditional `ETag` polling, strict workflow/artifact provenance & SHA-256 verification |
| `ServerForge.IIS` | `IISAdministration` / `appcmd.exe` bridge, read-only unmanaged site protection, WinSW Windows Service manager, `win-acme` Let's Encrypt helper |
| `ServerForge.Deployments` | Per-app serialized queue orchestrator, automatic rollback & 9 runtime adapters (Next.js SSR, React/Vite, NestJS, Express, Node, ASP.NET Core, Classic ASP, Python FastAPI, Static HTML) |
| `ServerForge.Databases.SqlServer` | Standalone SQL Server discovery, scoped logins, `COPY_ONLY` backups, explicit-approval restores |
| `ServerForge.Databases.PostgreSql` | Local, remote, and external managed (e.g., Neon) PostgreSQL management & `pg_dump` backups |
| `ServerForge.Monitoring` | Real Windows PerformanceCounter, GlobalMemoryStatusEx, DriveInfo, ServiceController, and certificate expiry telemetry |
