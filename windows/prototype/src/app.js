// ============================================================================
// Prototype State Controller, UndoToast & Interactive Studio Switcher (v2)
// ============================================================================

const STATE = {
  win: 'shelf',           // 'shelf' | 'reader' | 'pdf' | 'audiobook' | 'settings' | 'tray'
  theme: 'light',         // 'light' | 'dark'
  appearanceMode: 'light',// 'light' | 'dark' | 'auto'
  tint: 'camel',          // 'camel' | 'sage' | 'mist' | 'blush'
  chrome: 'win',          // 'win' (Seamless Windows 11 top strip) | 'mac' (macOS ● ● ● reference)
  sidebarShown: true,
  closedGroups: {},
  shelfTab: 'all',        // 'all' | 'books' | 'podcasts' | 'audio' | 'notes' | 'words' | 'collections' | 'col:<id>'
  shelfSort: '最近读过',
  coverSize: 100,
  showShelfProgress: true,
  showImportCard: false,
  shelfDropOverlay: false,
  shelfEmptyDemo: false,
  readingExpanded: false,
  booksStatus: 'reading',
  recordBookId: null,
  recordSubTab: 'contents',
  recordChaptersAsGrid: false,
  chapterJumpBookId: null,
  podFilter: 'all',
  podDrawerTab: null,     // null | 'clips' | 'later' | 'voices' | 'local'
  podClipDotCleared: false,
  podCmdDrop: false,
  podcastIssueOpen: false,
  podTrack: 'coach',      // 'orig' | 'dub' | 'coach'
  podBlind: false,
  podVocabPop: false,
  podInterruptOpen: false,
  podInterruptAnswered: false,
  notesByBook: false,
  notesCoversOnly: false,
  wordsShowKnown: false,
  reviewFinished: false,
  quickSearchOpen: false,
  quickQuery: '',
  colPickerSearching: false,
  fbCat: 'bug',
  fbInfoExpanded: false,
  bookMenu: null,
  modal: null,

  // Reader state
  paper: 'kami',
  paperTexture: 'none',
  customPaperBg: '#EFECE2',
  customPaperInk: '#26251F',
  readerFont: null,
  readerLh: null,
  fontSize: 18,
  readerTwoCol: true,
  readerPlaying: false,
  readerBarShown: false,
  readerShowCitation: true,
  readerBilingual: false,
  readerSearchBar: false,
  readerPanel: null,      // null | 'toc' | 'notes' | 'companion' | 'cast'
  castShowingUnsure: false,
  readerPopover: null,    // null | 'typeset' | 'listen'
  typesetSub: 'root',     // 'root' | 'paperEditor' | 'moreSettings'
  readerOverlay: null,    // null | 'selection' | 'word' | 'footnote' | 'recap' | 'elsewhere' | 'sleep'
  quoteLook: 'kami',

  // PDF & Audiobook state
  pdfNight: false,
  pdfLayout: 'two',
  pdfSide: null,
  pdfShowCard: false,
  audioMode: 'player',    // 'player' | 'align'
  audioHasMap: true,
  audioMakeMode: null,    // null | 'making' | 'result'

  // Settings state
  settingsPage: 'appearance',
  settingsHistoryOpen: false,
  historyConfirmClear: false,
  appLang: 'zh',
  langMenuOpen: false,
  foreignLevel: 3,
  voiceAuditionOpen: false,
  voiceSourceTab: 'mac',
  voiceBookLang: 'zh',
  auditionVoiceId: 'chenzhou',
  quickLinesOpen: false,
  voiceCustomLine: '',
  companionSelected: 'deepseek',
  companionModelListOpen: false,
  syncConnected: true,
  syncMethod: 'webdav',
  noteExportTarget: 'obsidian', // 'obsidian' | 'notion' | 'flomo' | 'xinzhi'
  snapshotIncludeBooks: false,
  snapImportMode: 'merge',
  csvExportFmt: 'goodreads',
  sigRun: 1,
  updateOfferShown: false,

  // UndoToast state
  toast: null,
  toastCanUndo: false,
  toastDanger: false
};

let toastTimer = null;

