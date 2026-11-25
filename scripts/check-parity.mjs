#!/usr/bin/env node
/**
 * Layer 1 CI gate: validates docs/parity.yaml.
 *
 * Rules enforced:
 *  1. Valid structure, unique feature IDs, all 4 platforms (mac, ios, android, windows) present.
 *  2. Every platform entry has status in {done, partial, planned, na}.
 *  3. status == "done" MUST declare at least one test reference in `tests`.
 *  4. status in {partial, planned, na} MUST include a non-empty `note` explaining the gap/reason.
 *  5. Every declared test reference MUST resolve to an existing file (and symbol, if `::symbol` is given).
 *
 * Usage:
 *   node scripts/check-parity.mjs              # validate and print summary matrix
 *   node scripts/check-parity.mjs --strict-auto # also fail if any `done` item has only `manual:` tests
 */

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PARITY_PATH = path.join(ROOT, 'docs', 'parity.yaml');
const PLATFORMS = ['mac', 'ios', 'android', 'windows'];
const VALID_STATUSES = new Set(['done', 'partial', 'planned', 'na']);
const STRICT_AUTO = process.argv.includes('--strict-auto');

/**
 * Minimal, strict YAML parser tailored to docs/parity.yaml (zero external dependencies).
 * Supports comments, scalar strings (bare or quoted), nested mappings, and sequences of maps/strings.
 */
function parseParityYaml(raw) {
  const lines = raw.replace(/\r\n/g, '\n').split('\n');
  const root = { schema_version: null, features: [] };
  let curFeature = null;
  let inPlatforms = false;
  let curPlatform = null;
  let inTests = false;

  const stripQuotes = (s) => {
    const t = s.trim();
    if ((t.startsWith('"') && t.endsWith('"')) || (t.startsWith("'") && t.endsWith("'"))) {
      return t.slice(1, -1);
    }
    return t;
  };

  for (let i = 0; i < lines.length; i++) {
    const lineNo = i + 1;
    const rawLine = lines[i];
    const trimmed = rawLine.trim();
    if (!trimmed || trimmed.startsWith('#')) continue;

    const indent = rawLine.length - rawLine.trimStart().length;

    if (indent === 0) {
      inPlatforms = false;
      curPlatform = null;
      inTests = false;
      if (trimmed.startsWith('schema_version:')) {
        root.schema_version = Number(trimmed.slice('schema_version:'.length).trim());
      } else if (trimmed === 'features:') {
        // start of features list
      } else {
        throw new Error(`docs/parity.yaml:${lineNo}: unexpected top-level entry "${trimmed}"`);
      }
      continue;
    }

    if (indent === 2 && trimmed.startsWith('- id:')) {
      curFeature = {
        line: lineNo,
        id: stripQuotes(trimmed.slice('- id:'.length)),
        title: '',
        owner_layer: '',
        testing_ref: '',
        platforms: {},
      };
      root.features.push(curFeature);
      inPlatforms = false;
      curPlatform = null;
      inTests = false;
      continue;
    }

    if (!curFeature) {
      throw new Error(`docs/parity.yaml:${lineNo}: entry outside feature item: "${trimmed}"`);
    }

    if (indent === 4) {
      inTests = false;
      curPlatform = null;
      if (trimmed === 'platforms:') {
        inPlatforms = true;
      } else {
        inPlatforms = false;
        const idx = trimmed.indexOf(':');
        if (idx < 0) throw new Error(`docs/parity.yaml:${lineNo}: expected key:value, got "${trimmed}"`);
        const k = trimmed.slice(0, idx).trim();
        const v = stripQuotes(trimmed.slice(idx + 1));
        curFeature[k] = v;
      }
      continue;
    }

    if (indent === 6 && inPlatforms) {
      inTests = false;
      if (!trimmed.endsWith(':')) {
        throw new Error(`docs/parity.yaml:${lineNo}: expected platform key:, got "${trimmed}"`);
      }
      const pName = trimmed.slice(0, -1).trim();
      curPlatform = { line: lineNo, status: '', note: '', tests: [] };
      curFeature.platforms[pName] = curPlatform;
      continue;
    }

    if (indent === 8 && curPlatform) {
      if (trimmed === 'tests:') {
        inTests = true;
      } else if (trimmed.startsWith('tests:')) {
        inTests = false;
        const rest = trimmed.slice('tests:'.length).trim();
        if (rest === '[]') {
          curPlatform.tests = [];
        } else if (rest.startsWith('[') && rest.endsWith(']')) {
          curPlatform.tests = rest
            .slice(1, -1)
            .split(',')
            .map((x) => stripQuotes(x))
            .filter(Boolean);
        } else {
          throw new Error(`docs/parity.yaml:${lineNo}: invalid inline tests value "${rest}"`);
        }
      } else {
        inTests = false;
        const idx = trimmed.indexOf(':');
        if (idx < 0) throw new Error(`docs/parity.yaml:${lineNo}: expected key:value, got "${trimmed}"`);
        const k = trimmed.slice(0, idx).trim();
        const v = stripQuotes(trimmed.slice(idx + 1));
        curPlatform[k] = v;
      }
      continue;
    }

    if (indent === 10 && inTests && curPlatform && trimmed.startsWith('- ')) {
      curPlatform.tests.push(stripQuotes(trimmed.slice(2)));
      continue;
    }

    throw new Error(`docs/parity.yaml:${lineNo}: unexpected indentation (${indent}) or syntax "${trimmed}"`);
  }

  return root;
}

