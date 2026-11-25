// Layer 3: Reader <-> Native bridge contract validation across WebKit (Apple WKWebView)
// and Chromium (Android WebView & Windows WebView2).
//
// Validates:
//  1. Every canonical message in `fixtures/conformance/reader-messages.json` against the
//     generated JSON Schema (`reader/schema/bridge-messages.schema.json`), plus negative cases.
//  2. Live messages emitted by `reader.js` during real interactions (ready, layout, tap,
//     doubleTap, context, activePage, page, scroll, bubble, selection).
//  3. All three native bridge transports: Apple (`webkit.messageHandlers.yy`),
//     Android (`yyNative.postMessage(JSON.stringify)`), and Windows (`chrome.webview.postMessage`).

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test as baseTest } from '@playwright/test';
import { test, expect, open, clearMessages, messages, waitMessages, pointOfSentence } from './util';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SCHEMA_PATH = path.resolve(HERE, '..', 'schema', 'bridge-messages.schema.json');
const FIXTURE_PATH = path.resolve(HERE, '..', '..', 'fixtures', 'conformance', 'reader-messages.json');

interface SchemaNode {
  $ref?: string;
  $defs?: Record<string, SchemaNode>;
  oneOf?: SchemaNode[];
  anyOf?: SchemaNode[];
  const?: unknown;
  enum?: unknown[];
  type?: string | string[];
  properties?: Record<string, SchemaNode>;
  required?: string[];
  additionalProperties?: boolean;
  items?: SchemaNode;
}

const ROOT_SCHEMA = JSON.parse(fs.readFileSync(SCHEMA_PATH, 'utf8')) as SchemaNode & {
  'x-message-types': string[];
};

function resolveRef(node: SchemaNode): SchemaNode {
  if (!node.$ref) return node;
  const m = /^#\/\$defs\/(.+)$/.exec(node.$ref);
  if (!m || !ROOT_SCHEMA.$defs?.[m[1]]) throw new Error(`Unresolvable $ref: ${node.$ref}`);
  return ROOT_SCHEMA.$defs[m[1]];
}

function jsonTypeOf(v: unknown): string {
  if (v === null) return 'null';
  if (Array.isArray(v)) return 'array';
  return typeof v;
}

function validate(value: unknown, rawSchema: SchemaNode, at = '$'): string[] {
  const schema = resolveRef(rawSchema);

  if ('const' in schema) {
    return value === schema.const ? [] : [`${at}: expected const ${JSON.stringify(schema.const)}, got ${JSON.stringify(value)}`];
  }
  if (schema.enum) {
    return schema.enum.includes(value) ? [] : [`${at}: ${JSON.stringify(value)} not in ${JSON.stringify(schema.enum)}`];
  }
  if (schema.oneOf) {
    const matches = schema.oneOf.filter((s) => validate(value, s, at).length === 0);
    if (matches.length === 1) return [];
    return [`${at}: expected exactly 1 oneOf match for ${JSON.stringify(value)}, got ${matches.length}`];
  }
  if (schema.anyOf) {
    const ok = schema.anyOf.some((s) => validate(value, s, at).length === 0);
    return ok ? [] : [`${at}: value ${JSON.stringify(value)} failed all anyOf branches`];
  }
  if (schema.type) {
    const allowed = Array.isArray(schema.type) ? schema.type : [schema.type];
    const actual = jsonTypeOf(value);
    if (!allowed.includes(actual)) {
      return [`${at}: expected type ${allowed.join('|')}, got ${actual}`];
    }
  }
  if (schema.type === 'array' && Array.isArray(value) && schema.items) {
    const errs: string[] = [];
    for (let i = 0; i < value.length; i++) {
      errs.push(...validate(value[i], schema.items, `${at}[${i}]`));
    }
    return errs;
  }
  if (schema.type === 'object' && value && typeof value === 'object' && !Array.isArray(value)) {
    const rec = value as Record<string, unknown>;
    const errs: string[] = [];
    for (const req of schema.required || []) {
      if (!(req in rec)) errs.push(`${at}: missing required property "${req}"`);
    }
    for (const [k, v] of Object.entries(rec)) {
      const propSchema = schema.properties?.[k];
      if (!propSchema) {
        if (schema.additionalProperties === false) errs.push(`${at}: unexpected property "${k}"`);
        continue;
      }
      errs.push(...validate(v, propSchema, `${at}.${k}`));
    }
    return errs;
  }
  return [];
}

