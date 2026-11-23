using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Yuyue.Core;
using Yuyue.Design;

namespace Yuyue.Reader;

public sealed record RecapDto(string[] Lines, int Days);

public sealed record SleepReturnInfo(uint Section, int Sentence, string When, string Place);

/// <summary>
/// 1:1 implementation of macOS <c>ReaderRecap.swift</c> (<c>前情提要</c>),
/// <c>ReaderFlag.swift</c> (<c>SleepReturnCard</c> <c>回到昨晚</c>), and
/// <c>BookFailure.swift</c> (pre-window book validation &amp; friendly recovery).
/// </summary>
public static class ReaderRecapAndSleepHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // ---------- 1. 前情提要 (ReaderRecap.swift) ----------

    private static string GetRecapCachePath(LibraryService library, string bookId)
    {
        string dir = Path.Combine(library.DataDir, "companion");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{bookId}.recap.json");
    }

    private static Dictionary<string, RecapDto> LoadRecapCache(LibraryService library, string bookId)
    {
        try
        {
            string path = GetRecapCachePath(library, bookId);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, RecapDto>>(File.ReadAllText(path)) ?? new();
        }
        catch { }
        return new();
    }

    private static void SaveRecapCache(LibraryService library, string bookId, Dictionary<string, RecapDto> cache)
    {
        try
        {
            string path = GetRecapCachePath(library, bookId);
            File.WriteAllText(path, JsonSerializer.Serialize(cache, JsonOpts));
        }
        catch { }
    }

    public static RecapDto? TryGetCachedRecap(LibraryService library, string bookId, uint section, int sentence, int days)
    {
        string key = $"recap|{section}|{Math.Max(0, sentence) / 20}";
        var cache = LoadRecapCache(library, bookId);
        if (cache.TryGetValue(key, out var cached) && cached.Lines.Length >= 2)
            return cached with { Days = Math.Max(1, days) };
        return null;
    }

    public static async Task<RecapDto?> TryLoadOrGenerateRecapAsync(
        LibraryService library,
        string bookId,
        BookDetail detail,
        long? lastOpenedMs,
        uint section,
        int sentence,
        bool manual = false)
    {
        if (!library.Companion.IsConfigured) return null;

        int days = 1;
        if (lastOpenedMs is long ms && ms > 0)
        {
            long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            days = (int)((nowSec - ms / 1000.0) / 86400.0);
        }
        else if (!manual)
        {
            return null;
        }

        if (!manual)
        {
            if (days < 1) return null;
            if (section <= detail.Start && sentence <= 20) return null;
        }
        else
        {
            days = Math.Max(1, days);
        }

        string key = $"recap|{section}|{Math.Max(0, sentence) / 20}";
        var cache = LoadRecapCache(library, bookId);
        if (cache.TryGetValue(key, out var cached) && cached.Lines.Length >= 2)
        {
            return cached with { Days = days };
        }

        string text = await Task.Run(() => ReadBefore(library.Core, bookId, detail, section, sentence));
        if (text.Length < 200) return null;

        string replyLang = CompanionClient.ReplyLanguage;
        string system =
            $"你是「语阅」里的书伴。读者隔了 {days} 天又打开《{detail.Summary.Title}》，帮他想起之前读到哪、发生了什么。\n" +
            "规则：\n" +
            "1. 只根据下面的原文，不要提后面的情节，不要剧透。\n" +
            $"2. 写三句话，每句一行，每句不超过 30 个字（英文不超过 20 个词），用{replyLang}，按发生的先后。\n" +
            "3. 只写这三行，不要标题，不要编号，不要别的话。";

        try
        {
            string reply = await library.Companion.ChatAsync(
                "原文（读者读过的最后一部分）：\n" + text,
                system,
                maxTokens: 512,
                fast: true,
                source: UsageSource.Recap);
            char[] trimChars = [' ', '\t', '-', '•', '*', '·', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', '、', '）', ')'];
            var lines = reply
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim().TrimStart(trimChars).Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Take(3)
                .ToArray();

            if (lines.Length < 2) return null;

            var recap = new RecapDto(lines, days);
            cache[key] = recap;
            SaveRecapCache(library, bookId, cache);
            return recap;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadBefore(YuyueCore core, string bookId, BookDetail detail, uint startSec, int startSent)
    {
        var parts = new List<string>();
        int count = 0;
        uint sec = startSec;
        int upTo = Math.Max(0, startSent);

        while (count < 2400)
        {
            string[] all;
            try { all = core.Sentences(bookId, sec); }
            catch { all = []; }

            int end = Math.Min(all.Length, upTo == int.MaxValue ? all.Length : upTo + 1);
            if (end > 0)
            {
                var taken = new List<string>();
                for (int i = end - 1; i >= 0 && count < 2400; i--)
                {
                    taken.Add(all[i]);
                    count += all[i].Length;
                }
                taken.Reverse();
                parts.Insert(0, string.Concat(taken));
            }

            if (count >= 2400 || sec <= detail.Start) break;
            uint? prev;
            try { prev = core.PreviousSection(bookId, sec, false); }
            catch { prev = null; }
            if (prev is not uint pSec) break;
            sec = pSec;
            upTo = int.MaxValue;
        }

        return string.Join("\n", parts).Replace("\n\n", "\n");
    }

    public static Border BuildRecapCard(RecapDto recap, YYPalette p, YYTintColors tint, bool manualOpened, Action onDone)
    {
        var root = new StackPanel { Spacing = 0 };

        // Header: icon + 前情提要 + N 天没读了
        var head = new Grid();
        var headLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        headLeft.Children.Add(YYIcons.Make("sparkle", 12, ThemeHelper.Brush(tint.Accent)));
        headLeft.Children.Add(new TextBlock
        {
            Text = "前情提要",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
        });
        head.Children.Add(headLeft);
        head.Children.Add(new TextBlock
        {
            Text = manualOpened ? "截至当前位置" : $"{recap.Days} 天没读了",
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(head);

        // 3 spoiler-free lines
        var linesStack = new StackPanel { Spacing = 6, Margin = new Thickness(0, 11, 0, 11) };
        for (int i = 0; i < recap.Lines.Length; i++)
        {
            var g = new Grid { ColumnSpacing = 7 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock
            {
                Text = $"{i + 1}.",
                FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(0, 2, 0, 0),
            });
            var lineTb = new TextBlock
            {
                Text = recap.Lines[i],
                FontSize = 13.5,
                Foreground = ThemeHelper.Brush(p.T1),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
            };
            Grid.SetColumn(lineTb, 1);
            g.Children.Add(lineTb);
            linesStack.Children.Add(g);
        }
        root.Children.Add(linesStack);

        root.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });

        // Footer: 只提要你读过的部分 + 接着读
        var foot = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        foot.Children.Add(new TextBlock
        {
            Text = "只提要你读过的部分",
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var doneBtn = new Button
        {
            Content = "接着读",
            Height = 30,
            MinHeight = 30,
            Padding = new Thickness(15, 0, 15, 0),
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(tint.Black),
            Foreground = ThemeHelper.Brush(tint.OnBlack),
            BorderThickness = new Thickness(0),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            UseSystemFocusVisuals = false,
        };
        doneBtn.Click += (_, _) => onDone();
        foot.Children.Add(doneBtn);
        root.Children.Add(foot);

        return new Border
        {
            Width = 372,
            CornerRadius = new CornerRadius(16),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14, 16, 12),
            Child = root,
        };
    }

    // ---------- 2. 回到昨晚睡眠起点 (ReaderFlag.swift:166-294) ----------

    public static void RememberSleepStop(LibraryService library, string bookId, uint armedSection, int armedSentence)
    {
        library.Prefs.SleepReturnSpots[bookId] =
        [
            armedSection,
            Math.Max(0, armedSentence),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ];
        library.SavePrefs();
    }

    public static void ClearSleepReturn(LibraryService library, string bookId)
    {
        if (library.Prefs.SleepReturnSpots.Remove(bookId))
            library.SavePrefs();
    }

    public static SleepReturnInfo? TryGetSleepReturn(LibraryService library, string bookId, BookDetail? detail = null)
    {
        if (!library.Prefs.SleepReturnSpots.TryGetValue(bookId, out var v) || v is null || v.Length < 3)
            return null;

        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (nowSec - v[2] >= 7 * 86400) return null;

        uint sec = (uint)Math.Max(0, v[0]);
        int sent = (int)Math.Max(0, v[1]);
        var dt = DateTimeOffset.FromUnixTimeSeconds(v[2]).LocalDateTime;

        string when;
        if (dt.Date == DateTime.Today)
            when = "刚才";
        else if (dt.Date == DateTime.Today.AddDays(-1))
            when = dt.Hour >= 18 ? "昨晚" : "昨天";
        else
            when = $"{dt.Month}月{dt.Day}日";

        string rawChap = detail is not null && sec < detail.Sections.Length ? (detail.Sections[sec].Title ?? "") : "";
        string chap = ShortChapterTitle(rawChap);
        string at = $"第 {sent + 1} 句";
        string place = string.IsNullOrWhiteSpace(chap) ? at : $"{chap} {at}";

        return new SleepReturnInfo(sec, sent, when, place);
    }

    private static string ShortChapterTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string t = raw.Trim();
        var parts = t.Split([' ', '\u3000'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts[0].Length <= 8) return parts[0];
        return t.Length <= 12 ? t : t[..12];
    }

    public static Border BuildSleepReturnCard(SleepReturnInfo info, YYPalette p, YYTintColors tint, Action<bool> onAnswer)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

        var tb = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        tb.Inlines.Add(new Run { Text = $"{info.When}定时从 " });
        tb.Inlines.Add(new Run { Text = info.Place, FontWeight = FontWeights.SemiBold });
        tb.Inlines.Add(new Run { Text = " 开始，之后睡着了？" });
        row.Children.Add(tb);

        var goBtn = new Button
        {
            Content = "回到那里",
            Height = 25,
            MinHeight = 25,
            Padding = new Thickness(11, 0, 11, 0),
            CornerRadius = new CornerRadius(12.5),
            Background = ThemeHelper.Brush(tint.Black),
            Foreground = ThemeHelper.Brush(tint.OnBlack),
            BorderThickness = new Thickness(0),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            UseSystemFocusVisuals = false,
        };
        goBtn.Click += (_, _) => onAnswer(true);

        var dismissBtn = new Button
        {
            Content = "不用",
            Height = 25,
            MinHeight = 25,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(12.5),
            Background = ThemeHelper.Brush(p.Inset),
            Foreground = ThemeHelper.Brush(p.T2),
            BorderThickness = new Thickness(0),
            FontSize = 11,
            UseSystemFocusVisuals = false,
        };
        dismissBtn.Click += (_, _) => onAnswer(false);

        row.Children.Add(goBtn);
        row.Children.Add(dismissBtn);

        return new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 5, 6, 5),
            Child = row,
        };
    }

    // ---------- 3. 打开书籍前校验与友好恢复卡片 (BookFailure.swift) ----------

    public static bool TryValidateBookOpen(LibraryService library, string bookId, BookFormat format, out string title, out string detail)
    {
        title = "";
        detail = "";
        try
        {
            if (format == BookFormat.Pdf)
            {
                string? orig = library.TryOriginalFile(bookId);
                if (string.IsNullOrWhiteSpace(orig) || !File.Exists(orig))
                {
                    title = "找不到这本书的文件";
                    detail = "PDF 原文件不在本机书库中（如果是从另一台设备同步过来的进度，请把原文件重新拖入书架或开启「同步书文件」）。";
                    return false;
                }
                return true;
            }

            if (format == BookFormat.Audio)
            {
                var rec = library.Core.AudioRecording(bookId);
                if (rec.Files.Length == 0 || rec.Files.All(f => !File.Exists(f.Path)))
                {
                    title = "找不到有声书的音频文件";
                    detail = "本机还没有这本书的录音音频文件，请重新选择音频文件夹或 .m4b 文件导入。";
                    return false;
                }
                return true;
            }

            var d = library.GetBookDetail(bookId);
            if (d.Sections.Length == 0)
            {
                title = "读不到这本书的正文";
                detail = "文件里没有解析出可读章节，可以尝试重新导入其他版本。";
                return false;
            }
            return true;
        }
        catch (CoreException.NotFound)
        {
            title = "找不到这本书的文件";
            detail = "原书文件不在本机书库里了（若来自多端仅进度同步，请在本机重新导入同名电子书自动汇合进度）。";
            return false;
        }
        catch (CoreException.Protected)
        {
            title = "这本书受加密保护";
            detail = "文件带有 DRM 数字版权保护，语阅无法直接解开阅读。请使用未加密的电子书文件。";
            return false;
        }
        catch (CoreException.Unsupported)
        {
            title = "这种文件打不开";
            detail = "暂不支持该电子书的内部封装格式，建议转换为标准 EPUB 或 PDF 后重新导入。";
            return false;
        }
        catch (CoreException.BadBook bad)
        {
            bool tooBig = bad.detail.Contains("large", StringComparison.OrdinalIgnoreCase) || bad.detail.Contains("size", StringComparison.OrdinalIgnoreCase);
            title = tooBig ? "这本书太大了" : "读不到这个文件";
            detail = string.IsNullOrWhiteSpace(bad.detail) ? "文件可能已损坏或结构不完整。" : bad.detail;
            return false;
        }
        catch (Exception ex)
        {
            title = "这本书打不开";
            detail = ex.Message;
            return false;
        }
    }

    public static async Task ShowBookOpenFailureAsync(
        Window ownerWindow,
        XamlRoot xamlRoot,
        LibraryService library,
        string bookTitle,
        string failTitle,
        string failDetail,
        Func<string, Task>? onReimported = null)
    {
        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = failTitle,
            Content = new StackPanel
            {
                Spacing = 8,
                MaxWidth = 380,
                Children =
                {
                    new TextBlock { Text = $"《{bookTitle}》", FontSize = 13, FontWeight = FontWeights.SemiBold },
                    new TextBlock
                    {
                        Text = failDetail,
                        FontSize = 12.5,
                        Foreground = (Brush)Application.Current.Resources["YYText2Brush"],
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 20,
                    },
                },
            },
            PrimaryButtonText = "重新导入…",
            CloseButtonText = "先放着",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
        foreach (var ext in LibraryService.SupportedExtensions) picker.FileTypeFilter.Add(ext);
        foreach (var ext in Yuyue.Audiobook.AudiobookImporter.Extensions) picker.FileTypeFilter.Add(ext);
        picker.FileTypeFilter.Add(".zip");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(ownerWindow));
        var picked = await picker.PickSingleFileAsync();
        if (picked is null) return;

        try
        {
            var imported = Yuyue.Audiobook.AudiobookImporter.IsAudiobookOrArchivePath(picked.Path)
                ? await Yuyue.Audiobook.AudiobookImporter.ImportFilesAsync(library, [picked.Path])
                : await library.ImportBookWithExtrasAsync(picked.Path);
            if (onReimported is not null) await onReimported(imported.Id);
        }
        catch (Exception ex)
        {
            var errDlg = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "导入没成功",
                Content = ex.Message,
                CloseButtonText = "知道了",
            };
            await errDlg.ShowAsync();
        }
    }

    // ---------- 4. 异地进度冲突卡片 ElsewhereCard & TXT 编码存疑提示条 DoubtPill (ReaderReturn.swift) ----------

    public static Border BuildElsewhereCard(
        Progress latest,
        double hereFraction,
        string? chapterTitle,
        string? excerpt,
        YYPalette p,
        YYTintColors tint,
        Action onStay,
        Action onJump)
    {
        string devName = string.IsNullOrWhiteSpace(latest.DeviceName) ? "另一台设备" : latest.DeviceName;
        var atDt = DateTimeOffset.FromUnixTimeMilliseconds(latest.At).LocalDateTime;
        var delta = DateTime.Now - atDt;
        string ago = delta.TotalMinutes < 1 ? "刚刚"
            : delta.TotalMinutes < 60 ? $"{(int)delta.TotalMinutes} 分钟前"
            : atDt.Date == DateTime.Today ? $"今天 {atDt:HH:mm}"
            : atDt.Date == DateTime.Today.AddDays(-1) ? $"昨天 {atDt:HH:mm}"
            : $"{atDt.Month}月{atDt.Day}日";

        var root = new StackPanel { Spacing = 8 };
        var head = new Grid();
        var hLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        hLeft.Children.Add(YYIcons.Make("laptop", 12, ThemeHelper.Brush(p.T2)));
        hLeft.Children.Add(new TextBlock
        {
            Text = $"{devName} 上读到了更后面",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
        });
        head.Children.Add(hLeft);
        head.Children.Add(new TextBlock
        {
            Text = ago,
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(p.T3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        });
        root.Children.Add(head);

        if (!string.IsNullOrWhiteSpace(excerpt))
        {
            var exGrid = new Grid { ColumnSpacing = 8 };
            exGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
            exGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            exGrid.Children.Add(new Border { Background = ThemeHelper.Brush(p.Line), CornerRadius = new CornerRadius(1) });
            var exTb = new TextBlock
            {
                Text = excerpt,
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = 12.5,
                LineHeight = 19,
                Foreground = ThemeHelper.Brush(p.T1),
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(exTb, 1);
            exGrid.Children.Add(exTb);
            root.Children.Add(exGrid);
        }

        root.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });

        var foot = new Grid();
        int latestPct = Math.Clamp((int)Math.Round(latest.Fraction * 100), 1, 100);
        int herePct = Math.Clamp((int)Math.Round(hereFraction * 100), 0, 100);
        string meta = $"{(string.IsNullOrWhiteSpace(chapterTitle) ? "" : chapterTitle + " · ")}{latestPct}% · 这台在 {herePct}%";
        foot.Children.Add(new TextBlock
        {
            Text = meta,
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 130, 0),
        });

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        var stayBtn = new Button
        {
            Content = "留在这里",
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Foreground = ThemeHelper.Brush(p.T2),
            BorderThickness = new Thickness(0),
            FontSize = 11.5,
            UseSystemFocusVisuals = false,
        };
        stayBtn.Click += (_, _) => onStay();

        var jumpBtn = new Button
        {
            Content = "跳过去",
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(tint.Black),
            Foreground = ThemeHelper.Brush(tint.OnBlack),
            BorderThickness = new Thickness(0),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            UseSystemFocusVisuals = false,
        };
        jumpBtn.Click += (_, _) => onJump();

        btns.Children.Add(stayBtn);
        btns.Children.Add(jumpBtn);
        foot.Children.Add(btns);
        root.Children.Add(foot);

        return new Border
        {
            Width = 372,
            CornerRadius = new CornerRadius(16),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14, 16, 12),
            Child = root,
        };
    }

    public static Border BuildDoubtPill(string doubt, YYPalette p, Action onOpen, Action onDismiss)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var openSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        openSp.Children.Add(new TextBlock { Text = "⚠", FontSize = 11.5, Foreground = ThemeHelper.Brush(Windows.UI.Color.FromArgb(255, 217, 148, 38)) });
        openSp.Children.Add(new TextBlock { Text = doubt, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxWidth = 260, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        openSp.Children.Add(new TextBlock { Text = "去看看", FontSize = 12, Foreground = ThemeHelper.Brush(p.T3) });

        var openBtn = new Button
        {
            Content = openSp,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(12, 0, 6, 0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
        };
        openBtn.Click += (_, _) => onOpen();

        var closeBtn = new Button
        {
            Content = YYIcons.Make("xmark", 9.5, ThemeHelper.Brush(p.T3)),
            Width = 28,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
        };
        closeBtn.Click += (_, _) => onDismiss();

        row.Children.Add(openBtn);
        row.Children.Add(closeBtn);

        return new Border
        {
            CornerRadius = new CornerRadius(17),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Child = row,
        };
    }

    // ---------- 5. 读完归档卡片 FinishedSheet (MacReaderView.swift:54-58) ----------

    public static async Task ShowFinishedSheetAsync(XamlRoot xamlRoot, LibraryService library, BookDetail detail)
    {
        bool dark = ThemeHelper.IsDark(library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        byte? pickedRating = detail.Summary.Rating;
        var starsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        var starBtns = new List<Button>();

        void RefreshStars()
        {
            for (int i = 0; i < starBtns.Count; i++)
            {
                bool lit = pickedRating is byte r && (i + 1) <= r;
                starBtns[i].Foreground = ThemeHelper.Brush(lit ? Windows.UI.Color.FromArgb(255, 226, 160, 38) : p.T3);
                starBtns[i].Content = lit ? "★" : "☆";
            }
        }

        for (int s = 1; s <= 5; s++)
        {
            byte val = (byte)s;
            var sb = new Button
            {
                FontSize = 22,
                Padding = new Thickness(4, 0, 4, 0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
            };
            sb.Click += (_, _) =>
            {
                pickedRating = pickedRating == val ? null : val;
                RefreshStars();
            };
            starBtns.Add(sb);
            starsPanel.Children.Add(sb);
        }
        RefreshStars();

        var commentBox = new TextBox
        {
            PlaceholderText = "写一句读完后的感想（可选，保存在「进度」页）…",
            Text = detail.Summary.Comment ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 76,
            FontSize = 12.5,
        };

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = $"读完了《{detail.Summary.Title}》",
            Content = new StackPanel
            {
                Spacing = 12,
                Width = 360,
                Children =
                {
                    new TextBlock
                    {
                        Text = "标为「读过」并留下一颗星或一句话，随时可在书架「进度」页翻看。",
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.T2),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    starsPanel,
                    commentBox,
                },
            },
            PrimaryButtonText = "标为读过",
            CloseButtonText = "先不标",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                library.SetStatus(detail.Summary.Id, ReadingStatus.Finished);
                library.Core.SetRating(detail.Summary.Id, pickedRating);
                string cmt = commentBox.Text.Trim();
                library.Core.SetComment(detail.Summary.Id, string.IsNullOrEmpty(cmt) ? null : cmt);
                App.CurrentApp.NotifyShelfRefresh();
            }
            catch { }
        }
    }
}
