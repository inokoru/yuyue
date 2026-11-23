using Yuyue.Core;

namespace Yuyue.Speech;

public enum LiveVoiceStateKind
{
    Idle,
    Downloading,
    Ready,
    Failed,
}

/// <summary>
/// Manages the on-device neural voice bundle (<c>live-zipvoice-1</c> + <c>voices-1</c>),
/// matching the exact release URLs, BLAKE3 digests, and core registration used on Apple &amp; Android.
/// </summary>
public sealed class LiveVoiceModel
{
    public const string ModelName = "live-zipvoice-1";
    public const string ModelUrl = "https://github.com/inokoru/yuyue/releases/download/models-1/live-zipvoice-1.zip";
    public const string ModelDigest = "7a2542b6301088152211d6edf4bd66ac3527fd29ec85ecf9b35fd4cc059b6bf9";
    public const long ModelBytes = 149_776_622L;

    public const string PackName = "voices-1";
    public const string PackUrl = "https://github.com/inokoru/yuyue/releases/download/models-1/voices-1.zip";
    public const string PackDigest = "2d35635523d08b1b2d9d5cbfb49695a1eb424bbe7d4eafe51e1c255414d44f2c";
    public const long PackBytes = 14_002_684L;

    public const int TotalMb = 160;

    private readonly YuyueCore _core;
    private readonly string _dataDir;
    private CancellationTokenSource? _downloadCts;

    public LiveVoiceStateKind State { get; private set; } = LiveVoiceStateKind.Idle;
    public double DownloadProgress { get; private set; }
    public string StatusMessage { get; private set; } = "";

    public event Action? StateChanged;

    public LiveVoiceModel(YuyueCore core, string dataDir)
    {
        _core = core;
        _dataDir = dataDir;
        RegisterIfInstalled();
    }

    public string ModelDir => Path.Combine(_dataDir, "models", ModelName);
    public string PackDir => Path.Combine(_dataDir, "models", PackName);

    public bool IsModelInstalled =>
        File.Exists(Path.Combine(ModelDir, "encoder.int8.onnx")) &&
        File.Exists(Path.Combine(ModelDir, "decoder.int8.onnx")) &&
        File.Exists(Path.Combine(ModelDir, "vocos_24khz.onnx")) &&
        File.Exists(Path.Combine(ModelDir, "tokens.txt"));

    public bool IsPackInstalled => File.Exists(Path.Combine(PackDir, "voices.json"));
    public bool IsReady => IsModelInstalled && IsPackInstalled;

    public void RegisterIfInstalled()
    {
        try
        {
            if (IsModelInstalled) _core.LiveVoiceSetDir(ModelDir);
            if (IsPackInstalled) _core.CardsSetPack(PackDir);
            if (IsReady)
            {
                State = LiveVoiceStateKind.Ready;
                DownloadProgress = 1.0;
                StatusMessage = "已就绪（离线神经语音 + 内置音色包）";
            }
            else if (State == LiveVoiceStateKind.Ready)
            {
                State = LiveVoiceStateKind.Idle;
                DownloadProgress = 0;
                StatusMessage = "";
            }
        }
        catch { }
    }

    public IReadOnlyList<VoiceCard> GetBuiltinCards()
    {
        if (!IsPackInstalled) return Array.Empty<VoiceCard>();
        try { return _core.CardsBuiltin(); } catch { return Array.Empty<VoiceCard>(); }
    }

    public IReadOnlyList<VoiceCard> GetClonedCards()
    {
        try { return _core.CardsCloned(); } catch { return Array.Empty<VoiceCard>(); }
    }

