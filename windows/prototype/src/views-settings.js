// ============================================================================
// Window 5: MacSettings — Forensic 1:1 All 11 Pages, VoiceSourcePage,
// 18-Provider CompanionProviderSheet, WebDAVCheckCard, 4 ExportSamples,
// Two-Folio MacUpdateSettings (YuyueMark + PencilSignature) & SettingsHistoryCard
// ============================================================================

const SETTINGS_PAGES = [
  { id: 'general', title: '通用', group: null, ico: 'sliders', hasInspector: false, summary: '语言、启动与系统休眠' },
  { id: 'appearance', title: '外观', group: null, ico: 'sun', hasInspector: false, summary: '应用界面的外观与配色；书页纸色在「阅读」中设置' },
  { id: 'reading', title: '阅读', group: '阅读与朗读', ico: 'openBook', hasInspector: true, summary: '新书的默认排版；单本书可在阅读器「Aa」中单独调整' },
  { id: 'foreign', title: '外文', group: '阅读与朗读', ico: 'charBook', hasInspector: true, summary: '查词、难词行间释义、生词标记与离线翻译' },
  { id: 'listening', title: '朗读', group: '阅读与朗读', ico: 'headphones', hasInspector: true, summary: '选一个念中文书的声音，一个念英文书的声音' },
  { id: 'companion', title: '书伴', group: '连接', ico: 'bubble', hasInspector: true, summary: '章节导读、选中即问、跨书问答与生词复习所用的大模型' },
  { id: 'sync', title: '同步', group: '连接', ico: 'sync', hasInspector: false, summary: '通过云盘文件夹或你自己的网盘在多设备间同步，无需注册语阅账号' },
  { id: 'notes', title: '笔记导出', group: '连接', ico: 'share', hasInspector: true, summary: '将划线与想法导出到常用笔记工具，连接一次即可随时增量同步' },
  { id: 'storage', title: '存储', group: '数据与关于', ico: 'drive', hasInspector: true, summary: '书籍、音频与缓存占用的本机空间' },
  { id: 'data', title: '数据', group: '数据与关于', ico: 'box', hasInspector: true, summary: '生成单个 .yuyue 快照备份与阅读记录 CSV 导出' },
  { id: 'update', title: '关于', group: '数据与关于', ico: 'info', hasInspector: false, summary: '' }
];

const COMPANION_PROVIDERS_CONFIGURED = [
  { id: 'thisMac', title: '这台电脑', tile: 'thisMac', detail: '本机模型 · 免费免联网', active: false },
  { id: 'deepseek', title: 'DeepSeek', tile: 'deepseek', model: 'deepseek-chat', host: 'api.deepseek.com/v1', key: 'sk-••••9a2f', detail: 'deepseek-chat · Key sk-••••9a2f', active: true },
  { id: 'openrouter', title: 'OpenRouter', tile: 'openrouter', model: 'anthropic/claude-sonnet-4.5', host: 'openrouter.ai/api/v1', key: 'sk-or-••••31b0', detail: 'claude-sonnet-4.5 · 已授权账号', active: false },
  { id: 'claude', title: 'Claude', tile: 'claude', model: 'claude-sonnet-4-5', host: 'api.anthropic.com/v1', key: 'sk-ant-••••78c1', detail: 'claude-sonnet-4-5 · Key sk-ant-••••78c1', active: false },
  { id: 'ollama', title: 'Ollama', tile: 'ollama', model: 'qwen3:14b', host: '127.0.0.1:11434', key: '', detail: '本机 · 127.0.0.1:11434', active: false }
];

function renderSettingsWindow(S) {
  const cur = SETTINGS_PAGES.find(p => p.id === S.settingsPage) || SETTINGS_PAGES[0];
  let prevGroup = null;
  const winH = S.chrome === 'mac' ? 640 : 668;
  const inVoiceDrill = cur.id === 'listening' && !!S.voiceAuditionOpen;

  return `
    <div class="win-frame" style="width:980px;height:${winH}px;">
      ${renderWinTitleBar('settings', cur.hasInspector)}
      <div class="mac-lights" style="position:absolute;top:20px;left:22px;">
        <span class="mac-dot close"></span><span class="mac-dot min"></span><span class="mac-dot max"></span>
      </div>

      <div class="settings-shell" onclick="closeSettingsPopups()">
        <!-- 1. Left 184px SettingsSidebar (#FBFAF9 / #161719 + 0.5px right border) -->
        <aside class="settings-side">
          ${SETTINGS_PAGES.map(p => {
            let hdr = '';
            if (p.group && p.group !== prevGroup) {
              prevGroup = p.group;
              hdr = `<div class="settings-side-group">${p.group}</div>`;
            }
            const on = p.id === cur.id;
            return `
              ${hdr}
              <button class="settings-side-row ${on ? 'active' : ''}" onclick="setSettingsPage('${p.id}')">
                <span class="s-ico">${ICONS[p.ico]}</span>
                <span style="flex:1;text-align:left;">${p.title}</span>
                ${p.id === 'update' && S.updateOfferShown ? `<span style="width:7px;height:7px;border-radius:50%;background:var(--yy-danger);"></span>` : ''}
              </button>`;
          }).join('')}
        </aside>

        <!-- 2. Middle Content Column (544px with Inspector, 796px without Inspector!) -->
        <main class="settings-mid">
          ${cur.id !== 'update' && !inVoiceDrill ? `
            <div class="settings-page-header">
              <div>
                <div style="font-size:16px;font-weight:600;color:var(--yy-t1);">${cur.title}</div>
                <div style="font-size:11.5px;color:var(--yy-t2);margin-top:2px;">${cur.summary}</div>
              </div>
              ${!cur.hasInspector ? renderSettingsCorner(S, cur.id) : ''}
            </div>
          ` : ''}

          ${cur.id === 'update' ? `
            <div style="position:absolute;top:8px;right:20px;z-index:20;">${renderSettingsCorner(S, cur.id)}</div>
          ` : ''}

          <div style="flex:1;overflow-y:auto;padding:${inVoiceDrill ? '10px 20px 16px' : (cur.id === 'update' ? '28px 24px 20px 18px' : '6px 24px 28px')};display:flex;flex-direction:column;">
            ${renderSettingsMiddleBody(cur.id, S)}
          </div>
        </main>

        <!-- 3. Right 252px SettingsInspector -->
        ${cur.hasInspector ? `
          <aside class="settings-inspector">
            <div style="padding:8px 14px 6px 16px;display:flex;align-items:center;justify-content:flex-end;gap:6px;min-height:36px;">
              ${(cur.id === 'reading' || cur.id === 'foreign') ? `<button class="yy-pill xs" onclick="showProtoToast('已恢复「${cur.title}」默认设置', true)">恢复本页默认</button>` : ''}
              ${renderSettingsHistoryButton(S)}
            </div>
            ${renderSettingsInspectorBody(cur.id, S)}
          </aside>
        ` : ''}

        ${S.settingsHistoryOpen ? renderSettingsHistoryPopover(S) : ''}
      </div>

      ${renderSettingsModals(S)}
    </div>
  `;
}

function renderSettingsCorner(S) {
  return `
    <div style="display:flex;align-items:center;gap:6px;">
      ${renderSettingsHistoryButton(S)}
    </div>
  `;
}

function renderSettingsHistoryButton(S) {
  return `
    <button onclick="event.stopPropagation();toggleSettingsHistory()" title="最近改过 (${S.chrome === 'mac' ? '⌘Z' : 'Ctrl+Z'})" style="width:28px;height:28px;border-radius:8px;background:${S.settingsHistoryOpen ? 'var(--yy-inset)' : 'transparent'};display:inline-flex;align-items:center;justify-content:center;color:var(--yy-t2);position:relative;">
      ${ICONS.history}
      <span class="tabular" style="position:absolute;top:-3px;right:-4px;min-width:15px;height:15px;padding:0 3.5px;border-radius:99px;background:var(--tint-accent);color:#FFFFFF;font-size:9.5px;font-weight:600;line-height:15px;text-align:center;">3</span>
    </button>
  `;
}

// ============================================================================
// ModeThumbnail (AppearanceSettings — Verified 1:1 against testMacSettingsAppearance.1.png!)
// 84x54 miniature book page: 4.5px left spine shadow, 24x3 + 50/46/34x2 bars, 10x1.5 mark, 3.5x8.5 top-right ribbon!
// ============================================================================
function renderModeThumbnail(mode, label, active) {
  const innerPage = (bg, ink, spine) => `
    <div style="position:absolute;inset:0;background:${bg};display:flex;">
      <div style="width:4.5px;height:100%;background:${spine};"></div>
      <div style="flex:1;padding:10px 10px 7px 9px;display:flex;flex-direction:column;justify-content:space-between;position:relative;">
        <span style="position:absolute;top:0;right:9px;width:3.5px;height:8.5px;border-radius:0 0 1px 1px;background:var(--tint-accent);"></span>
        <div style="display:flex;flex-direction:column;gap:4px;">
          <span style="width:24px;height:3px;border-radius:1.5px;background:${ink};opacity:0.78;"></span>
          <span style="width:50px;height:2px;border-radius:1px;background:${ink};opacity:0.32;margin-top:2px;"></span>
          <span style="width:46px;height:2px;border-radius:1px;background:${ink};opacity:0.32;"></span>
          <span style="width:34px;height:2px;border-radius:1px;background:${ink};opacity:0.32;"></span>
        </div>
        <span style="width:10px;height:1.5px;border-radius:1px;background:${ink};opacity:0.25;"></span>
      </div>
    </div>
  `;

  let thumbBody = '';
  if (mode === 'light') {
    thumbBody = innerPage('#F6F3EB', '#2A2B2E', 'rgba(0,0,0,0.06)');
  } else if (mode === 'dark') {
    thumbBody = innerPage('#1D1E21', '#E5E5E7', 'rgba(255,255,255,0.06)');
  } else {
    // Diagonal split 60% top -> 40% bottom!
    thumbBody = `
      ${innerPage('#F6F3EB', '#2A2B2E', 'rgba(0,0,0,0.06)')}
      <div style="position:absolute;inset:0;clip-path:polygon(60% 0, 100% 0, 100% 100%, 40% 100%);">
        ${innerPage('#1D1E21', '#E5E5E7', 'rgba(255,255,255,0.06)')}
      </div>
    `;
  }

  return `
    <button onclick="setAppearanceMode('${mode}')" style="display:flex;flex-direction:column;align-items:center;gap:5px;">
      <span style="padding:2.5px;border-radius:9.5px;border:${active ? '1.75px solid var(--tint-accent)' : '1.75px solid transparent'};">
        <div style="width:84px;height:54px;border-radius:7px;overflow:hidden;position:relative;border:0.5px solid rgba(128,128,128,0.24);box-shadow:0 1px 2px rgba(0,0,0,0.05);">
          ${thumbBody}
        </div>
      </span>
      <span style="font-size:11.5px;color:${active ? 'var(--yy-t1)' : 'var(--yy-t2)'};font-weight:${active ? '600' : '400'};">${label}</span>
    </button>
  `;
}

