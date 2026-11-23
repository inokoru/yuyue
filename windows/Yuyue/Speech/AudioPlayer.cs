using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;
using Yuyue.Core;

namespace Yuyue.Speech;

public sealed record SpeechVoiceOption(string Id, string Label, bool IsNeural);

/// <summary>
/// Unified sentence audio player supporting:
/// 1. On-device neural <see cref="VoiceCard"/> voices (ZipVoice-Distill-Int8 + Vocos 24 kHz via ONNX Runtime + cloned voices)
/// 2. Cloud TTS voices (<c>cloud.openai.*</c>, <c>cloud.azure.*</c>, <c>cloud.elevenlabs.*</c> with background 2-sentence prefetch)
/// 3. Windows system voices (<c>Windows.Media.SpeechSynthesis</c>)
/// 4. Multi-character Drama / Role dialogue synthesis (<see cref="YuyueCore.RoleSentenceSegments"/> + <see cref="YuyueCoreMethods.TuneSpeechPcm"/>)
/// 5. System stay-awake &amp; dynamic System Tray integration (<see cref="SystemIntegrationService"/>).
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly LibraryService? _library;
    private readonly MediaPlayer _player;
    private readonly SpeechSynthesizer _synth;
    private CancellationTokenSource? _cts;
    private readonly Stopwatch _sentenceClock = new();

    private IReadOnlyList<string> _sentences = [];
    private int _currentIndex = -1;
    private bool _singleSentenceMode;
    private double _rate = 1.0;
    private bool _playing;
    private string _bookTitle = "语阅";
    private string _chapterTitle = "";
    private string? _bookId;
    private uint? _sectionIndex;
    private bool _dramaMode;
    private string _language = "zh-Hans";
    private string? _selectedCardId;
    private string? _selectedCloudId;
    private string _voiceDisplayName = "系统语音";

    public event Action<int, int, bool>? SentenceStarted;
    public event Action<bool>? PlayStateChanged;
    public event Action? SectionFinished;
    public event Action? SentenceFinished;

    public bool IsPlaying => _playing;
    public int CurrentSentence => _currentIndex;
    public double Rate => _rate;
    public string VoiceName => _voiceDisplayName;

    public static IReadOnlyList<VoiceInformation> AvailableVoices => SpeechSynthesizer.AllVoices.ToArray();

    public static IReadOnlyList<SpeechVoiceOption> GetAllVoiceOptions(LibraryService library)
    {
        var list = new List<SpeechVoiceOption>();
        if (library.LiveVoice.IsReady)
        {
            foreach (var c in library.LiveVoice.GetBuiltinCards())
            {
                string tag = c.Tags.Length > 0 ? $" · {string.Join(" / ", c.Tags.Take(2))}" : "";
                list.Add(new SpeechVoiceOption($"card:{c.Id}", $"✦ {c.Name}（离线神经语音{tag}）", true));
            }
            foreach (var c in library.LiveVoice.GetClonedCards())
            {
                list.Add(new SpeechVoiceOption($"card:{c.Id}", $"✦ {c.Name}（克隆声音）", true));
            }
        }
        else
        {
            // Still expose cloned cards if user has ElevenLabs remote clones configured
            foreach (var c in library.LiveVoice.GetClonedCards())
            {
                list.Add(new SpeechVoiceOption($"card:{c.Id}", $"✦ {c.Name}（克隆声音）", true));
            }
        }

        foreach (var cv in library.CloudVoices.GetAllConfiguredVoices())
        {
            string pLabel = cv.Provider switch
            {
                CloudTtsProvider.Free => "免 Key 在线",
                CloudTtsProvider.Volcengine => "火山豆包",
                CloudTtsProvider.Fish => "Fish Audio",
                CloudTtsProvider.OpenAi => "OpenAI",
                CloudTtsProvider.Azure => "Azure",
                _ => "ElevenLabs",
            };
            list.Add(new SpeechVoiceOption(cv.FullId, $"☁ {cv.DisplayName}（{pLabel}）", true));
        }

        foreach (var v in AvailableVoices)
        {
            list.Add(new SpeechVoiceOption($"sys:{v.Id}", $"{v.DisplayName} ({v.Language}) · 系统自带（不推荐）", false));
        }
        return list;
    }

    public AudioPlayer(LibraryService library) : this(library, library.Prefs.VoiceId) { }

    public AudioPlayer(string? preferredVoiceId = null) : this(null, preferredVoiceId) { }

    private AudioPlayer(LibraryService? library, string? preferredVoiceId)
    {
        _library = library;
        _synth = new SpeechSynthesizer();
        SetVoice(preferredVoiceId);

        _player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Speech,
            AutoPlay = true,
        };
        _player.MediaEnded += OnMediaEnded;

        var smtc = _player.SystemMediaTransportControls;
        smtc.IsEnabled = true;
        smtc.IsPlayEnabled = true;
        smtc.IsPauseEnabled = true;
        smtc.IsNextEnabled = true;
        smtc.IsPreviousEnabled = true;
        smtc.ButtonPressed += OnSmtcButtonPressed;
    }

    public void SetBookContext(string? bookId, string? language)
    {
        _bookId = bookId;
        _language = string.IsNullOrWhiteSpace(language) ? "zh-Hans" : language;
    }

    public void SetSectionContext(uint sectionIndex, bool dramaMode)
    {
        _sectionIndex = sectionIndex;
        _dramaMode = dramaMode;
    }

    public void SetVoice(string? voiceId)
    {
        _selectedCardId = null;
        _selectedCloudId = null;
        string raw = voiceId ?? "";

        if (CloudVoiceService.TryParseFullId(raw, out var cp, out var cvId))
        {
            _selectedCloudId = raw;
            var entry = _library?.CloudVoices.GetVoices(cp).FirstOrDefault(x => x.VoiceId == cvId);
            _voiceDisplayName = entry is not null ? $"{entry.DisplayName} · 云端语音" : $"{cvId} · 云端语音";
        }
        else if (raw.StartsWith("card:", StringComparison.Ordinal) || raw.StartsWith("cloned.", StringComparison.Ordinal))
        {
            string cardId = raw.StartsWith("card:", StringComparison.Ordinal) ? raw[5..] : raw;
            var card = FindCard(cardId);
            if (card is not null)
            {
                _selectedCardId = card.Id;
                _voiceDisplayName = $"{card.Name} · 离线人声";
            }
        }
        else if (string.IsNullOrWhiteSpace(raw) && _library?.LiveVoice.IsReady == true)
        {
            var narrator = _library.LiveVoice.GetBuiltinCards().FirstOrDefault(c => c.Narrator)
                ?? _library.LiveVoice.GetBuiltinCards().FirstOrDefault();
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
        var voices = SpeechSynthesizer.AllVoices;
        VoiceInformation? chosen = !string.IsNullOrWhiteSpace(sysId) &&
                                   !raw.StartsWith("card:", StringComparison.Ordinal) &&
                                   !raw.StartsWith("cloud.", StringComparison.Ordinal)
            ? voices.FirstOrDefault(v => v.Id == sysId)
            : null;
        chosen ??= voices.FirstOrDefault(v =>
            v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) &&
            v.DisplayName.Contains("Xiaoxiao", StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault(v => v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase));

        if (chosen is not null) _synth.Voice = chosen;
        if (_selectedCardId is null && _selectedCloudId is null)
            _voiceDisplayName = _synth.Voice?.DisplayName ?? "系统语音";
    }

    private VoiceCard? FindCard(string? cardId)
    {
        if (string.IsNullOrEmpty(cardId) || _library is null) return null;
        return _library.LiveVoice.GetBuiltinCards().FirstOrDefault(c => c.Id == cardId)
            ?? _library.LiveVoice.GetClonedCards().FirstOrDefault(c => c.Id == cardId);
    }

    public static async Task PreviewVoiceAsync(LibraryService library, string? voiceId, double rate, string sampleText)
    {
        using var preview = new AudioPlayer(library, voiceId);
        preview.SetRate(rate);
        var tcs = new TaskCompletionSource();
        preview.SectionFinished += () => tcs.TrySetResult();
        preview.Start([sampleText], 0);
        await Task.WhenAny(tcs.Task, Task.Delay(12000));
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
            ?? voices.FirstOrDefault(v => v.Language.StartsWith(lang.Split('-')[0], StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase));

        string? sysId = chosen is not null ? "sys:" + chosen.Id : null;
        await PreviewVoiceRawAsync(sysId, 0.9, word);
    }

    private static async Task PreviewVoiceRawAsync(string? voiceId, double rate, string sampleText)
    {
        using var preview = new AudioPlayer(voiceId);
        preview.SetRate(rate);
        var tcs = new TaskCompletionSource();
        preview.SectionFinished += () => tcs.TrySetResult();
        preview.Start([sampleText], 0);
        await Task.WhenAny(tcs.Task, Task.Delay(8000));
    }

    public void SetMetadata(string bookTitle, string chapterTitle)
    {
        _bookTitle = bookTitle;
        _chapterTitle = chapterTitle;
        UpdateSmtcMetadata();
    }

    public void SetRate(double rate)
    {
        _rate = Math.Clamp(rate, 0.5, 2.5);
        _player.PlaybackSession.PlaybackRate = _rate;
    }

    public void Start(IReadOnlyList<string> sentences, int startIndex)
    {
        StopInternal(notify: false);
        _singleSentenceMode = false;
        _sentences = sentences;
        if (_sentences.Count == 0) return;

        _currentIndex = Math.Clamp(startIndex, 0, _sentences.Count - 1);
        _playing = true;
        NotifyPlayingState(true);
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
        if (_playing) Pause();
        else Resume();
    }

    public void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _player.Pause();
        _sentenceClock.Stop();
        NotifyPlayingState(false);
    }

    public void Resume()
    {
        if (_playing) return;
        if (_currentIndex < 0 || _currentIndex >= _sentences.Count) return;
        _playing = true;
        _player.Play();
        _sentenceClock.Start();
        NotifyPlayingState(true);
    }

    public void Next()
    {
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
        if (_sentences.Count == 0) return;
        _currentIndex = Math.Max(0, _currentIndex - 1);
        _playing = true;
        NotifyPlayingState(true);
        _ = SpeakCurrentAsync();
    }

    public void Stop() => StopInternal(notify: true);

    private void NotifyPlayingState(bool playing)
    {
        if (playing)
        {
            try { App.CurrentApp.ClaimAudioPlayback(this, Pause); } catch { }
            SyncAmbientPlayback(true);
        }
        else
        {
            SyncAmbientPlayback(false);
        }
        _library?.SystemIntegration.SetAudioPlaying(this, playing, _bookTitle, TogglePause);
        PlayStateChanged?.Invoke(playing);
    }

    private MediaPlayer? _ambientPlayer;
    private string _loadedAmbientId = "";

    public void RefreshAmbientSound()
    {
        SyncAmbientPlayback(_playing);
    }

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
                var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var dw = new Windows.Storage.Streams.DataWriter(ms.GetOutputStreamAt(0)))
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
                case "fire": // Warm crackle + deep brown noise
                    lp1 = 0.985 * lp1 + 0.015 * white;
                    double crackle = rng.NextDouble() > 0.9993 ? (rng.NextDouble() * 0.45) : 0;
                    samples[i] = (float)(lp1 * 0.55 + crackle);
                    break;
                case "stream": // Band-shaped babbling water
                    lp1 = 0.92 * lp1 + 0.08 * white;
                    lp2 = 0.992 * lp2 + 0.008 * white;
                    double lfo = 0.78 + 0.22 * Math.Sin(2.0 * Math.PI * i / (sr * 2.0));
                    samples[i] = (float)((lp1 - lp2) * 0.42 * lfo);
                    break;
                case "night": // Soft night breeze + gentle high harmonic chirp
                    lp1 = 0.96 * lp1 + 0.04 * white;
                    double env = Math.Max(0, Math.Sin(2.0 * Math.PI * i / (sr * 1.6)));
                    double cricket = Math.Sin(2.0 * Math.PI * 4180.0 * i / sr) * (env > 0.75 ? 0.018 : 0);
                    samples[i] = (float)(lp1 * 0.25 + cricket);
                    break;
                default: // "rain": soft pink/velvet rain
                    lp1 = 0.88 * lp1 + 0.12 * white;
                    double drop = rng.NextDouble() > 0.9988 ? (rng.NextDouble() * 0.16) : 0;
                    samples[i] = (float)(lp1 * 0.34 + drop);
                    break;
            }

            // Smooth loop crossfade at edges (0.2s)
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

    private void StopInternal(bool notify)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _playing = false;
        _sentenceClock.Reset();
        _library?.SystemIntegration.SetAudioPlaying(this, false);
        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch { }
        if (notify) PlayStateChanged?.Invoke(false);
    }

    // 1:1 RoleVoices.swift: splits mixed narration/dialogue sentences and tunes character voices!
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
        catch
        {
            return null;
        }

        var roleMap = roles.ToDictionary(r => r.Name, r => r.Voice);
        bool anyCustomOrDrama = _dramaMode || segs.Any(s => s.Role is not null && roleMap.ContainsKey(s.Role));
        if (!anyCustomOrDrama) return null;

        var allVoices = AvailableVoices;
        var langVoices = allVoices
            .Where(v => v.Language.StartsWith(_language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en", StringComparison.OrdinalIgnoreCase))
            .ToArray();

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
                    voiceOverride = rv.Voice;
                    speed = (float)Math.Clamp(rv.Rate, 0.6, 1.8);
                    semitones = (float)Math.Clamp(rv.Pitch, -6.0, 6.0);
                }
                else if (_dramaMode)
                {
                    int hash = Math.Abs(seg.Role.GetHashCode());
                    if (_library.LiveVoice.IsReady)
                    {
                        var cards = _library.LiveVoice.GetBuiltinCards().Where(c => !c.Narrator).ToArray();
                        if (cards.Length > 0) voiceOverride = "card:" + cards[hash % cards.Length].Id;
                    }
                    else if (langVoices.Length > 1)
                    {
                        voiceOverride = "sys:" + langVoices[hash % langVoices.Length].Id;
                    }
                    // Subtle pitch differentiation even when system only has 1 voice installed!
                    if (langVoices.Length <= 1 && !_library.LiveVoice.IsReady)
                        semitones = ((hash % 5) - 2) * 1.1f;
                }
            }

            byte[]? segWav = await SynthesizeSingleWavBytesAsync(seg.Text, voiceOverride, ct);
            if (segWav is null || segWav.Length <= 44) continue;

            var (samples, sr) = DecodeWavPcm16Mono(segWav);
            if (samples.Length == 0) continue;
            targetRate = sr;

            if (Math.Abs(speed - 1.0f) >= 0.02f || Math.Abs(semitones) >= 0.1f)
            {
                try { samples = YuyueCoreMethods.TuneSpeechPcm(samples, sr, speed, semitones); }
                catch { }
            }

            if (pcmParts.Count > 0)
            {
                // 0.08s inter-segment silence per RoleVoices.swift
                pcmParts.Add(new short[(int)(targetRate * 0.08)]);
            }
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

    private async Task<byte[]?> SynthesizeSingleWavBytesAsync(string text, string? overrideVoiceId, CancellationToken ct)
    {
        string? cloudId = _selectedCloudId;
        string? cardId = _selectedCardId;
        string? sysVoiceId = null;

        if (!string.IsNullOrEmpty(overrideVoiceId))
        {
            if (CloudVoiceService.TryParseFullId(overrideVoiceId, out _, out _))
            {
                cloudId = overrideVoiceId;
                cardId = null;
            }
            else if (overrideVoiceId.StartsWith("card:", StringComparison.Ordinal) || overrideVoiceId.StartsWith("cloned.", StringComparison.Ordinal))
            {
                cardId = overrideVoiceId.StartsWith("card:", StringComparison.Ordinal) ? overrideVoiceId[5..] : overrideVoiceId;
                cloudId = null;
            }
            else if (overrideVoiceId.StartsWith("sys:", StringComparison.Ordinal))
            {
                sysVoiceId = overrideVoiceId[4..];
                cloudId = null;
                cardId = null;
            }
        }

        if (!string.IsNullOrEmpty(cloudId) && _library is not null)
        {
            try
            {
                var wav = await _library.CloudVoices.SynthesizeWavAsync(cloudId, text, _language, null, ct);
                if (wav is { Length: > 44 }) return wav;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        var card = FindCard(cardId);
        if (card is not null && _library is not null)
        {
            // Check if cloned card has ElevenLabs remote fallback configured
            if (card.Id.StartsWith("cloned.", StringComparison.Ordinal) &&
                _library.Prefs.ClonedElevenLabsVoiceIds.TryGetValue(card.Id, out var remoteElevenId) &&
                !string.IsNullOrEmpty(remoteElevenId) &&
                _library.CloudVoices.IsConfigured(CloudTtsProvider.ElevenLabs))
            {
                try
                {
                    var wav = await _library.CloudVoices.SynthesizeWavAsync($"cloud.elevenlabs.{remoteElevenId}", text, _language, null, ct);
                    if (wav is { Length: > 44 }) return wav;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            if (CardVoiceEngine.SpeaksLanguage(_language))
            {
                try
                {
                    var wav = await CardVoiceEngine.Instance.SynthesizeWavAsync(
                        _library.Core, _library.LiveVoice, _library.DataDir,
                        card, _bookId, text, _language, CardStyle.Neutral, ct);
                    if (wav is { Length: > 44 }) return wav;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }

        string plain = CardVoiceEngine.PlainSpokenText(text);
        if (sysVoiceId is not null)
        {
            using var tempSynth = new SpeechSynthesizer();
            var match = AvailableVoices.FirstOrDefault(v => v.Id == sysVoiceId);
            if (match is not null) tempSynth.Voice = match;
            using var s = await tempSynth.SynthesizeTextToStreamAsync(plain).AsTask(ct);
            var bytes = new byte[s.Size];
            await s.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(ct);
            return bytes;
        }
        else
        {
            using var s = await _synth.SynthesizeTextToStreamAsync(plain).AsTask(ct);
            var bytes = new byte[s.Size];
            await s.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(ct);
            return bytes;
        }
    }

    private static (short[] Samples, uint Rate) DecodeWavPcm16Mono(byte[] wav)
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
            pos += 8 + Math.Max(0, chunkSize);
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
        // 1. Multi-role segmented dialogue synthesis when DramaMode or custom character roles apply
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

        // 2. Cloud TTS + 2-sentence prefetch
        if (!string.IsNullOrEmpty(_selectedCloudId) && _library is not null)
        {
            try
            {
                byte[]? wav = await _library.CloudVoices.SynthesizeWavAsync(_selectedCloudId, text, _language, null, ct);
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
            catch { /* fall through cleanly */ }
        }

        // 3. On-device neural ZipVoice / Cloned card
        var card = FindCard(_selectedCardId);
        if (card is not null && _library is not null)
        {
            byte[]? wav = await SynthesizeSingleWavBytesAsync(text, "card:" + card.Id, ct);
            if (wav is { Length: > 44 })
            {
                var mem = new InMemoryRandomAccessStream();
                await mem.WriteAsync(wav.AsBuffer()).AsTask(ct);
                mem.Seek(0);
                return MediaSource.CreateFromStream(mem, "audio/wav");
            }
        }

        // 4. Windows system voice fallback
        string plain = CardVoiceEngine.PlainSpokenText(text);
        var stream = await _synth.SynthesizeTextToStreamAsync(plain).AsTask(ct);
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
                var mediaSource = await SynthesizeMediaSourceAsync(_currentIndex, text, cts.Token);
                if (mediaSource is null || cts.IsCancellationRequested || !_playing)
                {
                    mediaSource?.Dispose();
                    return;
                }

                int estMs = Math.Max(800, (int)(text.Length * 180 / _rate));
                await mediaSource.OpenAsync().AsTask(cts.Token);
                if (mediaSource.Duration is { } dur && dur.TotalMilliseconds > 50)
                {
                    estMs = (int)(dur.TotalMilliseconds / _rate);
                }

                if (cts.IsCancellationRequested || !_playing)
                {
                    mediaSource.Dispose();
                    return;
                }

                _player.Source = mediaSource;
                _player.PlaybackSession.PlaybackRate = _rate;
                _sentenceClock.Restart();
                UpdateSmtcMetadata();
                _player.Play();
                SentenceStarted?.Invoke(_currentIndex, estMs, true);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
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
        if (_singleSentenceMode)
        {
            SentenceFinished?.Invoke();
            return;
        }
        if (_currentIndex + 1 < _sentences.Count)
        {
            _currentIndex++;
            _ = SpeakCurrentAsync();
        }
        else
        {
            SectionFinished?.Invoke();
        }
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play: Resume(); break;
            case SystemMediaTransportControlsButton.Pause: Pause(); break;
            case SystemMediaTransportControlsButton.Next: Next(); break;
            case SystemMediaTransportControlsButton.Previous: Previous(); break;
        }
    }

    private void UpdateSmtcMetadata()
    {
        try
        {
            var updater = _player.SystemMediaTransportControls.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = string.IsNullOrEmpty(_chapterTitle) ? _bookTitle : _chapterTitle;
            updater.MusicProperties.Artist = _bookTitle;
            updater.Update();
        }
        catch { }
    }

    public void Dispose()
    {
        StopInternal(notify: false);
        try { _ambientPlayer?.Pause(); _ambientPlayer?.Dispose(); } catch { }
        _player.MediaEnded -= OnMediaEnded;
        _player.Dispose();
        _synth.Dispose();
    }
}
