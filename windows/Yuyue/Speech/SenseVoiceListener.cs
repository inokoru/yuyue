using System.Buffers.Binary;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Yuyue.Core;

namespace Yuyue.Speech;

/// <summary>
/// 1:1 Windows port of <c>android/.../speech/Listener.kt</c>:
/// On-device <c>SenseVoice-Small</c> Int8 ONNX Runtime speech recognition
/// (Chinese, English, Japanese, Korean, Cantonese; ~228 MB, BLAKE3-verified),
/// using <see cref="SenseVoice"/> in <c>yuyue-core</c> for LFR filterbank front-end and CTC timestamped word decoding.
/// </summary>
public static class SenseVoiceListener
{
    public const string ModelFile = "sense_voice.int8.onnx";
    public const string TokensFile = "sense_voice_tokens.txt";
    public const string BaseUrl = "https://github.com/inokoru/yuyue/releases/download/models-1/";
    public const string ModelDigest = "3171a61e94132c6270584b5a0f2f100289d5958c28795695a4d43ef23660ab8e";
    public const string TokensDigest = "dc65b6ac2b8bab079278769054812b6972114f2f515983491dfd7750e20987e1";
    public const long ExpectedModelBytes = 239_233_841L;
    private const double ChunkSeconds = 28.0;

    private sealed class LoadedModel : IDisposable
    {
        public string ModelPath { get; }
        public InferenceSession Session { get; }
        public SenseVoice Core { get; }

