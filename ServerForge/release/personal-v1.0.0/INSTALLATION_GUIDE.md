# ServerForge Personal Edition (v1.0.0) — Installation & User Guide

ServerForge is a self-hosted native Windows deployment manager built for managing your own Windows IIS servers from your Windows 11 PC.

- **No .NET SDK required** — both installers are self-contained Windows x64 applications (`.NET 10 LTS` bundled).
- **No subscriptions, accounts, telemetry, or cloud relays** — everything stays between your Windows 11 PC and your own Windows Servers.
- **Safe by default** — installation never installs IIS roles, never opens firewall ports, never changes DNS, and never touches existing IIS websites or databases.

---

## Package Contents (`ServerForge-Personal-Windows-x64-v1.0.0.zip`)

| File | Purpose |
| --- | --- |
| `ServerForge-Desktop-Setup.exe` | Install on your **Windows 11 PC** (native WPF control center) |
| `ServerForge-Agent-Setup.exe` | Install on each **Windows Server** you want to manage (Windows Service) |
| `SHA256SUMS.txt` | Cryptographic SHA-256 checksums to verify every file in this package |
| `START_HERE.txt` | Quick-start plain-text checklist |
| `INSTALLATION_GUIDE.md` | This complete installation and operation guide |
| `VERSION.txt` | Exact release version, build commit SHA, and verified CI provenance |

> **Windows SmartScreen Notice:** Because this personal release is unsigned (no commercial Authenticode certificate), Windows Defender SmartScreen may display *"Windows protected your PC"*. Verify the installer's SHA-256 hash against `SHA256SUMS.txt` if desired, then click **More info -> Run anyway**.

---

## Part 1 — Install ServerForge Desktop (Windows 11 PC)

No PowerShell or developer tools are needed.

1. Extract `ServerForge-Personal-Windows-x64-v1.0.0.zip` to a folder on your PC.
2. Double-click **`ServerForge-Desktop-Setup.exe`**.
3. Follow the installer wizard:
   - Installs for your current user by default (no admin elevation required unless you choose a system-wide folder).
   - Creates Start Menu shortcuts under **ServerForge**.
   - Optionally check **Create a desktop shortcut**.
4. Click **Finish** (leave **Launch ServerForge Desktop** checked), or open **ServerForge Desktop** from the Start Menu.

### First-Time Administrator Setup
1. On first launch, the Setup Wizard guides you through creating your local administrator profile (username and password, minimum 8 characters).
2. Your password hash is stored in `%LOCALAPPDATA%\ServerForge\serverforge-desktop.db` and local secrets (GitHub PAT, client mTLS certificate) are encrypted using Windows DPAPI (`CurrentUser`).
3. You may optionally connect GitHub and pair a Windows Server inside the wizard, or uncheck **Add a Windows Server now** to finish immediately and configure them later from the main window.
4. Clicking the window Close (`X`) button minimizes ServerForge Desktop to the Windows system tray so you can reopen it quickly. Right-click the shield tray icon to **Open ServerForge** or **Exit Desktop (Agents keep running)**.

> **Login Behavior Note:** After first-time setup completes, ServerForge Desktop opens directly into your Dashboard under your signed-in Windows user account (protected by Windows sign-in and `CurrentUser` DPAPI rather than a separate per-launch login prompt).

---

## Part 2 — Install ServerForge Agent (Windows Server)

Run this on any 64-bit Windows Server (2019, 2022, or 2025) or Windows 10/11 x64 machine you want ServerForge to manage.

1. Copy **`ServerForge-Agent-Setup.exe`** to the target Windows Server.
2. Right-click **`ServerForge-Agent-Setup.exe`** and choose **Run as administrator**.
3. Follow the installer wizard. If Microsoft IIS (`w3wp.exe`) is not installed yet, the installer alerts you and lets you continue safely without modifying Windows roles.
4. During installation, the Agent installer automatically:
   - Installs the self-contained Agent binaries to `C:\Program Files\ServerForge\Agent`.
   - Registers and starts the **`ServerForgeAgent`** Windows Service (`Automatic` startup with automatic restart on failure).
   - Locks `C:\ProgramData\ServerForge\Agent` folder permissions to `Administrators` and `SYSTEM`.
   - Binds **only to `127.0.0.1:9443`** (local loopback) by default so nothing is exposed to the network until you explicitly configure it.
   - Generates a one-time pairing code and server TLS certificate thumbprint at:
     `C:\ProgramData\ServerForge\Agent\pairing-code.txt`

### Connecting a Remote Server Safely (Private Network / VPN Only)
ServerForge is designed for private networks (LAN, WireGuard, Tailscale, ZeroTier, or corporate VPN). **Never expose port `9443` to the public internet or bind to `0.0.0.0`.**

1. On the Windows Server, open Notepad as Administrator and edit:
   `C:\Program Files\ServerForge\Agent\appsettings.json`
2. Change `BindAddress` from `127.0.0.1` to the server's **private LAN or VPN IPv4 address** (for example `10.10.0.5` or `100.x.y.z`):
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
3. Restart the **`ServerForgeAgent`** service in Windows **Services** (`services.msc`).
4. In **Windows Defender Firewall with Advanced Security**, add an Inbound Rule allowing **TCP port `9443`** strictly from your **Windows 11 PC's private IP address** (not `Any`).

---

## Part 3 — Pair Desktop with Your Agent & Deploy Your First App

### 1. Pair Your Windows Server (Pinned mTLS)
1. On your Windows Server, open `C:\ProgramData\ServerForge\Agent\pairing-code.txt`.
2. On your Windows 11 PC, open ServerForge Desktop -> **Servers** (`Ctrl+9`).
3. Enter:
   - **Server Name** (e.g., `Home-IIS-01`)
   - **Host / Private IP** (e.g., `10.10.0.5`, or `127.0.0.1` if testing locally on the same machine)
   - **Port**: `9443`
   - **One-Time Pairing Code** and **Server Certificate Thumbprint** from `pairing-code.txt`
