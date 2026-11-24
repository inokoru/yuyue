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
    internal static bool IsCompanionEligible(BookSummary b) =>
        b.Available &&
        !b.Record &&
        b.Format is not BookFormat.Audio and not BookFormat.Pdf and not BookFormat.Podcast;

    internal static BookSummary? ResolveCompanionTargetBook(
        IEnumerable<BookSummary> matchedBooks,
        BookSummary? passageHitBook,
        IEnumerable<BookSummary> allBooks)
    {
        var fromMatched = matchedBooks.FirstOrDefault(IsCompanionEligible);
        if (fromMatched is not null) return fromMatched;
        if (passageHitBook is not null && IsCompanionEligible(passageHitBook)) return passageHitBook;
        return allBooks
            .Where(IsCompanionEligible)
            .OrderByDescending(b => b.OpenedAt ?? 0)
            .ThenByDescending(b => b.AddedAt)
            .FirstOrDefault();
    }

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

        TextBlock AddRow(string icon, string title, string subtitle, string? highlightQuery, Action activate, bool closeOnActivate = true)
        {
            var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 7, 8, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(YYIcons.Make(icon, 13, ThemeHelper.Brush(tint.Accent)));

            var sp = new StackPanel { Spacing = 2 };
            var titleTb = BuildHighlightedBlock(title, highlightQuery, 12.5, FontWeights.SemiBold, p.T1, tint.Accent);
            sp.Children.Add(titleTb);
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
            Action run = () =>
            {
                if (closeOnActivate) dlg?.Hide();
                activate();
            };
            btn.Click += (_, _) => run();
            items.Add((btn, run));
            resultsPanel.Children.Add(btn);
            return titleTb;
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
            statusTb.Foreground = ThemeHelper.Brush(p.T3);
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

            // 3. Companion quick action (#29: exclude Audio, PDF, unavailable, and record-only books)
            BookSummary? firstPassageEligibleBook = null;
            TextBlock? companionTitleTb = null;
            if (onAskCompanionInCurrentBook is not null && !string.IsNullOrEmpty(currentBookId))
            {
                AddSectionTitle("书伴提问");
                AddRow("sparkle", $"问书伴：{q}", "在当前书里基于已读内容解答（不剧透）", null, () => onAskCompanionInCurrentBook(q));
            }
            else
            {
                BookSummary? ResolveTarget() => ResolveCompanionTargetBook(bookHits, firstPassageEligibleBook, allBooks);
                var initialTarget = ResolveTarget();
                AddSectionTitle("书伴提问");
                string askTitle = initialTarget is not null ? $"在《{initialTarget.Title}》里问书伴：{q}" : $"问书伴：{q}";
                string askSub = initialTarget is not null ? "打开当前在读书的书伴直接解答" : "先打开一本电子书即可基于已读内容提问";
                companionTitleTb = AddRow(
                    "sparkle",
                    askTitle,
                    askSub,
                    null,
                    () =>
                    {
                        var target = ResolveTarget();
                        if (target is not null)
                        {
                            dlg?.Hide();
                            App.CurrentApp.OpenBook(target.Id, askQuestion: q);
                        }
                        else
                        {
                            statusTb.Text = "先打开一本书";
                            statusTb.Foreground = ThemeHelper.Brush(tint.Accent);
                            statusTb.Visibility = Visibility.Visible;
                        }
                    },
                    closeOnActivate: false);
            }

            if (items.Count > 0) { selectedIdx = 0; HighlightSelection(); }

            // 4. Async full-text passage search across ALL available text books (round-robin, up to 18 hits)
            if (q.Length >= 2)
            {
                statusTb.Text = "正在检索全部书籍正文…";
                statusTb.Foreground = ThemeHelper.Brush(p.T3);
                statusTb.Visibility = Visibility.Visible;
                var textBooks = allBooks.Where(b => b.Available && !b.Record && b.Format != BookFormat.Audio).ToArray();
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
                    firstPassageEligibleBook = passageHits.Select(x => x.Book).FirstOrDefault(IsCompanionEligible);
                    if (companionTitleTb is not null && bookHits.FirstOrDefault(IsCompanionEligible) is null && firstPassageEligibleBook is not null)
                    {
                        companionTitleTb.Text = $"在《{firstPassageEligibleBook.Title}》里问书伴：{q}";
                    }

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
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query))
        {
            tb.Text = text;
            return tb;
        }

        bool[]? mask = ComputeMatchMask(text, query.Trim());
        if (mask is null)
        {
            tb.Text = text;
            return tb;
        }

        var accentBrush = ThemeHelper.Brush(accentColor);
        int i = 0;
        while (i < text.Length)
        {
            bool hit = mask[i];
            int j = i + 1;
            while (j < text.Length && mask[j] == hit) j++;
            string seg = text[i..j];
            if (hit)
            {
                tb.Inlines.Add(new Run
                {
                    Text = seg,
                    Foreground = accentBrush,
                    FontWeight = FontWeights.Bold,
                });
            }
            else
            {
                tb.Inlines.Add(new Run { Text = seg });
            }
            i = j;
        }
        return tb;
    }

    internal static bool[]? ComputeMatchMask(string text, string q)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(q)) return null;
        var mask = new bool[text.Length];
        bool any = false;

        // 1. Direct case-insensitive substring matches (all occurrences)
        int searchFrom = 0;
        while (searchFrom <= text.Length - q.Length)
        {
            int idx = text.IndexOf(q, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            for (int k = idx; k < idx + q.Length; k++) mask[k] = true;
            any = true;
            searchFrom = idx + Math.Max(1, q.Length);
        }
        if (any) return mask;

        // 2. Compact whitespace/punctuation-stripped substring match
        var compactQBuilder = new System.Text.StringBuilder(q.Length);
        foreach (char ch in q)
        {
            if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
                compactQBuilder.Append(char.ToLowerInvariant(ch));
        }
        string compactQ = compactQBuilder.ToString();
        if (compactQ.Length == 0) return null;

        var compactChars = new List<char>(text.Length);
        var compactOrigIdx = new List<int>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
            {
                compactChars.Add(char.ToLowerInvariant(ch));
                compactOrigIdx.Add(i);
            }
        }
        string compactText = new string(compactChars.ToArray());
        int cIdx = compactText.IndexOf(compactQ, StringComparison.Ordinal);
        if (cIdx >= 0)
        {
            for (int k = cIdx; k < cIdx + compactQ.Length; k++)
                mask[compactOrigIdx[k]] = true;
            return mask;
        }

        // 3. NameMatcher Pinyin initials & ASCII word-initial matches (contiguous first, then subsequence)
        if (compactQ.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')))
        {
            var initChars = new List<char>(text.Length);
            var initOrigIdx = new List<int>(text.Length);
            bool inAsciiWord = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch >= 0x4E00 && ch <= 0x9FFF)
                {
                    inAsciiWord = false;
                    char py = NameMatcher.GetPinyinInitial(ch);
                    if (py != '\0')
                    {
                        initChars.Add(py);
                        initOrigIdx.Add(i);
                    }
                }
                else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
                {
                    if (!inAsciiWord)
                    {
                        initChars.Add(char.ToLowerInvariant(ch));
                        initOrigIdx.Add(i);
                        inAsciiWord = true;
                    }
                }
                else if (ch >= '0' && ch <= '9')
                {
                    inAsciiWord = false;
                    initChars.Add(ch);
                    initOrigIdx.Add(i);
                }
                else
                {
                    inAsciiWord = false;
                }
            }

            if (initChars.Count >= compactQ.Length)
            {
                string initStr = new string(initChars.ToArray());
                int pIdx = initStr.IndexOf(compactQ, StringComparison.Ordinal);
                if (pIdx >= 0)
                {
                    for (int k = pIdx; k < pIdx + compactQ.Length; k++)
                        mask[initOrigIdx[k]] = true;
                    return mask;
                }

                // Subsequence initial match
                int qi = 0;
                var subseqIndices = new int[compactQ.Length];
                for (int ti = 0; ti < initChars.Count && qi < compactQ.Length; ti++)
                {
                    if (initChars[ti] == compactQ[qi])
                    {
                        subseqIndices[qi] = initOrigIdx[ti];
                        qi++;
                    }
                }
                if (qi == compactQ.Length)
                {
                    foreach (int origPos in subseqIndices) mask[origPos] = true;
                    return mask;
                }
            }
        }

        return null;
    }
}
