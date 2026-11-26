using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Yuyue.Core;

namespace Yuyue.Speech;

public enum KokoroStateKind
{
    Idle,
    Downloading,
    Ready,
    Failed,
}

/// <summary>
/// Cross-platform lightweight offline neural narrator (<c>Kokoro-82M-v1.1-zh Int8</c>, Apache-2.0, ~125 MB).
/// 1:1 port of <c>apple/Yuyue/Speech/KokoroVoice.swift</c> &amp; <c>android/.../KokoroVoice.kt</c>:
/// <list type="bullet">
///   <item>16 curated Chinese &amp; English natural narrator voices sharing Rust core G2P/lexicon/postprocessing</item>
///   <item>Primary single-bundle download from ModelScope / GitHub Releases (<c>kokoro-v1_1-1.zip</c>) with BLAKE3 check</item>
///   <item>Automatic fallback to per-file resumable download from <c>hf-mirror.com</c> / <c>huggingface.co</c> with BLAKE3 check</item>
///   <item>Supports queueing a speaker selection before download finishes so it activates automatically once ready</item>
/// </list>
/// </summary>
public sealed class KokoroVoiceModel
{
    public const string ModelName = "kokoro-v1_1-1";
    public const string ArchiveName = "kokoro-v1_1-1.zip";
    public const string BundleUrl = "https://github.com/inokoru/yuyue/releases/download/models-1/kokoro-v1_1-1.zip";
    public const string BundleDigest = "0fb9a0af735001f293549bfcf096c0a2e834472b419ecdbbd1d83531c3b791a3";
    public const long BundleBytes = 131_005_940L;
    public const int TotalMb = 125;
    private const string HfRepo = "csukuangfj/kokoro-int8-multi-lang-v1_1";

    public sealed record RawFileSpec(string Name, long Bytes, string Blake3);

    public static readonly RawFileSpec[] RawFiles =
    [
        new("model.int8.onnx", 114_203_914L, "9c85d5920c66ca9ff88844b0de859df9badc29a3cada0e3696b993f111cf58b6"),
        new("voices.bin", 53_790_720L, "07d6924043f9d4b7fb13149f74ee702321c9a02baadf743bc9c307499471bcd6"),
        new("tokens.txt", 1_111L, "6f6916d8b098628f18a2d4c66cf9a38af6de85e78ba8343a8787ff4bd9630885"),
        new("lexicon-zh.txt", 2_120_131L, "d1c4160a6ffcee2fdba5b1a42c3c358f0eec8063935cac22db341ebfee782950"),
        new("lexicon-us-en.txt", 5_956_885L, "6c7e13b534ce7bf20884295e1ae3687010ed444ad01ef9a101d23a9dbef3a01e"),
    ];

