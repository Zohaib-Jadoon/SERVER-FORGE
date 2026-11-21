# ServerForge Personal Edition (v1.0.0) — README & Complete User Guide

**ServerForge** is a native Windows (`.NET 10 LTS` WPF + Windows Service) self-hosted deployment platform designed for managing your own Windows IIS servers directly from your Windows 11 PC.

- **100% Personal & Self-Hosted:** No commercial license, no subscriptions, no cloud accounts, no telemetry, and no third-party relay servers.
- **Self-Contained Windows x64 Installers:** Both installers bundle the `.NET 10 LTS` runtime — no `.NET SDK` or developer tools are needed to install and run ServerForge.
- **Safe by Default:** Installation never installs IIS roles, never opens public firewall ports, never modifies DNS, and never touches or restarts pre-existing IIS websites, AppPools, or databases.

---

## 1. What Is Inside This Release Package

When you extract **`ServerForge-Personal-Windows-x64-v1.0.0.zip`**, you get:

```text
ServerForge-Personal-Windows-x64-v1.0.0/
  ServerForge-Desktop-Setup.exe   Install on your Windows 11 PC (WPF Control Center)
  ServerForge-Agent-Setup.exe     Install on each Windows Server to manage (Windows Service)
  README.md                       This complete overview and user guide
  START_HERE.txt                  Quick-start plain-text checklist
  INSTALLATION_GUIDE.md           Step-by-step installation and private-network setup guide
  SHA256SUMS.txt                  SHA-256 checksums for verifying every file in this bundle
  VERSION.txt                     Exact release version, build commit SHA, and CI baseline
```

---

## 2. System Requirements

### Control Workstation (ServerForge Desktop)
- **OS:** 64-bit Windows 10 (version 2004 / Build 19041 or later) or Windows 11.
- **Runtime:** None required (self-contained `.NET 10` WPF executable).
- **Privileges:** Standard user install by default (no admin elevation required unless installing into a system-wide directory).

### Managed Windows Server (ServerForge Agent)
- **OS:** 64-bit Windows Server 2019, 2022, or 2025 (also works on Windows 10/11 x64 for local testing).
- **Privileges:** Administrator privileges required to install the `ServerForgeAgent` Windows Service and lock `C:\ProgramData\ServerForge\Agent` permissions.
- **IIS Hosting (Optional):** Microsoft IIS (`Web-Server` role) is only needed if you host IIS sites. If you deploy **ASP.NET Core** apps to IIS, install the Microsoft **.NET 10 Hosting Bundle** separately on the target IIS server.
- **Networking:** A private LAN or private VPN (WireGuard, Tailscale, ZeroTier, or corporate VPN) between your Windows 11 PC and your Windows Server.

---

## 3. Quick Installation

> **Windows SmartScreen Notice:** Because this personal-use release is unsigned (no commercial Authenticode certificate), Windows Defender SmartScreen may display *"Windows protected your PC"*. You can verify the SHA-256 hash against `SHA256SUMS.txt` first, then click **More info → Run anyway**.

### Step A — Install ServerForge Desktop on Your Windows 11 PC
No PowerShell commands are needed.

1. Double-click **`ServerForge-Desktop-Setup.exe`**.
2. Complete the setup wizard (check **Create a desktop shortcut** if you want a Desktop icon in addition to the Start Menu shortcut).
3. Launch **ServerForge Desktop** from the Start Menu or Desktop shortcut.

### Step B — Install ServerForge Agent on Your Windows Server
1. Copy **`ServerForge-Agent-Setup.exe`** to your Windows Server.
2. Right-click **`ServerForge-Agent-Setup.exe`** → **Run as administrator**.
3. Follow the installer wizard:
   - Installs self-contained binaries into `C:\Program Files\ServerForge\Agent`.
   - Registers and starts the **`ServerForgeAgent`** Windows Service (`Automatic` startup with automatic restart recovery at 5s / 15s / 30s).
   - Locks `C:\ProgramData\ServerForge\Agent` ACLs to `Administrators` and `SYSTEM` only.
   - Binds **exclusively to `127.0.0.1:9443`** (local loopback) by default so nothing is exposed to the network until you configure it.
   - Generates a 30-minute one-time pairing code and server certificate thumbprint in:
     `C:\ProgramData\ServerForge\Agent\pairing-code.txt`

### Step C — Enable Private LAN / VPN Connectivity (Remote Servers Only)
Skip this step if you are testing Desktop and Agent on the same PC (`127.0.0.1`). For a remote Windows Server:

1. On the Windows Server, open **Notepad as Administrator** and edit:
   `C:\Program Files\ServerForge\Agent\appsettings.json`
2. Change `BindAddress` from `127.0.0.1` to the server's **private LAN or VPN IPv4 address** (for example `10.10.0.5` or `100.x.y.z`). **Never use `0.0.0.0` or a public internet IP.**
   ```json
   {
     "Agent": {
       "ServiceName": "ServerForgeAgent",
       "BindAddress": "10.10.0.5",
       "Port": 9443,
       "DataRoot": "C:\\ProgramData\\ServerForge\\Agent",
       "AppsRoot": "C:\\ServerForgeApps",
       "RequireVpnOrPrivateNetwork": true
     }
   }
   ```
3. Restart **`ServerForgeAgent`** in Windows **Services** (`services.msc`).
4. In **Windows Defender Firewall with Advanced Security**, add an Inbound Rule allowing **TCP port `9443`** strictly from your **Windows 11 PC's private IP address**.

---

## 4. Complete User Guide

### First-Time Administrator Setup Wizard
When you open ServerForge Desktop for the first time, the Setup Wizard walks you through:
1. **Local Administrator Profile:** Create your username and password (minimum 8 characters), stored hashed (`PBKDF2-SHA512`) in local SQLite.
2. **Windows DPAPI Verification:** Confirms Windows `CurrentUser` encryption works so your local tokens and mTLS client certificate are stored encrypted on disk.
3. **GitHub Connection *(Optional)*:** Connect a Fine-Grained Personal Access Token now or skip and configure it later in **GitHub (`Ctrl+4`)**.
4. **Server Pairing *(Optional)*:** Pair a Windows Server now or uncheck **Add a Windows Server now** to jump straight to the Dashboard and pair later in **Servers (`Ctrl+9`)**.

> **Re-launch Sign-In Behavior:** After first-time setup completes, ServerForge Desktop opens directly into your Dashboard under your signed-in Windows user account (relying on Windows sign-in and `CurrentUser` DPAPI rather than showing a separate login dialog on every launch).

---

### Navigating All 13 Screens (Sidebar & Keyboard Shortcuts)

| Screen | Shortcut | What You Do Here |
| --- | --- | --- |
| **Overview** | `Ctrl+1` | Dashboard summary of paired servers, projects, applications, recent deployments, and **Refresh Live State**. |
| **Projects** | `Ctrl+2` | Create and organize logical Project Groups for your applications. |
| **Applications** | `Ctrl+3` | Register applications, select runtime adapter, set branch, internal port, health-check path, and release retention. |
| **GitHub** | `Ctrl+4` | Store a DPAPI-encrypted Fine-Grained PAT, validate permissions, search repositories, and explicitly authorize repos. |
| **Deployments** | `Ctrl+5` | Trigger manual deployments, enable/monitor automatic GitHub Actions deployments, inspect release history, and roll back. |
| **Domains & SSL** | `Ctrl+6` | Configure domain bindings, inspect DNS instructions, detect port/host conflicts, and manage `win-acme` / Let's Encrypt helpers. |
| **Environment Variables** | `Ctrl+7` | Manage per-application plain and secret variables (secrets are DPAPI-encrypted, masked in UI, and redacted on `.env` export). |
| **Databases** | `Ctrl+8` | Configure and test SQL Server and PostgreSQL connections (local, remote, or managed) and run safe backups/restores. |
| **Servers** | `Ctrl+9` | Pair new Windows Servers over certificate-pinned mTLS, run read-only diagnostics, and safely discover existing IIS sites. |
| **Monitoring** | `Ctrl+M` | Inspect real Windows CPU, memory, disk space, Windows Service status, and TLS certificate expiry telemetry. |
| **Logs** | `Ctrl+L` | View and filter Desktop logs, Agent logs, and the SQLite audit trail (all secrets automatically redacted). |
| **Backups** | `Ctrl+B` | Configure backup directories, inspect retention settings, and run safe backups (restores require explicit confirmation). |
| **Settings** | `Ctrl+,` | Switch Dark/Light theme (`Ctrl+T`), review local storage settings, and manage Desktop preferences. |

---

### Core Day-to-Day Workflows

#### 1. Pairing a Windows Server (Pinned mTLS)
1. On your Windows Server, open `C:\ProgramData\ServerForge\Agent\pairing-code.txt`.
2. In Desktop, open **Servers (`Ctrl+9`)** → **Add Server**.
3. Enter your server name, private LAN/VPN IP (or `127.0.0.1` locally), port `9443`, the **One-Time Pairing Code**, and the **Server Certificate Thumbprint**.
4. Click **Pair**. Desktop verifies the exact TLS certificate thumbprint (fail-closed) and exchanges a dedicated client mTLS certificate persisted in local DPAPI storage.

