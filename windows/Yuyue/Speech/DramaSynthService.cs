using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Yuyue.Core;

namespace Yuyue.Speech;

/// <summary>
/// 1:1 Windows port of <c>apple/Yuyue/Speech/DramaSynth.swift</c> + <c>LiveCastCache</c>:
/// <list type="bullet">
///   <item>Caches per-book <see cref="CastingPlan"/> via <see cref="YuyueCore.DramaCastVoices"/> so
///     <see cref="YuyueCore.DramaLiveSentenceAsks"/> and <see cref="DramaMixer"/> actually produce cast voices,
///     room ambience beds, spot SFX, reverb, and spatial pan on Windows-only books.</item>
///   <item>Applies <see cref="YuyueCore.DramaClipTuning"/> (<see cref="YuyueCoreMethods.TuneSpeechPcm"/>) and
///     maintains a 500 MB per-book SHA-256 disk cache under <c>drama/{book}/live-clips/</c>.</item>
///   <item>Drives background Studio pre-synthesis (<see cref="DramaTask.Tts"/> &amp; <see cref="DramaTask.Sfx"/>),
///     saves clips under <c>drama/{book}/clips/</c>, measures clips with <see cref="YuyueCore.DramaMeasureClip"/>,
///     and exports chaptered <c>.m4b</c> audiobooks via Media Foundation AAC + <see cref="YuyueCore.DramaFinishM4b"/>.</item>
///   <item>Supports LAN phone pre-render requests (<c>/want</c>) gated by AC power and <c>Prefs.DramaPreRenderPhone</c>.</item>
/// </list>
/// </summary>
public static class DramaSynthService
{
    public const uint MixSampleRate = 48_000;
    public const long MaxLiveCacheBytes = 500_000_000L;

    private static readonly ConcurrentDictionary<string, CastingPlan> CastPlans = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> PreRenderLoops = new(StringComparer.Ordinal);

    public static event Action<string>? SectionMade;

    public static bool IsPreRendering(string bookId) => PreRenderLoops.ContainsKey(bookId);

    public static bool IsAnyPreRendering => !PreRenderLoops.IsEmpty;

    public static bool IsPreparing(LibraryService library, string bookId) =>
        library.Prefs.DramaPreparingBooks.Contains(bookId);

    public static void SetPreparing(LibraryService library, string bookId, bool on)
    {
        if (on) library.Prefs.DramaPreparingBooks.Add(bookId);
        else library.Prefs.DramaPreparingBooks.Remove(bookId);
        library.SavePrefs();
    }

    public static bool IsDramaActive(LibraryService library, string bookId)
    {
        bool enabled;
        try { enabled = library.Core.DramaEnabled(bookId); }
        catch { return false; }
        if (!enabled && IsPreparing(library, bookId))
        {
            SetPreparing(library, bookId, false);
            StopPreRender(bookId);
        }
        return enabled;
    }

