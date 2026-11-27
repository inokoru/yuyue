using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;
using Yuyue.Audiobook;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Reader;
using Yuyue.Speech;
using Yuyue.UI.Shell;

namespace Yuyue.UI.Audiobook;

/// <summary>
/// Brand-new native Windows 11 Audiobook tab (<see cref="ITabContent"/> &amp; <see cref="IMiniPlayerSource"/>)
/// with 1-click detach to Always-on-Top Acrylic <see cref="MiniPlayerWindow"/>, chapter scrubber,
/// bookmarks, sleep timer, and paired ebook + alignment check integration.
/// </summary>
public sealed partial class AudiobookTabView : UserControl, ITabContent, IMiniPlayerSource
{
    private MainWindow _ownerWindow;
    private readonly LibraryService _library;
    private readonly string _bookId;
    private readonly BookDetail _detail;
    private AudioRecording? _recording;
    private SkipSettings _skipSettings = new(HeadTail: true, Middle: false, ShortenChapterGaps: true, ClearerVoice: false, ShortenPauses: false, FillUnread: false, WordFollow: false);
    private readonly MediaPlayer _player;
    private readonly DispatcherTimer _posTimer;

    private int _currentFileIdx;
    private int _currentChapterIdx;
    private double _rate;
    private bool _playing;
    private bool _suppressSlider;
    private bool _isScrubbingSlider;
    private bool _scrubDirty;
    private double _scrubPendingValue;
    private double? _pendingSeekInFile;
    private bool _pendingAutoPlay;
    private bool _mediaReady;
    private double _lastKnownInFileSec;
    private DateTime? _lastListenTickUtc;
    private double _accumulatedListenSec;
    private uint _accumulatedListenChapter;
    private DateTime? _sleepDeadlineUtc;
    private (uint ChapterIdx, uint RelativeMs)? _sleepArmedPos;
    private double _loudnessGain = 1.0;
    private bool _suppressGainSlider;
    private string _rightTab = "chapters"; // chapters | bookmarks | pair
    private bool _disposed;

    // ITabContent
    public string TabId { get; }
    public string TabTitle => _detail.Summary.Title;
    public string? BookId => _bookId;
    public string TabKind => "audiobook";
    public string TabIconName => "headphones";
    public bool IsPinned => false;
    public bool IsAudioPlaying => !_disposed && _playing;
    public PaperChromeTheme? ActivePaperChrome => null;
    public UIElement ViewElement => this;
    public event Action? TabMetaChanged;

    // IMiniPlayerSource
    string IMiniPlayerSource.SourceId => $"audio:{_bookId}";
    string IMiniPlayerSource.Title => _detail.Summary.Title;
    string IMiniPlayerSource.Subtitle => ChapterTitleText.Text;
    string? IMiniPlayerSource.CoverPath => _detail.Summary.CoverPath;
    bool IMiniPlayerSource.IsPlaying => !_disposed && _playing;
    double IMiniPlayerSource.ProgressRatio => !_disposed && _recording is { Seconds: > 0 }
        ? Math.Clamp(GetGlobalPositionSeconds() / _recording.Seconds, 0, 1)
        : 0;
    double IMiniPlayerSource.PlaybackRate => _rate;
    public event Action? StateChanged;

