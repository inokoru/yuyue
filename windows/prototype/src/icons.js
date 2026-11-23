// ============================================================================
// Official Assets, ProtoIcon, PodcastGlyph, ServiceTile, YuyueMark & Book Data
// 100% Verbatim from apple/Yuyue/Assets.xcassets & Swift Vector Paths
// ============================================================================

// Helper: Render an official SVG from OFFICIAL_SVGS with exact size & color mode
function officialSvg(key, size = 16, mode = 'current') {
  let raw = (typeof OFFICIAL_SVGS !== 'undefined' && OFFICIAL_SVGS[key]) ? OFFICIAL_SVGS[key] : '';
  if (!raw) return '';
  // Ensure root <svg> has viewBox if missing
  if (!/viewBox=/i.test(raw)) {
    const wm = raw.match(/\bwidth="([^"]+)"/i);
    const hm = raw.match(/\bheight="([^"]+)"/i);
    if (wm && hm) {
      raw = raw.replace('<svg', `<svg viewBox="0 0 ${parseFloat(wm[1])} ${parseFloat(hm[1])}"`);
    }
  }
  // Size root <svg> cleanly
  raw = raw.replace(/\bwidth="[^"]*"/i, `width="${size}"`).replace(/\bheight="[^"]*"/i, `height="${size}"`);
  if (!/\bwidth="/i.test(raw)) {
    raw = raw.replace('<svg', `<svg width="${size}" height="${size}"`);
  }
  if (mode === 'current') {
    raw = raw.replace(/#000000|#000\b/gi, 'currentColor');
  } else if (mode === 'tile-mono') {
    // Exact SwiftUI .renderingMode(.template) + .foregroundStyle(YYRGBA(42, 43, 46)) (#2A2B2E)
    return `<span style="display:inline-flex;width:${size}px;height:${size}px;align-items:center;justify-content:center;filter:url(#yy-tile-mono-filter);">${raw}</span>`;
  }
  return `<span style="display:inline-flex;width:${size}px;height:${size}px;align-items:center;justify-content:center;">${raw}</span>`;
}

// Global SVG filter definition injected once inside body for exact #2A2B2E template rendering
function renderGlobalSvgDefs() {
  return `
    <svg width="0" height="0" style="position:absolute;pointer-events:none;" aria-hidden="true">
      <defs>
        <filter id="yy-tile-mono-filter" color-interpolation-filters="sRGB">
          <feColorMatrix type="matrix" values="0 0 0 0 0.1647  0 0 0 0 0.1686  0 0 0 0 0.1804  0 0 0 1 0"/>
        </filter>
        <linearGradient id="yy-apple-intel-grad" x1="0%" y1="0%" x2="100%" y2="100%">
          <stop offset="0%" stop-color="#FF9E33"/>
          <stop offset="34%" stop-color="#FA4D80"/>
          <stop offset="68%" stop-color="#9E5CFA"/>
          <stop offset="100%" stop-color="#3399FF"/>
        </linearGradient>
      </defs>
    </svg>
  `;
}

