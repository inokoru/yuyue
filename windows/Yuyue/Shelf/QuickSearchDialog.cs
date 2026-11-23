using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Yuyue.Core;
using Yuyue.Design;

namespace Yuyue.Shelf;

/// <summary>
/// Shared cross-library QuickSearch modal (<c>Ctrl+K</c>) usable from ReaderWindow, PdfReaderWindow, and ShelfWindow.
/// Matches books by title/author/Pinyin initials (<see cref="NameMatcher"/>), searches marks &amp; full-text across ALL books
/// (round-robin, no 12/16-book cap), highlights matched keywords, and supports keyboard Up/Down/Enter navigation.
/// </summary>
public static class QuickSearchDialog
{
    public static async Task ShowAsync(
        XamlRoot xamlRoot,
        LibraryService library,
        string? currentBookId = null,
        Action<string>? onAskCompanionInCurrentBook = null)
    {
        bool dark = xamlRoot.Content is FrameworkElement fe && ThemeHelper.IsDark(fe.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        ContentDialog? dlg = null;
        CancellationTokenSource? searchCts = null;
        var items = new List<(Button Btn, Action Activate)>();
        int selectedIdx = -1;

        var searchBox = new TextBox
        {
            PlaceholderText = "搜书名（支持拼音首字母）、笔记或全书正文…",
            FontSize = 13.5,
            CornerRadius = new CornerRadius(10),
        };
        var resultsPanel = new StackPanel { Spacing = 4 };
        var statusTb = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), Visibility = Visibility.Collapsed };