function renderApp() {
  const root = document.documentElement;
  root.setAttribute('data-theme', STATE.theme);
  root.setAttribute('data-tint', STATE.tint);
  root.setAttribute('data-chrome', STATE.chrome);
  document.body.setAttribute('data-theme', STATE.theme);

  const mount = document.getElementById('app');
  if (!mount) return;

  mount.innerHTML = `
    ${renderGlobalSvgDefs()}
    <div class="proto-workbench">
      <!-- Top Interactive Prototype Studio Switcher -->
      <div class="proto-bar">
        <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap;">
          <div style="display:flex;align-items:center;gap:7px;padding-right:4px;">
            ${renderYuyueMark(24)}
            <strong style="font-size:13px;">语阅 Windows 1:1 交互原型</strong>
          </div>

          <div class="proto-win-tabs">
            ${[
              { id: 'shelf', label: '1. 书架与子页' },
              { id: 'reader', label: '2. 电子书阅读' },
              { id: 'pdf', label: '3. PDF 阅读' },
              { id: 'audiobook', label: '4. 有声书与对齐' },
              { id: 'settings', label: '5. 设置 (11页)' },
              { id: 'tray', label: '6. 系统托盘' }
            ].map(w => `
              <button class="proto-win-tab ${STATE.win === w.id ? 'active' : ''}" onclick="openWin('${w.id}')">${w.label}</button>
            `).join('')}
          </div>
        </div>

        <div class="proto-controls">
          <div class="proto-tint-dots" title="切换 4 套主题色">
            ${[['camel','#8A653F','暖纸'],['sage','#4F7657','草木'],['mist','#4F6D88','烟雨'],['blush','#A5656F','海棠']].map(t => `
              <button class="proto-tint-dot ${STATE.tint === t[0] ? 'active' : ''}" style="background:${t[1]};" title="主题色：${t[2]}" onclick="setTint('${t[0]}')"></button>
            `).join('')}
          </div>

          <div class="yy-seg sm">
            <button class="yy-seg-btn ${STATE.theme === 'light' ? 'active' : ''}" onclick="setAppearanceMode('light')">浅色</button>
            <button class="yy-seg-btn ${STATE.theme === 'dark' ? 'active' : ''}" onclick="setAppearanceMode('dark')">深色</button>
          </div>

          <div class="yy-seg sm" title="对比 Windows 无缝沉浸标题栏与 macOS 红黄绿基准">
            <button class="yy-seg-btn ${STATE.chrome === 'win' ? 'active' : ''}" onclick="setChrome('win')">Win 窗口钮</button>
            <button class="yy-seg-btn ${STATE.chrome === 'mac' ? 'active' : ''}" onclick="setChrome('mac')">Mac 对照</button>
          </div>
        </div>
      </div>

      <!-- Active Simulated Window -->
      ${renderCurrentWindow()}

      <!-- Quick Sub-State Jump Strip Below Window -->
      ${renderSubStateStrip()}

      <!-- Exact UndoToast (UndoToast.swift: 40px height, #FCFCFC/#2A2A2C, 17x17 disc, optional 24x24 undo circle!) -->
      ${STATE.toast ? `
        <div style="position:fixed;bottom:24px;left:50%;transform:translateX(-50%);height:40px;max-width:420px;padding:0 ${STATE.toastCanUndo ? '8px' : '18px'} 0 14px;border-radius:999px;background:${STATE.theme === 'dark' ? '#2A2A2C' : '#FCFCFC'};border:0.5px solid ${STATE.theme === 'dark' ? 'rgba(255,255,255,0.12)' : 'rgba(0,0,0,0.14)'};box-shadow:0 1px 1px rgba(0,0,0,0.03),0 6px 22px rgba(0,0,0,0.12);display:inline-flex;align-items:center;z-index:300;">
          <span style="width:17px;height:17px;border-radius:50%;background:${STATE.toastDanger ? 'var(--yy-danger)' : '#2DBE60'};color:#FFFFFF;display:inline-flex;align-items:center;justify-content:center;margin-right:10px;flex-shrink:0;">
            ${ICONS.check.replace('width="12" height="12"', 'width="9" height="9"')}
          </span>
          <span class="truncate" style="font-size:13px;font-weight:500;color:var(--yy-t1);">${STATE.toast}</span>
          ${STATE.toastCanUndo ? `
            <button onclick="showProtoToast('已撤销刚才的操作')" title="撤销 (Ctrl+Z)" style="margin-left:13px;width:24px;height:24px;border-radius:50%;background:${STATE.theme === 'dark' ? '#3A3A3C' : '#EBEBEB'};color:var(--yy-t1);display:inline-flex;align-items:center;justify-content:center;flex-shrink:0;">
              ${ICONS.undo}
            </button>
          ` : ''}
        </div>
      ` : ''}
    </div>
  `;

  if (STATE.quickSearchOpen) {
    const inp = document.getElementById('qs-input');
    if (inp) { inp.focus(); inp.setSelectionRange(inp.value.length, inp.value.length); }
  }
}

