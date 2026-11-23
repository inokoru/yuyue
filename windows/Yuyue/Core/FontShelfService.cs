using System.Net.Http;
using Microsoft.Win32;
using Yuyue.Speech;

namespace Yuyue.Core;

public sealed record SystemFontEntry(string DisplayName, string FamilyName);

/// <summary>
/// Manages downloadable open-source reading fonts (<c>wenkai</c>, <c>notoserif</c>, <c>notosans</c>)
/// from <see cref="YuyueCoreMethods.FontCatalog"/>, Windows system CJK fonts, and user-imported
/// <c>.ttf</c> / <c>.otf</c> / <c>.woff2</c> files in <see cref="YuyueCore.FontsDir"/>.
/// </summary>
public sealed class FontShelfService
{
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

    public bool DeleteCustomFont(string fileName)
    {
        string safe = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(safe)) return false;
        string path = Path.Combine(_fontsDir, safe);
        if (!File.Exists(path)) return false;
        try
        {
            File.Delete(path);
            Changed?.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool DeletePack(string packId)
    {
        var pack = Catalog.FirstOrDefault(p => p.Id == packId);
        if (pack is null || IsDownloading(packId)) return false;
        bool removedAny = false;
        foreach (var f in pack.Files)
        {
            foreach (string path in new[] { Path.Combine(_fontsDir, f.File), Path.Combine(_fontsDir, $".{f.File}.part") })
            {
                if (!File.Exists(path)) continue;
                try { File.Delete(path); removedAny = true; } catch { }
            }
        }
        if (removedAny) Changed?.Invoke();
        return removedAny;
    }

    public IReadOnlyList<SystemFontEntry> DiscoverSystemCjkFonts()
    {
        var results = new List<SystemFontEntry>();
        var seenFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string display, string family)
        {
            string f = family.Trim();
            if (string.IsNullOrEmpty(f) || !seenFamilies.Add(f)) return;
            results.Add(new SystemFontEntry(string.IsNullOrWhiteSpace(display) ? f : display.Trim(), f));
        }

        // Curated built-in Windows CJK faces matched against Registry installed fonts
        (string MatchKey, string Display, string Family)[] knownWindowsCjk =
        [
            ("SimSun", "宋体 (SimSun)", "SimSun"),
            ("NSimSun", "新宋体 (NSimSun)", "NSimSun"),
            ("FangSong", "仿宋 (FangSong)", "FangSong"),
            ("KaiTi", "楷体 (KaiTi)", "KaiTi"),
            ("Microsoft YaHei", "微软雅黑", "Microsoft YaHei"),
            ("DengXian", "等线", "DengXian"),
            ("SimHei", "黑体 (SimHei)", "SimHei"),
            ("STSong", "华文宋体", "STSong"),
            ("STKaiti", "华文楷体", "STKaiti"),
            ("STFangsong", "华文仿宋", "STFangsong"),
            ("STZhongsong", "华文中宋", "STZhongsong"),
            ("STXihei", "华文细黑", "STXihei"),
            ("YouYuan", "幼圆", "YouYuan"),
            ("LiSu", "隶书", "LiSu"),
            ("Source Han Serif", "思源宋体（系统安装）", "Source Han Serif SC"),
            ("Source Han Sans", "思源黑体（系统安装）", "Source Han Sans SC"),
            ("Noto Serif CJK", "Noto Serif CJK", "Noto Serif CJK SC"),
            ("Noto Sans CJK", "Noto Sans CJK", "Noto Sans CJK SC"),
            ("LXGW WenKai", "霞鹜文楷（系统安装）", "LXGW WenKai"),
            ("HarmonyOS Sans", "鸿蒙黑体", "HarmonyOS Sans SC"),
            ("MiSans", "小米兰亭 / MiSans", "MiSans"),
            ("PingFang", "苹方 (PingFang)", "PingFang SC"),
            ("Sarasa", "更纱黑体 (Sarasa)", "Sarasa Gothic SC"),
        ];

        string[] cjkTokens =
        [
            "Song", "Kai", "Hei", "Fang", "YaHei", "DengXian", "MingLiU", "SimSun", "SimHei", "YouYuan", "LiSu",
            "CJK", "SC", "TC", "Han", "WenKai", "PingFang", "MiSans", "HarmonyOS", "Sarasa", "Alibaba", "FZ",
            "宋", "楷", "黑", "仿", "雅黑", "等线", "华文", "方正", "文楷", "思源", "苹方", "鸿蒙", "兰亭", "圆", "隶",
        ];

        var registryNames = new List<string>();
        foreach (var rootKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = rootKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key is null) continue;
                registryNames.AddRange(key.GetValueNames());
            }
            catch { }
        }

        foreach (var (match, display, family) in knownWindowsCjk)
        {
            if (registryNames.Any(n => n.Contains(match, StringComparison.OrdinalIgnoreCase)))
                TryAdd(display, family);
        }

        foreach (string rawName in registryNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string cleaned = rawName
                .Replace("(TrueType)", "", StringComparison.OrdinalIgnoreCase)
                .Replace("(OpenType)", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (string.IsNullOrEmpty(cleaned) || cleaned.Contains('&')) continue;
            if (!cjkTokens.Any(tok => cleaned.Contains(tok, StringComparison.OrdinalIgnoreCase))) continue;
            if (cleaned.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
                cleaned.Contains("Light", StringComparison.OrdinalIgnoreCase) ||
                cleaned.Contains("Italic", StringComparison.OrdinalIgnoreCase) ||
                cleaned.Contains("ExtB", StringComparison.OrdinalIgnoreCase))
                continue;
            TryAdd(cleaned, cleaned);
        }

        if (results.Count == 0)
        {
            TryAdd("宋体 (SimSun)", "SimSun");
            TryAdd("楷体 (KaiTi)", "KaiTi");
            TryAdd("仿宋 (FangSong)", "FangSong");
            TryAdd("微软雅黑", "Microsoft YaHei");
        }
        return results;
    }

    public async Task DownloadPackAsync(string packId, CancellationToken ct = default)
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
                ct.ThrowIfCancellationRequested();
                string tmp = Path.Combine(_fontsDir, $".{file.File}.part");
                string dst = Path.Combine(_fontsDir, file.File);
                if (File.Exists(dst))
                {
                    doneOffset += file.Bytes;
                    continue;
                }

                var urls = ModelDownload.CandidateUrls(file.File, officialUrl: file.Url, mirrorPath: $"fonts/{file.File}");
                ulong baseDone = doneOffset;
                await ModelDownload.DownloadWithResumeAsync(
                    urls,
                    tmp,
                    (long)file.Bytes,
                    expectedBlake3: null,
                    (got, _) =>
                    {
                        if (totalBytes > 0)
                        {
                            _downloading[packId] = Math.Clamp((double)(baseDone + (ulong)Math.Max(0, got)) / totalBytes, 0.01, 0.99);
                            Changed?.Invoke();
                        }
                    },
                    ct);

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
