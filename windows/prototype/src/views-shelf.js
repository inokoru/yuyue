// ============================================================================
// Shelf Window (MacShelfView) — Forensic 1:1 All 7 Sidebar Views, Podcast & Modals
// Verbatim from MacShelfView.swift, MacImport.swift, PodcastSharedUI.swift,
// MacPodcastView.swift, PodcastReaderView.swift, CollectionPicker.swift, Feedback.swift
// ============================================================================

const COLLECTIONS = [
  { id: 'classic', name: '文学经典', count: 5, lastUsed: '今天', pinned: true, icon: 'book' },
  { id: 'en', name: '英文原版', count: 3, lastUsed: '昨天', pinned: true, icon: 'translate' },
  { id: 'ai', name: '人工智能与认知', count: 2, lastUsed: '3 天前', pinned: false, icon: 'sparkle' },
  { id: 'scifi', name: '科幻星海', count: 4, lastUsed: '上周', pinned: false, icon: 'stack' },
  { id: 'aesthetic', name: '东方器物与美学', count: 3, lastUsed: '2 周前', pinned: false, icon: 'sun' },
  { id: 'night', name: '睡前慢听', count: 2, lastUsed: '没用过', pinned: false, icon: 'headphones' }
];

const NOTES_DATA = [
  { id: 'm1', bookId: 'hlm', bookTitle: '红楼梦', chapter: '第十七回 · 大观园试才题对额', day: '今天', dateLabel: '11 月 23 日', time: '09:12', style: 'yellow', text: '曲径通幽处，禅房花木深。山光悦鸟性，潭影空人心。', note: '借古人句点破“绕堤柳借三篙翠，隔岸花分一脉香”之妙，园未见而气韵先出。', audio: true },
  { id: 'm2', bookId: 'gatsby', bookTitle: 'The Great Gatsby', chapter: 'Chapter IV', day: '今天', dateLabel: '11 月 23 日', time: '08:40', style: 'blue', text: 'Reserving judgments is a matter of infinite hope.', note: 'Nick 父亲给他的忠告，其实也是全书悲剧的旁观基调。', audio: false },
  { id: 'm3', bookId: 'guxiang', bookTitle: '呐喊', chapter: '故乡', day: '昨天', dateLabel: '11 月 22 日', time: '22:18', style: 'wave', text: '希望是本无所谓有，无所谓无的。这正如地上的路；其实地上本没有路，走的人多了，也便成了路。', note: null, audio: true },
  { id: 'm4', bookId: 'hlm', bookTitle: '红楼梦', chapter: '第五回 · 游幻境指迷十二钗', day: '昨天', dateLabel: '11 月 22 日', time: '21:05', style: 'pink', text: '假作真时真亦假，无为有处有还无。', note: '太虚幻境联语，全书总纲。', audio: false }
];

const VOCAB_DATA = [
  { word: 'incredulity', phonetic: '/ˌɪn.krəˈdjuː.lə.ti/', pos: 'n.', meaning: '怀疑；不轻信', context: 'It was the epoch of belief, it was the epoch of incredulity, it was the season of Light…', match: 'incredulity', book: 'Pride and Prejudice · Chapter I', freq: '在 3 本书里出现 19 次', day: '今天', dateLabel: '11 月 23 日', known: false },
  { word: 'ephemeral', phonetic: '/ɪˈfem.ər.əl/', pos: 'adj.', meaning: '转瞬即逝的；短暂的', context: 'Gatsby believed in the green light, the orgastic future that year by year recedes before us, chasing ephemeral dreams.', match: 'ephemeral', book: 'The Great Gatsby · Chapter IX', freq: '在 2 本书里出现 8 次', day: '今天', dateLabel: '11 月 23 日', known: false },
  { word: 'fastidious', phonetic: '/fæsˈtɪd.i.əs/', pos: 'adj.', meaning: '挑剔的；一丝不苟的', context: 'Darcy was clever, haughty, reserved, and fastidious, and his manners, though well bred, were not inviting.', match: 'fastidious', book: 'Pride and Prejudice · Chapter IV', freq: '在这本书里出现 5 次', day: '昨天', dateLabel: '11 月 22 日', known: false },
  { word: 'generalization', phonetic: '/ˌdʒen.ər.əl.aɪˈzeɪ.ʃən/', pos: 'n.', meaning: '泛化能力；归纳推演', context: 'The intelligence of a system is a measure of its skill-acquisition efficiency over a scope of tasks, with respect to priors and generalization difficulty.', match: 'generalization', book: 'On the Measure of Intelligence · §3.2', freq: '在 2 本书里出现 41 次', day: '昨天', dateLabel: '11 月 22 日', known: true }
];

const POD_CLIPS = [
  { id: 'c1', quote: '真正的智能并不是背下多少知识库，而是面对从未见过的新问题时，当场习得新技能的泛化效率。', show: 'Lex Fridman #452', clock: '14:18', dur: '16.4s', thought: '对应 Chollet 2019 论文的核心定义' },
  { id: 'c2', quote: '大观园题额并非卖弄辞藻，而是曹雪芹借宝玉之眼，提前为众女儿立传。', show: '书房电台 · 周刊', clock: '04:12', dur: '12.8s', thought: '' },
  { id: 'c3', quote: '美并不在于物体本身，而在于物体与物体所交织出的阴翳的波纹与明暗之中。', show: '书房电台 · 圆桌', clock: '08:45', dur: '11.2s', thought: '' }
];

function shelfNoteFor(b, isAudioTab) {
  if (isAudioTab || b.format === 'audio') {
    if (b.progress >= 1) return '有声 · 听完';
    if (b.progress > 0) return `有声 · ${b.pct}`;
    return `有声 · ${b.remaining.replace(/^约\s*/, '')}`;
  }
  return b.pct;
}

function renderShelfWindow(S) {
  const insetLeft = 32; // Clears SidebarToggle (28x22 at left:16px) inside shelf-paper
  const winH = S.chrome === 'mac' ? 720 : 748;
  return `
    <div class="win-frame" style="width:1120px;height:${winH}px;--cover-size:${S.coverSize || 96}px;">
      ${renderWinTitleBar('shelf')}
      <div class="mac-lights" style="position:absolute;top:18px;left:22px;">
        <span class="mac-dot close"></span><span class="mac-dot min"></span><span class="mac-dot max"></span>
      </div>

      <div class="shelf-desk">
        <!-- Left Sidebar directly on ShelfDesk (#EAE8E3) -->
        <aside class="shelf-sidebar ${S.sidebarShown ? '' : 'collapsed'}">
          <button class="shelf-search-btn" onclick="openQuickSearch()">
            ${ICONS.search}
            <span>搜索 · 问</span>
            <span style="margin-left:auto;font-size:10.5px;color:var(--yy-t3);">${S.chrome === 'mac' ? '⌘K' : 'Ctrl+K'}</span>
          </button>

          <div style="flex:1;overflow-y:auto;padding:0 2px;">
            ${renderSidebarGroup('资料库', 'lib', S)}
            ${!S.closedGroups.lib ? `
              ${renderSidebarRow('all', '书', S.shelfTab === 'all' ? ICONS.shelfFill : ICONS.shelf, BOOKS.length, S)}
              ${renderSidebarRow('books', '进度', ICONS.protoBook, BOOKS.filter(b => b.status === 'reading').length, S)}
              ${renderSidebarRow('podcasts', '播客', ICONS.radio, 3, S)}
              ${renderSidebarRow('audio', '有声', ICONS.wave, BOOKS.filter(b => b.hasAudio).length, S)}
            ` : ''}

            ${renderSidebarGroup('笔记', 'notes', S)}
            ${!S.closedGroups.notes ? `
              ${renderSidebarRow('notes', '笔记', ICONS.highlighter, NOTES_DATA.length, S)}
              ${renderSidebarRow('words', '生词本', ICONS.translate, VOCAB_DATA.filter(w => !w.known).length, S)}
            ` : ''}

            ${renderSidebarGroup('合集', 'cols', S)}
            ${!S.closedGroups.cols ? `
              <button class="sidebar-row ${S.shelfTab === 'collections' ? 'active' : ''}" onclick="setShelfTab('collections')">
                <span class="ico">${ICONS.stack}</span>
                <span>合集</span>
                <span class="cnt">${COLLECTIONS.length} ›</span>
              </button>
              ${COLLECTIONS.filter(c => c.pinned).map(c => `
                <button class="sidebar-row ${S.shelfTab === 'col:' + c.id ? 'active' : ''}" onclick="setShelfTab('col:${c.id}')">
                  <span class="ico">${ICONS[c.icon] || ICONS.book}</span>
                  <span class="truncate">${c.name}</span>
                  <span class="cnt">${c.count}</span>
                </button>
              `).join('')}
            ` : ''}
          </div>

          <div class="sidebar-foot">
            <button onclick="openWin('settings','sync')" style="flex:1;display:flex;align-items:center;gap:7px;padding:6px 8px;border-radius:7px;text-align:left;" title="点此打开设置 · 同步">
              <span style="width:6px;height:6px;border-radius:50%;background:${S.syncConnected ? 'var(--yy-ok)' : 'var(--yy-t3)'};flex-shrink:0;"></span>
              <span class="truncate" style="font-size:11px;color:var(--yy-t2);">${S.syncConnected ? 'WebDAV · 刚刚同步' : '尚未开启同步'}</span>
            </button>
            <button class="sidebar-foot-icon" title="设置 (${S.chrome === 'mac' ? '⌘,' : 'Ctrl+,'})" onclick="openWin('settings','general')">${ICONS.gear}</button>
            <button class="sidebar-foot-icon" title="反馈" onclick="setModal('feedback')">${ICONS.feedback}</button>
          </div>
        </aside>

        <!-- Right Lifted Paper Sheet (ShelfDesk.Paper: 12px radius, crisp outer 0.5px ring) -->
        <main class="shelf-paper">
          ${!(S.shelfTab === 'podcasts' && S.podcastIssueOpen) ? `
            <button class="shelf-sidebar-toggle" style="top:19px;left:16px;" onclick="toggleSidebar()" title="${S.sidebarShown ? '隐藏侧栏' : '显示侧栏'} (${S.chrome === 'mac' ? '⌘⌥N' : 'Ctrl+Alt+N'})">
              ${ICONS.sidebar}
            </button>
          ` : ''}
          ${renderShelfPageContent(S, insetLeft)}
          ${S.shelfDropOverlay ? renderMacDropHint(S) : ''}
        </main>
      </div>

      ${renderShelfModals(S)}
    </div>
  `;
}

function renderSidebarGroup(title, key, S) {
  const closed = !!S.closedGroups[key];
  return `
    <button class="sidebar-group-title ${closed ? 'closed' : ''}" onclick="toggleGroup('${key}')">
      <span>${title}</span>
      <span class="chev" style="transform:rotate(${closed ? '0deg' : '90deg'})">${ICONS.chevRight}</span>
    </button>`;
}

function renderSidebarRow(tab, title, icon, count, S) {
  const on = S.shelfTab === tab;
  return `
    <button class="sidebar-row ${on ? 'active' : ''}" onclick="setShelfTab('${tab}')">
      <span class="ico">${icon}</span>
      <span class="truncate">${title}</span>
      <span class="cnt">${count}</span>
    </button>`;
}

function renderShelfPageContent(S, insetLeft) {
  if (S.shelfTab === 'books') return renderMacBooksView(S, insetLeft);
  if (S.shelfTab === 'podcasts') return S.podcastIssueOpen ? renderPodcastReaderView(S) : renderMacPodcastView(S, insetLeft);
  if (S.shelfTab === 'notes') return renderMacNotesView(S, insetLeft);
  if (S.shelfTab === 'words') return renderMacWordsView(S, insetLeft);
  if (S.shelfTab === 'collections') return renderMacCollectionsWall(S, insetLeft);

  let filtered = S.shelfEmptyDemo ? [] : [...BOOKS];
  let pageTitle = '书';
  const isAudioTab = S.shelfTab === 'audio';
  if (isAudioTab) {
    filtered = filtered.filter(b => b.hasAudio);
    pageTitle = '有声';
  } else if (S.shelfTab.startsWith('col:')) {
    const cid = S.shelfTab.slice(4);
    const col = COLLECTIONS.find(c => c.id === cid);
    pageTitle = col ? col.name : '合集';
    filtered = filtered.filter(b => (b.collections || []).includes(cid));
  }

  const showReading = S.shelfTab === 'all' || isAudioTab;
  const allReading = showReading ? filtered.filter(b => b.status === 'reading') : [];
  const readingShown = S.readingExpanded ? allReading : allReading.slice(0, 3);
  const cardedIds = new Set(readingShown.map(b => b.id));
  const restBooks = filtered.filter(b => !cardedIds.has(b.id));
  const showProgress = S.showShelfProgress !== false;

  return `
    <div style="flex:1;overflow-y:auto;padding-bottom:60px;" onclick="closeBookMenu()">
      <div class="shelf-page-header">
        <div style="padding-left:${insetLeft}px;font-size:22px;font-weight:700;color:var(--yy-t1);">${pageTitle}</div>
        <div style="display:flex;align-items:center;gap:8px;">
          ${isAudioTab ? `<button class="yy-pill sm" onclick="setModal('audiobookPair')">配对向导</button>` : ''}
          <button class="yy-pill" onclick="toggleImportCard()">
            ${ICONS.plus}<span>导入</span>
          </button>
        </div>
      </div>

      ${S.showImportCard ? renderMacImportCard(S) : ''}

      ${filtered.length === 0 ? `
        <!-- Empty Shelf State (Verified 1:1 against testMacShelfEmpty.1.png!) -->
        <div style="height:490px;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;padding:0 32px;">
          <div style="width:52px;height:52px;border-radius:14px;background:var(--yy-inset);color:var(--yy-t3);display:flex;align-items:center;justify-content:center;margin-bottom:14px;">
            ${ICONS.openBook.replace('width="14" height="14"', 'width="24" height="24"')}
          </div>
          <div style="font-size:16px;font-weight:600;color:var(--yy-t1);">书架还是空的</div>
          <div style="font-size:12.5px;color:var(--yy-t2);margin-top:6px;max-width:300px;line-height:1.6;">把 EPUB、PDF、MOBI、AZW3、TXT 或有声书拖进来，或者点下面导入。</div>
          <div style="margin-top:18px;display:flex;gap:8px;">
            <button class="yy-pill primary" onclick="toggleImportCard()">${ICONS.plus}<span>导入书籍</span></button>
            <button class="yy-pill" onclick="toggleShelfEmptyDemo(false)">恢复示例书库</button>
          </div>
        </div>
      ` : `
        ${readingShown.length > 0 ? `
          <div class="reading-band">
            <div style="display:flex;align-items:center;justify-content:space-between;padding:0 2px 10px;">
              <span style="font-size:12.5px;font-weight:600;color:var(--yy-t1);">在读</span>
              ${allReading.length > 3 ? `
                <button onclick="toggleReadingExpand()" style="font-size:11.5px;color:var(--yy-t2);display:inline-flex;align-items:center;gap:4px;">
                  <span>${S.readingExpanded ? '收起' : `全部 ${allReading.length} 本`}</span>
                  ${ICONS.chevDown}
                </button>
              ` : `<span style="font-size:11px;color:var(--yy-t3);" class="tabular">${allReading.length} 本</span>`}
            </div>
            <div class="reading-grid">
              ${readingShown.map((b, idx) => renderReadingCard(b, idx === 0)).join('')}
            </div>
          </div>
        ` : ''}

        <div style="padding:0 28px 0 22px;">
          <div style="display:flex;align-items:center;justify-content:space-between;padding:0 2px 14px;">
            <span style="font-size:12.5px;font-weight:600;color:var(--yy-t1);" class="tabular">
              ${readingShown.length > 0 ? `其余 ${restBooks.length} 本` : `${restBooks.length} 本`}
            </span>
            <button onclick="cycleShelfSort()" style="font-size:11.5px;color:var(--yy-t2);display:inline-flex;align-items:center;gap:4px;">
              <span>${S.shelfSort}</span>${ICONS.chevUpDown}
            </button>
          </div>

          <div class="shelf-books-grid">
            ${restBooks.map(b => `
              <div class="book-cell" style="cursor:pointer;" onclick="openBookByFormat('${b.id}')" oncontextmenu="event.preventDefault();openBookMenu('${b.id}', event.clientX, event.clientY)">
                <div style="margin-bottom:9px;">
                  ${renderBookCover(b, null, 'lift')}
                </div>
                <div class="truncate" style="font-size:11.5px;font-weight:600;color:var(--yy-t1);margin-bottom:2px;">${b.title}</div>
                ${showProgress ? `<div class="truncate tabular" style="font-size:10.5px;color:var(--yy-t3);">${shelfNoteFor(b, isAudioTab)}</div>` : ''}
              </div>
            `).join('')}
          </div>
        </div>
      `}
    </div>
  `;
}

