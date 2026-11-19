# ServerForge — Native Windows Self-Hosted Deployment Platform

ServerForge is a genuine native Windows (.NET 10 LTS / WPF + Windows Worker Service) self-hosted deployment platform for managing Windows Servers, Microsoft IIS, WinSW/Windows Services, GitHub automated deployments, domains/SSL, environment variables, SQL Server, and PostgreSQL.

## Core Deliverables

1. **`ServerForge-Desktop-Setup.exe`** — Native WPF Windows 11 Fluent desktop application (`net10.0-windows`, self-contained `win-x64`).
2. **`ServerForge-Agent-Setup.exe`** — Autonomous .NET 10 Windows Service agent (`ServerForgeAgent`) with fail-closed mutual TLS (mTLS) API and background GitHub Auto-Deploy worker.

> **Important Build Environment Notice**: This repository was generated in a Linux build container without the .NET 10 Windows Desktop SDK or Inno Setup (`ISCC.exe`). Compilation of `.exe` binaries and `.iss` installers **must occur on Windows 10/11 or GitHub Actions `windows-latest`**. No fabricated binary build results are included.

## Quick Build on Windows 11

```powershell
# Prerequisites: .NET 10 LTS SDK + Inno Setup 6 (winget install JRSoftware.InnoSetup)
cd ServerForge
.\scripts\build-installers.ps1
```

Outputs:
- `artifacts\installers\ServerForge-Desktop-Setup.exe`
- `artifacts\installers\ServerForge-Agent-Setup.exe`

## Solution Map

| Project | Role |
|---|---|
| `ServerForge.Desktop` | Native WPF Windows 11 Fluent MVVM app, 10-step Setup Wizard, 12 navigation views, SQLite + DPAPI |
| `ServerForge.Agent` | Autonomous Windows Service, Kestrel mTLS API (loopback/VPN default), background GitHub Auto-Deploy worker |
| `ServerForge.Contracts` | Shared DTOs, enums, pairing ceremony messages, telemetry & dry-run models |
| `ServerForge.Core` | Domain entities, 12-state deployment state machine, validators, testable interfaces |
| `ServerForge.Security` | Windows DPAPI protector, log secret redactor, mTLS X.509 certificate & pairing token managers, `PathBoundaryGuard`, `SafeProcessRunner` |
| `ServerForge.Infrastructure` | SQLite EF Core WAL-mode persistence + automatic audit trail |
| `ServerForge.GitHub` | Typed `HttpClient` for Fine-Grained PATs, conditional `ETag` polling, Actions artifact download + SHA-256 verification |
| `ServerForge.IIS` | `IISAdministration` / `appcmd.exe` bridge, read-only unmanaged site protection, WinSW Windows Service manager, `win-acme` Let's Encrypt helper |
| `ServerForge.Deployments` | Per-app serialized queue orchestrator + 9 runtime adapters (Next.js SSR, React/Vite, NestJS, Express, Node, ASP.NET Core, Classic ASP, Python FastAPI, Static HTML) |
| `ServerForge.Databases.SqlServer` | Standalone SQL Server discovery, scoped logins, `COPY_ONLY` backups, explicit-approval restores |
| `ServerForge.Databases.PostgreSql` | Local, remote, and external managed (e.g., Neon) PostgreSQL management & `pg_dump` backups |
| `ServerForge.Monitoring` | Real Windows PerformanceCounter, GlobalMemoryStatusEx, DriveInfo, ServiceController, and certificate expiry telemetry |