const ICONS = {
  // Official Assets.xcassets UI icons
  shelf: officialSvg('tab-shelf-line', 14, 'current'),
  shelfFill: officialSvg('tab-shelf', 14, 'current'),
  shelfSwitch: officialSvg('tab-shelf-switch', 21, 'current'),
  radio: officialSvg('radio', 14, 'current'),
  cast: officialSvg('cast', 15, 'current'),
  noteCard: officialSvg('note-card', 15, 'current'),
  bug: officialSvg('bug', 14, 'current'),
  menubar: officialSvg('menubar', 15, 'current'),

  // Exact ProtoIcon (MacShelfView.swift L1884-1937)
  // proto.wave: (2,8)->(2.5,8), (4.5,5.5)v5, (7,3)v10, (9.5,5)v6, (12,6.5)v3, (14,8)h.01
  wave: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M2 8h.5M4.5 5.5v5M7 3v10M9.5 5v6M12 6.5v3M14 8h.01"/></svg>`,
  // proto.book: book with top hanging ribbon & bottom page crease transformed onto 16x16
  protoBook: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><g transform="translate(8,8) scale(0.86486) translate(-11.5,-12)"><path d="M6.5 3.5H17Q18 3.5 18 4.5V19.5Q18 20.5 17 20.5H6.5Q5 20.5 5 19V5Q5 3.5 6.5 3.5Z"/><path d="M5 17.5H18"/><path d="M14 3.5V10.5L12.25 9.1L10.5 10.5V3.5"/></g></svg>`,

  // Open book (SF Symbol 'book' for Settings › 阅读)
  openBook: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M2 3.6C4.2 3 6.2 3.4 8 4.6C9.8 3.4 11.8 3 14 3.6V12.4C11.8 11.8 9.8 12.2 8 13.4C6.2 12.2 4.2 11.8 2 12.4V3.6Z"/><path d="M8 4.6V13.4"/></svg>`,
  // Closed book (SF Symbol 'book.closed')
  book: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M3.5 2.5h9a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1h-9a1 1 0 0 1-1-1v-9a1 1 0 0 1 1-1Z"/><path d="M5.6 2.5v11"/></svg>`,
  // SF Symbol 'character.book.closed' for Settings › 外文 (Closed book with Chinese '字' glyph)
  charBook: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"><path d="M3.4 2.2h9.2a1 1 0 0 1 1 1v9.6a1 1 0 0 1-1 1H3.4a1 1 0 0 1-1-1V3.2a1 1 0 0 1 1-1Z"/><path d="M5.2 2.2v11.6"/><path d="M9.3 4.3v.9M7.2 5.3h4.2v1.1M7.9 7.1h2.6L9.3 8.4v2.1a.6.6 0 0 1-.6.6h-.5M7.1 9.1h4.4"/></svg>`,

  highlighter: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><path d="m9.5 3 3.5 3.5-5.8 5.8H3.7V8.8L9.5 3Z"/><path d="M2.5 14h6"/></svg>`,
  translate: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.35" stroke-linecap="round" stroke-linejoin="round"><path d="M2 4h7M5.5 2.2V4M3.5 4c.3 2.6 1.8 4.6 4.2 5.6M7.2 4c-.5 2.4-2 4.4-4.5 5.6"/><path d="m8.8 13.5 2.4-6 2.4 6M9.5 11.7h3.4"/></svg>`,
  stack: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><rect x="2.5" y="4.8" width="11" height="8.2" rx="2"/><path d="M4.2 2.6h7.6"/></svg>`,
  search: `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.55" stroke-linecap="round"><circle cx="7" cy="7" r="4.5"/><path d="m10.5 10.5 3.2 3.2"/></svg>`,
  plus: `<svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M8 3v10M3 8h10"/></svg>`,
  gear: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><circle cx="8" cy="8" r="2.2"/><path d="M8 1.8v1.6M8 12.6v1.6M1.8 8h1.6M12.6 8h1.6M3.6 3.6l1.1 1.1M11.3 11.3l1.1 1.1M12.4 3.6l-1.1 1.1M4.7 11.3l-1.1 1.1"/></svg>`,
  feedback: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M3 3h10a1.5 1.5 0 0 1 1.5 1.5v6A1.5 1.5 0 0 1 13 12H6l-3 2.5V12a1.5 1.5 0 0 1-1.5-1.5v-6A1.5 1.5 0 0 1 3 3Z"/><path d="M8 5.6v2.6M8 10.2h.01"/></svg>`,
  sidebar: `<svg width="15" height="15" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.35" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="2.5" width="12" height="11" rx="2"/><path d="M6 2.5v11"/></svg>`,
  headphones: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><path d="M3 9V7.5a5 5 0 0 1 10 0V9"/><rect x="2" y="8.5" width="2.6" height="4.5" rx="1.2"/><rect x="11.4" y="8.5" width="2.6" height="4.5" rx="1.2"/></svg>`,
  headphonesTiny: `<svg width="10" height="10" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M3 9.2V7.5a5 5 0 0 1 10 0v1.7"/><rect x="2" y="8.6" width="2.7" height="4.4" rx="1.2"/><rect x="11.3" y="8.6" width="2.7" height="4.4" rx="1.2"/></svg>`,
  eyeTiny: `<svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M1.6 8s2.4-4.2 6.4-4.2S14.4 8 14.4 8s-2.4 4.2-6.4 4.2S1.6 8 1.6 8Z"/><circle cx="8" cy="8" r="1.8"/></svg>`,
  dots: `<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor"><circle cx="3.5" cy="8" r="1.2"/><circle cx="8" cy="8" r="1.2"/><circle cx="12.5" cy="8" r="1.2"/></svg>`,
  chevDown: `<svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="m2.5 4.5 3.5 3.5 3.5-3.5"/></svg>`,
  chevRight: `<svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="m4.5 2.5 3.5 3.5-3.5 3.5"/></svg>`,
  chevLeft: `<svg width="11" height="11" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M7.5 2.5 4 6l3.5 3.5"/></svg>`,
  chevUpDown: `<svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="m3.5 4.2 2.5-2.4 2.5 2.4M3.5 7.8l2.5 2.4 2.5-2.4"/></svg>`,
  play: `<svg width="12" height="12" viewBox="0 0 14 14" fill="currentColor"><path d="M3.5 2.3a.7.7 0 0 1 1.06-.6l7.2 4.7a.7.7 0 0 1 0 1.2l-7.2 4.7a.7.7 0 0 1-1.06-.6V2.3Z"/></svg>`,
  stop: `<svg width="10" height="10" viewBox="0 0 12 12" fill="currentColor"><rect x="2.2" y="2.2" width="7.6" height="7.6" rx="1.4"/></svg>`,
  pause: `<svg width="12" height="12" viewBox="0 0 14 14" fill="currentColor"><rect x="3.2" y="2.4" width="2.6" height="9.2" rx="0.9"/><rect x="8.2" y="2.4" width="2.6" height="9.2" rx="0.9"/></svg>`,
  list: `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"><path d="M5.5 4h8M5.5 8h8M5.5 12h8M2.5 4h.01M2.5 8h.01M2.5 12h.01"/></svg>`,
  grid: `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linejoin="round"><rect x="2.2" y="2.2" width="4.6" height="4.6" rx="1"/><rect x="9.2" y="2.2" width="4.6" height="4.6" rx="1"/><rect x="2.2" y="9.2" width="4.6" height="4.6" rx="1"/><rect x="9.2" y="9.2" width="4.6" height="4.6" rx="1"/></svg>`,
  share: `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><path d="M8 10.2V2.4M5.2 5 8 2.2 10.8 5"/><path d="M3.2 8.8v3.6A1.4 1.4 0 0 0 4.6 13.8h6.8a1.4 1.4 0 0 0 1.4-1.4V8.8"/></svg>`,
  bookmark: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><path d="M4.6 3h6.8q.8 0 .8.8v9.4L8 10.4l-4.2 2.8V3.8Q3.8 3 4.6 3Z"/></svg>`,
  bookmarkFill: `<svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"><path d="M4.6 3h6.8q.8 0 .8.8v9.4L8 10.4l-4.2 2.8V3.8Q3.8 3 4.6 3Z"/></svg>`,
  sparkle: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"><path d="M8 1.8 9.6 6.4 14.2 8 9.6 9.6 8 14.2 6.4 9.6 1.8 8l4.6-1.6L8 1.8Z"/></svg>`,
  docZoom: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"><path d="M9.5 2H4a1.5 1.5 0 0 0-1.5 1.5v9A1.5 1.5 0 0 0 4 14h8a1.5 1.5 0 0 0 1.5-1.5V6L9.5 2Z"/><circle cx="7.5" cy="8.8" r="2"/><path d="m9 10.3 1.5 1.5"/></svg>`,
  check: `<svg width="12" height="12" viewBox="0 0 14 14" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M2.5 7.2 5.6 10.3 11.5 3.8"/></svg>`,
  xmark: `<svg width="11" height="11" viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><path d="M2.5 2.5 9.5 9.5M9.5 2.5l-7 7"/></svg>`,
  pin: `<svg width="11" height="11" viewBox="0 0 16 16" fill="currentColor"><path d="M9.8 2.2a1 1 0 0 1 1.4 0l2.6 2.6a1 1 0 0 1-.3 1.6l-2.3.8-.9 2.7a1 1 0 0 1-1.6.4L6.5 8.1l-3.6 3.6-.9-.9 3.6-3.6-2.2-2.2a1 1 0 0 1 .4-1.6l2.7-.9.8-2.3a1 1 0 0 1 .5-.6Z"/></svg>`,
  refresh: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><path d="M11.323 4.677A4.7 4.7 0 1 1 7.184 3.371"/><path d="M11.2 3.2 11.6 5.6 9.2 5.4"/></svg>`,
  mic: `<svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"><rect x="5.5" y="2" width="5" height="7.5" rx="2.5"/><path d="M3.5 7.8a4.5 4.5 0 0 0 9 0M8 12.5v2"/></svg>`,
  speaker: `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><path d="M2.5 6.2h2.6L8.5 3.5v9l-3.4-2.7H2.5v-3.6Z"/><path d="M10.8 5.6a3.5 3.5 0 0 1 0 4.8M12.6 3.8a6 6 0 0 1 0 8.4"/></svg>`,
  history: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M2.6 8a5.4 5.4 0 1 0 1.4-3.6"/><path d="M2.4 2.5v2.4h2.4M8 5v3.2l2.2 1.3"/></svg>`,
  undo: `<svg width="11" height="11" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M3.5 6.5h6a3.5 3.5 0 1 1 0 7H6"/><path d="M6 4 3.2 6.5 6 9"/></svg>`,
  sliders: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"><path d="M2.5 4.5h11M2.5 8h11M2.5 11.5h11"/><circle cx="5.5" cy="4.5" r="1.4" fill="var(--yy-settings-side)"/><circle cx="10.5" cy="8" r="1.4" fill="var(--yy-settings-side)"/><circle cx="6.8" cy="11.5" r="1.4" fill="var(--yy-settings-side)"/></svg>`,
  sun: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"><circle cx="8" cy="8" r="2.8"/><path d="M8 1.8v1.5M8 12.7v1.5M1.8 8h1.5M12.7 8h1.5M3.6 3.6l1.1 1.1M11.3 11.3l1.1 1.1M12.4 3.6l-1.1 1.1M4.7 11.3l-1.1 1.1"/></svg>`,
  bubble: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M3 3h10a1.5 1.5 0 0 1 1.5 1.5v6A1.5 1.5 0 0 1 13 12H6l-3 2.4V12a1.5 1.5 0 0 1-1.5-1.5v-6A1.5 1.5 0 0 1 3 3Z"/><path d="M5.2 6.5h5.6M5.2 9h3.6"/></svg>`,
  sync: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7a5 5 0 0 1 8.6-3.4M13 9a5 5 0 0 1-8.6 3.4"/><path d="M12 1.8v2.4H9.6M4 14.2v-2.4h2.4"/></svg>`,
  drive: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"><rect x="2" y="3.5" width="12" height="9" rx="2"/><path d="M2 9.5h12"/><circle cx="11.5" cy="11.2" r="0.8" fill="currentColor"/></svg>`,
  box: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"><path d="M8 2 2.5 4.8v6.4L8 14l5.5-2.8V4.8L8 2Z"/><path d="M2.5 4.8 8 7.6l5.5-2.8M8 7.6V14"/></svg>`,
  info: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"><circle cx="8" cy="8" r="5.6"/><path d="M8 7.4v3.4M8 5.1h.01"/></svg>`,
  folder: `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"><path d="M2 4a1.5 1.5 0 0 1 1.5-1.5h3l1.4 1.6h4.6A1.5 1.5 0 0 1 14 5.6v6.4a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 2 12V4Z"/></svg>`,
  trash: `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"><path d="M3 4.5h10M6 4.5V3a1 1 0 0 1 1-1h2a1 1 0 0 1 1 1v1.5M4.5 4.5l.6 8.5a1 1 0 0 0 1 .9h3.8a1 1 0 0 0 1-.9l.6-8.5"/></svg>`,
  pencil: `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="m10.8 2.5 2.7 2.7-7.8 7.8H3v-2.7l7.8-7.8Z"/></svg>`,
  link: `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"><path d="M6.6 9.4 9.4 6.6M5.3 7.6 3.8 9.1C2.5 10.4 5.7 13.6 7 12.3L8.5 10.8M10.7 8.4 12.2 6.9C13.5 5.6 10.3 2.4 9 3.7L7.5 5.2"/></svg>`,
  cloud: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M4.6 12.5h7.2a2.6 2.6 0 0 0 .4-5.17A3.8 3.8 0 0 0 4.8 6.2a2.9 2.9 0 0 0-.2 5.8Z"/></svg>`,
  globe: `<svg width="14" height="14" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.35"><circle cx="8" cy="8" r="5.6"/><ellipse cx="8" cy="8" rx="2.5" ry="5.6"/><path d="M2.6 8h10.8"/></svg>`,
  lock: `<svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"><rect x="3.5" y="7.2" width="9" height="6.3" rx="1.6"/><path d="M5.5 7.2V5.2a2.5 2.5 0 0 1 5 0v2"/></svg>`,
  paperclip: `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"><path d="m13.2 7.4-5.5 5.5a3.2 3.2 0 0 1-4.5-4.5l5.8-5.8a2.1 2.1 0 0 1 3 3L6.3 11.3a1 1 0 0 1-1.4-1.4l5.1-5.1"/></svg>`,
  camera: `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M2.5 5.5A1.5 1.5 0 0 1 4 4h1.4l1-1.3h3.2l1 1.3H12a1.5 1.5 0 0 1 1.5 1.5v6A1.5 1.5 0 0 1 12 13H4a1.5 1.5 0 0 1-1.5-1.5v-6Z"/><circle cx="8" cy="8.5" r="2.1"/></svg>`,
  arrowDown: `<svg width="13" height="13" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M8 2.5v8.2M4.8 7.8 8 11l3.2-3.2M3 13.5h10"/></svg>`,
  arrowUp: `<svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M8 13V3.5M4.2 7.2 8 3.4l3.8 3.8"/></svg>`,
  moon: `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"><path d="M12.8 10.2A5.5 5.5 0 0 1 5.8 3.2a5.5 5.5 0 1 0 7 7Z"/></svg>`,
  back15: `<svg width="20" height="20" viewBox="0 0 22 22" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M4.5 9A7 7 0 1 0 7 5.2H4.2"/><path d="M6.2 3 4 5.2l2.2 2.2"/><text x="11" y="14.2" text-anchor="middle" font-size="6.8" font-weight="700" fill="currentColor" stroke="none">15</text></svg>`,
  fwd30: `<svg width="20" height="20" viewBox="0 0 22 22" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M17.5 9A7 7 0 1 1 15 5.2h2.8"/><path d="M15.8 3 18 5.2l-2.2 2.2"/><text x="11" y="14.2" text-anchor="middle" font-size="6.8" font-weight="700" fill="currentColor" stroke="none">30</text></svg>`,
  winMin: `<svg width="10" height="10" viewBox="0 0 10 10" stroke="currentColor" stroke-width="1"><path d="M1 5h8"/></svg>`,
  winMax: `<svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" stroke-width="1"><rect x="1.5" y="1.5" width="7" height="7" rx="0.8"/></svg>`,
  winClose: `<svg width="10" height="10" viewBox="0 0 10 10" stroke="currentColor" stroke-width="1.1"><path d="m1.5 1.5 7 7M8.5 1.5l-7 7"/></svg>`
};