// MacImportCard (MacImport.swift L8-160)
function renderMacImportCard(S) {
  return `
    <div style="padding:0 28px 18px 22px;">
      <div class="yy-card" style="border-radius:14px;padding:12px 16px 4px;box-shadow:0 1px 1px rgba(0,0,0,0.05),0 4px 8px rgba(0,0,0,0.04);">
        <div style="display:flex;align-items:center;gap:8px;">
          <span style="width:24px;height:24px;border-radius:7px;background:var(--tint-soft);color:var(--tint-accent);display:inline-flex;align-items:center;justify-content:center;">${ICONS.arrowDown}</span>
          <span style="font-size:12.5px;font-weight:600;color:var(--yy-t1);">正在导入 3 个文件</span>
          <span class="tabular" style="font-size:11px;color:var(--yy-t3);">第 2 / 3 个 · 还要几秒</span>
          <button onclick="toggleImportCard()" title="收起" style="margin-left:auto;width:22px;height:22px;border-radius:50%;background:var(--yy-inset);color:var(--yy-t2);display:inline-flex;align-items:center;justify-content:center;">
            ${ICONS.xmark.replace('width="11" height="11"', 'width="9" height="9"')}
          </button>
        </div>
        <div style="margin:10px 0 2px;height:3px;border-radius:99px;background:var(--yy-inset);overflow:hidden;">
          <div style="width:62%;height:100%;border-radius:99px;background:var(--tint-fill);"></div>
        </div>
        <div>
          <div style="padding:8px 0;display:flex;align-items:center;gap:12px;">
            ${renderBookCover(BOOKS[0], 24)}
            <div style="flex:1;min-width:0;">
              <div class="truncate" style="font-size:12px;font-weight:600;">红楼梦（人民文学出版社校注本）.epub</div>
              <div class="tabular" style="font-size:10.5px;color:var(--yy-t3);">曹雪芹 · 120 章</div>
            </div>
            <span style="font-size:11px;font-weight:600;color:var(--yy-ok);display:inline-flex;align-items:center;gap:4px;">${ICONS.check}<span>已导入</span></span>
          </div>
          <div style="height:0.5px;background:var(--yy-line);"></div>
          <div style="padding:8px 0;display:flex;align-items:center;gap:12px;">
            ${renderBookCover(BOOKS[7], 24)}
            <div style="flex:1;min-width:0;">
              <div class="truncate" style="font-size:12px;font-weight:600;">阴翳礼赞-谷崎润一郎.epub</div>
              <div style="font-size:10.5px;color:var(--yy-t2);">正在读目录和封面</div>
            </div>
            <div style="width:90px;height:3px;border-radius:99px;background:var(--yy-inset);overflow:hidden;"><div style="width:54%;height:100%;background:var(--tint-fill);"></div></div>
          </div>
          <div style="height:0.5px;background:var(--yy-line);"></div>
          <div style="padding:8px 0;display:flex;align-items:center;gap:12px;">
            ${renderBookCover(BOOKS[1], 24)}
            <div style="flex:1;min-width:0;">
              <div class="truncate" style="font-size:12px;font-weight:600;">The_Great_Gatsby.epub</div>
              <div style="font-size:10.5px;color:var(--yy-t2);">书库中已存在此书，已自动跳过</div>
            </div>
            <button onclick="toggleImportCard()" style="font-size:11px;font-weight:600;color:var(--tint-accent);display:inline-flex;align-items:center;gap:3px;"><span>在书架里显示</span>${ICONS.chevRight}</button>
          </div>
        </div>
      </div>
    </div>
  `;
}

// MacDropHint (MacImport.swift L170-240: Full-shelf dashed drag overlay)
function renderMacDropHint(S) {
  return `
    <div onclick="toggleShelfDropOverlay()" style="position:absolute;inset:0;background:rgba(248,245,240,0.88);backdrop-filter:blur(4px);z-index:45;padding:12px 12px 12px 10px;display:flex;cursor:pointer;">
      <div style="flex:1;border-radius:18px;border:2px dashed var(--tint-accent);background:rgba(138,101,63,0.03);display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;padding:24px;">
        <div style="width:64px;height:64px;border-radius:50%;background:var(--tint-soft);color:var(--tint-accent);display:flex;align-items:center;justify-content:center;margin-bottom:16px;">
          ${ICONS.arrowDown.replace('width="13" height="13"', 'width="26" height="26"')}
        </div>
        <div style="font-size:20px;font-weight:700;color:var(--yy-t1);">松开即可导入书库</div>
        <div style="font-size:12.5px;color:var(--yy-t2);margin:6px 0 20px;">放进“书”。书名、作者和封面从文件里读出来，之后都能改</div>
        <div class="yy-card" style="border-radius:14px;padding:14px 20px 14px 16px;display:flex;flex-direction:column;gap:9px;box-shadow:0 4px 12px rgba(0,0,0,0.05);">
          <div style="display:flex;align-items:center;gap:6px;">
            <span style="width:46px;text-align:right;font-size:11px;font-weight:600;color:var(--yy-t2);padding-right:6px;">书</span>
            ${['EPUB', 'TXT', 'PDF', 'MOBI', 'AZW3'].map(ext => `<span class="mono" style="padding:4px 7px;border-radius:5px;background:var(--yy-inset);font-size:10.5px;font-weight:500;color:var(--yy-t2);">${ext}</span>`).join('')}
          </div>
          <div style="display:flex;align-items:center;gap:6px;">
            <span style="width:46px;text-align:right;font-size:11px;font-weight:600;color:var(--yy-t2);padding-right:6px;">有声书</span>
            ${['M4B', 'MP3'].map(ext => `<span class="mono" style="padding:4px 7px;border-radius:5px;background:var(--yy-inset);font-size:10.5px;font-weight:500;color:var(--yy-t2);">${ext}</span>`).join('')}
          </div>
        </div>
        <div style="margin-top:16px;display:inline-flex;align-items:center;gap:6px;font-size:11.5px;color:var(--yy-t2);">
          <span style="color:var(--yy-ok);">${ICONS.check}</span>
          <span>同一个文件只导入一次：按内容认，改过名字也认得出来</span>
        </div>
      </div>
    </div>
  `;
}

function renderReadingCard(b, tinted) {
  const isAudio = b.format === 'audio';
  const kindLabel = isAudio ? '有声' : (b.format === 'pdf' ? 'PDF' : '图书');
  const bgStyle = tinted ? `background:${b.leadHue || '#5C4438'};` : '';
  return `
    <div class="reading-card ${tinted ? 'tinted' : ''}" style="${bgStyle}" onclick="openBookByFormat('${b.id}')">
      <div style="width:46px;flex-shrink:0;">
        ${renderBookCover(b, 46)}
      </div>
      <div style="flex:1;min-width:0;display:flex;flex-direction:column;gap:2.5px;">
        <div class="truncate" style="font-size:13.5px;font-weight:600;">${b.title}</div>
        <div class="truncate" style="font-size:11.5px;color:${tinted ? 'rgba(255,255,255,0.78)' : 'var(--yy-t2)'};">${b.author}</div>
        <div class="truncate tabular" style="font-size:11px;margin-top:1px;color:${tinted ? 'rgba(255,255,255,0.74)' : 'var(--yy-t3)'};">
          ${kindLabel} · <span style="font-weight:600;color:${tinted ? '#FFFFFF' : 'var(--tint-accent)'};">${b.pct}</span> · ${b.remaining}
        </div>
      </div>
      <div style="display:flex;align-items:center;gap:2px;z-index:2;" onclick="event.stopPropagation()">
        <button class="rc-icon-btn" title="直接朗读 / 收听" onclick="openBookListen('${b.id}')">${ICONS.headphones}</button>
        <button class="rc-icon-btn" title="更多操作" onclick="openBookMenu('${b.id}', event.clientX, event.clientY)">${ICONS.dots}</button>
      </div>
      <div class="reading-progress-hair">
        <div class="reading-progress-fill" style="width:${Math.max(8, Math.round(b.progress * 100))}%"></div>
      </div>
    </div>
  `;
}

// ============================================================================
// 2. MacBooksView (进度) + BookRecordPage (书籍档案页)
// ============================================================================
function renderMacBooksView(S, insetLeft) {
  if (S.recordBookId) {
    const book = BOOKS.find(b => b.id === S.recordBookId) || BOOKS[0];
    return renderBookRecordPage(book, S, insetLeft);
  }
  const statuses = [
    { id: 'reading', name: '在读' },
    { id: 'want', name: '想读' },
    { id: 'finished', name: '读过' },
    { id: 'paused', name: '搁置' },
    { id: 'dropped', name: '弃读' }
  ];
  const curStatus = S.booksStatus || 'reading';
  const list = BOOKS.filter(b => b.status === curStatus);

  return `
    <div style="flex:1;overflow-y:auto;padding-bottom:40px;position:relative;" onclick="closeChapterJump()">
      <div class="shelf-page-header">
        <div style="padding-left:${insetLeft}px;display:flex;align-items:baseline;gap:12px;">
          <span style="font-size:22px;font-weight:700;">进度</span>
          <span class="tabular" style="font-size:12px;color:var(--yy-t2);">${BOOKS.length} 本</span>
        </div>
        <button class="yy-pill" onclick="setModal('addRecordBook')">${ICONS.plus}<span>加书</span></button>
      </div>

      <div style="padding:0 28px 0 22px;display:flex;align-items:center;justify-content:space-between;">
        <div style="display:flex;align-items:center;gap:2px;">
          ${statuses.map(st => {
            const cnt = BOOKS.filter(b => b.status === st.id).length;
            const on = curStatus === st.id;
            return `
              <button onclick="setBooksStatus('${st.id}')" style="height:28px;padding:0 10px;border-radius:8px;background:${on ? 'var(--yy-desk-on)' : 'transparent'};display:inline-flex;align-items:center;gap:6px;">
                <span style="font-size:12.5px;font-weight:${on ? '600' : '400'};color:var(--yy-t1);">${st.name}</span>
                <span class="tabular" style="font-size:10.5px;color:var(--yy-t3);">${cnt}</span>
              </button>`;
          }).join('')}
        </div>
        <span style="font-size:11px;color:var(--yy-t3);display:inline-flex;align-items:center;gap:4px;">最近读过 ${ICONS.chevUpDown}</span>
      </div>

      ${list.length === 0 ? `
        <div style="height:320px;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:10px;color:var(--yy-t2);">
          <div style="font-size:13px;">没有${statuses.find(s => s.id === curStatus)?.name || ''}的书</div>
        </div>
      ` : `
        <div style="padding:16px 28px 28px 22px;display:grid;grid-template-columns:repeat(auto-fill,minmax(294px,1fr));gap:12px;">
          ${list.map(b => renderBookRecordTile(b, S)).join('')}
        </div>
      `}

      ${S.chapterJumpBookId ? renderChapterJumpPopover(BOOKS.find(b => b.id === S.chapterJumpBookId) || BOOKS[0]) : ''}
    </div>
  `;
}

function renderBookRecordTile(b) {
  return `
    <div class="yy-card" onclick="openRecordBook('${b.id}')" style="min-height:94px;padding:11px 10px 11px 11px;border-radius:14px;display:flex;align-items:center;gap:12px;cursor:pointer;transition:transform 0.14s,box-shadow 0.14s;">
      <div style="width:48px;flex-shrink:0;">${renderBookCover(b, 48)}</div>
      <div style="flex:1;min-width:0;min-height:72px;display:flex;flex-direction:column;justify-content:space-between;">
        <div>
          <div class="serif truncate" style="font-size:14px;font-weight:700;color:var(--yy-t1);">${b.title}</div>
          <div class="truncate" style="font-size:11.5px;color:var(--yy-t2);margin-top:2px;">${b.author} · ${b.openedAgo}</div>
        </div>
        ${b.status === 'finished' ? `
          <div>
            <div style="font-size:11px;color:var(--yy-t3);"><span style="color:var(--yy-t1);letter-spacing:1px;">★★★★★</span> · ${b.openedAgo}读完</div>
            ${b.comment ? `<div class="serif truncate" style="font-size:11px;color:var(--yy-t2);margin-top:2px;">${b.comment}</div>` : ''}
          </div>
        ` : b.status === 'want' ? `
          <div style="font-size:11px;color:var(--yy-t3);">共 ${b.totalChapters} 章 · ${b.remaining}</div>
        ` : `
          <div style="display:flex;flex-direction:column;gap:5px;">
            <div class="tabular truncate" style="font-size:11px;">
              <span style="color:var(--tint-accent);font-weight:600;">读到 第 ${b.chapterIdx} 章</span>
              <span style="color:var(--yy-t3);"> · ${b.remaining}</span>
            </div>
            <div style="display:flex;gap:2px;max-width:168px;height:3.5px;">
              ${Array.from({ length: 12 }).map((_, i) => {
                const read = i < Math.round(b.progress * 12);
                return `<span style="flex:1;border-radius:2px;background:${read ? 'var(--tint-fill)' : 'var(--yy-inset)'};"></span>`;
              }).join('')}
            </div>
          </div>
        `}
      </div>
      <div style="min-height:72px;display:flex;flex-direction:column;justify-content:space-between;align-items:flex-end;" onclick="event.stopPropagation()">
        <button onclick="openBookMenu('${b.id}', event.clientX, event.clientY)" style="width:34px;height:24px;border-radius:7px;display:flex;align-items:center;justify-content:center;gap:2px;color:var(--yy-t3);" title="更改状态">
          ${ICONS.protoBook}${ICONS.chevDown}
        </button>
        <div style="display:flex;align-items:center;gap:4px;">
          <button onclick="toggleChapterJump('${b.id}')" title="快速跳章目录" style="width:24px;height:24px;border-radius:7px;background:var(--yy-card);border:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:center;color:var(--yy-t2);">
            ${ICONS.list}
          </button>
          <button onclick="openBookByFormat('${b.id}')" style="height:24px;padding:0 9px;border-radius:7px;background:var(--tint-black);border:0.5px solid var(--tint-edge);color:var(--tint-on-black);font-size:11px;font-weight:600;">
            ${b.progress > 0 ? '接着读' : '开始读'}
          </button>
        </div>
      </div>
    </div>
  `;
}

function renderChapterJumpPopover(b) {
  return `
    <div class="yy-float-card" onclick="event.stopPropagation()" style="position:absolute;top:112px;left:240px;width:360px;height:420px;z-index:80;display:flex;flex-direction:column;overflow:hidden;">
      <div style="padding:14px 16px 0;display:flex;align-items:baseline;gap:8px;">
        <span style="font-size:15px;font-weight:600;">目录</span>
        <span class="truncate" style="font-size:11.5px;color:var(--yy-t3);">${b.title}</span>
        <span class="tabular" style="margin-left:auto;font-size:11px;color:var(--yy-t3);">共 ${b.totalChapters} 章</span>
      </div>
      <div style="margin:10px 14px 0;height:30px;padding:0 10px;border-radius:8px;background:var(--yy-inset);display:flex;align-items:center;gap:6px;color:var(--yy-t3);">
        ${ICONS.search}<span style="font-size:12px;">第几章，或章名里的字</span>
      </div>
      <div style="height:0.5px;background:var(--yy-line);margin-top:10px;"></div>
      <div style="flex:1;overflow-y:auto;padding:6px 8px;">
        ${['第一回 · 甄士隐梦幻识通灵', '第五回 · 游幻境指迷十二钗', '第七回 · 送宫花贾琏戏熙凤', '第八回 · 比通灵金莺微露意', '第十七回 · 大观园试才题对额', '第二十三回 · 西厢记妙词通戏语'].map((ch, idx) => {
          const cur = idx === 3;
          const past = idx < 3;
          return `
            <div onclick="openBookByFormat('${b.id}')" style="padding:${cur ? '11px 8px 12px' : '9px 8px'};border-radius:11px;background:${cur ? 'var(--tint-soft)' : 'transparent'};display:flex;align-items:baseline;gap:6px;cursor:pointer;">
              <span class="tabular" style="width:52px;font-size:11px;color:${cur ? 'var(--tint-accent)' : (past ? 'var(--yy-t3)' : 'var(--yy-t2)')};font-weight:${cur ? '600' : '400'};">第 ${[1,5,7,8,17,23][idx]} 回</span>
              <div style="flex:1;min-width:0;">
                <div class="truncate" style="font-size:${cur ? '12.5px' : '12px'};font-weight:${cur ? '600' : '400'};">${ch.split(' · ')[1]}</div>
                <div style="font-size:10.5px;color:${cur ? 'var(--tint-accent)' : 'var(--yy-t3)'};margin-top:2px;">
                  ${cur ? '读到 34% · 还剩 9 分钟' : (past ? '翻过' : '约 18 分钟')}
                </div>
              </div>
            </div>`;
        }).join('')}
      </div>
      <div style="height:0.5px;background:var(--yy-line);"></div>
      <div style="height:38px;padding:0 12px 0 16px;display:flex;align-items:center;justify-content:space-between;font-size:11px;">
        <span style="color:var(--yy-t3);">读过 7 章</span>
        <span style="color:var(--tint-accent);font-weight:500;">读到的地方 · 第 8 回</span>
      </div>
    </div>
  `;
}

