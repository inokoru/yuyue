using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
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
    private double _rate = 1.0;
    private readonly bool _startFromBeginning;

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
    private Action? _toastAction;

    private static readonly double[] SpeedOptions = [0.75, 1.0, 1.1, 1.2, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];

    public AudiobookWindow(LibraryService library, string bookId, bool autoPlay = false, bool startFromBeginning = false)
    {
        _library = library;
        _bookId = bookId;
        _startFromBeginning = startFromBeginning;
        _summary = library.GetKeptBooks().FirstOrDefault(b => b.Id == bookId)
            ?? library.GetBookDetail(bookId).Summary;
        _rate = Math.Clamp(_library.Prefs.SpeechRate, 0.75, 3.0);

        InitializeComponent();
        Title = _summary.Title;
        ThemeHelper.ResizeWindow(this, 960, 640);
        SetTitleBar(TitleDragRegion);
        ThemeHelper.ApplyElevation(CoverFrame, ShadowReceiver, 18f);
        ThemeHelper.ApplyElevation(ToastBanner, ShadowReceiver, 10f);

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

        BookmarkTicksCanvas.SizeChanged += (_, _) => RenderBookmarkTicks();
        _library.AudiobookWork.StatusChanged += OnAudiobookWorkStatusChanged;
        Closed += OnWindowClosed;

        ApplyAppearance();
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
        if (status.RecordingId != _bookId) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            RenderPairCard();
            if (status.Phase == AudiobookWorkPhase.Done)
                ShowToast(status.Message);
            else if (status.Phase == AudiobookWorkPhase.Failed)
                ShowToast($"对齐出错：{status.Message}");
        });
    }

    private void TryAutoResumeAlignment()
    {
        try
        {
            var prog = _library.Core.AudiobookProgress(_bookId);
            if (!string.IsNullOrEmpty(prog.PairedWith) && !prog.Done && Aligner.IsInstalled(_library.DataDir))
                _library.AudiobookWork.Start(_bookId, prog.PairedWith, _globalPosSec, autoDownloadModel: false);
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
        RenderPairCard();
        L10n.Skip(BookBylineLabel);
        L10n.ApplyTree(RootGrid);
    }

    private async Task LoadRecordingAsync(bool autoPlay)
    {
        string author = _summary.Authors.FirstOrDefault() ?? "";
        BookBylineLabel.Text = string.IsNullOrWhiteSpace(author) ? _summary.Title : $"{_summary.Title} · {author}";

        if (!string.IsNullOrEmpty(_summary.CoverPath) && File.Exists(_summary.CoverPath))
        {
            CoverImage.Source = new BitmapImage(new Uri(_summary.CoverPath));
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

        try { _cachedMap = _library.Core.AudiobookMap(_bookId); } catch { _cachedMap = null; }
        if (_library.Prefs.AudiobookLoudnessGain.TryGetValue(_bookId, out float savedGain))
            _baseVolume = Math.Clamp(savedGain, 0.35, 1.0);
        else
            _ = MeasureBookLoudnessInBackgroundAsync();

        await SeekGlobalAsync(_globalPosSec, startPlaying: autoPlay);
        SaveCurrentProgress();
        App.CurrentApp.NotifyShelfRefresh();
        RenderChaptersAndBookmarks();
        RenderPairCard();

        // Offer 回到昨晚 (SleepReturn) or Elsewhere progress if applicable
        if (ReaderRecapAndSleepHelper.TryGetSleepReturn(_library, _bookId) is { } sleepRet)
        {
            int targetCh = Math.Clamp((int)sleepRet.Section, 0, Math.Max(0, _recording.Chapters.Length - 1));
            double returnSec = _recording.Chapters.Length > 0 ? _recording.Chapters[targetCh].Start + sleepRet.Sentence / 1000.0 : sleepRet.Sentence / 1000.0;
            ShowToast($"{sleepRet.When}定时暂停过", "回到暂停位置", () =>
            {
                ReaderRecapAndSleepHelper.ClearSleepReturn(_library, _bookId);
                _ = SeekGlobalAsync(returnSec, startPlaying: true);
            }, autoHideSeconds: 8);
        }
        else
        {
            try
            {
                var latest = _library.Core.LatestProgress(_bookId);
                if (latest is not null && !latest.ThisDevice && saved is not null && latest.At > saved.At && _recording.Chapters.Length > 0)
                {
                    int rCh = Math.Clamp((int)latest.Position.Section, 0, _recording.Chapters.Length - 1);
                    double remoteSec = _recording.Chapters[rCh].Start + latest.Position.Sentence / 1000.0;
                    if (Math.Abs(remoteSec - _globalPosSec) > 8.0)
                    {
                        ShowToast($"另一台设备听到了 {ChapterTitle(rCh)}", "跳过去", () => _ = SeekGlobalAsync(remoteSec, startPlaying: true), autoHideSeconds: 9);
                    }
                }
            }
            catch { }
        }
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
        if (_recording is null || _recording.Files.Length == 0) return;
        string firstPath = ResolveTrackPath(_recording.Files[0].Path);
        if (!File.Exists(firstPath)) return;
        try
        {
            string scratch = Path.Combine(_library.DataDir, "audiobooks", _bookId, "pcm-cache");
            string pcm = await Aligner.EnsurePcm16kWavAsync(firstPath, scratch, 0, CancellationToken.None);
            float gain = VoiceTapDsp.MeasureLoudnessFromWav(pcm);
            _library.Prefs.AudiobookLoudnessGain[_bookId] = gain;
            _library.SavePrefs();
            DispatcherQueue.TryEnqueue(() =>
            {
                _baseVolume = Math.Clamp(gain, 0.35, 1.0);
                if (!_isSleepFading) _player.Volume = _baseVolume;
            });
        }
        catch { }
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
        if (_recording is null || _recording.Files.Length == 0) return;
        double clamped = Math.Clamp(targetSec, 0, Math.Max(0, _recording.Seconds - 0.1));
        _globalPosSec = clamped;

        int fileIdx = 0;
        if (explicitFileIndex is int expIdx && expIdx >= 0 && expIdx < _recording.Files.Length)
        {
            fileIdx = expIdx;
        }
        else
        {
            for (int i = 0; i < _fileStartOffsets.Length; i++)
            {
                if (i > 0 && _recording.Files[i].Seconds <= 0) break;
                if (clamped >= _fileStartOffsets[i]) fileIdx = i;
                else break;
            }
        }

        bool wantClearer = false;
        try { wantClearer = _library.Core.AudiobookSkipSettings(_bookId).ClearerVoice; } catch { }

        double localSec = fileIdx < _fileStartOffsets.Length ? Math.Max(0, clamped - _fileStartOffsets[fileIdx]) : 0;
        if (fileIdx != _currentFileIndex || forceReload || wantClearer != _loadedFileIsClearer)
        {
            string filePath = ResolveTrackPath(_recording.Files[fileIdx].Path);
            if (!File.Exists(filePath))
            {
                ShowToast($"正在缓冲第 {fileIdx + 1} 轨…");
                bool fetched = false;
                try
                {
                    fetched = await _library.RemoteLibraries.EnsureStreamTrackAsync(_bookId, fileIdx, _windowCts.Token);
                }
                catch (OperationCanceledException) { return; }
                catch { fetched = false; }

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
                    }
                    localSec = fileIdx < _fileStartOffsets.Length ? Math.Max(0, clamped - _fileStartOffsets[fileIdx]) : 0;
                }

                if (!File.Exists(filePath))
                {
                    ShowToast("无法缓冲该音轨，请检查网络连接");
                    return;
                }

                RenderChaptersAndBookmarks();
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
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string scratch = Path.Combine(_library.DataDir, "audiobooks", _bookId, "pcm-cache");
                            string pcm = await Aligner.EnsurePcm16kWavAsync(srcCap, scratch, idxCap, _windowCts.Token);
                            float gain = _library.Prefs.AudiobookLoudnessGain.TryGetValue(_bookId, out var g) ? g : VoiceTapDsp.MeasureLoudnessFromWav(pcm);
                            if (VoiceTapDsp.ProcessWavFile(pcm, dspPath, gain, clearer: true))
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    if (_currentFileIndex == idxCap)
                                        _ = SeekGlobalAsync(_globalPosSec, _playing, forceReload: true);
                                });
                            }
                        }
                        catch { }
                    });
                }
            }

            var sf = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(playPath));
            var ms = MediaSource.CreateFromStorageFile(sf);
            await ms.OpenAsync();
            _player.Source = ms;
            _currentFileIndex = fileIdx;
            _loadedFileIsClearer = usedClearer;
        }

        _player.Volume = _isSleepFading ? _player.Volume : _baseVolume;
        _player.PlaybackSession.PlaybackRate = _rate;
        _player.PlaybackSession.Position = TimeSpan.FromSeconds(localSec);

        if (startPlaying)
        {
            App.CurrentApp.ClaimAudioPlayback(this, () => DispatcherQueue.TryEnqueue(PausePlayback));
            _player.Play();
            _playing = true;
            _listenSinceUtc ??= DateTime.UtcNow;
            _sleepLastTickUtc = DateTime.UtcNow;
        }

        SyncChapterUi();
        UpdateTransportUi();
        SaveCurrentProgress();
    }

    private void OnFileEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_recording is null) return;
            try { _library.RemoteLibraries.MarkStreamTrackPassed(_bookId, _currentFileIndex); } catch { }
            int nextIdx = _currentFileIndex + 1;
            if (nextIdx < _recording.Files.Length)
            {
                double nextOffset = nextIdx < _fileStartOffsets.Length ? _fileStartOffsets[nextIdx] : _globalPosSec;
                await SeekGlobalAsync(nextOffset, startPlaying: true, explicitFileIndex: nextIdx);
                RenderChaptersAndBookmarks();
            }
            else
            {
                _playing = false;
                UpdateTransportUi();
                SaveCurrentProgress();
            }
        });
    }

    private void OnUiTick(object? sender, object e)
    {
        if (_recording is null) return;
        int prevChap = CurrentChapterIndex();

        if (_playing && _currentFileIndex >= 0 && _currentFileIndex < _fileStartOffsets.Length)
        {
            _globalPosSec = _fileStartOffsets[_currentFileIndex] + _player.PlaybackSession.Position.TotalSeconds;

            if (_sleepMode == -2 && _sleepSentenceEndSec > 0 && _globalPosSec >= _sleepSentenceEndSec)
            {
                FinishSleepStop("已念完这句 · 已自动加书签");
                return;
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
                FinishSleepStop("本集播完 · 已自动加书签");
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
                ? $"即将渐弱暂停 {remSpan.Seconds:D2}s（按空格延 10 分）"
                : $"定时还剩 {(int)remSpan.TotalMinutes:D2}:{remSpan.Seconds:D2}";
        }

        SyncChapterUi();
    }

    private void FinishSleepStop(string toastMsg)
    {
        var armed = _sleepArmedSpot ?? ((uint)CurrentChapterIndex(), (int)Math.Round((_globalPosSec - CurrentChapterBounds().Start) * 1000.0));
        PausePlayback();
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
        double baseRem = _sleepDeadlineUtc is DateTime dl ? Math.Max(0, (dl - now).TotalSeconds) : Math.Max(0, _sleepRemainingSec);
        _sleepMode = 10;
        _sleepRemainingSec = baseRem + 600.0;
        _sleepDeadlineUtc = now.AddSeconds(_sleepRemainingSec);
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
        _suppressSlider = true;
        ChapterSlider.Maximum = chDur;
        ChapterSlider.Value = inChap;
        _suppressSlider = false;

        ChapterElapsedText.Text = FormatTime(inChap);
        ChapterTotalText.Text = FormatTime(chDur);

        double pauseSavings = 0;
        try { pauseSavings = _library.Core.AudiobookPauseSavings(_bookId, _globalPosSec); } catch { }
        double remBookSec = Math.Max(0, _recording.Seconds - _globalPosSec - pauseSavings) / Math.Max(0.5, _rate);
        if (_recording.Chapters.Length > 1 && remBookSec >= 60)
        {
            int hrs = (int)(remBookSec / 3600);
            int mins = (int)Math.Round((remBookSec % 3600) / 60.0);
            BookRemainingText.Text = hrs > 0 ? $"全书还剩 {hrs} 小时 {mins} 分" : $"全书还剩 {mins} 分钟";
        }
        else
        {
            BookRemainingText.Text = "";
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
        var (_, chDur) = CurrentChapterBounds();
        if (chDur <= 0) return;

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var bms = _library.GetBookmarks(_bookId).Where(b => (int)b.Section == curChap).ToArray();
        double w = BookmarkTicksCanvas.ActualWidth;

        foreach (var bm in bms)
        {
            double sec = bm.Sentence / 1000.0;
            double ratio = Math.Clamp(sec / chDur, 0.01, 0.99);
            var tick = new Border
            {
                Width = 2.5,
                Height = 8,
                CornerRadius = new CornerRadius(1.25),
                Background = ThemeHelper.Brush(p.Bg),
                BorderBrush = ThemeHelper.Brush(p.T1),
                BorderThickness = new Thickness(0.5),
            };
            Canvas.SetLeft(tick, ratio * w - 1.25);
            Canvas.SetTop(tick, 1);
            BookmarkTicksCanvas.Children.Add(tick);
        }
    }

    private void UpdateTransportUi()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        PlayPauseBtn.Background = ThemeHelper.Brush(p.T1);
        PlayPauseBtn.Content = YYIcons.Make(_playing ? "pause" : "play", 20, ThemeHelper.Brush(p.Bg), filled: true);

        _library.SystemIntegration.SetAudioPlaying(this, _playing, _summary.Title, () => DispatcherQueue.TryEnqueue(() => PlayPause_Click(this, new RoutedEventArgs())));

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
        bool wasPlaying = _playing;
        _playing = false;
        _isSleepFading = false;
        _player.Volume = _baseVolume;
        if (wasPlaying)
        {
            _pausedUtc = DateTime.UtcNow;
            FlushListeningTime();
        }
        try { _player.Pause(); } catch { }
        UpdateTransportUi();
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
                bmBtn.Click += async (_, _) => await SeekGlobalAsync(targetGlobal, startPlaying: true);
                ChapterListPanel.Children.Add(bmBtn);
            }
        }
    }

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
                cov.Child = new Image { Source = new BitmapImage(new Uri(pairedEbook.CoverPath)), Stretch = Stretch.UniformToFill };
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
                subLine = "已配对 · 点「自动对齐」生成句级时间轴";
            else
                subLine = "已配对 · 可下载本地对齐模型自动对齐";

            var mid = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = $"《{pairedEbook.Title}》", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            mid.Children.Add(new TextBlock { Text = subLine, FontSize = 10, Foreground = ThemeHelper.Brush(workStatus?.Phase == AudiobookWorkPhase.Failed ? p.Danger : tint.Accent), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            string eid = pairedEbook.Id;
            var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

            if (!alignDone)
            {
                var alignActionBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = workActive ? "暂停" : (Aligner.IsInstalled(_library.DataDir) ? "自动对齐" : "下载对齐"),
                    Height = 26,
                    MinHeight = 26,
                    Padding = new Thickness(9, 0, 9, 0),
                    FontSize = 10.5,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                alignActionBtn.Click += (_, _) =>
                {
                    if (_library.AudiobookWork.IsRunning(_bookId))
                    {
                        _library.AudiobookWork.Stop(_bookId);
                        RenderPairCard();
                    }
                    else
                    {
                        _library.AudiobookWork.Start(_bookId, eid, _globalPosSec, autoDownloadModel: true);
                        RenderPairCard();
                    }
                };
                rightBtns.Children.Add(alignActionBtn);
            }

            var openBtn = new Button
            {
                Content = "切到书页",
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
            var runAlignMi = new MenuFlyoutItem { Text = workActive ? "停止本地强制对齐" : "运行本地强制对齐 (MMS ONNX)" };
            runAlignMi.Click += (_, _) =>
            {
                if (_library.AudiobookWork.IsRunning(_bookId)) _library.AudiobookWork.Stop(_bookId);
                else _library.AudiobookWork.Start(_bookId, eid, _globalPosSec, autoDownloadModel: true);
                RenderPairCard();
            };
            ctx.Items.Add(runAlignMi);
            var pickOtherMi = new MenuFlyoutItem { Text = "换一本配对电子书…" };
            pickOtherMi.Click += async (_, _) => await ShowEbookPairPickerAsync();
            var unpairMi = new MenuFlyoutItem { Text = "解除配对", Foreground = ThemeHelper.Brush(p.Danger) };
            unpairMi.Click += (_, _) =>
            {
                _library.AudiobookWork.Stop(_bookId);
                try { _library.Core.AudiobookPair(_bookId, null); } catch { }
                RenderPairCard();
                ShowToast("已解除电子书配对");
            };
            ctx.Items.Add(pickOtherMi);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(unpairMi);
            PairCardHost.ContextFlyout = ctx;
        }
        else if (candidateEbook is not null)
        {
            PairCardHost.ContextFlyout = null;
            var cov = new Border { Width = 24, Height = 34, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(p.Inset) };
            if (!string.IsNullOrEmpty(candidateEbook.CoverPath) && File.Exists(candidateEbook.CoverPath))
                cov.Child = new Image { Source = new BitmapImage(new Uri(candidateEbook.CoverPath)), Stretch = Stretch.UniformToFill };
            g.Children.Add(cov);

            var mid = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = $"发现《{candidateEbook.Title}》电子书", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            mid.Children.Add(new TextBlock { Text = "一键合成听读书，进度互通", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            string cid = candidateEbook.Id;
            var pairBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                Content = "配对",
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(12, 0, 12, 0),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            pairBtn.Click += (_, _) =>
            {
                try { _library.Core.AudiobookPair(_bookId, cid); } catch { }
                if (Aligner.IsInstalled(_library.DataDir))
                    _library.AudiobookWork.Start(_bookId, cid, _globalPosSec, autoDownloadModel: false);
                RenderPairCard();
                ShowToast($"已与《{candidateEbook.Title}》配对");
            };
            Grid.SetColumn(pairBtn, 2);
            g.Children.Add(pairBtn);
        }
        else
        {
            PairCardHost.ContextFlyout = null;
            g.Children.Add(YYIcons.Make("book", 15, ThemeHelper.Brush(p.T2)));
            var mid = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = "配对电子书", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            mid.Children.Add(new TextBlock { Text = "关联书架电子书后可同步切换阅读", FontSize = 10, Foreground = ThemeHelper.Brush(p.T3) });
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var transcribeBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = "转文字版",
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(9, 0, 9, 0),
                FontSize = 11,
            };
            transcribeBtn.Click += async (_, _) => await ShowMakeTextVersionDialogAsync();
            actionRow.Children.Add(transcribeBtn);

            var pickBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = shelfEbooks.Length > 0 ? "选电子书…" : "去书架",
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 11,
            };
            pickBtn.Click += async (_, _) =>
            {
                if (shelfEbooks.Length > 0) await ShowEbookPairPickerAsync();
                else App.CurrentApp.ShowShelf();
            };
            actionRow.Children.Add(pickBtn);
            Grid.SetColumn(actionRow, 2);
            g.Children.Add(actionRow);
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

    private async Task ShowEbookPairPickerAsync()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var ebooks = _library.GetShelfBooks()
            .Where(b => b.Id != _bookId && b.Format != BookFormat.Audio && b.Format != BookFormat.Pdf && b.Format != BookFormat.Podcast)
            .ToArray();

        ContentDialog? dlgRef = null;
        var searchBox = new TextBox { PlaceholderText = "搜索书名或作者…" };
        var list = new StackPanel { Spacing = 6, Width = 390 };

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
                        ? "书架上还没有可配对的文字电子书（EPUB / TXT / MOBI / AZW3）。先将电子书拖入书架再来配对即可。"
                        : "没有匹配的电子书。",
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T2),
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            foreach (var eb in filtered)
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var meta = new StackPanel { Spacing = 1 };
                meta.Children.Add(new TextBlock { Text = eb.Title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                meta.Children.Add(new TextBlock { Text = eb.Authors.FirstOrDefault() ?? "电子书", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
                row.Children.Add(meta);
                var selTag = new TextBlock { Text = "配对并对齐 ›", FontSize = 11.5, Foreground = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(selTag, 1);
                row.Children.Add(selTag);

                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(12, 8, 12, 8),
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    UseSystemFocusVisuals = false,
                    Content = row,
                };
                string targetId = eb.Id;
                string targetTitle = eb.Title;
                btn.Click += (_, _) =>
                {
                    try { _library.Core.AudiobookPair(_bookId, targetId); } catch { }
                    _library.AudiobookWork.Start(_bookId, targetId, _globalPosSec, autoDownloadModel: true);
                    dlgRef?.Hide();
                    RenderPairCard();
                    ShowToast($"已与《{targetTitle}》配对，正在后台对齐…", "查看进度", () => _ = ShowPairStatusWizardAsync());
                };
                list.Children.Add(btn);
            }
        }

        searchBox.TextChanged += (_, _) => Rebuild(searchBox.Text);
        Rebuild("");

        dlgRef = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "选择要配对的电子书",
            Content = new StackPanel { Width = 400, Spacing = 8, Children = { searchBox, new ScrollViewer { MaxHeight = 320, Content = list } } },
            CloseButtonText = "关闭",
        };
        await dlgRef.ShowAsync();
    }

    private async Task ShowMakeTextVersionDialogAsync()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        using var cts = new CancellationTokenSource();

        var statusTb = new TextBlock
        {
            Text = "没有电子书时，可将录音逐段转写为可划线、可搜索的「文字版」，并自动与这本有声书配对。优先使用本地 Whisper 或已配置的语音转写接口。",
            FontSize = 12,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
        };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
        var detailTb = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap };
        var startBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = "开始转写生成文字版",
        };
        BookSummary? created = null;

        startBtn.Click += async (_, _) =>
        {
            startBtn.IsEnabled = false;
            detailTb.Text = "正在准备转写…";
            try
            {
                created = await Task.Run(async () => await WindowsTranscriber.MakeTextVersionAsync(
                    _library,
                    _bookId,
                    (frac, msg) => DispatcherQueue.TryEnqueue(() =>
                    {
                        bar.Value = Math.Clamp(frac * 100, 0, 100);
                        detailTb.Text = msg;
                    }),
                    cts.Token));
                if (created is not null)
                {
                    RenderPairCard();
                    ShowToast($"已生成文字版《{created.Title}》", "打开阅读", () => OpenSyncedPairedEbook(created.Id));
                }
            }
            catch (OperationCanceledException)
            {
                detailTb.Text = "已取消转写";
            }
            catch (Exception ex)
            {
                detailTb.Text = ex.Message;
                startBtn.IsEnabled = true;
            }
        };

        var dlg = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "转成文字版",
            Content = new StackPanel
            {
                Width = 390,
                Spacing = 10,
                Children = { statusTb, bar, detailTb, startBtn },
            },
            CloseButtonText = "关闭",
        };
        await dlg.ShowAsync();
        cts.Cancel();
    }

    /// <summary>
    /// 1:1 <c>AudiobookViews.swift</c> (<c>AudiobookPage</c> making/result summary):
    /// displays alignment progress, extra intro detection (&gt;15s) with preview &amp; skip toggle, and unread sentence count.
    /// </summary>
    private async Task ShowPairStatusWizardAsync()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var prog = _library.Core.AudiobookProgress(_bookId);
        var map = _library.Core.AudiobookMap(_bookId);
        _cachedMap = map;
        var extras = _library.Core.AudiobookExtras(_bookId);
        var skip = _library.Core.AudiobookSkipSettings(_bookId);
        var host = new StackPanel { Width = 420, Spacing = 10 };

        double progFrac = prog.Done ? 1.0
            : (prog.Sentences > 0 ? Math.Clamp((prog.Exact + prog.Spread * 0.5) / prog.Sentences, 0, 1)
                : (prog.Seconds > 1 ? Math.Clamp(prog.Transcribed / prog.Seconds, 0, 1) : 0));
        host.Children.Add(new TextBlock
        {
            Text = prog.Done ? "✓ 全书听读对齐已完成" : $"正在对齐中（已完成 {Math.Round(progFrac * 100)}%）",
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(prog.Done ? p.Ok : tint.Accent),
        });

        if (map is not null)
        {
            int unreadCount = map.Rows.Count(r => r.By == SyncBy.Unread);
            int mappedCount = map.Rows.Count(r => r.By != SyncBy.Unread);
            host.Children.Add(new TextBlock
            {
                Text = $"共对齐 {mappedCount} 句 · 未念/略过 {unreadCount} 句 · 共 {map.Chapters.Length} 章",
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T2),
            });
        }

        // Intro detection (>15s) with audition + skip toggle (1:1 AudiobookViews.swift:747-780)
        var headExtra = extras.FirstOrDefault(x => x.Place == ExtraPlace.Head && (x.T1 - x.T0) >= 15.0);
        if (headExtra is not null)
        {
            double dur = headExtra.T1 - headExtra.T0;
            var introStack = new StackPanel { Spacing = 6 };
            introStack.Children.Add(new TextBlock { Text = $"检测到 {dur:0} 秒开场片头", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var tryIntro = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "▶ 试听片头", FontSize = 11.5 };
            tryIntro.Click += (_, _) => { _ignoreExtraUntilSec = headExtra.T1; _ = SeekGlobalAsync(headExtra.T0, startPlaying: true); };
            var skipIntroBtn = new Button { Style = (Style)Application.Current.Resources[skip.HeadTail ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"], Content = skip.HeadTail ? "✓ 已自动跳过片头" : "自动跳过这段", FontSize = 11.5 };
            skipIntroBtn.Click += (_, _) =>
            {
                skip = skip with { HeadTail = !skip.HeadTail };
                try { _library.Core.AudiobookSetSkipSettings(_bookId, skip); } catch { }
                skipIntroBtn.Content = skip.HeadTail ? "✓ 已自动跳过片头" : "自动跳过这段";
            };
            row.Children.Add(tryIntro);
            row.Children.Add(skipIntroBtn);
            introStack.Children.Add(row);
            host.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(12, 10, 12, 10), Child = introStack });
        }

        await new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "听读书配对与对齐",
            Content = host,
            CloseButtonText = "完成",
        }.ShowAsync();
    }

    private void RecordingSettingsFlyout_Opened(object sender, object e) => RebuildRecordingSettings();

    private void RebuildRecordingSettings()
    {
        RecordingSettingsHost.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        SkipSettings s;
        try { s = _library.Core.AudiobookSkipSettings(_bookId); }
        catch { s = new SkipSettings(true, false, false, false, false, false, false); }

        double savedSec = 0;
        try { savedSec = _library.Core.AudiobookSaved(_bookId); } catch { }

        int unreadCount = 0;
        try { _cachedMap = _library.Core.AudiobookMap(_bookId); unreadCount = _cachedMap?.Rows.Count(r => r.By == SyncBy.Unread) ?? 0; } catch { }

        RecordingSettingsHost.Children.Add(new TextBlock
        {
            Text = "这本录音",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ThemeHelper.Brush(p.T1),
        });

        void SaveSkip(SkipSettings next, bool reloadAudio = false)
        {
            s = next;
            try { _library.Core.AudiobookSetSkipSettings(_bookId, s); } catch { }
            if (reloadAudio) _ = SeekGlobalAsync(_globalPosSec, _playing, forceReload: true);
            RebuildRecordingSettings();
        }

        Grid MakeToggleRow(string name, string? detail, bool value, Action<bool> onToggle)
        {
            var g = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sp = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = ThemeHelper.Brush(p.T1) });
            if (!string.IsNullOrEmpty(detail))
                sp.Children.Add(new TextBlock { Text = detail, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
            g.Children.Add(sp);
            var sw = new ToggleSwitch { IsOn = value, MinWidth = 0, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
            sw.Toggled += (_, _) => onToggle(sw.IsOn);
            Grid.SetColumn(sw, 1);
            g.Children.Add(sw);
            return g;
        }

        RecordingSettingsHost.Children.Add(MakeToggleRow("跳过片头片尾", "自动跳过每集开场与结尾口播", s.HeadTail, v => SaveSkip(s with { HeadTail = v })));
        RecordingSettingsHost.Children.Add(MakeToggleRow("跳过中间非正文", "跳过播讲人插入的广告或闲聊", s.Middle, v => SaveSkip(s with { Middle = v })));
        RecordingSettingsHost.Children.Add(MakeToggleRow("压短章间音乐", "章间过场音乐压到 1.5 秒", s.ShortenChapterGaps, v => SaveSkip(s with { ShortenChapterGaps = v })));
        RecordingSettingsHost.Children.Add(MakeToggleRow("人声清楚一点", "80Hz 低切 + 3kHz 人声清晰度 + 3:1 动态压缩", s.ClearerVoice, v => SaveSkip(s with { ClearerVoice = v }, reloadAudio: true)));
        RecordingSettingsHost.Children.Add(MakeToggleRow(
            "缩短停顿",
            savedSec >= 60 ? $"这本已经省了 {Math.Max(1, (int)Math.Round(savedSec / 60.0))} 分钟" : "长停顿自动压短一些",
            s.ShortenPauses,
            v => SaveSkip(s with { ShortenPauses = v })));
        RecordingSettingsHost.Children.Add(MakeToggleRow(
            "没念的用朗读补上",
            unreadCount > 0 ? $"这本有 {unreadCount} 句原声未念，书页跟读时自动用朗读补齐" : "与电子书配对时补齐漏读句",
            s.FillUnread,
            v => SaveSkip(s with { FillUnread = v })));
        RecordingSettingsHost.Children.Add(MakeToggleRow("逐词跟读", "在配对书页上按词推进下划线", s.WordFollow, v => SaveSkip(s with { WordFollow = v })));

        RecordingSettingsHost.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 2, 0, 2) });

        var pairStatusBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = "听读书配对与片头状态…",
            FontSize = 11.5,
        };
        pairStatusBtn.Click += async (_, _) =>
        {
            RecordingSettingsFlyout.Hide();
            await ShowPairStatusWizardAsync();
        };
        RecordingSettingsHost.Children.Add(pairStatusBtn);

        var alignBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = "对齐检查与微调…",
            FontSize = 11.5,
        };
        alignBtn.Click += (_, _) =>
        {
            RecordingSettingsFlyout.Hide();
            PausePlayback();
            AlignCheckAndPolishService.OpenAlignCheckWindow(_library, _bookId, 0, () => RenderPairCard());
        };
        RecordingSettingsHost.Children.Add(alignBtn);

        ListenPair? curPair = null;
        try { curPair = _library.Core.ListeningPairs().FirstOrDefault(lp => lp.Recording == _bookId); } catch { }
        if (curPair is not null && _library.Core.AudiobookIsTextVersion(curPair.Book))
        {
            string pairedTextId = curPair.Book;
            var polishBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = "整理文字版错字…",
                FontSize = 11.5,
            };
            polishBtn.Click += async (_, _) =>
            {
                RecordingSettingsFlyout.Hide();
                await AlignCheckAndPolishService.ShowTextPolishDialogAsync(RootGrid.XamlRoot, _library, pairedTextId);
            };
            RecordingSettingsHost.Children.Add(polishBtn);
        }

        var exportBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = "导出带朗读时间的电子书 (.epub)…",
            FontSize = 11.5,
        };
        exportBtn.Click += async (_, _) =>
        {
            RecordingSettingsFlyout.Hide();
            var picker = new FileSavePicker { SuggestedFileName = _summary.Title };
            picker.FileTypeChoices.Add("EPUB 电子书", [".epub"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var outFile = await picker.PickSaveFileAsync();
            if (outFile is null) return;
            ShowToast("正在打包带音频时间轴的 EPUB…");
            try
            {
                await Task.Run(() => _library.Core.AudiobookExportEpub(_bookId, outFile.Path));
                ShowToast("✓ 已导出有声电子书 (.epub)");
            }
            catch (Exception ex)
            {
                ShowToast($"导出失败（需先配对并对齐电子书）：{ex.Message}");
            }
        };
        RecordingSettingsHost.Children.Add(exportBtn);
    }

    private async Task RenameChapterAsync(int chapterIdx)
    {
        var box = new TextBox { Text = ChapterTitle(chapterIdx), PlaceholderText = "留空恢复默认“第 N 集”" };
        var dlg = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "改名",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            _summary = _library.Core.RenameAudioChapter(_bookId, (uint)chapterIdx, string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
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
            App.CurrentApp.ClaimAudioPlayback(this, () => DispatcherQueue.TryEnqueue(PausePlayback));
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
            _listenSinceUtc = DateTime.UtcNow;
            _sleepLastTickUtc = DateTime.UtcNow;
            UpdateTransportUi();
        }
    }

    private async void Rewind15_Click(object sender, RoutedEventArgs e) => await SeekGlobalAsync(_globalPosSec - 15, _playing);
    private async void Forward30_Click(object sender, RoutedEventArgs e) => await SeekGlobalAsync(_globalPosSec + 30, _playing);

    private async Task StepSentenceOrSeekAsync(bool forward, bool forceSeconds)
    {
        if (_isSleepFading) { ExtendSleepTenMin(); return; }
        if (!forceSeconds && _cachedMap is not null)
        {
            try
            {
                double? stepped = _library.Core.AudiobookStep(_bookId, _globalPosSec, forward);
                if (stepped is double s) { await SeekGlobalAsync(s, _playing); return; }
            }
            catch { }
        }
        await SeekGlobalAsync(_globalPosSec + (forward ? 30 : -15), _playing);
    }

    private async void ChapterSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlider || _recording is null) return;
        var (chStart, _) = CurrentChapterBounds();
        double target = chStart + e.NewValue;
        if (Math.Abs(target - _globalPosSec) > 1.2)
            await SeekGlobalAsync(target, _playing);
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
                _player.PlaybackSession.PlaybackRate = _rate;
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

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == VirtualKey.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }
        if (e.Key == VirtualKey.Escape && AppWindow?.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            try { AppWindow.SetPresenter(AppWindowPresenterKind.Default); } catch { }
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == VirtualKey.K)
        {
            _ = QuickSearchDialog.ShowAsync(RootGrid.XamlRoot, _library, _bookId);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == (VirtualKey)188)
        {
            App.CurrentApp.OpenSettings();
            e.Handled = true;
            return;
        }

        if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox)
            return;

        if (ctrl && e.Key == VirtualKey.D)
        {
            AddBookmarkAtCurrentPosition();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == VirtualKey.H)
        {
            HighlightHeardOrBookmark();
            e.Handled = true;
            return;
        }

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
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_recording is null) return;
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
        try { _windowCts.Cancel(); _windowCts.Dispose(); } catch { }
        _library.AudiobookWork.StatusChanged -= OnAudiobookWorkStatusChanged;
        _uiTimer.Stop();
        _listenTimeTracker.Stop();
        FlushListeningTime();
        _library.SystemIntegration.SetAudioPlaying(this, false);
        SaveCurrentProgress();
        try { _player.Pause(); _player.Dispose(); } catch { }
    }
}
