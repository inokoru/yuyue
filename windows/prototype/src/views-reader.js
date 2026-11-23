// ============================================================================
// Windows 2, 3, 4 & 6 — Forensic 1:1 Parity:
//   - MacReaderView + reader/src/reader.css (.yy-t::after, .yy-meta, ::highlight(yy-note),
//     <yy-from> 出处, <yy-ink>, barAway, TypesetCard subpages, ListenCard, Cast UnsureView & RoleView)
//   - MacPdfView (Warm Night Inversion + ShowCard)
//   - AudiobookScreen + RecordingSettings + TextVersionCard + AudiobookPage (合成中 / 合成好了) + AlignCheck
//   - MacMenuBar / Windows System Tray (Official menubar.svg "门里出声")
// ============================================================================

function renderReaderWindow(S) {
  const panel = S.readerPanel; // null | 'toc' | 'notes' | 'companion' | 'cast'
  const twoCol = S.readerTwoCol !== false;
  const winH = S.chrome === 'mac' ? 720 : 748;
  // barAway (MacReaderView.swift:92): capsule hides during quiet reading unless readerPlaying, barPinned, or listen popover open
  const barVisible = S.readerPlaying || S.readerBarShown || S.readerPopover === 'listen' || !!S.readerOverlay;
  const customStyle = S.paper === 'custom'
    ? `--paper-bg:${S.customPaperBg || '#F3EFE4'};--paper-ink:${S.customPaperInk || '#26251F'};--paper-ink2:#716E62;--paper-line:rgba(38,37,31,0.12);--paper-accent:#1B365D;--paper-pa:#2B4C7E;--paper-pa-soft:rgba(43,76,126,0.11);--paper-glass:rgba(243,239,228,0.88);--paper-main:#1B365D;--paper-on-main:#F3EFE4;--paper-hl:#E9E0C2;--paper-wave:#1B365D;--paper-mk-y:#FBE7A0;--paper-mk-b:#CFE6F4;--paper-mk-p:#F6C7C0;--paper-font:var(--font-serif);--paper-fscale:1.0357;--paper-lh:2.05;--paper-ps:0.3em;--paper-indent:2em;--paper-ls:0.01em;--paper-title-font:var(--font-serif);--paper-title-size:1.7857;--paper-title-ls:0.05em;--paper-title-rule:block;--paper-meta-font:var(--font-serif);--paper-meta-ls:0.06em;--paper-meta-border:0px;--paper-meta-mt:0;--paper-meta-pb:0.2857;`
    : '';
  const fontOverride = S.readerFont === 'sans' ? '--reader-font-override:var(--font-sans);' : (S.readerFont === 'kai' ? '--reader-font-override:var(--font-kai);' : (S.readerFont === 'serif' ? '--reader-font-override:var(--font-serif);' : ''));
  const lhOverride = S.readerLh ? `--reader-lh-override:${S.readerLh};` : '';

  return `
    <div class="win-frame" data-paper="${S.paper}" style="width:1120px;height:${winH}px;--yy-fs:${S.fontSize || 18}px;${customStyle}${fontOverride}${lhOverride}">
      ${renderWinTitleBar('reader')}
      <div class="mac-lights" style="position:absolute;top:19px;left:20px;">
        <span class="mac-dot close"></span><span class="mac-dot min"></span><span class="mac-dot max"></span>
      </div>

      <div class="reader-shell" data-paper="${S.paper}" data-texture="${S.paperTexture || 'none'}" onclick="closeReaderPopovers()">
        <!-- Left Book Page Canvas -->
        <div style="flex:1;position:relative;display:flex;flex-direction:column;min-width:0;">
          <!-- Unified Transparent Toolbar (MacReaderView.swift:440 — 16px flush left/right!) -->
          <div class="reader-toolbar" onclick="event.stopPropagation()">
            <button class="glass-capsule" onclick="openWin('shelf')" style="height:30px;padding:0 12px 0 8px;display:inline-flex;align-items:center;gap:3px;font-size:12px;color:var(--paper-ink);">
              ${ICONS.chevLeft}<span>书架</span>
            </button>

            ${S.readerSearchBar ? `
              <div class="glass-capsule" style="height:30px;padding:0 8px 0 10px;display:inline-flex;align-items:center;gap:8px;">
                <span style="color:var(--paper-ink2);">${ICONS.search}</span>
                <input value="通灵宝玉" style="width:124px;font-size:12.5px;font-weight:600;color:var(--paper-ink);" />
                <span class="tabular" style="font-size:11px;color:var(--paper-ink2);">第 1 / 5 处</span>
                <span style="width:0.5px;height:14px;background:var(--paper-line);"></span>
                <button style="color:var(--paper-ink);">${ICONS.chevLeft}</button>
                <button style="color:var(--paper-ink);">${ICONS.chevRight}</button>
                <button onclick="toggleReaderSearch(false)" style="width:20px;height:20px;border-radius:50%;background:var(--paper-line);display:flex;align-items:center;justify-content:center;color:var(--paper-ink);">${ICONS.xmark}</button>
              </div>
            ` : `
              <div style="text-align:center;min-width:200px;">
                <div style="font-size:13px;font-weight:600;color:var(--paper-ink);">红楼梦</div>
                <div class="tabular" style="font-size:10.5px;color:var(--paper-ink2);">曹雪芹 · 第八回 · 第 3 页</div>
              </div>
            `}

            <!-- Right Frosted Glass Tool Capsule (8 GlassTools, 34x26 each) -->
            <div class="glass-capsule" style="height:32px;padding:0 3px;display:inline-flex;align-items:center;gap:2px;">
              <button class="glass-tool ${S.readerPlaying ? 'on' : ''}" title="朗读 / 唤出听书胶囊" onclick="toggleReaderPlay()">${S.readerPlaying ? ICONS.pause : ICONS.play}</button>
              <button class="glass-tool ${panel === 'toc' ? 'on' : ''}" title="目录与书签" onclick="setReaderPanel('toc')">${ICONS.list}</button>
              <button class="glass-tool ${S.readerSearchBar ? 'on' : ''}" title="书内搜索" onclick="toggleReaderSearch(true)">${ICONS.search}</button>
              <button class="glass-tool ${S.readerPopover === 'typeset' ? 'on' : ''}" title="排版与纸色 (Aa)" onclick="setReaderPopover('typeset')">Aa</button>
              <button class="glass-tool ${S.readerBilingual ? 'on' : ''}" title="整页译文" onclick="toggleReaderBilingual()">${ICONS.translate}</button>
              <button class="glass-tool ${panel === 'companion' ? 'on' : ''}" title="书伴问答" onclick="setReaderPanel('companion')">${ICONS.sparkle}</button>
              <button class="glass-tool ${panel === 'cast' ? 'on' : ''}" title="人物与广播剧" onclick="setReaderPanel('cast')">${ICONS.cast}</button>
              <button class="glass-tool ${panel === 'notes' ? 'on' : ''}" title="本书笔记与生词" onclick="setReaderPanel('notes')">${ICONS.noteCard}</button>
            </div>
          </div>

          <!-- Pure Book Page Sheet (1:1 reader/src/reader.css: top 56px, column-gap: 2 * 40px = 80px, NO column rule!) -->
          <div style="flex:1;padding:58px 56px ${barVisible ? '84px' : '48px'};display:grid;grid-template-columns:${twoCol && !panel ? '1fr 1fr' : 'minmax(0,580px)'};justify-content:center;column-gap:80px;overflow:hidden;position:relative;">
            <!-- Left Column -->
            <div style="display:flex;flex-direction:column;">
              <!-- .yy-t + .yy-t::after (56px x 0.5px #1B365D short rule on 纸本/芦苇!) -->
              <h1 class="yy-t">第八回　比通灵金莺微露意</h1>
              <!-- .yy-meta with eye & headphones SVG icons (reader.css:278-317) -->
              <div class="yy-meta">
                <span>曹雪芹</span>
                <span>·</span>
                <span class="yy-meta-item">${ICONS.eyeTiny}<span>读 12 分钟</span></span>
                <span>·</span>
                <span class="yy-meta-item">${ICONS.headphonesTiny}<span>听 18 分钟</span></span>
              </div>

              <p class="yy-p">
                话说宝玉来至梨香院中，先入薛姨妈室中来，正见薛姨妈打点针黹与丫鬟们呢。宝玉忙请了安，薛姨妈忙一把拉了他，抱入怀内，笑说：“这么冷天，我的儿，难为你想着我！”
              </p>
              <p class="yy-p">
                宝钗自里间出来，宝玉看她头上挽着漆黑油光的纂儿，蜜合色棉袄，玫瑰紫二色金银鼠比肩褂。<span class="yy-hl-fill-y yy-has-note" onclick="event.stopPropagation();setReaderOverlay('selection')" title="含想法的划线（底部 1.5px 靛蓝虚线）· 点击查看划线浮卡">宝钗因笑说道：“成日家说你的这玉，究竟未曾细细的赏鉴，我今儿倒要瞧瞧。”</span>说着便挪近前来。
              </p>
              ${S.readerBilingual ? `<p style="font-size:12.5px;line-height:1.65;color:var(--paper-ink2);margin:0 0 0.6em;">Baochai emerged from the inner chamber and smiled: "Everyone speaks day in and day out of this jade of yours, yet I have never examined it closely—let me have a look today."</p>` : ''}
            </div>

            <!-- Right Column (when twoCol && !panel) -->
            ${twoCol && !panel ? `
              <div style="display:flex;flex-direction:column;justify-content:space-between;">
                <div>
                  <p class="yy-p" style="position:relative;">
                    ${S.readerShowCitation ? `<span class="yy-from-pill">出处</span>` : ''}
                    宝玉亦凑了上去，从项上摘了下来，递在宝钗手内。宝钗托于掌上，只见大如雀卵，灿若明霞，莹润如酥，五色花纹缠护。正面乃篆成八个字：<span class="${S.readerShowCitation ? 'yy-source-cited' : ''} ${S.readerPlaying ? 'yy-ink-sentence' : ''}" onclick="event.stopPropagation();toggleReaderCitation()" style="cursor:pointer;" title="点此切换书伴「出处」左边栏标记">“莫失莫忘，仙寿恒昌。”</span><sup class="yy-sup-fn" onclick="event.stopPropagation();setReaderOverlay('footnote')" title="查看页下注">1</sup>
                  </p>
                  <p class="yy-p">
                    莺儿在旁嘻嘻笑道：<span class="yy-hl-wavy" onclick="event.stopPropagation();setReaderOverlay('selection')">“我听这两句话，倒像和姑娘的项圈上的两句话是一对儿。”</span>宝玉听了，忙笑道：“原来姐姐那项圈上也有八个字，我也赏鉴赏鉴。”另附英文外文查词演示：<span class="georgia vocab-dotted" onclick="event.stopPropagation();setReaderOverlay('word')">incredulity</span> and subtle destiny.
                  </p>
                  ${S.readerBilingual ? `<p style="font-size:12.5px;line-height:1.65;color:var(--paper-ink2);margin:0 0 0.5em;">Ying'er giggled beside them: "Hearing those two phrases, they sound just like a matching pair with the two phrases on our young lady's golden locket!"</p>` : ''}
                </div>

                <!-- Publisher Footnote Block (.yy-footnote — reader.css:358) -->
                <div style="border-top:0.5px solid var(--paper-line);padding-top:8px;font-size:calc(var(--yy-fs,18px) * 0.74);line-height:1.65;color:var(--paper-ink2);">
                  <span style="color:var(--paper-pa);font-weight:600;margin-right:4px;">1</span>庚辰本脂批：“八字乃通部金玉姻缘之伏线，与癞头和尚所镌金锁吉谶遥相呼应。”
                </div>
              </div>
            ` : ''}

            ${renderReaderFloatingCards(S)}
          </div>

          <!-- Bottom barAway Reading Capsule OR Quiet 1.5px Progress Hairline (MacReaderView.swift:65) -->
          ${barVisible ? `
            <div style="position:absolute;left:50%;bottom:18px;transform:translateX(-50%);display:flex;flex-direction:column;align-items:center;gap:10px;z-index:42;" onclick="event.stopPropagation()">
              ${renderReaderBottomOverlays(S)}

              <div class="glass-capsule" style="width:360px;height:46px;padding:5px 6px;display:flex;align-items:center;gap:4px;position:relative;">
                <button onclick="setReaderPanel('toc')" style="width:32px;height:36px;display:flex;align-items:center;justify-content:center;color:var(--paper-ink);">${ICONS.list}</button>
                <button onclick="setReaderPopover('listen')" style="flex:1;height:36px;padding:0 8px;border-radius:12px;background:${S.readerPopover === 'listen' ? 'var(--paper-line)' : 'transparent'};text-align:left;display:flex;flex-direction:column;justify-content:center;">
                  <div class="tabular truncate" style="font-size:12px;font-weight:600;color:var(--paper-ink);">${S.readerPlaying ? '第 6 / 38 句 · 沉舟（正在跟读）' : '第八回 · 比通灵金莺微露意'}</div>
                  <div class="tabular truncate" style="font-size:10px;color:var(--paper-ink2);">34% · 本章还剩 9 分钟 · 点此展开听书卡</div>
                </button>
                <button style="width:28px;height:36px;display:flex;align-items:center;justify-content:center;color:var(--paper-ink);" title="上一句">${ICONS.chevLeft}</button>
                <button onclick="toggleReaderPlay()" style="width:36px;height:36px;border-radius:50%;background:var(--tint-fill);color:#FFF;display:flex;align-items:center;justify-content:center;">${S.readerPlaying ? ICONS.pause : ICONS.play}</button>
                <button onclick="toggleReaderBar(false)" title="收起控制栏，安静阅读" style="width:28px;height:36px;display:flex;align-items:center;justify-content:center;color:var(--paper-ink2);">${ICONS.chevDown}</button>
                <div style="position:absolute;left:22px;right:22px;bottom:1px;height:2px;border-radius:99px;background:var(--paper-line);overflow:hidden;">
                  <div style="width:34%;height:100%;background:var(--paper-ink);"></div>
                </div>
              </div>
            </div>
          ` : `
            <!-- Quiet Reading Mode: Only 1.5px bottom progress hairline + hover zone to summon capsule! -->
            <div onclick="event.stopPropagation();toggleReaderBar(true)" title="静读模式已自动收起胶囊 · 点击底部或点右上角 ▶ 唤出控制栏" style="position:absolute;left:0;right:0;bottom:0;height:14px;display:flex;align-items:flex-end;cursor:pointer;z-index:40;">
              <div style="width:100%;height:1.5px;background:var(--paper-line);">
                <div style="width:34%;height:100%;background:var(--paper-ink);opacity:0.32;"></div>
              </div>
            </div>
          `}

          ${renderReaderToolbarPopovers(S)}
        </div>

        <!-- Right 288px Floating Side Drawer -->
        ${panel ? renderReaderSidePanel(panel, S) : ''}
      </div>

      ${renderReaderModals(S)}
    </div>
  `;
}

