using System.Text;
using Windows.Storage.Pickers;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Yuyue.Design;

namespace Yuyue.Core;

/// <summary>
/// 1:1 port of <c>Snapshots.swift</c>, <c>SnapshotSheets.swift</c>, and <c>ConflictSheet.swift</c>:
/// - <c>SnapshotImportSheet</c> (400pt): inspect <c>.yuyue</c> archive, compare <c>合并</c> vs <c>覆盖</c> (auto-creating pre-overwrite backup!),
/// - <c>ReadingExportSheet</c> (400×470): Goodreads / StoryGraph CSV preview &amp; export,
/// - <c>NoteConflictsSheet</c> (440×600): side-by-side device note conflict resolver with character-level diff highlighting.
/// </summary>
public static class SnapshotAndConflictDialogs
{
    public static string GetSnapshotsFolder()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(docs)) docs = App.CurrentApp.Library.DataDir;
        return docs;
    }

    public static async Task<string> CreateSnapshotNowAsync(LibraryService library, SnapshotParts parts)
    {
        string dir = Path.Combine(library.DataDir, "snapshots");
        Directory.CreateDirectory(dir);
        string fileName = $"语阅快照-{DateTime.Now:yyyyMMdd-HHmmss}.yuyue";
        string fullPath = Path.Combine(dir, fileName);
        await Task.Run(() => library.Core.MakeSnapshot(fullPath, parts));
        return fullPath;
    }

    public static async Task<bool> ShowSnapshotImportDialogAsync(Window ownerWindow, XamlRoot xamlRoot, LibraryService library, string? presetPath = null)
    {
        string? snapPath = presetPath;
        if (string.IsNullOrEmpty(snapPath) || !File.Exists(snapPath))
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".yuyue");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(ownerWindow));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return false;
            snapPath = f.Path;
        }

        SnapshotInfo info;
        SnapshotCounts localCounts;
        try
        {
            info = await Task.Run(() => library.Core.ReadSnapshot(snapPath));
            localCounts = library.Core.SnapshotCounts();
        }
        catch (Exception ex)
        {
            await new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "无法读取快照文件",
                Content = ex.Message,
                CloseButtonText = "好",
            }.ShowAsync();
            return false;
        }

        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);
        bool replaceMode = false;

        var madeTime = DateTimeOffset.FromUnixTimeMilliseconds(info.MadeAt).LocalDateTime;
        string sizeStr = info.Bytes >= 1024 * 1024 ? $"{info.Bytes / 1048576.0:0.1} MB" : $"{Math.Max(1, info.Bytes / 1024.0):0} KB";

        var partTags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void AddTag(string t) => partTags.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Inset),
            Padding = new Thickness(7, 2, 7, 2),
            Child = new TextBlock { Text = t, FontSize = 11, Foreground = ThemeHelper.Brush(p.T2) },
        });
        if (info.Parts.Progress) AddTag("进度和书签");
        if (info.Parts.Notes) AddTag("笔记");
        if (info.Parts.Words) AddTag("生词");
        if (info.Parts.Settings) AddTag("设置与规则");

        var mergeCard = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(1.5) };
        var replaceCard = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), BorderThickness = new Thickness(1.5) };
        var diffText = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap };

        mergeCard.Child = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "合并到当前书库（推荐）", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) },
                new TextBlock { Text = "保留本机已有书与笔记，把快照里更新的进度和缺少的划线合并进来。", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap },
            },
        };
        replaceCard.Child = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "完全覆盖本机数据", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) },
                new TextBlock { Text = "恢复前会自动为你生成一份「覆盖前」安全备份快照，随时可撤回。", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap },
            },
        };

        void SyncModeVisuals()
        {
            mergeCard.BorderBrush = ThemeHelper.Brush(!replaceMode ? tint.Accent : p.Line);
            mergeCard.Background = ThemeHelper.Brush(!replaceMode ? tint.Soft : p.Card);
            replaceCard.BorderBrush = ThemeHelper.Brush(replaceMode ? p.Danger : p.Line);
            replaceCard.Background = ThemeHelper.Brush(replaceMode ? Color.FromArgb(20, p.Danger.R, p.Danger.G, p.Danger.B) : p.Card);

            diffText.Text = replaceMode
                ? $"覆盖后：书 {localCounts.Books} → {info.Counts.Books} 本 · 笔记 {localCounts.Notes} → {info.Counts.Notes} 条 · 生词 {localCounts.Words} → {info.Counts.Words} 个"
                : $"合并预览：本机现有 {localCounts.Books} 本书 / {localCounts.Notes} 条笔记 / {localCounts.Words} 个生词，将与快照（{info.Counts.Books} 本 / {info.Counts.Notes} 条 / {info.Counts.Words} 词）无损合并";
        }

        mergeCard.PointerPressed += (_, _) => { replaceMode = false; SyncModeVisuals(); };
        replaceCard.PointerPressed += (_, _) => { replaceMode = true; SyncModeVisuals(); };
        SyncModeVisuals();

        var root = new StackPanel
        {
            Width = 410,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = $"{info.DeviceName} ({info.Platform}) · {madeTime:yyyy-MM-dd HH:mm} · {sizeStr}", FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) },
                partTags,
                mergeCard,
                replaceCard,
                diffText,
            },
        };

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "从快照恢复",
            Content = root,
            PrimaryButtonText = "开始恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return false;

        try
        {
            if (replaceMode)
            {
                // Mandatory safety snapshot before overwrite (1:1 Snapshots.swift)
                string safetyDir = Path.Combine(library.DataDir, "snapshots");
                Directory.CreateDirectory(safetyDir);
                string safetyPath = Path.Combine(safetyDir, $"语阅快照-覆盖前-{DateTime.Now:yyyyMMdd-HHmmss}.yuyue");
                await Task.Run(() => library.Core.MakeSnapshot(safetyPath, new SnapshotParts(true, true, true, true, false)));
            }
            await Task.Run(() => library.Core.RestoreSnapshot(snapPath, replaceMode));
            App.CurrentApp.NotifyShelfRefresh();
            return true;
        }
        catch (Exception ex)
        {
            await new ContentDialog { XamlRoot = xamlRoot, Title = "恢复快照失败", Content = ex.Message, CloseButtonText = "好" }.ShowAsync();
            return false;
        }
    }

    public static async Task ShowReadingExportSheetAsync(Window ownerWindow, XamlRoot xamlRoot, LibraryService library)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        ReadingSite site = ReadingSite.Goodreads;
        int utcOffsetMin = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;

        var goodBtn = new Button { Content = "Goodreads", HorizontalAlignment = HorizontalAlignment.Stretch };
        var storyBtn = new Button { Content = "The StoryGraph", HorizontalAlignment = HorizontalAlignment.Stretch };
        var segGrid = new Grid { ColumnSpacing = 8 };
        segGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        segGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        segGrid.Children.Add(goodBtn);
        Grid.SetColumn(storyBtn, 1);
        segGrid.Children.Add(storyBtn);

        var statsText = new TextBlock { FontSize = 12, Foreground = ThemeHelper.Brush(p.T1) };
        var previewText = new TextBlock
        {
            FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(p.T2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 5,
        };
        var isbnNote = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap };
        ReadingExport currentExport = library.Core.ReadingExport(site, utcOffsetMin);

        void RebuildExportPreview()
        {
            goodBtn.Style = (Style)Application.Current.Resources[site == ReadingSite.Goodreads ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];
            storyBtn.Style = (Style)Application.Current.Resources[site == ReadingSite.StoryGraph ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];
            currentExport = library.Core.ReadingExport(site, utcOffsetMin);
            statsText.Text = $"想读 {currentExport.Want} 本  ·  在读 {currentExport.Reading} 本  ·  读完 {currentExport.Finished} 本";
            previewText.Text = string.Join("\n", currentExport.Csv.Split('\n').Take(4));
            isbnNote.Text = currentExport.WithoutIsbn > 0
                ? $"其中 {currentExport.WithoutIsbn} 本没有 ISBN，导入后将按书名和作者匹配。"
                : "全部书目均包含标准书名与状态字段。";
        }

        goodBtn.Click += (_, _) => { site = ReadingSite.Goodreads; RebuildExportPreview(); };
        storyBtn.Click += (_, _) => { site = ReadingSite.StoryGraph; RebuildExportPreview(); };
        RebuildExportPreview();

        var root = new StackPanel
        {
            Width = 400,
            Spacing = 10,
            Children =
            {
                segGrid,
                statsText,
                new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(p.Inset),
                    Padding = new Thickness(10, 8, 10, 8),
                    Child = previewText,
                },
                isbnNote,
            },
        };

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "导出阅读记录 (CSV)",
            Content = root,
            PrimaryButtonText = "保存 CSV…",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = site == ReadingSite.Goodreads ? "语阅-goodreads" : "语阅-storygraph",
            };
            picker.FileTypeChoices.Add("CSV 表格", [".csv"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(ownerWindow));
            var f = await picker.PickSaveFileAsync();
            if (f is not null)
                await File.WriteAllTextAsync(f.Path, currentExport.Csv, Encoding.UTF8);
        }
    }

    // ---------- 1:1 ConflictSheet.swift: Note Sync Conflict Resolver with Character-Level Diff ----------

    public static async Task ShowNoteConflictsSheetAsync(XamlRoot xamlRoot, LibraryService library, Action onResolved)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        var host = new StackPanel { Spacing = 12, Width = 432 };
        ContentDialog? dlgRef = null;

        void RebuildConflicts()
        {
            host.Children.Clear();
            NoteConflict[] conflicts;
            try { conflicts = library.Core.NoteConflicts(); } catch { conflicts = []; }

            if (conflicts.Length == 0)
            {
                dlgRef?.Hide();
                return;
            }

            var c = conflicts[0];
            host.Children.Add(new TextBlock
            {
                Text = $"《{c.BookTitle}》 · 还剩 {conflicts.Length} 处想法冲突",
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T2),
            });

            // Yellow highlighted passage
            host.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = ThemeHelper.Brush(Color.FromArgb(dark ? (byte)55 : (byte)90, p.HlY.R, p.HlY.G, p.HlY.B)),
                Padding = new Thickness(12, 9, 12, 9),
                Child = new TextBlock
                {
                    Text = c.Text,
                    FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                    FontSize = 13,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                },
            });

            // This device on top, other device below (1:1 ConflictSheet.swift)
            bool thisIsNewer = c.Newer.ThisDevice;
            var topSide = thisIsNewer ? c.Newer : c.Older;
            var botSide = thisIsNewer ? c.Older : c.Newer;
            var topChoice = thisIsNewer ? ConflictChoice.Newer : ConflictChoice.Older;
            var botChoice = thisIsNewer ? ConflictChoice.Older : ConflictChoice.Newer;

            var (topPre, topMid, topSuf, botMid) = ComputeDiffSlices(topSide.Note ?? "", botSide.Note ?? "");

            Border BuildSideCard(ConflictSide side, bool isNewer, string pre, string mid, string suf, ConflictChoice choice)
            {
                var st = new StackPanel { Spacing = 6 };
                var head = new Grid();
                var devLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                devLabel.Children.Add(new TextBlock
                {
                    Text = side.ThisDevice ? $"这台电脑（{side.DeviceName}）" : side.DeviceName,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                });
                if (isNewer)
                {
                    devLabel.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = ThemeHelper.Brush(tint.Soft),
                        Padding = new Thickness(5, 1, 5, 1),
                        Child = new TextBlock { Text = "较新", FontSize = 9.5, Foreground = ThemeHelper.Brush(tint.Accent) },
                    });
                }
                head.Children.Add(devLabel);

                var keepBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = "留这份",
                    Height = 24,
                    MinHeight = 24,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                keepBtn.Click += (_, _) =>
                {
                    library.Core.ResolveNoteConflict(c.Book, c.Mark, choice);
                    onResolved();
                    RebuildConflicts();
                };
                head.Children.Add(keepBtn);
                st.Children.Add(head);

                var rtb = new TextBlock { FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
                if (string.IsNullOrEmpty(side.Note))
                {
                    rtb.Text = "（清空了想法）";
                    rtb.Foreground = ThemeHelper.Brush(p.T3);
                }
                else
                {
                    if (pre.Length > 0) rtb.Inlines.Add(new Run { Text = pre });
                    if (mid.Length > 0) rtb.Inlines.Add(new Run { Text = mid, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
                    if (suf.Length > 0) rtb.Inlines.Add(new Run { Text = suf });
                }
                st.Children.Add(rtb);

                var dt = DateTimeOffset.FromUnixTimeMilliseconds(side.At).LocalDateTime;
                st.Children.Add(new TextBlock { Text = $"{dt:M 月 d 日 HH:mm}", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });

                return new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(isNewer ? tint.Edge : p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 10, 12, 10),
                    Child = st,
                };
            }

            host.Children.Add(BuildSideCard(topSide, thisIsNewer, topPre, topMid, topSuf, topChoice));
            host.Children.Add(BuildSideCard(botSide, !thisIsNewer, topPre, botMid, topSuf, botChoice));

            var bottomActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var bothBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "两份都留" };
            bothBtn.Click += (_, _) =>
            {
                library.Core.ResolveNoteConflict(c.Book, c.Mark, ConflictChoice.Both);
                onResolved();
                RebuildConflicts();
            };
            var newerBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "用较新的" };
            newerBtn.Click += (_, _) =>
            {
                library.Core.ResolveNoteConflict(c.Book, c.Mark, ConflictChoice.Newer);
                onResolved();
                RebuildConflicts();
            };
            bottomActions.Children.Add(bothBtn);
            bottomActions.Children.Add(newerBtn);
            host.Children.Add(bottomActions);
        }

        RebuildConflicts();
        if (host.Children.Count == 0) return;

        dlgRef = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "笔记同步冲突",
            Content = new ScrollViewer { MaxHeight = 520, Content = host },
            CloseButtonText = "稍后处理",
        };
        await dlgRef.ShowAsync();
    }

    private static (string Prefix, string MidA, string Suffix, string MidB) ComputeDiffSlices(string a, string b)
    {
        int pre = 0;
        int minLen = Math.Min(a.Length, b.Length);
        while (pre < minLen && a[pre] == b[pre]) pre++;

        int suf = 0;
        while (suf < minLen - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;

        string prefix = a[..pre];
        string suffix = suf > 0 ? a[(a.Length - suf)..] : "";
        string midA = a.Substring(pre, a.Length - pre - suf);
        string midB = b.Substring(pre, b.Length - pre - suf);
        return (prefix, midA, suffix, midB);
    }
}