// All 14 Exact PodcastGlyph Vector Paths (PodcastSharedUI.swift L110-251)
const POD_GLYPHS = {
  allGrid: (s = 15) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><rect x="2.5" y="2.5" width="4.4" height="4.4" rx="1"/><rect x="9.1" y="2.5" width="4.4" height="4.4" rx="1"/><rect x="2.5" y="9.1" width="4.4" height="4.4" rx="1"/><rect x="9.1" y="9.1" width="4.4" height="4.4" rx="1"/></svg>`,
  openBook: (s = 15) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><path d="M2.4 4.2Q5.4 3.4 8 4.8Q10.6 3.4 13.6 4.2V12.1Q10.6 11.3 8 12.7Q5.4 11.3 2.4 12.1ZM8 4.8V12.7"/></svg>`,
  waves: (s = 15) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"><path d="M2.5 6.2Q5.2 4.4 8 6.2Q10.8 8 13.5 6.2M2.5 10.2Q5.2 8.4 8 10.2Q10.8 12 13.5 10.2"/></svg>`,
  weekly: (s = 15) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"><path d="M3 4.5H13M3 8H10M3 11.5H8"/><circle cx="12" cy="10.5" r="2"/></svg>`,
  roundtable: (s = 15) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45"><circle cx="5.8" cy="8" r="3.1"/><circle cx="10.2" cy="8" r="3.1"/></svg>`,
  vocab: (s = 15) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"><path d="M2.6 4.6H7.8M5.2 3V4.6M7 4.6Q6.2 8.6 3.2 11.4M4.2 6.4Q5.6 9.2 7.8 11"/><path d="M9.5 12.2L11.6 5.4L13.7 12.2M10.3 10H12.9"/></svg>`,
  link: (s = 15) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"><path d="M6.6 9.4L9.4 6.6M5.3 7.6L3.8 9.1C2.5 10.4 5.7 13.6 7 12.3L8.5 10.8M10.7 8.4L12.2 6.9C13.5 5.6 10.3 2.4 9 3.7L7.5 5.2"/></svg>`,
  bookmark: (s = 15) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><path d="M4.6 3H11.4Q12.2 3 12.2 3.8V13.2L8 10.4L3.8 13.2V3.8Q3.8 3 4.6 3Z"/></svg>`,
  bookmarkFilled: (s = 15) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="currentColor" stroke="currentColor" stroke-width="1.45" stroke-linejoin="round"><path d="M4.6 3H11.4Q12.2 3 12.2 3.8V13.2L8 10.4L3.8 13.2V3.8Q3.8 3 4.6 3Z"/></svg>`,
  waveform: (s = 15) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"><path d="M2.8 6.6V9.4M5.4 4V12M8 5.4V10.6M10.6 3.2V12.8M13.2 6.2V9.8"/></svg>`,
  transcriptLines: (s = 15) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"><path d="M3 4.8H13M3 8H10.6M3 11.2H8.4"/></svg>`,
  plus: (s = 15) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round"><path d="M8 3.2V12.8M3.2 8H12.8"/></svg>`,
  refresh: (s = 15) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.45" stroke-linecap="round" stroke-linejoin="round"><path d="M11.323 4.677A4.7 4.7 0 1 1 7.184 3.371"/><path d="M11.2 3.2L11.6 5.6L9.2 5.4"/></svg>`,
  xmark: (s = 13.5) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"><path d="M4.5 4.5L11.5 11.5M11.5 4.5L4.5 11.5"/></svg>`,
  chevronDown: (s = 12) => `<svg width="${s}" height="${s}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M4.2 6.2L8 10L11.8 6.2"/></svg>`
};

