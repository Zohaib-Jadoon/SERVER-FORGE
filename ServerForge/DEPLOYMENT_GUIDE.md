# ServerForge Deployment Guide

## Recommended Autonomous Workflow (Antigravity IDE -> GitHub -> Windows Server)

1. Copy `.github/workflows/app-deploy-artifact-template.yml` into your repository as `.github/workflows/serverforge-deploy.yml`.
2. In ServerForge Desktop, go to **GitHub Repositories**, import your repository, select the target branch and monorepo working directory, and explicitly authorize the repository for deployment.
3. Configure the application's runtime adapter (`NextJsSsr`, `ReactViteStatic`, `NestJsApi`, `ExpressApi`, `AspNetCore`, `PythonFastApi`, `StaticHtml`, or adopted `ClassicAsp`), internal loopback port (for proxied runtimes), public domain, health-check path, and DPAPI-encrypted environment variables.
4. Push code from Antigravity IDE to GitHub.
5. GitHub Actions builds the production output on `windows-latest` and uploads the release artifact.
6. `ServerForgeAgent` (running continuously as a Windows Service) detects the new commit via conditional `ETag` polling, downloads the artifact, verifies its SHA-256 digest, stages an immutable release directory, deploys via the stack adapter, probes the readiness health check, and switches traffic.
7. If the health probe fails, `ServerForgeAgent` automatically rolls back to the last known healthy release and records the outcome for ServerForge Desktop to display on next launch.

## Optional Server-Local Build Mode

Server-local builds (`DeploymentSourceMode.ServerLocalBuildExplicitOptIn`) are disabled by default because running arbitrary build toolchains on a production Windows Server carries CPU, memory, and supply-chain risks. Enable only per explicitly authorized repository.
