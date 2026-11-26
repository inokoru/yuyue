// 气泡模式 (yy.setBubbles): quotes become bubbles with who says them; every sentence keeps its
// text and number, so the ink line, marks and taps keep working; null puts it all back.
// test/fixtures/hlm/script-2.json is `yuyue lines-json` for this section.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect, open, SCREENS, checkPaginated, messages, clearMessages, waitMessages } from './util';

const here = path.dirname(fileURLToPath(import.meta.url));
const script = JSON.parse(fs.readFileSync(path.join(here, 'fixtures/hlm/script-2.json'), 'utf8')) as any[];
const palette = ['#3E6FA8', '#B5523E', '#A87A1F', '#3E8F62', '#7A5AA6', '#A4506F', '#5E7F86'];
const lines = script.map((l, idx) => {
  const who: string | null = l.who;
  const k = who ? [...who].reduce((a, c) => a + c.codePointAt(0)!, 0) % palette.length : 0;
  const choices = !who ? ['賈雨村（甄士隱對坐談心之人）', '甄士隱（姑蘇閶門外仁清巷）'] : undefined;
  return { ...l, avatar: who ? [...who].slice(-1)[0] : '?', color: palette[k], right: idx % 3 === 1 && !!who, choices };
});

function texts(): string[] {
  const y = (window as any).yy;
  const n = y.geometry().sentencePages.length;
  const out: string[] = [];
  for (let s = 0; s < n; s++) {
    out.push(Array.from(document.querySelectorAll('#yy-content .s[data-s="' + s + '"]')).map((e) => e.textContent).join(''));
  }
  return out;
}

test('setBubbles keeps every sentence and puts them back', async ({ page }) => {
  await open(page, 'hlm/s/2.html');
  const before = await page.evaluate(texts);
  await page.evaluate((ls) => (window as any).yy.setBubbles(ls), lines);
  const bubbles = await page.locator('.yy-bq').count();
  expect(bubbles).toBeGreaterThan(10);
  expect(await page.evaluate(texts)).toEqual(before);
  expect((await checkPaginated(page)).problems).toEqual([]);
  const align = await page.evaluate(() => {
    let maxDelta = 0;
    for (const q of Array.from(document.querySelectorAll('#yy-content .yy-bq'))) {
      const av = q.querySelector('.yy-bq-av') as HTMLElement;
      const bu = q.querySelector('.yy-bq-bu') as HTMLElement;
      const d = Math.abs(av.getClientRects()[0].top - bu.getClientRects()[0].top);
      if (d > maxDelta) maxDelta = d;
    }
    const avEl = document.querySelector('#yy-content .yy-bq-av') as HTMLElement;
    const avBefore = getComputedStyle(avEl, '::before');
    const avHitH = avEl.getBoundingClientRect().height - parseFloat(avBefore.top) - parseFloat(avBefore.bottom);
    const unkCount = document.querySelectorAll('#yy-content .yy-bq-unk').length;
    const choiceCount = document.querySelectorAll('#yy-content .yy-bq-ch').length;
    return { maxDelta, avHitH, unkCount, choiceCount };
  });
  expect(align.maxDelta).toBeLessThanOrEqual(0.5);
  expect(align.avHitH).toBeGreaterThanOrEqual(44);
  // Unattributed quotes must stay as clean prose, never `? 谁说的？` bubbles or choice buttons.
  expect(align.unkCount).toBe(0);
  expect(align.choiceCount).toBe(0);
  await page.screenshot({ path: `${SCREENS}/bubbles.png` });
  await page.evaluate(() => (window as any).yy.scrollToSentence(40, false));
  await page.screenshot({ path: `${SCREENS}/bubbles-40.png` });
  await page.evaluate(() => (window as any).yy.setBubbles(null));
  expect(await page.locator('.yy-bq').count()).toBe(0);
  expect(await page.evaluate(texts)).toEqual(before);
});

