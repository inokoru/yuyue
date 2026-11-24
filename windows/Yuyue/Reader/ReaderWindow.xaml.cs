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
    private static readonly HashSet<ReaderWindow> ActiveReaders = [];

    private readonly LibraryService _library;
    private readonly string _bookId;
    private readonly Action _onFontsChanged;
    private BookDetail _detail;
    private readonly ReaderPagerHost _pager;
    private ReaderWebViewHost _host => _pager.Current;
    private readonly AudioPlayer _audio;
    private readonly DispatcherTimer _timeTracker;
    private readonly DispatcherTimer _searchDebounce;
    private DispatcherTimer? _capsuleAutoHideTimer;
    private DispatcherTimer? _immersivePeekTimer;

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
    private string? _pendingAskQuestion;
    private string? _pendingSearchQuery;

    private string? _activePanel;   // toc | notes | search | companion | cast
    private string _tocTab = "toc"; // toc | bookmarks
    private string _notesTab = "marks"; // marks | thoughts | words
    private bool _searchShown;
    private bool _typesetOpen;
    private bool _isFullScreen;
    private bool _isForeignBook;
    private bool _hasDialogue;
    private bool _bubblesOn;
    private BubbleLine[] _sectionBubbles = [];
    private bool _dramaModeOn;
    private bool _companionWholeBook;
    private IReadOnlyList<SearchHit> _searchHits = [];
    private int _hitIndex = -1;
    private readonly List<(string Role, string Content)> _companionHistory = [];
    private string[] _lastCompanionNextQuestions = [];

    private ReaderMessage? _selection;
    private Mark? _openMark;
    private ReaderWord? _lookupWord;
    private WordLookupResult? _lookupEntry;
    private Footnote? _openFootnote;
    private string? _companionQuote;
    private bool _initializingControls = true;
    private bool _translationsActive;
    private (uint Section, int Sentence)? _returnFrom;
    private int _returnTurnsRemaining;
    private readonly long? _previousOpenedAtMs;
    private bool _initialOffersChecked;
    private bool _dictFullExpanded;
    private bool _dictWebReady;
    private CustomPaper? _previewPaper;
    private (uint Section, int Sentence)? _sleepArmedSpot;
    private CancellationTokenSource? _translateCts;
    private DateTime _lastInteractionUtc = DateTime.UtcNow;
    private bool _listenFlyoutOpen;
    private bool _immersivePeeking;

    // ListenCard state (1:1 MacReaderCards.swift:623-1067)
    private double _speechRate = 1.0;
    private int _sleepMode; // 0=关, -2=本句读完, -1=本章结束, 15/30/60=分钟
    private bool _loopSingleSentence;
    private int _shadowPauseMode; // 0=关, 1=2秒, 2=按句长
    private bool _listenOnly;
    private DateTime _manualPageTurnIgnoreUntilUtc = DateTime.MinValue;
    private bool _hasPausedAudioSession;
    private bool _pausedOffSpot;
    private int _lastFollowedSentence = -1;
    private bool _hasUserMoved;
    private bool _pointerInBottomPeek;
    private bool _hasActiveDoubtPill;
    private bool _marksStaleAcknowledged;
    private string? _panelBeforeSearch;
    private string? _pageHighlightQuery;
    private int _slashSelectedIndex;
    private bool _slashDismissedForCurrentToken;
    private bool _slashManualOpen;
    private CancellationTokenSource? _wordLookupAiCts;
    private string? _pairedAudioBookId;
    private AudioRecording? _pairedRecording;
    private SyncMap? _pairedMap;
    private bool _dramaSectionOnly;
    private Action? _readerToastAction;
    private DispatcherTimer? _readerToastTimer;
    private CancellationTokenSource? _companionCts;
    private (uint Section, int Sentence)? _pendingSourceHighlight;
    private RecapDto? _readyRecap;
    private (uint Section, int Sentence, int Days, long LastReadMs)? _pendingRecapOffer;
    private bool _recapLoading;
    private bool _hasLoadedInitialSection;
    private uint _partialReadSeconds;
    private uint _partialListenSeconds;
    private readonly HashSet<string> _immersiveKeepShownMarks = [];
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private bool _applyingPaperTheme;
    private bool _chromeShown;
    private DateTime _programmaticScrollUntilUtc = DateTime.MinValue;
    private int? _programmaticTargetPage;
    private Action? _capsuleAlertAction;
    private DispatcherTimer? _capsuleAlertTimer;
    private CustomPaperDialog.PaperEditorHandle? _inlinePaperEditorHandle;
    private CustomPaper? _inlineEditingExistingPaper;
    private bool _inlineEditorReturnToAll;
    private bool _castShowingUnsureInline;

    public ReaderWindow(LibraryService library, string bookId, bool autoListen = false, uint? startSection = null, int? startSentence = null, string? askQuestion = null, string? searchQuery = null)
    {
        _library = library;
        _bookId = bookId;
        _detail = _library.GetBookDetail(bookId);
        _previousOpenedAtMs = _detail.Summary.OpenedAt;
        _pendingAskQuestion = askQuestion;
        _pendingSearchQuery = searchQuery;
        if (startSection is not null || startSentence is not null)
            _hasUserMoved = true;

        lock (ActiveReaders)
        {
            ActiveReaders.Add(this);
        }
        _onFontsChanged = () => DispatcherQueue.TryEnqueue(OnFontsChanged);
        _library.Fonts.Changed += _onFontsChanged;

        var bookOwn = _library.GetBookReaderSettings(bookId);
        _ownSettings = bookOwn is not null;
        _settings = bookOwn ?? _library.GetReaderSettings();
        _pendingAutoSpeak = autoListen;

        try { _bubblesOn = _library.Core.BookBubbles(_bookId); } catch { _bubblesOn = false; }
        try { _dramaModeOn = _library.Core.DramaEnabled(_bookId); } catch { _dramaModeOn = false; }
        _translationsActive = _library.Prefs.BookTranslationsActive.TryGetValue(_bookId, out var savedTr) && savedTr;

        _audio = new AudioPlayer(_library);
        _audio.SetBookContext(_bookId, _detail.Summary.Language);
        SyncSpeedFromPrefs();

        InitializeComponent();

        Title = _detail.Summary.Title;
        BookTitleText.Text = _detail.Summary.Title;
        CompanionBookTitleText.Text = _detail.Summary.Title;

        string lang = (_detail.Summary.Language ?? "").Trim().ToLowerInvariant();
        bool isCjkLang = lang.StartsWith("zh", StringComparison.Ordinal) || lang.StartsWith("ja", StringComparison.Ordinal) || lang.StartsWith("ko", StringComparison.Ordinal);
        _isForeignBook = (!string.IsNullOrEmpty(lang) && !isCjkLang && lang != "und")
            || (string.IsNullOrEmpty(lang) && _detail.Summary.Title.All(ch => ch < 256));

        try
        {
            int maxProbe = Math.Min(12, _detail.Sections.Length);
            for (uint s = 0; s < maxProbe; s++)
            {
                if (_library.Core.BookCast(_bookId, s).Length > 0) { _hasDialogue = true; break; }
            }
        }
        catch { _hasDialogue = !_isForeignBook; }

        TranslateBtn.Visibility = _isForeignBook ? Visibility.Visible : Visibility.Collapsed;
        ScriptRow.Visibility = _isForeignBook ? Visibility.Collapsed : Visibility.Visible;
        CastBtn.Visibility = _hasDialogue ? Visibility.Visible : Visibility.Collapsed;
        BubblesRow.Visibility = _hasDialogue ? Visibility.Visible : Visibility.Collapsed;
        TypesetCastNavRowWrap.Visibility = _hasDialogue ? Visibility.Visible : Visibility.Collapsed;
        TypesetCastNavBtn.Visibility = _hasDialogue ? Visibility.Visible : Visibility.Collapsed;
        TxtLayoutBtn.Visibility = _detail.Summary.Format == BookFormat.Txt ? Visibility.Visible : Visibility.Collapsed;

        BackToShelfLabel.Text = _detail.Summary.Format == BookFormat.Podcast ? L10n.T("播客") : L10n.T("书架");
        ToolTipService.SetToolTip(BackToShelfBtn, _detail.Summary.Format == BookFormat.Podcast ? L10n.T("回到播客") : L10n.T("回到书架"));
        SelPinLabel.Text = L10n.T("对准");
        SentenceLoadingText.Text = L10n.T("正在翻译…");
        CompanionSlashHeaderLeft.Text = L10n.T("指令");
        TypesetAllPapersTitleText.Text = L10n.T("全部纸张");

        // Instant launch page screenshot preview (TECH.md §3)
        TryShowLaunchSnapshot();

        int savedW = Math.Clamp(_library.Prefs.ReaderWindowWidth > 0 ? _library.Prefs.ReaderWindowWidth : 1080, 720, 2400);
        int savedH = Math.Clamp(_library.Prefs.ReaderWindowHeight > 0 ? _library.Prefs.ReaderWindowHeight : 740, 520, 1600);
        ThemeHelper.ResizeWindow(this, savedW, savedH);
        SetTitleBar(TitleDragRegion);
        ThemeHelper.ApplyElevation(SidePanelBorder, PageShadowReceiver, 18f);

        SidePanelResizeHandle.MinWidthLimit = 286.0;
        SidePanelResizeHandle.DefaultWidth = 334.0;
        SidePanelResizeHandle.GetCurrentWidth = () => SidePanelColumn.Width.Value;
        SidePanelResizeHandle.GetMaxWidth = () => Math.Max(286.0, Math.Min(554.0, (RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1080.0) * 0.50));
        SidePanelResizeHandle.ApplyWidth = w => SidePanelColumn.Width = new GridLength(w);
        SidePanelResizeHandle.CommitWidth = w =>
        {
            _library.Prefs.ReaderPanelWidth = Math.Max(272.0, w - 14.0);
            _library.SavePrefs();
            PushSettingsToReader();
        };
        ThemeHelper.ApplyElevation(ListenCapsule, PageShadowReceiver, 16f);
        ThemeHelper.ApplyElevation(SelectionCard, PageShadowReceiver, 16f);
        ThemeHelper.ApplyElevation(WordCard, PageShadowReceiver, 16f);
        ThemeHelper.ApplyElevation(SentenceCard, PageShadowReceiver, 16f);
        ThemeHelper.ApplyElevation(FootnoteCard, PageShadowReceiver, 16f);
        ThemeHelper.ApplyElevation(RecapCardHost, PageShadowReceiver, 18f);
        ThemeHelper.ApplyElevation(ElsewhereCardHost, PageShadowReceiver, 16f);
        ThemeHelper.ApplyElevation(DoubtPillHost, PageShadowReceiver, 14f);
        ThemeHelper.ApplyElevation(SleepReturnHost, PageShadowReceiver, 14f);
        ThemeHelper.ApplyElevation(ReturnPillBtn, PageShadowReceiver, 12f);
        ThemeHelper.ApplyElevation(CapsuleAlertBanner, PageShadowReceiver, 12f);
        ThemeHelper.ApplyElevation(ReaderToastBanner, PageShadowReceiver, 14f);

        _pager = new ReaderPagerHost(WebViewSlotHost, _library, _bookId, BuildGeometry);
        _pager.MessageReceived += OnReaderMessage;

        _audio.SentenceStarted += OnAudioSentenceStarted;
        _audio.SentenceProgressSynced += OnAudioSentenceProgressSynced;
        _audio.SentenceFinished += OnAudioSentenceFinished;
        _audio.PlayStateChanged += OnAudioPlayStateChanged;
        _audio.SectionFinished += OnAudioSectionFinished;
        _audio.MarkHeardRequested += OnAudioMarkHeardRequested;
        _audio.SynthesisError += err => DispatcherQueue.TryEnqueue(() =>
            ShowReaderToast(err, "换成系统声音", () => { _audio.FallbackToSystemVoice(); ShowReaderToast("已切换为系统声音"); }));
        _audio.SleepStopped += msg => DispatcherQueue.TryEnqueue(() =>
        {
            var armed = _sleepArmedSpot ?? (_currentSection, Math.Max(0, _currentSentence));
            _sleepMode = 0;
            _sleepArmedSpot = null;
            ReaderRecapAndSleepHelper.RememberSleepStop(_library, _bookId, armed.Section, armed.Sentence);
            try { _library.AddBookmark(_bookId, _currentSection, (uint)Math.Max(0, _currentSentence)); } catch { }
            ShowReaderToast(msg);
            if (_listenFlyoutOpen) RebuildListenCard();
        });
        _audio.SleepStateChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_audio.SleepMode == 0) _sleepMode = 0;
            UpdateCapsuleText(Math.Max(0, _audio.CurrentSentence));
            if (_listenFlyoutOpen) RebuildListenCard();
        });
        _audio.PairedSentenceActive += OnPairedSentenceActive;
        _audio.ExtraNoticeChanged += (msg, actLabel, act) => DispatcherQueue.TryEnqueue(() =>
        {
            if (string.IsNullOrEmpty(msg))
            {
                HideCapsuleAlertBanner();
            }
            else
            {
                ShowCapsuleAlertBanner(msg, actLabel, act, autoHideSeconds: 6);
            }
        });

        AlignCheckAndPolishService.PolishStateChanged += OnPolishStateChanged;
        RootGrid.ActualThemeChanged += OnRootActualThemeChanged;
        _uiSettings.ColorValuesChanged += OnSystemColorValuesChanged;

        TryAttachPairedAudiobook();
        UpdateTopInfoBanners();

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); _ = RunSearchAsync(); };

        _timeTracker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timeTracker.Tick += (_, _) =>
        {
            try
            {
                if (_audio.IsPlaying)
                {
                    _partialListenSeconds += 5;
                    if (_partialListenSeconds >= 30)
                    {
                        _library.AddTime(_bookId, _currentSection, 0, _partialListenSeconds);
                        _partialListenSeconds = 0;
                    }
                }
                else if ((DateTime.UtcNow - _lastInteractionUtc).TotalMinutes <= 5.0)
                {
                    _partialReadSeconds += 5;
                    if (_partialReadSeconds >= 30)
                    {
                        _library.AddTime(_bookId, _currentSection, _partialReadSeconds, 0);
                        _partialReadSeconds = 0;
                    }
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

        if (_library.Prefs.ImmersiveAutoEnter)
            DispatcherQueue.TryEnqueue(ToggleFullScreen);

        _ = LoadInitialAsync(startSection, startSentence);
    }

    private string LaunchSnapshotPath => Path.Combine(_library.DataDir, "page-cache", $"{_bookId}.png");

    private void TryShowLaunchSnapshot()
    {
        try
        {
            string path = LaunchSnapshotPath;
            if (!File.Exists(path)) return;
            var bmp = new BitmapImage();
            using var fs = File.OpenRead(path);
            using var ras = fs.AsRandomAccessStream();
            bmp.SetSource(ras);
            LaunchSnapshotImage.Source = bmp;
            LaunchSnapshotImage.Visibility = Visibility.Visible;
        }
        catch { }
    }

    public void AskCompanion(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) return;
        OpenPanel("companion");
        CompanionInputBox.Text = question;
        _ = SubmitCompanionQuestionAsync(question);
    }

    public void HighlightSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        string trimmed = query.Trim();
        _pageHighlightQuery = trimmed;
        if (!_pager.IsInitialized) return;
        _ = HighlightPageHitsOnlyAsync(trimmed);
    }

    private async Task HighlightPageHitsOnlyAsync(string query)
    {
        try
        {
            var hits = await Task.Run(() => _library.SearchBook(_bookId, query));
            var onSection = hits.FirstOrDefault(h => h.Section == _currentSection && (int)h.Sentence >= _currentSentence)
                ?? hits.FirstOrDefault(h => h.Section == _currentSection);
            await _host.SetHitsAsync(query, onSection is not null ? (int)onSection.Sentence : null, onSection is not null ? (int)onSection.Nth : null);
        }
        catch
        {
            try { await _host.SetHitsAsync(query); } catch { }
        }
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
        if (!_ownSettings) _settings = _library.GetReaderSettings();
        PushSettingsToReader(save: false);
    }

    public void OnAppearanceChanged()
    {
        PushSettingsToReader(save: false);
    }

    public void ReloadAfterSnapshotRestore()
    {
        _settings = _library.GetReaderSettings();
        PushSettingsToReader(save: false);
        ApplyTintAndChrome();
        LoadSection(_currentSection);
    }

    public void JumpTo(uint section, int sentence, bool recordReturn = true)
    {
        _hasUserMoved = true;
        if (!_audio.IsPlaying && _hasPausedAudioSession)
            _pausedOffSpot = true;

        if (recordReturn && (section != _currentSection || Math.Abs(sentence - _currentSentence) > 2))
        {
            RecordReturnOrigin();
        }

        if (section == _currentSection)
        {
            _currentSentence = sentence;
            if (_layout is not null) _currentPage = _layout.PageOf(sentence);
            MarkProgrammaticScroll(_currentPage);
            SaveCurrentProgress();
            UpdateHeaderStatus();
            _ = _host.ScrollToSentenceAsync(sentence, animated: ShouldAnimatePageTurn());
        }
        else
        {
            _pendingSentence = sentence;
            LoadSection(section);
        }
    }

    private void RecordReturnOrigin()
    {
        _returnTurnsRemaining = 3;
        if (_returnFrom is null)
        {
            _returnFrom = (_currentSection, _currentSentence);
            ReturnPillText.Text = $"↩ 回到第 {EstimateBookPage(_currentSection, _currentSentence)} 页";
        }
        UpdateBottomChromeVisibility();
    }

    private bool ShouldAnimatePageTurn() =>
        !(_library.Prefs.GlobalEinkMode || (_isFullScreen && _library.Prefs.ImmersiveNoAnimation));

    private void ReturnPill_Click(object sender, RoutedEventArgs e)
    {
        ReturnPillBtn.Visibility = Visibility.Collapsed;
        if (_returnFrom is var (sec, sent))
        {
            _returnFrom = null;
            UpdateBottomChromeVisibility();
            JumpTo(sec, sent, recordReturn: false);
        }
    }

    public void TriggerListen()
    {
        if (!_audio.IsPlaying) StartListeningAt(_currentSentence);
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
        {
            int totalAll = Math.Max(1, _detail.Sections.Sum(s => (int)s.Sentences));
            int beforeSents = 0;
            for (uint i = 0; i < section && i < _detail.Sections.Length; i++)
                beforeSents += (int)_detail.Sections[i].Sentences;
            return Math.Clamp((double)(beforeSents + Math.Max(0, sentence)) / totalAll, 0.0, 1.0);
        }

        double before = 0;
        for (int i = 0; i < _detail.Sections.Length; i++)
        {
            var s = _detail.Sections[i];
            if (!s.Linear || s.Kind != SectionKind.Text) continue;
            if (i < (int)section)
            {
                before += s.Units;
            }
            else if (i == (int)section)
            {
                double share = Math.Clamp((double)Math.Max(0, sentence) / Math.Max(1u, s.Sentences), 0.0, 1.0);
                before += s.Units * share;
            }
        }
        return Math.Clamp(before / totalUnits, 0.0, 1.0);
    }

    private int EstimateBookPage(uint section, int sentence)
    {
        double perPage = 500.0;
        if (_currentSection < _detail.Sections.Length
            && _detail.Sections[_currentSection] is { Linear: true, Kind: SectionKind.Text, Units: > 0 } curSec
            && _layout is { PageCount: > 0 })
        {
            double measured = (double)curSec.Units / Math.Max(1, _layout.PageCount);
            if (measured > 1.0) perPage = measured;
        }

        if (_layout is { PageCount: > 0 } && section == _currentSection && sentence == _currentSentence)
        {
            int sectionStartPage = EstimateBookPageAtUnits(section, 0, perPage);
            return sectionStartPage + Math.Min(_currentPage, Math.Max(0, _layout.PageCount - 1));
        }

        return EstimateBookPageAtUnits(section, sentence, perPage);
    }

    private int EstimateBookPageAtUnits(uint section, int sentence, double perPage)
    {
        double totalUnits = 0;
        double before = 0;
        for (int i = 0; i < _detail.Sections.Length; i++)
        {
            var s = _detail.Sections[i];
            if (!s.Linear || s.Kind != SectionKind.Text) continue;
            totalUnits += s.Units;
            if (i < (int)section)
            {
                before += s.Units;
            }
            else if (i == (int)section)
            {
                double share = Math.Clamp((double)Math.Max(0, sentence) / Math.Max(1u, s.Sentences), 0.0, 1.0);
                before += s.Units * share;
            }
        }

        if (totalUnits <= 0)
        {
            int totalSentencesBefore = 0;
            for (uint i = 0; i < section && i < _detail.Sections.Length; i++)
                totalSentencesBefore += (int)_detail.Sections[i].Sentences;
            int absSent = totalSentencesBefore + Math.Max(0, sentence);
            return Math.Max(1, absSent / 12 + 1);
        }

        return Math.Max(1, (int)Math.Floor(before / Math.Max(1.0, perPage)) + 1);
    }

    private int EstimateBookPercent(uint section, int sentence)
    {
        double frac = CalculateBookFraction(section, sentence);
        if (frac <= 0 && section == 0 && sentence <= 0) return 0;
        return Math.Clamp((int)Math.Round(frac * 100.0), 1, 100);
    }

    private void OnPolishStateChanged(string polishedBookId)
    {
        if (polishedBookId != _bookId) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTopInfoBanners();
            if (_pager.IsInitialized && _hasLoadedInitialSection && !AlignCheckAndPolishService.GetPolishState(_bookId).Running)
            {
                _pendingSentence = _currentSentence;
                LoadSection(_currentSection, forceReload: true);
            }
        });
    }

    private void OnRootActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_applyingPaperTheme) return;
        if (_library.Prefs.PaperFollowAppearance)
            PushSettingsToReader(save: false);
        else
            ApplyTintAndChrome();
    }

    private void OnSystemColorValuesChanged(Windows.UI.ViewManagement.UISettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_library.Prefs.PaperFollowAppearance && _library.Prefs.ToElementTheme() == ElementTheme.Default)
                PushSettingsToReader(save: false);
        });
    }

    private void ShowCapsuleAlertBanner(string message, string? actionLabel = null, Action? action = null, int autoHideSeconds = 6)
    {
        _capsuleAlertTimer?.Stop();
        _capsuleAlertTimer = null;
        _capsuleAlertAction = action;
        CapsuleAlertText.Text = message;
        if (!string.IsNullOrEmpty(actionLabel) && action is not null)
        {
            CapsuleAlertActionBtn.Content = actionLabel;
            CapsuleAlertActionBtn.Visibility = Visibility.Visible;
        }
        else
        {
            CapsuleAlertActionBtn.Visibility = Visibility.Collapsed;
        }
        CapsuleAlertBanner.Visibility = Visibility.Visible;

        if (autoHideSeconds > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoHideSeconds) };
            _capsuleAlertTimer = timer;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (ReferenceEquals(_capsuleAlertTimer, timer))
                {
                    _capsuleAlertTimer = null;
                    HideCapsuleAlertBanner();
                }
            };
            timer.Start();
        }
    }

    private void HideCapsuleAlertBanner()
    {
        _capsuleAlertTimer?.Stop();
        _capsuleAlertTimer = null;
        _capsuleAlertAction = null;
        CapsuleAlertBanner.Visibility = Visibility.Collapsed;
    }

    private void CapsuleAlertAction_Click(object sender, RoutedEventArgs e)
    {
        var act = _capsuleAlertAction;
        HideCapsuleAlertBanner();
        act?.Invoke();
    }

    public void PreviewCustomPaper(CustomPaper? draft)
    {
        _previewPaper = draft;
        PushSettingsToReader(save: false);
    }

    public void ApplyTintAndChrome()
    {
        var resolved = _previewPaper is not null
            ? new PaperAppearance.Resolved(YuyueCoreMethods.PaperColors(_previewPaper).Preset, _previewPaper)
            : PaperAppearance.Resolve(_library, _settings);
        CustomPaper? custom = resolved.Custom;

        bool einkOn = _library.Prefs.GlobalEinkMode;
        var paper = YYPapers.FromId(PaperCssId(resolved.Preset));
        bool isDark = einkOn ? false : paper.IsDark;
        Color bgCol = einkOn ? Colors.White : paper.Bg;
        Color inkCol = einkOn ? Colors.Black : paper.Ink;
        Color ink2Col = einkOn ? Color.FromArgb(255, 72, 72, 72) : paper.Ink2;
        Color glassCol = einkOn ? Colors.White : paper.Glass;
        Color lineCol = einkOn ? Color.FromArgb(255, 200, 200, 200) : paper.Line;

        if (!einkOn && custom is not null)
        {
            try
            {
                var pc = YuyueCoreMethods.PaperColors(custom);
                isDark = pc.Dark;
                bgCol = CustomPaperDialog.ParseHexColor(pc.Bg, paper.Bg);
                inkCol = CustomPaperDialog.ParseHexColor(pc.Ink, paper.Ink);
                ink2Col = CustomPaperDialog.ParseHexColor(pc.Ink2, paper.Ink2);
                lineCol = CustomPaperDialog.ParseHexColor(pc.Line, paper.Line);
                glassCol = CustomPaperDialog.ParseHexColor(pc.Glass, isDark ? Color.FromArgb(215, 28, 29, 32) : Color.FromArgb(224, 255, 255, 255));
            }
            catch { }
        }

        var tint = _library.Prefs.Tint;
        var tintColors = ThemeHelper.TintColors(tint, isDark);
        var accent = isDark ? tint.PaperAccentDark : tint.PaperAccentLight;
        var uiPalette = ThemeHelper.Palette(isDark);

        _applyingPaperTheme = true;
        try
        {
            RootGrid.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        }
        finally
        {
            _applyingPaperTheme = false;
        }
        ThemeHelper.ApplyControlThemeResources(RootGrid.Resources, isDark, tint);
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

        ScrollEdgeCue.Background = glassBrush;
        ScrollEdgeCue.BorderBrush = lineBrush;
        ScrollEdgeCueText.Foreground = inkBrush;

        ReturnPillBtn.Background = glassBrush;
        ReturnPillBtn.BorderBrush = lineBrush;
        ReturnPillText.Foreground = inkBrush;

        CapsuleAlertBanner.Background = glassBrush;
        CapsuleAlertBanner.BorderBrush = lineBrush;
        CapsuleAlertText.Foreground = inkBrush;
        CapsuleAlertActionBtn.Foreground = accentBrush;

        TextVersionBanner.Background = ThemeHelper.Brush(Color.FromArgb(140, lineCol.R, lineCol.G, lineCol.B));
        TextVersionBanner.BorderBrush = lineBrush;
        TextVersionBannerText.Foreground = ink2Brush;

        BackToShelfBtn.Background = glassBrush;
        BackToShelfBtn.BorderBrush = lineBrush;
        BackToShelfBtn.Foreground = inkBrush;
        BackChevronIcon.Content = YYIcons.Make("chevron-left", 11.5, inkBrush);
        BackToShelfLabel.Text = _detail.Summary.Format == BookFormat.Podcast ? L10n.T("播客") : L10n.T("书架");

        RecapTopPillBtn.Background = glassBrush;
        RecapTopPillBtn.BorderBrush = lineBrush;
        RecapTopPillBtn.Foreground = inkBrush;
        RecapTopIconHost.Content = YYIcons.Make("clock-history", 12, accentBrush);
        RecapTopIconHost.Visibility = _recapLoading ? Visibility.Collapsed : Visibility.Visible;
        RecapTopSpinner.Visibility = _recapLoading ? Visibility.Visible : Visibility.Collapsed;
        RecapTopSpinner.IsActive = _recapLoading;

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
        CapsuleTocBtn.Content = YYIcons.Make("list", 13, inkBrush);
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

        // SelectionCard & WordCard & Companion icons & composer
        var t1Brush = ThemeHelper.Brush(uiPalette.T1);
        var t3Brush = ThemeHelper.Brush(uiPalette.T3);
        SelectionNotePencilIcon.Content = YYIcons.Make("pencil", 11.5, t3Brush);
        SelDeleteIcon.Content = YYIcons.Make("trash", 12.5, ThemeHelper.Brush(uiPalette.Danger));
        SelThoughtIcon.Content = YYIcons.Make("pencil", 14, t1Brush);
        SelSpeakIcon.Content = YYIcons.Make("speaker", 14, t1Brush);
        SelPinIcon.Content = YYIcons.Make("wave", 14, t1Brush);
        SelAskIcon.Content = YYIcons.Make("sparkle", 14, accentBrush);
        SelTransIcon.Content = YYIcons.Make("a-wen", 14, t1Brush);
        SelCopyIcon.Content = YYIcons.Make("copy", 13.5, t1Brush);
        SpeakWordBtn.Content = YYIcons.Make("headphones", 13, t1Brush);
        CompanionBadgeIcon.Content = YYIcons.Make("sparkle", 12.5, accentBrush);
        CompanionMoreIcon.Content = YYIcons.Make("more", 12, t1Brush);
        CompanionComposerBorder.Background = ThemeHelper.Brush(bgCol);
        CompanionComposerBorder.BorderBrush = lineBrush;
        UpdateCompanionSendButtonVisuals(tintColors);
        TypesetCastIconHost.Content = YYIcons.Make("cast", 12.5, ThemeHelper.Brush(uiPalette.T2));

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
        L10n.Skip(BookTitleText);
        L10n.Skip(SubtitleText);
        L10n.Skip(ListenOnlyTitleText);
        L10n.ApplyTree(RootGrid);
    }

    private void UpdateCompanionSendButtonVisuals(YYTintColors? tintOverride = null)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var tc = tintOverride ?? ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        bool streaming = _companionCts is not null;
        bool hasText = !string.IsNullOrWhiteSpace(CompanionInputBox?.Text);
        CompanionSendBtn.Background = ThemeHelper.Brush(tc.Black);
        CompanionSendBtn.Foreground = ThemeHelper.Brush(tc.OnBlack);
        CompanionSendBtn.Opacity = (streaming || hasText) ? 1.0 : 0.32;
    }

    private async Task LoadInitialAsync(uint? startSection, int? startSentence)
    {
        try
        {
            await _pager.InitializeAsync();
        }
        catch (Exception ex)
        {
            App.AppendCrashLog(_library.DataDir, "WEBVIEW2_INIT", $"Failed to initialize WebView2 for book {_bookId}", ex);
            ShowWebView2UnavailableFallback(ex, startSection, startSentence);
            return;
        }
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
        UpdateHeaderStatus();
        _pager.CurrentWebView?.Focus(FocusState.Programmatic);

        if (!string.IsNullOrWhiteSpace(_pendingAskQuestion))
        {
            string q = _pendingAskQuestion;
            _pendingAskQuestion = null;
            AskCompanion(q);
        }
        if (!string.IsNullOrWhiteSpace(_pendingSearchQuery))
        {
            string sq = _pendingSearchQuery;
            _pendingSearchQuery = null;
            HighlightSearch(sq);
        }
        else if (string.IsNullOrWhiteSpace(_pendingAskQuestion)
            && !_isFullScreen
            && _library.Prefs.ReaderLastPanel is "toc" or "notes" or "companion" or "cast"
            && (_library.Prefs.ReaderLastPanel != "cast" || _hasDialogue))
        {
            OpenPanel(_library.Prefs.ReaderLastPanel);
        }
    }

    private void ShowWebView2UnavailableFallback(Exception ex, uint? startSection, int? startSentence)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        bool missingRuntime = !ReaderWebViewHost.IsWebView2RuntimeAvailable(out _);

        WebViewSlotHost.Children.Clear();
        var stack = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(24),
        };
        stack.Children.Add(new TextBlock
        {
            Text = missingRuntime ? "需要安装 WebView2 运行时" : "书页组件暂时无法启动",
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = missingRuntime
                ? "语阅电子书阅读器需要 Microsoft Edge WebView2 运行时（Windows 11 默认自带，Windows 10 部分版本需手动安装一次）。"
                : $"WebView2 初始化失败：{ex.Message}",
            FontSize = 12.5,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var dlBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
            Content = "下载 WebView2 运行时 ↗",
        };
        dlBtn.Click += (_, _) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(ReaderWebViewHost.WebView2DownloadUrl));
        var retryBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
            Content = "重试",
        };
        retryBtn.Click += (_, _) =>
        {
            WebViewSlotHost.Children.Clear();
            _ = LoadInitialAsync(startSection, startSentence);
        };
        actions.Children.Add(dlBtn);
        actions.Children.Add(retryBtn);
        stack.Children.Add(actions);

        WebViewSlotHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
        });
    }

    // 1:1 MacReaderView.swift：书页占满窗口全高，Top 留出 64px 悬浮顶栏安全区，Bottom 留出 88px 听书胶囊安全区
    private PageGeometry BuildGeometry()
    {
        double panelW = _activePanel is not null
            ? (SidePanelColumn.Width.Value > 0 ? SidePanelColumn.Width.Value : (_activePanel == "companion" ? 358 : 322))
            : 0;
        double width = WebViewContainer.ActualWidth > 0
            ? WebViewContainer.ActualWidth
            : Math.Max(480, (RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1080) - panelW);
        byte cols = (_detail?.Summary.Format != BookFormat.Podcast && _settings.Mode == LayoutMode.Paginated && _library.Prefs.TwoColumns && width >= 840) ? (byte)2 : (byte)1;
        double measure = _settings.FontSize * 31.0;
        double margin = Math.Round(Math.Max(width < 500 ? 24 : 40, (width / cols - measure) / 2.0));
        return new PageGeometry(
            Paging: Paging.Snap,
            Margin: margin,
            Top: _isFullScreen ? 40 : 64,
            Bottom: 88,
            Columns: cols,
            ReducedMotion: !ShouldAnimatePageTurn());
    }

    private void LoadSection(uint section, bool forceReload = false)
    {
        _currentSection = section;
        _currentPage = 0;
        _layout = null;
        _sectionSentenceCount = section < _detail.Sections.Length ? (int)_detail.Sections[section].Sentences : 0;
        DismissFloatingCards();
        UpdateHeaderStatus();
        _pager.OpenSection(section, forceReload, joinAtEnd: _pendingEndOfSection);
        if (_activePanel is not null) DispatcherQueue.TryEnqueue(RenderSidePanel);
    }

    // ---------- TypesetCard (1:1 MacReaderCards.swift:65-430 & CustomPapers.swift:470-713) ----------

    private void TypesetFlyout_Opened(object sender, object e)
    {
        _typesetOpen = true;
        _inlinePaperEditorHandle = null;
        _inlineEditingExistingPaper = null;
        TypesetMorePanel.Visibility = Visibility.Collapsed;
        TypesetPaperEditorPanel.Visibility = Visibility.Collapsed;
        TypesetAllPapersPanel.Visibility = Visibility.Collapsed;
        TypesetMainPanel.Visibility = Visibility.Visible;
        ApplyTypesetControlsFromSettings();
        ApplyTintAndChrome();
    }

    private void TypesetFlyout_Closed(object sender, object e)
    {
        _typesetOpen = false;
        _inlinePaperEditorHandle = null;
        _inlineEditingExistingPaper = null;
        if (_previewPaper is not null)
        {
            _previewPaper = null;
            PushSettingsToReader(save: false);
            return;
        }
        ApplyTintAndChrome();
    }

    private void ApplyTypesetControlsFromSettings()
    {
        _initializingControls = true;
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var activeBg = ThemeHelper.Brush(palette.Card);
        var clear = new SolidColorBrush(Colors.Transparent);

        var resolved = PaperAppearance.Resolve(_library, _settings);
        FollowAppearanceToggle.IsOn = _library.Prefs.PaperFollowAppearance;

        bool usingCustom = resolved.Custom is not null;
        (ContentControl Host, TextBlock Label, Paper Kind, YYPaper Spec, bool Textured)[] paperSpecs =
        [
            (PaperPlainHost, PaperPlainLabel, Paper.Plain, YYPapers.Plain, false),
            (PaperDarkHost, PaperDarkLabel, Paper.Dark, YYPapers.Dark, false),
            (PaperKamiHost, PaperKamiLabel, Paper.Kami, YYPapers.Kami, false),
            (PaperKamiNightHost, PaperKamiNightLabel, Paper.KamiNight, YYPapers.Kaminight, false),
            (PaperSageHost, PaperSageLabel, Paper.Sage, YYPapers.Sage, false),
            (PaperEinkHost, PaperEinkLabel, Paper.Eink, YYPapers.Eink, true),
        ];
        foreach (var (host, lbl, kind, spec, textured) in paperSpecs)
        {
            bool on = !usingCustom && resolved.Preset == kind;
            host.Content = CustomPaperDialog.BuildPaperSwatchTile(
                spec.Bg,
                spec.Ink,
                kind == Paper.Eink ? spec.Ink : spec.Line,
                tint.Accent,
                selected: on,
                textured: textured,
                width: 46,
                height: 38,
                fontSize: 15);
            lbl.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            lbl.Foreground = ThemeHelper.Brush(on ? palette.T1 : palette.T2);
        }

        RebuildCustomPapersMainHost(palette, tint);
        bool einkLocked = _library.Prefs.GlobalEinkMode;
        EinkPaperLockNote.Visibility = einkLocked ? Visibility.Visible : Visibility.Collapsed;
        FollowAppearanceRow.Opacity = einkLocked ? 0.45 : 1.0;
        FollowAppearanceToggle.IsEnabled = !einkLocked;
        PaperSwatchesGrid.Opacity = einkLocked ? 0.45 : 1.0;
        PaperSwatchesGrid.IsHitTestVisible = !einkLocked;
        CustomPapersMainHost.Opacity = einkLocked ? 0.45 : 1.0;
        CustomPapersMainHost.IsHitTestVisible = !einkLocked;
        RebuildFontTilesHost(palette, tint);

        FontSizeValueText.Text = $"{(int)Math.Round(_settings.FontSize)}";
        FontSizeSlider.Value = Math.Clamp(Math.Round(_settings.FontSize), 12, 34);

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

    private void OpenInlinePaperEditor(CustomPaper? existing = null, bool returnToAll = false)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var initial = existing ?? CustomPaperDialog.NewDraftFromSettings(_library, _settings);
        _inlineEditingExistingPaper = existing;
        _inlineEditorReturnToAll = returnToAll;

        TypesetEditorTitleText.Text = existing is null || string.IsNullOrEmpty(existing.Id)
            ? L10n.T("调纸")
            : $"{L10n.T("调整")}“{existing.Name}”";

        _inlinePaperEditorHandle = CustomPaperDialog.BuildPaperEditorBody(
            _library,
            dark,
            initial,
            showReadingSample: false,
            onDraftChanged: draft => PreviewCustomPaper(draft),
            onDuplicatePicked: dup =>
            {
                _previewPaper = null;
                bool save = PaperAppearance.PickCustom(_library, dup, _settings, out var updated);
                _settings = updated;
                PushSettingsToReader(save: save);
                CloseInlinePaperEditor(saved: true);
            },
            onDeleteRequested: () =>
            {
                if (existing is not null && !string.IsNullOrEmpty(existing.Id))
                {
                    _previewPaper = null;
                    DeleteCustomPaperWithUndo(existing);
                    CloseInlinePaperEditor(saved: true);
                }
            });

        TypesetPaperEditorHost.Content = _inlinePaperEditorHandle.Root;
        TypesetMainPanel.Visibility = Visibility.Collapsed;
        TypesetMorePanel.Visibility = Visibility.Collapsed;
        TypesetAllPapersPanel.Visibility = Visibility.Collapsed;
        TypesetPaperEditorPanel.Visibility = Visibility.Visible;
        YYChrome.AnimateSlideX(TypesetPaperEditorPanel, fromX: 16, durationMs: 160);
    }

    private void CloseInlinePaperEditor(bool saved)
    {
        if (!saved && _previewPaper is not null)
        {
            _previewPaper = null;
            PushSettingsToReader(save: false);
        }
        _inlinePaperEditorHandle = null;
        _inlineEditingExistingPaper = null;
        TypesetPaperEditorPanel.Visibility = Visibility.Collapsed;

        if (_inlineEditorReturnToAll && _library.Core.CustomPapers().Length > 0)
        {
            OpenInlineAllPapersPanel();
        }
        else
        {
            TypesetMainPanel.Visibility = Visibility.Visible;
            ApplyTypesetControlsFromSettings();
            YYChrome.AnimateSlideX(TypesetMainPanel, fromX: -14, durationMs: 150);
        }
    }

    private void TypesetPaperEditorCancel_Click(object sender, RoutedEventArgs e) =>
        CloseInlinePaperEditor(saved: false);

    private void TypesetPaperEditorSave_Click(object sender, RoutedEventArgs e)
    {
        if (_inlinePaperEditorHandle is null)
        {
            CloseInlinePaperEditor(saved: false);
            return;
        }
        try
        {
            var draft = _inlinePaperEditorHandle.GetDraft();
            _previewPaper = null;
            var saved = _library.Core.SaveCustomPaper(draft);
            bool save = PaperAppearance.PickCustom(_library, saved, _settings, out var updated);
            _settings = updated;
            PushSettingsToReader(save: save);
        }
        catch { _previewPaper = null; }
        CloseInlinePaperEditor(saved: true);
    }

    private void OpenInlineAllPapersPanel()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        CustomPaper[] all;
        try { all = _library.Core.CustomPapers(); } catch { all = []; }
        TypesetAllPapersTitleText.Text = $"{L10n.T("全部纸张")} ({all.Length})";
        TypesetAllPapersListHost.Children.Clear();

        var resolved = PaperAppearance.Resolve(_library, _settings);
        foreach (var cp in all)
        {
            var item = cp;
            var pc = YuyueCoreMethods.PaperColors(item);
            bool on = resolved.Custom?.Id == item.Id;

            var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(8, 7, 8, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tile = CustomPaperDialog.BuildPaperSwatchTile(
                CustomPaperDialog.ParseHexColor(pc.Bg, Colors.Beige),
                CustomPaperDialog.ParseHexColor(pc.Ink, Colors.Black),
                p.Line,
                tint.Accent,
                selected: on,
                textured: item.Texture != PaperTexture.None,
                width: 38,
                height: 30,
                fontSize: 13);
            row.Children.Add(tile);

            var titleBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = new TextBlock
                {
                    Text = item.Name,
                    FontSize = 12.5,
                    FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T1),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            titleBtn.Click += (_, _) =>
            {
                bool save = PaperAppearance.PickCustom(_library, item, _settings, out var updated);
                _settings = updated;
                PushSettingsToReader(save: save);
                OpenInlineAllPapersPanel();
            };
            Grid.SetColumn(titleBtn, 1);
            row.Children.Add(titleBtn);

            var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var edBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = L10n.T("调纸"), FontSize = 10.5, Height = 24, Padding = new Thickness(6, 0, 6, 0) };
            edBtn.Click += (_, _) => OpenInlinePaperEditor(item, returnToAll: true);
            var rmBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "×", FontSize = 12, Width = 24, Height = 24, Padding = new Thickness(0), Foreground = ThemeHelper.Brush(p.Danger) };
            rmBtn.Click += (_, _) =>
            {
                DeleteCustomPaperWithUndo(item);
                if (_library.Core.CustomPapers().Length > 0)
                    OpenInlineAllPapersPanel();
                else
                {
                    TypesetAllPapersPanel.Visibility = Visibility.Collapsed;
                    TypesetMainPanel.Visibility = Visibility.Visible;
                    ApplyTypesetControlsFromSettings();
                }
            };
            acts.Children.Add(edBtn);
            acts.Children.Add(rmBtn);
            Grid.SetColumn(acts, 2);
            row.Children.Add(acts);

            TypesetAllPapersListHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Child = row,
            });
        }

        TypesetMainPanel.Visibility = Visibility.Collapsed;
        TypesetMorePanel.Visibility = Visibility.Collapsed;
        TypesetPaperEditorPanel.Visibility = Visibility.Collapsed;
        TypesetAllPapersPanel.Visibility = Visibility.Visible;
        YYChrome.AnimateSlideX(TypesetAllPapersPanel, fromX: 16, durationMs: 160);
    }

    private void TypesetAllPapersNew_Click(object sender, RoutedEventArgs e) =>
        OpenInlinePaperEditor(existing: null, returnToAll: true);

    private void RebuildCustomPapersMainHost(YYPalette p, YYTintColors tint)
    {
        CustomPapersMainHost.Children.Clear();
        CustomPaper[] list;
        try { list = _library.Core.CustomPapers(); }
        catch { list = []; }

        var head = new Grid { Height = 24 };
        head.Children.Add(new TextBlock
        {
            Text = L10n.T("我的纸"),
            FontSize = 11.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T2),
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (list.Length == 0)
        {
            var tuneBtn = new Button
            {
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 0, 0, 0),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = ThemeHelper.Brush(tint.Accent),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                UseSystemFocusVisuals = false,
                Content = $"＋ {L10n.T("自己调一张")}",
            };
            tuneBtn.Click += (_, _) => OpenInlinePaperEditor(existing: null, returnToAll: false);
            head.Children.Add(tuneBtn);
            CustomPapersMainHost.Children.Add(head);
            return;
        }

        var rightHeaderSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (list.Length > 4)
        {
            var allBtn = new Button
            {
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 0, 0, 0),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                UseSystemFocusVisuals = false,
                Content = $"{L10n.T("全部")} {list.Length} ›",
            };
            allBtn.Click += (_, _) => OpenInlineAllPapersPanel();
            rightHeaderSp.Children.Add(allBtn);
        }
        var addLinkBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0, 0, 0),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = ThemeHelper.Brush(tint.Accent),
            UseSystemFocusVisuals = false,
            Content = $"＋ {L10n.T("自己调一张")}",
        };
        addLinkBtn.Click += (_, _) => OpenInlinePaperEditor(existing: null, returnToAll: false);
        rightHeaderSp.Children.Add(addLinkBtn);
        head.Children.Add(rightHeaderSp);
        CustomPapersMainHost.Children.Add(head);

        var resolvedPaper = PaperAppearance.Resolve(_library, _settings);
        var shown = list.Take(9).ToArray();
        int activeIdx = resolvedPaper.Custom is { } activeCustom
            ? Array.FindIndex(list, x => x.Id == activeCustom.Id)
            : -1;
        if (activeIdx >= 9 && shown.Length == 9)
            shown[8] = list[activeIdx];

        int rows = (shown.Length + 4) / 5;
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (int c = 0; c < 5; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < shown.Length; i++)
        {
            var cp = shown[i];
            var pc = YuyueCoreMethods.PaperColors(cp);
            bool on = resolvedPaper.Custom?.Id == cp.Id;
            var swatch = CustomPaperDialog.BuildPaperSwatchTile(
                CustomPaperDialog.ParseHexColor(pc.Bg, Colors.Beige),
                CustomPaperDialog.ParseHexColor(pc.Ink, Colors.Black),
                p.Line,
                tint.Accent,
                selected: on,
                textured: cp.Texture != PaperTexture.None,
                width: 46,
                height: 38,
                fontSize: 15);
            var sp = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(swatch);
            sp.Children.Add(new TextBlock
            {
                Text = cp.Name,
                FontSize = 10,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var ctx = new MenuFlyout();
            var editMi = new MenuFlyoutItem { Text = L10n.T("调整颜色…") };
            editMi.Click += (_, _) => OpenInlinePaperEditor(cp, returnToAll: false);
            var renameMi = new MenuFlyoutItem { Text = L10n.T("重命名…") };
            renameMi.Click += async (_, _) =>
            {
                TypesetBtn.Flyout?.Hide();
                await PromptRenameCustomPaperAsync(cp);
            };
            var dupMi = new MenuFlyoutItem { Text = L10n.T("复制一张") };
            dupMi.Click += (_, _) =>
            {
                var copy = CustomPaperDialog.DuplicateCustomPaper(_library, cp);
                if (copy is not null)
                {
                    bool save = PaperAppearance.PickCustom(_library, copy, _settings, out var updated);
                    _settings = updated;
                    PushSettingsToReader(save: save);
                }
            };
            var delMi = new MenuFlyoutItem { Text = L10n.T("删除…"), Foreground = ThemeHelper.Brush(p.Danger) };
            delMi.Click += async (_, _) =>
            {
                TypesetBtn.Flyout?.Hide();
                await ConfirmAndDeleteCustomPaperAsync(cp);
            };
            ctx.Items.Add(editMi);
            ctx.Items.Add(renameMi);
            ctx.Items.Add(dupMi);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(delMi);

            var b = new Button { Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, UseSystemFocusVisuals = false, Content = sp, ContextFlyout = ctx };
            b.Click += (_, _) =>
            {
                bool save = PaperAppearance.PickCustom(_library, cp, _settings, out var updated);
                _settings = updated;
                PushSettingsToReader(save: save);
            };
            Grid.SetRow(b, i / 5);
            Grid.SetColumn(b, i % 5);
            grid.Children.Add(b);
        }

        CustomPapersMainHost.Children.Add(grid);
    }

    private void DeleteCustomPaperWithUndo(CustomPaper cp)
    {
        string delId = cp.Id;
        string delName = cp.Name;
        var resolvedBefore = PaperAppearance.Resolve(_library, _settings);
        bool wasActive = resolvedBefore.Custom?.Id == delId || _settings.CustomPaper == delId;
        bool save = false;
        try { _library.Core.DeleteCustomPaper(delId); } catch { }
        PaperAppearance.Forget(_library, delId);
        if (_settings.CustomPaper == delId)
        {
            _settings = _settings with { CustomPaper = null };
            save = true;
        }
        PushSettingsToReader(save: save);
        ShowReaderToast($"{L10n.T("已删掉自定义纸张")}“{delName}”", L10n.T("撤销"), () =>
        {
            try
            {
                _library.Core.RestoreCustomPaper(delId);
                if (wasActive)
                {
                    var restored = _library.Core.CustomPapers().FirstOrDefault(x => x.Id == delId);
                    if (restored is not null)
                    {
                        bool restoreSave = PaperAppearance.PickCustom(_library, restored, _settings, out var updated);
                        _settings = updated;
                        PushSettingsToReader(save: restoreSave);
                        return;
                    }
                }
                PushSettingsToReader(save: false);
            }
            catch { }
        });
    }

    private async Task<bool> PromptRenameCustomPaperAsync(CustomPaper cp)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        string? trimmed = await YYChrome.ShowPromptAsync(
            RootGrid.XamlRoot,
            dark,
            L10n.T("重命名自定义纸张"),
            L10n.T("输入纸张名称"),
            cp.Name,
            L10n.T("保存"),
            L10n.T("取消"),
            tint: _library.Prefs.Tint);
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Trim() == cp.Name)
            return false;

        string clean = trimmed.Trim();
        try
        {
            var renameMethod = _library.Core.GetType().GetMethod("CustomPaperRename", [typeof(string), typeof(string)]);
            if (renameMethod is not null)
                renameMethod.Invoke(_library.Core, [cp.Id, clean]);
            else
                _library.Core.SaveCustomPaper(cp with { Name = clean });
            PushSettingsToReader(save: false);
            return true;
        }
        catch (Exception ex)
        {
            ShowReaderToast($"{L10n.T("重命名失败：")}{ex.Message}");
            return false;
        }
    }

    private async Task<bool> ConfirmAndDeleteCustomPaperAsync(CustomPaper cp)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        bool confirmed = await YYChrome.ShowConfirmAsync(
            RootGrid.XamlRoot,
            dark,
            $"{L10n.T("删除纸张")}“{cp.Name}”？",
            L10n.T("删除后使用这张纸的书籍会恢复为预设纸色。"),
            L10n.T("删除"),
            L10n.T("取消"),
            destructive: true,
            tint: _library.Prefs.Tint);
        if (!confirmed)
            return false;

        DeleteCustomPaperWithUndo(cp);
        return true;
    }

    private Task ShowAllCustomPapersDialogAsync()
    {
        OpenInlineAllPapersPanel();
        return Task.CompletedTask;
    }

    private void OnFontsChanged()
    {
        ApplyTypesetControlsFromSettings();
        if (_pager.IsInitialized && _hasLoadedInitialSection && (!string.IsNullOrEmpty(_settings.FontFace) || _settings.Font == Font.WenKai))
        {
            _pendingSentence = _currentSentence;
            LoadSection(_currentSection, forceReload: true);
        }
    }

    private static Font FallbackFontForKind(FontKind kind) => kind switch
    {
        FontKind.Song or FontKind.Fang => Font.Serif,
        FontKind.Kai or FontKind.Hand => Font.Kai,
        FontKind.Hei => Font.Sans,
        _ => Font.Serif,
    };

    private static string FallbackWindowsFamilyForKind(FontKind kind) => kind switch
    {
        FontKind.Kai or FontKind.Hand => YYFontFamily.Kaiti.WindowsFamily(),
        FontKind.Hei => YYFontFamily.Sans.WindowsFamily(),
        _ => YYFontFamily.Serif.WindowsFamily(),
    };

    private static FontKind InferFontKindFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return FontKind.Song;
        if (name.Contains("仿", StringComparison.OrdinalIgnoreCase) || name.Contains("Fang", StringComparison.OrdinalIgnoreCase))
            return FontKind.Fang;
        if (name.Contains("楷", StringComparison.OrdinalIgnoreCase) || name.Contains("Kai", StringComparison.OrdinalIgnoreCase) || name.Contains("WenKai", StringComparison.OrdinalIgnoreCase))
            return FontKind.Kai;
        if (name.Contains("手", StringComparison.OrdinalIgnoreCase) || name.Contains("行", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("草", StringComparison.OrdinalIgnoreCase) || name.Contains("隶", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("LiSu", StringComparison.OrdinalIgnoreCase))
            return FontKind.Hand;
        if (name.Contains("黑", StringComparison.OrdinalIgnoreCase) || name.Contains("Hei", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Sans", StringComparison.OrdinalIgnoreCase) || name.Contains("YaHei", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("雅黑", StringComparison.OrdinalIgnoreCase) || name.Contains("PingFang", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("苹方", StringComparison.OrdinalIgnoreCase) || name.Contains("MiSans", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Harmony", StringComparison.OrdinalIgnoreCase) || name.Contains("Sarasa", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Gothic", StringComparison.OrdinalIgnoreCase) || name.Contains("圆", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("等线", StringComparison.OrdinalIgnoreCase) || name.Contains("DengXian", StringComparison.OrdinalIgnoreCase))
            return FontKind.Hei;
        return FontKind.Song;
    }

    // 1:1 MacFonts.swift:63-170：4 列紧凑字样格（系统 / 宋体 / 楷体 / 霞鹜文楷 + 已固定字体 + 更多字样册）
    private void RebuildFontTilesHost(YYPalette p, YYTintColors tint)
    {
        FontTilesHost.Children.Clear();
        FontTilesHost.ColumnDefinitions.Clear();
        FontTilesHost.RowDefinitions.Clear();
        for (int c = 0; c < 4; c++) FontTilesHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        bool wenkaiInstalled = _library.Fonts.IsPackInstalled("wenkai");
        bool wenkaiDownloading = _library.Fonts.IsDownloading("wenkai");
        var resolvedPreset = PaperAppearance.Resolve(_library, _settings).Preset;
        bool defaultIsSans = resolvedPreset is Paper.Plain or Paper.Dark;

        var tiles = new List<(string Key, string? PinId, string Label, Font TargetFont, string? TargetFace, string SampleFont, bool Active)>
        {
            (
                "system",
                null,
                "系统",
                Font.Sans,
                null,
                YYFontFamily.Sans.WindowsFamily(),
                string.IsNullOrEmpty(_settings.FontFace) && (_settings.Font == Font.Sans || (_settings.Font is null && defaultIsSans))
            ),
            (
                "serif",
                null,
                "宋体",
                Font.Serif,
                null,
                YYFontFamily.Serif.WindowsFamily(),
                string.IsNullOrEmpty(_settings.FontFace) && (_settings.Font == Font.Serif || (_settings.Font is null && !defaultIsSans))
            ),
            (
                "kai",
                null,
                "楷体",
                Font.Kai,
                null,
                YYFontFamily.Kaiti.WindowsFamily(),
                string.IsNullOrEmpty(_settings.FontFace) && _settings.Font == Font.Kai
            ),
            (
                "wenkai",
                null,
                wenkaiDownloading ? "下载中…" : (wenkaiInstalled ? "霞鹜文楷" : "文楷 ↓"),
                Font.WenKai,
                null,
                _library.Fonts.ResolveXamlFontSource("wenkai", YYFontFamily.Wenkai.WindowsFamily()),
                string.IsNullOrEmpty(_settings.FontFace) && _settings.Font == Font.WenKai
            ),
        };

        foreach (string rawPin in _library.Prefs.PinnedFontIds)
        {
            if (string.IsNullOrWhiteSpace(rawPin) || rawPin == "wenkai") continue;
            if (tiles.Any(x => string.Equals(x.PinId, rawPin, StringComparison.OrdinalIgnoreCase))) continue;

            if (rawPin.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                string fn = rawPin[5..];
                string disp = _library.Fonts.GetFontFileDisplayName(fn);
                var kind = InferFontKindFromName($"{disp} {fn}");
                string fam = _library.Fonts.ResolveXamlFontSource(rawPin, FallbackWindowsFamilyForKind(kind));
                bool on = string.Equals(_settings.FontFace, rawPin, StringComparison.OrdinalIgnoreCase);
                tiles.Add((rawPin, rawPin, disp, FallbackFontForKind(kind), rawPin, fam, on));
            }
            else if (rawPin.StartsWith("sys:", StringComparison.OrdinalIgnoreCase) || rawPin.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
            {
                string rawFam = rawPin.StartsWith("sys:", StringComparison.OrdinalIgnoreCase) ? rawPin[4..] : rawPin[6..];
                string fam = FontShelfService.CleanRegistryFontFamilyName(rawFam);
                if (string.IsNullOrEmpty(fam)) continue;
                var kind = InferFontKindFromName(fam);
                string targetFace = $"local:{fam}";
                bool on = string.Equals(_settings.FontFace, targetFace, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(_settings.FontFace, fam, StringComparison.OrdinalIgnoreCase);
                tiles.Add((rawPin, rawPin, fam, FallbackFontForKind(kind), targetFace, $"{fam}, {FallbackWindowsFamilyForKind(kind)}", on));
            }
            else if (_library.Fonts.Catalog.FirstOrDefault(c => string.Equals(c.Id, rawPin, StringComparison.OrdinalIgnoreCase)) is { } pk)
            {
                bool inst = _library.Fonts.IsPackInstalled(pk.Id);
                bool dl = _library.Fonts.IsDownloading(pk.Id);
                string fbFam = FallbackWindowsFamilyForKind(pk.Kind);
                string fam = _library.Fonts.ResolveXamlFontSource(pk.Id, fbFam);
                string lbl = dl ? "下载中…" : (inst ? pk.Name : $"{pk.Name} ↓");
                bool on = string.Equals(_settings.FontFace, pk.Id, StringComparison.OrdinalIgnoreCase);
                tiles.Add((pk.Id, rawPin, lbl, FallbackFontForKind(pk.Kind), pk.Id, fam, on));
            }
        }

        int totalCells = tiles.Count + 1;
        int rowCount = (totalCells + 3) / 4;
        for (int r = 0; r < rowCount; r++) FontTilesHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        bool matchedAnyTile = false;
        for (int i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            if (t.Active) matchedAnyTile = true;
            var sp = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = "永", FontFamily = new FontFamily(t.SampleFont), FontSize = 15.5, Foreground = ThemeHelper.Brush(p.T1), HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = t.Label, FontSize = 10, Foreground = ThemeHelper.Brush(t.Active ? tint.Accent : p.T2), HorizontalAlignment = HorizontalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });

            var btn = new Button
            {
                Height = 44,
                Padding = new Thickness(3, 0, 3, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1.5),
                Background = ThemeHelper.Brush(t.Active ? p.Card : p.Inset),
                BorderBrush = t.Active ? ThemeHelper.Brush(tint.Accent) : new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = sp,
            };

            if (t.PinId is string pinKey)
            {
                var ctx = new MenuFlyout();
                var unpinMi = new MenuFlyoutItem { Text = "取消固定" };
                unpinMi.Click += (_, _) =>
                {
                    _library.Prefs.PinnedFontIds.RemoveAll(x => string.Equals(x, pinKey, StringComparison.OrdinalIgnoreCase));
                    _library.SavePrefs();
                    ApplyTypesetControlsFromSettings();
                };
                ctx.Items.Add(unpinMi);
                btn.ContextFlyout = ctx;
            }

            var captured = t;
            btn.Click += async (_, _) =>
            {
                string? packToEnsure = captured.Key == "wenkai" ? "wenkai"
                    : _library.Fonts.Catalog.FirstOrDefault(c => string.Equals(c.Id, captured.Key, StringComparison.OrdinalIgnoreCase))?.Id;

                bool needsReload = false;
                if (packToEnsure is not null)
                {
                    if (!_library.Fonts.IsPackInstalled(packToEnsure))
                    {
                        try { await _library.Fonts.DownloadPackAsync(packToEnsure); }
                        catch (Exception ex) { ShowReaderToast($"字体下载失败：{ex.Message}"); return; }
                    }
                    needsReload = true;
                }
                else if (captured.TargetFace?.StartsWith("file:", StringComparison.OrdinalIgnoreCase) == true)
                {
                    needsReload = true;
                }

                _settings = _settings with { Font = captured.TargetFont, FontFace = captured.TargetFace };
                PushSettingsToReader();
                if (needsReload)
                {
                    _pendingSentence = _currentSentence;
                    LoadSection(_currentSection, forceReload: true);
                }
            };
            Grid.SetRow(btn, i / 4);
            Grid.SetColumn(btn, i % 4);
            FontTilesHost.Children.Add(btn);
        }

        // 最后一格：更多（打开字样册 FontBook）
        bool customFaceActive = !matchedAnyTile && !string.IsNullOrEmpty(_settings.FontFace);
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
        Grid.SetRow(moreBtn, tiles.Count / 4);
        Grid.SetColumn(moreBtn, tiles.Count % 4);
        FontTilesHost.Children.Add(moreBtn);
    }

    private IReadOnlyList<string> GetAllCustomFontFiles()
    {
        var files = new HashSet<string>(_library.Fonts.GetCustomFontFiles(), StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(_library.FontsDir))
            {
                var catFiles = new HashSet<string>(_library.Fonts.Catalog.SelectMany(c => c.Files.Select(f => f.File)), StringComparer.OrdinalIgnoreCase);
                foreach (string full in Directory.GetFiles(_library.FontsDir, "*.ttc"))
                {
                    string fn = Path.GetFileName(full);
                    if (!fn.StartsWith('.') && !catFiles.Contains(fn))
                        files.Add(fn);
                }
            }
        }
        catch { }
        return files.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task ShowFontBookDialogAsync()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        ContentDialog? dlgRef = null;

        string activeCategory = "all"; // all | song | kai | hei | system | custom
        var sysFonts = _library.Fonts.DiscoverSystemCjkFonts();

        var specimenBox = new TextBox
        {
            Text = "天地玄黄，宇宙洪荒。明月松间照，清泉石上流。",
            PlaceholderText = "输入自定义试排样字…",
            FontSize = 12.5,
        };
        var searchBox = new TextBox
        {
            PlaceholderText = "搜索字体名称…",
            FontSize = 12,
        };

        var catRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var listStack = new StackPanel { Spacing = 8 };

        Button MakePinBtn(string pinId, Action refresh)
        {
            bool pinned = _library.Prefs.PinnedFontIds.Any(x => string.Equals(x, pinId, StringComparison.OrdinalIgnoreCase));
            var pb = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = pinned ? "★ 已固定" : "固定",
                Height = 25,
                MinHeight = 25,
                Padding = new Thickness(9, 0, 9, 0),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(pinned ? tint.Accent : p.T2),
            };
            pb.Click += (_, _) =>
            {
                int idx = _library.Prefs.PinnedFontIds.FindIndex(x => string.Equals(x, pinId, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) _library.Prefs.PinnedFontIds.RemoveAt(idx);
                else _library.Prefs.PinnedFontIds.Add(pinId);
                _library.SavePrefs();
                ApplyTypesetControlsFromSettings();
                refresh();
            };
            return pb;
        }

        bool MatchesKindFilter(FontKind kind) => activeCategory switch
        {
            "song" => kind is FontKind.Song or FontKind.Fang,
            "kai" => kind is FontKind.Kai or FontKind.Hand,
            "hei" => kind == FontKind.Hei,
            _ => true,
        };

        void ApplyFontChoice(Font fallbackFont, string? fontFace, bool reloadSection)
        {
            _settings = _settings with { Font = fallbackFont, FontFace = fontFace };
            PushSettingsToReader();
            if (reloadSection)
            {
                _pendingSentence = _currentSentence;
                LoadSection(_currentSection, forceReload: true);
            }
            dlgRef?.Hide();
        }

        void Rebuild()
        {
            var customFiles = GetAllCustomFontFiles();
            catRow.Children.Clear();
            foreach (var (ck, cl) in new[]
            {
                ("all", "全部"),
                ("song", "衬线·宋仿"),
                ("kai", "楷书·手写"),
                ("hei", "黑体"),
                ("system", $"系统 ({sysFonts.Count})"),
                ("custom", $"已导入 ({customFiles.Count})"),
            })
            {
                bool on = activeCategory == ck;
                var cb = new Button
                {
                    Content = cl,
                    Height = 25,
                    MinHeight = 25,
                    Padding = new Thickness(10, 0, 10, 0),
                    CornerRadius = new CornerRadius(12.5),
                    BorderThickness = new Thickness(0),
                    FontSize = 11,
                    FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Background = ThemeHelper.Brush(on ? tint.Soft : p.Inset),
                    Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T2),
                    UseSystemFocusVisuals = false,
                };
                string nextCat = ck;
                cb.Click += (_, _) => { activeCategory = nextCat; Rebuild(); };
                catRow.Children.Add(cb);
            }

            listStack.Children.Clear();
            string sampleText = string.IsNullOrWhiteSpace(specimenBox.Text)
                ? "天地玄黄，宇宙洪荒。明月松间照，清泉石上流。"
                : specimenBox.Text.Trim();
            string query = searchBox.Text.Trim();
            bool MatchesSearch(string text) => string.IsNullOrEmpty(query) || text.Contains(query, StringComparison.OrdinalIgnoreCase);

            var resolvedPreset = PaperAppearance.Resolve(_library, _settings).Preset;
            bool defaultIsSans = resolvedPreset is Paper.Plain or Paper.Dark;

            // 1. 内置核心字体与推荐开源字库
            if (activeCategory is "all" or "song" or "kai" or "hei")
            {
                var builtins = new (string Id, string Label, string Desc, FontKind Kind, Font TargetFont, string? TargetFace, string PreviewFam, bool Active)[]
                {
                    (
                        "system",
                        "系统无衬线",
                        "清晰利落屏幕正文字体（选中后在任何纸色上均使用黑体，不会卡在衬线体）",
                        FontKind.Hei,
                        Font.Sans,
                        null,
                        YYFontFamily.Sans.WindowsFamily(),
                        string.IsNullOrEmpty(_settings.FontFace) && (_settings.Font == Font.Sans || (_settings.Font is null && defaultIsSans))
                    ),
                    (
                        "serif",
                        "宋体（衬线）",
                        "传统中文书籍正文标准明体，长文耐读",
                        FontKind.Song,
                        Font.Serif,
                        null,
                        YYFontFamily.Serif.WindowsFamily(),
                        string.IsNullOrEmpty(_settings.FontFace) && (_settings.Font == Font.Serif || (_settings.Font is null && !defaultIsSans))
                    ),
                    (
                        "kai",
                        "楷体",
                        "温润手写书卷气，适合古典诗词与散文",
                        FontKind.Kai,
                        Font.Kai,
                        null,
                        YYFontFamily.Kaiti.WindowsFamily(),
                        string.IsNullOrEmpty(_settings.FontFace) && _settings.Font == Font.Kai
                    ),
                };

                foreach (var bItem in builtins)
                {
                    if (!MatchesKindFilter(bItem.Kind) || !MatchesSearch($"{bItem.Label} {bItem.Desc}")) continue;
                    var captured = bItem;
                    var card = new StackPanel { Spacing = 5 };
                    var top = new Grid { ColumnSpacing = 8 };
                    top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var meta = new StackPanel { Spacing = 1 };
                    meta.Children.Add(new TextBlock { Text = captured.Label, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(captured.Active ? tint.Accent : p.T1) });
                    meta.Children.Add(new TextBlock { Text = captured.Desc, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
                    top.Children.Add(meta);

                    var useBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources[captured.Active ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"],
                        Content = captured.Active ? "✓ 当前使用" : "选用",
                        Height = 25,
                        MinHeight = 25,
                        FontSize = 11,
                    };
                    useBtn.Click += (_, _) => ApplyFontChoice(captured.TargetFont, captured.TargetFace, reloadSection: false);
                    Grid.SetColumn(useBtn, 1);
                    top.Children.Add(useBtn);
                    card.Children.Add(top);

                    card.Children.Add(new TextBlock
                    {
                        Text = sampleText,
                        FontFamily = new FontFamily(captured.PreviewFam),
                        FontSize = 14.5,
                        Foreground = ThemeHelper.Brush(p.T1),
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });

                    listStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(11),
                        Background = ThemeHelper.Brush(captured.Active ? tint.Soft : p.Card),
                        BorderBrush = ThemeHelper.Brush(captured.Active ? tint.Edge : p.Line),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(12, 9, 12, 9),
                        Child = card,
                    });
                }

                foreach (var pack in _library.Fonts.Catalog)
                {
                    var pk = pack;
                    if (!MatchesKindFilter(pk.Kind) || !MatchesSearch($"{pk.Name} {pk.Id} {pk.License}")) continue;

                    bool inst = _library.Fonts.IsPackInstalled(pk.Id);
                    bool dl = _library.Fonts.IsDownloading(pk.Id);
                    bool active = pk.Id == "wenkai"
                        ? (string.IsNullOrEmpty(_settings.FontFace) && _settings.Font == Font.WenKai)
                        : string.Equals(_settings.FontFace, pk.Id, StringComparison.OrdinalIgnoreCase);
                    string fallbackFam = pk.Id == "wenkai" ? YYFontFamily.Wenkai.WindowsFamily() : FallbackWindowsFamilyForKind(pk.Kind);
                    string previewFam = _library.Fonts.ResolveXamlFontSource(pk.Id, fallbackFam);

                    var card = new StackPanel { Spacing = 5 };
                    var top = new Grid { ColumnSpacing = 8 };
                    top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    ulong totalBytes = 0;
                    foreach (var f in pk.Files) totalBytes += f.Bytes;
                    string sizeNote = totalBytes > 0 ? $" · {totalBytes / 1048576.0:0.1} MB" : "";
                    var meta = new StackPanel { Spacing = 1 };
                    meta.Children.Add(new TextBlock { Text = pk.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T1) });
                    meta.Children.Add(new TextBlock { Text = inst ? "开源正文字体 · 已安装在本机" : $"开源正文字体 ({pk.License}){sizeNote}", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                    top.Children.Add(meta);

                    var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    if (pk.Id != "wenkai")
                        acts.Children.Add(MakePinBtn(pk.Id, Rebuild));
                    if (dl)
                    {
                        var cancelBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "取消", FontSize = 11, Foreground = ThemeHelper.Brush(p.Danger) };
                        cancelBtn.Click += (_, _) => { _library.Fonts.CancelDownload(pk.Id); Rebuild(); };
                        acts.Children.Add(cancelBtn);
                    }
                    else if (inst)
                    {
                        var delBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "删除", FontSize = 11, Foreground = ThemeHelper.Brush(p.Danger) };
                        delBtn.Click += (_, _) =>
                        {
                            _library.Fonts.DeletePack(pk.Id);
                            if (active)
                            {
                                _settings = _settings with { Font = FallbackFontForKind(pk.Kind), FontFace = null };
                                PushSettingsToReader();
                            }
                            Rebuild();
                        };
                        acts.Children.Add(delBtn);
                    }

                    var useBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources[(active || inst) ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"],
                        Content = dl ? $"下载中 {(int)Math.Round(_library.Fonts.GetProgress(pk.Id) * 100)}%" : (active ? "✓ 当前使用" : (inst ? "选用" : "下载并选用")),
                        IsEnabled = !dl,
                        Height = 25,
                        MinHeight = 25,
                        FontSize = 11,
                    };
                    useBtn.Click += async (_, _) =>
                    {
                        if (!inst)
                        {
                            useBtn.IsEnabled = false;
                            useBtn.Content = "下载中…";
                            try { await _library.Fonts.DownloadPackAsync(pk.Id); }
                            catch (Exception ex) { ShowReaderToast($"下载失败：{ex.Message}"); Rebuild(); return; }
                        }
                        if (pk.Id == "wenkai")
                            ApplyFontChoice(Font.WenKai, null, reloadSection: true);
                        else
                            ApplyFontChoice(FallbackFontForKind(pk.Kind), pk.Id, reloadSection: true);
                    };
                    acts.Children.Add(useBtn);
                    Grid.SetColumn(acts, 1);
                    top.Children.Add(acts);
                    card.Children.Add(top);

                    card.Children.Add(new TextBlock
                    {
                        Text = sampleText,
                        FontFamily = new FontFamily(previewFam),
                        FontSize = 14.5,
                        Foreground = ThemeHelper.Brush(p.T1),
                        TextWrapping = TextWrapping.Wrap,
                        MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });

                    listStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(11),
                        Background = ThemeHelper.Brush(active ? tint.Soft : p.Card),
                        BorderBrush = ThemeHelper.Brush(active ? tint.Edge : p.Line),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(12, 9, 12, 9),
                        Child = card,
                    });
                }
            }

            // 2. 本机系统 CJK 字体
            if (activeCategory is "all" or "system" or "song" or "kai" or "hei")
            {
                var matchingSys = sysFonts
                    .Select(sf => (Entry: sf, Kind: InferFontKindFromName($"{sf.DisplayName} {sf.FamilyName}")))
                    .Where(x => (activeCategory == "system" || MatchesKindFilter(x.Kind)) && MatchesSearch($"{x.Entry.DisplayName} {x.Entry.FamilyName}"))
                    .ToArray();

                if (matchingSys.Length > 0)
                {
                    if (activeCategory != "system")
                    {
                        listStack.Children.Add(new TextBlock
                        {
                            Text = $"系统字体 ({matchingSys.Length})",
                            FontSize = 11.5,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = ThemeHelper.Brush(p.T3),
                            Margin = new Thickness(2, 6, 2, 0),
                        });
                    }

                    foreach (var (entry, kind) in matchingSys)
                    {
                        string targetFace = $"local:{entry.FamilyName}";
                        bool active = string.Equals(_settings.FontFace, targetFace, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(_settings.FontFace, entry.FamilyName, StringComparison.OrdinalIgnoreCase);

                        var card = new StackPanel { Spacing = 5 };
                        var top = new Grid { ColumnSpacing = 8 };
                        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        top.Children.Add(new TextBlock
                        {
                            Text = entry.DisplayName,
                            FontSize = 12.5,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T1),
                            VerticalAlignment = VerticalAlignment.Center,
                        });

                        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                        acts.Children.Add(MakePinBtn($"sys:{entry.FamilyName}", Rebuild));
                        var useBtn = new Button
                        {
                            Style = (Style)Application.Current.Resources[active ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"],
                            Content = active ? "✓ 当前使用" : "选用",
                            Height = 25,
                            MinHeight = 25,
                            FontSize = 11,
                        };
                        var capturedEntry = entry;
                        var capturedKind = kind;
                        useBtn.Click += (_, _) => ApplyFontChoice(FallbackFontForKind(capturedKind), $"local:{capturedEntry.FamilyName}", reloadSection: false);
                        acts.Children.Add(useBtn);
                        Grid.SetColumn(acts, 1);
                        top.Children.Add(acts);
                        card.Children.Add(top);

                        card.Children.Add(new TextBlock
                        {
                            Text = sampleText,
                            FontFamily = new FontFamily($"{entry.FamilyName}, {FallbackWindowsFamilyForKind(kind)}"),
                            FontSize = 14.5,
                            Foreground = ThemeHelper.Brush(p.T1),
                            TextWrapping = TextWrapping.Wrap,
                            MaxLines = 2,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        });

                        listStack.Children.Add(new Border
                        {
                            CornerRadius = new CornerRadius(10),
                            Background = ThemeHelper.Brush(active ? tint.Soft : p.Card),
                            BorderBrush = ThemeHelper.Brush(active ? tint.Edge : p.Line),
                            BorderThickness = new Thickness(1),
                            Padding = new Thickness(12, 8, 12, 8),
                            Child = card,
                        });
                    }
                }
            }

            // 3. 已导入的本地字体文件 (.ttf / .otf / .ttc / .woff2)
            if (activeCategory is "all" or "custom" or "song" or "kai" or "hei")
            {
                var matchingCustom = customFiles
                    .Select(fn =>
                    {
                        string disp = _library.Fonts.GetFontFileDisplayName(fn);
                        return (FileName: fn, Display: disp, Kind: InferFontKindFromName($"{disp} {fn}"));
                    })
                    .Where(x => (activeCategory == "custom" || MatchesKindFilter(x.Kind)) && MatchesSearch($"{x.Display} {x.FileName}"))
                    .ToArray();

                if (activeCategory == "custom" && customFiles.Count == 0)
                {
                    listStack.Children.Add(new TextBlock
                    {
                        Text = "尚未导入本地字体。点击下方按钮可导入任意 .ttf / .otf / .ttc / .woff2 字体文件。",
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.T3),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(4, 12, 4, 12),
                    });
                }
                else if (matchingCustom.Length > 0)
                {
                    if (activeCategory != "custom")
                    {
                        listStack.Children.Add(new TextBlock
                        {
                            Text = $"已导入的自定义字体 ({matchingCustom.Length})",
                            FontSize = 11.5,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = ThemeHelper.Brush(p.T3),
                            Margin = new Thickness(2, 6, 2, 0),
                        });
                    }

                    foreach (var (fileName, dispName, kind) in matchingCustom)
                    {
                        string faceKey = $"file:{fileName}";
                        bool active = string.Equals(_settings.FontFace, faceKey, StringComparison.OrdinalIgnoreCase);
                        string? realFam = _library.Fonts.GetFontFileFamily(fileName);
                        string previewFam = _library.Fonts.ResolveXamlFontSource(faceKey, FallbackWindowsFamilyForKind(kind));

                        var card = new StackPanel { Spacing = 5 };
                        var top = new Grid { ColumnSpacing = 8 };
                        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var meta = new StackPanel { Spacing = 1 };
                        meta.Children.Add(new TextBlock { Text = dispName, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T1) });
                        string subLine = !string.IsNullOrEmpty(realFam) && !string.Equals(realFam, dispName, StringComparison.OrdinalIgnoreCase)
                            ? $"{realFam} · {fileName}"
                            : fileName;
                        meta.Children.Add(new TextBlock { Text = subLine, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                        top.Children.Add(meta);

                        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                        acts.Children.Add(MakePinBtn(faceKey, Rebuild));
                        var delBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "删除", FontSize = 11, Foreground = ThemeHelper.Brush(p.Danger) };
                        string fnCapture = fileName;
                        var kindCapture = kind;
                        string dispCapture = dispName;
                        delBtn.Click += (_, _) =>
                        {
                            _library.Fonts.DeleteCustomFont(fnCapture);
                            _library.Prefs.PinnedFontIds.RemoveAll(x => string.Equals(x, faceKey, StringComparison.OrdinalIgnoreCase));
                            _library.SavePrefs();
                            if (active)
                            {
                                _settings = _settings with { Font = FallbackFontForKind(kindCapture), FontFace = null };
                                PushSettingsToReader();
                            }
                            else
                            {
                                ApplyTypesetControlsFromSettings();
                            }
                            Rebuild();
                        };
                        var useBtn = new Button
                        {
                            Style = (Style)Application.Current.Resources[active ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"],
                            Content = active ? "✓ 当前使用" : "选用",
                            Height = 25,
                            MinHeight = 25,
                            FontSize = 11,
                        };
                        useBtn.Click += (_, _) => ApplyFontChoice(FallbackFontForKind(kindCapture), faceKey, reloadSection: true);
                        acts.Children.Add(delBtn);
                        acts.Children.Add(useBtn);
                        Grid.SetColumn(acts, 1);
                        top.Children.Add(acts);
                        card.Children.Add(top);

                        card.Children.Add(new TextBlock
                        {
                            Text = sampleText,
                            FontFamily = new FontFamily(previewFam),
                            FontSize = 14.5,
                            Foreground = ThemeHelper.Brush(p.T1),
                            TextWrapping = TextWrapping.Wrap,
                            MaxLines = 2,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        });

                        listStack.Children.Add(new Border
                        {
                            CornerRadius = new CornerRadius(10),
                            Background = ThemeHelper.Brush(active ? tint.Soft : p.Card),
                            BorderBrush = ThemeHelper.Brush(active ? tint.Edge : p.Line),
                            BorderThickness = new Thickness(1),
                            Padding = new Thickness(12, 8, 12, 8),
                            Child = card,
                        });
                    }
                }
            }

            if (listStack.Children.Count == 0)
            {
                listStack.Children.Add(new TextBlock
                {
                    Text = "没有匹配的字体",
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 18, 0, 18),
                });
            }
        }

        specimenBox.TextChanged += (_, _) => Rebuild();
        searchBox.TextChanged += (_, _) => Rebuild();
        Action onCatalogProgress = () => DispatcherQueue.TryEnqueue(Rebuild);
        _library.Fonts.Changed += onCatalogProgress;
        Rebuild();

        var importFontBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
            Content = "＋ 导入本地字体文件 (.ttf / .otf / .ttc / .woff2)…",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        importFontBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".ttf");
            picker.FileTypeFilter.Add(".otf");
            picker.FileTypeFilter.Add(".ttc");
            picker.FileTypeFilter.Add(".woff2");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            try
            {
                string importedName = _library.Fonts.ImportFontFile(f.Path);
                string disp = _library.Fonts.GetFontFileDisplayName(importedName);
                var kind = InferFontKindFromName($"{disp} {importedName}");
                ShowReaderToast($"已导入并应用字体：{disp}");
                ApplyFontChoice(FallbackFontForKind(kind), $"file:{importedName}", reloadSection: true);
            }
            catch (Exception ex) { ShowReaderToast($"导入字体失败：{ex.Message}"); }
        };

        var rootPanel = new StackPanel { Spacing = 10, Padding = new Thickness(22, 18, 22, 18) };
        rootPanel.Children.Add(YYChrome.MakeSheetHeader(L10n.T("字样册"), _detail.Summary.Title, () => dlgRef?.Hide(), dark, titleSize: 18));
        rootPanel.Children.Add(specimenBox);
        rootPanel.Children.Add(searchBox);
        rootPanel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = catRow,
        });
        rootPanel.Children.Add(new ScrollViewer
        {
            MaxHeight = 350,
            Content = listStack,
        });
        rootPanel.Children.Add(importFontBtn);

        var doneBtn = YYChrome.MakeSheetPillButton(L10n.T("完成"), () => dlgRef?.Hide(), dark, _library.Prefs.Tint, "warm", height: 36, horizontalPadding: 22);
        doneBtn.HorizontalAlignment = HorizontalAlignment.Right;
        rootPanel.Children.Add(doneBtn);

        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, dark, 504, rootPanel, maxHeight: 740, tint: _library.Prefs.Tint);
        try
        {
            await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
        }
        finally
        {
            _library.Fonts.Changed -= onCatalogProgress;
        }
    }

    private void UpdatePaperChrome()
    {
        if (_layout is null) return;
        var g = BuildGeometry();
        _ = _pager.ApplySettingsAllAsync(new { margin = g.Margin, top = g.Top, bottom = g.Bottom, cols = g.Columns, motion = ShouldAnimatePageTurn() ? "full" : "reduced" });
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
        var resolved = _previewPaper is not null
            ? new PaperAppearance.Resolved(YuyueCoreMethods.PaperColors(_previewPaper).Preset, _previewPaper)
            : PaperAppearance.Resolve(_library, _settings);
        Paper effectivePreset = resolved.Preset;
        CustomPaper? activeCustom = resolved.Custom;
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
        _ = _pager.ApplySettingsAllAsync(new
        {
            paper = PaperCssId(effectivePreset),
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
            font = FontCssValue(_settings.Font, _settings.FontFace),
            compress = _settings.Compress,
            motion = ShouldAnimatePageTurn() ? "full" : "reduced",
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
                    bool isProgrammatic = DateTime.UtcNow <= _programmaticScrollUntilUtc
                        && (_programmaticTargetPage is null || _programmaticTargetPage.Value == p);
                    if (_programmaticTargetPage == p)
                        _programmaticTargetPage = null;

                    _currentPage = p;
                    if (_layout is not null)
                    {
                        _currentSentence = _layout.FirstSentenceOn(p);
                        if (!isProgrammatic)
                            _hasUserMoved = true;
                        SaveCurrentProgress();
                    }
                    if (!isProgrammatic)
                    {
                        _lastInteractionUtc = DateTime.UtcNow;
                        OnManualPageTurned();
                    }
                    UpdateHeaderStatus();
                    ApplyTintAndChrome();
                }
                break;

            case "scroll":
                if (msg.Sentence is int s)
                {
                    _lastInteractionUtc = DateTime.UtcNow;
                    if (_hasLoadedInitialSection && s != _currentSentence)
                    {
                        _hasUserMoved = true;
                        if (!_audio.IsPlaying && _hasPausedAudioSession && Math.Abs(s - _audio.CurrentSentence) > 2)
                            _pausedOffSpot = true;
                    }
                    _currentSentence = s;
                    SaveCurrentProgress();
                    UpdateHeaderStatus();
                    if (_settings.Mode == LayoutMode.Scrolled
                        && _library.NextSection(_bookId, _currentSection) is null
                        && _sectionSentenceCount > 0
                        && s >= Math.Max(0, _sectionSentenceCount - 2))
                    {
                        _ = MaybeOfferFinishedSheetAsync();
                    }
                }
                break;

            case "wheel":
                _lastInteractionUtc = DateTime.UtcNow;
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

            case "scrollEdge":
                _lastInteractionUtc = DateTime.UtcNow;
                if (_settings.Mode == LayoutMode.Scrolled && msg.DeltaY is double edgeDir && edgeDir != 0)
                {
                    bool forward = edgeDir > 0;
                    if (msg.Armed == true)
                    {
                        HideScrollEdgeCue();
                        await CrossSectionFromScrollEdgeAsync(forward);
                    }
                    else
                    {
                        ShowScrollEdgeCue(forward, msg.Progress ?? 0);
                    }
                }
                break;

            case "winContext":
                if (msg.X is double cx && msg.Y is double cy)
                    ShowPageContextMenu(new Point(cx, cy), targetSentence: msg.Sentence, targetOffset: msg.Offset);
                break;

            case "context":
                if (msg.X is double rcx && msg.Y is double rcy)
                    ShowPageContextMenu(new Point(rcx, rcy), targetSentence: msg.Sentence, targetOffset: msg.Offset);
                break;

            case "winKey":
                _lastInteractionUtc = DateTime.UtcNow;
                HandleForwardedWebKey(msg);
                break;

            case "bubble":
                if (msg.Line is int lineIdx && msg.X is double bx && msg.Y is double by)
                {
                    if (lineIdx >= 0 && lineIdx < _sectionBubbles.Length)
                    {
                        var bl = _sectionBubbles[lineIdx];
                        ShowBubbleSpeakerFlyout((int)bl.First, (int)bl.LineStart, msg.Choice, new Point(bx, by));
                    }
                    else
                    {
                        ShowBubbleSpeakerFlyout(lineIdx, msg.Offset ?? 0, msg.Choice, new Point(bx, by));
                    }
                }
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
                _lastInteractionUtc = DateTime.UtcNow;
                // Step 1: If ANY floating card is open, dismiss it and return immediately (never fall through to mark/word/link/page-turn)
                if (HasAnyFloatingCardOpen())
                {
                    DismissFloatingCards();
                    return;
                }
                // Step 2 (5415880 / ReaderModel.swift:1031-1038): If transient chrome is shown, hide it and return immediately
                if (_chromeShown)
                {
                    SetChromeShown(false);
                    return;
                }

                if (msg.Mark is string markId)
                {
                    var found = _library.GetMarks(_bookId).FirstOrDefault(m => m.Id == markId);
                    if (found is not null)
                    {
                        _openMark = found;
                        var mRect = await _host.MarkRectAsync(markId);
                        ShowSelectionCard(mRect ?? new ReaderRect { X = msg.X ?? 200, Y = msg.Y ?? 200, Width = 2, Height = 24 }, editingExistingMark: true);
                    }
                }
                else if (msg.Link?.Note is not null && msg.Link.Target is string noteTarget)
                {
                    ShowFootnote(noteTarget, msg.Link.Note);
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
                else if (_settings.Mode == LayoutMode.Paginated && msg.X is double tx && msg.Width is double tw && tw > 100)
                {
                    // 1:1 ReaderModel.swift:958-961 — clicking outer 30% edges turns pages
                    if (tx < tw * 0.30) await TurnPrevAsync();
                    else if (tx > tw * 0.70) await TurnNextAsync();
                }
                break;

            case "doubleTap":
                // 1:1 ReaderForeign.swift:468 — only foreign books open SentenceCard on double-click; never hijack Chinese double-click text selection!
                if (_isForeignBook && msg.Sentence is int tappedSentence)
                {
                    await ShowSentenceTranslationCardAsync(tappedSentence, msg.X, msg.Y);
                }
                break;

            case "activePage":
                if (msg.Page is int activePage && activePage != _currentPage && _settings.Mode == LayoutMode.Paginated)
                {
                    // 8-second manual page-turn grace period (1:1 ReaderModel.swift followFrom)
                    if (DateTime.UtcNow < _manualPageTurnIgnoreUntilUtc) break;
                    await ScrollToPageProgrammaticAsync(activePage, preserveCurrentSentence: true);
                    UpdateHeaderStatus();
                }
                break;
        }
    }

    // ---------- Right-Click Context Menus (1:1 MacReaderView.swift:792-845) ----------

    private void ShowPageContextMenu(Point pos, int? targetSentence = null, int? targetOffset = null)
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

            if (_audio.IsUsingPairedRecording && !string.IsNullOrEmpty(_pairedAudioBookId) && _selection.Start is { } selPt)
            {
                var pinItem = new MenuFlyoutItem { Text = "对准这句（校准原声进度）" };
                uint pinSent = (uint)Math.Max(0, selPt.S);
                pinItem.Click += (_, _) => PinPairedRecordingHere(pinSent);
                menu.Items.Add(pinItem);
            }

            if (_audio.IsPlaying && _selection.End is { } selEnd && selEnd.S >= _audio.CurrentSentence && _audio.CurrentSentence >= 0)
            {
                int a = _audio.CurrentSentence;
                int b = selEnd.S;
                var abItem = new MenuFlyoutItem { Text = "从正在念的这句循环到这里" };
                abItem.Click += (_, _) =>
                {
                    _loopSingleSentence = false;
                    _audio.SetLoopRange(a, b);
                    ShowReaderToast($"已开启 A–B 循环（第 {a + 1}–{b + 1} 句）", "取消循环", () => { _audio.ClearLoopRange(); ShowReaderToast("已取消区间循环"); });
                };
                menu.Items.Add(abItem);
            }

            var askItem = new MenuFlyoutItem { Text = "问书伴" };
            askItem.Click += AskSelection_Click;
            menu.Items.Add(askItem);

            if (_library.Companion.IsConfigured)
            {
                var transItem = new MenuFlyoutItem { Text = "翻译" };
                transItem.Click += TranslateSelection_Click;
                menu.Items.Add(transItem);
            }

            var copyItem = new MenuFlyoutItem { Text = "复制 (Ctrl+C)" };
            copyItem.Click += CopySelection_Click;
            menu.Items.Add(copyItem);

            var shareItem = new MenuFlyoutItem { Text = "分享…" };
            shareItem.Click += ShareSelection_Click;
            menu.Items.Add(shareItem);
        }
        else
        {
            if (targetSentence is int ts && ts >= 0)
            {
                var speakHere = new MenuFlyoutItem { Text = "从这句朗读" };
                speakHere.Click += (_, _) => StartListeningAt(ts);
                menu.Items.Add(speakHere);

                if (_audio.IsPlaying && _audio.CurrentSentence >= 0 && ts > _audio.CurrentSentence)
                {
                    int a = _audio.CurrentSentence;
                    var abItem = new MenuFlyoutItem { Text = "从正在念的这句循环到这里" };
                    abItem.Click += (_, _) =>
                    {
                        _loopSingleSentence = false;
                        _audio.SetLoopRange(a, ts);
                        ShowReaderToast($"已开启 A–B 循环（第 {a + 1}–{ts + 1} 句）", "取消循环", () => { _audio.ClearLoopRange(); ShowReaderToast("已取消区间循环"); });
                    };
                    menu.Items.Add(abItem);
                }

                if (_audio.IsUsingPairedRecording && !string.IsNullOrEmpty(_pairedAudioBookId))
                {
                    var pinItem = new MenuFlyoutItem { Text = "从这里对准原声" };
                    pinItem.Click += (_, _) => PinPairedRecordingHere((uint)ts);
                    menu.Items.Add(pinItem);
                }

                if (_hasDialogue)
                {
                    var whoItem = new MenuFlyoutItem { Text = "谁说的…" };
                    whoItem.Click += (_, _) => ShowBubbleSpeakerFlyout(ts, targetOffset ?? 0, null, pos);
                    menu.Items.Add(whoItem);
                }

                menu.Items.Add(new MenuFlyoutSeparator());
            }

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

        L10n.ApplyTree(WebViewContainer);
        menu.ShowAt(WebViewContainer, new FlyoutShowOptions { Position = pos });
    }

    private void OnManualPageTurned()
    {
        _hasUserMoved = true;
        if (_audio.IsPlaying)
            _manualPageTurnIgnoreUntilUtc = DateTime.UtcNow.AddSeconds(8);
        else if (_hasPausedAudioSession)
            _pausedOffSpot = true;

        if (FootnoteCard.Visibility == Visibility.Visible)
        {
            FootnoteCard.Visibility = Visibility.Collapsed;
            _openFootnote = null;
        }
        if (RecapCardHost.Visibility == Visibility.Visible)
            RecapCardHost.Visibility = Visibility.Collapsed;
        if (_returnFrom is not null)
        {
            _returnTurnsRemaining--;
            if (_returnTurnsRemaining <= 0)
            {
                _returnFrom = null;
                UpdateBottomChromeVisibility();
            }
        }
    }

    private void ShowBubbleSpeakerFlyout(int sentenceIdx, int startOffset, string? quickChoice, Point pos)
    {
        void RefreshAfterSpeakerEdit()
        {
            DramaSynthService.InvalidateCast(_bookId);
            _audio.InvalidateRolesAndSpeakers();
            _ = ApplySectionBubblesAsync();
            if (_activePanel == "cast") RenderSidePanel();
        }

        if (!string.IsNullOrEmpty(quickChoice))
        {
            try
            {
                _library.Core.DramaSetSpeaker(_bookId, _currentSection, (uint)sentenceIdx, (uint)Math.Max(0, startOffset), new UserChoice.Person(quickChoice));
                RefreshAfterSpeakerEdit();
            }
            catch { }
            return;
        }

        var menu = new MenuFlyout();
        string? topPerson = null;
        try
        {
            var people = _library.Core.DramaLinePeople(_bookId, _currentSection, (uint)sentenceIdx);
            foreach (var person in people.Take(10))
            {
                string nm = person.Name;
                topPerson ??= nm;
                var mi = new MenuFlyoutItem { Text = nm };
                mi.Click += (_, _) =>
                {
                    try
                    {
                        _library.Core.DramaSetSpeaker(_bookId, _currentSection, (uint)sentenceIdx, (uint)Math.Max(0, startOffset), new UserChoice.Person(nm));
                        RefreshAfterSpeakerEdit();
                    }
                    catch { }
                };
                menu.Items.Add(mi);
            }
        }
        catch { }

        var otherItem = new MenuFlyoutItem { Text = "别的人物…" };
        otherItem.Click += async (_, _) =>
        {
            var chosen = await VoiceCloneAndDramaDialogs.ShowPeoplePickerDialogAsync(RootGrid.XamlRoot, _library, _bookId, _currentSection, "选择这句的说话人");
            if (chosen is not null)
            {
                try
                {
                    _library.Core.DramaSetSpeaker(_bookId, _currentSection, (uint)sentenceIdx, (uint)Math.Max(0, startOffset), chosen);
                    RefreshAfterSpeakerEdit();
                }
                catch { }
            }
        };
        menu.Items.Add(otherItem);

        var grpItem = new MenuFlyoutItem { Text = "众人（齐声）" };
        grpItem.Click += (_, _) =>
        {
            try
            {
                _library.Core.DramaSetSpeaker(_bookId, _currentSection, (uint)sentenceIdx, (uint)Math.Max(0, startOffset), new UserChoice.Group("众人"));
                RefreshAfterSpeakerEdit();
            }
            catch { }
        };
        menu.Items.Add(grpItem);

        if (!string.IsNullOrEmpty(topPerson))
        {
            string capWho = topPerson;
            var voiceOfItem = new MenuFlyoutItem { Text = $"{capWho}的声音…" };
            voiceOfItem.Click += async (_, _) => await VoiceCloneAndDramaDialogs.ShowRoleDetailDialogAsync(
                this, RootGrid.XamlRoot, _library, _bookId, capWho, (vt, sel, cb) => ShowVoicePickerSheetAsync(vt, sel, cb, capWho), RefreshAfterSpeakerEdit);
            menu.Items.Add(voiceOfItem);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var narrItem = new MenuFlyoutItem { Text = "不是台词（旁白）" };
        narrItem.Click += (_, _) =>
        {
            try
            {
                _library.Core.DramaSetSpeaker(_bookId, _currentSection, (uint)sentenceIdx, (uint)Math.Max(0, startOffset), new UserChoice.NotSpeech());
                RefreshAfterSpeakerEdit();
            }
            catch { }
        };
        menu.Items.Add(narrItem);

        var clrItem = new MenuFlyoutItem { Text = "恢复自动识别" };
        clrItem.Click += (_, _) =>
        {
            try
            {
                _library.Core.DramaClearSpeaker(_bookId, _currentSection, (uint)sentenceIdx, (uint)Math.Max(0, startOffset));
                RefreshAfterSpeakerEdit();
            }
            catch { }
        };
        menu.Items.Add(clrItem);
        menu.ShowAt(WebViewContainer, new FlyoutShowOptions { Position = pos });
    }

    private async Task ApplySectionBubblesAsync()
    {
        try
        {
            if (_bubblesOn)
            {
                var lines = _library.Core.SectionBubbles(_bookId, _currentSection);
                _sectionBubbles = lines;
                await _host.SetBubblesAsync(lines);
            }
            else
            {
                _sectionBubbles = [];
                await _host.SetBubblesAsync(null);
            }
        }
        catch { }
    }

    private async Task OnSectionLaidOutAsync(bool isReady)
    {
        if (isReady)
        {
            await _host.SetUiAsync(L10n.IsEnglish ? "en" : "zh");
            if (_library.Prefs.GlobalEinkMode)
            {
                await _host.ApplySettingsAsync(new
                {
                    paper = "eink",
                    eink = true,
                    einkGray = _library.Prefs.GlobalEinkGray,
                    motion = "reduced",
                });
            }
            if (_bubblesOn) await ApplySectionBubblesAsync();
            VoiceCloneAndDramaDialogs.UpdateDramaFocusSection(_bookId, _currentSection);
            await ApplyPairedAlignmentOverlaysAsync();
            bool hideMarks = _isFullScreen && _library.Prefs.ImmersiveHideMarks;
            var allSectionMarks = _library.GetMarks(_bookId).Where(m => m.Section == _currentSection);
            var sectionMarks = hideMarks
                ? allSectionMarks.Where(m => _immersiveKeepShownMarks.Contains(m.Id))
                : allSectionMarks;
            await _host.SetMarksAsync(sectionMarks);
            await ApplyForeignReadingAsync();
            if (_pendingSourceHighlight is var (srcSec, srcSent) && srcSec == _currentSection)
            {
                await _host.SetSourceAsync(srcSent);
            }
            else if (_pendingSourceHighlight is not null)
            {
                await _host.SetSourceAsync(null);
            }
            var q = BookSearchBox.Text.Trim();
            if (_searchShown && !string.IsNullOrEmpty(q))
            {
                int? curSent = _hitIndex >= 0 && _hitIndex < _searchHits.Count && _searchHits[_hitIndex].Section == _currentSection
                    ? (int)_searchHits[_hitIndex].Sentence : null;
                int? curNth = curSent is not null ? (int)_searchHits[_hitIndex].Nth : null;
                await _host.SetHitsAsync(q, curSent, curNth);
            }
            else if (!string.IsNullOrEmpty(_pageHighlightQuery))
            {
                await HighlightPageHitsOnlyAsync(_pageHighlightQuery);
            }
        }

        if (_pendingAnchor is not null)
        {
            var anchor = _pendingAnchor;
            _pendingAnchor = null;
            MarkProgrammaticScroll();
            await _host.ScrollToAnchorAsync(anchor, animated: false, align: "top");
        }
        else if (_pendingEndOfSection && _layout is not null)
        {
            _pendingEndOfSection = false;
            _currentPage = Math.Max(0, _layout.PageCount - 1);
            MarkProgrammaticScroll(_currentPage);
            await _host.ScrollToEndOfSectionAsync(_layout.PageCount);
            _currentSentence = _settings.Mode == LayoutMode.Scrolled
                ? Math.Max(0, _sectionSentenceCount - 1)
                : _layout.FirstSentenceOn(_currentPage);
        }
        else if (_pendingSentence is int targetSentence)
        {
            _pendingSentence = null;
            _currentSentence = targetSentence;
            if (_layout is not null) _currentPage = _layout.PageOf(targetSentence);
            MarkProgrammaticScroll(_currentPage);
            await _host.ScrollToSentenceAsync(targetSentence, animated: false, align: "top");
        }

        if (isReady && LaunchSnapshotImage.Visibility == Visibility.Visible)
        {
            LaunchSnapshotImage.Visibility = Visibility.Collapsed;
            LaunchSnapshotImage.Source = null;
        }

        UpdateHeaderStatus();
        UpdateTopInfoBanners();
        ApplyTintAndChrome();
        if (_activePanel is "toc" or "notes" or "cast") RenderSidePanel();

        if (isReady)
        {
            // Check ElsewhereCard / SleepReturn / Recap BEFORE stamping local progress so synced remote progress isn't masked!
            if (!_initialOffersChecked)
            {
                _initialOffersChecked = true;
                CheckInitialRecapAndSleepOffer();
            }

            _hasLoadedInitialSection = true;
            if (_hasUserMoved)
            {
                SaveCurrentProgress();
                App.CurrentApp.NotifyShelfRefresh();
            }

            if (_translationsActive)
            {
                _ = ApplyCurrentSectionTranslationsAsync();
            }

            // Preload adjacent prev/next sections in background pooled WebView2 slots (TECH.md §3)
            _ = _pager.PreloadNeighborsAsync(_currentSection);
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
        var saved = _library.GetProgress(_bookId);
        if (saved is not null && saved.Position.Section < _detail.Sections.Length)
        {
            _currentSection = saved.Position.Section;
            _currentSentence = (int)saved.Position.Sentence;
        }
        UpdateHeaderStatus();

        if (panel == "typeset") _debugOpenTypeset = true;
        else if (panel == "listen-card") { _debugShowListenBar = true; _debugOpenListenCard = true; }
        else if (!string.IsNullOrEmpty(panel)) OpenPanel(panel);

        if (listenBar)
        {
            _debugShowListenBar = true;
            _debugListenActive = true;
            var sents = _library.GetSpokenSentences(_bookId, _currentSection);
            _sectionSentenceCount = Math.Max(8, sents.Count);
            ListenCapsule.Visibility = Visibility.Visible;
            UpdateCapsuleText(Math.Min(1, Math.Max(0, _sectionSentenceCount - 1)));
            ApplyTintAndChrome();
        }
    }
#endif

    private void MarkProgrammaticScroll(int? targetPage = null, int durationMs = 750)
    {
        _programmaticTargetPage = targetPage;
        _programmaticScrollUntilUtc = DateTime.UtcNow.AddMilliseconds(durationMs);
    }

    private Task ScrollToPageProgrammaticAsync(int page, bool? animated = null, bool preserveCurrentSentence = false)
    {
        bool anim = animated ?? ShouldAnimatePageTurn();
        _currentPage = page;
        if (!preserveCurrentSentence && _layout is not null)
            _currentSentence = _layout.FirstSentenceOn(page);
        MarkProgrammaticScroll(page, anim ? 950 : 600);
        return _host.ScrollToPageAsync(page, animated: anim);
    }

    private void SaveCurrentProgress()
    {
        if (!_hasLoadedInitialSection || !_hasUserMoved) return;
        try { _library.SaveProgress(_bookId, _currentSection, (uint)Math.Max(0, _currentSentence)); } catch { }
        if (!_finishedPrompted && CalculateBookFraction(_currentSection, _currentSentence) >= 0.995)
        {
            _ = MaybeOfferFinishedSheetAsync();
        }
    }

    private string ResolveSectionTitle(uint sec)
    {
        if (_detail.Toc.Length > 0)
        {
            var exact = _detail.Toc.FirstOrDefault(t => t.Section == sec && !string.IsNullOrWhiteSpace(t.Title));
            if (exact is not null) return exact.Title.Trim();

            var covering = _detail.Toc.LastOrDefault(t => t.Section <= sec && !string.IsNullOrWhiteSpace(t.Title));
            if (covering is not null && (sec >= _detail.Sections.Length || string.IsNullOrWhiteSpace(_detail.Sections[sec].Title)))
                return covering.Title.Trim();
        }
        if (sec < _detail.Sections.Length && !string.IsNullOrWhiteSpace(_detail.Sections[sec].Title))
            return _detail.Sections[sec].Title!.Trim();
        return "";
    }

    // 1:1 MacReaderView.swift:521-535 & ReaderModel.swift:1137-1143：TOC 优先解析章节名 + 全书估算页码
    private void UpdateHeaderStatus()
    {
        var parts = new List<string>();
        if (_detail.Summary.Authors.Length > 0 && !string.IsNullOrWhiteSpace(_detail.Summary.Authors[0]))
            parts.Add(_detail.Summary.Authors[0]);

        string chapterTitle = ResolveSectionTitle(_currentSection);
        if (!string.IsNullOrWhiteSpace(chapterTitle) && (_detail.Toc.Length > 1 || _detail.Sections.Length > 1))
        {
            var (num, _) = SplitChapterTitle(chapterTitle);
            parts.Add(num ?? chapterTitle);
        }

        if (_detail.Summary.Format == BookFormat.Podcast)
        {
            if (_detail.Summary.ListenMinutes > 0)
                parts.Add(L10n.T($"约 {_detail.Summary.ListenMinutes} 分钟"));
        }
        else
        {
            int bookPage = EstimateBookPage(_currentSection, _currentSentence);
            parts.Add(L10n.T($"第 {bookPage} 页"));
        }

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
        if (!_isFullScreen || _immersivePeeking || !_library.Prefs.ImmersiveKeepLine || _detail.Sections.Length == 0)
        {
            ImmersiveProgressLine.Visibility = Visibility.Collapsed;
            return;
        }
        double frac = CalculateBookFraction(_currentSection, _currentSentence);
        ImmersiveProgressLine.Width = Math.Max(4, WebViewContainer.ActualWidth * Math.Clamp(frac, 0, 1));
        ImmersiveProgressLine.Visibility = Visibility.Visible;
    }

    private void UpdateBottomChromeVisibility()
    {
        bool allowPills = !_isFullScreen || _immersivePeeking;
        ReturnPillBtn.Visibility = _returnFrom is not null && allowPills ? Visibility.Visible : Visibility.Collapsed;
        DoubtPillHost.Visibility = _hasActiveDoubtPill && allowPills ? Visibility.Visible : Visibility.Collapsed;

        if (_isFullScreen)
        {
            bool hasAudioBar = _audio.IsPlaying || _hasPausedAudioSession;
            ListenCapsule.Visibility = hasAudioBar && (_immersivePeeking || _listenFlyoutOpen)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private bool IsBookMarksStale()
    {
        if (_marksStaleAcknowledged) return false;
        try
        {
            var detailProp = _detail.GetType().GetProperty("MarksStale") ?? _detail.Summary.GetType().GetProperty("MarksStale");
            if (detailProp?.GetValue(detailProp.DeclaringType == _detail.GetType() ? _detail : _detail.Summary) is bool bProp && bProp)
                return true;

            var coreMethod = _library.Core.GetType().GetMethod("MarksStale", [typeof(string)]);
            if (coreMethod?.Invoke(_library.Core, [_bookId]) is bool bMethod && bMethod)
                return true;

            string staleMarker = Path.Combine(_library.DataDir, "books", _bookId, "marks-stale");
            if (File.Exists(staleMarker)) return true;
        }
        catch { }
        return false;
    }

    private void AcknowledgeMarksBanner_Click(object sender, RoutedEventArgs e)
    {
        _marksStaleAcknowledged = true;
        MarksStaleBanner.Visibility = Visibility.Collapsed;
        try
        {
            var ackMethod = _library.Core.GetType().GetMethod("AcknowledgeMarks", [typeof(string)]);
            ackMethod?.Invoke(_library.Core, [_bookId]);
        }
        catch { }
        try
        {
            string staleMarker = Path.Combine(_library.DataDir, "books", _bookId, "marks-stale");
            if (File.Exists(staleMarker)) File.Delete(staleMarker);
        }
        catch { }
    }

    private void SetChromeShown(bool shown)
    {
        _chromeShown = shown;
        if (!shown)
        {
            _immersivePeeking = false;
            _pointerInBottomPeek = false;
            _immersivePeekTimer?.Stop();
            if (_isFullScreen)
                TopToolbar.Visibility = Visibility.Collapsed;
            UpdateBottomChromeVisibility();
            UpdateImmersiveHairline();
        }
        else
        {
            if (_isFullScreen)
            {
                _immersivePeeking = true;
                TopToolbar.Visibility = Visibility.Visible;
            }
            UpdateBottomChromeVisibility();
            UpdateImmersiveHairline();
        }
    }

    private async void TextVersionPolish_Click(object sender, RoutedEventArgs e)
    {
        var st = AlignCheckAndPolishService.GetPolishState(_bookId);
        if (st.Running)
        {
            AlignCheckAndPolishService.StopTextPolish(_bookId);
            UpdateTopInfoBanners();
            return;
        }
        await AlignCheckAndPolishService.ShowTextPolishDialogAsync(RootGrid.XamlRoot, _library, _bookId, () =>
        {
            UpdateTopInfoBanners();
            if (_pager.IsInitialized && _hasLoadedInitialSection)
            {
                _pendingSentence = _currentSentence;
                LoadSection(_currentSection, forceReload: true);
            }
        });
        UpdateTopInfoBanners();
    }

    private void UpdateTopInfoBanners()
    {
        if (TopInfoBannersStack is null) return;
        if (_isFullScreen && _library.Prefs.ImmersiveHideHints)
        {
            TopInfoBannersStack.Visibility = Visibility.Collapsed;
            return;
        }
        TopInfoBannersStack.Visibility = Visibility.Visible;

        bool isTextVer = false;
        try { isTextVer = _library.Core.AudiobookIsTextVersion(_bookId); } catch { }
        if (isTextVer)
        {
            var (running, done, total, fixedCount, err) = AlignCheckAndPolishService.GetPolishState(_bookId);
            if (running)
            {
                TextVersionBannerText.Text = $"{L10n.T("正在整理")} · {done} / {total} {L10n.T("段")} · {L10n.T("改了")} {fixedCount} {L10n.T("处")}";
                TextVersionPolishBtn.Content = L10n.T("停");
                TextVersionPolishBtn.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrWhiteSpace(err))
            {
                TextVersionBannerText.Text = err;
                TextVersionPolishBtn.Content = L10n.T("整理错字");
                TextVersionPolishBtn.Visibility = _library.Companion.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
            }
            else if (fixedCount > 0)
            {
                TextVersionBannerText.Text = $"{L10n.T("整理过了，改了")} {fixedCount} {L10n.T("处同音字和人名，可能还有错字")}";
                TextVersionPolishBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                TextVersionBannerText.Text = L10n.T("这是录音转出来的文字，可能有错字");
                TextVersionPolishBtn.Content = L10n.T("整理错字");
                TextVersionPolishBtn.Visibility = _library.Companion.IsConfigured ? Visibility.Visible : Visibility.Collapsed;
            }
            TextVersionBanner.Visibility = Visibility.Visible;
        }
        else
        {
            TextVersionBanner.Visibility = Visibility.Collapsed;
        }

        MarksStaleBanner.Visibility = IsBookMarksStale() ? Visibility.Visible : Visibility.Collapsed;

        bool chapterOnlyAudio = _pairedMap is not null
            && _pairedMap.Rows.Length == 0
            && _pairedMap.Chapters.Length > 0
            && (!_audio.HasPairedRecording || _audio.IsUsingPairedRecording);
        ChapterOnlyAudioBanner.Visibility = chapterOnlyAudio ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleFullScreen()
    {
        _isFullScreen = !_isFullScreen;
        _chromeShown = false;
        AppWindow?.SetPresenter(_isFullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        TopToolbar.Visibility = _isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        if (_isFullScreen)
        {
            _pointerInBottomPeek = false;
            if (_activePanel is not null) ClosePanel();
            if (_library.Prefs.ImmersiveHideHints)
            {
                RecapCardHost.Visibility = Visibility.Collapsed;
                SleepReturnHost.Visibility = Visibility.Collapsed;
                if (_pager.IsInitialized) _ = _host.SetGlossesAsync([]);
            }
            UpdateBottomChromeVisibility();
        }
        else
        {
            _immersivePeeking = false;
            _pointerInBottomPeek = false;
            _immersivePeekTimer?.Stop();
            _immersiveKeepShownMarks.Clear();
            if (_audio.IsPlaying) ListenCapsule.Visibility = Visibility.Visible;
            UpdateBottomChromeVisibility();
            if (_library.Prefs.ImmersiveHideHints && _pager.IsInitialized) _ = ApplyForeignReadingAsync();
        }

        UpdateTopInfoBanners();
        if (_layout is not null && _pager.IsInitialized)
        {
            bool hideMarks = _isFullScreen && _library.Prefs.ImmersiveHideMarks;
            var allSectionMarks = _library.GetMarks(_bookId).Where(m => m.Section == _currentSection);
            var marks = hideMarks ? allSectionMarks.Where(m => _immersiveKeepShownMarks.Contains(m.Id)) : allSectionMarks;
            _ = _host.SetMarksAsync(marks);
        }
        UpdatePaperChrome();
    }

    private void ImmersiveBottomPeek_Entered(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFullScreen) return;
        _pointerInBottomPeek = true;
        _immersivePeekTimer?.Stop();
        if (!_immersivePeeking)
        {
            _immersivePeeking = true;
            _chromeShown = true;
            UpdateBottomChromeVisibility();
            UpdateImmersiveHairline();
        }
    }

    private void ImmersiveBottomPeek_Exited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFullScreen) return;
        _pointerInBottomPeek = false;
        ScheduleImmersivePeekHide();
    }

    private void ImmersiveBottomChild_Entered(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFullScreen) return;
        _pointerInBottomPeek = true;
        _immersivePeekTimer?.Stop();
        if (!_immersivePeeking)
        {
            _immersivePeeking = true;
            _chromeShown = true;
            UpdateBottomChromeVisibility();
            UpdateImmersiveHairline();
        }
    }

    private void ImmersiveBottomChild_Exited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFullScreen) return;
        _pointerInBottomPeek = false;
        ScheduleImmersivePeekHide();
    }

    private void ScheduleImmersivePeekHide()
    {
        _immersivePeekTimer?.Stop();
        _immersivePeekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _immersivePeekTimer.Tick += (_, _) =>
        {
            _immersivePeekTimer.Stop();
            if (_pointerInBottomPeek || _listenFlyoutOpen) return;
            _immersivePeeking = false;
            _chromeShown = false;
            UpdateBottomChromeVisibility();
            UpdateImmersiveHairline();
        };
        _immersivePeekTimer.Start();
    }

    private void HandleInternalLink(string target)
    {
        var parts = target.Split('#', 2);
        if (uint.TryParse(parts[0], out uint section) && section < _detail.Sections.Length)
        {
            _hasUserMoved = true;
            if (!_audio.IsPlaying && _hasPausedAudioSession) _pausedOffSpot = true;
            RecordReturnOrigin();
            var anchor = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : null;
            if (section == _currentSection && anchor is not null)
            {
                MarkProgrammaticScroll();
                _ = _host.ScrollToAnchorAsync(anchor, animated: ShouldAnimatePageTurn());
            }
            else
            {
                _pendingAnchor = anchor;
                LoadSection(section);
            }
        }
    }

    private void BackToShelf_Click(object sender, RoutedEventArgs e) => App.CurrentApp.ShowShelf();

    // ---------- Page turns & keyboard ----------

    private bool _finishedPrompted;

    private async Task TurnNextAsync()
    {
        _lastInteractionUtc = DateTime.UtcNow;
        _hasUserMoved = true;
        bool anim = ShouldAnimatePageTurn();
        if (!await _host.NextPageAsync(animated: anim))
        {
            if (_library.NextSection(_bookId, _currentSection) is uint s)
            {
                if (!_audio.IsPlaying && _hasPausedAudioSession) _pausedOffSpot = true;
                _pendingSentence = 0;
                LoadSection(s);
            }
            else await MaybeOfferFinishedSheetAsync();
        }
    }

    private async Task MaybeOfferFinishedSheetAsync()
    {
        if (_finishedPrompted || _detail.Summary.Status == ReadingStatus.Finished || _library.Prefs.FinishedPromptedBooks.Contains(_bookId)) return;
        _finishedPrompted = true;
        _library.Prefs.FinishedPromptedBooks.Add(_bookId);
        _library.SavePrefs();
        await ReaderRecapAndSleepHelper.ShowFinishedSheetAsync(RootGrid.XamlRoot, _library, _detail);
        try { _detail = _library.GetBookDetail(_bookId); } catch { }
    }

    private async Task TurnPrevAsync()
    {
        _lastInteractionUtc = DateTime.UtcNow;
        _hasUserMoved = true;
        bool anim = ShouldAnimatePageTurn();
        if (!await _host.PrevPageAsync(animated: anim))
        {
            if (_library.PreviousSection(_bookId, _currentSection) is uint s)
            {
                if (!_audio.IsPlaying && _hasPausedAudioSession) _pausedOffSpot = true;
                _pendingEndOfSection = true;
                LoadSection(s);
            }
        }
    }

    private CancellationTokenSource? _scrollEdgeCueCts;

    private async Task CrossSectionFromScrollEdgeAsync(bool forward)
    {
        _lastInteractionUtc = DateTime.UtcNow;
        _hasUserMoved = true;
        if (forward)
        {
            if (_library.NextSection(_bookId, _currentSection) is uint s)
            {
                if (!_audio.IsPlaying && _hasPausedAudioSession) _pausedOffSpot = true;
                _pendingSentence = 0;
                LoadSection(s);
                if (ShouldAnimatePageTurn())
                    YYChrome.AnimateEntrance(WebViewSlotHost, fromY: 18, durationMs: 200);
            }
            else
            {
                await MaybeOfferFinishedSheetAsync();
            }
        }
        else if (_library.PreviousSection(_bookId, _currentSection) is uint s)
        {
            if (!_audio.IsPlaying && _hasPausedAudioSession) _pausedOffSpot = true;
            _pendingEndOfSection = true;
            LoadSection(s);
            if (ShouldAnimatePageTurn())
                YYChrome.AnimateEntrance(WebViewSlotHost, fromY: -18, durationMs: 200);
        }
    }

    private void ShowScrollEdgeCue(bool forward, double progress = 0)
    {
        uint? target = forward
            ? _library.NextSection(_bookId, _currentSection)
            : _library.PreviousSection(_bookId, _currentSection);
        if (!forward && target is null) return;

        bool wasHidden = ScrollEdgeCue.Visibility != Visibility.Visible;
        if (target is null)
        {
            ScrollEdgeCueText.Text = L10n.T("已是最后一章");
        }
        else
        {
            string title = ResolveSectionTitle(target.Value);
            string dest = !string.IsNullOrEmpty(title) ? title : L10n.T(forward ? "下一章" : "上一章");
            string verb = progress >= 0.65
                ? L10n.T("松手进入")
                : L10n.T(forward ? "继续向下" : "继续向上");
            ScrollEdgeCueText.Text = $"{(forward ? "↓" : "↑")}  {verb} · {dest}";
        }
        ScrollEdgeCue.VerticalAlignment = forward ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        ScrollEdgeCue.Visibility = Visibility.Visible;
        if (wasHidden)
            YYChrome.AnimateEntrance(ScrollEdgeCue, fromY: forward ? 8 : -8, durationMs: 140);

        _scrollEdgeCueCts?.Cancel();
        _scrollEdgeCueCts?.Dispose();
        var cts = new CancellationTokenSource();
        _scrollEdgeCueCts = cts;
        _ = HideScrollEdgeCueAfterDelayAsync(cts.Token);
    }

    private async Task HideScrollEdgeCueAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(1000, ct);
            if (!ct.IsCancellationRequested) ScrollEdgeCue.Visibility = Visibility.Collapsed;
        }
        catch (TaskCanceledException) { }
    }

    private void HideScrollEdgeCue()
    {
        _scrollEdgeCueCts?.Cancel();
        _scrollEdgeCueCts?.Dispose();
        _scrollEdgeCueCts = null;
        ScrollEdgeCue.Visibility = Visibility.Collapsed;
    }

    private void HandleForwardedWebKey(ReaderMessage msg)
    {
        bool ctrl = msg.Ctrl == true;
        bool shift = msg.Shift == true;
        bool alt = msg.Alt == true;
        string k = msg.Key ?? "";

        if (ctrl && !shift && !alt && k.Equals("z", StringComparison.OrdinalIgnoreCase))
        {
            if (ReaderToastBanner.Visibility == Visibility.Visible && _readerToastAction is not null)
                ReaderToastAction_Click(this, new RoutedEventArgs());
            return;
        }
        if (ctrl && shift && k.Equals("f", StringComparison.OrdinalIgnoreCase)) { ToggleFullScreen(); return; }
        if (ctrl && k.Equals("f", StringComparison.OrdinalIgnoreCase)) { OpenSearch(); return; }
        if (ctrl && shift && k.Equals("k", StringComparison.OrdinalIgnoreCase)) { OpenPanel("companion"); CompanionInputBox.Focus(FocusState.Programmatic); return; }
        if (ctrl && k.Equals("k", StringComparison.OrdinalIgnoreCase)) { _ = QuickSearchDialog.ShowAsync(RootGrid.XamlRoot, _library, _bookId, AskCompanion); return; }
        if (ctrl && k.Equals("g", StringComparison.OrdinalIgnoreCase)) { StepHit(forward: !shift); return; }
        if (ctrl && k == ",") { App.CurrentApp.OpenSettings(); return; }
        if (ctrl && alt && k.Equals("t", StringComparison.OrdinalIgnoreCase)) { ToggleNamedPanel("toc"); return; }
        if (ctrl && alt && k.Equals("n", StringComparison.OrdinalIgnoreCase)) { ToggleNamedPanel("notes"); return; }
        if (ctrl && alt && k == "ArrowUp")
        {
            if (_library.PreviousSection(_bookId, _currentSection) is uint prevSec)
            {
                _hasUserMoved = true;
                if (!_audio.IsPlaying && _hasPausedAudioSession) _pausedOffSpot = true;
                _pendingSentence = 0;
                LoadSection(prevSec);
            }
            return;
        }
        if (ctrl && alt && k == "ArrowDown")
        {
            if (_library.NextSection(_bookId, _currentSection) is uint nextSec)
            {
                _hasUserMoved = true;
                if (!_audio.IsPlaying && _hasPausedAudioSession) _pausedOffSpot = true;
                _pendingSentence = 0;
                LoadSection(nextSec);
            }
            return;
        }
        if (ctrl && (k == "[" || msg.Code == "BracketLeft")) { SpeakPrev_Click(this, new RoutedEventArgs()); return; }
        if (ctrl && (k == "]" || msg.Code == "BracketRight")) { SpeakNext_Click(this, new RoutedEventArgs()); return; }
        if (ctrl && (k.Equals("b", StringComparison.OrdinalIgnoreCase) || k.Equals("d", StringComparison.OrdinalIgnoreCase))) { ToggleBookmark_Click(this, new RoutedEventArgs()); return; }
        if (ctrl && (k == "=" || k == "+")) { _settings = _settings with { FontSize = Math.Min(34, _settings.FontSize + 1) }; PushSettingsToReader(); return; }
        if (ctrl && (k == "-" || k == "_")) { _settings = _settings with { FontSize = Math.Max(12, _settings.FontSize - 1) }; PushSettingsToReader(); return; }

        if (!ctrl && !alt && SelectionCard.Visibility == Visibility.Visible)
        {
            MarkStyle? qs = k switch { "1" => MarkStyle.Yellow, "2" => MarkStyle.Blue, "3" => MarkStyle.Pink, "4" => MarkStyle.Wave, _ => null };
            if (qs is MarkStyle s) { _ = ApplySelectionMarkAsync(s); return; }
        }

        switch (k)
        {
            case "F11": ToggleFullScreen(); break;
            case "ArrowRight":
            case "PageDown":
                _ = TurnNextAsync(); break;
            case "ArrowDown":
                if (_settings.Mode == LayoutMode.Paginated) _ = TurnNextAsync(); break;
            case "ArrowLeft":
            case "PageUp":
                _ = TurnPrevAsync(); break;
            case "ArrowUp":
                if (_settings.Mode == LayoutMode.Paginated) _ = TurnPrevAsync(); break;
            case " ":
                ToggleListen_Click(this, new RoutedEventArgs()); break;
            case "Escape":
                HandleEscapeKey(); break;
        }
    }

    private void HandleEscapeKey()
    {
        if (_listenOnly) { _listenOnly = false; ListenOnlyCover.Visibility = Visibility.Collapsed; }
        else if (HasAnyFloatingCardOpen()) DismissFloatingCards();
        else if (_searchShown) CloseSearch();
        else if (_activePanel is not null) ClosePanel();
        else if (_isFullScreen) ToggleFullScreen();
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space) return;
        if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox) return;
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl || alt) return;
        ToggleListen_Click(sender, e);
        e.Handled = true;
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);

        if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox)
        {
            return;
        }

        if (ctrl && !shift && !alt && e.Key == VirtualKey.Z)
        {
            if (ReaderToastBanner.Visibility == Visibility.Visible && _readerToastAction is not null)
            {
                ReaderToastAction_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            return;
        }
        if (ctrl && shift && e.Key == VirtualKey.F) { ToggleFullScreen(); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.F) { OpenSearch(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == VirtualKey.K) { OpenPanel("companion"); CompanionInputBox.Focus(FocusState.Programmatic); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.K) { _ = QuickSearchDialog.ShowAsync(RootGrid.XamlRoot, _library, _bookId, AskCompanion); e.Handled = true; return; }
        if (ctrl && e.Key == VirtualKey.G) { StepHit(forward: !shift); e.Handled = true; return; }
        if (ctrl && e.Key == (VirtualKey)188) { App.CurrentApp.OpenSettings(); e.Handled = true; return; } // Ctrl+,
        if (ctrl && alt && e.Key == VirtualKey.T) { ToggleNamedPanel("toc"); e.Handled = true; return; }
        if (ctrl && alt && e.Key == VirtualKey.N) { ToggleNamedPanel("notes"); e.Handled = true; return; }
        if (ctrl && alt && e.Key == VirtualKey.Up)
        {
            if (_library.PreviousSection(_bookId, _currentSection) is uint prevSec)
            {
                _hasUserMoved = true;
                if (!_audio.IsPlaying && _hasPausedAudioSession) _pausedOffSpot = true;
                _pendingSentence = 0;
                LoadSection(prevSec);
            }
            e.Handled = true;
            return;
        }
        if (ctrl && alt && e.Key == VirtualKey.Down)
        {
            if (_library.NextSection(_bookId, _currentSection) is uint nextSec)
            {
                _hasUserMoved = true;
                if (!_audio.IsPlaying && _hasPausedAudioSession) _pausedOffSpot = true;
                _pendingSentence = 0;
                LoadSection(nextSec);
            }
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
            case VirtualKey.Down:
                if (_settings.Mode == LayoutMode.Paginated) { _ = TurnNextAsync(); e.Handled = true; } break;
            case VirtualKey.Left:
            case VirtualKey.PageUp:
                _ = TurnPrevAsync(); e.Handled = true; break;
            case VirtualKey.Up:
                if (_settings.Mode == LayoutMode.Paginated) { _ = TurnPrevAsync(); e.Handled = true; } break;
            case VirtualKey.Space:
                ToggleListen_Click(sender, e); e.Handled = true; break;
            case VirtualKey.Escape:
                HandleEscapeKey();
                e.Handled = true;
                break;
        }
    }

    // ---------- Typeset actions ----------

    private void PaperChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        bool save = PaperAppearance.PickPreset(_library, ParsePaper(id), _settings, out var updated);
        _settings = updated;
        PushSettingsToReader(save: save);
        if (!_ownSettings) App.CurrentApp.BroadcastReaderSettingsChanged();
    }

    private void FollowAppearanceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializingControls) return;
        bool save = PaperAppearance.SetFollow(_library, FollowAppearanceToggle.IsOn, _settings, out var updated);
        _settings = updated;
        PushSettingsToReader(save: save);
        App.CurrentApp.BroadcastReaderSettingsChanged();
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

    private void FontSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_initializingControls) return;
        double rounded = Math.Clamp(Math.Round(e.NewValue), 12, 34);
        if (Math.Abs(rounded - Math.Round(_settings.FontSize)) < 0.5) return;
        _settings = _settings with { FontSize = rounded };
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
            _ = ApplySectionBubblesAsync();
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
        TypesetPaperEditorPanel.Visibility = Visibility.Collapsed;
        TypesetAllPapersPanel.Visibility = Visibility.Collapsed;
        TypesetMorePanel.Visibility = Visibility.Visible;
        YYChrome.AnimateSlideX(TypesetMorePanel, fromX: 16, durationMs: 160);
    }

    private void TypesetBack_Click(object sender, RoutedEventArgs e)
    {
        TypesetMorePanel.Visibility = Visibility.Collapsed;
        TypesetPaperEditorPanel.Visibility = Visibility.Collapsed;
        TypesetAllPapersPanel.Visibility = Visibility.Collapsed;
        TypesetMainPanel.Visibility = Visibility.Visible;
        ApplyTypesetControlsFromSettings();
        YYChrome.AnimateSlideX(TypesetMainPanel, fromX: -14, durationMs: 150);
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
            _detail = _library.GetBookDetail(_bookId);
            ApplyTypesetControlsFromSettings();
            _pendingSentence = _currentSentence;
            LoadSection(_currentSection, forceReload: true);
        }
        catch { }
    }

    private async void ToggleTranslate_Click(object sender, RoutedEventArgs e)
    {
        if (_translationsActive)
        {
            _translateCts?.Cancel();
            _translationsActive = false;
            _library.Prefs.BookTranslationsActive[_bookId] = false;
            _library.SavePrefs();
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
        _library.Prefs.BookTranslationsActive[_bookId] = true;
        _library.SavePrefs();
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

        if (uncached.Count == 0) return;
        if (!_library.Companion.IsConfigured)
        {
            _translationsActive = false;
            _library.Prefs.BookTranslationsActive[_bookId] = false;
            _library.SavePrefs();
            if (results.Count == 0)
            {
                try { await _host.SetTranslationsAsync(null); } catch { }
            }
            ApplyTintAndChrome();
            ShowReaderToast("翻译暂不可用");
            return;
        }

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
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (!cts.IsCancellationRequested)
                {
                    _translationsActive = false;
                    _library.Prefs.BookTranslationsActive[_bookId] = false;
                    _library.SavePrefs();
                    try { await _host.SetTranslationsAsync(null); } catch { }
                    ApplyTintAndChrome();
                    ShowReaderToast("翻译暂不可用");
                }
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

    private bool HasAnyFloatingCardOpen() =>
        SelectionCard.Visibility == Visibility.Visible
        || WordCard.Visibility == Visibility.Visible
        || SentenceCard.Visibility == Visibility.Visible
        || FootnoteCard.Visibility == Visibility.Visible
        || RecapCardHost.Visibility == Visibility.Visible
        || CompanionSlashPopup.Visibility == Visibility.Visible
        || CompanionReadyLinesCard.Visibility == Visibility.Visible;

    private bool DismissFloatingCards()
    {
        bool hadOpen = HasAnyFloatingCardOpen();
        try { _wordLookupAiCts?.Cancel(); } catch { }
        HideSelectionCard();
        WordCard.Visibility = Visibility.Collapsed;
        DictEntryWebView.Visibility = Visibility.Collapsed;
        _dictFullExpanded = false;
        SentenceCard.Visibility = Visibility.Collapsed;
        FootnoteCard.Visibility = Visibility.Collapsed;
        RecapCardHost.Visibility = Visibility.Collapsed;
        CompanionSlashPopup.Visibility = Visibility.Collapsed;
        CompanionReadyLinesCard.Visibility = Visibility.Collapsed;
        _slashManualOpen = false;
        _lookupWord = null;
        _lookupEntry = null;
        _openFootnote = null;
        if (_pager.IsInitialized) _ = _host.SetLookingAsync(null, null);
        return hadOpen;
    }

    public void ReloadForeignPrefs() => _ = ApplyForeignReadingAsync();

    private async Task ApplyForeignReadingAsync()
    {
        if (!_pager.IsInitialized) return;
        try
        {
            if (!_isForeignBook)
            {
                await _host.SetLookupAsync(false);
                return;
            }

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

            string lang = (_detail.Summary.Language ?? "").Trim();
            if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                && !(_isFullScreen && _library.Prefs.ImmersiveHideHints)
                && _library.Prefs.ForeignGlossEnabled
                && (_library.Prefs.GlossLevel > 0 || _library.Prefs.GlossVocabOnly))
            {
                byte lvl = _library.Prefs.GlossLevel > 0 ? _library.Prefs.GlossLevel : (byte)3;
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

    private static void BroadcastReplaceRulesChanged(string changedBookId)
    {
        ReaderWindow[] snapshot;
        lock (ActiveReaders)
        {
            snapshot = ActiveReaders.ToArray();
        }
        foreach (var win in snapshot)
        {
            win.DispatcherQueue.TryEnqueue(win.ReloadAfterReplaceRulesChanged);
        }
    }

    private void ReloadAfterReplaceRulesChanged()
    {
        if (!_pager.IsInitialized) return;
        try { _detail = _library.GetBookDetail(_bookId); } catch { }
        _pendingSentence = _currentSentence;
        LoadSection(_currentSection, forceReload: true);
        if (_audio.IsPlaying || _hasPausedAudioSession || _audio.CurrentSentence >= 0)
        {
            try
            {
                _audio.UpdateSentencesInPlace(_library.GetSpokenSentences(_bookId, _currentSection));
            }
            catch { }
        }
        if (_searchShown && !string.IsNullOrWhiteSpace(BookSearchBox.Text))
        {
            _ = RunSearchAsync();
        }
    }

    private async void ReplaceRules_Click(object sender, RoutedEventArgs e)
    {
        TypesetBtn.Flyout?.Hide();
        bool liveBroadcasted = false;
        bool changed = await BookToolsDialogs.ShowReplaceRulesDialogAsync(
            RootGrid.XamlRoot,
            _library,
            _bookId,
            _detail.Summary.Title,
            onRulesChanged: () =>
            {
                liveBroadcasted = true;
                BroadcastReplaceRulesChanged(_bookId);
            });
        if (changed && !liveBroadcasted)
        {
            BroadcastReplaceRulesChanged(_bookId);
        }
    }

    private async void TxtLayout_Click(object sender, RoutedEventArgs e)
    {
        TypesetBtn.Flyout?.Hide();
        await OpenTxtLayoutDialogAsync();
    }

    private async Task OpenTxtLayoutDialogAsync()
    {
        bool rebuilt = await BookToolsDialogs.ShowTxtLayoutDialogAsync(
            RootGrid.XamlRoot,
            _library,
            _bookId,
            _detail.Summary.Title,
            sec => JumpTo(sec, 0),
            beforeRebuild: () =>
            {
                SaveCurrentProgress();
                _audio.Stop();
                _hasPausedAudioSession = false;
                _pausedOffSpot = false;
                _lastFollowedSentence = -1;
                ListenCapsule.Visibility = Visibility.Collapsed;
            });

        if (rebuilt)
        {
            _detail = _library.GetBookDetail(_bookId);
            _marksStaleAcknowledged = false;
            var prog = _library.GetProgress(_bookId);
            uint targetSec = prog is not null && prog.Position.Section < _detail.Sections.Length
                ? prog.Position.Section
                : Math.Min(_detail.Start, (uint)Math.Max(0, _detail.Sections.Length - 1));
            _pendingSentence = prog is not null ? (int)prog.Position.Sentence : 0;
            _audio.Stop();
            _audio.SetBookContext(_bookId, _detail.Summary.Language);
            _audio.SetSectionContext(targetSec, _dramaModeOn);
            if (_activePanel is "toc" or "notes" or "cast") RenderSidePanel();
            UpdateTopInfoBanners();
            LoadSection(targetSec, forceReload: true);
            App.CurrentApp.NotifyShelfRefresh();
        }
    }

    private void ShowSelectionCard(ReaderRect rect, bool editingExistingMark)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        MarkStyle? activeStyle = editingExistingMark ? _openMark?.Style : null;
        var accentBrush = ThemeHelper.Brush(tint.Accent);
        var clearBrush = new SolidColorBrush(Colors.Transparent);
        var t1Brush = ThemeHelper.Brush(p.T1);
        var insetBrush = ThemeHelper.Brush(p.Inset);
        var softBrush = ThemeHelper.Brush(tint.Soft);

        SwatchYellowRing.BorderBrush = activeStyle == MarkStyle.Yellow ? accentBrush : clearBrush;
        SwatchBlueRing.BorderBrush = activeStyle == MarkStyle.Blue ? accentBrush : clearBrush;
        SwatchPinkRing.BorderBrush = activeStyle == MarkStyle.Pink ? accentBrush : clearBrush;
        SwatchWaveRing.BorderBrush = activeStyle == MarkStyle.Wave ? accentBrush : clearBrush;

        bool underlineActive = activeStyle == MarkStyle.Underline;
        LineUnderlineBtn.Background = underlineActive ? softBrush : insetBrush;
        LineUnderlineBtn.BorderBrush = ThemeHelper.Brush(underlineActive ? tint.Accent : p.Line);
        LineUnderlineBtn.BorderThickness = new Thickness(underlineActive ? 1.5 : 1);
        LineUnderlineAText.Foreground = underlineActive ? accentBrush : t1Brush;
        LineUnderlineBar.Background = underlineActive ? accentBrush : t1Brush;

        bool dottedActive = activeStyle == MarkStyle.Dotted;
        LineDottedBtn.Background = dottedActive ? softBrush : insetBrush;
        LineDottedBtn.BorderBrush = ThemeHelper.Brush(dottedActive ? tint.Accent : p.Line);
        LineDottedBtn.BorderThickness = new Thickness(dottedActive ? 1.5 : 1);
        LineDottedAText.Foreground = dottedActive ? accentBrush : t1Brush;
        var dotBrush = dottedActive ? accentBrush : t1Brush;
        LineDot1.Fill = dotBrush;
        LineDot2.Fill = dotBrush;
        LineDot3.Fill = dotBrush;
        LineDot4.Fill = dotBrush;

        bool hasNote = editingExistingMark && !string.IsNullOrWhiteSpace(_openMark?.Note);
        SelectionNoteSection.Visibility = hasNote ? Visibility.Visible : Visibility.Collapsed;
        SelectionNotePreviewText.Text = _openMark?.Note ?? "";
        ThoughtCardLabel.Text = L10n.T(hasNote ? "改想法" : "想法");
        DeleteMarkCardBtn.Visibility = editingExistingMark ? Visibility.Visible : Visibility.Collapsed;

        // Dynamic bottom action row: 对准 (when paired audio plays & selecting unhighlighted text) + gate 翻译 on configured AI
        bool canPin = !editingExistingMark
            && _audio.IsPlaying
            && _audio.IsUsingPairedRecording
            && !string.IsNullOrEmpty(_pairedAudioBookId)
            && _selection?.Start is not null;
        bool canTranslate = _library.Companion.IsConfigured;
        SelPinBtn.Visibility = canPin ? Visibility.Visible : Visibility.Collapsed;
        SelTransBtn.Visibility = canTranslate ? Visibility.Visible : Visibility.Collapsed;

        var allActionBtns = new[] { SelThoughtBtn, SelSpeakBtn, SelPinBtn, SelAskBtn, SelTransBtn, SelCopyBtn };
        var visibleBtns = allActionBtns.Where(b => b.Visibility == Visibility.Visible).ToArray();
        SelectionActionsGrid.ColumnDefinitions.Clear();
        for (int ci = 0; ci < visibleBtns.Length; ci++)
        {
            SelectionActionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(visibleBtns[ci], ci);
        }

        double cardW = 332;
        double cardH = hasNote ? 154 : 106;
        double anchorX = rect.X + Math.Max(2, rect.Width) * 0.5;
        double maxX = Math.Max(12, WebViewContainer.ActualWidth - cardW - 12);
        double x = Math.Clamp(anchorX - cardW * 0.5, 12, maxX);
        bool placeAbove = rect.Y > cardH + 24;
        double y = placeAbove ? Math.Max(60, rect.Y - cardH - 12) : rect.Y + rect.Height + 12;
        SelectionCard.Margin = new Thickness(x, y, 0, 0);
        SelectionCard.Visibility = Visibility.Visible;

        double ptrX = Math.Clamp(anchorX - 5.5, x + 18, x + cardW - 29);
        double ptrY = placeAbove ? (y + cardH - 6) : (y - 5.5);
        SelectionCardPointer.Background = ThemeHelper.Brush(p.Card);
        SelectionCardPointer.BorderBrush = ThemeHelper.Brush(p.Line);
        SelectionCardPointer.BorderThickness = placeAbove ? new Thickness(0, 0, 1, 1) : new Thickness(1, 1, 0, 0);
        SelectionCardPointer.Margin = new Thickness(ptrX, ptrY, 0, 0);
        SelectionCardPointer.Visibility = Visibility.Visible;
    }

    private void HideSelectionCard()
    {
        SelectionCard.Visibility = Visibility.Collapsed;
        SelectionCardPointer.Visibility = Visibility.Collapsed;
        _selection = null;
        _openMark = null;
    }

    private void PinSelectionAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_selection?.Start is not { } pt) return;
        uint sent = (uint)Math.Max(0, pt.S);
        HideSelectionCard();
        _ = _host.ClearSelectionAsync();
        PinPairedRecordingHere(sent);
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

    private void SelectionNotePreview_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (_openMark is not null)
            AddThought_Click(sender, new RoutedEventArgs());
    }

    private MarkStyle RememberedHighlightStyle() => Enum.TryParse<MarkStyle>(_library.Prefs.LastHighlightStyle, true, out var st) ? st : MarkStyle.Yellow;

    private async Task ApplySelectionMarkAsync(MarkStyle style)
    {
        _library.Prefs.LastHighlightStyle = style.ToString();
        _library.SavePrefs();

        if (_openMark is not null)
        {
            _library.SetMarkStyle(_bookId, _openMark.Id, style);
            if (_isFullScreen) _immersiveKeepShownMarks.Add(_openMark.Id);
            HideSelectionCard();
            await RefreshSectionMarksAsync();
            return;
        }
        if (_selection?.Start is null || _selection.End is null || string.IsNullOrWhiteSpace(_selection.Text)) return;

        var created = _library.AddMark(_bookId, _currentSection,
            new TextPoint((uint)_selection.Start.S, (uint)_selection.Start.O),
            new TextPoint((uint)_selection.End.S, (uint)_selection.End.O),
            _selection.Text, style);
        if (_isFullScreen && created is not null) _immersiveKeepShownMarks.Add(created.Id);

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
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        string actionTaken = "cancel"; // cancel | save | remove_note | delete_mark
        MarkStyle chosenStyle = existingMark?.Style ?? RememberedHighlightStyle();
        ContentDialog? dialog = null;

        string chapTitle = ResolveSectionTitle(_currentSection);
        string chapSub = !string.IsNullOrWhiteSpace(chapTitle)
            ? $"{_detail.Summary.Title} · {chapTitle}"
            : _detail.Summary.Title;

        // 1:1 ReaderCards.swift ThoughtSheet: vertical-bar quote preview + 6pt white-dot color picker + input card + 移除想法/删掉划线 + Ctrl+Enter
        var quoteBar = new Grid { ColumnSpacing = 10, Margin = new Thickness(2, 2, 2, 2) };
        quoteBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.5) });
        quoteBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var quoteAccentBar = new Border { CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(tint.Accent) };
        quoteBar.Children.Add(quoteAccentBar);
        var qText = new TextBlock
        {
            Text = $"“{quote.Trim()}”",
            FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
            FontSize = 14,
            LineHeight = 22,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(qText, 1);
        quoteBar.Children.Add(qText);

        var styleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(2, 0, 2, 0) };
        void RebuildStyleSwatches()
        {
            styleRow.Children.Clear();
            (MarkStyle St, Color Fill, string Tip)[] colorOpts =
            [
                (MarkStyle.Yellow, Color.FromArgb(255, 232, 189, 76), L10n.T("黄色划线")),
                (MarkStyle.Blue, Color.FromArgb(255, 108, 174, 214), L10n.T("蓝色划线")),
                (MarkStyle.Pink, Color.FromArgb(255, 224, 134, 128), L10n.T("粉色划线")),
                (MarkStyle.Wave, Color.FromArgb(255, 239, 127, 113), L10n.T("波浪线")),
                (MarkStyle.Underline, tint.Accent, L10n.T("实线下划线")),
                (MarkStyle.Dotted, p.T2, L10n.T("点线下划线")),
            ];
            foreach (var (st, col, tip) in colorOpts)
            {
                bool on = chosenStyle == st;
                var circle = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = ThemeHelper.Brush(col),
                    BorderBrush = ThemeHelper.Brush(Color.FromArgb(36, 0, 0, 0)),
                    BorderThickness = new Thickness(0.5),
                    Child = on
                        ? new Border
                        {
                            Width = 6,
                            Height = 6,
                            CornerRadius = new CornerRadius(3),
                            Background = new SolidColorBrush(Colors.White),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        }
                        : null,
                };
                var sb = new Button
                {
                    Padding = new Thickness(1),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = circle,
                };
                ToolTipService.SetToolTip(sb, tip);
                var capturedSt = st;
                sb.Click += (_, _) =>
                {
                    chosenStyle = capturedSt;
                    RebuildStyleSwatches();
                };
                styleRow.Children.Add(sb);
            }
        }
        RebuildStyleSwatches();

        var input = new TextBox
        {
            Text = existingMark?.Note ?? "",
            PlaceholderText = L10n.T("写下此刻的想法…"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 104,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 13.5,
            Padding = new Thickness(12, 10, 12, 10),
        };
        var inputCard = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Child = input,
        };

        var counterTb = new TextBlock { Text = $"{input.Text.Length} {L10n.T("字")}", FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
        input.TextChanged += (_, _) => counterTb.Text = $"{input.Text.Length} {L10n.T("字")}";
        void OnThoughtInputKey(object s, KeyRoutedEventArgs ke)
        {
            bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            if (ctrl && ke.Key == VirtualKey.Enter)
            {
                ke.Handled = true;
                actionTaken = "save";
                dialog?.Hide();
            }
        }
        input.PreviewKeyDown += OnThoughtInputKey;
        input.KeyDown += OnThoughtInputKey;

        var footLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        footLeft.Children.Add(counterTb);
        if (existingMark is not null)
        {
            bool hasExistingNote = !string.IsNullOrWhiteSpace(existingMark.Note);
            var delBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T(hasExistingNote ? "移除想法" : "删掉划线"),
                Foreground = ThemeHelper.Brush(p.Danger),
                FontSize = 12,
                Padding = new Thickness(6, 2, 6, 2),
            };
            delBtn.Click += (_, _) =>
            {
                actionTaken = hasExistingNote ? "remove_note" : "delete_mark";
                dialog?.Hide();
            };
            footLeft.Children.Add(delBtn);
        }

        var saveSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        saveSp.Children.Add(new TextBlock { Text = L10n.T("保存"), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
        saveSp.Children.Add(new TextBlock { Text = "Ctrl+↵", FontSize = 11, Foreground = ThemeHelper.Brush(tint.Accent), Opacity = 0.72, VerticalAlignment = VerticalAlignment.Center });

        var footRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        footRight.Children.Add(YYChrome.MakeSheetPillButton(L10n.T("取消"), () => dialog?.Hide(), dark, _library.Prefs.Tint, "secondary", height: 36, horizontalPadding: 18));
        footRight.Children.Add(YYChrome.MakeSheetPillButton(saveSp, () => { actionTaken = "save"; dialog?.Hide(); }, dark, _library.Prefs.Tint, "warm", height: 36, horizontalPadding: 18));

        var footRow = new Grid { Margin = new Thickness(2, 2, 0, 0) };
        footRow.Children.Add(footLeft);
        footRow.Children.Add(footRight);

        var body = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(22, 18, 22, 18),
            Children =
            {
                YYChrome.MakeSheetHeader(L10n.T("想法"), chapSub, () => dialog?.Hide(), dark, titleSize: 18),
                quoteBar,
                styleRow,
                inputCard,
                footRow,
            },
        };

        dialog = YYChrome.CreateMacSheet(RootGrid.XamlRoot, dark, 472, body, tint: _library.Prefs.Tint);
        dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
        await dialog.ShowSafeAsync(_library.Prefs.Tint);

        if (actionTaken == "remove_note" && existingMark is not null)
        {
            _library.SetMarkNote(_bookId, existingMark.Id, null);
            HideSelectionCard();
            await RefreshSectionMarksAsync();
            return;
        }
        if (actionTaken == "delete_mark" && existingMark is not null)
        {
            string deletedId = existingMark.Id;
            _library.DeleteMark(_bookId, deletedId);
            HideSelectionCard();
            await RefreshSectionMarksAsync();
            return;
        }
        if (actionTaken == "save")
        {
            _library.Prefs.LastHighlightStyle = chosenStyle.ToString();
            _library.SavePrefs();
            string? note = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim();
            if (existingMark is not null)
            {
                if (existingMark.Style != chosenStyle)
                    _library.SetMarkStyle(_bookId, existingMark.Id, chosenStyle);
                _library.SetMarkNote(_bookId, existingMark.Id, note);
                if (_isFullScreen) _immersiveKeepShownMarks.Add(existingMark.Id);
            }
            else if (sel?.Start is not null && sel.End is not null)
            {
                var created = _library.AddMark(_bookId, _currentSection,
                    new TextPoint((uint)sel.Start.S, (uint)sel.Start.O),
                    new TextPoint((uint)sel.End.S, (uint)sel.End.O),
                    quote, chosenStyle, note);
                if (_isFullScreen && created is not null) _immersiveKeepShownMarks.Add(created.Id);
            }

            HideSelectionCard();
            await _host.ClearSelectionAsync();
            await RefreshSectionMarksAsync();
        }
    }

    private async void DeleteOpenMark_Click(object sender, RoutedEventArgs e)
    {
        if (_openMark is null) return;
        string deletedId = _openMark.Id;
        _library.DeleteMark(_bookId, deletedId);
        HideSelectionCard();
        await RefreshSectionMarksAsync();
        ShowReaderToast("已删除划线", "撤销", async () =>
        {
            try
            {
                _library.Core.RestoreMark(_bookId, deletedId);
                await RefreshSectionMarksAsync();
            }
            catch { }
        });
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
        uint markSec = _openMark?.Section ?? _currentSection;
        if (string.IsNullOrWhiteSpace(quote)) return;
        HideSelectionCard();
        await _host.ClearSelectionAsync();
        await QuoteShareDialog.ShowAsync(
            this,
            RootGrid.XamlRoot,
            quote,
            note,
            _detail.Summary.Title,
            _detail.Summary.Authors.FirstOrDefault(),
            _detail.Summary.CoverPath,
            chapter: GetChapterTitleForSection(markSec),
            library: _library);
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
        ShowReaderToast("已复制选中内容");
    }

    private static readonly Regex PosPrefixRegex = new(
        @"^(n\.|v\.|vt\.|vi\.|adj\.|adv\.|prep\.|conj\.|pron\.|art\.|num\.|int\.|abbr\.|pl\.|aux\.)\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void SetFormattedMeaningInlines(TextBlock target, string summary, YYPalette p)
    {
        target.Inlines.Clear();
        if (string.IsNullOrWhiteSpace(summary)) return;
        string[] lines = summary.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) target.Inlines.Add(new LineBreak());
            var m = PosPrefixRegex.Match(lines[i]);
            if (m.Success)
            {
                target.Inlines.Add(new Run
                {
                    Text = m.Groups[1].Value + " ",
                    FontFamily = new FontFamily("Georgia"),
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    Foreground = ThemeHelper.Brush(p.T3),
                });
                target.Inlines.Add(new Run
                {
                    Text = m.Groups[2].Value,
                    Foreground = ThemeHelper.Brush(p.T1),
                });
            }
            else
            {
                target.Inlines.Add(new Run
                {
                    Text = lines[i],
                    Foreground = ThemeHelper.Brush(p.T1),
                });
            }
        }
    }

    private void UpdateAddVocabButtonVisuals(bool activeInVocab, bool wasKnown)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        AddVocabBtn.IsEnabled = true;
        if (activeInVocab)
        {
            AddVocabBtn.Style = (Style)Application.Current.Resources["YYPillButtonStyle"];
            AddVocabBtn.Background = ThemeHelper.Brush(p.Inset);
            AddVocabBtn.Foreground = ThemeHelper.Brush(p.T2);
            AddVocabBtn.BorderThickness = new Thickness(0);
            AddVocabBtn.Content = L10n.T("✓ 在生词本里");
        }
        else
        {
            AddVocabBtn.Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"];
            AddVocabBtn.Background = ThemeHelper.Brush(tint.Black);
            AddVocabBtn.Foreground = ThemeHelper.Brush(tint.OnBlack);
            AddVocabBtn.Content = L10n.T(wasKnown ? "+ 重新加入生词本" : "+ 加入生词本");
        }
    }

    private void ShowWordLookup(ReaderWord word)
    {
        try { _wordLookupAiCts?.Cancel(); _wordLookupAiCts?.Dispose(); } catch { }
        _wordLookupAiCts = null;

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);

        _lookupWord = word;
        _lookupEntry = _library.LookUpWordRich(word.Text);
        string lang = (_detail.Summary.Language ?? "").Trim();
        if (_lookupEntry is null && _isForeignBook && lang.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var secGloss = _library.GetGlosses(_bookId, _currentSection, 6, false)
                    .FirstOrDefault(g => g.Form.Equals(word.Text, StringComparison.OrdinalIgnoreCase));
                if (secGloss is not null && !string.IsNullOrWhiteSpace(secGloss.GlossValue))
                    _lookupEntry = new WordLookupResult(word.Text.ToLowerInvariant(), null, secGloss.GlossValue, L10n.T("行间释义"));
            }
            catch { }
        }
        _dictFullExpanded = false;
        DictEntryWebView.Visibility = Visibility.Collapsed;
        WordCardMeaning.MaxLines = 4;

        string headword = _lookupEntry?.Headword ?? word.Text;
        WordCardHeadword.Text = headword;
        WordCardPhonetic.Text = !string.IsNullOrWhiteSpace(_lookupEntry?.Phonetic)
            ? $"/{_lookupEntry.Phonetic.Trim('/', '[', ']')}/"
            : "";

        string missingDictHint = L10n.T("本地词典暂无此词条。可在「设置 → 外文」导入 MDict (.mdx) 词典。");
        bool aiFallbackPending = _lookupEntry is null && _library.Companion.IsConfigured;
        if (_lookupEntry is not null)
        {
            SetFormattedMeaningInlines(WordCardMeaning, _lookupEntry.Summary, p);
        }
        else
        {
            WordCardMeaning.Inlines.Clear();
            WordCardMeaning.Text = aiFallbackPending ? L10n.T("正在结合语境释义…") : missingDictHint;
        }

        bool hasHtml = !string.IsNullOrWhiteSpace(_lookupEntry?.Html);
        bool hasLongSummary = (_lookupEntry?.Summary?.Count(c => c == '\n') ?? 0) >= 4 || (_lookupEntry?.Summary?.Length ?? 0) > 120;
        FullDictEntryBtn.Visibility = (hasHtml || hasLongSummary) ? Visibility.Visible : Visibility.Collapsed;
        FullDictEntryBtn.Content = L10n.T("更多释义");

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

        string freqText = L10n.T("本书只出现这一次");
        try
        {
            uint bookTimes = _library.Core.BookWordCount(_bookId, headword);
            if (bookTimes == 0 && !string.Equals(headword, word.Text, StringComparison.Ordinal))
                bookTimes = _library.Core.BookWordCount(_bookId, word.Text);
            if (bookTimes > 1) freqText = $"{L10n.T("本书出现")} {bookTimes:N0} {L10n.T("次")}";
        }
        catch { }
        WordCardSourceText.Text = $"{(_lookupEntry?.Source ?? (aiFallbackPending ? L10n.T("书伴语境释义") : L10n.T("查词")))} · {freqText}";

        var existingVocab = _library.GetWords().FirstOrDefault(w =>
            string.Equals(w.Word, headword, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(w.Word, word.Text, StringComparison.OrdinalIgnoreCase));
        bool activeInVocab = existingVocab is not null && !existingVocab.Known;
        UpdateAddVocabButtonVisuals(activeInVocab, existingVocab?.Known == true);

        _ = _host.SetLookingAsync(word.Start, word.End);

        double maxX = Math.Max(12, WebViewContainer.ActualWidth - 360);
        double x = Math.Clamp(word.Rect.X + word.Rect.Width * 0.5 - 173, 12, maxX);
        double y = word.Rect.Y + word.Rect.Height + 10;
        if (y + 190 > WebViewContainer.ActualHeight) y = Math.Max(60, word.Rect.Y - 180);
        WordCard.Margin = new Thickness(x, y, 0, 0);
        WordCard.Visibility = Visibility.Visible;

        // 1:1 ReaderForeign.swift:37 — 查词时自动用英音/美音系统发音朗读 (0.9×)
        if (_library.Prefs.ForeignSpeakOnLookup)
        {
            _ = AudioPlayer.SpeakForeignWordAsync(_library, word.Text, _detail.Summary.Language);
        }

        if (aiFallbackPending)
        {
            var aiCts = new CancellationTokenSource();
            _wordLookupAiCts = aiCts;
            _ = LookupWordViaCompanionAsync(word, freqText, missingDictHint, aiCts.Token);
        }
    }

    private async Task LookupWordViaCompanionAsync(ReaderWord word, string freqText, string fallbackHint, CancellationToken ct)
    {
        try
        {
            string contextPart = !string.IsNullOrWhiteSpace(word.Sentence)
                ? $"\n所在句子：{word.Sentence.Trim()}"
                : "";
            string reply = await _library.Companion.ChatAsync(
                $"请结合下面语境，用简明中文给出词语「{word.Text}」在此处的准确词性与释义（若是变形词可顺带标注原型，控制在 45 字以内，直接输出释义，不要寒暄）：{contextPart}",
                "你是精炼准确的双语词典助手，只输出简短中文语境释义。");
            if (ct.IsCancellationRequested || !ReferenceEquals(_lookupWord, word) || WordCard.Visibility != Visibility.Visible)
                return;

            string clean = reply.Trim();
            if (!string.IsNullOrEmpty(clean))
            {
                bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
                _lookupEntry = new WordLookupResult(word.Text, null, clean, L10n.T("书伴语境释义"));
                SetFormattedMeaningInlines(WordCardMeaning, clean, ThemeHelper.Palette(dark));
                WordCardSourceText.Text = $"{L10n.T("书伴语境释义")} · {freqText}";
            }
            else
            {
                WordCardMeaning.Inlines.Clear();
                WordCardMeaning.Text = fallbackHint;
                WordCardSourceText.Text = $"{L10n.T("查词")} · {freqText}";
            }
        }
        catch
        {
            if (!ct.IsCancellationRequested && ReferenceEquals(_lookupWord, word) && WordCard.Visibility == Visibility.Visible)
            {
                WordCardMeaning.Inlines.Clear();
                WordCardMeaning.Text = fallbackHint;
                WordCardSourceText.Text = $"{L10n.T("查词")} · {freqText}";
            }
        }
    }

    private static string? ExtractWordPhrase(string? sentence, string word)
    {
        if (string.IsNullOrWhiteSpace(sentence) || string.IsNullOrWhiteSpace(word)) return null;
        var tokens = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int idx = Array.FindIndex(tokens, t => t.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return null;
        if (tokens.Length <= 8) return sentence.Trim();
        int from = Math.Max(0, idx - 3);
        int to = Math.Min(tokens.Length - 1, idx + 2);
        string core = string.Join(" ", tokens[from..(to + 1)]).Trim(',', ';', ':', '“', '”', '"', '\'', '‘', '’');
        return (from > 0 ? "…" : "") + core + (to < tokens.Length - 1 ? "…" : "");
    }

    private async void ToggleFullDictEntry_Click(object sender, RoutedEventArgs e)
    {
        _dictFullExpanded = !_dictFullExpanded;
        FullDictEntryBtn.Content = L10n.T(_dictFullExpanded ? "收起释义" : "更多释义");
        WordCardMeaning.MaxLines = _dictFullExpanded ? 24 : 4;

        if (!_dictFullExpanded || string.IsNullOrWhiteSpace(_lookupEntry?.Html))
        {
            DictEntryWebView.Visibility = Visibility.Collapsed;
            return;
        }

        DictEntryWebView.Visibility = Visibility.Visible;
        if (_lookupWord is not null)
        {
            double x = WordCard.Margin.Left;
            double maxY = Math.Max(60, WebViewContainer.ActualHeight - 440);
            if (WordCard.Margin.Top > maxY)
                WordCard.Margin = new Thickness(x, maxY, 0, 0);
        }
        try
        {
            if (!_dictWebReady)
            {
                var env = await ReaderWebViewHost.GetSharedEnvironmentAsync(_library.DataDir);
                await DictEntryWebView.EnsureCoreWebView2Async(env);
                DictEntryWebView.CoreWebView2.Settings.IsScriptEnabled = false;
                DictEntryWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                DictEntryWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                DictEntryWebView.CoreWebView2.Settings.IsWebMessageEnabled = false;
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
            string headword = _lookupEntry?.Headword ?? _lookupWord.Text;
            var existing = _library.GetWords().FirstOrDefault(w =>
                string.Equals(w.Word, headword, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(w.Word, _lookupWord.Text, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && !existing.Known)
            {
                _library.DeleteWord(existing.Word);
                UpdateAddVocabButtonVisuals(activeInVocab: false, wasKnown: false);
                ShowReaderToast($"{L10n.T("已从生词本移出：")}{existing.Word}");
            }
            else if (existing is not null && existing.Known)
            {
                _library.Core.SetWordKnown(existing.Word, false);
                UpdateAddVocabButtonVisuals(activeInVocab: true, wasKnown: false);
                ShowReaderToast($"{L10n.T("已重新加入生词本：")}{existing.Word}");
            }
            else
            {
                _library.AddWord(
                    headword,
                    _lookupEntry?.Phonetic,
                    _lookupEntry?.Summary,
                    _bookId,
                    _currentSection,
                    (uint)_lookupWord.Start.S,
                    _lookupWord.Sentence);
                UpdateAddVocabButtonVisuals(activeInVocab: true, wasKnown: false);
                ShowReaderToast($"{L10n.T("已加入生词本：")}{headword}");
            }
            _ = ApplyForeignReadingAsync();
            if (_activePanel == "notes") RenderSidePanel();
            App.CurrentApp.NotifyShelfRefresh();
        }
        catch { }
    }

    private async Task ShowSentenceTranslationCardAsync(int sentenceIdx, double? anchorX = null, double? anchorY = null)
    {
        var sents = _library.GetSpokenSentences(_bookId, _currentSection);
        if (sentenceIdx < 0 || sentenceIdx >= sents.Count) return;
        await ShowTranslationForStringAsync(sents[sentenceIdx], anchorX, anchorY);
    }

    private async Task ShowTranslationForStringAsync(string orig, double? anchorX = null, double? anchorY = null)
    {
        double cardW = 360;
        double cw = WebViewContainer.ActualWidth > 0 ? WebViewContainer.ActualWidth : 800;
        double ch = WebViewContainer.ActualHeight > 0 ? WebViewContainer.ActualHeight : 620;
        double x = anchorX is double ax
            ? Math.Clamp(ax - cardW * 0.5, 12, Math.Max(12, cw - cardW - 12))
            : Math.Max(12, (cw - cardW) * 0.5);
        double y = anchorY is double ay
            ? (ay + 185 > ch ? Math.Max(60, ay - 165) : ay + 16)
            : Math.Max(68, ch * 0.28);
        SentenceCard.Margin = new Thickness(x, y, 0, 0);

        SentenceOrigText.Text = orig;
        CopySentenceTransBtn.Content = L10n.T("拷贝");
        if (!_library.Companion.IsConfigured)
        {
            SentenceLoadingRow.Visibility = Visibility.Collapsed;
            SentenceTransText.Text = L10n.T("尚未配置书伴翻译服务。可在「设置 → 书伴」配置后即时翻译。");
            SentenceTransText.Visibility = Visibility.Visible;
            SentenceCard.Visibility = Visibility.Visible;
            return;
        }

        SentenceLoadingRow.Visibility = Visibility.Visible;
        SentenceTransText.Text = "";
        SentenceTransText.Visibility = Visibility.Collapsed;
        SentenceCard.Visibility = Visibility.Visible;

        try
        {
            string tr = await _library.Companion.ChatAsync(
                $"请把下面这句准确自然地译为简体中文（若是中文则译为英文），只输出译文：\n\n{orig}",
                "你是专业的文学翻译家，只输出译文正文。");
            SentenceLoadingRow.Visibility = Visibility.Collapsed;
            SentenceTransText.Text = tr.Trim();
            SentenceTransText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            SentenceLoadingRow.Visibility = Visibility.Collapsed;
            SentenceTransText.Text = $"{L10n.T("翻译失败：")}{ex.Message}";
            SentenceTransText.Visibility = Visibility.Visible;
        }
    }

    private void CopySentenceTrans_Click(object sender, RoutedEventArgs e)
    {
        string trans = SentenceTransText.Text.Trim();
        if (SentenceLoadingRow.Visibility == Visibility.Visible
            || string.IsNullOrEmpty(trans)
            || trans.StartsWith(L10n.T("翻译失败"))
            || trans.StartsWith(L10n.T("尚未配置")))
        {
            ShowReaderToast(L10n.T("译文尚未就绪"));
            return;
        }
        var dp = new DataPackage();
        dp.SetText(trans);
        Clipboard.SetContent(dp);
        CopySentenceTransBtn.Content = L10n.T("已拷贝");
        ShowReaderToast(L10n.T("已拷贝译文"));
    }

    private string? _openFootnoteAnchor;

    private void ShowFootnote(string target, ReaderNoteRef? noteRef = null)
    {
        try
        {
            var fn = _library.GetFootnote(_bookId, _currentSection, target);
            if (fn is null) { HandleInternalLink(target); return; }
            _openFootnote = fn;
            var tParts = target.Split('#', 2);
            _openFootnoteAnchor = tParts.Length > 1 && !string.IsNullOrEmpty(tParts[1]) ? tParts[1] : null;
            string lbl = noteRef?.Label?.Trim() ?? "";
            string before = noteRef?.Before?.Trim() ?? "";
            FootnoteTitleText.Text = string.IsNullOrEmpty(lbl) ? L10n.T("注释") : $"{L10n.T("注")} {lbl}";
            FootnoteBeforeText.Text = before;
            FootnoteBeforeText.Visibility = string.IsNullOrEmpty(before) ? Visibility.Collapsed : Visibility.Visible;
            FootnoteBodyText.Text = fn.Text;
            FootnoteMetaText.Text = fn.Index is uint idx
                ? $"{_detail.Summary.Title} · {L10n.T("注释")} {idx} / {fn.Count}"
                : (!string.IsNullOrWhiteSpace(fn.Title) ? $"{_detail.Summary.Title} · {fn.Title}" : _detail.Summary.Title);
            FootnoteCard.Visibility = Visibility.Visible;
        }
        catch
        {
            HandleInternalLink(target);
        }
    }

    private void CloseFootnote_Click(object sender, RoutedEventArgs e)
    {
        FootnoteCard.Visibility = Visibility.Collapsed;
        _openFootnote = null;
    }

    private void JumpFootnote_Click(object sender, RoutedEventArgs e)
    {
        if (_openFootnote is null) return;
        _hasUserMoved = true;
        if (!_audio.IsPlaying && _hasPausedAudioSession) _pausedOffSpot = true;
        RecordReturnOrigin();
        uint sec = _openFootnote.Section;
        string? anchor = _openFootnoteAnchor;
        FootnoteCard.Visibility = Visibility.Collapsed;
        _openFootnote = null;
        if (sec == _currentSection && !string.IsNullOrEmpty(anchor))
        {
            MarkProgrammaticScroll();
            _ = _host.ScrollToAnchorAsync(anchor, animated: ShouldAnimatePageTurn());
        }
        else
        {
            _pendingAnchor = anchor;
            LoadSection(sec);
        }
    }

    private async Task RefreshSectionMarksAsync()
    {
        bool hideMarks = _isFullScreen && _library.Prefs.ImmersiveHideMarks;
        var all = _library.GetMarks(_bookId).Where(m => m.Section == _currentSection);
        var sectionMarks = hideMarks ? all.Where(m => _immersiveKeepShownMarks.Contains(m.Id)) : all;
        await _host.SetMarksAsync(sectionMarks);
        if (_activePanel is "notes" or "toc") RenderSidePanel();
        App.CurrentApp.NotifyShelfRefresh();
    }

    // ---------- Bookmarks ----------

    private Bookmark? FindBookmarkHere() => _library.GetBookmarks(_bookId)
        .FirstOrDefault(b => b.Section == _currentSection && (
            (_layout is not null && _layout.PageOf((int)b.Sentence) == _currentPage)
            || (_layout is null && Math.Abs((int)b.Sentence - _currentSentence) <= 2)));

    private bool HasBookmarkHere() => FindBookmarkHere() is not null;

    private void ToggleBookmark_Click(object sender, RoutedEventArgs e)
    {
        var existing = FindBookmarkHere();
        if (existing is not null)
        {
            string delId = existing.Id;
            _library.DeleteBookmark(_bookId, delId);
            ShowReaderToast("已移掉这一页的书签", "撤销", () =>
            {
                try
                {
                    _library.Core.RestoreBookmark(_bookId, delId);
                    ApplyTintAndChrome();
                    if (_activePanel is "toc" or "notes") RenderSidePanel();
                }
                catch { }
            });
        }
        else
        {
            _library.AddBookmark(_bookId, _currentSection, (uint)Math.Max(0, _currentSentence));
            ShowReaderToast("已给这一页加上书签");
        }

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
        if (!_searchShown)
            _panelBeforeSearch = _activePanel != "search" ? _activePanel : null;
        _pageHighlightQuery = null;
        _searchShown = true;
        InlineSearchCapsule.Visibility = Visibility.Visible;
        TitleCenterStack.Visibility = Visibility.Collapsed;
        OpenPanel("search");
        BookSearchBox.Focus(FocusState.Programmatic);
        ApplyTintAndChrome();
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e) => CloseSearch();

    private void CloseSearch()
    {
        _searchDebounce.Stop();
        _pageHighlightQuery = null;
        _searchShown = false;
        _searchHits = [];
        _hitIndex = -1;
        BookSearchBox.Text = "";
        SearchHitCountText.Text = "";
        InlineSearchCapsule.Visibility = Visibility.Collapsed;
        TitleCenterStack.Visibility = Visibility.Visible;
        _ = _host.SetHitsAsync(null);
        if (_activePanel == "search")
        {
            string? prevPanel = _panelBeforeSearch;
            _panelBeforeSearch = null;
            if (!string.IsNullOrEmpty(prevPanel))
                OpenPanel(prevPanel);
            else
                ClosePanel();
        }
        else
        {
            _panelBeforeSearch = null;
        }
        ApplyTintAndChrome();
    }

    private void BookSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void BookSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.G) { StepHit(forward: !shift); e.Handled = true; }
        else if (e.Key == VirtualKey.Enter) { StepHit(forward: !shift); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { CloseSearch(); e.Handled = true; }
    }

    private async Task RunSearchAsync()
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

        IReadOnlyList<SearchHit> hits;
        try { hits = await Task.Run(() => _library.SearchBook(_bookId, q)); }
        catch { hits = []; }

        if (BookSearchBox.Text.Trim() != q) return;
        _searchHits = hits;
        SearchPrevBtn.IsEnabled = _searchHits.Count > 0;
        SearchNextBtn.IsEnabled = _searchHits.Count > 0;

        if (_searchHits.Count == 0)
        {
            _hitIndex = -1;
            SearchHitCountText.Text = "没找到";
            _ = _host.SetHitsAsync(null);
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
            JumpToHit(_searchHits[_hitIndex]);
        }

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
        _hasUserMoved = true;
        if (hit.Section != _currentSection || Math.Abs((int)hit.Sentence - _currentSentence) > 2)
            RecordReturnOrigin();
        SearchHitCountText.Text = $"第 {_hitIndex + 1} / {_searchHits.Count}{(_searchHits.Count >= 500 ? "+" : "")} 处";
        var q = BookSearchBox.Text.Trim();
        bool wasPlaying = _audio.IsPlaying;
        if (!wasPlaying && _hasPausedAudioSession)
            _pausedOffSpot = true;

        if (hit.Section == _currentSection)
        {
            if (_layout is not null) _currentPage = _layout.PageOf((int)hit.Sentence);
            MarkProgrammaticScroll(_currentPage);
            _ = _host.ScrollToSentenceAsync((int)hit.Sentence, animated: ShouldAnimatePageTurn());
            _ = _host.SetHitsAsync(q, (int)hit.Sentence, (int)hit.Nth);
            if (wasPlaying)
                StartListeningAt((int)hit.Sentence);
        }
        else
        {
            if (wasPlaying) _pendingAutoSpeak = true;
            _pendingSentence = (int)hit.Sentence;
            LoadSection(hit.Section);
        }
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
        if (panel != "search" && !_searchShown)
            _panelBeforeSearch = null;
        _activePanel = panel;
        bool isCompanion = panel == "companion";
        double maxPanel = Math.Max(272.0, Math.Min(540.0, (RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 1080.0) * 0.50));
        double baseW = _library.Prefs.ReaderPanelWidth > 0 ? _library.Prefs.ReaderPanelWidth : (isCompanion ? 344.0 : 320.0);
        SidePanelColumn.Width = new GridLength(Math.Clamp(baseW, 272.0, maxPanel) + 14.0);
        SidePanelBorder.Visibility = Visibility.Visible;
        SidePanelResizeHandle.Visibility = Visibility.Visible;
        YYChrome.AnimateSlideX(SidePanelBorder, fromX: 18f, durationMs: 180);

        CompanionPanel.Visibility = isCompanion ? Visibility.Visible : Visibility.Collapsed;
        DynamicSideHost.Visibility = isCompanion ? Visibility.Collapsed : Visibility.Visible;

        if (!isCompanion) RenderSidePanel();
        else UpdateCompanionScopeVisuals();
        if (panel is "toc" or "notes" or "companion" or "cast")
        {
            _library.Prefs.ReaderLastPanel = panel;
            _library.SavePrefs();
        }
        ApplyTintAndChrome();
        UpdatePaperChrome();
    }

    private void ClosePanel()
    {
        if (_activePanel == "companion")
        {
            _pendingSourceHighlight = null;
            if (_pager.IsInitialized) _ = _host.SetSourceAsync(null);
        }
        _activePanel = null;
        _library.Prefs.ReaderLastPanel = "";
        _library.SavePrefs();
        SidePanelColumn.Width = new GridLength(0);
        SidePanelBorder.Visibility = Visibility.Collapsed;
        SidePanelResizeHandle.Visibility = Visibility.Collapsed;
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
        L10n.ApplyTree(SideHeaderHost);
        L10n.ApplyTree(SideBodyStack);
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
        var titlePair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        titlePair.Children.Add(new TextBlock { Text = "笔记", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        titlePair.Children.Add(new TextBlock { Text = "按书里的顺序", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) });
        topRow.Children.Add(titlePair);

        var expMenu = new MenuFlyout();
        var mdItem = new MenuFlyoutItem { Text = "导出 Markdown (.md)…" };
        mdItem.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileSavePicker { SuggestedFileName = $"{_detail.Summary.Title} 笔记" };
                picker.FileTypeChoices.Add("Markdown", [".md"]);
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var f = await picker.PickSaveFileAsync();
                if (f is not null)
                {
                    await File.WriteAllTextAsync(f.Path, NoteExporter.BuildBookMarkdown(_library, _detail.Summary));
                    ShowReaderToast("已导出 Markdown 笔记");
                }
            }
            catch (Exception ex) { ShowReaderToast($"导出失败：{ex.Message}"); }
        };
        expMenu.Items.Add(mdItem);

        if (_detail.Summary.Format != BookFormat.Pdf)
        {
            var epubItem = new MenuFlyoutItem { Text = "导出带笔记的 EPUB…" };
            epubItem.Click += async (_, _) =>
            {
                try
                {
                    var picker = new FileSavePicker { SuggestedFileName = $"{_detail.Summary.Title}（带笔记）" };
                    picker.FileTypeChoices.Add("EPUB 电子书", [".epub"]);
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                    var f = await picker.PickSaveFileAsync();
                    if (f is not null)
                    {
                        string outPath = f.Path;
                        await Task.Run(() => _library.ExportEpub(_bookId, includeNotes: true, outPath));
                        ShowReaderToast("已导出带笔记的 EPUB");
                    }
                }
                catch (Exception ex) { ShowReaderToast($"导出失败：{ex.Message}"); }
            };
            expMenu.Items.Add(epubItem);
        }

        // Wire configured external targets (Obsidian / Notion / flomo / 新枝) matching macOS NoteExportMenu
        bool anyExternal = false;
        void AddTargetItem(bool enabled, string label, Func<Task> action)
        {
            if (!enabled) return;
            if (!anyExternal) { expMenu.Items.Add(new MenuFlyoutSeparator()); anyExternal = true; }
            var mi = new MenuFlyoutItem { Text = $"发送到 {label}" };
            mi.Click += async (_, _) =>
            {
                try
                {
                    await action();
                    ShowReaderToast($"已发送笔记到 {label}");
                }
                catch (Exception ex) { ShowReaderToast($"发送到 {label} 失败：{ex.Message}"); }
            };
            expMenu.Items.Add(mi);
        }
        AddTargetItem(_library.Prefs.ObsidianExportOn && !string.IsNullOrEmpty(_library.Prefs.ObsidianFolder), "Obsidian",
            () => _library.NoteExports.ExportToObsidianAsync(_detail.Summary));
        AddTargetItem(_library.Prefs.NotionExportOn && !string.IsNullOrEmpty(_library.Secrets.Get("export.notion.token")) && !string.IsNullOrEmpty(_library.Prefs.NotionPageId), "Notion",
            () => _library.NoteExports.ExportToNotionAsync(_detail.Summary));
        AddTargetItem(_library.Prefs.FlomoExportOn && NoteExportService.ValidateFlomoWebhook(_library.Secrets.Get("export.flomo.url")), "flomo",
            () => _library.NoteExports.ExportToFlomoAsync(_detail.Summary));
        AddTargetItem(_library.Prefs.XinzhiExportOn && !string.IsNullOrEmpty(_library.Secrets.Get("export.xinzhi.token")), "新枝",
            () => _library.NoteExports.ExportToXinzhiAsync(_detail.Summary));

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

        // 1:1 MacReaderView.swift:1356 & 03-notes.png：始终展示「划线 / 想法 / 生词」三栏
        var tabList = new List<(string Key, string Label)>
        {
            ("marks", $"划线 {marks.Length}"),
            ("thoughts", $"想法 {thoughts.Length}"),
            ("words", words.Length == 0 ? "生词" : $"生词 {words.Length}"),
        };

        if (!tabList.Any(t => t.Key == _notesTab)) _notesTab = "marks";
        SideHeaderHost.Children.Add(BuildPanelTabs(tabList.ToArray(), _notesTab, next => { _notesTab = next; RenderSidePanel(); }));

        if (_notesTab == "words")
        {
            if (words.Length == 0)
            {
                AddSideEmpty(p, L10n.T("还没有生词"), L10n.T("点一个词，在卡片里点「+ 加入生词本」"));
                return;
            }

            for (int i = 0; i < words.Length; i++)
            {
                if (i > 0) SideBodyStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
                var w = words[i];
                int wordPage = EstimateBookPage(w.Section, (int)w.Sentence);
                bool isHere = w.Section == _currentSection && (
                    (_layout is not null && _layout.PageOf((int)w.Sentence) == _currentPage)
                    || (_layout is null && Math.Abs((int)w.Sentence - _currentSentence) <= 12));

                var sp = new StackPanel { Spacing = 4, Padding = new Thickness(4, 10, 4, 10) };
                var hw = new Grid();
                var hwLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                hwLeft.Children.Add(new TextBlock { Text = w.Word, FontFamily = new FontFamily("Georgia"), FontSize = 14.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
                if (!string.IsNullOrWhiteSpace(w.Phonetic))
                    hwLeft.Children.Add(new TextBlock { Text = $"/{w.Phonetic.Trim('/', '[', ']')}/", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Bottom });
                hw.Children.Add(hwLeft);

                string rightMeta = w.Known
                    ? $"{L10n.T("掌握了")} · {L10n.T($"第 {wordPage} 页")}"
                    : L10n.T($"第 {wordPage} 页");
                hw.Children.Add(new TextBlock
                {
                    Text = rightMeta,
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(isHere ? tint.Accent : p.T3),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                sp.Children.Add(hw);

                if (!string.IsNullOrEmpty(w.Meaning))
                {
                    var meaningTb = new TextBlock { FontSize = 12, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap };
                    SetFormattedMeaningInlines(meaningTb, w.Meaning, p);
                    sp.Children.Add(meaningTb);
                }
                if (!string.IsNullOrEmpty(w.Context))
                {
                    var ctxTb = new TextBlock
                    {
                        FontFamily = new FontFamily("Georgia"),
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.T2),
                        MaxLines = 2,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        TextWrapping = TextWrapping.Wrap,
                    };
                    int matchIdx = w.Context.IndexOf(w.Word, StringComparison.OrdinalIgnoreCase);
                    if (matchIdx >= 0 && w.Word.Length > 0)
                    {
                        if (matchIdx > 0)
                            ctxTb.Inlines.Add(new Run { Text = w.Context[..matchIdx] });
                        ctxTb.Inlines.Add(new Run
                        {
                            Text = w.Context.Substring(matchIdx, w.Word.Length),
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = ThemeHelper.Brush(tint.Accent),
                        });
                        if (matchIdx + w.Word.Length < w.Context.Length)
                            ctxTb.Inlines.Add(new Run { Text = w.Context[(matchIdx + w.Word.Length)..] });
                    }
                    else
                    {
                        ctxTb.Text = w.Context;
                    }
                    sp.Children.Add(ctxTb);
                }

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
            if (i > 0) SideBodyStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            var mk = marks[i];
            int markPage = EstimateBookPage(mk.Section, (int)mk.Start.Sentence);
            bool isHere = mk.Section == _currentSection && (
                (_layout is not null && _layout.PageOf((int)mk.Start.Sentence) == _currentPage)
                || (_layout is null && Math.Abs((int)mk.Start.Sentence - _currentSentence) <= 12));
            string pageLabel = isHere ? $"第 {markPage} 页 · 这一页" : $"第 {markPage} 页";

            var sp = new StackPanel { Spacing = 8, Padding = new Thickness(4, 12, 4, 12) };
            sp.Children.Add(new TextBlock
            {
                Text = pageLabel,
                FontSize = 10.5,
                FontWeight = isHere ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(isHere ? tint.Accent : p.T3),
            });

            sp.Children.Add(ShelfWindow.BuildHighlightedQuote(mk, p, dark, 13));

            if (!string.IsNullOrWhiteSpace(mk.Note))
            {
                var tRow = new Grid { ColumnSpacing = 9, Margin = new Thickness(0, 2, 0, 0) };
                tRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5) });
                tRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                tRow.Children.Add(new Border { Background = ThemeHelper.Brush(tint.Accent), CornerRadius = new CornerRadius(0.75) });
                var ntb = new TextBlock
                {
                    Text = mk.Note,
                    FontFamily = new FontFamily(YYFontFamily.Kaiti.WindowsFamily()),
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
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
            shareItem.Click += async (_, _) => await QuoteShareDialog.ShowAsync(
                this,
                RootGrid.XamlRoot,
                mk.Text,
                mk.Note,
                _detail.Summary.Title,
                _detail.Summary.Authors.FirstOrDefault(),
                _detail.Summary.CoverPath,
                chapter: GetChapterTitleForSection(mk.Section),
                library: _library);

            var delItem = new MenuFlyoutItem { Text = "删除划线", Foreground = ThemeHelper.Brush(p.Danger) };
            string delMarkId = mk.Id;
            delItem.Click += async (_, _) =>
            {
                _library.DeleteMark(_bookId, delMarkId);
                await RefreshSectionMarksAsync();
                ShowReaderToast("已删除划线", "撤销", async () =>
                {
                    try { _library.Core.RestoreMark(_bookId, delMarkId); await RefreshSectionMarksAsync(); } catch { }
                });
            };

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

    // 1:1 MacReaderView.swift:1079-1317：目录面板（封面头 + 3 分段 目录/书签/笔记 + 状态行 + 自适应章节号分栏 + 扉页版权分组 + 各章阅读时长 + 自动滚动到当前章）
    private void RenderTocSidePanel(YYPalette p, YYTintColors tint)
    {
        var rawItems = new List<(string Title, uint Section, string? Anchor, ushort Depth)>();
        if (_detail.Toc.Length > 0)
        {
            foreach (var t in _detail.Toc) rawItems.Add((t.Title, t.Section, t.Anchor, t.Depth));
        }
        else
        {
            for (uint i = 0; i < _detail.Sections.Length; i++)
            {
                string secTitle = !string.IsNullOrWhiteSpace(_detail.Sections[i].Title)
                    ? _detail.Sections[i].Title!.Trim()
                    : $"{L10n.T("第")} {i + 1} {L10n.T("节")}";
                if (_detail.Sections[i].Linear || !string.IsNullOrWhiteSpace(_detail.Sections[i].Title))
                    rawItems.Add((secTitle, i, null, 0));
            }
        }

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
        var bookMarks = _library.GetMarks(_bookId)
            .OrderBy(m => m.Section).ThenBy(m => m.Start.Sentence).ThenBy(m => m.Start.Offset)
            .ToArray();
        if (_tocTab is not ("toc" or "bookmarks" or "notes")) _tocTab = "toc";

        // Compact book cover header matching MacTocView
        var coverHead = new Grid { ColumnSpacing = 10 };
        coverHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        coverHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var coverBox = new Border
        {
            Width = 34,
            Height = 46,
            CornerRadius = new CornerRadius(5),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
        };
        if (!string.IsNullOrEmpty(_detail.Summary.CoverPath) && File.Exists(_detail.Summary.CoverPath))
        {
            try { coverBox.Child = new Image { Source = new BitmapImage(new Uri(_detail.Summary.CoverPath)), Stretch = Stretch.UniformToFill }; }
            catch { coverBox.Child = YYIcons.Make("book", 14, ThemeHelper.Brush(p.T3)); }
        }
        else
        {
            coverBox.Child = YYIcons.Make("book", 14, ThemeHelper.Brush(p.T3));
        }
        coverHead.Children.Add(coverBox);
        var bookMeta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        bookMeta.Children.Add(new TextBlock { Text = _detail.Summary.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        if (_detail.Summary.Authors.Length > 0)
            bookMeta.Children.Add(new TextBlock { Text = string.Join("、", _detail.Summary.Authors), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(bookMeta, 1);
        coverHead.Children.Add(bookMeta);
        SideHeaderHost.Children.Add(coverHead);

        // 1:1 MacReaderView.swift:1098-1140 — progress bar & status line sit ABOVE PanelTabs; 目录 tab omits count!
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

        SideHeaderHost.Children.Add(BuildPanelTabs(
            [
                ("toc", L10n.T("目录")),
                ("bookmarks", bookmarks.Count > 0 ? $"{L10n.T("书签")} {bookmarks.Count}" : L10n.T("书签")),
                ("notes", bookMarks.Length > 0 ? $"{L10n.T("笔记")} {bookMarks.Length}" : L10n.T("笔记")),
            ],
            _tocTab,
            next => { _tocTab = next; RenderSidePanel(); }));

        if (_tocTab == "notes")
        {
            bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
            if (bookMarks.Length == 0)
            {
                AddSideEmpty(p, "还没有划线与笔记", "选中文字即可划上并记录想法");
                return;
            }
            PopulateMarkItemsIntoSideBody(bookMarks, p, tint, dark);
            return;
        }

        if (_tocTab == "bookmarks")
        {
            bool hereBm = HasBookmarkHere();
            var bmRowSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            bmRowSp.Children.Add(YYIcons.Make("bookmark", 11.5, ThemeHelper.Brush(tint.Accent), filled: hereBm));
            bmRowSp.Children.Add(new TextBlock
            {
                Text = L10n.T(hereBm ? "拿掉这一页的书签" : "给这一页加书签"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = ThemeHelper.Brush(tint.Accent),
            });
            var toggleBmBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 8, 4, 8),
                UseSystemFocusVisuals = false,
                Content = bmRowSp,
            };
            toggleBmBtn.Click += ToggleBookmark_Click;
            SideBodyStack.Children.Add(toggleBmBtn);
            SideBodyStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 0, 0, 4) });

            if (bookmarks.Count == 0)
            {
                AddSideEmpty(p, "还没有书签", "按 Ctrl+D 给这一页加一个");
                return;
            }

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
                rm.Click += (_, _) =>
                {
                    _library.DeleteBookmark(_bookId, bid);
                    ApplyTintAndChrome();
                    RenderSidePanel();
                    ShowReaderToast("已删除书签", "撤销", () =>
                    {
                        try { _library.Core.RestoreBookmark(_bookId, bid); ApplyTintAndChrome(); RenderSidePanel(); } catch { }
                    });
                };
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
                int curIndexForScroll = i;
                btn.Loaded += (_, _) =>
                {
                    try
                    {
                        double approxOffset = Math.Max(0, curIndexForScroll * 52.0 - 120.0);
                        SideBodyScroll.ChangeView(null, approxOffset, null, disableAnimation: true);
                    }
                    catch { }
                };
            }
            var targetEntry = entry;
            btn.Click += (_, _) =>
            {
                _hasUserMoved = true;
                if (!_audio.IsPlaying && _hasPausedAudioSession) _pausedOffSpot = true;
                if (targetEntry.Section != _currentSection || _currentSentence > 2)
                    RecordReturnOrigin();
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
                    _hasUserMoved = true;
                    if (!_audio.IsPlaying && _hasPausedAudioSession) _pausedOffSpot = true;
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

    private int _unsureInlineSkipOffset;
    private int _unsureInlinePicks;
    private int _unsureInlineSettled;
    private (uint Sec, uint Sent, uint Start, string Name, uint Freed)? _unsureInlineUndo;

    // 1:1 CastView.swift & DramaViews.swift：人物面板（无剧透 N 处拿不准头胶囊 -> 内联 UnsureView，DramaStatusBlock & SameSuggestionRow 在旁白上方）
    private void RenderCastSidePanel(YYPalette p, YYTintColors tint)
    {
        CastMember[] cast;
        Role[] roles;
        UnsureLine[] unsureLines;
        Dictionary<string, (string Voice, bool IsFallback)> autoRoleVoices = new(StringComparer.Ordinal);
        string bookLang = _detail.Summary.Language ?? "zh-Hans";
        try
        {
            cast = _library.Core.BookCast(_bookId, _currentSection);
            roles = _library.Core.BookRoles(_bookId);
            unsureLines = _library.Core.DramaReview(_bookId, _currentSection);
            if (_dramaModeOn)
                DramaSynthService.EnsureCastPlan(_library, _bookId, bookLang);
            foreach (var rv in _library.Core.DramaRoleVoices(_bookId))
                autoRoleVoices[rv.Name] = (rv.Voice, rv.IsDialogueFallback);
        }
        catch
        {
            cast = [];
            roles = [];
            unsureLines = [];
        }

        void RefreshCastAndAudio()
        {
            DramaSynthService.InvalidateCast(_bookId);
            _audio.InvalidateRolesAndSpeakers();
            _ = ApplySectionBubblesAsync();
            if (_activePanel == "cast") RenderSidePanel();
        }

        // Inline UnsureView inside the Cast side panel (1:1 CastSheet.swift:100-104 & DramaViews.swift:650-785)
        if (_castShowingUnsureInline)
        {
            var unsureHead = new Grid();
            var backBtn = new Button
            {
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 2, 8, 2),
                UseSystemFocusVisuals = false,
                Content = new TextBlock { Text = $"‹ {L10n.T("人物")}", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) },
            };
            backBtn.Click += (_, _) =>
            {
                _castShowingUnsureInline = false;
                RenderSidePanel();
            };
            unsureHead.Children.Add(backBtn);
            unsureHead.Children.Add(new TextBlock
            {
                Text = L10n.T("待确认的台词"),
                FontSize = 13.5,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = ThemeHelper.Brush(p.T1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            SideHeaderHost.Children.Add(unsureHead);

            var unsureBody = new StackPanel { Spacing = 10, Padding = new Thickness(2, 4, 2, 12) };
            CastPick[] everyonePicks;
            try { everyonePicks = _library.Core.DramaEveryone(_bookId, _currentSection); } catch { everyonePicks = []; }
            var lookMap = everyonePicks.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            if (_unsureInlineUndo is var (uSec, uSent, uStart, uName, uFreed))
            {
                var undoGrid = new Grid { ColumnSpacing = 8 };
                undoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                undoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var undoLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                undoLeft.Children.Add(new TextBlock { Text = $"✓ {uName}", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
                if (uFreed > 0)
                    undoLeft.Children.Add(new TextBlock { Text = $"{L10n.T("也定了")} {uFreed} {L10n.T("句")}", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
                undoGrid.Children.Add(undoLeft);
                var undoBtn = new Button
                {
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    UseSystemFocusVisuals = false,
                    Content = L10n.T("撤销"),
                };
                undoBtn.Click += (_, _) =>
                {
                    try { _library.Core.DramaClearSpeaker(_bookId, uSec, uSent, uStart); } catch { }
                    _unsureInlineUndo = null;
                    RefreshCastAndAudio();
                };
                Grid.SetColumn(undoBtn, 1);
                undoGrid.Children.Add(undoBtn);
                unsureBody.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(12, 8, 12, 8),
                    Child = undoGrid,
                });
            }

            var orderedUnsure = unsureLines
                .OrderByDescending(u => u.Section == _currentSection)
                .ThenByDescending(u => u.Frees)
                .ToArray();

            if (orderedUnsure.Length == 0 || _unsureInlineSkipOffset >= orderedUnsure.Length)
            {
                var doneStack = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 22, 12, 22) };
                doneStack.Children.Add(new TextBlock { Text = "✓", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(tint.Accent), HorizontalAlignment = HorizontalAlignment.Center });
                doneStack.Children.Add(new TextBlock { Text = L10n.T("读到这里的台词都认好了"), FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), HorizontalAlignment = HorizontalAlignment.Center });
                if (_unsureInlinePicks > 0)
                    doneStack.Children.Add(new TextBlock { Text = $"{L10n.T("点了")} {_unsureInlinePicks} {L10n.T("下，带走")} {_unsureInlineSettled} {L10n.T("句")}", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Center });
                unsureBody.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Child = doneStack,
                });
            }
            else
            {
                var cur = orderedUnsure[_unsureInlineSkipOffset];
                void CommitInlineChoice(UserChoice choice, string label)
                {
                    uint freed = cur.Frees;
                    try { _library.Core.DramaSetSpeaker(_bookId, cur.Section, cur.Sentence, cur.Start, choice); } catch { }
                    _unsureInlineUndo = (cur.Section, cur.Sentence, cur.Start, label, freed);
                    _unsureInlinePicks++;
                    _unsureInlineSettled += 1 + (int)freed;
                    RefreshCastAndAudio();
                }

                var whereParts = new List<string>();
                string chapName = ResolveSectionTitle(cur.Section);
                whereParts.Add(!string.IsNullOrEmpty(chapName) ? chapName : $"{L10n.T("第")} {cur.Section + 1} {L10n.T("章")}");
                if (!string.IsNullOrWhiteSpace(cur.Place)) whereParts.Add(cur.Place);
                if (cur.Present is { Length: > 0 }) whereParts.Add($"{L10n.T("在场")} {string.Join(" ", cur.Present.Take(5))}");

                var cardStack = new StackPanel { Spacing = 0 };
                cardStack.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", whereParts),
                    FontSize = 10,
                    Foreground = ThemeHelper.Brush(p.T3),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(12, 10, 12, 0),
                });

                var ctxTb = new TextBlock
                {
                    FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                    FontSize = 13,
                    LineHeight = 20,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12, 6, 12, 10),
                };
                var ctxArr = cur.Context ?? [];
                int relAt = (int)cur.Sentence - (int)cur.ContextFirst;
                for (int ci = 0; ci < ctxArr.Length; ci++)
                {
                    string sLine = ctxArr[ci];
                    if (ci == relAt && !string.IsNullOrEmpty(cur.Text) && sLine.IndexOf(cur.Text, StringComparison.Ordinal) is int mPos && mPos >= 0)
                    {
                        if (mPos > 0)
                            ctxTb.Inlines.Add(new Run { Text = sLine[..mPos], Foreground = ThemeHelper.Brush(p.T3) });
                        ctxTb.Inlines.Add(new Run { Text = cur.Text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                        if (mPos + cur.Text.Length < sLine.Length)
                            ctxTb.Inlines.Add(new Run { Text = sLine[(mPos + cur.Text.Length)..], Foreground = ThemeHelper.Brush(p.T3) });
                    }
                    else
                    {
                        ctxTb.Inlines.Add(new Run { Text = sLine, Foreground = ThemeHelper.Brush(p.T3) });
                    }
                }
                if (ctxTb.Inlines.Count == 0)
                    ctxTb.Text = cur.Text;
                cardStack.Children.Add(ctxTb);
                cardStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });

                var pickStack = new StackPanel { Spacing = 6, Padding = new Thickness(12) };
                pickStack.Children.Add(new TextBlock { Text = L10n.T("这句是谁说的？"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(0, 0, 0, 2) });

                string[] candNames = (cur.Names is { Length: > 0 } ? cur.Names : cur.Present) ?? [];
                for (int i = 0; i < Math.Min(2, candNames.Length); i++)
                {
                    string cand = candNames[i];
                    lookMap.TryGetValue(cand, out var look);
                    Color avCol = CustomPaperDialog.ParseHexColor(look?.Color ?? "#888888", tint.Accent);
                    string avTxt = !string.IsNullOrEmpty(look?.Avatar) ? look.Avatar : (cand.Length > 0 ? cand[^1].ToString() : "?");
                    string reason = i == 0 ? L10n.T("最可能") : ((cur.Present?.Contains(cand) ?? false) ? L10n.T("在场") : L10n.T("也可能"));

                    var rowG = new Grid { ColumnSpacing = 8 };
                    rowG.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                    rowG.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowG.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    rowG.Children.Add(new Border
                    {
                        Width = 24,
                        Height = 24,
                        CornerRadius = new CornerRadius(12),
                        Background = ThemeHelper.Brush(Color.FromArgb(42, avCol.R, avCol.G, avCol.B)),
                        Child = new TextBlock { Text = avTxt, FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(avCol), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                    });
                    var midSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    midSp.Children.Add(L10n.Skip(new TextBlock { Text = cand, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) }));
                    midSp.Children.Add(new TextBlock { Text = reason, FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
                    Grid.SetColumn(midSp, 1);
                    rowG.Children.Add(midSp);

                    var keyBadge = new Border
                    {
                        Width = 16,
                        Height = 16,
                        CornerRadius = new CornerRadius(4),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.5),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = $"{i + 1}", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                    };
                    Grid.SetColumn(keyBadge, 2);
                    rowG.Children.Add(keyBadge);

                    var cb = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(9, 7, 9, 7),
                        CornerRadius = new CornerRadius(10),
                        Background = ThemeHelper.Brush(i == 0 ? tint.AccentSoft : p.Bg),
                        BorderBrush = ThemeHelper.Brush(i == 0 ? tint.Edge : p.Line),
                        BorderThickness = new Thickness(0.75),
                        UseSystemFocusVisuals = false,
                        Content = rowG,
                    };
                    cb.Click += (_, _) => CommitInlineChoice(new UserChoice.Person(cand), cand);
                    pickStack.Children.Add(cb);
                }

                var subRow = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                var subLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var otherBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = L10n.T("别人…"), Height = 24, MinHeight = 24, FontSize = 11, Padding = new Thickness(9, 0, 9, 0) };
                otherBtn.Click += async (_, _) =>
                {
                    var chosen = await VoiceCloneAndDramaDialogs.ShowPeoplePickerDialogAsync(RootGrid.XamlRoot, _library, _bookId, _currentSection, L10n.T("选择这句的说话人"));
                    if (chosen is not null)
                    {
                        string lbl = chosen switch { UserChoice.Person pr => pr.Name, UserChoice.Group gr => gr.Name, _ => L10n.T("不是台词") };
                        CommitInlineChoice(chosen, lbl);
                    }
                };
                var notSpeechBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = L10n.T("不是台词"), Height = 24, MinHeight = 24, FontSize = 11, Padding = new Thickness(9, 0, 9, 0) };
                notSpeechBtn.Click += (_, _) => CommitInlineChoice(new UserChoice.NotSpeech(), L10n.T("不是台词"));
                subLeft.Children.Add(otherBtn);
                subLeft.Children.Add(notSpeechBtn);
                subRow.Children.Add(subLeft);

                var skipBtn = new Button
                {
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(4, 2, 4, 2),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    UseSystemFocusVisuals = false,
                    Content = L10n.T("跳过 →"),
                };
                skipBtn.Click += (_, _) => { _unsureInlineSkipOffset++; RenderSidePanel(); };
                subRow.Children.Add(skipBtn);
                pickStack.Children.Add(subRow);
                cardStack.Children.Add(pickStack);

                unsureBody.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Child = cardStack,
                });

                if (cur.Frees > 0)
                {
                    unsureBody.Children.Add(new TextBlock
                    {
                        Text = $"{L10n.T("定了这句，后面")} {cur.Frees} {L10n.T("句也跟着定。")}",
                        FontSize = 10,
                        Foreground = ThemeHelper.Brush(p.T3),
                        Margin = new Thickness(2, 0, 2, 0),
                    });
                }
            }

            SideBodyStack.Children.Add(unsureBody);
            return;
        }

        var allVoices = AudioPlayer.GetAllVoiceOptions(_library, _bookId);
        string CleanVoiceName(string? rawId)
        {
            string? norm = DramaSynthService.NormalizeVoiceId(rawId);
            if (string.IsNullOrEmpty(norm)) return L10n.T("跟旁白");
            var hit = allVoices.FirstOrDefault(v => v.Id == norm || v.Id == "sys:" + norm);
            if (hit is not null)
                return hit.Label.Replace("✦ ", "").Replace("☁ ", "").Split('（')[0].Trim();
            return norm.Length > 14 ? norm[..14] : norm;
        }

        // Header: 人物 + spoiler-capped "N 处拿不准 ›" capsule (DramaReview(_bookId, _currentSection).Length)
        var headGrid = new Grid();
        var headLeft = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        headLeft.Children.Add(new TextBlock { Text = L10n.T("人物"), FontSize = 14.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
        headLeft.Children.Add(new TextBlock { Text = $"{L10n.T("读到这里")} · {cast.Length} {L10n.T("人")}", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
        headGrid.Children.Add(headLeft);

        if (unsureLines.Length > 0)
        {
            var pillSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            pillSp.Children.Add(new TextBlock { Text = $"{unsureLines.Length}", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
            pillSp.Children.Add(new TextBlock { Text = $"{L10n.T("处拿不准")} ›", FontSize = 11, Foreground = ThemeHelper.Brush(p.T2) });
            var unsureHeaderBtn = new Button
            {
                Height = 25,
                MinHeight = 25,
                Padding = new Thickness(9, 0, 9, 0),
                CornerRadius = new CornerRadius(12.5),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                UseSystemFocusVisuals = false,
                Content = pillSp,
            };
            unsureHeaderBtn.Click += (_, _) =>
            {
                _unsureInlineSkipOffset = 0;
                _castShowingUnsureInline = true;
                RenderSidePanel();
            };
            headGrid.Children.Add(unsureHeaderBtn);
        }
        SideHeaderHost.Children.Add(headGrid);

        var body = new StackPanel { Spacing = 10, Padding = new Thickness(2, 4, 2, 12) };

        // 1. DramaStatusBlock ABOVE 旁白 (1:1 CastView.swift:127 & DramaViews.swift:293-580)
        try
        {
            var prog = _library.Core.DramaProgress(_bookId);
            var (running, sentChars, lastErr) = VoiceCloneAndDramaDialogs.GetDramaWorkerStatus(_bookId);
            int secLines = 0, secKnown = 0;
            try
            {
                var play = _library.Core.DramaSection(_bookId, _currentSection);
                var seenUtterances = new HashSet<uint>();
                foreach (var seg in play.Segments)
                {
                    if (seg.Utterance is uint u && seenUtterances.Add(u) && seg.Kind != SegKind.Narration)
                    {
                        secLines++;
                        if (seg.Speaker is not DramaSpeaker.Unsure) secKnown++;
                    }
                }
            }
            catch { }

            if (secLines > 0 || running || !string.IsNullOrWhiteSpace(lastErr))
            {
                if (unsureLines.Length == 0 && prog.CastReady && !running && string.IsNullOrWhiteSpace(lastErr))
                {
                    body.Children.Add(new TextBlock
                    {
                        Text = L10n.T("读到这里的台词都认好了"),
                        FontSize = 10.5,
                        Foreground = ThemeHelper.Brush(p.T3),
                        Margin = new Thickness(4, 0, 4, 0),
                    });
                }
                else
                {
                    var statusCard = new StackPanel { Spacing = 0 };
                    var topArea = new StackPanel { Spacing = 9, Padding = new Thickness(12, 11, 12, 11) };
                    var statusTopRow = new Grid { ColumnSpacing = 8 };
                    statusTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    statusTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    statusTopRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    if (running)
                    {
                        statusTopRow.Children.Add(new Border
                        {
                            Width = 28,
                            Height = 28,
                            CornerRadius = new CornerRadius(8),
                            Background = ThemeHelper.Brush(p.T1),
                            Child = new ProgressRing { IsActive = true, Width = 14, Height = 14, Foreground = ThemeHelper.Brush(p.Card) },
                        });
                    }
                    else
                    {
                        statusTopRow.Children.Add(new Border
                        {
                            Width = 28,
                            Height = 28,
                            CornerRadius = new CornerRadius(8),
                            Background = ThemeHelper.Brush(p.Inset),
                            Child = YYIcons.Make("sparkle", 12, ThemeHelper.Brush(p.T1)),
                        });
                    }

                    int missing = Math.Max(0, secLines - secKnown);
                    string mainLine = running
                        ? L10n.T(_dramaSectionOnly ? "正在认本章" : "正在认全书")
                        : (!string.IsNullOrWhiteSpace(lastErr) ? L10n.T("停下了") : $"{L10n.T("本章台词")} · {secKnown} / {secLines}");
                    string subLine = running
                        ? (_dramaSectionOnly ? $"{L10n.T("已认出")} {secKnown} / {secLines} {L10n.T("句")}" : $"{L10n.T("第")} {prog.SectionsDone} / {Math.Max(1u, prog.Sections)} {L10n.T("章")}")
                        : (!string.IsNullOrWhiteSpace(lastErr)
                            ? lastErr!
                            : (prog.CastReady
                                ? (unsureLines.Length > 0 ? $"{L10n.T("截至当前共有")} {unsureLines.Length} {L10n.T("句待确认")}" : L10n.T("读到这里的台词都认好了"))
                                : (!_library.Companion.IsConfigured
                                    ? L10n.T("在「设置 › 书伴」配置模型后可自动补全")
                                    : (missing > 0 ? $"{L10n.T("余")} {missing} {L10n.T("句未写明说话人")}" : L10n.T("本章台词已全部识别")))));

                    var stMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                    stMeta.Children.Add(new TextBlock { Text = mainLine, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                    stMeta.Children.Add(new TextBlock { Text = subLine, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
                    Grid.SetColumn(stMeta, 1);
                    statusTopRow.Children.Add(stMeta);

                    if (running || !prog.CastReady)
                    {
                        string actLabel = running
                            ? L10n.T("暂停")
                            : (!string.IsNullOrWhiteSpace(lastErr)
                                ? L10n.T("接着认")
                                : (_library.Companion.IsConfigured ? L10n.T(_dramaSectionOnly ? "开始认本章" : "开始认全书") : L10n.T("去配置")));
                        var actBtn = new Button
                        {
                            Height = 26,
                            MinHeight = 26,
                            Padding = new Thickness(11, 0, 11, 0),
                            CornerRadius = new CornerRadius(13),
                            Background = ThemeHelper.Brush(p.Card),
                            BorderBrush = ThemeHelper.Brush(p.Line),
                            BorderThickness = new Thickness(0.75),
                            FontSize = 11,
                            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                            Foreground = ThemeHelper.Brush(p.T1),
                            VerticalAlignment = VerticalAlignment.Center,
                            UseSystemFocusVisuals = false,
                            Content = actLabel,
                        };
                        actBtn.Click += (_, _) =>
                        {
                            if (running) { VoiceCloneAndDramaDialogs.StopDramaWorker(_bookId); RenderSidePanel(); }
                            else if (_library.Companion.IsConfigured)
                            {
                                VoiceCloneAndDramaDialogs.StartDramaSpeakerWorker(_library, _bookId, _currentSection, () =>
                                    DispatcherQueue.TryEnqueue(() =>
                                    {
                                        _audio.InvalidateRolesAndSpeakers();
                                        if (_bubblesOn) _ = ApplySectionBubblesAsync();
                                        if (_activePanel == "cast") RenderSidePanel();
                                    }), sectionOnly: _dramaSectionOnly);
                                RenderSidePanel();
                            }
                            else App.CurrentApp.OpenSettings("companion");
                        };
                        Grid.SetColumn(actBtn, 2);
                        statusTopRow.Children.Add(actBtn);
                    }
                    topArea.Children.Add(statusTopRow);

                    double inkFrac = running && !_dramaSectionOnly
                        ? (prog.Sections > 0 ? Math.Clamp((double)prog.SectionsDone / prog.Sections, 0.05, 1.0) : 0.1)
                        : (secLines > 0 ? Math.Clamp((double)secKnown / secLines, 0.02, 1.0) : 0.02);
                    var barGrid = new Grid { Height = 2 };
                    barGrid.Children.Add(new Border { CornerRadius = new CornerRadius(1), Background = ThemeHelper.Brush(p.Inset) });
                    var barFill = new Grid();
                    barFill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(inkFrac, GridUnitType.Star) });
                    barFill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0 - inkFrac, GridUnitType.Star) });
                    barFill.Children.Add(new Border { CornerRadius = new CornerRadius(1), Background = ThemeHelper.Brush(p.T1) });
                    barGrid.Children.Add(barFill);
                    topArea.Children.Add(barGrid);
                    statusCard.Children.Add(topArea);

                    if (!running && !prog.CastReady && _library.Companion.IsConfigured)
                    {
                        statusCard.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
                        var trayRow = new Grid { Padding = new Thickness(12, 7, 12, 7), Background = ThemeHelper.Brush(p.Inset) };
                        var scopeCaps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
                        foreach (var (onlyChap, lbl) in new[] { (true, L10n.T("仅本章")), (false, L10n.T("全书")) })
                        {
                            bool on = _dramaSectionOnly == onlyChap;
                            var sb = new Button
                            {
                                Content = lbl,
                                Height = 22,
                                MinHeight = 22,
                                Padding = new Thickness(9, 0, 9, 0),
                                CornerRadius = new CornerRadius(11),
                                BorderThickness = new Thickness(0),
                                FontSize = 10.5,
                                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                                Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                                UseSystemFocusVisuals = false,
                            };
                            bool targetOnly = onlyChap;
                            sb.Click += (_, _) => { _dramaSectionOnly = targetOnly; RenderSidePanel(); };
                            scopeCaps.Children.Add(sb);
                        }
                        trayRow.Children.Add(scopeCaps);
                        statusCard.Children.Add(trayRow);
                    }
                    else if (running && sentChars > 0)
                    {
                        statusCard.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
                        statusCard.Children.Add(new Border
                        {
                            Padding = new Thickness(12, 6, 12, 6),
                            Background = ThemeHelper.Brush(p.Inset),
                            Child = new TextBlock { Text = $"{L10n.T("合上书也会继续")} · {(sentChars / 1000.0):0.1}k {L10n.T("字")}", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3) },
                        });
                    }

                    body.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(14),
                        Background = ThemeHelper.Brush(p.Card),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.75),
                        Child = statusCard,
                    });
                }
            }
        }
        catch { }

        // 2. SameSuggestionRow ABOVE 旁白 (1:1 CastView.swift:129-135, spoiler-capped to _currentSection)
        try
        {
            var suggestions = _library.Core.DramaSameSuggestions(_bookId, _currentSection);
            foreach (var sug in suggestions.Take(2))
            {
                var sg = sug;
                var sugGrid = new Grid { ColumnSpacing = 8 };
                sugGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                sugGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                sugGrid.Children.Add(new TextBlock
                {
                    Text = $"「{sg.A}」{L10n.T("与")}「{sg.B}」{L10n.T("可能是同一人")}",
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T1),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                });
                var sugActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
                var mergeBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = L10n.T("合成一个"), Height = 24, MinHeight = 24, FontSize = 10.5, Padding = new Thickness(9, 0, 9, 0) };
                mergeBtn.Click += (_, _) => { try { _library.Core.DramaEditCast(_bookId, new CastEdit.Merge(sg.A, sg.B)); } catch { } RefreshCastAndAudio(); };
                var diffBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = L10n.T("不是"), Height = 24, MinHeight = 24, FontSize = 10.5, Padding = new Thickness(8, 0, 8, 0) };
                diffBtn.Click += (_, _) => { try { _library.Core.DramaEditCast(_bookId, new CastEdit.NotSame(sg.A, sg.B)); } catch { } RefreshCastAndAudio(); };
                sugActs.Children.Add(mergeBtn);
                sugActs.Children.Add(diffBtn);
                Grid.SetColumn(sugActs, 1);
                sugGrid.Children.Add(sugActs);
                body.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(12, 9, 10, 9),
                    Child = sugGrid,
                });
            }
        }
        catch { }

        // 3. 旁白 group card (1:1 CastView.swift:137-145)
        string curLangVoiceId = DramaSynthService.NormalizeVoiceId(_library.Prefs.GetVoiceForLanguage(bookLang)) ?? "";
        var curVoice = allVoices.FirstOrDefault(v => v.Id == curLangVoiceId || v.Id == "sys:" + curLangVoiceId);
        string narVoiceShort = curVoice is not null
            ? curVoice.Label.Replace("✦ ", "").Replace("☁ ", "").Split('（')[0].Trim()
            : L10n.T("系统默认");
        var narGrid = new Grid { ColumnSpacing = 10 };
        narGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        narGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        narGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        narGrid.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = ThemeHelper.Brush(p.Inset), Child = YYIcons.Make("book", 11.5, ThemeHelper.Brush(p.T3)) });
        var narMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        narMeta.Children.Add(new TextBlock { Text = L10n.T("旁白"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        narMeta.Children.Add(new TextBlock { Text = L10n.T("没单独设声音的人物也用这个"), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(narMeta, 1);
        narGrid.Children.Add(narMeta);
        var narChg = new TextBlock { Text = $"{narVoiceShort} ›", FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(narChg, 2);
        narGrid.Children.Add(narChg);

        var narBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(12, 9, 12, 9),
            UseSystemFocusVisuals = false,
            Content = narGrid,
        };
        narBtn.Click += async (_, _) => await ShowVoicePickerSheetAsync(L10n.T("旁白的声音"), curLangVoiceId, vid =>
        {
            _library.Prefs.SetVoiceForLanguage(bookLang, DramaSynthService.NormalizeVoiceId(vid) ?? vid);
            _library.SavePrefs();
            DramaSynthService.InvalidateCast(_bookId);
            ReloadVoicePrefs();
            RenderSidePanel();
        });
        body.Children.Add(narBtn);

        // 4. 这一章 & 前面出场过 character groups (1:1 CastView.swift:146-155)
        if (cast.Length > 0)
        {
            var roleMap = roles.ToDictionary(r => r.Name, r => r);
            Button BuildCastMemberButton(CastMember member)
            {
                Color avCol = CustomPaperDialog.ParseHexColor(member.Color, tint.Accent);
                roleMap.TryGetValue(member.Name, out var customRole);
                bool hasCustom = !string.IsNullOrEmpty(customRole?.Voice?.Voice);
                string badgeLabel;
                if (hasCustom)
                {
                    badgeLabel = CleanVoiceName(customRole!.Voice!.Voice);
                }
                else if (_dramaModeOn && autoRoleVoices.TryGetValue(member.Name, out var autoInfo))
                {
                    badgeLabel = autoInfo.IsFallback ? L10n.T("自动 · 跟旁白") : $"{CleanVoiceName(autoInfo.Voice)} · {L10n.T("自动")}";
                }
                else
                {
                    badgeLabel = L10n.T("跟旁白");
                }

                var detailParts = new List<string>();
                if (member.Aliases is { Length: > 0 })
                    detailParts.Add(string.Join("、", member.Aliases.Take(2)));
                detailParts.Add($"{member.LinesSoFar} {L10n.T("句")}");
                if (customRole?.Voice?.Right == true)
                    detailParts.Add(L10n.T("右边"));

                var cg = new Grid { ColumnSpacing = 10 };
                cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                cg.Children.Add(new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Background = ThemeHelper.Brush(Color.FromArgb(42, avCol.R, avCol.G, avCol.B)),
                    Child = new TextBlock { Text = member.Avatar, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(avCol), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });

                var cm = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                cm.Children.Add(L10n.Skip(new TextBlock { Text = member.Name, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) }));
                cm.Children.Add(new TextBlock { Text = string.Join(" · ", detailParts), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(cm, 1);
                cg.Children.Add(cm);

                var rightVoice = new TextBlock
                {
                    Text = $"{badgeLabel} ›",
                    FontSize = 10,
                    FontWeight = hasCustom ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(hasCustom ? p.T1 : p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 108,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(rightVoice, 2);
                cg.Children.Add(rightVoice);

                string roleName = member.Name;
                var ctx = new MenuFlyout();
                if (!string.IsNullOrEmpty(member.MaybeSame))
                {
                    string maybeOther = member.MaybeSame;
                    var quickMergeMi = new MenuFlyoutItem { Text = $"{L10n.T("合并到")} {maybeOther}" };
                    quickMergeMi.Click += (_, _) => { try { _library.Core.DramaEditCast(_bookId, new CastEdit.Merge(maybeOther, roleName)); RefreshCastAndAudio(); } catch { } };
                    ctx.Items.Add(quickMergeMi);
                }
                var mergeMi = new MenuFlyoutItem { Text = L10n.T("合并到别人…") };
                mergeMi.Click += async (_, _) =>
                {
                    var picked = await VoiceCloneAndDramaDialogs.ShowPeoplePickerDialogAsync(RootGrid.XamlRoot, _library, _bookId, _currentSection, $"{roleName}{L10n.T("合并到")}", includeGroup: false);
                    if (picked is UserChoice.Person target && target.Name != roleName)
                    {
                        try { _library.Core.DramaEditCast(_bookId, new CastEdit.Merge(target.Name, roleName)); RefreshCastAndAudio(); } catch { }
                    }
                };
                var femaleMi = new MenuFlyoutItem { Text = member.Gender == CharacterGender.F ? $"✓ {L10n.T("设为女性")}" : L10n.T("设为女性") };
                femaleMi.Click += (_, _) => { try { _library.Core.DramaEditCast(_bookId, new CastEdit.SetGender(roleName, CharacterGender.F)); RefreshCastAndAudio(); } catch { } };
                var maleMi = new MenuFlyoutItem { Text = member.Gender == CharacterGender.M ? $"✓ {L10n.T("设为男性")}" : L10n.T("设为男性") };
                maleMi.Click += (_, _) => { try { _library.Core.DramaEditCast(_bookId, new CastEdit.SetGender(roleName, CharacterGender.M)); RefreshCastAndAudio(); } catch { } };
                var removeMi = new MenuFlyoutItem { Text = L10n.T("并非人物（移除）"), Foreground = ThemeHelper.Brush(p.Danger) };
                removeMi.Click += (_, _) => { try { _library.Core.DramaEditCast(_bookId, new CastEdit.Remove(roleName)); RefreshCastAndAudio(); } catch { } };
                ctx.Items.Add(mergeMi);
                ctx.Items.Add(new MenuFlyoutSeparator());
                ctx.Items.Add(femaleMi);
                ctx.Items.Add(maleMi);
                ctx.Items.Add(new MenuFlyoutSeparator());
                ctx.Items.Add(removeMi);

                var cBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(12, 8, 12, 8),
                    UseSystemFocusVisuals = false,
                    ContextFlyout = ctx,
                    Content = cg,
                };
                cBtn.Click += async (_, _) => await VoiceCloneAndDramaDialogs.ShowRoleDetailDialogAsync(
                    this,
                    RootGrid.XamlRoot,
                    _library,
                    _bookId,
                    roleName,
                    (vt, sel, cb) => ShowVoicePickerSheetAsync(vt, sel, cb, roleName),
                    RefreshCastAndAudio);
                return cBtn;
            }

            void AddCastGroup(string label, string rightCount, CastMember[] members)
            {
                if (members.Length == 0) return;
                var gh = new Grid { Margin = new Thickness(4, 2, 4, 0) };
                gh.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3) });
                gh.Children.Add(new TextBlock { Text = rightCount, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
                body.Children.Add(gh);

                var grpStack = new StackPanel { Spacing = 0 };
                for (int i = 0; i < members.Length; i++)
                {
                    if (i > 0) grpStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(48, 0, 12, 0) });
                    grpStack.Children.Add(BuildCastMemberButton(members[i]));
                }
                body.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Child = grpStack,
                });
            }

            var hereCast = cast.Where(m => m.LinesHere > 0).ToArray();
            var beforeCast = cast.Where(m => m.LinesHere == 0).ToArray();
            AddCastGroup(L10n.T("这一章"), $"{hereCast.Length} {L10n.T("人")}", hereCast);
            AddCastGroup(L10n.T("前面出场过"), $"{L10n.T("读到这里共")} {cast.Length} {L10n.T("人")}", beforeCast);
        }

        // 5. DramaListenGroup card (广播剧模式 + 旁白提示词 + 情感语气/环境音/音效 + 后台预合成 + 导出 M4B)
        var dramaCard = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(12, 8, 12, 8),
        };
        var dramaStack = new StackPanel { Spacing = 8 };
        var dRow = new Grid();
        var dMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 48, 0) };
        dMeta.Children.Add(new TextBlock { Text = L10n.T("广播剧模式"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
        dMeta.Children.Add(new TextBlock
        {
            Text = L10n.T(_dramaModeOn ? "按人物自动分声，带语气与场景音效，点了就听" : "开启后自动为人物配声并加入场景音效"),
            FontSize = 10,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
        });
        dRow.Children.Add(dMeta);
        var dSw = new ToggleSwitch { IsOn = _dramaModeOn, MinWidth = 0, OnContent = "", OffContent = "", HorizontalAlignment = HorizontalAlignment.Right };
        dSw.Toggled += (_, _) =>
        {
            _dramaModeOn = dSw.IsOn;
            _audio.SetSectionContext(_currentSection, _dramaModeOn);
            try { _library.Core.DramaSetEnabled(_bookId, _dramaModeOn); } catch { }
            if (_dramaModeOn)
            {
                DramaSynthService.EnsureCastPlan(_library, _bookId, bookLang);
            }
            else
            {
                if (DramaSynthService.IsPreparing(_library, _bookId))
                    DramaSynthService.SetPreparing(_library, _bookId, false);
                DramaSynthService.StopPreRender(_bookId);
            }
            RenderSidePanel();
        };
        dRow.Children.Add(dSw);
        dramaStack.Children.Add(dRow);

        if (_dramaModeOn)
        {
            try
            {
                var ds = _library.Core.DramaSound(_bookId);
                bool skipSpeechTags = _library.Core.DramaMode(_bookId);
                bool canAct = DramaSynthService.CanAct(_library, bookLang);

                Grid MakeDramaRowToggle(string label, string hint, bool val, Action<bool> onToggled, bool enabled = true)
                {
                    var rg = new Grid { ColumnSpacing = 8 };
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var lm = new StackPanel { Spacing = 0.5, VerticalAlignment = VerticalAlignment.Center };
                    lm.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T1) });
                    lm.Children.Add(new TextBlock { Text = hint, FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
                    rg.Children.Add(lm);
                    var sw = new ToggleSwitch { IsOn = enabled && val, IsEnabled = enabled, MinWidth = 0, OnContent = "", OffContent = "" };
                    sw.Toggled += (_, _) => { if (enabled) onToggled(sw.IsOn); };
                    Grid.SetColumn(sw, 1);
                    rg.Children.Add(sw);
                    return rg;
                }

                dramaStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
                var tagRow = new Grid { ColumnSpacing = 8 };
                tagRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                tagRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var tagMeta = new StackPanel { Spacing = 0.5, VerticalAlignment = VerticalAlignment.Center };
                tagMeta.Children.Add(new TextBlock { Text = L10n.T("旁白提示词"), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T1) });
                tagMeta.Children.Add(new TextBlock { Text = L10n.T("如「宝玉笑道」等引述语"), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3) });
                tagRow.Children.Add(tagMeta);
                var tagSeg = YYChrome.MakeSegmentedBar(
                    [("keep", L10n.T("保留")), ("skip", L10n.T("略过"))],
                    skipSpeechTags ? "skip" : "keep",
                    id =>
                    {
                        try { _library.Core.DramaSetMode(_bookId, id == "skip"); _audio.InvalidateRolesAndSpeakers(); RenderSidePanel(); } catch { }
                    },
                    ThemeHelper.IsDark(RootGrid.ActualTheme),
                    fontSize: 11,
                    height: 26);
                tagSeg.Width = 104;
                Grid.SetColumn(tagSeg, 1);
                tagRow.Children.Add(tagSeg);
                dramaStack.Children.Add(tagRow);

                dramaStack.Children.Add(MakeDramaRowToggle(
                    L10n.T("情感语气"),
                    L10n.T(canAct ? "按原文语境表现笑、叹、低语等语气" : "需使用支持情感的云端音色或增强本地音色"),
                    ds.Acting,
                    v => { try { ds = ds with { Acting = v }; _library.Core.DramaSetSound(_bookId, ds); _audio.InvalidateRolesAndSpeakers(); } catch { } },
                    enabled: canAct));
                dramaStack.Children.Add(MakeDramaRowToggle(L10n.T("环境音"), L10n.T("随室内、雨夜、水边等场景铺垫氛围音"), ds.Beds, v =>
                {
                    try
                    {
                        ds = ds with { Beds = v };
                        _library.Core.DramaSetSound(_bookId, ds);
                        if (ds.Beds || ds.Spots) _ = SoundLibraryService.EnsureAsync(_library.Core, _library.DataDir);
                        _audio.InvalidateRolesAndSpeakers();
                    }
                    catch { }
                }));
                dramaStack.Children.Add(MakeDramaRowToggle(L10n.T("音效"), L10n.T("随正文触发敲门、雷声、钟声等音效"), ds.Spots, v =>
                {
                    try
                    {
                        ds = ds with { Spots = v };
                        _library.Core.DramaSetSound(_bookId, ds);
                        if (ds.Beds || ds.Spots) _ = SoundLibraryService.EnsureAsync(_library.Core, _library.DataDir);
                        _audio.InvalidateRolesAndSpeakers();
                    }
                    catch { }
                }));

                dramaStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
                bool preRunning = DramaSynthService.IsPreRendering(_bookId);
                DramaMade? made = null;
                try { made = _library.Core.DramaMade(_bookId); } catch { }
                string preHint = made is not null && made.Sections > 0
                    ? $"{L10n.T("已完成")} {made.Made} / {made.Sections} {L10n.T("章")}"
                    : L10n.T("不开启也可即时听；开启后提前做好全书");
                dramaStack.Children.Add(MakeDramaRowToggle(L10n.T("后台预合成全书"), preHint, preRunning || DramaSynthService.IsPreparing(_library, _bookId), on =>
                {
                    DramaSynthService.SetPreparing(_library, _bookId, on);
                    if (on)
                    {
                        DramaSynthService.StartPreRender(_library, _bookId, bookLang, _currentSection, onSectionMade: () =>
                            DispatcherQueue.TryEnqueue(() => { if (_activePanel == "cast") RenderSidePanel(); }));
                    }
                    else
                    {
                        DramaSynthService.StopPreRender(_bookId);
                    }
                    RenderSidePanel();
                }));

                if (made is not null && made.Made > 0)
                {
                    var exportM4bBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Height = 28,
                        FontSize = 11.5,
                        Content = $"{L10n.T("导出成有声书")} (.m4b · {made.Made} {L10n.T("章")})…",
                    };
                    exportM4bBtn.Click += async (_, _) =>
                    {
                        var picker = new FileSavePicker { SuggestedFileName = $"{_detail.Summary.Title}（广播剧）" };
                        picker.FileTypeChoices.Add("M4B 有声书", [".m4b"]);
                        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                        var outFile = await picker.PickSaveFileAsync();
                        if (outFile is null) return;
                        exportM4bBtn.IsEnabled = false;
                        exportM4bBtn.Content = L10n.T("正在混音并封装 .m4b…");
                        bool ok = await DramaSynthService.ExportM4bAsync(_library, _bookId, outFile.Path, (cur, tot) =>
                            DispatcherQueue.TryEnqueue(() => exportM4bBtn.Content = $"{L10n.T("正在混音封装")} ({cur}/{tot})…"));
                        exportM4bBtn.IsEnabled = true;
                        ShowReaderToast(ok ? $"{L10n.T("已导出有声书：")}{outFile.Name}" : L10n.T("导出有声书失败"));
                        if (_activePanel == "cast") RenderSidePanel();
                    };
                    dramaStack.Children.Add(exportM4bBtn);
                }
            }
            catch { }
        }

        dramaCard.Child = dramaStack;
        body.Children.Add(dramaCard);

        body.Children.Add(new TextBlock
        {
            Text = cast.Length == 0
                ? L10n.T("读到这里还没认出谁在说话。对话多的书读几章后，这里会列出说过话的人。")
                : L10n.T("点一个人：声音、快慢高低、气泡放左边还是右边。改了从下一段起生效，跟着同步到你的其他设备。"),
            FontSize = 10.5,
            LineHeight = 16.5,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 2, 4, 0),
        });

        SideBodyStack.Children.Add(body);
    }

    // 1:1 MacVoicePickerPanel.swift：486×476 无边框双栏声音选择面板（左 136px 分类轨 + 右音色列表 + 内联下载/报错 + 延迟打开克隆窗口防崩）
    private async Task ShowVoicePickerSheetAsync(string title, string? currentSelectedId, Action<string> onPick, string? roleName = null)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        ContentDialog? dlgRef = null;
        string? deferredCloneDetailId = null;
        bool deferredCreateClone = false;
        bool deferredOpenSettings = false;

        string? inferredRole = roleName;
        if (inferredRole is null && title.EndsWith("的声音", StringComparison.Ordinal) && !title.StartsWith("旁白", StringComparison.Ordinal))
        {
            string candidate = title[..^3].Trim();
            if (!string.IsNullOrEmpty(candidate)) inferredRole = candidate;
        }

        string? normCurrent = inferredRole is not null && string.IsNullOrEmpty(currentSelectedId)
            ? null
            : DramaSynthService.NormalizeVoiceId(currentSelectedId ?? _library.Prefs.GetVoiceForLanguage(_detail.Summary.Language));
        string activeCat = "recommended"; // recommended | mine | cloud | system
        string genderFilter = "all";      // all | female | male
        string? previewingId = null;
        var previewErrors = new Dictionary<string, string>(StringComparer.Ordinal);

        Role[] bookRoles;
        try { bookRoles = _library.Core.BookRoles(_bookId); } catch { bookRoles = []; }
        string? GetUsedByLabel(string voiceId)
        {
            string? norm = DramaSynthService.NormalizeVoiceId(voiceId) ?? voiceId;
            var users = bookRoles
                .Where(r => r.Name != inferredRole && !string.IsNullOrEmpty(r.Voice?.Voice) &&
                            string.Equals(DramaSynthService.NormalizeVoiceId(r.Voice!.Voice), norm, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Name)
                .ToArray();
            return users.Length > 0 ? $"{string.Join("、", users.Take(2))}{L10n.T("在用")}" : null;
        }

        Color AvatarColorFor(string name)
        {
            Color[] palette =
            [
                Color.FromArgb(255, 138, 101, 63),
                Color.FromArgb(255, 79, 118, 87),
                Color.FromArgb(255, 79, 109, 136),
                Color.FromArgb(255, 165, 101, 111),
                Color.FromArgb(255, 126, 102, 152),
                Color.FromArgb(255, 166, 118, 56),
            ];
            uint hash = 0;
            foreach (char ch in name) hash = hash * 31 + ch;
            return palette[hash % (uint)palette.Length];
        }

        var sents = _library.GetSpokenSentences(_bookId, _currentSection);
        string sampleText = sents.Count > 0
            ? sents[Math.Clamp(_currentSentence, 0, sents.Count - 1)]
            : L10n.T("你好，这是我的声音。");

        var searchInput = new TextBox
        {
            PlaceholderText = L10n.T("搜声音…"),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 12,
            Padding = new Thickness(2, 4, 2, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var searchCapsule = new Grid { ColumnSpacing = 6, Padding = new Thickness(10, 0, 8, 0), Height = 30 };
        searchCapsule.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchCapsule.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchCapsule.Children.Add(YYIcons.Make("search", 11.5, ThemeHelper.Brush(p.T3)));
        Grid.SetColumn(searchInput, 1);
        searchCapsule.Children.Add(searchInput);

        var genderSegHost = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var railStack = new StackPanel { Spacing = 2, Padding = new Thickness(8, 8, 8, 8) };
        var rightListStack = new StackPanel { Spacing = 4, Padding = new Thickness(10, 8, 12, 10) };

        void RebuildAll()
        {
            var allVoices = AudioPlayer.GetAllVoiceOptions(_library, _bookId);
            string q = searchInput.Text.Trim();

            bool MatchesFilter(string label, string? gender) =>
                (string.IsNullOrEmpty(q) || label.Contains(q, StringComparison.OrdinalIgnoreCase)) &&
                (genderFilter == "all" || string.Equals(gender, genderFilter, StringComparison.OrdinalIgnoreCase));

            var recVoices = allVoices.Where(v => v.Kind is SpeechVoiceKind.Kokoro or SpeechVoiceKind.BuiltinCard or SpeechVoiceKind.BookCard && MatchesFilter(v.Label, v.Gender)).ToList();
            if (!_library.Kokoro.IsInstalled)
            {
                string wantLang = (_detail.Summary.Language ?? "zh").StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
                foreach (var sp in _library.Kokoro.GetSpeakers(wantLang))
                {
                    string g = sp.Gender == CharacterGender.F ? "female" : "male";
                    string lbl = $"{sp.Name} · {sp.Summary}";
                    if (MatchesFilter(lbl, g) && !recVoices.Any(x => x.Id == sp.Id))
                        recVoices.Add(new SpeechVoiceOption(sp.Id, $"{sp.Name}（{sp.Summary}）", true, SpeechVoiceKind.Kokoro, g, sp.Lang));
                }
            }
            var myVoices = allVoices.Where(v => v.Kind == SpeechVoiceKind.ClonedCard && MatchesFilter(v.Label, v.Gender)).ToArray();
            var cloudVoices = allVoices.Where(v => v.Kind == SpeechVoiceKind.Cloud && MatchesFilter(v.Label, v.Gender)).ToArray();
            var sysVoices = allVoices.Where(v => v.Kind == SpeechVoiceKind.System && MatchesFilter(v.Label, v.Gender)).ToArray();

            genderSegHost.Content = YYChrome.MakeSegmentedBar(
                [("all", L10n.T("全部")), ("female", L10n.T("女声")), ("male", L10n.T("男声"))],
                genderFilter,
                nextG => { genderFilter = nextG; RebuildAll(); },
                dark,
                fontSize: 11,
                height: 28);

            railStack.Children.Clear();
            (string Key, string Icon, string Label, int Count)[] cats =
            [
                ("recommended", "sparkle", L10n.T("推荐"), recVoices.Count),
                ("mine", "headphones", L10n.T("我的"), myVoices.Length),
                ("cloud", "globe", L10n.T("云端"), cloudVoices.Length),
                ("system", "laptop", L10n.T("系统自带"), sysVoices.Length),
            ];
            foreach (var (ck, cIcon, cLbl, cCnt) in cats)
            {
                bool on = string.IsNullOrEmpty(q) && activeCat == ck;
                var rg = new Grid { ColumnSpacing = 7, Height = 32, Padding = new Thickness(8, 0, 8, 0) };
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rg.Children.Add(YYIcons.Make(cIcon, 12, ThemeHelper.Brush(on ? tint.Accent : p.T2)));
                var lTb = new TextBlock { Text = cLbl, FontSize = 12, FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(lTb, 1);
                rg.Children.Add(lTb);
                var nTb = new TextBlock { Text = cCnt.ToString(), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(nTb, 2);
                rg.Children.Add(nTb);

                var cb = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(8),
                    Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = rg,
                };
                string nextCat = ck;
                cb.Click += (_, _) =>
                {
                    if (!string.IsNullOrEmpty(searchInput.Text)) searchInput.Text = "";
                    activeCat = nextCat;
                    RebuildAll();
                };
                railStack.Children.Add(cb);
            }

            rightListStack.Children.Clear();

            // Follow narrator row at top when picking for a specific character role
            if (inferredRole is not null && string.IsNullOrEmpty(q))
            {
                string narVid = DramaSynthService.NormalizeVoiceId(_library.Prefs.GetVoiceForLanguage(_detail.Summary.Language)) ?? "";
                var narOpt = allVoices.FirstOrDefault(v => v.Id == narVid || v.Id == "sys:" + narVid);
                string narName = narOpt?.Label.Replace("✦ ", "").Replace("☁ ", "").Split('（')[0].Trim() ?? L10n.T("默认语音");
                bool followOn = string.IsNullOrEmpty(normCurrent);

                var fg = new Grid { ColumnSpacing = 9, Padding = new Thickness(8, 7, 8, 7) };
                fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                fg.Children.Add(new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = ThemeHelper.Brush(p.Inset), Child = YYIcons.Make("book", 12, ThemeHelper.Brush(p.T2)) });
                var fm = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                fm.Children.Add(new TextBlock { Text = L10n.T(_dramaModeOn ? "自动选角" : "跟旁白"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(followOn ? tint.Accent : p.T1) });
                fm.Children.Add(new TextBlock { Text = _dramaModeOn ? L10n.T("按角色性别自动分配音色") : $"{L10n.T("现在旁白是")}{narName}", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                Grid.SetColumn(fm, 1);
                fg.Children.Add(fm);
                if (followOn)
                {
                    var chk = new TextBlock { Text = "✓", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(chk, 2);
                    fg.Children.Add(chk);
                }

                var fBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(followOn ? tint.Soft : p.Card),
                    BorderBrush = ThemeHelper.Brush(followOn ? tint.Edge : p.Line),
                    BorderThickness = new Thickness(0.75),
                    UseSystemFocusVisuals = false,
                    Content = fg,
                };
                fBtn.Click += (_, _) =>
                {
                    AudioPlayer.StopActivePreview();
                    normCurrent = null;
                    onPick("");
                    dlgRef?.Hide();
                };
                rightListStack.Children.Add(fBtn);
            }

            // Inline download cards in 推荐 when Kokoro or LiveVoice is not yet installed
            if ((string.IsNullOrEmpty(q) && activeCat == "recommended") || !string.IsNullOrEmpty(q))
            {
                if (!_library.Kokoro.IsInstalled)
                {
                    bool dl = _library.Kokoro.State == KokoroStateKind.Downloading;
                    var dlRow = new Grid { ColumnSpacing = 8 };
                    dlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    dlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var dm = new StackPanel { Spacing = 1 };
                    dm.Children.Add(new TextBlock { Text = L10n.T("Kokoro 轻量离线旁白 · 125 MB"), FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                    dm.Children.Add(new TextBlock { Text = dl ? _library.Kokoro.StatusMessage : L10n.T("16 个中英自然旁白 · 下载一次断网即听"), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
                    dlRow.Children.Add(dm);
                    var dlBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                        Height = 25,
                        MinHeight = 25,
                        Padding = new Thickness(11, 0, 11, 0),
                        FontSize = 11,
                        IsEnabled = !dl,
                        Content = dl ? $"{(int)Math.Round(_library.Kokoro.DownloadProgress * 100)}%" : L10n.T("下载"),
                    };
                    dlBtn.Click += (_, _) => _ = _library.Kokoro.DownloadAsync();
                    Grid.SetColumn(dlBtn, 1);
                    dlRow.Children.Add(dlBtn);
                    rightListStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(10),
                        Background = ThemeHelper.Brush(tint.Soft),
                        BorderBrush = ThemeHelper.Brush(tint.Edge),
                        BorderThickness = new Thickness(0.75),
                        Padding = new Thickness(10, 8, 10, 8),
                        Child = dlRow,
                    });
                }

                if (!_library.LiveVoice.IsReady && string.IsNullOrEmpty(q))
                {
                    bool dl = _library.LiveVoice.State == LiveVoiceStateKind.Downloading;
                    var dlRow = new Grid { ColumnSpacing = 8 };
                    dlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    dlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var dm = new StackPanel { Spacing = 1 };
                    dm.Children.Add(new TextBlock { Text = L10n.T("离线情感与声音克隆引擎 · 156 MB"), FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                    dm.Children.Add(new TextBlock { Text = dl ? _library.LiveVoice.StatusMessage : L10n.T("含内置角色音色包与本机 8 秒零样本克隆"), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
                    dlRow.Children.Add(dm);
                    var dlBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                        Height = 25,
                        MinHeight = 25,
                        Padding = new Thickness(11, 0, 11, 0),
                        FontSize = 11,
                        IsEnabled = !dl,
                        Content = dl ? $"{(int)Math.Round(_library.LiveVoice.DownloadProgress * 100)}%" : L10n.T("下载"),
                    };
                    dlBtn.Click += (_, _) => _ = _library.LiveVoice.DownloadAsync();
                    Grid.SetColumn(dlBtn, 1);
                    dlRow.Children.Add(dlBtn);
                    rightListStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(10),
                        Background = ThemeHelper.Brush(p.Card),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.75),
                        Padding = new Thickness(10, 8, 10, 8),
                        Child = dlRow,
                    });
                }
            }

            // + 克隆一个声音 row in 我的 (deferred until AFTER ShowSafeAsync returns!)
            if (string.IsNullOrEmpty(q) && activeCat == "mine")
            {
                var cg = new Grid { ColumnSpacing = 9, Padding = new Thickness(8, 7, 8, 7) };
                cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cg.Children.Add(new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = ThemeHelper.Brush(tint.Soft), Child = YYIcons.Make("plus", 12, ThemeHelper.Brush(tint.Accent)) });
                var cm = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                cm.Children.Add(new TextBlock { Text = $"＋ {L10n.T("克隆一个声音")}", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
                cm.Children.Add(new TextBlock { Text = L10n.T("念 8 秒，声音不出本机"), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3) });
                Grid.SetColumn(cm, 1);
                cg.Children.Add(cm);

                var cloneRowBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    UseSystemFocusVisuals = false,
                    Content = cg,
                };
                cloneRowBtn.Click += (_, _) =>
                {
                    deferredCreateClone = true;
                    dlgRef?.Hide();
                };
                rightListStack.Children.Add(cloneRowBtn);
            }

            SpeechVoiceOption[] shown = !string.IsNullOrEmpty(q)
                ? recVoices.Concat(myVoices).Concat(cloudVoices).Concat(sysVoices).ToArray()
                : activeCat switch
                {
                    "mine" => myVoices,
                    "cloud" => cloudVoices,
                    "system" => sysVoices,
                    _ => recVoices.ToArray(),
                };

            if (shown.Length == 0)
            {
                rightListStack.Children.Add(new TextBlock
                {
                    Text = activeCat == "cloud" && string.IsNullOrEmpty(q)
                        ? L10n.T("尚未启用云端音色，可在左下角「语音设置」里开启")
                        : L10n.T("没有匹配的声音"),
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12, 32, 12, 16),
                });
            }

            foreach (var v in shown)
            {
                string vid = v.Id;
                bool active = !string.IsNullOrEmpty(normCurrent) && (vid == normCurrent || vid == "sys:" + normCurrent);
                string cleanTitle = v.Label.Replace("✦ ", "").Replace("☁ ", "").Split('（')[0].Trim();
                int parenIdx = v.Label.IndexOf('（');
                string subNote = parenIdx >= 0 ? v.Label[(parenIdx + 1)..].TrimEnd('）') : v.Kind switch
                {
                    SpeechVoiceKind.Kokoro => L10n.T("Kokoro 离线旁白"),
                    SpeechVoiceKind.Cloud => L10n.T("云端语音"),
                    SpeechVoiceKind.ClonedCard => L10n.T("我的克隆声音"),
                    SpeechVoiceKind.BookCard => L10n.T("本书角色音色"),
                    SpeechVoiceKind.BuiltinCard => L10n.T("本地情感人声"),
                    _ => L10n.T("系统语音"),
                };
                string? usedBy = GetUsedByLabel(vid);
                Color avCol = AvatarColorFor(cleanTitle);
                string avChar = cleanTitle.Length > 0 ? cleanTitle[0].ToString() : "声";

                var itemWrap = new StackPanel { Spacing = 4 };
                var rowGrid = new Grid { ColumnSpacing = 9 };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var mainHitGrid = new Grid { ColumnSpacing = 9 };
                mainHitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                mainHitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                mainHitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                mainHitGrid.Children.Add(new Border
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(14),
                    Background = ThemeHelper.Brush(Color.FromArgb(40, avCol.R, avCol.G, avCol.B)),
                    Child = L10n.Skip(new TextBlock { Text = avChar, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(avCol), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }),
                });

                var metaSp = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                nameLine.Children.Add(L10n.Skip(new TextBlock { Text = cleanTitle, FontSize = 12.5, FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T1) }));
                if (!string.IsNullOrEmpty(usedBy))
                {
                    nameLine.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = ThemeHelper.Brush(p.Inset),
                        Padding = new Thickness(5, 1, 5, 1),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = usedBy, FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T2) },
                    });
                }
                metaSp.Children.Add(nameLine);
                metaSp.Children.Add(new TextBlock { Text = subNote, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(metaSp, 1);
                mainHitGrid.Children.Add(metaSp);

                if (active)
                {
                    var chk = new TextBlock { Text = "✓", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
                    Grid.SetColumn(chk, 2);
                    mainHitGrid.Children.Add(chk);
                }

                // Single-click row selects voice!
                var selectRowBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = mainHitGrid,
                };
                selectRowBtn.Click += (_, _) =>
                {
                    AudioPlayer.StopActivePreview();
                    if (v.Kind == SpeechVoiceKind.Kokoro && !_library.Kokoro.IsInstalled)
                    {
                        string langKey = (_detail.Summary.Language ?? "zh").StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
                        _library.Kokoro.QueueSelection(vid, langKey);
                    }
                    onPick(vid);
                    dlgRef?.Hide();
                };
                rowGrid.Children.Add(selectRowBtn);

                if (v.Kind == SpeechVoiceKind.ClonedCard)
                {
                    var detailBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                        Height = 24,
                        MinHeight = 24,
                        Padding = new Thickness(8, 0, 8, 0),
                        FontSize = 10.5,
                        Content = L10n.T("管理"),
                    };
                    detailBtn.Click += (_, _) =>
                    {
                        deferredCloneDetailId = vid;
                        dlgRef?.Hide();
                    };
                    Grid.SetColumn(detailBtn, 1);
                    rowGrid.Children.Add(detailBtn);
                }

                bool isPrev = previewingId == vid;
                var prevBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(14),
                    Background = ThemeHelper.Brush(p.Inset),
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    UseSystemFocusVisuals = false,
                    Content = YYIcons.Make(isPrev ? "pause" : "play", 10.5, ThemeHelper.Brush(p.T1)),
                };
                ToolTipService.SetToolTip(prevBtn, L10n.T("试听"));
                prevBtn.Click += async (_, _) =>
                {
                    if (previewingId == vid)
                    {
                        AudioPlayer.StopActivePreview();
                        previewingId = null;
                        RebuildAll();
                        return;
                    }
                    if (v.Kind == SpeechVoiceKind.Kokoro && !_library.Kokoro.IsInstalled)
                    {
                        previewErrors[vid] = L10n.T("请先点上方「下载」安装 Kokoro 离线语音包");
                        RebuildAll();
                        return;
                    }
                    previewErrors.Remove(vid);
                    previewingId = vid;
                    RebuildAll();
                    try
                    {
                        await AudioPlayer.PreviewVoiceAsync(_library, vid, _speechRate, sampleText);
                    }
                    catch (Exception ex)
                    {
                        previewErrors[vid] = ex.Message;
                    }
                    finally
                    {
                        if (previewingId == vid) previewingId = null;
                        RebuildAll();
                    }
                };
                Grid.SetColumn(prevBtn, 2);
                rowGrid.Children.Add(prevBtn);
                itemWrap.Children.Add(rowGrid);

                if (previewErrors.TryGetValue(vid, out var errMsg) && !string.IsNullOrWhiteSpace(errMsg))
                {
                    itemWrap.Children.Add(new TextBlock
                    {
                        Text = errMsg,
                        FontSize = 10.5,
                        Foreground = ThemeHelper.Brush(p.Danger),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(37, 0, 4, 0),
                    });
                }

                rightListStack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(active ? tint.Soft : p.Card),
                    BorderBrush = ThemeHelper.Brush(active ? tint.Edge : p.Line),
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(9, 7, 8, 7),
                    Child = itemWrap,
                });
            }
        }

        searchInput.TextChanged += (_, _) => RebuildAll();
        Action onModelStateChanged = () => DispatcherQueue.TryEnqueue(RebuildAll);
        _library.Kokoro.StateChanged += onModelStateChanged;
        _library.LiveVoice.StateChanged += onModelStateChanged;
        RebuildAll();

        // Assemble 486×476 two-pane MacVoicePickerPanel
        var topFilterGrid = new Grid { ColumnSpacing = 8, Padding = new Thickness(16, 0, 16, 10) };
        topFilterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topFilterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topFilterGrid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(p.Inset),
            Child = searchCapsule,
        });
        Grid.SetColumn(genderSegHost, 1);
        topFilterGrid.Children.Add(genderSegHost);

        var twoPaneGrid = new Grid { Height = 342 };
        twoPaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });
        twoPaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.5) });
        twoPaneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        twoPaneGrid.Children.Add(new Border { Background = ThemeHelper.Brush(p.Inset), Child = railStack });
        var vLine = new Border { Background = ThemeHelper.Brush(p.Line) };
        Grid.SetColumn(vLine, 1);
        twoPaneGrid.Children.Add(vLine);
        var rightScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rightListStack };
        Grid.SetColumn(rightScroll, 2);
        twoPaneGrid.Children.Add(rightScroll);

        var footerGrid = new Grid { Padding = new Thickness(14, 7, 14, 8) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.Children.Add(new TextBlock
        {
            Text = $"{L10n.T("试听：")}《{_detail.Summary.Title}》{L10n.T("停着的那一句")}",
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var openSettingsBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 2, 2),
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(tint.Accent),
            UseSystemFocusVisuals = false,
            Content = L10n.T("语音设置…"),
        };
        openSettingsBtn.Click += (_, _) =>
        {
            deferredOpenSettings = true;
            dlgRef?.Hide();
        };
        Grid.SetColumn(openSettingsBtn, 1);
        footerGrid.Children.Add(openSettingsBtn);

        var panelRoot = new StackPanel { Spacing = 0 };
        var headerWrap = new Border { Padding = new Thickness(16, 14, 14, 10), Child = YYChrome.MakeSheetHeader(string.IsNullOrWhiteSpace(title) ? L10n.T("声音") : title, null, () => dlgRef?.Hide(), dark, titleSize: 16) };
        panelRoot.Children.Add(headerWrap);
        panelRoot.Children.Add(topFilterGrid);
        panelRoot.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
        panelRoot.Children.Add(twoPaneGrid);
        panelRoot.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
        panelRoot.Children.Add(footerGrid);

        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, dark, 486, panelRoot, maxHeight: 520, tint: _library.Prefs.Tint);
        try
        {
            await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
        }
        finally
        {
            _library.Kokoro.StateChanged -= onModelStateChanged;
            _library.LiveVoice.StateChanged -= onModelStateChanged;
            AudioPlayer.StopActivePreview();
        }

        if (deferredOpenSettings)
        {
            App.CurrentApp.OpenSettings("listening");
            return;
        }
        if (deferredCreateClone)
        {
            var cloned = await VoiceCloneAndDramaDialogs.ShowCloneVoiceDialogAsync(this, RootGrid.XamlRoot, _library);
            if (cloned is not null)
                onPick(cloned.Id);
            return;
        }
        if (!string.IsNullOrEmpty(deferredCloneDetailId))
        {
            await VoiceCloneAndDramaDialogs.ShowClonedCardDetailDialogAsync(this, RootGrid.XamlRoot, _library, deferredCloneDetailId);
            await ShowVoicePickerSheetAsync(title, currentSelectedId, onPick, inferredRole);
        }
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

    private YYPaper ResolveActiveYYPaper()
    {
        if (_library.Prefs.GlobalEinkMode)
            return YYPapers.FromId("eink");
        var resolved = _previewPaper is not null
            ? new PaperAppearance.Resolved(YuyueCoreMethods.PaperColors(_previewPaper).Preset, _previewPaper)
            : PaperAppearance.Resolve(_library, _settings);
        var paper = YYPapers.FromId(PaperCssId(resolved.Preset));
        if (resolved.Custom is not null)
        {
            try
            {
                var pc = YuyueCoreMethods.PaperColors(resolved.Custom);
                return paper with
                {
                    IsDark = pc.Dark,
                    Bg = CustomPaperDialog.ParseHexColor(pc.Bg, paper.Bg),
                    Ink = CustomPaperDialog.ParseHexColor(pc.Ink, paper.Ink),
                    Ink2 = CustomPaperDialog.ParseHexColor(pc.Ink2, paper.Ink2),
                    Line = CustomPaperDialog.ParseHexColor(pc.Line, paper.Line),
                    Glass = CustomPaperDialog.ParseHexColor(pc.Glass, pc.Dark ? Color.FromArgb(215, 28, 29, 32) : Color.FromArgb(224, 255, 255, 255)),
                };
            }
            catch { }
        }
        return paper;
    }

    private void CheckInitialRecapAndSleepOffer()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var activePaper = ResolveActiveYYPaper();

        // 0a. DoubtPill for TXT books (ReaderReturn.swift:80-113)
        if (_detail.Summary.Format == BookFormat.Txt && !_library.Prefs.DismissedTxtDoubtBooks.Contains(_bookId))
        {
            try
            {
                string? doubt = _library.Core.TxtDoubt(_bookId);
                if (!string.IsNullOrWhiteSpace(doubt))
                {
                    _hasActiveDoubtPill = true;
                    DoubtPillHost.Content = ReaderRecapAndSleepHelper.BuildDoubtPill(
                        doubt,
                        p,
                        onOpen: async () =>
                        {
                            _hasActiveDoubtPill = false;
                            UpdateBottomChromeVisibility();
                            await OpenTxtLayoutDialogAsync();
                        },
                        onDismiss: () =>
                        {
                            _hasActiveDoubtPill = false;
                            UpdateBottomChromeVisibility();
                            _library.Prefs.DismissedTxtDoubtBooks.Add(_bookId);
                            _library.SavePrefs();
                        },
                        paper: activePaper);
                    UpdateBottomChromeVisibility();
                }
            }
            catch { }
        }

        // 0b. ElsewhereCard when another synced device moved to a newer timestamp (forward OR backward, ReaderReturn.swift:6-74)
        if (CheckElsewhereProgress(p, tint))
            return;

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

        // 2. RecapTopPill (1:1 ReaderRecap.swift: right-toolbar pill; paid cloud waits for user click)
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
            if (daysAway >= 1 && (_currentSection > _detail.Start || _currentSentence > 20))
            {
                uint snapSec = _currentSection;
                int snapSent = _currentSentence;
                _pendingRecapOffer = (snapSec, snapSent, daysAway, lms);
                _readyRecap = ReaderRecapAndSleepHelper.TryGetCachedRecap(_library, _bookId, snapSec, snapSent, daysAway);
                RecapTopPillLabel.Text = L10n.T("前情提要");
                RecapTopPillDays.Text = L10n.T($"{daysAway}天前");
                RecapTopPillBtn.Visibility = Visibility.Visible;
                ApplyTintAndChrome();

                // Free/local models pre-warm silently in background without auto-covering prose
                if (_readyRecap is null && !_library.Companion.IsCurrentProviderPaidCloud)
                {
                    _ = Task.Run(async () =>
                    {
                        var pre = await ReaderRecapAndSleepHelper.TryLoadOrGenerateRecapAsync(
                            _library, _bookId, _detail, lms, snapSec, snapSent, manual: false);
                        if (pre is not null)
                        {
                            DispatcherQueue.TryEnqueue(() => _readyRecap = pre);
                        }
                    });
                }
            }
        }
    }

    private bool CheckElsewhereProgress(YYPalette p, YYTintColors tint)
    {
        try
        {
            var latest = _library.Core.LatestProgress(_bookId);
            var here = _library.GetProgress(_bookId);
            long mineAt = here?.At ?? 0;
            double hereFrac = here?.Fraction ?? CalculateBookFraction(_currentSection, _currentSentence);
            bool differentSpot = latest is not null
                && (latest.Position.Section != _currentSection || (int)latest.Position.Sentence != _currentSentence);

            if (latest is not null
                && !latest.ThisDevice
                && differentSpot
                && latest.At > mineAt
                && Math.Abs(latest.Fraction - hereFrac) > 0.005)
            {
                string? chapTitle = ResolveSectionTitle(latest.Position.Section);
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
                    tint,
                    onStay: () =>
                    {
                        ElsewhereCardHost.Visibility = Visibility.Collapsed;
                        _hasUserMoved = true;
                        SaveCurrentProgress();
                    },
                    onJump: () =>
                    {
                        ElsewhereCardHost.Visibility = Visibility.Collapsed;
                        _hasUserMoved = true;
                        bool wasPlaying = _audio.IsPlaying;
                        if (wasPlaying && latest.Position.Section != _currentSection)
                            _pendingAutoSpeak = true;
                        JumpTo(latest.Position.Section, (int)latest.Position.Sentence, recordReturn: true);
                        if (wasPlaying && latest.Position.Section == _currentSection)
                            StartListeningAt((int)latest.Position.Sentence);
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
        if (RecapCardHost.Visibility == Visibility.Visible)
        {
            RecapCardHost.Visibility = Visibility.Collapsed;
            return;
        }

        if (_readyRecap is { } ready)
        {
            ShowRecapPopover(ready, manual: false);
            return;
        }

        if (_recapLoading || _pendingRecapOffer is not { } offer) return;
        _recapLoading = true;
        RecapTopPillLabel.Text = L10n.T("前情提要");
        RecapTopPillDays.Text = "";
        ApplyTintAndChrome();
        try
        {
            var got = await ReaderRecapAndSleepHelper.TryLoadOrGenerateRecapAsync(
                _library, _bookId, _detail, offer.LastReadMs, offer.Section, offer.Sentence, manual: true);
            if (got is not null)
            {
                _readyRecap = got with { Days = offer.Days };
                ShowRecapPopover(_readyRecap, manual: false);
            }
            else
            {
                RecapTopPillBtn.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            _recapLoading = false;
            RecapTopPillLabel.Text = L10n.T("前情提要");
            RecapTopPillDays.Text = L10n.T($"{offer.Days}天前");
            ApplyTintAndChrome();
        }
    }

    private void ShowRecapPopover(RecapDto recap, bool manual)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        RecapCardHost.Content = ReaderRecapAndSleepHelper.BuildRecapCard(recap, p, tint, manual, () =>
        {
            RecapCardHost.Visibility = Visibility.Collapsed;
        });
        L10n.ApplyTree(RecapCardHost);
        RecapCardHost.Visibility = Visibility.Visible;
    }

    private sealed record CompanionSlashCommandDef(
        string Name,
        string Title,
        string Subtitle,
        string Icon,
        string[] Keywords,
        bool TakesArg = false,
        string? ArgHint = null);

    // 1:1 BookCompanion.swift:311-435
    private static readonly CompanionSlashCommandDef[] AllCompanionCommands =
    [
        new("recap", "回顾前情", "梳理目前为止发生了什么", "clock-history", ["前情", "回顾", "剧情", "摘要", "summary", "previously"], TakesArg: true, ArgHint: "话题"),
        new("chapter", "总结本章", "概括当前章节的主要内容", "doc-lines", ["本章", "章节", "总结", "这一章", "summary"], TakesArg: true, ArgHint: "重点"),
        new("characters", "人物关系", "梳理已出场人物与关系", "people", ["人物", "角色", "关系", "谁", "who", "cast"], TakesArg: true, ArgHint: "人物名"),
        new("explain", "解释含义", "解读选中句子或当前段落", "doc-search", ["解释", "解析", "意思", "含义", "meaning"], TakesArg: true, ArgHint: "词句"),
        new("quote", "挑出金句", "从已读内容里找几句值得回味的话", "text-quote", ["金句", "摘录", "名句", "句子", "highlight"], TakesArg: true, ArgHint: "主题"),
        new("quiz", "考考我", "根据已读内容出两道思考题", "feedback", ["考考我", "测验", "提问", "思考", "test"], TakesArg: true, ArgHint: "范围"),
        new("read", "仅限已读", "切回不剧透模式", "eye-slash", ["已读", "不剧透", "防剧透", "spoiler"], TakesArg: true),
        new("whole", "全书范围", "检索整本书（可能剧透）", "book-open", ["全书", "剧透", "整本", "all"], TakesArg: true),
        new("model", "切换模型", "打开模型与服务商选择", "sparkle", ["模型", "服务商", "切换", "provider"]),
        new("clear", "清空对话", "清除当前这本书的对话记录", "trash", ["清空", "新对话", "清除", "重置", "reset", "new"]),
        new("settings", "书伴设置", "配置服务商与 API Key", "gear", ["设置", "配置", "key", "api"]),
    ];

    private void UpdateCompanionModelPillLabel()
    {
        if (CompanionModelPillText is null) return;
        string provName = CompanionClient.FindPreset(_library.Prefs.AiProvider)?.Name
            ?? (_library.Prefs.AiCustomProviderNames.TryGetValue(_library.Prefs.AiProvider, out var cn) ? cn : _library.Prefs.AiProvider);
        string modelShort = string.IsNullOrWhiteSpace(_library.Prefs.AiModel) ? provName : _library.Prefs.AiModel.Split('/').Last();
        L10n.Skip(CompanionModelPillText);
        CompanionModelPillText.Text = modelShort;
    }

    private string[] GetCompanionReadyQuestions()
    {
        if (!string.IsNullOrWhiteSpace(_companionQuote))
            return [L10n.T("这句是什么意思？"), L10n.T("为什么这么写？")];
        if (_lastCompanionNextQuestions.Length > 0)
            return _lastCompanionNextQuestions;
        return [L10n.T("前面发生了什么？"), L10n.T("这一章讲了什么？")];
    }

    private void RebuildCompanionReadyLinesCard()
    {
        if (CompanionReadyLinesCard is null || CompanionReadyLinesStack is null) return;
        CompanionReadyLinesStack.Children.Clear();

        var lines = GetCompanionReadyQuestions();
        if (lines.Length == 0)
        {
            CompanionReadyLinesCard.Visibility = Visibility.Collapsed;
            return;
        }

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                CompanionReadyLinesStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(8, 2, 8, 2) });

            string lineText = lines[i];
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(YYIcons.Make("sparkle", 11.5, ThemeHelper.Brush(tint.Accent)));
            var qTb = new TextBlock
            {
                Text = lineText,
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T1),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(qTb, 1);
            row.Children.Add(qTb);

            var arrowTb = new TextBlock
            {
                Text = "↩",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(arrowTb, 2);
            row.Children.Add(arrowTb);

            var lineBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 7, 8, 7),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = row,
            };
            lineBtn.Click += async (_, _) =>
            {
                CompanionReadyLinesCard.Visibility = Visibility.Collapsed;
                await SubmitCompanionQuestionAsync(lineText);
            };
            CompanionReadyLinesStack.Children.Add(lineBtn);
        }
    }

    private void CompanionPromptToggle_Click(object sender, RoutedEventArgs e)
    {
        if (CompanionReadyLinesCard is null) return;
        if (CompanionReadyLinesCard.Visibility == Visibility.Visible)
        {
            CompanionReadyLinesCard.Visibility = Visibility.Collapsed;
            return;
        }

        HideCompanionSlashPopup();
        CompanionModelFlyout?.Hide();
        RebuildCompanionReadyLinesCard();
        if (CompanionReadyLinesStack.Children.Count > 0)
            CompanionReadyLinesCard.Visibility = Visibility.Visible;
    }

    private void OpenCompanionWithQuote(string quote)
    {
        _companionQuote = quote;
        CompanionQuoteText.Text = $"“{quote}”";
        CompanionQuoteBanner.Visibility = Visibility.Visible;
        if (CompanionReadyLinesCard.Visibility == Visibility.Visible)
            RebuildCompanionReadyLinesCard();
        OpenPanel("companion");
        CompanionInputBox.Focus(FocusState.Programmatic);
    }

    private void ClearCompanionQuote_Click(object sender, RoutedEventArgs e)
    {
        _companionQuote = null;
        CompanionQuoteBanner.Visibility = Visibility.Collapsed;
        if (CompanionReadyLinesCard.Visibility == Visibility.Visible)
            RebuildCompanionReadyLinesCard();
    }

    private void ResetCompanionWelcome()
    {
        try { _companionCts?.Cancel(); _companionCts?.Dispose(); } catch { }
        _companionCts = null;
        _pendingSourceHighlight = null;
        _lastCompanionNextQuestions = [];
        if (_pager.IsInitialized) _ = _host.SetSourceAsync(null);
        _companionHistory.Clear();
        HideCompanionSlashPopup();
        if (CompanionReadyLinesCard is not null)
            CompanionReadyLinesCard.Visibility = Visibility.Collapsed;
        if (CompanionInputBox is not null)
            CompanionInputBox.PlaceholderText = L10n.T("问这本书，或输入 / …");
        UpdateCompanionSendButtonVisuals();

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        CompanionMessages.Children.Clear();

        var welcomeStack = new StackPanel { Spacing = 6 };
        welcomeStack.Children.Add(new TextBlock { Text = "读到哪儿问到哪儿", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        welcomeStack.Children.Add(new TextBlock
        {
            Text = "可以问人物关系、这句是什么意思、前面发生了什么。回答标着出处，点一下就翻到那句。",
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

        UpdateCompanionModelPillLabel();

        CompanionMessages.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Inset),
            Padding = new Thickness(14),
            Child = welcomeStack,
        });
        L10n.ApplyTree(CompanionMessages);
    }

    private static string ExtractSlashDraftArgument(string rawDraft)
    {
        string trimmed = rawDraft.Trim();
        if (trimmed.StartsWith('/') || trimmed.StartsWith('／'))
        {
            string rest = trimmed[1..];
            int wsIdx = rest.IndexOfAny([' ', '\t', '\r', '\n']);
            return wsIdx >= 0 ? rest[(wsIdx + 1)..].Trim() : "";
        }
        return trimmed;
    }

    private CompanionSlashCommandDef[] GetFilteredSlashCommands()
    {
        string raw = CompanionInputBox.Text;
        string trimmedStart = raw.TrimStart();
        string query = "";
        if (trimmedStart.StartsWith('/') || trimmedStart.StartsWith('／'))
        {
            string afterSlash = trimmedStart[1..];
            int wsIdx = afterSlash.IndexOfAny([' ', '\t', '\r', '\n']);
            if (wsIdx >= 0 && !_slashManualOpen)
                return [];
            query = wsIdx >= 0 ? afterSlash[..wsIdx].Trim() : afterSlash.Trim();
        }
        else if (!_slashManualOpen)
        {
            return [];
        }

        if (string.IsNullOrEmpty(query))
            return AllCompanionCommands;

        string q = query.ToLowerInvariant();
        return AllCompanionCommands
            .Select(cmd =>
            {
                int rank = -1;
                if (cmd.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) rank = 0;
                else if (cmd.Title.StartsWith(q, StringComparison.OrdinalIgnoreCase)) rank = 1;
                else if (cmd.Keywords.Any(k => k.StartsWith(q, StringComparison.OrdinalIgnoreCase))) rank = 2;
                else if (cmd.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || cmd.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || cmd.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || cmd.Keywords.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase)))
                    rank = 3;
                return (rank, cmd);
            })
            .Where(x => x.rank >= 0)
            .OrderBy(x => x.rank)
            .Select(x => x.cmd)
            .ToArray();
    }

    private void HideCompanionSlashPopup()
    {
        _slashManualOpen = false;
        if (CompanionSlashPopup is not null)
            CompanionSlashPopup.Visibility = Visibility.Collapsed;
    }

    private void RenderCompanionSlashPopup()
    {
        var matches = GetFilteredSlashCommands();
        if (matches.Length == 0 || (_slashDismissedForCurrentToken && !_slashManualOpen))
        {
            CompanionSlashPopup.Visibility = Visibility.Collapsed;
            return;
        }

        if (CompanionReadyLinesCard is not null)
            CompanionReadyLinesCard.Visibility = Visibility.Collapsed;

        _slashSelectedIndex = Math.Clamp(_slashSelectedIndex, 0, matches.Length - 1);
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        CompanionSlashList.Children.Clear();
        for (int i = 0; i < matches.Length; i++)
        {
            int idx = i;
            var cmd = matches[i];
            bool selected = i == _slashSelectedIndex;
            bool isCurrentScope = (cmd.Name == "read" && !_companionWholeBook) || (cmd.Name == "whole" && _companionWholeBook);

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(selected ? tint.Soft : p.Inset),
                Child = YYIcons.Make(cmd.Icon, 12, ThemeHelper.Brush(selected ? tint.Accent : p.T2)),
            });

            var slashTb = L10n.Skip(new TextBlock
            {
                Text = "/" + cmd.Name,
                FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(selected ? tint.Accent : p.T1),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(slashTb, 1);
            row.Children.Add(slashTb);

            var mid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock
            {
                Text = L10n.T(cmd.Title),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = ThemeHelper.Brush(p.T1),
                VerticalAlignment = VerticalAlignment.Center,
            });
            mid.Children.Add(new TextBlock
            {
                Text = L10n.T(cmd.Subtitle),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(mid, 2);
            row.Children.Add(mid);

            if (isCurrentScope)
            {
                var curBadge = new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Background = ThemeHelper.Brush(tint.Soft),
                    Padding = new Thickness(5, 1, 5, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = L10n.T("当前"), FontSize = 9.5, Foreground = ThemeHelper.Brush(tint.Accent) },
                };
                Grid.SetColumn(curBadge, 3);
                row.Children.Add(curBadge);
            }
            else if (!string.IsNullOrEmpty(cmd.ArgHint))
            {
                var argHintTb = new TextBlock
                {
                    Text = $"[{L10n.T(cmd.ArgHint)}]",
                    FontSize = 10,
                    Foreground = ThemeHelper.Brush(p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(argHintTb, 3);
                row.Children.Add(argHintTb);
            }

            var itemBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(7, 5, 8, 5),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(0),
                Background = selected ? ThemeHelper.Brush(tint.Soft) : new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = row,
            };
            itemBtn.Click += async (_, _) =>
            {
                _slashSelectedIndex = idx;
                await ActivateSelectedSlashCommandAsync(cmd);
            };
            CompanionSlashList.Children.Add(itemBtn);
        }

        CompanionSlashPopup.Visibility = Visibility.Visible;
    }

    private async Task ActivateSelectedSlashCommandAsync(CompanionSlashCommandDef cmd)
    {
        string existingArg = cmd.TakesArg ? ExtractSlashDraftArgument(CompanionInputBox.Text) : "";
        HideCompanionSlashPopup();
        string commandLine = string.IsNullOrEmpty(existingArg)
            ? "/" + cmd.Name
            : $"/{cmd.Name} {existingArg}";
        await SubmitCompanionQuestionAsync(commandLine);
    }

    private void CompanionSlashBtn_Click(object sender, RoutedEventArgs e)
    {
        if (CompanionReadyLinesCard is not null)
            CompanionReadyLinesCard.Visibility = Visibility.Collapsed;
        CompanionModelFlyout?.Hide();

        if (CompanionSlashPopup.Visibility == Visibility.Visible)
        {
            string cur = CompanionInputBox.Text.Trim();
            if (cur == "/" || cur == "／")
                CompanionInputBox.Text = "";
            HideCompanionSlashPopup();
            return;
        }

        _slashDismissedForCurrentToken = false;
        _slashSelectedIndex = 0;
        if (string.IsNullOrWhiteSpace(CompanionInputBox.Text))
        {
            _slashManualOpen = false;
            CompanionInputBox.Text = "/";
            CompanionInputBox.SelectionStart = 1;
        }
        else
        {
            _slashManualOpen = true;
        }
        RenderCompanionSlashPopup();
        CompanionInputBox.Focus(FocusState.Programmatic);
    }

    private void CompanionInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCompanionSendButtonVisuals();
        if (CompanionReadyLinesCard is not null && !string.IsNullOrEmpty(CompanionInputBox.Text))
            CompanionReadyLinesCard.Visibility = Visibility.Collapsed;

        string t = CompanionInputBox.Text.TrimStart();
        if (!t.StartsWith('/') && !t.StartsWith('／'))
        {
            _slashDismissedForCurrentToken = false;
            _slashManualOpen = false;
            CompanionSlashPopup.Visibility = Visibility.Collapsed;
            return;
        }
        _slashManualOpen = false;
        RenderCompanionSlashPopup();
    }

    private async void CompanionInput_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (CompanionSlashPopup.Visibility != Visibility.Visible) return;
        var matches = GetFilteredSlashCommands();
        if (matches.Length == 0)
        {
            HideCompanionSlashPopup();
            return;
        }

        if (e.Key == VirtualKey.Down)
        {
            _slashSelectedIndex = (_slashSelectedIndex + 1) % matches.Length;
            RenderCompanionSlashPopup();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Up)
        {
            _slashSelectedIndex = (_slashSelectedIndex - 1 + matches.Length) % matches.Length;
            RenderCompanionSlashPopup();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Tab)
        {
            var chosen = matches[Math.Clamp(_slashSelectedIndex, 0, matches.Length - 1)];
            string existingArg = chosen.TakesArg ? ExtractSlashDraftArgument(CompanionInputBox.Text) : "";
            string text = chosen.TakesArg
                ? (string.IsNullOrEmpty(existingArg) ? $"/{chosen.Name} " : $"/{chosen.Name} {existingArg}")
                : $"/{chosen.Name}";
            HideCompanionSlashPopup();
            CompanionInputBox.Text = text;
            CompanionInputBox.SelectionStart = text.Length;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            _slashDismissedForCurrentToken = true;
            HideCompanionSlashPopup();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
            if (!shift)
            {
                e.Handled = true;
                var chosen = matches[Math.Clamp(_slashSelectedIndex, 0, matches.Length - 1)];
                await ActivateSelectedSlashCommandAsync(chosen);
            }
        }
    }

    private void CompanionInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == VirtualKey.Enter)
        {
            var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
            if (!shift) { CompanionSend_Click(sender, e); e.Handled = true; }
        }
    }

    private async void CompanionSend_Click(object sender, RoutedEventArgs e)
    {
        var q = CompanionInputBox.Text.Trim();
        if (string.IsNullOrEmpty(q)) return;
        await SubmitCompanionQuestionAsync(q);
    }

    private async Task SubmitCompanionQuestionAsync(string rawInput)
    {
        string trimmed = rawInput.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        HideCompanionSlashPopup();
        if (CompanionReadyLinesCard is not null)
            CompanionReadyLinesCard.Visibility = Visibility.Collapsed;

        var activeQuote = _companionQuote;
        CompanionInputBox.Text = "";
        CompanionInputBox.PlaceholderText = L10n.T("继续问，或输入 / …");
        UpdateCompanionSendButtonVisuals();

        if (trimmed.StartsWith('/') || trimmed.StartsWith('／'))
        {
            string body = trimmed[1..].Trim();
            int spIdx = body.IndexOfAny([' ', '\t', '\r', '\n']);
            string cmdToken = (spIdx >= 0 ? body[..spIdx] : body).Trim().ToLowerInvariant();
            string arg = spIdx >= 0 ? body[(spIdx + 1)..].Trim() : "";

            var matchedCmd = AllCompanionCommands.FirstOrDefault(c =>
                string.Equals(c.Name, cmdToken, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Title, cmdToken, StringComparison.OrdinalIgnoreCase));
            string cmdName = matchedCmd?.Name ?? cmdToken;

            switch (cmdName)
            {
                case "clear":
                    ResetCompanionWelcome();
                    return;

                case "settings":
                    App.CurrentApp.OpenSettings("companion");
                    return;

                case "read":
                    _companionWholeBook = false;
                    UpdateCompanionScopeVisuals();
                    if (!string.IsNullOrWhiteSpace(arg))
                    {
                        ClearCompanionQuote_Click(this, new RoutedEventArgs());
                        await AskCompanionCoreAsync(arg, activeQuote, wholeBookOverride: false);
                    }
                    else
                    {
                        ShowReaderToast("书伴范围已切换为：仅限已读");
                    }
                    return;

                case "whole":
                    _companionWholeBook = true;
                    UpdateCompanionScopeVisuals();
                    if (!string.IsNullOrWhiteSpace(arg))
                    {
                        ClearCompanionQuote_Click(this, new RoutedEventArgs());
                        await AskCompanionCoreAsync(arg, activeQuote, wholeBookOverride: true);
                    }
                    else
                    {
                        ShowReaderToast("书伴范围已切换为：全书范围");
                    }
                    return;

                case "model":
                    if (string.IsNullOrWhiteSpace(arg))
                        OpenInlineCompanionModelPicker();
                    else
                        TrySwitchCompanionModelByQuery(arg);
                    return;

                // 1:1 BookCompanion.swift:487-490 — /recap answers inside Companion chat!
                case "recap":
                {
                    ClearCompanionQuote_Click(this, new RoutedEventArgs());
                    string prompt = !string.IsNullOrWhiteSpace(arg)
                        ? $"帮我回顾一下目前为止关于「{arg}」的情节。"
                        : "帮我回顾一下目前为止发生了什么？";
                    await AskCompanionCoreAsync(prompt, activeQuote, wholeBookOverride: null);
                    return;
                }

                case "chapter":
                {
                    ClearCompanionQuote_Click(this, new RoutedEventArgs());
                    string? chap = GetChapterTitleForSection(_currentSection);
                    string prompt = !string.IsNullOrWhiteSpace(arg)
                        ? $"结合「{arg}」，总结一下这一章讲了什么？"
                        : (!string.IsNullOrWhiteSpace(chap)
                            ? $"总结一下「{chap}」这一章讲了什么？"
                            : "总结一下这一章讲了什么？");
                    await AskCompanionCoreAsync(prompt, activeQuote, wholeBookOverride: null);
                    return;
                }

                case "characters":
                {
                    ClearCompanionQuote_Click(this, new RoutedEventArgs());
                    string prompt = !string.IsNullOrWhiteSpace(arg)
                        ? $"介绍一下「{arg}」是谁，以及和其他人物的关系。"
                        : "梳理一下目前出场的主要人物和他们之间的关系。";
                    await AskCompanionCoreAsync(prompt, activeQuote, wholeBookOverride: null);
                    return;
                }

                case "explain":
                {
                    ClearCompanionQuote_Click(this, new RoutedEventArgs());
                    string? targetQuote = activeQuote ?? _openMark?.Text ?? _selection?.Text;
                    if (!string.IsNullOrWhiteSpace(arg))
                    {
                        await AskCompanionCoreAsync($"解释一下「{arg}」在书里的意思和作用。", targetQuote, wholeBookOverride: null);
                    }
                    else if (!string.IsNullOrWhiteSpace(targetQuote))
                    {
                        await AskCompanionCoreAsync("这句是什么意思？结合上下文解释一下。", targetQuote, wholeBookOverride: null);
                    }
                    else
                    {
                        string? fallbackSent = null;
                        try
                        {
                            var sents = _library.GetSentences(_bookId, _currentSection);
                            if (sents.Count > 0)
                                fallbackSent = sents[Math.Clamp(_currentSentence, 0, sents.Count - 1)];
                        }
                        catch { }
                        await AskCompanionCoreAsync("结合上下文，解释一下当前这一段的意思和用意。", fallbackSent, wholeBookOverride: null);
                    }
                    return;
                }

                case "quote":
                {
                    ClearCompanionQuote_Click(this, new RoutedEventArgs());
                    string prompt = !string.IsNullOrWhiteSpace(arg)
                        ? $"从已读内容里挑几句和「{arg}」相关的精彩句子，并标出出处。"
                        : "从目前读过的内容里，挑两三句值得回味的句子并简短点评。";
                    await AskCompanionCoreAsync(prompt, activeQuote, wholeBookOverride: null);
                    return;
                }

                case "quiz":
                {
                    ClearCompanionQuote_Click(this, new RoutedEventArgs());
                    string prompt = !string.IsNullOrWhiteSpace(arg)
                        ? $"围绕「{arg}」，根据我已读的内容出两道思考题考考我。"
                        : "根据我目前读过的内容，出两道有意思的思考题考考我。";
                    await AskCompanionCoreAsync(prompt, activeQuote, wholeBookOverride: null);
                    return;
                }
            }
        }

        ClearCompanionQuote_Click(this, new RoutedEventArgs());
        await AskCompanionCoreAsync(trimmed, activeQuote, wholeBookOverride: null);
    }

    private void CompanionChangeModel_Click(object sender, RoutedEventArgs e)
    {
        OpenInlineCompanionModelPicker();
    }

    private void OpenInlineCompanionModelPicker()
    {
        HideCompanionSlashPopup();
        if (CompanionReadyLinesCard is not null)
            CompanionReadyLinesCard.Visibility = Visibility.Collapsed;

        if (CompanionModelFlyout is not null && CompanionModelPillBtn is not null)
        {
            PopulateCompanionModelFlyout();
            CompanionModelFlyout.ShowAt(CompanionModelPillBtn);
        }
        else
        {
            App.CurrentApp.OpenSettings("companion");
        }
    }

    private void CompanionModelFlyout_Opened(object sender, object e)
    {
        HideCompanionSlashPopup();
        if (CompanionReadyLinesCard is not null)
            CompanionReadyLinesCard.Visibility = Visibility.Collapsed;
        PopulateCompanionModelFlyout();
    }

    private List<(string ProviderId, string ProviderName, List<string> Models)> GetAvailableCompanionProviderModels()
    {
        var prefs = _library.Prefs;
        var providerIds = new List<string>();

        void AddProviderIfAvailable(string pid)
        {
            if (string.IsNullOrWhiteSpace(pid) || providerIds.Contains(pid, StringComparer.OrdinalIgnoreCase))
                return;
            var preset = CompanionClient.FindPreset(pid);
            bool isCurrent = string.Equals(pid, prefs.AiProvider, StringComparison.OrdinalIgnoreCase);
            bool inConfigured = prefs.ConfiguredAiProviders.Contains(pid, StringComparer.OrdinalIgnoreCase);
            bool hasSavedKey = !string.IsNullOrWhiteSpace(_library.Secrets.Get($"ai.key.{pid}"))
                || (isCurrent && !string.IsNullOrWhiteSpace(_library.Secrets.Get("ai.apiKey")));
            bool keylessReady = preset is { NeedsKey: false } && (inConfigured || isCurrent);

            if (isCurrent || hasSavedKey || keylessReady || (inConfigured && !string.IsNullOrWhiteSpace(preset?.DefaultKey)))
                providerIds.Add(pid);
        }

        AddProviderIfAvailable(prefs.AiProvider);
        foreach (var pid in prefs.ConfiguredAiProviders)
            AddProviderIfAvailable(pid);
        foreach (var preset in CompanionClient.Presets)
            AddProviderIfAvailable(preset.Id);

        var result = new List<(string ProviderId, string ProviderName, List<string> Models)>();
        foreach (var pid in providerIds)
        {
            var preset = CompanionClient.FindPreset(pid);
            string pName = preset?.Name
                ?? (prefs.AiCustomProviderNames.TryGetValue(pid, out var cn) && !string.IsNullOrWhiteSpace(cn) ? cn : pid);

            var models = new List<string>();
            void AddModel(string? m)
            {
                if (!string.IsNullOrWhiteSpace(m) && !models.Contains(m.Trim(), StringComparer.OrdinalIgnoreCase))
                    models.Add(m.Trim());
            }

            if (string.Equals(pid, prefs.AiProvider, StringComparison.OrdinalIgnoreCase))
                AddModel(prefs.AiModel);
            if (prefs.AiProviderModels.TryGetValue(pid, out var savedM))
                AddModel(savedM);
            AddModel(preset?.DefaultModel);
            AddModel(preset?.DefaultFastModel);

            foreach (var starred in prefs.StarredAiModels)
            {
                string prefix = pid + ":";
                if (starred.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    AddModel(starred[prefix.Length..]);
            }
            if (prefs.AiRecentModels.TryGetValue(pid, out var recents))
            {
                foreach (var rm in recents.Take(6))
                    AddModel(rm);
            }
            foreach (var cached in _library.Companion.GetCachedModels(pid).Take(8))
                AddModel(cached);

            if (models.Count > 0)
                result.Add((pid, pName, models));
        }

        return result;
    }

    private void SwitchCompanionModel(string providerId, string modelId)
    {
        var prefs = _library.Prefs;
        var preset = CompanionClient.FindPreset(providerId);
        bool sameProvider = string.Equals(prefs.AiProvider, providerId, StringComparison.OrdinalIgnoreCase);

        string existingPerProviderKey = _library.Secrets.Get($"ai.key.{providerId}");
        string currentGlobalKey = _library.Secrets.Get("ai.apiKey");
        if (sameProvider && string.IsNullOrWhiteSpace(existingPerProviderKey) && !string.IsNullOrWhiteSpace(currentGlobalKey))
        {
            _library.Secrets.Set($"ai.key.{providerId}", currentGlobalKey);
            existingPerProviderKey = currentGlobalKey;
        }

        prefs.AiProvider = providerId;
        prefs.AiModel = modelId.Trim();
        prefs.AiProviderModels[providerId] = modelId.Trim();
        if (prefs.AiProviderUrls.TryGetValue(providerId, out var customUrl) && !string.IsNullOrWhiteSpace(customUrl))
            prefs.AiBaseUrl = customUrl;
        else if (!sameProvider && !string.IsNullOrWhiteSpace(preset?.BaseUrl))
            prefs.AiBaseUrl = preset.BaseUrl;
        else if (string.IsNullOrWhiteSpace(prefs.AiBaseUrl) && !string.IsNullOrWhiteSpace(preset?.BaseUrl))
            prefs.AiBaseUrl = preset.BaseUrl;

        if (!prefs.ConfiguredAiProviders.Contains(providerId, StringComparer.OrdinalIgnoreCase))
            prefs.ConfiguredAiProviders.Insert(0, providerId);

        if (!string.IsNullOrWhiteSpace(existingPerProviderKey))
            _library.Secrets.Set("ai.apiKey", existingPerProviderKey);
        else if (!sameProvider)
            _library.Secrets.Set("ai.apiKey", preset?.DefaultKey ?? "");

        _library.Companion.RecordModelUsed(providerId, modelId.Trim());
        _library.SavePrefs();
        UpdateCompanionModelPillLabel();
    }

    private void TrySwitchCompanionModelByQuery(string query)
    {
        string q = query.Trim();
        var groups = GetAvailableCompanionProviderModels();

        foreach (var grp in groups)
        {
            var exact = grp.Models.FirstOrDefault(m => string.Equals(m, q, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                SwitchCompanionModel(grp.ProviderId, exact);
                ShowReaderToast($"已切换模型：{grp.ProviderName} · {exact}");
                return;
            }
        }

        foreach (var grp in groups)
        {
            if (grp.ProviderId.Equals(q, StringComparison.OrdinalIgnoreCase) || grp.ProviderName.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                string targetM = grp.Models[0];
                SwitchCompanionModel(grp.ProviderId, targetM);
                ShowReaderToast($"已切换模型：{grp.ProviderName} · {targetM}");
                return;
            }
            var partial = grp.Models.FirstOrDefault(m => m.Contains(q, StringComparison.OrdinalIgnoreCase));
            if (partial is not null)
            {
                SwitchCompanionModel(grp.ProviderId, partial);
                ShowReaderToast($"已切换模型：{grp.ProviderName} · {partial}");
                return;
            }
        }

        if (_library.Companion.IsConfigured && q.Length >= 2 && !q.Contains(' '))
        {
            SwitchCompanionModel(_library.Prefs.AiProvider, q);
            ShowReaderToast($"已切换模型：{q}");
            return;
        }

        OpenInlineCompanionModelPicker();
    }

    private void PopulateCompanionModelFlyout()
    {
        if (CompanionModelFlyoutHost is null) return;
        CompanionModelFlyoutHost.Children.Clear();

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var groups = GetAvailableCompanionProviderModels();

        if (groups.Count == 0 || !_library.Companion.IsConfigured)
        {
            CompanionModelFlyoutHost.Children.Add(new TextBlock
            {
                Text = L10n.T("尚未配置可用的书伴模型"),
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(8, 6, 8, 4),
            });
        }

        for (int gi = 0; gi < groups.Count; gi++)
        {
            var grp = groups[gi];
            if (gi > 0)
                CompanionModelFlyoutHost.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(6, 4, 6, 2) });

            CompanionModelFlyoutHost.Children.Add(new TextBlock
            {
                Text = grp.ProviderName,
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(8, 4, 8, 2),
            });

            foreach (var modelId in grp.Models)
            {
                bool active = string.Equals(_library.Prefs.AiProvider, grp.ProviderId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_library.Prefs.AiModel, modelId, StringComparison.OrdinalIgnoreCase);
                string capProv = grp.ProviderId;
                string capModel = modelId;

                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (active)
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = "✓",
                        FontSize = 11.5,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = ThemeHelper.Brush(tint.Accent),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                var mTb = L10n.Skip(new TextBlock
                {
                    Text = modelId,
                    FontSize = 12,
                    FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T1),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                Grid.SetColumn(mTb, 1);
                row.Children.Add(mTb);

                var mBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(6, 5, 8, 5),
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(0),
                    Background = active ? ThemeHelper.Brush(tint.Soft) : new SolidColorBrush(Colors.Transparent),
                    UseSystemFocusVisuals = false,
                    Content = row,
                };
                mBtn.Click += (_, _) =>
                {
                    SwitchCompanionModel(capProv, capModel);
                    CompanionModelFlyout.Hide();
                };
                CompanionModelFlyoutHost.Children.Add(mBtn);
            }
        }

        CompanionModelFlyoutHost.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(6, 4, 6, 2) });
        var moreBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 12,
            Foreground = ThemeHelper.Brush(tint.Accent),
            UseSystemFocusVisuals = false,
            Content = L10n.T("更多设置…"),
        };
        moreBtn.Click += (_, _) =>
        {
            CompanionModelFlyout.Hide();
            App.CurrentApp.OpenSettings("companion");
        };
        CompanionModelFlyoutHost.Children.Add(moreBtn);
    }

    private void JumpToCompanionSource(CompanionClient.CompanionSource source)
    {
        _pendingSourceHighlight = (source.Section, (int)source.Sentence);
        bool sameSec = source.Section == _currentSection;
        JumpTo(source.Section, (int)source.Sentence, recordReturn: true);
        if (sameSec)
            _ = _host.SetSourceAsync((int)source.Sentence);
    }

    // 1:1 MacReaderCards.swift:1443-1470 — inline " 第 N 页 " / " 第 N 页 · 未读 " tags instead of raw [n] + duplicate bottom pills
    private void RenderCompanionAnswerInlines(
        TextBlock replyTb,
        string displayText,
        IReadOnlyList<CompanionClient.CompanionSource> sources,
        YYTintColors tint)
    {
        replyTb.Text = "";
        replyTb.Inlines.Clear();
        if (string.IsNullOrEmpty(displayText)) return;

        var sourceByIdx = new Dictionary<int, CompanionClient.CompanionSource>();
        foreach (var src in sources)
            sourceByIdx[src.Index] = src;

        var matches = Regex.Matches(displayText, @"\s?\[(\d+)\]");
        if (matches.Count == 0 || sourceByIdx.Count == 0)
        {
            replyTb.Inlines.Add(new Run { Text = displayText });
            return;
        }

        int cursor = 0;
        foreach (Match m in matches)
        {
            if (m.Index > cursor)
                replyTb.Inlines.Add(new Run { Text = displayText[cursor..m.Index] });

            if (int.TryParse(m.Groups[1].Value, out int citeNum) && sourceByIdx.TryGetValue(citeNum, out var matchedSource))
            {
                var captured = matchedSource;
                int pg = EstimateBookPage(captured.Section, (int)captured.Sentence);
                string place = $"{L10n.T("第")} {pg} {L10n.T("页")}";
                string rawTag = " " + place + (captured.Later ? $" · {L10n.T("未读")}" : "") + " ";
                string nbspTag = rawTag.Replace(' ', '\u00A0');

                replyTb.Inlines.Add(new Run { Text = " " });
                var link = new Hyperlink
                {
                    UnderlineStyle = captured.Later ? UnderlineStyle.Single : UnderlineStyle.None,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    FontSize = 10.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                };
                link.Inlines.Add(new Run { Text = nbspTag });
                link.Click += (_, _) => JumpToCompanionSource(captured);
                replyTb.Inlines.Add(link);
            }
            else
            {
                replyTb.Inlines.Add(new Run { Text = m.Value });
            }

            cursor = m.Index + m.Length;
        }

        if (cursor < displayText.Length)
            replyTb.Inlines.Add(new Run { Text = displayText[cursor..] });
    }

    private async Task AskCompanionCoreAsync(string q, string? quote, bool? wholeBookOverride)
    {
        if (!_library.Companion.IsConfigured)
        {
            App.CurrentApp.OpenSettings("companion");
            return;
        }

        bool useWhole = wholeBookOverride ?? _companionWholeBook;
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        try { _companionCts?.Cancel(); _companionCts?.Dispose(); } catch { }
        var localCts = new CancellationTokenSource();
        _companionCts = localCts;
        var ct = localCts.Token;

        CompanionMessages.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14, 14, 4, 14),
            Background = ThemeHelper.Brush(p.Inset),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 280,
            Child = new TextBlock { Text = q, FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
        });

        var loadingRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        loadingRow.Children.Add(new ProgressRing { IsActive = true, Width = 12, Height = 12 });
        loadingRow.Children.Add(new TextBlock { Text = L10n.T("正在翻阅原文…"), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3) });

        var replyTb = new TextBlock { Visibility = Visibility.Collapsed, FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap, LineHeight = 21, IsTextSelectionEnabled = true };
        var extraActionHost = new StackPanel { Spacing = 4 };
        var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
        metaRow.Children.Add(YYIcons.Make(useWhole ? "book-open" : "sparkle", 9.5, ThemeHelper.Brush(p.T3)));
        var modelMeta = new TextBlock
        {
            Text = useWhole ? $"{_library.Companion.CurrentProviderLabel} · {L10n.T("用了全书")}" : _library.Companion.CurrentProviderLabel,
            FontSize = 10,
            Foreground = ThemeHelper.Brush(p.T3),
        };
        metaRow.Children.Add(modelMeta);

        var replyStack = new StackPanel { Spacing = 6, Padding = new Thickness(2, 4, 2, 4), Children = { loadingRow, replyTb, extraActionHost, metaRow } };
        CompanionMessages.Children.Add(replyStack);
        CompanionSendBtn.IsEnabled = false;
        UpdateCompanionSendButtonVisuals(tint);

        try
        {
            double frac = CalculateBookFraction(_currentSection, _currentSentence);
            var answer = await _library.Companion.AskBookStructuredAsync(
                _bookId, _detail.Summary.Title, _currentSection, _currentSentence,
                q, quote, useWhole, _companionHistory, _detail, frac, ct);
            if (ct.IsCancellationRequested) return;

            loadingRow.Visibility = Visibility.Collapsed;
            replyTb.Visibility = Visibility.Visible;

            bool isNotInRead = answer.NotInRead || string.Equals((answer.Text ?? "").Trim(), "没读到", StringComparison.Ordinal);
            if (isNotInRead && !useWhole)
            {
                int page = EstimateBookPage(_currentSection, _currentSentence);
                string notYetCopy = L10n.T($"截至第 {page} 页，这件事可能要到后面才写。切换至「全书范围」即可作答，但可能包含后续剧透。");
                replyTb.Text = notYetCopy;
                _lastCompanionNextQuestions = [];

                var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                btnRow.Children.Add(new TextBlock { Text = L10n.T("用全书回答"), FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                btnRow.Children.Add(new TextBlock { Text = $"⚠ {L10n.T("可能剧透")}", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.Warn), VerticalAlignment = VerticalAlignment.Center });

                var wholeBtn = new Button
                {
                    Height = 28,
                    MinHeight = 28,
                    Padding = new Thickness(12, 0, 12, 0),
                    CornerRadius = new CornerRadius(14),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 3, 0, 0),
                    UseSystemFocusVisuals = false,
                    Content = btnRow,
                };
                string origQ = q;
                string? origQuote = quote;
                wholeBtn.Click += async (_, _) => await AskCompanionCoreAsync(origQ, origQuote, wholeBookOverride: true);
                extraActionHost.Children.Add(wholeBtn);
            }
            else
            {
                string renderedText = answer.Text ?? "";
                RenderCompanionAnswerInlines(replyTb, renderedText, answer.Sources, tint);

                if (!string.IsNullOrEmpty(answer.ProviderLabel))
                    modelMeta.Text = useWhole ? $"{answer.ProviderLabel} · {L10n.T("用了全书")}" : answer.ProviderLabel;
                metaRow.Visibility = Visibility.Visible;

                _companionHistory.Add(("user", q));
                _companionHistory.Add(("assistant", renderedText));
                while (_companionHistory.Count > 4) _companionHistory.RemoveAt(0);

                // Save contextual follow-ups for ReadyLinesCard (“ toggle, 1:1 MacReaderCards.swift:1492-1495)
                _lastCompanionNextQuestions = answer.NextQuestions?.Take(2).ToArray() ?? [];
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            loadingRow.Visibility = Visibility.Collapsed;
            replyTb.Visibility = Visibility.Visible;
            replyTb.Foreground = ThemeHelper.Brush(p.Danger);
            replyTb.Text = L10n.T($"未能获得回答：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_companionCts, localCts))
                _companionCts = null;
            CompanionSendBtn.IsEnabled = true;
            UpdateCompanionSendButtonVisuals();
            CompanionScroll.ChangeView(null, CompanionScroll.ScrollableHeight, null);
        }
    }

    private string? GetChapterTitleForSection(uint section)
    {
        string? title = null;
        if (section < _detail.Sections.Length && !string.IsNullOrWhiteSpace(_detail.Sections[section].Title))
            title = _detail.Sections[section].Title!.Trim();
        else if (_detail.Toc.Length > 0)
            title = _detail.Toc.LastOrDefault(t => t.Section <= section)?.Title?.Trim();

        if (string.IsNullOrWhiteSpace(title) || string.Equals(title, _detail.Summary.Title.Trim(), StringComparison.Ordinal))
            return null;
        return title;
    }

    // ---------- Audio, ListenCapsule & 448px ListenCard (1:1 MacReaderCards.swift:623-1067) ----------

    private void StartListeningAt(int sentenceIndex)
    {
        var sentences = _library.GetSpokenSentences(_bookId, _currentSection);
        if (sentences.Count == 0) return;

        SleepReturnHost.Visibility = Visibility.Collapsed;
        ReaderRecapAndSleepHelper.ClearSleepReturn(_library, _bookId);

        _hasUserMoved = true;
        _hasPausedAudioSession = false;
        _pausedOffSpot = false;
        _manualPageTurnIgnoreUntilUtc = DateTime.MinValue;
        _lastFollowedSentence = sentenceIndex;

        _sectionSentenceCount = sentences.Count;
        string chapter = _currentSection < _detail.Sections.Length ? _detail.Sections[_currentSection].Title ?? "" : "";
        _audio.SetMetadata(_detail.Summary.Title, chapter, _detail.Summary.CoverPath);
        _audio.SetSectionContext(_currentSection, _dramaModeOn);
        _audio.LoopSingleSentence = _loopSingleSentence;
        _audio.ShadowPauseMode = _shadowPauseMode;
        _audio.StopAfterCurrentSentence = _sleepMode == -2;
        _audio.SetRate(_speechRate);
        _audio.Start(sentences, sentenceIndex);
        _capsuleAutoHideTimer?.Stop();
        if (!_isFullScreen || _immersivePeeking) ListenCapsule.Visibility = Visibility.Visible;
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
        string recPrefix = _audio.IsUsingPairedRecording ? $"{L10n.T("原声")} · " : "";

        CapsuleSentenceText.Text = $"{recPrefix}{L10n.T($"第 {cur} / {total} 句")}";
        CapsuleNoteText.Text = L10n.T($"{unit} {chapterPct}% · 还剩 {remMin} 分钟");
        CapsuleProgDoneCol.Width = new GridLength(ratio, GridUnitType.Star);
        CapsuleProgRestCol.Width = new GridLength(Math.Max(0, 1.0 - ratio), GridUnitType.Star);
    }

    private void ToggleListen_Click(object sender, RoutedEventArgs e)
    {
        if (_audio.IsPlaying)
        {
            _audio.Pause();
            _hasPausedAudioSession = true;
            _pausedOffSpot = false;
        }
        else if (_hasPausedAudioSession && !_pausedOffSpot)
        {
            SleepReturnHost.Visibility = Visibility.Collapsed;
            ReaderRecapAndSleepHelper.ClearSleepReturn(_library, _bookId);
            _manualPageTurnIgnoreUntilUtc = DateTime.MinValue;
            _hasPausedAudioSession = false;
            _pausedOffSpot = false;
            _capsuleAutoHideTimer?.Stop();
            if (!_isFullScreen || _immersivePeeking) ListenCapsule.Visibility = Visibility.Visible;
            _audio.Resume();
        }
        else
        {
            _manualPageTurnIgnoreUntilUtc = DateTime.MinValue;
            _hasPausedAudioSession = false;
            _pausedOffSpot = false;
            StartListeningAt(_currentSentence);
        }
    }

    private void SpeakPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_audio.IsPlaying || _hasPausedAudioSession)
        {
            _manualPageTurnIgnoreUntilUtc = DateTime.MinValue;
            bool wasOffSpot = !_audio.IsPlaying && _pausedOffSpot;
            int baseSent = wasOffSpot ? _currentSentence : Math.Max(0, _audio.CurrentSentence);
            _pausedOffSpot = false;

            if (baseSent <= 0)
            {
                if (_library.PreviousSection(_bookId, _currentSection, speech: true) is uint prevSec)
                {
                    int prevCount = _library.GetSpokenSentences(_bookId, prevSec).Count;
                    int lastIdx = Math.Max(0, prevCount - 1);
                    _hasUserMoved = true;
                    _pendingSentence = lastIdx;
                    _pendingAutoSpeak = true;
                    LoadSection(prevSec);
                }
                else
                {
                    StartListeningAt(0);
                    if (_settings.Mode == LayoutMode.Scrolled)
                        _ = _host.ScrollToSentenceAsync(0, animated: true, align: "third");
                }
                return;
            }

            int targetSent = baseSent - 1;
            if (wasOffSpot)
                StartListeningAt(targetSent);
            else
                _audio.Previous();

            if (_settings.Mode == LayoutMode.Scrolled)
                _ = _host.ScrollToSentenceAsync(targetSent, animated: true, align: "third");
        }
        else
        {
            _ = TurnPrevAsync();
        }
    }

    private void SpeakNext_Click(object sender, RoutedEventArgs e)
    {
        if (_audio.IsPlaying || _hasPausedAudioSession)
        {
            _manualPageTurnIgnoreUntilUtc = DateTime.MinValue;
            bool wasOffSpot = !_audio.IsPlaying && _pausedOffSpot;
            int baseSent = wasOffSpot ? _currentSentence : Math.Max(0, _audio.CurrentSentence);
            _pausedOffSpot = false;

            if (_sectionSentenceCount > 0 && baseSent + 1 >= _sectionSentenceCount)
            {
                if (_library.NextSection(_bookId, _currentSection, speech: true) is uint nextSec)
                {
                    _hasUserMoved = true;
                    _pendingSentence = 0;
                    _pendingAutoSpeak = true;
                    LoadSection(nextSec);
                }
                return;
            }

            int targetSent = baseSent + 1;
            if (wasOffSpot)
                StartListeningAt(targetSent);
            else
                _audio.Next();

            if (_settings.Mode == LayoutMode.Scrolled)
                _ = _host.ScrollToSentenceAsync(targetSent, animated: true, align: "third");
        }
        else
        {
            _ = TurnNextAsync();
        }
    }

    private void ExitListenOnly_Click(object sender, RoutedEventArgs e)
    {
        _listenOnly = false;
        ListenOnlyCover.Visibility = Visibility.Collapsed;
    }

    private void OnListenModelStateChanged()
    {
        if (!_listenFlyoutOpen) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_listenFlyoutOpen) RebuildListenCard();
        });
    }

    private void ListenCardFlyout_Opened(object sender, object e)
    {
        _listenFlyoutOpen = true;
        _capsuleAutoHideTimer?.Stop();
        _library.Kokoro.StateChanged -= OnListenModelStateChanged;
        _library.LiveVoice.StateChanged -= OnListenModelStateChanged;
        _library.Kokoro.StateChanged += OnListenModelStateChanged;
        _library.LiveVoice.StateChanged += OnListenModelStateChanged;
        RebuildListenCard();
    }

    private void ListenCardFlyout_Closed(object? sender, object e)
    {
        _listenFlyoutOpen = false;
        _library.Kokoro.StateChanged -= OnListenModelStateChanged;
        _library.LiveVoice.StateChanged -= OnListenModelStateChanged;
        if (_isFullScreen && !_pointerInBottomPeek)
            ScheduleImmersivePeekHide();
        if (!_audio.IsPlaying) ScheduleCapsuleAutoHide();
    }

    private void ScheduleCapsuleAutoHide()
    {
        _capsuleAutoHideTimer?.Stop();
        _capsuleAutoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _capsuleAutoHideTimer.Tick += (_, _) =>
        {
            _capsuleAutoHideTimer.Stop();
            if (!_audio.IsPlaying && !_listenFlyoutOpen)
                ListenCapsule.Visibility = Visibility.Collapsed;
        };
        _capsuleAutoHideTimer.Start();
    }

    // 1:1 MacReaderCards.swift:677-1171
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
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(5, 1.5, 5, 1.5),
            Child = new TextBlock { Text = L10n.T(k), FontSize = 9.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) },
        };

        // 1. Header: 听书 + 快捷键键帽
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = L10n.T("听书"), FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        caps.Children.Add(KeyCap("空格"));
        caps.Children.Add(new TextBlock { Text = L10n.T("暂停"), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        caps.Children.Add(KeyCap("Ctrl+["));
        caps.Children.Add(KeyCap("Ctrl+]"));
        caps.Children.Add(new TextBlock { Text = L10n.T("上一句 / 下一句"), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(caps);
        ListenCardHost.Children.Add(head);

        // 1b. Paired Audiobook 原声 ToggleSwitch row (1:1 MacReaderCards.swift:976-995)
        if (_audio.HasPairedRecording)
        {
            bool recOn = _audio.IsUsingPairedRecording;
            var narrRow = new Grid { ColumnSpacing = 12 };
            narrRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            narrRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            narrRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            narrRow.Children.Add(new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(recOn ? tint.Soft : p.Card),
                Child = YYIcons.Make("speaker", 15, ThemeHelper.Brush(recOn ? tint.Accent : p.T1)),
            });

            var narrMeta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            narrMeta.Children.Add(new TextBlock
            {
                Text = L10n.T("原声"),
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
            });
            narrMeta.Children.Add(new TextBlock
            {
                Text = L10n.T(recOn
                    ? "播放有声书原声，缺失处自动补读；若高亮错位，选中正在念的句子点「对准」"
                    : "已关闭，使用下方朗读音色"),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(narrMeta, 1);
            narrRow.Children.Add(narrMeta);

            var recSwitch = new ToggleSwitch
            {
                IsOn = recOn,
                OnContent = null,
                OffContent = null,
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            recSwitch.Toggled += (_, _) =>
            {
                bool nextOn = recSwitch.IsOn;
                if (nextOn == _audio.IsUsingPairedRecording) return;
                _audio.SetPairedRecording(_pairedAudioBookId, _pairedRecording, _pairedMap, useRecording: nextOn);
                UpdateTopInfoBanners();
                UpdateCapsuleText(Math.Max(0, _audio.CurrentSentence >= 0 ? _audio.CurrentSentence : _currentSentence));
                RebuildListenCard();
            };
            Grid.SetColumn(recSwitch, 2);
            narrRow.Children.Add(recSwitch);

            ListenCardHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = ThemeHelper.Brush(p.Inset),
                Padding = new Thickness(10),
                Child = narrRow,
            });
        }

        // 2. Narrator Voice Card (1:1 MacReaderCards.swift:890-951)
        string bookLang = _detail.Summary.Language ?? "zh-Hans";
        string curVoicePref = DramaSynthService.NormalizeVoiceId(_library.Prefs.GetVoiceForLanguage(bookLang)) ?? "";
        var curVoice = AudioPlayer.GetAllVoiceOptions(_library, _bookId).FirstOrDefault(v => v.Id == curVoicePref || v.Id == "sys:" + curVoicePref);
        var voiceGrid = new Grid { ColumnSpacing = 10 };
        voiceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        voiceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        voiceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        voiceGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        bars.Children.Add(new Border { Width = 2.5, Height = _audio.IsPlaying ? 10 : 6, CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Bottom });
        bars.Children.Add(new Border { Width = 2.5, Height = _audio.IsPlaying ? 15 : 9, CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Bottom });
        bars.Children.Add(new Border { Width = 2.5, Height = _audio.IsPlaying ? 11 : 7, CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Bottom });
        voiceGrid.Children.Add(new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Background = ThemeHelper.Brush(p.Card), BorderBrush = ThemeHelper.Brush(p.Line), BorderThickness = new Thickness(0.5), Child = bars });

        string engineKindLabel = curVoice?.Kind switch
        {
            SpeechVoiceKind.Cloud => L10n.T("云端（已读内容本地缓存）"),
            _ => L10n.T("本地离线"),
        };
        var voiceParts = new List<string>();
        if (_audio.IsPlaying) voiceParts.Add(L10n.T("正在朗读"));
        voiceParts.Add(_isForeignBook ? "English" : L10n.T("中文"));
        voiceParts.Add(engineKindLabel);

        var vMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var vNameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        vNameRow.Children.Add(new TextBlock
        {
            Text = curVoice?.Label ?? L10n.T("系统默认语音"),
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            MaxWidth = 176,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (curVoice is null || curVoice.Kind == SpeechVoiceKind.System)
            vNameRow.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = ThemeHelper.Brush(p.Card), Padding = new Thickness(5, 1, 5, 1), Child = new TextBlock { Text = L10n.T("系统自带"), FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T2) } });
        vMeta.Children.Add(vNameRow);
        vMeta.Children.Add(new TextBlock { Text = string.Join(" · ", voiceParts), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T2), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(vMeta, 1);
        voiceGrid.Children.Add(vMeta);

        var auditionSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        auditionSp.Children.Add(YYIcons.Make("play", 8.5, ThemeHelper.Brush(p.T1)));
        auditionSp.Children.Add(new TextBlock { Text = L10n.T("试听"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        var auditionBtn = new Button
        {
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(9, 0, 10, 0),
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            UseSystemFocusVisuals = false,
            Content = auditionSp,
        };
        auditionBtn.Click += (_, _) =>
        {
            var sents = _library.GetSpokenSentences(_bookId, _currentSection);
            string sample = sents.Count > 0 ? sents[Math.Clamp(_currentSentence, 0, sents.Count - 1)] : _detail.Summary.Title;
            _ = AudioPlayer.PreviewVoiceAsync(_library, curVoicePref, _speechRate, sample);
        };
        Grid.SetColumn(auditionBtn, 2);
        voiceGrid.Children.Add(auditionBtn);

        var switchVoiceBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 4, 2, 4),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T2),
            UseSystemFocusVisuals = false,
            Content = $"{L10n.T("换声音")} ›",
        };
        switchVoiceBtn.Click += async (_, _) =>
        {
            ListenCardFlyout.Hide();
            await ShowVoicePickerSheetAsync("声音", curVoicePref, vid =>
            {
                _library.Prefs.SetVoiceForLanguage(bookLang, DramaSynthService.NormalizeVoiceId(vid) ?? vid);
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
            Padding = new Thickness(9, 9, 8, 9),
            Child = voiceGrid,
        });

        // 2b. KokoroPrepBanner / LiveVoicePrepBanner when offline voice engine is uninstalled or downloading (1:1 MacReaderCards.swift:744-784)
        bool supportsCardLang = bookLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || bookLang.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        if (!_audio.IsUsingPairedRecording && supportsCardLang)
        {
            if (!_library.Kokoro.IsInstalled)
            {
                bool dl = _library.Kokoro.State == KokoroStateKind.Downloading;
                var prepRow = new Grid { ColumnSpacing = 8 };
                prepRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                prepRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var pm = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                pm.Children.Add(new TextBlock
                {
                    Text = L10n.T("准备更好的离线旁白（Kokoro · 125 MB）"),
                    FontSize = 11.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                });
                pm.Children.Add(new TextBlock
                {
                    Text = dl ? _library.Kokoro.StatusMessage : L10n.T("下载一次断网即听，下好后自动可用"),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T2),
                    TextWrapping = TextWrapping.Wrap,
                });
                prepRow.Children.Add(pm);
                var prepBtn = new Button
                {
                    Height = 26,
                    MinHeight = 26,
                    Padding = new Thickness(11, 0, 11, 0),
                    CornerRadius = new CornerRadius(13),
                    Background = ThemeHelper.Brush(tint.Soft),
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    BorderThickness = new Thickness(0),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    IsEnabled = !dl,
                    UseSystemFocusVisuals = false,
                    Content = dl ? $"{(int)Math.Round(_library.Kokoro.DownloadProgress * 100)}%" : L10n.T("下载"),
                };
                prepBtn.Click += (_, _) => _ = _library.Kokoro.DownloadAsync();
                Grid.SetColumn(prepBtn, 1);
                prepRow.Children.Add(prepBtn);

                ListenCardHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Bg),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(11, 8, 11, 8),
                    Child = prepRow,
                });
            }
            else if (!_library.LiveVoice.IsReady && (_dramaModeOn || _library.LiveVoice.State == LiveVoiceStateKind.Downloading))
            {
                bool dl = _library.LiveVoice.State == LiveVoiceStateKind.Downloading;
                var liveRow = new Grid { ColumnSpacing = 8 };
                liveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                liveRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var lm = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                lm.Children.Add(new TextBlock
                {
                    Text = L10n.T("准备广播剧与声音克隆引擎（156 MB）"),
                    FontSize = 11.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                });
                lm.Children.Add(new TextBlock
                {
                    Text = dl ? _library.LiveVoice.StatusMessage : L10n.T("下载后可启用多角色情感台词与 8 秒本机克隆"),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T2),
                    TextWrapping = TextWrapping.Wrap,
                });
                liveRow.Children.Add(lm);
                var liveBtn = new Button
                {
                    Height = 26,
                    MinHeight = 26,
                    Padding = new Thickness(11, 0, 11, 0),
                    CornerRadius = new CornerRadius(13),
                    Background = ThemeHelper.Brush(tint.Soft),
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    BorderThickness = new Thickness(0),
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    IsEnabled = !dl,
                    UseSystemFocusVisuals = false,
                    Content = dl ? $"{(int)Math.Round(_library.LiveVoice.DownloadProgress * 100)}%" : L10n.T("下载"),
                };
                liveBtn.Click += (_, _) => _ = _library.LiveVoice.DownloadAsync();
                Grid.SetColumn(liveBtn, 1);
                liveRow.Children.Add(liveBtn);

                ListenCardHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Bg),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(11, 8, 11, 8),
                    Child = liveRow,
                });
            }
        }

        // 3. Speed Section (0.8 .. 2.0× with [0.8, 1.0, 1.2, 1.5, 2.0] marks, 1:1 MacReaderCards.swift:710, 997-1034)
        int totalSents = Math.Max(1, _sectionSentenceCount);
        int curSent = Math.Clamp(_audio.CurrentSentence >= 0 ? _audio.CurrentSentence + 1 : _currentSentence + 1, 1, totalSents);
        int remMin = Math.Max(1, (int)Math.Ceiling(Math.Max(0, totalSents - curSent) * 3.8 / 60.0 / Math.Max(0.5, _speechRate)));
        string unit = _detail.Summary.Format == BookFormat.Podcast ? "本集" : "本章";

        var speedStack = new StackPanel { Spacing = 4 };
        var speedHead = new Grid { Padding = new Thickness(2, 0, 2, 0) };
        var spLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        spLeft.Children.Add(new TextBlock { Text = L10n.T("速度"), FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        var speedValTb = L10n.Skip(new TextBlock { Text = $"{_speechRate:0.0}×", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        spLeft.Children.Add(speedValTb);
        spLeft.Children.Add(new Border { CornerRadius = new CornerRadius(5), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = L10n.T("不变调"), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T2) } });
        speedHead.Children.Add(spLeft);
        var speedRemTb = new TextBlock { Text = L10n.T($"{unit}约剩 {remMin} 分钟"), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        speedHead.Children.Add(speedRemTb);
        speedStack.Children.Add(speedHead);

        double clampedRate = Math.Clamp(_speechRate, 0.8, 2.0);
        var speedSlider = new Slider { Minimum = 0.8, Maximum = 2.0, StepFrequency = 0.05, Value = clampedRate };
        double[] ticks = [0.8, 1.0, 1.2, 1.5, 2.0];
        var tickBtns = new List<(double Mark, Button Btn)>();

        void RefreshSpeedTickStyles()
        {
            foreach (var (mk, btn) in tickBtns)
            {
                bool hit = Math.Abs(_speechRate - mk) < 0.05;
                btn.Foreground = ThemeHelper.Brush(hit ? p.T1 : p.T3);
                btn.FontWeight = hit ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            }
        }

        speedSlider.ValueChanged += (_, ev) =>
        {
            _speechRate = Math.Round(ev.NewValue, 1);
            speedValTb.Text = $"{_speechRate:0.0}×";
            int rm = Math.Max(1, (int)Math.Ceiling(Math.Max(0, totalSents - curSent) * 3.8 / 60.0 / Math.Max(0.5, _speechRate)));
            speedRemTb.Text = L10n.T($"{unit}约剩 {rm} 分钟");
            RefreshSpeedTickStyles();
            _audio.SetRate(_speechRate);
            _library.Prefs.SpeechRate = _speechRate;
            _library.SavePrefs();
            UpdateCapsuleText(Math.Max(0, _audio.CurrentSentence));
        };

        var ticksCanvas = new Canvas { Height = 18 };
        foreach (double tv in ticks)
        {
            double markVal = tv;
            var tb = new Button
            {
                Content = $"{markVal:0.0}",
                Width = 28,
                Height = 18,
                MinWidth = 28,
                MinHeight = 18,
                Padding = new Thickness(0),
                FontSize = 10,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
            };
            tb.Click += (_, _) =>
            {
                _speechRate = markVal;
                speedSlider.Value = markVal;
                RefreshSpeedTickStyles();
            };
            tickBtns.Add((markVal, tb));
            ticksCanvas.Children.Add(tb);
        }

        void PositionSpeedTicks(double width)
        {
            if (width <= 32) return;
            foreach (var (mk, btn) in tickBtns)
            {
                double x = 8.0 + (width - 16.0) * ((mk - 0.8) / 1.2) - 14.0;
                Canvas.SetLeft(btn, Math.Clamp(x, -4.0, width - 24.0));
                Canvas.SetTop(btn, 0);
            }
        }
        ticksCanvas.SizeChanged += (_, sz) => PositionSpeedTicks(sz.NewSize.Width);
        PositionSpeedTicks(402);
        RefreshSpeedTickStyles();

        var sliderWrap = new StackPanel { Spacing = 2, Padding = new Thickness(9, 2, 9, 0), Children = { speedSlider, ticksCanvas } };
        speedStack.Children.Add(sliderWrap);
        ListenCardHost.Children.Add(speedStack);

        // 4. Sleep Timer Section (1:1 MacReaderCards.swift:1036-1084)
        var sleepStack = new StackPanel { Spacing = 6 };
        var sleepHead = new Grid { Padding = new Thickness(2, 0, 2, 0) };
        sleepHead.Children.Add(new TextBlock { Text = L10n.T("睡眠定时"), FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        string sleepRightHint = _audio.SleepMode == 1 && _audio.SleepSecondsRemaining > 0
            ? L10n.T($"还剩 {Math.Max(1, (int)Math.Ceiling(_audio.SleepSecondsRemaining / 60.0))} 分")
            : L10n.T("停止时自动添加书签");
        sleepHead.Children.Add(new TextBlock { Text = sleepRightHint, FontSize = 10.5, Foreground = ThemeHelper.Brush(_audio.SleepMode == 1 ? tint.Accent : p.T3), HorizontalAlignment = HorizontalAlignment.Right });
        sleepStack.Children.Add(sleepHead);

        var sleepGrid = new Grid { ColumnSpacing = 2 };
        string chapSleepLabel = _detail.Summary.Format == BookFormat.Podcast ? L10n.T("本集结束") : L10n.T("本章结束");
        var sleepItems = new (int Mode, string Label)[]
        {
            (0, L10n.T("关")),
            (-2, L10n.T("本句读完")),
            (-1, chapSleepLabel),
            (15, L10n.T("15 分钟")),
            (30, L10n.T("30 分钟")),
            (60, L10n.T("60 分钟")),
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
                Height = 34,
                MinHeight = 34,
                Padding = new Thickness(2, 0, 2, 0),
                FontSize = 11,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                UseSystemFocusVisuals = false,
            };
            int nextM = m;
            sb.Click += (_, _) =>
            {
                int targetM = (_sleepMode == nextM && nextM != 0) ? 0 : nextM;
                _sleepMode = targetM;
                _sleepArmedSpot = targetM != 0 ? (_currentSection, Math.Max(0, _currentSentence)) : null;
                if (targetM == 0) _audio.CancelSleepTimer();
                else if (targetM == -2) _audio.SetSleepAfterSentence();
                else if (targetM == -1) _audio.SetSleepEndOfChapter();
                else _audio.StartSleepTimer(targetM);
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
            var row = new Grid { ColumnSpacing = 7, VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(YYIcons.Make(icon, 13, ThemeHelper.Brush(active ? tint.Accent : p.T1)));
            var sp = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(new TextBlock { Text = sub, FontSize = 10, Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T2), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(sp, 1);
            row.Children.Add(sp);

            var b = new Button
            {
                Height = 44,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(9, 0, 7, 0),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(0),
                Background = ThemeHelper.Brush(active ? tint.Soft : p.Inset),
                UseSystemFocusVisuals = false,
                Content = row,
            };
            b.Click += (_, _) => onClick();
            Grid.SetColumn(b, col);
            return b;
        }

        int customRoleCount = 0;
        try { customRoleCount = _library.Core.BookRoles(_bookId).Count(r => !string.IsNullOrEmpty(r.Voice?.Voice)); } catch { }
        string castTileSub = _dramaModeOn ? L10n.T("广播剧开") : (customRoleCount > 0 ? L10n.T($"{customRoleCount} 人单独设") : L10n.T("都跟旁白"));

        tilesGrid.Children.Add(MakeFeatureTile(0, "people", L10n.T("人物声音"), castTileSub, _dramaModeOn || customRoleCount > 0, () =>
        {
            ListenCardFlyout.Hide();
            OpenPanel("cast");
        }));

        bool loopActive = _loopSingleSentence || _audio.HasLoopRange;
        string loopSub = _audio.HasLoopRange
            ? L10n.T($"第 {_audio.LoopRangeStart!.Value + 1}–{_audio.LoopRangeEnd!.Value + 1} 句")
            : L10n.T(_loopSingleSentence ? "开" : "关");
        tilesGrid.Children.Add(MakeFeatureTile(1, "repeat-1", L10n.T(_audio.HasLoopRange ? "区间循环" : "单句循环"), loopSub, loopActive, () =>
        {
            if (_audio.HasLoopRange)
            {
                _audio.ClearLoopRange();
            }
            else
            {
                _loopSingleSentence = !_loopSingleSentence;
                _audio.LoopSingleSentence = _loopSingleSentence;
            }
            RebuildListenCard();
        }));
        string pauseSub = _shadowPauseMode switch { 1 => L10n.T("2 秒"), 2 => L10n.T("按句长"), _ => L10n.T("关") };
        tilesGrid.Children.Add(MakeFeatureTile(2, "pause-circle", L10n.T("念完停一下"), pauseSub, _shadowPauseMode > 0, () =>
        {
            _shadowPauseMode = (_shadowPauseMode + 1) % 3;
            _audio.ShadowPauseMode = _shadowPauseMode;
            RebuildListenCard();
        }));
        tilesGrid.Children.Add(MakeFeatureTile(3, "eye-slash", L10n.T("只听"), L10n.T("隐藏原文"), _listenOnly, () =>
        {
            _listenOnly = !_listenOnly;
            ListenOnlyCover.Visibility = _listenOnly ? Visibility.Visible : Visibility.Collapsed;
            RebuildListenCard();
        }));
        ListenCardHost.Children.Add(tilesGrid);
    }

    private void OnAudioSentenceStarted(int sentenceIndex, int durationMs, bool playing)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (playing)
            {
                _hasUserMoved = true;
                _pausedOffSpot = false;
            }
            _currentSentence = sentenceIndex;
            SaveCurrentProgress();
            UpdateHeaderStatus();
            UpdateCapsuleText(sentenceIndex);
            _ = _host.SetActiveAsync(sentenceIndex, durationMs, 0, playing);
            if (DateTime.UtcNow >= _manualPageTurnIgnoreUntilUtc)
            {
                if (_settings.Mode == LayoutMode.Scrolled && (playing || sentenceIndex != _lastFollowedSentence))
                {
                    _lastFollowedSentence = sentenceIndex;
                    _ = _host.ScrollToSentenceAsync(sentenceIndex, animated: true, align: "third");
                }
                else if (_settings.Mode == LayoutMode.Paginated && _layout is not null)
                {
                    // 1:1 ReaderModel.swift:730-735 — auto-turn page in Paginated mode without triggering manual 8s lockout!
                    int targetPage = _layout.PageOf(sentenceIndex);
                    if (targetPage != _currentPage)
                    {
                        _lastFollowedSentence = sentenceIndex;
                        _ = ScrollToPageProgrammaticAsync(targetPage, preserveCurrentSentence: true);
                    }
                }
            }
        });
    }

    private void OnAudioSentenceProgressSynced(int sentenceIndex, int durationMs, int elapsedMs, bool playing)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _ = _host.SetActiveAsync(sentenceIndex, durationMs, elapsedMs, playing);
        });
    }

    private void OnAudioSentenceFinished()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_sleepMode == -2)
            {
                var armed = _sleepArmedSpot ?? (_currentSection, Math.Max(0, _currentSentence));
                _sleepMode = 0;
                _sleepArmedSpot = null;
                ReaderRecapAndSleepHelper.RememberSleepStop(_library, _bookId, armed.Section, armed.Sentence);
                try { _library.AddBookmark(_bookId, _currentSection, (uint)Math.Max(0, _currentSentence)); } catch { }
                UpdateCapsuleText(Math.Max(0, _currentSentence));
            }
        });
    }

    private void OnAudioPlayStateChanged(bool playing)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (playing)
            {
                _hasPausedAudioSession = false;
                _pausedOffSpot = false;
                _capsuleAutoHideTimer?.Stop();
                if (!_isFullScreen || _immersivePeeking) ListenCapsule.Visibility = Visibility.Visible;
            }
            else
            {
                if (_audio.CurrentSentence >= 0)
                    _hasPausedAudioSession = true;
                if (ListenCapsule.Visibility == Visibility.Visible)
                    ScheduleCapsuleAutoHide();
            }
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
                _hasPausedAudioSession = false;
                _pausedOffSpot = false;
                _lastFollowedSentence = -1;
                ReaderRecapAndSleepHelper.RememberSleepStop(_library, _bookId, armed.Section, armed.Sentence);
                try { _library.AddBookmark(_bookId, _currentSection, (uint)Math.Max(0, _currentSentence)); } catch { }
                UpdateCapsuleText(Math.Max(0, _currentSentence));
                _ = _host.SetActiveAsync(null);
                return;
            }

            if (_library.NextSection(_bookId, _currentSection, speech: true) is uint s)
            {
                _hasUserMoved = true;
                _pendingSentence = 0;
                _pendingAutoSpeak = true;
                LoadSection(s);
            }
            else
            {
                _audio.Stop();
                _hasPausedAudioSession = false;
                _pausedOffSpot = false;
                _lastFollowedSentence = -1;
                ListenCapsule.Visibility = Visibility.Collapsed;
                _ = _host.SetActiveAsync(null);
                _ = MaybeOfferFinishedSheetAsync();
            }
        });
    }

    private void TryAttachPairedAudiobook()
    {
        try
        {
            var pair = _library.Core.ListeningPairs().FirstOrDefault(lp => lp.Book == _bookId);
            if (pair is null) return;
            var rec = _library.Core.AudioRecording(pair.Recording);
            var map = _library.Core.AudiobookMap(pair.Recording);
            if (rec is not null && rec.Files.Length > 0)
            {
                _pairedAudioBookId = pair.Recording;
                _pairedRecording = rec;
                _pairedMap = map;
                _audio.SetPairedRecording(pair.Recording, rec, map, useRecording: map is not null);
            }
        }
        catch { }
    }

    private async Task ApplyPairedAlignmentOverlaysAsync()
    {
        if (_pairedMap is null) return;
        try
        {
            var secRows = _pairedMap.Rows.Where(r => r.Section == _currentSection).ToArray();
            var unread = secRows.Where(r => r.By == SyncBy.Unread).Select(r => (int)r.Sentence).ToArray();
            await _host.SetUnreadAsync(unread.Length > 0 ? unread : null, unread.Length > 0 ? L10n.Tr("原声略过", "Unread in audio") : null);

            var unsure = secRows.Where(r => r.By == SyncBy.Spread || r.Conf < 90).Select(r => (int)r.Sentence).OrderBy(x => x).ToArray();
            if (unsure.Length > 0)
                await _host.SetFlagAsync(unsure[0], unsure[^1]);
            else
                await _host.SetFlagAsync(null);
        }
        catch { }
    }

    private void PinPairedRecordingHere(uint sentence)
    {
        if (string.IsNullOrEmpty(_pairedAudioBookId)) return;
        try
        {
            double curSec = _audio.CurrentRecordingSeconds;
            _library.Core.AudiobookPin(_pairedAudioBookId, _currentSection, sentence, curSec);
            _pairedMap = _library.Core.AudiobookMap(_pairedAudioBookId);
            _audio.SetPairedRecording(_pairedAudioBookId, _pairedRecording, _pairedMap, useRecording: true);
            UpdateTopInfoBanners();
            _ = ApplyPairedAlignmentOverlaysAsync();
            ShowReaderToast($"已将原声 {TimeSpan.FromSeconds(curSec):mm\\:ss} 对准到第 {sentence + 1} 句");
        }
        catch (Exception ex)
        {
            ShowReaderToast($"对准失败：{ex.Message}");
        }
    }

    private void OnPairedSentenceActive(uint section, int sentence, int durMs, int elapsedMs, bool playing, int[]? wordTimings)
    {
        DispatcherQueue.TryEnqueue(() => SyncPairedReadingHighlight(section, sentence, durMs, elapsedMs, playing, wordTimings));
    }

    private void SyncPairedReadingHighlight(uint section, int sentenceIndex, int durMs, int elapsedMs, bool playing, int[]? wordTimings)
    {
        if (playing)
        {
            _hasUserMoved = true;
            _pausedOffSpot = false;
        }
        if (section != _currentSection)
        {
            if (DateTime.UtcNow >= _manualPageTurnIgnoreUntilUtc)
            {
                _pendingSentence = sentenceIndex;
                LoadSection(section);
            }
            return;
        }

        bool sentenceChanged = sentenceIndex != _lastFollowedSentence;
        _currentSentence = sentenceIndex;
        SaveCurrentProgress();
        UpdateHeaderStatus();
        UpdateCapsuleText(sentenceIndex);
        _ = _host.SetActiveAsync(sentenceIndex, durMs, elapsedMs, playing, wordTimings);

        if (DateTime.UtcNow >= _manualPageTurnIgnoreUntilUtc)
        {
            if (_settings.Mode == LayoutMode.Scrolled && sentenceChanged)
            {
                _lastFollowedSentence = sentenceIndex;
                _ = _host.ScrollToSentenceAsync(sentenceIndex, animated: true, align: "third");
            }
            else if (_settings.Mode == LayoutMode.Paginated && _layout is not null)
            {
                int targetPage = _layout.PageOf(sentenceIndex);
                if (targetPage != _currentPage)
                {
                    _lastFollowedSentence = sentenceIndex;
                    _ = ScrollToPageProgrammaticAsync(targetPage, preserveCurrentSentence: true);
                }
            }
        }
    }

    private void OnAudioMarkHeardRequested(int sentenceIndex)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var sents = _library.GetSentences(_bookId, _currentSection);
                if (sentenceIndex < 0 || sentenceIndex >= sents.Count) return;
                string txt = sents[sentenceIndex];
                if (string.IsNullOrWhiteSpace(txt)) return;
                var mk = _library.Core.MarkHeard(
                    _bookId,
                    _currentSection,
                    new TextPoint((uint)sentenceIndex, 0),
                    new TextPoint((uint)sentenceIndex, (uint)txt.Length),
                    txt,
                    RememberedHighlightStyle());
                await RefreshSectionMarksAsync();
                if (_activePanel == "notes") RenderSidePanel();
                string mkId = mk.Id;
                ShowReaderToast("已划下刚听到的这句", "撤销", async () =>
                {
                    try
                    {
                        _library.DeleteMark(_bookId, mkId);
                        await RefreshSectionMarksAsync();
                        if (_activePanel == "notes") RenderSidePanel();
                    }
                    catch { }
                });
            }
            catch { }
        });
    }

    public void ShowReaderToast(string message, string? actionLabel = null, Action? onAction = null, int autoHideSeconds = 5)
    {
        ReaderToastText.Text = L10n.T(message);
        _readerToastAction = onAction;
        if (!string.IsNullOrEmpty(actionLabel) && onAction is not null)
        {
            ReaderToastActionBtn.Content = L10n.T(actionLabel);
            ReaderToastActionBtn.Visibility = Visibility.Visible;
        }
        else
        {
            ReaderToastActionBtn.Visibility = Visibility.Collapsed;
        }
        ReaderToastBanner.Visibility = Visibility.Visible;
        YYChrome.AnimateEntrance(ReaderToastBanner, fromY: 10f, durationMs: 165);
        _readerToastTimer?.Stop();
        if (autoHideSeconds > 0)
        {
            _readerToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoHideSeconds) };
            _readerToastTimer.Tick += (_, _) =>
            {
                _readerToastTimer.Stop();
                _readerToastTimer = null;
                ReaderToastBanner.Visibility = Visibility.Collapsed;
            };
            _readerToastTimer.Start();
        }
    }

    private void ReaderToastAction_Click(object sender, RoutedEventArgs e)
    {
        var act = _readerToastAction;
        _readerToastAction = null;
        _readerToastTimer?.Stop();
        _readerToastTimer = null;
        ReaderToastBanner.Visibility = Visibility.Collapsed;
        try { act?.Invoke(); } catch { }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        lock (ActiveReaders)
        {
            ActiveReaders.Remove(this);
        }
        _library.Fonts.Changed -= _onFontsChanged;
        _library.Kokoro.StateChanged -= OnListenModelStateChanged;
        _library.LiveVoice.StateChanged -= OnListenModelStateChanged;
        AlignCheckAndPolishService.PolishStateChanged -= OnPolishStateChanged;
        RootGrid.ActualThemeChanged -= OnRootActualThemeChanged;
        _uiSettings.ColorValuesChanged -= OnSystemColorValuesChanged;
        try { _wordLookupAiCts?.Cancel(); _wordLookupAiCts?.Dispose(); } catch { }
        try { _companionCts?.Cancel(); _companionCts?.Dispose(); } catch { }
        _timeTracker.Stop();
        _searchDebounce.Stop();
        _capsuleAutoHideTimer?.Stop();
        _capsuleAlertTimer?.Stop();
        _immersivePeekTimer?.Stop();
        _readerToastTimer?.Stop();
        try
        {
            if (_partialReadSeconds > 0 || _partialListenSeconds > 0)
            {
                _library.AddTime(_bookId, _currentSection, _partialReadSeconds, _partialListenSeconds);
                _partialReadSeconds = 0;
                _partialListenSeconds = 0;
            }
            if (!_isFullScreen && AppWindow is not null)
            {
                var sz = AppWindow.Size;
                if (sz.Width >= 720 && sz.Height >= 520)
                {
                    _library.Prefs.ReaderWindowWidth = sz.Width;
                    _library.Prefs.ReaderWindowHeight = sz.Height;
                    _library.SavePrefs();
                }
            }
        }
        catch { }
        SaveCurrentProgress();
        try { if (_pager.IsInitialized) _ = _host.CapturePreviewPngAsync(LaunchSnapshotPath); } catch { }
        _audio.Dispose();
        _pager.Dispose();
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
        // Match exact @font-face family names emitted by core/yuyue-core/src/fonts.rs:111-161
        if (!string.IsNullOrWhiteSpace(fontFace))
        {
            string fallback = font switch
            {
                Font.Sans => "sans",
                Font.Serif => "serif",
                Font.Kai => "kaiti",
                Font.WenKai => "wenkai",
                _ => "p-font",
            };
            if (fontFace is "notoserif" or "notosans" || YuyueCoreMethods.FontCatalog().Any(c => string.Equals(c.Id, fontFace, StringComparison.Ordinal)))
                return $"\"yy-{fontFace}\", var(--yy-{fallback})";
            if (fontFace.StartsWith("file:", StringComparison.Ordinal))
                return $"\"yy-{fontFace}\", var(--yy-{fallback})";
            if (fontFace.StartsWith("local:", StringComparison.Ordinal))
                return $"\"{fontFace[6..].Trim()}\", var(--yy-{fallback})";
            return $"\"{fontFace.Trim()}\", var(--yy-{fallback})";
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
