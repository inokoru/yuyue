using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using Yuyue.Design;

namespace Yuyue.Core;

/// <summary>
/// 1:1 port of <c>Update.swift</c>, <c>MacUpdate.swift</c>, <c>UpdateUI.swift</c>, and <c>Feedback.swift</c>:
/// - Stable &amp; Beta GitHub Releases update feed check via <see cref="YuyueCoreMethods.ParseUpdate"/> / <see cref="YuyueCoreMethods.CompareVersions"/>
/// - Optional in-app Windows installer download with SHA-256 verification and progress dialog
/// - Bundled <c>CHANGELOG.md</c> viewer via <see cref="YuyueCoreMethods.ParseChangelog"/>, Privacy sheet, and Open-Source Licenses sheet
/// - Single-channel AirFeedback frameless sheet composer with persisted draft, up to 4 EXIF-stripped JPEG images (≤ 2048 px, ≤ 8 MB), drag-and-drop &amp; Ctrl+V paste, collapsible device info card, inline error retry, and in-sheet thanks view.
/// </summary>
public sealed class UpdateAndFeedbackService
{
    public static string CurrentVersion { get; } = ResolveAppVersion();
    public static ulong CurrentBuild { get; } = ResolveAppBuild();
    public const string RepoSlug = "inokoru/yuyue";

