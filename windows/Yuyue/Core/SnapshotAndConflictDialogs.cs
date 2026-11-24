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

        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe0 ? fe0.ActualTheme : ElementTheme.Light);
        SnapshotInfo info;
        try
        {
            info = await Task.Run(() => library.Core.ReadSnapshot(snapPath));
        }
        catch (Exception ex)
        {
            await YYChrome.ShowNoticeAsync(xamlRoot, dark, "无法读取快照文件", ex.Message, "好", tint: library.Prefs.Tint);
            return false;
        }

        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);
        bool replaceMode = false;
        bool confirmedImport = false;
        ContentDialog? dlg = null;

        var madeTime = DateTimeOffset.FromUnixTimeMilliseconds(info.MadeAt).LocalDateTime;
        string sizeStr = info.Bytes >= 1024 * 1024 ? $"{info.Bytes / 1048576.0:0.1} MB" : $"{Math.Max(1, info.Bytes / 1024.0):0} KB";

        // File summary card with shippingbox/cube tile + item count tags (1:1 SnapshotSheets.swift)
        var fileHead = new Grid { ColumnSpacing = 10 };
        fileHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        fileHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fileHead.Children.Add(new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(tint.Soft),
            VerticalAlignment = VerticalAlignment.Center,
            Child = YYIcons.Make("cube", 16, ThemeHelper.Brush(tint.Accent)),
        });
        var fileMeta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        fileMeta.Children.Add(L10n.Skip(new TextBlock
        {
            Text = Path.GetFileName(snapPath),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }));
        fileMeta.Children.Add(new TextBlock
        {
            Text = $"来自 {info.DeviceName} ({info.Platform}) · {madeTime:yyyy-MM-dd HH:mm} · {sizeStr}",
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(fileMeta, 1);
        fileHead.Children.Add(fileMeta);

        var partTags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void AddTag(string t) => partTags.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Inset),
            Padding = new Thickness(7, 2, 7, 2),
            Child = new TextBlock { Text = t, FontSize = 11, Foreground = ThemeHelper.Brush(p.T2) },
        });
        if (info.Parts.Progress) AddTag($"进度 {info.Counts.Read} 本");
        if (info.Parts.Notes) AddTag($"笔记 {info.Counts.Notes} 条");
        if (info.Parts.Words) AddTag($"生词 {info.Counts.Words} 个词");
        if (info.Parts.Settings) AddTag("设置");
        if (info.Parts.Files) AddTag("书文件");

        var fileCardStack = new StackPanel { Spacing = 10 };
        fileCardStack.Children.Add(fileHead);
        if (partTags.Children.Count > 0)
        {
            fileCardStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            fileCardStack.Children.Add(new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = partTags,
            });
        }
        var fileCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(14, 12, 14, 12),
            Child = fileCardStack,
        };

        // Side-by-side 合并 / 覆盖 cards
        var mergeCheck = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var replaceCheck = new ContentControl { VerticalAlignment = VerticalAlignment.Center };

        Border BuildModeCard(string title, string desc, ContentControl chkHost, out Button hitBtn)
        {
            var top = new Grid();
            top.Children.Add(new TextBlock { Text = L10n.T(title), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            chkHost.HorizontalAlignment = HorizontalAlignment.Right;
            top.Children.Add(chkHost);

            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(top);
            sp.Children.Add(new TextBlock { Text = L10n.T(desc), FontSize = 11, LineHeight = 16.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });

            var border = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1.2),
                Child = sp,
            };
            hitBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                UseSystemFocusVisuals = false,
                Content = border,
            };
            return border;
        }

        var mergeCard = BuildModeCard("合并", "保留本机已有书与笔记，合并快照里更新的进度与划线。", mergeCheck, out var mergeBtn);
        var replaceCard = BuildModeCard("覆盖", "恢复前自动备份当前书库快照，再完全替换。", replaceCheck, out var replaceBtn);

        var modeGrid = new Grid { ColumnSpacing = 10 };
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modeGrid.Children.Add(mergeBtn);
        Grid.SetColumn(replaceBtn, 1);
        modeGrid.Children.Add(replaceBtn);

        var diffText = L10n.Skip(new TextBlock
        {
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        bool IsMergeNoOp()
        {
            bool countsSame = info.Merged.Books == info.Now.Books
                && info.Merged.Read == info.Now.Read
                && info.Merged.Notes == info.Now.Notes
                && info.Merged.Words == info.Now.Words
                && info.Merged.Rules == info.Now.Rules;
            bool snapshotEmpty = info.Counts.Books == 0
                && info.Counts.Read == 0
                && info.Counts.Notes == 0
                && info.Counts.Words == 0
                && info.Counts.Rules == 0
                && info.Counts.FileBytes == 0
                && !info.Parts.Settings;
            return snapshotEmpty || (countsSame && info.Counts.FileBytes == 0 && !info.Parts.Settings);
        }

        string FormatDelta(uint before, uint after)
        {
            if (after == before) return $"{before}";
            int diff = (int)after - (int)before;
            return diff > 0 ? $"{before}→{after} (+{diff})" : $"{before}→{after} ({diff})";
        }

        var importBtn = YYChrome.MakeSheetPillButton("导入", () => { confirmedImport = true; dlg?.Hide(); }, dark, library.Prefs.Tint, "dark", height: 40, stretch: true);

        void SyncModeVisuals()
        {
            mergeCard.BorderBrush = ThemeHelper.Brush(!replaceMode ? tint.Accent : p.Line);
            mergeCard.Background = ThemeHelper.Brush(!replaceMode ? tint.Soft : p.Card);
            mergeCheck.Content = !replaceMode ? YYIcons.Make("checkmark", 12, ThemeHelper.Brush(tint.Accent)) : null;

            replaceCard.BorderBrush = ThemeHelper.Brush(replaceMode ? p.Danger : p.Line);
            replaceCard.Background = ThemeHelper.Brush(replaceMode ? Color.FromArgb(20, p.Danger.R, p.Danger.G, p.Danger.B) : p.Card);
            replaceCheck.Content = replaceMode ? YYIcons.Make("checkmark", 12, ThemeHelper.Brush(p.Danger)) : null;

            var target = replaceMode ? info.Replaced : info.Merged;
            bool noOp = !replaceMode && IsMergeNoOp();

            if (noOp)
            {
                diffText.Text = L10n.T("这份快照里的书籍、进度、笔记和生词已全部在本机中，无需重复合并。");
                diffText.Foreground = ThemeHelper.Brush(p.T3);
            }
            else
            {
                string modeTag = replaceMode ? "覆盖后" : "合并后";
                diffText.Text = L10n.T($"{modeTag}：书 {FormatDelta(info.Now.Books, target.Books)} · 进度 {FormatDelta(info.Now.Read, target.Read)} · 笔记 {FormatDelta(info.Now.Notes, target.Notes)} · 生词 {FormatDelta(info.Now.Words, target.Words)}");
                diffText.Foreground = ThemeHelper.Brush(replaceMode ? p.Danger : p.T2);
            }

            importBtn.IsEnabled = !noOp;
            importBtn.Opacity = noOp ? 0.42 : 1.0;
        }

        mergeBtn.Click += (_, _) => { replaceMode = false; SyncModeVisuals(); };
        replaceBtn.Click += (_, _) => { replaceMode = true; SyncModeVisuals(); };
        SyncModeVisuals();

        var root = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(22, 18, 22, 18),
            Children =
            {
                YYChrome.MakeSheetHeader("导入快照", null, () => dlg?.Hide(), dark, titleSize: 17),
                fileCard,
                modeGrid,
                diffText,
                importBtn,
            },
        };

        dlg = YYChrome.CreateMacSheet(xamlRoot, dark, 400, root, tint: library.Prefs.Tint);
        await dlg.ShowSafeAsync(library.Prefs.Tint);
        if (!confirmedImport) return false;

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
            App.CurrentApp.ReloadAllAfterRestore();
            return true;
        }
        catch (Exception ex)
        {
            await YYChrome.ShowNoticeAsync(xamlRoot, dark, "恢复快照失败", ex.Message, "好", tint: library.Prefs.Tint);
            return false;
        }
    }

    public static async Task ShowReadingExportSheetAsync(Window ownerWindow, XamlRoot xamlRoot, LibraryService library)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);
        ContentDialog? dlgRef = null;
        bool saveCsv = false;

        ReadingSite site = ReadingSite.Goodreads;
        int utcOffsetMin = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;

        var segHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        var wantRightTb = new TextBlock { FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        var readingRightTb = new TextBlock { FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        var finishedRightTb = new TextBlock { FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };

        Grid MakeStatRow(string icon, string label, TextBlock rightTb)
        {
            var g = new Grid { ColumnSpacing = 10, Padding = new Thickness(14, 10, 14, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(YYIcons.Make(icon, 13, ThemeHelper.Brush(tint.Accent)));
            var lTb = new TextBlock { Text = L10n.T(label), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lTb, 1);
            g.Children.Add(lTb);
            Grid.SetColumn(rightTb, 2);
            g.Children.Add(rightTb);
            return g;
        }

        var groupCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Child = new StackPanel
            {
                Children =
                {
                    MakeStatRow("bookmark", "想读", wantRightTb),
                    new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(42, 0, 14, 0) },
                    MakeStatRow("book", "在读", readingRightTb),
                    new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(42, 0, 14, 0) },
                    MakeStatRow("check-circle", "读完", finishedRightTb),
                },
            },
        };

        var previewText = L10n.Skip(new TextBlock
        {
            FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(p.T2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 4,
        });
        var isbnNote = L10n.Skip(new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
        ReadingExport currentExport = await Task.Run(() => library.Core.ReadingExport(site, utcOffsetMin));
        var exportBtn = YYChrome.MakeSheetPillButton("导出 CSV", () => { saveCsv = true; dlgRef?.Hide(); }, dark, library.Prefs.Tint, "dark", height: 40, stretch: true);
        bool sheetClosed = false;
        int exportReqSeq = 0;

        void RenderSegmentedBar()
        {
            string activeKey = site == ReadingSite.Goodreads ? "goodreads" : "storygraph";
            var bar = YYChrome.MakeSegmentedBar(
                [("goodreads", "Goodreads"), ("storygraph", "StoryGraph")],
                activeKey,
                nextKey =>
                {
                    site = nextKey == "storygraph" ? ReadingSite.StoryGraph : ReadingSite.Goodreads;
                    _ = RefreshExportPreviewAsync(site);
                },
                dark,
                fontSize: 12.5,
                height: 32);
            bar.HorizontalAlignment = HorizontalAlignment.Left;
            segHost.Content = bar;
        }

        void ApplyExportPreview(ReadingExport exp)
        {
            uint totalBooks = exp.Want + exp.Reading + exp.Finished;
            wantRightTb.Text = L10n.T($"{exp.Want} 本");
            readingRightTb.Text = L10n.T($"{exp.Reading} 本");
            finishedRightTb.Text = L10n.T($"{exp.Finished} 本 · 带读完日期");
            previewText.Text = string.Join("\n", exp.Csv.Split('\n').Take(4));
            isbnNote.Text = L10n.T(exp.WithoutIsbn > 0
                ? $"其中 {exp.WithoutIsbn} 本没有 ISBN，导入后将按书名和作者匹配。"
                : "全部书目均包含标准书名与阅读状态字段。");
            if (exportBtn.Content is TextBlock tb)
                tb.Text = L10n.T($"导出 {totalBooks} 本 · CSV");
            exportBtn.IsEnabled = totalBooks > 0;
            exportBtn.Opacity = totalBooks > 0 ? 1.0 : 0.42;
        }

        async Task RefreshExportPreviewAsync(ReadingSite targetSite)
        {
            int mySeq = ++exportReqSeq;
            RenderSegmentedBar();
            var exp = await Task.Run(() => library.Core.ReadingExport(targetSite, utcOffsetMin));
            if (sheetClosed || mySeq != exportReqSeq) return;
            currentExport = exp;
            ApplyExportPreview(exp);
        }

        RenderSegmentedBar();
        ApplyExportPreview(currentExport);

        var root = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(22, 18, 22, 18),
            Children =
            {
                YYChrome.MakeSheetHeader("导出阅读记录", null, () => dlgRef?.Hide(), dark, titleSize: 17),
                segHost,
                groupCard,
                new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(12, 9, 12, 9),
                    Child = previewText,
                },
                isbnNote,
                exportBtn,
            },
        };

        try
        {
            dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 400, root, tint: library.Prefs.Tint);
            await dlgRef.ShowSafeAsync(library.Prefs.Tint);
        }
        finally
        {
            sheetClosed = true;
        }

        if (saveCsv)
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = site == ReadingSite.Goodreads ? "语阅-goodreads" : "语阅-storygraph",
            };
            picker.FileTypeChoices.Add(L10n.T("CSV 表格"), [".csv"]);
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
        var kaiti = new FontFamily(YYFontFamily.Kaiti.WindowsFamily());
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());

        var host = new StackPanel { Spacing = 12, Padding = new Thickness(22, 18, 22, 18) };
        ContentDialog? dlgRef = null;
        int initialTotal = 0;

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
            if (initialTotal < conflicts.Length) initialTotal = conflicts.Length;
            int currentOrdinal = Math.Max(1, initialTotal - conflicts.Length + 1);

            var c = conflicts[0];

            // Top bar: left X close button + centered "同步冲突" / "第 X 处，共 Y 处"
            var topBar = new Grid();
            var closeBtn = YYChrome.MakeSheetCloseButton(() => dlgRef?.Hide(), dark, 28);
            closeBtn.HorizontalAlignment = HorizontalAlignment.Left;
            topBar.Children.Add(closeBtn);

            var centerTitle = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            centerTitle.Children.Add(new TextBlock { Text = L10n.T("同步冲突"), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), HorizontalAlignment = HorizontalAlignment.Center });
            centerTitle.Children.Add(new TextBlock { Text = $"第 {currentOrdinal} 处，共 {initialTotal} 处", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Center });
            topBar.Children.Add(centerTitle);
            host.Children.Add(topBar);

            host.Children.Add(new TextBlock
            {
                Text = L10n.T("两台设备上的这条笔记不一样"),
                FontSize = 17,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = ThemeHelper.Brush(p.T1),
                Margin = new Thickness(0, 2, 0, -2),
            });

            // Mini book cover + "{BookTitle} · 划线的想法"
            var srcRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
            var matchBook = library.GetKeptBooks().FirstOrDefault(b => b.Id == c.Book);
            if (matchBook is not null && !string.IsNullOrEmpty(matchBook.CoverPath) && File.Exists(matchBook.CoverPath))
            {
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
                {
                    DecodePixelWidth = 48,
                    UriSource = new Uri(matchBook.CoverPath),
                };
                srcRow.Children.Add(new Border
                {
                    Width = 13,
                    Height = 18,
                    CornerRadius = new CornerRadius(2),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill },
                });
            }
            else
            {
                srcRow.Children.Add(new Border
                {
                    Width = 13,
                    Height = 18,
                    CornerRadius = new CornerRadius(2),
                    Background = ThemeHelper.Brush(p.Inset),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                });
            }
            srcRow.Children.Add(L10n.Skip(new TextBlock
            {
                Text = $"{c.BookTitle} · {L10n.T("划线的想法")}",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T2),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }));
            host.Children.Add(srcRow);

            // Yellow highlighted passage
            host.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(Color.FromArgb(dark ? (byte)55 : (byte)90, p.HlY.R, p.HlY.G, p.HlY.B)),
                Padding = new Thickness(12, 9, 12, 9),
                Child = L10n.Skip(new TextBlock
                {
                    Text = c.Text,
                    FontFamily = serif,
                    FontSize = 13,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                }),
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
                var st = new StackPanel { Spacing = 8 };
                var head = new Grid();
                var devLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                devLabel.Children.Add(YYIcons.Make("laptop", 12, ThemeHelper.Brush(p.T2)));
                devLabel.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = side.ThisDevice ? L10n.T($"这台电脑（{side.DeviceName}）") : side.DeviceName,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    VerticalAlignment = VerticalAlignment.Center,
                }));
                if (isNewer)
                {
                    devLabel.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Background = ThemeHelper.Brush(tint.Soft),
                        Padding = new Thickness(5, 1, 5, 1),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = L10n.T("较新"), FontSize = 9.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) },
                    });
                }
                head.Children.Add(devLabel);

                var keepBtn = YYChrome.MakeSheetPillButton("留这份", () =>
                {
                    try { library.Core.ResolveNoteConflict(c.Book, c.Mark, choice); } catch { }
                    onResolved();
                    RebuildConflicts();
                }, dark, library.Prefs.Tint, "secondary", height: 26, horizontalPadding: 11);
                keepBtn.HorizontalAlignment = HorizontalAlignment.Right;
                head.Children.Add(keepBtn);
                st.Children.Add(head);

                // Kaiti left-bar thought with character-level diff highlight
                var diffRow = new Grid { ColumnSpacing = 8 };
                diffRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                diffRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                diffRow.Children.Add(new Border { Background = ThemeHelper.Brush(tint.Accent), CornerRadius = new CornerRadius(1) });

                var rtb = L10n.Skip(new TextBlock
                {
                    FontFamily = kaiti,
                    FontSize = 13,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                });
                if (string.IsNullOrEmpty(side.Note))
                {
                    rtb.Text = L10n.T("（清空了想法）");
                    rtb.Foreground = ThemeHelper.Brush(p.T3);
                }
                else
                {
                    if (pre.Length > 0) rtb.Inlines.Add(new Run { Text = pre });
                    if (mid.Length > 0) rtb.Inlines.Add(new Run { Text = mid, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(tint.Accent) });
                    if (suf.Length > 0) rtb.Inlines.Add(new Run { Text = suf });
                }
                Grid.SetColumn(rtb, 1);
                diffRow.Children.Add(rtb);
                st.Children.Add(diffRow);

                var dt = DateTimeOffset.FromUnixTimeMilliseconds(side.At).LocalDateTime;
                st.Children.Add(new TextBlock { Text = $"{dt:M 月 d 日 HH:mm}", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });

                return new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(isNewer ? tint.Edge : p.Line),
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(14, 11, 14, 11),
                    Child = st,
                };
            }

            host.Children.Add(BuildSideCard(topSide, thisIsNewer, topPre, topMid, topSuf, topChoice));
            host.Children.Add(BuildSideCard(botSide, !thisIsNewer, topPre, botMid, topSuf, botChoice));

            var bottomActions = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 4, 0, 0) };
            bottomActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bothBtn = YYChrome.MakeSheetPillButton("两份都留", () =>
            {
                try { library.Core.ResolveNoteConflict(c.Book, c.Mark, ConflictChoice.Both); } catch { }
                onResolved();
                RebuildConflicts();
            }, dark, library.Prefs.Tint, "secondary", height: 38, stretch: true);
            var newerBtn = YYChrome.MakeSheetPillButton("用较新的", () =>
            {
                try { library.Core.ResolveNoteConflict(c.Book, c.Mark, ConflictChoice.Newer); } catch { }
                onResolved();
                RebuildConflicts();
            }, dark, library.Prefs.Tint, "dark", height: 38, stretch: true);
            Grid.SetColumn(newerBtn, 1);
            bottomActions.Children.Add(bothBtn);
            bottomActions.Children.Add(newerBtn);
            host.Children.Add(bottomActions);
            L10n.ApplyTree(host);
        }

        RebuildConflicts();
        if (host.Children.Count == 0) return;

        dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 440, new ScrollViewer { MaxHeight = 640, Content = host }, maxHeight: 680, tint: library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(library.Prefs.Tint);
    }

    internal static (string Prefix, string MidA, string Suffix, string MidB) ComputeDiffSlices(string a, string b)
    {
        int pre = 0;
        int minLen = Math.Min(a.Length, b.Length);
        while (pre < minLen && a[pre] == b[pre]) pre++;
        // Never split between a UTF-16 high surrogate and low surrogate
        if (pre > 0 && (char.IsHighSurrogate(a[pre - 1]) || char.IsHighSurrogate(b[pre - 1])))
            pre--;

        int suf = 0;
        while (suf < minLen - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;
        if (suf > 0 && (char.IsLowSurrogate(a[a.Length - suf]) || char.IsLowSurrogate(b[b.Length - suf])))
            suf--;

        string prefix = a[..pre];
        string suffix = suf > 0 ? a[(a.Length - suf)..] : "";
        string midA = a.Substring(pre, a.Length - pre - suf);
        string midB = b.Substring(pre, b.Length - pre - suf);
        return (prefix, midA, suffix, midB);
    }
}
