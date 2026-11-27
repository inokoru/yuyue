using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Yuyue.Audiobook;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Shelf;

namespace Yuyue.UI.Library;

public sealed partial class LibraryView
{
    private async Task<XamlRoot?> WaitForXamlRootAsync(int timeoutMs = 4000)
    {
        if (RootGrid.XamlRoot is { } existing)
            return existing;

        var tcs = new TaskCompletionSource<XamlRoot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (RootGrid.XamlRoot is { } xr)
                tcs.TrySetResult(xr);
        }

        RootGrid.Loaded += OnLoaded;
        try
        {
            if (RootGrid.XamlRoot is { } immediate)
                return immediate;

            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (RootGrid.XamlRoot is { } xr)
                    return xr;
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(50));
                if (completed == tcs.Task && tcs.Task.Result is { } loadedXr)
                    return loadedXr;
            }
            return RootGrid.XamlRoot;
        }
        finally
        {
            RootGrid.Loaded -= OnLoaded;
        }
    }

    private void ConfigureDialogTheme(ContentDialog dlg)
    {
        var theme = _library.Prefs.ToElementTheme();
        bool dark = ThemeHelper.IsDark(theme);
        dlg.RequestedTheme = theme;
        ThemeHelper.ApplyControlThemeResources(dlg.Resources, dark, _library.Prefs.Tint);
    }

    private static string FormatImportError(Exception ex) => ex switch
    {
        CoreException.Protected => L10n.T("这本书有版权保护（DRM），打不开"),
        CoreException.Unsupported => L10n.T("不支持这种文件，目前能打开 EPUB、PDF、MOBI、AZW3、TXT"),
        CoreException.BadBook bad => FormatBadBookError(bad.detail),
        CoreException.NotFound => L10n.T("找不到这本书的文件，可能被移走或删掉了"),
        CoreException.Io io => string.IsNullOrWhiteSpace(io.detail) ? L10n.T("无法读取文件") : L10n.T($"出错了（{io.detail}）"),
        CoreException.Invalid inv => string.IsNullOrWhiteSpace(inv.detail) ? L10n.T("文件参数无效") : inv.detail,
        CoreException.Db db => string.IsNullOrWhiteSpace(db.detail) ? L10n.T("出错了") : L10n.T($"出错了（{db.detail}）"),
        _ => string.IsNullOrWhiteSpace(ex.Message) ? L10n.T("未知错误") : ex.Message,
    };

    private static string FormatBadBookError(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return L10n.T("这本书读不出来，文件可能坏了，或者不是它看起来的格式");
        if (detail.Contains("empty", StringComparison.OrdinalIgnoreCase))
            return L10n.T("这个文件是空的，里面没有内容");
        if (detail.Contains("large", StringComparison.OrdinalIgnoreCase) || detail.Contains("size", StringComparison.OrdinalIgnoreCase))
            return L10n.T("这本书太大了，读不进来");
        return L10n.T("这本书读不出来，文件可能坏了，或者不是它看起来的格式");
    }

    private sealed class ImportRowState
    {
        public string FileName { get; init; } = "";
        public string Status { get; set; } = "pending"; // pending | importing | imported | exists | failed
        public string? Detail { get; set; }
    }

    public async Task ImportPathsAsync(IReadOnlyList<string> rawPaths, bool openFirst = false, string source = "picker", string? targetCollectionId = null)
    {
        if (rawPaths.Count == 0) return;

        string? targetColName = !string.IsNullOrWhiteSpace(targetCollectionId)
            ? _collections.FirstOrDefault(c => string.Equals(c.Id, targetCollectionId, StringComparison.OrdinalIgnoreCase))?.Name
            : null;

        var audioCandidates = new List<string>();
        var bookFiles = new List<string>();
        var seenBookFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<(string FileName, string Reason)>();
        string? snapshotPath = null;

        void AddBookFile(string filePath)
        {
            if (seenBookFiles.Add(filePath))
                bookFiles.Add(filePath);
        }

        foreach (var path in rawPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (path.EndsWith(".yuyue", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                snapshotPath = path;
                continue;
            }
            if (Directory.Exists(path))
            {
                try
                {
                    var dirFiles = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).ToList();
                    var dirEbooks = dirFiles
                        .Where(f => LibraryService.SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .ToList();
                    bool hasAudio = dirFiles.Any(f =>
                    {
                        string fExt = Path.GetExtension(f).ToLowerInvariant();
                        return AudiobookImporter.Extensions.Contains(fExt) || AudiobookImporter.DrmExtensions.Contains(fExt);
                    });
                    if (hasAudio)
                    {
                        audioCandidates.Add(path);
                        foreach (var eb in dirEbooks)
                        {
                            if (!string.Equals(Path.GetExtension(eb), ".txt", StringComparison.OrdinalIgnoreCase))
                                AddBookFile(eb);
                        }
                    }
                    else if (dirEbooks.Count > 0)
                    {
                        foreach (var eb in dirEbooks)
                            AddBookFile(eb);
                    }
                }
                catch (Exception ex)
                {
                    string dirName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    failures.Add((string.IsNullOrEmpty(dirName) ? path : dirName, FormatImportError(ex)));
                }
                continue;
            }
            if (File.Exists(path) && AudiobookImporter.IsAudiobookOrArchivePath(path))
            {
                audioCandidates.Add(path);
                continue;
            }
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (LibraryService.SupportedExtensions.Contains(ext) && File.Exists(path))
                AddBookFile(path);
        }

        if (snapshotPath is null && audioCandidates.Count == 0 && bookFiles.Count == 0)
        {
            if (failures.Count > 0)
            {
                var firstFail = failures[0];
                ShowToast(L10n.T($"导入失败 ({firstFail.FileName})：{firstFail.Reason}"));
            }
            else
            {
                ShowToast(L10n.T("未找到可导入的书籍或有声书（支持 EPUB / PDF / TXT / MOBI / 有声书 / .yuyue 快照）"));
            }
            return;
        }

        if (snapshotPath is not null)
        {
            var snapXr = await WaitForXamlRootAsync();
            if (snapXr is not null)
            {
                try
                {
                    await SnapshotAndConflictDialogs.ShowSnapshotImportDialogAsync(_ownerWindow, snapXr, _library, snapshotPath);
                }
                catch (Exception ex)
                {
                    ShowToast(L10n.T($"恢复快照失败：{FormatImportError(ex)}"));
                }
            }
            else
            {
                ShowToast(L10n.T("窗口尚未就绪，请重试导入快照"));
            }
            return;
        }

        if (audioCandidates.Count > 0)
        {
            var audXr = await WaitForXamlRootAsync();
            if (audXr is not null)
            {
                try
                {
                    var importedAudio = await AudiobookImporter.ShowImportSheetAsync(_ownerWindow, audXr, _library, audioCandidates);
                    if (importedAudio is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(targetCollectionId))
                        {
                            try { _library.SetInCollection(targetCollectionId, importedAudio.Id, true); } catch { }
                        }
                        RefreshBooks();
                        ShowToast(!string.IsNullOrWhiteSpace(targetColName)
                            ? L10n.T($"已导入《{importedAudio.Title}》并加入「{targetColName}」")
                            : L10n.T($"已导入《{importedAudio.Title}》"));
                        if (openFirst) TryOpenBook(importedAudio);
                    }
                }
                catch (Exception ex)
                {
                    string reason = FormatImportError(ex);
                    if (bookFiles.Count > 0)
                        failures.Add((L10n.T("有声书"), reason));
                    else
                        ShowToast(L10n.T($"有声书导入失败：{reason}"));
                }
            }
            else
            {
                ShowToast(L10n.T("窗口尚未就绪，请重试导入有声书"));
            }
        }

        if (bookFiles.Count == 0)
        {
            if (failures.Count > 0)
            {
                var firstFail = failures[0];
                ShowToast(L10n.T($"导入失败 ({firstFail.FileName})：{firstFail.Reason}"));
            }
            return;
        }

        var existingIds = new HashSet<string>(_library.GetBooks().Select(b => b.Id), StringComparer.OrdinalIgnoreCase);
        var rows = bookFiles.Select(f => new ImportRowState { FileName = Path.GetFileName(f), Status = "pending" }).ToList();
        foreach (var (fn, reason) in failures)
            rows.Add(new ImportRowState { FileName = fn, Status = "failed", Detail = reason });

        _importCardAutoHideTimer?.Stop();
        BookSummary? firstImported = null;
        BookSummary? firstExisted = null;
        int newlyImported = 0;
        int alreadyExisted = 0;

        for (int i = 0; i < bookFiles.Count; i++)
        {
            rows[i].Status = "importing";
            RenderImportProgressCard(rows, done: i, total: bookFiles.Count, newlyImported, alreadyExisted, failures.Count, targetColName, targetCollectionId, revealBook: null, finished: false);
            try
            {
                var imported = await _library.ImportBookWithExtrasAsync(bookFiles[i]);
                bool existed = !existingIds.Add(imported.Id);
                if (!string.IsNullOrWhiteSpace(targetCollectionId))
                {
                    try { _library.SetInCollection(targetCollectionId, imported.Id, true); } catch { }
                }
                if (existed)
                {
                    alreadyExisted++;
                    firstExisted ??= imported;
                    rows[i].Status = "exists";
                    rows[i].Detail = !string.IsNullOrWhiteSpace(targetColName)
                        ? L10n.T($"已在书库中，已加入「{targetColName}」")
                        : L10n.T("已在书库中");
                }
                else
                {
                    newlyImported++;
                    firstImported ??= imported;
                    rows[i].Status = "imported";
                    rows[i].Detail = imported.Title;
                }
            }
            catch (Exception ex)
            {
                string reason = FormatImportError(ex);
                failures.Add((Path.GetFileName(bookFiles[i]), reason));
                rows[i].Status = "failed";
                rows[i].Detail = reason;
            }
        }

        if (newlyImported > 0 || alreadyExisted > 0)
            RefreshBooks();

        var revealBook = firstImported ?? firstExisted;
        RenderImportProgressCard(rows, done: bookFiles.Count, total: bookFiles.Count, newlyImported, alreadyExisted, failures.Count, targetColName, targetCollectionId, revealBook, finished: true);

        if (openFirst && firstImported is not null)
            TryOpenBook(firstImported);

        if (failures.Count == 0 && !_disposed)
        {
            _importCardAutoHideTimer?.Stop();
            _importCardAutoHideTimer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(8) };
            _importCardAutoHideTimer.Tick += (_, _) =>
            {
                _importCardAutoHideTimer.Stop();
                if (!_disposed) ImportProgressCard.Visibility = Visibility.Collapsed;
            };
            _importCardAutoHideTimer.Start();
        }
    }

    private void RenderImportProgressCard(
        List<ImportRowState> rows,
        int done,
        int total,
        int newlyImported,
        int alreadyExisted,
        int failCount,
        string? targetColName,
        string? targetCollectionId,
        BookSummary? revealBook,
        bool finished)
    {
        if (_disposed) return;
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        ImportProgressContent.Children.Clear();

        // Header row: status indicator + summary title + close button
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (!finished)
        {
            head.Children.Add(new ProgressRing { Width = 15, Height = 15, IsActive = true, VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            var iconColor = (newlyImported > 0 || alreadyExisted > 0) ? p.Ok : p.Danger;
            head.Children.Add(YYIcons.Make((newlyImported > 0 || alreadyExisted > 0) ? "check-circle" : "info-circle", 15, ThemeHelper.Brush(iconColor)));
        }

        string titleText;
        if (!finished)
        {
            titleText = !string.IsNullOrWhiteSpace(targetColName)
                ? L10n.T($"正在导入「{targetColName}」({done + 1} / {total})…")
                : L10n.T($"正在导入 {done + 1} / {total}…");
        }
        else
        {
            var parts = new List<string>();
            if (newlyImported > 0) parts.Add(L10n.T($"已导入 {newlyImported} 本"));
            if (alreadyExisted > 0) parts.Add(L10n.T($"已存在 {alreadyExisted} 本"));
            if (failCount > 0) parts.Add(L10n.T($"{failCount} 本失败"));
            titleText = parts.Count > 0 ? string.Join(" · ", parts) : L10n.T("导入完成");
            if (!string.IsNullOrWhiteSpace(targetColName) && (newlyImported > 0 || alreadyExisted > 0))
                titleText += $" · {targetColName}";
        }

        var titleTb = L10n.Skip(new TextBlock
        {
            Text = titleText,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(titleTb, 1);
        head.Children.Add(titleTb);

        var closeBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYToolbarIconButtonStyle"],
            Width = 22,
            Height = 22,
            MinWidth = 22,
            MinHeight = 22,
            Padding = new Thickness(0),
            Content = YYIcons.Make("xmark", 10.5, ThemeHelper.Brush(p.T3)),
        };
        closeBtn.Click += (_, _) =>
        {
            _importCardAutoHideTimer?.Stop();
            ImportProgressCard.Visibility = Visibility.Collapsed;
        };
        Grid.SetColumn(closeBtn, 2);
        head.Children.Add(closeBtn);
        ImportProgressContent.Children.Add(head);

        if (!finished)
        {
            ImportProgressContent.Children.Add(new ProgressBar
            {
                Minimum = 0,
                Maximum = Math.Max(1, total),
                Value = Math.Clamp(done, 0, Math.Max(1, total)),
                Height = 3,
            });
        }

        // Per-file rows
        var listStack = new StackPanel { Spacing = 4 };
        foreach (var r in rows)
        {
            var rg = new Grid { ColumnSpacing = 8 };
            rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var fnTb = L10n.Skip(new TextBlock
            {
                Text = r.FileName,
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(r.Status == "failed" ? p.Danger : p.T1),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (!string.IsNullOrWhiteSpace(r.Detail))
                ToolTipService.SetToolTip(fnTb, r.Detail);
            rg.Children.Add(fnTb);

            var (stLabel, stColor) = r.Status switch
            {
                "importing" => (L10n.T("导入中…"), tc.Accent),
                "imported" => (L10n.T("已导入"), p.Ok),
                "exists" => (L10n.T("已存在"), p.T2),
                "failed" => (L10n.T("失败"), p.Danger),
                _ => (L10n.T("等待中"), p.T3),
            };
            var stTb = L10n.Skip(new TextBlock
            {
                Text = stLabel,
                FontSize = 11,
                FontWeight = r.Status is "importing" or "imported" ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ThemeHelper.Brush(stColor),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (!string.IsNullOrWhiteSpace(r.Detail))
                ToolTipService.SetToolTip(stTb, r.Detail);
            Grid.SetColumn(stTb, 1);
            rg.Children.Add(stTb);
            listStack.Children.Add(rg);

            if (r.Status == "failed" && !string.IsNullOrWhiteSpace(r.Detail))
            {
                listStack.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = r.Detail,
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    MaxLines = 2,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, -2, 0, 2),
                }));
            }
        }
        ImportProgressContent.Children.Add(new ScrollViewer
        {
            MaxHeight = 148,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = listStack,
        });

        if (finished && revealBook is not null)
        {
            var footRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
            var bookCap = revealBook;
            var colCap = targetCollectionId;
            var revealBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                Height = 28,
                MinHeight = 28,
                FontSize = 11.5,
                Content = L10n.T("在书架里显示"),
            };
            revealBtn.Click += (_, _) =>
            {
                _importCardAutoHideTimer?.Stop();
                ImportProgressCard.Visibility = Visibility.Collapsed;
                RevealBookInShelf(bookCap.Id, colCap);
            };
            footRow.Children.Add(revealBtn);

            if (newlyImported + alreadyExisted == 1)
            {
                var openBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"],
                    Height = 28,
                    MinHeight = 28,
                    FontSize = 11.5,
                    Content = L10n.T("打开"),
                };
                openBtn.Click += (_, _) =>
                {
                    _importCardAutoHideTimer?.Stop();
                    ImportProgressCard.Visibility = Visibility.Collapsed;
                    TryOpenBook(bookCap);
                };
                footRow.Children.Add(openBtn);
            }
            ImportProgressContent.Children.Add(footRow);
        }

        ImportProgressCard.Visibility = Visibility.Visible;
    }

    private async Task PickAndImportBooksAsync()
    {
        try
        {
            var files = await NativePickerHelper.PickMultipleFilesAsync(
                _ownerWindow,
                [".epub", ".pdf", ".txt", ".mobi", ".azw3", ".azw", ".prc", ".mp3", ".m4b", ".m4a", ".zip"]);
            if (files.Count > 0)
                await ImportPathsAsync(files, openFirst: false, targetCollectionId: ActiveCollectionId);
        }
        catch (Exception ex)
        {
            ShowToast(L10n.T($"导入失败：{FormatImportError(ex)}"));
        }
    }

    private async Task PickAndImportAudiobookAsync()
    {
        try
        {
            string? folder = await NativePickerHelper.PickSingleFolderAsync(_ownerWindow);
            if (!string.IsNullOrEmpty(folder))
            {
                await ImportPathsAsync([folder], openFirst: false, targetCollectionId: ActiveCollectionId);
            }
        }
        catch (Exception ex)
        {
            ShowToast(L10n.T($"有声书导入失败：{FormatImportError(ex)}"));
        }
    }

    private void ShowWifiTransfer()
    {
        if (RootGrid.XamlRoot is { } xr)
            _ = ShelfFeaturesDialogs.ShowWifiTransferDialogAsync(xr, _library, () => DispatcherQueue.TryEnqueue(RefreshBooks));
    }

    private void ShowRemoteLibrary()
    {
        if (RootGrid.XamlRoot is { } xr)
            _ = _library.RemoteLibraries.ShowRemoteLibraryModalAsync(xr, () => DispatcherQueue.TryEnqueue(RefreshBooks));
    }

    private async Task ImportSnapshotAsync()
    {
        try
        {
            var xr = await WaitForXamlRootAsync();
            if (xr is null) return;
            await SnapshotAndConflictDialogs.ShowSnapshotImportDialogAsync(_ownerWindow, xr, _library);
        }
        catch (Exception ex)
        {
            ShowToast(L10n.T($"恢复快照失败：{FormatImportError(ex)}"));
        }
    }

    public async Task PromptAddRecordBookAsync(ReadingStatus defaultStatus)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var titleBox = new TextBox { Header = L10n.T("书名"), PlaceholderText = L10n.T("书名") };
        var authorBox = new TextBox { Header = L10n.T("作者（可选）"), PlaceholderText = L10n.T("几位作者用顿号隔开") };
        var hint = new TextBlock
        {
            Text = L10n.T("作者、页数、封面以后在书的页面里补，不补也行。"),
            FontSize = 11.5,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
        };

        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            Title = L10n.T("加一本书"),
            PrimaryButtonText = L10n.T("加入"),
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel { Spacing = 12, MinWidth = 340, Children = { titleBox, authorBox, hint } },
        };
        ConfigureDialogTheme(dlg);
        if (await dlg.ShowSafeAsync(_library.Prefs.Tint) != ContentDialogResult.Primary) return;
        string title = (titleBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(title)) return;
        string[] authors = (authorBox.Text ?? "")
            .Split(['、', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        try
        {
            var added = _library.Core.AddRecordBook(title, authors, defaultStatus);
            RefreshBooks();
            ShowToast($"已加入《{added.Title}》");
        }
        catch (Exception ex)
        {
            ShowToast($"记录失败：{ex.Message}");
        }
    }

    private async Task ShowEditBookDialogAsync(BookSummary book)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var titleBox = new TextBox { Header = L10n.T("书名"), Text = book.Title, PlaceholderText = L10n.T("留空就用文件里的") };
        var authorBox = new TextBox { Header = L10n.T("作者"), Text = string.Join("、", book.Authors), PlaceholderText = L10n.T("几位作者用顿号隔开") };
        byte[]? pickedCoverBytes = null;
        var coverStatus = new TextBlock { Text = L10n.T("保持当前封面"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var pickCoverBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Content = L10n.T("换封面"),
        };
        pickCoverBtn.Click += async (_, _) =>
        {
            try
            {
                string? img = await NativePickerHelper.PickSingleFileAsync(_ownerWindow, [".png", ".jpg", ".jpeg", ".webp"]);
                if (!string.IsNullOrEmpty(img) && File.Exists(img))
                {
                    pickedCoverBytes = await File.ReadAllBytesAsync(img);
                    coverStatus.Text = Path.GetFileName(img);
                }
            }
            catch (Exception ex)
            {
                ShowToast($"读取封面失败：{ex.Message}");
            }
        };

        var coverRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { pickCoverBtn, coverStatus } };
        var noteTb = new TextBlock
        {
            Text = L10n.T("只改书架上显示的名字，书的文件不动。"),
            FontSize = 11.5,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
        };
        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            Title = L10n.T("编辑信息"),
            PrimaryButtonText = L10n.T("好"),
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel { Spacing = 12, MinWidth = 360, Children = { titleBox, authorBox, coverRow, noteTb } },
        };
        ConfigureDialogTheme(dlg);

        if (await dlg.ShowSafeAsync(_library.Prefs.Tint) != ContentDialogResult.Primary) return;
        string newTitle = (titleBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(newTitle)) return;
        string[] newAuthors = (authorBox.Text ?? "")
            .Split(['、', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        try
        {
            _library.EditBookInfo(book.Id, newTitle, newAuthors);
            if (pickedCoverBytes is { Length: > 0 })
                _library.SetCover(book.Id, pickedCoverBytes);
            RefreshBooks();
            ShowToast("已保存书籍信息");
        }
        catch (Exception ex)
        {
            ShowToast($"保存失败：{ex.Message}");
        }
    }

    private async Task ShowPickCollectionDialogAsync(IReadOnlyList<BookSummary> books)
    {
        if (RootGrid.XamlRoot is not { } xr || books.Count == 0) return;
        var listStack = new StackPanel { Spacing = 8 };
        var checks = new List<(BookCollection Col, CheckBox Box)>();

        foreach (var col in _collections)
        {
            bool allIn = books.All(b => b.Collections.Contains(col.Id, StringComparer.OrdinalIgnoreCase));
            bool anyIn = books.Any(b => b.Collections.Contains(col.Id, StringComparer.OrdinalIgnoreCase));
            var cb = new CheckBox
            {
                Content = col.Name,
                IsThreeState = anyIn && !allIn,
                IsChecked = allIn ? true : (anyIn ? null : false),
            };
            checks.Add((col, cb));
            listStack.Children.Add(cb);
        }

        var newColBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            PlaceholderText = L10n.T("找合集，或打个新名字"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        var body = new StackPanel { Spacing = 8, MinWidth = 320 };
        body.Children.Add(new ScrollViewer { MaxHeight = 240, Content = listStack });
        body.Children.Add(newColBox);

        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            Title = books.Count == 1 ? L10n.T("加入合集") : L10n.T($"将 {books.Count} 本书加入合集"),
            PrimaryButtonText = L10n.T("好"),
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            Content = body,
        };
        ConfigureDialogTheme(dlg);
        if (await dlg.ShowSafeAsync(_library.Prefs.Tint) != ContentDialogResult.Primary) return;

        try
        {
            foreach (var (col, cb) in checks)
            {
                if (cb.IsChecked is null) continue;
                bool inside = cb.IsChecked == true;
                foreach (var b in books)
                    _library.SetInCollection(col.Id, b.Id, inside);
            }

            string newName = (newColBox.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(newName))
            {
                var created = _library.CreateCollection(newName, books[0].Id);
                for (int i = 1; i < books.Count; i++)
                    _library.SetInCollection(created.Id, books[i].Id, true);
            }
            RefreshBooks();
            ShowToast("已加入合集");
        }
        catch (Exception ex)
        {
            ShowToast($"更新合集失败：{ex.Message}");
        }
    }

    private async Task CreateNewCollectionDialogAsync()
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var nameBox = new TextBox { Header = L10n.T("起个名字，之后可以把书拖进去。"), PlaceholderText = L10n.T("合集的名字") };
        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            Title = L10n.T("新建合集"),
            PrimaryButtonText = L10n.T("建好了"),
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            Content = nameBox,
        };
        ConfigureDialogTheme(dlg);
        if (await dlg.ShowSafeAsync(_library.Prefs.Tint) != ContentDialogResult.Primary) return;
        string name = (nameBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            _library.CreateCollection(name);
            RefreshBooks();
            ShowToast($"已创建合集「{name}」");
        }
        catch (Exception ex)
        {
            ShowToast($"创建合集失败：{ex.Message}");
        }
    }

    private async Task ShowPairAudiobookDialogAsync(BookSummary book)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var audios = _shelfBooks.Where(b => b.Format == BookFormat.Audio).ToList();
        if (audios.Count == 0)
        {
            ShowToast("书架上还没有有声书，可以先导入 .m4b / .mp3 文件或文件夹后再配对。");
            return;
        }
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = audios.Select(a => a.Title).ToList(),
            SelectedIndex = 0,
        };
        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            Title = L10n.T($"合成听读书 · 《{book.Title}》"),
            PrimaryButtonText = L10n.T("开始合成"),
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 10,
                MinWidth = 340,
                Children =
                {
                    new TextBlock { Text = L10n.T("合成以后听到哪句，书页就走到哪句。"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5 },
                    combo,
                },
            },
        };
        ConfigureDialogTheme(dlg);
        if (await dlg.ShowSafeAsync(_library.Prefs.Tint) != ContentDialogResult.Primary || combo.SelectedIndex < 0) return;
        var picked = audios[combo.SelectedIndex];
        try
        {
            _library.Core.AudiobookPair(picked.Id, book.Id);
            RefreshBooks();
            ShowToast($"已配上《{picked.Title}》");
        }
        catch (Exception ex)
        {
            ShowToast($"配对失败：{ex.Message}");
        }
    }

    private async Task HandleDeleteBooksAsync(IReadOnlyList<BookSummary> books)
    {
        if (RootGrid.XamlRoot is not { } xr || books.Count == 0) return;

        // 1:1 macOS deletingRecord (BookRecord.swift:318): offer "只删文件，留下记录" when deleting a single
        // digital book that already carries progress, rating, comment, or highlights.
        bool canKeepRecordOnly = books.Count == 1
            && !books[0].Record
            && (books[0].Progress > 0.001f
                || books[0].Status == ReadingStatus.Finished
                || books[0].Rating is > 0
                || !string.IsNullOrWhiteSpace(books[0].Comment)
                || (_markCounts.TryGetValue(books[0].Id, out var mc) && mc > 0));
        bool isSinglePaper = books.Count == 1 && books[0].Record;

        string title = books.Count == 1
            ? L10n.T($"删除《{books[0].Title}》")
            : L10n.T($"删除这 {books.Count} 本书？");
        string msg = canKeepRecordOnly
            ? L10n.T("留下记录的话，评分、短评、笔记和读到哪都还在「进度」里。")
            : isSinglePaper
                ? L10n.T("评分、短评和读书记录都会一起删掉。")
                : books.Count == 1
                    ? L10n.T("删除后可以立即点击撤销恢复。")
                    : L10n.T("删除后可以立即点击撤销恢复。");

        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            Title = title,
            Content = msg,
            PrimaryButtonText = (canKeepRecordOnly || isSinglePaper) ? L10n.T("连记录一起删") : L10n.T("删除"),
            SecondaryButtonText = canKeepRecordOnly ? L10n.T("只删文件，留下记录") : null,
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Close,
        };
        ConfigureDialogTheme(dlg);
        var result = await dlg.ShowSafeAsync(_library.Prefs.Tint);
        if (result == ContentDialogResult.Secondary && canKeepRecordOnly)
        {
            var target = books[0];
            App.CurrentApp.CloseBookWindows(target.Id);
            try
            {
                _library.KeepRecordOnly(target.Id);
                _selectedBookIds.Remove(target.Id);
                RefreshBooks();
                ShowToast($"已删除《{target.Title}》", detail: "笔记和进度都留着");
            }
            catch (Exception ex)
            {
                ShowToast($"操作失败：{ex.Message}");
            }
            return;
        }
        if (result != ContentDialogResult.Primary) return;

        var deletedIds = books.Select(b => b.Id).ToList();
        foreach (var id in deletedIds)
        {
            App.CurrentApp.CloseBookWindows(id);
            try { _library.DeleteBook(id); } catch { }
            _selectedBookIds.Remove(id);
        }
        RefreshBooks();
        ShowToast(
            deletedIds.Count == 1 ? $"已删除《{books[0].Title}》" : $"已删除 {deletedIds.Count} 本书",
            canUndo: true,
            isDelete: true,
            undo: () =>
            {
                foreach (var id in deletedIds)
                    try { _library.RestoreBook(id); } catch { }
                RefreshBooks();
            });
    }

    private void RevealInExplorer(BookSummary book)
    {
        string? orig = _library.TryOriginalFile(book.Id);
        if (!string.IsNullOrEmpty(orig) && File.Exists(orig))
        {
            try { Process.Start("explorer.exe", $"/select,\"{orig}\""); } catch { }
        }
    }

    private async Task ExportAnnotatedEpubAsync(BookSummary book)
    {
        try
        {
            string? path = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, $"{book.Title}（带笔记）", [("EPUB", [".epub"])]);
            if (string.IsNullOrEmpty(path)) return;
            await _library.NoteExports.ExportAnnotatedEpubAsync(book.Id, path);
            ShowToast("导出好了", detail: Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            ShowToast($"导出失败：{ex.Message}");
        }
    }
}