// PodcastShowMark (PodcastSharedUI.swift L255-315: Studio openBook vs 5 DJB2 literary tones)
function renderPodcastShowMark(title, isStudio = false, size = 22) {
  const r = size >= 30 ? 8 : (size <= 20 ? 5 : 6);
  if (isStudio) {
    return `<span style="width:${size}px;height:${size}px;border-radius:${r}px;background:var(--tint-wash);border:0.5px solid var(--tint-ring);color:var(--tint-accent);display:inline-flex;align-items:center;justify-content:center;flex-shrink:0;">${POD_GLYPHS.openBook(Math.round(size * 0.58))}</span>`;
  }
  const tones = [
    { bg: '#26292E', fg: '#F5F2EB' }, // Ink slate
    { bg: '#5E3035', fg: '#F8F0EC' }, // Literary burgundy
    { bg: '#26403E', fg: '#ECF4F1' }, // Deep spruce
    { bg: '#2F475B', fg: '#EEF3F8' }, // Slate blue
    { bg: '#524130', fg: '#F6F0E7' }  // Warm umber
  ];
  let hash = 5381;
  const str = (title || 'P').trim();
  for (let i = 0; i < str.length; i++) {
    hash = (((hash << 5) + hash) + str.charCodeAt(i)) >>> 0;
  }
  const t = tones[hash % 5];
  const ch = (str[0] || 'P').toUpperCase();
  return `<span class="serif" style="width:${size}px;height:${size}px;border-radius:${r}px;background:${t.bg};color:${t.fg};border:0.5px solid rgba(0,0,0,0.14);font-size:${(size * 0.52).toFixed(1)}px;display:inline-flex;align-items:center;justify-content:center;flex-shrink:0;line-height:1;">${ch}</span>`;
}

