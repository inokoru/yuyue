// yy.applySettings (contract §4): attributes and defaults, and keeping the reading position
// through relayouts (settings, resize, mode switches).
import type { Page } from '@playwright/test';
import { test, expect, open, checkPaginated, clearMessages, waitMessages, messages } from './util';

/** The reading position of the current page, as native saves it: the first sentence that starts on
 *  the page (the first sentence on it when none starts there, a long one filling it). Remembers the
 *  first character of that sentence as window.__a. A relayout may move the tail of the sentence
 *  before off the page; the sentence itself must stay, on the page sentencePages names for it. */
function markPosition(page: Page): Promise<{ s: number; text: string }> {
  return page.evaluate(() => {
    const y = (window as any).yy;
    const g = y.geometry();
    const p = y.currentPage();
    let s = -1;
    // The first sentence whose page is p (parts are exclusive: they start on the page of their
    // first character).
    for (let i = g.pageFirstSentence[p]; i >= 0 && i < g.sentencePages.length; i++) {
      if (g.sentencePages[i] > p) break;
      if (g.sentencePages[i] === p) {
        s = i;
        break;
      }
    }
    if (s < 0) s = g.pageFirstSentence[p];
    for (const el of Array.from(document.querySelectorAll('#yy-content .s[data-s="' + s + '"]'))) {
      const w = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
      for (let t = w.nextNode() as Text | null; t; t = w.nextNode() as Text | null) {
        if (t.parentElement!.closest('rt, rp, .yy-noteref')) continue;
        for (let i = 0; i < t.data.length; i++) {
          if (/\s/.test(t.data[i])) continue;
          const r = document.createRange();
          r.setStart(t, i);
          r.setEnd(t, i + 1);
          if (r.getClientRects()[0]) {
            (window as any).__a = { t, i };
            return { s, text: t.data.slice(i, i + 10) };
          }
        }
      }
    }
    throw new Error('sentence ' + s + ' has no text');
  });
}

/** Whether the page shown is the one where sentence s starts (what native restores to). */
const onItsPage = (page: Page, s: number): Promise<boolean> =>
  page.evaluate((s) => (window as any).yy.currentPage() === (window as any).yy.geometry().sentencePages[s], s);

/** Whether the character remembered by markPosition is fully on screen. */
function markVisible(page: Page): Promise<boolean> {
  return page.evaluate(() => {
    const { t, i } = (window as any).__a;
    const r = document.createRange();
    r.setStart(t, i);
    r.setEnd(t, i + 1);
    const b = r.getClientRects()[0];
    if (!b) return false;
    return document.documentElement.getAttribute('data-yy-mode') === 'scrolled'
      ? b.top >= 0 && b.bottom <= innerHeight
      : b.left >= 0 && b.right <= innerWidth;
  });
}

const apply = (page: Page, s: Record<string, unknown>): Promise<void> =>
  page.evaluate((s) => (window as any).yy.applySettings(s), s);
const currentPage = (page: Page): Promise<number> => page.evaluate(() => (window as any).yy.currentPage());

test('font size steps keep the reading position on screen and come back to the same page', async ({ page }) => {
  await open(page, 'hlm/s/2.html');
  await page.evaluate(() => (window as any).yy.scrollToPage(5));
  const mark = await markPosition(page);
  for (const fontSize of [20, 24, 30, 16, 15, 18]) {
    await clearMessages(page);
    await apply(page, { fontSize });
    expect(await onItsPage(page, mark.s), `fontSize ${fontSize}: page of s${mark.s} “${mark.text}”`).toBe(true);
    expect(await markVisible(page), `fontSize ${fontSize}, “${mark.text}”`).toBe(true);
    // applySettings always posts `layout`, synchronously.
    expect((await messages(page, 'layout')).length).toBe(1);
  }
  expect(await currentPage(page)).toBe(5);
});

test.describe('wide', () => {
  test.use({ viewport: { width: 1180, height: 820 } });
  test('paper, font, columns, margins and spacing keep it too', async ({ page }) => {
    await open(page, 'hlm/s/2.html');
    await page.evaluate(() => (window as any).yy.scrollToPage(3));
    const mark = await markPosition(page);
    const steps = [
      { paper: 'kami' },
      { cols: 2 },
      { font: 'kai' },
      { margin: 40 },
      { lineHeight: 2.4 },
      { paragraphSpacing: 1 },
      { justify: 0, indent: 0 },
      { paper: 'eink' },
    ];
    for (const s of steps) {
      await apply(page, s);
      expect(await onItsPage(page, mark.s), JSON.stringify(s)).toBe(true);
      expect(await markVisible(page), JSON.stringify(s)).toBe(true);
      expect((await checkPaginated(page)).problems, JSON.stringify(s)).toEqual([]);
    }
    await apply(page, { paper: 'plain', cols: 1, font: null, margin: 24, lineHeight: null, paragraphSpacing: null, justify: null, indent: null });
    expect(await currentPage(page)).toBe(3);
  });
});

