using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yuyue.Core;

namespace Yuyue.Speech;

/// <summary>
/// 1:1 Windows implementation of <c>apple/Yuyue/Speech/SoundLibrary.swift</c> and <c>SfxMaker.swift</c>:
/// manages the optional <c>sounds-1</c> drama room-tone &amp; spot-SFX asset pack (<c>models/sounds-1/library.json</c>),
/// registers it with <see cref="YuyueCore.DramaSetSoundLibrary"/>, supports importing a local <c>.zip</c> pack or
/// downloading from release mirrors when published, and exposes an optional local HTTP SFX maker endpoint
/// (<c>YUYUE_SFX_URL</c>) for custom spot sound synthesis.
/// </summary>
public static class SoundLibraryService
{
    public const string FolderName = "sounds-1";
    public const string OfficialUrl = "https://github.com/inokoru/yuyue/releases/download/models-1/sounds-1.zip";
    /// <summary>BLAKE3 of sounds-1.zip once published to models-1 (matches SoundLibrary.swift).</summary>
    public const string Digest = "";

    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromMinutes(10) };

    public static string Folder(string dataDir) => Path.Combine(dataDir, "models", FolderName);
    public static string ManifestPath(string dataDir) => Path.Combine(Folder(dataDir), "library.json");
    public static bool IsInstalled(string dataDir) => File.Exists(ManifestPath(dataDir));

    public static string? SfxMakerEndpoint
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("YUYUE_SFX_URL");
            return !string.IsNullOrWhiteSpace(env) && Uri.TryCreate(env.Trim(), UriKind.Absolute, out _)
                ? env.Trim().TrimEnd('/')
                : null;
        }
    }

    public static bool IsSfxMakerAvailable => SfxMakerEndpoint is not null;

    /// <summary>Registers the installed <c>sounds-1</c> library with Rust core; returns asset count loaded.</summary>
    public static uint Register(YuyueCore core, string dataDir)
    {
        if (!IsInstalled(dataDir)) return 0;
        try { return core.DramaSetSoundLibrary(Folder(dataDir)); }
        catch { return 0; }
    }

    /// <summary>
    /// Downloads and unpacks <c>sounds-1.zip</c> when published (or from <c>YUYUE_SOUNDS_URL</c>),
    /// falling back silently to Rust core's built-in synthesized room tones &amp; spot sounds when offline.
    /// </summary>
    public static async Task<uint> EnsureAsync(YuyueCore core, string dataDir, CancellationToken ct = default)
    {
        if (IsInstalled(dataDir)) return Register(core, dataDir);

        string? overrideUrl = Environment.GetEnvironmentVariable("YUYUE_SOUNDS_URL");
        string? wantDigest = string.IsNullOrWhiteSpace(overrideUrl) ? Digest : null;
        if (wantDigest is not null && wantDigest.Length == 0)
            return 0;

        var urls = !string.IsNullOrWhiteSpace(overrideUrl)
            ? new List<string> { overrideUrl.Trim() }
            : ModelDownload.CandidateUrls(FolderName + ".zip", OfficialUrl);

        string modelsDir = Path.Combine(dataDir, "models");
        Directory.CreateDirectory(modelsDir);
        string partFile = Path.Combine(modelsDir, $".{FolderName}.zip.part");

        try
        {
            await ModelDownload.DownloadWithResumeAsync(urls, partFile, 0, wantDigest, (_, _) => { }, ct);
            uint loaded = await ImportLocalZipAsync(core, dataDir, partFile, ct);
            try { File.Delete(partFile); } catch { }
            return loaded;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Unpacks a local <c>sounds-1.zip</c> pack into <c>models/sounds-1</c> and registers it with core.</summary>
    public static async Task<uint> ImportLocalZipAsync(YuyueCore core, string dataDir, string zipPath, CancellationToken ct = default)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("找不到音效素材压缩包", zipPath);

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string modelsDir = Path.Combine(dataDir, "models");
            Directory.CreateDirectory(modelsDir);
            string tempDir = Path.Combine(modelsDir, $"unpack-{Guid.NewGuid():N}");
            try
            {
                ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);
                string? manifest = Directory.EnumerateFiles(tempDir, "library.json", SearchOption.AllDirectories).FirstOrDefault();
                if (manifest is null)
                    throw new InvalidDataException("压缩包内未找到 library.json，不是有效的语阅音效素材包。");

                string srcRoot = Path.GetDirectoryName(manifest)!;
                string dst = Folder(dataDir);
                if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
                Directory.Move(srcRoot, dst);
                return Register(core, dataDir);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }, ct);
    }

    public static void DeleteLibrary(string dataDir)
    {
        string dst = Folder(dataDir);
        if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
    }

    /// <summary>
    /// 1:1 <c>SfxMaker.make(description:seconds:)</c>: queries a local Stable Audio Open / SFX server when configured,
    /// caching generated WAVs under <c>cache/sfx/</c>.
    /// </summary>
    public static async Task<byte[]?> GenerateSfxWavAsync(string dataDir, string description, double seconds, CancellationToken ct)
    {
        string? endpoint = SfxMakerEndpoint;
        if (endpoint is null || string.IsNullOrWhiteSpace(description)) return null;

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{description}\u0001{seconds:F2}"));
        string key = Convert.ToHexString(hashBytes.AsSpan(0, 16)).ToLowerInvariant();
        string cacheDir = Path.Combine(dataDir, "cache", "sfx");
        string cacheFile = Path.Combine(cacheDir, $"{key}.wav");
        if (File.Exists(cacheFile))
            return await File.ReadAllBytesAsync(cacheFile, ct);

        string payload = JsonSerializer.Serialize(new { prompt = description, seconds });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/generate")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        byte[] wav = await resp.Content.ReadAsByteArrayAsync(ct);
        if (wav.Length <= 44) return null;
        Directory.CreateDirectory(cacheDir);
        await File.WriteAllBytesAsync(cacheFile, wav, ct);
        return wav;
    }
}