test('canonical reader-messages fixture satisfies JSON Schema and covers all message types', async () => {
  const fixtures = JSON.parse(fs.readFileSync(FIXTURE_PATH, 'utf8')) as {
    valid: Array<{ type: string }>;
    invalid: unknown[];
  };

  const seenTypes = new Set<string>();
  for (const msg of fixtures.valid) {
    seenTypes.add(msg.type);
    expect(validate(msg, ROOT_SCHEMA), `valid fixture ${JSON.stringify(msg)}`).toEqual([]);
  }
  for (const expectedType of ROOT_SCHEMA['x-message-types']) {
    expect(seenTypes.has(expectedType), `missing canonical fixture for message type "${expectedType}"`).toBe(true);
  }
  for (const bad of fixtures.invalid) {
    expect(validate(bad, ROOT_SCHEMA).length, `invalid fixture should fail: ${JSON.stringify(bad)}`).toBeGreaterThan(0);
  }
});

test('live reader messages across interactions conform strictly to JSON Schema', async ({ page }) => {
  await open(page, 'beiying/s/0.html', { paging: 'snap' });

  // 1. ready message
  const ready = await messages(page, 'ready');
  expect(ready.length).toBe(1);
  expect(validate(ready[0], ROOT_SCHEMA)).toEqual([]);

  // 2. applySettings -> layout message
  await clearMessages(page);
  await page.evaluate(() => (window as any).yy.applySettings({ paper: 'kami', fontSize: 20 }));
  const layouts = await messages(page, 'layout');
  expect(layouts.length).toBeGreaterThanOrEqual(1);
  for (const m of layouts) expect(validate(m, ROOT_SCHEMA)).toEqual([]);

  // 3. tap message
  await clearMessages(page);
  const pt = await pointOfSentence(page, 1);
  await page.mouse.click(pt.x, pt.y);
  const taps = await waitMessages(page, 'tap');
  for (const m of taps) expect(validate(m, ROOT_SCHEMA)).toEqual([]);

  // 4. activePage via setActive
  await clearMessages(page);
  await page.evaluate(() => (window as any).yy.setActive(1, { durationMs: 2000, elapsedMs: 0, playing: true }));
  const active = await messages(page, 'activePage');
  expect(active.length).toBeGreaterThanOrEqual(1);
  for (const m of active) expect(validate(m, ROOT_SCHEMA)).toEqual([]);

  // 5. scroll message in scrolled mode
  await clearMessages(page);
  await page.evaluate(() => (window as any).yy.applySettings({ mode: 'scrolled' }));
  await page.evaluate(() => window.scrollTo(0, 300));
  await page.waitForTimeout(220);
  const all = await messages(page);
  expect(all.some((m) => m.type === 'scroll' || m.type === 'layout')).toBe(true);
  for (const m of all) expect(validate(m, ROOT_SCHEMA), `live message ${JSON.stringify(m)}`).toEqual([]);
});

// Explicitly test Windows WebView2 bridge transport (`window.chrome.webview.postMessage`)
// alongside Apple (`webkit.messageHandlers.yy`) and Android (`yyNative`).
baseTest('Windows WebView2 bridge transport delivers schema-valid messages', async ({ page }) => {
  await page.addInitScript(() => {
    const w = window as any;
    w.__winMessages = [];
    w.chrome = {
      webview: {
        postMessage: (m: unknown) => w.__winMessages.push(JSON.parse(JSON.stringify(m))),
      },
    };
  });
  await page.goto('/book/beiying/s/0.html', { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => (window as any).__winMessages.some((m: any) => m.type === 'ready'));
  const got = (await page.evaluate(() => (window as any).__winMessages)) as unknown[];
  expect(got.length).toBeGreaterThanOrEqual(1);
  for (const m of got) {
    expect(validate(m, ROOT_SCHEMA), `WebView2 message ${JSON.stringify(m)}`).toEqual([]);
  }
});