const fileCache = new Map();
function readCached(absPath) {
  if (!fileCache.has(absPath)) {
    fileCache.set(absPath, fs.existsSync(absPath) ? fs.readFileSync(absPath, 'utf8') : null);
  }
  return fileCache.get(absPath);
}

/**
 * Resolves and verifies a single test locator from docs/parity.yaml.
 * Returns null if valid, or an error description string.
 */
function verifyTestRef(ref) {
  const colon = ref.indexOf(':');
  if (colon <= 0) return `invalid test ref "${ref}" (expected <scheme>:<target>)`;
  const scheme = ref.slice(0, colon);
  const target = ref.slice(colon + 1);

  const checkFileAndSymbol = (baseDir, relAndSym) => {
    const [rel, sym] = relAndSym.split('::');
    const abs = path.join(baseDir, rel);
    const content = readCached(abs);
    if (content === null) return `missing file for "${ref}" -> ${path.relative(ROOT, abs)}`;
    if (sym && !content.includes(sym)) {
      return `symbol "${sym}" not found in ${path.relative(ROOT, abs)} (from "${ref}")`;
    }
    return null;
  };

  switch (scheme) {
    case 'rust':
      return checkFileAndSymbol(path.join(ROOT, 'core'), target);
    case 'conformance': {
      const abs = path.join(ROOT, 'fixtures', 'conformance', `${target}.json`);
      return fs.existsSync(abs) ? null : `missing conformance fixture ${path.relative(ROOT, abs)}`;
    }
    case 'reader': {
      const [spec, sym] = target.split('::');
      const abs = path.join(ROOT, 'reader', 'test', spec);
      const content = readCached(abs);
      if (content === null) return `missing reader test ${path.relative(ROOT, abs)}`;
      if (sym && !content.includes(sym)) return `symbol "${sym}" not found in ${path.relative(ROOT, abs)}`;
      return null;
    }
    case 'apple':
      return checkFileAndSymbol(path.join(ROOT, 'apple'), target);
    case 'android':
      return checkFileAndSymbol(path.join(ROOT, 'android'), target);
    case 'windows':
      return checkFileAndSymbol(path.join(ROOT, 'windows'), target);
    case 'midscene': {
      const abs = path.join(ROOT, 'e2e', 'midscene', 'scenarios', target);
      return fs.existsSync(abs) ? null : `missing Midscene scenario ${path.relative(ROOT, abs)}`;
    }
    case 'sync-e2e': {
      const abs = path.join(ROOT, 'scripts', 'sync-webdav-e2e.mjs');
      const content = readCached(abs);
      if (content === null) return `missing ${path.relative(ROOT, abs)}`;
      if (target && !content.includes(target)) return `sync-e2e case "${target}" not found in scripts/sync-webdav-e2e.mjs`;
      return null;
    }
    case 'script': {
      const abs = path.join(ROOT, target);
      return fs.existsSync(abs) ? null : `missing script ${target}`;
    }
    case 'manual': {
      if (!target) return `empty manual reference "${ref}"`;
      const docPath = path.join(ROOT, 'docs', 'dev', 'testing.md');
      return fs.existsSync(docPath) ? null : `missing docs/dev/testing.md`;
    }
    default:
      return `unknown test ref scheme "${scheme}" in "${ref}"`;
  }
}

