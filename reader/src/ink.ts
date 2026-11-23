// The ink line (墨线, contract §7): a 1.5px line under the sentence being read that grows over
// the sentence's audio duration.
//
// Each line box of the sentence's fragments gets a bar in <yy-ink>, grown on the compositor with
// `transform: scaleX(0) -> scaleX(1)` (`transform-origin: 0 50%`) so no text or inline box is
// repainted per frame. Bars run back to back in reading order across line and column breaks:
// fragment i gets its share of the sentence's characters, and each line box inside it gets its
// share of the fragment's width. Seeking is `currentTime`, pausing is `pause()`.
//
// The same proportions predict when the head of the line crosses onto another page: inside a
// fragment the head moves at constant speed along the concatenated line boxes, so the moment it
// reaches line box j is start + dur * (width of the boxes before j / total width).

import { post } from './bridge';
import { fragmentsOf, pageOfRect, state } from './layout';

interface Bar {
  el: HTMLElement;
  start: number;
  dur: number;
  anim: Animation | null;
}

let active: number | null = null;
let activeEls: HTMLElement[] = [];
let bars: Bar[] = [];
let inkLayer: HTMLElement | null = null;
let total = 0;
let playing = false;
let full = false;
// The reader's own clock: `base` ms of the sentence had elapsed at performance time `at`.
let base = 0;
let at = 0;
let events: { t: number; page: number }[] = [];
let timers: number[] = [];
let postedPage = -1;

const now = (): number => (window.performance && performance.now ? performance.now() : Date.now());
const reducedMotion =
  typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia('(prefers-reduced-motion: reduce)')
    : null;

export function elapsed(): number {
  const e = playing ? base + (now() - at) : base;
  return Math.max(0, Math.min(total, e));
}

function spokenLength(el: Element): number {
  let n = 0;
  const w = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, null);
  for (let t = w.nextNode(); t; t = w.nextNode()) {
    let skip = false;
    for (let p = t.parentNode as Element | null; p && p !== el.parentNode; p = p.parentNode as Element | null) {
      if (p.nodeName === 'RT' || p.nodeName === 'RP' || (p.classList && p.classList.contains('yy-noteref'))) {
        skip = true;
        break;
      }
    }
    if (!skip) n += (t as Text).data.replace(/\s+/g, ' ').length;
  }
  return n;
}

function useFull(): boolean {
  const root = document.documentElement;
  return (
    root.getAttribute('data-yy-motion') === 'reduced' ||
    root.getAttribute('data-yy-eink') === '1' ||
    !!(reducedMotion && reducedMotion.matches) ||
    typeof (document.documentElement as HTMLElement).animate !== 'function'
  );
}

function inkHeightPx(): number {
  const v = parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--yy-ink-w'));
  return v > 0 ? v : 1.5;
}

function clearTimers(): void {
  for (const t of timers) clearTimeout(t);
  timers = [];
}

function teardown(): void {
  clearTimers();
  stopWords();
  for (const b of bars) if (b.anim) b.anim.cancel();
  bars = [];
  if (inkLayer) {
    inkLayer.textContent = '';
    inkLayer.classList.remove('yy-ink-lite');
  }
  for (const el of activeEls) el.classList.remove('yy-ink', 'yy-ink-full', 'yy-ink-lite');
  activeEls = [];
  events = [];
}

function pageAt(e: number): number {
  let page = events.length ? events[0].page : -1;
  for (const ev of events) {
    if (ev.t <= e) page = ev.page;
    else break;
  }
  return page;
}

function emit(page: number): void {
  if (active == null || page < 0 || page === postedPage) return;
  postedPage = page;
  post({ type: 'activePage', sentence: active, page });
}

function schedule(): void {
  clearTimers();
  if (!playing || active == null) return;
  const e = elapsed();
  for (const ev of events) {
    if (ev.t <= e) continue;
    const page = ev.page;
    timers.push(window.setTimeout(() => emit(page), ev.t - e));
  }
}

function sync(b: Bar, e: number): void {
  const a = b.anim;
  if (!a) return;
  const end = b.start + b.dur;
  a.currentTime = Math.max(0, Math.min(end, e));
  // play() on a finished animation rewinds it to the start, so only resume unfinished ones.
  if (playing && e < end) {
    if (a.playState !== 'running') a.play();
  } else if (a.playState !== 'paused') {
    a.pause();
  }
}

