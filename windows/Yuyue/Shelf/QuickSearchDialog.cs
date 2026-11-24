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
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        var kaiti = new FontFamily(YYFontFamily.Kaiti.WindowsFamily());

        ContentDialog? dlg = null;
        CancellationTokenSource? searchCts = null;
        bool sheetClosed = false;
        var items = new List<(Button Btn, Action Activate, FrameworkElement? OpenHint)>();
        int selectedIdx = -1;
        BookSummary? bestCompanionBook = null;

        var allKeptInitial = library.GetKeptBooks();
        var markCounts = library.GetMarkCounts();
        int totalNotesCount = 0;
        foreach (var b in allKeptInitial)
            if (markCounts.TryGetValue(b.Id, out var c)) totalNotesCount += (int)c;

        var searchBox = new TextBox
        {
            PlaceholderText = L10n.T("搜书名、原文、笔记，或者问书伴"),
            FontSize = 16,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 6, 4, 6),
        };
        YYChrome.StripTextBoxChrome(searchBox);
        var resultsPanel = new StackPanel { Spacing = 3 };
        var scopeText = new TextBlock
        {
            Text = L10n.T($"在 {allKeptInitial.Count} 本书和 {totalNotesCount} 条笔记里搜"),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        void HighlightSelection()
        {
            for (int i = 0; i < items.Count; i++)
            {
                var (btn, _, openHint) = items[i];
                if (i == selectedIdx)
                {
                    btn.Background = ThemeHelper.Brush(i == 0
                        ? Windows.UI.Color.FromArgb(dark ? (byte)36 : (byte)24, tint.Accent.R, tint.Accent.G, tint.Accent.B)
                        : p.Inset);
                    if (openHint is not null) openHint.Visibility = Visibility.Visible;
                    btn.StartBringIntoView();
                }
                else
                {
                    btn.Background = i == 0
                        ? ThemeHelper.Brush(Windows.UI.Color.FromArgb(dark ? (byte)18 : (byte)12, tint.Accent.R, tint.Accent.G, tint.Accent.B))
                        : new SolidColorBrush(Colors.Transparent);
                    if (openHint is not null) openHint.Visibility = Visibility.Collapsed;
                }
            }
        }

        Border MakeOpenBadge() => new()
        {
            CornerRadius = new CornerRadius(5),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(tint.Edge),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(6, 1.5, 6, 1.5),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = L10n.T("↩ 打开"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(tint.Accent),
            },
        };

        FrameworkElement MakeMiniBookBadge(BookSummary b)
        {
            if (!string.IsNullOrEmpty(b.CoverPath) && System.IO.File.Exists(b.CoverPath))
            {
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
                {
                    DecodePixelWidth = 48,
                    UriSource = new Uri(b.CoverPath),
                };
                return new Border
                {
                    Width = 20,
                    Height = 28,
                    CornerRadius = new CornerRadius(3),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Opacity = b.Available || b.Record ? 1.0 : 0.55,
                    Child = new Image
                    {
                        Source = bmp,
                        Stretch = Stretch.UniformToFill,
                    },
                };
            }
            return new Border
            {
                Width = 20,
                Height = 28,
                CornerRadius = new CornerRadius(3),
                Background = ThemeHelper.Brush(b.Record ? ShelfWindow.PaperClothColor(b.Title, dark) : p.Inset),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                Opacity = b.Available || b.Record ? 1.0 : 0.55,
                Child = YYIcons.Make(b.Format == BookFormat.Audio ? "headphones" : "book", 10, ThemeHelper.Brush(p.T3)),
            };
        }

        void AddSectionHeader(string title, string? right = null)
        {
            var g = new Grid { Margin = new Thickness(10, 8, 10, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock
            {
                Text = L10n.T(title),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (!string.IsNullOrEmpty(right))
            {
                var rtb = new TextBlock
                {
                    Text = L10n.T(right),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Opacity = 0.85,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(rtb, 1);
                g.Children.Add(rtb);
            }
            resultsPanel.Children.Add(g);
        }

        void RegisterRow(Button btn, Action activate, FrameworkElement? openHint = null, bool closeOnActivate = true)
        {
            int idx = items.Count;
            Action run = () =>
            {
                if (closeOnActivate) dlg?.Hide();
                activate();
            };
            items.Add((btn, run, openHint));
            btn.Click += (_, _) => run();
            btn.PointerEntered += (_, _) =>
            {
                selectedIdx = idx;
                HighlightSelection();
            };
            resultsPanel.Children.Add(btn);
        }

        void TriggerAskCompanion(string q)
        {
            if (onAskCompanionInCurrentBook is not null && !string.IsNullOrEmpty(currentBookId))
            {
                dlg?.Hide();
                onAskCompanionInCurrentBook(q);
                return;
            }
            var target = bestCompanionBook ?? ResolveCompanionTargetBook([], null, library.GetShelfBooks());
            if (target is not null)
            {
                dlg?.Hide();
                App.CurrentApp.TryOpenBookFromAnywhere(target, askQuestion: string.IsNullOrWhiteSpace(q) ? null : q);
            }
            else
            {
                scopeText.Text = L10n.T("先打开一本书");
                scopeText.Foreground = ThemeHelper.Brush(tint.Accent);
            }
        }

        void RenderRows(
            string q,
            BookSummary[] bookMatches,
            IReadOnlyList<(BookSummary Book, uint SectionIndex, string Title)> tocMatches,
            IReadOnlyList<(BookSummary Book, SearchHit Hit, string Chapter)> passageHits,
            int hitBookCount,
            int totalLineCount,
            bool lineCapped,
            IReadOnlyList<(BookSummary Book, Mark Mark)> markHits,
            bool searching)
        {
            resultsPanel.Children.Clear();
            items.Clear();
            selectedIdx = -1;

            scopeText.Foreground = ThemeHelper.Brush(p.T3);
            scopeText.Text = L10n.T(searching
                ? "正在书库和笔记里检索…"
                : $"在 {allKeptInitial.Count} 本书和 {totalNotesCount} 条笔记里搜");

            var passageBook = passageHits.Select(x => x.Book).FirstOrDefault(IsCompanionEligible);
            bestCompanionBook = ResolveCompanionTargetBook(
                string.IsNullOrEmpty(q) ? [] : bookMatches,
                passageBook,
                library.GetShelfBooks());

            // Row 0: Ask Companion
            var askGrid = new Grid { ColumnSpacing = 10 };
            askGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            askGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            askGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            askGrid.Children.Add(new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(9),
                Background = ThemeHelper.Brush(tint.Soft),
                Child = YYIcons.Make("sparkle", 13.5, ThemeHelper.Brush(tint.Accent)),
            });
            var askMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            askMeta.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(q) ? "问书伴" : $"问书伴：“{q}”",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(tint.Accent),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            string askSub = onAskCompanionInCurrentBook is not null && !string.IsNullOrEmpty(currentBookId)
                ? "在当前书里只用你读过的部分回答，带出处"
                : (bestCompanionBook is not null
                    ? $"在《{bestCompanionBook.Title}》里只用你读过的部分回答，带出处"
                    : "只用你读过的部分回答，带出处");
            askMeta.Children.Add(new TextBlock
            {
                Text = askSub,
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(askMeta, 1);
            askGrid.Children.Add(askMeta);
            var ctrlEnterCap = YYChrome.MakeKeycap("Ctrl+↵", dark);
            Grid.SetColumn(ctrlEnterCap, 2);
            askGrid.Children.Add(ctrlEnterCap);

            var askBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = ThemeHelper.Brush(Windows.UI.Color.FromArgb(dark ? (byte)18 : (byte)12, tint.Accent.R, tint.Accent.G, tint.Accent.B)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 6, 10, 6),
                UseSystemFocusVisuals = false,
                Content = askGrid,
            };
            RegisterRow(askBtn, () => TriggerAskCompanion(q), closeOnActivate: false);

            // Group 1: Books & TOC
            if (bookMatches.Length > 0 || tocMatches.Count > 0)
            {
                string? countNote = string.IsNullOrEmpty(q) ? null : $"{bookMatches.Length + tocMatches.Count} 项";
                AddSectionHeader(string.IsNullOrEmpty(q) ? "最近读过" : "书名和篇目", countNote);
                foreach (var book in bookMatches)
                {
                    var b = book;
                    var row = new Grid { ColumnSpacing = 10 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(MakeMiniBookBadge(b));

                    var tb = L10n.Skip(BuildHighlightedBlock(b.Title, q, 13, FontWeights.Normal, p.T1, tint.Accent));
                    tb.MaxLines = 1;
                    tb.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(tb, 1);
                    row.Children.Add(tb);

                    string statusNote = !b.Available && !b.Record
                        ? L10n.T("在别的设备上")
                        : (b.Record ? L10n.T($"纸书 · {BookRecordPageBuilder.StatusDisplayName(b.Status)}") : L10n.T($"已读 {(int)Math.Round(b.Progress * 100)}%"));
                    string subLabel = string.Join(" · ", new[] { b.Authors.FirstOrDefault(), statusNote }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    var sub = L10n.Skip(BuildHighlightedBlock(subLabel, q, 11, FontWeights.Normal, p.T3, tint.Accent));
                    sub.MaxLines = 1;
                    sub.VerticalAlignment = VerticalAlignment.Center;
                    var openBadge = MakeOpenBadge();
                    var rightSlot = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    rightSlot.Children.Add(sub);
                    rightSlot.Children.Add(openBadge);
                    Grid.SetColumn(rightSlot, 2);
                    row.Children.Add(rightSlot);

                    var btn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(10, 6, 10, 6),
                        UseSystemFocusVisuals = false,
                        Content = row,
                    };
                    RegisterRow(btn, () => App.CurrentApp.TryOpenBookFromAnywhere(b), openBadge);
                }

                foreach (var (book, secIdx, secTitle) in tocMatches)
                {
                    var b = book;
                    uint targetSec = secIdx;
                    var row = new Grid { ColumnSpacing = 10 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(MakeMiniBookBadge(b));

                    var tb = L10n.Skip(BuildHighlightedBlock(secTitle, q, 12.5, FontWeights.Normal, p.T1, tint.Accent));
                    tb.MaxLines = 1;
                    tb.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(tb, 1);
                    row.Children.Add(tb);

                    var sub = L10n.Skip(new TextBlock
                    {
                        Text = $"{L10n.T("章节")} · {b.Title}",
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                        MaxWidth = 150,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    var openBadge = MakeOpenBadge();
                    var rightSlot = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    rightSlot.Children.Add(sub);
                    rightSlot.Children.Add(openBadge);
                    Grid.SetColumn(rightSlot, 2);
                    row.Children.Add(rightSlot);

                    var btn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(10, 6, 10, 6),
                        UseSystemFocusVisuals = false,
                        Content = row,
                    };
                    RegisterRow(btn, () => App.CurrentApp.TryOpenBookFromAnywhere(b, section: targetSec, sentence: 0), openBadge);
                }
            }

            if (string.IsNullOrEmpty(q))
            {
                L10n.ApplyTree(resultsPanel);
                if (items.Count > 1) selectedIdx = 1;
                else if (items.Count > 0) selectedIdx = 0;
                HighlightSelection();
                return;
            }

            // Group 2: Full-text round-robin hits
            if (passageHits.Count > 0)
            {
                AddSectionHeader("全文", $"{totalLineCount}{(lineCapped ? "+" : "")} 处 · {hitBookCount} 本书");
                foreach (var (book, hit, chap) in passageHits)
                {
                    var b = book;
                    var h = hit;
                    string searchQ = q;
                    var row = new Grid { ColumnSpacing = 10 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(MakeMiniBookBadge(b));

                    var sentTb = L10n.Skip(new TextBlock
                    {
                        FontFamily = serif,
                        FontSize = 12.5,
                        Foreground = ThemeHelper.Brush(p.T1),
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    if (!string.IsNullOrEmpty(h.Before)) sentTb.Inlines.Add(new Run { Text = h.Before });
                    var ul = new Underline();
                    ul.Inlines.Add(new Run { Text = h.Matched, FontWeight = FontWeights.Bold, Foreground = ThemeHelper.Brush(tint.Accent) });
                    sentTb.Inlines.Add(ul);
                    if (!string.IsNullOrEmpty(h.After)) sentTb.Inlines.Add(new Run { Text = h.After });
                    Grid.SetColumn(sentTb, 1);
                    row.Children.Add(sentTb);

                    string srcLabel = string.IsNullOrWhiteSpace(chap) ? b.Title : $"{b.Title} · {chap}";
                    var src = L10n.Skip(new TextBlock
                    {
                        Text = srcLabel,
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                        MaxWidth = 150,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    var openBadge = MakeOpenBadge();
                    var rightSlot = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    rightSlot.Children.Add(src);
                    rightSlot.Children.Add(openBadge);
                    Grid.SetColumn(rightSlot, 2);
                    row.Children.Add(rightSlot);

                    var btn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(10, 6, 10, 6),
                        UseSystemFocusVisuals = false,
                        Content = row,
                    };
                    RegisterRow(btn, () => App.CurrentApp.TryOpenBookFromAnywhere(b, section: h.Section, sentence: (int)h.Sentence, searchQuery: searchQ), openBadge);
                }
            }

            // Group 3: Notes
            if (markHits.Count > 0)
            {
                AddSectionHeader("笔记", $"{markHits.Count} 条");
                foreach (var (book, mk) in markHits.Take(5))
                {
                    var b = book;
                    var m = mk;
                    var row = new Grid { ColumnSpacing = 10 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.Children.Add(MakeMiniBookBadge(b));

                    var sp = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                    var mkTb = L10n.Skip(BuildHighlightedBlock(m.Text, q, 12.5, FontWeights.Normal, p.T1, tint.Accent));
                    mkTb.FontFamily = serif;
                    mkTb.MaxLines = 1;
                    sp.Children.Add(mkTb);
                    if (!string.IsNullOrWhiteSpace(m.Note))
                    {
                        var ntTb = L10n.Skip(BuildHighlightedBlock(m.Note, q, 11.5, FontWeights.Normal, tint.Accent, tint.Accent));
                        ntTb.FontFamily = kaiti;
                        ntTb.MaxLines = 1;
                        sp.Children.Add(ntTb);
                    }
                    Grid.SetColumn(sp, 1);
                    row.Children.Add(sp);

                    var src = L10n.Skip(new TextBlock
                    {
                        Text = b.Title,
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                        MaxWidth = 140,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    var openBadge = MakeOpenBadge();
                    var rightSlot = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    rightSlot.Children.Add(src);
                    rightSlot.Children.Add(openBadge);
                    Grid.SetColumn(rightSlot, 2);
                    row.Children.Add(rightSlot);

                    var btn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(10, 6, 10, 6),
                        UseSystemFocusVisuals = false,
                        Content = row,
                    };
                    RegisterRow(btn, () => App.CurrentApp.TryOpenBookFromAnywhere(b, section: m.Section, sentence: (int)m.Start.Sentence), openBadge);
                }
            }

            if (!searching && bookMatches.Length == 0 && tocMatches.Count == 0 && passageHits.Count == 0 && markHits.Count == 0)
            {
                resultsPanel.Children.Add(new TextBlock
                {
                    Text = L10n.T("书里和笔记里都没找到。按 Ctrl+↵ 问书伴试试"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 22, 0, 18),
                });
            }

            L10n.ApplyTree(resultsPanel);
            if (items.Count > 1) selectedIdx = 1;
            else if (items.Count > 0) selectedIdx = 0;
            HighlightSelection();
        }

        async Task RefreshAsync(string rawQuery)
        {
            searchCts?.Cancel();
            searchCts?.Dispose();
            searchCts = new CancellationTokenSource();
            var ct = searchCts.Token;

            string q = rawQuery.Trim();
            var allBooks = library.GetKeptBooks().OrderByDescending(b => Math.Max(b.OpenedAt ?? 0, b.AddedAt)).ToArray();

            if (string.IsNullOrEmpty(q))
            {
                RenderRows("", allBooks.Take(6).ToArray(), [], [], 0, 0, false, [], searching: false);
                return;
            }

            var bookHits = allBooks
                .Where(b => NameMatcher.Matches(b.Title, q) || b.Authors.Any(a => NameMatcher.Matches(a, q)))
                .Take(8)
                .ToArray();

            RenderRows(q, bookHits, [], [], 0, 0, false, [], searching: true);

            var snapshotBooks = allBooks.ToArray();
            (List<(BookSummary Book, uint SectionIndex, string Title)> tocMatches,
             List<(BookSummary Book, SearchHit Hit, string Chapter)> rrHits,
             int hitBooks, int totalLines, bool capped,
             List<(BookSummary Book, Mark Mark)> markHits) computed;
            try
            {
                computed = await Task.Run(async () =>
                {
                    await Task.Delay(90, ct);
                    var tocMatches = new List<(BookSummary Book, uint SectionIndex, string Title)>();
                    foreach (var b in snapshotBooks.Where(b => !b.Record && b.Available && b.Format != BookFormat.Audio))
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            var det = library.GetBookDetail(b.Id);
                            for (uint sIdx = 0; sIdx < det.Sections.Length; sIdx++)
                            {
                                string? sTitle = det.Sections[sIdx].Title;
                                if (!string.IsNullOrWhiteSpace(sTitle) && NameMatcher.Matches(sTitle, q))
                                {
                                    tocMatches.Add((b, sIdx, sTitle));
                                    if (tocMatches.Count >= 6) break;
                                }
                            }
                            if (tocMatches.Count >= 6) break;
                        }
                        catch { }
                    }

                    var buckets = new List<(BookSummary Book, IReadOnlyList<SearchHit> Hits, BookDetail? Detail)>();
                    int hitBooks = 0;
                    int totalLines = 0;
                    bool capped = false;
                    foreach (var b in snapshotBooks.Where(b => !b.Record && b.Available && b.Format != BookFormat.Audio))
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            var found = library.SearchBook(b.Id, q);
                            if (found.Count > 0)
                            {
                                hitBooks++;
                                totalLines += found.Count;
                                if (found.Count >= 500) capped = true;
                                BookDetail? det = null;
                                try { det = library.GetBookDetail(b.Id); } catch { }
                                buckets.Add((b, found, det));
                            }
                        }
                        catch { }
                    }

                    var rrHits = new List<(BookSummary Book, SearchHit Hit, string Chapter)>();
                    for (int round = 0; rrHits.Count < 10; round++)
                    {
                        bool anyInRound = false;
                        foreach (var (b, found, det) in buckets)
                        {
                            if (round < found.Count)
                            {
                                anyInRound = true;
                                var h = found[round];
                                string chap = det is not null && h.Section < det.Sections.Length
                                    ? (det.Sections[h.Section].Title ?? "")
                                    : "";
                                rrHits.Add((b, h, chap));
                                if (rrHits.Count >= 10) break;
                            }
                        }
                        if (!anyInRound) break;
                    }

                    var markHits = new List<(BookSummary Book, Mark Mark)>();
                    foreach (var b in snapshotBooks)
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            foreach (var m in library.GetMarks(b.Id))
                            {
                                if (m.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
                                    || (!string.IsNullOrEmpty(m.Note) && m.Note.Contains(q, StringComparison.OrdinalIgnoreCase)))
                                {
                                    markHits.Add((b, m));
                                }
                            }
                        }
                        catch { }
                    }

                    return (tocMatches, rrHits, hitBooks, totalLines, capped, markHits);
                }, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (sheetClosed || ct.IsCancellationRequested) return;
            RenderRows(q, bookHits, computed.tocMatches, computed.rrHits, computed.hitBooks, computed.totalLines, computed.capped, computed.markHits, searching: false);
        }

        searchBox.TextChanged += (_, _) => _ = RefreshAsync(searchBox.Text);
        searchBox.KeyDown += (_, e) =>
        {
            bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (ctrl && e.Key == VirtualKey.Enter)
            {
                TriggerAskCompanion(searchBox.Text.Trim());
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Down && items.Count > 0)
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

        var topGrid = new Grid { Height = 52, Padding = new Thickness(16, 0, 16, 0), ColumnSpacing = 10 };
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topGrid.Children.Add(YYIcons.Make("search", 15, ThemeHelper.Brush(p.T2)));
        Grid.SetColumn(searchBox, 1);
        topGrid.Children.Add(searchBox);
        var escCap = YYChrome.MakeKeycap("esc", dark);
        Grid.SetColumn(escCap, 2);
        topGrid.Children.Add(escCap);

        var bottomGrid = new Grid { Height = 36, Padding = new Thickness(14, 0, 14, 0) };
        var hintsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        hintsRow.Children.Add(YYChrome.MakeKeycap("↑", dark));
        hintsRow.Children.Add(YYChrome.MakeKeycap("↓", dark));
        hintsRow.Children.Add(new TextBlock { Text = L10n.T("选择"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        hintsRow.Children.Add(YYChrome.MakeKeycap("↵", dark));
        hintsRow.Children.Add(new TextBlock { Text = L10n.T("打开"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        hintsRow.Children.Add(YYChrome.MakeKeycap("Ctrl+↵", dark));
        hintsRow.Children.Add(new TextBlock { Text = L10n.T("问书伴"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        bottomGrid.Children.Add(hintsRow);
        bottomGrid.Children.Add(scopeText);

        var spotlightGrid = new Grid();
        spotlightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        spotlightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.5) });
        spotlightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        spotlightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.5) });
        spotlightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });

        spotlightGrid.Children.Add(topGrid);
        var topLine = new Border { Background = ThemeHelper.Brush(p.Line) };
        Grid.SetRow(topLine, 1);
        spotlightGrid.Children.Add(topLine);

        var scroll = new ScrollViewer
        {
            MaxHeight = 420,
            Padding = new Thickness(10, 8, 10, 8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = resultsPanel,
        };
        Grid.SetRow(scroll, 2);
        spotlightGrid.Children.Add(scroll);

        var bottomLine = new Border { Background = ThemeHelper.Brush(p.Line) };
        Grid.SetRow(bottomLine, 3);
        spotlightGrid.Children.Add(bottomLine);

        Grid.SetRow(bottomGrid, 4);
        spotlightGrid.Children.Add(bottomGrid);

        var cardSurface = new Border
        {
            Background = ThemeHelper.Brush(p.Card),
            CornerRadius = new CornerRadius(18),
            Child = spotlightGrid,
        };

        dlg = YYChrome.CreateMacSheet(xamlRoot, dark, 640, cardSurface, maxHeight: 540, tint: library.Prefs.Tint);
        dlg.Opened += (_, _) =>
        {
            searchBox.Focus(FocusState.Programmatic);
            _ = RefreshAsync("");
        };

        try
        {
            await dlg.ShowSafeAsync(library.Prefs.Tint);
        }
        finally
        {
            sheetClosed = true;
            try { searchCts?.Cancel(); searchCts?.Dispose(); } catch { }
        }
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
