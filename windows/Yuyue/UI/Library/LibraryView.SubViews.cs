using System.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using Yuyue.Core;
using Yuyue.Design;

namespace Yuyue.UI.Library;

public sealed partial class LibraryView
{
    private string _notesQuery = "";
    private string _notesStyleFilter = "all"; // all | thought | yellow | blue | pink | wave
    private string _wordsQuery = "";
    private string _wordsFilter = "learning"; // all | learning | known

    // ---------- 笔记与划线子页 ----------
    private void RenderNotesSubView(bool dark)
    {
        var resultsHost = new StackPanel { Spacing = 20 };

        // Header right controls: Search + Export Menu (exact 32px Fluent alignment)
        var searchBox = YYChrome.MakeSearchBox("搜索划线或想法…", _notesQuery, width: 200);
        searchBox.TextChanged += (_, _) =>
        {
            _notesQuery = searchBox.Text ?? "";
            RenderNotesResults(resultsHost, dark);
        };

        var exportBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Content = L10n.T("导出"),
        };
        var expMenu = new MenuFlyout();
        void AddExp(string label, Func<Task> action)
        {
            var mi = new MenuFlyoutItem { Text = L10n.T(label) };
            mi.Click += async (_, _) =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    ShowToast($"导出失败：{ex.Message}");
                }
            };
            expMenu.Items.Add(mi);
        }
        AddExp("复制 Markdown", CopyAllNotesMarkdownAsync);
        AddExp("Markdown…", SaveAllNotesMarkdownAsync);
        expMenu.Items.Add(new MenuFlyoutSeparator());
        AddExp("发到 Obsidian", async () => await RunNoteExportAsync("Obsidian", () => _library.NoteExports.ExportToObsidianAsync()));
        AddExp("发到 Notion", async () => await RunNoteExportAsync("Notion", () => _library.NoteExports.ExportToNotionAsync()));
        AddExp("发到 flomo", async () => await RunNoteExportAsync("flomo", () => _library.NoteExports.ExportToFlomoAsync()));
        AddExp("发到新枝", async () => await RunNoteExportAsync("新枝", () => _library.NoteExports.ExportToXinzhiAsync()));
        exportBtn.Flyout = expMenu;

        HeaderRightExtras.Children.Add(searchBox);
        HeaderRightExtras.Children.Add(exportBtn);

        // Style filter pills in header left (exact 32px segmented track)
        var styleWrap = YYChrome.MakeSegmentedBar(
            [("all", "全部"), ("thought", "有想法"), ("yellow", "黄"), ("blue", "蓝"), ("pink", "粉"), ("wave", "波浪")],
            _notesStyleFilter,
            cap =>
            {
                _notesStyleFilter = cap;
                RenderNotesResults(resultsHost, dark);
            },
            dark,
            fontSize: 12,
            height: 32);
        HeaderLeftExtras.Children.Add(styleWrap);

        MainContentStack.Children.Add(resultsHost);
        RenderNotesResults(resultsHost, dark);
    }

    private void RenderNotesResults(StackPanel resultsHost, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        resultsHost.Children.Clear();

        // Gather all marks across books
        var allEntries = new List<(BookSummary Book, Mark Mark)>();
        foreach (var book in _keptBooks)
        {
            if (!_markCounts.TryGetValue(book.Id, out var cnt) || cnt == 0) continue;
            try
            {
                foreach (var mk in _library.GetMarks(book.Id))
                {
                    if (!MatchesNoteFilter(book, mk)) continue;
                    allEntries.Add((book, mk));
                }
            }
            catch { }
        }
        allEntries.Sort((a, b) => b.Mark.At.CompareTo(a.Mark.At));

        int distinctBooks = allEntries.Select(x => x.Book.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        StatusLeftText.Text = L10n.T($"{allEntries.Count} 条 · 来自 {distinctBooks} 本书");

        if (allEntries.Count == 0)
        {
            bool anyMarkExists = _markCounts.Values.Any(c => c > 0);
            resultsHost.Children.Add(anyMarkExists
                ? BuildSubPageEmptyCard("没有匹配的笔记", "换个筛选条件试试。", dark)
                : BuildSubPageEmptyCard("还没有笔记", "读书时选中文字，按 1–4 就划上；所有书的划线和想法都会在这里", dark));
            return;
        }

        // 1:1 macOS ReviewDeckCard (MacReviewView.swift:70): compact horizontal bar over notes list
        if (string.IsNullOrWhiteSpace(_notesQuery) && _notesStyleFilter == "all")
        {
            ReviewDeck? deck = null;
            try { deck = _library.GetReviewToday(bookId: null, count: 5); } catch { }
            var booksMap = _keptBooks.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);
            var reviewItems = deck?.Items
                .Where(it => booksMap.ContainsKey(it.Book))
                .Select(it => (Book: booksMap[it.Book], Mark: it.Mark))
                .ToList() ?? [];
            uint doneToday = deck?.DoneToday ?? 0;

            if (reviewItems.Count > 0 || doneToday > 0)
            {
                var reviewCard = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(18, 12, 14, 12),
                };
                var row = new Grid { ColumnSpacing = 16 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                int estMin = Math.Max(1, (int)Math.Round(reviewItems.Count * 25.0 / 60.0));
                var leftCol = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                leftCol.Children.Add(new TextBlock
                {
                    Text = L10n.T("今天回顾"),
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = ThemeHelper.Brush(p.T1),
                });
                leftCol.Children.Add(new TextBlock
                {
                    Text = reviewItems.Count == 0
                        ? L10n.T("看完了")
                        : L10n.T($"{reviewItems.Count} 条 · 约 {estMin} 分钟"),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                });
                Grid.SetColumn(leftCol, 0);
                row.Children.Add(leftCol);

                var sep = new Border { Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 2, 0, 2) };
                Grid.SetColumn(sep, 1);
                row.Children.Add(sep);

                if (reviewItems.Count > 0)
                {
                    var first = reviewItems[0];
                    var midRow = new Grid { ColumnSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
                    midRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    midRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var quoteTb = L10n.Skip(new TextBlock
                    {
                        Text = $"“{first.Mark.Text}”",
                        FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                        FontSize = 13.5,
                        Foreground = ThemeHelper.Brush(p.T1),
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    var bookTb = L10n.Skip(new TextBlock
                    {
                        Text = first.Book.Title,
                        FontSize = 10.5,
                        Foreground = ThemeHelper.Brush(p.T3),
                        MaxLines = 1,
                        MaxWidth = 180,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    Grid.SetColumn(quoteTb, 0);
                    Grid.SetColumn(bookTb, 1);
                    midRow.Children.Add(quoteTb);
                    midRow.Children.Add(bookTb);
                    Grid.SetColumn(midRow, 2);
                    row.Children.Add(midRow);

                    var startBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"],
                        Content = L10n.T("开始"),
                        Height = 28,
                        MinHeight = 28,
                        Padding = new Thickness(16, 0, 16, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var deckSnapshot = reviewItems.ToList();
                    startBtn.Click += async (_, _) =>
                    {
                        try
                        {
                            await ShowNotesReviewDeckDialogAsync(deckSnapshot, dark);
                            RenderNotesResults(resultsHost, dark);
                        }
                        catch (Exception ex)
                        {
                            ShowToast($"打开今天回顾失败：{ex.Message}");
                        }
                    };
                    Grid.SetColumn(startBtn, 3);
                    row.Children.Add(startBtn);
                }
                else
                {
                    var doneTb = new TextBlock
                    {
                        Text = L10n.T($"今天的 {doneToday} 条看完了。留下的以后还会再出现。"),
                        FontSize = 12.5,
                        Foreground = ThemeHelper.Brush(p.T2),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(doneTb, 2);
                    Grid.SetColumnSpan(doneTb, 2);
                    row.Children.Add(doneTb);
                }

                reviewCard.Child = row;
                resultsHost.Children.Add(reviewCard);
            }
        }

        // Render note cards
        var list = new StackPanel { Spacing = 10 };
        foreach (var (book, mk) in allEntries.Take(160))
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 13, 16, 12),
            };
            var stack = new StackPanel { Spacing = 8 };

            var header = new Grid { ColumnSpacing = 12 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var bookTitle = L10n.Skip(new TextBlock
            {
                Text = $"《{book.Title}》",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T2),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(bookTitle, 0);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(actions, 1);
            var openBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("回到书里这一页"),
                FontSize = 11.5,
                MinHeight = 24,
            };
            openBtn.Click += (_, _) => TryOpenBook(book, section: mk.Section, sentence: (int)mk.Start.Sentence);

            var editNoteBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T(string.IsNullOrWhiteSpace(mk.Note) ? "写想法…" : "改想法…"),
                FontSize = 11.5,
                MinHeight = 24,
            };
            editNoteBtn.Click += async (_, _) =>
            {
                try
                {
                    if (RootGrid.XamlRoot is not { } xr) return;
                    var (saved, newNote) = await global::Yuyue.Shelf.BookRecordPageBuilder.ShowThoughtEditorSheetAsync(xr, dark, _library.Prefs.Tint, mk.Text, mk.Note);
                    if (saved)
                    {
                        _library.Core.SetMarkNote(book.Id, mk.Id, string.IsNullOrWhiteSpace(newNote) ? null : newNote.Trim());
                        RefreshBooks();
                        ShowToast("已保存想法");
                    }
                }
                catch (Exception ex)
                {
                    ShowToast($"保存想法失败：{ex.Message}");
                }
            };

            var shareBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("分享书摘…"),
                FontSize = 11.5,
                MinHeight = 24,
            };
            shareBtn.Click += async (_, _) =>
            {
                try
                {
                    if (RootGrid.XamlRoot is { } xr)
                        await global::Yuyue.Reader.QuoteShareDialog.ShowAsync(_ownerWindow, xr, mk.Text, mk.Note, book.Title, book.Authors.FirstOrDefault(), book.CoverPath, library: _library);
                }
                catch (Exception ex)
                {
                    ShowToast($"打开书摘卡失败：{ex.Message}");
                }
            };

            var delBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("删除划线"),
                FontSize = 11.5,
                MinHeight = 24,
                Foreground = ThemeHelper.Brush(p.Danger),
            };
            delBtn.Click += (_, _) =>
            {
                try
                {
                    _library.DeleteMark(book.Id, mk.Id);
                    RefreshBooks();
                }
                catch (Exception ex)
                {
                    ShowToast($"删除笔记失败：{ex.Message}");
                }
            };

            actions.Children.Add(openBtn);
            actions.Children.Add(editNoteBtn);
            actions.Children.Add(shareBtn);
            actions.Children.Add(delBtn);
            header.Children.Add(bookTitle);
            header.Children.Add(actions);

            stack.Children.Add(header);
            stack.Children.Add(BookVisuals.BuildHighlightedQuote(mk, p, dark, 13.5));

            if (!string.IsNullOrWhiteSpace(mk.Note))
            {
                var noteGrid = new Grid { ColumnSpacing = 8 };
                noteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                noteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                noteGrid.Children.Add(new Border { Background = ThemeHelper.Brush(tc.Accent), CornerRadius = new CornerRadius(1) });
                var ntb = L10n.Skip(new TextBlock
                {
                    Text = mk.Note,
                    FontSize = 12.5,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextWrapping = TextWrapping.Wrap,
                });
                Grid.SetColumn(ntb, 1);
                noteGrid.Children.Add(ntb);
                stack.Children.Add(noteGrid);
            }

            card.Child = stack;
            list.Children.Add(card);
        }
        resultsHost.Children.Add(list);
    }

    private bool MatchesNoteFilter(BookSummary book, Mark mk)
    {
        if (_notesStyleFilter == "thought" && string.IsNullOrWhiteSpace(mk.Note)) return false;
        if (_notesStyleFilter == "yellow" && mk.Style != MarkStyle.Yellow) return false;
        if (_notesStyleFilter == "blue" && mk.Style != MarkStyle.Blue) return false;
        if (_notesStyleFilter == "pink" && mk.Style != MarkStyle.Pink) return false;
        if (_notesStyleFilter == "wave" && mk.Style != MarkStyle.Wave) return false;

        if (!string.IsNullOrWhiteSpace(_notesQuery))
        {
            string q = _notesQuery.Trim();
            bool hit = mk.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(mk.Note) && mk.Note.Contains(q, StringComparison.OrdinalIgnoreCase))
                || NameMatcher.Matches(book.Title, q);
            if (!hit) return false;
        }
        return true;
    }

    private string BuildFilteredNotesMarkdown()
    {
        var sb = new StringBuilder();
        foreach (var book in _keptBooks)
        {
            var marks = _library.GetMarks(book.Id).Where(m => MatchesNoteFilter(book, m)).ToList();
            if (marks.Count == 0) continue;
            sb.AppendLine($"# {book.Title}");
            if (book.Authors.Length > 0) sb.AppendLine($"> {string.Join(" · ", book.Authors)}");
            sb.AppendLine();
            foreach (var mk in marks)
            {
                sb.AppendLine($"> {mk.Text}");
                if (!string.IsNullOrWhiteSpace(mk.Note))
                    sb.AppendLine($"\n{mk.Note}");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private Task CopyAllNotesMarkdownAsync()
    {
        try
        {
            string md = BuildFilteredNotesMarkdown();
            var dp = new DataPackage();
            dp.SetText(md);
            Clipboard.SetContent(dp);
            ShowToast("已复制 Markdown 笔记到剪贴板");
        }
        catch (Exception ex)
        {
            ShowToast($"复制笔记失败：{ex.Message}");
        }
        return Task.CompletedTask;
    }

    private async Task SaveAllNotesMarkdownAsync()
    {
        try
        {
            string? path = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, "全部笔记", [("Markdown", [".md"])]);
            if (string.IsNullOrEmpty(path)) return;
            await File.WriteAllTextAsync(path, BuildFilteredNotesMarkdown(), Encoding.UTF8);
            ShowToast("导出好了", detail: Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            ShowToast($"保存笔记失败：{ex.Message}");
        }
    }

    private async Task RunNoteExportAsync(string name, Func<Task<(int BooksCount, int MarksCount)>> exportFunc)
    {
        try
        {
            var (_, mCnt) = await exportFunc();
            ShowToast(mCnt > 0 ? $"发到了 {name}" : "没有新的划线", detail: mCnt > 0 ? $"{mCnt} 条划线" : null);
        }
        catch (Exception ex)
        {
            ShowToast($"{name} 导出失败：{ex.Message}");
        }
    }

    // ---------- 生词本子页 ----------
    private void RenderWordsSubView(bool dark)
    {
        var resultsHost = new StackPanel { Spacing = 8 };

        var searchBox = YYChrome.MakeSearchBox("搜索生词或释义…", _wordsQuery, width: 196);
        searchBox.TextChanged += (_, _) =>
        {
            _wordsQuery = searchBox.Text ?? "";
            RenderWordsResults(resultsHost, dark);
        };

        var exportBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Content = L10n.T("导出"),
        };
        var wordExpMenu = new MenuFlyout();
        var ankiMi = new MenuFlyoutItem { Text = L10n.T("Anki 卡片…") };
        ankiMi.Click += async (_, _) =>
        {
            try { await ExportWordsCsvAsync(asCsv: false); }
            catch (Exception ex) { ShowToast($"导出失败：{ex.Message}"); }
        };
        var csvMi = new MenuFlyoutItem { Text = L10n.T("CSV 表格…") };
        csvMi.Click += async (_, _) =>
        {
            try { await ExportWordsCsvAsync(asCsv: true); }
            catch (Exception ex) { ShowToast($"导出失败：{ex.Message}"); }
        };
        wordExpMenu.Items.Add(ankiMi);
        wordExpMenu.Items.Add(csvMi);
        exportBtn.Flyout = wordExpMenu;

        HeaderRightExtras.Children.Add(searchBox);
        HeaderRightExtras.Children.Add(exportBtn);

        // Filter pills (exact 32px segmented track)
        var wrap = YYChrome.MakeSegmentedBar(
            [("learning", "在学"), ("known", "已掌握"), ("all", "全部")],
            _wordsFilter,
            cap =>
            {
                _wordsFilter = cap;
                RenderWordsResults(resultsHost, dark);
            },
            dark,
            fontSize: 12,
            height: 32);
        HeaderLeftExtras.Children.Add(wrap);

        MainContentStack.Children.Add(resultsHost);
        RenderWordsResults(resultsHost, dark);
    }

    private void RenderWordsResults(StackPanel resultsHost, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        resultsHost.Children.Clear();

        var filtered = _words.Where(w =>
        {
            if (_wordsFilter == "learning" && w.Known) return false;
            if (_wordsFilter == "known" && !w.Known) return false;
            if (!string.IsNullOrWhiteSpace(_wordsQuery))
            {
                string q = _wordsQuery.Trim();
                return w.Word.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(w.Meaning) && w.Meaning.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
            return true;
        }).ToList();

        int learningCount = _words.Count(w => !w.Known);
        int knownCount = _words.Count - learningCount;
        StatusLeftText.Text = L10n.T($"在学 {learningCount} · 已掌握 {knownCount}");

        if (filtered.Count == 0)
        {
            resultsHost.Children.Add(_words.Count == 0
                ? BuildSubPageEmptyCard("还没有生词", "读外文书时点一个词，在释义卡片里点「加入生词本」，就记在这里，带着原句", dark)
                : _wordsFilter == "known"
                    ? BuildSubPageEmptyCard("暂无已掌握的词", "将在学的词标为「已掌握」后移至此处", dark)
                    : BuildSubPageEmptyCard("已全部掌握", "新查的词加入生词本后会显示在这里", dark));
            return;
        }

        var list = new StackPanel { Spacing = 8 };
        var booksById = _keptBooks.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var w in filtered.Take(200))
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 12, 16, 12),
            };
            var stack = new StackPanel { Spacing = 5 };

            var top = new Grid { ColumnSpacing = 12 };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var headRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(headRow, 0);
            headRow.Children.Add(L10n.Skip(new TextBlock
            {
                Text = w.Word,
                FontFamily = new FontFamily("Georgia, Segoe UI"),
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = ThemeHelper.Brush(p.T1),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }));
            if (!string.IsNullOrWhiteSpace(w.Phonetic))
            {
                headRow.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = w.Phonetic,
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 2),
                }));
            }

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(actions, 1);
            var knownBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                Height = 26,
                MinHeight = 26,
                FontSize = 11.5,
                Content = L10n.T(w.Known ? "再学一遍" : "已掌握"),
            };
            knownBtn.Click += (_, _) =>
            {
                try
                {
                    _library.SetWordKnown(w.Word, !w.Known);
                    RefreshBooks();
                }
                catch (Exception ex)
                {
                    ShowToast($"更新生词状态失败：{ex.Message}");
                }
            };
            var delBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("删除"),
                Foreground = ThemeHelper.Brush(p.Danger),
                FontSize = 11.5,
                MinHeight = 26,
            };
            delBtn.Click += (_, _) =>
            {
                try
                {
                    string removedWord = w.Word;
                    _library.DeleteWord(removedWord);
                    RefreshBooks();
                    ShowToast(
                        $"已删除「{removedWord}」",
                        canUndo: true,
                        isDelete: true,
                        undo: () =>
                        {
                            try { _library.RestoreWord(removedWord); } catch { }
                            RefreshBooks();
                        });
                }
                catch (Exception ex)
                {
                    ShowToast($"移除生词失败：{ex.Message}");
                }
            };
            actions.Children.Add(knownBtn);
            actions.Children.Add(delBtn);

            top.Children.Add(headRow);
            top.Children.Add(actions);
            stack.Children.Add(top);

            if (!string.IsNullOrWhiteSpace(w.Meaning))
            {
                stack.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = w.Meaning,
                    FontSize = 13,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextWrapping = TextWrapping.Wrap,
                }));
            }

            if (!string.IsNullOrWhiteSpace(w.Context))
            {
                stack.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = w.Context,
                    FontFamily = new FontFamily("Georgia, SimSun"),
                    FontSize = 12.5,
                    Foreground = ThemeHelper.Brush(p.T2),
                    TextWrapping = TextWrapping.Wrap,
                }));
            }

            if (!string.IsNullOrEmpty(w.Book) && booksById.TryGetValue(w.Book, out var srcBook))
            {
                var srcBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Content = $"《{srcBook.Title}》 →",
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(tc.Accent),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(0, 2, 4, 2),
                };
                srcBtn.Click += (_, _) => TryOpenBook(srcBook, section: w.Section, sentence: (int)w.Sentence);
                stack.Children.Add(srcBtn);
            }

            card.Child = stack;
            list.Children.Add(card);
        }
        resultsHost.Children.Add(list);
    }

    private async Task ExportWordsCsvAsync(bool asCsv = false)
    {
        try
        {
            var exportList = _words.Where(w =>
                _wordsFilter == "learning" ? !w.Known : _wordsFilter == "known" ? w.Known : true).ToList();
            string? path = asCsv
                ? await NativePickerHelper.PickSaveFileAsync(_ownerWindow, "语阅生词", [("CSV", [".csv"])])
                : await NativePickerHelper.PickSaveFileAsync(_ownerWindow, "语阅生词 Anki", [("Anki", [".txt"])]);
            if (string.IsNullOrEmpty(path)) return;
            var sb = new StringBuilder();
            if (asCsv)
            {
                sb.AppendLine("Word,Phonetic,Meaning,Context");
                static string Csv(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
                foreach (var w in exportList)
                    sb.AppendLine($"{Csv(SanitizeTsvField(w.Word))},{Csv(SanitizeTsvField(w.Phonetic))},{Csv(SanitizeTsvField(w.Meaning))},{Csv(SanitizeTsvField(w.Context))}");
            }
            else
            {
                foreach (var w in exportList)
                {
                    string cleanWord = SanitizeTsvField(w.Word);
                    string cleanPhonetic = SanitizeTsvField(w.Phonetic);
                    string cleanMeaning = SanitizeTsvField(w.Meaning);
                    string cleanCtx = SanitizeTsvField(w.Context);
                    string definition = string.IsNullOrEmpty(cleanPhonetic)
                        ? cleanMeaning
                        : (string.IsNullOrEmpty(cleanMeaning) ? cleanPhonetic : $"{cleanPhonetic} {cleanMeaning}");
                    sb.AppendLine($"{cleanWord}\t{definition}\t{cleanCtx}");
                }
            }
            await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
            ShowToast(
                "导出好了",
                detail: asCsv ? $"{exportList.Count} 个词" : $"{exportList.Count} 个词。在 Anki 里选“文件 › 导入”打开它");
        }
        catch (Exception ex)
        {
            ShowToast($"导出失败：{ex.Message}");
        }
    }

    private static string SanitizeTsvField(string? value) =>
        string.IsNullOrEmpty(value)
            ? ""
            : value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();

    private List<BookCollection> GetOrderedCollections()
    {
        var pinned = _library.Prefs.PinnedCollectionIds;
        var pinnedSet = new HashSet<string>(pinned, StringComparer.OrdinalIgnoreCase);

        var pinnedList = new List<BookCollection>();
        foreach (string pid in pinned)
        {
            var found = _collections.FirstOrDefault(c => string.Equals(c.Id, pid, StringComparison.OrdinalIgnoreCase));
            if (found is not null) pinnedList.Add(found);
        }

        IEnumerable<BookCollection> rest = _collections.Where(c => !pinnedSet.Contains(c.Id));
        rest = _library.Prefs.CollectionSort switch
        {
            "count" => rest.OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            "name" => rest.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => rest,
        };

        pinnedList.AddRange(rest);
        return pinnedList;
    }

    private void TogglePinCollection(string colId)
    {
        var pins = _library.Prefs.PinnedCollectionIds;
        int existingIdx = pins.FindIndex(id => string.Equals(id, colId, StringComparison.OrdinalIgnoreCase));
        if (existingIdx >= 0) pins.RemoveAt(existingIdx);
        else pins.Insert(0, colId);
        _library.SavePrefs();
        RefreshBooks();
    }

    private void MoveCollectionOrder(string colId, int delta)
    {
        var ids = _collections.Select(x => x.Id).ToList();
        int pos = ids.FindIndex(id => string.Equals(id, colId, StringComparison.OrdinalIgnoreCase));
        int target = pos + delta;
        if (pos < 0 || target < 0 || target >= ids.Count) return;
        (ids[pos], ids[target]) = (ids[target], ids[pos]);
        try { _library.Core.ReorderCollections(ids.ToArray()); } catch { }
        RefreshBooks();
    }

    private MenuFlyout BuildCollectionContextMenu(BookCollection col, bool dark, int indexInList, int totalCount)
    {
        var p = ThemeHelper.Palette(dark);
        bool isPinned = _library.Prefs.PinnedCollectionIds.Contains(col.Id, StringComparer.OrdinalIgnoreCase);

        var menu = new MenuFlyout();
        var openMi = new MenuFlyoutItem { Text = L10n.T("打开合集") };
        openMi.Click += (_, _) => SwitchTab($"col:{col.Id}");

        var pinMi = new MenuFlyoutItem { Text = L10n.T(isPinned ? "取消固定" : "固定到侧栏") };
        pinMi.Click += (_, _) => TogglePinCollection(col.Id);

        var renameMi = new MenuFlyoutItem { Text = L10n.T("重命名") };
        renameMi.Click += async (_, _) =>
        {
            try { await RenameCollectionDialogAsync(col); }
            catch (Exception ex) { ShowToast($"重命名合集失败：{ex.Message}"); }
        };

        var iconSub = new MenuFlyoutSubItem { Text = L10n.T("换图标") };
        foreach (var (icId, icLbl) in new (string?, string)[]
        {
            (null, "按名字自动选"), ("book", "书"), ("stack", "书摞"), ("star", "最爱"),
            ("heart", "喜欢"), ("bookmark", "书签"), ("sparkle", "精选"), ("globe", "外文"),
        })
        {
            string? capIc = icId;
            var imi = new MenuFlyoutItem { Text = L10n.T(icLbl) };
            imi.Click += (_, _) => { _library.Core.SetCollectionIcon(col.Id, capIc); RefreshBooks(); };
            iconSub.Items.Add(imi);
        }

        menu.Items.Add(openMi);
        menu.Items.Add(pinMi);
        menu.Items.Add(renameMi);
        menu.Items.Add(iconSub);

        if (_library.Prefs.CollectionSort == "custom" && totalCount > 1)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var upMi = new MenuFlyoutItem { Text = L10n.T("往前挪"), IsEnabled = indexInList > 0 };
            upMi.Click += (_, _) => MoveCollectionOrder(col.Id, -1);
            var dnMi = new MenuFlyoutItem { Text = L10n.T("往后挪"), IsEnabled = indexInList + 1 < totalCount };
            dnMi.Click += (_, _) => MoveCollectionOrder(col.Id, +1);
            menu.Items.Add(upMi);
            menu.Items.Add(dnMi);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var delMi = new MenuFlyoutItem { Text = L10n.T("删除合集"), Foreground = ThemeHelper.Brush(p.Danger) };
        delMi.Click += (_, _) =>
        {
            bool wasPinned = _library.Prefs.PinnedCollectionIds.RemoveAll(id => string.Equals(id, col.Id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (wasPinned) _library.SavePrefs();
            string removedId = col.Id;
            string removedName = col.Name;
            _library.DeleteCollection(removedId);
            RefreshBooks();
            ShowToast(
                $"已删除合集「{removedName}」",
                canUndo: true,
                isDelete: true,
                undo: () =>
                {
                    try { _library.RestoreCollection(removedId); } catch { }
                    if (wasPinned && !_library.Prefs.PinnedCollectionIds.Contains(removedId, StringComparer.OrdinalIgnoreCase))
                    {
                        _library.Prefs.PinnedCollectionIds.Insert(0, removedId);
                        _library.SavePrefs();
                    }
                    RefreshBooks();
                });
        };
        menu.Items.Add(delMi);
        return menu;
    }

    // ---------- 合集墙子页 ----------
    private void RenderCollectionsSubView(bool dark)
    {
        var sortSeg = YYChrome.MakeSegmentedBar(
            [("custom", "自己排的"), ("count", "书的多少"), ("name", "名字")],
            string.IsNullOrWhiteSpace(_library.Prefs.CollectionSort) ? "custom" : _library.Prefs.CollectionSort,
            val =>
            {
                _library.Prefs.CollectionSort = val;
                _library.SavePrefs();
                RefreshBooks();
            },
            dark,
            fontSize: 12,
            height: 32);
        HeaderLeftExtras.Children.Add(sortSeg);

        var addBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"],
            Content = L10n.T("+ 新建合集"),
        };
        addBtn.Click += (_, _) => _ = CreateNewCollectionDialogAsync();
        HeaderRightExtras.Children.Add(addBtn);

        var ordered = GetOrderedCollections();
        StatusLeftText.Text = L10n.T($"共 {ordered.Count} 个合集");

        if (ordered.Count == 0)
        {
            MainContentStack.Children.Add(BuildSubPageEmptyCard("还没有合集", "按主题、心境或阅读计划建几个书匣，把书放进去慢慢读。", dark));
            return;
        }

        double availW = Math.Max(460, SubPageAvailableWidth);
        int cols = Math.Max(2, (int)Math.Floor((availW + 16) / 252.0));
        var gridStack = new StackPanel { Spacing = 16 };

        for (int i = 0; i < ordered.Count; i += cols)
        {
            var row = new Grid { ColumnSpacing = 16 };
            for (int c = 0; c < cols; c++)
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int c = 0; c < cols && (i + c) < ordered.Count; c++)
            {
                var col = ordered[i + c];
                var card = BuildCollectionCard(col, dark, i + c, ordered.Count);
                Grid.SetColumn(card, c);
                row.Children.Add(card);
            }
            gridStack.Children.Add(row);
        }
        MainContentStack.Children.Add(gridStack);
    }

    private Border BuildCollectionCard(BookCollection col, bool dark, int indexInList, int totalCount)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var tone = BookVisuals.ResolveCollectionClothTone(col.Id, col.Icon, dark, _library.Prefs.GlobalEinkMode);
        var emb = BookVisuals.ResolveExLibrisEmblem(col.Name, col.Icon);
        bool isPinned = _library.Prefs.PinnedCollectionIds.Contains(col.Id, StringComparer.OrdinalIgnoreCase);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(isPinned ? tc.Edge : p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            ContextFlyout = BuildCollectionContextMenu(col, dark, indexInList, totalCount),
        };

        var g = new Grid { ColumnSpacing = 14 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var well = new Border
        {
            Width = 56,
            Height = 72,
            CornerRadius = new CornerRadius(6),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = tone.WellTop, Offset = 0 },
                    new GradientStop { Color = tone.WellBottom, Offset = 1 },
                },
            },
            BorderBrush = ThemeHelper.Brush(tone.WellBorder),
            BorderThickness = new Thickness(1),
            Child = emb.IsSymbol
                ? YYIcons.Make(emb.Value, 18, ThemeHelper.Brush(tone.Ink))
                : new TextBlock
                {
                    Text = emb.Value,
                    FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                    FontSize = emb.FontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(tone.Ink),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
        };
        Grid.SetColumn(well, 0);
        g.Children.Add(well);

        var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new Grid { ColumnSpacing = 6 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(L10n.Skip(new TextBlock
        {
            Text = col.Name,
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        }));
        if (isPinned)
        {
            var pinBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = ThemeHelper.Brush(tc.Soft),
                Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = L10n.T("固定在侧栏"), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tc.Accent) },
            };
            Grid.SetColumn(pinBadge, 1);
            titleRow.Children.Add(pinBadge);
        }
        info.Children.Add(titleRow);
        info.Children.Add(new TextBlock
        {
            Text = col.Count == 0 ? L10n.T("还没有书") : L10n.T($"{col.Count} 本"),
            FontSize = 12,
            Foreground = ThemeHelper.Brush(p.T2),
        });
        Grid.SetColumn(info, 1);
        g.Children.Add(info);

        card.Child = g;
        var restBorder = ThemeHelper.Brush(isPinned ? tc.Edge : p.Line);
        var hoverBorder = ThemeHelper.Brush(tc.Edge);
        var dropBorder = ThemeHelper.Brush(tc.Accent);
        var restBg = ThemeHelper.Brush(p.Card);
        var hoverBg = ThemeHelper.Brush(dark ? Color.FromArgb(255, 42, 40, 37) : Color.FromArgb(255, 250, 248, 244));
        var dropBg = ThemeHelper.Brush(tc.Soft);
        card.PointerEntered += (_, _) =>
        {
            card.BorderBrush = hoverBorder;
            card.Background = hoverBg;
        };
        card.PointerExited += (_, _) =>
        {
            card.BorderBrush = restBorder;
            card.Background = restBg;
        };
        card.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                SwitchTab($"col:{col.Id}");
        };
        card.AllowDrop = true;
        string colIdCap = col.Id;
        string colNameCap = col.Name;
        card.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Text))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.Handled = true;
                card.BorderBrush = dropBorder;
                card.Background = dropBg;
            }
        };
        card.DragLeave += (_, _) =>
        {
            card.BorderBrush = restBorder;
            card.Background = restBg;
        };
        card.Drop += async (_, e) =>
        {
            card.BorderBrush = restBorder;
            card.Background = restBg;
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.Handled = true;
                var def = e.GetDeferral();
                try
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    var paths = items.Select(it => it.Path).Where(fp => !string.IsNullOrEmpty(fp)).ToList();
                    if (paths.Count > 0)
                        await ImportPathsAsync(paths, openFirst: false, source: "drag_drop", targetCollectionId: colIdCap);
                }
                catch { }
                finally { def.Complete(); }
                return;
            }
            if (e.DataView.Contains(StandardDataFormats.Text))
            {
                e.Handled = true;
                try
                {
                    string text = await e.DataView.GetTextAsync();
                    if (text.StartsWith("yuyue-book:", StringComparison.Ordinal))
                    {
                        string bid = text["yuyue-book:".Length..];
                        _library.SetInCollection(colIdCap, bid, true);
                        RefreshBooks();
                        ShowToast($"已加入「{colNameCap}」");
                    }
                }
                catch { }
            }
        };
        return card;
    }

    private async Task RenameCollectionDialogAsync(BookCollection col)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        string? res = await YYChrome.ShowPromptAsync(
            xr,
            dark,
            "重命名",
            placeholder: "合集的名字",
            initialText: col.Name,
            confirmText: "好",
            cancelText: "取消",
            subtitle: null,
            tint: _library.Prefs.Tint);
        string name = (res ?? "").Trim();
        if (string.IsNullOrEmpty(name) || string.Equals(name, col.Name, StringComparison.Ordinal)) return;
        try
        {
            _library.Core.RenameCollection(col.Id, name);
            RefreshBooks();
            ShowToast($"已将合集重命名为「{name}」");
        }
        catch (Exception ex)
        {
            ShowToast($"重命名合集失败：{ex.Message}");
        }
    }

    private async Task ShowNotesReviewDeckDialogAsync(List<(BookSummary Book, Mark Mark)> entries, bool dark)
    {
        if (RootGrid.XamlRoot is not { } xr || entries.Count == 0) return;
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var queue = entries.ToList();
        int totalCount = queue.Count;
        int reviewedCount = 0;
        ContentDialog? dlgRef = null;

        var bodyHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var counterTb = new TextBlock { FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };

        void RenderCard()
        {
            if (queue.Count == 0)
            {
                dlgRef?.Hide();
                return;
            }
            var (book, mk) = queue[0];
            counterTb.Text = $"{reviewedCount + 1} / {totalCount}";
            var sp = new StackPanel { Spacing = 12 };
            sp.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = ThemeHelper.Brush(p.Inset),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 14, 16, 14),
                Child = BookVisuals.BuildHighlightedQuote(mk, p, dark, 15),
            });
            if (!string.IsNullOrWhiteSpace(mk.Note))
            {
                sp.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = mk.Note,
                    FontSize = 13,
                    LineHeight = 20,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextWrapping = TextWrapping.Wrap,
                }));
            }
            var metaRow = new Grid { ColumnSpacing = 8 };
            metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var srcTb = L10n.Skip(new TextBlock
            {
                Text = book.Title,
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T2),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var jumpBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("回原文 ›"),
                Foreground = ThemeHelper.Brush(tc.Accent),
            };
            jumpBtn.Click += (_, _) =>
            {
                dlgRef?.Hide();
                TryOpenBook(book, section: mk.Section, sentence: (int)mk.Start.Sentence);
            };
            Grid.SetColumn(srcTb, 0);
            Grid.SetColumn(jumpBtn, 1);
            metaRow.Children.Add(srcTb);
            metaRow.Children.Add(jumpBtn);
            sp.Children.Add(metaRow);
            bodyHost.Content = sp;
        }

        void MarkReviewed(bool keep)
        {
            if (queue.Count == 0) return;
            var (book, mk) = queue[0];
            try { _library.SetMarkReview(book.Id, mk.Id, keep); } catch { }
            queue.RemoveAt(0);
            reviewedCount++;
            if (queue.Count == 0)
            {
                dlgRef?.Hide();
                ShowToast($"今天的 {totalCount} 条看完了");
                return;
            }
            RenderCard();
        }

        RenderCard();

        var root = new StackPanel { Spacing = 14, Padding = new Thickness(22, 18, 22, 18) };
        root.Children.Add(YYChrome.MakeDialogHeader("今天回顾", "留下的以后还会再出现。不要了的只是不再回顾，笔记还在。", () => dlgRef?.Hide(), dark));
        root.Children.Add(bodyHost);

        var foot = new Grid { ColumnSpacing = 8 };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(counterTb, 0);
        foot.Children.Add(counterTb);
        var navBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        navBtns.Children.Add(YYChrome.MakeDialogButton("不要了", () => MarkReviewed(keep: false), dark, _library.Prefs.Tint, "secondary", height: 32, horizontalPadding: 14));
        navBtns.Children.Add(YYChrome.MakeDialogButton("留下", () => MarkReviewed(keep: true), dark, _library.Prefs.Tint, "primary", height: 32, horizontalPadding: 16));
        Grid.SetColumn(navBtns, 1);
        foot.Children.Add(navBtns);
        root.Children.Add(foot);

        dlgRef = YYChrome.CreateFluentDialog(xr, dark, 500, root, tint: _library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
    }

    /// <summary>
    /// 1:1 port of macOS <c>MacStatsSheet</c> (<c>MacBooksView.swift:233</c>):
    /// opened from the header "阅读统计" button on the 阅读进度 page, with 周/月/年 switcher,
    /// period steppers, stacked read+listen bar chart, annual heatmap (Year tab only), and per-book list.
    /// </summary>
    private async Task ShowReadingStatsDialogAsync(bool dark)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        string period = "year"; // week | month | year
        DateOnly anchor = DateOnly.FromDateTime(DateTime.Today);
        ContentDialog? dlgRef = null;

        var contentHost = new StackPanel { Spacing = 14 };

        static string FormatStatDuration(long seconds)
        {
            long m = seconds / 60;
            if (m < 60) return L10n.T($"{m} 分");
            return m % 60 == 0 ? L10n.T($"{m / 60} 小时") : L10n.T($"{m / 60} 小时 {m % 60} 分");
        }

        (DateOnly Start, DateOnly End, List<DateOnly> Days) ComputeRange(string kind, DateOnly a)
        {
            if (kind == "week")
            {
                int lead = ((int)a.DayOfWeek + 6) % 7;
                var start = a.AddDays(-lead);
                var days = Enumerable.Range(0, 7).Select(i => start.AddDays(i)).ToList();
                return (start, start.AddDays(6), days);
            }
            if (kind == "month")
            {
                var start = new DateOnly(a.Year, a.Month, 1);
                int n = DateTime.DaysInMonth(a.Year, a.Month);
                var days = Enumerable.Range(0, n).Select(i => start.AddDays(i)).ToList();
                return (start, start.AddDays(n - 1), days);
            }
            var yStart = new DateOnly(a.Year, 1, 1);
            var yEnd = new DateOnly(a.Year, 12, 31);
            int yDays = yEnd.DayNumber - yStart.DayNumber + 1;
            var allDays = Enumerable.Range(0, yDays).Select(i => yStart.AddDays(i)).ToList();
            return (yStart, yEnd, allDays);
        }

        void RenderStatsBody()
        {
            contentHost.Children.Clear();
            DateOnly today = DateOnly.FromDateTime(DateTime.Today);
            var (start, end, days) = ComputeRange(period, anchor);
            bool isCurrent = today >= start && today <= end;

            IReadOnlyList<DayTime> times;
            try
            {
                times = _library.GetDayTimes(
                    ReadingHeatmapBuilder.FormatDayKey(start),
                    ReadingHeatmapBuilder.FormatDayKey(end));
            }
            catch
            {
                times = Array.Empty<DayTime>();
            }

            long readTotal = times.Sum(t => (long)t.ReadSeconds);
            long listenTotal = times.Sum(t => (long)t.ListenSeconds);

            string periodTitle;
            string periodRange;
            string periodWord;
            DateOnly shownEnd = isCurrent && today < end ? today : end;
            if (period == "week")
            {
                periodWord = isCurrent ? L10n.T("这周") : L10n.T("那一周");
                periodTitle = periodWord;
                periodRange = start.Month == end.Month
                    ? $"{start.Month} 月 {start.Day} 日 – {end.Day} 日"
                    : $"{start.Month} 月 {start.Day} 日 – {end.Month} 月 {end.Day} 日";
            }
            else if (period == "month")
            {
                periodWord = isCurrent ? L10n.T("这个月") : L10n.T("那个月");
                periodTitle = $"{start.Month} 月";
                periodRange = $"1 日 – {shownEnd.Day} 日";
            }
            else
            {
                periodWord = isCurrent ? L10n.T("今年") : L10n.T("那年");
                periodTitle = $"{start.Year} 年";
                periodRange = $"1 – {shownEnd.Month} 月";
            }

            // Period card
            var cardStack = new StackPanel { Spacing = 0 };
            var headRow = new Grid { ColumnSpacing = 8 };
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titlePair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            titlePair.Children.Add(L10n.Skip(new TextBlock { Text = periodTitle, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Bottom }));
            titlePair.Children.Add(L10n.Skip(new TextBlock { Text = periodRange, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Bottom }));
            Grid.SetColumn(titlePair, 0);
            headRow.Children.Add(titlePair);

            var navRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            Button MakeStep(string icon, bool enabled, Action onClick)
            {
                var b = new Button
                {
                    Width = 24,
                    Height = 24,
                    MinWidth = 24,
                    MinHeight = 24,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Inset),
                    BorderThickness = new Thickness(0),
                    IsEnabled = enabled,
                    Opacity = enabled ? 1.0 : 0.4,
                    UseSystemFocusVisuals = false,
                    Content = YYIcons.Make(icon, 10, ThemeHelper.Brush(p.T1)),
                };
                b.Click += (_, _) => onClick();
                return b;
            }
            navRow.Children.Add(MakeStep("chevron-left", true, () =>
            {
                anchor = period switch { "week" => anchor.AddDays(-7), "month" => anchor.AddMonths(-1), _ => anchor.AddYears(-1) };
                RenderStatsBody();
            }));
            navRow.Children.Add(MakeStep("chevron-right", !isCurrent, () =>
            {
                anchor = period switch { "week" => anchor.AddDays(7), "month" => anchor.AddMonths(1), _ => anchor.AddYears(1) };
                RenderStatsBody();
            }));
            Grid.SetColumn(navRow, 1);
            headRow.Children.Add(navRow);
            cardStack.Children.Add(headRow);

            // Totals row (读 / 听)
            var totalsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(0, 10, 0, 0) };
            StackPanel MakeStatTotal(string lbl, long secs, Color dot)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2), Background = ThemeHelper.Brush(dot), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = L10n.T(lbl), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Bottom });
                sp.Children.Add(L10n.Skip(new TextBlock { Text = FormatStatDuration(secs), FontSize = 18, FontWeight = FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Bottom }));
                return sp;
            }
            totalsRow.Children.Add(MakeStatTotal("读", readTotal, tc.Accent));
            totalsRow.Children.Add(MakeStatTotal("听", listenTotal, p.Tint));
            cardStack.Children.Add(totalsRow);

            // Stacked bar chart (12 months for year, N days for month, 7 days for week)
            var slots = new List<(long Read, long Listen, string AxisLabel, bool IsFuture)>();
            if (period == "year")
            {
                for (int m = 1; m <= 12; m++)
                {
                    string prefix = $"{start.Year:D4}-{m:D2}";
                    long r = times.Where(t => t.Day.StartsWith(prefix, StringComparison.Ordinal)).Sum(t => (long)t.ReadSeconds);
                    long l = times.Where(t => t.Day.StartsWith(prefix, StringComparison.Ordinal)).Sum(t => (long)t.ListenSeconds);
                    bool fut = new DateOnly(start.Year, m, 1) > today;
                    slots.Add((r, l, m % 2 == 1 ? $"{m} 月" : "", fut));
                }
            }
            else
            {
                string[] weekLabels = ["一", "二", "三", "四", "五", "六", "日"];
                for (int i = 0; i < days.Count; i++)
                {
                    var d = days[i];
                    string key = ReadingHeatmapBuilder.FormatDayKey(d);
                    long r = times.Where(t => t.Day == key).Sum(t => (long)t.ReadSeconds);
                    long l = times.Where(t => t.Day == key).Sum(t => (long)t.ListenSeconds);
                    string lbl = period == "week" ? weekLabels[i] : (i % 7 == 0 ? $"{i + 1}" : "");
                    slots.Add((r, l, lbl, d > today));
                }
            }

            long maxVal = Math.Max(1, slots.Max(s => s.Read + s.Listen));
            const double chartH = 60.0;
            double barW = period == "week" ? 12.0 : 7.0;
            var barsGrid = new Grid { Height = chartH, Margin = new Thickness(0, 14, 0, 0) };
            var axisGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            for (int i = 0; i < slots.Count; i++)
            {
                barsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                axisGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var s = slots[i];

                var colStack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 1 };
                if (s.IsFuture)
                {
                    colStack.Children.Add(new Border { Width = barW, Height = 2, CornerRadius = new CornerRadius(1), Background = ThemeHelper.Brush(p.Line) });
                }
                else if (s.Read + s.Listen > 0)
                {
                    if (s.Listen > 0)
                        colStack.Children.Add(new Border { Width = barW, Height = Math.Max(2, chartH * s.Listen / maxVal), CornerRadius = new CornerRadius(2, 2, 0, 0), Background = ThemeHelper.Brush(p.Tint) });
                    if (s.Read > 0)
                        colStack.Children.Add(new Border { Width = barW, Height = Math.Max(2, chartH * s.Read / maxVal), CornerRadius = s.Listen > 0 ? new CornerRadius(0) : new CornerRadius(2, 2, 0, 0), Background = ThemeHelper.Brush(tc.Accent) });
                }
                else
                {
                    colStack.Children.Add(new Border { Width = barW, Height = 2, CornerRadius = new CornerRadius(1), Background = ThemeHelper.Brush(p.Inset) });
                }
                Grid.SetColumn(colStack, i);
                barsGrid.Children.Add(colStack);

                if (!string.IsNullOrEmpty(s.AxisLabel))
                {
                    var axTb = L10n.Skip(new TextBlock { Text = s.AxisLabel, FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Center });
                    Grid.SetColumn(axTb, i);
                    axisGrid.Children.Add(axTb);
                }
            }
            cardStack.Children.Add(barsGrid);
            cardStack.Children.Add(axisGrid);

            // Annual heatmap shown ONLY on Year tab (1:1 MacStatsSheet)
            if (period == "year")
            {
                cardStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 14, 0, 12) });
                var hm = ReadingHeatmapBuilder.BuildForYear(times, start.Year, today);
                var hmGrid = new Grid { ColumnSpacing = 2, RowSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
                for (int r = 0; r < 7; r++)
                    hmGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
                for (int w = 0; w < hm.Weeks.Count; w++)
                {
                    hmGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                    var col = hm.Weeks[w];
                    for (int d = 0; d < 7; d++)
                    {
                        var cell = col[d];
                        Color fill = cell.IsFuture
                            ? Colors.Transparent
                            : cell.Level switch
                            {
                                0 => p.Inset,
                                1 => Color.FromArgb(72, tc.Accent.R, tc.Accent.G, tc.Accent.B),
                                2 => Color.FromArgb(132, tc.Accent.R, tc.Accent.G, tc.Accent.B),
                                3 => Color.FromArgb(196, tc.Accent.R, tc.Accent.G, tc.Accent.B),
                                _ => tc.Accent,
                            };
                        var box = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = ThemeHelper.Brush(fill) };
                        if (!cell.IsFuture)
                            ToolTipService.SetToolTip(box, cell.TotalSeconds > 0 ? $"{cell.DayKey} · {FormatStatDuration(cell.TotalSeconds)}" : $"{cell.DayKey} · {L10n.T("未阅读")}");
                        Grid.SetColumn(box, w);
                        Grid.SetRow(box, d);
                        hmGrid.Children.Add(box);
                    }
                }
                cardStack.Children.Add(new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = hmGrid,
                });
            }

            contentHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16),
                Child = cardStack,
            });

            // Per-book duration breakdown (books with >= 60s in this period)
            var booksById = _keptBooks.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);
            var perBook = times
                .GroupBy(t => t.Book, StringComparer.OrdinalIgnoreCase)
                .Select(g => (BookId: g.Key, Read: g.Sum(x => (long)x.ReadSeconds), Listen: g.Sum(x => (long)x.ListenSeconds)))
                .Where(x => x.Read + x.Listen >= 60 && booksById.ContainsKey(x.BookId))
                .OrderByDescending(x => x.Read + x.Listen)
                .ToList();

            if (perBook.Count > 0)
            {
                var secHead = new Grid { Margin = new Thickness(4, 2, 4, 0) };
                secHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                secHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                secHead.Children.Add(new TextBlock { Text = L10n.T("每本书"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) });
                var sub = L10n.Skip(new TextBlock { Text = $"{periodWord} · {L10n.T("按时长")}", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
                Grid.SetColumn(sub, 1);
                secHead.Children.Add(sub);
                contentHost.Children.Add(secHead);

                var bookRows = new StackPanel { Spacing = 0 };
                for (int i = 0; i < perBook.Count; i++)
                {
                    if (i > 0) bookRows.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });
                    var item = perBook[i];
                    var book = booksById[item.BookId];
                    var bRow = new Grid { Padding = new Thickness(0, 9, 0, 9), ColumnSpacing = 8 };
                    bRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    bRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var left = new StackPanel { Spacing = 2 };
                    left.Children.Add(L10n.Skip(new TextBlock { Text = book.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }));
                    var parts = new List<string>();
                    if (item.Read > 0) parts.Add($"{L10n.T("读")} {FormatStatDuration(item.Read)}");
                    if (item.Listen > 0) parts.Add($"{L10n.T("听")} {FormatStatDuration(item.Listen)}");
                    left.Children.Add(L10n.Skip(new TextBlock { Text = string.Join(" · ", parts), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) }));
                    Grid.SetColumn(left, 0);
                    bRow.Children.Add(left);

                    string rightText = book.Status == ReadingStatus.Finished
                        ? L10n.T("读完了")
                        : $"{(int)Math.Round(Math.Clamp(book.Progress, 0f, 1f) * 100)}%";
                    var rTb = new TextBlock { Text = rightText, FontSize = 11, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(rTb, 1);
                    bRow.Children.Add(rTb);
                    bookRows.Children.Add(bRow);
                }

                contentHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(14, 2, 14, 2),
                    Child = bookRows,
                });
            }
        }

        var topBar = new Grid { ColumnSpacing = 12 };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        topBar.Children.Add(new TextBlock
        {
            Text = L10n.T("阅读统计"),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = ThemeHelper.Brush(p.T1),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var seg = YYChrome.MakeSegmentedBar(
            [("week", "周"), ("month", "月"), ("year", "年")],
            period,
            next =>
            {
                period = next;
                anchor = DateOnly.FromDateTime(DateTime.Today);
                RenderStatsBody();
            },
            dark,
            fontSize: 11.5,
            height: 28);
        Grid.SetColumn(seg, 1);
        topBar.Children.Add(seg);

        var doneBtn = YYChrome.MakeDialogButton("完成", () => dlgRef?.Hide(), dark, _library.Prefs.Tint, "secondary", height: 28, horizontalPadding: 14);
        Grid.SetColumn(doneBtn, 2);
        topBar.Children.Add(doneBtn);

        RenderStatsBody();

        var outer = new StackPanel { Spacing = 14, Padding = new Thickness(22, 18, 22, 18) };
        outer.Children.Add(topBar);
        outer.Children.Add(new ScrollViewer { MaxHeight = 460, Content = contentHost });

        dlgRef = YYChrome.CreateFluentDialog(xr, dark, 600, outer, maxHeight: 560, tint: _library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
    }

    private Border BuildSubPageEmptyCard(string title, string desc, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(32, 48, 32, 48),
            Child = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = L10n.T(title), FontSize = 15.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = L10n.T(desc), FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 420 },
                },
            },
        };
    }
}