function main() {
  if (!fs.existsSync(PARITY_PATH)) {
    console.error('Missing docs/parity.yaml');
    process.exit(1);
  }
  const parsed = parseParityYaml(fs.readFileSync(PARITY_PATH, 'utf8'));
  const errors = [];

  if (parsed.schema_version !== 1) {
    errors.push(`schema_version must be 1, got ${parsed.schema_version}`);
  }
  if (!parsed.features.length) {
    errors.push('features list must not be empty');
  }

  const seenIds = new Set();
  const counts = Object.fromEntries(
    PLATFORMS.map((p) => [p, { done: 0, partial: 0, planned: 0, na: 0, autoTested: 0 }]),
  );

  for (const f of parsed.features) {
    if (!f.id) errors.push(`line ${f.line}: feature missing id`);
    if (seenIds.has(f.id)) errors.push(`line ${f.line}: duplicate feature id "${f.id}"`);
    seenIds.add(f.id);
    if (!f.title) errors.push(`${f.id}: missing title`);
    if (!f.owner_layer) errors.push(`${f.id}: missing owner_layer`);

    for (const p of Object.keys(f.platforms)) {
      if (!PLATFORMS.includes(p)) errors.push(`${f.id}: unknown platform "${p}"`);
    }

    for (const p of PLATFORMS) {
      const entry = f.platforms[p];
      if (!entry) {
        errors.push(`${f.id}: missing platform "${p}"`);
        continue;
      }
      if (!VALID_STATUSES.has(entry.status)) {
        errors.push(`${f.id}.${p}: invalid status "${entry.status}"`);
        continue;
      }
      counts[p][entry.status]++;

      if (entry.status === 'done') {
        if (!entry.tests.length) {
          errors.push(`${f.id}.${p}: status is "done" but has no tests declared`);
        }
        const hasAuto = entry.tests.some((t) => !t.startsWith('manual:'));
        if (hasAuto) counts[p].autoTested++;
        if (STRICT_AUTO && !hasAuto) {
          errors.push(`${f.id}.${p}: --strict-auto requires at least one automated test (only manual refs present)`);
        }
      } else {
        if (!entry.note || !entry.note.trim()) {
          errors.push(`${f.id}.${p}: status "${entry.status}" requires a non-empty "note" explaining why`);
        }
      }

      for (const ref of entry.tests) {
        const err = verifyTestRef(ref);
        if (err) errors.push(`${f.id}.${p}: ${err}`);
      }
    }
  }

  console.log(`Parity matrix: ${parsed.features.length} features across ${PLATFORMS.join(', ')}`);
  console.log('Platform   Done  Partial  Planned   N/A   Auto-tested (of Done)');
  for (const p of PLATFORMS) {
    const c = counts[p];
    const pct = c.done ? Math.round((c.autoTested / c.done) * 100) : 100;
    console.log(
      `${p.padEnd(10)} ${String(c.done).padStart(4)}  ${String(c.partial).padStart(7)}  ${String(c.planned).padStart(7)}  ${String(c.na).padStart(4)}   ${String(c.autoTested).padStart(2)}/${String(c.done).padEnd(2)} (${pct}%)`,
    );
  }

  if (errors.length) {
    console.error(`\nParity check FAILED (${errors.length} issue${errors.length === 1 ? '' : 's'}):`);
    for (const e of errors) console.error(`  - ${e}`);
    process.exit(1);
  }
  console.log('\nParity check PASSED.');
}

main();
