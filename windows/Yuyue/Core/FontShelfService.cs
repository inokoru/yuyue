using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Yuyue.Speech;

namespace Yuyue.Core;

public sealed record SystemFontEntry(string DisplayName, string FamilyName);

/// <summary>
/// Manages downloadable open-source reading fonts (<c>wenkai</c>, <c>notoserif</c>, <c>notosans</c>)
/// from <see cref="YuyueCoreMethods.FontCatalog"/>, Windows system CJK fonts, and user-imported
/// <c>.ttf</c> / <c>.otf</c> / <c>.ttc</c> / <c>.woff2</c> files in <see cref="YuyueCore.FontsDir"/>.
/// </summary>
public sealed class FontShelfService
{
    private const uint FrPrivate = 0x10;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AddFontResourceExW(string lpszFilename, uint fl, IntPtr pdv);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool RemoveFontResourceExW(string lpszFilename, uint fl, IntPtr pdv);

    private static readonly Regex TrailingStyleSuffixRx = new(
        @"(?:\s+|-|_)(?:Regular|Normal|Book|Roman|Standard|Medium|Light|DemiLight|ExtraLight|UltraLight|Thin|Hairline|SemiBold|Demibold|Bold|ExtraBold|UltraBold|Heavy|Black|Italic|Oblique|VF|Variable|W[1-9]|[1-9]5)(?=\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _fontsDir;
    private readonly object _gate = new();
    private readonly object _sysFontsGate = new();
    private readonly Dictionary<string, double> _downloading = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _downloadCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string? Family, string? Localized)> _familyCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SystemFontEntry>? _cachedSystemFonts;

    public IReadOnlyList<FontPack> Catalog { get; }
    public string FontsDir => _fontsDir;
    public IReadOnlyList<SystemFontEntry>? CachedSystemCjkFonts => _cachedSystemFonts;
    public event Action? Changed;

    public FontShelfService(string fontsDir)
    {
        _fontsDir = fontsDir;
        Directory.CreateDirectory(_fontsDir);
        Catalog = YuyueCoreMethods.FontCatalog();
        RegisterExistingFonts();
    }

