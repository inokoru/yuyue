using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Reader;
using Yuyue.Speech;
using Yuyue.UI.Shell;

namespace Yuyue.UI.Reader;

/// <summary>
/// Brand-new native Windows 11 reading pane (<see cref="UserControl"/>).
/// Composable standalone OR side-by-side inside <see cref="ReaderTabView"/> for Dual-Book Split View.
/// Features paper-matched 40px topbar &amp; 28px watermark statusbar, docked resizable Left (TOC/Bookmarks/Search)
/// &amp; Right (Notes/AI Companion/Cast) panes, in-flow paper-matched Live TTS bar, Smart Space, Ctrl+Wheel zoom,
/// Alt+←/→ &amp; Mouse XButton navigation history, and 100% real ReaderPagerHost + AudioPlayer + CompanionClient engines.
/// </summary>
public sealed partial class ReaderPaneView : UserControl, IDisposable
{
    private MainWindow _ownerWindow;
    private ITabContent? _ownerTab;
    private readonly LibraryService _library;
    private readonly string _bookId;
    private readonly bool _isSecondarySplitPane;

    private BookDetail _detail = null!;
    private ReaderSettings _settings = null!;
    private bool _ownSettings;
    private ReaderPagerHost _pager = null!;
    private AudioPlayer _audio = null!;

    private uint _currentSection;
    private int _currentPage;
    private int _currentSentence;
    private double _scrollProgress;
    private SectionLayout? _layout;
    private IReadOnlyList<string> _currentSentences = [];
    private bool _pendingEndOfSection;
    private int? _pendingJumpSentence;
    private string? _pendingJumpAnchor;
    private string? _pendingSearchHighlight;
    private bool _autoListenOnReady;
    private bool _isFullScreen;
    private bool _disposed;

    // Docked pane state
    private bool _leftPaneOpen;
    private string _leftPaneTab = "toc"; // toc | bookmarks | search
    private double _leftPaneWidth = 260;

    private bool _rightPaneOpen;
    private string _rightPaneTab = "notes"; // notes | companion | cast
    private double _rightPaneWidth = 328;

    // Navigation history stack (Alt+←/→ & mouse XButton1/XButton2) + macOS ReturnPill
    private readonly Stack<(uint Section, int Sentence)> _backStack = new();
    private readonly Stack<(uint Section, int Sentence)> _forwardStack = new();
    private (uint Section, int Sentence)? _returnFrom;
    private int _returnTurnsRemaining;

    // Foreign translation, dialogue bubbles, drama & context cards state
    private bool _translationsActive;
    private bool _bubblesOn;
    private bool _dramaModeOn;
    private bool _isTextVersion;
    private BubbleLine[] _sectionBubbles = [];
    private long? _previousOpenedAtMs;
    private bool _finishedPrompted;
    private (uint Section, int Sentence, int DaysAway, long LastReadMs)? _pendingRecapOffer;
    private RecapDto? _readyRecap;
    private CancellationTokenSource? _translateCts;
    private CancellationTokenSource? _sentenceTransCts;
    private bool _dictFullExpanded;
    private bool _dictWebReady;
    private SyncMap? _pairedMap;
    private string? _pairedAudioBookId;

    // Reading time tracking
    private readonly DispatcherTimer _timeTracker;
    private DateTime _lastTimeTickUtc = DateTime.UtcNow;
    private uint _accumReadSec;
    private uint _accumListenSec;
    private DispatcherTimer? _alertTimer;
    private Action? _alertAction;

    // Active paper chrome broadcast to MainWindow
    public PaperChromeTheme? CurrentPaperChrome { get; private set; }
    public string BookId => _bookId;
    public string BookTitle => _detail?.Summary.Title ?? "";
    public bool IsAudioPlaying => _audio?.IsPlaying == true;

    public event Action? ChromeOrMetaChanged;
    public event Action? PaneActivated;
    public event Action<string>? RequestOpenSplitBook;
    public event Action? RequestCloseSplitPane;
    public event Action? RequestDetachWindow;

    public void SetOwnerWindow(MainWindow owner)
    {
        _ownerWindow = owner;
    }

    public void SetOwnerTab(ITabContent ownerTab)
    {
        _ownerTab = ownerTab;
    }

    public ReaderPaneView(
        MainWindow ownerWindow,
        LibraryService library,
        string bookId,
        bool isSecondarySplitPane = false,
        bool autoListen = false,
        uint? initialSection = null,
        int? initialSentence = null,
        string? initialAskQuestion = null,
        string? initialSearchQuery = null)
    {
        _ownerWindow = ownerWindow;
        _library = library;
        _bookId = bookId;
        _isSecondarySplitPane = isSecondarySplitPane;
        _autoListenOnReady = autoListen;
        _pendingJumpSentence = initialSentence;
        _pendingSearchHighlight = initialSearchQuery;

        InitializeComponent();

        L10n.Skip(TopBookTitleText);
        L10n.Skip(TopChapterText);
        L10n.Skip(SelectionNotePreviewText);
        L10n.Skip(WordHeadwordText);
        L10n.Skip(WordPhoneticText);
        L10n.Skip(WordMeaningText);
        L10n.Skip(WordSourceText);
        L10n.Skip(SentenceOrigText);
        L10n.Skip(SentenceTransText);
        L10n.Skip(InfoPopupBody);
        L10n.Skip(ListenOnlyTitle);
        L10n.Skip(TtsLiveSentenceText);

        _detail = _library.GetBookDetail(bookId);
        _previousOpenedAtMs = _detail.Summary.OpenedAt;
        try { _bubblesOn = _library.Core.BookBubbles(_bookId); } catch { _bubblesOn = false; }
        try { _dramaModeOn = _library.Core.DramaEnabled(_bookId); } catch { _dramaModeOn = false; }
        try { _isTextVersion = _library.Core.AudiobookIsTextVersion(_bookId); } catch { _isTextVersion = false; }
        _translationsActive = _library.Prefs.BookTranslationsActive.TryGetValue(_bookId, out var savedTr) && savedTr;
        TranslateBtn.Visibility = IsForeignBook() ? Visibility.Visible : Visibility.Collapsed;
        var perBook = _library.GetBookReaderSettings(bookId);
        _ownSettings = perBook is not null;
        _settings = perBook ?? _library.GetReaderSettings();

        _leftPaneWidth = Math.Clamp(_library.Prefs.ReaderLeftPaneWidth, 210, 400);
        _rightPaneWidth = Math.Clamp(_library.Prefs.ReaderPanelWidth, 260, 480);

        if (_isSecondarySplitPane)
        {
            SplitViewBtn.Visibility = Visibility.Collapsed;
            CloseSplitPaneBtn.Visibility = Visibility.Visible;
        }
        else
        {
            _leftPaneOpen = _library.Prefs.ReaderLeftPaneOpen;
            _leftPaneTab = string.IsNullOrWhiteSpace(_library.Prefs.ReaderLeftPaneTab) ? "toc" : _library.Prefs.ReaderLeftPaneTab;
        }

        // Determine starting position
        if (initialSection is uint sec)
        {
            _currentSection = sec;
        }
        else if (_library.GetProgress(bookId) is { } prog)
        {
            _currentSection = prog.Position.Section;
            _pendingJumpSentence ??= (int)prog.Position.Sentence;
        }
        else
        {
            _currentSection = _detail.Start;
        }

        // Initialize AudioPlayer & PagerHost
        InitAudioPlayer();
        _pager = new ReaderPagerHost(WebViewSlotHost, _library, _bookId, BuildGeometry);
        _pager.MessageReceived += OnReaderMessage;
        _pager.DecorateSectionBeforeReadyAsync = DecorateSectionHostAsync;

        // Splitter wiring
        LeftSplitter.DragDelta += dx =>
        {
            _leftPaneWidth = Math.Clamp(_leftPaneWidth + dx, 200, 420);
            LeftPaneCol.Width = new GridLength(_leftPaneWidth);
        };
        LeftSplitter.DragCompleted += () =>
        {
            _library.Prefs.ReaderLeftPaneWidth = _leftPaneWidth;
            _library.SavePrefs();
            PushGeometryToReader();
        };
        RightSplitter.DragDelta += dx =>
        {
            _rightPaneWidth = Math.Clamp(_rightPaneWidth - dx, 250, 500);
            RightPaneCol.Width = new GridLength(_rightPaneWidth);
        };
        RightSplitter.DragCompleted += () =>
        {
            _library.Prefs.ReaderPanelWidth = _rightPaneWidth;
            _library.SavePrefs();
            PushGeometryToReader();
        };

        BuildMoreMenuFlyout();
        BuildSleepTimerFlyout();

        _timeTracker = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(30) };
        _timeTracker.Tick += (_, _) => OnTimeTrackerTick();
        _timeTracker.Start();

        ApplyPaperAndChrome();
        ApplyDockedPanesLayout();
        UpdateHeaderAndStatus();

