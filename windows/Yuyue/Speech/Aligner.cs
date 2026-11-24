using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Yuyue.Speech;

/// <summary>
/// 1:1 Windows implementation of <c>android/.../speech/Aligner.kt</c> and <c>apple/Yuyue/App/Aligner.swift</c>:
/// Meta MMS 8-bit ONNX forced alignment model (<c>mms_fa.int8.onnx</c>, 317 MB, BLAKE3-verified).
/// Decodes any audiobook file to 16 kHz mono PCM via Windows Media Foundation (<see cref="MediaTranscoder"/>),
/// measures 30 ms dB levels for VAD, and runs sliding-window ONNX Runtime inference to emit 28-letter log-probabilities
/// every ~20 ms for Rust core's <c>AudiobookHeardLettersSaved</c> &amp; <c>AudiobookAlignWindowSaved</c>.
/// </summary>
public static class Aligner
{
    public const string FileName = "mms_fa.int8.onnx";
    public const string OfficialUrl = "https://github.com/inokoru/yuyue/releases/download/models-1/mms_fa.int8.onnx";
    public const string Digest = "fad66a4545c60898c382c001aefd26dafb550e3baf58b915480b70dd89d48861";
    public const long ExpectedBytes = 317_327_887L;

    public const int Letters = 28;
    public const int SampleRate = 16_000;
    public const int HopSamples = 30 * SampleRate;
    public const int ContextSamples = 2 * SampleRate;
    public const int VadFrameSamples = 480; // 30 ms @ 16 kHz

    private static readonly object SessionGate = new();
    private static InferenceSession? _sharedSession;
    private static string? _sharedSessionModelPath;
    private static int _activeSessionUsers;
    private static bool _unloadPending;

    public static void Unload()
    {
        InferenceSession? toDispose = null;
        lock (SessionGate)
        {
            if (_activeSessionUsers > 0)
            {
                _unloadPending = true;
            }
            else
            {
                toDispose = _sharedSession;
                _sharedSession = null;
                _sharedSessionModelPath = null;
                _unloadPending = false;
            }
        }
        try { toDispose?.Dispose(); } catch { }
    }