function renderCurrentWindow() {
  switch (STATE.win) {
    case 'reader': return renderReaderWindow(STATE);
    case 'pdf': return renderPdfWindow(STATE);
    case 'audiobook': return renderAudiobookWindow(STATE);
    case 'settings': return renderSettingsWindow(STATE);
    case 'tray': return renderTrayWindow(STATE);
    default: return renderShelfWindow(STATE);
  }
}

function renderSubStateStrip() {
  let pills = [];
  if (STATE.win === 'shelf') {
    pills = [
      ['书架首页', "setShelfTab('all');toggleShelfEmptyDemo(false);toggleImportCard(false)"],
      ['导入进度卡', "setShelfTab('all');toggleImportCard(true)"],
      ['拖入文件浮层', "setShelfTab('all');toggleShelfDropOverlay()"],
      ['空书架状态', "setShelfTab('all');toggleShelfEmptyDemo(true)"],
      ['删除确认框', "setShelfTab('all');setModal('deleteBookConfirm')"],
      ['加入合集框', "setShelfTab('all');setModal('collectionPicker')"],
      ['进度卡片墙', "setShelfTab('books');openRecordBook(null)"],
      ['书籍档案页', "setShelfTab('books');openRecordBook('hlm')"],
      ['播客工作台', "setShelfTab('podcasts');closePodcastReader()"],
      ['播客·书摘/稍后听抽屉', "setShelfTab('podcasts');openPodcastDrawer('clips')"],
      ['播客·声卡/本机抽屉', "setShelfTab('podcasts');openPodcastDrawer('voices')"],
      ['播客访谈书页', "setShelfTab('podcasts');openPodcastReader()"],
      ['笔记与今天回顾', "setShelfTab('notes');setNotesMode(false)"],
      ['生词本', "setShelfTab('words')"],
      ['合集墙', "setShelfTab('collections')"],
      ['反馈弹窗', "setModal('feedback')"]
    ];
  } else if (STATE.win === 'reader') {
    pills = [
      ['静读模式 (无胶囊)', "toggleReaderBar(false);STATE.readerPlaying=false;renderApp()"],
      ['唤出跟读胶囊', "toggleReaderBar(true)"],
      ['排版卡 (Aa)', "setReaderPopover('typeset');setTypesetSub('root')"],
      ['调纸 (我的纸)', "setReaderPopover('typeset');setTypesetSub('paperEditor')"],
      ['更多排版 (8项)', "setReaderPopover('typeset');setTypesetSub('moreSettings')"],
      ['听书悬浮卡', "toggleReaderBar(true);setReaderPopover('listen')"],
      ['选词划线卡', "setReaderOverlay('selection')"],
      ['外文查词卡', "setReaderOverlay('word')"],
      ['右侧·目录', "setReaderPanel('toc')"],
      ['右侧·书伴问答', "setReaderPanel('companion')"],
      ['右侧·人物广播剧', "setReaderPanel('cast');toggleCastUnsure(false)"],
      ['右侧·待确认台词', "setReaderPanel('cast');toggleCastUnsure(true)"],
      ['人物详情弹窗', "setModal('castRole')"],
      ['分享书摘卡', "setModal('quoteShare')"]
    ];
  } else if (STATE.win === 'audiobook') {
    pills = [
      ['已配对播放器 + 这本录音', "setAudioMode('player');STATE.audioHasMap=true;setAudioMakeMode(null)"],
      ['未配对纯录音状态', "setAudioMode('player');STATE.audioHasMap=false;setAudioMakeMode(null)"],
      ['合成中 (68% 矩阵)', "setAudioMode('player');setAudioMakeMode('making')"],
      ['合成好了 (3处报告)', "setAudioMode('player');setAudioMakeMode('result')"],
      ['对齐检查微调窗', "setAudioMode('align')"]
    ];
  } else if (STATE.win === 'settings') {
    pills = [
      ...SETTINGS_PAGES.map(p => [p.title, `setSettingsPage('${p.id}')`]),
      ['朗读·气泡试听墙', "setSettingsPage('listening');setVoiceAuditionOpen(true)"],
      ['书伴·18家服务商弹窗', "setSettingsPage('companion');setModal('companionSheet')"],
      ['最近改过 (380px)', "toggleSettingsHistory(true)"]
    ];
  }
  if (!pills.length) return '';
  return `
    <div style="max-width:1120px;display:flex;align-items:center;gap:6px;flex-wrap:wrap;justify-content:center;">
      <span style="font-size:11px;color:var(--yy-t2);">快捷跳页验收：</span>
      ${pills.map(p => `<button class="yy-pill sm" style="height:24px;font-size:11px;" onclick="${p[1]}">${p[0]}</button>`).join('')}
    </div>
  `;
}

