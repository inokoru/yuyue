using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinRT.Interop;
using Yuyue.Audiobook;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Shelf;
using Yuyue.Speech;
using TimeSpan = System.TimeSpan;

namespace Yuyue.Reader;

public sealed partial class ReaderWindow : Window
{
    private readonly LibraryService _library;
    private readonly string _bookId;
    private BookDetail _detail;
    private readonly ReaderWebViewHost _host;
    private readonly AudioPlayer _audio;
    private readonly DispatcherTimer _timeTracker;

    private ReaderSettings _settings;
    private bool _ownSettings;
    private uint _currentSection;
    private int _currentPage;
    private int _currentSentence;
    private int _sectionSentenceCount;
    private SectionLayout? _layout;

    private int? _pendingSentence;
    private string? _pendingAnchor;
    private bool _pendingEndOfSection;
    private bool _pendingAutoSpeak;

    private string? _activePanel;   // toc | notes | search | companion | cast
    private string _tocTab = "toc"; // toc | bookmarks
    private string _notesTab = "marks"; // marks | thoughts | words
    private string _wordsFilter = "all"; // all | learning | known
    private bool _searchShown;
    private bool _typesetOpen;
    private bool _isFullScreen;
    private bool _isForeignBook;
    private bool _hasDialogue;
    private bool _bubblesOn;
    private bool _dramaModeOn;
    private bool _companionWholeBook;
    private IReadOnlyList<SearchHit> _searchHits = [];
    private int _hitIndex = -1;

    private ReaderMessage? _selection;
    private Mark? _openMark;
    private ReaderWord? _lookupWord;
    private WordLookupResult? _lookupEntry;
    private Footnote? _openFootnote;
    private string? _companionQuote;
    private bool _initializingControls = true;
    private bool _translationsActive;
    private (uint Section, int Sentence)? _returnFrom;
    private readonly long? _previousOpenedAtMs;
    private bool _initialOffersChecked;
    private bool _dictFullExpanded;
    private bool _dictWebReady;
    private CustomPaper? _previewPaper;
    private (uint Section, int Sentence)? _sleepArmedSpot;
    private CancellationTokenSource? _translateCts;

    // ListenCard state (1:1 MacReaderCards.swift:623-1067)
    private double _speechRate = 1.0;
    private int _sleepMode; // 0=关, -2=本句读完, -1=本章结束, 15/30/60=分钟
    private DateTime? _sleepUntilUtc;
    private bool _loopSingleSentence;
    private int _shadowPauseMode; // 0=关, 1=2秒, 2=按句长
    private bool _listenOnly;

    public ReaderWindow(LibraryService library, string bookId, bool autoListen = false, uint? startSection = null, int? startSentence = null)
    {
        _library = library;
        _bookId = bookId;
        _detail = _library.GetBookDetail(bookId);
        _previousOpenedAtMs = _detail.Summary.OpenedAt;

        var bookOwn = _library.GetBookReaderSettings(bookId);
        _ownSettings = bookOwn is not null;
        _settings = bookOwn ?? _library.GetReaderSettings();
        _pendingAutoSpeak = autoListen;

        _audio = new AudioPlayer(_library);
        _audio.SetBookContext(_bookId, _detail.Summary.Language);
        SyncSpeedFromPrefs();

        InitializeComponent();

        Title = _detail.Summary.Title;
        BookTitleText.Text = _detail.Summary.Title;
        CompanionBookTitleText.Text = _detail.Summary.Title;

        string lang = (_detail.Summary.Language ?? "").Trim().ToLowerInvariant();
        _isForeignBook = (!string.IsNullOrEmpty(lang) && !lang.StartsWith("zh", StringComparison.Ordinal))
            || (string.IsNullOrEmpty(lang) && _detail.Summary.Title.All(ch => ch < 256));

        try
        {
            uint probeSec = Math.Min(_detail.Start, (uint)Math.Max(0, _detail.Sections.Length - 1));
            _hasDialogue = _library.Core.BookCast(_bookId, probeSec).Length > 0;
        }
        catch { _hasDialogue = !_isForeignBook; }

        TranslateBtn.Visibility = _isForeignBook ? Visibility.Visible : Visibility.Collapsed;
        ScriptRow.Visibility = _isForeignBook ? Visibility.Collapsed : Visibility.Visible;
        CastBtn.Visibility = _hasDialogue ? Visibility.Visible : Visibility.Collapsed;
        BubblesRow.Visibility = _hasDialogue ? Visibility.Visible : Visibility.Collapsed;
        TypesetCastNavBtn.Visibility = _hasDialogue ? Visibility.Visible : Visibility.Collapsed;
        TxtLayoutBtn.Visibility = _detail.Summary.Format == BookFormat.Txt ? Visibility.Visible : Visibility.Collapsed;

        ThemeHelper.ResizeWindow(this, 1080, 740);
        SetTitleBar(TitleDragRegion);
        ThemeHelper.ApplyElevation(SidePanelBorder, PageShadowReceiver, 18f);
        ThemeHelper.ApplyElevation(ListenCapsule, PageShadowReceiver, 16f);
        ThemeHelper.ApplyElevation(SelectionCard, PageShadowReceiver, 16f);
        ThemeHelper.ApplyElevation(WordCard, PageShadowReceiver, 16f);
        ThemeHelper.ApplyElevation(SentenceCard, PageShadowReceiver, 16f);
        ThemeHelper.ApplyElevation(FootnoteCard, PageShadowReceiver, 16f);
        ThemeHelper.ApplyElevation(RecapCardHost, PageShadowReceiver, 18f);
        ThemeHelper.ApplyElevation(SleepReturnHost, PageShadowReceiver, 14f);

        _host = new ReaderWebViewHost(ReaderWebView, _library, BuildGeometry);
        _host.MessageReceived += OnReaderMessage;

        _audio.SentenceStarted += OnAudioSentenceStarted;
        _audio.PlayStateChanged += OnAudioPlayStateChanged;
        _audio.SectionFinished += OnAudioSectionFinished;

        _timeTracker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timeTracker.Tick += (_, _) =>
        {
            try
            {
                if (_audio.IsPlaying)
                {
                    _library.AddTime(_bookId, _currentSection, 0, 30);
                    if (_sleepUntilUtc is DateTime until && DateTime.UtcNow >= until)
                    {
                        var armed = _sleepArmedSpot ?? (_currentSection, Math.Max(0, _currentSentence));
                        _sleepUntilUtc = null;
                        _sleepMode = 0;
                        _sleepArmedSpot = null;
                        _audio.Pause();
                        ReaderRecapAndSleepHelper.RememberSleepStop(_library, _bookId, armed.Section, armed.Sentence);
                        try { _library.AddBookmark(_bookId, _currentSection, (uint)Math.Max(0, _currentSentence)); } catch { }
                    }
                }
                else
                {
                    _library.AddTime(_bookId, _currentSection, 30, 0);
                }
            }
            catch { }
        };
        _timeTracker.Start();

        Closed += OnWindowClosed;
        RootGrid.SizeChanged += (_, _) => UpdatePaperChrome();
        WebViewContainer.SizeChanged += (_, _) => UpdatePaperChrome();

        ApplyTypesetControlsFromSettings();
        ApplyTintAndChrome();
        UpdateCompanionScopeVisuals();
        ResetCompanionWelcome();

        _ = LoadInitialAsync(startSection, startSentence);
    }

    private void SyncSpeedFromPrefs()
    {
        _speechRate = Math.Clamp(_library.Prefs.SpeechRate, 0.8, 2.0);
        _audio.SetRate(_speechRate);
    }

    public void ReloadVoicePrefs()
    {
        _audio.SetVoice(_library.Prefs.VoiceId);
        SyncSpeedFromPrefs();
        if (ListenCapsule.Visibility == Visibility.Visible)
            UpdateCapsuleText(Math.Max(0, _audio.CurrentSentence));
    }

    public void ReloadReaderSettingsFromCore()
    {
        if (_ownSettings) return;
        _settings = _library.GetReaderSettings();
        PushSettingsToReader(save: false);
    }

    public void JumpTo(uint section, int sentence, bool recordReturn = true)
    {
        if (recordReturn && (section != _currentSection || Math.Abs(sentence - _currentSentence) > 2))
        {
            _returnFrom = (_currentSection, _currentSentence);
            ReturnPillText.Text = $"↩ 回到第 {EstimateBookPage(_currentSection, _currentSentence)} 页";
            ReturnPillBtn.Visibility = Visibility.Visible;
        }

        if (section == _currentSection)
        {
            _currentSentence = sentence;
            _ = _host.ScrollToSentenceAsync(sentence, animated: true);
        }
        else
        {
            _pendingSentence = sentence;
            LoadSection(section);
        }
    }

    private void ReturnPill_Click(object sender, RoutedEventArgs e)
    {
        ReturnPillBtn.Visibility = Visibility.Collapsed;
        if (_returnFrom is var (sec, sent))
        {
            _returnFrom = null;
            JumpTo(sec, sent, recordReturn: false);
        }
    }

    public void TriggerListen()
    {
        if (!_audio.IsPlaying) StartListeningAt(_currentSentence);
    }

    private int EstimateBookPage(uint section, int sentence)
    {
        int totalSentencesBefore = 0;
        for (uint i = 0; i < section && i < _detail.Sections.Length; i++)
            totalSentencesBefore += (int)_detail.Sections[i].Sentences;
        int absSent = totalSentencesBefore + Math.Max(0, sentence);
        return Math.Max(1, absSent / 12 + 1);
    }

    private int EstimateBookPercent(uint section, int sentence)
    {
        int totalAll = Math.Max(1, _detail.Sections.Sum(s => (int)s.Sentences));
        int before = 0;
        for (uint i = 0; i < section && i < _detail.Sections.Length; i++)
            before += (int)_detail.Sections[i].Sentences;
        return Math.Clamp((int)Math.Round(100.0 * (before + Math.Max(1, sentence + 1)) / totalAll), 1, 100);
    }

    public void PreviewCustomPaper(CustomPaper? draft)
    {
        _previewPaper = draft;
        PushSettingsToReader(save: false);
    }

    public void ApplyTintAndChrome()
    {
        CustomPaper? custom = _previewPaper;
        if (custom is null && !string.IsNullOrEmpty(_settings.CustomPaper))
        {
            try { custom = _library.Core.CustomPapers().FirstOrDefault(cp => cp.Id == _settings.CustomPaper); }
            catch { }
        }

        var paper = YYPapers.FromId(PaperCssId(_settings.Paper));
        bool isDark = paper.IsDark;
        Color bgCol = paper.Bg;
        Color inkCol = paper.Ink;
        Color ink2Col = paper.Ink2;
        Color glassCol = paper.Glass;
        Color lineCol = paper.Line;

        if (custom is not null)
        {
            try
            {
                var pc = YuyueCoreMethods.PaperColors(custom);
                isDark = pc.Dark;
                bgCol = CustomPaperDialog.ParseHexColor(pc.Bg, paper.Bg);
                inkCol = CustomPaperDialog.ParseHexColor(pc.Ink, paper.Ink);
                ink2Col = CustomPaperDialog.ParseHexColor(pc.Ink2, paper.Ink2);
                lineCol = CustomPaperDialog.ParseHexColor(pc.Line, paper.Line);
                glassCol = isDark ? Color.FromArgb(215, 28, 29, 32) : Color.FromArgb(224, 255, 255, 255);
            }
            catch { }
        }

        var tint = _library.Prefs.Tint;
        var accent = isDark ? tint.PaperAccentDark : tint.PaperAccentLight;
        var uiPalette = ThemeHelper.Palette(isDark);

        RootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        RootGrid.Background = ThemeHelper.Brush(bgCol);
        ListenOnlyCover.Background = ThemeHelper.Brush(bgCol);

        var inkBrush = ThemeHelper.Brush(inkCol);
        var ink2Brush = ThemeHelper.Brush(ink2Col);
        var glassBrush = ThemeHelper.Brush(glassCol);
        var lineBrush = ThemeHelper.Brush(lineCol);
        var accentBrush = ThemeHelper.Brush(accent);
        var softBrush = ThemeHelper.Brush(Color.FromArgb(isDark ? (byte)46 : (byte)30, accent.R, accent.G, accent.B));
        var transparent = new SolidColorBrush(Colors.Transparent);

        BookTitleText.Foreground = inkBrush;
        SubtitleText.Foreground = ink2Brush;
        ListenOnlyTitleText.Foreground = inkBrush;
        ListenOnlySubText.Foreground = ink2Brush;
        ListenOnlyIconHost.Content = YYIcons.Make("headphones", 34, ink2Brush);

        BackToShelfBtn.Background = glassBrush;
        BackToShelfBtn.BorderBrush = lineBrush;
        BackToShelfBtn.Foreground = inkBrush;
        BackChevronIcon.Content = YYIcons.Make("chevron-left", 11.5, inkBrush);

        ToolCapsule.Background = glassBrush;
        ToolCapsule.BorderBrush = lineBrush;

        InlineSearchCapsule.Background = glassBrush;
        InlineSearchCapsule.BorderBrush = lineBrush;
        InlineSearchIcon.Content = YYIcons.Make("search", 12, ink2Brush);
        SearchPrevBtn.Content = YYIcons.Make("chevron-left", 10.5, inkBrush);
        SearchNextBtn.Content = YYIcons.Make("chevron-right", 10.5, inkBrush);
        SearchCloseCircleBtn.Background = lineBrush;
        SearchCloseXIcon.Content = YYIcons.Make("xmark", 9.5, inkBrush);

        ListenCapsule.Background = glassBrush;
        ListenCapsule.BorderBrush = lineBrush;
        CapsuleSentenceText.Foreground = inkBrush;
        CapsuleNoteText.Foreground = ink2Brush;
        CapsulePrevBtn.Content = YYIcons.Make("chevron-left", 12, inkBrush);
        CapsuleNextBtn.Content = YYIcons.Make("chevron-right", 12, inkBrush);
#if DEBUG
        bool isListeningNow = _audio.IsPlaying || _debugListenActive;
#else
        bool isListeningNow = _audio.IsPlaying;
#endif
        CapsulePlayBtn.Background = ThemeHelper.Brush(tint.Play(paper));
        CapsulePlayBtn.Content = YYIcons.Make(isListeningNow ? "pause" : "play", 13, ThemeHelper.Brush(paper.OnMain));
        CapsuleProgressTrack.Background = lineBrush;
        CapsuleProgressFill.Background = inkBrush;
        ImmersiveProgressLine.Background = inkBrush;

        // SelectionCard & WordCard & Companion icons
        var t1Brush = ThemeHelper.Brush(uiPalette.T1);
        SelDeleteIcon.Content = YYIcons.Make("trash", 12.5, ThemeHelper.Brush(uiPalette.Danger));
        SelThoughtIcon.Content = YYIcons.Make("pencil", 14, t1Brush);
        SelSpeakIcon.Content = YYIcons.Make("speaker", 14, t1Brush);
        SelAskIcon.Content = YYIcons.Make("sparkle", 14, accentBrush);
        SelTransIcon.Content = YYIcons.Make("a-wen", 14, t1Brush);
        SelCopyIcon.Content = YYIcons.Make("copy", 13.5, t1Brush);
        SpeakWordBtn.Content = YYIcons.Make("headphones", 13, t1Brush);
        CompanionBadgeIcon.Content = YYIcons.Make("sparkle", 12.5, accentBrush);
        CompanionMoreIcon.Content = YYIcons.Make("more", 12, t1Brush);

        void PaintTool(Button b, string? icon, bool active, bool filled = false)
        {
            b.Background = active ? softBrush : transparent;
            var fg = active ? accentBrush : inkBrush;
            b.Foreground = fg;
            if (icon is not null) b.Content = YYIcons.Make(icon, 13.5, fg, filled: filled);
        }

        PaintTool(ListenBtn, isListeningNow ? "pause" : "play", isListeningNow);
        PaintTool(TocBtn, "list", _activePanel == "toc");
        PaintTool(SearchBtn, "search", _searchShown);
        PaintTool(TypesetBtn, null, _typesetOpen);
        PaintTool(TranslateBtn, "a-wen", _translationsActive);
        PaintTool(CompanionBtn, "sparkle", _activePanel == "companion");
        PaintTool(CastBtn, "cast", _activePanel == "cast");
        PaintTool(NotesBtn, "note-card", _activePanel == "notes");

        LhTightIcon.Content = YYIcons.Make("lh-tight", 12, t1Brush);
        LhNormalIcon.Content = YYIcons.Make("lh-normal", 12, t1Brush);
        LhLooseIcon.Content = YYIcons.Make("lh-loose", 12, t1Brush);
        Col1Icon.Content = YYIcons.Make("col-1", 12, t1Brush);
        Col2Icon.Content = YYIcons.Make("col-2", 12, t1Brush);

        ThemeHelper.ConfigureTitleBar(this, isDark);
    }

    private async Task LoadInitialAsync(uint? startSection, int? startSentence)
    {
        await _host.InitializeAsync();
        if (startSection is uint forcedSec && forcedSec < _detail.Sections.Length)
        {
            _currentSection = forcedSec;
            _pendingSentence = startSentence ?? 0;
        }
        else
        {
            var saved = _library.GetProgress(_bookId);
            if (saved is not null && saved.Position.Section < _detail.Sections.Length)
            {
                _currentSection = saved.Position.Section;
                _pendingSentence = (int)saved.Position.Sentence;
            }
            else
            {
                _currentSection = _detail.Start;
                _pendingSentence = 0;
            }
        }

        LoadSection(_currentSection);
        ReaderWebView.Focus(FocusState.Programmatic);
    }

    // 1:1 MacReaderView.swift：书页占满窗口全高，Top 留出 64px 悬浮顶栏安全区，Bottom 留出 88px 听书胶囊安全区
    private PageGeometry BuildGeometry()
    {
        double panelW = _activePanel == "companion" ? 358 : (_activePanel is not null ? 322 : 0);
        double width = WebViewContainer.ActualWidth > 0
            ? WebViewContainer.ActualWidth
            : Math.Max(480, (RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1080) - panelW);
        byte cols = (_settings.Mode == LayoutMode.Paginated && _library.Prefs.TwoColumns && width >= 840) ? (byte)2 : (byte)1;
        double measure = _settings.FontSize * 31.0;
        double margin = Math.Round(Math.Max(width < 500 ? 24 : 40, (width / cols - measure) / 2.0));
        return new PageGeometry(
            Paging: Paging.Snap,
            Margin: margin,
            Top: _isFullScreen ? 40 : 64,
            Bottom: 88,
            Columns: cols,
            ReducedMotion: false);
    }

    private void LoadSection(uint section)
    {
        _currentSection = section;
        _currentPage = 0;
        _layout = null;
        _sectionSentenceCount = section < _detail.Sections.Length ? (int)_detail.Sections[section].Sentences : 0;
        DismissFloatingCards();
        UpdateHeaderStatus();
        _host.LoadSection(_bookId, section);
        if (_activePanel is not null) DispatcherQueue.TryEnqueue(RenderSidePanel);
    }

    // ---------- TypesetCard (1:1 MacReaderCards.swift:65-430) ----------

    private void TypesetFlyout_Opened(object sender, object e)
    {
        _typesetOpen = true;
        TypesetMorePanel.Visibility = Visibility.Collapsed;
        TypesetMainPanel.Visibility = Visibility.Visible;
        ApplyTypesetControlsFromSettings();
        ApplyTintAndChrome();
    }

    private void TypesetFlyout_Closed(object sender, object e)
    {
        _typesetOpen = false;
        ApplyTintAndChrome();
    }

