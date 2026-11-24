using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Yuyue.Core;
using CoreTimeSpan = Yuyue.Core.TimeSpan;

namespace Yuyue.Speech;

public enum AudiobookWorkPhase
{
    Idle,
    DownloadingModel,
    DecodingAudio,
    RunningAlignment,
    Done,
    Failed,
}

public sealed record AudiobookWorkStatus(
    string RecordingId,
    AudiobookWorkPhase Phase,
    double Fraction,
    string Message,
    AudiobookProgress? Progress = null);

/// <summary>
/// 1:1 Windows implementation of <c>android/.../speech/AudiobookWork.kt</c> and <c>apple/Yuyue/Speech/AudiobookWork.swift</c>:
/// drives Rust core's <see cref="YuyueCore.AudiobookNextTask"/> / <see cref="YuyueCore.AudiobookSubmit"/> pipeline
/// (VAD -> MMS letter emissions -> forced sentence alignment) in the background on desktop Windows.
/// </summary>
public sealed class AudiobookWork : IDisposable
{
    private readonly YuyueCore _core;
    private readonly string _dataDir;
    private readonly Action? _onAlignStarted;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<string, double> _focus = new();
    private readonly ConcurrentDictionary<string, AudiobookWorkStatus> _statuses = new();

    public event Action<AudiobookWorkStatus>? StatusChanged;

    public AudiobookWork(YuyueCore core, string dataDir, Action? onAlignStarted = null)
    {
        _core = core;
        _dataDir = dataDir;
        _onAlignStarted = onAlignStarted;
    }

    public AudiobookWorkStatus? GetStatus(string recordingId) =>
        _statuses.TryGetValue(recordingId, out var s) ? s : null;

    public bool IsRunning(string recordingId) => _running.ContainsKey(recordingId);

    public string WorkFolderOf(string recordingId)
    {
        string dir = Path.Combine(_dataDir, "audiobook-work", recordingId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Starts (or reprioritizes) background forced alignment for <paramref name="recordingId"/>.
    /// If <paramref name="autoDownloadModel"/> is true and <c>mms_fa.int8.onnx</c> is missing,
    /// downloads and verifies the model first with live progress reporting.
    /// </summary>
    public void Start(string recordingId, string? ebookId = null, double? focusSec = null, bool autoDownloadModel = false, bool trackAnalytics = true)
    {
        if (focusSec is double f) _focus[recordingId] = f;

        if (!string.IsNullOrEmpty(ebookId))
        {
            try
            {
                var p = _core.AudiobookProgress(recordingId);
                if (p.PairedWith != ebookId) _core.AudiobookPair(recordingId, ebookId);
            }
            catch { }
        }

        if (_running.ContainsKey(recordingId)) return;

        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(recordingId, cts))
        {
            cts.Dispose();
            return;
        }

        if (trackAnalytics)
        {
            try { _onAlignStarted?.Invoke(); } catch { }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(recordingId, autoDownloadModel, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Publish(new AudiobookWorkStatus(recordingId, AudiobookWorkPhase.Idle, 0, "已暂停对齐"));
            }
            catch (Exception ex)
            {
                Publish(new AudiobookWorkStatus(recordingId, AudiobookWorkPhase.Failed, 0, ex.Message));
            }
            finally
            {
                if (_running.TryRemove(recordingId, out var doneCts))
                    doneCts.Dispose();
                if (_running.IsEmpty)
                    Aligner.Unload();
            }
        });
    }

    public void Stop(string recordingId)
    {
        if (_running.TryRemove(recordingId, out var cts))
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
        if (_running.IsEmpty)
            Aligner.Unload();
    }

    public void Dispose()
    {
        foreach (var kv in _running.ToArray())
            Stop(kv.Key);
        Aligner.Unload();
    }

    private void Publish(AudiobookWorkStatus status)
    {
        _statuses[status.RecordingId] = status;
        StatusChanged?.Invoke(status);
    }