function renderBookRecordPage(b, S, insetLeft) {
  const tab = S.recordSubTab || 'contents';
  const asGrid = !!S.recordChaptersAsGrid;
  return `
    <div style="flex:1;overflow-y:auto;padding-bottom:48px;">
      <div class="shelf-page-header">
        <button onclick="openRecordBook(null)" style="padding-left:${insetLeft}px;display:inline-flex;align-items:center;gap:6px;font-size:13px;color:var(--yy-t2);">
          ${ICONS.chevLeft}<span>进度</span>
        </button>
      </div>

      <div style="max-width:760px;padding:0 28px 0 22px;">
        <div style="display:flex;align-items:flex-start;gap:26px;">
          <div style="width:120px;flex-shrink:0;">${renderBookCover(b, 120)}</div>
          <div style="flex:1;min-height:172px;display:flex;flex-direction:column;">
            <div class="serif" style="font-size:25px;font-weight:700;color:var(--yy-t1);line-height:1.3;">${b.title}</div>
            <div style="font-size:13px;color:var(--yy-t2);margin-top:4px;">${b.author}</div>
            <div style="font-size:11.5px;color:var(--yy-t3);margin-top:3px;">${b.format.toUpperCase()} · ${b.totalChapters} 章 · ${b.remaining}</div>

            <div style="display:flex;align-items:center;gap:10px;margin-top:12px;">
              <span style="font-size:11px;color:var(--yy-t3);">你的评分</span>
              <div style="display:flex;gap:3px;font-size:15px;">
                ${[1,2,3,4,5].map(n => `<span style="color:${n <= (b.rating || 4) ? 'var(--yy-t1)' : 'var(--yy-record-star)'};">★</span>`).join('')}
              </div>
            </div>

            <div style="margin-top:auto;padding-top:16px;display:flex;align-items:center;gap:8px;flex-wrap:wrap;">
              <button class="yy-rec-btn main" onclick="openBookByFormat('${b.id}')">接着读 · 第 ${b.chapterIdx} 章</button>
              <button class="yy-rec-btn" onclick="openBookMenu('${b.id}', event.clientX, event.clientY)">
                ${ICONS.protoBook}<span>在读</span>${ICONS.chevDown}
              </button>
              <span style="font-size:11.5px;color:var(--yy-t3);padding-left:6px;">10月28日开始 · 读了 6 小时 · ${b.remaining}</span>
            </div>
          </div>
        </div>

        <div style="margin-top:30px;margin-left:-8px;display:flex;align-items:center;justify-content:space-between;">
          <div style="display:flex;align-items:center;gap:2px;">
            ${[
              { id: 'contents', label: '目录', ico: ICONS.openBook, cnt: b.totalChapters },
              { id: 'about', label: '简介', ico: ICONS.noteCard },
              { id: 'notes', label: '笔记', ico: ICONS.highlighter, cnt: 2 },
              { id: 'log', label: '记录', ico: ICONS.history, cnt: 4 },
              { id: 'podcast', label: '播客', ico: ICONS.headphones, cnt: 2 }
            ].map(t => {
              const on = tab === t.id;
              return `
                <button onclick="setRecordSubTab('${t.id}')" style="height:28px;padding:0 9px;border-radius:8px;background:${on ? 'var(--yy-desk-on)' : 'transparent'};display:inline-flex;align-items:center;gap:7px;">
                  <span style="color:${on ? 'var(--yy-t1)' : 'var(--yy-t2)'};display:flex;">${t.ico}</span>
                  <span style="font-size:12px;font-weight:${on ? '600' : '400'};">${t.label}</span>
                  ${t.cnt ? `<span class="tabular" style="font-size:10.5px;color:var(--yy-t3);">${t.cnt}</span>` : ''}
                </button>`;
            }).join('')}
          </div>

          <div style="display:flex;align-items:center;gap:4px;">
            ${tab === 'contents' ? `
              <span class="tabular" style="font-size:11px;color:var(--yy-t3);padding-right:6px;">读到第 ${b.chapterIdx} 章 · ${b.pct}</span>
              <button onclick="toggleRecordGrid(false)" style="width:26px;height:26px;border-radius:7px;background:${!asGrid ? 'var(--yy-desk-on)' : 'transparent'};display:flex;align-items:center;justify-content:center;" title="列表">${ICONS.list}</button>
              <button onclick="toggleRecordGrid(true)" style="width:26px;height:26px;border-radius:7px;background:${asGrid ? 'var(--yy-desk-on)' : 'transparent'};display:flex;align-items:center;justify-content:center;" title="章节方格">${ICONS.grid}</button>
            ` : ''}
            <button class="pod-paper-pill" onclick="setModal('podcastStudio')" style="margin-left:6px;">${ICONS.radio}<span>书房电台</span></button>
          </div>
        </div>

        <div style="margin-top:14px;">
          ${tab === 'contents' ? (asGrid ? `
            <div>
              <div style="display:grid;grid-template-columns:repeat(auto-fill,30px);gap:6px;">
                ${Array.from({ length: Math.min(48, b.totalChapters) }).map((_, i) => {
                  const n = i + 1;
                  const cur = n === b.chapterIdx;
                  const read = n < b.chapterIdx;
                  return `<button onclick="openBookByFormat('${b.id}')" title="第 ${n} 章" class="tabular" style="width:30px;height:30px;border-radius:7px;font-size:11px;font-weight:${cur ? '700' : '400'};background:${cur ? 'var(--tint-fill)' : (read ? 'var(--tint-black)' : 'var(--yy-card)')};color:${cur ? '#FFF' : (read ? 'var(--tint-accent)' : 'var(--yy-t3)')};border:${cur ? 'none' : `0.5px solid ${read ? 'var(--tint-edge)' : 'var(--yy-line)'}`};">${n}</button>`;
                }).join('')}
              </div>
              <div style="display:flex;gap:16px;margin-top:14px;font-size:11px;color:var(--yy-t3);">
                <span style="display:inline-flex;align-items:center;gap:5px;"><i style="width:12px;height:12px;border-radius:3px;background:var(--tint-black);border:0.5px solid var(--tint-edge);"></i>读过</span>
                <span style="display:inline-flex;align-items:center;gap:5px;"><i style="width:12px;height:12px;border-radius:3px;background:var(--tint-fill);"></i>读到</span>
                <span style="display:inline-flex;align-items:center;gap:5px;"><i style="width:12px;height:12px;border-radius:3px;background:var(--yy-card);border:0.5px solid var(--yy-line);"></i>没读</span>
              </div>
            </div>
          ` : `
            <div style="padding-left:26px;">
              ${[
                { n: 6, t: '贾宝玉初试云雨情　刘姥姥一进荣国府', s: 'read', r: '读过' },
                { n: 7, t: '送宫花贾琏戏熙凤　宴宁府宝玉会秦钟', s: 'read', r: '读过' },
                { n: 8, t: '比通灵金莺微露意　探宝钗黛玉半含酸', s: 'cur', r: '读到 34%' },
                { n: 9, t: '恋风流情友入家塾　起嫌疑顽童闹学堂', s: 'todo', r: '18 分钟' },
                { n: 10, t: '金寡妇贪利权受辱　张太医论病细穷源', s: 'todo', r: '16 分钟' },
                { n: 11, t: '庆寿辰宁府排家宴　见熙凤贾瑞起淫心', s: 'todo', r: '19 分钟' }
              ].map(row => `
                <div onclick="openBookByFormat('${b.id}')" style="height:31px;display:flex;align-items:center;gap:12px;position:relative;cursor:pointer;">
                  ${row.s === 'read' ? `<span style="position:absolute;left:-24px;color:var(--tint-fill);">${ICONS.check}</span>` : ''}
                  ${row.s === 'cur' ? `<span style="position:absolute;left:-14px;width:2.5px;height:13px;border-radius:99px;background:var(--tint-accent);"></span>` : ''}
                  <span class="tabular" style="width:22px;text-align:right;font-size:11px;color:${row.s === 'cur' ? 'var(--tint-accent)' : 'var(--yy-record-faint)'};font-weight:${row.s === 'cur' ? '600' : '400'};">${row.n}</span>
                  <span class="serif truncate" style="font-size:13.5px;font-weight:${row.s === 'cur' ? '700' : '400'};color:${row.s === 'read' ? 'var(--yy-record-faint)' : 'var(--yy-t1)'};">${row.t}</span>
                  <span style="flex:1;border-bottom:1.2px dotted var(--yy-record-leader);height:1px;transform:translateY(2px);"></span>
                  <span class="tabular" style="font-size:11px;font-weight:${row.s === 'cur' ? '600' : '400'};color:${row.s === 'cur' ? 'var(--tint-accent)' : 'var(--yy-record-faint)'};">${row.r}</span>
                </div>
              `).join('')}
            </div>
          `) : tab === 'notes' ? `
            <div>
              <div style="display:flex;justify-content:space-between;font-size:12.5px;"><strong style="color:var(--yy-t1);">短评</strong><span style="color:var(--yy-t3);">只有你看得到</span></div>
              <div class="serif" style="margin-top:8px;padding:10px 12px;background:var(--yy-card);border-radius:12px;border:0.5px solid var(--yy-line);font-size:13.5px;line-height:1.6;">${b.comment || '写一句（可以不写）'}</div>
              <div style="display:flex;gap:6px;margin-top:12px;">
                ${(b.tags || ['古典文学']).map(tg => `<span style="height:26px;padding:0 9px;border-radius:7px;background:var(--yy-pill);border:0.5px solid var(--yy-pill-line);display:inline-flex;align-items:center;font-size:12px;">${tg}</span>`).join('')}
                <span style="height:26px;padding:0 9px;border-radius:7px;border:0.5px solid var(--yy-pill-line);display:inline-flex;align-items:center;gap:3px;font-size:12px;color:var(--yy-t3);">+ 标签</span>
              </div>
            </div>
          ` : tab === 'log' ? `
            <div class="yy-card" style="padding:4px 16px;">
              ${[
                { d: '今天 09:12', t: '读了 38 分钟 · 第七回 → 第八回 (34%)' },
                { d: '昨天 22:05', t: '收听有声书 24 分钟 · 第六回' },
                { d: '11 月 20 日', t: '记下 2 条划线与 1 条想法' },
                { d: '10 月 28 日', t: '导入书库并标为「在读」' }
              ].map((l, i) => `
                ${i > 0 ? `<div style="height:0.5px;background:var(--yy-line);"></div>` : ''}
                <div style="height:40px;display:flex;align-items:center;justify-content:space-between;font-size:12.5px;">
                  <span>${l.t}</span>
                  <span class="tabular" style="font-size:11px;color:var(--yy-t3);">${l.d}</span>
                </div>
              `).join('')}
            </div>
          ` : tab === 'podcast' ? `
            <div class="yy-stationery-card" style="padding:13px 16px 10px;">
              <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px;">
                <div><span class="serif" style="font-size:15px;">关联播客提及</span> <span style="font-size:11.5px;color:var(--yy-t3);">2 处原声锚点 · 防剧透锁</span></div>
                <button class="pod-paper-pill accent" onclick="setModal('podcastStudio')">+ 酿一期</button>
              </div>
              <div onclick="openPodcastReader()" style="padding:10px;border-radius:9px;background:var(--tint-wash);display:flex;align-items:center;gap:11px;cursor:pointer;">
                <span style="color:var(--tint-accent);">${ICONS.play}</span>
                <div style="flex:1;"><div style="font-size:13.5px;">本周书房周刊：金玉良缘与木石前盟的初次交锋</div><div style="font-size:11.5px;color:var(--yy-t3);">书房电台 · 仅覆盖至《${b.title}》第八回</div></div>
                <span class="pod-paper-pill accent">跳到 04:18</span>
              </div>
            </div>
          ` : `
            <div class="serif" style="font-size:13.5px;line-height:1.85;color:var(--yy-t1);">
              中国古典长篇世情小说巅峰之作，以贾、史、王、薛四大家族兴衰为背景，借宝黛钗悲剧写尽人情冷暖与生命幻灭。
            </div>
          `}
        </div>
      </div>
    </div>
  `;
}

// ============================================================================
// 3. MacPodcastView + Right-Edge 2-Button -> 2-Tab-Pair Drawer + PodcastReaderView
// ============================================================================
function renderPodcastRightEdgeTriggers(S) {
  if (S.podDrawerTab) return '';
  return `
    <div style="position:absolute;right:12px;top:46%;transform:translateY(-50%);display:flex;flex-direction:column;gap:10px;z-index:35;">
      <div class="pod-edge-wrap">
        <span class="pod-edge-tip">原声书摘 · 稍后听</span>
        <button class="pod-circle-btn" style="width:36px;height:36px;" onclick="openPodcastDrawer('clips')">
          ${POD_GLYPHS.bookmark(15.5)}
          ${!S.podClipDotCleared ? `<span style="position:absolute;top:5px;right:5px;width:6px;height:6px;border-radius:50%;background:var(--tint-accent);box-shadow:0 0 0 1.2px var(--yy-card);"></span>` : ''}
        </button>
      </div>
      <div class="pod-edge-wrap">
        <span class="pod-edge-tip">声音卡 · 本机整理</span>
        <button class="pod-circle-btn" style="width:36px;height:36px;" onclick="openPodcastDrawer('voices')">
          ${POD_GLYPHS.waveform(15.5)}
        </button>
      </div>
    </div>
  `;
}