function renderReaderFloatingCards(S) {
  if (S.readerOverlay === 'selection') {
    return `
      <div class="yy-float-card" onclick="event.stopPropagation()" style="position:absolute;top:128px;left:72px;width:332px;color:var(--yy-t1);z-index:48;">
        <div style="padding:9px 16px 6px;display:flex;align-items:center;gap:12px;">
          ${[
            { c: '#FBE7A0', k: '1', on: true },
            { c: '#CFE6F4', k: '2', on: false },
            { c: '#F6C7C0', k: '3', on: false },
            { c: '#EF7F71', k: '4', wave: true }
          ].map(sw => `
            <div style="display:flex;flex-direction:column;align-items:center;gap:3px;cursor:pointer;" onclick="showProtoToast('已切换为 ${sw.k} 号划线样式')">
              <span style="width:20px;height:20px;border-radius:50%;background:${sw.c};border:${sw.on ? '2px solid var(--tint-accent)' : '0.5px solid rgba(0,0,0,0.15)'};display:flex;align-items:center;justify-content:center;font-size:10px;color:#FFF;">${sw.wave ? '〰' : ''}</span>
              <span class="yy-key" style="min-width:16px;height:15px;font-size:9.5px;">${sw.k}</span>
            </div>
          `).join('')}
          <div style="margin-left:auto;display:flex;align-items:center;gap:4px;">
            <button style="width:24px;height:25px;border-radius:6px;background:var(--tint-soft);color:var(--tint-accent);font-family:var(--font-serif);text-decoration:underline;font-size:14px;" title="直线">A</button>
            <button style="width:24px;height:25px;border-radius:6px;font-family:var(--font-serif);border-bottom:1.5px dotted var(--yy-t2);font-size:14px;" title="点线">A</button>
            <button style="width:25px;height:25px;color:var(--yy-danger);display:flex;align-items:center;justify-content:center;" onclick="setReaderOverlay(null)" title="删除划线">${ICONS.trash}</button>
          </div>
        </div>
        <div style="padding:6px 14px 8px;border-top:0.5px solid var(--yy-line);background:var(--yy-leaf);font-size:12px;color:var(--tint-accent);" class="kai">
          想法：宝钗主动请看通灵玉，引出金锁八字，设笔极自然。
        </div>
        <div style="height:0.5px;background:var(--yy-line);"></div>
        <div style="padding:8px 6px;display:flex;">
          ${[
            { l: '改想法', ico: ICONS.pencil, act: "setModal('thoughtSheet')" },
            { l: '从这读', ico: ICONS.headphones, act: "toggleReaderPlay();setReaderOverlay(null)" },
            { l: '问', ico: ICONS.sparkle, accent: true, act: "setReaderPanel('companion');setReaderOverlay(null)" },
            { l: '分享', ico: ICONS.share, act: "setModal('quoteShare')" },
            { l: '复制', ico: ICONS.noteCard, act: "showProtoToast('已复制原文');setReaderOverlay(null)" }
          ].map(a => `
            <button onclick="${a.act}" style="flex:1;height:41px;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:4px;font-size:11px;color:${a.accent ? 'var(--tint-accent)' : 'var(--yy-t1)'};font-weight:${a.accent ? '600' : '400'};">
              ${a.ico}<span>${a.l}</span>
            </button>
          `).join('')}
        </div>
      </div>`;
  }
  if (S.readerOverlay === 'word') {
    return `
      <div class="yy-float-card" onclick="event.stopPropagation()" style="position:absolute;top:236px;right:76px;width:350px;padding:14px 16px 12px;color:var(--yy-t1);z-index:48;">
        <div style="display:flex;align-items:center;justify-content:space-between;">
          <div><span class="georgia" style="font-size:21px;font-weight:700;">incredulity</span> <span style="font-size:11.5px;color:var(--yy-t2);margin-left:6px;">/ˌɪn.krəˈdjuː.lə.ti/</span></div>
          <button style="width:28px;height:28px;border-radius:50%;background:var(--yy-inset);display:flex;align-items:center;justify-content:center;">${ICONS.speaker}</button>
        </div>
        <div style="margin-top:6px;font-size:13px;"><span class="georgia" style="font-style:italic;color:var(--yy-t3);">n.</span> 怀疑；不轻信；难以置信</div>
        <div class="georgia" style="margin-top:5px;font-size:11.5px;font-style:italic;color:var(--yy-t2);">“…incredulity and subtle destiny.”</div>
        <div style="height:0.5px;background:var(--yy-line);margin:11px 0 9px;"></div>
        <div style="display:flex;align-items:center;justify-content:space-between;font-size:10.5px;color:var(--yy-t3);">
          <span>牛津高阶 MDict · 3 本书出现 19 次</span>
          <div style="display:flex;gap:6px;">
            <button style="height:24px;padding:0 9px;border-radius:99px;background:var(--tint-black);color:var(--tint-on-black);font-size:11px;font-weight:600;" onclick="showProtoToast('已标为「已掌握」');setReaderOverlay(null)">✓ 已在生词本</button>
          </div>
        </div>
      </div>`;
  }
  if (S.readerOverlay === 'footnote') {
    return `
      <div class="yy-float-card" onclick="event.stopPropagation()" style="position:absolute;top:210px;right:88px;width:320px;padding:12px 14px;color:var(--yy-t1);z-index:48;">
        <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:6px;">
          <span style="font-size:11px;font-weight:600;color:var(--tint-accent);">注释 [1]</span>
          <button onclick="setReaderOverlay(null)" style="color:var(--yy-t3);">${ICONS.xmark}</button>
        </div>
        <div class="serif" style="font-size:13px;line-height:1.7;">庚辰本脂砚斋批语：“八字乃通部金玉姻缘之伏线，与癞头和尚所镌宝钗金锁‘不离不弃，芳龄永继’互成吉谶。”</div>
      </div>`;
  }
  return '';
}

function renderReaderBottomOverlays(S) {
  if (S.readerOverlay === 'recap') {
    return `
      <div class="yy-float-card" style="width:396px;padding:14px 16px 12px;border-radius:16px;color:var(--yy-t1);">
        <div style="display:flex;align-items:center;gap:7px;">
          <span style="color:var(--tint-accent);">${ICONS.history}</span>
          <strong style="font-size:13px;">前情提要</strong>
          <span style="font-size:11px;color:var(--yy-t3);margin-left:auto;">3 天没读了</span>
        </div>
        <div style="padding:10px 0;font-size:12.5px;line-height:1.65;display:flex;flex-direction:column;gap:6px;">
          <div>1. 刘姥姥一进荣国府得凤姐周济，周瑞家的送宫花引出冷香丸。</div>
          <div>2. 宝玉随凤姐过宁府结识秦钟，约同入贾氏家塾读书。</div>
          <div>3. 宝玉赴梨香院探视微恙的薛宝钗，二人首次互观通灵玉与金锁。</div>
        </div>
        <div style="padding-top:9px;border-top:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:space-between;">
          <span style="font-size:11px;color:var(--yy-t3);">只提要你读过的前七回，绝不剧透</span>
          <button class="yy-rec-btn main" style="height:28px;font-size:12px;" onclick="setReaderOverlay(null)">接着读</button>
        </div>
      </div>`;
  }
  if (S.readerOverlay === 'elsewhere') {
    return `
      <div class="yy-float-card" style="width:396px;padding:14px 16px 12px;border-radius:16px;color:var(--yy-t1);">
        <div style="display:flex;justify-content:space-between;align-items:center;">
          <strong style="font-size:12.5px;">iPhone 上读到了更后面</strong>
          <span style="font-size:10.5px;color:var(--yy-t3);">15 分钟前</span>
        </div>
        <div class="serif" style="margin:9px 0;padding-left:10px;border-left:2px solid var(--tint-accent);font-size:12.5px;color:var(--yy-t2);">“第十七回 · 大观园试才题对额，荣国府归省庆元宵”</div>
        <div style="padding-top:9px;border-top:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:space-between;">
          <span class="tabular" style="font-size:11px;color:var(--yy-t3);">12% → 34%</span>
          <div style="display:flex;gap:6px;">
            <button class="yy-rec-btn" style="height:28px;font-size:12px;" onclick="setReaderOverlay(null)">留在这里</button>
            <button class="yy-rec-btn main" style="height:28px;font-size:12px;" onclick="setReaderOverlay(null);showProtoToast('已跳至第 17 回')">跳过去</button>
          </div>
        </div>
      </div>`;
  }
  if (S.readerOverlay === 'sleep') {
    return `
      <div class="yy-float-card" style="border-radius:999px;padding:5px 6px 5px 14px;display:flex;align-items:center;gap:8px;color:var(--yy-t1);">
        <span style="font-size:11.5px;">昨晚定时从 第八回 04:12 开始，之后睡着了？</span>
        <button class="yy-pill primary sm" onclick="setReaderOverlay(null);showProtoToast('已回到昨晚定时起点')">回到那里</button>
        <button class="yy-pill sm" onclick="setReaderOverlay(null)">不用</button>
      </div>`;
  }
  return '';
}

