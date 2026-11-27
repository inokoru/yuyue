using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Shelf;
using Yuyue.Speech;
using TimeSpan = System.TimeSpan;
using XamlLine = Microsoft.UI.Xaml.Shapes.Line;

namespace Yuyue.Podcast;

/// <summary>
/// 1:1 implementation of macOS <c>MacPodcastView.swift</c>, <c>PodcastReaderView.swift</c>,
/// <c>PodcastSharedUI.swift</c> (<c>PodcastDrawerContentView</c>), <c>PodcastStudioSheet.swift</c>,
/// and <c>PodcastAddSheet.swift</c>:
/// - Asymmetric 2-column workspace (independent left station ScrollViewer + pinned right literary desk)
/// - Overlay 386px stationery right drawer with contextually paired tabs (<c>原声书摘 | 稍后听</c> vs <c>声音卡 | 本机整理</c>)
/// - Zero-button lightweight <see cref="TextBlock"/> / <see cref="Hyperlink"/> transcript rendering + in-place turn &amp; scrubber updates
/// - Single-row 44px slim player bar with dynamic-width segmented chapter/ad/clip scrubber + non-modal floating <c>插话</c> card
/// - Frameless warm-paper <c>PodcastStudioSheet</c> and <c>PodcastAddSheet</c> via <see cref="YYChrome.CreateFluentDialog"/> + <see cref="YYChrome.ShowSafeAsync"/>.
/// </summary>
public sealed class PodcastWorkspaceBuilder : IDisposable
{
    private sealed record ExternalShowEntry(string Key, string Title, string? ChannelId, string? ArtworkUrl);

    private sealed record PlayerSegment(
        int Id,
        int? TopicIndex,
        string Title,
        double StartSec,
        double EndSec,
        bool IsAd,
        bool HasBookMention,
        double[] ClipTimes)
    {
        public double Duration => Math.Max(1.0, EndSec - StartSec);
    }

    private sealed record SegLayout(PlayerSegment Seg, double X, double Width);

    private sealed class TurnVisualMeta
    {
        public required int TurnIndex { get; init; }
        public required string SentenceId { get; init; }
        public required Border Card { get; init; }
        public required TextBlock TimeTb { get; init; }
        public required StackPanel BodyPanel { get; init; }
        public TextBlock? ClippedBadge { get; init; }
        public Button? PeekButton { get; init; }
        public bool IsClipped { get; set; }
    }

    private readonly UI.Shell.ILibraryHost _shelf;
    private readonly LibraryService _library;
    private readonly AudioPlayer _ttsPlayer;
    private readonly MediaPlayer _mediaPlayer;
    private readonly MediaPlayer _clipPreviewPlayer;
    private readonly DispatcherTimer _mediaPosTimer;
    private readonly DispatcherTimer _eqPulseTimer;

    public string? OpenIssueId { get; set; }
    public bool IsPlaying => _playing;

    private string _selectedShowKey = "all"; // all | studio | show:<titleOrChannel>
    private bool _allCollapsed;
    private readonly HashSet<string> _collapsedGroups = [];
    private readonly HashSet<string> _expandedSkipIds = [];
    private readonly HashSet<int> _peekedTurnIndices = [];

    // Live discovery cache
    private IReadOnlyList<PodcastSearchHitDto>? _discoveredShows;
    private bool _loadingDiscovery;
    private readonly HashSet<string> _subscribingFeedUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadingMoreChannelIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _exhaustedChannelIds = new(StringComparer.OrdinalIgnoreCase);

    // Right-edge 386px Stationery Overlay Drawer (clips | later | voices | local)
    private bool _drawerOpen;
    private string _drawerTab = "clips";
    private bool _hasNewClipDot;
    private string? _focusClipIdOnOpen;
    private string? _playingClipId;
    private (int AudioFiles, long AudioBytes, int ClipFiles, long ClipBytes)? _cachedStorageStats;
    private bool _loadingStorageStats;

    // Inline PodcastReaderView & Playback state (separated playing vs open reader!)
    private bool _disposed;
    private PodcastTrack _track = PodcastTrack.Original;
    private bool _blindListen;
    private int _playingTurnIdx;
    private MagazineSentence[] _playingSentences = [];
    private int _readerTurnIdx;
    private MagazineSentence[] _readerSentences = [];
    private bool _playing;
    private bool _usingExternalMedia;
    private double _rate = 1.0;
    private string? _playingIssueId;
    private string? _transcribingIssueId;
    private string _transcribeStatus = "";
    private TextBox? _stationCmdBox;
    private Border? _commandDropdownHost;
    private StackPanel? _commandDropdownStack;
    private bool _isPreviewingInterruptAnswer;
    private bool _eqPulseToggle;
    private readonly List<Border[]> _liveEqBarSets = [];

    // Live in-place references for open InlineEpisodeReader (zero RefreshCurrentSubPage on ticks!)
    private readonly List<Border> _turnCardElements = [];
    private readonly Dictionary<int, TurnVisualMeta> _turnVisualByIndex = [];
    private bool _readerDarkTheme;
    private PodcastIssue? _renderedReaderIssue;
    private ContentPresenter? _slimPlayIconHost;
    private Grid? _slimCenterGrid;
    private TextBlock? _slimClockTb;
    private TextBlock? _slimChapterBtnLabel;
    private Action<double>? _redrawSlimScrubber;
    private TextBlock? _slimRateLabelTb;
    private StackPanel? _slimClipBtnContent;
    private StackPanel? _topClipBtnContent;
    private Button? _slimInterruptBtn;
    private bool _clipFlashActive;

    // Live in-place references for Station Desk Tabletop Player
    private string? _renderedDeskIssueId;
    private ContentPresenter? _deskPlayIconHost;
    private TextBlock? _deskStatusMetaTb;
    private TextBlock? _deskSpeakerTb;
    private TextBlock? _deskExcerptTb;
    private TextBlock? _deskClockPosTb;
    private TextBlock? _deskClockDurTb;
    private Action<double>? _redrawDeskScrubber;

    // Non-modal floating bottom 插话 (Cut-In Q&A) state
    private bool _interruptOpen;
    private bool _interruptSending;
    private bool _interruptListening;
    private bool _interruptWasPlayingExternal;
    private bool _interruptWasPlayingTts;
    private string _interruptQuestion = "";
    private string? _interruptVoiceHint;
    private PodcastInterrupt? _latestInterruptReply;
    private CancellationTokenSource? _interruptMicCts;
    private Border? _interruptCardHost;
    private TextBox? _interruptInputBox;

    public PodcastWorkspaceBuilder(UI.Shell.ILibraryHost shelf, LibraryService library)
    {
        _shelf = shelf;
        _library = library;
        _ttsPlayer = new AudioPlayer(library);
        try { App.CurrentApp.BindAudioClaimOwner(_ttsPlayer, _shelf); } catch { }
        _ttsPlayer.SentenceFinished += OnStudioTurnFinished;

        _mediaPlayer = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech, AutoPlay = false };
        _mediaPlayer.CommandManager.IsEnabled = false;
        var smtc = _mediaPlayer.SystemMediaTransportControls;
        smtc.IsEnabled = true;
        smtc.IsPlayEnabled = true;
        smtc.IsPauseEnabled = true;
        smtc.IsNextEnabled = true;
        smtc.IsPreviousEnabled = true;
        smtc.ButtonPressed += OnSmtcButtonPressed;

