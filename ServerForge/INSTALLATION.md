# ServerForge Installation, Networking, Security & First-Launch Guide

> **Signing & SmartScreen Transparency Notice**: The installers produced by GitHub Actions CI (`ServerForge-Desktop-Setup.exe` and `ServerForge-Agent-Setup.exe`) are currently **unsigned** (no Authenticode EV/OV code-signing certificate is configured in CI). Windows Defender SmartScreen will display *"Windows protected your PC"* until you verify the SHA-256 digest against `SHA256SUMS.txt` and choose **More info -> Run anyway**, or sign the installers with your own Authenticode certificate (`signtool.exe sign /fd SHA256 ...`) before organizational distribution.

## 1. Verify Release Integrity First (SHA-256)

Before running either installer downloaded from GitHub Actions (`serverforge-windows-x64-installers`), verify its SHA-256 checksum against `SHA256SUMS.txt`:

```powershell
Get-FileHash .\ServerForge-Desktop-Setup.exe -Algorithm SHA256
Get-FileHash .\ServerForge-Agent-Setup.exe -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

Confirm the printed hashes match `SHA256SUMS.txt` character for character. Both installers are **self-contained `.NET 10 LTS` `win-x64`** packages — you do **not** need to pre-install a `.NET 10` Desktop or ASP.NET Core runtime.

---

## 2. Installing ServerForge Agent on Windows Server (Private / VPN Only)

### Prerequisites
- 64-bit Windows Server 2019, 2022, or 2025 (or Windows 10/11 x64 for disposable local lab testing).
- Administrator privileges (required to register the Windows Service and lock `C:\ProgramData\ServerForge\Agent` ACLs to `Administrators` and `SYSTEM`).
- Optional: Microsoft IIS (`Web-Server` role) + URL Rewrite / ARR for IIS hosting features. Pre-existing IIS sites, AppPools, Classic ASP apps, and databases are **never modified or restarted** by the installer.

### Installation Steps
1. Right-click `ServerForge-Agent-Setup.exe` and select **Run as administrator**.
   - Installer diagnostic logs are enabled automatically (`SetupLogging=yes`) and written to `%TEMP%\Setup Log YYYY-MM-DD #NNN.txt` (or pass `/LOG="C:\Temp\agent-install.log"`).
2. The installer:
   - Stops an existing `ServerForgeAgent` service cleanly on upgrade without closing unrelated applications.
   - Preserves existing `C:\Program Files\ServerForge\Agent\appsettings.json` on upgrades.
   - Registers the `ServerForgeAgent` Windows Service (`Automatic` startup + automatic failure recovery: restart after 5s / 15s / 30s).
   - Locks `C:\ProgramData\ServerForge\Agent` ACLs to `Administrators` and `SYSTEM` only.
   - Starts the service bound **exclusively to `127.0.0.1:9443` by default** (fail-closed — never exposed publicly).
   - Generates a 30-minute single-use pairing code and server certificate thumbprint at:
     `C:\ProgramData\ServerForge\Agent\pairing-code.txt`