test('setBubbles updates in-place when called repeatedly (speaker reassign + right toggle)', async ({ page }) => {
  await open(page, 'hlm/s/2.html');
  const before = await page.evaluate(texts);
  await page.evaluate((ls) => (window as any).yy.setBubbles(ls), lines);
  const initialCount = await page.locator('.yy-bq').count();
  expect(initialCount).toBeGreaterThan(10);

  // Reassign the first attributed line to a custom speaker on the right, and flip every other line's `right`.
  const firstKnownIdx = lines.findIndex((l) => !!l.who);
  expect(firstKnownIdx).toBeGreaterThanOrEqual(0);
  const updated = lines.map((l, idx) =>
    idx === firstKnownIdx
      ? { ...l, who: '新說話人', avatar: '新', color: '#3E8F62', right: true, choices: undefined }
      : { ...l, right: !l.right && !!l.who },
  );
  await page.evaluate((ls) => (window as any).yy.setBubbles(ls), updated);

  // Bubbles must stay intact (not vanish back to plain text) with the updated speaker and side.
  expect(await page.locator('.yy-bq').count()).toBe(initialCount);
  expect(await page.evaluate(texts)).toEqual(before);
  expect((await checkPaginated(page)).problems).toEqual([]);
  const firstBubble = page.locator(`#yy-content .yy-bq[data-yy-line="${firstKnownIdx}"]`);
  await expect(firstBubble).toHaveClass(/yy-bq-r/);
  await expect(firstBubble.locator('.yy-bq-av')).toHaveText('新');
  await expect(firstBubble.locator('.yy-bq-nm')).toContainText('新說話人');

  // A third update back to `lines` also keeps all bubbles intact.
  await page.evaluate((ls) => (window as any).yy.setBubbles(ls), lines);
  expect(await page.locator('.yy-bq').count()).toBe(initialCount);
  expect(await page.evaluate(texts)).toEqual(before);
});

test('scrolled mode with bubbles tracks progress and preserves position across setBubbles refresh', async ({ page }) => {
  await open(page, 'hlm/s/2.html', { mode: 'scrolled' });
  await page.evaluate((ls) => (window as any).yy.setBubbles(ls), lines);
  const count = await page.locator('.yy-bq').count();
  expect(count).toBeGreaterThan(10);

  // Scroll well down the section; `scroll` messages must report sentence > 0 and progress > 0.
  await clearMessages(page);
  await page.evaluate(() => window.scrollTo(0, 3500));
  await waitMessages(page, 'scroll');
  await page.waitForTimeout(300);
  const beforeMsgs = await messages(page, 'scroll');
  const beforeScroll = beforeMsgs[beforeMsgs.length - 1];
  expect(beforeScroll.sentence).toBeGreaterThan(20);
  expect(beforeScroll.progress).toBeGreaterThan(0.1);
  const yBefore = await page.evaluate(() => scrollY);
  expect(yBefore).toBeGreaterThan(1000);

  // Refresh bubbles (assign a speaker + toggle right): position must stay near the current sentence,
  // not snap back to sentence 0, and bubbles must stay rendered (newly attributed lines become bubbles).
  const expectedAfter = count + (lines[5].who ? 0 : 1);
  const updated = lines.map((l, idx) =>
    idx === 5 ? { ...l, who: '甄士隱', avatar: '甄', right: true, choices: undefined } : { ...l, right: !l.right && !!l.who },
  );
  await clearMessages(page);
  await page.evaluate((ls) => (window as any).yy.setBubbles(ls), updated);
  expect(await page.locator('.yy-bq').count()).toBe(expectedAfter);

  await page.waitForTimeout(200);
  const yAfter = await page.evaluate(() => scrollY);
  expect(yAfter).toBeGreaterThan(1000);
  const afterMsgs = await messages(page, 'scroll');
  expect(afterMsgs.length).toBeGreaterThan(0);
  const afterScroll = afterMsgs[afterMsgs.length - 1];
  expect(Math.abs(afterScroll.sentence - beforeScroll.sentence)).toBeLessThanOrEqual(3);
  expect(afterScroll.progress).toBeGreaterThan(0.1);

  // Scrolling further after refresh must keep updating sentence and progress (fresh allChars cache).
  await clearMessages(page);
  await page.evaluate(() => window.scrollTo(0, scrollY + 2000));
  await waitMessages(page, 'scroll');
  await page.waitForTimeout(300);
  const laterMsgs = await messages(page, 'scroll');
  const laterScroll = laterMsgs[laterMsgs.length - 1];
  expect(laterScroll.sentence).toBeGreaterThan(afterScroll.sentence);
  expect(laterScroll.progress).toBeGreaterThan(afterScroll.progress);
});