        _ = InitializeReaderAsync(initialAskQuestion);
    }

    private async Task InitializeReaderAsync(string? initialAskQuestion)
    {
        try
        {
            await _pager.InitializeAsync();
            if (_disposed) return;
            LoadSection(_currentSection);
            CheckInitialContextCards();
            if (!string.IsNullOrWhiteSpace(initialAskQuestion))
            {
                OpenRightPane("companion");
                _ = SendCompanionQuestionAsync(initialAskQuestion);
            }
        }
        catch (Exception ex)
        {
            ShowReaderAlert($"阅读引擎初始化失败：{ex.Message}");
        }
    }

    public void OnAppearanceChanged()
    {
        ApplyPaperAndChrome();
        PushSettingsToReader(save: false);
        if (_leftPaneOpen) RenderLeftPane();
        if (_rightPaneOpen) RenderRightPane();
    }

    public void ReloadReaderSettingsFromCore()
    {
        Font? prevFont = _settings.Font;
        string? prevFace = _settings.FontFace;
        var perBook = _library.GetBookReaderSettings(_bookId);
        _ownSettings = perBook is not null;
        _settings = perBook ?? _library.GetReaderSettings();

        bool faceChanged = !string.Equals(prevFace, _settings.FontFace, StringComparison.OrdinalIgnoreCase)
            || (_settings.Font == Font.WenKai && prevFont != Font.WenKai);
        bool usesManagedFontFace = _settings.Font == Font.WenKai
            || (!string.IsNullOrWhiteSpace(_settings.FontFace)
                && !_settings.FontFace.StartsWith("local:", StringComparison.OrdinalIgnoreCase));

        ApplyPaperAndChrome();
        if (faceChanged && usesManagedFontFace && _pager.IsInitialized)
        {
            _pendingJumpSentence = CurrentReadingSentence();
            _pendingEndOfSection = false;
            LoadSection(_currentSection, forceReload: true);
            return;
        }
        PushSettingsToReader(save: false);
    }

    public void ReloadVoicePrefs()
    {
        string vid = _library.Prefs.GetVoiceForLanguage(_detail.Summary.Language);
        _audio.SetVoice(vid);
        _audio.SetRate(_library.Prefs.SpeechRate);
        UpdateTtsBarVisuals();
    }

    public void ReloadForeignPrefs()
    {
        _ = RefreshSectionOverlaysAsync();
    }

    public void SetFullScreenChrome(bool isFullScreen)
    {
        _isFullScreen = isFullScreen;
        TopBarRow.Height = isFullScreen ? new GridLength(0) : new GridLength(40);
        TopBar.Visibility = isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        PushGeometryToReader();
    }

    /// <summary>
    /// Computes the active reading paper palette and tints the TopBar, docked panes, TTS Bar, StatusBar,
    /// AND notifies <see cref="MainWindow"/> so the 38px TabView strip merges seamlessly with the paper!
    /// </summary>
    public void ApplyPaperAndChrome()
    {
        var resolved = PaperAppearance.Resolve(_library, _settings);
        YYPaper paper = YYPapers.FromId(PaperAppearance.PaperCssId(resolved.Preset));
        Color bg = paper.Bg;
        Color ink1 = paper.Ink;
        Color ink2 = paper.Ink2;
        Color line = paper.Line;
        bool pDark = paper.IsDark;

        if (resolved.Custom is { } cp)
        {
            try
            {
                var cols = YuyueCoreMethods.PaperColors(cp);
                bg = ParseHexColor(cols.Bg, bg);
                ink1 = ParseHexColor(cols.Ink, ink1);
                ink2 = ParseHexColor(cols.Ink2, ink2);
                line = ParseHexColor(cols.Line, line);
                pDark = cols.Dark;
            }
            catch { }
        }

        // Slightly deeper/warmer tab strip derived from paperBg so tabs look crafted from the same paper stock
        Color tabStripBg = ShiftColor(bg, pDark ? -5 : -7);
        Color sidePaneBg = ShiftColor(bg, pDark ? 4 : -3);

        CurrentPaperChrome = new PaperChromeTheme(bg, tabStripBg, ink1, ink2, line, pDark);

        RootGrid.RequestedTheme = pDark ? ElementTheme.Dark : ElementTheme.Light;
        ThemeHelper.ApplyControlThemeResources(RootGrid.Resources, pDark, _library.Prefs.Tint);
        RootGrid.Background = ThemeHelper.Brush(bg);
        TopBar.Background = ThemeHelper.Brush(bg);
        TopBar.BorderBrush = ThemeHelper.Brush(line);
        StatusBarHost.Background = ThemeHelper.Brush(bg);
        StatusBarHost.BorderBrush = ThemeHelper.Brush(line);
        TtsBarHost.Background = ThemeHelper.Brush(sidePaneBg);
        TtsBarHost.BorderBrush = ThemeHelper.Brush(line);
        LeftPaneHost.Background = ThemeHelper.Brush(sidePaneBg);
        LeftPaneHost.BorderBrush = ThemeHelper.Brush(line);
        RightPaneHost.Background = ThemeHelper.Brush(sidePaneBg);
        RightPaneHost.BorderBrush = ThemeHelper.Brush(line);
        ListenOnlyCover.Background = ThemeHelper.Brush(bg);

        var fg1 = ThemeHelper.Brush(ink1);
        var fg2 = ThemeHelper.Brush(ink2);
        var accent = ThemeHelper.Brush(ThemeHelper.TintColors(_library.Prefs.Tint, pDark).Accent);

        TopBookTitleText.Foreground = fg1;
        TopChapterSep.Foreground = fg2;
        TopChapterText.Foreground = fg2;
        TopBarDivider.Background = fg2;
        TopLeftSep.Background = fg2;
        StatusSep1.Background = fg2;
        StatusSep2.Background = fg2;
        StatusSep3.Background = fg2;
        StatusProgressText.Foreground = fg2;
        StatusFontSizeText.Foreground = fg2;
        StatusModeBtn.Foreground = fg2;
        StatusColBtn.Foreground = fg2;
        StatusPaperBtn.Foreground = fg2;
        TtsLiveSentenceText.Foreground = fg1;
        ListenOnlyTitle.Foreground = fg1;

        LeftPaneBtn.Content = YYIcons.Make("panel-left", 14.5, _leftPaneOpen ? accent : fg2);
        HistBackBtn.Content = YYIcons.Make("arrow-left", 13.5, fg2);
        HistFwdBtn.Content = YYIcons.Make("arrow-right", 13.5, fg2);
        IconRecapPill.Content = YYIcons.Make("clock-history", 12.5, accent);
        RecapTopPillLabel.Foreground = accent;
        TranslateBtnLabel.Foreground = _translationsActive ? accent : fg1;
        TranslateBtn.Background = _translationsActive
            ? ThemeHelper.Brush(ThemeHelper.TintColors(_library.Prefs.Tint, pDark).Soft)
            : new SolidColorBrush(Colors.Transparent);
        SearchBtn.Content = YYIcons.Make("search", 14, fg2);
        TypographyBtn.Foreground = fg1;
        ListenBtn.Content = YYIcons.Make("headphones", 14.5, IsAudioPlaying ? accent : fg2);
        SplitViewBtn.Content = YYIcons.Make("split-view", 14.5, fg2);
        MoreBtn.Content = YYIcons.Make("more", 14.5, fg2);
        CloseSplitPaneBtn.Content = YYIcons.Make("xmark", 12, fg2);

        IconTopNotes.Content = YYIcons.Make("square.and.pencil", 13.5, (_rightPaneOpen && _rightPaneTab == "notes") ? accent : fg2);
        LabelTopNotes.Foreground = (_rightPaneOpen && _rightPaneTab == "notes") ? accent : fg2;
        IconTopCompanion.Content = YYIcons.Make("sparkle", 13.5, (_rightPaneOpen && _rightPaneTab == "companion") ? accent : fg2);
        LabelTopCompanion.Foreground = (_rightPaneOpen && _rightPaneTab == "companion") ? accent : fg2;
        IconTopCast.Content = YYIcons.Make("cast", 13.5, (_rightPaneOpen && _rightPaneTab == "cast") ? accent : fg2);
        LabelTopCast.Foreground = (_rightPaneOpen && _rightPaneTab == "cast") ? accent : fg2;

        LeftPaneCloseBtn.Content = YYIcons.Make("xmark", 11.5, fg2);
        RightPaneCloseBtn.Content = YYIcons.Make("xmark", 11.5, fg2);
        DeleteMarkBtn.Content = YYIcons.Make("trash", 13, ThemeHelper.Brush(ThemeHelper.Palette(pDark).Danger));
        WordSpeakBtn.Content = YYIcons.Make("speaker", 13, fg2);
        ListenOnlyIcon.Content = YYIcons.Make("headphones", 34, accent);

        TtsPrevBtn.Content = YYIcons.Make("chevron-left", 14, fg1);
        TtsNextBtn.Content = YYIcons.Make("chevron-right", 14, fg1);
        TtsSleepBtn.Content = YYIcons.Make("moon", 13.5, _audio.SleepMode > 0 ? accent : fg2);
        TtsPureBtn.Content = YYIcons.Make("eye-slash", 13.5, ListenOnlyCover.Visibility == Visibility.Visible ? accent : fg2);
        TtsPipBtn.Content = YYIcons.Make("pip", 13.5, fg2);
        TtsStopBtn.Content = YYIcons.Make("xmark", 12, fg2);
        TtsVoiceChip.Background = ThemeHelper.Brush(ThemeHelper.TintColors(_library.Prefs.Tint, pDark).Soft);
        TtsVoiceLabel.Foreground = accent;

        StatusPaperBtn.Content = L10n.T(resolved.Custom?.Name ?? paper.Name);
        StatusFontSizeText.Text = $"{Math.Round(_settings.FontSize)}px";
        StatusModeBtn.Content = L10n.T(_settings.Mode == LayoutMode.Scrolled ? "滚动" : "分页");
        StatusColBtn.Content = L10n.T(EffectiveColumnCount() == 2 ? "双栏" : "单栏");

        UpdateTtsBarVisuals();
        L10n.ApplyTree(RootGrid);
        ChromeOrMetaChanged?.Invoke();
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 740;
        var vis = compact ? Visibility.Collapsed : Visibility.Visible;
        LabelTopNotes.Visibility = vis;
        LabelTopCompanion.Visibility = vis;
        LabelTopCast.Visibility = vis;
        NotesPaneBtn.Padding = compact ? new Thickness(6, 0, 6, 0) : new Thickness(8, 0, 8, 0);
        CompanionPaneBtn.Padding = compact ? new Thickness(6, 0, 6, 0) : new Thickness(8, 0, 8, 0);
        CastPaneBtn.Padding = compact ? new Thickness(6, 0, 6, 0) : new Thickness(8, 0, 8, 0);
        TopBookTitleText.MaxWidth = compact ? 150 : 210;
    }

    private byte EffectiveColumnCount()
    {
        if (_settings.Mode == LayoutMode.Scrolled) return 1;
        if (!_library.Prefs.TwoColumns) return 1;
        double stageW = BookStage.ActualWidth > 0 ? BookStage.ActualWidth : 960;
        // When both docked panes or Split View squeeze center stage below 820px, cleanly adapt to airy single-column!
        return stageW >= 820 ? (byte)2 : (byte)1;
    }

    private void ApplyDockedPanesLayout()
    {
        LeftPaneHost.Visibility = _leftPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitter.Visibility = _leftPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        LeftPaneCol.Width = _leftPaneOpen ? new GridLength(_leftPaneWidth) : new GridLength(0);
        LeftSplitterCol.Width = _leftPaneOpen ? new GridLength(6) : new GridLength(0);

        RightPaneHost.Visibility = _rightPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        RightSplitter.Visibility = _rightPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        RightPaneCol.Width = _rightPaneOpen ? new GridLength(_rightPaneWidth) : new GridLength(0);
        RightSplitterCol.Width = _rightPaneOpen ? new GridLength(6) : new GridLength(0);

        if (_leftPaneOpen) RenderLeftPane();
        if (_rightPaneOpen) RenderRightPane();
        ApplyPaperAndChrome();
        PushGeometryToReader();
    }

    public void OpenLeftPane(string tab)
    {
        _leftPaneTab = tab;
        _leftPaneOpen = true;
        if (!_isSecondarySplitPane)
        {
            _library.Prefs.ReaderLeftPaneOpen = true;
            _library.Prefs.ReaderLeftPaneTab = tab;
            _library.SavePrefs();
        }
        ApplyDockedPanesLayout();
    }

    public void OpenRightPane(string tab)
    {
        _rightPaneTab = tab;
        _rightPaneOpen = true;
        ApplyDockedPanesLayout();
    }

    public void ConfigureDebugView(string? panel, bool showListenBar)
    {
        if (!string.IsNullOrWhiteSpace(panel))
        {
            if (panel is "toc" or "bookmarks" or "search") OpenLeftPane(panel);
            else if (panel is "notes" or "companion" or "cast") OpenRightPane(panel);
        }
        if (showListenBar)
        {
            TtsBarHost.Visibility = Visibility.Visible;
            UpdateTtsBarVisuals();
        }
    }

    public void JumpTo(uint section, int sentence, bool recordHistory = true)
    {
        if (recordHistory)
        {
            PushHistoryPoint();
            RecordReturnOrigin();
        }
        DismissOverlays();
        int clampedSent = Math.Max(0, sentence);
        if (section == _currentSection && _pager.IsInitialized)
        {
            _currentSentence = clampedSent;
            _ = _pager.Current.ScrollToSentenceAsync(clampedSent, animated: false, align: "top");
            return;
        }
        _pendingJumpSentence = clampedSent;
        _pendingEndOfSection = false;
        LoadSection(section);
    }

    private void RecordReturnOrigin()
    {
        _returnTurnsRemaining = 3;
        if (_returnFrom is null)
        {
            int curSent = CurrentReadingSentence();
            _returnFrom = (_currentSection, curSent);
            int estPage = EstimateBookPage(_currentSection, curSent);
            ReturnPillText.Text = estPage > 0 ? L10n.T($"↩ 回到第 {estPage} 页") : L10n.T("↩ 回到刚才位置");
        }
        ReturnPillBtn.Visibility = Visibility.Visible;
    }

    private void ReturnPill_Click(object sender, RoutedEventArgs e)
    {
        ReturnPillBtn.Visibility = Visibility.Collapsed;
        if (_returnFrom is var (sec, sent))
        {
            _returnFrom = null;
            _returnTurnsRemaining = 0;
            JumpTo(sec, sent, recordHistory: false);
        }
    }

    private void TickReturnPillTurn()
    {
        if (_returnFrom is null) return;
        _returnTurnsRemaining--;
        if (_returnTurnsRemaining <= 0)
        {
            _returnFrom = null;
            ReturnPillBtn.Visibility = Visibility.Collapsed;
        }
    }

    private double CalculateBookFraction(uint section, int sentence)
    {
        double totalUnits = 0;
        for (int i = 0; i < _detail.Sections.Length; i++)
        {
            var s = _detail.Sections[i];
            if (s.Linear && s.Kind == SectionKind.Text)
                totalUnits += s.Units;
        }
        if (totalUnits <= 0)
            return _detail.Sections.Length > 0 ? Math.Clamp((double)section / _detail.Sections.Length, 0, 1) : 0;

        double before = 0;
        for (int i = 0; i < Math.Min((int)section, _detail.Sections.Length); i++)
        {
            var s = _detail.Sections[i];
            if (s.Linear && s.Kind == SectionKind.Text)
                before += s.Units;
        }
        if (section < _detail.Sections.Length)
        {
            var cur = _detail.Sections[section];
            if (cur.Linear && cur.Kind == SectionKind.Text && cur.Sentences > 0)
                before += cur.Units * Math.Clamp((double)sentence / cur.Sentences, 0, 1);
        }
        return Math.Clamp(before / totalUnits, 0, 1);
    }

    private int EstimateBookPage(uint section, int sentence)
    {
        double totalUnits = 0;
        double before = 0;
        for (int i = 0; i < _detail.Sections.Length; i++)
        {
            var s = _detail.Sections[i];
            if (!s.Linear || s.Kind != SectionKind.Text) continue;
            totalUnits += s.Units;
            if (i < section) before += s.Units;
            else if (i == section && s.Sentences > 0)
                before += s.Units * Math.Clamp((double)sentence / s.Sentences, 0, 1);
        }
        if (totalUnits <= 0) return (int)section + 1;
        return Math.Max(1, (int)Math.Ceiling(before / 450.0));
    }

    public void AskCompanion(string question)
    {
        OpenRightPane("companion");
        _ = SendCompanionQuestionAsync(question);
    }

    public void HighlightSearch(string query)
    {
        _pendingSearchHighlight = query;
        OpenLeftPane("search");
        RunInBookSearch(query);
    }

    public bool HandleWindowKeyDown(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        if (ctrl && !alt)
        {
            if (key == VirtualKey.F)
            {
                OpenLeftPane("search");
                FocusLeftSearchBox();
                return true;
            }
            if (key == VirtualKey.D)
            {
                ToggleBookmarkAtCurrentPosition();
                return true;
            }
            if (key == VirtualKey.G)
            {
                StatusProgress_Click(StatusProgressBtn, new RoutedEventArgs());
                return true;
            }
            if (key == (VirtualKey)187 || key == VirtualKey.Add) // Ctrl+= / Ctrl++
            {
                AdjustFontSize(+1);
                return true;
            }
            if (key == (VirtualKey)189 || key == VirtualKey.Subtract) // Ctrl+-
            {
                AdjustFontSize(-1);
                return true;
            }
            if (key == VirtualKey.Number0 || key == VirtualKey.NumberPad0) // Ctrl+0 reset 18px
            {
                SetFontSize(18);
                return true;
            }
        }

        if (alt && !ctrl)
        {
            if (key == VirtualKey.Left) return NavigateHistory(isBack: true);
            if (key == VirtualKey.Right) return NavigateHistory(isBack: false);
        }

        if (!ctrl && !alt)
        {
            if (_activeSelection is not null || _editingMark is not null)
            {
                if (key == VirtualKey.Number1 || key == VirtualKey.NumberPad1) { ApplySelectionStyle(MarkStyle.Yellow); return true; }
                if (key == VirtualKey.Number2 || key == VirtualKey.NumberPad2) { ApplySelectionStyle(MarkStyle.Blue); return true; }
                if (key == VirtualKey.Number3 || key == VirtualKey.NumberPad3) { ApplySelectionStyle(MarkStyle.Pink); return true; }
                if (key == VirtualKey.Number4 || key == VirtualKey.NumberPad4) { ApplySelectionStyle(MarkStyle.Wave); return true; }
            }
            if (key == VirtualKey.F3)
            {
                StepSearchHit(forward: !shift);
                return true;
            }
            if (key == VirtualKey.Right || key == VirtualKey.PageDown)
            {
                _ = NextPageOrSectionAsync();
                return true;
            }
            if (key == VirtualKey.Left || key == VirtualKey.PageUp)
            {
                _ = PrevPageOrSectionAsync();
                return true;
            }
            if (key == VirtualKey.Down)
            {
                if (_settings.Mode == LayoutMode.Scrolled) _ = ScrollLinesAsync(down: true);
                else _ = NextPageOrSectionAsync();
                return true;
            }
            if (key == VirtualKey.Up)
            {
                if (_settings.Mode == LayoutMode.Scrolled) _ = ScrollLinesAsync(down: false);
                else _ = PrevPageOrSectionAsync();
                return true;
            }
            if (key == VirtualKey.Space)
            {
                HandleSpaceKey();
                return true;
            }
            if (key == VirtualKey.Escape)
            {
                if (HasOpenOverlay())
                {
                    DismissOverlays();
                    return true;
                }
                if (_isFullScreen)
                {
                    _ownerWindow.SetFullScreen(false);
                    return true;
                }
            }
        }
        return false;
    }

    public bool HandleMouseBackForward(bool isBack) => NavigateHistory(isBack);

    /// <summary>
    /// Locked-in Windows 11 Smart Space decision:
    /// Default ("smart"): Space pages down during quiet reading, and toggles play/pause while TTS/audio is active.
    /// Optional ("listen"): Always toggles read-aloud (matching macOS habit).
    /// </summary>
    private void HandleSpaceKey()
    {
        bool alwaysListen = string.Equals(_library.Prefs.ReaderSpaceAction, "listen", StringComparison.OrdinalIgnoreCase);
        if (alwaysListen || TtsBarHost.Visibility == Visibility.Visible || _audio.IsPlaying)
        {
            ToggleListenPlayback();
        }
        else
        {
            _ = NextPageOrSectionAsync();
        }
    }

    private void PushHistoryPoint()
    {
        int curSent = CurrentReadingSentence();
        if (_backStack.Count == 0 || _backStack.Peek() != (_currentSection, curSent))
            _backStack.Push((_currentSection, curSent));
        _forwardStack.Clear();
        UpdateHistoryButtons();
    }

    private bool NavigateHistory(bool isBack)
    {
        var src = isBack ? _backStack : _forwardStack;
        var dst = isBack ? _forwardStack : _backStack;
        if (src.Count == 0) return false;
        int curSent = CurrentReadingSentence();
        dst.Push((_currentSection, curSent));
        var target = src.Pop();
        UpdateHistoryButtons();
        JumpTo(target.Section, target.Sentence, recordHistory: false);
        return true;
    }

    private void UpdateHistoryButtons()
    {
        HistBackBtn.IsEnabled = _backStack.Count > 0;
        HistFwdBtn.IsEnabled = _forwardStack.Count > 0;
    }

    private void UpdateHeaderAndStatus()
    {
        TopBookTitleText.Text = _detail.Summary.Title;
        L10n.Skip(TopBookTitleText);

        string chapTitle = ResolveCurrentChapterTitle();
        TopChapterText.Text = chapTitle;
        L10n.Skip(TopChapterText);
        TopChapterSep.Visibility = string.IsNullOrWhiteSpace(chapTitle) ? Visibility.Collapsed : Visibility.Visible;

        if (_settings.Mode == LayoutMode.Scrolled)
        {
            double secPct = Math.Clamp(_scrollProgress, 0.0, 1.0);
            double bookPct = _detail.Sections.Length > 0
                ? Math.Clamp((_currentSection + secPct) / _detail.Sections.Length * 100.0, 0, 100)
                : secPct * 100.0;
            StatusProgressText.Text = L10n.T($"本章 {(int)Math.Round(secPct * 100)}% · {bookPct:0.#}%");
        }
        else
        {
            int pageTotal = Math.Max(1, _layout?.PageCount ?? 1);
            int pageNum = Math.Clamp(_currentPage + 1, 1, pageTotal);
            double bookPct = _detail.Sections.Length > 0
                ? Math.Clamp((_currentSection + (double)pageNum / pageTotal) / _detail.Sections.Length * 100.0, 0, 100)
                : 0;
            int remPages = Math.Max(0, pageTotal - pageNum);
            int remMin = Math.Max(1, (int)Math.Ceiling(remPages * 1.2));

            StatusProgressText.Text = L10n.T($"第 {pageNum} / {pageTotal} 页 · {bookPct:0.#}% · 本章还剩 {remMin} 分钟");
        }
        StatusFontSizeText.Text = $"{Math.Round(_settings.FontSize)}px";
        StatusModeBtn.Content = L10n.T(_settings.Mode == LayoutMode.Scrolled ? "滚动" : "分页");
        StatusColBtn.Content = L10n.T(EffectiveColumnCount() == 2 ? "双栏" : "单栏");
    }

    private string ResolveCurrentChapterTitle()
    {
        var tocHit = _detail.Toc.LastOrDefault(t => t.Section <= _currentSection);
        if (tocHit is not null && !string.IsNullOrWhiteSpace(tocHit.Title)) return tocHit.Title;
        if (_currentSection < _detail.Sections.Length && !string.IsNullOrWhiteSpace(_detail.Sections[_currentSection].Title))
            return _detail.Sections[_currentSection].Title!;
        return L10n.T($"第 {_currentSection + 1} 节");
    }

    private void AdjustFontSize(double delta) => SetFontSize(Math.Clamp(_settings.FontSize + delta, 12, 34));

    private void SetFontSize(double size)
    {
        _settings = _settings with { FontSize = Math.Clamp(Math.Round(size), 12, 34) };
        PushSettingsToReader(save: true);
        UpdateHeaderAndStatus();
    }

    private void ShowReaderAlert(string msg, string? actionLabel = null, Action? action = null, int seconds = 4)
    {
        ReaderAlertText.Text = L10n.T(msg);
        _alertAction = action;
        if (!string.IsNullOrWhiteSpace(actionLabel) && action is not null)
        {
            ReaderAlertActionBtn.Content = L10n.T(actionLabel);
            ReaderAlertActionBtn.Visibility = Visibility.Visible;
        }
        else
        {
            ReaderAlertActionBtn.Visibility = Visibility.Collapsed;
        }
        ReaderAlertBanner.Visibility = Visibility.Visible;
        _alertTimer?.Stop();
        _alertTimer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(seconds) };
        _alertTimer.Tick += (_, _) => { _alertTimer.Stop(); ReaderAlertBanner.Visibility = Visibility.Collapsed; };
        _alertTimer.Start();
    }

    private void BuildMoreMenuFlyout()
    {
        var menu = new MenuFlyout();
        void Add(string label, Action act, string? key = null)
        {
            var mi = new MenuFlyoutItem { Text = L10n.T(label) };
            if (key is not null) mi.KeyboardAcceleratorTextOverride = key;
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }

        Add("沉浸全屏", () => _ownerWindow.ToggleFullScreen(), "F11");
        Add("添加或取消书签", ToggleBookmarkAtCurrentPosition, "Ctrl+D");
        Add("跳转章节与页码…", () => StatusProgress_Click(StatusProgressBtn, new RoutedEventArgs()), "Ctrl+G");
        Add("前情提要（不剧透）…", () => _ = ShowRecapDialogAsync());
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("净化替换规则…", async () =>
        {
            if (RootGrid.XamlRoot is { } xr && await BookToolsDialogs.ShowReplaceRulesDialogAsync(xr, _library, _bookId, _detail.Summary.Title))
                LoadSection(_currentSection, forceReload: true);
        });
        Add("AI 文本精校与润色…", async () =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await global::Yuyue.Audiobook.AlignCheckAndPolishService.ShowTextPolishDialogAsync(xr, _library, _bookId, () => LoadSection(_currentSection, forceReload: true));
        });
        if (_detail.Summary.Format == BookFormat.Txt)
        {
            Add("TXT 分章与排版…", async () =>
            {
                if (RootGrid.XamlRoot is { } xr && await BookToolsDialogs.ShowTxtLayoutDialogAsync(xr, _library, _bookId, _detail.Summary.Title))
                {
                    _detail = _library.GetBookDetail(_bookId);
                    LoadSection(_currentSection, forceReload: true);
                }
            });
        }
        Add("自定义纸张…", async () =>
        {
            if (RootGrid.XamlRoot is { } xr)
            {
                var cp = await CustomPaperDialog.ShowAsync(xr, _library);
                if (cp is not null)
                {
                    if (PaperAppearance.PickCustom(_library, cp, _settings, out var updated, perBookOverride: _ownSettings))
                        _settings = updated;
                    PushSettingsToReader(save: true);
                }
            }
        });
        Add("导出带笔记的 EPUB…", async () =>
        {
            string? path = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, $"{_detail.Summary.Title}-笔记版", [("EPUB", [".epub"])]);
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                await _library.NoteExports.ExportAnnotatedEpubAsync(_bookId, path);
                ShowReaderAlert($"已导出带笔记的 EPUB：{Path.GetFileName(path)}");
            }
            catch (Exception ex) { ShowReaderAlert($"导出失败：{ex.Message}"); }
        });
        if (_isTextVersion)
        {
            Add("整理录音转写错字…", async () =>
            {
                if (RootGrid.XamlRoot is { } xr)
                {
                    await global::Yuyue.Audiobook.AlignCheckAndPolishService.ShowTextPolishDialogAsync(xr, _library, _bookId, () =>
                    {
                        if (!_disposed) LoadSection(_currentSection);
                    });
                }
            });
        }
        Add("书籍详情与阅读记录", () =>
        {
            App.CurrentApp.ShowShelf();
            App.CurrentApp.OpenBookRecordInPrimaryShelf(_bookId);
        });
        if (!_isSecondarySplitPane)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("撕出到独立窗口", () => RequestDetachWindow?.Invoke());
            Add("阅读设置…", () => _ownerWindow.ShowSettings("reading"), "Ctrl+,");
        }
        MoreBtn.Flyout = menu;
    }

    private void CheckInitialContextCards()
    {
        if (_disposed) return;
        bool dark = CurrentPaperChrome?.IsDark ?? ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var resolved = PaperAppearance.Resolve(_library, _settings);
        var activePaper = YYPapers.FromId(PaperAppearance.PaperCssId(resolved.Preset));

        // 0. Audio-transcribed text version notice (parity with Apple TextVersionBanner & Android ReaderScreen)
        if (_isTextVersion)
        {
            ShowReaderAlert("这是录音转出来的文字版，可能有错字", "整理错字", () =>
            {
                if (RootGrid.XamlRoot is { } xr)
                {
                    _ = global::Yuyue.Audiobook.AlignCheckAndPolishService.ShowTextPolishDialogAsync(xr, _library, _bookId, () =>
                    {
                        if (!_disposed) LoadSection(_currentSection);
                    });
                }
            }, seconds: 7);
        }

        // 1. TXT encoding/chapter doubt pill (ReaderReturn.swift)
        if (_detail.Summary.Format == BookFormat.Txt && !_library.Prefs.DismissedTxtDoubtBooks.Contains(_bookId))
        {
            try
            {
                string? doubt = _library.Core.TxtDoubt(_bookId);
                if (!string.IsNullOrWhiteSpace(doubt))
                {
                    DoubtPillHost.Content = ReaderRecapAndSleepHelper.BuildDoubtPill(
                        doubt,
                        p,
                        onOpen: async () =>
                        {
                            DoubtPillHost.Visibility = Visibility.Collapsed;
                            if (RootGrid.XamlRoot is { } xr && await BookToolsDialogs.ShowTxtLayoutDialogAsync(xr, _library, _bookId, _detail.Summary.Title))
                            {
                                _detail = _library.GetBookDetail(_bookId);
                                LoadSection(_currentSection, forceReload: true);
                            }
                        },
                        onDismiss: () =>
                        {
                            DoubtPillHost.Visibility = Visibility.Collapsed;
                            _library.Prefs.DismissedTxtDoubtBooks.Add(_bookId);
                            _library.SavePrefs();
                        },
                        paper: activePaper);
                    DoubtPillHost.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        // 2. ElsewhereCard when another synced device moved to a newer spot (forward OR backward)
        if (CheckElsewhereProgress(p, tc))
            return;

        // 3. SleepReturnCard ("回到昨晚" sleep-timer origin return pill)
        var sleepInfo = ReaderRecapAndSleepHelper.TryGetSleepReturn(_library, _bookId, _detail);
        if (sleepInfo is not null)
        {
            SleepReturnHost.Content = ReaderRecapAndSleepHelper.BuildSleepReturnCard(sleepInfo, p, tc, go =>
            {
                SleepReturnHost.Visibility = Visibility.Collapsed;
                ReaderRecapAndSleepHelper.ClearSleepReturn(_library, _bookId);
                if (go) JumpTo(sleepInfo.Section, sleepInfo.Sentence, recordHistory: true);
            });
            SleepReturnHost.Visibility = Visibility.Visible;
        }

        // 4. RecapTopPill when returning after >= 1 day away and Companion AI is configured
        long? lastReadMs = _previousOpenedAtMs;
        try
        {
            var latestProg = _library.Core.LatestProgress(_bookId);
            if (latestProg is not null && latestProg.At > (lastReadMs ?? 0))
                lastReadMs = latestProg.At;
        }
        catch { }

        if (_library.Companion.IsConfigured && lastReadMs is long lms && lms > 0)
        {
            long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int daysAway = (int)((nowSec - lms / 1000.0) / 86400.0);
            int curSent = CurrentReadingSentence();
            if (daysAway >= 1 && (_currentSection > _detail.Start || curSent > 20))
            {
                uint snapSec = _currentSection;
                int snapSent = curSent;
                _pendingRecapOffer = (snapSec, snapSent, daysAway, lms);
                _readyRecap = ReaderRecapAndSleepHelper.TryGetCachedRecap(_library, _bookId, snapSec, snapSent, daysAway);
                RecapTopPillLabel.Text = L10n.T($"前情 · {daysAway}天前");
                RecapTopPillBtn.Visibility = Visibility.Visible;
                ApplyPaperAndChrome();

                if (_readyRecap is null && !_library.Companion.IsCurrentProviderPaidCloud)
                {
                    _ = Task.Run(async () =>
                    {
                        var pre = await ReaderRecapAndSleepHelper.TryLoadOrGenerateRecapAsync(
                            _library, _bookId, _detail, lms, snapSec, snapSent, manual: false);
                        if (pre is not null && !_disposed)
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (!_disposed) _readyRecap = pre;
                            });
                        }
                    });
                }
            }
        }
    }

    private bool CheckElsewhereProgress(YYPalette p, YYTintColors tc)
    {
        try
        {
            var latest = _library.Core.LatestProgress(_bookId);
            var here = _library.GetProgress(_bookId);
            long mineAt = here?.At ?? 0;
            int curSent = CurrentReadingSentence();
            double hereFrac = here?.Fraction ?? CalculateBookFraction(_currentSection, curSent);
            bool differentSpot = latest is not null
                && (latest.Position.Section != _currentSection || (int)latest.Position.Sentence != curSent);

            if (latest is not null
                && !latest.ThisDevice
                && differentSpot
                && latest.At > mineAt
                && Math.Abs(latest.Fraction - hereFrac) > 0.005)
            {
                string? chapTitle = _detail.Toc.LastOrDefault(t => t.Section <= latest.Position.Section)?.Title;
                if (string.IsNullOrWhiteSpace(chapTitle) && latest.Position.Section < _detail.Sections.Length)
                    chapTitle = _detail.Sections[latest.Position.Section].Title;

                string? excerpt = null;
                try
                {
                    var sents = _library.Core.Sentences(_bookId, latest.Position.Section);
                    if (latest.Position.Sentence < sents.Length)
                        excerpt = sents[latest.Position.Sentence];
                }
                catch { }

                ElsewhereCardHost.Content = ReaderRecapAndSleepHelper.BuildElsewhereCard(
                    latest,
                    hereFrac,
                    chapTitle,
                    excerpt,
                    p,
                    tc,
                    onStay: () =>
                    {
                        ElsewhereCardHost.Visibility = Visibility.Collapsed;
                        SaveCurrentProgress();
                    },
                    onJump: () =>
                    {
                        ElsewhereCardHost.Visibility = Visibility.Collapsed;
                        bool wasPlaying = _audio.IsPlaying;
                        if (wasPlaying && latest.Position.Section != _currentSection)
                            _autoListenOnReady = true;
                        JumpTo(latest.Position.Section, (int)latest.Position.Sentence, recordHistory: true);
                        if (wasPlaying && latest.Position.Section == _currentSection)
                            StartListenFromSentence((int)latest.Position.Sentence);
                        SaveCurrentProgress();
                    });
                ElsewhereCardHost.Visibility = Visibility.Visible;
                return true;
            }
        }
        catch { }
        return false;
    }

    private async void RecapTopPill_Click(object sender, RoutedEventArgs e)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        if (_readyRecap is { } ready)
        {
            await PresentRecapCardDialogAsync(xr, ready, manualOpened: false);
            return;
        }
        await ShowRecapDialogAsync();
    }

    private async Task PresentRecapCardDialogAsync(XamlRoot xr, RecapDto recap, bool manualOpened)
    {
        bool dark = CurrentPaperChrome?.IsDark ?? ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        ContentDialog? dlgRef = null;
        var card = ReaderRecapAndSleepHelper.BuildRecapCard(recap, p, tc, manualOpened, onDone: () => dlgRef?.Hide());
        dlgRef = YYChrome.CreateFluentDialog(xr, dark, 420, card, tint: _library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
    }

    private async Task ShowRecapDialogAsync()
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        if (!_library.Companion.IsConfigured)
        {
            ShowReaderAlert("前情提要需要先在「设置 → 书伴」配置 AI 模型", "去配置", () => _ownerWindow.ShowSettings("companion"), seconds: 6);
            return;
        }
        ShowReaderAlert("正在为你整理截至当前页的不剧透前情提要…", seconds: 8);
        int curSent = CurrentReadingSentence();
        long? lastMs = _pendingRecapOffer?.LastReadMs ?? _previousOpenedAtMs;
        var recap = await ReaderRecapAndSleepHelper.TryLoadOrGenerateRecapAsync(_library, _bookId, _detail, lastMs, _currentSection, curSent, manual: true);
        if (_disposed) return;
        if (recap is null)
        {
            ShowReaderAlert("暂时无法生成前情提要，请稍后重试");
            return;
        }
        _readyRecap = recap;
        await PresentRecapCardDialogAsync(xr, recap, manualOpened: true);
    }

    // Complete Fluent 2 Typography & Paper Flyout
    private void TypographyBtn_Click(object sender, RoutedEventArgs e)
    {
        bool dark = CurrentPaperChrome?.IsDark ?? ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        Flyout? flyoutRef = null;

        var root = new StackPanel
        {
            Spacing = 12,
            Width = 336,
            Padding = new Thickness(16, 14, 16, 14),
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
        };
        ThemeHelper.ApplyControlThemeResources(root.Resources, dark, _library.Prefs.Tint);

        // 1. Reading papers + Custom papers + Follow system toggle
        var paperHead = new Grid { ColumnSpacing = 8 };
        paperHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        paperHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var paperLbl = new TextBlock { Text = L10n.T("阅读纸张"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        var newPaperBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Content = L10n.T("+ 自定义"),
            FontSize = 11.5,
            MinHeight = 22,
            Padding = new Thickness(6, 0, 6, 0),
            Foreground = ThemeHelper.Brush(tc.Accent),
        };
        newPaperBtn.Click += async (_, _) =>
        {
            flyoutRef?.Hide();
            if (RootGrid.XamlRoot is not { } xr) return;
            var cp = await CustomPaperDialog.ShowEditorAsync(xr, _library, existing: null, currentSettings: _settings);
            if (cp is not null)
            {
                if (PaperAppearance.PickCustom(_library, cp, _settings, out var updated, perBookOverride: _ownSettings))
                    _settings = updated;
                PushSettingsToReader(save: true);
            }
        };
        Grid.SetColumn(paperLbl, 0);
        Grid.SetColumn(newPaperBtn, 1);
        paperHead.Children.Add(paperLbl);
        paperHead.Children.Add(newPaperBtn);
        root.Children.Add(paperHead);

        var paperRow = new Grid { ColumnSpacing = 6 };
        (Paper Preset, string Name, Color Bg, Color Ink)[] papers =
        [
            (Paper.Plain, "素白", YYPapers.Plain.Bg, YYPapers.Plain.Ink),
            (Paper.Kami, "纸本", YYPapers.Kami.Bg, YYPapers.Kami.Ink),
            (Paper.Sage, "芦苇", YYPapers.Sage.Bg, YYPapers.Sage.Ink),
            (Paper.Eink, "铅纸", YYPapers.Eink.Bg, YYPapers.Eink.Ink),
            (Paper.Dark, "暗黑", YYPapers.Dark.Bg, YYPapers.Dark.Ink),
            (Paper.KamiNight, "纸夜", YYPapers.Kaminight.Bg, YYPapers.Kaminight.Ink),
        ];
        var activeRes = PaperAppearance.Resolve(_library, _settings);
        for (int i = 0; i < papers.Length; i++)
        {
            paperRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (preset, name, bg, ink) = papers[i];
            bool isSel = activeRes.Custom is null && activeRes.Preset == preset;
            var btn = new Button
            {
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(bg),
                BorderBrush = ThemeHelper.Brush(isSel ? tc.Accent : p.Line),
                BorderThickness = new Thickness(isSel ? 2 : 1),
                Content = new TextBlock { Text = L10n.T(name), FontSize = 11, FontWeight = isSel ? FontWeights.SemiBold : FontWeights.Normal, Foreground = ThemeHelper.Brush(ink) },
                UseSystemFocusVisuals = false,
            };
            btn.Click += (_, _) =>
            {
                if (PaperAppearance.PickPreset(_library, preset, _settings, out var updated, perBookOverride: _ownSettings))
                    _settings = updated;
                PushSettingsToReader(save: true);
                flyoutRef?.Hide();
            };
            Grid.SetColumn(btn, i);
            paperRow.Children.Add(btn);
        }
        root.Children.Add(paperRow);

        // Saved custom papers row (if any)
        var savedCustoms = _library.Core.CustomPapers();
        if (savedCustoms.Length > 0)
        {
            var cpWrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var cp in savedCustoms.Take(5))
            {
                var cpCap = cp;
                bool isSel = activeRes.Custom?.Id == cpCap.Id;
                Color bgCol = CustomPaperDialog.ParseHexColor(cpCap.Bg, YYPapers.Kami.Bg);
                Color inkCol = CustomPaperDialog.ParseHexColor(cpCap.Ink ?? "", YYPapers.Kami.Ink);
                try
                {
                    var cols = YuyueCoreMethods.PaperColors(cpCap);
                    bgCol = ParseHexColor(cols.Bg, bgCol);
                    inkCol = ParseHexColor(cols.Ink, inkCol);
                }
                catch { }
                var cBtn = new Button
                {
                    Height = 28,
                    MinHeight = 28,
                    Padding = new Thickness(8, 0, 8, 0),
                    CornerRadius = new CornerRadius(6),
                    Background = ThemeHelper.Brush(bgCol),
                    BorderBrush = ThemeHelper.Brush(isSel ? tc.Accent : p.Line),
                    BorderThickness = new Thickness(isSel ? 2 : 1),
                    Content = new TextBlock { Text = cpCap.Name, FontSize = 11, Foreground = ThemeHelper.Brush(inkCol), MaxWidth = 64, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis },
                    UseSystemFocusVisuals = false,
                };
                cBtn.Click += (_, _) =>
                {
                    if (PaperAppearance.PickCustom(_library, cpCap, _settings, out var updated, perBookOverride: _ownSettings))
                        _settings = updated;
                    PushSettingsToReader(save: true);
                    flyoutRef?.Hide();
                };
                var cpMenu = new MenuFlyout();
                var editPaperMi = new MenuFlyoutItem { Text = L10n.T("调整这张纸…") };
                editPaperMi.Click += async (_, _) =>
                {
                    flyoutRef?.Hide();
                    if (RootGrid.XamlRoot is not { } xr) return;
                    var edited = await CustomPaperDialog.ShowEditorAsync(
                        xr,
                        _library,
                        existing: cpCap,
                        currentSettings: _settings,
                        onDeleted: deleted =>
                        {
                            ShowReaderAlert($"已删除纸张「{deleted.Name}」", "撤销", () =>
                            {
                                try { _library.RestoreCustomPaper(deleted.Id); } catch { }
                                ShowReaderAlert($"已恢复纸张「{deleted.Name}」");
                            });
                        });
                    if (edited is not null)
                    {
                        if (PaperAppearance.PickCustom(_library, edited, _settings, out var updated, perBookOverride: _ownSettings))
                            _settings = updated;
                        PushSettingsToReader(save: true);
                    }
                };
                var delPaperMi = new MenuFlyoutItem { Text = L10n.T("删除这张纸"), Foreground = ThemeHelper.Brush(p.Danger) };
                delPaperMi.Click += (_, _) =>
                {
                    flyoutRef?.Hide();
                    string deletedId = cpCap.Id;
                    string deletedName = cpCap.Name;
                    try { _library.Core.DeleteCustomPaper(deletedId); } catch { }
                    PaperAppearance.Forget(_library, deletedId);
                    PushSettingsToReader(save: false);
                    ShowReaderAlert($"已删除纸张「{deletedName}」", "撤销", () =>
                    {
                        try { _library.RestoreCustomPaper(deletedId); } catch { }
                        ShowReaderAlert($"已恢复纸张「{deletedName}」");
                    });
                };
                cpMenu.Items.Add(editPaperMi);
                cpMenu.Items.Add(delPaperMi);
                cBtn.ContextFlyout = cpMenu;
                cpWrap.Children.Add(cBtn);
            }
            root.Children.Add(cpWrap);
        }

        // 2. Font family segmented selector + imported custom fonts
        root.Children.Add(new TextBlock { Text = L10n.T("正文字体"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) });
        var fontRow = new Grid { ColumnSpacing = 6 };
        (Font? F, string Label)[] fonts = [(null, "随纸张"), (Font.Sans, "黑体"), (Font.Serif, "宋体"), (Font.Kai, "楷体")];
        for (int i = 0; i < fonts.Length; i++)
        {
            fontRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (fVal, fLbl) = fonts[i];
            bool on = _settings.Font == fVal && string.IsNullOrEmpty(_settings.FontFace);
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 30,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(on ? tc.Soft : p.Inset),
                BorderBrush = ThemeHelper.Brush(on ? tc.Accent : p.Line),
                BorderThickness = new Thickness(1),
                Content = new TextBlock { Text = L10n.T(fLbl), FontSize = 12, Foreground = ThemeHelper.Brush(on ? tc.Accent : p.T1) },
                UseSystemFocusVisuals = false,
            };
            btn.Click += (_, _) =>
            {
                _settings = _settings with { Font = fVal, FontFace = null };
                PushSettingsToReader(save: true);
                flyoutRef?.Hide();
            };
            Grid.SetColumn(btn, i);
            fontRow.Children.Add(btn);
        }
        root.Children.Add(fontRow);

        var extraFonts = new List<(string Label, Font? Font, string? FontFace)>();
        foreach (var pack in _library.Fonts.Catalog)
        {
            if (!_library.Fonts.IsPackInstalled(pack.Id)) continue;
            if (string.Equals(pack.Id, "wenkai", StringComparison.OrdinalIgnoreCase))
            {
                extraFonts.Add((pack.Name, Font.WenKai, null));
            }
            else
            {
                Font fallbackFont = pack.Kind switch
                {
                    FontKind.Hei => Font.Sans,
                    FontKind.Kai or FontKind.Hand => Font.Kai,
                    _ => Font.Serif,
                };
                extraFonts.Add((pack.Name, fallbackFont, pack.Id));
            }
        }
        foreach (var fn in _library.Fonts.GetCustomFontFiles())
        {
            extraFonts.Add((_library.Fonts.GetFontFileDisplayName(fn), _settings.Font, $"file:{fn}"));
        }
        if (!string.IsNullOrWhiteSpace(_settings.FontFace) &&
            _settings.FontFace.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            string localFamily = _settings.FontFace[6..].Trim();
            if (!string.IsNullOrEmpty(localFamily))
                extraFonts.Add((localFamily, _settings.Font, _settings.FontFace));
        }

        if (extraFonts.Count > 0)
        {
            var customFontCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = L10n.T("选用已下载或已导入字体…") };
            foreach (var ef in extraFonts) customFontCombo.Items.Add(ef.Label);
            int curFIdx = extraFonts.FindIndex(ef =>
                ef.FontFace is not null
                    ? string.Equals(_settings.FontFace, ef.FontFace, StringComparison.OrdinalIgnoreCase)
                    : (string.IsNullOrEmpty(_settings.FontFace) && _settings.Font == ef.Font));
            if (curFIdx >= 0) customFontCombo.SelectedIndex = curFIdx;
            customFontCombo.SelectionChanged += (_, _) =>
            {
                if (customFontCombo.SelectedIndex >= 0 && customFontCombo.SelectedIndex < extraFonts.Count)
                {
                    var chosen = extraFonts[customFontCombo.SelectedIndex];
                    _settings = _settings with { Font = chosen.Font, FontFace = chosen.FontFace };
                    PushSettingsToReader(save: true);
                    flyoutRef?.Hide();
                }
            };
            root.Children.Add(customFontCombo);
        }

        // 3. Font size & Line height sliders
        var szHeader = new Grid { ColumnSpacing = 8 };
        szHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        szHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var szLbl = new TextBlock { Text = L10n.T("字号"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        var szValTb = new TextBlock { Text = $"{Math.Round(_settings.FontSize)} px", FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(szLbl, 0);
        Grid.SetColumn(szValTb, 1);
        szHeader.Children.Add(szLbl);
        szHeader.Children.Add(szValTb);
        var szSlider = new Slider { Minimum = 12, Maximum = 34, StepFrequency = 1, Value = Math.Clamp(_settings.FontSize, 12, 34) };
        szSlider.ValueChanged += (_, args) =>
        {
            szValTb.Text = $"{Math.Round(args.NewValue)} px";
            SetFontSize(args.NewValue);
        };
        root.Children.Add(szHeader);
        root.Children.Add(szSlider);

        var lhHeader = new Grid { ColumnSpacing = 8 };
        lhHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lhHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lhLbl = new TextBlock { Text = L10n.T("行距"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        double curLh = _settings.LineHeight ?? 1.9;
        var lhValTb = new TextBlock { Text = $"{curLh:0.0}", FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(lhLbl, 0);
        Grid.SetColumn(lhValTb, 1);
        lhHeader.Children.Add(lhLbl);
        lhHeader.Children.Add(lhValTb);
        var lhSlider = new Slider { Minimum = 1.4, Maximum = 2.6, StepFrequency = 0.1, Value = curLh };
        lhSlider.ValueChanged += (_, args) =>
        {
            lhValTb.Text = $"{args.NewValue:0.0}";
            _settings = _settings with { LineHeight = Math.Round(args.NewValue, 1) };
            PushSettingsToReader(save: true);
        };
        root.Children.Add(lhHeader);
        root.Children.Add(lhSlider);

        // 4. Layout mode + Columns + Paragraph spacing + Indent + Justify + Script
        Grid MakeOptionRow(string label, FrameworkElement ctrl)
        {
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tb = new TextBlock { Text = L10n.T(label), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tb, 0);
            Grid.SetColumn(ctrl, 1);
            g.Children.Add(tb);
            g.Children.Add(ctrl);
            return g;
        }

        root.Children.Add(MakeOptionRow("翻页方式", YYChrome.MakeSegmentedBar(
            [("page", "左右翻页"), ("scroll", "上下滚动")],
            _settings.Mode == LayoutMode.Scrolled ? "scroll" : "page",
            v => { _settings = _settings with { Mode = v == "scroll" ? LayoutMode.Scrolled : LayoutMode.Paginated }; PushSettingsToReader(save: true); UpdateHeaderAndStatus(); flyoutRef?.Hide(); },
            dark, fontSize: 11.5, height: 28)));

        root.Children.Add(MakeOptionRow("栏数排版", YYChrome.MakeSegmentedBar(
            [("1", "单栏"), ("2", "双栏")],
            _library.Prefs.TwoColumns ? "2" : "1",
            v => { _library.Prefs.TwoColumns = v == "2"; _library.SavePrefs(); PushSettingsToReader(save: false); UpdateHeaderAndStatus(); flyoutRef?.Hide(); },
            dark, fontSize: 11.5, height: 28)));

        root.Children.Add(MakeOptionRow("段落间距", YYChrome.MakeSegmentedBar(
            [("0", "无"), ("0.5", "适中"), ("1.0", "宽")],
            _settings.ParagraphSpacing switch { <= 0.1 => "0", >= 0.8 => "1.0", _ => "0.5" },
            v => { _settings = _settings with { ParagraphSpacing = v switch { "0" => 0.0, "1.0" => 1.0, _ => 0.5 } }; PushSettingsToReader(save: true); flyoutRef?.Hide(); },
            dark, fontSize: 11.5, height: 28)));

        root.Children.Add(MakeOptionRow("首行缩进", YYChrome.MakeSegmentedBar(
            [("auto", "跟书"), ("on", "缩进"), ("off", "不缩")],
            _settings.Indent is null ? "auto" : (_settings.Indent.Value ? "on" : "off"),
            v => { _settings = _settings with { Indent = v switch { "on" => true, "off" => false, _ => null } }; PushSettingsToReader(save: true); flyoutRef?.Hide(); },
            dark, fontSize: 11.5, height: 28)));

        root.Children.Add(MakeOptionRow("段落对齐", YYChrome.MakeSegmentedBar(
            [("auto", "跟书"), ("on", "对齐"), ("off", "靠左")],
            _settings.Justify is null ? "auto" : (_settings.Justify.Value ? "on" : "off"),
            v => { _settings = _settings with { Justify = v switch { "on" => true, "off" => false, _ => null } }; PushSettingsToReader(save: true); flyoutRef?.Hide(); },
            dark, fontSize: 11.5, height: 28)));

        var curScript = _library.Core.BookScript(_bookId);
        root.Children.Add(MakeOptionRow("简繁转换", YYChrome.MakeSegmentedBar(
            [("orig", "原样"), ("simp", "简体"), ("trad", "繁體")],
            curScript switch { Script.Simplified => "simp", Script.Traditional => "trad", _ => "orig" },
            v =>
            {
                Script? sc = v switch { "simp" => Script.Simplified, "trad" => Script.Traditional, _ => null };
                _library.Core.SetBookScript(_bookId, sc);
                LoadSection(_currentSection, forceReload: true);
                flyoutRef?.Hide();
            },
            dark, fontSize: 11.5, height: 28)));

        var compSw = new ToggleSwitch { IsOn = _settings.Compress };
        compSw.Toggled += (_, _) => { _settings = _settings with { Compress = compSw.IsOn }; PushSettingsToReader(save: true); };
        root.Children.Add(MakeOptionRow("标点挤压", compSw));

        var glossSw = new ToggleSwitch { IsOn = _library.Prefs.ForeignGlossEnabled };
        glossSw.Toggled += (_, _) =>
        {
            _library.Prefs.ForeignGlossEnabled = glossSw.IsOn;
            _library.SavePrefs();
            _ = RefreshSectionOverlaysAsync();
        };
        root.Children.Add(MakeOptionRow("外文难词行间释义", glossSw));

        var bubblesSw = new ToggleSwitch { IsOn = _bubblesOn };
        bubblesSw.Toggled += (_, _) =>
        {
            _bubblesOn = bubblesSw.IsOn;
            try { _library.Core.SetBookBubbles(_bookId, _bubblesOn); } catch { }
            _ = ApplySectionBubblesAsync();
        };
        root.Children.Add(MakeOptionRow("对话角色气泡", bubblesSw));

        var perBookSw = new ToggleSwitch { IsOn = _ownSettings };
        perBookSw.Toggled += (_, _) =>
        {
            _ownSettings = perBookSw.IsOn;
            if (_ownSettings)
            {
                _library.SetBookReaderSettings(_bookId, _settings);
            }
            else
            {
                _library.SetBookReaderSettings(_bookId, null);
                _settings = _library.GetReaderSettings();
                ApplyPaperAndChrome();
                PushSettingsToReader(save: false);
                UpdateHeaderAndStatus();
            }
        };
        root.Children.Add(MakeOptionRow("仅对本书生效", perBookSw));

        flyoutRef = new Flyout
        {
            FlyoutPresenterStyle = (Style)Application.Current.Resources["YYFloatFlyoutPresenterStyle"],
            Content = new ScrollViewer { MaxHeight = 560, Content = root },
        };
        flyoutRef.ShowAt(TypographyBtn);
    }

    // Event handlers
    private void LeftPaneBtn_Click(object sender, RoutedEventArgs e)
    {
        _leftPaneOpen = !_leftPaneOpen;
        if (!_isSecondarySplitPane)
        {
            _library.Prefs.ReaderLeftPaneOpen = _leftPaneOpen;
            _library.SavePrefs();
        }
        ApplyDockedPanesLayout();
    }

    private void CloseLeftPane_Click(object sender, RoutedEventArgs e)
    {
        _leftPaneOpen = false;
        if (!_isSecondarySplitPane)
        {
            _library.Prefs.ReaderLeftPaneOpen = false;
            _library.SavePrefs();
        }
        ApplyDockedPanesLayout();
    }

    private void RightPaneTab_Click(object sender, RoutedEventArgs e)
    {
        string tab = (sender as FrameworkElement)?.Tag as string ?? "notes";
        if (_rightPaneOpen && _rightPaneTab == tab)
        {
            _rightPaneOpen = false;
        }
        else
        {
            _rightPaneTab = tab;
            _rightPaneOpen = true;
        }
        ApplyDockedPanesLayout();
    }

    private void CloseRightPane_Click(object sender, RoutedEventArgs e)
    {
        _rightPaneOpen = false;
        ApplyDockedPanesLayout();
    }

    private void SearchBtn_Click(object sender, RoutedEventArgs e)
    {
        OpenLeftPane("search");
        FocusLeftSearchBox();
    }

    private void HistBackBtn_Click(object sender, RoutedEventArgs e) => NavigateHistory(isBack: true);
    private void HistFwdBtn_Click(object sender, RoutedEventArgs e) => NavigateHistory(isBack: false);

    private void SplitViewBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        var others = _library.GetShelfBooks()
            .Where(b => b.Id != _bookId && b.Available && b.Format is BookFormat.Epub or BookFormat.Txt or BookFormat.Mobi)
            .OrderByDescending(b => b.OpenedAt ?? b.AddedAt)
            .Take(12)
            .ToList();

        if (others.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = L10n.T("书库中暂无其他电子书可对照"), IsEnabled = false });
        }
        else
        {
            foreach (var b in others)
            {
                string bid = b.Id;
                var mi = new MenuFlyoutItem { Text = b.Title };
                mi.Click += (_, _) => RequestOpenSplitBook?.Invoke(bid);
                menu.Items.Add(mi);
            }
        }
        menu.ShowAt(SplitViewBtn);
    }

    private void CloseSplitPane_Click(object sender, RoutedEventArgs e) => RequestCloseSplitPane?.Invoke();

    private void StatusProgress_Click(object sender, RoutedEventArgs e)
    {
        // Quick chapter/page jump flyout
        bool dark = CurrentPaperChrome?.IsDark ?? ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        int totalPages = Math.Max(1, _layout?.PageCount ?? 1);
        var panel = new StackPanel
        {
            Spacing = 10,
            Width = 260,
            Padding = new Thickness(14, 12, 14, 12),
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
        };
        ThemeHelper.ApplyControlThemeResources(panel.Resources, dark, _library.Prefs.Tint);
        var lbl = new TextBlock { Text = L10n.T($"跳转本章页码（当前第 {_currentPage + 1} / {totalPages} 页）"), FontSize = 12 };
        var slider = new Slider { Minimum = 1, Maximum = totalPages, Value = Math.Clamp(_currentPage + 1, 1, totalPages), StepFrequency = 1 };
        slider.ValueChanged += (_, args) =>
        {
            int targetP = Math.Clamp((int)Math.Round(args.NewValue) - 1, 0, totalPages - 1);
            if (targetP != _currentPage && _pager.IsInitialized)
            {
                if (_audio.IsPlaying)
                {
                    _autoTurnTargetPage = null;
                    _ttsFollowPage = false;
                }
                _currentPage = targetP;
                _currentSentence = _layout?.FirstSentenceOn(targetP) ?? _currentSentence;
                _ = _pager.Current.ScrollToPageAsync(targetP);
                UpdateHeaderAndStatus();
            }
        };
        panel.Children.Add(lbl);
        panel.Children.Add(slider);

        var flyout = new Flyout
        {
            FlyoutPresenterStyle = (Style)Application.Current.Resources["YYFloatFlyoutPresenterStyle"],
            Content = panel,
        };
        flyout.ShowAt(StatusProgressBtn);
    }

    private void StatusModeToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings = _settings with
        {
            Mode = _settings.Mode == LayoutMode.Scrolled ? LayoutMode.Paginated : LayoutMode.Scrolled,
        };
        PushSettingsToReader(save: true);
        UpdateHeaderAndStatus();
    }

    private void StatusColToggle_Click(object sender, RoutedEventArgs e)
    {
        _library.Prefs.TwoColumns = !_library.Prefs.TwoColumns;
        _library.SavePrefs();
        PushGeometryToReader();
        UpdateHeaderAndStatus();
    }

    private void FontSmaller_Click(object sender, RoutedEventArgs e) => AdjustFontSize(-1);
    private void FontLarger_Click(object sender, RoutedEventArgs e) => AdjustFontSize(+1);

    private void StatusPaperToggle_Click(object sender, RoutedEventArgs e)
    {
        var cur = PaperAppearance.Resolve(_library, _settings);
        Paper target = cur.IsDark ? Paper.Kami : Paper.KamiNight;
        if (PaperAppearance.PickPreset(_library, target, _settings, out var updated, perBookOverride: _ownSettings))
            _settings = updated;
        PushSettingsToReader(save: true);
    }

    private void BookStage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pager.IsInitialized)
        {
            PushGeometryToReader();
            UpdateHeaderAndStatus();
        }
    }

    private void ExitListenOnly_Click(object sender, RoutedEventArgs e)
    {
        ListenOnlyCover.Visibility = Visibility.Collapsed;
        ApplyPaperAndChrome();
    }

    private void ReaderAlertAction_Click(object sender, RoutedEventArgs e)
    {
        ReaderAlertBanner.Visibility = Visibility.Collapsed;
        _alertTimer?.Stop();
        _alertAction?.Invoke();
    }

    private bool IsPaneTabActiveAndVisible()
    {
        try
        {
            if (_ownerWindow is null) return false;
            if (_ownerWindow.AppWindow is { IsVisible: false }) return false;
            if (_ownerWindow.AppWindow?.Presenter is OverlappedPresenter op && op.State == OverlappedPresenterState.Minimized)
                return false;
            var activeTab = _ownerWindow.ActiveTab;
            if (activeTab is null) return false;
            if (_ownerTab is not null)
                return ReferenceEquals(activeTab, _ownerTab);
            return activeTab.ContainsBookId(_bookId);
        }
        catch
        {
            return false;
        }
    }

    private void OnTimeTrackerTick()
    {
        if (_disposed) return;
        bool playing = _audio?.IsPlaying == true;
        if (playing)
        {
            _accumListenSec += 30;
        }
        else if (IsPaneTabActiveAndVisible())
        {
            _accumReadSec += 30;
        }
        else
        {
            _lastTimeTickUtc = DateTime.UtcNow;
            return;
        }
        _lastTimeTickUtc = DateTime.UtcNow;
        FlushReadingTime();
    }

    private void FlushReadingTime(bool includePartial = false)
    {
        if (_disposed) return;
        if (includePartial)
        {
            uint partialSec = (uint)Math.Clamp((DateTime.UtcNow - _lastTimeTickUtc).TotalSeconds, 0, 29);
            if (partialSec >= 5)
            {
                if (_audio?.IsPlaying == true) _accumListenSec += partialSec;
                else if (IsPaneTabActiveAndVisible()) _accumReadSec += partialSec;
            }
            _lastTimeTickUtc = DateTime.UtcNow;
        }

        if (_accumReadSec > 0 || _accumListenSec > 0)
        {
            try
            {
                _library.AddTime(_bookId, _currentSection, _accumReadSec, _accumListenSec);
                _accumReadSec = 0;
                _accumListenSec = 0;
            }
            catch { }
        }
    }

    private static Color ShiftColor(Color c, int delta) => Color.FromArgb(
        c.A,
        (byte)Math.Clamp(c.R + delta, 0, 255),
        (byte)Math.Clamp(c.G + delta, 0, 255),
        (byte)Math.Clamp(c.B + delta, 0, 255));

    private static Color ParseHexColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string s = hex.Trim().TrimStart('#');
        if (s.Length == 6 &&
            byte.TryParse(s[0..2], System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
            byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
            byte.TryParse(s[4..6], System.Globalization.NumberStyles.HexNumber, null, out byte b))
        {
            return Color.FromArgb(255, r, g, b);
        }
        return fallback;
    }

    public (UIElement HostElement, ReaderWebViewHost? WebViewHost) GetWebViewCaptureTarget() =>
        (BookStage, _pager.IsInitialized ? _pager.Current : null);

    public async Task<bool> SaveCompositeScreenshotAsync(string path)
    {
        try
        {
            await QuoteShareDialog.SaveElementPngAsync(RootGrid, path);
            return File.Exists(path);
        }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _timeTracker.Stop();
        _alertTimer?.Stop();
        _scrollEdgeCueTimer?.Stop();
        try { _translateCts?.Cancel(); _translateCts?.Dispose(); } catch { }
        try { _sentenceTransCts?.Cancel(); _sentenceTransCts?.Dispose(); } catch { }
        FlushReadingTime(includePartial: true);
        SaveCurrentProgress();
        _disposed = true;
        DisposeAudioPlayer();
        try { _pager.Dispose(); } catch { }
    }
}