// ServiceTile & ExportTile (MacCompanionSettings.swift:599 & MacNoteExportSettings.swift:108)
function renderServiceTile(kind, size = 30) {
  const r = (size * 0.25).toFixed(1);
  const iconSz = Math.round(size * 0.6);
  if (kind === 'add') {
    return `<span class="service-tile add-tile" style="width:${size}px;height:${size}px;border-radius:${r}px;">${ICONS.plus}</span>`;
  }
  if (kind === 'thisMac') {
    const spSz = Math.round(size * 0.54);
    return `<span class="service-tile" style="width:${size}px;height:${size}px;border-radius:${r}px;">
      <svg width="${spSz}" height="${spSz}" viewBox="0 0 16 16" fill="url(#yy-apple-intel-grad)"><path d="M8 1.2 9.7 6.3 14.8 8 9.7 9.7 8 14.8 6.3 9.7 1.2 8 6.3 6.3 8 1.2Z"/><circle cx="12.6" cy="3.4" r="1.1"/></svg>
    </span>`;
  }
  if (kind === 'laptop') {
    const lw = Math.round(size * 0.52);
    return `<span class="service-tile" style="width:${size}px;height:${size}px;border-radius:${r}px;">
      <svg width="${lw}" height="${lw}" viewBox="0 0 16 16" fill="none" stroke="#2A2B2E" stroke-width="1.35" stroke-linecap="round" stroke-linejoin="round"><rect x="2.5" y="3.2" width="11" height="7.5" rx="1.2"/><path d="M1 12.2h14"/></svg>
    </span>`;
  }
  if (kind === 'phone') {
    const pw = Math.round(size * 0.5);
    return `<span class="service-tile" style="width:${size}px;height:${size}px;border-radius:${r}px;">
      <svg width="${pw}" height="${pw}" viewBox="0 0 16 16" fill="none" stroke="#2A2B2E" stroke-width="1.35"><rect x="4.2" y="1.8" width="7.6" height="12.4" rx="1.8"/><path d="M7 3.4h2"/></svg>
    </span>`;
  }
  if (kind === 'sparkle') {
    return `<span class="service-tile" style="width:${size}px;height:${size}px;border-radius:${r}px;color:#2A2B2E;">${ICONS.sparkle}</span>`;
  }
  if (kind === 'elevenlabs') {
    return `<span class="service-tile" style="width:${size}px;height:${size}px;border-radius:${r}px;font-size:${(size * 0.42).toFixed(1)}px;font-weight:600;color:#6E6B68;">E</span>`;
  }
  if (kind === 'azure') {
    return `<span class="service-tile" style="width:${size}px;height:${size}px;border-radius:${r}px;">${officialSvg('brand-azure', iconSz, 'tile-mono')}</span>`;
  }
  const svgKey = kind.startsWith('provider-') ? kind : `provider-${kind}`;
  if (typeof OFFICIAL_SVGS !== 'undefined' && OFFICIAL_SVGS[svgKey]) {
    return `<span class="service-tile" style="width:${size}px;height:${size}px;border-radius:${r}px;">${officialSvg(svgKey, iconSz, 'tile-mono')}</span>`;
  }
  return `<span class="service-tile" style="width:${size}px;height:${size}px;border-radius:${r}px;font-size:${(size * 0.42).toFixed(1)}px;font-weight:600;color:#6E6B68;">${(kind[0] || 'M').toUpperCase()}</span>`;
}