// TypesetCard (with subpages: root | paperEditor | moreSettings!) & ListenCard (MacReaderCards.swift)
function renderReaderToolbarPopovers(S) {
  if (S.readerPopover === 'typeset') {
    const sub = S.typesetSub || 'root'; // 'root' | 'paperEditor' | 'moreSettings'
    const papers = [
      { id: 'plain', name: '素白', bg: '#FFFFFF', ink: '#2A2B2E' },
      { id: 'kami', name: '纸本', bg: '#F5F4ED', ink: '#26251F' },
      { id: 'sage', name: '芦苇', bg: '#E6E7DC', ink: '#2A2C24' },
      { id: 'eink', name: '铅纸', bg: '#E9E8E3', ink: '#141414' },
      { id: 'dark', name: '暗黑', bg: '#121314', ink: '#D6D6D8' }
    ];

    if (sub === 'paperEditor') {
      return `
        <!-- PaperEditor Subpage (调纸 — MacReaderCards.swift) -->
        <div class="yy-float-card" onclick="event.stopPropagation()" style="position:absolute;top:48px;right:16px;width:304px;padding:14px 16px;border-radius:16px;color:var(--yy-t1);z-index:55;display:flex;flex-direction:column;gap:12px;">
          <div style="display:flex;align-items:center;justify-content:space-between;">
            <button onclick="setTypesetSub('root')" style="display:inline-flex;align-items:center;gap:4px;font-size:12.5px;font-weight:600;">${ICONS.chevLeft}<span>调纸</span></button>
            <button class="yy-pill primary xs" onclick="setPaper('custom');setTypesetSub('root');showProtoToast('已保存「我的纸 · 宣白」')">存为我的纸</button>
          </div>
          <div>
            <div style="font-size:11px;color:var(--yy-t2);margin-bottom:6px;">色相与纸温</div>
            <div style="height:12px;border-radius:99px;background:linear-gradient(90deg,#F5EAE6,#F5EFE2,#EEF2E2,#E4F0EC,#E6ECF5,#F0E8F5);border:0.5px solid rgba(0,0,0,0.12);position:relative;cursor:pointer;" onclick="setCustomPaper('#EFECE2','#26251F')">
              <span style="position:absolute;left:24%;top:50%;transform:translate(-50%,-50%);width:15px;height:15px;border-radius:50%;background:#FFFFFF;border:1.5px solid var(--yy-t1);"></span>
            </div>
          </div>
          <div>
            <div style="font-size:11px;color:var(--yy-t2);margin-bottom:6px;">纸纹质感</div>
            <div class="yy-seg sm" style="width:100%;">
              ${[['none','光滑'],['grain','细砂'],['xuan','宣纸'],['linen','亚麻']].map(tx => `
                <button class="yy-seg-btn ${(S.paperTexture || 'none') === tx[0] ? 'active' : ''}" onclick="setPaperTexture('${tx[0]}')">${tx[1]}</button>
              `).join('')}
            </div>
          </div>
          <div>
            <div style="font-size:11px;color:var(--yy-t2);margin-bottom:6px;">墨色</div>
            <div style="display:flex;gap:10px;">
              ${[['#1F1E1B','浓墨'],['#2B3A4E','黛蓝'],['#3A3228','茶褐'],['#464844','淡墨']].map((ik, i) => `
                <button onclick="setCustomPaper(S.customPaperBg || '#F3EFE4', '${ik[0]}')" style="flex:1;padding:6px 4px;border-radius:8px;background:var(--yy-inset);display:flex;flex-direction:column;align-items:center;gap:4px;">
                  <span style="width:18px;height:18px;border-radius:50%;background:${ik[0]};box-shadow:${i===0?'0 0 0 2px var(--yy-card),0 0 0 3.5px var(--tint-accent)':'none'};"></span>
                  <span style="font-size:10px;color:var(--yy-t2);">${ik[1]}</span>
                </button>
              `).join('')}
            </div>
          </div>
        </div>`;
    }

    if (sub === 'moreSettings') {
      return `
        <!-- moreSettings 8-Row Subpage (更多排版 — MacReaderCards.swift) -->
        <div class="yy-float-card" onclick="event.stopPropagation()" style="position:absolute;top:48px;right:16px;width:304px;padding:12px 16px;border-radius:16px;color:var(--yy-t1);z-index:55;display:flex;flex-direction:column;">
          <div style="padding-bottom:8px;border-bottom:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:space-between;">
            <button onclick="setTypesetSub('root')" style="display:inline-flex;align-items:center;gap:4px;font-size:12.5px;font-weight:600;">${ICONS.chevLeft}<span>更多排版</span></button>
            <button onclick="showProtoToast('已恢复默认排版')" style="font-size:11px;color:var(--tint-accent);">恢复默认</button>
          </div>
          ${[
            ['翻页方式', `<div class="yy-seg sm"><button class="yy-seg-btn active">翻页</button><button class="yy-seg-btn">滚动</button></div>`],
            ['页边距', `<div class="yy-seg sm"><button class="yy-seg-btn">窄</button><button class="yy-seg-btn active">适中</button><button class="yy-seg-btn">宽</button></div>`],
            ['段距', `<div class="yy-seg sm"><button class="yy-seg-btn">无</button><button class="yy-seg-btn active">适中</button><button class="yy-seg-btn">宽</button></div>`],
            ['首行缩进两字', `<div class="yy-switch on" onclick="this.classList.toggle('on')"></div>`],
            ['两端对齐', `<div class="yy-switch on" onclick="this.classList.toggle('on')"></div>`],
            ['中文标点挤压', `<div class="yy-switch on" onclick="this.classList.toggle('on')"></div>`],
            ['简繁转换', `<div class="yy-seg sm"><button class="yy-seg-btn active">原文</button><button class="yy-seg-btn">简</button><button class="yy-seg-btn">繁</button></div>`],
            ['英文混排字体', `<span class="georgia" style="font-size:11.5px;color:var(--yy-t2);">Georgia ›</span>`]
          ].map((r, i) => `
            ${i > 0 ? `<div style="height:0.5px;background:var(--yy-line);"></div>` : ''}
            <div style="min-height:36px;display:flex;align-items:center;justify-content:space-between;font-size:12px;">
              <span>${r[0]}</span>${r[1]}
            </div>
          `).join('')}
        </div>`;
    }

    return `
      <!-- TypesetCard Root (304px wide popover flush right:16px under Aa!) -->
      <div class="yy-float-card" onclick="event.stopPropagation()" style="position:absolute;top:48px;right:16px;width:304px;padding:16px 16px 8px;border-radius:16px;color:var(--yy-t1);z-index:55;display:flex;flex-direction:column;gap:12px;">
        <!-- 5 Presets + 调纸 -->
        <div style="display:grid;grid-template-columns:repeat(6,1fr);gap:5px;">
          ${papers.map(p => {
            const on = S.paper === p.id;
            return `
              <button onclick="setPaper('${p.id}')" style="display:flex;flex-direction:column;align-items:center;gap:4px;">
                <span style="padding:1.5px;border-radius:10px;border:${on ? '1.5px solid var(--yy-t1)' : '1.5px solid transparent'};">
                  <i class="serif" style="width:36px;height:32px;border-radius:7.5px;background:${p.bg};color:${p.ink};border:0.5px solid rgba(0,0,0,0.12);display:flex;align-items:center;justify-content:center;font-style:normal;font-size:13.5px;">文</i>
                </span>
                <span style="font-size:10px;color:${on ? 'var(--yy-t1)' : 'var(--yy-t2)'};font-weight:${on ? '600' : '400'};">${p.name}</span>
              </button>`;
          }).join('')}
          <button onclick="setTypesetSub('paperEditor')" style="display:flex;flex-direction:column;align-items:center;gap:4px;" title="我的纸 · 自定义色相、纸纹与墨色">
            <span style="padding:1.5px;border-radius:10px;border:${S.paper === 'custom' ? '1.5px solid var(--yy-t1)' : '1.5px solid transparent'};">
              <i style="width:36px;height:32px;border-radius:7.5px;background:var(--yy-inset);color:var(--yy-t2);border:0.5px dashed var(--yy-t3);display:flex;align-items:center;justify-content:center;">${ICONS.sliders}</i>
            </span>
            <span style="font-size:10px;color:var(--yy-t2);">调纸</span>
          </button>
        </div>

        <!-- 4-Col Font Tiles -->
        <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:5px;">
          ${[
            { id: 'sans', n: '系统', cls: '' },
            { id: 'serif', n: '宋体', cls: 'serif' },
            { id: 'kai', n: '霞鹜文楷', cls: 'kai' },
            { id: 'more', n: '更多字体', cls: 'serif', more: true }
          ].map(f => {
            const curFont = S.readerFont || (S.paper === 'plain' || S.paper === 'dark' ? 'sans' : 'serif');
            const on = curFont === f.id;
            return `
              <button onclick="${f.more ? "showProtoToast('打开系统字体册选择任意中英文字体')" : `setReaderFont('${f.id}')`}" style="padding:8px 4px 7px;border-radius:9px;border:${on ? '1.5px solid var(--tint-accent)' : '0.5px solid var(--yy-line)'};display:flex;flex-direction:column;align-items:center;gap:2px;">
                <span class="${f.cls}" style="font-size:19px;height:24px;line-height:24px;">${f.more ? '永+' : '永'}</span>
                <span style="font-size:10px;color:${on ? 'var(--yy-t1)' : 'var(--yy-t2)'};font-weight:${on ? '600' : '400'};">${f.n}</span>
              </button>`;
          }).join('')}
        </div>

        <!-- Font Size Bar -->
        <div style="height:34px;padding:0 5px 0 10px;border-radius:12px;background:var(--yy-inset);display:flex;align-items:center;gap:10px;">
          <span class="serif" style="font-size:12px;">A</span>
          <input type="range" class="yy-range" min="14" max="26" value="${S.fontSize || 18}" oninput="setReaderFontSize(this.value)" style="flex:1;" />
          <span class="serif" style="font-size:18px;">A</span>
          <span class="tabular" style="width:26px;height:26px;border-radius:8px;background:var(--yy-card);display:flex;align-items:center;justify-content:center;font-size:12px;font-weight:700;">${S.fontSize || 18}</span>
        </div>

        <!-- Line Spacing & Single/Dual Column -->
        <div style="display:flex;gap:8px;">
          <div class="yy-seg sm" style="flex:1;">
            <button class="yy-seg-btn ${S.readerLh === 1.65 ? 'active' : ''}" onclick="setReaderLh(1.65)">紧</button>
            <button class="yy-seg-btn ${!S.readerLh ? 'active' : ''}" onclick="setReaderLh(null)">适中</button>
            <button class="yy-seg-btn ${S.readerLh === 2.3 ? 'active' : ''}" onclick="setReaderLh(2.3)">松</button>
          </div>
          <div class="yy-seg sm" style="flex:1.25;">
            <button class="yy-seg-btn ${!S.readerTwoCol ? 'active' : ''}" onclick="setReaderTwoCol(false)">单栏</button>
            <button class="yy-seg-btn ${S.readerTwoCol !== false ? 'active' : ''}" onclick="setReaderTwoCol(true)">双栏</button>
          </div>
        </div>

        <!-- Footer Rows -->
        <div style="display:flex;flex-direction:column;">
          <div style="height:36px;border-top:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:space-between;font-size:12px;">
            <span>只用于这本书</span><div class="yy-switch" onclick="this.classList.toggle('on')"></div>
          </div>
          <button onclick="setModal('replaceRules')" style="height:36px;border-top:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:space-between;font-size:12px;">
            <span>净化替换规则</span><span style="font-size:11px;color:var(--yy-t3);">2 条启用 ›</span>
          </button>
          <button onclick="setTypesetSub('moreSettings')" style="height:36px;border-top:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:space-between;font-size:12px;">
            <span>更多</span><span style="font-size:11px;color:var(--yy-t3);">边距、段距、简繁 ›</span>
          </button>
        </div>
      </div>`;
  }

  if (S.readerPopover === 'listen') {
    return `
      <!-- ListenCard (460px wide popover above Reading Capsule — MacReaderCards.swift) -->
      <div class="yy-float-card" onclick="event.stopPropagation()" style="position:absolute;bottom:72px;left:50%;transform:translateX(-50%);width:460px;padding:14px 16px;border-radius:16px;color:var(--yy-t1);z-index:55;display:flex;flex-direction:column;gap:12px;">
        <div style="display:flex;align-items:center;justify-content:space-between;">
          <strong style="font-size:13.5px;">听书</strong>
          <div style="font-size:10.5px;color:var(--yy-t3);display:flex;align-items:center;gap:5px;">
            <span class="yy-key">空格</span> 暂停 <span class="yy-key">${S.chrome === 'mac' ? '⌘[' : 'Ctrl+['}</span><span class="yy-key">]</span> 上下句
          </div>
        </div>

        <div style="padding:9px 11px;border-radius:14px;background:var(--yy-inset);display:flex;align-items:center;gap:10px;">
          ${renderServiceTile('laptop', 30)}
          <div style="flex:1;min-width:0;">
            <div style="font-size:12.5px;font-weight:600;">沉舟 <span style="font-size:9.5px;padding:1px 5px;border-radius:4px;background:var(--tint-soft);color:var(--tint-accent);margin-left:4px;">这台电脑</span></div>
            <div style="font-size:10.5px;color:var(--yy-t2);">本机神经声卡 · 广播剧 4 位角色声线已就绪</div>
          </div>
          <button style="height:26px;padding:0 10px;border-radius:99px;background:var(--yy-card);font-size:11px;font-weight:600;" onclick="showProtoToast('正在试听「沉舟」')">▶ 试听</button>
          <button onclick="openWin('settings','listening');setVoiceAuditionOpen(true)" style="font-size:11px;color:var(--tint-accent);font-weight:500;">换声音 ›</button>
        </div>

        <div>
          <div style="display:flex;align-items:baseline;gap:8px;margin-bottom:6px;">
            <span style="font-size:11.5px;font-weight:600;">语速</span>
            <strong class="tabular" style="font-size:16px;">1.0×</strong>
            <span style="font-size:10px;padding:1px 5px;border-radius:5px;background:var(--yy-inset);color:var(--yy-t2);">原调不失真</span>
            <span style="margin-left:auto;font-size:10.5px;color:var(--yy-t3);">本章约剩 9 分钟</span>
          </div>
          <input type="range" class="yy-range" min="7" max="20" value="10" style="width:100%;" />
          <div class="tabular" style="display:flex;justify-content:space-between;font-size:10px;color:var(--yy-t3);margin-top:4px;"><span>0.7×</span><span>1×</span><span>1.25×</span><span>1.5×</span><span>2×</span></div>
        </div>

        <div>
          <div style="display:flex;justify-content:space-between;margin-bottom:6px;font-size:11.5px;"><strong>定时停止</strong><span style="font-size:10.5px;color:var(--yy-t3);">停下时自动记下位置</span></div>
          <div class="yy-seg" style="width:100%;">
            ${['关', '本句完', '本章完', '15 分', '30 分', '60 分'].map((t, i) => `<button class="yy-seg-btn ${i === 0 ? 'active' : ''}" style="height:30px;font-size:11px;">${t}</button>`).join('')}
          </div>
        </div>

        <div style="display:grid;grid-template-columns:repeat(4,1fr);gap:6px;">
          ${[
            { t: '广播剧', d: '分角演绎', on: true, act: "setReaderPanel('cast');setReaderPopover(null)" },
            { t: '跟读墨线', d: '句下细线', on: true, act: "toggleReaderPlay()" },
            { t: '自动翻页', d: '读完翻页', on: true, act: "" },
            { t: '环境底声', d: '梨香冬雪', on: false, act: "" }
          ].map(tl => `
            <button onclick="${tl.act}" style="height:44px;padding:0 9px;border-radius:12px;background:${tl.on ? 'var(--tint-soft)' : 'var(--yy-inset)'};color:${tl.on ? 'var(--tint-accent)' : 'var(--yy-t1)'};text-align:left;">
              <div style="font-size:11.5px;font-weight:600;">${tl.t}</div>
              <div style="font-size:10px;opacity:0.78;">${tl.d}</div>
            </button>
          `).join('')}
        </div>
      </div>`;
  }
  return '';
}