    private static InferenceSession AcquireSession(string modelPath)
    {
        InferenceSession? stale = null;
        lock (SessionGate)
        {
            _unloadPending = false;
            if (_sharedSession is not null && string.Equals(_sharedSessionModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                _activeSessionUsers++;
                return _sharedSession;
            }
            stale = _sharedSession;
            _sharedSession = null;
            _sharedSessionModelPath = null;
        }
        try { stale?.Dispose(); } catch { }

        using var opts = new SessionOptions();
        opts.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1);
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        var created = new InferenceSession(modelPath, opts);

        InferenceSession? raceWinner = null;
        lock (SessionGate)
        {
            if (_sharedSession is not null && string.Equals(_sharedSessionModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                _activeSessionUsers++;
                raceWinner = _sharedSession;
            }
            else
            {
                _sharedSession = created;
                _sharedSessionModelPath = modelPath;
                _activeSessionUsers++;
            }
        }
        if (raceWinner is not null)
        {
            try { created.Dispose(); } catch { }
            return raceWinner;
        }
        return created;
    }

    private static void ReleaseSession(InferenceSession session)
    {
        InferenceSession? toDispose = null;
        lock (SessionGate)
        {
            if (ReferenceEquals(_sharedSession, session))
            {
                _activeSessionUsers = Math.Max(0, _activeSessionUsers - 1);
                if (_activeSessionUsers == 0 && _unloadPending)
                {
                    toDispose = _sharedSession;
                    _sharedSession = null;
                    _sharedSessionModelPath = null;
                    _unloadPending = false;
                }
            }
        }
        try { toDispose?.Dispose(); } catch { }
    }

    public static string ModelsDir(string dataDir) => Path.Combine(dataDir, "models");
    public static string ModelPath(string dataDir) => Path.Combine(ModelsDir(dataDir), FileName);
    public static bool IsInstalled(string dataDir) => File.Exists(ModelPath(dataDir));

    /// <summary>
    /// Downloads and verifies <c>mms_fa.int8.onnx</c> with ModelScope mirror + GitHub fallback and HTTP Range resume.
    /// </summary>
    public static async Task EnsureAsync(string dataDir, Action<double>? onProgress, CancellationToken ct)
    {
        if (IsInstalled(dataDir))
        {
            onProgress?.Invoke(1.0);
            return;
        }

        string dir = ModelsDir(dataDir);
        Directory.CreateDirectory(dir);
        string partFile = Path.Combine(dir, $".{FileName}.part");
        long existing = File.Exists(partFile) ? new FileInfo(partFile).Length : 0L;

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace + existing < ExpectedBytes * 2)
                    throw new IOException("磁盘可用空间不足（强制对齐模型下载与校验需要约 650 MB 可用空间）。");
            }
        }
        catch (IOException) { throw; }
        catch { }

        var urls = ModelDownload.CandidateUrls(FileName, OfficialUrl);
        await ModelDownload.DownloadWithResumeAsync(
            urls,
            partFile,
            ExpectedBytes,
            Digest,
            (done, total) =>
            {
                double denom = Math.Max(Math.Max(total, ExpectedBytes), 1L);
                onProgress?.Invoke(Math.Clamp(done / denom, 0.0, 1.0));
            },
            ct);

        string dest = ModelPath(dataDir);
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(partFile, dest);
        onProgress?.Invoke(1.0);
    }

    public static void DeleteModel(string dataDir)
    {
        Unload();
        string path = ModelPath(dataDir);
        if (File.Exists(path)) File.Delete(path);
        string part = Path.Combine(ModelsDir(dataDir), $".{FileName}.part");
        if (File.Exists(part)) File.Delete(part);
    }

    /// <summary>
    /// Decodes an audiobook file (<c>.m4b</c>, <c>.mp3</c>, <c>.m4a</c>, <c>.flac</c>, <c>.wav</c>, etc.)
    /// into a 16 kHz mono 16-bit PCM WAV file for constant-memory sliding-window VAD and ONNX inference.
    /// </summary>
    public static async Task<string> EnsurePcm16kWavAsync(string audioPath, string scratchDir, int fileIndex, CancellationToken ct)
    {
        if (!File.Exists(audioPath))
            throw new FileNotFoundException("找不到有声书音频文件", audioPath);

        // Fast path: already a standard 16 kHz mono 16-bit PCM WAV
        if (audioPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && TryReadWavInfo(audioPath, out var existingInfo)
            && existingInfo.AudioFormat == 1 && existingInfo.SampleRate == SampleRate && existingInfo.Channels == 1 && existingInfo.BitsPerSample == 16)
        {
            return audioPath;
        }

        Directory.CreateDirectory(scratchDir);
        string wavPath = Path.Combine(scratchDir, $"pcm16k-{fileIndex}.wav");
        if (File.Exists(wavPath) && new FileInfo(wavPath).Length > 64 && TryReadWavInfo(wavPath, out var cachedInfo)
            && cachedInfo.DataBytes > 0)
        {
            return wavPath;
        }

        string partWav = wavPath + ".part";
        if (File.Exists(partWav)) File.Delete(partWav);

        var srcFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(audioPath));
        var dstFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(scratchDir));
        var dstFile = await dstFolder.CreateFileAsync(Path.GetFileName(partWav), CreationCollisionOption.ReplaceExisting);

        var profile = new MediaEncodingProfile
        {
            Audio = AudioEncodingProperties.CreatePcm(SampleRate, 1, 16),
            Container = new ContainerEncodingProperties { Subtype = MediaEncodingSubtypes.Wave },
        };

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prep = await transcoder.PrepareFileTranscodeAsync(srcFile, dstFile, profile);
        if (!prep.CanTranscode)
        {
            try { File.Delete(partWav); } catch { }
            throw new NotSupportedException(
                prep.FailureReason == TranscodeFailureReason.CodecNotFound
                    ? $"系统缺少该音频格式的解码器（{Path.GetExtension(audioPath)}）。可在 Microsoft Store 安装免费“Web 媒体扩展”或转换为 M4B/MP3。"
                    : $"无法解码音频文件（{prep.FailureReason}）。");
        }

        var op = prep.TranscodeAsync();
        using var reg = ct.Register(() => { try { op.Cancel(); } catch { } });
        await op;
        ct.ThrowIfCancellationRequested();

        if (File.Exists(wavPath)) File.Delete(wavPath);
        File.Move(partWav, wavPath);
        return wavPath;
    }

    /// <summary>
    /// Computes each 30 ms frame's loudness in dB over a 16 kHz mono 16-bit PCM WAV (1:1 <c>Aligner.levels</c>).
    /// </summary>
    public static float[] ComputeLevels(string pcmWavPath, CancellationToken ct)
    {
        if (!TryReadWavInfo(pcmWavPath, out var info) || info.SampleCount == 0)
            return [];

        int totalFrames = (int)(info.SampleCount / VadFrameSamples);
        if (totalFrames <= 0) return [];

        var levels = new float[totalFrames];
        const int chunkFrames = 2000;
        byte[] byteBuf = new byte[chunkFrames * VadFrameSamples * 2];

        using var fs = new FileStream(pcmWavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(info.DataOffset, SeekOrigin.Begin);

        int outIdx = 0;
        long remainingBytes = info.DataBytes;
        while (outIdx < totalFrames && remainingBytes >= VadFrameSamples * 2)
        {
            ct.ThrowIfCancellationRequested();
            int wantFrames = Math.Min(chunkFrames, totalFrames - outIdx);
            int wantBytes = (int)Math.Min(wantFrames * VadFrameSamples * 2L, remainingBytes);
            wantBytes -= wantBytes % (VadFrameSamples * 2);
            if (wantBytes <= 0) break;

            int read = ReadExactOrAvailable(fs, byteBuf, wantBytes);
            int framesRead = read / (VadFrameSamples * 2);
            if (framesRead <= 0) break;

            var shorts = MemoryMarshal.Cast<byte, short>(byteBuf.AsSpan(0, framesRead * VadFrameSamples * 2));
            for (int f = 0; f < framesRead; f++)
            {
                double sumSq = 0;
                int baseOff = f * VadFrameSamples;
                for (int j = 0; j < VadFrameSamples; j++)
                {
                    double sample = shorts[baseOff + j] / 32768.0;
                    sumSq += sample * sample;
                }
                levels[outIdx++] = (float)(10.0 * Math.Log10(sumSq / VadFrameSamples + 1e-10));
            }
            remainingBytes -= framesRead * VadFrameSamples * 2L;
        }

        if (outIdx < levels.Length) Array.Resize(ref levels, outIdx);
        return levels;
    }

    /// <summary>
    /// Runs sliding-window ONNX Runtime inference on a 16 kHz mono 16-bit PCM WAV and streams little-endian
    /// float32 emissions (<c>frames × 28</c>) directly to <paramref name="outEmissionsPath"/> so even multi-hour
    /// audiobooks keep low constant memory usage (1:1 <c>Aligner.kt:66-116</c>).
    /// </summary>
    public static (int Frames, double Seconds) ComputeEmissionsToFile(
        string dataDir,
        string pcmWavPath,
        string outEmissionsPath,
        Action<double>? onSecondsDone,
        CancellationToken ct)
    {
        string modelPath = ModelPath(dataDir);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("尚未下载强制对齐模型（mms_fa.int8.onnx）", modelPath);

        if (!TryReadWavInfo(pcmWavPath, out var info) || info.SampleCount == 0)
            throw new InvalidDataException("无法读取解码后的 16 kHz PCM 音频。");

        string? outDir = Path.GetDirectoryName(outEmissionsPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        string partPath = outEmissionsPath + ".part";
        if (File.Exists(partPath)) File.Delete(partPath);

        var session = AcquireSession(modelPath);
        try
        {
            string inputName = session.InputMetadata.Keys.FirstOrDefault() ?? "wav";

            int totalFrames = 0;
            long totalSamples = info.SampleCount;

            using (var wavStream = new FileStream(pcmWavPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var outStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                byte[] pcmBytes = new byte[(HopSamples + 2 * ContextSamples) * 2];

                for (long start = 0; start < totalSamples; start += HopSamples)
                {
                    ct.ThrowIfCancellationRequested();
                    long a = Math.Max(0, start - ContextSamples);
                    long b = Math.Min(totalSamples, start + HopSamples + ContextSamples);
                    int count = (int)(b - a);
                    if (count < 400) break;

                    wavStream.Seek(info.DataOffset + a * 2L, SeekOrigin.Begin);
                    int gotBytes = ReadExactOrAvailable(wavStream, pcmBytes, count * 2);
                    int gotSamples = gotBytes / 2;
                    if (gotSamples < 400) break;

                    var shorts = MemoryMarshal.Cast<byte, short>(pcmBytes.AsSpan(0, gotSamples * 2));
                    var x = new float[gotSamples];
                    double mean = 0;
                    for (int i = 0; i < gotSamples; i++)
                    {
                        float v = shorts[i] / 32768f;
                        x[i] = v;
                        mean += v;
                    }
                    mean /= gotSamples;

                    double variance = 0;
                    for (int i = 0; i < gotSamples; i++)
                    {
                        double d = x[i] - mean;
                        variance += d * d;
                    }
                    variance /= gotSamples;
                    float scale = (float)(1.0 / Math.Sqrt(variance + 1e-5));
                    for (int i = 0; i < gotSamples; i++)
                    {
                        x[i] = (float)((x[i] - mean) * scale);
                    }

                    var inputTensor = new DenseTensor<float>(x, new[] { 1, gotSamples });
                    using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) });
                    var outTensor = results[0].AsTensor<float>();
                    int frames = outTensor.Dimensions[1];
                    int stride = outTensor.Dimensions.Length >= 3 ? outTensor.Dimensions[2] : Letters;

                    double windowSec = gotSamples / (double)SampleRate;
                    double fps = frames / windowSec;
                    int lo = (int)Math.Round((start - a) / (double)SampleRate * fps);
                    int hi = Math.Min(frames, lo + (int)Math.Round(Math.Min(HopSamples, totalSamples - start) / (double)SampleRate * fps));
                    lo = Math.Clamp(lo, 0, frames);
                    hi = Math.Clamp(hi, lo, frames);

                    int keepFrames = hi - lo;
                    if (keepFrames > 0)
                    {
                        float[] slice = new float[keepFrames * Letters];
                        if (outTensor is DenseTensor<float> dense && stride == Letters)
                        {
                            dense.Buffer.Span.Slice(lo * Letters, keepFrames * Letters).CopyTo(slice);
                        }
                        else
                        {
                            for (int f = 0; f < keepFrames; f++)
                                for (int c = 0; c < Letters; c++)
                                    slice[f * Letters + c] = outTensor[0, lo + f, c];
                        }
                        outStream.Write(MemoryMarshal.AsBytes(slice.AsSpan()));
                        totalFrames += keepFrames;
                    }

                    onSecondsDone?.Invoke(Math.Min(start + HopSamples, totalSamples) / (double)SampleRate);
                }
            }

            if (File.Exists(outEmissionsPath)) File.Delete(outEmissionsPath);
            File.Move(partPath, outEmissionsPath);

            return (totalFrames, totalSamples / (double)SampleRate);
        }
        finally
        {
            ReleaseSession(session);
        }
    }

    internal readonly record struct WavInfo(
        ushort AudioFormat,
        ushort Channels,
        int SampleRate,
        ushort BitsPerSample,
        long DataOffset,
        long DataBytes)
    {
        public long SampleCount => BitsPerSample == 16 && Channels == 1 ? DataBytes / 2L : 0L;
    }

    internal static bool TryReadWavInfo(string wavPath, out WavInfo info)
    {
        info = default;
        try
        {
            using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 44) return false;
            Span<byte> hdr = stackalloc byte[12];
            if (fs.Read(hdr) < 12) return false;
            if (hdr[0] != 'R' || hdr[1] != 'I' || hdr[2] != 'F' || hdr[3] != 'F' ||
                hdr[8] != 'W' || hdr[9] != 'A' || hdr[10] != 'V' || hdr[11] != 'E')
                return false;

            ushort format = 0, channels = 0, bps = 0;
            int rate = 0;
            long dataOffset = -1, dataBytes = 0;

            Span<byte> chunkHdr = stackalloc byte[8];
            Span<byte> fmt = stackalloc byte[16];
            while (fs.Position + 8 <= fs.Length)
            {
                if (fs.Read(chunkHdr) < 8) break;
                uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHdr.Slice(4, 4));
                long nextPos = fs.Position + chunkSize + (chunkSize & 1);

                if (chunkHdr[0] == 'f' && chunkHdr[1] == 'm' && chunkHdr[2] == 't' && chunkHdr[3] == ' ' && chunkSize >= 16)
                {
                    if (fs.Read(fmt) == 16)
                    {
                        format = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(0, 2));
                        channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(2, 2));
                        rate = BinaryPrimitives.ReadInt32LittleEndian(fmt.Slice(4, 4));
                        bps = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(14, 2));
                    }
                }
                else if (chunkHdr[0] == 'd' && chunkHdr[1] == 'a' && chunkHdr[2] == 't' && chunkHdr[3] == 'a')
                {
                    dataOffset = fs.Position;
                    dataBytes = Math.Min(chunkSize, fs.Length - fs.Position);
                    break;
                }

                if (nextPos > fs.Length) break;
                fs.Seek(nextPos, SeekOrigin.Begin);
            }

            if (dataOffset < 0 || rate <= 0) return false;
            info = new WavInfo(format, channels, rate, bps, dataOffset, dataBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadExactOrAvailable(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