    private async Task RunAsync(string recordingId, bool autoDownloadModel, CancellationToken ct)
    {
        AudioRecording rec;
        try { rec = _core.AudioRecording(recordingId); }
        catch (Exception ex)
        {
            Publish(new AudiobookWorkStatus(recordingId, AudiobookWorkPhase.Failed, 0, $"无法读取录音记录：{ex.Message}"));
            return;
        }

        if (autoDownloadModel && !Aligner.IsInstalled(_dataDir))
        {
            Publish(new AudiobookWorkStatus(recordingId, AudiobookWorkPhase.DownloadingModel, 0, "正在下载强制对齐模型（约 303 MB）…"));
            await Aligner.EnsureAsync(_dataDir, frac =>
            {
                Publish(new AudiobookWorkStatus(
                    recordingId,
                    AudiobookWorkPhase.DownloadingModel,
                    frac,
                    $"正在下载强制对齐模型… {Math.Round(frac * 100)}%"));
            }, ct);
        }

        string folder = WorkFolderOf(recordingId);

        while (!ct.IsCancellationRequested)
        {
            bool modelInstalled = Aligner.IsInstalled(_dataDir);
            _core.AudiobookSetCaps(new AudiobookCaps(
                Vad: true,
                Transcribe: modelInstalled,
                Letters: true,
                Align: modelInstalled,
                WholeBookNow: true));

            double? focus = _focus.TryGetValue(recordingId, out var fv) ? fv : null;
            AudiobookTask? task = _core.AudiobookNextTask(recordingId, focus);
            if (task is null)
            {
                var curProg = SafeProgress(recordingId);
                if (curProg?.Done == true)
                {
                    CleanupTempPcmFiles(folder);
                    Publish(new AudiobookWorkStatus(
                        recordingId,
                        AudiobookWorkPhase.Done,
                        1.0,
                        $"已完成听读对齐（精确 {curProg.Exact} / {curProg.Sentences} 句）",
                        curProg));
                }
                else
                {
                    Publish(new AudiobookWorkStatus(
                        recordingId,
                        AudiobookWorkPhase.Idle,
                        ComputeProgressFraction(curProg),
                        modelInstalled ? "对齐暂告一段落" : "需要下载强制对齐模型（约 303 MB）后继续",
                        curProg));
                }
                break;
            }

            string taskId;
            AudiobookResult result;
            switch (task)
            {
                case AudiobookTask.Vad vadTask:
                    taskId = vadTask.Id;
                    Publish(new AudiobookWorkStatus(recordingId, AudiobookWorkPhase.DecodingAudio, 0.1, $"正在检测人声分段（第 {vadTask.File + 1} 轨）…", SafeProgress(recordingId)));
                    result = await RunVadAsync(rec, folder, (int)vadTask.File, vadTask.From, vadTask.To, vadTask.FileOffset, ct);
                    break;

                case AudiobookTask.Transcribe trTask:
                    taskId = trTask.Id;
                    result = await RunLettersAsync(recordingId, rec, folder, (int)trTask.File, trTask.Spans, trTask.FileOffset, ct);
                    break;

                case AudiobookTask.Align alTask:
                    taskId = alTask.Id;
                    result = await RunAlignAsync(recordingId, rec, folder, (int)alTask.File, alTask.From, alTask.To, alTask.Sentences, alTask.Lang, alTask.FileOffset, ct);
                    break;

                default:
                    return;
            }

            AudiobookResult submitResult = ct.IsCancellationRequested ? new AudiobookResult.Returned() : result;
            var progress = _core.AudiobookSubmit(recordingId, taskId, submitResult);
            double frac = ComputeProgressFraction(progress);

            if (progress.Done)
            {
                CleanupTempPcmFiles(folder);
                Publish(new AudiobookWorkStatus(
                    recordingId,
                    AudiobookWorkPhase.Done,
                    1.0,
                    $"已完成听读对齐（精确 {progress.Exact} / {progress.Sentences} 句）",
                    progress));
                break;
            }

            string summary = progress.Sentences > 0
                ? $"正在本地强制对齐… 精确 {progress.Exact} / {progress.Sentences} 句"
                : $"正在分析音频并匹配电子书… {Math.Round(frac * 100)}%";
            Publish(new AudiobookWorkStatus(recordingId, AudiobookWorkPhase.RunningAlignment, frac, summary, progress));
        }
    }

    private AudiobookProgress? SafeProgress(string recordingId)
    {
        try { return _core.AudiobookProgress(recordingId); }
        catch { return null; }
    }

    private static double ComputeProgressFraction(AudiobookProgress? p)
    {
        if (p is null) return 0;
        if (p.Done) return 1.0;
        if (p.Sentences > 0)
            return Math.Clamp((p.Exact + p.Spread * 0.5) / p.Sentences, 0.05, 0.99);
        if (p.Seconds > 1)
            return Math.Clamp(p.Transcribed / p.Seconds, 0.02, 0.95);
        return 0.05;
    }

    private async Task<AudiobookResult> RunVadAsync(
        AudioRecording rec, string folder, int fileIndex, double from, double to, double offset, CancellationToken ct)
    {
        try
        {
            string levelsPath = await EnsureLevelsFileAsync(rec, folder, fileIndex, ct);
            const double frameSec = 0.03;
            int totalFrames = (int)(new FileInfo(levelsPath).Length / 4L);
            int lo = Math.Clamp((int)((from - offset) / frameSec), 0, totalFrames);
            int hi = Math.Clamp((int)((to - offset) / frameSec), lo, totalFrames);
            float[] slice = ReadFloatSlice(levelsPath, lo, hi - lo);
            CoreTimeSpan[] speech = _core.AudiobookSpeech(slice, frameSec, offset + lo * frameSec);
            return new AudiobookResult.Vad(speech, []);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new AudiobookResult.Failed(ex.Message); }
    }

