#!/usr/bin/env node
/**
 * Merges official Apple Localizable.xcstrings + Android extra-en.json + Windows-specific
 * windows-en.json into windows/Yuyue/Assets/L10n/en.json.
 * Run: node windows/build-l10n.mjs [--check]
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(__dirname, '..');

const xcPath = path.join(root, 'apple/Yuyue/Localizable.xcstrings');
const androidExtraPath = path.join(root, 'android/app/src/main/l10n/extra-en.json');
const winExtraPath = path.join(root, 'windows/Yuyue/Assets/L10n/windows-en.json');
const outPath = path.join(root, 'windows/Yuyue/Assets/L10n/en.json');

const dict = {};

// 1. Apple Localizable.xcstrings (official primary zh-Hans -> en dictionary)
if (fs.existsSync(xcPath)) {
  const xc = JSON.parse(fs.readFileSync(xcPath, 'utf8'));
  for (const [k, v] of Object.entries(xc.strings || {})) {
    if (!k) continue;
    const en = v?.localizations?.en;
    if (!en) continue;
    let val = en.stringUnit?.value;
    if (!val && en.variations?.plural) {
      val = en.variations.plural.other?.stringUnit?.value
         ?? en.variations.plural.one?.stringUnit?.value;
    }
    if (typeof val === 'string' && val.length > 0 && !val.includes('%#@')) {
      dict[k] = val;
    }
  }
}

// 2. Android extra-en.json
if (fs.existsSync(androidExtraPath)) {
  const extra = JSON.parse(fs.readFileSync(androidExtraPath, 'utf8'));
  for (const [k, v] of Object.entries(extra)) {
    if (!k || dict[k]) continue;
    if (typeof v === 'string' && v.length > 0) dict[k] = v;
    else if (v && typeof v === 'object' && typeof v.other === 'string') dict[k] = v.other;
  }
}

// 3. Windows-specific overrides & additions (always wins)
if (fs.existsSync(winExtraPath)) {
  const win = JSON.parse(fs.readFileSync(winExtraPath, 'utf8'));
  for (const [k, v] of Object.entries(win)) {
    if (k && typeof v === 'string' && v.length > 0) dict[k] = v;
  }
}

// Keep only entries without unresolved Obj-C format specifiers or sample-book title collisions
const excludedKeys = new Set(['背影', '故乡']);
const clean = {};
for (const k of Object.keys(dict).sort()) {
  if (excludedKeys.has(k)) continue;
  if (!k.includes('%@') && !k.includes('%lld') && !k.includes('%1$') && !k.includes('%d')) {
    clean[k] = dict[k];
  }
}

fs.mkdirSync(path.dirname(outPath), { recursive: true });
fs.writeFileSync(outPath, JSON.stringify(clean, null, 2) + '\n', 'utf8');
console.log(`Wrote ${Object.keys(clean).length} static zh->en entries to ${path.relative(root, outPath)}`);

if (process.argv.includes('--audit')) {
  function walk(dir, exts, out = []) {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      if (['bin', 'obj', 'Generated', 'Assets'].includes(e.name)) continue;
      const p = path.join(dir, e.name);
      if (e.isDirectory()) walk(p, exts, out);
      else if (exts.some(x => e.name.endsWith(x))) out.push(p);
    }
    return out;
  }

  const missing = new Set();
  for (const f of walk(path.join(root, 'windows/Yuyue'), ['.xaml'])) {
    const txt = fs.readFileSync(f, 'utf8');
    for (const m of txt.matchAll(/(?:Text|Content|PlaceholderText|Header|ToolTip|Title)="([^"]*[\u4e00-\u9fff][^"]*)"/g)) {
      if (!clean[m[1]]) missing.add(m[1]);
    }
  }
  console.log(`Uncovered XAML Chinese literals: ${missing.size}`);
  for (const s of [...missing].sort()) console.log('  ' + s);
  if (missing.size > 0) process.exit(1);
}
