# ServerForge Troubleshooting

## 1. Pairing Rejected or Connection Refused (Port 9443)
- Confirm `ServerForgeAgent` is running (`Get-Service ServerForgeAgent`).
- Verify `Agent.BindAddress` in `appsettings.json` matches the server's private VPN IP and Windows Firewall permits TCP 9443 from your workstation's VPN IP.
- Confirm the one-time pairing code has not expired (default 30 minutes). Re-generate by running:
  `& "C:\Program Files\ServerForge\Agent\ServerForge.Agent.exe" --generate-pairing-code`

## 2. Reverse Proxy Returns 502 / 500.19 on IIS
- Run **Read-Only Diagnostics** to verify both **IIS URL Rewrite** and **Application Request Routing (ARR)** are installed and Proxy is enabled in IIS Manager -> Server -> Application Request Routing Cache -> Server Proxy Settings.
- Verify the managed WinSW Windows Service is `Running` and listening on `127.0.0.1:<InternalPort>`.

## 3. Auto-Deploy Waiting for Artifact
- Check GitHub Actions in your repository to ensure the workflow completed with `conclusion: success` and uploaded a non-expired artifact for the exact commit SHA.
- Confirm your Fine-Grained PAT includes `Actions: Read` and `Contents: Read`.

## 4. Classic ASP / Legacy Site Blocked from Deployment
- By design, pre-existing IIS sites default to `ReadOnlyUnmanaged`. Open **Servers -> IIS Inventory**, review the dry-run preview, and explicitly adopt the site before triggering deployments.
