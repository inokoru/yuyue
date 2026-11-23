using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Yuyue.Core;

namespace Yuyue.Speech;

/// <summary>
/// 1:1 port of <c>apple/Yuyue/Speech/SpeakerPrint.swift</c>:
/// CAM++ speaker embedding extractor (<c>campplus-1</c>) via ONNX Runtime when the model is installed beside
/// the library (<c>models/campplus.onnx</c> or <c>models/SpeakerPrint.onnx</c>), plus direct seeding of
/// pre-computed voice-card embeddings into Rust core (<c>Core.SpeakerModel</c>, <c>Core.SpeakerSetPrint</c>,
/// <c>Core.SpeakerUnprinted</c>, <c>Core.SpeakerSimilarity</c>) so scene casting and clip audition keep voices distinct.
/// Gracefully degrades to <c>null</c> whenever the optional ONNX model is absent.
/// </summary>
public static class SpeakerPrintService
{
    public const string ModelName = "campplus-1";

    private static readonly object SessionGate = new();
    private static string? _loadedPath;
    private static InferenceSession? _session;

    public static string? FindModelPath(string dataDir)
    {
        string modelsDir = Path.Combine(dataDir, "models");
        string[] candidates =
        [
            Path.Combine(modelsDir, "campplus.onnx"),
            Path.Combine(modelsDir, "SpeakerPrint.onnx"),
            Path.Combine(modelsDir, "campplus-1", "campplus.onnx"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsModelInstalled(string dataDir) => FindModelPath(dataDir) is not null;

    /// <summary>
    /// Computes a CAM++ speaker embedding vector for mono float32 <paramref name="samples"/> at <paramref name="sampleRate"/>.
    /// Returns <c>null</c> if the ONNX model is not installed or audio is shorter than 0.5 s (&lt; 50 frames).
    /// </summary>
    public static float[]? ComputeEmbedding(YuyueCore core, string dataDir, float[] samples, uint sampleRate)
    {
        var session = EnsureSession(dataDir);
        if (session is null || samples.Length < sampleRate / 2) return null;

        try
        {
            var feat = core.SpeakerFeatures(samples, sampleRate);
            if (feat.Frames < 50 || feat.Data.Length < (int)feat.Frames * 80) return null;

            string inputName = session.InputMetadata.Keys.FirstOrDefault() ?? "fbank";
            var tensor = new DenseTensor<float>(feat.Data, new[] { 1, (int)feat.Frames, 80 });
            using var outputs = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
            var emb = outputs[0].AsTensor<float>();
            if (emb is DenseTensor<float> dense)
                return dense.Buffer.ToArray();
            return emb.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Registers <c>campplus-1</c> with Rust core, immediately populates all voice-card embeddings already known on
    /// <paramref name="voices"/>, and (if CAM++ ONNX is installed) computes prints for any remaining non-cloud voices.
    /// </summary>
    public static async Task PrintVoicesAsync(
        LibraryService library,
        VoiceInfo[] voices,
        string language,
        CancellationToken ct = default)
    {
        if (voices.Length == 0) return;

        try
        {
            library.Core.SpeakerModel(ModelName);
            foreach (var v in voices)
            {
                if (v.Embedding is { Length: > 0 })
                    library.Core.SpeakerSetPrint(v.Id, v.Embedding);
            }
        }
        catch { return; }

        if (!IsModelInstalled(library.DataDir)) return;

        string[] unprinted;
        string sampleText;
        try
        {
            string[] localIds = voices.Select(v => v.Id).Where(id => !id.StartsWith("cloud.", StringComparison.Ordinal)).ToArray();
            unprinted = library.Core.SpeakerUnprinted(localIds);
            sampleText = library.Core.SpeakerSampleText(language);
        }
        catch { return; }

        foreach (string id in unprinted)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                byte[]? wav = null;
                if (id.StartsWith("sys:", StringComparison.Ordinal))
                {
                    wav = await AudioPlayer.SynthesizeSystemVoiceToWavAsync(sampleText, id[4..], language, ct);
                }
                else if (library.LiveVoice.IsModelInstalled)
                {
                    var card = library.Core.CardFind(null, id);
                    if (card is not null)
                        wav = await CardVoiceEngine.Instance.SynthesizeWavAsync(
                            library.Core, library.LiveVoice, library.DataDir, card, null, sampleText, language, CardStyle.Neutral, ct);
                }

                if (wav is not { Length: > 44 }) continue;
                var (pcm, rate) = DecodeWavToMonoFloat(wav);
                if (pcm.Length == 0) continue;

                float[]? emb = ComputeEmbedding(library.Core, library.DataDir, pcm, rate);
                if (emb is { Length: > 0 })
                    library.Core.SpeakerSetPrint(id, emb);
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    /// <summary>
    /// Cosine similarity between a rendered clip WAV and a registered voice ID (1:1 <c>SpeakerPrint</c> audition check).
    /// Returns <c>null</c> when unavailable.
    /// </summary>
    public static float? CheckClipSimilarity(YuyueCore core, string dataDir, string voiceId, byte[] wavBytes)
    {
        if (!IsModelInstalled(dataDir) || wavBytes.Length <= 44) return null;
        var (pcm, rate) = DecodeWavToMonoFloat(wavBytes);
        if (pcm.Length == 0) return null;
        float[]? emb = ComputeEmbedding(core, dataDir, pcm, rate);
        if (emb is null) return null;
        try { return core.SpeakerSimilarity(voiceId, emb); }
        catch { return null; }
    }

    internal static (float[] Samples, uint SampleRate) DecodeWavToMonoFloat(byte[] wav)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
            return ([], 0);
        int channels = Math.Max(1, (int)BitConverter.ToInt16(wav, 22));
        uint rate = (uint)Math.Max(8000, BitConverter.ToInt32(wav, 24));
        int bits = BitConverter.ToInt16(wav, 34);
        if (bits != 16) return ([], 0);

        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            pos += 8;
            if (id == "data")
            {
                int avail = Math.Min(size, wav.Length - pos);
                int frames = avail / (channels * 2);
                if (frames <= 0) return ([], rate);
                var mono = new float[frames];
                for (int f = 0; f < frames; f++)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++)
                        sum += BitConverter.ToInt16(wav, pos + (f * channels + c) * 2) / 32768f;
                    mono[f] = sum / channels;
                }
                return (mono, rate);
            }
            pos += size + (size & 1);
        }
        return ([], rate);
    }

    private static InferenceSession? EnsureSession(string dataDir)
    {
        string? path = FindModelPath(dataDir);
        if (path is null) return null;
        lock (SessionGate)
        {
            if (_session is not null && string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
                return _session;
            try
            {
                _session?.Dispose();
                using var opts = new SessionOptions();
                opts.IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount));
                opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                _session = new InferenceSession(path, opts);
                _loadedPath = path;
                return _session;
            }
            catch
            {
                _session = null;
                _loadedPath = null;
                return null;
            }
        }
    }
}