function renderReaderSidePanel(panel, S) {
  return `
    <aside class="yy-float-card" onclick="event.stopPropagation()" style="width:288px;margin:50px 14px 14px 0;border-radius:16px;color:var(--yy-t1);display:flex;flex-direction:column;overflow:hidden;flex-shrink:0;z-index:35;">
      ${panel === 'toc' ? `
        <div style="padding:14px 14px 10px;">
          <div style="display:flex;align-items:center;gap:10px;">
            ${renderBookCover(BOOKS[0], 30)}
            <div style="flex:1;min-width:0;"><div style="font-size:14px;font-weight:700;">红楼梦</div><div style="font-size:10.5px;color:var(--yy-t3);">曹雪芹 · 共 120 回</div></div>
          </div>
          <div style="margin-top:10px;height:2.5px;border-radius:99px;background:var(--yy-inset);overflow:hidden;"><div style="width:34%;height:100%;background:var(--tint-fill);"></div></div>
          <div style="margin-top:6px;display:flex;justify-content:space-between;font-size:10.5px;"><strong style="color:var(--tint-accent);">读到第 8 回 · 34%</strong><span style="color:var(--yy-t3);">读 6 小时</span></div>
          <div class="yy-seg sm" style="width:100%;margin-top:10px;">
            <button class="yy-seg-btn active">目录</button><button class="yy-seg-btn">书签 2</button><button class="yy-seg-btn">笔记 2</button>
          </div>
        </div>
        <div style="flex:1;overflow-y:auto;padding:0 8px 12px;">
          ${['第六回 · 刘姥姥一进荣国府', '第七回 · 送宫花贾琏戏熙凤', '第八回 · 比通灵金莺微露意', '第九回 · 恋风流情友入家塾', '第十回 · 金寡妇贪利权受辱'].map((c, i) => `
            <div style="padding:9px 8px;border-radius:11px;background:${i === 2 ? 'var(--tint-soft)' : 'transparent'};cursor:pointer;">
              <div style="font-size:12px;font-weight:${i === 2 ? '600' : '400'};color:${i === 2 ? 'var(--tint-accent)' : (i < 2 ? 'var(--yy-t3)' : 'var(--yy-t1)')};">${c}</div>
              <div style="font-size:10.5px;color:${i === 2 ? 'var(--tint-accent)' : 'var(--yy-t3)'};margin-top:2px;">${i === 2 ? '读到 34% · 还剩 9 分钟' : (i < 2 ? '翻过' : '约 18 分钟')}</div>
            </div>
          `).join('')}
        </div>
      ` : panel === 'companion' ? `
        <div style="padding:14px;display:flex;flex-direction:column;height:100%;">
          <div style="display:flex;align-items:center;gap:8px;">
            <span style="width:24px;height:24px;border-radius:7px;background:var(--tint-soft);color:var(--tint-accent);display:flex;align-items:center;justify-content:center;">${ICONS.sparkle}</span>
            <div style="flex:1;"><div style="font-size:14px;font-weight:700;">书伴</div><div style="font-size:10.5px;color:var(--yy-t3);">红楼梦 · 防剧透</div></div>
            <button style="width:26px;height:26px;border-radius:50%;background:var(--yy-inset);display:flex;align-items:center;justify-content:center;">${ICONS.dots}</button>
          </div>
          <div class="yy-seg sm" style="width:100%;margin-top:10px;">
            <button class="yy-seg-btn active">仅限已读</button><button class="yy-seg-btn">全书范围</button>
          </div>
          <div style="font-size:10.5px;color:var(--yy-t3);margin-top:5px;">截至第 8 回第 3 页，绝不剧透后续情节</div>
          <div style="height:0.5px;background:var(--yy-line);margin:10px 0;"></div>
          <div style="flex:1;overflow-y:auto;display:flex;flex-direction:column;gap:12px;">
            <div style="align-self:flex-end;max-width:215px;padding:8px 12px;border-radius:14px 14px 4px 14px;background:var(--yy-pill);border:0.5px solid var(--yy-pill-line);font-size:12px;">通灵玉上的八个字和宝钗金锁有什么呼应？</div>
            <div style="font-size:12.5px;line-height:1.68;">
              通灵宝玉正面篆刻“莫失莫忘，仙寿恒昌”，恰与薛宝钗金锁上癞头和尚所赠吉谶“不离不弃，芳龄永继”字句工整相对，故莺儿脱口点破“倒像是一对儿”
              <button onclick="toggleReaderCitation(true)" style="font-size:10px;padding:1px 6px;border-radius:99px;background:var(--tint-soft);color:var(--tint-accent);font-weight:600;display:inline-flex;align-items:center;" title="点击在左页边距标出「出处」">出处 · 第 3 页</button>。
              <div style="font-size:10px;color:var(--yy-t3);margin-top:5px;">DeepSeek · 仅检索前 8 回</div>
            </div>
          </div>
          <div style="margin-top:10px;min-height:40px;padding:5px 6px 5px 10px;border-radius:14px;background:var(--yy-bg);border:0.5px solid var(--yy-line);display:flex;align-items:center;gap:6px;">
            <input placeholder="读到哪儿问到哪儿…" style="flex:1;font-size:12px;" />
            <button style="width:28px;height:28px;border-radius:9px;background:var(--tint-black);color:var(--tint-on-black);display:flex;align-items:center;justify-content:center;">${ICONS.arrowUp}</button>
          </div>
        </div>
      ` : panel === 'cast' ? (S.castShowingUnsure ? `
        <!-- CastPanel Inline UnsureView (待确认的台词 — DramaViews.swift:412) -->
        <div style="padding:14px;display:flex;flex-direction:column;gap:10px;overflow-y:auto;">
          <div style="display:flex;align-items:center;justify-content:space-between;">
            <button onclick="toggleCastUnsure(false)" style="display:inline-flex;align-items:center;gap:4px;font-size:13px;font-weight:600;">${ICONS.chevLeft}<span>待确认的台词</span></button>
            <span class="tabular" style="font-size:11px;color:var(--yy-t3);">1 / 3</span>
          </div>
          <div class="yy-card" style="padding:12px 13px;border-radius:12px;display:flex;flex-direction:column;gap:9px;">
            <div style="font-size:10px;color:var(--yy-t3);">第八回 · 梨香院 · 在场 宝玉 宝钗 莺儿</div>
            <div class="serif" style="font-size:13px;line-height:1.68;color:var(--yy-t3);">
              宝玉忙笑道：“原来姐姐那项圈上也有八个字。”<span style="background:rgba(251,231,160,0.55);color:var(--yy-t1);padding:1px 2px;border-radius:2px;">“是个癞头和尚送的，说必须錾在金器上。”</span>
            </div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="font-size:11px;color:var(--yy-t3);">这句是谁说的？</div>
            <button onclick="toggleCastUnsure(false);showProtoToast('✓ 已定为薛宝钗 · 后面 2 句也跟着定')" style="padding:8px 10px;border-radius:10px;background:var(--tint-soft);border:0.5px solid var(--tint-edge);display:flex;align-items:center;gap:8px;">
              <span style="width:22px;height:22px;border-radius:50%;background:rgba(165,101,111,0.2);color:#A5656F;font-size:11px;font-weight:700;display:flex;align-items:center;justify-content:center;">钗</span>
              <span style="font-size:12.5px;font-weight:600;">薛宝钗</span>
              <span style="font-size:10px;color:var(--tint-accent);font-weight:600;">最可能</span>
              <span class="yy-key" style="margin-left:auto;">1</span>
            </button>
            <button onclick="toggleCastUnsure(false);showProtoToast('✓ 已定为莺儿')" style="padding:8px 10px;border-radius:10px;background:var(--yy-bg);border:0.5px solid var(--yy-line);display:flex;align-items:center;gap:8px;">
              <span style="width:22px;height:22px;border-radius:50%;background:rgba(79,118,87,0.2);color:#4F7657;font-size:11px;font-weight:700;display:flex;align-items:center;justify-content:center;">莺</span>
              <span style="font-size:12.5px;">莺儿</span>
              <span style="font-size:10px;color:var(--yy-t3);">在场</span>
              <span class="yy-key" style="margin-left:auto;">2</span>
            </button>
            <div style="display:flex;align-items:center;gap:8px;font-size:11px;color:var(--yy-t2);padding-top:2px;">
              <button>别人…</button><span>·</span><button>不是台词</button>
              <button style="margin-left:auto;color:var(--yy-t3);">跳过 →</button>
            </div>
          </div>
          <div style="font-size:10.5px;color:var(--yy-t3);padding:0 4px;">定了这句，后面 2 句也跟着定。</div>
        </div>
      ` : `
        <!-- CastPanel Root (人物 · 广播剧 — MacReaderView.swift:662) -->
        <div style="padding:14px;display:flex;flex-direction:column;gap:10px;overflow-y:auto;">
          <div style="display:flex;align-items:center;justify-content:space-between;">
            <div><div style="font-size:15px;font-weight:700;">人物</div><div style="font-size:10.5px;color:var(--yy-t3);">读到这里共 9 人</div></div>
            <button onclick="setModal('castRole')" class="yy-pill xs">全部 ›</button>
          </div>
          <div class="yy-card" style="padding:10px 12px;border-radius:12px;display:flex;flex-direction:column;gap:7px;">
            <div style="display:flex;justify-content:space-between;align-items:baseline;">
              <strong style="font-size:12px;">认出 35 句</strong>
              <span class="tabular" style="font-size:10.5px;color:var(--yy-t3);">这一章 38 句台词</span>
            </div>
            <div style="height:3px;border-radius:99px;background:var(--yy-inset);display:flex;overflow:hidden;">
              <span style="width:84%;background:var(--tint-fill);"></span>
              <span style="width:10%;background:var(--yy-warn);"></span>
            </div>
            <button onclick="toggleCastUnsure(true)" style="padding-top:4px;border-top:0.5px solid var(--yy-line);display:flex;align-items:center;gap:6px;font-size:12px;font-weight:500;">
              <span style="width:6px;height:6px;border-radius:50%;background:var(--yy-warn);"></span>
              <span>3 句待确认</span>
              <span style="margin-left:auto;font-size:10.5px;color:var(--yy-warn);">这一章 3 句 ›</span>
            </button>
          </div>

          <div class="yy-card" style="padding:4px 12px;border-radius:12px;">
            ${[
              { av: '书', c: 'var(--yy-inset)', fg: 'var(--yy-t2)', n: '旁白', d: '全书默认', v: '沉舟' },
              { av: '宝', c: 'rgba(79,109,136,0.18)', fg: '#4F6D88', n: '贾宝玉', d: '宝二爷 · 14 句', v: '清朗少年 · 自动' },
              { av: '钗', c: 'rgba(165,101,111,0.18)', fg: '#A5656F', n: '薛宝钗', d: '宝姐姐 · 11 句', v: '苏晚' },
              { av: '莺', c: 'rgba(79,118,87,0.18)', fg: '#4F7657', n: '莺儿', d: '丫鬟 · 6 句', v: '灵秀少女 · 自动' }
            ].map((r, i) => `
              ${i > 0 ? `<div style="height:0.5px;background:var(--yy-line);"></div>` : ''}
              <div onclick="setModal('castRole')" style="height:42px;display:flex;align-items:center;gap:9px;cursor:pointer;">
                <span style="width:26px;height:26px;border-radius:50%;background:${r.c};color:${r.fg};font-size:11px;font-weight:700;display:flex;align-items:center;justify-content:center;flex-shrink:0;">${r.av}</span>
                <div style="flex:1;min-width:0;"><div style="font-size:11.5px;font-weight:600;">${r.n}</div><div class="truncate" style="font-size:10px;color:var(--yy-t3);">${r.d}</div></div>
                <span style="font-size:10.5px;color:var(--tint-accent);white-space:nowrap;">${r.v} ›</span>
              </div>
            `).join('')}
          </div>

          <!-- DramaListenGroup (compact: true — DramaViews.swift:352) -->
          <div class="yy-card" style="padding:6px 12px;border-radius:12px;display:flex;flex-direction:column;">
            <div style="min-height:36px;display:flex;justify-content:space-between;align-items:center;"><strong style="font-size:12px;">广播剧模式</strong><div class="yy-switch on" onclick="this.classList.toggle('on')"></div></div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="min-height:34px;display:flex;justify-content:space-between;align-items:center;font-size:11.5px;"><span>旁白提示词</span><div class="yy-seg sm" style="width:98px;"><button class="yy-seg-btn">保留</button><button class="yy-seg-btn active">略过</button></div></div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="min-height:34px;display:flex;justify-content:space-between;align-items:center;font-size:11.5px;"><span>情感语气</span><div class="yy-switch on" onclick="this.classList.toggle('on')"></div></div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="min-height:34px;display:flex;justify-content:space-between;align-items:center;font-size:11.5px;"><span>环境音与音效</span><div class="yy-switch on" onclick="this.classList.toggle('on')"></div></div>
          </div>
        </div>
      `) : `
        <div style="padding:14px;display:flex;flex-direction:column;gap:12px;overflow-y:auto;">
          <div style="display:flex;align-items:center;justify-content:space-between;">
            <div><div style="font-size:15px;font-weight:700;">笔记</div><div style="font-size:11px;color:var(--yy-t3);">按书里的顺序</div></div>
            <button onclick="openWin('settings','notes')" style="width:28px;height:28px;border-radius:50%;background:var(--yy-inset);display:flex;align-items:center;justify-content:center;">${ICONS.share}</button>
          </div>
          <div class="yy-seg sm"><button class="yy-seg-btn active">划线 2</button><button class="yy-seg-btn">想法 2</button><button class="yy-seg-btn">生词 1</button></div>
          ${NOTES_DATA.filter(n => n.bookId === 'hlm').map(m => `
            <div style="padding:10px 4px;border-bottom:0.5px solid var(--yy-line);">
              <div style="font-size:10.5px;color:var(--tint-accent);font-weight:600;margin-bottom:4px;">第八回 · 第 3 页</div>
              <div class="serif" style="font-size:13px;line-height:1.65;">${m.text}</div>
              ${m.note ? `<div class="kai" style="margin-top:5px;padding-left:8px;border-left:1.5px solid var(--tint-accent);font-size:12px;color:var(--tint-accent);">${m.note}</div>` : ''}
            </div>
          `).join('')}
        </div>
      `}
    </aside>
  `;
}

