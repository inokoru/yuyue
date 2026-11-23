using System.Net.Http;

namespace Yuyue.Core;

/// <summary>
/// Manages downloadable open-source reading fonts (<c>wenkai</c>, <c>notoserif</c>, <c>notosans</c>)
/// from <see cref="YuyueCoreMethods.FontCatalog"/> and user-imported <c>.ttf</c> / <c>.otf</c> files
/// in <see cref="YuyueCore.FontsDir"/>.
/// </summary>
public sealed class FontShelfService
{
    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromMinutes(10) };

    private readonly string _fontsDir;
    private readonly Dictionary<string, double> _downloading = new();

    public IReadOnlyList<FontPack> Catalog { get; }
    public event Action? Changed;

    public FontShelfService(string fontsDir)
    {
        _fontsDir = fontsDir;
        Directory.CreateDirectory(_fontsDir);
        Catalog = YuyueCoreMethods.FontCatalog();
    }

    public bool IsPackInstalled(string packId)
    {
        var pack = Catalog.FirstOrDefault(p => p.Id == packId);
        if (pack is null) return false;
        return pack.Files.All(f => File.Exists(Path.Combine(_fontsDir, f.File)));
    }

    public bool IsDownloading(string packId) => _downloading.ContainsKey(packId);
    public double GetProgress(string packId) => _downloading.TryGetValue(packId, out var p) ? p : 0.0;

    public IReadOnlyList<string> GetCustomFontFiles()
    {
        if (!Directory.Exists(_fontsDir)) return Array.Empty<string>();
        var catalogFiles = new HashSet<string>(Catalog.SelectMany(c => c.Files.Select(f => f.File)), StringComparer.OrdinalIgnoreCase);
        return Directory.GetFiles(_fontsDir)
            .Select(Path.GetFileName)
            .Where(n => n is not null && !catalogFiles.Contains(n) &&
                        (n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                         n.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
                         n.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase)))
            .Cast<string>()
            .OrderBy(n => n)
            .ToArray();
    }

    public string ImportFontFile(string sourcePath)
    {
        Directory.CreateDirectory(_fontsDir);
        string name = Path.GetFileName(sourcePath);
        string dst = Path.Combine(_fontsDir, name);
        File.Copy(sourcePath, dst, overwrite: true);
        Changed?.Invoke();
        return name;
    }

    public async Task DownloadPackAsync(string packId)
    {
        var pack = Catalog.FirstOrDefault(p => p.Id == packId);
        if (pack is null || IsDownloading(packId) || IsPackInstalled(packId)) return;

        _downloading[packId] = 0.01;
        Changed?.Invoke();

        try
        {
            Directory.CreateDirectory(_fontsDir);
            ulong totalBytes = 0;
            foreach (var f in pack.Files) totalBytes += f.Bytes;
            ulong doneOffset = 0;

            foreach (var file in pack.Files)
            {
                string tmp = Path.Combine(_fontsDir, $".{file.File}.part");
                string dst = Path.Combine(_fontsDir, file.File);

                using var resp = await Http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();

                await using (var remote = await resp.Content.ReadAsStreamAsync())
                await using (var local = File.Create(tmp))
                {
                    byte[] buf = new byte[65536];
                    ulong fileRead = 0;
                    int r;
                    while ((r = await remote.ReadAsync(buf)) > 0)
                    {
                        await local.WriteAsync(buf.AsMemory(0, r));
                        fileRead += (ulong)r;
                        if (totalBytes > 0)
                        {
                            _downloading[packId] = Math.Clamp((double)(doneOffset + fileRead) / totalBytes, 0.01, 0.99);
                            Changed?.Invoke();
                        }
                    }
                }

                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);
                doneOffset += file.Bytes;
            }
        }
        finally
        {
            _downloading.Remove(packId);
            Changed?.Invoke();
        }
    }
}
