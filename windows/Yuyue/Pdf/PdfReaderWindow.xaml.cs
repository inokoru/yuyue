using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Shelf;
using Yuyue.Speech;
using TimeSpan = System.TimeSpan;

namespace Yuyue.Pdf;

/// <summary>
/// 1:1 port of <c>apple/Yuyue/PDF/MacPdfView.swift</c>:
/// clean white / dark-inverted page canvas, 4-icon top glass capsule (<c>目录 / 笔记 / 书内搜索 / 显示</c>),
/// <c>ShowCard</c> popover (<c>连续滚动 / 单页 / 双页</c> + <c>夜间</c>), always-visible bottom <c>PdfCapsule</c>
/// with <c>ListenCard</c> popover, and floating right <c>300px</c> side card.
/// </summary>
public sealed partial class PdfReaderWindow : Window
{
    private readonly LibraryService _library;
    private readonly string _bookId;
    private readonly BookDetail _detail;
    private readonly AudioPlayer _audio;
    private readonly DispatcherTimer _readingTimer;
    private readonly DispatcherTimer _sleepTimer;
    private readonly DispatcherTimer _searchDebounce;

    private PdfDocument? _doc;
    private string? _filePath;
    private int _pageCount;
    private int _currentPage; // 0-based
    private double _zoom = 1.0;
    private string _layoutMode = "scroll"; // scroll | single | double (MacPdfView default: scroll)
    private bool _nightPaper;
    private bool _isFullScreen;
    private bool _showOpen;
    private string? _sideMode; // toc | notes | search
    private string _searchQuery = "";
    private IReadOnlyList<(int PageZeroBased, int CharOffset, int MatchLen, string Snippet)> _searchHits = [];
    private int _searchHitIndex = -1;
    private (int Page, int Start, int Len)? _activeSearchHighlight;
    private DateTime _lastWheelTurnUtc = DateTime.MinValue;

    private IReadOnlyList<(string Title, int PageIndex, int Level)> _outline = [];
    private readonly List<Image> _continuousImages = [];
    private readonly List<Canvas> _continuousOverlays = [];
    private readonly HashSet<int> _renderedContinuous = [];

    // Page text geometry, inline mark overlays & drag-selection state (supports cross-page marks)
    private readonly Dictionary<int, PdfPageLayout> _layoutCache = new();
    private readonly HashSet<int> _loadingLayouts = [];
    private int _selPage = -1;
    private int _selEndPage = -1;
    private int _selStart = -1;
    private int _selEnd = -1;
    private int _selAnchorGlyph = -1;
    private bool _selDragging;
    private Mark? _editingMark;
    private Canvas? _activeDragCanvas;

    // TTS state
    private readonly Dictionary<int, PageSentence[]> _sentenceCache = new();
    private PageSentence[] _activeSentences = [];
    private int _activeSentenceIndex = -1;
    private int _listeningPage = -1;
    private bool _isListening;
    private bool _pendingAutoListen;
    private int _sleepMode; // 0=off, -2=sentence, -1=page, >0=minutes
    private DateTime? _sleepUntilUtc;

    private Action? _toastUndo;
    private DispatcherTimer? _toastTimer;

    public PdfReaderWindow(LibraryService library, string bookId, bool autoListen = false, uint? startPage = null)
    {
        _library = library;
        _bookId = bookId;
        _detail = library.GetBookDetail(bookId);
        _pendingAutoListen = autoListen;

        // Per-book PDF display preferences (aligning PdfModel.swift lines 66-70)
        if (_library.Prefs.PdfBookLayouts.TryGetValue(bookId, out var savedLayout) && !string.IsNullOrEmpty(savedLayout))
            _layoutMode = savedLayout;
        else
            _layoutMode = string.IsNullOrEmpty(_library.Prefs.PdfLayout) ? "scroll" : _library.Prefs.PdfLayout;

        // Per-book night mode isolation (toggling for one PDF never forces night mode on other PDFs)
        _nightPaper = _library.Prefs.PdfNightBooks.Contains(bookId);

        InitializeComponent();
        Title = _detail.Summary.Title;
        BookTitleText.Text = _detail.Summary.Title;
        ThemeHelper.ResizeWindow(this, 1080, 760);
        SetTitleBar(TitleDragRegion);
        ThemeHelper.ApplyElevation(PdfSidePanel, PageShadowReceiver, 16f);

        SidePanelResizeHandle.MinWidthLimit = 286.0;
        SidePanelResizeHandle.DefaultWidth = 334.0;
        SidePanelResizeHandle.GetCurrentWidth = () => SidePanelColumn.Width.Value;
        SidePanelResizeHandle.GetMaxWidth = () => Math.Max(286.0, Math.Min(554.0, (RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1080.0) * 0.50));
        SidePanelResizeHandle.ApplyWidth = w =>
        {
            SidePanelColumn.Width = new GridLength(w);
            if (_layoutMode != "scroll") UpdatePagedFitSize();
        };
        SidePanelResizeHandle.CommitWidth = w =>
        {
            _library.Prefs.ReaderPanelWidth = Math.Max(272.0, w - 14.0);
            _library.SavePrefs();
        };
        ThemeHelper.ApplyElevation(PdfCapsule, PageShadowReceiver, 14f);
        ThemeHelper.ApplyElevation(PdfSelectionCard, PageShadowReceiver, 16f);
        ThemeHelper.ApplyElevation(ToastBanner, PageShadowReceiver, 12f);

        AttachOverlayEvents(SinglePageOverlay, () => _currentPage);
        AttachOverlayEvents(SecondPageOverlay, () => _currentPage + 1);

        _audio = new AudioPlayer(_library);
        _audio.SetRate(_library.Prefs.SpeechRate);
        _audio.SetVoice(_library.Prefs.GetVoiceForLanguage(_detail.Summary.Language));
        _audio.SentenceFinished += OnAudioSentenceFinished;
        _audio.PlayStateChanged += OnAudioPlayStateChanged;
        _audio.MarkHeardRequested += OnAudioMarkHeardRequested;

        _readingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _readingTimer.Tick += (_, _) =>
        {
            try
            {
                _library.AddTime(_bookId, 0, _isListening ? 0u : 30u, _isListening ? 30u : 0u);
            }
            catch { }
        };
        _readingTimer.Start();

        _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sleepTimer.Tick += (_, _) =>
        {
            if (_sleepUntilUtc is DateTime until && DateTime.UtcNow >= until)
            {
                _sleepUntilUtc = null;
                _sleepMode = 0;
                _sleepTimer.Stop();
                StopListening();
                try { _library.AddBookmark(_bookId, 0, (uint)Math.Max(0, _currentPage)); } catch { }
                ShowBriefToast($"睡眠定时已到，已记下第 {_currentPage + 1} 页");
            }
        };

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce.Stop();
            await ExecutePdfSearchAsync(_searchQuery);
        };

        Closed += OnWindowClosed;
        CanvasHost.SizeChanged += (_, _) =>
        {
            if (_layoutMode != "scroll" && _doc is not null) UpdatePagedFitSize();
        };

        ApplyTintAndChrome();
        _ = LoadDocumentAsync(startPage);
    }

    public void ReloadVoicePrefs()
    {
        _audio.SetVoice(_library.Prefs.VoiceId);
        _audio.SetRate(_library.Prefs.SpeechRate);
        UpdateCapsuleState();
    }

    public void ReloadAfterSnapshotRestore()
    {
        ApplyTintAndChrome();
        RefreshPageOverlay(_currentPage);
        if (_layoutMode == "double" && _currentPage + 1 < _pageCount)
            RefreshPageOverlay(_currentPage + 1);
        if (!string.IsNullOrEmpty(_sideMode)) RenderSidePanel();
    }

    public void JumpToPage(int pageZeroBased)
    {
        if (_pageCount <= 0) return;
        _ = GoToPageAsync(Math.Clamp(pageZeroBased, 0, _pageCount - 1));
    }

    public void TriggerListen() => StartListeningCurrentPage(0);

#if DEBUG
    public void OpenDebugSidePanel(string panel) => ToggleSidePanel(panel);