    private void ApplyTypesetControlsFromSettings()
    {
        _initializingControls = true;
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var accentBrush = ThemeHelper.Brush(tint.Accent);
        var activeBg = ThemeHelper.Brush(palette.Card);
        var clear = new SolidColorBrush(Colors.Transparent);

        bool usingCustom = !string.IsNullOrEmpty(_settings.CustomPaper);
        (Border Box, Paper Kind)[] paperBoxes =
        [
            (PaperPlainBox, Paper.Plain),
            (PaperDarkBox, Paper.Dark),
            (PaperKamiBox, Paper.Kami),
            (PaperKamiNightBox, Paper.KamiNight),
            (PaperSageBox, Paper.Sage),
            (PaperEinkBox, Paper.Eink),
        ];
        foreach (var (box, kind) in paperBoxes)
        {
            bool on = !usingCustom && _settings.Paper == kind;
            box.BorderThickness = new Thickness(on ? 2 : 1);
            if (on) box.BorderBrush = accentBrush;
        }

        RebuildCustomPapersMainHost(palette, tint);
        RebuildFontTilesHost(palette, tint);

        FontSizeValueText.Text = $"{(int)Math.Round(_settings.FontSize)}";

        LhTightBtn.Background = _settings.LineHeight is <= 1.7 ? activeBg : clear;
        LhNormalBtn.Background = _settings.LineHeight is null or (> 1.7 and < 2.05) ? activeBg : clear;
        LhLooseBtn.Background = _settings.LineHeight is >= 2.05 ? activeBg : clear;

        ColSingleBtn.Background = !_library.Prefs.TwoColumns ? activeBg : clear;
        ColDoubleBtn.Background = _library.Prefs.TwoColumns ? activeBg : clear;
        bool pag = _settings.Mode == LayoutMode.Paginated;
        ColSingleBtn.IsEnabled = pag;
        ColDoubleBtn.IsEnabled = pag;
        ColumnsSegmentWrap.Opacity = pag ? 1.0 : 0.42;

        BubblesToggle.IsOn = _bubblesOn;
        OwnSettingsToggle.IsOn = _ownSettings;

        ModePaginatedBtn.Background = _settings.Mode == LayoutMode.Paginated ? activeBg : clear;
        ModeScrolledBtn.Background = _settings.Mode == LayoutMode.Scrolled ? activeBg : clear;

        ParaNoneBtn.Background = _settings.ParagraphSpacing is <= 0.05 ? activeBg : clear;
        ParaMidBtn.Background = _settings.ParagraphSpacing is null or (> 0.05 and < 0.85) ? activeBg : clear;
        ParaWideBtn.Background = _settings.ParagraphSpacing is >= 0.85 ? activeBg : clear;

        IndentAutoBtn.Background = _settings.Indent is null ? activeBg : clear;
        IndentOnBtn.Background = _settings.Indent == true ? activeBg : clear;
        IndentOffBtn.Background = _settings.Indent == false ? activeBg : clear;

        JustifyAutoBtn.Background = _settings.Justify is null ? activeBg : clear;
        JustifyOnBtn.Background = _settings.Justify == true ? activeBg : clear;
        JustifyOffBtn.Background = _settings.Justify == false ? activeBg : clear;

        CompressToggle.IsOn = _settings.Compress;

        Script? curScript = null;
        try { curScript = _library.Core.BookScript(_bookId); } catch { }
        ScriptOrigBtn.Background = curScript is null ? activeBg : clear;
        ScriptSimpBtn.Background = curScript == Script.Simplified ? activeBg : clear;
        ScriptTradBtn.Background = curScript == Script.Traditional ? activeBg : clear;

        UpdateCompanionScopeVisuals();
        _initializingControls = false;
    }

