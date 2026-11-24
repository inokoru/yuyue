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
/// - 2-column Harbor Stationery workspace + 386px right-edge 4-tab drawer (<c>原声书摘 / 稍后听 / 声音卡 / 本机整理</c>)
/// - Dual-voice Studio turn-by-turn synthesis (<c>林深 / 苏晚</c>, <c>Clara &amp; David</c>, cloned voices)
/// - <c>-24 dB</c> audio ducking + <c>-3s</c> rewind resume + mic voice-dictation &amp; spoken answer during <c>🎙 插话</c>
/// - <see cref="YuyueCoreMethods.ResolveClipWindow"/> natural sentence-snapped <c>原声划线</c> with local WAV micro-clips + clickable vocab popover
/// - Live iTunes Podcast Directory search &amp; top charts, OPML import, single-episode extraction, and on-device transcription.
/// </summary>
public sealed class PodcastWorkspaceBuilder : IDisposable
{
    private readonly ShelfWindow _shelf;
    private readonly LibraryService _library;
    private readonly AudioPlayer _ttsPlayer;
    private readonly MediaPlayer _mediaPlayer;
    private readonly MediaPlayer _clipPreviewPlayer;
    private readonly DispatcherTimer _mediaPosTimer;

    public string? OpenIssueId { get; set; }

