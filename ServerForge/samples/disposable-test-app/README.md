# ServerForge Disposable End-to-End Verification App

This minimal `.NET 10` web application is designed strictly for **safe, isolated, disposable** end-to-end verification of ServerForge on a Windows Server 2019/2022/2025 VM (or test server).

**PRODUCTION SAFETY RULE**: Never target an existing production IIS website, Classic ASP application, or production database during verification. Use ONLY this disposable sample repository and an isolated IIS site name (e.g. `SF-sample-e2e`).

## Endpoints
- `GET /` — Returns JSON with `version`, `commitSha`, `machineName`, and UTC timestamp.
- `GET /health` — Returns HTTP `200 OK` normally, or HTTP `503 Service Unavailable` when environment variable `SIMULATE_UNHEALTHY=true` is configured (for real rollback verification).
- `GET /data` — Verifies that `PERSISTENT_DATA_DIR/persistent-marker.txt` survives versioned release swaps and rollbacks untouched.

## Complete 20-Step Disposable E2E Verification Checklist
1. Push the contents of `/ServerForge/samples/disposable-test-app/` to a disposable GitHub repository (e.g. `your-user/serverforge-e2e-sample`).
2. Confirm its GitHub Actions workflow (`.github/workflows/serverforge-deploy.yml`) produces artifact `serverforge-sample-app-win-x64` (`app-release.zip` + SHA-256).
3. Install `ServerForge-Desktop-Setup.exe` on Windows 11.
4. Launch ServerForge Desktop and complete the 10-step Setup Wizard (local admin profile, DPAPI check, GitHub Fine-Grained PAT).
5. Install `ServerForge-Agent-Setup.exe` on a disposable Windows Server VM with IIS installed.
6. Bind Agent to your private VPN IP (never `0.0.0.0`) and add a scoped Windows Firewall rule for your Desktop VPN IP on TCP `9443`.
7. Copy the one-time pairing code and certificate thumbprint from `C:\ProgramData\ServerForge\Agent\pairing-code.txt`.
8. Pair Desktop with Agent in ServerForge Desktop -> **Servers**.
9. Run read-only diagnostics and verify all pre-existing IIS sites are listed as `ReadOnlyUnmanaged` and remain untouched.
10. Create a new Project (`E2E Verification`) in Desktop -> **Projects**.
11. Import `your-user/serverforge-e2e-sample` via Desktop -> **GitHub** -> **Import to New App**.
12. Configure runtime `AspNetCore`, slug `sample-e2e`, branch `main`, port `5199`, domain `sample-e2e.local`, and explicitly authorize the repository for auto-deploy.
13. Configure Production environment variables in Desktop -> **Environment Variables**: `APP_VERSION=1.0.0` and `PERSISTENT_DATA_DIR=C:\ServerForgeApps\sample-e2e\data`.
14. Trigger the first verified deployment (or push a commit to `main` and allow `GitHubAutoDeployWorker` to pick up the workflow artifact).
15. Run `ServerForge/scripts/verify-e2e-deployment.ps1 -BaseUrl http://127.0.0.1:5199 -ExpectedVersion 1.0.0 -PreserveIisSiteName "<ExistingSiteName>"`.
16. Capture the `/data` marker value printed by the script.
17. Push a second commit updating `APP_VERSION=1.0.1` and verify autonomous deployment updates `/` to `1.0.1` while `/data` preserves the exact original marker.
18. Test automatic rollback: set `SIMULATE_UNHEALTHY=true` (or push a broken health check commit), trigger deployment, and confirm ServerForge detects HTTP `503`, rolls back automatically to the previous healthy release (`1.0.1`), and preserves `/data`.
19. Verify pre-existing IIS sites and AppPools were never stopped, modified, or deleted.
20. Test **Remove from Management** in Desktop -> **Applications** and confirm the hosted files under `C:\ServerForgeApps\sample-e2e` and IIS site remain intact on disk.
