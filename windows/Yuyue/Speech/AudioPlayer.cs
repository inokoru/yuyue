using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;
using Yuyue.Core;

namespace Yuyue.Speech;

public enum SpeechVoiceKind
{
    Kokoro,
    BuiltinCard,
    ClonedCard,
    BookCard,
    Cloud,
    System,
}

public sealed record SpeechVoiceOption(
    string Id,
    string Label,
    bool IsNeural,
    SpeechVoiceKind Kind = SpeechVoiceKind.System,
    string? Gender = null,
    string? Language = null);

/// <summary>
/// Unified sentence &amp; paired-audiobook audio player (1:1 <c>apple/Yuyue/Speech/Player.swift</c>):
/// <list type="bullet">
///   <item>On-device neural <see cref="VoiceCard"/> voices (ZipVoice-Distill-Int8 + Vocos 24 kHz, built-in/cloned/book-designed)</item>
///   <item>Cloud &amp; local-LAN TTS voices (<see cref="CloudVoiceService"/>)</item>
///   <item>Windows system voices with <c>SpokenLanguage</c> regional fallback</item>
///   <item>DramaMixer live &amp; studio playback (<see cref="DramaSynthService"/>) + role-segmented fallback</item>
///   <item>Paired human audiobook recording (<c>原声 / 听读书</c>) with word-by-word karaoke, <c>VoiceTapDsp</c>,
///     <c>FillUnread</c>, <c>AudiobookResumeAt</c>, and skip notices</item>
///   <item>20-second linear sleep fade-out, <c>ExtendSleep</c> (+10 min), between-sentence stop, A–B range loop,
///     SMTC <c>MarkHeard</c>, cancellable singleton previews, and error-recovery fallback</item>
/// </list>
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private const double SleepFadeSeconds = 20.0;

    private static readonly object PreviewGate = new();
    private static CancellationTokenSource? ActivePreviewCts;
    private static AudioPlayer? ActivePreviewPlayer;

    private readonly LibraryService? _library;
    private readonly MediaPlayer _player;
    private readonly SpeechSynthesizer _synth;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;
    private readonly Microsoft.UI.Xaml.DispatcherTimer? _tickTimer;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _prefetchCts;
    private readonly Dictionary<int, Task<PreparedClip?>> _prefetchTasks = new();
    private readonly object _prefetchLock = new();
    private double _currentRawDurationMs;
    private string? _coverPath;

    private IReadOnlyList<string> _sentences = [];
    private int _currentIndex = -1;
    private DateTime _sentenceStartedUtc = DateTime.UtcNow;
    private bool _singleSentenceMode;
    private bool _loopSingleSentence;
    private int? _loopRangeStart;
    private int? _loopRangeEnd;
    private int _shadowPauseMode; // 0=off, 1=2s, 2=match sentence duration (capped 20s)
    private bool _stopAfterSentence;
    private double _rate = 1.0;
    private bool _playing;
    private string _bookTitle = "语阅";
    private string _chapterTitle = "";
    private string? _bookId;
    private uint? _sectionIndex;
    private bool _dramaMode;
    private uint? _cachedDramaSectionIndex;
    private Dictionary<uint, DramaAct>? _cachedSectionActs;
    private string _language = "zh-Hans";
    private string? _selectedKokoroId;
    private string? _selectedCardId;
    private string? _selectedCloudId;
    private string _voiceDisplayName = "系统语音";

    // Sleep timer with 20s linear volume fade & pause-aware countdown
    private int _sleepMode; // 0=off, 1=timed, 2=endOfChapter, 3=afterSentence
    private double _sleepRemainingSec;
    private DateTime _sleepLastTickUtc = DateTime.UtcNow;
    private bool _isSleepFading;

    // Paired human audiobook recording state ("原声" inside ReaderWindow)
    private string? _pairedAudioBookId;
    private AudioRecording? _pairedRecording;
    private SyncMap? _pairedMap;
    private bool _usePairedRecording;
    private int _pairedFileIndex = -1;
    private double _pairedGlobalPosSec;
    private double[] _pairedFileOffsets = [];
    private DateTime? _pairedPausedUtc;
    private double _pairedLastMomentSkipTarget = -1;
    private double _pairedIgnoreExtraUntilSec = -1;
    private bool _pairedSpeakingUnread;
    private int _pairedLastReportedSentence = -1;
    private uint _pairedLastReportedSection = uint.MaxValue;
    private double _pairedSentenceEndStopSec = -1;
    private double _pairedBaseVolume = 1.0;

    private sealed record PreparedClip(MediaSource Source, double RawDurationMs);

    public event Action<int, int, bool>? SentenceStarted;
    public event Action<int, int, int, bool>? SentenceProgressSynced;
    public event Action<bool>? PlayStateChanged;
    public event Action? SectionFinished;
    public event Action? SentenceFinished;
    public event Action<int>? MarkHeardRequested;
    public event Action<string>? SynthesisError;
    public event Action? SleepStateChanged;
    public event Action<string>? SleepStopped;
    public event Action<uint, int, int, int, bool, int[]?>? PairedSentenceActive;
    public event Action<string?, string?, Action?>? ExtraNoticeChanged;

    public bool IsPlaying => _playing;
    public int CurrentSentence => _currentIndex;
    public double Rate => _rate;
    public string VoiceName => IsUsingPairedRecording ? "原声录音" : _voiceDisplayName;
    public bool LoopSingleSentence { get => _loopSingleSentence; set => _loopSingleSentence = value; }
    public int? LoopRangeStart => _loopRangeStart;
    public int? LoopRangeEnd => _loopRangeEnd;
    public bool HasLoopRange => _loopRangeStart.HasValue && _loopRangeEnd.HasValue;
    public int ShadowPauseMode { get => _shadowPauseMode; set => _shadowPauseMode = Math.Clamp(value, 0, 2); }
    public bool StopAfterCurrentSentence { get => _stopAfterSentence; set => _stopAfterSentence = value; }
    public bool HeadphoneMarkMode { get; set; } = true;

    public int SleepMode => _sleepMode;
    public double SleepSecondsRemaining => _sleepRemainingSec;
    public bool IsSleepFading => _isSleepFading;

    public bool HasPairedRecording => _pairedRecording is not null && !string.IsNullOrEmpty(_pairedAudioBookId);
    public bool IsUsingPairedRecording => HasPairedRecording && _usePairedRecording;
    public string? PairedAudioBookId => _pairedAudioBookId;
    public double CurrentRecordingSeconds => _pairedGlobalPosSec;

    public static IReadOnlyList<VoiceInformation> AvailableVoices => SpeechSynthesizer.AllVoices.ToArray();

    public static IReadOnlyList<VoiceInformation> GetSystemVoicesForLanguage(string? language)
    {
        var all = AvailableVoices;
        if (all.Count == 0) return [];
        string norm = (language ?? "zh-Hans").Replace('_', '-').Trim().ToLowerInvariant();

        string[] prefixes = norm switch
        {
            var s when s.StartsWith("zh-hk") || s.StartsWith("zh-mo") || s == "yue" => ["zh-hk", "zh-tw", "zh"],
            var s when s.StartsWith("zh-tw") || s.StartsWith("zh-hant") => ["zh-tw", "zh-cn", "zh"],
            var s when s.StartsWith("zh") || s == "cmn" => ["zh-cn", "zh-hans", "zh"],
            var s when s.StartsWith("en") => ["en-us", "en-gb", "en"],
            var s when s.StartsWith("ja") => ["ja-jp", "ja"],
            var s when s.StartsWith("ko") => ["ko-kr", "ko"],
            var s when s.StartsWith("fr") => ["fr-fr", "fr"],
            var s when s.StartsWith("de") => ["de-de", "de"],
            var s when s.StartsWith("es") => ["es-es", "es"],
            _ => [norm.Split('-')[0], "en", "zh"],
        };

        var matched = new List<VoiceInformation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string pfx in prefixes)
        {
            foreach (var v in all)
            {
                if (v.Language.StartsWith(pfx, StringComparison.OrdinalIgnoreCase) && seen.Add(v.Id))
                    matched.Add(v);
            }
            if (matched.Count > 0) break;
        }

        if (matched.Count == 0) matched.AddRange(all);
        return matched
            .OrderByDescending(v => v.DisplayName.Contains("Natural", StringComparison.OrdinalIgnoreCase) ||
                                    v.DisplayName.Contains("Xiaoxiao", StringComparison.OrdinalIgnoreCase) ||
                                    v.DisplayName.Contains("Yunxi", StringComparison.OrdinalIgnoreCase) ||
                                    v.DisplayName.Contains("Jenny", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public static async Task<byte[]?> SynthesizeSystemVoiceToWavAsync(string text, string? sysVoiceId, string? language, CancellationToken ct = default)
    {
        string plain = CardVoiceEngine.PlainSpokenText(text);
        if (!plain.Any(char.IsLetterOrDigit)) return null;
        try
        {
            using var synth = new SpeechSynthesizer();
            var match = !string.IsNullOrEmpty(sysVoiceId)
                ? AvailableVoices.FirstOrDefault(v => v.Id == sysVoiceId)
                : BestSystemVoice(language);
            if (match is not null) synth.Voice = match;
            using var stream = await synth.SynthesizeTextToStreamAsync(plain).AsTask(ct);
            byte[] bytes = new byte[stream.Size];
            using var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)bytes.Length).AsTask(ct);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch { return null; }
    }

    public static VoiceInformation? BestSystemVoice(string? language) =>
        GetSystemVoicesForLanguage(language).FirstOrDefault();

    public static IReadOnlyList<SpeechVoiceOption> GetAllVoiceOptions(LibraryService library, string? bookId = null)
    {
        var list = new List<SpeechVoiceOption>();

        if (library.Kokoro.IsInstalled)
        {
            foreach (var s in library.Kokoro.GetAllSpeakers())
            {
                string? g = s.Gender == CharacterGender.M ? "male" : (s.Gender == CharacterGender.F ? "female" : null);
                list.Add(new SpeechVoiceOption(s.Id, $"✦ {s.Name}（Kokoro 离线 · {s.Summary}）", true, SpeechVoiceKind.Kokoro, g, s.Lang));
            }
        }

        if (library.LiveVoice.IsReady)
        {
            foreach (var c in library.LiveVoice.GetBuiltinCards())
            {
                string tag = c.Tags.Length > 0 ? $" · {string.Join(" / ", c.Tags.Take(2))}" : "";
                string? g = c.Gender == CharacterGender.M ? "male" : (c.Gender == CharacterGender.F ? "female" : null);
                list.Add(new SpeechVoiceOption(c.Id, $"✦ {c.Name}（离线神经语音{tag}）", true, SpeechVoiceKind.BuiltinCard, g, c.Lang));
            }
        }

        // Book-designed actor cards (card.d.*)
        if (!string.IsNullOrEmpty(bookId))
        {
            try
            {
                foreach (var c in library.Core.CardsOfBook(bookId))
                {
                    string? g = c.Gender == CharacterGender.M ? "male" : (c.Gender == CharacterGender.F ? "female" : null);
                    list.Add(new SpeechVoiceOption(c.Id, $"✦ {c.Name}（本书角色音）", true, SpeechVoiceKind.BookCard, g, c.Lang));
                }
            }
            catch { }
        }

        // Cloned cards (available whenever ZipVoice model OR ElevenLabs is configured)
        foreach (var c in library.LiveVoice.GetClonedCards())
        {
            string? g = c.Gender == CharacterGender.M ? "male" : (c.Gender == CharacterGender.F ? "female" : null);
            list.Add(new SpeechVoiceOption(c.Id, $"✦ {c.Name}（克隆声音）", true, SpeechVoiceKind.ClonedCard, g, c.Lang));
        }

        foreach (var cv in library.CloudVoices.GetAllConfiguredVoices())
        {
            string pLabel = CloudVoiceService.ProviderDisplayName(cv.Provider, library.Prefs);
            bool recommended = cv.Provider is CloudTtsProvider.Edge or CloudTtsProvider.Free or CloudTtsProvider.Volcengine or CloudTtsProvider.Fish;
            list.Add(new SpeechVoiceOption(cv.FullId, $"☁ {cv.DisplayName}（{pLabel}）", recommended, SpeechVoiceKind.Cloud, cv.Gender, cv.Language));
        }

        foreach (var v in AvailableVoices)
        {
            string g = v.Gender == VoiceGender.Male ? "male" : "female";
            list.Add(new SpeechVoiceOption($"sys:{v.Id}", $"{v.DisplayName} ({v.Language}) · 系统自带（不推荐）", false, SpeechVoiceKind.System, g, v.Language));
        }
        return list;
    }

    public AudioPlayer(LibraryService library) : this(library, library.Prefs.VoiceId) { }

    public AudioPlayer(string? preferredVoiceId = null) : this(null, preferredVoiceId) { }

    private AudioPlayer(LibraryService? library, string? preferredVoiceId)
    {
        _library = library;
        _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _synth = new SpeechSynthesizer();
        SetVoice(preferredVoiceId);

        _player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Speech,
            AutoPlay = true,
        };
        _player.CommandManager.IsEnabled = false;
        _player.MediaEnded += OnMediaEnded;

        var smtc = _player.SystemMediaTransportControls;
        smtc.IsEnabled = true;
        smtc.IsPlayEnabled = true;
        smtc.IsPauseEnabled = true;
        smtc.IsNextEnabled = true;
        smtc.IsPreviousEnabled = true;
        smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        smtc.ButtonPressed += OnSmtcButtonPressed;

        if (_uiQueue is not null)
        {
            _tickTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(100) };
            _tickTimer.Tick += OnTickTimer;
        }
    }

    public void SetBookContext(string? bookId, string? language)
    {
        _bookId = bookId;
        _language = string.IsNullOrWhiteSpace(language) ? "zh-Hans" : language;
        _cachedDramaSectionIndex = null;
        _cachedSectionActs = null;
        if (_library is not null)
            SetVoice(_library.Prefs.GetVoiceForLanguage(_language));
    }

    public void SetSectionContext(uint sectionIndex, bool dramaMode)
    {
        if (_sectionIndex != sectionIndex || _dramaMode != dramaMode)
        {
            _sectionIndex = sectionIndex;
            _dramaMode = dramaMode;
            _cachedDramaSectionIndex = null;
            _cachedSectionActs = null;
            ClearPrefetch();
        }
    }

    public void InvalidateRolesAndSpeakers()
    {
        if (!string.IsNullOrEmpty(_bookId)) DramaSynthService.InvalidateCast(_bookId);
        _cachedDramaSectionIndex = null;
        _cachedSectionActs = null;
        ClearPrefetch();
        if (_playing && !_usePairedRecording) ScheduleLookahead(_currentIndex + 1);
    }

    public void UpdateSentencesInPlace(IReadOnlyList<string> sentences)
    {
        _sentences = sentences;
        ClearPrefetch();
        if (_playing && !_usePairedRecording) ScheduleLookahead(_currentIndex + 1);
    }

    public void SetLoopRange(int startSentence, int endSentence)
    {
        _loopSingleSentence = false;
        _loopRangeStart = Math.Min(startSentence, endSentence);
        _loopRangeEnd = Math.Max(startSentence, endSentence);
    }

    public void ClearLoopRange()
    {
        _loopRangeStart = null;
        _loopRangeEnd = null;
    }

    // ---------- 20-Second Linear Sleep Fade-Out & ExtendSleep (1:1 Player.swift) ----------

    public void StartSleepTimer(int minutes)
    {
        _sleepMode = 1;
        _sleepRemainingSec = Math.Max(1, minutes) * 60.0;
        _sleepLastTickUtc = DateTime.UtcNow;
        _isSleepFading = false;
        _stopAfterSentence = false;
        _pairedSentenceEndStopSec = -1;
        RestoreVolume();
        EnsureTickTimer();
        SleepStateChanged?.Invoke();
    }

    public void SetSleepEndOfChapter()
    {
        _sleepMode = 2;
        _sleepRemainingSec = 0;
        _isSleepFading = false;
        _stopAfterSentence = false;
        _pairedSentenceEndStopSec = -1;
        RestoreVolume();
        SleepStateChanged?.Invoke();
    }

    public void SetSleepAfterSentence()
    {
        _sleepMode = 3;
        _sleepRemainingSec = 0;
        _isSleepFading = false;
        _stopAfterSentence = true;
        RestoreVolume();
        if (IsUsingPairedRecording && _library is not null && !string.IsNullOrEmpty(_pairedAudioBookId))
        {
            try
            {
                var bounds = _library.Core.AudiobookSentenceBounds(_pairedAudioBookId, _pairedGlobalPosSec);
                _pairedSentenceEndStopSec = bounds is not null ? bounds.T1 : _pairedGlobalPosSec + 0.5;
            }
            catch { _pairedSentenceEndStopSec = _pairedGlobalPosSec + 0.5; }
        }
        SleepStateChanged?.Invoke();
    }

    public void ExtendSleep(int addMinutes = 10)
    {
        _sleepMode = 1;
        _sleepRemainingSec = Math.Max(0, _sleepRemainingSec) + addMinutes * 60.0;
        _sleepLastTickUtc = DateTime.UtcNow;
        _isSleepFading = false;
        _stopAfterSentence = false;
        _pairedSentenceEndStopSec = -1;
        RestoreVolume();
        EnsureTickTimer();
        SleepStateChanged?.Invoke();
    }

    public void CancelSleepTimer()
    {
        _sleepMode = 0;
        _sleepRemainingSec = 0;
        _isSleepFading = false;
        _stopAfterSentence = false;
        _pairedSentenceEndStopSec = -1;
        RestoreVolume();
        SleepStateChanged?.Invoke();
    }

    private void RestoreVolume()
    {
        try { _player.Volume = IsUsingPairedRecording ? _pairedBaseVolume : 1.0; }
        catch { }
    }

    private void EnsureTickTimer()
    {
        if (_tickTimer is not null && !_tickTimer.IsEnabled)
            _tickTimer.Start();
    }

    // ---------- Paired Audiobook Recording Mode ("原声 / 听读书") ----------

    public void SetPairedRecording(string? audioBookId, AudioRecording? recording, SyncMap? map, bool useRecording)
    {
        bool wasPlaying = _playing;
        int curSent = Math.Max(0, _currentIndex);
        if (wasPlaying) Pause();

        _pairedAudioBookId = audioBookId;
        _pairedRecording = recording;
        _pairedMap = map;
        _usePairedRecording = useRecording && recording is not null && !string.IsNullOrEmpty(audioBookId);
        _pairedFileIndex = -1;
        _pairedLastReportedSentence = -1;
        _pairedLastReportedSection = uint.MaxValue;
        _pairedLastMomentSkipTarget = -1;
        _pairedIgnoreExtraUntilSec = -1;
        _pairedSpeakingUnread = false;

        if (recording is not null && recording.Files.Length > 0)
        {
            _pairedFileOffsets = new double[recording.Files.Length];
            double acc = 0;
            for (int i = 0; i < recording.Files.Length; i++)
            {
                _pairedFileOffsets[i] = acc;
                acc += Math.Max(0.1, recording.Files[i].Seconds);
            }
            if (_library is not null && !string.IsNullOrEmpty(audioBookId) &&
                _library.Prefs.AudiobookLoudnessGain.TryGetValue(audioBookId, out float gain))
            {
                _pairedBaseVolume = Math.Clamp(gain, 0.35, 1.0);
            }
            else
            {
                _pairedBaseVolume = 1.0;
            }
        }
        else
        {
            _pairedFileOffsets = [];
            _pairedBaseVolume = 1.0;
        }

        if (wasPlaying && _sentences.Count > 0)
            Start(_sentences, curSent);
    }

    public double? LookupPairedTimeForSentence(uint section, int sentence)
    {
        if (_pairedMap is null) return null;
        var exact = _pairedMap.Rows.FirstOrDefault(r => r.Section == section && r.Sentence == (uint)sentence && r.By != SyncBy.Unread);
        if (exact is not null) return exact.T0Ms / 1000.0;

        var nearby = _pairedMap.Rows
            .Where(r => r.Section == section && r.By != SyncBy.Unread)
            .OrderBy(r => Math.Abs((int)r.Sentence - sentence))
            .FirstOrDefault();
        if (nearby is not null) return nearby.T0Ms / 1000.0;

        var ch = _pairedMap.Chapters.FirstOrDefault(c => c.BookSection == section);
        return ch?.T0;
    }

    public void SeekRecordingTo(double globalSeconds)
    {
        if (!IsUsingPairedRecording || _pairedRecording is null || _pairedFileOffsets.Length == 0) return;
        double total = _pairedRecording.Files.Sum(f => f.Seconds);
        double target = Math.Clamp(globalSeconds, 0, Math.Max(0, total - 0.1));
        _pairedGlobalPosSec = target;

        int fileIdx = 0;
        for (int i = 0; i < _pairedFileOffsets.Length; i++)
        {
            if (target >= _pairedFileOffsets[i]) fileIdx = i;
            else break;
        }
        double localSec = Math.Max(0, target - _pairedFileOffsets[fileIdx]);
        LoadAndPlayPairedFile(fileIdx, localSec, autoPlay: _playing);
    }

    public void SkipExtraNow()
    {
        if (!IsUsingPairedRecording || _library is null || string.IsNullOrEmpty(_pairedAudioBookId)) return;
        try
        {
            var m = _library.Core.AudiobookMoment(_pairedAudioBookId, _pairedGlobalPosSec);
            if (m.Extra is { } ex)
            {
                _pairedIgnoreExtraUntilSec = -1;
                SeekRecordingTo(ex.T1 + 0.05);
                ExtraNoticeChanged?.Invoke(null, null, null);
            }
        }
        catch { }
    }

    public void PlaySkippedHeadNow(double fromSeconds)
    {
        if (!IsUsingPairedRecording || _library is null || string.IsNullOrEmpty(_pairedAudioBookId)) return;
        try
        {
            var m = _library.Core.AudiobookMoment(_pairedAudioBookId, fromSeconds);
            _pairedIgnoreExtraUntilSec = m.Extra?.T1 ?? (fromSeconds + 30.0);
        }
        catch { _pairedIgnoreExtraUntilSec = fromSeconds + 30.0; }
        SeekRecordingTo(fromSeconds);
        ExtraNoticeChanged?.Invoke(null, null, null);
    }

    private void LoadAndPlayPairedFile(int fileIdx, double localSec, bool autoPlay)
    {
        if (_pairedRecording is null || fileIdx < 0 || fileIdx >= _pairedRecording.Files.Length || _library is null)
            return;

        string rel = _pairedRecording.Files[fileIdx].Path;
        string fullPath = Path.IsPathRooted(rel) ? rel : Path.Combine(_library.DataDir, rel);
        if (!File.Exists(fullPath))
        {
            SynthesisError?.Invoke("找不到已配对的有声书音频文件，将改用朗读继续。");
            _usePairedRecording = false;
            _ = SpeakCurrentAsync();
            return;
        }

        try
        {
            bool clearer = false;
            try { clearer = _library.Core.AudiobookSkipSettings(_pairedAudioBookId!).ClearerVoice; } catch { }

            string playPath = fullPath;
            if (clearer)
            {
                string dspPath = Path.Combine(_library.DataDir, "audiobooks", _pairedAudioBookId!, $"clear-{fileIdx}.wav");
                if (File.Exists(dspPath))
                {
                    playPath = dspPath;
                }
                else
                {
                    // Kick off background VoiceTapDsp clearer-voice WAV render for seamless upgrade
                    string srcCapture = fullPath;
                    string bookCapture = _pairedAudioBookId!;
                    int idxCapture = fileIdx;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string scratch = Path.Combine(_library.DataDir, "audiobooks", bookCapture, "pcm-cache");
                            string pcm = await Aligner.EnsurePcm16kWavAsync(srcCapture, scratch, idxCapture, CancellationToken.None);
                            float gain = VoiceTapDsp.MeasureLoudnessFromWav(pcm);
                            _library.Prefs.AudiobookLoudnessGain[bookCapture] = gain;
                            VoiceTapDsp.ProcessWavFile(pcm, dspPath, gain, clearer: true);
                        }
                        catch { }
                    });
                }
            }

            if (_pairedFileIndex != fileIdx || _player.Source is null)
            {
                _pairedFileIndex = fileIdx;
                _player.AutoPlay = autoPlay;
                _player.Source = MediaSource.CreateFromUri(new Uri(playPath));
            }
            _player.Volume = _isSleepFading ? _player.Volume : _pairedBaseVolume;
            _player.PlaybackSession.PlaybackRate = _rate;
            _player.PlaybackSession.Position = System.TimeSpan.FromSeconds(localSec);
            if (autoPlay) _player.Play();
            EnsureTickTimer();
        }
        catch (Exception ex)
        {
            SynthesisError?.Invoke($"原声播放失败：{ex.Message}");
        }
    }

    private void OnTickTimer(object? sender, object e)
    {
        // 1. Sleep timer with 20s linear fade-out (pause-aware!)
        if (_sleepMode == 1 && _playing)
        {
            var now = DateTime.UtcNow;
            double dt = Math.Clamp((now - _sleepLastTickUtc).TotalSeconds, 0, 2.0);
            _sleepLastTickUtc = now;
            _sleepRemainingSec = Math.Max(0, _sleepRemainingSec - dt);

            if (_sleepRemainingSec <= SleepFadeSeconds && _sleepRemainingSec > 0)
            {
                _isSleepFading = true;
                double frac = Math.Clamp(_sleepRemainingSec / SleepFadeSeconds, 0.05, 1.0);
                double baseVol = IsUsingPairedRecording ? _pairedBaseVolume : 1.0;
                try { _player.Volume = baseVol * frac; } catch { }
            }
            else if (_sleepRemainingSec <= 0)
            {
                _sleepMode = 0;
                _isSleepFading = false;
                if (IsUsingPairedRecording)
                {
                    Pause();
                    RestoreVolume();
                    SleepStopped?.Invoke("定时已到，已暂停并记住位置");
                }
                else
                {
                    // Clean between-sentence stop for TTS (1:1 Player.swift)
                    _stopAfterSentence = true;
                }
                SleepStateChanged?.Invoke();
                return;
            }
        }
        else if (_sleepMode == 1)
        {
            _sleepLastTickUtc = DateTime.UtcNow;
        }

        // 2. Paired human-recording live tracking (words, moments, FillUnread, sentence-end stop)
        if (!IsUsingPairedRecording || !_playing || _pairedSpeakingUnread || _pairedRecording is null || _library is null || string.IsNullOrEmpty(_pairedAudioBookId))
            return;

        if (_pairedFileIndex < 0 || _pairedFileIndex >= _pairedFileOffsets.Length) return;

        double localSec = _player.PlaybackSession.Position.TotalSeconds;
        _pairedGlobalPosSec = _pairedFileOffsets[_pairedFileIndex] + localSec;

        if (_pairedSentenceEndStopSec > 0 && _pairedGlobalPosSec >= _pairedSentenceEndStopSec)
        {
            _pairedSentenceEndStopSec = -1;
            _sleepMode = 0;
            Pause();
            SleepStateChanged?.Invoke();
            SleepStopped?.Invoke("已念完这句并暂停");
            return;
        }

        try
        {
            var moment = _library.Core.AudiobookMoment(_pairedAudioBookId, _pairedGlobalPosSec);

            // Handle skips (pauses, head/tail, middle)
            if (moment.SkipTo is double skipTarget && skipTarget > _pairedGlobalPosSec + 0.15)
            {
                bool blockedByUserOverride = moment.Extra is not null && _pairedGlobalPosSec < _pairedIgnoreExtraUntilSec;
                if (!blockedByUserOverride && Math.Abs(skipTarget - _pairedLastMomentSkipTarget) > 0.25)
                {
                    _pairedLastMomentSkipTarget = skipTarget;
                    double delta = skipTarget - _pairedGlobalPosSec;

                    // 1:1 AudiobookPlayer.swift: ONLY count pause cuts (Extra == null) into AudiobookAddSaved
                    if (moment.Extra is null && delta > 0.1 && delta < 30.0)
                    {
                        try { _library.Core.AudiobookAddSaved(_pairedAudioBookId, delta); } catch { }
                    }
                    else if (moment.Extra is { } ex)
                    {
                        if (ex.Place == ExtraPlace.Tail)
                        {
                            Pause();
                            ExtraNoticeChanged?.Invoke("正文已播完，已跳过片尾", null, null);
                            return;
                        }
                        if (ex.Place == ExtraPlace.Head)
                        {
                            double headStart = ex.T0;
                            ExtraNoticeChanged?.Invoke($"跳过了片头（{(int)Math.Round(delta)} 秒）", "点这里听", () => PlaySkippedHeadNow(headStart));
                        }
                    }

                    SeekRecordingTo(skipTarget);
                    return;
                }
            }
            else if (moment.Extra is { } curEx && curEx.Place == ExtraPlace.Middle && _pairedGlobalPosSec >= _pairedIgnoreExtraUntilSec)
            {
                ExtraNoticeChanged?.Invoke("这段不在书里", "跳过", SkipExtraNow);
            }
            else
            {
                ExtraNoticeChanged?.Invoke(null, null, null);
            }

            if (moment.Hit is SyncHit.Sentence sh)
            {
                int sentIdx = (int)sh.SentenceValue;
                if (sh.Section != _pairedLastReportedSection || sentIdx != _pairedLastReportedSentence)
                {
                    // Check FillUnread: did the narrator skip any unread sentences right before this one?
                    bool fillUnread = false;
                    try { fillUnread = _library.Core.AudiobookSkipSettings(_pairedAudioBookId).FillUnread; } catch { }
                    if (fillUnread && _pairedLastReportedSection == sh.Section && sentIdx > _pairedLastReportedSentence + 1 && _pairedMap is not null)
                    {
                        var unreadRun = _pairedMap.Rows
                            .Where(r => r.Section == sh.Section && r.Sentence > (uint)_pairedLastReportedSentence && r.Sentence < sh.SentenceValue && r.By == SyncBy.Unread)
                            .OrderBy(r => r.Sentence)
                            .Select(r => (int)r.Sentence)
                            .ToArray();
                        if (unreadRun.Length > 0 && unreadRun.Length <= 8)
                        {
                            _pairedLastReportedSentence = sentIdx;
                            _ = SpeakPairedUnreadRunAsync(sh.Section, unreadRun);
                            return;
                        }
                    }

                    _pairedLastReportedSection = sh.Section;
                    _pairedLastReportedSentence = sentIdx;
                    _currentIndex = sentIdx;
                    _sentenceStartedUtc = DateTime.UtcNow;

                    int durMs = 2500;
                    int elapsedMs = 0;
                    int[]? wordPayload = null;
                    try
                    {
                        var bounds = _library.Core.AudiobookSentenceBounds(_pairedAudioBookId, _pairedGlobalPosSec);
                        double t0 = bounds?.T0 ?? _pairedGlobalPosSec;
                        double t1 = bounds?.T1 ?? (_pairedGlobalPosSec + 2.5);
                        durMs = Math.Max(300, (int)Math.Round((t1 - t0) * 1000.0 / Math.Max(0.5, _rate)));
                        elapsedMs = Math.Clamp((int)Math.Round((_pairedGlobalPosSec - t0) * 1000.0 / Math.Max(0.5, _rate)), 0, durMs);

                        bool wordFollow = _library.Core.AudiobookSkipSettings(_pairedAudioBookId).WordFollow;
                        if (wordFollow)
                        {
                            var units = _library.Core.AudiobookUnits(_pairedAudioBookId, sh.Section, sh.SentenceValue);
                            if (units.Length >= 2)
                            {
                                wordPayload = new int[units.Length * 2];
                                for (int i = 0; i < units.Length; i++)
                                {
                                    wordPayload[i * 2] = (int)units[i].At;
                                    wordPayload[i * 2 + 1] = Math.Max(0, (int)Math.Round((units[i].Seconds - t0) * 1000.0 / Math.Max(0.5, _rate)));
                                }
                            }
                        }
                    }
                    catch { }

                    PairedSentenceActive?.Invoke(sh.Section, sentIdx, durMs, elapsedMs, true, wordPayload);
                }
            }
        }
        catch { }
    }

    private async Task SpeakPairedUnreadRunAsync(uint section, int[] unreadSentenceIndices)
    {
        if (_library is null) return;
        _pairedSpeakingUnread = true;
        try
        {
            _player.Pause();
            ExtraNoticeChanged?.Invoke("原声跳过了几句，正用朗读补上…", null, null);
            using var tts = new AudioPlayer(_library);
            tts.SetBookContext(_bookId, _language);
            tts.SetRate(_rate);
            foreach (int idx in unreadSentenceIndices)
            {
                if (!_playing) return;
                if (idx >= 0 && idx < _sentences.Count)
                {
                    string txt = _sentences[idx];
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        PairedSentenceActive?.Invoke(section, idx, Math.Max(800, txt.Length * 180), 0, true, null);
                        var done = new TaskCompletionSource();
                        tts.SentenceFinished += () => done.TrySetResult();
                        await tts.SpeakSentenceAsync(txt, _bookTitle, _chapterTitle, _bookId, _language);
                        await Task.WhenAny(done.Task, Task.Delay(20_000));
                    }
                }
            }
        }
        catch { }
        finally
        {
            _pairedSpeakingUnread = false;
            ExtraNoticeChanged?.Invoke(null, null, null);
            if (_playing) _player.Play();
        }
    }

    private DramaAct? GetDramaActForSentence(int sentenceIdx)
    {
        if (!_dramaMode || _library is null || string.IsNullOrEmpty(_bookId) || _sectionIndex is not uint sec)
            return null;
        if (_cachedDramaSectionIndex != sec)
        {
            _cachedDramaSectionIndex = sec;
            _cachedSectionActs = new Dictionary<uint, DramaAct>();
            try
            {
                var ds = _library.Core.DramaSection(_bookId, sec);
                foreach (var seg in ds.Segments)
                {
                    if (!_cachedSectionActs.ContainsKey(seg.Sentence))
                        _cachedSectionActs[seg.Sentence] = seg.Act;
                }
            }
            catch { }
        }
        return _cachedSectionActs is not null && _cachedSectionActs.TryGetValue((uint)sentenceIdx, out var act) ? act : null;
    }

    public void SetVoice(string? voiceId)
    {
        ClearPrefetch();
        _selectedKokoroId = null;
        _selectedCardId = null;
        _selectedCloudId = null;
        string raw = DramaSynthService.NormalizeVoiceId(voiceId) ?? "";

        if (CloudVoiceService.TryParseFullId(raw, out var cp, out var cvId))
        {
            _selectedCloudId = raw;
            var entry = _library?.CloudVoices.GetVoices(cp).FirstOrDefault(x => x.VoiceId == cvId);
            _voiceDisplayName = entry is not null ? $"{entry.DisplayName} · 云端语音" : $"{cvId} · 云端语音";
        }
        else if (raw.StartsWith("kokoro.", StringComparison.Ordinal))
        {
            var speaker = _library?.Kokoro.FindSpeaker(raw);
            if (speaker is not null)
            {
                _selectedKokoroId = speaker.Id;
                _voiceDisplayName = $"{speaker.Name} · Kokoro 离线";
            }
        }
        else if (raw.StartsWith("card.", StringComparison.Ordinal) || raw.StartsWith("cloned.", StringComparison.Ordinal))
        {
            var card = FindCard(raw);
            if (card is not null)
            {
                _selectedCardId = card.Id;
                _voiceDisplayName = $"{card.Name} · 离线人声";
            }
        }
        else if (string.IsNullOrWhiteSpace(raw) && _library?.Kokoro.IsInstalled == true && CardVoiceEngine.SpeaksLanguage(_language))
        {
            var kNarrator = _library.Kokoro.DefaultNarrator(_language);
            if (kNarrator is not null)
            {
                _selectedKokoroId = kNarrator.Id;
                _voiceDisplayName = $"{kNarrator.Name} · Kokoro 离线";
            }
        }
        else if (string.IsNullOrWhiteSpace(raw) && _library?.LiveVoice.IsReady == true && CardVoiceEngine.SpeaksLanguage(_language))
        {
            var narrator = DramaSynthService.DefaultNarratorCard(_library, _language);
            if (narrator is not null)
            {
                _selectedCardId = narrator.Id;
                _voiceDisplayName = $"{narrator.Name} · 离线人声";
            }
        }
        else if (string.IsNullOrWhiteSpace(raw) && _library?.CloudVoices.IsConfigured(CloudTtsProvider.Free) == true)
        {
            bool isEn = _language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
            string freeId = isEn ? "af_heart" : "zf_001";
            _selectedCloudId = $"cloud.free.{freeId}";
            _voiceDisplayName = isEn ? "Heart · 免 Key 在线" : "晓语 · 免 Key 在线";
        }

        string sysId = raw.StartsWith("sys:", StringComparison.Ordinal) ? raw[4..] : raw;
        var voices = AvailableVoices;
        VoiceInformation? chosen = !string.IsNullOrWhiteSpace(sysId) &&
                                   !raw.StartsWith("kokoro.", StringComparison.Ordinal) &&
                                   !raw.StartsWith("card.", StringComparison.Ordinal) &&
                                   !raw.StartsWith("cloned.", StringComparison.Ordinal) &&
                                   !raw.StartsWith("cloud.", StringComparison.Ordinal)
            ? voices.FirstOrDefault(v => v.Id == sysId)
            : null;
        chosen ??= BestSystemVoice(_language);

        if (chosen is not null) _synth.Voice = chosen;
        if (_selectedKokoroId is null && _selectedCardId is null && _selectedCloudId is null)
            _voiceDisplayName = _synth.Voice?.DisplayName ?? "系统语音";
    }

    public void FallbackToSystemVoice()
    {
        _selectedKokoroId = null;
        _selectedCardId = null;
        _selectedCloudId = null;
        var sys = BestSystemVoice(_language);
        if (sys is not null) _synth.Voice = sys;
        _voiceDisplayName = _synth.Voice?.DisplayName ?? "系统语音";
        ClearPrefetch();
        if (_playing) _ = SpeakCurrentAsync();
    }

    private VoiceCard? FindCard(string? cardId)
    {
        string? norm = DramaSynthService.NormalizeVoiceId(cardId);
        if (string.IsNullOrEmpty(norm) || _library is null) return null;
        var local = _library.LiveVoice.GetBuiltinCards().FirstOrDefault(c => c.Id == norm)
            ?? _library.LiveVoice.GetClonedCards().FirstOrDefault(c => c.Id == norm);
        if (local is not null) return local;
        try { return _library.Core.CardFind(_bookId, norm); }
        catch { return null; }
    }

    // ---------- Cancellable Singleton Voice Preview (1:1 Voice.swift Previewer) ----------

    public static void StopActivePreview()
    {
        lock (PreviewGate)
        {
            try { ActivePreviewCts?.Cancel(); } catch { }
            try { ActivePreviewCts?.Dispose(); } catch { }
            ActivePreviewCts = null;
            try { ActivePreviewPlayer?.Dispose(); } catch { }
            ActivePreviewPlayer = null;
        }
    }

    public static async Task PreviewVoiceAsync(LibraryService library, string? voiceId, double rate, string sampleText)
    {
        StopActivePreview();
        var cts = new CancellationTokenSource();
        var preview = new AudioPlayer(library, voiceId);
        lock (PreviewGate)
        {
            ActivePreviewCts = cts;
            ActivePreviewPlayer = preview;
        }

        try
        {
            preview.SetRate(rate);
            var tcs = new TaskCompletionSource();
            preview.SectionFinished += () => tcs.TrySetResult();
            preview.SentenceFinished += () => tcs.TrySetResult();
            using var reg = cts.Token.Register(() => tcs.TrySetResult());
            preview.Start([sampleText], 0);
            await Task.WhenAny(tcs.Task, Task.Delay(12000, cts.Token));
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (PreviewGate)
            {
                if (ReferenceEquals(ActivePreviewPlayer, preview))
                {
                    ActivePreviewPlayer = null;
                    ActivePreviewCts = null;
                }
            }
            try { preview.Dispose(); } catch { }
            try { cts.Dispose(); } catch { }
        }
    }

    public static Task PreviewVoiceAsync(string? voiceId, double rate, string sampleText) =>
        PreviewVoiceRawAsync(voiceId, rate, sampleText);

    public static async Task SpeakForeignWordAsync(LibraryService library, string word, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(word)) return;
        string lang = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
        string wantLang = lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? (library.Prefs.ForeignAccent == "us" ? "en-US" : "en-GB")
            : lang;

        var voices = AvailableVoices;
        var chosen = voices.FirstOrDefault(v => string.Equals(v.Language, wantLang, StringComparison.OrdinalIgnoreCase))
            ?? BestSystemVoice(lang);

        string? sysId = chosen is not null ? "sys:" + chosen.Id : null;
        await PreviewVoiceRawAsync(sysId, 0.9, word);
    }

    private static async Task PreviewVoiceRawAsync(string? voiceId, double rate, string sampleText)
    {
        StopActivePreview();
        var cts = new CancellationTokenSource();
        var preview = new AudioPlayer(voiceId);
        lock (PreviewGate)
        {
            ActivePreviewCts = cts;
            ActivePreviewPlayer = preview;
        }
        try
        {
            preview.SetRate(rate);
            var tcs = new TaskCompletionSource();
            preview.SectionFinished += () => tcs.TrySetResult();
            using var reg = cts.Token.Register(() => tcs.TrySetResult());
            preview.Start([sampleText], 0);
            await Task.WhenAny(tcs.Task, Task.Delay(8000, cts.Token));
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (PreviewGate)
            {
                if (ReferenceEquals(ActivePreviewPlayer, preview))
                {
                    ActivePreviewPlayer = null;
                    ActivePreviewCts = null;
                }
            }
            try { preview.Dispose(); } catch { }
            try { cts.Dispose(); } catch { }
        }
    }

    public void SetMetadata(string bookTitle, string chapterTitle, string? coverPath = null)
    {
        _bookTitle = bookTitle;
        _chapterTitle = chapterTitle;
        if (coverPath is not null) _coverPath = coverPath;
        UpdateSmtcMetadata();
    }

    public void SetRate(double rate)
    {
        _rate = Math.Clamp(rate, 0.5, 3.0);
        _player.PlaybackSession.PlaybackRate = _rate;
        if (_playing && !IsUsingPairedRecording && _currentIndex >= 0 && _currentRawDurationMs > 50)
        {
            int totalMs = Math.Max(200, (int)(_currentRawDurationMs / _rate));
            int elapsedMs = Math.Clamp((int)(_player.PlaybackSession.Position.TotalMilliseconds / _rate), 0, totalMs);
            SentenceProgressSynced?.Invoke(_currentIndex, totalMs, elapsedMs, true);
        }
    }

    public void Start(IReadOnlyList<string> sentences, int startIndex)
    {
        StopInternal(notify: false);
        _singleSentenceMode = false;
        _sentences = sentences;
        if (_sentences.Count == 0) return;

        _currentIndex = Math.Clamp(startIndex, 0, _sentences.Count - 1);
        _playing = true;
        _sleepLastTickUtc = DateTime.UtcNow;
        NotifyPlayingState(true);

        if (IsUsingPairedRecording && _sectionIndex is uint sec)
        {
            double? t = LookupPairedTimeForSentence(sec, _currentIndex);
            if (t is double secTime)
            {
                _pairedLastReportedSentence = -1;
                SeekRecordingTo(secTime);
                return;
            }
        }

        _ = SpeakCurrentAsync();
    }

    public async Task SpeakSentenceAsync(string text, string bookTitle, string chapterTitle, string? bookId = null, string? language = null)
    {
        StopInternal(notify: false);
        _singleSentenceMode = true;
        _bookTitle = bookTitle;
        _chapterTitle = chapterTitle;
        if (bookId is not null) _bookId = bookId;
        if (!string.IsNullOrWhiteSpace(language)) _language = language;
        _sentences = [text];
        _currentIndex = 0;
        _playing = true;
        NotifyPlayingState(true);
        await SpeakCurrentAsync();
    }

    public void TogglePause()
    {
        if (_isSleepFading)
        {
            ExtendSleep(10);
            return;
        }
        if (_playing) Pause();
        else Resume();
    }

    public void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _cts?.Cancel();
        ClearPrefetch();
        if (IsUsingPairedRecording) _pairedPausedUtc = DateTime.UtcNow;
        if (_isSleepFading)
        {
            _isSleepFading = false;
            RestoreVolume();
        }
        _player.Pause();
        NotifyPlayingState(false);
    }

    public void Resume()
    {
        if (_playing) return;
        if (_currentIndex < 0 || (_sentences.Count > 0 && _currentIndex >= _sentences.Count)) return;
        _playing = true;
        _sleepLastTickUtc = DateTime.UtcNow;
        NotifyPlayingState(true);

        if (IsUsingPairedRecording)
        {
            if (_pairedPausedUtc is DateTime pausedAt && _library is not null && !string.IsNullOrEmpty(_pairedAudioBookId))
            {
                double pausedSec = (DateTime.UtcNow - pausedAt).TotalSeconds;
                _pairedPausedUtc = null;
                if (pausedSec >= 5.0)
                {
                    try
                    {
                        double resumeAt = _library.Core.AudiobookResumeAt(_pairedAudioBookId, _pairedGlobalPosSec, pausedSec);
                        if (Math.Abs(resumeAt - _pairedGlobalPosSec) > 0.3)
                        {
                            SeekRecordingTo(resumeAt);
                            return;
                        }
                    }
                    catch { }
                }
            }
            _player.PlaybackSession.PlaybackRate = _rate;
            _player.Play();
            EnsureTickTimer();
            return;
        }

        if (_player.Source is null)
        {
            _ = SpeakCurrentAsync();
            return;
        }
        _player.PlaybackSession.PlaybackRate = _rate;
        _player.Play();
        if (_currentRawDurationMs > 50)
        {
            int totalMs = Math.Max(200, (int)(_currentRawDurationMs / _rate));
            int elapsedMs = Math.Clamp((int)(_player.PlaybackSession.Position.TotalMilliseconds / _rate), 0, totalMs);
            SentenceProgressSynced?.Invoke(_currentIndex, totalMs, elapsedMs, true);
        }
        ScheduleLookahead(_currentIndex + 1);
    }

    public void Next()
    {
        if (_isSleepFading) { ExtendSleep(10); return; }
        if (IsUsingPairedRecording && _library is not null && !string.IsNullOrEmpty(_pairedAudioBookId))
        {
            try
            {
                double? nextSec = _library.Core.AudiobookStep(_pairedAudioBookId, _pairedGlobalPosSec, true);
                if (nextSec is double ns) { SeekRecordingTo(ns); return; }
            }
            catch { }
        }
        if (_sentences.Count == 0) return;
        if (_currentIndex + 1 < _sentences.Count)
        {
            _currentIndex++;
            _playing = true;
            NotifyPlayingState(true);
            _ = SpeakCurrentAsync();
        }
        else
        {
            SectionFinished?.Invoke();
        }
    }

    public void Previous()
    {
        if (_isSleepFading) { ExtendSleep(10); return; }
        if (IsUsingPairedRecording && _library is not null && !string.IsNullOrEmpty(_pairedAudioBookId))
        {
            try
            {
                double? prevSec = _library.Core.AudiobookStep(_pairedAudioBookId, _pairedGlobalPosSec, false);
                if (prevSec is double ps) { SeekRecordingTo(ps); return; }
            }
            catch { }
        }
        if (_sentences.Count == 0) return;
        _currentIndex = Math.Max(0, _currentIndex - 1);
        _playing = true;
        NotifyPlayingState(true);
        _ = SpeakCurrentAsync();
    }

    /// <summary>
    /// 1:1 <c>Player.swift:361-364</c>: marks the sentence just heard (previous sentence if &lt; 1s into current).
    /// </summary>
    public void TriggerMarkHeard()
    {
        if (_currentIndex < 0) return;
        int target = _currentIndex;
        if ((DateTime.UtcNow - _sentenceStartedUtc).TotalSeconds < 1.0 && target > 0)
            target--;
        MarkHeardRequested?.Invoke(target);
    }

    public void Stop() => StopInternal(notify: true);

    private void NotifyPlayingState(bool playing)
    {
        if (playing)
        {
            try { App.CurrentApp.ClaimAudioPlayback(this, Pause); } catch { }
            SyncAmbientPlayback(true);
            EnsureTickTimer();
        }
        else
        {
            SyncAmbientPlayback(false);
        }
        try
        {
            _player.SystemMediaTransportControls.PlaybackStatus = playing
                ? MediaPlaybackStatus.Playing
                : (_player.Source is null ? MediaPlaybackStatus.Stopped : MediaPlaybackStatus.Paused);
        }
        catch { }
        _library?.SystemIntegration.SetAudioPlaying(this, playing, _bookTitle, TogglePause);
        PlayStateChanged?.Invoke(playing);
    }

    private MediaPlayer? _ambientPlayer;
    private string _loadedAmbientId = "";

    public void RefreshAmbientSound() => SyncAmbientPlayback(_playing);

    private void SyncAmbientPlayback(bool shouldPlay)
    {
        string ambId = _library?.Prefs.AmbientSoundId ?? "";
        double vol = Math.Clamp(_library?.Prefs.AmbientSoundVolume ?? 0.25, 0.05, 0.8);

        if (!shouldPlay || string.IsNullOrEmpty(ambId))
        {
            try { _ambientPlayer?.Pause(); } catch { }
            return;
        }

        try
        {
            _ambientPlayer ??= new MediaPlayer { IsLoopingEnabled = true, AutoPlay = false };
            _ambientPlayer.Volume = vol;
            if (_loadedAmbientId != ambId)
            {
                byte[] wav = BuildProceduralAmbientWav(ambId);
                var ms = new InMemoryRandomAccessStream();
                using (var dw = new DataWriter(ms.GetOutputStreamAt(0)))
                {
                    dw.WriteBytes(wav);
                    dw.StoreAsync().GetAwaiter().GetResult();
                    dw.DetachStream();
                }
                ms.Seek(0);
                _ambientPlayer.Source = MediaSource.CreateFromStream(ms, "audio/wav");
                _loadedAmbientId = ambId;
            }
            _ambientPlayer.Play();
        }
        catch { }
    }

    private static byte[] BuildProceduralAmbientWav(string kind)
    {
        const int sr = 24000;
        const int secs = 8;
        int n = sr * secs;
        var samples = new float[n];
        var rng = new Random(kind.GetHashCode());
        double lp1 = 0, lp2 = 0;

        for (int i = 0; i < n; i++)
        {
            double white = rng.NextDouble() * 2.0 - 1.0;
            switch (kind)
            {
                case "fire":
                    lp1 = 0.985 * lp1 + 0.015 * white;
                    double crackle = rng.NextDouble() > 0.9993 ? (rng.NextDouble() * 0.45) : 0;
                    samples[i] = (float)(lp1 * 0.55 + crackle);
                    break;
                case "stream":
                    lp1 = 0.92 * lp1 + 0.08 * white;
                    lp2 = 0.992 * lp2 + 0.008 * white;
                    double lfo = 0.78 + 0.22 * Math.Sin(2.0 * Math.PI * i / (sr * 2.0));
                    samples[i] = (float)((lp1 - lp2) * 0.42 * lfo);
                    break;
                case "night":
                    lp1 = 0.96 * lp1 + 0.04 * white;
                    double env = Math.Max(0, Math.Sin(2.0 * Math.PI * i / (sr * 1.6)));
                    double cricket = Math.Sin(2.0 * Math.PI * 4180.0 * i / sr) * (env > 0.75 ? 0.018 : 0);
                    samples[i] = (float)(lp1 * 0.25 + cricket);
                    break;
                default:
                    lp1 = 0.88 * lp1 + 0.12 * white;
                    double drop = rng.NextDouble() > 0.9988 ? (rng.NextDouble() * 0.16) : 0;
                    samples[i] = (float)(lp1 * 0.34 + drop);
                    break;
            }

            int fadeFrames = sr / 5;
            if (i < fadeFrames) samples[i] *= (float)i / fadeFrames;
            else if (i > n - fadeFrames) samples[i] *= (float)(n - i) / fadeFrames;
        }

        using var mem = new MemoryStream(44 + n * 2);
        using var bw = new BinaryWriter(mem);
        bw.Write("RIFF"u8);
        bw.Write(36 + n * 2);
        bw.Write("WAVEfmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sr);
        bw.Write(sr * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(n * 2);
        for (int i = 0; i < n; i++)
        {
            short pcm = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
            bw.Write(pcm);
        }
        return mem.ToArray();
    }

    private void ClearPrefetch()
    {
        lock (_prefetchLock)
        {
            _prefetchCts?.Cancel();
            _prefetchCts?.Dispose();
            _prefetchCts = null;
            foreach (var task in _prefetchTasks.Values)
            {
                if (task.IsCompletedSuccessfully && task.Result is { } clip)
                {
                    try { clip.Source.Dispose(); } catch { }
                }
            }
            _prefetchTasks.Clear();
        }
    }

    private void ScheduleLookahead(int fromIndex)
    {
        if (_singleSentenceMode || !_playing || IsUsingPairedRecording || _sentences.Count == 0) return;
        // Larger lookahead for local neural voices (Kokoro / CardVoice) to keep 15–60s buffered ahead (1:1 Player.swift)
        int lookaheadCount = (_selectedKokoroId is not null || _selectedCardId is not null) ? 4 : 2;

        lock (_prefetchLock)
        {
            _prefetchCts ??= new CancellationTokenSource();
            var ct = _prefetchCts.Token;

            var stale = _prefetchTasks.Keys.Where(k => k < fromIndex).ToArray();
            foreach (var k in stale)
            {
                if (_prefetchTasks.Remove(k, out var t) && t.IsCompletedSuccessfully && t.Result is { } clip)
                {
                    try { clip.Source.Dispose(); } catch { }
                }
            }

            int queued = 0;
            for (int idx = fromIndex; idx < _sentences.Count && queued < lookaheadCount; idx++)
            {
                string txt = _sentences[idx]?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(txt)) continue;
                queued++;
                if (_prefetchTasks.ContainsKey(idx)) continue;
                int capIdx = idx;
                string capTxt = txt;
                _prefetchTasks[capIdx] = PrepareClipAsync(capIdx, capTxt, ct);
            }
        }
    }

    private async Task<PreparedClip?> PrepareClipAsync(int sentenceIdx, string text, CancellationToken ct)
    {
        try
        {
            var src = await SynthesizeMediaSourceAsync(sentenceIdx, text, ct);
            if (src is null || ct.IsCancellationRequested)
            {
                src?.Dispose();
                return null;
            }
            await src.OpenAsync().AsTask(ct);
            double rawMs = src.Duration is { } d && d.TotalMilliseconds > 50
                ? d.TotalMilliseconds
                : Math.Max(800, text.Length * 180.0);
            if (ct.IsCancellationRequested)
            {
                src.Dispose();
                return null;
            }
            return new PreparedClip(src, rawMs);
        }
        catch
        {
            return null;
        }
    }

    private void StopInternal(bool notify)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        ClearPrefetch();
        _playing = false;
        _currentRawDurationMs = 0;
        _isSleepFading = false;
        RestoreVolume();
        _library?.SystemIntegration.SetAudioPlaying(this, false);
        try
        {
            _player.Pause();
            _player.Source = null;
            _player.SystemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
        }
        catch { }
        if (notify) PlayStateChanged?.Invoke(false);
    }

    private async Task<byte[]?> TrySynthesizeDramaMixedWavAsync(int sentenceIdx, CancellationToken ct)
    {
        if (!_dramaMode || _library is null || string.IsNullOrEmpty(_bookId) || _sectionIndex is not uint sec)
            return null;

        string? narrator = _selectedCloudId ?? _selectedKokoroId ?? _selectedCardId ?? _library.Prefs.GetVoiceForLanguage(_language);
        return await DramaSynthService.RenderLiveSentenceWavAsync(
            this, _library, _bookId, sec, (uint)sentenceIdx, _language, narrator, ct);
    }

    internal Task<byte[]?> SynthesizeClipForDramaAsync(string annotatedText, string plainText, string? voiceOverride, DramaAct? acting, CancellationToken ct) =>
        SynthesizeSingleWavBytesAsync(annotatedText, voiceOverride, acting, ct, plainOverride: plainText);

    private async Task<byte[]?> TrySynthesizeRoleSegmentedWavAsync(int sentenceIdx, CancellationToken ct)
    {
        if (_library is null || string.IsNullOrEmpty(_bookId) || _sectionIndex is not uint sec)
            return null;

        RoleSegment[] segs;
        Role[] roles;
        try
        {
            segs = _library.Core.RoleSentenceSegments(_bookId, sec, (uint)sentenceIdx);
            if (segs.Length == 0 || segs.All(s => string.IsNullOrEmpty(s.Role))) return null;
            roles = _library.Core.BookRoles(_bookId);
        }
        catch { return null; }

        var roleMap = roles.ToDictionary(r => r.Name, r => r.Voice);
        bool anyCustomOrDrama = _dramaMode || segs.Any(s => s.Role is not null && roleMap.ContainsKey(s.Role));
        if (!anyCustomOrDrama) return null;

        var langVoices = GetSystemVoicesForLanguage(_language);
        var pcmParts = new List<short[]>();
        uint targetRate = 24_000;

        foreach (var seg in segs)
        {
            ct.ThrowIfCancellationRequested();
            if (!CardVoiceEngine.PlainSpokenText(seg.Text).Any(char.IsLetterOrDigit)) continue;

            string? voiceOverride = null;
            float speed = 1.0f;
            float semitones = 0.0f;

            if (seg.Role is not null)
            {
                if (roleMap.TryGetValue(seg.Role, out var rv) && rv is not null)
                {
                    voiceOverride = DramaSynthService.NormalizeVoiceId(rv.Voice);
                    speed = (float)Math.Clamp(rv.Rate, 0.6, 1.8);
                    semitones = (float)Math.Clamp(rv.Pitch, -6.0, 6.0);
                }
                else if (_dramaMode)
                {
                    int hash = Math.Abs(seg.Role.GetHashCode());
                    if (_library.LiveVoice.IsReady)
                    {
                        var cards = _library.LiveVoice.GetBuiltinCards().Where(c => !c.Narrator).ToArray();
                        if (cards.Length > 0) voiceOverride = cards[hash % cards.Length].Id;
                    }
                    else if (langVoices.Count > 1)
                    {
                        voiceOverride = "sys:" + langVoices[hash % langVoices.Count].Id;
                    }
                    if (langVoices.Count <= 1 && !_library.LiveVoice.IsReady)
                        semitones = ((hash % 5) - 2) * 1.1f;
                }
            }

            var act = GetDramaActForSentence(sentenceIdx);
            byte[]? segWav = await SynthesizeSingleWavBytesAsync(seg.Text, voiceOverride, act, ct);
            if (segWav is null || segWav.Length <= 44) continue;

            var (samples, sr) = DecodeWavPcm16Mono(segWav);
            if (samples.Length == 0) continue;

            if (sr != targetRate) samples = ResampleMono16(samples, sr, targetRate);

            if (Math.Abs(speed - 1.0f) >= 0.02f || Math.Abs(semitones) >= 0.1f)
            {
                try { samples = YuyueCoreMethods.TuneSpeechPcm(samples, targetRate, speed, semitones); }
                catch { }
            }

            if (pcmParts.Count > 0) pcmParts.Add(new short[(int)(targetRate * 0.08)]);
            pcmParts.Add(samples);
        }

        if (pcmParts.Count == 0) return null;
        int totalSamples = pcmParts.Sum(p => p.Length);
        byte[] outPcm = new byte[totalSamples * 2];
        int byteOffset = 0;
        foreach (var part in pcmParts)
        {
            System.Buffer.BlockCopy(part, 0, outPcm, byteOffset, part.Length * 2);
            byteOffset += part.Length * 2;
        }
        return CloudVoiceService.WrapPcm16MonoInWav(outPcm, (int)targetRate);
    }

    internal static short[] ResampleMono16(short[] input, uint fromRate, uint toRate)
    {
        if (input.Length == 0 || fromRate == toRate || fromRate == 0 || toRate == 0) return input;
        double ratio = (double)fromRate / toRate;
        int outLen = Math.Max(1, (int)Math.Round(input.Length / ratio));
        var output = new short[outLen];
        for (int i = 0; i < outLen; i++)
        {
            double pos = i * ratio;
            int idx = (int)pos;
            double frac = pos - idx;
            short s0 = input[Math.Min(idx, input.Length - 1)];
            short s1 = input[Math.Min(idx + 1, input.Length - 1)];
            output[i] = (short)Math.Clamp(s0 + (s1 - s0) * frac, short.MinValue, short.MaxValue);
        }
        return output;
    }

    private async Task<byte[]?> SynthesizeSingleWavBytesAsync(
        string text, string? overrideVoiceId, DramaAct? acting, CancellationToken ct, string? plainOverride = null)
    {
        string? cloudId = _selectedCloudId;
        string? kokoroId = _selectedKokoroId;
        string? cardId = _selectedCardId;
        string? sysVoiceId = null;

        string? normOverride = DramaSynthService.NormalizeVoiceId(overrideVoiceId);
        if (!string.IsNullOrEmpty(normOverride))
        {
            if (CloudVoiceService.TryParseFullId(normOverride, out _, out _))
            {
                cloudId = normOverride;
                kokoroId = null;
                cardId = null;
            }
            else if (normOverride.StartsWith("kokoro.", StringComparison.Ordinal))
            {
                kokoroId = normOverride;
                cloudId = null;
                cardId = null;
            }
            else if (normOverride.StartsWith("card.", StringComparison.Ordinal) || normOverride.StartsWith("cloned.", StringComparison.Ordinal))
            {
                cardId = normOverride;
                kokoroId = null;
                cloudId = null;
            }
            else if (normOverride.StartsWith("sys:", StringComparison.Ordinal))
            {
                sysVoiceId = normOverride[4..];
                cloudId = null;
                kokoroId = null;
                cardId = null;
            }
        }

        if (!string.IsNullOrEmpty(kokoroId) && _library is not null)
        {
            var speaker = _library.Kokoro.FindSpeaker(kokoroId);
            if (speaker is not null)
            {
                if (!_library.Kokoro.IsInstalled)
                {
                    if (_library.Kokoro.State is KokoroStateKind.Idle or KokoroStateKind.Failed)
                        _ = _library.Kokoro.DownloadAsync();
                    SynthesisError?.Invoke("离线语音包还在下载中，下好后即可离线朗读");
                    return null;
                }
                if (CardVoiceEngine.SpeaksLanguage(_language))
                {
                    try
                    {
                        byte[]? wav = await KokoroEngine.Instance.SynthesizeWavAsync(
                            _library.Core, _library.Kokoro, _library.DataDir, speaker, text, _language, ct);
                        if (wav is { Length: > 44 }) return wav;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        SynthesisError?.Invoke($"Kokoro 离线朗读出错：{ex.Message}");
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(cloudId) && _library is not null)
        {
            try
            {
                var cloudActing = acting is not null
                    ? _library.CloudVoices.MapDramaActing(acting, _language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                    : null;
                byte[]? wav = await _library.CloudVoices.SynthesizeWavAsync(cloudId, text, _language, cloudActing, ct);
                if (wav is { Length: > 44 }) return wav;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SynthesisError?.Invoke($"云端朗读暂时不可用：{ex.Message}");
            }
        }

        var card = FindCard(cardId);
        if (card is not null && _library is not null)
        {
            // Fixed priority (1:1 ClonedVoice.swift): prefer local ZipVoice unless user explicitly opted into ElevenLabs or local model isn't installed
            bool preferEleven = card.Id.StartsWith("cloned.", StringComparison.Ordinal) &&
                                _library.Prefs.ClonedElevenLabsVoiceIds.TryGetValue(card.Id, out var remoteElevenId) &&
                                !string.IsNullOrEmpty(remoteElevenId) &&
                                _library.CloudVoices.IsConfigured(CloudTtsProvider.ElevenLabs) &&
                                (_library.Prefs.ClonedUseElevenLabs.Contains(card.Id) || !_library.LiveVoice.IsModelInstalled);

            if (preferEleven && _library.Prefs.ClonedElevenLabsVoiceIds.TryGetValue(card.Id, out var elId))
            {
                try
                {
                    var cloudActing = acting is not null
                        ? _library.CloudVoices.MapDramaActing(acting, _language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                        : null;
                    byte[]? wav = await _library.CloudVoices.SynthesizeWavAsync($"cloud.elevenlabs.{elId}", text, _language, cloudActing, ct);
                    if (wav is { Length: > 44 }) return wav;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            if (CardVoiceEngine.SpeaksLanguage(_language))
            {
                try
                {
                    CardStyle style = CardStyle.Neutral;
                    if (acting is not null)
                    {
                        try { style = _library.Core.CardStyleFor(acting); } catch { }
                    }
                    var wav = await CardVoiceEngine.Instance.SynthesizeWavAsync(
                        _library.Core, _library.LiveVoice, _library.DataDir,
                        card, _bookId, text, _language, style, ct);
                    if (wav is { Length: > 44 }) return wav;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }

        string plain = plainOverride ?? CardVoiceEngine.PlainSpokenText(text);
        int timeoutMs = Math.Clamp((int)((20.0 + plain.Length * 0.3) * 1000.0), 10_000, 60_000);
        using var synthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        synthCts.CancelAfter(timeoutMs);

        if (sysVoiceId is not null)
        {
            using var tempSynth = new SpeechSynthesizer();
            var match = AvailableVoices.FirstOrDefault(v => v.Id == sysVoiceId);
            if (match is not null) tempSynth.Voice = match;
            using var s = await tempSynth.SynthesizeTextToStreamAsync(plain).AsTask(synthCts.Token);
            var bytes = new byte[s.Size];
            await s.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(synthCts.Token);
            return bytes;
        }
        else
        {
            using var s = await _synth.SynthesizeTextToStreamAsync(plain).AsTask(synthCts.Token);
            var bytes = new byte[s.Size];
            await s.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(synthCts.Token);
            return bytes;
        }
    }

    internal static (short[] Samples, uint Rate) DecodeWavPcm16Mono(byte[] wav)
    {
        if (wav.Length <= 44) return ([], 24_000);
        ushort channels = BitConverter.ToUInt16(wav, 22);
        uint rate = BitConverter.ToUInt32(wav, 24);
        ushort bits = BitConverter.ToUInt16(wav, 34);
        if (rate == 0) rate = 24_000;

        int pos = 12;
        int dataOffset = 44, dataLen = wav.Length - 44;
        while (pos + 8 <= wav.Length)
        {
            int chunkSize = BitConverter.ToInt32(wav, pos + 4);
            if (wav[pos] == 'd' && wav[pos + 1] == 'a' && wav[pos + 2] == 't' && wav[pos + 3] == 'a')
            {
                dataOffset = pos + 8;
                dataLen = Math.Min(chunkSize, wav.Length - dataOffset);
                break;
            }
            pos += 8 + Math.Max(0, chunkSize) + (chunkSize & 1);
        }

        if (bits != 16 || dataLen <= 0) return ([], rate);
        int frameStep = Math.Max(1, (int)channels) * 2;
        int count = dataLen / frameStep;
        var outSamples = new short[count];
        for (int i = 0; i < count; i++)
            outSamples[i] = BitConverter.ToInt16(wav, dataOffset + i * frameStep);
        return (outSamples, rate);
    }

    private async Task<MediaSource?> SynthesizeMediaSourceAsync(int sentenceIdx, string text, CancellationToken ct)
    {
        if (!_singleSentenceMode && _dramaMode)
        {
            var mixedWav = await TrySynthesizeDramaMixedWavAsync(sentenceIdx, ct);
            if (mixedWav is { Length: > 44 })
            {
                var mem = new InMemoryRandomAccessStream();
                await mem.WriteAsync(mixedWav.AsBuffer()).AsTask(ct);
                mem.Seek(0);
                return MediaSource.CreateFromStream(mem, "audio/wav");
            }
        }

        if (!_singleSentenceMode)
        {
            var roleWav = await TrySynthesizeRoleSegmentedWavAsync(sentenceIdx, ct);
            if (roleWav is { Length: > 44 })
            {
                var mem = new InMemoryRandomAccessStream();
                await mem.WriteAsync(roleWav.AsBuffer()).AsTask(ct);
                mem.Seek(0);
                return MediaSource.CreateFromStream(mem, "audio/wav");
            }
        }

        var acting = GetDramaActForSentence(sentenceIdx);

        if (!string.IsNullOrEmpty(_selectedCloudId) && _library is not null)
        {
            try
            {
                var cloudActing = acting is not null
                    ? _library.CloudVoices.MapDramaActing(acting, _language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                    : null;
                byte[]? wav = await _library.CloudVoices.SynthesizeWavAsync(_selectedCloudId, text, _language, cloudActing, ct);
                if (wav is { Length: > 44 })
                {
                    if (!_singleSentenceMode && sentenceIdx + 1 < _sentences.Count)
                    {
                        var upcoming = _sentences.Skip(sentenceIdx + 1).Take(4).ToArray();
                        string cid = _selectedCloudId;
                        _ = Task.Run(() => _library.CloudVoices.PrefetchAsync(cid, upcoming, _language, CancellationToken.None));
                    }
                    var mem = new InMemoryRandomAccessStream();
                    await mem.WriteAsync(wav.AsBuffer()).AsTask(ct);
                    mem.Seek(0);
                    return MediaSource.CreateFromStream(mem, "audio/wav");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SynthesisError?.Invoke($"云端语音暂不可用（{ex.Message}），已自动回退系统声音");
            }
        }

        if (!string.IsNullOrEmpty(_selectedKokoroId) && _library is not null)
        {
            var speaker = _library.Kokoro.FindSpeaker(_selectedKokoroId);
            if (speaker is not null)
            {
                if (!_library.Kokoro.IsInstalled)
                {
                    if (_library.Kokoro.State is KokoroStateKind.Idle or KokoroStateKind.Failed)
                        _ = _library.Kokoro.DownloadAsync();
                    SynthesisError?.Invoke("离线语音包还在下载中，下好后即可离线朗读");
                    return null;
                }
                byte[]? wav = await SynthesizeSingleWavBytesAsync(text, speaker.Id, acting, ct);
                if (wav is { Length: > 44 })
                {
                    var mem = new InMemoryRandomAccessStream();
                    await mem.WriteAsync(wav.AsBuffer()).AsTask(ct);
                    mem.Seek(0);
                    return MediaSource.CreateFromStream(mem, "audio/wav");
                }
            }
        }

        var card = FindCard(_selectedCardId);
        if (card is not null && _library is not null)
        {
            byte[]? wav = await SynthesizeSingleWavBytesAsync(text, card.Id, acting, ct);
            if (wav is { Length: > 44 })
            {
                var mem = new InMemoryRandomAccessStream();
                await mem.WriteAsync(wav.AsBuffer()).AsTask(ct);
                mem.Seek(0);
                return MediaSource.CreateFromStream(mem, "audio/wav");
            }
        }

        string plain = CardVoiceEngine.PlainSpokenText(text);
        int timeoutMs = Math.Clamp((int)((20.0 + plain.Length * 0.3) * 1000.0), 10_000, 60_000);
        using var synthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        synthCts.CancelAfter(timeoutMs);
        var stream = await _synth.SynthesizeTextToStreamAsync(plain).AsTask(synthCts.Token);
        return MediaSource.CreateFromStream(stream, stream.ContentType);
    }

    private async Task SpeakCurrentAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        while (_currentIndex >= 0 && _currentIndex < _sentences.Count)
        {
            if (cts.IsCancellationRequested || !_playing) return;
            var text = _sentences[_currentIndex]?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                _currentIndex++;
                continue;
            }

            try
            {
                PreparedClip? clip = null;
                Task<PreparedClip?>? prefetched = null;
                lock (_prefetchLock)
                {
                    _prefetchTasks.Remove(_currentIndex, out prefetched);
                }
                if (prefetched is not null)
                {
                    try { clip = await prefetched.WaitAsync(cts.Token); } catch { clip = null; }
                }

                clip ??= await PrepareClipAsync(_currentIndex, text, cts.Token);
                if (clip is null || cts.IsCancellationRequested || !_playing)
                {
                    clip?.Source.Dispose();
                    return;
                }

                _currentRawDurationMs = clip.RawDurationMs;
                _sentenceStartedUtc = DateTime.UtcNow;
                int estMs = Math.Max(300, (int)(clip.RawDurationMs / _rate));

                _player.Source = clip.Source;
                _player.PlaybackSession.PlaybackRate = _rate;
                UpdateSmtcMetadata();
                _player.Play();
                SentenceStarted?.Invoke(_currentIndex, estMs, true);
                ScheduleLookahead(_currentIndex + 1);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                SynthesisError?.Invoke($"朗读出错：{ex.Message}");
                _currentIndex++;
            }
        }

        if (_playing)
        {
            if (_singleSentenceMode) SentenceFinished?.Invoke();
            else SectionFinished?.Invoke();
        }
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        if (!_playing) return;

        // Paired recording track ended -> advance to next file seamlessly
        if (IsUsingPairedRecording && _pairedRecording is not null)
        {
            void NextTrack()
            {
                if (_pairedFileIndex + 1 < _pairedRecording.Files.Length)
                    LoadAndPlayPairedFile(_pairedFileIndex + 1, 0, autoPlay: true);
                else
                    SectionFinished?.Invoke();
            }
            if (_uiQueue is not null) _uiQueue.TryEnqueue(NextTrack);
            else NextTrack();
            return;
        }

        if (_singleSentenceMode)
        {
            SentenceFinished?.Invoke();
            return;
        }
        if (_stopAfterSentence)
        {
            _stopAfterSentence = false;
            bool wasSleep = _sleepMode is 1 or 3;
            _sleepMode = 0;
            void FinishStop()
            {
                Pause();
                RestoreVolume();
                SleepStateChanged?.Invoke();
                if (wasSleep) SleepStopped?.Invoke("已定时停在句尾并记住位置");
                SentenceFinished?.Invoke();
            }
            if (_uiQueue is not null) _uiQueue.TryEnqueue(FinishStop);
            else FinishStop();
            return;
        }

        int pauseMs = _shadowPauseMode switch
        {
            1 => 2000,
            2 => Math.Clamp((int)(_currentRawDurationMs / Math.Max(0.5, _rate)), 1200, 20000),
            _ => 0,
        };

        void Advance()
        {
            if (!_playing) return;
            if (_loopSingleSentence)
            {
                _ = SpeakCurrentAsync();
                return;
            }
            if (_loopRangeStart is int rStart && _loopRangeEnd is int rEnd && _currentIndex >= rEnd)
            {
                _currentIndex = rStart;
                _ = SpeakCurrentAsync();
                return;
            }
            if (_currentIndex + 1 < _sentences.Count)
            {
                _currentIndex++;
                _ = SpeakCurrentAsync();
            }
            else
            {
                if (_sleepMode == 2)
                {
                    _sleepMode = 0;
                    Pause();
                    SleepStateChanged?.Invoke();
                    SleepStopped?.Invoke("本章播完，已暂停并记住位置");
                    return;
                }
                SectionFinished?.Invoke();
            }
        }

        if (pauseMs > 0)
        {
            var token = _cts?.Token ?? CancellationToken.None;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(pauseMs, token);
                    if (_uiQueue is not null) _uiQueue.TryEnqueue(Advance);
                    else Advance();
                }
                catch { }
            });
        }
        else
        {
            if (_uiQueue is not null) _uiQueue.TryEnqueue(Advance);
            else Advance();
        }
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        void Handle()
        {
            if (_isSleepFading && args.Button is SystemMediaTransportControlsButton.Play or SystemMediaTransportControlsButton.Next or SystemMediaTransportControlsButton.Previous)
            {
                ExtendSleep(10);
                return;
            }
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play: Resume(); break;
                case SystemMediaTransportControlsButton.Pause: Pause(); break;
                case SystemMediaTransportControlsButton.Next:
                    if (HeadphoneMarkMode && MarkHeardRequested is not null) TriggerMarkHeard();
                    else Next();
                    break;
                case SystemMediaTransportControlsButton.Previous: Previous(); break;
            }
        }
        if (_uiQueue is not null && !_uiQueue.HasThreadAccess) _uiQueue.TryEnqueue(Handle);
        else Handle();
    }

    private void UpdateSmtcMetadata()
    {
        try
        {
            var updater = _player.SystemMediaTransportControls.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = string.IsNullOrEmpty(_chapterTitle) ? _bookTitle : _chapterTitle;
            updater.MusicProperties.Artist = _bookTitle;
            if (!string.IsNullOrEmpty(_coverPath) && File.Exists(_coverPath))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_coverPath);
                        updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                        updater.Update();
                    }
                    catch { }
                });
            }
            updater.Update();
        }
        catch { }
    }

    public void Dispose()
    {
        try { _tickTimer?.Stop(); } catch { }
        StopInternal(notify: false);
        try { _ambientPlayer?.Pause(); _ambientPlayer?.Dispose(); } catch { }
        _player.MediaEnded -= OnMediaEnded;
        _player.Dispose();
        _synth.Dispose();
    }
}