    private void RebuildCustomPapersMainHost(YYPalette p, YYTintColors tint)
    {
        CustomPapersMainHost.Children.Clear();
        CustomPaper[] list;
        try { list = _library.Core.CustomPapers(); }
        catch { list = []; }

        if (list.Length == 0)
        {
            var btnGrid = new Grid();
            btnGrid.Children.Add(new TextBlock { Text = "自己调一张", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
            btnGrid.Children.Add(new TextBlock { Text = "＋", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            var tuneBtn = new Button
            {
                Height = 30,
                MinHeight = 30,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(8),
                Background = ThemeHelper.Brush(p.Inset),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 0, 10, 0),
                UseSystemFocusVisuals = false,
                Content = btnGrid,
            };
            tuneBtn.Click += async (_, _) =>
            {
                TypesetBtn.Flyout?.Hide();
                var created = await CustomPaperDialog.ShowEditorAsync(RootGrid.XamlRoot, _library, onPreview: PreviewCustomPaper);
                if (created is not null)
                {
                    var pc = YuyueCoreMethods.PaperColors(created);
                    _settings = _settings with { Paper = pc.Preset, CustomPaper = created.Id };
                    PushSettingsToReader();
                }
            };
            CustomPapersMainHost.Children.Add(tuneBtn);
            return;
        }

        var head = new Grid();
        head.Children.Add(new TextBlock { Text = "我的纸", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3) });
        head.Children.Add(new TextBlock { Text = $"{list.Length} 张", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
        CustomPapersMainHost.Children.Add(head);

        var shown = list.Take(9).ToArray();
        int totalCells = shown.Length + 1;
        int rows = (totalCells + 4) / 5;
        var grid = new Grid { ColumnSpacing = 7, RowSpacing = 6 };
        for (int c = 0; c < 5; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < shown.Length; i++)
        {
            var cp = shown[i];
            var pc = YuyueCoreMethods.PaperColors(cp);
            bool on = _settings.CustomPaper == cp.Id;
            var swatch = new Border
            {
                Width = 46,
                Height = 32,
                CornerRadius = new CornerRadius(9),
                Background = ThemeHelper.Brush(CustomPaperDialog.ParseHexColor(pc.Bg, Colors.Beige)),
                BorderBrush = on ? ThemeHelper.Brush(tint.Accent) : ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(on ? 2 : 1),
                Child = new TextBlock { Text = "文", FontFamily = new FontFamily("SimSun"), FontSize = 13, Foreground = ThemeHelper.Brush(CustomPaperDialog.ParseHexColor(pc.Ink, Colors.Black)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            var sp = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(swatch);
            sp.Children.Add(new TextBlock { Text = cp.Name, FontSize = 10, Foreground = ThemeHelper.Brush(p.T2), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center });

            var ctx = new MenuFlyout();
            var editMi = new MenuFlyoutItem { Text = "调整颜色…" };
            editMi.Click += async (_, _) =>
            {
                TypesetBtn.Flyout?.Hide();
                var edited = await CustomPaperDialog.ShowEditorAsync(RootGrid.XamlRoot, _library, cp, PreviewCustomPaper);
                if (edited is not null)
                {
                    var epc = YuyueCoreMethods.PaperColors(edited);
                    _settings = _settings with { Paper = epc.Preset, CustomPaper = edited.Id };
                }
                else if (_settings.CustomPaper == cp.Id && !_library.Core.CustomPapers().Any(x => x.Id == cp.Id))
                {
                    _settings = _settings with { CustomPaper = null };
                }
                PushSettingsToReader();
            };
            var dupMi = new MenuFlyoutItem { Text = "复制一张" };
            dupMi.Click += (_, _) =>
            {
                var copy = CustomPaperDialog.DuplicateCustomPaper(_library, cp);
                if (copy is not null)
                {
                    var cpc = YuyueCoreMethods.PaperColors(copy);
                    _settings = _settings with { Paper = cpc.Preset, CustomPaper = copy.Id };
                    PushSettingsToReader();
                }
            };
            var delMi = new MenuFlyoutItem { Text = "删除", Foreground = ThemeHelper.Brush(p.Danger) };
            delMi.Click += (_, _) =>
            {
                try { _library.Core.DeleteCustomPaper(cp.Id); } catch { }
                if (_settings.CustomPaper == cp.Id)
                    _settings = _settings with { CustomPaper = null };
                PushSettingsToReader();
            };
            ctx.Items.Add(editMi);
            ctx.Items.Add(dupMi);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(delMi);

            var b = new Button { Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, UseSystemFocusVisuals = false, Content = sp, ContextFlyout = ctx };
            b.Click += (_, _) =>
            {
                _settings = _settings with { Paper = pc.Preset, CustomPaper = cp.Id };
                PushSettingsToReader();
            };
            Grid.SetRow(b, i / 5);
            Grid.SetColumn(b, i % 5);
            grid.Children.Add(b);
        }

        // + 新建 tile
        var addTile = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        addTile.Children.Add(new Border
        {
            Width = 46,
            Height = 32,
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(p.Inset),
            Child = YYIcons.Make("plus", 12, ThemeHelper.Brush(p.T2)),
        });
        addTile.Children.Add(new TextBlock { Text = "新建", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Center });
        var addBtn = new Button { Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, UseSystemFocusVisuals = false, Content = addTile };
        addBtn.Click += async (_, _) =>
        {
            TypesetBtn.Flyout?.Hide();
            var created = await CustomPaperDialog.ShowEditorAsync(RootGrid.XamlRoot, _library, onPreview: PreviewCustomPaper);
            if (created is not null)
            {
                var pc = YuyueCoreMethods.PaperColors(created);
                _settings = _settings with { Paper = pc.Preset, CustomPaper = created.Id };
                PushSettingsToReader();
            }
        };
        Grid.SetRow(addBtn, shown.Length / 5);
        Grid.SetColumn(addBtn, shown.Length % 5);
        grid.Children.Add(addBtn);
        CustomPapersMainHost.Children.Add(grid);
    }

    // 1:1 MacFonts.swift:63-170：4 列紧凑字样格（系统 / 宋体 / 楷体 / 霞鹜文楷 + 更多字样册）
    private void RebuildFontTilesHost(YYPalette p, YYTintColors tint)
    {
        FontTilesHost.Children.Clear();
        FontTilesHost.ColumnDefinitions.Clear();
        FontTilesHost.RowDefinitions.Clear();
        for (int c = 0; c < 4; c++) FontTilesHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FontTilesHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        FontTilesHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        bool wenkaiInstalled = _library.Fonts.IsPackInstalled("wenkai");
        var tiles = new (string Key, string Label, string SampleFont, bool Active)[]
        {
            ("default", "系统", "Microsoft YaHei UI", _settings.Font is null && string.IsNullOrEmpty(_settings.FontFace)),
            ("serif", "宋体", "SimSun", _settings.Font == Font.Serif && string.IsNullOrEmpty(_settings.FontFace)),
            ("kai", "楷体", "KaiTi", _settings.Font == Font.Kai && string.IsNullOrEmpty(_settings.FontFace)),
            ("wenkai", wenkaiInstalled ? "霞鹜文楷" : "文楷 ↓", "KaiTi", _settings.Font == Font.WenKai),
        };

        for (int i = 0; i < tiles.Length; i++)
        {
            var t = tiles[i];
            var sp = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = "永", FontFamily = new FontFamily(t.SampleFont), FontSize = 15.5, Foreground = ThemeHelper.Brush(p.T1), HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = t.Label, FontSize = 10, Foreground = ThemeHelper.Brush(t.Active ? tint.Accent : p.T2), HorizontalAlignment = HorizontalAlignment.Center });

            var btn = new Button
            {
                Height = 44,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1.5),
                Background = ThemeHelper.Brush(t.Active ? p.Card : p.Inset),
                BorderBrush = t.Active ? ThemeHelper.Brush(tint.Accent) : new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = sp,
            };
            string key = t.Key;
            btn.Click += async (_, _) =>
            {
                if (key == "wenkai")
                {
                    if (!_library.Fonts.IsPackInstalled("wenkai"))
                    {
                        try { await _library.Fonts.DownloadPackAsync("wenkai"); } catch { return; }
                    }
                    _settings = _settings with { Font = Font.WenKai, FontFace = null };
                    PushSettingsToReader();
                    LoadSection(_currentSection);
                    return;
                }
                Font? f = key switch { "serif" => Font.Serif, "kai" => Font.Kai, _ => null };
                _settings = _settings with { Font = f, FontFace = null };
                PushSettingsToReader();
            };
            Grid.SetRow(btn, i / 4);
            Grid.SetColumn(btn, i % 4);
            FontTilesHost.Children.Add(btn);
        }

        // Row 1 Col 0: 更多（打开字样册 FontBook）
        bool customFaceActive = !string.IsNullOrEmpty(_settings.FontFace) || _settings.Font == Font.Sans;
        var moreSp = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        moreSp.Children.Add(new TextBlock { Text = "永永", FontFamily = new FontFamily("SimSun"), FontSize = 13, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Center });
        moreSp.Children.Add(new TextBlock { Text = "更多", FontSize = 10, Foreground = ThemeHelper.Brush(customFaceActive ? tint.Accent : p.T3), HorizontalAlignment = HorizontalAlignment.Center });
        var moreBtn = new Button
        {
            Height = 44,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1.5),
            Background = ThemeHelper.Brush(customFaceActive ? p.Card : p.Inset),
            BorderBrush = customFaceActive ? ThemeHelper.Brush(tint.Accent) : new SolidColorBrush(Colors.Transparent),
            UseSystemFocusVisuals = false,
            Content = moreSp,
        };
        moreBtn.Click += async (_, _) =>
        {
            TypesetBtn.Flyout?.Hide();
            await ShowFontBookDialogAsync();
        };
        Grid.SetRow(moreBtn, 1);
        Grid.SetColumn(moreBtn, 0);
        FontTilesHost.Children.Add(moreBtn);
    }

    private async Task ShowFontBookDialogAsync()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        ContentDialog? dlgRef = null;

        var listStack = new StackPanel { Spacing = 6, Width = 440 };
        var faces = new (string Label, string Desc, Font? Builtin, string? Face, string SampleFont)[]
        {
            ("系统默认", "跟随书籍内置字体，无内置时回落无衬线体", null, null, "Microsoft YaHei UI"),
            ("宋体（衬线）", "传统中文书籍正文标准明体，长文耐读", Font.Serif, null, "SimSun"),
            ("楷体", "温润手写书卷气，适合古典诗词与散文", Font.Kai, null, "KaiTi"),
            ("黑体（无衬线）", "现代清晰屏幕黑体，适合科技与社科", Font.Sans, null, "Microsoft YaHei UI"),
            ("霞鹜文楷", "开源现代楷书，兼顾楷体刀锋与黑体清晰度", Font.WenKai, null, "KaiTi"),
            ("思源宋体 (Noto Serif SC)", "开源现代人文衬线字体", Font.Serif, "notoserif", "SimSun"),
            ("思源黑体 (Noto Sans SC)", "开源现代无衬线字体", Font.Sans, "notosans", "Microsoft YaHei UI"),
            ("仿宋", "清朗古籍刻本风味", Font.Serif, "FangSong, serif", "FangSong"),
        };

        foreach (var item in faces)
        {
            bool active = _settings.Font == item.Builtin && (_settings.FontFace ?? "") == (item.Face ?? "");
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(9),
                Background = ThemeHelper.Brush(p.Inset),
                Child = new TextBlock { Text = "永", FontFamily = new FontFamily(item.SampleFont), FontSize = 19, Foreground = ThemeHelper.Brush(p.T1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });

            var meta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(new TextBlock { Text = item.Label, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T1) });
            meta.Children.Add(new TextBlock { Text = item.Desc, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
            Grid.SetColumn(meta, 1);
            row.Children.Add(meta);

            if (active)
            {
                var chk = YYIcons.Make("checkmark", 12.5, ThemeHelper.Brush(tint.Accent));
                Grid.SetColumn(chk, 2);
                row.Children.Add(chk);
            }

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 8, 12, 8),
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(active ? tint.Soft : p.Card),
                BorderBrush = ThemeHelper.Brush(active ? tint.Edge : p.Line),
                BorderThickness = new Thickness(1),
                UseSystemFocusVisuals = false,
                Content = row,
            };
            var fTarget = item;
            btn.Click += async (_, _) =>
            {
                if (fTarget.Builtin == Font.WenKai && !_library.Fonts.IsPackInstalled("wenkai"))
                {
                    try { await _library.Fonts.DownloadPackAsync("wenkai"); } catch { }
                }
                _settings = _settings with { Font = fTarget.Builtin, FontFace = fTarget.Face };
                PushSettingsToReader();
                if (fTarget.Builtin == Font.WenKai) LoadSection(_currentSection);
                dlgRef?.Hide();
            };
            listStack.Children.Add(btn);
        }

        dlgRef = new ContentDialog
        {
            Title = "字样册",
            Content = new ScrollViewer { MaxHeight = 420, Content = listStack },
            CloseButtonText = "完成",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dlgRef.ShowAsync();
    }

    private void UpdatePaperChrome()
    {
        if (_layout is null) return;
        var g = BuildGeometry();
        _ = _host.ApplySettingsAsync(new { margin = g.Margin, top = g.Top, bottom = g.Bottom, cols = g.Columns });
        UpdateImmersiveHairline();
    }

    private void PushSettingsToReader(bool save = true)
    {
        if (save)
        {
            if (_ownSettings) _library.SetBookReaderSettings(_bookId, _settings);
            else _library.SaveReaderSettings(_settings);
        }
        ApplyTintAndChrome();
        ApplyTypesetControlsFromSettings();

        string? paperStyle = null;
        string? texture = null;
        Paper effectivePreset = _settings.Paper;
        CustomPaper? activeCustom = _previewPaper;
        if (activeCustom is null && !string.IsNullOrEmpty(_settings.CustomPaper))
        {
            try { activeCustom = _library.Core.CustomPapers().FirstOrDefault(x => x.Id == _settings.CustomPaper); }
            catch { }
        }
        if (activeCustom is not null)
        {
            try
            {
                effectivePreset = YuyueCoreMethods.PaperColors(activeCustom).Preset;
                paperStyle = YuyueCoreMethods.PaperStyle(activeCustom);
                texture = activeCustom.Texture == PaperTexture.None ? null : activeCustom.Texture.ToString().ToLowerInvariant();
            }
            catch { }
        }

        var g = BuildGeometry();
        _ = _host.ApplySettingsAsync(new
        {
            paper = PaperCssId(effectivePreset),
            paperStyle,
            texture,
            mode = _settings.Mode == LayoutMode.Scrolled ? "scrolled" : "paginated",
            paging = "snap",
            fontSize = _settings.FontSize,
            lineHeight = _settings.LineHeight,
            paragraphSpacing = _settings.ParagraphSpacing,
            justify = _settings.Justify,
            indent = _settings.Indent,
            font = FontCssValue(_settings.Font, _settings.FontFace),
            compress = _settings.Compress,
            motion = "full",
            margin = g.Margin,
            top = g.Top,
            bottom = g.Bottom,
            cols = g.Columns,
        });
    }

    private async void OnReaderMessage(ReaderMessage msg)
    {
        switch (msg.Type)
        {
            case "ready":
            case "layout":
                _layout = new SectionLayout(msg.PageCount ?? 1, msg.SentencePages, msg.PageFirstSentence);
                await OnSectionLaidOutAsync(isReady: msg.Type == "ready");
                break;

            case "page":
                if (msg.Page is int p)
                {
                    _currentPage = p;
                    if (_layout is not null)
                    {
                        _currentSentence = _layout.FirstSentenceOn(p);
                        SaveCurrentProgress();
                    }
                    UpdateHeaderStatus();
                    ApplyTintAndChrome();
                }
                break;

            case "scroll":
                if (msg.Sentence is int s)
                {
                    _currentSentence = s;
                    SaveCurrentProgress();
                    UpdateHeaderStatus();
                }
                break;

            case "wheel":
                if (msg.DeltaY is double dy)
                {
                    if (msg.Ctrl == true)
                    {
                        double step = dy < 0 ? 1 : -1;
                        _settings = _settings with { FontSize = Math.Clamp(_settings.FontSize + step, 12, 34) };
                        PushSettingsToReader();
                    }
                    else if (dy > 0) await TurnNextAsync();
                    else if (dy < 0) await TurnPrevAsync();
                }
                break;

            case "winContext":
                if (msg.X is double cx && msg.Y is double cy)
                    ShowPageContextMenu(new Point(cx, cy));
                break;

            case "selection":
                if (msg.Start is not null && msg.End is not null && !string.IsNullOrWhiteSpace(msg.Text) && msg.Rect is not null)
                {
                    _openMark = null;
                    _selection = msg;
                    ShowSelectionCard(msg.Rect, editingExistingMark: false);
                }
                else if (_openMark is null)
                {
                    HideSelectionCard();
                }
                break;

            case "tap":
                DismissFloatingCards();
                if (msg.Mark is string markId)
                {
                    var found = _library.GetMarks(_bookId).FirstOrDefault(m => m.Id == markId);
                    if (found is not null)
                    {
                        _openMark = found;
                        ShowSelectionCard(new ReaderRect { X = msg.X ?? 200, Y = msg.Y ?? 200, Width = 2, Height = 24 }, editingExistingMark: true);
                    }
                }
                else if (msg.Link?.Note is not null && msg.Link.Target is string noteTarget)
                {
                    ShowFootnote(noteTarget);
                }
                else if (msg.Link?.Target is string target)
                {
                    HandleInternalLink(target);
                }
                else if (msg.Link?.External is string external && Uri.TryCreate(external, UriKind.Absolute, out var extUri))
                {
                    _ = Launcher.LaunchUriAsync(extUri);
                }
                else if (msg.Word is ReaderWord word)
                {
                    ShowWordLookup(word);
                }
                break;

            case "doubleTap":
                // 1:1 MacForeignCards.swift:112-150：外文书未开全页双语时双击句子弹出单句翻译卡；中文书双击从该句开始朗读
                if (msg.Sentence is int tappedSentence)
                {
                    if (_isForeignBook && !_translationsActive)
                        await ShowSentenceTranslationCardAsync(tappedSentence);
                    else
                        StartListeningAt(tappedSentence);
                }
                break;

            case "activePage":
                if (msg.Page is int activePage && activePage != _currentPage && _settings.Mode == LayoutMode.Paginated)
                {
                    _currentPage = activePage;
                    await _host.ScrollToPageAsync(activePage, animated: true);
                    UpdateHeaderStatus();
                }
                break;
        }
    }

    // ---------- Right-Click Context Menus (1:1 MacReaderView.swift:792-845) ----------

    private void ShowPageContextMenu(Point pos)
    {
        var menu = new MenuFlyout();

        if (_openMark is Mark existingMark)
        {
            var noteItem = new MenuFlyoutItem { Text = string.IsNullOrWhiteSpace(existingMark.Note) ? "写想法…" : "改想法…" };
            noteItem.Click += AddThought_Click;
            menu.Items.Add(noteItem);

            var copyMk = new MenuFlyoutItem { Text = "复制" };
            copyMk.Click += CopySelection_Click;
            menu.Items.Add(copyMk);

            var shareMk = new MenuFlyoutItem { Text = "分享…" };
            shareMk.Click += ShareSelection_Click;
            menu.Items.Add(shareMk);

            menu.Items.Add(new MenuFlyoutSeparator());
            var delMk = new MenuFlyoutItem { Text = "删除划线" };
            delMk.Click += DeleteOpenMark_Click;
            menu.Items.Add(delMk);
        }
        else if (_selection is not null && !string.IsNullOrWhiteSpace(_selection.Text))
        {
            foreach (var (label, style) in new[]
            {
                ("划黄色 (1)", MarkStyle.Yellow),
                ("划蓝色 (2)", MarkStyle.Blue),
                ("划粉色 (3)", MarkStyle.Pink),
                ("划波浪线 (4)", MarkStyle.Wave),
                ("划下划线", MarkStyle.Underline),
            })
            {
                var item = new MenuFlyoutItem { Text = label };
                var st = style;
                item.Click += async (_, _) => await ApplySelectionMarkAsync(st);
                menu.Items.Add(item);
            }

            menu.Items.Add(new MenuFlyoutSeparator());

            var thoughtItem = new MenuFlyoutItem { Text = "想法…" };
            thoughtItem.Click += AddThought_Click;
            menu.Items.Add(thoughtItem);

            var speakItem = new MenuFlyoutItem { Text = "从这听" };
            speakItem.Click += SpeakFromSelection_Click;
            menu.Items.Add(speakItem);

            var askItem = new MenuFlyoutItem { Text = "问书伴" };
            askItem.Click += AskSelection_Click;
            menu.Items.Add(askItem);

            var transItem = new MenuFlyoutItem { Text = "翻译" };
            transItem.Click += TranslateSelection_Click;
            menu.Items.Add(transItem);

            var copyItem = new MenuFlyoutItem { Text = "复制 (Ctrl+C)" };
            copyItem.Click += CopySelection_Click;
            menu.Items.Add(copyItem);

            var shareItem = new MenuFlyoutItem { Text = "分享…" };
            shareItem.Click += ShareSelection_Click;
            menu.Items.Add(shareItem);
        }
        else
        {
            var playItem = new MenuFlyoutItem { Text = _audio.IsPlaying ? "暂停朗读 (空格)" : "从这页朗读 (空格)" };
            playItem.Click += ToggleListen_Click;
            menu.Items.Add(playItem);

            var bmItem = new MenuFlyoutItem { Text = HasBookmarkHere() ? "拿掉书签 (Ctrl+D)" : "添加书签 (Ctrl+D)" };
            bmItem.Click += ToggleBookmark_Click;
            menu.Items.Add(bmItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var searchItem = new MenuFlyoutItem { Text = "书内搜索 (Ctrl+F)" };
            searchItem.Click += (_, _) => OpenSearch();
            menu.Items.Add(searchItem);

            var tocItem = new MenuFlyoutItem { Text = "目录、书签 (Ctrl+Alt+T)" };
            tocItem.Click += (_, _) => OpenPanel("toc");
            menu.Items.Add(tocItem);

            var notesItem = new MenuFlyoutItem { Text = "笔记 (Ctrl+Alt+N)" };
            notesItem.Click += (_, _) => OpenPanel("notes");
            menu.Items.Add(notesItem);

            var compItem = new MenuFlyoutItem { Text = "书伴" };
            compItem.Click += (_, _) => OpenPanel("companion");
            menu.Items.Add(compItem);

            menu.Items.Add(new MenuFlyoutSeparator());

            var fsItem = new MenuFlyoutItem { Text = _isFullScreen ? "退出全屏 (F11)" : "沉浸全屏 (F11)" };
            fsItem.Click += (_, _) => ToggleFullScreen();
            menu.Items.Add(fsItem);
        }

        menu.ShowAt(WebViewContainer, new FlyoutShowOptions { Position = pos });
    }

    private async Task OnSectionLaidOutAsync(bool isReady)
    {
        if (isReady)
        {
            UpdatePaperChrome();
            var sectionMarks = _library.GetMarks(_bookId).Where(m => m.Section == _currentSection);
            await _host.SetMarksAsync(sectionMarks);
            await ApplyForeignReadingAsync();
            var q = BookSearchBox.Text.Trim();
            if (_searchShown && !string.IsNullOrEmpty(q)) await _host.SetHitsAsync(q);
        }

        if (_pendingAnchor is not null)
        {
            var anchor = _pendingAnchor;
            _pendingAnchor = null;
            await _host.ScrollToAnchorAsync(anchor, animated: false);
        }
        else if (_pendingEndOfSection && _layout is not null)
        {
            _pendingEndOfSection = false;
            _currentPage = Math.Max(0, _layout.PageCount - 1);
            await _host.ScrollToPageAsync(_currentPage, animated: false);
            _currentSentence = _layout.FirstSentenceOn(_currentPage);
        }
        else if (_pendingSentence is int targetSentence)
        {
            _pendingSentence = null;
            _currentSentence = targetSentence;
            await _host.ScrollToSentenceAsync(targetSentence, animated: false);
            if (_layout is not null) _currentPage = _layout.PageOf(targetSentence);
        }

        UpdateHeaderStatus();
        ApplyTintAndChrome();
        if (_activePanel is "toc" or "notes" or "cast") RenderSidePanel();

        if (isReady)
        {
            SaveCurrentProgress();
            App.CurrentApp.NotifyShelfRefresh();

            if (!_initialOffersChecked)
            {
                _initialOffersChecked = true;
                _ = CheckInitialRecapAndSleepOfferAsync();
            }

            if (_translationsActive)
            {
                _ = ApplyCurrentSectionTranslationsAsync();
            }
        }

        if (_pendingAutoSpeak)
        {
            _pendingAutoSpeak = false;
            StartListeningAt(_currentSentence);
        }
#if DEBUG
        if (_debugShowListenBar)
        {
            _debugShowListenBar = false;
            _debugListenActive = true;
            var sents = _library.GetSpokenSentences(_bookId, _currentSection);
            _sectionSentenceCount = Math.Max(8, sents.Count);
            int targetSent = Math.Min(1, Math.Max(0, sents.Count - 1));
            ListenCapsule.Visibility = Visibility.Visible;
            UpdateCapsuleText(targetSent);
            ApplyTintAndChrome();
            await _host.SetActiveAsync(targetSent, 6000, 2200, true);
            if (_debugOpenListenCard)
            {
                _debugOpenListenCard = false;
                CapsuleInfoBtn.Flyout?.ShowAt(CapsuleInfoBtn);
                if (Environment.GetEnvironmentVariable("YUYUE_DUMP_FLYOUT") is string dumpPath && !string.IsNullOrEmpty(dumpPath))
                {
                    await Task.Delay(500);
                    await QuoteShareDialog.SaveElementPngAsync(ListenCardHost, dumpPath);
                }
            }
        }
        if (_debugOpenTypeset)
        {
            _debugOpenTypeset = false;
            TypesetBtn.Flyout?.ShowAt(TypesetBtn);
            if (Environment.GetEnvironmentVariable("YUYUE_DUMP_FLYOUT") is string dumpPath && !string.IsNullOrEmpty(dumpPath))
            {
                await Task.Delay(500);
                await QuoteShareDialog.SaveElementPngAsync(TypesetMainPanel, dumpPath);
            }
        }
#endif
    }

#if DEBUG
    private bool _debugShowListenBar;
    private bool _debugListenActive;
    private bool _debugOpenListenCard;
    private bool _debugOpenTypeset;

    public void ConfigureDebugView(string? panel, bool listenBar)
    {
        if (panel == "typeset") _debugOpenTypeset = true;
        else if (panel == "listen-card") { _debugShowListenBar = true; _debugOpenListenCard = true; }
        else if (!string.IsNullOrEmpty(panel)) OpenPanel(panel);
        if (listenBar) _debugShowListenBar = true;
    }
#endif

    private void SaveCurrentProgress()
    {
        try { _library.SaveProgress(_bookId, _currentSection, (uint)Math.Max(0, _currentSentence)); } catch { }
    }

    // 1:1 MacReaderView.swift:521-532：全书估算页码在分页和滚动模式下均显示
    private void UpdateHeaderStatus()
    {
        var parts = new List<string>();
        if (_detail.Summary.Authors.Length > 0 && !string.IsNullOrWhiteSpace(_detail.Summary.Authors[0]))
            parts.Add(_detail.Summary.Authors[0]);

        string chapterTitle = _currentSection < _detail.Sections.Length ? _detail.Sections[_currentSection].Title ?? "" : "";
        if (!string.IsNullOrWhiteSpace(chapterTitle) && _detail.Toc.Length > 1)
        {
            var (num, _) = SplitChapterTitle(chapterTitle);
            parts.Add(num ?? chapterTitle);
        }

        int bookPage = EstimateBookPage(_currentSection, _currentSentence);
        parts.Add($"第 {bookPage} 页");

        SubtitleText.Text = string.Join(" · ", parts);
        ListenOnlyTitleText.Text = !string.IsNullOrWhiteSpace(chapterTitle) ? chapterTitle : _detail.Summary.Title;
        UpdateCompanionScopeVisuals();
        UpdateImmersiveHairline();
    }

    private static (string? Number, string Title) SplitChapterTitle(string raw)
    {
        string t = raw.Trim();
        var m = Regex.Match(t, @"^(第[零一二三四五六七八九十百千0-9]+[章节回篇卷部]|Chapter\s+[0-9IVXLCDM]+|[0-9]+[\.、])\s*(.*)$", RegexOptions.IgnoreCase);
        if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[2].Value))
            return (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
        return (null, t);
    }

    private void UpdateImmersiveHairline()
    {
        if (!_isFullScreen || _detail.Sections.Length == 0)
        {
            ImmersiveProgressLine.Visibility = Visibility.Collapsed;
            return;
        }
        double frac = (_currentSection + (_layout is not null ? (_currentPage + 1.0) / _layout.PageCount : 0.0)) / _detail.Sections.Length;
        ImmersiveProgressLine.Width = Math.Max(4, WebViewContainer.ActualWidth * Math.Clamp(frac, 0, 1));
        ImmersiveProgressLine.Visibility = Visibility.Visible;
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        AppWindow?.SetPresenter(_isFullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        TopToolbar.Visibility = _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        UpdatePaperChrome();
    }

    private void HandleInternalLink(string target)
    {
        var parts = target.Split('#', 2);
        if (uint.TryParse(parts[0], out uint section) && section < _detail.Sections.Length)
        {
            var anchor = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : null;
            if (section == _currentSection && anchor is not null) _ = _host.ScrollToAnchorAsync(anchor, animated: true);
            else { _pendingAnchor = anchor; LoadSection(section); }
        }
    }

    private void BackToShelf_Click(object sender, RoutedEventArgs e) => App.CurrentApp.ShowShelf();

    // ---------- Page turns & keyboard ----------

    private bool _finishedPrompted;

    private async Task TurnNextAsync()
    {
        if (_settings.Mode == LayoutMode.Scrolled)
        {
            if (_library.NextSection(_bookId, _currentSection) is uint s) { _pendingSentence = 0; LoadSection(s); }
            else await MaybeOfferFinishedSheetAsync();
            return;
        }
        if (!await _host.NextPageAsync(animated: true))
        {
            if (_library.NextSection(_bookId, _currentSection) is uint s) { _pendingSentence = 0; LoadSection(s); }
            else await MaybeOfferFinishedSheetAsync();
        }
    }

    private async Task MaybeOfferFinishedSheetAsync()
    {
        if (_finishedPrompted || _detail.Summary.Status == ReadingStatus.Finished) return;
        _finishedPrompted = true;
        await ReaderRecapAndSleepHelper.ShowFinishedSheetAsync(RootGrid.XamlRoot, _library, _detail);
        try { _detail = _library.GetBookDetail(_bookId); } catch { }
    }

    private async Task TurnPrevAsync()
    {
        if (_settings.Mode == LayoutMode.Scrolled)
        {
            if (_library.PreviousSection(_bookId, _currentSection) is uint s) { _pendingSentence = 0; LoadSection(s); }
            return;
        }
        if (!await _host.PrevPageAsync(animated: true))
        {
            if (_library.PreviousSection(_bookId, _currentSection) is uint s) { _pendingEndOfSection = true; LoadSection(s); }
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox) return;

        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);

        if (ctrl && shift && e.Key == VirtualKey.F) { ToggleFullScreen(); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.F) { OpenSearch(); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.K) { OpenPanel("companion"); CompanionInputBox.Focus(FocusState.Programmatic); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.G) { StepHit(forward: !shift); e.Handled = true; return; }
        if (ctrl && alt && e.Key == VirtualKey.T) { ToggleNamedPanel("toc"); e.Handled = true; return; }
        if (ctrl && alt && e.Key == VirtualKey.N) { ToggleNamedPanel("notes"); e.Handled = true; return; }
        if (ctrl && alt && e.Key == VirtualKey.Up)
        {
            if (_library.PreviousSection(_bookId, _currentSection) is uint prevSec) { _pendingSentence = 0; LoadSection(prevSec); }
            e.Handled = true;
            return;
        }
        if (ctrl && alt && e.Key == VirtualKey.Down)
        {
            if (_library.NextSection(_bookId, _currentSection) is uint nextSec) { _pendingSentence = 0; LoadSection(nextSec); }
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == (VirtualKey)219) { SpeakPrev_Click(sender, e); e.Handled = true; return; } // Ctrl+[
        if (ctrl && e.Key == (VirtualKey)221) { SpeakNext_Click(sender, e); e.Handled = true; return; } // Ctrl+]
        if (ctrl && (e.Key == VirtualKey.B || e.Key == VirtualKey.D)) { ToggleBookmark_Click(sender, e); e.Handled = true; return; }
        if (ctrl && (e.Key == (VirtualKey)187 || e.Key == VirtualKey.Add)) { _settings = _settings with { FontSize = Math.Min(34, _settings.FontSize + 1) }; PushSettingsToReader(); e.Handled = true; return; }
        if (ctrl && (e.Key == (VirtualKey)189 || e.Key == VirtualKey.Subtract)) { _settings = _settings with { FontSize = Math.Max(12, _settings.FontSize - 1) }; PushSettingsToReader(); e.Handled = true; return; }

        // 1..4 快捷键立即应用划线样式（1:1 MacReaderView.swift:881-891）
        if (!ctrl && !alt && SelectionCard.Visibility == Visibility.Visible)
        {
            MarkStyle? quickStyle = e.Key switch
            {
                VirtualKey.Number1 => MarkStyle.Yellow,
                VirtualKey.Number2 => MarkStyle.Blue,
                VirtualKey.Number3 => MarkStyle.Pink,
                VirtualKey.Number4 => MarkStyle.Wave,
                _ => null,
            };
            if (quickStyle is MarkStyle qs)
            {
                _ = ApplySelectionMarkAsync(qs);
                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case VirtualKey.F11:
                ToggleFullScreen(); e.Handled = true; break;
            case VirtualKey.Right:
            case VirtualKey.PageDown:
                _ = TurnNextAsync(); e.Handled = true; break;
            case VirtualKey.Left:
            case VirtualKey.PageUp:
                _ = TurnPrevAsync(); e.Handled = true; break;
            case VirtualKey.Space:
                ToggleListen_Click(sender, e); e.Handled = true; break;
            case VirtualKey.Escape:
                if (_listenOnly) { _listenOnly = false; ListenOnlyCover.Visibility = Visibility.Collapsed; }
                else if (RecapCardHost.Visibility == Visibility.Visible) { RecapCardHost.Visibility = Visibility.Collapsed; }
                else if (_isFullScreen) ToggleFullScreen();
                else if (_searchShown) CloseSearch();
                else if (SelectionCard.Visibility == Visibility.Visible || WordCard.Visibility == Visibility.Visible || SentenceCard.Visibility == Visibility.Visible || FootnoteCard.Visibility == Visibility.Visible) DismissFloatingCards();
                else if (_activePanel is not null) ClosePanel();
                e.Handled = true;
                break;
        }
    }

    // ---------- Typeset actions ----------

    private void PaperChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        _settings = _settings with { Paper = ParsePaper(id), CustomPaper = null };
        PushSettingsToReader();
    }

    private void FontStepDown_Click(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { FontSize = Math.Max(12, Math.Round(_settings.FontSize) - 1) };
        PushSettingsToReader();
    }

    private void FontStepUp_Click(object sender, RoutedEventArgs e)
    {
        _settings = _settings with { FontSize = Math.Min(34, Math.Round(_settings.FontSize) + 1) };
        PushSettingsToReader();
    }

    private void LineHeight_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        double? lh = tag switch { "tight" => 1.6, "loose" => 2.2, _ => null };
        _settings = _settings with { LineHeight = lh };
        PushSettingsToReader();
    }

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        _library.Prefs.TwoColumns = tag == "2";
        _library.SavePrefs();
        PushSettingsToReader();
    }