    public static readonly KokoroSpeaker[] FallbackSpeakers =
    [
        new("kokoro.zf_001", "zf_001", 3, "清禾", "zh", CharacterGender.F, true, "温润清朗 · 默认中文女声"),
        new("kokoro.zf_002", "zf_002", 4, "知夏", "zh", CharacterGender.F, false, "柔和舒缓 · 适合散文与小说"),
        new("kokoro.zf_004", "zf_004", 6, "云舒", "zh", CharacterGender.F, false, "沉静端庄 · 适合历史与社科"),
        new("kokoro.zf_006", "zf_006", 8, "茗烟", "zh", CharacterGender.F, false, "轻快自然 · 适合随笔与故事"),
        new("kokoro.zf_018", "zf_018", 12, "晚晴", "zh", CharacterGender.F, false, "柔和细腻 · 适合夜读"),
        new("kokoro.zf_028", "zf_028", 20, "素心", "zh", CharacterGender.F, false, "干净利落 · 适合非虚构长文"),
        new("kokoro.zf_079", "zf_079", 46, "凌霜", "zh", CharacterGender.F, false, "清冷干练 · 适合独立女主"),
        new("kokoro.zf_074", "zf_074", 41, "映月", "zh", CharacterGender.F, false, "婉转清澈 · 适合古典与言情"),
        new("kokoro.zf_090", "zf_090", 53, "疏桐", "zh", CharacterGender.F, false, "沉着知性 · 适合熟龄女角"),
        new("kokoro.zf_094", "zf_094", 56, "晴川", "zh", CharacterGender.F, false, "明朗清脆 · 适合少女角色"),
        new("kokoro.zf_024", "zf_024", 17, "念安", "zh", CharacterGender.F, false, "温婉平和 · 适合日常对话"),
        new("kokoro.zf_027", "zf_027", 19, "语微", "zh", CharacterGender.F, false, "轻柔低语 · 适合细腻内心戏"),
        new("kokoro.zf_019", "zf_019", 13, "含烟", "zh", CharacterGender.F, false, "灵动俏皮 · 适合活泼角色"),
        new("kokoro.zf_076", "zf_076", 43, "听兰", "zh", CharacterGender.F, false, "雅致从容 · 适合长辈与配角"),
        new("kokoro.zm_010", "zm_010", 59, "沉舟", "zh", CharacterGender.M, true, "醇厚稳重 · 推荐中文男声"),
        new("kokoro.zm_009", "zm_009", 58, "松风", "zh", CharacterGender.M, false, "清朗平和 · 适合纪实与传记"),
        new("kokoro.zm_012", "zm_012", 61, "远山", "zh", CharacterGender.M, false, "低沉内敛 · 适合经典文学"),
        new("kokoro.zm_025", "zm_025", 67, "怀瑾", "zh", CharacterGender.M, false, "青年明快 · 适合科幻与通识"),
        new("kokoro.zm_035", "zm_035", 73, "竹轩", "zh", CharacterGender.M, false, "温厚从容 · 适合长篇慢听"),
        new("kokoro.zm_052", "zm_052", 78, "景行", "zh", CharacterGender.M, false, "清晰有力 · 适合论著与技术"),
        new("kokoro.zm_098", "zm_098", 101, "沧澜", "zh", CharacterGender.M, false, "苍劲浑厚 · 适合长者与反派"),
        new("kokoro.zm_056", "zm_056", 82, "拂晓", "zh", CharacterGender.M, false, "爽朗少年 · 适合年轻男角"),
        new("kokoro.zm_041", "zm_041", 75, "临渊", "zh", CharacterGender.M, false, "冷静深沉 · 适合悬疑与谋士"),
        new("kokoro.zm_082", "zm_082", 95, "昭明", "zh", CharacterGender.M, false, "端正大气 · 适合正派主角"),
        new("kokoro.zm_033", "zm_033", 71, "墨白", "zh", CharacterGender.M, false, "温润书生 · 适合文人角色"),
        new("kokoro.zm_095", "zm_095", 98, "听涛", "zh", CharacterGender.M, false, "豪迈粗犷 · 适合侠客配角"),
        new("kokoro.zm_080", "zm_080", 93, "敬之", "zh", CharacterGender.M, false, "沉稳温和 · 适合中年男角"),
        new("kokoro.zm_096", "zm_096", 99, "扶摇", "zh", CharacterGender.M, false, "轻快松弛 · 适合幽默角色"),
        new("kokoro.af_maple", "af_maple", 0, "Maple", "en", CharacterGender.F, true, "Warm American narrator · Default"),
        new("kokoro.bf_vale", "bf_vale", 2, "Vale", "en", CharacterGender.F, true, "Crisp British RP narrator"),
        new("kokoro.af_sol", "af_sol", 1, "Sol", "en", CharacterGender.F, false, "Bright conversational American"),
        new("kokoro.zm_010.en", "zm_010", 59, "Arthur (Bilingual)", "en", CharacterGender.M, false, "Calm bilingual male narrator"),
    ];

    private readonly YuyueCore _core;
    private readonly string _dataDir;
    private readonly Action<string, string>? _applyPendingVoice;
    private readonly Dictionary<string, string> _pendingVoices = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _downloadCts;

    public KokoroStateKind State { get; private set; } = KokoroStateKind.Idle;
    public double DownloadProgress { get; private set; }
    public string StatusMessage { get; private set; } = "";
    /// <summary>
    /// Offline narrator voice that finished downloading while <see cref="AudioPlayer"/> was actively playing;
    /// held here instead of forcing a mid-sentence voice swap until playback stops or the user accepts switching.
    /// </summary>
    public string? ReadyOfflineVoice { get; private set; }

    public event Action? StateChanged;
    public event Action? Ready;

