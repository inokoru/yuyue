using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Pdf;
using Yuyue.Speech;
using Yuyue.UI.Shell;

namespace Yuyue.UI.Pdf;

/// <summary>
/// Brand-new native Windows 11 Edge-inspired PDF reader tab (<see cref="ITabContent"/> &amp; <see cref="IMiniPlayerSource"/>).
/// Renders crisp WinRT <see cref="PdfDocument"/> pages with continuous/single/two-page layouts, Ctrl+Wheel zoom,
/// warm night mode, PDF outline &amp; full-text search, annotations pane, and integrated read-aloud with Mini Player.
/// </summary>
public sealed partial class PdfTabView : UserControl, ITabContent, IMiniPlayerSource
{
    private MainWindow _ownerWindow;
    private readonly LibraryService _library;
    private readonly string _bookId;
    private readonly BookDetail _detail;
    private readonly AudioPlayer _audio;
    private readonly DispatcherTimer _scrollRenderTimer;
    private readonly DispatcherTimer _progressSaveTimer;

    private string? _pdfPath;
    private PdfDocument? _pdfDoc;
    private int _pageCount;
    private int _currentPage;
    private double _zoom = 1.0;
    private string _layoutMode; // scroll | single | double
    private bool _nightMode;
    private bool _leftOpen;
    private string _leftTab = "outline"; // outline | search
    private bool _rightOpen;
    private string _searchQuery = "";
    private IReadOnlyList<string> _currentPageSentences = [];
    private int _speakingIdx = -1;
    private bool _disposed;

    // Continuous scroll virtualization & render concurrency state
    private double[] _pageAspects = [];
    private readonly List<Border> _scrollPageContainers = [];
    private double[] _scrollPageHeights = [];
    private readonly HashSet<int> _renderedScrollPages = [];
    private readonly HashSet<int> _renderingScrollPages = [];
    private double _scrollDisplayWidth;
    private int _renderSeq;
    private int _scrollRenderGen;
    private bool _suppressScrollTracking;
    private long _suppressScrollUntilTick;

    private bool IsEffectiveDark => _nightMode || ThemeHelper.IsDark(_library.Prefs.ToElementTheme());

    private static string NormalizeLayoutMode(string? mode) => mode switch
    {
        "single" => "single",
        "double" => "double",
        "scroll" or "continuous" => "scroll",
        _ => "scroll",
    };

    private static int ResolveMarkPageIndex(Mark mk) =>
        (int)(mk.Start.Sentence > 0 ? mk.Start.Sentence : mk.Section);

    // ITabContent
    public string TabId { get; }
    public string TabTitle => _detail.Summary.Title;
    public string? BookId => _bookId;
    public string TabKind => "pdf";
    public string TabIconName => "doc-lines";
    public bool IsPinned => false;
    public bool IsAudioPlaying => !_disposed && _audio.IsPlaying;
    public PaperChromeTheme? ActivePaperChrome => _activePaperChrome;
    private PaperChromeTheme? _activePaperChrome;
    public UIElement ViewElement => this;
    public event Action? TabMetaChanged;

    // IMiniPlayerSource
    string IMiniPlayerSource.SourceId => $"pdf:{_bookId}";
    string IMiniPlayerSource.Title => _detail.Summary.Title;
    string IMiniPlayerSource.Subtitle => _speakingIdx >= 0 && _speakingIdx < _currentPageSentences.Count
        ? _currentPageSentences[_speakingIdx]
        : $"第 {_currentPage + 1} / {Math.Max(1, _pageCount)} 页";
    string? IMiniPlayerSource.CoverPath => _detail.Summary.CoverPath;
    bool IMiniPlayerSource.IsPlaying => !_disposed && _audio.IsPlaying;
    double IMiniPlayerSource.ProgressRatio => _pageCount > 0 ? Math.Clamp((double)(_currentPage + 1) / _pageCount, 0, 1) : 0;
    double IMiniPlayerSource.PlaybackRate => _audio.Rate;
    public event Action? StateChanged;

