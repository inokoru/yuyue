# Reader contract

How the Rust core, the TypeScript reader (`reader/`) and the native apps talk to each other
on the reading screen.

Division of labour:
- **Core** parses the book once at import and writes one normalized HTML file per section.
  It also assembles the full document the WebView loads.
- **Reader** (runs inside the WebView) lays the section out in pages, draws the marks that live
  inside the text (the ink line under the sentence being read, later highlights), and reports
  geometry to native. It never animates page turns and never parses books.
- **Native** owns everything that moves: page turns (iOS: the WebView's own `UIScrollView`
  with paging; Mac/Android/Windows: CSS scroll snapping inside the WebView), chapter-to-chapter
  paging, gestures, floating cards, audio.

## 1. URLs

Everything is served from one origin so relative URLs and fonts work without CORS:

| Platform | Origin |
|---|---|
| Apple | `yuyue://local` (a `WKURLSchemeHandler`) |
| Android | `https://appassets.androidplatform.net` (`shouldInterceptRequest`) |
| Windows | `https://yuyue.local` (WebView2 `WebResourceRequested`) |

| Path | Content |
|---|---|
| `/app/reader.js`, `/app/reader.css` | the reader bundle (`reader/dist/`) |
| `/book/<bookId>/s/<n>.html` | the document for section `n` (assembled by core on request) |
| `/book/<bookId>/r/<path>` | book resources (images, CSS, fonts), original paths from the EPUB |

Inside a section document, resources are referenced relatively (`../r/OEBPS/images/x.jpg`).

## 2. The section document

```html
<!DOCTYPE html>
<html lang="zh-CN" data-yy-paper="plain" data-yy-mode="paginated" data-yy-paging="native"
      data-yy-justify="auto" data-yy-indent="auto" data-yy-compress="1" data-yy-motion="full"
      style="--yy-fs:18px;--yy-margin:24px;--yy-top:56px;--yy-bottom:88px;--yy-cols:1">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no, viewport-fit=cover">
<link rel="stylesheet" href="../r/OEBPS/style.css">      <!-- publisher CSS, may be absent -->
<style>/* publisher <style> blocks from the source <head>, if any */</style>
<link rel="stylesheet" href="/app/reader.css">           <!-- last, so it can override -->
</head>
<body>
<main id="yy-content" class="yy-section" data-section="3">
  …normalized content…
</main>
<script src="/app/reader.js"></script>
</body>
</html>
```

`<html>` attributes and custom properties (the same names are used by `yy.applySettings`).
我的纸 (a paper the reader made) is the preset it reads like in `data-yy-paper` plus its colours as
inline `--yy-p-*` properties and `color-scheme` on `<html>` (core's `paper_style`; `paperStyle` in
`applySettings`, `null` to clear them).

Typography that differs per paper (font, line height, paragraph spacing, indent, justify) has a
paper default in the reader CSS: `auto` or an absent variable means "use the paper's default".

| Name | Values | Meaning |
|---|---|---|
| `data-yy-paper` | `plain` `dark` `kami` `kaminight` `sage` `eink` | paper theme (素白、暗黑、纸本、纸本·夜、芦苇、铅纸) |
| `data-yy-texture` | `grain` `xuan` `linen` or absent | 纹理 on 我的纸 (细颗粒、宣纸、亚麻) |
| `data-yy-eink` | `0` `1` or absent | device-local 墨水屏模式: pure white/black high-contrast surface without grain, black mark lines, no animation; overrides synced paper & 我的纸 |
| `data-yy-eink-gray` | `0` `1` or absent | grayscale images/videos while `data-yy-eink="1"` (off on colour e-ink screens) |
| `data-yy-mode` | `paginated` `scrolled` | 翻页 or 滚动 |
| `data-yy-paging` | `native` `snap` | `native`: the platform scroll view pages (iOS), so the reader must not add scroll snapping or animate scrolling. `snap`: the reader adds CSS scroll snapping and the JS page API scrolls smoothly (Mac, Android, Windows) |
| `data-yy-justify` | `auto` `0` `1` | 两端对齐 |
| `data-yy-indent` | `auto` `0` `1` | 首行缩进 2em |
| `data-yy-compress` | `0` `1` | 中文标点挤压 (applies the `pz-*` classes below) |
| `data-yy-motion` | `full` `reduced` | `reduced`: no ink-line animation (墨水屏模式, system reduced motion, 沉浸 option) |
| `data-yy-tint` | `blue` `ink` or absent | accent ink palette override (墨蓝 / 墨黑) |
| `data-yy-ui` | `zh` `en` | UI language used for reader-generated labels (set via `yy.setUi`) |
| `--yy-fs` | px | font size (always set) |
| `--yy-lh` | number | line height |
| `--yy-ps` | em | paragraph spacing (margin after a paragraph) |
| `--yy-font` | font stack | e.g. `var(--yy-sans)`, `var(--yy-serif)`, `var(--yy-kaiti)`, `var(--yy-wenkai)`, `var(--yy-mono)` |
| `--yy-margin` | px | left/right page margin (always set) |
| `--yy-top`, `--yy-bottom` | px | top/bottom page padding, chosen by native so text clears the status bar, safe areas and floating controls (always set) |
| `--yy-cols` | `1` `2` | columns per page (2 = iPad/Mac landscape 双栏) |