// Actions
window.openWin = (w, sub) => {
  STATE.win = w;
  STATE.modal = null;
  STATE.bookMenu = null;
  if (w === 'settings' && sub) {
    STATE.settingsPage = sub;
    STATE.voiceAuditionOpen = false;
  }
  renderApp();
};
window.setTheme = t => { STATE.theme = t; STATE.appearanceMode = t; renderApp(); };
window.setAppearanceMode = m => {
  STATE.appearanceMode = m;
  STATE.theme = m === 'dark' ? 'dark' : 'light';
  renderApp();
};
window.setTint = t => { STATE.tint = t; renderApp(); };
window.setChrome = c => { STATE.chrome = c; renderApp(); };
window.toggleSidebar = () => { STATE.sidebarShown = !STATE.sidebarShown; renderApp(); };
window.toggleGroup = k => { STATE.closedGroups[k] = !STATE.closedGroups[k]; renderApp(); };
window.setShelfTab = t => {
  STATE.shelfTab = t;
  STATE.recordBookId = null;
  STATE.chapterJumpBookId = null;
  STATE.podcastIssueOpen = false;
  STATE.podDrawerTab = null;
  STATE.bookMenu = null;
  STATE.shelfEmptyDemo = false;
  renderApp();
};
window.toggleImportCard = v => { STATE.showImportCard = typeof v === 'boolean' ? v : !STATE.showImportCard; renderApp(); };
window.toggleShelfDropOverlay = () => { STATE.shelfDropOverlay = !STATE.shelfDropOverlay; renderApp(); };
window.toggleShelfEmptyDemo = v => { STATE.shelfEmptyDemo = typeof v === 'boolean' ? v : !STATE.shelfEmptyDemo; renderApp(); };
window.setShelfCoverSize = v => { STATE.coverSize = Number(v); renderApp(); };
window.toggleShelfProgressShow = () => { STATE.showShelfProgress = !STATE.showShelfProgress; renderApp(); };
window.toggleReadingExpand = () => { STATE.readingExpanded = !STATE.readingExpanded; renderApp(); };
window.cycleShelfSort = () => {
  const orders = ['最近读过', '加入时间', '书名', '作者', '封面颜色'];
  STATE.shelfSort = orders[(orders.indexOf(STATE.shelfSort) + 1) % orders.length];
  renderApp();
};
window.openBookByFormat = id => {
  const b = BOOKS.find(x => x.id === id);
  STATE.bookMenu = null;
  if (b && b.format === 'pdf') STATE.win = 'pdf';
  else if (b && b.format === 'audio') { STATE.win = 'audiobook'; STATE.audioMode = 'player'; }
  else STATE.win = 'reader';
  renderApp();
};
window.openBookListen = id => {
  const b = BOOKS.find(x => x.id === id);
  if (b && b.format === 'audio') { STATE.win = 'audiobook'; STATE.audioMode = 'player'; }
  else { STATE.win = 'reader'; STATE.readerPlaying = true; STATE.readerBarShown = true; STATE.readerPopover = 'listen'; }
  renderApp();
};
window.openBookMenu = (id, x, y) => { STATE.bookMenu = { id, x: x || 320, y: y || 240 }; renderApp(); };
window.closeBookMenu = () => { if (STATE.bookMenu) { STATE.bookMenu = null; renderApp(); } };
window.toggleColPickerSearch = v => { STATE.colPickerSearching = v; renderApp(); };
window.setFbCat = c => { STATE.fbCat = c; renderApp(); };
window.toggleFbInfoExpand = () => { STATE.fbInfoExpanded = !STATE.fbInfoExpanded; renderApp(); };