export interface ActiveOptions {
  durationMs?: number | null;
  elapsedMs?: number | null;
  playing?: boolean | null;
  /** 逐词跟读: when each word starts, as [character offset in the sentence, ms] pairs laid end
   *  to end. The sentence's line goes pale and the word being said gets its own line. */
  words?: number[] | null;
}

// 逐词跟读: each word's range and start, and the one underlined now.
let words: { range: Range; t: number }[] = [];
let wordShown = -1;
let wordTimer = 0;

/** The range of the sentence's text from character `a` to `b` (code points), across its
 *  fragments' text nodes. */
function rangeOf(frags: HTMLElement[], a: number, b: number): Range | null {
  const r = document.createRange();
  let pos = 0;
  let started = false;
  for (const f of frags) {
    const walker = document.createTreeWalker(f, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode() as Text | null; n; n = walker.nextNode() as Text | null) {
      const cps = Array.from(n.data);
      const end = pos + cps.length;
      const unit = (k: number) => cps.slice(0, k).join('').length;
      if (!started && a < end) {
        r.setStart(n, unit(Math.max(0, a - pos)));
        started = true;
      }
      if (started && b <= end) {
        r.setEnd(n, unit(Math.max(0, b - pos)));
        return r;
      }
      pos = end;
    }
  }
  return started ? r : null;
}

function showWord(): void {
  if (wordTimer) {
    clearTimeout(wordTimer);
    wordTimer = 0;
  }
  const hl = (window as unknown as { CSS?: { highlights?: Map<string, unknown> } }).CSS?.highlights;
  const H = (window as unknown as { Highlight?: new (r: Range) => unknown }).Highlight;
  if (!hl || !H || full || !words.length) return;
  const e = elapsed();
  let i = -1;
  for (let k = 0; k < words.length && words[k].t <= e; k++) i = k;
  if (i !== wordShown) {
    wordShown = i;
    if (i >= 0) hl.set('yy-word', new H(words[i].range));
    else hl.delete('yy-word');
  }
  if (playing && i + 1 < words.length) {
    wordTimer = window.setTimeout(showWord, Math.max(0, words[i + 1].t - e));
  }
}

function stopWords(): void {
  if (wordTimer) clearTimeout(wordTimer);
  wordTimer = 0;
  words = [];
  wordShown = -1;
  const hl = (window as unknown as { CSS?: { highlights?: Map<string, unknown> } }).CSS?.highlights;
  hl?.delete('yy-word');
}

function startWords(frags: HTMLElement[], list: number[] | null | undefined): void {
  stopWords();
  if (full || !list || list.length < 4) return;
  const len = frags.reduce((n, f) => n + Array.from(f.textContent || '').length, 0);
  for (let k = 0; k + 1 < list.length; k += 2) {
    const a = list[k];
    const b = k + 2 < list.length ? list[k + 2] : len;
    const range = b > a ? rangeOf(frags, a, b) : null;
    if (range) words.push({ range, t: list[k + 1] });
  }
  for (const f of frags) f.classList.add('yy-ink-lite');
  if (inkLayer) inkLayer.classList.add('yy-ink-lite');
  showWord();
}

export function setActive(s: number | null, opts?: ActiveOptions | null): void {
  const o = opts || {};
  if (s == null || !(s >= 0) || !fragmentsOf(s).length) {
    teardown();
    active = null;
    total = 0;
    base = 0;
    postedPage = -1;
    return;
  }
  const e = o.elapsedMs != null && o.elapsedMs >= 0 ? o.elapsedMs : 0;
  const play = o.playing != null ? !!o.playing : true;
  const frags = fragmentsOf(s);
  const lens = frags.map(spokenLength);
  const chars = lens.reduce((a, b) => a + b, 0);
  const dur = o.durationMs != null && o.durationMs > 0 ? o.durationMs : Math.max(1800, chars * 120);

  if (s === active && dur === total && full === useFull()) {
    // Same sentence: just seek / pause / resume.
    base = Math.min(dur, e);
    at = now();
    playing = play;
    for (const g of bars) sync(g, base);
    schedule();
    if (o.words !== undefined) startWords(frags, o.words);
    else if (words.length) showWord();
    emit(pageAt(base));
    return;
  }
  if (s !== active) postedPage = -1;
  build(s, dur, e, play, o.words);
}

