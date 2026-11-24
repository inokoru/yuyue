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
/// - Full Feedback composer with persisted draft, title field (≤ 60 chars, never truncated), log attachment, drag-and-drop, Ctrl+V screenshot paste, up to 3 public attachments (any file ≤ 1.5 MB), EXIF-stripping JPEG, <see cref="YuyueCoreMethods.FeedbackMessage"/> ntfy.sh delivery, FIFO offline queue, and post-send confirmation.
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
            footRow.Children.Add(YYChrome.MakeSheetPillButton(releasesBtnText, () => { openReleases = true; upToDateDlg?.Hide(); }, dark, _library.Prefs.Tint, "secondary", height: 34, horizontalPadding: 14));
            footRow.Children.Add(YYChrome.MakeSheetPillButton("好", () => upToDateDlg?.Hide(), dark, _library.Prefs.Tint, "dark", height: 34, horizontalPadding: 18));

            var body = new StackPanel
            {
                Spacing = 12,
                Padding = new Thickness(22, 20, 22, 18),
                Children =
                {
                    YYChrome.MakeSheetHeader(info is not null ? "已是最新版本" : "检查更新", null, () => upToDateDlg?.Hide(), dark, titleSize: 16),
                    new TextBlock { Text = msg, FontSize = 13, LineHeight = 20, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"更新渠道：{(_library.Prefs.UpdateChannel == "beta" ? "测试版 (Beta)" : "正式版")}", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3) },
                    footRow,
                },
            };

            upToDateDlg = YYChrome.CreateMacSheet(xamlRoot, dark, 368, body, tint: _library.Prefs.Tint);
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
        var closeBtn = YYChrome.MakeSheetCloseButton(() => offerDlg?.Hide(), dark);
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
        var installBtn = YYChrome.MakeSheetPillButton(primaryLabel, () => { action = 1; offerDlg?.Hide(); }, dark, _library.Prefs.Tint, "dark", height: 40, stretch: true);

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

        offerDlg = YYChrome.CreateMacSheet(xamlRoot, dark, 440, offerBody, tint: _library.Prefs.Tint);
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
        var cancelBtn = YYChrome.MakeSheetPillButton("取消", () => { cts.Cancel(); progDlg?.Hide(); }, dark, _library.Prefs.Tint, "secondary", height: 32, horizontalPadding: 16);
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

        progDlg = YYChrome.CreateMacSheet(xamlRoot, dark, 380, progBody, tint: _library.Prefs.Tint);
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
        var okBtn = YYChrome.MakeSheetPillButton("好", () => dlgRef?.Hide(), dark, _library.Prefs.Tint, "dark", height: 34, horizontalPadding: 20);
        okBtn.HorizontalAlignment = HorizontalAlignment.Right;
        foot.Children.Add(okBtn);

        var sheetBody = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(24, 20, 24, 18),
            Children =
            {
                YYChrome.MakeSheetHeader("更新日志", "每一版改了什么", () => dlgRef?.Hide(), dark, titleSize: 18, serifTitle: true),
                new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) },
                new ScrollViewer { MaxHeight = 440, Content = stack },
                foot,
            },
        };

        dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 540, sheetBody, tint: _library.Prefs.Tint);
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
            ("没有账号，也没有服务器", "语阅没有注册登录，不埋点、不接入任何第三方统计或崩溃分析 SDK。你的书库、阅读进度、划线想法与生词全部保存在本机目录中。"),
            ("连接外部服务时直连服务商", "使用免费在线语音时，当前要读的句子会直接发给在线语音服务；当你主动配置 WebDAV 云盘、AI 书伴（DeepSeek / OpenAI / Claude / Ollama 等）、云端语音或 Notion / flomo / 新枝笔记导出时，客户端直接向你指定的服务地址发起请求，API Key 使用 Windows DPAPI 加密存储在本机，从不经过语阅中转，也从不写入跨端同步包。"),
            ("检查更新与模型下载", "检查更新与按需下载开源字体、离线语音模型、声音包时，仅向 GitHub Releases、魔搭（ModelScope）或 Hugging Face 发起普通 HTTPS GET 请求（字体预览按需访问 Google Fonts），不携带任何书库或设备标识。"),
            ("问题反馈", "只有在你主动提交反馈时才会联网，通过 ntfy.sh 中转为公开的 GitHub Issue；正文与附件均会公开展示，不收集邮箱。"),
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
        copyDiagBtn = YYChrome.MakeSheetPillButton("复制诊断摘要", () =>
        {
            string summary = $"语阅 {CurrentVersion} ({CurrentBuild})\n系统：{OsDisplayVersion()}\n架构：{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
            var dp = new DataPackage();
            dp.SetText(summary);
            Clipboard.SetContent(dp);
            if (copyDiagBtn?.Content is TextBlock tb)
                tb.Text = L10n.T("✓ 已复制诊断摘要");
        }, dark, tint, "secondary", height: 34, horizontalPadding: 14);
        copyDiagBtn.HorizontalAlignment = HorizontalAlignment.Left;

        var okBtn = YYChrome.MakeSheetPillButton("好", () => dlgRef?.Hide(), dark, tint, "dark", height: 34, horizontalPadding: 20);
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
                YYChrome.MakeSheetHeader("隐私说明", "没有服务器，不收集任何数据", () => dlgRef?.Hide(), dark, titleSize: 18, serifTitle: true),
                new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) },
                new ScrollViewer { MaxHeight = 430, Content = groupCard },
                foot,
            },
        };

        dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 540, sheetBody, tint: tint);
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
        var okBtn = YYChrome.MakeSheetPillButton("好", () => dlgRef?.Hide(), dark, tint, "dark", height: 34, horizontalPadding: 20);
        okBtn.HorizontalAlignment = HorizontalAlignment.Right;
        foot.Children.Add(okBtn);

        var sheetBody = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(24, 20, 24, 18),
            Children =
            {
                YYChrome.MakeSheetHeader("开源许可与致谢", "GPL-3.0 · NOTICE", () => dlgRef?.Hide(), dark, titleSize: 18, serifTitle: true),
                new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) },
                new ScrollViewer { MaxHeight = 440, Content = scrollContent },
                foot,
            },
        };

        dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 560, sheetBody, tint: tint);
        await dlgRef.ShowSafeAsync(tint);
    }

    // ---------- 1:1 Feedback.swift Composer (500px sheet + draft persistence + DnD/Ctrl+V + attachments + EXIF-stripping JPEG + ntfy/GitHub) ----------

    private string? FindLocalLogFile()
    {
        string[] candidates =
        [
            Path.Combine(_library.DataDir, "yuyue.log"),
            Path.Combine(_library.DataDir, "logs", "yuyue.log"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private byte[]? ReadLocalLogTailBytes(int maxBytes = 65536)
    {
        string? logPath = FindLocalLogFile();
        if (logPath is null) return null;
        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long len = fs.Length;
            if (len <= 0) return null;
            int toRead = (int)Math.Min(len, maxBytes);
            fs.Seek(len - toRead, SeekOrigin.Begin);
            byte[] buf = new byte[toRead];
            int r = fs.Read(buf, 0, toRead);
            return r > 0 ? buf[..r] : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task ShowFeedbackComposerAsync(Window ownerWindow, XamlRoot xamlRoot)
    {
        await FlushOfflineFeedbackQueueAsync();

        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        string category = _library.Prefs.FeedbackDraftCategory is "idea" or "other" ? _library.Prefs.FeedbackDraftCategory : "bug";
        bool withDeviceInfo = _library.Prefs.FeedbackAttachInfo;
        bool withLogs = _library.Prefs.FeedbackAttachLogs;
        var attachments = new List<(string Name, byte[] Data, bool IsImage, string? SourcePath)>();

        string draftTempDir = Path.Combine(_library.DataDir, "cache", "feedback-drafts");
        Directory.CreateDirectory(draftTempDir);
        string fullDraftTempPrefix = Path.GetFullPath(draftTempDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        string SaveTempDraftAttachment(string fileName, byte[] data)
        {
            Directory.CreateDirectory(draftTempDir);
            string safe = string.Concat(fileName.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrEmpty(safe)) safe = "attachment.jpg";
            string dst = Path.Combine(draftTempDir, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid().ToString("N")[..6]}-{safe}");
            File.WriteAllBytes(dst, data);
            return dst;
        }

        void DeleteOwnedDraftTempIfAny(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string full = Path.GetFullPath(path);
                if (full.StartsWith(fullDraftTempPrefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                    File.Delete(full);
            }
            catch { }
        }

        void ClearPersistedDraft()
        {
            foreach (string saved in _library.Prefs.FeedbackDraftFiles.ToArray())
                DeleteOwnedDraftTempIfAny(saved);
            foreach (var att in attachments)
                DeleteOwnedDraftTempIfAny(att.SourcePath);
            _library.Prefs.FeedbackDraftTitle = "";
            _library.Prefs.FeedbackDraftText = "";
            _library.Prefs.FeedbackDraftFiles.Clear();
            _library.SavePrefs();
        }

        // Restore persisted draft attachment files if still present on disk
        foreach (string savedPath in _library.Prefs.FeedbackDraftFiles.Take(3).ToArray())
        {
            if (!File.Exists(savedPath)) continue;
            try
            {
                var fi = new FileInfo(savedPath);
                if (fi.Length == 0 || fi.Length > 1_500_000) continue;
                string ext = fi.Extension.ToLowerInvariant();
                bool isImg = ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp";
                byte[] raw = await File.ReadAllBytesAsync(savedPath);
                if (isImg) raw = await SanitizeImageBytesToJpegAsync(raw, raw);
                if (raw.Length <= 1_500_000)
                {
                    string displayName = fi.Name;
                    // Strip internal timestamp-guid prefix from draft temp filenames for clean UI display
                    if (Path.GetFullPath(savedPath).StartsWith(fullDraftTempPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = fi.Name.Split('-', 3);
                        if (parts.Length == 3 && long.TryParse(parts[0], out _) && parts[1].Length == 6)
                            displayName = parts[2];
                    }
                    attachments.Add((isImg ? Path.ChangeExtension(displayName, ".jpg") : displayName, raw, isImg, savedPath));
                }
            }
            catch { }
        }

        void SaveDraftState()
        {
            _library.Prefs.FeedbackDraftCategory = category;
            _library.Prefs.FeedbackAttachInfo = withDeviceInfo;
            _library.Prefs.FeedbackAttachLogs = withLogs;
            _library.Prefs.FeedbackDraftTitle = titleBoxRefText();
            _library.Prefs.FeedbackDraftText = bodyBoxRefText();
            _library.Prefs.FeedbackDraftFiles = attachments
                .Where(a => !string.IsNullOrEmpty(a.SourcePath))
                .Select(a => a.SourcePath!)
                .Take(3)
                .ToList();
            _library.SavePrefs();
        }

        TextBox? titleBoxHolder = null;
        TextBox? bodyBoxHolder = null;
        string titleBoxRefText() => titleBoxHolder?.Text ?? _library.Prefs.FeedbackDraftTitle;
        string bodyBoxRefText() => bodyBoxHolder?.Text ?? _library.Prefs.FeedbackDraftText;

        var segHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Left };
        (string Id, string Label, string TitleHint, string BodyHint)[] catDefs =
        [
            ("bug", "问题", "一句话概括遇到的问题（最多 60 字）", "哪本书、哪一页，做了什么，结果怎样"),
            ("idea", "建议", "一句话概括你的建议（最多 60 字）", "想要什么功能，或者哪里可以更顺手"),
            ("other", "其他", "一句话标题（最多 60 字）", "想说的都可以"),
        ];

        var titleBox = new TextBox
        {
            Text = _library.Prefs.FeedbackDraftTitle,
            PlaceholderText = "一句话概括遇到的问题（最多 60 字）",
            AcceptsReturn = false,
            MaxLength = 60,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 13.5,
            Padding = new Thickness(0, 6, 0, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        YYChrome.StripTextBoxChrome(titleBox);
        AutomationProperties.SetName(titleBox, L10n.T("反馈标题"));
        titleBoxHolder = titleBox;
        var titleCounterText = new TextBlock
        {
            Text = titleBox.Text.Length >= 45 ? $"{titleBox.Text.Length} / 60" : "",
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = titleBox.Text.Length >= 45 ? Visibility.Visible : Visibility.Collapsed,
        };

        var bodyBox = new TextBox
        {
            Text = _library.Prefs.FeedbackDraftText,
            PlaceholderText = "哪本书、哪一页，做了什么，结果怎样",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 112,
            MaxLength = 800,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 13.5,
            Padding = new Thickness(0, 4, 0, 4),
        };
        YYChrome.StripTextBoxChrome(bodyBox);
        AutomationProperties.SetName(bodyBox, L10n.T("反馈正文"));
        bodyBoxHolder = bodyBox;
        var counterText = new TextBlock
        {
            Text = bodyBox.Text.Length >= 500 ? $"{bodyBox.Text.Length} / 800" : "",
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        ContentDialog? activeDlg = null;
        Button? sendBtn = null;
        bool isSending = false;

        void SyncSendEnabled()
        {
            bool ok = !isSending && !string.IsNullOrWhiteSpace(titleBox.Text) && !string.IsNullOrWhiteSpace(bodyBox.Text);
            if (sendBtn is not null)
            {
                sendBtn.IsEnabled = ok;
                sendBtn.Opacity = ok ? 1.0 : 0.42;
            }
        }

        titleBox.TextChanged += (_, _) =>
        {
            string flat = titleBox.Text.Replace('\r', ' ').Replace('\n', ' ');
            if (flat != titleBox.Text)
            {
                titleBox.Text = flat;
                titleBox.SelectionStart = flat.Length;
            }
            _library.Prefs.FeedbackDraftTitle = titleBox.Text;
            bool showCount = titleBox.Text.Length >= 45;
            titleCounterText.Text = showCount ? $"{titleBox.Text.Length} / 60" : "";
            titleCounterText.Visibility = showCount ? Visibility.Visible : Visibility.Collapsed;
            SyncSendEnabled();
        };
        bodyBox.TextChanged += (_, _) =>
        {
            _library.Prefs.FeedbackDraftText = bodyBox.Text;
            counterText.Text = bodyBox.Text.Length >= 500 ? $"{bodyBox.Text.Length} / 800" : "";
            SyncSendEnabled();
        };

        void SyncCatVisuals()
        {
            var curDef = catDefs.FirstOrDefault(c => c.Id == category);
            if (!string.IsNullOrEmpty(curDef.Id))
            {
                titleBox.PlaceholderText = L10n.T(curDef.TitleHint);
                bodyBox.PlaceholderText = L10n.T(curDef.BodyHint);
            }
            segHost.Content = YYChrome.MakeSegmentedBar(
                catDefs.Select(c => (c.Id, L10n.T(c.Label))).ToArray(),
                category,
                nextCat =>
                {
                    category = nextCat;
                    SyncCatVisuals();
                    SaveDraftState();
                },
                dark,
                fontSize: 12.5,
                height: 32);
            L10n.ApplyTree(segHost);
        }
        SyncCatVisuals();

        var attachList = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        var attachErrorText = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.Danger), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };

        void ShowAttachError(string? err)
        {
            if (string.IsNullOrWhiteSpace(err))
            {
                attachErrorText.Text = "";
                attachErrorText.Visibility = Visibility.Collapsed;
            }
            else
            {
                attachErrorText.Text = L10n.T(err);
                attachErrorText.Visibility = Visibility.Visible;
            }
        }

        void RebuildAttachments()
        {
            attachList.Children.Clear();
            attachList.Visibility = attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < attachments.Count; i++)
            {
                int idx = i;
                var att = attachments[i];
                var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                chip.Children.Add(YYIcons.Make(att.IsImage ? "photo" : "note-card", 11, ThemeHelper.Brush(tint.Accent)));
                chip.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = $"{att.Name} · {Math.Max(1, att.Data.Length / 1024)} KB",
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T1),
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 136,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }));
                var rm = new Button
                {
                    Content = "×",
                    Padding = new Thickness(4, 0, 4, 0),
                    MinWidth = 0,
                    MinHeight = 0,
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T2),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                };
                AutomationProperties.SetName(rm, L10n.T("移除附件"));
                rm.Click += (_, _) =>
                {
                    DeleteOwnedDraftTempIfAny(attachments[idx].SourcePath);
                    attachments.RemoveAt(idx);
                    ShowAttachError(null);
                    RebuildAttachments();
                    SaveDraftState();
                };
                chip.Children.Add(rm);
                attachList.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Inset),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(9, 3, 5, 3),
                    Child = chip,
                });
            }
            L10n.ApplyTree(attachList);
        }

        async Task TryAttachFileAsync(StorageFile f)
        {
            if (attachments.Count >= 3)
            {
                ShowAttachError("最多只能添加 3 个附件。");
                return;
            }
            string ext = Path.GetExtension(f.Name).ToLowerInvariant();
            bool isImg = ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp";
            byte[] raw;
            try { raw = await File.ReadAllBytesAsync(f.Path); }
            catch (Exception ex) { ShowAttachError($"无法读取「{f.Name}」：{ex.Message}"); return; }

            if (isImg) raw = await SanitizeImageToJpegAsync(f, raw);
            if (raw.Length == 0)
            {
                ShowAttachError($"「{f.Name}」读不出来或为空文件。");
                return;
            }
            if (raw.Length > 1_500_000)
            {
                ShowAttachError($"「{f.Name}」太大了，一个附件最多 1.5 MB（当前 {raw.Length / 1048576.0:0.1} MB）。");
                return;
            }
            ShowAttachError(null);
            attachments.Add((isImg ? Path.ChangeExtension(f.Name, ".jpg") : f.Name, raw, isImg, f.Path));
            RebuildAttachments();
            SaveDraftState();
        }

        bool captureRequested = false;

        StackPanel MakeAttachIconLabel(string iconKey, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(YYIcons.Make(iconKey, 11.5, ThemeHelper.Brush(p.T2)));
            sp.Children.Add(new TextBlock { Text = L10n.T(text), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        var addFileBtn = YYChrome.MakeSheetPillButton(MakeAttachIconLabel("paperclip", "附件"), async () =>
        {
            if (attachments.Count >= 3)
            {
                ShowAttachError("最多只能添加 3 个附件。");
                return;
            }
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(ownerWindow));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            await TryAttachFileAsync(f);
        }, dark, _library.Prefs.Tint, "secondary", height: 26, horizontalPadding: 10);

        var snapWindowBtn = YYChrome.MakeSheetPillButton(MakeAttachIconLabel("camera", "截图"), () =>
        {
            if (attachments.Count >= 3)
            {
                ShowAttachError("最多只能添加 3 个附件。");
                return;
            }
            if (ownerWindow.Content is not UIElement) return;
            SaveDraftState();
            captureRequested = true;
            activeDlg?.Hide();
        }, dark, _library.Prefs.Tint, "secondary", height: 26, horizontalPadding: 10);

        // YYGroup single-line title card
        var titleRowGrid = new Grid { ColumnSpacing = 8, Padding = new Thickness(13, 3, 12, 3), MinHeight = 40 };
        titleRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRowGrid.Children.Add(titleBox);
        Grid.SetColumn(titleCounterText, 1);
        titleRowGrid.Children.Add(titleCounterText);
        var titleGroupCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = titleRowGrid,
        };

        // YYGroup multiline body + attachments + bottom toolbar card
        var bodyToolbar = new Grid { Padding = new Thickness(0, 8, 0, 2) };
        var leftTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        leftTools.Children.Add(addFileBtn);
        leftTools.Children.Add(snapWindowBtn);
        leftTools.Children.Add(new TextBlock
        {
            Text = L10n.T("支持 Ctrl+V 粘图 · 拖入文件"),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        });
        bodyToolbar.Children.Add(leftTools);
        bodyToolbar.Children.Add(counterText);

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

        // Collapsible 附带设备信息 YYGroup card (Feedback.swift)
        bool deviceExpanded = false;
        var chevronPresenter = new ContentControl
        {
            VerticalAlignment = VerticalAlignment.Center,
            Content = YYIcons.Make("chevron-down", 10, ThemeHelper.Brush(p.T3)),
        };
        var deviceToggle = new ToggleSwitch
        {
            IsOn = withDeviceInfo,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(deviceToggle, L10n.T("附带设备信息"));
        var logsCheck = new CheckBox
        {
            Content = FindLocalLogFile() is not null ? L10n.T("附带最近运行日志（末尾 64 KB，随反馈公开）") : L10n.T("附带最近运行日志（如有，随反馈公开）"),
            IsChecked = withLogs,
            FontSize = 11.5,
        };
        logsCheck.Checked += (_, _) => { withLogs = true; SaveDraftState(); };
        logsCheck.Unchecked += (_, _) => { withLogs = false; SaveDraftState(); };

        var detailGrid = new Grid { ColumnSpacing = 12 };
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        (string Key, string Val)[] envSpecs =
        [
            ("版本", $"v{CurrentVersion} ({CurrentBuild})"),
            ("系统", OsDisplayVersion()),
            ("设备", $"Windows · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}"),
        ];
        for (int i = 0; i < envSpecs.Length; i++)
        {
            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(new TextBlock { Text = L10n.T(envSpecs[i].Key), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
            sp.Children.Add(new TextBlock { Text = envSpecs[i].Val, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T1), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(sp, i);
            detailGrid.Children.Add(sp);
        }

        var deviceDetailPanel = new StackPanel
        {
            Spacing = 8,
            Padding = new Thickness(13, 0, 13, 10),
            Visibility = Visibility.Collapsed,
            Children =
            {
                new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 0, 0, 4) },
                detailGrid,
                logsCheck,
            },
        };
        deviceToggle.Toggled += (_, _) => { withDeviceInfo = deviceToggle.IsOn; SaveDraftState(); };

        var devHeadLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        devHeadLeft.Children.Add(new TextBlock { Text = L10n.T("附带设备信息"), FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
        devHeadLeft.Children.Add(new TextBlock { Text = $"语阅 {CurrentVersion} · {OsDisplayVersion()}", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        devHeadLeft.Children.Add(chevronPresenter);

        var expandBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(13, 8, 8, 8),
            MinHeight = 40,
            UseSystemFocusVisuals = false,
            Content = devHeadLeft,
        };
        expandBtn.Click += (_, _) =>
        {
            deviceExpanded = !deviceExpanded;
            deviceDetailPanel.Visibility = deviceExpanded ? Visibility.Visible : Visibility.Collapsed;
            chevronPresenter.Content = YYIcons.Make(deviceExpanded ? "chevron-up" : "chevron-down", 10, ThemeHelper.Brush(p.T3));
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

        // Mail hint + public issue disclosure
        var infoMetaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        infoMetaRow.Children.Add(new TextBlock { Text = L10n.T("需要回复或不想公开？"), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        infoMetaRow.Children.Add(new HyperlinkButton
        {
            Content = L10n.T("直接发邮件 ↗"),
            NavigateUri = new Uri("mailto:yuyue.app@icloud.com"),
            FontSize = 11.5,
            Padding = new Thickness(2, 0, 2, 0),
            MinHeight = 0,
            MinWidth = 0,
        });
        infoMetaRow.Children.Add(new TextBlock { Text = "·", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        bool preferCn = Yuyue.Speech.ModelDownload.PrefersChinaMirror();
        var openGhDirectBtn = YYChrome.MakeSettingsActionButton(preferCn ? "在 CNB 打开 Issue ↗" : "在 GitHub 打开 Issue ↗", () =>
        {
            string t0 = titleBox.Text.Trim();
            string b0 = bodyBox.Text.Trim();
            SaveDraftState();
            activeDlg?.Hide();
            OpenGitHubIssue(category, t0, b0, withDeviceInfo);
        }, dark);
        openGhDirectBtn.FontSize = 11.5;
        infoMetaRow.Children.Add(openGhDirectBtn);

        void ResetComposerToEmpty()
        {
            category = "bug";
            SyncCatVisuals();
            titleBox.Text = "";
            bodyBox.Text = "";
            ClearPersistedDraft();
            attachments.Clear();
            ShowAttachError(null);
            RebuildAttachments();
            SaveDraftState();
            SyncSendEnabled();
        }

        var clearDraftBtn = YYChrome.MakeSettingsActionButton("清空草稿", ResetComposerToEmpty, dark);
        clearDraftBtn.FontSize = 11.5;

        var outerSwitchHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        StackPanel? composerRoot = null;

        StackPanel BuildThanksView(bool queuedOffline)
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
            thanksBtns.Children.Add(YYChrome.MakeSheetPillButton("再写一条", () =>
            {
                ResetComposerToEmpty();
                outerSwitchHost.Content = composerRoot;
                L10n.ApplyTree(outerSwitchHost);
                titleBox.Focus(FocusState.Programmatic);
            }, dark, _library.Prefs.Tint, "secondary", height: 36, horizontalPadding: 18));
            thanksBtns.Children.Add(YYChrome.MakeSheetPillButton("完成", () => activeDlg?.Hide(), dark, _library.Prefs.Tint, "dark", height: 36, horizontalPadding: 22));

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
                        Text = L10n.T(queuedOffline ? "已暂存离线队列" : "收到了，谢谢"),
                        FontSize = 19,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = ThemeHelper.Brush(p.T1),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = L10n.T(queuedOffline
                            ? "当前网络或服务暂时繁忙，内容已安全保存在本机，联网后将自动补发。"
                            : "稍后会出现在 GitHub 问题板上。"),
                        FontSize = 13,
                        Foreground = ThemeHelper.Brush(p.T2),
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new HyperlinkButton
                    {
                        Content = L10n.T("需要补充或私信？直接发邮件 ↗"),
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

        var uploadFailRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Visibility = Visibility.Collapsed,
        };
        var fallbackGhBtn = YYChrome.MakeSheetPillButton(preferCn ? "改为在 CNB 打开 Issue ↗" : "改为在 GitHub 打开 Issue ↗", () =>
        {
            string t0 = titleBox.Text.Trim();
            string b0 = bodyBox.Text.Trim();
            SaveDraftState();
            activeDlg?.Hide();
            OpenGitHubIssue(category, t0, b0, withDeviceInfo);
        }, dark, _library.Prefs.Tint, "secondary", height: 30, horizontalPadding: 12);
        uploadFailRow.Children.Add(fallbackGhBtn);

        async Task SubmitFeedbackNowAsync()
        {
            if (isSending) return;
            string title = titleBox.Text.Trim();
            string text = bodyBox.Text.Trim();
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(text)) return;

            isSending = true;
            ShowAttachError(null);
            uploadFailRow.Visibility = Visibility.Collapsed;
            if (sendBtn?.Content is TextBlock stb) stb.Text = L10n.T("发送中…");
            SyncSendEnabled();

            try
            {
                SaveDraftState();
                var finalAttachments = attachments.Select(a => (a.Name, a.Data, a.IsImage)).ToList();
                if (withLogs && finalAttachments.Count < 3 && ReadLocalLogTailBytes() is { Length: > 0 } logBytes)
                    finalAttachments.Add(("yuyue.log", logBytes, false));

                var relayStatus = await TrySubmitViaRelayAsync(category, title, text, withDeviceInfo, finalAttachments);
                if (relayStatus == RelaySubmitOutcome.Sent)
                {
                    ClearPersistedDraft();
                    outerSwitchHost.Content = BuildThanksView(queuedOffline: false);
                }
                else if (relayStatus == RelaySubmitOutcome.QueuedOffline)
                {
                    outerSwitchHost.Content = BuildThanksView(queuedOffline: true);
                }
                else if (relayStatus == RelaySubmitOutcome.UploadFailed)
                {
                    // NEVER clear saved draft when network upload throws!
                    ShowAttachError("网络上传未成功，你填写的文字与截图已保存在草稿中，可稍后再试或直接打开 GitHub Issue。");
                    uploadFailRow.Visibility = Visibility.Visible;
                }
                else
                {
                    // YuyueFeedbackTopic unconfigured: NEVER clear saved draft!
                    activeDlg?.Hide();
                    OpenGitHubIssue(category, title, text, withDeviceInfo);
                }
            }
            finally
            {
                isSending = false;
                if (sendBtn?.Content is TextBlock stb2) stb2.Text = L10n.T("发送");
                SyncSendEnabled();
            }
        }

        sendBtn = YYChrome.MakeSheetPillButton("发送", () => _ = SubmitFeedbackNowAsync(), dark, _library.Prefs.Tint, "dark", height: 36, horizontalPadding: 20);
        SyncSendEnabled();

        var footerGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.Children.Add(clearDraftBtn);
        var rightFootBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rightFootBtns.Children.Add(YYChrome.MakeSheetPillButton("取消", () => { SaveDraftState(); activeDlg?.Hide(); }, dark, _library.Prefs.Tint, "secondary", height: 36, horizontalPadding: 16));
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
                YYChrome.MakeSheetHeader("反馈", "语阅还在早期，哪里不对、想要什么，直接写。", () => { SaveDraftState(); activeDlg?.Hide(); }, dark, titleSize: 17),
                segHost,
                titleGroupCard,
                bodyGroupCard,
                attachErrorText,
                uploadFailRow,
                deviceCard,
                infoMetaRow,
                new TextBlock
                {
                    Text = L10n.T("正文和附件都会公开在 GitHub Issue 中，方便社区一起排查；不收集邮箱，请勿上传个人隐私或完整版权书。"),
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T3),
                    TextWrapping = TextWrapping.Wrap,
                },
                footerGrid,
            },
        };

        // Drag-and-drop files onto feedback dialog
        composerRoot.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.Handled = true;
            }
        };
        composerRoot.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (item is StorageFile sf)
                    await TryAttachFileAsync(sf);
            }
        };

        // Ctrl+V clipboard image paste & Ctrl+Enter quick submit
        async Task HandleComposerKeysAsync(KeyRoutedEventArgs e)
        {
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
                e.Handled = true;
                if (attachments.Count >= 3)
                {
                    ShowAttachError("最多只能添加 3 个附件。");
                    return;
                }
                var bmpRef = await content.GetBitmapAsync();
                using var inStream = await bmpRef.OpenReadAsync();
                byte[] jpeg = await SanitizeStreamToJpegAsync(inStream);
                if (jpeg.Length == 0)
                {
                    ShowAttachError("无法读取剪贴板中的图片。");
                    return;
                }
                if (jpeg.Length > 1_500_000)
                {
                    ShowAttachError("剪贴板图片压缩后仍超过 1.5 MB 上限。");
                    return;
                }
                string pasteName = $"paste-{attachments.Count + 1}.jpg";
                string draftPath = SaveTempDraftAttachment(pasteName, jpeg);
                ShowAttachError(null);
                attachments.Add((pasteName, jpeg, true, draftPath));
                RebuildAttachments();
                SaveDraftState();
            }
            catch { }
        }
        titleBox.KeyDown += async (_, e) => await HandleComposerKeysAsync(e);
        bodyBox.KeyDown += async (_, e) => await HandleComposerKeysAsync(e);
        composerRoot.KeyDown += async (_, e) => await HandleComposerKeysAsync(e);

        RebuildAttachments();
        outerSwitchHost.Content = composerRoot;
        L10n.ApplyTree(outerSwitchHost);

        while (true)
        {
            activeDlg = YYChrome.CreateMacSheet(xamlRoot, dark, 500, outerSwitchHost, maxHeight: 780, tint: _library.Prefs.Tint);
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
                    using var ms = new InMemoryRandomAccessStream();
                    var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);
                    enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pix.ToArray());
                    await enc.FlushAsync();
                    ms.Seek(0);
                    var rawBytes = new byte[ms.Size];
                    await ms.ReadAsync(rawBytes.AsBuffer(), (uint)rawBytes.Length, InputStreamOptions.None);
                    byte[] jpeg = await SanitizeImageBytesToJpegAsync(rawBytes, rawBytes);
                    if (jpeg.Length > 1_500_000)
                    {
                        ShowAttachError("窗口截图压缩后仍超过 1.5 MB 上限。");
                    }
                    else
                    {
                        string shotName = $"shot-{attachments.Count + 1}.jpg";
                        string draftPath = SaveTempDraftAttachment(shotName, jpeg);
                        ShowAttachError(null);
                        attachments.Add((shotName, jpeg, true, draftPath));
                        RebuildAttachments();
                        SaveDraftState();
                    }
                }
                catch (Exception ex)
                {
                    ShowAttachError($"截屏失败：{ex.Message}");
                }
            }
        }
    }

    private enum RelaySubmitOutcome { Unconfigured, Sent, QueuedOffline, UploadFailed }

    internal static string? ResolveFeedbackTopic()
    {
        string? topic = Environment.GetEnvironmentVariable("YuyueFeedbackTopic");
        if (string.IsNullOrWhiteSpace(topic))
        {
            try
            {
                var attrs = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>();
                foreach (var a in attrs)
                {
                    if (a.Key == "YuyueFeedbackTopic" && !string.IsNullOrWhiteSpace(a.Value))
                    {
                        topic = a.Value.Trim();
                        break;
                    }
                }
            }
            catch { }
        }
        return string.IsNullOrWhiteSpace(topic) ? null : topic;
    }

    private static string SafeFileExt(string name)
    {
        string ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (ext.Length is >= 1 and <= 10 && ext.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
            return "." + ext;
        return ".bin";
    }

    private static string EncodeHeaderName(string name)
    {
        string baseName = Path.GetFileName(name ?? "").Trim();
        if (string.IsNullOrEmpty(baseName)) return "";
        if (baseName.Length > 80) baseName = baseName[..80];
        return Uri.EscapeDataString(baseName);
    }

    private async Task<RelaySubmitOutcome> TrySubmitViaRelayAsync(
        string category, string title, string text, bool withInfo,
        List<(string Name, byte[] Data, bool IsImage)> attachments)
    {
        string? topic = ResolveFeedbackTopic();
        if (string.IsNullOrWhiteSpace(topic))
            return RelaySubmitOutcome.Unconfigured;

        try
        {
            var fileIds = new List<string>();
            for (int i = 0; i < attachments.Count; i++)
            {
                var att = attachments[i];
                using var putReq = new HttpRequestMessage(HttpMethod.Put, $"https://ntfy.sh/{topic}")
                {
                    Content = new ByteArrayContent(att.Data),
                };
                putReq.Headers.Add("X-Firebase", "no");
                if (att.IsImage)
                {
                    putReq.Headers.Add("Filename", $"shot-{i + 1}.jpg");
                    putReq.Headers.Add("X-Message", "v2shot");
                }
                else
                {
                    string encoded = EncodeHeaderName(att.Name);
                    putReq.Headers.Add("Filename", $"file-{i + 1}{SafeFileExt(att.Name)}");
                    putReq.Headers.Add("X-Message", string.IsNullOrEmpty(encoded) ? "v2file" : $"v2file {encoded}");
                }
                using var putResp = await Http.SendAsync(putReq);
                putResp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await putResp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.GetString() is string fid)
                    fileIds.Add(fid);
            }

            var draft = new FeedbackDraft(
                Category: category,
                Title: title,
                Text: text,
                AppVersion: withInfo ? $"{CurrentVersion} ({CurrentBuild})" : "",
                Platform: withInfo ? "windows" : "",
                OsVersion: withInfo ? OsDisplayVersion() : "",
                Files: fileIds.ToArray());

            string sealedJson = YuyueCoreMethods.FeedbackMessage(draft);
            using var postReq = new HttpRequestMessage(HttpMethod.Post, $"https://ntfy.sh/{topic}")
            {
                Content = new StringContent(sealedJson, Encoding.UTF8, "application/json"),
            };
            postReq.Headers.Add("X-Firebase", "no");
            using var postResp = await Http.SendAsync(postReq);
            if ((int)postResp.StatusCode == 429 || !postResp.IsSuccessStatusCode)
            {
                if (attachments.Count == 0)
                {
                    EnqueueOfflineFeedback(topic, sealedJson);
                    return RelaySubmitOutcome.QueuedOffline;
                }
                return RelaySubmitOutcome.UploadFailed;
            }
            return RelaySubmitOutcome.Sent;
        }
        catch
        {
            return RelaySubmitOutcome.UploadFailed;
        }
    }

    private void EnqueueOfflineFeedback(string topic, string body)
    {
        _library.Prefs.FeedbackOfflineQueue.Add($"{topic}\n{body}");
        while (_library.Prefs.FeedbackOfflineQueue.Count > 10)
            _library.Prefs.FeedbackOfflineQueue.RemoveAt(0);
        _library.SavePrefs();
    }

    public async Task FlushOfflineFeedbackQueueAsync()
    {
        if (_library.Prefs.FeedbackOfflineQueue.Count == 0) return;
        var remaining = new List<string>();
        foreach (var entry in _library.Prefs.FeedbackOfflineQueue.ToArray())
        {
            int nl = entry.IndexOf('\n');
            if (nl <= 0) continue;
            string topic = entry[..nl];
            string body = entry[(nl + 1)..];
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"https://ntfy.sh/{topic}")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                req.Headers.Add("X-Firebase", "no");
                using var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) remaining.Add(entry);
            }
            catch
            {
                remaining.Add(entry);
            }
        }
        _library.Prefs.FeedbackOfflineQueue = remaining;
        _library.SavePrefs();
    }

    private static void OpenGitHubIssue(string category, string title, string text, bool withInfo)
    {
        string cleanTitle = title.Replace('\r', ' ').Replace('\n', ' ').Trim();
        string tag = category switch { "idea" => "[建议] ", "other" => "[其他] ", _ => "[问题] " };
        string body = withInfo
            ? $"{text}\n\n---\n- 平台：Windows ({OsDisplayVersion()})\n- 版本：语阅 {CurrentVersion} ({CurrentBuild})"
            : text;
        string url = Yuyue.Speech.ModelDownload.PrefersChinaMirror()
            ? $"https://cnb.cool/{RepoSlug}/-/issues"
            : $"https://github.com/{RepoSlug}/issues/new?title={Uri.EscapeDataString(tag + cleanTitle)}&body={Uri.EscapeDataString(body)}";
        _ = Launcher.LaunchUriAsync(new Uri(url));
    }

    // Strips EXIF metadata and downscales to <= 1600px JPEG <= 1 MB (1:1 Feedback.swift)
    private static async Task<byte[]> SanitizeImageToJpegAsync(StorageFile file, byte[] fallback)
    {
        try
        {
            using var inStream = await file.OpenReadAsync();
            var bytes = await SanitizeStreamToJpegAsync(inStream);
            return bytes.Length > 0 ? bytes : fallback;
        }
        catch { }
        return fallback;
    }

    private static async Task<byte[]> SanitizeImageBytesToJpegAsync(byte[] raw, byte[] fallback)
    {
        try
        {
            using var ms = new InMemoryRandomAccessStream();
            await ms.WriteAsync(raw.AsBuffer());
            ms.Seek(0);
            var bytes = await SanitizeStreamToJpegAsync(ms);
            return bytes.Length > 0 ? bytes : fallback;
        }
        catch { }
        return fallback;
    }

    private static async Task<byte[]> SanitizeStreamToJpegAsync(IRandomAccessStream inStream)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(inStream);
            uint w = decoder.PixelWidth, h = decoder.PixelHeight;

            foreach (var (maxSide, quality) in new[] { (1600u, 0.78), (1600u, 0.60), (1200u, 0.60), (900u, 0.50) })
            {
                double scale = Math.Min(1.0, (double)maxSide / Math.Max(w, h));
                uint tw = Math.Max(1, (uint)Math.Round(w * scale));
                uint th = Math.Max(1, (uint)Math.Round(h * scale));

                var transform = new BitmapTransform { ScaledWidth = tw, ScaledHeight = th, InterpolationMode = BitmapInterpolationMode.Linear };
                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                    ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);

                using var outStream = new InMemoryRandomAccessStream();
                var props = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(quality, Windows.Foundation.PropertyType.Single) } };
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outStream, props);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tw, th, 96, 96, pixelData.DetachPixelData());
                await encoder.FlushAsync();

                if (outStream.Size <= 1_000_000 || maxSide == 900u)
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