    private async Task<AudiobookResult> RunLettersAsync(
        string recordingId, AudioRecording rec, string folder, int fileIndex, CoreTimeSpan[] spans, double offset, CancellationToken ct)
    {
        try
        {
            string emPath = await EnsureEmissionsFileAsync(recordingId, rec, folder, fileIndex, ct);
            double fileSec = rec.Files[Math.Clamp(fileIndex, 0, rec.Files.Length - 1)].Seconds;
            var allWords = new List<HeardWord>();
            foreach (var sp in spans)
            {
                ct.ThrowIfCancellationRequested();
                var w = _core.AudiobookHeardLettersSaved(emPath, fileSec, offset, sp.T0, sp.T1);
                allWords.AddRange(w);
            }
            return new AudiobookResult.Transcribe(allWords.ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new AudiobookResult.Failed(ex.Message); }
    }

    private async Task<AudiobookResult> RunAlignAsync(
        string recordingId, AudioRecording rec, string folder, int fileIndex, double from, double to, string[] sentences, string lang, double offset, CancellationToken ct)
    {
        try
        {
            string emPath = await EnsureEmissionsFileAsync(recordingId, rec, folder, fileIndex, ct);
            double fileSec = rec.Files[Math.Clamp(fileIndex, 0, rec.Files.Length - 1)].Seconds;
            var refined = _core.AudiobookAlignWindowSaved(sentences, lang, emPath, fileSec, offset, from, to);
            return refined is not null
                ? new AudiobookResult.Align(refined)
                : new AudiobookResult.Failed("缺少该音频轨的模型发射概率缓存");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new AudiobookResult.Failed(ex.Message); }
    }

    private async Task<string> EnsureLevelsFileAsync(AudioRecording rec, string folder, int fileIndex, CancellationToken ct)
    {
        string path = Path.Combine(folder, $"levels-{fileIndex}.f32");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        int safeIdx = Math.Clamp(fileIndex, 0, rec.Files.Length - 1);
        string pcmPath = await Aligner.EnsurePcm16kWavAsync(rec.Files[safeIdx].Path, folder, safeIdx, ct);
        float[] levels = Aligner.ComputeLevels(pcmPath, ct);

        string part = path + ".part";
        await File.WriteAllBytesAsync(part, MemoryMarshal.AsBytes(levels.AsSpan()).ToArray(), ct);
        if (File.Exists(path)) File.Delete(path);
        File.Move(part, path);
        return path;
    }

    private async Task<string> EnsureEmissionsFileAsync(string recordingId, AudioRecording rec, string folder, int fileIndex, CancellationToken ct)
    {
        string path = Path.Combine(folder, $"emissions-{fileIndex}.f32");
        long frameStrideBytes = 4L * Aligner.Letters;
        if (File.Exists(path))
        {
            long len = new FileInfo(path).Length;
            if (len > 0 && len % frameStrideBytes == 0) return path;
        }

        int safeIdx = Math.Clamp(fileIndex, 0, rec.Files.Length - 1);
        var fileInfo = rec.Files[safeIdx];
        Publish(new AudiobookWorkStatus(
            recordingId,
            AudiobookWorkPhase.DecodingAudio,
            0.1,
            $"正在解码音频（第 {safeIdx + 1} / {rec.Files.Length} 轨）…",
            SafeProgress(recordingId)));

        string pcmPath = await Aligner.EnsurePcm16kWavAsync(fileInfo.Path, folder, safeIdx, ct);

        double totalSec = Math.Max(fileInfo.Seconds, 1.0);
        Aligner.ComputeEmissionsToFile(
            _dataDir,
            pcmPath,
            path,
            doneSec =>
            {
                double f = Math.Clamp(doneSec / totalSec, 0.0, 1.0);
                Publish(new AudiobookWorkStatus(
                    recordingId,
                    AudiobookWorkPhase.RunningAlignment,
                    f,
                    $"正在推理声学特征（第 {safeIdx + 1}/{rec.Files.Length} 轨 · {Math.Round(f * 100)}%）…",
                    SafeProgress(recordingId)));
            },
            ct);

        return path;
    }

    private static float[] ReadFloatSlice(string path, int startFloat, int count)
    {
        if (count <= 0) return [];
        byte[] bytes = new byte[count * 4];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(startFloat * 4L, SeekOrigin.Begin);
        int total = 0;
        while (total < bytes.Length)
        {
            int n = fs.Read(bytes, total, bytes.Length - total);
            if (n <= 0) break;
            total += n;
        }
        int floats = total / 4;
        float[] result = new float[floats];
        MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, floats * 4)).CopyTo(result);
        return result;
    }

    private static void CleanupTempPcmFiles(string folder)
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(folder, "pcm16k-*.wav*"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }
}
