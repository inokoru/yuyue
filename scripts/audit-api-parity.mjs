#!/usr/bin/env node
/**
 * Cross-platform API & Reader Bridge parity auditor.
 *
 * Scans:
 *  1. Every `#[uniffi::export]` method on `YuyueCore` in `core/yuyue-core/src/`
 *     -> checks whether Apple (Swift camelCase), Android (Kotlin camelCase), and
 *        Windows (C# PascalCase) actually call it.
 *  2. Every public `window.yy.<method>` API in `reader/src/main.ts`
 *     -> checks whether Apple, Android, and Windows actually invoke it.
 *  3. Every Reader -> Native message type in `reader/schema/bridge-messages.schema.json`
 *     -> checks whether Apple, Android, and Windows actually handle it.
 *
 * Usage:
 *   node scripts/audit-api-parity.mjs
 *   node scripts/audit-api-parity.mjs --json
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const JSON_OUT = process.argv.includes('--json');

function walk(dir, ext, ignoreDirs = new Set(['bin', 'obj', 'build', 'target', 'Generated', 'node_modules', 'prototype', 'prototype-win11'])) {
  const out = [];
  if (!fs.existsSync(dir)) return out;
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    if (ignoreDirs.has(ent.name)) continue;
    const full = path.join(dir, ent.name);
    if (ent.isDirectory()) out.push(...walk(full, ext, ignoreDirs));
    else if (ent.name.endsWith(ext)) out.push(full);
  }
  return out;
}

function readCorpus(dir, ext) {
  return walk(dir, ext)
    .map((f) => fs.readFileSync(f, 'utf8'))
    .join('\n');
}

function snakeToCamel(s) {
  return s.replace(/_([a-z0-9])/g, (_, c) => c.toUpperCase());
}

function snakeToPascal(s) {
  const c = snakeToCamel(s);
  return c.charAt(0).toUpperCase() + c.slice(1);
}

/** Extract `pub fn <name>(` inside `#[uniffi::export] impl YuyueCore { ... }` blocks. */
function extractUniffiCoreMethods() {
  const files = walk(path.join(ROOT, 'core', 'yuyue-core', 'src'), '.rs');
  const methods = new Map(); // snake_name -> relFile

  for (const file of files) {
    const src = fs.readFileSync(file, 'utf8');
    const rel = path.relative(path.join(ROOT, 'core', 'yuyue-core', 'src'), file).replace(/\\/g, '/');
    const re = /#\[uniffi::export\]\s*impl\s+YuyueCore\s*\{/g;
    let m;
    while ((m = re.exec(src)) !== null) {
      let depth = 1;
      let i = m.index + m[0].length;
      while (i < src.length && depth > 0) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}') depth--;
        i++;
      }
      const block = src.slice(m.index + m[0].length, i - 1);
      const fnRe = /pub\s+fn\s+([a-z0-9_]+)\s*\(/g;
      let fm;
      while ((fm = fnRe.exec(block)) !== null) {
        methods.set(fm[1], rel);
      }
    }
  }
  return [...methods.entries()].sort((a, b) => a[0].localeCompare(b[0]));
}

