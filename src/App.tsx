import { useEffect, useState } from 'react';
import { Download, Moon, Sun } from 'lucide-react';
import { ArchitectureOverview } from './components/ArchitectureOverview';
import { SourceExplorerPane } from './components/SourceExplorerPane';
import { WindowsBuildGuide } from './components/WindowsBuildGuide';
import { formatByteSize, type SolutionFileEntry } from './utils/solutionTree';

type ActiveTab = 'explorer' | 'architecture' | 'build';

interface SolutionManifest {
  solutionRoot: string;
  hostPlatform: string;
  dotnetAvailable: boolean;
  totalFiles: number;
  totalLines: number;
  totalBytes: number;
  files: SolutionFileEntry[];
}

interface LoadedFile {
  path: string;
  name: string;
  sizeBytes: number;
  lines: number;
  content: string;
}

export default function App() {
  const [darkMode, setDarkMode] = useState(true);
  const [activeTab, setActiveTab] = useState<ActiveTab>('explorer');
  const [manifest, setManifest] = useState<SolutionManifest | null>(null);
  const [manifestError, setManifestError] = useState<string | null>(null);
  const [selectedPath, setSelectedPath] = useState<string | null>(null);
  const [loadedFile, setLoadedFile] = useState<LoadedFile | null>(null);
  const [isLoadingFile, setIsLoadingFile] = useState(false);
  const [fileError, setFileError] = useState<string | null>(null);

  useEffect(() => {
    document.documentElement.classList.toggle('dark', darkMode);
  }, [darkMode]);

  useEffect(() => {
    let cancelled = false;
    fetch('/api/solution/files')
      .then((res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json() as Promise<SolutionManifest>;
      })
      .then((data) => {
        if (cancelled) return;
        setManifest(data);
        const initialFile =
          data.files.find((f) => f.path === 'ServerForge.sln') ??
          data.files.find((f) => f.path === 'README.md') ??
          data.files[0];
        if (initialFile) {
          void loadFile(initialFile.path);
        }
      })
      .catch((err: Error) => {
        if (!cancelled) setManifestError(`Could not read ServerForge solution manifest: ${err.message}`);
      });

    return () => {
      cancelled = true;
    };
  }, []);

  const loadFile = async (relPath: string) => {
    setSelectedPath(relPath);
    setIsLoadingFile(true);
    setFileError(null);
    try {
      const res = await fetch(`/api/solution/file?path=${encodeURIComponent(relPath)}`);
      if (!res.ok) throw new Error(`Unable to read ${relPath} (HTTP ${res.status})`);
      const data = (await res.json()) as LoadedFile;
      setLoadedFile(data);
    } catch (err) {
      setLoadedFile(null);
      setFileError(err instanceof Error ? err.message : 'Failed to load file.');
    } finally {
      setIsLoadingFile(false);
    }
  };

  const handleOpenFileFromSubView = (relPath: string) => {
    setActiveTab('explorer');
    void loadFile(relPath);
  };

  return (
    <div className="min-h-screen bg-[#F8FAFC] dark:bg-[#0B0F17] text-slate-900 dark:text-slate-100 flex flex-col">
      {/* Three-Zone Top Bar */}
      <header className="border-b border-slate-200 dark:border-slate-800 bg-white dark:bg-[#0E1420]">
        <div className="max-w-[1440px] mx-auto px-6 h-16 flex items-center justify-between gap-8">
          <a
            href="#top"
            onClick={(e) => {
              e.preventDefault();
              setActiveTab('explorer');
            }}
            className="text-lg font-bold tracking-tight text-slate-900 dark:text-white whitespace-nowrap shrink-0"
          >
            ServerForge
          </a>

          <nav aria-label="Workspace views" className="flex items-center gap-6 text-sm font-medium">
            <button
              type="button"
              onClick={() => setActiveTab('explorer')}
              className={`py-1 border-b-2 transition-colors whitespace-nowrap shrink-0 ${
                activeTab === 'explorer'
                  ? 'border-sky-600 dark:border-sky-400 text-slate-900 dark:text-white'
                  : 'border-transparent text-slate-600 dark:text-slate-400 hover:text-slate-900 dark:hover:text-slate-200'
              }`}
            >
              Solution Explorer
            </button>
            <button
              type="button"
              onClick={() => setActiveTab('architecture')}
              className={`py-1 border-b-2 transition-colors whitespace-nowrap shrink-0 ${
                activeTab === 'architecture'
                  ? 'border-sky-600 dark:border-sky-400 text-slate-900 dark:text-white'
                  : 'border-transparent text-slate-600 dark:text-slate-400 hover:text-slate-900 dark:hover:text-slate-200'
              }`}
            >
              Architecture
            </button>
            <button
              type="button"
              onClick={() => setActiveTab('build')}
              className={`py-1 border-b-2 transition-colors whitespace-nowrap shrink-0 ${
                activeTab === 'build'
                  ? 'border-sky-600 dark:border-sky-400 text-slate-900 dark:text-white'
                  : 'border-transparent text-slate-600 dark:text-slate-400 hover:text-slate-900 dark:hover:text-slate-200'
              }`}
            >
              Windows Installers
            </button>
          </nav>

          <a
            href="/api/solution/download"
            download="ServerForge-Windows-NET10-Solution.tar.gz"
            className="inline-flex items-center gap-2 px-4 py-2 text-xs font-semibold rounded-md bg-sky-600 hover:bg-sky-700 text-white transition-colors whitespace-nowrap shrink-0"
          >
            <Download className="w-3.5 h-3.5" aria-hidden="true" />
            <span>Download .NET Solution</span>
          </a>
        </div>
      </header>

      {/* Main Content */}
      <main id="top" className="flex-1 max-w-[1440px] w-full mx-auto px-6 py-6 space-y-6">
        {/* Contextual Summary Row */}
        <section className="flex flex-col md:flex-row md:items-end justify-between gap-4 pb-5 border-b border-slate-200 dark:border-slate-800">
          <div className="space-y-1.5 max-w-3xl">
            <h1 className="text-2xl font-bold tracking-tight text-slate-900 dark:text-white text-balance">
              Native Windows C# / .NET 10 Solution Repository
            </h1>
            <p className="text-sm text-slate-600 dark:text-slate-400 leading-relaxed">
              Read-only source browser for the generated <span className="font-mono text-xs text-slate-800 dark:text-slate-200">/ServerForge</span> Visual Studio solution (WPF Desktop + Windows Service Agent + IIS/GitHub/Database engines + Inno Setup scripts). This web viewer inspects the native codebase and is not a web substitute for ServerForge Desktop.
            </p>
          </div>

          <div className="flex items-center gap-5 shrink-0">
            {manifest && (
              <div className="text-xs text-slate-500 dark:text-slate-400 font-mono tabular-nums">
                <span>{manifest.totalFiles} files</span>
                <span aria-hidden="true"> · </span>
                <span>{manifest.totalLines.toLocaleString()} lines</span>
                <span aria-hidden="true"> · </span>
                <span>{formatByteSize(manifest.totalBytes)}</span>
              </div>
            )}
            <button
              type="button"
              onClick={() => setDarkMode((prev) => !prev)}
              aria-label={darkMode ? 'Switch to light theme' : 'Switch to dark theme'}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium rounded-md border border-slate-300 dark:border-slate-700 bg-white dark:bg-[#141B29] text-slate-700 dark:text-slate-200 hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors whitespace-nowrap shrink-0"
            >
              {darkMode ? <Sun className="w-3.5 h-3.5" /> : <Moon className="w-3.5 h-3.5" />}
              <span>{darkMode ? 'Light' : 'Dark'}</span>
            </button>
          </div>
        </section>

        {manifestError ? (
          <div className="p-6 border border-red-300 dark:border-red-900 rounded-lg bg-red-50 dark:bg-red-950/30 text-sm text-red-700 dark:text-red-300">
            {manifestError}
          </div>
        ) : activeTab === 'explorer' ? (
          <SourceExplorerPane
            files={manifest?.files ?? []}
            selectedPath={selectedPath}
            loadedFile={loadedFile}
            isLoadingFile={isLoadingFile}
            fileError={fileError}
            onSelectFile={loadFile}
          />
        ) : activeTab === 'architecture' ? (
          <ArchitectureOverview onOpenFile={handleOpenFileFromSubView} />
        ) : (
          <WindowsBuildGuide onOpenFile={handleOpenFileFromSubView} />
        )}
      </main>
    </div>
  );
}