    public async Task DownloadAsync()
    {
        if (State == LiveVoiceStateKind.Downloading) return;
        RegisterIfInstalled();
        if (IsReady)
        {
            Notify();
            return;
        }

        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        State = LiveVoiceStateKind.Downloading;
        DownloadProgress = 0.0;
        StatusMessage = "正在连接下载服务器…";
        Notify();

        try
        {
            Directory.CreateDirectory(Path.Combine(_dataDir, "models"));
            bool needModel = !IsModelInstalled;
            bool needPack = !IsPackInstalled;
            long totalBytes = (needModel ? ModelBytes : 0) + (needPack ? PackBytes : 0);
            long doneOffset = 0;

            if (needModel)
            {
                await DownloadVerifyUnpackAsync(
                    ModelUrl, ModelDigest, ModelBytes, ModelName, ModelDir,
                    "正在下载离线语音模型 (149 MB)…",
                    bytes => ReportProgress(doneOffset + bytes, totalBytes), ct);
                doneOffset += ModelBytes;
            }

            if (needPack)
            {
                await DownloadVerifyUnpackAsync(
                    PackUrl, PackDigest, PackBytes, PackName, PackDir,
                    "正在下载内置音色包 (14 MB)…",
                    bytes => ReportProgress(doneOffset + bytes, totalBytes), ct);
            }

            RegisterIfInstalled();
            if (!IsReady) throw new InvalidOperationException("解压后未找到完整模型文件");
            Notify();
        }
        catch (OperationCanceledException)
        {
            State = LiveVoiceStateKind.Idle;
            DownloadProgress = 0;
            StatusMessage = "已取消下载";
            Notify();
        }
        catch (Exception ex)
        {
            State = LiveVoiceStateKind.Failed;
            StatusMessage = $"下载失败：{ex.Message}";
            Notify();
        }
    }

    public void CancelDownload() => _downloadCts?.Cancel();

    public void DeleteInstalledModels()
    {
        CancelDownload();
        CardVoiceEngine.Instance.CloseSessions();
        try { if (Directory.Exists(ModelDir)) Directory.Delete(ModelDir, true); } catch { }
        try { if (Directory.Exists(PackDir)) Directory.Delete(PackDir, true); } catch { }
        State = LiveVoiceStateKind.Idle;
        DownloadProgress = 0;
        StatusMessage = "已删除本地模型文件";
        Notify();
    }

    private void ReportProgress(long downloaded, long total)
    {
        if (total <= 0) return;
        double p = Math.Clamp((double)downloaded / total, 0.0, 0.99);
        if (Math.Abs(p - DownloadProgress) >= 0.01)
        {
            DownloadProgress = p;
            StatusMessage = $"正在下载离线自然人声… {(int)(p * 100)}%";
            Notify();
        }
    }

    private async Task DownloadVerifyUnpackAsync(
        string url, string expectedBlake3, long expectedBytes, string name, string destDir,
        string label, Action<long> onBytes, CancellationToken ct)
    {
        StatusMessage = label;
        Notify();

        string modelsRoot = Path.Combine(_dataDir, "models");
        string tmpZip = Path.Combine(modelsRoot, $".{name}.zip.part");
        string unpackDir = Path.Combine(modelsRoot, $".unpack-{name}");
        string asset = url[(url.LastIndexOf('/') + 1)..];
        var urls = ModelDownload.CandidateUrls(asset, url);

        try
        {
            await ModelDownload.DownloadWithResumeAsync(
                urls,
                tmpZip,
                expectedBytes,
                expectedBlake3,
                (done, _) => onBytes(done),
                ct);

            StatusMessage = $"正在解压 {name}…";
            Notify();

            await Task.Run(() =>
            {
                if (Directory.Exists(unpackDir)) Directory.Delete(unpackDir, true);
                YuyueCoreMethods.UnpackModel(tmpZip, unpackDir);
                string inner = Path.Combine(unpackDir, name);
                string source = Directory.Exists(inner) ? inner : unpackDir;
                if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
                Directory.Move(source, destDir);
            }, ct);

            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
        }
        finally
        {
            try { if (Directory.Exists(unpackDir)) Directory.Delete(unpackDir, true); } catch { }
        }
    }

    private void Notify() => StateChanged?.Invoke();
}
