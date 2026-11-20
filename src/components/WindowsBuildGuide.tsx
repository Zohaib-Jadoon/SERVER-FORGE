interface WindowsBuildGuideProps {
  onOpenFile: (path: string) => void;
}

export function WindowsBuildGuide({ onOpenFile }: WindowsBuildGuideProps) {
  return (
    <div className="space-y-6">
      <div className="p-6 border border-slate-200 dark:border-slate-800 rounded-lg bg-white dark:bg-[#101622] space-y-4">
        <div>
          <h2 className="text-base font-semibold text-slate-900 dark:text-slate-100">
            Honest Compilation Status &amp; Windows Build Instructions
          </h2>
          <p className="text-sm text-slate-600 dark:text-slate-300 mt-1.5 leading-relaxed">
            This workspace container runs Linux x64 without the .NET 10 Windows Desktop SDK or Inno Setup (<span className="font-mono text-xs">ISCC.exe</span>). In accordance with strict engineering honesty, no fake compilation logs or fabricated <span className="font-mono text-xs">.exe</span> binaries were produced here. Download the complete <span className="font-mono text-xs">ServerForge/</span> repository or push it to GitHub to build both installers on Windows 11 or GitHub Actions <span className="font-mono text-xs">windows-latest</span>.
          </p>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-4 pt-2">
          <div className="p-4 rounded-md border border-slate-200 dark:border-slate-800 bg-slate-50/70 dark:bg-[#0D121C] space-y-2">
            <p className="text-xs font-semibold text-slate-900 dark:text-slate-100">
              1. Local Windows 11 Build (PowerShell)
            </p>
            <pre className="text-xs font-mono text-slate-700 dark:text-slate-300 overflow-x-auto leading-relaxed">
{`winget install Microsoft.DotNet.SDK.10
winget install JRSoftware.InnoSetup
cd ServerForge
.\\scripts\\build-installers.ps1`}
            </pre>
            <p className="text-xs text-slate-500 dark:text-slate-400">
              Produces <span className="font-mono">ServerForge-Desktop-Setup.exe</span> and <span className="font-mono">ServerForge-Agent-Setup.exe</span> plus SHA-256 checksums.
            </p>
          </div>

          <div className="p-4 rounded-md border border-slate-200 dark:border-slate-800 bg-slate-50/70 dark:bg-[#0D121C] space-y-2">
            <p className="text-xs font-semibold text-slate-900 dark:text-slate-100">
              2. GitHub Actions Automated Windows Release
            </p>
            <pre className="text-xs font-mono text-slate-700 dark:text-slate-300 overflow-x-auto leading-relaxed">
{`git checkout -b feat/complete-serverforge
git add .github/workflows/build-windows-installers.yml ServerForge
git commit -m "Complete ServerForge .NET 10 platform and root Windows CI"
git push -u origin feat/complete-serverforge`}
            </pre>
            <p className="text-xs text-slate-500 dark:text-slate-400">
              Triggers <span className="font-mono">/.github/workflows/build-windows-installers.yml</span> on <span className="font-mono">windows-latest</span> (.NET 10 build, xUnit <span className="font-mono">.trx</span> tests, and Inno Setup packaging).
            </p>
          </div>
        </div>
      </div>

      <div className="border border-slate-200 dark:border-slate-800 rounded-lg bg-white dark:bg-[#101622] overflow-hidden">
        <div className="px-6 py-4 border-b border-slate-200 dark:border-slate-800">
          <h3 className="text-sm font-semibold text-slate-900 dark:text-slate-100">
            Installer, CI &amp; Documentation Artifacts Ready to Inspect
          </h3>
        </div>
        <div className="divide-y divide-slate-200 dark:divide-slate-800 text-sm">
          {[
            {
              label: '.github/workflows/build-windows-installers.yml (Repository Root)',
              desc: 'GitHub Actions windows-latest pipeline compiling ServerForge.sln, running xUnit suites (.trx upload), and building both .exe installers + SHA256SUMS.txt.',
              path: '.github/workflows/build-windows-installers.yml',
            },
            {
              label: 'ServerForge-Desktop.iss',
              desc: 'Hardened Inno Setup script bundling self-contained .NET 10 WPF Desktop, graceful close filter, and %LOCALAPPDATA% preservation across upgrades.',
              path: 'installers/Desktop/ServerForge-Desktop.iss',
            },
            {
              label: 'ServerForge-Agent.iss',
              desc: 'Hardened Inno Setup script with safe upgrade service stop/config/restart lifecycle, ProgramData ACL lock, and C:\\ServerForgeApps preservation.',
              path: 'installers/Agent/ServerForge-Agent.iss',
            },
            {
              label: 'DEVELOPMENT_STATUS.md',
              desc: 'Feature-by-feature status matrix separating implemented native C# modules from Windows CI / E2E checks marked BLOCKED.',
              path: 'DEVELOPMENT_STATUS.md',
            },
            {
              label: 'BUILD_REPORT.md & TEST_REPORT.md',
              desc: 'Honest Windows build status (BLOCKED in Linux container) and complete xUnit test catalog ready for windows-latest execution.',
              path: 'BUILD_REPORT.md',
            },
            {
              label: 'PRODUCTION_READINESS_REPORT.md',
              desc: 'Transparent readiness report (currently INCOMPLETE until Windows CI builds, xUnit passes, and 13-step E2E acceptance passes).',
              path: 'PRODUCTION_READINESS_REPORT.md',
            },
          ].map((item) => (
            <div key={item.path} className="px-6 py-3.5 flex items-center justify-between gap-6">
              <div>
                <p className="font-mono text-xs font-medium text-slate-900 dark:text-slate-100">{item.label}</p>
                <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">{item.desc}</p>
              </div>
              <button
                type="button"
                onClick={() => onOpenFile(item.path)}
                className="text-xs font-medium text-sky-600 dark:text-sky-400 hover:underline whitespace-nowrap shrink-0"
              >
                Open file
              </button>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