// ============================================================================
// All 11 Pages Middle Column Bodies
// ============================================================================
function renderSettingsMiddleBody(page, S) {
  // ---------------- Page 1: 通用 (GeneralSettings) ----------------
  if (page === 'general') {
    const lang = S.appLang || 'zh';
    const langLabels = { zh: '中文', en: 'English', sys: '跟随系统' };
    return `
      <div style="display:flex;flex-direction:column;gap:12px;">
        <div class="settings-card" style="position:relative;">
          <!-- Row 1: MacLanguageCard (180x32 warm-paper trigger with 20x20 "文" seal badge) -->
          <div class="settings-row">
            <div>
              <div style="font-size:12.5px;font-weight:500;">语言</div>
              <div style="font-size:10.5px;color:var(--yy-t3);margin-top:1px;">当前：${langLabels[lang]} · 切换需确认重开，并回到当前页</div>
            </div>
            <div style="position:relative;" onclick="event.stopPropagation()">
              <button onclick="toggleLangMenu()" style="width:180px;height:32px;padding:0 10px 0 6px;border-radius:9px;background:var(--yy-pill);border:0.5px solid var(--yy-pill-line);display:flex;align-items:center;gap:8px;font-size:12px;">
                <span class="serif" style="width:20px;height:20px;border-radius:5px;background:var(--tint-soft);color:var(--tint-accent);font-size:11px;font-weight:700;display:inline-flex;align-items:center;justify-content:center;">${lang === 'en' ? 'A' : (lang === 'sys' ? '文A' : '文')}</span>
                <span style="font-weight:500;flex:1;text-align:left;">${langLabels[lang]}</span>
                ${lang === 'sys' ? `<span style="font-size:10px;color:var(--yy-t3);">中文</span>` : ''}
                <span style="color:var(--yy-t3);">${ICONS.chevDown}</span>
              </button>
              ${S.langMenuOpen ? `
                <div class="yy-float-card" style="position:absolute;top:36px;right:0;width:236px;padding:6px;border-radius:12px;z-index:80;">
                  ${[
                    ['zh', '文', '中文', '简体中文界面'],
                    ['en', 'A', 'English', '英文界面'],
                    ['sys', '文A', '跟随系统', '随系统语言自动切换 · 当前为中文']
                  ].map(opt => `
                    <button onclick="pickLanguage('${opt[0]}')" style="width:100%;padding:7px 8px;border-radius:8px;display:flex;align-items:center;gap:9px;text-align:left;background:${lang === opt[0] ? 'var(--tint-soft)' : 'transparent'};">
                      <span class="serif" style="width:22px;height:22px;border-radius:6px;background:var(--yy-card);border:0.5px solid var(--yy-line);font-size:10.5px;font-weight:700;display:inline-flex;align-items:center;justify-content:center;color:var(--tint-accent);">${opt[1]}</span>
                      <div style="flex:1;"><div style="font-size:12px;font-weight:600;">${opt[2]}</div><div style="font-size:10px;color:var(--yy-t3);">${opt[3]}</div></div>
                      ${lang === opt[0] ? `<span style="color:var(--tint-accent);">${ICONS.check}</span>` : ''}
                    </button>
                  `).join('')}
                </div>
              ` : ''}
            </div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">启动时继续上次阅读</div><div style="font-size:10.5px;color:var(--yy-t3);margin-top:1px;">关闭则进入书架</div></div>
            <div class="yy-switch" onclick="this.classList.toggle('on')"></div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">登录时后台启动</div><div style="font-size:10.5px;color:var(--yy-t3);margin-top:1px;">常驻托盘栏，不弹出窗口，按时生成播客</div></div>
            <div class="yy-switch on" onclick="this.classList.toggle('on')"></div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">朗读时防止系统休眠</div><div style="font-size:10.5px;color:var(--yy-t3);margin-top:1px;">锁屏或合上盖子仍可按系统电源策略控制</div></div>
            <div class="yy-switch on" onclick="this.classList.toggle('on')"></div>
          </div>
        </div>

        <div class="settings-heading"><span>导入</span></div>
        <div class="settings-card">
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">拖入书架即可导入</div><div style="font-size:10.5px;color:var(--yy-t3);margin-top:1px;">文件会复制到书库，移动或删除原文件不受影响</div></div>
            <span class="mono" style="font-size:11.5px;color:var(--yy-t3);letter-spacing:0.4px;">EPUB PDF MOBI AZW3 TXT</span>
          </div>
        </div>
      </div>`;
  }

  // ---------------- Page 2: 外观 (AppearanceSettings — 1:1 testMacSettingsAppearance.1.png!) ----------------
  if (page === 'appearance') {
    const appMode = S.appearanceMode || S.theme || 'light';
    return `
      <div style="display:flex;flex-direction:column;gap:12px;">
        <!-- Card 1: 界面 (3 ModeThumbnails: 浅色 | 深色 | 跟随系统) -->
        <div class="settings-card" style="padding:14px 16px;display:flex;align-items:center;justify-content:space-between;">
          <div style="font-size:12.5px;font-weight:500;">界面</div>
          <div style="display:flex;gap:12px;">
            ${renderModeThumbnail('light', '浅色', appMode === 'light')}
            ${renderModeThumbnail('dark', '深色', appMode === 'dark')}
            ${renderModeThumbnail('auto', '跟随系统', appMode === 'auto')}
          </div>
        </div>

        <!-- Card 2: 主题色 -->
        <div class="settings-card" style="padding:14px 16px;display:flex;align-items:center;justify-content:space-between;">
          <div>
            <div style="font-size:12.5px;font-weight:500;">主题色</div>
            <div style="font-size:10.5px;color:var(--yy-t3);margin-top:2px;">用于按钮、开关与进度标记</div>
          </div>
          <div style="display:flex;gap:16px;">
            ${[['camel','暖纸','#8A653F'],['sage','草木','#4F7657'],['mist','烟雨','#4F6D88'],['blush','海棠','#A5656F']].map(t => `
              <button onclick="setTint('${t[0]}')" style="display:flex;flex-direction:column;align-items:center;gap:5px;">
                <span style="padding:2px;border-radius:50%;border:${S.tint === t[0] ? `2px solid ${t[2]}` : '2px solid transparent'};display:inline-flex;">
                  <i style="width:26px;height:26px;border-radius:50%;background:${t[2]};display:block;"></i>
                </span>
                <span style="font-size:11.5px;color:${S.tint === t[0] ? 'var(--yy-t1)' : 'var(--yy-t2)'};font-weight:${S.tint === t[0] ? '600' : '400'};">${t[1]}</span>
              </button>
            `).join('')}
          </div>
        </div>

        <!-- Card 3: 书架封面大小 (with filled 8x11 & 11x16 mini book icons + filled slider track + 6 stepped tick dots!) & 封面下显示进度 -->
        <div class="settings-card">
          <div class="settings-row">
            <div>
              <div style="font-size:12.5px;font-weight:500;">书架封面大小</div>
              <div style="font-size:10.5px;color:var(--yy-t3);margin-top:1px;">每行本数随窗口宽度自动排布</div>
            </div>
            <div style="display:flex;align-items:center;gap:10px;">
              <span style="width:8px;height:11px;border-radius:2px;background:var(--yy-tint-neutral);display:inline-block;"></span>
              <div style="width:180px;display:flex;flex-direction:column;gap:4px;">
                <input type="range" class="yy-range" min="80" max="130" step="10" value="${S.coverSize || 100}" oninput="setShelfCoverSize(this.value)" style="width:100%;background:linear-gradient(90deg,var(--tint-fill) ${(((S.coverSize || 100) - 80) / 50 * 100).toFixed(0)}%,var(--yy-inset) ${(((S.coverSize || 100) - 80) / 50 * 100).toFixed(0)}%);" />
                <div style="display:flex;justify-content:space-between;padding:0 6px;">
                  ${[80,90,100,110,120,130].map(v => `<i style="width:2.5px;height:2.5px;border-radius:50%;background:var(--yy-tint-neutral);"></i>`).join('')}
                </div>
              </div>
              <span style="width:11px;height:16px;border-radius:2.5px;background:var(--yy-tint-neutral);display:inline-block;"></span>
            </div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div>
              <div style="font-size:12.5px;font-weight:500;">封面下显示进度</div>
              <div style="font-size:10.5px;color:var(--yy-t3);margin-top:1px;">关闭后仅显示书名</div>
            </div>
            <div class="yy-switch ${S.showShelfProgress !== false ? 'on' : ''}" onclick="toggleShelfProgressShow()"></div>
          </div>
        </div>

        <!-- Card 4: 液态玻璃效果 -->
        <div class="settings-card">
          <div class="settings-row">
            <div>
              <div style="font-size:12.5px;font-weight:500;">液态玻璃效果</div>
              <div style="font-size:10.5px;color:var(--yy-t3);margin-top:1px;">浮动工具栏与提示条使用光学液态玻璃，关闭则为毛玻璃</div>
            </div>
            <div class="yy-switch on" onclick="this.classList.toggle('on')"></div>
          </div>
        </div>
      </div>`;
  }

  // ---------------- Page 3: 阅读 (ReadingSettings — All 3 Cards + All 7 Immersive Rows!) ----------------
  if (page === 'reading') {
    const papers = [
      ['plain', '素白', '#FFFFFF', '#2A2B2E'],
      ['dark', '暗黑', '#121314', '#D6D6D8'],
      ['kami', '纸本', '#F5F4ED', '#26251F'],
      ['kaminight', '纸本 · 夜', '#1B1A17', '#E3DED0'],
      ['sage', '芦苇', '#E6E7DC', '#2A2C24'],
      ['eink', '铅纸', '#E9E8E3', '#141414']
    ];
    return `
      <div style="display:flex;flex-direction:column;gap:12px;">
        <!-- Card 1: 纸色 + 字体 + 字号 + 行距 -->
        <div class="settings-card">
          <div style="padding:12px 16px;">
            <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px;">
              <span style="font-size:12.5px;font-weight:500;">纸色</span>
              <button onclick="setModal('newPaperSheet')" style="font-size:11px;color:var(--tint-accent);font-weight:500;">+ 新建纸色</button>
            </div>
            <div style="display:grid;grid-template-columns:repeat(6,1fr);gap:8px;">
              ${papers.map(p => {
                const on = S.paper === p[0];
                return `
                  <button onclick="setPaper('${p[0]}')" style="display:flex;flex-direction:column;align-items:center;gap:4px;">
                    <span style="width:100%;height:32px;border-radius:8px;background:${p[2]};border:${on ? '1.75px solid var(--tint-accent)' : '0.5px solid rgba(128,128,128,0.22)'};display:flex;flex-direction:column;justify-content:center;padding:0 9px;gap:3.5px;">
                      <i style="width:24px;height:2px;border-radius:1px;background:${p[3]};opacity:0.65;display:block;"></i>
                      <i style="width:17px;height:2px;border-radius:1px;background:${p[3]};opacity:0.32;display:block;"></i>
                    </span>
                    <span style="font-size:10.5px;color:${on ? 'var(--yy-t1)' : 'var(--yy-t2)'};font-weight:${on ? '600' : '400'};">${p[1]}</span>
                  </button>`;
              }).join('')}
            </div>
          </div>
          <div class="settings-div"></div>
          <div style="padding:12px 16px;">
            <div style="font-size:12.5px;font-weight:500;margin-bottom:8px;">字体</div>
            <div style="display:grid;grid-template-columns:repeat(5,1fr);gap:8px;">
              ${[
                { id: 'sans', n: '系统', cls: '' },
                { id: 'serif', n: '宋体', cls: 'serif' },
                { id: 'kai', n: '楷体', cls: 'kai' },
                { id: 'wenkai', n: '霞鹜文楷', cls: 'kai' },
                { id: 'more', n: '更多字体', cls: 'serif', more: true }
              ].map(f => {
                const curF = S.readerFont || 'serif';
                const on = curF === f.id;
                return `
                  <button onclick="${f.more ? "showProtoToast('打开系统字体册选择字体')" : `setReaderFont('${f.id}')`}" style="padding:8px 4px 6px;border-radius:9px;border:${on ? '1.5px solid var(--tint-accent)' : '0.5px solid var(--yy-line)'};background:${on ? 'var(--tint-wash)' : 'transparent'};display:flex;flex-direction:column;align-items:center;gap:3px;">
                    <span class="${f.cls}" style="font-size:22px;line-height:26px;height:26px;">${f.more ? '永+' : '永'}</span>
                    <span style="font-size:10.5px;color:${on ? 'var(--tint-accent)' : 'var(--yy-t2)'};font-weight:${on ? '600' : '400'};">${f.n}</span>
                  </button>`;
              }).join('')}
            </div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row compact">
            <span style="font-size:12.5px;font-weight:500;">字号</span>
            <div style="display:flex;align-items:center;gap:10px;">
              <input type="range" class="yy-range" min="12" max="34" value="${S.fontSize || 18}" oninput="setReaderFontSize(this.value)" style="width:180px;" />
              <span class="mono tabular" style="width:22px;text-align:right;font-size:12px;color:var(--yy-t2);">${S.fontSize || 18}</span>
            </div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row compact">
            <span style="font-size:12.5px;font-weight:500;">行距</span>
            <div class="yy-seg sm" style="min-width:160px;">
              <button class="yy-seg-btn ${S.readerLh === 1.65 ? 'active' : ''}" onclick="setReaderLh(1.65)">紧</button>
              <button class="yy-seg-btn ${!S.readerLh ? 'active' : ''}" onclick="setReaderLh(null)">适中</button>
              <button class="yy-seg-btn ${S.readerLh === 2.3 ? 'active' : ''}" onclick="setReaderLh(2.3)">松</button>
            </div>
          </div>
        </div>

        <!-- Card 2: 翻页与版式 -->
        <div class="settings-card">
          <div class="settings-row compact">
            <span style="font-size:12.5px;font-weight:500;">翻页方式</span>
            <div class="yy-seg sm" style="min-width:160px;"><button class="yy-seg-btn active">左右翻页</button><button class="yy-seg-btn">上下滚动</button></div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">宽窗口下双栏显示</div><div style="font-size:10.5px;color:var(--yy-t3);">左右并排显示两页</div></div>
            <div class="yy-switch ${S.readerTwoCol !== false ? 'on' : ''}" onclick="setReaderTwoCol(S.readerTwoCol === false)"></div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row compact"><span style="font-size:12.5px;font-weight:500;">两端对齐</span><div class="yy-switch on" onclick="this.classList.toggle('on')"></div></div>
          <div class="settings-div"></div>
          <div class="settings-row compact"><span style="font-size:12.5px;font-weight:500;">首行缩进两字</span><div class="yy-switch on" onclick="this.classList.toggle('on')"></div></div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">中文标点挤压</div><div style="font-size:10.5px;color:var(--yy-t3);">收紧连续标点的留白</div></div>
            <div class="yy-switch on" onclick="this.classList.toggle('on')"></div>
          </div>
        </div>

        <!-- Card 3: 沉浸阅读 (All 7 rows from MacSettings.swift!) -->
        <div class="settings-heading"><span>沉浸阅读</span></div>
        <div class="settings-card">
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">进入与退出</div><div style="font-size:10.5px;color:var(--yy-t3);">全屏隐藏界面，指针移至底部唤出控制栏</div></div>
            <span style="font-size:12px;color:var(--yy-t2);">${S.chrome === 'mac' ? '⌃⌘F 或 Esc' : 'F11 或 Esc'}</span>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row compact"><span style="font-size:12.5px;font-weight:500;">打开书籍时自动进入</span><div class="yy-switch" onclick="this.classList.toggle('on')"></div></div>
          <div class="settings-div"></div>
          <div class="settings-row compact"><span style="font-size:12.5px;font-weight:500;">底部保留进度细线</span><div class="yy-switch on" onclick="this.classList.toggle('on')"></div></div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">隐藏划线与想法</div><div style="font-size:10.5px;color:var(--yy-t3);">退出沉浸后恢复显示</div></div>
            <div class="yy-switch" onclick="this.classList.toggle('on')"></div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row compact"><span style="font-size:12.5px;font-weight:500;">隐藏书伴提示与难词注释</span><div class="yy-switch" onclick="this.classList.toggle('on')"></div></div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">关闭翻页动画</div><div style="font-size:10.5px;color:var(--yy-t3);">直接切换整页，无滑动过渡</div></div>
            <div class="yy-switch" onclick="this.classList.toggle('on')"></div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">专注模式联动</div><div style="font-size:10.5px;color:var(--yy-t3);">阅读沉浸时自动免打扰</div></div>
            <button class="yy-pill xs" onclick="showProtoToast('打开系统专注助手设置')">专注设置…</button>
          </div>
        </div>
      </div>`;
  }

  // ---------------- Page 4: 外文 (ForeignSettings — All 5 Headed Cards!) ----------------
  if (page === 'foreign') {
    const lvl = S.foreignLevel || 3;
    return `
      <div style="display:flex;flex-direction:column;gap:10px;">
        <div class="settings-heading"><span>查词</span></div>
        <div class="settings-card">
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">内置词典</div><div style="font-size:10.5px;color:var(--yy-t3);">导入词典未命中时使用离线英汉词库</div></div>
            <span style="font-size:11.5px;color:var(--yy-ok);">✓ 已就绪</span>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row compact"><span style="font-size:12.5px;font-weight:500;">查词时自动发音</span><div class="yy-switch on" onclick="this.classList.toggle('on')"></div></div>
          <div class="settings-div"></div>
          <div class="settings-row compact">
            <span style="font-size:12.5px;font-weight:500;">英文发音</span>
            <div class="yy-seg sm" style="width:120px;"><button class="yy-seg-btn active">英音</button><button class="yy-seg-btn">美音</button></div>
          </div>
        </div>

        <div class="settings-heading"><span>MDict 词典</span></div>
        <div class="settings-card">
          <div class="settings-row compact">
            <div><div style="font-size:12.5px;font-weight:500;">牛津高阶英汉双解词典 (第10版)</div><div class="tabular" style="font-size:10.5px;color:var(--yy-t3);">142,860 个词条</div></div>
            <div class="yy-switch on" onclick="this.classList.toggle('on')"></div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">导入词典（.mdx）</div><div style="font-size:10.5px;color:var(--yy-t3);">按列表顺序优先查词，右键可调整先后或移除</div></div>
            <button class="yy-pill sm" onclick="showProtoToast('选择本地 .mdx / .mdd 词典文件')">选择文件…</button>
          </div>
        </div>

        <div class="settings-heading"><span>难词注释</span></div>
        <div class="settings-card">
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">难词行间释义</div><div style="font-size:10.5px;color:var(--yy-t3);">阅读英文书时，在较难词汇上方标注简短中文词义</div></div>
            <div class="yy-switch on" onclick="this.classList.toggle('on')"></div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">词汇难度</div><div style="font-size:10.5px;color:var(--yy-t3);">等级越高，仅标注更生僻的词（切换实时更新右侧样张）</div></div>
            <div class="yy-seg sm" style="width:248px;">
              ${[[2,'四级'],[3,'六级'],[4,'考研'],[5,'雅思'],[6,'GRE']].map(it => `
                <button class="yy-seg-btn ${lvl === it[0] ? 'active' : ''}" onclick="setForeignLevel(${it[0]})">${it[1]}</button>
              `).join('')}
            </div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row compact"><span style="font-size:12.5px;font-weight:500;">仅标注生词本中的词</span><div class="yy-switch" onclick="this.classList.toggle('on')"></div></div>
        </div>

        <div class="settings-heading"><span>生词</span></div>
        <div class="settings-card">
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">在书中标记生词</div><div style="font-size:10.5px;color:var(--yy-t3);">「在学」生词在所有书中以虚线标出，标为「已掌握」后不再标记</div></div>
            <div class="yy-switch on" onclick="this.classList.toggle('on')"></div>
          </div>
        </div>

        <div class="settings-heading"><span>翻译</span></div>
        <div class="settings-card">
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">离线翻译引擎</div><div style="font-size:10.5px;color:var(--yy-t3);">本地离线运行，原文不上传；供整页译文与双击整句翻译使用</div></div>
            <span style="font-size:12px;font-weight:500;color:var(--yy-ok);">✓ 英文 → 中文 已下载</span>
          </div>
        </div>
      </div>`;
  }

  // ---------------- Page 5: 朗读 (Root + Drill-In VoiceSourcePage Chat-Bubble Audition!) ----------------
  if (page === 'listening') {
    if (S.voiceAuditionOpen) {
      return renderVoiceSourcePage(S);
    }
    return `
      <div style="display:flex;flex-direction:column;gap:12px;">
        <div class="settings-heading">
          <span>声音来源</span>
          <span style="font-size:10.5px;font-weight:400;color:var(--yy-t3);">点一个，进去听它的声音</span>
        </div>
        <div class="settings-card">
          ${[
            { id: 'mac', tile: 'laptop', n: '这台电脑', d: '推荐 12 个自带声音 · 我的克隆 1 个 · 系统自带', pins: ['中文书 · 沉舟', '英文书 · Ava'] },
            { id: 'openai', tile: 'openai', n: 'OpenAI', d: '10 个音色，支持中英双语', pins: [], status: '已连接' },
            { id: 'azure', tile: 'azure', n: '微软 Azure', d: '中文音色丰富，含免费额度', pins: [], status: '未连接' },
            { id: 'elevenlabs', tile: 'elevenlabs', n: 'ElevenLabs', d: '自然度高，适合英文朗读', pins: [], status: '未连接' }
          ].map((s, i) => `
            ${i > 0 ? `<div class="settings-div" style="margin-left:58px;"></div>` : ''}
            <div onclick="setVoiceAuditionOpen(true, '${s.id}')" style="height:58px;padding:0 16px;display:flex;align-items:center;gap:12px;cursor:pointer;" onmouseover="this.style.background='rgba(42,43,46,0.03)'" onmouseout="this.style.background='transparent'">
              ${renderServiceTile(s.tile, 32)}
              <div style="flex:1;min-width:0;">
                <div style="font-size:13px;font-weight:500;">${s.n}</div>
                <div class="truncate" style="font-size:10.5px;color:var(--yy-t3);margin-top:1px;">${s.d}</div>
              </div>
              ${s.pins.length ? `
                <div style="display:flex;flex-direction:column;align-items:flex-end;gap:2px;font-size:11px;color:var(--yy-t2);">
                  ${s.pins.map(p => `<span style="display:inline-flex;align-items:center;gap:3px;">${ICONS.pin}<span>${p}</span></span>`).join('')}
                </div>
              ` : `<span style="font-size:11px;color:${s.status === '已连接' ? 'var(--yy-ok)' : 'var(--yy-t3)'};">${s.status}</span>`}
              <span style="color:var(--yy-t3);">${ICONS.chevRight}</span>
            </div>
          `).join('')}
        </div>
        <div style="font-size:11px;color:var(--yy-t3);padding:0 16px;line-height:1.55;">云端语音使用你自己的 API Key，费用由服务商直接结算；已合成的句子缓存在本机，重听不重复计费。</div>

        <!-- 广播剧工坊 (MacVoicePicker.swift L172) -->
        <div class="settings-heading">
          <span>广播剧工坊</span>
          <span style="font-size:10.5px;font-weight:400;color:var(--yy-t3);">只在这台电脑上跑，不传云端</span>
        </div>
        <div class="settings-card">
          <div class="settings-row">
            <div style="display:flex;align-items:center;gap:12px;">
              ${renderServiceTile('sparkle', 32)}
              <div>
                <div style="font-size:12.5px;font-weight:500;">精做的声音 <span style="font-size:10px;padding:1px 6px;border-radius:99px;background:rgba(62,155,106,0.14);color:var(--yy-ok);margin-left:4px;">已装好</span></div>
                <div style="font-size:10.5px;color:var(--yy-t3);margin-top:1px;">Qwen3-TTS 1.7B 本机模型 · 提前细做广播剧语气、为人物造专属声音</div>
              </div>
            </div>
            <button class="yy-pill xs" onclick="showProtoToast('管理本机广播剧语音模型')">管理</button>
          </div>
          <div class="settings-div" style="margin-left:58px;"></div>
          <div class="settings-row">
            <div style="display:flex;align-items:center;gap:12px;">
              ${renderServiceTile('phone', 32)}
              <div>
                <div style="font-size:12.5px;font-weight:500;">给我的手机提前做好</div>
                <div style="font-size:10.5px;color:var(--yy-t3);margin-top:1px;">接着电源时，附近手机正在听的书自动往后精做 10 章</div>
              </div>
            </div>
            <div class="yy-switch on" onclick="this.classList.toggle('on')"></div>
          </div>
        </div>

        <!-- 语速 -->
        <div class="settings-heading">
          <span>语速</span>
          <span style="font-size:10.5px;font-weight:400;color:var(--yy-t3);">读书窗口里调的也是这一个</span>
        </div>
        <div class="settings-card">
          <div class="settings-row compact">
            <div style="display:flex;align-items:center;gap:10px;">
              <span style="font-size:11.5px;color:var(--yy-t3);">慢</span>
              <input type="range" class="yy-range" min="8" max="20" value="10" style="width:150px;" />
              <span style="font-size:11.5px;color:var(--yy-t3);">快</span>
            </div>
            <button class="yy-pill xs tabular">1.0× ⌄</button>
          </div>
        </div>
      </div>`;
  }

  // ---------------- Page 6: 书伴 (CompanionSettings — Pure #FFFFFF ServiceTiles + 18-Provider Sheet!) ----------------
  if (page === 'companion') {
    const selId = S.companionSelected || 'deepseek';
    return `
      <div style="display:flex;flex-direction:column;gap:12px;">
        <div class="settings-heading"><span>模型服务</span></div>
        <div class="settings-card" style="padding:4px;">
          ${COMPANION_PROVIDERS_CONFIGURED.map((p, idx) => {
            const sel = selId === p.id;
            return `
              ${idx > 0 ? `<div style="height:0.5px;background:var(--yy-line);margin:0 12px 0 54px;"></div>` : ''}
              <div onclick="selectCompanionProvider('${p.id}')" style="min-height:52px;padding:8px 12px;border-radius:9px;background:${sel ? 'rgba(42,43,46,0.055)' : 'transparent'};display:flex;align-items:center;gap:12px;cursor:pointer;">
                ${renderServiceTile(p.tile, 30)}
                <div style="flex:1;min-width:0;">
                  <div style="display:flex;align-items:center;gap:6px;">
                    <span style="font-size:13px;font-weight:500;">${p.title}</span>
                    ${p.active ? `<span style="font-size:10px;padding:1px 7px;border-radius:99px;background:rgba(62,155,106,0.14);color:var(--yy-ok);font-weight:500;">当前使用</span>` : ''}
                  </div>
                  <div class="truncate" style="font-size:11px;color:var(--yy-t3);margin-top:1px;">${p.detail}</div>
                </div>
                <span style="color:var(--yy-t3);">${ICONS.chevRight}</span>
              </div>
            `;
          }).join('')}
          <div style="height:0.5px;background:var(--yy-line);margin:2px 12px 2px 54px;"></div>
          <div onclick="setModal('companionSheet')" style="min-height:52px;padding:8px 12px;border-radius:9px;display:flex;align-items:center;gap:12px;cursor:pointer;" onmouseover="this.style.background='rgba(42,43,46,0.035)'" onmouseout="this.style.background='transparent'">
            ${renderServiceTile('add', 30)}
            <div style="flex:1;min-width:0;">
              <div style="font-size:13px;font-weight:500;color:var(--yy-t1);">添加服务商</div>
              <div class="truncate" style="font-size:11px;color:var(--yy-t3);margin-top:1px;">Claude、OpenAI、DeepSeek、Gemini、Kimi、通义、本机 Ollama…</div>
            </div>
          </div>
        </div>
        <div style="font-size:11px;color:var(--yy-t3);padding:0 16px;line-height:1.6;">API Key 仅保存在这台电脑上，不同步、不上传；调用费用由服务商直接结算。播客文稿生成同样使用此处所选模型。</div>
      </div>`;
  }

  // ---------------- Page 7: 同步 (SyncSettings + Inline WebDAVForm + 4-Step WebDAVCheckCard Thread!) ----------------
  if (page === 'sync') {
    const method = S.syncMethod || 'webdav';
    return `
      <div style="display:flex;flex-direction:column;gap:12px;">
        <!-- Card 1: Status + 3-Col DeviceInfo Tiles -->
        <div class="settings-card" style="padding:14px 16px;display:flex;flex-direction:column;gap:12px;">
          <div style="display:flex;align-items:center;justify-content:space-between;">
            <div style="display:flex;align-items:center;gap:12px;">
              <span style="width:32px;height:32px;border-radius:50%;background:${S.syncConnected ? 'rgba(62,155,106,0.14)' : 'var(--yy-inset)'};color:${S.syncConnected ? 'var(--yy-ok)' : 'var(--yy-t3)'};display:flex;align-items:center;justify-content:center;">${S.syncConnected ? ICONS.check : ICONS.sync}</span>
              <div>
                <div style="font-size:13px;font-weight:600;">${S.syncConnected ? '已同步 · WebDAV' : '尚未开启同步'}</div>
                <div style="font-size:11px;color:var(--yy-t3);margin-top:1px;">${S.syncConnected ? '上次同步：刚刚 · 3 台设备在线' : '选择下方任一方式连接个人云盘，无需语阅账号'}</div>
              </div>
            </div>
            <button class="yy-pill ${S.syncConnected ? 'sm' : 'primary sm'}" onclick="toggleSyncDemo()">${S.syncConnected ? '立即同步' : '测试连接并开启'}</button>
          </div>
          ${S.syncConnected ? `
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="display:grid;grid-template-columns:repeat(3,1fr);gap:8px;">
              ${[
                ['Windows 台式机', '这台', '刚刚 · 《红楼梦》34%'],
                ['MacBook Pro', '', '12 分钟前 · 《呐喊》读完'],
                ['iPhone 16 Pro', '', '昨晚 · 《红楼梦》34%']
              ].map(d => `
                <div style="padding:8px 10px;border-radius:10px;background:var(--yy-inset);">
                  <div style="font-size:11.5px;font-weight:600;display:flex;align-items:center;gap:5px;">
                    <span>${d[0]}</span>
                    ${d[1] ? `<span style="font-size:9.5px;padding:0 5px;border-radius:4px;background:var(--yy-card);color:var(--tint-accent);">${d[1]}</span>` : ''}
                  </div>
                  <div class="truncate" style="font-size:10px;color:var(--yy-t3);margin-top:2px;">${d[2]}</div>
                </div>
              `).join('')}
            </div>
          ` : ''}
        </div>

        <!-- 同步方式: 3 methodCards -->
        <div class="settings-heading"><span>同步方式</span></div>
        <div style="display:grid;grid-template-columns:repeat(3,1fr);gap:12px;">
          ${[
            { id: 'folder', ico: ICONS.cloud, t: '个人云盘文件夹', d: '使用 OneDrive / iCloud 云盘同步目录' },
            { id: 'webdav', ico: ICONS.drive, t: 'WebDAV', d: '坚果云、群晖、Nextcloud…' },
            { id: 'gdrive', ico: ICONS.globe, t: 'Google Drive', d: '用你自己的 Google 账号跨端同步' }
          ].map(m => {
            const on = method === m.id;
            return `
              <div onclick="setSyncMethod('${m.id}')" class="settings-card" style="padding:14px;border:${on ? '1.5px solid var(--tint-accent)' : '0.5px solid var(--yy-card-border)'};cursor:pointer;">
                <span style="width:28px;height:28px;border-radius:8px;background:${on ? 'var(--tint-soft)' : 'var(--yy-inset)'};color:${on ? 'var(--tint-accent)' : 'var(--yy-t2)'};display:inline-flex;align-items:center;justify-content:center;margin-bottom:8px;">${m.ico}</span>
                <div style="font-size:12.5px;font-weight:600;">${m.t}</div>
                <div style="font-size:10.5px;color:var(--yy-t3);margin-top:3px;line-height:1.45;">${m.d}</div>
              </div>`;
          }).join('')}
        </div>

        ${method === 'webdav' ? `
          <!-- Inline WebDAVForm + 4-Step WebDAVCheckCard Vertical Thread (WebDAVCheckCard.swift:1-111!) -->
          <div style="display:grid;grid-template-columns:1.15fr 0.85fr;gap:12px;">
            <div class="settings-card">
              <div class="settings-row compact"><span style="color:var(--yy-t2);font-size:12px;">服务器地址</span><input value="https://dav.jianguoyun.com/dav/" class="mono" style="width:210px;text-align:right;font-size:11.5px;" /></div>
              <div class="settings-div"></div>
              <div class="settings-row compact"><span style="color:var(--yy-t2);font-size:12px;">账号</span><input value="reader@example.com" class="mono" style="width:190px;text-align:right;font-size:11.5px;" /></div>
              <div class="settings-div"></div>
              <div class="settings-row compact"><div><div style="color:var(--yy-t2);font-size:12px;">密码</div><div style="font-size:10px;color:var(--yy-t3);">坚果云填“应用密码”；只存在本机</div></div><input type="password" value="abcd1234efgh" style="width:120px;text-align:right;font-size:12px;" /></div>
              <div class="settings-div"></div>
              <div class="settings-row compact"><span style="color:var(--yy-t2);font-size:12px;">文件夹</span><input value="语阅" style="width:100px;text-align:right;font-size:12px;" /></div>
            </div>

            <!-- WebDAVCheckCard: 1px continuous vertical thread connecting 4 diagnostic steps! -->
            <div class="settings-card" style="padding:10px 14px;position:relative;display:flex;flex-direction:column;justify-content:space-between;">
              <span style="position:absolute;left:23px;top:22px;bottom:22px;width:1px;background:var(--yy-line);"></span>
              ${[
                ['能连上服务器', '38 ms'],
                ['证书有效', 'TLS 1.3'],
                ['账号和密码对', 'HTTP 207'],
                ['文件夹能读写', '/dav/语阅']
              ].map(st => `
                <div style="height:34px;display:flex;align-items:center;gap:10px;position:relative;z-index:1;">
                  <span style="width:19px;height:19px;border-radius:50%;background:${S.syncConnected ? 'rgba(62,155,106,0.16)' : 'var(--yy-card)'};border:${S.syncConnected ? 'none' : '1.2px dashed var(--yy-t3)'};color:var(--yy-ok);display:inline-flex;align-items:center;justify-content:center;flex-shrink:0;">
                    ${S.syncConnected ? ICONS.check.replace('width="12" height="12"', 'width="10" height="10"') : ''}
                  </span>
                  <span style="font-size:12px;flex:1;">${st[0]}</span>
                  <span class="mono tabular" style="font-size:10.5px;color:var(--yy-t3);">${S.syncConnected ? st[1] : '待检查'}</span>
                </div>
              `).join('')}
            </div>
          </div>
        ` : ''}

        <!-- 同步内容 (All 5 rows!) -->
        <div class="settings-heading"><span>同步内容</span></div>
        <div class="settings-card">
          <div class="settings-row compact"><span style="font-size:12.5px;">进度和书签</span><div class="yy-switch on" onclick="this.classList.toggle('on')"></div></div>
          <div class="settings-div"></div>
          <div class="settings-row compact"><span style="font-size:12.5px;">笔记和生词</span><div class="yy-switch on" onclick="this.classList.toggle('on')"></div></div>
          <div class="settings-div"></div>
          <div class="settings-row compact"><span style="font-size:12.5px;">设置和替换规则</span><div class="yy-switch on" onclick="this.classList.toggle('on')"></div></div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;">书文件</div><div style="font-size:10.5px;color:var(--yy-t3);">86.7 MB · 占用你的网盘空间；播客单集体积很小，始终同步</div></div>
            <div class="yy-switch on" onclick="this.classList.toggle('on')"></div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;">有声书录音</div><div style="font-size:10.5px;color:var(--yy-t3);">186.4 MB · 关闭时仅同步句级对齐信息，其他设备导入同名音频即可直接跟读</div></div>
            <div class="yy-switch" onclick="this.classList.toggle('on')"></div>
          </div>
        </div>

        <div style="display:flex;align-items:center;gap:6px;padding:2px 16px;font-size:11.5px;color:var(--yy-t3);">
          ${ICONS.lock}<span>无需注册账号，数据仅在你的设备与个人网盘之间传输</span>
        </div>
      </div>`;
  }

  // ---------------- Page 8: 笔记导出 (Official ExportTiles + Clickable Target Switching!) ----------------
  if (page === 'notes') {
    const selTarget = S.noteExportTarget || 'obsidian';
    const targets = [
      { id: 'obsidian', title: 'Obsidian', d: 'D:\\Obsidian\\Knowledge\\语阅 · 写入本地库 Markdown 文件，无需联网', on: true },
      { id: 'notion', title: 'Notion', d: '每本书生成一页，增量追加不重复', on: false },
      { id: 'flomo', title: 'flomo', d: '已填入专属链接 · 每条笔记生成一张带标签的卡片', on: true },
      { id: 'xinzhi', title: '新枝', d: '按书籍归档摘录，便于后续回顾', on: false }
    ];
    return `
      <div style="display:flex;flex-direction:column;gap:12px;">
        <div class="settings-heading">
          <span>导出目标</span>
          <span style="font-size:10.5px;font-weight:400;color:var(--yy-t3);">点选任一工具，右侧实时预览导出样张</span>
        </div>
        <div class="settings-card" style="padding:4px;">
          ${targets.map((x, idx) => {
            const sel = selTarget === x.id;
            return `
              ${idx > 0 ? `<div style="height:0.5px;background:var(--yy-line);margin:0 12px 0 54px;"></div>` : ''}
              <div onclick="selectNoteExportTarget('${x.id}')" style="min-height:54px;padding:9px 12px;border-radius:9px;background:${sel ? 'rgba(42,43,46,0.055)' : 'transparent'};display:flex;align-items:center;gap:12px;cursor:pointer;">
                ${renderExportTile(x.id, 30)}
                <div style="flex:1;min-width:0;">
                  <div style="font-size:13px;font-weight:500;">${x.title}</div>
                  <div class="truncate" style="font-size:11px;color:var(--yy-t3);margin-top:1px;">${x.d}</div>
                </div>
                <div class="yy-switch ${x.on ? 'on' : ''}" onclick="event.stopPropagation();this.classList.toggle('on')"></div>
              </div>`;
          }).join('')}
        </div>
        <div style="font-size:11px;color:var(--yy-t3);padding:0 16px;line-height:1.6;">开启的项目会显示在笔记页「导出」菜单中。令牌与目录仅保存在本机，不同步、不上传。</div>
      </div>`;
  }

  // ---------------- Page 9: 存储 (5-Category Bar + 3 Cleanup Rows + 各书占用空间!) ----------------
  if (page === 'storage') {
    return `
      <div style="display:flex;flex-direction:column;gap:12px;">
        <div class="settings-card">
          <div style="padding:16px;">
            <div style="display:flex;align-items:baseline;gap:8px;">
              <span style="font-size:13px;color:var(--yy-t2);">已用空间</span>
              <strong class="tabular" style="font-size:22px;">568.4 MB</strong>
              <span class="tabular" style="margin-left:auto;font-size:11.5px;color:var(--yy-t3);">本机可用 412 GB</span>
            </div>
            <!-- 5-Category Stacked Capsule Bar (StorageSettings L1265!) -->
            <div style="margin:12px 0 11px;height:8px;border-radius:99px;display:flex;gap:2px;overflow:hidden;">
              <span style="width:18%;background:var(--tint-fill);"></span>
              <span style="width:33%;background:var(--tint-fill);opacity:0.55;"></span>
              <span style="width:38%;background:var(--yy-t3);"></span>
              <span style="width:6%;background:var(--yy-danger);opacity:0.7;"></span>
              <span style="width:5%;background:var(--yy-tint-neutral);"></span>
            </div>
            <div style="display:flex;flex-wrap:wrap;gap:10px 16px;font-size:11px;color:var(--yy-t2);">
              <span style="display:inline-flex;align-items:center;gap:5px;"><i style="width:7px;height:7px;border-radius:50%;background:var(--tint-fill);"></i>书文件 86.7 MB</span>
              <span style="display:inline-flex;align-items:center;gap:5px;"><i style="width:7px;height:7px;border-radius:50%;background:var(--tint-fill);opacity:0.55;"></i>有声书录音 186.4 MB</span>
              <span style="display:inline-flex;align-items:center;gap:5px;"><i style="width:7px;height:7px;border-radius:50%;background:var(--yy-t3);"></i>对齐模型 268 MB</span>
              <span style="display:inline-flex;align-items:center;gap:5px;"><i style="width:7px;height:7px;border-radius:50%;background:var(--yy-danger);opacity:0.7;"></i>已删除 18.2 MB</span>
              <span style="display:inline-flex;align-items:center;gap:5px;"><i style="width:7px;height:7px;border-radius:50%;background:var(--yy-tint-neutral);"></i>其他 9.1 MB</span>
            </div>
          </div>

          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">最近删除 · 1 本（18.2 MB）</div><div style="font-size:10.5px;color:var(--yy-t3);">保留 30 天以便恢复，到期自动清理</div></div>
            <button class="yy-pill danger xs" onclick="showProtoToast('已清空最近删除')">立即清空</button>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">语音对齐模型</div><div style="font-size:10.5px;color:var(--yy-t3);">用于有声书句级对齐；移除后下次合成时重新下载（约 270 MB）</div></div>
            <div style="display:flex;align-items:center;gap:8px;"><span class="mono tabular" style="font-size:11.5px;color:var(--yy-t2);">268 MB</span><button class="yy-pill danger xs">移除</button></div>
          </div>
          <div class="settings-div"></div>
          <div class="settings-row">
            <div><div style="font-size:12.5px;font-weight:500;">书伴问答缓存</div><div style="font-size:10.5px;color:var(--yy-t3);">同章节重复提问直接读取本地缓存，不重复调用模型</div></div>
            <div style="display:flex;align-items:center;gap:8px;"><span class="mono tabular" style="font-size:11.5px;color:var(--yy-t2);">6.4 MB</span><button class="yy-pill danger xs" onclick="showProtoToast('已清理问答缓存')">清理</button></div>
          </div>
        </div>

        <div class="settings-heading"><span>各书占用空间</span></div>
        <div class="settings-card">
          ${[...BOOKS].sort((a, b) => parseFloat(b.bytes) - parseFloat(a.bytes)).slice(0, 6).map((b, i) => `
            ${i > 0 ? `<div class="settings-div"></div>` : ''}
            <div class="settings-row compact">
              <span class="truncate" style="font-size:12.5px;">${b.title}</span>
              <span class="mono tabular" style="font-size:11.5px;color:var(--yy-t2);">${b.bytes}</span>
            </div>
          `).join('')}
        </div>
      </div>`;
  }

  // ---------------- Page 10: 数据 (5-Toggle Snapshot Card + CSV Export + 历史快照!) ----------------
  if (page === 'data') {
    const withBooks = !!S.snapshotIncludeBooks;
    return `
      <div style="display:flex;flex-direction:column;gap:12px;">
        <div class="settings-heading"><span>生成快照</span></div>
        <div class="settings-card">
          ${[
            ['进度和书签', '9 本', true, null],
            ['笔记', '4 条', true, null],
            ['生词', '4 个词', true, null],
            ['设置和替换规则', '', true, null],
            ['书文件', '86.7 MB', withBooks, 'toggleSnapshotBooks()']
          ].map((r, i) => `
            ${i > 0 ? `<div class="settings-div"></div>` : ''}
            <div class="settings-row compact">
              <div>
                <span style="font-size:12.5px;font-weight:500;">${r[0]}</span>
                ${r[1] ? `<span class="tabular" style="font-size:11px;color:var(--yy-t3);margin-left:6px;">${r[1]}</span>` : ''}
              </div>
              <div class="yy-switch ${r[2] ? 'on' : ''}" onclick="${r[3] || "this.classList.toggle('on')"}"></div>
            </div>
          `).join('')}
          <div style="height:0.5px;background:var(--yy-line);"></div>
          <div style="padding:11px 16px;display:flex;align-items:center;justify-content:space-between;">
            <span style="font-size:12px;color:var(--yy-t3);">${withBooks ? '含书文件 · 约 ' : '不含书文件 · 约 '}<strong class="tabular" style="color:var(--yy-t1);">${withBooks ? '88.1 MB' : '1.4 MB'}</strong></span>
            <div style="display:flex;gap:8px;">
              <button class="yy-pill sm" onclick="setModal('snapshotImport')">导入快照…</button>
              <button class="yy-pill primary sm" onclick="showProtoToast('已生成快照 2026-11-23.yuyue')">生成快照</button>
            </div>
          </div>
        </div>

        <div class="settings-card">
          <div class="settings-row">
            <div>
              <div style="font-size:12.5px;font-weight:500;">导出阅读记录</div>
              <div style="font-size:10.5px;color:var(--yy-t3);margin-top:1px;">导出想读、在读、读完状态与完成日期（CSV），可导入 Goodreads 或 StoryGraph</div>
            </div>
            <button class="yy-pill sm" onclick="setModal('readingCsvExport')">导出…</button>
          </div>
        </div>

        <div class="settings-heading"><span>历史快照</span></div>
        <div class="settings-card">
          ${[
            ['11 月 22 日 21:40', '这台电脑 · 含书文件 · 87.9 MB'],
            ['11 月 10 日 09:15', '这台电脑 · 1.2 MB']
          ].map((s, i) => `
            ${i > 0 ? `<div class="settings-div"></div>` : ''}
            <div class="settings-row compact">
              <div><div style="font-size:12.5px;font-weight:500;">${s[0]}</div><div style="font-size:10.5px;color:var(--yy-t3);">${s[1]}</div></div>
              <div style="display:flex;align-items:center;gap:8px;">
                <button class="yy-pill xs" onclick="setModal('snapshotImport')">导入</button>
                <button style="color:var(--yy-t2);" title="分享">${ICONS.share}</button>
              </div>
            </div>
          `).join('')}
        </div>
        <div style="font-size:11px;color:var(--yy-t3);padding:0 16px;">快照为单个 .yuyue 文件，在 Windows、Mac、iPhone 与 Android 上打开即可恢复书库。</div>
      </div>`;
  }

  // ---------------- Page 11: 关于 (Exact Two-Folio Bookplate — MacUpdate.swift L121-531!) ----------------
  if (page === 'update') {
    return `
      <div style="display:flex;align-items:flex-start;gap:38px;">
        <!-- Left Folio: 书扉 (width: 214px — YuyueMark + 语阅 + Yuyue + 24x1.5 rule + 3-line creed + PencilSignature!) -->
        <div style="width:214px;flex-shrink:0;display:flex;flex-direction:column;align-items:flex-start;">
          ${renderYuyueMark(54)}
          <div class="serif" style="font-size:28px;font-weight:600;letter-spacing:3.9px;color:var(--yy-t1);margin-top:16px;line-height:1.15;">语阅</div>
          <div class="georgia" style="font-size:13px;font-style:italic;letter-spacing:0.26px;color:var(--yy-t3);margin-top:5px;">${S.chrome === 'mac' ? 'Yuyue for macOS' : 'Yuyue for Windows'}</div>

          <div style="width:24px;height:1.5px;background:var(--yy-about-hair);margin:18px 0 16px;"></div>

          <div class="serif" style="font-size:13.5px;line-height:2.05;color:var(--yy-t2);">
            读着听，听着读，进度接得上。<br/>
            没有服务器，不收集数据，<br/>
            书与笔记都留在你手里。
          </div>

          <div style="height:28px;"></div>

          <button onclick="replaySignature()" title="点一下重绘铅笔签名 (CTT)" style="text-align:left;opacity:0.9;">
            ${renderPencilSignature(58, !!S.sigAnimating)}
            <div style="font-size:11px;letter-spacing:0.22px;color:var(--yy-t3);margin-top:4px;">by <span style="color:var(--yy-t2);text-decoration:underline;text-underline-offset:2px;">nowtin</span> · GPL-3.0</div>
          </button>
        </div>

        <!-- Right Folio: 牌记 (width: 468px — UpdatePaper offer card + AboutCard 1 + AboutCard 2) -->
        <div style="width:468px;display:flex;flex-direction:column;gap:12px;">
          ${S.updateOfferShown ? `
            <!-- Conditional UpdatePaper offerCard (#F5F4ED / #1B1A17, 13px radius) -->
            <div style="padding:14px 16px;border-radius:13px;background:var(--yy-up-bg);border:0.5px solid var(--yy-up-line);display:flex;flex-direction:column;gap:8px;">
              <div style="display:flex;align-items:center;gap:8px;">
                <span class="serif" style="font-size:18px;font-weight:600;color:var(--yy-up-ink);">第 0.2.0 版</span>
                <span style="font-size:10px;padding:1px 7px;border-radius:99px;background:var(--tint-soft);color:var(--tint-accent);font-weight:600;">可以安装</span>
                <span class="tabular" style="margin-left:auto;font-size:11px;color:var(--yy-up-ink2);">2026.11.23 · 38 MB</span>
              </div>
              <div style="display:flex;flex-direction:column;gap:4px;font-size:12px;color:var(--yy-up-ink);">
                <div style="display:flex;align-items:center;gap:7px;"><i style="width:5px;height:5px;border-radius:50%;background:var(--tint-accent);"></i>新增「朗读」对话气泡式试听墙与 8 秒本机声音克隆</div>
                <div style="display:flex;align-items:center;gap:7px;"><i style="width:5px;height:5px;border-radius:50%;background:var(--tint-accent);"></i>新增 Obsidian / Notion / flomo / 新枝 实时导出样张</div>
              </div>
              <div style="display:flex;align-items:center;gap:8px;margin-top:4px;">
                <button class="yy-pill primary sm" onclick="showProtoToast('正在下载第 0.2.0 版更新包…')">下载并安装</button>
                <button class="yy-pill sm" onclick="toggleUpdateOffer(false)">稍后提醒</button>
                <button style="margin-left:auto;font-size:11.5px;color:var(--yy-up-ink2);" onclick="toggleUpdateOffer(false)">跳过此版本</button>
              </div>
            </div>
          ` : ''}

          <!-- AboutCard 1 -->
          <div class="settings-card" style="border-radius:13px;">
            <div style="padding:13px 16px;display:flex;align-items:center;justify-content:space-between;">
              <div>
                <div style="display:flex;align-items:baseline;gap:8px;">
                  <span class="serif" style="font-size:17px;font-weight:600;">第 0.1.0 版</span>
                  <span class="mono" style="font-size:11.5px;color:var(--yy-t3);">build 42</span>
                </div>
                <div style="font-size:11px;color:var(--yy-ok);margin-top:2px;">已是最新 · 上次检查 刚刚</div>
              </div>
              <button class="yy-pill sm" onclick="toggleUpdateOffer(true)">检查更新</button>
            </div>
            <div class="settings-div"></div>
            <div class="settings-row compact" style="cursor:pointer;" onclick="setModal('changelogSheet')">
              <span style="font-size:12.5px;">更新日志</span>
              <span class="tabular" style="font-size:12px;color:var(--yy-t3);">2026.11.23 · 6 项 ›</span>
            </div>
            <div class="settings-div"></div>
            <div class="settings-row">
              <div><div style="font-size:12.5px;">自动检查更新</div><div style="font-size:10.5px;color:var(--yy-t3);">每天启动时检查一次，仅读取版本号，不上传本地数据</div></div>
              <div class="yy-switch on" onclick="this.classList.toggle('on')"></div>
            </div>
            <div class="settings-div"></div>
            <div class="settings-row">
              <div><div style="font-size:12.5px;">更新渠道</div><div style="font-size:10.5px;color:var(--yy-t3);">测试版先拿到新功能；换回正式版后随下个正式版恢复</div></div>
              <div class="yy-seg sm" style="width:128px;"><button class="yy-seg-btn active">正式版</button><button class="yy-seg-btn">测试版</button></div>
            </div>
          </div>

          <!-- AboutCard 2 -->
          <div class="settings-card" style="border-radius:13px;">
            <div class="settings-row">
              <div><div style="font-size:12.5px;">问题与建议</div><div style="font-size:10.5px;color:var(--yy-t3);">可附截图或日志直达开发者，无需注册账号</div></div>
              <div style="display:flex;align-items:center;gap:10px;">
                <span style="font-size:12px;color:var(--yy-t2);">GitHub ↗</span>
                <button class="yy-pill sm" onclick="setModal('feedback')">写反馈…</button>
              </div>
            </div>
            <div class="settings-div"></div>
            <div class="settings-row compact" style="cursor:pointer;" onclick="setModal('privacySheet')">
              <span style="font-size:12.5px;">隐私说明</span>
              <span style="font-size:12px;color:var(--yy-t3);">仅存本机 · 无统计 ›</span>
            </div>
            <div class="settings-div"></div>
            <div class="settings-row compact" style="cursor:pointer;" onclick="setModal('creditsSheet')">
              <span style="font-size:12.5px;">开源许可与致谢</span>
              <span style="font-size:12px;color:var(--yy-t3);">GPL-3.0 与组件清单 ›</span>
            </div>
            <div class="settings-div"></div>
            <div class="settings-row compact">
              <span style="font-size:12.5px;">源代码</span>
              <span class="mono" style="font-size:11.5px;color:var(--tint-accent);">github.com/inokoru/yuyue ↗</span>
            </div>
          </div>
        </div>
      </div>`;
  }
  return '';
}

// ============================================================================
// VoiceSourcePage — Chat-Bubble Audition Layout (MacVoicePicker.swift:225-938!)
// ============================================================================
function renderVoiceSourcePage(S) {
  const src = S.voiceSourceTab || 'mac';
  const langBook = S.voiceBookLang || 'zh';
  const zhQuotes = [
    ['江上之清风，与山间之明月，耳得之而为声，目遇之而成色。', '苏轼《赤壁赋》'],
    ['庭下如积水空明，水中藻、荇交横，盖竹柏影也。', '苏轼《记承天寺夜游》'],
    ['一个人只拥有此生此世是不够的，他还应该拥有诗意的世界。', '王小波《万寿寺》'],
    ['醉后不知天在水，满船清梦压星河。', '唐温如《题龙阳县青草湖》']
  ];
  const enQuotes = [
    ['I declare after all there is no enjoyment like reading!', 'Jane Austen · Pride and Prejudice'],
    ['Whatever our souls are made of, his and mine are the same.', 'Emily Brontë · Wuthering Heights'],
    ['Only that day dawns to which we are awake. There is more day to dawn.', 'Henry David Thoreau · Walden']
  ];
  const pool = langBook === 'zh' ? zhQuotes : enQuotes;
  const daily = pool[(S.dailyQuoteIdx || 0) % pool.length];
  const hasCustom = Boolean(S.voiceCustomLine);
  const promptLine = S.voiceCustomLine || daily[0];
  const voices = [
    { id: 'chenzhou', name: '沉舟', mark: '默认旁白', sum: '温润叙事男声', dur: '5″', bg: '#E3EAEF', fg: '#2F475B', heard: true, pinned: true },
    { id: 'suwan', name: '苏晚', mark: '推荐', sum: '清澈书卷女声', dur: '5″', bg: '#F2E4E6', fg: '#7A3E48', heard: false, pinned: false },
    { id: 'linshen', name: '林深', mark: '推荐', sum: '松弛夜谈男声', dur: '4″', bg: '#E4ECE4', fg: '#39573D', heard: false, pinned: false },
    { id: 'myclone', name: '我的声音', mark: '本机克隆', sum: '8 秒采样 · 不出本机', dur: '5″', bg: '#EFE8DC', fg: '#6E5233', heard: true, pinned: false }
  ];

  return `
    <div style="display:flex;flex-direction:column;height:100%;gap:9px;position:relative;">
      <!-- Top Bar: SquareBackButton + SourceTabs + Gear -->
      <div style="display:flex;align-items:center;gap:8px;">
        <button onclick="setVoiceAuditionOpen(false)" title="返回声音来源" style="width:28px;height:28px;border-radius:7px;background:var(--yy-card);border:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:center;">${ICONS.chevLeft}</button>
        <div style="display:inline-flex;gap:2px;padding:2px;border-radius:8px;background:var(--yy-inset);">
          ${[['mac','laptop','这台电脑'],['openai','openai','OpenAI']].map(tb => `
            <button onclick="setVoiceAuditionOpen(true,'${tb[0]}')" style="height:26px;padding:0 9px 0 6px;border-radius:6.5px;background:${src === tb[0] ? 'var(--yy-card)' : 'transparent'};display:inline-flex;align-items:center;gap:6px;font-size:12px;font-weight:${src === tb[0] ? '600' : '400'};">
              ${renderServiceTile(tb[1], 18)}<span>${tb[2]}</span>
            </button>
          `).join('')}
        </div>
        <button onclick="showProtoToast('切换声音来源高级参数')" style="margin-left:auto;width:28px;height:28px;border-radius:7px;background:var(--yy-card);border:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:center;color:var(--yy-t2);">${ICONS.gear}</button>
      </div>

      <!-- Sub-bar: [中文书 | 英文书] + hint -->
      <div style="display:flex;align-items:center;justify-content:space-between;">
        <div class="yy-seg sm" style="width:136px;">
          <button class="yy-seg-btn ${langBook === 'zh' ? 'active' : ''}" onclick="setVoiceBookLang('zh')">中文书</button>
          <button class="yy-seg-btn ${langBook === 'en' ? 'active' : ''}" onclick="setVoiceBookLang('en')">英文书</button>
        </div>
        <span style="font-size:11px;color:var(--yy-t3);">点语音听，指着点“选择”</span>
      </div>

      <!-- PinnedVoice Bar (44px, r14) -->
      <div style="height:44px;padding:0 12px;border-radius:14px;background:linear-gradient(90deg,rgba(227,234,239,0.5),var(--yy-card));border:0.5px solid rgba(47,71,91,0.18);display:flex;align-items:center;gap:9px;">
        <div style="position:relative;width:28px;height:28px;border-radius:50%;background:#E3EAEF;color:#2F475B;display:flex;align-items:center;justify-content:center;font-weight:600;" class="serif">
          沉
          <span style="position:absolute;right:-3px;bottom:-2px;width:14px;height:14px;border-radius:50%;background:var(--yy-t1);color:var(--yy-card);display:flex;align-items:center;justify-content:center;">${ICONS.pin.replace('width="11" height="11"', 'width="8" height="8"')}</span>
        </div>
        <strong style="font-size:12.5px;">沉舟</strong>
        <button onclick="selectAuditionVoice('chenzhou')" style="height:28px;padding:0 10px 0 5px;border-radius:99px;background:var(--yy-t1);color:var(--yy-card);display:inline-flex;align-items:center;gap:6px;">
          <span style="width:18px;height:18px;border-radius:50%;background:var(--yy-card);color:var(--yy-t1);display:inline-flex;align-items:center;justify-content:center;">${ICONS.play.replace('width="12" height="12"', 'width="9" height="9"')}</span>
          <span style="display:inline-flex;align-items:center;gap:1.5px;height:12px;">${[5,9,11,7,10,6,8,4].map(h => `<i style="width:2px;height:${h}px;background:currentColor;border-radius:1px;display:block;"></i>`).join('')}</span>
          <span class="tabular" style="font-size:10.5px;">5″</span>
        </button>
        <span style="margin-left:auto;font-size:11px;color:var(--yy-t2);display:inline-flex;align-items:center;gap:4px;">${ICONS.pin}<span>念你的${langBook === 'zh' ? '中文书' : '英文书'}</span></span>
      </div>

      <!-- DailyQuoteCard by default; User Prompt Bubble ("我") only when user sends custom line -->
      ${hasCustom ? `
      <div style="display:flex;justify-content:flex-end;align-items:flex-start;gap:9px;padding-left:60px;">
        <div class="${langBook === 'zh' ? 'serif' : 'georgia'}" style="max-width:290px;padding:8px 12px;border-radius:14px;background:var(--yy-t1);color:var(--yy-card);font-size:12.5px;line-height:1.55;">
          ${promptLine}
        </div>
        <span style="width:34px;height:34px;border-radius:50%;background:var(--yy-inset);border:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:center;font-size:12px;font-weight:600;color:var(--yy-t2);flex-shrink:0;">我</span>
      </div>
      ` : `
      <button onclick="cycleDailyQuote()" style="width:100%;padding:9px 12px;border-radius:12px;background:var(--yy-card);border:0.5px solid var(--yy-line);text-align:left;display:flex;flex-direction:column;gap:5px;box-shadow:0 1px 2px rgba(0,0,0,0.03);">
        <div style="display:flex;align-items:center;gap:5px;width:100%;font-size:10.5px;">
          <span style="font-weight:600;color:var(--yy-t2);">“ 每日一言</span>
          <span style="color:var(--yy-t3);overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">· ${daily[1]}</span>
          <span style="margin-left:auto;color:var(--yy-t3);font-weight:500;">↻ 换一句</span>
        </div>
        <div class="${langBook === 'zh' ? 'serif' : 'georgia'}" style="font-size:12.5px;line-height:1.55;color:var(--yy-t1);">${daily[0]}</div>
      </button>
      `}

      <!-- Scrolling Voice Feed -->
      <div style="flex:1;overflow-y:auto;display:flex;flex-direction:column;gap:10px;padding:2px 2px 6px;">
        <div style="display:flex;justify-content:center;">
          <button onclick="setModal('cloneVoiceSheet')" style="height:28px;padding:0 13px;border-radius:99px;background:var(--yy-card);border:0.5px solid var(--yy-line);font-size:11.5px;font-weight:500;box-shadow:0 1px 2px rgba(0,0,0,0.04);">+ 克隆一个声音 · 念 8 秒，声音不出本机</button>
        </div>

        <div style="display:flex;align-items:center;gap:8px;font-size:10.5px;color:var(--yy-t3);">
          <span style="flex:1;height:0.5px;background:var(--yy-line);"></span><span>推荐与我的声音</span><span style="flex:1;height:0.5px;background:var(--yy-line);"></span>
        </div>

        ${voices.map(v => {
          const playing = (S.auditionVoiceId || 'chenzhou') === v.id;
          return `
            <div style="display:flex;align-items:flex-start;gap:9px;">
              <span class="serif" style="width:34px;height:34px;border-radius:50%;background:${v.bg};color:${v.fg};font-size:15px;font-weight:500;display:flex;align-items:center;justify-content:center;flex-shrink:0;">${v.name[0]}</span>
              <div style="flex:1;display:flex;flex-direction:column;gap:4px;">
                <div style="display:flex;align-items:baseline;gap:6px;">
                  <span style="font-size:12.5px;font-weight:500;">${v.name}</span>
                  <span style="font-size:10.5px;color:var(--yy-t3);">${v.mark} · ${v.sum}</span>
                </div>
                <div style="display:flex;align-items:center;gap:8px;">
                  <button onclick="selectAuditionVoice('${v.id}')" style="height:36px;padding:0 12px 0 7px;border-radius:14px;background:var(--yy-card);border:${playing ? '1.5px solid var(--yy-t1)' : '0.5px solid var(--yy-line)'};display:inline-flex;align-items:center;gap:8px;">
                    <span style="width:22px;height:22px;border-radius:50%;background:var(--yy-inset);display:inline-flex;align-items:center;justify-content:center;">${playing ? ICONS.stop : ICONS.play.replace('width="12" height="12"', 'width="10" height="10"')}</span>
                    <span style="display:inline-flex;align-items:center;gap:2px;height:18px;">
                      ${Array.from({ length: 18 }).map((_, bi) => {
                        const h = 5 + ((bi * 7 + v.name.charCodeAt(0)) % 13);
                        const dark = playing && bi < 10;
                        return `<i style="width:2px;height:${h}px;border-radius:1px;background:${dark ? 'var(--yy-t1)' : 'var(--yy-tint-neutral)'};display:block;"></i>`;
                      }).join('')}
                    </span>
                    <span class="tabular" style="font-size:11px;color:var(--yy-t2);">${v.dur}</span>
                  </button>
                  ${!v.heard && !playing ? `<span title="还没听过这句" style="width:6px;height:6px;border-radius:50%;background:#E5483B;"></span>` : ''}
                  ${!v.pinned ? `
                    <button onclick="showProtoToast('已选定「${v.name}」念你的中文书', true)" style="height:26px;padding:0 10px;border-radius:99px;background:var(--yy-card);border:0.5px solid var(--yy-line);font-size:11px;font-weight:500;display:inline-flex;align-items:center;gap:4px;">${ICONS.pin}<span>选择</span></button>
                  ` : ''}
                </div>
              </div>
            </div>`;
        }).join('')}
      </div>

      <!-- Bottom VoiceComposer (height: 40px white Capsule + “ QuickLines popup!) -->
      <div style="position:relative;" onclick="event.stopPropagation()">
        ${S.quickLinesOpen ? `
          <div class="yy-float-card" style="position:absolute;bottom:46px;left:0;width:330px;padding:6px;border-radius:12px;z-index:70;">
            <div style="font-size:10.5px;font-weight:600;color:var(--yy-t3);padding:4px 8px;">换一句试试他们怎么念</div>
            ${[
              ['叙述', '时候既然是深冬；渐近故乡时，天气又阴晦了。'],
              ['对话', '“成日家说你的这玉，我今儿倒要细细瞧瞧！”'],
              ['数字', '公元 1791 年程甲本刊行，全书共 120 回。'],
              ['中英夹杂', '所谓 Fluid Intelligence，就是面对陌生问题时的即时推理能力。'],
              ['书里的一句', '莫失莫忘，仙寿恒昌。']
            ].map(ql => `
              <button onclick="pickQuickLine('${ql[1]}')" style="width:100%;padding:6px 8px;border-radius:8px;text-align:left;display:flex;gap:8px;align-items:baseline;" onmouseover="this.style.background='var(--yy-inset)'" onmouseout="this.style.background='transparent'">
                <span style="font-size:10.5px;font-weight:600;color:var(--tint-accent);width:56px;flex-shrink:0;">${ql[0]}</span>
                <span class="serif truncate" style="font-size:12px;color:var(--yy-t1);">${ql[1]}</span>
              </button>
            `).join('')}
          </div>
        ` : ''}

        <div style="height:40px;padding:0 6px;border-radius:999px;background:var(--yy-card);border:0.5px solid var(--yy-line);box-shadow:0 2px 8px rgba(0,0,0,0.05);display:flex;align-items:center;gap:8px;">
          <button onclick="toggleQuickLines()" title="常用试听句" class="georgia" style="width:28px;height:28px;border-radius:50%;background:var(--yy-inset);font-size:17px;font-weight:700;line-height:32px;color:var(--yy-t1);">“</button>
          <input class="serif" placeholder="想听他们念什么，写一句，或粘贴书里的一段" style="flex:1;font-size:12.5px;" onkeydown="if(event.key==='Enter'&&this.value){pickQuickLine(this.value);this.value='';}" />
          <span style="font-size:10.5px;color:var(--yy-t3);">回车发</span>
          <button onclick="showProtoToast('已发给全部音色重新试读')" style="width:28px;height:28px;border-radius:50%;background:var(--yy-t1);color:var(--yy-card);display:flex;align-items:center;justify-content:center;">${ICONS.arrowUp}</button>
        </div>
      </div>
    </div>
  `;
}

// ============================================================================
// Right 252px SettingsInspector — All 7 Inspector Pages + All 4 Authentic ExportSamples!
// ============================================================================
function renderSettingsInspectorBody(page, S) {
  if (page === 'reading') {
    const paperNames = { plain: '素白', dark: '暗黑', kami: '纸本', kaminight: '纸本 · 夜', sage: '芦苇', eink: '铅纸', custom: '我的纸' };
    return `
      <div style="padding:4px 16px 14px;" data-paper="${S.paper}">
        <div style="display:flex;justify-content:space-between;align-items:baseline;margin-bottom:8px;">
          <strong style="font-size:12.5px;">样张</strong>
          <span class="tabular" style="font-size:11px;color:var(--yy-t3);">${paperNames[S.paper] || '纸本'} · ${S.fontSize || 18}</span>
        </div>
        <div style="height:236px;padding:12px 14px;border-radius:8px;background:var(--paper-bg);color:var(--paper-ink);border:0.5px solid var(--yy-line);font-family:var(--paper-font);font-size:${Math.round((S.fontSize || 18) * 0.68)}px;line-height:1.95;overflow:hidden;">
          <div style="display:flex;justify-content:space-between;font-size:9px;opacity:0.45;margin-bottom:8px;font-family:var(--font-sans);"><span>故乡</span><span class="tabular">12 / 31</span></div>
          <p style="text-indent:2em;margin:0 0 6px;text-align:justify;">我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。</p>
          <p style="text-indent:2em;margin:0;text-align:justify;">时候既然是深冬；渐近故乡时，天气又阴晦了，冷风吹进船舱中，呜呜的响，从蓬隙向外一望，苍黄的天底下，远近横着几个萧索的荒村。</p>
        </div>
      </div>`;
  }

  if (page === 'foreign') {
    const lvl = S.foreignLevel || 3;
    const gloss = (word, zh, minLvl, isVocab = false) => {
      const inner = isVocab ? `<span style="border-bottom:1.2px dashed var(--tint-accent);">${word}</span>` : word;
      if (lvl <= minLvl) {
        return `<ruby>${inner}<rt style="font-family:var(--font-sans);font-size:8.5px;color:var(--yy-t2);">${zh}</rt></ruby>`;
      }
      return inner;
    };
    return `
      <div style="padding:4px 16px 14px;">
        <div style="display:flex;justify-content:space-between;margin-bottom:8px;">
          <strong style="font-size:12.5px;">样张</strong>
          <span style="font-size:11px;color:var(--yy-t3);">词义标注在难词上方</span>
        </div>
        <div class="georgia" style="padding:14px;border-radius:8px;background:var(--yy-card);border:0.5px solid var(--yy-line);font-size:13px;line-height:2.25;color:var(--yy-t1);">
          It was the age of ${gloss('wisdom', '智慧', 2)}, it was the age of ${gloss('foolishness', '愚蠢', 4)}, it was the ${gloss('epoch', '时代', 5)} of ${gloss('belief', '信仰', 3)}, it was the epoch of ${gloss('incredulity', '怀疑', 6, true)}, it was the season of Light…
        </div>
        <div style="font-size:11px;color:var(--yy-t3);margin-top:8px;">节选自《双城记》· 虚线词在生词本中</div>
      </div>`;
  }

  if (page === 'companion') {
    const selId = S.companionSelected || 'deepseek';
    const p = COMPANION_PROVIDERS_CONFIGURED.find(x => x.id === selId) || COMPANION_PROVIDERS_CONFIGURED[1];
    if (p.id === 'thisMac') {
      return `
        <div style="padding:4px 14px 14px;display:flex;flex-direction:column;gap:10px;">
          <div style="display:flex;align-items:center;gap:9px;padding:0 2px;">
            ${renderServiceTile('thisMac', 28)}
            <div><div style="font-size:13.5px;font-weight:600;">这台电脑</div><div style="font-size:10.5px;color:var(--yy-t3);">本地端侧智能</div></div>
          </div>
          <div class="settings-card" style="padding:4px 12px;">
            <div style="height:36px;display:flex;justify-content:space-between;align-items:center;font-size:12px;"><span style="color:var(--yy-t2);">费用</span><span>免费 · 不联网</span></div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="height:36px;display:flex;justify-content:space-between;align-items:center;font-size:12px;"><span style="color:var(--yy-t2);">隐私</span><span>书的内容不出这台电脑</span></div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="height:36px;display:flex;justify-content:space-between;align-items:center;font-size:12px;"><span>设为当前服务</span><div class="yy-switch" onclick="this.classList.toggle('on')"></div></div>
          </div>
        </div>`;
    }
    return `
      <div style="padding:4px 14px 14px;display:flex;flex-direction:column;gap:10px;">
        <div style="display:flex;align-items:center;gap:9px;padding:0 2px;">
          ${renderServiceTile(p.tile, 28)}
          <div>
            <div style="font-size:13.5px;font-weight:600;">${p.title}</div>
            <div style="font-size:10.5px;color:${p.active ? 'var(--yy-ok)' : 'var(--yy-t3)'};">${p.active ? '当前使用' : '已配置'}</div>
          </div>
        </div>

        <div class="settings-card" style="padding:3px;">
          <button onclick="toggleCompanionModelList()" style="width:100%;height:36px;padding:0 10px;display:flex;align-items:center;justify-content:space-between;font-size:12px;">
            <span>模型</span>
            <span class="truncate" style="max-width:136px;color:var(--tint-accent);font-weight:500;">${p.model} ›</span>
          </button>
          ${S.companionModelListOpen ? `
            <div style="padding:6px 8px;border-top:0.5px solid var(--yy-line);background:var(--yy-bg);display:flex;flex-direction:column;gap:4px;">
              <div style="height:26px;padding:0 8px;border-radius:6px;background:var(--yy-card);border:0.5px solid var(--yy-line);display:flex;align-items:center;gap:5px;font-size:11px;color:var(--yy-t3);">${ICONS.search}<span>搜模型…</span></div>
              ${['deepseek-chat (在用)', 'deepseek-reasoner (推荐)', 'deepseek-coder'].map((m, mi) => `
                <button onclick="toggleCompanionModelList();showProtoToast('已切换模型：${m}')" style="height:26px;padding:0 6px;border-radius:5px;display:flex;justify-content:space-between;align-items:center;font-size:11px;background:${mi===0?'var(--tint-soft)':'transparent'};">
                  <span class="mono truncate">${m}</span>${mi===0 ? `<span style="color:var(--tint-accent);">${ICONS.check}</span>` : ''}
                </button>
              `).join('')}
            </div>
          ` : ''}
          <div style="height:0.5px;background:var(--yy-line);margin:0 10px;"></div>
          <div style="height:36px;padding:0 10px;display:flex;align-items:center;justify-content:space-between;font-size:12px;"><span>设为当前服务</span><div class="yy-switch ${p.active ? 'on' : ''}" onclick="this.classList.toggle('on')"></div></div>
        </div>

        <div class="settings-card" style="padding:3px;">
          ${p.key ? `
            <div style="height:36px;padding:0 10px;display:flex;align-items:center;justify-content:space-between;font-size:12px;"><span>Key</span><input value="${p.key}" class="mono" style="width:116px;text-align:right;font-size:11px;color:var(--yy-t2);" /></div>
            <div style="height:0.5px;background:var(--yy-line);margin:0 10px;"></div>
          ` : ''}
          <div style="height:36px;padding:0 10px;display:flex;align-items:center;justify-content:space-between;font-size:12px;"><span>接口地址</span><span class="mono truncate" style="max-width:128px;font-size:10.5px;color:var(--yy-t3);">${p.host}</span></div>
        </div>

        <button class="settings-card" onclick="showProtoToast('✓ 正常 · 0.4 秒')" style="height:36px;padding:0 12px;display:flex;align-items:center;justify-content:space-between;font-size:12px;color:var(--yy-ok);font-weight:500;">
          <span>检查连接</span><span style="font-size:10.5px;padding:1px 6px;border-radius:99px;background:rgba(62,155,106,0.12);">正常 · 0.4 秒</span>
        </button>

        <button class="settings-card" onclick="showProtoToast('已移除 ${p.title}', true, true)" style="height:34px;font-size:12px;color:var(--yy-danger);font-weight:500;">移除 ${p.title}</button>
      </div>`;
  }

  // ListeningVoiceCard (MacVoicePicker.swift:1098-1371: 30-bar red-progress Wave + karaoke sentence + 来自/费用 + 两层合成!)
  if (page === 'listening') {
    const vid = S.auditionVoiceId || 'chenzhou';
    const vMap = {
      chenzhou: { name: '沉舟', bg: '#E3EAEF', fg: '#2F475B', pinned: true },
      suwan: { name: '苏晚', bg: '#F2E4E6', fg: '#7A3E48', pinned: false },
      linshen: { name: '林深', bg: '#E4ECE4', fg: '#39573D', pinned: false },
      myclone: { name: '我的声音', bg: '#EFE8DC', fg: '#6E5233', pinned: false }
    };
    const v = vMap[vid] || vMap.chenzhou;
    return `
      <div style="padding:4px 14px 14px;display:flex;flex-direction:column;gap:10px;">
        <div style="font-size:11px;font-weight:600;color:var(--yy-t2);">${v.pinned ? '在念你的中文书' : '正在试听'}</div>

        <div style="display:flex;align-items:center;gap:9px;">
          <span class="serif" style="width:34px;height:34px;border-radius:50%;background:${v.bg};color:${v.fg};font-size:15px;font-weight:500;display:flex;align-items:center;justify-content:center;">${v.name[0]}</span>
          <strong style="font-size:15px;flex:1;">${v.name}</strong>
          ${v.pinned
            ? `<span style="height:24px;padding:0 9px;border-radius:99px;background:rgba(62,155,106,0.12);color:var(--yy-ok);font-size:11px;font-weight:600;display:inline-flex;align-items:center;gap:3px;">${ICONS.check}<span>已选择</span></span>`
            : `<button onclick="showProtoToast('已选定「${v.name}」', true)" style="height:24px;padding:0 9px;border-radius:99px;background:var(--yy-card);border:0.5px solid var(--yy-line);font-size:11px;font-weight:500;display:inline-flex;align-items:center;gap:4px;">${ICONS.pin}<span>选择</span></button>`
          }
        </div>

        <div class="settings-card" style="padding:12px;">
          <div style="display:flex;align-items:center;gap:8px;">
            <button style="width:30px;height:30px;border-radius:50%;background:var(--yy-t1);color:var(--yy-card);display:flex;align-items:center;justify-content:center;flex-shrink:0;">${ICONS.play}</button>
            <div style="flex:1;height:22px;display:flex;align-items:center;gap:1.8px;">
              ${Array.from({ length: 30 }).map((_, i) => `<span style="flex:1;height:${5 + ((i * 7 + 3) % 16)}px;border-radius:1.5px;background:${i < 13 ? 'var(--yy-danger)' : 'var(--yy-inset)'};"></span>`).join('')}
            </div>
            <span class="tabular" style="font-size:10.5px;color:var(--yy-t3);">5″</span>
          </div>
          <div style="height:0.5px;background:var(--yy-line);margin:10px 0 8px;"></div>
          <!-- Karaoke LineProgress -->
          <div class="serif" style="font-size:13px;line-height:1.65;">
            <span style="color:var(--yy-t1);">我冒了严寒，回到相隔二千余里，</span><span style="color:var(--yy-t3);">别了二十余年的故乡去。</span>
          </div>
        </div>

        <div class="settings-card" style="padding:3px 12px;">
          <div style="height:34px;display:flex;justify-content:space-between;align-items:center;font-size:12px;"><span style="color:var(--yy-t2);">来自</span><span>这台电脑</span></div>
          <div style="height:0.5px;background:var(--yy-line);"></div>
          <div style="height:34px;display:flex;justify-content:space-between;align-items:center;font-size:12px;"><span style="color:var(--yy-t2);">费用</span><span>免费 · 不联网</span></div>
        </div>

        <div style="padding:2px 2px 0;">
          <div style="font-size:11px;font-weight:600;color:var(--yy-t2);">两层合成</div>
          <div style="font-size:10.5px;color:var(--yy-t3);line-height:1.55;margin-top:3px;">试听和边看边听用轻巧款，点一下就出声；广播剧的语气和人物专属声，装好「广播剧工坊」后在后台细做。</div>
        </div>
      </div>`;
  }

  // NotesInspector + ALL 4 Exact ExportSample Designs (MacNoteExportSettings.swift:570-590!)
  if (page === 'notes') {
    const t = S.noteExportTarget || 'obsidian';
    const names = { obsidian: 'Obsidian', notion: 'Notion', flomo: 'flomo', xinzhi: '新枝' };
    return `
      <div style="padding:4px 14px 14px;display:flex;flex-direction:column;gap:10px;">
        <div style="display:flex;align-items:center;gap:8px;">
          ${renderExportTile(t, 26)}
          <div style="flex:1;">
            <div style="font-size:13px;font-weight:600;">${names[t]}</div>
            <div style="font-size:10.5px;color:var(--yy-t3);">${t === 'obsidian' || t === 'flomo' ? '已连接 · 上次发送 10 分钟前' : '填入令牌或目录后开启增量同步'}</div>
          </div>
        </div>

        <div class="settings-card" style="padding:4px 10px;">
          ${t === 'obsidian' ? `
            <div style="height:34px;display:flex;justify-content:space-between;align-items:center;font-size:11.5px;"><span style="color:var(--yy-t2);">库目录</span><span class="mono truncate" style="max-width:130px;">D:\\Obsidian\\Vault</span></div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="height:34px;display:flex;justify-content:space-between;align-items:center;font-size:11.5px;"><span style="color:var(--yy-t2);">子文件夹</span><span>语阅</span></div>
          ` : t === 'notion' ? `
            <div style="height:34px;display:flex;justify-content:space-between;align-items:center;font-size:11.5px;"><span style="color:var(--yy-t2);">Token</span><input placeholder="ntn_..." class="mono" style="width:120px;text-align:right;font-size:11px;" /></div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="height:34px;display:flex;justify-content:space-between;align-items:center;font-size:11.5px;"><span style="color:var(--yy-t2);">父页面</span><span style="color:var(--yy-t3);">读书笔记</span></div>
          ` : t === 'flomo' ? `
            <div style="height:34px;display:flex;justify-content:space-between;align-items:center;font-size:11.5px;"><span style="color:var(--yy-t2);">API</span><span class="mono truncate" style="max-width:140px;color:var(--yy-t3);">flomoapp.com/iwh/•••</span></div>
          ` : `
            <div style="height:34px;display:flex;justify-content:space-between;align-items:center;font-size:11.5px;"><span style="color:var(--yy-t2);">API Key</span><input placeholder="粘贴新枝密钥" style="width:120px;text-align:right;font-size:11px;" /></div>
          `}
        </div>

        <div style="height:0.5px;background:var(--yy-line);margin:2px 2px;"></div>

        <!-- ExportSample: 4 Distinct Authentic Designs! -->
        <div>
          <div style="display:flex;justify-content:space-between;align-items:baseline;margin-bottom:8px;">
            <strong style="font-size:12px;">样张</strong>
            <span style="font-size:10.5px;color:var(--yy-t3);">发到 ${names[t]} 是这样</span>
          </div>

          ${t === 'obsidian' ? `
            <div class="settings-card" style="border-radius:10px;padding:12px;display:flex;flex-direction:column;gap:7px;">
              <div style="display:flex;justify-content:space-between;font-size:10px;color:var(--yy-t3);"><span class="mono">语阅/故乡.md</span><span>Markdown</span></div>
              <div style="font-size:15px;font-weight:600;">故乡</div>
              <div style="font-size:11px;color:var(--yy-t3);margin-top:-4px;">鲁迅 · 《呐喊》</div>
              <div class="serif" style="padding-left:8px;border-left:2.5px solid rgba(124,58,237,0.5);font-size:12px;line-height:1.55;">我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。</div>
              <div class="serif" style="padding-left:8px;border-left:2.5px solid rgba(124,58,237,0.5);font-size:12px;line-height:1.55;">希望本是无所谓有，无所谓无的。</div>
              <div style="font-size:11.5px;color:var(--yy-t2);">这句是全篇的落点</div>
              <div style="display:flex;gap:5px;margin-top:2px;">
                <span style="padding:1px 7px;border-radius:99px;background:rgba(124,58,237,0.10);color:#7C3AED;font-size:10.5px;">#语阅</span>
                <span style="padding:1px 7px;border-radius:99px;background:rgba(124,58,237,0.10);color:#7C3AED;font-size:10.5px;">#鲁迅</span>
              </div>
            </div>
          ` : t === 'notion' ? `
            <div class="settings-card" style="border-radius:10px;padding:12px;display:flex;flex-direction:column;gap:7px;">
              <div style="display:flex;justify-content:space-between;font-size:10px;color:var(--yy-t3);"><span>读书笔记 › 故乡</span><span>Notion</span></div>
              <div style="font-size:15px;font-weight:700;">故乡</div>
              <div style="font-size:12px;font-weight:600;">第一章</div>
              <div style="padding-left:8px;border-left:2px solid var(--yy-t1);font-size:12px;line-height:1.55;">我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。</div>
              <div style="padding-left:8px;border-left:2px solid var(--yy-t1);font-size:12px;line-height:1.55;">希望本是无所谓有，无所谓无的。</div>
              <div style="font-size:12px;color:var(--yy-t1);">这句是全篇的落点</div>
            </div>
          ` : t === 'flomo' ? `
            <div style="display:flex;flex-direction:column;gap:8px;">
              <div class="settings-card" style="border-radius:10px;padding:10px 12px;display:flex;flex-direction:column;gap:5px;">
                <div class="tabular" style="font-size:10.5px;color:var(--yy-t3);">2026-11-05 10:12</div>
                <div style="font-size:12px;line-height:1.55;">我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。</div>
                <div style="font-size:11px;color:#30CF79;">#语阅 #故乡</div>
              </div>
              <div class="settings-card" style="border-radius:10px;padding:10px 12px;display:flex;flex-direction:column;gap:5px;">
                <div class="tabular" style="font-size:10.5px;color:var(--yy-t3);">2026-11-05 10:12</div>
                <div style="font-size:12px;line-height:1.55;">希望本是无所谓有，无所谓无的。</div>
                <div style="font-size:12px;">这句是全篇的落点</div>
                <div style="font-size:11px;color:#30CF79;">#语阅 #故乡</div>
              </div>
            </div>
          ` : `
            <div style="display:flex;flex-direction:column;gap:8px;">
              <div class="settings-card" style="border-radius:10px;padding:10px 12px;display:flex;flex-direction:column;gap:5px;">
                <div style="font-size:12px;line-height:1.55;">我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。</div>
                <div style="font-size:11px;color:var(--yy-t3);">——《故乡》 鲁迅</div>
                <div style="font-size:10.5px;color:#2E7D52;">语阅/故乡</div>
              </div>
              <div class="settings-card" style="border-radius:10px;padding:10px 12px;display:flex;flex-direction:column;gap:5px;">
                <div style="font-size:12px;line-height:1.55;">希望本是无所谓有，无所谓无的。</div>
                <div style="font-size:12px;color:var(--yy-t2);">这句是全篇的落点</div>
                <div style="font-size:11px;color:var(--yy-t3);">——《故乡》 鲁迅</div>
                <div style="font-size:10.5px;color:#2E7D52;">语阅/故乡</div>
              </div>
            </div>
          `}

          <div style="font-size:10.5px;color:var(--yy-t3);margin-top:7px;">用《故乡》的两条划线和一条想法排的</div>
        </div>
      </div>`;
  }

  if (page === 'storage') {
    return `
      <div style="padding:4px 14px 14px;">
        <div style="font-size:12px;font-weight:600;margin-bottom:8px;">书库</div>
        <div class="settings-card" style="border-radius:10px;padding:12px;">
          <div style="font-size:13px;font-weight:500;">Yuyue</div>
          <div class="mono" style="font-size:10.5px;color:var(--yy-t3);margin-top:4px;word-break:break-all;">~/AppData/Local/Yuyue</div>
          <button onclick="showProtoToast('已在资源管理器中打开书库目录')" style="margin-top:10px;color:var(--tint-accent);font-size:11.5px;font-weight:500;display:inline-flex;align-items:center;gap:5px;">${ICONS.folder}<span>在资源管理器中显示</span></button>
        </div>
      </div>`;
  }

  // 'data'
  return `
    <div style="padding:4px 14px 14px;">
      <div style="font-size:12px;font-weight:600;margin-bottom:8px;">上次快照</div>
      <div class="settings-card" style="border-radius:10px;padding:12px;">
        <div class="tabular" style="font-size:16px;font-weight:600;">11 月 22 日 21:40</div>
        <div style="font-size:11px;color:var(--yy-t3);margin-top:4px;">含书文件 · 87.9 MB · 9 本书</div>
        <button onclick="showProtoToast('已在资源管理器中定位快照文件')" style="margin-top:10px;color:var(--tint-accent);font-size:11.5px;font-weight:500;display:inline-flex;align-items:center;gap:5px;">${ICONS.folder}<span>在资源管理器中显示</span></button>
      </div>
    </div>`;
}

// ============================================================================
// SettingsHistoryCard ("最近改过" — MacSettingsHistory.swift:1-703, width: 380px!)
// ============================================================================
function renderSettingsHistoryPopover(S) {
  return `
    <div class="yy-float-card" onclick="event.stopPropagation()" style="position:absolute;top:40px;right:14px;width:380px;background:var(--yy-bg);border-radius:14px;z-index:95;display:flex;flex-direction:column;overflow:hidden;">
      <!-- Header -->
      <div style="padding:13px 14px 8px;">
        <div style="display:flex;align-items:baseline;gap:8px;">
          <strong style="font-size:14px;color:var(--yy-t1);">最近改过</strong>
          <span class="tabular" style="font-size:11px;color:var(--yy-t3);">3 项记录</span>
        </div>
        <div style="display:flex;gap:5px;margin-top:8px;">
          ${[['全部 3', true], ['阅读 1', false], ['外观 1', false], ['朗读 1', false]].map(fp => `
            <span style="height:23px;padding:0 9px;border-radius:99px;background:${fp[1] ? 'var(--tint-soft)' : 'var(--yy-card)'};border:0.5px solid ${fp[1] ? 'var(--tint-edge)' : 'var(--yy-line)'};color:${fp[1] ? 'var(--tint-accent)' : 'var(--yy-t2)'};font-size:11px;font-weight:${fp[1] ? '600' : '400'};display:inline-flex;align-items:center;">${fp[0]}</span>
          `).join('')}
        </div>
      </div>

      <!-- Day Group: 今天 -->
      <div style="padding:4px 12px 12px;display:flex;flex-direction:column;gap:6px;">
        <div style="font-size:11px;font-weight:600;color:var(--yy-t3);padding-left:2px;">今天</div>
        <div class="settings-card" style="border-radius:12px;overflow:hidden;">
          <!-- Entry 1: Paper swatch rich token -->
          <div style="padding:9px 10px 9px 11px;display:flex;align-items:center;gap:10px;">
            <span style="width:28px;height:28px;border-radius:8px;background:var(--yy-inset);color:var(--yy-t2);display:inline-flex;align-items:center;justify-content:center;flex-shrink:0;">${ICONS.openBook}</span>
            <div style="flex:1;min-width:0;">
              <div style="display:flex;align-items:center;gap:5px;">
                <strong style="font-size:12.5px;">纸色</strong>
                <button onclick="setSettingsPage('reading')" style="font-size:11px;color:var(--yy-t3);">阅读 ›</button>
                <span class="tabular" style="margin-left:auto;font-size:10.5px;color:var(--yy-t3);">10:36</span>
              </div>
              <div style="display:flex;align-items:center;gap:6px;margin-top:3px;font-size:11.5px;color:var(--yy-t2);">
                <span style="display:inline-flex;align-items:center;gap:4px;"><i style="width:11px;height:11px;border-radius:50%;background:#FFFFFF;border:0.5px solid rgba(0,0,0,0.2);"></i>素白</span>
                <span style="font-size:10px;color:var(--yy-t3);">→</span>
                <span style="display:inline-flex;align-items:center;gap:4px;color:var(--yy-t1);font-weight:500;"><i style="width:11px;height:11px;border-radius:50%;background:#F5F4ED;border:0.5px solid rgba(0,0,0,0.2);"></i>纸本</span>
              </div>
            </div>
            <button onclick="setPaper('plain');showProtoToast('✓ 已改回「纸色」')" style="width:62px;height:25px;border-radius:99px;background:var(--yy-inset);border:0.5px solid var(--yy-line);font-size:11px;font-weight:500;display:inline-flex;align-items:center;justify-content:center;gap:3px;">${ICONS.undo}<span>改回</span></button>
          </div>

          <div style="height:0.5px;background:var(--yy-line);margin:0 10px 0 49px;"></div>

          <!-- Entry 2: Voice audition pill token -->
          <div style="padding:9px 10px 9px 11px;display:flex;align-items:center;gap:10px;">
            <span style="width:28px;height:28px;border-radius:8px;background:var(--yy-inset);color:var(--yy-t2);display:inline-flex;align-items:center;justify-content:center;flex-shrink:0;">${ICONS.headphones}</span>
            <div style="flex:1;min-width:0;">
              <div style="display:flex;align-items:center;gap:5px;">
                <strong style="font-size:12.5px;">中文书声音</strong>
                <button onclick="setSettingsPage('listening')" style="font-size:11px;color:var(--yy-t3);">朗读 ›</button>
                <span class="tabular" style="margin-left:auto;font-size:10.5px;color:var(--yy-t3);">10:21</span>
              </div>
              <div style="display:flex;align-items:center;gap:6px;margin-top:3px;font-size:11.5px;color:var(--yy-t2);">
                <span style="height:21px;padding:0 7px;border-radius:99px;background:var(--yy-inset);font-size:10.5px;display:inline-flex;align-items:center;gap:3px;">▶ 苏晚</span>
                <span style="font-size:10px;color:var(--yy-t3);">→</span>
                <span style="height:21px;padding:0 7px;border-radius:99px;background:var(--yy-inset);color:var(--yy-t1);font-weight:500;font-size:10.5px;display:inline-flex;align-items:center;gap:3px;">▶ 沉舟</span>
              </div>
            </div>
            <button onclick="showProtoToast('✓ 已改回「苏晚」')" style="width:62px;height:25px;border-radius:99px;background:var(--yy-inset);border:0.5px solid var(--yy-line);font-size:11px;font-weight:500;display:inline-flex;align-items:center;justify-content:center;gap:3px;">${ICONS.undo}<span>改回</span></button>
          </div>

          <div style="height:0.5px;background:var(--yy-line);margin:0 10px 0 49px;"></div>

          <!-- Entry 3: Mini toggle token -->
          <div style="padding:9px 10px 9px 11px;display:flex;align-items:center;gap:10px;">
            <span style="width:28px;height:28px;border-radius:8px;background:var(--yy-inset);color:var(--yy-t2);display:inline-flex;align-items:center;justify-content:center;flex-shrink:0;">${ICONS.sun}</span>
            <div style="flex:1;min-width:0;">
              <div style="display:flex;align-items:center;gap:5px;">
                <strong style="font-size:12.5px;">封面下显示进度</strong>
                <button onclick="setSettingsPage('appearance')" style="font-size:11px;color:var(--yy-t3);">外观 ›</button>
                <span class="tabular" style="margin-left:auto;font-size:10.5px;color:var(--yy-t3);">09:50</span>
              </div>
              <div style="display:flex;align-items:center;gap:6px;margin-top:3px;font-size:11.5px;color:var(--yy-t2);">
                <span style="width:19px;height:11.5px;border-radius:99px;background:rgba(120,116,110,0.28);position:relative;display:inline-block;"><i style="position:absolute;left:1.5px;top:1.5px;width:8.5px;height:8.5px;border-radius:50%;background:#FFF;"></i></span>
                <span style="font-size:10px;color:var(--yy-t3);">→</span>
                <span style="width:19px;height:11.5px;border-radius:99px;background:var(--tint-fill);position:relative;display:inline-block;"><i style="position:absolute;right:1.5px;top:1.5px;width:8.5px;height:8.5px;border-radius:50%;background:#FFF;"></i></span>
              </div>
            </div>
            <button onclick="toggleShelfProgressShow();showProtoToast('✓ 已改回「封面下显示进度」')" style="width:62px;height:25px;border-radius:99px;background:var(--yy-inset);border:0.5px solid var(--yy-line);font-size:11px;font-weight:500;display:inline-flex;align-items:center;justify-content:center;gap:3px;">${ICONS.undo}<span>改回</span></button>
          </div>
        </div>
      </div>

      <!-- Footer -->
      <div style="min-height:41px;padding:8px 12px;border-top:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:space-between;">
        ${S.historyConfirmClear ? `
          <span style="font-size:11.5px;font-weight:500;">清空记录？当前设置会保留</span>
          <div style="display:flex;gap:6px;">
            <button onclick="toggleHistoryClearConfirm(false)" style="height:25px;padding:0 10px;border-radius:99px;background:var(--yy-inset);font-size:11px;">取消</button>
            <button onclick="toggleHistoryClearConfirm(false);closeSettingsPopups();showProtoToast('已清空修改记录')" style="height:25px;padding:0 10px;border-radius:99px;background:var(--yy-danger);color:#FFF;font-size:11px;font-weight:600;">清空</button>
          </div>
        ` : `
          <button onclick="setPaper('plain');showProtoToast('✓ 已改回「纸色」')" style="display:inline-flex;align-items:center;gap:6px;font-size:11.5px;color:var(--yy-t1);">
            <span class="yy-key" style="background:var(--yy-card);">${S.chrome === 'mac' ? '⌘Z' : 'Ctrl+Z'}</span>
            <span>改回「纸色」</span>
          </button>
          <button onclick="toggleHistoryClearConfirm(true)" style="height:25px;padding:0 10px;border-radius:99px;font-size:11px;color:var(--yy-t2);" onmouseover="this.style.color='var(--yy-danger)'" onmouseout="this.style.color='var(--yy-t2)'">清空记录</button>
        `}
      </div>
    </div>
  `;
}

// ============================================================================
// Settings Modal Sheets:
//   - CompanionProviderSheet (440x480 — All 18 Official Providers!)
//   - LanguageRestartConfirmCard
//   - SnapshotImportSheet (合并 vs 覆盖)
//   - ReadingExportSheet (Goodreads | StoryGraph CSV Preview)
//   - CloneVoiceSheet, FeedbackForm, Changelog / Privacy / Credits
// ============================================================================
function renderSettingsModals(S) {
  if (S.modal === 'feedback') {
    return renderFeedbackSheet(S);
  }

  // CompanionProviderSheet (MacCompanionSettings.swift: 440x480, 3-col grid of ALL 17 official SVG providers + 自己添加!)
  if (S.modal === 'companionSheet') {
    const apiProviders = [
      ['claude', 'Claude'], ['openai', 'OpenAI'], ['gemini', 'Gemini'],
      ['deepseek', 'DeepSeek'], ['dashscope', '通义千问'], ['moonshot', 'Kimi'],
      ['zhipu', '智谱 GLM'], ['volcengine', '豆包'], ['siliconflow', '硅基流动'],
      ['minimax', 'MiniMax'], ['xai', 'Grok'], ['mistral', 'Mistral'], ['groq', 'Groq']
    ];
    const localProviders = [
      ['magpie', 'magpie'], ['ollama', 'Ollama'], ['lmstudio', 'LM Studio']
    ];
    return `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:440px;max-height:520px;display:flex;flex-direction:column;">
          <div style="padding:14px 16px 10px;border-bottom:0.5px solid var(--yy-line);display:flex;justify-content:space-between;align-items:center;">
            <strong style="font-size:14.5px;">添加服务商</strong>
            <button onclick="setModal(null)" style="width:26px;height:26px;border-radius:50%;background:var(--yy-inset);display:flex;align-items:center;justify-content:center;">${ICONS.xmark}</button>
          </div>
          <div style="flex:1;overflow-y:auto;padding:12px 16px 16px;display:flex;flex-direction:column;gap:12px;">
            <div>
              <div style="font-size:11px;font-weight:600;color:var(--yy-t3);margin-bottom:6px;">登录账号</div>
              <button onclick="setModal(null);showProtoToast('正在打开浏览器授权 OpenRouter (PKCE)…')" class="settings-card" style="width:100%;min-height:42px;padding:8px 12px;display:flex;align-items:center;gap:9px;">
                ${renderServiceTile('openrouter', 24)}
                <span style="font-size:12.5px;font-weight:500;">OpenRouter</span>
                <span style="font-size:10px;padding:1px 6px;border-radius:99px;background:var(--tint-soft);color:var(--tint-accent);font-weight:600;">一键</span>
                <span style="margin-left:auto;font-size:11px;color:var(--yy-t3);">免填 Key 直接授权 ›</span>
              </button>
            </div>

            <div>
              <div style="font-size:11px;font-weight:600;color:var(--yy-t3);margin-bottom:6px;">API Key</div>
              <div style="display:grid;grid-template-columns:repeat(3,1fr);gap:8px;">
                ${apiProviders.map(p => `
                  <button onclick="setModal(null);showProtoToast('已选择 ${p[1]} · 填入 Key 自动测速并拉取模型')" class="settings-card" style="min-height:42px;padding:6px 9px;display:flex;align-items:center;gap:8px;text-align:left;" onmouseover="this.style.borderColor='var(--tint-accent)'" onmouseout="this.style.borderColor='var(--yy-card-border)'">
                    ${renderServiceTile(p[0], 24)}
                    <span class="truncate" style="font-size:12px;font-weight:500;">${p[1]}</span>
                  </button>
                `).join('')}
              </div>
            </div>

            <div>
              <div style="font-size:11px;font-weight:600;color:var(--yy-t3);margin-bottom:6px;">本机和局域网</div>
              <div style="display:grid;grid-template-columns:repeat(3,1fr);gap:8px;">
                ${localProviders.map(p => `
                  <button onclick="setModal(null);showProtoToast('正在扫描本机 ${p[1]} 端口…')" class="settings-card" style="min-height:42px;padding:6px 9px;display:flex;align-items:center;gap:8px;text-align:left;">
                    ${renderServiceTile(p[0], 24)}
                    <span class="truncate" style="font-size:12px;font-weight:500;">${p[1]}</span>
                  </button>
                `).join('')}
              </div>
            </div>

            <button onclick="setModal(null);showProtoToast('添加自定义 OpenAI 兼容接口')" style="min-height:42px;padding:8px 12px;border-radius:12px;border:1px dashed var(--yy-tint-neutral);display:flex;align-items:center;gap:9px;">
              ${renderServiceTile('add', 24)}
              <span style="font-size:12px;font-weight:500;">自己添加</span>
              <span style="font-size:11px;color:var(--yy-t3);">· 任何 OpenAI 兼容接口</span>
            </button>
          </div>
        </div>
      </div>`;
  }

  // LanguageRestartConfirmCard (352px wide)
  if (S.modal === 'langRestartConfirm') {
    return `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:352px;padding:20px;display:flex;flex-direction:column;gap:12px;">
          <div style="font-size:15px;font-weight:700;">切换界面语言并重新打开？</div>
          <div style="font-size:12.5px;color:var(--yy-t2);line-height:1.58;">语阅将立即重新打开并自动回到当前「设置 › 通用」页，正在读的书与进度不会丢失。</div>
          <div style="display:flex;justify-content:flex-end;gap:8px;margin-top:4px;">
            <button class="yy-pill" onclick="setModal(null)">稍后</button>
            <button class="yy-pill primary" onclick="setModal(null);showProtoToast('已保存语言偏好')">立即重开</button>
          </div>
        </div>
      </div>`;
  }

  // SnapshotImportSheet (SnapshotSheets.swift: width 400, 合并 vs 覆盖)
  if (S.modal === 'snapshotImport') {
    const mode = S.snapImportMode || 'merge';
    return `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:400px;padding:18px 20px;display:flex;flex-direction:column;gap:12px;">
          <div style="display:flex;justify-content:space-between;align-items:center;"><strong style="font-size:15px;">导入快照</strong><button onclick="setModal(null)">${ICONS.xmark}</button></div>
          <div class="yy-card" style="padding:12px;border-radius:12px;display:flex;flex-direction:column;gap:8px;">
            <div style="display:flex;justify-content:space-between;align-items:baseline;"><strong>2026-11-22.yuyue</strong><span class="tabular" style="font-size:11px;color:var(--yy-t3);">87.9 MB</span></div>
            <div style="display:flex;gap:5px;flex-wrap:wrap;">
              ${['进度 9 本', '笔记 4 条', '生词 4 个', '设置', '书文件'].map(tg => `<span style="padding:2px 7px;border-radius:6px;background:var(--yy-inset);font-size:10.5px;color:var(--yy-t2);">${tg}</span>`).join('')}
            </div>
          </div>
          <div style="display:grid;grid-template-columns:1fr 1fr;gap:8px;">
            <button onclick="setSnapImportMode('merge')" class="yy-card" style="padding:12px;border-radius:12px;border:${mode === 'merge' ? '1.5px solid var(--tint-accent)' : '0.5px solid var(--yy-line)'};text-align:left;">
              <div style="font-size:13px;font-weight:600;color:${mode === 'merge' ? 'var(--tint-accent)' : 'var(--yy-t1)'};">合并</div>
              <div style="font-size:10.5px;color:var(--yy-t3);margin-top:3px;line-height:1.45;">保留本机已有书和笔记，只合入更新的进度与新条目</div>
            </button>
            <button onclick="setSnapImportMode('replace')" class="yy-card" style="padding:12px;border-radius:12px;border:${mode === 'replace' ? '1.5px solid var(--tint-accent)' : '0.5px solid var(--yy-line)'};text-align:left;">
              <div style="font-size:13px;font-weight:600;color:${mode === 'replace' ? 'var(--tint-accent)' : 'var(--yy-t1)'};">覆盖</div>
              <div style="font-size:10.5px;color:var(--yy-t3);margin-top:3px;line-height:1.45;">完全恢复到快照当时的状态（恢复前自动备份当前书库）</div>
            </button>
          </div>
          <button class="yy-pill primary lg" style="width:100%;" onclick="setModal(null);showProtoToast('已完成快照合并导入')">导入</button>
        </div>
      </div>`;
  }

  // ReadingExportSheet (SnapshotSheets.swift & records.rs: 400x450, [Goodreads | StoryGraph] + monospace CSV preview!)
  if (S.modal === 'readingCsvExport') {
    const fmt = S.csvExportFmt || 'goodreads';
    const csvHeader = fmt === 'goodreads'
      ? 'Title,Author,ISBN13,Exclusive Shelf,Date Read,Date Added'
      : 'Title,Authors,ISBN/UID,Read Status,Last Date Read,Date Added';
    const csvRows = fmt === 'goodreads'
      ? [
          '红楼梦,曹雪芹,9787020002207,currently-reading,,2026/10/28',
          '呐喊,鲁迅,9787020044023,read,2026/11/03,2026/10/12',
          'Pride and Prejudice,Jane Austen,9780141439518,to-read,,2026/11/18'
        ]
      : [
          '红楼梦,曹雪芹,9787020002207,currently-reading,,2026/10/28',
          '呐喊,鲁迅,9787020044023,read,2026/11/03,2026/10/12',
          '阴翳礼赞,谷崎润一郎,9787532774241,currently-reading,,2026/11/09'
        ];
    return `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:400px;padding:18px 20px;display:flex;flex-direction:column;gap:12px;">
          <div style="display:flex;justify-content:space-between;align-items:center;"><strong style="font-size:15px;">导出阅读记录</strong><button onclick="setModal(null)">${ICONS.xmark}</button></div>
          <div class="yy-seg" style="width:100%;">
            <button class="yy-seg-btn ${fmt === 'goodreads' ? 'active' : ''}" onclick="setCsvExportFmt('goodreads')">Goodreads</button>
            <button class="yy-seg-btn ${fmt === 'storygraph' ? 'active' : ''}" onclick="setCsvExportFmt('storygraph')">StoryGraph</button>
          </div>
          <div class="yy-card" style="padding:2px 14px;border-radius:12px;">
            <div style="height:36px;display:flex;align-items:center;gap:8px;font-size:12.5px;">${ICONS.bookmark}<span>想读</span><span class="tabular" style="margin-left:auto;color:var(--yy-t3);">2 本</span></div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="height:36px;display:flex;align-items:center;gap:8px;font-size:12.5px;">${ICONS.openBook}<span>在读</span><span class="tabular" style="margin-left:auto;color:var(--yy-t3);">5 本</span></div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="height:36px;display:flex;align-items:center;gap:8px;font-size:12.5px;">${ICONS.check}<span>读完</span><span class="tabular" style="margin-left:auto;color:var(--yy-t3);">2 本 · 带读完日期</span></div>
          </div>
          <div class="mono" style="padding:10px 12px;border-radius:10px;background:var(--yy-inset);font-size:9.5px;line-height:1.65;color:var(--yy-t2);overflow-x:auto;white-space:nowrap;">
            <div style="color:var(--yy-t1);font-weight:600;">${csvHeader}</div>
            ${csvRows.map(r => `<div>${r}</div>`).join('')}
          </div>
          <div style="font-size:10.5px;color:var(--yy-t3);">没有 ISBN 的 2 本按书名和作者对，导进去后可能要在网站上确认一下。</div>
          <button class="yy-pill primary lg" style="width:100%;" onclick="setModal(null);showProtoToast('已导出 语阅-${fmt}.csv')">导出 9 本 · CSV</button>
        </div>
      </div>`;
  }

  if (S.modal === 'cloneVoiceSheet') {
    return `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:400px;padding:20px;display:flex;flex-direction:column;gap:14px;">
          <div style="display:flex;justify-content:space-between;align-items:center;"><strong style="font-size:15px;">克隆一个声音</strong><button onclick="setModal(null)">${ICONS.xmark}</button></div>
          <div style="font-size:12px;color:var(--yy-t2);">对着麦克风自然念出下面这段话（约 8 秒），声纹模型完全在本机提取，录音不出电脑。</div>
          <div class="serif" style="padding:14px 16px;border-radius:12px;background:var(--yy-card);border:0.5px solid var(--yy-line);font-size:15px;line-height:1.75;">“春水初生，春林初盛，春风十里，不如在夜深人静时翻开一卷好书。”</div>
          <button class="yy-pill primary lg" style="width:100%;" onclick="setModal(null);showProtoToast('已生成本机克隆声音卡')">${ICONS.mic}<span>按住开始录 8 秒</span></button>
        </div>
      </div>`;
  }

  if (S.modal === 'newPaperSheet') {
    return `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:340px;padding:16px 18px;display:flex;flex-direction:column;gap:12px;">
          <div style="display:flex;justify-content:space-between;align-items:center;"><strong style="font-size:14px;">新建纸色</strong><button onclick="setModal(null)">${ICONS.xmark}</button></div>
          <div class="serif" style="padding:12px 14px;border-radius:8px;background:#EFECE2;color:#26251F;border:0.5px solid var(--yy-line);font-size:13px;line-height:1.85;">我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。</div>
          <div style="height:12px;border-radius:99px;background:linear-gradient(90deg,#F5EAE6,#F5EFE2,#EEF2E2,#E4F0EC,#E6ECF5,#F0E8F5);border:0.5px solid rgba(0,0,0,0.12);"></div>
          <div class="yy-seg sm"><button class="yy-seg-btn">光滑</button><button class="yy-seg-btn">细砂</button><button class="yy-seg-btn active">宣纸</button><button class="yy-seg-btn">亚麻</button></div>
          <button class="yy-pill primary" style="width:100%;" onclick="setPaper('custom');setModal(null);showProtoToast('已新建「宣纸白」')">保存纸色</button>
        </div>
      </div>`;
  }

  if (S.modal === 'changelogSheet' || S.modal === 'privacySheet' || S.modal === 'creditsSheet') {
    const titles = { changelogSheet: '更新日志', privacySheet: '隐私说明', creditsSheet: '开源许可与致谢' };
    return `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:500px;padding:18px 20px;display:flex;flex-direction:column;gap:12px;">
          <div style="display:flex;justify-content:space-between;align-items:center;"><strong style="font-size:15px;">${titles[S.modal]}</strong><button onclick="setModal(null)">${ICONS.xmark}</button></div>
          ${S.modal === 'privacySheet' ? `
            <div class="yy-card" style="padding:12px 16px;display:flex;flex-direction:column;gap:8px;font-size:12.5px;line-height:1.65;">
              <div>1. <strong>无服务器、无账号</strong>：语阅没有后台服务器，不收集任何阅读行为统计或崩溃遥测。</div>
              <div>2. <strong>书籍与笔记留在本机</strong>：所有 EPUB/PDF、划线、想法与生词默认仅保存在你的电脑本地目录。</div>
              <div>3. <strong>直连你选的服务</strong>：同步仅连接你自己的云盘 / WebDAV；书伴与云端语音仅使用你自己的 API Key 直连对应服务商。</div>
            </div>
          ` : S.modal === 'creditsSheet' ? `
            <div class="yy-card" style="padding:12px 16px;display:flex;flex-direction:column;gap:8px;font-size:12px;line-height:1.6;">
              <div><strong>语阅 (Yuyue)</strong> 以 <strong>GPL-3.0</strong> 开源协议发布。</div>
              <div style="color:var(--yy-t2);">核心组件致谢：Rust (yuyue-core)、SQLite (FTS5)、Sherpa-ONNX / Qwen3-TTS（本机语音与句级对齐）、PDFium、Readability、MDict 解析器。</div>
            </div>
          ` : `
            <div class="yy-card" style="padding:12px 16px;display:flex;flex-direction:column;gap:6px;font-size:12.5px;line-height:1.65;">
              <div><strong class="serif">第 0.1.0 版</strong> <span style="font-size:11px;color:var(--yy-t3);margin-left:6px;">2026.11.23</span></div>
              <div>· 书架、进度档案、播客工作台与访谈书页完整桌面端体验</div>
              <div>· 纸本短横线排版、划线虚线批注、跟读墨线与广播剧多人配音</div>
              <div>· 有声书自动句级对齐检查器与多端无账号 WebDAV 同步</div>
            </div>
          `}
          <div style="display:flex;justify-content:flex-end;"><button class="yy-pill primary" onclick="setModal(null)">好</button></div>
        </div>
      </div>`;
  }

  return '';
}