        void HighlightSelection()
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Btn.Background = i == selectedIdx
                    ? ThemeHelper.Brush(tint.Soft)
                    : new SolidColorBrush(Colors.Transparent);
            }
        }

        void AddSectionTitle(string text)
        {
            resultsPanel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(6, 6, 6, 2),
            });
        }

        void AddRow(string icon, string title, string subtitle, string? highlightQuery, Action activate)
        {
            var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 7, 8, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(YYIcons.Make(icon, 13, ThemeHelper.Brush(tint.Accent)));

            var sp = new StackPanel { Spacing = 2 };
            sp.Children.Add(BuildHighlightedBlock(title, highlightQuery, 12.5, FontWeights.SemiBold, p.T1, tint.Accent));
            if (!string.IsNullOrWhiteSpace(subtitle))
                sp.Children.Add(BuildHighlightedBlock(subtitle, highlightQuery, 11, FontWeights.Normal, p.T3, tint.Accent));
            Grid.SetColumn(sp, 1);
            grid.Children.Add(sp);

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = grid,
            };
            Action run = () => { dlg?.Hide(); activate(); };
            btn.Click += (_, _) => run();
            items.Add((btn, run));
            resultsPanel.Children.Add(btn);
        }

        async Task RefreshAsync(string rawQuery)
        {
            searchCts?.Cancel();
            searchCts?.Dispose();
            searchCts = new CancellationTokenSource();
            var ct = searchCts.Token;

            string q = rawQuery.Trim();
            resultsPanel.Children.Clear();
            items.Clear();
            selectedIdx = -1;
            statusTb.Visibility = Visibility.Collapsed;

            var allBooks = library.GetKeptBooks().OrderByDescending(b => b.OpenedAt ?? b.AddedAt).ToArray();
            if (string.IsNullOrEmpty(q))
            {
                AddSectionTitle("最近阅读");
                foreach (var b in allBooks.Take(8))
                {
                    var book = b;
                    string sub = string.Join(" · ", new[] { string.Join("、", book.Authors), $"已读 {(int)Math.Round(book.Progress * 100)}%" }.Where(s => !string.IsNullOrEmpty(s)));
                    AddRow("book", book.Title, sub, null, () => App.CurrentApp.OpenBook(book.Id));
                }
                if (items.Count > 0) { selectedIdx = 0; HighlightSelection(); }
                return;
            }

            // 1. Books (title + authors + Pinyin initials)
            var bookHits = allBooks
                .Where(b => NameMatcher.Matches(b.Title, q) || b.Authors.Any(a => NameMatcher.Matches(a, q)))
                .Take(8)
                .ToArray();
            if (bookHits.Length > 0)
            {
                AddSectionTitle($"书籍 · {bookHits.Length}");
                foreach (var b in bookHits)
                {
                    var book = b;
                    string sub = string.Join(" · ", new[] { string.Join("、", book.Authors), $"已读 {(int)Math.Round(book.Progress * 100)}%" }.Where(s => !string.IsNullOrEmpty(s)));
                    AddRow("book", book.Title, sub, q, () => App.CurrentApp.OpenBook(book.Id));
                }
            }

            // 2. Marks across ALL books (no cap on book count)
            var markHits = new List<(BookSummary Book, Mark Mark)>();
            foreach (var b in allBooks)
            {
                if (markHits.Count >= 10) break;
                try
                {
                    foreach (var m in library.GetMarks(b.Id))
                    {
                        if (m.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrEmpty(m.Note) && m.Note.Contains(q, StringComparison.OrdinalIgnoreCase)))
                        {
                            markHits.Add((b, m));
                            if (markHits.Count >= 10) break;
                        }
                    }
                }
                catch { }
            }
            if (markHits.Count > 0)
            {
                AddSectionTitle($"笔记与划线 · {markHits.Count}");
                foreach (var (book, mk) in markHits)
                {
                    var b = book;
                    var m = mk;
                    string snippet = !string.IsNullOrWhiteSpace(m.Note) ? $"{m.Text} — 💭 {m.Note}" : m.Text;
                    AddRow("note-card", snippet, $"《{b.Title}》", q, () => App.CurrentApp.OpenBook(b.Id, section: m.Section, sentence: (int)m.Start.Sentence));
                }
            }

            // 3. Companion quick action
            if (onAskCompanionInCurrentBook is not null && !string.IsNullOrEmpty(currentBookId))
            {
                AddSectionTitle("书伴提问");
                AddRow("sparkle", $"问书伴：{q}", "在当前书里基于已读内容解答（不剧透）", null, () => onAskCompanionInCurrentBook(q));
            }
            else if (allBooks.Length > 0)
            {
                var topBook = allBooks[0];
                AddSectionTitle("书伴提问");
                AddRow("sparkle", $"在《{topBook.Title}》里问书伴：{q}", "打开当前在读书的书伴直接解答", null, () => App.CurrentApp.OpenBook(topBook.Id, askQuestion: q));
            }

            if (items.Count > 0) { selectedIdx = 0; HighlightSelection(); }

            // 4. Async full-text passage search across ALL text books (round-robin, up to 18 hits)
            if (q.Length >= 2)
            {
                statusTb.Text = "正在检索全部书籍正文…";
                statusTb.Visibility = Visibility.Visible;
                var textBooks = allBooks.Where(b => b.Format != BookFormat.Audio).ToArray();
                var passageHits = await Task.Run(() =>
                {
                    var hits = new List<(BookSummary Book, SearchHit Hit, string Chapter)>();
                    // Round-robin: first pass 2 hits per book across ALL books so later books are never starved
                    foreach (var b in textBooks)
                    {
                        if (ct.IsCancellationRequested || hits.Count >= 18) break;
                        try
                        {
                            var found = library.SearchBook(b.Id, q);
                            if (found.Count == 0) continue;
                            BookDetail? det = null;
                            try { det = library.GetBookDetail(b.Id); } catch { }
                            foreach (var h in found.Take(2))
                            {
                                string chap = det is not null && h.Section < det.Sections.Length
                                    ? det.Sections[h.Section].Title ?? $"第 {h.Section + 1} 节"
                                    : $"第 {h.Section + 1} 节";
                                hits.Add((b, h, chap));
                                if (hits.Count >= 18) break;
                            }
                        }
                        catch { }
                    }
                    return hits;
                }, ct);

                if (ct.IsCancellationRequested) return;
                statusTb.Visibility = Visibility.Collapsed;

                if (passageHits.Count > 0)
                {
                    AddSectionTitle($"全文命中 · {passageHits.Count}");
                    foreach (var (book, hit, chap) in passageHits)
                    {
                        var b = book;
                        var h = hit;
                        string snippet = $"{h.Before}{h.Matched}{h.After}".Trim();
                        AddRow("search", snippet, $"《{b.Title}》 · {chap}", q,
                            () => App.CurrentApp.OpenBook(b.Id, section: h.Section, sentence: (int)h.Sentence, searchQuery: q));
                    }
                    if (selectedIdx < 0 && items.Count > 0) { selectedIdx = 0; HighlightSelection(); }
                }
            }
        }

        searchBox.TextChanged += (_, _) => _ = RefreshAsync(searchBox.Text);
        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Down && items.Count > 0)
            {
                selectedIdx = (selectedIdx + 1) % items.Count;
                HighlightSelection();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Up && items.Count > 0)
            {
                selectedIdx = selectedIdx <= 0 ? items.Count - 1 : selectedIdx - 1;
                HighlightSelection();
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Enter && selectedIdx >= 0 && selectedIdx < items.Count)
            {
                items[selectedIdx].Activate();
                e.Handled = true;
            }
        };

        var root = new StackPanel
        {
            Width = 500,
            Spacing = 8,
            Children =
            {
                searchBox,
                statusTb,
                new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = resultsPanel },
            },
        };

        dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "快速跳转 (Ctrl+K)",
            Content = root,
            CloseButtonText = "关闭",
        };
        dlg.Resources["ContentDialogMaxWidth"] = 560.0;
        dlg.Opened += (_, _) =>
        {
            searchBox.Focus(FocusState.Programmatic);
            _ = RefreshAsync("");
        };

        await dlg.ShowAsync();
        searchCts?.Cancel();
        searchCts?.Dispose();
    }

    public static TextBlock BuildHighlightedBlock(
        string text,
        string? query,
        double fontSize,
        Windows.UI.Text.FontWeight baseWeight,
        Windows.UI.Color baseColor,
        Windows.UI.Color accentColor)
    {
        var tb = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = baseWeight,
            Foreground = ThemeHelper.Brush(baseColor),
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
        };
        if (string.IsNullOrWhiteSpace(query))
        {
            tb.Text = text;
            return tb;
        }
        int idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            tb.Text = text;
            return tb;
        }
        if (idx > 0) tb.Inlines.Add(new Run { Text = text[..idx] });
        tb.Inlines.Add(new Run
        {
            Text = text.Substring(idx, query.Length),
            Foreground = ThemeHelper.Brush(accentColor),
            FontWeight = FontWeights.Bold,
        });
        if (idx + query.Length < text.Length)
            tb.Inlines.Add(new Run { Text = text[(idx + query.Length)..] });
        return tb;
    }
}