#### 2. Discovering Existing IIS Sites Safely
1. Select your paired server and click **Refresh Live State**.
2. All pre-existing IIS sites (including Classic ASP or legacy sites) appear tagged **`ReadOnlyUnmanaged`**.
3. ServerForge will **never** modify, stop, or delete unmanaged IIS sites unless you explicitly perform an adoption workflow.

#### 3. Connecting GitHub for Automated Deployments
1. Create a GitHub **Fine-Grained Personal Access Token** scoped ONLY to your target repository with minimum read-only permissions:
   - **Contents:** Read-only
   - **Actions:** Read-only
   - **Metadata:** Read-only
2. In Desktop, open **GitHub (`Ctrl+4`)**, paste the PAT, click **Validate & Save**, and explicitly authorize the repository.
3. Ensure your repository has a GitHub Actions workflow (e.g., `.github/workflows/serverforge-deploy.yml`) that builds your app and uploads a release ZIP artifact.

#### 4. Creating an Application & Configuring Environment Variables
1. Open **Projects (`Ctrl+2`)** and create a Project Group (e.g., `Personal Sites`).
2. Open **Applications (`Ctrl+3`)** and add an Application:
   - Choose your paired server and Project Group.
   - Select the runtime (**ASP.NET Core**, **Static HTML**, **React/Vite**, **Next.js SSR**, **NestJS**, **Express**, **Node.js**, **Python FastAPI**, or **Classic ASP**).
   - Configure the repository, branch, internal port, health-check path (e.g., `/health`), and release retention count.
3. Open **Environment Variables (`Ctrl+7`)** to add plain or encrypted secret variables before deploying.

#### 5. Deploying, Monitoring & Rolling Back
- **Manual Deployment (`Deployments`, `Ctrl+5`):**
  - *GitHub Actions Artifact mode:* Provide the 40-character commit SHA. The Agent independently verifies the authorized repository, branch, workflow path, workflow run, artifact metadata, and SHA-256 digest directly against GitHub before extracting.
  - *Manual Signed Package mode:* Provide a local ZIP path and its 64-character SHA-256 digest.