function renderReaderModals(S) {
  if (S.modal === 'castRole') {
    return `
      <!-- RoleView Sheet (人物页 — CastView.swift:165) -->
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:420px;padding:18px 20px;display:flex;flex-direction:column;gap:12px;">
          <div style="display:flex;align-items:center;gap:12px;">
            <span style="width:36px;height:36px;border-radius:50%;background:rgba(165,101,111,0.2);color:#A5656F;font-size:15px;font-weight:700;display:flex;align-items:center;justify-content:center;">钗</span>
            <div style="flex:1;"><div style="font-size:17px;font-weight:700;">薛宝钗</div><div style="font-size:11.5px;color:var(--yy-t2);">读到这里开口 29 句</div></div>
            <button onclick="setModal(null)" style="width:26px;height:26px;border-radius:50%;background:var(--yy-inset);display:flex;align-items:center;justify-content:center;">${ICONS.xmark}</button>
          </div>
          <div class="yy-card" style="padding:10px 12px;border-radius:12px;display:flex;flex-direction:column;gap:8px;">
            <div style="font-size:11px;color:var(--yy-t3);">书里怎么叫</div>
            <div style="display:flex;gap:6px;flex-wrap:wrap;">
              ${['宝钗', '宝姐姐', '蘅芜君'].map(al => `<span style="height:24px;padding:0 9px;border-radius:99px;background:var(--yy-inset);font-size:11.5px;display:inline-flex;align-items:center;gap:4px;">${al} <i style="opacity:0.5;font-style:normal;">×</i></span>`).join('')}
              <button style="height:24px;padding:0 9px;border-radius:99px;border:0.5px dashed var(--yy-t3);font-size:11.5px;color:var(--yy-t2);">＋ 叫法</button>
            </div>
            <div style="display:flex;justify-content:space-between;align-items:center;padding-top:4px;border-top:0.5px solid var(--yy-line);font-size:12px;">
              <span>是</span>
              <div class="yy-seg sm" style="width:150px;"><button class="yy-seg-btn">男</button><button class="yy-seg-btn active">女</button><button class="yy-seg-btn">不知道</button></div>
            </div>
          </div>
          <div class="yy-card" style="padding:4px 12px;border-radius:12px;">
            <div style="height:38px;display:flex;justify-content:space-between;align-items:center;font-size:12px;"><span>声音</span><button onclick="setModal(null);openWin('settings','listening');setVoiceAuditionOpen(true)" style="color:var(--tint-accent);font-weight:500;">苏晚 ›</button></div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="height:38px;display:flex;justify-content:space-between;align-items:center;font-size:12px;"><span>快慢</span><div style="display:flex;align-items:center;gap:8px;"><input type="range" class="yy-range" min="70" max="140" value="100" style="width:120px;" /><span class="tabular" style="font-size:11px;color:var(--yy-t2);">1.00×</span></div></div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="height:38px;display:flex;justify-content:space-between;align-items:center;font-size:12px;"><span>高低</span><div style="display:flex;align-items:center;gap:8px;"><input type="range" class="yy-range" min="-6" max="6" value="0" style="width:120px;" /><span style="font-size:11px;color:var(--yy-t2);">原调</span></div></div>
          </div>
          <div class="yy-card" style="padding:12px;border-radius:12px;">
            <div class="serif" style="font-size:13px;line-height:1.65;">“成日家说你的这玉，究竟未曾细细的赏鉴，我今儿倒要瞧瞧。”</div>
            <button onclick="showProtoToast('正在试听薛宝钗台词')" style="margin-top:8px;display:inline-flex;align-items:center;gap:6px;font-size:11.5px;color:var(--tint-accent);font-weight:500;">${ICONS.speaker}<span>薛宝钗的一句 · 点一下试听</span></button>
          </div>
        </div>
      </div>`;
  }

  if (S.modal === 'thoughtSheet') {
    return `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:400px;padding:16px 18px;display:flex;flex-direction:column;gap:12px;">
          <div style="display:flex;justify-content:space-between;align-items:center;"><strong style="font-size:14px;">想法</strong><button onclick="setModal(null)">${ICONS.xmark}</button></div>
          <div class="serif" style="padding-left:10px;border-left:2px solid var(--tint-accent);font-size:12.5px;color:var(--yy-t2);line-height:1.6;">“成日家说你的这玉，究竟未曾细细的赏鉴，我今儿倒要瞧瞧。”</div>
          <textarea rows="3" class="kai" style="padding:10px 12px;border-radius:12px;background:var(--yy-card);border:1px solid var(--tint-edge);font-size:13.5px;line-height:1.6;resize:none;">宝钗主动请看通灵玉，引出金锁八字，设笔极自然。</textarea>
          <div style="display:flex;justify-content:flex-end;gap:8px;">
            <button class="yy-pill" onclick="setModal(null)">取消</button>
            <button class="yy-pill primary" onclick="setModal(null);showProtoToast('已保存想法')">保存</button>
          </div>
        </div>
      </div>`;
  }

  if (S.modal === 'quoteShare') {
    const look = S.quoteLook || 'kami';
    const lookStyles = {
      cover: { bg: '#2F3A33', ink: '#F1EADA', sub: '#C8BFA6' },
      kami: { bg: '#F5F4ED', ink: '#26251F', sub: '#716E62' },
      plain: { bg: '#FFFFFF', ink: '#2A2B2E', sub: '#777571' },
      dark: { bg: '#161719', ink: '#E5E5E7', sub: '#8E8E93' }
    }[look];
    return `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:440px;padding:16px 18px 18px;display:flex;flex-direction:column;gap:12px;">
          <div style="display:flex;justify-content:space-between;align-items:center;"><strong style="font-size:14px;">分享书摘</strong><button onclick="setModal(null)">${ICONS.xmark}</button></div>
          <div style="height:310px;display:flex;align-items:center;justify-content:center;">
            <div style="width:248px;height:298px;border-radius:6px;background:${lookStyles.bg};color:${lookStyles.ink};padding:26px 22px 18px;box-shadow:0 18px 24px rgba(30,28,24,0.22);display:flex;flex-direction:column;position:relative;border:0.5px solid rgba(0,0,0,0.08);">
              <div class="georgia" style="font-size:32px;line-height:20px;opacity:0.7;">“</div>
              <div class="serif" style="margin-top:8px;font-size:15px;font-weight:600;line-height:1.72;">莫失莫忘，仙寿恒昌。</div>
              <div class="kai" style="margin-top:12px;padding-left:8px;border-left:1.5px solid ${lookStyles.sub};font-size:11.5px;color:${lookStyles.sub};">金玉初逢，伏脉千里。</div>
              <div style="margin-top:auto;display:flex;align-items:center;justify-content:space-between;font-size:10px;color:${lookStyles.sub};">
                <span>《红楼梦》· 曹雪芹</span><span class="serif" style="letter-spacing:1.2px;">语阅</span>
              </div>
            </div>
          </div>
          <div style="display:flex;justify-content:center;gap:16px;">
            ${[['kami','纸本','#F5F4ED'],['plain','素白','#FFFFFF'],['cover','封面色','#2F3A33'],['dark','暗黑','#161719']].map(l => `
              <button onclick="setQuoteLook('${l[0]}')" style="display:flex;flex-direction:column;align-items:center;gap:4px;">
                <span style="width:18px;height:18px;border-radius:50%;background:${l[2]};border:${look === l[0] ? '2px solid var(--yy-t1)' : '0.5px solid rgba(0,0,0,0.2)'};"></span>
                <span style="font-size:10.5px;color:var(--yy-t2);">${l[1]}</span>
              </button>
            `).join('')}
          </div>
          <div class="yy-seg"><button class="yy-seg-btn active">竖图</button><button class="yy-seg-btn">方图</button><button class="yy-seg-btn">横图</button></div>
          <div style="display:flex;gap:8px;">
            <button class="yy-pill lg" style="flex:1;" onclick="showProtoToast('已复制纯文字书摘')">纯文字</button>
            <button class="yy-pill lg" style="flex:1;" onclick="showProtoToast('已导出高清 PNG 书摘图')">存图</button>
            <button class="yy-pill primary lg" style="flex:1;" onclick="setModal(null);showProtoToast('已复制图片至剪贴板')">复制图片</button>
          </div>
        </div>
      </div>`;
  }

  if (S.modal === 'replaceRules') {
    return `
      <div class="yy-modal-backdrop" onclick="setModal(null)">
        <div class="yy-sheet" onclick="event.stopPropagation()" style="width:460px;padding:18px;display:flex;flex-direction:column;gap:12px;">
          <div style="display:flex;justify-content:space-between;align-items:center;"><strong style="font-size:15px;">净化替换规则</strong><button onclick="setModal(null)">${ICONS.xmark}</button></div>
          <div class="yy-seg sm"><button class="yy-seg-btn active">只管《红楼梦》(2)</button><button class="yy-seg-btn">所有书通用 (1)</button></div>
          <div class="yy-card" style="padding:4px 12px;">
            <div style="height:42px;display:flex;align-items:center;justify-content:space-between;"><div><div style="font-size:12.5px;font-weight:500;">去除校对注记 【.*?】</div><div style="font-size:10.5px;color:var(--yy-t3);">正则 · 本章命中 6 处</div></div><div class="yy-switch on"></div></div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="height:42px;display:flex;align-items:center;justify-content:space-between;"><div><div style="font-size:12.5px;font-weight:500;">统一异体字「裏 → 里」</div><div style="font-size:10.5px;color:var(--yy-t3);">纯文本 · 本章命中 14 处</div></div><div class="yy-switch on"></div></div>
          </div>
          <div style="padding:10px 12px;border-radius:10px;background:var(--yy-inset);font-size:11.5px;color:var(--yy-t2);">实时预览（250ms 防抖）：已自动折叠校勘尾注与多余空行。</div>
          <div style="display:flex;justify-content:flex-end;gap:8px;"><button class="yy-pill primary" onclick="setModal(null)">完成</button></div>
        </div>
      </div>`;
  }
  return '';
}