test('dash (——) and script (老周：) dialogue lines render as bubbles and update cleanly', async ({ page }) => {
  const html = `<!DOCTYPE html>
<html lang="zh-Hans" data-yy-paper="plain" data-yy-mode="scrolled" data-yy-paging="snap" data-yy-justify="auto" data-yy-indent="auto" data-yy-compress="1" data-yy-motion="full" style="--yy-fs:18px;--yy-margin:24px;--yy-top:56px;--yy-bottom:88px;--yy-cols:1">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no">
<link rel="stylesheet" href="/app/reader.css">
</head>
<body>
<main id="yy-content" class="yy-section" data-section="0">
<h2 class="yy-title"><span class="s" data-s="0">第一章 雨夜</span></h2>
<p><span class="s" data-s="1">雨下了一整夜。</span><span class="s" data-s="2">老周推开门，把伞靠在墙边。</span></p>
<p><span class="s" data-s="3">——你又来晚了。</span><span class="s" data-s="4">阿青头也不抬，手里还在剥豆子。</span></p>
<p><span class="s" data-s="5">——路上积水，车开不快。</span><span class="s" data-s="6">老周搓着手坐下。</span></p>
<p><span class="s" data-s="7">老周：明天的船还开不开？</span></p>
<p><span class="s" data-s="8">阿青：开，天亮就开。</span><span class="s" data-s="9">你把东西收拾好。</span></p>
</main>
<script src="/app/reader.js"></script>
</body>
</html>`;
  await page.route('**/book/dash-dialogue-test.html', (route) =>
    route.fulfill({ status: 200, contentType: 'text/html; charset=utf-8', body: html }),
  );
  await open(page, 'dash-dialogue-test.html');
  const before = await page.evaluate(texts);

  const dashLines = [
    { first: 3, last: 3, start: 0, end: 8, who: '阿青', avatar: '青', color: '#3E6FA8', right: false },
    { first: 5, last: 5, start: 0, end: 12, who: '老周', avatar: '周', color: '#B5523E', right: true },
    { first: 7, last: 7, start: 3, end: 12, who: '老周', avatar: '周', color: '#B5523E', right: true },
    { first: 8, last: 8, start: 3, end: 10, who: '阿青', avatar: '青', color: '#3E6FA8', right: false },
  ];
  await page.evaluate((ls) => (window as any).yy.setBubbles(ls), dashLines);

  expect(await page.locator('#yy-content .yy-bq').count()).toBe(4);
  expect(await page.evaluate(texts)).toEqual(before);

  // Dash lines render inside bubble bodies with speaker avatars.
  await expect(page.locator('#yy-content .yy-bq[data-yy-line="0"] .yy-bq-bu')).toHaveText('——你又来晚了。');
  await expect(page.locator('#yy-content .yy-bq[data-yy-line="1"] .yy-bq-bu')).toHaveText('——路上积水，车开不快。');
  await expect(page.locator('#yy-content .yy-bq[data-yy-line="1"]')).toHaveClass(/yy-bq-r/);

  // Script lines place the `老周：`/`阿青：` tag in `.yy-bq-nm` and spoken text in `.yy-bq-bu`.
  await expect(page.locator('#yy-content .yy-bq[data-yy-line="2"] .yy-bq-nm')).toContainText('老周：');
  await expect(page.locator('#yy-content .yy-bq[data-yy-line="2"] .yy-bq-bu')).toHaveText('明天的船还开不开？');
  await expect(page.locator('#yy-content .yy-bq[data-yy-line="3"] .yy-bq-nm')).toContainText('阿青：');
  await expect(page.locator('#yy-content .yy-bq[data-yy-line="3"] .yy-bq-bu')).toHaveText('开，天亮就开。');

  // Re-calling setBubbles updates speaker/side cleanly without dropping bubbles.
  const updatedDash = dashLines.map((l, i) => (i === 0 ? { ...l, right: true } : l));
  await page.evaluate((ls) => (window as any).yy.setBubbles(ls), updatedDash);
  expect(await page.locator('#yy-content .yy-bq').count()).toBe(4);
  await expect(page.locator('#yy-content .yy-bq[data-yy-line="0"]')).toHaveClass(/yy-bq-r/);
  expect(await page.evaluate(texts)).toEqual(before);

  // Clearing restores plain text.
  await page.evaluate(() => (window as any).yy.setBubbles(null));
  expect(await page.locator('#yy-content .yy-bq').count()).toBe(0);
  expect(await page.evaluate(texts)).toEqual(before);
});

