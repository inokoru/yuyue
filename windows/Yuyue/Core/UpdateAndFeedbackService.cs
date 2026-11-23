using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using Yuyue.Design;

namespace Yuyue.Core;

/// <summary>
/// 1:1 port of <c>Update.swift</c>, <c>MacUpdate.swift</c>, <c>UpdateUI.swift</c>, and <c>Feedback.swift</c>:
/// - Stable &amp; Beta GitHub Releases update feed check via <see cref="YuyueCoreMethods.ParseUpdate"/> / <see cref="YuyueCoreMethods.CompareVersions"/>
/// - Bundled <c>CHANGELOG.md</c> viewer via <see cref="YuyueCoreMethods.ParseChangelog"/>, Privacy sheet, and Open-Source Licenses sheet
/// - Full Feedback composer with up to 3 EXIF-stripped JPEG/file attachments, <see cref="YuyueCoreMethods.SealFeedbackFile"/> + <see cref="YuyueCoreMethods.FeedbackMessage"/> ntfy.sh delivery, FIFO offline queue, and floating screenshot helper!
/// </summary>
public sealed class UpdateAndFeedbackService
{
    public const string CurrentVersion = "0.1.0";
    public const ulong CurrentBuild = 1;
    public const string RepoSlug = "inokoru/yuyue";

    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(20) };
    private readonly LibraryService _library;

    public UpdateInfo? LatestOffer { get; private set; }
    public Release[] OfferNotes { get; private set; } = [];
    public bool HasUnreadBadge =>
        LatestOffer is not null &&
        LatestOffer.Version != _library.Prefs.UpdateSkippedVersion &&
        !(LatestOffer.Version == _library.Prefs.UpdateRemindVersion &&
          _library.Prefs.UpdateRemindUntilMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public UpdateAndFeedbackService(LibraryService library)
    {
        _library = library;
        if (!Http.DefaultRequestHeaders.UserAgent.Any())
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Yuyue/0.1 (Windows)");
    }

    public async Task<(bool HasUpdate, UpdateInfo? Info, string Message)> CheckForUpdatesAsync(bool manual = false, CancellationToken ct = default)
    {
        if (!manual && !_library.Prefs.AutoCheckUpdates)
            return (false, null, "自动检查已关闭");

        if (!manual && _library.Prefs.UpdateLastCheckedMs is long lastMs &&
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastMs < 24L * 3600_000L)
        {
            return (LatestOffer is not null, LatestOffer, "24 小时内已检查过");
        }

        try
        {
            string baseFeed = await ResolveFeedBaseAsync(_library.Prefs.UpdateChannel == "beta", ct);
            string updateJson = await Http.GetStringAsync(baseFeed + "update.json", ct);
            var info = YuyueCoreMethods.ParseUpdate(updateJson);

            _library.Prefs.UpdateLastCheckedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _library.SavePrefs();

            bool newer = info.Code > 0
                ? info.Code > CurrentBuild
                : YuyueCoreMethods.CompareVersions(info.Version, CurrentVersion) > 0;

            if (!newer)
            {
                LatestOffer = null;
                OfferNotes = [];
                return (false, info, $"当前已是最新版本（v{CurrentVersion}）");
            }

            LatestOffer = info;
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

            return (true, info, $"发现新版本 v{info.Version}");
        }
        catch (Exception ex)
        {
            return (false, null, $"暂时无法连接更新源（当前 v{CurrentVersion}）：{ex.Message}");
        }
    }

    private static async Task<string> ResolveFeedBaseAsync(bool beta, CancellationToken ct)
    {
        string? overrideBase = Environment.GetEnvironmentVariable("YYUpdateBase");
        if (!string.IsNullOrWhiteSpace(overrideBase))
            return overrideBase.EndsWith('/') ? overrideBase : overrideBase + "/";

        if (!beta)
            return $"https://github.com/{RepoSlug}/releases/latest/download/";

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
        return $"https://github.com/{RepoSlug}/releases/latest/download/";
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
                @"c:\Users\PC\Desktop\yuyue\CHANGELOG.md",
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
            new Release("0.1.0", "2026-11-22",
            [
                "全新桌面端书架、在读封面取色横带与内联书籍档案页",
                "双栏仿真暖纸电子书阅读器、自定义调纸与字样册",
                "本地离线 ZipVoice 24kHz 自然人声、多角色广播剧与声音克隆",
                "书房电台双主播播客、有声书与电子书对齐",
                "个人云盘 / WebDAV 无账号多端同步与本地快照备份",
            ],
            [
                "Desktop library shelf, cover-dyed reading band, and inline book record page",
                "Two-column warm-paper EPUB/MOBI/TXT/PDF reader with custom paper studio",
                "On-device ZipVoice 24kHz neural TTS, multi-role drama, and voice cloning",
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

        var (hasUpdate, info, msg) = await CheckForUpdatesAsync(manual: true);
        if (!hasUpdate || info is null)
        {
            var dlg = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = info is not null ? "已是最新版本" : "检查更新",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Width = 340,
                    Children =
                    {
                        new TextBlock { Text = msg, FontSize = 13, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = $"更新渠道：{(_library.Prefs.UpdateChannel == "beta" ? "测试版 (Beta)" : "正式版")}", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3) },
                    },
                },
                PrimaryButtonText = "查看 GitHub Releases ↗",
                CloseButtonText = "好",
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                _ = Launcher.LaunchUriAsync(new Uri($"https://github.com/{RepoSlug}/releases"));
            return;
        }

        // 460px Release Offer Dialog (1:1 MacUpdateDialog)
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

        var skipBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
            Content = "跳过此版本",
            FontSize = 11.5,
            Height = 26,
        };

        ContentDialog? offerDlg = null;
        skipBtn.Click += (_, _) =>
        {
            _library.Prefs.UpdateSkippedVersion = info.Version;
            _library.SavePrefs();
            offerDlg?.Hide();
        };

        var root = new StackPanel
        {
            Width = 440,
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"语阅 v{info.Version} 已发布（当前 v{CurrentVersion}）", FontSize = 13, Foreground = ThemeHelper.Brush(p.T2) },
                new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Inset),
                    Padding = new Thickness(14, 12, 14, 12),
                    Child = new ScrollViewer { MaxHeight = 240, Content = notesStack },
                },
                skipBtn,
            },
        };

        offerDlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "发现新版本",
            Content = root,
            PrimaryButtonText = "前往下载安装",
            SecondaryButtonText = "稍后提醒（3 天后）",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
        };

        var res = await offerDlg.ShowAsync();
        if (res == ContentDialogResult.Primary)
        {
            string url = !string.IsNullOrWhiteSpace(info.Page) ? info.Page : $"https://github.com/{RepoSlug}/releases/latest";
            _ = Launcher.LaunchUriAsync(new Uri(url));
        }
        else if (res == ContentDialogResult.Secondary)
        {
            _library.Prefs.UpdateRemindVersion = info.Version;
            _library.Prefs.UpdateRemindUntilMs = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeMilliseconds();
            _library.SavePrefs();
        }
    }

    public async Task ShowChangelogSheetAsync(XamlRoot xamlRoot)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var releases = LoadBundledChangelog();
        var stack = new StackPanel { Spacing = 14, Width = 460 };
        foreach (var r in releases)
        {
            var card = new StackPanel { Spacing = 5 };
            var head = new Grid();
            head.Children.Add(new TextBlock { Text = $"第 {r.Version} 版", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
            head.Children.Add(new TextBlock { Text = r.Date, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            card.Children.Add(head);

            var lines = L10n.IsEnglish && r.NotesEn.Length > 0 ? r.NotesEn : r.NotesZh;
            foreach (var item in lines)
                card.Children.Add(new TextBlock { Text = "• " + item, FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap, LineHeight = 20 });

            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(p.Inset),
                Padding = new Thickness(14, 12, 14, 12),
                Child = card,
            });
        }

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "更新日志",
            Content = new ScrollViewer { MaxHeight = 420, Content = stack },
            CloseButtonText = "好",
        };
        await dlg.ShowAsync();
    }

    public static async Task ShowPrivacySheetAsync(XamlRoot xamlRoot)
    {
        var stack = new StackPanel { Spacing = 10, Width = 440 };
        (string Title, string Body)[] items =
        [
            ("没有账号，也没有服务器", "语阅没有注册登录，不埋点、不接入任何第三方统计或崩溃分析 SDK。你的书库、阅读进度、划线想法与生词全部保存在本机目录中。"),
            ("连接外部服务时直连服务商", "当你主动配置 WebDAV 云盘、AI 书伴（DeepSeek / OpenAI / Claude / Ollama 等）、云端语音或 Notion / flomo / 新枝笔记导出时，客户端直接向你指定的服务地址发起请求，API Key 使用 Windows DPAPI 加密存储在本机，从不经过语阅中转，也从不写入跨端同步包。"),
            ("检查更新与模型下载", "检查更新与下载开源字体、离线语音模型时，仅向 GitHub Releases 发起普通 HTTPS GET 请求，不携带任何书库或设备标识。"),
        ];
        foreach (var (t, b) in items)
        {
            stack.Children.Add(new TextBlock { Text = t, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            stack.Children.Add(new TextBlock { Text = b, FontSize = 12, Opacity = 0.78, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(0, 0, 0, 4) });
        }
        await new ContentDialog { XamlRoot = xamlRoot, Title = "隐私说明", Content = stack, CloseButtonText = "好" }.ShowAsync();
    }

    public static async Task ShowLicensesSheetAsync(XamlRoot xamlRoot)
    {
        var stack = new StackPanel { Spacing = 8, Width = 460 };
        stack.Children.Add(new TextBlock { Text = "语阅（Yuyue）基于 GNU General Public License v3.0 (GPL-3.0) 开源发布。", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        (string Name, string Lic, string Desc)[] deps =
        [
            ("yuyue-core (Rust)", "GPL-3.0", "跨端统一排版解析、全文检索、同步日志、有声对齐与播客引擎"),
            ("SQLite / rusqlite", "Public Domain / MIT", "本地资料库与全文索引存储"),
            ("UniFFI (Mozilla)", "MPL-2.0", "Rust ↔ Swift / Kotlin / C# 跨语言绑定生成"),
            ("ONNX Runtime (Microsoft)", "MIT", "本地 ZipVoice + Vocos 24kHz 离线神经语音推理"),
            ("Windows App SDK / WinUI 3", "MIT", "Windows 原生桌面界面与 WebView2 宿主"),
            ("霞鹜文楷 (LXGW WenKai)", "OFL-1.1", "开源中文字体排印支持"),
            ("ECDICT (skywind3000)", "MIT", "内置离线英汉词典与词频分级数据"),
        ];
        foreach (var (n, l, d) in deps)
        {
            var row = new StackPanel { Spacing = 2, Padding = new Thickness(10, 8, 10, 8) };
            row.Children.Add(new TextBlock { Text = $"{n}  ·  {l}", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            row.Children.Add(new TextBlock { Text = d, FontSize = 11.5, Opacity = 0.72, TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = (Brush)Application.Current.Resources["YYInsetBrush"], Child = row });
        }
        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "开源许可与致谢",
            Content = new ScrollViewer { MaxHeight = 400, Content = stack },
            PrimaryButtonText = "查看源代码 (GitHub) ↗",
            CloseButtonText = "好",
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            _ = Launcher.LaunchUriAsync(new Uri($"https://github.com/{RepoSlug}"));
    }

    // ---------- 1:1 Feedback.swift Composer (500px sheet + attachments + EXIF-stripping JPEG + ntfy/GitHub) ----------

    public async Task ShowFeedbackComposerAsync(Window ownerWindow, XamlRoot xamlRoot)
    {
        await FlushOfflineFeedbackQueueAsync();

        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        string category = "bug";
        bool publicShots = true;
        bool withDeviceInfo = true;
        var attachments = new List<(string Name, byte[] Data, bool IsImage)>();

        var catRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var catBtns = new List<(string Id, Button Btn)>();

        var bodyBox = new TextBox
        {
            Text = _library.Prefs.FeedbackDraftText,
            PlaceholderText = "哪本书、哪一页，做了什么，结果怎样（支持拖拽或选择最多 3 张截图/附件）",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 116,
            MaxLength = 800,
        };
        var counterText = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right };
        bodyBox.TextChanged += (_, _) =>
        {
            _library.Prefs.FeedbackDraftText = bodyBox.Text;
            counterText.Text = bodyBox.Text.Length >= 500 ? $"{bodyBox.Text.Length} / 800" : "";
        };

        foreach (var (id, label, hint) in new[]
        {
            ("bug", "问题", "哪本书、哪一页，做了什么，结果怎样"),
            ("idea", "建议", "想要什么功能，或者哪里可以更顺手"),
            ("other", "其他", "想说的都可以"),
        })
        {
            string cid = id, ph = hint;
            var b = new Button { Content = label, Height = 28, MinHeight = 28, Padding = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(14) };
            b.Click += (_, _) =>
            {
                category = cid;
                bodyBox.PlaceholderText = ph;
                foreach (var (k, btn) in catBtns)
                    btn.Style = (Style)Application.Current.Resources[k == category ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];
            };
            catBtns.Add((cid, b));
            catRow.Children.Add(b);
        }
        foreach (var (k, btn) in catBtns)
            btn.Style = (Style)Application.Current.Resources[k == category ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];

        var attachList = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var publicShotToggle = new CheckBox { Content = "截图可公开附在 GitHub Issue 中（已自动剥离 EXIF 位置信息）", IsChecked = true, FontSize = 11.5, Visibility = Visibility.Collapsed };
        publicShotToggle.Checked += (_, _) => publicShots = true;
        publicShotToggle.Unchecked += (_, _) => publicShots = false;

        void RebuildAttachments()
        {
            attachList.Children.Clear();
            publicShotToggle.Visibility = attachments.Any(a => a.IsImage) ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < attachments.Count; i++)
            {
                int idx = i;
                var att = attachments[i];
                var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                chip.Children.Add(new TextBlock { Text = $"{att.Name} ({att.Data.Length / 1024} KB)", FontSize = 11, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
                var rm = new Button { Content = "×", Padding = new Thickness(4, 0, 4, 0), FontSize = 11, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
                rm.Click += (_, _) => { attachments.RemoveAt(idx); RebuildAttachments(); };
                chip.Children.Add(rm);
                attachList.Children.Add(new Border { CornerRadius = new CornerRadius(7), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(8, 3, 4, 3), Child = chip });
            }
        }

        var addFileBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "＋ 添加截图或文件", Height = 26, FontSize = 11.5 };
        addFileBtn.Click += async (_, _) =>
        {
            if (attachments.Count >= 3) return;
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".log");
            picker.FileTypeFilter.Add(".txt");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(ownerWindow));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            string ext = Path.GetExtension(f.Name).ToLowerInvariant();
            bool isImg = ext is ".png" or ".jpg" or ".jpeg";
            byte[] raw = await File.ReadAllBytesAsync(f.Path);
            if (isImg) raw = await SanitizeImageToJpegAsync(f, raw);
            if (raw.Length > 1_500_000) return;
            attachments.Add((isImg ? Path.ChangeExtension(f.Name, ".jpg") : f.Name, raw, isImg));
            RebuildAttachments();
        };

        var snapWindowBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "📷 截取当前窗口", Height = 26, FontSize = 11.5 };
        snapWindowBtn.Click += async (_, _) =>
        {
            if (attachments.Count >= 3 || ownerWindow.Content is not UIElement rootEl) return;
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
                var bytes = new byte[ms.Size];
                await ms.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
                attachments.Add(($"shot-{attachments.Count + 1}.jpg", bytes, true));
                RebuildAttachments();
            }
            catch { }
        };

        var infoCheck = new CheckBox
        {
            Content = $"附带基础环境信息（语阅 v{CurrentVersion} · Windows {Environment.OSVersion.Version}）",
            IsChecked = true,
            FontSize = 11.5,
        };
        infoCheck.Checked += (_, _) => withDeviceInfo = true;
        infoCheck.Unchecked += (_, _) => withDeviceInfo = false;

        var mailLink = new HyperlinkButton
        {
            Content = "需要回复或不想公开？直接发邮件 ↗",
            NavigateUri = new Uri("mailto:yuyue.app@icloud.com"),
            FontSize = 11.5,
            Padding = new Thickness(0, 2, 0, 2),
        };

        var toolBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { snapWindowBtn, addFileBtn } };
        var root = new StackPanel
        {
            Width = 468,
            Spacing = 10,
            Children =
            {
                catRow,
                bodyBox,
                counterText,
                toolBar,
                attachList,
                publicShotToggle,
                infoCheck,
                mailLink,
                new TextBlock
                {
                    Text = "公开文字会整理为 GitHub Issue 便于追踪修复；不收集邮箱，私密附件与日志全程使用 X25519 公钥加密传输。",
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T3),
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "写反馈",
            Content = root,
            PrimaryButtonText = "发送反馈",
            SecondaryButtonText = "在 GitHub 打开 Issue ↗",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.None) return;

        string text = bodyBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        if (res == ContentDialogResult.Primary)
        {
            bool sentViaRelay = await TrySubmitViaRelayAsync(category, text, withDeviceInfo, publicShots, attachments);
            _library.Prefs.FeedbackDraftText = "";
            _library.SavePrefs();
            if (!sentViaRelay)
            {
                OpenGitHubIssue(category, text, withDeviceInfo);
            }
        }
        else if (res == ContentDialogResult.Secondary)
        {
            _library.Prefs.FeedbackDraftText = "";
            _library.SavePrefs();
            OpenGitHubIssue(category, text, withDeviceInfo);
        }
    }

    private async Task<bool> TrySubmitViaRelayAsync(
        string category, string text, bool withInfo, bool publicShots,
        List<(string Name, byte[] Data, bool IsImage)> attachments)
    {
        string? topic = Environment.GetEnvironmentVariable("YuyueFeedbackTopic");
        string? pubKey = Environment.GetEnvironmentVariable("YuyueFeedbackPublicKey");
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(pubKey))
            return false;

        try
        {
            var fileIds = new List<string>();
            for (int i = 0; i < attachments.Count; i++)
            {
                var att = attachments[i];
                bool plainShot = publicShots && att.IsImage;
                byte[] payload = plainShot ? att.Data : YuyueCoreMethods.SealFeedbackFile(pubKey, att.Name, att.Data);
                using var putReq = new HttpRequestMessage(HttpMethod.Put, $"https://ntfy.sh/{topic}")
                {
                    Content = new ByteArrayContent(payload),
                };
                putReq.Headers.Add("X-Firebase", "no");
                putReq.Headers.Add("Filename", plainShot ? $"shot-{i + 1}.jpg" : "f.bin");
                putReq.Headers.Add("X-Message", plainShot ? "v2shot" : "v1file");
                using var putResp = await Http.SendAsync(putReq);
                putResp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await putResp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.GetString() is string fid)
                    fileIds.Add(fid);
            }

            var draft = new FeedbackDraft(
                Category: category,
                Text: text,
                AppVersion: withInfo ? CurrentVersion : "",
                Platform: withInfo ? "Windows" : "",
                OsVersion: withInfo ? Environment.OSVersion.VersionString : "",
                Files: fileIds.ToArray());

            string sealedJson = YuyueCoreMethods.FeedbackMessage(draft);
            try
            {
                using var postReq = new HttpRequestMessage(HttpMethod.Post, $"https://ntfy.sh/{topic}")
                {
                    Content = new StringContent(sealedJson, Encoding.UTF8, "application/json"),
                };
                postReq.Headers.Add("X-Firebase", "no");
                using var postResp = await Http.SendAsync(postReq);
                if ((int)postResp.StatusCode == 429 || !postResp.IsSuccessStatusCode)
                    EnqueueOfflineFeedback(topic, sealedJson);
            }
            catch
            {
                EnqueueOfflineFeedback(topic, sealedJson);
            }
            return true;
        }
        catch
        {
            return false;
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

    private static void OpenGitHubIssue(string category, string text, bool withInfo)
    {
        string firstLine = text.Split('\n')[0].Trim();
        if (firstLine.Length > 48) firstLine = firstLine[..48] + "…";
        string tag = category switch { "idea" => "[建议] ", "other" => "[反馈] ", _ => "[问题] " };
        string body = withInfo
            ? $"{text}\n\n---\n- 平台：Windows ({Environment.OSVersion.VersionString})\n- 版本：语阅 v{CurrentVersion}"
            : text;
        string url = $"https://github.com/{RepoSlug}/issues/new?title={Uri.EscapeDataString(tag + firstLine)}&body={Uri.EscapeDataString(body)}";
        _ = Launcher.LaunchUriAsync(new Uri(url));
    }

    // Strips EXIF metadata and downscales to <= 1600px JPEG <= 1 MB (1:1 Feedback.swift)
    private static async Task<byte[]> SanitizeImageToJpegAsync(StorageFile file, byte[] fallback)
    {
        try
        {
            using var inStream = await file.OpenReadAsync();
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
        return fallback;
    }
}