4. Click **Pair Server**. Desktop pins the Agent's TLS certificate and exchanges a dedicated mTLS client certificate.

### 2. Discover Existing IIS Sites Safely
- Click **Refresh Live State** in Desktop.
- Pre-existing IIS sites are listed as **`ReadOnlyUnmanaged`** and are never edited, stopped, or deleted unless you explicitly adopt them.

### 3. Connect GitHub (Optional — For GitHub Actions Deployments)
1. Open **GitHub** (`Ctrl+4`).
2. Enter a GitHub Fine-Grained Personal Access Token with minimal read-only permissions:
   - **Contents:** Read-only
   - **Actions:** Read-only
   - **Metadata:** Read-only
3. Validate the token and explicitly authorize the repository you want to deploy.

### 4. Create a Project, Application & Deployment
1. Open **Projects** (`Ctrl+2`) and create a Project Group.
2. Open **Applications** (`Ctrl+3`) and add an application on your paired server (choose runtime such as ASP.NET Core / Static HTML / Node, branch, internal port, and health check path such as `/health`).
3. Open **Environment Variables** (`Ctrl+7`) to add plain or encrypted secret variables.
4. Open **Deployments** (`Ctrl+5`):
   - **Manual deployment:** Supply a verified local package + SHA-256 or a GitHub commit SHA (for `GitHubActionsArtifact` mode, the Agent independently downloads and verifies the workflow artifact from GitHub).
   - **Automatic deployment:** Enable Auto-Deploy on the application. The Agent polls GitHub on its own and deploys verified GitHub Actions artifacts even when Desktop is closed.
5. Monitor health in **Monitoring** (`Ctrl+M`), review redacted audit & Agent logs in **Logs** (`Ctrl+L`), and restore any earlier healthy release from **Deployments** if needed.

---

## Part 4 — File Locations, Upgrades & Safe Uninstall

### Configuration & Logs
- **Desktop (Windows 11 PC):**
  - SQLite database & local settings: `%LOCALAPPDATA%\ServerForge\serverforge-desktop.db`
  - Pre-upgrade DB backups: `%LOCALAPPDATA%\ServerForge\backups\`
  - Desktop logs (secrets redacted): `%LOCALAPPDATA%\ServerForge\logs\desktop-*.log`
- **Agent (Windows Server):**
  - Service config: `C:\Program Files\ServerForge\Agent\appsettings.json`
  - Identity, mTLS certs & SQLite DB: `C:\ProgramData\ServerForge\Agent\`
  - Pairing code: `C:\ProgramData\ServerForge\Agent\pairing-code.txt`
  - Agent logs (secrets redacted): `C:\ProgramData\ServerForge\Agent\logs\agent-*.log`
  - Managed application releases: `C:\ServerForgeApps\`

### Upgrading
- Run the new `ServerForge-Desktop-Setup.exe` or `ServerForge-Agent-Setup.exe` directly over the existing version.
- Your Desktop database, Agent `appsettings.json`, pairing certificates, and hosted apps under `C:\ServerForgeApps\` are preserved automatically.

### Uninstalling
- Uninstall via **Windows Settings -> Apps -> Installed apps**.
- Uninstalling Desktop removes binaries while keeping `%LOCALAPPDATA%\ServerForge`.
- Uninstalling Agent stops and removes only the `ServerForgeAgent` Windows Service and binaries — it **never** deletes `C:\ServerForgeApps`, IIS sites, AppPools, or databases.

---

## Part 5 — Optional Advanced PowerShell Troubleshooting

PowerShell is **not** needed for ordinary installation, but these commands help if you want to verify checksums or check service status on your server:

```powershell
# Verify installer SHA-256 hashes against SHA256SUMS.txt
Get-FileHash .\ServerForge-Desktop-Setup.exe -Algorithm SHA256
Get-FileHash .\ServerForge-Agent-Setup.exe -Algorithm SHA256
Get-Content .\SHA256SUMS.txt

# Check Agent Windows Service status and listening IP/port (on Windows Server)
Get-Service ServerForgeAgent
Get-NetTCPConnection -LocalPort 9443 -State Listen

# Scoped Windows Firewall rule example (replace with your server & PC VPN IPs)
New-NetFirewallRule `
  -DisplayName "ServerForge Agent (Private VPN mTLS)" `
  -Direction Inbound -Protocol TCP -LocalPort 9443 `
  -LocalAddress 10.10.0.5 -RemoteAddress 10.10.0.25 -Action Allow
```

---

## Known Personal-Use Scope & Verified vs. Unverified Boundaries

- **Verified in Windows GitHub Actions CI (Run #13):** Native `.NET 10` compilation, `48/48` automated xUnit tests (security, SQLite migrations, DPAPI, GitHub provenance, HTTP 304 autonomous polling, rollback state machine, mTLS), both self-contained Inno Setup installers, SHA-256 checksums, and `50/50` automated Windows installer/service smoke checks.
- **Not Verified in CI (Personal-Use Limitations):**
  - Installers are **unsigned** (SmartScreen warning on first run).
  - Real production Windows IIS (`w3wp.exe`) deployments/rollbacks (TEST A & TEST B) and interactive Windows 11 WPF visual clicks have not been executed inside CI; test first against a non-critical app/site on your server.
  - Hosting ASP.NET Core apps under IIS still requires the Microsoft **.NET 10 Hosting Bundle** installed on the target IIS server (not bundled with the Agent).
