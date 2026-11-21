# ServerForge — Native Windows Self-Hosted Deployment Platform (Personal Edition v1.0.0)

**ServerForge** is a native Windows (`.NET 10 LTS` WPF Desktop + Windows Service Agent) self-hosted deployment control center for managing your own Windows IIS servers, Windows Services, GitHub Actions deployments, domains/SSL, environment variables, SQL Server, and PostgreSQL from your Windows 11 PC.

- **100% Personal & Self-Hosted:** No commercial licensing, subscriptions, cloud relays, or telemetry.
- **Zero `.NET SDK` Needed to Run:** Self-contained `win-x64` Inno Setup installers bundle `.NET 10 LTS`.
- **Safe by Default:** Never installs IIS roles, never opens public ports, never touches DNS, and keeps pre-existing IIS websites `ReadOnlyUnmanaged`.

---

## Verified Windows CI Baseline

- **Repository:** `https://github.com/Zohaib-Jadoon/SERVER-FORGE`
- **Verified Green CI Run #13:** [`44870755690`](https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44870755690) @ commit `f10ae9218ce0ddcc8f3453b0330cae05fe979242`
  - Native `.NET 10` Windows compilation: **PASSED**
  - Automated xUnit test suite: **48 / 48 PASSED** (`0` skipped)
  - Automated Windows installer & service smoke checks: **50 / 50 PASSED** (`0` failed)
  - Self-contained Windows installers (`ServerForge-Desktop-Setup.exe`, `ServerForge-Agent-Setup.exe`) & `SHA256SUMS.txt`: **GENERATED & VERIFIED**

---

## Distribution Package (`ServerForge-Personal-Windows-x64-v1.0.0.zip`)