window.setBooksStatus = s => { STATE.booksStatus = s; STATE.chapterJumpBookId = null; renderApp(); };
window.openRecordBook = id => { STATE.recordBookId = id; STATE.chapterJumpBookId = null; renderApp(); };
window.setRecordSubTab = t => { STATE.recordSubTab = t; renderApp(); };
window.toggleRecordGrid = v => { STATE.recordChaptersAsGrid = v; renderApp(); };
window.toggleChapterJump = id => { STATE.chapterJumpBookId = STATE.chapterJumpBookId === id ? null : id; renderApp(); };
window.closeChapterJump = () => { if (STATE.chapterJumpBookId) { STATE.chapterJumpBookId = null; renderApp(); } };

window.setPodFilter = f => { STATE.podFilter = f; renderApp(); };
window.openPodcastDrawer = t => {
  STATE.podDrawerTab = t;
  if (t === 'clips' || t === 'later') STATE.podClipDotCleared = true;
  renderApp();
};
window.openPodcastReader = () => { STATE.podcastIssueOpen = true; STATE.podDrawerTab = null; renderApp(); };
window.closePodcastReader = () => { STATE.podcastIssueOpen = false; STATE.podInterruptOpen = false; STATE.podInterruptAnswered = false; STATE.podVocabPop = false; renderApp(); };
window.setPodTrack = t => { STATE.podTrack = t; renderApp(); };
window.togglePodBlind = () => { STATE.podBlind = !STATE.podBlind; renderApp(); };
window.togglePodVocabPop = () => { STATE.podVocabPop = !STATE.podVocabPop; renderApp(); };
window.togglePodInterrupt = () => { STATE.podInterruptOpen = !STATE.podInterruptOpen; if (!STATE.podInterruptOpen) STATE.podInterruptAnswered = false; renderApp(); };
window.answerPodInterrupt = () => { STATE.podInterruptAnswered = true; renderApp(); };
window.togglePodCmdDrop = v => { STATE.podCmdDrop = typeof v === 'boolean' ? v : !STATE.podCmdDrop; renderApp(); };

window.setNotesMode = v => { STATE.notesByBook = v; renderApp(); };
window.toggleNotesCovers = v => { STATE.notesCoversOnly = v; renderApp(); };
window.toggleWordsKnown = v => { STATE.wordsShowKnown = v; renderApp(); };
window.toggleReviewDone = v => { STATE.reviewFinished = v; renderApp(); };

window.openQuickSearch = () => { STATE.quickSearchOpen = true; renderApp(); };
window.closeQuickSearch = () => { STATE.quickSearchOpen = false; renderApp(); };
window.updateQuickQuery = v => { STATE.quickQuery = v; renderApp(); };
window.setModal = m => { STATE.modal = m; STATE.readerPopover = null; STATE.colPickerSearching = false; renderApp(); };