function renderMacPodcastView(S) {
  const showFilter = S.podFilter || 'all';
  return `
    <div style="flex:1;position:relative;overflow:hidden;display:flex;flex-direction:column;" onclick="if(STATE.podCmdDrop) togglePodCmdDrop(false)">
      <!-- Top Bar: Recessed Matte Bookplate Tray + Right Refresh/Add (100% aligned with 60px right gutter!) -->
      <div style="height:52px;margin-top:8px;margin-bottom:10px;padding:0 60px 0 52px;display:flex;align-items:center;justify-content:space-between;flex-shrink:0;">
        <div style="display:inline-flex;align-items:center;gap:3px;padding:3px;background:var(--yy-inset);border-radius:11px;border:0.5px solid var(--yy-line);">
          <button onclick="setPodFilter('all')" title="全部节目" style="width:30px;height:28px;border-radius:8px;background:${showFilter === 'all' ? 'var(--yy-card)' : 'transparent'};color:${showFilter === 'all' ? 'var(--tint-accent)' : 'var(--yy-t3)'};display:flex;align-items:center;justify-content:center;box-shadow:${showFilter === 'all' ? '0 1px 2px rgba(0,0,0,0.06)' : 'none'};">
            ${POD_GLYPHS.allGrid(15)}
          </button>
          ${[
            { id: 'studio', title: '书房电台', studio: true },
            { id: 'gushi', title: '故事FM', studio: false },
            { id: 'lex', title: 'Lex Fridman', studio: false }
          ].map(sh => {
            const on = showFilter === sh.id;
            return `
              <button onclick="setPodFilter('${sh.id}')" style="min-width:30px;height:28px;padding:0 ${on ? '9px' : '5.5px'};border-radius:8px;background:${on ? 'var(--yy-card)' : 'transparent'};display:inline-flex;align-items:center;gap:6px;box-shadow:${on ? '0 1px 2px rgba(0,0,0,0.06)' : 'none'};">
                ${renderPodcastShowMark(sh.title, sh.studio, 19)}
                ${on ? `<span style="font-size:11.5px;font-weight:600;">${sh.title}</span>` : ''}
              </button>`;
          }).join('')}
        </div>

        <div style="display:flex;align-items:center;gap:7px;">
          <button class="pod-circle-btn" style="width:32px;height:32px;" title="刷新全部已订阅频道" onclick="showProtoToast('已刷新 2 档播客订阅')">${POD_GLYPHS.refresh(14.5)}</button>
          <button class="pod-circle-btn" style="width:32px;height:32px;" title="添加节目 · 粘贴链接 / OPML / 本地音频" onclick="setModal('podcastAdd')">${POD_GLYPHS.plus(14.5)}</button>
        </div>
      </div>

      <!-- Asymmetric 2-Column Workspace: Left Station + Right Literary Desk -->
      <div style="flex:1;display:flex;gap:36px;padding:0 60px 0 52px;overflow:hidden;">
        <!-- Left Station Column -->
        <div style="flex:1.15;overflow-y:auto;padding-bottom:36px;display:flex;flex-direction:column;gap:12px;">
          <div style="height:28px;display:flex;align-items:center;justify-content:space-between;">
            <div style="display:flex;align-items:baseline;gap:9px;">
              <span class="serif" style="font-size:16px;color:var(--yy-t1);">收听台</span>
              <span class="tabular" style="font-size:11.5px;color:var(--yy-t3);">3 组 · 5 期</span>
            </div>
            <button class="pod-paper-pill"><span>全部折叠</span>${POD_GLYPHS.chevronDown(11)}</button>
          </div>

          <!-- Group 1: 正在听 -->
          <div class="yy-stationery-card" style="padding:13px 16px 8px;">
            <div style="display:flex;align-items:center;gap:8px;padding-bottom:6px;">
              <span class="serif" style="font-size:15px;">正在听</span>
              <span style="font-size:11.5px;color:var(--yy-t3);">1 期未完</span>
              <button class="pod-paper-pill" style="margin-left:auto;" onclick="openPodcastReader()">接着听</button>
              <span style="color:var(--yy-t3);">${POD_GLYPHS.chevronDown(12)}</span>
            </div>
            <div onclick="openPodcastReader()" style="padding:10px;margin:0 -10px;border-radius:9px;background:var(--tint-wash);display:flex;align-items:center;gap:11px;cursor:pointer;">
              <span class="pod-eq"><i></i><i></i><i></i></span>
              ${renderPodcastShowMark('Lex Fridman', false, 22)}
              <div style="flex:1;min-width:0;">
                <div class="truncate" style="font-size:13.5px;font-weight:500;color:var(--tint-accent);">#452 François Chollet: ARC-AGI & Fluid Intelligence</div>
                <div class="truncate" style="font-size:11.5px;color:var(--yy-t3);">Lex Fridman · <span style="color:var(--tint-accent);">原声线中文</span> · 还剩 28 分</div>
                <div style="margin-top:5px;width:108px;height:2px;border-radius:99px;background:var(--yy-inset);overflow:hidden;"><div style="width:38%;height:100%;background:var(--tint-accent);"></div></div>
              </div>
              <span style="color:var(--tint-accent);" title="展开访谈书页">${POD_GLYPHS.transcriptLines(14.5)}</span>
            </div>
          </div>

          <!-- Group 2: 书房电台 -->
          <div class="yy-stationery-card" style="padding:13px 16px 8px;">
            <div style="display:flex;align-items:center;gap:8px;padding-bottom:6px;">
              <span class="serif" style="font-size:15px;">书房电台</span>
              <span style="font-size:11.5px;color:var(--yy-t3);">由你的书架酿制 · 防剧透锁</span>
              <button class="pod-paper-pill accent" style="margin-left:auto;" onclick="setModal('podcastStudio')">+ 酿一期</button>
              <span style="color:var(--yy-t3);">${POD_GLYPHS.chevronDown(12)}</span>
            </div>
            ${[
              { t: '本周书房周刊：重读《红楼梦》第八回与近期划线', badge: '周', bg: 'var(--tint-soft)', fg: 'var(--tint-accent)', tag: '书房周刊 · 防剧透', meta: '11 分 · 今天' },
              { t: '跨书圆桌：《阴翳礼赞》与大观园里的东方光影', badge: '桌', bg: '#E8EFE5', fg: '#466041', tag: '跨书圆桌 · 防剧透', meta: '15 分 · 昨天' },
              { t: '难书导读：《On the Measure of Intelligence》核心直觉', badge: '导', bg: '#E6EDF4', fg: '#2D4A6E', tag: '难书导读 · 防剧透', meta: '12 分 · 3 天前' }
            ].map((ep, i) => `
              ${i > 0 ? `<div style="height:0.5px;background:var(--yy-soft-div);"></div>` : ''}
              <div onclick="openPodcastReader()" style="padding:10px 0;display:flex;align-items:center;gap:11px;cursor:pointer;">
                <span class="serif" style="width:24px;height:24px;border-radius:7px;background:${ep.bg};color:${ep.fg};font-size:11px;font-weight:600;display:inline-flex;align-items:center;justify-content:center;flex-shrink:0;">${ep.badge}</span>
                <div style="flex:1;min-width:0;">
                  <div class="truncate" style="font-size:13.5px;">${ep.t}</div>
                  <div style="font-size:11.5px;color:var(--yy-t3);"><span style="color:var(--tint-accent);">${ep.tag}</span> · ${ep.meta}</div>
                </div>
                <span style="color:var(--yy-t3);opacity:0.55;">${POD_GLYPHS.bookmark(14)}</span>
              </div>
            `).join('')}
          </div>

          <!-- Group 3: 精选播客 -->
          <div class="yy-stationery-card" style="padding:13px 16px 8px;">
            <div style="display:flex;align-items:center;gap:8px;padding-bottom:6px;">
              <span class="serif" style="font-size:15px;">精选播客</span>
              <span style="font-size:11.5px;color:var(--yy-t3);">公开目录 · 一键订阅</span>
              <button class="pod-paper-pill" style="margin-left:auto;" onclick="setModal('podcastAdd')">搜更多</button>
            </div>
            ${[
              { t: '忽左忽右', d: 'JustPod · 文化与历史访谈' },
              { t: '声东击西', d: 'ETW · 科技与人文观察' }
            ].map((p, i) => `
              ${i > 0 ? `<div style="height:0.5px;background:var(--yy-soft-div);"></div>` : ''}
              <div style="padding:9px 0;display:flex;align-items:center;gap:11px;">
                ${renderPodcastShowMark(p.t, false, 22)}
                <div style="flex:1;min-width:0;">
                  <div style="font-size:13px;font-weight:500;">${p.t}</div>
                  <div style="font-size:11.5px;color:var(--yy-t3);">${p.d}</div>
                </div>
                <button class="pod-paper-pill accent" style="height:24px;padding:0 10px;" onclick="showProtoToast('已订阅《${p.t}》')">订阅</button>
              </div>
            `).join('')}
          </div>
        </div>

        <!-- Right Literary Listening Desk -->
        <div style="width:396px;flex-shrink:0;overflow-y:auto;padding-bottom:36px;position:relative;">
          <div style="display:flex;align-items:baseline;gap:11px;">
            <span class="serif" style="font-size:30px;color:var(--yy-t1);">上午好</span>
            <span style="display:inline-flex;align-items:center;gap:6px;font-size:12px;color:var(--yy-t3);">
              <i style="width:4px;height:4px;border-radius:50%;background:var(--tint-accent);"></i>
              十一月廿三日 · 星期一
            </span>
          </div>

          <button onclick="openPodcastDrawer('clips')" style="margin-top:10px;text-align:left;line-height:1.65;">
            <span class="serif" style="font-size:13.5px;color:var(--yy-t2);">「${POD_CLIPS[0].quote}」</span>
            <span class="tabular" style="font-size:11.5px;color:var(--yy-t3);margin-left:6px;">${POD_CLIPS[0].show} · ${POD_CLIPS[0].clock}</span>
          </button>

          <!-- Recessed Matte Command Search Bar + commandDropdownCard -->
          <div style="position:relative;margin-top:16px;" onclick="event.stopPropagation()">
            <div onclick="togglePodCmdDrop()" style="height:40px;padding:0 12px;border-radius:11px;background:var(--yy-inset);border:0.5px solid ${S.podCmdDrop ? 'var(--tint-edge)' : 'var(--yy-line)'};display:flex;align-items:center;gap:9px;cursor:text;">
              <span style="color:var(--yy-t3);">${ICONS.search}</span>
              <span style="font-size:13px;color:var(--yy-t3);flex:1;">搜节目、听过的句子，或粘贴单集链接…</span>
              <span style="width:20px;height:20px;border-radius:5px;background:var(--yy-card);border:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:center;font-size:10.5px;color:var(--yy-t3);">/</span>
            </div>

            ${S.podCmdDrop ? `
              <div class="yy-float-card" style="position:absolute;top:46px;left:0;right:0;padding:8px;z-index:55;border-radius:12px;">
                <div style="font-size:10.5px;font-weight:600;color:var(--yy-t3);padding:4px 8px;">书房快捷动作</div>
                <button onclick="togglePodCmdDrop(false);setModal('podcastStudio')" style="width:100%;height:32px;padding:0 8px;border-radius:8px;display:flex;align-items:center;gap:8px;font-size:12.5px;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'">
                  <span style="color:var(--tint-accent);">${POD_GLYPHS.weekly(14)}</span><span>把最近读过的章节酿成一期书房周刊</span>
                </button>
                <button onclick="togglePodCmdDrop(false);setModal('podcastAdd')" style="width:100%;height:32px;padding:0 8px;border-radius:8px;display:flex;align-items:center;gap:8px;font-size:12.5px;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'">
                  <span style="color:var(--tint-accent);">${POD_GLYPHS.link(14)}</span><span>粘贴小宇宙 / Apple Podcasts 单集链接放入稍后听</span>
                </button>
                <div style="height:0.5px;background:var(--yy-line);margin:6px 4px;"></div>
                <div style="font-size:10.5px;font-weight:600;color:var(--yy-t3);padding:2px 8px 4px;">听过的句子</div>
                <button onclick="togglePodCmdDrop(false);openPodcastReader()" style="width:100%;padding:6px 8px;border-radius:8px;text-align:left;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'">
                  <div class="serif truncate" style="font-size:12.5px;">“Memorization is static crystallized skill, whereas intelligence…”</div>
                  <div class="tabular" style="font-size:10.5px;color:var(--yy-t3);margin-top:1px;">Lex Fridman #452 · 14:18</div>
                </button>
              </div>
            ` : ''}
          </div>

          <!-- 5 Flush-Aligned Circular Quick-Launch Buttons (Exact PodcastGlyphs!) -->
          <div style="margin-top:16px;display:flex;justify-content:space-between;">
            ${[
              { label: '酿一期', ico: POD_GLYPHS.plus(16), accent: true, act: "setModal('podcastStudio')" },
              { label: '书房周刊', ico: POD_GLYPHS.weekly(16), accent: false, act: "setModal('podcastStudio')" },
              { label: '跨书圆桌', ico: POD_GLYPHS.roundtable(16), accent: false, act: "setModal('podcastStudio')" },
              { label: '生词伴听', ico: POD_GLYPHS.vocab(16), accent: false, act: "setModal('podcastStudio')" },
              { label: '贴单集', ico: POD_GLYPHS.link(16), accent: false, act: "setModal('podcastAdd')" }
            ].map(q => `
              <div style="width:48px;display:flex;flex-direction:column;align-items:center;gap:6px;">
                <button class="pod-circle-btn ${q.accent ? 'accent' : ''}" style="width:40px;height:40px;" onclick="${q.act}">${q.ico}</button>
                <span style="font-size:11px;color:var(--yy-t3);">${q.label}</span>
              </div>
            `).join('')}
          </div>

          <!-- 3-Zone Tabletop Now-Playing Card -->
          <div class="yy-stationery-card" style="margin-top:22px;border-radius:14px;overflow:hidden;">
            <div style="padding:15px 17px 13px;">
              <div style="display:flex;align-items:center;gap:11px;">
                ${renderPodcastShowMark('Lex Fridman', false, 32)}
                <div style="flex:1;min-width:0;">
                  <div class="truncate" style="font-size:13.5px;font-weight:500;">#452 François Chollet: ARC-AGI</div>
                  <div style="font-size:11.5px;color:var(--yy-t3);">Lex Fridman · 正在播放 · 还剩 28 分</div>
                </div>
                <button class="pod-paper-pill accent" onclick="openPodcastReader()">展开书页</button>
              </div>

              <div onclick="openPodcastReader()" style="margin-top:13px;padding:11px 13px;border-radius:10px;background:var(--yy-leaf);border:0.5px solid var(--yy-hairline);cursor:pointer;">
                <div style="display:flex;justify-content:space-between;font-size:11px;margin-bottom:5px;">
                  <span style="color:var(--tint-accent);font-weight:500;">François Chollet</span>
                  <span style="color:var(--yy-t3);font-size:10.5px;">原声线中文 · 声纹保真</span>
                </div>
                <div class="serif" style="font-size:14px;line-height:1.65;color:var(--yy-t1);">
                  真正的智能并不是背下多少知识库，而是面对从未见过的新问题时，当场习得新技能的泛化效率。
                </div>
              </div>

              <div style="margin-top:12px;">
                <div style="height:2.5px;border-radius:99px;background:var(--yy-inset);overflow:hidden;">
                  <div style="width:38%;height:100%;background:var(--yy-t1);"></div>
                </div>
                <div class="tabular" style="display:flex;justify-content:space-between;font-size:10.5px;color:var(--yy-t3);margin-top:5px;">
                  <span>14:22</span><span>42:18</span>
                </div>
              </div>
            </div>

            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="padding:9px 13px;background:var(--yy-leaf);display:flex;align-items:center;gap:6px;">
              <button style="font-size:11px;color:var(--yy-t3);padding:0 4px;">-15s</button>
              <button onclick="openPodcastReader()" style="width:30px;height:30px;border-radius:50%;background:var(--yy-t1);color:var(--yy-card);display:flex;align-items:center;justify-content:center;">${ICONS.play}</button>
              <button style="font-size:11px;color:var(--yy-t3);padding:0 4px;">+15s</button>
              <button style="font-size:11px;color:var(--yy-t3);padding:0 4px;" class="tabular">1.0×</button>
              <span style="margin-left:auto;font-size:11px;color:var(--tint-accent);font-weight:500;">原声 · 书摘与插话就绪</span>
              <button onclick="showProtoToast('已记下原声片段，收入「原声书摘」')" style="display:inline-flex;align-items:center;gap:4px;font-size:11px;color:var(--yy-t2);margin-left:6px;">
                <span>划线</span><span class="yy-key" style="background:var(--yy-card);">H</span>
              </button>
            </div>
          </div>
        </div>
      </div>

      ${renderPodcastRightEdgeTriggers(S)}
      ${S.podDrawerTab ? renderPodcastDrawer(S) : ''}
    </div>
  `;
}

// PodcastDrawerContentView — Exact 2-Tab-Pair Mapping & All 4 Tabs (PodcastSharedUI.swift L757-1241)
function renderPodcastDrawer(S) {
  const tab = S.podDrawerTab;
  // Exact 2-button -> 2-tab-pair rule on Mac!
  const pairIsClips = (tab === 'clips' || tab === 'later');
  const tabs = pairIsClips
    ? [{ id: 'clips', label: `原声书摘 · ${POD_CLIPS.length}` }, { id: 'later', label: '稍后听 · 2' }]
    : [{ id: 'voices', label: '声音卡 · 4' }, { id: 'local', label: '本机整理' }];

  return `
    <div style="position:absolute;inset:0;background:rgba(0,0,0,0.08);z-index:60;display:flex;justify-content:flex-end;" onclick="openPodcastDrawer(null)">
      <div onclick="event.stopPropagation()" style="width:386px;margin:14px;border-radius:15px;background:var(--yy-card);border:0.5px solid var(--yy-line);box-shadow:0 10px 26px rgba(30,26,20,0.16);display:flex;flex-direction:column;overflow:hidden;">
        <!-- Header: Capsule 2-tab pair + xmark -->
        <div style="padding:12px 16px;border-bottom:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:space-between;">
          <div style="display:inline-flex;gap:2px;padding:2.5px;border-radius:999px;background:var(--yy-inset);border:0.5px solid var(--yy-line);">
            ${tabs.map(t => {
              const on = tab === t.id;
              return `<button onclick="openPodcastDrawer('${t.id}')" style="height:24px;padding:0 11px;border-radius:999px;font-size:11.5px;font-weight:${on ? '600' : '500'};color:${on ? 'var(--tint-accent)' : 'var(--yy-t3)'};background:${on ? 'var(--yy-card)' : 'transparent'};box-shadow:${on ? '0 1px 2px var(--yy-pod-pill-shadow)' : 'none'};">${t.label}</button>`;
            }).join('')}
          </div>
          <button onclick="openPodcastDrawer(null)" style="width:28px;height:28px;display:flex;align-items:center;justify-content:center;color:var(--yy-t3);">${POD_GLYPHS.xmark(13.5)}</button>
        </div>

        <!-- Scroll Body -->
        <div style="flex:1;overflow-y:auto;padding:16px;display:flex;flex-direction:column;gap:10px;">
          ${tab === 'clips' ? POD_CLIPS.map(c => `
            <div style="padding:12px 13px;border-radius:11px;background:var(--yy-leaf);border:0.5px solid var(--yy-line);display:flex;flex-direction:column;gap:8px;">
              <div class="serif" style="font-size:13.5px;line-height:1.65;color:var(--yy-t1);">「${c.quote}」</div>
              <div style="display:flex;align-items:center;gap:8px;">
                <span class="tabular" style="font-size:11px;color:var(--yy-t3);flex:1;">${c.show} · ${c.clock}</span>
                <button class="pod-paper-pill" style="height:23px;padding:0 9px;font-size:11px;" onclick="showProtoToast('正在回放原声书摘 (${c.dur})')">${ICONS.play.replace('width="12" height="12"', 'width="9" height="9"')}<span>${c.dur}</span></button>
                <button onclick="openPodcastDrawer(null);openPodcastReader()" title="回到原句" style="width:23px;height:23px;display:inline-flex;align-items:center;justify-content:center;color:var(--yy-t3);">${ICONS.chevRight}</button>
              </div>
              <div style="border-top:0.5px dashed var(--yy-line);margin-top:2px;padding-top:6px;">
                <input value="${c.thought}" placeholder="添一句随想…" style="width:100%;font-size:12px;color:var(--yy-t2);" />
              </div>
            </div>
          `).join('') : tab === 'later' ? `
            <div style="display:flex;flex-direction:column;">
              ${[
                { title: 'Vol.182 从活字印刷到电子墨水：阅读界面的五百年', show: '忽左忽右', dur: '54 分', studio: false },
                { title: '跨书圆桌：《瓦尔登湖》与现代人的注意力危机', show: '书房电台', dur: '16 分', studio: true }
              ].map((it, i) => `
                ${i > 0 ? `<div style="height:0.5px;background:var(--yy-soft-div);"></div>` : ''}
                <div style="padding:11px 0;display:flex;align-items:flex-start;gap:10px;">
                  <div style="padding-top:1px;">${renderPodcastShowMark(it.show, it.studio, 22)}</div>
                  <div style="flex:1;min-width:0;">
                    <div style="font-size:13px;font-weight:500;color:var(--yy-t1);line-height:1.4;">${it.title}</div>
                    <div style="font-size:11.5px;color:var(--yy-t3);margin-top:2px;">${it.show} · ${it.dur}</div>
                  </div>
                  <div style="display:flex;align-items:center;gap:3px;">
                    <button onclick="openPodcastDrawer(null);openPodcastReader()" title="立即收听" style="width:24px;height:24px;display:inline-flex;align-items:center;justify-content:center;color:var(--yy-t3);">${ICONS.play}</button>
                    <button onclick="showProtoToast('已移出稍后听')" title="移出稍后听" style="width:24px;height:24px;display:inline-flex;align-items:center;justify-content:center;color:var(--yy-t3);">${POD_GLYPHS.xmark(11)}</button>
                  </div>
                </div>
              `).join('')}
            </div>
          ` : tab === 'voices' ? `
            <div style="display:flex;flex-direction:column;">
              ${[
                { name: '林深 · 书房主播', detail: '本地声卡 · 沉静温和 · 书房电台默认主讲', studio: true, on: true },
                { name: '苏晚 · 理性对谈', detail: '本地声卡 · 清晰克制 · 跨书圆桌搭档', studio: true, on: true },
                { name: 'François Chollet', detail: '嘉宾声纹 · 本机提取 · Lex Fridman #452 (8.1s)', studio: false, on: true },
                { name: '我的声纹（本机克隆）', detail: '仅存本机安全区 · 不上传 · 可在周刊中播报自己的批注', studio: false, on: false }
              ].map((v, i) => `
                ${i > 0 ? `<div style="height:0.5px;background:var(--yy-soft-div);"></div>` : ''}
                <div style="padding:11px 0;display:flex;align-items:center;gap:10px;">
                  ${renderPodcastShowMark(v.name, v.studio, 22)}
                  <div style="flex:1;min-width:0;">
                    <div class="truncate" style="font-size:13px;font-weight:500;color:var(--yy-t1);">${v.name}</div>
                    <div style="font-size:11.5px;color:var(--yy-t3);line-height:1.35;margin-top:2px;">${v.detail}</div>
                  </div>
                  <button class="pod-paper-pill" style="height:24px;padding:0 9px;font-size:11px;" onclick="showProtoToast('正在试听「${v.name}」')">试听</button>
                  <div class="yy-switch ${v.on ? 'on' : ''}" title="跨期记住声纹" onclick="this.classList.toggle('on')"></div>
                </div>
              `).join('')}
            </div>
          ` : `
            <!-- Tab 4: 本机整理 (All 3 leafSurface cards — PodcastSharedUI.swift L1190-1241) -->
            <div style="display:flex;flex-direction:column;gap:9px;">
              <div style="padding:12px;border-radius:11px;background:var(--yy-leaf);border:0.5px solid var(--yy-line);">
                <div style="font-size:13px;font-weight:500;color:var(--yy-t1);">本机播客书库</div>
                <div style="font-size:11.5px;color:var(--yy-t3);line-height:1.5;margin-top:4px;">已订阅 3 档频道 · 书桌 5 期 · 原声书摘 ${POD_CLIPS.length} 条</div>
              </div>
              <div style="padding:12px;border-radius:11px;background:var(--yy-leaf);border:0.5px solid var(--yy-line);">
                <div style="display:flex;align-items:center;justify-content:space-between;">
                  <span style="font-size:13px;font-weight:500;color:var(--yy-t1);">本机语音引擎已就绪</span>
                  <span style="font-size:11px;font-weight:600;color:var(--yy-ok);">✓ 离线</span>
                </div>
                <div style="font-size:11.5px;color:var(--yy-t3);line-height:1.5;margin-top:6px;">书房电台与双语中文配音均使用本机离线神经声卡合成，不经过云端。</div>
              </div>
              <div style="padding:12px;border-radius:11px;background:var(--yy-leaf);border:0.5px solid var(--yy-line);">
                <div style="font-size:13px;font-weight:500;color:var(--yy-t1);">本地优先 · 无账号</div>
                <div style="font-size:11.5px;color:var(--yy-t3);line-height:1.5;margin-top:4px;">不设推荐信息流、不弹未读红点；书摘、声音卡与私人电台全部保存在你的设备里。</div>
              </div>
            </div>
          `}
        </div>
      </div>
    </div>
  `;
}