### Binding Agent to a Private VPN / Tailscale / WireGuard Interface (NEVER Public Internet)
To manage the remote Windows Server from your Windows 11 PC:
1. Establish a private encrypted tunnel (WireGuard, Tailscale, ZeroTier, or corporate VPN) between your Windows 11 workstation and your Windows Server.
2. Edit `C:\Program Files\ServerForge\Agent\appsettings.json` and set `Agent.BindAddress` to the server's **private VPN IPv4 address** (e.g. `10.10.0.5` or `100.x.y.z` — **never** `0.0.0.0` or a public WAN IP):
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
3. Add a tightly scoped Windows Defender Firewall rule permitting TCP `9443` **only** from your Windows 11 workstation's private VPN IP:
   ```powershell
   New-NetFirewallRule `
     -DisplayName "ServerForge Agent (VPN mTLS Only)" `
     -Direction Inbound `
     -Protocol TCP `
     -LocalPort 9443 `
     -LocalAddress 10.10.0.5 `
     -RemoteAddress 10.10.0.25 `
     -Action Allow
   ```
4. Restart the Agent service and verify it binds **only** to your private VPN IP:
   ```powershell
   Restart-Service ServerForgeAgent
   Get-NetTCPConnection -LocalPort 9443 -State Listen
   ```

---

## 3. Installing ServerForge Desktop on Windows 11

1. Run `ServerForge-Desktop-Setup.exe` (standard user install by default; does not close unrelated applications).
2. Complete the 10-step First-Launch Setup Wizard:
   - **Step 2**: Create your local administrator profile (password >= 8 characters + Confirm Password; stored in `%LOCALAPPDATA%\ServerForge\serverforge-desktop.db` using PBKDF2-SHA512).
   - **Step 3**: Verify Windows DPAPI `CurrentUser` encryption round-trip.
   - **Step 4** *(Optional)*: Connect a GitHub Fine-Grained Personal Access Token (`Contents: Read`, `Actions: Read`, `Metadata: Read`).
   - **Step 5–9** *(Optional — can uncheck "Add a Windows Server now" to pair later)*: Pair with your remote ServerForge Agent using its private VPN IP, port `9443`, one-time pairing code, and pinned server certificate thumbprint from `pairing-code.txt`, then run read-only diagnostics.
   - **Step 10**: Click **Finish & Open Dashboard**.
3. **System Tray Behavior**: Clicking the window Close (`X`) button minimizes ServerForge Desktop to the Windows system tray. Right-click the shield tray icon -> **Open ServerForge** to restore, or **Exit Desktop (Agents keep running)** to exit cleanly. Remote Agents continue autonomous deployments even while Desktop is closed.

---

## 4. Failed-Install Recovery & Safe Uninstall

- **Installer Logs**: Inspect `%TEMP%\Setup Log *.txt` (or the custom `/LOG=...` path).
- **Desktop Runtime Logs**: `%LOCALAPPDATA%\ServerForge\logs\desktop-*.log` (secrets redacted automatically).
- **Agent Runtime Logs**: `C:\ProgramData\ServerForge\Agent\logs\agent-*.log`.
- **SQLite Pre-Upgrade Backups**: Automatic `.bak` snapshots are written to `%LOCALAPPDATA%\ServerForge\backups\` (Desktop) and `C:\ProgramData\ServerForge\Agent\backups\` (Agent) before any schema migration.
- **Safe Uninstall**:
  - Uninstalling **ServerForge Desktop** removes binaries only and **preserves** `%LOCALAPPDATA%\ServerForge` (SQLite DB, DPAPI client cert, and paired server metadata).
  - Uninstalling **ServerForge Agent** stops and deletes ONLY the `ServerForgeAgent` Windows Service and binaries — it **NEVER** deletes `C:\ServerForgeApps`, hosted websites, IIS sites, AppPools, or databases.
  - **Automated / Silent Uninstall Tip**: When invoking `unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="..."` from scripts or CI, always pass a **fully qualified absolute path** to `/LOG=` (for example `/LOG="C:\Temp\desktop-uninstall.log"`). Inno Setup copies its uninstaller into `%TEMP%\_iu*.tmp` and re-launches with `%TEMP%` as its working directory, so relative `/LOG=` paths will fail with exit code `1`.

---

## 5. Manual Windows 11 Interactive Desktop Verification Checklist (NOT VERIFIED IN CI)

Unattended GitHub Actions `windows-latest` runners verify installer execution, self-contained files, Windows Service lifecycle, loopback binding, and ViewModel/mTLS logic, **not** interactive WPF visual rendering. Execute and sign off the following checklist on a real Windows 11 workstation:

- [ ] **NOT VERIFIED** — Install `ServerForge-Desktop-Setup.exe` cleanly on Windows 11 (verify SmartScreen prompt behavior and SHA-256 match).
- [ ] **NOT VERIFIED** — Launch Desktop -> verify Setup Wizard opens cleanly, rejects mismatched/<8-char passwords, passes DPAPI check, and transitions cleanly into `MainWindow` on Finish (no premature shutdown).
- [ ] **NOT VERIFIED** — Toggle Fluent Dark / Light theme in Settings -> verify crisp readable contrast across all 13 screens, restart Desktop, and confirm theme preference persisted.
- [ ] **NOT VERIFIED** — Navigate all 13 screens (`Ctrl+1`..`Ctrl+9`, `Ctrl+M`, `Ctrl+L`, `Ctrl+B`, `Ctrl+,`) and verify zero unhandled exceptions or clipped controls.
- [ ] **NOT VERIFIED** — Click window Close (`X`) -> verify app minimizes to system tray -> double-click tray icon to reopen -> right-click tray icon -> Exit cleanly.
- [ ] **NOT VERIFIED** — Pair a live disposable Windows Server Agent over private VPN -> restart Desktop -> confirm automatic mTLS reconnection without re-pairing.
- [ ] **NOT VERIFIED** — Complete the 20-step disposable E2E deployment & rollback runbook in `/ServerForge/samples/disposable-test-app/README.md`.
