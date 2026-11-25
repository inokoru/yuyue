using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Reader;
using Yuyue.Speech;

namespace Yuyue.UI.Reader;

public sealed partial class ReaderPaneView
{
    private string _tocFilterQuery = "";
    private string _inBookSearchQuery = "";
    private IReadOnlyList<SearchHit> _searchHits = [];
    private int _activeSearchHitIdx = -1;
    private TextBox? _leftSearchBox;

    // Companion chat state
    private readonly List<(bool IsUser, string Text, IReadOnlyList<CompanionClient.CompanionSource>? Sources, IReadOnlyList<string>? FollowUps)> _companionMessages = [];
    private bool _companionNoSpoiler = true;
    private bool _companionBusy;
    private int _castRenderGen;

    private sealed record CompanionSlashCmd(
        string Name,
        string Title,
        string Subtitle,
        string Icon,
        string[] Keywords,
        bool TakesArg = false,
        string? ArgHint = null);

    private static readonly CompanionSlashCmd[] AllCompanionSlashCommands =
    [
        new("recap", "回顾前情", "梳理目前为止发生了什么", "clock-history", ["前情", "回顾", "剧情", "摘要", "summary"], TakesArg: true, ArgHint: "话题"),
        new("chapter", "总结本章", "概括当前章节的主要内容", "doc-lines", ["本章", "章节", "总结", "这一章"], TakesArg: true, ArgHint: "重点"),
        new("characters", "人物关系", "梳理已出场人物与关系", "people", ["人物", "角色", "关系", "谁", "cast"], TakesArg: true, ArgHint: "人物名"),
        new("explain", "解释含义", "解读选中句子或当前段落", "doc-search", ["解释", "解析", "意思", "含义"], TakesArg: true, ArgHint: "词句"),
        new("quote", "挑出金句", "从已读内容里找值得回味的话", "text-quote", ["金句", "摘录", "名句", "句子"], TakesArg: true, ArgHint: "主题"),
        new("quiz", "考考我", "根据已读内容出两道思考题", "feedback", ["考考我", "测验", "提问", "思考"], TakesArg: true, ArgHint: "范围"),
        new("read", "仅限已读", "切回不剧透模式", "eye-slash", ["已读", "不剧透", "防剧透"], TakesArg: true),
        new("whole", "全书范围", "检索整本书（可能剧透）", "book-open", ["全书", "剧透", "整本"], TakesArg: true),
        new("model", "切换模型", "打开模型与服务商选择", "sparkle", ["模型", "服务商", "切换", "provider"]),
        new("clear", "清空对话", "清除当前这本书的对话记录", "trash", ["清空", "新对话", "清除", "重置"]),
        new("settings", "书伴设置", "配置服务商与 API Key", "gear", ["设置", "配置", "key", "api"]),
    ];

    // ---------- 左侧停靠栏：目录 / 书签 / 搜索 ----------
    private void RenderLeftPane()
    {
        bool dark = CurrentPaperChrome?.IsDark ?? ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        LeftPaneTabsBar.Children.Clear();
        var leftSeg = YYChrome.MakeSegmentedBar(
            [("toc", "目录"), ("bookmarks", "书签"), ("search", "搜索")],
            _leftPaneTab,
            cap =>
            {
                _leftPaneTab = cap;
                if (!_isSecondarySplitPane)
                {
                    _library.Prefs.ReaderLeftPaneTab = cap;
                    _library.SavePrefs();
                }
                RenderLeftPane();
            },
            dark,
            fontSize: 11.5,
            height: 28);
        LeftPaneTabsBar.Children.Add(leftSeg);

        LeftPaneBody.Children.Clear();
        switch (_leftPaneTab)
        {
            case "bookmarks":
                LeftPaneBody.Children.Add(BuildBookmarksPane(dark));
                break;
            case "search":
                LeftPaneBody.Children.Add(BuildSearchPane(dark));
                break;
            default:
                LeftPaneBody.Children.Add(BuildTocPane(dark));
                break;
        }
    }

    private Grid BuildTocPane(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var filterBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            Text = _tocFilterQuery,
            PlaceholderText = L10n.T("筛选章节…"),
            Margin = new Thickness(10, 0, 10, 8),
            Height = 30,
            MinHeight = 30,
            FontSize = 12,
            Padding = new Thickness(9, 0, 9, 0),
        };
        var listStack = new StackPanel { Spacing = 2, Padding = new Thickness(8, 2, 8, 24) };

        void PopulateToc(string q)
        {
            listStack.Children.Clear();
            Button? activeBtn = null;
            var items = _detail.Toc;
            if (items.Length == 0)
            {
                for (uint i = 0; i < _detail.Sections.Length; i++)
                {
                    string title = _detail.Sections[i].Title ?? L10n.T($"第 {i + 1} 节");
                    if (!string.IsNullOrWhiteSpace(q) && !title.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                    var b = AddTocButton(listStack, title, 0, i, null, i == _currentSection, p, tc);
                    if (i == _currentSection && activeBtn is null) activeBtn = b;
                }
            }
            else
            {
                foreach (var item in items)
                {
                    if (!string.IsNullOrWhiteSpace(q) && !item.Title.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                    bool isCur = item.Section == _currentSection;
                    var b = AddTocButton(listStack, item.Title, item.Depth, item.Section, item.Anchor, isCur, p, tc);
                    if (isCur && activeBtn is null) activeBtn = b;
                }
            }

            if (activeBtn is not null)
            {
                activeBtn.Loaded += (_, _) =>
                {
                    try
                    {
                        activeBtn.StartBringIntoView(new BringIntoViewOptions
                        {
                            AnimationDesired = false,
                            VerticalAlignmentRatio = 0.35,
                        });
                    }
                    catch { }
                };
            }
        }

        filterBox.TextChanged += (_, _) =>
        {
            _tocFilterQuery = filterBox.Text ?? "";
            PopulateToc(_tocFilterQuery);
        };
        PopulateToc(_tocFilterQuery);

        var scroller = new ScrollViewer
        {
            Content = listStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 4),
        };
        Grid.SetRow(filterBox, 0);
        Grid.SetRow(scroller, 1);
        g.Children.Add(filterBox);
        g.Children.Add(scroller);
        return g;
    }

