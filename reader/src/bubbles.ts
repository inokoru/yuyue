// 气泡模式: a book's dialogue as a chat. Narration stays a paragraph; each quote with a confirmed
// speaker becomes a bubble with whoever says it. Lines whose speaker isn't confirmed stay as clean
// normal prose (never rendered as `? 谁说的？` prompts). Short narration that only says who speaks
// (“宝钗因笑道：”) goes into the line above the bubble instead of a paragraph of its own.
//
// Only the boxes change: every sentence keeps its `.s[data-s]` fragments, split where a quote
// starts or ends (the contract allows a sentence in several fragments), so the ink line, marks,
// selection and taps work as before. Names and avatars are outside the fragments and never count
// as text. setBubbles(null) puts the section back as it came.

import { reindex } from './layout';
import { sentenceText, textNodes } from './marks';

export interface BubbleLine {
  first: number;
  last: number;
  /** Characters (Unicode scalars) into the first sentence: where the quote's text starts and ends. */
  start: number;
  end: number;
  who: string | null;
  avatar: string;
  color: string;
  right: boolean;
  /** A line nobody is sure of: the two people it most likely is, offered on the bubble. */
  choices?: string[];
}

let original: string | null = null;

const OPEN = '“"「『';
const CLOSE = '”"」』';
const BLOCKS = ['P', 'DIV', 'LI', 'BLOCKQUOTE', 'DD'];

function content(): HTMLElement {
  return (document.getElementById('yy-content') || document.body) as HTMLElement;
}

/** A character index (scalars, as core counts) as a UTF-16 offset into the sentence's text. */
function utf16(text: string, chars: number): number {
  let n = 0;
  let k = 0;
  for (const ch of text) {
    if (k >= chars) break;
    n += ch.length;
    k++;
  }
  return n;
}

/** The DOM place `o` UTF-16 units into sentence `s`. */
function place(s: number, o: number): { node: Text; offset: number } | null {
  const nodes = textNodes(s);
  let left = o;
  for (const t of nodes) {
    if (left <= t.data.length) return { node: t, offset: left };
    left -= t.data.length;
  }
  const last = nodes[nodes.length - 1];
  return last ? { node: last, offset: last.data.length } : null;
}

function blockOf(n: Node): HTMLElement | null {
  const root = content();
  for (let el = n.parentElement; el && el !== root; el = el.parentElement) {
    if (BLOCKS.indexOf(el.nodeName) >= 0) return el;
  }
  return null;
}

/** Where a line's quote starts and ends, quote marks included. */
function quoteRange(l: BubbleLine): { a: { node: Text; offset: number }; b: { node: Text; offset: number } } | null {
  const first = sentenceText(l.first);
  let from = utf16(first, Math.max(0, l.start - 1));
  if (!OPEN.includes(first.charAt(from))) from = utf16(first, l.start);
  let to: number;
  let endS = l.first;
  if (l.last === l.first) {
    to = utf16(first, l.end);
    if (CLOSE.includes(first.charAt(to))) to += 1;
  } else {
    endS = l.last;
    const last = sentenceText(l.last);
    // The first close: what's after it is narration, or the next line (core's `lines` goes on there).
    let k = -1;
    for (const c of CLOSE) {
      const i = last.indexOf(c);
      if (i >= 0 && (k < 0 || i < k)) k = i;
    }
    to = k >= 0 ? k + 1 : last.length;
  }
  const a = place(l.first, from);
  const b = place(endS, to);
  return a && b ? { a, b } : null;
}

/** The range's contents with the inline elements around them kept (the sentence's `.s`, an
 *  `<em>`): cloneContents drops whatever wraps the range's common ancestor. */
function cut(r: Range, block: HTMLElement): DocumentFragment {
  let f = r.cloneContents();
  let n: Node | null = r.commonAncestorContainer;
  if (n.nodeType !== Node.ELEMENT_NODE) n = n.parentNode;
  for (; n && n !== block; n = n.parentNode) {
    const wrap = (n as Element).cloneNode(false);
    wrap.appendChild(f);
    f = document.createDocumentFragment();
    f.appendChild(wrap);
  }
  return f;
}

function textOf(f: DocumentFragment): string {
  return (f.textContent || '').replace(/\s+/g, '');
}