function renderExportTile(target, size = 30) {
  const r = (size * 0.25).toFixed(1);
  const iconSz = Math.round(size * 0.6);
  if (target === 'flomo' && typeof OFFICIAL_IMAGES !== 'undefined' && OFFICIAL_IMAGES['brand-flomo']) {
    return `<span class="service-tile full-bleed" style="width:${size}px;height:${size}px;border-radius:${r}px;"><img src="${OFFICIAL_IMAGES['brand-flomo']}" style="width:100%;height:100%;object-fit:cover;display:block;" alt="flomo"/></span>`;
  }
  if (target === 'xinzhi' && typeof OFFICIAL_SVGS !== 'undefined' && OFFICIAL_SVGS['brand-xinzhi']) {
    return `<span class="service-tile full-bleed" style="width:${size}px;height:${size}px;border-radius:${r}px;">${officialSvg('brand-xinzhi', size, 'original')}</span>`;
  }
  if (target === 'obsidian') {
    return `<span class="service-tile" style="width:${size}px;height:${size}px;border-radius:${r}px;">${officialSvg('brand-obsidian', iconSz, 'tile-mono')}</span>`;
  }
  if (target === 'notion') {
    return `<span class="service-tile" style="width:${size}px;height:${size}px;border-radius:${r}px;">${officialSvg('brand-notion', iconSz, 'tile-mono')}</span>`;
  }
  return renderServiceTile(target, size);
}

// YuyueMark (UpdateUI.swift L13-35: warm ivory seal + Songti "阅" + #2B4C7E navy rule)
function renderYuyueMark(px = 54) {
  const r = (px * 0.23).toFixed(1);
  const fs = (px * 0.46).toFixed(1);
  const ruleW = (px * 0.375).toFixed(1);
  const ruleH = Math.max(2, px * 0.04).toFixed(1);
  return `
    <div style="width:${px}px;height:${px}px;border-radius:${r}px;background:#FBFAF7;border:0.5px solid rgba(0,0,0,0.10);box-shadow:0 ${(px * 0.08).toFixed(1)}px ${(px * 0.12).toFixed(1)}px rgba(30,28,24,0.14);position:relative;display:inline-flex;align-items:center;justify-content:center;flex-shrink:0;">
      <span class="serif" style="font-size:${fs}px;font-weight:700;color:#1C1D20;transform:translateY(-${(px * 0.05).toFixed(1)}px);line-height:1;">阅</span>
      <span style="position:absolute;width:${ruleW}px;height:${ruleH}px;border-radius:99px;background:#2B4C7E;transform:translateY(${(px * 0.27).toFixed(1)}px);"></span>
    </div>
  `;
}

// PencilSignature (PhoneSignature.swift L1-115: 3-stroke "CTT" graphite signature)
function renderPencilSignature(size = 58, animating = false) {
  const h = (size * 56 / 60).toFixed(1);
  return `
    <svg width="${size}" height="${h}" viewBox="0 0 60 56" fill="none" style="display:block;overflow:visible;">
      <g stroke="var(--yy-sig-lead)" stroke-linecap="round" stroke-linejoin="round">
        <path class="${animating ? 'yy-sig-stroke-0' : ''}" d="M 16 3 C 11 9, 5.5 19, 3.5 27 C 2 33, 5 35.5, 12 35.5 C 20 35.5, 28 34.5, 36 33.5 C 43 32.5, 50 31.5, 56 31" stroke-width="1.9" stroke-opacity="0.9"/>
        <path class="${animating ? 'yy-sig-stroke-0' : ''}" d="M 32 34.2 C 40 33, 48 31.8, 55.5 31.2" stroke-width="2.5" stroke-opacity="0.48"/>
        <path class="${animating ? 'yy-sig-stroke-1' : ''}" d="M 13.5 11 C 19 12.5, 23.5 12.8, 27 12.2 C 23 15, 19.5 20, 18.3 27 C 17.3 34, 16.6 41, 17.2 47" stroke-width="1.85" stroke-opacity="0.9"/>
        <path class="${animating ? 'yy-sig-stroke-2' : ''}" d="M 35 11.5 C 40 12.8, 44.5 11.8, 48.5 9.5 C 43 13, 37.5 18, 35.3 25 C 33.5 33, 32.3 43, 33 53.5" stroke-width="1.85" stroke-opacity="0.9"/>
      </g>
    </svg>
  `;
}

