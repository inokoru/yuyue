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
        var searchBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            Width = 200,
            Text = _notesQuery,
            PlaceholderText = L10n.T("搜索划线或想法…"),
        };
        searchBox.TextChanged += (_, _) =>
        {
            _notesQuery = searchBox.Text ?? "";
            RenderNotesResults(resultsHost, dark);
        };

        var exportBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Content = L10n.T("导出笔记 ▾"),
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
        AddExp("复制全部筛选笔记为 Markdown", CopyAllNotesMarkdownAsync);
        AddExp("保存为 Markdown 文件…", SaveAllNotesMarkdownAsync);
        expMenu.Items.Add(new MenuFlyoutSeparator());
        AddExp("同步导出到 Obsidian", async () => await RunNoteExportAsync("Obsidian", () => _library.NoteExports.ExportToObsidianAsync()));
        AddExp("同步导出到 Notion", async () => await RunNoteExportAsync("Notion", () => _library.NoteExports.ExportToNotionAsync()));
        AddExp("同步导出到 flomo", async () => await RunNoteExportAsync("flomo", () => _library.NoteExports.ExportToFlomoAsync()));
        AddExp("同步导出到新枝", async () => await RunNoteExportAsync("新枝", () => _library.NoteExports.ExportToXinzhiAsync()));
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

        StatusLeftText.Text = L10n.T($"共 {allEntries.Count} 条划线与笔记");

        if (allEntries.Count == 0)
        {
            resultsHost.Children.Add(BuildSubPageEmptyCard("没有匹配的笔记", "在阅读器中划选文字即可添加划线、波浪线或批注想法。", dark));
            return;
        }

        // Today's Review spotlight card when unfiltered
        if (string.IsNullOrWhiteSpace(_notesQuery) && _notesStyleFilter == "all" && allEntries.Count >= 2)
        {
            int daySeed = DateTime.Today.DayOfYear % allEntries.Count;
            var spotlight = allEntries[daySeed];
            var reviewCard = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(tc.Soft),
                BorderBrush = ThemeHelper.Brush(tc.Edge),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 14, 18, 14),
            };
            var rStack = new StackPanel { Spacing = 8 };
            var rHead = new Grid { ColumnSpacing = 8 };
            rHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var rHeadLbl = new TextBlock
            {
                Text = L10n.T("今日重温"),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(tc.Accent),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var deckBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T($"闪卡回顾 ({allEntries.Count}) →"),
                FontSize = 11.5,
                MinHeight = 24,
                Foreground = ThemeHelper.Brush(tc.Accent),
            };
            var deckSnapshot = allEntries.ToList();
            deckBtn.Click += async (_, _) =>
            {
                try
                {
                    await ShowNotesReviewDeckDialogAsync(deckSnapshot, dark);
                }
                catch (Exception ex)
                {
                    ShowToast($"打开闪卡回顾失败：{ex.Message}");
                }
            };
            Grid.SetColumn(rHeadLbl, 0);
            Grid.SetColumn(deckBtn, 1);
            rHead.Children.Add(rHeadLbl);
            rHead.Children.Add(deckBtn);
            rStack.Children.Add(rHead);
            rStack.Children.Add(BookVisuals.BuildHighlightedQuote(spotlight.Mark, p, dark, 14.5));
            if (!string.IsNullOrWhiteSpace(spotlight.Mark.Note))
            {
                rStack.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = spotlight.Mark.Note,
                    FontSize = 12.5,
                    Foreground = ThemeHelper.Brush(p.T2),
                    TextWrapping = TextWrapping.Wrap,
                }));
            }
            var rFoot = new Grid { ColumnSpacing = 12 };
            rFoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rFoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var rBookTb = L10n.Skip(new TextBlock
            {
                Text = $"——《{spotlight.Book.Title}》",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T2),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(rBookTb, 0);
            rFoot.Children.Add(rBookTb);
            var jumpBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("回到原文 →"),
                Foreground = ThemeHelper.Brush(tc.Accent),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            jumpBtn.Click += (_, _) => TryOpenBook(spotlight.Book, section: spotlight.Mark.Section, sentence: (int)spotlight.Mark.Start.Sentence);
            Grid.SetColumn(jumpBtn, 1);
            rFoot.Children.Add(jumpBtn);
            rStack.Children.Add(rFoot);
            reviewCard.Child = rStack;
            resultsHost.Children.Add(reviewCard);
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
                Content = L10n.T("定位原文"),
                FontSize = 11.5,
                MinHeight = 24,
            };
            openBtn.Click += (_, _) => TryOpenBook(book, section: mk.Section, sentence: (int)mk.Start.Sentence);

            var editNoteBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T(string.IsNullOrWhiteSpace(mk.Note) ? "写想法" : "改想法"),
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
                        ShowToast("已保存批注想法");
                    }
                }
                catch (Exception ex)
                {
                    ShowToast($"保存批注失败：{ex.Message}");
                }
            };

            var shareBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("书摘卡"),
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
                Content = L10n.T("删除"),
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
            string? path = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, "语阅笔记", [("Markdown", [".md"])]);
            if (string.IsNullOrEmpty(path)) return;
            await File.WriteAllTextAsync(path, BuildFilteredNotesMarkdown(), Encoding.UTF8);
            ShowToast($"已保存笔记到 {Path.GetFileName(path)}");
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
            var (bCnt, mCnt) = await exportFunc();
            ShowToast($"已导出 {bCnt} 本书、{mCnt} 条笔记到 {name}");
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

        var searchBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            Width = 196,
            Text = _wordsQuery,
            PlaceholderText = L10n.T("搜索生词或释义…"),
        };
        searchBox.TextChanged += (_, _) =>
        {
            _wordsQuery = searchBox.Text ?? "";
            RenderWordsResults(resultsHost, dark);
        };

        var exportBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Content = L10n.T("导出 Anki / CSV"),
        };
        exportBtn.Click += async (_, _) =>
        {
            try
            {
                await ExportWordsCsvAsync();
            }
            catch (Exception ex)
            {
                ShowToast($"导出失败：{ex.Message}");
            }
        };

        HeaderRightExtras.Children.Add(searchBox);
        HeaderRightExtras.Children.Add(exportBtn);

        // Filter pills (exact 32px segmented track)
        var wrap = YYChrome.MakeSegmentedBar(
            [("learning", "待掌握"), ("known", "已掌握"), ("all", "全部")],
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

        StatusLeftText.Text = L10n.T($"生词 {filtered.Count} / 总计 {_words.Count}");

        if (filtered.Count == 0)
        {
            resultsHost.Children.Add(_words.Count == 0
                ? BuildSubPageEmptyCard("生词本为空", "在阅读外文书籍时双击单词查词并点「加入生词本」，词条会自动沉淀在这里。", dark)
                : BuildSubPageEmptyCard("没有匹配的生词", "清空搜索词或切换上方掌握状态筛选即可查看全部词条。", dark));
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
                Content = L10n.T(w.Known ? "标为待掌握" : "已掌握"),
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
                Content = L10n.T("移除"),
                Foreground = ThemeHelper.Brush(p.Danger),
                FontSize = 11.5,
                MinHeight = 26,
            };
            delBtn.Click += (_, _) =>
            {
                try
                {
                    _library.DeleteWord(w.Word);
                    RefreshBooks();
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

    private async Task ExportWordsCsvAsync()
    {
        try
        {
            string? path = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, "yuyue-words", [("Anki TSV", [".tsv"])]);
            if (string.IsNullOrEmpty(path)) return;
            var sb = new StringBuilder();
            foreach (var w in _words)
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
            await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8);
            ShowToast($"已导出 {_words.Count} 条生词（可直接导入 Anki）");
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

        var pinMi = new MenuFlyoutItem { Text = L10n.T(isPinned ? "取消置顶" : "置顶合集") };
        pinMi.Click += (_, _) => TogglePinCollection(col.Id);

        var renameMi = new MenuFlyoutItem { Text = L10n.T("重命名合集…") };
        renameMi.Click += async (_, _) =>
        {
            try { await RenameCollectionDialogAsync(col); }
            catch (Exception ex) { ShowToast($"重命名合集失败：{ex.Message}"); }
        };

        var iconSub = new MenuFlyoutSubItem { Text = L10n.T("更换合集图标") };
        foreach (var (icId, icLbl) in new (string?, string)[]
        {
            (null, "默认首字印章"), ("book", "书本"), ("stack", "书摞"), ("star", "星标"),
            ("heart", "喜欢"), ("bookmark", "书签"), ("sparkle", "精选"), ("globe", "世界"),
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
            _library.Prefs.PinnedCollectionIds.RemoveAll(id => string.Equals(id, col.Id, StringComparison.OrdinalIgnoreCase));
            _library.SavePrefs();
            _library.DeleteCollection(col.Id);
            RefreshBooks();
            ShowToast($"已删除合集「{col.Name}」");
        };
        menu.Items.Add(delMi);
        return menu;
    }

    // ---------- 合集墙子页 ----------
    private void RenderCollectionsSubView(bool dark)
    {
        var sortSeg = YYChrome.MakeSegmentedBar(
            [("custom", "自定义"), ("count", "书本最多"), ("name", "按名称")],
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
            MainContentStack.Children.Add(BuildSubPageEmptyCard("还没有合集", "创建合集来按主题整理书架，支持直接拖拽封面到左侧合集名上归档。", dark));
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
                Child = new TextBlock { Text = L10n.T("置顶"), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tc.Accent) },
            };
            Grid.SetColumn(pinBadge, 1);
            titleRow.Children.Add(pinBadge);
        }
        info.Children.Add(titleRow);
        info.Children.Add(new TextBlock
        {
            Text = L10n.T($"{col.Count} 本"),
            FontSize = 12,
            Foreground = ThemeHelper.Brush(p.T2),
        });
        Grid.SetColumn(info, 1);
        g.Children.Add(info);

        card.Child = g;
        card.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                SwitchTab($"col:{col.Id}");
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
            "重命名合集",
            placeholder: "合集名称",
            initialText: col.Name,
            confirmText: "保存",
            cancelText: "取消",
            subtitle: "输入新的合集名称：",
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
        int idx = DateTime.Today.DayOfYear % entries.Count;
        ContentDialog? dlgRef = null;

        var bodyHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var counterTb = new TextBlock { FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };

        void RenderCard()
        {
            var (book, mk) = entries[idx];
            counterTb.Text = $"{idx + 1} / {entries.Count}";
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
                Text = $"——《{book.Title}》",
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T2),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var jumpBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("定位原文 →"),
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

        RenderCard();

        var root = new StackPanel { Spacing = 14, Padding = new Thickness(22, 18, 22, 18) };
        root.Children.Add(YYChrome.MakeDialogHeader("笔记闪卡回顾", "重温过往的精彩划线与灵感", () => dlgRef?.Hide(), dark));
        root.Children.Add(bodyHost);

        var foot = new Grid { ColumnSpacing = 8 };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(counterTb, 0);
        foot.Children.Add(counterTb);
        var navBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        navBtns.Children.Add(YYChrome.MakeDialogButton("上一条", () => { idx = (idx - 1 + entries.Count) % entries.Count; RenderCard(); }, dark, _library.Prefs.Tint, "secondary", height: 32, horizontalPadding: 14));
        navBtns.Children.Add(YYChrome.MakeDialogButton("下一条", () => { idx = (idx + 1) % entries.Count; RenderCard(); }, dark, _library.Prefs.Tint, "primary", height: 32, horizontalPadding: 16));
        Grid.SetColumn(navBtns, 1);
        foot.Children.Add(navBtns);
        root.Children.Add(foot);

        dlgRef = YYChrome.CreateFluentDialog(xr, dark, 500, root, tint: _library.Prefs.Tint);
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