    public void OpenIssueAt(string issueId, double? seekSec = null, bool autoPlay = false)
    {
        OpenIssueId = issueId;
        var issue = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == issueId);
        if (issue is not null)
        {
            var sents = LoadOrSynthesizeTranscript(issue);
            if (seekSec is double sec && sec >= 0)
            {
                int idx = Array.FindLastIndex(sents, s => sec >= s.T0);
                _activeTurnIdx = Math.Max(0, idx);
                try { _library.Core.PodcastSavePlayback(issueId, sec, null); } catch { }
            }
            if (!SupportsTrackSwitch(issue, sents))
            {
                _track = PodcastTrack.Original;
            }
            if (autoPlay && sents.Length > 0)
            {
                PlayIssueAtTurn(issue, sents, Math.Clamp(_activeTurnIdx, 0, sents.Length - 1));
            }
        }
    }
    private string _selectedShowKey = "all"; // all | studio | channelId
    private bool _allCollapsed;
    private readonly HashSet<string> _collapsedGroups = [];
    private readonly HashSet<string> _expandedSkipIds = [];
    private readonly HashSet<int> _peekedTurnIndices = [];

    // Right-edge 386px Stationery Drawer (1:1 PodcastDrawerTab: clips | later | voices | local)
    private bool _drawerOpen;
    private string _drawerTab = "clips";
    private bool _hasNewClipDot;

    // Inline PodcastReaderView & Playback state
    private PodcastTrack _track = PodcastTrack.Original;
    private bool _blindListen;
    private int _activeTurnIdx;
    private bool _playing;
    private bool _usingExternalMedia;
    private double _rate = 1.0;
    private string? _playingIssueId;
    private MagazineSentence[] _activeSentences = [];
    private string? _transcribingIssueId;
    private string _transcribeStatus = "";
    private TextBox? _stationCmdBox;
    private bool _isPreviewingInterruptAnswer;

    public PodcastWorkspaceBuilder(ShelfWindow shelf, LibraryService library)
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
            _playing = false;
            _usingExternalMedia = false;
            _library.SystemIntegration.SetAudioPlaying(_shelf, false);
            UpdateSmtcState(null, playing: false);
            _shelf.RefreshCurrentSubPage();
        });

        _clipPreviewPlayer = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech, AutoPlay = false };

        _mediaPosTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
        _mediaPosTimer.Tick += OnMediaPosTick;
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        _shelf.DispatcherQueue.TryEnqueue(() =>
        {
            if (_playingIssueId is null) return;
            var issue = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == _playingIssueId);
            if (issue is null) return;
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    if (!_playing) { PlayIssueAtTurn(issue, _activeSentences, _activeTurnIdx); _shelf.RefreshCurrentSubPage(); }
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    if (_playing) { StopPlayback(); _shelf.RefreshCurrentSubPage(); }
                    break;
                case SystemMediaTransportControlsButton.Next:
                    StepPlayback(issue, _activeSentences, forward: true);
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    StepPlayback(issue, _activeSentences, forward: false);
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
        _mediaPosTimer.Stop();
        _ttsPlayer.SentenceFinished -= OnStudioTurnFinished;
        _ttsPlayer.Dispose();
        _library.SystemIntegration.SetAudioPlaying(_shelf, false);
        try { _mediaPlayer.SystemMediaTransportControls.ButtonPressed -= OnSmtcButtonPressed; } catch { }
        try { _mediaPlayer.Pause(); _mediaPlayer.Dispose(); } catch { }
        try { _clipPreviewPlayer.Pause(); _clipPreviewPlayer.Dispose(); } catch { }
    }

    public bool SupportsTrackSwitch(PodcastIssue? issue, MagazineSentence[]? preloaded = null)
    {
        if (issue is null) return false;
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

            var sents = _activeSentences.Length > 0 && _playingIssueId == issueId
                ? _activeSentences
                : LoadOrSynthesizeTranscript(issue);

            switch (key)
            {
                case VirtualKey.Space:
                    if (_playing) StopPlayback();
                    else PlayIssueAtTurn(issue, sents, _activeTurnIdx);
                    _shelf.RefreshCurrentSubPage();
                    return true;
                case VirtualKey.H:
                    _ = ClipCurrentTurnAsync(issue.Id);
                    return true;
                case VirtualKey.I:
                {
                    double sec = sents.Length > _activeTurnIdx ? sents[_activeTurnIdx].T0 : issue.PositionSec;
                    _ = ShowInterruptDialogAsync(issue, sec, sents.ElementAtOrDefault(_activeTurnIdx)?.TextOrig);
                    return true;
                }
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
                    if (_drawerOpen) { _drawerOpen = false; _shelf.RefreshCurrentSubPage(); }
                    else { StopPlayback(); OpenIssueId = null; _shelf.RefreshCurrentSubPage(); }
                    return true;
                default:
                    return false;
            }
        }

        // Station desk shortcuts
        if (key == (VirtualKey)191 && _stationCmdBox is not null) // '/'
        {
            _stationCmdBox.Focus(FocusState.Programmatic);
            return true;
        }
        if (key == VirtualKey.Escape && _drawerOpen)
        {
            _drawerOpen = false;
            _shelf.RefreshCurrentSubPage();
            return true;
        }
        if (key == VirtualKey.Space)
        {
            var target = (_playingIssueId is not null
                ? _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == _playingIssueId)
                : null)
                ?? _library.Podcasts.GetIssues().FirstOrDefault();
            if (target is not null)
            {
                if (_playing) StopPlayback();
                else
                {
                    var sents = LoadOrSynthesizeTranscript(target);
                    PlayIssueAtTurn(target, sents, Math.Clamp(_activeTurnIdx, 0, Math.Max(0, sents.Length - 1)));
                }
                _shelf.RefreshCurrentSubPage();
                return true;
            }
        }
        if (key == VirtualKey.H && _playingIssueId is string pid)
        {
            _ = ClipCurrentTurnAsync(pid);
            return true;
        }
        if (key == VirtualKey.I && _playingIssueId is string pid2)
        {
            var cur = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == pid2);
            if (cur is not null)
            {
                var sents = _activeSentences.Length > 0 ? _activeSentences : LoadOrSynthesizeTranscript(cur);
                double sec = sents.Length > _activeTurnIdx ? sents[_activeTurnIdx].T0 : cur.PositionSec;
                _ = ShowInterruptDialogAsync(cur, sec, sents.ElementAtOrDefault(_activeTurnIdx)?.TextOrig);
                return true;
            }
        }
        return false;
    }

    // ---------- Top Header ----------

    public void PopulateTopHeader(Panel leftAfterToggleHost, Panel rightHost, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        if (OpenIssueId is string issueId)
        {
            var curIssue = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == issueId);

            // Left: < 返回收听台
            var backSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            backSp.Children.Add(YYIcons.Make("chevron-left", 11.5, ThemeHelper.Brush(p.T1)));
            backSp.Children.Add(new TextBlock { Text = "返回收听台", FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
            var backBtn = new Button
            {
                Height = 30,
                MinHeight = 30,
                Padding = new Thickness(10, 0, 12, 0),
                CornerRadius = new CornerRadius(15),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                UseSystemFocusVisuals = false,
                Content = backSp,
            };
            backBtn.Click += (_, _) =>
            {
                StopPlayback();
                OpenIssueId = null;
                _shelf.RefreshCurrentSubPage();
            };
            leftAfterToggleHost.Children.Add(backBtn);

            // Right: Track segmented control (only when episode supports translation / coach notes) + 盲听 + 稍后听 + 原声划线 + 书摘抽屉
            bool canSwitchTrack = SupportsTrackSwitch(curIssue, _playingIssueId == issueId && _activeSentences.Length > 0 ? _activeSentences : null);
            if (!canSwitchTrack && _track != PodcastTrack.Original)
            {
                _track = PodcastTrack.Original;
            }
            if (canSwitchTrack)
            {
                var trackGrid = new Grid { ColumnSpacing = 2 };
                (PodcastTrack Tr, string Lbl)[] tracks =
                [
                    (PodcastTrack.Original, "原声"),
                    (PodcastTrack.Dub, "中文译配"),
                    (PodcastTrack.Coach, "双语助教"),
                ];
                for (int i = 0; i < tracks.Length; i++)
                {
                    trackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var (tr, lbl) = tracks[i];
                    bool on = _track == tr;
                    var tb = new Button
                    {
                        Content = lbl,
                        Height = 24,
                        MinHeight = 24,
                        Padding = new Thickness(9, 0, 9, 0),
                        FontSize = 11,
                        FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                        Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(7),
                        UseSystemFocusVisuals = false,
                    };
                    var nextTr = tr;
                    tb.Click += (_, _) =>
                    {
                        SwitchTrack(issueId, curIssue, nextTr);
                    };
                    Grid.SetColumn(tb, i);
                    trackGrid.Children.Add(tb);
                }
                rightHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(p.Inset),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(2),
                    Child = trackGrid,
                });
            }

            var blindBtn = new Button
            {
                Content = "盲听",
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(14),
                FontSize = 11.5,
                FontWeight = _blindListen ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ThemeHelper.Brush(_blindListen ? tint.Accent : p.T2),
                Background = ThemeHelper.Brush(_blindListen ? tint.Soft : p.Card),
                BorderBrush = ThemeHelper.Brush(_blindListen ? tint.Edge : p.Line),
                BorderThickness = new Thickness(1),
                UseSystemFocusVisuals = false,
            };
            blindBtn.Click += (_, _) => { _blindListen = !_blindListen; _peekedTurnIndices.Clear(); _shelf.RefreshCurrentSubPage(); };
            rightHost.Children.Add(blindBtn);

            if (curIssue is not null)
            {
                bool inLater = curIssue.Placement == PodcastPlacement.Archive;
                var laterBtn = new Button
                {
                    Content = inLater ? "✓ 稍后听" : "稍后听",
                    Height = 28,
                    MinHeight = 28,
                    Padding = new Thickness(10, 0, 10, 0),
                    CornerRadius = new CornerRadius(14),
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(inLater ? tint.Accent : p.T2),
                    Background = ThemeHelper.Brush(inLater ? tint.Soft : p.Card),
                    BorderBrush = ThemeHelper.Brush(inLater ? tint.Edge : p.Line),
                    BorderThickness = new Thickness(1),
                    UseSystemFocusVisuals = false,
                };
                laterBtn.Click += (_, _) =>
                {
                    var nextPl = inLater ? PodcastPlacement.Desk : PodcastPlacement.Archive;
                    try { _library.Core.PodcastSetPlacement(issueId, nextPl); } catch { }
                    _shelf.ShowToast(inLater ? "已移回在听书桌" : "已存入稍后听", canUndo: false);
                    _shelf.RefreshCurrentSubPage();
                };
                rightHost.Children.Add(laterBtn);
            }

            var clipSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            clipSp.Children.Add(YYIcons.Make("bookmark", 11, ThemeHelper.Brush(tint.Accent), filled: true));
            clipSp.Children.Add(new TextBlock { Text = "原声划线", FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
            var clipBtn = new Button
            {
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(11, 0, 11, 0),
                CornerRadius = new CornerRadius(14),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                UseSystemFocusVisuals = false,
                Content = clipSp,
            };
            clipBtn.Click += async (_, _) => await ClipCurrentTurnAsync(issueId);
            rightHost.Children.Add(clipBtn);

            var drawerBtn = MakeCircleIconButton("note-card", "打开原声书摘与声音卡抽屉", p, _hasNewClipDot, tint);
            drawerBtn.Click += (_, _) => ToggleDrawer("clips");
            rightHost.Children.Add(drawerBtn);
            return;
        }

        // Workspace header (1:1 shelf-podcasts/1.png): compact icon-only segmented tray on left, single '+' circle on right
        var trayRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        void AddTrayChip(string key, string icon, string label)
        {
            bool on = _selectedShowKey == key;
            var chip = new Button
            {
                Width = 30,
                Height = 26,
                MinWidth = 30,
                MinHeight = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                BorderBrush = on ? ThemeHelper.Brush(dark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(14, 42, 43, 46)) : null,
                BorderThickness = new Thickness(on ? 0.5 : 0),
                UseSystemFocusVisuals = false,
                Content = YYIcons.Make(icon, 13, ThemeHelper.Brush(on ? tint.Accent : p.T2)),
            };
            ToolTipService.SetToolTip(chip, label);
            string k = key;
            chip.Click += (_, _) => { _selectedShowKey = k; _shelf.RefreshCurrentSubPage(); };
            trayRow.Children.Add(chip);
        }

        AddTrayChip("all", "grid-all", "全部节目");
        AddTrayChip("studio", "book-open", "书房电台");
        foreach (var ch in _library.Podcasts.GetChannels().Take(6))
            AddTrayChip(ch.Id, "radio", ch.Title);

        leftAfterToggleHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(3),
            Child = trayRow,
        });

        var channels = _library.Podcasts.GetChannels();
        if (channels.Count > 0)
        {
            var refBtn = MakeCircleIconButton("refresh", "刷新全部订阅", p);
            refBtn.Click += async (_, _) =>
            {
                refBtn.IsEnabled = false;
                int ok = 0;
                foreach (var c in channels)
                {
                    try { await _library.Podcasts.AddUrlOrFeedAsync(c.FeedUrl, putLatestOnDesk: false); ok++; } catch { }
                }
                refBtn.IsEnabled = true;
                _shelf.RefreshBooks();
                _shelf.ShowToast($"已刷新 {ok} 档订阅", canUndo: false);
            };
            rightHost.Children.Add(refBtn);
        }

        var addShowBtn = MakeCircleIconButton("plus", "添加节目 / 搜目录 / 导入 OPML", p);
        addShowBtn.Click += async (_, _) => await ShowAddPodcastSheetAsync();
        rightHost.Children.Add(addShowBtn);
    }

    private void ToggleDrawer(string tab)
    {
        if (_drawerOpen && _drawerTab == tab)
        {
            _drawerOpen = false;
        }
        else
        {
            _drawerTab = tab;
            _drawerOpen = true;
            if (tab is "clips" or "later") _hasNewClipDot = false;
        }
        _shelf.RefreshCurrentSubPage();
    }

    public void RenderInto(StackPanel host, bool dark)
    {
        host.Children.Clear();
        FrameworkElement mainBody;

        if (OpenIssueId is string iid)
        {
            var issue = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == iid);
            if (issue is not null)
            {
                mainBody = BuildInlineEpisodeReader(issue, dark);
                host.Children.Add(WrapWithOptionalDrawer(mainBody, dark));
                return;
            }
            OpenIssueId = null;
        }

        mainBody = BuildStationWorkspace(dark);
        host.Children.Add(WrapWithOptionalDrawer(mainBody, dark));
    }

    private FrameworkElement WrapWithOptionalDrawer(FrameworkElement mainBody, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        if (!_drawerOpen)
        {
            // 1:1 shelf-podcasts/1.png: two floating circular white drawer triggers centered on the far right edge
            var host = new Grid { ColumnSpacing = 14 };
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(mainBody, 0);
            host.Children.Add(mainBody);

            var floatRail = new StackPanel
            {
                Spacing = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 60, 4, 0),
            };
            var clipsBtn = MakeCircleIconButton("bookmark", "原声书摘 · 稍后听", p, _hasNewClipDot, tint);
            clipsBtn.Width = 36;
            clipsBtn.Height = 36;
            clipsBtn.CornerRadius = new CornerRadius(18);
            clipsBtn.Click += (_, _) => ToggleDrawer("clips");
            floatRail.Children.Add(clipsBtn);

            var voicesBtn = MakeCircleIconButton("wave", "声音卡 · 本机整理", p);
            voicesBtn.Width = 36;
            voicesBtn.Height = 36;
            voicesBtn.CornerRadius = new CornerRadius(18);
            voicesBtn.Click += (_, _) => ToggleDrawer("voices");
            floatRail.Children.Add(voicesBtn);

            Grid.SetColumn(floatRail, 1);
            host.Children.Add(floatRail);
            return host;
        }

        var split = new Grid { ColumnSpacing = 18 };
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(368) });

        Grid.SetColumn(mainBody, 0);
        split.Children.Add(mainBody);

        var drawer = BuildRightDrawer(dark);
        Grid.SetColumn(drawer, 1);
        split.Children.Add(drawer);

        return split;
    }

    // ---------- 1. 双栏收听台工作区（MacPodcastView.swift 1:1） ----------

    private FrameworkElement BuildStationWorkspace(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        Color leafSurface = dark ? Color.FromArgb(255, 31, 30, 28) : Color.FromArgb(255, 248, 245, 239);

        var allIssues = _library.Podcasts.GetIssues();
        var channels = _library.Podcasts.GetChannels();
        var clips = _library.Core.PodcastClips(null);

        var workspace = new Grid { ColumnSpacing = 24, IsTabStop = true };
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_drawerOpen ? 316 : 348) });

        // ===== 左栏：收听台 =====
        var leftCol = new StackPanel { Spacing = 12 };
        var leftHead = new Grid { Height = 28 };
        var lhSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        string stationTitle = _selectedShowKey switch
        {
            "studio" => "书房电台",
            "all" => "收听台",
            _ => channels.FirstOrDefault(c => c.Id == _selectedShowKey)?.Title ?? "收听台",
        };
        lhSp.Children.Add(new TextBlock { Text = stationTitle, FontFamily = serif, FontSize = 16.5, FontWeight = FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
        int groupCount = (_selectedShowKey is "all" or "studio" ? 1 : 0) + (channels.Count > 0 ? channels.Count : (_selectedShowKey == "all" ? 1 : 0));
        lhSp.Children.Add(new TextBlock { Text = $"{groupCount} 组 · {allIssues.Count} 期", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        leftHead.Children.Add(lhSp);

        var foldSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        foldSp.Children.Add(YYIcons.Make(_allCollapsed ? "chevron-right" : "chevron-down", 9.5, ThemeHelper.Brush(p.T2)));
        foldSp.Children.Add(new TextBlock { Text = _allCollapsed ? "全部展开" : "全部折叠", FontSize = 11, Foreground = ThemeHelper.Brush(p.T2) });
        var foldBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = foldSp,
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            UseSystemFocusVisuals = false,
        };
        foldBtn.Click += (_, _) =>
        {
            _allCollapsed = !_allCollapsed;
            _collapsedGroups.Clear();
            if (_allCollapsed)
            {
                _collapsedGroups.Add("playing");
                _collapsedGroups.Add("studio");
                foreach (var c in channels) _collapsedGroups.Add(c.Id);
            }
            _shelf.RefreshCurrentSubPage();
        };
        leftHead.Children.Add(foldBtn);
        leftCol.Children.Add(leftHead);

        var inProgress = allIssues.Where(i => i.PositionSec > 5 && (_selectedShowKey == "all" || (_selectedShowKey == "studio" && i.StudioKind is not null) || i.ChannelId == _selectedShowKey)).ToArray();
        if (inProgress.Length > 0)
        {
            leftCol.Children.Add(BuildStationGroupCard("playing", "正在听", $"{inProgress.Length} 期未完", null, inProgress, p, tint));
        }

        if (_selectedShowKey is "all" or "studio")
        {
            var studioIssues = allIssues.Where(i => i.StudioKind is not null).ToArray();
            var brewBtn = MakePaperPill("+ 酿一期", accent: false, p, tint);
            brewBtn.Click += async (_, _) => await ShowStudioSheetAsync();
            leftCol.Children.Add(BuildStudioGroupCard(studioIssues, brewBtn, p, tint, serif));
        }

        foreach (var ch in channels.Where(c => _selectedShowKey == "all" || _selectedShowKey == c.Id))
        {
            var chIssues = allIssues.Where(i => i.ChannelId == ch.Id).Take(6).ToArray();
            leftCol.Children.Add(BuildStationGroupCard(ch.Id, ch.Title, string.IsNullOrEmpty(ch.Author) ? $"{chIssues.Length} 期" : ch.Author, null, chIssues, p, tint));
        }

        var singleImports = allIssues.Where(i => i.StudioKind is null && string.IsNullOrEmpty(i.ChannelId) && _selectedShowKey == "all").ToArray();
        if (singleImports.Length > 0)
        {
            leftCol.Children.Add(BuildStationGroupCard("singles", "单集与本地音频", $"{singleImports.Length} 期", null, singleImports, p, tint));
        }

        if (channels.Count == 0 && _selectedShowKey == "all")
        {
            leftCol.Children.Add(BuildDiscoveryCard(p, tint, serif));
        }

        Grid.SetColumn(leftCol, 0);
        workspace.Children.Add(leftCol);

        // ===== 右栏：书桌 =====
        var rightCol = new StackPanel { Spacing = 14 };

        var now = DateTime.Now;
        string greeting = now.Hour switch
        {
            < 5 => "夜深了",
            < 11 => "早上好",
            < 14 => "中午好",
            < 18 => "下午好",
            _ => "晚上好",
        };
        string[] wkNames = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];
        var greetLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        greetLine.Children.Add(new TextBlock { Text = greeting, FontFamily = serif, FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        greetLine.Children.Add(new Border { Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Background = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        greetLine.Children.Add(new TextBlock { Text = $"{now.Month}月{now.Day}日 · {wkNames[(int)now.DayOfWeek]}", FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) });
        rightCol.Children.Add(greetLine);

        var latestClip = clips.FirstOrDefault();
        string quoteStr = latestClip is not null
            ? $"「{latestClip.QuoteText}」  {latestClip.ShowTitle}"
            : "「把声音读成一本书，把书架酿成一档只属于你的私人电台。」  书房电台";
        rightCol.Children.Add(new TextBlock
        {
            Text = quoteStr,
            FontFamily = serif,
            FontSize = 13,
            LineHeight = 20,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // 1:1 shelf-podcasts/1.png: recessed warm inset pill with search icon left + '/' keycap right
        var cmdBox = new TextBox
        {
            PlaceholderText = "搜节目、听过的句子，或粘贴单集链接…",
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 6, 0, 4),
        };
        _stationCmdBox = cmdBox;
        cmdBox.KeyDown += async (_, e) =>
        {
            if (e.Key != VirtualKey.Enter || string.IsNullOrWhiteSpace(cmdBox.Text)) return;
            e.Handled = true;
            string q = cmdBox.Text.Trim();
            if (q.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || q.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var (_, single) = await _library.Podcasts.AddUrlOrFeedAsync(q, putLatestOnDesk: true);
                    cmdBox.Text = "";
                    if (single is not null) OpenIssueId = single.Id;
                    _shelf.RefreshBooks();
                    _shelf.ShowToast("已导入并放入收听台", canUndo: false);
                }
                catch (Exception ex) { _shelf.ShowToast($"解析失败：{ex.Message}", canUndo: false); }
            }
            else
            {
                await ShowAddPodcastSheetAsync(q);
            }
        };
        var cmdGrid = new Grid { ColumnSpacing = 8, Padding = new Thickness(12, 0, 10, 0), Height = 38 };
        cmdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cmdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cmdGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var searchIc = YYIcons.Make("search", 13.5, ThemeHelper.Brush(p.T3));
        searchIc.VerticalAlignment = VerticalAlignment.Center;
        cmdGrid.Children.Add(searchIc);
        Grid.SetColumn(cmdBox, 1);
        cmdGrid.Children.Add(cmdBox);
        var slashCap = YYChrome.MakeKeycap("/", dark);
        Grid.SetColumn(slashCap, 2);
        cmdGrid.Children.Add(slashCap);
        rightCol.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = cmdGrid,
        });

        var quickGrid = new Grid { ColumnSpacing = 6 };
        (string Icon, string Label, bool Accent, Func<Task> Act)[] quicks =
        [
            ("plus", "酿一期", true, () => ShowStudioSheetAsync()),
            ("checklist-lines", "书房周刊", false, () => ShowStudioSheetAsync(StudioKind.WeeklyDigest)),
            ("circles-venn", "跨书圆桌", false, () => ShowStudioSheetAsync(StudioKind.Roundtable)),
            ("translate", "生词伴听", false, () => ShowStudioSheetAsync(StudioKind.VocabRadio)),
            ("link-chain", "贴单集", false, () => ShowAddPodcastSheetAsync()),
        ];
        for (int i = 0; i < quicks.Length; i++)
        {
            quickGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (icon, lbl, acc, act) = quicks[i];
            var sp = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            var circle = new Button
            {
                Width = 44,
                Height = 44,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(22),
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
            sp.Children.Add(new TextBlock { Text = lbl, FontSize = 11, Foreground = ThemeHelper.Brush(acc ? tint.Accent : p.T2), HorizontalAlignment = HorizontalAlignment.Center });
            Grid.SetColumn(sp, i);
            quickGrid.Children.Add(sp);
        }
        rightCol.Children.Add(quickGrid);

        var activeIssue = (_playingIssueId is not null ? allIssues.FirstOrDefault(x => x.Id == _playingIssueId) : null)
            ?? allIssues.FirstOrDefault(x => x.PositionSec > 0)
            ?? allIssues.FirstOrDefault();
        rightCol.Children.Add(BuildTabletopPlayerCard(activeIssue, p, tint, serif, leafSurface));

        if (clips.Length > 0 && !_drawerOpen)
        {
            var clipCard = new StackPanel { Spacing = 6, Padding = new Thickness(14, 12, 14, 12) };
            var clipHead = new Grid();
            clipHead.Children.Add(new TextBlock { Text = $"原声书摘 · {clips.Length} 条", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            var viewAllBtn = new Button
            {
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 0, 2, 0),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(tint.Accent),
                HorizontalAlignment = HorizontalAlignment.Right,
                Content = "展开抽屉 ›",
            };
            viewAllBtn.Click += (_, _) => ToggleDrawer("clips");
            clipHead.Children.Add(viewAllBtn);
            clipCard.Children.Add(clipHead);

            foreach (var cl in clips.Take(3))
            {
                var cRow = new StackPanel { Spacing = 2, Padding = new Thickness(0, 4, 0, 4) };
                cRow.Children.Add(new TextBlock { Text = cl.QuoteText, FontFamily = serif, FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                cRow.Children.Add(new TextBlock { Text = $"{cl.ShowTitle} · {FormatClock(cl.T0)}", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                clipCard.Children.Add(cRow);
            }
            rightCol.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Child = clipCard,
            });
        }

        Grid.SetColumn(rightCol, 1);
        workspace.Children.Add(rightCol);
        return workspace;
    }

    // ---------- 2. 右侧 368px 文房抽屉（1:1 PodcastDrawerContentView） ----------

    private FrameworkElement BuildRightDrawer(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());

        var outer = new StackPanel { Spacing = 10, Padding = new Thickness(14) };

        // Header + 4-tab segmented bar
        var top = new Grid();
        string drawerTitle = _drawerTab switch
        {
            "later" => "稍后听",
            "voices" => "声音卡",
            "local" => "本机整理",
            _ => "原声书摘",
        };
        top.Children.Add(new TextBlock { Text = drawerTitle, FontFamily = serif, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        var closeBtn = MakeCircleIconButton("xmark", "收起抽屉", p);
        closeBtn.Width = 26;
        closeBtn.Height = 26;
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.Click += (_, _) => { _drawerOpen = false; _shelf.RefreshCurrentSubPage(); };
        top.Children.Add(closeBtn);
        outer.Children.Add(top);

        var tabGrid = new Grid { ColumnSpacing = 2 };
        (string Key, string Label)[] tabs =
        [
            ("clips", "书摘"),
            ("later", "稍后听"),
            ("voices", "声音卡"),
            ("local", "本机整理"),
        ];
        for (int i = 0; i < tabs.Length; i++)
        {
            tabGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (k, lbl) = tabs[i];
            bool on = _drawerTab == k;
            var tb = new Button
            {
                Content = lbl,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 25,
                MinHeight = 25,
                Padding = new Thickness(0),
                FontSize = 11,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                UseSystemFocusVisuals = false,
            };
            string nextTab = k;
            tb.Click += (_, _) => { _drawerTab = nextTab; _shelf.RefreshCurrentSubPage(); };
            Grid.SetColumn(tb, i);
            tabGrid.Children.Add(tb);
        }
        outer.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(p.Inset),
            Padding = new Thickness(2.5),
            Child = tabGrid,
        });

        var bodyStack = new StackPanel { Spacing = 8 };
        switch (_drawerTab)
        {
            case "clips":
            {
                var clips = _library.Core.PodcastClips(null);
                if (clips.Length == 0)
                {
                    bodyStack.Children.Add(new TextBlock
                    {
                        Text = "还没有原声书摘。\n在访谈书页或台面播放器点「原声划线」即可把那一句与前后时间轴收进这里。",
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.T3),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 19,
                        Margin = new Thickness(4, 16, 4, 16),
                    });
                }
                foreach (var cl in clips)
                {
                    var item = cl;
                    var sp = new StackPanel { Spacing = 5 };
                    sp.Children.Add(new TextBlock { Text = $"「{item.QuoteText}」", FontFamily = serif, FontSize = 13, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
                    if (!string.IsNullOrWhiteSpace(item.Thought))
                        sp.Children.Add(new TextBlock { Text = $"💭 {item.Thought}", FontSize = 11.5, Foreground = ThemeHelper.Brush(tint.Accent), TextWrapping = TextWrapping.Wrap });
                    sp.Children.Add(new TextBlock { Text = $"{item.ShowTitle} · {FormatClock(item.T0)}–{FormatClock(item.T1)}", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });

                    var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                    if (!string.IsNullOrWhiteSpace(item.AudioRelPath))
                    {
                        string clipPath = Path.Combine(_library.Core.PodcastClipsDir(), item.AudioRelPath);
                        if (File.Exists(clipPath))
                        {
                            var replayBtn = MakePaperPill("▶ 原声", accent: true, p, tint);
                            replayBtn.Height = 23;
                            replayBtn.FontSize = 10.5;
                            replayBtn.Click += (_, _) =>
                            {
                                try
                                {
                                    _clipPreviewPlayer.Pause();
                                    _clipPreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(clipPath));
                                    _clipPreviewPlayer.Play();
                                }
                                catch { }
                            };
                            acts.Children.Add(replayBtn);
                        }
                    }

                    var jumpBtn = MakePaperPill("听原处", accent: string.IsNullOrWhiteSpace(item.AudioRelPath), p, tint);
                    jumpBtn.Height = 23;
                    jumpBtn.FontSize = 10.5;
                    jumpBtn.Click += (_, _) =>
                    {
                        OpenIssueId = item.IssueId;
                        var sents = _library.Core.PodcastTranscript(item.IssueId);
                        int hitIdx = Array.FindIndex(sents, s => s.T0 >= item.T0 - 1.0);
                        _activeTurnIdx = Math.Max(0, hitIdx);
                        _shelf.RefreshCurrentSubPage();
                    };
                    var editBtn = MakePaperPill("想法", accent: false, p, tint);
                    editBtn.Height = 23;
                    editBtn.FontSize = 10.5;
                    editBtn.Click += async (_, _) => await EditClipThoughtAsync(item);

                    var delBtn = MakePaperPill("删除", accent: false, p, tint);
                    delBtn.Height = 23;
                    delBtn.FontSize = 10.5;
                    delBtn.Click += (_, _) =>
                    {
                        try { _library.Core.PodcastDeleteClip(item.Id); } catch { }
                        _shelf.RefreshCurrentSubPage();
                    };
                    acts.Children.Add(jumpBtn);
                    acts.Children.Add(editBtn);
                    acts.Children.Add(delBtn);
                    sp.Children.Add(acts);

                    bodyStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(10),
                        Background = ThemeHelper.Brush(p.Inset),
                        Padding = new Thickness(11, 9, 11, 9),
                        Child = sp,
                    });
                }
                break;
            }
            case "later":
            {
                var laterList = _library.Podcasts.GetIssues(PodcastPlacement.Archive)
                    .Concat(_library.Podcasts.GetIssues(PodcastPlacement.Newsstand))
                    .DistinctBy(x => x.Id)
                    .ToArray();
                if (laterList.Length == 0)
                {
                    bodyStack.Children.Add(new TextBlock
                    {
                        Text = "稍后听与报刊栏暂无单集。\n订阅节目的近 14 天更新与归档单集会安静收在这里，没有未读红点。",
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.T3),
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 19,
                        Margin = new Thickness(4, 16, 4, 16),
                    });
                }
                foreach (var iss in laterList.Take(24))
                {
                    var target = iss;
                    var itemSp = new StackPanel { Spacing = 6 };
                    var meta = new StackPanel { Spacing = 2 };
                    meta.Children.Add(new TextBlock { Text = target.Title, FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    meta.Children.Add(new TextBlock { Text = $"{target.ShowTitle} · {Math.Max(1, (int)Math.Round(target.DurationSec / 60.0))} 分钟", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    itemSp.Children.Add(meta);

                    var actRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                    var openBtn = MakePaperPill("展开", accent: true, p, tint);
                    openBtn.Height = 23;
                    openBtn.FontSize = 10.5;
                    openBtn.Click += (_, _) => { OpenIssueId = target.Id; _shelf.RefreshCurrentSubPage(); };

                    var deskBtn = MakePaperPill("移上书桌", accent: false, p, tint);
                    deskBtn.Height = 23;
                    deskBtn.FontSize = 10.5;
                    deskBtn.Click += (_, _) =>
                    {
                        try { _library.Core.PodcastSetPlacement(target.Id, PodcastPlacement.Desk); } catch { }
                        _shelf.ShowToast("已移上在听书桌", canUndo: false);
                        _shelf.RefreshCurrentSubPage();
                    };

                    var delIssBtn = MakePaperPill("删除", accent: false, p, tint);
                    delIssBtn.Height = 23;
                    delIssBtn.FontSize = 10.5;
                    delIssBtn.Click += (_, _) =>
                    {
                        try { _library.Core.PodcastDeleteIssue(target.Id); } catch { }
                        _shelf.RefreshBooks();
                    };

                    actRow.Children.Add(openBtn);
                    actRow.Children.Add(deskBtn);
                    actRow.Children.Add(delIssBtn);
                    itemSp.Children.Add(actRow);
                    bodyStack.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(10, 8, 10, 8), Child = itemSp });
                }
                break;
            }
            case "voices":
            {
                (string Id, string Name, string Role, string Sample)[] studioVoices =
                [
                    ("linshen", "林深", "书房男声主理人 · 沉稳耐听", "今天我们从书架上最近翻开的那几页聊起。"),
                    ("suwan", "苏晚", "书房女声搭档 · 清朗敏锐", "那些划过线的地方，往往藏着读第二遍才看见的伏笔。"),
                    ("clara", "Clara", "English Host · Warm & Clear", "Let us revisit the words you highlighted this week."),
                    ("david", "David", "English Co-Host · Calm", "Every unfamiliar word makes more sense inside a living story."),
                ];
                foreach (var v in studioVoices)
                {
                    bool on = !_library.Prefs.PodcastRememberedVoices.TryGetValue(v.Id, out var rem) || rem;
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                    meta.Children.Add(new TextBlock { Text = v.Name, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                    meta.Children.Add(new TextBlock { Text = v.Role, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                    row.Children.Add(meta);

                    var tryBtn = MakePaperPill("▶ 试听", accent: false, p, tint);
                    tryBtn.Height = 24;
                    tryBtn.FontSize = 10.5;
                    string sample = v.Sample;
                    bool isEn = v.Id is "clara" or "david";
                    bool isFemale = v.Id is "suwan" or "clara";
                    tryBtn.Click += (_, _) =>
                    {
                        string? vid = PickHostVoiceId(isFemale, isEn);
                        _ = AudioPlayer.PreviewVoiceAsync(_library, vid, 1.0, sample);
                    };
                    Grid.SetColumn(tryBtn, 1);
                    row.Children.Add(tryBtn);

                    string vKey = v.Id;
                    var sw = new ToggleSwitch { IsOn = on, MinWidth = 0, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
                    sw.Toggled += (_, _) =>
                    {
                        _library.Prefs.PodcastRememberedVoices[vKey] = sw.IsOn;
                        _library.SavePrefs();
                    };
                    Grid.SetColumn(sw, 2);
                    row.Children.Add(sw);
                    bodyStack.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(10, 8, 10, 8), Child = row });
                }

                foreach (var cloned in _library.LiveVoice.GetClonedCards())
                {
                    var cc = cloned;
                    var cRow = new Grid { ColumnSpacing = 8 };
                    cRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    cRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var cm = new StackPanel { Spacing = 1 };
                    cm.Children.Add(new TextBlock { Text = $"✦ {cc.Name}", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
                    cm.Children.Add(new TextBlock { Text = "我的本机克隆声音卡", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                    cRow.Children.Add(cm);
                    var useBtn = MakePaperPill(_library.Prefs.PodcastStudioVoiceStyle == "card:" + cc.Id ? "✓ 当前主播" : "设为主播", accent: true, p, tint);
                    useBtn.Height = 24;
                    useBtn.Click += (_, _) =>
                    {
                        _library.Prefs.PodcastStudioVoiceStyle = "card:" + cc.Id;
                        _library.SavePrefs();
                        _shelf.RefreshCurrentSubPage();
                    };
                    Grid.SetColumn(useBtn, 1);
                    cRow.Children.Add(useBtn);
                    bodyStack.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(10, 8, 10, 8), Child = cRow });
                }

                var cloneBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = "🎙 克隆一段自己的主播声音…",
                    FontSize = 11.5,
                };
                cloneBtn.Click += async (_, _) =>
                {
                    var saved = await VoiceCloneAndDramaDialogs.ShowCloneVoiceDialogAsync(_shelf, _shelf.WindowXamlRoot, _library);
                    if (saved is not null) _shelf.RefreshCurrentSubPage();
                };
                bodyStack.Children.Add(cloneBtn);
                break;
            }
            default:
            {
                int chCnt = _library.Podcasts.GetChannels().Count;
                int issCnt = _library.Podcasts.GetIssues().Count;
                int clipCnt = _library.Core.PodcastClips(null).Length;
                var stats = _library.Podcasts.GetStorageStats();
                string audioMb = $"{stats.AudioBytes / (1024.0 * 1024.0):F1} MB";
                string clipMb = $"{stats.ClipBytes / (1024.0 * 1024.0):F1} MB";

                // On-device transcription card for currently open or latest external/local audio issue
                var transcribable = (OpenIssueId is not null
                    ? _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == OpenIssueId)
                    : null)
                    ?? _library.Podcasts.GetIssues().FirstOrDefault(x => x.StudioKind is null && (!string.IsNullOrEmpty(x.LocalAudioPath) || !string.IsNullOrEmpty(x.AudioUrl)));

                if (transcribable is not null && transcribable.StudioKind is null)
                {
                    var trSp = new StackPanel { Spacing = 6 };
                    trSp.Children.Add(new TextBlock { Text = "本机语音转逐字稿", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                    trSp.Children.Add(new TextBlock
                    {
                        Text = $"当前目标：《{transcribable.Title}》（支持 SenseVoice 离线模型 / 本地 Whisper / 已配置转写 API / 系统语音识别）",
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T3),
                        TextWrapping = TextWrapping.Wrap,
                    });
                    bool busy = _transcribingIssueId == transcribable.Id;
                    if (busy && !string.IsNullOrEmpty(_transcribeStatus))
                        trSp.Children.Add(new TextBlock { Text = _transcribeStatus, FontSize = 11, Foreground = ThemeHelper.Brush(tint.Accent), TextWrapping = TextWrapping.Wrap });

                    var trBtn = MakePaperPill(busy ? "转写中…" : "为当前单集生成逐字稿", accent: true, p, tint);
                    trBtn.IsEnabled = !busy;
                    string targetId = transcribable.Id;
                    trBtn.Click += async (_, _) =>
                    {
                        _transcribingIssueId = targetId;
                        _transcribeStatus = "准备音频…";
                        _shelf.RefreshCurrentSubPage();
                        try
                        {
                            int count = await _library.Podcasts.TranscribeIssueOnDeviceAsync(targetId, msg =>
                            {
                                _transcribeStatus = msg;
                                _shelf.DispatcherQueue.TryEnqueue(() => _shelf.RefreshCurrentSubPage());
                            });
                            _transcribingIssueId = null;
                            _transcribeStatus = "";
                            OpenIssueId = targetId;
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
                    };
                    trSp.Children.Add(trBtn);
                    bodyStack.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(11, 9, 11, 9), Child = trSp });
                }

                // Storage & cache cleanup card
                var cacheSp = new StackPanel { Spacing = 6 };
                cacheSp.Children.Add(new TextBlock { Text = "缓存与离线音频空间", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                cacheSp.Children.Add(new TextBlock
                {
                    Text = $"单集缓存：{stats.AudioFiles} 个文件 · {audioMb}\n原声书摘片段：{stats.ClipFiles} 段 WAV · {clipMb}",
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T3),
                    LineHeight = 17,
                });
                if (stats.AudioBytes > 0)
                {
                    var clearBtn = MakePaperPill("清理单集音频缓存", accent: false, p, tint);
                    clearBtn.Click += (_, _) =>
                    {
                        long freed = _library.Podcasts.ClearAudioCache();
                        _shelf.ShowToast($"已清理 {freed / (1024.0 * 1024.0):F1} MB 缓存（原声书摘保留不受影响）", canUndo: false);
                        _shelf.RefreshCurrentSubPage();
                    };
                    cacheSp.Children.Add(clearBtn);
                }
                bodyStack.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(11, 9, 11, 9), Child = cacheSp });

                (string Title, string Detail)[] items =
                [
                    ("防剧透物理锁", "书房电台默认只读取你已翻阅到的章节与划线，未读后文在送往模型前由 Rust 内核物理截断"),
                    ("本机离线语音合成", _library.LiveVoice.IsReady ? "已安装 24kHz 离线自然人声，双人交替演绎零云端费用" : "当前使用系统双人语音，可在「设置 → 朗读」下载 160MB 离线自然人声"),
                    ("报刊栏零红点", $"已订阅 {chCnt} 档节目 · 共 {issCnt} 期单集 · 已收 {clipCnt} 条原声书摘"),
                ];
                foreach (var (itTitle, itDetail) in items)
                {
                    var sp = new StackPanel { Spacing = 3 };
                    sp.Children.Add(new TextBlock { Text = itTitle, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                    sp.Children.Add(new TextBlock { Text = itDetail, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap, LineHeight = 17 });
                    bodyStack.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(11, 9, 11, 9), Child = sp });
                }
                break;
            }
        }

        outer.Children.Add(new ScrollViewer { MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = bodyStack });

        return new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = outer,
        };
    }

    // ---------- Station Cards ----------

    private FrameworkElement BuildStudioGroupCard(PodcastIssue[] studioIssues, Button brewBtn, YYPalette p, YYTintColors tint, FontFamily serif)
    {
        bool collapsed = _collapsedGroups.Contains("studio");
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(BuildGroupHeader("studio", "书房电台", "由你的书架酿制", brewBtn, collapsed, p));

        if (!collapsed)
        {
            stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            if (studioIssues.Length > 0)
            {
                for (int i = 0; i < studioIssues.Length; i++)
                {
                    if (i > 0) stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
                    stack.Children.Add(BuildEpisodeRow(studioIssues[i], p, tint));
                }
            }
            else
            {
                string sampleBook = _library.GetShelfBooks().FirstOrDefault()?.Title ?? "书架在读书";
                (StudioKind Kind, string Title, string Tag, string Meta)[] starters =
                [
                    (StudioKind.WeeklyDigest, $"本周书房周刊：重读《{sampleBook}》…", "不剧透锁", "10 分 · 本机酿制"),
                    (StudioKind.Roundtable, "跨书圆桌：挑两三本在读书同台碰撞…", "跨书对谈", "15 分 · 防剧透"),
                    (StudioKind.BookGuide, $"难书导读：《{sampleBook}》直觉热身…", "读前热身", "8 分 · 附原文锚点"),
                    (StudioKind.VocabRadio, "生词伴听 · 通勤外语台（融入在学词）", "生词本联动", "12 分 · 语速 0.9×"),
                ];
                for (int i = 0; i < starters.Length; i++)
                {
                    if (i > 0) stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
                    var (kind, title, tag, meta) = starters[i];
                    var r = new Grid { ColumnSpacing = 10, Padding = new Thickness(14, 10, 14, 10) };
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    r.Children.Add(YYIcons.Make("play", 10, ThemeHelper.Brush(p.T3), filled: true));
                    var badge = new Border
                    {
                        Width = 24,
                        Height = 24,
                        CornerRadius = new CornerRadius(6),
                        Background = ThemeHelper.Brush(tint.Soft),
                        Child = YYIcons.Make("book-open", 11.5, ThemeHelper.Brush(tint.Accent)),
                    };
                    Grid.SetColumn(badge, 1);
                    r.Children.Add(badge);

                    var m = new StackPanel { Spacing = 2 };
                    m.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    var subLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                    subLine.Children.Add(new TextBlock { Text = tag, FontSize = 11, Foreground = ThemeHelper.Brush(tint.Accent) });
                    subLine.Children.Add(new TextBlock { Text = $"· {meta}", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    m.Children.Add(subLine);
                    Grid.SetColumn(m, 2);
                    r.Children.Add(m);

                    var trail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.55 };
                    trail.Children.Add(YYIcons.Make("doc-lines", 11.5, ThemeHelper.Brush(p.T3)));
                    trail.Children.Add(YYIcons.Make("bookmark", 11.5, ThemeHelper.Brush(p.T3)));
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
                    btn.Click += async (_, _) => await ShowStudioSheetAsync(sk);
                    stack.Children.Add(btn);
                }
            }
        }

        return new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    private FrameworkElement BuildStationGroupCard(string groupKey, string title, string sub, Button? actionPill, PodcastIssue[] issues, YYPalette p, YYTintColors tint)
    {
        bool collapsed = _collapsedGroups.Contains(groupKey);
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(BuildGroupHeader(groupKey, title, sub, actionPill, collapsed, p));

        if (!collapsed && issues.Length > 0)
        {
            stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            for (int i = 0; i < issues.Length; i++)
            {
                if (i > 0) stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
                stack.Children.Add(BuildEpisodeRow(issues[i], p, tint));
            }
        }

        return new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    private FrameworkElement BuildGroupHeader(string groupKey, string title, string sub, Button? rightBtn, bool collapsed, YYPalette p)
    {
        // 1:1 shelf-podcasts/1.png: Title + subtle subtitle on left, action pill + collapse chevron on the far right
        var g = new Grid { Padding = new Thickness(14, 12, 12, 10), ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        leftSp.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        leftSp.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(leftSp);

        var rightSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (rightBtn is not null) rightSp.Children.Add(rightBtn);
        var chevBtn = new Button
        {
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = YYIcons.Make(collapsed ? "chevron-right" : "chevron-down", 10.5, ThemeHelper.Brush(p.T3)),
        };
        string gk = groupKey;
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

    private FrameworkElement BuildEpisodeRow(PodcastIssue issue, YYPalette p, YYTintColors tint)
    {
        var r = new Grid { ColumnSpacing = 10, Padding = new Thickness(14, 10, 14, 10) };
        r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        bool isPlayingThis = _playing && _playingIssueId == issue.Id;
        r.Children.Add(YYIcons.Make(isPlayingThis ? "pause" : "play", 11, ThemeHelper.Brush(tint.Accent), filled: true));

        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(issue.StudioKind is not null ? tint.Soft : p.Inset),
            Child = YYIcons.Make(issue.StudioKind is not null ? "book" : "radio", 11.5, ThemeHelper.Brush(issue.StudioKind is not null ? tint.Accent : p.T2)),
        };
        Grid.SetColumn(badge, 1);
        r.Children.Add(badge);

        var mid = new StackPanel { Spacing = 2 };
        mid.Children.Add(new TextBlock { Text = issue.Title, FontSize = 13.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        int durMin = Math.Max(1, (int)Math.Round(issue.DurationSec / 60.0));
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(issue.PublishedAt > 0 ? issue.PublishedAt : issue.AddedAt).LocalDateTime;
        string fidBadge = issue.Fidelity == PodcastFidelity.L2Magazine ? " · 逐字稿" : "";
        string meta = $"{issue.ShowTitle} · {durMin} 分钟 · {dt.Month}月{dt.Day}日{fidBadge}";
        mid.Children.Add(new TextBlock { Text = meta, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(mid, 2);
        r.Children.Add(mid);

        string iid = issue.Id;
        bool inLater = issue.Placement == PodcastPlacement.Archive;

        var trailActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        trailActs.Children.Add(YYIcons.Make("doc-lines", 11.5, ThemeHelper.Brush(p.T3)));
        var bmBtn = new Button
        {
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = YYIcons.Make("bookmark", 11.5, ThemeHelper.Brush(inLater ? tint.Accent : p.T3), filled: inLater),
        };
        ToolTipService.SetToolTip(bmBtn, inLater ? "移回在听书桌" : "存入稍后听");
        bmBtn.Click += (_, _) =>
        {
            try { _library.Core.PodcastSetPlacement(iid, inLater ? PodcastPlacement.Desk : PodcastPlacement.Archive); _shelf.RefreshBooks(); } catch { }
        };
        trailActs.Children.Add(bmBtn);
        Grid.SetColumn(trailActs, 3);
        r.Children.Add(trailActs);

        var ctx = new MenuFlyout();
        var laterMi = new MenuFlyoutItem { Text = inLater ? "移回在听书桌" : "存入稍后听" };
        laterMi.Click += (_, _) =>
        {
            try { _library.Core.PodcastSetPlacement(iid, inLater ? PodcastPlacement.Desk : PodcastPlacement.Archive); _shelf.RefreshBooks(); } catch { }
        };
        var del = new MenuFlyoutItem { Text = "删除单集", Foreground = ThemeHelper.Brush(p.Danger) };
        del.Click += (_, _) => { try { _library.Core.PodcastDeleteIssue(iid); _shelf.RefreshBooks(); } catch { } };
        ctx.Items.Add(laterMi);
        ctx.Items.Add(new MenuFlyoutSeparator());
        ctx.Items.Add(del);

        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            UseSystemFocusVisuals = false,
            ContextFlyout = ctx,
            Content = r,
        };
        btn.Click += (_, _) =>
        {
            OpenIssueId = iid;
            _activeTurnIdx = 0;
            _track = issue.Track;
            _shelf.RefreshCurrentSubPage();
        };
        return btn;
    }

    private FrameworkElement BuildDiscoveryCard(YYPalette p, YYTintColors tint, FontFamily serif)
    {
        bool collapsed = _collapsedGroups.Contains("discovery");
        var stack = new StackPanel { Spacing = 0 };
        var moreBtn = MakePaperPill("搜更多", accent: false, p, tint);
        moreBtn.Click += async (_, _) => await ShowAddPodcastSheetAsync();
        stack.Children.Add(BuildGroupHeader("discovery", "精选播客", "公开目录 · 一键订阅", moreBtn, collapsed, p));

        (string Name, string Desc, string Rss, Color BadgeBg, string BadgeTxt)[] picks =
        [
            ("声动早咖啡", "声动活泼 · 创业", "https://feeds.fireside.fm/morningcoffee/rss", Color.FromArgb(255, 226, 152, 46), "早"),
            ("故事FM", "用你的声音，讲述你的故事", "https://storyfm.cn/feed/episodes/", Color.FromArgb(255, 214, 72, 62), "故"),
            ("忽左忽右", "文化与历史的十字路口", "https://feeds.fireside.fm/left-right/rss", Color.FromArgb(255, 58, 120, 88), "左"),
            ("Lex Fridman Podcast", "Science, technology, history & philosophy", "https://lexfridman.com/feed/podcast/", Color.FromArgb(255, 76, 86, 146), "LF"),
        ];

        if (!collapsed)
        {
            for (int i = 0; i < picks.Length; i++)
            {
                if (i > 0) stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
                var (name, desc, rss, badgeBg, badgeTxt) = picks[i];
                var r = new Grid { ColumnSpacing = 11, Padding = new Thickness(14, 10, 14, 10) };
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                r.Children.Add(new Border
                {
                    Width = 32,
                    Height = 32,
                    CornerRadius = new CornerRadius(8),
                    Background = ThemeHelper.Brush(badgeBg),
                    Child = new TextBlock
                    {
                        Text = badgeTxt,
                        FontSize = badgeTxt.Length > 1 ? 11 : 13,
                        FontWeight = FontWeights.Bold,
                        Foreground = ThemeHelper.Brush(Colors.White),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                });
                var m = new StackPanel { Spacing = 1.5, VerticalAlignment = VerticalAlignment.Center };
                m.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                m.Children.Add(new TextBlock { Text = desc, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(m, 1);
                r.Children.Add(m);

                var subBtn = MakePaperPill("订阅", accent: false, p, tint);
                string feedUrl = rss;
                subBtn.Click += async (_, _) =>
                {
                    subBtn.IsEnabled = false;
                    subBtn.Content = "订阅中…";
                    try
                    {
                        await _library.Podcasts.SubscribeRssAsync(feedUrl);
                        _shelf.RefreshBooks();
                        _shelf.ShowToast($"已订阅「{name}」", canUndo: false);
                    }
                    catch (Exception ex)
                    {
                        subBtn.IsEnabled = true;
                        subBtn.Content = "订阅";
                        _shelf.ShowToast($"订阅失败：{ex.Message}", canUndo: false);
                    }
                };
                Grid.SetColumn(subBtn, 2);
                r.Children.Add(subBtn);
                stack.Children.Add(r);
            }
        }

        return new Border
        {
            CornerRadius = new CornerRadius(15),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = stack,
        };
    }

    private FrameworkElement BuildTabletopPlayerCard(PodcastIssue? active, YYPalette p, YYTintColors tint, FontFamily serif, Color leafSurface)
    {
        bool dark = ThemeHelper.IsDark((_shelf.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Light);
        var outer = new StackPanel { Spacing = 0 };
        var upper = new StackPanel { Spacing = 10, Padding = new Thickness(14, 14, 14, 12) };

        var topRow = new Grid { ColumnSpacing = 10 };
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topRow.Children.Add(new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(leafSurface),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = YYIcons.Make(active?.StudioKind is not null || active is null ? "book-open" : "radio", 15, ThemeHelper.Brush(tint.Accent)),
        });

        var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        meta.Children.Add(new TextBlock
        {
            Text = active?.Title ?? "书房电台 · 导读待酿制",
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        meta.Children.Add(new TextBlock
        {
            Text = active?.ShowTitle ?? "书房电台 · 读前 8 分钟热身",
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(meta, 1);
        topRow.Children.Add(meta);

        var actBtn = MakePaperPill(active is not null ? "展开书页" : "酿一期", accent: false, p, tint);
        actBtn.Click += async (_, _) =>
        {
            if (active is not null)
            {
                OpenIssueId = active.Id;
                _shelf.RefreshCurrentSubPage();
            }
            else
            {
                await ShowStudioSheetAsync();
            }
        };
        Grid.SetColumn(actBtn, 2);
        topRow.Children.Add(actBtn);
        upper.Children.Add(topRow);

        // 1:1 shelf-podcasts/1.png: quote card with speaker + spoiler-lock badge row & serif preview
        string excerpt = active is not null && !string.IsNullOrWhiteSpace(active.Summary)
            ? active.Summary
            : "点右上角「酿一期」或下方播放键，把已读章节与划线酿成一期私人双人对谈…";
        var quoteBox = new StackPanel { Spacing = 6 };
        var qHead = new Grid();
        qHead.Children.Add(new TextBlock { Text = "林深 · 书房主播", FontSize = 10.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(tint.Accent) });
        qHead.Children.Add(new TextBlock { Text = "本地声卡 · 防剧透锁", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
        quoteBox.Children.Add(qHead);
        quoteBox.Children.Add(new TextBlock
        {
            Text = excerpt,
            FontFamily = serif,
            FontSize = 13,
            LineHeight = 20,
            Foreground = ThemeHelper.Brush(p.T1),
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
        });
        upper.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(11),
            Background = ThemeHelper.Brush(leafSurface),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(12, 9, 12, 10),
            Child = quoteBox,
        });

        // 1:1 shelf-podcasts/1.png: sleek 2.5px hairline progress bar + 00:00 / 08:00 timestamps below
        double maxDur = Math.Max(1.0, active?.DurationSec ?? 480.0);
        double curPos = (active is not null && _playing && _playingIssueId == active.Id && _usingExternalMedia)
            ? _mediaPlayer.PlaybackSession.Position.TotalSeconds
            : (active?.PositionSec ?? 0.0);
        double frac = Math.Clamp(curPos / maxDur, 0, 1);

        var progStack = new StackPanel { Spacing = 5, Margin = new Thickness(0, 2, 0, 0) };
        var progBar = new Grid { Height = 3, CornerRadius = new CornerRadius(1.5), Background = ThemeHelper.Brush(p.Inset) };
        if (frac > 0.002)
        {
            progBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
            progBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1.0 - frac), GridUnitType.Star) });
            progBar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = ThemeHelper.Brush(tint.Fill) });
        }
        progStack.Children.Add(progBar);

        var timeRow = new Grid();
        timeRow.Children.Add(new TextBlock { Text = FormatClock(curPos), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
        timeRow.Children.Add(new TextBlock { Text = FormatClock(maxDur), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
        progStack.Children.Add(timeRow);
        upper.Children.Add(progStack);

        outer.Children.Add(upper);
        outer.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });

        var footOuter = new StackPanel { Spacing = 7, Padding = new Thickness(14, 10, 14, 11), Background = ThemeHelper.Brush(leafSurface), CornerRadius = new CornerRadius(0, 0, 16, 16) };

        var foot = new Grid();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        bool isPlayingActive = active is not null && _playing && _playingIssueId == active.Id;

        var back15 = new Button
        {
            Style = (Style)Application.Current.Resources["YYIconButtonStyle"],
            Content = "-15s",
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(p.T2),
            Padding = new Thickness(4, 2, 4, 2),
        };
        if (active is not null) back15.Click += (_, _) => StepPlayback(active, LoadOrSynthesizeTranscript(active), forward: false, deltaSec: 15);
        controls.Children.Add(back15);

        var playBtn = new Button
        {
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Background = ThemeHelper.Brush(p.T1),
            BorderThickness = new Thickness(0),
            Content = YYIcons.Make(isPlayingActive ? "pause" : "play", 12, ThemeHelper.Brush(p.Bg), filled: true),
        };
        playBtn.Click += async (_, _) =>
        {
            if (active is not null)
            {
                if (isPlayingActive)
                {
                    StopPlayback();
                    _shelf.RefreshCurrentSubPage();
                }
                else
                {
                    var sents = LoadOrSynthesizeTranscript(active);
                    int startIdx = Array.FindLastIndex(sents, s => s.T0 <= active.PositionSec + 1.0);
                    PlayIssueAtTurn(active, sents, Math.Max(0, startIdx));
                    _shelf.RefreshCurrentSubPage();
                }
            }
            else
            {
                await ShowStudioSheetAsync();
            }
        };
        controls.Children.Add(playBtn);

        var fwd15 = new Button
        {
            Style = (Style)Application.Current.Resources["YYIconButtonStyle"],
            Content = "+15s",
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(p.T2),
            Padding = new Thickness(4, 2, 4, 2),
        };
        if (active is not null) fwd15.Click += (_, _) => StepPlayback(active, LoadOrSynthesizeTranscript(active), forward: true, deltaSec: 15);
        controls.Children.Add(fwd15);

        var miniRateBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYIconButtonStyle"],
            Content = $"{_rate:0.0}×",
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(p.T2),
            Padding = new Thickness(4, 2, 4, 2),
        };
        miniRateBtn.Click += (_, _) => CyclePlaybackRate();
        controls.Children.Add(miniRateBtn);
        foot.Children.Add(controls);

        // Right: 划线 [H] keycap trigger
        var clipWrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        clipWrap.Children.Add(new TextBlock { Text = "划线", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        clipWrap.Children.Add(YYChrome.MakeKeycap("H", dark));
        var clipQuickBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 2, 2),
            UseSystemFocusVisuals = false,
            Content = clipWrap,
        };
        if (active is not null)
        {
            string aid = active.Id;
            clipQuickBtn.Click += async (_, _) => await ClipCurrentTurnAsync(aid);
        }
        foot.Children.Add(clipQuickBtn);

        footOuter.Children.Add(foot);
        footOuter.Children.Add(new TextBlock
        {
            Text = "防剧透锁 · 仅基于已读进度",
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(tint.Accent),
        });
        outer.Children.Add(footOuter);

        return new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = outer,
        };
    }

    // ---------- 3. 内联「访谈书页」阅读器（PodcastReaderView.swift 1:1） ----------

    private MagazineSentence[] LoadOrSynthesizeTranscript(PodcastIssue issue)
    {
        MagazineSentence[] sentences;
        try { sentences = _library.Core.PodcastTranscript(issue.Id); }
        catch { sentences = []; }

        if (sentences.Length == 0 && !string.IsNullOrWhiteSpace(issue.Shownotes))
        {
            var rawLines = issue.Shownotes.Split(['\n', '。'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(30).ToArray();
            sentences = rawLines.Select((ln, idx) => new MagazineSentence(
                Id: $"s-{idx}", Speaker: issue.ShowTitle, TextOrig: ln, TextTrans: null, CoachNote: null,
                T0: idx * 12, T1: (idx + 1) * 12, SkipReason: null, BookId: null, BookTitle: null, BookSection: null, BookSentence: null, BookQuote: null)).ToArray();
        }
        return sentences;
    }

    private FrameworkElement BuildInlineEpisodeReader(PodcastIssue issue, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        var geoSerif = new FontFamily(YYFontFamily.LatinSerif.WindowsFamily());

        var sentences = LoadOrSynthesizeTranscript(issue);
        _activeSentences = sentences;

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
            Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 16),
            IsTabStop = true,
        };

        var kicker = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        kicker.Children.Add(new Border
        {
            Width = 19,
            Height = 19,
            CornerRadius = new CornerRadius(5),
            Background = ThemeHelper.Brush(tint.Soft),
            Child = YYIcons.Make(issue.StudioKind is not null ? "book" : "radio", 10.5, ThemeHelper.Brush(tint.Accent)),
        });
        int mins = Math.Max(1, (int)Math.Round(issue.DurationSec / 60.0));
        kicker.Children.Add(new TextBlock { Text = $"{issue.ShowTitle} · {mins} 分钟", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center });
        sheet.Children.Add(kicker);

        sheet.Children.Add(new TextBlock
        {
            Text = issue.Title,
            FontFamily = serif,
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = ThemeHelper.Brush(p.T1),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 33,
        });

        if (!string.IsNullOrWhiteSpace(issue.Summary))
        {
            sheet.Children.Add(new TextBlock
            {
                Text = issue.Summary,
                FontSize = 12.5,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
            });
        }
        sheet.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 2, 0, 4) });

        var turnsStack = new StackPanel { Spacing = 4 };
        for (int i = 0; i < sentences.Length; i++)
        {
            int idx = i;
            var s = sentences[i];
            if (!string.IsNullOrEmpty(s.SkipReason))
            {
                string skipKey = $"{issue.Id}:{s.Id}";
                bool expanded = _expandedSkipIds.Contains(skipKey);
                var skipWrap = new StackPanel { Spacing = 6 };
                var skipHead = new Grid();
                skipHead.Children.Add(new TextBlock
                {
                    Text = expanded
                        ? $"已展开跳过片段（{s.SkipReason} · {FormatClock(s.T0)}–{FormatClock(s.T1)}）"
                        : $"已静默折叠一段片头/口播（{FormatClock(s.T0)}–{FormatClock(s.T1)}）· 点击展开",
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var toggleSkipBtn = MakePaperPill(expanded ? "折叠" : "展开", accent: false, p, tint);
                toggleSkipBtn.Height = 22;
                toggleSkipBtn.FontSize = 10.5;
                toggleSkipBtn.HorizontalAlignment = HorizontalAlignment.Right;
                toggleSkipBtn.Click += (_, _) =>
                {
                    if (!_expandedSkipIds.Remove(skipKey)) _expandedSkipIds.Add(skipKey);
                    _shelf.RefreshCurrentSubPage();
                };
                skipHead.Children.Add(toggleSkipBtn);
                skipWrap.Children.Add(skipHead);

                if (expanded)
                {
                    skipWrap.Children.Add(new TextBlock
                    {
                        Text = s.TextOrig,
                        FontFamily = serif,
                        FontSize = 13.5,
                        LineHeight = 21,
                        Foreground = ThemeHelper.Brush(p.T2),
                        TextWrapping = TextWrapping.Wrap,
                    });
                    var playSkipBtn = MakePaperPill("▶ 从这里听", accent: false, p, tint);
                    playSkipBtn.Height = 22;
                    playSkipBtn.FontSize = 10.5;
                    playSkipBtn.HorizontalAlignment = HorizontalAlignment.Left;
                    playSkipBtn.Click += (_, _) => { _activeTurnIdx = idx; PlayIssueAtTurn(issue, sentences, idx); _shelf.RefreshCurrentSubPage(); };
                    skipWrap.Children.Add(playSkipBtn);
                }

                turnsStack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(p.Inset),
                    Padding = new Thickness(12, 7, 12, 7),
                    Margin = new Thickness(0, 4, 0, 4),
                    Child = skipWrap,
                });
                continue;
            }

            bool active = idx == _activeTurnIdx;
            bool isClipped = highlightedIds.Contains(s.Id);
            bool peeked = _peekedTurnIndices.Contains(idx);

            var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(10, 10, 12, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = FormatClock(s.T0),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T3),
                Margin = new Thickness(0, 3, 0, 0),
            });

            var body = new StackPanel { Spacing = 4, Opacity = (_blindListen && !active && !peeked) ? 0.16 : 1.0 };
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
                if (isClipped)
                    spLine.Children.Add(new TextBlock { Text = "· 已收书摘", FontSize = 10, Foreground = ThemeHelper.Brush(p.Warn), VerticalAlignment = VerticalAlignment.Center });
                if (_blindListen && !active)
                {
                    var peekPill = new Button
                    {
                        Content = peeked ? "收起" : "偷看",
                        Height = 19,
                        MinHeight = 19,
                        Padding = new Thickness(6, 0, 6, 0),
                        CornerRadius = new CornerRadius(9),
                        FontSize = 10,
                        Foreground = ThemeHelper.Brush(tint.Accent),
                        Background = ThemeHelper.Brush(tint.Soft),
                        BorderThickness = new Thickness(0),
                    };
                    peekPill.Click += (_, _) =>
                    {
                        if (!_peekedTurnIndices.Remove(idx)) _peekedTurnIndices.Add(idx);
                        _shelf.RefreshCurrentSubPage();
                    };
                    spLine.Children.Add(peekPill);
                }
                body.Children.Add(spLine);
            }

            string primaryText = (_track == PodcastTrack.Dub && !string.IsNullOrWhiteSpace(s.TextTrans)) ? s.TextTrans! : s.TextOrig;
            bool isLatin = primaryText.Take(20).Count(c => c < 128 && char.IsLetter(c)) > 8;
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
                    };
                    if (!string.IsNullOrWhiteSpace(s.BookQuote))
                    {
                        var flyout = new Flyout();
                        var pop = new StackPanel { Width = 310, Spacing = 8 };
                        pop.Children.Add(new TextBlock { Text = $"引自《{srcBook.Title}》", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
                        pop.Children.Add(new TextBlock { Text = $"「{s.BookQuote}」", FontFamily = serif, FontSize = 13, LineHeight = 20, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap });
                        var openOrigBtn = MakePaperPill("跳回书页原文 →", accent: true, p, tint);
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
                var intrCard = new StackPanel { Spacing = 3 };
                intrCard.Children.Add(new TextBlock { Text = $"🎙 插话：{intr.Question}", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent), TextWrapping = TextWrapping.Wrap });
                intrCard.Children.Add(new TextBlock { Text = intr.Answer, FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap, LineHeight = 18 });
                body.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(p.Inset),
                    Padding = new Thickness(10, 7, 10, 7),
                    Margin = new Thickness(0, 4, 0, 0),
                    Child = intrCard,
                });
            }

            Grid.SetColumn(body, 1);
            row.Children.Add(body);

            Brush rowBg = active
                ? ThemeHelper.Brush(tint.Soft)
                : isClipped
                    ? ThemeHelper.Brush(Color.FromArgb(dark ? (byte)32 : (byte)42, 245, 206, 98))
                    : new SolidColorBrush(Colors.Transparent);

            var turnCtx = new MenuFlyout();
            var clipThisMi = new MenuFlyoutItem { Text = "原声划线并写想法…" };
            clipThisMi.Click += async (_, _) => { _activeTurnIdx = idx; await ClipCurrentTurnAsync(issue.Id, promptThought: true); };
            var askThisMi = new MenuFlyoutItem { Text = "在这里插话提问…" };
            askThisMi.Click += async (_, _) => await ShowInterruptDialogAsync(issue, s.T0, s.TextOrig);
            turnCtx.Items.Add(clipThisMi);
            turnCtx.Items.Add(askThisMi);

            var turnBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = rowBg,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(0),
                UseSystemFocusVisuals = false,
                ContextFlyout = turnCtx,
                Content = row,
            };
            turnBtn.Click += (_, _) =>
            {
                _activeTurnIdx = idx;
                PlayIssueAtTurn(issue, sentences, idx);
                _shelf.RefreshCurrentSubPage();
            };
            turnsStack.Children.Add(turnBtn);
        }
        sheet.Children.Add(turnsStack);

        // Pinned slim bottom dock with interactive segmented scrubber (ad-span & clip ticks)
        var dockStack = new StackPanel { Spacing = 2, Padding = new Thickness(12, 6, 12, 6) };
        double curSec = (_playing && _playingIssueId == issue.Id && _usingExternalMedia)
            ? _mediaPlayer.PlaybackSession.Position.TotalSeconds
            : (sentences.Length > _activeTurnIdx ? sentences[_activeTurnIdx].T0 : issue.PositionSec);
        double totalDur = Math.Max(1.0, Math.Max(issue.DurationSec, sentences.Length > 0 ? sentences[^1].T1 : 1.0));

        // Visual segment marker strip (ad spans in muted amber, saved clips in accent dots)
        var segCanvas = new Canvas { Height = 4, Margin = new Thickness(6, 0, 6, -4), IsHitTestVisible = false };
        const double scrubWidth = 584.0;
        var clipsForBar = _library.Core.PodcastClips(issue.Id);
        foreach (var s in sentences.Where(x => !string.IsNullOrEmpty(x.SkipReason)))
        {
            double x0 = Math.Clamp(s.T0 / totalDur, 0, 1) * scrubWidth;
            double x1 = Math.Clamp(s.T1 / totalDur, 0, 1) * scrubWidth;
            var adMark = new Border
            {
                Width = Math.Max(3, x1 - x0),
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = ThemeHelper.Brush(p.Warn),
                Opacity = 0.55,
            };
            Canvas.SetLeft(adMark, x0);
            Canvas.SetTop(adMark, 0.5);
            segCanvas.Children.Add(adMark);
        }
        foreach (var cl in clipsForBar)
        {
            double cx = Math.Clamp(cl.T0 / totalDur, 0, 1) * scrubWidth;
            var dot = new Border
            {
                Width = 5,
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Background = ThemeHelper.Brush(tint.Accent),
            };
            Canvas.SetLeft(dot, Math.Max(0, cx - 2.5));
            Canvas.SetTop(dot, -0.5);
            segCanvas.Children.Add(dot);
        }

        var scrubSlider = new Slider
        {
            Minimum = 0,
            Maximum = totalDur,
            Value = Math.Clamp(curSec, 0, totalDur),
            StepFrequency = 1,
            Height = 20,
            Margin = new Thickness(0, -4, 0, -4),
        };
        scrubSlider.PointerCaptureLost += (_, _) => SeekIssueToSeconds(issue, sentences, scrubSlider.Value);
        dockStack.Children.Add(segCanvas);
        dockStack.Children.Add(scrubSlider);

        var dock = new Grid { MaxWidth = 620, Height = 36, ColumnSpacing = 8 };
        dock.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dock.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dock.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftTrans = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var prevTurnBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Width = 30, Height = 30, Content = YYIcons.Make("goback-15", 14, ThemeHelper.Brush(p.T1)) };
        prevTurnBtn.Click += (_, _) => StepPlayback(issue, sentences, forward: false, deltaSec: 15);

        var mainPlayBtn = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            Background = ThemeHelper.Brush(p.T1),
            BorderThickness = new Thickness(0),
            Content = YYIcons.Make(_playing ? "pause" : "play", 11.5, ThemeHelper.Brush(p.Bg), filled: true),
        };
        mainPlayBtn.Click += (_, _) =>
        {
            if (_playing) StopPlayback();
            else PlayIssueAtTurn(issue, sentences, _activeTurnIdx);
            _shelf.RefreshCurrentSubPage();
        };

        var nextTurnBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Width = 30, Height = 30, Content = YYIcons.Make("goforward-30", 14, ThemeHelper.Brush(p.T1)) };
        nextTurnBtn.Click += (_, _) => StepPlayback(issue, sentences, forward: true, deltaSec: 30);

        leftTrans.Children.Add(prevTurnBtn);
        leftTrans.Children.Add(mainPlayBtn);
        leftTrans.Children.Add(nextTurnBtn);
        dock.Children.Add(leftTrans);

        var centerProg = new TextBlock
        {
            Text = $"{FormatClock(curSec)} / {FormatClock(totalDur)}  ·  第 {Math.Min(_activeTurnIdx + 1, Math.Max(1, sentences.Length))} / {Math.Max(1, sentences.Length)} 段",
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(centerProg, 1);
        dock.Children.Add(centerProg);

        var rightTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var rateBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = $"{_rate:0.##}×", FontSize = 11.5, Padding = new Thickness(6, 2, 6, 2) };
        rateBtn.Click += (_, _) => CyclePlaybackRate();
        rightTools.Children.Add(rateBtn);

        var interSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        interSp.Children.Add(YYIcons.Make("mic", 11.5, ThemeHelper.Brush(tint.Accent)));
        interSp.Children.Add(new TextBlock { Text = "插话", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
        var interBtn = new Button
        {
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(tint.Soft),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = interSp,
        };
        interBtn.Click += async (_, _) => await ShowInterruptDialogAsync(issue, curSec, sentences.ElementAtOrDefault(_activeTurnIdx)?.TextOrig);
        rightTools.Children.Add(interBtn);

        Grid.SetColumn(rightTools, 2);
        dock.Children.Add(rightTools);
        dockStack.Children.Add(dock);

        var dockBorder = new Border
        {
            Width = 620,
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(dark ? Color.FromArgb(252, 38, 37, 35) : Color.FromArgb(254, 252, 250, 246)),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Child = dockStack,
        };
        _shelf.SetBottomDock(dockBorder);

        return sheet;
    }

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
        if (coachTerms.Count == 0)
        {
            return new TextBlock
            {
                Text = $"💡 {coachNote}",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(tint.Accent),
                TextWrapping = TextWrapping.Wrap,
            };
        }

        var rtb = new RichTextBlock
        {
            FontSize = 11.5,
            LineHeight = 19,
            Foreground = ThemeHelper.Brush(tint.Accent),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = false,
        };
        var para = new Paragraph { LineHeight = 19 };
        para.Inlines.Add(new Run { Text = "💡 " });
        int idx = 0;
        foreach (var kv in coachTerms)
        {
            if (idx > 0) para.Inlines.Add(new Run { Text = "  ·  " });
            string term = kv.Key;
            string gloss = kv.Value;
            para.Inlines.Add(MakeDottedWordInline(term, new FontFamily("Georgia"), 11.5, highlighted: true, p, tint, anchor =>
                ShowVocabPopover(anchor, term, gloss, contextText, issueId, turnT0, p, tint)));
            para.Inlines.Add(new Run { Text = $"：{gloss}" });
            idx++;
        }
        rtb.Blocks.Add(para);
        return rtb;
    }

    private static InlineUIContainer MakeDottedWordInline(
        string wordText,
        FontFamily font,
        double fontSize,
        bool highlighted,
        YYPalette p,
        YYTintColors tint,
        Action<FrameworkElement> onClick)
    {
        var wordTb = new TextBlock
        {
            Text = wordText,
            FontFamily = font,
            FontSize = fontSize,
            FontWeight = highlighted ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = ThemeHelper.Brush(highlighted ? tint.Accent : p.T1),
            Padding = new Thickness(0),
        };
        var dashLine = new XamlLine
        {
            X1 = 0,
            X2 = Math.Max(12, wordText.Length * fontSize * 0.52),
            Y1 = 0,
            Y2 = 0,
            Stroke = ThemeHelper.Brush(highlighted ? tint.Accent : p.T3),
            StrokeThickness = highlighted ? 1.3 : 1.05,
            StrokeDashArray = new DoubleCollection { 1.5, 2.0 },
            Opacity = highlighted ? 0.85 : 0.52,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 1),
            IsHitTestVisible = false,
        };
        wordTb.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0) dashLine.X2 = e.NewSize.Width;
        };
        var cell = new Grid();
        cell.Children.Add(wordTb);
        cell.Children.Add(dashLine);

        var btn = new Button
        {
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            UseSystemFocusVisuals = false,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -2),
            Content = cell,
        };
        btn.Click += (_, _) => onClick(btn);
        return new InlineUIContainer { Child = btn };
    }

    private FrameworkElement BuildRichPodcastText(
        string text,
        FontFamily font,
        HashSet<string> vocabWords,
        Dictionary<string, string>? coachTerms,
        string issueId,
        double turnT0,
        YYPalette p,
        YYTintColors tint)
    {
        bool hasLatin = text.Any(c => c < 128 && char.IsLetter(c));
        bool hasCoachInText = coachTerms is { Count: > 0 } && coachTerms.Keys.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (!hasLatin && !hasCoachInText)
        {
            return new TextBlock
            {
                FontFamily = font,
                FontSize = 16,
                LineHeight = 26,
                Foreground = ThemeHelper.Brush(p.T1),
                TextWrapping = TextWrapping.Wrap,
                Text = text,
            };
        }

        var rtb = new RichTextBlock
        {
            FontFamily = font,
            FontSize = 16,
            LineHeight = 26,
            Foreground = ThemeHelper.Brush(p.T1),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = false,
        };
        var para = new Paragraph { LineHeight = 26 };

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
                    para.Inlines.Add(new Run { Text = text[pos..] });
                    break;
                }
                if (bestIdx > pos)
                {
                    para.Inlines.Add(new Run { Text = text[pos..bestIdx] });
                }
                string surface = text.Substring(bestIdx, bestTerm.Length);
                string hint = coachTerms[bestTerm];
                string w = bestTerm;
                string sentenceCtx = text;
                para.Inlines.Add(MakeDottedWordInline(surface, font, 16, highlighted: true, p, tint, anchor =>
                    ShowVocabPopover(anchor, w, hint, sentenceCtx, issueId, turnT0, p, tint)));
                pos = bestIdx + bestTerm.Length;
            }
            rtb.Blocks.Add(para);
            return rtb;
        }

        char[] trimPunct = [',', '.', ';', ':', '!', '?', '"', '\'', '“', '”', '(', ')', '[', ']', '—', '…'];
        var parts = text.Split(' ');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) para.Inlines.Add(new Run { Text = " " });
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
            bool isLookupWord = isVocab || isCoach || (clean.Length >= 3 && clean.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-' || c == '\''));

            if (isLookupWord)
            {
                if (start > 0) para.Inlines.Add(new Run { Text = rawToken[..start] });
                string w = clean;
                string? hint = coachHint;
                string sentenceCtx = text;
                para.Inlines.Add(MakeDottedWordInline(clean, font, 16, highlighted: isVocab || isCoach, p, tint, anchor =>
                    ShowVocabPopover(anchor, w, hint, sentenceCtx, issueId, turnT0, p, tint)));
                if (end < rawToken.Length) para.Inlines.Add(new Run { Text = rawToken[end..] });
            }
            else
            {
                para.Inlines.Add(new Run { Text = rawToken });
            }
        }
        rtb.Blocks.Add(para);
        return rtb;
    }

    private void ShowVocabPopover(
        FrameworkElement anchor,
        string word,
        string? coachHint,
        string context,
        string issueId,
        double turnT0,
        YYPalette p,
        YYTintColors tint)
    {
        var hit = _library.LookUpWordRich(word);
        string headword = hit?.Headword ?? word.ToLowerInvariant();
        bool alreadySaved = _library.GetWords().Any(x => x.Word.Equals(headword, StringComparison.OrdinalIgnoreCase) && !x.Known);

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
            Text = !string.IsNullOrWhiteSpace(combinedMeaning) ? combinedMeaning : "词典中未查到该词，仍可收进生词本稍后复习。",
            FontSize = 12,
            LineHeight = 18,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var pronBtn = MakePaperPill("▶ 读一遍", accent: false, p, tint);
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
        var saveBtn = MakePaperPill(alreadySaved ? "✓ 已在生词本" : "+ 收进生词本", accent: !alreadySaved, p, tint);
        saveBtn.Height = 24;
        saveBtn.FontSize = 11;
        saveBtn.IsEnabled = !alreadySaved;
        saveBtn.Click += (_, _) =>
        {
            try
            {
                string podcastBookKey = "podcast:" + issueId;
                uint sentSec = (uint)Math.Max(0, (int)Math.Round(turnT0));
                _library.Core.AddWord(headword, hit?.Phonetic, combinedMeaning, podcastBookKey, 0, sentSec, context);
                _library.Core.SetWordKnown(headword, false);
                saveBtn.Content = "✓ 已收进生词本";
                saveBtn.IsEnabled = false;
                _shelf.ShowToast($"已收进生词本：{headword}", canUndo: false);
                _shelf.RefreshCurrentSubPage();
            }
            catch { }
        };
        actions.Children.Add(pronBtn);
        actions.Children.Add(saveBtn);
        pop.Children.Add(actions);

        flyout.ShowAt(anchor);
    }

    // ---------- Dual-Voice Studio Playback & Media Tracking ----------

    private void SwitchTrack(string issueId, PodcastIssue? issue, PodcastTrack nextTr)
    {
        _track = nextTr;
        try { _library.Core.PodcastSetViewMode(issueId, PodcastEdition.Full, nextTr); } catch { }
        if (_playing && _playingIssueId == issueId && issue is not null)
        {
            PlayIssueAtTurn(issue, _activeSentences.Length > 0 ? _activeSentences : LoadOrSynthesizeTranscript(issue), _activeTurnIdx);
        }
        _shelf.RefreshCurrentSubPage();
    }

    private void CyclePlaybackRate()
    {
        double[] rs = [0.8, 1.0, 1.25, 1.5, 2.0];
        int rIdx = Array.FindIndex(rs, x => Math.Abs(x - _rate) < 0.05);
        _rate = rs[(rIdx + 1) % rs.Length];
        _ttsPlayer.SetRate(_rate);
        try { _mediaPlayer.PlaybackSession.PlaybackRate = _rate; } catch { }
        _shelf.RefreshCurrentSubPage();
    }

    private void SeekIssueToSeconds(PodcastIssue issue, MagazineSentence[] sentences, double targetSec)
    {
        double clamped = Math.Clamp(targetSec, 0, Math.Max(1.0, issue.DurationSec));
        try { _library.Core.PodcastSavePlayback(issue.Id, clamped, null); } catch { }

        if (_usingExternalMedia && _playing && _playingIssueId == issue.Id)
        {
            try { _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(clamped); } catch { }
            if (sentences.Length > 0)
            {
                int idx = Array.FindLastIndex(sentences, s => clamped >= s.T0);
                if (idx >= 0) _activeTurnIdx = idx;
            }
            _shelf.RefreshCurrentSubPage();
            return;
        }

        if (sentences.Length > 0)
        {
            int idx = Array.FindLastIndex(sentences, s => clamped >= s.T0);
            _activeTurnIdx = Math.Max(0, idx);
            if (_playing && _playingIssueId == issue.Id)
                PlayIssueAtTurn(issue, sentences, _activeTurnIdx);
        }
        _shelf.RefreshCurrentSubPage();
    }

    private void StepPlayback(PodcastIssue issue, MagazineSentence[] sentences, bool forward, double deltaSec = 15)
    {
        if (_usingExternalMedia && _playing && _playingIssueId == issue.Id)
        {
            double cur = _mediaPlayer.PlaybackSession.Position.TotalSeconds;
            SeekIssueToSeconds(issue, sentences, cur + (forward ? deltaSec : -deltaSec));
            return;
        }

        int step = forward ? 1 : -1;
        int next = Math.Clamp(_activeTurnIdx + step, 0, Math.Max(0, sentences.Length - 1));
        _activeTurnIdx = next;
        PlayIssueAtTurn(issue, sentences, _activeTurnIdx);
        _shelf.RefreshCurrentSubPage();
    }

    private Action CreateShelfPauseCallback() => () =>
    {
        if (_isPreviewingInterruptAnswer) return;
        _shelf.DispatcherQueue.TryEnqueue(() =>
        {
            if (_isPreviewingInterruptAnswer) return;
            StopPlayback();
            _shelf.RefreshCurrentSubPage();
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
            {
                return clonedCard is not null ? clonedCard.Id : clonedId;
            }
            // When a cloned voice is host 0, pick a distinct partner voice for host 1 (1:1 PodcastHub.swift:1172-1190)
        }

        if (style == "studio-solo-zh") secondHost = false;

        // Respect PodcastRememberedVoices toggles from the "声音卡" drawer when no cloned override is active
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

        // 1. LiveVoice neural cards (both Chinese and English when matching cards exist)
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

        // 2. Kokoro offline neural voices
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

        // 3. Edge online neural voices (zero API key)
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

        // 4. Windows SAPI fallback (gender-matched)
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

    private void PlayIssueAtTurn(PodcastIssue issue, MagazineSentence[] sentences, int idx)
    {
        App.CurrentApp.ClaimAudioPlayback(_shelf, CreateShelfPauseCallback());

        _playing = true;
        _playingIssueId = issue.Id;
        _activeSentences = sentences;
        _activeTurnIdx = Math.Clamp(idx, 0, Math.Max(0, sentences.Length - 1));

        _library.SystemIntegration.SetAudioPlaying(_shelf, true, issue.Title, () =>
            _shelf.DispatcherQueue.TryEnqueue(() =>
            {
                if (_playing) StopPlayback();
                else PlayIssueAtTurn(issue, _activeSentences, _activeTurnIdx);
                _shelf.RefreshCurrentSubPage();
            }));
        UpdateSmtcState(issue, playing: true);

        bool hasAudioStream = (!string.IsNullOrEmpty(issue.LocalAudioPath) && File.Exists(issue.LocalAudioPath))
            || !string.IsNullOrEmpty(issue.AudioUrl);
        bool wantsTranslatedDub = _track == PodcastTrack.Dub && sentences.Any(s => !string.IsNullOrWhiteSpace(s.TextTrans));
        bool wantsCoachNarration = _track == PodcastTrack.Coach && sentences.Any(s => !string.IsNullOrWhiteSpace(s.CoachNote));

        if (hasAudioStream && !wantsTranslatedDub && !wantsCoachNarration)
        {
            try
            {
                _ttsPlayer.Stop();
                string src = !string.IsNullOrEmpty(issue.LocalAudioPath) && File.Exists(issue.LocalAudioPath)
                    ? issue.LocalAudioPath
                    : issue.AudioUrl!;
                _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(src));
                _mediaPlayer.Volume = 1.0;
                _mediaPlayer.PlaybackSession.PlaybackRate = _rate;
                double seekSec = _activeTurnIdx < sentences.Length ? Math.Max(0, sentences[_activeTurnIdx].T0) : Math.Max(0, issue.PositionSec);
                _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(seekSec);
                _mediaPlayer.Play();
                _usingExternalMedia = true;
                _mediaPosTimer.Start();
                return;
            }
            catch { }
        }

        // Studio / Translated Dub / Bilingual Coach turn-by-turn dual-voice synthesis
        _usingExternalMedia = false;
        _mediaPosTimer.Stop();
        try { _mediaPlayer.Pause(); } catch { }

        if (_activeTurnIdx >= 0 && _activeTurnIdx < sentences.Length)
        {
            var s = sentences[_activeTurnIdx];
            string spk = s.Speaker ?? "";
            bool secondHost = spk.Contains("苏") || spk.Contains("沈") || spk.Contains("Clara", StringComparison.OrdinalIgnoreCase)
                || (_activeTurnIdx % 2 == 1 && string.IsNullOrEmpty(spk));
            bool isEn = (issue.Language ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase) && _track != PodcastTrack.Dub;
            string targetLang = isEn ? "en-US" : "zh-Hans";

            string? hostVid = PickHostVoiceId(secondHost, isEn);
            _ttsPlayer.SetBookContext(issue.Id, targetLang);
            _ttsPlayer.SetVoice(hostVid);
            _ttsPlayer.SetRate(_rate);

            string text = _track switch
            {
                PodcastTrack.Dub when !string.IsNullOrWhiteSpace(s.TextTrans) => s.TextTrans!,
                PodcastTrack.Coach when !string.IsNullOrWhiteSpace(s.CoachNote) =>
                    $"{s.TextOrig}。助教提示：{s.CoachNote}",
                _ => s.TextOrig,
            };
            try { _library.Core.PodcastSavePlayback(issue.Id, s.T0, issue.DurationSec); } catch { }
            _ = _ttsPlayer.SpeakSentenceAsync(text, issue.Title, issue.ShowTitle, issue.Id, targetLang);
        }
    }

    private void OnStudioTurnFinished()
    {
        _shelf.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_playing || _usingExternalMedia || _playingIssueId is null) return;
            var issue = _library.Podcasts.GetIssues().FirstOrDefault(x => x.Id == _playingIssueId);
            if (issue is null) { StopPlayback(); return; }

            int next = _activeTurnIdx + 1;
            while (next < _activeSentences.Length && !string.IsNullOrEmpty(_activeSentences[next].SkipReason))
                next++;

            if (next < _activeSentences.Length)
            {
                _activeTurnIdx = next;
                PlayIssueAtTurn(issue, _activeSentences, next);
                if (OpenIssueId == issue.Id) _shelf.RefreshCurrentSubPage();
            }
            else
            {
                StopPlayback();
                try { _library.Core.PodcastSavePlayback(issue.Id, issue.DurationSec, issue.DurationSec); } catch { }
                if (OpenIssueId == issue.Id) _shelf.RefreshCurrentSubPage();
            }
        });
    }

    private void OnMediaPosTick(object? sender, object e)
    {
        if (!_playing || !_usingExternalMedia || _playingIssueId is null) return;
        double pos = _mediaPlayer.PlaybackSession.Position.TotalSeconds;
        try { _library.Core.PodcastSavePlayback(_playingIssueId, pos, null); } catch { }

        if (_activeSentences.Length > 0)
        {
            int idx = Array.FindLastIndex(_activeSentences, s => pos >= s.T0);
            if (idx >= 0 && idx != _activeTurnIdx)
            {
                _activeTurnIdx = idx;
                if (OpenIssueId == _playingIssueId) _shelf.RefreshCurrentSubPage();
            }
        }
    }

    private void StopPlayback()
    {
        _playing = false;
        _usingExternalMedia = false;
        _mediaPosTimer.Stop();
        _ttsPlayer.Stop();
        try { _mediaPlayer.Pause(); } catch { }
        _library.SystemIntegration.SetAudioPlaying(_shelf, false);
        UpdateSmtcState(null, playing: false);
    }

    // ---------- 原声划线 (ResolveClipWindow + WAV Slice) & 插话 (-24 dB Ducking + 语音提问 + -3s Rewind) ----------

    private async Task EditClipThoughtAsync(PodcastClip item)
    {
        var tBox = new TextBox
        {
            Text = item.Thought ?? "",
            PlaceholderText = "写下你对这句原声的思考…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 72,
        };
        var dlg = new ContentDialog
        {
            XamlRoot = _shelf.WindowXamlRoot,
            Title = "编辑原声书摘想法",
            Content = new StackPanel
            {
                Width = 400,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"「{item.QuoteText}」", FontSize = 12.5, Opacity = 0.8, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap },
                    tBox,
                },
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        string? nextThought = string.IsNullOrWhiteSpace(tBox.Text) ? null : tBox.Text.Trim();
        try
        {
            _library.Core.PodcastDeleteClip(item.Id);
            _library.Core.PodcastAddClip(item.IssueId, item.T0, item.T1, item.Speaker, item.QuoteText, nextThought, item.AudioRelPath);
            _shelf.ShowToast("已更新想法", canUndo: false);
            _shelf.RefreshCurrentSubPage();
        }
        catch { }
    }

    private async Task ClipCurrentTurnAsync(string issueId, bool promptThought = false)
    {
        var issue = _library.Podcasts.GetIssues().FirstOrDefault(i => i.Id == issueId);
        if (issue is null) return;
        var sents = LoadOrSynthesizeTranscript(issue);
        double curSec = _usingExternalMedia
            ? _mediaPlayer.PlaybackSession.Position.TotalSeconds
            : (sents.ElementAtOrDefault(_activeTurnIdx)?.T0 ?? issue.PositionSec);

        var win = YuyueCoreMethods.ResolveClipWindow(sents, curSec, Math.Max(10.0, issue.DurationSec));
        string quote = !string.IsNullOrWhiteSpace(win.QuoteText)
            ? win.QuoteText
            : (sents.ElementAtOrDefault(_activeTurnIdx)?.TextOrig ?? issue.Title);

        string? thought = null;
        if (promptThought)
        {
            var tBox = new TextBox { PlaceholderText = "写一句此刻听到的想法（可留空直接收进原声书摘）…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70 };
            var dlg = new ContentDialog
            {
                XamlRoot = _shelf.WindowXamlRoot,
                Title = "原声划线",
                Content = new StackPanel
                {
                    Width = 400,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = $"「{quote}」", FontSize = 12.5, Opacity = 0.8, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap },
                        tBox,
                    },
                },
                PrimaryButtonText = "收进原声书摘",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            thought = string.IsNullOrWhiteSpace(tBox.Text) ? null : tBox.Text.Trim();
        }

        string spkForClip = win.Speaker ?? sents.ElementAtOrDefault(_activeTurnIdx)?.Speaker ?? "";
        bool clipSecondHost = spkForClip.Contains("苏") || spkForClip.Contains("沈") || spkForClip.Contains("Clara", StringComparison.OrdinalIgnoreCase);
        bool clipIsEn = (issue.Language ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase);
        string? clipVoiceId = PickHostVoiceId(clipSecondHost, clipIsEn);

        string? audioRelPath = await _library.Podcasts.SliceLocalAudioClipAsync(issue, win.T0, win.T1, default, quote, clipVoiceId);
        try
        {
            _library.Core.PodcastAddClip(issueId, win.T0, win.T1, win.Speaker, quote, thought, audioRelPath);
            if (!_drawerOpen) _hasNewClipDot = true;
            _shelf.ShowToast(audioRelPath is not null ? "已裁切原声音频并收入「原声书摘」" : "已记下原声片段，收入「原声书摘」", canUndo: false);
            _shelf.RefreshCurrentSubPage();
        }
        catch { }
    }

    private async Task ShowInterruptDialogAsync(PodcastIssue issue, double atSec, string? contextLine)
    {
        // 1:1 PodcastHub.swift — Duck external audio to -24 dB (0.063) or pause Studio TTS
        bool wasPlayingExternal = _playing && _usingExternalMedia;
        bool wasPlayingTts = _playing && !_usingExternalMedia;
        if (wasPlayingExternal)
        {
            try { _mediaPlayer.Volume = 0.063; } catch { }
        }
        else if (wasPlayingTts)
        {
            _ttsPlayer.Pause();
        }

        bool dark = ThemeHelper.IsDark(_shelf.WindowXamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        string latestReply = "";
        CancellationTokenSource? micCts = null;
        var qBox = new TextBox
        {
            PlaceholderText = "打断主播追问一个细节，书伴会结合上下文为你解答…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 68,
        };

        // Live voice dictation + quick-ask pills
        var micBtn = MakePaperPill("🎙 语音提问", accent: true, p, tint);
        micBtn.Height = 25;
        micBtn.FontSize = 11;
        micBtn.Click += async (_, _) =>
        {
            if (micCts is not null)
            {
                micCts.Cancel();
                return;
            }
            micCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            micBtn.Content = "⏹ 正在聆听…";
            try
            {
                string lang = string.IsNullOrWhiteSpace(issue.Language) ? "zh-CN" : issue.Language;
                var (text, err) = await WindowsTranscriber.RecognizeMicrophoneOnceAsync(lang, micCts.Token);
                if (!string.IsNullOrWhiteSpace(text))
                    qBox.Text = string.IsNullOrWhiteSpace(qBox.Text) ? text : $"{qBox.Text.Trim()} {text}";
                else if (!string.IsNullOrWhiteSpace(err))
                    _shelf.ShowToast(err, canUndo: false);
            }
            finally
            {
                micCts?.Dispose();
                micCts = null;
                micBtn.Content = "🎙 语音提问";
            }
        };

        var quickChips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        quickChips.Children.Add(micBtn);
        foreach (string prompt in new[] { "这句话什么意思？", "展开讲讲这个背景", "和我书架的书有什么联系？" })
        {
            string pr = prompt;
            var chip = MakePaperPill(pr, accent: false, p, tint);
            chip.Height = 25;
            chip.FontSize = 10.5;
            chip.Click += (_, _) => qBox.Text = pr;
            quickChips.Children.Add(chip);
        }

        var ansTb = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        var speakReplyBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
            Content = "▶ 听主播回答",
            Height = 26,
            FontSize = 11.5,
            Visibility = Visibility.Collapsed,
        };
        speakReplyBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(latestReply)) return;
            bool replyEn = !latestReply.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);
            _isPreviewingInterruptAnswer = true;
            try
            {
                await AudioPlayer.PreviewVoiceAsync(_library, PickHostVoiceId(false, replyEn), 1.0, latestReply);
            }
            finally
            {
                _isPreviewingInterruptAnswer = false;
                if (_playing)
                    App.CurrentApp.ClaimAudioPlayback(_shelf, CreateShelfPauseCallback());
            }
        };

        var sp = new StackPanel { Width = 450, Spacing = 9 };
        if (wasPlayingExternal)
            sp.Children.Add(new TextBlock { Text = "🔉 原节目已压低至 -24 dB 背景伴音，收起后自动倒回 3 秒续播", FontSize = 11, Opacity = 0.68 });
        if (!string.IsNullOrWhiteSpace(contextLine))
            sp.Children.Add(new TextBlock { Text = $"当前句：{contextLine}", FontSize = 11.5, Opacity = 0.75, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = quickChips });
        sp.Children.Add(qBox);
        sp.Children.Add(ansTb);
        sp.Children.Add(speakReplyBtn);

        var dlg = new ContentDialog
        {
            XamlRoot = _shelf.WindowXamlRoot,
            Title = "🎙 插话提问",
            Content = sp,
            PrimaryButtonText = "提问",
            CloseButtonText = "收起并继续听",
            DefaultButton = ContentDialogButton.Primary,
        };
        dlg.PrimaryButtonClick += async (_, args) =>
        {
            string q = qBox.Text.Trim();
            if (string.IsNullOrEmpty(q)) { args.Cancel = true; return; }
            args.Cancel = true;
            ansTb.Text = "正在向主播/书伴提问…";
            try
            {
                string reply = _library.Companion.IsConfigured
                    ? await _library.Companion.ChatAsync(
                        $"播客《{issue.Title}》听众在 {FormatClock(atSec)} 处插话问：{q}\n当前原文：{contextLine}",
                        "你是语阅书房电台的主播，请简明亲切地回答听众插话（120字以内），绝不剧透未读后文。",
                        source: UsageSource.Podcast)
                    : "请先在「设置 · 书伴」中连接大模型 API Key 或本地 Ollama 后即可实时插话问答。";
                latestReply = reply.Trim();
                ansTb.Text = latestReply;
                if (_library.Companion.IsConfigured)
                {
                    _library.Core.PodcastAddInterrupt(issue.Id, atSec, q, latestReply);
                    speakReplyBtn.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex) { ansTb.Text = $"提问失败：{ex.Message}"; }
        };

        await dlg.ShowAsync();
        _isPreviewingInterruptAnswer = false;
        micCts?.Cancel();

        // Restore volume & rewind 3 seconds on resume (1:1 PodcastHub.swift)
        if (wasPlayingExternal)
        {
            try
            {
                double cur = _mediaPlayer.PlaybackSession.Position.TotalSeconds;
                _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, cur - 3.0));
                _mediaPlayer.Volume = 1.0;
            }
            catch { }
        }
        else if (wasPlayingTts)
        {
            _ttsPlayer.Resume();
        }

        if (!string.IsNullOrEmpty(latestReply) && OpenIssueId == issue.Id)
            _shelf.RefreshCurrentSubPage();
    }

    // ---------- 4. PodcastStudioSheet（700px 酿一期书房电台 1:1） ----------

    public async Task ShowStudioSheetAsync(StudioKind initialKind = StudioKind.WeeklyDigest, string? preselectedBookId = null)
    {
        bool dark = ThemeHelper.IsDark(_shelf.WindowXamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
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
        bool confirmedGenerate = false;
        ContentDialog? dlgRef = null;

        var allBooks = _library.GetKeptBooks().Where(b => b.Format != BookFormat.Audio).ToArray();
        if (allBooks.Any(b => b.Status == ReadingStatus.Reading)) bookFilter = "reading";

        var selectedBooks = new HashSet<string>();
        if (!string.IsNullOrEmpty(preselectedBookId)) selectedBooks.Add(preselectedBookId);
        else
        {
            foreach (var b in allBooks.Where(b => b.Status == ReadingStatus.Reading).Take(initialKind == StudioKind.Roundtable ? 2 : 1))
                selectedBooks.Add(b.Id);
            if (selectedBooks.Count == 0 && allBooks.Length > 0) selectedBooks.Add(allBooks[0].Id);
        }

        // 1:1 shelf-show-make/1.png: 720px frameless two-column studio sheet
        var headerSubtitle = new TextBlock
        {
            FontSize = 13,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 2, 0, 0),
        };
        var leftFormats = new StackPanel { Spacing = 4, Padding = new Thickness(8) };
        var spoilerSegHost = new Grid { ColumnSpacing = 2 };
        spoilerSegHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        spoilerSegHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var refScopeSub = new TextBlock { FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
        var refCountText = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };

        var searchBox = new TextBox
        {
            PlaceholderText = $"搜书名、作者（共 {allBooks.Length} 本）…",
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 5, 0, 5),
        };
        var searchPill = new Grid
        {
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(8),
            Background = ThemeHelper.Brush(p.Inset),
            ColumnSpacing = 6,
        };
        searchPill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchPill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchPill.Children.Add(YYIcons.Make("search", 12.5, ThemeHelper.Brush(p.T3)));
        Grid.SetColumn(searchBox, 1);
        searchPill.Children.Add(searchBox);

        var filterSegGrid = new Grid { ColumnSpacing = 2 };
        for (int c = 0; c < 3; c++) filterSegGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var filterSegWrap = new Border { CornerRadius = new CornerRadius(8), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(2), Child = filterSegGrid };

        var booksBodyHost = new ContentControl();
        var topicBox = new TextBox
        {
            PlaceholderText = "选填 · 留空则按最近划线自动拟题",
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 6, 0, 6),
        };

        uint[] durTicks = [5, 10, 15, 30, 45, 60];
        int durIdx = Math.Max(0, Array.IndexOf(durTicks, durationMin));
        var durSlider = new Slider { Minimum = 0, Maximum = durTicks.Length - 1, StepFrequency = 1, Value = durIdx, VerticalAlignment = VerticalAlignment.Center };
        var durBadgeText = new TextBlock { Text = $"{durationMin} 分钟", FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var tickLabelsGrid = new Grid();
        for (int ti = 0; ti < durTicks.Length; ti++)
            tickLabelsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var voiceOptions = new List<(string Id, string ShortLabel, string FullLabel)>
        {
            ("studio-duo-zh", "林深 · 苏晚", "林深 · 苏晚（中文双人对谈）"),
            ("studio-duo-en", "Clara · David", "Clara & David（英文双人对谈）"),
            ("studio-solo-zh", "林深 · 独白", "林深 · 单人深度导读"),
        };
        foreach (var cc in _library.LiveVoice.GetClonedCards())
            voiceOptions.Add(("card:" + cc.Id, cc.Name, $"✦ {cc.Name}（我的克隆声音）"));

        string ShortHostName(string id) => voiceOptions.FirstOrDefault(v => v.Id == id).ShortLabel ?? "林深 · 苏晚";
        var hostVoiceLabel = new TextBlock { Text = ShortHostName(voiceStyle), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };

        void RebuildTickLabels()
        {
            tickLabelsGrid.Children.Clear();
            for (int ti = 0; ti < durTicks.Length; ti++)
            {
                bool active = durTicks[ti] == durationMin;
                var tl = new TextBlock
                {
                    Text = durTicks[ti].ToString(),
                    FontSize = 10.5,
                    FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T3),
                    HorizontalAlignment = ti == 0 ? HorizontalAlignment.Left : (ti == durTicks.Length - 1 ? HorizontalAlignment.Right : HorizontalAlignment.Center),
                };
                Grid.SetColumn(tl, ti);
                tickLabelsGrid.Children.Add(tl);
            }
        }

        void RebuildForm()
        {
            headerSubtitle.Text = chosenKind switch
            {
                StudioKind.BookGuide => "·  读前热身精讲 · 拆解核心脉络",
                StudioKind.Roundtable => "·  多书观点碰撞 · 串联不同视角",
                StudioKind.VocabRadio => "·  把生词本织进自然对话",
                _ => "·  串起近期划线与阅读心绪",
            };
            refScopeSub.Text = chosenKind == StudioKind.BookGuide ? "单选精讲" : (chosenKind == StudioKind.VocabRadio ? "生词本" : "可多选串讲");
            refCountText.Text = chosenKind == StudioKind.VocabRadio ? "" : $"已选 {selectedBooks.Count} 本";

            leftFormats.Children.Clear();
            (StudioKind K, string Icon, string Name, string Sub)[] kinds =
            [
                (StudioKind.WeeklyDigest, "checklist-lines", "书房周刊", "串起本周划线"),
                (StudioKind.BookGuide, "book-open", "难书导读", "精讲难懂章节"),
                (StudioKind.Roundtable, "circles-venn", "跨书圆桌", "多书观点对谈"),
                (StudioKind.VocabRadio, "vocab", "生词外语", "生词自然伴听"),
            ];
            foreach (var (k, ic, nm, sub) in kinds)
            {
                bool on = chosenKind == k;
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(YYIcons.Make(ic, 14, ThemeHelper.Brush(on ? tint.Accent : p.T2)));
                var sp = new StackPanel { Spacing = 1 };
                sp.Children.Add(new TextBlock { Text = nm, FontSize = 12.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Medium, Foreground = ThemeHelper.Brush(on ? p.T1 : p.T1) });
                sp.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T3) });
                Grid.SetColumn(sp, 1);
                row.Children.Add(sp);
                var b = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 8, 10, 8),
                    CornerRadius = new CornerRadius(10),
                    Background = on ? ThemeHelper.Brush(p.Inset) : new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = row,
                };
                var nextK = k;
                b.Click += (_, _) =>
                {
                    chosenKind = nextK;
                    if (chosenKind == StudioKind.BookGuide && selectedBooks.Count > 1)
                    {
                        string keep = selectedBooks.First();
                        selectedBooks.Clear();
                        selectedBooks.Add(keep);
                    }
                    RebuildForm();
                };
                leftFormats.Children.Add(b);
            }

            // Spoiler control: 🔒 仅已读 | 全书
            spoilerSegHost.Children.Clear();
            foreach (var (col, isSafe, lbl) in new[] { (0, true, "仅已读"), (1, false, "全书") })
            {
                bool on = noSpoiler == isSafe;
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (isSafe) sp.Children.Add(YYIcons.Make("lock", 10.5, ThemeHelper.Brush(on ? tint.Accent : p.T2)));
                sp.Children.Add(new TextBlock { Text = lbl, FontSize = 11.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = ThemeHelper.Brush(on ? (isSafe ? tint.Accent : p.T1) : p.T2) });
                var sb = new Button
                {
                    Content = sp,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = 26,
                    MinHeight = 26,
                    Padding = new Thickness(0),
                    Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(7),
                    UseSystemFocusVisuals = false,
                };
                bool nextSafe = isSafe;
                sb.Click += (_, _) => { noSpoiler = nextSafe; RebuildForm(); };
                Grid.SetColumn(sb, col);
                spoilerSegHost.Children.Add(sb);
            }

            filterSegGrid.Children.Clear();
            var fOpts = new[] { ("reading", "在读"), ("finished", "读过"), ("all", "全部") };
            for (int fi = 0; fi < fOpts.Length; fi++)
            {
                var (fk, fl) = fOpts[fi];
                bool on = bookFilter == fk;
                var fb = new Button
                {
                    Content = fl,
                    Height = 28,
                    MinHeight = 28,
                    Padding = new Thickness(10, 0, 10, 0),
                    FontSize = 11.5,
                    FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                    Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(7),
                    UseSystemFocusVisuals = false,
                };
                string key = fk;
                fb.Click += (_, _) => { bookFilter = key; RebuildForm(); };
                Grid.SetColumn(fb, fi);
                filterSegGrid.Children.Add(fb);
            }

            if (chosenKind == StudioKind.VocabRadio)
            {
                var words = _library.GetWords().Where(w => !w.Known).ToArray();
                var vStack = new StackPanel { Spacing = 8, Padding = new Thickness(4, 6, 4, 6) };
                vStack.Children.Add(new TextBlock
                {
                    Text = words.Length > 0 ? $"将从生词本挑选近期 {Math.Min(12, words.Length)} 个「在学」词汇自然融入对话：" : "生词本目前还没有「在学」生词，将为你生成一段英文阅读热身短播客。",
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T2),
                    TextWrapping = TextWrapping.Wrap,
                });
                if (words.Length > 0)
                {
                    string previewWords = string.Join("  ·  ", words.Take(12).Select(w => w.Word));
                    vStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(10),
                        Background = ThemeHelper.Brush(p.Inset),
                        Padding = new Thickness(12, 10, 12, 10),
                        Child = new TextBlock { Text = previewWords, FontFamily = new FontFamily("Georgia"), FontSize = 13, Foreground = ThemeHelper.Brush(tint.Accent), TextWrapping = TextWrapping.Wrap, LineHeight = 21 },
                    });
                }
                booksBodyHost.Content = vStack;
            }
            else
            {
                string q = bookSearch.Trim();
                var filtered = !string.IsNullOrEmpty(q)
                    ? allBooks.Where(b => b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || b.Authors.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase))).ToArray()
                    : bookFilter switch
                    {
                        "reading" => allBooks.Where(b => b.Status == ReadingStatus.Reading).ToArray(),
                        "finished" => allBooks.Where(b => b.Status == ReadingStatus.Finished).ToArray(),
                        _ => allBooks,
                    };

                var bGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
                bGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                bGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int shownCount = filtered.Length;
                int bRows = (shownCount + 1) / 2;
                for (int r = 0; r < bRows; r++) bGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (int i = 0; i < shownCount; i++)
                {
                    var bk = filtered[i];
                    bool checkedOn = selectedBooks.Contains(bk.Id);
                    var cell = new Grid { ColumnSpacing = 10 };
                    cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                    cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    // Mini cover thumbnail inside each book card (1:1 shelf-show-make/1.png)
                    UIElement miniCover;
                    if (!string.IsNullOrEmpty(bk.CoverPath) && File.Exists(bk.CoverPath))
                    {
                        miniCover = new Border
                        {
                            Width = 28,
                            Height = 38,
                            CornerRadius = new CornerRadius(3),
                            BorderBrush = ThemeHelper.Brush(p.Line),
                            BorderThickness = new Thickness(0.5),
                            Child = new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(bk.CoverPath)), Stretch = Stretch.UniformToFill },
                        };
                    }
                    else
                    {
                        miniCover = new Border
                        {
                            Width = 28,
                            Height = 38,
                            CornerRadius = new CornerRadius(3),
                            Background = ThemeHelper.Brush(Color.FromArgb(255, 234, 230, 222)),
                            BorderBrush = ThemeHelper.Brush(p.Line),
                            BorderThickness = new Thickness(0.5),
                            Child = new TextBlock { Text = bk.Title.Length > 0 ? bk.Title[..1] : "书", FontFamily = serif, FontSize = 9, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                        };
                    }
                    cell.Children.Add(miniCover);

                    var bMeta = new StackPanel { Spacing = 1.5, VerticalAlignment = VerticalAlignment.Center };
                    bMeta.Children.Add(new TextBlock { Text = bk.Title, FontSize = 12.5, FontWeight = checkedOn ? FontWeights.SemiBold : FontWeights.Medium, Foreground = ThemeHelper.Brush(checkedOn ? tint.Accent : p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    string stName = bk.Status switch { ReadingStatus.Reading => $"在读 {(int)Math.Round(bk.Progress * 100)}%", ReadingStatus.Finished => "读过", _ => "想读" };
                    string authorPrefix = bk.Authors.FirstOrDefault() is { Length: > 0 } au ? $"{au} · {stName}" : stName;
                    bMeta.Children.Add(new TextBlock { Text = authorPrefix, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    Grid.SetColumn(bMeta, 1);
                    cell.Children.Add(bMeta);

                    if (checkedOn)
                    {
                        var chk = YYIcons.Make("checkmark", 11.5, ThemeHelper.Brush(tint.Accent));
                        Grid.SetColumn(chk, 2);
                        cell.Children.Add(chk);
                    }

                    var bBtn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(10, 8, 10, 8),
                        CornerRadius = new CornerRadius(10),
                        Background = ThemeHelper.Brush(checkedOn ? tint.Soft : leafSurface),
                        BorderBrush = ThemeHelper.Brush(checkedOn ? tint.Edge : p.Line),
                        BorderThickness = new Thickness(0.75),
                        UseSystemFocusVisuals = false,
                        Content = cell,
                    };
                    string bid = bk.Id;
                    bBtn.Click += (_, _) =>
                    {
                        if (chosenKind == StudioKind.BookGuide)
                        {
                            selectedBooks.Clear();
                            selectedBooks.Add(bid);
                        }
                        else if (!selectedBooks.Remove(bid))
                        {
                            selectedBooks.Add(bid);
                        }
                        RebuildForm();
                    };
                    Grid.SetRow(bBtn, i / 2);
                    Grid.SetColumn(bBtn, i % 2);
                    bGrid.Children.Add(bBtn);
                }

                booksBodyHost.Content = new ScrollViewer { MaxHeight = 148, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = bGrid };
            }

            RebuildTickLabels();
        }

        durSlider.ValueChanged += (_, args) =>
        {
            int ni = Math.Clamp((int)Math.Round(args.NewValue), 0, durTicks.Length - 1);
            durationMin = durTicks[ni];
            durBadgeText.Text = $"{durationMin} 分钟";
            RebuildTickLabels();
        };

        searchBox.TextChanged += (_, _) => { bookSearch = searchBox.Text; RebuildForm(); };
        RebuildForm();

        // Top Header
        var topHead = new Grid { Padding = new Thickness(24, 18, 22, 12) };
        var topLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        topLeft.Children.Add(new TextBlock { Text = L10n.T("酿一期书房电台"), FontFamily = serif, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        topLeft.Children.Add(headerSubtitle);
        topHead.Children.Add(topLeft);
        var closeBtn = YYChrome.MakeSheetCloseButton(() => dlgRef?.Hide(), dark);
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
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = leftGrid,
        };

        // Right column
        var refHead = new Grid();
        var refHeadLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        refHeadLeft.Children.Add(new TextBlock { Text = L10n.T("参考书目"), FontFamily = serif, FontSize = 14.5, FontWeight = FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
        refHeadLeft.Children.Add(refScopeSub);
        refHead.Children.Add(refHeadLeft);
        refHead.Children.Add(refCountText);

        var topFilterLine = new Grid { ColumnSpacing = 8 };
        topFilterLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topFilterLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topFilterLine.Children.Add(searchPill);
        Grid.SetColumn(filterSegWrap, 1);
        topFilterLine.Children.Add(filterSegWrap);

        var booksCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(12, 10, 12, 10),
            Child = new StackPanel { Spacing = 10, Children = { topFilterLine, booksBodyHost } },
        };

        // Topic + Duration card
        var topicRow = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 5, 12, 5), MinHeight = 40 };
        topicRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        topicRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topicRow.Children.Add(new TextBlock { Text = L10n.T("选题"), FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(topicBox, 1);
        topicRow.Children.Add(topicBox);

        var durGrid = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 8, 14, 10) };
        durGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        durGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        durGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        durGrid.Children.Add(new TextBlock { Text = L10n.T("时长"), FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        var sliderCol = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { durSlider, tickLabelsGrid } };
        Grid.SetColumn(sliderCol, 1);
        durGrid.Children.Add(sliderCol);
        var durBadge = new Border
        {
            Height = 32,
            MinWidth = 68,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(leafSurface),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = durBadgeText,
        };
        Grid.SetColumn(durBadge, 2);
        durGrid.Children.Add(durBadge);

        var metaCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = new StackPanel
            {
                Spacing = 0,
                Children =
                {
                    topicRow,
                    new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) },
                    durGrid,
                },
            },
        };

        var rightHost = new StackPanel { Spacing = 10, Children = { refHead, booksCard, metaCard } };

        var columnsGrid = new Grid { ColumnSpacing = 16, Padding = new Thickness(24, 0, 24, 14) };
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(184) });
        columnsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columnsGrid.Children.Add(leftCard);
        Grid.SetColumn(rightHost, 1);
        columnsGrid.Children.Add(rightHost);

        // Bottom bar: host voice dropdown pill + AI status + Cancel + dark 开始生成
        var voiceMenu = new MenuFlyout();
        foreach (var vo in voiceOptions)
        {
            string vid = vo.Id;
            string vShort = vo.ShortLabel;
            var mi = new MenuFlyoutItem { Text = vo.FullLabel };
            mi.Click += (_, _) => { voiceStyle = vid; hostVoiceLabel.Text = vShort; };
            voiceMenu.Items.Add(mi);
        }
        var hostSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        hostSp.Children.Add(YYIcons.Make("wave", 13, ThemeHelper.Brush(tint.Accent)));
        hostSp.Children.Add(hostVoiceLabel);
        hostSp.Children.Add(YYIcons.Make("chevron-down", 9.5, ThemeHelper.Brush(p.T3)));
        var hostVoiceBtn = new Button
        {
            Height = 36,
            MinHeight = 36,
            Padding = new Thickness(14, 0, 12, 0),
            CornerRadius = new CornerRadius(18),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            UseSystemFocusVisuals = false,
            Content = hostSp,
            Flyout = voiceMenu,
        };

        var bottomBar = new Grid { Padding = new Thickness(24, 4, 24, 18), ColumnSpacing = 12 };
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottomBar.Children.Add(hostVoiceBtn);

        var aiNote = new TextBlock
        {
            Text = _library.Companion.IsConfigured ? L10n.T("已连接书伴模型 · 生成双人对谈文稿") : L10n.T("未接书伴模型 · 将用本机离线摘要成稿"),
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(aiNote, 1);
        bottomBar.Children.Add(aiNote);

        var rightActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        rightActs.Children.Add(YYChrome.MakeSheetPillButton("取消", () => dlgRef?.Hide(), dark, _library.Prefs.Tint, "secondary", height: 36, horizontalPadding: 18));
        rightActs.Children.Add(YYChrome.MakeSheetPillButton("开始生成", () => { confirmedGenerate = true; dlgRef?.Hide(); }, dark, _library.Prefs.Tint, "dark", height: 36, horizontalPadding: 20));
        Grid.SetColumn(rightActs, 2);
        bottomBar.Children.Add(rightActs);

        var sheetStack = new StackPanel { Spacing = 0, Children = { topHead, columnsGrid, bottomBar } };
        dlgRef = YYChrome.CreateMacSheet(_shelf.WindowXamlRoot, dark, 720, sheetStack, maxHeight: 780);
        await dlgRef.ShowAsync();

        if (confirmedGenerate)
        {
            if (selectedBooks.Count == 0 && chosenKind != StudioKind.VocabRadio)
            {
                _shelf.ShowToast("请先点选至少一本参考书", canUndo: false);
                return;
            }
            _library.Prefs.PodcastStudioVoiceStyle = voiceStyle;
            _library.SavePrefs();

            _shelf.ShowToast("正在为你酿制书房电台文稿…", canUndo: false);
            try
            {
                var issue = await _library.Podcasts.GenerateStudioIssueAsync(
                    chosenKind,
                    selectedBooks.ToArray(),
                    durationMin,
                    string.IsNullOrWhiteSpace(topicBox.Text) ? null : topicBox.Text.Trim(),
                    noSpoiler: noSpoiler,
                    voiceStyle: voiceStyle);
                OpenIssueId = issue.Id;
                _shelf.RefreshBooks();
            }
            catch (Exception ex)
            {
                _shelf.ShowToast($"生成失败：{ex.Message}", canUndo: false);
            }
        }
    }

    // ---------- 5. PodcastAddSheet（搜目录 + 贴网页/RSS + OPML + 本地音频 1:1） ----------

    public async Task ShowAddPodcastSheetAsync(string initialQuery = "")
    {
        bool dark = ThemeHelper.IsDark(_shelf.WindowXamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var subscribedFeeds = _library.Podcasts.GetChannels().Select(c => c.FeedUrl).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ContentDialog? dlgRef = null;

        var root = new StackPanel { Width = 480, Spacing = 12 };
        var queryRow = new Grid { ColumnSpacing = 8 };
        queryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        queryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var queryBox = new TextBox
        {
            Text = initialQuery,
            PlaceholderText = "搜节目名，或贴 RSS / 小宇宙单集 / 音频链接…",
            FontSize = 12.5,
        };
        // Auto-prefill clipboard URL when opened with empty query (1:1 PodcastAddSheet.swift)
        if (string.IsNullOrWhiteSpace(initialQuery))
        {
            try
            {
                var dpv = Clipboard.GetContent();
                if (dpv.Contains(StandardDataFormats.Text))
                {
                    string clip = (await dpv.GetTextAsync()).Trim();
                    if ((clip.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        && !clip.Contains('\n') && clip.Length < 400)
                    {
                        queryBox.Text = clip;
                        queryBox.SelectAll();
                    }
                }
            }
            catch { }
        }
        var goBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
            Content = "搜索 / 订阅",
            Height = 32,
            FontSize = 12,
        };
        Grid.SetColumn(goBtn, 1);
        queryRow.Children.Add(queryBox);
        queryRow.Children.Add(goBtn);
        root.Children.Add(queryRow);

        var statusTb = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(tint.Accent), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        root.Children.Add(statusTb);

        var resultsStack = new StackPanel { Spacing = 0 };
        void RenderHitRows(IReadOnlyList<PodcastSearchHitDto> hits)
        {
            for (int i = 0; i < hits.Count; i++)
            {
                if (i > 0) resultsStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
                var hit = hits[i];
                bool added = subscribedFeeds.Contains(hit.FeedUrl);

                var row = new Grid { Padding = new Thickness(12, 9, 12, 9), ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var artBox = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = ThemeHelper.Brush(p.Inset) };
                if (!string.IsNullOrEmpty(hit.ArtworkUrl) && Uri.TryCreate(hit.ArtworkUrl, UriKind.Absolute, out var artUri))
                    artBox.Child = new Image { Source = new BitmapImage(artUri), Stretch = Stretch.UniformToFill };
                else
                    artBox.Child = YYIcons.Make("radio", 14, ThemeHelper.Brush(p.T2));
                row.Children.Add(artBox);

                var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                meta.Children.Add(new TextBlock { Text = hit.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                string sub = string.Join(" · ", new[] { hit.Author, hit.Genre }.Where(x => !string.IsNullOrWhiteSpace(x)));
                meta.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(meta, 1);
                row.Children.Add(meta);

                var subBtn = MakePaperPill(added ? "已订阅" : "订阅", accent: !added, p, tint);
                subBtn.Height = 25;
                subBtn.IsEnabled = !added;
                string feedUrl = hit.FeedUrl;
                string showTitle = hit.Title;
                subBtn.Click += async (_, _) =>
                {
                    subBtn.IsEnabled = false;
                    subBtn.Content = "订阅中…";
                    try
                    {
                        await _library.Podcasts.AddUrlOrFeedAsync(feedUrl, putLatestOnDesk: true);
                        subscribedFeeds.Add(feedUrl);
                        subBtn.Content = "已订阅";
                        statusTb.Text = $"已订阅《{showTitle}》，最新一期已放到收听台";
                        statusTb.Foreground = ThemeHelper.Brush(tint.Accent);
                        statusTb.Visibility = Visibility.Visible;
                        _shelf.RefreshBooks();
                    }
                    catch (Exception ex)
                    {
                        subBtn.IsEnabled = true;
                        subBtn.Content = "订阅";
                        statusTb.Text = $"订阅失败：{ex.Message}";
                        statusTb.Foreground = ThemeHelper.Brush(p.Danger);
                        statusTb.Visibility = Visibility.Visible;
                    }
                };
                Grid.SetColumn(subBtn, 2);
                row.Children.Add(subBtn);
                resultsStack.Children.Add(row);
            }
        }

        void RenderEmptyGuide()
        {
            resultsStack.Children.Clear();
            var guide = new StackPanel { Spacing = 8, Padding = new Thickness(14, 12, 14, 8) };
            guide.Children.Add(new TextBlock { Text = "热门播客榜单 · 一键订阅", FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            guide.Children.Add(new TextBlock { Text = "也可直接粘贴 RSS、小宇宙单集网页、Apple Podcasts 或 MP3 链接回车导入。", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
            resultsStack.Children.Add(guide);
            resultsStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });

            var loadingTb = new TextBlock { Text = "正在加载公开播客精选榜…", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(14, 10, 14, 10) };
            resultsStack.Children.Add(loadingTb);
            _ = Task.Run(async () =>
            {
                var charts = await _library.Podcasts.FetchItunesTopChartsAsync();
                _shelf.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!string.IsNullOrWhiteSpace(queryBox.Text) && queryBox.Text != initialQuery) return;
                    resultsStack.Children.Remove(loadingTb);
                    if (resultsStack.Children.Count <= 2) RenderHitRows(charts);
                });
            });
        }

        async Task SubmitQueryAsync()
        {
            string q = queryBox.Text.Trim();
            if (string.IsNullOrEmpty(q)) { RenderEmptyGuide(); return; }

            bool isUrl = q.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || q.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || q.Contains(".xml", StringComparison.OrdinalIgnoreCase)
                || q.Contains("/feed", StringComparison.OrdinalIgnoreCase);

            if (isUrl)
            {
                statusTb.Text = "正在解析链接并获取节目…";
                statusTb.Foreground = ThemeHelper.Brush(tint.Accent);
                statusTb.Visibility = Visibility.Visible;
                goBtn.IsEnabled = false;
                try
                {
                    var (_, single) = await _library.Podcasts.AddUrlOrFeedAsync(q, putLatestOnDesk: true);
                    if (single is not null) OpenIssueId = single.Id;
                    _shelf.RefreshBooks();
                    _shelf.ShowToast("已放入收听台", canUndo: false);
                    dlgRef?.Hide();
                }
                catch (Exception ex)
                {
                    statusTb.Text = $"解析没成功：{ex.Message}";
                    statusTb.Foreground = ThemeHelper.Brush(p.Danger);
                }
                finally { goBtn.IsEnabled = true; }
                return;
            }

            statusTb.Text = "正在搜索公开播客目录…";
            statusTb.Foreground = ThemeHelper.Brush(p.T3);
            statusTb.Visibility = Visibility.Visible;
            goBtn.IsEnabled = false;
            try
            {
                var hits = await _library.Podcasts.SearchItunesAsync(q);
                resultsStack.Children.Clear();
                if (hits.Count == 0)
                {
                    statusTb.Text = "没搜到匹配的公开播客，也可以直接粘贴节目的 RSS 链接。";
                    RenderEmptyGuide();
                    return;
                }

                statusTb.Text = $"找到 {hits.Count} 档节目";
                RenderHitRows(hits);
            }
            finally
            {
                goBtn.IsEnabled = true;
            }
        }

        goBtn.Click += async (_, _) => await SubmitQueryAsync();
        queryBox.KeyDown += async (_, ke) =>
        {
            if (ke.Key == VirtualKey.Enter) { ke.Handled = true; await SubmitQueryAsync(); }
        };

        RenderEmptyGuide();
        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 270,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Child = resultsStack,
            },
        });

        // Footer pills: 导入 OPML + 本地音频
        var footRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var opmlBtn = MakePaperPill("导入 OPML…", accent: false, p, tint);
        opmlBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".opml");
            picker.FileTypeFilter.Add(".xml");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_shelf));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            statusTb.Text = "正在批量导入 OPML 订阅源…";
            statusTb.Visibility = Visibility.Visible;
            try
            {
                int cnt = await _library.Podcasts.ImportOpmlAsync(f.Path);
                statusTb.Text = $"✓ 已导入 {cnt} 档播客到报刊栏";
                _shelf.RefreshBooks();
            }
            catch (Exception ex)
            {
                statusTb.Text = $"OPML 导入失败：{ex.Message}";
                statusTb.Foreground = ThemeHelper.Brush(p.Danger);
            }
        };

        var localAudioBtn = MakePaperPill("导入本地音频…", accent: false, p, tint);
        localAudioBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".m4a");
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".flac");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_shelf));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            try
            {
                var iss = await _library.Podcasts.ImportLocalAudioAsync(f.Path);
                dlgRef?.Hide();
                OpenIssueId = iss.Id;
                _shelf.RefreshBooks();
            }
            catch (Exception ex) { statusTb.Text = ex.Message; statusTb.Visibility = Visibility.Visible; }
        };

        footRow.Children.Add(opmlBtn);
        footRow.Children.Add(localAudioBtn);
        root.Children.Add(footRow);

        dlgRef = new ContentDialog
        {
            XamlRoot = _shelf.WindowXamlRoot,
            Title = "添加节目",
            Content = root,
            CloseButtonText = "完成",
        };

        if (!string.IsNullOrWhiteSpace(initialQuery))
            _ = SubmitQueryAsync();

        await dlgRef.ShowAsync();
    }

    // ---------- Helpers ----------

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
            CornerRadius = new CornerRadius(16),
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
        Height = 26,
        MinHeight = 26,
        Padding = new Thickness(11, 0, 11, 0),
        CornerRadius = new CornerRadius(13),
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
