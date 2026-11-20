import express from 'express';
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { createServer as createViteServer } from 'vite';

const PORT = 3000;
const ROOT_DIR = process.cwd();
const SOLUTION_DIR = path.resolve(ROOT_DIR, 'ServerForge');

interface SolutionFileEntry {
  path: string;
  name: string;
  sizeBytes: number;
  lines: number;
  extension: string;
  category: string;
}

function classifyCategory(relPath: string): string {
  if (relPath.startsWith('src/ServerForge.Desktop/')) return 'Desktop (WPF .NET 10)';
  if (relPath.startsWith('src/ServerForge.Agent/')) return 'Agent (Windows Service)';
  if (relPath.startsWith('src/')) return 'Core & Platform Libraries';
  if (relPath.startsWith('tests/')) return 'Automated xUnit Tests';
  if (relPath.startsWith('installers/')) return 'Inno Setup Installers';
  if (relPath.startsWith('scripts/') || relPath.startsWith('.github/')) return 'Build Scripts & CI';
  return 'Solution & Docs';
}

const ROOT_GITHUB_WORKFLOWS_DIR = path.resolve(ROOT_DIR, '.github', 'workflows');

function walkSolution(dir: string, base: string, acc: SolutionFileEntry[], prefix = ''): void {
  if (!fs.existsSync(dir)) return;
  const entries = fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name));
  for (const entry of entries) {
    if (entry.name === 'bin' || entry.name === 'obj' || entry.name === 'artifacts') continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      walkSolution(full, base, acc, prefix);
    } else if (entry.isFile()) {
      const relRaw = path.relative(base, full).split(path.sep).join('/');
      const rel = prefix ? `${prefix}/${relRaw}` : relRaw;
      const stat = fs.statSync(full);
      const raw = fs.readFileSync(full, 'utf8');
      const lines = raw.length === 0 ? 0 : raw.split(/\r?\n/).length;
      acc.push({
        path: rel,
        name: entry.name,
        sizeBytes: stat.size,
        lines,
        extension: path.extname(entry.name).toLowerCase(),
        category: classifyCategory(rel),
      });
    }
  }
}

async function startServer() {
  const app = express();

  app.get('/api/solution/files', (_req, res) => {
    const files: SolutionFileEntry[] = [];
    walkSolution(ROOT_GITHUB_WORKFLOWS_DIR, ROOT_GITHUB_WORKFLOWS_DIR, files, '.github/workflows');
    walkSolution(SOLUTION_DIR, SOLUTION_DIR, files);
    res.json({
      solutionRoot: 'ServerForge',
      hostPlatform: process.platform,
      dotnetAvailable: false,
      totalFiles: files.length,
      totalLines: files.reduce((sum, f) => sum + f.lines, 0),
      totalBytes: files.reduce((sum, f) => sum + f.sizeBytes, 0),
      files,
    });
  });

  app.get('/api/solution/file', (req, res) => {
    const requested = typeof req.query.path === 'string' ? req.query.path : '';
    if (!requested || requested.includes('..') || path.isAbsolute(requested)) {
      res.status(400).json({ error: 'Invalid relative file path.' });
      return;
    }

    let resolved: string;
    if (requested.startsWith('.github/workflows/')) {
      const workflowRel = requested.slice('.github/workflows/'.length);
      resolved = path.resolve(ROOT_GITHUB_WORKFLOWS_DIR, workflowRel);
      if (!resolved.startsWith(ROOT_GITHUB_WORKFLOWS_DIR + path.sep)) {
        res.status(400).json({ error: 'Invalid workflow file path.' });
        return;
      }
    } else {
      resolved = path.resolve(SOLUTION_DIR, requested);
      if (!resolved.startsWith(SOLUTION_DIR + path.sep)) {
        res.status(404).json({ error: 'File not found inside ServerForge solution.' });
        return;
      }
    }

    if (!fs.existsSync(resolved) || !fs.statSync(resolved).isFile()) {
      res.status(404).json({ error: 'File not found inside ServerForge solution.' });
      return;
    }

    const content = fs.readFileSync(resolved, 'utf8');
    res.json({
      path: requested,
      name: path.basename(resolved),
      sizeBytes: Buffer.byteLength(content, 'utf8'),
      lines: content.split(/\r?\n/).length,
      content,
    });
  });

  app.get('/api/solution/download', (_req, res) => {
    if (!fs.existsSync(SOLUTION_DIR)) {
      res.status(404).json({ error: 'ServerForge directory not found.' });
      return;
    }
    res.setHeader('Content-Type', 'application/gzip');
    res.setHeader('Content-Disposition', 'attachment; filename="ServerForge-Windows-NET10-Solution.tar.gz"');
    const archiveItems = fs.existsSync(ROOT_GITHUB_WORKFLOWS_DIR)
      ? ['.github', 'ServerForge']
      : ['ServerForge'];
    const tar = spawn('tar', ['-czf', '-', '-C', ROOT_DIR, ...archiveItems]);
    tar.stdout.pipe(res);
    tar.on('error', () => {
      if (!res.headersSent) res.status(500).end('Archive generation failed.');
    });
  });

  const isProd = process.env.NODE_ENV === 'production';
  if (!isProd) {
    const vite = await createViteServer({
      server: { middlewareMode: true },
      appType: 'spa',
    });
    app.use(vite.middlewares);
  } else {
    const distDir = path.resolve(ROOT_DIR, 'dist');
    app.use(express.static(distDir));
    app.get('*', (_req, res) => {
      res.sendFile(path.join(distDir, 'index.html'));
    });
  }

  app.listen(PORT, '0.0.0.0', () => {
    console.log(`ServerForge Source Explorer listening on http://localhost:${PORT}`);
  });
}

startServer();