Built on Windows via `.github/workflows/release-personal-windows-zip.yml` (reusing verified Run #13 installers by default) and published to tag **`v1.0.0-personal`**:

```text
ServerForge-Personal-Windows-x64-v1.0.0/
  ServerForge-Desktop-Setup.exe   Install on your Windows 11 PC (WPF Desktop)
  ServerForge-Agent-Setup.exe     Install on each Windows Server to manage (Windows Service)
  README.md                       Complete overview & user guide
  START_HERE.txt                  Quick-start plain-text checklist
  INSTALLATION_GUIDE.md           Non-developer installation & private-network setup guide
  SHA256SUMS.txt                  SHA-256 checksums for every file in the ZIP
  VERSION.txt                     Version, commit SHA, and CI baseline metadata
```

---

## Quick Start & User Guide

For the full non-developer installation guide and complete 13-screen user guide, see:
- **Complete User Guide (bundled in ZIP):** [`ServerForge/release/personal-v1.0.0/README.md`](ServerForge/release/personal-v1.0.0/README.md)
- **Quick-Start Checklist (bundled in ZIP):** [`ServerForge/release/personal-v1.0.0/START_HERE.txt`](ServerForge/release/personal-v1.0.0/START_HERE.txt)
- **Installation Guide (bundled in ZIP):** [`ServerForge/release/personal-v1.0.0/INSTALLATION_GUIDE.md`](ServerForge/release/personal-v1.0.0/INSTALLATION_GUIDE.md)

### 4-Step Setup Summary
1. **Install Desktop on Windows 11:** Double-click `ServerForge-Desktop-Setup.exe` (for unsigned personal installers, choose **More info → Run anyway** in SmartScreen after checking `SHA256SUMS.txt`), open **ServerForge Desktop**, and complete the first-time administrator setup wizard.
2. **Install Agent on Windows Server:** Right-click `ServerForge-Agent-Setup.exe` → **Run as administrator**. It registers and starts `ServerForgeAgent` bound strictly to `127.0.0.1:9443` by default.
3. **Configure Private LAN / VPN Access (Remote Server):** Set `Agent.BindAddress` to your server's private LAN/VPN IPv4 address in `C:\Program Files\ServerForge\Agent\appsettings.json` (never `0.0.0.0` or a public IP), restart `ServerForgeAgent`, and allow TCP `9443` in Windows Firewall only from your PC's private IP.
4. **Pair & Deploy:** Open `C:\ProgramData\ServerForge\Agent\pairing-code.txt`, pair in Desktop under **Servers (`Ctrl+9`)** using the one-time code and pinned certificate thumbprint, then create your project/app, connect a read-only GitHub Fine-Grained PAT (optional), and deploy.

### All 13 Desktop Screens & Shortcuts
| Shortcut | Screen | Purpose |
| --- | --- | --- |
| `Ctrl+1` | **Overview** | Live server/app status dashboard & manual refresh |
| `Ctrl+2` | **Projects** | Group applications into logical projects |
| `Ctrl+3` | **Applications** | Configure apps across 9 runtime adapters, ports, health checks & retention |
| `Ctrl+4` | **GitHub** | DPAPI-encrypted Fine-Grained PAT & explicit repository authorization |
| `Ctrl+5` | **Deployments** | Manual/automatic deployments, release history & rollback |
| `Ctrl+6` | **Domains & SSL** | Domain bindings, DNS guidance, conflict detection & `win-acme` helpers |
| `Ctrl+7` | **Environment Variables** | Plain and DPAPI-encrypted secret variables (redacted `.env` export) |
| `Ctrl+8` | **Databases** | SQL Server & PostgreSQL connections, backups & explicit-approval restores |
| `Ctrl+9` | **Servers** | Pinned mTLS pairing, diagnostics & safe `ReadOnlyUnmanaged` IIS discovery |
| `Ctrl+M` | **Monitoring** | Live Windows CPU, RAM, disk, service & certificate expiry telemetry |
| `Ctrl+L` | **Logs** | Redacted Desktop/Agent logs & SQLite audit trail |
| `Ctrl+B` | **Backups** | Backup directories, retention & pre-migration SQLite snapshots |
| `Ctrl+,` / `Ctrl+T` | **Settings & Theme** | Light/Dark Fluent theme & system tray preferences |

---

## File & Log Locations

- **Desktop (Windows 11 PC):** `%LOCALAPPDATA%\ServerForge\serverforge-desktop.db`, `%LOCALAPPDATA%\ServerForge\backups\`, `%LOCALAPPDATA%\ServerForge\logs\desktop-*.log`
- **Agent (Windows Server):** `C:\Program Files\ServerForge\Agent\appsettings.json`, `C:\ProgramData\ServerForge\Agent\`, `C:\ProgramData\ServerForge\Agent\pairing-code.txt`, `C:\ProgramData\ServerForge\Agent\logs\agent-*.log`, `C:\ServerForgeApps\`

---

## Building from Source (Windows 10/11)

```powershell
# Prerequisites: .NET 10 LTS SDK + Inno Setup 6 (winget install JRSoftware.InnoSetup)
cd ServerForge
.\scripts\build-installers.ps1
```

To assemble the verified personal distribution ZIP locally after building installers:

```powershell
.\scripts\package-personal-release.ps1 -InstallerDir .\artifacts\installers
```

---

## Solution Architecture

| Project | Role |
| --- | --- |
| `ServerForge.Desktop` | Native `.NET 10` WPF Desktop (13 screens, Setup Wizard, system tray, SQLite + DPAPI) |
| `ServerForge.Agent` | Autonomous Windows Service, certificate-pinned mTLS API, background GitHub Auto-Deploy worker |
| `ServerForge.Contracts` | Shared DTOs, enums, pairing messages, telemetry & dry-run models |
| `ServerForge.Core` | Domain entities, 12-state deployment state machine, validators & interfaces |
| `ServerForge.Security` | Windows DPAPI protector, log secret redactor, mTLS X.509 & pairing token managers, `PathBoundaryGuard`, `SafeProcessRunner` |
| `ServerForge.Infrastructure` | SQLite EF Core WAL-mode persistence, schema migrations with `.bak` snapshots & audit trail |
| `ServerForge.GitHub` | Typed GitHub client, conditional `ETag` polling, strict workflow/artifact provenance & SHA-256 verification |
| `ServerForge.IIS` | IIS discovery (`ReadOnlyUnmanaged` protection), `appcmd.exe` bridge, WinSW service manager & `win-acme` helper |
| `ServerForge.Deployments` | Serialized deployment orchestrator, automatic rollback & 9 runtime adapters |
| `ServerForge.Databases.SqlServer` | SQL Server discovery, scoped logins, `COPY_ONLY` backups & explicit-approval restores |
| `ServerForge.Databases.PostgreSql` | Local, remote & managed PostgreSQL connections and `pg_dump` backups |
| `ServerForge.Monitoring` | Real Windows CPU, memory, disk, service & certificate expiry telemetry |

---

## Known Personal-Use Limitations

- Installers are **unsigned** (Windows SmartScreen displays a warning until you click **More info → Run anyway** or sign them with your own certificate).
- Live end-to-end Windows IIS (`w3wp.exe`) deployments/rollbacks (TEST A & TEST B) and interactive Windows 11 WPF visual tests were not executed in CI (`48/48` xUnit and `50/50` installer/service smoke checks passed in Run #13).
- After initial administrator setup, Desktop auto-opens into the Dashboard under your Windows user profile + `CurrentUser` DPAPI rather than showing a separate login prompt on every launch.
- Hosting ASP.NET Core apps in IIS requires installing the Microsoft **.NET 10 Hosting Bundle** separately on target IIS servers.