// Seamless Windows 11 Titlebar Strip (Matches underlying column surfaces)
function renderWinTitleBar(kind = 'shelf', hasInspector = false) {
  const btns = `
    <div class="win-caption-btns" title="Windows 原生标题栏区 (与下方内容列无错位衔接)">
      <button class="win-cap-btn" title="最小化">${ICONS.winMin}</button>
      <button class="win-cap-btn" title="最大化">${ICONS.winMax}</button>
      <button class="win-cap-btn close" title="关闭">${ICONS.winClose}</button>
    </div>
  `;
  if (kind === 'settings') {
    return `
      <div class="win-titlebar settings">
        <div class="win-tb-col-side"></div>
        <div class="win-tb-col-mid" style="display:flex;justify-content:flex-end;">${!hasInspector ? btns : ''}</div>
        ${hasInspector ? `<div class="win-tb-col-insp">${btns}</div>` : ''}
      </div>
    `;
  }
  return `<div class="win-titlebar ${kind}">${btns}</div>`;
}

// Authentic Book Library Data (Real cover image + clean publisher jacket designs — NO fake stamps!)
const BOOKS = [
  {
    id: 'hlm', title: '红楼梦', author: '曹雪芹', format: 'epub', status: 'reading',
    progress: 0.34, pct: '34%', remaining: '还剩 18 小时 20 分', chapter: '第八回 · 比通灵金莺微露意',
    chapterIdx: 8, totalChapters: 120, rating: 5, hasAudio: true, leadHue: '#5C4438',
    coverKind: 'img-hlm', collections: ['classic'], openedAgo: '2 小时前',
    comment: '草蛇灰线，伏脉千里。每一回重读都有新细节。', tags: ['古典文学', '精读'], bytes: '48.2 MB'
  },
  {
    id: 'gatsby', title: 'The Great Gatsby', author: 'F. Scott Fitzgerald', format: 'epub', status: 'reading',
    progress: 0.42, pct: '42%', remaining: '还剩 2 小时 15 分', chapter: 'Chapter IV',
    chapterIdx: 4, totalChapters: 9, rating: 5, hasAudio: false, leadHue: '#1D4259',
    coverKind: 'jacket-gatsby', collections: ['classic', 'en'], openedAgo: '昨天',
    comment: 'So we beat on, boats against the current.', tags: ['英文原版', '美国文学'], bytes: '3.6 MB'
  },
  {
    id: 'beiying-audio', title: '背影 · 名家朗诵集', author: '朱自清', format: 'audio', status: 'reading',
    progress: 0.65, pct: '65%', remaining: '还剩 14 分钟', chapter: '第 1 集 · 背影',
    chapterIdx: 1, totalChapters: 6, rating: 5, hasAudio: true, leadHue: '#4A5548',
    coverKind: 'audio', collections: ['classic'], openedAgo: '3 天前',
    comment: '', tags: ['有声书'], bytes: '186.4 MB'
  },
  {
    id: 'pride', title: 'Pride and Prejudice', author: 'Jane Austen', format: 'epub', status: 'want',
    progress: 0, pct: '想读', remaining: '约 7 小时', chapter: 'Chapter I',
    chapterIdx: 1, totalChapters: 61, rating: 0, hasAudio: false,
    coverKind: 'jacket-pride', collections: ['classic', 'en'], openedAgo: '5 天前', comment: '', tags: ['英文原版'], bytes: '2.4 MB'
  },
  {
    id: 'guxiang', title: '呐喊', author: '鲁迅', format: 'txt', status: 'finished',
    progress: 1.0, pct: '读完', remaining: '读完', chapter: '故乡',
    chapterIdx: 14, totalChapters: 14, rating: 5, hasAudio: true,
    coverKind: 'stone', collections: ['classic'], openedAgo: '11月3日',
    comment: '其实地上本没有路，走的人多了，也便成了路。', tags: ['现代文学'], bytes: '1.1 MB'
  },
  {
    id: 'arc-pdf', title: 'On the Measure of Intelligence', author: 'François Chollet', format: 'pdf', status: 'reading',
    progress: 0.28, pct: '第 18 页', remaining: '第 18 / 64 页', chapter: '3.2 Fluid Intelligence',
    chapterIdx: 3, totalChapters: 8, rating: 4, hasAudio: false,
    coverKind: 'stone-pdf', collections: ['ai', 'en'], openedAgo: '今天', comment: '', tags: ['AI 论文'], bytes: '14.8 MB'
  },
  {
    id: 'santi', title: '三体', author: '刘慈欣', format: 'epub', status: 'finished',
    progress: 1.0, pct: '读完', remaining: '读完', chapter: '第三十五章',
    chapterIdx: 35, totalChapters: 35, rating: 5, hasAudio: false,
    coverKind: 'jacket-santi', collections: ['scifi'], openedAgo: '10月19日',
    comment: '给岁月以文明，而不是给文明以岁月。', tags: ['科幻'], bytes: '6.9 MB'
  },
  {
    id: 'tanizaki', title: '阴翳礼赞', author: '谷崎润一郎', format: 'epub', status: 'paused',
    progress: 0.48, pct: '48%', remaining: '停在 48%', chapter: '纸与光影',
    chapterIdx: 3, totalChapters: 6, rating: 4, hasAudio: false,
    coverKind: 'jacket-tanizaki', collections: ['aesthetic'], openedAgo: '2 周前', comment: '', tags: ['美学随笔'], bytes: '4.2 MB'
  },
  {
    id: 'walden', title: '瓦尔登湖', author: '亨利·戴维·梭罗', format: 'epub', status: 'want',
    progress: 0, pct: '想读', remaining: '约 8 小时', chapter: '简朴生活',
    chapterIdx: 1, totalChapters: 18, rating: 0, hasAudio: false,
    coverKind: 'jacket-walden', collections: ['aesthetic'], openedAgo: '未读', comment: '', tags: [], bytes: '5.5 MB'
  }
];