    private static string ResolveAppVersion()
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v is not null && (v.Major > 0 || v.Minor > 0 || v.Build > 0))
            {
                int build = Math.Max(0, v.Build);
                return $"{v.Major}.{v.Minor}.{build}";
            }
        }
        catch { }
        return "0.1.0";
    }

    private static ulong ResolveAppBuild()
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v is not null && v.Revision > 0) return (ulong)v.Revision;
        }
        catch { }
        return 1;
    }

    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(20) };
    private static readonly HttpClient DownloadHttp = new() { Timeout = System.TimeSpan.FromMinutes(10) };
    private readonly LibraryService _library;

    public UpdateInfo? LatestOffer { get; private set; }
    public string? LatestWinInstallerUrl { get; private set; }
    public string? LatestWinInstallerSha256 { get; private set; }
    public Release[] OfferNotes { get; private set; } = [];
    public event Action? StateChanged;

    public void NotifyStateChanged() => StateChanged?.Invoke();

    public static string ResolvePreferredRepoUrl() =>
        Yuyue.Speech.ModelDownload.PrefersChinaMirror()
            ? $"https://cnb.cool/{RepoSlug}"
            : $"https://github.com/{RepoSlug}";

    public string GetPreferredRepoUrl() => ResolvePreferredRepoUrl();

    public string GetPreferredReleasesUrl(bool latest = false) =>
        Yuyue.Speech.ModelDownload.PrefersChinaMirror()
            ? $"https://cnb.cool/{RepoSlug}/-/releases"
            : (latest ? $"https://github.com/{RepoSlug}/releases/latest" : $"https://github.com/{RepoSlug}/releases");

    public bool IsOfferSuppressed =>
        LatestOffer is not null && !HasUnreadBadge;
    public bool HasUnreadBadge =>
        LatestOffer is not null &&
        LatestOffer.Version != _library.Prefs.UpdateSkippedVersion &&
        !(LatestOffer.Version == _library.Prefs.UpdateRemindVersion &&
          _library.Prefs.UpdateRemindUntilMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public UpdateAndFeedbackService(LibraryService library)
    {
        _library = library;
        if (!Http.DefaultRequestHeaders.UserAgent.Any())
            Http.DefaultRequestHeaders.UserAgent.ParseAdd($"Yuyue/{CurrentVersion} (Windows)");
        if (!DownloadHttp.DefaultRequestHeaders.UserAgent.Any())
            DownloadHttp.DefaultRequestHeaders.UserAgent.ParseAdd($"Yuyue/{CurrentVersion} (Windows)");
    }

    private static string CurrentArchSlug() =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "arm64"
            : "x64";

    internal static (string? Url, string? Sha256) ParseWinInstallerJson(string updateJson, string? arch = null)
    {
        string targetArch = string.IsNullOrWhiteSpace(arch) ? CurrentArchSlug() : arch.Trim().ToLowerInvariant();
        try
        {
            using var doc = JsonDocument.Parse(updateJson);
            var root = doc.RootElement;
            string? url = null, sha = null;
            if (root.TryGetProperty("windows", out var winObj) && winObj.ValueKind == JsonValueKind.Object)
            {
                if (winObj.TryGetProperty(targetArch, out var archObj) && archObj.ValueKind == JsonValueKind.Object)
                {
                    if (archObj.TryGetProperty("url", out var au)) url = au.GetString();
                    if (archObj.TryGetProperty("sha256", out var asha)) sha = asha.GetString();
                }
                if (string.IsNullOrWhiteSpace(url) && winObj.TryGetProperty("url", out var u)) url = u.GetString();
                if (string.IsNullOrWhiteSpace(sha) && winObj.TryGetProperty("sha256", out var s)) sha = s.GetString();
            }
            if (string.IsNullOrWhiteSpace(url) && root.TryGetProperty("win", out var winStr))
            {
                if (winStr.ValueKind == JsonValueKind.String) url = winStr.GetString();
                else if (winStr.ValueKind == JsonValueKind.Object)
                {
                    if (winStr.TryGetProperty("url", out var u)) url = u.GetString();
                    if (winStr.TryGetProperty("sha256", out var s)) sha = s.GetString();
                }
            }
            if (string.IsNullOrWhiteSpace(url) && root.TryGetProperty("winUrl", out var wu)) url = wu.GetString();
            if (string.IsNullOrWhiteSpace(sha) && root.TryGetProperty("winSha256", out var ws)) sha = ws.GetString();
            if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return (url.Trim(), string.IsNullOrWhiteSpace(sha) ? null : sha.Trim().ToLowerInvariant());
        }
        catch { }
        return (null, null);
    }

    internal static string? ParseSha256Sums(string sumsText, string fileName)
    {
        if (string.IsNullOrWhiteSpace(sumsText) || string.IsNullOrWhiteSpace(fileName)) return null;
        foreach (string rawLine in sumsText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length < 66) continue;
            string[] parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            string hash = parts[0].Trim().ToLowerInvariant();
            string name = parts[^1].Trim().TrimStart('*');
            if (hash.Length == 64 && hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) &&
                string.Equals(Path.GetFileName(name), Path.GetFileName(fileName), StringComparison.OrdinalIgnoreCase))
            {
                return hash;
            }
        }
        return null;
    }

    private static async Task<(string? Url, string? Sha256)> TryDiscoverGitHubWinAssetAsync(string version, CancellationToken ct)
    {
        string arch = CurrentArchSlug();
        string tag = version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : "v" + version;
        try
        {
            string json = await Http.GetStringAsync($"https://api.github.com/repos/{RepoSlug}/releases/tags/{tag}", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return (null, null);

            string? setupUrl = null, setupName = null, zipUrl = null, zipName = null, sumsUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                string dl = asset.TryGetProperty("browser_download_url", out var up) ? up.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(name) || !dl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;

                if (name.Contains($"windows-{arch}", StringComparison.OrdinalIgnoreCase) && name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    setupUrl = dl;
                    setupName = name;
                }
                else if (name.Contains($"windows-{arch}", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = dl;
                    zipName = name;
                }
                else if (name.Equals($"SHA256SUMS-windows-{arch}.txt", StringComparison.OrdinalIgnoreCase) ||
                         (sumsUrl is null && name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)))
                {
                    sumsUrl = dl;
                }
            }

            string? chosenUrl = setupUrl ?? zipUrl;
            string? chosenName = setupName ?? zipName;
            if (string.IsNullOrEmpty(chosenUrl) || string.IsNullOrEmpty(chosenName))
                return (null, null);

            string? sha = null;
            if (!string.IsNullOrEmpty(sumsUrl))
            {
                try
                {
                    string sumsText = await Http.GetStringAsync(sumsUrl, ct);
                    sha = ParseSha256Sums(sumsText, chosenName);
                }
                catch { }
            }
            return (chosenUrl, sha);
        }
        catch
        {
            return (null, null);
        }
    }

    public async Task<(bool HasUpdate, UpdateInfo? Info, string Message)> CheckForUpdatesAsync(bool manual = false, CancellationToken ct = default)
    {
        if (!manual && !_library.Prefs.AutoCheckUpdates)
            return (false, null, "自动检查已关闭");

        if (!manual && _library.Prefs.UpdateCheckedBuild == CurrentBuild &&
            _library.Prefs.UpdateLastCheckedMs is long lastMs &&
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastMs < 24L * 3600_000L)
        {
            return (LatestOffer is not null, LatestOffer, "24 小时内已检查过");
        }

        try
        {
            var candidateBases = await ResolveCandidateFeedBasesAsync(_library.Prefs.UpdateChannel == "beta", ct);
            string? updateJson = null;
            Exception? lastErr = null;
            foreach (string baseFeed in candidateBases)
            {
                try
                {
                    updateJson = await Http.GetStringAsync(baseFeed + "update.json", ct);
                    if (!string.IsNullOrWhiteSpace(updateJson)) break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastErr = ex;
                }
            }
            if (string.IsNullOrWhiteSpace(updateJson))
                throw lastErr ?? new HttpRequestException("未能读取 update.json");
            var info = YuyueCoreMethods.ParseUpdate(updateJson);

            _library.Prefs.UpdateLastCheckedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _library.Prefs.UpdateCheckedBuild = CurrentBuild;
            _library.SavePrefs();

            bool newer = info.Code > 0
                ? info.Code > CurrentBuild
                : YuyueCoreMethods.CompareVersions(info.Version, CurrentVersion) > 0;

            if (!newer)
            {
                LatestOffer = null;
                LatestWinInstallerUrl = null;
                LatestWinInstallerSha256 = null;
                OfferNotes = [];
                StateChanged?.Invoke();
                return (false, info, $"当前已是最新版本（v{CurrentVersion}）");
            }

            LatestOffer = info;
            (LatestWinInstallerUrl, LatestWinInstallerSha256) = ParseWinInstallerJson(updateJson);
            if (string.IsNullOrWhiteSpace(LatestWinInstallerUrl))
                (LatestWinInstallerUrl, LatestWinInstallerSha256) = await TryDiscoverGitHubWinAssetAsync(info.Version, ct);
            if (!string.IsNullOrWhiteSpace(info.Changelog))
            {
                try
                {
                    if (info.Changelog.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        string clText = await Http.GetStringAsync(info.Changelog, ct);
                        OfferNotes = YuyueCoreMethods.ReleasesAfter(clText, CurrentVersion);
                    }
                    else
                    {
                        OfferNotes = YuyueCoreMethods.ReleasesAfter(info.Changelog, CurrentVersion);
                    }
                }
                catch { OfferNotes = [YuyueCoreMethods.UpdateRelease(info)]; }
            }
            if (OfferNotes.Length == 0) OfferNotes = [YuyueCoreMethods.UpdateRelease(info)];

            StateChanged?.Invoke();
            return (true, info, $"发现新版本 v{info.Version}");
        }
        catch (Exception ex)
        {
            return (false, null, $"暂时无法连接更新源（当前 v{CurrentVersion}）：{ex.Message}");
        }
    }

    private static async Task<List<string>> ResolveCandidateFeedBasesAsync(bool beta, CancellationToken ct)
    {
        string? overrideBase = Environment.GetEnvironmentVariable("YYUpdateBase");
        if (!string.IsNullOrWhiteSpace(overrideBase))
            return [overrideBase.EndsWith('/') ? overrideBase : overrideBase + "/"];

        string ghLatest = $"https://github.com/{RepoSlug}/releases/latest/download/";
        string cnbLatest = $"https://cnb.cool/{RepoSlug}/-/releases/latest/download/";
        bool preferCN = Yuyue.Speech.ModelDownload.PrefersChinaMirror();

        if (!beta)
            return preferCN ? [cnbLatest, ghLatest] : [ghLatest, cnbLatest];

        var list = new List<string>();
        if (preferCN)
        {
            if (await TryNewestCnbBaseAsync(ct) is string cb) list.Add(cb);
            if (await TryNewestGitHubBaseAsync(ct) is string gb) list.Add(gb);
            list.Add(cnbLatest);
            list.Add(ghLatest);
        }
        else
        {
            if (await TryNewestGitHubBaseAsync(ct) is string gb) list.Add(gb);
            if (await TryNewestCnbBaseAsync(ct) is string cb) list.Add(cb);
            list.Add(ghLatest);
            list.Add(cnbLatest);
        }
        return list.Distinct().ToList();
    }

    private static async Task<string?> TryNewestGitHubBaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await Http.GetStringAsync($"https://api.github.com/repos/{RepoSlug}/releases?per_page=20", ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                string tag = rel.TryGetProperty("tag_name", out var tp) ? tp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(tag)) continue;
                if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    bool hasManifest = assets.EnumerateArray().Any(a =>
                        a.TryGetProperty("name", out var np) && np.GetString() == "update.json");
                    if (hasManifest)
                        return $"https://github.com/{RepoSlug}/releases/download/{tag}/";
                }
            }
        }
        catch { }
        return null;
    }

    private static async Task<string?> TryNewestCnbBaseAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://cnb.cool/{RepoSlug}/-/releases?page=1&page_size=20");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.cnb.web+json");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("releases", out var rels) || rels.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var rel in rels.EnumerateArray())
            {
                if (rel.TryGetProperty("is_draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
                string rawTag = rel.TryGetProperty("tag_ref", out var tp) ? tp.GetString() ?? "" : "";
                string tag = rawTag.StartsWith("refs/tags/", StringComparison.Ordinal) ? rawTag["refs/tags/".Length..] : rawTag;
                if (string.IsNullOrEmpty(tag)) continue;
                if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    bool hasManifest = assets.EnumerateArray().Any(a =>
                        a.TryGetProperty("name", out var np) && np.GetString() == "update.json");
                    if (hasManifest)
                        return $"https://cnb.cool/{RepoSlug}/-/releases/download/{tag}/";
                }
            }
        }
        catch { }
        return null;
    }

    private static string OsDisplayVersion()
    {
        var v = Environment.OSVersion.Version;
        string name = v.Major >= 10 && v.Build >= 22000 ? "Windows 11" : $"Windows {v.Major}";
        return v.Build > 0 ? $"{name} (Build {v.Build})" : name;
    }

    public Release[] LoadBundledChangelog()
    {
        try
        {
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "Assets", "Shared", "CHANGELOG.md"),
                Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "CHANGELOG.md")),
            ];
            string? found = candidates.FirstOrDefault(File.Exists);
            if (found is not null)
            {
                string raw = File.ReadAllText(found, Encoding.UTF8);
                var parsed = YuyueCoreMethods.ParseChangelog(raw);
                if (parsed.Length > 0) return parsed;
            }
        }
        catch { }

        return
        [
            new Release("0.1.0", "2026-11-23",
            [
                "全新桌面端书架、在读封面取色横带与内联书籍档案页",
                "双栏仿真暖纸电子书阅读器、自定义调纸与字样册",
                "本地离线 Kokoro / ZipVoice 24kHz 自然人声、多角色广播剧与声音克隆",
                "书房电台双主播播客、有声书与电子书对齐",
                "个人云盘 / WebDAV 无账号多端同步与本地快照备份",
            ],
            [
                "Desktop library shelf, cover-dyed reading band, and inline book record page",
                "Two-column warm-paper EPUB/MOBI/TXT/PDF reader with custom paper studio",
                "On-device Kokoro / ZipVoice 24kHz neural TTS, multi-role drama, and voice cloning",
                "Private Studio dual-host podcasts and audiobook-to-ebook alignment",
                "Serverless personal cloud / WebDAV sync and portable .yuyue snapshots",
            ]),
        ];
    }

    public async Task ShowUpdateCheckDialogAsync(XamlRoot xamlRoot)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());

        var (hasUpdate, info, msg) = await CheckForUpdatesAsync(manual: true);
        if (!hasUpdate || info is null)
        {
            ContentDialog? upToDateDlg = null;
            bool openReleases = false;
            string releasesBtnText = Yuyue.Speech.ModelDownload.PrefersChinaMirror() ? "查看发布页 (CNB) ↗" : "查看 GitHub Releases ↗";
            var footRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
            footRow.Children.Add(YYChrome.MakeDialogButton(releasesBtnText, () => { openReleases = true; upToDateDlg?.Hide(); }, dark, _library.Prefs.Tint, "secondary", height: 34, horizontalPadding: 14));
            footRow.Children.Add(YYChrome.MakeDialogButton("好", () => upToDateDlg?.Hide(), dark, _library.Prefs.Tint, "dark", height: 34, horizontalPadding: 18));

            var body = new StackPanel
            {
                Spacing = 12,
                Padding = new Thickness(22, 20, 22, 18),
                Children =
                {
                    YYChrome.MakeDialogHeader(info is not null ? "已是最新版本" : "检查更新", null, () => upToDateDlg?.Hide(), dark, titleSize: 16),
                    new TextBlock { Text = msg, FontSize = 13, LineHeight = 20, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"更新渠道：{(_library.Prefs.UpdateChannel == "beta" ? "测试版 (Beta)" : "正式版")}", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3) },
                    footRow,
                },
            };

            upToDateDlg = YYChrome.CreateFluentDialog(xamlRoot, dark, 368, body, tint: _library.Prefs.Tint);
            await upToDateDlg.ShowSafeAsync(_library.Prefs.Tint);
            if (openReleases)
                _ = Launcher.LaunchUriAsync(new Uri(GetPreferredReleasesUrl()));
            return;
        }

        // 440px Bookplate Release Offer Sheet (1:1 MacUpdate.swift)
        ContentDialog? offerDlg = null;
        int action = 0; // 0 = close, 1 = install, 2 = remind later

        var notesStack = new StackPanel { Spacing = 10 };
        foreach (var rel in OfferNotes)
        {
            var rStack = new StackPanel { Spacing = 4 };
            rStack.Children.Add(new TextBlock { Text = $"v{rel.Version} · {rel.Date}", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
            var bullets = L10n.IsEnglish && rel.NotesEn.Length > 0 ? rel.NotesEn : rel.NotesZh;
            foreach (var line in bullets)
                rStack.Children.Add(new TextBlock { Text = "• " + line, FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
            notesStack.Children.Add(rStack);
        }

        var topHead = new Grid();
        var kickerStack = new StackPanel { Spacing = 3 };
        kickerStack.Children.Add(new TextBlock { Text = L10n.T("发现新版本"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
        kickerStack.Children.Add(new TextBlock { Text = $"语阅 v{info.Version}", FontFamily = serif, FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
        kickerStack.Children.Add(new TextBlock { Text = $"当前版本 v{CurrentVersion}", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3) });
        topHead.Children.Add(kickerStack);
        var closeBtn = YYChrome.MakeDialogCloseButton(() => offerDlg?.Hide(), dark);
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.VerticalAlignment = VerticalAlignment.Top;
        topHead.Children.Add(closeBtn);

        var notesCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(16, 12, 16, 12),
            Child = new ScrollViewer { MaxHeight = 240, Content = notesStack },
        };

        string primaryLabel = !string.IsNullOrWhiteSpace(LatestWinInstallerUrl) ? "下载并安装" : "前往下载安装";
        var installBtn = YYChrome.MakeDialogButton(primaryLabel, () => { action = 1; offerDlg?.Hide(); }, dark, _library.Prefs.Tint, "dark", height: 40, stretch: true);

        var subRow = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        var skipBtn = YYChrome.MakeSettingsActionButton("跳过此版本", () =>
        {
            _library.Prefs.UpdateSkippedVersion = info.Version;
            _library.SavePrefs();
            StateChanged?.Invoke();
            offerDlg?.Hide();
        }, dark);
        skipBtn.HorizontalAlignment = HorizontalAlignment.Left;
        subRow.Children.Add(skipBtn);

        var laterBtn = YYChrome.MakeSettingsActionButton("稍后提醒（3 天后）", () => { action = 2; offerDlg?.Hide(); }, dark);
        laterBtn.HorizontalAlignment = HorizontalAlignment.Right;
        subRow.Children.Add(laterBtn);

        var offerBody = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(24, 20, 24, 18),
            Children = { topHead, notesCard, installBtn, subRow },
        };

        offerDlg = YYChrome.CreateFluentDialog(xamlRoot, dark, 440, offerBody, tint: _library.Prefs.Tint);
        await offerDlg.ShowSafeAsync(_library.Prefs.Tint);

        if (action == 1)
        {
            await InstallOfferAsync(xamlRoot, info);
        }
        else if (action == 2)
        {
            _library.Prefs.UpdateRemindVersion = info.Version;
            _library.Prefs.UpdateRemindUntilMs = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeMilliseconds();
            _library.SavePrefs();
            StateChanged?.Invoke();
        }
    }

    public async Task InstallOfferAsync(XamlRoot xamlRoot, UpdateInfo? offerOverride = null)
    {
        var info = offerOverride ?? LatestOffer;
        if (info is null) return;
        if (!string.IsNullOrWhiteSpace(LatestWinInstallerUrl))
        {
            await DownloadAndInstallUpdateAsync(xamlRoot, info, LatestWinInstallerUrl!, LatestWinInstallerSha256);
        }
        else
        {
            string url = !string.IsNullOrWhiteSpace(info.Page) ? info.Page : GetPreferredReleasesUrl(latest: true);
            _ = Launcher.LaunchUriAsync(new Uri(url));
        }
    }

    private async Task DownloadAndInstallUpdateAsync(XamlRoot xamlRoot, UpdateInfo info, string installerUrl, string? expectedSha256)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var cts = new CancellationTokenSource();
        ContentDialog? progDlg = null;

        var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, IsIndeterminate = true, HorizontalAlignment = HorizontalAlignment.Stretch };
        var statusText = new TextBlock { Text = $"正在下载语阅 v{info.Version} 安装包…", FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap };
        var cancelBtn = YYChrome.MakeDialogButton("取消", () => { cts.Cancel(); progDlg?.Hide(); }, dark, _library.Prefs.Tint, "secondary", height: 32, horizontalPadding: 16);
        cancelBtn.HorizontalAlignment = HorizontalAlignment.Right;

        var progBody = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(22, 20, 22, 18),
            Children =
            {
                new TextBlock { Text = L10n.T("下载更新"), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) },
                statusText,
                progressBar,
                cancelBtn,
            },
        };

        progDlg = YYChrome.CreateFluentDialog(xamlRoot, dark, 380, progBody, tint: _library.Prefs.Tint);
        progDlg.Closed += (_, _) => { try { cts.Cancel(); } catch { } };

        string? downloadedPath = null;
        string? errorMsg = null;

        var showTask = progDlg.ShowSafeAsync(_library.Prefs.Tint);
        try
        {
            string updateDir = Path.Combine(Path.GetTempPath(), "YuyueUpdate");
            Directory.CreateDirectory(updateDir);
            string fileName = Path.GetFileName(new Uri(installerUrl).AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = $"Yuyue-{info.Version}-Setup.exe";
            string destPath = Path.Combine(updateDir, fileName);
            string partPath = destPath + ".part";

            using var resp = await DownloadHttp.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var remote = await resp.Content.ReadAsStreamAsync(cts.Token))
            await using (var local = File.Create(partPath))
            {
                byte[] buf = new byte[65536];
                long got = 0;
                long lastUiTick = 0;
                int read;
                while ((read = await remote.ReadAsync(buf, cts.Token)) > 0)
                {
                    await local.WriteAsync(buf.AsMemory(0, read), cts.Token);
                    sha.AppendData(buf, 0, read);
                    got += read;
                    long nowTick = Environment.TickCount64;
                    bool reachedFinal = total is > 0 && got >= total.Value;
                    if (reachedFinal || nowTick - lastUiTick >= 100)
                    {
                        lastUiTick = nowTick;
                        if (total is > 0)
                        {
                            double pct = reachedFinal ? 100.0 : Math.Clamp(got * 100.0 / total.Value, 0, 100);
                            progressBar.IsIndeterminate = false;
                            progressBar.Value = pct;
                            statusText.Text = L10n.T($"正在下载 v{info.Version} · {got / 1048576.0:0.1} / {total.Value / 1048576.0:0.1} MB ({pct:0}%)");
                        }
                        else
                        {
                            statusText.Text = L10n.T($"正在下载 v{info.Version} · {got / 1048576.0:0.1} MB");
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                string actualHex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(actualHex, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(partPath); } catch { }
                    throw new InvalidOperationException("安装包 SHA-256 校验未通过，已取消安装以保护安全。");
                }
            }

            File.Move(partPath, destPath, overwrite: true);
            downloadedPath = destPath;
        }
        catch (OperationCanceledException)
        {
            errorMsg = null;
        }
        catch (Exception ex)
        {
            errorMsg = ex.Message;
        }
        finally
        {
            try { progDlg.Hide(); } catch { }
            try { await showTask; } catch { }
            cts.Dispose();
        }

        if (!string.IsNullOrEmpty(downloadedPath) && File.Exists(downloadedPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(downloadedPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                errorMsg = $"安装包已下载但启动失败：{ex.Message}";
            }
        }

        if (!string.IsNullOrEmpty(errorMsg))
        {
            bool openPage = await YYChrome.ShowConfirmAsync(
                xamlRoot,
                dark,
                "更新下载失败",
                errorMsg,
                confirmText: "改为打开下载页 ↗",
                cancelText: "好",
                tint: _library.Prefs.Tint);
            if (openPage)
            {
                string url = !string.IsNullOrWhiteSpace(info.Page) ? info.Page : GetPreferredReleasesUrl(latest: true);
                _ = Launcher.LaunchUriAsync(new Uri(url));
            }
        }
    }

    /// <summary>1-based release ordinal (oldest = 1) for <paramref name="version"/> in a newest-first changelog.</summary>
    public static int? ResolveEdition(string version, Release[] releases)
    {
        if (releases.Length == 0) return null;
        for (int i = 0; i < releases.Length; i++)
        {
            if (releases[i].Version == version) return releases.Length - i;
        }
        if (YuyueCoreMethods.CompareVersions(version, releases[0].Version) > 0)
            return releases.Length + 1;
        return null;
    }

    public async Task ShowChangelogSheetAsync(XamlRoot xamlRoot)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        var mono = new FontFamily(YYFontFamily.Mono.WindowsFamily());
        ContentDialog? dlgRef = null;

        var releases = LoadBundledChangelog();
        var stack = new StackPanel { Spacing = 12 };
        for (int i = 0; i < releases.Length; i++)
        {
            var r = releases[i];
            int edition = releases.Length - i;
            bool isCurrent = string.Equals(r.Version, CurrentVersion, StringComparison.OrdinalIgnoreCase);

            var card = new StackPanel { Spacing = 6 };
            var head = new Grid();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(new TextBlock { Text = L10n.T($"第 {edition} 版"), FontFamily = serif, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
            titleRow.Children.Add(new TextBlock { Text = $"v{r.Version}", FontFamily = mono, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
            if (isCurrent)
            {
                titleRow.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(5),
                    Background = ThemeHelper.Brush(tint.Soft),
                    BorderBrush = ThemeHelper.Brush(tint.Edge),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(6, 1, 6, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = L10n.T("当前"), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) },
                });
            }
            head.Children.Add(titleRow);
            head.Children.Add(new TextBlock { Text = r.Date, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            card.Children.Add(head);

            var lines = L10n.IsEnglish && r.NotesEn.Length > 0 ? r.NotesEn : r.NotesZh;
            foreach (var item in lines)
                card.Children.Add(new TextBlock { Text = "• " + item, FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap, LineHeight = 20 });

            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(isCurrent ? tint.Edge : p.Line),
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(16, 13, 16, 13),
                Child = card,
            });
        }

        var foot = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        string relLabel = Yuyue.Speech.ModelDownload.PrefersChinaMirror() ? "CNB Releases ↗" : "GitHub Releases ↗";
        var ghBtn = YYChrome.MakeSettingsActionButton(relLabel, () => _ = Launcher.LaunchUriAsync(new Uri(GetPreferredReleasesUrl())), dark);
        ghBtn.HorizontalAlignment = HorizontalAlignment.Left;
        foot.Children.Add(ghBtn);
        var okBtn = YYChrome.MakeDialogButton("好", () => dlgRef?.Hide(), dark, _library.Prefs.Tint, "dark", height: 34, horizontalPadding: 20);
        okBtn.HorizontalAlignment = HorizontalAlignment.Right;
        foot.Children.Add(okBtn);

        var sheetBody = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(24, 20, 24, 18),
            Children =
            {
                YYChrome.MakeDialogHeader("更新日志", "每一版改了什么", () => dlgRef?.Hide(), dark, titleSize: 18, serifTitle: true),
                new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) },
                new ScrollViewer { MaxHeight = 440, Content = stack },
                foot,
            },
        };

        dlgRef = YYChrome.CreateFluentDialog(xamlRoot, dark, 540, sheetBody, tint: _library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
    }

    public static async Task ShowPrivacySheetAsync(XamlRoot xamlRoot)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = App.CurrentApp?.Library?.Prefs.Tint ?? YYTints.Default;
        ContentDialog? dlgRef = null;

        (string Title, string Body)[] items =
        [
            ("没有账号，也没有广告", "语阅没有注册登录、广告或跨应用追踪。你的书库、阅读进度、划线想法与生词全部保存在本机目录中。为了解功能使用情况，应用默认开启匿名使用统计，仅记录功能使用形态，不关联个人身份，绝不收集书名、笔记、划线或搜索词，可随时在「关于」页关闭。"),
            ("连接外部服务时直连服务商", "使用免费在线语音时，当前要读的句子会直接发给在线语音服务；当你主动配置 WebDAV 云盘、AI 书伴（DeepSeek / OpenAI / Claude / Ollama 等）、云端语音或 Notion / flomo / 新枝笔记导出时，客户端直接向你指定的服务地址发起请求，API Key 使用 Windows DPAPI 加密存储在本机，从不经过语阅中转，也从不写入跨端同步包。"),
            ("检查更新与模型下载", "检查更新与按需下载开源字体、离线语音模型、声音包时，仅向 GitHub Releases、魔搭（ModelScope）或 Hugging Face 发起普通 HTTPS GET 请求（字体预览按需访问 Google Fonts），不携带任何书库或设备标识。"),
            ("问题反馈", "只有在你主动提交反馈时才会联网，正文和截图仅发给开发者，不会公开，也不收集邮箱。"),
        ];

        var groupStack = new StackPanel { Spacing = 0 };
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0) groupStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(16, 0, 16, 0) });
            var (t, b) = items[i];
            var itemSp = new StackPanel { Spacing = 4, Padding = new Thickness(16, 13, 16, 13) };
            itemSp.Children.Add(new TextBlock { Text = L10n.T(t), FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            itemSp.Children.Add(new TextBlock { Text = L10n.T(b), FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap, LineHeight = 19 });
            groupStack.Children.Add(itemSp);
        }

        var groupCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = groupStack,
        };

        Button? copyDiagBtn = null;
        copyDiagBtn = YYChrome.MakeDialogButton("复制诊断摘要", () =>
        {
            string summary = $"语阅 {CurrentVersion} ({CurrentBuild})\n系统：{OsDisplayVersion()}\n架构：{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
            var dp = new DataPackage();
            dp.SetText(summary);
            Clipboard.SetContent(dp);
            if (copyDiagBtn?.Content is TextBlock tb)
                tb.Text = L10n.T("✓ 已复制诊断摘要");
        }, dark, tint, "secondary", height: 34, horizontalPadding: 14);
        copyDiagBtn.HorizontalAlignment = HorizontalAlignment.Left;

        var okBtn = YYChrome.MakeDialogButton("好", () => dlgRef?.Hide(), dark, tint, "dark", height: 34, horizontalPadding: 20);
        okBtn.HorizontalAlignment = HorizontalAlignment.Right;

        var foot = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        foot.Children.Add(copyDiagBtn);
        foot.Children.Add(okBtn);

        var sheetBody = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(24, 20, 24, 18),
            Children =
            {
                YYChrome.MakeDialogHeader("隐私说明", "书库仅存本机，不含身份与广告", () => dlgRef?.Hide(), dark, titleSize: 18, serifTitle: true),
                new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) },
                new ScrollViewer { MaxHeight = 430, Content = groupCard },
                foot,
            },
        };

        dlgRef = YYChrome.CreateFluentDialog(xamlRoot, dark, 540, sheetBody, tint: tint);
        await dlgRef.ShowSafeAsync(tint);
    }

    public static async Task ShowLicensesSheetAsync(XamlRoot xamlRoot)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = App.CurrentApp?.Library?.Prefs.Tint ?? YYTints.Default;
        var tc = ThemeHelper.TintColors(tint, dark);
        ContentDialog? dlgRef = null;

        Border BuildDepGroup((string Name, string Lic, string Desc)[] rows)
        {
            var sp = new StackPanel { Spacing = 0 };
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) sp.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
                var (n, l, d) = rows[i];
                var r = new StackPanel { Spacing = 2, Padding = new Thickness(14, 10, 14, 10) };
                var top = new Grid();
                top.Children.Add(new TextBlock { Text = n, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                top.Children.Add(new TextBlock { Text = l, FontSize = 11, Foreground = ThemeHelper.Brush(tc.Accent), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
                r.Children.Add(top);
                r.Children.Add(new TextBlock { Text = L10n.T(d), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap });
                sp.Children.Add(r);
            }
            return new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                Child = sp,
            };
        }

        var gplCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(tc.Soft),
            BorderBrush = ThemeHelper.Brush(tc.Edge),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(16, 12, 16, 12),
            Child = new TextBlock
            {
                Text = L10n.T("语阅（Yuyue）基于 GNU General Public License v3.0 (GPL-3.0) 开源发布。"),
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
                TextWrapping = TextWrapping.Wrap,
            },
        };

        var coreDeps = BuildDepGroup(
        [
            ("yuyue-core (Rust)", "GPL-3.0", "跨端统一排版解析、全文检索、同步日志、有声对齐与播客引擎"),
            ("SQLite / rusqlite", "Public Domain / MIT", "本地资料库与全文索引存储"),
            ("UniFFI (Mozilla)", "MPL-2.0", "Rust ↔ Swift / Kotlin / C# 跨语言绑定生成"),
            ("ONNX Runtime (Microsoft)", "MIT", "本地 Kokoro / ZipVoice / 对齐等离线模型推理"),
            ("PdfPig", "Apache-2.0", "Windows 端 PDF 文档解析与文字提取"),
            ("Windows App SDK / WinUI 3", "MIT", "Windows 原生桌面界面与 WebView2 宿主"),
            ("霞鹜文楷 (LXGW WenKai)", "OFL-1.1", "开源中文字体排印支持"),
            ("ECDICT (skywind3000)", "MIT", "内置离线英汉词典与词频分级数据"),
        ]);

        var modelDeps = BuildDepGroup(
        [
            ("Kokoro-82M-v1.1-zh", "Apache-2.0", "按需下载的本地中英离线旁白语音模型"),
            ("ZipVoice · Meta MMS · Vocos", "CC-BY-NC 4.0 · MIT", "按需下载的多角色语音、对齐模型与声码器"),
            ("3D-Speaker CAM++", "Apache-2.0", "按需下载的本地声纹识别模型"),
        ]);

        var scrollContent = new StackPanel { Spacing = 12 };
        scrollContent.Children.Add(gplCard);
        scrollContent.Children.Add(new TextBlock { Text = L10n.T("开源组件"), FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(4, 2, 0, -4) });
        scrollContent.Children.Add(coreDeps);
        scrollContent.Children.Add(new TextBlock { Text = L10n.T("声音与按需下载模型"), FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(4, 2, 0, -4) });
        scrollContent.Children.Add(modelDeps);

        var foot = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        string repoUrl = ResolvePreferredRepoUrl();
        string repoHost = repoUrl.Contains("cnb.cool", StringComparison.OrdinalIgnoreCase) ? "CNB" : "GitHub";
        var srcBtn = YYChrome.MakeSettingsActionButton($"查看源代码 ({repoHost}) ↗", () => _ = Launcher.LaunchUriAsync(new Uri(repoUrl)), dark);
        srcBtn.HorizontalAlignment = HorizontalAlignment.Left;
        foot.Children.Add(srcBtn);
        var okBtn = YYChrome.MakeDialogButton("好", () => dlgRef?.Hide(), dark, tint, "dark", height: 34, horizontalPadding: 20);
        okBtn.HorizontalAlignment = HorizontalAlignment.Right;
        foot.Children.Add(okBtn);

        var sheetBody = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(24, 20, 24, 18),
            Children =
            {
                YYChrome.MakeDialogHeader("开源许可与致谢", "GPL-3.0 · NOTICE", () => dlgRef?.Hide(), dark, titleSize: 18, serifTitle: true),
                new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) },
                new ScrollViewer { MaxHeight = 440, Content = scrollContent },
                foot,
            },
        };

        dlgRef = YYChrome.CreateFluentDialog(xamlRoot, dark, 560, sheetBody, tint: tint);
        await dlgRef.ShowSafeAsync(tint);
    }

    // ---------- Single-Channel AirFeedback Frameless Sheet (v6 spec: text + up to 4 EXIF-stripped JPEG images + collapsible device info) ----------

    public async Task ShowFeedbackComposerAsync(Window ownerWindow, XamlRoot xamlRoot)
    {
        _library.Analytics.TrackScreen("Feedback");

        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);

        string draftTempDir = Path.Combine(_library.DataDir, "cache", "feedback-drafts");
        Directory.CreateDirectory(draftTempDir);
        string fullDraftTempPrefix = Path.GetFullPath(draftTempDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        string osDisplay = OsDisplayVersion();
        string deviceModelStr = $"Windows ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
        string versionDetailStr = $"{CurrentVersion} ({CurrentBuild})";
        string localeStr = System.Globalization.CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrWhiteSpace(localeStr)) localeStr = "zh-CN";

        string SaveTempDraftAttachment(string fileName, byte[] data)
        {
            Directory.CreateDirectory(draftTempDir);
            string safe = string.Concat(fileName.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrEmpty(safe)) safe = "image.jpg";
            string dst = Path.Combine(draftTempDir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid().ToString("N")[..6]}-{safe}");
            File.WriteAllBytes(dst, data);
            return dst;
        }

        void DeleteOwnedDraftTempIfAny(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            try
            {
                string full = Path.GetFullPath(filePath);
                if (full.StartsWith(fullDraftTempPrefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                    File.Delete(full);
            }
            catch { }
        }

        static async Task LoadThumbnailAsync(Image target, byte[] jpegBytes)
        {
            try
            {
                using var ms = new InMemoryRandomAccessStream();
                await ms.WriteAsync(jpegBytes.AsBuffer());
                ms.Seek(0);
                var bmp = new BitmapImage { DecodePixelWidth = 128 };
                await bmp.SetSourceAsync(ms);
                target.Source = bmp;
            }
            catch { }
        }

        bool withDeviceInfo = _library.Prefs.FeedbackAttachInfo;
        bool deviceExpanded = false;
        bool isSending = false;
        bool captureRequested = false;
        var attachments = new List<(string Name, byte[] Data, string? SourcePath)>();

        void ClearPersistedDraft()
        {
            foreach (string saved in _library.Prefs.FeedbackDraftFiles.ToArray())
                DeleteOwnedDraftTempIfAny(saved);
            foreach (var att in attachments)
                DeleteOwnedDraftTempIfAny(att.SourcePath);
            _library.Prefs.FeedbackDraftText = "";
            _library.Prefs.FeedbackDraftFiles.Clear();
            _library.SavePrefs();
        }

        // Restore persisted draft images if still present on disk
        foreach (string savedPath in _library.Prefs.FeedbackDraftFiles.Take(AirFeedbackClient.MaxImages).ToArray())
        {
            if (!File.Exists(savedPath)) continue;
            try
            {
                var fi = new FileInfo(savedPath);
                string ext = fi.Extension.ToLowerInvariant();
                if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")) continue;
                if (fi.Length == 0 || fi.Length > 32_000_000) continue;

                byte[] raw = await File.ReadAllBytesAsync(savedPath);
                bool isOwnedTemp = Path.GetFullPath(savedPath).StartsWith(fullDraftTempPrefix, StringComparison.OrdinalIgnoreCase);
                bool alreadyValidJpeg = isOwnedTemp
                    && raw.Length > 2 && raw[0] == 0xFF && raw[1] == 0xD8
                    && raw.Length <= AirFeedbackClient.MaxImageBytes;
                byte[] jpeg = alreadyValidJpeg ? raw : await SanitizeImageBytesToJpegAsync(raw);
                if (jpeg.Length is > 0 and <= AirFeedbackClient.MaxImageBytes)
                {
                    string displayName = fi.Name;
                    if (isOwnedTemp)
                    {
                        string[] parts = fi.Name.Split('-', 3);
                        if (parts.Length == 3 && long.TryParse(parts[0], out _) && parts[1].Length == 6)
                            displayName = parts[2];
                    }
                    attachments.Add((Path.ChangeExtension(displayName, ".jpg"), jpeg, savedPath));
                }
            }
            catch { }
        }

        TextBox? bodyBoxHolder = null;
        string CurrentBodyText() => bodyBoxHolder?.Text ?? _library.Prefs.FeedbackDraftText;

        void SaveDraftState()
        {
            _library.Prefs.FeedbackAttachInfo = withDeviceInfo;
            _library.Prefs.FeedbackDraftText = CurrentBodyText();
            _library.Prefs.FeedbackDraftFiles = attachments
                .Where(a => !string.IsNullOrEmpty(a.SourcePath))
                .Select(a => a.SourcePath!)
                .Take(AirFeedbackClient.MaxImages)
                .ToList();
            _library.SavePrefs();
        }

        string initialBody = AirFeedbackClient.ClipMessage(_library.Prefs.FeedbackDraftText);
        var bodyBox = new TextBox
        {
            Text = initialBody,
            PlaceholderText = L10n.T("遇到什么问题、想要什么功能，或哪本书打开不对，都可以直接写"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 128,
            MaxHeight = 220,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 13.5,
            Padding = new Thickness(0, 4, 0, 4),
        };
        YYChrome.StripTextBoxChrome(bodyBox);
        AutomationProperties.SetName(bodyBox, L10n.T("反馈正文"));
        bodyBoxHolder = bodyBox;

        var counterText = L10n.Skip(new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        });

        var imageHintText = L10n.Skip(new TextBlock
        {
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var attachList = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Visibility = Visibility.Collapsed,
        };

        var errorBannerText = L10n.Skip(new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.Danger),
            TextWrapping = TextWrapping.Wrap,
        });
        var errorBanner = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(Windows.UI.Color.FromArgb(dark ? (byte)36 : (byte)22, p.Danger.R, p.Danger.G, p.Danger.B)),
            BorderBrush = ThemeHelper.Brush(Windows.UI.Color.FromArgb(72, p.Danger.R, p.Danger.G, p.Danger.B)),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(12, 8, 12, 8),
            Visibility = Visibility.Collapsed,
            Child = errorBannerText,
        };

        void ShowError(string? err)
        {
            if (string.IsNullOrWhiteSpace(err))
            {
                errorBannerText.Text = "";
                errorBanner.Visibility = Visibility.Collapsed;
            }
            else
            {
                errorBannerText.Text = err;
                errorBanner.Visibility = Visibility.Visible;
            }
        }

        ContentDialog? activeDlg = null;
        Button? sendBtn = null;
        Button? cancelBtn = null;
        Button? addImageBtn = null;
        Button? snapWindowBtn = null;
        StackPanel? draftBar = null;

        void SyncButtonsAndDraftBar()
        {
            bool canSend = AirFeedbackClient.CanSubmit(bodyBox.Text, attachments.Count, isSending);
            if (sendBtn is not null)
            {
                sendBtn.IsEnabled = canSend;
                sendBtn.Opacity = canSend ? 1.0 : 0.42;
            }
            if (cancelBtn is not null)
            {
                cancelBtn.IsEnabled = !isSending;
                cancelBtn.Opacity = isSending ? 0.5 : 1.0;
            }
            if (addImageBtn is not null)
            {
                bool canAdd = !isSending && attachments.Count < AirFeedbackClient.MaxImages;
                addImageBtn.IsEnabled = canAdd;
                addImageBtn.Opacity = canAdd ? 1.0 : 0.45;
            }
            if (snapWindowBtn is not null)
            {
                bool canSnap = !isSending && attachments.Count < AirFeedbackClient.MaxImages;
                snapWindowBtn.IsEnabled = canSnap;
                snapWindowBtn.Opacity = canSnap ? 1.0 : 0.45;
            }
            if (draftBar is not null)
            {
                bool hasDraft = !isSending && (!string.IsNullOrWhiteSpace(bodyBox.Text) || attachments.Count > 0);
                draftBar.Visibility = hasDraft ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        void SyncCounter()
        {
            int count = AirFeedbackClient.CodePointLength(bodyBox.Text);
            if (count >= 3600)
            {
                counterText.Text = $"{count} / {AirFeedbackClient.MaxMessageLength}";
                counterText.Foreground = ThemeHelper.Brush(count >= 3900 ? p.Danger : p.T3);
                counterText.Visibility = Visibility.Visible;
            }
            else
            {
                counterText.Text = "";
                counterText.Visibility = Visibility.Collapsed;
            }
            SyncButtonsAndDraftBar();
        }

        void RebuildAttachments()
        {
            attachList.Children.Clear();
            attachList.Visibility = attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            imageHintText.Text = attachments.Count == 0
                ? L10n.T("最多 4 张图")
                : L10n.T($"图片 {attachments.Count}/4");

            for (int i = 0; i < attachments.Count; i++)
            {
                int idx = i;
                var att = attachments[i];

                var img = new Image
                {
                    Width = 56,
                    Height = 56,
                    Stretch = Stretch.UniformToFill,
                };
                _ = LoadThumbnailAsync(img, att.Data);

                var thumbBorder = new Border
                {
                    Width = 56,
                    Height = 56,
                    CornerRadius = new CornerRadius(10),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Background = ThemeHelper.Brush(p.Inset),
                    Child = img,
                };

                var removeBtn = new Button
                {
                    Content = "×",
                    Width = 18,
                    Height = 18,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(9),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Colors.White),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(185, 24, 24, 24)),
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, -5, -5, 0),
                    IsEnabled = !isSending,
                    UseSystemFocusVisuals = false,
                };
                ToolTipService.SetToolTip(removeBtn, L10n.T("移除图片"));
                AutomationProperties.SetName(removeBtn, L10n.T("移除图片"));
                removeBtn.Click += (_, _) =>
                {
                    if (isSending) return;
                    DeleteOwnedDraftTempIfAny(attachments[idx].SourcePath);
                    attachments.RemoveAt(idx);
                    ShowError(null);
                    RebuildAttachments();
                    SaveDraftState();
                };

                var tile = new Grid { Width = 62, Height = 62, Padding = new Thickness(0, 5, 5, 0) };
                tile.Children.Add(thumbBorder);
                tile.Children.Add(removeBtn);
                attachList.Children.Add(tile);
            }

            L10n.ApplyTree(attachList);
            SyncButtonsAndDraftBar();
        }

        async Task TryAttachImageFileAsync(StorageFile f)
        {
            if (isSending) return;
            if (attachments.Count >= AirFeedbackClient.MaxImages)
            {
                ShowError(L10n.T("最多只能添加 4 张图片。"));
                return;
            }
            string ext = Path.GetExtension(f.Name).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp"))
            {
                ShowError(L10n.T("只支持图片或截图，不支持上传普通文件。"));
                return;
            }

            byte[] jpeg = await SanitizeImageToJpegAsync(f);
            if (jpeg.Length == 0)
            {
                ShowError(L10n.T("无法读取该图片，请换一张常见格式的图片。"));
                return;
            }
            if (jpeg.Length > AirFeedbackClient.MaxImageBytes)
            {
                ShowError(L10n.T("图片压缩后仍超过 8 MB 上限。"));
                return;
            }

            string cleanName = Path.ChangeExtension(f.Name, ".jpg");
            string draftPath = SaveTempDraftAttachment(cleanName, jpeg);
            ShowError(null);
            attachments.Add((cleanName, jpeg, draftPath));
            RebuildAttachments();
            SaveDraftState();
        }

        bodyBox.TextChanged += (_, _) =>
        {
            string clipped = AirFeedbackClient.ClipMessage(bodyBox.Text);
            if (!string.Equals(clipped, bodyBox.Text, StringComparison.Ordinal))
            {
                bodyBox.Text = clipped;
                bodyBox.SelectionStart = clipped.Length;
            }
            _library.Prefs.FeedbackDraftText = bodyBox.Text;
            SyncCounter();
        };

        StackPanel MakeAttachIconLabel(string iconKey, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(YYIcons.Make(iconKey, 11.5, ThemeHelper.Brush(p.T2)));
            sp.Children.Add(new TextBlock { Text = L10n.T(text), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        addImageBtn = YYChrome.MakeDialogButton(MakeAttachIconLabel("photo", "图片"), async () =>
        {
            if (isSending) return;
            if (attachments.Count >= AirFeedbackClient.MaxImages)
            {
                ShowError(L10n.T("最多只能添加 4 张图片。"));
                return;
            }
            var pickedPaths = await NativePickerHelper.PickMultipleFilesAsync(ownerWindow, [".png", ".jpg", ".jpeg", ".webp", ".bmp"]);
            if (pickedPaths.Count == 0) return;
            foreach (var pickedPath in pickedPaths)
            {
                if (attachments.Count >= AirFeedbackClient.MaxImages) break;
                try
                {
                    var f = await Windows.Storage.StorageFile.GetFileFromPathAsync(pickedPath);
                    await TryAttachImageFileAsync(f);
                }
                catch
                {
                    ShowError(L10n.T("无法读取该图片，请换一张常见格式的图片。"));
                }
            }
        }, dark, _library.Prefs.Tint, "secondary", height: 26, horizontalPadding: 10);

        snapWindowBtn = YYChrome.MakeDialogButton(MakeAttachIconLabel("camera", "截图"), () =>
        {
            if (isSending) return;
            if (attachments.Count >= AirFeedbackClient.MaxImages)
            {
                ShowError(L10n.T("最多只能添加 4 张图片。"));
                return;
            }
            if (ownerWindow.Content is not UIElement) return;
            SaveDraftState();
            captureRequested = true;
            activeDlg?.Hide();
        }, dark, _library.Prefs.Tint, "secondary", height: 26, horizontalPadding: 10);

        // Unified multiline editor + thumbnail strip + bottom image toolbar card
        var bodyToolbar = new Grid { Padding = new Thickness(0, 8, 0, 2) };
        bodyToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bodyToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bodyToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        leftTools.Children.Add(addImageBtn);
        leftTools.Children.Add(snapWindowBtn);
        bodyToolbar.Children.Add(leftTools);

        var rightMeta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        rightMeta.Children.Add(imageHintText);
        rightMeta.Children.Add(counterText);
        Grid.SetColumn(rightMeta, 2);
        bodyToolbar.Children.Add(rightMeta);

        var bodyStack = new StackPanel { Spacing = 8, Padding = new Thickness(13, 8, 13, 8) };
        bodyStack.Children.Add(bodyBox);
        bodyStack.Children.Add(attachList);
        bodyStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
        bodyStack.Children.Add(bodyToolbar);
        var bodyGroupCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = bodyStack,
        };

        // Collapsible 附带设备信息 card (default ON, collapsed by default)
        string summaryOnText = $"{L10n.T("语阅")} {CurrentVersion} · {osDisplay} · Windows";
        var chevronPresenter = new ContentControl
        {
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = withDeviceInfo ? Visibility.Visible : Visibility.Collapsed,
            Content = YYIcons.Make("chevron-down", 10, ThemeHelper.Brush(p.T3)),
        };
        var infoSubText = L10n.Skip(new TextBlock
        {
            Text = withDeviceInfo ? summaryOnText : L10n.T("本次不附带"),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var deviceToggle = new ToggleSwitch
        {
            IsOn = withDeviceInfo,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(deviceToggle, L10n.T("附带设备信息"));

        var detailGrid = new Grid { ColumnSpacing = 14, RowSpacing = 8 };
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        (string Key, string Val)[] envSpecs =
        [
            ("版本", versionDetailStr),
            ("系统", osDisplay),
            ("设备", deviceModelStr),
            ("语言", localeStr),
        ];
        for (int i = 0; i < envSpecs.Length; i++)
        {
            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(new TextBlock { Text = L10n.T(envSpecs[i].Key), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
            var vTb = new TextBlock { Text = envSpecs[i].Val, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T1), TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
            L10n.Skip(vTb);
            sp.Children.Add(vTb);
            Grid.SetRow(sp, i / 2);
            Grid.SetColumn(sp, i % 2);
            detailGrid.Children.Add(sp);
        }

        var deviceDetailPanel = new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(13, 0, 13, 10),
            Visibility = Visibility.Collapsed,
            Children =
            {
                new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 0, 0, 4) },
                detailGrid,
            },
        };

        var devHeadTitleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        devHeadTitleRow.Children.Add(new TextBlock { Text = L10n.T("附带设备信息"), FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
        devHeadTitleRow.Children.Add(chevronPresenter);

        var devHeadStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        devHeadStack.Children.Add(devHeadTitleRow);
        devHeadStack.Children.Add(infoSubText);

        var expandBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(13, 8, 8, 8),
            MinHeight = 44,
            IsEnabled = withDeviceInfo,
            UseSystemFocusVisuals = false,
            Content = devHeadStack,
        };
        expandBtn.Click += (_, _) =>
        {
            if (!withDeviceInfo) return;
            deviceExpanded = !deviceExpanded;
            deviceDetailPanel.Visibility = deviceExpanded ? Visibility.Visible : Visibility.Collapsed;
            chevronPresenter.Content = YYIcons.Make(deviceExpanded ? "chevron-up" : "chevron-down", 10, ThemeHelper.Brush(p.T3));
        };
        deviceToggle.Toggled += (_, _) =>
        {
            withDeviceInfo = deviceToggle.IsOn;
            if (!withDeviceInfo)
            {
                deviceExpanded = false;
                deviceDetailPanel.Visibility = Visibility.Collapsed;
                chevronPresenter.Content = YYIcons.Make("chevron-down", 10, ThemeHelper.Brush(p.T3));
            }
            expandBtn.IsEnabled = withDeviceInfo;
            chevronPresenter.Visibility = withDeviceInfo ? Visibility.Visible : Visibility.Collapsed;
            infoSubText.Text = withDeviceInfo ? summaryOnText : L10n.T("本次不附带");
            SaveDraftState();
        };

        var devHeaderGrid = new Grid();
        devHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        devHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        devHeaderGrid.Children.Add(expandBtn);
        var swWrap = new Border { Padding = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Child = deviceToggle };
        Grid.SetColumn(swWrap, 1);
        devHeaderGrid.Children.Add(swWrap);

        var deviceCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = new StackPanel { Children = { devHeaderGrid, deviceDetailPanel } },
        };

        // Direct mail row (no email input collected in form)
        var mailRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        mailRow.Children.Add(new TextBlock { Text = L10n.T("需要回复？"), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        mailRow.Children.Add(new HyperlinkButton
        {
            Content = L10n.T("直接发邮件 ↗"),
            NavigateUri = new Uri("mailto:yuyue.app@icloud.com"),
            FontSize = 11.5,
            Padding = new Thickness(2, 0, 2, 0),
            MinHeight = 0,
            MinWidth = 0,
        });

        // Privacy indicator row (green lock + private notice)
        var privacyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        privacyRow.Children.Add(YYIcons.Make("lock", 12, ThemeHelper.Brush(p.Ok)));
        privacyRow.Children.Add(new TextBlock
        {
            Text = L10n.T("仅发给开发者，不会公开"),
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
        });

        void ResetComposerToEmpty()
        {
            if (isSending) return;
            bodyBox.Text = "";
            ClearPersistedDraft();
            attachments.Clear();
            ShowError(null);
            RebuildAttachments();
            SyncCounter();
            SaveDraftState();
        }

        var clearDraftBtn = YYChrome.MakeSettingsActionButton(L10n.T("清空"), ResetComposerToEmpty, dark);
        clearDraftBtn.FontSize = 11.5;

        draftBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Children =
            {
                new TextBlock { Text = L10n.T("草稿已自动保存"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "·", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center },
                clearDraftBtn,
            },
        };

        var outerSwitchHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        StackPanel? composerRoot = null;

        StackPanel BuildThanksView()
        {
            var checkCircle = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(28),
                Background = ThemeHelper.Brush(Windows.UI.Color.FromArgb(dark ? (byte)38 : (byte)26, 45, 190, 96)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 4),
                Child = YYIcons.Make("checkmark", 22, ThemeHelper.Brush(p.Ok)),
            };

            var thanksBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            thanksBtns.Children.Add(YYChrome.MakeDialogButton("再写一条", () =>
            {
                ResetComposerToEmpty();
                outerSwitchHost.Content = composerRoot;
                L10n.ApplyTree(outerSwitchHost);
                bodyBox.Focus(FocusState.Programmatic);
            }, dark, _library.Prefs.Tint, "secondary", height: 36, horizontalPadding: 18));
            thanksBtns.Children.Add(YYChrome.MakeDialogButton("完成", () => activeDlg?.Hide(), dark, _library.Prefs.Tint, "dark", height: 36, horizontalPadding: 22));

            var thanksStack = new StackPanel
            {
                Spacing = 12,
                Padding = new Thickness(28, 28, 28, 24),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children =
                {
                    checkCircle,
                    new TextBlock
                    {
                        Text = L10n.T("已发给开发者，谢谢"),
                        FontSize = 19,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = ThemeHelper.Brush(p.T1),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = L10n.T("这条反馈仅开发者可见，不会公开。"),
                        FontSize = 13,
                        Foreground = ThemeHelper.Brush(p.T2),
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new HyperlinkButton
                    {
                        Content = L10n.T("如需回复，可发邮件至 yuyue.app@icloud.com"),
                        NavigateUri = new Uri("mailto:yuyue.app@icloud.com"),
                        FontSize = 12,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    thanksBtns,
                },
            };
            L10n.ApplyTree(thanksStack);
            return thanksStack;
        }

        async Task SubmitFeedbackNowAsync()
        {
            if (!AirFeedbackClient.CanSubmit(bodyBox.Text, attachments.Count, isSending)) return;

            isSending = true;
            ShowError(null);
            if (sendBtn?.Content is TextBlock stb) stb.Text = L10n.T("正在发送…");
            bodyBox.IsEnabled = false;
            deviceToggle.IsEnabled = false;
            RebuildAttachments();
            SaveDraftState();

            try
            {
                AirFeedbackClient.DeviceInfo? deviceInfo = withDeviceInfo
                    ? new AirFeedbackClient.DeviceInfo(
                        AppVersion: CurrentVersion,
                        AppBuild: CurrentBuild.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        DeviceModel: deviceModelStr,
                        OsVersion: osDisplay,
                        Locale: localeStr)
                    : null;

                var imageBytes = attachments.Select(a => a.Data).ToList();
                var sendResult = await AirFeedbackClient.SendAsync(bodyBox.Text, deviceInfo, imageBytes);

                if (sendResult.Success)
                {
                    _library.Analytics.TrackFeedbackSubmitted(Math.Min(imageBytes.Count, AirFeedbackClient.MaxImages), withDeviceInfo);
                    ClearPersistedDraft();
                    attachments.Clear();
                    bodyBox.Text = "";
                    outerSwitchHost.Content = BuildThanksView();
                }
                else
                {
                    ShowError(L10n.T(sendResult.ErrorMessage ?? AirFeedbackClient.OfflineRetryMessage));
                }
            }
            catch
            {
                ShowError(L10n.T(AirFeedbackClient.OfflineRetryMessage));
            }
            finally
            {
                isSending = false;
                if (sendBtn?.Content is TextBlock stb2) stb2.Text = L10n.T("发送给开发者");
                bodyBox.IsEnabled = true;
                deviceToggle.IsEnabled = true;
                RebuildAttachments();
                SyncCounter();
            }
        }

        sendBtn = YYChrome.MakeDialogButton("发送给开发者", () => _ = SubmitFeedbackNowAsync(), dark, _library.Prefs.Tint, "dark", height: 36, horizontalPadding: 20);
        cancelBtn = YYChrome.MakeDialogButton("取消", () => { if (!isSending) { SaveDraftState(); activeDlg?.Hide(); } }, dark, _library.Prefs.Tint, "secondary", height: 36, horizontalPadding: 16);

        var footerGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.Children.Add(draftBar);
        var rightFootBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rightFootBtns.Children.Add(cancelBtn);
        rightFootBtns.Children.Add(sendBtn);
        Grid.SetColumn(rightFootBtns, 2);
        footerGrid.Children.Add(rightFootBtns);

        composerRoot = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(22, 18, 22, 18),
            AllowDrop = true,
            Children =
            {
                YYChrome.MakeDialogHeader("反馈", L10n.T("语阅还在早期，哪里不对、想要什么，直接写。"), () => { if (!isSending) { SaveDraftState(); activeDlg?.Hide(); } }, dark, titleSize: 17),
                bodyGroupCard,
                errorBanner,
                deviceCard,
                mailRow,
                privacyRow,
                footerGrid,
            },
        };

        // Drag-and-drop image files onto feedback sheet
        composerRoot.DragOver += (_, e) =>
        {
            if (!isSending && e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.Handled = true;
            }
        };
        composerRoot.Drop += async (_, e) =>
        {
            if (isSending || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (attachments.Count >= AirFeedbackClient.MaxImages) break;
                if (item is StorageFile sf)
                    await TryAttachImageFileAsync(sf);
            }
        };

        // Clipboard bitmap paste (tunneling PreviewKeyDown so TextBox doesn't swallow Ctrl+V + right-click Paste + Ctrl+Enter submit)
        async Task AttachClipboardBitmapAsync(DataPackageView content)
        {
            try
            {
                if (attachments.Count >= AirFeedbackClient.MaxImages)
                {
                    ShowError(L10n.T("最多只能添加 4 张图片。"));
                    return;
                }
                var bmpRef = await content.GetBitmapAsync();
                using var inStream = await bmpRef.OpenReadAsync();
                byte[] jpeg = await SanitizeStreamToJpegAsync(inStream);
                if (jpeg.Length == 0)
                {
                    ShowError(L10n.T("无法读取剪贴板中的图片。"));
                    return;
                }
                if (jpeg.Length > AirFeedbackClient.MaxImageBytes)
                {
                    ShowError(L10n.T("剪贴板图片压缩后仍超过 8 MB 上限。"));
                    return;
                }
                string pasteName = $"paste-{attachments.Count + 1}.jpg";
                string draftPath = SaveTempDraftAttachment(pasteName, jpeg);
                ShowError(null);
                attachments.Add((pasteName, jpeg, draftPath));
                RebuildAttachments();
                SaveDraftState();
            }
            catch { }
        }

        bodyBox.Paste += async (_, e) =>
        {
            if (isSending) return;
            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Text) || !content.Contains(StandardDataFormats.Bitmap)) return;
                e.Handled = true;
                await AttachClipboardBitmapAsync(content);
            }
            catch { }
        };

        composerRoot.PreviewKeyDown += async (_, e) =>
        {
            if (isSending) return;
            bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
            if (!ctrl) return;
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await SubmitFeedbackNowAsync();
                return;
            }
            if (e.Key != VirtualKey.V) return;
            try
            {
                var content = Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Bitmap)) return;
                bool focusInBody = ReferenceEquals(e.OriginalSource, bodyBox)
                    || ReferenceEquals(FocusManager.GetFocusedElement(xamlRoot), bodyBox);
                if (focusInBody && content.Contains(StandardDataFormats.Text)) return;
                e.Handled = true;
                await AttachClipboardBitmapAsync(content);
            }
            catch { }
        };

        SyncCounter();
        RebuildAttachments();
        outerSwitchHost.Content = composerRoot;
        L10n.ApplyTree(outerSwitchHost);

        while (true)
        {
            activeDlg = YYChrome.CreateFluentDialog(xamlRoot, dark, 500, outerSwitchHost, maxHeight: 780, tint: _library.Prefs.Tint);
            activeDlg.Closing += (_, closeArgs) =>
            {
                if (isSending) closeArgs.Cancel = true;
            };
            await activeDlg.ShowSafeAsync(_library.Prefs.Tint);
            if (activeDlg.Content is Border surf) surf.Child = null;
            activeDlg.Content = null;
            if (!captureRequested) break;

            captureRequested = false;
            await Task.Delay(180);
            if (ownerWindow.Content is UIElement rootEl)
            {
                try
                {
                    var rtb = new RenderTargetBitmap();
                    await rtb.RenderAsync(rootEl);
                    var pix = await rtb.GetPixelsAsync();
                    byte[] jpeg = await SanitizeBgraPixelsToJpegAsync(pix.ToArray(), (uint)rtb.PixelWidth, (uint)rtb.PixelHeight);
                    if (jpeg.Length == 0)
                    {
                        ShowError(L10n.T("截屏失败，请重试。"));
                    }
                    else if (jpeg.Length > AirFeedbackClient.MaxImageBytes)
                    {
                        ShowError(L10n.T("窗口截图压缩后仍超过 8 MB 上限。"));
                    }
                    else
                    {
                        string shotName = $"shot-{attachments.Count + 1}.jpg";
                        string draftPath = SaveTempDraftAttachment(shotName, jpeg);
                        ShowError(null);
                        attachments.Add((shotName, jpeg, draftPath));
                        RebuildAttachments();
                        SaveDraftState();
                    }
                }
                catch
                {
                    ShowError(L10n.T("截屏失败，请重试。"));
                }
            }
        }

        SaveDraftState();
    }

    // Strips EXIF metadata and downscales to <= 2048px JPEG <= 8 MB
    private static async Task<byte[]> SanitizeImageToJpegAsync(StorageFile file)
    {
        try
        {
            using var inStream = await file.OpenReadAsync();
            return await SanitizeStreamToJpegAsync(inStream);
        }
        catch { }
        return Array.Empty<byte>();
    }

    private static async Task<byte[]> SanitizeImageBytesToJpegAsync(byte[] raw)
    {
        try
        {
            using var ms = new InMemoryRandomAccessStream();
            await ms.WriteAsync(raw.AsBuffer());
            ms.Seek(0);
            return await SanitizeStreamToJpegAsync(ms);
        }
        catch { }
        return Array.Empty<byte>();
    }

    private static async Task<byte[]> SanitizeBgraPixelsToJpegAsync(byte[] bgra, uint w, uint h)
    {
        if (w == 0 || h == 0 || bgra.Length == 0) return Array.Empty<byte>();
        try
        {
            foreach (var (maxSide, quality) in new (uint, float)[]
            {
                (AirFeedbackClient.MaxImageLongSide, 0.80f),
                (AirFeedbackClient.MaxImageLongSide, 0.65f),
                (1600u, 0.65f),
                (1200u, 0.55f),
            })
            {
                double scale = Math.Min(1.0, (double)maxSide / Math.Max(w, h));
                uint tw = Math.Max(1, (uint)Math.Round(w * scale));
                uint th = Math.Max(1, (uint)Math.Round(h * scale));

                using var outStream = new InMemoryRandomAccessStream();
                var props = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) } };
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream, props);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, w, h, 96, 96, bgra);
                if (tw != w || th != h)
                {
                    encoder.BitmapTransform.ScaledWidth = tw;
                    encoder.BitmapTransform.ScaledHeight = th;
                    encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
                }
                await encoder.FlushAsync();

                if (outStream.Size <= (ulong)AirFeedbackClient.MaxImageBytes || maxSide == 1200u)
                {
                    outStream.Seek(0);
                    var bytes = new byte[outStream.Size];
                    await outStream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
                    return bytes;
                }
            }
        }
        catch { }
        return Array.Empty<byte>();
    }

    private static async Task<byte[]> SanitizeStreamToJpegAsync(IRandomAccessStream inStream)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(inStream);
            uint w = decoder.PixelWidth, h = decoder.PixelHeight;
            if (w == 0 || h == 0) return Array.Empty<byte>();
            bool swapped = decoder.OrientedPixelWidth != decoder.PixelWidth;

            foreach (var (maxSide, quality) in new (uint, float)[]
            {
                (AirFeedbackClient.MaxImageLongSide, 0.80f),
                (AirFeedbackClient.MaxImageLongSide, 0.65f),
                (1600u, 0.65f),
                (1200u, 0.55f),
            })
            {
                double scale = Math.Min(1.0, (double)maxSide / Math.Max(w, h));
                uint tw = Math.Max(1, (uint)Math.Round(w * scale));
                uint th = Math.Max(1, (uint)Math.Round(h * scale));
                uint outW = swapped ? th : tw;
                uint outH = swapped ? tw : th;

                var transform = new BitmapTransform { ScaledWidth = tw, ScaledHeight = th, InterpolationMode = BitmapInterpolationMode.Linear };
                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                    ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);

                using var outStream = new InMemoryRandomAccessStream();
                var props = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) } };
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream, props);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, outW, outH, 96, 96, pixelData.DetachPixelData());
                await encoder.FlushAsync();

                if (outStream.Size <= (ulong)AirFeedbackClient.MaxImageBytes || maxSide == 1200u)
                {
                    outStream.Seek(0);
                    var bytes = new byte[outStream.Size];
                    await outStream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
                    return bytes;
                }
            }
        }
        catch { }
        return Array.Empty<byte>();
    }
}
