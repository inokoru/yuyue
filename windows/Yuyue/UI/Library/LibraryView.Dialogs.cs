using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        CoreException.Protected => L10n.T("文件受加密或 DRM 保护，无法导入"),
        CoreException.Unsupported => L10n.T("暂不支持该文件格式"),
        CoreException.BadBook bad => FormatBadBookError(bad.detail),
        CoreException.NotFound => L10n.T("找不到原文件"),
        CoreException.Io io => string.IsNullOrWhiteSpace(io.detail) ? L10n.T("无法读取文件") : L10n.T($"文件读写失败：{io.detail}"),
        CoreException.Invalid inv => string.IsNullOrWhiteSpace(inv.detail) ? L10n.T("文件参数无效") : inv.detail,
        CoreException.Db db => string.IsNullOrWhiteSpace(db.detail) ? L10n.T("书库写入失败") : L10n.T($"书库写入失败：{db.detail}"),
        _ => string.IsNullOrWhiteSpace(ex.Message) ? L10n.T("未知错误") : ex.Message,
    };

    private static string FormatBadBookError(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return L10n.T("文件已损坏或无法打开");
        if (detail.Contains("empty", StringComparison.OrdinalIgnoreCase))
            return L10n.T("文件是空的");
        if (detail.Contains("large", StringComparison.OrdinalIgnoreCase) || detail.Contains("size", StringComparison.OrdinalIgnoreCase))
            return L10n.T("文件过大，无法导入");
        return L10n.T("文件已损坏或无法打开");
    }

    public async Task ImportPathsAsync(IReadOnlyList<string> rawPaths, bool openFirst = false, string source = "picker")
    {
        if (rawPaths.Count == 0) return;

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
                        RefreshBooks();
                        ShowToast(L10n.T($"已导入有声书《{importedAudio.Title}》"));
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

        BookSummary? firstImported = null;
        int ok = 0;
        for (int i = 0; i < bookFiles.Count; i++)
        {
            if (bookFiles.Count > 1)
                ShowToast(L10n.T($"正在导入 {i + 1} / {bookFiles.Count}…"));
            try
            {
                var imported = await _library.ImportBookWithExtrasAsync(bookFiles[i]);
                firstImported ??= imported;
                ok++;
            }
            catch (Exception ex)
            {
                failures.Add((Path.GetFileName(bookFiles[i]), FormatImportError(ex)));
            }
        }

        if (ok > 0)
        {
            RefreshBooks();
            if (failures.Count == 0)
            {
                ShowToast(ok == 1 && firstImported is not null
                    ? L10n.T($"已导入《{firstImported.Title}》")
                    : L10n.T($"已成功导入 {ok} 本书"));
            }
            else
            {
                var firstFail = failures[0];
                string failDetail = failures.Count == 1
                    ? $"{firstFail.FileName}：{firstFail.Reason}"
                    : L10n.T($"{firstFail.FileName}：{firstFail.Reason} 等 {failures.Count} 个文件");
                ShowToast(
                    L10n.T($"已导入 {ok} 本，{failures.Count} 本失败"),
                    detail: failDetail);
            }
            if (openFirst && firstImported is not null)
                TryOpenBook(firstImported);
        }
        else if (failures.Count > 0)
        {
            var firstFail = failures[0];
            if (failures.Count == 1)
            {
                ShowToast(L10n.T($"导入失败 ({firstFail.FileName})：{firstFail.Reason}"));
            }
            else
            {
                ShowToast(
                    L10n.T($"导入失败，共 {failures.Count} 个文件未能导入"),
                    detail: $"{firstFail.FileName}：{firstFail.Reason}");
            }
        }
    }

    private async Task PickAndImportBooksAsync()
    {
        try
        {
            var files = await NativePickerHelper.PickMultipleFilesAsync(
                _ownerWindow,
                [".epub", ".pdf", ".txt", ".mobi", ".azw3", ".azw", ".prc", ".mp3", ".m4b", ".m4a", ".zip"]);
            if (files.Count > 0)
                await ImportPathsAsync(files, openFirst: false);
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
                await ImportPathsAsync([folder], openFirst: false);
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
        var titleBox = new TextBox { Header = L10n.T("书名"), PlaceholderText = L10n.T("输入纸质书书名") };
        var authorBox = new TextBox { Header = L10n.T("作者（可选）"), PlaceholderText = L10n.T("多位作者用顿号或逗号隔开") };

        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            Title = L10n.T("记一本纸质书"),
            PrimaryButtonText = L10n.T("加入书架"),
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel { Spacing = 12, MinWidth = 340, Children = { titleBox, authorBox } },
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
            ShowToast($"已记录纸质书《{added.Title}》");
        }
        catch (Exception ex)
        {
            ShowToast($"记录失败：{ex.Message}");
        }
    }

    private async Task ShowEditBookDialogAsync(BookSummary book)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var titleBox = new TextBox { Header = L10n.T("书名"), Text = book.Title };
        var authorBox = new TextBox { Header = L10n.T("作者"), Text = string.Join("、", book.Authors) };
        byte[]? pickedCoverBytes = null;
        var coverStatus = new TextBlock { Text = L10n.T("保持当前封面"), FontSize = 12 };
        var pickCoverBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Content = L10n.T("更换封面图片…"),
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
        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            Title = L10n.T("编辑书籍信息"),
            PrimaryButtonText = L10n.T("保存"),
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel { Spacing = 12, MinWidth = 360, Children = { titleBox, authorBox, coverRow } },
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
            PlaceholderText = L10n.T("或输入新合集名称直接创建…"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        var body = new StackPanel { Spacing = 8, MinWidth = 320 };
        body.Children.Add(new ScrollViewer { MaxHeight = 240, Content = listStack });
        body.Children.Add(newColBox);

        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            Title = books.Count == 1 ? L10n.T($"加入合集 · 《{books[0].Title}》") : L10n.T($"将 {books.Count} 本书加入合集"),
            PrimaryButtonText = L10n.T("确定"),
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
            ShowToast("已更新合集归档");
        }
        catch (Exception ex)
        {
            ShowToast($"更新合集失败：{ex.Message}");
        }
    }

    private async Task CreateNewCollectionDialogAsync()
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var nameBox = new TextBox { Header = L10n.T("合集名称"), PlaceholderText = L10n.T("例如：文学经典、2026 必读") };
        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            Title = L10n.T("新建合集"),
            PrimaryButtonText = L10n.T("创建"),
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
            ShowToast("书库中暂无可配对的有声书，请先导入有声书");
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
            Title = L10n.T($"为《{book.Title}》配对有声书"),
            PrimaryButtonText = L10n.T("配对并后台对齐"),
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 10,
                MinWidth = 340,
                Children =
                {
                    new TextBlock { Text = L10n.T("配对后可在阅读器内直接切换真人原声跟读，文字随声音逐句推进。"), TextWrapping = TextWrapping.Wrap, FontSize = 12.5 },
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
            ShowToast($"已配对《{picked.Title}》");
        }
        catch (Exception ex)
        {
            ShowToast($"配对失败：{ex.Message}");
        }
    }

    private async Task HandleDeleteBooksAsync(IReadOnlyList<BookSummary> books)
    {
        if (RootGrid.XamlRoot is not { } xr || books.Count == 0) return;
        string msg = books.Count == 1
            ? L10n.T($"确定要将《{books[0].Title}》移出语阅书库吗？移出后可立即点击撤销恢复。")
            : L10n.T($"确定要将选中的 {books.Count} 本书移出语阅书库吗？");

        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            Title = L10n.T("移出书库"),
            Content = msg,
            PrimaryButtonText = L10n.T("移出"),
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Close,
        };
        ConfigureDialogTheme(dlg);
        if (await dlg.ShowSafeAsync(_library.Prefs.Tint) != ContentDialogResult.Primary) return;

        var deletedIds = books.Select(b => b.Id).ToList();
        foreach (var id in deletedIds)
        {
            App.CurrentApp.CloseBookWindows(id);
            try { _library.DeleteBook(id); } catch { }
            _selectedBookIds.Remove(id);
        }
        RefreshBooks();
        ShowToast(
            deletedIds.Count == 1 ? $"已移出《{books[0].Title}》" : $"已移出 {deletedIds.Count} 本书",
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
            string? path = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, $"{book.Title}-笔记版", [("EPUB", [".epub"])]);
            if (string.IsNullOrEmpty(path)) return;
            await _library.NoteExports.ExportAnnotatedEpubAsync(book.Id, path);
            ShowToast($"已导出带笔记的 EPUB：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            ShowToast($"导出失败：{ex.Message}");
        }
    }
}