#endif

    public void ApplyTintAndChrome()
    {
        bool dark = _nightPaper || ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        RootGrid.Background = ThemeHelper.Brush(_nightPaper
            ? Color.FromArgb(255, 33, 33, 35)
            : (dark ? Color.FromArgb(255, 24, 24, 26) : Colors.White));

        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var t1 = ThemeHelper.Brush(p.T1);
        var accentBrush = ThemeHelper.Brush(tint.Accent);
        var softBrush = ThemeHelper.Brush(tint.Soft);
        var clear = new SolidColorBrush(Colors.Transparent);
        var glassBrush = ThemeHelper.Brush(dark ? Color.FromArgb(220, 28, 29, 32) : Color.FromArgb(236, 252, 251, 249));
        var lineBrush = ThemeHelper.Brush(p.Line);

        BackToShelfBtn.Background = glassBrush;
        BackToShelfBtn.BorderBrush = lineBrush;
        IconBack.Content = YYIcons.Make("chevron-left", 11.5, t1);

        ToolCapsule.Background = glassBrush;
        ToolCapsule.BorderBrush = lineBrush;

        void PaintTool(Button b, string icon, bool active)
        {
            b.Background = active ? softBrush : clear;
            b.Content = YYIcons.Make(icon, 13.5, active ? accentBrush : t1);
        }

        PaintTool(PdfTocBtn, "list", _sideMode == "toc");
        PaintTool(PdfNotesBtn, "note-card", _sideMode == "notes");
        PaintTool(PdfSearchBtn, "search", _sideMode == "search");
        PaintTool(PdfShowBtn, "doc-search", _showOpen);

        PdfCapsule.Background = ThemeHelper.Brush(dark ? Color.FromArgb(255, 36, 37, 41) : Color.FromArgb(255, 248, 246, 242));
        PdfCapsule.BorderBrush = ThemeHelper.Brush(dark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0));
        CapsulePrevBtn.Content = YYIcons.Make("chevron-left", 12, t1);
        CapsuleNextBtn.Content = YYIcons.Make("chevron-right", 12, t1);
        CapsulePlayBtn.Background = ThemeHelper.Brush(tint.Fill);
        CapsulePlayBtn.Content = YYIcons.Make(_isListening && _audio.IsPlaying ? "pause" : "play", 13, new SolidColorBrush(Colors.White), filled: true);

        ToastCheckIcon.Content = YYIcons.Make("checkmark", 9.5, new SolidColorBrush(Colors.White));
        PdfSelDeleteBtn.Content = YYIcons.Make("trash", 12, ThemeHelper.Brush(p.Danger));
        RefreshVisibleOverlays();
        ToastUndoBtn.Content = YYIcons.Make("arrow-undo", 11.5, t1);

        UpdateCapsuleState();
        ThemeHelper.ConfigureTitleBar(this, dark);
        L10n.ApplyTree(RootGrid);
    }

    private void UpdateCapsuleState()
    {
        if (_pageCount <= 0) return;
        if (_isListening && _activeSentences.Length > 0)
        {
            int cur = Math.Clamp(_activeSentenceIndex + 1, 1, _activeSentences.Length);
            int remSentences = Math.Max(0, _activeSentences.Length - cur);
            int minsLeft = Math.Max(1, (int)Math.Ceiling(remSentences * 3.8 / 60.0 / Math.Max(0.5, _library.Prefs.SpeechRate)));
            CapsuleTitleText.Text = L10n.T($"第 {_currentPage + 1} 页 · 第 {cur} / {_activeSentences.Length} 句");
            CapsuleSubText.Text = L10n.T($"这一页还剩 {minsLeft} 分钟 · {_library.Prefs.SpeechRate:0.#}×");
        }
        else
        {
            string pageRange = (_layoutMode == "double" && _currentPage + 1 < _pageCount)
                ? $"第 {_currentPage + 1}–{_currentPage + 2} / {_pageCount} 页"
                : $"第 {_currentPage + 1} / {_pageCount} 页";
            CapsuleTitleText.Text = L10n.T(pageRange);
            CapsuleSubText.Text = L10n.T("点播放从这一页读起");
        }
        CapsulePrevBtn.IsEnabled = _isListening ? (_activeSentenceIndex > 0 || _currentPage > 0) : _currentPage > 0;
        CapsuleNextBtn.IsEnabled = _isListening ? (_activeSentenceIndex + 1 < _activeSentences.Length || _currentPage + 1 < _pageCount) : _currentPage + 1 < _pageCount;
    }

    private async Task LoadDocumentAsync(uint? startPage)
    {
        _filePath = _library.TryOriginalFile(_bookId);
        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
        {
            ErrorText.Text = "可能被移走或删掉了。从书架上删掉，再导入一次就好。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_filePath);
            _doc = await PdfDocument.LoadFromFileAsync(file);
            _pageCount = (int)_doc.PageCount;
            if (_pageCount <= 0)
            {
                ErrorText.Text = "这份 PDF 没有可显示的页面。";
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            string fp = _filePath;
            _outline = await Task.Run(() => PdfInfoReader.ExtractOutline(fp));

            if (_detail.Sections.Length == 0 || _detail.Sections[0].Sentences == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var info = await PdfInfoReader.ReadAsync(fp);
                        _library.Core.SetPdfInfo(_bookId, info);
                    }
                    catch { }
                });
            }

            if (startPage is uint forced)
                _currentPage = Math.Clamp((int)forced, 0, _pageCount - 1);
            else
            {
                var saved = _library.GetProgress(_bookId);
                _currentPage = saved is not null ? Math.Clamp((int)saved.Position.Sentence, 0, _pageCount - 1) : 0;
            }

            await RebuildViewModeAsync();
            SaveCurrentProgress();
            App.CurrentApp.NotifyShelfRefresh();

            if (_pendingAutoListen)
            {
                _pendingAutoListen = false;
                StartListeningCurrentPage(0);
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"打开 PDF 失败：{ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private async Task RebuildViewModeAsync()
    {
        bool scroll = _layoutMode == "scroll";
        PagedScroller.Visibility = scroll ? Visibility.Collapsed : Visibility.Visible;
        ContinuousScroller.Visibility = scroll ? Visibility.Visible : Visibility.Collapsed;

        if (scroll)
        {
            BuildContinuousPlaceholders();
            await RenderContinuousWindowAsync(_currentPage);
            ScrollContinuousToPage(_currentPage);
        }
        else
        {
            await RenderPagedAsync(_currentPage);
        }

        UpdateStatusText();
        ApplyTintAndChrome();
        if (_sideMode == "toc") RenderSidePanel();
    }

    private async Task GoToPageAsync(int page)
    {
        if (_doc is null || _pageCount <= 0) return;
        _currentPage = Math.Clamp(page, 0, _pageCount - 1);

        if (_layoutMode == "scroll")
        {
            await RenderContinuousWindowAsync(_currentPage);
            ScrollContinuousToPage(_currentPage);
        }
        else
        {
            await RenderPagedAsync(_currentPage);
            PagedScroller.ChangeView(null, 0, null, disableAnimation: true);
        }

        SaveCurrentProgress();
        UpdateStatusText();
        ApplyTintAndChrome();
        if (_sideMode == "toc") RenderSidePanel();
    }

    private async Task RenderPagedAsync(int pageIndex)
    {
        if (_doc is null || pageIndex < 0 || pageIndex >= _pageCount) return;
        try
        {
            var bmp1 = await RenderPageBitmapAsync(pageIndex, _zoom);
            SinglePageImage.Source = bmp1;

            bool twoUp = _layoutMode == "double" && pageIndex + 1 < _pageCount;
            SecondPageFrame.Visibility = twoUp ? Visibility.Visible : Visibility.Collapsed;
            if (twoUp)
            {
                SecondPageImage.Source = await RenderPageBitmapAsync(pageIndex + 1, _zoom);
            }
            UpdatePagedFitSize();
        }
        catch { }
    }

    private void UpdatePagedFitSize()
    {
        if (_doc is null || _currentPage < 0 || _currentPage >= _pageCount) return;
        try
        {
            using var p = _doc.GetPage((uint)_currentPage);
            bool twoUp = _layoutMode == "double" && _currentPage + 1 < _pageCount;
            double availW = Math.Max(320, (CanvasHost.ActualWidth > 0 ? CanvasHost.ActualWidth : 980) - 64);
            double availH = Math.Max(320, (CanvasHost.ActualHeight > 0 ? CanvasHost.ActualHeight : 660) - 140);
            double aspect = p.Size.Width > 0 ? p.Size.Height / p.Size.Width : 1.414;
            double perPageAvailW = twoUp ? (availW - 14) / 2.0 : availW;
            double fitW = Math.Min(perPageAvailW, availH / aspect);
            double displayW = fitW * _zoom;

            SinglePageImage.Width = displayW;
            SinglePageImage.Height = displayW * aspect;
            SinglePageOverlay.Width = displayW;
            SinglePageOverlay.Height = displayW * aspect;
            EnsurePageLayoutAsync(_currentPage);
            if (twoUp)
            {
                SecondPageImage.Width = displayW;
                SecondPageImage.Height = displayW * aspect;
                SecondPageOverlay.Width = displayW;
                SecondPageOverlay.Height = displayW * aspect;
                EnsurePageLayoutAsync(_currentPage + 1);
            }
            RefreshVisibleOverlays();
        }
        catch { }
    }

    private void BuildContinuousPlaceholders()
    {
        ContinuousStack.Children.Clear();
        _continuousImages.Clear();
        _continuousOverlays.Clear();
        _renderedContinuous.Clear();
        HidePdfSelectionCard();
        if (_doc is null) return;

        double baseW = Math.Clamp((CanvasHost.ActualWidth > 0 ? CanvasHost.ActualWidth : 940) - 100, 440, 880) * _zoom;
        double defaultAspect = 1.414;
        try
        {
            using var p0 = _doc.GetPage(0);
            if (p0.Size.Width > 0) defaultAspect = p0.Size.Height / p0.Size.Width;
        }
        catch { }

        Color hairline = _nightPaper ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
        for (int i = 0; i < _pageCount; i++)
        {
            double pageH = baseW * defaultAspect;
            var img = new Image { Width = baseW, Height = pageH, Stretch = Stretch.Uniform };
            var overlay = new Canvas { Width = baseW, Height = pageH, Background = new SolidColorBrush(Colors.Transparent) };
            _continuousImages.Add(img);
            _continuousOverlays.Add(overlay);
            int pageIdx = i;
            AttachOverlayEvents(overlay, () => pageIdx);

            var pageGrid = new Grid { Width = baseW, Height = pageH };
            pageGrid.Children.Add(img);
            pageGrid.Children.Add(overlay);

            var frame = new Border
            {
                BorderBrush = ThemeHelper.Brush(hairline),
                BorderThickness = new Thickness(0, 0, 0, i + 1 < _pageCount ? 1 : 0),
                Padding = new Thickness(0, 0, 0, 12),
                ContextFlyout = BuildPageContextMenu(pageIdx),
                Child = pageGrid,
            };
            ContinuousStack.Children.Add(frame);
        }
        SinglePageFrame.ContextFlyout = BuildPageContextMenu(_currentPage);
    }

    private async Task RenderContinuousWindowAsync(int centerPage)
    {
        if (_doc is null) return;
        int from = Math.Max(0, centerPage - 2);
        int to = Math.Min(_pageCount - 1, centerPage + 3);
        for (int i = from; i <= to; i++)
        {
            EnsurePageLayoutAsync(i);
            if (!_renderedContinuous.Add(i)) continue;
            var bmp = await RenderPageBitmapAsync(i, _zoom);
            if (bmp is not null && i < _continuousImages.Count)
            {
                _continuousImages[i].Source = bmp;
                RefreshPageOverlay(i);
            }
        }
    }

    private void ScrollContinuousToPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _continuousImages.Count) return;
        double y = 0;
        for (int i = 0; i < pageIndex; i++)
        {
            if (_continuousImages[i].Height > 0) y += _continuousImages[i].Height + 24;
        }
        ContinuousScroller.ChangeView(null, y, null, disableAnimation: true);
    }

    /// <summary>
    /// Renders a PDF page; in <c>夜间</c> mode applies MacPdfView's white-to-charcoal (#1F1F21) and
    /// black-to-warm-ink (#DCD6CC) pixel filter so diagrams &amp; text invert cleanly!
    /// </summary>
    private async Task<ImageSource?> RenderPageBitmapAsync(int pageIndex, double zoom)
    {
        if (_doc is null || pageIndex < 0 || pageIndex >= _pageCount) return null;
        using var page = _doc.GetPage((uint)pageIndex);
        uint targetWidth = (uint)Math.Clamp(Math.Round(1100 * zoom), 600, 2400);
        uint targetHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * targetWidth / Math.Max(1, page.Size.Width)));

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = targetWidth,
            DestinationHeight = targetHeight,
            BackgroundColor = Colors.White,
        });
        stream.Seek(0);

        if (!_nightPaper)
        {
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            return bmp;
        }

        // True Night Filter (MacPdfView lines 519-528: white #FFFFFF -> #1F1F21, black #000000 -> #DCD6CC)
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        byte[] bytes = pixelData.DetachPixelData();
        for (int idx = 0; idx + 3 < bytes.Length; idx += 4)
        {
            // Luminance-preserving warm inversion
            double b = bytes[idx] / 255.0;
            double g = bytes[idx + 1] / 255.0;
            double r = bytes[idx + 2] / 255.0;
            double invR = 31 + (1.0 - r) * (220 - 31);
            double invG = 31 + (1.0 - g) * (214 - 31);
            double invB = 33 + (1.0 - b) * (204 - 33);
            bytes[idx] = (byte)Math.Clamp((int)invB, 0, 255);
            bytes[idx + 1] = (byte)Math.Clamp((int)invG, 0, 255);
            bytes[idx + 2] = (byte)Math.Clamp((int)invR, 0, 255);
        }

        var wb = new WriteableBitmap((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        using (var outStream = wb.PixelBuffer.AsStream())
        {
            await outStream.WriteAsync(bytes, 0, bytes.Length);
        }
        wb.Invalidate();
        return wb;
    }

    private void SaveCurrentProgress()
    {
        try { _library.SaveProgress(_bookId, 0, (uint)Math.Max(0, _currentPage)); } catch { }
    }

    private void UpdateStatusText()
    {
        string? author = _detail.Summary.Authors.FirstOrDefault();
        string? chap = null;
        if (_outline.Count > 1)
        {
            foreach (var o in _outline)
            {
                if (o.PageIndex <= _currentPage) chap = o.Title;
                else break;
            }
        }
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(author)) parts.Add(author!);
        if (!string.IsNullOrWhiteSpace(chap)) parts.Add(chap!);
        parts.Add($"第 {_currentPage + 1} / {_pageCount} 页");
        PageStatusText.Text = string.Join(" · ", parts);

        if (_isFullScreen && CanvasHost.ActualWidth > 0 && _pageCount > 0)
            FullScreenProgressFill.Width = CanvasHost.ActualWidth * (_currentPage + 1) / _pageCount;

        SinglePageFrame.ContextFlyout = BuildPageContextMenu(_currentPage);
        UpdateCapsuleState();
    }

    // ---------- ShowCard Popover (MacPdfView.ShowCard: 连续滚动 / 单页 / 双页 + 夜间) ----------

    private void ShowCardFlyout_Opening(object sender, object e)
    {
        _showOpen = true;
        ApplyTintAndChrome();
        BuildShowCard();
    }

    private void ShowCardFlyout_Closed(object sender, object e)
    {
        _showOpen = false;
        ApplyTintAndChrome();
    }

    private void BuildShowCard()
    {
        ShowCardHost.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);

        ShowCardHost.Children.Add(new TextBlock
        {
            Text = "显示",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ThemeHelper.Brush(p.T1),
        });

        var tiles = new Grid { ColumnSpacing = 8 };
        for (int c = 0; c < 3; c++) tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        (string Mode, string Label, string Icon)[] modes =
        [
            ("scroll", "连续滚动", "col-scroll"),
            ("single", "单页", "col-1"),
            ("double", "双页", "col-2"),
        ];

        for (int i = 0; i < modes.Length; i++)
        {
            var (mode, label, icon) = modes[i];
            bool on = _layoutMode == mode;
            var sp = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(YYIcons.Make(icon, 16, ThemeHelper.Brush(on ? p.T1 : p.T2)));
            sp.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var btn = new Button
            {
                Height = 64,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = ThemeHelper.Brush(on ? p.Inset : p.Card),
                BorderBrush = ThemeHelper.Brush(on ? Color.FromArgb(255, 118, 116, 112) : p.Line),
                BorderThickness = new Thickness(on ? 1.5 : 0.75),
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = sp,
            };
            string nextMode = mode;
            btn.Click += async (_, _) =>
            {
                _layoutMode = nextMode;
                _library.Prefs.PdfLayout = nextMode;
                _library.Prefs.PdfBookLayouts[_bookId] = nextMode;
                _library.SavePrefs();
                BuildShowCard();
                await RebuildViewModeAsync();
            };
            Grid.SetColumn(btn, i);
            tiles.Children.Add(btn);
        }
        ShowCardHost.Children.Add(tiles);

        var nightRow = new Grid { Padding = new Thickness(12, 10, 12, 10) };
        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = "夜间", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
        left.Children.Add(new TextBlock { Text = "白纸变黑，字变浅", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
        nightRow.Children.Add(left);

        var sw = new ToggleSwitch { IsOn = _nightPaper, MinWidth = 0, OnContent = "", OffContent = "", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        sw.Toggled += async (_, _) =>
        {
            _nightPaper = sw.IsOn;
            if (_nightPaper) _library.Prefs.PdfNightBooks.Add(_bookId);
            else _library.Prefs.PdfNightBooks.Remove(_bookId);
            _library.SavePrefs();
            _renderedContinuous.Clear();
            await RebuildViewModeAsync();
            BuildShowCard();
        };
        nightRow.Children.Add(sw);

        ShowCardHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(11),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Child = nightRow,
        });
    }

    // ---------- ListenCard Popover (MacPdfView -> ListenCard) ----------

    private void ListenCardFlyout_Opening(object sender, object e) => BuildListenCard();

    private void BuildListenCard()
    {
        ListenCardHost.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var head = new Grid();
        head.Children.Add(new TextBlock { Text = "听书", FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = "空格 暂停 · Ctrl+[ / ] 上下句", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        ListenCardHost.Children.Add(head);

        // Narrator Voice Row
        var voices = AudioPlayer.GetAllVoiceOptions(_library);
        var curVoice = voices.FirstOrDefault(v => v.Id == _library.Prefs.VoiceId) ?? voices.FirstOrDefault();
        var voiceRow = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 10, 12, 10) };
        voiceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        voiceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        voiceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        voiceRow.Children.Add(new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(p.Card),
            Child = YYIcons.Make("wave", 14, ThemeHelper.Brush(tint.Accent)),
        });

        var vMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        vMeta.Children.Add(new TextBlock { Text = curVoice?.Label ?? "系统默认人声", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        vMeta.Children.Add(new TextBlock { Text = "本地离线合成", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
        Grid.SetColumn(vMeta, 1);
        voiceRow.Children.Add(vMeta);

        var changeVoiceBtn = new Button
        {
            Content = "换声音 ›",
            FontSize = 11.5,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = ThemeHelper.Brush(tint.Accent),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4, 4, 4),
            VerticalAlignment = VerticalAlignment.Center,
            UseSystemFocusVisuals = false,
        };
        var vMenu = new MenuFlyout();
        foreach (var v in voices.Take(12))
        {
            string vid = v.Id;
            var item = new MenuFlyoutItem { Text = (vid == _library.Prefs.VoiceId ? "✓ " : "") + v.Label };
            item.Click += (_, _) => { _library.Prefs.VoiceId = vid; _library.SavePrefs(); ReloadVoicePrefs(); BuildListenCard(); };
            vMenu.Items.Add(item);
        }
        vMenu.Items.Add(new MenuFlyoutSeparator());
        var openListenSettings = new MenuFlyoutItem { Text = "去设置 · 朗读…" };
        openListenSettings.Click += (_, _) => { ListenCardFlyout.Hide(); App.CurrentApp.OpenSettings("listening"); };
        vMenu.Items.Add(openListenSettings);
        changeVoiceBtn.Flyout = vMenu;
        Grid.SetColumn(changeVoiceBtn, 2);
        voiceRow.Children.Add(changeVoiceBtn);

        ListenCardHost.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = ThemeHelper.Brush(p.Inset), Child = voiceRow });

        // Speed Slider (0.8 .. 2.0x)
        var speedTop = new Grid();
        var sLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        sLeft.Children.Add(new TextBlock { Text = "速度", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        var speedValText = new TextBlock { Text = $"{_library.Prefs.SpeechRate:0.0}×", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
        sLeft.Children.Add(speedValText);
        sLeft.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "不变调", FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T2) } });
        speedTop.Children.Add(sLeft);
        ListenCardHost.Children.Add(speedTop);

        var slider = new Slider { Minimum = 0.8, Maximum = 2.0, StepFrequency = 0.1, Value = Math.Clamp(_library.Prefs.SpeechRate, 0.8, 2.0) };
        slider.ValueChanged += (_, e) =>
        {
            double r = Math.Round(e.NewValue, 1);
            _library.Prefs.SpeechRate = r;
            _library.SavePrefs();
            _audio.SetRate(r);
            speedValText.Text = $"{r:0.0}×";
            UpdateCapsuleState();
        };
        ListenCardHost.Children.Add(slider);

        // Sleep Timer Segmented Bar
        var sleepHead = new Grid();
        sleepHead.Children.Add(new TextBlock { Text = "睡眠定时", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) });
        sleepHead.Children.Add(new TextBlock { Text = "停止时自动记录位置", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
        ListenCardHost.Children.Add(sleepHead);

        var sleepGrid = new Grid { ColumnSpacing = 2 };
        (int Mode, string Label)[] sleepOpts = [(0, "关"), (-2, "本句完"), (-1, "本页完"), (15, "15 分"), (30, "30 分"), (60, "60 分")];
        for (int i = 0; i < sleepOpts.Length; i++)
        {
            sleepGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (m, lbl) = sleepOpts[i];
            bool on = _sleepMode == m;
            var b = new Button
            {
                Content = lbl,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 30,
                Padding = new Thickness(0),
                FontSize = 11,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                UseSystemFocusVisuals = false,
            };
            int nextM = m;
            b.Click += (_, _) =>
            {
                _sleepMode = nextM;
                _sleepUntilUtc = nextM > 0 ? DateTime.UtcNow.AddMinutes(nextM) : null;
                if (_sleepUntilUtc is not null) _sleepTimer.Start();
                else _sleepTimer.Stop();
                BuildListenCard();
            };
            Grid.SetColumn(b, i);
            sleepGrid.Children.Add(b);
        }
        ListenCardHost.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(3), Child = sleepGrid });
    }

    // ---------- Right Side Panel: 目录 / 笔记 / 书内搜索 ----------

    private void ToggleSidePanel(string mode)
    {
        if (_sideMode == mode)
        {
            _sideMode = null;
            SidePanelColumn.Width = new GridLength(0);
            PdfSidePanel.Visibility = Visibility.Collapsed;
            SidePanelResizeHandle.Visibility = Visibility.Collapsed;
        }
        else
        {
            _sideMode = mode;
            double maxPanel = Math.Max(272.0, Math.Min(540.0, (RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1080.0) * 0.50));
            double baseW = _library.Prefs.ReaderPanelWidth > 0 ? _library.Prefs.ReaderPanelWidth : 320.0;
            SidePanelColumn.Width = new GridLength(Math.Clamp(baseW, 272.0, maxPanel) + 14.0);
            PdfSidePanel.Visibility = Visibility.Visible;
            SidePanelResizeHandle.Visibility = Visibility.Visible;
            RenderSidePanel();
        }
        ApplyTintAndChrome();
        if (_layoutMode != "scroll") UpdatePagedFitSize();
    }

    private void PdfToc_Click(object sender, RoutedEventArgs e) => ToggleSidePanel("toc");
    private void PdfNotes_Click(object sender, RoutedEventArgs e) => ToggleSidePanel("notes");
    private void PdfSearch_Click(object sender, RoutedEventArgs e) => ToggleSidePanel("search");

    private void RenderSidePanel()
    {
        PdfSideHeaderHost.Children.Clear();
        PdfSideList.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        switch (_sideMode)
        {
            case "toc":
            {
                var head = new Grid();
                head.Children.Add(new TextBlock { Text = "目录", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
                head.Children.Add(new TextBlock { Text = $"{_pageCount} 页", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
                PdfSideHeaderHost.Children.Add(head);

                if (_outline.Count == 0)
                {
                    AddSideEmpty(p, "这个 PDF 没有目录", "可以用书内搜索找内容");
                    return;
                }

                int curIdx = -1;
                for (int i = 0; i < _outline.Count; i++)
                    if (_outline[i].PageIndex <= _currentPage) curIdx = i;

                for (int i = 0; i < _outline.Count; i++)
                {
                    var (title, pg, level) = _outline[i];
                    bool active = i == curIdx;
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(new TextBlock
                    {
                        Text = title,
                        FontSize = 12.5,
                        FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                        Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T1),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1,
                    });
                    var pgTb = new TextBlock { Text = $"{pg + 1}", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(pgTb, 1);
                    row.Children.Add(pgTb);

                    var btn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = active ? ThemeHelper.Brush(p.Inset) : new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(7),
                        Padding = new Thickness(10 + level * 14, 7, 10, 7),
                        UseSystemFocusVisuals = false,
                        Content = row,
                    };
                    int targetPg = pg;
                    btn.Click += (_, _) => _ = GoToPageAsync(targetPg);
                    PdfSideList.Children.Add(btn);
                }
                break;
            }

            case "notes":
            {
                var marks = _library.GetMarks(_bookId).OrderBy(m => m.Start.Sentence).ToArray();
                var head = new Grid();
                head.Children.Add(new TextBlock { Text = "笔记", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
                var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                right.Children.Add(new TextBlock { Text = $"{marks.Length} 条划线", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
                var addBtn = new Button
                {
                    Width = 26,
                    Height = 26,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(13),
                    Background = ThemeHelper.Brush(p.Inset),
                    BorderThickness = new Thickness(0),
                    Content = YYIcons.Make("plus", 11.5, ThemeHelper.Brush(p.T1)),
                };
                ToolTipService.SetToolTip(addBtn, "摘录当前页句子或写想法");
                addBtn.Click += async (_, _) => await AddCurrentPageNoteDialogAsync();
                right.Children.Add(addBtn);
                head.Children.Add(right);
                PdfSideHeaderHost.Children.Add(head);

                if (marks.Length == 0)
                {
                    AddSideEmpty(p, "还没有划线", "直接拖拽正文选字即可划线写想法，或点右上角「+」摘录");
                    return;
                }

                for (int i = 0; i < marks.Length; i++)
                {
                    if (i > 0) PdfSideList.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(6, 0, 6, 0) });
                    var mk = marks[i];
                    int pg = (int)mk.Start.Sentence;
                    var sp = new StackPanel { Spacing = 6, Padding = new Thickness(6, 10, 6, 10) };

                    var meta = new Grid();
                    meta.Children.Add(new TextBlock { Text = $"第 {pg + 1} 页", FontSize = 10.5, FontWeight = pg == _currentPage ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = ThemeHelper.Brush(pg == _currentPage ? tint.Accent : p.T3) });
                    var dt = DateTimeOffset.FromUnixTimeMilliseconds(mk.At).LocalDateTime;
                    meta.Children.Add(new TextBlock { Text = dt.Date == DateTime.Today ? "今天" : $"{dt.Month}月{dt.Day}日", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
                    sp.Children.Add(meta);
                    sp.Children.Add(ShelfWindow.BuildHighlightedQuote(mk, p, dark, 12.5));

                    if (!string.IsNullOrWhiteSpace(mk.Note))
                    {
                        var tRow = new Grid { ColumnSpacing = 8, Margin = new Thickness(1, 2, 0, 0) };
                        tRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                        tRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        tRow.Children.Add(new Border { Background = ThemeHelper.Brush(tint.Accent), CornerRadius = new CornerRadius(1) });
                        var ntb = new TextBlock { Text = mk.Note, FontFamily = new FontFamily(YYFontFamily.Kaiti.WindowsFamily()), FontSize = 12, Foreground = ThemeHelper.Brush(tint.Accent), TextWrapping = TextWrapping.Wrap };
                        Grid.SetColumn(ntb, 1);
                        tRow.Children.Add(ntb);
                        sp.Children.Add(tRow);
                    }

                    var ctx = new MenuFlyout();
                    var editNote = new MenuFlyoutItem { Text = string.IsNullOrWhiteSpace(mk.Note) ? "写想法…" : "改想法…" };
                    editNote.Click += async (_, _) => await EditPdfMarkNoteAsync(mk);

                    var styleSub = new MenuFlyoutSubItem { Text = "换个样子" };
                    foreach (var (st, sName) in new[] { (MarkStyle.Yellow, "黄色"), (MarkStyle.Blue, "蓝色"), (MarkStyle.Pink, "粉色"), (MarkStyle.Wave, "波浪线"), (MarkStyle.Underline, "实线下划线"), (MarkStyle.Dotted, "点线下划线") })
                    {
                        var ms = st;
                        var si = new MenuFlyoutItem { Text = (mk.Style == ms ? "✓ " : "") + sName };
                        si.Click += (_, _) => { _library.SetMarkStyle(_bookId, mk.Id, ms); RefreshPageOverlay((int)mk.Start.Sentence); RenderSidePanel(); };
                        styleSub.Items.Add(si);
                    }

                    var speakFrom = new MenuFlyoutItem { Text = "从这听" };
                    speakFrom.Click += async (_, _) =>
                    {
                        await GoToPageAsync(pg);
                        int sentIdx = FindSentenceIndexForOffset(pg, (int)mk.Start.Offset);
                        StartListeningCurrentPage(sentIdx);
                    };

                    var del = new MenuFlyoutItem { Text = "删除划线", Foreground = ThemeHelper.Brush(p.Danger) };
                    del.Click += (_, _) => DeletePdfMarkWithUndo(mk);

                    ctx.Items.Add(editNote);
                    ctx.Items.Add(styleSub);
                    ctx.Items.Add(speakFrom);
                    ctx.Items.Add(new MenuFlyoutSeparator());
                    ctx.Items.Add(del);

                    var btn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(0),
                        UseSystemFocusVisuals = false,
                        ContextFlyout = ctx,
                        Content = sp,
                    };
                    btn.Click += (_, _) => _ = GoToPageAsync(pg);
                    PdfSideList.Children.Add(btn);
                }
                break;
            }

            case "search":
            {
                var box = new TextBox
                {
                    PlaceholderText = "在这个 PDF 里找",
                    Text = _searchQuery,
                    FontSize = 12.5,
                    CornerRadius = new CornerRadius(9),
                };
                var countTb = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(4, 0, 0, 0) };
                box.TextChanged += (_, _) =>
                {
                    _searchQuery = box.Text.Trim();
                    _searchDebounce.Stop();
                    _searchDebounce.Start();
                };
                PdfSideHeaderHost.Children.Add(box);
                PdfSideHeaderHost.Children.Add(countTb);
                box.Focus(FocusState.Programmatic);
                if (!string.IsNullOrEmpty(_searchQuery))
                    _ = ExecutePdfSearchAsync(_searchQuery);
                break;
            }
        }
        L10n.ApplyTree(PdfSideHeaderHost);
        L10n.ApplyTree(PdfSideList);
    }

    private async Task ExecutePdfSearchAsync(string query)
    {
        if (_sideMode != "search") return;
        PdfSideList.Children.Clear();
        var countTb = PdfSideHeaderHost.Children.OfType<TextBlock>().LastOrDefault();
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(_filePath))
        {
            if (countTb is not null) countTb.Text = "";
            return;
        }

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        string fp = _filePath;
        var hits = await Task.Run(() => PdfInfoReader.SearchText(fp, query));
        if (_sideMode != "search" || _searchQuery != query) return;
        _searchHits = hits;
        _searchHitIndex = hits.Count > 0 ? 0 : -1;

        if (countTb is not null)
            countTb.Text = L10n.T(hits.Count == 0 ? "没找到" : (hits.Count >= 350 ? "350 多处" : $"{hits.Count} 处"));

        for (int hi = 0; hi < hits.Count; hi++)
        {
            int hitIdx = hi;
            var (pg, charOff, matchLen, snippet) = hits[hi];
            int targetPage = pg;
            var tb = new TextBlock { FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
            int matchIdx = snippet.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (matchIdx >= 0)
            {
                if (matchIdx > 0) tb.Inlines.Add(new Run { Text = snippet[..matchIdx] });
                tb.Inlines.Add(new Run { Text = snippet.Substring(matchIdx, query.Length), FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(tint.Accent) });
                int after = matchIdx + query.Length;
                if (after < snippet.Length) tb.Inlines.Add(new Run { Text = snippet[after..] });
            }
            else
            {
                tb.Text = snippet;
            }

            var sp = new StackPanel { Spacing = 3, Padding = new Thickness(8, 7, 8, 7) };
            sp.Children.Add(new TextBlock { Text = L10n.T($"第 {targetPage + 1} 页"), FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3) });
            sp.Children.Add(tb);

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = sp,
            };
            btn.Click += (_, _) => _ = JumpToSearchHitAsync(hitIdx);
            PdfSideList.Children.Add(btn);
        }
    }

    private async Task JumpToSearchHitAsync(int index)
    {
        if (_searchHits.Count == 0) return;
        _searchHitIndex = ((index % _searchHits.Count) + _searchHits.Count) % _searchHits.Count;
        var (pg, charOff, matchLen, _) = _searchHits[_searchHitIndex];
        _activeSearchHighlight = (pg, charOff, matchLen);
        await GoToPageAsync(pg);
        GetOrLoadPageLayoutSync(pg);
        RefreshPageOverlay(pg);
    }

    private void AddSideEmpty(YYPalette p, string title, string sub)
    {
        var sp = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12, 72, 12, 12) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        PdfSideList.Children.Add(sp);
    }

    private MenuFlyout BuildPageContextMenu(int pageIndex)
    {
        var menu = new MenuFlyout();
        var markItem = new MenuFlyoutItem { Text = "摘录这一页的句子 / 写想法…" };
        markItem.Click += async (_, _) => { _currentPage = pageIndex; await AddCurrentPageNoteDialogAsync(); };
        var listenItem = new MenuFlyoutItem { Text = "从这一页开始朗读" };
        listenItem.Click += async (_, _) => { await GoToPageAsync(pageIndex); StartListeningCurrentPage(0); };
        var searchItem = new MenuFlyoutItem { Text = "书内搜索 (Ctrl+F)" };
        searchItem.Click += (_, _) => ToggleSidePanel("search");
        menu.Items.Add(markItem);
        menu.Items.Add(listenItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(searchItem);
        return menu;
    }

    private async Task AddCurrentPageNoteDialogAsync()
    {
        var sents = GetSentencesForPage(_currentPage);
        string seed = sents.FirstOrDefault()?.Text.Trim() ?? $"第 {_currentPage + 1} 页摘录";
        var quoteBox = new TextBox { Header = "原文摘录", Text = seed, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 110 };
        var noteBox = new TextBox { Header = "想法（可选）", PlaceholderText = "记下此刻的想法…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
        var sp = new StackPanel { Spacing = 10, Width = 380 };
        sp.Children.Add(quoteBox);
        sp.Children.Add(noteBox);

        var dlg = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = $"第 {_currentPage + 1} 页 · 划线与想法",
            Content = sp,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(quoteBox.Text))
        {
            string q = quoteBox.Text.Trim();
            string? n = string.IsNullOrWhiteSpace(noteBox.Text) ? null : noteBox.Text.Trim();
            _library.AddMark(_bookId, 0, new TextPoint((uint)_currentPage, 0), new TextPoint((uint)_currentPage, (uint)q.Length), q, MarkStyle.Yellow, n);
            RefreshPageOverlay(_currentPage);
            if (_sideMode == "notes") RenderSidePanel();
            App.CurrentApp.NotifyShelfRefresh();
        }
    }

    // ---------- PDF In-Page Text Selection, Highlight Overlays & Live Speech Tracking (TECH.md §9) ----------

    private void EnsurePageLayoutAsync(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= _pageCount || string.IsNullOrEmpty(_filePath)) return;
        if (_layoutCache.ContainsKey(pageIndex) || !_loadingLayouts.Add(pageIndex)) return;
        string fp = _filePath;
        _ = Task.Run(() => PdfInfoReader.ExtractPageLayout(fp, pageIndex)).ContinueWith(t =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _loadingLayouts.Remove(pageIndex);
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    _layoutCache[pageIndex] = t.Result;
                    RefreshPageOverlay(pageIndex);
                }
            });
        });
    }

    private PdfPageLayout GetOrLoadPageLayoutSync(int pageIndex)
    {
        if (_layoutCache.TryGetValue(pageIndex, out var cached)) return cached;
        if (string.IsNullOrEmpty(_filePath)) return new PdfPageLayout(pageIndex, "", []);
        var layout = PdfInfoReader.ExtractPageLayout(_filePath, pageIndex);
        _layoutCache[pageIndex] = layout;
        return layout;
    }

    private Canvas? GetOverlayForPage(int pageIndex)
    {
        if (_layoutMode == "scroll")
            return pageIndex >= 0 && pageIndex < _continuousOverlays.Count ? _continuousOverlays[pageIndex] : null;
        if (pageIndex == _currentPage) return SinglePageOverlay;
        if (_layoutMode == "double" && pageIndex == _currentPage + 1) return SecondPageOverlay;
        return null;
    }

    private void RefreshVisibleOverlays()
    {
        if (_layoutMode == "scroll")
        {
            int from = Math.Max(0, _currentPage - 2);
            int to = Math.Min(_pageCount - 1, _currentPage + 3);
            for (int i = from; i <= to; i++) RefreshPageOverlay(i);
        }
        else
        {
            RefreshPageOverlay(_currentPage);
            if (_layoutMode == "double" && _currentPage + 1 < _pageCount)
                RefreshPageOverlay(_currentPage + 1);
        }
    }

    private void RefreshPageOverlay(int pageIndex)
    {
        var canvas = GetOverlayForPage(pageIndex);
        if (canvas is null) return;
        canvas.Children.Clear();

        double w = canvas.Width > 0 ? canvas.Width : canvas.ActualWidth;
        double h = canvas.Height > 0 ? canvas.Height : canvas.ActualHeight;
        if (w <= 1 || h <= 1) return;

        if (!_layoutCache.TryGetValue(pageIndex, out var layout) || layout.Glyphs.Length == 0)
            return;

        var palette = ThemeHelper.Palette(_nightPaper);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, _nightPaper);

        // 1. Persistent user marks on this page — supports both single-page AND cross-page marks (Start.Sentence .. End.Sentence)
        IReadOnlyList<Mark> marks;
        try { marks = _library.GetMarks(_bookId); }
        catch { marks = []; }

        foreach (var mk in marks)
        {
            if (mk.Section != 0) continue;
            int pStart = (int)mk.Start.Sentence;
            int pEnd = Math.Max(pStart, (int)mk.End.Sentence);
            if (pageIndex < pStart || pageIndex > pEnd) continue;

            var (c0, c1) = ResolveMarkCharRangeOnPage(layout, mk, pageIndex);
            if (c1 <= c0) continue;
            var rects = PdfInfoReader.MergeLineRects(layout, c0, c1);
            DrawMarkRects(canvas, rects, w, h, mk.Style, palette, tint);
        }

        // 1b. Active search hit highlight on this page
        if (_activeSearchHighlight is var (hitPg, hitOff, hitLen) && hitPg == pageIndex && !string.IsNullOrEmpty(_searchQuery))
        {
            int idx = layout.Text.IndexOf(_searchQuery, Math.Clamp(hitOff - 16, 0, layout.Text.Length), StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = layout.Text.IndexOf(_searchQuery, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var hitRects = PdfInfoReader.MergeLineRects(layout, idx, Math.Min(layout.Text.Length, idx + _searchQuery.Length));
                var hitBrush = ThemeHelper.Brush(Color.FromArgb(128, 255, 196, 36));
                foreach (var r in hitRects)
                {
                    var box = new Rectangle
                    {
                        Width = Math.Max(3, r.W * w + 3),
                        Height = Math.Max(5, r.H * h + 2),
                        RadiusX = 3,
                        RadiusY = 3,
                        Fill = hitBrush,
                        Stroke = ThemeHelper.Brush(tint.Accent),
                        StrokeThickness = 1,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(box, Math.Max(0, r.X * w - 1.5));
                    Canvas.SetTop(box, Math.Max(0, r.Y * h - 1));
                    canvas.Children.Add(box);
                }
            }
        }

        // 2. Active speech sentence highlight
        if (_isListening && pageIndex == _currentPage && _activeSentenceIndex >= 0 && _activeSentenceIndex < _activeSentences.Length)
        {
            var sent = _activeSentences[_activeSentenceIndex];
            var rects = PdfInfoReader.MergeLineRects(layout, (int)sent.Start, (int)sent.End);
            var speechBrush = ThemeHelper.Brush(Color.FromArgb(_nightPaper ? (byte)64 : (byte)46, tint.Accent.R, tint.Accent.G, tint.Accent.B));
            foreach (var r in rects)
            {
                var box = new Rectangle
                {
                    Width = Math.Max(2, r.W * w + 2),
                    Height = Math.Max(4, r.H * h + 2),
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = speechBrush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(box, Math.Max(0, r.X * w - 1));
                Canvas.SetTop(box, Math.Max(0, r.Y * h - 1));
                canvas.Children.Add(box);
            }
        }

        // 3. Live selection highlight (supports cross-page Shift+click extension)
        if (_selPage >= 0 && _selStart >= 0)
        {
            int sp = _selPage;
            int ep = _selEndPage >= sp ? _selEndPage : sp;
            if (pageIndex >= sp && pageIndex <= ep)
            {
                int r0 = pageIndex == sp ? _selStart : 0;
                int r1 = pageIndex == ep ? _selEnd : layout.Text.Length;
                if (r1 > r0)
                {
                    var rects = PdfInfoReader.MergeLineRects(layout, r0, r1);
                    var selBrush = ThemeHelper.Brush(Color.FromArgb(_nightPaper ? (byte)84 : (byte)62, tint.Accent.R, tint.Accent.G, tint.Accent.B));
                    foreach (var r in rects)
                    {
                        var box = new Rectangle
                        {
                            Width = Math.Max(2, r.W * w),
                            Height = Math.Max(4, r.H * h + 1),
                            RadiusX = 2.5,
                            RadiusY = 2.5,
                            Fill = selBrush,
                            IsHitTestVisible = false,
                        };
                        Canvas.SetLeft(box, r.X * w);
                        Canvas.SetTop(box, r.Y * h);
                        canvas.Children.Add(box);
                    }
                }
            }
        }
    }

    private static (int Start, int End) ResolveMarkCharRangeOnPage(PdfPageLayout layout, Mark mk, int pageIndex)
    {
        int pStart = (int)mk.Start.Sentence;
        int pEnd = Math.Max(pStart, (int)mk.End.Sentence);
        int maxLen = layout.Text.Length;
        if (pStart != pEnd)
        {
            if (pageIndex == pStart) return (Math.Clamp((int)mk.Start.Offset, 0, maxLen), maxLen);
            if (pageIndex == pEnd) return (0, Math.Clamp((int)mk.End.Offset, 0, maxLen));
            return (0, maxLen);
        }
        return ResolveMarkCharRange(layout, mk);
    }

    private static (int Start, int End) ResolveMarkCharRange(PdfPageLayout layout, Mark mk)
    {
        int s = (int)mk.Start.Offset;
        int e = (int)mk.End.Offset;
        int maxLen = layout.Text.Length;
        if (s >= 0 && e > s && e <= maxLen)
        {
            // Verify slice roughly matches mark text; if legacy dialog saved (0..q.Length) on non-prefix text, fall back to substring lookup
            string slice = layout.Text.Substring(s, e - s);
            if (string.IsNullOrWhiteSpace(mk.Text) || slice.Trim().Equals(mk.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                return (s, e);
        }

        if (!string.IsNullOrWhiteSpace(mk.Text))
        {
            int idx = layout.Text.IndexOf(mk.Text.Trim(), StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return (idx, Math.Min(maxLen, idx + mk.Text.Trim().Length));
        }

        return (Math.Clamp(s, 0, maxLen), Math.Clamp(e, 0, maxLen));
    }

    private void DrawMarkRects(Canvas canvas, List<PdfLineRect> rects, double w, double h, MarkStyle style, YYPalette p, YYTintColors tint)
    {
        foreach (var r in rects)
        {
            double rx = r.X * w;
            double ry = r.Y * h;
            double rw = Math.Max(3, r.W * w);
            double rh = Math.Max(6, r.H * h);

            if (style is MarkStyle.Yellow or MarkStyle.Blue or MarkStyle.Pink)
            {
                Color baseCol = style switch
                {
                    MarkStyle.Blue => p.MkB,
                    MarkStyle.Pink => p.MkP,
                    _ => p.MkY,
                };
                byte alpha = _nightPaper ? (byte)82 : (byte)96;
                var rect = new Rectangle
                {
                    Width = rw + 2,
                    Height = rh + 2,
                    RadiusX = 2.5,
                    RadiusY = 2.5,
                    Fill = ThemeHelper.Brush(Color.FromArgb(alpha, baseCol.R, baseCol.G, baseCol.B)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, Math.Max(0, rx - 1));
                Canvas.SetTop(rect, Math.Max(0, ry - 1));
                canvas.Children.Add(rect);
            }
            else if (style == MarkStyle.Wave)
            {
                // True quadratic Bezier red sine wave (1:1 ReaderCards.swift WavyLineRenderer)
                var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(rx, ry + rh + 1.2) };
                double step = 4.0;
                double amp = 1.65;
                double cx = rx;
                bool up = true;
                while (cx < rx + rw)
                {
                    double nx = Math.Min(cx + step, rx + rw);
                    double mx = (cx + nx) * 0.5;
                    double cy = ry + rh + 1.2 + (up ? -amp : amp);
                    fig.Segments.Add(new QuadraticBezierSegment
                    {
                        Point1 = new Windows.Foundation.Point(mx, cy),
                        Point2 = new Windows.Foundation.Point(nx, ry + rh + 1.2),
                    });
                    cx = nx;
                    up = !up;
                }
                var geo = new PathGeometry();
                geo.Figures.Add(fig);
                var path = new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = geo,
                    Stroke = ThemeHelper.Brush(p.HlR),
                    StrokeThickness = 1.35,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false,
                };
                canvas.Children.Add(path);
            }
            else if (style == MarkStyle.Underline)
            {
                var line = new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = rx,
                    Y1 = ry + rh + 1,
                    X2 = rx + rw,
                    Y2 = ry + rh + 1,
                    Stroke = ThemeHelper.Brush(tint.Accent),
                    StrokeThickness = 1.6,
                    IsHitTestVisible = false,
                };
                canvas.Children.Add(line);
            }
            else if (style == MarkStyle.Dotted)
            {
                var line = new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = rx,
                    Y1 = ry + rh + 1,
                    X2 = rx + rw,
                    Y2 = ry + rh + 1,
                    Stroke = ThemeHelper.Brush(p.T2),
                    StrokeThickness = 1.5,
                    StrokeDashArray = [2.2, 2.2],
                    IsHitTestVisible = false,
                };
                canvas.Children.Add(line);
            }
        }
    }

    private void AttachOverlayEvents(Canvas overlay, Func<int> getPageIndex)
    {
        overlay.PointerPressed += (s, e) =>
        {
            var pt = e.GetCurrentPoint(overlay);
            if (!pt.Properties.IsLeftButtonPressed) return;
            int pageIdx = getPageIndex();
            double w = overlay.Width > 0 ? overlay.Width : overlay.ActualWidth;
            double h = overlay.Height > 0 ? overlay.Height : overlay.ActualHeight;
            if (w <= 1 || h <= 1) return;

            var layout = GetOrLoadPageLayoutSync(pageIdx);
            if (layout.Glyphs.Length == 0)
            {
                HidePdfSelectionCard();
                return;
            }

            double nx = Math.Clamp(pt.Position.X / w, 0, 1);
            double ny = Math.Clamp(pt.Position.Y / h, 0, 1);

            int glyphIdx = PdfInfoReader.HitTestGlyph(layout, nx, ny);
            if (glyphIdx < 0)
            {
                HidePdfSelectionCard();
                return;
            }

            // Shift+click on a later page extends an existing selection across pages!
            if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift) && _selPage >= 0 && pageIdx > _selPage)
            {
                _editingMark = null;
                _selEndPage = pageIdx;
                _selEnd = layout.Glyphs[glyphIdx].CharEnd;
                for (int pi = _selPage; pi <= _selEndPage; pi++)
                {
                    GetOrLoadPageLayoutSync(pi);
                    RefreshPageOverlay(pi);
                }
                ShowPdfSelectionCard(overlay, pageIdx, layout);
                e.Handled = true;
                return;
            }

            _editingMark = null;
            _selPage = pageIdx;
            _selEndPage = pageIdx;
            _selAnchorGlyph = glyphIdx;
            _selStart = layout.Glyphs[glyphIdx].CharStart;
            _selEnd = layout.Glyphs[glyphIdx].CharEnd;
            _selDragging = true;
            _activeDragCanvas = overlay;
            overlay.CapturePointer(e.Pointer);
            PdfSelectionCard.Visibility = Visibility.Collapsed;
            RefreshPageOverlay(pageIdx);
            e.Handled = true;
        };

        overlay.PointerMoved += (s, e) =>
        {
            if (!_selDragging || !ReferenceEquals(_activeDragCanvas, overlay)) return;
            int pageIdx = getPageIndex();
            if (pageIdx != _selPage || !_layoutCache.TryGetValue(pageIdx, out var layout)) return;
            double w = overlay.Width > 0 ? overlay.Width : overlay.ActualWidth;
            double h = overlay.Height > 0 ? overlay.Height : overlay.ActualHeight;
            if (w <= 1 || h <= 1) return;

            var pt = e.GetCurrentPoint(overlay).Position;
            int curGlyph = PdfInfoReader.HitTestGlyph(layout, Math.Clamp(pt.X / w, 0, 1), Math.Clamp(pt.Y / h, 0, 1));
            if (curGlyph < 0 || _selAnchorGlyph < 0) return;

            int g0 = Math.Min(_selAnchorGlyph, curGlyph);
            int g1 = Math.Max(_selAnchorGlyph, curGlyph);
            int nextStart = layout.Glyphs[g0].CharStart;
            int nextEnd = layout.Glyphs[g1].CharEnd;
            if (nextStart != _selStart || nextEnd != _selEnd)
            {
                _selStart = nextStart;
                _selEnd = nextEnd;
                RefreshPageOverlay(pageIdx);
            }
            e.Handled = true;
        };

        overlay.PointerReleased += (s, e) =>
        {
            if (!_selDragging || !ReferenceEquals(_activeDragCanvas, overlay)) return;
            _selDragging = false;
            _activeDragCanvas = null;
            try { overlay.ReleasePointerCapture(e.Pointer); } catch { }

            int pageIdx = getPageIndex();
            if (!_layoutCache.TryGetValue(pageIdx, out var layout)) return;
            double w = overlay.Width > 0 ? overlay.Width : overlay.ActualWidth;
            double h = overlay.Height > 0 ? overlay.Height : overlay.ActualHeight;
            var pt = e.GetCurrentPoint(overlay).Position;

            // Single click (1 char): check if user clicked an existing mark on this page -> open edit card!
            if (_selEnd - _selStart <= 1)
            {
                var hitMark = FindMarkAtPoint(pageIdx, layout, pt.X / Math.Max(1, w), pt.Y / Math.Max(1, h));
                if (hitMark is not null)
                {
                    _editingMark = hitMark;
                    var (c0, c1) = ResolveMarkCharRange(layout, hitMark);
                    _selPage = pageIdx;
                    _selStart = c0;
                    _selEnd = c1;
                    RefreshPageOverlay(pageIdx);
                    ShowPdfSelectionCard(overlay, pageIdx, layout);
                    e.Handled = true;
                    return;
                }
                HidePdfSelectionCard();
                return;
            }

            _editingMark = null;
            RefreshPageOverlay(pageIdx);
            ShowPdfSelectionCard(overlay, pageIdx, layout);
            e.Handled = true;
        };

        overlay.PointerCaptureLost += (s, e) =>
        {
            if (ReferenceEquals(_activeDragCanvas, overlay))
            {
                _selDragging = false;
                _activeDragCanvas = null;
            }
        };
    }

    private Mark? FindMarkAtPoint(int pageIndex, PdfPageLayout layout, double normX, double normY)
    {
        try
        {
            foreach (var mk in _library.GetMarks(_bookId))
            {
                if (mk.Section != 0) continue;
                int pStart = (int)mk.Start.Sentence;
                int pEnd = Math.Max(pStart, (int)mk.End.Sentence);
                if (pageIndex < pStart || pageIndex > pEnd) continue;
                var (c0, c1) = ResolveMarkCharRangeOnPage(layout, mk, pageIndex);
                foreach (var r in PdfInfoReader.MergeLineRects(layout, c0, c1))
                {
                    if (normX >= r.X - 0.008 && normX <= r.X + r.W + 0.008 &&
                        normY >= r.Y - 0.01 && normY <= r.Y + r.H + 0.012)
                        return mk;
                }
            }
        }
        catch { }
        return null;
    }

    private void ShowPdfSelectionCard(Canvas overlay, int pageIndex, PdfPageLayout layout)
    {
        int r0 = (_selEndPage > _selPage && pageIndex == _selEndPage) ? 0 : _selStart;
        var rects = PdfInfoReader.MergeLineRects(layout, r0, _selEnd);
        if (rects.Count == 0) { HidePdfSelectionCard(); return; }

        double ow = overlay.Width > 0 ? overlay.Width : overlay.ActualWidth;
        double oh = overlay.Height > 0 ? overlay.Height : overlay.ActualHeight;
        var first = rects[0];
        var last = rects[^1];

        try
        {
            var xform = overlay.TransformToVisual(CanvasHost);
            var topPt = xform.TransformPoint(new Windows.Foundation.Point((first.X + first.W * 0.5) * ow, first.Y * oh));
            var botPt = xform.TransformPoint(new Windows.Foundation.Point((last.X + last.W * 0.5) * ow, (last.Y + last.H) * oh));

            double cardW = 328;
            double cardH = _editingMark is not null && !string.IsNullOrWhiteSpace(_editingMark.Note) ? 148 : 96;
            double hostW = CanvasHost.ActualWidth > 0 ? CanvasHost.ActualWidth : 900;
            double hostH = CanvasHost.ActualHeight > 0 ? CanvasHost.ActualHeight : 640;

            double x = Math.Clamp(topPt.X - cardW * 0.5, 16, Math.Max(16, hostW - cardW - 16));
            double y = topPt.Y - cardH - 12;
            if (y < 60) y = Math.Min(hostH - cardH - 72, botPt.Y + 12);

            PdfSelectionCard.Margin = new Thickness(x, Math.Max(60, y), 0, 0);
        }
        catch
        {
            PdfSelectionCard.Margin = new Thickness(24, 72, 0, 0);
        }

        bool hasNote = _editingMark is not null && !string.IsNullOrWhiteSpace(_editingMark.Note);
        PdfSelNotePreviewBox.Visibility = hasNote ? Visibility.Visible : Visibility.Collapsed;
        PdfSelNotePreviewText.Text = hasNote ? _editingMark!.Note! : "";
        PdfSelDeleteBtn.Visibility = _editingMark is not null ? Visibility.Visible : Visibility.Collapsed;
        PdfSelectionCard.Visibility = Visibility.Visible;
    }

    private void HidePdfSelectionCard()
    {
        int p0 = _selPage;
        int p1 = Math.Max(p0, _selEndPage);
        _selPage = -1;
        _selEndPage = -1;
        _selStart = -1;
        _selEnd = -1;
        _selAnchorGlyph = -1;
        _editingMark = null;
        PdfSelectionCard.Visibility = Visibility.Collapsed;
        if (p0 >= 0)
        {
            for (int pi = p0; pi <= p1; pi++) RefreshPageOverlay(pi);
        }
    }

    private string GetSelectedPdfText()
    {
        if (_editingMark is not null) return _editingMark.Text;
        if (_selPage < 0) return "";
        int endPg = Math.Max(_selPage, _selEndPage);
        if (endPg == _selPage)
        {
            if (!_layoutCache.TryGetValue(_selPage, out var layout)) return "";
            int s = Math.Clamp(_selStart, 0, layout.Text.Length);
            int e = Math.Clamp(_selEnd, s, layout.Text.Length);
            return layout.Text.Substring(s, e - s).Replace("\r", "").Replace("\n", " ").Trim();
        }
        var parts = new List<string>();
        for (int pi = _selPage; pi <= endPg; pi++)
        {
            var lay = GetOrLoadPageLayoutSync(pi);
            int s = pi == _selPage ? Math.Clamp(_selStart, 0, lay.Text.Length) : 0;
            int e = pi == endPg ? Math.Clamp(_selEnd, 0, lay.Text.Length) : lay.Text.Length;
            if (e > s) parts.Add(lay.Text.Substring(s, e - s).Replace("\r", "").Replace("\n", " ").Trim());
        }
        return string.Join(" ", parts.Where(x => x.Length > 0));
    }

    private void ApplyPdfMarkStyle(MarkStyle style)
    {
        if (_editingMark is not null)
        {
            int p0 = (int)_editingMark.Start.Sentence;
            int p1 = Math.Max(p0, (int)_editingMark.End.Sentence);
            _library.SetMarkStyle(_bookId, _editingMark.Id, style);
            HidePdfSelectionCard();
            for (int pi = p0; pi <= p1; pi++) RefreshPageOverlay(pi);
            if (_sideMode == "notes") RenderSidePanel();
            App.CurrentApp.NotifyShelfRefresh();
            return;
        }

        string text = GetSelectedPdfText();
        if (_selPage < 0 || string.IsNullOrWhiteSpace(text)) { HidePdfSelectionCard(); return; }
        int pageStart = _selPage;
        int pageEnd = Math.Max(_selPage, _selEndPage);
        _library.AddMark(
            _bookId,
            0,
            new TextPoint((uint)pageStart, (uint)Math.Max(0, _selStart)),
            new TextPoint((uint)pageEnd, (uint)Math.Max(0, _selEnd)),
            text,
            style,
            null);
        HidePdfSelectionCard();
        for (int pi = pageStart; pi <= pageEnd; pi++) RefreshPageOverlay(pi);
        if (_sideMode == "notes") RenderSidePanel();
        App.CurrentApp.NotifyShelfRefresh();
    }

    private void PdfMarkStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        var style = tag switch
        {
            "blue" => MarkStyle.Blue,
            "pink" => MarkStyle.Pink,
            "wave" => MarkStyle.Wave,
            "underline" => MarkStyle.Underline,
            "dotted" => MarkStyle.Dotted,
            _ => MarkStyle.Yellow,
        };
        ApplyPdfMarkStyle(style);
    }

    private void PdfSelDelete_Click(object sender, RoutedEventArgs e)
    {
        var mk = _editingMark;
        if (mk is null) return;
        HidePdfSelectionCard();
        DeletePdfMarkWithUndo(mk);
    }

    private async void PdfSelNote_Click(object sender, RoutedEventArgs e)
    {
        if (_editingMark is not null)
        {
            var mk = _editingMark;
            HidePdfSelectionCard();
            await EditPdfMarkNoteAsync(mk);
            return;
        }

        string text = GetSelectedPdfText();
        int pageStart = _selPage;
        int pageEnd = Math.Max(_selPage, _selEndPage);
        int c0 = Math.Max(0, _selStart);
        int c1 = Math.Max(0, _selEnd);
        HidePdfSelectionCard();
        if (pageStart < 0 || string.IsNullOrWhiteSpace(text)) return;

        var noteBox = new TextBox { PlaceholderText = "记下此刻的想法…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 84, Width = 360 };
        var dlg = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "写想法",
            Content = noteBox,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            string? note = string.IsNullOrWhiteSpace(noteBox.Text) ? null : noteBox.Text.Trim();
            _library.AddMark(_bookId, 0, new TextPoint((uint)pageStart, (uint)c0), new TextPoint((uint)pageEnd, (uint)c1), text, MarkStyle.Yellow, note);
            for (int pi = pageStart; pi <= pageEnd; pi++) RefreshPageOverlay(pi);
            if (_sideMode == "notes") RenderSidePanel();
            App.CurrentApp.NotifyShelfRefresh();
        }
    }

    private void PdfSelCopy_Click(object sender, RoutedEventArgs e)
    {
        string text = GetSelectedPdfText();
        HidePdfSelectionCard();
        if (string.IsNullOrWhiteSpace(text)) return;
        var pkg = new DataPackage();
        pkg.SetText(text);
        Clipboard.SetContent(pkg);
        ShowBriefToast("已复制选中文字");
    }

    private async void PdfSelSpeak_Click(object sender, RoutedEventArgs e)
    {
        int pageIdx = _selPage >= 0 ? _selPage : _currentPage;
        int charAt = Math.Max(0, _selStart);
        HidePdfSelectionCard();
        if (pageIdx != _currentPage) await GoToPageAsync(pageIdx);

        var sents = GetSentencesForPage(pageIdx);
        int hitIdx = 0;
        for (int i = 0; i < sents.Length; i++)
        {
            if (charAt >= (int)sents[i].Start && charAt < (int)sents[i].End) { hitIdx = i; break; }
            if (charAt >= (int)sents[i].Start) hitIdx = i;
        }
        StartListeningCurrentPage(hitIdx);
    }

    private async void PdfSelLookup_Click(object sender, RoutedEventArgs e)
    {
        int pageIdx = _selPage >= 0 ? _selPage : _currentPage;
        string text = GetSelectedPdfText();
        HidePdfSelectionCard();
        if (string.IsNullOrWhiteSpace(text)) return;

        string word = text.Split([' ', '\t', ',', '.', ';', ':', '!', '?', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
        var res = _library.LookUpWordRich(word);
        var sp = new StackPanel { Spacing = 8, Width = 380 };
        if (res is not null)
        {
            string head = string.IsNullOrWhiteSpace(res.Phonetic) ? res.Headword : $"{res.Headword}  /{res.Phonetic}/";
            sp.Children.Add(new TextBlock { Text = head, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = res.Summary, FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
            sp.Children.Add(new TextBlock { Text = $"词典来源：{res.Source}", FontSize = 11, Opacity = 0.65 });
        }
        else
        {
            sp.Children.Add(new TextBlock { Text = $"本地词典未找到 “{word}”", FontSize = 13, TextWrapping = TextWrapping.Wrap });
        }

        var dlg = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "查词",
            Content = sp,
            PrimaryButtonText = res is not null ? "加入生词本" : "",
            CloseButtonText = "关闭",
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && res is not null)
        {
            try
            {
                _library.AddWord(res.Headword, res.Phonetic, res.Summary, _bookId, 0, (uint)Math.Max(0, pageIdx), text);
                ShowBriefToast($"已加入生词本：{res.Headword}");
                App.CurrentApp.NotifyShelfRefresh();
            }
            catch { }
        }
    }

    private void ShowBriefToast(string message)
    {
        _toastUndo = null;
        ToastUndoBtn.Visibility = Visibility.Collapsed;
        ToastText.Text = message;
        ToastBanner.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBanner.Visibility = Visibility.Collapsed; ToastUndoBtn.Visibility = Visibility.Visible; };
        _toastTimer.Start();
    }

    private async Task EditPdfMarkNoteAsync(Mark mk)
    {
        var noteBox = new TextBox { Text = mk.Note ?? "", PlaceholderText = "写下你的想法…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 84, Width = 360 };
        var dlg = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "想法",
            Content = noteBox,
            PrimaryButtonText = "保存",
            SecondaryButtonText = string.IsNullOrWhiteSpace(mk.Note) ? "" : "删掉想法",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            _library.SetMarkNote(_bookId, mk.Id, string.IsNullOrWhiteSpace(noteBox.Text) ? null : noteBox.Text.Trim());
            RenderSidePanel();
            App.CurrentApp.NotifyShelfRefresh();
        }
        else if (res == ContentDialogResult.Secondary)
        {
            _library.SetMarkNote(_bookId, mk.Id, null);
            RenderSidePanel();
            App.CurrentApp.NotifyShelfRefresh();
        }
    }

    private void DeletePdfMarkWithUndo(Mark mk)
    {
        string delId = mk.Id;
        int p0 = (int)mk.Start.Sentence;
        int p1 = Math.Max(p0, (int)mk.End.Sentence);
        _library.DeleteMark(_bookId, delId);
        for (int pi = p0; pi <= p1; pi++) RefreshPageOverlay(pi);
        if (_sideMode == "notes") RenderSidePanel();
        App.CurrentApp.NotifyShelfRefresh();

        _toastUndo = () =>
        {
            try
            {
                _library.Core.RestoreMark(_bookId, delId);
                for (int pi = p0; pi <= p1; pi++) RefreshPageOverlay(pi);
                if (_sideMode == "notes") RenderSidePanel();
                App.CurrentApp.NotifyShelfRefresh();
            }
            catch { }
        };
        ToastUndoBtn.Visibility = Visibility.Visible;
        ToastText.Text = "已删除划线";
        ToastBanner.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBanner.Visibility = Visibility.Collapsed; };
        _toastTimer.Start();
    }

    private void ToastUndo_Click(object sender, RoutedEventArgs e)
    {
        _toastTimer?.Stop();
        ToastBanner.Visibility = Visibility.Collapsed;
        var u = _toastUndo;
        _toastUndo = null;
        u?.Invoke();
    }

    // ---------- Read-Aloud & Capsule Controls ----------

    private PageSentence[] GetSentencesForPage(int pageIndex)
    {
        if (_sentenceCache.TryGetValue(pageIndex, out var cached)) return cached;
        if (string.IsNullOrEmpty(_filePath)) return [];
        var layout = GetOrLoadPageLayoutSync(pageIndex);
        string raw = layout.Text;
        var list = string.IsNullOrWhiteSpace(raw) ? [] : YuyueCoreMethods.PageSentences(raw);
        _sentenceCache[pageIndex] = list;
        return list;
    }

    private int FindSentenceIndexForOffset(int pageIndex, int charOffset)
    {
        var sents = GetSentencesForPage(pageIndex);
        int hit = 0;
        for (int i = 0; i < sents.Length; i++)
        {
            if (charOffset >= (int)sents[i].Start && charOffset < (int)sents[i].End) return i;
            if (charOffset >= (int)sents[i].Start) hit = i;
        }
        return hit;
    }

    private void OnAudioMarkHeardRequested(int _)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isListening || _activeSentenceIndex < 0 || _activeSentenceIndex >= _activeSentences.Length) return;
            var s = _activeSentences[_activeSentenceIndex];
            try
            {
                _library.Core.MarkHeard(
                    _bookId,
                    0,
                    new TextPoint((uint)_currentPage, s.Start),
                    new TextPoint((uint)_currentPage, s.End),
                    s.Text,
                    MarkStyle.Yellow);
                RefreshPageOverlay(_currentPage);
                if (_sideMode == "notes") RenderSidePanel();
                ShowBriefToast("已划下正在听的这一句");
            }
            catch { }
        });
    }

    private async void StartListeningCurrentPage(int startIndex)
    {
        int targetPage = _currentPage;
        var sents = GetSentencesForPage(targetPage);
        // Skip blank / scanned image pages automatically (up to 8 pages ahead)
        int scanned = 0;
        while (sents.Length == 0 && targetPage + 1 < _pageCount && scanned < 8)
        {
            targetPage++;
            scanned++;
            sents = GetSentencesForPage(targetPage);
        }
        if (sents.Length == 0)
        {
            _isListening = false;
            CapsuleSubText.Text = "附近页面没有可提取的正文文字（可能是扫描版）";
            return;
        }
        if (targetPage != _currentPage)
        {
            await GoToPageAsync(targetPage);
            startIndex = 0;
        }

        _activeSentences = sents;
        _listeningPage = targetPage;
        _isListening = true;
        SpeakSentenceAt(Math.Clamp(startIndex, 0, _activeSentences.Length - 1));
    }

    private void SpeakSentenceAt(int index)
    {
        if (!_isListening || index < 0 || index >= _activeSentences.Length) return;
        _activeSentenceIndex = index;
        _listeningPage = _currentPage;
        var s = _activeSentences[index];
        string lang = (!string.IsNullOrWhiteSpace(_detail.Summary.Language) && !_detail.Summary.Language.Equals("und", StringComparison.OrdinalIgnoreCase))
            ? _detail.Summary.Language
            : PdfInfoReader.DetectLanguage(s.Text);
        _audio.SetVoice(_library.Prefs.GetVoiceForLanguage(lang));
        _ = _audio.SpeakSentenceAsync(s.Text, _detail.Summary.Title, $"第 {_currentPage + 1} 页", _bookId, lang);
        ApplyTintAndChrome();
    }

    private void OnAudioSentenceFinished()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_isListening) return;
            if (_sleepMode == -2)
            {
                _sleepMode = 0;
                StopListening();
                ShowBriefToast("本句读完已自动暂停");
                return;
            }

            if (_activeSentenceIndex + 1 < _activeSentences.Length)
            {
                SpeakSentenceAt(_activeSentenceIndex + 1);
            }
            else if (_sleepMode == -1)
            {
                _sleepMode = 0;
                StopListening();
                ShowBriefToast("本页读完已自动暂停");
            }
            else if (_currentPage + 1 < _pageCount)
            {
                await GoToPageAsync(_currentPage + 1);
                StartListeningCurrentPage(0);
            }
            else
            {
                StopListening();
            }
        });
    }

    private void OnAudioPlayStateChanged(bool playing) => DispatcherQueue.TryEnqueue(ApplyTintAndChrome);

    private void StopListening()
    {
        _isListening = false;
        _activeSentenceIndex = -1;
        _listeningPage = -1;
        _audio.Stop();
        ApplyTintAndChrome();
    }

    private void CapsuleTogglePlay_Click(object sender, RoutedEventArgs e)
    {
        if (_isListening)
        {
            // If paused and user flipped to a different page, start from the new page instead of old page!
            if (!_audio.IsPlaying && _listeningPage != _currentPage)
            {
                StartListeningCurrentPage(0);
                return;
            }
            _audio.TogglePause();
            ApplyTintAndChrome();
        }
        else
        {
            StartListeningCurrentPage(0);
        }
    }

    private async void CapsuleStepPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_isListening)
        {
            if (_activeSentenceIndex > 0)
            {
                SpeakSentenceAt(_activeSentenceIndex - 1);
            }
            else if (_currentPage > 0)
            {
                // Cross-page backward sentence step (skip blank pages)
                int prevPg = _currentPage - 1;
                var prevSents = GetSentencesForPage(prevPg);
                while (prevSents.Length == 0 && prevPg > 0)
                {
                    prevPg--;
                    prevSents = GetSentencesForPage(prevPg);
                }
                if (prevSents.Length > 0)
                {
                    await GoToPageAsync(prevPg);
                    _activeSentences = prevSents;
                    _listeningPage = prevPg;
                    SpeakSentenceAt(prevSents.Length - 1);
                }
            }
        }
        else
        {
            int step = _layoutMode == "double" ? 2 : 1;
            if (_currentPage > 0) await GoToPageAsync(Math.Max(0, _currentPage - step));
        }
    }

    private async void CapsuleStepNext_Click(object sender, RoutedEventArgs e)
    {
        if (_isListening)
        {
            if (_activeSentenceIndex + 1 < _activeSentences.Length)
            {
                SpeakSentenceAt(_activeSentenceIndex + 1);
            }
            else if (_currentPage + 1 < _pageCount)
            {
                await GoToPageAsync(_currentPage + 1);
                StartListeningCurrentPage(0);
            }
        }
        else
        {
            int step = _layoutMode == "double" ? 2 : 1;
            if (_currentPage + 1 < _pageCount) await GoToPageAsync(Math.Min(_pageCount - 1, _currentPage + step));
        }
    }

    // ---------- Input & Wheel ----------

    private void BackToShelf_Click(object sender, RoutedEventArgs e) => App.CurrentApp.ShowShelf();

    private async Task AdjustZoomAsync(double delta)
    {
        double next = Math.Clamp(Math.Round((_zoom + delta) * 20) / 20.0, 0.5, 2.5);
        if (Math.Abs(next - _zoom) < 0.01) return;
        _zoom = next;
        await RebuildViewModeAsync();
    }

    private void Paged_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(PagedScroller);
        int delta = pt.Properties.MouseWheelDelta;
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            e.Handled = true;
            _ = AdjustZoomAsync(delta > 0 ? 0.1 : -0.1);
            return;
        }

        if (PagedScroller.ScrollableHeight <= 4)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastWheelTurnUtc).TotalMilliseconds < 180) { e.Handled = true; return; }
            _lastWheelTurnUtc = now;
            e.Handled = true;
            int step = _layoutMode == "double" ? 2 : 1;
            if (delta < 0 && _currentPage + 1 < _pageCount) _ = GoToPageAsync(Math.Min(_pageCount - 1, _currentPage + step));
            else if (delta > 0 && _currentPage > 0) _ = GoToPageAsync(Math.Max(0, _currentPage - step));
        }
    }

    private void Continuous_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            var pt = e.GetCurrentPoint(ContinuousScroller);
            e.Handled = true;
            _ = AdjustZoomAsync(pt.Properties.MouseWheelDelta > 0 ? 0.1 : -0.1);
        }
    }

    private void ContinuousScroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_layoutMode != "scroll" || _continuousImages.Count == 0) return;
        double y = ContinuousScroller.VerticalOffset + ContinuousScroller.ViewportHeight * 0.35;
        double acc = 0;
        int hit = 0;
        for (int i = 0; i < _continuousImages.Count; i++)
        {
            double h = _continuousImages[i].Height + 24;
            if (y < acc + h) { hit = i; break; }
            acc += h;
            hit = i;
        }
        if (hit != _currentPage)
        {
            _currentPage = hit;
            SaveCurrentProgress();
            UpdateStatusText();
            ApplyTintAndChrome();
            if (_sideMode == "toc") RenderSidePanel();
            _ = RenderContinuousWindowAsync(hit);
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox) return;

        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && shift && e.Key == VirtualKey.F) { ToggleFullScreen(); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.F) { ToggleSidePanel("search"); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.K) { _ = QuickSearchDialog.ShowAsync(RootGrid.XamlRoot, _library, _bookId); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.G && _searchHits.Count > 0) { _ = JumpToSearchHitAsync(_searchHitIndex + (shift ? -1 : 1)); e.Handled = true; return; }
        if (ctrl && e.Key == (VirtualKey)188) { App.CurrentApp.OpenSettings(); e.Handled = true; return; }
        if (ctrl && alt && e.Key == VirtualKey.T) { ToggleSidePanel("toc"); e.Handled = true; return; }
        if (ctrl && alt && e.Key == VirtualKey.N) { ToggleSidePanel("notes"); e.Handled = true; return; }
        if (ctrl && alt && (e.Key == VirtualKey.Up || e.Key == VirtualKey.Down) && _outline.Count > 0)
        {
            int curOut = -1;
            for (int i = 0; i < _outline.Count; i++) if (_outline[i].PageIndex <= _currentPage) curOut = i;
            int nextOut = e.Key == VirtualKey.Up ? Math.Max(0, curOut - 1) : Math.Min(_outline.Count - 1, curOut + 1);
            _ = GoToPageAsync(_outline[nextOut].PageIndex);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == VirtualKey.D) { TogglePageBookmark(); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.Z && ToastBanner.Visibility == Visibility.Visible) { ToastUndo_Click(sender, e); e.Handled = true; return; }
        if (ctrl && (e.Key == (VirtualKey)187 || e.Key == VirtualKey.Add)) { _ = AdjustZoomAsync(0.15); e.Handled = true; return; }
        if (ctrl && (e.Key == (VirtualKey)189 || e.Key == VirtualKey.Subtract)) { _ = AdjustZoomAsync(-0.15); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.Number0) { _zoom = 1.0; _ = RebuildViewModeAsync(); e.Handled = true; return; }
        if (ctrl && e.Key == (VirtualKey)219) { CapsuleStepPrev_Click(sender, e); e.Handled = true; return; }
        if (ctrl && e.Key == (VirtualKey)221) { CapsuleStepNext_Click(sender, e); e.Handled = true; return; }

        switch (e.Key)
        {
            case VirtualKey.Space:
                CapsuleTogglePlay_Click(sender, e); e.Handled = true; break;
            case VirtualKey.F11:
                ToggleFullScreen(); e.Handled = true; break;
            case VirtualKey.Escape:
                if (PdfSelectionCard.Visibility == Visibility.Visible) HidePdfSelectionCard();
                else if (_isFullScreen) ToggleFullScreen();
                else if (_sideMode is not null) ToggleSidePanel(_sideMode);
                e.Handled = true;
                break;
            case VirtualKey.Right:
            case VirtualKey.PageDown:
                if (_layoutMode != "scroll") { CapsuleStepNext_Click(sender, e); e.Handled = true; }
                break;
            case VirtualKey.Left:
            case VirtualKey.PageUp:
                if (_layoutMode != "scroll") { CapsuleStepPrev_Click(sender, e); e.Handled = true; }
                break;
            case VirtualKey.Number1:
            case VirtualKey.Number2:
            case VirtualKey.Number3:
            case VirtualKey.Number4:
                if (PdfSelectionCard.Visibility == Visibility.Visible)
                {
                    ApplyPdfMarkStyle(e.Key switch
                    {
                        VirtualKey.Number2 => MarkStyle.Blue,
                        VirtualKey.Number3 => MarkStyle.Pink,
                        VirtualKey.Number4 => MarkStyle.Wave,
                        _ => MarkStyle.Yellow,
                    });
                }
                else
                {
                    _ = AddCurrentPageNoteDialogAsync();
                }
                e.Handled = true;
                break;
        }
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        try { AppWindow.SetPresenter(_isFullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped); }
        catch { }
        TopBar.Visibility = _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        FullScreenProgressBar.Visibility = _isFullScreen ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatusText();
    }

    private void TogglePageBookmark()
    {
        try
        {
            var existing = _library.GetBookmarks(_bookId).FirstOrDefault(b => b.Section == 0 && (int)b.Sentence == _currentPage);
            if (existing is not null)
            {
                string delId = existing.Id;
                _library.DeleteBookmark(_bookId, delId);
                _toastUndo = () => { try { _library.Core.RestoreBookmark(_bookId, delId); } catch { } };
                ToastUndoBtn.Visibility = Visibility.Visible;
                ToastText.Text = $"已移掉第 {_currentPage + 1} 页书签";
                ToastBanner.Visibility = Visibility.Visible;
                _toastTimer?.Stop();
                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBanner.Visibility = Visibility.Collapsed; };
                _toastTimer.Start();
            }
            else
            {
                _library.AddBookmark(_bookId, 0, (uint)Math.Max(0, _currentPage));
                ShowBriefToast($"已给第 {_currentPage + 1} 页加上书签");
            }
        }
        catch { }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _readingTimer.Stop();
        _sleepTimer.Stop();
        _searchDebounce.Stop();
        SaveCurrentProgress();
        _audio.Dispose();
    }
}