// ============================================================================
// Window 3: PDF Reader Window (MacPdfView.swift — Symmetric 16px Toolbar!)
// ============================================================================
function renderPdfWindow(S) {
  const night = !!S.pdfNight;
  const layout = S.pdfLayout || 'two';
  const winH = S.chrome === 'mac' ? 720 : 748;
  return `
    <div class="win-frame" style="width:1080px;height:${winH}px;background:${night ? '#212121' : '#F4F3EF'};color:${night ? '#EDEDEE' : '#2A2B2E'};--paper-bg:${night ? '#212121' : '#F4F3EF'};--paper-ink2:${night ? '#9A9895' : '#6E6B68'};">
      ${renderWinTitleBar('reader')}
      <div class="mac-lights" style="position:absolute;top:19px;left:20px;">
        <span class="mac-dot close"></span><span class="mac-dot min"></span><span class="mac-dot max"></span>
      </div>

      <div style="width:100%;flex:1;min-height:0;display:flex;position:relative;">
        <div style="flex:1;position:relative;display:flex;flex-direction:column;">
          <div class="reader-toolbar" style="color:${night ? '#EDEDEE' : '#2A2B2E'};">
            <button class="glass-capsule" onclick="openWin('shelf')" style="height:30px;padding:0 12px 0 8px;display:inline-flex;align-items:center;gap:3px;font-size:12px;background:${night ? 'rgba(40,40,44,0.86)' : 'rgba(255,255,255,0.88)'};border-color:${night ? '#38383C' : '#E7E5E1'};">
              ${ICONS.chevLeft}<span>书架</span>
            </button>
            <div style="text-align:center;">
              <div style="font-size:13px;font-weight:600;">On the Measure of Intelligence</div>
              <div class="tabular" style="font-size:10.5px;opacity:0.68;">François Chollet · §3.2 · 第 18 / 64 页</div>
            </div>
            <div class="glass-capsule" style="height:32px;padding:0 3px;display:inline-flex;align-items:center;gap:2px;background:${night ? 'rgba(40,40,44,0.86)' : 'rgba(255,255,255,0.88)'};border-color:${night ? '#38383C' : '#E7E5E1'};">
              <button class="glass-tool ${S.pdfSide === 'toc' ? 'on' : ''}" onclick="togglePdfSide('toc')" style="color:inherit;">${ICONS.list}</button>
              <button class="glass-tool ${S.pdfSide === 'notes' ? 'on' : ''}" onclick="togglePdfSide('notes')" style="color:inherit;">${ICONS.noteCard}</button>
              <button class="glass-tool ${S.pdfSide === 'search' ? 'on' : ''}" onclick="togglePdfSide('search')" style="color:inherit;">${ICONS.search}</button>
              <button class="glass-tool ${S.pdfShowCard ? 'on' : ''}" onclick="togglePdfShowCard()" style="color:inherit;" title="显示与夜间反色">${ICONS.docZoom}</button>
            </div>
          </div>

          <div style="flex:1;padding:58px 36px 76px;display:flex;align-items:center;justify-content:center;gap:18px;background:${night ? '#212121' : '#F4F3EF'};">
            ${(layout === 'two' ? [18, 19] : [18]).map(pg => `
              <div class="georgia" style="width:${layout === 'two' ? '340px' : '460px'};height:550px;background:${night ? '#1F1F21' : '#FFFFFF'};color:${night ? '#DCD6CC' : '#222'};padding:34px 32px;border-radius:4px;box-shadow:0 6px 24px rgba(0,0,0,${night ? '0.45' : '0.08'});display:flex;flex-direction:column;font-size:12.5px;line-height:1.72;">
                <div style="display:flex;justify-content:space-between;font-size:9.5px;opacity:0.55;margin-bottom:18px;"><span>arXiv:1911.01547v2 [cs.AI]</span><span>Page ${pg}</span></div>
                <div style="font-size:15px;font-weight:700;margin-bottom:10px;">${pg === 18 ? '3.2 Defining Intelligence: Skill-Acquisition Efficiency' : '3.3 Priors, Experience, and Generalization Difficulty'}</div>
                <p style="margin:0 0 10px;text-align:justify;">The intelligence of a system is a measure of its skill-acquisition efficiency over a scope of tasks, with respect to priors, experience, and generalization difficulty.</p>
                <p style="margin:0 0 10px;text-align:justify;"><span style="background:${night ? 'rgba(251,231,160,0.24)' : '#FBE7A0'};padding:1px 2px;">If you can buy high performance on a benchmark by injecting unlimited prior knowledge or training data, the benchmark is measuring crystallized skill rather than fluid intelligence.</span></p>
                <div style="margin:12px 0;padding:12px;border:0.5px solid ${night ? '#3A3A3E' : '#E2E0DA'};border-radius:4px;text-align:center;font-style:italic;">I(S) = Avg_T [ θ(T) / (P(T) + E(T)) ]</div>
                <p style="margin:0;text-align:justify;">Therefore, an authentic test of general intelligence (ARC) must control strictly for pre-existing task-specific priors.</p>
              </div>
            `).join('')}
          </div>

          ${S.pdfShowCard ? `
            <div class="yy-float-card" style="position:absolute;top:48px;right:16px;width:276px;padding:16px;color:var(--yy-t1);z-index:55;display:flex;flex-direction:column;gap:12px;">
              <strong style="font-size:13px;">显示</strong>
              <div style="display:flex;gap:8px;">
                ${[['scroll','连续滚动'],['one','单页'],['two','双页']].map(m => `
                  <button onclick="setPdfLayout('${m[0]}')" style="flex:1;height:56px;border-radius:9px;background:${layout === m[0] ? 'var(--yy-inset)' : 'transparent'};border:${layout === m[0] ? '1.2px solid var(--yy-t1)' : '0.5px solid var(--yy-line)'};font-size:11px;font-weight:${layout === m[0] ? '600' : '400'};">${m[1]}</button>
                `).join('')}
              </div>
              <div style="padding:10px 12px;border-radius:10px;background:var(--yy-inset);display:flex;align-items:center;justify-content:space-between;">
                <div><div style="font-size:12.5px;font-weight:500;">夜间</div><div style="font-size:10.5px;color:var(--yy-t3);">白纸变暖黑，字变浅</div></div>
                <div class="yy-switch ${night ? 'on' : ''}" onclick="togglePdfNight()"></div>
              </div>
            </div>
          ` : ''}

          <div class="glass-capsule" style="position:absolute;bottom:18px;left:50%;transform:translateX(-50%);width:300px;height:46px;padding:5px 6px;display:flex;align-items:center;gap:4px;background:${night ? 'rgba(38,38,42,0.88)' : 'rgba(255,255,255,0.88)'};border-color:${night ? '#3A3A3E' : '#E7E5E1'};">
            <div style="flex:1;padding:0 10px;">
              <div class="tabular" style="font-size:12px;font-weight:600;">第 18 / 64 页</div>
              <div style="font-size:10px;opacity:0.68;">点播放从这一页读起</div>
            </div>
            <button style="width:30px;height:36px;display:flex;align-items:center;justify-content:center;">${ICONS.chevLeft}</button>
            <button onclick="showProtoToast('正在按页提取 PDF 文本朗读')" style="width:36px;height:36px;border-radius:50%;background:var(--tint-accent);color:#FFF;display:flex;align-items:center;justify-content:center;">${ICONS.play}</button>
            <button style="width:30px;height:36px;display:flex;align-items:center;justify-content:center;">${ICONS.chevRight}</button>
          </div>
        </div>

        ${S.pdfSide ? `
          <aside class="yy-float-card" style="width:300px;margin:50px 14px 14px 0;border-radius:16px;padding:14px 16px;color:var(--yy-t1);display:flex;flex-direction:column;gap:10px;">
            <div style="display:flex;justify-content:space-between;align-items:baseline;"><strong>${S.pdfSide === 'toc' ? '目录' : (S.pdfSide === 'notes' ? '笔记' : 'PDF 搜索')}</strong><span style="font-size:11px;color:var(--yy-t3);">64 页</span></div>
            ${['1. Context & History', '2. A New Perspective', '3.1 Defining Intelligence', '3.2 Skill-Acquisition Efficiency', '4. The ARC Benchmark'].map((t, i) => `
              <div style="padding:8px 10px;border-radius:8px;background:${i === 3 ? 'var(--yy-inset)' : 'transparent'};font-size:12.5px;font-weight:${i === 3 ? '600' : '400'};">${t}</div>
            `).join('')}
          </aside>
        ` : ''}
      </div>
    </div>
  `;
}

// ============================================================================
// Window 4: AudiobookScreen + RecordingSettings + TextVersionCard + AudiobookPage + AlignCheck
// ============================================================================
function renderAudiobookWindow(S) {
  if (S.audioMode === 'align') return renderAlignCheckWindow(S);
  const hasMap = S.audioHasMap !== false;
  const winH = S.chrome === 'mac' ? 720 : 748;

  return `
    <div class="win-frame" style="width:1080px;height:${winH}px;background:var(--yy-bg);">
      ${renderWinTitleBar('plain')}
      <div class="mac-lights" style="position:absolute;top:19px;left:20px;">
        <span class="mac-dot close"></span><span class="mac-dot min"></span><span class="mac-dot max"></span>
      </div>

      <div style="flex:1;min-height:0;display:flex;position:relative;">
        <!-- Top Left Shelf Back Capsule -->
        <div style="position:absolute;top:10px;left:${S.chrome === 'mac' ? '84px' : '16px'};z-index:40;">
          <button class="yy-pill" onclick="openWin('shelf')" style="height:30px;padding:0 12px 0 8px;">${ICONS.chevLeft}<span>书架</span></button>
        </div>

        <!-- Left Centered 420px Player Column (AudiobookScreen.swift L28-43 & L470-495) -->
        <div style="flex:1;display:flex;flex-direction:column;align-items:center;justify-content:center;position:relative;">
          <div style="width:420px;display:flex;flex-direction:column;align-items:center;">
            <!-- 240x240 SquareCover (radius: 10, 0.5pt black.opacity(0.12), shadow r17 y14) -->
            <div style="width:240px;height:240px;border-radius:10px;background:linear-gradient(155deg,#4B5849 0%,#2C352B 100%);color:#EFF2EC;box-shadow:0 14px 32px rgba(0,0,0,0.18);border:0.5px solid rgba(0,0,0,0.12);display:flex;flex-direction:column;align-items:center;justify-content:center;gap:10px;margin-bottom:20px;">
              <div style="transform:scale(1.8);opacity:0.85;">${ICONS.headphones}</div>
              <div class="serif" style="font-size:20px;font-weight:600;">背影 · 名家朗诵</div>
              <div style="font-size:11.5px;opacity:0.75;">朱自清 著</div>
            </div>

            <div style="font-size:19px;font-weight:700;color:var(--yy-t1);">第 1 集 · 背影</div>
            <div style="font-size:11.5px;color:var(--yy-t2);margin-top:3px;">背影 · 名家朗诵集 · 朱自清</div>

            <div style="width:100%;height:36px;display:flex;align-items:center;position:relative;margin-top:8px;">
              <div style="width:100%;height:4px;border-radius:99px;background:var(--yy-inset);position:relative;">
                <div style="width:42%;height:100%;border-radius:99px;background:var(--yy-t1);"></div>
                <span style="position:absolute;left:24%;top:-1px;width:2px;height:6px;background:var(--yy-card);border-radius:1px;" title="书签 01:58"></span>
                <span style="position:absolute;left:42%;top:50%;transform:translate(-50%,-50%);width:12px;height:12px;border-radius:50%;background:var(--yy-t1);box-shadow:0 0 0 3px var(--yy-bg);"></span>
              </div>
            </div>

            <div class="tabular" style="width:100%;display:flex;justify-content:space-between;font-size:10.5px;color:var(--yy-t3);margin-top:-4px;">
              <span style="color:var(--yy-t2);font-weight:500;">03:18</span>
              <span>全书还剩 14 分钟</span>
              <span style="color:var(--yy-t2);font-weight:500;">07:52</span>
            </div>

            <div style="font-size:11px;font-weight:600;color:var(--tint-accent);margin-top:8px;">已跳过 12 秒片头音乐 · 点这里重听</div>

            <div style="width:100%;padding:16px 36px;display:flex;align-items:center;justify-content:space-between;">
              <button class="tabular" style="width:40px;height:40px;font-size:12.5px;font-weight:600;">1.0×</button>
              <button style="width:40px;height:40px;display:flex;align-items:center;justify-content:center;">${ICONS.back15}</button>
              <button onclick="showProtoToast('播放 / 暂停有声书')" style="width:60px;height:60px;border-radius:50%;background:var(--yy-t1);color:var(--yy-bg);display:flex;align-items:center;justify-content:center;">${ICONS.play}</button>
              <button style="width:40px;height:40px;display:flex;align-items:center;justify-content:center;">${ICONS.fwd30}</button>
              <button style="width:40px;height:40px;display:flex;align-items:center;justify-content:center;" title="睡眠定时">${ICONS.moon}</button>
            </div>
          </div>

          <!-- Exact Bottom keys Bar (AudiobookScreen.swift L470-495) -->
          <div style="position:absolute;bottom:18px;display:flex;align-items:center;gap:16px;font-size:10.5px;color:var(--yy-t3);">
            <span style="display:inline-flex;align-items:center;gap:3px;"><span class="yy-key">空格</span> 暂停</span>
            ${hasMap ? `
              <span style="display:inline-flex;align-items:center;gap:3px;"><span class="yy-key">←</span><span class="yy-key">→</span> 上一句 / 下一句</span>
              <span style="display:inline-flex;align-items:center;gap:3px;"><span class="yy-key">⇧←</span><span class="yy-key">⇧→</span> 15 秒</span>
            ` : `
              <span style="display:inline-flex;align-items:center;gap:3px;"><span class="yy-key">←</span><span class="yy-key">→</span> 后退 15 秒 / 前进 30 秒</span>
            `}
            <span style="display:inline-flex;align-items:center;gap:3px;"><span class="yy-key">${S.chrome === 'mac' ? '⌘D' : 'Ctrl+D'}</span> 加书签</span>
          </div>
        </div>

        <!-- Right 300px Column (AudiobookScreen.swift L38-42: ChapterColumn + RecordingSettings / TextVersionCard + pairCard) -->
        <div style="width:300px;padding:12px 12px 12px 0;display:flex;flex-direction:column;gap:10px;flex-shrink:0;overflow-y:auto;">
          <!-- 1. ChapterColumn (Plain clean rows with 0.5pt hairline + inline 书签 section in same card! L895-971) -->
          <div class="yy-card" style="border-radius:14px;padding:6px 12px;display:flex;flex-direction:column;">
            ${[
              ['第 1 集 · 背影', '还剩 04:34', true],
              ['第 2 集 · 荷塘月色', '09:15', false],
              ['第 3 集 · 匆匆', '04:42', false],
              ['第 4 集 · 春', '05:18', false],
              ['第 5 集 · 桨声灯影里的秦淮河', '14:10', false]
            ].map((c, i) => `
              ${i > 0 ? `<div style="height:0.5px;background:var(--yy-line);"></div>` : ''}
              <div style="padding:7px 0;display:flex;justify-content:space-between;align-items:center;font-size:12px;cursor:pointer;">
                <span class="truncate" style="font-weight:${c[2] ? '600' : '400'};color:var(--yy-t1);">${c[0]}</span>
                <span class="tabular" style="font-size:10.5px;color:var(--yy-t3);flex-shrink:0;margin-left:8px;">${c[1]}</span>
              </div>
            `).join('')}

            <!-- Inline Bookmarks Section in SAME card (L956) -->
            <div style="font-size:10.5px;font-weight:600;color:var(--yy-t3);padding:10px 0 2px;">书签</div>
            <div style="height:0.5px;background:var(--yy-line);"></div>
            <div style="padding:7px 0;display:flex;justify-content:space-between;align-items:center;font-size:12px;">
              <span class="truncate">第 1 集 · 背影</span>
              <span class="tabular" style="font-size:10.5px;color:var(--yy-t3);">01:58</span>
            </div>
          </div>

          ${hasMap ? `
            <!-- 2A. Exact RecordingSettings (这本录音 — AudiobookScreen.swift L790-890: All 7 rows!) -->
            <div class="yy-card" style="border-radius:14px;padding:0 12px 4px;display:flex;flex-direction:column;">
              <div style="font-size:12px;font-weight:700;padding:10px 0 4px;">这本录音</div>

              <div style="height:0.5px;background:var(--yy-line);"></div>
              <div style="padding:7px 0;display:flex;justify-content:space-between;align-items:center;">
                <span style="font-size:12px;">片头片尾</span>
                <div class="yy-seg sm" style="width:116px;"><button class="yy-seg-btn active">跳过</button><button class="yy-seg-btn">照放</button></div>
              </div>

              ${[
                ['章间音乐', '压到 1.5 秒', true],
                ['人声清楚一点', '压低底噪，人声提一点', true],
                ['缩短停顿', '这本已经省了 3 分钟', true],
                ['没念的用朗读补上', '这本有 2 句没念', true],
                ['逐词跟读', '念到哪个词，哪个词下面一道线', false]
              ].map(r => `
                <div style="height:0.5px;background:var(--yy-line);"></div>
                <div style="padding:6.5px 0;display:flex;justify-content:space-between;align-items:center;gap:8px;">
                  <div style="min-width:0;"><div style="font-size:12px;">${r[0]}</div><div class="tabular truncate" style="font-size:10px;color:var(--yy-t3);">${r[1]}</div></div>
                  <div class="yy-switch ${r[2] ? 'on' : ''}" onclick="this.classList.toggle('on')"></div>
                </div>
              `).join('')}

              <div style="height:0.5px;background:var(--yy-line);"></div>
              <div style="padding:7px 0;display:flex;justify-content:space-between;align-items:center;gap:8px;">
                <div style="min-width:0;"><div style="font-size:12px;">导出带朗读时间的电子书</div><div class="truncate" style="font-size:10px;color:var(--yy-t3);">一个 EPUB，别的阅读器也能跟读</div></div>
                <button class="yy-pill xs" onclick="showProtoToast('正在打包 Media Overlays EPUB…')">导出…</button>
              </div>
            </div>

            <!-- 3A. Paired Action Card -->
            <div class="yy-card" style="border-radius:14px;padding:10px 12px;display:flex;align-items:center;gap:10px;">
              ${renderBookCover(BOOKS[4], 24)}
              <div style="flex:1;min-width:0;">
                <div class="truncate" style="font-size:11px;font-weight:600;">已配对《背影》电子书</div>
                <div class="truncate" style="font-size:10px;color:var(--yy-t3);">5 章对得很好 · 1 处可微调</div>
              </div>
              <button class="yy-pill xs" onclick="setAudioMakeMode('result')">报告</button>
              <button class="yy-pill dark-solid xs" onclick="setAudioMode('align')">微调</button>
            </div>
          ` : `
            <!-- 2B. TextVersionCard when unpaired (!player.hasMap — AudiobookScreen.swift L723-787) -->
            <div class="yy-card" style="border-radius:14px;padding:0 12px 4px;display:flex;flex-direction:column;">
              <div style="font-size:12px;font-weight:700;padding:10px 0 4px;">这本录音</div>
              <div style="height:0.5px;background:var(--yy-line);"></div>
              <div style="padding:8px 0;display:flex;justify-content:space-between;align-items:center;gap:10px;">
                <div><div style="font-size:12px;">配上书架上的电子书</div><div style="font-size:10px;color:var(--yy-t3);margin-top:1px;">合成以后听到哪句，书页就走到哪句</div></div>
                <button class="yy-pill sm" onclick="setAudioMakeMode('making')">选书…</button>
              </div>
              <div style="height:0.5px;background:var(--yy-line);"></div>
              <div style="padding:8px 0;display:flex;justify-content:space-between;align-items:center;gap:10px;">
                <div><div style="font-size:12px;">没有电子书</div><div style="font-size:10px;color:var(--yy-t3);margin-top:1px;">把录音转成文字，能翻能搜能划线</div></div>
                <button class="yy-pill sm" onclick="showProtoToast('正在本机离线转写录音为可检索章节…')">转成文字</button>
              </div>
            </div>
          `}
        </div>
      </div>

      ${S.audioMakeMode ? renderAudiobookMakeSheet(S) : ''}
    </div>
  `;
}