## 3. Normalized content (written by core)

- **Sentences.** Every sentence is wrapped in `<span class="s" data-s="K">`, `K` = 0-based index
  within the section. A sentence that crosses inline markup is split into several fragments with
  the same `data-s`, in document order:
  `<p><span class="s" data-s="0">Hello </span><em><span class="s" data-s="0">world.</span> <span class="s" data-s="1">Next</span></em><span class="s" data-s="1"> one.</span></p>`
- **Not spoken, not part of any sentence:** `<rt>`/`<rp>`, footnote references
  (`<a class="yy-noteref">`), and the chapter meta line
  `<div class="yy-meta">读 8 分钟 · 听 12 分钟</div>` (inserted after the first heading of a
  section that starts a table-of-contents chapter).
- **Punctuation compression.** For adjacent full-width punctuation core wraps the character that
  loses half its width: `<span class="pz-r">。</span>` (drop the blank on the right — closing
  punctuation followed by more punctuation) and `<span class="pz-l">“</span>` (drop the blank on the
  left — opening punctuation preceded by an opening one). Only for Chinese/Japanese books.
- **Links.** `href` is always `#`. Internal: `data-yy-target="<section>#<anchor>"`. External:
  `data-yy-external="https://…"`.
- **Generated headings** (TXT imports): `<h2 class="yy-title">第一章 …</h2>`.
- Element `id`s from the book are kept (anchors). Scripts, event handlers, forms and iframes are gone.

## 4. JavaScript API (native → reader)

`window.yy`:

| Call | Effect |
|---|---|
| `applySettings(s)` | `s` has the keys of §2 in camelCase (`paper`, `paperStyle`, `texture`, `eink`, `einkGray`, `tint`, `mode`, `paging`, `justify`, `indent`, `compress`, `motion`, `fontSize`, `lineHeight`, `paragraphSpacing`, `font`, `margin`, `top`, `bottom`, `cols`); missing keys keep their current value, `null` means paper default. Re-lays out, keeps the first visible sentence in view, posts `layout` |
| `scrollToPage(p, animated)` | go to page `p`. In `native` paging always instant (native animates by itself) |
| `nextPage(animated)` / `prevPage(animated)` | returns `false` (and does nothing) at the last/first page, so native can move to the next/previous section |
| `scrollToSentence(s, animated)` | go to the page where sentence `s` starts (scrolled mode: bring it to the top third); returns the page |
| `scrollToAnchor(id, animated)` | same for an element id; returns the page or `-1` |
| `currentPage()` | current page index |
| `setActive(s, {durationMs, elapsedMs, playing})` | draw the ink line under sentence `s`; `s = null` clears it |
| `setPlaying(playing)` | pause/resume the ink line |
| `setMarks(list)` | draw the section's highlights (§8): `[{id, start: {s, o}, end: {s, o}, style, note}]` |
| `setHits(query, current)` | mark every place `query` turns up; `current` (`{s, nth}`) gets the solid colour; returns the count. `null` clears |
| `setSource(s)` | highlight the cited source sentence `s` jumped from Book Companion; `null` clears |
| `setUi(ui)` | set reader UI language (`'zh'` or `'en'`) for reader-rendered labels |
| `setLookup(on)` / `setLooking(start, end)` | toggle tap-to-look-up mode / highlight the currently looked-up word span |
| `setFlag(first, last)` | audiobook alignment: draw an amber marker beside paragraphs spanning sentences `first..last` (`null` clears) |
| `setUnread(list, tag)` | audiobook alignment: dim sentences not spoken in the recording and show `tag` after each run (`null` clears) |
| `setVocab(words)` / `setGlosses(list)` | underline saved vocabulary words / render interlinear glosses (`[{form, gloss}]`) |
| `paragraphs()` | return paragraph metadata for paragraph-level translation |
| `setTranslations(list, only)` / `setTranslationOnly(only)` | render bilingual or translation-only paragraphs (`[{last, text, lang?}]`) and switch between bilingual / translation-only modes |
| `clearSelection()` / `select(start, end)` | drop the text selection / select a range (a tapped highlight) |
| `markRect(id)` | where a highlight is drawn on this page (`{x, y, width, height}` or null) |
| `setBubbles(lines)` | 气泡模式: `lines` = core's `section_bubbles` (`[{first, last, start, end, who, avatar, color, right}]`; `start`/`end` in characters into sentence `first`). Each quote becomes `div.yy-bq` (avatar, a name line, the bubble); narration stays a paragraph (`.yy-bn`); short narration that only says who speaks (“宝钗因笑道：”) moves into the name line. Every sentence keeps its text and `data-s` (fragments split where a quote starts or ends), so ink, marks and taps are unchanged. A quote that runs out of its paragraph stays text. `null` puts the section back as it came. Re-lays out and keeps the first visible sentence |
| `relayout()` | trigger a manual re-layout keeping the current reading anchor |

