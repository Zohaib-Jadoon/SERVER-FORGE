import test from 'node:test';
import assert from 'node:assert/strict';
import {
  detectLanguageLabel,
  filterSolutionFiles,
  formatByteSize,
  groupFilesByCategory,
  type SolutionFileEntry,
} from './solutionTree';

const SAMPLE_FILES: SolutionFileEntry[] = [
  {
    path: 'installers/Desktop/ServerForge-Desktop.iss',
    name: 'ServerForge-Desktop.iss',
    sizeBytes: 2100,
    lines: 64,
    extension: '.iss',
    category: 'Inno Setup Installers',
  },
  {
    path: 'ServerForge.sln',
    name: 'ServerForge.sln',
    sizeBytes: 5400,
    lines: 120,
    extension: '.sln',
    category: 'Solution & Docs',
  },
  {
    path: 'src/ServerForge.Desktop/Views/MainWindow.xaml',
    name: 'MainWindow.xaml',
    sizeBytes: 6200,
    lines: 148,
    extension: '.xaml',
    category: 'Desktop (WPF .NET 10)',
  },
  {
    path: 'src/ServerForge.Agent/Program.cs',
    name: 'Program.cs',
    sizeBytes: 7900,
    lines: 215,
    extension: '.cs',
    category: 'Agent (Windows Service)',
  },
];

test('filterSolutionFiles matches case-insensitive path and extension queries', () => {
  const issMatches = filterSolutionFiles(SAMPLE_FILES, '.iss', 'All');
  assert.equal(issMatches.length, 1);
  assert.equal(issMatches[0].name, 'ServerForge-Desktop.iss');

  const categoryMatches = filterSolutionFiles(SAMPLE_FILES, '', 'Desktop (WPF .NET 10)');
  assert.equal(categoryMatches.length, 1);
  assert.equal(categoryMatches[0].name, 'MainWindow.xaml');
});

test('groupFilesByCategory orders groups canonically and aggregates line counts', () => {
  const groups = groupFilesByCategory(SAMPLE_FILES);
  assert.deepEqual(
    groups.map((g) => g.category),
    [
      'Solution & Docs',
      'Desktop (WPF .NET 10)',
      'Agent (Windows Service)',
      'Inno Setup Installers',
    ]
  );
  assert.equal(groups[0].totalLines, 120);
  assert.equal(groups[2].totalLines, 215);
});

test('formatByteSize and detectLanguageLabel return readable values', () => {
  assert.equal(formatByteSize(512), '512 B');
  assert.equal(formatByteSize(2048), '2.0 KB');
  assert.equal(detectLanguageLabel('.cs'), 'C# 14 (.NET 10)');
  assert.equal(detectLanguageLabel('.iss'), 'Inno Setup Script');
});