    public KokoroVoiceModel(YuyueCore core, string dataDir, Action<string, string>? applyPendingVoice = null)
    {
        _core = core;
        _dataDir = dataDir;
        _applyPendingVoice = applyPendingVoice;
        RegisterIfInstalled();
    }

    public string ModelDir => Path.Combine(_dataDir, "models", ModelName);

    public bool IsInstalled =>
        RawFiles.All(f => File.Exists(Path.Combine(ModelDir, f.Name)));

    public void RegisterIfInstalled()
    {
        try
        {
            if (IsInstalled)
            {
                _core.KokoroSetDir(ModelDir);
                State = KokoroStateKind.Ready;
                DownloadProgress = 1.0;
                StatusMessage = $"已就绪（{GetAllSpeakers().Count} 个中英离线旁白）";
            }
            else if (State == KokoroStateKind.Ready)
            {
                State = KokoroStateKind.Idle;
                DownloadProgress = 0;
                StatusMessage = "";
            }
        }
        catch { }
    }

    public IReadOnlyList<KokoroSpeaker> GetAllSpeakers()
    {
        try
        {
            var list = _core.KokoroVoices();
            if (list is { Length: > 0 }) return list;
        }
        catch { }
        return FallbackSpeakers;
    }

    public IReadOnlyList<KokoroSpeaker> GetSpeakers(string? language)
    {
        var all = GetAllSpeakers();
        if (string.IsNullOrWhiteSpace(language)) return all;
        if (!CardVoiceEngine.SpeaksLanguage(language)) return Array.Empty<KokoroSpeaker>();
        string want = language.Trim().ToLowerInvariant().StartsWith("en", StringComparison.Ordinal) ? "en" : "zh";
        return all.Where(s => s.Lang.StartsWith(want, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public KokoroSpeaker? DefaultNarrator(string? language)
    {
        if (!IsInstalled || !CardVoiceEngine.SpeaksLanguage(language)) return null;
        var list = GetSpeakers(language);
        if (list.Count == 0) return null;
        bool isEn = !string.IsNullOrWhiteSpace(language) && language.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase);
        string preferred = isEn ? "kokoro.af_maple" : "kokoro.zf_001";
        return list.FirstOrDefault(s => s.Id == preferred)
            ?? list.FirstOrDefault(s => s.Narrator)
            ?? list[0];
    }

    public KokoroSpeaker? FindSpeaker(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("kokoro.", StringComparison.Ordinal)) return null;
        return GetAllSpeakers().FirstOrDefault(s => s.Id == id);
    }

    /// <summary>
    /// Extracts an L2-normalized 256-dim timbre vector directly from <c>voices.bin</c> (<c>[103, 510, 256]</c> f32)
    /// so <c>casting.rs</c> maximizes acoustic separation between characters sharing scenes at zero extra cost.
    /// </summary>
    public float[] ExtractStyleEmbedding(uint sid)
    {
        if (sid >= 103) return [];
        string path = Path.Combine(ModelDir, "voices.bin");
        if (!File.Exists(path)) return [];
        const int dim = 256;
        const int bytesPerVec = dim * sizeof(float);
        int[] buckets = [10, 20, 40, 80];
        float[] acc = new float[dim];
        byte[] buf = new byte[bytesPerVec];
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            foreach (int b in buckets)
            {
                long offset = ((long)sid * 510 + b) * bytesPerVec;
                if (offset + bytesPerVec > fs.Length) return [];
                fs.Seek(offset, SeekOrigin.Begin);
                int read = 0;
                while (read < bytesPerVec)
                {
                    int n = fs.Read(buf, read, bytesPerVec - read);
                    if (n <= 0) return [];
                    read += n;
                }
                for (int i = 0; i < dim; i++)
                {
                    acc[i] += System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(i * 4, 4));
                }
            }
        }
        catch
        {
            return [];
        }