    private Button AddTocButton(StackPanel host, string title, ushort depth, uint section, string? anchor, bool isCur, YYPalette p, YYTintColors tc)
    {
        var rowHost = new Grid();
        if (isCur)
        {
            rowHost.Children.Add(new Border
            {
                Width = 3,
                Height = 14,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-8, 0, 0, 0),
                Background = ThemeHelper.Brush(tc.Accent),
            });
        }
        rowHost.Children.Add(L10n.Skip(new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = isCur ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = ThemeHelper.Brush(isCur ? tc.Accent : p.T1),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        }));

        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12 + Math.Min(3, (int)depth) * 14, 7, 10, 7),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            Background = isCur ? ThemeHelper.Brush(tc.Soft) : new SolidColorBrush(Colors.Transparent),
            UseSystemFocusVisuals = false,
            Content = rowHost,
        };
        btn.Click += (_, _) =>
        {
            PushHistoryPoint();
            RecordReturnOrigin();
            _pendingJumpAnchor = anchor;
            _pendingEndOfSection = false;
            LoadSection(section);
        };
        host.Children.Add(btn);
        return btn;
    }

    private Grid BuildBookmarksPane(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var addBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(10, 0, 10, 8),
            Content = L10n.T("+ 添加当前页书签 (Ctrl+D)"),
        };
        addBtn.Click += (_, _) => ToggleBookmarkAtCurrentPosition();
        Grid.SetRow(addBtn, 0);
        g.Children.Add(addBtn);

        var stack = new StackPanel { Spacing = 6, Padding = new Thickness(10, 0, 10, 16) };
        var bms = _library.GetBookmarks(_bookId);
        if (bms.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = L10n.T("还没有书签，按 Ctrl+D 随时记录当前位置。"),
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 16, 4, 0),
            });
        }
        else
        {
            foreach (var bm in bms.OrderByDescending(b => b.At))
            {
                var card = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 8, 10, 8),
                };
                var inner = new StackPanel { Spacing = 4 };
                var topRow = new Grid();
                topRow.Children.Add(new TextBlock
                {
                    Text = L10n.T($"第 {bm.Section + 1} 节"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(tc.Accent),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var delBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Content = "×",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Padding = new Thickness(4, 0, 4, 0),
                    MinHeight = 20,
                };
                string bmId = bm.Id;
                delBtn.Click += (_, _) =>
                {
                    _library.DeleteBookmark(_bookId, bmId);
                    RenderLeftPane();
                };
                topRow.Children.Add(delBtn);

                inner.Children.Add(topRow);
                inner.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = bm.Text,
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T1),
                    MaxLines = 3,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }));
                card.Child = inner;
                uint sCap = bm.Section;
                int sentCap = (int)bm.Sentence;
                card.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                        JumpTo(sCap, sentCap);
                };
                stack.Children.Add(card);
            }
        }

        var scroller = new ScrollViewer { Content = stack };
        Grid.SetRow(scroller, 1);
        g.Children.Add(scroller);
        return g;
    }

    private void ToggleBookmarkAtCurrentPosition()
    {
        uint sent = (uint)CurrentReadingSentence();
        var existing = _library.GetBookmarks(_bookId).FirstOrDefault(b => b.Section == _currentSection && Math.Abs((int)b.Sentence - (int)sent) <= 2);
        if (existing is not null)
        {
            _library.DeleteBookmark(_bookId, existing.Id);
            ShowReaderAlert("已移除此处书签");
        }
        else
        {
            _library.AddBookmark(_bookId, _currentSection, sent);
            ShowReaderAlert("已添加书签");
        }
        if (_leftPaneOpen && _leftPaneTab == "bookmarks") RenderLeftPane();
    }

    private Grid BuildSearchPane(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var box = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            Text = _inBookSearchQuery,
            PlaceholderText = L10n.T("搜索本书全文，Enter 查找…"),
            Margin = new Thickness(10, 0, 10, 6),
            Height = 30,
            MinHeight = 30,
            FontSize = 12,
            Padding = new Thickness(9, 0, 9, 0),
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                RunInBookSearch(box.Text ?? "");
                e.Handled = true;
            }
        };
        _leftSearchBox = box;
        Grid.SetRow(box, 0);
        g.Children.Add(box);

        // Prev / Next F3 bar
        var navBar = new Grid { Padding = new Thickness(10, 0, 10, 6), ColumnSpacing = 8 };
        navBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var navText = new TextBlock
        {
            Text = _searchHits.Count > 0
                ? L10n.T($"{Math.Max(1, _activeSearchHitIdx + 1)} / {_searchHits.Count} 处匹配")
                : (string.IsNullOrWhiteSpace(_inBookSearchQuery) ? L10n.T("支持 F3 / Shift+F3 切换") : L10n.T("未找到匹配项")),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(navText, 0);
        navBar.Children.Add(navText);
        var stepBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(stepBtns, 1);
        var prevBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Content = "↑", Padding = new Thickness(6, 0, 6, 0), MinHeight = 22 };
        prevBtn.Click += (_, _) => StepSearchHit(forward: false);
        var nextBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Content = "↓", Padding = new Thickness(6, 0, 6, 0), MinHeight = 22 };
        nextBtn.Click += (_, _) => StepSearchHit(forward: true);
        stepBtns.Children.Add(prevBtn);
        stepBtns.Children.Add(nextBtn);
        navBar.Children.Add(stepBtns);
        Grid.SetRow(navBar, 1);
        g.Children.Add(navBar);

        var stack = new StackPanel { Spacing = 6, Padding = new Thickness(10, 0, 10, 16) };
        for (int i = 0; i < Math.Min(_searchHits.Count, 120); i++)
        {
            var hit = _searchHits[i];
            int idxCap = i;
            bool isActive = i == _activeSearchHitIdx;
            var card = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(isActive ? tc.Soft : p.Card),
                BorderBrush = ThemeHelper.Brush(isActive ? tc.Accent : p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 7, 10, 7),
                Child = L10n.Skip(new TextBlock
                {
                    Text = $"{hit.Before}{hit.Matched}…",
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T1),
                    MaxLines = 3,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }),
            };
            card.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                    JumpToSearchHit(idxCap);
            };
            stack.Children.Add(card);
        }

        var scroller = new ScrollViewer { Content = stack };
        Grid.SetRow(scroller, 2);
        g.Children.Add(scroller);
        return g;
    }

    private void FocusLeftSearchBox()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_disposed) return;
            _leftSearchBox?.Focus(FocusState.Programmatic);
            _leftSearchBox?.SelectAll();
        });
    }

    private void RunInBookSearch(string query)
    {
        _inBookSearchQuery = query.Trim();
        if (string.IsNullOrEmpty(_inBookSearchQuery))
        {
            _searchHits = [];
            _activeSearchHitIdx = -1;
            if (_pager.IsInitialized) _ = _pager.Current.SetHitsAsync(null);
            RenderLeftPane();
            return;
        }
        try
        {
            _searchHits = _library.SearchBook(_bookId, _inBookSearchQuery);
            _activeSearchHitIdx = _searchHits.Count > 0 ? 0 : -1;
            RenderLeftPane();
            if (_searchHits.Count > 0) JumpToSearchHit(0);
        }
        catch { }
    }

    private void StepSearchHit(bool forward)
    {
        if (_searchHits.Count == 0) return;
        int next = forward
            ? (_activeSearchHitIdx + 1) % _searchHits.Count
            : (_activeSearchHitIdx - 1 + _searchHits.Count) % _searchHits.Count;
        JumpToSearchHit(next);
    }

    private void JumpToSearchHit(int index)
    {
        if (index < 0 || index >= _searchHits.Count) return;
        _activeSearchHitIdx = index;
        var hit = _searchHits[index];
        _pendingSearchHighlight = _inBookSearchQuery;
        JumpTo(hit.Section, (int)hit.Sentence);
        if (hit.Section == _currentSection && _pager.IsInitialized)
            _ = _pager.Current.SetHitsAsync(_inBookSearchQuery, (int)hit.Sentence, (int)hit.Nth);
        if (_leftPaneOpen && _leftPaneTab == "search") RenderLeftPane();
    }

    // ---------- 右侧停靠栏：本书笔记 / AI 书伴 / 演播 ----------
    private void RenderRightPane()
    {
        bool dark = CurrentPaperChrome?.IsDark ?? ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        RightPaneTabsBar.Children.Clear();
        var rightSeg = YYChrome.MakeSegmentedBar(
            [("notes", "本书笔记"), ("companion", "书伴 AI"), ("cast", "演播")],
            _rightPaneTab,
            cap =>
            {
                _rightPaneTab = cap;
                RenderRightPane();
                ApplyPaperAndChrome();
            },
            dark,
            fontSize: 11.5,
            height: 28);
        RightPaneTabsBar.Children.Add(rightSeg);

        RightPaneBody.Children.Clear();
        switch (_rightPaneTab)
        {
            case "companion":
                RightPaneBody.Children.Add(BuildCompanionPane(dark));
                break;
            case "cast":
                RightPaneBody.Children.Add(BuildCastPane(dark));
                break;
            default:
                RightPaneBody.Children.Add(BuildBookNotesPane(dark));
                break;
        }
    }

    private ScrollViewer BuildBookNotesPane(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var stack = new StackPanel { Spacing = 8, Padding = new Thickness(12, 4, 12, 18) };
        var marks = _library.GetMarks(_bookId).OrderBy(m => m.Section).ThenBy(m => m.Start.Sentence).ToList();

        if (marks.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = L10n.T("本书还没有划线或想法。在书页中拖动划选文字即可随手记录。"),
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 18, 4, 0),
            });
        }
        else
        {
            foreach (var mk in marks)
            {
                var card = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 10, 12, 9),
                };
                var inner = new StackPanel { Spacing = 6 };
                inner.Children.Add(BookVisuals.BuildHighlightedQuote(mk, p, dark, 12.5));

                if (!string.IsNullOrWhiteSpace(mk.Note))
                {
                    inner.Children.Add(L10n.Skip(new TextBlock
                    {
                        Text = mk.Note,
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.T2),
                        TextWrapping = TextWrapping.Wrap,
                    }));
                }

                var foot = new Grid();
                foot.Children.Add(new TextBlock
                {
                    Text = L10n.T($"第 {mk.Section + 1} 节"),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
                var shareBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Content = L10n.T("书摘"), FontSize = 11, MinHeight = 20, Padding = new Thickness(5, 0, 5, 0) };
                shareBtn.Click += async (_, _) =>
                {
                    if (RootGrid.XamlRoot is { } xr)
                        await QuoteShareDialog.ShowAsync(_ownerWindow, xr, mk.Text, mk.Note, _detail.Summary.Title, _detail.Summary.Authors.FirstOrDefault(), _detail.Summary.CoverPath, library: _library);
                };
                var delBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Content = L10n.T("删"), Foreground = ThemeHelper.Brush(p.Danger), FontSize = 11, MinHeight = 20, Padding = new Thickness(5, 0, 5, 0) };
                string mkId = mk.Id;
                delBtn.Click += (_, _) =>
                {
                    _library.DeleteMark(_bookId, mkId);
                    _ = RefreshSectionOverlaysAsync();
                    RenderRightPane();
                };
                acts.Children.Add(shareBtn);
                acts.Children.Add(delBtn);
                foot.Children.Add(acts);
                inner.Children.Add(foot);

                card.Child = inner;
                uint secCap = mk.Section;
                int sentCap = (int)mk.Start.Sentence;
                card.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                        JumpTo(secCap, sentCap);
                };
                stack.Children.Add(card);
            }
        }
        return new ScrollViewer { Content = stack };
    }

    private Grid BuildCompanionPane(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Top scope bar: Read-only No-Spoiler toggle + clear + inline model switcher pill
        var scopeBar = new Grid { Padding = new Thickness(12, 2, 12, 8), ColumnSpacing = 8 };
        scopeBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scopeBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var noSpoilerCb = new CheckBox
        {
            Content = L10n.T("仅已读（防剧透）"),
            IsChecked = _companionNoSpoiler,
            FontSize = 11.5,
            MinWidth = 0,
            MinHeight = 26,
            Padding = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        noSpoilerCb.Click += (_, _) => _companionNoSpoiler = noSpoilerCb.IsChecked == true;

        string provName = CompanionClient.FindPreset(_library.Prefs.AiProvider)?.Name
            ?? (_library.Prefs.AiCustomProviderNames.TryGetValue(_library.Prefs.AiProvider, out var cn) && !string.IsNullOrWhiteSpace(cn) ? cn : _library.Prefs.AiProvider);
        string shortModel = string.IsNullOrWhiteSpace(_library.Prefs.AiModel) ? provName : _library.Prefs.AiModel.Split('/').Last();
        var modelLabelTb = L10n.Skip(new TextBlock
        {
            Text = $"{shortModel} ▾",
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T2),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var modelBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Content = modelLabelTb,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 2, 6, 2),
            MinHeight = 24,
        };
        ToolTipService.SetToolTip(modelBtn, L10n.T("点击切换 AI 模型或服务商"));
        modelBtn.Click += (_, _) => ShowCompanionModelSwitcherFlyout(modelBtn, dark);

        var rightScopeBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (_companionMessages.Count > 0)
        {
            var clearBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("清空"),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                Padding = new Thickness(5, 2, 5, 2),
                MinHeight = 24,
            };
            clearBtn.Click += (_, _) => { _companionMessages.Clear(); RenderRightPane(); };
            rightScopeBtns.Children.Add(clearBtn);
        }
        rightScopeBtns.Children.Add(modelBtn);
        Grid.SetColumn(noSpoilerCb, 0);
        Grid.SetColumn(rightScopeBtns, 1);
        scopeBar.Children.Add(noSpoilerCb);
        scopeBar.Children.Add(rightScopeBtns);
        Grid.SetRow(scopeBar, 0);
        g.Children.Add(scopeBar);

        // Chat transcript
        var chatStack = new StackPanel { Spacing = 10, Padding = new Thickness(12, 4, 12, 12) };
        if (_companionMessages.Count == 0)
        {
            chatStack.Children.Add(new TextBlock
            {
                Text = L10n.T("向书伴提问本书内容、人物或思想，或输入 / 使用快捷指令。回答会附上原句定位，且可严守已读进度不剧透。"),
                FontSize = 12,
                LineHeight = 19,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
            });
            var quickRow = new StackPanel { Spacing = 7, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var prompt in new[] { "帮我梳理本章的核心脉络", "目前出场的几个主要人物是什么关系？", "提炼这一章最值得回味的细节" })
            {
                string pr = prompt;
                var qGrid = new Grid { ColumnSpacing = 8 };
                qGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                qGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                qGrid.Children.Add(new TextBlock
                {
                    Text = L10n.T(pr),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var chev = YYIcons.Make("chevron-right", 10.5, ThemeHelper.Brush(p.T3));
                chev.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(chev, 1);
                qGrid.Children.Add(chev);

                var qBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(12, 9, 10, 9),
                    CornerRadius = new CornerRadius(7),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    UseSystemFocusVisuals = false,
                    Content = qGrid,
                };
                qBtn.Click += (_, _) => _ = SendCompanionQuestionAsync(pr);
                quickRow.Children.Add(qBtn);
            }
            chatStack.Children.Add(quickRow);
        }
        else
        {
            for (int mi = 0; mi < _companionMessages.Count; mi++)
            {
                var msg = _companionMessages[mi];
                var bubble = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 9, 12, 9),
                    Background = ThemeHelper.Brush(msg.IsUser ? tc.Soft : p.Card),
                    BorderBrush = ThemeHelper.Brush(msg.IsUser ? tc.Edge : p.Line),
                    BorderThickness = new Thickness(1),
                };
                var bStack = new StackPanel { Spacing = 6 };
                bStack.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = msg.Text,
                    FontSize = 12.5,
                    LineHeight = 20,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                }));

                if (msg.Sources is { Count: > 0 })
                {
                    var cites = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };
                    foreach (var src in msg.Sources.Take(4))
                    {
                        var sCap = src;
                        var cBtn = new Button
                        {
                            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Padding = new Thickness(6, 3, 6, 3),
                            Content = L10n.Skip(new TextBlock
                            {
                                Text = $"[{src.Index}] {src.Snippet}",
                                FontSize = 11,
                                Foreground = ThemeHelper.Brush(tc.Accent),
                                MaxLines = 1,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                            }),
                        };
                        cBtn.Click += (_, _) => JumpTo(sCap.Section, (int)sCap.Sentence);
                        cites.Children.Add(cBtn);
                    }
                    bStack.Children.Add(cites);
                }

                bubble.Child = bStack;
                chatStack.Children.Add(bubble);

                // Follow-up chips under the latest assistant message
                if (!msg.IsUser && mi == _companionMessages.Count - 1 && !_companionBusy && msg.FollowUps is { Count: > 0 })
                {
                    var fuStack = new StackPanel { Spacing = 4, Margin = new Thickness(0, -2, 0, 0) };
                    foreach (var fu in msg.FollowUps.Take(3))
                    {
                        string fuCap = fu;
                        var fBtn = new Button
                        {
                            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Height = double.NaN,
                            MinHeight = 28,
                            Padding = new Thickness(10, 5, 10, 5),
                            Content = L10n.Skip(new TextBlock
                            {
                                Text = $"↳ {fuCap}",
                                FontSize = 11.5,
                                Foreground = ThemeHelper.Brush(p.T2),
                                TextWrapping = TextWrapping.Wrap,
                            }),
                        };
                        fBtn.Click += (_, _) => _ = SendCompanionQuestionAsync(fuCap);
                        fuStack.Children.Add(fBtn);
                    }
                    chatStack.Children.Add(fuStack);
                }
            }
        }
        if (_companionBusy)
        {
            chatStack.Children.Add(new TextBlock { Text = L10n.T("书伴正在翻阅原文并思考…"), FontSize = 12, Foreground = ThemeHelper.Brush(tc.Accent) });
        }

        var scroller = new ScrollViewer { Content = chatStack };
        Grid.SetRow(scroller, 1);
        g.Children.Add(scroller);

        // Slash Command popup card (shown right above input bar when "/" is active)
        var slashListStack = new StackPanel { Spacing = 2 };
        var slashPopup = new Border
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(10, 0, 10, 2),
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(8),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer { MaxHeight = 230, Content = slashListStack },
        };
        Grid.SetRow(slashPopup, 2);
        g.Children.Add(slashPopup);

        // Input bar: "/" slash command button + YYSingleLineTextBoxStyle input + Send button
        var inputBar = new Grid { Padding = new Thickness(10, 6, 10, 10), ColumnSpacing = 6 };
        inputBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var slashBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Width = 30,
            MinWidth = 30,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            Content = new TextBlock { Text = "/", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
        };
        ToolTipService.SetToolTip(slashBtn, L10n.T("快捷斜杠指令 (/chapter, /characters, /recap…)"));

        var inputBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            PlaceholderText = L10n.T("问书伴，或输入 / …"),
            AcceptsReturn = false,
        };
        var sendBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"],
            Height = 32,
            MinHeight = 32,
            Content = L10n.T("发送"),
            IsEnabled = !_companionBusy,
        };

        void PopulateSlashPopup(string filterText)
        {
            slashListStack.Children.Clear();
            string q = filterText.Trim().TrimStart('/', '／');
            int spIdx = q.IndexOfAny([' ', '\t']);
            if (spIdx >= 0) q = q[..spIdx];

            var matched = string.IsNullOrEmpty(q)
                ? AllCompanionSlashCommands
                : AllCompanionSlashCommands.Where(c =>
                    c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || c.Keywords.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase))).ToArray();

            if (matched.Length == 0)
            {
                slashPopup.Visibility = Visibility.Collapsed;
                return;
            }

            foreach (var cmd in matched)
            {
                var cCap = cmd;
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var ic = YYIcons.Make(cCap.Icon, 12.5, ThemeHelper.Brush(tc.Accent));
                ic.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(ic);

                var cmdNameTb = new TextBlock
                {
                    Text = $"/{cCap.Name}",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(cmdNameTb, 1);
                row.Children.Add(cmdNameTb);

                var cmdDescTb = new TextBlock
                {
                    Text = $"{L10n.T(cCap.Title)} · {L10n.T(cCap.Subtitle)}",
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(cmdDescTb, 2);
                row.Children.Add(cmdDescTb);

                var itemBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 5, 8, 5),
                    UseSystemFocusVisuals = false,
                    Content = row,
                };
                itemBtn.Click += (_, _) =>
                {
                    slashPopup.Visibility = Visibility.Collapsed;
                    inputBox.Text = "";
                    _ = ExecuteCompanionInputAsync("/" + cCap.Name, modelBtn, dark);
                };
                slashListStack.Children.Add(itemBtn);
            }
            slashPopup.Visibility = Visibility.Visible;
        }

        slashBtn.Click += (_, _) =>
        {
            if (slashPopup.Visibility == Visibility.Visible)
            {
                slashPopup.Visibility = Visibility.Collapsed;
                return;
            }
            PopulateSlashPopup("");
            inputBox.Focus(FocusState.Programmatic);
        };

        inputBox.TextChanged += (_, _) =>
        {
            string t = (inputBox.Text ?? "").TrimStart();
            if (t.StartsWith('/') || t.StartsWith('／'))
            {
                if (!t.Contains(' '))
                    PopulateSlashPopup(t);
                else
                    slashPopup.Visibility = Visibility.Collapsed;
            }
            else
            {
                slashPopup.Visibility = Visibility.Collapsed;
            }
        };

        void Submit()
        {
            string q = (inputBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(q) || _companionBusy) return;
            slashPopup.Visibility = Visibility.Collapsed;
            inputBox.Text = "";
            _ = ExecuteCompanionInputAsync(q, modelBtn, dark);
        }
        sendBtn.Click += (_, _) => Submit();
        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { Submit(); e.Handled = true; }
            else if (e.Key == VirtualKey.Escape && slashPopup.Visibility == Visibility.Visible) { slashPopup.Visibility = Visibility.Collapsed; e.Handled = true; }
        };

        Grid.SetColumn(slashBtn, 0);
        Grid.SetColumn(inputBox, 1);
        Grid.SetColumn(sendBtn, 2);
        inputBar.Children.Add(slashBtn);
        inputBar.Children.Add(inputBox);
        inputBar.Children.Add(sendBtn);
        Grid.SetRow(inputBar, 3);
        g.Children.Add(inputBar);

        return g;
    }

    private List<(string ProviderId, string ProviderName, List<string> Models)> GetAvailableCompanionProviderModels()
    {
        var prefs = _library.Prefs;
        var providerIds = new List<string>();

        void AddProviderIfAvailable(string pid)
        {
            if (string.IsNullOrWhiteSpace(pid) || providerIds.Contains(pid, StringComparer.OrdinalIgnoreCase))
                return;
            var preset = CompanionClient.FindPreset(pid);
            bool isCurrent = string.Equals(pid, prefs.AiProvider, StringComparison.OrdinalIgnoreCase);
            bool inConfigured = prefs.ConfiguredAiProviders.Contains(pid, StringComparer.OrdinalIgnoreCase);
            bool hasSavedKey = !string.IsNullOrWhiteSpace(_library.Secrets.Get($"ai.key.{pid}"))
                || (isCurrent && !string.IsNullOrWhiteSpace(_library.Secrets.Get("ai.apiKey")));
            bool keylessReady = preset is { NeedsKey: false } && (inConfigured || isCurrent);

            if (isCurrent || hasSavedKey || keylessReady || (inConfigured && !string.IsNullOrWhiteSpace(preset?.DefaultKey)))
                providerIds.Add(pid);
        }

        AddProviderIfAvailable(prefs.AiProvider);
        foreach (var pid in prefs.ConfiguredAiProviders) AddProviderIfAvailable(pid);
        foreach (var preset in CompanionClient.Presets) AddProviderIfAvailable(preset.Id);

        var result = new List<(string ProviderId, string ProviderName, List<string> Models)>();
        foreach (var pid in providerIds)
        {
            var preset = CompanionClient.FindPreset(pid);
            string pName = preset?.Name
                ?? (prefs.AiCustomProviderNames.TryGetValue(pid, out var cn) && !string.IsNullOrWhiteSpace(cn) ? cn : pid);

            var models = new List<string>();
            void AddModel(string? m)
            {
                if (!string.IsNullOrWhiteSpace(m) && !models.Contains(m.Trim(), StringComparer.OrdinalIgnoreCase))
                    models.Add(m.Trim());
            }

            if (string.Equals(pid, prefs.AiProvider, StringComparison.OrdinalIgnoreCase)) AddModel(prefs.AiModel);
            if (prefs.AiProviderModels.TryGetValue(pid, out var savedM)) AddModel(savedM);
            AddModel(preset?.DefaultModel);
            AddModel(preset?.DefaultFastModel);
            foreach (var starred in prefs.StarredAiModels)
            {
                string prefix = pid + ":";
                if (starred.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) AddModel(starred[prefix.Length..]);
            }
            if (prefs.AiRecentModels.TryGetValue(pid, out var recents))
                foreach (var rm in recents.Take(6)) AddModel(rm);
            foreach (var cached in _library.Companion.GetCachedModels(pid).Take(8)) AddModel(cached);

            if (models.Count > 0) result.Add((pid, pName, models));
        }
        return result;
    }

    private void SwitchCompanionModel(string providerId, string modelId)
    {
        var prefs = _library.Prefs;
        var preset = CompanionClient.FindPreset(providerId);
        bool sameProvider = string.Equals(prefs.AiProvider, providerId, StringComparison.OrdinalIgnoreCase);

        string? existingPerProviderKey = _library.Secrets.Get($"ai.key.{providerId}");
        if (!sameProvider && !string.IsNullOrWhiteSpace(prefs.AiProvider))
        {
            string curKey = _library.Secrets.Get("ai.apiKey") ?? "";
            if (!string.IsNullOrWhiteSpace(curKey))
                _library.Secrets.Set($"ai.key.{prefs.AiProvider}", curKey);
        }

        prefs.AiProvider = providerId;
        prefs.AiModel = modelId.Trim();
        prefs.AiProviderModels[providerId] = modelId.Trim();

        if (prefs.AiProviderUrls.TryGetValue(providerId, out var customUrl) && !string.IsNullOrWhiteSpace(customUrl))
            prefs.AiBaseUrl = customUrl;
        else if (!sameProvider && !string.IsNullOrWhiteSpace(preset?.BaseUrl))
            prefs.AiBaseUrl = preset.BaseUrl;
        else if (string.IsNullOrWhiteSpace(prefs.AiBaseUrl) && !string.IsNullOrWhiteSpace(preset?.BaseUrl))
            prefs.AiBaseUrl = preset.BaseUrl;

        if (!prefs.ConfiguredAiProviders.Contains(providerId, StringComparer.OrdinalIgnoreCase))
            prefs.ConfiguredAiProviders.Insert(0, providerId);

        if (!string.IsNullOrWhiteSpace(existingPerProviderKey))
            _library.Secrets.Set("ai.apiKey", existingPerProviderKey);
        else if (!sameProvider)
            _library.Secrets.Set("ai.apiKey", preset?.DefaultKey ?? "");

        _library.Companion.RecordModelUsed(providerId, modelId.Trim());
        _library.SavePrefs();
        if (_rightPaneOpen && _rightPaneTab == "companion") RenderRightPane();
    }

    private void ShowCompanionModelSwitcherFlyout(FrameworkElement anchor, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        Flyout? flyoutRef = null;

        var host = new StackPanel
        {
            Spacing = 3,
            Width = 264,
            Padding = new Thickness(8, 8, 8, 8),
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
        };
        ThemeHelper.ApplyControlThemeResources(host.Resources, dark, _library.Prefs.Tint);

        var groups = GetAvailableCompanionProviderModels();
        if (groups.Count == 0 || !_library.Companion.IsConfigured)
        {
            host.Children.Add(new TextBlock
            {
                Text = L10n.T("尚未配置可用的书伴模型"),
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T3),
                Margin = new Thickness(6, 4, 6, 4),
            });
        }
        else
        {
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var grp = groups[gi];
                if (gi > 0)
                    host.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(4, 4, 4, 2) });
                host.Children.Add(new TextBlock
                {
                    Text = grp.ProviderName,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(6, 3, 6, 2),
                });

                foreach (var modelId in grp.Models)
                {
                    string pidCap = grp.ProviderId;
                    string pNameCap = grp.ProviderName;
                    string midCap = modelId;
                    bool active = string.Equals(_library.Prefs.AiProvider, pidCap, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(_library.Prefs.AiModel, midCap, StringComparison.OrdinalIgnoreCase);

                    var mg = new Grid { ColumnSpacing = 6 };
                    mg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    mg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    mg.Children.Add(L10n.Skip(new TextBlock
                    {
                        Text = midCap,
                        FontSize = 12,
                        FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = ThemeHelper.Brush(active ? tc.Accent : p.T1),
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    }));
                    if (active)
                    {
                        var chk = new TextBlock { Text = "✓", FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = ThemeHelper.Brush(tc.Accent), VerticalAlignment = VerticalAlignment.Center };
                        Grid.SetColumn(chk, 1);
                        mg.Children.Add(chk);
                    }

                    var mBtn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = active ? ThemeHelper.Brush(tc.Soft) : new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(8, 5, 8, 5),
                        UseSystemFocusVisuals = false,
                        Content = mg,
                    };
                    mBtn.Click += (_, _) =>
                    {
                        flyoutRef?.Hide();
                        SwitchCompanionModel(pidCap, midCap);
                        ShowReaderAlert($"已切换模型：{pNameCap} · {midCap}");
                    };
                    host.Children.Add(mBtn);
                }
            }
        }

        host.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(4, 4, 4, 2) });
        var manageBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 5, 8, 5),
            Content = L10n.T("管理服务商与密钥…"),
            Foreground = ThemeHelper.Brush(tc.Accent),
            FontSize = 11.5,
        };
        manageBtn.Click += (_, _) => { flyoutRef?.Hide(); _ownerWindow.ShowSettings("companion"); };
        host.Children.Add(manageBtn);

        flyoutRef = new Flyout
        {
            FlyoutPresenterStyle = (Style)Application.Current.Resources["YYFloatFlyoutPresenterStyle"],
            Content = new ScrollViewer { MaxHeight = 380, Content = host },
        };
        flyoutRef.ShowAt(anchor);
    }

    private async Task ExecuteCompanionInputAsync(string rawInput, FrameworkElement modelAnchor, bool dark)
    {
        string trimmed = rawInput.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        if (trimmed.StartsWith('/') || trimmed.StartsWith('／'))
        {
            string body = trimmed[1..].Trim();
            int spIdx = body.IndexOfAny([' ', '\t', '\r', '\n']);
            string cmdToken = (spIdx >= 0 ? body[..spIdx] : body).Trim().ToLowerInvariant();
            string arg = spIdx >= 0 ? body[(spIdx + 1)..].Trim() : "";

            var matched = AllCompanionSlashCommands.FirstOrDefault(c =>
                string.Equals(c.Name, cmdToken, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Title, cmdToken, StringComparison.OrdinalIgnoreCase));
            string cmdName = matched?.Name ?? cmdToken;

            switch (cmdName)
            {
                case "clear":
                    _companionMessages.Clear();
                    RenderRightPane();
                    return;

                case "settings":
                    _ownerWindow.ShowSettings("companion");
                    return;

                case "read":
                    _companionNoSpoiler = true;
                    RenderRightPane();
                    if (!string.IsNullOrWhiteSpace(arg))
                        await SendCompanionQuestionAsync(arg);
                    else
                        ShowReaderAlert("书伴范围已切换为：仅限已读（防剧透）");
                    return;

                case "whole":
                    _companionNoSpoiler = false;
                    RenderRightPane();
                    if (!string.IsNullOrWhiteSpace(arg))
                        await SendCompanionQuestionAsync(arg);
                    else
                        ShowReaderAlert("书伴范围已切换为：全书范围");
                    return;

                case "model":
                    if (string.IsNullOrWhiteSpace(arg))
                    {
                        ShowCompanionModelSwitcherFlyout(modelAnchor, dark);
                    }
                    else
                    {
                        var groups = GetAvailableCompanionProviderModels();
                        foreach (var grp in groups)
                        {
                            var hit = grp.Models.FirstOrDefault(m => m.Contains(arg, StringComparison.OrdinalIgnoreCase));
                            if (hit is not null)
                            {
                                SwitchCompanionModel(grp.ProviderId, hit);
                                ShowReaderAlert($"已切换模型：{grp.ProviderName} · {hit}");
                                return;
                            }
                        }
                        ShowCompanionModelSwitcherFlyout(modelAnchor, dark);
                    }
                    return;

                case "recap":
                    await SendCompanionQuestionAsync(!string.IsNullOrWhiteSpace(arg)
                        ? $"帮我回顾一下目前为止关于「{arg}」的情节。"
                        : "帮我回顾一下目前为止发生了什么？");
                    return;

                case "chapter":
                {
                    string chap = ResolveCurrentChapterTitle();
                    await SendCompanionQuestionAsync(!string.IsNullOrWhiteSpace(arg)
                        ? $"结合「{arg}」，总结一下这一章讲了什么？"
                        : $"总结一下「{chap}」这一章讲了什么？");
                    return;
                }

                case "characters":
                    await SendCompanionQuestionAsync(!string.IsNullOrWhiteSpace(arg)
                        ? $"介绍一下「{arg}」是谁，以及和其他人物的关系。"
                        : "梳理一下目前出场的主要人物和他们之间的关系。");
                    return;

                case "explain":
                    await SendCompanionQuestionAsync(!string.IsNullOrWhiteSpace(arg)
                        ? $"解释一下「{arg}」在书里的意思和作用。"
                        : "结合上下文，解释一下当前这一段的意思和用意。");
                    return;

                case "quote":
                    await SendCompanionQuestionAsync(!string.IsNullOrWhiteSpace(arg)
                        ? $"从已读内容里挑几句和「{arg}」相关的精彩句子，并标出出处。"
                        : "从目前读过的内容里，挑两三句值得回味的句子并简短点评。");
                    return;

                case "quiz":
                    await SendCompanionQuestionAsync(!string.IsNullOrWhiteSpace(arg)
                        ? $"围绕「{arg}」，根据我已读的内容出两道思考题考考我。"
                        : "根据我目前读过的内容，出两道有意思的思考题考考我。");
                    return;
            }
        }

        await SendCompanionQuestionAsync(trimmed);
    }

    private async Task SendCompanionQuestionAsync(string question)
    {
        if (_companionBusy) return;
        _companionBusy = true;
        _companionMessages.Add((true, question, null, null));
        if (_rightPaneOpen && _rightPaneTab == "companion") RenderRightPane();

        try
        {
            int curSent = CurrentReadingSentence();
            var ans = await _library.Companion.AskBookStructuredAsync(
                _bookId,
                _detail.Summary.Title,
                _currentSection,
                curSent,
                question,
                wholeBook: !_companionNoSpoiler,
                detail: _detail);
            var (cleanAnswer, followUps) = CompanionClient.SplitFollowUpQuestions(ans.Text);
            _companionMessages.Add((false, cleanAnswer, ans.Sources, followUps));
        }
        catch (Exception ex)
        {
            _companionMessages.Add((false, $"书伴暂时无法回答：{CompanionClient.FormatErrorMessage(ex)}\n可在「设置 → 书伴」检查模型与密钥。", null, null));
        }
        finally
        {
            _companionBusy = false;
            if (!_disposed && _rightPaneOpen && _rightPaneTab == "companion") RenderRightPane();
        }
    }

    private ScrollViewer BuildCastPane(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var stack = new StackPanel { Spacing = 10, Padding = new Thickness(12, 6, 12, 22) };
        int gen = ++_castRenderGen;
        uint targetSection = _currentSection;
        string bookLang = string.IsNullOrWhiteSpace(_detail.Summary.Language) ? "zh" : _detail.Summary.Language;

        void RefreshCastAndAudio()
        {
            DramaSynthService.InvalidateCast(_bookId);
            _audio.InvalidateRolesAndSpeakers();
            _ = ApplySectionBubblesAsync();
            if (!_disposed && _rightPaneOpen && _rightPaneTab == "cast") RenderRightPane();
        }

        // 1. Drama Mode + Dialogue Bubbles + Narrator Voice Card
        var topCardStack = new StackPanel { Spacing = 8 };

        Grid MakeToggleRow(string title, string sub, bool value, Action<bool> onChanged, bool enabled = true)
        {
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock { Text = L10n.T(title), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(enabled ? p.T1 : p.T3) });
            left.Children.Add(new TextBlock { Text = L10n.T(sub), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
            var sw = new ToggleSwitch { IsOn = value, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center };
            sw.Toggled += (_, _) => onChanged(sw.IsOn);
            Grid.SetColumn(left, 0);
            Grid.SetColumn(sw, 1);
            g.Children.Add(left);
            g.Children.Add(sw);
            return g;
        }

        topCardStack.Children.Add(MakeToggleRow(
            "多角色广播剧演播",
            "为小说人物自动分配不同声线，带情感与环境音效",
            _dramaModeOn,
            on =>
            {
                _dramaModeOn = on;
                try { _library.Core.DramaSetEnabled(_bookId, on); } catch { }
                RefreshCastAndAudio();
            }));

        topCardStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });

        topCardStack.Children.Add(MakeToggleRow(
            "正文对话角色气泡",
            "在对话旁显示说话人头像，点击气泡即可改认说话人",
            _bubblesOn,
            on =>
            {
                _bubblesOn = on;
                try { _library.Core.SetBookBubbles(_bookId, on); } catch { }
                _ = ApplySectionBubblesAsync();
            }));

        topCardStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });

        // Clickable Narrator Voice selector
        var narGrid = new Grid { ColumnSpacing = 8 };
        narGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        narGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var narMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        narMeta.Children.Add(new TextBlock { Text = L10n.T("旁白音色"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        narMeta.Children.Add(L10n.Skip(new TextBlock { Text = $"{_audio.VoiceName} · {_audio.Rate:0.##}×", FontSize = 11, Foreground = ThemeHelper.Brush(p.T2), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }));
        var pickNarBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 11,
            Content = L10n.T("更换声音"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        pickNarBtn.Click += async (_, _) =>
        {
            string curVoicePref = _library.Prefs.GetVoiceForLanguage(bookLang);
            bool wasPlaying = _audio.IsPlaying;
            int resumeSent = _speakingSentence >= 0 ? _speakingSentence : CurrentReadingSentence();
            bool picked = false;
            await ShowVoicePickerSheetAsync(L10n.T("旁白的声音"), curVoicePref, vid =>
            {
                picked = true;
                _library.Prefs.SetVoiceForLanguage(bookLang, DramaSynthService.NormalizeVoiceId(vid) ?? vid);
                _library.SavePrefs();
                DramaSynthService.InvalidateCast(_bookId);
                ReloadVoicePrefs();
                RenderRightPane();
            });
            if (picked && wasPlaying && !_disposed) StartListenFromSentence(Math.Max(0, resumeSent));
        };
        Grid.SetColumn(narMeta, 0);
        Grid.SetColumn(pickNarBtn, 1);
        narGrid.Children.Add(narMeta);
        narGrid.Children.Add(pickNarBtn);
        topCardStack.Children.Add(narGrid);

        stack.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Child = topCardStack,
        });

        // Dynamic container populated off-UI thread with real Cast / SpeakerWorker / PreRender studio controls
        var dynamicHost = new StackPanel { Spacing = 10 };
        dynamicHost.Children.Add(new TextBlock { Text = L10n.T("正在读取本书角色与演播状态…"), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(2, 4, 0, 4) });
        stack.Children.Add(dynamicHost);

        _ = PopulateCastDynamicAsync(dynamicHost, gen, targetSection, bookLang, dark, p, tc, MakeToggleRow, RefreshCastAndAudio);

        // Quick tools section
        Button MakeCastAction(string icon, string title, string subtitle, bool primary, Action onClick)
        {
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ic = YYIcons.Make(icon, 14.5, ThemeHelper.Brush(primary ? tc.Accent : p.T2));
            ic.HorizontalAlignment = HorizontalAlignment.Center;
            ic.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(ic);

            var txt = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            txt.Children.Add(new TextBlock
            {
                Text = L10n.T(title),
                FontSize = 12.5,
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = ThemeHelper.Brush(primary ? tc.Accent : p.T1),
            });
            txt.Children.Add(new TextBlock
            {
                Text = L10n.T(subtitle),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
            });
            Grid.SetColumn(txt, 1);
            g.Children.Add(txt);

            var chev = YYIcons.Make("chevron-right", 10.5, ThemeHelper.Brush(primary ? tc.Accent : p.T3));
            chev.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(chev, 2);
            g.Children.Add(chev);

            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 9, 10, 9),
                CornerRadius = new CornerRadius(7),
                Background = ThemeHelper.Brush(primary ? tc.Soft : p.Card),
                BorderBrush = ThemeHelper.Brush(primary ? tc.Edge : p.Line),
                BorderThickness = new Thickness(1),
                UseSystemFocusVisuals = false,
                Content = g,
            };
            b.Click += (_, _) => onClick();
            return b;
        }

        stack.Children.Add(MakeCastAction("cast", "校对本章对话角色", "逐句确认未写明说话人的台词", true, async () =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await VoiceCloneAndDramaDialogs.ShowUnsureSpeakerReviewAsync(xr, _library, _bookId, _currentSection, RefreshCastAndAudio);
        }));
        stack.Children.Add(MakeCastAction("people", "全书角色与说话人", "搜索并查看全书已识别的所有人物", false, async () =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await VoiceCloneAndDramaDialogs.ShowPeoplePickerDialogAsync(xr, _library, _bookId, _currentSection, "全书角色与说话人");
        }));
        stack.Children.Add(MakeCastAction("mic", "克隆专属朗读音色", "录制 10 秒样本打造个人专属音色卡", false, async () =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await VoiceCloneAndDramaDialogs.ShowCloneVoiceDialogAsync(_ownerWindow, xr, _library, forBookId: _bookId);
        }));
        stack.Children.Add(MakeCastAction("pip", "置顶迷你听书浮窗", "缩小为置顶半透明浮窗，边办公边听书", false, OpenMiniPlayer));
        stack.Children.Add(MakeCastAction("headphones", "更多声源与语音引擎设置", "管理云端 / 本地 TTS、离线引擎与音效库", false, () => _ownerWindow.ShowSettings("listening")));

        return new ScrollViewer { Content = stack };
    }

    private async Task PopulateCastDynamicAsync(
        StackPanel dynamicHost,
        int gen,
        uint targetSection,
        string bookLang,
        bool dark,
        YYPalette p,
        YYTintColors tc,
        Func<string, string, bool, Action<bool>, bool, Grid> makeToggleRow,
        Action refreshCastAndAudio)
    {
        try
        {
            bool dramaOn = _dramaModeOn;
            var snap = await Task.Run(() =>
            {
                CastMember[] cast = [];
                Role[] roles = [];
                UnsureLine[] unsureLines = [];
                SameSuggestion[] suggestions = [];
                DramaProgress? prog = null;
                DramaSound? sound = null;
                DramaMade? made = null;
                var autoMap = new Dictionary<string, (string Voice, bool IsFallback)>(StringComparer.Ordinal);
                IReadOnlyList<SpeechVoiceOption> allVoices = [];
                int secLines = 0, secKnown = 0;

                var worker = VoiceCloneAndDramaDialogs.GetDramaWorkerStatus(_bookId);
                bool deferHeavyReview = targetSection >= 8;
                try { cast = _library.Core.BookCast(_bookId, targetSection); } catch { }
                try { roles = _library.Core.BookRoles(_bookId); } catch { }
                if (!deferHeavyReview)
                {
                    try { unsureLines = _library.Core.DramaReview(_bookId, targetSection); } catch { }
                    try { suggestions = _library.Core.DramaSameSuggestions(_bookId, targetSection); } catch { }
                }
                if (dramaOn || worker.Running)
                {
                    try { prog = _library.Core.DramaProgress(_bookId); } catch { }
                }
                try { allVoices = AudioPlayer.GetAllVoiceOptions(_library, _bookId); } catch { }
                try
                {
                    var play = _library.Core.DramaSection(_bookId, targetSection);
                    var seen = new HashSet<uint>();
                    foreach (var seg in play.Segments)
                    {
                        if (seg.Utterance is uint u && seen.Add(u) && seg.Kind != SegKind.Narration)
                        {
                            secLines++;
                            if (seg.Speaker is not DramaSpeaker.Unsure) secKnown++;
                        }
                    }
                }
                catch { }

                if (dramaOn)
                {
                    try { sound = _library.Core.DramaSound(_bookId); } catch { }
                    try { made = _library.Core.DramaMade(_bookId); } catch { }
                    try
                    {
                        DramaSynthService.EnsureCastPlan(_library, _bookId, bookLang);
                        foreach (var rv in _library.Core.DramaRoleVoices(_bookId))
                            autoMap[rv.Name] = (rv.Voice, rv.IsDialogueFallback);
                    }
                    catch { }
                }

                bool canAct = dramaOn && DramaSynthService.CanAct(_library, bookLang);
                bool preRunning = dramaOn && DramaSynthService.IsPreRendering(_bookId);
                bool isPreparing = dramaOn && DramaSynthService.IsPreparing(_library, _bookId);
                return (cast, roles, unsureLines, suggestions, prog, sound, made, autoMap, allVoices, secLines, secKnown, worker, canAct, preRunning, isPreparing);
            });

            if (_disposed || gen != _castRenderGen || !_rightPaneOpen || _rightPaneTab != "cast") return;
            dynamicHost.Children.Clear();

        string CleanVoiceName(string? rawId)
        {
            string? norm = DramaSynthService.NormalizeVoiceId(rawId);
            if (string.IsNullOrEmpty(norm)) return L10n.T("跟旁白");
            var hit = snap.allVoices.FirstOrDefault(v => v.Id == norm || v.Id == "sys:" + norm);
            if (hit is not null)
                return hit.Label.Replace("✦ ", "").Replace("☁ ", "").Split('（')[0].Trim();
            return norm.Length > 14 ? norm[..14] : norm;
        }

        // A. Background Speaker Recognition Worker Status Card
        var (running, _, lastErr) = snap.worker;
        if (snap.secLines > 0 || running || !string.IsNullOrWhiteSpace(lastErr) || snap.prog is not null)
        {
            var stGrid = new Grid { ColumnSpacing = 8 };
            stGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            stGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            stGrid.Children.Add(new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                Background = ThemeHelper.Brush(running ? tc.Accent : p.Inset),
                Child = running
                    ? new ProgressRing { IsActive = true, Width = 14, Height = 14, Foreground = ThemeHelper.Brush(Colors.White) }
                    : YYIcons.Make("sparkle", 12.5, ThemeHelper.Brush(tc.Accent)),
            });

            int missing = Math.Max(0, snap.secLines - snap.secKnown);
            string mainLine = running
                ? L10n.T("正在后台识别全书说话人…")
                : (!string.IsNullOrWhiteSpace(lastErr) ? L10n.T("说话人识别已暂停") : L10n.T($"本章台词 · {snap.secKnown} / {snap.secLines}"));
            string subLine = running && snap.prog is { } pr
                ? L10n.T($"已处理 {pr.SectionsDone} / {Math.Max(1u, pr.Sections)} 章")
                : (!string.IsNullOrWhiteSpace(lastErr)
                    ? lastErr!
                    : (snap.unsureLines.Length > 0
                        ? L10n.T($"截至当前有 {snap.unsureLines.Length} 句待确认")
                        : (missing > 0 ? L10n.T($"余 {missing} 句未写明说话人，可用 AI 自动补全") : L10n.T("读到这里的台词都已认好"))));

            var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(new TextBlock { Text = mainLine, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            meta.Children.Add(new TextBlock { Text = subLine, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
            Grid.SetColumn(meta, 1);
            stGrid.Children.Add(meta);

            var workerBtn = new Button
            {
                Style = (Style)Application.Current.Resources[running ? "YYFluentButtonStyle" : "YYFluentAccentButtonStyle"],
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(10, 0, 10, 0),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Content = L10n.T(running ? "暂停" : (_library.Companion.IsConfigured ? "开始识别" : "去配置")),
            };
            workerBtn.Click += (_, _) =>
            {
                if (running)
                {
                    VoiceCloneAndDramaDialogs.StopDramaWorker(_bookId);
                    RenderRightPane();
                }
                else if (!_library.Companion.IsConfigured)
                {
                    _ownerWindow.ShowSettings("companion");
                }
                else
                {
                    var weakSelf = new WeakReference<ReaderPaneView>(this);
                    VoiceCloneAndDramaDialogs.StartDramaSpeakerWorker(_library, _bookId, _currentSection, () =>
                    {
                        if (!weakSelf.TryGetTarget(out var self) || self._disposed) return;
                        self.DispatcherQueue.TryEnqueue(() =>
                        {
                            if (self._disposed) return;
                            self._audio.InvalidateRolesAndSpeakers();
                            if (self._bubblesOn) _ = self.ApplySectionBubblesAsync();
                            if (self._rightPaneOpen && self._rightPaneTab == "cast") self.RenderRightPane();
                        });
                    });
                    RenderRightPane();
                }
            };
            Grid.SetColumn(workerBtn, 2);
            stGrid.Children.Add(workerBtn);

            dynamicHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 9, 11, 9),
                Child = stGrid,
            });
        }

        // B. Same-Character Merge Suggestions
        foreach (var sug in snap.suggestions.Take(2))
        {
            var sg = sug;
            var sgGrid = new Grid { ColumnSpacing = 6 };
            sgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sgGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sgGrid.Children.Add(new TextBlock
            {
                Text = $"「{sg.A}」{L10n.T("与")}「{sg.B}」{L10n.T("可能是同一人")}",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T1),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var mergeBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Height = 24, MinHeight = 24, Padding = new Thickness(8, 0, 8, 0), FontSize = 10.5, Content = L10n.T("合并") };
            mergeBtn.Click += (_, _) => { try { _library.Core.DramaEditCast(_bookId, new CastEdit.Merge(sg.A, sg.B)); } catch { } refreshCastAndAudio(); };
            var noBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Height = 24, MinHeight = 24, Padding = new Thickness(8, 0, 8, 0), FontSize = 10.5, Content = L10n.T("不是") };
            noBtn.Click += (_, _) => { try { _library.Core.DramaEditCast(_bookId, new CastEdit.NotSame(sg.A, sg.B)); } catch { } refreshCastAndAudio(); };
            acts.Children.Add(mergeBtn);
            acts.Children.Add(noBtn);
            Grid.SetColumn(acts, 1);
            sgGrid.Children.Add(acts);
            dynamicHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(tc.Edge),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                Child = sgGrid,
            });
        }

        // C. Clickable Character Cast List (click -> ShowRoleDetailDialogAsync, right-click -> merge/gender/remove)
        if (snap.cast.Length > 0)
        {
            var roleMap = snap.roles.ToDictionary(r => r.Name, r => r);
            var autoMap = snap.autoMap;

            Button BuildRoleRow(CastMember member)
            {
                Color avCol = CustomPaperDialog.ParseHexColor(member.Color, tc.Accent);
                roleMap.TryGetValue(member.Name, out var customRole);
                bool hasCustom = !string.IsNullOrEmpty(customRole?.Voice?.Voice);
                string badgeLabel = hasCustom
                    ? CleanVoiceName(customRole!.Voice!.Voice)
                    : (_dramaModeOn && autoMap.TryGetValue(member.Name, out var ai)
                        ? (ai.IsFallback ? L10n.T("自动 · 跟旁白") : $"{CleanVoiceName(ai.Voice)} · {L10n.T("自动")}")
                        : L10n.T("跟旁白"));

                var parts = new List<string>();
                if (member.Aliases is { Length: > 0 }) parts.Add(string.Join("、", member.Aliases.Take(2)));
                parts.Add($"{member.LinesSoFar} {L10n.T("句")}");

                var cg = new Grid { ColumnSpacing = 8 };
                cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                cg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                cg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                cg.Children.Add(new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    Background = ThemeHelper.Brush(Color.FromArgb(42, avCol.R, avCol.G, avCol.B)),
                    Child = new TextBlock { Text = member.Avatar, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(avCol), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });

                var cm = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                cm.Children.Add(L10n.Skip(new TextBlock { Text = member.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }));
                cm.Children.Add(new TextBlock { Text = string.Join(" · ", parts), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(cm, 1);
                cg.Children.Add(cm);

                var voiceTb = new TextBlock
                {
                    Text = $"{badgeLabel} ›",
                    FontSize = 10.5,
                    FontWeight = hasCustom ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(hasCustom ? tc.Accent : p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 104,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(voiceTb, 2);
                cg.Children.Add(voiceTb);

                string roleName = member.Name;
                var ctx = new MenuFlyout();
                var mergeMi = new MenuFlyoutItem { Text = L10n.T("合并到别人…") };
                mergeMi.Click += async (_, _) =>
                {
                    if (RootGrid.XamlRoot is not { } xr) return;
                    var picked = await VoiceCloneAndDramaDialogs.ShowPeoplePickerDialogAsync(xr, _library, _bookId, _currentSection, $"{roleName}{L10n.T("合并到")}", includeGroup: false);
                    if (!_disposed && picked is UserChoice.Person target && target.Name != roleName)
                    {
                        try { _library.Core.DramaEditCast(_bookId, new CastEdit.Merge(target.Name, roleName)); refreshCastAndAudio(); } catch { }
                    }
                };
                var fMi = new MenuFlyoutItem { Text = member.Gender == CharacterGender.F ? $"✓ {L10n.T("设为女性")}" : L10n.T("设为女性") };
                fMi.Click += (_, _) => { try { _library.Core.DramaEditCast(_bookId, new CastEdit.SetGender(roleName, CharacterGender.F)); refreshCastAndAudio(); } catch { } };
                var mMi = new MenuFlyoutItem { Text = member.Gender == CharacterGender.M ? $"✓ {L10n.T("设为男性")}" : L10n.T("设为男性") };
                mMi.Click += (_, _) => { try { _library.Core.DramaEditCast(_bookId, new CastEdit.SetGender(roleName, CharacterGender.M)); refreshCastAndAudio(); } catch { } };
                var rmMi = new MenuFlyoutItem { Text = L10n.T("并非人物（移除）"), Foreground = ThemeHelper.Brush(p.Danger) };
                rmMi.Click += (_, _) => { try { _library.Core.DramaEditCast(_bookId, new CastEdit.Remove(roleName)); refreshCastAndAudio(); } catch { } };
                ctx.Items.Add(mergeMi);
                ctx.Items.Add(new MenuFlyoutSeparator());
                ctx.Items.Add(fMi);
                ctx.Items.Add(mMi);
                ctx.Items.Add(new MenuFlyoutSeparator());
                ctx.Items.Add(rmMi);

                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(10, 7, 10, 7),
                    CornerRadius = new CornerRadius(6),
                    UseSystemFocusVisuals = false,
                    ContextFlyout = ctx,
                    Content = cg,
                };
                btn.Click += async (_, _) =>
                {
                    if (RootGrid.XamlRoot is { } xr)
                        await VoiceCloneAndDramaDialogs.ShowRoleDetailDialogAsync(
                            _ownerWindow, xr, _library, _bookId, roleName,
                            (vt, sel, cb) => ShowVoicePickerSheetAsync(vt, sel, cb, roleName),
                            refreshCastAndAudio);
                };
                return btn;
            }

            void AddRoleGroup(string title, CastMember[] items)
            {
                if (items.Length == 0) return;
                dynamicHost.Children.Add(new TextBlock
                {
                    Text = $"{L10n.T(title)}（{items.Length}）",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(2, 2, 0, -4),
                });
                var listSp = new StackPanel { Spacing = 0 };
                for (int i = 0; i < items.Length; i++)
                {
                    if (i > 0) listSp.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(44, 0, 10, 0) });
                    listSp.Children.Add(BuildRoleRow(items[i]));
                }
                dynamicHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    Child = listSp,
                });
            }

            AddRoleGroup("本章出场角色", snap.cast.Where(m => m.LinesHere > 0).ToArray());
            AddRoleGroup("之前出场角色", snap.cast.Where(m => m.LinesHere == 0).Take(12).ToArray());
        }

        // D. Drama Sound Effects & Background Pre-Render Studio Card (when drama mode is ON)
        if (_dramaModeOn)
        {
            var studioStack = new StackPanel { Spacing = 8 };
            if (snap.sound is { } initDs)
            {
                var ds = initDs;
                studioStack.Children.Add(makeToggleRow(
                    "情感语气",
                    snap.canAct ? "按原文语境表现笑、叹、低语等语气" : "需使用支持情感的云端音色或增强本地音色",
                    ds.Acting,
                    v => { try { ds = ds with { Acting = v }; _library.Core.DramaSetSound(_bookId, ds); _audio.InvalidateRolesAndSpeakers(); } catch { } },
                    snap.canAct));
                studioStack.Children.Add(makeToggleRow(
                    "场景环境音",
                    "随室内、雨夜、水边等场景铺垫氛围音",
                    ds.Beds,
                    v =>
                    {
                        try
                        {
                            ds = ds with { Beds = v };
                            _library.Core.DramaSetSound(_bookId, ds);
                            if (ds.Beds || ds.Spots) _ = SoundLibraryService.EnsureAsync(_library.Core, _library.DataDir);
                            _audio.InvalidateRolesAndSpeakers();
                        }
                        catch { }
                    },
                    true));
                studioStack.Children.Add(makeToggleRow(
                    "剧情音效",
                    "随正文触发敲门、雷声、钟声等音效",
                    ds.Spots,
                    v =>
                    {
                        try
                        {
                            ds = ds with { Spots = v };
                            _library.Core.DramaSetSound(_bookId, ds);
                            if (ds.Beds || ds.Spots) _ = SoundLibraryService.EnsureAsync(_library.Core, _library.DataDir);
                            _audio.InvalidateRolesAndSpeakers();
                        }
                        catch { }
                    },
                    true));

                studioStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });
            }

            string preHint = snap.made is { Sections: > 0 } md
                ? L10n.T($"已预合成 {md.Made} / {md.Sections} 章")
                : L10n.T("开启后后台提前合成全书，切章零等待");
            studioStack.Children.Add(makeToggleRow(
                "后台预合成全书",
                preHint,
                snap.preRunning || snap.isPreparing,
                on =>
                {
                    DramaSynthService.SetPreparing(_library, _bookId, on);
                    if (on) DramaSynthService.StartPreRender(_library, _bookId, bookLang, _currentSection);
                    else DramaSynthService.StopPreRender(_bookId);
                    RenderRightPane();
                },
                true));

            var exportBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                MinHeight = 32,
                Content = snap.made is { Made: > 0 } m2
                    ? L10n.T($"导出多人有声书 (.m4b · {m2.Made} 章)…")
                    : L10n.T("导出多人有声书 (.m4b)…"),
            };
            exportBtn.Click += async (_, _) =>
            {
                string? path = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, $"{_detail.Summary.Title}-演播版", [("M4B 有声书", [".m4b"])]);
                if (string.IsNullOrEmpty(path) || _disposed) return;
                exportBtn.IsEnabled = false;
                exportBtn.Content = L10n.T("正在混音封装 .m4b…");
                bool ok = await DramaSynthService.ExportM4bAsync(_library, _bookId, path, (cur, tot) =>
                    DispatcherQueue.TryEnqueue(() => { if (!_disposed) exportBtn.Content = L10n.T($"正在混音封装 ({cur}/{tot})…"); }));
                if (_disposed) return;
                exportBtn.IsEnabled = true;
                ShowReaderAlert(ok ? $"已导出有声书：{Path.GetFileName(path)}" : "当前尚无已合成的演播章节可导出，请先开启演播或预合成");
                if (_rightPaneOpen && _rightPaneTab == "cast") RenderRightPane();
            };
            studioStack.Children.Add(exportBtn);

            dynamicHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                Child = studioStack,
            });
        }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReaderPaneView] PopulateCastDynamicAsync failed: {ex}");
        }
    }
}