    public AudiobookTabView(MainWindow ownerWindow, LibraryService library, string bookId, bool autoPlay = false, bool startFromBeginning = false)
    {
        _ownerWindow = ownerWindow;
        _library = library;
        _bookId = bookId;
        TabId = $"audio:{bookId}:{Guid.NewGuid():N}";
        _detail = _library.GetBookDetail(bookId);
        try { _recording = _library.Core.AudioRecording(bookId); } catch { }
        try { _skipSettings = _library.Core.AudiobookSkipSettings(bookId); } catch { }
        _rate = Math.Clamp(_library.Prefs.AudiobookRate, 0.5, 2.5);

        InitializeComponent();

        L10n.Skip(ChapterTitleText);

        ChapterSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(ChapterSlider_PointerPressed), true);
        ChapterSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ChapterSlider_PointerReleasedOrLost), true);
        ChapterSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ChapterSlider_PointerReleasedOrLost), true);
        ChapterSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(ChapterSlider_PointerReleasedOrLost), true);

        _player = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech, AutoPlay = false };
        _player.MediaOpened += OnMediaOpened;
        _player.MediaFailed += OnMediaFailed;
        _player.MediaEnded += OnMediaEnded;
        _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;

        _posTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(500) };
        _posTimer.Tick += (_, _) => OnPositionTick();
        _posTimer.Start();

        BuildSleepMenu();
        LoadBookMetadata();
        InitLoudnessGain();

        double startSec = 0;
        if (!startFromBeginning && _library.GetProgress(bookId) is { } prog)
            startSec = FromChapterPosition(prog.Position.Section, prog.Position.Sentence);

        ApplyAppearance();
        CheckSleepReturnBanner();
        if (_recording is null || _recording.Files.Length == 0)
            StatusLeftText.Text = L10n.T("未找到该有声书音频文件，请重新导入。");
        else
            SeekGlobalSeconds(startSec, autoPlay);
    }

    private void InitLoudnessGain()
    {
        if (_library.Prefs.AudiobookLoudnessGain.TryGetValue(_bookId, out float savedGain))
        {
            ApplyLoudnessGain(Math.Clamp(savedGain, 0.35, 1.0), persist: false);
        }
        else
        {
            ApplyLoudnessGain(1.0, persist: false);
            _ = MeasureBookLoudnessAsync();
        }
    }

    private void ApplyLoudnessGain(double gain, bool persist)
    {
        _loudnessGain = Math.Clamp(gain, 0.35, 1.0);
        try { _player.Volume = _loudnessGain; } catch { }
        _suppressGainSlider = true;
        GainSlider.Value = Math.Round(_loudnessGain * 100.0);
        GainValueText.Text = $"{Math.Round(_loudnessGain * 100.0):0}%";
        _suppressGainSlider = false;
        if (persist)
        {
            _library.Prefs.AudiobookLoudnessGain[_bookId] = (float)_loudnessGain;
            _library.SavePrefs();
        }
    }

    private async Task MeasureBookLoudnessAsync()
    {
        if (_disposed || _recording is null || _recording.Files.Length == 0) return;
        string raw = _recording.Files[0].Path;
        string path = Path.IsPathRooted(raw) ? raw : Path.Combine(_library.DataDir, raw);
        if (!File.Exists(path)) return;
        try
        {
            string scratch = Path.Combine(_library.DataDir, "audiobooks", _bookId, "pcm-cache");
            string pcm = await Aligner.EnsurePcm16kWavAsync(path, scratch, 0, CancellationToken.None);
            if (_disposed) return;
            float measured = VoiceTapDsp.MeasureLoudnessFromWav(pcm);
            _library.Prefs.AudiobookLoudnessGain[_bookId] = measured;
            _library.SavePrefs();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed) ApplyLoudnessGain(measured, persist: false);
            });
        }
        catch { }
    }

    private void CheckSleepReturnBanner()
    {
        if (_disposed || _recording is null) return;
        var info = ReaderRecapAndSleepHelper.TryGetSleepReturn(_library, _bookId);
        if (info is null) return;
        int chIdx = _recording.Chapters.Length > 0 ? Math.Clamp((int)info.Section, 0, _recording.Chapters.Length - 1) : 0;
        double inChSec = info.Sentence / 1000.0;
        double targetSec = _recording.Chapters.Length > 0 ? _recording.Chapters[chIdx].Start + inChSec : inChSec;
        string chName = _recording.Chapters.Length > 0 && !string.IsNullOrWhiteSpace(_recording.Chapters[chIdx].Title)
            ? _recording.Chapters[chIdx].Title!
            : L10n.T($"第 {chIdx + 1} 集");
        var enriched = info with { Place = $"{chName} {FormatClock(inChSec)}" };

        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        SleepReturnHost.Content = ReaderRecapAndSleepHelper.BuildSleepReturnCard(
            enriched,
            ThemeHelper.Palette(dark),
            ThemeHelper.TintColors(_library.Prefs.Tint, dark),
            go =>
            {
                SleepReturnHost.Visibility = Visibility.Collapsed;
                ReaderRecapAndSleepHelper.ClearSleepReturn(_library, _bookId);
                if (go) SeekGlobalSeconds(targetSec, autoPlay: true);
            });
        SleepReturnHost.Visibility = Visibility.Visible;
    }

    private void GainSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressGainSlider || _disposed) return;
        ApplyLoudnessGain(e.NewValue / 100.0, persist: true);
    }

    private double GetChapterStartSeconds(int chapterIdx)
    {
        if (_recording is null || _recording.Chapters.Length == 0) return 0;
        if (chapterIdx < 0 || chapterIdx >= _recording.Chapters.Length) return 0;
        return _recording.Chapters[chapterIdx].Start;
    }

    private int FindChapterIndex(double globalSec)
    {
        if (_recording is null || _recording.Chapters.Length == 0) return 0;
        int chapIdx = 0;
        for (int i = 0; i < _recording.Chapters.Length; i++)
        {
            if (globalSec >= _recording.Chapters[i].Start - 0.25) chapIdx = i;
        }
        return chapIdx;
    }

    private (uint ChapterIdx, uint RelativeMs) ToChapterPosition(double globalSec)
    {
        int chIdx = FindChapterIndex(globalSec);
        double chStart = GetChapterStartSeconds(chIdx);
        uint relMs = (uint)Math.Max(0, Math.Round((globalSec - chStart) * 1000.0));
        return ((uint)Math.Max(0, chIdx), relMs);
    }

    private double FromChapterPosition(uint section, uint sentenceMs)
    {
        if (_recording is null || _recording.Chapters.Length == 0)
            return sentenceMs / 1000.0;
        int chIdx = Math.Clamp((int)section, 0, _recording.Chapters.Length - 1);
        double chStart = _recording.Chapters[chIdx].Start;
        return chStart + sentenceMs / 1000.0;
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            _mediaReady = true;
            try
            {
                if (_pendingSeekInFile is double seekSec)
                {
                    _pendingSeekInFile = null;
                    double clampedInFile = seekSec;
                    var natDur = _player.PlaybackSession.NaturalDuration;
                    if (natDur > System.TimeSpan.Zero)
                        clampedInFile = Math.Clamp(seekSec, 0, Math.Max(0, natDur.TotalSeconds - 0.1));
                    _player.PlaybackSession.Position = System.TimeSpan.FromSeconds(clampedInFile);
                    _lastKnownInFileSec = clampedInFile;
                }
                _player.PlaybackSession.PlaybackRate = _rate;
                if (_pendingAutoPlay)
                {
                    _pendingAutoPlay = false;
                    _player.Play();
                }
            }
            catch { }
            OnPositionTick();
        });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        if (_disposed) return;
        string errMsg = args.ErrorMessage;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            _mediaReady = false;
            _pendingSeekInFile = null;
            _pendingAutoPlay = false;
            _playing = false;
            FlushListeningTime();
            UpdatePlayVisuals();
            string msg = string.IsNullOrWhiteSpace(errMsg)
                ? L10n.T("音频文件播放失败，请检查文件是否完整。")
                : $"{L10n.T("音频播放失败：")}{errMsg}";
            StatusLeftText.Text = msg;
            _ownerWindow.ShowToast(msg);
            TabMetaChanged?.Invoke();
            StateChanged?.Invoke();
        });
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        if (_disposed) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed) OnTrackEnded();
        });
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        if (_disposed) return;
        bool nowPlaying = sender.PlaybackState == MediaPlaybackState.Playing;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            bool wasPlaying = _playing;
            _playing = nowPlaying;
            if (_playing)
            {
                if (!wasPlaying)
                {
                    _lastListenTickUtc = DateTime.UtcNow;
                    _accumulatedListenChapter = (uint)Math.Max(0, _currentChapterIdx);
                }
                App.CurrentApp.ClaimAudioPlayback(this, Pause);
                App.CurrentApp.SetActiveMiniPlayerSource(this);
            }
            else if (wasPlaying)
            {
                FlushListeningTime();
            }
            _library.SystemIntegration.SetAudioPlaying(this, _playing, _detail.Summary.Title, TogglePlayPause);
            UpdatePlayVisuals();
            TabMetaChanged?.Invoke();
            StateChanged?.Invoke();
        });
    }

    private void LoadBookMetadata()
    {
        TopTitleText.Text = _detail.Summary.Title;
        L10n.Skip(TopTitleText);
        BookBylineText.Text = _detail.Summary.Authors.Length > 0
            ? $"{_detail.Summary.Title} · {string.Join("、", _detail.Summary.Authors)}"
            : _detail.Summary.Title;
        L10n.Skip(BookBylineText);

        if (!string.IsNullOrEmpty(_detail.Summary.CoverPath) && File.Exists(_detail.Summary.CoverPath))
        {
            try
            {
                var bmp = new BitmapImage { DecodePixelWidth = 480 };
                bmp.UriSource = new Uri(_detail.Summary.CoverPath);
                CoverImage.Source = bmp;
                CoverImage.Visibility = Visibility.Visible;
                CoverFallbackIcon.Visibility = Visibility.Collapsed;
            }
            catch { }
        }
    }

    public void EnsurePlaying()
    {
        if (_disposed) return;
        if (!_playing) Play();
    }

    public void JumpToStartAndPlay()
    {
        if (_disposed) return;
        SeekGlobalSeconds(0, autoPlay: true);
    }

    private void Play()
    {
        if (_disposed) return;
        SleepReturnHost.Visibility = Visibility.Collapsed;
        ReaderRecapAndSleepHelper.ClearSleepReturn(_library, _bookId);
        if (_player.Source is null)
        {
            SeekGlobalSeconds(GetGlobalPositionSeconds(), autoPlay: true);
            return;
        }
        if (!_mediaReady)
        {
            _pendingAutoPlay = true;
            return;
        }
        try
        {
            _player.PlaybackSession.PlaybackRate = _rate;
            _player.Play();
        }
        catch { }
    }

    private void Pause()
    {
        if (_disposed) return;
        _pendingAutoPlay = false;
        try { _player.Pause(); } catch { }
        FlushListeningTime();
        SaveProgress();
    }

    private void TogglePlayPause()
    {
        if (_disposed) return;
        if (_playing) Pause();
        else Play();
    }

    private double GetGlobalPositionSeconds()
    {
        if (_recording is null || _recording.Files.Length == 0) return 0;
        double offset = 0;
        for (int i = 0; i < _currentFileIdx && i < _recording.Files.Length; i++)
            offset += _recording.Files[i].Seconds;
        double inFile;
        if (_pendingSeekInFile is double pending)
        {
            inFile = pending;
        }
        else if (_disposed || !_mediaReady)
        {
            inFile = _lastKnownInFileSec;
        }
        else
        {
            try { inFile = _player.PlaybackSession.Position.TotalSeconds; }
            catch { inFile = _lastKnownInFileSec; }
        }
        return offset + inFile;
    }

    private void SeekGlobalSeconds(double globalSec, bool autoPlay)
    {
        if (_disposed || _recording is null || _recording.Files.Length == 0) return;
        double clamped = Math.Clamp(globalSec, 0, Math.Max(0, _recording.Seconds - 0.5));
        double acc = 0;
        int targetFile = 0;
        double inFile = 0;
        for (int i = 0; i < _recording.Files.Length; i++)
        {
            double len = Math.Max(0.1, _recording.Files[i].Seconds);
            if (clamped < acc + len || i == _recording.Files.Length - 1)
            {
                targetFile = i;
                inFile = Math.Max(0, clamped - acc);
                break;
            }
            acc += len;
        }

        if (_player.Source is null || targetFile != _currentFileIdx)
        {
            string rawPath = _recording.Files[targetFile].Path;
            string path = Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(_library.DataDir, rawPath);
            if (!File.Exists(path))
            {
                string missingMsg = L10n.T("未找到该有声书音频文件，请重新导入。");
                StatusLeftText.Text = missingMsg;
                _ownerWindow.ShowToast(missingMsg);
                return;
            }

            _currentFileIdx = targetFile;
            _mediaReady = false;
            _pendingSeekInFile = inFile;
            _pendingAutoPlay = autoPlay;
            _lastKnownInFileSec = inFile;
            _player.Source = MediaSource.CreateFromUri(new Uri(path));
            OnPositionTick();
            return;
        }

        if (!_mediaReady)
        {
            _pendingSeekInFile = inFile;
            _pendingAutoPlay = autoPlay;
            _lastKnownInFileSec = inFile;
            OnPositionTick();
            return;
        }

        _pendingSeekInFile = null;
        _pendingAutoPlay = false;
        _lastKnownInFileSec = inFile;
        try
        {
            _player.PlaybackSession.Position = System.TimeSpan.FromSeconds(inFile);
            _player.PlaybackSession.PlaybackRate = _rate;
        }
        catch { }
        if (autoPlay) Play();
        OnPositionTick();
    }

    private void OnTrackEnded()
    {
        if (_disposed || _recording is null) return;
        if (_currentFileIdx + 1 < _recording.Files.Length)
        {
            double nextStart = 0;
            for (int i = 0; i <= _currentFileIdx; i++) nextStart += _recording.Files[i].Seconds;
            SeekGlobalSeconds(nextStart, autoPlay: true);
        }
        else
        {
            FlushListeningTime();
            SaveProgress();
            _playing = false;
            UpdatePlayVisuals();
        }
    }

    private void FlushListeningTime()
    {
        if (_lastListenTickUtc is DateTime prev && _playing)
        {
            var now = DateTime.UtcNow;
            _accumulatedListenSec += Math.Clamp((now - prev).TotalSeconds, 0, 5.0);
            _lastListenTickUtc = now;
        }
        else
        {
            _lastListenTickUtc = null;
        }

        uint wholeSec = (uint)Math.Floor(_accumulatedListenSec);
        if (wholeSec == 0) return;
        _accumulatedListenSec -= wholeSec;
        try { _library.AddTime(_bookId, _accumulatedListenChapter, 0, wholeSec); } catch { }
    }

    private void OnPositionTick()
    {
        if (_disposed || _recording is null) return;

        if (_mediaReady && _pendingSeekInFile is null)
        {
            try { _lastKnownInFileSec = _player.PlaybackSession.Position.TotalSeconds; } catch { }
        }

        if (_sleepDeadlineUtc is DateTime dl)
        {
            var left = dl - DateTime.UtcNow;
            if (left <= System.TimeSpan.Zero)
            {
                var armed = _sleepArmedPos ?? ToChapterPosition(GetGlobalPositionSeconds());
                _sleepDeadlineUtc = null;
                _sleepArmedPos = null;
                SleepStatusText.Text = "";
                Pause();
                ReaderRecapAndSleepHelper.RememberSleepStop(_library, _bookId, armed.ChapterIdx, (int)armed.RelativeMs);
                try
                {
                    var curPos = ToChapterPosition(GetGlobalPositionSeconds());
                    _library.AddBookmark(_bookId, curPos.ChapterIdx, curPos.RelativeMs);
                }
                catch { }
                bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
                SleepBtn.Content = YYIcons.Make("moon", 16, ThemeHelper.Brush(ThemeHelper.Palette(dark).T2));
                _ownerWindow.ShowToast("定时到了 · 已暂停并加书签");
            }
            else
            {
                SleepStatusText.Text = L10n.T($"定时 · {(int)Math.Ceiling(left.TotalMinutes)} 分钟");
            }
        }

        double globalSec = GetGlobalPositionSeconds();
        int chapIdx = FindChapterIndex(globalSec);
        bool chapChanged = chapIdx != _currentChapterIdx;
        _currentChapterIdx = chapIdx;

        if (_playing)
        {
            var now = DateTime.UtcNow;
            if (_lastListenTickUtc is DateTime prev)
            {
                double dt = Math.Clamp((now - prev).TotalSeconds, 0, 5.0);
                uint curCh = (uint)Math.Max(0, _currentChapterIdx);
                if (curCh != _accumulatedListenChapter && _accumulatedListenSec >= 1.0)
                    FlushListeningTime();
                _accumulatedListenChapter = curCh;
                _accumulatedListenSec += dt;
                if (_accumulatedListenSec >= 15.0)
                {
                    FlushListeningTime();
                    SaveProgress();
                }
            }
            _lastListenTickUtc = now;
        }

        var chap = _recording.Chapters.Length > 0 ? _recording.Chapters[chapIdx] : null;
        double chapStart = chap?.Start ?? 0;
        double chapDur = Math.Max(1, chap?.Seconds ?? _recording.Seconds);
        double chapElapsed = Math.Clamp(globalSec - chapStart, 0, chapDur);

        ChapterTitleText.Text = !string.IsNullOrWhiteSpace(chap?.Title) ? chap!.Title! : L10n.T($"第 {chapIdx + 1} 集");
        DurationText.Text = FormatClock(chapDur);

        double totalRem = Math.Max(0, _recording.Seconds - globalSec);
        if (_skipSettings.ShortenPauses)
        {
            try { totalRem = Math.Max(0, totalRem - _library.Core.AudiobookPauseSavings(_bookId, globalSec)); }
            catch { }
        }
        int remH = (int)(totalRem / 3600);
        int remM = (int)((totalRem % 3600) / 60);
        RemainingText.Text = remH > 0
            ? (remM > 0 ? L10n.T($"全书还剩 {remH} 小时 {remM} 分") : L10n.T($"全书还剩 {remH} 小时"))
            : L10n.T($"全书还剩 {Math.Max(1, (int)(totalRem / 60))} 分钟");

        if (!_isScrubbingSlider)
        {
            ElapsedText.Text = FormatClock(chapElapsed);
            _suppressSlider = true;
            ChapterSlider.Maximum = chapDur;
            ChapterSlider.Value = chapElapsed;
            _suppressSlider = false;
        }

        StatusLeftText.Text = L10n.T($"第 {chapIdx + 1} / {Math.Max(1, _recording.Chapters.Length)} 集 · {_rate:0.##}×");

        if (chapChanged && _rightTab == "chapters") RenderRightPane();
        StateChanged?.Invoke();
    }

    private void SaveProgress()
    {
        if (_recording is null) return;
        try
        {
            double globalSec = GetGlobalPositionSeconds();
            var (chIdx, relMs) = ToChapterPosition(globalSec);
            _library.SaveProgress(_bookId, chIdx, relMs);
        }
        catch { }
    }

    public void ApplyAppearance()
    {
        if (_disposed) return;
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        ThemeHelper.ApplyControlThemeResources(RootGrid.Resources, dark, _library.Prefs.Tint);
        L10n.ApplyTree(RootGrid);

        TopAudioIcon.Content = YYIcons.Make("headphones", 15, ThemeHelper.Brush(tc.Accent));
        DetachBtn.Content = YYIcons.Make("detach", 13.5, ThemeHelper.Brush(p.T2));
        CoverFallbackIcon.Content = YYIcons.Make("headphones", 54, ThemeHelper.Brush(p.T3));
        RewindBtn.Content = YYIcons.Make("goback-15", 18, ThemeHelper.Brush(p.T1));
        ForwardBtn.Content = YYIcons.Make("goforward-30", 18, ThemeHelper.Brush(p.T1));
        SleepBtn.Content = YYIcons.Make("moon", 16, ThemeHelper.Brush(_sleepDeadlineUtc is not null ? tc.Accent : p.T2));
        RateBtn.Content = $"{_rate:0.##}×";

        UpdatePlayVisuals();
        RenderRightPane();
        if (_recording is not null) OnPositionTick();
    }

    private void UpdatePlayVisuals()
    {
        PlayPauseBtn.Content = YYIcons.Make(_playing ? "pause" : "play", 20, ThemeHelper.Brush(Colors.White), filled: true);
    }

    private void RenderRightPane()
    {
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        RightTabsBar.Children.Clear();
        foreach (var (id, lbl) in new[] { ("chapters", "章节"), ("bookmarks", "书签"), ("pair", "配对电子书") })
        {
            bool on = _rightTab == id;
            string cap = id;
            var btn = new Button
            {
                Content = L10n.T(lbl),
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(9, 0, 9, 0),
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(0),
                FontSize = 12,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                Background = on ? ThemeHelper.Brush(tc.Soft) : new SolidColorBrush(Colors.Transparent),
                Foreground = ThemeHelper.Brush(on ? tc.Accent : p.T2),
                UseSystemFocusVisuals = false,
            };
            btn.Click += (_, _) => { _rightTab = cap; RenderRightPane(); };
            RightTabsBar.Children.Add(btn);
        }

        RightPaneStack.Children.Clear();
        if (_rightTab == "chapters" && _recording is not null)
        {
            for (int i = 0; i < _recording.Chapters.Length; i++)
            {
                var ch = _recording.Chapters[i];
                bool cur = i == _currentChapterIdx;
                double startCap = ch.Start;
                var row = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 8, 10, 8),
                    CornerRadius = new CornerRadius(6),
                    BorderThickness = new Thickness(0),
                    Background = cur ? ThemeHelper.Brush(tc.Soft) : new SolidColorBrush(Colors.Transparent),
                    UseSystemFocusVisuals = false,
                };
                var g = new Grid { ColumnSpacing = 8 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = !string.IsNullOrWhiteSpace(ch.Title) ? ch.Title : L10n.T($"第 {i + 1} 集"),
                    FontSize = 12.5,
                    FontWeight = cur ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(cur ? tc.Accent : p.T1),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }));
                var durTb = new TextBlock { Text = FormatClock(ch.Seconds), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) };
                Grid.SetColumn(durTb, 1);
                g.Children.Add(durTb);
                row.Content = g;
                row.Click += (_, _) => SeekGlobalSeconds(startCap, autoPlay: true);
                int chIdxCap = i;
                string currentTitleCap = ch.Title ?? "";
                var chMenu = new MenuFlyout();
                var renameMi = new MenuFlyoutItem { Text = L10n.T("改名") };
                renameMi.Click += async (_, _) => await PromptRenameChapterAsync(chIdxCap, currentTitleCap);
                chMenu.Items.Add(renameMi);
                row.ContextFlyout = chMenu;
                RightPaneStack.Children.Add(row);
            }
        }
        else if (_rightTab == "bookmarks")
        {
            var bms = _library.GetBookmarks(_bookId);
            if (bms.Count == 0)
            {
                RightPaneStack.Children.Add(new TextBlock { Text = L10n.T("听到想记住的地方，点书签按钮记一下。"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 14, 4, 0) });
            }
            else
            {
                foreach (var bm in bms.OrderByDescending(x => x.At))
                {
                    double relSec = bm.Sentence / 1000.0;
                    double globalSec = FromChapterPosition(bm.Section, bm.Sentence);
                    int chIdx = _recording is not null && _recording.Chapters.Length > 0
                        ? Math.Clamp((int)bm.Section, 0, _recording.Chapters.Length - 1)
                        : (int)bm.Section;
                    string chLabel = (_recording is not null && chIdx >= 0 && chIdx < _recording.Chapters.Length && !string.IsNullOrWhiteSpace(_recording.Chapters[chIdx].Title))
                        ? _recording.Chapters[chIdx].Title!
                        : L10n.T($"第 {chIdx + 1} 集");
                    string label = string.IsNullOrWhiteSpace(bm.Text)
                        ? $"{chLabel} · {FormatClock(relSec)}"
                        : $"{chLabel} · {FormatClock(relSec)} · {bm.Text}";
                    string bmId = bm.Id;

                    var rowGrid = new Grid { ColumnSpacing = 4 };
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var jumpBtn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Padding = new Thickness(10, 8, 6, 8),
                        CornerRadius = new CornerRadius(5),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        UseSystemFocusVisuals = false,
                        Content = L10n.Skip(new TextBlock
                        {
                            Text = label,
                            FontSize = 12,
                            Foreground = ThemeHelper.Brush(p.T1),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        }),
                    };
                    jumpBtn.Click += (_, _) => SeekGlobalSeconds(globalSec, autoPlay: true);
                    rowGrid.Children.Add(jumpBtn);

                    var delBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYToolbarIconButtonStyle"],
                        Width = 26,
                        Height = 26,
                        MinWidth = 26,
                        MinHeight = 26,
                        Padding = new Thickness(0),
                        Margin = new Thickness(0, 0, 4, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Content = YYIcons.Make("trash", 12, ThemeHelper.Brush(p.T3)),
                    };
                    ToolTipService.SetToolTip(delBtn, L10n.T("删除"));
                    delBtn.Click += (_, _) =>
                    {
                        if (_disposed) return;
                        try { _library.DeleteBookmark(_bookId, bmId); } catch { }
                        RenderRightPane();
                        _ownerWindow.ShowToast("书签拿掉了", "撤销", () =>
                        {
                            try { _library.RestoreBookmark(_bookId, bmId); } catch { }
                            if (!_disposed) RenderRightPane();
                        });
                    };
                    Grid.SetColumn(delBtn, 1);
                    rowGrid.Children.Add(delBtn);

                    var card = new Border
                    {
                        CornerRadius = new CornerRadius(6),
                        Background = ThemeHelper.Brush(p.Card),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(1),
                        Child = rowGrid,
                    };
                    RightPaneStack.Children.Add(card);
                }
            }
        }
        else
        {
            var pair = _library.Core.ListeningPairs().FirstOrDefault(x => x.Recording == _bookId);
            var shelfEbooks = _library.GetShelfBooks().Where(b => b.Id != _bookId && b.Format != BookFormat.Audio).ToList();

            if (pair is not null)
            {
                var ebook = _library.GetKeptBooks().FirstOrDefault(b => b.Id == pair.Book);
                AudiobookProgress? alignProg = null;
                try { alignProg = _library.Core.AudiobookProgress(_bookId); } catch { }
                bool workActive = _library.AudiobookWork.IsRunning(_bookId);
                var workSt = _library.AudiobookWork.GetStatus(_bookId);
                bool alignDone = alignProg?.Done == true || pair.Made;

                string alignDesc = workActive && workSt is not null
                    ? workSt.Message
                    : (alignDone && alignProg is { Sentences: > 0 }
                        ? L10n.T("合成好了 · 听到哪句，书页就走到哪句")
                        : L10n.T("合成以后听到哪句，书页就走到哪句"));

                RightPaneStack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 10, 12, 10),
                    Child = new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new TextBlock { Text = L10n.T("配对电子书"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) },
                            L10n.Skip(new TextBlock { Text = $"《{ebook?.Title ?? pair.Book}》", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap }),
                            new TextBlock { Text = alignDesc, FontSize = 11.5, Foreground = ThemeHelper.Brush(tc.Accent), TextWrapping = TextWrapping.Wrap },
                        },
                    },
                });

                var openEbookBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = L10n.T("开始听") };
                openEbookBtn.Click += (_, _) => App.CurrentApp.OpenBook(pair.Book, autoListen: true);
                RightPaneStack.Children.Add(openEbookBtn);

                string pairBookId = pair.Book;
                var alignBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = L10n.T(workActive ? "停下合成" : (alignDone ? "重新合成" : "开始合成")) };
                alignBtn.Click += (_, _) =>
                {
                    if (_library.AudiobookWork.IsRunning(_bookId)) _library.AudiobookWork.Stop(_bookId);
                    else _library.AudiobookWork.Start(_bookId, pairBookId, GetGlobalPositionSeconds(), autoDownloadModel: true);
                    RenderRightPane();
                };
                RightPaneStack.Children.Add(alignBtn);

                var checkBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = L10n.T("对齐检查与微调…") };
                checkBtn.Click += (_, _) => AlignCheckAndPolishService.OpenAlignCheckWindow(_library, _bookId);
                RightPaneStack.Children.Add(checkBtn);

                if (_library.AudiobookWork.HasSavedEmissions(_bookId))
                {
                    var fastRealignBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = L10n.T("快速重新对齐") };
                    fastRealignBtn.Click += async (_, _) =>
                    {
                        fastRealignBtn.IsEnabled = false;
                        try
                        {
                            var res = await Task.Run(() => _library.AudiobookWork.RealignFromSavedEmissions(_bookId));
                            if (!_disposed)
                            {
                                _ownerWindow.ShowToast(res is not null ? "重新对齐好了" : "未能重新对齐");
                                RenderRightPane();
                            }
                        }
                        catch (Exception ex)
                        {
                            if (!_disposed) _ownerWindow.ShowToast($"快速对齐失败：{ex.Message}");
                        }
                        finally
                        {
                            if (!_disposed) fastRealignBtn.IsEnabled = true;
                        }
                    };
                    RightPaneStack.Children.Add(fastRealignBtn);
                }

                if (alignDone)
                {
                    // 1:1 macOS AudiobookScreen "这本录音" card (includes export read-along EPUB inside card)
                    RenderSkipSettingsCard(dark, p, tc);
                }

                var unpairBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = L10n.T("取消配对"), Foreground = ThemeHelper.Brush(p.Danger) };
                unpairBtn.Click += (_, _) =>
                {
                    _library.AudiobookWork.Stop(_bookId);
                    try { _library.Core.AudiobookPair(_bookId, null); } catch { }
                    _ownerWindow.ShowToast("已取消配对");
                    RenderRightPane();
                };
                RightPaneStack.Children.Add(unpairBtn);
            }
            else
            {
                var candidate = AudiobookImporter.FindMatchingShelfEbook(_library, _detail.Summary.Title);
                if (candidate is not null)
                {
                    var candCap = candidate;
                    var autoCard = new StackPanel { Spacing = 8 };
                    autoCard.Children.Add(new TextBlock { Text = L10n.T($"书架上有《{candCap.Title}》电子书"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap });
                    autoCard.Children.Add(new TextBlock { Text = L10n.T("合成以后听到哪句，书页就走到哪句"), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap });
                    var autoPairBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = L10n.T("合成") };
                    autoPairBtn.Click += (_, _) =>
                    {
                        try { _library.Core.AudiobookPair(_bookId, candCap.Id); } catch { }
                        _library.AudiobookWork.Start(_bookId, candCap.Id, GetGlobalPositionSeconds(), autoDownloadModel: true);
                        _ownerWindow.ShowToast($"已配上《{candCap.Title}》，正在合成…");
                        RenderRightPane();
                    };
                    autoCard.Children.Add(autoPairBtn);

                    RightPaneStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Background = ThemeHelper.Brush(tc.Soft),
                        BorderBrush = ThemeHelper.Brush(tc.Edge),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(12, 10, 12, 10),
                        Child = autoCard,
                    });
                }
                else
                {
                    RightPaneStack.Children.Add(new TextBlock
                    {
                        Text = L10n.T("配上书架上的电子书，合成以后听到哪句，书页就走到哪句。"),
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.T2),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                if (shelfEbooks.Count > 0)
                {
                    var pickBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = L10n.T("选书…") };
                    var pickMenu = new MenuFlyout();
                    foreach (var eb in shelfEbooks.OrderByDescending(b => b.OpenedAt ?? b.AddedAt).Take(16))
                    {
                        var ebCap = eb;
                        var mi = new MenuFlyoutItem { Text = ebCap.Title };
                        mi.Click += (_, _) =>
                        {
                            try { _library.Core.AudiobookPair(_bookId, ebCap.Id); } catch { }
                            _library.AudiobookWork.Start(_bookId, ebCap.Id, GetGlobalPositionSeconds(), autoDownloadModel: true);
                            _ownerWindow.ShowToast($"已与《{ebCap.Title}》配对");
                            RenderRightPane();
                        };
                        pickMenu.Items.Add(mi);
                    }
                    pickBtn.Flyout = pickMenu;
                    RightPaneStack.Children.Add(pickBtn);
                }
            }
        }
    }

    private void RenderSkipSettingsCard(bool dark, YYPalette p, YYTintColors tc)
    {
        try { _skipSettings = _library.Core.AudiobookSkipSettings(_bookId); } catch { }
        double savedSec = 0;
        int extrasCount = 0;
        try { savedSec = _library.Core.AudiobookSaved(_bookId); } catch { }
        try { extrasCount = _library.Core.AudiobookExtras(_bookId).Length; } catch { }

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = L10n.T("这本录音"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T2),
        });

        void AddToggle(string title, string? subtitle, bool initial, Func<SkipSettings, bool, SkipSettings> mutate)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var textCol = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            textCol.Children.Add(new TextBlock { Text = L10n.T(title), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1) });
            if (!string.IsNullOrEmpty(subtitle))
                textCol.Children.Add(new TextBlock { Text = subtitle, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
            row.Children.Add(textCol);
            var sw = new ToggleSwitch { IsOn = initial, OnContent = null, OffContent = null, MinWidth = 0 };
            sw.Toggled += (_, _) =>
            {
                if (_disposed) return;
                _skipSettings = mutate(_skipSettings, sw.IsOn);
                try { _library.Core.AudiobookSetSkipSettings(_bookId, _skipSettings); } catch { }
                OnPositionTick();
            };
            Grid.SetColumn(sw, 1);
            row.Children.Add(sw);
            stack.Children.Add(row);
        }

        static string FormatSavedDuration(double sec)
        {
            int mins = Math.Max(1, (int)Math.Round(sec / 60.0));
            return mins >= 60
                ? (mins % 60 == 0 ? L10n.T($"{mins / 60} 小时") : L10n.T($"{mins / 60} 小时 {mins % 60} 分"))
                : L10n.T($"{mins} 分钟");
        }

        AddToggle("片头片尾", extrasCount > 0 ? L10n.T("自动跳过片头片尾") : null, _skipSettings.HeadTail, (s, on) => s with { HeadTail = on });
        AddToggle("章间音乐", L10n.T("压到 1.5 秒"), _skipSettings.ShortenChapterGaps, (s, on) => s with { ShortenChapterGaps = on });
        AddToggle("人声清楚一点", L10n.T("压低底噪，人声提一点"), _skipSettings.ClearerVoice, (s, on) => s with { ClearerVoice = on });
        AddToggle("缩短停顿", savedSec >= 60 ? L10n.T($"这本已经省了 {FormatSavedDuration(savedSec)}") : L10n.T("长停顿压短一些"), _skipSettings.ShortenPauses, (s, on) => s with { ShortenPauses = on });
        AddToggle("没念的用朗读补上", L10n.T("录音里跳过的地方自动补读"), _skipSettings.FillUnread, (s, on) => s with { FillUnread = on });
        AddToggle("逐词跟读", L10n.T("念到哪个词，哪个词下面一道线"), _skipSettings.WordFollow, (s, on) => s with { WordFollow = on });

        stack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 2, 0, 2) });
        var expRow = new Grid { ColumnSpacing = 8 };
        expRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        expRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var expMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        expMeta.Children.Add(new TextBlock { Text = L10n.T("导出带朗读时间的电子书"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1) });
        expMeta.Children.Add(new TextBlock { Text = L10n.T("一个 EPUB，别的阅读器也能跟读"), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
        expRow.Children.Add(expMeta);
        var expBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Height = 26, MinHeight = 26, Padding = new Thickness(10, 0, 10, 0), FontSize = 11.5, Content = L10n.T("导出…") };
        expBtn.Click += async (_, _) =>
        {
            try
            {
                string? outPath = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, $"{_detail.Summary.Title}", [("EPUB", [".epub"])]);
                if (string.IsNullOrEmpty(outPath) || _disposed) return;
                await Task.Run(() => _library.Core.AudiobookExportEpub(_bookId, outPath));
                if (!_disposed) _ownerWindow.ShowToast("导出好了", Path.GetFileName(outPath));
            }
            catch (Exception ex)
            {
                if (!_disposed) _ownerWindow.ShowToast($"导出失败：{ex.Message}");
            }
        };
        Grid.SetColumn(expBtn, 1);
        expRow.Children.Add(expBtn);
        stack.Children.Add(expRow);

        RightPaneStack.Children.Add(new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            CornerRadius = new CornerRadius(8),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Child = stack,
        });
    }

    private async Task PromptRenameChapterAsync(int chapterIndex, string currentTitle)
    {
        if (_disposed || RootGrid.XamlRoot is not { } xr) return;
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        string? next = await YYChrome.ShowPromptAsync(
            xr,
            dark,
            "给这一集起个名字",
            placeholder: $"第 {chapterIndex + 1} 集",
            initialText: currentTitle,
            confirmText: "好",
            cancelText: "取消",
            subtitle: "留空就叫「第几集」。",
            tint: _library.Prefs.Tint);
        if (next is null || _disposed) return;
        string? normalized = string.IsNullOrWhiteSpace(next) ? null : next.Trim();
        try
        {
            _library.Core.RenameAudioChapter(_bookId, (uint)chapterIndex, normalized);
            _recording = _library.Core.AudioRecording(_bookId);
            RenderRightPane();
            OnPositionTick();
            _ownerWindow.ShowToast("改好了");
        }
        catch (Exception ex)
        {
            _ownerWindow.ShowToast($"改名失败：{ex.Message}");
        }
    }

    private void BuildSleepMenu()
    {
        var menu = new MenuFlyout();
        void Add(string lbl, int minutes)
        {
            var mi = new MenuFlyoutItem { Text = L10n.T(lbl) };
            mi.Click += (_, _) =>
            {
                if (_disposed) return;
                _sleepDeadlineUtc = minutes > 0 ? DateTime.UtcNow.AddMinutes(minutes) : null;
                _sleepArmedPos = minutes > 0 ? ToChapterPosition(GetGlobalPositionSeconds()) : null;
                if (_sleepDeadlineUtc is null) SleepStatusText.Text = "";
                ApplyAppearance();
            };
            menu.Items.Add(mi);
        }
        Add("15 分钟", 15);
        Add("30 分钟", 30);
        Add("45 分钟", 45);
        Add("60 分钟", 60);
        Add("90 分钟", 90);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("关闭", 0);
        SleepBtn.Flyout = menu;
    }

    private static string FormatClock(double seconds)
    {
        var ts = System.TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    // Event handlers
    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlayPause();
    private void Rewind15_Click(object sender, RoutedEventArgs e) => SeekGlobalSeconds(GetGlobalPositionSeconds() - 15, _playing || _pendingAutoPlay);
    private void Forward30_Click(object sender, RoutedEventArgs e) => SeekGlobalSeconds(GetGlobalPositionSeconds() + 30, _playing || _pendingAutoPlay);

    private void Rate_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        double[] rates = [0.75, 1.0, 1.25, 1.5, 1.75, 2.0];
        int idx = Array.FindIndex(rates, r => Math.Abs(r - _rate) < 0.05);
        _rate = rates[(idx + 1) % rates.Length];
        try { _player.PlaybackSession.PlaybackRate = _rate; } catch { }
        _library.Prefs.AudiobookRate = _rate;
        _library.SavePrefs();
        RateBtn.Content = $"{_rate:0.##}×";
        StateChanged?.Invoke();
    }

    private void ChapterSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_disposed || _recording is null) return;
        _isScrubbingSlider = true;
        _scrubDirty = false;
        _scrubPendingValue = ChapterSlider.Value;
    }

    private void ChapterSlider_PointerReleasedOrLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isScrubbingSlider) return;
        _isScrubbingSlider = false;
        if (_disposed || _recording is null) return;
        if (_scrubDirty)
        {
            _scrubDirty = false;
            double chapStart = GetChapterStartSeconds(_currentChapterIdx);
            SeekGlobalSeconds(chapStart + _scrubPendingValue, _playing || _pendingAutoPlay);
        }
    }

    private void ChapterSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlider || _disposed || _recording is null) return;
        if (_isScrubbingSlider)
        {
            _scrubDirty = true;
            _scrubPendingValue = e.NewValue;
            ElapsedText.Text = FormatClock(e.NewValue);
            return;
        }
        double chapStart = GetChapterStartSeconds(_currentChapterIdx);
        SeekGlobalSeconds(chapStart + e.NewValue, _playing || _pendingAutoPlay);
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _recording is null) return;
        double globalSec = GetGlobalPositionSeconds();
        var (chIdx, relMs) = ToChapterPosition(globalSec);
        _library.AddBookmark(_bookId, chIdx, relMs);
        if (_rightTab == "bookmarks") RenderRightPane();
        BookSentence? heard = null;
        try { heard = _library.Core.AudiobookHeardSentence(_bookId, globalSec); } catch { }
        if (heard is not null)
        {
            var captured = heard;
            _ownerWindow.ShowToast("记了一个书签", "回原文 ›", () =>
            {
                App.CurrentApp.OpenBook(captured.Book, section: captured.Section, sentence: (int)captured.Sentence);
            });
        }
        else
        {
            _ownerWindow.ShowToast("记了一个书签");
        }
    }

    private void OpenMiniPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (!_disposed) App.CurrentApp.ShowMiniPlayer(this);
    }

    private void Detach_Click(object sender, RoutedEventArgs e)
    {
        if (!_disposed) App.CurrentApp.TearOutTabToNewWindow(_ownerWindow, this);
    }

    // ITabContent
    public void OnTabActivated() => ApplyAppearance();
    public void OnTabDeactivated() { }
    public void OnAppearanceChanged() => ApplyAppearance();
    public void OnReaderSettingsChanged() { }
    public void OnVoiceChanged() { }
    public void OnForeignPrefsChanged() { }
    public void OnSnapshotRestored() => ApplyAppearance();
    public void SetOwnerWindow(MainWindow owner) => _ownerWindow = owner;
    public void SetFullScreenChrome(bool isFullScreen) { }

    public bool HandleWindowKeyDown(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        if (!ctrl && !alt)
        {
            if (key == VirtualKey.Space) { TogglePlayPause(); return true; }
            if (key == VirtualKey.Left) { Rewind15_Click(this, new RoutedEventArgs()); return true; }
            if (key == VirtualKey.Right) { Forward30_Click(this, new RoutedEventArgs()); return true; }
            if (key == VirtualKey.B) { AddBookmark_Click(this, new RoutedEventArgs()); return true; }
        }
        return false;
    }

    public bool HandleMouseBackForward(bool isBack) => false;
    public SavedTabEntry? SerializeTabState() => new(_bookId, "audiobook");
    public Task<bool> PrepareCloseAsync() => Task.FromResult(true);
    public async Task<bool> SaveCompositeScreenshotAsync(string path)
    {
        try { await Yuyue.Reader.QuoteShareDialog.SaveElementPngAsync(RootGrid, path); return File.Exists(path); }
        catch { return false; }
    }

    // IMiniPlayerSource
    void IMiniPlayerSource.TogglePlayPause() { if (!_disposed) TogglePlayPause(); }
    void IMiniPlayerSource.SkipPrevious() { if (!_disposed) Rewind15_Click(this, new RoutedEventArgs()); }
    void IMiniPlayerSource.SkipNext() { if (!_disposed) Forward30_Click(this, new RoutedEventArgs()); }
    void IMiniPlayerSource.CycleRate() { if (!_disposed) Rate_Click(this, new RoutedEventArgs()); }
    void IMiniPlayerSource.JumpToSource()
    {
        if (_disposed) return;
        App.CurrentApp.RestoreAndSelectTab(_ownerWindow, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (_mediaReady && _pendingSeekInFile is null)
                _lastKnownInFileSec = _player.PlaybackSession.Position.TotalSeconds;
        }
        catch { }
        _disposed = true;
        _posTimer.Stop();
        FlushListeningTime();
        SaveProgress();
        try
        {
            _player.MediaOpened -= OnMediaOpened;
            _player.MediaFailed -= OnMediaFailed;
            _player.MediaEnded -= OnMediaEnded;
            _player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
        }
        catch { }
        try { App.CurrentApp.ClearActiveMiniPlayerSource(this); } catch { }
        try { App.CurrentApp.ReleaseAudioClaim(this); } catch { }
        try { _player.Pause(); _library.SystemIntegration.ClearAudioSource(this); _player.Dispose(); } catch { }
    }
}
