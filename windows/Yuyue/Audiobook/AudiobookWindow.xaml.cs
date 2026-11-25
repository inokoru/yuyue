using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Reader;
using Yuyue.Shelf;
using Yuyue.Speech;
using TimeSpan = System.TimeSpan;

namespace Yuyue.Audiobook;

/// <summary>
/// 1:1 port of <c>apple/Yuyue/Library/AudiobookScreen.swift</c>:
/// open warm-paper stage on the left (<c>240×240</c> square cover, chapter title above <c>Book · Author</c>,
/// per-chapter scrubber with bookmark ticks, 5 transport controls, keyboard keycap bar) and
/// <c>300px</c> <c>ChapterColumn</c> (chapters + bookmarks) + <c>pairCard</c> on the right.
/// </summary>
public sealed partial class AudiobookWindow : Window
{
    private readonly LibraryService _library;
    private readonly string _bookId;
    private BookSummary _summary;
    private AudioRecording? _recording;

    private readonly MediaPlayer _player;
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _listenTimeTracker;
    private DispatcherTimer? _toastTimer;

    private double[] _fileStartOffsets = [];
    private int _currentFileIndex = -1;
    private double _globalPosSec;
    private bool _playing;
    private bool _suppressSlider;
    private bool _isScrubbingSlider;
    private double _pendingSliderInChapSec = -1;
    private DispatcherTimer? _sliderSeekTimer;
    private int _seekRequestVersion;
    private double _rate = 1.0;
    private readonly bool _startFromBeginning;

    private (double RemoteSec, int RemoteChap, string? DeviceName, long RemoteAtMs, double RemoteFraction)? _elsewhereState;
    private (SleepReturnInfo Info, double ReturnSec)? _sleepReturnState;
    private string? _exportingStatus;
    private MediaPlayer? _introPreviewPlayer;
    private DispatcherTimer? _introPreviewStopTimer;

    // Sleep timer: 0 = off, -1 = end of chapter, -2 = end of sentence, >0 = timed (wall-clock deadline + 20s fade)
    private int _sleepMode;
    private double _sleepRemainingSec;
    private DateTime? _sleepDeadlineUtc;
    private DateTime _sleepLastTickUtc = DateTime.UtcNow;
    private bool _isSleepFading;
    private double _sleepSentenceEndSec = -1;
    private (uint Chapter, int SecMs)? _sleepArmedSpot;
    private readonly CancellationTokenSource _windowCts = new();

    // Alignment, VoiceTapDsp, ResumeAt, ExtraSpan notice & exact listening time
    private DateTime? _pausedUtc;
    private DateTime? _listenSinceUtc;
    private double _baseVolume = 1.0;
    private bool _loadedFileIsClearer;
    private double _lastMomentSkipTarget = -1;
    private double _ignoreExtraUntilSec = -1;
    private SyncMap? _cachedMap;
    private SkipSettings _cachedSkipSettings = new(true, false, false, false, false, false, false);
    private Action? _toastAction;
    private bool _closed;

    // 1:1 AudiobookPlayer.swift FillUnread: contiguous SyncBy.Unread stretches spoken via child AudioPlayer
    private sealed record UnreadRun(double At, double Resume, string[] Texts);
    private UnreadRun[] _unreadRuns = [];
    private UnreadRun? _activeUnreadRun;
    private readonly AudioPlayer _fillTtsPlayer;
    private double _prevGlobalPosSec;

    // Remote/Share streaming state & per-book auto-clear preference
    private bool _isStreamedAudiobook;
    private int _bufferingTrackIndex = -1;
    private readonly HashSet<int> _downloadingChapterTracks = [];
    private bool _autoClearPlayedStreamTracks = true;
    private int _activeBackgroundDecodeCount;

