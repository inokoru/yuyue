using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Reader;
using Yuyue.Speech;

namespace Yuyue.UI.Reader;

public sealed partial class ReaderPaneView
{
    private (ReaderPoint Start, ReaderPoint End, string Text, ReaderRect Rect)? _activeSelection;
    private Mark? _editingMark;
    private WordLookupResult? _activeWordLookup;
    private ReaderWord? _activeWordMsg;
    private bool _wordAlreadySaved;
    private int _wheelAccum;
    private bool _ttsFollowPage = true;
    private int? _autoTurnTargetPage;
    private DispatcherTimer? _scrollEdgeCueTimer;

    private PageGeometry BuildGeometry()
    {
        double width = BookStage.ActualWidth > 0 ? BookStage.ActualWidth : 940;
        byte cols = EffectiveColumnCount();
        double measure = _settings.FontSize * 31.0;
        double margin = Math.Round(Math.Max(width < 520 ? 22 : 38, (width / cols - measure) / 2.0));
        return new PageGeometry(
            Paging: Paging.Snap,
            Margin: margin,
            Top: 28,
            Bottom: 28,
            Columns: cols,
            ReducedMotion: _library.Prefs.ImmersiveNoAnimation || _library.Prefs.GlobalEinkMode);
    }

    private void LoadSection(uint section, bool forceReload = false)
    {
        _currentSection = section;
        _currentPage = 0;
        _currentSentence = 0;
        _scrollProgress = 0;
        _autoTurnTargetPage = null;
        _layout = null;
        _scrollEdgeCueTimer?.Stop();
        ScrollEdgeCue.Visibility = Visibility.Collapsed;
        try { _currentSentences = _library.GetSpokenSentences(_bookId, section); }
        catch { _currentSentences = []; }

        DismissOverlays();
        UpdateHeaderAndStatus();
        _pager.OpenSection(section, forceReload, joinAtEnd: _pendingEndOfSection);
        if (_leftPaneOpen && _leftPaneTab == "toc") RenderLeftPane();
    }

    private void PushGeometryToReader()
    {
        if (!_pager.IsInitialized) return;
        var g = BuildGeometry();
        _ = _pager.ApplySettingsAllAsync(new
        {
            margin = g.Margin,
            top = g.Top,
            bottom = g.Bottom,
            cols = g.Columns,
            motion = g.ReducedMotion ? "reduced" : "full",
        });
    }

    private void PushSettingsToReader(bool save) => _ = PushSettingsToReaderAsync(save);

    private Task PushSettingsToReaderAsync(bool save)
    {
        if (save)
        {
            if (_ownSettings) _library.SetBookReaderSettings(_bookId, _settings);
            else
            {
                _library.SaveReaderSettings(_settings);
                App.CurrentApp.BroadcastReaderSettingsChanged();
            }
        }

        ApplyPaperAndChrome();
        if (!_pager.IsInitialized) return Task.CompletedTask;

        var resolved = PaperAppearance.Resolve(_library, _settings);
        Paper effectivePreset = resolved.Preset;
        string? paperStyle = null;
        string? texture = null;
        if (resolved.Custom is { } cp)
        {
            try
            {
                effectivePreset = YuyueCoreMethods.PaperColors(cp).Preset;
                paperStyle = YuyueCoreMethods.PaperStyle(cp);
                texture = cp.Texture == PaperTexture.None ? null : cp.Texture.ToString().ToLowerInvariant();
            }
            catch { }
        }

        var g = BuildGeometry();
        return _pager.ApplySettingsAllAsync(new
        {
            paper = PaperAppearance.PaperCssId(effectivePreset),
            paperStyle,
            texture,
            eink = _library.Prefs.GlobalEinkMode,
            einkGray = _library.Prefs.GlobalEinkMode && _library.Prefs.GlobalEinkGray,
            mode = _settings.Mode == LayoutMode.Scrolled ? "scrolled" : "paginated",
            paging = "snap",
            fontSize = _settings.FontSize,
            lineHeight = _settings.LineHeight,
            paragraphSpacing = _settings.ParagraphSpacing,
            justify = _settings.Justify,
            indent = _settings.Indent,
            font = ResolveFontCssValue(_settings.Font, _settings.FontFace),
            compress = _settings.Compress,
            motion = g.ReducedMotion ? "reduced" : "full",
            margin = g.Margin,
            top = g.Top,
            bottom = g.Bottom,
            cols = g.Columns,
        });
    }