// PodcastReaderView ("访谈书页" — PodcastReaderView.swift)
function renderPodcastReaderView(S) {
  const track = S.podTrack || 'coach';
  return `
    <div style="flex:1;display:flex;flex-direction:column;background:var(--yy-bg);position:relative;overflow:hidden;">
      <!-- Top Bar (Exact 32px symmetric padding + PodcastTrackSegmentedControl: 原声 | 原声线中文 | 双语) -->
      <div style="height:52px;margin-top:4px;padding:0 32px;border-bottom:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:space-between;flex-shrink:0;">
        <button onclick="closePodcastReader()" style="display:inline-flex;align-items:center;gap:5px;font-size:12.5px;font-weight:500;color:var(--yy-t2);padding:5px 10px 5px 6px;border-radius:8px;">
          ${ICONS.chevLeft}<span>返回收听台</span>
        </button>

        <div style="display:flex;align-items:center;gap:10px;">
          <div style="display:inline-flex;padding:2px;border-radius:999px;background:var(--yy-inset);border:0.5px solid var(--yy-line);">
            ${[
              { id: 'orig', label: '原声' },
              { id: 'dub', label: '原声线中文' },
              { id: 'coach', label: '双语' }
            ].map(tr => `
              <button onclick="setPodTrack('${tr.id}')" style="height:22px;padding:0 10px;border-radius:999px;font-size:11px;background:${track === tr.id ? 'var(--yy-card)' : 'transparent'};color:${track === tr.id ? 'var(--tint-accent)' : 'var(--yy-t3)'};font-weight:${track === tr.id ? '600' : '500'};">
                ${tr.label}
              </button>
            `).join('')}
          </div>
          <button class="pod-paper-pill ${S.podBlind ? 'accent' : ''}" onclick="togglePodBlind()">${S.podBlind ? '退出盲听' : '盲听'}</button>
          <button class="pod-paper-pill accent" onclick="showProtoToast('已记下 14:18 原声书摘')">${POD_GLYPHS.bookmark(13.5)}<span>划线</span></button>
        </div>
      </div>

      <!-- Centered 650px Editorial Magazine Column -->
      <div style="flex:1;overflow-y:auto;padding:28px 36px 90px;">
        <div style="max-width:650px;margin:0 auto;position:relative;">
          <div style="display:flex;align-items:center;gap:8px;margin-bottom:10px;font-size:12px;color:var(--yy-t3);">
            ${renderPodcastShowMark('Lex Fridman', false, 20)}
            <span>Lex Fridman Podcast · 42 分 · 已识 2 位声纹</span>
          </div>
          <div class="serif" style="font-size:25px;color:var(--yy-t1);line-height:1.35;">#452 François Chollet: ARC-AGI, Deep Learning & Fluid Intelligence</div>
          <div style="padding:10px 0 18px;margin-bottom:12px;border-bottom:0.5px solid var(--yy-line);font-size:12px;color:var(--yy-t3);">
            当前声轨：${track === 'orig' ? '原声' : (track === 'dub' ? '原声线中文' : '双语')} · 点任意句子即刻跳播，点虚线词查词收进生词本
          </div>

          <div style="margin:8px 0 12px;padding:8.5px 12px;background:var(--yy-inset);border-radius:9px;display:flex;justify-content:space-between;font-size:11.5px;color:var(--yy-t3);">
            <span>已静默折叠 1 段赞助口播（00:48）</span><span>展开</span>
          </div>

          <div style="display:flex;flex-direction:column;gap:6px;${S.podBlind ? 'filter:blur(6px);opacity:0.42;' : ''}">
            <div style="display:flex;gap:14px;padding:13px 12px;margin:0 -12px;border-radius:11px;">
              <span class="tabular" style="width:50px;padding-top:4px;font-size:11px;color:var(--yy-t3);">13:52</span>
              <div style="flex:1;">
                <div style="font-size:11.5px;margin-bottom:5px;"><strong style="color:var(--tint-accent);">Lex Fridman</strong> <span style="font-size:11px;color:var(--yy-t3);">原声线 · 6.4s 采样</span></div>
                <div class="georgia" style="font-size:16.5px;line-height:1.68;">Why do giant models memorize benchmarks yet stumble on simple visual puzzles?</div>
                ${track === 'coach' ? `<div class="serif" style="font-size:13.5px;color:var(--yy-t2);margin-top:4px;line-height:1.6;">为什么超大模型能背下无数题库，却在简单的视觉推理谜题前屡屡碰壁？</div>` : ''}
              </div>
            </div>

            <div style="display:flex;gap:14px;padding:13px 12px;margin:0 -12px;border-radius:11px;background:var(--tint-wash);position:relative;">
              <span class="tabular" style="width:50px;padding-top:4px;font-size:11px;color:var(--tint-accent);font-weight:600;">14:18</span>
              <div style="flex:1;">
                <div style="font-size:11.5px;margin-bottom:5px;"><strong style="color:var(--tint-accent);">François Chollet</strong> <span style="font-size:11px;color:var(--yy-t3);">原声线克隆 · 保留原本韵律</span></div>
                <div class="georgia" style="font-size:16.5px;line-height:1.68;">
                  Memorization is static crystallized skill, whereas intelligence is efficiency of <span class="vocab-dotted" onclick="togglePodVocabPop()">generalization</span> across novel tasks.
                </div>
                ${track !== 'orig' ? `<div class="serif" style="font-size:13.5px;color:var(--yy-t2);margin-top:5px;line-height:1.6;">记忆只是静态的晶体技能，而真正的智能是在全新任务上的泛化效率。</div>` : ''}
                <div style="margin-top:8px;">
                  <button class="pod-paper-pill accent" style="height:23px;padding:0 9px;font-size:11px;" onclick="openBookByFormat('arc-pdf')">《On the Measure of Intelligence》§3.2</button>
                </div>

                ${S.podVocabPop ? `
                  <div class="yy-float-card" style="position:absolute;top:118px;left:180px;width:310px;padding:14px 16px;z-index:45;">
                    <div style="font-size:12.5px;font-weight:600;color:var(--tint-accent);margin-bottom:6px;">generalization  <span style="font-weight:400;color:var(--yy-t2);">/ˌdʒen.ər.əl.aɪˈzeɪ.ʃən/</span></div>
                    <div class="serif" style="font-size:13.5px;line-height:1.55;">n. 泛化能力；在本期语境中指系统脱离训练分布、当场合成新解法的流体智能。</div>
                    <div style="height:0.5px;background:var(--yy-soft-div);margin:10px 0 9px;"></div>
                    <div style="display:flex;justify-content:space-between;font-size:11.5px;">
                      <span style="color:var(--yy-t3);">语境释义</span>
                      <button style="color:var(--tint-accent);font-weight:600;" onclick="showProtoToast('已收进生词本');togglePodVocabPop()">+ 收进生词本</button>
                    </div>
                  </div>
                ` : ''}
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- Optional Floating Cut-in Q&A Overlay (Both Question & Spoken Reply States!) -->
      ${S.podInterruptOpen ? `
        <div style="position:absolute;left:50%;bottom:66px;transform:translateX(-50%);width:610px;border-radius:14px;background:var(--yy-card);border:0.5px solid var(--yy-hairline);box-shadow:0 8px 36px rgba(40,34,26,0.16);overflow:hidden;z-index:50;">
          ${S.podInterruptAnswered ? `
            <div style="padding:12px 14px;background:var(--yy-leaf);border-bottom:0.5px solid var(--yy-line);display:flex;align-items:flex-start;gap:10px;">
              <span class="pod-eq" style="margin-top:4px;"><i></i><i></i><i></i></span>
              <div style="flex:1;">
                <div style="font-size:11px;font-weight:600;color:var(--tint-accent);margin-bottom:3px;">林深（书伴语音作答中）</div>
                <div class="serif" style="font-size:13px;line-height:1.6;color:var(--yy-t1);">「晶体智能好比背熟的菜谱，只对见过的考题有效；流体智能则是面对陌生食材当场推理出做法的能力——这正是 ARC-AGI 测量的部分。」</div>
              </div>
              <button class="pod-paper-pill accent" onclick="togglePodInterrupt()">继续 -3s</button>
            </div>
          ` : ''}
          <div style="height:44px;padding:0 10px;display:flex;align-items:center;gap:8px;">
            <button class="pod-paper-pill accent" style="height:26px;">${ICONS.mic}<span>语音说</span></button>
            <input value="他说的晶体智能和流体智能有什么区别？" style="flex:1;font-size:12.5px;" />
            <button class="yy-pill dark-solid sm" onclick="answerPodInterrupt()">发送</button>
            <button class="pod-paper-pill" onclick="togglePodInterrupt()">继续 -3s</button>
          </div>
          <div style="height:34px;padding:0 12px;background:var(--yy-leaf);border-top:0.5px solid var(--yy-line);display:flex;align-items:center;gap:8px;font-size:11px;color:var(--yy-t3);">
            <span style="width:5px;height:5px;border-radius:50%;background:var(--tint-accent);"></span>
            <span>已自动压低原声 (-24 dB)</span>
            <button class="pod-paper-pill" style="height:21px;font-size:10.5px;margin-left:auto;" onclick="answerPodInterrupt()">用大白话再讲一遍</button>
            <button class="pod-paper-pill" style="height:21px;font-size:10.5px;" onclick="answerPodInterrupt()">和《On the Measure…》有何关联？</button>
          </div>
        </div>
      ` : ''}

      <!-- Bottom Ultra-Slim 44px Segmented-Chapter Player Dock -->
      <div style="padding:4px 20px 14px;display:flex;justify-content:center;flex-shrink:0;">
        <div style="width:100%;max-width:620px;height:44px;padding:0 10px;border-radius:12px;background:var(--yy-card);border:0.5px solid var(--yy-hairline);box-shadow:0 2px 16px rgba(36,32,26,0.06);display:flex;align-items:center;gap:8px;">
          <button style="font-size:11px;color:var(--yy-t2);padding:0 4px;">-15s</button>
          <button style="width:28px;height:28px;border-radius:50%;background:var(--yy-t1);color:var(--yy-card);display:flex;align-items:center;justify-content:center;">${ICONS.pause}</button>
          <button style="font-size:11px;color:var(--yy-t2);padding:0 4px;">+15s</button>

          <span class="tabular" style="font-size:10.5px;font-weight:600;color:var(--tint-accent);">02/04 ⌄</span>
          <div style="flex:1;display:flex;align-items:center;gap:3.5px;position:relative;" title="02 · Fluid Intelligence vs Memorization · 14:22">
            <div style="flex:1;height:3.6px;border-radius:99px;background:var(--yy-t1);opacity:0.7;"></div>
            <div style="flex:1.6;height:4.6px;border-radius:99px;background:var(--yy-inset);position:relative;">
              <div style="width:55%;height:100%;border-radius:99px;background:var(--tint-accent);"></div>
              <span style="position:absolute;left:42%;top:-1.5px;width:2.5px;height:7.5px;background:#C99A3B;border-radius:1px;" title="原声书摘标记"></span>
              <span style="position:absolute;left:55%;top:50%;transform:translate(-50%,-50%);width:9px;height:9px;border-radius:50%;background:var(--yy-t1);border:1.5px solid var(--yy-card);"></span>
            </div>
            <div style="flex:0.4;height:2.6px;border-radius:99px;background:var(--yy-inset);opacity:0.45;" title="已折叠口播段"></div>
            <div style="flex:1.2;height:3.6px;border-radius:99px;background:var(--yy-inset);"></div>
          </div>
          <span class="tabular" style="font-size:10.5px;color:var(--yy-t3);">14:22</span>

          <span class="tabular" style="font-size:11px;color:var(--yy-t2);">1.0×</span>
          <button onclick="showProtoToast('已记下原声书摘')" style="font-size:11px;color:var(--yy-t2);display:inline-flex;align-items:center;gap:3px;">划线 <span class="yy-key">H</span></button>
          <button class="pod-paper-pill ${S.podInterruptOpen ? '' : 'accent'}" style="${S.podInterruptOpen ? 'background:var(--tint-accent);color:#FFF;border-color:transparent;' : ''}" onclick="togglePodInterrupt()">
            ${ICONS.mic}<span>${S.podInterruptOpen ? '插话中' : '插话'}</span>
          </button>
        </div>
      </div>

      ${renderPodcastRightEdgeTriggers(S)}
      ${S.podDrawerTab ? renderPodcastDrawer(S) : ''}
    </div>
  `;
}

