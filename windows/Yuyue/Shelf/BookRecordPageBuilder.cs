using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using Yuyue.Core;
using Yuyue.Design;

namespace Yuyue.Shelf;

/// <summary>
/// 1:1 port of <c>MacBooksView.swift</c>, <c>BookRecord.swift</c>, and <c>BookRecordPage.swift</c>:
/// <c>进度</c> status tabs + sort menu + <c>BookRecordCard</c> grid (with segmented per-chapter <c>ChapterBar</c>
/// and <c>ChapterJumpList</c> flyout) + full inline <c>BookRecordPage</c> (<c>目录 / 简介 / 笔记 / 记录 / 播客</c>).
/// </summary>
public sealed class BookRecordPageBuilder
{
    private readonly UI.Shell.ILibraryHost _shelf;
    private readonly LibraryService _library;

    public ReadingStatus ActiveStatus { get; set; } = ReadingStatus.Reading;
    public string SortKey { get; set; } = "recent"; // recent | added | title | rating
    public string? OpenBookId { get; set; }
    public void SetSubTab(string subTab) { if (!string.IsNullOrWhiteSpace(subTab)) _subTab = subTab; }

    // Inline BookRecordPage sub-state
    private string _subTab = "toc"; // toc | desc | notes | log | podcast
    private bool _tocGridMode;
    private bool _tocExpanded;

    public BookRecordPageBuilder(UI.Shell.ILibraryHost shelf, LibraryService library)
    {
        _shelf = shelf;
        _library = library;
        SortKey = library.Prefs.BooksSort is "added" or "title" or "rating" ? library.Prefs.BooksSort : "recent";
    }

    public void RenderInto(StackPanel host, IReadOnlyList<BookSummary> keptBooks, bool dark)
    {
        host.Children.Clear();
        if (OpenBookId is string bid)
        {
            var book = keptBooks.FirstOrDefault(b => b.Id == bid);
            if (book is not null)
            {
                RenderInlineBookRecordPage(host, book, dark);
                L10n.ApplyTree(host);
                return;
            }
            OpenBookId = null;
        }

        RenderStatusList(host, keptBooks, dark);
        L10n.ApplyTree(host);
    }

    // ---------- 1. 进度列表（MacBooksView.swift + BookRecordCard） ----------