test('after a swipe, the new page is what is kept', async ({ page }) => {
  await open(page, 'hlm/s/2.html', { paging: 'snap' });
  // What native paging (or a user swipe) does: the page moves without the JS API.
  await page.evaluate(() => window.scrollTo(4 * innerWidth, 0));
  await page.waitForTimeout(400); // the reader takes the new position once scrolling settles
  const mark = await markPosition(page);
  await apply(page, { fontSize: 24 });
  expect(await onItsPage(page, mark.s)).toBe(true);
  expect(await markVisible(page)).toBe(true);
  await apply(page, { fontSize: 18 });
  expect(await currentPage(page)).toBe(4);
});

test('resizing re-lays out, posts `layout` and keeps the position', async ({ page }) => {
  await open(page, 'hlm/s/2.html');
  await page.evaluate(() => (window as any).yy.scrollToPage(5));
  const mark = await markPosition(page);
  await clearMessages(page);
  await page.setViewportSize({ width: 430, height: 932 });
  const [layout] = await waitMessages(page, 'layout');
  expect(layout.pageWidth).toBe(430);
  expect(layout.pageHeight).toBe(932);
  expect(await onItsPage(page, mark.s)).toBe(true);
  expect(await markVisible(page)).toBe(true);
  expect((await checkPaginated(page)).problems).toEqual([]);
  await clearMessages(page);
  await page.setViewportSize({ width: 393, height: 852 });
  await waitMessages(page, 'layout');
  expect(await currentPage(page)).toBe(5);
});

test('switching to scrolled mode and back keeps the position', async ({ page }) => {
  await open(page, 'hlm/s/2.html');
  await page.evaluate(() => (window as any).yy.scrollToPage(5));
  const mark = await markPosition(page);
  await apply(page, { mode: 'scrolled' });
  expect(await page.evaluate(() => scrollX)).toBe(0);
  expect(await markVisible(page), 'the character the page started with is in the band').toBe(true);
  await apply(page, { mode: 'paginated' });
  expect(await currentPage(page)).toBe(5);
  expect(await onItsPage(page, mark.s), 'the page is the one s' + mark.s + ' starts on').toBe(true);
  expect((await checkPaginated(page)).problems).toEqual([]);
});

test('null resets to the defaults; missing keys keep their value', async ({ page }) => {
  await open(page, 'beiying/s/0.html');
  const state = (): Promise<Record<string, string | null>> =>
    page.evaluate(() => {
      const r = document.documentElement;
      const out: Record<string, string | null> = {};
      for (const a of ['paper', 'mode', 'paging', 'justify', 'indent', 'compress', 'motion', 'tint']) out[a] = r.getAttribute('data-yy-' + a);
      for (const v of ['fs', 'lh', 'ps', 'font', 'font-en', 'margin', 'top', 'bottom', 'cols']) out[v] = r.style.getPropertyValue('--yy-' + v).trim() || null;
      return out;
    });
  await apply(page, {
    paper: 'kami', mode: 'scrolled', paging: 'native', justify: 0, indent: 1, compress: false, motion: 'reduced', tint: 'ink',
    fontSize: 22, lineHeight: 1.8, paragraphSpacing: 0.5, font: 'kai', margin: 30, top: 60, bottom: 90, cols: 2,
  });
  expect(await state()).toEqual({
    paper: 'kami', mode: 'scrolled', paging: 'native', justify: '0', indent: '1', compress: '0', motion: 'reduced', tint: 'ink',
    fs: '22px', lh: '1.8', ps: '0.5em', font: 'var(--yy-kaiti)', 'font-en': 'var(--yy-latin-serif)', margin: '30px', top: '60px', bottom: '90px', cols: '2',
  });
  await apply(page, { fontSize: 20 });
  expect(await state()).toMatchObject({ paper: 'kami', fs: '20px', font: 'var(--yy-kaiti)' });
  await apply(page, {
    paper: null, mode: null, paging: null, justify: null, indent: null, compress: null, motion: null, tint: null,
    fontSize: null, lineHeight: null, paragraphSpacing: null, font: null, margin: null, top: null, bottom: null, cols: null,
  });
  expect(await state()).toEqual({
    paper: 'plain', mode: 'paginated', paging: 'native', justify: 'auto', indent: 'auto', compress: '1', motion: 'full', tint: null,
    fs: '18px', lh: null, ps: null, font: null, 'font-en': null, margin: '24px', top: '56px', bottom: '88px', cols: '1',
  });
});