// ============================================================================
// 4. MacNotesView, MacWordsView & CollectionWall
// ============================================================================
function renderMacNotesView(S, insetLeft) {
  const byBook = !!S.notesByBook;
  const coversOnly = !!S.notesCoversOnly;
  return `
    <div style="flex:1;overflow-y:auto;padding-bottom:36px;">
      <div class="shelf-page-header">
        <div style="padding-left:${insetLeft}px;display:flex;align-items:baseline;gap:12px;">
          <span style="font-size:22px;font-weight:700;">笔记</span>
          <span class="tabular" style="font-size:12px;color:var(--yy-t2);">${NOTES_DATA.length} 条 · 来自 3 本书</span>
        </div>
        <div style="display:flex;align-items:center;gap:10px;">
          <div class="yy-seg sm" style="min-width:152px;">
            <button class="yy-seg-btn ${!byBook ? 'active' : ''}" onclick="setNotesMode(false)">按时间</button>
            <button class="yy-seg-btn ${byBook ? 'active' : ''}" onclick="setNotesMode(true)">按书</button>
          </div>
          <button class="yy-pill" onclick="openWin('settings','notes')">${ICONS.share}<span>导出</span></button>
        </div>
      </div>

      ${!byBook ? `
        <div style="padding:2px 28px 28px 22px;display:flex;flex-direction:column;gap:18px;">
          <div style="display:flex;align-items:center;gap:6px;">
            <button class="yy-pill primary sm">全部 ${NOTES_DATA.length}</button>
            <button class="yy-pill sm">有想法 3</button>
            <span style="width:0.5px;height:16px;background:var(--yy-line);margin:0 4px;"></span>
            <button class="yy-pill sm"><i style="width:10px;height:10px;border-radius:50%;background:#FBE7A0;border:0.5px solid rgba(0,0,0,0.2);"></i> 1</button>
            <button class="yy-pill sm"><i style="width:10px;height:10px;border-radius:50%;background:#C4E3F4;border:0.5px solid rgba(0,0,0,0.2);"></i> 1</button>
            <button class="yy-pill sm"><i style="width:10px;height:10px;border-radius:50%;background:#F09E94;border:0.5px solid rgba(0,0,0,0.2);"></i> 1</button>
            <button class="yy-pill sm"><span style="color:#EF7F71;font-weight:700;">〰</span> 1</button>
          </div>

          <div style="position:relative;padding-bottom:11px;">
            <div style="position:absolute;left:28px;right:28px;bottom:0;height:36px;border-radius:14px;background:var(--yy-card);opacity:0.45;border:0.5px solid var(--yy-line);"></div>
            <div style="position:absolute;left:14px;right:14px;bottom:5px;height:40px;border-radius:14px;background:var(--yy-card);opacity:0.75;border:0.5px solid var(--yy-line);"></div>
            <div class="yy-card" style="position:relative;padding:12px 14px 12px 18px;display:flex;align-items:center;gap:16px;">
              <div style="padding-right:18px;border-right:1px solid var(--yy-line);">
                <div style="font-size:13px;font-weight:700;">今天回顾</div>
                <div class="tabular" style="font-size:10.5px;color:var(--yy-t3);margin-top:2px;">5 条 · 约 2 分钟</div>
              </div>
              <div style="flex:1;min-width:0;">
                <div class="serif truncate" style="font-size:13.5px;">“曲径通幽处，禅房花木深。山光悦鸟性，潭影空人心。”</div>
                <div style="font-size:10.5px;color:var(--yy-t3);margin-top:2px;">《红楼梦》等 3 本书的旧划线</div>
              </div>
              <button class="yy-pill primary" onclick="setModal('review')">开始</button>
            </div>
          </div>

          ${['今天', '昨天'].map(day => {
            const items = NOTES_DATA.filter(n => n.day === day);
            return `
              <div style="display:flex;align-items:flex-start;gap:16px;">
                <div style="width:92px;padding-top:14px;flex-shrink:0;">
                  <div style="font-size:13px;font-weight:700;">${day}</div>
                  <div class="tabular" style="font-size:10.5px;color:var(--yy-t3);margin-top:3px;">${items[0].dateLabel}</div>
                  <div class="tabular" style="font-size:10.5px;color:var(--yy-t3);margin-top:2px;">${items.length} 条</div>
                </div>
                <div class="yy-card" style="flex:1;padding:0 20px;">
                  ${items.map((m, i) => `
                    ${i > 0 ? `<div style="height:0.5px;background:var(--yy-line);"></div>` : ''}
                    <div style="padding:14px 0 12px;">
                      <div class="serif" style="font-size:14px;line-height:1.75;letter-spacing:0.28px;">
                        <span style="${m.style === 'wave' ? 'text-decoration:underline wavy #EF7F71;text-underline-offset:4px;' : `background:var(--yy-mk-${m.style === 'yellow' ? 'y' : (m.style === 'blue' ? 'b' : 'p')});padding:1px 2px;border-radius:3px;`}">${m.text}</span>
                      </div>
                      ${m.note ? `<div class="kai" style="margin-top:7px;padding-left:10px;border-left:1.5px solid var(--tint-accent);font-size:13px;color:var(--tint-accent);line-height:1.55;">${m.note}</div>` : ''}
                      <div style="margin-top:9px;display:flex;align-items:center;gap:6px;font-size:10.5px;color:var(--yy-t3);">
                        <span style="font-weight:600;color:var(--yy-t2);">${m.bookTitle}</span>
                        <span>· ${m.chapter}</span>
                        <span class="tabular" style="margin-left:auto;display:inline-flex;align-items:center;gap:4px;">${m.audio ? ICONS.headphonesTiny : ''} ${m.time}</span>
                      </div>
                    </div>
                  `).join('')}
                </div>
              </div>`;
          }).join('')}
        </div>
      ` : `
        <div style="padding:4px 28px 28px 22px;display:flex;align-items:flex-start;gap:24px;">
          <div style="width:${coversOnly ? '48px' : '250px'};flex-shrink:0;display:flex;flex-direction:column;gap:8px;transition:width 0.2s ease;">
            <div style="display:flex;align-items:center;justify-content:space-between;">
              ${!coversOnly ? `<span class="tabular" style="font-size:12.5px;font-weight:600;padding-left:4px;">3 本书</span>` : ''}
              <div style="display:inline-flex;gap:2px;padding:2px;background:var(--yy-inset);border-radius:8px;">
                <button onclick="toggleNotesCovers(false)" title="显示书名" style="width:21px;height:18px;border-radius:6px;background:${!coversOnly ? 'var(--yy-card)' : 'transparent'};display:flex;align-items:center;justify-content:center;">${ICONS.list}</button>
                <button onclick="toggleNotesCovers(true)" title="只看封面" style="width:21px;height:18px;border-radius:6px;background:${coversOnly ? 'var(--yy-card)' : 'transparent'};display:flex;align-items:center;justify-content:center;">${ICONS.sidebar}</button>
              </div>
            </div>
            <div class="yy-card" style="padding:${coversOnly ? '8px 6px' : '5px'};border-radius:14px;display:flex;flex-direction:column;gap:${coversOnly ? '8px' : '2px'};align-items:${coversOnly ? 'center' : 'stretch'};">
              ${[BOOKS[0], BOOKS[1], BOOKS[4]].map((b, idx) => coversOnly ? `
                <div style="padding:2px;border-radius:6px;border:${idx === 0 ? '1.5px solid var(--tint-accent)' : '1.5px solid transparent'};opacity:${idx === 0 ? '1' : '0.72'};cursor:pointer;">
                  ${renderBookCover(b, 30)}
                </div>
              ` : `
                <div style="padding:7px 10px 7px 8px;border-radius:10px;background:${idx === 0 ? 'var(--tint-soft)' : 'transparent'};display:flex;align-items:center;gap:10px;cursor:pointer;">
                  ${renderBookCover(b, 26)}
                  <div style="flex:1;min-width:0;">
                    <div class="truncate" style="font-size:12px;font-weight:600;color:${idx === 0 ? 'var(--tint-accent)' : 'var(--yy-t1)'};">${b.title}</div>
                    <div style="font-size:10.5px;color:var(--yy-t3);">划线 2 · 想法 2</div>
                  </div>
                </div>
              `).join('')}
            </div>
          </div>

          <div style="flex:1;display:flex;flex-direction:column;gap:14px;">
            <div style="display:flex;align-items:center;gap:14px;">
              ${renderBookCover(BOOKS[0], 46)}
              <div style="flex:1;">
                <div style="font-size:19px;font-weight:700;">红楼梦</div>
                <div style="font-size:10.5px;color:var(--yy-t3);margin-top:2px;">曹雪芹 · 读到 34% · 划线 2 · 按书里的顺序</div>
              </div>
              <button class="yy-pill sm" onclick="setModal('review')">回顾</button>
              <button class="yy-pill sm" onclick="openBookByFormat('hlm')">打开书</button>
            </div>
            <div class="yy-card" style="padding:0 20px;">
              ${NOTES_DATA.filter(n => n.bookId === 'hlm').map((m, i) => `
                ${i > 0 ? `<div style="height:0.5px;background:var(--yy-line);"></div>` : ''}
                <div style="padding:14px 0 12px;">
                  <div class="serif" style="font-size:14px;line-height:1.75;"><span style="background:var(--yy-mk-y);padding:1px 2px;border-radius:3px;">${m.text}</span></div>
                  ${m.note ? `<div class="kai" style="margin-top:7px;padding-left:10px;border-left:1.5px solid var(--tint-accent);font-size:13px;color:var(--tint-accent);">${m.note}</div>` : ''}
                  <div style="margin-top:8px;font-size:10.5px;color:var(--yy-t3);display:flex;justify-content:space-between;"><span>${m.chapter}</span><span>${m.dateLabel}</span></div>
                </div>
              `).join('')}
            </div>
          </div>
        </div>
      `}
    </div>
  `;
}

function renderMacWordsView(S, insetLeft) {
  const showKnown = !!S.wordsShowKnown;
  const list = VOCAB_DATA.filter(w => w.known === showKnown);
  return `
    <div style="flex:1;overflow-y:auto;padding-bottom:36px;">
      <div class="shelf-page-header">
        <div style="padding-left:${insetLeft}px;display:flex;align-items:baseline;gap:12px;">
          <span style="font-size:22px;font-weight:700;">生词本</span>
          <span class="tabular" style="font-size:12px;color:var(--yy-t2);">在学 3 · 已掌握 1</span>
        </div>
        <div style="display:flex;align-items:center;gap:10px;">
          <div class="yy-seg sm" style="min-width:152px;">
            <button class="yy-seg-btn active">按时间</button>
            <button class="yy-seg-btn">按书</button>
          </div>
          <button class="yy-pill" onclick="showProtoToast('支持导出 Anki 制表符卡片 (.txt) 与 Excel UTF-8 BOM (.csv)')">${ICONS.share}<span>导出</span></button>
        </div>
      </div>

      <div style="padding:2px 28px 28px 22px;display:flex;flex-direction:column;gap:18px;">
        <div style="display:flex;gap:6px;">
          <button class="yy-pill ${!showKnown ? 'primary' : ''} sm" onclick="toggleWordsKnown(false)">在学 3</button>
          <button class="yy-pill ${showKnown ? 'primary' : ''} sm" onclick="toggleWordsKnown(true)">已掌握 1</button>
        </div>

        <div style="display:flex;align-items:flex-start;gap:16px;">
          <div style="width:92px;padding-top:14px;flex-shrink:0;">
            <div style="font-size:13px;font-weight:700;">今天</div>
            <div class="tabular" style="font-size:10.5px;color:var(--yy-t3);margin-top:3px;">11 月 23 日</div>
            <div class="tabular" style="font-size:10.5px;color:var(--yy-t3);margin-top:2px;">${list.length} 个</div>
          </div>
          <div class="yy-card" style="flex:1;padding:0 20px;">
            ${list.map((w, i) => `
              ${i > 0 ? `<div style="height:0.5px;background:var(--yy-line);"></div>` : ''}
              <div style="padding:16px 0;">
                <div style="display:flex;align-items:center;justify-content:space-between;">
                  <div style="display:flex;align-items:baseline;gap:9px;">
                    <span class="georgia" style="font-size:19px;font-weight:700;color:var(--yy-t1);">${w.word}</span>
                    <span style="font-size:12px;color:var(--yy-t2);">${w.phonetic}</span>
                  </div>
                  <button onclick="showProtoToast('正在朗读 ${w.word} (0.9×)')" style="width:28px;height:28px;border-radius:50%;background:var(--yy-inset);display:flex;align-items:center;justify-content:center;color:var(--yy-t1);">${ICONS.speaker}</button>
                </div>
                <div style="margin-top:4px;font-size:13px;">
                  <span class="georgia" style="font-style:italic;color:var(--yy-t3);margin-right:4px;">${w.pos}</span>
                  <span>${w.meaning}</span>
                </div>
                <div class="georgia" style="margin-top:9px;font-size:13.5px;line-height:1.58;color:var(--yy-t2);">
                  ${w.context.replace(w.match, `<span style="color:var(--yy-t1);border-bottom:1.5px solid var(--tint-accent);">${w.match}</span>`)}
                </div>
                <div style="margin-top:11px;display:flex;align-items:center;justify-content:space-between;">
                  <div>
                    <div style="font-size:11px;font-weight:600;color:var(--yy-t2);">${w.book}</div>
                    <div class="tabular" style="font-size:11px;color:var(--yy-t3);margin-top:1px;">${w.freq}</div>
                  </div>
                  <button onclick="showProtoToast('已更新生词掌握状态')" style="height:24px;padding:0 9px;border-radius:999px;background:var(--yy-inset);font-size:11px;font-weight:600;display:inline-flex;align-items:center;gap:4px;">
                    ${ICONS.check}<span>${w.known ? '再学一遍' : '已掌握'}</span>
                  </button>
                </div>
              </div>
            `).join('')}
          </div>
        </div>
      </div>
    </div>
  `;
}

function renderMacCollectionsWall(S, insetLeft) {
  return `
    <div style="flex:1;overflow-y:auto;padding-bottom:40px;">
      <div class="shelf-page-header">
        <div style="padding-left:${insetLeft}px;font-size:22px;font-weight:700;">全部合集</div>
        <button class="yy-pill" onclick="setModal('collectionPicker')">${ICONS.plus}<span>新建合集</span></button>
      </div>

      <div style="padding:9px 28px 14px 22px;display:flex;justify-content:space-between;align-items:center;">
        <span class="tabular" style="font-size:12.5px;font-weight:600;">${COLLECTIONS.length} 个</span>
        <span style="font-size:11.5px;color:var(--yy-t2);display:inline-flex;align-items:center;gap:4px;">最近用过 ${ICONS.chevUpDown}</span>
      </div>

      <div style="margin:0 28px 40px 22px;padding:10px;background:var(--yy-card);border-radius:22px;border:0.5px solid var(--yy-line);box-shadow:0 1px 6px rgba(0,0,0,0.035);">
        <div style="display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));column-gap:8px;row-gap:2px;">
          ${COLLECTIONS.map(c => `
            <div onclick="setShelfTab('col:${c.id}')" style="height:54px;padding:0 12px 0 6px;border-radius:13px;display:flex;align-items:center;gap:12px;cursor:pointer;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'">
              <div style="width:36px;height:36px;border-radius:9px;background:var(--yy-col-mark-bg);border:0.5px solid rgba(43,43,43,0.07);display:flex;flex-direction:column;align-items:center;justify-content:center;gap:2px;color:var(--yy-col-mark-ink);flex-shrink:0;">
                <span style="transform:scale(0.88);">${ICONS[c.icon] || ICONS.book}</span>
                <span style="width:20px;height:0.8px;background:currentColor;opacity:0.65;"></span>
                <span style="width:13px;height:0.8px;background:currentColor;opacity:0.45;"></span>
              </div>
              <div style="flex:1;min-width:0;">
                <div class="truncate" style="font-size:14px;font-weight:500;">${c.name}</div>
                <div class="tabular" style="font-size:11px;color:var(--yy-t3);margin-top:1px;">${c.lastUsed}</div>
              </div>
              ${c.pinned ? `<span style="color:var(--tint-accent);" title="已固定在侧栏">${ICONS.pin}</span>` : ''}
              <span class="tabular" style="font-size:12px;color:var(--yy-t3);">${c.count} 本</span>
              <span style="color:var(--yy-t3);opacity:0.8;">${ICONS.chevRight}</span>
            </div>
          `).join('')}
        </div>
      </div>
    </div>
  `;
}