// AudiobookPage Sheet: 420x640 — Both `making` (合成中) & `result` (合成好了) (AudiobookViews.swift L414-835)
function renderAudiobookMakeSheet(S) {
  const mode = S.audioMakeMode; // 'making' | 'result'
  return `
    <div class="yy-modal-backdrop" onclick="setAudioMakeMode(null)">
      <div class="yy-sheet" onclick="event.stopPropagation()" style="width:420px;height:620px;display:flex;flex-direction:column;position:relative;">
        <!-- Top nav bar -->
        <div style="padding:14px 14px 6px;display:flex;align-items:center;justify-content:space-between;">
          <button onclick="setAudioMakeMode(null)" style="width:28px;height:28px;border-radius:50%;background:var(--yy-card);border:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:center;">
            ${mode === 'making' ? ICONS.chevLeft : ICONS.xmark}
          </button>
          <div style="text-align:center;">
            ${mode === 'result' ? `<div style="font-size:13px;font-weight:600;">合成好了</div><div style="font-size:10px;color:var(--yy-t3);">背影 · 用了 2 分钟</div>` : ''}
          </div>
          <button onclick="setAudioMakeMode(mode === 'making' ? 'result' : 'making')" title="切换合成中 / 合成好了" style="width:28px;height:28px;border-radius:50%;background:var(--yy-card);border:0.5px solid var(--yy-line);display:flex;align-items:center;justify-content:center;">${ICONS.dots}</button>
        </div>

        ${mode === 'making' ? `
          <!-- State 1: 合成中 (L510-704) -->
          <div style="flex:1;overflow-y:auto;padding:12px 18px 86px;">
            <div style="padding:14px 10px 18px;">
              <div style="display:flex;align-items:baseline;gap:2px;">
                <span class="tabular" style="font-size:58px;font-weight:700;letter-spacing:-1px;line-height:1;">68</span>
                <span style="font-size:20px;font-weight:600;color:var(--yy-t2);">%</span>
              </div>
              <div style="font-size:11.5px;color:var(--yy-t2);margin-top:9px;">还要不到 1 分钟 · 在这台电脑上处理</div>
              <div style="margin-top:16px;height:3px;border-radius:99px;background:var(--yy-inset);overflow:hidden;">
                <div style="width:68%;height:100%;border-radius:99px;background:var(--tint-fill);"></div>
              </div>
            </div>

            <!-- 4-Step YYGroup -->
            <div class="yy-card" style="border-radius:14px;padding:2px 14px;">
              ${[
                { s: 'done', n: '读取章节', v: '14 章 · 42 分钟' },
                { s: 'done', n: '找出每集念的是哪一段', v: '用了不到 1 分钟' },
                { s: 'now', n: '逐句对齐', v: '第 10 / 14 章' },
                { s: 'todo', n: '检查', v: '最后一步' }
              ].map((st, i) => `
                ${i > 0 ? `<div style="height:0.5px;background:var(--yy-line);"></div>` : ''}
                <div style="height:38px;display:flex;align-items:center;gap:10px;">
                  ${st.s === 'done'
                    ? `<span style="width:18px;height:18px;border-radius:50%;background:var(--tint-fill);color:#FFF;display:inline-flex;align-items:center;justify-content:center;">${ICONS.check.replace('width="12" height="12"', 'width="9" height="9"')}</span>`
                    : st.s === 'now'
                    ? `<span style="width:18px;height:18px;border-radius:50%;border:2px solid var(--tint-soft);border-top-color:var(--tint-accent);animation:yySpin 0.9s linear infinite;"></span>`
                    : `<span style="width:18px;height:18px;border-radius:50%;border:1.2px solid var(--yy-tint-neutral);"></span>`
                  }
                  <span style="flex:1;font-size:12px;font-weight:${st.s === 'now' ? '600' : '400'};color:${st.s === 'todo' ? 'var(--yy-t3)' : 'var(--yy-t1)'};">${st.n}</span>
                  <span class="tabular" style="font-size:11px;color:${st.s === 'now' ? 'var(--tint-accent)' : 'var(--yy-t3)'};">${st.v}</span>
                </div>
              `).join('')}
            </div>

            <div style="display:flex;justify-content:space-between;padding:16px 2px 8px;font-size:12px;">
              <strong>14 章</strong><span style="font-size:11px;color:var(--yy-t3);">9 章已经能听</span>
            </div>

            <!-- 12-Column Chapter Matrix Card -->
            <div class="yy-card" style="border-radius:14px;padding:13px 12px 12px;">
              <div style="display:grid;grid-template-columns:repeat(12,1fr);gap:5px 4px;">
                ${Array.from({ length: 14 }).map((_, i) => {
                  const st = i === 3 ? 'check' : (i < 9 ? 'done' : (i === 9 ? 'now' : 'todo'));
                  const bg = st === 'done' ? 'var(--tint-fill)' : (st === 'check' ? 'var(--yy-warn)' : (st === 'now' ? 'var(--tint-soft)' : 'var(--yy-inset)'));
                  const fg = (st === 'done' || st === 'check') ? '#FFF' : (st === 'now' ? 'var(--tint-accent)' : 'var(--yy-t3)');
                  const bd = st === 'now' ? '1.5px solid var(--tint-accent)' : 'none';
                  return `<span class="tabular" style="height:22px;border-radius:5px;background:${bg};color:${fg};border:${bd};font-size:9.5px;font-weight:${st==='now'||st==='check'?'600':'400'};display:flex;align-items:center;justify-content:center;">${i + 1}</span>`;
                }).join('')}
              </div>
              <div style="display:flex;gap:12px;margin-top:10px;font-size:10.5px;color:var(--yy-t2);">
                <span style="display:inline-flex;align-items:center;gap:4px;"><i style="width:8px;height:8px;border-radius:2px;background:var(--tint-fill);"></i>对好了</span>
                <span style="display:inline-flex;align-items:center;gap:4px;"><i style="width:8px;height:8px;border-radius:2px;background:var(--yy-warn);"></i>要看一下</span>
                <span style="display:inline-flex;align-items:center;gap:4px;"><i style="width:8px;height:8px;border-radius:2px;background:var(--tint-soft);border:1px solid var(--tint-accent);"></i>正在对</span>
                <span style="display:inline-flex;align-items:center;gap:4px;"><i style="width:8px;height:8px;border-radius:2px;background:var(--yy-inset);"></i>还没到</span>
              </div>
            </div>
            <div style="font-size:11px;color:var(--yy-t3);margin-top:10px;padding:0 2px;">可以离开这一页，也可以去做别的，好了会通知你。</div>
          </div>

          <div style="position:absolute;left:0;right:0;bottom:0;padding:24px 16px 20px;background:linear-gradient(180deg,transparent 0%,var(--yy-bg) 48%);">
            <button class="yy-pill primary lg" style="width:100%;" onclick="setAudioMakeMode('result')">${ICONS.headphones}<span>先听对好的 9 章（点此预览完成页）</span></button>
          </div>
        ` : `
          <!-- State 2: 合成好了 (L708-835) -->
          <div style="flex:1;overflow-y:auto;padding:8px 16px 86px;">
            <div style="display:flex;flex-direction:column;align-items:center;padding:8px 0 16px;">
              <div style="position:relative;">
                ${renderBookCover(BOOKS[4], 58)}
                <span style="position:absolute;right:-8px;bottom:-6px;width:24px;height:24px;border-radius:50%;background:var(--tint-black);border:2.5px solid var(--yy-bg);color:var(--tint-on-black);display:flex;align-items:center;justify-content:center;">${ICONS.headphonesTiny}</span>
              </div>
              <div style="font-size:17px;font-weight:700;text-align:center;line-height:1.4;margin-top:16px;">13 章对得很好<br/>3 处要你看一下</div>
            </div>

            <div style="display:flex;flex-direction:column;gap:8px;">
              <!-- Issue 1: .intro -->
              <div class="yy-card" style="border-radius:14px;padding:11px 12px 10px;">
                <div style="display:flex;align-items:center;gap:7px;">
                  <span style="width:6px;height:6px;border-radius:50%;background:var(--yy-warn);"></span>
                  <strong style="font-size:12px;">片头多出 12 秒</strong>
                  <span style="margin-left:auto;font-size:10px;color:var(--yy-t3);">第 1 章开头</span>
                </div>
                <div style="font-size:11px;color:var(--yy-t2);padding-left:13px;margin-top:3px;line-height:1.5;">录音开头是出品方的介绍，书里没有。听的时候从正文开始。</div>
                <div style="padding-left:13px;margin-top:8px;display:flex;gap:6px;">
                  <button class="yy-pill xs">${ICONS.play.replace('width="12" height="12"', 'width="9" height="9"')}<span>试听</span></button>
                  <button class="yy-pill primary xs" onclick="showProtoToast('已设为自动跳过片头 12 秒')">跳过这段</button>
                </div>
              </div>

              <!-- Issue 2: .missing -->
              <div class="yy-card" style="border-radius:14px;padding:11px 12px 10px;">
                <div style="display:flex;align-items:center;gap:7px;">
                  <span style="width:6px;height:6px;border-radius:50%;background:var(--yy-ok);"></span>
                  <strong style="font-size:12px;">电子书这段，录音里没有</strong>
                  <span style="margin-left:auto;font-size:10px;color:var(--yy-t3);">第 2 章 · 2 句</span>
                </div>
                <div style="font-size:11px;color:var(--yy-t2);padding-left:13px;margin-top:3px;line-height:1.5;">朗读的人跳过了这几句，这里改用朗读声音补上。</div>
                <div style="padding-left:13px;margin-top:8px;display:flex;justify-content:flex-end;">
                  <span style="height:24px;padding:0 9px;border-radius:99px;background:var(--yy-inset);color:var(--yy-ok);font-size:10.5px;font-weight:600;display:inline-flex;align-items:center;gap:4px;">${ICONS.check}<span>已用朗读补上</span></span>
                </div>
              </div>

              <!-- Issue 3: .unsure -->
              <div class="yy-card" style="border-radius:14px;padding:11px 12px 10px;">
                <div style="display:flex;align-items:center;gap:7px;">
                  <span style="width:6px;height:6px;border-radius:50%;background:var(--yy-warn);"></span>
                  <strong style="font-size:12px;">这一段没听清，可能对得不准</strong>
                  <span style="margin-left:auto;font-size:10px;color:var(--yy-t3);">第 4 章 · 第 12 句起</span>
                </div>
                <div style="font-size:11px;color:var(--yy-t2);padding-left:13px;margin-top:3px;line-height:1.5;">这 3 句的墨线可能和声音对不上，听一下，不准就微调。</div>
                <div style="padding-left:13px;margin-top:8px;display:flex;gap:6px;">
                  <button class="yy-pill xs">${ICONS.play.replace('width="12" height="12"', 'width="9" height="9"')}<span>试听</span></button>
                  <button class="yy-pill primary xs" onclick="setAudioMakeMode(null);setAudioMode('align')">微调</button>
                </div>
              </div>
            </div>
          </div>

          <div style="position:absolute;left:0;right:0;bottom:0;padding:24px 16px 20px;background:linear-gradient(180deg,transparent 0%,var(--yy-bg) 48%);">
            <button class="yy-pill primary lg" style="width:100%;" onclick="setAudioMakeMode(null);openWin('reader');toggleReaderPlay()">${ICONS.headphones}<span>开始听</span></button>
          </div>
        `}
      </div>
    </div>
  `;
}