let lastWords: number[] | null = null;

function build(s: number, dur: number, e: number, play: boolean, list: number[] | null | undefined = lastWords, announce = true): void {
  lastWords = list ?? null;
  teardown();
  const frags = fragmentsOf(s);
  const lens = frags.map(spokenLength);
  const chars = lens.reduce((a, b) => a + b, 0);
  active = s;
  activeEls = frags.slice();
  total = dur;
  base = Math.min(dur, e);
  at = now();
  playing = play;
  full = useFull();
  let acc = 0;
  const h = inkHeightPx();
  const dx = window.pageXOffset;
  const dy = window.pageYOffset;
  const out = full ? null : document.createDocumentFragment();
  frags.forEach((el, i) => {
    const share = chars > 0 ? lens[i] / chars : 1 / frags.length;
    const segStart = dur * acc;
    const segDur = Math.max(1, dur * share);
    acc += share;
    el.classList.add('yy-ink');
    if (full) el.classList.add('yy-ink-full');
    const rs = el.getClientRects();
    const boxes: { left: number; bottom: number; w: number; page: number }[] = [];
    let sum = 0;
    for (let k = 0; k < rs.length; k++) {
      if (rs[k].width <= 0) continue;
      boxes.push({ left: rs[k].left, bottom: rs[k].bottom, w: rs[k].width, page: pageOfRect(rs[k]) });
      sum += rs[k].width;
    }
    let before = 0;
    for (const b of boxes) {
      const boxStart = segStart + (sum > 0 ? (segDur * before) / sum : 0);
      const boxDur = Math.max(1, sum > 0 ? (segDur * b.w) / sum : segDur);
      const last = events[events.length - 1];
      if (!last || last.page !== b.page) events.push({ t: boxStart, page: b.page });
      before += b.w;
      if (out) {
        const bar = document.createElement('i');
        bar.style.cssText = `left:${b.left + dx}px;top:${b.bottom + dy - h}px;width:${b.w}px;height:${h}px`;
        out.appendChild(bar);
        bars.push({ el: bar, start: boxStart, dur: boxDur, anim: null });
      }
    }
  });
  if (out && out.childNodes.length) {
    if (!inkLayer) {
      inkLayer = document.createElement('yy-ink');
      inkLayer.setAttribute('aria-hidden', 'true');
      document.documentElement.appendChild(inkLayer);
    }
    inkLayer.appendChild(out);
    for (const g of bars) {
      g.anim = g.el.animate([{ transform: 'scaleX(0)' }, { transform: 'scaleX(1)' }], {
        duration: g.dur,
        delay: g.start,
        fill: 'both',
        easing: 'linear',
      });
    }
  }
  for (const g of bars) sync(g, base);
  schedule();
  startWords(frags, list);
  const page = pageAt(base);
  if (announce) emit(page);
  else if (page >= 0) postedPage = page;
}

export function setPlaying(p: boolean): void {
  if (active == null) {
    playing = !!p;
    return;
  }
  if (!!p === playing) return;
  base = elapsed();
  at = now();
  playing = !!p;
  for (const g of bars) sync(g, base);
  schedule();
  if (words.length) showWord();
}

/** Builds the line again on the sentence's new fragments, after the content was rebuilt (气泡). */
export function rebuild(): void {
  if (active != null) build(active, total, elapsed(), playing, lastWords, false);
}

/** After a relayout or a paper/motion change: same sentence and timing, new geometry. */
export function refresh(): void {
  if (active != null) build(active, total, elapsed(), playing, lastWords, false);
}

if (reducedMotion && reducedMotion.addEventListener) {
  reducedMotion.addEventListener('change', () => refresh());
}

/** Debug/test view of the ink state. */
export function inkState(): {
  sentence: number | null;
  elapsed: number;
  duration: number;
  playing: boolean;
  full: boolean;
  events: { t: number; page: number }[];
  pageCount: number;
} {
  return { sentence: active, elapsed: elapsed(), duration: total, playing, full, events: events.slice(), pageCount: state.pageCount };
}
