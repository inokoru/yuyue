(() => {
  'use strict';

  const root = document.documentElement;
  const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const canHoverFine = () => window.matchMedia('(hover: hover) and (pointer: fine)').matches;

  /* ------------------------------------------------------------------------
     1. Language System (中文 / EN)
     ------------------------------------------------------------------------ */
  const langButtons = Array.from(document.querySelectorAll('[data-set-lang]'));

  function getLang() {
    return root.dataset.lang === 'en' ? 'en' : 'zh';
  }

  function syncLocalizedMeta(lang) {
    const title = root.getAttribute(lang === 'en' ? 'data-title-en' : 'data-title-zh');
    const desc = root.getAttribute(lang === 'en' ? 'data-desc-en' : 'data-desc-zh');
    if (title) document.title = title;
    const metaDesc = document.querySelector('meta[name="description"]');
    if (metaDesc && desc) metaDesc.setAttribute('content', desc);

    document.querySelectorAll('[data-aria-zh][data-aria-en]').forEach((el) => {
      el.setAttribute('aria-label', el.getAttribute(lang === 'en' ? 'data-aria-en' : 'data-aria-zh'));
    });
    document.querySelectorAll('[data-title-tip-zh][data-title-tip-en]').forEach((el) => {
      el.setAttribute('title', el.getAttribute(lang === 'en' ? 'data-title-tip-en' : 'data-title-tip-zh'));
    });

    langButtons.forEach((btn) => {
      btn.setAttribute('aria-pressed', String(btn.dataset.setLang === lang));
    });
  }

  function setLang(lang, { userInitiated = false } = {}) {
    const next = lang === 'en' ? 'en' : 'zh';
    const prev = getLang();

    const apply = () => {
      root.dataset.lang = next;
      root.lang = next === 'en' ? 'en' : 'zh-CN';
      syncLocalizedMeta(next);
      try { localStorage.setItem('yuyue-lang', next); } catch (_) {}
      try {
        const url = new URL(window.location.href);
        if (url.searchParams.has('lang')) {
          url.searchParams.set('lang', next);
          window.history.replaceState(null, '', url.toString());
        }
      } catch (_) {}
      window.dispatchEvent(new CustomEvent('yuyue:langchange', { detail: { lang: next, userInitiated } }));
    };

    if (userInitiated && prev !== next && !reducedMotion()) {
      const main = document.querySelector('main');
      if (main) {
        main.classList.add('lang-switching');
        window.setTimeout(() => {
          apply();
          requestAnimationFrame(() => main.classList.remove('lang-switching'));
        }, 65);
        return;
      }
    }
    apply();
  }

  langButtons.forEach((btn) => {
    btn.addEventListener('click', () => setLang(btn.dataset.setLang, { userInitiated: true }));
  });
  syncLocalizedMeta(getLang());

  /* ------------------------------------------------------------------------
     2. Theme Toggle (Warm Paper / Espresso Night)
     ------------------------------------------------------------------------ */
  const themeBtns = Array.from(document.querySelectorAll('[data-toggle-theme]'));
  let userPickedPaper = false;

  function isDarkActive() {
    if (root.dataset.theme === 'dark') return true;
    if (root.dataset.theme === 'light') return false;
    return window.matchMedia('(prefers-color-scheme: dark)').matches;
  }

  function syncThemeMeta() {
    const dark = isDarkActive();
    let dynamicMeta = document.querySelector('meta[name="theme-color"][data-dynamic]');
    if (!dynamicMeta) {
      dynamicMeta = document.createElement('meta');
      dynamicMeta.name = 'theme-color';
      dynamicMeta.setAttribute('data-dynamic', 'true');
      document.head.appendChild(dynamicMeta);
    }
    dynamicMeta.content = dark ? '#1A1613' : '#F8F5F0';
    themeBtns.forEach((b) => b.setAttribute('aria-pressed', String(dark)));
  }

  themeBtns.forEach((btn) => {
    btn.addEventListener('click', () => {
      const next = isDarkActive() ? 'light' : 'dark';
      root.dataset.theme = next;
      try { localStorage.setItem('yuyue-theme', next); } catch (_) {}
      syncThemeMeta();
      window.dispatchEvent(new CustomEvent('yuyue:themechange', { detail: { theme: next } }));
    });
  });
  syncThemeMeta();

  /* ------------------------------------------------------------------------
     3. Floating Scroll Pill & Mobile Sheet Navigation
     ------------------------------------------------------------------------ */
  const floatNav = document.querySelector('.float-nav');
  if (floatNav) {
    const onScroll = () => {
      const show = window.scrollY > 220;
      floatNav.classList.toggle('is-visible', show);
      floatNav.setAttribute('aria-hidden', String(!show));
    };
    window.addEventListener('scroll', onScroll, { passive: true });
    onScroll();
  }

  const menuBtns = Array.from(document.querySelectorAll('[data-menu-toggle]'));
  const drawer = document.getElementById('mobile-drawer');
  if (menuBtns.length && drawer) {
    const closeBtn = drawer.querySelector('[data-menu-close]');
    function setMenuOpen(open) {
      menuBtns.forEach((b) => b.setAttribute('aria-expanded', String(open)));
      drawer.classList.toggle('is-open', open);
      drawer.setAttribute('aria-hidden', String(!open));
      document.body.style.overflow = open ? 'hidden' : '';
    }
    menuBtns.forEach((btn) => {
      btn.addEventListener('click', () => setMenuOpen(!drawer.classList.contains('is-open')));
    });
    if (closeBtn) closeBtn.addEventListener('click', () => setMenuOpen(false));
    drawer.addEventListener('click', (e) => {
      if (e.target === drawer || e.target.closest('a')) setMenuOpen(false);
    });
    document.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && drawer.classList.contains('is-open')) setMenuOpen(false);
    });
  }

  /* ------------------------------------------------------------------------
     4. INTERACTION #1 — The Living Book Page + Breathing Acoustic Ink-Wave
     ------------------------------------------------------------------------ */
  const pageEl = document.getElementById('live-reader');
  if (pageEl) {
    const proseArea = pageEl.querySelector('.page-prose-area');
    const bookTabs = Array.from(pageEl.querySelectorAll('[data-book-tab]'));
    const bookPanes = Array.from(pageEl.querySelectorAll('[data-book]'));
    const glossBtn = pageEl.querySelector('[data-toggle-gloss]');
    const voiceBtn = pageEl.querySelector('[data-toggle-voice]');
    const typesetBtn = pageEl.querySelector('[data-toggle-typeset]');
    const typesetDrawer = pageEl.querySelector('#typeset-drawer');
    const paperTiles = Array.from(pageEl.querySelectorAll('[data-set-paper]'));
    const fontPills = Array.from(pageEl.querySelectorAll('[data-set-font]'));
    const sizeSlider = pageEl.querySelector('[data-font-size]');
    const sizeLabel = pageEl.querySelector('[data-font-size-val]');
    const vocabBadge = pageEl.querySelector('[data-vocab-count]');
    const vocabWrap = pageEl.querySelector('[data-vocab-wrap]');
    const inkWavePath = pageEl.querySelector('.ink-wave-path');

    const barMeta = pageEl.querySelector('[data-page-meta]');
    const barCount = pageEl.querySelector('[data-page-count]');
    const barFill = pageEl.querySelector('[data-page-fill]');
    const btnPrev = pageEl.querySelector('[data-cap-prev]');
    const btnNext = pageEl.querySelector('[data-cap-next]');
    const btnPlay = pageEl.querySelector('[data-cap-play]');
    const playLabel = btnPlay ? btnPlay.querySelector('[data-play-label]') : null;

    const selBar = pageEl.querySelector('#sel-bar');
    const thoughtsSlot = pageEl.querySelector('#live-thoughts');

    const dictPopover = pageEl.querySelector('#dict-popover');
    const dictWord = pageEl.querySelector('[data-dict-word]');
    const dictIpa = pageEl.querySelector('[data-dict-ipa]');
    const dictDef = pageEl.querySelector('[data-dict-def]');
    const dictSaveBtn = pageEl.querySelector('[data-dict-save]');
    const dictCloseBtn = pageEl.querySelector('[data-dict-close]');

    const DICT = {
      Concord: {
        ipa: '/ˈkɒŋ.kɔːrd/',
        zh: '康科德（马萨诸塞州小镇，梭罗隐居瓦尔登湖所在地）；本义为“和谐、一致”。',
        en: 'Town in Massachusetts near Walden Pond; also means agreement or quiet harmony.'
      },
      deliberately: {
        ipa: '/dɪˈlɪb.ər.ət.li/',
        zh: 'adv. 审慎地、从容不迫地；清醒自觉地。',
        en: 'adv. Consciously, intentionally, and unhurriedly.'
      },
      marrow: {
        ipa: '/ˈmær.əʊ/',
        zh: 'n. 骨髓；（引申）最核心的精髓与真味。',
        en: 'n. The essential, innermost, or vital part of something.'
      },
      sturdily: {
        ipa: '/ˈstɜː.dɪ.li/',
        zh: 'adv. 笃实地、坚定朴素地。',
        en: 'adv. Firmly, resolutely, and without pretension.'
      },
      rout: {
        ipa: '/raʊt/',
        zh: 'n. 彻底击溃（put to rout：彻底驱散、摒弃）。',
        en: 'n. A decisive defeat ("put to rout" — drive away completely).'
      }
    };

    const savedWords = new Set();
    let activeBook = getLang() === 'en' ? 'en' : 'zh';
    let activeIndex = 1;
    let isPlaying = false;
    let voiceEnabled = false;
    let playTimer = null;
    let selectedDictKey = null;
    let savedRange = null;

    /* Acoustic Ink-Wave SVG animation (zero CPU when calm/paused) */
    let waveRaf = null;
    let waveAmp = 0;
    let wavePhase = 0;

    function buildWavePath(amp, phase) {
      const w = 54;
      const cy = 8;
      if (amp < 0.08) return `M 0 ${cy} L ${w} ${cy}`;
      const steps = 18;
      let d = `M 0 ${cy}`;
      for (let i = 1; i <= steps; i++) {
        const t = i / steps;
        const x = (t * w).toFixed(1);
        const env = Math.sin(Math.PI * t);
        const y = cy + env * amp * (Math.sin(t * Math.PI * 3.2 - phase) * 0.72 + Math.sin(t * Math.PI * 6.4 + phase * 1.4) * 0.28);
        d += ` L ${x} ${y.toFixed(2)}`;
      }
      return d;
    }

    function stepWave() {
      if (!inkWavePath) return;
      const target = isPlaying && !reducedMotion() ? 5.2 : 0;
      waveAmp += (target - waveAmp) * 0.14;
      wavePhase += 0.16;
      inkWavePath.setAttribute('d', buildWavePath(waveAmp, wavePhase));
      if (isPlaying || waveAmp > 0.08) {
        waveRaf = requestAnimationFrame(stepWave);
      } else {
        waveRaf = null;
      }
    }

    function nudgeWave() {
      if (!inkWavePath) return;
      if (reducedMotion()) {
        inkWavePath.setAttribute('d', 'M 0 8 L 54 8');
        return;
      }
      if (!waveRaf) waveRaf = requestAnimationFrame(stepWave);
    }

    const hasSpeech = typeof window.speechSynthesis !== 'undefined';
    if (!hasSpeech && voiceBtn) voiceBtn.hidden = true;

    function getActivePane() {
      return bookPanes.find((p) => p.dataset.book === activeBook) || bookPanes[0];
    }
    function getSentences() {
      const pane = getActivePane();
      return pane ? Array.from(pane.querySelectorAll('.sent')) : [];
    }

    function cancelSpeech() {
      if (hasSpeech) { try { window.speechSynthesis.cancel(); } catch (_) {} }
    }

    function speakSentence(el, onEnd) {
      if (!hasSpeech || !voiceEnabled || !el) return false;
      cancelSpeech();
      const clone = el.cloneNode(true);
      clone.querySelectorAll('rt').forEach((r) => r.remove());
      const text = (clone.textContent || '').trim();
      if (!text) return false;
      const utt = new SpeechSynthesisUtterance(text);
      utt.lang = activeBook === 'zh' ? 'zh-CN' : 'en-US';
      utt.rate = activeBook === 'zh' ? 1.0 : 0.96;
      utt.onend = () => { if (isPlaying && onEnd) onEnd(); };
      utt.onerror = () => { if (isPlaying && onEnd) onEnd(); };
      try { window.speechSynthesis.speak(utt); return true; } catch (_) { return false; }
    }

    function setPaper(paper, manual = false) {
      if (manual) userPickedPaper = true;
      pageEl.dataset.paper = paper;
      paperTiles.forEach((tile) => tile.setAttribute('aria-pressed', String(tile.dataset.setPaper === paper)));
    }

    setPaper(isDarkActive() ? 'kaminight' : 'plain', false);
    window.addEventListener('yuyue:themechange', (e) => {
      if (!userPickedPaper) setPaper(e.detail.theme === 'dark' ? 'kaminight' : 'plain', false);
    });
    paperTiles.forEach((tile) => tile.addEventListener('click', () => setPaper(tile.dataset.setPaper, true)));

    fontPills.forEach((pill) => {
      pill.addEventListener('click', () => {
        pageEl.dataset.font = pill.dataset.setFont;
        fontPills.forEach((p) => p.setAttribute('aria-pressed', String(p === pill)));
      });
    });

    if (sizeSlider) {
      sizeSlider.addEventListener('input', () => {
        pageEl.style.setProperty('--p-size', `${sizeSlider.value}px`);
        if (sizeLabel) sizeLabel.textContent = `${sizeSlider.value}`;
      });
    }

    if (typesetBtn && typesetDrawer) {
      typesetBtn.addEventListener('click', () => {
        const open = !typesetDrawer.classList.contains('is-open');
        typesetDrawer.classList.toggle('is-open', open);
        typesetBtn.setAttribute('aria-pressed', String(open));
      });
    }

    function updateBar() {
      const sents = getSentences();
      const total = sents.length || 1;
      const num = activeIndex + 1;
      const uiLang = getLang();

      if (barMeta) {
        barMeta.textContent = activeBook === 'zh'
          ? (uiLang === 'zh' ? '悉达多 · 第一部' : 'Siddhartha · Part I')
          : 'Walden · Chapter II';
      }
      if (barCount) {
        barCount.textContent = uiLang === 'zh' ? `第 ${num} / ${total} 句` : `Sentence ${num} / ${total}`;
      }
      if (barFill) {
        barFill.style.transform = `scaleX(${Math.max(0.08, num / total)})`;
      }
      if (playLabel && btnPlay) {
        playLabel.textContent = isPlaying
          ? (uiLang === 'zh' ? '暂停' : 'Pause')
          : (uiLang === 'zh' ? '朗读' : 'Listen');
        btnPlay.setAttribute('aria-pressed', String(isPlaying));
      }
    }

    function activateSentence(idx, { animateSweep = true, speak = false } = {}) {
      const sents = getSentences();
      if (!sents.length) return;
      activeIndex = ((idx % sents.length) + sents.length) % sents.length;
      sents.forEach((s) => {
        s.classList.remove('is-active', 'is-active-instant');
        s.removeAttribute('aria-current');
      });
      const target = sents[activeIndex];
      target.setAttribute('aria-current', 'true');
      if (animateSweep && !reducedMotion()) {
        void target.offsetWidth;
        target.classList.add('is-active');
      } else {
        target.classList.add('is-active-instant');
      }
      updateBar();
      if (speak && voiceEnabled) {
        speakSentence(target, () => {
          if (isPlaying) activateSentence(activeIndex + 1, { animateSweep: true, speak: true });
        });
      }
    }

    function stopPlayback() {
      isPlaying = false;
      if (playTimer) { window.clearInterval(playTimer); playTimer = null; }
      cancelSpeech();
      updateBar();
      nudgeWave();
    }

    function startPlayback() {
      stopPlayback();
      isPlaying = true;
      nudgeWave();
      if (voiceEnabled && hasSpeech) {
        activateSentence(activeIndex, { animateSweep: true, speak: true });
      } else {
        activateSentence(activeIndex, { animateSweep: true, speak: false });
        playTimer = window.setInterval(() => {
          activateSentence(activeIndex + 1, { animateSweep: true, speak: false });
        }, 3400);
      }
    }

    if (voiceBtn) {
      voiceBtn.addEventListener('click', () => {
        voiceEnabled = !voiceEnabled;
        voiceBtn.setAttribute('aria-pressed', String(voiceEnabled));
        if (voiceEnabled) startPlayback();
        else { cancelSpeech(); if (isPlaying) startPlayback(); }
      });
    }

    function closeDict() {
      if (!dictPopover) return;
      dictPopover.classList.remove('is-open');
      dictPopover.setAttribute('aria-hidden', 'true');
      selectedDictKey = null;
      pageEl.querySelectorAll('.word-gloss.is-selected').forEach((w) => w.classList.remove('is-selected'));
    }

    function updateVocabCounter(animate = false) {
      if (!vocabBadge || !vocabWrap) return;
      vocabBadge.textContent = String(savedWords.size);
      vocabWrap.hidden = savedWords.size === 0;
      if (animate && !reducedMotion()) {
        vocabBadge.classList.remove('pop');
        void vocabBadge.offsetWidth;
        vocabBadge.classList.add('pop');
        window.setTimeout(() => vocabBadge.classList.remove('pop'), 240);
      }
    }

    function updateDictSaveBtn() {
      if (!dictSaveBtn || !selectedDictKey) return;
      const saved = savedWords.has(selectedDictKey);
      const uiLang = getLang();
      dictSaveBtn.classList.toggle('is-saved', saved);
      dictSaveBtn.textContent = saved
        ? (uiLang === 'zh' ? '✓ 已记入生词本' : '✓ Saved')
        : (uiLang === 'zh' ? '+ 加入生词本' : '+ Save word');
    }

    function openDict(wordEl) {
      if (!dictPopover || !proseArea) return;
      const key = wordEl.dataset.word;
      const entry = DICT[key];
      if (!entry) return;
      closeDict();
      hideSelBar();
      selectedDictKey = key;
      wordEl.classList.add('is-selected');
      const uiLang = getLang();
      dictWord.textContent = key;
      dictIpa.textContent = entry.ipa;
      dictDef.textContent = uiLang === 'zh' ? entry.zh : entry.en;
      updateDictSaveBtn();

      const areaRect = proseArea.getBoundingClientRect();
      const wordRect = wordEl.getBoundingClientRect();
      const popWidth = Math.min(296, areaRect.width - 28);
      let left = wordRect.left - areaRect.left + wordRect.width / 2 - popWidth / 2;
      left = Math.max(14, Math.min(left, areaRect.width - popWidth - 14));
      const top = wordRect.bottom - areaRect.top + 8;

      dictPopover.style.left = `${Math.round(left)}px`;
      dictPopover.style.top = `${Math.round(top)}px`;
      dictPopover.setAttribute('aria-hidden', 'false');
      dictPopover.classList.add('is-open');
    }

    function hideSelBar() {
      if (!selBar) return;
      selBar.classList.remove('is-open');
      selBar.setAttribute('aria-hidden', 'true');
      savedRange = null;
    }

    function checkSelection() {
      if (!selBar || !proseArea) return;
      const sel = window.getSelection();
      if (!sel || sel.isCollapsed || sel.rangeCount === 0) { hideSelBar(); return; }
      const text = sel.toString().trim();
      if (text.length < 2) { hideSelBar(); return; }
      const range = sel.getRangeAt(0);
      if (!proseArea.contains(range.commonAncestorContainer)) { hideSelBar(); return; }
      if (range.commonAncestorContainer.nodeType === 1 &&
          (range.commonAncestorContainer.closest('#dict-popover') || range.commonAncestorContainer.closest('#live-thoughts'))) {
        hideSelBar();
        return;
      }

      savedRange = range.cloneRange();
      const rect = range.getBoundingClientRect();
      const areaRect = proseArea.getBoundingClientRect();
      let left = rect.left - areaRect.left + rect.width / 2;
      left = Math.max(88, Math.min(left, areaRect.width - 88));
      const top = Math.max(6, rect.top - areaRect.top - 42);

      selBar.style.left = `${Math.round(left)}px`;
      selBar.style.top = `${Math.round(top)}px`;
      selBar.setAttribute('aria-hidden', 'false');
      selBar.classList.add('is-open');
    }

    if (proseArea && selBar) {
      proseArea.addEventListener('mouseup', () => window.setTimeout(checkSelection, 10));
      proseArea.addEventListener('touchend', () => window.setTimeout(checkSelection, 60));
      selBar.addEventListener('mousedown', (e) => e.preventDefault());
      selBar.addEventListener('click', (e) => {
        const dot = e.target.closest('[data-hl]');
        const noteBtn = e.target.closest('[data-add-note]');
        if (!savedRange) return;

        if (dot) {
          const kind = dot.dataset.hl;
          const span = document.createElement('span');
          span.className = kind === 'y' ? 'user-hl-y' : kind === 'w' ? 'user-hl-w' : 'user-hl-b';
          try {
            span.appendChild(savedRange.extractContents());
            savedRange.insertNode(span);
          } catch (_) {}
          window.getSelection().removeAllRanges();
          hideSelBar();
          return;
        }

        if (noteBtn && thoughtsSlot) {
          const quote = savedRange.toString().trim().slice(0, 46);
          try {
            const hlSpan = document.createElement('span');
            hlSpan.className = 'user-hl-y';
            hlSpan.appendChild(savedRange.extractContents());
            savedRange.insertNode(hlSpan);
          } catch (_) {}
          window.getSelection().removeAllRanges();
          hideSelBar();

          thoughtsSlot.querySelectorAll('.thought-composer').forEach((el) => el.remove());
          const uiLang = getLang();
          const composer = document.createElement('form');
          composer.className = 'thought-composer';
          composer.innerHTML =
            `<div class="thought-composer-quote">“${quote}${quote.length >= 46 ? '…' : ''}”</div>` +
            `<div class="thought-composer-row">` +
            `<input type="text" placeholder="${uiLang === 'zh' ? '写下此刻的想法…' : 'Write a quick thought…'}" autocomplete="off">` +
            `<button type="submit" class="bar-btn bar-btn-play">${uiLang === 'zh' ? '记下' : 'Save'}</button>` +
            `<button type="button" class="bar-btn" data-cancel-thought>${uiLang === 'zh' ? '取消' : 'Cancel'}</button>` +
            `</div>`;
          const input = composer.querySelector('input');
          composer.querySelector('[data-cancel-thought]').addEventListener('click', () => composer.remove());
          composer.addEventListener('submit', (ev) => {
            ev.preventDefault();
            const val = input.value.trim() || (uiLang === 'zh' ? '读到这里很有感触。' : 'Worth revisiting later.');
            const card = document.createElement('div');
            card.className = 'live-thought';
            const body = document.createElement('div');
            const title = document.createElement('b');
            title.textContent = uiLang === 'zh' ? '想法 · 本页摘录' : 'Thought · Page note';
            const qEl = document.createElement('span');
            qEl.className = 'thought-quote';
            qEl.textContent = `“${quote}${quote.length >= 46 ? '…' : ''}”`;
            const textEl = document.createElement('span');
            textEl.textContent = val;
            body.append(title, qEl, textEl);
            const del = document.createElement('button');
            del.type = 'button';
            del.setAttribute('aria-label', uiLang === 'zh' ? '删除想法' : 'Remove note');
            del.textContent = '×';
            del.addEventListener('click', () => card.remove());
            card.append(body, del);
            composer.replaceWith(card);
          });
          thoughtsSlot.prepend(composer);
          input.focus();
        }
      });
    }

    function switchBook(book, { keepIndex = false } = {}) {
      activeBook = book === 'en' ? 'en' : 'zh';
      closeDict();
      hideSelBar();
      bookTabs.forEach((tab) => tab.setAttribute('aria-selected', String(tab.dataset.bookTab === activeBook)));
      bookPanes.forEach((pane) => { pane.hidden = pane.dataset.book !== activeBook; });
      if (glossBtn) glossBtn.hidden = activeBook !== 'en';
      activateSentence(keepIndex ? activeIndex : 1, { animateSweep: false });
    }

    bookTabs.forEach((tab) => tab.addEventListener('click', () => { stopPlayback(); switchBook(tab.dataset.bookTab); }));

    if (glossBtn) {
      glossBtn.addEventListener('click', () => {
        const next = glossBtn.getAttribute('aria-pressed') !== 'true';
        glossBtn.setAttribute('aria-pressed', String(next));
        pageEl.classList.toggle('glosses-off', !next);
      });
    }

    pageEl.addEventListener('click', (e) => {
      const sel = window.getSelection();
      if (sel && !sel.isCollapsed && sel.toString().trim().length >= 2) return;
      const wordEl = e.target.closest('.word-gloss');
      if (wordEl) {
        e.stopPropagation();
        if (selectedDictKey === wordEl.dataset.word) closeDict();
        else openDict(wordEl);
        return;
      }
      const sentEl = e.target.closest('.sent');
      if (sentEl) {
        closeDict();
        hideSelBar();
        const sents = getSentences();
        const idx = sents.indexOf(sentEl);
        if (idx !== -1) {
          if (isPlaying) { activeIndex = idx; startPlayback(); }
          else activateSentence(idx, { animateSweep: true, speak: voiceEnabled });
        }
        return;
      }
      if (dictPopover && dictPopover.classList.contains('is-open') && !e.target.closest('#dict-popover')) closeDict();
    });

    pageEl.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter' && e.key !== ' ') return;
      const wordEl = e.target.closest('.word-gloss');
      if (wordEl) {
        e.preventDefault();
        if (selectedDictKey === wordEl.dataset.word) closeDict();
        else openDict(wordEl);
      }
    });

    if (dictSaveBtn) {
      dictSaveBtn.addEventListener('click', () => {
        if (!selectedDictKey) return;
        const added = !savedWords.has(selectedDictKey);
        if (added) savedWords.add(selectedDictKey);
        else savedWords.delete(selectedDictKey);
        updateDictSaveBtn();
        updateVocabCounter(added);
      });
    }
    if (dictCloseBtn) dictCloseBtn.addEventListener('click', closeDict);
    if (btnPlay) btnPlay.addEventListener('click', () => { if (isPlaying) stopPlayback(); else startPlayback(); });
    if (btnPrev) btnPrev.addEventListener('click', () => {
      if (isPlaying) { activeIndex -= 1; startPlayback(); }
      else activateSentence(activeIndex - 1, { animateSweep: true, speak: voiceEnabled });
    });
    if (btnNext) btnNext.addEventListener('click', () => {
      if (isPlaying) { activeIndex += 1; startPlayback(); }
      else activateSentence(activeIndex + 1, { animateSweep: true, speak: voiceEnabled });
    });

    document.addEventListener('visibilitychange', () => { if (document.hidden && isPlaying) stopPlayback(); });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') { closeDict(); hideSelBar(); } });
    document.addEventListener('mousedown', (e) => {
      if (selBar && !selBar.contains(e.target) && proseArea && !proseArea.contains(e.target)) hideSelBar();
    });

    window.addEventListener('yuyue:langchange', (e) => {
      stopPlayback();
      switchBook(e.detail.lang === 'en' ? 'en' : 'zh', { keepIndex: true });
      updateBar();
    });

    switchBook(activeBook);
  }

  /* ------------------------------------------------------------------------
     5. INTERACTION #2 — 60fps Draggable Paper Curtain + Spring Click-Glide
     ------------------------------------------------------------------------ */
  const curtain = document.getElementById('paper-curtain');
  if (curtain) {
    const dayLayer = curtain.querySelector('.curtain-day');
    const nightLayer = curtain.querySelector('.curtain-night');
    const divider = curtain.querySelector('.curtain-divider');
    const handle = curtain.querySelector('.curtain-handle');
    const dayBtns = Array.from(document.querySelectorAll('[data-curtain-day]'));
    const nightBtns = Array.from(document.querySelectorAll('[data-curtain-night]'));

    const DAY_LABELS = {
      kami: { zh: '纸本 · 温暖纸书', en: 'Paper · Warm Print' },
      plain: { zh: '素白 · 清爽明净', en: 'Plain · Crisp White' },
      sage: { zh: '芦苇 · 柔和护眼', en: 'Reed · Soft Sage' },
      eink: { zh: '铅纸 · 墨水屏质感', en: 'E-Ink · Matte Grain' }
    };
    const NIGHT_LABELS = {
      kaminight: { zh: '纸本·夜 · 深夜暖黑', en: 'Night Paper · Warm Dark' },
      dark: { zh: '暗黑 · 纯净深灰', en: 'Dark · Pure Charcoal' }
    };

    let ratio = 0.54;
    let targetRatio = 0.54;
    let glideRaf = null;
    let dragging = false;
    let activePointerId = null;

    function syncCurtainLabels() {
      const lang = getLang();
      const dKey = dayLayer.dataset.dayPaper || 'kami';
      const nKey = nightLayer.dataset.nightPaper || 'kaminight';
      curtain.querySelectorAll('[data-day-label]').forEach((el) => { el.textContent = (DAY_LABELS[dKey] || DAY_LABELS.kami)[lang]; });
      curtain.querySelectorAll('[data-night-label]').forEach((el) => { el.textContent = (NIGHT_LABELS[nKey] || NIGHT_LABELS.kaminight)[lang]; });
    }

    function paintSplit(r) {
      ratio = Math.max(0.08, Math.min(0.92, r));
      const rect = curtain.getBoundingClientRect();
      const x = Math.round(rect.width * ratio);
      const pct = (ratio * 100).toFixed(2);
      nightLayer.style.clipPath = `inset(0 0 0 ${pct}%)`;
      divider.style.transform = `translateX(${x}px)`;
      handle.style.transform = `translateX(${x}px)`;
      handle.setAttribute('aria-valuenow', String(Math.round(ratio * 100)));
    }

    function glideTo(r) {
      targetRatio = Math.max(0.08, Math.min(0.92, r));
      if (reducedMotion()) { paintSplit(targetRatio); return; }
      if (glideRaf) cancelAnimationFrame(glideRaf);
      const step = () => {
        const diff = targetRatio - ratio;
        if (Math.abs(diff) < 0.002) {
          paintSplit(targetRatio);
          glideRaf = null;
          return;
        }
        paintSplit(ratio + diff * 0.22);
        glideRaf = requestAnimationFrame(step);
      };
      glideRaf = requestAnimationFrame(step);
    }

    function ratioFromX(clientX) {
      const rect = curtain.getBoundingClientRect();
      return rect.width > 0 ? (clientX - rect.left) / rect.width : ratio;
    }

    handle.addEventListener('pointerdown', (e) => {
      if (dragging) return;
      if (glideRaf) { cancelAnimationFrame(glideRaf); glideRaf = null; }
      dragging = true;
      activePointerId = e.pointerId;
      handle.setPointerCapture(e.pointerId);
      paintSplit(ratioFromX(e.clientX));
    });
    handle.addEventListener('pointermove', (e) => {
      if (!dragging || e.pointerId !== activePointerId) return;
      paintSplit(ratioFromX(e.clientX));
    });
    const endDrag = (e) => {
      if (!dragging || e.pointerId !== activePointerId) return;
      dragging = false;
      activePointerId = null;
    };
    handle.addEventListener('pointerup', endDrag);
    handle.addEventListener('pointercancel', endDrag);

    curtain.addEventListener('pointerdown', (e) => {
      if (e.target === handle) return;
      glideTo(ratioFromX(e.clientX));
    });

    handle.addEventListener('keydown', (e) => {
      if (e.key === 'ArrowLeft') { e.preventDefault(); glideTo(ratio - 0.06); }
      else if (e.key === 'ArrowRight') { e.preventDefault(); glideTo(ratio + 0.06); }
    });

    dayBtns.forEach((btn) => {
      btn.addEventListener('click', () => {
        dayLayer.dataset.dayPaper = btn.dataset.curtainDay;
        dayBtns.forEach((b) => b.setAttribute('aria-pressed', String(b === btn)));
        syncCurtainLabels();
      });
    });
    nightBtns.forEach((btn) => {
      btn.addEventListener('click', () => {
        nightLayer.dataset.nightPaper = btn.dataset.curtainNight;
        nightBtns.forEach((b) => b.setAttribute('aria-pressed', String(b === btn)));
        syncCurtainLabels();
      });
    });

    window.addEventListener('yuyue:langchange', syncCurtainLabels);
    window.addEventListener('resize', () => paintSplit(ratio), { passive: true });
    syncCurtainLabels();
    requestAnimationFrame(() => paintSplit(ratio));
  }

  /* ------------------------------------------------------------------------
     7. INTERACTION #4 — Highlight Export Studio + 3D Tilt Quote + 3D Anki Flip
     ------------------------------------------------------------------------ */
  const exportStudio = document.getElementById('export-studio');
  if (exportStudio) {
    const tabs = Array.from(exportStudio.querySelectorAll('[data-export-tab]'));
    const panes = Array.from(exportStudio.querySelectorAll('[data-export-pane]'));

    function selectExport(key, { focus = false } = {}) {
      tabs.forEach((t) => {
        const active = t.dataset.exportTab === key;
        t.setAttribute('aria-selected', String(active));
        t.tabIndex = active ? 0 : -1;
        if (focus && active) t.focus();
      });
      panes.forEach((p) => { p.hidden = p.dataset.exportPane !== key; });
    }

    tabs.forEach((tab, i) => {
      tab.addEventListener('click', () => selectExport(tab.dataset.exportTab));
      tab.addEventListener('keydown', (e) => {
        if (e.key === 'ArrowRight') { e.preventDefault(); selectExport(tabs[(i + 1) % tabs.length].dataset.exportTab, { focus: true }); }
        else if (e.key === 'ArrowLeft') { e.preventDefault(); selectExport(tabs[(i - 1 + tabs.length) % tabs.length].dataset.exportTab, { focus: true }); }
      });
    });

    /* Subtle 3D tilt & sheen + paper picker on the Literary Quote Card */
    const qCard = exportStudio.querySelector('.quote-card-3d');
    const qSheen = exportStudio.querySelector('.quote-sheen');
    const qPaperDots = Array.from(exportStudio.querySelectorAll('[data-qpaper-pick]'));
    let userPickedQPaper = false;

    function setQPaper(p, manual = false) {
      if (!qCard) return;
      if (manual) userPickedQPaper = true;
      qCard.dataset.qpaper = p;
      qPaperDots.forEach((d) => d.setAttribute('aria-pressed', String(d.dataset.qpaperPick === p)));
    }
    setQPaper(isDarkActive() ? 'night' : 'kami', false);
    window.addEventListener('yuyue:themechange', (e) => {
      if (!userPickedQPaper) setQPaper(e.detail.theme === 'dark' ? 'night' : 'kami', false);
    });
    qPaperDots.forEach((dot) => {
      dot.addEventListener('click', () => setQPaper(dot.dataset.qpaperPick, true));
    });

    if (qCard && !reducedMotion()) {
      qCard.addEventListener('pointermove', (e) => {
        if (!canHoverFine()) return;
        const r = qCard.getBoundingClientRect();
        const nx = (e.clientX - r.left) / r.width;
        const ny = (e.clientY - r.top) / r.height;
        const rx = (0.5 - ny) * 8;
        const ry = (nx - 0.5) * 9;
        qCard.style.transform = `rotateX(${rx.toFixed(2)}deg) rotateY(${ry.toFixed(2)}deg)`;
        if (qSheen) {
          qSheen.style.opacity = '1';
          qSheen.style.background = `radial-gradient(circle at ${(nx * 100).toFixed(0)}% ${(ny * 100).toFixed(0)}%, rgba(255,255,255,0.28), transparent 62%)`;
        }
      });
      qCard.addEventListener('pointerleave', () => {
        qCard.style.transform = 'rotateX(0deg) rotateY(0deg)';
        if (qSheen) qSheen.style.opacity = '0';
      });
    }

    /* 3D Flippable Anki Flashcard */
    const ankiCard = exportStudio.querySelector('#anki-card');
    if (ankiCard) {
      ankiCard.addEventListener('click', () => {
        const flipped = ankiCard.classList.toggle('is-flipped');
        ankiCard.setAttribute('aria-pressed', String(flipped));
      });
    }
  }

  /* ------------------------------------------------------------------------
     8. INTERACTION #5 — Spoiler-Free Reading Companion (The Great Gatsby)
     ------------------------------------------------------------------------ */
  const compBox = document.getElementById('companion-demo');
  if (compBox) {
    const promptBtns = Array.from(compBox.querySelectorAll('[data-comp-q]'));
    const replyEl = compBox.querySelector('[data-comp-reply]');
    const lines = Array.from(compBox.querySelectorAll('[data-comp-line]'));
    let activeStreamTimer = null;

    const ANSWERS = {
      q1: {
        zh: {
          prefix: '从开篇这两句话就能看出，叙述者尼克习惯克制、少下武断评判。他牢记父亲的提醒——不要拿自己拥有的条件去苛责别人',
          cite: '第 2 句', citeLine: '2',
          suffix: '——这也让他成了后面最容易倾听他人秘密的旁观者。'
        },
        en: {
          prefix: 'Right from the opening lines, Nick presents himself as restrained and slow to judge. Remembering his father’s warning not to measure others by his own advantages',
          cite: 'Line 2', citeLine: '2',
          suffix: ' explains why people around him so readily confide in him.'
        }
      },
      q2: {
        zh: {
          prefix: '作者用回忆父训的方式轻轻定下整本书的语调：既带着年轻时的谦逊自省',
          cite: '第 1 句', citeLine: '1',
          suffix: '，又暗暗埋下阶层差异与「优越条件」的伏笔，引导你带着克制的眼光走进后面的故事。'
        },
        en: {
          prefix: 'Opening with a father’s quiet counsel sets the book’s tone immediately—combining reflective humility',
          cite: 'Line 1', citeLine: '1',
          suffix: ' with an early hint about privilege and perspective before the story even begins.'
        }
      },
      q3: {
        zh: {
          prefix: '这部分还在你当前的阅读进度之后。',
          cite: null,
          suffix: '「书伴」默认只读取你已经翻过的章节和划线，不会提前剧透盖茨比的结局。等你读到后半程，再来一起聊吧。'
        },
        en: {
          prefix: 'That happens beyond your current bookmark.',
          cite: null,
          suffix: ' By default, the Reading Companion only reads up to where you are—so it won’t spoil Gatsby’s fate. Ask again once you get there.'
        }
      }
    };

    function clearHighlights() { lines.forEach((l) => l.classList.remove('is-cited')); }
    function highlightLine(id) {
      clearHighlights();
      const target = compBox.querySelector(`[data-comp-line="${id}"]`);
      if (target) target.classList.add('is-cited');
    }

    function renderAnswer(key, animate = true) {
      if (activeStreamTimer) { window.clearInterval(activeStreamTimer); activeStreamTimer = null; }
      clearHighlights();
      promptBtns.forEach((b) => b.setAttribute('aria-pressed', String(b.dataset.compQ === key)));
      const lang = getLang();
      const data = ANSWERS[key][lang];

      const buildFinalHTML = () => data.cite
        ? `${data.prefix} <button type="button" class="cite-pill" data-jump-line="${data.citeLine}">${data.cite}</button>${data.suffix}`
        : `<strong>${data.prefix}</strong>${data.suffix}`;

      const bindCitation = () => {
        const pill = replyEl.querySelector('[data-jump-line]');
        if (pill) {
          const lineId = pill.dataset.jumpLine;
          pill.addEventListener('click', () => highlightLine(lineId));
          pill.addEventListener('mouseenter', () => highlightLine(lineId));
          highlightLine(lineId);
        }
      };

      if (!animate || reducedMotion()) {
        replyEl.innerHTML = buildFinalHTML();
        bindCitation();
        return;
      }

      const plain = data.prefix + ' ' + data.suffix;
      let i = 0;
      replyEl.textContent = '';
      activeStreamTimer = window.setInterval(() => {
        i += lang === 'zh' ? 2 : 4;
        if (i >= plain.length) {
          window.clearInterval(activeStreamTimer);
          activeStreamTimer = null;
          replyEl.innerHTML = buildFinalHTML();
          bindCitation();
        } else {
          replyEl.textContent = plain.slice(0, i);
        }
      }, 18);
    }

    promptBtns.forEach((btn) => btn.addEventListener('click', () => renderAnswer(btn.dataset.compQ, true)));
    window.addEventListener('yuyue:langchange', () => {
      const current = promptBtns.find((b) => b.getAttribute('aria-pressed') === 'true');
      renderAnswer(current ? current.dataset.compQ : 'q1', false);
    });
    renderAnswer('q1', false);
  }

  /* ------------------------------------------------------------------------
     9. Platform Hint, Copy Code, Changelog Filter
     ------------------------------------------------------------------------ */
  const platformCards = document.querySelectorAll('[data-platform]');
  if (platformCards.length) {
    const ua = navigator.userAgent || '';
    const plat = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || '';
    let detected = '';
    if (/android/i.test(ua)) detected = 'android';
    else if (/iphone|ipad|ipod/i.test(ua) || (/mac/i.test(plat) && navigator.maxTouchPoints > 1)) detected = 'ios';
    else if (/mac/i.test(plat) || /macintosh/i.test(ua)) detected = 'mac';
    else if (/win/i.test(plat) || /windows/i.test(ua)) detected = 'windows';
    if (detected) {
      platformCards.forEach((c) => { if (c.dataset.platform === detected) c.classList.add('is-detected'); });
    }
  }

  document.querySelectorAll('[data-copy]').forEach((btn) => {
    btn.addEventListener('click', async () => {
      const box = btn.closest('.code-box');
      if (!box) return;
      const pres = Array.from(box.querySelectorAll('pre'));
      const pre = pres.find((p) => window.getComputedStyle(p).display !== 'none') || pres[0];
      if (!pre) return;
      try {
        await navigator.clipboard.writeText(pre.textContent.trim());
        btn.classList.add('is-copied');
        const zh = btn.querySelector('.i18n-zh');
        const en = btn.querySelector('.i18n-en');
        if (zh) zh.textContent = '已复制';
        if (en) en.textContent = 'Copied';
        window.setTimeout(() => {
          btn.classList.remove('is-copied');
          if (zh) zh.textContent = '复制';
          if (en) en.textContent = 'Copy';
        }, 1600);
      } catch (_) {}
    });
  });

  const filterBtns = Array.from(document.querySelectorAll('[data-filter]'));
  const changeItems = Array.from(document.querySelectorAll('[data-cat]'));
  if (filterBtns.length && changeItems.length) {
    filterBtns.forEach((btn) => {
      btn.addEventListener('click', () => {
        const cat = btn.dataset.filter;
        filterBtns.forEach((b) => b.setAttribute('aria-pressed', String(b === btn)));
        changeItems.forEach((item) => { item.hidden = cat !== 'all' && item.dataset.cat !== cat; });
      });
    });
  }
})();
