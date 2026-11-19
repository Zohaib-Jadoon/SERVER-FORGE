import { useState } from 'react';
import { Check, Copy, FileCode2, FolderOpen, Search, X } from 'lucide-react';
import {
  detectLanguageLabel,
  filterSolutionFiles,
  formatByteSize,
  groupFilesByCategory,
  type SolutionFileEntry,
} from '../utils/solutionTree';

interface LoadedFile {
  path: string;
  name: string;
  sizeBytes: number;
  lines: number;
  content: string;
}

interface SourceExplorerPaneProps {
  files: SolutionFileEntry[];
  selectedPath: string | null;
  loadedFile: LoadedFile | null;
  isLoadingFile: boolean;
  fileError: string | null;
  onSelectFile: (path: string) => void;
}

const CATEGORY_FILTERS = [
  'All',
  'Desktop (WPF .NET 10)',
  'Agent (Windows Service)',
  'Core & Platform Libraries',
  'Automated xUnit Tests',
  'Inno Setup Installers',
  'Build Scripts & CI',
  'Solution & Docs',
];

export function SourceExplorerPane({
  files,
  selectedPath,
  loadedFile,
  isLoadingFile,
  fileError,
  onSelectFile,
}: SourceExplorerPaneProps) {
  const [query, setQuery] = useState('');
  const [category, setCategory] = useState('All');
  const [copied, setCopied] = useState(false);

  const filtered = filterSolutionFiles(files, query, category);
  const grouped = groupFilesByCategory(filtered);

  const handleCopy = async () => {
    if (!loadedFile) return;
    try {
      await navigator.clipboard.writeText(loadedFile.content);
      setCopied(true);
      setTimeout(() => setCopied(false), 1800);
    } catch {
      setCopied(false);
    }
  };

  return (
    <div className="grid grid-cols-1 lg:grid-cols-[340px_1fr] border border-slate-200 dark:border-slate-800 rounded-lg overflow-hidden bg-white dark:bg-[#101622] min-h-[640px]">
      {/* File Tree Sidebar */}
      <aside
        aria-label="Solution file tree"
        className="border-b lg:border-b-0 lg:border-r border-slate-200 dark:border-slate-800 flex flex-col bg-slate-50/70 dark:bg-[#0D121C]"
      >
        <div className="p-4 border-b border-slate-200 dark:border-slate-800 space-y-3">
          <div className="relative">
            <label htmlFor="solution-file-search" className="sr-only">
              Filter ServerForge source files
            </label>
            <Search
              aria-hidden="true"
              className="w-4 h-4 text-slate-400 absolute left-3 top-1/2 -translate-y-1/2 pointer-events-none"
            />
            <input
              id="solution-file-search"
              type="text"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="Search .cs, .xaml, .iss, .ps1..."
              className="w-full pl-9 pr-8 py-2 text-sm rounded-md bg-white dark:bg-[#141B29] border border-slate-300 dark:border-slate-700 text-slate-900 dark:text-slate-100 placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-sky-500"
            />
            {query && (
              <button
                type="button"
                aria-label="Clear search query"
                onClick={() => setQuery('')}
                className="absolute right-2 top-1/2 -translate-y-1/2 p-1 text-slate-400 hover:text-slate-700 dark:hover:text-slate-200"
              >
                <X className="w-3.5 h-3.5" />
              </button>
            )}
          </div>

          <div className="flex items-center justify-between gap-2">
            <label htmlFor="category-filter" className="text-xs text-slate-500 dark:text-slate-400 whitespace-nowrap">
              Module
            </label>
            <select
              id="category-filter"
              value={category}
              onChange={(e) => setCategory(e.target.value)}
              className="flex-1 text-xs py-1.5 px-2.5 rounded-md bg-white dark:bg-[#141B29] border border-slate-300 dark:border-slate-700 text-slate-800 dark:text-slate-200 focus:outline-none focus:ring-2 focus:ring-sky-500"
            >
              {CATEGORY_FILTERS.map((c) => (
                <option key={c} value={c}>
                  {c}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto max-h-[600px] p-2 space-y-4">
          {grouped.length === 0 ? (
            <div className="p-6 text-center space-y-2">
              <p className="text-sm font-medium text-slate-700 dark:text-slate-300">No matching files</p>
              <p className="text-xs text-slate-500 dark:text-slate-400">
                Try searching for DeploymentOrchestrator, MainWindow, or .iss.
              </p>
              <button
                type="button"
                onClick={() => {
                  setQuery('');
                  setCategory('All');
                }}
                className="text-xs font-medium text-sky-600 dark:text-sky-400 hover:underline"
              >
                Reset filters
              </button>
            </div>
          ) : (
            grouped.map((group) => (
              <div key={group.category}>
                <div className="px-2.5 py-1 flex items-center justify-between text-xs font-semibold text-slate-500 dark:text-slate-400">
                  <span className="flex items-center gap-1.5 truncate">
                    <FolderOpen className="w-3.5 h-3.5 shrink-0" aria-hidden="true" />
                    <span className="truncate">{group.category}</span>
                  </span>
                  <span className="font-mono tabular-nums text-[11px] font-normal">
                    {group.files.length}
                  </span>
                </div>
                <ul className="mt-1 space-y-0.5">
                  {group.files.map((file) => {
                    const active = selectedPath === file.path;
                    return (
                      <li key={file.path}>
                        <button
                          type="button"
                          onClick={() => onSelectFile(file.path)}
                          className={`w-full text-left px-2.5 py-2 rounded-md text-xs transition-colors flex items-center justify-between gap-2 ${
                            active
                              ? 'bg-sky-600 text-white font-medium'
                              : 'text-slate-700 dark:text-slate-300 hover:bg-slate-200/70 dark:hover:bg-slate-800/70'
                          }`}
                        >
                          <span className="flex items-center gap-2 min-w-0">
                            <FileCode2 className="w-3.5 h-3.5 shrink-0 opacity-75" aria-hidden="true" />
                            <span className="truncate font-mono">{file.name}</span>
                          </span>
                          <span
                            className={`font-mono tabular-nums text-[11px] shrink-0 ${
                              active ? 'text-sky-100' : 'text-slate-400 dark:text-slate-500'
                            }`}
                          >
                            {file.lines}L
                          </span>
                        </button>
                      </li>
                    );
                  })}
                </ul>
              </div>
            ))
          )}
        </div>
      </aside>

      {/* Code Viewer */}
      <section aria-label="Source code viewer" className="flex flex-col min-w-0">
        {loadedFile ? (
          <>
            <div className="px-5 py-3.5 border-b border-slate-200 dark:border-slate-800 flex flex-wrap items-center justify-between gap-3 bg-slate-50/40 dark:bg-[#121927]">
              <div className="min-w-0">
                <p className="text-sm font-mono font-medium text-slate-900 dark:text-slate-100 truncate">
                  ServerForge/{loadedFile.path}
                </p>
                <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5 font-mono tabular-nums">
                  {detectLanguageLabel(
                    loadedFile.name.includes('.') ? `.${loadedFile.name.split('.').pop()}` : ''
                  )}{' '}
                  · {loadedFile.lines} lines · {formatByteSize(loadedFile.sizeBytes)}
                </p>
              </div>
              <button
                type="button"
                onClick={handleCopy}
                className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium rounded-md border border-slate-300 dark:border-slate-700 bg-white dark:bg-[#182234] text-slate-800 dark:text-slate-200 hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors whitespace-nowrap shrink-0"
              >
                {copied ? <Check className="w-3.5 h-3.5 text-emerald-500" /> : <Copy className="w-3.5 h-3.5" />}
                <span>{copied ? 'Copied' : 'Copy Source'}</span>
              </button>
            </div>

            <div className="flex-1 overflow-auto max-h-[600px] p-4 bg-[#F8FAFC] dark:bg-[#0B0F17]">
              <pre className="text-xs leading-relaxed font-mono text-slate-800 dark:text-slate-200">
                <code>
                  {loadedFile.content.split(/\r?\n/).map((line, idx) => (
                    <div key={idx} className="table-row hover:bg-slate-200/40 dark:hover:bg-slate-800/40">
                      <span className="table-cell select-none pr-4 text-right text-slate-400 dark:text-slate-600 w-10 tabular-nums">
                        {idx + 1}
                      </span>
                      <span className="table-cell whitespace-pre">{line || ' '}</span>
                    </div>
                  ))}
                </code>
              </pre>
            </div>
          </>
        ) : isLoadingFile ? (
          <div className="flex-1 flex items-center justify-center p-10 text-sm text-slate-500 dark:text-slate-400">
            Loading source file...
          </div>
        ) : fileError ? (
          <div className="flex-1 flex items-center justify-center p-10 text-sm text-red-600 dark:text-red-400">
            {fileError}
          </div>
        ) : (
          <div className="flex-1 flex items-center justify-center p-10 text-sm text-slate-500 dark:text-slate-400">
            Select a file from the ServerForge solution tree to inspect its native C#, XAML, or Inno Setup source code.
          </div>
        )}
      </section>
    </div>
  );
}