    private static readonly double[] SpeedOptions = [0.75, 1.0, 1.1, 1.2, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    public AudiobookWindow(LibraryService library, string bookId, bool autoPlay = false, bool startFromBeginning = false)
    {
        _library = library;
        _bookId = bookId;
        _startFromBeginning = startFromBeginning;
        _summary = library.GetKeptBooks().FirstOrDefault(b => b.Id == bookId)
            ?? library.GetBookDetail(bookId).Summary;
        _rate = Math.Clamp(_library.Prefs.AudiobookRate > 0 ? _library.Prefs.AudiobookRate : 1.0, 0.75, 3.0);
        (_autoClearPlayedStreamTracks, _isStreamedAudiobook) = LoadStreamPrefs();

        InitializeComponent();
        Title = _summary.Title;
        int initW = Math.Max(760, _library.Prefs.AudiobookWindowWidth > 0 ? _library.Prefs.AudiobookWindowWidth : 960);
        int initH = Math.Max(520, _library.Prefs.AudiobookWindowHeight > 0 ? _library.Prefs.AudiobookWindowHeight : 640);
        ThemeHelper.ResizeWindow(this, initW, initH, minWidthDip: 760, minHeightDip: 520);
        SetTitleBar(TitleDragRegion);
        ThemeHelper.ApplyElevation(CoverFrame, ShadowReceiver, 18f);
        ThemeHelper.ApplyElevation(ToastBanner, ShadowReceiver, 10f);

        _fillTtsPlayer = new AudioPlayer(_library);
        App.CurrentApp.BindAudioClaimOwner(_fillTtsPlayer, this);
        _fillTtsPlayer.SectionFinished += OnFillTtsFinished;
        _fillTtsPlayer.SynthesisError += OnFillTtsError;

        _player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Speech,
            AutoPlay = false,
        };
        _player.CommandManager.IsEnabled = false;
        _player.MediaEnded += OnFileEnded;

        var smtc = _player.SystemMediaTransportControls;
        smtc.IsEnabled = true;
        smtc.IsPlayEnabled = true;
        smtc.IsPauseEnabled = true;
        smtc.IsNextEnabled = true;
        smtc.IsPreviousEnabled = true;
        smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
        smtc.ButtonPressed += OnSmtcButtonPressed;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        _listenTimeTracker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _listenTimeTracker.Tick += (_, _) => FlushListeningTime();
        _listenTimeTracker.Start();

        _sliderSeekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _sliderSeekTimer.Tick += async (_, _) =>
        {
            _sliderSeekTimer.Stop();
            if (_pendingSliderInChapSec >= 0 && _recording is not null)
            {
                double inChap = _pendingSliderInChapSec;
                _pendingSliderInChapSec = -1;
                var (chStart, _) = CurrentChapterBounds();
                await SeekGlobalAsync(chStart + inChap, _playing);
            }
        };
        ChapterSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _isScrubbingSlider = true), true);
        ChapterSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnChapterSliderPointerReleased), true);
        ChapterSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnChapterSliderPointerReleased), true);

        BookmarkTicksCanvas.SizeChanged += (_, _) => RenderBookmarkTicks();
        _library.AudiobookWork.StatusChanged += OnAudiobookWorkStatusChanged;
        Closed += OnWindowClosed;

        ApplyAppearance();
        _library.Analytics.TrackScreen("Reader");
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                _library.Analytics.TrackScreen("Reader");
        };
        _ = LoadRecordingAsync(autoPlay);
        TryAutoResumeAlignment();
    }

    public void JumpToStartAndPlay() => _ = SeekGlobalAsync(0, startPlaying: true);

    public void EnsurePlaying()
    {
        if (!_playing) PlayPause_Click(this, new RoutedEventArgs());
    }

    private void FlushListeningTime()
    {
        if (_listenSinceUtc is not DateTime since) return;
        double sec = (DateTime.UtcNow - since).TotalSeconds;
        _listenSinceUtc = _playing ? DateTime.UtcNow : null;
        uint whole = (uint)Math.Max(0, Math.Round(sec));
        if (whole >= 2)
        {
            try { _library.AddTime(_bookId, (uint)CurrentChapterIndex(), 0, whole); } catch { }
        }
    }

    private void OnAudiobookWorkStatusChanged(AudiobookWorkStatus status)
    {
        if (_closed || status.RecordingId != _bookId) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            RefreshCachedMap();
            RebuildRecordingSettings();
            RenderPairCard();
            UpdateKeyHints();
            if (status.Phase == AudiobookWorkPhase.Done)
                ShowToast(status.Message);
            else if (status.Phase == AudiobookWorkPhase.Failed)
                ShowToast($"对齐出错：{status.Message}");
        });
    }

    private bool RefreshCachedMap()
    {
        try { _cachedSkipSettings = _library.Core.AudiobookSkipSettings(_bookId); }
        catch { _cachedSkipSettings = new SkipSettings(true, false, false, false, false, false, false); }

        try { _cachedMap = _library.Core.AudiobookMap(_bookId); }
        catch { _cachedMap = null; }

        if (_cachedMap is null || _cachedMap.Rows.Length == 0)
        {
            _unreadRuns = [];
            return false;
        }

        // Build UnreadRun stretches (1:1 AudiobookPlayer.swift:850-896)
        var rows = _cachedMap.Rows;
        var runs = new List<UnreadRun>();
        var secCache = new Dictionary<uint, IReadOnlyList<string>>();
        double lastTimedEndSec = 0.0;
        int i = 0;
        while (i < rows.Length)
        {
            if (rows[i].By != SyncBy.Unread)
            {
                lastTimedEndSec = Math.Max(lastTimedEndSec, rows[i].T1Ms / 1000.0);
                i++;
                continue;
            }

            int start = i;
            while (i < rows.Length && rows[i].By == SyncBy.Unread)
                i++;

            double at = lastTimedEndSec;
            double resume = i < rows.Length
                ? Math.Max(at, rows[i].T0Ms / 1000.0)
                : Math.Max(at, _recording?.Seconds ?? at);

            var texts = new List<string>(i - start);
            for (int k = start; k < i; k++)
            {
                var r = rows[k];
                if (!secCache.TryGetValue(r.Section, out var sents))
                {
                    try { sents = _library.GetSentences(_cachedMap.BookId, r.Section); }
                    catch { sents = []; }
                    secCache[r.Section] = sents;
                }
                if (r.Sentence < (uint)sents.Count)
                {
                    string txt = sents[(int)r.Sentence];
                    if (!string.IsNullOrWhiteSpace(txt))
                        texts.Add(txt);
                }
            }

            if (texts.Count > 0)
                runs.Add(new UnreadRun(at, resume, texts.ToArray()));
        }

        _unreadRuns = runs.ToArray();
        return true;
    }

    private void StartUnreadFill(UnreadRun run)
    {
        if (_closed || run.Texts.Length == 0) return;
        _activeUnreadRun = run;
        try { _player.Pause(); } catch { }
        ShowToast("录音里没念这里，朗读补上");

        string ebookId = _cachedMap?.BookId ?? _bookId;
        string lang = _library.GetBooks().FirstOrDefault(b => b.Id == ebookId)?.Language ?? _summary.Language;
        _fillTtsPlayer.SetBookContext(ebookId, lang);
        _fillTtsPlayer.SetVoice(_library.Prefs.GetVoiceForLanguage(lang));
        _fillTtsPlayer.SetMetadata(_summary.Title, ChapterTitle(CurrentChapterIndex()), _summary.CoverPath);
        _fillTtsPlayer.SetRate(_rate);
        _fillTtsPlayer.Start(run.Texts, 0);
    }

    private void OnFillTtsFinished() => CompleteActiveUnreadFill();

    private void OnFillTtsError(string _) => CompleteActiveUnreadFill();

    private void CompleteActiveUnreadFill()
    {
        if (_closed) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_closed || _activeUnreadRun is not { } run) return;
            _activeUnreadRun = null;
            try { _fillTtsPlayer.Stop(); } catch { }
            if (_playing)
            {
                double target = Math.Max(run.At + 0.02, run.Resume);
                await SeekGlobalAsync(target, startPlaying: true);
            }
        });
    }

    private sealed class AudiobookStreamPrefsDoc
    {
        public Dictionary<string, bool> AutoClear { get; set; } = new();
        public HashSet<string> StreamedBooks { get; set; } = new();
    }

    private string StreamPrefsPath => Path.Combine(_library.DataDir, "audiobook-stream-prefs.json");

    private (bool AutoClear, bool IsStreamed) LoadStreamPrefs()
    {
        try
        {
            if (File.Exists(StreamPrefsPath))
            {
                var doc = JsonSerializer.Deserialize<AudiobookStreamPrefsDoc>(File.ReadAllText(StreamPrefsPath));
                if (doc is not null)
                {
                    bool autoClear = !doc.AutoClear.TryGetValue(_bookId, out bool ac) || ac;
                    bool isStreamed = doc.StreamedBooks.Contains(_bookId);
                    return (autoClear, isStreamed);
                }
            }
        }
        catch { }
        return (true, false);
    }

    private void SaveStreamPrefs()
    {
        try
        {
            AudiobookStreamPrefsDoc doc = new();
            if (File.Exists(StreamPrefsPath))
            {
                try { doc = JsonSerializer.Deserialize<AudiobookStreamPrefsDoc>(File.ReadAllText(StreamPrefsPath)) ?? new(); }
                catch { doc = new(); }
            }
            doc.AutoClear[_bookId] = _autoClearPlayedStreamTracks;
            if (_isStreamedAudiobook)
                doc.StreamedBooks.Add(_bookId);
            File.WriteAllText(StreamPrefsPath, JsonSerializer.Serialize(doc));
        }
        catch { }
    }

    private static BitmapImage CreateCoverBitmap(string path, int decodePixelWidthDip)
    {
        var bmp = new BitmapImage();
        if (decodePixelWidthDip > 0)
            bmp.DecodePixelWidth = decodePixelWidthDip;
        bmp.UriSource = new Uri(path);
        return bmp;
    }

    private bool HasPairedMap => _cachedMap is not null && _cachedMap.Rows.Length > 0;

    private void TryAutoResumeAlignment()
    {
        try
        {
            var prog = _library.Core.AudiobookProgress(_bookId);
            if (!string.IsNullOrEmpty(prog.PairedWith) && !prog.Done && Aligner.IsInstalled(_library.DataDir))
                _library.AudiobookWork.Start(_bookId, prog.PairedWith, _globalPosSec, autoDownloadModel: false, trackAnalytics: false);
        }
        catch { }
    }

    public void ApplyAppearance()
    {
        RootGrid.RequestedTheme = _library.Prefs.ToElementTheme();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var t1 = ThemeHelper.Brush(p.T1);
        var t2 = ThemeHelper.Brush(p.T2);

        IconBack.Content = YYIcons.Make("chevron-left", 11.5, t1);
        CoverFallbackIcon.Content = YYIcons.Make("headphones", 46, ThemeHelper.Brush(p.T3));
        RateBtn.Content = $"{_rate:0.##}×";
        RateBtn.Foreground = t1;
        Rewind15Btn.Content = YYIcons.Make("goback-15", 19, t1);
        Forward30Btn.Content = YYIcons.Make("goforward-30", 19, t1);
        SleepBtn.Content = YYIcons.Make("moon", 16, _sleepMode != 0 ? ThemeHelper.Brush(tint.Accent) : t2, filled: _sleepMode != 0);

        UpdateTransportUi();
        ThemeHelper.ConfigureTitleBar(this, dark);
        RenderChaptersAndBookmarks();
        RebuildRecordingSettings();
        RenderPairCard();
        RenderInlineReturnCards();
        UpdateKeyHints();
        L10n.Skip(BookBylineLabel);
        L10n.ApplyTree(RootGrid);
    }

    private async Task LoadRecordingAsync(bool autoPlay)
    {
        string author = _summary.Authors.FirstOrDefault() ?? "";
        BookBylineLabel.Text = string.IsNullOrWhiteSpace(author) ? _summary.Title : $"{_summary.Title} · {author}";

        if (!string.IsNullOrEmpty(_summary.CoverPath) && File.Exists(_summary.CoverPath))
        {
            CoverImage.Source = CreateCoverBitmap(_summary.CoverPath, 480);
            CoverFallbackIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            CoverFallbackIcon.Visibility = Visibility.Visible;
        }

        try
        {
            _recording = _library.Core.AudioRecording(_bookId);
        }
        catch (Exception ex)
        {
            ChapterLabel.Text = $"无法读取音频文件：{ex.Message}";
            return;
        }

        RebuildFileOffsets();
        if (_recording.Files.Any(f =>
            !File.Exists(ResolveTrackPath(f.Path)) ||
            f.Path.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
            f.Path.Contains("remote", StringComparison.OrdinalIgnoreCase) ||
            f.Path.Contains("share", StringComparison.OrdinalIgnoreCase)))
        {
            if (!_isStreamedAudiobook)
            {
                _isStreamedAudiobook = true;
                SaveStreamPrefs();
            }
        }

        double totalSec = Math.Max(1.0, _recording.Seconds);
        var saved = _library.GetProgress(_bookId);
        if (_startFromBeginning)
        {
            _globalPosSec = 0;
        }
        else if (saved is not null && _recording.Chapters.Length > 0)
        {
            int ch = Math.Clamp((int)saved.Position.Section, 0, _recording.Chapters.Length - 1);
            _globalPosSec = Math.Clamp(_recording.Chapters[ch].Start + saved.Position.Sentence / 1000.0, 0, totalSec);
        }
        _prevGlobalPosSec = _globalPosSec;

        RefreshCachedMap();
        if (_library.Prefs.AudiobookLoudnessGain.TryGetValue(_bookId, out float savedGain))
            _baseVolume = Math.Clamp(savedGain, 0.35, 1.0);
        else
            _ = MeasureBookLoudnessInBackgroundAsync();

        await SeekGlobalAsync(_globalPosSec, startPlaying: autoPlay);
        SaveCurrentProgress();
        App.CurrentApp.NotifyShelfRefresh();
        RenderChaptersAndBookmarks();
        RebuildRecordingSettings();
        RenderPairCard();
        UpdateKeyHints();

        // Offer persistent inline ElsewhereCard (>= 30s threshold) or SleepReturnCard (1:1 AudiobookScreen.swift:131-139, 199-204)
        _elsewhereState = null;
        _sleepReturnState = null;
        try
        {
            var latest = _library.Core.LatestProgress(_bookId);
            if (latest is not null && !latest.ThisDevice && saved is not null && latest.At > saved.At && _recording.Chapters.Length > 0)
            {
                int rCh = Math.Clamp((int)latest.Position.Section, 0, _recording.Chapters.Length - 1);
                double remoteSec = _recording.Chapters[rCh].Start + latest.Position.Sentence / 1000.0;
                if (Math.Abs(remoteSec - _globalPosSec) >= 30.0)
                {
                    _elsewhereState = (remoteSec, rCh, latest.DeviceName, latest.At, latest.Fraction);
                }
            }
        }
        catch { }

        if (ReaderRecapAndSleepHelper.TryGetSleepReturn(_library, _bookId) is { } sleepRet)
        {
            int targetCh = Math.Clamp((int)sleepRet.Section, 0, Math.Max(0, _recording.Chapters.Length - 1));
            double inChSec = sleepRet.Sentence / 1000.0;
            double returnSec = _recording.Chapters.Length > 0 ? _recording.Chapters[targetCh].Start + inChSec : inChSec;
            string place = $"{ChapterTitle(targetCh)} {FormatTime(inChSec)}";
            _sleepReturnState = (sleepRet with { Place = place }, returnSec);
        }

        RenderInlineReturnCards();
    }

    private void RebuildFileOffsets()
    {
        if (_recording is null)
        {
            _fileStartOffsets = [];
            return;
        }
        _fileStartOffsets = new double[_recording.Files.Length];
        double acc = 0;
        for (int i = 0; i < _recording.Files.Length; i++)
        {
            _fileStartOffsets[i] = acc;
            acc += _recording.Files[i].Seconds;
        }
    }

    private string ResolveTrackPath(string rawPath) =>
        Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(_library.DataDir, rawPath);

    private async Task MeasureBookLoudnessInBackgroundAsync()
    {
        if (_closed || _recording is null || _recording.Files.Length == 0) return;
        string firstPath = ResolveTrackPath(_recording.Files[0].Path);
        if (!File.Exists(firstPath)) return;
        Interlocked.Increment(ref _activeBackgroundDecodeCount);
        try
        {
            string scratch = Path.Combine(_library.DataDir, "audiobooks", _bookId, "pcm-cache");
            string pcm = await Aligner.EnsurePcm16kWavAsync(firstPath, scratch, 0, _windowCts.Token);
            if (_closed) return;
            float gain = VoiceTapDsp.MeasureLoudnessFromWav(pcm);
            _library.Prefs.AudiobookLoudnessGain[_bookId] = gain;
            _library.SavePrefs();
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed) return;
                _baseVolume = Math.Clamp(gain, 0.35, 1.0);
                if (!_isSleepFading) _player.Volume = _baseVolume;
            });
        }
        catch { }
        finally
        {
            if (Interlocked.Decrement(ref _activeBackgroundDecodeCount) == 0 && _closed)
            {
                try { _windowCts.Dispose(); } catch { }
            }
        }
    }

    private int TrackIndexForChapter(int chIdx)
    {
        if (_recording is null || _recording.Files.Length == 0) return -1;
        if (_recording.Chapters.Length == _recording.Files.Length)
            return Math.Clamp(chIdx, 0, _recording.Files.Length - 1);
        double chStart = chIdx >= 0 && chIdx < _recording.Chapters.Length ? _recording.Chapters[chIdx].Start : 0;
        int count = Math.Min(_fileStartOffsets.Length, _recording.Files.Length);
        int fileIdx = Math.Max(0, count - 1);
        for (int i = 0; i < count; i++)
        {
            double s = _fileStartOffsets[i];
            double d = _recording.Files[i].Seconds;
            if (chStart < s + d) { fileIdx = i; break; }
            if (d <= 0 && chStart <= s) { fileIdx = i; break; }
        }
        return fileIdx;
    }

    private async Task DownloadStreamTrackInBackgroundAsync(int trackIdx)
    {
        if (_closed || _recording is null || trackIdx < 0 || trackIdx >= _recording.Files.Length) return;
        if (!_downloadingChapterTracks.Add(trackIdx)) return;
        RenderChaptersAndBookmarks();
        Interlocked.Increment(ref _activeBackgroundDecodeCount);
        try
        {
            bool ok = await _library.RemoteLibraries.EnsureStreamTrackAsync(_bookId, trackIdx, _windowCts.Token);
            if (ok && !_closed)
            {
                try
                {
                    _recording = _library.Core.AudioRecording(_bookId);
                    RebuildFileOffsets();
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            _downloadingChapterTracks.Remove(trackIdx);
            if (!_closed)
            {
                RenderChaptersAndBookmarks();
                SyncChapterUi();
            }
            if (Interlocked.Decrement(ref _activeBackgroundDecodeCount) == 0 && _closed)
            {
                try { _windowCts.Dispose(); } catch { }
            }
        }
    }

    private int CurrentChapterIndex()
    {
        if (_recording is null || _recording.Chapters.Length == 0) return 0;
        int hit = 0;
        for (int i = 0; i < _recording.Chapters.Length; i++)
        {
            if (_globalPosSec + 0.25 >= _recording.Chapters[i].Start) hit = i;
            else break;
        }
        return hit;
    }

    private string ChapterTitle(int index)
    {
        if (_recording is null || index < 0 || index >= _recording.Chapters.Length) return _summary.Title;
        // Prefer paired ebook chapter title from SyncMap.Chapters when available (1:1 AudiobookPlayer.swift bookTitles)
        if (_cachedMap is not null && _cachedMap.Chapters.Length > 0)
        {
            double chStart = _recording.Chapters[index].Start;
            var matched = _cachedMap.Chapters.LastOrDefault(c => c.T0 <= chStart + 5.0);
            if (matched is not null && !string.IsNullOrWhiteSpace(matched.Title))
                return matched.Title;
        }
        var raw = _recording.Chapters[index].Title;
        return string.IsNullOrWhiteSpace(raw) ? $"第 {index + 1} 集" : raw;
    }

    private (double Start, double Duration) CurrentChapterBounds()
    {
        if (_recording is null || _recording.Chapters.Length == 0)
            return (0, Math.Max(1.0, _recording?.Seconds ?? 1.0));
        int idx = CurrentChapterIndex();
        var ch = _recording.Chapters[idx];
        return (ch.Start, Math.Max(1.0, ch.Seconds));
    }

    private async Task SeekGlobalAsync(double targetSec, bool startPlaying, bool forceReload = false, int? explicitFileIndex = null)
    {
        if (_closed || _recording is null || _recording.Files.Length == 0) return;
        if (_activeUnreadRun is not null)
        {
            _activeUnreadRun = null;
            try { _fillTtsPlayer.Stop(); } catch { }
        }
        int reqVer = ++_seekRequestVersion;
        double clamped = Math.Clamp(targetSec, 0, Math.Max(0, _recording.Seconds - 0.1));
        _globalPosSec = clamped;
        _prevGlobalPosSec = clamped;

        int fileIdx = 0;
        if (explicitFileIndex is int expIdx && expIdx >= 0 && expIdx < _recording.Files.Length)
        {
            fileIdx = expIdx;
        }
        else
        {
            // 1:1 b4424bd AudiobookPlayer.fileIndex(at:): never prematurely break when an intermediate or trailing file has Seconds <= 0!
            int count = Math.Min(_fileStartOffsets.Length, _recording.Files.Length);
            fileIdx = Math.Max(0, count - 1);
            for (int i = 0; i < count; i++)
            {
                double s = _fileStartOffsets[i];
                double d = _recording.Files[i].Seconds;
                if (clamped < s + d) { fileIdx = i; break; }
                if (d <= 0 && clamped <= s) { fileIdx = i; break; }
            }
        }

        bool wantClearer = _cachedSkipSettings.ClearerVoice;

        double localSec = fileIdx < _fileStartOffsets.Length ? Math.Max(0, clamped - _fileStartOffsets[fileIdx]) : 0;
        if (fileIdx != _currentFileIndex || forceReload || wantClearer != _loadedFileIsClearer)
        {
            string filePath = ResolveTrackPath(_recording.Files[fileIdx].Path);
            if (!File.Exists(filePath))
            {
                if (!_isStreamedAudiobook)
                {
                    _isStreamedAudiobook = true;
                    SaveStreamPrefs();
                    RebuildRecordingSettings();
                }
                _bufferingTrackIndex = fileIdx;
                int waitEpNum = (_recording.Chapters.Length == _recording.Files.Length ? fileIdx : CurrentChapterIndex()) + 1;
                WaitingStreamText.Text = L10n.T($"第 {waitEpNum} 集还在下，下好就接着放");
                WaitingStreamText.Visibility = Visibility.Visible;
                PlayBufferingRing.IsActive = true;
                PlayBufferingRing.Visibility = Visibility.Visible;
                RenderChaptersAndBookmarks();
                SyncChapterUi();

                bool fetched = false;
                Interlocked.Increment(ref _activeBackgroundDecodeCount);
                try
                {
                    fetched = await _library.RemoteLibraries.EnsureStreamTrackAsync(_bookId, fileIdx, _windowCts.Token);
                }
                catch (OperationCanceledException) { return; }
                catch { fetched = false; }
                finally
                {
                    if (!_closed && reqVer == _seekRequestVersion)
                    {
                        _bufferingTrackIndex = -1;
                        WaitingStreamText.Visibility = Visibility.Collapsed;
                        PlayBufferingRing.IsActive = false;
                        PlayBufferingRing.Visibility = Visibility.Collapsed;
                    }
                    if (Interlocked.Decrement(ref _activeBackgroundDecodeCount) == 0 && _closed)
                    {
                        try { _windowCts.Dispose(); } catch { }
                    }
                }

                if (_closed || reqVer != _seekRequestVersion) return;

                if (fetched)
                {
                    try
                    {
                        _recording = _library.Core.AudioRecording(_bookId);
                        RebuildFileOffsets();
                    }
                    catch { }
                    if (_recording is not null && fileIdx < _recording.Files.Length)
                        filePath = ResolveTrackPath(_recording.Files[fileIdx].Path);
                    if (explicitFileIndex is not null && fileIdx < _fileStartOffsets.Length)
                    {
                        clamped = _fileStartOffsets[fileIdx];
                        _globalPosSec = clamped;
                        _prevGlobalPosSec = clamped;
                    }
                    localSec = fileIdx < _fileStartOffsets.Length ? Math.Max(0, clamped - _fileStartOffsets[fileIdx]) : 0;
                }

                if (!File.Exists(filePath))
                {
                    ShowToast("无法缓冲该音轨，请检查网络连接");
                    RenderChaptersAndBookmarks();
                    SyncChapterUi();
                    return;
                }

                RenderChaptersAndBookmarks();
            }
            else
            {
                _bufferingTrackIndex = -1;
                WaitingStreamText.Visibility = Visibility.Collapsed;
                PlayBufferingRing.IsActive = false;
                PlayBufferingRing.Visibility = Visibility.Collapsed;
            }

            string playPath = filePath;
            bool usedClearer = false;
            if (wantClearer)
            {
                string dspPath = Path.Combine(_library.DataDir, "audiobooks", _bookId, $"clear-{fileIdx}.wav");
                if (File.Exists(dspPath))
                {
                    playPath = dspPath;
                    usedClearer = true;
                }
                else
                {
                    ShowToast("正在生成人声增强音轨…");
                    string srcCap = filePath;
                    int idxCap = fileIdx;
                    Interlocked.Increment(ref _activeBackgroundDecodeCount);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string scratch = Path.Combine(_library.DataDir, "audiobooks", _bookId, "pcm-cache");
                            string pcm = await Aligner.EnsurePcm16kWavAsync(srcCap, scratch, idxCap, _windowCts.Token);
                            if (_closed) return;
                            float gain = _library.Prefs.AudiobookLoudnessGain.TryGetValue(_bookId, out var g) ? g : VoiceTapDsp.MeasureLoudnessFromWav(pcm);
                            if (VoiceTapDsp.ProcessWavFile(pcm, dspPath, gain, clearer: true))
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    if (!_closed && _currentFileIndex == idxCap)
                                        _ = SeekGlobalAsync(_globalPosSec, _playing, forceReload: true);
                                });
                            }
                        }
                        catch { }
                        finally
                        {
                            if (Interlocked.Decrement(ref _activeBackgroundDecodeCount) == 0 && _closed)
                            {
                                try { _windowCts.Dispose(); } catch { }
                            }
                        }
                    });
                }
            }

            var sf = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(playPath));
            var ms = MediaSource.CreateFromStorageFile(sf);
            await ms.OpenAsync();
            if (_closed || reqVer != _seekRequestVersion)
            {
                ms.Dispose();
                return;
            }
            _player.Source = ms;
            _currentFileIndex = fileIdx;
            _loadedFileIsClearer = usedClearer;
        }

        _player.Volume = _isSleepFading ? _player.Volume : _baseVolume;
        _player.PlaybackSession.PlaybackRate = _rate;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(localSec);

        if (startPlaying)
        {
            bool wasPlaying = _playing;
            App.CurrentApp.ClaimAudioPlayback(this, () => DispatcherQueue.TryEnqueue(() => { if (!_closed) PausePlayback(); }));
            _player.Play();
            _playing = true;
            if (!wasPlaying)
                _library.Analytics.TrackAudioFeatureUsed("audiobook_play");
            _listenSinceUtc ??= DateTime.UtcNow;
            _sleepLastTickUtc = DateTime.UtcNow;
        }

        SyncChapterUi();
        UpdateTransportUi();
        SaveCurrentProgress();
    }

    private void OnFileEnded(MediaPlayer sender, object args)
    {
        if (_closed) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_closed || _recording is null) return;
            if (_autoClearPlayedStreamTracks)
            {
                try { _library.RemoteLibraries.MarkStreamTrackPassed(_bookId, _currentFileIndex); } catch { }
            }
            int nextIdx = _currentFileIndex + 1;
            if (nextIdx < _recording.Files.Length)
            {
                if (_sleepMode == -1 && _recording.Chapters.Length == _recording.Files.Length)
                {
                    double nextStart = nextIdx < _recording.Chapters.Length
                        ? _recording.Chapters[nextIdx].Start
                        : (nextIdx < _fileStartOffsets.Length ? _fileStartOffsets[nextIdx] : _globalPosSec);
                    FinishSleepStop("本集播完 · 已自动加书签", nextStart);
                    RenderChaptersAndBookmarks();
                    return;
                }
                double nextOffset = nextIdx < _fileStartOffsets.Length ? _fileStartOffsets[nextIdx] : _globalPosSec;
                await SeekGlobalAsync(nextOffset, startPlaying: true, explicitFileIndex: nextIdx);
                RenderChaptersAndBookmarks();
            }
            else
            {
                if (_sleepMode == -1)
                {
                    FinishSleepStop("本集播完 · 已自动加书签");
                    return;
                }
                _playing = false;
                UpdateTransportUi();
                SaveCurrentProgress();
                RenderChaptersAndBookmarks();
                SyncChapterUi();
            }
        });
    }

    private void OnUiTick(object? sender, object e)
    {
        if (_closed || _recording is null) return;
        int prevChap = CurrentChapterIndex();

        if (_playing && _activeUnreadRun is null && _currentFileIndex >= 0 && _currentFileIndex < _fileStartOffsets.Length)
        {
            double prevPos = _prevGlobalPosSec;
            _globalPosSec = _fileStartOffsets[_currentFileIndex] + _player.PlaybackSession.Position.TotalSeconds;
            _prevGlobalPosSec = _globalPosSec;

            if (_sleepMode == -2 && _sleepSentenceEndSec > 0 && _globalPosSec >= _sleepSentenceEndSec)
            {
                FinishSleepStop("已念完这句 · 已自动加书签");
                return;
            }

            if (_cachedSkipSettings.FillUnread && _unreadRuns.Length > 0)
            {
                for (int r = 0; r < _unreadRuns.Length; r++)
                {
                    var run = _unreadRuns[r];
                    if (prevPos < run.At && _globalPosSec >= run.At - 0.05)
                    {
                        StartUnreadFill(run);
                        return;
                    }
                }
            }

            try
            {
                var moment = _library.Core.AudiobookMoment(_bookId, _globalPosSec);
                if (moment.SkipTo is double skipTo && skipTo > _globalPosSec + 0.35)
                {
                    bool overrideActive = moment.Extra is not null && _globalPosSec < _ignoreExtraUntilSec;
                    if (!overrideActive && Math.Abs(skipTo - _lastMomentSkipTarget) > 0.35)
                    {
                        _lastMomentSkipTarget = skipTo;
                        double savedDelta = skipTo - _globalPosSec;
                        // 1:1 AudiobookPlayer.swift: count ONLY pause shortening (Extra == null) into AudiobookAddSaved
                        if (moment.Extra is null && savedDelta > 0.1 && savedDelta < 30.0)
                        {
                            try { _library.Core.AudiobookAddSaved(_bookId, savedDelta); } catch { }
                        }
                        else if (moment.Extra is { } ex)
                        {
                            if (ex.Place == ExtraPlace.Tail)
                            {
                                PausePlayback();
                                ShowToast("正文已播完，已跳过片尾");
                                return;
                            }
                            if (ex.Place == ExtraPlace.Head)
                            {
                                double headStart = ex.T0;
                                double headEnd = ex.T1;
                                ShowToast($"跳过了片头（{(int)Math.Round(savedDelta)} 秒）", "点这里听", () =>
                                {
                                    _ignoreExtraUntilSec = headEnd;
                                    _ = SeekGlobalAsync(headStart, startPlaying: true);
                                }, autoHideSeconds: 6);
                            }
                        }
                        _ = SeekGlobalAsync(skipTo, startPlaying: true);
                    }
                }
                else if (moment.Extra is { } curEx && curEx.Place == ExtraPlace.Middle && _globalPosSec >= _ignoreExtraUntilSec && ToastBanner.Visibility != Visibility.Visible)
                {
                    double skipOut = curEx.T1 + 0.05;
                    ShowToast("不在书里的话", "跳过", () => _ = SeekGlobalAsync(skipOut, startPlaying: true), autoHideSeconds: 5);
                }
            }
            catch { }
        }

        int curChap = CurrentChapterIndex();
        if (curChap != prevChap)
        {
            if (_sleepMode == -1)
            {
                double nextChapStart = curChap >= 0 && curChap < _recording.Chapters.Length
                    ? _recording.Chapters[curChap].Start
                    : _globalPosSec;
                FinishSleepStop("本集播完 · 已自动加书签", nextChapStart);
                RenderChaptersAndBookmarks();
                return;
            }
            RenderChaptersAndBookmarks();
        }

        // Wall-clock sleep countdown (#28: expires even while paused) + 20s linear volume fade
        if (_sleepMode > 0)
        {
            var now = DateTime.UtcNow;
            if (_sleepDeadlineUtc is DateTime deadline)
                _sleepRemainingSec = Math.Max(0, (deadline - now).TotalSeconds);
            else
                _sleepDeadlineUtc = now.AddSeconds(_sleepRemainingSec);
            _sleepLastTickUtc = now;

            if (_playing)
            {
                if (_sleepRemainingSec <= 20.0 && _sleepRemainingSec > 0)
                {
                    _isSleepFading = true;
                    _player.Volume = _baseVolume * Math.Clamp(_sleepRemainingSec / 20.0, 0.05, 1.0);
                }
                else if (_sleepRemainingSec <= 0)
                {
                    FinishSleepStop("定时到了 · 已渐弱暂停并加书签");
                    return;
                }
            }
            else if (_sleepRemainingSec <= 0)
            {
                _sleepMode = 0;
                _sleepRemainingSec = 0;
                _sleepDeadlineUtc = null;
                _isSleepFading = false;
                _sleepSentenceEndSec = -1;
                _sleepArmedSpot = null;
                _player.Volume = _baseVolume;
                SleepCountdownText.Text = "";
                ApplyAppearance();
                SyncChapterUi();
                return;
            }

            var remSpan = TimeSpan.FromSeconds(Math.Ceiling(_sleepRemainingSec));
            SleepCountdownText.Text = _isSleepFading
                ? L10n.T($"即将渐弱暂停 {remSpan.Seconds:D2}s（按空格延 10 分）")
                : L10n.T($"定时还剩 {(int)remSpan.TotalMinutes:D2}:{remSpan.Seconds:D2}");
        }

        SyncChapterUi();
    }

    private void FinishSleepStop(string toastMsg, double? seekAfterPauseSec = null)
    {
        var armed = _sleepArmedSpot ?? ((uint)CurrentChapterIndex(), (int)Math.Round((_globalPosSec - CurrentChapterBounds().Start) * 1000.0));
        PausePlayback();
        if (seekAfterPauseSec is double targetSec)
        {
            _ = SeekGlobalAsync(targetSec, startPlaying: false);
        }
        _sleepMode = 0;
        _sleepRemainingSec = 0;
        _sleepDeadlineUtc = null;
        _isSleepFading = false;
        _sleepSentenceEndSec = -1;
        _sleepArmedSpot = null;
        _player.Volume = _baseVolume;
        SleepCountdownText.Text = "";
        ReaderRecapAndSleepHelper.RememberSleepStop(_library, _bookId, armed.Chapter, armed.SecMs);
        ApplyAppearance();
        AddBookmarkAtCurrentPosition(silent: true);
        ShowToast(toastMsg);
    }

    private void ExtendSleepTenMin()
    {
        var now = DateTime.UtcNow;
        if (_sleepMode <= 0) _sleepMode = 10;
        _sleepRemainingSec = 600.0;
        _sleepDeadlineUtc = now.AddSeconds(600.0);
        _sleepLastTickUtc = now;
        _isSleepFading = false;
        _player.Volume = _baseVolume;
        ShowToast("已延长睡眠定时 10 分钟");
    }

    private void SyncChapterUi()
    {
        if (_recording is null) return;
        int curChap = CurrentChapterIndex();
        var (chStart, chDur) = CurrentChapterBounds();
        double inChap = Math.Clamp(_globalPosSec - chStart, 0, chDur);

        ChapterLabel.Text = ChapterTitle(curChap);
        if (!_isScrubbingSlider)
        {
            _suppressSlider = true;
            ChapterSlider.Maximum = chDur;
            ChapterSlider.Value = inChap;
            _suppressSlider = false;
            ChapterElapsedText.Text = FormatTime(inChap);
        }
        else
        {
            _suppressSlider = true;
            ChapterSlider.Maximum = chDur;
            _suppressSlider = false;
        }
        ChapterTotalText.Text = FormatTime(chDur);

        double pauseSavings = 0;
        if (_cachedSkipSettings.ShortenPauses)
        {
            try { pauseSavings = _library.Core.AudiobookPauseSavings(_bookId, _globalPosSec); } catch { }
        }
        double remBookSec = Math.Max(0, _recording.Seconds - _globalPosSec - pauseSavings) / Math.Max(0.5, _rate);
        string remPart = "";
        if (_recording.Chapters.Length >= 1 && remBookSec >= 60)
        {
            int hrs = (int)(remBookSec / 3600);
            int mins = (int)Math.Round((remBookSec % 3600) / 60.0);
            remPart = hrs > 0
                ? L10n.T($"全书还剩 {hrs} 小时 {mins} 分")
                : L10n.T($"全书还剩 {mins} 分钟");
        }

        int totalTracks = _recording.Files.Length;
        int haveTracks = 0;
        for (int i = 0; i < totalTracks; i++)
        {
            if (File.Exists(ResolveTrackPath(_recording.Files[i].Path)))
                haveTracks++;
        }
        if (haveTracks < totalTracks && !_isStreamedAudiobook)
        {
            _isStreamedAudiobook = true;
            SaveStreamPrefs();
        }

        if (_isStreamedAudiobook && totalTracks > 0)
        {
            string dlPart = L10n.T($"已下 {haveTracks} / {totalTracks} 集");
            BookRemainingText.Text = string.IsNullOrEmpty(remPart) ? dlPart : $"{remPart} · {dlPart}";
        }
        else
        {
            BookRemainingText.Text = remPart;
        }

        try
        {
            _player.SystemMediaTransportControls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                EndTime = TimeSpan.FromSeconds(chDur),
                MinSeekTime = TimeSpan.Zero,
                MaxSeekTime = TimeSpan.FromSeconds(chDur),
                Position = TimeSpan.FromSeconds(inChap),
            });
        }
        catch { }

        RenderBookmarkTicks();
    }

    private void RenderBookmarkTicks()
    {
        BookmarkTicksCanvas.Children.Clear();
        if (_recording is null || BookmarkTicksCanvas.ActualWidth <= 20) return;
        int curChap = CurrentChapterIndex();
        var (chStart, chDur) = CurrentChapterBounds();
        if (chDur <= 0) return;

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var bms = _library.GetBookmarks(_bookId).Where(b => (int)b.Section == curChap).ToArray();
        double w = BookmarkTicksCanvas.ActualWidth;
        double usableW = Math.Max(0, w - 18.0);

        foreach (var bm in bms)
        {
            double sec = bm.Sentence / 1000.0;
            double ratio = Math.Clamp(sec / chDur, 0.0, 1.0);
            double centerX = 9.0 + ratio * usableW;
            var tick = new Border
            {
                Width = 2.5,
                Height = 8,
                CornerRadius = new CornerRadius(1.25),
                Background = ThemeHelper.Brush(p.Bg),
                BorderBrush = ThemeHelper.Brush(p.T1),
                BorderThickness = new Thickness(0.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            var hitTarget = new Border
            {
                Width = 10,
                Height = 22,
                Background = new SolidColorBrush(Colors.Transparent),
                Child = tick,
            };
            ToolTipService.SetToolTip(hitTarget, $"{L10n.T("书签")} · {FormatTime(sec)}");
            double targetGlobal = chStart + sec;
            hitTarget.PointerPressed += (_, ev) => ev.Handled = true;
            hitTarget.Tapped += async (_, ev) =>
            {
                ev.Handled = true;
                await SeekGlobalAsync(targetGlobal, startPlaying: _playing);
            };
            Canvas.SetLeft(hitTarget, centerX - 5.0);
            Canvas.SetTop(hitTarget, 0);
            BookmarkTicksCanvas.Children.Add(hitTarget);
        }
    }

    private void UpdateTransportUi()
    {
        if (_closed) return;
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        PlayPauseBtn.Background = ThemeHelper.Brush(p.T1);
        PlayPauseBtn.Content = YYIcons.Make(_playing ? "pause" : "play", 20, ThemeHelper.Brush(p.Bg), filled: true);

        _library.SystemIntegration.SetAudioPlaying(this, _playing, _summary.Title, () => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_closed) PlayPause_Click(this, new RoutedEventArgs());
        }));

        try
        {
            var smtc = _player.SystemMediaTransportControls;
            smtc.PlaybackStatus = _playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            var u = smtc.DisplayUpdater;
            u.Type = MediaPlaybackType.Music;
            u.MusicProperties.Title = ChapterTitle(CurrentChapterIndex());
            u.MusicProperties.Artist = _summary.Title;
            if (!string.IsNullOrEmpty(_summary.CoverPath) && File.Exists(_summary.CoverPath) && u.Thumbnail is null)
            {
                string cp = _summary.CoverPath;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var file = await StorageFile.GetFileFromPathAsync(cp);
                        u.Thumbnail = Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file);
                        u.Update();
                    }
                    catch { }
                });
            }
            u.Update();
        }
        catch { }
    }

    private void PausePlayback()
    {
        if (_closed) return;
        bool wasPlaying = _playing;
        _playing = false;
        _isSleepFading = false;
        _player.Volume = _baseVolume;
        if (_activeUnreadRun is { } activeRun)
        {
            _activeUnreadRun = null;
            try { _fillTtsPlayer.Stop(); } catch { }
            _globalPosSec = Math.Max(0, activeRun.At - 0.3);
            _prevGlobalPosSec = _globalPosSec;
            if (_currentFileIndex >= 0 && _currentFileIndex < _fileStartOffsets.Length)
            {
                double localRewind = Math.Max(0, _globalPosSec - _fileStartOffsets[_currentFileIndex]);
                try { _player.PlaybackSession.Position = TimeSpan.FromSeconds(localRewind); } catch { }
            }
        }
        if (wasPlaying)
        {
            _pausedUtc = DateTime.UtcNow;
            FlushListeningTime();
        }
        try { _player.Pause(); } catch { }
        UpdateTransportUi();
        SyncChapterUi();
        SaveCurrentProgress();
    }

    private void SaveCurrentProgress()
    {
        if (_recording is null || _recording.Chapters.Length == 0) return;
        int ch = CurrentChapterIndex();
        uint ms = (uint)Math.Max(0, Math.Round((_globalPosSec - _recording.Chapters[ch].Start) * 1000.0));
        try { _library.SaveProgress(_bookId, (uint)ch, ms); } catch { }
    }

    // ---------- Right Column: ChapterColumn (Chapters + Bookmarks) & PairCard ----------

    private void RenderChaptersAndBookmarks()
    {
        ChapterListPanel.Children.Clear();
        if (_recording is null) return;

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        int active = CurrentChapterIndex();

        for (int i = 0; i < _recording.Chapters.Length; i++)
        {
            if (i > 0) ChapterListPanel.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
            int idx = i;
            var ch = _recording.Chapters[i];
            bool isCur = idx == active;
            bool isPast = idx < active;

            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = ChapterTitle(idx),
                FontSize = 12.5,
                FontWeight = isCur ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(isCur ? tint.Accent : (isPast ? p.T3 : p.T1)),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            int trackIdx = TrackIndexForChapter(idx);
            bool trackDownloaded = trackIdx < 0 || File.Exists(ResolveTrackPath(_recording.Files[trackIdx].Path));
            if (!trackDownloaded)
            {
                if (!_isStreamedAudiobook)
                {
                    _isStreamedAudiobook = true;
                    SaveStreamPrefs();
                }

                FrameworkElement shareIndicator;
                if (_downloadingChapterTracks.Contains(trackIdx) || _bufferingTrackIndex == trackIdx)
                {
                    shareIndicator = new ProgressRing
                    {
                        Width = 16,
                        Height = 16,
                        IsActive = true,
                        Foreground = ThemeHelper.Brush(tint.Accent),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                }
                else
                {
                    var ring = new Border
                    {
                        Width = 16,
                        Height = 16,
                        CornerRadius = new CornerRadius(8),
                        BorderBrush = ThemeHelper.Brush(isCur ? tint.Accent : p.T3),
                        BorderThickness = new Thickness(1.2),
                        Background = new SolidColorBrush(Colors.Transparent),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = YYIcons.Make("arrow-down", 8.5, ThemeHelper.Brush(isCur ? tint.Accent : p.T3)),
                    };
                    ToolTipService.SetToolTip(ring, L10n.T("下载这一集"));
                    int dlTrackCap = trackIdx;
                    ring.PointerPressed += (_, ev) => ev.Handled = true;
                    ring.Tapped += (_, ev) =>
                    {
                        ev.Handled = true;
                        _ = DownloadStreamTrackInBackgroundAsync(dlTrackCap);
                    };
                    shareIndicator = ring;
                }
                Grid.SetColumn(shareIndicator, 1);
                row.Children.Add(shareIndicator);
            }
            else
            {
                string timeStr = isCur
                    ? $"还剩 {FormatTime(Math.Max(0, ch.Start + ch.Seconds - _globalPosSec))}"
                    : FormatTime(ch.Seconds);
                var durTb = new TextBlock
                {
                    Text = timeStr,
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(isCur ? tint.Accent : p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(durTb, 1);
                row.Children.Add(durTb);
            }

            var ctx = new MenuFlyout();
            var renItem = new MenuFlyoutItem { Text = "改名…" };
            renItem.Click += async (_, _) => await RenameChapterAsync(idx);
            ctx.Items.Add(renItem);

            var itemBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = isCur ? ThemeHelper.Brush(tint.Soft) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(6, 1, 6, 1),
                Padding = new Thickness(8, 8, 8, 8),
                UseSystemFocusVisuals = false,
                ContextFlyout = ctx,
                Content = row,
            };
            AutomationProperties.SetName(itemBtn, ChapterTitle(idx));
            itemBtn.Click += async (_, _) =>
            {
                int? explicitTrack = _recording is not null && _recording.Chapters.Length == _recording.Files.Length ? idx : null;
                await SeekGlobalAsync(ch.Start, startPlaying: true, explicitFileIndex: explicitTrack);
                RenderChaptersAndBookmarks();
            };
            ChapterListPanel.Children.Add(itemBtn);
        }

        // Bookmarks inside the same ChapterColumn card (AudiobookScreen.swift lines 935-968)
        var bms = _library.GetBookmarks(_bookId);
        if (bms.Count > 0)
        {
            ChapterListPanel.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 6, 12, 4) });
            ChapterListPanel.Children.Add(new TextBlock
            {
                Text = "书签",
                FontSize = 10.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(14, 4, 14, 2),
            });

            foreach (var bm in bms)
            {
                int chIdx = Math.Clamp((int)bm.Section, 0, Math.Max(0, _recording.Chapters.Length - 1));
                double inChSec = bm.Sentence / 1000.0;
                double targetGlobal = (_recording.Chapters.Length > chIdx ? _recording.Chapters[chIdx].Start : 0) + inChSec;

                var bRow = new Grid { ColumnSpacing = 8 };
                bRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                bRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                bRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                bRow.Children.Add(YYIcons.Make("bookmark", 11.5, ThemeHelper.Brush(tint.Accent), filled: true));

                var titleTb = new TextBlock { Text = ChapterTitle(chIdx), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(titleTb, 1);
                bRow.Children.Add(titleTb);

                var atTb = new TextBlock { Text = FormatTime(inChSec), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(atTb, 2);
                bRow.Children.Add(atTb);

                var ctx = new MenuFlyout();
                string bmId = bm.Id;
                var delItem = new MenuFlyoutItem { Text = "删除书签", Foreground = ThemeHelper.Brush(p.Danger) };
                delItem.Click += (_, _) =>
                {
                    _library.DeleteBookmark(_bookId, bmId);
                    RenderChaptersAndBookmarks();
                    RenderBookmarkTicks();
                };
                ctx.Items.Add(delItem);

                var bmBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(7),
                    Margin = new Thickness(6, 1, 6, 1),
                    Padding = new Thickness(8, 6, 8, 6),
                    UseSystemFocusVisuals = false,
                    ContextFlyout = ctx,
                    Content = bRow,
                };
                AutomationProperties.SetName(bmBtn, $"{L10n.T("书签")} {ChapterTitle(chIdx)} {FormatTime(inChSec)}");
                bmBtn.Click += async (_, _) => await SeekGlobalAsync(targetGlobal, startPlaying: _playing);
                ChapterListPanel.Children.Add(bmBtn);
            }
        }
        L10n.ApplyTree(ChapterListPanel);
    }

    private void DismissSleepReturnCard(bool clearSaved)
    {
        if (clearSaved)
            ReaderRecapAndSleepHelper.ClearSleepReturn(_library, _bookId);
        _sleepReturnState = null;
        SleepReturnCardHost.Visibility = Visibility.Collapsed;
        SleepReturnCardHost.Child = null;
    }

    private void RenderInlineReturnCards()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        if (_elsewhereState is { } ew)
        {
            double totalSec = Math.Max(1.0, _recording?.Seconds ?? 1.0);
            int ewPct = (int)Math.Clamp(Math.Round((ew.RemoteFraction > 0 ? ew.RemoteFraction : ew.RemoteSec / totalSec) * 100.0), 0, 100);
            int herePct = (int)Math.Clamp(Math.Round(_globalPosSec / totalSec * 100.0), 0, 100);
            string devName = string.IsNullOrWhiteSpace(ew.DeviceName) ? L10n.T("另一台设备") : ew.DeviceName.Trim();

            var topRow = new Grid { ColumnSpacing = 6 };
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.Children.Add(YYIcons.Make("laptop", 12, ThemeHelper.Brush(p.T2)));
            var titleTb = new TextBlock
            {
                Text = $"{devName} {L10n.T("上听到了更后面")}",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(titleTb, 1);
            topRow.Children.Add(titleTb);

            var agoTb = new TextBlock
            {
                Text = FormatAgo(ew.RemoteAtMs),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(agoTb, 2);
            topRow.Children.Add(agoTb);

            var cardStack = new StackPanel { Spacing = 8 };
            cardStack.Children.Add(topRow);

            var bottomRow = new Grid { ColumnSpacing = 8 };
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            bottomRow.Children.Add(new TextBlock
            {
                Text = $"{ChapterTitle(ew.RemoteChap)} · {ewPct}% · 这台在 {herePct}%",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var stayBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("留在这里"),
                FontSize = 11,
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            stayBtn.Click += (_, _) =>
            {
                _elsewhereState = null;
                RenderInlineReturnCards();
            };
            Grid.SetColumn(stayBtn, 1);
            bottomRow.Children.Add(stayBtn);

            var jumpBtn = YYChrome.MakeSheetPillButton(L10n.T("跳过去"), () =>
            {
                double target = ew.RemoteSec;
                _elsewhereState = null;
                RenderInlineReturnCards();
                _ = SeekGlobalAsync(target, startPlaying: true);
            }, dark, _library.Prefs.Tint, "primary", height: 26, horizontalPadding: 12);
            Grid.SetColumn(jumpBtn, 2);
            bottomRow.Children.Add(jumpBtn);

            cardStack.Children.Add(bottomRow);

            ElsewhereCardHost.Child = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Padding = new Thickness(14, 11, 12, 10),
                Child = cardStack,
            };
            ElsewhereCardHost.Visibility = Visibility.Visible;
            L10n.ApplyTree(ElsewhereCardHost);
        }
        else
        {
            ElsewhereCardHost.Visibility = Visibility.Collapsed;
            ElsewhereCardHost.Child = null;
        }

        if (_elsewhereState is null && _sleepReturnState is { } sr)
        {
            var card = ReaderRecapAndSleepHelper.BuildSleepReturnCard(sr.Info, p, tint, go =>
            {
                double target = sr.ReturnSec;
                DismissSleepReturnCard(clearSaved: true);
                if (go)
                    _ = SeekGlobalAsync(target, startPlaying: true);
            });
            SleepReturnCardHost.Child = card;
            SleepReturnCardHost.Visibility = Visibility.Visible;
            L10n.ApplyTree(SleepReturnCardHost);
        }
        else
        {
            SleepReturnCardHost.Visibility = Visibility.Collapsed;
            SleepReturnCardHost.Child = null;
        }
    }

    private void UpdateKeyHints()
    {
        if (KeyHintsPanel is null) return;
        KeyHintsPanel.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        bool hasMap = HasPairedMap;

        StackPanel MakeHint(string[] keys, string desc)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            foreach (string k in keys)
                sp.Children.Add(YYChrome.MakeKeycap(k, dark));
            sp.Children.Add(new TextBlock
            {
                Text = L10n.T(desc),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            });
            return sp;
        }

        KeyHintsPanel.Children.Add(MakeHint([L10n.T("空格")], "暂停"));
        if (hasMap)
        {
            KeyHintsPanel.Children.Add(MakeHint(["←", "→"], "上一句 / 下一句"));
            KeyHintsPanel.Children.Add(MakeHint(["⇧←", "⇧→"], "15 秒"));
        }
        else
        {
            KeyHintsPanel.Children.Add(MakeHint(["←", "→"], "后退 15 秒 / 前进 30 秒"));
        }
        KeyHintsPanel.Children.Add(MakeHint(["Ctrl+D"], "加书签"));
    }

    private static string FormatAgo(long epochMs)
    {
        if (epochMs <= 0) return "";
        double mins = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - epochMs) / 60000.0);
        if (mins < 2) return L10n.T("刚刚");
        if (mins < 60) return L10n.T($"{(int)Math.Round(mins)} 分钟前");
        double hours = mins / 60.0;
        if (hours < 24) return L10n.T($"{(int)Math.Round(hours)} 小时前");
        return L10n.T($"{(int)Math.Round(hours / 24.0)} 天前");
    }

    private static string FormatChineseLength(double sec)
    {
        int mins = Math.Max(1, (int)Math.Round(sec / 60.0));
        return L10n.T(mins >= 60 ? $"{mins / 60} 小时 {mins % 60} 分" : $"{mins} 分钟");
    }

    private static string FormatBookFormatBadge(BookFormat format) => format switch
    {
        BookFormat.Epub => "EPUB",
        BookFormat.Txt => "TXT",
        BookFormat.Mobi => "Kindle",
        BookFormat.Pdf => "PDF",
        BookFormat.Audio => L10n.T("有声书"),
        BookFormat.Podcast => L10n.T("播客"),
        _ => "EPUB",
    };

    private void RenderPairCard()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        ListenPair? activePair = null;
        try { activePair = _library.Core.ListeningPairs().FirstOrDefault(lp => lp.Recording == _bookId); }
        catch { }

        var shelfEbooks = _library.GetShelfBooks().Where(b => b.Id != _bookId && b.Format != BookFormat.Audio).ToArray();
        BookSummary? pairedEbook = activePair is not null
            ? shelfEbooks.FirstOrDefault(b => b.Id == activePair.Book)
            : null;
        BookSummary? candidateEbook = pairedEbook is null
            ? AudiobookImporter.FindMatchingShelfEbook(_library, _summary.Title)
            : null;

        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (pairedEbook is not null)
        {
            var cov = new Border { Width = 24, Height = 34, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(p.Inset) };
            if (!string.IsNullOrEmpty(pairedEbook.CoverPath) && File.Exists(pairedEbook.CoverPath))
                cov.Child = new Image { Source = CreateCoverBitmap(pairedEbook.CoverPath, 48), Stretch = Stretch.UniformToFill };
            g.Children.Add(cov);

            AudiobookProgress? alignProg = null;
            try { alignProg = _library.Core.AudiobookProgress(_bookId); } catch { }
            var workStatus = _library.AudiobookWork.GetStatus(_bookId);
            bool workActive = _library.AudiobookWork.IsRunning(_bookId);
            bool alignDone = alignProg?.Done == true || activePair?.Made == true;

            string subLine;
            if (workActive && workStatus is not null)
                subLine = workStatus.Message;
            else if (alignDone)
                subLine = alignProg is not null && alignProg.Sentences > 0
                    ? $"已对齐 {alignProg.Exact}/{alignProg.Sentences} 句 · 可同步跟读"
                    : "已配对 · 可切到书页同步阅读";
            else if (workStatus?.Phase == AudiobookWorkPhase.Failed)
                subLine = $"对齐中断：{workStatus.Message}";
            else if (Aligner.IsInstalled(_library.DataDir))
                subLine = "已配对 · 点「合成进度」生成句级时间轴";
            else
                subLine = "已配对 · 可下载本地对齐模型合成听读书";

            var mid = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = $"《{pairedEbook.Title}》", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            mid.Children.Add(new TextBlock { Text = subLine, FontSize = 10, Foreground = ThemeHelper.Brush(workStatus?.Phase == AudiobookWorkPhase.Failed ? p.Danger : tint.Accent), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            var midBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = mid,
            };
            midBtn.Click += async (_, _) => await ShowPairStatusWizardAsync();
            Grid.SetColumn(midBtn, 1);
            g.Children.Add(midBtn);

            string eid = pairedEbook.Id;
            var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

            if (!alignDone)
            {
                var alignActionBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = workActive ? L10n.T("查看进度") : L10n.T("合成"),
                    Height = 26,
                    MinHeight = 26,
                    Padding = new Thickness(9, 0, 9, 0),
                    FontSize = 10.5,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                alignActionBtn.Click += async (_, _) =>
                {
                    if (!_library.AudiobookWork.IsRunning(_bookId))
                    {
                        _library.AudiobookWork.Start(_bookId, eid, _globalPosSec, autoDownloadModel: true);
                        RenderPairCard();
                    }
                    await ShowPairStatusWizardAsync();
                };
                rightBtns.Children.Add(alignActionBtn);
            }

            var openBtn = new Button
            {
                Content = L10n.T("切到书页"),
                Height = 26,
                Padding = new Thickness(11, 0, 11, 0),
                CornerRadius = new CornerRadius(13),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Background = ThemeHelper.Brush(p.T1),
                Foreground = ThemeHelper.Brush(p.Bg),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            openBtn.Click += (_, _) => OpenSyncedPairedEbook(eid);
            rightBtns.Children.Add(openBtn);

            Grid.SetColumn(rightBtns, 2);
            g.Children.Add(rightBtns);

            var ctx = new MenuFlyout();
            var statusMi = new MenuFlyoutItem { Text = L10n.T("查看合成进度与片头…") };
            statusMi.Click += async (_, _) => await ShowPairStatusWizardAsync();
            ctx.Items.Add(statusMi);

            var runAlignMi = new MenuFlyoutItem { Text = workActive ? L10n.T("停下合成") : L10n.T("重新合成") };
            runAlignMi.Click += (_, _) =>
            {
                if (_library.AudiobookWork.IsRunning(_bookId)) _library.AudiobookWork.Stop(_bookId);
                else _library.AudiobookWork.Start(_bookId, eid, _globalPosSec, autoDownloadModel: true);
                RenderPairCard();
            };
            ctx.Items.Add(runAlignMi);
            var pickOtherMi = new MenuFlyoutItem { Text = L10n.T("换一本电子书…") };
            pickOtherMi.Click += async (_, _) =>
            {
                var picked = await ShowEbookPairPickerAsync(eid);
                if (picked is not null)
                {
                    try { _library.Core.AudiobookPair(_bookId, picked.Id); } catch { }
                    _library.AudiobookWork.Start(_bookId, picked.Id, _globalPosSec, autoDownloadModel: true);
                    RefreshCachedMap();
                    RebuildRecordingSettings();
                    RenderPairCard();
                    UpdateKeyHints();
                    await ShowPairStatusWizardAsync();
                }
            };
            var unpairMi = new MenuFlyoutItem { Text = L10n.T("解除配对"), Foreground = ThemeHelper.Brush(p.Danger) };
            unpairMi.Click += (_, _) =>
            {
                _library.AudiobookWork.Stop(_bookId);
                try { _library.Core.AudiobookPair(_bookId, null); } catch { }
                RefreshCachedMap();
                RebuildRecordingSettings();
                RenderPairCard();
                UpdateKeyHints();
                ShowToast("已解除电子书配对");
            };
            ctx.Items.Add(pickOtherMi);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(unpairMi);
            PairCardHost.ContextFlyout = ctx;
        }
        else if (candidateEbook is not null)
        {
            // 1:1 AudiobookScreen.swift:168-186
            PairCardHost.ContextFlyout = null;
            var cov = new Border { Width = 24, Height = 34, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(p.Inset) };
            if (!string.IsNullOrEmpty(candidateEbook.CoverPath) && File.Exists(candidateEbook.CoverPath))
                cov.Child = new Image { Source = CreateCoverBitmap(candidateEbook.CoverPath, 48), Stretch = Stretch.UniformToFill };
            g.Children.Add(cov);

            var mid = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = $"书架上有《{candidateEbook.Title}》电子书", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            mid.Children.Add(new TextBlock { Text = L10n.T("合成以后听到哪句，书页就走到哪句"), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            var matched = candidateEbook;
            var pairBtn = new Button
            {
                Content = L10n.T("合成"),
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(13),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Background = ThemeHelper.Brush(p.T1),
                Foreground = ThemeHelper.Brush(p.Bg),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                UseSystemFocusVisuals = false,
            };
            pairBtn.Click += async (_, _) => await ShowAudiobookPairSheetAsync(matched);
            Grid.SetColumn(pairBtn, 2);
            g.Children.Add(pairBtn);
        }
        else
        {
            // 1:1 TextVersionCard (AudiobookScreen.swift:735-777): 这本录音 · 配上书架上的电子书 / 没有电子书 -> 转成文字
            PairCardHost.ContextFlyout = null;
            var tvStack = new StackPanel { Spacing = 0 };
            tvStack.Children.Add(new TextBlock
            {
                Text = L10n.T("这本录音"),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = ThemeHelper.Brush(p.T1),
                Margin = new Thickness(0, 0, 0, 4),
            });

            if (shelfEbooks.Length > 0)
            {
                tvStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
                var pickRow = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 7, 0, 7) };
                pickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                pickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var pickInfo = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                pickInfo.Children.Add(new TextBlock { Text = L10n.T("配上书架上的电子书"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1) });
                pickInfo.Children.Add(new TextBlock { Text = L10n.T("合成以后听到哪句，书页就走到哪句"), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
                pickRow.Children.Add(pickInfo);
                var pickBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = L10n.T("选书…"),
                    Height = 26,
                    MinHeight = 26,
                    Padding = new Thickness(10, 0, 10, 0),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                pickBtn.Click += async (_, _) => await ShowAudiobookPairSheetAsync(null);
                Grid.SetColumn(pickBtn, 1);
                pickRow.Children.Add(pickBtn);
                tvStack.Children.Add(pickRow);
            }

            tvStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            var trRow = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 7, 0, 2) };
            trRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            trRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var trInfo = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            trInfo.Children.Add(new TextBlock { Text = L10n.T("没有电子书"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1) });
            trInfo.Children.Add(new TextBlock { Text = L10n.T("把录音转成文字，能翻能搜能划线"), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
            trRow.Children.Add(trInfo);
            var transcribeBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = L10n.T("转成文字"),
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            transcribeBtn.Click += async (_, _) => await ShowMakeTextVersionDialogAsync();
            Grid.SetColumn(transcribeBtn, 1);
            trRow.Children.Add(transcribeBtn);
            tvStack.Children.Add(trRow);

            PairCardHost.Child = tvStack;
            L10n.ApplyTree(PairCardHost);
            return;
        }

        PairCardHost.Child = g;
        L10n.ApplyTree(PairCardHost);
    }

    private void OpenSyncedPairedEbook(string ebookId)
    {
        PausePlayback();
        uint? targetSec = null;
        int? targetSent = null;
        try
        {
            var moment = _library.Core.AudiobookMoment(_bookId, _globalPosSec);
            if (moment.Hit is SyncHit.Sentence sentHit)
            {
                targetSec = sentHit.Section;
                targetSent = (int)sentHit.SentenceValue;
            }
        }
        catch { }

        if (targetSec is null && _recording is not null && _recording.Seconds > 1)
        {
            try
            {
                var detail = _library.GetBookDetail(ebookId);
                if (detail.Sections.Length > 0)
                {
                    double ratio = Math.Clamp(_globalPosSec / _recording.Seconds, 0, 0.999);
                    uint sec = (uint)Math.Clamp((int)Math.Floor(ratio * detail.Sections.Length), 0, detail.Sections.Length - 1);
                    targetSec = sec;
                    targetSent = 0;
                }
            }
            catch { }
        }

        App.CurrentApp.OpenBook(ebookId, section: targetSec, sentence: targetSent);
    }

    /// <summary>
    /// 1:1 <c>AudiobookPair</c> (<c>AudiobookViews.swift:142-330</c>):
    /// 380px frameless warm-paper sheet with two lifted covers (<c>78×78</c> + link badge + <c>52×78</c>),
    /// local device card, auto-pair switch, <c>开始合成</c> primary button, and <c>换一本电子书</c>.
    /// </summary>
    private async Task ShowAudiobookPairSheetAsync(BookSummary? initialChosen)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        BookSummary? chosen = initialChosen ?? AudiobookImporter.FindMatchingShelfEbook(_library, _summary.Title);
        bool autoPairSameName = true;

        while (true)
        {
            string action = "dismiss";
            ContentDialog? dlgRef = null;
            double totalSec = Math.Max(60.0, _recording?.Seconds ?? 1800.0);

            var root = new StackPanel { Spacing = 14, Padding = new Thickness(18, 22, 18, 20) };

            // Top-right subtle close button + centered Duo covers (78x78 + link + 52x78)
            var topOverlay = new Grid();
            var closeTop = YYChrome.MakeSheetCloseButton(() => dlgRef?.Hide(), dark, size: 28);
            closeTop.HorizontalAlignment = HorizontalAlignment.Right;
            closeTop.VerticalAlignment = VerticalAlignment.Top;

            var duoRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            };

            var leftCoverBox = new Border
            {
                Width = 78,
                Height = 78,
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(p.Inset),
                BorderBrush = ThemeHelper.Brush(Color.FromArgb(32, 0, 0, 0)),
                BorderThickness = new Thickness(0.5),
            };
            if (!string.IsNullOrEmpty(_summary.CoverPath) && File.Exists(_summary.CoverPath))
                leftCoverBox.Child = new Image { Source = CreateCoverBitmap(_summary.CoverPath, 156), Stretch = Stretch.UniformToFill };
            else
                leftCoverBox.Child = YYIcons.Make("headphones", 22, ThemeHelper.Brush(p.T3));

            var leftCol = new StackPanel { Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center };
            leftCol.Children.Add(leftCoverBox);
            leftCol.Children.Add(new TextBlock
            {
                Text = $"有声书 · {FormatChineseLength(totalSec)}",
                FontSize = 10,
                Foreground = ThemeHelper.Brush(p.T3),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            duoRow.Children.Add(leftCol);

            duoRow.Children.Add(new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 25, 0, 0),
                Child = YYIcons.Make("link-chain", 13, ThemeHelper.Brush(p.T2)),
            });

            var rightCoverBox = new Border
            {
                Width = 52,
                Height = 78,
                CornerRadius = new CornerRadius(4),
                Background = ThemeHelper.Brush(chosen is not null ? p.Inset : Colors.Transparent),
                BorderBrush = ThemeHelper.Brush(chosen is not null ? Color.FromArgb(32, 0, 0, 0) : p.T3),
                BorderThickness = new Thickness(chosen is not null ? 0.5 : 1),
            };
            if (chosen is not null && !string.IsNullOrEmpty(chosen.CoverPath) && File.Exists(chosen.CoverPath))
                rightCoverBox.Child = new Image { Source = CreateCoverBitmap(chosen.CoverPath, 104), Stretch = Stretch.UniformToFill };
            else if (chosen is not null)
                rightCoverBox.Child = YYIcons.Make("book", 18, ThemeHelper.Brush(p.T3));
            else
                rightCoverBox.Child = new TextBlock { Text = "?", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            var rightCol = new StackPanel { Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center };
            rightCol.Children.Add(rightCoverBox);
            rightCol.Children.Add(new TextBlock
            {
                Text = chosen is not null ? $"电子书 · {FormatBookFormatBadge(chosen.Format)}" : L10n.T("电子书 · 还没有"),
                FontSize = 10,
                Foreground = ThemeHelper.Brush(p.T3),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            duoRow.Children.Add(rightCol);

            topOverlay.Children.Add(duoRow);
            topOverlay.Children.Add(closeTop);
            root.Children.Add(topOverlay);

            // Headline
            var headline = new StackPanel { Spacing = 5, Padding = new Thickness(8, 0, 8, 0) };
            headline.Children.Add(new TextBlock
            {
                Text = chosen is not null ? $"书架上有同一本《{chosen.Title}》" : $"书架上没找到《{_summary.Title}》",
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = ThemeHelper.Brush(p.T1),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            headline.Children.Add(new TextBlock
            {
                Text = chosen is null
                    ? L10n.T("有声书要配上同一本电子书才能听：听到哪句，书页上就走到哪句。先导入电子书再来，或者从书架上选一本。")
                    : L10n.T("合成一本听读书：听到哪句，书页上就走到哪句，点一句就从那句听。全部在你的设备上做，不上传。"),
                FontSize = 11,
                LineHeight = 18,
                Foreground = ThemeHelper.Brush(p.T2),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            root.Children.Add(headline);

            if (chosen is not null)
            {
                var placeGroup = new StackPanel { Spacing = 6 };
                placeGroup.Children.Add(new TextBlock
                {
                    Text = L10n.T("在哪里处理"),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(4, 0, 4, 0),
                });

                var placeGrid = new Grid();
                var placeLeft = new StackPanel { Spacing = 3 };
                placeLeft.Children.Add(new Border
                {
                    Width = 28,
                    Height = 28,
                    CornerRadius = new CornerRadius(8),
                    Background = ThemeHelper.Brush(p.Inset),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 4),
                    Child = YYIcons.Make("laptop", 13.5, ThemeHelper.Brush(p.T1)),
                });

                var placeTitleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                placeTitleRow.Children.Add(new TextBlock { Text = L10n.T("这台电脑"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                placeTitleRow.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Background = ThemeHelper.Brush(tint.Soft),
                    Padding = new Thickness(6, 1, 6, 1),
                    Child = new TextBlock { Text = L10n.T("推荐"), FontSize = 9.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) },
                });
                placeLeft.Children.Add(placeTitleRow);
                placeLeft.Children.Add(new TextBlock
                {
                    Text = $"约 {FormatChineseLength(Math.Max(60.0, totalSec / 40.0))}",
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T1),
                });
                placeLeft.Children.Add(new TextBlock
                {
                    Text = L10n.T("可以去做别的，好了会通知你"),
                    FontSize = 10,
                    Foreground = ThemeHelper.Brush(p.T3),
                });
                placeGrid.Children.Add(placeLeft);

                var checkBadge = new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(tint.Fill),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = YYIcons.Make("checkmark", 9.5, ThemeHelper.Brush(tint.OnBlack)),
                };
                placeGrid.Children.Add(checkBadge);

                placeGroup.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(14),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.T1),
                    BorderThickness = new Thickness(1.5),
                    Padding = new Thickness(12, 11, 12, 10),
                    Child = placeGrid,
                });
                root.Children.Add(placeGroup);

                var autoRow = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 8, 12, 8) };
                autoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                autoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                autoRow.Children.Add(new TextBlock
                {
                    Text = L10n.T("以后同名的书都这样配"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T1),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var autoSw = new ToggleSwitch { IsOn = autoPairSameName, MinWidth = 0, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
                autoSw.Toggled += (_, _) => autoPairSameName = autoSw.IsOn;
                Grid.SetColumn(autoSw, 1);
                autoRow.Children.Add(autoSw);

                root.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Child = autoRow,
                });

                var btns = new StackPanel { Spacing = 4 };
                btns.Children.Add(YYChrome.MakeSheetPillButton(L10n.T("开始合成"), () =>
                {
                    action = "start";
                    dlgRef?.Hide();
                }, dark, _library.Prefs.Tint, "primary", height: 38, stretch: true));

                var switchBookBtn = new Button
                {
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Height = 30,
                    FontSize = 11.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = ThemeHelper.Brush(p.T2),
                    UseSystemFocusVisuals = false,
                    Content = L10n.T("换一本电子书"),
                };
                switchBookBtn.Click += (_, _) =>
                {
                    action = "pick";
                    dlgRef?.Hide();
                };
                btns.Children.Add(switchBookBtn);
                root.Children.Add(btns);
            }
            else
            {
                var btns = new StackPanel { Spacing = 4 };
                btns.Children.Add(YYChrome.MakeSheetPillButton(L10n.T("从书架上选一本"), () =>
                {
                    action = "pick";
                    dlgRef?.Hide();
                }, dark, _library.Prefs.Tint, "primary", height: 38, stretch: true));

                var cancelBtn = new Button
                {
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Height = 30,
                    FontSize = 11.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = ThemeHelper.Brush(p.T2),
                    UseSystemFocusVisuals = false,
                    Content = L10n.T("先不配对"),
                };
                cancelBtn.Click += (_, _) => dlgRef?.Hide();
                btns.Children.Add(cancelBtn);
                root.Children.Add(btns);
            }

            dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, dark, 380, root, maxHeight: 640, tint: _library.Prefs.Tint);
            await dlgRef.ShowSafeAsync(_library.Prefs.Tint);

            if (action == "pick")
            {
                var picked = await ShowEbookPairPickerAsync(chosen?.Id, commitImmediately: false);
                if (picked is not null)
                    chosen = picked;
                else if (chosen is null)
                    return;
                continue;
            }

            if (action == "start" && chosen is not null)
            {
                try { _library.Core.AudiobookPair(_bookId, chosen.Id); } catch { }
                _library.AudiobookWork.Start(_bookId, chosen.Id, _globalPosSec, autoDownloadModel: true);
                RefreshCachedMap();
                RebuildRecordingSettings();
                RenderPairCard();
                UpdateKeyHints();
                await ShowPairStatusWizardAsync();
            }
            return;
        }
    }

    /// <summary>
    /// 1:1 <c>AudiobookBookPicker</c> (<c>AudiobookViews.swift:333-398</c>):
    /// 360px frameless warm-paper sheet listing shelf ebooks with cover &amp; checkmark.
    /// </summary>
    private async Task<BookSummary?> ShowEbookPairPickerAsync(string? currentChosenId = null, bool commitImmediately = true)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var ebooks = _library.GetShelfBooks()
            .Where(b => b.Id != _bookId && b.Format != BookFormat.Audio && b.Format != BookFormat.Pdf && b.Format != BookFormat.Podcast)
            .ToArray();

        ContentDialog? dlgRef = null;
        BookSummary? selected = null;

        var searchRow = new Grid
        {
            Height = 34,
            Padding = new Thickness(10, 0, 10, 0),
            ColumnSpacing = 6,
            Background = ThemeHelper.Brush(p.Inset),
            CornerRadius = new CornerRadius(10),
        };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.Children.Add(YYIcons.Make("search", 12, ThemeHelper.Brush(p.T3)));

        var searchBox = new TextBox
        {
            PlaceholderText = L10n.T("书名或作者"),
            FontSize = 12,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
        };
        YYChrome.StripTextBoxChrome(searchBox);
        Grid.SetColumn(searchBox, 1);
        searchRow.Children.Add(searchBox);

        var list = new StackPanel { Spacing = 0 };

        void Rebuild(string q)
        {
            list.Children.Clear();
            var filtered = ebooks.Where(b =>
                string.IsNullOrWhiteSpace(q) ||
                b.Title.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase) ||
                b.Authors.Any(a => a.Contains(q.Trim(), StringComparison.OrdinalIgnoreCase))).ToArray();

            if (filtered.Length == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = ebooks.Length == 0
                        ? L10n.T("书架上还没有可配对的文字电子书（EPUB / TXT / MOBI / AZW3）。先将电子书拖入书架再来配对即可。")
                        : L10n.T("没有匹配的电子书。"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T2),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12, 14, 12, 14),
                });
                return;
            }

            for (int i = 0; i < filtered.Length; i++)
            {
                if (i > 0)
                    list.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });

                var eb = filtered[i];
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var cov = new Border { Width = 24, Height = 34, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(p.Inset) };
                if (!string.IsNullOrEmpty(eb.CoverPath) && File.Exists(eb.CoverPath))
                    cov.Child = new Image { Source = CreateCoverBitmap(eb.CoverPath, 48), Stretch = Stretch.UniformToFill };
                row.Children.Add(cov);

                var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                meta.Children.Add(new TextBlock { Text = eb.Title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                if (eb.Authors.Length > 0)
                    meta.Children.Add(new TextBlock { Text = string.Join("、", eb.Authors), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(meta, 1);
                row.Children.Add(meta);

                if (eb.Id == currentChosenId)
                {
                    var ck = YYIcons.Make("checkmark", 12, ThemeHelper.Brush(tint.Accent));
                    Grid.SetColumn(ck, 2);
                    row.Children.Add(ck);
                }

                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(12, 8, 12, 8),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = row,
                };
                var pickedBook = eb;
                btn.Click += (_, _) =>
                {
                    selected = pickedBook;
                    if (commitImmediately)
                    {
                        try { _library.Core.AudiobookPair(_bookId, pickedBook.Id); } catch { }
                        _library.AudiobookWork.Start(_bookId, pickedBook.Id, _globalPosSec, autoDownloadModel: true);
                        RenderPairCard();
                    }
                    dlgRef?.Hide();
                };
                list.Children.Add(btn);
            }
        }

        searchBox.TextChanged += (_, _) => Rebuild(searchBox.Text);
        Rebuild("");

        var root = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(18, 16, 18, 18),
        };
        root.Children.Add(YYChrome.MakeSheetHeader(L10n.T("选一本电子书"), L10n.T("听的时候书页跟着走"), () => dlgRef?.Hide(), dark, titleSize: 16));
        root.Children.Add(searchRow);
        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Child = list,
            },
        });

        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, dark, 360, root, maxHeight: 480, tint: _library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
        return selected;
    }

    private async Task ShowMakeTextVersionDialogAsync()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        using var cts = new CancellationTokenSource();
        ContentDialog? dlgRef = null;

        var statusTb = new TextBlock
        {
            Text = L10n.T("没有电子书时，可将录音逐段转写为可划线、可搜索的「文字版」，并自动与这本有声书配对。优先使用本地 SenseVoice / Whisper 或已配置的语音转写接口。"),
            FontSize = 12,
            LineHeight = 19,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
        };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
        var detailTb = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap };
        BookSummary? created = null;

        Button? startBtn = null;
        startBtn = YYChrome.MakeSheetPillButton(L10n.T("开始转写生成文字版"), async () =>
        {
            if (startBtn is not null) startBtn.IsEnabled = false;
            detailTb.Text = L10n.T("正在准备转写…");
            try
            {
                created = await Task.Run(async () => await WindowsTranscriber.MakeTextVersionAsync(
                    _library,
                    _bookId,
                    (frac, msg) => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_closed) return;
                        bar.Value = Math.Clamp(frac * 100, 0, 100);
                        detailTb.Text = msg;
                    }),
                    cts.Token));
                if (created is not null)
                {
                    RefreshCachedMap();
                    RebuildRecordingSettings();
                    RenderPairCard();
                    UpdateKeyHints();
                    dlgRef?.Hide();
                    ShowToast($"已生成文字版《{created.Title}》", "打开阅读", () => OpenSyncedPairedEbook(created.Id));
                }
            }
            catch (OperationCanceledException)
            {
                detailTb.Text = L10n.T("已取消转写");
            }
            catch (Exception ex)
            {
                detailTb.Text = ex.Message;
                if (startBtn is not null) startBtn.IsEnabled = true;
            }
        }, dark, _library.Prefs.Tint, "primary", height: 38, stretch: true);

        var cardInner = new StackPanel { Spacing = 10, Padding = new Thickness(14, 12, 14, 12), Children = { statusTb, bar, detailTb } };
        var body = new StackPanel { Spacing = 14, Padding = new Thickness(20, 18, 20, 18) };
        body.Children.Add(YYChrome.MakeSheetHeader(L10n.T("转成文字版"), _summary.Title, () => dlgRef?.Hide(), dark, titleSize: 17));
        body.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Child = cardInner,
        });
        body.Children.Add(startBtn);

        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, dark, 400, body, tint: _library.Prefs.Tint);
        try
        {
            await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
        }
        finally
        {
            cts.Cancel();
        }
    }

    private void StopIntroPreview()
    {
        _introPreviewStopTimer?.Stop();
        try { _introPreviewPlayer?.Pause(); _introPreviewPlayer?.Dispose(); } catch { }
        _introPreviewPlayer = null;
    }

    private void PreviewIntroClip(double fromSec, double toSec)
    {
        if (_recording is null || _recording.Files.Length == 0) return;
        PausePlayback();
        StopIntroPreview();

        // Claim audio focus BEFORE starting intro preview!
        App.CurrentApp.ClaimAudioPlayback(this, () => DispatcherQueue.TryEnqueue(() => { if (!_closed) StopIntroPreview(); }));

        string firstPath = ResolveTrackPath(_recording.Files[0].Path);
        if (!File.Exists(firstPath)) return;

        double clipEnd = Math.Max(fromSec + 1.0, Math.Min(toSec, fromSec + 30.0));
        var mp = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Speech, AutoPlay = false };
        mp.Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(firstPath)));
        mp.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, fromSec));
        mp.Play();
        _introPreviewPlayer = mp;

        _introPreviewStopTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _introPreviewStopTimer.Stop();
        _introPreviewStopTimer.Tick -= OnIntroPreviewTick;
        void OnIntroPreviewTick(object? s, object e)
        {
            try
            {
                if (_introPreviewPlayer is null || _introPreviewPlayer.PlaybackSession.Position.TotalSeconds >= clipEnd)
                {
                    _introPreviewStopTimer?.Stop();
                    StopIntroPreview();
                }
            }
            catch { StopIntroPreview(); }
        }
        _introPreviewStopTimer.Tick += OnIntroPreviewTick;
        _introPreviewStopTimer.Start();
    }

    /// <summary>
    /// 1:1 <c>AudiobookPage</c> (<c>AudiobookViews.swift:414-892</c>):
    /// 420×640 frameless warm-paper sheet with <c>58pt</c> giant percentage, 4-step checklist,
    /// 12-column chapter completion matrix, <c>先听对好的 N 章</c>, and <c>合成好了</c> result view.
    /// </summary>
    private async Task ShowPairStatusWizardAsync()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        while (true)
        {
            string pendingAction = "";
            ContentDialog? dlgRef = null;
            var hostGrid = new Grid { MinHeight = 560 };

            void RebuildSheetView()
            {
                hostGrid.Children.Clear();

                ListenPair? pair = null;
                try { pair = _library.Core.ListeningPairs().FirstOrDefault(lp => lp.Recording == _bookId); } catch { }
                BookSummary? pairedBook = pair is not null
                    ? _library.GetBooks().FirstOrDefault(b => b.Id == pair.Book)
                    : null;

                AudiobookProgress prog;
                try { prog = _library.Core.AudiobookProgress(_bookId); }
                catch { prog = new AudiobookProgress(0, 0, pair?.Book, null, 0, 0, 0, 0, false); }

                var map = RefreshCachedMap() ? _cachedMap : null;
                var workStatus = _library.AudiobookWork.GetStatus(_bookId);
                bool running = _library.AudiobookWork.IsRunning(_bookId);
                bool isFinished = !running && (prog.Done || pair?.Made == true) && map is not null;

                // Top nav bar: 34x34 round close/back button | Title + Subtitle | 34x34 ⋯ menu button
                Grid BuildNavBar(string iconName, string? navTitle, string? navSub)
                {
                    var nav = new Grid { Padding = new Thickness(14, 14, 14, 6), MinHeight = 44 };
                    nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                    nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    nav.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

                    var closeRound = new Button
                    {
                        Width = 34,
                        Height = 34,
                        Padding = new Thickness(0),
                        CornerRadius = new CornerRadius(17),
                        Background = ThemeHelper.Brush(p.Card),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.5),
                        UseSystemFocusVisuals = false,
                        Content = YYIcons.Make(iconName, 12, ThemeHelper.Brush(p.T1)),
                    };
                    closeRound.Click += (_, _) => dlgRef?.Hide();
                    nav.Children.Add(closeRound);

                    if (!string.IsNullOrEmpty(navTitle) || !string.IsNullOrEmpty(navSub))
                    {
                        var mid = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                        if (!string.IsNullOrEmpty(navTitle))
                            mid.Children.Add(new TextBlock { Text = navTitle, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), HorizontalAlignment = HorizontalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                        if (!string.IsNullOrEmpty(navSub))
                            mid.Children.Add(new TextBlock { Text = navSub, FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                        Grid.SetColumn(mid, 1);
                        nav.Children.Add(mid);
                    }

                    var moreMenu = new MenuFlyout();
                    if (running)
                    {
                        var stopMi = new MenuFlyoutItem { Text = L10n.T("停下合成") };
                        stopMi.Click += (_, _) =>
                        {
                            _library.AudiobookWork.Stop(_bookId);
                            RebuildSheetView();
                        };
                        moreMenu.Items.Add(stopMi);
                    }
                    else if (pairedBook is not null)
                    {
                        var redoMi = new MenuFlyoutItem { Text = L10n.T("重新合成") };
                        redoMi.Click += (_, _) =>
                        {
                            _library.AudiobookWork.Start(_bookId, pairedBook.Id, _globalPosSec, autoDownloadModel: true);
                            RebuildSheetView();
                        };
                        moreMenu.Items.Add(redoMi);
                    }

                    if (map is not null && map.Rows.Length > 0)
                    {
                        var checkMi = new MenuFlyoutItem { Text = L10n.T("对齐检查与微调…") };
                        checkMi.Click += (_, _) =>
                        {
                            pendingAction = "align-check";
                            dlgRef?.Hide();
                        };
                        moreMenu.Items.Add(checkMi);
                    }

                    var removeMi = new MenuFlyoutItem { Text = L10n.T("删掉合成记录…"), Foreground = ThemeHelper.Brush(p.Danger) };
                    removeMi.Click += (_, _) =>
                    {
                        pendingAction = "confirm-unpair";
                        dlgRef?.Hide();
                    };
                    moreMenu.Items.Add(removeMi);

                    var moreBtn = new Button
                    {
                        Width = 34,
                        Height = 34,
                        Padding = new Thickness(0),
                        CornerRadius = new CornerRadius(17),
                        Background = ThemeHelper.Brush(p.Card),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.5),
                        UseSystemFocusVisuals = false,
                        Flyout = moreMenu,
                        Content = new TextBlock { Text = "⋯", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) },
                    };
                    Grid.SetColumn(moreBtn, 2);
                    nav.Children.Add(moreBtn);
                    return nav;
                }

                if (isFinished && map is not null)
                {
                    // ---------- 合成好了 (AudiobookViews.swift:692-809) ----------
                    var extras = Array.Empty<ExtraSpan>();
                    try { extras = _library.Core.AudiobookExtras(_bookId); } catch { }
                    SkipSettings skip;
                    try { skip = _library.Core.AudiobookSkipSettings(_bookId); }
                    catch { skip = new SkipSettings(true, false, false, false, false, false, false); }

                    var headExtra = extras.FirstOrDefault(x => x.Place == ExtraPlace.Head && (x.T1 - x.T0) >= 15.0);
                    int missingCount = map.Rows.Count(r => r.By == SyncBy.Unread);
                    int chapCount = Math.Max(1, map.Chapters.Length > 0 ? map.Chapters.Length : (_recording?.Chapters.Length ?? 1));

                    var outer = new Grid();
                    outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    outer.Children.Add(BuildNavBar("xmark", L10n.T("合成好了"), pairedBook?.Title ?? _summary.Title));

                    var contentStack = new StackPanel { Spacing = 14, Padding = new Thickness(18, 12, 18, 20) };

                    // Lifted 58x87 cover + 24x24 dark headphones badge
                    var heroStack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
                    var coverWrap = new Grid { Width = 68, Height = 94, HorizontalAlignment = HorizontalAlignment.Center };
                    var bookCov = new Border
                    {
                        Width = 58,
                        Height = 87,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        CornerRadius = new CornerRadius(4),
                        Background = ThemeHelper.Brush(p.Inset),
                        BorderBrush = ThemeHelper.Brush(Color.FromArgb(32, 0, 0, 0)),
                        BorderThickness = new Thickness(0.5),
                    };
                    string? covPath = pairedBook?.CoverPath ?? _summary.CoverPath;
                    if (!string.IsNullOrEmpty(covPath) && File.Exists(covPath))
                        bookCov.Child = new Image { Source = CreateCoverBitmap(covPath, 116), Stretch = Stretch.UniformToFill };
                    else
                        bookCov.Child = YYIcons.Make("book", 20, ThemeHelper.Brush(p.T3));
                    coverWrap.Children.Add(bookCov);

                    coverWrap.Children.Add(new Border
                    {
                        Width = 24,
                        Height = 24,
                        CornerRadius = new CornerRadius(12),
                        Background = ThemeHelper.Brush(tint.Black),
                        BorderBrush = ThemeHelper.Brush(p.Bg),
                        BorderThickness = new Thickness(2.5),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Child = YYIcons.Make("headphones", 11, ThemeHelper.Brush(tint.OnBlack)),
                    });
                    heroStack.Children.Add(coverWrap);

                    heroStack.Children.Add(new TextBlock
                    {
                        Text = $"{chapCount} 章都准备好了",
                        FontSize = 17,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = ThemeHelper.Brush(p.T1),
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(0, 6, 0, 0),
                    });
                    heroStack.Children.Add(new TextBlock
                    {
                        Text = L10n.T("听到哪句，书页就走到哪句。若偶有错位，选中正在念的句子点「对准」即可。"),
                        FontSize = 11,
                        LineHeight = 17.5,
                        Foreground = ThemeHelper.Brush(p.T2),
                        TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 340,
                    });
                    contentStack.Children.Add(heroStack);

                    // Intro card (> 15s)
                    if (headExtra is not null && !skip.HeadTail)
                    {
                        double dur = headExtra.T1 - headExtra.T0;
                        string introTitle = dur < 60 ? $"片头多出 {(int)Math.Round(dur)} 秒" : $"片头多出 {(int)Math.Round(dur / 60.0)} 分钟";
                        var introCardStack = new StackPanel { Spacing = 6 };
                        var iHead = new Grid { ColumnSpacing = 7 };
                        iHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        iHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        iHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        iHead.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(Color.FromArgb(255, 217, 148, 38)), VerticalAlignment = VerticalAlignment.Center });
                        var itTb = new TextBlock { Text = introTitle, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
                        Grid.SetColumn(itTb, 1);
                        iHead.Children.Add(itTb);
                        var ch1Tb = new TextBlock { Text = L10n.T("第 1 章开头"), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
                        Grid.SetColumn(ch1Tb, 2);
                        iHead.Children.Add(ch1Tb);
                        introCardStack.Children.Add(iHead);

                        introCardStack.Children.Add(new TextBlock
                        {
                            Text = L10n.T("录音开头是出品方的介绍，书里没有。听的时候从正文开始。"),
                            FontSize = 10.5,
                            LineHeight = 16.5,
                            Foreground = ThemeHelper.Brush(p.T2),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(13, 0, 0, 0),
                        });

                        var iActions = new Grid { Margin = new Thickness(13, 4, 0, 0) };
                        var tryBtn = YYChrome.MakeSheetPillButton(L10n.T("▶ 试听"), () =>
                        {
                            PreviewIntroClip(headExtra.T0, headExtra.T1);
                        }, dark, _library.Prefs.Tint, "secondary", height: 28, horizontalPadding: 12);
                        tryBtn.HorizontalAlignment = HorizontalAlignment.Left;
                        iActions.Children.Add(tryBtn);

                        var skipBtn = YYChrome.MakeSheetPillButton(L10n.T("跳过这段"), () =>
                        {
                            StopIntroPreview();
                            try { _library.Core.AudiobookSetSkipSettings(_bookId, skip with { HeadTail = true }); } catch { }
                            RebuildRecordingSettings();
                            RebuildSheetView();
                        }, dark, _library.Prefs.Tint, "warm", height: 28, horizontalPadding: 14);
                        skipBtn.HorizontalAlignment = HorizontalAlignment.Right;
                        iActions.Children.Add(skipBtn);
                        introCardStack.Children.Add(iActions);

                        contentStack.Children.Add(new Border
                        {
                            CornerRadius = new CornerRadius(12),
                            Background = ThemeHelper.Brush(p.Card),
                            BorderBrush = ThemeHelper.Brush(p.Line),
                            BorderThickness = new Thickness(0.75),
                            Padding = new Thickness(12, 11, 12, 10),
                            Child = introCardStack,
                        });
                    }

                    // Missing sentences card
                    if (missingCount > 0)
                    {
                        var missStack = new StackPanel { Spacing = 5 };
                        var mHead = new Grid { ColumnSpacing = 7 };
                        mHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        mHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        mHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        mHead.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(p.Ok), VerticalAlignment = VerticalAlignment.Center });
                        var mTb = new TextBlock { Text = $"录音里跳过了 {missingCount} 句", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
                        Grid.SetColumn(mTb, 1);
                        mHead.Children.Add(mTb);
                        var okBadge = new Border
                        {
                            CornerRadius = new CornerRadius(11),
                            Background = ThemeHelper.Brush(p.Inset),
                            Padding = new Thickness(8, 2, 8, 2),
                            Child = new TextBlock { Text = $"✓ {L10n.T("已用朗读补上")}", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.Ok) },
                        };
                        Grid.SetColumn(okBadge, 2);
                        mHead.Children.Add(okBadge);
                        missStack.Children.Add(mHead);

                        missStack.Children.Add(new TextBlock
                        {
                            Text = L10n.T("朗读的人跳过了书里部分内容，听书时会自动用朗读声音补上。"),
                            FontSize = 10.5,
                            LineHeight = 16.5,
                            Foreground = ThemeHelper.Brush(p.T2),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(13, 0, 0, 0),
                        });

                        contentStack.Children.Add(new Border
                        {
                            CornerRadius = new CornerRadius(12),
                            Background = ThemeHelper.Brush(p.Card),
                            BorderBrush = ThemeHelper.Brush(p.Line),
                            BorderThickness = new Thickness(0.75),
                            Padding = new Thickness(12, 11, 12, 10),
                            Child = missStack,
                        });
                    }

                    var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = contentStack };
                    Grid.SetRow(scroll, 1);
                    outer.Children.Add(scroll);

                    var footWrap = new Border { Padding = new Thickness(16, 10, 16, 18) };
                    var startListenBtn = YYChrome.MakeSheetPillButton(L10n.T("开始听"), () =>
                    {
                        pendingAction = "listen";
                        dlgRef?.Hide();
                    }, dark, _library.Prefs.Tint, "primary", height: 40, stretch: true);
                    startListenBtn.IsEnabled = pairedBook is not null;
                    footWrap.Child = startListenBtn;
                    Grid.SetRow(footWrap, 2);
                    outer.Children.Add(footWrap);

                    hostGrid.Children.Add(outer);
                    return;
                }

                // ---------- 合成中 (AudiobookViews.swift:497-688) ----------
                double totalRecSec = Math.Max(1.0, prog.Seconds > 1 ? prog.Seconds : (_recording?.Seconds ?? 1.0));
                double frac = prog.Done ? 1.0
                    : (workStatus is not null && running ? Math.Clamp(workStatus.Fraction, 0, 0.99)
                        : (prog.Sentences > 0 ? Math.Clamp((prog.Exact + prog.Spread * 0.5) / prog.Sentences, 0, 0.99)
                            : Math.Clamp(prog.Transcribed / totalRecSec, 0, 0.99)));

                int totalChaps = Math.Max(1, pairedBook is not null
                    ? Math.Max(1, _library.GetBookDetail(pairedBook.Id).Sections.Length)
                    : (_recording?.Chapters.Length ?? 1));

                // Compute per-chapter states (done / now / todo)
                var chapStates = new int[totalChaps]; // 2=done, 1=now, 0=todo
                var alignedSections = new HashSet<uint>();
                if (map is not null)
                {
                    foreach (var r in map.Rows)
                        if (r.By != SyncBy.Unread) alignedSections.Add(r.Section);
                }
                int fallbackReady = (int)Math.Floor(frac * totalChaps);
                bool assignedNow = false;
                for (int ci = 0; ci < totalChaps; ci++)
                {
                    bool secDone = prog.Done || alignedSections.Contains((uint)ci) || ci < fallbackReady;
                    if (secDone)
                    {
                        chapStates[ci] = 2;
                    }
                    else if (!assignedNow && (running || frac > 0))
                    {
                        assignedNow = true;
                        chapStates[ci] = 1;
                    }
                    else
                    {
                        chapStates[ci] = 0;
                    }
                }
                int readyChaps = chapStates.Count(s => s == 2);

                string? problem = workStatus?.Phase == AudiobookWorkPhase.Failed ? workStatus.Message : null;
                string sublineText = problem is not null
                    ? L10n.T("停下了 · 在这台电脑上处理")
                    : (!running
                        ? L10n.T("停下了，做好的部分都留着 · 在这台电脑上处理")
                        : (frac >= 0.95 ? L10n.T("就快好了 · 在这台电脑上处理") : (workStatus?.Message ?? L10n.T("正在估算还要多久 · 在这台电脑上处理"))));

                var makingOuter = new Grid();
                makingOuter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                makingOuter.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                makingOuter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                makingOuter.Children.Add(BuildNavBar("chevron-left", null, null));

                var makingBody = new StackPanel { Spacing = 14, Padding = new Thickness(18, 6, 18, 20) };

                // 58pt giant percentage + 3px capsule bar
                var pctHero = new StackPanel { Spacing = 6, Padding = new Thickness(8, 12, 8, 8) };
                var pctLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
                pctLine.Children.Add(new TextBlock
                {
                    Text = $"{(int)Math.Clamp(Math.Round(frac * 100), 0, 100)}",
                    FontSize = 58,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    LineHeight = 62,
                });
                pctLine.Children.Add(new TextBlock
                {
                    Text = "%",
                    FontSize = 20,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T2),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 10),
                });
                pctHero.Children.Add(pctLine);
                pctHero.Children.Add(new TextBlock
                {
                    Text = sublineText,
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(problem is null ? p.T2 : Color.FromArgb(255, 217, 148, 38)),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                });

                var trackBar = new Grid { Height = 3, Width = 330, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
                trackBar.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = ThemeHelper.Brush(p.Inset) });
                trackBar.Children.Add(new Border
                {
                    Height = 3,
                    Width = Math.Round(330.0 * Math.Clamp(frac, 0, 1)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    CornerRadius = new CornerRadius(1.5),
                    Background = ThemeHelper.Brush(tint.Fill),
                });
                pctHero.Children.Add(trackBar);
                makingBody.Children.Add(pctHero);

                // 4-step checklist card
                bool downloadingModel = workStatus?.Phase == AudiobookWorkPhase.DownloadingModel;
                int step1State = downloadingModel ? 1 : 2;
                int step2State = downloadingModel ? 0 : (prog.Transcribed >= totalRecSec * 0.95 || readyChaps > 0 ? 2 : 1);
                int step3State = prog.Done ? 2 : (readyChaps > 0 || frac > 0.25 ? 1 : 0);
                int step4State = prog.Done ? 2 : (frac >= 0.97 ? 1 : 0);

                FrameworkElement MakeStepBadge(int st)
                {
                    if (st == 2)
                    {
                        return new Border
                        {
                            Width = 18,
                            Height = 18,
                            CornerRadius = new CornerRadius(9),
                            Background = ThemeHelper.Brush(tint.Fill),
                            Child = YYIcons.Make("checkmark", 9, ThemeHelper.Brush(tint.OnBlack)),
                        };
                    }
                    if (st == 1)
                    {
                        return new Border
                        {
                            Width = 18,
                            Height = 18,
                            CornerRadius = new CornerRadius(9),
                            Background = ThemeHelper.Brush(tint.Soft),
                            BorderBrush = ThemeHelper.Brush(tint.Accent),
                            BorderThickness = new Thickness(2),
                        };
                    }
                    return new Border
                    {
                        Width = 18,
                        Height = 18,
                        CornerRadius = new CornerRadius(9),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(1.2),
                    };
                }

                Grid MakeStepRow(string name, string val, int st)
                {
                    var rg = new Grid { Height = 36, ColumnSpacing = 10, Padding = new Thickness(12, 0, 12, 0) };
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    rg.Children.Add(MakeStepBadge(st));
                    var nTb = new TextBlock
                    {
                        Text = name,
                        FontSize = 11.5,
                        FontWeight = st == 1 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                        Foreground = ThemeHelper.Brush(st == 0 ? p.T3 : p.T1),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(nTb, 1);
                    rg.Children.Add(nTb);
                    var vTb = new TextBlock
                    {
                        Text = val,
                        FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
                        FontSize = 10.5,
                        FontWeight = st == 1 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                        Foreground = ThemeHelper.Brush(st == 1 ? tint.Accent : p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(vTb, 2);
                    rg.Children.Add(vTb);
                    return rg;
                }

                var stepsStack = new StackPanel { Spacing = 0 };
                stepsStack.Children.Add(MakeStepRow(
                    L10n.T("读取章节"),
                    downloadingModel ? $"下载对齐模型 {(int)Math.Round(frac * 100)}%" : $"{totalChaps} 章 · {FormatChineseLength(totalRecSec)}",
                    step1State));
                stepsStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(40, 0, 12, 0) });
                stepsStack.Children.Add(MakeStepRow(
                    L10n.T("找出每集念的是哪一段"),
                    $"{FormatTime(Math.Min(totalRecSec, prog.Transcribed))} / {FormatTime(totalRecSec)}",
                    step2State));
                stepsStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(40, 0, 12, 0) });
                stepsStack.Children.Add(MakeStepRow(
                    L10n.T("逐句对齐"),
                    readyChaps > 0 ? $"第 {Math.Min(readyChaps + 1, totalChaps)} / {totalChaps} 章" : L10n.T("一集一集来"),
                    step3State));
                stepsStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(40, 0, 12, 0) });
                stepsStack.Children.Add(MakeStepRow(L10n.T("检查"), L10n.T("最后一步"), step4State));

                makingBody.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Child = stepsStack,
                });

                // 12-column chapter completion matrix (AudiobookViews.swift:537-688)
                var matrixLabelRow = new Grid { Margin = new Thickness(4, 2, 4, -6) };
                matrixLabelRow.Children.Add(new TextBlock { Text = $"{totalChaps} 章", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) });
                matrixLabelRow.Children.Add(new TextBlock { Text = $"{readyChaps} 章已经能听", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
                makingBody.Children.Add(matrixLabelRow);

                var matrixOuter = new StackPanel { Spacing = 10 };
                int cols = 12;
                int rowsCount = (totalChaps + cols - 1) / cols;
                var matrixGrid = new Grid { ColumnSpacing = 4, RowSpacing = 5 };
                for (int c = 0; c < cols; c++)
                    matrixGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int r = 0; r < rowsCount; r++)
                    matrixGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (int idx = 0; idx < totalChaps; idx++)
                {
                    int st = chapStates[idx];
                    Color fillCol = st == 2 ? tint.Fill : (st == 1 ? tint.Soft : p.Inset);
                    Color textCol = st == 2 ? tint.OnBlack : (st == 1 ? tint.Accent : p.T3);
                    var cell = new Border
                    {
                        Height = 22,
                        CornerRadius = new CornerRadius(5),
                        Background = ThemeHelper.Brush(fillCol),
                        BorderBrush = st == 1 ? ThemeHelper.Brush(tint.Accent) : null,
                        BorderThickness = new Thickness(st == 1 ? 1.5 : 0),
                        Child = new TextBlock
                        {
                            Text = $"{idx + 1}",
                            FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
                            FontSize = 9.5,
                            FontWeight = st == 1 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                            Foreground = ThemeHelper.Brush(textCol),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    };
                    Grid.SetRow(cell, idx / cols);
                    Grid.SetColumn(cell, idx % cols);
                    matrixGrid.Children.Add(cell);
                }
                matrixOuter.Children.Add(matrixGrid);

                var legendRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                StackPanel MakeLegend(int st, string text)
                {
                    Color fillCol = st == 2 ? tint.Fill : (st == 1 ? tint.Soft : p.Inset);
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                    sp.Children.Add(new Border
                    {
                        Width = 8,
                        Height = 8,
                        CornerRadius = new CornerRadius(2),
                        Background = ThemeHelper.Brush(fillCol),
                        BorderBrush = st == 1 ? ThemeHelper.Brush(tint.Accent) : null,
                        BorderThickness = new Thickness(st == 1 ? 1.2 : 0),
                    });
                    sp.Children.Add(new TextBlock { Text = text, FontSize = 10, Foreground = ThemeHelper.Brush(p.T2) });
                    return sp;
                }
                legendRow.Children.Add(MakeLegend(2, L10n.T("对好了")));
                legendRow.Children.Add(MakeLegend(1, L10n.T("正在对")));
                legendRow.Children.Add(MakeLegend(0, L10n.T("还没到")));
                matrixOuter.Children.Add(legendRow);

                makingBody.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(12, 12, 12, 11),
                    Child = matrixOuter,
                });

                if (!string.IsNullOrEmpty(problem))
                {
                    var probRow = new Grid { ColumnSpacing = 7 };
                    probRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    probRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    probRow.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(Color.FromArgb(255, 217, 148, 38)), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) });
                    var pTb = new TextBlock { Text = problem, FontSize = 11, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap };
                    Grid.SetColumn(pTb, 1);
                    probRow.Children.Add(pTb);

                    makingBody.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(12),
                        Background = ThemeHelper.Brush(p.Card),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.75),
                        Padding = new Thickness(12, 10, 12, 10),
                        Child = probRow,
                    });
                }

                makingBody.Children.Add(new TextBlock
                {
                    Text = L10n.T("可以离开这一页，也可以去做别的，好了会通知你。"),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(4, 0, 4, 0),
                    TextWrapping = TextWrapping.Wrap,
                });

                var mScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = makingBody };
                Grid.SetRow(mScroll, 1);
                makingOuter.Children.Add(mScroll);

                var mFoot = new Border { Padding = new Thickness(16, 10, 16, 18) };
                if (running)
                {
                    string footLabel = readyChaps == 0 ? L10n.T("对好一章就能先听") : $"先听对好的 {readyChaps} 章";
                    var listenReadyBtn = YYChrome.MakeSheetPillButton(footLabel, () =>
                    {
                        pendingAction = "listen";
                        dlgRef?.Hide();
                    }, dark, _library.Prefs.Tint, "primary", height: 40, stretch: true);
                    listenReadyBtn.IsEnabled = readyChaps > 0 && pairedBook is not null;
                    mFoot.Child = listenReadyBtn;
                }
                else
                {
                    string resumeLabel = problem is null ? L10n.T("接着合成") : L10n.T("再试一次");
                    var resumeBtn = YYChrome.MakeSheetPillButton(resumeLabel, () =>
                    {
                        if (pairedBook is not null)
                        {
                            _library.AudiobookWork.Start(_bookId, pairedBook.Id, _globalPosSec, autoDownloadModel: true);
                            RebuildSheetView();
                        }
                    }, dark, _library.Prefs.Tint, "primary", height: 40, stretch: true);
                    resumeBtn.IsEnabled = pairedBook is not null;
                    mFoot.Child = resumeBtn;
                }
                Grid.SetRow(mFoot, 2);
                makingOuter.Children.Add(mFoot);

                hostGrid.Children.Add(makingOuter);
            }

            bool sheetClosed = false;
            void OnLiveStatus(AudiobookWorkStatus st)
            {
                if (_closed || sheetClosed || st.RecordingId != _bookId) return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_closed && !sheetClosed) RebuildSheetView();
                });
            }

            RebuildSheetView();
            _library.AudiobookWork.StatusChanged += OnLiveStatus;
            dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, dark, 420, hostGrid, maxHeight: 640, tint: _library.Prefs.Tint);
            try
            {
                await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
            }
            finally
            {
                sheetClosed = true;
                _library.AudiobookWork.StatusChanged -= OnLiveStatus;
                StopIntroPreview();
            }

            if (pendingAction == "confirm-unpair")
            {
                bool confirmed = await YYChrome.ShowConfirmAsync(
                    RootGrid.XamlRoot,
                    dark,
                    L10n.T("解除这本有声书的配对？"),
                    L10n.T("合成好的句级时间轴会清除，书和录音文件本身不受影响。"),
                    confirmText: L10n.T("解除配对"),
                    cancelText: L10n.T("取消"),
                    destructive: true,
                    tint: _library.Prefs.Tint);
                if (confirmed)
                {
                    _library.AudiobookWork.Stop(_bookId);
                    try { _library.Core.AudiobookPair(_bookId, null); } catch { }
                    RefreshCachedMap();
                    RebuildRecordingSettings();
                    RenderPairCard();
                    UpdateKeyHints();
                    return;
                }
                continue;
            }

            if (pendingAction == "align-check")
            {
                PausePlayback();
                AlignCheckAndPolishService.OpenAlignCheckWindow(_library, _bookId, 0, () =>
                {
                    RefreshCachedMap();
                    RebuildRecordingSettings();
                    RenderPairCard();
                    UpdateKeyHints();
                });
                return;
            }

            if (pendingAction == "listen")
            {
                ListenPair? pair = null;
                try { pair = _library.Core.ListeningPairs().FirstOrDefault(lp => lp.Recording == _bookId); } catch { }
                if (pair is not null)
                    OpenSyncedPairedEbook(pair.Book);
            }
            return;
        }
    }

    private void RebuildRecordingSettings()
    {
        RecordingSettingsHost.Children.Clear();
        RefreshCachedMap();
        bool hasMap = HasPairedMap;
        bool showStreamToggle = _isStreamedAudiobook;
        if (!hasMap && !showStreamToggle)
        {
            RecordingSettingsCard.Visibility = Visibility.Collapsed;
            return;
        }
        RecordingSettingsCard.Visibility = Visibility.Visible;

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        StackPanel MakeSettingRow(string name, string? detail, FrameworkElement control)
        {
            var wrap = new StackPanel { Spacing = 0 };
            wrap.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            var g = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 6, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = L10n.T(name), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1) });
            if (!string.IsNullOrEmpty(detail))
                sp.Children.Add(new TextBlock { Text = detail, FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            g.Children.Add(sp);
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            wrap.Children.Add(g);
            return wrap;
        }

        StackPanel MakeSwitchRow(string name, string? detail, bool val, Action<bool> onToggle)
        {
            var sw = new ToggleSwitch { IsOn = val, MinWidth = 0, OnContent = "", OffContent = "" };
            sw.Toggled += (_, _) => onToggle(sw.IsOn);
            return MakeSettingRow(name, detail, sw);
        }

        // Header: 这本录音 + subtle quick tools (对齐检查 / 整理错字)
        var headRow = new Grid { Padding = new Thickness(0, 2, 0, 4) };
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headRow.Children.Add(new TextBlock
        {
            Text = L10n.T("这本录音"),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ThemeHelper.Brush(p.T1),
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (hasMap)
        {
            var headTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var alignLink = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("微调对齐"),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T2),
                Padding = new Thickness(4, 1, 4, 1),
            };
            alignLink.Click += (_, _) =>
            {
                PausePlayback();
                AlignCheckAndPolishService.OpenAlignCheckWindow(_library, _bookId, 0, () =>
                {
                    RefreshCachedMap();
                    RebuildRecordingSettings();
                    RenderPairCard();
                });
            };
            headTools.Children.Add(alignLink);

            ListenPair? curPair = null;
            try { curPair = _library.Core.ListeningPairs().FirstOrDefault(lp => lp.Recording == _bookId); } catch { }
            if (curPair is not null && _library.Core.AudiobookIsTextVersion(curPair.Book))
            {
                string pairedTextId = curPair.Book;
                var polishLink = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Content = L10n.T("整理错字"),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    Padding = new Thickness(4, 1, 4, 1),
                };
                polishLink.Click += async (_, _) => await AlignCheckAndPolishService.ShowTextPolishDialogAsync(RootGrid.XamlRoot, _library, pairedTextId);
                headTools.Children.Add(polishLink);
            }
            Grid.SetColumn(headTools, 1);
            headRow.Children.Add(headTools);
        }
        RecordingSettingsHost.Children.Add(headRow);

        if (!hasMap)
        {
            RecordingSettingsHost.Children.Add(MakeSwitchRow(
                "听过的自动清掉",
                L10n.T("听完一集清掉本地缓存"),
                _autoClearPlayedStreamTracks,
                v =>
                {
                    _autoClearPlayedStreamTracks = v;
                    SaveStreamPrefs();
                }));
            return;
        }

        SkipSettings s = _cachedSkipSettings;
        double savedSec = 0;
        try { savedSec = _library.Core.AudiobookSaved(_bookId); } catch { }

        int unreadCount = _cachedMap?.Rows.Count(r => r.By == SyncBy.Unread) ?? 0;

        void SaveSkip(SkipSettings next, bool reloadAudio = false)
        {
            s = next;
            _cachedSkipSettings = next;
            try { _library.Core.AudiobookSetSkipSettings(_bookId, s); } catch { }
            if (!s.FillUnread && _activeUnreadRun is { } curRun)
            {
                _activeUnreadRun = null;
                try { _fillTtsPlayer.Stop(); } catch { }
                if (_playing) _ = SeekGlobalAsync(curRun.Resume, startPlaying: true);
            }
            if (reloadAudio) _ = SeekGlobalAsync(_globalPosSec, _playing, forceReload: true);
            RebuildRecordingSettings();
            SyncChapterUi();
        }

        // 1:1 AudiobookScreen.swift:799-823: 片头片尾 segmented + switches + 导出…
        var headTailSeg = YYChrome.MakeSegmentedBar(
            [("skip", L10n.T("跳过")), ("keep", L10n.T("照放"))],
            s.HeadTail ? "skip" : "keep",
            picked => SaveSkip(s with { HeadTail = picked == "skip" }),
            dark,
            fontSize: 11,
            height: 26);
        RecordingSettingsHost.Children.Add(MakeSettingRow("片头片尾", null, headTailSeg));

        RecordingSettingsHost.Children.Add(MakeSwitchRow("章间音乐", L10n.T("压到 1.5 秒"), s.ShortenChapterGaps, v => SaveSkip(s with { ShortenChapterGaps = v })));
        RecordingSettingsHost.Children.Add(MakeSwitchRow("人声清楚一点", L10n.T("压低底噪，人声提一点"), s.ClearerVoice, v => SaveSkip(s with { ClearerVoice = v }, reloadAudio: true)));
        RecordingSettingsHost.Children.Add(MakeSwitchRow(
            "缩短停顿",
            savedSec >= 60 ? $"这本已经省了 {FormatChineseLength(savedSec)}" : L10n.T("长停顿压短一些"),
            s.ShortenPauses,
            v => SaveSkip(s with { ShortenPauses = v })));
        RecordingSettingsHost.Children.Add(MakeSwitchRow(
            "没念的用朗读补上",
            unreadCount > 0 ? $"这本有 {unreadCount} 句没念" : L10n.T("录音每句都念了"),
            s.FillUnread,
            v => SaveSkip(s with { FillUnread = v })));
        RecordingSettingsHost.Children.Add(MakeSwitchRow("逐词跟读", L10n.T("念到哪个词，哪个词下面一道线"), s.WordFollow, v => SaveSkip(s with { WordFollow = v })));

        if (showStreamToggle)
        {
            RecordingSettingsHost.Children.Add(MakeSwitchRow(
                "听过的自动清掉",
                L10n.T("听完一集清掉本地缓存"),
                _autoClearPlayedStreamTracks,
                v =>
                {
                    _autoClearPlayedStreamTracks = v;
                    SaveStreamPrefs();
                }));
        }

        var exportBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
            Content = L10n.T("导出…"),
            Height = 25,
            MinHeight = 25,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 11,
            IsEnabled = _exportingStatus is null,
        };
        exportBtn.Click += async (_, _) =>
        {
            var outPath = await NativePickerHelper.PickSaveFileAsync(this, _summary.Title, [(L10n.T("EPUB 电子书"), [".epub"])]);
            if (string.IsNullOrEmpty(outPath)) return;
            _exportingStatus = L10n.T("正在打包，录音大的要一会儿");
            RebuildRecordingSettings();
            try
            {
                await Task.Run(() => _library.Core.AudiobookExportEpub(_bookId, outPath));
                _exportingStatus = null;
                RebuildRecordingSettings();
                ShowToast("✓ 已导出有声电子书 (.epub)");
            }
            catch (Exception ex)
            {
                _exportingStatus = L10n.T("没导出来，再试一次");
                RebuildRecordingSettings();
                ShowToast($"导出失败：{ex.Message}");
            }
        };
        RecordingSettingsHost.Children.Add(MakeSettingRow(
            "导出带朗读时间的电子书",
            _exportingStatus ?? L10n.T("一个 EPUB，别的阅读器也能跟读"),
            exportBtn));
        L10n.ApplyTree(RecordingSettingsHost);
    }

    private async Task RenameChapterAsync(int chapterIdx)
    {
        if (_recording is null || chapterIdx < 0 || chapterIdx >= _recording.Chapters.Length) return;
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        string initial = _recording.Chapters[chapterIdx].Title ?? "";
        string? typed = await YYChrome.ShowPromptAsync(
            RootGrid.XamlRoot,
            dark,
            L10n.T("给这一集起个名字"),
            placeholder: $"第 {chapterIdx + 1} 集",
            initialText: initial,
            confirmText: L10n.T("保存"),
            cancelText: L10n.T("取消"),
            subtitle: L10n.T("留空就叫「第几集」。"),
            tint: _library.Prefs.Tint);
        if (typed is null) return;
        try
        {
            _summary = _library.Core.RenameAudioChapter(_bookId, (uint)chapterIdx, string.IsNullOrWhiteSpace(typed) ? null : typed.Trim());
            _recording = _library.Core.AudioRecording(_bookId);
            SyncChapterUi();
            RenderChaptersAndBookmarks();
        }
        catch { }
    }

    // ---------- Transport & Menus ----------

    private void BackToShelf_Click(object sender, RoutedEventArgs e) => App.CurrentApp.ShowShelf();

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isSleepFading)
        {
            ExtendSleepTenMin();
            return;
        }
        if (_playing)
        {
            PausePlayback();
        }
        else
        {
            StopIntroPreview();
            DismissSleepReturnCard(clearSaved: true);

            // Heard to the end (100%): start again from 0! (1:1 AudiobookPlayer.swift:394)
            if (_recording is not null &&
                _recording.Seconds > 0 &&
                _globalPosSec >= _recording.Seconds - 0.5 &&
                (_recording.Files.Length == 0 || _recording.Files[^1].Seconds > 0))
            {
                await SeekGlobalAsync(0, startPlaying: true);
                return;
            }

            App.CurrentApp.ClaimAudioPlayback(this, () => DispatcherQueue.TryEnqueue(() => { if (!_closed) PausePlayback(); }));
            if (_pausedUtc is DateTime pausedAt)
            {
                double pausedSec = (DateTime.UtcNow - pausedAt).TotalSeconds;
                _pausedUtc = null;
                if (pausedSec >= 5.0)
                {
                    try
                    {
                        double resumeAt = _library.Core.AudiobookResumeAt(_bookId, _globalPosSec, pausedSec);
                        if (Math.Abs(resumeAt - _globalPosSec) > 0.3)
                        {
                            await SeekGlobalAsync(resumeAt, startPlaying: true);
                            return;
                        }
                    }
                    catch { }
                }
            }
            _player.Volume = _baseVolume;
            _player.Play();
            _playing = true;
            _library.Analytics.TrackAudioFeatureUsed("audiobook_play");
            _listenSinceUtc = DateTime.UtcNow;
            _sleepLastTickUtc = DateTime.UtcNow;
            UpdateTransportUi();
        }
    }

    private async void Rewind15_Click(object sender, RoutedEventArgs e)
    {
        if (_isSleepFading) { ExtendSleepTenMin(); return; }
        await SeekGlobalAsync(_globalPosSec - 15, _playing);
    }

    private async void Forward30_Click(object sender, RoutedEventArgs e)
    {
        if (_isSleepFading) { ExtendSleepTenMin(); return; }
        await SeekGlobalAsync(_globalPosSec + 30, _playing);
    }

    private async Task StepSentenceOrSeekAsync(bool forward, bool forceSeconds)
    {
        if (_isSleepFading) { ExtendSleepTenMin(); return; }
        if (!forceSeconds && HasPairedMap)
        {
            try
            {
                double? stepped = _library.Core.AudiobookStep(_bookId, _globalPosSec, forward);
                if (stepped is double s) { await SeekGlobalAsync(s, _playing); return; }
            }
            catch { }
        }
        double delta = HasPairedMap && forceSeconds ? (forward ? 15 : -15) : (forward ? 30 : -15);
        await SeekGlobalAsync(_globalPosSec + delta, _playing);
    }

    private void OnChapterSliderPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isScrubbingSlider = false;
        if (_pendingSliderInChapSec >= 0 && _recording is not null)
        {
            _sliderSeekTimer?.Stop();
            double inChap = _pendingSliderInChapSec;
            _pendingSliderInChapSec = -1;
            var (chStart, _) = CurrentChapterBounds();
            _ = SeekGlobalAsync(chStart + inChap, _playing);
        }
    }

    private void ChapterSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlider || _recording is null) return;
        ChapterElapsedText.Text = FormatTime(e.NewValue);
        var (chStart, _) = CurrentChapterBounds();
        double target = chStart + e.NewValue;
        if (Math.Abs(target - _globalPosSec) <= 0.6) return;
        _pendingSliderInChapSec = e.NewValue;
        _sliderSeekTimer?.Stop();
        _sliderSeekTimer?.Start();
    }

    private void Rate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        var menu = new MenuFlyout();
        foreach (var r in SpeedOptions)
        {
            double val = r;
            bool on = Math.Abs(val - _rate) < 0.03;
            var item = new MenuFlyoutItem { Text = (on ? "✓ " : "") + $"{val:0.##}×" };
            item.Click += (_, _) =>
            {
                _rate = val;
                _library.Prefs.AudiobookRate = _rate;
                _library.SavePrefs();
                _player.PlaybackSession.PlaybackRate = _rate;
                if (_activeUnreadRun is not null)
                    _fillTtsPlayer.SetRate(_rate);
                RateBtn.Content = $"{_rate:0.##}×";
                SyncChapterUi();
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }

    private void SleepTimer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        var menu = new MenuFlyout();

        void AddOpt(int mode, string title)
        {
            var item = new MenuFlyoutItem { Text = (_sleepMode == mode ? "✓ " : "") + title };
            item.Click += (_, _) =>
            {
                _sleepMode = mode;
                _isSleepFading = false;
                _player.Volume = _baseVolume;
                _sleepSentenceEndSec = -1;
                _sleepArmedSpot = mode != 0 ? ((uint)CurrentChapterIndex(), (int)Math.Round((_globalPosSec - CurrentChapterBounds().Start) * 1000.0)) : null;

                if (mode > 0)
                {
                    _sleepRemainingSec = mode * 60.0;
                    _sleepDeadlineUtc = DateTime.UtcNow.AddSeconds(_sleepRemainingSec);
                    _sleepLastTickUtc = DateTime.UtcNow;
                }
                else if (mode == -2)
                {
                    _sleepRemainingSec = 0;
                    _sleepDeadlineUtc = null;
                    try
                    {
                        var b = _library.Core.AudiobookSentenceBounds(_bookId, _globalPosSec);
                        _sleepSentenceEndSec = b is not null ? b.T1 : _globalPosSec + 1.0;
                    }
                    catch { _sleepSentenceEndSec = _globalPosSec + 1.0; }
                    SleepCountdownText.Text = "念完这句自动停止";
                }
                else
                {
                    _sleepRemainingSec = 0;
                    _sleepDeadlineUtc = null;
                    SleepCountdownText.Text = mode == -1 ? "本集播完自动停止" : "";
                }
                ApplyAppearance();
            };
            menu.Items.Add(item);
        }

        if (_cachedMap is not null) AddOpt(-2, "念完这句");
        AddOpt(-1, "本集完");
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var m in new[] { 15, 30, 45, 60, 90 }) AddOpt(m, $"{m} 分钟");
        if (_sleepMode != 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            AddOpt(0, "关闭定时");
        }
        menu.ShowAt(anchor);
    }

    private void HighlightHeardOrBookmark()
    {
        try
        {
            var heard = _library.Core.AudiobookHeardSentence(_bookId, _globalPosSec);
            if (heard is not null)
            {
                var sents = _library.GetSentences(heard.Book, heard.Section);
                if (heard.Sentence < sents.Count)
                {
                    string txt = sents[(int)heard.Sentence];
                    var mk = _library.Core.MarkHeard(
                        heard.Book,
                        heard.Section,
                        new TextPoint(heard.Sentence, 0),
                        new TextPoint(heard.Sentence, (uint)txt.Length),
                        txt,
                        MarkStyle.Yellow);
                    string bk = heard.Book, mkId = mk.Id;
                    ShowToast("已划下刚听到的这句", "撤销", () => { try { _library.DeleteMark(bk, mkId); } catch { } });
                    return;
                }
            }
        }
        catch { }
        AddBookmarkAtCurrentPosition();
    }

    private void AddBookmarkAtCurrentPosition(bool silent = false)
    {
        if (_recording is null || _recording.Chapters.Length == 0) return;
        int ch = CurrentChapterIndex();
        uint ms = (uint)Math.Max(0, Math.Round((_globalPosSec - _recording.Chapters[ch].Start) * 1000.0));
        try
        {
            _library.AddBookmark(_bookId, (uint)ch, ms);
            RenderChaptersAndBookmarks();
            RenderBookmarkTicks();
            if (!silent) ShowToast($"已加书签 · {ChapterTitle(ch)} {FormatTime(ms / 1000.0)}");
        }
        catch { }
    }

    private void ShowToast(string msg, string? actionLabel = null, Action? onAction = null, int autoHideSeconds = 4)
    {
        ToastText.Text = L10n.T(msg);
        _toastAction = onAction;
        if (!string.IsNullOrEmpty(actionLabel) && onAction is not null)
        {
            ToastActionBtn.Content = L10n.T(actionLabel);
            ToastActionBtn.Visibility = Visibility.Visible;
        }
        else
        {
            ToastActionBtn.Visibility = Visibility.Collapsed;
        }
        ToastBanner.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoHideSeconds) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBanner.Visibility = Visibility.Collapsed; };
        _toastTimer.Start();
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var act = _toastAction;
        _toastAction = null;
        _toastTimer?.Stop();
        ToastBanner.Visibility = Visibility.Collapsed;
        try { act?.Invoke(); } catch { }
    }

    private void ToggleFullScreen()
    {
        try
        {
            var aw = AppWindow;
            if (aw is null) return;
            if (aw.Presenter.Kind == AppWindowPresenterKind.FullScreen)
                aw.SetPresenter(AppWindowPresenterKind.Default);
            else
                aw.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        catch { }
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_closed || YYChrome.HasBlockingModalOrFlyoutOpen(RootGrid.XamlRoot))
            return;

        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == VirtualKey.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Escape)
        {
            if (AppWindow?.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }
            if (ToastBanner.Visibility == Visibility.Visible)
            {
                _toastTimer?.Stop();
                ToastBanner.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
        }
        if (ctrl && !alt && e.Key == VirtualKey.W)
        {
            e.Handled = true;
            Close();
            return;
        }
        if (ctrl && !alt && e.Key == VirtualKey.K)
        {
            _ = QuickSearchDialog.ShowAsync(RootGrid.XamlRoot, _library, _bookId);
            e.Handled = true;
            return;
        }
        if (ctrl && !alt && e.Key == (VirtualKey)188)
        {
            App.CurrentApp.OpenSettings();
            e.Handled = true;
            return;
        }

        if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox)
            return;

        if (ctrl && !alt && e.Key == VirtualKey.D)
        {
            AddBookmarkAtCurrentPosition();
            e.Handled = true;
            return;
        }
        if (ctrl && !alt && e.Key == VirtualKey.H)
        {
            HighlightHeardOrBookmark();
            e.Handled = true;
            return;
        }

        if (ctrl || alt)
            return;

        switch (e.Key)
        {
            case VirtualKey.Space:
                PlayPause_Click(sender, e); e.Handled = true; break;
            case VirtualKey.Left:
                _ = StepSentenceOrSeekAsync(forward: false, forceSeconds: shift); e.Handled = true; break;
            case VirtualKey.Right:
                _ = StepSentenceOrSeekAsync(forward: true, forceSeconds: shift); e.Handled = true; break;
        }
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (_closed) return;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_closed || _recording is null) return;
            if (_isSleepFading && args.Button is SystemMediaTransportControlsButton.Play or SystemMediaTransportControlsButton.Next or SystemMediaTransportControlsButton.Previous)
            {
                ExtendSleepTenMin();
                return;
            }
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    PlayPause_Click(this, new RoutedEventArgs());
                    break;
                case SystemMediaTransportControlsButton.Pause: PausePlayback(); break;
                case SystemMediaTransportControlsButton.Next:
                    if (_cachedMap is not null)
                    {
                        await StepSentenceOrSeekAsync(forward: true, forceSeconds: false);
                    }
                    else if (_recording.Chapters.Length > 0)
                    {
                        int next = Math.Min(_recording.Chapters.Length - 1, CurrentChapterIndex() + 1);
                        await SeekGlobalAsync(_recording.Chapters[next].Start, _playing);
                        RenderChaptersAndBookmarks();
                    }
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    if (_cachedMap is not null)
                    {
                        await StepSentenceOrSeekAsync(forward: false, forceSeconds: false);
                    }
                    else if (_recording.Chapters.Length > 0)
                    {
                        int prev = Math.Max(0, CurrentChapterIndex() - 1);
                        await SeekGlobalAsync(_recording.Chapters[prev].Start, _playing);
                        RenderChaptersAndBookmarks();
                    }
                    break;
            }
        });
    }

    private static string FormatTime(double sec)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}" : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        try
        {
            if (ThemeHelper.TryGetRestoredDipSize(this, out int dipW, out int dipH))
            {
                _library.Prefs.AudiobookWindowWidth = Math.Max(760, dipW);
                _library.Prefs.AudiobookWindowHeight = Math.Max(520, dipH);
                _library.SavePrefs();
            }
        }
        catch { }

        try
        {
            _windowCts.Cancel();
            if (Volatile.Read(ref _activeBackgroundDecodeCount) == 0)
                _windowCts.Dispose();
        }
        catch { }
        _library.AudiobookWork.StatusChanged -= OnAudiobookWorkStatusChanged;
        _uiTimer.Stop();
        _listenTimeTracker.Stop();
        _sliderSeekTimer?.Stop();
        _toastTimer?.Stop();
        StopIntroPreview();
        FlushListeningTime();

        _activeUnreadRun = null;
        _fillTtsPlayer.SectionFinished -= OnFillTtsFinished;
        _fillTtsPlayer.SynthesisError -= OnFillTtsError;
        try { _fillTtsPlayer.Stop(); _fillTtsPlayer.Dispose(); } catch { }

        _library.SystemIntegration.SetAudioPlaying(this, false);
        SaveCurrentProgress();
        try
        {
            _player.MediaEnded -= OnFileEnded;
            _player.SystemMediaTransportControls.ButtonPressed -= OnSmtcButtonPressed;
        }
        catch { }
        try
        {
            _player.Pause();
            _player.Source = null;
            _player.Dispose();
        }
        catch { }
    }
}
