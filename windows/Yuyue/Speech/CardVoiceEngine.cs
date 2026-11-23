using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Yuyue.Core;

namespace Yuyue.Speech;

/// <summary>
/// On-device neural TTS engine running <c>ZipVoice-Distill-Int8</c> + <c>Vocos 24 kHz</c>
/// via ONNX Runtime, sharing tokenization, flow-matching schedules, Vocos ISTFT, and
/// quality gating with <see cref="YuyueCore"/>.
/// </summary>
public sealed class CardVoiceEngine : IDisposable
{
    public const int SampleRate = 24_000;
    private const int ClausePauseSamples = 2_880; // 0.12 s at 24 kHz
    private const long MaxBookCacheBytes = 500L * 1024L * 1024L;
    private const int MaxCachedPrompts = 16;

    public static CardVoiceEngine Instance { get; } = new();

    private sealed class Sessions : IDisposable
    {
        public string DirPath { get; }
        public InferenceSession Encoder { get; }
        public InferenceSession Decoder { get; }
        public InferenceSession Vocoder { get; }

        public Sessions(string dirPath, InferenceSession encoder, InferenceSession decoder, InferenceSession vocoder)
        {
            DirPath = dirPath;
            Encoder = encoder;
            Decoder = decoder;
            Vocoder = vocoder;
        }

        public void Dispose()
        {
            try { Encoder.Dispose(); } catch { }
            try { Decoder.Dispose(); } catch { }
            try { Vocoder.Dispose(); } catch { }
        }
    }

    private readonly SemaphoreSlim _inferGate = new(1, 1);
    private readonly object _promptLock = new();
    private readonly LinkedList<(string Key, ZipVoicePrompt Value)> _promptLru = new();
    private Sessions? _sessions;
    private int _writesSinceTrim;

    private CardVoiceEngine() { }

    public static bool SpeaksLanguage(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return true;
        string t = tag.Replace('_', '-').Trim().ToLowerInvariant();
        if (t == "yue" || t.StartsWith("zh-hk") || t.StartsWith("zh-mo")) return false;
        return t == "zh" || t.StartsWith("zh-") || t == "cmn" || t == "en" || t.StartsWith("en-");
    }

