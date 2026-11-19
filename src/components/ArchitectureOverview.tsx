interface ModuleSpec {
  project: string;
  target: string;
  responsibility: string;
  keyFile: string;
}

const MODULES: ModuleSpec[] = [
  {
    project: 'ServerForge.Desktop',
    target: 'net10.0-windows (WPF, self-contained win-x64)',
    responsibility:
      'Native Windows 11 Fluent MVVM interface with 10-step First-Launch Setup Wizard, 12 sidebar sections, local SQLite metadata, DPAPI CurrentUser vault, system tray, and isolated Demo Mode.',
    keyFile: 'src/ServerForge.Desktop/Views/MainWindow.xaml',
  },
  {
    project: 'ServerForge.Agent',
    target: 'net10.0-windows (Windows Worker Service)',
    responsibility:
      'Autonomous 24/7 Windows Service hosting fail-closed mTLS Kestrel API (loopback/VPN default) and GitHubAutoDeployWorker so deployments continue when Desktop is closed.',
    keyFile: 'src/ServerForge.Agent/Program.cs',
  },
  {
    project: 'ServerForge.Deployments',
    target: 'net10.0',
    responsibility:
      '12-state deployment orchestrator with per-app SemaphoreSlim serialization, Zip-Slip protection, SHA-256 verification, health probes, automatic rollback, and 9 stack adapters.',
    keyFile: 'src/ServerForge.Deployments/Engine/DeploymentOrchestrator.cs',
  },
  {
    project: 'ServerForge.IIS',
    target: 'net10.0',
    responsibility:
      'IISAdministration PowerShell & appcmd bridge, pre-existing site read-only safety lock, binding conflict detection, WinSW service management, and win-acme SSL verification.',
    keyFile: 'src/ServerForge.IIS/Management/IisAdministrationService.cs',
  },
  {
    project: 'ServerForge.GitHub',
    target: 'net10.0',
    responsibility:
      'Typed HttpClient for Fine-Grained PAT validation, repository listing/search, conditional ETag branch polling, and GitHub Actions Windows artifact downloads.',
    keyFile: 'src/ServerForge.GitHub/Client/GitHubApiClient.cs',
  },
  {
    project: 'ServerForge.Security',
    target: 'net10.0',
    responsibility:
      'Windows DPAPI encryption, regex + secret redaction, ECDSA P-384 mTLS certificates, expiring one-time pairing tokens, PathBoundaryGuard, and SafeProcessRunner.',
    keyFile: 'src/ServerForge.Security/Certificates/MutualTlsCertificateManager.cs',
  },
  {
    project: 'ServerForge.Databases.*',
    target: 'net10.0 (SqlClient + Npgsql)',
    responsibility:
      'Standalone Microsoft SQL Server and PostgreSQL (local & external managed Neon) discovery, scoped logins, COPY_ONLY/pg_dump backups, and approval-gated restores.',
    keyFile: 'src/ServerForge.Databases.SqlServer/SqlServerManagementService.cs',
  },
  {
    project: 'ServerForge.Monitoring',
    target: 'net10.0',
    responsibility:
      'Real Windows PerformanceCounter CPU, GlobalMemoryStatusEx RAM, DriveInfo disk, ServiceController status, X509Store certificate expiry, and HTTP readiness probes.',
    keyFile: 'src/ServerForge.Monitoring/WindowsTelemetryService.cs',
  },
];

interface ArchitectureOverviewProps {
  onOpenFile: (path: string) => void;
}

export function ArchitectureOverview({ onOpenFile }: ArchitectureOverviewProps) {
  return (
    <div className="space-y-6">
      <div className="border border-slate-200 dark:border-slate-800 rounded-lg bg-white dark:bg-[#101622] overflow-hidden">
        <div className="px-6 py-4 border-b border-slate-200 dark:border-slate-800">
          <h2 className="text-base font-semibold text-slate-900 dark:text-slate-100">
            Native .NET 10 LTS Solution Architecture
          </h2>
          <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
            Twelve source projects and three xUnit test assemblies cleanly separated by domain responsibility. Click any module to inspect its primary implementation file.
          </p>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full text-left border-collapse">
            <thead>
              <tr className="border-b border-slate-200 dark:border-slate-800 text-xs font-medium text-slate-500 dark:text-slate-400 bg-slate-50/70 dark:bg-[#0D121C]">
                <th className="py-3 px-6">Assembly</th>
                <th className="py-3 px-4">Target Framework</th>
                <th className="py-3 px-4">Responsibilities</th>
                <th className="py-3 px-6 text-right">Source</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-200 dark:divide-slate-800 text-sm">
              {MODULES.map((mod) => (
                <tr
                  key={mod.project}
                  className="hover:bg-slate-50/80 dark:hover:bg-slate-800/30 transition-colors"
                >
                  <td className="py-3.5 px-6 font-mono font-medium text-slate-900 dark:text-slate-100 whitespace-nowrap">
                    {mod.project}
                  </td>
                  <td className="py-3.5 px-4 font-mono text-xs text-slate-600 dark:text-slate-400 whitespace-nowrap">
                    {mod.target}
                  </td>
                  <td className="py-3.5 px-4 text-slate-600 dark:text-slate-300 leading-relaxed max-w-xl">
                    {mod.responsibility}
                  </td>
                  <td className="py-3.5 px-6 text-right whitespace-nowrap">
                    <button
                      type="button"
                      onClick={() => onOpenFile(mod.keyFile)}
                      className="text-xs font-medium text-sky-600 dark:text-sky-400 hover:underline whitespace-nowrap"
                    >
                      Inspect file
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
        <div className="p-6 border border-slate-200 dark:border-slate-800 rounded-lg bg-white dark:bg-[#101622] space-y-3">
          <h3 className="text-sm font-semibold text-slate-900 dark:text-slate-100">
            Existing Server Protection Guarantees
          </h3>
          <ul className="space-y-2 text-sm text-slate-600 dark:text-slate-300 list-disc pl-5">
            <li>Pre-existing IIS websites and AppPools default to <span className="font-mono text-xs">ReadOnlyUnmanaged</span>.</li>
            <li>Classic ASP websites are detected automatically and blocked from mutation until explicitly adopted.</li>
            <li>SQL Server &amp; PostgreSQL ports are never opened publicly; restores require explicit operator approval.</li>
            <li>Uninstalling ServerForge Agent stops only <span className="font-mono text-xs">ServerForgeAgent</span> and preserves all hosted apps.</li>
          </ul>
        </div>

        <div className="p-6 border border-slate-200 dark:border-slate-800 rounded-lg bg-white dark:bg-[#101622] space-y-3">
          <h3 className="text-sm font-semibold text-slate-900 dark:text-slate-100">
            Supported Application Deployment Adapters (9 Stacks)
          </h3>
          <p className="text-sm text-slate-600 dark:text-slate-300 leading-relaxed">
            Next.js SSR (standalone + WinSW + IIS ARR) · React/Vite SPA (atomic IIS swap + SPA web.config) · NestJS API · Express API · Generic Node.js · ASP.NET Core (AspNetCoreModuleV2) · Classic ASP (adopt-only preservation) · Python FastAPI (isolated venv + Uvicorn WinSW + IIS ARR) · Static HTML/CSS/JS.
          </p>
          <button
            type="button"
            onClick={() => onOpenFile('src/ServerForge.Deployments/Adapters/RuntimeAdapters.cs')}
            className="text-xs font-medium text-sky-600 dark:text-sky-400 hover:underline"
          >
            Open RuntimeAdapters.cs
          </button>
        </div>
      </div>
    </div>
  );
}