    private static string QuoteCssFontFamily(string raw)
    {
        var sb = new StringBuilder(raw.Length + 2);
        sb.Append('"');
        foreach (char ch in raw)
        {
            if (ch is '"' or '\\' or '<' or '>' or ';' or '{' or '}') continue;
            sb.Append(ch);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string? ResolveFontCssValue(Font? font, string? face)
    {
        static string FallbackVar(Font? f) => f switch
        {
            Font.Sans => "var(--yy-sans)",
            Font.Serif => "var(--yy-serif)",
            Font.Kai => "var(--yy-kaiti)",
            Font.WenKai => "var(--yy-wenkai)",
            _ => "var(--yy-p-font)",
        };

        if (!string.IsNullOrWhiteSpace(face))
        {
            string trimmed = face.Trim();
            if (trimmed.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
            {
                string localFamily = trimmed[6..].Trim();
                if (!string.IsNullOrEmpty(localFamily))
                    return $"{QuoteCssFontFamily(localFamily)}, {FallbackVar(font)}";
            }
            if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                string fileName = trimmed[5..].Trim();
                if (!string.IsNullOrEmpty(fileName) &&
                    !fileName.StartsWith('.') &&
                    !fileName.Contains('/') &&
                    !fileName.Contains('\\') &&
                    !fileName.Contains('"') &&
                    !fileName.Contains('\''))
                {
                    return $"{QuoteCssFontFamily($"yy-file:{fileName}")}, {FallbackVar(font)}";
                }
            }
            if (string.Equals(trimmed, "wenkai", StringComparison.OrdinalIgnoreCase))
                return "wenkai";
            if (string.Equals(trimmed, "notoserif", StringComparison.OrdinalIgnoreCase))
                return $"\"yy-notoserif\", {FallbackVar(font ?? Font.Serif)}";
            if (string.Equals(trimmed, "notosans", StringComparison.OrdinalIgnoreCase))
                return $"\"yy-notosans\", {FallbackVar(font ?? Font.Sans)}";

            var pack = YuyueCoreMethods.FontCatalog()
                .FirstOrDefault(p => string.Equals(p.Id, trimmed, StringComparison.OrdinalIgnoreCase));
            if (pack is not null)
            {
                Font packFb = font ?? pack.Kind switch
                {
                    FontKind.Hei => Font.Sans,
                    FontKind.Kai or FontKind.Hand => Font.Kai,
                    _ => Font.Serif,
                };
                return $"{QuoteCssFontFamily($"yy-{pack.Id}")}, {FallbackVar(packFb)}";
            }
        }
        return font switch
        {
            Font.Sans => "sans",
            Font.Serif => "serif",
            Font.Kai => "kai",
            Font.WenKai => "wenkai",
            _ => null,
        };
    }

    private async Task DecorateSectionHostAsync(ReaderWebViewHost host, uint section)
    {
        try
        {
            var marks = _library.GetMarks(_bookId).Where(m => m.Section == section).ToList();
            await host.SetMarksAsync(marks);

            bool isForeign = IsForeignBook();
            await host.SetLookupAsync(isForeign && _library.Prefs.ForeignLookup);
            if (isForeign)
            {
                if (_library.Prefs.ForeignMarkVocab)
                    await host.SetVocabAsync(_library.GetWords().Where(w => !w.Known).Select(w => w.Word));
                else
                    await host.SetVocabAsync([]);

                if (_library.Prefs.ForeignGlossEnabled && _library.Prefs.GlossLevel > 0)
                    await host.SetGlossesAsync(_library.GetGlosses(_bookId, section, _library.Prefs.GlossLevel, _library.Prefs.GlossVocabOnly));
                else
                    await host.SetGlossesAsync([]);
            }
        }
        catch { }
    }

    private async Task RefreshSectionOverlaysAsync()
    {
        if (!_pager.IsInitialized) return;
        await DecorateSectionHostAsync(_pager.Current, _currentSection);
    }

    private bool IsForeignBook()
    {
        string lang = _detail.Summary.Language ?? "";
        return !string.IsNullOrWhiteSpace(lang) && !lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryOpenMarkCardAsync(string? markId, ReaderRect? fallbackRect)
    {
        if (string.IsNullOrEmpty(markId)) return false;
        var found = _library.GetMarks(_bookId).FirstOrDefault(m => m.Id == markId);
        if (found is null) return false;
        _editingMark = found;
        _activeSelection = null;
        var rect = fallbackRect
            ?? await _pager.Current.MarkRectAsync(found.Id)
            ?? new ReaderRect { X = 260, Y = 180, Width = 80, Height = 24 };
        if (_disposed) return true;
        ShowSelectionCard(rect, existingMark: found);
        return true;
    }

    private void RestartScrollEdgeCueHideTimer()
    {
        _scrollEdgeCueTimer?.Stop();
        _scrollEdgeCueTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(950) };
        _scrollEdgeCueTimer.Tick += (_, _) =>
        {
            _scrollEdgeCueTimer.Stop();
            if (!_disposed) ScrollEdgeCue.Visibility = Visibility.Collapsed;
        };
        _scrollEdgeCueTimer.Start();
    }

    private async void OnReaderMessage(ReaderMessage msg)
    {
        if (_disposed) return;

        try
        {
            if (msg.Type is "tap" or "selection" or "wheel" or "winKey")
                PaneActivated?.Invoke();

            switch (msg.Type)
            {
                case "ready":
                case "layout":
                    _layout = new SectionLayout(msg.PageCount ?? 1, msg.SentencePages, msg.PageFirstSentence);
                    if (msg.Type == "ready")
                    {
                        uint sectionAtStart = _currentSection;
                        await PushSettingsToReaderAsync(save: false);
                        if (_disposed || _currentSection != sectionAtStart) return;
                        await DecorateSectionHostAsync(_pager.Current, _currentSection);
                        if (_disposed || _currentSection != sectionAtStart) return;

                        if (!string.IsNullOrEmpty(_pendingJumpAnchor))
                        {
                            string anchor = _pendingJumpAnchor;
                            _pendingJumpAnchor = null;
                            await _pager.Current.ScrollToAnchorAsync(anchor, animated: false, align: "top");
                            if (_disposed || _currentSection != sectionAtStart) return;
                            _currentPage = _settings.Mode == LayoutMode.Paginated ? await _pager.Current.CurrentPageAsync() : 0;
                            _currentSentence = _layout?.FirstSentenceOn(_currentPage) ?? 0;
                        }
                        else if (_pendingEndOfSection)
                        {
                            _pendingEndOfSection = false;
                            int pageCount = _layout?.PageCount ?? 1;
                            _currentPage = Math.Max(0, pageCount - 1);
                            await _pager.Current.ScrollToEndOfSectionAsync(pageCount);
                            if (_disposed || _currentSection != sectionAtStart) return;
                            _currentSentence = _layout?.FirstSentenceOn(_currentPage) ?? Math.Max(0, _currentSentences.Count - 1);
                            if (_settings.Mode == LayoutMode.Scrolled) _scrollProgress = 1.0;
                        }
                        else if (_pendingJumpSentence is int jumpS)
                        {
                            _pendingJumpSentence = null;
                            int clampedS = Math.Max(0, jumpS);
                            await _pager.Current.ScrollToSentenceAsync(clampedS, animated: false, align: "top");
                            if (_disposed || _currentSection != sectionAtStart) return;
                            _currentSentence = clampedS;
                            _currentPage = _settings.Mode == LayoutMode.Paginated
                                ? await _pager.Current.CurrentPageAsync()
                                : (_layout?.PageOf(clampedS) ?? 0);
                        }

                        if (!string.IsNullOrWhiteSpace(_pendingSearchHighlight))
                        {
                            string q = _pendingSearchHighlight;
                            _pendingSearchHighlight = null;
                            await _pager.Current.SetHitsAsync(q);
                            if (_disposed || _currentSection != sectionAtStart) return;
                        }

                        if (_pendingPairedActive is { } paired && paired.Section == _currentSection)
                        {
                            _pendingPairedActive = null;
                            _speakingSentence = paired.Sentence;
                            await _pager.Current.SetActiveAsync(paired.Sentence, paired.DurMs, paired.ElapsedMs, paired.Playing, paired.Words);
                            if (_disposed || _currentSection != sectionAtStart) return;
                            if (paired.Sentence >= 0 && paired.Sentence < _currentSentences.Count)
                                TtsLiveSentenceText.Text = _currentSentences[paired.Sentence];
                            UpdateTtsBarVisuals();
                        }

                        if (_autoListenOnReady)
                        {
                            _autoListenOnReady = false;
                            StartListenFromCurrentPage();
                        }

                        _ = _pager.PreloadNeighborsAsync(_currentSection);
                    }
                    UpdateHeaderAndStatus();
                    break;

                case "page":
                    _currentPage = Math.Max(0, msg.Page ?? 0);
                    _currentSentence = _layout?.FirstSentenceOn(_currentPage) ?? _currentSentence;
                    if (_autoTurnTargetPage == _currentPage)
                    {
                        _autoTurnTargetPage = null;
                    }
                    else if (_audio.IsPlaying && _speakingSentence >= 0 && _layout is not null)
                    {
                        _autoTurnTargetPage = null;
                        _ttsFollowPage = _currentPage == _layout.PageOf(_speakingSentence);
                    }
                    UpdateHeaderAndStatus();
                    SaveCurrentProgress();
                    break;

                case "scroll":
                    if (msg.Sentence is int scrollSent && scrollSent >= 0)
                        _currentSentence = scrollSent;
                    if (msg.Progress is double scrollProg)
                        _scrollProgress = Math.Clamp(scrollProg, 0.0, 1.0);
                    if (_layout is not null)
                    {
                        if (msg.Sentence is int sIdx && sIdx >= 0)
                            _currentPage = _layout.PageOf(sIdx);
                        else if (msg.Progress is double sp2)
                            _currentPage = Math.Clamp((int)Math.Round(sp2 * Math.Max(0, _layout.PageCount - 1)), 0, Math.Max(0, _layout.PageCount - 1));
                    }
                    if (ScrollEdgeCue.Visibility == Visibility.Visible)
                    {
                        _scrollEdgeCueTimer?.Stop();
                        ScrollEdgeCue.Visibility = Visibility.Collapsed;
                    }
                    UpdateHeaderAndStatus();
                    SaveCurrentProgress();
                    break;

                case "activePage":
                    if (msg.Page is int pg && pg >= 0 && _settings.Mode == LayoutMode.Paginated && (_audio.IsPlaying || TtsBarHost.Visibility == Visibility.Visible) && _ttsFollowPage && pg != _currentPage)
                    {
                        await JumpToPageAsync(pg);
                    }
                    break;

                case "selection":
                    if (msg.Start is not null && msg.End is not null && !string.IsNullOrWhiteSpace(msg.Text) && msg.Rect is not null)
                    {
                        _editingMark = null;
                        _activeSelection = (msg.Start, msg.End, msg.Text.Trim(), msg.Rect);
                        ShowSelectionCard(msg.Rect, existingMark: null);
                    }
                    else
                    {
                        DismissOverlays();
                    }
                    break;

                case "mark":
                    await TryOpenMarkCardAsync(msg.Mark, msg.Rect);
                    break;

                case "word":
                    if (msg.Word is { } w && !string.IsNullOrWhiteSpace(w.Text))
                    {
                        ShowWordLookupCard(w);
                    }
                    break;

                case "link":
                    if (msg.Link is { } link)
                    {
                        HandleReaderLink(link, msg.Rect);
                    }
                    break;

                case "tap":
                {
                    ReaderRect? tapRect = msg.Rect ?? (msg.X is double rx && msg.Y is double ry
                        ? new ReaderRect { X = rx - 20, Y = ry - 10, Width = 40, Height = 20 }
                        : null);
                    if (msg.Link is { } tapLink)
                    {
                        HandleReaderLink(tapLink, tapRect);
                        break;
                    }
                    if (!string.IsNullOrEmpty(msg.Mark))
                    {
                        var markRect = msg.Rect ?? await _pager.Current.MarkRectAsync(msg.Mark) ?? tapRect;
                        if (_disposed) return;
                        if (await TryOpenMarkCardAsync(msg.Mark, markRect))
                            break;
                    }
                    if (msg.Word is { } tapWord && !string.IsNullOrWhiteSpace(tapWord.Text))
                    {
                        ShowWordLookupCard(tapWord);
                        break;
                    }
                    if (HasOpenOverlay())
                    {
                        DismissOverlays();
                        break;
                    }
                    if (msg.X is double tx && BookStage.ActualWidth > 0 && _settings.Mode == LayoutMode.Paginated)
                    {
                        if (tx < BookStage.ActualWidth * 0.16)
                            await PrevPageOrSectionAsync();
                        else if (tx > BookStage.ActualWidth * 0.84)
                            await NextPageOrSectionAsync();
                    }
                    break;
                }

                case "wheel":
                    if (msg.Ctrl == true && msg.DeltaY is double dy)
                    {
                        // Native Windows Ctrl+Wheel real-time reading font size zoom!
                        AdjustFontSize(dy < 0 ? +1 : -1);
                    }
                    else if (_settings.Mode == LayoutMode.Paginated && msg.DeltaY is double pdy)
                    {
                        _wheelAccum += pdy > 0 ? 1 : -1;
                        if (Math.Abs(_wheelAccum) >= 1)
                        {
                            bool forward = _wheelAccum > 0;
                            _wheelAccum = 0;
                            if (forward) await NextPageOrSectionAsync();
                            else await PrevPageOrSectionAsync();
                        }
                    }
                    break;

                case "edge":
                case "scrollEdge":
                {
                    double? edgeDir = msg.DeltaY ?? msg.Offset;
                    if (edgeDir is double dir && Math.Abs(dir) > 0.001)
                    {
                        if (dir < 0 && _library.PreviousSection(_bookId, _currentSection) is null)
                        {
                            _scrollEdgeCueTimer?.Stop();
                            ScrollEdgeCue.Visibility = Visibility.Collapsed;
                            break;
                        }
                        if (msg.Armed == true)
                        {
                            _scrollEdgeCueTimer?.Stop();
                            ScrollEdgeCue.Visibility = Visibility.Collapsed;
                            if (dir > 0) MoveToNextSection();
                            else MoveToPreviousSection(joinAtEnd: true);
                        }
                        else
                        {
                            ScrollEdgeCueText.Text = L10n.T(dir > 0 ? "继续下拉进入下一章" : "继续上拉返回上一章");
                            ScrollEdgeCue.VerticalAlignment = dir > 0 ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                            ScrollEdgeCue.Margin = dir > 0 ? new Thickness(0, 0, 0, 20) : new Thickness(0, 20, 0, 0);
                            ScrollEdgeCue.Visibility = Visibility.Visible;
                            RestartScrollEdgeCueHideTimer();
                        }
                    }
                    else
                    {
                        _scrollEdgeCueTimer?.Stop();
                        ScrollEdgeCue.Visibility = Visibility.Collapsed;
                    }
                    break;
                }

                case "winKey":
                    HandleForwardedWebViewKey(msg);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ReaderPaneView] OnReaderMessage({msg.Type}) error: {ex}");
        }
    }

    private void HandleForwardedWebViewKey(ReaderMessage msg)
    {
        bool ctrl = msg.Ctrl == true;
        bool shift = msg.Shift == true;
        bool alt = msg.Alt == true;
        string k = msg.Key ?? "";
        string c = msg.Code ?? "";

        if (k == "F11") { _ownerWindow.ToggleFullScreen(); return; }
        if (k == "F3") { StepSearchHit(forward: !shift); return; }
        if (k == "Escape")
        {
            if (HasOpenOverlay())
                DismissOverlays();
            else
                _ownerWindow.SetFullScreen(false);
            return;
        }
        if (k == " " || k == "Spacebar") { HandleSpaceKey(); return; }
        if (!ctrl && !alt && (_activeSelection is not null || _editingMark is not null))
        {
            if (k == "1" || c == "Numpad1") { ApplySelectionStyle(MarkStyle.Yellow); return; }
            if (k == "2" || c == "Numpad2") { ApplySelectionStyle(MarkStyle.Blue); return; }
            if (k == "3" || c == "Numpad3") { ApplySelectionStyle(MarkStyle.Pink); return; }
            if (k == "4" || c == "Numpad4") { ApplySelectionStyle(MarkStyle.Wave); return; }
        }
        if (!ctrl && !alt && k is "ArrowRight" or "PageDown") { _ = NextPageOrSectionAsync(); return; }
        if (!ctrl && !alt && k is "ArrowLeft" or "PageUp") { _ = PrevPageOrSectionAsync(); return; }
        if (!ctrl && !alt && k == "ArrowDown")
        {
            if (_settings.Mode == LayoutMode.Scrolled) _ = ScrollLinesAsync(down: true);
            else _ = NextPageOrSectionAsync();
            return;
        }
        if (!ctrl && !alt && k == "ArrowUp")
        {
            if (_settings.Mode == LayoutMode.Scrolled) _ = ScrollLinesAsync(down: false);
            else _ = PrevPageOrSectionAsync();
            return;
        }
        if (alt && !ctrl && k == "ArrowLeft") { NavigateHistory(isBack: true); return; }
        if (alt && !ctrl && k == "ArrowRight") { NavigateHistory(isBack: false); return; }
        if (ctrl && !alt && shift && (k == "t" || k == "T")) { _ownerWindow.ReopenLastClosedTab(); return; }
        if (ctrl && !alt && k == "Tab")
        {
            if (_ownerWindow.Tabs.Count > 1)
            {
                int cur = _ownerWindow.ActiveTab is not null
                    ? _ownerWindow.Tabs.ToList().IndexOf(_ownerWindow.ActiveTab)
                    : 0;
                if (cur < 0) cur = 0;
                int next = shift
                    ? (cur - 1 + _ownerWindow.Tabs.Count) % _ownerWindow.Tabs.Count
                    : (cur + 1) % _ownerWindow.Tabs.Count;
                _ownerWindow.SelectTab(_ownerWindow.Tabs[next]);
            }
            return;
        }
        if (ctrl && !alt && (k == "f" || k == "F")) { OpenLeftPane("search"); FocusLeftSearchBox(); return; }
        if (ctrl && !alt && (k == "d" || k == "D")) { ToggleBookmarkAtCurrentPosition(); return; }
        if (ctrl && !alt && (k == "g" || k == "G")) { StatusProgress_Click(StatusProgressBtn, new RoutedEventArgs()); return; }
        if (ctrl && !alt && !shift && (k == "w" || k == "W")) { if (_ownerWindow.ActiveTab is { } tab && !tab.IsPinned) _ = _ownerWindow.CloseTabAsync(tab); return; }
        if (ctrl && !alt && (k == "k" || k == "K") && _ownerWindow.WindowXamlRoot is { } xr) { _ = Yuyue.Shelf.QuickSearchDialog.ShowAsync(xr, _library, _bookId); return; }
        if (ctrl && !alt && k == ",") { _ownerWindow.ShowSettings("reading"); return; }
        if (ctrl && !alt && (k == "=" || k == "+")) { AdjustFontSize(+1); return; }
        if (ctrl && !alt && (k == "-" || k == "_")) { AdjustFontSize(-1); return; }
        if (ctrl && !alt && (k == "0" || c is "Digit0" or "Numpad0")) { SetFontSize(18); return; }
        if (ctrl && !alt && !shift)
        {
            char digit = k.Length == 1 && k[0] >= '1' && k[0] <= '9'
                ? k[0]
                : (c.Length == 6 && c.StartsWith("Digit", StringComparison.Ordinal) && c[5] >= '1' && c[5] <= '9'
                    ? c[5]
                    : (c.Length == 7 && c.StartsWith("Numpad", StringComparison.Ordinal) && c[6] >= '1' && c[6] <= '9' ? c[6] : '\0'));
            if (digit >= '1' && digit <= '9')
            {
                int idx = digit - '1';
                if (digit == '9' && _ownerWindow.Tabs.Count > 0) _ownerWindow.SelectTab(_ownerWindow.Tabs[^1]);
                else if (idx < _ownerWindow.Tabs.Count) _ownerWindow.SelectTab(_ownerWindow.Tabs[idx]);
            }
        }
    }

    private Task ScrollLinesAsync(bool down)
    {
        if (!_pager.IsInitialized) return Task.CompletedTask;
        string behavior = (_library.Prefs.ImmersiveNoAnimation || _library.Prefs.GlobalEinkMode) ? "auto" : "smooth";
        int delta = down ? 120 : -120;
        return _pager.Current.CallAsync($"window.scrollBy({{ top: {delta}, behavior: '{behavior}' }});");
    }

    private async Task JumpToPageAsync(int page)
    {
        if (!_pager.IsInitialized || _disposed) return;
        int clamped = Math.Clamp(page, 0, Math.Max(0, (_layout?.PageCount ?? 1) - 1));
        _autoTurnTargetPage = clamped;
        _currentPage = clamped;
        _currentSentence = _layout?.FirstSentenceOn(clamped) ?? _currentSentence;
        await _pager.Current.ScrollToPageAsync(clamped, animated: !_library.Prefs.ImmersiveNoAnimation);
        if (_disposed) return;
        UpdateHeaderAndStatus();
        SaveCurrentProgress();
    }

    private async Task NextPageOrSectionAsync()
    {
        if (!_pager.IsInitialized) return;
        DismissOverlays();
        if (_audio.IsPlaying)
        {
            _autoTurnTargetPage = null;
            _ttsFollowPage = false;
        }
        bool moved = await _pager.Current.NextPageAsync(animated: !_library.Prefs.ImmersiveNoAnimation);
        if (!moved) MoveToNextSection();
    }

    private async Task PrevPageOrSectionAsync()
    {
        if (!_pager.IsInitialized) return;
        DismissOverlays();
        if (_audio.IsPlaying)
        {
            _autoTurnTargetPage = null;
            _ttsFollowPage = false;
        }
        bool moved = await _pager.Current.PrevPageAsync(animated: !_library.Prefs.ImmersiveNoAnimation);
        if (!moved) MoveToPreviousSection(joinAtEnd: true);
    }

    private void MoveToNextSection()
    {
        if (_library.NextSection(_bookId, _currentSection) is uint next)
        {
            _pendingEndOfSection = false;
            LoadSection(next);
        }
        else
        {
            ShowReaderAlert("已读完本书最后一页", "标为读完", () =>
            {
                _library.SetStatus(_bookId, ReadingStatus.Finished);
                App.CurrentApp.NotifyShelfRefresh();
                ShowReaderAlert("已标记为读完");
            });
        }
    }

    private void MoveToPreviousSection(bool joinAtEnd)
    {
        if (_library.PreviousSection(_bookId, _currentSection) is uint prev)
        {
            _pendingEndOfSection = joinAtEnd;
            LoadSection(prev);
        }
    }

    private int CurrentReadingSentence() =>
        _settings.Mode == LayoutMode.Scrolled
            ? Math.Max(0, _currentSentence)
            : Math.Max(0, _layout?.FirstSentenceOn(_currentPage) ?? _currentSentence);

    private void SaveCurrentProgress()
    {
        try
        {
            int sent = CurrentReadingSentence();
            _library.SaveProgress(_bookId, _currentSection, (uint)Math.Max(0, sent));
        }
        catch { }
    }

    // ---------- Overlays: Selection / Word / Footnote / Translation ----------
    private bool HasOpenOverlay() =>
        SelectionCard.Visibility == Visibility.Visible
        || WordCard.Visibility == Visibility.Visible
        || InfoPopupCard.Visibility == Visibility.Visible;

    private void DismissOverlays()
    {
        SelectionCard.Visibility = Visibility.Collapsed;
        WordCard.Visibility = Visibility.Collapsed;
        InfoPopupCard.Visibility = Visibility.Collapsed;
        _activeSelection = null;
        _editingMark = null;
        if (_pager.IsInitialized)
        {
            _ = _pager.Current.ClearSelectionAsync();
            _ = _pager.Current.SetLookingAsync(null, null);
        }
    }

    private void PositionOverlayCard(Border card, ReaderRect rect, double cardWidth, double estHeight)
    {
        double stageW = Math.Max(400, BookStage.ActualWidth);
        double stageH = Math.Max(300, BookStage.ActualHeight);
        double x = Math.Clamp(rect.X + rect.Width / 2.0 - cardWidth / 2.0, 14, Math.Max(14, stageW - cardWidth - 14));
        double y = rect.Y - estHeight - 10;
        if (y < 10) y = Math.Min(stageH - estHeight - 12, rect.Y + rect.Height + 10);
        card.Margin = new Thickness(Math.Round(x), Math.Round(Math.Max(10, y)), 0, 0);
    }

    private void ShowSelectionCard(ReaderRect rect, Mark? existingMark)
    {
        WordCard.Visibility = Visibility.Collapsed;
        InfoPopupCard.Visibility = Visibility.Collapsed;
        if (_pager.IsInitialized)
            _ = _pager.Current.SetLookingAsync(null, null);
        DeleteMarkBtn.Visibility = existingMark is not null ? Visibility.Visible : Visibility.Collapsed;
        if (existingMark is not null && !string.IsNullOrWhiteSpace(existingMark.Note))
        {
            SelectionNotePreviewText.Text = existingMark.Note;
            SelectionNoteSection.Visibility = Visibility.Visible;
        }
        else
        {
            SelectionNoteSection.Visibility = Visibility.Collapsed;
        }
        PositionOverlayCard(SelectionCard, rect, 336, existingMark?.Note is not null ? 140 : 96);
        SelectionCard.Visibility = Visibility.Visible;
    }

    private void ShowWordLookupCard(ReaderWord w)
    {
        SelectionCard.Visibility = Visibility.Collapsed;
        InfoPopupCard.Visibility = Visibility.Collapsed;
        _activeWordMsg = w;
        _activeWordLookup = _library.LookUpWordRich(w.Text);

        string headword = _activeWordLookup?.Headword ?? w.Text;
        WordHeadwordText.Text = headword;
        WordPhoneticText.Text = _activeWordLookup?.Phonetic ?? "";
        WordPhoneticText.Visibility = string.IsNullOrWhiteSpace(WordPhoneticText.Text) ? Visibility.Collapsed : Visibility.Visible;
        WordMeaningText.Text = _activeWordLookup?.Summary ?? L10n.T("本地词典未收录该词，可划选后点击「翻译」或「问书伴」。");
        WordSourceText.Text = _activeWordLookup?.Source ?? "";

        _wordAlreadySaved = _library.GetWords().Any(x => string.Equals(x.Word, headword, StringComparison.OrdinalIgnoreCase));
        WordSaveBtn.Content = L10n.T(_wordAlreadySaved ? "已在生词本" : "加入生词");

        PositionOverlayCard(WordCard, w.Rect, 336, 155);
        WordCard.Visibility = Visibility.Visible;
        _ = _pager.Current.SetLookingAsync(w.Start, w.End);

        if (_library.Prefs.ForeignSpeakOnLookup && !string.IsNullOrWhiteSpace(headword))
            _ = SpeakForeignWordSafeAsync(headword);
    }

    private async Task SpeakForeignWordSafeAsync(string word)
    {
        try { await AudioPlayer.SpeakForeignWordAsync(_library, word, _detail.Summary.Language); }
        catch { }
    }

    private void HandleReaderLink(ReaderLink link, ReaderRect? rect)
    {
        if (!string.IsNullOrWhiteSpace(link.External))
        {
            try { Process.Start(new ProcessStartInfo(link.External) { UseShellExecute = true }); } catch { }
            return;
        }
        if (link.Note is not null && !string.IsNullOrWhiteSpace(link.Target))
        {
            var fn = _library.GetFootnote(_bookId, _currentSection, link.Target);
            if (fn is not null)
            {
                ShowInfoPopup(fn.Index is uint idx ? $"注释 {idx} / {fn.Count}" : "注释", fn.Text, rect ?? new ReaderRect { X = 280, Y = 180, Width = 60, Height = 20 });
                return;
            }
        }
        if (!string.IsNullOrWhiteSpace(link.Target))
        {
            string[] parts = link.Target.Split('#', 2);
            if (uint.TryParse(parts[0], out uint sec))
            {
                PushHistoryPoint();
                string? anchor = parts.Length > 1 ? parts[1] : null;
                if (sec == _currentSection && _pager.IsInitialized)
                {
                    DismissOverlays();
                    if (!string.IsNullOrEmpty(anchor))
                        _ = _pager.Current.ScrollToAnchorAsync(anchor, animated: false, align: "top");
                    else
                        _ = _pager.Current.ScrollToStartOfSectionAsync();
                }
                else
                {
                    _pendingJumpAnchor = anchor;
                    _pendingEndOfSection = false;
                    LoadSection(sec);
                }
            }
        }
    }

    private void ShowInfoPopup(string title, string body, ReaderRect rect)
    {
        SelectionCard.Visibility = Visibility.Collapsed;
        WordCard.Visibility = Visibility.Collapsed;
        if (_pager.IsInitialized)
            _ = _pager.Current.SetLookingAsync(null, null);
        InfoPopupTitle.Text = L10n.T(title);
        InfoPopupBody.Text = body;
        PositionOverlayCard(InfoPopupCard, rect, 356, 165);
        InfoPopupCard.Visibility = Visibility.Visible;
    }

    // Selection card actions
    private void MarkColor_Click(object sender, RoutedEventArgs e)
    {
        string tag = (sender as FrameworkElement)?.Tag as string ?? "yellow";
        MarkStyle style = tag switch
        {
            "blue" => MarkStyle.Blue,
            "pink" => MarkStyle.Pink,
            "wave" => MarkStyle.Wave,
            "underline" => MarkStyle.Underline,
            "dotted" => MarkStyle.Dotted,
            _ => MarkStyle.Yellow,
        };

        ApplySelectionStyle(style);
    }

    private void ApplySelectionStyle(MarkStyle style)
    {
        if (_editingMark is { } existing)
        {
            _library.SetMarkStyle(_bookId, existing.Id, style);
        }
        else if (_activeSelection is { } sel)
        {
            _library.AddMark(
                _bookId, _currentSection,
                new TextPoint((uint)sel.Start.S, (uint)sel.Start.O),
                new TextPoint((uint)sel.End.S, (uint)sel.End.O),
                sel.Text, style);
        }

        DismissOverlays();
        _ = RefreshSectionOverlaysAsync();
        if (_rightPaneOpen && _rightPaneTab == "notes") RenderRightPane();
    }

    private async void AddThought_Click(object sender, RoutedEventArgs e)
    {
        try { await PromptEditThoughtAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[ReaderPaneView] AddThought error: {ex}"); }
    }

    private async void SelectionNote_Pressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try { await PromptEditThoughtAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[ReaderPaneView] SelectionNote error: {ex}"); }
    }

    private async Task PromptEditThoughtAsync()
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var capturedMark = _editingMark;
        var capturedSelection = _activeSelection;
        uint capturedSection = _currentSection;
        string initialNote = capturedMark?.Note ?? "";
        string quote = capturedMark?.Text ?? capturedSelection?.Text ?? "";
        if (string.IsNullOrWhiteSpace(quote)) return;

        bool dark = CurrentPaperChrome?.IsDark ?? ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var noteBox = new TextBox
        {
            Text = initialNote,
            PlaceholderText = L10n.T("写下你对这句话的想法…"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 96,
        };
        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
            Title = L10n.T("记下想法"),
            PrimaryButtonText = L10n.T("保存"),
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 10,
                MinWidth = 360,
                Children =
                {
                    L10n.Skip(new TextBlock { Text = quote, FontSize = 12.5, Opacity = 0.72, MaxLines = 3, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis }),
                    noteBox,
                },
            },
        };
        if (await dlg.ShowSafeAsync(_library.Prefs.Tint) != ContentDialogResult.Primary || _disposed) return;

        string note = (noteBox.Text ?? "").Trim();
        if (capturedMark is { } existing)
        {
            _library.SetMarkNote(_bookId, existing.Id, string.IsNullOrEmpty(note) ? null : note);
        }
        else if (capturedSelection is { } sel)
        {
            _library.AddMark(
                _bookId, capturedSection,
                new TextPoint((uint)sel.Start.S, (uint)sel.Start.O),
                new TextPoint((uint)sel.End.S, (uint)sel.End.O),
                sel.Text, MarkStyle.Yellow, string.IsNullOrEmpty(note) ? null : note);
        }
        DismissOverlays();
        _ = RefreshSectionOverlaysAsync();
        if (_rightPaneOpen && _rightPaneTab == "notes") RenderRightPane();
    }

    private void DeleteOpenMark_Click(object sender, RoutedEventArgs e)
    {
        if (_editingMark is { } mk)
        {
            _library.DeleteMark(_bookId, mk.Id);
            DismissOverlays();
            _ = RefreshSectionOverlaysAsync();
            if (_rightPaneOpen && _rightPaneTab == "notes") RenderRightPane();
        }
    }

    private void SpeakFromSelection_Click(object sender, RoutedEventArgs e)
    {
        int startSent = _editingMark is not null
            ? (int)_editingMark.Start.Sentence
            : (_activeSelection?.Start.S ?? 0);
        DismissOverlays();
        StartListenFromSentence(startSent);
    }

    private void AskSelection_Click(object sender, RoutedEventArgs e)
    {
        string text = _editingMark?.Text ?? _activeSelection?.Text ?? "";
        DismissOverlays();
        if (!string.IsNullOrWhiteSpace(text))
        {
            OpenRightPane("companion");
            _ = SendCompanionQuestionAsync($"请结合前文解读这段话：\n「{text}」");
        }
    }

    private async void TranslateSelection_Click(object sender, RoutedEventArgs e)
    {
        string text = _editingMark?.Text ?? _activeSelection?.Text ?? "";
        var rect = _activeSelection?.Rect ?? new ReaderRect { X = 260, Y = 180, Width = 80, Height = 24 };
        DismissOverlays();
        if (string.IsNullOrWhiteSpace(text)) return;

        ShowInfoPopup("翻译", L10n.T("正在翻译…"), rect);
        try
        {
            string res = await _library.Companion.ChatAsync(
                text,
                "你是一位信达雅的文学翻译。直接给出自然流畅的中文译文，不要额外寒暄。");
            if (_disposed) return;
            InfoPopupBody.Text = res.Trim();
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            InfoPopupBody.Text = $"翻译失败：{CompanionClient.FormatErrorMessage(ex)}";
        }
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        string text = _editingMark?.Text ?? _activeSelection?.Text ?? "";
        if (!string.IsNullOrEmpty(text))
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
            ShowReaderAlert("已复制选定文字");
        }
        DismissOverlays();
    }

    private async void WordSpeak_Click(object sender, RoutedEventArgs e)
    {
        string word = WordHeadwordText.Text;
        if (!string.IsNullOrWhiteSpace(word))
            await SpeakForeignWordSafeAsync(word);
    }

    private void WordSave_Click(object sender, RoutedEventArgs e)
    {
        if (_activeWordMsg is null) return;
        string hw = WordHeadwordText.Text;
        if (!_wordAlreadySaved)
        {
            _library.AddWord(
                hw,
                _activeWordLookup?.Phonetic,
                _activeWordLookup?.Summary,
                _bookId,
                _currentSection,
                (uint)Math.Max(0, _activeWordMsg.Start.S),
                _activeWordMsg.Sentence);
            _wordAlreadySaved = true;
            WordSaveBtn.Content = L10n.T("✓ 已加入");
            _ = RefreshSectionOverlaysAsync();
        }
    }

    private void CloseInfoPopup_Click(object sender, RoutedEventArgs e) => InfoPopupCard.Visibility = Visibility.Collapsed;
}
