// 有声书 (yy.setFlag): an amber line beside the paragraphs of a stretch the recording doesn't
// line up with, without moving the text.
import { test, expect, open, SCREENS } from './util';

test('setFlag marks the paragraphs holding the sentences, and moves nothing', async ({ page }) => {
  await open(page, 'beiying/s/0.html');
  const before = await page.evaluate(() => {
    const el = document.querySelector('#yy-content .s[data-s="3"]') as HTMLElement;
    return el.getClientRects()[0].left;
  });
  const flagged = await page.evaluate(() => {
    (window as any).yy.setFlag(3, 4);
    return [...document.querySelectorAll('.yy-flag')].map((el) => {
      const cs = getComputedStyle(el, '::before');
      return { width: cs.width, bg: cs.backgroundColor };
    });
  });
  expect(flagged.length).toBeGreaterThan(0);
  for (const b of flagged) {
    expect(b.width).toBe('2.5px');
    expect(b.bg).not.toBe('rgba(0, 0, 0, 0)');
  }
  const after = await page.evaluate(() => (document.querySelector('#yy-content .s[data-s="3"]') as HTMLElement).getClientRects()[0].left);
  expect(after).toBe(before);
  // Narrow page margins clamp the bar inside the column edge, and host margins stay untouched.
  const narrow = await page.evaluate(() => {
    (window as any).yy.applySettings({ margin: 6 });
    const el = document.querySelector('.yy-flag') as HTMLElement;
    const left = el.getBoundingClientRect().left + parseFloat(getComputedStyle(el, '::before').left);
    (window as any).yy.applySettings({ margin: 24 });
    return left;
  });
  expect(narrow).toBeGreaterThanOrEqual(2);
  await page.screenshot({ path: `${SCREENS}/flag.png` });
  await page.evaluate(() => (window as any).yy.setFlag(null));
  expect(await page.locator('.yy-flag').count()).toBe(0);
  // The chapter heading too.
  const heading = await page.evaluate(() => {
    (window as any).yy.setFlag(0, 0);
    const el = document.querySelector('.yy-flag');
    return el ? el.tagName + ' ' + getComputedStyle(el, '::before').width + ' ' + getComputedStyle(el, '::before').backgroundColor : null;
  });
  expect(heading).toContain('2.5px');
  await page.screenshot({ path: `${SCREENS}/flag-heading.png` });
});

test('paragraphs and setTranslations support Chinese paragraphs and custom lang attribute', async ({ page }) => {
  await open(page, 'beiying/s/0.html');
  const paras = await page.evaluate(() => (window as any).yy.paragraphs());
  expect(paras.length).toBeGreaterThan(0);
  expect(paras[0].text.length).toBeGreaterThan(0);

  const firstLast = paras[0].last;
  const rendered = await page.evaluate((last: number) => {
    (window as any).yy.setTranslations([{ last, text: 'Modern vernacular translation.', lang: 'en' }]);
    const tr = document.querySelector('yy-tr');
    return tr ? { text: tr.textContent, lang: tr.getAttribute('lang') } : null;
  }, firstLast);
  expect(rendered).toEqual({ text: 'Modern vernacular translation.', lang: 'en' });

  await page.evaluate(() => (window as any).yy.setTranslations(null));
  expect(await page.locator('yy-tr').count()).toBe(0);
});