## 5. Messages (reader → native)

Posted with `window.webkit.messageHandlers.yy.postMessage(obj)` on Apple,
`window.yyNative.postMessage(JSON.stringify(obj))` on Android, and
`window.chrome.webview.postMessage(obj)` on Windows (WebView2).

The machine-readable JSON Schema is generated from `reader/src/bridge.ts` and `reader/src/settings.ts`
into `reader/schema/bridge-messages.schema.json` (`cd reader && pnpm schema:check`), with canonical
payload fixtures in `fixtures/conformance/reader-messages.json`.

| `type` | Fields | When |
|---|---|---|
| `ready` | `section`, `pageCount`, `pageWidth`, `pageHeight`, `sentencePages`, `pageFirstSentence` | first layout done (after fonts) |
| `layout` | same as `ready` | any later re-layout (settings, resize, late images) |
| `tap` | `x`, `y`, `width`, `height`, `sentence` (index or null), `link` (`{target, note?}` or `{external}` or null), `mark` (highlight id or null), `word` (`WordHit` or null) | a tap that was not part of a scroll |
| `doubleTap` | `sentence` (index or null), `x`, `y` | double tap on a sentence (e.g. translate) |
| `context` | `sentence`, `offset`, `x`, `y` | right-click / long-press before context menu opens |
| `selection` | `start`, `end` (`{s, o}`), `text`, `rect` (the selected lines on this page, viewport coordinates); all null when the selection goes away | text selected, after the mouse or finger lets go |
| `activePage` | `sentence`, `page` | the ink line starts, or enters a new page partway through a long sentence; native turns the page if it is following playback |
| `bubble` | `line`, `choice` (string or null), `x`, `y` | bubble avatar/name clicked in 气泡模式 |
| `page` | `page` | snap paging: the visible page changed |
| `scroll` | `sentence`, `progress` (0–1 within the section) | scrolled mode: first visible sentence changed (throttled) |
| `log` | `level`, `message` | debugging; native forwards to its log |

- `sentencePages[s]`: page where sentence `s` starts.
- `pageFirstSentence[p]`: first sentence visible on page `p` (may have started on `p-1`); `-1` if
  the page has no text.

## 6. Page geometry (paginated mode)

- Page width `W` = `window.innerWidth` in whole CSS pixels; page `p` starts at `scrollX = p * W`.
- CSS columns on the root element: column gap `2·margin`, left/right padding `margin`, column width
  `W / cols − 2·margin`. Then every page is exactly `W` wide (with two columns the gutter between
  them is also `2·margin`) and the scroll width is `pageCount · W`. Engines differ on whether the
  trailing padding counts toward scroll width; the reader must make it exact (e.g. a spacer) so
  native paging never ends on a half page. Top/bottom padding are `--yy-top`/`--yy-bottom`.
- Nothing may straddle two pages horizontally; images are limited to one column.
- The document never scrolls vertically in paginated mode.

## 7. The ink line (墨线)

A 1.5px line in `--yy-p-accent` under the sentence being read, growing from the
start of the sentence to its end over the sentence's audio duration (`.s { padding-bottom: 2px }`).
Fragments of one sentence grow one after another, each for its share of the characters.
`setPlaying(false)` freezes it. With `data-yy-motion="reduced"` the full line is drawn at once
(墨水屏模式 uses `#000000`).

## 8. Highlights and search hits

A place in the text is `{s, o}`: sentence `s` and the UTF-16 offset into the sentence's text,
counted over its fragments' text nodes in document order, leaving out ruby readings (`rt`, `rp`)
and note markers (`.yy-noteref`). Core stores highlights in these terms and never looks inside.

Line-style highlights and search hits are drawn with the CSS Custom Highlight API
(`::highlight(yy-wave)`, `yy-underline`, `yy-dotted`, `yy-hit`, `yy-hit-now`), while background-fill
highlights (`yy-yellow`, `yy-blue`, `yy-pink`) are rendered as non-interactive overlays inside
`yy-fills` so they never shift layout. Colours follow the paper (`--yy-mk-*`).