test('dark and kaminight papers preserve tinted bubbles, avatars, names, and eink wireframe', async ({ page }) => {
  await open(page, 'hlm/s/2.html', { mode: 'scrolled' });
  await page.evaluate((ls) => (window as any).yy.setBubbles(ls), lines);

  for (const paper of ['dark', 'kaminight'] as const) {
    await page.evaluate((p) => (window as any).yy.applySettings({ paper: p }), paper);
    const styles = await page.evaluate(() => {
      const known = document.querySelector('#yy-content .yy-bq') as HTMLElement;
      const bu = known.querySelector('.yy-bq-bu') as HTMLElement;
      const av = known.querySelector('.yy-bq-av') as HTMLElement;
      const nmB = known.querySelector('.yy-bq-nm b') as HTMLElement;
      const rootInk = getComputedStyle(document.documentElement).getPropertyValue('--yy-p-ink').trim();
      const rootBg = getComputedStyle(document.documentElement).getPropertyValue('--yy-p-bg').trim();
      return {
        rootInk,
        rootBg,
        buBg: getComputedStyle(bu).backgroundColor,
        buShadow: getComputedStyle(bu).boxShadow,
        avBg: getComputedStyle(av).backgroundColor,
        avColor: getComputedStyle(av).color,
        nmBColor: getComputedStyle(nmB).color,
        unkCount: document.querySelectorAll('#yy-content .yy-bq-unk').length,
      };
    });
    expect(styles.buBg).not.toBe('rgba(0, 0, 0, 0)');
    expect(styles.buShadow).not.toBe('none');
    expect(styles.avBg).not.toBe('rgba(0, 0, 0, 0)');
    expect(styles.unkCount).toBe(0);
    // Speaker name & avatar text must be tinted rather than collapsing to transparent or pure black.
    expect(styles.avColor).not.toBe('rgba(0, 0, 0, 0)');
    expect(styles.nmBColor).not.toBe('rgba(0, 0, 0, 0)');
    await page.screenshot({ path: `${SCREENS}/bubbles-${paper}.png` });
  }

  // E-ink mode must still force monochrome wireframe even if data-yy-paper="dark".
  await page.evaluate(() => {
    (window as any).yy.applySettings({ paper: 'dark' });
    document.documentElement.setAttribute('data-yy-eink', '1');
  });
  const einkStyles = await page.evaluate(() => {
    const known = document.querySelector('#yy-content .yy-bq') as HTMLElement;
    const bu = known.querySelector('.yy-bq-bu') as HTMLElement;
    const av = known.querySelector('.yy-bq-av') as HTMLElement;
    return {
      buBg: getComputedStyle(bu).backgroundColor,
      buShadow: getComputedStyle(bu).boxShadow,
      avBg: getComputedStyle(av).backgroundColor,
      avColor: getComputedStyle(av).color,
    };
  });
  expect(einkStyles.buBg).toBe('rgba(0, 0, 0, 0)');
  expect(einkStyles.buShadow).toContain('rgb(0, 0, 0)');
  expect(einkStyles.avBg).toBe('rgba(0, 0, 0, 0)');
  expect(einkStyles.avColor).toBe('rgb(0, 0, 0)');
});