    private void RegisterExistingFonts()
    {
        try
        {
            if (!Directory.Exists(_fontsDir)) return;
            foreach (string path in Directory.EnumerateFiles(_fontsDir))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith('.')) continue;
                if (IsTrueTypeOrOpenTypeFile(name))
                    TryRegisterPrivateFont(path);
            }
        }
        catch { }
    }

    private static bool IsTrueTypeOrOpenTypeFile(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        return ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedCustomFontFile(string fileName) =>
        !fileName.StartsWith('.') &&
        (IsTrueTypeOrOpenTypeFile(fileName) || fileName.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase));

    private static void TryRegisterPrivateFont(string fullPath)
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(fullPath))
                _ = AddFontResourceExW(fullPath, FrPrivate, IntPtr.Zero);
        }
        catch { }
    }

    private static void TryUnregisterPrivateFont(string fullPath)
    {
        try
        {
            if (OperatingSystem.IsWindows() && File.Exists(fullPath))
                _ = RemoveFontResourceExW(fullPath, FrPrivate, IntPtr.Zero);
        }
        catch { }
    }

    public bool IsPackInstalled(string packId)
    {
        var pack = Catalog.FirstOrDefault(p => p.Id == packId);
        if (pack is null) return false;
        return pack.Files.All(f => File.Exists(Path.Combine(_fontsDir, f.File)));
    }

    public bool IsDownloading(string packId)
    {
        lock (_gate) return _downloading.ContainsKey(packId);
    }

    public double GetProgress(string packId)
    {
        lock (_gate) return _downloading.TryGetValue(packId, out var p) ? p : 0.0;
    }

    public void CancelDownload(string packId)
    {
        CancellationTokenSource? cts = null;
        bool removed;
        lock (_gate)
        {
            if (_downloadCts.TryGetValue(packId, out cts))
                _downloadCts.Remove(packId);
            removed = _downloading.Remove(packId);
        }
        try { cts?.Cancel(); } catch { }
        if (removed || cts is not null)
            Changed?.Invoke();
    }

    public IReadOnlyList<string> GetCustomFontFiles()
    {
        if (!Directory.Exists(_fontsDir)) return Array.Empty<string>();
        var catalogFiles = new HashSet<string>(Catalog.SelectMany(c => c.Files.Select(f => f.File)), StringComparer.OrdinalIgnoreCase);
        return Directory.GetFiles(_fontsDir)
            .Select(Path.GetFileName)
            .Where(n => n is not null && !catalogFiles.Contains(n) && IsSupportedCustomFontFile(n))
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string ImportFontFile(string sourcePath)
    {
        Directory.CreateDirectory(_fontsDir);
        string origName = Path.GetFileName(sourcePath);
        string rawBase = Path.GetFileNameWithoutExtension(origName).Trim();
        var sb = new System.Text.StringBuilder(rawBase.Length);
        foreach (char ch in rawBase)
        {
            if (ch is '/' or '\\' or '"' or '\'' or '<' or '>' or ';' or '{' or '}')
                sb.Append('-');
            else
                sb.Append(ch);
        }
        string baseName = sb.ToString().TrimStart('.').Trim();
        string ext = Path.GetExtension(origName);
        if (string.IsNullOrEmpty(baseName)) baseName = "font";

        string name = $"{baseName}{ext}";
        int suffix = 2;
        while (File.Exists(Path.Combine(_fontsDir, name)))
        {
            name = $"{baseName}-{suffix}{ext}";
            suffix++;
        }

        string dst = Path.Combine(_fontsDir, name);
        File.Copy(sourcePath, dst, overwrite: false);
        lock (_gate) _familyCache.Remove(name);
        if (IsTrueTypeOrOpenTypeFile(name))
            TryRegisterPrivateFont(dst);
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
            TryUnregisterPrivateFont(path);
            File.Delete(path);
            lock (_gate) _familyCache.Remove(safe);
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
            string installedPath = Path.Combine(_fontsDir, f.File);
            TryUnregisterPrivateFont(installedPath);
            lock (_gate) _familyCache.Remove(f.File);
            foreach (string path in new[] { installedPath, Path.Combine(_fontsDir, $".{f.File}.part") })
            {
                if (!File.Exists(path)) continue;
                try { File.Delete(path); removedAny = true; } catch { }
            }
        }
        if (removedAny) Changed?.Invoke();
        return removedAny;
    }

    public string? GetFontFileFamily(string fileName)
    {
        var (fam, _) = GetCachedFileNames(fileName);
        return fam;
    }

    public string GetFontFileDisplayName(string fileName)
    {
        var (fam, loc) = GetCachedFileNames(fileName);
        if (!string.IsNullOrWhiteSpace(loc)) return loc!;
        if (!string.IsNullOrWhiteSpace(fam)) return fam!;
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private (string? Family, string? Localized) GetCachedFileNames(string fileName)
    {
        string safe = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(safe)) return (null, null);
        lock (_gate)
        {
            if (_familyCache.TryGetValue(safe, out var hit))
                return hit;
        }
        string fullPath = Path.Combine(_fontsDir, safe);
        if (!File.Exists(fullPath) || !IsTrueTypeOrOpenTypeFile(safe))
            return (null, null);

        string? fam = ReadTrueTypeFamilyName(fullPath, preferLocalized: false);
        string? loc = ReadTrueTypeFamilyName(fullPath, preferLocalized: true);
        lock (_gate) _familyCache[safe] = (fam, loc);
        return (fam, loc);
    }

    /// <summary>
    /// Builds a WinUI XAML <c>FontFamily</c> source string referencing downloaded/imported font files via
    /// <c>ms-appdata:///local/...#&lt;Family&gt;</c> (when packaged) or local file URI/path <c>...#&lt;Family&gt;</c>
    /// with fallback families.
    /// </summary>
    public string ResolveXamlFontSource(string idOrFace, string fallbackFamily)
    {
        if (string.IsNullOrWhiteSpace(idOrFace)) return fallbackFamily;

        if (idOrFace.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            string sysFam = CleanRegistryFontFamilyName(idOrFace[6..]);
            return string.IsNullOrEmpty(sysFam) ? fallbackFamily : $"{sysFam}, {fallbackFamily}";
        }
        if (idOrFace.StartsWith("sys:", StringComparison.OrdinalIgnoreCase))
        {
            string sysFam = CleanRegistryFontFamilyName(idOrFace[4..]);
            return string.IsNullOrEmpty(sysFam) ? fallbackFamily : $"{sysFam}, {fallbackFamily}";
        }
        if (idOrFace.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            string fn = Path.GetFileName(idOrFace[5..]);
            string fullPath = Path.Combine(_fontsDir, fn);
            if (File.Exists(fullPath) && IsTrueTypeOrOpenTypeFile(fn))
            {
                string fam = GetFontFileFamily(fn) ?? Path.GetFileNameWithoutExtension(fn);
                string uriRef = FormatXamlFontFilePrefix(fullPath);
                return $"{uriRef}#{fam}, {fam}, {fallbackFamily}";
            }
            return fallbackFamily;
        }

        var pack = Catalog.FirstOrDefault(p => string.Equals(p.Id, idOrFace, StringComparison.OrdinalIgnoreCase));
        if (pack is not null && IsPackInstalled(pack.Id) && pack.Files.Length > 0)
        {
            string? fontFile = pack.Files.Select(f => f.File).FirstOrDefault(IsTrueTypeOrOpenTypeFile);
            if (fontFile is not null)
            {
                string fullPath = Path.Combine(_fontsDir, fontFile);
                string defaultPackFam = pack.Id switch
                {
                    "wenkai" => "LXGW WenKai Lite",
                    "notoserif" => "Noto Serif SC",
                    "notosans" => "Noto Sans SC",
                    _ => pack.PreviewFamily ?? pack.Name,
                };
                string fam = GetFontFileFamily(fontFile) ?? defaultPackFam;
                string uriRef = FormatXamlFontFilePrefix(fullPath);
                return $"{uriRef}#{fam}, {fam}, {fallbackFamily}";
            }
        }

        return fallbackFamily;
    }

    private static string FormatXamlFontFilePrefix(string fullPath)
    {
        try
        {
            string localAppData = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
            if (!string.IsNullOrEmpty(localAppData))
            {
                string normRoot = Path.GetFullPath(localAppData).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string normFile = Path.GetFullPath(fullPath);
                if (normFile.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = normFile[normRoot.Length..].Replace('\\', '/');
                    return $"ms-appdata:///local/{rel}";
                }
            }
        }
        catch
        {
            // Unpackaged desktop mode (WindowsPackageType=None): use normalized file path for DirectWrite FontFamily
        }
        return Path.GetFullPath(fullPath).Replace('\\', '/');
    }

    public static string CleanRegistryFontFamilyName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return "";
        string cleaned = rawName
            .Replace("(TrueType Collection)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(TrueType)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(OpenType)", "", StringComparison.OrdinalIgnoreCase)
            .Replace("(All res)", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (cleaned.Contains('&'))
        {
            string[] parts = cleaned.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            cleaned = parts.FirstOrDefault(p => !p.EndsWith(" UI", StringComparison.OrdinalIgnoreCase) && !p.Contains("ExtB", StringComparison.OrdinalIgnoreCase))
                      ?? parts.FirstOrDefault()
                      ?? "";
        }

        // Iteratively strip trailing weight/style suffixes ("Source Han Serif SC SemiBold" -> "Source Han Serif SC")
        for (int pass = 0; pass < 3; pass++)
        {
            string next = TrailingStyleSuffixRx.Replace(cleaned, "").Trim();
            if (next.Length == 0 || next == cleaned) break;
            cleaned = next;
        }

        return cleaned;
    }

    /// <summary>
    /// Parses the OpenType / TrueType / TTC (<c>ttcf</c>) <c>name</c> table to extract the real typographic
    /// family name (<c>nameID</c> 16 preferred over <c>nameID</c> 1). Returns <c>null</c> on non-SFNT or truncated files.
    /// </summary>
    public static string? ReadTrueTypeFamilyName(string filePath, bool preferLocalized = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 28) return null;

            Span<byte> hdr = stackalloc byte[16];
            if (fs.Read(hdr[..12]) < 12) return null;

            long sfntOffset = 0;
            uint magic = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
            if (magic == 0x74746366u) // "ttcf" (.ttc TrueType Collection)
            {
                uint numFonts = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(8, 4));
                if (numFonts == 0 || fs.Length < 16) return null;
                if (fs.Read(hdr.Slice(12, 4)) < 4) return null;
                sfntOffset = BinaryPrimitives.ReadUInt32BigEndian(hdr.Slice(12, 4));
                if (sfntOffset <= 0 || sfntOffset + 12 > fs.Length) return null;
                fs.Seek(sfntOffset, SeekOrigin.Begin);
                if (fs.Read(hdr[..12]) < 12) return null;
                magic = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
            }

            // 0x00010000 (TrueType), "OTTO" (0x4F54544F), "true" (0x74727565)
            if (magic is not (0x00010000u or 0x4F54544Fu or 0x74727565u))
                return null;

            ushort numTables = BinaryPrimitives.ReadUInt16BigEndian(hdr.Slice(4, 2));
            if (numTables == 0 || numTables > 256 || sfntOffset + 12 + numTables * 16L > fs.Length)
                return null;

            byte[] dirBuf = new byte[numTables * 16];
            if (fs.Read(dirBuf, 0, dirBuf.Length) < dirBuf.Length) return null;

            uint nameOffset = 0, nameLength = 0;
            for (int i = 0; i < numTables; i++)
            {
                int pos = i * 16;
                if (dirBuf[pos] == (byte)'n' && dirBuf[pos + 1] == (byte)'a' &&
                    dirBuf[pos + 2] == (byte)'m' && dirBuf[pos + 3] == (byte)'e')
                {
                    nameOffset = BinaryPrimitives.ReadUInt32BigEndian(dirBuf.AsSpan(pos + 8, 4));
                    nameLength = BinaryPrimitives.ReadUInt32BigEndian(dirBuf.AsSpan(pos + 12, 4));
                    break;
                }
            }

            if (nameOffset == 0 || nameLength < 6 || nameOffset + 6L > fs.Length)
                return null;

            fs.Seek(nameOffset, SeekOrigin.Begin);
            int readLen = (int)Math.Min(nameLength, Math.Min(262_144u, (uint)(fs.Length - nameOffset)));
            byte[] nameTbl = new byte[readLen];
            if (fs.Read(nameTbl, 0, readLen) < 6) return null;

            ushort count = BinaryPrimitives.ReadUInt16BigEndian(nameTbl.AsSpan(2, 2));
            ushort strStorageOffset = BinaryPrimitives.ReadUInt16BigEndian(nameTbl.AsSpan(4, 2));
            if (count == 0 || count > 4096) return null;

            string? best = null;
            int bestScore = int.MinValue;

            for (int i = 0; i < count; i++)
            {
                int recPos = 6 + i * 12;
                if (recPos + 12 > nameTbl.Length) break;

                ushort platformId = BinaryPrimitives.ReadUInt16BigEndian(nameTbl.AsSpan(recPos, 2));
                ushort encodingId = BinaryPrimitives.ReadUInt16BigEndian(nameTbl.AsSpan(recPos + 2, 2));
                ushort langId = BinaryPrimitives.ReadUInt16BigEndian(nameTbl.AsSpan(recPos + 4, 2));
                ushort nameId = BinaryPrimitives.ReadUInt16BigEndian(nameTbl.AsSpan(recPos + 6, 2));
                ushort strLen = BinaryPrimitives.ReadUInt16BigEndian(nameTbl.AsSpan(recPos + 8, 2));
                ushort strOff = BinaryPrimitives.ReadUInt16BigEndian(nameTbl.AsSpan(recPos + 10, 2));

                if (nameId is not (16 or 1) || strLen == 0) continue;
                int absOff = strStorageOffset + strOff;
                if (absOff < 0 || absOff + strLen > nameTbl.Length) continue;

                string? decoded = null;
                if (platformId == 0 || (platformId == 3 && encodingId is 0 or 1 or 10))
                {
                    decoded = Encoding.BigEndianUnicode.GetString(nameTbl, absOff, strLen);
                }
                else if (platformId == 1 && encodingId == 0)
                {
                    decoded = Encoding.ASCII.GetString(nameTbl, absOff, strLen);
                }
                if (string.IsNullOrWhiteSpace(decoded)) continue;
                decoded = decoded.Replace("\0", "").Trim();
                if (decoded.Length == 0 || decoded.StartsWith('.')) continue;

                int score = nameId == 16 ? 100 : 50;
                if (platformId == 3) score += 20;
                if (preferLocalized)
                {
                    if (langId == 0x0804) score += 40; // zh-CN
                    else if (langId is 0x0404 or 0x0C04) score += 20;
                    else if (langId == 0x0409) score += 10;
                }
                else
                {
                    if (langId == 0x0409) score += 40; // en-US canonical family name
                    else if (langId == 0x0804) score += 25;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = decoded;
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    public static FontKind ClassifyFontKind(string? rawNameOrFamily)
    {
        if (string.IsNullOrWhiteSpace(rawNameOrFamily)) return FontKind.Hei;
        string s = rawNameOrFamily;
        if (s.Contains("仿宋", StringComparison.OrdinalIgnoreCase)
            || s.Contains("仿", StringComparison.OrdinalIgnoreCase)
            || s.Contains("FangSong", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Fangsong", StringComparison.OrdinalIgnoreCase))
            return FontKind.Fang;
        if (s.Contains("楷", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Kai", StringComparison.OrdinalIgnoreCase))
            return FontKind.Kai;
        if (s.Contains("手", StringComparison.OrdinalIgnoreCase)
            || s.Contains("行", StringComparison.OrdinalIgnoreCase)
            || s.Contains("草", StringComparison.OrdinalIgnoreCase)
            || s.Contains("隶", StringComparison.OrdinalIgnoreCase)
            || s.Contains("LiSu", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Brush", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Script", StringComparison.OrdinalIgnoreCase))
            return FontKind.Hand;
        if (s.Contains("黑", StringComparison.OrdinalIgnoreCase)
            || s.Contains("雅黑", StringComparison.OrdinalIgnoreCase)
            || s.Contains("等线", StringComparison.OrdinalIgnoreCase)
            || s.Contains("圆", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Sans", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Gothic", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Hei", StringComparison.OrdinalIgnoreCase)
            || s.Contains("PingFang", StringComparison.OrdinalIgnoreCase)
            || s.Contains("MiSans", StringComparison.OrdinalIgnoreCase)
            || s.Contains("HarmonyOS", StringComparison.OrdinalIgnoreCase)
            || s.Contains("DengXian", StringComparison.OrdinalIgnoreCase)
            || s.Contains("YouYuan", StringComparison.OrdinalIgnoreCase))
            return FontKind.Hei;
        if (s.Contains("宋", StringComparison.OrdinalIgnoreCase)
            || s.Contains("明", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Serif", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Song", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Ming", StringComparison.OrdinalIgnoreCase)
            || s.Contains("SimSun", StringComparison.OrdinalIgnoreCase))
            return FontKind.Song;
        return FontKind.Hei;
    }

    public FontKind GetFontKind(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return FontKind.Hei;
        if (id == "system") return FontKind.Hei;
        if (id == "serif") return FontKind.Song;
        if (id is "kai" or "wenkai") return FontKind.Kai;
        if (Catalog.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) is { } pack)
            return pack.Kind;
        if (id.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            string fn = id[5..];
            return ClassifyFontKind($"{GetFontFileDisplayName(fn)} {GetFontFileFamily(fn)} {fn}");
        }
        if (id.StartsWith("sys:", StringComparison.OrdinalIgnoreCase))
            return ClassifyFontKind(id[4..]);
        if (id.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
            return ClassifyFontKind(id[6..]);
        return ClassifyFontKind(id);
    }

    public Task<IReadOnlyList<SystemFontEntry>> LoadSystemCjkFontsAsync() =>
        _cachedSystemFonts is { } ready
            ? Task.FromResult(ready)
            : Task.Run(() => DiscoverSystemCjkFonts());

    public IReadOnlyList<SystemFontEntry> DiscoverSystemCjkFonts()
    {
        if (_cachedSystemFonts is { } existing)
            return existing;

        lock (_sysFontsGate)
        {
            if (_cachedSystemFonts is { } lockedHit)
                return lockedHit;

            var scanned = ScanSystemCjkFontsCore();
            _cachedSystemFonts = scanned;
            return scanned;
        }
    }

    private IReadOnlyList<SystemFontEntry> ScanSystemCjkFontsCore()
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
            "CJK", "SC", "TC", "Han", "WenKai", "PingFang", "MiSans", "HarmonyOS", "Sarasa", "Alibaba", "PuHui", "FZ", "HY",
            "宋", "楷", "黑", "仿", "雅黑", "等线", "华文", "方正", "汉仪", "文楷", "思源", "苹方", "鸿蒙", "兰亭", "普惠", "圆", "隶",
        ];

        string winFontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string userFontsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");

        var registryEntries = new List<(string RawName, string? FileValue)>();
        foreach (var rootKey in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = rootKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key is null) continue;
                foreach (string valName in key.GetValueNames())
                {
                    registryEntries.Add((valName, key.GetValue(valName) as string));
                }
            }
            catch { }
        }

        foreach (var (match, display, family) in knownWindowsCjk)
        {
            if (registryEntries.Any(e => e.RawName.Contains(match, StringComparison.OrdinalIgnoreCase)))
                TryAdd(display, family);
        }

        foreach (var (rawName, fileVal) in registryEntries.OrderBy(x => x.RawName, StringComparer.OrdinalIgnoreCase))
        {
            if (rawName.Contains("ExtB", StringComparison.OrdinalIgnoreCase) ||
                rawName.Contains("Symbol", StringComparison.OrdinalIgnoreCase))
                continue;

            string cleaned = CleanRegistryFontFamilyName(rawName);
            if (string.IsNullOrEmpty(cleaned)) continue;
            if (!cjkTokens.Any(tok => cleaned.Contains(tok, StringComparison.OrdinalIgnoreCase))) continue;

            string? resolvedFontPath = null;
            if (!string.IsNullOrWhiteSpace(fileVal))
            {
                if (Path.IsPathRooted(fileVal) && File.Exists(fileVal))
                    resolvedFontPath = fileVal;
                else if (!string.IsNullOrEmpty(winFontsDir) && File.Exists(Path.Combine(winFontsDir, fileVal)))
                    resolvedFontPath = Path.Combine(winFontsDir, fileVal);
                else if (File.Exists(Path.Combine(userFontsDir, fileVal)))
                    resolvedFontPath = Path.Combine(userFontsDir, fileVal);
            }

            string realFamily = (resolvedFontPath is not null ? ReadTrueTypeFamilyName(resolvedFontPath, preferLocalized: false) : null)
                                ?? cleaned;
            realFamily = CleanRegistryFontFamilyName(realFamily);
            if (string.IsNullOrEmpty(realFamily)) continue;

            string localized = (resolvedFontPath is not null ? ReadTrueTypeFamilyName(resolvedFontPath, preferLocalized: true) : null)
                               ?? cleaned;
            localized = CleanRegistryFontFamilyName(localized);
            string display = !string.IsNullOrEmpty(localized) && !string.Equals(localized, realFamily, StringComparison.OrdinalIgnoreCase)
                ? $"{localized} ({realFamily})"
                : realFamily;

            TryAdd(display, realFamily);
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
        if (pack is null || IsPackInstalled(packId)) return;

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            if (_downloading.ContainsKey(packId))
            {
                linkedCts.Dispose();
                return;
            }
            _downloading[packId] = 0.01;
            _downloadCts[packId] = linkedCts;
        }
        Changed?.Invoke();

        var token = linkedCts.Token;
        try
        {
            Directory.CreateDirectory(_fontsDir);
            ulong totalBytes = 0;
            foreach (var f in pack.Files) totalBytes += f.Bytes;
            ulong doneOffset = 0;

            foreach (var file in pack.Files)
            {
                token.ThrowIfCancellationRequested();
                string tmp = Path.Combine(_fontsDir, $".{file.File}.part");
                string dst = Path.Combine(_fontsDir, file.File);
                if (File.Exists(dst))
                {
                    doneOffset += file.Bytes;
                    continue;
                }

                var urls = ModelDownload.CandidateUrls(file.File, officialUrl: file.Url, mirrorPath: $"fonts/{file.File}");
                ulong baseDone = doneOffset;
                long lastEmitTick = 0;
                double lastEmitProg = 0;
                await ModelDownload.DownloadWithResumeAsync(
                    urls,
                    tmp,
                    (long)file.Bytes,
                    expectedBlake3: null,
                    (got, _) =>
                    {
                        if (totalBytes > 0)
                        {
                            double prog = Math.Clamp((double)(baseDone + (ulong)Math.Max(0, got)) / totalBytes, 0.01, 0.99);
                            lock (_gate)
                            {
                                if (_downloading.ContainsKey(packId))
                                    _downloading[packId] = prog;
                            }
                            long nowTick = Environment.TickCount64;
                            if (Math.Abs(prog - lastEmitProg) >= 0.015 && nowTick - lastEmitTick >= 100)
                            {
                                lastEmitProg = prog;
                                lastEmitTick = nowTick;
                                Changed?.Invoke();
                            }
                        }
                    },
                    token);

                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);
                lock (_gate) _familyCache.Remove(file.File);
                if (IsTrueTypeOrOpenTypeFile(file.File))
                    TryRegisterPrivateFont(dst);
                doneOffset += file.Bytes;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cancelled cleanly via CancelDownload(packId)
        }
        finally
        {
            lock (_gate)
            {
                _downloading.Remove(packId);
                if (_downloadCts.TryGetValue(packId, out var existing) && ReferenceEquals(existing, linkedCts))
                    _downloadCts.Remove(packId);
            }
            linkedCts.Dispose();
            Changed?.Invoke();
        }
    }
}