// AlignCheckWindow ("对齐检查" — AlignCheck.swift)
function renderAlignCheckWindow(S) {
  const winH = S.chrome === 'mac' ? 720 : 748;
  return `
    <div class="win-frame" style="width:1080px;height:${winH}px;background:#FFFFFF;color:#2A2B2E;--paper-bg:#FFFFFF;--paper-ink2:#777571;">
      ${renderWinTitleBar('reader')}
      <div style="height:48px;padding:0 16px;border-bottom:0.5px solid #EFEEEB;display:flex;align-items:center;justify-content:space-between;">
        <button class="yy-pill" onclick="setAudioMode('player')" style="height:28px;">${ICONS.chevLeft}<span>返回播放器</span></button>
        <div style="font-size:13px;font-weight:600;">对齐检查 · 《背影》</div>
        <div style="display:flex;align-items:center;gap:8px;">
          <span class="tabular" style="font-size:11.5px;color:#777571;">第 1 / 3 处存疑</span>
          <button class="yy-pill primary sm" onclick="setAudioMode('player');showProtoToast('已保存句级对齐锚点')">完成</button>
        </div>
      </div>

      <div style="flex:1;min-height:0;display:flex;">
        <div style="flex:1;position:relative;display:flex;flex-direction:column;align-items:center;overflow-y:auto;padding:32px 24px 160px;">
          <div style="width:460px;">
            <div style="font-size:20px;font-weight:700;margin-bottom:14px;">第 1 章 · 背影</div>
            <div style="font-size:15px;line-height:2.05;">
              <span>我与父亲不相见已二年余了，</span>
              <span style="background:rgba(42,43,46,0.06);padding:2px 0;">我最不能忘记的是他的背影。</span>
              <span>那年冬天，祖母死了，父亲的差使也交卸了，正是祸不单行的日子。</span>

              <div style="position:relative;margin:14px -16px;padding:14px 16px 10px;border:1.5px solid #B7791F;background:rgba(183,121,31,0.04);border-radius:12px;">
                <span style="position:absolute;top:-11px;left:12px;height:21px;padding:0 8px;border-radius:99px;background:#FFF;border:1.5px solid #B7791F;color:#B7791F;font-size:10.5px;font-weight:600;display:inline-flex;align-items:center;">⚠ 这一段对得不太准</span>
                <span style="background:rgba(43,76,126,0.13);color:#2B4C7E;font-weight:600;padding:2px 3px;border-radius:4px;">我看见他戴着黑布小帽，穿着黑布大马褂，深青布棉袍，</span>
                <span>蹒跚地走到铁道边，慢慢探身下去，尚不大难。</span>
              </div>

              <span>可是他穿过铁道，要爬上那边月台，就不容易了。</span>
            </div>
          </div>

          <div style="position:absolute;left:18px;right:18px;bottom:14px;padding:10px 16px 12px;border-radius:18px;background:rgba(255,255,255,0.88);backdrop-filter:blur(16px);border:0.5px solid #E7E5E1;box-shadow:0 8px 24px rgba(0,0,0,0.10);">
            <div class="tabular" style="display:flex;justify-content:space-between;padding-left:44px;font-size:10px;color:#777571;border-bottom:0.5px solid #EFEEEB;padding-bottom:4px;">
              <span>0:12</span><span>0:14</span><span>0:16</span><span style="color:#2B4C7E;font-weight:700;">0:17.4</span><span>0:18</span><span>0:20</span><span>0:22</span>
            </div>
            <div style="display:flex;align-items:center;margin-top:6px;">
              <span style="width:44px;font-size:10.5px;color:#777571;">句子</span>
              <div style="flex:1;display:flex;gap:4px;">
                <div class="serif truncate" style="flex:1;height:26px;padding:0 8px;border-radius:6px;background:#F4F3EF;font-size:11px;line-height:26px;">正是祸不单行的日子</div>
                <div class="serif truncate" style="flex:1.5;height:26px;padding:0 8px;border-radius:6px;background:rgba(43,76,126,0.13);border:1.2px solid #2B4C7E;color:#2B4C7E;font-weight:600;font-size:11px;line-height:24px;">我看见他戴着黑布小帽…</div>
                <div class="serif truncate" style="flex:1.2;height:26px;padding:0 8px;border-radius:6px;background:#F4F3EF;font-size:11px;line-height:26px;">蹒跚地走到铁道边</div>
              </div>
            </div>
            <div style="display:flex;align-items:center;margin-top:8px;">
              <span style="width:44px;font-size:10.5px;color:#777571;">录音</span>
              <div style="flex:1;height:36px;display:flex;align-items:center;gap:2px;">
                ${Array.from({ length: 84 }).map((_, i) => {
                  const h = 6 + ((i * 17 + 11) % 28);
                  const inCur = i >= 28 && i <= 58;
                  return `<span style="flex:1;height:${h}px;border-radius:2px;background:${inCur ? '#2B4C7E' : '#9E9B95'};opacity:${inCur ? '0.85' : '0.55'};"></span>`;
                }).join('')}
              </div>
            </div>
          </div>
        </div>

        <div class="yy-float-card" style="width:288px;margin:14px 14px 14px 0;padding:14px 16px;border-radius:16px;display:flex;flex-direction:column;gap:14px;">
          <div>
            <div style="display:flex;justify-content:space-between;align-items:baseline;"><strong>第 1 章</strong><span style="font-size:10.5px;color:#8A8682;">6 章 · 1 章要看一下</span></div>
            <div style="display:grid;grid-template-columns:repeat(6,1fr);gap:4px;margin-top:8px;">
              ${[0,1,2,3,4,5].map(i => `<span style="height:9px;border-radius:2.5px;background:${i === 0 ? '#B7791F' : 'rgba(62,155,106,0.35)'};outline:${i === 0 ? '1.5px solid #2A2B2E' : 'none'};outline-offset:1.5px;"></span>`).join('')}
            </div>
          </div>
          <div style="height:0.5px;background:#E7E5E1;"></div>
          <div style="display:flex;flex-direction:column;gap:8px;">
            <div style="display:flex;justify-content:space-between;font-size:12px;"><strong>当前句</strong><span style="color:#8A8682;font-size:10.5px;">第 4 句</span></div>
            <div class="serif" style="padding:7px 10px;border-radius:8px;background:#F2E7DB;font-size:12.5px;">我看见他戴着黑布小帽，穿着黑布大马褂…</div>
            ${[['开始','0:15.7'],['结束','0:20.2']].map(r => `
              <div style="display:flex;align-items:center;justify-content:space-between;font-size:11.5px;">
                <span style="color:#6E6B68;">${r[0]}</span>
                <strong class="tabular">${r[1]}</strong>
                <div style="display:flex;gap:4px;">
                  <button onclick="showProtoToast('${r[0]}时间 -0.1s')" style="width:40px;height:24px;border-radius:7px;background:#EFEEEB;font-size:11px;">−0.1</button>
                  <button onclick="showProtoToast('${r[0]}时间 +0.1s')" style="width:40px;height:24px;border-radius:7px;background:#EFEEEB;font-size:11px;">+0.1</button>
                </div>
              </div>
            `).join('')}
            <button class="yy-pill" style="width:100%;margin-top:4px;" onclick="showProtoToast('试听第 4 句 (0:15.7 - 0:20.2)')">▶ 试听这句 (空格)</button>
          </div>
        </div>
      </div>
    </div>
  `;
}

// ============================================================================
// Window 6: Windows System Tray ("门里出声" — Official Assets.xcassets/menubar.svg!)
// ============================================================================
function renderTrayWindow(S) {
  return `
    <div class="win-frame" style="width:760px;height:460px;background:linear-gradient(145deg,#D5D2CA 0%,#C5C1B8 100%);display:flex;flex-direction:column;justify-content:flex-end;position:relative;">
      <div style="position:absolute;top:24px;left:28px;color:#2A2B2E;">
        <div style="display:flex;align-items:center;gap:8px;">
          <span style="width:30px;height:30px;border-radius:8px;background:#FBFAF7;border:0.5px solid rgba(0,0,0,0.1);display:inline-flex;align-items:center;justify-content:center;color:#2A2B2E;">${officialSvg('menubar', 18, 'current')}</span>
          <strong style="font-size:16px;">系统托盘常驻菜单（1:1 复刻 MacMenuBar「门里出声」官方矢量图标）</strong>
        </div>
        <div style="font-size:12px;opacity:0.76;margin-top:6px;max-width:640px;line-height:1.6;">播放朗读或播客时，使用官方 <code style="font-family:var(--font-mono);">menubar.svg</code>（「门」字内嵌三根圆头声波柱）；静止待命时切换为 <code style="font-family:var(--font-mono);">menubar-door.svg</code>。</div>
      </div>

      <div class="yy-float-card" style="position:absolute;right:48px;bottom:54px;width:270px;padding:6px;border-radius:12px;color:var(--yy-t1);">
        <div style="padding:6px 10px 4px;font-size:10.5px;font-weight:600;color:var(--yy-t3);">广播剧工坊</div>
        <div style="padding:6px 10px;display:flex;align-items:center;justify-content:space-between;gap:8px;font-size:12px;">
          <span class="truncate">正在后台精做《红楼梦》第 9 回</span>
          <button class="yy-pill sm" style="height:22px;padding:0 8px;">暂停</button>
        </div>
        <div style="height:0.5px;background:var(--yy-line);margin:4px 8px;"></div>
        <div style="padding:4px 10px;font-size:10.5px;font-weight:600;color:var(--yy-t3);">正在播放</div>
        <div style="padding:5px 10px;font-size:12.5px;font-weight:500;" class="truncate">#452 François Chollet: ARC-AGI</div>
        <button onclick="openWin('shelf');openPodcastReader()" style="width:100%;height:28px;padding:0 10px;border-radius:7px;text-align:left;font-size:12px;color:var(--tint-accent);font-weight:600;">暂停 / 展开访谈书页</button>
        <div style="height:0.5px;background:var(--yy-line);margin:4px 8px;"></div>
        <button onclick="openWin('shelf')" style="width:100%;height:28px;padding:0 10px;border-radius:7px;text-align:left;font-size:12px;">打开书架</button>
        <button onclick="openWin('settings','general')" style="width:100%;height:28px;padding:0 10px;border-radius:7px;text-align:left;font-size:12px;">设置…</button>
        <button style="width:100%;height:28px;padding:0 10px;border-radius:7px;text-align:left;font-size:12px;color:var(--yy-danger);">退出语阅</button>
      </div>

      <div style="height:42px;background:rgba(245,244,240,0.88);backdrop-filter:blur(16px);border-top:0.5px solid rgba(0,0,0,0.1);padding:0 18px;display:flex;align-items:center;justify-content:flex-end;gap:12px;color:#2A2B2E;">
        <div style="width:30px;height:28px;border-radius:6px;background:rgba(138,101,63,0.15);color:#2A2B2E;display:flex;align-items:center;justify-content:center;" title="语阅 · 门里出声 (menubar.svg)">
          ${officialSvg('menubar', 17, 'current')}
        </div>
        <div style="width:30px;height:28px;border-radius:6px;color:#2A2B2E;opacity:0.65;display:flex;align-items:center;justify-content:center;" title="静止状态 (menubar-door.svg)">
          ${officialSvg('menubar-door', 17, 'current')}
        </div>
        <span class="tabular" style="font-size:11.5px;">10:42</span>
      </div>
    </div>
  `;
}