    private void BubblesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializingControls) return;
        _bubblesOn = BubblesToggle.IsOn;
        try
        {
            _library.Core.SetBookBubbles(_bookId, _bubblesOn);
            _pendingSentence = _currentSentence;
            LoadSection(_currentSection);
        }
        catch { }
    }

    private void TypesetOpenCast_Click(object sender, RoutedEventArgs e)
    {
        TypesetBtn.Flyout?.Hide();
        OpenPanel("cast");
    }

    private void OwnSettingsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializingControls) return;
        _ownSettings = OwnSettingsToggle.IsOn;
        if (_ownSettings) _library.SetBookReaderSettings(_bookId, _settings);
        else
        {
            _library.SetBookReaderSettings(_bookId, null);
            _settings = _library.GetReaderSettings();
            PushSettingsToReader(save: false);
        }
    }

    private void TypesetMore_Click(object sender, RoutedEventArgs e)
    {
        TypesetMainPanel.Visibility = Visibility.Collapsed;
        TypesetMorePanel.Visibility = Visibility.Visible;
    }

    private void TypesetBack_Click(object sender, RoutedEventArgs e)
    {
        TypesetMorePanel.Visibility = Visibility.Collapsed;
        TypesetMainPanel.Visibility = Visibility.Visible;
    }

    private void LayoutMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        _settings = _settings with { Mode = tag == "scrolled" ? LayoutMode.Scrolled : LayoutMode.Paginated };
        PushSettingsToReader();
    }

    private void ParaSpacing_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        double? sp = tag switch { "none" => 0.0, "wide" => 1.0, _ => null };
        _settings = _settings with { ParagraphSpacing = sp };
        PushSettingsToReader();
    }

    private void Indent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        bool? ind = tag switch { "on" => true, "off" => false, _ => null };
        _settings = _settings with { Indent = ind };
        PushSettingsToReader();
    }

    private void Justify_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        bool? just = tag switch { "on" => true, "off" => false, _ => null };
        _settings = _settings with { Justify = just };
        PushSettingsToReader();
    }

    private void CompressToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializingControls) return;
        _settings = _settings with { Compress = CompressToggle.IsOn };
        PushSettingsToReader();
    }

    private void BookScript_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        Script? next = tag switch
        {
            "simp" => Script.Simplified,
            "trad" => Script.Traditional,
            _ => null,
        };
        try
        {
            _library.Core.SetBookScript(_bookId, next);
            ApplyTypesetControlsFromSettings();
            _pendingSentence = _currentSentence;
            LoadSection(_currentSection);
        }
        catch { }
    }

    private async void ToggleTranslate_Click(object sender, RoutedEventArgs e)
    {
        if (_translationsActive)
        {
            _translateCts?.Cancel();
            _translationsActive = false;
            await _host.SetTranslationsAsync(null);
            ApplyTintAndChrome();
            return;
        }

        if (!_library.Companion.IsConfigured)
        {
            App.CurrentApp.OpenSettings("companion");
            return;
        }

        _translationsActive = true;
        ApplyTintAndChrome();
        await ApplyCurrentSectionTranslationsAsync();
    }

    private string GetTranslationCachePath()
    {
        string dir = Path.Combine(_library.DataDir, "companion");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{_bookId}.trans.json");
    }

    private Dictionary<string, string> LoadTranslationCache()
    {
        try
        {
            string path = GetTranslationCachePath();
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch { }
        return new();
    }

    private void SaveTranslationCache(Dictionary<string, string> cache)
    {
        try
        {
            File.WriteAllText(GetTranslationCachePath(), JsonSerializer.Serialize(cache));
        }
        catch { }
    }

    private async Task ApplyCurrentSectionTranslationsAsync()
    {
        _translateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _translateCts = cts;
        uint targetSec = _currentSection;

        var paragraphs = await _host.GetForeignParagraphsAsync();
        if (paragraphs.Count == 0 || cts.IsCancellationRequested) return;

        var cache = LoadTranslationCache();
        var results = new List<(int Last, string Text)>();
        var uncached = new List<(int Last, string Key, string Text)>();

        foreach (var p in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(p.text)) continue;
            string key = $"{targetSec}|{p.last}|{HashText32(p.text):X8}";
            if (cache.TryGetValue(key, out var cached) && !string.IsNullOrWhiteSpace(cached))
                results.Add((p.last, cached));
            else
                uncached.Add((p.last, key, p.text));
        }

        if (results.Count > 0 && !cts.IsCancellationRequested && _currentSection == targetSec && _translationsActive)
            await _host.SetTranslationsAsync(results);

        if (uncached.Count == 0 || !_library.Companion.IsConfigured) return;

        // Translate in batches of 4 paragraphs for speed while progressively updating the page
        for (int offset = 0; offset < uncached.Count; offset += 4)
        {
            if (cts.IsCancellationRequested || !_translationsActive || _currentSection != targetSec) return;
            var chunk = uncached.Skip(offset).Take(4).ToArray();
            try
            {
                if (chunk.Length == 1)
                {
                    string tr = await _library.Companion.ChatAsync(
                        $"请把下面这段外文准确自然地译为简体中文，只输出译文正文，不要加任何解释：\n\n{chunk[0].Text}",
                        "你是专业的文学翻译家，译文信达雅、贴合原文语境。");
                    string clean = tr.Trim();
                    if (!string.IsNullOrEmpty(clean))
                    {
                        cache[chunk[0].Key] = clean;
                        results.Add((chunk[0].Last, clean));
                    }
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("请将以下按 [1]、[2]… 编号的外文段落逐段准确自然地译为简体中文。严格按相同编号 `[1] 译文`、`[2] 译文` 分段输出，不要遗漏或合并，不要加解释：");
                    for (int i = 0; i < chunk.Length; i++)
                        sb.AppendLine($"\n[{i + 1}] {chunk[i].Text}");

                    string reply = await _library.Companion.ChatAsync(sb.ToString(), "你是专业的文学翻译家，严格保持对应编号输出简体中文译文。");
                    var parsed = ParseNumberedTranslations(reply, chunk.Length);
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(parsed[i]))
                        {
                            cache[chunk[i].Key] = parsed[i]!;
                            results.Add((chunk[i].Last, parsed[i]!));
                        }
                    }
                }

                SaveTranslationCache(cache);
                if (!cts.IsCancellationRequested && _translationsActive && _currentSection == targetSec)
                    await _host.SetTranslationsAsync(results.OrderBy(x => x.Last));
            }
            catch
            {
                break;
            }
        }
    }

    private static uint HashText32(string text)
    {
        uint h = 2_166_136_261u;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            h ^= b;
            h *= 16_777_619u;
        }
        return h;
    }

    private static string?[] ParseNumberedTranslations(string reply, int expectedCount)
    {
        var arr = new string?[expectedCount];
        var matches = Regex.Matches(reply, @"\[(\d+)\]\s*([\s\S]*?)(?=\n\s*\[\d+\]|\z)");
        foreach (Match m in matches)
        {
            if (int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= expectedCount)
                arr[n - 1] = m.Groups[2].Value.Trim();
        }
        if (arr.All(x => x is null))
        {
            var lines = reply.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < Math.Min(expectedCount, lines.Length); i++)
                arr[i] = Regex.Replace(lines[i].Trim(), @"^\d+[\.\、\)]\s*", "");
        }
        return arr;
    }

    // ---------- Floating Cards: Selection / Thought / Word / Sentence / Footnote ----------

    private void DismissFloatingCards()
    {
        HideSelectionCard();
        WordCard.Visibility = Visibility.Collapsed;
        DictEntryWebView.Visibility = Visibility.Collapsed;
        _dictFullExpanded = false;
        SentenceCard.Visibility = Visibility.Collapsed;
        FootnoteCard.Visibility = Visibility.Collapsed;
        _lookupWord = null;
        _lookupEntry = null;
        _openFootnote = null;
        _ = _host.SetLookingAsync(null, null);
    }

    public void ReloadForeignPrefs() => _ = ApplyForeignReadingAsync();

    private async Task ApplyForeignReadingAsync()
    {
        try
        {
            await _host.SetLookupAsync(_library.Prefs.ForeignLookup);
            if (_library.Prefs.ForeignMarkVocab)
            {
                var unknown = _library.GetWords().Where(w => !w.Known).Select(w => w.Word).ToArray();
                await _host.SetVocabAsync(unknown);
            }
            else
            {
                await _host.SetVocabAsync([]);
            }

            if (_library.Prefs.GlossLevel > 0 || _library.Prefs.GlossVocabOnly)
            {
                byte lvl = _library.Prefs.GlossLevel > 0 ? _library.Prefs.GlossLevel : (byte)2;
                var glosses = _library.GetGlosses(_bookId, _currentSection, lvl, _library.Prefs.GlossVocabOnly);
                await _host.SetGlossesAsync(glosses);
            }
            else
            {
                await _host.SetGlossesAsync([]);
            }
        }
        catch { }
    }

    private async void ReplaceRules_Click(object sender, RoutedEventArgs e)
    {
        TypesetBtn.Flyout?.Hide();
        if (await BookToolsDialogs.ShowReplaceRulesDialogAsync(RootGrid.XamlRoot, _library, _bookId, _detail.Summary.Title))
            LoadSection(_currentSection);
    }

    private async void TxtLayout_Click(object sender, RoutedEventArgs e)
    {
        TypesetBtn.Flyout?.Hide();
        if (await BookToolsDialogs.ShowTxtLayoutDialogAsync(RootGrid.XamlRoot, _library, _bookId, _detail.Summary.Title, sec => JumpTo(sec, 0)))
        {
            _detail = _library.GetBookDetail(_bookId);
            if (_activePanel == "toc") RenderSidePanel();
            LoadSection(0);
            App.CurrentApp.NotifyShelfRefresh();
        }
    }

    private void ShowSelectionCard(ReaderRect rect, bool editingExistingMark)
    {
        bool hasNote = editingExistingMark && !string.IsNullOrWhiteSpace(_openMark?.Note);
        SelectionNoteSection.Visibility = hasNote ? Visibility.Visible : Visibility.Collapsed;
        SelectionNotePreviewText.Text = _openMark?.Note ?? "";
        ThoughtCardLabel.Text = hasNote ? "改想法" : "想法";
        DeleteMarkCardBtn.Visibility = editingExistingMark ? Visibility.Visible : Visibility.Collapsed;

        double cardH = hasNote ? 152 : 104;
        double maxX = Math.Max(12, WebViewContainer.ActualWidth - 348);
        double x = Math.Clamp(rect.X - 40, 12, maxX);
        double y = rect.Y > cardH + 20 ? rect.Y - cardH - 10 : rect.Y + rect.Height + 10;
        SelectionCard.Margin = new Thickness(x, Math.Max(60, y), 0, 0);
        SelectionCard.Visibility = Visibility.Visible;
    }

    private void HideSelectionCard()
    {
        SelectionCard.Visibility = Visibility.Collapsed;
        _selection = null;
        _openMark = null;
    }

    private async void MarkColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string styleTag) return;
        var style = styleTag switch
        {
            "blue" => MarkStyle.Blue,
            "pink" => MarkStyle.Pink,
            "wave" => MarkStyle.Wave,
            "underline" => MarkStyle.Underline,
            "dotted" => MarkStyle.Dotted,
            _ => MarkStyle.Yellow,
        };
        await ApplySelectionMarkAsync(style);
    }

    private async Task ApplySelectionMarkAsync(MarkStyle style)
    {
        if (_openMark is not null)
        {
            _library.SetMarkStyle(_bookId, _openMark.Id, style);
            HideSelectionCard();
            await RefreshSectionMarksAsync();
            return;
        }
        if (_selection?.Start is null || _selection.End is null || string.IsNullOrWhiteSpace(_selection.Text)) return;

        _library.AddMark(_bookId, _currentSection,
            new TextPoint((uint)_selection.Start.S, (uint)_selection.Start.O),
            new TextPoint((uint)_selection.End.S, (uint)_selection.End.O),
            _selection.Text, style);

        HideSelectionCard();
        await _host.ClearSelectionAsync();
        await RefreshSectionMarksAsync();
    }

    private async void AddThought_Click(object sender, RoutedEventArgs e)
    {
        var existingMark = _openMark;
        var sel = _selection;
        string quote = existingMark?.Text ?? sel?.Text ?? "";
        if (string.IsNullOrWhiteSpace(quote)) return;

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var input = new TextBox
        {
            Text = existingMark?.Note ?? "",
            PlaceholderText = "写下这一句的想法…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 96,
        };
        var counterTb = new TextBlock { Text = $"{input.Text.Length} 字", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right };
        input.TextChanged += (_, _) => counterTb.Text = $"{input.Text.Length} 字";

        var dialog = new ContentDialog
        {
            Title = $"想法 · {_detail.Summary.Title}",
            Content = new StackPanel
            {
                Spacing = 10,
                Width = 420,
                Children =
                {
                    new TextBlock { Text = $"“{quote}”", FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()), FontSize = 13, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap, MaxLines = 3 },
                    input,
                    counterTb,
                },
            },
            PrimaryButtonText = "保存",
            SecondaryButtonText = existingMark is not null && !string.IsNullOrWhiteSpace(existingMark.Note) ? "删掉想法" : "",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };

        var res = await dialog.ShowAsync();
        if (res == ContentDialogResult.Secondary && existingMark is not null)
        {
            _library.SetMarkNote(_bookId, existingMark.Id, null);
            HideSelectionCard();
            await RefreshSectionMarksAsync();
            return;
        }
        if (res == ContentDialogResult.Primary)
        {
            string? note = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim();
            if (existingMark is not null)
            {
                _library.SetMarkNote(_bookId, existingMark.Id, note);
            }
            else if (sel?.Start is not null && sel.End is not null)
            {
                _library.AddMark(_bookId, _currentSection,
                    new TextPoint((uint)sel.Start.S, (uint)sel.Start.O),
                    new TextPoint((uint)sel.End.S, (uint)sel.End.O),
                    quote, MarkStyle.Yellow, note);
            }

            HideSelectionCard();
            await _host.ClearSelectionAsync();
            await RefreshSectionMarksAsync();
        }
    }

    private async void DeleteOpenMark_Click(object sender, RoutedEventArgs e)
    {
        if (_openMark is null) return;
        _library.DeleteMark(_bookId, _openMark.Id);
        HideSelectionCard();
        await RefreshSectionMarksAsync();
    }

    private void SpeakFromSelection_Click(object sender, RoutedEventArgs e)
    {
        int sentence = _openMark is not null ? (int)_openMark.Start.Sentence : (_selection?.Start?.S ?? _currentSentence);
        HideSelectionCard();
        _ = _host.ClearSelectionAsync();
        StartListeningAt(sentence);
    }

    private void AskSelection_Click(object sender, RoutedEventArgs e)
    {
        string? quote = _openMark?.Text ?? _selection?.Text;
        if (string.IsNullOrWhiteSpace(quote)) return;
        HideSelectionCard();
        _ = _host.ClearSelectionAsync();
        OpenCompanionWithQuote(quote);
    }

    private async void TranslateSelection_Click(object sender, RoutedEventArgs e)
    {
        string? text = _openMark?.Text ?? _selection?.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        HideSelectionCard();
        await _host.ClearSelectionAsync();
        await ShowTranslationForStringAsync(text.Trim());
    }

    private async void ShareSelection_Click(object sender, RoutedEventArgs e)
    {
        string? quote = _openMark?.Text ?? _selection?.Text;
        string? note = _openMark?.Note;
        if (string.IsNullOrWhiteSpace(quote)) return;
        HideSelectionCard();
        await _host.ClearSelectionAsync();
        await QuoteShareDialog.ShowAsync(this, RootGrid.XamlRoot, quote, note, _detail.Summary.Title, _detail.Summary.Authors.FirstOrDefault(), _detail.Summary.CoverPath);
    }

    private void CopySelection_Click(object sender, RoutedEventArgs e)
    {
        string? text = _openMark?.Text ?? _selection?.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
        HideSelectionCard();
        _ = _host.ClearSelectionAsync();
    }

    private void ShowWordLookup(ReaderWord word)
    {
        _lookupWord = word;
        _lookupEntry = _library.LookUpWordRich(word.Text);
        _dictFullExpanded = false;
        DictEntryWebView.Visibility = Visibility.Collapsed;

        string headword = _lookupEntry?.Headword ?? word.Text;
        WordCardHeadword.Text = headword;
        WordCardPhonetic.Text = !string.IsNullOrWhiteSpace(_lookupEntry?.Phonetic)
            ? $"/{_lookupEntry.Phonetic.Trim('/', '[', ']')}/"
            : "";
        WordCardMeaning.Text = _lookupEntry is not null
            ? _lookupEntry.Summary
            : "本地词典暂无此词条。可在「设置 → 外文」导入 MDict (.mdx) 词典，或点「问书伴」结合语境释义。";

        bool hasHtml = !string.IsNullOrWhiteSpace(_lookupEntry?.Html);
        FullDictEntryBtn.Visibility = hasHtml ? Visibility.Visible : Visibility.Collapsed;
        FullDictEntryBtn.Content = "完整释义";

        string? phrase = ExtractWordPhrase(word.Sentence, word.Text);
        if (!string.IsNullOrWhiteSpace(phrase))
        {
            WordCardContext.Text = phrase;
            WordCardContext.Visibility = Visibility.Visible;
        }
        else
        {
            WordCardContext.Visibility = Visibility.Collapsed;
        }

        string freqText = "本书只出现这一次";
        try
        {
            uint bookTimes = _library.Core.BookWordCount(_bookId, headword);
            if (bookTimes == 0 && !string.Equals(headword, word.Text, StringComparison.Ordinal))
                bookTimes = _library.Core.BookWordCount(_bookId, word.Text);
            if (bookTimes > 1) freqText = $"本书出现 {bookTimes:N0} 次";
        }
        catch { }
        WordCardSourceText.Text = $"{_lookupEntry?.Source ?? "查词"} · {freqText}";

        bool alreadyInVocab = _library.GetWords().Any(w =>
            string.Equals(w.Word, headword, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(w.Word, word.Text, StringComparison.OrdinalIgnoreCase));
        AddVocabBtn.IsEnabled = !alreadyInVocab;
        AddVocabBtn.Content = alreadyInVocab ? "✓ 在生词本里" : "+ 加入生词本";

        _ = _host.SetLookingAsync(word.Start, word.End);

        double maxX = Math.Max(12, WebViewContainer.ActualWidth - 360);
        double x = Math.Clamp(word.Rect.X, 12, maxX);
        double y = word.Rect.Y + word.Rect.Height + 10;
        if (y + 190 > WebViewContainer.ActualHeight) y = Math.Max(60, word.Rect.Y - 180);
        WordCard.Margin = new Thickness(x, y, 0, 0);
        WordCard.Visibility = Visibility.Visible;

        // 1:1 ReaderForeign.swift:37 — 查词时自动用英音/美音系统发音朗读 (0.9×)
        if (_library.Prefs.ForeignSpeakOnLookup)
        {
            _ = AudioPlayer.SpeakForeignWordAsync(_library, word.Text, _detail.Summary.Language);
        }
    }

    private static string? ExtractWordPhrase(string? sentence, string word)
    {
        if (string.IsNullOrWhiteSpace(sentence)) return null;
        var tokens = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= 8) return sentence.Trim();
        int idx = Array.FindIndex(tokens, t => t.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return sentence.Trim();
        int from = Math.Max(0, idx - 4);
        int to = Math.Min(tokens.Length - 1, idx + 2);
        string core = string.Join(" ", tokens[from..(to + 1)]).Trim(',', ';', ':', '“', '”', '"', '\'', '‘', '’');
        return (from > 0 ? "…" : "") + core + (to < tokens.Length - 1 ? "…" : "");
    }

    private async void ToggleFullDictEntry_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lookupEntry?.Html)) return;
        _dictFullExpanded = !_dictFullExpanded;
        FullDictEntryBtn.Content = _dictFullExpanded ? "收起释义" : "完整释义";
        if (!_dictFullExpanded)
        {
            DictEntryWebView.Visibility = Visibility.Collapsed;
            return;
        }

        DictEntryWebView.Visibility = Visibility.Visible;
        try
        {
            if (!_dictWebReady)
            {
                var userDataDir = Path.Combine(_library.DataDir, "webview2");
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateWithOptionsAsync(null, userDataDir, null);
                await DictEntryWebView.EnsureCoreWebView2Async(env);
                DictEntryWebView.CoreWebView2.Settings.IsScriptEnabled = false;
                DictEntryWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                DictEntryWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                DictEntryWebView.CoreWebView2.NewWindowRequested += (_, ev) => ev.Handled = true;
                _dictWebReady = true;
            }
            bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
            DictEntryWebView.NavigateToString(BuildDictEntryHtml(_lookupEntry.Html, dark));
        }
        catch { }
    }

    private static string BuildDictEntryHtml(string rawHtml, bool dark)
    {
        string fg = dark ? "#E8E6E1" : "#2A2B2E";
        string link = dark ? "#8FB0E0" : "#2B4C7E";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <style>
            :root { color-scheme: {{(dark ? "dark" : "light")}}; }
            body { margin: 0; padding: 2px 4px 12px; font: 13.5px/1.6 "Segoe UI", "Microsoft YaHei UI", sans-serif; color: {{fg}}; background: transparent; overflow-wrap: anywhere; }
            img { max-width: 100%; height: auto; }
            a { color: {{link}}; pointer-events: none; text-decoration: none; }
            </style></head><body>{{rawHtml}}</body></html>
            """;
    }

    private void SpeakWord_Click(object sender, RoutedEventArgs e)
    {
        if (_lookupWord is null) return;
        _ = AudioPlayer.SpeakForeignWordAsync(_library, _lookupWord.Text, _detail.Summary.Language);
    }

    private void AddVocab_Click(object sender, RoutedEventArgs e)
    {
        if (_lookupWord is null) return;
        try
        {
            _library.AddWord(
                _lookupWord.Text,
                _lookupEntry?.Phonetic,
                _lookupEntry?.Summary,
                _bookId,
                _currentSection,
                (uint)_lookupWord.Start.S,
                _lookupWord.Sentence);
            AddVocabBtn.Content = "✓ 在生词本里";
            AddVocabBtn.IsEnabled = false;
            _ = ApplyForeignReadingAsync();
            App.CurrentApp.NotifyShelfRefresh();
        }
        catch { }
    }

    private void AskWord_Click(object sender, RoutedEventArgs e)
    {
        if (_lookupWord is null) return;
        var w = _lookupWord;
        WordCard.Visibility = Visibility.Collapsed;
        OpenCompanionWithQuote(w.Sentence);
        CompanionInputBox.Text = $"解释句子里“{w.Text}”在这句话中的准确含义";
    }

    private async Task ShowSentenceTranslationCardAsync(int sentenceIdx)
    {
        var sents = _library.GetSpokenSentences(_bookId, _currentSection);
        if (sentenceIdx < 0 || sentenceIdx >= sents.Count) return;
        await ShowTranslationForStringAsync(sents[sentenceIdx]);
    }

    private async Task ShowTranslationForStringAsync(string orig)
    {
        SentenceOrigText.Text = orig;
        SentenceTransText.Text = _library.Companion.IsConfigured ? "正在翻译…" : "尚未配置书伴翻译服务。可在「设置 → 书伴」填写 API Key 或本地 Ollama 后即时翻译。";
        SentenceCard.Visibility = Visibility.Visible;

        if (!_library.Companion.IsConfigured) return;
        try
        {
            string tr = await _library.Companion.ChatAsync(
                $"请把下面这句准确自然地译为简体中文（若是中文则译为英文），只输出译文：\n\n{orig}",
                "你是专业的文学翻译家，只输出译文正文。");
            SentenceTransText.Text = tr.Trim();
        }
        catch (Exception ex)
        {
            SentenceTransText.Text = $"翻译失败：{ex.Message}";
        }
    }

    private void CloseSentenceCard_Click(object sender, RoutedEventArgs e) => SentenceCard.Visibility = Visibility.Collapsed;

    private void CopySentenceTrans_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText($"{SentenceOrigText.Text}\n{SentenceTransText.Text}");
        Clipboard.SetContent(dp);
        SentenceCard.Visibility = Visibility.Collapsed;
    }

    private void ShowFootnote(string target)
    {
        try
        {
            var fn = _library.GetFootnote(_bookId, _currentSection, target);
            if (fn is null) { HandleInternalLink(target); return; }
            _openFootnote = fn;
            FootnoteTitleText.Text = "注释";
            FootnoteBodyText.Text = fn.Text;
            FootnoteMetaText.Text = fn.Index is uint idx ? $"{_detail.Summary.Title} · 注释 {idx} / {fn.Count}" : _detail.Summary.Title;
            FootnoteCard.Visibility = Visibility.Visible;
        }
        catch
        {
            HandleInternalLink(target);
        }
    }

    private void CloseFootnote_Click(object sender, RoutedEventArgs e) => FootnoteCard.Visibility = Visibility.Collapsed;

    private void JumpFootnote_Click(object sender, RoutedEventArgs e)
    {
        if (_openFootnote is null) return;
        _returnFrom = (_currentSection, _currentSentence);
        ReturnPillText.Text = $"↩ 回到第 {EstimateBookPage(_currentSection, _currentSentence)} 页";
        ReturnPillBtn.Visibility = Visibility.Visible;
        uint sec = _openFootnote.Section;
        FootnoteCard.Visibility = Visibility.Collapsed;
        LoadSection(sec);
    }

    private async Task RefreshSectionMarksAsync()
    {
        var sectionMarks = _library.GetMarks(_bookId).Where(m => m.Section == _currentSection);
        await _host.SetMarksAsync(sectionMarks);
        if (_activePanel is "notes" or "toc") RenderSidePanel();
        App.CurrentApp.NotifyShelfRefresh();
    }

    // ---------- Bookmarks ----------

    private bool HasBookmarkHere() => _library.GetBookmarks(_bookId)
        .Any(b => b.Section == _currentSection && Math.Abs((int)b.Sentence - _currentSentence) <= 2);

    private void ToggleBookmark_Click(object sender, RoutedEventArgs e)
    {
        var existing = _library.GetBookmarks(_bookId)
            .FirstOrDefault(b => b.Section == _currentSection && Math.Abs((int)b.Sentence - _currentSentence) <= 2);

        if (existing is not null) _library.DeleteBookmark(_bookId, existing.Id);
        else _library.AddBookmark(_bookId, _currentSection, (uint)Math.Max(0, _currentSentence));

        ApplyTintAndChrome();
        if (_activePanel is "toc" or "notes") RenderSidePanel();
    }

    // ---------- Search (1:1 MacReaderView.swift:536-580 & ReaderNotes.swift:252) ----------

    private void ToggleSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_searchShown) CloseSearch();
        else OpenSearch();
    }

    private void OpenSearch()
    {
        _searchShown = true;
        InlineSearchCapsule.Visibility = Visibility.Visible;
        OpenPanel("search");
        BookSearchBox.Focus(FocusState.Programmatic);
        ApplyTintAndChrome();
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e) => CloseSearch();

    private void CloseSearch()
    {
        _searchShown = false;
        _searchHits = [];
        _hitIndex = -1;
        BookSearchBox.Text = "";
        SearchHitCountText.Text = "";
        InlineSearchCapsule.Visibility = Visibility.Collapsed;
        _ = _host.SetHitsAsync(null);
        if (_activePanel == "search") ClosePanel();
        ApplyTintAndChrome();
    }

    private void BookSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RunSearch();

    private void BookSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Enter) { StepHit(forward: !shift); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { CloseSearch(); e.Handled = true; }
    }

    private void RunSearch()
    {
        var q = BookSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            _searchHits = [];
            _hitIndex = -1;
            SearchHitCountText.Text = "";
            SearchPrevBtn.IsEnabled = false;
            SearchNextBtn.IsEnabled = false;
            _ = _host.SetHitsAsync(null);
            if (_activePanel == "search") RenderSidePanel();
            return;
        }

        _searchHits = _library.SearchBook(_bookId, q);
        SearchPrevBtn.IsEnabled = _searchHits.Count > 0;
        SearchNextBtn.IsEnabled = _searchHits.Count > 0;

        if (_searchHits.Count == 0)
        {
            _hitIndex = -1;
            SearchHitCountText.Text = "没找到";
        }
        else
        {
            // 1:1 ReaderNotes.swift:252：默认定位在当前章节与句子之后的第一处匹配
            int firstAfter = -1;
            for (int i = 0; i < _searchHits.Count; i++)
            {
                var h = _searchHits[i];
                if (h.Section > _currentSection || (h.Section == _currentSection && (int)h.Sentence >= _currentSentence))
                {
                    firstAfter = i;
                    break;
                }
            }
            _hitIndex = firstAfter >= 0 ? firstAfter : 0;
            SearchHitCountText.Text = $"第 {_hitIndex + 1} / {_searchHits.Count}{(_searchHits.Count >= 500 ? "+" : "")} 处";
        }

        _ = _host.SetHitsAsync(q);
        if (_activePanel == "search") RenderSidePanel();
    }

    private void PrevHit_Click(object sender, RoutedEventArgs e) => StepHit(forward: false);
    private void NextHit_Click(object sender, RoutedEventArgs e) => StepHit(forward: true);

    private void StepHit(bool forward)
    {
        if (_searchHits.Count == 0) return;
        _hitIndex = forward ? (_hitIndex + 1) % _searchHits.Count : (_hitIndex - 1 + _searchHits.Count) % _searchHits.Count;
        JumpToHit(_searchHits[_hitIndex]);
        if (_activePanel == "search") RenderSidePanel();
    }

    private void JumpToHit(SearchHit hit)
    {
        SearchHitCountText.Text = $"第 {_hitIndex + 1} / {_searchHits.Count}{(_searchHits.Count >= 500 ? "+" : "")} 处";
        var q = BookSearchBox.Text.Trim();
        if (hit.Section == _currentSection)
        {
            _ = _host.ScrollToSentenceAsync((int)hit.Sentence, animated: true);
            _ = _host.SetHitsAsync(q, (int)hit.Sentence, (int)hit.Nth);
        }
        else { _pendingSentence = (int)hit.Sentence; LoadSection(hit.Section); }
    }

    // ---------- Side Panels (1:1 MacReaderView.swift:1079-1653 & CastView.swift:855-1068) ----------

    private void TogglePanel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string panel) return;
        ToggleNamedPanel(panel);
    }

    private void ToggleNamedPanel(string panel)
    {
        if (_activePanel == panel) ClosePanel();
        else OpenPanel(panel);
    }

    private void OpenPanel(string panel)
    {
        _activePanel = panel;
        bool isCompanion = panel == "companion";
        // 1:1 MacReaderView.swift:46-51：书伴宽 344px (+14 右边距 = 358)，其余面板宽 308px (+14 右边距 = 322)
        SidePanelColumn.Width = new GridLength(isCompanion ? 358 : 322);
        SidePanelBorder.Visibility = Visibility.Visible;

        CompanionPanel.Visibility = isCompanion ? Visibility.Visible : Visibility.Collapsed;
        DynamicSideHost.Visibility = isCompanion ? Visibility.Collapsed : Visibility.Visible;

        if (!isCompanion) RenderSidePanel();
        else UpdateCompanionScopeVisuals();
        ApplyTintAndChrome();
        UpdatePaperChrome();
    }

    private void ClosePanel()
    {
        _activePanel = null;
        SidePanelColumn.Width = new GridLength(0);
        SidePanelBorder.Visibility = Visibility.Collapsed;
        ApplyTintAndChrome();
        UpdatePaperChrome();
    }

    private Border BuildPanelTabs((string Key, string Label)[] tabs, string current, Action<string> onSelect)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var grid = new Grid { ColumnSpacing = 2 };
        for (int i = 0; i < tabs.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (key, lbl) = tabs[i];
            bool on = key == current;
            var btn = new Button
            {
                Content = lbl,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(4, 0, 4, 0),
                FontSize = 11.5,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                UseSystemFocusVisuals = false,
            };
            string k = key;
            btn.Click += (_, _) => onSelect(k);
            Grid.SetColumn(btn, i);
            grid.Children.Add(btn);
        }

        return new Border
        {
            Background = ThemeHelper.Brush(p.Inset),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(3),
            Child = grid,
        };
    }

    private void RenderSidePanel()
    {
        SideHeaderHost.Children.Clear();
        SideBodyStack.Children.Clear();

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        switch (_activePanel)
        {
            case "notes":
                RenderNotesSidePanel(p, tint, dark);
                break;
            case "toc":
                RenderTocSidePanel(p, tint);
                break;
            case "search":
                RenderSearchSidePanel(p, tint);
                break;
            case "cast":
                RenderCastSidePanel(p, tint);
                break;
        }
    }

    // 1:1 MacReaderView.swift:1338-1545：笔记面板（含导出菜单 + 换个样子子菜单）
    private void RenderNotesSidePanel(YYPalette p, YYTintColors tint, bool dark)
    {
        var marks = _library.GetMarks(_bookId)
            .OrderBy(m => m.Section).ThenBy(m => m.Start.Sentence).ThenBy(m => m.Start.Offset)
            .ToArray();
        var thoughts = marks.Where(m => !string.IsNullOrWhiteSpace(m.Note)).ToArray();
        var words = _library.GetWords().Where(w => w.Book == _bookId).ToArray();

        var topRow = new Grid();
        topRow.Children.Add(new TextBlock { Text = "笔记", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });

        var expMenu = new MenuFlyout();
        var mdItem = new MenuFlyoutItem { Text = "导出 Markdown (.md)…" };
        mdItem.Click += async (_, _) =>
        {
            var picker = new FileSavePicker { SuggestedFileName = $"{_detail.Summary.Title} 笔记" };
            picker.FileTypeChoices.Add("Markdown", [".md"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var f = await picker.PickSaveFileAsync();
            if (f is not null)
                await File.WriteAllTextAsync(f.Path, NoteExporter.BuildBookMarkdown(_library, _detail.Summary));
        };
        expMenu.Items.Add(mdItem);
        expMenu.Items.Add(new MenuFlyoutSeparator());
        var targetCfg = new MenuFlyoutItem { Text = "导出设置（Obsidian / Notion / flomo…）" };
        targetCfg.Click += (_, _) => App.CurrentApp.OpenSettings("export");
        expMenu.Items.Add(targetCfg);

        var expBtn = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Inset),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            UseSystemFocusVisuals = false,
            IsEnabled = marks.Length > 0 || words.Length > 0,
            Content = YYIcons.Make("share-up", 11.5, ThemeHelper.Brush(p.T1)),
            Flyout = expMenu,
        };
        topRow.Children.Add(expBtn);
        SideHeaderHost.Children.Add(topRow);

        var tabList = new List<(string Key, string Label)>
        {
            ("marks", $"划线 {marks.Length}"),
            ("thoughts", $"想法 {thoughts.Length}"),
        };
        if (_isForeignBook || words.Length > 0)
            tabList.Add(("words", $"生词 {words.Length}"));

        if (!tabList.Any(t => t.Key == _notesTab)) _notesTab = "marks";
        SideHeaderHost.Children.Add(BuildPanelTabs(tabList.ToArray(), _notesTab, next => { _notesTab = next; RenderSidePanel(); }));

        if (_notesTab == "words")
        {
            if (words.Length == 0)
            {
                AddSideEmpty(p, "还没有生词", "点一个词，在卡片里点「+ 加入生词本」");
                return;
            }

            // 子筛选：全部 / 在学 / 已掌握
            int learnCnt = words.Count(w => !w.Known);
            int knownCnt = words.Count(w => w.Known);
            var subRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(2, 2, 2, 8) };
            foreach (var (fk, fl) in new[] { ("all", $"全部 {words.Length}"), ("learning", $"在学 {learnCnt}"), ("known", $"已掌握 {knownCnt}") })
            {
                bool on = _wordsFilter == fk;
                var fb = new Button
                {
                    Content = fl,
                    Height = 23,
                    MinHeight = 23,
                    Padding = new Thickness(9, 0, 9, 0),
                    CornerRadius = new CornerRadius(11.5),
                    BorderThickness = new Thickness(0),
                    FontSize = 11,
                    Background = ThemeHelper.Brush(on ? tint.Soft : p.Inset),
                    Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T2),
                    UseSystemFocusVisuals = false,
                };
                string key = fk;
                fb.Click += (_, _) => { _wordsFilter = key; RenderSidePanel(); };
                subRow.Children.Add(fb);
            }
            SideBodyStack.Children.Add(subRow);

            var filteredWords = _wordsFilter switch
            {
                "learning" => words.Where(w => !w.Known).ToArray(),
                "known" => words.Where(w => w.Known).ToArray(),
                _ => words,
            };

            for (int i = 0; i < filteredWords.Length; i++)
            {
                if (i > 0) SideBodyStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });
                var w = filteredWords[i];
                var sp = new StackPanel { Spacing = 4, Padding = new Thickness(4, 10, 4, 10) };
                var hw = new Grid();
                var hwLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                hwLeft.Children.Add(new TextBlock { Text = w.Word, FontFamily = new FontFamily("Georgia"), FontSize = 14.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
                if (!string.IsNullOrWhiteSpace(w.Phonetic))
                    hwLeft.Children.Add(new TextBlock { Text = $"/{w.Phonetic.Trim('/', '[', ']')}/", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Bottom });
                hw.Children.Add(hwLeft);
                if (w.Known)
                    hw.Children.Add(new TextBlock { Text = "掌握了", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(hw);

                if (!string.IsNullOrEmpty(w.Meaning))
                    sp.Children.Add(new TextBlock { Text = w.Meaning, FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                if (!string.IsNullOrEmpty(w.Context))
                    sp.Children.Add(new TextBlock { Text = w.Context, FontFamily = new FontFamily("Georgia"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });

                var btn = MakeSideItemButton(sp);
                uint wSec = w.Section;
                int wSent = (int)w.Sentence;
                btn.Click += (_, _) => JumpTo(wSec, wSent);
                SideBodyStack.Children.Add(btn);
            }
            return;
        }

        var shownMarks = _notesTab == "thoughts" ? thoughts : marks;
        if (shownMarks.Length == 0)
        {
            AddSideEmpty(p, _notesTab == "thoughts" ? "还没有想法" : "还没有划线", "选中文字即可划上并记录想法");
            return;
        }

        PopulateMarkItemsIntoSideBody(shownMarks, p, tint, dark);
    }

    private void PopulateMarkItemsIntoSideBody(IReadOnlyList<Mark> marks, YYPalette p, YYTintColors tint, bool dark)
    {
        for (int i = 0; i < marks.Count; i++)
        {
            if (i > 0) SideBodyStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });
            var mk = marks[i];
            string chTitle = mk.Section < _detail.Sections.Length && !string.IsNullOrWhiteSpace(_detail.Sections[mk.Section].Title)
                ? _detail.Sections[mk.Section].Title!
                : $"第 {EstimateBookPage(mk.Section, (int)mk.Start.Sentence)} 页";
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(mk.At).LocalDateTime;
            string dayText = dt.Date == DateTime.Today ? "今天" : dt.Date == DateTime.Today.AddDays(-1) ? "昨天" : $"{dt.Month}月{dt.Day}日";

            var sp = new StackPanel { Spacing = 6, Padding = new Thickness(4, 11, 4, 11) };
            var metaRow = new Grid();
            metaRow.Children.Add(new TextBlock { Text = chTitle, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 56, 0) });
            metaRow.Children.Add(new TextBlock { Text = dayText, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
            sp.Children.Add(metaRow);

            sp.Children.Add(ShelfWindow.BuildHighlightedQuote(mk, p, dark, 13));

            if (!string.IsNullOrWhiteSpace(mk.Note))
            {
                var tRow = new Grid { ColumnSpacing = 8, Margin = new Thickness(1, 2, 0, 0) };
                tRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                tRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                tRow.Children.Add(new Border { Background = ThemeHelper.Brush(tint.Accent), CornerRadius = new CornerRadius(1) });
                var ntb = new TextBlock
                {
                    Text = mk.Note,
                    FontFamily = new FontFamily(YYFontFamily.Kaiti.WindowsFamily()),
                    FontSize = 12.5,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    TextWrapping = TextWrapping.Wrap,
                };
                Grid.SetColumn(ntb, 1);
                tRow.Children.Add(ntb);
                sp.Children.Add(tRow);
            }

            // 右键菜单：写想法… / 换个样子（6 种样式） / 分享书摘… / 删除划线
            var ctx = new MenuFlyout();
            var editNoteItem = new MenuFlyoutItem { Text = string.IsNullOrWhiteSpace(mk.Note) ? "写想法…" : "改想法…" };
            editNoteItem.Click += (_, _) => { _openMark = mk; AddThought_Click(editNoteItem, new RoutedEventArgs()); };

            var styleSub = new MenuFlyoutSubItem { Text = "换个样子" };
            foreach (var (sName, sVal) in new[]
            {
                ("黄色", MarkStyle.Yellow),
                ("蓝色", MarkStyle.Blue),
                ("粉色", MarkStyle.Pink),
                ("波浪线", MarkStyle.Wave),
                ("下划线", MarkStyle.Underline),
                ("点线", MarkStyle.Dotted),
            })
            {
                var si = new MenuFlyoutItem { Text = mk.Style == sVal ? $"✓ {sName}" : sName };
                var nextStyle = sVal;
                string markId = mk.Id;
                si.Click += async (_, _) =>
                {
                    _library.SetMarkStyle(_bookId, markId, nextStyle);
                    await RefreshSectionMarksAsync();
                };
                styleSub.Items.Add(si);
            }

            var shareItem = new MenuFlyoutItem { Text = "分享书摘…" };
            shareItem.Click += async (_, _) => await QuoteShareDialog.ShowAsync(this, RootGrid.XamlRoot, mk.Text, mk.Note, _detail.Summary.Title, _detail.Summary.Authors.FirstOrDefault(), _detail.Summary.CoverPath);

            var delItem = new MenuFlyoutItem { Text = "删除划线", Foreground = ThemeHelper.Brush(p.Danger) };
            delItem.Click += async (_, _) => { _library.DeleteMark(_bookId, mk.Id); await RefreshSectionMarksAsync(); };

            ctx.Items.Add(editNoteItem);
            ctx.Items.Add(styleSub);
            ctx.Items.Add(shareItem);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(delItem);

            var btn = MakeSideItemButton(sp);
            btn.ContextFlyout = ctx;
            uint mSec = mk.Section;
            int mSent = (int)mk.Start.Sentence;
            btn.Click += (_, _) => JumpTo(mSec, mSent);
            SideBodyStack.Children.Add(btn);
        }
    }

    // 1:1 MacReaderView.swift:1079-1317：目录面板（2 分段 目录/书签 + 状态行 + 自适应章节号分栏 + 各章阅读时长）
    private void RenderTocSidePanel(YYPalette p, YYTintColors tint)
    {
        var rawItems = new List<(string Title, uint Section, string? Anchor, ushort Depth)>();
        if (_detail.Toc.Length > 0)
            foreach (var t in _detail.Toc) rawItems.Add((t.Title, t.Section, t.Anchor, t.Depth));
        else
            for (uint i = 0; i < _detail.Sections.Length; i++)
                if (!string.IsNullOrWhiteSpace(_detail.Sections[i].Title))
                    rawItems.Add((_detail.Sections[i].Title!, i, null, 0));

        var items = new List<TocEntry>(rawItems.Count);
        for (int i = 0; i < rawItems.Count; i++)
        {
            var r = rawItems[i];
            int start = (int)r.Section;
            int end = _detail.Sections.Length;
            for (int j = i + 1; j < rawItems.Count; j++)
            {
                if ((int)rawItems[j].Section > start) { end = (int)rawItems[j].Section; break; }
            }
            int rangeEnd = Math.Max(start + 1, end);
            bool saw = false, allBoilerplate = true;
            for (int s = start; s < rangeEnd && s < _detail.Sections.Length; s++)
            {
                saw = true;
                if (_detail.Sections[s].Kind != SectionKind.Boilerplate) { allBoilerplate = false; break; }
            }
            items.Add(new TocEntry(r.Title, r.Section, r.Anchor, r.Depth, saw && allBoilerplate));
        }

        var mainItems = items.Where(it => !it.IsBoilerplate).ToList();
        var boilerplateItems = items.Where(it => it.IsBoilerplate).ToList();
        int displayCount = mainItems.Count > 0 ? mainItems.Count : items.Count;

        var bookmarks = _library.GetBookmarks(_bookId);
        if (_tocTab is not ("toc" or "bookmarks")) _tocTab = "toc";

        SideHeaderHost.Children.Add(BuildPanelTabs(
            [
                ("toc", $"目录 {displayCount}"),
                ("bookmarks", $"书签 {bookmarks.Count}"),
            ],
            _tocTab,
            next => { _tocTab = next; RenderSidePanel(); }));

        int bookPct = EstimateBookPercent(_currentSection, _currentSentence);
        double frac = Math.Clamp(bookPct / 100.0, 0.02, 1.0);
        var progGrid = new Grid { Height = 2.5 };
        progGrid.Children.Add(new Border { CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(p.Line) });
        var fillG = new Grid();
        fillG.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
        fillG.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0 - frac, GridUnitType.Star) });
        fillG.Children.Add(new Border { CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(tint.Fill) });
        progGrid.Children.Add(fillG);
        SideHeaderHost.Children.Add(progGrid);

        // 章节耗时统计
        var secTimes = new Dictionary<uint, SectionTime>();
        uint totalReadSec = 0, totalListenSec = 0;
        try
        {
            foreach (var st in _library.Core.SectionTimes(_bookId))
            {
                secTimes[st.Section] = st;
                totalReadSec += st.ReadSeconds;
                totalListenSec += st.ListenSeconds;
            }
        }
        catch { }

        int currentIdx = items.FindLastIndex(it => it.Section <= _currentSection);
        var statusLine = new Grid();
        string curChapShort = currentIdx >= 0 ? $"第 {currentIdx + 1} 章" : "这里";
        statusLine.Children.Add(new TextBlock { Text = $"读到{curChapShort} · {bookPct}%", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) });
        if (totalReadSec + totalListenSec >= 60)
        {
            var timeParts = new List<string>();
            if (totalReadSec >= 60) timeParts.Add($"读 {Math.Max(1, totalReadSec / 60)} 分钟");
            if (totalListenSec >= 60) timeParts.Add($"听 {Math.Max(1, totalListenSec / 60)} 分钟");
            statusLine.Children.Add(new TextBlock { Text = string.Join(" · ", timeParts), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
        }
        SideHeaderHost.Children.Add(statusLine);

        if (_tocTab == "bookmarks")
        {
            bool hereBm = HasBookmarkHere();
            if (bookmarks.Count == 0 && !hereBm)
            {
                AddSideEmpty(p, "还没有书签", "按 Ctrl+D 给这一页加一个");
                var addFirstBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "给这一页加书签", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
                addFirstBtn.Click += ToggleBookmark_Click;
                SideBodyStack.Children.Add(addFirstBtn);
                return;
            }

            var toggleBmBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(hereBm ? tint.Soft : p.Inset),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(hereBm ? tint.Accent : p.T1),
                UseSystemFocusVisuals = false,
                Content = hereBm ? "✓ 这一页有书签 · 点一下拿掉" : "+ 给这一页加书签",
            };
            toggleBmBtn.Click += ToggleBookmark_Click;
            SideBodyStack.Children.Add(toggleBmBtn);

            foreach (var bm in bookmarks)
            {
                int bmPage = EstimateBookPage(bm.Section, (int)bm.Sentence);
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(bm.At).LocalDateTime;
                string dText = dt.Date == DateTime.Today ? "今天" : dt.Date == DateTime.Today.AddDays(-1) ? "昨天" : $"{dt.Month}月{dt.Day}日";

                var sp = new StackPanel { Spacing = 5, Padding = new Thickness(4, 10, 4, 10) };
                var top = new Grid();
                var topLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                topLeft.Children.Add(YYIcons.Make("bookmark", 11, ThemeHelper.Brush(tint.Accent), filled: true));
                topLeft.Children.Add(new TextBlock { Text = $"第 {bmPage} 页", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
                top.Children.Add(topLeft);
                top.Children.Add(new TextBlock { Text = dText, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
                sp.Children.Add(top);

                var qGrid = new Grid { ColumnSpacing = 8 };
                qGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                qGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                qGrid.Children.Add(new Border { Background = ThemeHelper.Brush(p.Line), CornerRadius = new CornerRadius(1) });
                var qTb = new TextBlock { Text = bm.Text, FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(qTb, 1);
                qGrid.Children.Add(qTb);
                sp.Children.Add(qGrid);

                var btn = MakeSideItemButton(sp);
                uint bSec = bm.Section;
                int bSent = (int)bm.Sentence;
                string bid = bm.Id;
                btn.Click += (_, _) => JumpTo(bSec, bSent);
                var ctx = new MenuFlyout();
                var rm = new MenuFlyoutItem { Text = "删除书签", Foreground = ThemeHelper.Brush(p.Danger) };
                rm.Click += (_, _) => { _library.DeleteBookmark(_bookId, bid); ApplyTintAndChrome(); RenderSidePanel(); };
                ctx.Items.Add(rm);
                btn.ContextFlyout = ctx;
                SideBodyStack.Children.Add(btn);
                SideBodyStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });
            }
            return;
        }

        if (items.Count == 0)
        {
            AddSideEmpty(p, "这本书没有目录", "书里没写章节，往后翻就行");
            return;
        }

        double numColWidth = 46;
        foreach (var entry in (mainItems.Count > 0 ? mainItems : items))
        {
            var (cn, _) = SplitChapterTitle(entry.Title);
            if (cn is null) continue;
            double units = 0;
            foreach (char ch in cn) units += ch < 128 ? 0.65 : 1.0;
            numColWidth = Math.Max(numColWidth, Math.Min(84, Math.Ceiling(units * 11.5 + 6)));
        }

        int lastMainIdx = items.FindLastIndex(it => !it.IsBoilerplate);
        for (int i = 0; i < items.Count; i++)
        {
            var entry = items[i];
            if (entry.IsBoilerplate) continue;
            bool cur = i == currentIdx;
            bool past = currentIdx > i;
            var (chapNum, chapRest) = SplitChapterTitle(entry.Title);

            var rowGrid = new Grid { ColumnSpacing = 8, Padding = new Thickness(6 + entry.Depth * 10, cur ? 9 : 8, 6, cur ? 9 : 8) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(chapNum is not null ? numColWidth : 0) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (chapNum is not null)
            {
                rowGrid.Children.Add(new TextBlock
                {
                    Text = chapNum,
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(cur ? tint.Accent : p.T3),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 1, 0, 0),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            var rightCol = new StackPanel { Spacing = 3 };
            rightCol.Children.Add(new TextBlock
            {
                Text = chapRest,
                FontSize = 12.5,
                FontWeight = cur ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(cur ? tint.Accent : past ? p.T3 : p.T1),
                MaxLines = cur ? 2 : 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            int secSents = entry.Section < _detail.Sections.Length ? (int)_detail.Sections[entry.Section].Sentences : 20;
            int estMins = Math.Max(1, secSents / 14);
            string subLine;
            if (cur)
            {
                int chapPct = _layout is not null ? (int)Math.Round(100.0 * (_currentPage + 1) / Math.Max(1, _layout.PageCount)) : 10;
                int remMins = Math.Max(1, (int)Math.Round(estMins * (1.0 - chapPct / 100.0)));
                subLine = $"读到 {chapPct}% · 还剩 {remMins} 分钟";
            }
            else if (past)
            {
                if (secTimes.TryGetValue(entry.Section, out var st) && st.ReadSeconds + st.ListenSeconds >= 30)
                    subLine = $"读 {Math.Max(1, (st.ReadSeconds + st.ListenSeconds) / 60)} 分钟";
                else
                    subLine = "翻过";
            }
            else
            {
                subLine = $"约 {estMins} 分钟";
            }
            rightCol.Children.Add(new TextBlock { Text = subLine, FontSize = 10.5, Foreground = ThemeHelper.Brush(cur ? tint.Accent : p.T3) });

            if (cur && _layout is not null)
            {
                double cf = Math.Clamp((_currentPage + 1.0) / Math.Max(1, _layout.PageCount), 0.05, 1.0);
                var miniTrack = new Grid { Width = 72, Height = 2.5, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
                miniTrack.Children.Add(new Border { CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(p.Line) });
                var mf = new Grid();
                mf.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cf, GridUnitType.Star) });
                mf.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0 - cf, GridUnitType.Star) });
                mf.Children.Add(new Border { CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(tint.Fill) });
                miniTrack.Children.Add(mf);
                rightCol.Children.Add(miniTrack);
            }

            Grid.SetColumn(rightCol, 1);
            rowGrid.Children.Add(rightCol);

            var btn = MakeSideItemButton(rowGrid);
            if (cur)
            {
                btn.Background = ThemeHelper.Brush(tint.AccentSoft);
                btn.CornerRadius = new CornerRadius(10);
            }
            var targetEntry = entry;
            btn.Click += (_, _) =>
            {
                _pendingAnchor = targetEntry.Anchor;
                _pendingSentence = targetEntry.Anchor is null ? 0 : null;
                LoadSection(targetEntry.Section);
            };
            SideBodyStack.Children.Add(btn);
            if (!cur && i + 1 != currentIdx && i < lastMainIdx)
                SideBodyStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });
        }

        if (boilerplateItems.Count > 0)
        {
            SideBodyStack.Children.Add(new TextBlock
            {
                Text = "附录与版权",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(6, mainItems.Count > 0 ? 14 : 4, 6, 6),
            });
            for (int bi = 0; bi < items.Count; bi++)
            {
                var entry = items[bi];
                if (!entry.IsBoilerplate) continue;
                bool cur = bi == currentIdx;
                var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(6, 8, 6, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var ico = YYIcons.Make("note-card", 12, ThemeHelper.Brush(cur ? tint.Accent : p.T3));
                row.Children.Add(ico);

                var titleTb = new TextBlock
                {
                    Text = entry.Title,
                    FontSize = 11,
                    FontWeight = cur ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(cur ? tint.Accent : p.T2),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(titleTb, 1);
                row.Children.Add(titleTb);

                var tag = new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Background = ThemeHelper.Brush(cur ? tint.AccentSoft : p.Inset),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "版权",
                        FontSize = 9.5,
                        Foreground = ThemeHelper.Brush(cur ? tint.Accent : p.T3),
                    },
                };
                Grid.SetColumn(tag, 2);
                row.Children.Add(tag);

                var bpBtn = MakeSideItemButton(row);
                if (cur)
                {
                    bpBtn.Background = ThemeHelper.Brush(tint.AccentSoft);
                    bpBtn.CornerRadius = new CornerRadius(9);
                }
                var targetEntry = entry;
                bpBtn.Click += (_, _) =>
                {
                    _pendingAnchor = targetEntry.Anchor;
                    _pendingSentence = targetEntry.Anchor is null ? 0 : null;
                    LoadSection(targetEntry.Section);
                };
                SideBodyStack.Children.Add(bpBtn);
            }
        }
    }

    // 1:1 MacReaderView.swift:1548-1653：搜索结果按估算页码分组，高光加粗匹配词（无【】括号）
    private void RenderSearchSidePanel(YYPalette p, YYTintColors tint)
    {
        var q = BookSearchBox.Text.Trim();
        var head = new Grid();
        var headLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headLeft.Children.Add(new TextBlock { Text = "搜索结果", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
        if (!string.IsNullOrEmpty(q))
            headLeft.Children.Add(new TextBlock { Text = $"{_searchHits.Count}{(_searchHits.Count >= 500 ? "+" : "")} 处", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Bottom });
        head.Children.Add(headLeft);
        SideHeaderHost.Children.Add(head);

        if (string.IsNullOrEmpty(q))
        {
            AddSideEmpty(p, "搜这本书", "在顶部输入关键词，按回车跳到下一处");
            return;
        }
        if (_searchHits.Count == 0)
        {
            AddSideEmpty(p, "没找到", "换个词试试");
            return;
        }

        int curBookPage = EstimateBookPage(_currentSection, _currentSentence);
        var indexed = _searchHits.Select((h, idx) => (Hit: h, Index: idx, Page: EstimateBookPage(h.Section, (int)h.Sentence))).ToArray();

        foreach (var grp in indexed.GroupBy(x => x.Page))
        {
            bool here = grp.Key == curBookPage;
            var grpHead = new Grid { Margin = new Thickness(4, 8, 4, 4) };
            grpHead.Children.Add(new TextBlock { Text = $"第 {grp.Key} 页", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(here ? tint.Accent : p.T3) });
            grpHead.Children.Add(new TextBlock { Text = here ? $"{grp.Count()} 处 · 这一页" : $"{grp.Count()} 处", FontSize = 10.5, Foreground = ThemeHelper.Brush(here ? tint.Accent : p.T3), HorizontalAlignment = HorizontalAlignment.Right });
            SideBodyStack.Children.Add(grpHead);

            foreach (var item in grp)
            {
                bool on = item.Index == _hitIndex;
                var tb = new TextBlock
                {
                    FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                    FontSize = 12.5,
                    Foreground = ThemeHelper.Brush(p.T1),
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.Wrap,
                };
                if (!string.IsNullOrEmpty(item.Hit.Before)) tb.Inlines.Add(new Run { Text = item.Hit.Before });
                tb.Inlines.Add(new Run { Text = item.Hit.Matched, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(tint.Accent) });
                if (!string.IsNullOrEmpty(item.Hit.After)) tb.Inlines.Add(new Run { Text = item.Hit.After });

                var wrap = new Border { Padding = new Thickness(6, 7, 6, 7), Child = tb };
                var btn = MakeSideItemButton(wrap);
                if (on) btn.Background = ThemeHelper.Brush(tint.AccentSoft);
                int idx = item.Index;
                var hit = item.Hit;
                btn.Click += (_, _) => { _hitIndex = idx; JumpToHit(hit); RenderSidePanel(); };
                SideBodyStack.Children.Add(btn);
            }
        }
        if (_searchHits.Count >= 500)
        {
            SideBodyStack.Children.Add(new TextBlock
            {
                Text = "仅显示前 500 处，输入更长的词可缩小范围",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(6, 12, 6, 8),
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    // 1:1 CastView.swift:855-1068：人物与广播剧配音面板（替代旧版旁白面板）
    private void RenderCastSidePanel(YYPalette p, YYTintColors tint)
    {
        CastMember[] cast;
        Role[] roles;
        try
        {
            cast = _library.Core.BookCast(_bookId, _currentSection);
            roles = _library.Core.BookRoles(_bookId);
        }
        catch
        {
            cast = [];
            roles = [];
        }

        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(new TextBlock { Text = "人物", FontSize = 14.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
        head.Children.Add(new TextBlock { Text = $"读到这里 · {cast.Length} 人", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
        SideHeaderHost.Children.Add(head);

        var body = new StackPanel { Spacing = 12, Padding = new Thickness(2, 4, 2, 12) };

        // 旁白默认声音行
        body.Children.Add(new TextBlock { Text = "旁白", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3) });
        var curVoice = AudioPlayer.GetAllVoiceOptions(_library).FirstOrDefault(v => v.Id == _library.Prefs.VoiceId || v.Id == "sys:" + _library.Prefs.VoiceId);
        var narGrid = new Grid { ColumnSpacing = 10 };
        narGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        narGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        narGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        narGrid.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = ThemeHelper.Brush(tint.Soft), Child = YYIcons.Make("speaker", 12.5, ThemeHelper.Brush(tint.Accent)) });
        var narMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        narMeta.Children.Add(new TextBlock { Text = "旁白 · 全书默认", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
        narMeta.Children.Add(new TextBlock { Text = curVoice?.Label ?? "系统默认语音", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(narMeta, 1);
        narGrid.Children.Add(narMeta);
        var narChg = new TextBlock { Text = "换声音 ›", FontSize = 11, Foreground = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(narChg, 2);
        narGrid.Children.Add(narChg);

        var narBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Inset),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 8, 10, 8),
            UseSystemFocusVisuals = false,
            Content = narGrid,
        };
        narBtn.Click += async (_, _) => await ShowVoicePickerSheetAsync("旁白的声音", vid =>
        {
            _library.Prefs.VoiceId = vid;
            _library.SavePrefs();
            ReloadVoicePrefs();
            RenderSidePanel();
        });
        body.Children.Add(narBtn);

        // 出场人物列表
        if (cast.Length > 0)
        {
            body.Children.Add(new TextBlock { Text = "出场人物", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(0, 4, 0, 0) });
            var roleMap = roles.ToDictionary(r => r.Name, r => r);
            var castStack = new StackPanel { Spacing = 4 };
            foreach (var member in cast.Take(24))
            {
                Color avCol = CustomPaperDialog.ParseHexColor(member.Color, tint.Accent);
                roleMap.TryGetValue(member.Name, out var customRole);

                var cg = new Grid { ColumnSpacing = 10 };
                cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                cg.Children.Add(new Border
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(14),
                    Background = ThemeHelper.Brush(Color.FromArgb(38, avCol.R, avCol.G, avCol.B)),
                    Child = new TextBlock { Text = member.Avatar, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(avCol), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });

                var cm = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                cm.Children.Add(new TextBlock { Text = member.Name, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                cm.Children.Add(new TextBlock { Text = $"{member.LinesSoFar} 句台词", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                Grid.SetColumn(cm, 1);
                cg.Children.Add(cm);

                var badge = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background = ThemeHelper.Brush(p.Inset),
                    Padding = new Thickness(7, 2, 7, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = !string.IsNullOrEmpty(customRole?.Voice?.Voice) ? "专属音色" : "自动分配", FontSize = 10, Foreground = ThemeHelper.Brush(!string.IsNullOrEmpty(customRole?.Voice?.Voice) ? tint.Accent : p.T3) },
                };
                Grid.SetColumn(badge, 2);
                cg.Children.Add(badge);

                var cBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    CornerRadius = new CornerRadius(10),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6, 6, 6, 6),
                    UseSystemFocusVisuals = false,
                    Content = cg,
                };
                string roleName = member.Name;
                cBtn.Click += async (_, _) => await VoiceCloneAndDramaDialogs.ShowRoleDetailDialogAsync(
                    this,
                    RootGrid.XamlRoot,
                    _library,
                    _bookId,
                    roleName,
                    ShowVoicePickerSheetAsync,
                    () => { if (_activePanel == "cast") RenderSidePanel(); });
                castStack.Children.Add(cBtn);
            }
            body.Children.Add(castStack);
        }

        // 同名角色合并建议（1:1 DramaViews.swift SameSuggestionRow）
        try
        {
            var suggestions = _library.Core.DramaSameSuggestions(_bookId, _currentSection + 5);
            foreach (var sug in suggestions.Take(3))
            {
                var sg = sug;
                var sugStack = new StackPanel { Spacing = 6 };
                sugStack.Children.Add(new TextBlock { Text = $"「{sg.A}」和「{sg.B}」可能是同一个人", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap });
                var sugActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var mergeBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "合成一个", Height = 24, MinHeight = 24, FontSize = 11 };
                mergeBtn.Click += (_, _) => { try { _library.Core.DramaEditCast(_bookId, new CastEdit.Merge(sg.A, sg.B)); } catch { } RenderSidePanel(); };
                var diffBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "不是", Height = 24, MinHeight = 24, FontSize = 11 };
                diffBtn.Click += (_, _) => { try { _library.Core.DramaEditCast(_bookId, new CastEdit.NotSame(sg.A, sg.B)); } catch { } RenderSidePanel(); };
                sugActs.Children.Add(mergeBtn);
                sugActs.Children.Add(diffBtn);
                sugStack.Children.Add(sugActs);
                body.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(tint.Soft), Padding = new Thickness(10, 8, 10, 8), Child = sugStack });
            }
        }
        catch { }

        // 广播剧模式分组 + AI 台词识别与待确认审核（1:1 DramaViews.swift DramaStatusBlock）
        body.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 4, 0, 0) });
        var dramaCard = new Border { CornerRadius = new CornerRadius(12), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(12, 8, 12, 8) };
        var dramaStack = new StackPanel { Spacing = 8 };
        var dRow = new Grid();
        var dMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        dMeta.Children.Add(new TextBlock { Text = "广播剧模式", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        dMeta.Children.Add(new TextBlock { Text = "按人物台词自动切换不同声线朗读", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
        dRow.Children.Add(dMeta);
        var dSw = new ToggleSwitch { IsOn = _dramaModeOn, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 0, OnContent = "", OffContent = "" };
        dSw.Toggled += (_, _) =>
        {
            _dramaModeOn = dSw.IsOn;
            _audio.SetSectionContext(_currentSection, _dramaModeOn);
            try { _library.Core.DramaSetMode(_bookId, _dramaModeOn); } catch { }
        };
        dRow.Children.Add(dSw);
        dramaStack.Children.Add(dRow);

        try
        {
            var prog = _library.Core.DramaProgress(_bookId);
            bool running = VoiceCloneAndDramaDialogs.IsDramaWorkerRunning(_bookId);
            var recogRow = new Grid();
            recogRow.Children.Add(new TextBlock
            {
                Text = running ? $"正在识别角色… ({prog.SectionsDone}/{Math.Max(1u, prog.Sections)} 章)" : $"已分析 {prog.SectionsDone}/{Math.Max(1u, prog.Sections)} 章",
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T2),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var recogBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = running ? "暂停" : (_library.Companion.IsConfigured ? "识别全书角色" : "先配置书伴"),
                Height = 24,
                MinHeight = 24,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            recogBtn.Click += (_, _) =>
            {
                if (running) { VoiceCloneAndDramaDialogs.StopDramaWorker(_bookId); RenderSidePanel(); }
                else if (_library.Companion.IsConfigured)
                {
                    VoiceCloneAndDramaDialogs.StartDramaSpeakerWorker(_library, _bookId, _currentSection, () =>
                        DispatcherQueue.TryEnqueue(() => { if (_activePanel == "cast") RenderSidePanel(); }));
                    RenderSidePanel();
                }
                else App.CurrentApp.OpenSettings("companion");
            };
            recogRow.Children.Add(recogBtn);
            dramaStack.Children.Add(recogRow);

            if (prog.Unsure > 0)
            {
                var unsureBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = 26,
                    FontSize = 11.5,
                    Content = $"有 {prog.Unsure} 句台词待确认说话人 ›",
                };
                unsureBtn.Click += async (_, _) => await VoiceCloneAndDramaDialogs.ShowUnsureSpeakerReviewAsync(
                    RootGrid.XamlRoot, _library, _bookId, _currentSection, () => { if (_activePanel == "cast") RenderSidePanel(); });
                dramaStack.Children.Add(unsureBtn);
            }
        }
        catch { }

        dramaCard.Child = dramaStack;
        body.Children.Add(dramaCard);

        SideBodyStack.Children.Add(body);
    }

    // 1:1 MacVoicePickerPanel.swift：486px 暖纸声音选择器浮层
    private async Task ShowVoicePickerSheetAsync(string title, Action<string> onPick)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        ContentDialog? dlgRef = null;

        var searchBox = new TextBox { PlaceholderText = "搜声音、语种（晓晓、云希、English…）" };
        var cloneQuickBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "🎙 克隆一段自己的声音…", HorizontalAlignment = HorizontalAlignment.Right, Height = 26, FontSize = 11.5 };
        cloneQuickBtn.Click += async (_, _) =>
        {
            dlgRef?.Hide();
            var cloned = await VoiceCloneAndDramaDialogs.ShowCloneVoiceDialogAsync(this, RootGrid.XamlRoot, _library);
            if (cloned is not null) onPick("card:" + cloned.Id);
        };
        var listHost = new StackPanel { Spacing = 5 };
        var allVoices = AudioPlayer.GetAllVoiceOptions(_library);

        void RebuildVoices(string q)
        {
            listHost.Children.Clear();
            string filter = q.Trim();
            foreach (var v in allVoices.Where(x => string.IsNullOrEmpty(filter) || x.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                bool active = v.Id == _library.Prefs.VoiceId || v.Id == "sys:" + _library.Prefs.VoiceId;
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                meta.Children.Add(new TextBlock { Text = v.Label, FontSize = 12.5, FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T1) });
                meta.Children.Add(new TextBlock { Text = v.IsNeural ? "本地神经离线人声" : "Windows 系统语音", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                row.Children.Add(meta);

                var prevBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Height = 24, MinHeight = 24, FontSize = 11, Content = "▶ 试听" };
                string vid = v.Id;
                prevBtn.Click += (_, _) =>
                {
                    var sents = _library.GetSpokenSentences(_bookId, _currentSection);
                    string sample = sents.Count > 0 ? sents[Math.Clamp(_currentSentence, 0, sents.Count - 1)] : "你好，我是语阅的朗读声音。";
                    _ = AudioPlayer.PreviewVoiceAsync(_library, vid, _speechRate, sample);
                };
                Grid.SetColumn(prevBtn, 1);
                row.Children.Add(prevBtn);

                var pickBtn = new Button { Style = (Style)Application.Current.Resources[active ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"], Height = 24, MinHeight = 24, FontSize = 11, Content = active ? "当前" : "选用" };
                pickBtn.Click += (_, _) =>
                {
                    onPick(vid);
                    dlgRef?.Hide();
                };
                Grid.SetColumn(pickBtn, 2);
                row.Children.Add(pickBtn);

                listHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(active ? tint.Soft : p.Card),
                    BorderBrush = ThemeHelper.Brush(active ? tint.Edge : p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 8, 10, 8),
                    Child = row,
                });
            }
        }

        searchBox.TextChanged += (_, _) => RebuildVoices(searchBox.Text);
        RebuildVoices("");

        var openListeningSettings = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "下载更多离线自然人声 / 配置云端语音…" };
        openListeningSettings.Click += (_, _) => { dlgRef?.Hide(); App.CurrentApp.OpenSettings("listening"); };

        dlgRef = new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 10,
                Width = 440,
                Children =
                {
                    searchBox,
                    cloneQuickBtn,
                    new ScrollViewer { MaxHeight = 320, Content = listHost },
                    openListeningSettings,
                },
            },
            CloseButtonText = "关闭",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dlgRef.ShowAsync();
    }

    private void AddSideEmpty(YYPalette p, string title, string detail)
    {
        var sp = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 56, 0, 0) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        SideBodyStack.Children.Add(sp);
    }

    private static Button MakeSideItemButton(UIElement content) => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Colors.Transparent),
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(8),
        UseSystemFocusVisuals = false,
        Content = content,
    };

    // ---------- CompanionPanel (1:1 MacReaderCards.swift:1074-1368) ----------

    private void CompanionScope_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        _companionWholeBook = tag == "whole";
        UpdateCompanionScopeVisuals();
    }

    private void UpdateCompanionScopeVisuals()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        CompScopeReadBtn.Background = !_companionWholeBook ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent);
        CompScopeReadBtn.FontWeight = !_companionWholeBook ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        CompScopeWholeBtn.Background = _companionWholeBook ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent);
        CompScopeWholeBtn.FontWeight = _companionWholeBook ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        int page = EstimateBookPage(_currentSection, _currentSentence);
        CompanionScopeSubText.Text = _companionWholeBook
            ? "检索整本书，可能包含后续剧透"
            : $"截至第 {page} 页，不涉及后续情节";
    }

    private void CompanionClear_Click(object sender, RoutedEventArgs e) => ResetCompanionWelcome();
    private void CompanionSettings_Click(object sender, RoutedEventArgs e) => App.CurrentApp.OpenSettings("companion");

    private async void CompanionRecap_Click(object sender, RoutedEventArgs e)
    {
        if (!_library.Companion.IsConfigured)
        {
            App.CurrentApp.OpenSettings("companion");
            return;
        }
        await ShowRecapCardAsync(manual: true);
    }

    private async Task CheckInitialRecapAndSleepOfferAsync()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        // 0a. DoubtPill for TXT books (ReaderReturn.swift:80-113)
        if (_detail.Summary.Format == BookFormat.Txt)
        {
            try
            {
                var curChoice = _library.Core.TxtChoice(_bookId);
                var txtLayout = _library.Core.TxtLayout(_bookId, curChoice);
                if (!string.IsNullOrWhiteSpace(txtLayout.Doubt))
                {
                    SleepReturnHost.Content = ReaderRecapAndSleepHelper.BuildDoubtPill(
                        txtLayout.Doubt,
                        p,
                        onOpen: async () =>
                        {
                            SleepReturnHost.Visibility = Visibility.Collapsed;
                            if (await BookToolsDialogs.ShowTxtLayoutDialogAsync(
                                RootGrid.XamlRoot, _library, _bookId, _detail.Summary.Title,
                                sec => JumpTo(sec, 0, recordReturn: true)))
                            {
                                _detail = _library.GetBookDetail(_bookId);
                                LoadSection(Math.Min(_currentSection, (uint)Math.Max(0, _detail.Sections.Length - 1)));
                            }
                        },
                        onDismiss: () => SleepReturnHost.Visibility = Visibility.Collapsed);
                    SleepReturnHost.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        // 0b. ElsewhereCard when another synced device read further ahead (ReaderReturn.swift:6-74)
        try
        {
            var latest = _library.Core.LatestProgress(_bookId);
            var here = _library.GetProgress(_bookId);
            double hereFrac = here?.Fraction ?? (EstimateBookPercent(_currentSection, _currentSentence) / 100.0);
            if (latest is not null && !latest.ThisDevice && latest.Fraction > hereFrac + 0.015)
            {
                string? chapTitle = latest.Position.Section < _detail.Sections.Length
                    ? _detail.Sections[latest.Position.Section].Title
                    : null;
                string? excerpt = null;
                try
                {
                    var sents = _library.Core.Sentences(_bookId, latest.Position.Section);
                    if (latest.Position.Sentence < sents.Length)
                        excerpt = sents[latest.Position.Sentence];
                }
                catch { }

                RecapCardHost.Content = ReaderRecapAndSleepHelper.BuildElsewhereCard(
                    latest,
                    hereFrac,
                    chapTitle,
                    excerpt,
                    p,
                    tint,
                    onStay: () => RecapCardHost.Visibility = Visibility.Collapsed,
                    onJump: () =>
                    {
                        RecapCardHost.Visibility = Visibility.Collapsed;
                        JumpTo(latest.Position.Section, (int)latest.Position.Sentence, recordReturn: true);
                    });
                RecapCardHost.Visibility = Visibility.Visible;
                return;
            }
        }
        catch { }

        // 1. SleepReturnCard (ReaderFlag.swift:192-213)
        var sleepInfo = ReaderRecapAndSleepHelper.TryGetSleepReturn(_library, _bookId, _detail);
        if (sleepInfo is not null)
        {
            SleepReturnHost.Content = ReaderRecapAndSleepHelper.BuildSleepReturnCard(sleepInfo, p, tint, go =>
            {
                SleepReturnHost.Visibility = Visibility.Collapsed;
                ReaderRecapAndSleepHelper.ClearSleepReturn(_library, _bookId);
                if (go) JumpTo(sleepInfo.Section, sleepInfo.Sentence, recordReturn: true);
            });
            SleepReturnHost.Visibility = Visibility.Visible;
        }

        // 2. RecapCard (ReaderRecap.swift:14-48)
        await ShowRecapCardAsync(manual: false);
    }

    private async Task ShowRecapCardAsync(bool manual)
    {
        uint startSec = _currentSection;
        int startSent = _currentSentence;
        var recap = await ReaderRecapAndSleepHelper.TryLoadOrGenerateRecapAsync(
            _library, _bookId, _detail, _previousOpenedAtMs, startSec, startSent, manual);
        if (recap is null) return;
        if (!manual && (_currentSection != startSec || Math.Abs(_currentSentence - startSent) >= 30))
            return;

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        RecapCardHost.Content = ReaderRecapAndSleepHelper.BuildRecapCard(recap, p, tint, manual, () =>
        {
            RecapCardHost.Visibility = Visibility.Collapsed;
        });
        RecapCardHost.Visibility = Visibility.Visible;
    }

    private void OpenCompanionWithQuote(string quote)
    {
        _companionQuote = quote;
        CompanionQuoteText.Text = $"“{quote}”";
        CompanionQuoteBanner.Visibility = Visibility.Visible;
        OpenPanel("companion");
        CompanionInputBox.Focus(FocusState.Programmatic);
    }

    private void ClearCompanionQuote_Click(object sender, RoutedEventArgs e)
    {
        _companionQuote = null;
        CompanionQuoteBanner.Visibility = Visibility.Collapsed;
    }

    private void ResetCompanionWelcome()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        CompanionMessages.Children.Clear();

        var welcomeStack = new StackPanel { Spacing = 6 };
        welcomeStack.Children.Add(new TextBlock { Text = "读到哪儿问到哪儿", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        welcomeStack.Children.Add(new TextBlock
        {
            Text = "可以问人物关系、这句是什么意思、前面发生了什么。回答标着出处，不剧透你还没读到的地方。",
            FontSize = 12,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
        });
        if (!_library.Companion.IsConfigured)
        {
            var cfgBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "去设置 · 书伴", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            cfgBtn.Click += (_, _) => App.CurrentApp.OpenSettings("companion");
            welcomeStack.Children.Add(cfgBtn);
        }

        CompanionMessages.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Inset),
            Padding = new Thickness(14),
            Child = welcomeStack,
        });
    }

    private void CompanionQuickPrompt_Click(object sender, RoutedEventArgs e)
    {
        string? prompt = (sender as MenuFlyoutItem)?.Tag as string ?? (sender as Button)?.Tag as string;
        if (!string.IsNullOrEmpty(prompt))
        {
            CompanionInputBox.Text = prompt;
            CompanionSend_Click(sender, e);
        }
    }

    private void CompanionInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { CompanionSend_Click(sender, e); e.Handled = true; }
    }

    private async void CompanionSend_Click(object sender, RoutedEventArgs e)
    {
        var q = CompanionInputBox.Text.Trim();
        if (string.IsNullOrEmpty(q)) return;

        if (!_library.Companion.IsConfigured)
        {
            App.CurrentApp.OpenSettings("companion");
            return;
        }

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var quote = _companionQuote;
        CompanionInputBox.Text = "";
        CompanionInputBox.PlaceholderText = "继续问…";
        ClearCompanionQuote_Click(sender, e);

        CompanionMessages.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14, 14, 4, 14),
            Background = ThemeHelper.Brush(p.Inset),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 280,
            Child = new TextBlock { Text = q, FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap },
        });

        var replyTb = new TextBlock { Text = "正在翻阅原文…", FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        var modelMeta = new TextBlock
        {
            Text = _companionWholeBook ? $"{_library.Prefs.AiModel} · 用了全书" : _library.Prefs.AiModel,
            FontSize = 10,
            Foreground = ThemeHelper.Brush(p.T3),
        };
        CompanionMessages.Children.Add(new StackPanel { Spacing = 4, Padding = new Thickness(2, 4, 2, 4), Children = { replyTb, modelMeta } });
        CompanionSendBtn.IsEnabled = false;

        try
        {
            var answer = await _library.Companion.AskBookAsync(_bookId, _detail.Summary.Title, _currentSection, q, quote, _companionWholeBook);
            replyTb.Text = answer;
        }
        catch (Exception ex)
        {
            replyTb.Text = $"未能获得回答：{ex.Message}";
        }
        finally
        {
            CompanionSendBtn.IsEnabled = true;
            CompanionScroll.ChangeView(null, CompanionScroll.ScrollableHeight, null);
        }
    }

    // ---------- Audio, ListenCapsule & 448px ListenCard (1:1 MacReaderCards.swift:623-1067) ----------

    private void StartListeningAt(int sentenceIndex)
    {
        var sentences = _library.GetSpokenSentences(_bookId, _currentSection);
        if (sentences.Count == 0) return;

        SleepReturnHost.Visibility = Visibility.Collapsed;
        ReaderRecapAndSleepHelper.ClearSleepReturn(_library, _bookId);

        _sectionSentenceCount = sentences.Count;
        string chapter = _currentSection < _detail.Sections.Length ? _detail.Sections[_currentSection].Title ?? "" : "";
        _audio.SetMetadata(_detail.Summary.Title, chapter);
        _audio.SetSectionContext(_currentSection, _dramaModeOn);
        _audio.SetRate(_speechRate);
        _audio.Start(sentences, sentenceIndex);
        ListenCapsule.Visibility = Visibility.Visible;
        UpdateCapsuleText(sentenceIndex);
    }

    private void UpdateCapsuleText(int sentenceIndex)
    {
        int total = Math.Max(1, _sectionSentenceCount);
        int cur = Math.Clamp(sentenceIndex + 1, 1, total);
        double chapterProgress = Math.Clamp((double)cur / total, 0.0, 1.0);
        double ratio = Math.Clamp(chapterProgress, 0.02, 1.0);
        int chapterPct = (int)Math.Round(chapterProgress * 100.0);
        int remMin = Math.Max(1, (int)Math.Ceiling(Math.Max(0, total - cur) * 3.8 / 60.0 / Math.Max(0.5, _speechRate)));
        string unit = _detail.Summary.Format == BookFormat.Podcast ? "本集" : "本章";

        CapsuleSentenceText.Text = $"第 {cur} / {total} 句";
        CapsuleNoteText.Text = $"{unit} {chapterPct}% · 还剩 {remMin} 分钟";
        CapsuleProgDoneCol.Width = new GridLength(ratio, GridUnitType.Star);
        CapsuleProgRestCol.Width = new GridLength(Math.Max(0, 1.0 - ratio), GridUnitType.Star);
    }

    private void ToggleListen_Click(object sender, RoutedEventArgs e)
    {
        if (_audio.IsPlaying) _audio.Pause();
        else if (_audio.CurrentSentence >= 0 && ListenCapsule.Visibility == Visibility.Visible)
        {
            SleepReturnHost.Visibility = Visibility.Collapsed;
            ReaderRecapAndSleepHelper.ClearSleepReturn(_library, _bookId);
            _audio.Resume();
        }
        else StartListeningAt(_currentSentence);
    }

    private void SpeakPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_audio.IsPlaying || ListenCapsule.Visibility == Visibility.Visible) _audio.Previous();
        else _ = TurnPrevAsync();
    }

    private void SpeakNext_Click(object sender, RoutedEventArgs e)
    {
        if (_audio.IsPlaying || ListenCapsule.Visibility == Visibility.Visible) _audio.Next();
        else _ = TurnNextAsync();
    }

    private void ExitListenOnly_Click(object sender, RoutedEventArgs e)
    {
        _listenOnly = false;
        ListenOnlyCover.Visibility = Visibility.Collapsed;
    }

    private void ListenCardFlyout_Opened(object sender, object e) => RebuildListenCard();

    private void RebuildListenCard()
    {
        ListenCardHost.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        Border KeyCap(string k) => new()
        {
            CornerRadius = new CornerRadius(4),
            Background = ThemeHelper.Brush(p.Inset),
            Padding = new Thickness(5, 1.5, 5, 1.5),
            Child = new TextBlock { Text = k, FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T2) },
        };

        // 1. Header: 听书 + 快捷键键帽
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = "听书", FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        caps.Children.Add(KeyCap("空格"));
        caps.Children.Add(new TextBlock { Text = "暂停", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        caps.Children.Add(KeyCap("Ctrl+["));
        caps.Children.Add(KeyCap("Ctrl+]"));
        caps.Children.Add(new TextBlock { Text = "上一句 / 下一句", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(caps);
        ListenCardHost.Children.Add(head);

        // 2. Narrator Voice Card
        var curVoice = AudioPlayer.GetAllVoiceOptions(_library).FirstOrDefault(v => v.Id == _library.Prefs.VoiceId || v.Id == "sys:" + _library.Prefs.VoiceId);
        var voiceGrid = new Grid { ColumnSpacing = 10 };
        voiceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        voiceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        voiceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        voiceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        bars.Children.Add(new Border { Width = 2.5, Height = 8, CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Bottom });
        bars.Children.Add(new Border { Width = 2.5, Height = 14, CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Bottom });
        bars.Children.Add(new Border { Width = 2.5, Height = 10, CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Bottom });
        voiceGrid.Children.Add(new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Background = ThemeHelper.Brush(p.Card), Child = bars });

        var vMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var vNameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        vNameRow.Children.Add(new TextBlock { Text = curVoice?.Label ?? "系统默认语音", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxWidth = 176, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        if (curVoice?.IsNeural != true)
            vNameRow.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = ThemeHelper.Brush(p.Card), Padding = new Thickness(5, 1, 5, 1), Child = new TextBlock { Text = "系统自带", FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T3) } });
        vMeta.Children.Add(vNameRow);
        vMeta.Children.Add(new TextBlock { Text = $"正在朗读 · {(_isForeignBook ? "English" : "中文")} · 本地离线", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
        Grid.SetColumn(vMeta, 1);
        voiceGrid.Children.Add(vMeta);

        var auditionBtn = new Button
        {
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            FontSize = 11,
            UseSystemFocusVisuals = false,
            Content = "▶ 试听",
        };
        auditionBtn.Click += (_, _) =>
        {
            var sents = _library.GetSpokenSentences(_bookId, _currentSection);
            string sample = sents.Count > 0 ? sents[Math.Clamp(_currentSentence, 0, sents.Count - 1)] : _detail.Summary.Title;
            _ = AudioPlayer.PreviewVoiceAsync(_library, _library.Prefs.VoiceId, _speechRate, sample);
        };
        Grid.SetColumn(auditionBtn, 2);
        voiceGrid.Children.Add(auditionBtn);

        var switchVoiceBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 4, 2, 4),
            FontSize = 11.5,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = ThemeHelper.Brush(tint.Accent),
            UseSystemFocusVisuals = false,
            Content = "换声音 ›",
        };
        switchVoiceBtn.Click += async (_, _) =>
        {
            ListenCardFlyout.Hide();
            await ShowVoicePickerSheetAsync("声音", vid =>
            {
                _library.Prefs.VoiceId = vid;
                _library.SavePrefs();
                ReloadVoicePrefs();
            });
        };
        Grid.SetColumn(switchVoiceBtn, 3);
        voiceGrid.Children.Add(switchVoiceBtn);

        ListenCardHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Inset),
            Padding = new Thickness(12, 10, 10, 10),
            Child = voiceGrid,
        });

        // 3. Speed Section (0.8 .. 2.0×)
        int totalSents = Math.Max(1, _sectionSentenceCount);
        int curSent = Math.Clamp(_audio.CurrentSentence >= 0 ? _audio.CurrentSentence + 1 : _currentSentence + 1, 1, totalSents);
        int remMin = Math.Max(1, (int)Math.Ceiling(Math.Max(0, totalSents - curSent) * 3.8 / 60.0 / Math.Max(0.5, _speechRate)));

        var speedStack = new StackPanel { Spacing = 4 };
        var speedHead = new Grid();
        var spLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        spLeft.Children.Add(new TextBlock { Text = "速度", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        var speedValTb = new TextBlock { Text = $"{_speechRate:0.0}×", FontSize = 16.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
        spLeft.Children.Add(speedValTb);
        spLeft.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(6, 1.5, 6, 1.5), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "不变调", FontSize = 10, Foreground = ThemeHelper.Brush(p.T2) } });
        speedHead.Children.Add(spLeft);
        var speedRemTb = new TextBlock { Text = $"本章约剩 {remMin} 分钟", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        speedHead.Children.Add(speedRemTb);
        speedStack.Children.Add(speedHead);

        var speedSlider = new Slider { Minimum = 0.8, Maximum = 2.0, StepFrequency = 0.1, Value = _speechRate };
        speedSlider.ValueChanged += (_, ev) =>
        {
            _speechRate = Math.Round(ev.NewValue, 1);
            speedValTb.Text = $"{_speechRate:0.0}×";
            int rm = Math.Max(1, (int)Math.Ceiling(Math.Max(0, totalSents - curSent) * 3.8 / 60.0 / Math.Max(0.5, _speechRate)));
            speedRemTb.Text = $"本章约剩 {rm} 分钟";
            _audio.SetRate(_speechRate);
            _library.Prefs.SpeechRate = _speechRate;
            _library.SavePrefs();
            UpdateCapsuleText(Math.Max(0, _audio.CurrentSentence));
        };
        speedStack.Children.Add(speedSlider);

        var ticksGrid = new Grid { ColumnSpacing = 4 };
        double[] ticks = [0.8, 1.0, 1.2, 1.5, 2.0];
        for (int i = 0; i < ticks.Length; i++)
        {
            ticksGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            double tv = ticks[i];
            var tb = new Button
            {
                Content = $"{tv:0.0}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 22,
                MinHeight = 22,
                Padding = new Thickness(0),
                FontSize = 10.5,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = ThemeHelper.Brush(Math.Abs(_speechRate - tv) < 0.05 ? tint.Accent : p.T3),
                UseSystemFocusVisuals = false,
            };
            tb.Click += (_, _) => { speedSlider.Value = tv; RebuildListenCard(); };
            Grid.SetColumn(tb, i);
            ticksGrid.Children.Add(tb);
        }
        speedStack.Children.Add(ticksGrid);
        ListenCardHost.Children.Add(speedStack);

        // 4. Sleep Timer Section
        var sleepStack = new StackPanel { Spacing = 6 };
        var sleepHead = new Grid();
        sleepHead.Children.Add(new TextBlock { Text = "睡眠定时", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) });
        sleepHead.Children.Add(new TextBlock { Text = "停止时自动添加书签", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
        sleepStack.Children.Add(sleepHead);

        var sleepGrid = new Grid { ColumnSpacing = 2 };
        var sleepItems = new (int Mode, string Label)[]
        {
            (0, "关"),
            (-2, "本句读完"),
            (-1, "本章结束"),
            (15, "15 分钟"),
            (30, "30 分钟"),
            (60, "60 分钟"),
        };
        for (int i = 0; i < sleepItems.Length; i++)
        {
            sleepGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (m, lbl) = sleepItems[i];
            bool on = _sleepMode == m;
            var sb = new Button
            {
                Content = lbl,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                MinHeight = 32,
                Padding = new Thickness(2, 0, 2, 0),
                FontSize = 11,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                UseSystemFocusVisuals = false,
            };
            int nextM = m;
            sb.Click += (_, _) =>
            {
                _sleepMode = nextM;
                _sleepUntilUtc = nextM > 0 ? DateTime.UtcNow.AddMinutes(nextM) : null;
                _sleepArmedSpot = nextM != 0 ? (_currentSection, Math.Max(0, _currentSentence)) : null;
                UpdateCapsuleText(Math.Max(0, _audio.CurrentSentence));
                RebuildListenCard();
            };
            Grid.SetColumn(sb, i);
            sleepGrid.Children.Add(sb);
        }
        sleepStack.Children.Add(new Border { CornerRadius = new CornerRadius(12), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(3), Child = sleepGrid });
        ListenCardHost.Children.Add(sleepStack);

        // 5. Bottom 4 Feature Tiles (人物声音 / 单句循环 / 念完停一下 / 只听)
        var tilesGrid = new Grid { ColumnSpacing = 6 };
        for (int i = 0; i < 4; i++) tilesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Button MakeFeatureTile(int col, string icon, string title, string sub, bool active, Action onClick)
        {
            var sp = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            var r1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            r1.Children.Add(YYIcons.Make(icon, 11.5, ThemeHelper.Brush(active ? tint.Accent : p.T1)));
            r1.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T1) });
            sp.Children.Add(r1);
            sp.Children.Add(new TextBlock { Text = sub, FontSize = 10, Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T3) });

            var b = new Button
            {
                Height = 44,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(9, 0, 6, 0),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Background = ThemeHelper.Brush(active ? tint.Soft : p.Inset),
                BorderBrush = active ? ThemeHelper.Brush(tint.Edge) : new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = sp,
            };
            b.Click += (_, _) => onClick();
            Grid.SetColumn(b, col);
            return b;
        }

        tilesGrid.Children.Add(MakeFeatureTile(0, "people", "人物声音", _dramaModeOn ? "广播剧开" : "都跟旁白", _dramaModeOn, () =>
        {
            ListenCardFlyout.Hide();
            OpenPanel("cast");
        }));
        tilesGrid.Children.Add(MakeFeatureTile(1, "repeat-1", "单句循环", _loopSingleSentence ? "开" : "关", _loopSingleSentence, () =>
        {
            _loopSingleSentence = !_loopSingleSentence;
            RebuildListenCard();
        }));
        string pauseSub = _shadowPauseMode switch { 1 => "2 秒", 2 => "按句长", _ => "关" };
        tilesGrid.Children.Add(MakeFeatureTile(2, "pause-circle", "念完停一下", pauseSub, _shadowPauseMode > 0, () =>
        {
            _shadowPauseMode = (_shadowPauseMode + 1) % 3;
            RebuildListenCard();
        }));
        tilesGrid.Children.Add(MakeFeatureTile(3, "eye-slash", "只听", _listenOnly ? "正隐藏原文" : "隐藏原文", _listenOnly, () =>
        {
            _listenOnly = !_listenOnly;
            ListenOnlyCover.Visibility = _listenOnly ? Visibility.Visible : Visibility.Collapsed;
            RebuildListenCard();
        }));
        ListenCardHost.Children.Add(tilesGrid);

        // 6. 背景环境音（夜雨 / 壁炉 / 溪流 / 夏夜）
        var ambStack = new StackPanel { Spacing = 5 };
        ambStack.Children.Add(new TextBlock { Text = "背景环境音", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) });
        var ambGrid = new Grid { ColumnSpacing = 2 };
        (string Id, string Label)[] ambItems = [("", "关"), ("rain", "夜雨"), ("fire", "壁炉"), ("stream", "溪流"), ("night", "夏夜")];
        for (int i = 0; i < ambItems.Length; i++)
        {
            ambGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (aid, albl) = ambItems[i];
            bool on = (_library.Prefs.AmbientSoundId ?? "") == aid;
            var ab = new Button
            {
                Content = albl,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(0),
                FontSize = 11,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0),
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                UseSystemFocusVisuals = false,
            };
            string nextAid = aid;
            ab.Click += (_, _) =>
            {
                _library.Prefs.AmbientSoundId = nextAid;
                _library.SavePrefs();
                _audio.RefreshAmbientSound();
                RebuildListenCard();
            };
            Grid.SetColumn(ab, i);
            ambGrid.Children.Add(ab);
        }
        ambStack.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(3), Child = ambGrid });
        ListenCardHost.Children.Add(ambStack);

        // 7. 配对有声书微调与文字版整理错字入口（对齐 ReaderFlag.swift & TextPolish.swift）
        try
        {
            var pair = _library.Core.ListeningPairs().FirstOrDefault(lp => lp.Book == _bookId);
            bool isTextVer = _library.Core.AudiobookIsTextVersion(_bookId);
            if (pair is not null || isTextVer)
            {
                var extraRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (pair is not null)
                {
                    string recId = pair.Recording;
                    var alignBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "对齐检查与微调…", FontSize = 11 };
                    alignBtn.Click += (_, _) =>
                    {
                        ListenCardFlyout.Hide();
                        _audio.Pause();
                        AlignCheckAndPolishService.OpenAlignCheckWindow(_library, recId);
                    };
                    extraRow.Children.Add(alignBtn);
                }
                if (isTextVer)
                {
                    var polishBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "整理错字…", FontSize = 11 };
                    polishBtn.Click += async (_, _) =>
                    {
                        ListenCardFlyout.Hide();
                        await AlignCheckAndPolishService.ShowTextPolishDialogAsync(RootGrid.XamlRoot, _library, _bookId, () => LoadSection(_currentSection));
                    };
                    extraRow.Children.Add(polishBtn);
                }
                ListenCardHost.Children.Add(extraRow);
            }
        }
        catch { }
    }

    private void OnAudioSentenceStarted(int sentenceIndex, int durationMs, bool playing)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_sleepMode == -2 && sentenceIndex != _currentSentence)
            {
                var armed = _sleepArmedSpot ?? (_currentSection, Math.Max(0, _currentSentence));
                _sleepMode = 0;
                _sleepArmedSpot = null;
                _audio.Pause();
                ReaderRecapAndSleepHelper.RememberSleepStop(_library, _bookId, armed.Section, armed.Sentence);
                try { _library.AddBookmark(_bookId, _currentSection, (uint)Math.Max(0, _currentSentence)); } catch { }
                return;
            }
            _currentSentence = sentenceIndex;
            SaveCurrentProgress();
            UpdateHeaderStatus();
            UpdateCapsuleText(sentenceIndex);
            _ = _host.SetActiveAsync(sentenceIndex, durationMs, 0, playing);
        });
    }

    private void OnAudioPlayStateChanged(bool playing)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyTintAndChrome();
            _ = _host.SetPlayingAsync(playing);
        });
    }

    private void OnAudioSectionFinished()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_sleepMode == -1)
            {
                var armed = _sleepArmedSpot ?? (_currentSection, Math.Max(0, _currentSentence));
                _sleepMode = 0;
                _sleepArmedSpot = null;
                _audio.Stop();
                ReaderRecapAndSleepHelper.RememberSleepStop(_library, _bookId, armed.Section, armed.Sentence);
                try { _library.AddBookmark(_bookId, _currentSection, (uint)Math.Max(0, _currentSentence)); } catch { }
                UpdateCapsuleText(Math.Max(0, _currentSentence));
                _ = _host.SetActiveAsync(null);
                return;
            }

            if (_library.NextSection(_bookId, _currentSection, speech: true) is uint s)
            {
                _pendingSentence = 0;
                _pendingAutoSpeak = true;
                LoadSection(s);
            }
            else
            {
                _audio.Stop();
                ListenCapsule.Visibility = Visibility.Collapsed;
                _ = _host.SetActiveAsync(null);
            }
        });
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _timeTracker.Stop();
        SaveCurrentProgress();
        _audio.Dispose();
        _host.Dispose();
    }

    private static string PaperCssId(Paper paper) => paper switch
    {
        Paper.Dark => "dark",
        Paper.Kami => "kami",
        Paper.KamiNight => "kaminight",
        Paper.Sage => "sage",
        Paper.Eink => "eink",
        _ => "plain",
    };

    private static Paper ParsePaper(string id) => id switch
    {
        "dark" => Paper.Dark,
        "kami" => Paper.Kami,
        "kaminight" => Paper.KamiNight,
        "sage" => Paper.Sage,
        "eink" => Paper.Eink,
        _ => Paper.Plain,
    };

    private static string? FontCssValue(Font? font, string? fontFace = null)
    {
        if (!string.IsNullOrWhiteSpace(fontFace))
        {
            return fontFace switch
            {
                "notoserif" => "\"Noto Serif SC\", var(--yy-serif)",
                "notosans" => "\"Noto Sans SC\", var(--yy-sans)",
                _ when fontFace.StartsWith("file:", StringComparison.Ordinal) => $"\"{Path.GetFileNameWithoutExtension(fontFace[5..])}\", var(--yy-serif)",
                _ => fontFace,
            };
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

    private sealed record TocEntry(string Title, uint Section, string? Anchor, ushort Depth, bool IsBoilerplate);
}