    /// <summary>
    /// Normalizes legacy Windows <c>"card:card.b.01"</c> / <c>"card:cloned.xxx"</c> IDs to macOS/Rust core canonical
    /// bare IDs (<c>"card.b.01"</c>, <c>"cloned.xxx"</c>) so cross-device role sync &amp; core lookups match 1:1.
    /// </summary>
    public static string? NormalizeVoiceId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (s.StartsWith("card:", StringComparison.Ordinal))
            return s[5..];
        return s;
    }

    public static void InvalidateCast(string bookId)
    {
        CastPlans.TryRemove(bookId, out _);
    }

    public static void InvalidateAllCasts()
    {
        CastPlans.Clear();
    }

    public static string ClipsFolder(string dataDir, string bookId) =>
        Path.Combine(dataDir, "drama", bookId, "clips");

    public static string LiveClipsFolder(string dataDir, string bookId) =>
        Path.Combine(dataDir, "drama", bookId, "live-clips");

    public static string ClipPath(string dataDir, string bookId, string key) =>
        Path.Combine(ClipsFolder(dataDir, bookId), $"{key}.wav");

    public static bool IsClipMade(string dataDir, string bookId, string key) =>
        File.Exists(ClipPath(dataDir, bookId, key));

    /// <summary>
    /// Resolves the effective narrator voice ID for <paramref name="language"/>, upgrading system defaults to the
    /// built-in ZipVoice narrator (<c>card.b.01</c> zh / <c>card.b.33</c> en) when the live model is ready.
    /// </summary>
    public static string? EffectiveNarrator(LibraryService library, string language, string? chosen = null)
    {
        string? raw = NormalizeVoiceId(chosen ?? library.Prefs.GetVoiceForLanguage(language));
        if (!string.IsNullOrEmpty(raw) &&
            (raw.StartsWith("kokoro.", StringComparison.Ordinal) ||
             raw.StartsWith("card.", StringComparison.Ordinal) ||
             raw.StartsWith("cloned.", StringComparison.Ordinal) ||
             raw.StartsWith("cloud.", StringComparison.Ordinal)))
        {
            return raw;
        }

        if (library.Kokoro.IsInstalled && CardVoiceEngine.SpeaksLanguage(language))
        {
            var kDef = library.Kokoro.DefaultNarrator(language);
            if (kDef is not null) return kDef.Id;
        }

        if (library.LiveVoice.IsReady && CardVoiceEngine.SpeaksLanguage(language))
        {
            var def = DefaultNarratorCard(library, language);
            if (def is not null) return def.Id;
        }

        if (!string.IsNullOrEmpty(raw)) return raw;
        var sys = AudioPlayer.BestSystemVoice(language);
        return sys is not null ? $"sys:{sys.Id}" : null;
    }

    public static VoiceCard? DefaultNarratorCard(LibraryService library, string language)
    {
        bool isEn = language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        string preferredId = isEn ? "card.b.33" : "card.b.01";
        string langPrefix = isEn ? "en" : "zh";
        var cards = library.LiveVoice.GetBuiltinCards();
        return cards.FirstOrDefault(c => c.Id == preferredId)
            ?? cards.FirstOrDefault(c => c.Narrator && c.Lang.StartsWith(langPrefix, StringComparison.OrdinalIgnoreCase))
            ?? cards.FirstOrDefault(c => c.Lang.StartsWith(langPrefix, StringComparison.OrdinalIgnoreCase))
            ?? cards.FirstOrDefault(c => c.Narrator)
            ?? cards.FirstOrDefault();
    }

    /// <summary>
    /// Whether the currently effective voice pool supports expressive acting (ZipVoice cards, acting cloud providers,
    /// or core's persisted <see cref="YuyueCore.DramaCanAct"/>). Matches <c>CastView.swift:796</c>.
    /// </summary>
    public static bool CanAct(LibraryService library, string language, string? narrator = null, string? bookId = null)
    {
        if (!string.IsNullOrEmpty(bookId))
        {
            try
            {
                if (library.Core.DramaCanAct(bookId)) return true;
            }
            catch { }
        }
        string? eff = EffectiveNarrator(library, language, narrator);
        if (library.LiveVoice.IsReady && CardVoiceEngine.SpeaksLanguage(language)) return true;
        if (eff is not null && eff.StartsWith("cloud.", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Switches drama vs. single-narrator playback mode in core so all synced devices follow.
    /// </summary>
    public static void SetDramaMode(LibraryService library, string bookId, bool dramaMode)
    {
        try { library.Core.DramaSetMode(bookId, dramaMode); } catch { }
    }

    /// <summary>
    /// Queries whether <paramref name="section"/> has pre-rendered studio clips ready or should synthesize live.
    /// </summary>
    public static DramaLayer GetSectionLayer(LibraryService library, string bookId, uint section, string studioEngine = "zipvoice")
    {
        try { return library.Core.DramaSectionLayer(bookId, section, studioEngine); }
        catch { return DramaLayer.Live; }
    }

    /// <summary>
    /// Resolves per-sentence speaking role names for <paramref name="section"/> from core.
    /// </summary>
    public static string?[] GetSectionRoles(LibraryService library, string bookId, uint section)
    {
        try { return library.Core.SectionRoles(bookId, section); }
        catch { return []; }
    }

    /// <summary>
    /// Returns the name of the LAN peer device whose pre-rendered drama clips were adopted for <paramref name="bookId"/>.
    /// </summary>
    public static string? GetBorrowedFrom(LibraryService library, string bookId)
    {
        try { return library.Core.DramaBorrowedFrom(bookId); }
        catch { return null; }
    }

    /// <summary>
    /// Adopts pre-rendered drama clips received from a peer device on the LAN (parity with Apple RecordingShare.swift).
    /// </summary>
    public static void AdoptRemoteClips(LibraryService library, string bookId, string device, string manifest, IEnumerable<string> keys)
    {
        string[] have = keys.Where(k => IsClipMade(library.DataDir, bookId, k)).ToArray();
        library.Core.DramaAdoptClips(bookId, device, manifest, have);
        if (have.Length > 0)
        {
            try { library.Core.DramaSetEnabled(bookId, true); } catch { }
        }
    }

    /// <summary>
    /// Adopts designed voice cards JSON shared by a peer device for <paramref name="bookId"/>.
    /// </summary>
    public static uint AdoptRemoteCardsJson(LibraryService library, string bookId, string json)
    {
        uint adopted = library.Core.CardsAdoptBookJson(bookId, json);
        if (adopted > 0) InvalidateCast(bookId);
        return adopted;
    }

    /// <summary>
    /// Builds the candidate <see cref="VoiceInfo"/> catalog for <see cref="YuyueCore.DramaCastVoices"/>
    /// (1:1 <c>DramaSynth.voices(for:narrator:book:)</c>).
    /// </summary>
    public static VoiceInfo[] BuildCastingVoices(LibraryService library, string language, string? narrator, string? bookId = null)
    {
        var outList = new List<VoiceInfo>();
        string langPrefix = language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";

        if (library.LiveVoice.IsReady && CardVoiceEngine.SpeaksLanguage(language))
        {
            var builtin = library.LiveVoice.GetBuiltinCards()
                .Where(c => c.Lang.StartsWith(langPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (builtin.Count == 0) builtin = library.LiveVoice.GetBuiltinCards().ToList();

            VoiceCard[] designed = [];
            if (!string.IsNullOrEmpty(bookId))
            {
                try { designed = library.Core.CardsOfBook(bookId); }
                catch { }
            }

            foreach (var card in builtin.Concat(designed))
            {
                try
                {
                    outList.Add(library.Core.CardVoiceInfo(card, card.Embedding, acts: true));
                }
                catch { }
            }
        }

        if (outList.Count == 0)
        {
            foreach (var v in AudioPlayer.GetSystemVoicesForLanguage(language))
            {
                var gender = v.Gender switch
                {
                    Windows.Media.SpeechSynthesis.VoiceGender.Male => CharacterGender.M,
                    Windows.Media.SpeechSynthesis.VoiceGender.Female => CharacterGender.F,
                    _ => CharacterGender.Unknown,
                };
                outList.Add(new VoiceInfo(
                    Id: $"sys:{v.Id}",
                    Langs: [v.Language],
                    Gender: gender,
                    Age: CharacterAge.Unknown,
                    Tags: [],
                    Embedding: [],
                    Acts: false,
                    NarratorOk: true,
                    LoudnessLufs: -20f));
            }
        }

        string? normNarr = NormalizeVoiceId(narrator);
        if (normNarr is not null && normNarr.StartsWith("cloud.", StringComparison.Ordinal) &&
            CloudVoiceService.TryParseFullId(normNarr, out var prov, out _))
        {
            foreach (var cv in library.CloudVoices.GetVoices(prov))
            {
                if (!cv.Speaks(language)) continue;
                var gender = cv.Gender?.ToLowerInvariant() switch
                {
                    "male" => CharacterGender.M,
                    "female" => CharacterGender.F,
                    _ => CharacterGender.Unknown,
                };
                var age = cv.Age?.ToLowerInvariant() switch
                {
                    "young" => CharacterAge.Young,
                    "middle" or "middle_aged" or "middle aged" => CharacterAge.Middle,
                    "old" => CharacterAge.Old,
                    _ => CharacterAge.Unknown,
                };
                outList.Add(new VoiceInfo(
                    Id: cv.FullId,
                    Langs: [cv.Language ?? language],
                    Gender: gender,
                    Age: age,
                    Tags: [],
                    Embedding: [],
                    Acts: true,
                    NarratorOk: true,
                    LoudnessLufs: -20f));
            }
        }

        return outList.ToArray();
    }

    /// <summary>
    /// Ensures a persisted <see cref="CastingPlan"/> exists for <paramref name="bookId"/> in Rust core
    /// (fixes the dead-code bug where <c>DramaLiveSentenceAsks</c> returned <c>[]</c> on Windows-only books).
    /// </summary>
    public static CastingPlan? EnsureCastPlan(LibraryService library, string bookId, string language, string? narrator = null)
    {
        if (CastPlans.TryGetValue(bookId, out var cached))
            return cached;

        string? effNarrator = EffectiveNarrator(library, language, narrator);
        var voices = BuildCastingVoices(library, language, effNarrator, bookId);
        if (voices.Length == 0) return null;

        string resolvedNarrator = effNarrator ?? voices[0].Id;
        if (!voices.Any(v => v.Id == resolvedNarrator))
        {
            // Include user's chosen cloned/custom voice in pool if it isn't in built-ins
            if (resolvedNarrator.StartsWith("cloned.", StringComparison.Ordinal))
            {
                var cloned = library.LiveVoice.GetClonedCards().FirstOrDefault(c => c.Id == resolvedNarrator);
                if (cloned is not null)
                {
                    try
                    {
                        voices = [.. voices, library.Core.CardVoiceInfo(cloned, cloned.Embedding, acts: true)];
                    }
                    catch { }
                }
            }
            if (!voices.Any(v => v.Id == resolvedNarrator))
                resolvedNarrator = voices[0].Id;
        }

        try
        {
            // Seed voice-card speaker embeddings for core scene casting (1:1 SpeakerPrint.swift)
            try
            {
                library.Core.SpeakerModel(SpeakerPrintService.ModelName);
                foreach (var v in voices)
                {
                    if (v.Embedding is { Length: > 0 })
                        library.Core.SpeakerSetPrint(v.Id, v.Embedding);
                }
            }
            catch { }

            // Respect the user's chosen narrator verbatim — never silently replace a cloud narrator with a local one.
            var plan = library.Core.DramaCastVoices(bookId, voices, resolvedNarrator);
            CastPlans[bookId] = plan;
            return plan;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to render a pre-rendered Studio sentence from saved clips on disk via <see cref="YuyueCore.DramaSentenceWindow"/>.
    /// </summary>
    public static byte[]? TryRenderStudioSentenceWav(LibraryService library, string bookId, uint section, uint sentence)
    {
        try
        {
            SoundLibraryService.Register(library.Core, library.DataDir);
            var window = library.Core.DramaSentenceWindow(bookId, section, sentence);
            if (window is null) return null;

            var sources = new List<MixSource>();
            foreach (string key in window.Clips.Distinct(StringComparer.Ordinal))
            {
                float[]? samples = ReadClipFloat48k(library.DataDir, bookId, key);
                if (samples is null) return null;
                sources.Add(new MixSource(key, samples, Stereo: false));
            }

            using var mixer = new DramaMixer(window.Timeline, sources.ToArray());
            float[] mixed = mixer.RenderSpan(window.From, window.To);
            return mixed.Length > 0 ? WrapFloatMonoAsWav48k(mixed) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Renders a live drama sentence through <see cref="EnsureCastPlan"/>, <see cref="YuyueCore.DramaLiveSentenceAsks"/>,
    /// <see cref="YuyueCore.DramaClipTuning"/>, <see cref="DramaMixer"/>, and a 500 MB per-book SHA-256 disk cache
    /// (1:1 <c>DramaSynth.swift:522-662</c>).
    /// </summary>
    public static async Task<byte[]?> RenderLiveSentenceWavAsync(
        AudioPlayer player,
        LibraryService library,
        string bookId,
        uint section,
        uint sentence,
        string language,
        string? narrator,
        CancellationToken ct)
    {
        // 1. Prefer Studio pre-rendered clips when all clips for this sentence exist on disk
        byte[]? studioHit = TryRenderStudioSentenceWav(library, bookId, section, sentence);
        if (studioHit is not null) return studioHit;

        // 2. Ensure casting plan exists in Rust core so DramaLiveSentenceAsks returns clips!
        var plan = EnsureCastPlan(library, bookId, language, narrator);
        if (plan is null) return null;

        SoundLibraryService.Register(library.Core, library.DataDir);

        ClipAsk[] asks;
        DramaSound sound;
        bool dramaMode;
        try
        {
            asks = library.Core.DramaLiveSentenceAsks(bookId, section, sentence);
            if (asks.Length == 0) return null;
            sound = library.Core.DramaSound(bookId);
            dramaMode = library.Core.DramaMode(bookId);
        }
        catch
        {
            return null;
        }

        // 3. Check 500 MB live-clips SHA-256 disk cache
        var keySb = new StringBuilder($"v2:{section}:{sentence}:{dramaMode}:{sound.Acting}:{sound.Beds}:{sound.Spots}");
        var tunings = new RoleVoice?[asks.Length];
        for (int i = 0; i < asks.Length; i++)
        {
            var ask = asks[i];
            RoleVoice? tuning = null;
            try { tuning = library.Core.DramaClipTuning(bookId, ask.Section, ask.Sentence, ask.Start); }
            catch { }
            tunings[i] = tuning;
            string rKey = tuning is not null ? tuning.Rate.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "1.00";
            string pKey = tuning is not null ? tuning.Pitch.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "0.0";
            keySb.Append('|').Append(ask.Key).Append('#').Append(rKey).Append('#').Append(pKey);
        }

        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keySb.ToString())).AsSpan(0, 16)).ToLowerInvariant();
        string cacheFile = Path.Combine(LiveClipsFolder(library.DataDir, bookId), $"s-{section:D5}", $"{sentence}-{digest}.wav");
        if (File.Exists(cacheFile))
        {
            try
            {
                byte[] cachedBytes = await File.ReadAllBytesAsync(cacheFile, ct);
                if (cachedBytes.Length > 44) return cachedBytes;
            }
            catch { }
        }

        // 4. Synthesize each ClipAsk, apply per-role rate/pitch tuning, resample to 48 kHz mono float
        var madeClips = new List<MadeClip>(asks.Length);
        var sources = new List<MixSource>(asks.Length);
        var fallbackParts = new List<short[]>();
        int gap48k = (int)(MixSampleRate * 0.08);

        for (int i = 0; i < asks.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ask = asks[i];
            string plain = SpokenPlainText(library.Core, ask.Text);
            if (!plain.Any(char.IsLetterOrDigit))
            {
                madeClips.Add(new MadeClip(ask.Key, 0.0, []));
                continue;
            }

            string? voiceId = ResolveVoiceForAsk(ask.Voice, plan.Narrator);
            DramaAct? act = sound.Acting ? ask.Act : null;

            byte[]? wav;
            try
            {
                wav = await player.SynthesizeClipForDramaAsync(ask.Text, plain, voiceId, act, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { wav = null; }
            if (wav is null || wav.Length <= 44)
            {
                madeClips.Add(new MadeClip(ask.Key, 0.0, []));
                continue;
            }

            var (pcm16, sr) = AudioPlayer.DecodeWavPcm16Mono(wav);
            if (pcm16.Length == 0)
            {
                madeClips.Add(new MadeClip(ask.Key, 0.0, []));
                continue;
            }

            var tuning = tunings[i];
            if (tuning is not null && (Math.Abs(tuning.Rate - 1.0) >= 0.01 || Math.Abs(tuning.Pitch) >= 0.05))
            {
                try
                {
                    pcm16 = YuyueCoreMethods.TuneSpeechPcm(
                        pcm16,
                        sr,
                        (float)Math.Clamp(tuning.Rate, 0.6, 1.8),
                        (float)Math.Clamp(tuning.Pitch, -6.0, 6.0));
                }
                catch { }
            }

            if (sr != MixSampleRate)
                pcm16 = AudioPlayer.ResampleMono16(pcm16, sr, MixSampleRate);

            var floats = new float[pcm16.Length];
            for (int k = 0; k < pcm16.Length; k++) floats[k] = pcm16[k] / 32768f;

            madeClips.Add(new MadeClip(ask.Key, floats.Length / (double)MixSampleRate, []));
            sources.Add(new MixSource(ask.Key, floats, Stereo: false));

            if (fallbackParts.Count > 0) fallbackParts.Add(new short[gap48k]);
            fallbackParts.Add(pcm16);
        }

        if (sources.Count == 0) return null;

        byte[]? resultWav = null;
        try
        {
            var window = library.Core.DramaLiveSentenceWindow(bookId, section, sentence, madeClips.ToArray());
            if (window is not null)
            {
                using var mixer = new DramaMixer(window.Timeline, sources.ToArray());
                float[] mixed = mixer.RenderSpan(window.From, window.To);
                if (mixed.Length > 0)
                    resultWav = WrapFloatMonoAsWav48k(mixed);
            }
        }
        catch { }

        if (resultWav is null && fallbackParts.Count > 0)
        {
            int totalSamples = fallbackParts.Sum(p => p.Length);
            byte[] merged = new byte[totalSamples * 2];
            int off = 0;
            foreach (var part in fallbackParts)
            {
                Buffer.BlockCopy(part, 0, merged, off, part.Length * 2);
                off += part.Length * 2;
            }
            resultWav = CloudVoiceService.WrapPcm16MonoInWav(merged, (int)MixSampleRate);
        }

        if (resultWav is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
                string tmp = cacheFile + ".tmp";
                await File.WriteAllBytesAsync(tmp, resultWav, ct);
                File.Move(tmp, cacheFile, overwrite: true);
                PruneLiveCacheIfNeeded(library.DataDir, bookId, keepFromSection: section);
            }
            catch { }
        }

        return resultWav;
    }

    public static void PruneLiveCacheIfNeeded(string dataDir, string bookId, uint keepFromSection)
    {
        try
        {
            string root = LiveClipsFolder(dataDir, bookId);
            if (!Directory.Exists(root)) return;

            var dirs = new List<(uint Section, string Path, long Bytes)>();
            long total = 0;
            foreach (string sub in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(sub);
                uint sec = name.StartsWith("s-", StringComparison.Ordinal) && uint.TryParse(name[2..], out var parsed) ? parsed : 0;
                long sz = 0;
                foreach (string f in Directory.EnumerateFiles(sub))
                    try { sz += new FileInfo(f).Length; } catch { }
                total += sz;
                dirs.Add((sec, sub, sz));
            }
            if (total <= MaxLiveCacheBytes) return;

            var ordered = dirs
                .OrderByDescending(d => d.Section < keepFromSection)
                .ThenBy(d => d.Section);
            foreach (var item in ordered)
            {
                if (item.Section == keepFromSection) continue;
                if (total <= MaxLiveCacheBytes) break;
                try
                {
                    Directory.Delete(item.Path, recursive: true);
                    total -= item.Bytes;
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Starts background Studio pre-synthesis (<c>后台预合成全书</c>) for <paramref name="bookId"/> starting from
    /// <paramref name="focusSection"/> (1:1 <c>DramaSynth.swift:285-367</c>).
    /// </summary>
    public static void StartPreRender(
        LibraryService library,
        string bookId,
        string language,
        uint focusSection,
        string? narrator = null,
        Action? onSectionMade = null,
        bool requireAcPower = false)
    {
        if (PreRenderLoops.ContainsKey(bookId)) return;
        if (requireAcPower && !IsOnAcPower()) return;

        var cts = new CancellationTokenSource();
        if (!PreRenderLoops.TryAdd(bookId, cts))
        {
            cts.Dispose();
            return;
        }
        var ct = cts.Token;

        _ = Task.Run(async () =>
        {
            using var synthPlayer = new AudioPlayer(library);
            synthPlayer.SetBookContext(bookId, language);
            try
            {
                UpdateDramaCaps(library, bookId, language, narrator, ttsActive: true, allowLlm: false);
                SoundLibraryService.Register(library.Core, library.DataDir);
                var plan = EnsureCastPlan(library, bookId, language, narrator);
                if (plan is null) return;

                Directory.CreateDirectory(ClipsFolder(library.DataDir, bookId));
                bool chinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

                uint totalSections = 1;
                try { totalSections = (uint)Math.Max(1, library.GetBookDetail(bookId).Sections.Length); }
                catch { }

                uint focus = focusSection;
                uint passed = 0;
                while (!ct.IsCancellationRequested && passed < totalSections)
                {
                    if (requireAcPower && !IsOnAcPower()) break;

                    DramaTask? task;
                    try { task = library.Core.DramaNextTask(bookId, focus); }
                    catch { break; }

                    if (task is null)
                    {
                        PruneLiveCacheIfNeeded(library.DataDir, bookId, keepFromSection: focus);
                        focus = (focus + 2) % totalSections;
                        passed += 2;
                        SectionMade?.Invoke(bookId);
                        onSectionMade?.Invoke();
                        continue;
                    }

                    (string taskId, DramaResult result) = task switch
                    {
                        DramaTask.Tts t => (t.Id, await RenderStudioClipTaskAsync(synthPlayer, library, bookId, t.Clip, plan.Narrator, chinese, ct)),
                        DramaTask.Sfx sfx => (sfx.Id, await RenderSfxTaskAsync(library, sfx.Description, sfx.Seconds, ct)),
                        DramaTask.Llm llm => (llm.Id, new DramaResult.Returned()),
                        DramaTask.DesignVoice dv => (dv.Id, new DramaResult.Returned()),
                        _ => ("", new DramaResult.Returned()),
                    };

                    if (string.IsNullOrEmpty(taskId)) break;
                    bool isReturned = result is DramaResult.Returned;
                    if (isReturned && !taskId.StartsWith("tts:", StringComparison.Ordinal))
                    {
                        try { library.Core.DramaSubmit(bookId, taskId, result); } catch { }
                        break;
                    }

                    try
                    {
                        library.Core.DramaSubmit(bookId, taskId, ct.IsCancellationRequested ? new DramaResult.Returned() : result);
                    }
                    catch { break; }

                    if (result is DramaResult.Voice) InvalidateCast(bookId);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                PreRenderLoops.TryRemove(bookId, out _);
                UpdateDramaCaps(library, bookId, language, narrator, ttsActive: false, allowLlm: VoiceCloneAndDramaDialogs.IsDramaWorkerRunning(bookId));
                SectionMade?.Invoke(bookId);
                onSectionMade?.Invoke();
            }
        }, ct);
    }

    public static void StopPreRender(string bookId)
    {
        if (PreRenderLoops.TryRemove(bookId, out var cts))
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Handles a LAN phone <c>/want</c> pre-render notification when <c>Prefs.DramaPreRenderPhone</c> is enabled
    /// and the PC is on AC power (1:1 <c>RecordingShare.swift</c> + <c>MacListeningSettings.swift</c>).
    /// </summary>
    public static void EnqueuePhoneWant(LibraryService library, string bookId, uint section)
    {
        if (!library.Prefs.DramaPreRenderPhone || !IsOnAcPower()) return;
        string lang = "zh-Hans";
        try { lang = library.GetBookDetail(bookId).Summary.Language ?? "zh-Hans"; } catch { }
        StartPreRender(library, bookId, lang, section, requireAcPower: true);
    }

    private static async Task<DramaResult> RenderStudioClipTaskAsync(
        AudioPlayer player,
        LibraryService library,
        string bookId,
        ClipAsk clip,
        string fallbackNarrator,
        bool chinese,
        CancellationToken ct)
    {
        try
        {
            string plain = SpokenPlainText(library.Core, clip.Text);
            if (!plain.Any(char.IsLetterOrDigit))
            {
                return new DramaResult.Clip(
                    new MadeClip(clip.Key, 0.0, []),
                    new ClipCheck(0f, 0u, 1f, false, 0f, 0f, 0f, 0u));
            }

            string? voiceId = ResolveVoiceForAsk(clip.Voice, fallbackNarrator);
            DramaSound sound;
            try { sound = library.Core.DramaSound(bookId); }
            catch { sound = new DramaSound(true, true, true); }

            byte[]? wav = await player.SynthesizeClipForDramaAsync(clip.Text, plain, voiceId, sound.Acting ? clip.Act : null, ct);
            if (wav is null || wav.Length <= 44)
                return new DramaResult.Failed("语音引擎未返回音频");

            var (pcm16, sr) = AudioPlayer.DecodeWavPcm16Mono(wav);
            if (pcm16.Length == 0)
                return new DramaResult.Failed("解码音频为空");

            try
            {
                var tuning = library.Core.DramaClipTuning(bookId, clip.Section, clip.Sentence, clip.Start);
                if (tuning is not null && (Math.Abs(tuning.Rate - 1.0) >= 0.01 || Math.Abs(tuning.Pitch) >= 0.05))
                {
                    pcm16 = YuyueCoreMethods.TuneSpeechPcm(
                        pcm16,
                        sr,
                        (float)Math.Clamp(tuning.Rate, 0.6, 1.8),
                        (float)Math.Clamp(tuning.Pitch, -6.0, 6.0));
                }
            }
            catch { }

            if (sr != MixSampleRate)
                pcm16 = AudioPlayer.ResampleMono16(pcm16, sr, MixSampleRate);

            var floats = new float[pcm16.Length];
            for (int i = 0; i < pcm16.Length; i++) floats[i] = pcm16[i] / 32768f;

            byte[] outWav = WrapFloatMonoAsWav48k(floats);
            string dest = ClipPath(library.DataDir, bookId, clip.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            string tmp = dest + ".tmp";
            await File.WriteAllBytesAsync(tmp, outWav, ct);
            File.Move(tmp, dest, overwrite: true);

            double seconds = floats.Length / (double)MixSampleRate;
            var check = library.Core.DramaMeasureClip(clip.Text, plain, floats, MixSampleRate, 1.0f, chinese);
            return new DramaResult.Clip(new MadeClip(clip.Key, seconds, []), check);
        }
        catch (OperationCanceledException)
        {
            return new DramaResult.Returned();
        }
        catch (Exception ex)
        {
            return new DramaResult.Failed(ex.Message);
        }
    }

    private static async Task<DramaResult> RenderSfxTaskAsync(
        LibraryService library,
        string description,
        float seconds,
        CancellationToken ct)
    {
        try
        {
            byte[]? wav = await SoundLibraryService.GenerateSfxWavAsync(library.DataDir, description, seconds, ct);
            if (wav is null || wav.Length <= 44) return new DramaResult.Returned();
            var (pcm16, sr) = AudioPlayer.DecodeWavPcm16Mono(wav);
            if (pcm16.Length == 0) return new DramaResult.Returned();
            if (sr != MixSampleRate) pcm16 = AudioPlayer.ResampleMono16(pcm16, sr, MixSampleRate);
            var floats = new float[pcm16.Length];
            for (int i = 0; i < pcm16.Length; i++) floats[i] = pcm16[i] / 32768f;
            return new DramaResult.Sound(floats);
        }
        catch (OperationCanceledException)
        {
            return new DramaResult.Returned();
        }
        catch (Exception ex)
        {
            return new DramaResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Mixes a completed section's timelines into a 48 kHz stereo WAV byte array (1:1 <c>DramaSynth.mixSection</c>).
    /// </summary>
    public static byte[]? MixSectionWav(LibraryService library, string bookId, uint section)
    {
        SoundLibraryService.Register(library.Core, library.DataDir);
        DramaTimeline[] timelines;
        try { timelines = library.Core.DramaTimelines(bookId, section, []); }
        catch { return null; }
        if (timelines.Length == 0) return null;

        byte[]? header = null;
        using var body = new MemoryStream();
        foreach (var tl in timelines)
        {
            var sources = new List<MixSource>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in tl.Items)
            {
                if (item is TimelineItem.Voice v && seen.Add(v.Clip))
                {
                    float[]? samples = ReadClipFloat48k(library.DataDir, bookId, v.Clip);
                    if (samples is not null)
                        sources.Add(new MixSource(v.Clip, samples, Stereo: false));
                }
            }
            using var mixer = new DramaMixer(tl, sources.ToArray());
            byte[] wav = mixer.RenderWav();
            if (wav.Length <= 44) continue;
            header ??= wav[..44];
            body.Write(wav, 44, wav.Length - 44);
        }

        if (header is null || body.Length == 0) return null;
        uint dataLen = (uint)body.Length;
        byte[] result = new byte[44 + dataLen];
        Buffer.BlockCopy(header, 0, result, 0, 44);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), dataLen + 36u);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40, 4), dataLen);
        Buffer.BlockCopy(body.GetBuffer(), 0, result, 44, (int)dataLen);
        return result;
    }

    /// <summary>
    /// Exports all pre-rendered sections of <paramref name="bookId"/> into a single chaptered <c>.m4b</c> audiobook
    /// using Windows Media Foundation AAC encoding + <see cref="YuyueCore.DramaFinishM4b"/> (1:1 <c>DramaSynth.exportM4B</c>).
    /// </summary>
    public static async Task<bool> ExportM4bAsync(
        LibraryService library,
        string bookId,
        string outM4bPath,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default)
    {
        DramaMade made;
        BookDetail detail;
        try
        {
            made = library.Core.DramaMade(bookId);
            detail = library.GetBookDetail(bookId);
        }
        catch { return false; }

        if (made.MadeSections.Length == 0) return false;

        string scratchDir = Path.Combine(library.DataDir, "drama", bookId, "export-tmp");
        Directory.CreateDirectory(scratchDir);
        string combinedWav = Path.Combine(scratchDir, $"book-{Guid.NewGuid():N}.wav");
        string tempM4a = Path.Combine(scratchDir, $"book-{Guid.NewGuid():N}.m4a");

        try
        {
            var marks = new List<AudioMark>();
            long totalDataBytes = 0;
            byte[]? firstHeader = null;
            int totalCount = made.MadeSections.Length;

            using (var fs = new FileStream(combinedWav, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                fs.Write(new byte[44]); // placeholder RIFF header
                for (int i = 0; i < totalCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    uint secIdx = made.MadeSections[i];
                    onProgress?.Invoke(i + 1, totalCount);

                    byte[]? secWav = await Task.Run(() => MixSectionWav(library, bookId, secIdx), ct);
                    if (secWav is null || secWav.Length <= 44) continue;

                    firstHeader ??= secWav[..44];
                    // 48 kHz stereo 16-bit = 4 bytes per frame
                    double startSec = (totalDataBytes / 4.0) / MixSampleRate;
                    string? title = secIdx < detail.Sections.Length ? detail.Sections[secIdx].Title : null;
                    marks.Add(new AudioMark(title, startSec));

                    await fs.WriteAsync(secWav.AsMemory(44), ct);
                    totalDataBytes += secWav.Length - 44;
                }

                if (firstHeader is null || totalDataBytes <= 0) return false;
                fs.Seek(0, SeekOrigin.Begin);
                BinaryPrimitives.WriteUInt32LittleEndian(firstHeader.AsSpan(4, 4), (uint)Math.Min(uint.MaxValue - 36, totalDataBytes + 36));
                BinaryPrimitives.WriteUInt32LittleEndian(firstHeader.AsSpan(40, 4), (uint)Math.Min(uint.MaxValue, totalDataBytes));
                fs.Write(firstHeader, 0, 44);
            }

            // Encode combined WAV -> AAC .m4a via Windows Media Foundation MediaTranscoder
            var srcFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(combinedWav));
            var dstFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(scratchDir));
            var dstFile = await dstFolder.CreateFileAsync(Path.GetFileName(tempM4a), CreationCollisionOption.ReplaceExisting);

            var profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium);
            profile.Audio.SampleRate = MixSampleRate;
            profile.Audio.ChannelCount = 2;
            profile.Audio.Bitrate = 96_000;

            var transcoder = new MediaTranscoder();
            var prep = await transcoder.PrepareFileTranscodeAsync(srcFile, dstFile, profile);
            if (!prep.CanTranscode) return false;

            var op = prep.TranscodeAsync();
            using var reg = ct.Register(() => { try { op.Cancel(); } catch { } });
            await op;
            ct.ThrowIfCancellationRequested();

            // Write MP4 chapter metadata atoms via Rust core
            library.Core.DramaFinishM4b(bookId, tempM4a, marks.ToArray());

            string? targetDir = Path.GetDirectoryName(outM4bPath);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
            if (File.Exists(outM4bPath)) File.Delete(outM4bPath);
            File.Move(tempM4a, outM4bPath);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(combinedWav)) File.Delete(combinedWav); } catch { }
            try { if (File.Exists(tempM4a)) File.Delete(tempM4a); } catch { }
        }
    }

    public static DramaCaps BuildCaps(
        LibraryService library,
        string language,
        string? narrator,
        bool ttsActive,
        bool allowLlm = false)
    {
        string? effNarr = EffectiveNarrator(library, language, narrator);
        bool castsCloud = effNarr is not null && effNarr.StartsWith("cloud.", StringComparison.Ordinal);
        bool canTts = ttsActive || library.LiveVoice.IsReady || library.Kokoro.IsInstalled || castsCloud;
        return new DramaCaps(
            Llm: allowLlm && library.Companion.IsConfigured,
            VerifyDistinct: false,
            ContextTokens: 120_000,
            Tts: canTts,
            DesignVoice: false,
            Sfx: SoundLibraryService.IsSfxMakerAvailable,
            Combo: $"{library.Prefs.AiProvider}/{library.Prefs.AiModel}",
            Engine: castsCloud ? effNarr! : (library.LiveVoice.IsReady ? "zipvoice-1" : (library.Kokoro.IsInstalled ? "kokoro-1" : "sapi")),
            TtsSameEachTime: !castsCloud);
    }

    public static void UpdateDramaCaps(
        LibraryService library,
        string bookId,
        string language,
        string? narrator,
        bool ttsActive,
        bool allowLlm = false)
    {
        try
        {
            library.Core.DramaSetCaps(BuildCaps(library, language, narrator, ttsActive, allowLlm));
        }
        catch { }
    }

    public static float[]? ReadClipFloat48k(string dataDir, string bookId, string key)
    {
        string path = ClipPath(dataDir, bookId, key);
        if (!File.Exists(path)) return null;
        try
        {
            byte[] wav = File.ReadAllBytes(path);
            var (pcm16, sr) = AudioPlayer.DecodeWavPcm16Mono(wav);
            if (pcm16.Length == 0) return null;
            if (sr != MixSampleRate) pcm16 = AudioPlayer.ResampleMono16(pcm16, sr, MixSampleRate);
            var floats = new float[pcm16.Length];
            for (int i = 0; i < pcm16.Length; i++) floats[i] = pcm16[i] / 32768f;
            return floats;
        }
        catch { return null; }
    }

    private static string SpokenPlainText(YuyueCore core, string text)
    {
        try
        {
            var parts = core.DramaSpokenParts(text);
            if (parts.Length > 0) return string.Concat(parts.Select(p => p.Text));
        }
        catch { }
        return CardVoiceEngine.PlainSpokenText(text);
    }

    private static string? ResolveVoiceForAsk(string askVoice, string fallbackNarrator)
    {
        string? norm = NormalizeVoiceId(askVoice);
        if (!string.IsNullOrEmpty(norm)) return norm;
        return NormalizeVoiceId(fallbackNarrator);
    }

    private static byte[] WrapFloatMonoAsWav48k(float[] mono)
    {
        byte[] pcm = new byte[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
        {
            short s = (short)Math.Clamp(Math.Round(mono[i] * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return CloudVoiceService.WrapPcm16MonoInWav(pcm, (int)MixSampleRate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    public static bool IsOnAcPower()
    {
        try
        {
            if (GetSystemPowerStatus(out var st))
                return st.ACLineStatus != 0; // 1 = AC online, 255 = unknown (desktop)
        }
        catch { }
        return true;
    }
}