        double sumSq = 0;
        for (int i = 0; i < dim; i++) sumSq += (double)acc[i] * acc[i];
        float norm = (float)Math.Sqrt(sumSq);
        if (norm <= 1e-6f) return [];
        for (int i = 0; i < dim; i++) acc[i] /= norm;
        return acc;
    }

    public void QueueSelection(string voiceId, string languageKey = "zh")
    {
        if (string.IsNullOrWhiteSpace(voiceId)) return;
        lock (_pendingVoices) _pendingVoices[languageKey] = voiceId;
        if (State is KokoroStateKind.Idle or KokoroStateKind.Failed)
            _ = DownloadAsync();
        else
            Notify();
    }

    public string? GetPendingVoiceName(string languageKey = "zh")
    {
        string? vid;
        lock (_pendingVoices) _pendingVoices.TryGetValue(languageKey, out vid);
        if (string.IsNullOrEmpty(vid)) return null;
        return FindSpeaker(vid)?.Name;
    }

    public async Task StartIfUnmeteredAsync()
    {
        if (IsInstalled || State is KokoroStateKind.Downloading or KokoroStateKind.Failed) return;
        if (ModelDownload.IsMeteredNetwork()) return;
        await DownloadAsync();
    }

    public async Task DownloadAsync()
    {
        if (State == KokoroStateKind.Downloading) return;
        RegisterIfInstalled();
        if (IsInstalled)
        {
            ApplyPendingVoices();
            Notify();
            Ready?.Invoke();
            return;
        }

        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;
        long runId = Interlocked.Increment(ref _downloadRunId);

        State = KokoroStateKind.Downloading;
        DownloadProgress = 0.0;
        StatusMessage = "正在连接 Kokoro 语音包下载源…";
        Notify();

        try
        {
            string modelsRoot = Path.Combine(_dataDir, "models");
            Directory.CreateDirectory(modelsRoot);

            // Require ~2x archive space for safe unpack
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(modelsRoot));
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady && drive.AvailableFreeSpace < BundleBytes * 2)
                        throw new IOException($"磁盘可用空间不足（需要约 {Math.Ceiling(BundleBytes * 2 / (1024.0 * 1024.0))} MB 可用空间）。");
                }
            }
            catch (IOException) { throw; }
            catch { }

            bool installed = false;

            // 1. Primary: single compressed archive (ModelScope CN mirror -> GitHub Releases)
            try
            {
                string tmpZip = Path.Combine(modelsRoot, $".{ModelName}.zip.part");
                string unpackDir = Path.Combine(modelsRoot, $".unpack-{ModelName}");
                var urls = ModelDownload.CandidateUrls(ArchiveName, BundleUrl);

                try
                {
                    await ModelDownload.DownloadWithResumeAsync(
                        urls,
                        tmpZip,
                        BundleBytes,
                        BundleDigest,
                        (done, total) => ReportProgress(runId, done, total > 0 ? total : BundleBytes),
                        ct);

                    StatusMessage = "正在解压 Kokoro 轻量离线语音…";
                    Notify();

                    await Task.Run(() =>
                    {
                        if (Directory.Exists(unpackDir)) Directory.Delete(unpackDir, true);
                        YuyueCoreMethods.UnpackModel(tmpZip, unpackDir);
                        string inner = Path.Combine(unpackDir, ModelName);
                        string src = Directory.Exists(inner) ? inner : unpackDir;
                        if (Directory.Exists(ModelDir)) Directory.Delete(ModelDir, true);
                        Directory.Move(src, ModelDir);
                    }, ct);

                    try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
                    installed = IsInstalled;
                }
                finally
                {
                    try { if (Directory.Exists(unpackDir)) Directory.Delete(unpackDir, true); } catch { }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Fall through to direct HuggingFace / hf-mirror multi-file fallback
            }

            // 2. Fallback: stream the 5 raw verified files directly from hf-mirror / huggingface.co
            if (!installed)
            {
                string stageDir = Path.Combine(modelsRoot, ".kokoro-stage");
                Directory.CreateDirectory(stageDir);
                long totalRaw = Math.Max(1L, RawFiles.Sum(f => f.Bytes));
                long completed = 0;

                string mirrorBase = $"https://hf-mirror.com/{HfRepo}/resolve/main/";
                string officialBase = $"https://huggingface.co/{HfRepo}/resolve/main/";
                string[] bases = ModelDownload.PrefersChinaMirror()
                    ? [mirrorBase, officialBase]
                    : [officialBase, mirrorBase];

                foreach (var rf in RawFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    string dst = Path.Combine(stageDir, rf.Name);
                    if (File.Exists(dst) && new FileInfo(dst).Length == rf.Bytes)
                    {
                        bool ok = await Task.Run(() =>
                        {
                            try { return string.Equals(YuyueCoreMethods.FileDigest(dst), rf.Blake3, StringComparison.OrdinalIgnoreCase); }
                            catch { return false; }
                        }, ct);
                        if (ok)
                        {
                            completed += rf.Bytes;
                            ReportProgress(runId, completed, totalRaw);
                            continue;
                        }
                    }

                    string part = Path.Combine(stageDir, $".{rf.Name}.part");
                    var urls = bases.Select(b => b + rf.Name).ToArray();
                    long baseDone = completed;
                    await ModelDownload.DownloadWithResumeAsync(
                        urls,
                        part,
                        rf.Bytes,
                        rf.Blake3,
                        (done, _) => ReportProgress(runId, baseDone + Math.Min(done, rf.Bytes), totalRaw),
                        ct);

                    if (File.Exists(dst)) File.Delete(dst);
                    File.Move(part, dst);
                    completed += rf.Bytes;
                    ReportProgress(runId, completed, totalRaw);
                }

                await Task.Run(() =>
                {
                    if (Directory.Exists(ModelDir)) Directory.Delete(ModelDir, true);
                    Directory.Move(stageDir, ModelDir);
                }, ct);
            }

            RegisterIfInstalled();
            if (!IsInstalled) throw new InvalidOperationException("解压后未找到完整的 Kokoro 模型文件");
            ApplyPendingVoices();
            Notify();
            Ready?.Invoke();
        }
        catch (OperationCanceledException)
        {
            State = KokoroStateKind.Idle;
            DownloadProgress = 0;
            StatusMessage = "已取消下载";
            Notify();
        }
        catch (Exception ex)
        {
            State = KokoroStateKind.Failed;
            StatusMessage = $"下载失败：{ex.Message}";
            Notify();
        }
    }

    public void CancelDownload() => _downloadCts?.Cancel();

    public void DeleteInstalledModel()
    {
        CancelDownload();
        KokoroEngine.Instance.CloseSession();
        try { if (Directory.Exists(ModelDir)) Directory.Delete(ModelDir, true); } catch { }
        try
        {
            string cacheDir = Path.Combine(_dataDir, "cache", "kokoro");
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true);
        }
        catch { }
        State = KokoroStateKind.Idle;
        DownloadProgress = 0;
        ReadyOfflineVoice = null;
        StatusMessage = "已移除 Kokoro 离线语音模型";
        Notify();
    }

    public void AcceptReadyOfflineVoice()
    {
        ReadyOfflineVoice = null;
        FlushPendingVoicesImmediate();
        Notify();
    }

    public void DismissReadyOfflineVoice()
    {
        ReadyOfflineVoice = null;
        lock (_pendingVoices) _pendingVoices.Clear();
        Notify();
    }

    internal void FlushDeferredVoiceIfIdle()
    {
        if (AudioPlayer.IsAnyActivelyPlaying || !IsInstalled) return;
        bool hadPending;
        lock (_pendingVoices) hadPending = _pendingVoices.Count > 0;
        if (hadPending)
        {
            ReadyOfflineVoice = null;
            FlushPendingVoicesImmediate();
        }
    }

    private void ApplyPendingVoices()
    {
        if (AudioPlayer.IsAnyActivelyPlaying)
        {
            string? candidate = null;
            lock (_pendingVoices)
            {
                candidate = _pendingVoices.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            }
            ReadyOfflineVoice = candidate ?? DefaultNarrator("zh")?.Id ?? "kokoro.zf_001";
            return;
        }
        ReadyOfflineVoice = null;
        FlushPendingVoicesImmediate();
    }

    private void FlushPendingVoicesImmediate()
    {
        KeyValuePair<string, string>[] items;
        lock (_pendingVoices)
        {
            items = _pendingVoices.ToArray();
            _pendingVoices.Clear();
        }
        foreach (var kv in items)
        {
            try { _applyPendingVoice?.Invoke(kv.Key, kv.Value); } catch { }
        }
    }

    private long _downloadRunId;

    private void ReportProgress(long runId, long downloaded, long total)
    {
        if (total <= 0 || Interlocked.Read(ref _downloadRunId) != runId || State != KokoroStateKind.Downloading) return;
        double p = Math.Clamp((double)downloaded / total, 0.0, 0.99);
        if (Math.Abs(p - DownloadProgress) >= 0.01)
        {
            DownloadProgress = p;
            StatusMessage = $"正在下载 Kokoro 轻量离线语音… {(int)Math.Round(p * 100)}%";
            Notify();
        }
    }

    private void Notify() => StateChanged?.Invoke();
}

