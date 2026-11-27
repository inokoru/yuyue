#!/usr/bin/env node
/**
 * Convenience launcher for Yuyue Windows Midscene E2E suite.
 *
 * Examples:
 *   node run.mjs --release                    # Run full Release acceptance suite (6 files / 17 cases)
 *   node run.mjs --release --smoke            # Fast smoke (shelf + settings/persistence, 2 files / 5 cases)
 *   node run.mjs --release --settings         # Settings, Companion, Listening, Export, About (3 files / 10 cases)
 *   node run.mjs --debug cases/05-companion-and-listening.yaml
 *   node run.mjs --release --cache            # Enable Midscene planning/locate cache
 */
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, '..', '..');

const rawArgs = process.argv.slice(2);
const env = { ...process.env };
const flagArgs = [];
const fileArgs = [];

let suitePreset = null;
for (let i = 0; i < rawArgs.length; i++) {
  const a = rawArgs[i];
  if (a === '--release') {
    env.YUYUE_BUILD = 'release';
  } else if (a === '--debug') {
    env.YUYUE_BUILD = 'debug';
  } else if (a === '--cache') {
    env.MIDSCENE_CACHE = 'true';
  } else if (a === '--smoke') {
    suitePreset = [
      'cases/01-shelf-smoke.yaml',
      'cases/04-settings-and-persistence.yaml',
    ];
  } else if (a === '--settings') {
    suitePreset = [
      'cases/04-settings-and-persistence.yaml',
      'cases/05-companion-and-listening.yaml',
      'cases/06-export-sync-and-about.yaml',
    ];
  } else if (a.startsWith('-')) {
    flagArgs.push(a);
    // Preserve value for flags like --reporter verbose / --config path / --result-dir dir / --project name
    if (
      !a.includes('=') &&
      ['--config', '--result-dir', '--project', '--reporter'].includes(a) &&
      i + 1 < rawArgs.length
    ) {
      flagArgs.push(rawArgs[++i]);
    }
  } else {
    fileArgs.push(a);
  }
}

const buildMode = (env.YUYUE_BUILD || 'debug').toLowerCase();
const expectedExe =
  env.YUYUE_EXE ||
  path.join(
    REPO_ROOT,
    'windows',
    'Yuyue',
    'bin',
    'x64',
    buildMode === 'release' ? 'Release' : 'Debug',
    'net8.0-windows10.0.22621.0',
    'win-x64',
    'Yuyue.exe',
  );

if (!fs.existsSync(expectedExe)) {
  console.error(
    `未找到 ${buildMode} 版 Yuyue.exe: ${expectedExe}\n` +
      `请先编译: dotnet build windows/Yuyue/Yuyue.csproj -c ${buildMode === 'release' ? 'Release' : 'Debug'} -p:Platform=x64 -p:RuntimeIdentifier=win-x64`,
  );
  process.exit(2);
}

const selectedFiles = fileArgs.length > 0 ? fileArgs : suitePreset || [];
const finalArgs = [...flagArgs];
if (selectedFiles.length === 1) {
  finalArgs.push(selectedFiles[0]);
} else if (selectedFiles.length > 1) {
  // midscene-test CLI accepts at most one positional path; multi-file sets are passed via midscene.config.ts files.include
  env.YUYUE_CASE_FILES = selectedFiles.join(',');
}

const stat = fs.statSync(expectedExe);
console.log(
  `▸ Midscene Target: ${expectedExe}\n` +
    `  Build Mode: ${buildMode.toUpperCase()} | Built At: ${stat.mtime.toLocaleString()} | Cache: ${env.MIDSCENE_CACHE || 'false'}` +
    (selectedFiles.length > 0 ? `\n  Selected Files (${selectedFiles.length}): ${selectedFiles.join(', ')}` : ''),
);

const bin = path.join(
  HERE,
  'node_modules',
  '.bin',
  process.platform === 'win32' ? 'midscene-test.cmd' : 'midscene-test',
);

if (process.platform === 'win32') {
  const retryUrl = pathToFileURL(path.join(HERE, 'win-fs-retry.mjs')).href;
  const importFlag = `--import ${retryUrl}`;
  if (!(env.NODE_OPTIONS || '').includes('win-fs-retry.mjs')) {
    env.NODE_OPTIONS = env.NODE_OPTIONS ? `${env.NODE_OPTIONS} ${importFlag}` : importFlag;
  }
}

const res = spawnSync(bin, finalArgs, {
  cwd: HERE,
  env,
  stdio: 'inherit',
  shell: process.platform === 'win32',
});
process.exit(res.status ?? 1);