/** Extract public `window.yy.<method>` calls from `reader/src/main.ts`. */
function extractReaderApiMethods() {
  const src = fs.readFileSync(path.join(ROOT, 'reader', 'src', 'main.ts'), 'utf8');
  const start = src.indexOf('const api = {');
  const end = src.indexOf('declare global', start);
  const block = src.slice(start, end);
  // Stop before debug-only helper comment
  const cut = block.indexOf('// Not part of the contract');
  const contractBlock = cut > 0 ? block.slice(0, cut) : block;
  const methods = [];
  const re = /^\s{2}([a-zA-Z0-9_]+)\s*\(/gm;
  let m;
  while ((m = re.exec(contractBlock)) !== null) {
    methods.push(m[1]);
  }
  return [...new Set(methods)].sort();
}

function main() {
  const apple = readCorpus(path.join(ROOT, 'apple', 'Yuyue'), '.swift');
  const android = readCorpus(path.join(ROOT, 'android', 'app', 'src', 'main', 'java', 'app', 'yuyue'), '.kt');
  const windows = readCorpus(path.join(ROOT, 'windows', 'Yuyue'), '.cs');

  const hasIdent = (corpus, name) => new RegExp(`\\b${name}\\b`).test(corpus);

  // 1. Core UniFFI methods
  const coreMethods = extractUniffiCoreMethods();
  const coreRows = coreMethods.map(([snake, file]) => {
    const camel = snakeToCamel(snake);
    const pascal = snakeToPascal(snake);
    return {
      method: snake,
      module: file,
      apple: hasIdent(apple, camel),
      android: hasIdent(android, camel),
      windows: hasIdent(windows, pascal),
    };
  });

  // 2. Reader JS API (`yy.<fn>`)
  const readerMethods = extractReaderApiMethods();
  const readerRows = readerMethods.map((m) => ({
    method: `yy.${m}`,
    apple: apple.includes(`yy.${m}(`) || apple.includes(`.${m}(`),
    android: android.includes(`yy.${m}(`) || android.includes(`.${m}(`),
    windows: windows.includes(`yy.${m}(`) || windows.includes(`.${m}(`),
  }));

  // 3. Reader -> Native bridge messages
  const schema = JSON.parse(
    fs.readFileSync(path.join(ROOT, 'reader', 'schema', 'bridge-messages.schema.json'), 'utf8'),
  );
  const appleSectionPage = fs.readFileSync(
    path.join(ROOT, 'apple', 'Yuyue', 'Reader', 'SectionPage.swift'),
    'utf8',
  );
  const appleKindMatch = /enum\s+Kind\s*:\s*String[^{]*\{([\s\S]*?)\}/.exec(appleSectionPage);
  const appleKindBlock = appleKindMatch ? appleKindMatch[1] : '';
  const androidPager = fs.readFileSync(
    path.join(ROOT, 'android', 'app', 'src', 'main', 'java', 'app', 'yuyue', 'reader', 'ReaderPager.kt'),
    'utf8',
  );
  const windowsBridge =
    fs.readFileSync(path.join(ROOT, 'windows', 'Yuyue', 'UI', 'Reader', 'ReaderPaneView.Bridge.cs'), 'utf8') +
    '\n' +
    fs.readFileSync(path.join(ROOT, 'windows', 'Yuyue', 'Reader', 'ReaderPagerHost.cs'), 'utf8') +
    '\n' +
    fs.readFileSync(path.join(ROOT, 'windows', 'Yuyue', 'Reader', 'ReaderWebViewHost.cs'), 'utf8');

  const bridgeRows = schema['x-message-types'].map((t) => ({
    type: t,
    apple: new RegExp(`\\b${t}\\b`).test(appleKindBlock),
    android: androidPager.includes(`"${t}"`),
    windows: windowsBridge.includes(`"${t}"`),
  }));

  if (JSON_OUT) {
    console.log(JSON.stringify({ coreRows, readerRows, bridgeRows }, null, 2));
    return;
  }

  const countTrue = (rows, k) => rows.filter((r) => r[k]).length;
  console.log(`=== 1. YuyueCore UniFFI API Coverage (${coreRows.length} exported methods) ===`);
  console.log(
    `Apple: ${countTrue(coreRows, 'apple')}/${coreRows.length} | Android: ${countTrue(coreRows, 'android')}/${coreRows.length} | Windows: ${countTrue(coreRows, 'windows')}/${coreRows.length}`,
  );
  const coreGaps = coreRows.filter((r) => (r.apple || r.android || r.windows) && !(r.apple && r.android && r.windows));
  console.log(`\nMethods used on some platforms but NOT all three (${coreGaps.length}):`);
  for (const r of coreGaps) {
    const flags = `${r.apple ? 'A' : '-'} ${r.android ? 'K' : '-'} ${r.windows ? 'W' : '-'}`;
    console.log(`  [${flags}] ${r.method.padEnd(34)} (${r.module})`);
  }

  const unusedCore = coreRows.filter((r) => !r.apple && !r.android && !r.windows);
  if (unusedCore.length) {
    console.log(`\nExported core methods unused by all 3 apps (${unusedCore.length}): ${unusedCore.map((r) => r.method).join(', ')}`);
  }

  console.log(`\n=== 2. Reader JS API Coverage (${readerRows.length} window.yy.* methods) ===`);
  console.log(
    `Apple: ${countTrue(readerRows, 'apple')}/${readerRows.length} | Android: ${countTrue(readerRows, 'android')}/${readerRows.length} | Windows: ${countTrue(readerRows, 'windows')}/${readerRows.length}`,
  );
  for (const r of readerRows.filter((x) => !(x.apple && x.android && x.windows))) {
    const flags = `${r.apple ? 'A' : '-'} ${r.android ? 'K' : '-'} ${r.windows ? 'W' : '-'}`;
    console.log(`  [${flags}] ${r.method}`);
  }

  console.log(`\n=== 3. Reader -> Native Bridge Messages (${bridgeRows.length} schema types) ===`);
  for (const r of bridgeRows) {
    const flags = `${r.apple ? 'A' : '-'} ${r.android ? 'K' : '-'} ${r.windows ? 'W' : '-'}`;
    console.log(`  [${flags}] ${r.type}`);
  }
}

main();