    public PdfTabView(MainWindow ownerWindow, LibraryService library, string bookId, bool autoListen = false, uint? initialPage = null)
    {
        _ownerWindow = ownerWindow;
        _library = library;
        _bookId = bookId;
        TabId = $"pdf:{bookId}:{Guid.NewGuid():N}";
        _detail = _library.GetBookDetail(bookId);

        InitializeComponent();

        string rawLayout = _library.Prefs.PdfBookLayouts.TryGetValue(bookId, out var savedLay) ? savedLay : _library.Prefs.PdfLayout;
        _layoutMode = NormalizeLayoutMode(rawLayout);
        _nightMode = _library.Prefs.PdfNightBooks.Contains(bookId) || _library.Prefs.PdfNight;
        _currentPage = initialPage is uint p0
            ? (int)p0
            : (int)(_library.GetProgress(bookId)?.Position.Sentence ?? 0);

        _scrollRenderTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(90) };
        _scrollRenderTimer.Tick += (_, _) =>
        {
            _scrollRenderTimer.Stop();
            if (_disposed || _layoutMode != "scroll") return;
            _ = EnsureScrollViewportRenderedAsync(_renderSeq);
        };
        _progressSaveTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(450) };
        _progressSaveTimer.Tick += (_, _) =>
        {
            _progressSaveTimer.Stop();
            if (!_disposed) SaveProgress();
        };

        _audio = new AudioPlayer(_library);
        _audio.SetBookContext(_bookId, _detail.Summary.Language);
        _audio.SetVoice(_library.Prefs.GetVoiceForLanguage(_detail.Summary.Language));
        _audio.SetRate(_library.Prefs.SpeechRate);
        App.CurrentApp.BindAudioClaimOwner(_audio, this);

        _audio.SentenceStarted += OnAudioSentenceStarted;
        _audio.PlayStateChanged += OnAudioPlayStateChanged;
        _audio.SectionFinished += OnAudioSectionFinished;

        LeftSplitter.DragDelta += dx =>
        {
            double cur = LeftCol.Width.Value > 0 ? LeftCol.Width.Value : 240;
            LeftCol.Width = new GridLength(Math.Clamp(cur + dx, 180, 380));
        };
        RightSplitter.DragDelta += dx =>
        {
            double cur = RightCol.Width.Value > 0 ? RightCol.Width.Value : 300;
            RightCol.Width = new GridLength(Math.Clamp(cur - dx, 220, 420));
        };

        TitleText.Text = _detail.Summary.Title;
        L10n.Skip(TitleText);
        L10n.Skip(TtsLineText);
        L10n.Skip(PdfSelNotePreviewText);
        L10n.Skip(PdfInfoPopupBody);
        ApplyAppearance();
        _ = LoadPdfAsync(autoListen);
    }

    private void OnAudioSentenceStarted(int idx, int _, bool __)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            _speakingIdx = idx;
            if (idx >= 0 && idx < _currentPageSentences.Count)
                TtsLineText.Text = _currentPageSentences[idx];
            if (_listeningPage >= 0) RefreshPageOverlay(_listeningPage);
            StateChanged?.Invoke();
        });
    }

    private void OnAudioPlayStateChanged(bool playing)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            if (playing)
            {
                App.CurrentApp.ClaimAudioPlayback(this, () => { if (!_disposed) _audio.Pause(); });
                App.CurrentApp.SetActiveMiniPlayerSource(this);
            }
            _library.SystemIntegration.SetAudioPlaying(this, playing, _detail.Summary.Title, ToggleListen);
            UpdateTtsIcons();
            TabMetaChanged?.Invoke();
            StateChanged?.Invoke();
        });
    }

    private void OnAudioSectionFinished()
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            if (_currentPage + 1 < _pageCount)
            {
                JumpToPage(_currentPage + 1);
                StartReadCurrentPage();
            }
        });
    }

    private async Task LoadPdfAsync(bool autoListen)
    {
        try
        {
            _pdfPath = _library.TryOriginalFile(_bookId);
            if (string.IsNullOrEmpty(_pdfPath) || !File.Exists(_pdfPath))
            {
                if (_disposed) return;
                ErrorText.Text = L10n.T("未找到该 PDF 原始文件，请重新导入。");
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            var sf = await StorageFile.GetFileFromPathAsync(_pdfPath);
            if (_disposed) return;
            _pdfDoc = await PdfDocument.LoadFromFileAsync(sf);
            if (_disposed) return;
            _pageCount = (int)_pdfDoc.PageCount;
            _currentPage = Math.Clamp(_currentPage, 0, Math.Max(0, _pageCount - 1));
            _pageAspects = PdfInfoReader.GetPageAspectRatios(_pdfDoc, _pdfPath);
            await RenderVisiblePagesAsync();
            if (_disposed) return;
            UpdateHeaderLabels();
            if (autoListen) StartReadCurrentPage();
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            ErrorText.Text = $"{L10n.T("无法打开 PDF：")}{ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private double GetPageAspect(int pageIdx) =>
        (pageIdx >= 0 && pageIdx < _pageAspects.Length && _pageAspects[pageIdx] > 0)
            ? _pageAspects[pageIdx]
            : 1.414;

    private TextBlock MakeScrollPlaceholder(int pageIdx, SolidColorBrush fgBrush) =>
        new()
        {
            Text = $"{pageIdx + 1}",
            FontSize = 13,
            Foreground = fgBrush,
            Opacity = 0.45,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

    private double GetScrollPageOffsetY(int pageIdx)
    {
        double y = PagesStack.Padding.Top;
        double spacing = PagesStack.Spacing;
        int limit = Math.Min(pageIdx, _scrollPageHeights.Length);
        for (int i = 0; i < limit; i++)
            y += _scrollPageHeights[i] + spacing;
        return y;
    }

    private void ScrollToPageInScrollMode(int pageIdx)
    {
        if (_disposed || _scrollPageContainers.Count == 0) return;
        double targetY = Math.Max(0, GetScrollPageOffsetY(pageIdx) - 12);
        PagesStack.UpdateLayout();
        PdfScroller.ChangeView(null, targetY, null, disableAnimation: true);
        if (targetY > 1 && PdfScroller.ScrollableHeight < targetY - 2)
        {
            int capturedPage = pageIdx;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_disposed || _layoutMode != "scroll" || _currentPage != capturedPage) return;
                _suppressScrollTracking = true;
                _suppressScrollUntilTick = Environment.TickCount64 + 350;
                double retryY = Math.Max(0, GetScrollPageOffsetY(capturedPage) - 12);
                PdfScroller.ChangeView(null, retryY, null, disableAnimation: true);
            });
        }
    }

    private int FindVisiblePageIndex()
    {
        if (_scrollPageHeights.Length == 0) return 0;
        double offsetY = PdfScroller.VerticalOffset;
        double viewportH = PdfScroller.ViewportHeight > 0 ? PdfScroller.ViewportHeight : 600;
        if (offsetY <= 1) return 0;
        if (PdfScroller.ScrollableHeight > 0 && offsetY >= PdfScroller.ScrollableHeight - 2)
            return _scrollPageHeights.Length - 1;

        double viewTop = offsetY;
        double viewBottom = offsetY + viewportH;
        double spacing = PagesStack.Spacing;
        double y = PagesStack.Padding.Top;

        int bestPage = _currentPage;
        double bestOverlap = -1;
        for (int i = 0; i < _scrollPageHeights.Length; i++)
        {
            double h = _scrollPageHeights[i];
            double top = y;
            double bottom = y + h;
            double overlap = Math.Min(viewBottom, bottom) - Math.Max(viewTop, top);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                bestPage = i;
            }
            if (top > viewBottom) break;
            y = bottom + spacing;
        }
        return Math.Clamp(bestPage, 0, _scrollPageHeights.Length - 1);
    }

    private (int First, int Last) FindVisiblePageRange(int? centerPageOverride = null)
    {
        if (_scrollPageHeights.Length == 0) return (0, -1);
        if (centerPageOverride is int cp)
        {
            int f = Math.Clamp(cp - 2, 0, _scrollPageHeights.Length - 1);
            int l = Math.Clamp(cp + 4, 0, _scrollPageHeights.Length - 1);
            return (f, l);
        }

        double offsetY = PdfScroller.VerticalOffset;
        double viewportH = PdfScroller.ViewportHeight > 0 ? PdfScroller.ViewportHeight : 700;
        double preloadTop = Math.Max(0, offsetY - viewportH * 1.2);
        double preloadBottom = offsetY + viewportH * 2.2;
        double spacing = PagesStack.Spacing;
        double y = PagesStack.Padding.Top;
        int first = -1, last = -1;
        for (int i = 0; i < _scrollPageHeights.Length; i++)
        {
            double top = y;
            double bottom = y + _scrollPageHeights[i];
            if (bottom >= preloadTop && top <= preloadBottom)
            {
                if (first < 0) first = i;
                last = i;
            }
            if (top > preloadBottom) break;
            y = bottom + spacing;
        }
        if (first < 0)
        {
            first = Math.Clamp(_currentPage - 2, 0, _scrollPageHeights.Length - 1);
            last = Math.Clamp(_currentPage + 4, 0, _scrollPageHeights.Length - 1);
        }
        else
        {
            first = Math.Max(0, first - 1);
            last = Math.Min(_scrollPageHeights.Length - 1, last + 2);
        }
        return (first, last);
    }

    private async Task RenderVisiblePagesAsync()
    {
        if (_pdfDoc is null || _disposed || _pageCount <= 0) return;
        int seq = ++_renderSeq;
        _scrollRenderGen++;
        _scrollRenderTimer.Stop();

        PagesStack.Children.Clear();
        _scrollPageContainers.Clear();
        _renderedScrollPages.Clear();
        _renderingScrollPages.Clear();
        _pageOverlays.Clear();
        HidePdfSelectionCard();
        _scrollPageHeights = [];

        double baseW = Math.Clamp((CanvasHost.ActualWidth > 0 ? CanvasHost.ActualWidth : 900) - 80, 420, 960) * _zoom;
        if (_layoutMode == "double")
        {
            double halfW = Math.Max(280, (baseW - 14) / 2.0);
            var firstPage = await RenderSinglePageBorderAsync(_currentPage, halfW);
            if (_disposed || seq != _renderSeq) return;
            Border? secondPage = null;
            if (_currentPage + 1 < _pageCount)
            {
                secondPage = await RenderSinglePageBorderAsync(_currentPage + 1, halfW);
                if (_disposed || seq != _renderSeq) return;
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(firstPage);
            if (secondPage is not null) row.Children.Add(secondPage);
            PagesStack.Children.Add(row);
            PdfScroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        else if (_layoutMode == "single")
        {
            var singlePage = await RenderSinglePageBorderAsync(_currentPage, baseW);
            if (_disposed || seq != _renderSeq) return;
            PagesStack.Children.Add(singlePage);
            PdfScroller.ChangeView(null, 0, null, disableAnimation: true);
        }
        else
        {
            // Full-document continuous scroll with lightweight placeholders for ALL pages + lazy viewport rendering
            bool dark = IsEffectiveDark;
            var pal = ThemeHelper.Palette(dark);
            var pageBg = ThemeHelper.Brush(_nightMode ? Color.FromArgb(255, 28, 27, 25) : Colors.White);
            var lineBrush = ThemeHelper.Brush(pal.Line);
            var mutedBrush = ThemeHelper.Brush(pal.T3);

            _scrollDisplayWidth = baseW;
            _scrollPageHeights = new double[_pageCount];

            for (int i = 0; i < _pageCount; i++)
            {
                double displayH = Math.Round(baseW * GetPageAspect(i));
                double outerH = displayH + 2;
                _scrollPageHeights[i] = outerH;
                var border = new Border
                {
                    Width = baseW + 2,
                    Height = outerH,
                    CornerRadius = new CornerRadius(4),
                    Background = pageBg,
                    BorderBrush = lineBrush,
                    BorderThickness = new Thickness(1),
                    Tag = i,
                    Child = MakeScrollPlaceholder(i, mutedBrush),
                };
                _scrollPageContainers.Add(border);
                PagesStack.Children.Add(border);
            }

            _suppressScrollTracking = true;
            _suppressScrollUntilTick = Environment.TickCount64 + 400;
            ScrollToPageInScrollMode(_currentPage);
            SaveProgress();
            UpdateHeaderLabels();
            await EnsureScrollViewportRenderedAsync(seq, centerPageOverride: _currentPage);
            return;
        }

        SaveProgress();
        UpdateHeaderLabels();
    }

    private async Task EnsureScrollViewportRenderedAsync(int seq, int? centerPageOverride = null)
    {
        if (_disposed || _pdfDoc is null || seq != _renderSeq || _layoutMode != "scroll" || _scrollPageContainers.Count == 0)
            return;

        int gen = ++_scrollRenderGen;
        var (first, last) = FindVisiblePageRange(centerPageOverride);
        if (last < first) return;

        // Evict far-away bitmaps so long PDFs stay lean on memory
        int keepMin = Math.Max(0, first - 6);
        int keepMax = Math.Min(_scrollPageContainers.Count - 1, last + 6);
        List<int>? toEvict = null;
        foreach (int renderedIdx in _renderedScrollPages)
        {
            if ((renderedIdx < keepMin || renderedIdx > keepMax) && !_renderingScrollPages.Contains(renderedIdx))
            {
                toEvict ??= [];
                toEvict.Add(renderedIdx);
            }
        }
        if (toEvict is not null)
        {
            var mutedBrush = ThemeHelper.Brush(ThemeHelper.Palette(IsEffectiveDark).T3);
            foreach (int evIdx in toEvict)
            {
                _renderedScrollPages.Remove(evIdx);
                _pageOverlays.Remove(evIdx);
                if (evIdx >= 0 && evIdx < _scrollPageContainers.Count)
                    _scrollPageContainers[evIdx].Child = MakeScrollPlaceholder(evIdx, mutedBrush);
            }
        }

        int center = centerPageOverride ?? _currentPage;
        var order = Enumerable.Range(first, last - first + 1)
            .OrderBy(i => Math.Abs(i - center))
            .ToArray();

        foreach (int pageIdx in order)
        {
            if (_disposed || seq != _renderSeq || gen != _scrollRenderGen || _layoutMode != "scroll")
                return;
            if (_renderedScrollPages.Contains(pageIdx) || _renderingScrollPages.Contains(pageIdx))
                continue;
            await RenderIntoScrollBorderAsync(pageIdx, seq);
        }
    }

    private async Task RenderIntoScrollBorderAsync(int pageIdx, int seq)
    {
        if (_disposed || _pdfDoc is null || seq != _renderSeq || pageIdx < 0 || pageIdx >= _scrollPageContainers.Count)
            return;
        if (!_renderingScrollPages.Add(pageIdx))
            return;

        try
        {
            double displayW = _scrollDisplayWidth;
            using var page = _pdfDoc.GetPage((uint)pageIdx);
            double aspect = page.Size.Width > 0 && page.Size.Height > 0
                ? Math.Clamp(page.Size.Height / page.Size.Width, 0.25, 4.0)
                : GetPageAspect(pageIdx);
            double displayH = Math.Round(displayW * aspect);
            using var stream = new InMemoryRandomAccessStream();
            var opts = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Clamp(Math.Round(displayW * 1.5), 400, 2400),
                BackgroundColor = _nightMode ? Color.FromArgb(255, 232, 228, 218) : Colors.White,
            };
            await page.RenderToStreamAsync(stream, opts);
            if (_disposed || seq != _renderSeq) return;

            stream.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            if (_disposed || seq != _renderSeq || pageIdx >= _scrollPageContainers.Count) return;

            var border = _scrollPageContainers[pageIdx];
            double outerH = displayH + 2;
            if (Math.Abs(outerH - _scrollPageHeights[pageIdx]) > 1.0)
            {
                _scrollPageHeights[pageIdx] = outerH;
                border.Height = outerH;
            }
            border.Child = BuildInteractivePageSurface(pageIdx, bmp, displayW, displayH);
            _renderedScrollPages.Add(pageIdx);
        }
        catch { }
        finally
        {
            _renderingScrollPages.Remove(pageIdx);
        }
    }

    private async Task<Border> RenderSinglePageBorderAsync(int pageIdx, double displayW)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = ThemeHelper.Brush(_nightMode ? Color.FromArgb(255, 28, 27, 25) : Colors.White),
            BorderBrush = ThemeHelper.Brush(ThemeHelper.Palette(IsEffectiveDark).Line),
            BorderThickness = new Thickness(1),
            Tag = pageIdx,
        };
        try
        {
            using var page = _pdfDoc!.GetPage((uint)pageIdx);
            double aspect = page.Size.Height / Math.Max(1, page.Size.Width);
            double displayH = Math.Round(displayW * aspect);
            using var stream = new InMemoryRandomAccessStream();
            var opts = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Clamp(Math.Round(displayW * 1.5), 400, 2400),
                BackgroundColor = _nightMode ? Color.FromArgb(255, 232, 228, 218) : Colors.White,
            };
            await page.RenderToStreamAsync(stream, opts);
            stream.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            border.Child = BuildInteractivePageSurface(pageIdx, bmp, displayW, displayH);
        }
        catch { }
        return border;
    }

    public void JumpToPage(int pageZeroBased)
    {
        if (_disposed || _pageCount <= 0) return;
        _currentPage = Math.Clamp(pageZeroBased, 0, _pageCount - 1);
        if (_layoutMode == "scroll" && _scrollPageContainers.Count == _pageCount && _pdfDoc is not null)
        {
            _suppressScrollTracking = true;
            _suppressScrollUntilTick = Environment.TickCount64 + 400;
            ScrollToPageInScrollMode(_currentPage);
            SaveProgress();
            UpdateHeaderLabels();
            StateChanged?.Invoke();
            _ = EnsureScrollViewportRenderedAsync(_renderSeq, centerPageOverride: _currentPage);
            return;
        }
        _ = RenderVisiblePagesAsync();
    }

    public void TriggerListen() => StartReadCurrentPage();

    private void StartReadCurrentPage(int startSentenceIndex = 0)
    {
        if (_disposed || string.IsNullOrEmpty(_pdfPath)) return;
        try
        {
            var layout = GetOrLoadPageLayoutSync(_currentPage);
            var spanSents = YuyueCoreMethods.PageSentences(layout.Text);
            var sents = spanSents.Select(s => s.Text.Trim()).Where(s => s.Length > 0).ToArray();
            if (sents.Length == 0) return;
            _listeningPage = _currentPage;
            _activePageSentences = spanSents;
            _currentPageSentences = sents;
            TtsBarHost.Visibility = Visibility.Visible;
            _audio.SetMetadata(_detail.Summary.Title, $"第 {_currentPage + 1} 页", _detail.Summary.CoverPath);
            _audio.Start(sents, Math.Clamp(startSentenceIndex, 0, sents.Length - 1));
            App.CurrentApp.SetActiveMiniPlayerSource(this);
            UpdateTtsIcons();
            RefreshPageOverlay(_listeningPage);
        }
        catch { }
    }

    private void ToggleListen()
    {
        if (_disposed) return;
        if (TtsBarHost.Visibility != Visibility.Visible)
        {
            StartReadCurrentPage();
            return;
        }
        if (_audio.IsPlaying) _audio.Pause();
        else if (_speakingIdx >= 0) _audio.Resume();
        else StartReadCurrentPage();
    }

    private void SaveProgress()
    {
        try { _library.SaveProgress(_bookId, 0, (uint)Math.Max(0, _currentPage)); } catch { }
    }

    private void UpdateHeaderLabels()
    {
        PageCounterText.Text = $"{_currentPage + 1} / {Math.Max(1, _pageCount)}";
        ZoomLabelText.Text = $"{Math.Round(_zoom * 100)}%";
        LayoutModeBtn.Content = _layoutMode switch
        {
            "single" => L10n.T("单页"),
            "double" => L10n.T("双页"),
            _ => L10n.T("连续滚动"),
        };
        double pct = _pageCount > 0 ? (_currentPage + 1.0) / _pageCount * 100.0 : 0;
        StatusLeftText.Text = L10n.T($"第 {_currentPage + 1} / {Math.Max(1, _pageCount)} 页 · {pct:0.#}%");
    }

    private void UpdateTtsIcons()
    {
        if (_disposed) return;
        bool dark = IsEffectiveDark;
        var p = ThemeHelper.Palette(dark);
        TtsPrevBtn.Content = YYIcons.Make("chevron-left", 14, ThemeHelper.Brush(p.T1));
        TtsNextBtn.Content = YYIcons.Make("chevron-right", 14, ThemeHelper.Brush(p.T1));
        TtsPlayBtn.Content = YYIcons.Make(_audio.IsPlaying ? "pause" : "play", 12, ThemeHelper.Brush(Colors.White), filled: true);
        TtsPipBtn.Content = YYIcons.Make("pip", 13.5, ThemeHelper.Brush(p.T2));
        TtsStopBtn.Content = YYIcons.Make("xmark", 12, ThemeHelper.Brush(p.T2));
        TtsRateBtn.Content = $"{_audio.Rate:0.##}×";
    }

    public void ApplyAppearance()
    {
        if (_disposed) return;
        bool dark = IsEffectiveDark;
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        // Unified warm reading paper chrome across tabstrip, toolbar, backdrop, and statusbar (eliminates 4-shade mismatch)
        Color chromeBg = dark ? Color.FromArgb(255, 29, 28, 26) : Color.FromArgb(255, 246, 243, 237);
        Color tabStripBg = dark ? Color.FromArgb(255, 22, 21, 20) : Color.FromArgb(255, 239, 235, 228);
        Color backdropBg = dark ? Color.FromArgb(255, 20, 19, 18) : Color.FromArgb(255, 237, 233, 226);
        Color line = dark ? Color.FromArgb(255, 48, 45, 41) : Color.FromArgb(255, 226, 221, 213);

        _activePaperChrome = new PaperChromeTheme(chromeBg, tabStripBg, p.T1, p.T2, line, dark);
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        ThemeHelper.ApplyControlThemeResources(RootGrid.Resources, dark, _library.Prefs.Tint);
        RootGrid.Background = ThemeHelper.Brush(chromeBg);
        TopBar.Background = ThemeHelper.Brush(chromeBg);
        TopBar.BorderBrush = ThemeHelper.Brush(line);
        StatusBarHost.Background = ThemeHelper.Brush(chromeBg);
        StatusBarHost.BorderBrush = ThemeHelper.Brush(line);
        TtsBarHost.Background = ThemeHelper.Brush(chromeBg);
        TtsBarHost.BorderBrush = ThemeHelper.Brush(line);
        CanvasHost.Background = ThemeHelper.Brush(backdropBg);

        LeftPaneBtn.Content = YYIcons.Make("panel-left", 14, ThemeHelper.Brush(_leftOpen ? tc.Accent : p.T2));
        PrevPageBtn.Content = YYIcons.Make("chevron-left", 14, ThemeHelper.Brush(p.T1));
        NextPageBtn.Content = YYIcons.Make("chevron-right", 14, ThemeHelper.Brush(p.T1));
        NightBtn.Content = YYIcons.Make("moon", 14, ThemeHelper.Brush(_nightMode ? tc.Accent : p.T2));
        ListenBtn.Content = YYIcons.Make("headphones", 14, ThemeHelper.Brush(_audio.IsPlaying ? tc.Accent : p.T2));
        NotesPaneBtn.Content = YYIcons.Make("square.and.pencil", 14, ThemeHelper.Brush(_rightOpen ? tc.Accent : p.T2));
        DetachBtn.Content = YYIcons.Make("detach", 13.5, ThemeHelper.Brush(p.T2));
        PdfSelDeleteBtn.Content = YYIcons.Make("trash", 13, ThemeHelper.Brush(p.Danger));

        L10n.ApplyTree(RootGrid);
        UpdateHeaderLabels();
        UpdateTtsIcons();
        RefreshAllVisibleOverlays();
        if (_leftOpen) RenderLeftPane();
        if (_rightOpen) RenderRightNotesPane();
        TabMetaChanged?.Invoke();
    }

    private void RenderLeftPane()
    {
        bool dark = IsEffectiveDark;
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        LeftTabsBar.Children.Clear();
        foreach (var (id, lbl) in new[] { ("outline", "目录"), ("search", "搜索") })
        {
            bool on = _leftTab == id;
            string cap = id;
            var b = new Button
            {
                Content = L10n.T(lbl),
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(9, 0, 9, 0),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Background = on ? ThemeHelper.Brush(tc.Soft) : new SolidColorBrush(Colors.Transparent),
                Foreground = ThemeHelper.Brush(on ? tc.Accent : p.T2),
                UseSystemFocusVisuals = false,
            };
            b.Click += (_, _) => { _leftTab = cap; RenderLeftPane(); };
            LeftTabsBar.Children.Add(b);
        }

        LeftPaneBody.Children.Clear();
        if (_leftTab == "search")
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var box = new TextBox
            {
                Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
                Text = _searchQuery,
                PlaceholderText = L10n.T("在这个 PDF 里找"),
                Margin = new Thickness(10, 0, 10, 8),
                Height = 30,
                MinHeight = 30,
                FontSize = 12,
                Padding = new Thickness(9, 0, 9, 0),
            };
            var resStack = new StackPanel { Spacing = 6, Padding = new Thickness(10, 0, 10, 14) };

            void RunSearch()
            {
                _searchQuery = (box.Text ?? "").Trim();
                resStack.Children.Clear();
                if (string.IsNullOrEmpty(_searchQuery) || string.IsNullOrEmpty(_pdfPath)) return;
                bool curDark = IsEffectiveDark;
                var curP = ThemeHelper.Palette(curDark);
                foreach (var hit in PdfInfoReader.SearchText(_pdfPath, _searchQuery).Take(80))
                {
                    int pg = hit.PageZeroBased;
                    int charOff = hit.CharOffset;
                    int matchLen = hit.MatchLen;
                    var btn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Padding = new Thickness(8, 6, 8, 6),
                        CornerRadius = new CornerRadius(6),
                        Background = ThemeHelper.Brush(curP.Card),
                        BorderBrush = ThemeHelper.Brush(curP.Line),
                        BorderThickness = new Thickness(1),
                        Content = L10n.Skip(new TextBlock { Text = $"{L10n.T($"第 {pg + 1} 页")} · {hit.Snippet}", FontSize = 11.5, Foreground = ThemeHelper.Brush(curP.T1), TextWrapping = TextWrapping.Wrap, MaxLines = 3 }),
                    };
                    btn.Click += (_, _) =>
                    {
                        int prevHitPg = _activeSearchHighlight?.Page ?? -1;
                        _activeSearchHighlight = (pg, charOff, matchLen);
                        if (prevHitPg >= 0 && prevHitPg != pg) RefreshPageOverlay(prevHitPg);
                        JumpToPage(pg);
                        RefreshPageOverlay(pg);
                    };
                    resStack.Children.Add(btn);
                }
            }
            box.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { RunSearch(); e.Handled = true; } };
            Grid.SetRow(box, 0);
            var sv = new ScrollViewer { Content = resStack };
            Grid.SetRow(sv, 1);
            g.Children.Add(box);
            g.Children.Add(sv);
            LeftPaneBody.Children.Add(g);
        }
        else
        {
            var stack = new StackPanel { Spacing = 2, Padding = new Thickness(8, 0, 8, 16) };
            var outline = !string.IsNullOrEmpty(_pdfPath) ? PdfInfoReader.ExtractOutline(_pdfPath) : [];
            if (outline.Count == 0)
            {
                for (int i = 0; i < Math.Min(_pageCount, 200); i++)
                {
                    int pg = i;
                    var b = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Content = L10n.T($"第 {pg + 1} 页"),
                        Background = pg == _currentPage ? ThemeHelper.Brush(tc.Soft) : new SolidColorBrush(Colors.Transparent),
                        Foreground = ThemeHelper.Brush(pg == _currentPage ? tc.Accent : p.T1),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(5),
                    };
                    b.Click += (_, _) => JumpToPage(pg);
                    stack.Children.Add(b);
                }
            }
            else
            {
                foreach (var (title, pg, lvl) in outline)
                {
                    int pCap = pg;
                    var b = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Padding = new Thickness(10 + Math.Min(3, lvl) * 12, 6, 8, 6),
                        Background = pCap == _currentPage ? ThemeHelper.Brush(tc.Soft) : new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(5),
                        Content = L10n.Skip(new TextBlock { Text = title, FontSize = 12, Foreground = ThemeHelper.Brush(pCap == _currentPage ? tc.Accent : p.T1), TextTrimming = TextTrimming.CharacterEllipsis }),
                    };
                    b.Click += (_, _) => JumpToPage(pCap);
                    stack.Children.Add(b);
                }
            }
            LeftPaneBody.Children.Add(new ScrollViewer { Content = stack });
        }
    }

    private void RenderRightNotesPane()
    {
        bool dark = IsEffectiveDark;
        var p = ThemeHelper.Palette(dark);
        PdfNotesStack.Children.Clear();
        var marks = _library.GetMarks(_bookId)
            .OrderBy(ResolveMarkPageIndex)
            .ThenBy(m => m.At)
            .ToList();
        if (marks.Count == 0)
        {
            PdfNotesStack.Children.Add(new TextBlock { Text = L10n.T("还没有划线。选中文字，按 1–4 就划上，也可以点上方「+ 记当前页」。"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var mk in marks)
        {
            int pg = ResolveMarkPageIndex(mk);
            string markId = mk.Id;
            var card = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 8, 8),
            };
            var s = new StackPanel { Spacing = 5 };
            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.Children.Add(new TextBlock
            {
                Text = L10n.T($"第 {pg + 1} 页"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T2),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var delBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYToolbarIconButtonStyle"],
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                Content = YYIcons.Make("trash", 12, ThemeHelper.Brush(p.T3)),
            };
            ToolTipService.SetToolTip(delBtn, L10n.T("删除标注"));
            delBtn.Click += (_, _) =>
            {
                if (_disposed) return;
                try { _library.DeleteMark(_bookId, markId); } catch { }
                RefreshPageOverlay(pg);
                RenderRightNotesPane();
            };
            Grid.SetColumn(delBtn, 1);
            headerRow.Children.Add(delBtn);

            s.Children.Add(headerRow);
            s.Children.Add(BookVisuals.BuildHighlightedQuote(mk, p, dark, 12));
            if (!string.IsNullOrWhiteSpace(mk.Note))
                s.Children.Add(L10n.Skip(new TextBlock { Text = mk.Note, FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap }));
            card.Child = s;
            card.PointerPressed += (_, e) =>
            {
                if (!e.Handled && e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                    JumpToPage(pg);
            };
            PdfNotesStack.Children.Add(card);
        }
    }

    // Event handlers
    private void LeftPaneBtn_Click(object sender, RoutedEventArgs e)
    {
        _leftOpen = !_leftOpen;
        LeftPaneHost.Visibility = _leftOpen ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitter.Visibility = _leftOpen ? Visibility.Visible : Visibility.Collapsed;
        LeftCol.Width = _leftOpen ? new GridLength(240) : new GridLength(0);
        LeftSplitCol.Width = _leftOpen ? new GridLength(6) : new GridLength(0);
        ApplyAppearance();
    }

    private void RightPaneBtn_Click(object sender, RoutedEventArgs e)
    {
        _rightOpen = !_rightOpen;
        RightPaneHost.Visibility = _rightOpen ? Visibility.Visible : Visibility.Collapsed;
        RightSplitter.Visibility = _rightOpen ? Visibility.Visible : Visibility.Collapsed;
        RightCol.Width = _rightOpen ? new GridLength(300) : new GridLength(0);
        RightSplitCol.Width = _rightOpen ? new GridLength(6) : new GridLength(0);
        ApplyAppearance();
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        int prev = _currentPage;
        JumpToPage(_currentPage - (_layoutMode == "double" ? 2 : 1));
        if (_currentPage != prev) _library.PageTurnSounds.PlayManualTurn(isReadingAloud: false);
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        int prev = _currentPage;
        JumpToPage(_currentPage + (_layoutMode == "double" ? 2 : 1));
        if (_currentPage != prev) _library.PageTurnSounds.PlayManualTurn(isReadingAloud: false);
    }
    private async Task ShowJumpToPageDialogAsync()
    {
        if (_disposed || RootGrid.XamlRoot is not { } xr || _pageCount <= 1) return;
        bool dark = IsEffectiveDark;
        string? res = await YYChrome.ShowPromptAsync(
            xr,
            dark,
            "跳转到页码",
            placeholder: "页码",
            initialText: (_currentPage + 1).ToString(),
            confirmText: "跳转",
            cancelText: "取消",
            subtitle: $"请输入页码（1 – {_pageCount}）：",
            tint: _library.Prefs.Tint);
        if (_disposed) return;
        if (int.TryParse((res ?? "").Trim(), out int pg) && pg >= 1 && pg <= _pageCount)
            JumpToPage(pg - 1);
    }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { _zoom = Math.Clamp(_zoom - 0.15, 0.5, 2.5); _ = RenderVisiblePagesAsync(); }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { _zoom = Math.Clamp(_zoom + 0.15, 0.5, 2.5); _ = RenderVisiblePagesAsync(); }

    private void CycleLayout_Click(object sender, RoutedEventArgs e)
    {
        _layoutMode = _layoutMode switch { "scroll" => "single", "single" => "double", _ => "scroll" };
        _library.Prefs.PdfBookLayouts[_bookId] = _layoutMode;
        _library.SavePrefs();
        _ = RenderVisiblePagesAsync();
    }

    private void ToggleNight_Click(object sender, RoutedEventArgs e)
    {
        _nightMode = !_nightMode;
        if (_nightMode) _library.Prefs.PdfNightBooks.Add(_bookId);
        else _library.Prefs.PdfNightBooks.Remove(_bookId);
        _library.SavePrefs();
        ApplyAppearance();
        _ = RenderVisiblePagesAsync();
    }

    private void ToggleListen_Click(object sender, RoutedEventArgs e) => ToggleListen();
    private void TtsPrev_Click(object sender, RoutedEventArgs e) { if (!_disposed) _audio.Previous(); }
    private void TtsNext_Click(object sender, RoutedEventArgs e) { if (!_disposed) _audio.Next(); }

    private void TtsRate_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        double[] rates = [0.8, 1.0, 1.25, 1.5, 2.0];
        int idx = Array.FindIndex(rates, r => Math.Abs(r - _audio.Rate) < 0.05);
        double next = rates[(idx + 1) % rates.Length];
        _audio.SetRate(next);
        _library.Prefs.SpeechRate = next;
        _library.SavePrefs();
        UpdateTtsIcons();
        StateChanged?.Invoke();
    }

    private void OpenMiniPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (!_disposed) App.CurrentApp.ShowMiniPlayer(this);
    }

    private void TtsStop_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        int prevPg = _listeningPage;
        _listeningPage = -1;
        _audio.Pause();
        TtsBarHost.Visibility = Visibility.Collapsed;
        if (prevPg >= 0) RefreshPageOverlay(prevPg);
        ApplyAppearance();
    }

    private async void AddPageNote_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || RootGrid.XamlRoot is not { } xr) return;
        int targetPage = Math.Max(0, _currentPage);
        bool dark = IsEffectiveDark;
        var noteBox = new TextBox { PlaceholderText = L10n.T("写下本页笔记…"), AcceptsReturn = true, MinHeight = 84 };
        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
            Title = L10n.T($"添加第 {targetPage + 1} 页笔记"),
            PrimaryButtonText = L10n.T("保存"),
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            Content = noteBox,
        };
        ThemeHelper.ApplyControlThemeResources(dlg.Resources, dark, _library.Prefs.Tint);
        if (await dlg.ShowSafeAsync(_library.Prefs.Tint) != ContentDialogResult.Primary || _disposed) return;
        string txt = (noteBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(txt)) return;
        uint page = (uint)targetPage;
        _library.AddMark(_bookId, 0, new TextPoint(page, 0), new TextPoint(page, 1), $"第 {page + 1} 页笔记", MarkStyle.Yellow, txt);
        RenderRightNotesPane();
    }

    private void PdfScroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl)
        {
            int delta = e.GetCurrentPoint(PdfScroller).Properties.MouseWheelDelta;
            _zoom = Math.Clamp(_zoom + (delta > 0 ? 0.1 : -0.1), 0.5, 2.5);
            _ = RenderVisiblePagesAsync();
            e.Handled = true;
        }
    }

    private void PdfScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_disposed || _pdfDoc is null) return;
        if (PdfSelectionCard.Visibility == Visibility.Visible || PdfInfoPopupCard.Visibility == Visibility.Visible)
        {
            PdfSelectionCard.Visibility = Visibility.Collapsed;
            PdfInfoPopupCard.Visibility = Visibility.Collapsed;
        }
        if (_layoutMode != "scroll" || _scrollPageContainers.Count == 0)
            return;

        if (_suppressScrollTracking && Environment.TickCount64 < _suppressScrollUntilTick)
        {
            if (!e.IsIntermediate)
                _suppressScrollTracking = false;
        }
        else
        {
            _suppressScrollTracking = false;
            int visPage = FindVisiblePageIndex();
            if (visPage != _currentPage)
            {
                _currentPage = visPage;
                UpdateHeaderLabels();
                StateChanged?.Invoke();
                if (!e.IsIntermediate)
                {
                    _progressSaveTimer.Stop();
                    SaveProgress();
                }
                else
                {
                    _progressSaveTimer.Stop();
                    _progressSaveTimer.Start();
                }
            }
        }

        if (!e.IsIntermediate)
        {
            _scrollRenderTimer.Stop();
            _ = EnsureScrollViewportRenderedAsync(_renderSeq);
        }
        else if (!_scrollRenderTimer.IsEnabled)
        {
            _scrollRenderTimer.Start();
        }
    }

    private void Detach_Click(object sender, RoutedEventArgs e) => App.CurrentApp.TearOutTabToNewWindow(_ownerWindow, this);

    // ITabContent methods
    public void OnTabActivated() => ApplyAppearance();
    public void OnTabDeactivated() { }
    public void OnAppearanceChanged() => ApplyAppearance();
    public void OnReaderSettingsChanged() { }
    public void OnVoiceChanged() { if (!_disposed) _audio.SetVoice(_library.Prefs.GetVoiceForLanguage(_detail.Summary.Language)); }
    public void OnForeignPrefsChanged() { }
    public void OnSnapshotRestored() => ApplyAppearance();
    public void SetOwnerWindow(MainWindow owner) => _ownerWindow = owner;
    public void SetFullScreenChrome(bool isFullScreen)
    {
        TopBarRow.Height = isFullScreen ? new GridLength(0) : new GridLength(40);
        TopBar.Visibility = isFullScreen ? Visibility.Collapsed : Visibility.Visible;
    }

    public bool HandleWindowKeyDown(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        if (ctrl && key == VirtualKey.F) { _leftTab = "search"; if (!_leftOpen) LeftPaneBtn_Click(this, new RoutedEventArgs()); else RenderLeftPane(); return true; }
        if (ctrl && key == VirtualKey.G) { _ = ShowJumpToPageDialogAsync(); return true; }
        if (ctrl && (key == (VirtualKey)187 || key == VirtualKey.Add)) { ZoomIn_Click(this, new RoutedEventArgs()); return true; }
        if (ctrl && (key == (VirtualKey)189 || key == VirtualKey.Subtract)) { ZoomOut_Click(this, new RoutedEventArgs()); return true; }
        if (ctrl && (key == VirtualKey.Number0 || key == VirtualKey.NumberPad0)) { _zoom = 1.0; _ = RenderVisiblePagesAsync(); return true; }
        if (!ctrl && !alt)
        {
            if (PdfSelectionCard.Visibility == Visibility.Visible)
            {
                if (key == VirtualKey.Number1 || key == VirtualKey.NumberPad1) { ApplyPdfMarkStyle(MarkStyle.Yellow); return true; }
                if (key == VirtualKey.Number2 || key == VirtualKey.NumberPad2) { ApplyPdfMarkStyle(MarkStyle.Blue); return true; }
                if (key == VirtualKey.Number3 || key == VirtualKey.NumberPad3) { ApplyPdfMarkStyle(MarkStyle.Pink); return true; }
                if (key == VirtualKey.Number4 || key == VirtualKey.NumberPad4) { ApplyPdfMarkStyle(MarkStyle.Wave); return true; }
            }
            if (key == VirtualKey.Escape && (PdfSelectionCard.Visibility == Visibility.Visible || PdfInfoPopupCard.Visibility == Visibility.Visible))
            {
                HidePdfSelectionCard();
                PdfInfoPopupCard.Visibility = Visibility.Collapsed;
                return true;
            }
            if (key == VirtualKey.Right || key == VirtualKey.PageDown || (key == VirtualKey.Down && _layoutMode != "scroll")) { NextPage_Click(this, new RoutedEventArgs()); return true; }
            if (key == VirtualKey.Left || key == VirtualKey.PageUp || (key == VirtualKey.Up && _layoutMode != "scroll")) { PrevPage_Click(this, new RoutedEventArgs()); return true; }
            if (key == VirtualKey.Space) { ToggleListen(); return true; }
        }
        return false;
    }

    public bool HandleMouseBackForward(bool isBack) => false;
    public SavedTabEntry? SerializeTabState() => new(_bookId, "pdf");
    public Task<bool> PrepareCloseAsync() => Task.FromResult(true);
    public async Task<bool> SaveCompositeScreenshotAsync(string path)
    {
        try { await Yuyue.Reader.QuoteShareDialog.SaveElementPngAsync(RootGrid, path); return File.Exists(path); }
        catch { return false; }
    }

    // IMiniPlayerSource actions
    void IMiniPlayerSource.TogglePlayPause() { if (!_disposed) ToggleListen(); }
    void IMiniPlayerSource.SkipPrevious() { if (!_disposed) _audio.Previous(); }
    void IMiniPlayerSource.SkipNext() { if (!_disposed) _audio.Next(); }
    void IMiniPlayerSource.CycleRate() { if (!_disposed) TtsRate_Click(this, new RoutedEventArgs()); }
    void IMiniPlayerSource.JumpToSource()
    {
        if (_disposed) return;
        App.CurrentApp.RestoreAndSelectTab(_ownerWindow, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderSeq++;
        _scrollRenderGen++;
        try { _scrollRenderTimer.Stop(); } catch { }
        try { _progressSaveTimer.Stop(); } catch { }
        SaveProgress();
        _audio.SentenceStarted -= OnAudioSentenceStarted;
        _audio.PlayStateChanged -= OnAudioPlayStateChanged;
        _audio.SectionFinished -= OnAudioSectionFinished;
        try { App.CurrentApp.ClearActiveMiniPlayerSource(this); } catch { }
        try { App.CurrentApp.ReleaseAudioClaim(this); } catch { }
        try { _audio.Pause(); _library.SystemIntegration.ClearAudioSource(this); _audio.Dispose(); } catch { }
        if (!string.IsNullOrEmpty(_pdfPath))
        {
            try { PdfInfoReader.CloseCachedDocument(_pdfPath); } catch { }
        }
    }
}