    private void RenderStatusList(StackPanel host, IReadOnlyList<BookSummary> keptBooks, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        // StatusTabs + 右侧排序菜单（明确两列 Grid 绝不重叠，Fluent 2 6px 分段轨道）
        var topBar = new Grid { Margin = new Thickness(0, 0, 0, 10), ColumnSpacing = 12 };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tabsWrap = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        (ReadingStatus St, string Name)[] statuses =
        [
            (ReadingStatus.Reading, "在读"),
            (ReadingStatus.Want, "想读"),
            (ReadingStatus.Finished, "读过"),
            (ReadingStatus.Paused, "搁置"),
            (ReadingStatus.Dropped, "弃读"),
        ];

        foreach (var (st, name) in statuses)
        {
            int n = keptBooks.Count(b => b.Status == st);
            bool on = ActiveStatus == st;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 12,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T2),
                VerticalAlignment = VerticalAlignment.Center,
            });
            sp.Children.Add(new TextBlock
            {
                Text = $"{n}",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T3),
                Opacity = on ? 0.85 : 0.75,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var btn = new Button
            {
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(on ? 1 : 0),
                BorderBrush = on ? ThemeHelper.Brush(dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(18, 42, 43, 46)) : null,
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = sp,
            };
            var nextSt = st;
            btn.Click += (_, _) =>
            {
                ActiveStatus = nextSt;
                _shelf.RefreshCurrentSubPage();
            };
            tabs.Children.Add(btn);
        }
        tabsWrap.Child = tabs;
        Grid.SetColumn(tabsWrap, 0);
        topBar.Children.Add(tabsWrap);

        // 右侧排序菜单：最近读过 / 加入时间 / 书名 / 评分
        string sortTitle = SortKey switch
        {
            "added" => "加入时间",
            "title" => "书名",
            "rating" => "评分",
            _ => "最近读过",
        };
        var sortSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        sortSp.Children.Add(YYIcons.Make("updown", 12, ThemeHelper.Brush(p.T2)));
        sortSp.Children.Add(new TextBlock { Text = sortTitle, FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        var sortMenu = new MenuFlyout();
        foreach (var (k, lbl) in new[] { ("recent", "最近读过"), ("added", "加入时间"), ("title", "书名"), ("rating", "评分") })
        {
            string key = k;
            var item = new ToggleMenuFlyoutItem { Text = L10n.T(lbl), IsChecked = SortKey == key };
            item.Click += (_, _) =>
            {
                SortKey = key;
                _library.Prefs.BooksSort = key;
                _library.SavePrefs();
                _shelf.SyncShelfSortFromPrefs();
                _shelf.RefreshCurrentSubPage();
            };
            sortMenu.Items.Add(item);
        }
        var sortBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Content = sortSp,
            Flyout = sortMenu,
        };
        AutomationProperties.SetName(sortBtn, L10n.T(sortTitle));
        Grid.SetColumn(sortBtn, 1);
        topBar.Children.Add(sortBtn);
        host.Children.Add(topBar);

        var filtered = SortProgressBooks(keptBooks.Where(b => b.Status == ActiveStatus)).ToArray();
        if (filtered.Length == 0)
        {
            var empty = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 84, 0, 0) };
            empty.Children.Add(new TextBlock
            {
                Text = ActiveStatus switch
                {
                    ReadingStatus.Reading => "还没有正在读的书。打开任意一本书，或点「+ 加书」记一本纸质书",
                    ReadingStatus.Want => "还没有想读的书。可以先记下书名，以后慢慢读",
                    ReadingStatus.Finished => "读过的书会收在这里，带着你的评分与短评",
                    ReadingStatus.Paused => "暂时放下的书会收在这里",
                    _ => "弃读的书会收在这里",
                },
                FontSize = 12.5,
                Foreground = ThemeHelper.Brush(p.T3),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            if (ActiveStatus is ReadingStatus.Reading or ReadingStatus.Want)
            {
                var addBtn = MakeRecordButton("加一本书", main: true, p, tint);
                addBtn.HorizontalAlignment = HorizontalAlignment.Center;
                addBtn.Click += async (_, _) => await _shelf.PromptAddRecordBookAsync(ActiveStatus);
                empty.Children.Add(addBtn);
            }
            host.Children.Add(empty);
            return;
        }

        // 单张卡片限宽 344px 靠左，多张按可用宽度自适应多列等宽网格（对齐 MacBooksView: single maxWidth 344, adaptive min 294 + spacing 14 = 308）
        var grid = new Grid { ColumnSpacing = 14, RowSpacing = 12 };
        if (filtered.Length == 1)
        {
            grid.MaxWidth = 344;
            grid.HorizontalAlignment = HorizontalAlignment.Left;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(BuildBookRecordCard(filtered[0], p, tint, dark));
        }
        else
        {
            double availWidth = _shelf.SubPageAvailableWidth;
            int cols = Math.Max(2, (int)((availWidth + 14) / 308));
            cols = Math.Min(cols, filtered.Length);
            for (int c = 0; c < cols; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int rows = (filtered.Length + cols - 1) / cols;
            for (int r = 0; r < rows; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < filtered.Length; i++)
            {
                var card = BuildBookRecordCard(filtered[i], p, tint, dark);
                Grid.SetRow(card, i / cols);
                Grid.SetColumn(card, i % cols);
                grid.Children.Add(card);
            }
        }
        host.Children.Add(grid);
    }

    private static long LastTouchedAt(BookSummary b)
    {
        long t = b.AddedAt;
        if (b.OpenedAt is long oa && oa > t) t = oa;
        if (b.Manual?.At is long ma && ma > t) t = ma;
        if (b.FinishedAt is long fa && fa > t) t = fa;
        return t;
    }

    private IEnumerable<BookSummary> SortProgressBooks(IEnumerable<BookSummary> src) => SortKey switch
    {
        "added" => src.OrderByDescending(b => b.AddedAt),
        "title" => src.OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase),
        "rating" => src.OrderByDescending(b => b.Rating ?? 0).ThenByDescending(LastTouchedAt),
        _ => src.OrderByDescending(LastTouchedAt),
    };

    internal static Border MakePaperBookTag(bool dark)
    {
        var bg = dark ? Color.FromArgb(36, 218, 196, 162) : Color.FromArgb(28, 146, 118, 76);
        var fg = dark ? Color.FromArgb(255, 218, 198, 166) : Color.FromArgb(255, 122, 96, 58);
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = ThemeHelper.Brush(bg),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "纸书",
                FontSize = 9.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(fg),
            },
        };
    }

    private static string StatusShortName(ReadingStatus st) => st switch
    {
        ReadingStatus.Reading => "在读",
        ReadingStatus.Want => "想读",
        ReadingStatus.Finished => "读过",
        ReadingStatus.Paused => "搁置",
        ReadingStatus.Dropped => "弃读",
        _ => "在读",
    };

    private FrameworkElement BuildBookRecordCard(BookSummary book, YYPalette p, YYTintColors tint, bool dark)
    {
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        bool eink = _library.Prefs.GlobalEinkMode;
        int sectionCount = 1;
        int curSection = 0;
        if (!book.Record && book.Format is BookFormat.Epub or BookFormat.Txt or BookFormat.Mobi)
        {
            try
            {
                var det = _library.GetBookDetail(book.Id);
                sectionCount = Math.Max(1, det.Sections.Length);
                var prog = _library.GetProgress(book.Id);
                curSection = prog is not null ? Math.Clamp((int)prog.Position.Section, 0, sectionCount - 1) : (int)Math.Floor(book.Progress * sectionCount);
            }
            catch { }
        }

        var root = new Grid { ColumnSpacing = 12, MinHeight = 94, Padding = new Thickness(12, 11, 10, 11) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var coverEl = _shelf.CreateCoverView(book, 48, 68, elevated: false);
        root.Children.Add(coverEl);

        // 中栏：14px 宋体粗体书名 + 作者/纸书标签 + 状态进度/分章条/星级 (1:1 BookRecord.swift e42fcb6)
        var mid = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(L10n.Skip(new TextBlock
        {
            Text = book.Title,
            FontFamily = serif,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ThemeHelper.Brush(p.T1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }));

        string author = book.Authors.FirstOrDefault() ?? "";
        string relTime = !book.Record && book.OpenedAt is long oa && oa > 0 && book.Status is ReadingStatus.Reading or ReadingStatus.Paused
            ? L10n.T(RelativeAgo(oa))
            : "";
        string byline = string.Join(" · ", new[] { author, relTime }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (book.Record)
        {
            var bylineRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            bylineRow.Children.Add(MakePaperBookTag(dark));
            if (!string.IsNullOrEmpty(byline))
            {
                bylineRow.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = byline,
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T2),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                }));
            }
            mid.Children.Add(bylineRow);
        }
        else if (!string.IsNullOrEmpty(byline))
        {
            mid.Children.Add(L10n.Skip(new TextBlock
            {
                Text = byline,
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T2),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            }));
        }

        // 状态专属底部行（对齐 BookRecord.swift e42fcb6：纸质书仅追踪阅读状态与短评，不记手动页码）
        if (book.Status is ReadingStatus.Reading or ReadingStatus.Paused or ReadingStatus.Dropped)
        {
            bool muted = book.Status != ReadingStatus.Reading;
            if (book.Record)
            {
                string paperTime = book.Status == ReadingStatus.Reading && book.StartedAt is long sa && sa > 0
                    ? $"{DateTimeOffset.FromUnixTimeMilliseconds(sa).LocalDateTime.Month}月{DateTimeOffset.FromUnixTimeMilliseconds(sa).LocalDateTime.Day}日开始"
                    : $"{RelativeAgo(book.AddedAt)}加的";
                mid.Children.Add(new TextBlock { Text = paperTime, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
                if (!string.IsNullOrWhiteSpace(book.Comment))
                    mid.Children.Add(L10n.Skip(new TextBlock { Text = book.Comment, FontFamily = serif, FontSize = 11, Foreground = ThemeHelper.Brush(p.T2), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }));
            }
            else
            {
                var metaSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
                int pct = (int)Math.Round(book.Progress * 100);
                string lead = muted ? $"停在 {pct}%" : (sectionCount > 1 ? $"读到 第 {curSection + 1} 章" : (pct > 0 ? $"读到 {pct}%" : "刚开始"));
                metaSp.Children.Add(new TextBlock { Text = lead, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(muted ? p.T2 : tint.Accent) });
                uint mins = book.Format == BookFormat.Audio ? book.ListenMinutes : book.ReadMinutes;
                if (mins > 0 && book.Progress < 0.99)
                {
                    int rem = Math.Max(1, (int)Math.Round(mins * (1.0 - book.Progress)));
                    string remTxt = rem >= 60 ? $"· 还剩 {rem / 60} 小时" : $"· 还剩 {rem} 分";
                    metaSp.Children.Add(new TextBlock { Text = remTxt, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
                }
                mid.Children.Add(metaSp);
                mid.Children.Add(BuildChapterBar(sectionCount, curSection, book.Progress, muted ? p.T3 : tint.Fill, p));
            }
        }
        else if (book.Status == ReadingStatus.Finished)
        {
            var finRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 2, 0, 0) };
            if (book.Rating is byte r && r > 0)
                finRow.Children.Add(L10n.Skip(new TextBlock { Text = new string('★', r) + new string('☆', Math.Max(0, 5 - r)), FontSize = 10.5, Foreground = ThemeHelper.Brush(tint.Accent) }));
            if (book.FinishedAt is long fa && fa > 0)
            {
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(fa).LocalDateTime;
                finRow.Children.Add(new TextBlock { Text = $"{(book.Rating > 0 ? "· " : "")}{dt.Month}月{dt.Day}日读完", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
            }
            else
            {
                finRow.Children.Add(new TextBlock { Text = "读过", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
            }
            mid.Children.Add(finRow);
            if (!string.IsNullOrWhiteSpace(book.Comment))
                mid.Children.Add(L10n.Skip(new TextBlock { Text = book.Comment, FontFamily = serif, FontSize = 11, Foreground = ThemeHelper.Brush(p.T2), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }));
        }
        else // Want
        {
            uint mins = book.Format == BookFormat.Audio ? book.ListenMinutes : book.ReadMinutes;
            string meta = book.Record
                ? $"{RelativeAgo(book.AddedAt)}加的"
                : string.Join(" · ", new[] { sectionCount > 1 ? $"共 {sectionCount} 章" : null, mins >= 60 ? $"约 {Math.Max(1, mins / 60)} 小时" : (mins > 0 ? $"约 {mins} 分钟" : null) }.Where(s => s is not null));
            if (!string.IsNullOrEmpty(meta))
                mid.Children.Add(new TextBlock { Text = meta, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(0, 2, 0, 0) });
        }

        Grid.SetColumn(mid, 1);
        root.Children.Add(mid);

        // 右栏：电子书顶部 StatusMark + 右下目录/接着读；纸质书右下无图标「纸书 · 在读」标签（对齐 BookRecord.swift:707-739）
        var rightCol = new Grid { MinHeight = 72 };
        var statusSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (book.Record)
        {
            statusSp.Children.Add(new TextBlock
            {
                Text = $"纸书 · {StatusShortName(book.Status)}",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = ThemeHelper.Brush(p.T2),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            statusSp.Children.Add(YYIcons.Make(StatusIconName(book.Status), 11.5, ThemeHelper.Brush(p.T2)));
        }
        statusSp.Children.Add(YYIcons.Make("chevron-down", 8, ThemeHelper.Brush(p.T3)));
        var statusBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = book.Record ? VerticalAlignment.Bottom : VerticalAlignment.Top,
            Height = 24,
            MinHeight = 24,
            Padding = book.Record ? new Thickness(8, 0, 7, 0) : new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(7),
            Background = book.Record
                ? ThemeHelper.Brush(p.Card)
                : new SolidColorBrush(Colors.Transparent),
            BorderBrush = book.Record ? ThemeHelper.Brush(p.Line) : null,
            BorderThickness = new Thickness(book.Record ? 0.5 : 0),
            UseSystemFocusVisuals = false,
            Content = statusSp,
            Flyout = BuildStatusMenuFlyout(book, dark, p, tint),
        };
        AutomationProperties.SetName(statusBtn, L10n.T(StatusShortName(book.Status)));
        if (!book.Record)
            rightCol.Children.Add(statusBtn);

        var quickRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        if (book.Record)
        {
            if (book.Status == ReadingStatus.Finished && book.Rating is null)
            {
                var rateBtn = MakeCompactCardButton("打分", main: false, p, tint);
                rateBtn.Click += (_, _) =>
                {
                    OpenBookId = book.Id;
                    _shelf.RefreshCurrentSubPage();
                };
                quickRow.Children.Add(rateBtn);
            }
            quickRow.Children.Add(statusBtn);
        }
        else if (book.Status is ReadingStatus.Reading or ReadingStatus.Paused or ReadingStatus.Dropped or ReadingStatus.Want)
        {
            if (sectionCount > 1 && book.Available)
            {
                var tocBtn = new Button
                {
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(7),
                    Background = ThemeHelper.Brush(p.Inset),
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = YYIcons.Make("list", 11.5, ThemeHelper.Brush(p.T2)),
                    Flyout = BuildChapterJumpFlyout(book, p, tint),
                };
                ToolTipService.SetToolTip(tocBtn, "跳到某一章");
                AutomationProperties.SetName(tocBtn, L10n.T("跳到某一章"));
                quickRow.Children.Add(tocBtn);
            }
            var openBtn = MakeCompactCardButton(book.Status == ReadingStatus.Want ? "开始读" : "接着读", main: true, p, tint);
            openBtn.Click += (_, _) => _shelf.TryOpenBook(book);
            quickRow.Children.Add(openBtn);
        }
        else if (book.Status == ReadingStatus.Finished && book.Rating is null)
        {
            var rateBtn = MakeCompactCardButton("打分", main: false, p, tint);
            rateBtn.Click += (_, _) =>
            {
                OpenBookId = book.Id;
                _shelf.RefreshCurrentSubPage();
            };
            quickRow.Children.Add(rateBtn);
        }

        rightCol.Children.Add(quickRow);
        Grid.SetColumn(rightCol, 2);
        root.Children.Add(rightCol);

        // 点击主体进入内联 BookRecordPage（对齐 MacBooksView.swift lines 36-57）
        var hitBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            ContextFlyout = _shelf.BuildBookMenuFlyout(book),
        };
        AutomationProperties.SetName(hitBtn, book.Title);
        hitBtn.Click += (_, _) =>
        {
            OpenBookId = book.Id;
            _subTab = book.Record ? "notes" : "toc";
            _tocExpanded = false;
            _shelf.RefreshCurrentSubPage();
        };

        var outer = new Grid();
        outer.Children.Add(hitBtn);
        // 让右栏按钮高于透明点击层，但左中区域透传点击到 hitBtn
        coverEl.IsHitTestVisible = false;
        mid.IsHitTestVisible = false;
        outer.Children.Add(root);

        Color cardBg = (!eink && book.Record)
            ? (dark ? Color.FromArgb(255, 36, 32, 27) : Color.FromArgb(255, 252, 246, 233))
            : p.Card;
        Color cardBorder = book.Record
            ? (dark ? Color.FromArgb(41, 218, 196, 162) : Color.FromArgb(61, 176, 148, 102))
            : p.Line;

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = ThemeHelper.Brush(cardBg),
            BorderBrush = ThemeHelper.Brush(cardBorder),
            BorderThickness = new Thickness(1),
            Child = outer,
        };
    }

    /// <summary>
    /// 分章颗粒进度条（移植自 <c>BookRecord.swift ChapterBar</c> lines 967-1007）：按真实当前章与占比点亮 2..28 段分章小胶囊。
    /// </summary>
    private static FrameworkElement BuildChapterBar(int chapters, int curSection, double progress, Color fillCol, YYPalette p)
    {
        double clamped = Math.Clamp(progress, 0, 1);
        int segs = Math.Clamp(chapters, 1, 28);
        if (segs <= 1)
        {
            var g = new Grid { MaxWidth = 168, Height = 3.5, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
            g.Children.Add(new Border { CornerRadius = new CornerRadius(1.75), Background = ThemeHelper.Brush(p.Inset) });
            var fg = new Grid();
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.04, clamped), GridUnitType.Star) });
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1.0 - clamped), GridUnitType.Star) });
            fg.Children.Add(new Border { CornerRadius = new CornerRadius(1.75), Background = ThemeHelper.Brush(fillCol) });
            g.Children.Add(fg);
            return g;
        }

        var bar = new Grid { MaxWidth = 168, Height = 3.5, ColumnSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
        int activeBucket = chapters > 1
            ? Math.Clamp((int)Math.Floor((double)Math.Max(0, curSection) * segs / chapters), 0, segs - 1)
            : (int)Math.Floor(clamped * segs);
        bool finished = clamped >= 0.995;
        Color halfCol = Color.FromArgb(145, fillCol.R, fillCol.G, fillCol.B);
        for (int i = 0; i < segs; i++)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Color segCol = finished || i < activeBucket
                ? fillCol
                : (i == activeBucket && clamped > 0.002 ? halfCol : p.Inset);
            var seg = new Border
            {
                CornerRadius = new CornerRadius(1.5),
                Background = ThemeHelper.Brush(segCol),
            };
            Grid.SetColumn(seg, i);
            bar.Children.Add(seg);
        }
        return bar;
    }

    private Flyout BuildChapterJumpFlyout(BookSummary book, YYPalette p, YYTintColors tint)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var host = new StackPanel { Width = 330, Spacing = 8 };
        flyout.Opening += (_, _) =>
        {
            host.Children.Clear();
            BookDetail det;
            try { det = _library.GetBookDetail(book.Id); }
            catch { return; }

            var items = det.Toc.Length > 0
                ? det.Toc.Select(t => (t.Title, t.Section)).ToArray()
                : det.Sections.Select((s, idx) => (Title: string.IsNullOrWhiteSpace(s.Title) ? L10n.T($"第 {idx + 1} 节") : s.Title!, Section: (uint)idx)).ToArray();

            var prog = _library.GetProgress(book.Id);
            uint curSec = prog?.Position.Section ?? 0;

            const int chunkSize = 100;
            int curChunk = 0;
            int curItemIdx = Array.FindIndex(items, it => it.Section >= curSec);
            if (curItemIdx >= 0) curChunk = curItemIdx / chunkSize;

            var search = new TextBox { PlaceholderText = L10n.T($"搜章节（共 {items.Length} 章，支持拼音首字母）…"), FontSize = 12, CornerRadius = new CornerRadius(8) };
            var rangeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var rangeScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = rangeRow,
                Visibility = items.Length > chunkSize ? Visibility.Visible : Visibility.Collapsed,
            };
            var listStack = new StackPanel { Spacing = 2 };
            var scroller = new ScrollViewer { MaxHeight = 320, Content = listStack };

            void Populate(string q)
            {
                listStack.Children.Clear();
                rangeRow.Children.Clear();
                bool searching = !string.IsNullOrEmpty(q);
                rangeScroll.Visibility = !searching && items.Length > chunkSize ? Visibility.Visible : Visibility.Collapsed;

                if (!searching && items.Length > chunkSize)
                {
                    int totalChunks = (items.Length + chunkSize - 1) / chunkSize;
                    for (int c = 0; c < totalChunks; c++)
                    {
                        int chunkIdx = c;
                        int a = c * chunkSize + 1;
                        int bEnd = Math.Min(items.Length, (c + 1) * chunkSize);
                        bool on = chunkIdx == curChunk;
                        var rb = new Button
                        {
                            Content = $"{a}–{bEnd}",
                            Height = 23,
                            MinHeight = 23,
                            Padding = new Thickness(8, 0, 8, 0),
                            CornerRadius = new CornerRadius(11),
                            FontSize = 10.5,
                            FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                            Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T2),
                            Background = ThemeHelper.Brush(on ? tint.Soft : p.Inset),
                            BorderThickness = new Thickness(0),
                            UseSystemFocusVisuals = false,
                        };
                        rb.Click += (_, _) => { curChunk = chunkIdx; Populate(""); };
                        rangeRow.Children.Add(rb);
                    }
                }

                var shown = searching
                    ? items.Where(it => NameMatcher.Matches(it.Title, q)).ToArray()
                    : items.Skip(curChunk * chunkSize).Take(chunkSize).ToArray();

                foreach (var (title, sec) in shown)
                {
                    bool here = sec == curSec;
                    var r = new Grid { ColumnSpacing = 8 };
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    r.Children.Add(L10n.Skip(new TextBlock { Text = title, FontSize = 12.5, FontWeight = here ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = ThemeHelper.Brush(here ? tint.Accent : p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }));
                    if (here)
                    {
                        var hereTb = new TextBlock { Text = L10n.T("读到这"), FontSize = 10.5, Foreground = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center };
                        Grid.SetColumn(hereTb, 1);
                        r.Children.Add(hereTb);
                    }
                    var b = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = here ? ThemeHelper.Brush(tint.Soft) : new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(7),
                        Padding = new Thickness(8, 6, 8, 6),
                        UseSystemFocusVisuals = false,
                        Content = r,
                    };
                    uint target = sec;
                    b.Click += (_, _) => { flyout.Hide(); _shelf.TryOpenBook(book, section: target, sentence: 0); };
                    listStack.Children.Add(b);
                }
                L10n.ApplyTree(listStack);
            }

            search.TextChanged += (_, _) => Populate(search.Text.Trim());
            host.Children.Add(search);
            host.Children.Add(rangeScroll);
            host.Children.Add(scroller);
            Populate("");
            L10n.ApplyTree(host);
        };
        flyout.Content = host;
        return flyout;
    }

    // ---------- 2. 内联单书档案页（BookRecordPage.swift 1:1） ----------

    private static string FormatBookFormatBadge(BookFormat fmt) => fmt switch
    {
        BookFormat.Mobi => "Kindle",
        BookFormat.Audio => L10n.T("有声书"),
        _ => fmt.ToString().ToUpperInvariant(),
    };

    private void RenderInlineBookRecordPage(StackPanel outerHost, BookSummary book, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());

        double pageW = Math.Min(820, Math.Max(480, _shelf.SubPageAvailableWidth));
        var page = new StackPanel { Width = pageW, Spacing = 18, HorizontalAlignment = HorizontalAlignment.Left };

        // 顶部返回阅读进度列表按钮
        var backSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        backSp.Children.Add(YYIcons.Make("arrow-left", 13, ThemeHelper.Brush(p.T2)));
        backSp.Children.Add(new TextBlock { Text = "返回阅读进度", FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        var backBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Padding = new Thickness(6, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-6, -4, 0, -4),
            Content = backSp,
        };
        backBtn.Click += (_, _) => { OpenBookId = null; _shelf.RefreshCurrentSubPage(); };
        page.Children.Add(backBtn);

        BookDetail? detail = null;
        if (!book.Record && book.Format != BookFormat.Podcast)
        {
            try { detail = _library.GetBookDetail(book.Id); } catch { }
        }
        int chapterCount = detail is not null ? Math.Max(1, detail.Toc.Length > 0 ? detail.Toc.Length : detail.Sections.Length) : 0;
        var marks = _library.GetMarks(book.Id);
        var prog = _library.GetProgress(book.Id);
        uint curSection = prog?.Position.Section ?? 0;

        // 顶部 Hero：左 120x172 封面 + 右侧大标题/作者/评分/主操作行
        var hero = new Grid { ColumnSpacing = 26, MinHeight = 174 };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.Children.Add(_shelf.CreateCoverView(book, 120, 172, elevated: true));

        var rightHero = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        rightHero.Children.Add(L10n.Skip(new TextBlock
        {
            Text = book.Title,
            FontFamily = serif,
            FontSize = 25,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ThemeHelper.Brush(p.T1),
            MaxLines = 3,
            TextWrapping = TextWrapping.Wrap,
        }));

        if (book.Authors.Length > 0)
        {
            rightHero.Children.Add(L10n.Skip(new TextBlock { Text = string.Join(" · ", book.Authors), FontSize = 13, Foreground = ThemeHelper.Brush(p.T2) }));
        }
        else if (book.Record)
        {
            rightHero.Children.Add(new TextBlock { Text = "纸质书 / 阅读档案", FontSize = 13, Foreground = ThemeHelper.Brush(p.T2) });
        }

        string fmtLabel = book.Record ? L10n.T("纸书") : FormatBookFormatBadge(book.Format);
        uint estMins = book.Format == BookFormat.Audio ? book.ListenMinutes : book.ReadMinutes;
        string specLine = string.Join(" · ", new[]
        {
            fmtLabel,
            !book.Record && chapterCount > 1 ? L10n.T($"{chapterCount} 章") : null,
            estMins >= 60 ? L10n.T($"约 {Math.Max(1, estMins / 60)} 小时") : (estMins > 0 ? L10n.T($"约 {estMins} 分钟") : null),
        }.Where(s => !string.IsNullOrEmpty(s)));
        rightHero.Children.Add(L10n.Skip(new TextBlock { Text = specLine, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3) }));

        // 你的评分（可交互五星）
        var ratingRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 2) };
        ratingRow.Children.Add(new TextBlock { Text = "你的评分", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        var stars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        for (byte s = 1; s <= 5; s++)
        {
            byte starVal = s;
            bool lit = (book.Rating ?? 0) >= starVal;
            var sb = new Button
            {
                Content = lit ? "★" : "☆",
                FontSize = 16,
                Foreground = ThemeHelper.Brush(lit ? tint.Accent : p.T3),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 0, 2, 0),
                MinWidth = 0,
                MinHeight = 0,
                UseSystemFocusVisuals = false,
            };
            sb.Click += (_, _) =>
            {
                byte? next = book.Rating == starVal ? null : starVal;
                _library.Core.SetRating(book.Id, next);
                _shelf.RefreshBooks();
            };
            stars.Children.Add(sb);
        }
        ratingRow.Children.Add(stars);
        rightHero.Children.Add(ratingRow);

        // 主按钮 + 状态下拉 + standingLine
        var actionLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        if (book.Record)
        {
            var (btnText, nextStatus) = book.Status switch
            {
                ReadingStatus.Want => ("开始读", ReadingStatus.Reading),
                ReadingStatus.Reading => ("记为读过", ReadingStatus.Finished),
                ReadingStatus.Finished => ("重读", ReadingStatus.Reading),
                _ => ("接着读", ReadingStatus.Reading),
            };
            var recBtn = MakeRecordButton(btnText, main: true, p, tint);
            recBtn.Click += (_, _) =>
            {
                _library.SetStatus(book.Id, nextStatus);
                _shelf.RefreshBooks();
            };
            actionLine.Children.Add(recBtn);
        }
        else
        {
            string mainText = book.Status == ReadingStatus.Finished
                ? "重读"
                : (book.Progress > 0 && chapterCount > 1 ? $"接着读 · 第 {curSection + 1} 章" : (book.Progress > 0 ? "接着读" : "开始读"));
            var openBtn = MakeRecordButton(mainText, main: true, p, tint);
            openBtn.Click += (_, _) => _shelf.TryOpenBook(book);
            actionLine.Children.Add(openBtn);
        }

        var stBtnSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        stBtnSp.Children.Add(YYIcons.Make(StatusIconName(book.Status), 12, ThemeHelper.Brush(p.T1)));
        stBtnSp.Children.Add(new TextBlock { Text = StatusDisplayName(book.Status), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
        stBtnSp.Children.Add(YYIcons.Make("chevron-down", 9, ThemeHelper.Brush(p.T3)));
        var stBtn = new Button
        {
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(12, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            UseSystemFocusVisuals = false,
            Content = stBtnSp,
            Flyout = BuildStatusMenuFlyout(book, dark, p, tint),
        };
        actionLine.Children.Add(stBtn);
        rightHero.Children.Add(actionLine);

        // Standing summary line
        var standingParts = new List<string>();
        if (book.StartedAt is long sa && sa > 0)
        {
            var sdt = DateTimeOffset.FromUnixTimeMilliseconds(sa).LocalDateTime;
            standingParts.Add(L10n.T($"{sdt.Month}月{sdt.Day}日开始"));
        }
        if (detail is not null)
        {
            try
            {
                var secTimes = _library.Core.SectionTimes(book.Id);
                uint rSec = (uint)secTimes.Sum(t => t.ReadSeconds);
                uint lSec = (uint)secTimes.Sum(t => t.ListenSeconds);
                if (rSec >= 60)
                {
                    uint rMin = rSec / 60;
                    standingParts.Add(rMin >= 60
                        ? (rMin % 60 == 0 ? L10n.T($"读了 {rMin / 60} 小时") : L10n.T($"读了 {rMin / 60} 小时 {rMin % 60} 分"))
                        : L10n.T($"读了 {rMin} 分钟"));
                }
                if (lSec >= 60)
                {
                    uint lMin = lSec / 60;
                    standingParts.Add(lMin >= 60
                        ? (lMin % 60 == 0 ? L10n.T($"听了 {lMin / 60} 小时") : L10n.T($"听了 {lMin / 60} 小时 {lMin % 60} 分"))
                        : L10n.T($"听了 {lMin} 分钟"));
                }
            }
            catch { }
        }
        if (standingParts.Count > 0)
            rightHero.Children.Add(L10n.Skip(new TextBlock { Text = string.Join(" · ", standingParts), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) }));

        Grid.SetColumn(rightHero, 1);
        hero.Children.Add(rightHero);
        page.Children.Add(hero);

        BookPodcastMention[] podcastMentions;
        try { podcastMentions = _library.Core.PodcastMentionsForBook(book.Id, book.Title); }
        catch { podcastMentions = []; }
        var bookPodcastIssues = _library.Podcasts.GetIssues().Where(i => i.BookIds.Contains(book.Id)).ToArray();

        if (book.Record && _subTab == "toc") _subTab = "notes";
        if (_subTab == "podcast" && podcastMentions.Length == 0)
            _subTab = book.Record ? "notes" : (chapterCount > 0 ? "toc" : "desc");

        // 子标签栏：明确两列 Grid（* | Auto），彻底杜绝标签与右侧「书房电台」按钮任何重叠或截断
        var subSection = new StackPanel { Spacing = 10 };
        var subBar = new Grid { ColumnSpacing = 12 };
        subBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        subBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var subTabsWrap = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var subTabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var tabDefs = new List<(string Key, string Icon, string Label, int? Count)>();
        if (!book.Record && chapterCount > 0) tabDefs.Add(("toc", "book", "目录", chapterCount));
        tabDefs.Add(("desc", "note-card", "简介", null));
        tabDefs.Add(("notes", "highlighter", "笔记", marks.Count));
        tabDefs.Add(("log", "clock", "记录", null));
        if (podcastMentions.Length > 0)
            tabDefs.Add(("podcast", "headphones", "播客", podcastMentions.Length));

        foreach (var (key, icon, lbl, cnt) in tabDefs)
        {
            bool on = _subTab == key;
            var tsp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            tsp.Children.Add(YYIcons.Make(icon, 12, ThemeHelper.Brush(on ? tint.Accent : p.T3)));
            tsp.Children.Add(new TextBlock
            {
                Text = lbl,
                FontSize = 12,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T2),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (cnt is int c)
            {
                tsp.Children.Add(new TextBlock
                {
                    Text = $"{c}",
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T3),
                    Opacity = on ? 0.85 : 0.75,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            var tb = new Button
            {
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(11, 0, 11, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(on ? 1 : 0),
                BorderBrush = on ? ThemeHelper.Brush(dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(18, 42, 43, 46)) : null,
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = tsp,
            };
            string nextK = key;
            tb.Click += (_, _) => { _subTab = nextK; _shelf.RefreshCurrentSubPage(); };
            subTabs.Children.Add(tb);
        }
        subTabsWrap.Child = subTabs;
        Grid.SetColumn(subTabsWrap, 0);
        subBar.Children.Add(subTabsWrap);

        if (_subTab != "podcast")
        {
            var studioPill = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                Height = 32,
                MinHeight = 32,
                Padding = new Thickness(12, 0, 12, 0),
                FontSize = 12,
                Content = "✦ 书房电台",
            };
            studioPill.Click += async (_, _) => await _shelf.OpenStudioSheetForBookAsync(book.Id);
            Grid.SetColumn(studioPill, 1);
            subBar.Children.Add(studioPill);
        }
        subSection.Children.Add(subBar);
        subSection.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });
        page.Children.Add(subSection);

        // 子标签内容区（直接铺在温润纸面 p.Bg 上，无笨重白底卡片外框）
        switch (_subTab)
        {
            case "toc" when detail is not null:
                page.Children.Add(BuildRecordTocTab(book, detail, curSection, p, tint));
                break;
            case "desc":
                page.Children.Add(BuildRecordDescTab(book, p, tint, serif));
                break;
            case "notes":
                page.Children.Add(BuildRecordNotesTab(book, detail, marks, p, tint, dark, serif));
                break;
            case "log":
                page.Children.Add(BuildRecordTimelineTab(book, marks, curSection, chapterCount, p, tint));
                break;
            case "podcast":
                page.Children.Add(BuildRecordPodcastTab(book, bookPodcastIssues, podcastMentions, p, tint, serif));
                break;
        }

        outerHost.Children.Add(page);
    }

    private FrameworkElement BuildRecordTocTab(BookSummary book, BookDetail detail, uint curSection, YYPalette p, YYTintColors tint)
    {
        var wrap = new StackPanel { Spacing = 10 };
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());

        var items = detail.Toc.Length > 0
            ? detail.Toc.Select((t, idx) => (Idx: idx, Title: t.Title, Section: t.Section)).ToArray()
            : detail.Sections.Select((s, idx) => (Idx: idx, Title: string.IsNullOrWhiteSpace(s.Title) ? $"第 {idx + 1} 章" : s.Title!, Section: (uint)idx)).ToArray();

        var head = new Grid();
        int pct = (int)Math.Round(book.Progress * 100);
        head.Children.Add(new TextBlock { Text = $"读到第 {curSection + 1} 章 · {pct}%", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });

        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (gridOn, icon, tip) in new[] { (false, "list", "列表"), (true, "grid-all", "格子") })
        {
            bool active = _tocGridMode == gridOn;
            var b = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                Background = active ? ThemeHelper.Brush(p.Inset) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = YYIcons.Make(icon, 12, ThemeHelper.Brush(active ? p.T1 : p.T3)),
            };
            ToolTipService.SetToolTip(b, tip);
            AutomationProperties.SetName(b, L10n.T(tip));
            bool gVal = gridOn;
            b.Click += (_, _) => { _tocGridMode = gVal; _shelf.RefreshCurrentSubPage(); };
            modeRow.Children.Add(b);
        }
        head.Children.Add(modeRow);
        wrap.Children.Add(head);

        if (_tocGridMode)
        {
            var gridPanel = new StackPanel { Spacing = 10 };
            int cols = 14;
            int rows = (items.Length + cols - 1) / cols;
            var g = new Grid { ColumnSpacing = 5, RowSpacing = 5, HorizontalAlignment = HorizontalAlignment.Left };
            for (int c = 0; c < Math.Min(cols, items.Length); c++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            for (int r = 0; r < rows; r++) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

            for (int i = 0; i < items.Length; i++)
            {
                var it = items[i];
                bool past = it.Section < curSection || book.Status == ReadingStatus.Finished;
                bool cur = it.Section == curSection && book.Status != ReadingStatus.Finished;
                var cell = new Button
                {
                    Width = 30,
                    Height = 30,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(7),
                    FontSize = 10.5,
                    FontWeight = cur ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(cur ? Colors.White : (past ? tint.Accent : p.T3)),
                    Background = ThemeHelper.Brush(cur ? tint.Fill : (past ? tint.Soft : p.Inset)),
                    BorderThickness = new Thickness(0),
                    Content = $"{i + 1}",
                };
                ToolTipService.SetToolTip(cell, it.Title);
                uint sec = it.Section;
                cell.Click += (_, _) => _shelf.TryOpenBook(book, section: sec, sentence: 0);
                Grid.SetRow(cell, i / cols);
                Grid.SetColumn(cell, i % cols);
                g.Children.Add(cell);
            }
            gridPanel.Children.Add(g);
            gridPanel.Children.Add(new TextBlock { Text = "浅色 = 读过 · 深色 = 正在读 · 灰格 = 未读（点击任一格直接跳章）", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
            wrap.Children.Add(gridPanel);
            return wrap;
        }

        // 书扉式点线目录（RecordChapterList：左对勾/书签竖条 + 序号 + 13.5px 宋体章名 + 点线 ··· + 右侧状态）
        var listStack = new StackPanel { Spacing = 0 };
        int cap = _tocExpanded ? items.Length : Math.Min(10, items.Length);
        for (int i = 0; i < cap; i++)
        {
            var it = items[i];
            bool past = it.Section < curSection || book.Status == ReadingStatus.Finished;
            bool cur = it.Section == curSection && book.Status != ReadingStatus.Finished;

            var row = new Grid { ColumnSpacing = 8, Height = 34 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (cur)
                row.Children.Add(new Border { Width = 2.5, Height = 13, CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(tint.Accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            else if (past)
                row.Children.Add(YYIcons.Make("checkmark", 10, ThemeHelper.Brush(p.T3)));

            var idxTb = new TextBlock { Text = $"{i + 1:D2}", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(idxTb, 1);
            row.Children.Add(idxTb);

            var titleTb = L10n.Skip(new TextBlock
            {
                Text = it.Title,
                FontFamily = serif,
                FontSize = 13.5,
                FontWeight = cur ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(cur ? tint.Accent : (past ? p.T2 : p.T1)),
                MaxWidth = 420,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(titleTb, 2);
            row.Children.Add(titleTb);

            var dots = L10n.Skip(new TextBlock
            {
                Text = "· · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · · ·",
                FontSize = 10,
                Foreground = ThemeHelper.Brush(p.Line),
                MaxLines = 1,
                TextTrimming = TextTrimming.Clip,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
            });
            Grid.SetColumn(dots, 3);
            row.Children.Add(dots);

            string rightLbl = cur ? "读到这" : (past ? "读过" : $"第 {it.Section + 1} 节");
            var rTb = new TextBlock { Text = rightLbl, FontSize = 11, Foreground = ThemeHelper.Brush(cur ? tint.Accent : p.T3), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(rTb, 4);
            row.Children.Add(rTb);

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(6, 0, 8, 0),
                UseSystemFocusVisuals = false,
                Content = row,
            };
            uint targetSec = it.Section;
            btn.Click += (_, _) => _shelf.TryOpenBook(book, section: targetSec, sentence: 0);
            listStack.Children.Add(btn);
        }

        if (items.Length > 10)
        {
            var moreBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 6, 8, 4),
                FontSize = 12,
                Foreground = ThemeHelper.Brush(tint.Accent),
                Content = _tocExpanded ? "收起目录" : $"展开全部 {items.Length} 章",
            };
            moreBtn.Click += (_, _) => { _tocExpanded = !_tocExpanded; _shelf.RefreshCurrentSubPage(); };
            listStack.Children.Add(moreBtn);
        }

        wrap.Children.Add(listStack);
        return wrap;
    }

    private FrameworkElement BuildRecordDescTab(BookSummary book, YYPalette p, YYTintColors tint, FontFamily serif)
    {
        var stack = new StackPanel { Spacing = 14, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        string? desc = null;
        try { desc = _library.Core.BookDescription(book.Id); } catch { }

        bool hasDesc = !string.IsNullOrWhiteSpace(desc);
        var descTb = new TextBlock
        {
            Text = hasDesc ? desc!.Trim() : "这本书的文件里没有附带内容简介。",
            FontFamily = serif,
            FontSize = 13.5,
            LineHeight = 23,
            Foreground = ThemeHelper.Brush(hasDesc ? p.T1 : p.T3),
            TextWrapping = TextWrapping.Wrap,
        };
        if (hasDesc) L10n.Skip(descTb);
        stack.Children.Add(descTb);

        var addedDt = DateTimeOffset.FromUnixTimeMilliseconds(book.AddedAt).LocalDateTime;
        stack.Children.Add(L10n.Skip(new TextBlock
        {
            Text = $"{L10n.T($"加入于 {addedDt:yyyy年M月d日}")} · {(book.Record ? L10n.T("仅保留阅读记录") : FormatBookFormatBadge(book.Format))}",
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
        }));

        stack.Children.Add(BuildTagsRow(book, p, tint));
        return stack;
    }

    private FrameworkElement BuildRecordNotesTab(BookSummary book, BookDetail? detail, IReadOnlyList<Mark> marks, YYPalette p, YYTintColors tint, bool dark, FontFamily serif)
    {
        var stack = new StackPanel { Spacing = 14 };

        var tocMap = new Dictionary<uint, string>();
        if (detail is not null)
        {
            foreach (var t in detail.Toc)
                if (!tocMap.ContainsKey(t.Section) && !string.IsNullOrWhiteSpace(t.Title))
                    tocMap[t.Section] = t.Title;
        }
        string ResolveMarkChapter(Mark mk)
        {
            if (book.Format == BookFormat.Pdf) return $"第 {mk.Section + 1} 页";
            if (tocMap.TryGetValue(mk.Section, out var t)) return t;
            if (detail is not null && mk.Section < detail.Sections.Length && !string.IsNullOrWhiteSpace(detail.Sections[mk.Section].Title))
                return detail.Sections[mk.Section].Title!;
            return book.Record ? "" : $"第 {mk.Section + 1} 章";
        }

        // 短评卡片（自动保存）
        var commentStack = new StackPanel { Spacing = 8 };
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = "短评", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        head.Children.Add(new TextBlock { Text = "只有你看得到", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
        commentStack.Children.Add(head);

        var commentBox = new TextBox
        {
            Text = book.Comment ?? "",
            PlaceholderText = "写一句读后感（离开输入框自动保存）…",
            FontFamily = serif,
            FontSize = 13.5,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 68,
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(12, 10, 12, 10),
        };
        commentBox.LostFocus += (_, _) =>
        {
            string val = commentBox.Text.Trim();
            if (val != (book.Comment ?? ""))
            {
                try { _library.Core.SetComment(book.Id, string.IsNullOrEmpty(val) ? null : val); _shelf.RefreshBooks(); } catch { }
            }
        };
        commentStack.Children.Add(commentBox);
        commentStack.Children.Add(BuildTagsRow(book, p, tint));
        stack.Children.Add(commentStack);

        // 本书划线列表（直接铺在纸面上，点击跳回原文，右键分享/删除）
        if (marks.Count > 0)
        {
            stack.Children.Add(new TextBlock { Text = $"划线 · {marks.Count} 条", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), Margin = new Thickness(0, 6, 0, 0) });
            var mStack = new StackPanel { Spacing = 0 };
            for (int i = 0; i < marks.Count; i++)
            {
                if (i > 0) mStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
                var mk = marks[i];
                var item = new StackPanel { Spacing = 6, Padding = new Thickness(0, 12, 0, 12) };
                item.Children.Add(BookVisuals.BuildHighlightedQuote(mk, p, dark, 13.5));
                if (!string.IsNullOrWhiteSpace(mk.Note))
                {
                    var tRow = new Grid { ColumnSpacing = 8 };
                    tRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                    tRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    tRow.Children.Add(new Border { Background = ThemeHelper.Brush(tint.Accent), CornerRadius = new CornerRadius(1) });
                    var ntb = L10n.Skip(new TextBlock { Text = mk.Note, FontFamily = new FontFamily(YYFontFamily.Kaiti.WindowsFamily()), FontSize = 12.5, Foreground = ThemeHelper.Brush(tint.Accent), TextWrapping = TextWrapping.Wrap });
                    Grid.SetColumn(ntb, 1);
                    tRow.Children.Add(ntb);
                    item.Children.Add(tRow);
                }

                var ctx = new MenuFlyout();
                if (!book.Record)
                {
                    var openMi = new MenuFlyoutItem { Text = L10n.T("回到书里这一页") };
                    openMi.Click += (_, _) => _shelf.TryOpenBook(book, section: mk.Section, sentence: (int)mk.Start.Sentence);
                    ctx.Items.Add(openMi);
                }

                var editNoteMi = new MenuFlyoutItem { Text = L10n.T(string.IsNullOrWhiteSpace(mk.Note) ? "写想法…" : "改想法…") };
                editNoteMi.Click += async (_, _) =>
                {
                    var updated = await ShowThoughtEditorSheetAsync(
                        _shelf.WindowXamlRoot, dark, _library.Prefs.Tint, mk.Text, mk.Note);
                    if (updated.Saved)
                    {
                        _library.SetMarkNote(book.Id, mk.Id, updated.Note);
                        _shelf.RefreshBooks();
                    }
                };
                ctx.Items.Add(editNoteMi);

                var styleSub = new MenuFlyoutSubItem { Text = L10n.T("换个样子") };
                foreach (var (st, stName) in new[]
                {
                    (MarkStyle.Yellow, "荧光黄"),
                    (MarkStyle.Blue, "浅蓝"),
                    (MarkStyle.Pink, "樱粉"),
                    (MarkStyle.Wave, "波浪线"),
                    (MarkStyle.Underline, "实线下划线"),
                    (MarkStyle.Dotted, "点线下划线"),
                })
                {
                    var targetStyle = st;
                    var smi = new ToggleMenuFlyoutItem { Text = L10n.T(stName), IsChecked = mk.Style == targetStyle };
                    smi.Click += (_, _) =>
                    {
                        _library.SetMarkStyle(book.Id, mk.Id, targetStyle);
                        _shelf.RefreshBooks();
                    };
                    styleSub.Items.Add(smi);
                }
                ctx.Items.Add(styleSub);

                var copyMi = new MenuFlyoutItem { Text = L10n.T("复制") };
                copyMi.Click += (_, _) =>
                {
                    var dp = new DataPackage();
                    dp.SetText(string.IsNullOrWhiteSpace(mk.Note) ? mk.Text : $"{mk.Text}\n—— {mk.Note}");
                    Clipboard.SetContent(dp);
                    _shelf.ShowToast("已复制划线", canUndo: false);
                };
                ctx.Items.Add(copyMi);

                string markChap = ResolveMarkChapter(mk);
                string? chapArg = string.IsNullOrWhiteSpace(markChap) ? null : markChap;
                var shareMi = new MenuFlyoutItem { Text = L10n.T("分享书摘…") };
                shareMi.Click += async (_, _) =>
                {
                    if (_shelf.WindowXamlRoot is { } xr)
                        await Yuyue.Reader.QuoteShareDialog.ShowAsync(
                            _shelf.OwnerWindow, xr, mk.Text, mk.Note, book.Title, book.Authors.FirstOrDefault(), book.CoverPath, chapter: chapArg, library: _library);
                };
                var delMi = new MenuFlyoutItem { Text = L10n.T("删除划线"), Foreground = ThemeHelper.Brush(p.Danger) };
                delMi.Click += (_, _) =>
                {
                    string delId = mk.Id;
                    _library.DeleteMark(book.Id, delId);
                    _shelf.RefreshBooks();
                    _shelf.ShowToast("已删除划线", canUndo: true, isDelete: true, undo: () =>
                    {
                        try { _library.Core.RestoreMark(book.Id, delId); _shelf.RefreshBooks(); } catch { }
                    });
                };
                ctx.Items.Add(shareMi);
                ctx.Items.Add(new MenuFlyoutSeparator());
                ctx.Items.Add(delMi);

                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    ContextFlyout = ctx,
                    Content = item,
                };
                if (!book.Record)
                    btn.Click += (_, _) => _shelf.TryOpenBook(book, section: mk.Section, sentence: (int)mk.Start.Sentence);
                mStack.Children.Add(btn);
            }
            stack.Children.Add(mStack);
        }

        return stack;
    }

    private FrameworkElement BuildRecordTimelineTab(BookSummary book, IReadOnlyList<Mark> marks, uint curSection, int chapterCount, YYPalette p, YYTintColors tint)
    {
        var events = new List<(long At, string Label)>();
        events.Add((book.AddedAt, "加进书架"));
        if (book.StartedAt is long sa && sa > 0) events.Add((sa, "开始阅读"));
        // Per-day mark clusters matching BookRecordPage.swift timeline
        foreach (var dayGrp in marks.GroupBy(m => DateTimeOffset.FromUnixTimeMilliseconds(m.At).LocalDateTime.Date))
        {
            long maxAt = dayGrp.Max(m => m.At);
            int cnt = dayGrp.Count();
            int thoughtCnt = dayGrp.Count(m => !string.IsNullOrWhiteSpace(m.Note));
            string lbl = thoughtCnt > 0 ? $"划了 {cnt} 句，记了 {thoughtCnt} 条想法" : $"划了 {cnt} 句原文";
            events.Add((maxAt, lbl));
        }
        if (!book.Record && book.OpenedAt is long oa && oa > 0 && book.Progress > 0)
            events.Add((oa, chapterCount > 1 ? $"读到 第 {curSection + 1} 章（{(int)Math.Round(book.Progress * 100)}%）" : $"读到 {(int)Math.Round(book.Progress * 100)}%"));
        if (book.FinishedAt is long fa && fa > 0)
            events.Add((fa, $"读过 {(book.Rating is byte r && r > 0 ? new string('★', r) : "")}".Trim()));

        var stack = new StackPanel { Spacing = 0, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left };
        var ordered = events.OrderByDescending(e => e.At).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            var (at, lbl) = ordered[i];
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime;
            var r = new Grid { ColumnSpacing = 14, Padding = new Thickness(0, 10, 0, 10) };
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r.Children.Add(new TextBlock { Text = $"{dt:yyyy/MM/dd}", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
            var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(tint.Accent), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(dot, 1);
            r.Children.Add(dot);
            var lTb = new TextBlock { Text = lbl, FontSize = 13, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lTb, 2);
            r.Children.Add(lTb);
            stack.Children.Add(r);
        }

        return stack;
    }

    private static string FormatMentionClock(double sec)
    {
        int s = Math.Max(0, (int)Math.Round(sec));
        int h = s / 3600;
        int m = (s % 3600) / 60;
        int rem = s % 60;
        return h > 0 ? $"{h}:{m:D2}:{rem:D2}" : $"{m:D2}:{rem:D2}";
    }

    private FrameworkElement BuildMentionRow(BookPodcastMention mt, YYPalette p, YYTintColors tint, FontFamily serif)
    {
        string showName = string.IsNullOrWhiteSpace(mt.ShowTitle) ? L10n.T("书房电台") : mt.ShowTitle;
        var r = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 8, 4, 8) };
        r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        r.Children.Add(new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(tint.Soft),
            VerticalAlignment = VerticalAlignment.Center,
            Child = YYIcons.Make("play", 9, ThemeHelper.Brush(tint.Accent)),
        });

        var m = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        m.Children.Add(L10n.Skip(new TextBlock
        {
            Text = mt.IssueTitle,
            FontFamily = serif,
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }));
        string subText = string.IsNullOrWhiteSpace(mt.Snippet) ? showName : $"{showName} · {mt.Snippet}";
        m.Children.Add(L10n.Skip(new TextBlock
        {
            Text = subText,
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T2),
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }));
        Grid.SetColumn(m, 1);
        r.Children.Add(m);

        string mid = mt.IssueId;
        double seekAt = mt.AtSec;
        var jumpBtn = MakeCompactCardButton($"跳到 {FormatMentionClock(seekAt)}", main: false, p, tint);
        jumpBtn.Click += (_, _) => _shelf.OpenPodcastInline(mid, seekAt, autoPlay: true);
        Grid.SetColumn(jumpBtn, 2);
        r.Children.Add(jumpBtn);

        var rowBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = r,
        };
        rowBtn.Click += (_, _) => _shelf.OpenPodcastInline(mid, seekAt, autoPlay: true);
        return rowBtn;
    }

    private FrameworkElement BuildRecordPodcastTab(
        BookSummary book,
        PodcastIssue[] issues,
        BookPodcastMention[] mentions,
        YYPalette p,
        YYTintColors tint,
        FontFamily serif)
    {
        var stack = new StackPanel { Spacing = 10 };

        if (issues.Length > 0 || mentions.Length > 0)
        {
            var head = new Grid();
            string headLabel = mentions.Length > 0
                ? $"你听过的节目里聊到过它 · {mentions.Length} 处提及"
                : "你听过的节目里聊到过它";
            head.Children.Add(new TextBlock { Text = headLabel, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
            var brewMoreBtn = MakeCompactCardButton("+ 酿一期", main: false, p, tint);
            brewMoreBtn.HorizontalAlignment = HorizontalAlignment.Right;
            brewMoreBtn.Click += async (_, _) => await _shelf.OpenStudioSheetForBookAsync(book.Id);
            head.Children.Add(brewMoreBtn);
            stack.Children.Add(head);

            for (int i = 0; i < mentions.Length; i++)
            {
                if (i > 0) stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
                stack.Children.Add(BuildMentionRow(mentions[i], p, tint, serif));
            }

            var unmentionedIssues = issues.Where(iss => mentions.All(mt => mt.IssueId != iss.Id)).ToArray();
            foreach (var iss in unmentionedIssues)
            {
                if (mentions.Length > 0) stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
                var r = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 6, 4, 6) };
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var m = new StackPanel { Spacing = 2 };
                m.Children.Add(L10n.Skip(new TextBlock { Text = iss.Title, FontFamily = serif, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) }));
                m.Children.Add(L10n.Skip(new TextBlock { Text = iss.Summary, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis }));
                r.Children.Add(m);
                string iid = iss.Id;
                var openBtn = MakeCompactCardButton("展开书页", main: true, p, tint);
                openBtn.Click += (_, _) => _shelf.OpenPodcastInline(iid);
                Grid.SetColumn(openBtn, 1);
                r.Children.Add(openBtn);
                stack.Children.Add(r);
            }
        }
        else
        {
            stack.Children.Add(new TextBlock { Text = $"围绕《{book.Title}》酿一期私人书房电台", FontFamily = serif, FontSize = 14.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            stack.Children.Add(new TextBlock { Text = "严格遵循防剧透范围，只读取你已读过的章节与划线想法。", FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap });
            foreach (var (sk, title, desc) in new[]
            {
                (StudioKind.BookGuide, "难书导读", "10 分钟梳理人物背景与核心脉络"),
                (StudioKind.WeeklyDigest, "书房周刊", "串联你在这本书里的划线与想法"),
                (StudioKind.Roundtable, "跨书圆桌", "把它和书架上另一本书摆在一起对谈"),
            })
            {
                var kind = sk;
                var sr = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 8, 10, 8) };
                sr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                sr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var sm = new StackPanel { Spacing = 1 };
                sm.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                sm.Children.Add(new TextBlock { Text = desc, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
                sr.Children.Add(sm);
                var goIcon = YYIcons.Make("chevron-right", 10.5, ThemeHelper.Brush(tint.Accent));
                Grid.SetColumn(goIcon, 1);
                sr.Children.Add(goIcon);
                var rowBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = ThemeHelper.Brush(p.Inset),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = sr,
                };
                rowBtn.Click += async (_, _) => await _shelf.OpenStudioSheetForBookAsync(book.Id, kind);
                stack.Children.Add(rowBtn);
            }
        }

        return stack;
    }

    private MenuFlyout BuildStatusMenuFlyout(BookSummary book, bool dark, YYPalette p, YYTintColors tint)
    {
        var menu = new MenuFlyout();
        foreach (var (lbl, st) in new[]
        {
            ("在读", ReadingStatus.Reading),
            ("想读", ReadingStatus.Want),
            ("读过", ReadingStatus.Finished),
            ("搁置", ReadingStatus.Paused),
            ("弃读", ReadingStatus.Dropped),
        })
        {
            var target = st;
            var item = new ToggleMenuFlyoutItem { Text = L10n.T(lbl), IsChecked = book.Status == target };
            item.Click += (_, _) =>
            {
                _library.SetStatus(book.Id, target);
                _shelf.RefreshBooks();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var colItem = new MenuFlyoutItem { Text = L10n.T("放进合集…") };
        colItem.Click += (_, _) => _shelf.OpenPickCollectionForBook(book);
        menu.Items.Add(colItem);

        var delItem = new MenuFlyoutItem { Text = L10n.T("删除…"), Foreground = ThemeHelper.Brush(p.Danger) };
        delItem.Click += (_, _) => _shelf.OpenDeleteForBook(book);
        menu.Items.Add(delItem);

        return menu;
    }

    internal static async Task<(bool Saved, string? Note)> ShowThoughtEditorSheetAsync(
        XamlRoot? xamlRoot,
        bool dark,
        YYTint tint,
        string quoteText,
        string? initialNote)
    {
        if (xamlRoot is null) return (false, null);
        var p = ThemeHelper.Palette(dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        ContentDialog? dlgRef = null;
        bool saved = false;

        var root = new StackPanel { Spacing = 14, Padding = new Thickness(22, 18, 22, 18) };
        root.Children.Add(YYChrome.MakeDialogHeader(
            string.IsNullOrWhiteSpace(initialNote) ? "写想法" : "改想法",
            null,
            () => dlgRef?.Hide(),
            dark));

        root.Children.Add(L10n.Skip(new TextBlock
        {
            Text = $"“{quoteText}”",
            FontFamily = serif,
            FontSize = 12.5,
            LineHeight = 20,
            Foreground = ThemeHelper.Brush(p.T2),
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
        }));

        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 96,
            Text = initialNote ?? "",
            PlaceholderText = "写下此刻的想法…",
            FontFamily = new FontFamily(YYFontFamily.Kaiti.WindowsFamily()),
            FontSize = 13.5,
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
        };
        root.Children.Add(box);

        var foot = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        foot.Children.Add(YYChrome.MakeDialogButton("取消", () => dlgRef?.Hide(), dark, tint, "secondary", height: 34, horizontalPadding: 16));
        foot.Children.Add(YYChrome.MakeDialogButton("保存", () => { saved = true; dlgRef?.Hide(); }, dark, tint, "dark", height: 34, horizontalPadding: 18));
        root.Children.Add(foot);

        dlgRef = YYChrome.CreateFluentDialog(xamlRoot, dark, 396, root, tint: tint);
        dlgRef.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectionStart = box.Text.Length;
        };
        await dlgRef.ShowSafeAsync(tint);
        return saved
            ? (true, string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim())
            : (false, null);
    }

    private FrameworkElement BuildTagsRow(BookSummary book, YYPalette p, YYTintColors tint)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var tag in book.Tags ?? [])
        {
            string t = tag;
            var chip = L10n.Skip(new Button
            {
                Content = $"#{t}  ×",
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(6),
                FontSize = 12,
                Foreground = ThemeHelper.Brush(tint.Accent),
                Background = ThemeHelper.Brush(tint.Soft),
                BorderBrush = ThemeHelper.Brush(tint.Edge),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                UseSystemFocusVisuals = false,
            });
            chip.Click += (_, _) => { try { _library.Core.SetTag(book.Id, t, false); _shelf.RefreshBooks(); } catch { } };
            row.Children.Add(chip);
        }

        var addBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            PlaceholderText = "+ 添加标签",
            Width = 116,
            Height = 28,
            MinHeight = 28,
            FontSize = 12,
            Padding = new Thickness(9, 0, 9, 0),
        };
        addBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && !string.IsNullOrWhiteSpace(addBox.Text))
            {
                e.Handled = true;
                try { _library.Core.SetTag(book.Id, addBox.Text.Trim().TrimStart('#'), true); _shelf.RefreshBooks(); } catch { }
            }
        };
        row.Children.Add(addBox);
        return row;
    }

    // ---------- Helpers ----------

    public static string StatusDisplayName(ReadingStatus st) => st switch
    {
        ReadingStatus.Reading => "在读",
        ReadingStatus.Want => "想读",
        ReadingStatus.Finished => "读过",
        ReadingStatus.Paused => "搁置",
        ReadingStatus.Dropped => "弃读",
        _ => "想读",
    };

    public static string StatusIconName(ReadingStatus st) => st switch
    {
        ReadingStatus.Reading => "book",
        ReadingStatus.Want => "bookmark",
        ReadingStatus.Finished => "check-circle",
        ReadingStatus.Paused => "pause-circle",
        ReadingStatus.Dropped => "xmark-circle",
        _ => "book",
    };

    private static Button MakeRecordButton(string label, bool main, YYPalette p, YYTintColors tint)
    {
        return new Button
        {
            Content = label,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(6),
            FontSize = 12.5,
            FontWeight = main ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = ThemeHelper.Brush(main ? Colors.White : p.T1),
            Background = ThemeHelper.Brush(main ? tint.Fill : p.Card),
            BorderBrush = ThemeHelper.Brush(main ? tint.Fill : p.Line),
            BorderThickness = new Thickness(main ? 0 : 1),
            UseSystemFocusVisuals = false,
        };
    }

    private static Button MakeCompactCardButton(string label, bool main, YYPalette p, YYTintColors tint)
    {
        return new Button
        {
            Content = label,
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(5),
            FontSize = 11.5,
            FontWeight = main ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium,
            Foreground = ThemeHelper.Brush(main ? tint.Accent : p.T2),
            Background = ThemeHelper.Brush(main ? tint.Soft : p.Card),
            BorderBrush = ThemeHelper.Brush(main ? tint.Edge : p.Line),
            BorderThickness = new Thickness(1),
            UseSystemFocusVisuals = false,
        };
    }

    private static string RelativeAgo(long unixMs)
    {
        var diff = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        if (diff.TotalMinutes < 2) return "刚刚";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} 分钟前";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} 小时前";
        if (diff.TotalDays < 30) return $"{(int)diff.TotalDays} 天前";
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
        return $"{dt.Month}月{dt.Day}日";
    }
}