test('我的纸: its colours and texture over the preset, and gone again with null', async ({ page }) => {
  await open(page, 'beiying/s/0.html');
  const look = (): Promise<{ bg: string; ink: string; scheme: string; texture: string; ps: string }> =>
    page.evaluate(() => {
      const r = getComputedStyle(document.documentElement);
      const s = getComputedStyle(document.querySelector('#yy-content p .s') as Element);
      return { bg: r.backgroundColor, ink: s.color, scheme: r.colorScheme, texture: r.backgroundImage, ps: r.getPropertyValue('--yy-p-ps').trim() };
    });
  await apply(page, { paper: 'kaminight', paperStyle: '--yy-p-bg:#18202B;--yy-p-ink:#C9D3DE;color-scheme:dark;--yy-x:red', texture: 'linen' });
  const mine = await look();
  expect(mine).toMatchObject({ bg: 'rgb(24, 32, 43)', ink: 'rgb(201, 211, 222)', scheme: 'dark', ps: '.3em' });
  expect(mine.texture).toContain('repeating-linear-gradient');
  expect(await page.evaluate(() => document.documentElement.style.getPropertyValue('--yy-x'))).toBe('');
  await apply(page, { paper: 'plain', paperStyle: null, texture: null });
  expect(await look()).toMatchObject({ bg: 'rgb(255, 255, 255)', texture: 'none' });
});

test('font keywords choose the CJK face and a matching Latin face', async ({ page }) => {
  // Core writes only --yy-font; the reader derives the Latin face.
  await open(page, 'gatsby/s/4.html', { font: 'serif' });
  const vars = (): Promise<string[]> =>
    page.evaluate(() => ['--yy-font', '--yy-font-en'].map((v) => document.documentElement.style.getPropertyValue(v).trim()));
  expect(await vars()).toEqual(['var(--yy-serif)', 'var(--yy-latin-serif)']);
  await apply(page, { font: 'sans' });
  expect(await vars()).toEqual(['var(--yy-sans)', 'var(--yy-sans)']);
  await apply(page, { font: 'Georgia, serif' });
  expect(await vars()).toEqual(['Georgia, serif', 'Georgia, serif']);
  const family = await page.evaluate(() => getComputedStyle(document.querySelector('#yy-content p .s') as Element).fontFamily);
  expect(family).toContain('Georgia');
  expect(await page.evaluate(() => (window as any).yy.settings().font)).toBe('Georgia, serif');
});

test('墨水屏模式 overrides presets and 我的纸 to pure white/black, decoupled from 铅纸', async ({ page }) => {
  await open(page, 'image/s/0.html');
  const look = (): Promise<{ bg: string; ink: string; texture: string; imgFilter: string }> =>
    page.evaluate(() => {
      const r = getComputedStyle(document.documentElement);
      const s = getComputedStyle(document.querySelector('#yy-content p .s') as Element);
      const img = document.querySelector('#yy-content img') as Element | null;
      return {
        bg: r.backgroundColor,
        ink: s.color,
        texture: r.backgroundImage,
        imgFilter: img ? getComputedStyle(img).filter : 'none',
      };
    });
  // 铅纸 (paper: 'eink') keeps warm-grey grain and normal colour illustrations.
  await apply(page, { paper: 'eink', eink: false, einkGray: false });
  const lead = await look();
  expect(lead.bg).toBe('rgb(233, 232, 227)');
  expect(lead.ink).toBe('rgb(20, 20, 20)');
  expect(lead.texture).toContain('svg+xml');
  expect(lead.imgFilter).toBe('none');

  // 墨水屏模式 overrides even 我的纸 inline colours and texture to pure white/black.
  await apply(page, { paper: 'kaminight', paperStyle: '--yy-p-bg:#18202B;--yy-p-ink:#C9D3DE;color-scheme:dark', texture: 'linen', eink: true, einkGray: true });
  const einkGrayOn = await look();
  expect(einkGrayOn.bg).toBe('rgb(255, 255, 255)');
  expect(einkGrayOn.ink).toBe('rgb(0, 0, 0)');
  expect(einkGrayOn.texture).toBe('none');
  expect(einkGrayOn.imgFilter).toContain('grayscale');

  // Colour e-ink: disabling grayscale keeps pure white/black page while preserving artwork colour.
  await apply(page, { eink: true, einkGray: false });
  const einkColor = await look();
  expect(einkColor.bg).toBe('rgb(255, 255, 255)');
  expect(einkColor.imgFilter).toBe('none');

  // Turning 墨水屏模式 off restores the underlying 我的纸 without touching synced paper settings.
  await apply(page, { eink: false, einkGray: false });
  const restored = await look();
  expect(restored.bg).toBe('rgb(24, 32, 43)');
  expect(restored.texture).toContain('repeating-linear-gradient');
});

