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
    private DateTime _lastWheelTurnUtc = DateTime.MinValue;

    private IReadOnlyList<(string Title, int PageIndex, int Level)> _outline = [];
    private readonly List<Image> _continuousImages = [];
    private readonly HashSet<int> _renderedContinuous = [];

    // TTS state
    private readonly Dictionary<int, PageSentence[]> _sentenceCache = new();
    private PageSentence[] _activeSentences = [];
    private int _activeSentenceIndex = -1;
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

        _nightPaper = _library.Prefs.PdfNightBooks.Contains(bookId) || _library.Prefs.PdfNight;

        InitializeComponent();
        Title = _detail.Summary.Title;
        BookTitleText.Text = _detail.Summary.Title;
        ThemeHelper.ResizeWindow(this, 1080, 760);
        SetTitleBar(TitleDragRegion);
        ThemeHelper.ApplyElevation(PdfSidePanel, PageShadowReceiver, 16f);
        ThemeHelper.ApplyElevation(PdfCapsule, PageShadowReceiver, 14f);
        ThemeHelper.ApplyElevation(ToastBanner, PageShadowReceiver, 12f);

        _audio = new AudioPlayer(_library);
        _audio.SetRate(_library.Prefs.SpeechRate);
        _audio.SetVoice(_library.Prefs.VoiceId);
        _audio.SentenceFinished += OnAudioSentenceFinished;
        _audio.PlayStateChanged += OnAudioPlayStateChanged;

        _readingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _readingTimer.Tick += (_, _) =>
        {
            try
            {
                _library.AddTime(_bookId, 0, _isListening ? 0u : 30u, _isListening ? 30u : 0u);
                if (_sleepUntilUtc is DateTime until && DateTime.UtcNow >= until)
                {
                    _sleepUntilUtc = null;
                    _sleepMode = 0;
                    StopListening();
                }
            }
            catch { }
        };
        _readingTimer.Start();

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

    public void JumpToPage(int pageZeroBased)
    {
        if (_pageCount <= 0) return;
        _ = GoToPageAsync(Math.Clamp(pageZeroBased, 0, _pageCount - 1));
    }

    public void TriggerListen() => StartListeningCurrentPage(0);

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
        ToastUndoBtn.Content = YYIcons.Make("arrow-undo", 11.5, t1);

        UpdateCapsuleState();
        ThemeHelper.ConfigureTitleBar(this, dark);
    }

    private void UpdateCapsuleState()
    {
        if (_pageCount <= 0) return;
        if (_isListening && _activeSentences.Length > 0)
        {
            int cur = Math.Clamp(_activeSentenceIndex + 1, 1, _activeSentences.Length);
            int remSentences = Math.Max(0, _activeSentences.Length - cur);
            int minsLeft = Math.Max(1, (int)Math.Ceiling(remSentences * 3.8 / 60.0 / Math.Max(0.5, _library.Prefs.SpeechRate)));
            CapsuleTitleText.Text = $"第 {_currentPage + 1} 页 · 第 {cur} / {_activeSentences.Length} 句";
            CapsuleSubText.Text = $"这一页还剩 {minsLeft} 分钟 · {_library.Prefs.SpeechRate:0.#}×";
        }
        else
        {
            string pageRange = (_layoutMode == "double" && _currentPage + 1 < _pageCount)
                ? $"第 {_currentPage + 1}–{_currentPage + 2} / {_pageCount} 页"
                : $"第 {_currentPage + 1} / {_pageCount} 页";
            CapsuleTitleText.Text = pageRange;
            CapsuleSubText.Text = "点播放从这一页读起";
        }
        CapsulePrevBtn.IsEnabled = _isListening ? _activeSentenceIndex > 0 : _currentPage > 0;
        CapsuleNextBtn.IsEnabled = _isListening ? _activeSentenceIndex + 1 < _activeSentences.Length : _currentPage + 1 < _pageCount;
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
            if (twoUp)
            {
                SecondPageImage.Width = displayW;
                SecondPageImage.Height = displayW * aspect;
            }
        }
        catch { }
    }

    private void BuildContinuousPlaceholders()
    {
        ContinuousStack.Children.Clear();
        _continuousImages.Clear();
        _renderedContinuous.Clear();
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
            var img = new Image { Width = baseW, Height = baseW * defaultAspect, Stretch = Stretch.Uniform };
            _continuousImages.Add(img);
            int pageIdx = i;
            var frame = new Border
            {
                BorderBrush = ThemeHelper.Brush(hairline),
                BorderThickness = new Thickness(0, 0, 0, i + 1 < _pageCount ? 1 : 0),
                Padding = new Thickness(0, 0, 0, 12),
                ContextFlyout = BuildPageContextMenu(pageIdx),
                Child = img,
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
            if (!_renderedContinuous.Add(i)) continue;
            var bmp = await RenderPageBitmapAsync(i, _zoom);
            if (bmp is not null && i < _continuousImages.Count)
                _continuousImages[i].Source = bmp;
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
                Height = 58,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = ThemeHelper.Brush(p.Inset),
                BorderBrush = on ? ThemeHelper.Brush(p.T1) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(9),
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
            _library.Prefs.PdfNight = _nightPaper;
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
            Background = ThemeHelper.Brush(p.Inset),
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
        }
        else
        {
            _sideMode = mode;
            SidePanelColumn.Width = new GridLength(314);
            PdfSidePanel.Visibility = Visibility.Visible;
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
                    AddSideEmpty(p, "还没有划线", "右键书页或点右上角「+」即可摘录句子并写想法");
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
                    foreach (var (st, sName) in new[] { (MarkStyle.Yellow, "黄色"), (MarkStyle.Blue, "蓝色"), (MarkStyle.Pink, "粉色"), (MarkStyle.Wave, "波浪线") })
                    {
                        var ms = st;
                        var si = new MenuFlyoutItem { Text = (mk.Style == ms ? "✓ " : "") + sName };
                        si.Click += (_, _) => { _library.SetMarkStyle(_bookId, mk.Id, ms); RenderSidePanel(); };
                        styleSub.Items.Add(si);
                    }

                    var speakFrom = new MenuFlyoutItem { Text = "从这听" };
                    speakFrom.Click += async (_, _) => { await GoToPageAsync(pg); StartListeningCurrentPage(0); };

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

        if (countTb is not null)
            countTb.Text = hits.Count == 0 ? "没找到" : (hits.Count >= 300 ? "300 多处" : $"{hits.Count} 处");

        foreach (var (pg, snippet) in hits.Take(300))
        {
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
            sp.Children.Add(new TextBlock { Text = $"第 {targetPage + 1} 页", FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3) });
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
            btn.Click += (_, _) => _ = GoToPageAsync(targetPage);
            PdfSideList.Children.Add(btn);
        }
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
            if (_sideMode == "notes") RenderSidePanel();
            App.CurrentApp.NotifyShelfRefresh();
        }
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
        _library.DeleteMark(_bookId, mk.Id);
        if (_sideMode == "notes") RenderSidePanel();
        App.CurrentApp.NotifyShelfRefresh();

        _toastUndo = () =>
        {
            _library.AddMark(_bookId, mk.Section, mk.Start, mk.End, mk.Text, mk.Style, mk.Note);
            if (_sideMode == "notes") RenderSidePanel();
            App.CurrentApp.NotifyShelfRefresh();
        };
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
        string raw = PdfInfoReader.ExtractPageText(_filePath, pageIndex);
        var list = string.IsNullOrWhiteSpace(raw) ? [] : YuyueCoreMethods.PageSentences(raw);
        _sentenceCache[pageIndex] = list;
        return list;
    }

    private void StartListeningCurrentPage(int startIndex)
    {
        _activeSentences = GetSentencesForPage(_currentPage);
        if (_activeSentences.Length == 0)
        {
            _isListening = false;
            CapsuleSubText.Text = "这一页没有可提取的正文文字";
            return;
        }

        _isListening = true;
        SpeakSentenceAt(Math.Clamp(startIndex, 0, _activeSentences.Length - 1));
    }

    private void SpeakSentenceAt(int index)
    {
        if (!_isListening || index < 0 || index >= _activeSentences.Length) return;
        _activeSentenceIndex = index;
        var s = _activeSentences[index];
        _ = _audio.SpeakSentenceAsync(s.Text, _detail.Summary.Title, $"第 {_currentPage + 1} 页", _bookId, _detail.Summary.Language);
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
        _audio.Stop();
        ApplyTintAndChrome();
    }

    private void CapsuleTogglePlay_Click(object sender, RoutedEventArgs e)
    {
        if (_isListening)
        {
            _audio.TogglePause();
            ApplyTintAndChrome();
        }
        else
        {
            StartListeningCurrentPage(0);
        }
    }

    private void CapsuleStepPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_isListening)
        {
            if (_activeSentenceIndex > 0) SpeakSentenceAt(_activeSentenceIndex - 1);
        }
        else
        {
            int step = _layoutMode == "double" ? 2 : 1;
            if (_currentPage > 0) _ = GoToPageAsync(Math.Max(0, _currentPage - step));
        }
    }

    private void CapsuleStepNext_Click(object sender, RoutedEventArgs e)
    {
        if (_isListening)
        {
            if (_activeSentenceIndex + 1 < _activeSentences.Length) SpeakSentenceAt(_activeSentenceIndex + 1);
        }
        else
        {
            int step = _layoutMode == "double" ? 2 : 1;
            if (_currentPage + 1 < _pageCount) _ = GoToPageAsync(Math.Min(_pageCount - 1, _currentPage + step));
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
        if (ctrl && e.Key == VirtualKey.F) { ToggleSidePanel("search"); e.Handled = true; return; }
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
                if (_isFullScreen) ToggleFullScreen();
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
                _ = AddCurrentPageNoteDialogAsync();
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

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _readingTimer.Stop();
        _searchDebounce.Stop();
        SaveCurrentProgress();
        _audio.Dispose();
    }
}