/// <summary>
/// Serialized ONNX Runtime session and sentence-level WAV cache for <c>Kokoro-82M-v1.1-zh Int8</c> (24 kHz mono).
/// </summary>
public sealed class KokoroEngine : IDisposable
{
    public const int SampleRate = 24_000;
    private const int StyleDim = 256;
    private const int StyleMaxLen = 510;
    private const int ClauseGapSamples = 2_400; // 0.10 s at 24 kHz
    private const long MaxCacheBytes = 180L * 1024L * 1024L;

    public static KokoroEngine Instance { get; } = new();

    private sealed class Loaded : IDisposable
    {
        public string DirPath { get; }
        public InferenceSession Session { get; }
        public MemoryMappedFile VoicesMap { get; }
        public MemoryMappedViewAccessor VoicesView { get; }
        public int VoiceCount { get; }

        public Loaded(string dirPath, InferenceSession session, MemoryMappedFile map, MemoryMappedViewAccessor view, int voiceCount)
        {
            DirPath = dirPath;
            Session = session;
            VoicesMap = map;
            VoicesView = view;
            VoiceCount = voiceCount;
        }

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { VoicesView.Dispose(); } catch { }
            try { VoicesMap.Dispose(); } catch { }
        }
    }

    private readonly SemaphoreSlim _inferGate = new(1, 1);
    private Loaded? _loaded;
    private int _writesSinceTrim;

    private KokoroEngine() { }

    public void CloseSession()
    {
        _inferGate.Wait();
        try
        {
            _loaded?.Dispose();
            _loaded = null;
        }
        finally { _inferGate.Release(); }
    }

    public async Task<byte[]?> SynthesizeWavAsync(
        YuyueCore core,
        KokoroVoiceModel model,
        string dataDir,
        KokoroSpeaker speaker,
        string text,
        string language,
        CancellationToken ct = default)
    {
        string plain = CardVoiceEngine.PlainSpokenText(text);
        if (!plain.Any(char.IsLetterOrDigit)) return null;
        if (!model.IsInstalled || !CardVoiceEngine.SpeaksLanguage(language)) return null;

        string lang = CardVoiceEngine.InferLanguage(plain, string.IsNullOrWhiteSpace(language) ? speaker.Lang : language);
        string cachePath = GetCacheFilePath(dataDir, speaker.Id, lang, plain);
        if (File.Exists(cachePath))
        {
            try
            {
                byte[] cached = await File.ReadAllBytesAsync(cachePath, ct);
                if (cached.Length > 44)
                {
                    try { File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow); } catch { }
                    return cached;
                }
            }
            catch { }
        }

        await _inferGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var h = EnsureLoaded(core, model);
                if (h is null) return null;

                int sid = (int)speaker.Sid;
                if (sid < 0 || sid >= h.VoiceCount) return null;

                string[] clauses = core.KokoroClauses(plain, lang);
                if (clauses.Length == 0) return null;

                var chunks = new List<float[]>(clauses.Length);
                int total = 0;

                foreach (string clause in clauses)
                {
                    ct.ThrowIfCancellationRequested();
                    long[] inner = core.KokoroTokens(clause, lang);
                    if (inner.Length == 0) continue;

                    float[]? clean = RunClauseClean(core, h, inner, sid, 1.0f);
                    if (clean is null || clean.Length == 0)
                    {
                        long[] nudged = inner.Length < StyleMaxLen - 1 ? [.. inner, inner[^1]] : inner;
                        clean = RunClauseClean(core, h, nudged, sid, 1.015f);
                    }
                    if (clean is null || clean.Length == 0) continue;

                    if (chunks.Count > 0) total += ClauseGapSamples;
                    chunks.Add(clean);
                    total += clean.Length;
                }

                if (chunks.Count == 0 || total == 0) return null;

                float[] merged;
                if (chunks.Count == 1)
                {
                    merged = chunks[0];
                }
                else
                {
                    merged = new float[total];
                    int pos = 0;
                    for (int i = 0; i < chunks.Count; i++)
                    {
                        if (i > 0) pos += ClauseGapSamples;
                        Array.Copy(chunks[i], 0, merged, pos, chunks[i].Length);
                        pos += chunks[i].Length;
                    }
                }

                byte[] wav = EncodeWav24kMono16(merged);
                TryWriteCache(cachePath, wav);
                return wav;
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally { _inferGate.Release(); }
    }

    private Loaded? EnsureLoaded(YuyueCore core, KokoroVoiceModel model)
    {
        if (!model.IsInstalled) return null;
        model.RegisterIfInstalled();
        string dir = model.ModelDir;
        if (_loaded is not null && string.Equals(_loaded.DirPath, dir, StringComparison.OrdinalIgnoreCase))
            return _loaded;

        _loaded?.Dispose();
        _loaded = null;

        try
        {
            string onnxPath = Path.Combine(dir, "model.int8.onnx");
            string voicesPath = Path.Combine(dir, "voices.bin");
            long voicesLen = new FileInfo(voicesPath).Length;
            int strideFloats = StyleMaxLen * StyleDim;
            int voiceCount = (int)(voicesLen / ((long)strideFloats * sizeof(float)));
            if (voiceCount <= 0) return null;

            using var opts = new SessionOptions
            {
                EnableCpuMemArena = false,
                EnableMemoryPattern = false,
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount - 1, 2, 4),
            };
            var session = new InferenceSession(onnxPath, opts);
            var map = MemoryMappedFile.CreateFromFile(voicesPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            var view = map.CreateViewAccessor(0, voicesLen, MemoryMappedFileAccess.Read);
            _loaded = new Loaded(dir, session, map, view, voiceCount);
            return _loaded;
        }
        catch
        {
            return null;
        }
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
        bw.Write((short)1);
        bw.Write((short)1);
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

    private static float[]? RunClauseClean(YuyueCore core, Loaded h, long[] inner, int sid, float speed)
    {
        int cappedLen = Math.Min(inner.Length, StyleMaxLen);
        long[] padded = new long[cappedLen + 2];
        Array.Copy(inner, 0, padded, 1, cappedLen);

        int lenIdx = Math.Min(cappedLen, StyleMaxLen - 1);
        long byteOffset = (long)(sid * StyleMaxLen + lenIdx) * StyleDim * sizeof(float);
        float[] styleVec = new float[StyleDim];
        h.VoicesView.ReadArray(byteOffset, styleVec, 0, StyleDim);

        var tokTensor = new DenseTensor<long>(padded, [1, padded.Length]);
        var styleTensor = new DenseTensor<float>(styleVec, [1, StyleDim]);
        var speedTensor = new DenseTensor<float>(new[] { speed }, [1]);

        using var results = h.Session.Run(
        [
            NamedOnnxValue.CreateFromTensor("tokens", tokTensor),
            NamedOnnxValue.CreateFromTensor("style", styleTensor),
            NamedOnnxValue.CreateFromTensor("speed", speedTensor),
        ]);

        var rawAudio = results.First().AsEnumerable<float>().ToArray();
        if (rawAudio.Length == 0) return null;
        float[] clean = core.KokoroPostprocess(rawAudio, (uint)SampleRate);
        return clean.Length == 0 ? null : clean;
    }

    private static string GetCacheFilePath(string dataDir, string speakerId, string lang, string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"kokoro-v1_1-v2|{speakerId}|{lang}|{text}"));
        string hex = Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        return Path.Combine(dataDir, "cache", "kokoro", $"{hex}.wav");
    }

    private void TryWriteCache(string path, byte[] wav)
    {
        try
        {
            string? folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(path, wav);

            if (++_writesSinceTrim >= 24)
            {
                _writesSinceTrim = 0;
                var files = new DirectoryInfo(folder).GetFiles();
                long total = files.Sum(f => f.Length);
                if (total <= MaxCacheBytes) return;
                foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    long len = f.Length;
                    try { f.Delete(); total -= len; } catch { }
                    if (total <= MaxCacheBytes * 9 / 10) break;
                }
            }
        }
        catch { }
    }

    public void Dispose() => CloseSession();
}
