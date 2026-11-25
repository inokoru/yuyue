// Layer 3: Cross-engine visual parity & Playwright screenshot regression suite.
//
// Runs on BOTH WebKit (Apple WKWebView) and Chromium (Android WebView & Windows WebView2).
// Always asserts computed-style & layout visual parity invariants (paper themes, e-ink high
// contrast, 2-column spread, highlights + ink line, dialogue bubbles).
//
// Pixel-level `toHaveScreenshot` runs when either:
//  - `YY_VISUAL=1` is set (e.g. recording or verifying baselines on your dev/CI platform), OR
//  - a matching platform baseline PNG already exists under `test/visual.spec.ts-snapshots/`.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import type { Page } from '@playwright/test';
import { test, expect, open, checkPaginated, problems } from './util';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SNAP_DIR = path.join(HERE, 'visual.spec.ts-snapshots');

async function maybeAssertScreenshot(page: Page, name: string, projectName: string): Promise<void> {
  const force = process.env.YY_VISUAL === '1';
  const expectedFile = path.join(SNAP_DIR, `${name}-${projectName}-${process.platform}.png`);
  if (!force && !fs.existsSync(expectedFile)) return;
  await expect(page).toHaveScreenshot(`${name}.png`, {
    animations: 'disabled',
    maxDiffPixelRatio: 0.02,
  });
}

test('visual parity: plain, kami, dark, and e-ink themes apply exact surface tokens', async ({ page }, testInfo) => {
  await open(page, 'beiying/s/0.html');

  const themes = [
    { name: 'paper-plain', settings: { paper: 'plain', eink: 0, fontSize: 18 }, attrPaper: 'plain', attrEink: '0' },
    { name: 'paper-kami', settings: { paper: 'kami', eink: 0, font: 'serif', fontSize: 19 }, attrPaper: 'kami', attrEink: '0' },
    { name: 'paper-dark', settings: { paper: 'dark', eink: 0, fontSize: 18 }, attrPaper: 'dark', attrEink: '0' },
    { name: 'paper-eink', settings: { paper: 'plain', eink: 1, motion: 'reduced', fontSize: 18 }, attrPaper: 'plain', attrEink: '1' },
  ];

  const backgrounds = new Map<string, string>();
  for (const t of themes) {
    await page.evaluate((s) => (window as any).yy.applySettings(s), t.settings);
    const state = await page.evaluate(() => {
      const root = document.documentElement;
      const rootStyle = getComputedStyle(root);
      return {
        paper: root.getAttribute('data-yy-paper'),
        eink: root.getAttribute('data-yy-eink'),
        bg: rootStyle.backgroundColor,
        color: rootStyle.color,
      };
    });
    expect(state.paper).toBe(t.attrPaper);
    expect(state.eink).toBe(t.attrEink);
    expect(state.bg).not.toBe(state.color);
    backgrounds.set(t.name, state.bg);

    const inv = await checkPaginated(page);
    expect(inv.problems, t.name).toEqual([]);
    await maybeAssertScreenshot(page, t.name, testInfo.project.name);
  }

  expect(backgrounds.get('paper-dark')).not.toBe(backgrounds.get('paper-plain'));
  expect(backgrounds.get('paper-kami')).not.toBe(backgrounds.get('paper-dark'));
  expect(await problems(page)).toEqual([]);
});

test('visual parity: highlights + active ink line render without shifting layout', async ({ page }, testInfo) => {
  await open(page, 'beiying/s/0.html');
  const before = await checkPaginated(page);
  expect(before.problems).toEqual([]);

  await page.evaluate(() => {
    const y = (window as any).yy;
    y.applySettings({ motion: 'reduced' });
    y.setMarks([
      { id: 'm1', start: { s: 1, o: 0 }, end: { s: 1, o: 6 }, style: 'yellow', note: true },
      { id: 'm2', start: { s: 2, o: 0 }, end: { s: 2, o: 8 }, style: 'wave', note: false },
    ]);
    y.setActive(1, { durationMs: 3000, elapsedMs: 1500, playing: false });
  });

  const rect = await page.evaluate(() => (window as any).yy.markRect('m1'));
  expect(rect).not.toBeNull();
  expect(rect.width).toBeGreaterThan(10);

  const after = await checkPaginated(page);
  expect(after.problems).toEqual([]);
  expect(after.pageCount).toBe(before.pageCount);

  await maybeAssertScreenshot(page, 'marks-and-ink', testInfo.project.name);
});

test.describe('two-column desktop spread', () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  test('visual parity: 2-column layout renders cleanly across engines', async ({ page }, testInfo) => {
    await open(page, 'hlm/s/2.html', { cols: 2, paper: 'kami' });
    const inv = await checkPaginated(page);
    expect(inv.problems).toEqual([]);
    expect(inv.W).toBe(1280);
    await maybeAssertScreenshot(page, 'desktop-2col-kami', testInfo.project.name);
  });
});