// ============================================================================
// Shelf Overlays: BookMenu, QuickSearch, CollectionPicker, YYConfirm, FeedbackForm, Studio, Add
// ============================================================================
function renderShelfModals(S) {
  let out = '';

  if (S.bookMenu) {
    const b = BOOKS.find(x => x.id === S.bookMenu.id) || BOOKS[0];
    out += `
      <div class="yy-float-card" style="position:fixed;left:${Math.min(window.innerWidth - 230, S.bookMenu.x)}px;top:${Math.min(window.innerHeight - 340, S.bookMenu.y)}px;width:214px;z-index:200;overflow:hidden;" onclick="event.stopPropagation()">
        <div style="padding:11px 14px 10px;">
          <div style="display:flex;align-items:baseline;gap:6px;">
            <span class="truncate" style="font-size:13px;font-weight:700;">${b.title}</span>
            <span class="truncate" style="font-size:10.5px;color:var(--yy-t3);">${b.author}</span>
          </div>
          <div style="display:flex;align-items:center;gap:8px;margin-top:7px;">
            <div style="flex:1;height:2.5px;border-radius:99px;background:var(--yy-inset);overflow:hidden;"><div style="width:${Math.round(b.progress*100)}%;height:100%;background:var(--tint-fill);"></div></div>
            <span style="font-size:10.5px;color:var(--yy-t2);">${b.pct}</span>
          </div>
        </div>
        <div style="border-top:0.5px solid var(--yy-line);padding:4px 5px;">
          ${['在读', '想读', '读过', '搁置', '弃读'].map((st, idx) => `
            <button onclick="closeBookMenu();showProtoToast('已将《${b.title}》标为「${st}」', true)" style="width:100%;height:28px;padding:0 9px;border-radius:7px;display:flex;align-items:center;justify-content:space-between;font-size:12.5px;color:${idx === 0 ? 'var(--tint-accent)' : 'var(--yy-t1)'};font-weight:${idx === 0 ? '600' : '400'};" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'">
              <span>${st}</span>${idx === 0 ? ICONS.check : ''}
            </button>
          `).join('')}
        </div>
        <div style="border-top:0.5px solid var(--yy-line);padding:4px 5px;">
          <button onclick="closeBookMenu();openBookByFormat('${b.id}')" style="width:100%;height:28px;padding:0 9px;border-radius:7px;display:flex;align-items:center;justify-content:space-between;font-size:12.5px;font-weight:600;color:var(--tint-accent);" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'"><span>接着读</span>${ICONS.openBook}</button>
          <button onclick="closeBookMenu();openBookListen('${b.id}')" style="width:100%;height:28px;padding:0 9px;border-radius:7px;display:flex;align-items:center;justify-content:space-between;font-size:12.5px;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'"><span>从头听</span>${ICONS.headphones}</button>
          <button onclick="closeBookMenu();setModal('collectionPicker')" style="width:100%;height:28px;padding:0 9px;border-radius:7px;display:flex;align-items:center;justify-content:space-between;font-size:12.5px;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'"><span>加入合集…</span>${ICONS.stack}</button>
          <button onclick="closeBookMenu();setModal('editBook')" style="width:100%;height:28px;padding:0 9px;border-radius:7px;display:flex;align-items:center;justify-content:space-between;font-size:12.5px;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'"><span>编辑信息…</span>${ICONS.pencil}</button>
          <button onclick="closeBookMenu();showProtoToast('已在资源管理器中定位原文件')" style="width:100%;height:28px;padding:0 9px;border-radius:7px;display:flex;align-items:center;justify-content:space-between;font-size:12.5px;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'"><span>在资源管理器中显示</span>${ICONS.folder}</button>
          <button onclick="closeBookMenu();setModal('deleteBookConfirm')" style="width:100%;height:28px;padding:0 9px;border-radius:7px;display:flex;align-items:center;justify-content:space-between;font-size:12.5px;color:var(--yy-danger);" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'"><span>删除…</span>${ICONS.trash}</button>
        </div>
      </div>
    `;
  }

  // Exact CollectionPicker.swift (320px width, 30x30 search circle, 新建合集 capsule, inside + others groups)
  if (S.modal === 'collectionPicker') {
    const b = BOOKS[0];
    const inside = COLLECTIONS.filter(c => b.collections.includes(c.id));
    const others = COLLECTIONS.filter(c => !b.collections.includes(c.id));
    out += `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:320px;padding:12px;display:flex;flex-direction:column;gap:12px;">
          ${S.colPickerSearching ? `
            <div style="height:30px;padding:0 10px;border-radius:9px;background:var(--yy-card);border:1.5px solid var(--tint-accent);display:flex;align-items:center;gap:7px;">
              ${ICONS.search}<input placeholder="找合集，或打个新名字" style="flex:1;font-size:13px;" autofocus /><button onclick="toggleColPickerSearch(false)" style="color:var(--yy-t3);">${ICONS.xmark}</button>
            </div>
          ` : `
            <div style="display:flex;align-items:center;gap:10px;">
              <button onclick="toggleColPickerSearch(true)" title="找合集" style="width:30px;height:30px;border-radius:50%;background:var(--yy-card);box-shadow:0 0.5px 1.5px rgba(0,0,0,0.06);display:flex;align-items:center;justify-content:center;color:var(--yy-t1);">${ICONS.search}</button>
              <div style="flex:1;min-width:0;">
                <div style="font-size:13px;font-weight:600;color:var(--yy-t1);">加入合集</div>
                <div class="truncate" style="font-size:10.5px;color:var(--yy-t3);">${b.title}</div>
              </div>
              <button onclick="toggleColPickerSearch(true)" style="height:30px;padding:0 13px;border-radius:999px;background:var(--yy-card);box-shadow:0 0.5px 1.5px rgba(0,0,0,0.06);font-size:12px;font-weight:600;color:var(--yy-t1);">新建合集</button>
            </div>
          `}

          <div class="yy-card" style="border-radius:14px;overflow:hidden;">
            ${inside.map((c, i) => `
              ${i > 0 ? `<div style="height:0.5px;background:var(--yy-line);margin:0 16px;"></div>` : ''}
              <div onclick="setModal(null);showProtoToast('已更新《${b.title}》所属合集', true)" style="padding:4px;">
                <div style="height:42px;padding:0 12px;border-radius:10px;display:flex;align-items:center;gap:10px;cursor:pointer;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'">
                  <span style="width:32px;height:32px;border-radius:9px;background:var(--yy-inset);display:inline-flex;align-items:center;justify-content:center;">${ICONS[c.icon] || ICONS.book}</span>
                  <div style="flex:1;min-width:0;"><div style="font-size:13px;font-weight:600;">${c.name}</div><div class="tabular" style="font-size:11px;color:var(--yy-t3);">${c.count} 本</div></div>
                  <span style="color:var(--tint-accent);">${ICONS.check}</span>
                </div>
              </div>
            `).join('')}
          </div>

          <div class="yy-card" style="border-radius:14px;overflow:hidden;">
            ${others.map((c, i) => `
              ${i > 0 ? `<div style="height:0.5px;background:var(--yy-line);margin:0 16px;"></div>` : ''}
              <div onclick="setModal(null);showProtoToast('已加入「${c.name}」', true)" style="padding:4px;">
                <div style="height:42px;padding:0 12px;border-radius:10px;display:flex;align-items:center;gap:10px;cursor:pointer;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'">
                  <span style="width:32px;height:32px;border-radius:9px;background:var(--yy-inset);display:inline-flex;align-items:center;justify-content:center;">${ICONS[c.icon] || ICONS.book}</span>
                  <div style="flex:1;min-width:0;"><div style="font-size:13px;font-weight:600;">${c.name}</div><div class="tabular" style="font-size:11px;color:var(--yy-t3);">${c.count} 本</div></div>
                </div>
              </div>
            `).join('')}
          </div>
        </div>
      </div>
    `;
  }

  // Exact YYConfirm Dialog (Verified 1:1 against testMacDeleteBook.1.png & testMacBookWontOpen.1.png!)
  if (S.modal === 'deleteBookConfirm') {
    const b = BOOKS[0];
    out += `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:360px;padding:20px 22px 18px;display:flex;flex-direction:column;gap:12px;">
          <div style="width:44px;height:44px;border-radius:12px;background:#F5E5E2;color:var(--yy-danger);display:flex;align-items:center;justify-content:center;">
            ${ICONS.trash.replace('width="13" height="13"', 'width="20" height="20"')}
          </div>
          <div>
            <div style="font-size:15px;font-weight:700;color:var(--yy-t1);">从书库删除《${b.title}》？</div>
            <div style="font-size:12.5px;color:var(--yy-t2);line-height:1.58;margin-top:6px;">书文件会移进「最近删除」保留 30 天，期间可在「设置 › 存储」恢复；你的划线、想法与阅读进度会保留。</div>
          </div>
          <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:4px;">
            <button class="yy-pill" onclick="setModal(null)">取消</button>
            <button class="yy-pill danger-solid" onclick="setModal(null);showProtoToast('已删除《${b.title}》', true, true)">删除</button>
          </div>
        </div>
      </div>
    `;
  }

  if (S.quickSearchOpen) {
    out += `
      <div class="yy-modal-backdrop" style="align-items:flex-start;padding-top:50px;" onclick="closeQuickSearch()">
        <div onclick="event.stopPropagation()" style="width:640px;border-radius:18px;overflow:hidden;box-shadow:0 32px 60px rgba(0,0,0,0.34),0 0 0 0.5px rgba(0,0,0,0.14);background:var(--yy-card);">
          <div style="height:52px;padding:0 18px;background:var(--yy-glass);backdrop-filter:blur(20px);display:flex;align-items:center;gap:12px;">
            <span style="width:30px;display:flex;justify-content:center;color:var(--yy-t2);">${ICONS.search}</span>
            <input id="qs-input" value="${S.quickQuery || ''}" oninput="updateQuickQuery(this.value)" placeholder="搜书名、原文、笔记，或者问书伴" style="flex:1;font-size:19px;" />
            <span class="yy-key">esc</span>
          </div>
          <div style="height:1px;background:var(--yy-line);"></div>
          <div style="padding:6px 8px;max-height:400px;overflow-y:auto;">
            <div onclick="closeQuickSearch();openWin('reader');setReaderPanel('companion')" style="min-height:46px;padding:6px 10px;border-radius:10px;background:var(--tint-soft);display:flex;align-items:center;gap:12px;cursor:pointer;">
              <span style="width:30px;height:30px;border-radius:8px;background:var(--yy-card);color:var(--tint-accent);display:flex;align-items:center;justify-content:center;">${ICONS.sparkle}</span>
              <div style="flex:1;"><div style="font-size:13px;font-weight:600;">问书伴：${S.quickQuery || '第八回通灵宝玉上的八个字是什么？'}</div><div style="font-size:11px;color:var(--yy-t3);">基于上次阅读《红楼梦》已读前八回，不剧透后续情节</div></div>
              <span class="yy-key">${S.chrome === 'mac' ? '⌘↩' : 'Ctrl+↩'}</span>
            </div>
            <div style="padding:8px 10px 2px 52px;font-size:10.5px;font-weight:600;color:var(--yy-t3);display:flex;justify-content:space-between;"><span>书名和篇目</span><span>3</span></div>
            ${BOOKS.slice(0, 3).map(b => `
              <div onclick="closeQuickSearch();openBookByFormat('${b.id}')" style="min-height:38px;padding:5px 10px;border-radius:10px;display:flex;align-items:center;gap:12px;cursor:pointer;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'">
                <div style="width:30px;display:flex;justify-content:center;">${renderBookCover(b, 20)}</div>
                <div style="flex:1;"><span style="font-size:13px;font-weight:500;">${b.title}</span> <span style="font-size:11.5px;color:var(--yy-t3);">· ${b.author}</span></div>
                <span style="font-size:11px;color:var(--yy-t3);">${b.pct}</span>
              </div>
            `).join('')}
            <div style="padding:8px 10px 2px 52px;font-size:10.5px;font-weight:600;color:var(--yy-t3);display:flex;justify-content:space-between;"><span>全文与笔记</span><span>12 处 · 3 本书</span></div>
            <div onclick="closeQuickSearch();openWin('reader')" style="min-height:38px;padding:6px 10px 6px 52px;border-radius:10px;cursor:pointer;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'">
              <div class="serif truncate" style="font-size:12.5px;">“莫失莫忘，仙寿恒昌。”</div>
              <div style="font-size:10.5px;color:var(--yy-t3);">《红楼梦》· 第八回</div>
            </div>
          </div>
          <div style="height:1px;background:var(--yy-line);"></div>
          <div style="height:32px;padding:0 16px;display:flex;align-items:center;justify-content:space-between;font-size:11px;color:var(--yy-t3);">
            <span>↑↓ 选择 · ↩ 打开 · ${S.chrome === 'mac' ? '⌘↩' : 'Ctrl+↩'} 问书伴</span>
            <span>共 9 本书 · 4 条笔记</span>
          </div>
        </div>
      </div>
    `;
  }

  if (S.modal === 'review') {
    const done = !!S.reviewFinished;
    out += `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:440px;height:550px;padding:16px 24px 24px;display:flex;flex-direction:column;">
          <div style="display:flex;align-items:center;justify-content:space-between;">
            <button onclick="setModal(null)" style="width:30px;height:30px;border-radius:50%;background:var(--yy-card);display:flex;align-items:center;justify-content:center;">${ICONS.xmark}</button>
            <span class="tabular" style="font-size:13px;font-weight:700;">${done ? '今天回顾 · 看完了' : '今天回顾 · 2 / 5'}</span>
            <button onclick="setModal('quoteShare')" style="width:30px;height:30px;border-radius:50%;background:var(--yy-card);display:flex;align-items:center;justify-content:center;" title="生成书摘卡">${ICONS.share}</button>
          </div>
          <div style="margin-top:14px;display:flex;gap:4px;">
            ${[0,1,2,3,4].map(i => `<span style="flex:1;height:3px;border-radius:99px;background:${done || i < 2 ? 'var(--yy-t1)' : 'var(--yy-line)'};"></span>`).join('')}
          </div>
          <div style="flex:1;margin-top:22px;position:relative;padding-bottom:16px;">
            <div style="position:absolute;left:24px;right:24px;bottom:0;height:40px;border-radius:16px;background:var(--yy-card);opacity:0.4;"></div>
            <div style="position:absolute;left:12px;right:12px;bottom:8px;height:40px;border-radius:16px;background:var(--yy-card);opacity:0.7;"></div>
            <div class="yy-card" style="position:relative;height:100%;padding:24px 22px 18px;display:flex;flex-direction:column;">
              ${!done ? `
                <div class="georgia" style="font-size:40px;line-height:22px;color:var(--tint-accent);">“</div>
                <div class="serif" style="margin-top:12px;font-size:19px;font-weight:600;line-height:1.78;">曲径通幽处，禅房花木深。山光悦鸟性，潭影空人心。</div>
                <div class="kai" style="margin-top:14px;padding-left:10px;border-left:1.5px solid var(--tint-accent);font-size:13px;color:var(--tint-accent);">借古人句点破园林幽致，园未见而气韵先出。</div>
                <div style="margin-top:auto;padding-top:14px;border-top:1px solid var(--yy-line);display:flex;align-items:center;gap:10px;">
                  ${renderBookCover(BOOKS[0], 26)}
                  <div style="flex:1;min-width:0;">
                    <div style="font-size:12px;font-weight:600;">红楼梦</div>
                    <div style="font-size:10.5px;color:var(--yy-t3);">曹雪芹 · 第十七回 · 12 天前划的</div>
                  </div>
                  <button class="yy-pill primary sm" style="height:24px;padding:0 9px;" onclick="showProtoToast('正在播放关联播客原声片段')">${ICONS.wave}<span>重听原声</span></button>
                </div>
              ` : `
                <div class="serif" style="font-size:19px;font-weight:600;">今天的 5 条看完了</div>
                <div style="font-size:11.5px;color:var(--yy-t2);margin-top:4px;">留下 4 条 · 不要了 1 条（点任意行可反悔切换）</div>
                <div style="margin-top:14px;flex:1;display:flex;flex-direction:column;">
                  ${NOTES_DATA.map((n, idx) => `
                    <div style="height:40px;border-bottom:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:space-between;gap:8px;font-size:12.5px;">
                      <span class="serif truncate">${n.text}</span>
                      <span style="font-size:11px;color:${idx === 2 ? 'var(--yy-t3)' : 'var(--tint-accent)'};flex-shrink:0;">${idx === 2 ? '不要了' : '✓ 留下'}</span>
                    </div>
                  `).join('')}
                </div>
              `}
            </div>
          </div>
          <div style="margin-top:20px;display:flex;gap:10px;">
            ${!done ? `
              <button class="yy-pill lg" style="flex:1;" onclick="toggleReviewDone(true)">${ICONS.xmark}<span>不要了</span></button>
              <button class="yy-pill primary lg" style="flex:1;" onclick="toggleReviewDone(true)">${ICONS.check}<span>留下</span></button>
            ` : `
              <button class="yy-pill primary lg" style="flex:1;" onclick="toggleReviewDone(false);setModal(null)">完成</button>
            `}
          </div>
        </div>
      </div>
    `;
  }

  if (S.modal === 'podcastStudio') {
    out += `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:720px;padding:16px 20px;display:flex;flex-direction:column;gap:13px;">
          <div style="display:flex;align-items:center;justify-content:space-between;">
            <div><span class="serif" style="font-size:19px;">酿一期书房电台</span> <span style="font-size:12px;color:var(--yy-t3);margin-left:6px;">· 串联近期已读章节与夜间划线，绝不剧透</span></div>
            <button class="pod-circle-btn" style="width:28px;height:28px;" onclick="setModal(null)">${POD_GLYPHS.xmark(13)}</button>
          </div>
          <div style="display:flex;gap:14px;">
            <div class="yy-stationery-card" style="width:174px;display:flex;flex-direction:column;overflow:hidden;">
              <div style="padding:6px;display:flex;flex-direction:column;gap:2px;">
                ${[
                  { t: '书房周刊', d: '近期阅读与划线回顾', on: true },
                  { t: '难书导读', d: '读前直觉热身精讲', on: false },
                  { t: '跨书圆桌', d: '多本书同桌碰撞', on: false },
                  { t: '生词伴听', d: '通勤双语词汇电台', on: false }
                ].map(f => `
                  <div style="height:44px;padding:0 10px;border-radius:8.5px;background:${f.on ? 'var(--tint-wash)' : 'transparent'};display:flex;flex-direction:column;justify-content:center;">
                    <div style="font-size:12.5px;font-weight:${f.on ? '600' : '400'};color:${f.on ? 'var(--tint-accent)' : 'var(--yy-t1)'};">${f.t}</div>
                    <div style="font-size:10px;color:var(--yy-t3);">${f.d}</div>
                  </div>
                `).join('')}
              </div>
              <div style="margin-top:auto;padding:10px;background:var(--yy-leaf);border-top:0.5px solid var(--yy-hairline);">
                <div style="font-size:11px;font-weight:500;color:var(--yy-t2);margin-bottom:6px;">防剧透范围</div>
                <div class="yy-seg sm" style="width:100%;">
                  <button class="yy-seg-btn active">🔒 仅已读</button>
                  <button class="yy-seg-btn">全书</button>
                </div>
              </div>
            </div>

            <div style="flex:1;display:flex;flex-direction:column;gap:11px;">
              <div style="display:flex;justify-content:space-between;align-items:baseline;">
                <div><span class="serif" style="font-size:14px;">参考书目</span> <span style="font-size:11px;color:var(--yy-t3);">可多选</span></div>
                <span style="font-size:11px;color:var(--tint-accent);font-weight:600;">已选 2 本</span>
              </div>
              <div class="yy-stationery-card" style="overflow:hidden;">
                <div style="height:42px;padding:0 10px;background:var(--yy-leaf);border-bottom:0.5px solid var(--yy-hairline);display:flex;align-items:center;justify-content:space-between;gap:8px;">
                  <div style="height:28px;padding:0 10px;border-radius:8px;background:var(--yy-inset);display:flex;align-items:center;gap:6px;flex:1;color:var(--yy-t3);font-size:12px;">${ICONS.search}<span>筛选书架…</span></div>
                  <div class="yy-seg sm"><button class="yy-seg-btn active">在读</button><button class="yy-seg-btn">读过</button><button class="yy-seg-btn">全部</button></div>
                </div>
                <div style="padding:10px;display:grid;grid-template-columns:1fr 1fr;gap:8px;">
                  ${BOOKS.slice(0, 4).map((b, i) => `
                    <div style="height:46px;padding:0 10px;border-radius:10px;background:${i < 2 ? 'var(--tint-wash)' : 'var(--yy-leaf)'};border:0.5px solid ${i < 2 ? 'var(--tint-ring)' : 'var(--yy-soft-div)'};display:flex;align-items:center;gap:9px;">
                      ${renderBookCover(b, 22)}
                      <div style="flex:1;min-width:0;"><div class="truncate" style="font-size:12.5px;font-weight:500;">${b.title}</div><div style="font-size:10.5px;color:var(--yy-t3);">${b.author} · ${b.pct}</div></div>
                      ${i < 2 ? `<span style="color:var(--tint-accent);">${ICONS.check}</span>` : ''}
                    </div>
                  `).join('')}
                </div>
              </div>

              <div class="yy-stationery-card" style="overflow:hidden;">
                <div style="height:39px;padding:0 14px;display:flex;align-items:center;gap:10px;">
                  <span style="width:34px;font-size:12px;color:var(--yy-t2);">选题</span>
                  <input placeholder="选填 · 留空则按最近划线自动拟题" style="flex:1;font-size:12.5px;" />
                </div>
                <div style="height:0.5px;background:var(--yy-soft-div);margin-left:14px;"></div>
                <div style="height:50px;padding:0 14px;display:flex;align-items:center;gap:10px;">
                  <span style="width:34px;font-size:12px;color:var(--yy-t2);">时长</span>
                  <div style="flex:1;display:flex;flex-direction:column;gap:4px;">
                    <div style="display:flex;gap:7px;align-items:center;">
                      ${[1,1,0,0,0].map((on, idx) => `<div style="flex:1;height:6px;border-radius:99px;background:${on ? 'var(--tint-accent)' : 'rgba(45,40,34,0.16)'};position:relative;">${idx === 1 ? `<span style="position:absolute;right:-4px;top:-4px;width:14px;height:14px;border-radius:50%;background:#FFF;box-shadow:0 1px 3px rgba(0,0,0,0.25);display:flex;align-items:center;justify-content:center;"><i style="width:8px;height:8px;border-radius:50%;background:var(--yy-t1);"></i></span>` : ''}</div>`).join('')}
                    </div>
                    <div class="tabular" style="display:flex;justify-content:space-between;font-size:10px;color:var(--yy-t3);"><span>5</span><span>10</span><span style="color:var(--tint-accent);font-weight:600;">15</span><span>30</span><span>45</span><span>60</span></div>
                  </div>
                  <div style="width:72px;height:28px;border-radius:8px;background:var(--yy-leaf);border:0.5px solid var(--yy-pill-line);display:flex;align-items:center;justify-content:center;gap:3px;"><strong class="tabular">15</strong><span style="font-size:11px;color:var(--yy-t2);">分钟</span></div>
                </div>
              </div>
            </div>
          </div>
          <div style="display:flex;align-items:center;gap:8px;">
            <button class="pod-paper-pill" style="height:30px;">${POD_GLYPHS.waveform(14)}<span>林深 · 苏晚 ⌄</span></button>
            <span style="font-size:11px;color:var(--yy-t3);">双人对话 · 本机神经语音合成</span>
            <button class="pod-paper-pill" style="height:30px;margin-left:auto;" onclick="setModal(null)">取消</button>
            <button class="yy-pill dark-solid" style="height:30px;padding:0 16px;" onclick="setModal(null);openPodcastReader()">开始生成</button>
          </div>
        </div>
      </div>
    `;
  }

  if (S.modal === 'podcastAdd') {
    out += `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:500px;padding:16px 20px;">
          <div style="display:flex;justify-content:space-between;align-items:center;">
            <div><div class="serif" style="font-size:18px;">添加节目</div><div style="font-size:11.5px;color:var(--yy-t3);margin-top:2px;">搜播客目录、贴单集或导入本地音频</div></div>
            <button class="pod-circle-btn" style="width:28px;height:28px;" onclick="setModal(null)">${POD_GLYPHS.xmark(13)}</button>
          </div>
          <div style="margin-top:12px;height:36px;padding:0 12px;border-radius:10px;background:var(--yy-inset);display:flex;align-items:center;gap:8px;">
            ${ICONS.search}<input placeholder="搜索公开播客名称，或粘贴小宇宙 / Apple Podcasts / RSS 链接" style="flex:1;font-size:12.5px;" /><span class="yy-key">${S.chrome === 'mac' ? '⌘V' : 'Ctrl+V'}</span>
          </div>
          <div class="yy-stationery-card" style="margin-top:12px;padding:14px 16px;display:flex;flex-direction:column;gap:12px;">
            <div class="serif" style="font-size:14px;">三种添加方式</div>
            <div style="display:flex;gap:10px;align-items:center;"><span style="width:24px;height:24px;border-radius:7px;background:var(--tint-wash);color:var(--tint-accent);display:flex;align-items:center;justify-content:center;">${ICONS.search}</span><div><div style="font-size:12.5px;font-weight:500;">搜索公开目录一键订阅</div><div style="font-size:11px;color:var(--yy-t3);">支持中英文播客自动拉取 Podcasting 2.0 外挂逐字稿</div></div></div>
            <div style="display:flex;gap:10px;align-items:center;"><span style="width:24px;height:24px;border-radius:7px;background:var(--tint-wash);color:var(--tint-accent);display:flex;align-items:center;justify-content:center;">${POD_GLYPHS.link(14)}</span><div><div style="font-size:12.5px;font-weight:500;">粘贴单集网页免订阅直听</div><div style="font-size:11px;color:var(--yy-t3);">解析单集音频并放入收听台，听完即走</div></div></div>
            <div style="display:flex;gap:10px;align-items:center;"><span style="width:24px;height:24px;border-radius:7px;background:var(--tint-wash);color:var(--tint-accent);display:flex;align-items:center;justify-content:center;">${POD_GLYPHS.waveform(14)}</span><div><div style="font-size:12.5px;font-weight:500;">导入 OPML 或本地讲座录音</div><div style="font-size:11px;color:var(--yy-t3);">把 MP3 / M4A 访谈放进书页边听边划线</div></div></div>
          </div>
          <div style="margin-top:12px;display:flex;align-items:center;gap:8px;">
            <button class="pod-paper-pill" onclick="setModal(null);showProtoToast('选择 .opml 文件导入')">导入 OPML</button>
            <button class="pod-paper-pill" onclick="setModal(null);showProtoToast('选择本地音频导入')">本地音频</button>
            <span style="margin-left:auto;font-size:11px;color:var(--yy-t3);">粘贴链接回车即可收单集</span>
          </div>
        </div>
      </div>
    `;
  }

  if (S.modal === 'audiobookPair') {
    out += `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:380px;padding:24px 18px 20px;display:flex;flex-direction:column;align-items:center;gap:14px;">
          <div style="display:flex;align-items:flex-start;gap:16px;">
            <div style="display:flex;flex-direction:column;align-items:center;gap:7px;">
              <div style="width:84px;height:84px;border-radius:7px;background:var(--yy-inset);border:0.5px solid rgba(0,0,0,0.12);box-shadow:0 8px 18px rgba(30,28,24,0.18);display:flex;align-items:center;justify-content:center;color:var(--yy-t3);">${ICONS.headphones}</div>
              <span style="font-size:10.5px;color:var(--yy-t3);">有声书 · 42 分钟</span>
            </div>
            <div style="margin-top:26px;width:30px;height:30px;border-radius:50%;background:var(--yy-card);border:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:center;color:var(--yy-t2);">${ICONS.link}</div>
            <div style="display:flex;flex-direction:column;align-items:center;gap:7px;">
              <div style="width:56px;">${renderBookCover(BOOKS[4], 56)}</div>
              <span style="font-size:10.5px;color:var(--yy-t3);">电子书 · TXT</span>
            </div>
          </div>
          <div style="text-align:center;padding:0 8px;">
            <div style="font-size:16px;font-weight:700;">书架上有同一本《呐喊》</div>
            <div style="font-size:11.5px;color:var(--yy-t2);line-height:1.6;margin-top:5px;">合成一本听读书：听到哪句，书页上就走到哪句，点一句就从那句听。全部在你的电脑上做，不上传。</div>
          </div>
          <div class="yy-card" style="width:100%;padding:12px 14px;border-radius:14px;border:1.5px solid var(--yy-t1);position:relative;">
            <div style="display:flex;align-items:center;gap:6px;"><strong style="font-size:12.5px;">这台电脑</strong><span style="font-size:10px;padding:2px 6px;border-radius:5px;background:var(--yy-inset);color:var(--yy-t2);">推荐</span></div>
            <div style="font-size:11px;color:var(--yy-t3);margin-top:3px;">可以去做别的，好了会通知你</div>
            <span style="position:absolute;top:12px;right:12px;color:var(--tint-accent);">${ICONS.check}</span>
          </div>
          <div class="yy-card" style="width:100%;padding:0 14px;height:42px;border-radius:14px;display:flex;align-items:center;justify-content:space-between;font-size:12px;">
            <span>以后同名的书都这样配</span><div class="yy-switch on"></div>
          </div>
          <button class="yy-pill primary lg" style="width:100%;" onclick="setModal(null);openWin('audiobook');setAudioMakeMode('making')">开始合成</button>
          <button style="font-size:12px;color:var(--yy-t2);" onclick="setModal(null)">换一本电子书</button>
        </div>
      </div>
    `;
  }

  if (S.modal === 'editBook') {
    const b = BOOKS[0];
    out += `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:360px;padding:18px;display:flex;flex-direction:column;gap:14px;">
          <div style="display:flex;justify-content:space-between;align-items:center;"><strong style="font-size:15px;">编辑信息</strong><button onclick="setModal(null)">${ICONS.xmark}</button></div>
          <div style="display:flex;flex-direction:column;align-items:center;gap:8px;">
            ${renderBookCover(b, 64)}
            <button style="font-size:11.5px;font-weight:600;color:var(--tint-accent);">换封面</button>
          </div>
          <div class="yy-card" style="padding:0 12px;border-radius:14px;">
            <div style="height:40px;display:flex;align-items:center;gap:10px;"><span style="width:44px;font-size:12px;color:var(--yy-t2);">书名</span><input value="${b.title}" style="flex:1;font-size:12.5px;" /></div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="height:40px;display:flex;align-items:center;gap:10px;"><span style="width:44px;font-size:12px;color:var(--yy-t2);">作者</span><input value="${b.author}" style="flex:1;font-size:12.5px;" /></div>
          </div>
          <div style="font-size:11px;color:var(--yy-t3);">只改书架上显示的名字，书的文件不动。封面也可以直接拖一张图到上面。</div>
          <button class="yy-pill primary lg" style="width:100%;" onclick="setModal(null);showProtoToast('已保存书籍信息')">好</button>
        </div>
      </div>
    `;
  }

  // Exact FeedbackForm (Feedback.swift: 500x532, 问题|建议|其他, attachments bar, email + expandable device info, GitHub Issues notice)
  if (S.modal === 'feedback') {
    out += renderFeedbackSheet(S);
  }

  if (S.modal === 'addRecordBook') {
    out += `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:380px;padding:20px;display:flex;flex-direction:column;gap:14px;">
          <div style="font-size:15px;font-weight:600;">加一本书</div>
          <input class="serif" placeholder="书名" style="height:40px;padding:0 12px;border-radius:10px;background:var(--yy-card);border:0.5px solid var(--yy-line);font-size:17px;font-weight:700;" />
          <div style="display:flex;align-items:center;gap:8px;">
            <span style="font-size:12px;color:var(--yy-t3);">放进 在读</span>
            <button class="yy-rec-btn" style="margin-left:auto;" onclick="setModal(null)">取消</button>
            <button class="yy-rec-btn main" onclick="setModal(null);showProtoToast('已加入阅读进度档案')">加入</button>
          </div>
          <div style="font-size:11px;color:var(--yy-t3);">作者、页数、封面以后在书的页面里补，不补也行。</div>
        </div>
      </div>
    `;
  }

  return out;
}