function renderBookCover(b, widthPx = null, extraClass = '') {
  const style = widthPx ? `width:${widthPx}px;` : 'width:100%;';
  const small = widthPx && widthPx <= 52;
  const audioBadge = (b.hasAudio && !small) ? `<div class="audio-badge" title="有声书">${ICONS.headphonesTiny}</div>` : '';

  // 1. Standalone Audiobook (Square 1:1 inset cover with headphones icon — BookCover.swift)
  if (b.coverKind === 'audio') {
    return `
      <div class="book-cover ${extraClass}" style="${style}aspect-ratio:1/1;border-radius:6px;background:var(--yy-inset);display:flex;align-items:center;justify-content:center;color:var(--yy-t3);">
        ${ICONS.headphones}
      </div>`;
  }

  // 2. Real fixture cover PNG from reader/test/fixtures/hlm
  if (b.coverKind === 'img-hlm' && typeof OFFICIAL_IMAGES !== 'undefined' && OFFICIAL_IMAGES['cover-hlm']) {
    return `
      <div class="book-cover ${extraClass}" style="${style}">
        <img src="${OFFICIAL_IMAGES['cover-hlm']}" alt="${b.title}" style="width:100%;height:100%;object-fit:cover;display:block;"/>
        ${audioBadge}
      </div>`;
  }

  // 3. CoverFace fallback (BookCover.swift: warm stone #EBE6DD, serif title + author, bottom-left PDF label only for PDFs)
  if (b.coverKind === 'stone' || b.coverKind === 'stone-pdf') {
    return `
      <div class="book-cover ${extraClass}" style="${style}">
        <div class="cover-face">
          <div>
            <div class="serif" style="font-size:${small ? '8px' : '12.5px'};font-weight:600;color:#2A2B2E;line-height:1.35;">${b.title}</div>
            ${!small ? `<div class="serif" style="font-size:9.5px;color:#6E6B68;margin-top:4px;">${b.author}</div>` : ''}
          </div>
          ${b.coverKind === 'stone-pdf' && !small ? `<div style="font-size:8.5px;font-weight:600;letter-spacing:0.5px;color:#6E6B68;">PDF</div>` : ''}
        </div>
        ${audioBadge}
      </div>`;
  }

  // 4. Clean Publisher Edition Jackets (Zero fake "[语阅藏书]" stamps — realistic typographic & geometric book jackets)
  const jackets = {
    'jacket-gatsby': {
      bg: '#13293D', ink: '#F2DDA4', sub: '#94B3CA',
      art: `<svg viewBox="0 0 100 150" style="position:absolute;inset:0;width:100%;height:100%;opacity:0.42;"><circle cx="50" cy="108" r="28" fill="none" stroke="#E4B84A" stroke-width="0.8"/><circle cx="50" cy="108" r="19" fill="none" stroke="#E4B84A" stroke-width="0.6"/><path d="M50 68v80M18 108h64M28 86l44 44M72 86l-44 44" stroke="#E4B84A" stroke-width="0.5"/></svg>`
    },
    'jacket-pride': {
      bg: '#2F4037', ink: '#F2E9D8', sub: '#B9C4B8',
      art: `<svg viewBox="0 0 100 150" style="position:absolute;inset:0;width:100%;height:100%;opacity:0.35;"><rect x="9" y="9" width="82" height="132" rx="2" fill="none" stroke="#D9C39E" stroke-width="0.8"/><path d="M32 108c10-16 26-16 36 0" fill="none" stroke="#D9C39E" stroke-width="1"/></svg>`
    },
    'jacket-santi': {
      bg: '#181C22', ink: '#EFF1F5', sub: '#8F98A6',
      art: `<svg viewBox="0 0 100 150" style="position:absolute;inset:0;width:100%;height:100%;opacity:0.65;"><circle cx="50" cy="95" r="16" fill="#C94A38" opacity="0.75"/><circle cx="34" cy="114" r="6" fill="#E3A857" opacity="0.8"/><circle cx="68" cy="110" r="3.5" fill="#9DB4CE"/></svg>`
    },
    'jacket-tanizaki': {
      bg: '#DFD9CE', ink: '#262421', sub: '#6E685F',
      art: `<svg viewBox="0 0 100 150" style="position:absolute;inset:0;width:100%;height:100%;opacity:0.35;"><rect x="0" y="96" width="100" height="54" fill="#2A2723" opacity="0.18"/></svg>`
    },
    'jacket-walden': {
      bg: '#3B5042', ink: '#EFF3EE', sub: '#B8C8BC',
      art: `<svg viewBox="0 0 100 150" style="position:absolute;inset:0;width:100%;height:100%;opacity:0.38;"><path d="M0 112 Q30 104 55 112 T100 110 L100 150 L0 150 Z" fill="#1F2E24"/><path d="M18 122h64M26 130h48" stroke="#DCE7DE" stroke-width="0.6"/></svg>`
    }
  };
  const j = jackets[b.coverKind] || jackets['jacket-gatsby'];
  return `
    <div class="book-cover ${extraClass}" style="${style}background:${j.bg};color:${j.ink};">
      ${j.art}
      <div style="position:relative;z-index:1;height:100%;padding:${small ? '5px' : '12px 10px'};display:flex;flex-direction:column;justify-content:space-between;">
        <div>
          <div class="${b.title.charCodeAt(0) > 255 ? 'serif' : 'georgia'}" style="font-size:${small ? '7.5px' : '12.5px'};font-weight:600;line-height:1.28;display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden;">${b.title}</div>
          ${!small ? `<div style="font-size:9px;color:${j.sub};margin-top:4px;" class="truncate">${b.author}</div>` : ''}
        </div>
      </div>
      ${audioBadge}
    </div>`;
}