/** Narration that only says who's speaking, short enough for the line above the bubble. */
function isTag(text: string): boolean {
  return text.length > 0 && text.length <= 14 && /[：:，,]$/.test(text) && !/[。！？.!?]/.test(text);
}

/** Bold the speaker's name where the narration writes it. */
function boldName(f: DocumentFragment, who: string): boolean {
  const w = document.createTreeWalker(f, NodeFilter.SHOW_TEXT, null);
  for (let t = w.nextNode() as Text | null; t; t = w.nextNode() as Text | null) {
    const i = t.data.indexOf(who);
    if (i < 0) continue;
    const r = document.createRange();
    r.setStart(t, i);
    r.setEnd(t, i + who.length);
    r.surroundContents(document.createElement('b'));
    return true;
  }
  return false;
}

function el(tag: string, cls: string): HTMLElement {
  const e = document.createElement(tag);
  e.className = cls;
  return e;
}

/** Visually hide redundant outer quote marks or trailing tag colons while keeping textContent intact. */
function hideChar(t: Text, index: number): void {
  if (index < 0 || index >= t.data.length) return;
  const r = document.createRange();
  r.setStart(t, index);
  r.setEnd(t, index + 1);
  const span = document.createElement('span');
  span.className = 'yy-bq-q';
  r.surroundContents(span);
}

function hideOuterQuotes(f: DocumentFragment): void {
  const nodes: Text[] = [];
  const w = document.createTreeWalker(f, NodeFilter.SHOW_TEXT, null);
  for (let t = w.nextNode() as Text | null; t; t = w.nextNode() as Text | null) {
    if (t.data.length) nodes.push(t);
  }
  if (!nodes.length) return;
  const first = nodes.find((t) => t.data.trim().length > 0);
  const last = [...nodes].reverse().find((t) => t.data.trim().length > 0);
  if (!first || !last) return;
  const openIdx = first.data.search(/\S/);
  const closeMatch = last.data.match(/(\S)\s*$/);
  const closeIdx = closeMatch ? last.data.lastIndexOf(closeMatch[1]) : -1;
  const hasOpen = openIdx >= 0 && OPEN.includes(first.data.charAt(openIdx));
  const hasClose = closeIdx >= 0 && CLOSE.includes(last.data.charAt(closeIdx));
  // Hide closing quote first so offsets in the same Text node remain valid for opening quote.
  if (hasClose && !(first === last && closeIdx === openIdx)) hideChar(last, closeIdx);
  if (hasOpen) hideChar(first, openIdx);
}

function hideTagDelimiter(f: DocumentFragment): void {
  const nodes: Text[] = [];
  const w = document.createTreeWalker(f, NodeFilter.SHOW_TEXT, null);
  for (let t = w.nextNode() as Text | null; t; t = w.nextNode() as Text | null) {
    if (t.data.trim().length) nodes.push(t);
  }
  const last = nodes[nodes.length - 1];
  if (!last) return;
  const m = last.data.match(/([：:，,])\s*$/);
  if (!m) return;
  const idx = last.data.lastIndexOf(m[1]);
  if (idx >= 0) hideChar(last, idx);
}

function bubble(l: BubbleLine, index: number, quote: DocumentFragment, tag: DocumentFragment | null, cont: boolean): HTMLElement {
  const who = l.who!;
  const q = el('div', 'yy-bq' + (l.right ? ' yy-bq-r' : '') + (cont ? ' yy-bq-cont' : ''));
  q.setAttribute('data-yy-line', String(index));
  q.style.setProperty('--yy-c', l.color);
  if (!cont || tag) {
    const nm = el('div', 'yy-bq-nm');
    if (tag) {
      hideTagDelimiter(tag);
      if (!boldName(tag, who)) {
        const b = el('b', 'yy-bq-who');
        b.textContent = who;
        nm.appendChild(b);
      }
      nm.appendChild(tag);
    } else {
      const b = el('b', 'yy-bq-who');
      b.textContent = who;
      nm.appendChild(b);
    }
    q.appendChild(nm);
  }
  const av = el('span', 'yy-bq-av');
  av.setAttribute('aria-hidden', 'true');
  av.textContent = l.avatar;
  const bx = el('div', 'yy-bq-bx');
  const bu = el('div', 'yy-bq-bu');
  hideOuterQuotes(quote);
  bu.appendChild(quote);
  bx.appendChild(bu);
  q.appendChild(av);
  q.appendChild(bx);
  return q;
}

