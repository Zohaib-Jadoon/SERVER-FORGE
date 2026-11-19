export interface SolutionFileEntry {
  path: string;
  name: string;
  sizeBytes: number;
  lines: number;
  extension: string;
  category: string;
}

export interface CategoryGroup {
  category: string;
  files: SolutionFileEntry[];
  totalLines: number;
}

const CATEGORY_ORDER = [
  'Solution & Docs',
  'Desktop (WPF .NET 10)',
  'Agent (Windows Service)',
  'Core & Platform Libraries',
  'Automated xUnit Tests',
  'Inno Setup Installers',
  'Build Scripts & CI',
];

export function filterSolutionFiles(
  files: readonly SolutionFileEntry[],
  query: string,
  selectedCategory: string
): SolutionFileEntry[] {
  const normalizedQuery = query.trim().toLowerCase();
  return files.filter((file) => {
    if (selectedCategory !== 'All' && file.category !== selectedCategory) {
      return false;
    }
    if (!normalizedQuery) {
      return true;
    }
    return (
      file.path.toLowerCase().includes(normalizedQuery) ||
      file.name.toLowerCase().includes(normalizedQuery) ||
      file.category.toLowerCase().includes(normalizedQuery)
    );
  });
}

export function groupFilesByCategory(files: readonly SolutionFileEntry[]): CategoryGroup[] {
  const map = new Map<string, SolutionFileEntry[]>();
  for (const file of files) {
    const existing = map.get(file.category);
    if (existing) {
      existing.push(file);
    } else {
      map.set(file.category, [file]);
    }
  }

  return Array.from(map.entries())
    .sort(([a], [b]) => {
      const ia = CATEGORY_ORDER.indexOf(a);
      const ib = CATEGORY_ORDER.indexOf(b);
      if (ia === -1 && ib === -1) return a.localeCompare(b);
      if (ia === -1) return 1;
      if (ib === -1) return -1;
      return ia - ib;
    })
    .map(([category, groupFiles]) => ({
      category,
      files: groupFiles.sort((a, b) => a.path.localeCompare(b.path)),
      totalLines: groupFiles.reduce((sum, item) => sum + item.lines, 0),
    }));
}

export function formatByteSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}

export function detectLanguageLabel(extension: string): string {
  switch (extension.toLowerCase()) {
    case '.cs':
      return 'C# 14 (.NET 10)';
    case '.xaml':
      return 'WPF XAML';
    case '.csproj':
    case '.props':
    case '.manifest':
      return 'MSBuild XML';
    case '.sln':
      return 'Visual Studio Solution';
    case '.iss':
      return 'Inno Setup Script';
    case '.ps1':
      return 'PowerShell';
    case '.yml':
    case '.yaml':
      return 'GitHub Actions YAML';
    case '.json':
      return 'JSON';
    case '.md':
      return 'Markdown';
    default:
      return 'Plain Text';
  }
}