test('EPUB publisher CSS on spans, wildcards, and secondary classes honors reader font settings', async ({ page }) => {
  const customHtml = `<!DOCTYPE html>
<html lang="zh-CN" data-yy-paper="plain" data-yy-mode="paginated" data-yy-paging="snap"
      data-yy-justify="auto" data-yy-indent="auto" data-yy-compress="1" data-yy-motion="full"
      style="--yy-fs:18px;--yy-margin:24px;--yy-top:56px;--yy-bottom:88px;--yy-cols:1">
<head>
<meta charset="utf-8">
<style>
  * { font-family: "PublisherWildcard", fantasy; }
  span.calibre2 { font-family: "PublisherBody", cursive; }
  p.quote { font-family: "PublisherQuote", monospace; }
  span.inline-accent { font-family: "PublisherInline", monospace; }
</style>
<link rel="stylesheet" href="/app/reader.css">
</head>
<body>
<main id="yy-content" class="yy-section" data-section="0">
  <h2><span class="calibre2"><span class="s" data-s="0">第一章</span></span></h2>
  <div class="yy-meta">读 1 分钟 · 听 1 分钟</div>
  <p class="calibre1"><span class="calibre2"><span class="s" data-s="1">这是第一段正文内容，用来测试出版社内联字体样式覆盖。</span></span></p>
  <p class="calibre1"><span class="calibre2"><span class="s" data-s="2">这是第二段正文内容，包含更多文字以确保成为主导正文样式。</span></span></p>
  <p class="quote"><span class="s" data-s="3">这是一段引文。</span><span class="inline-accent"><span class="s" data-s="4">强调句。</span></span></p>
</main>
<script src="/app/reader.js"></script>
</body>
</html>`;

  await page.route('**/book/epub-font-test/s/0.html', (route) =>
    route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: customHtml }),
  );
  await open(page, 'epub-font-test/s/0.html');

  const families = () =>
    page.evaluate(() => {
      const f = (sel: string) => getComputedStyle(document.querySelector(sel) as Element).fontFamily;
      return {
        title: f('#yy-content h2 .s'),
        body: f('#yy-content p.calibre1 .s'),
        quote: f('#yy-content p.quote .s[data-s="3"]'),
        accent: f('#yy-content span.inline-accent .s'),
        meta: f('#yy-content .yy-meta'),
      };
    });

  // Paper default (no explicit user font): dominant Calibre span is overridden by paper sans,
  // while deliberate publisher quote/inline variations keep their publisher font.
  const initial = await families();
  expect(initial.body).not.toContain('PublisherBody');
  expect(initial.body).not.toContain('PublisherWildcard');
  expect(initial.quote).toContain('PublisherQuote');
  expect(initial.accent).toContain('PublisherInline');

  // Explicit user font (`kai` or custom `fontFace` stack): overrides all text blocks and spans,
  // while keeping `.yy-meta` in UI sans.
  await apply(page, { font: '"yy-notoserif", var(--yy-serif)' });
  const chosen = await families();
  expect(chosen.title).toContain('yy-notoserif');
  expect(chosen.body).toContain('yy-notoserif');
  expect(chosen.quote).toContain('yy-notoserif');
  expect(chosen.accent).toContain('yy-notoserif');
  expect(chosen.meta).not.toContain('yy-notoserif');

  // Resetting font to null restores paper default and publisher deliberate quote font.
  await apply(page, { font: null });
  const reset = await families();
  expect(reset.body).not.toContain('PublisherBody');
  expect(reset.quote).toContain('PublisherQuote');
});