        public LoadedModel(string modelPath, InferenceSession session, SenseVoice core)
        {
            ModelPath = modelPath;
            Session = session;
            Core = core;
        }

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { Core.Dispose(); } catch { }
        }
    }

    private static readonly object Gate = new();
    private static LoadedModel? _held;

    public static string ModelsDir(string dataDir) => Path.Combine(dataDir, "models");
    public static string ModelPath(string dataDir) => Path.Combine(ModelsDir(dataDir), ModelFile);
    public static string TokensPath(string dataDir) => Path.Combine(ModelsDir(dataDir), TokensFile);

    public static bool IsInstalled(string dataDir) =>
        File.Exists(ModelPath(dataDir)) && File.Exists(TokensPath(dataDir));

    public static void Unload()
    {
        lock (Gate)
        {
            _held?.Dispose();
            _held = null;
        }
    }

    public static void DeleteModel(string dataDir)
    {
        Unload();
        foreach (string name in new[] { ModelFile, TokensFile, $".{ModelFile}.part", $".{TokensFile}.part" })
        {
            string p = Path.Combine(ModelsDir(dataDir), name);
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }

    public static async Task EnsureAsync(string dataDir, Action<double>? onProgress, CancellationToken ct)
    {
        if (IsInstalled(dataDir))
        {
            onProgress?.Invoke(1.0);
            return;
        }

        string dir = ModelsDir(dataDir);
        Directory.CreateDirectory(dir);
        string modelPart = Path.Combine(dir, $".{ModelFile}.part");
        long existing = File.Exists(modelPart) ? new FileInfo(modelPart).Length : 0L;

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace + existing < ExpectedModelBytes * 2)
                    throw new IOException("磁盘可用空间不足（SenseVoice 离线转写模型下载需要约 480 MB 可用空间）。");
            }
        }
        catch (IOException) { throw; }
        catch { }

        foreach (var (name, bytes, digest) in new[]
        {
            (TokensFile, 0L, TokensDigest),
            (ModelFile, ExpectedModelBytes, ModelDigest),
        })
        {
            string target = Path.Combine(dir, name);
            if (File.Exists(target)) continue;
            string part = Path.Combine(dir, $".{name}.part");
            var urls = ModelDownload.CandidateUrls(name, BaseUrl + name);
            await ModelDownload.DownloadWithResumeAsync(
                urls,
                part,
                bytes,
                digest,
                (done, total) =>
                {
                    if (name == ModelFile)
                    {
                        double denom = Math.Max(Math.Max(total, ExpectedModelBytes), 1L);
                        onProgress?.Invoke(Math.Clamp(done / denom, 0.0, 1.0));
                    }
                },
                ct);
            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
        }

        onProgress?.Invoke(1.0);
    }

    private static LoadedModel? Load(string dataDir)
    {
        if (!IsInstalled(dataDir)) return null;
        string mp = ModelPath(dataDir);
        lock (Gate)
        {
            if (_held is not null && string.Equals(_held.ModelPath, mp, StringComparison.OrdinalIgnoreCase))
                return _held;

            _held?.Dispose();
            _held = null;
            try
            {
                var opts = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4) };
                var session = new InferenceSession(mp, opts);
                var meta = new Dictionary<string, string>(session.ModelMetadata.CustomMetadataMap, StringComparer.Ordinal);
                string tokens = File.ReadAllText(TokensPath(dataDir));
                var core = new SenseVoice(meta, tokens);
                _held = new LoadedModel(mp, session, core);
                return _held;
            }
            catch
            {
                return null;
            }
        }
    }

    public static HeardWord[]? TranscribeSamples(
        string dataDir,
        float[] samples,
        int sampleRate,
        string language,
        bool punctuated,
        double offsetSec = 0.0)
    {
        var loaded = Load(dataDir);
        if (loaded is null) return null;
        if (samples.Length == 0) return [];

        lock (Gate)
        {
            if (!ReferenceEquals(_held, loaded)) return null;
            var input = loaded.Core.Input(samples, (uint)sampleRate);
            if (input.Frames == 0) return [];

            int t = (int)input.Frames;
            int d = (int)input.Dim;
            var xTensor = new DenseTensor<float>(input.Data, [1, t, d]);
            var lenTensor = new DenseTensor<int>(new[] { t }, [1]);
            var langTensor = new DenseTensor<int>(new[] { loaded.Core.LanguageId(language) }, [1]);
            var normTensor = new DenseTensor<int>(new[] { loaded.Core.TextNormId(punctuated) }, [1]);

            var inputs = new List<NamedOnnxValue>(4)
            {
                NamedOnnxValue.CreateFromTensor("x", xTensor),
                NamedOnnxValue.CreateFromTensor("x_length", lenTensor),
                NamedOnnxValue.CreateFromTensor("language", langTensor),
                NamedOnnxValue.CreateFromTensor("text_norm", normTensor),
            };

            using var results = loaded.Session.Run(inputs);
            var logits = results[0].AsTensor<float>();
            int frames = logits.Dimensions[1];
            int vocab = logits.Dimensions[2];
            int[] best = new int[frames];

            if (logits is DenseTensor<float> dense)
            {
                ReadOnlySpan<float> buf = dense.Buffer.Span;
                for (int f = 0; f < frames; f++)
                {
                    int bi = 0;
                    float bv = float.NegativeInfinity;
                    int baseIdx = f * vocab;
                    for (int k = 0; k < vocab; k++)
                    {
                        float v = buf[baseIdx + k];
                        if (v > bv) { bv = v; bi = k; }
                    }
                    best[f] = bi;
                }
            }
            else
            {
                for (int f = 0; f < frames; f++)
                {
                    int bi = 0;
                    float bv = float.NegativeInfinity;
                    for (int k = 0; k < vocab; k++)
                    {
                        float v = logits[0, f, k];
                        if (v > bv) { bv = v; bi = k; }
                    }
                    best[f] = bi;
                }
            }

            return loaded.Core.Words(best, offsetSec);
        }
    }

    /// <summary>
    /// Transcribes a 16 kHz mono 16-bit PCM WAV file in ~28-second quiet-boundary stretches,
    /// returning punctuated <see cref="HeardWord"/>[] shifted by <paramref name="globalOffsetSec"/>.
    /// </summary>
    internal static async Task<HeardWord[]?> TranscribePcm16kWavAsync(
        string dataDir,
        string wavPath,
        Aligner.WavInfo info,
        string language,
        double globalOffsetSec,
        Action<double>? onLocalSecondsDone,
        CancellationToken ct)
    {
        if (Load(dataDir) is null) return null;
        return await Task.Run(() =>
        {
            int rate = info.SampleRate;
            long totalSamples = info.SampleCount;
            if (totalSamples <= 0 || rate <= 0) return Array.Empty<HeardWord>();

            int maxChunkSamples = (int)(ChunkSeconds * rate);
            var outWords = new List<HeardWord>();
            using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            long pos = 0;
            byte[] rawBuf = new byte[maxChunkSamples * 2];
            while (pos < totalSamples)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(maxChunkSamples, totalSamples - pos);
                fs.Seek(info.DataOffset + pos * 2L, SeekOrigin.Begin);
                int bytesRead = ReadExact(fs, rawBuf, want * 2);
                int gotSamples = bytesRead / 2;
                if (gotSamples <= 0) break;

                float[] chunk = new float[gotSamples];
                for (int i = 0; i < gotSamples; i++)
                {
                    short s = BinaryPrimitives.ReadInt16LittleEndian(rawBuf.AsSpan(i * 2, 2));
                    chunk[i] = s / 32768f;
                }

                int cut = gotSamples < maxChunkSamples
                    ? gotSamples
                    : FindQuietestOffset(chunk, gotSamples * 2 / 3, rate);

                float[] slice = cut == gotSamples ? chunk : chunk[..cut];
                var words = TranscribeSamples(
                    dataDir,
                    slice,
                    rate,
                    language,
                    punctuated: true,
                    offsetSec: globalOffsetSec + pos / (double)rate);
                if (words is null) return null;

                outWords.AddRange(words);
                pos += Math.Max(1, cut);
                onLocalSecondsDone?.Invoke(pos / (double)rate);
            }
            return outWords.ToArray();
        }, ct);
    }

    internal static int FindQuietestOffset(float[] x, int from, int sampleRate)
    {
        int step = Math.Max(1, sampleRate / 10); // 0.1 s window
        int best = x.Length;
        double bestE = double.MaxValue;
        for (int i = Math.Max(0, from); i + step <= x.Length; i += step)
        {
            double e = 0.0;
            for (int k = i; k < i + step; k++)
                e += (double)x[k] * x[k];
            if (e < bestE)
            {
                bestE = e;
                best = i + step / 2;
            }
        }
        return Math.Max(step, best);
    }

    private static int ReadExact(Stream s, byte[] buf, int count)
    {
        int done = 0;
        while (done < count)
        {
            int r = s.Read(buf, done, count - done);
            if (r <= 0) break;
            done += r;
        }
        return done;
    }
}