        _mediaPlayer.MediaEnded += (_, _) => _shelf.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            string? endedId = _playingIssueId;
            _playing = false;
            _usingExternalMedia = false;
            _mediaPosTimer?.Stop();
            SyncEqTimer();
            _library.SystemIntegration.SetAudioPlaying(_shelf, false);
            UpdateSmtcState(null, playing: false);
            if (endedId is not null && OpenIssueId == endedId)
            {
                RefreshPlayButtonsInPlace();
            }
            else
            {
                _shelf.RefreshCurrentSubPage();
            }
        });

        _clipPreviewPlayer = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech, AutoPlay = false };
        _clipPreviewPlayer.MediaEnded += (_, _) => _shelf.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            _playingClipId = null;
            if (_drawerOpen && _drawerTab == "clips")
                _shelf.RefreshCurrentSubPage();
        });

        _mediaPosTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _mediaPosTimer.Tick += OnMediaPosTick;

        _eqPulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(480) };
        _eqPulseTimer.Tick += (_, _) =>
        {
            if (_disposed || !_playing || _liveEqBarSets.Count == 0)
            {
                _eqPulseTimer.Stop();
                return;
            }
            _eqPulseToggle = !_eqPulseToggle;
            foreach (var bars in _liveEqBarSets)
            {
                if (bars.Length < 3) continue;
                bars[0].Height = _eqPulseToggle ? 9 : 4;
                bars[1].Height = _eqPulseToggle ? 5 : 10;
                bars[2].Height = _eqPulseToggle ? 8 : 3.5;
            }
        };
    }

    private void SyncEqTimer()
    {
        if (!_disposed && _playing && _liveEqBarSets.Count > 0)
        {
            if (!_eqPulseTimer.IsEnabled) _eqPulseTimer.Start();
        }
        else
        {
            _eqPulseTimer.Stop();
        }
    }

    public void OpenIssueAt(string issueId, double? seekSec = null, bool autoPlay = false)
    {
        if (_interruptOpen) CloseInterrupt(resumeAudio: false);
        OpenIssueId = issueId;
        var issue = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == issueId);
        if (issue is not null)
        {
            var sents = _playingIssueId == issueId && _playingSentences.Length > 0
                ? _playingSentences
                : LoadOrSynthesizeTranscript(issue);
            _readerSentences = sents;

            if (seekSec is double sec && sec >= 0)
            {
                int idx = sents.Length > 0 ? Array.FindLastIndex(sents, s => sec >= s.T0) : 0;
                _readerTurnIdx = Math.Max(0, idx);
                if (_playingIssueId == issueId)
                {
                    _playingTurnIdx = _readerTurnIdx;
                    if (_usingExternalMedia)
                    {
                        try { _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(sec); } catch { }
                    }
                }
                try { _library.Core.PodcastSavePlayback(issueId, sec, null); } catch { }
            }
            else if (_playingIssueId == issueId)
            {
                _readerTurnIdx = sents.Length > 0 ? Math.Clamp(_playingTurnIdx, 0, sents.Length - 1) : 0;
            }
            else if (sents.Length > 0 && issue.PositionSec > 0)
            {
                int idx = Array.FindLastIndex(sents, s => issue.PositionSec >= s.T0);
                _readerTurnIdx = Math.Max(0, idx);
            }
            else
            {
                _readerTurnIdx = 0;
            }

            _track = SupportsTrackSwitch(issue, sents) ? issue.Track : PodcastTrack.Original;
            if (autoPlay)
            {
                PlayIssueAtTurn(issue, sents, sents.Length > 0 ? Math.Clamp(_readerTurnIdx, 0, sents.Length - 1) : 0, exactSeekSec: seekSec);
            }
        }
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        _shelf.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || _playingIssueId is null) return;
            var issue = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == _playingIssueId);
            if (issue is null) return;
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    if (!_playing)
                    {
                        PlayIssueAtTurn(issue, _playingSentences, _playingTurnIdx);
                        if (OpenIssueId == issue.Id) RefreshPlayButtonsInPlace();
                        else _shelf.RefreshCurrentSubPage();
                    }
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    if (_playing)
                    {
                        StopPlayback();
                        if (OpenIssueId == issue.Id) RefreshPlayButtonsInPlace();
                        else _shelf.RefreshCurrentSubPage();
                    }
                    break;
                case SystemMediaTransportControlsButton.Next:
                    StepPlayback(issue, _playingSentences, forward: true);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    StepPlayback(issue, _playingSentences, forward: false);
                    break;
            }
        });
    }

    private void UpdateSmtcState(PodcastIssue? issue, bool playing)
    {
        try
        {
            var smtc = _mediaPlayer.SystemMediaTransportControls;
            smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            if (issue is not null)
            {
                var updater = smtc.DisplayUpdater;
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = issue.Title;
                updater.MusicProperties.Artist = issue.ShowTitle;
                updater.Update();
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        _mediaPosTimer.Stop();
        _eqPulseTimer.Stop();
        AudioPlayer.StopActivePreview();
        try { _interruptMicCts?.Cancel(); _interruptMicCts?.Dispose(); } catch { }
        _ttsPlayer.SentenceFinished -= OnStudioTurnFinished;
        _ttsPlayer.Dispose();
        _library.SystemIntegration.SetAudioPlaying(_shelf, false);
        try { _mediaPlayer.SystemMediaTransportControls.ButtonPressed -= OnSmtcButtonPressed; } catch { }
        try { _mediaPlayer.Pause(); _mediaPlayer.Source = null; _mediaPlayer.Dispose(); } catch { }
        try { _clipPreviewPlayer.Pause(); _clipPreviewPlayer.Source = null; _clipPreviewPlayer.Dispose(); } catch { }
    }

    private void StopDrawerAudioPreviews()
    {
        _playingClipId = null;
        try
        {
            _clipPreviewPlayer.Pause();
            _clipPreviewPlayer.Source = null;
        }
        catch { }
        AudioPlayer.StopActivePreview();
    }

    private void CloseDrawer()
    {
        _drawerOpen = false;
        StopDrawerAudioPreviews();
    }

    public bool SupportsTrackSwitch(PodcastIssue? issue, MagazineSentence[]? preloaded = null)
    {
        if (issue is null) return false;
        if (issue.StudioKind == StudioKind.VocabRadio || issue.Fidelity == PodcastFidelity.L3CrossLingual)
            return true;
        var sents = preloaded is { Length: > 0 } ? preloaded : LoadOrSynthesizeTranscript(issue);
        return sents.Any(s => !string.IsNullOrWhiteSpace(s.TextTrans) || !string.IsNullOrWhiteSpace(s.CoachNote));
    }

    public bool HandleKeyDown(VirtualKey key)
    {
        if (OpenIssueId is string issueId)
        {
            var issue = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == issueId);
            if (issue is null)
            {
                if (key == VirtualKey.Escape)
                {
                    OpenIssueId = null;
                    _shelf.RefreshCurrentSubPage();
                    return true;
                }
                return false;
            }

            if (_interruptOpen && _interruptInputBox is not null && _interruptInputBox.FocusState != FocusState.Unfocused)
            {
                if (key == VirtualKey.Escape)
                {
                    CloseInterrupt(resumeAudio: true);
                    return true;
                }
                return false;
            }

            var sents = _readerSentences.Length > 0 && _renderedReaderIssue?.Id == issueId
                ? _readerSentences
                : (_playingIssueId == issueId && _playingSentences.Length > 0 ? _playingSentences : LoadOrSynthesizeTranscript(issue));

            switch (key)
            {
                case VirtualKey.Space:
                    if (_playing && _playingIssueId == issue.Id) StopPlayback();
                    else PlayIssueAtTurn(issue, sents, _readerTurnIdx);
                    RefreshPlayButtonsInPlace();
                    return true;
                case VirtualKey.H:
                    _ = ClipCurrentTurnAsync(issue.Id);
                    return true;
                case VirtualKey.I:
                    if (_interruptOpen) CloseInterrupt(resumeAudio: true);
                    else BeginInterrupt(issue);
                    return true;
                case VirtualKey.Number1:
                    if (SupportsTrackSwitch(issue, sents)) SwitchTrack(issue.Id, issue, PodcastTrack.Original);
                    return true;
                case VirtualKey.Number2:
                    if (SupportsTrackSwitch(issue, sents)) SwitchTrack(issue.Id, issue, PodcastTrack.Dub);
                    return true;
                case VirtualKey.Number3:
                    if (SupportsTrackSwitch(issue, sents)) SwitchTrack(issue.Id, issue, PodcastTrack.Coach);
                    return true;
                case VirtualKey.Escape:
                    if (_interruptOpen)
                    {
                        CloseInterrupt(resumeAudio: true);
                        return true;
                    }
                    if (_drawerOpen)
                    {
                        CloseDrawer();
                        _shelf.RefreshCurrentSubPage();
                        return true;
                    }
                    OpenIssueId = null;
                    _shelf.RefreshCurrentSubPage();
                    return true;
                default:
                    return false;
            }
        }

        // Station desk shortcuts
        if (_stationCmdBox is not null && _stationCmdBox.FocusState != FocusState.Unfocused)
        {
            if (key == VirtualKey.Escape)
            {
                HideCommandDropdown();
                return true;
            }
            return false;
        }

        if (key == (VirtualKey)191 && _stationCmdBox is not null) // '/'
        {
            _stationCmdBox.Focus(FocusState.Programmatic);
            return true;
        }
        if (key == VirtualKey.Escape)
        {
            if (_commandDropdownHost is { Visibility: Visibility.Visible })
            {
                HideCommandDropdown();
                return true;
            }
            if (_drawerOpen)
            {
                CloseDrawer();
                _shelf.RefreshCurrentSubPage();
                return true;
            }
        }
        if (key == VirtualKey.Space)
        {
            var target = ResolveDeskActiveIssue(_library.Podcasts.GetIssues());
            if (target is not null)
            {
                ToggleDeskIssuePlayback(target);
                return true;
            }
        }
        if (key == VirtualKey.H)
        {
            var target = ResolveDeskActiveIssue(_library.Podcasts.GetIssues());
            if (target is not null)
            {
                _ = ClipCurrentTurnAsync(target.Id);
                return true;
            }
        }
        if (key == VirtualKey.I && _playingIssueId is string pid2)
        {
            var cur = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == pid2);
            if (cur is not null)
            {
                OpenIssueId = cur.Id;
                _shelf.RefreshCurrentSubPage();
                BeginInterrupt(cur);
                return true;
            }
        }
        return false;
    }

    // ---------- Distinct External Shows (Subscribed Channels + Standalone Imported Shows) ----------

    private List<ExternalShowEntry> GetExternalShows(IReadOnlyList<PodcastChannel> channels, IReadOnlyList<PodcastIssue> allIssues)
    {
        var list = new List<ExternalShowEntry>();
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ch in channels)
        {
            string title = string.IsNullOrWhiteSpace(ch.Title) ? L10n.T("播客频道") : ch.Title.Trim();
            if (seenTitles.Add(title))
            {
                list.Add(new ExternalShowEntry("show:" + title, title, ch.Id, ch.ArtworkUrl));
            }
        }

        string standaloneFallback = L10n.T("单集导入");
        foreach (var iss in allIssues.Where(i => i.StudioKind is null))
        {
            string title = string.IsNullOrWhiteSpace(iss.ShowTitle) ? standaloneFallback : iss.ShowTitle.Trim();
            if (seenTitles.Add(title))
            {
                list.Add(new ExternalShowEntry("show:" + title, title, iss.ChannelId, iss.ArtworkUrl));
            }
        }

        return list;
    }

    private static bool MatchesExternalShow(PodcastIssue issue, ExternalShowEntry show)
    {
        if (issue.StudioKind is not null) return false;
        if (!string.IsNullOrEmpty(show.ChannelId) && issue.ChannelId == show.ChannelId) return true;
        string title = string.IsNullOrWhiteSpace(issue.ShowTitle) ? L10n.T("单集导入") : issue.ShowTitle.Trim();
        return string.Equals(title, show.Title, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Top Header ----------

    public void PopulateTopHeader(Panel leftAfterToggleHost, Panel rightHost, bool dark)
    {
        _topClipBtnContent = null;
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        if (OpenIssueId is string issueId)
        {
            var curIssue = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == issueId);

            // Left: < 返回收听台
            var backSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            backSp.Children.Add(YYIcons.Make("chevron-left", 11.5, ThemeHelper.Brush(p.T1)));
            backSp.Children.Add(new TextBlock { Text = L10n.T("返回收听台"), FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
            var backBtn = new Button
            {
                Height = 32,
                MinHeight = 32,
                Padding = new Thickness(10, 0, 12, 0),
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                UseSystemFocusVisuals = false,
                Content = backSp,
            };
            backBtn.Click += (_, _) =>
            {
                if (_interruptOpen) CloseInterrupt(resumeAudio: false);
                OpenIssueId = null;
                _shelf.RefreshCurrentSubPage();
            };
            leftAfterToggleHost.Children.Add(backBtn);

            // Right: Track segmented control (only when episode supports translation / coach notes) + 盲听 + 稍后听 + 原声划线
            var cachedSents = _readerSentences.Length > 0 && _renderedReaderIssue?.Id == issueId
                ? _readerSentences
                : (_playingIssueId == issueId && _playingSentences.Length > 0 ? _playingSentences : null);
            bool canSwitchTrack = SupportsTrackSwitch(curIssue, cachedSents);
            if (!canSwitchTrack && _track != PodcastTrack.Original)
            {
                _track = PodcastTrack.Original;
            }
            if (canSwitchTrack)
            {
                bool isEnglishEp = curIssue is null
                    || !(curIssue.Language ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                    || curIssue.StudioKind == StudioKind.VocabRadio
                    || curIssue.Fidelity == PodcastFidelity.L3CrossLingual;
                rightHost.Children.Add(BuildTrackSegmentedControl(_track, compact: false, p, tint, nextTr => SwitchTrack(issueId, curIssue, nextTr), isEnglishEp));
            }

            var blindBtn = MakePaperPill(L10n.T("盲听"), accent: _blindListen, p, tint);
            blindBtn.Height = 28;
            blindBtn.Click += (_, _) =>
            {
                _blindListen = !_blindListen;
                _peekedTurnIndices.Clear();
                _shelf.RefreshCurrentSubPage();
            };
            rightHost.Children.Add(blindBtn);

            if (curIssue is not null)
            {
                bool inLater = curIssue.Placement == PodcastPlacement.Archive;
                var laterBtn = MakePaperPill(inLater ? L10n.T("✓ 稍后听") : L10n.T("稍后听"), accent: inLater, p, tint);
                laterBtn.Height = 28;
                laterBtn.Click += (_, _) =>
                {
                    var nextPl = inLater ? PodcastPlacement.Desk : PodcastPlacement.Archive;
                    try { _library.Core.PodcastSetPlacement(issueId, nextPl); } catch { }
                    _shelf.ShowToast(inLater ? L10n.T("已移回在听书桌") : L10n.T("已存入稍后听"), canUndo: false);
                    _shelf.RefreshCurrentSubPage();
                };
                rightHost.Children.Add(laterBtn);
            }

            var clipSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            PopulateTopClipButtonContent(clipSp, _clipFlashActive, p, tint);
            _topClipBtnContent = clipSp;
            var clipBtn = new Button
            {
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(11, 0, 11, 0),
                CornerRadius = new CornerRadius(14),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                UseSystemFocusVisuals = false,
                Content = clipSp,
            };
            clipBtn.Click += async (_, _) => await ClipCurrentTurnAsync(issueId);
            rightHost.Children.Add(clipBtn);
            return;
        }

        // Workspace header (MacPodcastView.swift:383-477): Recessed matte bookplate tray with show artwork/monogram + expanded title on active show!
        var channels = _library.Podcasts.GetChannels();
        var allIssues = _library.Podcasts.GetIssues();
        var externalShows = GetExternalShows(channels, allIssues);
        if (_selectedShowKey != "all" && _selectedShowKey != "studio" && externalShows.All(s => s.Key != _selectedShowKey))
        {
            _selectedShowKey = "all";
        }

        var trayRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };

        // 1. All Shows Grid Button
        bool allOn = _selectedShowKey == "all";
        var allBtn = new Button
        {
            Width = 28,
            Height = 26,
            MinWidth = 28,
            MinHeight = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = allOn ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
            BorderBrush = allOn ? ThemeHelper.Brush(dark ? Color.FromArgb(24, 255, 255, 255) : p.Line) : null,
            BorderThickness = new Thickness(allOn ? 1 : 0),
            UseSystemFocusVisuals = false,
            Content = YYIcons.Make("grid-all", 13.5, ThemeHelper.Brush(allOn ? tint.Accent : p.T3)),
        };
        ToolTipService.SetToolTip(allBtn, L10n.T("全部节目"));
        allBtn.Click += (_, _) => { _selectedShowKey = "all"; _shelf.RefreshCurrentSubPage(); };
        trayRow.Children.Add(allBtn);

        void AddShowTrayButton(string key, string title, string? artworkUrl, bool isStudio)
        {
            bool on = _selectedShowKey == key;
            var contentSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            contentSp.Children.Add(BuildShowMark(title, artworkUrl, isStudio, 19, p, tint));
            if (on)
            {
                contentSp.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    MaxLines = 1,
                    MaxWidth = 128,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 3, 0),
                });
            }

            var btn = new Button
            {
                MinWidth = 30,
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(5.5, 0, 5.5, 0),
                CornerRadius = new CornerRadius(8),
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                BorderBrush = on ? ThemeHelper.Brush(dark ? Color.FromArgb(24, 255, 255, 255) : p.Line) : null,
                BorderThickness = new Thickness(on ? 0.5 : 0),
                UseSystemFocusVisuals = false,
                Content = contentSp,
            };
            ToolTipService.SetToolTip(btn, title);
            string k = key;
            btn.Click += (_, _) =>
            {
                _selectedShowKey = _selectedShowKey == k ? "all" : k;
                _shelf.RefreshCurrentSubPage();
            };
            trayRow.Children.Add(btn);
        }

        AddShowTrayButton("studio", L10n.T("书房电台"), null, isStudio: true);
        foreach (var show in externalShows.Take(7))
        {
            AddShowTrayButton(show.Key, show.Title, show.ArtworkUrl, isStudio: false);
        }

        var trayScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxWidth = 520,
            Content = trayRow,
        };
        leftAfterToggleHost.Children.Add(new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = trayScroll,
        });

        if (channels.Count > 0)
        {
            var refBtn = MakeCircleIconButton("refresh", L10n.T("刷新全部已订阅频道"), p);
            refBtn.Click += async (_, _) =>
            {
                refBtn.IsEnabled = false;
                int ok = 0;
                foreach (var c in channels)
                {
                    try { await _library.Podcasts.AddUrlOrFeedAsync(c.FeedUrl, putLatestOnDesk: false, isRefresh: true); ok++; } catch { }
                }
                _exhaustedChannelIds.Clear();
                refBtn.IsEnabled = true;
                _shelf.RefreshBooks();
                _shelf.ShowToast($"已刷新 {ok} 档订阅", canUndo: false);
            };
            rightHost.Children.Add(refBtn);
        }

        var addShowBtn = MakeCircleIconButton("plus", L10n.T("添加节目 · 粘贴链接 / OPML / 本地音频"), p);
        addShowBtn.Click += async (_, _) => await ShowAddPodcastSheetAsync();
        rightHost.Children.Add(addShowBtn);
    }

    private void ToggleDrawer(string tab)
    {
        if (_drawerOpen && _drawerTab == tab)
        {
            CloseDrawer();
        }
        else
        {
            StopDrawerAudioPreviews();
            _drawerTab = tab;
            _drawerOpen = true;
            if (tab is "clips" or "later") _hasNewClipDot = false;
        }
        _shelf.RefreshCurrentSubPage();
    }

    private void OpenDrawer(string tab, string? focusClipId = null)
    {
        StopDrawerAudioPreviews();
        _drawerTab = tab;
        _drawerOpen = true;
        _focusClipIdOnOpen = focusClipId;
        if (tab is "clips" or "later") _hasNewClipDot = false;
        _shelf.RefreshCurrentSubPage();
    }

    public void RenderInto(StackPanel host, bool dark)
    {
        host.Children.Clear();
        _liveEqBarSets.Clear();
        SyncEqTimer();
        ClearLiveReaderRefs();
        ClearLiveDeskRefs();

        if (OpenIssueId is string iid)
        {
            var issue = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == iid);
            if (issue is not null)
            {
                var readerBody = BuildInlineEpisodeReader(issue, dark);
                var wrappedReader = WrapWithOptionalDrawer(readerBody, dark);
                BindViewportHeight(wrappedReader, host);
                host.Children.Add(wrappedReader);
                return;
            }
            OpenIssueId = null;
        }

        if (_interruptOpen) CloseInterrupt(resumeAudio: false);
        var deskBody = BuildStationWorkspace(dark);
        var wrappedDesk = WrapWithOptionalDrawer(deskBody, dark);
        BindViewportHeight(wrappedDesk, host);
        host.Children.Add(wrappedDesk);
    }

    private void BindViewportHeight(FrameworkElement targetRoot, StackPanel host)
    {
        void SyncHeight()
        {
            var sv = host.Parent as ScrollViewer;
            double rawH = sv is not null && sv.ActualHeight > 120
                ? sv.ActualHeight - sv.Padding.Top - sv.Padding.Bottom - 2
                : ((_shelf.Content as FrameworkElement)?.ActualHeight ?? 760) - 92 - (sv?.Padding.Top ?? 4) - (sv?.Padding.Bottom ?? 40);
            double h = Math.Max(420, rawH);
            if (double.IsNaN(targetRoot.Height) || Math.Abs(targetRoot.Height - h) > 1.0)
                targetRoot.Height = h;
        }

        SyncHeight();
        SizeChangedEventHandler? onSvSize = null;
        if (host.Parent is ScrollViewer parentSv)
        {
            onSvSize = (_, _) => SyncHeight();
            parentSv.SizeChanged += onSvSize;
        }
        targetRoot.Loaded += (_, _) => SyncHeight();
        targetRoot.Unloaded += (_, _) =>
        {
            if (onSvSize is not null && host.Parent is ScrollViewer pSv)
                pSv.SizeChanged -= onSvSize;
        };
    }

    private void ClearLiveReaderRefs()
    {
        _turnCardElements.Clear();
        _turnVisualByIndex.Clear();
        _renderedReaderIssue = null;
        _slimPlayIconHost = null;
        _slimCenterGrid = null;
        _slimClockTb = null;
        _slimChapterBtnLabel = null;
        _redrawSlimScrubber = null;
        _slimRateLabelTb = null;
        _slimClipBtnContent = null;
        _slimInterruptBtn = null;
        _interruptCardHost = null;
        _interruptInputBox = null;
    }

    private void ClearLiveDeskRefs()
    {
        _renderedDeskIssueId = null;
        _deskPlayIconHost = null;
        _deskStatusMetaTb = null;
        _deskSpeakerTb = null;
        _deskExcerptTb = null;
        _deskClockPosTb = null;
        _deskClockDurTb = null;
        _redrawDeskScrubber = null;
        _stationCmdBox = null;
        _commandDropdownHost = null;
        _commandDropdownStack = null;
    }

    // ---------- Overlay 386px Right Drawer (Never Squishes Main Layout!) ----------

    private FrameworkElement WrapWithOptionalDrawer(FrameworkElement mainBody, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var overlayRoot = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        overlayRoot.Children.Add(mainBody);

        if (!_drawerOpen)
        {
            // Detached right-edge 36px circular drawer triggers (MacPodcastView.swift:270-276)
            var floatRail = new StackPanel
            {
                Spacing = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -16, 0, 0),
            };
            var clipsBtn = MakeCircleIconButton("bookmark", L10n.T("原声书摘 · 稍后听"), p, _hasNewClipDot, tint);
            clipsBtn.Width = 36;
            clipsBtn.Height = 36;
            clipsBtn.CornerRadius = new CornerRadius(18);
            clipsBtn.Click += (_, _) => OpenDrawer("clips");
            floatRail.Children.Add(clipsBtn);

            var voicesBtn = MakeCircleIconButton("wave", L10n.T("声音卡 · 本机整理"), p);
            voicesBtn.Width = 36;
            voicesBtn.Height = 36;
            voicesBtn.CornerRadius = new CornerRadius(18);
            voicesBtn.Click += (_, _) => OpenDrawer("voices");
            floatRail.Children.Add(voicesBtn);

            overlayRoot.Children.Add(floatRail);
            return overlayRoot;
        }

        // Subtle clickable scrim + 386px floating stationery drawer above content
        var scrim = new Border
        {
            Background = new SolidColorBrush(dark ? Color.FromArgb(72, 0, 0, 0) : Color.FromArgb(22, 0, 0, 0)),
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        scrim.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            CloseDrawer();
            _shelf.RefreshCurrentSubPage();
        };
        overlayRoot.Children.Add(scrim);

        var drawer = BuildRightDrawer(dark);
        drawer.Width = 386;
        drawer.HorizontalAlignment = HorizontalAlignment.Right;
        drawer.VerticalAlignment = VerticalAlignment.Stretch;
        drawer.Margin = new Thickness(0, 6, 4, 6);
        ThemeHelper.ApplyElevation(drawer, overlayRoot, 22f);
        overlayRoot.Children.Add(drawer);

        return overlayRoot;
    }

    // ---------- 1. 双栏收听台工作区（MacPodcastView.swift 1:1） ----------

    private PodcastIssue? ResolveDeskActiveIssue(IReadOnlyList<PodcastIssue> allIssues)
    {
        return (_playingIssueId is not null ? allIssues.FirstOrDefault(x => x.Id == _playingIssueId) : null)
            ?? allIssues.FirstOrDefault(x => x.PositionSec > 0)
            ?? allIssues.FirstOrDefault();
    }

    private FrameworkElement BuildStationWorkspace(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        Color leafSurface = dark ? Color.FromArgb(255, 31, 30, 28) : Color.FromArgb(255, 248, 245, 239);

        var allIssues = _library.Podcasts.GetIssues();
        var channels = _library.Podcasts.GetChannels();
        var externalShows = GetExternalShows(channels, allIssues);
        var clips = _library.Core.PodcastClips(null);

        if (_discoveredShows is null && !_loadingDiscovery && externalShows.Count == 0)
        {
            EnsureDiscoveryLoadedAsync();
        }

        var workspace = new Grid
        {
            ColumnSpacing = 32,
            Margin = new Thickness(4, 4, 48, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent),
            IsTabStop = true,
        };
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(376) });
        workspace.PointerPressed += (_, _) => HideCommandDropdown();

        // ===== 左栏：收听台（独立 ScrollViewer，滚动绝不带动右侧书桌） =====
        var leftCol = new StackPanel { Spacing = 12, Padding = new Thickness(0, 0, 6, 32) };

        // Build structured non-duplicating sections matching MacPodcastView.swift:481-584
        var sections = new List<(string Id, string Title, string Meta, Button? ActionPill, PodcastIssue[] Issues, bool IsStudioSection, bool IsPlayingGroup, PodcastChannel? LoadMoreChannel)>();

        if (_selectedShowKey == "all")
        {
            var inProgress = allIssues.Where(i => i.PositionSec > 5).ToArray();
            var inProgressIds = inProgress.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            if (inProgress.Length > 0)
            {
                var resumeBtn = MakePaperPill(L10n.T("接着听"), accent: false, p, tint);
                var firstIp = inProgress[0];
                resumeBtn.Click += (_, _) => ToggleDeskIssuePlayback(firstIp, forcePlay: true);
                sections.Add(("playing-now", L10n.T("正在听"), $"{inProgress.Length} 期未完", resumeBtn, inProgress, false, true, null));
            }

            var studioAll = allIssues.Where(i => i.StudioKind is not null).ToArray();
            var studioRest = studioAll.Where(i => !inProgressIds.Contains(i.Id)).ToArray();
            if (studioAll.Length == 0 || studioRest.Length > 0)
            {
                string studioMeta = studioAll.Length == 0
                    ? L10n.T("由你的书架酿制")
                    : (studioRest.Length < studioAll.Length
                        ? $"其余 {studioRest.Length} 期 · 由你的书架酿制"
                        : $"{studioRest.Length} 期 · 由你的书架酿制");
                var brewBtn = MakePaperPill(L10n.T("+ 酿一期"), accent: true, p, tint);
                brewBtn.Click += async (_, _) => await ShowStudioSheetAsync();
                sections.Add(("studio", L10n.T("书房电台"), studioMeta, brewBtn, studioRest, studioAll.Length == 0, false, null));
            }

            foreach (var show in externalShows)
            {
                var showAll = allIssues.Where(i => MatchesExternalShow(i, show)).OrderByDescending(i => i.PublishedAt).ToArray();
                var rest = showAll.Where(i => !inProgressIds.Contains(i.Id)).ToArray();
                if (rest.Length == 0) continue;
                string prefix = rest.Length < showAll.Length ? $"其余 {rest.Length} 期" : $"{rest.Length} 期";
                bool hasEnglish = rest.Any(i => (i.Language ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase));
                string suffix = hasEnglish ? L10n.T(" · 外语伴听就绪") : L10n.T(" · 原声访谈");
                Button? viewAllBtn = null;
                if (rest.Length > 6 || show.ChannelId is not null)
                {
                    var keyCapture = show.Key;
                    viewAllBtn = MakePaperPill(L10n.T("查看全部"), accent: false, p, tint);
                    viewAllBtn.Click += (_, _) => { _selectedShowKey = keyCapture; _shelf.RefreshCurrentSubPage(); };
                }
                sections.Add((show.Key, show.Title, prefix + suffix, viewAllBtn, rest.Take(6).ToArray(), false, false, null));
            }
        }
        else if (_selectedShowKey == "studio")
        {
            var studioAll = allIssues.Where(i => i.StudioKind is not null).ToArray();
            var brewBtn = MakePaperPill(L10n.T("+ 酿一期"), accent: true, p, tint);
            brewBtn.Click += async (_, _) => await ShowStudioSheetAsync();
            string meta = studioAll.Length == 0 ? L10n.T("由你的书架酿制") : $"{studioAll.Length} 期 · 防剧透锁";
            sections.Add(("studio", L10n.T("书房电台"), meta, brewBtn, studioAll, studioAll.Length == 0, false, null));
        }
        else
        {
            var matchedShow = externalShows.FirstOrDefault(s => s.Key == _selectedShowKey);
            string showName = matchedShow?.Title ?? L10n.T("收听台");
            var list = matchedShow is not null
                ? allIssues.Where(i => MatchesExternalShow(i, matchedShow)).OrderByDescending(i => i.PublishedAt).ToArray()
                : [];
            var matchedChannel = matchedShow?.ChannelId is { Length: > 0 } cid
                ? channels.FirstOrDefault(c => c.Id == cid)
                : channels.FirstOrDefault(c => string.Equals(c.Title, showName, StringComparison.OrdinalIgnoreCase));
            var allViewBtn = MakePaperPill(L10n.T("全部节目"), accent: false, p, tint);
            allViewBtn.Click += (_, _) => { _selectedShowKey = "all"; _shelf.RefreshCurrentSubPage(); };
            sections.Add((_selectedShowKey, showName, $"{list.Length} 期", allViewBtn, list, false, false, matchedChannel));
        }

        bool showDiscovery = _selectedShowKey == "all" && externalShows.Count == 0;
        var allGroupIds = sections.Select(s => s.Id).Concat(showDiscovery ? ["discovery"] : []).ToArray();
        _allCollapsed = allGroupIds.Length > 0 && allGroupIds.All(id => _collapsedGroups.Contains(id));

        var leftHead = new Grid { Height = 28 };
        var lhSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
        string stationTitle = _selectedShowKey switch
        {
            "studio" => L10n.T("书房电台"),
            "all" => L10n.T("收听台"),
            _ => externalShows.FirstOrDefault(s => s.Key == _selectedShowKey)?.Title ?? L10n.T("收听台"),
        };
        lhSp.Children.Add(new TextBlock { Text = stationTitle, FontFamily = serif, FontSize = 16.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        lhSp.Children.Add(new TextBlock { Text = $"{allGroupIds.Length} 组 · {allIssues.Count} 期", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        leftHead.Children.Add(lhSp);

        var foldSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        foldSp.Children.Add(YYIcons.Make(_allCollapsed ? "chevron-right" : "chevron-down", 9.5, ThemeHelper.Brush(p.T2)));
        foldSp.Children.Add(new TextBlock { Text = _allCollapsed ? L10n.T("全部展开") : L10n.T("全部折叠"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T2) });
        var foldBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = foldSp,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            UseSystemFocusVisuals = false,
        };
        foldBtn.Click += (_, _) =>
        {
            if (_allCollapsed) _collapsedGroups.Clear();
            else foreach (var gid in allGroupIds) _collapsedGroups.Add(gid);
            _shelf.RefreshCurrentSubPage();
        };
        leftHead.Children.Add(foldBtn);
        leftCol.Children.Add(leftHead);

        foreach (var sec in sections)
        {
            if (sec.IsStudioSection && sec.Issues.Length == 0 && sec.ActionPill is not null)
            {
                leftCol.Children.Add(BuildStudioGroupCard([], sec.ActionPill, p, tint, serif));
            }
            else
            {
                leftCol.Children.Add(BuildStationGroupCard(sec.Id, sec.Title, sec.Meta, sec.ActionPill, sec.Issues, sec.IsPlayingGroup, sec.LoadMoreChannel, p, tint, serif));
            }
        }

        if (showDiscovery)
        {
            leftCol.Children.Add(BuildDiscoveryCard(p, tint, serif));
        }

        var leftScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Stretch,
            Content = leftCol,
        };
        Grid.SetColumn(leftScroll, 0);
        workspace.Children.Add(leftScroll);

        // ===== 右栏：留白书桌（固定贴顶，绝不随左栏滚动；含悬浮命令下拉层） =====
        var rightOverlayHost = new Grid { VerticalAlignment = VerticalAlignment.Top };
        var rightCol = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Top };

        var now = DateTime.Now;
        string greeting = now.Hour switch
        {
            < 5 => L10n.T("夜深了"),
            < 11 => L10n.T("早上好"),
            < 14 => L10n.T("中午好"),
            < 18 => L10n.T("下午好"),
            _ => L10n.T("晚上好"),
        };
        var greetLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        greetLine.Children.Add(new TextBlock { Text = greeting, FontFamily = serif, FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        greetLine.Children.Add(new Border
        {
            Width = 4,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = ThemeHelper.Brush(tint.Accent),
            Opacity = 0.72,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });
        greetLine.Children.Add(new TextBlock
        {
            Text = FormatChineseLiteraryDate(now),
            FontSize = 12,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6),
        });
        rightCol.Children.Add(greetLine);

        // Clickable clipped quote line -> opens `.clips` drawer (MacPodcastView.swift:1079-1108)
        var latestClip = clips.FirstOrDefault();
        var quoteTb = new TextBlock
        {
            FontFamily = serif,
            FontSize = 13.5,
            LineHeight = 20,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (latestClip is not null)
        {
            quoteTb.Inlines.Add(new Run { Text = $"「{latestClip.QuoteText}」" });
            quoteTb.Inlines.Add(new Run
            {
                Text = $"  {latestClip.ShowTitle} · {FormatClock(latestClip.T0)}",
                FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
            });
        }
        else
        {
            quoteTb.Inlines.Add(new Run { Text = L10n.T("「把声音读成一本书，把书架酿成一档只属于你的私人电台。」") });
            quoteTb.Inlines.Add(new Run
            {
                Text = $"  {L10n.T("书房电台")}",
                FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
            });
        }

        var quoteBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 2, 0, 2),
            Margin = new Thickness(0, 8, 0, 0),
            UseSystemFocusVisuals = false,
            Content = quoteTb,
        };
        ToolTipService.SetToolTip(quoteBtn, L10n.T("查看原声书摘"));
        quoteBtn.Click += (_, _) => OpenDrawer("clips");
        rightCol.Children.Add(quoteBtn);

        // Command search bar + floating dropdown
        var cmdBox = new TextBox
        {
            PlaceholderText = L10n.T("搜节目、听过的句子，或粘贴单集链接…"),
            FontSize = 12.5,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 6, 0, 4),
        };
        YYChrome.StripTextBoxChrome(cmdBox);
        _stationCmdBox = cmdBox;

        var leadIconHost = new ContentPresenter
        {
            VerticalAlignment = VerticalAlignment.Center,
            Content = YYIcons.Make("search", 13.5, ThemeHelper.Brush(p.T3)),
        };
        var trailSlot = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var slashCap = YYChrome.MakeKeycap("/", dark);
        var clearCmdBtn = new Button
        {
            Width = 20,
            Height = 20,
            MinWidth = 20,
            MinHeight = 20,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = YYIcons.Make("xmark", 9.5, ThemeHelper.Brush(p.T3)),
        };
        clearCmdBtn.Click += (_, _) =>
        {
            cmdBox.Text = "";
            cmdBox.Focus(FocusState.Programmatic);
        };
        trailSlot.Content = slashCap;

        var cmdBorder = new Border
        {
            CornerRadius = new CornerRadius(11),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Margin = new Thickness(0, 14, 0, 0),
        };
        var cmdGrid = new Grid { ColumnSpacing = 8, Padding = new Thickness(12, 0, 10, 0), Height = 40 };
        cmdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cmdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cmdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cmdGrid.Children.Add(leadIconHost);
        Grid.SetColumn(cmdBox, 1);
        cmdGrid.Children.Add(cmdBox);
        Grid.SetColumn(trailSlot, 2);
        cmdGrid.Children.Add(trailSlot);
        cmdBorder.Child = cmdGrid;
        cmdBorder.PointerPressed += (_, e) => e.Handled = true;
        rightCol.Children.Add(cmdBorder);

        var dropdownStack = new StackPanel { Spacing = 2, Padding = new Thickness(6) };
        var dropdownBorder = new Border
        {
            Visibility = Visibility.Collapsed,
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = dropdownStack,
        };
        dropdownBorder.PointerPressed += (_, e) => e.Handled = true;
        ThemeHelper.ApplyElevation(dropdownBorder, rightOverlayHost, 18f);
        _commandDropdownHost = dropdownBorder;
        _commandDropdownStack = dropdownStack;

        void PositionAndRebuildDropdown()
        {
            string q = cmdBox.Text.Trim();
            bool isUrl = LooksLikePodcastUrl(q);
            leadIconHost.Content = YYIcons.Make(isUrl ? "link-chain" : "search", 13.5, ThemeHelper.Brush(isUrl ? tint.Accent : p.T3));
            trailSlot.Content = string.IsNullOrEmpty(q) ? slashCap : clearCmdBtn;

            try
            {
                var pt = cmdBorder.TransformToVisual(rightOverlayHost).TransformPoint(new Point(0, cmdBorder.ActualHeight + 6));
                if (pt.Y > 20) dropdownBorder.Margin = new Thickness(0, pt.Y, 0, 0);
                else dropdownBorder.Margin = new Thickness(0, 128, 0, 0);
            }
            catch
            {
                dropdownBorder.Margin = new Thickness(0, 128, 0, 0);
            }

            PopulateCommandDropdown(dropdownStack, q, allIssues, clips, p, tint);
        }

        cmdBox.GotFocus += (_, _) =>
        {
            cmdBorder.Background = ThemeHelper.Brush(p.Card);
            cmdBorder.BorderBrush = ThemeHelper.Brush(tint.Edge);
            cmdBorder.BorderThickness = new Thickness(1);
            PositionAndRebuildDropdown();
            dropdownBorder.Visibility = Visibility.Visible;
        };
        cmdBox.LostFocus += (_, _) =>
        {
            cmdBorder.Background = ThemeHelper.Brush(p.Inset);
            cmdBorder.BorderBrush = ThemeHelper.Brush(p.Line);
            cmdBorder.BorderThickness = new Thickness(0.5);
        };
        cmdBox.TextChanged += (_, _) =>
        {
            PositionAndRebuildDropdown();
            dropdownBorder.Visibility = Visibility.Visible;
        };
        cmdBox.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                HideCommandDropdown();
                return;
            }
            if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(cmdBox.Text)) return;
            e.Handled = true;
            string q = cmdBox.Text.Trim();
            HideCommandDropdown();
            if (LooksLikePodcastUrl(q))
            {
                await ImportUrlDirectFromDeskAsync(q, cmdBox);
            }
            else
            {
                await ShowAddPodcastSheetAsync(q);
            }
        };

        // 5 Circular Quick-Launch Buttons (MacPodcastView.swift:1344-1392)
        var quickGrid = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 16, 0, 0) };
        (string Icon, string Label, bool Accent, Func<Task> Act)[] quicks =
        [
            ("plus", L10n.T("酿一期"), true, () => ShowStudioSheetAsync()),
            ("checklist-lines", L10n.T("书房周刊"), false, () => OpenOrBrewStudioAsync(StudioKind.WeeklyDigest)),
            ("circles-venn", L10n.T("跨书圆桌"), false, () => OpenOrBrewStudioAsync(StudioKind.Roundtable)),
            ("translate", L10n.T("生词伴听"), false, () => OpenOrBrewStudioAsync(StudioKind.VocabRadio)),
            ("link-chain", L10n.T("贴单集"), false, () => PasteLinkOrFocusSearchAsync(cmdBox)),
        ];
        for (int i = 0; i < quicks.Length; i++)
        {
            quickGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (icon, lbl, acc, act) = quicks[i];
            var sp = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            var circle = new Button
            {
                Width = 42,
                Height = 42,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(21),
                Background = ThemeHelper.Brush(acc ? tint.Soft : p.Card),
                BorderBrush = ThemeHelper.Brush(acc ? tint.Edge : p.Line),
                BorderThickness = new Thickness(0.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                UseSystemFocusVisuals = false,
                Content = YYIcons.Make(icon, 15, ThemeHelper.Brush(acc ? tint.Accent : p.T1)),
            };
            var fn = act;
            circle.Click += async (_, _) => await fn();
            sp.Children.Add(circle);
            sp.Children.Add(new TextBlock { Text = lbl, FontSize = 11, Foreground = ThemeHelper.Brush(acc ? tint.Accent : p.T3), HorizontalAlignment = HorizontalAlignment.Center });
            Grid.SetColumn(sp, i);
            quickGrid.Children.Add(sp);
        }
        rightCol.Children.Add(quickGrid);

        // Single Compact Tabletop Listening Card (stale extra 原声书摘 card removed!)
        var activeIssue = ResolveDeskActiveIssue(allIssues);
        var tabletopCard = BuildTabletopPlayerCard(activeIssue, p, tint, serif, leafSurface);
        tabletopCard.Margin = new Thickness(0, 20, 0, 0);
        rightCol.Children.Add(tabletopCard);

        rightOverlayHost.Children.Add(rightCol);
        Canvas.SetZIndex(dropdownBorder, 40);
        rightOverlayHost.Children.Add(dropdownBorder);

        Grid.SetColumn(rightOverlayHost, 1);
        workspace.Children.Add(rightOverlayHost);
        return workspace;
    }

    private void HideCommandDropdown()
    {
        if (_commandDropdownHost is not null)
            _commandDropdownHost.Visibility = Visibility.Collapsed;
    }

    private void PopulateCommandDropdown(
        StackPanel stack,
        string rawQuery,
        IReadOnlyList<PodcastIssue> allIssues,
        PodcastClip[] clips,
        YYPalette p,
        YYTintColors tint)
    {
        stack.Children.Clear();
        string q = rawQuery.Trim();

        Button MakeDropRow(string icon, string title, string sub, string trailing, Action onClick)
        {
            var g = new Grid { ColumnSpacing = 9, Padding = new Thickness(9, 7, 9, 7) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            g.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(p.Inset),
                Child = YYIcons.Make(icon, 11.5, ThemeHelper.Brush(p.T2)),
            });

            var m = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            m.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            m.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(m, 1);
            g.Children.Add(m);

            var trailTb = new TextBlock { Text = trailing, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(trailTb, 2);
            g.Children.Add(trailTb);

            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                UseSystemFocusVisuals = false,
                Content = g,
            };
            b.Click += (_, _) => onClick();
            return b;
        }

        if (string.IsNullOrEmpty(q))
        {
            stack.Children.Add(new TextBlock
            {
                Text = L10n.T("快捷直达"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(9, 4, 9, 2),
            });
            stack.Children.Add(MakeDropRow(
                "link-chain",
                L10n.T("粘贴单集链接或 RSS（免订阅直听）"),
                L10n.T("支持小宇宙、Apple Podcasts、单集网页或音频直链"),
                L10n.T("去导入"),
                async () => { HideCommandDropdown(); await ShowAddPodcastSheetAsync(); }));
            stack.Children.Add(MakeDropRow(
                "wave",
                L10n.T("导入本地音频文件"),
                L10n.T("把 .mp3 / .m4a 讲座或访谈放进收听台"),
                L10n.T("选择"),
                async () => { HideCommandDropdown(); await PickAndImportLocalAudioAsync(); }));
            stack.Children.Add(MakeDropRow(
                "list",
                L10n.T("导入 OPML 订阅列表"),
                L10n.T("从其他播客应用一键迁入"),
                L10n.T("导入"),
                async () => { HideCommandDropdown(); await PickAndImportOpmlAsync(); }));
            return;
        }

        if (LooksLikePodcastUrl(q))
        {
            stack.Children.Add(new TextBlock
            {
                Text = L10n.T("识别到链接"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(9, 4, 9, 2),
            });
            stack.Children.Add(MakeDropRow(
                "link-chain",
                L10n.T("直接导入此单集 / RSS"),
                q,
                L10n.T("导入"),
                async () => { HideCommandDropdown(); await ImportUrlDirectFromDeskAsync(q, _stationCmdBox); }));
            return;
        }

        var matchingIssues = allIssues
            .Where(i => i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || i.ShowTitle.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(4)
            .ToArray();
        if (matchingIssues.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = L10n.T("本地单集"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(9, 4, 9, 2),
            });
            foreach (var iss in matchingIssues)
            {
                var target = iss;
                stack.Children.Add(MakeDropRow(
                    target.StudioKind is not null ? "book-open" : "radio",
                    target.Title,
                    target.ShowTitle,
                    L10n.T("打开"),
                    () =>
                    {
                        HideCommandDropdown();
                        OpenIssueAt(target.Id);
                        _shelf.RefreshCurrentSubPage();
                    }));
            }
        }

        var matchingClips = clips
            .Where(c => c.QuoteText.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (matchingClips.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = L10n.T("听过的书摘"),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(9, 4, 9, 2),
            });
            foreach (var cl in matchingClips)
            {
                var targetClip = cl;
                stack.Children.Add(MakeDropRow(
                    "bookmark",
                    $"「{targetClip.QuoteText}」",
                    $"{targetClip.ShowTitle} · {FormatClock(targetClip.T0)}",
                    L10n.T("听原处"),
                    () =>
                    {
                        HideCommandDropdown();
                        OpenIssueAt(targetClip.IssueId, targetClip.T0);
                        _shelf.RefreshCurrentSubPage();
                    }));
            }
        }

        string queryCopy = q;
        stack.Children.Add(MakeDropRow(
            "search",
            $"在公开播客目录搜索「{queryCopy}」",
            L10n.T("查找 RSS 订阅源并加入收听台"),
            L10n.T("搜索"),
            async () =>
            {
                HideCommandDropdown();
                await ShowAddPodcastSheetAsync(queryCopy);
            }));
    }

    private async Task OpenOrBrewStudioAsync(StudioKind kind)
    {
        var existing = _library.Podcasts.GetIssues().FirstOrDefault(i => i.StudioKind == kind);
        if (existing is not null)
        {
            OpenIssueAt(existing.Id);
            _shelf.RefreshCurrentSubPage();
            return;
        }
        await ShowStudioSheetAsync(kind);
    }

    private async Task PasteLinkOrFocusSearchAsync(TextBox cmdBox)
    {
        try
        {
            var dpv = Clipboard.GetContent();
            if (dpv.Contains(StandardDataFormats.Text))
            {
                string clip = (await dpv.GetTextAsync()).Trim();
                if (LooksLikePodcastUrl(clip) && !clip.Contains('\n') && clip.Length < 400)
                {
                    cmdBox.Text = clip;
                }
            }
        }
        catch { }
        cmdBox.Focus(FocusState.Programmatic);
    }

    private async Task ImportUrlDirectFromDeskAsync(string url, TextBox? cmdBox)
    {
        try
        {
            var (_, single) = await _library.Podcasts.AddUrlOrFeedAsync(url, putLatestOnDesk: true);
            if (cmdBox is not null) cmdBox.Text = "";
            if (single is not null) OpenIssueAt(single.Id);
            _shelf.RefreshBooks();
            _shelf.ShowToast(L10n.T("已导入并放到收听台"), canUndo: false);
        }
        catch (Exception ex)
        {
            _shelf.ShowToast($"解析失败：{ex.Message}", canUndo: false);
        }
    }

    private async Task PickAndImportLocalAudioAsync()
    {
        string? path = await NativePickerHelper.PickSingleFileAsync(_shelf.OwnerWindow, [".mp3", ".m4a", ".wav", ".flac"]);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var iss = await _library.Podcasts.ImportLocalAudioAsync(path);
            _shelf.RefreshBooks();
            _shelf.ShowToast($"已将《{iss.Title}》放上收听台", canUndo: false);
        }
        catch (Exception ex)
        {
            _shelf.ShowToast($"导入失败：{ex.Message}", canUndo: false);
        }
    }

    private async Task PickAndImportOpmlAsync()
    {
        string? path = await NativePickerHelper.PickSingleFileAsync(_shelf.OwnerWindow, [".opml", ".xml"]);
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            int cnt = await _library.Podcasts.ImportOpmlAsync(path);
            _shelf.RefreshBooks();
            _shelf.ShowToast($"已导入 {cnt} 档播客订阅", canUndo: false);
        }
        catch (Exception ex)
        {
            _shelf.ShowToast($"OPML 导入失败：{ex.Message}", canUndo: false);
        }
    }

    private void EnsureDiscoveryLoadedAsync()
    {
        if (_loadingDiscovery) return;
        _loadingDiscovery = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var charts = await _library.Podcasts.FetchItunesTopChartsAsync();
                _shelf.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    _discoveredShows = charts;
                    _loadingDiscovery = false;
                    if (OpenIssueId is null) _shelf.RefreshCurrentSubPage();
                });
            }
            catch
            {
                _shelf.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    _loadingDiscovery = false;
                });
            }
        });
    }

    // ---------- 2. 右侧 386px 文房抽屉（1:1 PodcastDrawerContentView + 非破坏性内联随想框） ----------

    private Border BuildRightDrawer(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        Color leafSurface = dark ? Color.FromArgb(255, 31, 30, 28) : Color.FromArgb(255, 248, 245, 239);

        var clips = _library.Core.PodcastClips(null);
        var laterList = _library.Podcasts.GetIssues(PodcastPlacement.Archive)
            .Concat(_library.Podcasts.GetIssues(PodcastPlacement.Newsstand))
            .DistinctBy(x => x.Id)
            .ToArray();
        int clonedCount = _library.LiveVoice.GetClonedCards().Count;
        int voiceCount = 4 + clonedCount;

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Contextually paired 2-tab capsule (PodcastSharedUI.swift:619-628):
        // Bookmark trigger opens `原声书摘 | 稍后听`; Wave trigger opens `声音卡 | 本机整理`
        bool isClipLaterPair = _drawerTab is "clips" or "later";
        (string Key, string Label)[] visibleTabs = isClipLaterPair
            ? [("clips", $"原声书摘 · {clips.Length}"), ("later", $"稍后听 · {laterList.Length}")]
            : [("voices", $"声音卡 · {voiceCount}"), ("local", L10n.T("本机整理"))];

        var topHeader = new Grid { Padding = new Thickness(16, 12, 14, 12), ColumnSpacing = 8 };
        topHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var pairRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (k, lbl) in visibleTabs)
        {
            bool on = _drawerTab == k;
            var tb = new Button
            {
                Content = lbl,
                Height = 25,
                MinHeight = 25,
                Padding = new Thickness(11, 0, 11, 0),
                FontSize = 11.5,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T3),
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                BorderBrush = on ? ThemeHelper.Brush(p.Line) : null,
                BorderThickness = new Thickness(on ? 0.5 : 0),
                CornerRadius = new CornerRadius(12.5),
                UseSystemFocusVisuals = false,
            };
            string nextTab = k;
            tb.Click += (_, _) => { StopDrawerAudioPreviews(); _drawerTab = nextTab; _shelf.RefreshCurrentSubPage(); };
            pairRow.Children.Add(tb);
        }

        topHeader.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(15),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(2.5),
            Child = pairRow,
        });

        // Subtle switch-pair icon button so user can jump between the two pairs without closing
        var switchPairBtn = MakeCircleIconButton(isClipLaterPair ? "wave" : "bookmark", isClipLaterPair ? L10n.T("切换到声音卡 · 本机整理") : L10n.T("切换到原声书摘 · 稍后听"), p);
        switchPairBtn.Width = 28;
        switchPairBtn.Height = 28;
        switchPairBtn.CornerRadius = new CornerRadius(14);
        switchPairBtn.Click += (_, _) =>
        {
            StopDrawerAudioPreviews();
            _drawerTab = isClipLaterPair ? "voices" : "clips";
            if (_drawerTab == "clips") _hasNewClipDot = false;
            _shelf.RefreshCurrentSubPage();
        };
        Grid.SetColumn(switchPairBtn, 2);
        topHeader.Children.Add(switchPairBtn);

        var closeBtn = new Button
        {
            Width = 28,
            Height = 28,
            MinWidth = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = YYIcons.Make("xmark", 12, ThemeHelper.Brush(p.T3)),
        };
        ToolTipService.SetToolTip(closeBtn, L10n.T("关闭"));
        closeBtn.Click += (_, _) => { CloseDrawer(); _shelf.RefreshCurrentSubPage(); };
        Grid.SetColumn(closeBtn, 3);
        topHeader.Children.Add(closeBtn);
        outer.Children.Add(topHeader);

        var divider = new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) };
        Grid.SetRow(divider, 1);
        outer.Children.Add(divider);

        var bodyStack = new StackPanel { Spacing = 10, Padding = new Thickness(16) };
        string? pendingFocusClip = _focusClipIdOnOpen;
        _focusClipIdOnOpen = null;

        switch (_drawerTab)
        {
            case "clips":
            {
                if (clips.Length == 0)
                {
                    var emptySp = new StackPanel { Spacing = 6 };
                    emptySp.Children.Add(new TextBlock { Text = L10n.T("还没有原声书摘"), FontFamily = serif, FontSize = 14.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                    emptySp.Children.Add(new TextBlock
                    {
                        Text = L10n.T("收听时点「划线」或按 H，即可截取当前句子的原声片段永久留存，整集音频清理后也不丢失。"),
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.T3),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 19,
                    });
                    bodyStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(11),
                        Background = ThemeHelper.Brush(leafSurface),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(14),
                        Child = emptySp,
                    });
                }

                foreach (var cl in clips)
                {
                    var item = cl;
                    bool isPlayingClip = _playingClipId == item.Id;
                    double dur = Math.Max(1.0, item.T1 - item.T0);

                    var sp = new StackPanel { Spacing = 8 };
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"「{item.QuoteText}」",
                        FontFamily = serif,
                        FontSize = 13.5,
                        Foreground = ThemeHelper.Brush(p.T1),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 21,
                    });

                    var metaRow = new Grid { ColumnSpacing = 6 };
                    metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    metaRow.Children.Add(new TextBlock
                    {
                        Text = $"{item.ShowTitle} · {FormatClock(item.T0)}",
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });

                    string? clipPath = !string.IsNullOrWhiteSpace(item.AudioRelPath)
                        ? Path.Combine(_library.Core.PodcastClipsDir(), item.AudioRelPath)
                        : null;
                    var replayBtn = MakePaperPill(isPlayingClip ? L10n.T("播放中…") : $"▶ {dur:0.0}s", accent: isPlayingClip, p, tint);
                    replayBtn.Height = 23;
                    replayBtn.FontSize = 10.5;
                    replayBtn.Click += (_, _) =>
                    {
                        if (_playingClipId == item.Id)
                        {
                            StopDrawerAudioPreviews();
                            _shelf.RefreshCurrentSubPage();
                            return;
                        }
                        if (!string.IsNullOrEmpty(clipPath) && File.Exists(clipPath))
                        {
                            try
                            {
                                if (_playing) StopPlayback();
                                AudioPlayer.StopActivePreview();
                                App.CurrentApp.ClaimAudioPlayback(_clipPreviewPlayer, () => _shelf.DispatcherQueue.TryEnqueue(() =>
                                {
                                    if (_disposed) return;
                                    StopDrawerAudioPreviews();
                                    if (_drawerOpen && _drawerTab == "clips")
                                        _shelf.RefreshCurrentSubPage();
                                }));
                                _clipPreviewPlayer.Pause();
                                _clipPreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(clipPath));
                                _clipPreviewPlayer.Play();
                                _playingClipId = item.Id;
                                _shelf.RefreshCurrentSubPage();
                            }
                            catch { }
                        }
                        else
                        {
                            CloseDrawer();
                            OpenIssueAt(item.IssueId, item.T0, autoPlay: true);
                            _shelf.RefreshCurrentSubPage();
                        }
                    };
                    Grid.SetColumn(replayBtn, 1);
                    metaRow.Children.Add(replayBtn);

                    var jumpBtn = new Button
                    {
                        Width = 24,
                        Height = 23,
                        MinWidth = 24,
                        MinHeight = 23,
                        Padding = new Thickness(0),
                        CornerRadius = new CornerRadius(6),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        UseSystemFocusVisuals = false,
                        Content = YYIcons.Make("chevron-right", 10.5, ThemeHelper.Brush(p.T3)),
                    };
                    ToolTipService.SetToolTip(jumpBtn, L10n.T("回到原句"));
                    jumpBtn.Click += (_, _) =>
                    {
                        CloseDrawer();
                        OpenIssueAt(item.IssueId, item.T0);
                        _shelf.RefreshCurrentSubPage();
                    };
                    Grid.SetColumn(jumpBtn, 2);
                    metaRow.Children.Add(jumpBtn);
                    sp.Children.Add(metaRow);

                    // Non-destructive inline dashed rule + `添一句随想…` TextBox (PodcastSharedUI.swift:761-775)
                    var dashRule = new XamlLine
                    {
                        X1 = 0,
                        X2 = 310,
                        Y1 = 0,
                        Y2 = 0,
                        Stroke = ThemeHelper.Brush(p.Line),
                        StrokeThickness = 0.75,
                        StrokeDashArray = new DoubleCollection { 3, 3 },
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Margin = new Thickness(0, 2, 0, 0),
                    };
                    sp.SizeChanged += (_, se) => { if (se.NewSize.Width > 8) dashRule.X2 = se.NewSize.Width; };
                    sp.Children.Add(dashRule);

                    string cid = item.Id;
                    var noteBox = new TextBox
                    {
                        Text = _library.Podcasts.GetClipThought(item),
                        PlaceholderText = L10n.T("添一句随想…"),
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.T2),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(0, 2, 0, 2),
                    };
                    YYChrome.StripTextBoxChrome(noteBox);
                    noteBox.LostFocus += (_, _) => _library.Podcasts.SetClipThought(cid, noteBox.Text);
                    noteBox.KeyDown += (_, ke) =>
                    {
                        if (ke.Key == VirtualKey.Enter)
                        {
                            ke.Handled = true;
                            _library.Podcasts.SetClipThought(cid, noteBox.Text);
                            _shelf.ShowToast(L10n.T("已记下随想"), canUndo: false);
                        }
                    };
                    if (pendingFocusClip == cid)
                    {
                        noteBox.Loaded += (_, _) => noteBox.Focus(FocusState.Programmatic);
                    }
                    sp.Children.Add(noteBox);

                    var ctx = new MenuFlyout();
                    var jumpMi = new MenuFlyoutItem { Text = L10n.T("跳到原句") };
                    jumpMi.Click += (_, _) =>
                    {
                        CloseDrawer();
                        OpenIssueAt(item.IssueId, item.T0);
                        _shelf.RefreshCurrentSubPage();
                    };
                    var delMi = new MenuFlyoutItem { Text = L10n.T("删除书摘"), Foreground = ThemeHelper.Brush(p.Danger) };
                    delMi.Click += (_, _) =>
                    {
                        try { _library.Core.PodcastDeleteClip(cid); } catch { }
                        _shelf.RefreshCurrentSubPage();
                    };
                    ctx.Items.Add(jumpMi);
                    ctx.Items.Add(delMi);

                    bodyStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(11),
                        Background = ThemeHelper.Brush(leafSurface),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(13, 12, 13, 10),
                        ContextFlyout = ctx,
                        Child = sp,
                    });
                }
                break;
            }
            case "later":
            {
                if (laterList.Length == 0)
                {
                    bodyStack.Children.Add(new TextBlock
                    {
                        Text = L10n.T("稍后听空着 · 在搜索条粘贴单集链接或点书签即可存到这里"),
                        FontSize = 12.5,
                        Foreground = ThemeHelper.Brush(p.T3),
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(8, 32, 8, 32),
                    });
                }
                else
                {
                    var listStack = new StackPanel { Spacing = 0 };
                    for (int idx = 0; idx < Math.Min(24, laterList.Length); idx++)
                    {
                        if (idx > 0) listStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
                        var target = laterList[idx];
                        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(2, 10, 2, 10) };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var mark = BuildShowMark(target.ShowTitle, target.ArtworkUrl, target.StudioKind is not null, 22, p, tint);
                        mark.VerticalAlignment = VerticalAlignment.Top;
                        row.Children.Add(mark);

                        var metaSp = new StackPanel { Spacing = 2 };
                        metaSp.Children.Add(new TextBlock { Text = target.Title, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                        metaSp.Children.Add(new TextBlock { Text = $"{target.ShowTitle} · {Math.Max(1, (int)Math.Round(target.DurationSec / 60.0))} 分钟", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                        var openBtn = new Button
                        {
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Background = new SolidColorBrush(Colors.Transparent),
                            BorderThickness = new Thickness(0),
                            Padding = new Thickness(0),
                            UseSystemFocusVisuals = false,
                            Content = metaSp,
                        };
                        openBtn.Click += (_, _) =>
                        {
                            CloseDrawer();
                            OpenIssueAt(target.Id);
                            _shelf.RefreshCurrentSubPage();
                        };
                        Grid.SetColumn(openBtn, 1);
                        row.Children.Add(openBtn);

                        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
                        var playNowBtn = new Button
                        {
                            Width = 24,
                            Height = 24,
                            Padding = new Thickness(0),
                            Background = new SolidColorBrush(Colors.Transparent),
                            BorderThickness = new Thickness(0),
                            UseSystemFocusVisuals = false,
                            Content = YYIcons.Make("play", 10, ThemeHelper.Brush(p.T3), filled: true),
                        };
                        ToolTipService.SetToolTip(playNowBtn, L10n.T("立即收听"));
                        playNowBtn.Click += (_, _) =>
                        {
                            try { _library.Core.PodcastSetPlacement(target.Id, PodcastPlacement.Desk); } catch { }
                            CloseDrawer();
                            ToggleDeskIssuePlayback(target, forcePlay: true);
                        };
                        acts.Children.Add(playNowBtn);

                        var moveDeskBtn = new Button
                        {
                            Width = 24,
                            Height = 24,
                            Padding = new Thickness(0),
                            Background = new SolidColorBrush(Colors.Transparent),
                            BorderThickness = new Thickness(0),
                            UseSystemFocusVisuals = false,
                            Content = YYIcons.Make("xmark", 10.5, ThemeHelper.Brush(p.T3)),
                        };
                        ToolTipService.SetToolTip(moveDeskBtn, L10n.T("移回在听书桌"));
                        moveDeskBtn.Click += (_, _) =>
                        {
                            try { _library.Core.PodcastSetPlacement(target.Id, PodcastPlacement.Desk); } catch { }
                            _shelf.ShowToast(L10n.T("已移回在听书桌"), canUndo: false);
                            _shelf.RefreshCurrentSubPage();
                        };
                        acts.Children.Add(moveDeskBtn);
                        Grid.SetColumn(acts, 2);
                        row.Children.Add(acts);

                        listStack.Children.Add(row);
                    }
                    bodyStack.Children.Add(listStack);
                }
                break;
            }
            case "voices":
            {
                (string Id, string Name, string Role, string Sample)[] studioVoices =
                [
                    ("linshen", L10n.T("林深 · 书房主播"), L10n.T("本地声卡 · 沉静温和 · 默认主讲"), L10n.T("欢迎回到书房电台，所有内容都严格停在你读到的页码之前。")),
                    ("suwan", L10n.T("苏晚 · 理性对谈"), L10n.T("本地声卡 · 清晰克制 · 跨书圆桌搭档"), L10n.T("把同一件事放进另一本书的语境里重说一遍，理解才会真正立体起来。")),
                    ("clara", "Clara", "English Host · Warm & Clear", "Let us revisit the words you highlighted this week."),
                    ("david", "David", "English Co-Host · Calm", "Every unfamiliar word makes more sense inside a living story."),
                ];
                foreach (var v in studioVoices)
                {
                    bool on = !_library.Prefs.PodcastRememberedVoices.TryGetValue(v.Id, out var rem) || rem;
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    row.Children.Add(BuildShowMark(v.Name, null, isStudio: true, 22, p, tint));
                    var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                    meta.Children.Add(new TextBlock { Text = v.Name, FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                    meta.Children.Add(new TextBlock { Text = v.Role, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    Grid.SetColumn(meta, 1);
                    row.Children.Add(meta);

                    var tryBtn = MakePaperPill(L10n.T("试听"), accent: false, p, tint);
                    tryBtn.Height = 24;
                    tryBtn.FontSize = 11;
                    string sample = v.Sample;
                    bool isEn = v.Id is "clara" or "david";
                    bool isFemale = v.Id is "suwan" or "clara";
                    tryBtn.Click += async (_, _) =>
                    {
                        string? vid = PickHostVoiceId(isFemale, isEn);
                        _isPreviewingInterruptAnswer = true;
                        try
                        {
                            await AudioPlayer.PreviewVoiceAsync(_library, vid, 1.0, sample);
                        }
                        finally
                        {
                            _isPreviewingInterruptAnswer = false;
                            if (_playing) App.CurrentApp.ClaimAudioPlayback(_shelf, CreateShelfPauseCallback());
                        }
                    };
                    Grid.SetColumn(tryBtn, 2);
                    row.Children.Add(tryBtn);

                    string vKey = v.Id;
                    var sw = new ToggleSwitch { IsOn = on, MinWidth = 0, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
                    sw.Toggled += (_, _) =>
                    {
                        _library.Prefs.PodcastRememberedVoices[vKey] = sw.IsOn;
                        _library.SavePrefs();
                    };
                    Grid.SetColumn(sw, 3);
                    row.Children.Add(sw);
                    bodyStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(11),
                        Background = ThemeHelper.Brush(leafSurface),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(11, 9, 11, 9),
                        Child = row,
                    });
                }

                foreach (var cloned in _library.LiveVoice.GetClonedCards())
                {
                    var cc = cloned;
                    var cRow = new Grid { ColumnSpacing = 8 };
                    cRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    cRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var cm = new StackPanel { Spacing = 1 };
                    cm.Children.Add(new TextBlock { Text = $"✦ {cc.Name}", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
                    cm.Children.Add(new TextBlock { Text = L10n.T("嘉宾声纹 · 本机安全区提取"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
                    cRow.Children.Add(cm);
                    bool isActiveCard = _library.Prefs.PodcastStudioVoiceStyle == "card:" + cc.Id;
                    var useBtn = MakePaperPill(isActiveCard ? L10n.T("✓ 当前主播") : L10n.T("设为主播"), accent: true, p, tint);
                    useBtn.Height = 24;
                    useBtn.Click += (_, _) =>
                    {
                        _library.Prefs.PodcastStudioVoiceStyle = "card:" + cc.Id;
                        _library.SavePrefs();
                        _shelf.RefreshCurrentSubPage();
                    };
                    Grid.SetColumn(useBtn, 1);
                    cRow.Children.Add(useBtn);
                    bodyStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(11),
                        Background = ThemeHelper.Brush(leafSurface),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(11, 9, 11, 9),
                        Child = cRow,
                    });
                }

                var cloneBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = L10n.T("🎙 克隆一段自己的主播声音…"),
                    FontSize = 11.5,
                };
                cloneBtn.Click += async (_, _) =>
                {
                    if (_shelf.WindowXamlRoot is { } xr)
                    {
                        var saved = await VoiceCloneAndDramaDialogs.ShowCloneVoiceDialogAsync(_shelf.OwnerWindow, xr, _library);
                        if (saved is not null) _shelf.RefreshCurrentSubPage();
                    }
                };
                bodyStack.Children.Add(cloneBtn);
                break;
            }
            default:
            {
                int chCnt = _library.Podcasts.GetChannels().Count;
                int issCnt = _library.Podcasts.GetIssues().Count;
                int clipCnt = clips.Length;
                var stats = _cachedStorageStats ?? (0, 0L, 0, 0L);
                string FormatStorageSummary((int AudioFiles, long AudioBytes, int ClipFiles, long ClipBytes) st) =>
                    $"单集缓存：{st.AudioFiles} 个文件 · {st.AudioBytes / (1024.0 * 1024.0):F1} MB\n原声书摘片段：{st.ClipFiles} 段 WAV · {st.ClipBytes / (1024.0 * 1024.0):F1} MB";

                var transcribable = (OpenIssueId is not null
                    ? _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == OpenIssueId)
                    : null)
                    ?? _library.Podcasts.GetIssues().FirstOrDefault(x => x.StudioKind is null && (!string.IsNullOrEmpty(x.LocalAudioPath) || !string.IsNullOrEmpty(x.AudioUrl)));

                if (transcribable is not null && transcribable.StudioKind is null)
                {
                    var trSp = new StackPanel { Spacing = 6 };
                    trSp.Children.Add(new TextBlock { Text = L10n.T("本机语音转逐字稿"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                    trSp.Children.Add(new TextBlock
                    {
                        Text = $"当前目标：《{transcribable.Title}》（支持 SenseVoice 离线模型 / 本地 Whisper / 已配置转写 API）",
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T3),
                        TextWrapping = TextWrapping.Wrap,
                    });
                    bool busy = _transcribingIssueId == transcribable.Id;
                    if (busy && !string.IsNullOrEmpty(_transcribeStatus))
                        trSp.Children.Add(new TextBlock { Text = _transcribeStatus, FontSize = 11, Foreground = ThemeHelper.Brush(tint.Accent), TextWrapping = TextWrapping.Wrap });

                    var trBtn = MakePaperPill(busy ? L10n.T("转写中…") : L10n.T("本机转文字"), accent: true, p, tint);
                    trBtn.IsEnabled = !busy;
                    string targetId = transcribable.Id;
                    trBtn.Click += async (_, _) => await RunOnDeviceTranscribeAsync(targetId);
                    trSp.Children.Add(trBtn);
                    bodyStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(11),
                        Background = ThemeHelper.Brush(leafSurface),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(12, 10, 12, 10),
                        Child = trSp,
                    });
                }

                var cacheSp = new StackPanel { Spacing = 6 };
                cacheSp.Children.Add(new TextBlock { Text = L10n.T("缓存与离线音频空间"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                var cacheSummaryTb = new TextBlock
                {
                    Text = _cachedStorageStats is not null ? FormatStorageSummary(stats) : L10n.T("正在统计缓存空间…"),
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T3),
                    LineHeight = 17,
                };
                cacheSp.Children.Add(cacheSummaryTb);

                var clearBtn = MakePaperPill(L10n.T("清理单集音频缓存"), accent: false, p, tint);
                clearBtn.Visibility = stats.AudioBytes > 0 ? Visibility.Visible : Visibility.Collapsed;
                clearBtn.Click += async (_, _) =>
                {
                    clearBtn.IsEnabled = false;
                    clearBtn.Content = L10n.T("清理中…");
                    long freed = await Task.Run(() => _library.Podcasts.ClearAudioCache());
                    var nextStats = await Task.Run(() => _library.Podcasts.GetStorageStats());
                    if (_disposed) return;
                    _cachedStorageStats = nextStats;
                    _shelf.ShowToast($"已清理 {freed / (1024.0 * 1024.0):F1} MB 缓存（原声书摘保留不受影响）", canUndo: false);
                    _shelf.RefreshCurrentSubPage();
                };
                cacheSp.Children.Add(clearBtn);

                if (!_loadingStorageStats)
                {
                    _loadingStorageStats = true;
                    _ = Task.Run(() => _library.Podcasts.GetStorageStats()).ContinueWith(t =>
                    {
                        _shelf.DispatcherQueue.TryEnqueue(() =>
                        {
                            _loadingStorageStats = false;
                            if (_disposed || !t.IsCompletedSuccessfully) return;
                            _cachedStorageStats = t.Result;
                            cacheSummaryTb.Text = FormatStorageSummary(t.Result);
                            clearBtn.Visibility = t.Result.AudioBytes > 0 ? Visibility.Visible : Visibility.Collapsed;
                        });
                    }, TaskScheduler.Default);
                }
                bodyStack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(11),
                    Background = ThemeHelper.Brush(leafSurface),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(12, 10, 12, 10),
                    Child = cacheSp,
                });

                (string Title, string Detail)[] items =
                [
                    (L10n.T("防剧透物理锁"), L10n.T("书房电台默认只读取你已翻阅到的章节与划线，未读后文在送往模型前由内核物理截断")),
                    (L10n.T("本机离线语音合成"), _library.LiveVoice.IsReady ? L10n.T("已安装 24kHz 离线自然人声，双人交替演绎零云端费用") : L10n.T("可在「设置 → 朗读」下载离线自然人声模型")),
                    (L10n.T("报刊栏零红点"), $"已订阅 {chCnt} 档节目 · 共 {issCnt} 期单集 · 已收 {clipCnt} 条原声书摘"),
                ];
                foreach (var (itTitle, itDetail) in items)
                {
                    var sp = new StackPanel { Spacing = 3 };
                    sp.Children.Add(new TextBlock { Text = itTitle, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                    sp.Children.Add(new TextBlock { Text = itDetail, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap, LineHeight = 17 });
                    bodyStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(11),
                        Background = ThemeHelper.Brush(leafSurface),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(12, 10, 12, 10),
                        Child = sp,
                    });
                }
                break;
            }
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = bodyStack,
        };
        Grid.SetRow(scroll, 2);
        outer.Children.Add(scroll);

        return new Border
        {
            CornerRadius = new CornerRadius(15),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(dark ? Color.FromArgb(32, 255, 255, 255) : p.Line),
            BorderThickness = new Thickness(0.5),
            Child = outer,
        };
    }

    private async Task RunOnDeviceTranscribeAsync(string issueId)
    {
        if (_transcribingIssueId == issueId) return;
        _transcribingIssueId = issueId;
        _transcribeStatus = L10n.T("正在准备单集音频…");
        _shelf.RefreshCurrentSubPage();
        try
        {
            int count = await _library.Podcasts.TranscribeIssueOnDeviceAsync(issueId, msg =>
            {
                _transcribeStatus = msg;
                _shelf.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    if (OpenIssueId == issueId || _drawerOpen)
                        _shelf.RefreshCurrentSubPage();
                });
            });
            if (_disposed) return;
            _transcribingIssueId = null;
            _transcribeStatus = "";
            OpenIssueId = issueId;
            _shelf.ShowToast($"转写完成：共生成 {count} 段带时间轴文稿", canUndo: false);
            _shelf.RefreshCurrentSubPage();
        }
        catch (Exception ex)
        {
            _transcribingIssueId = null;
            _transcribeStatus = "";
            _shelf.ShowToast($"转写失败：{ex.Message}", canUndo: false);
            _shelf.RefreshCurrentSubPage();
        }
    }

    // ---------- Station Cards & Un-nested Episode Rows ----------

    private FrameworkElement BuildStudioGroupCard(PodcastIssue[] studioIssues, Button brewBtn, YYPalette p, YYTintColors tint, FontFamily serif)
    {
        bool collapsed = _collapsedGroups.Contains("studio");
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(BuildGroupHeader("studio", L10n.T("书房电台"), L10n.T("由你的书架酿制"), brewBtn, collapsed, p, serif));

        if (!collapsed)
        {
            stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            if (studioIssues.Length > 0)
            {
                for (int i = 0; i < studioIssues.Length; i++)
                {
                    if (i > 0) stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
                    stack.Children.Add(BuildEpisodeRow(studioIssues[i], showProgress: studioIssues[i].PositionSec > 0, p, tint));
                }
            }
            else
            {
                var readingBook = _library.GetKeptBooks().FirstOrDefault(b => b.Status == ReadingStatus.Reading && b.Format != BookFormat.Audio)
                    ?? _library.GetKeptBooks().FirstOrDefault(b => b.Format != BookFormat.Audio);
                (StudioKind Kind, string Title, string Tag, string Meta)[] starters =
                [
                    (StudioKind.WeeklyDigest, readingBook is not null ? $"本周书房周刊：重读《{readingBook.Title}》与近期划线" : L10n.T("本周书房周刊：串联本周阅读与夜间划线"), L10n.T("不剧透锁"), L10n.T("10 分 · 本机酿制")),
                    (StudioKind.Roundtable, L10n.T("跨书圆桌：挑两三本在读书同桌对谈"), L10n.T("跨书对谈"), L10n.T("15 分 · 防剧透")),
                    (StudioKind.BookGuide, readingBook is not null ? $"难书导读：《{readingBook.Title}》直觉热身精讲" : L10n.T("难书与论文导读：晦涩章节直觉精讲"), L10n.T("读前热身"), L10n.T("8 分 · 附原文锚点")),
                    (StudioKind.VocabRadio, L10n.T("生词伴听 · 通勤外语台（融入本周生词）"), L10n.T("生词本联动"), L10n.T("12 分 · 语速 0.9×")),
                ];
                for (int i = 0; i < starters.Length; i++)
                {
                    if (i > 0) stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
                    var (kind, title, tag, meta) = starters[i];
                    var r = new Grid { ColumnSpacing = 10, Padding = new Thickness(14, 10, 14, 10) };
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    r.Children.Add(YYIcons.Make("play", 9.5, ThemeHelper.Brush(p.T3), filled: true));
                    var badge = BuildShowMark(L10n.T("书房电台"), null, isStudio: true, 22, p, tint);
                    Grid.SetColumn(badge, 1);
                    r.Children.Add(badge);

                    var m = new StackPanel { Spacing = 2 };
                    m.Children.Add(new TextBlock { Text = title, FontSize = 13.5, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    var subTb = new TextBlock { FontSize = 11.5, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
                    subTb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = tag, Foreground = ThemeHelper.Brush(tint.Accent) });
                    subTb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $" · {meta}", Foreground = ThemeHelper.Brush(p.T3) });
                    m.Children.Add(subTb);
                    Grid.SetColumn(m, 2);
                    r.Children.Add(m);

                    var trail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.35 };
                    trail.Children.Add(YYIcons.Make("doc-lines", 12, ThemeHelper.Brush(p.T3)));
                    trail.Children.Add(YYIcons.Make("bookmark", 12, ThemeHelper.Brush(p.T3)));
                    Grid.SetColumn(trail, 3);
                    r.Children.Add(trail);

                    var btn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(0),
                        UseSystemFocusVisuals = false,
                        Content = r,
                    };
                    var sk = kind;
                    string? preBookId = (sk is StudioKind.BookGuide or StudioKind.WeeklyDigest) ? readingBook?.Id : null;
                    btn.Click += async (_, _) => await ShowStudioSheetAsync(sk, preBookId);
                    stack.Children.Add(btn);
                }
            }
        }

        return new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = stack,
        };
    }

    private FrameworkElement BuildStationGroupCard(
        string groupKey,
        string title,
        string sub,
        Button? actionPill,
        PodcastIssue[] issues,
        bool isPlayingGroup,
        PodcastChannel? loadMoreChannel,
        YYPalette p,
        YYTintColors tint,
        FontFamily serif)
    {
        bool collapsed = _collapsedGroups.Contains(groupKey);
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(BuildGroupHeader(groupKey, title, sub, actionPill, collapsed, p, serif));

        if (!collapsed && issues.Length > 0)
        {
            stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            for (int i = 0; i < issues.Length; i++)
            {
                bool cur = _playingIssueId == issues[i].Id;
                bool prevCur = i > 0 && _playingIssueId == issues[i - 1].Id;
                if (i > 0 && !cur && !prevCur)
                    stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
                stack.Children.Add(BuildEpisodeRow(issues[i], showProgress: isPlayingGroup || issues[i].PositionSec > 0, p, tint));
            }
            if (loadMoreChannel is not null && !_exhaustedChannelIds.Contains(loadMoreChannel.Id))
            {
                bool loading = _loadingMoreChannelIds.Contains(loadMoreChannel.Id);
                stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
                var moreBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0, 10, 0, 10),
                    IsEnabled = !loading,
                    UseSystemFocusVisuals = false,
                    Content = new TextBlock
                    {
                        Text = loading ? L10n.T("正在加载往期…") : L10n.T("加载更多往期"),
                        FontSize = 12,
                        FontWeight = FontWeights.Medium,
                        Foreground = ThemeHelper.Brush(loading ? p.T3 : tint.Accent),
                    },
                };
                var chCapture = loadMoreChannel;
                moreBtn.Click += async (_, _) =>
                {
                    if (!_loadingMoreChannelIds.Add(chCapture.Id)) return;
                    _shelf.RefreshCurrentSubPage();
                    try
                    {
                        var (added, exhausted) = await _library.Podcasts.LoadMoreEpisodesAsync(chCapture);
                        if (exhausted) _exhaustedChannelIds.Add(chCapture.Id);
                        _shelf.ShowToast(added > 0 ? $"已加载 {added} 期往期节目" : L10n.T("已加载该节目全部往期"));
                    }
                    catch (Exception ex)
                    {
                        _shelf.ShowToast($"加载往期失败：{ex.Message}");
                    }
                    finally
                    {
                        _loadingMoreChannelIds.Remove(chCapture.Id);
                        _shelf.RefreshCurrentSubPage();
                    }
                };
                stack.Children.Add(moreBtn);
            }
        }

        return new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = stack,
        };
    }

    private FrameworkElement BuildGroupHeader(string groupKey, string title, string sub, Button? rightBtn, bool collapsed, YYPalette p, FontFamily serif)
    {
        var g = new Grid { Padding = new Thickness(16, 12, 12, 10), ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftGrid = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        leftGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leftGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        leftGrid.Children.Add(new TextBlock { Text = title, FontFamily = serif, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1 });
        var subHeaderTb = new TextBlock { Text = sub, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(subHeaderTb, 1);
        leftGrid.Children.Add(subHeaderTb);
        var titleToggleBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = leftGrid,
        };
        string gk = groupKey;
        titleToggleBtn.Click += (_, _) =>
        {
            if (!_collapsedGroups.Remove(gk)) _collapsedGroups.Add(gk);
            _shelf.RefreshCurrentSubPage();
        };
        g.Children.Add(titleToggleBtn);

        var rightSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (rightBtn is not null) rightSp.Children.Add(rightBtn);
        var chevBtn = new Button
        {
            Width = 26,
            Height = 24,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = YYIcons.Make(collapsed ? "chevron-right" : "chevron-down", 10.5, ThemeHelper.Brush(p.T3)),
        };
        chevBtn.Click += (_, _) =>
        {
            if (!_collapsedGroups.Remove(gk)) _collapsedGroups.Add(gk);
            _shelf.RefreshCurrentSubPage();
        };
        rightSp.Children.Add(chevBtn);
        Grid.SetColumn(rightSp, 1);
        g.Children.Add(rightSp);
        return g;
    }

    private void ToggleDeskIssuePlayback(PodcastIssue issue, bool forcePlay = false)
    {
        bool isPlayingThis = _playing && _playingIssueId == issue.Id;
        if (isPlayingThis && !forcePlay)
        {
            StopPlayback();
            _shelf.RefreshCurrentSubPage();
            return;
        }
        var sents = LoadOrSynthesizeTranscript(issue);
        int startIdx = sents.Length > 0 ? Math.Max(0, Array.FindLastIndex(sents, s => s.T0 <= issue.PositionSec + 1.0)) : 0;
        _track = SupportsTrackSwitch(issue, sents) ? issue.Track : PodcastTrack.Original;
        PlayIssueAtTurn(issue, sents, startIdx);
        _shelf.RefreshCurrentSubPage();
    }

    /// <summary>
    /// Un-nested 4-column episode row (<c>MacPodcastView.swift:702-836</c>):
    /// - Left play circle <see cref="Button"/> toggles inline desk playback (with live equalizer bars)
    /// - Center title/meta <see cref="Button"/> opens the inline episode reader
    /// - Right doc-lines &amp; bookmark <see cref="Button"/>s act independently with zero illegal nested buttons!
    /// </summary>
    private FrameworkElement BuildEpisodeRow(PodcastIssue issue, bool showProgress, YYPalette p, YYTintColors tint)
    {
        bool isCurrent = _playingIssueId == issue.Id;
        bool isPlayingThis = _playing && isCurrent;
        string iid = issue.Id;
        bool inLater = issue.Placement == PodcastPlacement.Archive;

        double dur = Math.Max(1.0, issue.DurationSec);
        double pos = isCurrent && _usingExternalMedia
            ? _mediaPlayer.PlaybackSession.Position.TotalSeconds
            : issue.PositionSec;

        var r = new Grid { ColumnSpacing = 9, Padding = new Thickness(10, 8, 10, 8) };
        r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 1. Play / Equalizer toggle button
        UIElement playVisual = isPlayingThis
            ? BuildEqualizerBars(tint.Accent)
            : YYIcons.Make("play", 9.5, ThemeHelper.Brush(isCurrent ? tint.Accent : p.T3), filled: true);
        var playBtn = new Button
        {
            Width = 24,
            Height = 26,
            MinWidth = 24,
            MinHeight = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = playVisual,
        };
        ToolTipService.SetToolTip(playBtn, isPlayingThis ? L10n.T("暂停") : L10n.T("在桌面直接收听"));
        playBtn.Click += (_, _) => ToggleDeskIssuePlayback(issue);
        r.Children.Add(playBtn);

        // 2. Show artwork / monogram mark
        var badge = BuildShowMark(issue.StudioKind is not null ? L10n.T("书房电台") : issue.ShowTitle, issue.ArtworkUrl, issue.StudioKind is not null, 22, p, tint);
        Grid.SetColumn(badge, 1);
        r.Children.Add(badge);

        // 3. Center title + metadata + mini progress bar button (opens episode reader)
        var mid = new StackPanel { Spacing = 2 };
        mid.Children.Add(new TextBlock
        {
            Text = issue.Title,
            FontSize = 13.5,
            FontWeight = isCurrent ? FontWeights.Medium : FontWeights.Normal,
            Foreground = ThemeHelper.Brush(isCurrent ? tint.Accent : p.T1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        string noteText = issue.StudioKind is StudioKind sk
            ? $"{StudioKindTitle(sk)} · 防剧透"
            : ((issue.Language ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase) ? TrackLabel(issue.Track) : L10n.T("中文原声"));

        int durMin = Math.Max(1, (int)Math.Round(dur / 60.0));
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(issue.PublishedAt > 0 ? issue.PublishedAt : issue.AddedAt).LocalDateTime;
        string tailMeta;
        if (showProgress && dur > 0)
        {
            int remMin = Math.Max(1, (int)Math.Ceiling(Math.Max(0, dur - pos) / 60.0));
            tailMeta = pos >= dur - 5 ? L10n.T("已听完") : $"还剩 {remMin} 分";
        }
        else
        {
            string fidBadge = issue.Fidelity == PodcastFidelity.L2Magazine ? " · 逐字稿" : "";
            tailMeta = $"{durMin} 分钟 · {dt.Month}月{dt.Day}日{fidBadge}";
        }

        var metaTb = new TextBlock { FontSize = 11.5, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        if (showProgress && !string.IsNullOrWhiteSpace(issue.ShowTitle))
        {
            metaTb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $"{issue.ShowTitle} · ", Foreground = ThemeHelper.Brush(p.T3) });
        }
        metaTb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = noteText, Foreground = ThemeHelper.Brush(tint.Accent) });
        metaTb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = $" · {tailMeta}", Foreground = ThemeHelper.Brush(p.T3) });
        mid.Children.Add(metaTb);

        if (showProgress && dur > 0)
        {
            double frac = Math.Clamp(pos / dur, 0.04, 1.0);
            var miniProg = new Grid
            {
                Width = 108,
                Height = 2.5,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(1.25),
                Background = ThemeHelper.Brush(p.Inset),
                Margin = new Thickness(0, 3, 0, 0),
            };
            miniProg.Children.Add(new Border
            {
                Width = 108 * frac,
                Height = 2.5,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(1.25),
                Background = ThemeHelper.Brush(tint.Accent),
            });
            mid.Children.Add(miniProg);
        }

        var openBodyBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 2, 2, 2),
            UseSystemFocusVisuals = false,
            Content = mid,
        };
        openBodyBtn.Click += (_, _) =>
        {
            OpenIssueAt(iid);
            _shelf.RefreshCurrentSubPage();
        };
        Grid.SetColumn(openBodyBtn, 2);
        r.Children.Add(openBodyBtn);

        // 4. Independent trailing glyph buttons (Transcript + Bookmark)
        var trailActs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = isCurrent || inLater ? 1.0 : 0.42,
        };
        var openDocBtn = new Button
        {
            Width = 26,
            Height = 26,
            MinWidth = 26,
            MinHeight = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = YYIcons.Make("doc-lines", 12, ThemeHelper.Brush(p.T3)),
        };
        ToolTipService.SetToolTip(openDocBtn, L10n.T("展开访谈书页"));
        openDocBtn.Click += (_, _) =>
        {
            OpenIssueAt(iid);
            _shelf.RefreshCurrentSubPage();
        };
        trailActs.Children.Add(openDocBtn);

        var bmBtn = new Button
        {
            Width = 26,
            Height = 26,
            MinWidth = 26,
            MinHeight = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = YYIcons.Make("bookmark", 12, ThemeHelper.Brush(inLater ? tint.Accent : p.T3), filled: inLater),
        };
        ToolTipService.SetToolTip(bmBtn, inLater ? L10n.T("已在稍后听（点击移回在听）") : L10n.T("存入稍后听"));
        bmBtn.Click += (_, _) =>
        {
            try
            {
                _library.Core.PodcastSetPlacement(iid, inLater ? PodcastPlacement.Desk : PodcastPlacement.Archive);
                _shelf.ShowToast(inLater ? L10n.T("已移回在听书桌") : L10n.T("已存入稍后听"), canUndo: false);
                _shelf.RefreshBooks();
            }
            catch { }
        };
        trailActs.Children.Add(bmBtn);
        Grid.SetColumn(trailActs, 3);
        r.Children.Add(trailActs);

        var ctx = new MenuFlyout();
        var openMi = new MenuFlyoutItem { Text = L10n.T("展开访谈书页") };
        openMi.Click += (_, _) => { OpenIssueAt(iid); _shelf.RefreshCurrentSubPage(); };
        var playMi = new MenuFlyoutItem { Text = isPlayingThis ? L10n.T("暂停") : L10n.T("播放") };
        playMi.Click += (_, _) => ToggleDeskIssuePlayback(issue);
        var laterMi = new MenuFlyoutItem { Text = inLater ? L10n.T("移回在听书桌") : L10n.T("存入稍后听") };
        laterMi.Click += (_, _) =>
        {
            try { _library.Core.PodcastSetPlacement(iid, inLater ? PodcastPlacement.Desk : PodcastPlacement.Archive); _shelf.RefreshBooks(); } catch { }
        };
        var del = new MenuFlyoutItem { Text = L10n.T("删除单集"), Foreground = ThemeHelper.Brush(p.Danger) };
        del.Click += (_, _) => { try { _library.Core.PodcastDeleteIssue(iid); _shelf.RefreshBooks(); } catch { } };
        ctx.Items.Add(openMi);
        ctx.Items.Add(playMi);
        ctx.Items.Add(laterMi);
        ctx.Items.Add(new MenuFlyoutSeparator());
        ctx.Items.Add(del);

        var rowWrap = new Border
        {
            Margin = new Thickness(4, 2, 4, 2),
            CornerRadius = new CornerRadius(9),
            Background = isCurrent ? ThemeHelper.Brush(tint.Soft) : new SolidColorBrush(Colors.Transparent),
            ContextFlyout = ctx,
            Child = r,
        };
        rowWrap.PointerEntered += (_, _) =>
        {
            trailActs.Opacity = 1.0;
            if (!isCurrent) rowWrap.Background = ThemeHelper.Brush(Color.FromArgb(140, p.Inset.R, p.Inset.G, p.Inset.B));
        };
        rowWrap.PointerExited += (_, _) =>
        {
            trailActs.Opacity = isCurrent || inLater ? 1.0 : 0.42;
            rowWrap.Background = isCurrent ? ThemeHelper.Brush(tint.Soft) : new SolidColorBrush(Colors.Transparent);
        };
        return rowWrap;
    }

    private FrameworkElement BuildEqualizerBars(Color accent)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 1.5,
            Height = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        double[] initialHeights = [_eqPulseToggle ? 9 : 4, _eqPulseToggle ? 5 : 10, _eqPulseToggle ? 8 : 3.5];
        var bars = new Border[3];
        for (int i = 0; i < 3; i++)
        {
            bars[i] = new Border
            {
                Width = 2,
                Height = initialHeights[i],
                CornerRadius = new CornerRadius(1),
                Background = ThemeHelper.Brush(accent),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            sp.Children.Add(bars[i]);
        }
        _liveEqBarSets.Add(bars);
        SyncEqTimer();
        return sp;
    }

    private FrameworkElement BuildDiscoveryCard(YYPalette p, YYTintColors tint, FontFamily serif)
    {
        bool collapsed = _collapsedGroups.Contains("discovery");
        var subscribedFeeds = _library.Podcasts.GetChannels().Select(c => c.FeedUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stack = new StackPanel { Spacing = 0 };
        var moreBtn = MakePaperPill(L10n.T("搜更多"), accent: false, p, tint);
        moreBtn.Click += async (_, _) => await ShowAddPodcastSheetAsync();
        stack.Children.Add(BuildGroupHeader("discovery", L10n.T("精选播客"), L10n.T("公开目录 · 一键订阅"), moreBtn, collapsed, p, serif));

        if (!collapsed)
        {
            stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            var picks = (_discoveredShows is { Count: > 0 } ? _discoveredShows :
            [
                new PodcastSearchHitDto("storyfm", "故事FM", "爱哲", "https://storyfm.cn/feed/episodes/", null, "社会与文化"),
                new PodcastSearchHitDto("leftright", "忽左忽右", "JustPod", "https://feeds.fireside.fm/left-right/rss", null, "历史与文化"),
                new PodcastSearchHitDto("lex", "Lex Fridman Podcast", "Lex Fridman", "https://lexfridman.com/feed/podcast/", null, "科技与哲学"),
                new PodcastSearchHitDto("dwarkesh", "Dwarkesh Podcast", "Dwarkesh Patel", "https://apple.dwarkesh-patel.com/feed.xml", null, "深度访谈"),
            ]).Take(4).ToArray();

            if (_loadingDiscovery && _discoveredShows is null)
            {
                var loadSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(16, 12, 16, 12) };
                loadSp.Children.Add(new ProgressRing { Width = 14, Height = 14, IsActive = true });
                loadSp.Children.Add(new TextBlock { Text = L10n.T("正在连接公开播客目录…"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
                stack.Children.Add(loadSp);
            }
            else
            {
                for (int i = 0; i < picks.Length; i++)
                {
                    if (i > 0) stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
                    var hit = picks[i];
                    bool isSub = subscribedFeeds.Contains(hit.FeedUrl);
                    bool busy = _subscribingFeedUrls.Contains(hit.FeedUrl);

                    var r = new Grid { ColumnSpacing = 11, Padding = new Thickness(16, 10, 14, 10) };
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    r.Children.Add(BuildShowMark(hit.Title, hit.ArtworkUrl, isStudio: false, 26, p, tint));

                    var m = new StackPanel { Spacing = 1.5, VerticalAlignment = VerticalAlignment.Center };
                    m.Children.Add(new TextBlock { Text = hit.Title, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    string desc = string.Join(" · ", new[] { hit.Author, hit.Genre }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    m.Children.Add(new TextBlock { Text = desc, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    Grid.SetColumn(m, 1);
                    r.Children.Add(m);

                    if (isSub)
                    {
                        var subLabel = new TextBlock { Text = L10n.T("已订阅"), FontSize = 11, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
                        Grid.SetColumn(subLabel, 2);
                        r.Children.Add(subLabel);
                    }
                    else
                    {
                        var subBtn = MakePaperPill(busy ? L10n.T("订阅中…") : L10n.T("订阅"), accent: true, p, tint);
                        subBtn.Height = 24;
                        subBtn.IsEnabled = !busy;
                        string feedUrl = hit.FeedUrl;
                        string showName = hit.Title;
                        subBtn.Click += async (_, _) =>
                        {
                            _subscribingFeedUrls.Add(feedUrl);
                            subBtn.IsEnabled = false;
                            subBtn.Content = L10n.T("订阅中…");
                            try
                            {
                                await _library.Podcasts.SubscribeRssAsync(feedUrl);
                                _subscribingFeedUrls.Remove(feedUrl);
                                _shelf.RefreshBooks();
                                _shelf.ShowToast($"已订阅《{showName}》", canUndo: false);
                            }
                            catch (Exception ex)
                            {
                                _subscribingFeedUrls.Remove(feedUrl);
                                subBtn.IsEnabled = true;
                                subBtn.Content = L10n.T("订阅");
                                _shelf.ShowToast($"订阅失败：{ex.Message}", canUndo: false);
                            }
                        };
                        Grid.SetColumn(subBtn, 2);
                        r.Children.Add(subBtn);
                    }
                    stack.Children.Add(r);
                }
            }
        }

        return new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = stack,
        };
    }

    // ---------- Dynamic Tabletop Player Card (MacPodcastView.swift:1396-1774) ----------

    private Border BuildTabletopPlayerCard(PodcastIssue? active, YYPalette p, YYTintColors tint, FontFamily serif, Color leafSurface)
    {
        bool dark = ThemeHelper.IsDark((_shelf.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Light);
        var outer = new StackPanel { Spacing = 0 };
        var upper = new StackPanel { Spacing = 11, Padding = new Thickness(16, 15, 16, 13) };

        bool isStudio = active is null || active.StudioKind is not null;
        bool isPlayingActive = active is not null && _playing && _playingIssueId == active.Id;
        var sentsForActive = active is not null
            ? (_playingIssueId == active.Id && _playingSentences.Length > 0 ? _playingSentences : LoadOrSynthesizeTranscript(active))
            : [];
        bool canSwitchTrack = active is not null && SupportsTrackSwitch(active, sentsForActive);

        var readingBook = _library.GetKeptBooks().FirstOrDefault(b => b.Status == ReadingStatus.Reading && b.Format != BookFormat.Audio)
            ?? _library.GetKeptBooks().FirstOrDefault(b => b.Format != BookFormat.Audio);

        var topRow = new Grid { ColumnSpacing = 11 };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        topRow.Children.Add(BuildShowMark(
            active is not null ? (isStudio ? L10n.T("书房电台") : active.ShowTitle) : L10n.T("书房电台"),
            active?.ArtworkUrl,
            isStudio,
            32,
            p,
            tint));

        double maxDur = Math.Max(1.0, active?.DurationSec ?? 480.0);
        double curPos = active is not null
            ? (isPlayingActive && _usingExternalMedia
                ? _mediaPlayer.PlaybackSession.Position.TotalSeconds
                : (isPlayingActive && _playingTurnIdx < sentsForActive.Length ? sentsForActive[_playingTurnIdx].T0 : active.PositionSec))
            : 0.0;

        string statusSub;
        if (active is not null)
        {
            string st = isPlayingActive ? L10n.T("正在播放") : L10n.T("暂停中");
            int remMin = Math.Max(1, (int)Math.Ceiling(Math.Max(0, maxDur - curPos) / 60.0));
            string rem = curPos >= maxDur - 5 ? L10n.T("已听完") : $"还剩 {remMin} 分";
            statusSub = string.Join(" · ", new[] { active.ShowTitle, st, rem }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        else if (readingBook is not null)
        {
            int pct = (int)Math.Round(readingBook.Progress * 100);
            statusSub = pct > 0 ? $"书房电台 · 已读 {pct}% · 绝不剧透" : L10n.T("书房电台 · 读前 8 分钟热身");
        }
        else
        {
            statusSub = L10n.T("书房电台 · 本机整理 · 防剧透");
        }

        var meta = new StackPanel { Spacing = 1.5, VerticalAlignment = VerticalAlignment.Center };
        meta.Children.Add(new TextBlock
        {
            Text = active?.Title ?? (readingBook is not null ? $"《{readingBook.Title}》· 导读待酿制" : L10n.T("书房电台 · 准备就绪")),
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(p.T1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var metaSubTb = new TextBlock
        {
            Text = statusSub,
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        meta.Children.Add(metaSubTb);
        Grid.SetColumn(meta, 1);
        topRow.Children.Add(meta);

        var actBtn = MakePaperPill(active is not null ? L10n.T("展开书页") : L10n.T("酿一期"), accent: true, p, tint);
        actBtn.Click += async (_, _) =>
        {
            if (active is not null)
            {
                OpenIssueAt(active.Id);
                _shelf.RefreshCurrentSubPage();
            }
            else
            {
                await ShowStudioSheetAsync(readingBook is not null ? StudioKind.BookGuide : StudioKind.WeeklyDigest, readingBook?.Id);
            }
        };
        Grid.SetColumn(actBtn, 2);
        topRow.Children.Add(actBtn);
        upper.Children.Add(topRow);

        // Dynamic live sentence / summary preview (NEVER hardcodes "林深 · 书房主播" or "防剧透锁" on external RSS!)
        MagazineSentence? curSent = null;
        if (active is not null && sentsForActive.Length > 0)
        {
            int idx = Math.Max(0, Array.FindLastIndex(sentsForActive, s => curPos >= s.T0));
            curSent = sentsForActive[Math.Clamp(idx, 0, sentsForActive.Length - 1)];
        }

        string speakerLabel = active is null
            ? L10n.T("林深 · 书房主播")
            : (!string.IsNullOrWhiteSpace(curSent?.Speaker)
                ? curSent.Speaker
                : (active.StudioKind is not null ? L10n.T("林深 · 书房主播") : active.ShowTitle));

        string rightBadgeLabel = active is null || active.StudioKind is not null
            ? L10n.T("本地声卡 · 不剧透锁")
            : TrackLabel(active.Track, active);

        string excerpt;
        if (active is not null)
        {
            if (curSent is not null)
            {
                excerpt = active.Track == PodcastTrack.Dub && !string.IsNullOrWhiteSpace(curSent.TextTrans)
                    ? curSent.TextTrans!
                    : curSent.TextOrig;
            }
            else if (!string.IsNullOrWhiteSpace(active.Summary))
            {
                excerpt = active.Summary;
            }
            else
            {
                excerpt = L10n.T("点击展开访谈书页，查看文稿、章节或一键本机转文字。");
            }
        }
        else
        {
            excerpt = readingBook is not null
                ? $"点右上角「酿一期」或下方播放键，把《{readingBook.Title}》已读章节与划线酿成一期私人电台；或在上方粘贴任意单集链接直听。"
                : L10n.T("在上方粘贴单集链接、搜索公开播客目录，或点「酿一期」把你的书架酿成不剧透的私人电台。");
        }

        var quoteBox = new StackPanel { Spacing = 5 };
        var qHead = new Grid();
        var spkTb = new TextBlock { Text = speakerLabel, FontSize = 11, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(tint.Accent), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        qHead.Children.Add(spkTb);
        qHead.Children.Add(new TextBlock { Text = rightBadgeLabel, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
        quoteBox.Children.Add(qHead);
        var excerptTb = new TextBlock
        {
            Text = excerpt,
            FontFamily = serif,
            FontSize = 13.5,
            LineHeight = 20,
            Foreground = ThemeHelper.Brush(p.T1),
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
        };
        quoteBox.Children.Add(excerptTb);

        var quoteLeafBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(13, 10, 13, 10),
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(leafSurface),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            UseSystemFocusVisuals = false,
            Content = quoteBox,
        };
        quoteLeafBtn.Click += async (_, _) =>
        {
            if (active is not null)
            {
                OpenIssueAt(active.Id);
                _shelf.RefreshCurrentSubPage();
            }
            else
            {
                await ShowStudioSheetAsync(readingBook is not null ? StudioKind.BookGuide : StudioKind.WeeklyDigest, readingBook?.Id);
            }
        };
        upper.Children.Add(quoteLeafBtn);

        // Interactive click/drag seek scrubber + live clock labels
        var progStack = new StackPanel { Spacing = 3, Margin = new Thickness(0, 1, 0, 0) };
        var scrubHit = new Grid
        {
            Height = 14,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        var trackBg = new Border
        {
            Height = 2.5,
            CornerRadius = new CornerRadius(1.25),
            Background = ThemeHelper.Brush(p.Inset),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var trackFill = new Border
        {
            Height = 2.5,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(1.25),
            Background = ThemeHelper.Brush(active is not null ? p.T1 : Color.FromArgb(64, p.T1.R, p.T1.G, p.T1.B)),
        };
        scrubHit.Children.Add(trackBg);
        scrubHit.Children.Add(trackFill);

        var posClockTb = new TextBlock { Text = FormatClock(curPos), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) };
        var durClockTb = new TextBlock { Text = FormatClock(maxDur), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right };

        void DrawDeskScrub(double sec)
        {
            double w = scrubHit.ActualWidth > 4 ? scrubHit.ActualWidth : 340;
            double ratio = active is not null ? Math.Clamp(sec / maxDur, 0, 1) : 0.07;
            trackFill.Width = Math.Max(3, w * ratio);
            posClockTb.Text = FormatClock(sec);
            durClockTb.Text = FormatClock(maxDur);
        }

        scrubHit.SizeChanged += (_, _) => DrawDeskScrub(curPos);
        if (active is not null)
        {
            bool draggingDesk = false;
            void SeekFromX(double x, bool commit)
            {
                double w = Math.Max(1.0, scrubHit.ActualWidth);
                double target = Math.Clamp(x / w, 0, 1) * maxDur;
                DrawDeskScrub(target);
                if (commit)
                {
                    curPos = target;
                    SeekIssueToSeconds(active, sentsForActive, target);
                }
            }
            scrubHit.PointerPressed += (_, pe) =>
            {
                draggingDesk = true;
                scrubHit.CapturePointer(pe.Pointer);
                SeekFromX(pe.GetCurrentPoint(scrubHit).Position.X, commit: false);
            };
            scrubHit.PointerMoved += (_, pe) =>
            {
                if (!draggingDesk) return;
                SeekFromX(pe.GetCurrentPoint(scrubHit).Position.X, commit: false);
            };
            scrubHit.PointerReleased += (_, pe) =>
            {
                if (!draggingDesk) return;
                draggingDesk = false;
                scrubHit.ReleasePointerCapture(pe.Pointer);
                SeekFromX(pe.GetCurrentPoint(scrubHit).Position.X, commit: true);
            };
        }
        progStack.Children.Add(scrubHit);

        var timeRow = new Grid();
        timeRow.Children.Add(posClockTb);
        timeRow.Children.Add(durClockTb);
        progStack.Children.Add(timeRow);
        upper.Children.Add(progStack);

        outer.Children.Add(upper);
        outer.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });

        // Footer bar on leafSurface: Transport + Track segments (or dynamic note) + 划线 [H]
        var footGrid = new Grid { Padding = new Thickness(13, 9, 13, 9), Background = ThemeHelper.Brush(leafSurface), CornerRadius = new CornerRadius(0, 0, 14, 14), ColumnSpacing = 4 };
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var back15 = MakeCompactTextButton("-15s", p);
        back15.Click += (_, _) =>
        {
            if (active is not null) StepPlayback(active, sentsForActive, forward: false, deltaSec: 15);
            else _shelf.ShowToast(L10n.T("先酿一期或订阅节目即可收听"), canUndo: false);
        };
        controls.Children.Add(back15);

        var playIconHost = new ContentPresenter { Content = YYIcons.Make(isPlayingActive ? "pause" : "play", 10.5, ThemeHelper.Brush(p.Card), filled: true) };
        var playBtn = new Button
        {
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            Background = ThemeHelper.Brush(p.T1),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Margin = new Thickness(2, 0, 2, 0),
            Content = playIconHost,
        };
        playBtn.Click += async (_, _) =>
        {
            if (active is not null)
            {
                ToggleDeskIssuePlayback(active);
            }
            else
            {
                await ShowStudioSheetAsync(readingBook is not null ? StudioKind.BookGuide : StudioKind.WeeklyDigest, readingBook?.Id);
            }
        };
        controls.Children.Add(playBtn);

        var fwd15 = MakeCompactTextButton("+15s", p);
        fwd15.Click += (_, _) =>
        {
            if (active is not null) StepPlayback(active, sentsForActive, forward: true, deltaSec: 15);
            else _shelf.ShowToast(L10n.T("先酿一期或订阅节目即可收听"), canUndo: false);
        };
        controls.Children.Add(fwd15);

        var miniRateBtn = MakeCompactTextButton($"{_rate:0.0}×", p);
        miniRateBtn.Click += (_, _) =>
        {
            CyclePlaybackRate();
            miniRateBtn.Content = $"{_rate:0.0}×";
            _shelf.ShowToast($"倍速 {_rate:0.0}×", canUndo: false);
        };
        controls.Children.Add(miniRateBtn);
        footGrid.Children.Add(controls);

        var midSlot = new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        if (active is not null && canSwitchTrack)
        {
            midSlot.Child = BuildTrackSegmentedControl(active.Track, compact: true, p, tint, nextTr =>
            {
                SwitchTrack(active.Id, active, nextTr);
            });
        }
        else
        {
            string dynamicFootNote = active is null
                ? L10n.T("防剧透锁 · 仅基于已读进度")
                : (active.StudioKind is StudioKind sk2
                    ? $"{StudioKindTitle(sk2)} · 不剧透"
                    : L10n.T("原声 · 书摘就绪"));
            midSlot.Child = new TextBlock
            {
                Text = dynamicFootNote,
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                Foreground = ThemeHelper.Brush(tint.Accent),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
        }
        Grid.SetColumn(midSlot, 1);
        footGrid.Children.Add(midSlot);

        var clipWrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        clipWrap.Children.Add(new TextBlock { Text = L10n.T("划线"), FontSize = 11, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        clipWrap.Children.Add(YYChrome.MakeKeycap("H", dark));
        var clipQuickBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 2, 2),
            UseSystemFocusVisuals = false,
            Content = clipWrap,
        };
        clipQuickBtn.Click += async (_, _) =>
        {
            if (active is not null) await ClipCurrentTurnAsync(active.Id);
            else OpenDrawer("clips");
        };
        Grid.SetColumn(clipQuickBtn, 2);
        footGrid.Children.Add(clipQuickBtn);

        outer.Children.Add(footGrid);

        if (active is not null)
        {
            _renderedDeskIssueId = active.Id;
            _deskPlayIconHost = playIconHost;
            _deskStatusMetaTb = metaSubTb;
            _deskSpeakerTb = spkTb;
            _deskExcerptTb = excerptTb;
            _deskClockPosTb = posClockTb;
            _deskClockDurTb = durClockTb;
            _redrawDeskScrubber = DrawDeskScrub;
        }

        return new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = outer,
        };
    }

    // ---------- 3. 内联「访谈书页」阅读器（PodcastReaderView.swift 1:1） ----------

    /// <summary>
    /// Loads real persisted transcript sentences for <paramref name="issue"/>.
    /// NEVER splits <see cref="PodcastIssue.Shownotes"/> into fake 12-second sentences!
    /// </summary>
    private MagazineSentence[] LoadOrSynthesizeTranscript(PodcastIssue issue)
    {
        try { return _library.Core.PodcastTranscript(issue.Id); }
        catch { return []; }
    }

    private FrameworkElement BuildInlineEpisodeReader(PodcastIssue issue, bool dark)
    {
        _readerDarkTheme = dark;
        _renderedReaderIssue = issue;
        _turnCardElements.Clear();
        _turnVisualByIndex.Clear();

        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        var geoSerif = new FontFamily(YYFontFamily.LatinSerif.WindowsFamily());

        var sentences = _playingIssueId == issue.Id && _playingSentences.Length > 0
            ? _playingSentences
            : LoadOrSynthesizeTranscript(issue);
        _readerSentences = sentences;
        if (_playingIssueId == issue.Id)
            _readerTurnIdx = sentences.Length > 0 ? Math.Clamp(_playingTurnIdx, 0, sentences.Length - 1) : 0;
        else if (sentences.Length > 0)
            _readerTurnIdx = Math.Clamp(_readerTurnIdx, 0, sentences.Length - 1);
        else
            _readerTurnIdx = 0;

        HashSet<string> highlightedIds;
        try { highlightedIds = _library.Core.PodcastHighlightSentenceIds(issue.Id).ToHashSet(StringComparer.Ordinal); }
        catch { highlightedIds = []; }

        PodcastInterrupt[] interrupts;
        try { interrupts = _library.Core.PodcastInterrupts(issue.Id); }
        catch { interrupts = []; }

        var unknownWords = _library.GetWords().Where(w => !w.Known).Select(w => w.Word).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sheet = new StackPanel
        {
            MaxWidth = 650,
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 136),
            IsTabStop = true,
            UseSystemFocusVisuals = false,
        };

        // 1. Kicker: ShowMark + ShowTitle · Duration
        var kicker = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        kicker.Children.Add(BuildShowMark(issue.StudioKind is not null ? L10n.T("书房电台") : issue.ShowTitle, issue.ArtworkUrl, issue.StudioKind is not null, 19, p, tint));
        int mins = Math.Max(1, (int)Math.Round(issue.DurationSec / 60.0));
        kicker.Children.Add(new TextBlock
        {
            Text = $"{issue.ShowTitle} · {mins} 分钟",
            FontSize = 12,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
        });
        sheet.Children.Add(kicker);

        // 2. 25pt Songti serif title
        sheet.Children.Add(new TextBlock
        {
            Text = issue.Title,
            FontFamily = serif,
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 35,
        });

        // 3. Meta bar + 0.5pt hairline (PodcastReaderView.swift:597-619)
        bool canDub = SupportsTrackSwitch(issue, sentences);
        string metaLeft = canDub
            ? $"当前声轨：{TrackLabel(_track, issue)}"
            : (issue.StudioKind is StudioKind sk ? $"{StudioKindTitle(sk)} · 防剧透物理锁" : L10n.T("原声收听"));
        string metaRight = canDub
            ? L10n.T("点任意句子即刻跳播，点高亮词查词收进生词本")
            : L10n.T("点任意句子即刻跳播，点书名签核对原书出处");
        sheet.Children.Add(new TextBlock
        {
            Text = $"{metaLeft}  ·  {metaRight}",
            FontSize = 12,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
        });
        sheet.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 2, 0, 6) });

        // 4. Transcript turns OR clean L0 Shownotes + one-click 本机转文字 fallback
        if (sentences.Length == 0)
        {
            sheet.Children.Add(BuildL0ShownotesFallback(issue, p, tint, serif));
        }
        else
        {
            var turnsStack = new StackPanel { Spacing = 4 };
            for (int i = 0; i < sentences.Length; i++)
            {
                int idx = i;
                var s = sentences[i];
                if (!string.IsNullOrEmpty(s.SkipReason))
                {
                    string skipKey = $"{issue.Id}:{s.Id}";
                    bool expanded = _expandedSkipIds.Contains(skipKey);
                    double skipDur = Math.Max(1.0, s.T1 - s.T0);
                    string summaryText = !string.IsNullOrWhiteSpace(s.TextTrans)
                        ? s.TextTrans!
                        : $"已静默折叠 1 段赞助口播（{FormatClock(skipDur)}）";

                    var skipWrap = new StackPanel { Spacing = 6 };
                    var skipHead = new Grid();
                    skipHead.Children.Add(new TextBlock
                    {
                        Text = summaryText,
                        FontSize = 11.5,
                        Foreground = ThemeHelper.Brush(p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                    });
                    var toggleSkipBtn = new TextBlock
                    {
                        Text = expanded ? L10n.T("收起") : L10n.T("展开"),
                        FontSize = 11.5,
                        Foreground = ThemeHelper.Brush(p.T3),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    skipHead.Children.Add(toggleSkipBtn);
                    skipWrap.Children.Add(skipHead);

                    if (expanded)
                    {
                        skipWrap.Children.Add(new TextBlock
                        {
                            Text = s.TextOrig,
                            FontFamily = serif,
                            FontSize = 13,
                            LineHeight = 20,
                            Foreground = ThemeHelper.Brush(p.T3),
                            TextWrapping = TextWrapping.Wrap,
                        });
                    }

                    var adBtn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        CornerRadius = new CornerRadius(9),
                        Background = ThemeHelper.Brush(p.Inset),
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(12, 8.5, 12, 8.5),
                        Margin = new Thickness(0, 6, 0, 6),
                        UseSystemFocusVisuals = false,
                        Content = skipWrap,
                    };
                    adBtn.Click += (_, _) =>
                    {
                        if (!_expandedSkipIds.Remove(skipKey)) _expandedSkipIds.Add(skipKey);
                        _shelf.RefreshCurrentSubPage();
                    };
                    turnsStack.Children.Add(adBtn);
                    continue;
                }

                bool active = idx == _readerTurnIdx;
                bool isClipped = highlightedIds.Contains(s.Id);
                bool peeked = _peekedTurnIndices.Contains(idx);

                var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(10, 10, 12, 10) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var timeTb = new TextBlock
                {
                    Text = FormatClock(s.T0),
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T3),
                    Margin = new Thickness(0, 3, 0, 0),
                };
                row.Children.Add(timeTb);

                var body = new StackPanel { Spacing = 4, Opacity = (_blindListen && !active && !peeked) ? 0.14 : 1.0 };
                TextBlock? clippedBadge = null;
                Button? peekPill = null;

                if (!string.IsNullOrWhiteSpace(s.Speaker))
                {
                    var spLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    spLine.Children.Add(new TextBlock
                    {
                        Text = s.Speaker,
                        FontSize = 11.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(tint.Accent),
                    });
                    clippedBadge = new TextBlock
                    {
                        Text = L10n.T("· 已收书摘"),
                        FontSize = 10,
                        Foreground = ThemeHelper.Brush(p.Warn),
                        VerticalAlignment = VerticalAlignment.Center,
                        Visibility = isClipped ? Visibility.Visible : Visibility.Collapsed,
                    };
                    spLine.Children.Add(clippedBadge);

                    if (_blindListen)
                    {
                        peekPill = new Button
                        {
                            Content = peeked ? L10n.T("收起") : L10n.T("偷看"),
                            Height = 19,
                            MinHeight = 19,
                            Padding = new Thickness(6, 0, 6, 0),
                            CornerRadius = new CornerRadius(9),
                            FontSize = 10,
                            Foreground = ThemeHelper.Brush(tint.Accent),
                            Background = ThemeHelper.Brush(tint.Soft),
                            BorderThickness = new Thickness(0),
                            UseSystemFocusVisuals = false,
                            Visibility = active ? Visibility.Collapsed : Visibility.Visible,
                        };
                        peekPill.Click += (_, _) =>
                        {
                            bool nowPeeked = !_peekedTurnIndices.Remove(idx);
                            if (nowPeeked) _peekedTurnIndices.Add(idx);
                            if (_turnVisualByIndex.TryGetValue(idx, out var vm))
                            {
                                if (vm.PeekButton is not null) vm.PeekButton.Content = nowPeeked ? L10n.T("收起") : L10n.T("偷看");
                                vm.BodyPanel.Opacity = (_blindListen && idx != _readerTurnIdx && !nowPeeked) ? 0.14 : 1.0;
                            }
                        };
                        spLine.Children.Add(peekPill);
                    }
                    body.Children.Add(spLine);
                }

                string primaryText = (_track == PodcastTrack.Dub && !string.IsNullOrWhiteSpace(s.TextTrans)) ? s.TextTrans! : s.TextOrig;
                bool isLatin = primaryText.Take(24).Count(c => c < 128 && char.IsLetter(c)) > 8;
                var coachTerms = ParseCoachNoteTerms(s.CoachNote);
                var mainTb = BuildRichPodcastText(primaryText, isLatin ? geoSerif : serif, unknownWords, coachTerms, issue.Id, s.T0, p, tint);
                body.Children.Add(mainTb);

                if (_track == PodcastTrack.Coach && (!string.IsNullOrWhiteSpace(s.TextTrans) || !string.IsNullOrWhiteSpace(s.CoachNote)))
                {
                    if (!string.IsNullOrWhiteSpace(s.TextTrans))
                    {
                        body.Children.Add(new TextBlock
                        {
                            Text = s.TextTrans,
                            FontFamily = serif,
                            FontSize = 13.5,
                            LineHeight = 21,
                            Foreground = ThemeHelper.Brush(p.T2),
                            TextWrapping = TextWrapping.Wrap,
                        });
                    }
                    if (!string.IsNullOrWhiteSpace(s.CoachNote))
                    {
                        body.Children.Add(BuildCoachNoteLine(s.CoachNote!, coachTerms, s.TextOrig, issue.Id, s.T0, p, tint));
                    }
                }

                if (!string.IsNullOrEmpty(s.BookId))
                {
                    string targetBookId = s.BookId;
                    var srcBook = _library.GetKeptBooks().FirstOrDefault(b => b.Id == targetBookId);
                    if (srcBook is not null)
                    {
                        var srcBtn = new Button
                        {
                            Content = $"📖 《{srcBook.Title}》 →",
                            Height = 22,
                            MinHeight = 22,
                            Padding = new Thickness(8, 0, 8, 0),
                            CornerRadius = new CornerRadius(11),
                            FontSize = 10.5,
                            Foreground = ThemeHelper.Brush(tint.Accent),
                            Background = ThemeHelper.Brush(tint.Soft),
                            BorderThickness = new Thickness(0),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            UseSystemFocusVisuals = false,
                        };
                        if (!string.IsNullOrWhiteSpace(s.BookQuote))
                        {
                            var flyout = new Flyout();
                            var pop = new StackPanel { Width = 310, Spacing = 8 };
                            pop.Children.Add(new TextBlock { Text = $"引自《{srcBook.Title}》", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
                            pop.Children.Add(new TextBlock { Text = $"「{s.BookQuote}」", FontFamily = serif, FontSize = 13, LineHeight = 20, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap });
                            var openOrigBtn = MakePaperPill(L10n.T("跳回书页原文 →"), accent: true, p, tint);
                            openOrigBtn.HorizontalAlignment = HorizontalAlignment.Right;
                            openOrigBtn.Click += (_, _) => { flyout.Hide(); App.CurrentApp.OpenBook(targetBookId, section: s.BookSection, sentence: (int?)s.BookSentence); };
                            pop.Children.Add(openOrigBtn);
                            flyout.Content = pop;
                            srcBtn.Flyout = flyout;
                        }
                        else
                        {
                            srcBtn.Click += (_, _) => App.CurrentApp.OpenBook(targetBookId, section: s.BookSection, sentence: (int?)s.BookSentence);
                        }
                        body.Children.Add(srcBtn);
                    }
                }

                // Inline saved cut-in Q&A notes anchored in this turn's [T0, T1]
                foreach (var intr in interrupts.Where(q => q.AtSec >= s.T0 - 0.5 && q.AtSec < s.T1 + 0.5))
                {
                    body.Children.Add(BuildSavedInterruptInlineCard(intr, p, tint));
                }

                Grid.SetColumn(body, 1);
                row.Children.Add(body);

                var turnCtx = new MenuFlyout();
                var clipThisMi = new MenuFlyoutItem { Text = L10n.T("原声划线并写随想…") };
                clipThisMi.Click += async (_, _) =>
                {
                    SetActiveTurnInPlace(idx, bringIntoView: false);
                    await ClipCurrentTurnAsync(issue.Id, promptThought: true);
                };
                var askThisMi = new MenuFlyoutItem { Text = L10n.T("在这里插话提问…") };
                askThisMi.Click += (_, _) =>
                {
                    SetActiveTurnInPlace(idx, bringIntoView: false);
                    BeginInterrupt(issue);
                };
                turnCtx.Items.Add(clipThisMi);
                turnCtx.Items.Add(askThisMi);

                if (isLatin)
                {
                    var candWords = ExtractLookupCandidateWords(primaryText);
                    if (candWords.Length > 0)
                    {
                        var lookupSub = new MenuFlyoutSubItem { Text = L10n.T("查词…") };
                        foreach (var cw in candWords)
                        {
                            string wCopy = cw;
                            string? cHint = coachTerms.TryGetValue(wCopy, out var gh) ? gh : null;
                            var wi = new MenuFlyoutItem { Text = wCopy };
                            wi.Click += (_, _) => ShowVocabPopover(mainTb, wCopy, cHint, primaryText, issue.Id, s.T0, p, tint);
                            lookupSub.Items.Add(wi);
                        }
                        turnCtx.Items.Add(new MenuFlyoutSeparator());
                        turnCtx.Items.Add(lookupSub);
                    }
                }

                var turnCard = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ComputeTurnBackground(active, isClipped, dark, tint),
                    BorderBrush = active ? ThemeHelper.Brush(tint.Edge) : null,
                    BorderThickness = new Thickness(active ? 0.5 : 0),
                    ContextFlyout = turnCtx,
                    Child = row,
                };
                turnCard.Tapped += (_, te) =>
                {
                    // Do not hijack taps that originated inside child Buttons/Hyperlinks
                    if (te.OriginalSource is DependencyObject src && IsInsideInteractiveChild(src, turnCard))
                        return;
                    int prevIdx = _readerTurnIdx;
                    _readerTurnIdx = idx;
                    PlayIssueAtTurn(issue, sentences, idx);
                    UpdateActiveTurnVisualsInPlace(prevIdx, idx, bringIntoView: false);
                    RefreshPlayButtonsInPlace();
                };

                _turnCardElements.Add(turnCard);
                _turnVisualByIndex[idx] = new TurnVisualMeta
                {
                    TurnIndex = idx,
                    SentenceId = s.Id,
                    Card = turnCard,
                    TimeTb = timeTb,
                    BodyPanel = body,
                    ClippedBadge = clippedBadge,
                    PeekButton = peekPill,
                    IsClipped = isClipped,
                };
                turnsStack.Children.Add(turnCard);
            }
            sheet.Children.Add(turnsStack);
        }

        var transcriptScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 0, 36, 0),
            Content = sheet,
        };
        transcriptScroll.Loaded += (_, _) =>
        {
            if (_turnVisualByIndex.TryGetValue(_readerTurnIdx, out var activeVm) && _readerTurnIdx > 0)
            {
                try { activeVm.Card.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.35 }); }
                catch { try { activeVm.Card.StartBringIntoView(); } catch { } }
            }
            if (!_interruptOpen && !_drawerOpen)
            {
                try { sheet.Focus(FocusState.Programmatic); } catch { }
            }
        };

        var readerViewport = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        readerViewport.Children.Add(transcriptScroll);

        // Non-modal floating bottom 插话 card anchored right above the 44px slim player bar!
        var interruptHost = new Border
        {
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16, 0, 44, 0),
            Visibility = _interruptOpen ? Visibility.Visible : Visibility.Collapsed,
        };
        ThemeHelper.ApplyElevation(interruptHost, readerViewport, 18f);
        _interruptCardHost = interruptHost;
        if (_interruptOpen)
        {
            RebuildFloatingInterruptCard(issue);
        }
        readerViewport.Children.Add(interruptHost);

        // Single-row 44px bottom slim player bar mounted in ShelfWindow's BottomDock
        var slimDock = BuildSlimPlayerBar(issue, sentences, dark, p, tint);
        _shelf.SetBottomDock(slimDock);

        return readerViewport;
    }

    private FrameworkElement BuildL0ShownotesFallback(PodcastIssue issue, YYPalette p, YYTintColors tint, FontFamily serif)
    {
        var wrap = new StackPanel { Spacing = 14, Margin = new Thickness(0, 6, 0, 0) };

        var bannerGrid = new Grid { ColumnSpacing = 10 };
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var waveIcon = YYIcons.Make("wave", 14, ThemeHelper.Brush(tint.Accent));
        waveIcon.VerticalAlignment = VerticalAlignment.Center;
        bannerGrid.Children.Add(waveIcon);

        bool busy = _transcribingIssueId == issue.Id;
        var textCol = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textCol.Children.Add(new TextBlock
        {
            Text = L10n.T("原声播放中 · 文字稿尚未就绪"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
        });
        textCol.Children.Add(new TextBlock
        {
            Text = busy && !string.IsNullOrWhiteSpace(_transcribeStatus)
                ? _transcribeStatus
                : L10n.T("随时可按 H 记下原声或点「插话」，音频单集支持一键本机转文字。"),
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(busy ? tint.Accent : p.T3),
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(textCol, 1);
        bannerGrid.Children.Add(textCol);

        bool hasAudio = !string.IsNullOrWhiteSpace(issue.LocalAudioPath) || !string.IsNullOrWhiteSpace(issue.AudioUrl);
        if (hasAudio)
        {
            var trBtn = MakePaperPill(busy ? L10n.T("转写中…") : L10n.T("本机转文字"), accent: true, p, tint);
            trBtn.Height = 26;
            trBtn.IsEnabled = !busy;
            string targetId = issue.Id;
            trBtn.Click += async (_, _) => await RunOnDeviceTranscribeAsync(targetId);
            Grid.SetColumn(trBtn, 2);
            bannerGrid.Children.Add(trBtn);
        }

        wrap.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(14, 11, 14, 11),
            Child = bannerGrid,
        });

        if (!string.IsNullOrWhiteSpace(issue.Shownotes))
        {
            wrap.Children.Add(new TextBlock
            {
                Text = issue.Shownotes,
                FontFamily = serif,
                FontSize = 14.5,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 25,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        return wrap;
    }

    private static Border BuildSavedInterruptInlineCard(PodcastInterrupt intr, YYPalette p, YYTintColors tint)
    {
        var intrCard = new StackPanel { Spacing = 3 };
        intrCard.Children.Add(new TextBlock
        {
            Text = $"🎙 插话：{intr.Question}",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(tint.Accent),
            TextWrapping = TextWrapping.Wrap,
        });
        intrCard.Children.Add(new TextBlock
        {
            Text = intr.Answer,
            FontSize = 12,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
        });
        return new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(p.Inset),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 4, 0, 0),
            Child = intrCard,
        };
    }

    // ---------- Single-Row 44px Bottom Slim Player Bar & Dynamic Segmented Scrubber ----------

    private List<PlayerSegment> BuildPlayerSegments(PodcastIssue issue, MagazineSentence[] sentences, double totalDur)
    {
        double total = Math.Max(1.0, totalDur);
        string[] adKeywords = ["寒暄", "赞助", "广告", "片头", "片尾", "sponsor", "intro", "outro", "ad"];
        PodcastClip[] issueClips;
        try { issueClips = _library.Core.PodcastClips(issue.Id); }
        catch { issueClips = []; }

        var sortedChapters = (issue.Chapters ?? []).OrderBy(c => c.StartSec).ToArray();
        if (sortedChapters.Length > 0)
        {
            var raw = new List<(string Title, double Start, double End)>();
            if (sortedChapters[0].StartSec > 15)
                raw.Add((L10n.T("开场"), 0, sortedChapters[0].StartSec));
            for (int i = 0; i < sortedChapters.Length; i++)
            {
                double start = Math.Max(0, sortedChapters[i].StartSec);
                double nextStart = i + 1 < sortedChapters.Length ? sortedChapters[i + 1].StartSec : total;
                double end = Math.Min(total, Math.Max(start + 1.0, sortedChapters[i].EndSec ?? nextStart));
                raw.Add((sortedChapters[i].Title, start, end));
            }
            int topicCounter = 0;
            var result = new List<PlayerSegment>(raw.Count);
            for (int idx = 0; idx < raw.Count; idx++)
            {
                var item = raw[idx];
                string lower = item.Title.ToLowerInvariant();
                var spanSents = sentences.Where(s => s.T0 >= item.Start - 1.0 && s.T0 < item.End).ToArray();
                bool onlySkipped = spanSents.Length > 0 && spanSents.All(s => !string.IsNullOrEmpty(s.SkipReason));
                bool isAd = onlySkipped || adKeywords.Any(k => lower.Contains(k, StringComparison.Ordinal));
                bool hasBook = spanSents.Any(s => !string.IsNullOrEmpty(s.BookId));
                var clipsInSpan = issueClips.Where(c => c.T0 >= item.Start && c.T0 <= item.End).Select(c => c.T0).ToArray();
                int? tIdx = isAd ? null : ++topicCounter;
                result.Add(new PlayerSegment(idx, tIdx, item.Title, item.Start, item.End, isAd, hasBook, clipsInSpan));
            }
            return result;
        }

        var firstAd = sentences.FirstOrDefault(s => !string.IsNullOrEmpty(s.SkipReason));
        if (firstAd is not null && firstAd.T1 < total - 15)
        {
            double split = Math.Max(12.0, firstAd.T1);
            return
            [
                new PlayerSegment(0, null, L10n.T("寒暄与赞助"), 0, split, true, false, issueClips.Where(c => c.T0 < split).Select(c => c.T0).ToArray()),
                new PlayerSegment(1, 1, issue.ShowTitle, split, total, false, sentences.Any(s => !string.IsNullOrEmpty(s.BookId)), issueClips.Where(c => c.T0 >= split).Select(c => c.T0).ToArray()),
            ];
        }

        return
        [
            new PlayerSegment(0, 1, issue.ShowTitle, 0, total, false, sentences.Any(s => !string.IsNullOrEmpty(s.BookId)), issueClips.Select(c => c.T0).ToArray()),
        ];
    }

    private static SegLayout[] ComputeSegLayouts(IReadOnlyList<PlayerSegment> segments, double totalWidth, double gap)
    {
        if (segments.Count == 0) return [];
        double usable = Math.Max(1.0, totalWidth - gap * Math.Max(0, segments.Count - 1));
        double totalDur = Math.Max(1.0, segments.Sum(s => s.Duration));
        double minWeight = segments.Count > 1 ? totalDur * 0.065 : totalDur;
        double[] weights = segments.Select(s => Math.Max(s.Duration, minWeight)).ToArray();
        double weightSum = Math.Max(1.0, weights.Sum());

        var layouts = new SegLayout[segments.Count];
        double cursor = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            double w = usable * (weights[i] / weightSum);
            layouts[i] = new SegLayout(segments[i], cursor, Math.Max(4.0, w));
            cursor += w + gap;
        }
        return layouts;
    }

    private static double XPositionForTime(double sec, SegLayout[] layouts)
    {
        if (layouts.Length == 0) return 0;
        for (int i = 0; i < layouts.Length; i++)
        {
            var item = layouts[i];
            if (sec <= item.Seg.EndSec || i == layouts.Length - 1)
            {
                double frac = Math.Clamp((sec - item.Seg.StartSec) / item.Seg.Duration, 0, 1);
                return item.X + item.Width * frac;
            }
        }
        return layouts[0].X;
    }

    private static double TimeForXPosition(double x, SegLayout[] layouts)
    {
        if (layouts.Length == 0) return 0;
        var target = layouts[0];
        for (int i = 0; i < layouts.Length; i++)
        {
            if (x >= layouts[i].X - 1.5) target = layouts[i];
            else break;
        }
        double frac = Math.Clamp((x - target.X) / Math.Max(1.0, target.Width), 0, 1);
        return target.Seg.StartSec + target.Seg.Duration * frac;
    }

    private Border BuildSlimPlayerBar(PodcastIssue issue, MagazineSentence[] sentences, bool dark, YYPalette p, YYTintColors tint)
    {
        bool isPlayingNow = _playing && _playingIssueId == issue.Id;
        double totalDur = Math.Max(1.0, Math.Max(issue.DurationSec, sentences.Length > 0 ? sentences[^1].T1 : 1.0));
        double curSec = isPlayingNow && _usingExternalMedia
            ? _mediaPlayer.PlaybackSession.Position.TotalSeconds
            : (isPlayingNow && _playingTurnIdx < _playingSentences.Length
                ? _playingSentences[_playingTurnIdx].T0
                : (_readerTurnIdx < sentences.Length ? sentences[_readerTurnIdx].T0 : issue.PositionSec));

        var segments = BuildPlayerSegments(issue, sentences, totalDur);
        int topicCount = segments.Count(s => !s.IsAd);

        // Single-row 44px bar (PodcastReaderView.swift:1888-2063)
        var row = new Grid { Height = 44, Padding = new Thickness(10, 0, 10, 0), ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 1. Left typographic transport (`-15s · 28px ▶ · +15s`)
        var leftTrans = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var backBtn = MakeCompactTextButton("-15s", p);
        backBtn.Click += (_, _) => StepPlayback(issue, sentences, forward: false, deltaSec: 15);
        leftTrans.Children.Add(backBtn);

        var playIconHost = new ContentPresenter
        {
            Content = YYIcons.Make(isPlayingNow ? "pause" : "play", 10, ThemeHelper.Brush(p.Card), filled: true),
        };
        var mainPlayBtn = new Button
        {
            Width = 28,
            Height = 28,
            MinWidth = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.T1),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(2, 0, 2, 0),
            UseSystemFocusVisuals = false,
            Content = playIconHost,
        };
        ToolTipService.SetToolTip(mainPlayBtn, L10n.T("播放 / 暂停 (Space)"));
        mainPlayBtn.Click += (_, _) =>
        {
            if (_playing && _playingIssueId == issue.Id) StopPlayback();
            else PlayIssueAtTurn(issue, sentences, _readerTurnIdx);
            RefreshPlayButtonsInPlace();
        };
        leftTrans.Children.Add(mainPlayBtn);

        var fwdBtn = MakeCompactTextButton("+15s", p);
        fwdBtn.Click += (_, _) => StepPlayback(issue, sentences, forward: true, deltaSec: 15);
        leftTrans.Children.Add(fwdBtn);
        row.Children.Add(leftTrans);

        // 2. Center stretch: Chapter menu + Dynamic-width Segmented Scrubber + Clock
        var centerGrid = new Grid
        {
            ColumnSpacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = _interruptOpen ? 0.45 : 1.0,
            IsHitTestVisible = !_interruptOpen,
        };
        centerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        centerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        centerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        TextBlock? chapterLbl = null;
        if (segments.Count > 1)
        {
            var curSeg = segments.LastOrDefault(s => curSec >= s.StartSec) ?? segments[0];
            chapterLbl = new TextBlock
            {
                Text = curSeg.IsAd ? L10n.T("赞助") : $"{curSeg.TopicIndex ?? 1:D2}/{Math.Max(1, topicCount):D2}",
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                Foreground = ThemeHelper.Brush(curSeg.IsAd ? p.T3 : tint.Accent),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var chapSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            chapSp.Children.Add(chapterLbl);
            chapSp.Children.Add(YYIcons.Make("chevron-down", 8.5, ThemeHelper.Brush(p.T3)));

            var chapMenu = new MenuFlyout();
            foreach (var seg in segments)
            {
                var targetSeg = seg;
                string prefix = targetSeg.IsAd ? L10n.T("[赞助]") : (targetSeg.TopicIndex is int ti ? $"{ti:D2}" : "");
                string bookMark = targetSeg.HasBookMention ? " · 📖" : "";
                var mi = new MenuFlyoutItem { Text = $"{FormatClock(targetSeg.StartSec)}  {prefix} {targetSeg.Title}{bookMark}" };
                mi.Click += (_, _) => SeekIssueToSeconds(issue, sentences, targetSeg.StartSec);
                chapMenu.Items.Add(mi);
            }

            var chapBtn = new Button
            {
                Height = 24,
                MinHeight = 24,
                Padding = new Thickness(5, 0, 5, 0),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = chapSp,
                Flyout = chapMenu,
            };
            ToolTipService.SetToolTip(chapBtn, curSeg.Title);
            centerGrid.Children.Add(chapBtn);
        }

        // Dynamic-width Segmented Scrubber (SizeChanged-driven, NEVER hardcoded 584px!)
        var scrubHost = new Grid
        {
            Height = 22,
            MinWidth = 140,
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var scrubCanvas = new Canvas { Height = 22, IsHitTestVisible = false };
        scrubHost.Children.Add(scrubCanvas);

        double? hoverSec = null;
        bool isDraggingScrub = false;
        double liveDisplayedSec = curSec;
        Color clipGold = Color.FromArgb(255, 201, 154, 59);
        Color idleTrack = dark ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(34, 38, 34, 28);

        void RenderSegmentedScrubber(double activeSec)
        {
            liveDisplayedSec = activeSec;
            double w = scrubHost.ActualWidth;
            if (w < 16) return;
            scrubCanvas.Children.Clear();

            double gap = segments.Count > 1 ? 3.5 : 0;
            var layouts = ComputeSegLayouts(segments, w, gap);
            var curSeg = segments.LastOrDefault(s => activeSec >= s.StartSec) ?? segments[0];
            if (chapterLbl is not null)
            {
                chapterLbl.Text = curSeg.IsAd ? L10n.T("赞助") : $"{curSeg.TopicIndex ?? 1:D2}/{Math.Max(1, topicCount):D2}";
                chapterLbl.Foreground = ThemeHelper.Brush(curSeg.IsAd ? p.T3 : tint.Accent);
            }

            double midY = 11.0;
            foreach (var item in layouts)
            {
                bool isCurSeg = item.Seg.Id == curSeg.Id;
                double barH = item.Seg.IsAd ? 2.6 : (isCurSeg ? 4.8 : 3.6);
                double fillFrac = activeSec >= item.Seg.EndSec
                    ? 1.0
                    : (activeSec <= item.Seg.StartSec ? 0.0 : (activeSec - item.Seg.StartSec) / item.Seg.Duration);

                var bgBar = new Border
                {
                    Width = item.Width,
                    Height = barH,
                    CornerRadius = new CornerRadius(barH / 2),
                    Background = ThemeHelper.Brush(idleTrack),
                    Opacity = item.Seg.IsAd ? 0.45 : 1.0,
                };
                Canvas.SetLeft(bgBar, item.X);
                Canvas.SetTop(bgBar, midY - barH / 2);
                scrubCanvas.Children.Add(bgBar);

                if (fillFrac > 0.002)
                {
                    var fillBar = new Border
                    {
                        Width = Math.Max(barH, item.Width * fillFrac),
                        Height = barH,
                        CornerRadius = new CornerRadius(barH / 2),
                        Background = ThemeHelper.Brush(item.Seg.IsAd ? p.T3 : (isCurSeg ? tint.Accent : p.T1)),
                        Opacity = item.Seg.IsAd ? 0.55 : (isCurSeg ? 1.0 : 0.72),
                    };
                    Canvas.SetLeft(fillBar, item.X);
                    Canvas.SetTop(fillBar, midY - barH / 2);
                    scrubCanvas.Children.Add(fillBar);
                }

                foreach (double clipSec in item.Seg.ClipTimes)
                {
                    double cFrac = Math.Clamp((clipSec - item.Seg.StartSec) / item.Seg.Duration, 0, 1);
                    var tick = new Border
                    {
                        Width = 2.5,
                        Height = barH + 3,
                        CornerRadius = new CornerRadius(1.25),
                        Background = ThemeHelper.Brush(clipGold),
                    };
                    Canvas.SetLeft(tick, item.X + Math.Clamp(item.Width * cFrac - 1.25, 0, Math.Max(0, item.Width - 2.5)));
                    Canvas.SetTop(tick, midY - (barH + 3) / 2);
                    scrubCanvas.Children.Add(tick);
                }
            }

            double knobSize = isDraggingScrub ? 11.0 : 9.0;
            double knobX = Math.Clamp(XPositionForTime(activeSec, layouts), knobSize / 2, Math.Max(knobSize / 2, w - knobSize / 2));
            var knob = new Border
            {
                Width = knobSize,
                Height = knobSize,
                CornerRadius = new CornerRadius(knobSize / 2),
                Background = ThemeHelper.Brush(isDraggingScrub ? tint.Accent : p.T1),
                BorderBrush = ThemeHelper.Brush(p.Card),
                BorderThickness = new Thickness(1.5),
            };
            Canvas.SetLeft(knob, knobX - knobSize / 2);
            Canvas.SetTop(knob, midY - knobSize / 2);
            scrubCanvas.Children.Add(knob);

            if (hoverSec is double pSec)
            {
                var pSeg = segments.LastOrDefault(s => pSec >= s.StartSec) ?? segments[0];
                string tip = segments.Count > 1 ? $"{pSeg.Title} · {FormatClock(pSec)}" : FormatClock(pSec);
                ToolTipService.SetToolTip(scrubHost, tip);
            }
        }

        scrubHost.SizeChanged += (_, _) => RenderSegmentedScrubber(liveDisplayedSec);
        scrubHost.PointerMoved += (_, pe) =>
        {
            double w = Math.Max(1.0, scrubHost.ActualWidth);
            var layouts = ComputeSegLayouts(segments, w, segments.Count > 1 ? 3.5 : 0);
            double sec = TimeForXPosition(pe.GetCurrentPoint(scrubHost).Position.X, layouts);
            hoverSec = sec;
            if (isDraggingScrub)
            {
                RenderSegmentedScrubber(sec);
                if (_slimClockTb is not null) _slimClockTb.Text = FormatClock(sec);
            }
        };
        scrubHost.PointerExited += (_, _) => hoverSec = null;
        scrubHost.PointerPressed += (_, pe) =>
        {
            isDraggingScrub = true;
            scrubHost.CapturePointer(pe.Pointer);
            double w = Math.Max(1.0, scrubHost.ActualWidth);
            var layouts = ComputeSegLayouts(segments, w, segments.Count > 1 ? 3.5 : 0);
            double sec = TimeForXPosition(pe.GetCurrentPoint(scrubHost).Position.X, layouts);
            RenderSegmentedScrubber(sec);
            if (_slimClockTb is not null) _slimClockTb.Text = FormatClock(sec);
        };
        scrubHost.PointerReleased += (_, pe) =>
        {
            if (!isDraggingScrub) return;
            isDraggingScrub = false;
            scrubHost.ReleasePointerCapture(pe.Pointer);
            double w = Math.Max(1.0, scrubHost.ActualWidth);
            var layouts = ComputeSegLayouts(segments, w, segments.Count > 1 ? 3.5 : 0);
            double target = TimeForXPosition(pe.GetCurrentPoint(scrubHost).Position.X, layouts);
            RenderSegmentedScrubber(target);
            SeekIssueToSeconds(issue, sentences, target);
        };

        Grid.SetColumn(scrubHost, 1);
        centerGrid.Children.Add(scrubHost);

        var clockTb = new TextBlock
        {
            Text = FormatClock(curSec),
            FontSize = 10.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(clockTb, 2);
        centerGrid.Children.Add(clockTb);

        Grid.SetColumn(centerGrid, 1);
        row.Children.Add(centerGrid);

        // 3. Right actions (`1.0×` menu · `划线 [H]` · porcelain `🎙 插话`)
        var rightTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };

        var rateLabelTb = new TextBlock
        {
            Text = $"{_rate:0.0}×",
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(p.T2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var speedMenu = new MenuFlyout();
        foreach (double rVal in new[] { 0.8, 1.0, 1.2, 1.5, 1.8, 2.0 })
        {
            double rv = rVal;
            var mi = new MenuFlyoutItem { Text = $"{rv:0.0}×" };
            mi.Click += (_, _) =>
            {
                _rate = rv;
                _ttsPlayer.SetRate(_rate);
                try { _mediaPlayer.PlaybackSession.PlaybackRate = _rate; } catch { }
                rateLabelTb.Text = $"{_rate:0.0}×";
            };
            speedMenu.Items.Add(mi);
        }
        var rateBtn = new Button
        {
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = rateLabelTb,
            Flyout = speedMenu,
        };
        ToolTipService.SetToolTip(rateBtn, L10n.T("播放倍速"));
        rightTools.Children.Add(rateBtn);

        var clipBtnSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        PopulateSlimClipButtonContent(clipBtnSp, _clipFlashActive, dark, p, tint);
        var clipBtn = new Button
        {
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(5, 0, 5, 0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = clipBtnSp,
        };
        ToolTipService.SetToolTip(clipBtn, L10n.T("记这句原声书摘 (H)"));
        clipBtn.Click += async (_, _) => await ClipCurrentTurnAsync(issue.Id);
        rightTools.Children.Add(clipBtn);

        var interBtn = new Button
        {
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(13),
            UseSystemFocusVisuals = false,
        };
        StyleSlimInterruptButton(interBtn, _interruptOpen, p, tint);
        ToolTipService.SetToolTip(interBtn, L10n.T("压低节目声，语音或打字插话提问 (I)"));
        interBtn.Click += (_, _) =>
        {
            if (_interruptOpen) CloseInterrupt(resumeAudio: true);
            else BeginInterrupt(issue);
        };
        rightTools.Children.Add(interBtn);

        Grid.SetColumn(rightTools, 2);
        row.Children.Add(rightTools);

        _slimPlayIconHost = playIconHost;
        _slimCenterGrid = centerGrid;
        _slimClockTb = clockTb;
        _slimChapterBtnLabel = chapterLbl;
        _redrawSlimScrubber = RenderSegmentedScrubber;
        _slimRateLabelTb = rateLabelTb;
        _slimClipBtnContent = clipBtnSp;
        _slimInterruptBtn = interBtn;

        return new Border
        {
            Width = 620,
            MaxWidth = 640,
            Height = 44,
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = row,
        };
    }

    private static void PopulateTopClipButtonContent(StackPanel sp, bool flashed, YYPalette p, YYTintColors tint)
    {
        sp.Children.Clear();
        if (flashed)
        {
            sp.Children.Add(YYIcons.Make("checkmark", 10, ThemeHelper.Brush(tint.Accent)));
            sp.Children.Add(new TextBlock { Text = L10n.T("已记原声"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            sp.Children.Add(YYIcons.Make("bookmark", 11, ThemeHelper.Brush(tint.Accent), filled: true));
            sp.Children.Add(new TextBlock { Text = L10n.T("原声划线"), FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        }
    }

    private static void PopulateSlimClipButtonContent(StackPanel sp, bool flashed, bool dark, YYPalette p, YYTintColors tint)
    {
        sp.Children.Clear();
        if (flashed)
        {
            sp.Children.Add(YYIcons.Make("checkmark", 9.5, ThemeHelper.Brush(tint.Accent)));
            sp.Children.Add(new TextBlock { Text = L10n.T("已记原声"), FontSize = 11, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            sp.Children.Add(new TextBlock { Text = L10n.T("划线"), FontSize = 11, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(YYChrome.MakeKeycap("H", dark));
        }
    }

    private static void StyleSlimInterruptButton(Button btn, bool active, YYPalette p, YYTintColors tint)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(YYIcons.Make("mic", 10.5, ThemeHelper.Brush(active ? p.Card : tint.Accent)));
        sp.Children.Add(new TextBlock
        {
            Text = active ? L10n.T("插话中") : L10n.T("插话"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(active ? p.Card : tint.Accent),
        });
        btn.Content = sp;
        btn.Background = ThemeHelper.Brush(active ? tint.Accent : p.Card);
        btn.BorderBrush = ThemeHelper.Brush(active ? tint.Accent : tint.Edge);
        btn.BorderThickness = new Thickness(active ? 0 : 0.75);
    }

    // ---------- Lightweight Transcript Word Rendering (ZERO Button-per-word!) ----------

    private static Dictionary<string, string> ParseCoachNoteTerms(string? coachNote)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(coachNote)) return map;
        char[] segSeps = ['\n', '\r', '；', ';', '|', '·'];
        char[] kvSeps = ['：', ':', '—'];
        foreach (var rawSeg in coachNote.Split(segSeps, StringSplitOptions.RemoveEmptyEntries))
        {
            string seg = rawSeg.Trim();
            if (seg.StartsWith("💡", StringComparison.Ordinal)) seg = seg[2..];
            seg = seg.TrimStart('•', '*', '-', ' ');
            int sepIdx = seg.IndexOfAny(kvSeps);
            if (sepIdx <= 0 || sepIdx >= seg.Length - 1) continue;
            string term = seg[..sepIdx].Trim(' ', '"', '\'', '“', '”', '「', '」', '*', '`');
            string gloss = seg[(sepIdx + 1)..].Trim();
            if (term.Length is >= 2 and <= 36 && !string.IsNullOrEmpty(gloss))
            {
                map[term] = gloss;
            }
        }
        return map;
    }

    private FrameworkElement BuildCoachNoteLine(
        string coachNote,
        Dictionary<string, string> coachTerms,
        string contextText,
        string issueId,
        double turnT0,
        YYPalette p,
        YYTintColors tint)
    {
        var tb = new TextBlock
        {
            FontSize = 11.5,
            LineHeight = 19,
            Foreground = ThemeHelper.Brush(tint.Accent),
            TextWrapping = TextWrapping.Wrap,
        };
        if (coachTerms.Count == 0)
        {
            tb.Text = $"💡 {coachNote}";
            return tb;
        }

        tb.Inlines.Add(new Run { Text = "💡 " });
        int idx = 0;
        foreach (var kv in coachTerms)
        {
            if (idx > 0) tb.Inlines.Add(new Run { Text = "  ·  " });
            string term = kv.Key;
            string gloss = kv.Value;
            var link = new Hyperlink
            {
                UnderlineStyle = UnderlineStyle.Single,
                Foreground = ThemeHelper.Brush(tint.Accent),
                FontWeight = FontWeights.SemiBold,
            };
            link.Inlines.Add(new Run { Text = term });
            link.Click += (_, _) => ShowVocabPopover(tb, term, gloss, contextText, issueId, turnT0, p, tint);
            tb.Inlines.Add(link);
            tb.Inlines.Add(new Run { Text = $"：{gloss}" });
            idx++;
        }
        return tb;
    }

    /// <summary>
    /// Renders an entire turn sentence inside a SINGLE <see cref="TextBlock"/>:
    /// plain contiguous <see cref="Run"/>s for normal words and lightweight <see cref="Hyperlink"/>s
    /// ONLY on saved vocabulary / coach terms. Zero <see cref="InlineUIContainer"/> or <see cref="Button"/> allocations!
    /// </summary>
    private TextBlock BuildRichPodcastText(
        string text,
        FontFamily font,
        HashSet<string> vocabWords,
        Dictionary<string, string>? coachTerms,
        string issueId,
        double turnT0,
        YYPalette p,
        YYTintColors tint)
    {
        var tb = new TextBlock
        {
            FontFamily = font,
            FontSize = 16,
            LineHeight = 26,
            Foreground = ThemeHelper.Brush(p.T1),
            TextWrapping = TextWrapping.Wrap,
        };

        bool hasLatin = text.Any(c => c < 128 && char.IsLetter(c));
        bool hasCoachInText = coachTerms is { Count: > 0 } && coachTerms.Keys.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
        if ((!hasLatin || (vocabWords.Count == 0 && (coachTerms is null || coachTerms.Count == 0))) && !hasCoachInText)
        {
            tb.Text = text;
            return tb;
        }

        if (!hasLatin && coachTerms is { Count: > 0 })
        {
            int pos = 0;
            while (pos < text.Length)
            {
                int bestIdx = -1;
                string? bestTerm = null;
                foreach (var k in coachTerms.Keys)
                {
                    int found = text.IndexOf(k, pos, StringComparison.OrdinalIgnoreCase);
                    if (found >= 0 && (bestIdx < 0 || found < bestIdx || (found == bestIdx && k.Length > (bestTerm?.Length ?? 0))))
                    {
                        bestIdx = found;
                        bestTerm = k;
                    }
                }
                if (bestIdx < 0 || bestTerm is null)
                {
                    tb.Inlines.Add(new Run { Text = text[pos..] });
                    break;
                }
                if (bestIdx > pos)
                {
                    tb.Inlines.Add(new Run { Text = text[pos..bestIdx] });
                }
                string surface = text.Substring(bestIdx, bestTerm.Length);
                string hint = coachTerms[bestTerm];
                string w = bestTerm;
                string sentenceCtx = text;
                var link = new Hyperlink
                {
                    UnderlineStyle = UnderlineStyle.Single,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                };
                link.Inlines.Add(new Run { Text = surface });
                link.Click += (_, _) => ShowVocabPopover(tb, w, hint, sentenceCtx, issueId, turnT0, p, tint);
                tb.Inlines.Add(link);
                pos = bestIdx + bestTerm.Length;
            }
            return tb;
        }

        // Latin sentence: coalesce plain tokens into contiguous StringBuilder runs, only creating Hyperlink on vocab/coach words
        char[] trimPunct = [',', '.', ';', ':', '!', '?', '"', '\'', '“', '”', '(', ')', '[', ']', '—', '…'];
        var parts = text.Split(' ');
        var plainBuf = new System.Text.StringBuilder();
        bool anySpecial = false;

        void FlushPlain()
        {
            if (plainBuf.Length > 0)
            {
                tb.Inlines.Add(new Run { Text = plainBuf.ToString() });
                plainBuf.Clear();
            }
        }

        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) plainBuf.Append(' ');
            string rawToken = parts[i];
            if (string.IsNullOrEmpty(rawToken)) continue;

            int start = 0;
            while (start < rawToken.Length && Array.IndexOf(trimPunct, rawToken[start]) >= 0) start++;
            int end = rawToken.Length;
            while (end > start && Array.IndexOf(trimPunct, rawToken[end - 1]) >= 0) end--;

            string clean = start < end ? rawToken[start..end] : "";
            bool isVocab = !string.IsNullOrEmpty(clean) && vocabWords.Contains(clean);
            string? coachHint = null;
            bool isCoach = !string.IsNullOrEmpty(clean) && coachTerms is not null && coachTerms.TryGetValue(clean, out coachHint);

            if (isVocab || isCoach)
            {
                anySpecial = true;
                if (start > 0) plainBuf.Append(rawToken.AsSpan(0, start));
                FlushPlain();

                string w = clean;
                string? hint = coachHint;
                string sentenceCtx = text;
                var link = new Hyperlink
                {
                    UnderlineStyle = UnderlineStyle.Single,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                };
                link.Inlines.Add(new Run { Text = clean });
                link.Click += (_, _) => ShowVocabPopover(tb, w, hint, sentenceCtx, issueId, turnT0, p, tint);
                tb.Inlines.Add(link);

                if (end < rawToken.Length) plainBuf.Append(rawToken.AsSpan(end));
            }
            else
            {
                plainBuf.Append(rawToken);
            }
        }

        if (!anySpecial)
        {
            tb.Text = text;
            return tb;
        }
        FlushPlain();
        return tb;
    }

    private static string[] ExtractLookupCandidateWords(string text)
    {
        char[] seps = [' ', ',', '.', ';', ':', '!', '?', '"', '\'', '“', '”', '(', ')', '[', ']', '—', '…', '\t', '\r', '\n'];
        return text
            .Split(seps, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4 && w.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
    }

    private static bool IsInsideInteractiveChild(DependencyObject? src, DependencyObject stopAt)
    {
        var cur = src;
        while (cur is not null && !ReferenceEquals(cur, stopAt))
        {
            if (cur is ButtonBase) return true;
            try { cur = VisualTreeHelper.GetParent(cur); }
            catch { break; }
        }
        return false;
    }

    private async void ShowVocabPopover(
        FrameworkElement anchor,
        string word,
        string? coachHint,
        string context,
        string issueId,
        double turnT0,
        YYPalette p,
        YYTintColors tint)
    {
        var (hit, savedWords) = await Task.Run(() =>
        {
            try { return (_library.LookUpWordRich(word), _library.GetWords()); }
            catch { return ((WordLookupResult?)null, (IReadOnlyList<VocabWord>)Array.Empty<VocabWord>()); }
        });
        if (_disposed || !anchor.IsLoaded) return;

        string headword = hit?.Headword ?? word.ToLowerInvariant();
        bool alreadySaved = savedWords.Any(x => x.Word.Equals(headword, StringComparison.OrdinalIgnoreCase) && !x.Known);

        string? combinedMeaning = hit?.Summary;
        if (!string.IsNullOrWhiteSpace(coachHint))
        {
            combinedMeaning = string.IsNullOrWhiteSpace(combinedMeaning)
                ? coachHint
                : $"{coachHint}\n{combinedMeaning}";
        }

        var pop = new StackPanel { Width = 280, Spacing = 7 };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(new TextBlock { Text = headword, FontFamily = new FontFamily("Georgia"), FontSize = 16, FontWeight = FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
        if (!string.IsNullOrWhiteSpace(hit?.Phonetic))
            top.Children.Add(new TextBlock { Text = $"/{hit.Phonetic}/", FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) });
        pop.Children.Add(top);

        pop.Children.Add(new TextBlock
        {
            Text = !string.IsNullOrWhiteSpace(combinedMeaning) ? combinedMeaning : L10n.T("词典中未查到该词，仍可收进生词本稍后复习。"),
            FontSize = 12,
            LineHeight = 18,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var pronBtn = MakePaperPill(L10n.T("▶ 读一遍"), accent: false, p, tint);
        pronBtn.Height = 24;
        pronBtn.FontSize = 11;
        pronBtn.Click += async (_, _) =>
        {
            bool wordIsEn = headword.Any(c => c < 128 && char.IsLetter(c));
            _isPreviewingInterruptAnswer = true;
            try
            {
                await AudioPlayer.SpeakForeignWordAsync(_library, headword, wordIsEn ? "en" : "zh-CN");
            }
            finally
            {
                _isPreviewingInterruptAnswer = false;
                if (_playing)
                    App.CurrentApp.ClaimAudioPlayback(_shelf, CreateShelfPauseCallback());
            }
        };

        var flyout = new Flyout { Content = pop };
        var saveBtn = MakePaperPill(alreadySaved ? L10n.T("✓ 已在生词本") : L10n.T("+ 收进生词本"), accent: !alreadySaved, p, tint);
        saveBtn.Height = 24;
        saveBtn.FontSize = 11;
        saveBtn.Click += (_, _) =>
        {
            try
            {
                if (alreadySaved)
                {
                    _library.Core.DeleteWord(headword);
                    alreadySaved = false;
                    saveBtn.Content = L10n.T("+ 收进生词本");
                    _shelf.ShowToast(L10n.T("已从生词本移出"), canUndo: false);
                }
                else
                {
                    string podcastBookKey = "podcast:" + issueId;
                    uint sentSec = (uint)Math.Max(0, (int)Math.Round(turnT0));
                    _library.Core.AddWord(headword, hit?.Phonetic, combinedMeaning, podcastBookKey, 0, sentSec, context);
                    _library.Core.SetWordKnown(headword, false);
                    alreadySaved = true;
                    saveBtn.Content = L10n.T("✓ 已收进生词本");
                    _shelf.ShowToast(L10n.T("已收进生词本 · 下期生词伴听自动融入"), canUndo: false);
                }
            }
            catch { }
        };
        actions.Children.Add(pronBtn);
        actions.Children.Add(saveBtn);
        pop.Children.Add(actions);

        flyout.ShowAt(anchor);
    }

    // ---------- In-Place Turn & Scrubber Visual Updates (ZERO Scroll-Reset!) ----------

    private static Brush ComputeTurnBackground(bool active, bool isClipped, bool dark, YYTintColors tint)
    {
        if (active) return ThemeHelper.Brush(tint.Soft);
        if (isClipped) return ThemeHelper.Brush(Color.FromArgb(dark ? (byte)32 : (byte)42, 245, 206, 98));
        return new SolidColorBrush(Colors.Transparent);
    }

    private void SetActiveTurnInPlace(int newIdx, bool bringIntoView)
    {
        int oldIdx = _readerTurnIdx;
        _readerTurnIdx = newIdx;
        if (_playingIssueId == OpenIssueId)
            _playingTurnIdx = newIdx;
        UpdateActiveTurnVisualsInPlace(oldIdx, newIdx, bringIntoView);
    }

    private void UpdateActiveTurnVisualsInPlace(int oldIdx, int newIdx, bool bringIntoView)
    {
        var p = ThemeHelper.Palette(_readerDarkTheme);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, _readerDarkTheme);

        if (oldIdx != newIdx && _turnVisualByIndex.TryGetValue(oldIdx, out var oldVm))
        {
            oldVm.Card.Background = ComputeTurnBackground(false, oldVm.IsClipped, _readerDarkTheme, tint);
            oldVm.Card.BorderBrush = null;
            oldVm.Card.BorderThickness = new Thickness(0);
            oldVm.TimeTb.Foreground = ThemeHelper.Brush(p.T3);
            bool oldPeeked = _peekedTurnIndices.Contains(oldIdx);
            oldVm.BodyPanel.Opacity = (_blindListen && !oldPeeked) ? 0.14 : 1.0;
            if (oldVm.PeekButton is not null)
                oldVm.PeekButton.Visibility = _blindListen ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_turnVisualByIndex.TryGetValue(newIdx, out var newVm))
        {
            newVm.Card.Background = ComputeTurnBackground(true, newVm.IsClipped, _readerDarkTheme, tint);
            newVm.Card.BorderBrush = ThemeHelper.Brush(tint.Edge);
            newVm.Card.BorderThickness = new Thickness(0.5);
            newVm.TimeTb.Foreground = ThemeHelper.Brush(tint.Accent);
            newVm.BodyPanel.Opacity = 1.0;
            if (newVm.PeekButton is not null)
                newVm.PeekButton.Visibility = Visibility.Collapsed;

            if (bringIntoView)
            {
                try
                {
                    newVm.Card.StartBringIntoView(new BringIntoViewOptions
                    {
                        AnimationDesired = true,
                        VerticalAlignmentRatio = 0.35,
                    });
                }
                catch
                {
                    try { newVm.Card.StartBringIntoView(); } catch { }
                }
            }
        }
    }

    private void RefreshPlayButtonsInPlace()
    {
        bool dark = ThemeHelper.IsDark((_shelf.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);

        if (_slimPlayIconHost is not null && _renderedReaderIssue is not null)
        {
            bool playingReader = _playing && _playingIssueId == _renderedReaderIssue.Id;
            _slimPlayIconHost.Content = YYIcons.Make(playingReader ? "pause" : "play", 10, ThemeHelper.Brush(p.Card), filled: true);
        }

        if (_deskPlayIconHost is not null && _renderedDeskIssueId is not null)
        {
            bool playingDesk = _playing && _playingIssueId == _renderedDeskIssueId;
            _deskPlayIconHost.Content = YYIcons.Make(playingDesk ? "pause" : "play", 10.5, ThemeHelper.Brush(p.Card), filled: true);
        }
    }

    private void UpdateLiveProgressInPlace(double posSec)
    {
        if (OpenIssueId is string openId && _playingIssueId == openId)
        {
            _slimClockTb?.SetValue(TextBlock.TextProperty, FormatClock(posSec));
            _redrawSlimScrubber?.Invoke(posSec);
            return;
        }

        if (OpenIssueId is null && _renderedDeskIssueId is not null && _playingIssueId == _renderedDeskIssueId)
        {
            _redrawDeskScrubber?.Invoke(posSec);
            if (_playingSentences.Length > 0)
            {
                int idx = Math.Max(0, Array.FindLastIndex(_playingSentences, s => posSec >= s.T0));
                var s = _playingSentences[Math.Clamp(idx, 0, _playingSentences.Length - 1)];
                if (_deskSpeakerTb is not null && !string.IsNullOrWhiteSpace(s.Speaker))
                    _deskSpeakerTb.Text = s.Speaker;
                if (_deskExcerptTb is not null)
                    _deskExcerptTb.Text = _track == PodcastTrack.Dub && !string.IsNullOrWhiteSpace(s.TextTrans) ? s.TextTrans! : s.TextOrig;
            }
        }
    }

    // ---------- Dual-Voice Studio Playback & Media Tracking ----------

    private void SwitchTrack(string issueId, PodcastIssue? issue, PodcastTrack nextTr)
    {
        if (_track == nextTr) return;
        PodcastTrack prevTr = _track;

        double? preserveSec = null;
        if (_playingIssueId == issueId && issue is not null)
        {
            if (_usingExternalMedia)
            {
                try { preserveSec = _mediaPlayer.PlaybackSession.Position.TotalSeconds; } catch { }
            }
            else if (_playingTurnIdx >= 0 && _playingTurnIdx < _playingSentences.Length)
            {
                preserveSec = _playingSentences[_playingTurnIdx].T0;
            }
        }

        _track = nextTr;
        try { _library.Core.PodcastSetViewMode(issueId, PodcastEdition.Full, nextTr); } catch { }

        if (_playing && _playingIssueId == issueId && issue is not null)
        {
            var sents = _playingSentences.Length > 0 ? _playingSentences : LoadOrSynthesizeTranscript(issue);
            bool hasAudioStream = (!string.IsNullOrEmpty(issue.LocalAudioPath) && File.Exists(issue.LocalAudioPath))
                || !string.IsNullOrEmpty(issue.AudioUrl);
            bool prevUsedStream = prevTr != PodcastTrack.Dub && hasAudioStream;
            bool nextUsesStream = nextTr != PodcastTrack.Dub && hasAudioStream;

            if (prevUsedStream && nextUsesStream && _usingExternalMedia)
            {
                // Keep real human audio stream playing seamlessly without recreating MediaSource!
            }
            else
            {
                PlayIssueAtTurn(issue, sents, _playingTurnIdx, exactSeekSec: preserveSec);
            }
        }

        _shelf.ShowToast(L10n.T($"同句无缝切换：{TrackLabel(nextTr, issue)}"), canUndo: false);
        _shelf.RefreshCurrentSubPage();
    }

    private void CyclePlaybackRate()
    {
        double[] rs = [0.8, 1.0, 1.2, 1.5, 1.8, 2.0];
        int rIdx = Array.FindIndex(rs, x => Math.Abs(x - _rate) < 0.05);
        _rate = rs[(rIdx + 1) % rs.Length];
        _ttsPlayer.SetRate(_rate);
        try { _mediaPlayer.PlaybackSession.PlaybackRate = _rate; } catch { }
        if (_slimRateLabelTb is not null) _slimRateLabelTb.Text = $"{_rate:0.0}×";
    }

    private void SeekIssueToSeconds(PodcastIssue issue, MagazineSentence[] sentences, double targetSec)
    {
        double totalDur = Math.Max(1.0, Math.Max(issue.DurationSec, sentences.Length > 0 ? sentences[^1].T1 : 1.0));
        double clamped = Math.Clamp(targetSec, 0, totalDur);
        try { _library.Core.PodcastSavePlayback(issue.Id, clamped, null); } catch { }

        int prevReaderIdx = _readerTurnIdx;
        int newIdx = sentences.Length > 0 ? Math.Max(0, Array.FindLastIndex(sentences, s => clamped >= s.T0)) : 0;

        if (OpenIssueId == issue.Id)
        {
            _readerSentences = sentences;
            _readerTurnIdx = newIdx;
        }
        if (_playingIssueId == issue.Id)
        {
            _playingSentences = sentences;
            _playingTurnIdx = newIdx;
        }

        if (_usingExternalMedia && _playing && _playingIssueId == issue.Id)
        {
            try { _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(clamped); } catch { }
        }
        else if (_playing && _playingIssueId == issue.Id && sentences.Length > 0)
        {
            PlayIssueAtTurn(issue, sentences, newIdx, exactSeekSec: clamped);
        }

        if (OpenIssueId == issue.Id)
        {
            UpdateActiveTurnVisualsInPlace(prevReaderIdx, _readerTurnIdx, bringIntoView: true);
            UpdateLiveProgressInPlace(clamped);
            RefreshPlayButtonsInPlace();
        }
        else
        {
            UpdateLiveProgressInPlace(clamped);
        }
    }

    private void StepPlayback(PodcastIssue issue, MagazineSentence[] sentences, bool forward, double deltaSec = 15)
    {
        if (_usingExternalMedia && _playing && _playingIssueId == issue.Id)
        {
            double cur = _mediaPlayer.PlaybackSession.Position.TotalSeconds;
            SeekIssueToSeconds(issue, sentences, cur + (forward ? deltaSec : -deltaSec));
            return;
        }

        if (sentences.Length == 0)
        {
            double nextPos = Math.Clamp(issue.PositionSec + (forward ? deltaSec : -deltaSec), 0, Math.Max(1.0, issue.DurationSec));
            SeekIssueToSeconds(issue, sentences, nextPos);
            return;
        }

        int baseIdx = _playingIssueId == issue.Id ? _playingTurnIdx : (OpenIssueId == issue.Id ? _readerTurnIdx : 0);
        int prevReaderIdx = _readerTurnIdx;
        int step = forward ? 1 : -1;
        int next = Math.Clamp(baseIdx + step, 0, Math.Max(0, sentences.Length - 1));
        if (OpenIssueId == issue.Id) _readerTurnIdx = next;
        PlayIssueAtTurn(issue, sentences, next);

        if (OpenIssueId == issue.Id)
        {
            UpdateActiveTurnVisualsInPlace(prevReaderIdx, _readerTurnIdx, bringIntoView: true);
            UpdateLiveProgressInPlace(sentences[next].T0);
            RefreshPlayButtonsInPlace();
        }
        else
        {
            _shelf.RefreshCurrentSubPage();
        }
    }

    private Action CreateShelfPauseCallback() => () =>
    {
        if (_disposed || _isPreviewingInterruptAnswer) return;
        _shelf.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || _isPreviewingInterruptAnswer) return;
            StopPlayback();
            if (OpenIssueId is not null) RefreshPlayButtonsInPlace();
            else _shelf.RefreshCurrentSubPage();
        });
    };

    private string? PickHostVoiceId(bool secondHost, bool english)
    {
        string style = _library.Prefs.PodcastStudioVoiceStyle ?? "studio-duo-zh";
        bool useEn = english || style == "studio-duo-en";
        string langPrefix = useEn ? "en" : "zh";

        string? clonedId = null;
        if (style.StartsWith("card:", StringComparison.Ordinal))
            clonedId = style[5..];
        else if (style.StartsWith("cloned:", StringComparison.Ordinal))
            clonedId = "cloned." + style[7..];
        else if (style.StartsWith("cloned.", StringComparison.Ordinal))
            clonedId = style;

        VoiceCard? clonedCard = null;
        if (!string.IsNullOrEmpty(clonedId))
        {
            clonedCard = _library.LiveVoice.GetClonedCards().FirstOrDefault(c => c.Id == clonedId);
            if (!secondHost)
                return clonedCard is not null ? clonedCard.Id : clonedId;
        }

        if (style == "studio-solo-zh") secondHost = false;

        if (string.IsNullOrEmpty(clonedId))
        {
            string firstKey = useEn ? "david" : "linshen";
            string secondKey = useEn ? "clara" : "suwan";
            bool allowFirst = !_library.Prefs.PodcastRememberedVoices.TryGetValue(firstKey, out var fOn) || fOn;
            bool allowSecond = !_library.Prefs.PodcastRememberedVoices.TryGetValue(secondKey, out var sOn) || sOn;
            if (secondHost && !allowSecond && allowFirst) secondHost = false;
            else if (!secondHost && !allowFirst && allowSecond) secondHost = true;
        }

        CharacterGender wantGender = secondHost
            ? (clonedCard?.Gender == CharacterGender.F ? CharacterGender.M : CharacterGender.F)
            : CharacterGender.M;

        if (_library.LiveVoice.IsReady)
        {
            var cards = _library.LiveVoice.GetBuiltinCards()
                .Where(c => c.Lang.StartsWith(langPrefix, StringComparison.OrdinalIgnoreCase) && c.Id != clonedId)
                .ToArray();
            if (cards.Length > 0)
            {
                var hostA = cards.FirstOrDefault(c => c.Gender == CharacterGender.M && c.Narrator)
                    ?? cards.FirstOrDefault(c => c.Gender == CharacterGender.M)
                    ?? cards[0];
                if (!secondHost) return hostA.Id;

                var hostB = cards.FirstOrDefault(c => c.Id != hostA.Id && c.Gender == wantGender && c.Narrator)
                    ?? cards.FirstOrDefault(c => c.Id != hostA.Id && c.Gender == wantGender)
                    ?? cards.FirstOrDefault(c => c.Id != hostA.Id)
                    ?? hostA;
                return hostB.Id;
            }
        }

        if (_library.Kokoro.IsInstalled)
        {
            var spks = _library.Kokoro.GetSpeakers(langPrefix)
                .Where(s => s.Id != clonedId)
                .ToArray();
            if (spks.Length > 0)
            {
                var hostA = spks.FirstOrDefault(s => s.Gender == CharacterGender.M && s.Narrator)
                    ?? spks.FirstOrDefault(s => s.Gender == CharacterGender.M)
                    ?? spks[0];
                if (!secondHost) return hostA.Id;

                var hostB = spks.FirstOrDefault(s => s.Id != hostA.Id && s.Gender == wantGender && s.Narrator)
                    ?? spks.FirstOrDefault(s => s.Id != hostA.Id && s.Gender == wantGender)
                    ?? spks.FirstOrDefault(s => s.Id != hostA.Id)
                    ?? hostA;
                return hostB.Id;
            }
        }

        if (_library.CloudVoices.IsConfigured(CloudTtsProvider.Edge))
        {
            if (useEn)
            {
                return secondHost
                    ? (wantGender == CharacterGender.M ? "cloud.edge.en-US-AndrewMultilingualNeural" : "cloud.edge.en-US-AvaMultilingualNeural")
                    : "cloud.edge.en-US-AndrewMultilingualNeural";
            }
            return secondHost
                ? (wantGender == CharacterGender.M ? "cloud.edge.zh-CN-YunxiNeural" : "cloud.edge.zh-CN-XiaoxiaoNeural")
                : "cloud.edge.zh-CN-YunxiNeural";
        }

        var sysVoices = AudioPlayer.GetSystemVoicesForLanguage(useEn ? "en-US" : "zh-CN");
        if (sysVoices.Count > 0)
        {
            var wantSysGender = wantGender == CharacterGender.F
                ? Windows.Media.SpeechSynthesis.VoiceGender.Female
                : Windows.Media.SpeechSynthesis.VoiceGender.Male;
            var hostA = sysVoices.FirstOrDefault(v => v.Gender == Windows.Media.SpeechSynthesis.VoiceGender.Male) ?? sysVoices[0];
            if (!secondHost) return "sys:" + hostA.Id;

            var hostB = sysVoices.FirstOrDefault(v => v.Id != hostA.Id && v.Gender == wantSysGender)
                ?? sysVoices.FirstOrDefault(v => v.Id != hostA.Id)
                ?? hostA;
            return "sys:" + hostB.Id;
        }

        return _library.Prefs.GetVoiceForLanguage(useEn ? "en-US" : "zh-Hans");
    }

    private void PlayIssueAtTurn(PodcastIssue issue, MagazineSentence[] sentences, int idx, double? exactSeekSec = null, bool fromAutoAdvance = false)
    {
        StopDrawerAudioPreviews();
        bool wasPlaying = _playing;
        string? prevIssueId = _playingIssueId;
        bool wasExternalMedia = _usingExternalMedia;

        App.CurrentApp.ClaimAudioPlayback(_shelf, CreateShelfPauseCallback());

        _playing = true;
        _playingIssueId = issue.Id;
        _playingSentences = sentences;
        _playingTurnIdx = sentences.Length > 0 ? Math.Clamp(idx, 0, sentences.Length - 1) : 0;
        if (OpenIssueId == issue.Id)
        {
            _readerSentences = sentences;
            _readerTurnIdx = _playingTurnIdx;
        }
        SyncEqTimer();

        bool isNewPlayStart = !fromAutoAdvance && (!wasPlaying || prevIssueId != issue.Id);
        if (isNewPlayStart)
            _library.Analytics.TrackAudioFeatureUsed("podcast_play");

        _library.SystemIntegration.SetAudioPlaying(_shelf, true, issue.Title, () =>
            _shelf.DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed) return;
                if (_playing) StopPlayback();
                else PlayIssueAtTurn(issue, _playingSentences, _playingTurnIdx);
                if (OpenIssueId == issue.Id) RefreshPlayButtonsInPlace();
                else _shelf.RefreshCurrentSubPage();
            }));
        App.CurrentApp?.ClearActiveMiniPlayerSource(null);
        UpdateSmtcState(issue, playing: true);

        bool hasAudioStream = (!string.IsNullOrEmpty(issue.LocalAudioPath) && File.Exists(issue.LocalAudioPath))
            || !string.IsNullOrEmpty(issue.AudioUrl);
        // 1:1 PodcastHub.swift:203-206: Coach ("双语助教") keeps playing the real human podcast audio stream!
        // Only PodcastTrack.Dub ("原声线中文") switches to synthesized translated narration.
        bool wantsTranslatedDub = _track == PodcastTrack.Dub && sentences.Any(s => !string.IsNullOrWhiteSpace(s.TextTrans));

        if (hasAudioStream && !wantsTranslatedDub)
        {
            try
            {
                _ttsPlayer.Stop();
                string src = !string.IsNullOrEmpty(issue.LocalAudioPath) && File.Exists(issue.LocalAudioPath)
                    ? issue.LocalAudioPath
                    : issue.AudioUrl!;
                _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(src));
                _mediaPlayer.Volume = _interruptOpen ? 0.063 : 1.0;
                _mediaPlayer.PlaybackSession.PlaybackRate = _rate;
                double seekSec = exactSeekSec is double es && es >= 0
                    ? es
                    : (_playingTurnIdx < sentences.Length ? Math.Max(0, sentences[_playingTurnIdx].T0) : Math.Max(0, issue.PositionSec));
                _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(seekSec);
                _mediaPlayer.Play();
                _usingExternalMedia = true;
                _mediaPosTimer.Start();
                return;
            }
            catch { }
        }

        _usingExternalMedia = false;
        _mediaPosTimer.Stop();
        try { _mediaPlayer.Pause(); } catch { }

        if (_playingTurnIdx >= 0 && _playingTurnIdx < sentences.Length)
        {
            var s = sentences[_playingTurnIdx];
            string spk = s.Speaker ?? "";
            bool secondHost = spk.Contains("苏") || spk.Contains("沈") || spk.Contains("Clara", StringComparison.OrdinalIgnoreCase)
                || (_playingTurnIdx % 2 == 1 && string.IsNullOrEmpty(spk));
            bool isEn = (issue.Language ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase) && _track != PodcastTrack.Dub;
            string targetLang = isEn ? "en-US" : "zh-Hans";

            string? hostVid = PickHostVoiceId(secondHost, isEn);
            _ttsPlayer.SetBookContext(issue.Id, targetLang);
            _ttsPlayer.SetVoice(hostVid);
            _ttsPlayer.SetRate(_rate);
            if (!fromAutoAdvance && (isNewPlayStart || wasExternalMedia))
                _library.Analytics.TrackTtsStarted(_ttsPlayer.ActiveTtsEngineKey, "podcast");

            string text = _track == PodcastTrack.Dub && !string.IsNullOrWhiteSpace(s.TextTrans)
                ? s.TextTrans!
                : s.TextOrig;
            try { _library.Core.PodcastSavePlayback(issue.Id, s.T0, issue.DurationSec); } catch { }
            UpdateLiveProgressInPlace(s.T0);
            _ = _ttsPlayer.SpeakSentenceAsync(text, issue.Title, issue.ShowTitle, issue.Id, targetLang);
        }
    }

    /// <summary>
    /// Advances Studio TTS turn-by-turn IN-PLACE without calling <c>_shelf.RefreshCurrentSubPage()</c>!
    /// </summary>
    private void OnStudioTurnFinished()
    {
        _shelf.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed || !_playing || _usingExternalMedia || _playingIssueId is null) return;
            var issue = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == _playingIssueId);
            if (issue is null) { StopPlayback(); return; }

            int prevIdx = _playingTurnIdx;
            int next = _playingTurnIdx + 1;
            while (next < _playingSentences.Length && !string.IsNullOrEmpty(_playingSentences[next].SkipReason))
                next++;

            if (next < _playingSentences.Length)
            {
                _playingTurnIdx = next;
                PlayIssueAtTurn(issue, _playingSentences, next, fromAutoAdvance: true);
                if (OpenIssueId == issue.Id)
                {
                    _readerTurnIdx = next;
                    UpdateActiveTurnVisualsInPlace(prevIdx, next, bringIntoView: true);
                    UpdateLiveProgressInPlace(_playingSentences[next].T0);
                }
                else
                {
                    UpdateLiveProgressInPlace(_playingSentences[next].T0);
                }
            }
            else
            {
                StopPlayback();
                try { _library.Core.PodcastSavePlayback(issue.Id, issue.DurationSec, issue.DurationSec); } catch { }
                if (OpenIssueId == issue.Id)
                {
                    UpdateLiveProgressInPlace(issue.DurationSec);
                    RefreshPlayButtonsInPlace();
                }
                else
                {
                    _shelf.RefreshCurrentSubPage();
                }
            }
        });
    }

    /// <summary>
    /// Updates active turn highlight, scroll-into-view, and scrubber clock IN-PLACE on external media ticks —
    /// NEVER calls <c>_shelf.RefreshCurrentSubPage()</c>!
    /// </summary>
    private void OnMediaPosTick(object? sender, object e)
    {
        if (_disposed || !_playing || !_usingExternalMedia || _playingIssueId is null) return;
        double pos = _mediaPlayer.PlaybackSession.Position.TotalSeconds;
        try { _library.Core.PodcastSavePlayback(_playingIssueId, pos, null); } catch { }

        UpdateLiveProgressInPlace(pos);

        if (_playingSentences.Length > 0)
        {
            int idx = Array.FindLastIndex(_playingSentences, s => pos >= s.T0);
            if (idx >= 0 && idx != _playingTurnIdx)
            {
                int prevIdx = _playingTurnIdx;
                _playingTurnIdx = idx;
                if (OpenIssueId == _playingIssueId)
                {
                    _readerTurnIdx = idx;
                    UpdateActiveTurnVisualsInPlace(prevIdx, idx, bringIntoView: true);
                }
            }
        }
    }

    private void StopPlayback()
    {
        _playing = false;
        _usingExternalMedia = false;
        _mediaPosTimer.Stop();
        SyncEqTimer();
        _ttsPlayer.Stop();
        try { _mediaPlayer.Pause(); } catch { }
        _library.SystemIntegration.SetAudioPlaying(_shelf, false);
        UpdateSmtcState(null, playing: false);
    }

    // ---------- 原声划线 & 非模态底部悬浮「插话」卡 (PodcastReaderView.swift:1672-1881) ----------

    private async Task ClipCurrentTurnAsync(string issueId, bool promptThought = false)
    {
        var issue = _library.Podcasts.GetIssues().FirstOrDefault(i => i.Id == issueId);
        if (issue is null) return;

        bool isPlayingTarget = _playingIssueId == issueId;
        bool isReaderTarget = OpenIssueId == issueId && _renderedReaderIssue?.Id == issueId;
        var sents = isPlayingTarget && _playingSentences.Length > 0
            ? _playingSentences
            : (isReaderTarget && _readerSentences.Length > 0 ? _readerSentences : LoadOrSynthesizeTranscript(issue));
        int targetTurnIdx = isPlayingTarget ? _playingTurnIdx : (isReaderTarget ? _readerTurnIdx : 0);

        double curSec = isPlayingTarget && _usingExternalMedia
            ? _mediaPlayer.PlaybackSession.Position.TotalSeconds
            : (sents.ElementAtOrDefault(targetTurnIdx)?.T0 ?? issue.PositionSec);

        var win = YuyueCoreMethods.ResolveClipWindow(sents, curSec, Math.Max(10.0, issue.DurationSec));
        string quote = !string.IsNullOrWhiteSpace(win.QuoteText)
            ? win.QuoteText
            : (sents.ElementAtOrDefault(targetTurnIdx)?.TextOrig ?? $"原声片段 {FormatClock(win.T0)} – {FormatClock(win.T1)}");

        string spkForClip = win.Speaker ?? sents.ElementAtOrDefault(targetTurnIdx)?.Speaker ?? "";
        bool clipSecondHost = spkForClip.Contains("苏") || spkForClip.Contains("沈") || spkForClip.Contains("Clara", StringComparison.OrdinalIgnoreCase);
        bool clipIsEn = (issue.Language ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase);
        string? clipVoiceId = PickHostVoiceId(clipSecondHost, clipIsEn);

        string? audioRelPath = await _library.Podcasts.SliceLocalAudioClipAsync(issue, win.T0, win.T1, default, quote, clipVoiceId);
        if (_disposed) return;
        try
        {
            var saved = _library.Core.PodcastAddClip(issueId, win.T0, win.T1, win.Speaker, quote, null, audioRelPath);
            if (!_drawerOpen) _hasNewClipDot = true;

            int clippedIdx = sents.Length > 0
                ? Math.Max(0, Array.FindLastIndex(sents, s => win.T0 >= s.T0 - 0.1))
                : targetTurnIdx;

            // Update turn badge & BOTH top and bottom clip button flash in-place without resetting scroll!
            if (OpenIssueId == issueId && _turnVisualByIndex.TryGetValue(clippedIdx, out var vm))
            {
                vm.IsClipped = true;
                if (vm.ClippedBadge is not null) vm.ClippedBadge.Visibility = Visibility.Visible;
                var tint = ThemeHelper.TintColors(_library.Prefs.Tint, _readerDarkTheme);
                vm.Card.Background = ComputeTurnBackground(_readerTurnIdx == vm.TurnIndex, true, _readerDarkTheme, tint);
            }

            if (_slimClipBtnContent is not null || _topClipBtnContent is not null)
            {
                _clipFlashActive = true;
                var p = ThemeHelper.Palette(_readerDarkTheme);
                var tint = ThemeHelper.TintColors(_library.Prefs.Tint, _readerDarkTheme);
                if (_slimClipBtnContent is not null)
                    PopulateSlimClipButtonContent(_slimClipBtnContent, true, _readerDarkTheme, p, tint);
                if (_topClipBtnContent is not null)
                    PopulateTopClipButtonContent(_topClipBtnContent, true, p, tint);
                _ = ResetClipFlashLaterAsync();
            }

            _shelf.ShowToast(audioRelPath is not null ? L10n.T("已裁切原声音频并收入「原声书摘」") : L10n.T("已记下原声片段，收入「原声书摘」"), canUndo: false);
            if (promptThought)
            {
                OpenDrawer("clips", focusClipId: saved.Id);
            }
            else if (_drawerOpen && _drawerTab == "clips")
            {
                _shelf.RefreshCurrentSubPage();
            }
        }
        catch { }
    }

    private async Task ResetClipFlashLaterAsync()
    {
        await Task.Delay(1500);
        _shelf.DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            _clipFlashActive = false;
            var p = ThemeHelper.Palette(_readerDarkTheme);
            var tint = ThemeHelper.TintColors(_library.Prefs.Tint, _readerDarkTheme);
            if (_slimClipBtnContent is not null)
                PopulateSlimClipButtonContent(_slimClipBtnContent, false, _readerDarkTheme, p, tint);
            if (_topClipBtnContent is not null)
                PopulateTopClipButtonContent(_topClipBtnContent, false, p, tint);
        });
    }

    private void BeginInterrupt(PodcastIssue issue)
    {
        _latestInterruptReply = null;
        _interruptQuestion = "";
        _interruptVoiceHint = null;
        _interruptSending = false;
        _interruptListening = false;
        _interruptOpen = true;

        _interruptWasPlayingExternal = _playing && _usingExternalMedia && _playingIssueId == issue.Id;
        _interruptWasPlayingTts = _playing && !_usingExternalMedia && _playingIssueId == issue.Id;
        if (_interruptWasPlayingExternal)
        {
            try { _mediaPlayer.Volume = 0.063; } catch { }
        }
        else if (_interruptWasPlayingTts)
        {
            _ttsPlayer.Pause();
        }

        if (_slimCenterGrid is not null)
        {
            _slimCenterGrid.Opacity = 0.45;
            _slimCenterGrid.IsHitTestVisible = false;
        }

        if (_slimInterruptBtn is not null)
        {
            var p = ThemeHelper.Palette(_readerDarkTheme);
            var tint = ThemeHelper.TintColors(_library.Prefs.Tint, _readerDarkTheme);
            StyleSlimInterruptButton(_slimInterruptBtn, true, p, tint);
        }

        if (_interruptCardHost is not null)
        {
            RebuildFloatingInterruptCard(issue);
            _interruptCardHost.Visibility = Visibility.Visible;
            _interruptInputBox?.Focus(FocusState.Programmatic);
        }
    }

    private void CloseInterrupt(bool resumeAudio)
    {
        AudioPlayer.StopActivePreview();
        try { _interruptMicCts?.Cancel(); _interruptMicCts?.Dispose(); } catch { }
        _interruptMicCts = null;
        _interruptListening = false;
        _interruptSending = false;
        _interruptOpen = false;
        _latestInterruptReply = null;
        _interruptQuestion = "";
        _interruptVoiceHint = null;
        _isPreviewingInterruptAnswer = false;

        if (resumeAudio)
        {
            if (_interruptWasPlayingExternal)
            {
                try
                {
                    double cur = _mediaPlayer.PlaybackSession.Position.TotalSeconds;
                    double rewound = Math.Max(0, cur - 3.0);
                    _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(rewound);
                    _mediaPlayer.Volume = 1.0;
                    if (_playingIssueId is not null)
                    {
                        try { _library.Core.PodcastSavePlayback(_playingIssueId, rewound, null); } catch { }
                        UpdateLiveProgressInPlace(rewound);
                    }
                }
                catch { }
            }
            else if (_interruptWasPlayingTts)
            {
                _ttsPlayer.Resume();
                if (!_ttsPlayer.IsPlaying && _playingIssueId is string pid)
                {
                    var curIss = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == pid);
                    if (curIss is not null)
                        PlayIssueAtTurn(curIss, _playingSentences, _playingTurnIdx);
                }
            }
            else if (_renderedReaderIssue is PodcastIssue pausedIss)
            {
                // Rewind 3s even when paused before 插话 (PodcastHub.swift:799-800)
                try
                {
                    double baseSec = (_playingIssueId == pausedIss.Id && _mediaPlayer.Source is not null)
                        ? _mediaPlayer.PlaybackSession.Position.TotalSeconds
                        : (_readerTurnIdx < _readerSentences.Length ? _readerSentences[_readerTurnIdx].T0 : pausedIss.PositionSec);
                    double rewound = Math.Max(0, baseSec - 3.0);
                    SeekIssueToSeconds(pausedIss, _readerSentences, rewound);
                }
                catch { }
            }
        }
        else
        {
            if (_interruptWasPlayingExternal)
            {
                try { _mediaPlayer.Volume = 1.0; } catch { }
            }
            else if (_interruptWasPlayingTts)
            {
                StopPlayback();
            }
        }

        _interruptWasPlayingExternal = false;
        _interruptWasPlayingTts = false;

        if (_slimCenterGrid is not null)
        {
            _slimCenterGrid.Opacity = 1.0;
            _slimCenterGrid.IsHitTestVisible = true;
        }

        if (_interruptCardHost is not null)
        {
            _interruptCardHost.Visibility = Visibility.Collapsed;
            _interruptCardHost.Child = null;
        }
        if (_slimInterruptBtn is not null)
        {
            var p = ThemeHelper.Palette(_readerDarkTheme);
            var tint = ThemeHelper.TintColors(_library.Prefs.Tint, _readerDarkTheme);
            StyleSlimInterruptButton(_slimInterruptBtn, false, p, tint);
        }
        RefreshPlayButtonsInPlace();
    }

    private void RebuildFloatingInterruptCard(PodcastIssue issue)
    {
        if (_interruptCardHost is null) return;
        bool dark = _readerDarkTheme;
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        Color leafSurface = dark ? Color.FromArgb(255, 31, 30, 28) : Color.FromArgb(255, 248, 245, 239);

        var sents = _readerSentences.Length > 0 && OpenIssueId == issue.Id
            ? _readerSentences
            : (_playingIssueId == issue.Id && _playingSentences.Length > 0 ? _playingSentences : LoadOrSynthesizeTranscript(issue));
        string? linkedBookTitle = sents.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.BookTitle))?.BookTitle;
        bool showingReply = _latestInterruptReply is not null && !_interruptListening && string.IsNullOrWhiteSpace(_interruptQuestion) && !_interruptSending;

        var cardStack = new StackPanel { Spacing = 0 };

        if (showingReply && _latestInterruptReply is PodcastInterrupt reply)
        {
            // Upper pure-white answer zone
            var upper = new StackPanel { Spacing = 7, Padding = new Thickness(16, 12, 16, 12), Background = ThemeHelper.Brush(p.Card), CornerRadius = new CornerRadius(14, 14, 0, 0) };
            var headRow = new Grid { ColumnSpacing = 6 };
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var headLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            headLeft.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(tint.Accent) });
            headLeft.Children.Add(new TextBlock { Text = L10n.T("书伴已答"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
            headLeft.Children.Add(new TextBlock { Text = L10n.T("· 已存入当前句旁"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
            headRow.Children.Add(headLeft);

            var speakBtn = MakePaperPill(_isPreviewingInterruptAnswer ? L10n.T("播放中…") : L10n.T("▶ 听回答"), accent: _isPreviewingInterruptAnswer, p, tint);
            speakBtn.Height = 24;
            speakBtn.FontSize = 11;
            speakBtn.Click += async (_, _) =>
            {
                if (_isPreviewingInterruptAnswer) return;
                bool replyEn = !reply.Answer.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);
                _isPreviewingInterruptAnswer = true;
                speakBtn.Content = L10n.T("播放中…");
                try
                {
                    string? replyVoice = PickHostVoiceId(false, replyEn);
                    _library.Analytics.TrackTtsStarted(
                        AudioPlayer.ResolveTtsEngineKey(_library, replyVoice, replyEn ? "en-US" : "zh-Hans"),
                        "podcast");
                    await AudioPlayer.PreviewVoiceAsync(_library, replyVoice, 1.0, reply.Answer);
                }
                finally
                {
                    _isPreviewingInterruptAnswer = false;
                    speakBtn.Content = L10n.T("▶ 听回答");
                    if (_playing)
                        App.CurrentApp.ClaimAudioPlayback(_shelf, CreateShelfPauseCallback());
                }
            };
            Grid.SetColumn(speakBtn, 1);
            headRow.Children.Add(speakBtn);
            upper.Children.Add(headRow);

            upper.Children.Add(new TextBlock { Text = $"你问：{reply.Question}", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            upper.Children.Add(new TextBlock { Text = reply.Answer, FontFamily = serif, FontSize = 14, LineHeight = 22, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap });
            cardStack.Children.Add(upper);
            cardStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });

            // Lower warm-leaf action bar (`语音追问` · `打字追问` · `明白了，继续 -3s`)
            var lower = new Grid { Height = 40, Padding = new Thickness(16, 0, 16, 0), Background = ThemeHelper.Brush(leafSurface), CornerRadius = new CornerRadius(0, 0, 14, 14) };
            var leftActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
            var micFollowBtn = MakePaperPill(L10n.T("🎙 语音追问"), accent: true, p, tint);
            micFollowBtn.Height = 26;
            micFollowBtn.Click += (_, _) =>
            {
                _latestInterruptReply = null;
                RebuildFloatingInterruptCard(issue);
                _ = ToggleInterruptVoiceDictationAsync(issue);
            };
            var typeFollowBtn = MakePaperPill(L10n.T("打字追问"), accent: false, p, tint);
            typeFollowBtn.Height = 26;
            typeFollowBtn.Click += (_, _) =>
            {
                _latestInterruptReply = null;
                RebuildFloatingInterruptCard(issue);
                _interruptInputBox?.Focus(FocusState.Programmatic);
            };
            leftActs.Children.Add(micFollowBtn);
            leftActs.Children.Add(typeFollowBtn);
            lower.Children.Add(leftActs);

            var resumeBtn = YYChrome.MakeDialogButton(L10n.T("明白了，继续 -3s"), () => CloseInterrupt(resumeAudio: true), dark, _library.Prefs.Tint, "dark", height: 26, horizontalPadding: 13);
            resumeBtn.FontSize = 11.5;
            resumeBtn.HorizontalAlignment = HorizontalAlignment.Right;
            resumeBtn.VerticalAlignment = VerticalAlignment.Center;
            lower.Children.Add(resumeBtn);
            cardStack.Children.Add(lower);
        }
        else
        {
            // Upper pure-white voice + text composer row
            var composerGrid = new Grid { Height = 44, Padding = new Thickness(14, 0, 14, 0), ColumnSpacing = 9, Background = ThemeHelper.Brush(p.Card), CornerRadius = new CornerRadius(14, 14, 0, 0) };
            composerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            composerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            composerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var micSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            micSp.Children.Add(YYIcons.Make("mic", 10.5, ThemeHelper.Brush(_interruptListening ? p.Card : tint.Accent)));
            micSp.Children.Add(new TextBlock
            {
                Text = _interruptListening
                    ? (!string.IsNullOrWhiteSpace(_interruptQuestion) ? L10n.T("说完发送") : L10n.T("聆听中…"))
                    : L10n.T("语音说"),
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                Foreground = ThemeHelper.Brush(_interruptListening ? p.Card : tint.Accent),
            });
            var micBtn = new Button
            {
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(13),
                Background = ThemeHelper.Brush(_interruptListening ? tint.Accent : p.Card),
                BorderBrush = ThemeHelper.Brush(_interruptListening ? tint.Accent : tint.Edge),
                BorderThickness = new Thickness(_interruptListening ? 0 : 0.75),
                UseSystemFocusVisuals = false,
                Content = micSp,
            };
            micBtn.Click += async (_, _) => await ToggleInterruptVoiceDictationAsync(issue);
            composerGrid.Children.Add(micBtn);

            var qBox = new TextBox
            {
                Text = _interruptQuestion,
                PlaceholderText = _interruptListening ? L10n.T("正在收音，请直接开口说…") : L10n.T("点「语音说」直接开口，或直接打字追问…"),
                FontSize = 12.5,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0, 6, 0, 4),
            };
            YYChrome.StripTextBoxChrome(qBox);
            _interruptInputBox = qBox;
            Grid.SetColumn(qBox, 1);
            composerGrid.Children.Add(qBox);

            var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (_interruptSending)
            {
                rightBtns.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true, Margin = new Thickness(8, 0, 8, 0) });
            }
            else
            {
                var sendBtn = new Button
                {
                    Content = L10n.T("发送"),
                    Height = 26,
                    MinHeight = 26,
                    Padding = new Thickness(12, 0, 12, 0),
                    CornerRadius = new CornerRadius(13),
                    FontSize = 11.5,
                    FontWeight = FontWeights.Medium,
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                };
                void StyleSendBtn()
                {
                    bool canSend = !string.IsNullOrWhiteSpace(qBox.Text);
                    sendBtn.IsEnabled = canSend;
                    sendBtn.Background = ThemeHelper.Brush(canSend ? p.T1 : p.Inset);
                    sendBtn.Foreground = ThemeHelper.Brush(canSend ? p.Card : p.T3);
                }
                StyleSendBtn();
                qBox.TextChanged += (_, _) =>
                {
                    _interruptQuestion = qBox.Text;
                    StyleSendBtn();
                };
                qBox.KeyDown += async (_, ke) =>
                {
                    if (ke.Key == VirtualKey.Enter && !string.IsNullOrWhiteSpace(qBox.Text))
                    {
                        ke.Handled = true;
                        await SubmitInterruptAsync(issue);
                    }
                    else if (ke.Key == VirtualKey.Escape)
                    {
                        ke.Handled = true;
                        CloseInterrupt(resumeAudio: true);
                    }
                };
                sendBtn.Click += async (_, _) => await SubmitInterruptAsync(issue);
                rightBtns.Children.Add(sendBtn);
            }

            var contBtn = MakePaperPill(L10n.T("继续 -3s"), accent: false, p, tint);
            contBtn.Height = 26;
            contBtn.Click += (_, _) => CloseInterrupt(resumeAudio: true);
            rightBtns.Children.Add(contBtn);

            Grid.SetColumn(rightBtns, 2);
            composerGrid.Children.Add(rightBtns);
            cardStack.Children.Add(composerGrid);
            cardStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });

            // Lower warm-leaf status & quick-ask chips strip
            var lowerStrip = new Grid { Height = 34, Padding = new Thickness(14, 0, 14, 0), Background = ThemeHelper.Brush(leafSurface), CornerRadius = new CornerRadius(0, 0, 14, 14) };
            var hintLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            hintLeft.Children.Add(new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = ThemeHelper.Brush(tint.Accent) });
            hintLeft.Children.Add(new TextBlock
            {
                Text = _interruptVoiceHint ?? (_interruptListening ? L10n.T("节目已压低 · 端侧实时转写中") : L10n.T("已压低声 · 语音或一键追问")),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(_interruptVoiceHint is not null ? p.T2 : p.T3),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            lowerStrip.Children.Add(hintLeft);

            if (!_interruptListening && string.IsNullOrWhiteSpace(_interruptQuestion))
            {
                var chipsRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                string[] quickPrompts =
                [
                    L10n.T("用大白话再讲一遍"),
                    !string.IsNullOrWhiteSpace(linkedBookTitle) ? $"和《{linkedBookTitle}》有何关联？" : L10n.T("这个观点有什么局限？"),
                ];
                foreach (var qp in quickPrompts)
                {
                    string promptCopy = qp;
                    var chip = MakePaperPill(promptCopy, accent: false, p, tint);
                    chip.Height = 22;
                    chip.FontSize = 10.5;
                    chip.Padding = new Thickness(9, 0, 9, 0);
                    chip.Click += async (_, _) =>
                    {
                        _interruptQuestion = promptCopy;
                        await SubmitInterruptAsync(issue);
                    };
                    chipsRight.Children.Add(chip);
                }
                lowerStrip.Children.Add(chipsRight);
            }
            cardStack.Children.Add(lowerStrip);
        }

        _interruptCardHost.CornerRadius = new CornerRadius(14);
        _interruptCardHost.Background = ThemeHelper.Brush(p.Card);
        _interruptCardHost.BorderBrush = ThemeHelper.Brush(p.Line);
        _interruptCardHost.BorderThickness = new Thickness(0.75);
        _interruptCardHost.Child = cardStack;
    }

    private async Task ToggleInterruptVoiceDictationAsync(PodcastIssue issue)
    {
        if (_interruptListening)
        {
            try { _interruptMicCts?.Cancel(); } catch { }
            return;
        }

        _interruptMicCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        _interruptListening = true;
        _interruptVoiceHint = L10n.T("正在聆听你的提问…");
        RebuildFloatingInterruptCard(issue);

        try
        {
            string lang = string.IsNullOrWhiteSpace(issue.Language) ? "zh-CN" : issue.Language;
            var (text, err) = await WindowsTranscriber.RecognizeMicrophoneOnceAsync(lang, _interruptMicCts.Token);
            _interruptListening = false;
            if (!string.IsNullOrWhiteSpace(text))
            {
                _interruptQuestion = string.IsNullOrWhiteSpace(_interruptQuestion) ? text.Trim() : $"{_interruptQuestion.Trim()} {text.Trim()}";
                _interruptVoiceHint = null;
                await SubmitInterruptAsync(issue);
                return;
            }
            if (!string.IsNullOrWhiteSpace(err))
            {
                _interruptVoiceHint = err;
            }
        }
        finally
        {
            try { _interruptMicCts?.Dispose(); } catch { }
            _interruptMicCts = null;
            _interruptListening = false;
            if (_interruptOpen) RebuildFloatingInterruptCard(issue);
        }
    }

    private async Task SubmitInterruptAsync(PodcastIssue issue)
    {
        try { _interruptMicCts?.Cancel(); } catch { }
        string q = _interruptQuestion.Trim();
        if (string.IsNullOrEmpty(q) || _interruptSending) return;

        bool isPlayingTarget = _playingIssueId == issue.Id;
        var sents = isPlayingTarget && _playingSentences.Length > 0
            ? _playingSentences
            : (OpenIssueId == issue.Id && _readerSentences.Length > 0 ? _readerSentences : LoadOrSynthesizeTranscript(issue));
        int askTurnIdx = isPlayingTarget ? _playingTurnIdx : _readerTurnIdx;
        double atSec = isPlayingTarget && _usingExternalMedia
            ? _mediaPlayer.PlaybackSession.Position.TotalSeconds
            : (sents.ElementAtOrDefault(askTurnIdx)?.T0 ?? issue.PositionSec);
        string? contextLine = sents.ElementAtOrDefault(askTurnIdx)?.TextOrig;

        _interruptSending = true;
        _interruptVoiceHint = L10n.T("书伴正在结合上下文思考…");
        RebuildFloatingInterruptCard(issue);

        try
        {
            string reply = _library.Companion.IsConfigured
                ? await _library.Companion.ChatAsync(
                    $"播客《{issue.Title}》听众在 {FormatClock(atSec)} 处插话问：{q}\n当前原文：{contextLine}",
                    "你是语阅书房电台的主播，请简明亲切地回答听众插话（120字以内），绝不剧透未读后文。",
                    source: UsageSource.Podcast)
                : L10n.T("请先在「设置 · 书伴」中连接大模型 API Key 或本地 Ollama 后即可实时插话问答。");

            if (_disposed) return;
            string cleanReply = reply.Trim();
            PodcastInterrupt savedObj;
            if (_library.Companion.IsConfigured)
            {
                savedObj = _library.Core.PodcastAddInterrupt(issue.Id, atSec, q, cleanReply);
                // Append inline right inside the asked turn (captured before await!) without resetting scroll!
                if (OpenIssueId == issue.Id && _turnVisualByIndex.TryGetValue(askTurnIdx, out var vm))
                {
                    var p = ThemeHelper.Palette(_readerDarkTheme);
                    var tint = ThemeHelper.TintColors(_library.Prefs.Tint, _readerDarkTheme);
                    vm.BodyPanel.Children.Add(BuildSavedInterruptInlineCard(savedObj, p, tint));
                }
            }
            else
            {
                savedObj = new PodcastInterrupt("preview", issue.Id, atSec, q, cleanReply, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }

            _interruptQuestion = "";
            _interruptVoiceHint = null;
            _latestInterruptReply = savedObj;
        }
        catch (Exception ex)
        {
            _interruptVoiceHint = $"提问失败：{ex.Message}";
        }
        finally
        {
            _interruptSending = false;
            if (_interruptOpen) RebuildFloatingInterruptCard(issue);
        }
    }

    // ---------- 4. PodcastStudioSheet（720px 酿一期书房电台 1:1，含分段时长与实时酿制开场） ----------

    public async Task ShowStudioSheetAsync(StudioKind initialKind = StudioKind.WeeklyDigest, string? preselectedBookId = null)
    {
        bool dark = ThemeHelper.IsDark(_shelf.WindowXamlRoot?.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        Color leafSurface = dark ? Color.FromArgb(255, 31, 30, 28) : Color.FromArgb(255, 248, 245, 239);

        StudioKind chosenKind = initialKind;
        uint durationMin = initialKind == StudioKind.Roundtable ? 15u : 10u;
        bool noSpoiler = true;
        string bookFilter = "all"; // reading | finished | all
        string bookSearch = "";
        string voiceStyle = _library.Prefs.PodcastStudioVoiceStyle;
        bool isBrewing = false;
        ContentDialog? dlgRef = null;

        var allBooks = _library.GetKeptBooks().Where(b => b.Format != BookFormat.Audio).ToArray();
        if (allBooks.Any(b => b.Status == ReadingStatus.Reading)) bookFilter = "reading";

        var selectedBookIds = new List<string>();
        if (!string.IsNullOrEmpty(preselectedBookId))
        {
            selectedBookIds.Add(preselectedBookId);
        }
        else
        {
            foreach (var b in allBooks.Where(b => b.Status == ReadingStatus.Reading).Take(initialKind == StudioKind.Roundtable ? 2 : 1))
                selectedBookIds.Add(b.Id);
            if (selectedBookIds.Count == 0 && allBooks.Length > 0)
                selectedBookIds.Add(allBooks[0].Id);
        }

        var headerSubtitle = new TextBlock
        {
            FontSize = 12.5,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 2, 0, 0),
        };
        var leftFormats = new StackPanel { Spacing = 3, Padding = new Thickness(6) };
        var spoilerSegHost = new Grid { ColumnSpacing = 2 };
        spoilerSegHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        spoilerSegHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var refScopeSub = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
        var refRightPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var selectedTokenHost = new ContentControl { Visibility = Visibility.Collapsed };

        var searchBox = new TextBox
        {
            PlaceholderText = $"搜书名、作者（共 {allBooks.Length} 本）…",
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 4, 0, 4),
        };
        YYChrome.StripTextBoxChrome(searchBox);
        var searchPill = new Grid
        {
            Height = 28,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(8),
            Background = ThemeHelper.Brush(p.Inset),
            ColumnSpacing = 6,
        };
        searchPill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchPill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchPill.Children.Add(YYIcons.Make("search", 11.5, ThemeHelper.Brush(p.T3)));
        Grid.SetColumn(searchBox, 1);
        searchPill.Children.Add(searchBox);

        var filterSegGrid = new Grid { ColumnSpacing = 2 };
        for (int c = 0; c < 3; c++) filterSegGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var filterSegWrap = new Border { CornerRadius = new CornerRadius(8), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(2), Child = filterSegGrid };

        var booksSectionHost = new ContentControl();
        var topicBox = new TextBox
        {
            PlaceholderText = L10n.T("选填 · 留空则按最近划线自动拟题"),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 6, 0, 6),
        };
        YYChrome.StripTextBoxChrome(topicBox);

        // Gapped Segmented Duration Scrubber (`5–10–15–30–45–60`, extending to `3` / `90` when custom) + Click-to-edit Pill!
        var durScrubHost = new Grid { Height = 38, Background = new SolidColorBrush(Colors.Transparent), VerticalAlignment = VerticalAlignment.Center };
        var durCanvas = new Canvas { Height = 38, IsHitTestVisible = false };
        durScrubHost.Children.Add(durCanvas);

        var durPillHost = new ContentControl { Width = 74, Height = 28, VerticalAlignment = VerticalAlignment.Center };
        bool editingCustomDur = false;

        uint[] GetActiveStops()
        {
            var stops = new List<uint> { durationMin < 5 ? 3u : 5u, 10u, 15u, 30u, 45u, 60u };
            if (durationMin > 60) stops.Add(90u);
            return stops.ToArray();
        }

        const double durInset = 8.0;
        const double durGap = 7.0;
        const double durTrackH = 6.0;
        const double durTrackY = 13.5;

        double StopX(int idx, int segCount, double segW)
        {
            if (idx <= 0) return durInset;
            if (idx >= segCount) return durInset + segCount * segW + (segCount - 1) * durGap;
            double prevRight = durInset + idx * segW + (idx - 1) * durGap;
            return prevRight + durGap / 2.0;
        }

        double KnobXForMinutes(uint val, uint[] stops, double segW)
        {
            int segCount = Math.Max(1, stops.Length - 1);
            if (val <= stops[0]) return StopX(0, segCount, segW);
            if (val >= stops[^1]) return StopX(segCount, segCount, segW);
            for (int i = 0; i < segCount; i++)
            {
                uint s0 = stops[i];
                uint s1 = stops[i + 1];
                if (val == s1 && i < segCount - 1) return StopX(i + 1, segCount, segW);
                if (val >= s0 && val <= s1)
                {
                    double segLeft = durInset + i * (segW + durGap);
                    double t = (double)(val - s0) / Math.Max(1u, s1 - s0);
                    return segLeft + segW * t;
                }
            }
            return StopX(0, segCount, segW);
        }

        uint MinutesFromX(double x, uint[] stops, double segW, double snapPx)
        {
            int segCount = Math.Max(1, stops.Length - 1);
            for (int idx = 0; idx < stops.Length; idx++)
            {
                if (Math.Abs(x - StopX(idx, segCount, segW)) <= snapPx)
                    return stops[idx];
            }
            if (x <= StopX(0, segCount, segW)) return stops[0];
            if (x >= StopX(segCount, segCount, segW)) return stops[^1];
            for (int i = 0; i < segCount; i++)
            {
                double segLeft = durInset + i * (segW + durGap);
                double segRight = segLeft + segW;
                if (x >= segLeft && x <= segRight)
                {
                    double t = (x - segLeft) / Math.Max(1.0, segW);
                    double raw = stops[i] + t * (stops[i + 1] - stops[i]);
                    return (uint)Math.Clamp((int)Math.Round(raw), 3, 90);
                }
                if (i < segCount - 1 && x > segRight && x < segRight + durGap)
                    return stops[i + 1];
            }
            return durationMin;
        }

        void RenderDurationScrubber()
        {
            double w = durScrubHost.ActualWidth;
            if (w < 40) return;
            durCanvas.Children.Clear();

            var stops = GetActiveStops();
            int segCount = Math.Max(1, stops.Length - 1);
            double span = Math.Max(1.0, w - durInset * 2);
            double segW = Math.Max(6.0, (span - durGap * (segCount - 1)) / segCount);
            Color unfilled = dark ? Color.FromArgb(42, 255, 255, 255) : Color.FromArgb(42, 45, 40, 34);

            for (int i = 0; i < segCount; i++)
            {
                uint s0 = stops[i];
                uint s1 = stops[i + 1];
                double segLeft = durInset + i * (segW + durGap);
                double fillFrac = durationMin >= s1 ? 1.0 : (durationMin <= s0 ? 0.0 : (double)(durationMin - s0) / Math.Max(1u, s1 - s0));

                var bgSeg = new Border
                {
                    Width = segW,
                    Height = durTrackH,
                    CornerRadius = new CornerRadius(durTrackH / 2),
                    Background = ThemeHelper.Brush(unfilled),
                };
                Canvas.SetLeft(bgSeg, segLeft);
                Canvas.SetTop(bgSeg, durTrackY - durTrackH / 2);
                durCanvas.Children.Add(bgSeg);

                if (fillFrac > 0.001)
                {
                    var fgSeg = new Border
                    {
                        Width = Math.Max(durTrackH, segW * fillFrac),
                        Height = durTrackH,
                        CornerRadius = new CornerRadius(durTrackH / 2),
                        Background = ThemeHelper.Brush(tint.Accent),
                    };
                    Canvas.SetLeft(fgSeg, segLeft);
                    Canvas.SetTop(fgSeg, durTrackY - durTrackH / 2);
                    durCanvas.Children.Add(fgSeg);
                }
            }

            // Porcelain-ringed dark-ink bead knob
            double kx = KnobXForMinutes(durationMin, stops, segW);
            var halo = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                Child = new Border
                {
                    Width = 8.5,
                    Height = 8.5,
                    CornerRadius = new CornerRadius(4.25),
                    Background = ThemeHelper.Brush(p.T1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Canvas.SetLeft(halo, kx - 7);
            Canvas.SetTop(halo, durTrackY - 7);
            durCanvas.Children.Add(halo);

            // Boundary stop labels (`5  10  15  30  45  60`)
            for (int idx = 0; idx < stops.Length; idx++)
            {
                double lx = StopX(idx, segCount, segW);
                bool activeStop = durationMin == stops[idx];
                var lbl = new TextBlock
                {
                    Text = stops[idx].ToString(),
                    FontSize = 10,
                    FontWeight = activeStop ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(activeStop ? tint.Accent : p.T3),
                };
                Canvas.SetLeft(lbl, lx - (stops[idx] >= 10 ? 6 : 3));
                Canvas.SetTop(lbl, 22);
                durCanvas.Children.Add(lbl);
            }
        }

        void RenderDurationPill()
        {
            if (editingCustomDur)
            {
                var editBox = new TextBox
                {
                    Text = durationMin.ToString(),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    Padding = new Thickness(4, 2, 2, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                YYChrome.StripTextBoxChrome(editBox);
                void CommitCustomDur()
                {
                    string digits = new string(editBox.Text.Where(char.IsDigit).ToArray());
                    if (uint.TryParse(digits, out uint parsed))
                        durationMin = Math.Clamp(parsed, 3u, 90u);
                    editingCustomDur = false;
                    RenderDurationPill();
                    RenderDurationScrubber();
                }
                editBox.LostFocus += (_, _) => { if (editingCustomDur) CommitCustomDur(); };
                editBox.KeyDown += (_, ke) =>
                {
                    if (ke.Key == VirtualKey.Enter) { ke.Handled = true; CommitCustomDur(); }
                    else if (ke.Key == VirtualKey.Escape) { ke.Handled = true; editingCustomDur = false; RenderDurationPill(); }
                };
                editBox.Loaded += (_, _) => { editBox.Focus(FocusState.Programmatic); editBox.SelectAll(); };

                var editGrid = new Grid { Padding = new Thickness(6, 0, 6, 0), ColumnSpacing = 2 };
                editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                editGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                editGrid.Children.Add(editBox);
                var unitTb = new TextBlock { Text = L10n.T("分钟"), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(unitTb, 1);
                editGrid.Children.Add(unitTb);

                durPillHost.Content = new Border
                {
                    Height = 28,
                    CornerRadius = new CornerRadius(8),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(tint.Accent),
                    BorderThickness = new Thickness(1),
                    Child = editGrid,
                };
                return;
            }

            var pillSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            pillSp.Children.Add(new TextBlock { Text = durationMin.ToString(), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            pillSp.Children.Add(new TextBlock { Text = L10n.T("分钟"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) });

            var pillBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Background = ThemeHelper.Brush(leafSurface),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                UseSystemFocusVisuals = false,
                Content = pillSp,
            };
            ToolTipService.SetToolTip(pillBtn, L10n.T("点击可直接输入 3–90 分钟"));
            pillBtn.Click += (_, _) =>
            {
                if (isBrewing) return;
                editingCustomDur = true;
                RenderDurationPill();
            };
            durPillHost.Content = pillBtn;
        }

        bool draggingDur = false;
        uint[]? frozenStops = null;
        void UpdateDurFromPointer(double x, double snapPx)
        {
            double w = Math.Max(40.0, durScrubHost.ActualWidth);
            var stops = frozenStops ?? GetActiveStops();
            int segCount = Math.Max(1, stops.Length - 1);
            double segW = Math.Max(6.0, (w - durInset * 2 - durGap * (segCount - 1)) / segCount);
            uint next = MinutesFromX(x, stops, segW, snapPx);
            if (next != durationMin)
            {
                durationMin = next;
                if (!editingCustomDur) RenderDurationPill();
                RenderDurationScrubber();
            }
        }

        durScrubHost.SizeChanged += (_, _) => RenderDurationScrubber();
        durScrubHost.PointerPressed += (_, pe) =>
        {
            if (isBrewing) return;
            if (editingCustomDur) { editingCustomDur = false; RenderDurationPill(); }
            draggingDur = true;
            frozenStops = GetActiveStops();
            durScrubHost.CapturePointer(pe.Pointer);
            UpdateDurFromPointer(pe.GetCurrentPoint(durScrubHost).Position.X, snapPx: 10);
        };
        durScrubHost.PointerMoved += (_, pe) =>
        {
            if (!draggingDur) return;
            UpdateDurFromPointer(pe.GetCurrentPoint(durScrubHost).Position.X, snapPx: 5.5);
        };
        durScrubHost.PointerReleased += (_, pe) =>
        {
            if (!draggingDur) return;
            draggingDur = false;
            durScrubHost.ReleasePointerCapture(pe.Pointer);
            UpdateDurFromPointer(pe.GetCurrentPoint(durScrubHost).Position.X, snapPx: 6.5);
            frozenStops = null;
            RenderDurationScrubber();
        };

        var voiceOptions = new List<(string Id, string ShortLabel, string FullLabel)>
        {
            ("studio-duo-zh", L10n.T("林深 · 苏晚"), L10n.T("林深 · 苏晚（书房双人对谈）")),
            ("studio-duo-en", "Clara & David", L10n.T("Clara & David（自然英语对谈）")),
            ("studio-solo-zh", L10n.T("林深 · 单人"), L10n.T("林深（单人沉静导读）")),
        };
        foreach (var cc in _library.LiveVoice.GetClonedCards())
            voiceOptions.Add(("card:" + cc.Id, cc.Name, $"✦ {cc.Name}（我的克隆声纹）"));

        string ShortHostName(string id) => voiceOptions.FirstOrDefault(v => v.Id == id).ShortLabel ?? L10n.T("林深 · 苏晚");
        var hostVoiceLabel = new TextBlock { Text = ShortHostName(voiceStyle), FontSize = 12, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };

        var booksCard = new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
        };

        var topFilterBar = new Grid
        {
            Height = 42,
            Padding = new Thickness(10, 0, 10, 0),
            ColumnSpacing = 8,
            Background = ThemeHelper.Brush(leafSurface),
            CornerRadius = new CornerRadius(13, 13, 0, 0),
        };
        topFilterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topFilterBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topFilterBar.Children.Add(searchPill);
        Grid.SetColumn(filterSegWrap, 1);
        topFilterBar.Children.Add(filterSegWrap);

        var booksListHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        var booksPickerInner = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                topFilterBar,
                new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) },
                booksListHost,
            },
        };

        void RebuildForm()
        {
            headerSubtitle.Text = chosenKind switch
            {
                StudioKind.BookGuide => L10n.T("·  读前热身精讲 · 拆解核心脉络"),
                StudioKind.Roundtable => L10n.T("·  多书观点碰撞 · 串联不同视角"),
                StudioKind.VocabRadio => L10n.T("·  把生词本织进自然对话"),
                _ => L10n.T("·  串起近期划线与阅读心绪"),
            };

            leftFormats.Children.Clear();
            (StudioKind K, string Icon, string Name, string Sub)[] kinds =
            [
                (StudioKind.WeeklyDigest, "checklist-lines", L10n.T("书房周刊"), L10n.T("串起本周划线")),
                (StudioKind.BookGuide, "book-open", L10n.T("难书导读"), L10n.T("精讲难懂章节")),
                (StudioKind.Roundtable, "circles-venn", L10n.T("跨书圆桌"), L10n.T("多书观点对谈")),
                (StudioKind.VocabRadio, "translate", L10n.T("生词外语"), L10n.T("生词自然伴听")),
            ];
            foreach (var (k, ic, nm, sub) in kinds)
            {
                bool on = chosenKind == k;
                var row = new Grid { ColumnSpacing = 9 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(YYIcons.Make(ic, 13.5, ThemeHelper.Brush(on ? tint.Accent : p.T2)));
                var sp = new StackPanel { Spacing = 1.5 };
                sp.Children.Add(new TextBlock { Text = nm, FontSize = 12.5, FontWeight = on ? FontWeights.Medium : FontWeights.Normal, Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T1) });
                sp.Children.Add(new TextBlock { Text = sub, FontSize = 10, Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T3) });
                Grid.SetColumn(sp, 1);
                row.Children.Add(sp);
                var b = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Height = 44,
                    Padding = new Thickness(10, 0, 10, 0),
                    CornerRadius = new CornerRadius(8.5),
                    Background = on ? ThemeHelper.Brush(tint.Soft) : new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = row,
                };
                var nextK = k;
                b.Click += (_, _) =>
                {
                    if (isBrewing) return;
                    chosenKind = nextK;
                    if (chosenKind == StudioKind.BookGuide && selectedBookIds.Count > 1)
                    {
                        string keep = selectedBookIds[0];
                        selectedBookIds.Clear();
                        selectedBookIds.Add(keep);
                    }
                    RebuildForm();
                };
                leftFormats.Children.Add(b);
            }

            // Spoiler control: 🔒 仅已读 | 全书
            spoilerSegHost.Children.Clear();
            foreach (var (col, isSafe, lbl) in new[] { (0, true, L10n.T("仅已读")), (1, false, L10n.T("全书")) })
            {
                bool on = noSpoiler == isSafe;
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (isSafe) sp.Children.Add(YYIcons.Make("lock", 10, ThemeHelper.Brush(on ? tint.Accent : p.T3)));
                sp.Children.Add(new TextBlock { Text = lbl, FontSize = 11, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T3) });
                var sb = new Button
                {
                    Content = sp,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = 23,
                    MinHeight = 23,
                    Padding = new Thickness(0),
                    Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(6),
                    UseSystemFocusVisuals = false,
                };
                bool nextSafe = isSafe;
                sb.Click += (_, _) => { if (!isBrewing) { noSpoiler = nextSafe; RebuildForm(); } };
                Grid.SetColumn(sb, col);
                spoilerSegHost.Children.Add(sb);
            }

            if (chosenKind == StudioKind.VocabRadio)
            {
                var words = _library.GetWords().Where(w => !w.Known).ToArray();
                refScopeSub.Text = words.Length == 0 ? L10n.T("通用英语精听") : L10n.T("自动编入自然对话");
                refRightPanel.Children.Clear();
                if (words.Length > 0)
                {
                    refRightPanel.Children.Add(new TextBlock { Text = $"共 {Math.Min(15, words.Length)} 词", FontSize = 11, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(tint.Accent) });
                }
                selectedTokenHost.Visibility = Visibility.Collapsed;

                var vStack = new StackPanel { Spacing = 8, Padding = new Thickness(14), MinHeight = 158 };
                vStack.Children.Add(new TextBlock
                {
                    Text = words.Length == 0
                        ? L10n.T("生词本目前为空，将生成一期通用英语精听")
                        : $"待复习 {Math.Min(15, words.Length)} 个生词将自然编入双人对话",
                    FontSize = 13,
                    FontWeight = FontWeights.Medium,
                    Foreground = ThemeHelper.Brush(p.T1),
                });
                vStack.Children.Add(new TextBlock
                {
                    Text = words.Length == 0
                        ? L10n.T("阅读书页或收听英文播客时点词收藏，待复习生词会自动汇入这里，在真实语境里反复复现。")
                        : string.Join(" · ", words.Take(14).Select(w => w.Word)),
                    FontFamily = words.Length > 0 ? new FontFamily("Georgia") : new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(words.Length > 0 ? p.T2 : p.T3),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                });
                booksCard.Child = vStack;
                return;
            }

            refScopeSub.Text = chosenKind == StudioKind.BookGuide ? L10n.T("单选精讲") : L10n.T("可多选");
            refRightPanel.Children.Clear();
            if (selectedBookIds.Count > 0)
            {
                refRightPanel.Children.Add(new TextBlock { Text = $"已选 {selectedBookIds.Count} 本", FontSize = 11, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center });
                if (selectedBookIds.Count > 1 || chosenKind != StudioKind.BookGuide)
                {
                    var clearBtn = new Button
                    {
                        Content = L10n.T("清空"),
                        Padding = new Thickness(2, 0, 2, 0),
                        MinWidth = 0,
                        MinHeight = 0,
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T3),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        UseSystemFocusVisuals = false,
                    };
                    clearBtn.Click += (_, _) => { if (!isBrewing) { selectedBookIds.Clear(); RebuildForm(); } };
                    refRightPanel.Children.Add(clearBtn);
                }
            }

            filterSegGrid.Children.Clear();
            var fOpts = new[] { ("reading", L10n.T("在读")), ("finished", L10n.T("读过")), ("all", L10n.T("全部")) };
            for (int fi = 0; fi < fOpts.Length; fi++)
            {
                var (fk, fl) = fOpts[fi];
                bool on = bookFilter == fk && string.IsNullOrWhiteSpace(bookSearch);
                var fb = new Button
                {
                    Content = fl,
                    Height = 24,
                    MinHeight = 24,
                    Padding = new Thickness(8, 0, 8, 0),
                    FontSize = 11,
                    FontWeight = on ? FontWeights.Medium : FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(on ? p.T1 : p.T3),
                    Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(6.5),
                    UseSystemFocusVisuals = false,
                };
                string key = fk;
                fb.Click += (_, _) =>
                {
                    if (isBrewing) return;
                    searchBox.Text = "";
                    bookSearch = "";
                    bookFilter = key;
                    RebuildForm();
                };
                Grid.SetColumn(fb, fi);
                filterSegGrid.Children.Add(fb);
            }

            string q = bookSearch.Trim();
            var filtered = !string.IsNullOrEmpty(q)
                ? allBooks.Where(b => b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || b.Authors.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase))).ToArray()
                : bookFilter switch
                {
                    "reading" => allBooks.Where(b => b.Status == ReadingStatus.Reading).ToArray() is { Length: > 0 } rd ? rd : allBooks,
                    "finished" => allBooks.Where(b => b.Status == ReadingStatus.Finished).ToArray(),
                    _ => allBooks,
                };

            var visibleIds = filtered.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
            bool showPinnedTokens = selectedBookIds.Count > 0 && (allBooks.Length > 6 || !string.IsNullOrEmpty(q) || selectedBookIds.Any(id => !visibleIds.Contains(id)));
            if (showPinnedTokens)
            {
                var tokenRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Padding = new Thickness(0, 1, 0, 1) };
                foreach (var sid in selectedBookIds.ToArray())
                {
                    var sbk = allBooks.FirstOrDefault(b => b.Id == sid);
                    if (sbk is null) continue;
                    int pct = (int)Math.Round(sbk.Progress * 100);
                    var chipSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
                    chipSp.Children.Add(new TextBlock { Text = sbk.Title, FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(tint.Accent), MaxWidth = 140, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    if (pct > 0) chipSp.Children.Add(new TextBlock { Text = $"{pct}%", FontSize = 10, Foreground = ThemeHelper.Brush(tint.Accent), Opacity = 0.78, VerticalAlignment = VerticalAlignment.Center });
                    var rmBtn = new Button
                    {
                        Width = 14,
                        Height = 14,
                        MinWidth = 14,
                        MinHeight = 14,
                        Padding = new Thickness(0),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        UseSystemFocusVisuals = false,
                        Content = YYIcons.Make("xmark", 8.5, ThemeHelper.Brush(tint.Accent)),
                    };
                    string removeId = sid;
                    rmBtn.Click += (_, _) => { if (!isBrewing) { selectedBookIds.Remove(removeId); RebuildForm(); } };
                    chipSp.Children.Add(rmBtn);

                    tokenRow.Children.Add(new Border
                    {
                        Height = 24,
                        CornerRadius = new CornerRadius(12),
                        Background = ThemeHelper.Brush(p.Card),
                        BorderBrush = ThemeHelper.Brush(tint.Edge),
                        BorderThickness = new Thickness(0.75),
                        Padding = new Thickness(10, 0, 6, 0),
                        Child = chipSp,
                    });
                }
                selectedTokenHost.Content = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = tokenRow,
                };
                selectedTokenHost.Visibility = Visibility.Visible;
            }
            else
            {
                selectedTokenHost.Visibility = Visibility.Collapsed;
            }

            if (allBooks.Length == 0)
            {
                booksListHost.Content = new TextBlock
                {
                    Text = L10n.T("书架里还没有书，将根据你的选题直接生成。"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(14, 48, 14, 48),
                };
            }
            else if (filtered.Length == 0)
            {
                var emptyFiltered = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(14, 38, 14, 38) };
                emptyFiltered.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(q) ? L10n.T("当前分组暂时没有书") : $"没有找到「{q}」", FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Center });
                var showAllBtn = MakePaperPill(L10n.T("看全部书目"), accent: false, p, tint);
                showAllBtn.HorizontalAlignment = HorizontalAlignment.Center;
                showAllBtn.Click += (_, _) => { searchBox.Text = ""; bookSearch = ""; bookFilter = "all"; RebuildForm(); };
                emptyFiltered.Children.Add(showAllBtn);
                booksListHost.Content = emptyFiltered;
            }
            else
            {
                var bGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8, Padding = new Thickness(10) };
                bGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                bGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int shownCount = filtered.Length;
                int bRows = (shownCount + 1) / 2;
                for (int r = 0; r < bRows; r++) bGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (int i = 0; i < shownCount; i++)
                {
                    var bk = filtered[i];
                    bool checkedOn = selectedBookIds.Contains(bk.Id);
                    var cell = new Grid { ColumnSpacing = 9 };
                    cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                    cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    UIElement miniCover;
                    if (!string.IsNullOrEmpty(bk.CoverPath) && File.Exists(bk.CoverPath))
                    {
                        miniCover = new Border
                        {
                            Width = 22,
                            Height = 30,
                            CornerRadius = new CornerRadius(3),
                            BorderBrush = ThemeHelper.Brush(p.Line),
                            BorderThickness = new Thickness(0.5),
                            Child = new Image { Source = new BitmapImage(new Uri(bk.CoverPath)) { DecodePixelWidth = 48 }, Stretch = Stretch.UniformToFill },
                        };
                    }
                    else
                    {
                        miniCover = new Border
                        {
                            Width = 22,
                            Height = 30,
                            CornerRadius = new CornerRadius(3),
                            Background = ThemeHelper.Brush(p.Inset),
                            BorderBrush = ThemeHelper.Brush(p.Line),
                            BorderThickness = new Thickness(0.5),
                            Child = new TextBlock { Text = bk.Title.Length > 0 ? bk.Title[..1] : "书", FontFamily = serif, FontSize = 9, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                        };
                    }
                    cell.Children.Add(miniCover);

                    int pct = (int)Math.Round(bk.Progress * 100);
                    string stName = bk.Status == ReadingStatus.Finished ? L10n.T("读过") : (pct > 0 ? $"{pct}%" : (bk.Status == ReadingStatus.Want ? L10n.T("想读") : L10n.T("未读")));
                    string authorPrefix = bk.Authors.FirstOrDefault() is { Length: > 0 } au ? $"{au} · {stName}" : stName;
                    var bMeta = new StackPanel { Spacing = 1.5, VerticalAlignment = VerticalAlignment.Center };
                    bMeta.Children.Add(new TextBlock { Text = bk.Title, FontSize = 12.5, FontWeight = checkedOn ? FontWeights.Medium : FontWeights.Normal, Foreground = ThemeHelper.Brush(checkedOn ? tint.Accent : p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    bMeta.Children.Add(new TextBlock { Text = authorPrefix, FontSize = 10.5, Foreground = ThemeHelper.Brush(checkedOn ? tint.Accent : p.T3), Opacity = checkedOn ? 0.8 : 1.0, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    Grid.SetColumn(bMeta, 1);
                    cell.Children.Add(bMeta);

                    if (checkedOn)
                    {
                        var chk = YYIcons.Make("checkmark", 10.5, ThemeHelper.Brush(tint.Accent));
                        Grid.SetColumn(chk, 2);
                        cell.Children.Add(chk);
                    }

                    var bBtn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Height = 46,
                        Padding = new Thickness(9, 0, 9, 0),
                        CornerRadius = new CornerRadius(10),
                        Background = ThemeHelper.Brush(checkedOn ? tint.Soft : leafSurface),
                        BorderBrush = ThemeHelper.Brush(checkedOn ? tint.Edge : p.Line),
                        BorderThickness = new Thickness(0.5),
                        UseSystemFocusVisuals = false,
                        Content = cell,
                    };
                    string bid = bk.Id;
                    bBtn.Click += (_, _) =>
                    {
                        if (isBrewing) return;
                        if (selectedBookIds.Contains(bid))
                        {
                            selectedBookIds.Remove(bid);
                        }
                        else if (chosenKind == StudioKind.BookGuide)
                        {
                            selectedBookIds.Clear();
                            selectedBookIds.Add(bid);
                        }
                        else
                        {
                            selectedBookIds.Add(bid);
                        }
                        RebuildForm();
                    };
                    Grid.SetRow(bBtn, i / 2);
                    Grid.SetColumn(bBtn, i % 2);
                    bGrid.Children.Add(bBtn);
                }

                booksListHost.Content = new ScrollViewer
                {
                    MaxHeight = showPinnedTokens ? 136 : 160,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                    Content = bGrid,
                };
            }

            if (!ReferenceEquals(booksCard.Child, booksPickerInner))
                booksCard.Child = booksPickerInner;
        }

        searchBox.TextChanged += (_, _) => { bookSearch = searchBox.Text; RebuildForm(); };
        RenderDurationPill();
        RebuildForm();

        // Top Header
        var topHead = new Grid { Padding = new Thickness(20, 16, 20, 12) };
        var topLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        topLeft.Children.Add(new TextBlock { Text = L10n.T("酿一期书房电台"), FontFamily = serif, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        topLeft.Children.Add(headerSubtitle);
        topHead.Children.Add(topLeft);
        var closeBtn = YYChrome.MakeDialogCloseButton(() => { if (!isBrewing) dlgRef?.Hide(); }, dark);
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        topHead.Children.Add(closeBtn);

        // Left rail card
        var leftGrid = new Grid();
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftGrid.Children.Add(leftFormats);
        var leftDiv = new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) };
        Grid.SetRow(leftDiv, 1);
        leftGrid.Children.Add(leftDiv);
        var spoilerBox = new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(10),
            Background = ThemeHelper.Brush(leafSurface),
            CornerRadius = new CornerRadius(0, 0, 13, 13),
            Children =
            {
                new TextBlock { Text = L10n.T("防剧透范围"), FontSize = 11, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T2) },
                new Border { CornerRadius = new CornerRadius(8), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(2), Child = spoilerSegHost },
            },
        };
        Grid.SetRow(spoilerBox, 2);
        leftGrid.Children.Add(spoilerBox);

        var leftCard = new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = leftGrid,
        };

        // Right column
        var refHead = new Grid();
        var refHeadLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        refHeadLeft.Children.Add(new TextBlock { Text = L10n.T("参考书目"), FontFamily = serif, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        refHeadLeft.Children.Add(refScopeSub);
        refHead.Children.Add(refHeadLeft);
        refHead.Children.Add(refRightPanel);

        var topicRow = new Grid { ColumnSpacing = 10, Padding = new Thickness(14, 0, 14, 0), Height = 39 };
        topicRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        topicRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topicRow.Children.Add(new TextBlock { Text = L10n.T("选题"), FontSize = 12, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(topicBox, 1);
        topicRow.Children.Add(topicBox);

        var durGrid = new Grid { ColumnSpacing = 10, Padding = new Thickness(14, 6, 14, 6), Height = 50 };
        durGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        durGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        durGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        durGrid.Children.Add(new TextBlock { Text = L10n.T("时长"), FontSize = 12, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(durScrubHost, 1);
        durGrid.Children.Add(durScrubHost);
        Grid.SetColumn(durPillHost, 2);
        durGrid.Children.Add(durPillHost);

        var metaCard = new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = new StackPanel
            {
                Spacing = 0,
                Children =
                {
                    topicRow,
                    new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 0, 0) },
                    durGrid,
                },
            },
        };

        var rightColStack = new StackPanel { Spacing = 8, Children = { refHead, selectedTokenHost, booksCard, metaCard } };
        booksSectionHost.Content = rightColStack;

        var columnsGrid = new Grid { ColumnSpacing = 14, Padding = new Thickness(20, 0, 20, 12) };
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columnsGrid.Children.Add(leftCard);
        Grid.SetColumn(booksSectionHost, 1);
        columnsGrid.Children.Add(booksSectionHost);

        // Unified Footer with live brewing status (keeps sheet open while generating, then auto-opens & plays!)
        var voiceMenu = new MenuFlyout();
        foreach (var vo in voiceOptions)
        {
            string vid = vo.Id;
            string vShort = vo.ShortLabel;
            var mi = new MenuFlyoutItem { Text = vo.FullLabel };
            mi.Click += (_, _) => { voiceStyle = vid; hostVoiceLabel.Text = vShort; };
            voiceMenu.Items.Add(mi);
        }
        var hostSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        hostSp.Children.Add(YYIcons.Make("wave", 12, ThemeHelper.Brush(tint.Accent)));
        hostSp.Children.Add(hostVoiceLabel);
        hostSp.Children.Add(YYIcons.Make("chevron-down", 9.5, ThemeHelper.Brush(p.T3)));
        var hostVoiceBtn = new Button
        {
            Height = 30,
            MinHeight = 30,
            Padding = new Thickness(11, 0, 10, 0),
            CornerRadius = new CornerRadius(15),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            UseSystemFocusVisuals = false,
            Content = hostSp,
            Flyout = voiceMenu,
        };

        var brewRing = new ProgressRing { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed };
        var statusNote = new TextBlock
        {
            Text = _library.Companion.IsConfigured ? L10n.T("已连接书伴模型 · 生成双人对谈文稿") : L10n.T("未接书伴模型 · 将用本机离线摘要成稿"),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var footerLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { hostVoiceBtn, brewRing, statusNote } };

        Button? cancelBtn = null;
        Button? startBtn = null;

        async Task RunGenerateInsideSheetAsync()
        {
            if (isBrewing) return;
            if (selectedBookIds.Count == 0 && chosenKind != StudioKind.VocabRadio && allBooks.Length > 0)
            {
                statusNote.Text = L10n.T("请先点选至少一本参考书");
                statusNote.Foreground = ThemeHelper.Brush(p.Danger);
                return;
            }

            isBrewing = true;
            _library.Prefs.PodcastStudioVoiceStyle = voiceStyle;
            _library.SavePrefs();

            hostVoiceBtn.Visibility = Visibility.Collapsed;
            brewRing.Visibility = Visibility.Visible;
            brewRing.IsActive = true;
            statusNote.Foreground = ThemeHelper.Brush(p.T2);
            statusNote.Text = _library.Companion.IsConfigured
                ? L10n.T("正在提取已读章节与划线，酿制双人对谈文稿…")
                : L10n.T("正在整理已读内容，生成本机书房电台文稿…");
            if (cancelBtn is not null) cancelBtn.IsEnabled = false;
            if (startBtn is not null)
            {
                startBtn.IsEnabled = false;
                startBtn.Content = L10n.T("正在酿制…");
            }

            try
            {
                var created = await _library.Podcasts.GenerateStudioIssueAsync(
                    chosenKind,
                    selectedBookIds.ToArray(),
                    durationMin,
                    string.IsNullOrWhiteSpace(topicBox.Text) ? null : topicBox.Text.Trim(),
                    noSpoiler: noSpoiler,
                    voiceStyle: voiceStyle);

                dlgRef?.Hide();
                OpenIssueAt(created.Id, 0, autoPlay: true);
                _shelf.RefreshBooks();
            }
            catch (Exception ex)
            {
                isBrewing = false;
                brewRing.IsActive = false;
                brewRing.Visibility = Visibility.Collapsed;
                hostVoiceBtn.Visibility = Visibility.Visible;
                statusNote.Text = $"酿制失败：{ex.Message}";
                statusNote.Foreground = ThemeHelper.Brush(p.Danger);
                if (cancelBtn is not null) cancelBtn.IsEnabled = true;
                if (startBtn is not null)
                {
                    startBtn.IsEnabled = true;
                    startBtn.Content = L10n.T("开始生成");
                }
            }
        }

        cancelBtn = YYChrome.MakeDialogButton(L10n.T("取消"), () => { if (!isBrewing) dlgRef?.Hide(); }, dark, _library.Prefs.Tint, "secondary", height: 30, horizontalPadding: 14);
        startBtn = YYChrome.MakeDialogButton(L10n.T("开始生成"), () => _ = RunGenerateInsideSheetAsync(), dark, _library.Prefs.Tint, "dark", height: 30, horizontalPadding: 16);

        var bottomBar = new Grid { Padding = new Thickness(20, 2, 20, 16), ColumnSpacing = 10 };
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomBar.Children.Add(footerLeft);
        var rightActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancelBtn, startBtn } };
        Grid.SetColumn(rightActs, 1);
        bottomBar.Children.Add(rightActs);

        var sheetStack = new StackPanel { Spacing = 0, Children = { topHead, columnsGrid, bottomBar } };
        if (_shelf.WindowXamlRoot is not { } xr) return;
        dlgRef = YYChrome.CreateFluentDialog(xr, dark, 720, sheetStack, maxHeight: 760, tint: _library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
    }

    // ---------- 5. PodcastAddSheet（500×420 无框暖纸弹窗 1:1 PodcastAddSheet.swift） ----------

    public async Task ShowAddPodcastSheetAsync(string initialQuery = "")
    {
        bool dark = ThemeHelper.IsDark(_shelf.WindowXamlRoot?.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());

        var subscribedFeeds = _library.Podcasts.GetChannels().Select(c => c.FeedUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ContentDialog? dlgRef = null;
        bool busyAdding = false;

        var root = new StackPanel { Spacing = 0 };

        // 1. Songti Header + Subtitle + aligned porcelain × button
        var topHead = new Grid { Padding = new Thickness(20, 16, 20, 12), ColumnSpacing = 8 };
        topHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headTitles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        headTitles.Children.Add(new TextBlock { Text = L10n.T("添加节目"), FontFamily = serif, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        headTitles.Children.Add(new TextBlock { Text = L10n.T("搜播客目录、贴单集或导入本地音频"), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) });
        topHead.Children.Add(headTitles);

        var closeBtn = YYChrome.MakeDialogCloseButton(() => dlgRef?.Hide(), dark);
        Grid.SetColumn(closeBtn, 1);
        topHead.Children.Add(closeBtn);
        root.Children.Add(topHead);

        // 2. Recessed matte command input with clear button + `Ctrl+V` / `放入收听台` dark pill
        var queryBox = new TextBox
        {
            Text = initialQuery,
            PlaceholderText = L10n.T("搜节目名，或贴 RSS / 单集网页 / 音频链接…"),
            FontSize = 13,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 6, 0, 4),
        };
        YYChrome.StripTextBoxChrome(queryBox);

        if (string.IsNullOrWhiteSpace(initialQuery))
        {
            try
            {
                var dpv = Clipboard.GetContent();
                if (dpv.Contains(StandardDataFormats.Text))
                {
                    string clip = (await dpv.GetTextAsync()).Trim();
                    if (LooksLikePodcastUrl(clip) && !clip.Contains('\n') && clip.Length < 400)
                    {
                        queryBox.Text = clip;
                    }
                }
            }
            catch { }
        }

        var leadIcon = new ContentPresenter
        {
            VerticalAlignment = VerticalAlignment.Center,
            Content = YYIcons.Make(LooksLikePodcastUrl(queryBox.Text) ? "link-chain" : "search", 12.5, ThemeHelper.Brush(p.T3)),
        };
        var clearBtn = new Button
        {
            Width = 18,
            Height = 18,
            MinWidth = 18,
            MinHeight = 18,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Visibility = string.IsNullOrEmpty(queryBox.Text) ? Visibility.Collapsed : Visibility.Visible,
            Content = YYIcons.Make("xmark", 9, ThemeHelper.Brush(p.T3)),
        };

        var rightActionHost = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var statusTb = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(tint.Accent),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(22, 8, 22, 0),
        };
        var resultsStack = new StackPanel { Spacing = 0 };

        void RenderHitRows(IReadOnlyList<PodcastSearchHitDto> hits)
        {
            for (int i = 0; i < hits.Count; i++)
            {
                if (i > 0) resultsStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
                var hit = hits[i];
                bool added = subscribedFeeds.Contains(hit.FeedUrl);

                var row = new Grid { Padding = new Thickness(14, 9, 14, 9), ColumnSpacing = 11 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                row.Children.Add(BuildShowMark(hit.Title, hit.ArtworkUrl, isStudio: false, 34, p, tint));

                var meta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                meta.Children.Add(new TextBlock { Text = hit.Title, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                string sub = string.Join(" · ", new[] { hit.Author, hit.Genre }.Where(x => !string.IsNullOrWhiteSpace(x)));
                meta.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(meta, 1);
                row.Children.Add(meta);

                if (added)
                {
                    var addedTb = new TextBlock { Text = L10n.T("已订阅"), FontSize = 11, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(addedTb, 2);
                    row.Children.Add(addedTb);
                }
                else
                {
                    var subBtn = MakePaperPill(L10n.T("订阅"), accent: true, p, tint);
                    subBtn.Height = 25;
                    string feedUrl = hit.FeedUrl;
                    string showTitle = hit.Title;
                    subBtn.Click += async (_, _) =>
                    {
                        subBtn.IsEnabled = false;
                        subBtn.Content = L10n.T("订阅中…");
                        try
                        {
                            await _library.Podcasts.AddUrlOrFeedAsync(feedUrl, putLatestOnDesk: true);
                            subscribedFeeds.Add(feedUrl);
                            subBtn.Content = L10n.T("已订阅");
                            statusTb.Text = $"已订阅《{showTitle}》，最新一期已放到收听台";
                            statusTb.Foreground = ThemeHelper.Brush(tint.Accent);
                            statusTb.Visibility = Visibility.Visible;
                            _shelf.RefreshBooks();
                        }
                        catch (Exception ex)
                        {
                            subBtn.IsEnabled = true;
                            subBtn.Content = L10n.T("订阅");
                            statusTb.Text = $"订阅失败：{ex.Message}";
                            statusTb.Foreground = ThemeHelper.Brush(p.Danger);
                            statusTb.Visibility = Visibility.Visible;
                        }
                    };
                    Grid.SetColumn(subBtn, 2);
                    row.Children.Add(subBtn);
                }
                resultsStack.Children.Add(row);
            }
        }

        void RenderEmptyGuide()
        {
            resultsStack.Children.Clear();
            var guide = new StackPanel { Spacing = 10, Padding = new Thickness(16, 14, 16, 12) };
            guide.Children.Add(new TextBlock { Text = L10n.T("三种添加方式"), FontFamily = serif, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });

            (string Icon, string Lead, string Detail)[] hints =
            [
                ("grid-all", L10n.T("搜节目名"), L10n.T("直连公开播客目录找到 RSS，回车即可搜索并一键订阅。")),
                ("link-chain", L10n.T("贴单集链接"), L10n.T("支持小宇宙、Apple Podcasts、网页或音频直链，不强迫订阅整档节目。")),
                ("wave", L10n.T("导入 OPML / 音频"), L10n.T("一键迁入现有订阅或本地 .mp3 / .m4a 讲座录音，零未读红点焦虑。")),
            ];
            foreach (var (ic, lead, det) in hints)
            {
                var hr = new Grid { ColumnSpacing = 10 };
                hr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                hr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                hr.Children.Add(new Border
                {
                    Width = 24,
                    Height = 24,
                    CornerRadius = new CornerRadius(7),
                    Background = ThemeHelper.Brush(tint.Soft),
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = YYIcons.Make(ic, 12.5, ThemeHelper.Brush(tint.Accent)),
                });
                var hText = new StackPanel { Spacing = 2 };
                hText.Children.Add(new TextBlock { Text = lead, FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                hText.Children.Add(new TextBlock { Text = det, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap, LineHeight = 17 });
                Grid.SetColumn(hText, 1);
                hr.Children.Add(hText);
                guide.Children.Add(hr);
            }
            resultsStack.Children.Add(guide);

            _ = Task.Run(async () =>
            {
                var charts = await _library.Podcasts.FetchItunesTopChartsAsync();
                _shelf.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_disposed) return;
                    if (!string.IsNullOrWhiteSpace(queryBox.Text) && !LooksLikePodcastUrl(queryBox.Text)) return;
                    if (resultsStack.Children.Count <= 1 && charts.Count > 0)
                    {
                        resultsStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
                        var chartHead = new Grid { Padding = new Thickness(14, 10, 14, 4) };
                        chartHead.Children.Add(new TextBlock { Text = L10n.T("公开精选榜"), FontFamily = serif, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                        resultsStack.Children.Add(chartHead);
                        RenderHitRows(charts.Take(4).ToArray());
                    }
                });
            });
        }

        async Task SubmitQueryAsync()
        {
            string q = queryBox.Text.Trim();
            if (string.IsNullOrEmpty(q) || busyAdding) { RenderEmptyGuide(); return; }

            if (LooksLikePodcastUrl(q))
            {
                busyAdding = true;
                UpdateCommandRightSlot();
                statusTb.Text = L10n.T("正在解析链接并获取节目…");
                statusTb.Foreground = ThemeHelper.Brush(tint.Accent);
                statusTb.Visibility = Visibility.Visible;
                try
                {
                    var (_, single) = await _library.Podcasts.AddUrlOrFeedAsync(q, putLatestOnDesk: true);
                    if (single is not null) OpenIssueAt(single.Id);
                    _shelf.RefreshBooks();
                    _shelf.ShowToast(L10n.T("已放入收听台"), canUndo: false);
                    dlgRef?.Hide();
                }
                catch (Exception ex)
                {
                    statusTb.Text = $"解析没成功：{ex.Message}";
                    statusTb.Foreground = ThemeHelper.Brush(p.Danger);
                }
                finally
                {
                    busyAdding = false;
                    UpdateCommandRightSlot();
                }
                return;
            }

            resultsStack.Children.Clear();
            var searchingSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 72, 0, 72) };
            searchingSp.Children.Add(new ProgressRing { Width = 16, Height = 16, IsActive = true });
            searchingSp.Children.Add(new TextBlock { Text = L10n.T("正在搜索播客目录…"), FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
            resultsStack.Children.Add(searchingSp);

            var hits = await _library.Podcasts.SearchItunesAsync(q);
            resultsStack.Children.Clear();
            if (hits.Count == 0)
            {
                statusTb.Text = L10n.T("没搜到匹配的公开播客，也可以直接粘贴节目的 RSS 链接。");
                statusTb.Foreground = ThemeHelper.Brush(p.T3);
                statusTb.Visibility = Visibility.Visible;
                RenderEmptyGuide();
                return;
            }

            statusTb.Visibility = Visibility.Collapsed;
            var hitHead = new Grid { Padding = new Thickness(14, 11, 14, 5) };
            hitHead.Children.Add(new TextBlock { Text = L10n.T("搜索结果"), FontFamily = serif, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            hitHead.Children.Add(new TextBlock { Text = $"{hits.Count} 档节目", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            resultsStack.Children.Add(hitHead);
            RenderHitRows(hits);
        }

        void UpdateCommandRightSlot()
        {
            string q = queryBox.Text.Trim();
            bool isUrl = LooksLikePodcastUrl(q);
            leadIcon.Content = YYIcons.Make(isUrl ? "link-chain" : "search", 12.5, ThemeHelper.Brush(isUrl ? tint.Accent : p.T3));
            clearBtn.Visibility = string.IsNullOrEmpty(q) ? Visibility.Collapsed : Visibility.Visible;

            if (isUrl)
            {
                var putBtn = YYChrome.MakeDialogButton(busyAdding ? L10n.T("解析中…") : L10n.T("放入收听台"), () => _ = SubmitQueryAsync(), dark, _library.Prefs.Tint, "dark", height: 24, horizontalPadding: 11);
                putBtn.FontSize = 11.5;
                putBtn.IsEnabled = !busyAdding;
                rightActionHost.Content = putBtn;
            }
            else
            {
                var pasteBtn = new Button
                {
                    Content = "Ctrl+V",
                    Height = 20,
                    MinHeight = 20,
                    Padding = new Thickness(6, 0, 6, 0),
                    CornerRadius = new CornerRadius(5),
                    FontSize = 10,
                    FontWeight = FontWeights.Medium,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    UseSystemFocusVisuals = false,
                };
                ToolTipService.SetToolTip(pasteBtn, L10n.T("粘贴剪贴板链接"));
                pasteBtn.Click += async (_, _) =>
                {
                    try
                    {
                        var dpv = Clipboard.GetContent();
                        if (dpv.Contains(StandardDataFormats.Text))
                        {
                            queryBox.Text = (await dpv.GetTextAsync()).Trim();
                            queryBox.Focus(FocusState.Programmatic);
                        }
                    }
                    catch { }
                };
                rightActionHost.Content = pasteBtn;
            }
        }

        clearBtn.Click += (_, _) =>
        {
            queryBox.Text = "";
            statusTb.Visibility = Visibility.Collapsed;
            RenderEmptyGuide();
            queryBox.Focus(FocusState.Programmatic);
        };
        queryBox.TextChanged += (_, _) => UpdateCommandRightSlot();
        queryBox.KeyDown += async (_, ke) =>
        {
            if (ke.Key == VirtualKey.Enter) { ke.Handled = true; await SubmitQueryAsync(); }
        };
        UpdateCommandRightSlot();

        var searchBarGrid = new Grid { Height = 36, Padding = new Thickness(12, 0, 10, 0), ColumnSpacing = 8 };
        searchBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchBarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchBarGrid.Children.Add(leadIcon);
        Grid.SetColumn(queryBox, 1);
        searchBarGrid.Children.Add(queryBox);
        Grid.SetColumn(clearBtn, 2);
        searchBarGrid.Children.Add(clearBtn);
        Grid.SetColumn(rightActionHost, 3);
        searchBarGrid.Children.Add(rightActionHost);

        var searchBarBorder = new Border
        {
            Margin = new Thickness(20, 0, 20, 0),
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(255, 236, 234, 227)),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = searchBarGrid,
        };
        queryBox.GotFocus += (_, _) => { searchBarBorder.BorderBrush = ThemeHelper.Brush(tint.Edge); searchBarBorder.BorderThickness = new Thickness(0.75); };
        queryBox.LostFocus += (_, _) => { searchBarBorder.BorderBrush = ThemeHelper.Brush(p.Line); searchBarBorder.BorderThickness = new Thickness(0.5); };
        root.Children.Add(searchBarBorder);
        root.Children.Add(statusTb);

        // 3. Pure-white PodcastStationeryCard results / empty guide
        RenderEmptyGuide();
        root.Children.Add(new Border
        {
            Margin = new Thickness(20, 12, 20, 0),
            Height = 236,
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                Content = resultsStack,
            },
        });

        // 4. Quiet Homepage-matched footer pills
        var footGrid = new Grid { Padding = new Thickness(20, 12, 20, 16) };
        var footPills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var opmlBtn = MakePaperPill(L10n.T("导入 OPML"), accent: false, p, tint);
        opmlBtn.Height = 28;
        opmlBtn.Click += async (_, _) =>
        {
            string? path = await NativePickerHelper.PickSingleFileAsync(_shelf.OwnerWindow, [".opml", ".xml"]);
            if (string.IsNullOrEmpty(path)) return;
            statusTb.Text = L10n.T("正在批量导入 OPML 订阅源…");
            statusTb.Foreground = ThemeHelper.Brush(tint.Accent);
            statusTb.Visibility = Visibility.Visible;
            try
            {
                int cnt = await _library.Podcasts.ImportOpmlAsync(path);
                statusTb.Text = $"已导入 {cnt} 档播客到报刊栏";
                _shelf.RefreshBooks();
            }
            catch (Exception ex)
            {
                statusTb.Text = $"OPML 导入失败：{ex.Message}";
                statusTb.Foreground = ThemeHelper.Brush(p.Danger);
            }
        };

        var localAudioBtn = MakePaperPill(L10n.T("本地音频"), accent: false, p, tint);
        localAudioBtn.Height = 28;
        localAudioBtn.Click += async (_, _) =>
        {
            string? path = await NativePickerHelper.PickSingleFileAsync(_shelf.OwnerWindow, [".mp3", ".m4a", ".wav", ".flac"]);
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var iss = await _library.Podcasts.ImportLocalAudioAsync(path);
                dlgRef?.Hide();
                OpenIssueAt(iss.Id);
                _shelf.RefreshBooks();
            }
            catch (Exception ex)
            {
                statusTb.Text = ex.Message;
                statusTb.Foreground = ThemeHelper.Brush(p.Danger);
                statusTb.Visibility = Visibility.Visible;
            }
        };

        footPills.Children.Add(opmlBtn);
        footPills.Children.Add(localAudioBtn);
        footGrid.Children.Add(footPills);
        footGrid.Children.Add(new TextBlock
        {
            Text = L10n.T("粘贴链接回车即可收单集"),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(footGrid);

        if (_shelf.WindowXamlRoot is not { } xr) return;
        dlgRef = YYChrome.CreateFluentDialog(xr, dark, 500, root, maxHeight: 460, tint: _library.Prefs.Tint);
        if (!string.IsNullOrWhiteSpace(initialQuery) && !LooksLikePodcastUrl(initialQuery))
            _ = SubmitQueryAsync();

        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
    }

    // ---------- Shared Visual & Literary Formatting Helpers ----------

    private static bool LooksLikePodcastUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string t = raw.Trim();
        return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || t.Contains("xiaoyuzhou", StringComparison.OrdinalIgnoreCase)
            || t.Contains("podcasts.apple.com", StringComparison.OrdinalIgnoreCase)
            || t.Contains(".xml", StringComparison.OrdinalIgnoreCase)
            || t.Contains("/feed", StringComparison.OrdinalIgnoreCase);
    }

    private static string StudioKindTitle(StudioKind kind) => kind switch
    {
        StudioKind.WeeklyDigest => L10n.T("书房周刊"),
        StudioKind.BookGuide => L10n.T("难书导读"),
        StudioKind.Roundtable => L10n.T("跨书圆桌"),
        StudioKind.VocabRadio => L10n.T("生词外语"),
        _ => L10n.T("书房电台"),
    };

    private static string TrackLabel(PodcastTrack track, PodcastIssue? issue = null)
    {
        bool isEnglish = issue is null
            || !(issue.Language ?? "").StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            || issue.StudioKind == StudioKind.VocabRadio
            || issue.Fidelity == PodcastFidelity.L3CrossLingual;
        return track switch
        {
            PodcastTrack.Dub => L10n.T("原声线中文"),
            PodcastTrack.Coach => L10n.T("双语助教"),
            _ => isEnglish ? L10n.T("英文原声") : L10n.T("原声"),
        };
    }

    private static Border BuildTrackSegmentedControl(
        PodcastTrack current,
        bool compact,
        YYPalette p,
        YYTintColors tint,
        Action<PodcastTrack> onSelect,
        bool isEnglish = true)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        (PodcastTrack Tr, string Label)[] opts =
        [
            (PodcastTrack.Original, compact ? L10n.T("原声") : (isEnglish ? L10n.T("英文原声") : L10n.T("原声"))),
            (PodcastTrack.Dub, compact ? L10n.T("中文") : L10n.T("原声线中文")),
            (PodcastTrack.Coach, compact ? L10n.T("双语") : L10n.T("双语助教")),
        ];
        foreach (var (tr, lbl) in opts)
        {
            bool on = current == tr;
            var b = new Button
            {
                Content = lbl,
                Height = 22,
                MinHeight = 22,
                Padding = new Thickness(compact ? 7 : 8.5, 0, compact ? 7 : 8.5, 0),
                CornerRadius = new CornerRadius(11),
                FontSize = compact ? 10.5 : 11,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T3),
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                BorderBrush = on ? ThemeHelper.Brush(p.Line) : null,
                BorderThickness = new Thickness(on ? 0.5 : 0),
                UseSystemFocusVisuals = false,
            };
            var next = tr;
            b.Click += (_, _) => onSelect(next);
            row.Children.Add(b);
        }

        return new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(2),
            Child = row,
        };
    }

    private static Border BuildShowMark(string title, string? artworkUrl, bool isStudio, double size, YYPalette p, YYTintColors tint)
    {
        double radius = Math.Round(size * 0.26, 1);
        if (isStudio)
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(radius),
                Background = ThemeHelper.Brush(tint.Soft),
                BorderBrush = ThemeHelper.Brush(tint.Edge),
                BorderThickness = new Thickness(0.5),
                Child = YYIcons.Make("book-open", Math.Max(10, size * 0.5), ThemeHelper.Brush(tint.Accent)),
            };
        }

        if (!string.IsNullOrWhiteSpace(artworkUrl) && Uri.TryCreate(artworkUrl, UriKind.Absolute, out var artUri))
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(radius),
                Background = ThemeHelper.Brush(p.Inset),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                Child = new Image
                {
                    Source = new BitmapImage(artUri) { DecodePixelWidth = Math.Max(36, (int)Math.Ceiling(size * 2)) },
                    Stretch = Stretch.UniformToFill,
                },
            };
        }

        Color[] palette =
        [
            Color.FromArgb(255, 198, 126, 58),
            Color.FromArgb(255, 74, 118, 92),
            Color.FromArgb(255, 86, 98, 142),
            Color.FromArgb(255, 168, 84, 78),
            Color.FromArgb(255, 124, 102, 78),
        ];
        int hash = 0;
        foreach (char ch in title) hash = unchecked(hash * 31 + ch);
        var bg = palette[Math.Abs(hash) % palette.Length];
        string glyph = ExtractMonogram(title);

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(radius),
            Background = ThemeHelper.Brush(bg),
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = Math.Max(9.5, size * (glyph.Length > 1 ? 0.38 : 0.46)),
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static string ExtractMonogram(string title)
    {
        string trimmed = (title ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) return "播";
        foreach (char ch in trimmed)
        {
            if (ch >= 0x4E00 && ch <= 0x9FFF) return ch.ToString();
        }
        var words = trimmed.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && char.IsLetter(words[0][0]) && char.IsLetter(words[1][0]))
            return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
        return char.ToUpperInvariant(trimmed[0]).ToString();
    }

    /// <summary>
    /// Formats a Chinese literary date matching <c>PodcastFormat.editorialDate()</c>
    /// (e.g. <c>十一月廿五日 · 星期三</c>).
    /// </summary>
    private static string FormatChineseLiteraryDate(DateTime dt)
    {
        string[] months = ["", "一月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "十一月", "十二月"];
        string[] digits = ["", "一", "二", "三", "四", "五", "六", "七", "八", "九"];
        int d = Math.Clamp(dt.Day, 1, 31);
        string dayStr = d switch
        {
            10 => "十",
            20 => "二十",
            30 => "三十",
            < 10 => digits[d],
            < 20 => "十" + digits[d - 10],
            < 30 => "廿" + digits[d - 20],
            _ => "卅" + digits[d - 30],
        };
        string[] wkNames = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];
        return $"{months[Math.Clamp(dt.Month, 1, 12)]}{dayStr}日 · {wkNames[(int)dt.DayOfWeek]}";
    }

    private static Button MakeCompactTextButton(string label, YYPalette p) => new()
    {
        Content = label,
        Height = 26,
        MinHeight = 26,
        MinWidth = 0,
        Padding = new Thickness(5, 0, 5, 0),
        CornerRadius = new CornerRadius(6),
        FontSize = 11,
        FontWeight = FontWeights.Medium,
        Foreground = ThemeHelper.Brush(p.T2),
        Background = new SolidColorBrush(Colors.Transparent),
        BorderThickness = new Thickness(0),
        UseSystemFocusVisuals = false,
    };

    private static Button MakeCircleIconButton(string icon, string tooltip, YYPalette p, bool showDot = false, YYTintColors? tint = null)
    {
        var grid = new Grid();
        grid.Children.Add(YYIcons.Make(icon, 13, ThemeHelper.Brush(p.T1)));
        if (showDot && tint is YYTintColors tc)
        {
            grid.Children.Add(new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = ThemeHelper.Brush(tc.Accent),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
            });
        }

        var b = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            UseSystemFocusVisuals = false,
            Content = grid,
        };
        ToolTipService.SetToolTip(b, tooltip);
        return b;
    }

    private static Button MakePaperPill(string text, bool accent, YYPalette p, YYTintColors tint) => new()
    {
        Content = text,
        Height = 28,
        MinHeight = 28,
        Padding = new Thickness(11, 0, 11, 0),
        CornerRadius = new CornerRadius(6),
        FontSize = 11.5,
        FontWeight = accent ? FontWeights.SemiBold : FontWeights.Medium,
        Foreground = ThemeHelper.Brush(accent ? tint.Accent : p.T1),
        Background = ThemeHelper.Brush(accent ? tint.Soft : p.Card),
        BorderBrush = ThemeHelper.Brush(accent ? tint.Edge : p.Line),
        BorderThickness = new Thickness(1),
        UseSystemFocusVisuals = false,
    };

    private static string FormatClock(double sec)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