// Reader actions
window.setPaper = p => { STATE.paper = p; renderApp(); };
window.setPaperTexture = tx => { STATE.paperTexture = tx; renderApp(); };
window.setCustomPaper = (bg, ink) => { STATE.paper = 'custom'; STATE.customPaperBg = bg; STATE.customPaperInk = ink; renderApp(); };
window.setReaderFont = f => { STATE.readerFont = f; renderApp(); };
window.setReaderLh = lh => { STATE.readerLh = lh; renderApp(); };
window.setReaderFontSize = v => { STATE.fontSize = Number(v); renderApp(); };
window.setReaderTwoCol = v => { STATE.readerTwoCol = v; renderApp(); };
window.toggleReaderPlay = () => { STATE.readerPlaying = !STATE.readerPlaying; if (STATE.readerPlaying) STATE.readerBarShown = true; renderApp(); };
window.toggleReaderBar = v => { STATE.readerBarShown = typeof v === 'boolean' ? v : !STATE.readerBarShown; if (!STATE.readerBarShown) { STATE.readerPlaying = false; STATE.readerPopover = null; } renderApp(); };
window.toggleReaderCitation = v => { STATE.readerShowCitation = typeof v === 'boolean' ? v : !STATE.readerShowCitation; renderApp(); };
window.toggleReaderBilingual = () => { STATE.readerBilingual = !STATE.readerBilingual; renderApp(); };
window.toggleReaderSearch = v => { STATE.readerSearchBar = v; renderApp(); };
window.setReaderPanel = p => { STATE.readerPanel = STATE.readerPanel === p ? null : p; STATE.readerPopover = null; renderApp(); };
window.toggleCastUnsure = v => { STATE.castShowingUnsure = v; renderApp(); };
window.setReaderPopover = p => { STATE.readerPopover = STATE.readerPopover === p ? null : p; STATE.typesetSub = 'root'; renderApp(); };
window.setTypesetSub = s => { STATE.typesetSub = s; renderApp(); };
window.setReaderOverlay = o => { STATE.readerOverlay = STATE.readerOverlay === o ? null : o; if (STATE.readerOverlay && ['recap','elsewhere','sleep'].includes(STATE.readerOverlay)) STATE.readerBarShown = true; renderApp(); };
window.closeReaderPopovers = () => {
  if (STATE.readerPopover || STATE.readerOverlay) {
    STATE.readerPopover = null;
    STATE.readerOverlay = null;
    renderApp();
  }
};
window.setQuoteLook = l => { STATE.quoteLook = l; renderApp(); };

// PDF & Audiobook actions
window.togglePdfNight = () => { STATE.pdfNight = !STATE.pdfNight; renderApp(); };
window.setPdfLayout = l => { STATE.pdfLayout = l; renderApp(); };
window.togglePdfSide = s => { STATE.pdfSide = STATE.pdfSide === s ? null : s; STATE.pdfShowCard = false; renderApp(); };
window.togglePdfShowCard = () => { STATE.pdfShowCard = !STATE.pdfShowCard; renderApp(); };
window.setAudioMode = m => { STATE.audioMode = m; renderApp(); };
window.toggleAudioHasMap = () => { STATE.audioHasMap = !STATE.audioHasMap; renderApp(); };
window.setAudioMakeMode = m => { STATE.audioMakeMode = m; renderApp(); };

// Settings actions
window.setSettingsPage = p => {
  STATE.settingsPage = p;
  STATE.modal = null;
  STATE.settingsHistoryOpen = false;
  STATE.voiceAuditionOpen = false;
  STATE.langMenuOpen = false;
  renderApp();
};
window.toggleSettingsHistory = v => { STATE.settingsHistoryOpen = typeof v === 'boolean' ? v : !STATE.settingsHistoryOpen; STATE.historyConfirmClear = false; renderApp(); };
window.toggleHistoryClearConfirm = v => { STATE.historyConfirmClear = v; renderApp(); };
window.closeSettingsPopups = () => {
  if (STATE.settingsHistoryOpen || STATE.langMenuOpen || STATE.quickLinesOpen) {
    STATE.settingsHistoryOpen = false;
    STATE.langMenuOpen = false;
    STATE.quickLinesOpen = false;
    renderApp();
  }
};
window.toggleLangMenu = () => { STATE.langMenuOpen = !STATE.langMenuOpen; renderApp(); };
window.pickLanguage = l => {
  const prev = STATE.appLang;
  STATE.appLang = l;
  STATE.langMenuOpen = false;
  if (l !== prev) STATE.modal = 'langRestartConfirm';
  renderApp();
};
window.setForeignLevel = lv => { STATE.foreignLevel = lv; renderApp(); };
window.setVoiceAuditionOpen = (v, src) => {
  STATE.voiceAuditionOpen = v;
  if (src) STATE.voiceSourceTab = src;
  renderApp();
};
window.setVoiceBookLang = l => { STATE.voiceBookLang = l; STATE.voiceCustomLine = ''; renderApp(); };
window.selectAuditionVoice = id => { STATE.auditionVoiceId = id; renderApp(); };
window.toggleQuickLines = () => { STATE.quickLinesOpen = !STATE.quickLinesOpen; renderApp(); };
window.pickQuickLine = line => { STATE.voiceCustomLine = line; STATE.quickLinesOpen = false; renderApp(); };
window.selectCompanionProvider = id => { STATE.companionSelected = id; STATE.companionModelListOpen = false; renderApp(); };
window.toggleCompanionModelList = () => { STATE.companionModelListOpen = !STATE.companionModelListOpen; renderApp(); };
window.toggleSyncDemo = () => { STATE.syncConnected = !STATE.syncConnected; renderApp(); };
window.setSyncMethod = m => { STATE.syncMethod = m; renderApp(); };
window.selectNoteExportTarget = t => { STATE.noteExportTarget = t; renderApp(); };
window.toggleSnapshotBooks = () => { STATE.snapshotIncludeBooks = !STATE.snapshotIncludeBooks; renderApp(); };
window.setSnapImportMode = m => { STATE.snapImportMode = m; renderApp(); };
window.setCsvExportFmt = f => { STATE.csvExportFmt = f; renderApp(); };
window.replaySignature = () => {
  STATE.sigAnimating = false;
  renderApp();
  requestAnimationFrame(() => {
    STATE.sigAnimating = true;
    renderApp();
    setTimeout(() => { STATE.sigAnimating = false; renderApp(); }, 1650);
  });
};
window.toggleUpdateOffer = v => { STATE.updateOfferShown = v; renderApp(); };

