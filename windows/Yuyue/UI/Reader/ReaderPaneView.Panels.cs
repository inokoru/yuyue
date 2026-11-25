using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
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

        // Top scope bar: Read-only No-Spoiler toggle + active model pill
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
        var modelBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Content = new TextBlock
            {
                Text = _library.Prefs.AiModel,
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(6, 2, 6, 2),
            MinHeight = 24,
        };
        modelBtn.Click += (_, _) => _ownerWindow.ShowSettings("companion");
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
                Text = L10n.T("向书伴提问本书内容、人物或思想。回答会附上原句定位，且可严守已读进度不剧透。"),
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
            foreach (var msg in _companionMessages)
            {
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
            }
        }
        if (_companionBusy)
        {
            chatStack.Children.Add(new TextBlock { Text = L10n.T("书伴正在翻阅原文并思考…"), FontSize = 12, Foreground = ThemeHelper.Brush(tc.Accent) });
        }

        var scroller = new ScrollViewer { Content = chatStack };
        Grid.SetRow(scroller, 1);
        g.Children.Add(scroller);

        // Input box
        var inputBar = new Grid { Padding = new Thickness(10, 8, 10, 10), ColumnSpacing = 6 };
        inputBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var inputBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            PlaceholderText = L10n.T("问书伴… (Enter 发送)"),
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
        void Submit()
        {
            string q = (inputBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(q) || _companionBusy) return;
            inputBox.Text = "";
            _ = SendCompanionQuestionAsync(q);
        }
        sendBtn.Click += (_, _) => Submit();
        inputBox.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Submit(); e.Handled = true; } };

        Grid.SetColumn(inputBox, 0);
        Grid.SetColumn(sendBtn, 1);
        inputBar.Children.Add(inputBox);
        inputBar.Children.Add(sendBtn);
        Grid.SetRow(inputBar, 2);
        g.Children.Add(inputBar);

        return g;
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

        var stack = new StackPanel { Spacing = 12, Padding = new Thickness(12, 6, 12, 18) };

        // Current voice summary card
        var voiceCard = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = L10n.T("当前朗读音色"), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3) },
                    new TextBlock { Text = _audio.VoiceName, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) },
                    new TextBlock { Text = L10n.T($"倍速 {_audio.Rate:0.##}× · 支持 Kokoro 离线 / Edge / 云端 / 真人配对"), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap },
                },
            },
        };
        stack.Children.Add(voiceCard);

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
                Padding = new Thickness(12, 10, 10, 10),
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

        stack.Children.Add(MakeCastAction("headphones", "朗读音色与语速", "切换离线 Kokoro / Edge / 云端神经音色", false, () => _ownerWindow.ShowSettings("listening")));
        stack.Children.Add(MakeCastAction("pip", "置顶迷你听书浮窗", "缩小为置顶半透明浮窗，边办公边听书", false, OpenMiniPlayer));
        stack.Children.Add(MakeCastAction("cast", "校对本章对话角色", "为小说角色分配专属音色，开启广播剧演播", true, async () =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await VoiceCloneAndDramaDialogs.ShowUnsureSpeakerReviewAsync(xr, _library, _bookId, _currentSection, () => { if (!_disposed) RenderRightPane(); });
        }));
        stack.Children.Add(MakeCastAction("people", "全书角色与配音表", "查看并管理本书识别出的主要角色和说话人", false, async () =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await VoiceCloneAndDramaDialogs.ShowPeoplePickerDialogAsync(xr, _library, _bookId, _currentSection, "全书角色与说话人");
        }));
        stack.Children.Add(MakeCastAction("mic", "克隆专属朗读音色", "录制 10 秒样本打造个人专属音色卡", false, async () =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await VoiceCloneAndDramaDialogs.ShowCloneVoiceDialogAsync(_ownerWindow, xr, _library, forBookId: _bookId);
        }));
        stack.Children.Add(MakeCastAction("share-up", "导出多人有声书 (M4B)", "将已合成的广播剧演播打包为带章节的有声书文件", false, async () =>
        {
            string? path = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, $"{_detail.Summary.Title}-演播版", [("M4B 有声书", [".m4b"])]);
            if (string.IsNullOrEmpty(path) || _disposed) return;
            ShowReaderAlert("正在打包导出多人有声书 (.m4b)…", seconds: 8);
            bool ok = await DramaSynthService.ExportM4bAsync(_library, _bookId, path);
            if (_disposed) return;
            ShowReaderAlert(ok ? $"已导出有声书：{Path.GetFileName(path)}" : "当前尚无已合成的演播章节可导出，请先开启演播试听");
        }));

        return new ScrollViewer { Content = stack };
    }
}