function rebuild(block: HTMLElement, lines: { l: BubbleLine; i: number }[]): void {
  const out: HTMLElement[] = [];
  const cursor = document.createRange();
  cursor.setStart(block, 0);
  let lastWho: string | null | undefined;
  const narration = (f: DocumentFragment) => {
    if (!textOf(f)) return;
    const p = block.cloneNode(false) as HTMLElement;
    p.removeAttribute('id');
    p.classList.add('yy-bn');
    p.appendChild(f);
    out.push(p);
    lastWho = undefined;
  };
  for (const { l, i } of lines) {
    const q = quoteRange(l);
    if (!q) continue;
    const before = document.createRange();
    before.setStart(cursor.startContainer, cursor.startOffset);
    before.setEnd(q.a.node, q.a.offset);
    const pre = cut(before, block);
    const preText = textOf(pre);
    let tag: DocumentFragment | null = null;
    if (preText && l.who && isTag(preText)) tag = pre;
    else narration(pre);
    const qr = document.createRange();
    qr.setStart(q.a.node, q.a.offset);
    qr.setEnd(q.b.node, q.b.offset);
    const cont = !tag && lastWho !== undefined && lastWho === l.who && !!l.who;
    out.push(bubble(l, i, cut(qr, block), tag, cont));
    lastWho = l.who;
    cursor.setStart(q.b.node, q.b.offset);
  }
  const after = document.createRange();
  after.setStart(cursor.startContainer, cursor.startOffset);
  after.setEnd(block, block.childNodes.length);
  narration(cut(after, block));
  if (!out.length) return;
  if (block.id) out[0].id = block.id;
  block.replaceWith(...out);
}

/** Lays the section out as bubbles for `lines`, or (null) puts it back as it came. Returns
 *  whether anything changed. */
/** What a click on a bubble means: its avatar (who says it), or one of the names it offers. */
export function bubbleHit(target: Element | null): { line: number; choice: string | null } | null {
  if (!target || !target.closest) return null;
  const q = target.closest('.yy-bq');
  if (!q) return null;
  const line = Number(q.getAttribute('data-yy-line'));
  if (!isFinite(line)) return null;
  const ch = target.closest('.yy-bq-ch');
  if (ch) return { line, choice: ch.getAttribute('data-yy-who') || '' };
  if (target.closest('.yy-bq-av') || target.closest('.yy-bq-who')) return { line, choice: null };
  return null;
}

export function setBubbles(lines: BubbleLine[] | null): boolean {
  const root = content();
  if (original != null) {
    root.innerHTML = original;
    original = null;
    document.documentElement.removeAttribute('data-yy-bubbles');
    if (!lines || !lines.length) return true;
    reindex();
  }
  if (!lines || !lines.length) return false;
  // Keep original array indices for passive avatar taps, but only turn confirmed speakers into
  // bubbles; unattributed quotes stay as clean normal prose.
  const attributed = lines
    .map((l, i) => ({ l, i }))
    .filter(({ l }) => Boolean(l.who && l.who.trim()));
  if (!attributed.length) return false;
  original = root.innerHTML;
  document.documentElement.setAttribute('data-yy-bubbles', '1');
  // Lines by the paragraph they're in; a quote running out of its paragraph stays as text.
  const byBlock = new Map<HTMLElement, { l: BubbleLine; i: number }[]>();
  for (const { l, i } of attributed.sort((x, y) => x.l.first - y.l.first || x.l.start - y.l.start)) {
    const q = quoteRange(l);
    if (!q) continue;
    const b = blockOf(q.a.node);
    if (!b || b !== blockOf(q.b.node)) continue;
    if (!byBlock.has(b)) byBlock.set(b, []);
    byBlock.get(b)!.push({ l, i });
  }
  if (!byBlock.size) {
    original = null;
    document.documentElement.removeAttribute('data-yy-bubbles');
    return false;
  }
  // Every range is worked out before any paragraph is replaced: replacing one leaves the
  // others' text nodes where they were.
  byBlock.forEach((ls, b) => rebuild(b, ls));
  return true;
}