window.showProtoToast = (msg, canUndo = false, danger = false) => {
  STATE.toast = msg;
  STATE.toastCanUndo = !!canUndo;
  STATE.toastDanger = !!danger;
  if (toastTimer) clearTimeout(toastTimer);
  renderApp();
  toastTimer = setTimeout(() => { STATE.toast = null; renderApp(); }, 2800);
};

window.addEventListener('keydown', e => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
    e.preventDefault();
    STATE.quickSearchOpen = !STATE.quickSearchOpen;
    renderApp();
  } else if (e.key === 'Escape') {
    STATE.quickSearchOpen = false;
    STATE.modal = null;
    STATE.bookMenu = null;
    STATE.readerPopover = null;
    STATE.readerOverlay = null;
    STATE.podDrawerTab = null;
    STATE.settingsHistoryOpen = false;
    STATE.shelfDropOverlay = false;
    renderApp();
  }
});

function applyUrlHash() {
  const hash = new URLSearchParams(location.hash.slice(1));
  if (hash.get('win')) STATE.win = hash.get('win');
  STATE.chrome = hash.get('chrome') || 'win';
  if (hash.get('theme')) { STATE.theme = hash.get('theme'); STATE.appearanceMode = hash.get('theme'); }
  STATE.shelfTab = hash.get('tab') || 'all';
  STATE.podDrawerTab = hash.get('pdraw') || null;
  STATE.podcastIssueOpen = hash.get('pissue') === '1';
  if (hash.get('spage')) STATE.settingsPage = hash.get('spage');
  STATE.voiceAuditionOpen = hash.get('vaud') === '1';
  STATE.noteExportTarget = hash.get('ntarget') || 'obsidian';
  STATE.settingsHistoryOpen = hash.get('shist') === '1';
  STATE.readerBarShown = hash.get('rbar') === '1' || hash.get('rplay') === '1' || hash.get('rpop') === 'listen';
  STATE.readerPlaying = hash.get('rplay') === '1';
  STATE.readerPopover = hash.get('rpop') || null;
  STATE.typesetSub = hash.get('tsub') || 'root';
  STATE.readerPanel = hash.get('rpanel') || null;
  STATE.castShowingUnsure = hash.get('cunsure') === '1';
  STATE.audioMode = hash.get('amode') || 'player';
  STATE.audioMakeMode = hash.get('amake') || null;
  STATE.modal = hash.get('modal') || null;
  STATE.shelfEmptyDemo = hash.get('empty') === '1';
  STATE.showImportCard = hash.get('import') === '1';
}

window.addEventListener('hashchange', () => { applyUrlHash(); renderApp(); });
window.addEventListener('DOMContentLoaded', () => { applyUrlHash(); renderApp(); });