// Shared exact FeedbackForm (usable from both Shelf & Settings › 关于)
function renderFeedbackSheet(S) {
  const cat = S.fbCat || 'bug';
  const placeholders = {
    bug: '哪本书、哪一页，做了什么，结果怎样',
    idea: '想要什么功能，或者哪里可以更顺手',
    other: '想说的都可以'
  };
  return `
    <div class="yy-modal-backdrop" onclick="setModal(null)">
      <div class="yy-sheet" onclick="event.stopPropagation()" style="width:500px;display:flex;flex-direction:column;">
        <!-- Header -->
        <div style="padding:14px 16px 10px 20px;">
          <div style="display:flex;align-items:center;justify-content:space-between;">
            <strong style="font-size:15px;color:var(--yy-t1);">反馈</strong>
            <button onclick="setModal(null)" style="width:26px;height:26px;border-radius:50%;background:var(--yy-inset);display:flex;align-items:center;justify-content:center;color:var(--yy-t2);">${ICONS.xmark}</button>
          </div>
          <div style="font-size:12px;color:var(--yy-t3);margin-top:3px;">语阅还在早期，哪里不对、想要什么，直接写。</div>
        </div>

        <!-- Body -->
        <div style="padding:2px 20px 12px;display:flex;flex-direction:column;gap:12px;">
          <div class="yy-seg" style="width:100%;">
            <button class="yy-seg-btn ${cat === 'bug' ? 'active' : ''}" onclick="setFbCat('bug')">问题</button>
            <button class="yy-seg-btn ${cat === 'idea' ? 'active' : ''}" onclick="setFbCat('idea')">建议</button>
            <button class="yy-seg-btn ${cat === 'other' ? 'active' : ''}" onclick="setFbCat('other')">其他</button>
          </div>

          <div class="yy-card" style="border-radius:14px;overflow:hidden;">
            <textarea rows="4" placeholder="${placeholders[cat]}" style="width:100%;min-height:104px;padding:10px 12px 6px;font-size:14px;line-height:1.55;resize:none;"></textarea>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="padding:7px 10px;display:flex;align-items:center;gap:4px;">
              <button onclick="showProtoToast('已选择附件（最多 3 个）')" style="height:26px;padding:0 8px;border-radius:7px;font-size:12px;color:var(--yy-t2);display:inline-flex;align-items:center;gap:5px;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'">${ICONS.paperclip}<span>附件</span></button>
              <button onclick="showProtoToast('可去到要截的界面点「截图」')" style="height:26px;padding:0 8px;border-radius:7px;font-size:12px;color:var(--yy-t2);display:inline-flex;align-items:center;gap:5px;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'">${ICONS.camera}<span>截图</span></button>
              <span style="margin-left:auto;font-size:11px;color:var(--yy-t3);">支持 ${S.chrome === 'mac' ? '⌘V' : 'Ctrl+V'} 粘图 · 拖入文件</span>
            </div>
          </div>

          <div class="yy-card" style="border-radius:12px;overflow:hidden;">
            <div style="height:40px;padding:0 14px;display:flex;align-items:center;justify-content:space-between;gap:10px;">
              <button onclick="toggleFbInfoExpand()" style="display:inline-flex;align-items:center;gap:6px;text-align:left;">
                <span style="font-size:12.5px;color:var(--yy-t1);">附带设备信息</span>
                <span style="font-size:11.5px;color:var(--yy-t3);">语阅 0.1.0 · ${S.chrome === 'mac' ? 'macOS 15.4 · Mac' : 'Windows 11 · PC'}</span>
                <span style="transform:rotate(${S.fbInfoExpanded ? '180deg' : '0deg'});display:inline-flex;color:var(--yy-t3);">${ICONS.chevDown}</span>
              </button>
              <div class="yy-switch on" onclick="this.classList.toggle('on')"></div>
            </div>
            ${S.fbInfoExpanded ? `
              <div style="height:0.5px;background:var(--yy-line);margin:0 14px;"></div>
              <div style="padding:10px 14px;display:flex;gap:24px;">
                <div><div style="font-size:10.5px;color:var(--yy-t3);">版本</div><div style="font-size:11.5px;color:var(--yy-t2);margin-top:2px;">语阅 0.1.0 (build 42)</div></div>
                <div><div style="font-size:10.5px;color:var(--yy-t3);">系统</div><div style="font-size:11.5px;color:var(--yy-t2);margin-top:2px;">${S.chrome === 'mac' ? 'macOS 15.4' : 'Windows 11 24H2'}</div></div>
                <div><div style="font-size:10.5px;color:var(--yy-t3);">设备</div><div style="font-size:11.5px;color:var(--yy-t2);margin-top:2px;">${S.chrome === 'mac' ? 'Mac' : 'Windows x64'}</div></div>
              </div>
            ` : ''}
          </div>
          <div style="display:flex;align-items:center;gap:5px;padding:0 4px;font-size:11.5px;color:var(--yy-t3);">
            <span>需要回复或不想公开？</span>
            <a href="mailto:yuyue.app@icloud.com" style="color:var(--tint-accent);">直接发邮件 ↗</a>
          </div>
        </div>

        <!-- Footer -->
        <div style="border-top:0.5px solid var(--yy-line);padding:10px 20px 14px;display:flex;flex-direction:column;gap:10px;">
          <div style="display:flex;align-items:center;gap:4px;font-size:11px;color:var(--yy-t2);">
            <span style="color:var(--tint-accent);display:inline-flex;">${ICONS.globe}</span>
            <span>正文将公开在</span>
            <span style="color:var(--tint-accent);">GitHub Issues ↗</span>
            <span>· 日志和私密附件仅开发者可见</span>
          </div>
          <div style="display:flex;align-items:center;gap:8px;">
            <span style="font-size:11px;color:var(--yy-t3);">草稿已自动保存</span>
            <div style="margin-left:auto;display:flex;gap:8px;">
              <button class="yy-pill" onclick="setModal(null)">取消</button>
              <button class="yy-pill primary" onclick="setModal(null);showProtoToast('收到了，谢谢')">
                <span>发送</span>
                <span style="font-size:10px;opacity:0.75;margin-left:3px;">${S.chrome === 'mac' ? '⌘↩' : 'Ctrl+↩'}</span>
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>
  `;
}