- **Autonomous Deployment:** Enable Auto-Deploy on the application. The `ServerForgeAgent` Windows Service polls GitHub in the background (handling `HTTP 304 Not Modified`, in-progress builds, rate limits, and Agent restarts automatically) and deploys verified artifacts **even while ServerForge Desktop is closed**.
- **Automatic & Manual Rollback:** Every release is staged into a versioned directory under `C:\ServerForgeApps\`. If a new release fails its post-deploy health check, ServerForge automatically restores the previous healthy release. You can also select any earlier retained healthy release in **Deployments (`Ctrl+5`)** and trigger a rollback.

#### 6. System Tray Behavior
- Clicking the window Close (`X`) button minimizes ServerForge Desktop to the Windows system tray (shield icon).
- Double-click the tray icon or right-click → **Open ServerForge** to restore the window.
- Right-click → **Exit Desktop (Agents keep running)** to exit Desktop completely; paired remote Agents keep running independently as Windows Services.

---

## 5. Security & Safety Architecture

- **Loopback-Only Default:** Fresh Agent installations listen strictly on `127.0.0.1:9443`.
- **Certificate-Pinned Mutual TLS (mTLS):** Both Desktop and Agent authenticate each other with pinned X.509 certificates; mismatched thumbprints fail closed.
- **Encrypted Secrets & Redacted Logs:** All PATs, database connection strings, environment secrets, and mTLS keys are protected with Windows DPAPI and automatically scrubbed from logs and error messages.
- **Server-Side GitHub Provenance Verification:** In `GitHubActionsArtifact` mode, the Agent enforces exact `owner/repo`, branch, 40-char commit SHA, workflow path (resolving `workflow_id` if omitted), safe HTTPS redirect stripping of `Authorization` headers, archive size limits (`512 MB` compressed / `2 GB` uncompressed), SHA-256 verification before ZIP extraction, and Zip-Slip protection.
- **Path & IIS Boundary Protection:** `PathBoundaryGuard` blocks path traversal outside managed directories, and pre-existing IIS sites remain `ReadOnlyUnmanaged`.

---

## 6. File, Configuration & Log Locations

### Windows 11 PC (ServerForge Desktop)
| Item | Path |
| --- | --- |
| Program Files | `%LOCALAPPDATA%\Programs\ServerForge Desktop\` (or chosen install path) |
| SQLite Database & Settings | `%LOCALAPPDATA%\ServerForge\serverforge-desktop.db` |
| Pre-Migration DB Backups | `%LOCALAPPDATA%\ServerForge\backups\` |
| Desktop Logs (Redacted) | `%LOCALAPPDATA%\ServerForge\logs\desktop-*.log` |

### Windows Server (ServerForge Agent)
| Item | Path |
| --- | --- |
| Agent Binaries & Config | `C:\Program Files\ServerForge\Agent\appsettings.json` |
| Agent State, Certs & SQLite DB | `C:\ProgramData\ServerForge\Agent\` |
| One-Time Pairing Code | `C:\ProgramData\ServerForge\Agent\pairing-code.txt` |
| Agent Logs (Redacted) | `C:\ProgramData\ServerForge\Agent\logs\agent-*.log` |
| Managed Application Releases | `C:\ServerForgeApps\` |

---

## 7. Upgrading & Safe Uninstalling

### Upgrading
- Run the newer `ServerForge-Desktop-Setup.exe` or `ServerForge-Agent-Setup.exe` directly over the existing version.
- Desktop upgrades preserve `%LOCALAPPDATA%\ServerForge` and automatically back up SQLite before schema upgrades.
- Agent upgrades cleanly stop `ServerForgeAgent`, update binaries while preserving `appsettings.json`, pairing certificates, SQLite state, and `C:\ServerForgeApps\`, and restart the service.

### Uninstalling
- Use **Windows Settings → Apps → Installed apps** and uninstall **ServerForge Desktop** or **ServerForge Agent**.
- Uninstalling Desktop removes binaries only and keeps `%LOCALAPPDATA%\ServerForge`.
- Uninstalling Agent stops and removes ONLY the `ServerForgeAgent` Windows Service and binaries — it **never** deletes `C:\ServerForgeApps\`, hosted websites, IIS sites, AppPools, or databases.

---

## 8. Troubleshooting (Optional PowerShell Commands)

PowerShell is **not** required for normal Desktop installation, but these commands help verify checksums or diagnose server connectivity:

```powershell
# 1. Verify installer SHA-256 checksums against SHA256SUMS.txt
Get-FileHash .\ServerForge-Desktop-Setup.exe -Algorithm SHA256
Get-FileHash .\ServerForge-Agent-Setup.exe -Algorithm SHA256
Get-Content .\SHA256SUMS.txt

# 2. Check Agent service status & listening address on Windows Server
Get-Service ServerForgeAgent
Get-NetTCPConnection -LocalPort 9443 -State Listen

# 3. Create a scoped Windows Firewall rule (replace IPs with your private server & PC IPs)
New-NetFirewallRule `
  -DisplayName "ServerForge Agent (Private VPN mTLS)" `
  -Direction Inbound -Protocol TCP -LocalPort 9443 `
  -LocalAddress 10.10.0.5 -RemoteAddress 10.10.0.25 -Action Allow
```

Common fixes:
- **Pairing code expired:** Pairing codes expire after 30 minutes (or after first use). Restart the `ServerForgeAgent` service while unpaired to regenerate `C:\ProgramData\ServerForge\Agent\pairing-code.txt`.
- **Desktop cannot reach Agent:** Confirm `BindAddress` in `appsettings.json` matches the server's private LAN/VPN IP, `ServerForgeAgent` is `Running`, and Windows Firewall allows TCP `9443` from your PC's private IP.
- **ASP.NET Core site returns HTTP 500.19 or 502.5 in IIS:** Install the Microsoft **.NET 10 Hosting Bundle** on the Windows Server and run `iisreset` (or recycle the site's AppPool).

---

## 9. Verified Baseline & Known Personal-Use Limitations

- **Verified in Windows CI (Run #13, `44870755690` @ `f10ae9218ce0ddcc8f3453b0330cae05fe979242`):** Native `.NET 10` compilation, **48/48** automated xUnit tests, both self-contained Inno Setup installers, SHA-256 checksums, and **50/50** automated Windows installer/service smoke checks (install, upgrade, restart, loopback binding, hosted-file preservation, and clean uninstall).
- **Known Limitations:**
  - Installers are **unsigned** (SmartScreen warning on first run).
  - Live end-to-end Windows IIS (`w3wp.exe`) deployments/rollbacks (TEST A & TEST B) and interactive Windows 11 WPF visual tests were not executed in CI — test first on a non-critical site before using on important workloads.
  - ASP.NET Core IIS hosting requires installing the Microsoft `.NET 10 Hosting Bundle` separately on target IIS servers.