    public static string PlainSpokenText(string text)
    {
        if (!text.Contains('{')) return text;
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int a = text.IndexOf('{', i);
            if (a < 0) { sb.Append(text, i, text.Length - i); break; }
            int b = text.IndexOf('}', a + 1);
            if (b < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, a - i);
            string inner = text.Substring(a + 1, b - a - 1);
            int pipe = inner.IndexOf('|');
            sb.Append(pipe >= 0 ? inner[..pipe] : text.Substring(a, b - a + 1));
            i = b + 1;
        }
        return sb.ToString();
    }

    public void CloseSessions()
    {
        _inferGate.Wait();
        try
        {
            _sessions?.Dispose();
            _sessions = null;
            lock (_promptLock) _promptLru.Clear();
        }
        finally { _inferGate.Release(); }
    }

    public static string InferLanguage(string text, string fallback)
    {
        int cjk = 0, latin = 0;
        foreach (char ch in text)
        {
            if ((ch >= '\u4E00' && ch <= '\u9FFF') || (ch >= '\u3400' && ch <= '\u4DBF')) cjk++;
            else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) latin++;
        }
        if (cjk >= 3 && cjk >= latin) return "zh";
        if (latin >= 6 && cjk == 0) return "en";
        return fallback;
    }

    private static ulong DeterministicSeed(string a, string b)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte by in Encoding.UTF8.GetBytes(a)) { h ^= by; h *= 1099511628211UL; }
        h ^= 0xFF; h *= 1099511628211UL;
        foreach (byte by in Encoding.UTF8.GetBytes(b)) { h ^= by; h *= 1099511628211UL; }
        return h | 1UL;
    }

    /// <summary>
    /// Pre-save audition of a raw recorded/imported reference audio clip (<c>CardVoice.previewReference</c>).
    /// </summary>
    public async Task<byte[]?> PreviewReferenceWavAsync(
        YuyueCore core,
        LiveVoiceModel model,
        float[] refSamples,
        uint refRate,
        string refPromptText,
        string sampleText,
        string language,
        CancellationToken ct = default)
    {
        if (!model.IsModelInstalled || !SpeaksLanguage(language) || refSamples.Length < 400)
            return null;

        await _inferGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                var sess = EnsureSessions(core, model);
                if (sess is null) return null;

                string tmpRef = Path.Combine(Path.GetTempPath(), $"yuyue-clone-prev-{Guid.NewGuid():N}.wav");
                try
                {
                    WritePcm16MonoWav(tmpRef, refSamples, (int)refRate);
                    var prompt = core.LiveVoicePrompt(tmpRef, refPromptText, language);
                    var clauses = core.LiveVoiceClauses(sampleText, language);
                    if (clauses.Length == 0) return null;

                    ulong seed = DeterministicSeed(refPromptText, sampleText);
                    float[]? pcm = SynthesizeClauses(core, sess, prompt, clauses, language, seed, ct);
                    return pcm is { Length: > 0 } ? EncodeWav24kMono16(pcm) : null;
                }
                finally
                {
                    try { File.Delete(tmpRef); } catch { }
                }
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { _inferGate.Release(); }
    }

    /// <summary>
    /// Synthesizes one sentence into a 24 kHz 16-bit mono WAV byte buffer (with disk cache + quality gate).
    /// Returns <c>null</c> if the model isn't ready or both synthesis attempts fail quality gating.
    /// </summary>
    public async Task<byte[]?> SynthesizeWavAsync(
        YuyueCore core,
        LiveVoiceModel model,
        string dataDir,
        VoiceCard card,
        string? bookId,
        string text,
        string language,
        CardStyle style = CardStyle.Neutral,
        CancellationToken ct = default)
    {
        if (!model.IsModelInstalled || !SpeaksLanguage(language)) return null;
        string plain = PlainSpokenText(text);
        if (!plain.Any(char.IsLetterOrDigit)) return null;
        string effLang = InferLanguage(plain, language);

        string cacheFile = GetCacheFilePath(dataDir, bookId, card.Id, card.Version, style, effLang, text);
        if (File.Exists(cacheFile))
        {
            try
            {
                File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow);
                return await File.ReadAllBytesAsync(cacheFile, ct);
            }
            catch { }
        }

        await _inferGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await Task.Run(() =>
            {
                var sess = EnsureSessions(core, model);
                if (sess is null) return null;

                var prompt = ResolvePrompt(core, card, bookId, style);
                if (prompt is null) return null;

                var clauses = core.LiveVoiceClauses(text, effLang);
                if (clauses.Length == 0) return null;

                ulong baseSeed = DeterministicSeed(card.Id, text);
                for (int attempt = 0; attempt <= 1; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    float[]? pcm = null;
                    try { pcm = SynthesizeClauses(core, sess, prompt, clauses, effLang, baseSeed + (ulong)attempt, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { pcm = null; }

                    if (pcm is { Length: > 0 } && core.LiveVoiceCheck(pcm, SampleRate, plain, language, card.Rate))
                    {
                        byte[] wav = EncodeWav24kMono16(pcm);
                        TryWriteCache(cacheFile, wav);
                        return wav;
                    }
                }
                return null;
            }, ct);
        }
        finally
        {
            _inferGate.Release();
        }
    }

    private Sessions? EnsureSessions(YuyueCore core, LiveVoiceModel model)
    {
        if (!model.IsModelInstalled) return null;
        model.RegisterIfInstalled();
        string dir = model.ModelDir;
        if (_sessions is not null && string.Equals(_sessions.DirPath, dir, StringComparison.OrdinalIgnoreCase))
            return _sessions;

        _sessions?.Dispose();
        _sessions = null;

        try
        {
            int threads = Math.Clamp(Environment.ProcessorCount - 1, 2, 4);
            SessionOptions MakeOpts()
            {
                var opts = new SessionOptions
                {
                    EnableCpuMemArena = false,
                    EnableMemoryPattern = false,
                    IntraOpNumThreads = threads,
                };
                try { opts.AddSessionConfigEntry("session.use_env_allocators", "0"); } catch { }
                return opts;
            }

            using var o1 = MakeOpts();
            using var o2 = MakeOpts();
            using var o3 = MakeOpts();
            var enc = new InferenceSession(Path.Combine(dir, "encoder.int8.onnx"), o1);
            var dec = new InferenceSession(Path.Combine(dir, "decoder.int8.onnx"), o2);
            var voc = new InferenceSession(Path.Combine(dir, "vocos_24khz.onnx"), o3);
            _sessions = new Sessions(dir, enc, dec, voc);
            return _sessions;
        }
        catch
        {
            return null;
        }
    }

    private ZipVoicePrompt? ResolvePrompt(YuyueCore core, VoiceCard card, string? bookId, CardStyle style)
    {
        var clip = core.CardClipResolved(bookId, card.Id, style);
        if (clip is null) return null;
        string key = $"{card.Id}@{card.Version}/{clip.Style}";

        lock (_promptLock)
        {
            for (var node = _promptLru.First; node is not null; node = node.Next)
            {
                if (node.Value.Key == key)
                {
                    _promptLru.Remove(node);
                    _promptLru.AddFirst(node);
                    return node.Value.Value;
                }
            }
        }

        ZipVoicePrompt built;
        try { built = core.LiveVoicePrompt(clip.File, clip.Text, card.Lang); }
        catch { return null; }

        lock (_promptLock)
        {
            _promptLru.AddFirst((key, built));
            while (_promptLru.Count > MaxCachedPrompts) _promptLru.RemoveLast();
        }
        return built;
    }

    private static float[] SynthesizeClauses(
        YuyueCore core, Sessions sess, ZipVoicePrompt prompt,
        string[] clauses, string language, ulong seed, CancellationToken ct)
    {
        var parts = new List<float[]>(clauses.Length);
        int totalLen = 0;
        for (int idx = 0; idx < clauses.Length; idx++)
        {
            ct.ThrowIfCancellationRequested();
            var pcm = SynthesizeOneClause(core, sess, prompt, clauses[idx], language, seed + (ulong)idx * 97UL);
            if (pcm.Length > 0)
            {
                if (parts.Count > 0) totalLen += ClausePauseSamples;
                parts.Add(pcm);
                totalLen += pcm.Length;
            }
        }
        if (parts.Count == 0) return Array.Empty<float>();
        if (parts.Count == 1) return parts[0];

        var output = new float[totalLen];
        int pos = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) pos += ClausePauseSamples;
            Array.Copy(parts[i], 0, output, pos, parts[i].Length);
            pos += parts[i].Length;
        }
        return output;
    }

    private static float[] SynthesizeOneClause(
        YuyueCore core, Sessions sess, ZipVoicePrompt prompt,
        string clause, string language, ulong seed)
    {
        long[] tokens = core.LiveVoiceTokens(clause, language);
        if (tokens.Length == 0 || prompt.PromptTokens.Length == 0) return Array.Empty<float>();

        int promptFrames = (int)prompt.PromptFrames;

        // 1. Encoder -> text_condition [1, T_tot, 100]
        var tokTensor = new DenseTensor<long>(tokens, [1, tokens.Length]);
        var pTokTensor = new DenseTensor<long>(prompt.PromptTokens, [1, prompt.PromptTokens.Length]);
        var pLenTensor = new DenseTensor<long>(new long[] { promptFrames }, ReadOnlySpan<int>.Empty);
        var speedTensor = new DenseTensor<float>(new float[] { 1.0f }, ReadOnlySpan<int>.Empty);

        int tTot;
        float[] tcData;
        using (var encResults = sess.Encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("tokens", tokTensor),
            NamedOnnxValue.CreateFromTensor("prompt_tokens", pTokTensor),
            NamedOnnxValue.CreateFromTensor("prompt_features_len", pLenTensor),
            NamedOnnxValue.CreateFromTensor("speed", speedTensor),
        ]))
        {
            var tcTensor = encResults.First().AsTensor<float>();
            tTot = tcTensor.Dimensions[1];
            tcData = tcTensor.ToArray();
        }

        int kept = tTot - promptFrames;
        if (kept <= 0 || tcData.Length < tTot * 100) return Array.Empty<float>();

        // 2. Flow-matching Euler decoder (3 steps, guidance_scale = 3.0)
        int totalElems = tTot * 100;
        float[] x = core.LiveVoiceNoise((uint)totalElems, seed);
        var scData = new float[totalElems];
        int copyCount = Math.Min(Math.Min(promptFrames * 100, prompt.PromptFeatures.Length), scData.Length);
        Array.Copy(prompt.PromptFeatures, 0, scData, 0, copyCount);

        float[] timesteps = core.LiveVoiceTimesteps(3, 0.5f);
        int[] shape3d = [1, tTot, 100];
        var tcInput = new DenseTensor<float>(tcData, shape3d);
        var scInput = new DenseTensor<float>(scData, shape3d);
        var gsInput = new DenseTensor<float>(new float[] { 3.0f }, ReadOnlySpan<int>.Empty);

        for (int step = 0; step < timesteps.Length - 1; step++)
        {
            float tCur = timesteps[step];
            float dt = timesteps[step + 1] - tCur;
            var tInput = new DenseTensor<float>(new float[] { tCur }, ReadOnlySpan<int>.Empty);
            var xInput = new DenseTensor<float>(x, shape3d);
            using var decResults = sess.Decoder.Run(
            [
                NamedOnnxValue.CreateFromTensor("t", tInput),
                NamedOnnxValue.CreateFromTensor("x", xInput),
                NamedOnnxValue.CreateFromTensor("text_condition", tcInput),
                NamedOnnxValue.CreateFromTensor("speech_condition", scInput),
                NamedOnnxValue.CreateFromTensor("guidance_scale", gsInput),
            ]);
            var vData = decResults.First().AsTensor<float>().ToArray();
            for (int i = 0; i < totalElems; i++) x[i] += vData[i] * dt;
        }

        // 3. Transpose generated suffix [kept, 100] * 10 -> [1, 100, kept] for Vocos 24 kHz
        var mels = new float[100 * kept];
        int baseOff = promptFrames * 100;
        for (int f = 0; f < kept; f++)
        {
            int row = baseOff + f * 100;
            for (int b = 0; b < 100; b++) mels[b * kept + f] = x[row + b] * 10f;
        }

        float[] mag, xCos, ySin;
        var melTensor = new DenseTensor<float>(mels, [1, 100, kept]);
        using (var vocResults = sess.Vocoder.Run([NamedOnnxValue.CreateFromTensor("mels", melTensor)]))
        {
            var list = vocResults.ToList();
            var magVal = list.FirstOrDefault(r => r.Name == "mag") ?? list[0];
            var xVal = list.FirstOrDefault(r => r.Name == "x") ?? list[1];
            var yVal = list.FirstOrDefault(r => r.Name == "y") ?? list[2];
            mag = magVal.AsTensor<float>().ToArray();
            xCos = xVal.AsTensor<float>().ToArray();
            ySin = yVal.AsTensor<float>().ToArray();
        }

        // 4. Vocos ISTFT + -20 LUFS loudness normalization in Rust core
        return core.LiveVoiceIstft(mag, xCos, ySin, (uint)kept);
    }

    private static void WritePcm16MonoWav(string path, float[] samples, int sampleRate)
    {
        byte[] pcm = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Math.Clamp(Math.Round(samples[i] * 32767.0), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        File.WriteAllBytes(path, CloudVoiceService.WrapPcm16MonoInWav(pcm, Math.Max(8000, sampleRate)));
    }

    private static byte[] EncodeWav24kMono16(float[] samples)
    {
        int dataBytes = samples.Length * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // mono
        bw.Write(SampleRate);
        bw.Write(SampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(dataBytes);
        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            bw.Write((short)(clamped * 32767f));
        }
        return ms.ToArray();
    }

    private static string GetCacheFilePath(string dataDir, string? bookId, string cardId, uint version, CardStyle style, string lang, string text)
    {
        string scope = string.IsNullOrEmpty(bookId) ? "shared" : bookId.Replace('/', '_').Replace('\\', '_');
        string raw = $"{cardId}@{version}/{style}\u0001{lang}\u0001{text}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        string hex = Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        return Path.Combine(dataDir, "cache", "live-voices", scope, $"{hex}.wav");
    }

    private void TryWriteCache(string path, byte[] wav)
    {
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(path, wav);

            if (++_writesSinceTrim >= 16)
            {
                _writesSinceTrim = 0;
                TrimFolder(folder, MaxBookCacheBytes);
            }
        }
        catch { }
    }

    private static void TrimFolder(string folder, long maxBytes)
    {
        try
        {
            var files = new DirectoryInfo(folder).GetFiles();
            long total = files.Sum(f => f.Length);
            if (total <= maxBytes) return;
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                long len = f.Length;
                try { f.Delete(); total -= len; } catch { }
                if (total <= maxBytes * 9 / 10) break;
            }
        }
        catch { }
    }

    public void Dispose() => CloseSessions();
}
