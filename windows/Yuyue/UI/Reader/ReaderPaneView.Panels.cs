using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;
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

    private sealed class CompanionAnchor
    {
        public string? MarkId { get; init; }
        public uint Section { get; init; }
        public TextPoint Start { get; init; } = null!;
        public TextPoint End { get; init; } = null!;
        public string Text { get; init; } = "";
    }

    private sealed class CompanionChatMsg
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public bool IsUser { get; init; }
        public string Text { get; init; } = "";
        public IReadOnlyList<CompanionClient.CompanionSource>? Sources { get; init; }
        public IReadOnlyList<string>? FollowUps { get; init; }
        public CompanionAnchor? Anchor { get; init; }
        public uint AskedSection { get; init; }
        public int AskedSentence { get; init; }
        public string? SavedMarkId { get; set; }
        public bool IsError { get; init; }
    }

    // Companion chat state
    private readonly List<CompanionChatMsg> _companionMessages = [];
    private bool _companionNoSpoiler = true;
    private bool _companionBusy;
    private bool _companionPicking;
    private readonly HashSet<string> _companionPickedIds = new(StringComparer.Ordinal);
    private bool _companionPreviewMerged;
    private ScrollViewer? _companionScroller;
    private Border? _companionStreamBubble;
    private string _companionStreamText = "";
    private bool _companionStreamThinking;
    private int _castRenderGen;

    private sealed record CompanionSlashCmd(
        string Name,
        string Title,
        string Subtitle,
        string Icon,
        string[] Keywords,
        bool TakesArg = false,
        string? ArgHint = null,
        string? CustomPrompt = null);

    private static readonly CompanionSlashCmd[] BuiltInAskSlashCommands =
    [
        new("recap", "回顾前情", "梳理目前为止发生了什么", "clock-history", ["前情", "回顾", "剧情", "摘要", "summary"], TakesArg: true, ArgHint: "话题"),
        new("chapter", "总结本章", "概括当前章节的主要内容", "doc-lines", ["本章", "章节", "总结", "这一章"], TakesArg: true, ArgHint: "重点"),
        new("characters", "人物关系", "梳理已出场人物与关系", "people", ["人物", "角色", "关系", "谁", "cast"], TakesArg: true, ArgHint: "人物名"),
        new("explain", "解释含义", "解读选中句子或当前段落", "doc-search", ["解释", "解析", "意思", "含义"], TakesArg: true, ArgHint: "词句"),
        new("quote", "挑出金句", "从已读内容里找值得回味的话", "text-quote", ["金句", "摘录", "名句", "句子"], TakesArg: true, ArgHint: "主题"),
        new("quiz", "考考我", "根据已读内容出两道思考题", "feedback", ["考考我", "测验", "提问", "思考"], TakesArg: true, ArgHint: "范围"),
    ];

    private static readonly CompanionSlashCmd[] BuiltInControlSlashCommands =
    [
        new("read", "仅限已读", "切回不剧透模式", "eye-slash", ["已读", "不剧透", "防剧透"], TakesArg: true),
        new("whole", "全书范围", "检索整本书（可能剧透）", "book-open", ["全书", "剧透", "整本"], TakesArg: true),
        new("model", "切换模型", "打开模型与服务商选择", "sparkle", ["模型", "服务商", "切换", "provider"]),
        new("clear", "清空对话", "清除当前这本书的对话记录", "trash", ["清空", "新对话", "清除", "重置"]),
        new("settings", "书伴设置", "配置服务商与 API Key", "gear", ["设置", "配置", "key", "api"]),
    ];

    private CompanionSlashCmd[] GetAllCompanionSlashCommands()
    {
        var customItems = CompanionPromptsHelper.GetItems(_library.Prefs);
        var list = new List<CompanionSlashCmd>(BuiltInAskSlashCommands.Length + customItems.Count + BuiltInControlSlashCommands.Length);
        list.AddRange(BuiltInAskSlashCommands);
        foreach (var item in customItems)
        {
            list.Add(new CompanionSlashCmd(
                item.Command,
                item.Prompt,
                "常用提问",
                "bubble-lines",
                ["常用", "自定义", "custom", "prompt"],
                TakesArg: true,
                CustomPrompt: item.Prompt));
        }
        list.AddRange(BuiltInControlSlashCommands);
        return list.ToArray();
    }

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

        var filterBox = YYChrome.MakeSearchBox("筛选章节…", _tocFilterQuery, height: 30, fontSize: 12);
        filterBox.Margin = new Thickness(10, 0, 10, 8);
        filterBox.Padding = new Thickness(9, 0, 4, 0);
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
                    ShowReaderAlert("已移除此处书签", "撤销", () =>
                    {
                        try { _library.RestoreBookmark(_bookId, bmId); } catch { }
                        RenderLeftPane();
                    });
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
            string bmId = existing.Id;
            _library.DeleteBookmark(_bookId, bmId);
            ShowReaderAlert("已移除此处书签", "撤销", () =>
            {
                try { _library.RestoreBookmark(_bookId, bmId); } catch { }
                if (_leftPaneOpen && _leftPaneTab == "bookmarks") RenderLeftPane();
            });
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

        var box = YYChrome.MakeSearchBox("搜索本书全文，Enter 查找…", _inBookSearchQuery, height: 30, fontSize: 12, onCleared: () => RunInBookSearch(""));
        box.Margin = new Thickness(10, 0, 10, 6);
        box.Padding = new Thickness(9, 0, 4, 0);
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
        // When paired with a human audiobook, seek to the exact matched word offset (parity with Apple ReaderNotes.swift)
        if (_audio.IsUsingPairedRecording && !string.IsNullOrEmpty(_pairedAudioBookId))
        {
            try
            {
                uint charAt = 0;
                var sents = _library.GetSentences(_bookId, hit.Section);
                if (hit.Sentence < sents.Count && !string.IsNullOrEmpty(_inBookSearchQuery))
                {
                    int pos = sents[(int)hit.Sentence].IndexOf(_inBookSearchQuery, StringComparison.OrdinalIgnoreCase);
                    if (pos >= 0) charAt = (uint)pos;
                }
                double? wordSec = _library.Core.AudiobookWordTime(_pairedAudioBookId, hit.Section, hit.Sentence, charAt);
                if (wordSec is double sec && sec >= 0)
                    _audio.SeekRecordingTo(sec);
            }
            catch { }
        }
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
            [("notes", "笔记"), ("companion", "书伴"), ("cast", "人物")],
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
                Text = L10n.T("还没有划线。选中文字，按 1–4 就划上"),
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

        // Top scope bar: picking header OR Read-only No-Spoiler toggle + multi-pick + clear + inline model switcher pill
        var scopeBar = new Grid { Padding = new Thickness(12, 2, 12, 8), ColumnSpacing = 8 };
        scopeBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scopeBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

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
        ToolTipService.SetToolTip(modelBtn, L10n.T("切换服务商与模型"));
        modelBtn.Click += (_, _) => ShowCompanionModelSwitcherFlyout(modelBtn, dark);

        void ExitCompanionPicking()
        {
            _companionPicking = false;
            _companionPickedIds.Clear();
            _companionPreviewMerged = false;
            RenderRightPane();
        }

        if (_companionPicking)
        {
            var pickCountTb = new TextBlock
            {
                Text = _companionPickedIds.Count > 0
                    ? L10n.T($"已选 {_companionPickedIds.Count} 条")
                    : L10n.T("多选存为想法"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(tc.Accent),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var pickRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var selAllBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("全选"),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(tc.Accent),
                Padding = new Thickness(5, 2, 5, 2),
                MinHeight = 24,
            };
            selAllBtn.Click += (_, _) =>
            {
                foreach (var m in _companionMessages)
                    if (!m.IsError) _companionPickedIds.Add(m.Id);
                RenderRightPane();
            };
            var cancelPickTopBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("取消"),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T2),
                Padding = new Thickness(5, 2, 5, 2),
                MinHeight = 24,
            };
            cancelPickTopBtn.Click += (_, _) => ExitCompanionPicking();
            pickRight.Children.Add(selAllBtn);
            pickRight.Children.Add(cancelPickTopBtn);
            Grid.SetColumn(pickCountTb, 0);
            Grid.SetColumn(pickRight, 1);
            scopeBar.Children.Add(pickCountTb);
            scopeBar.Children.Add(pickRight);
        }
        else
        {
            var noSpoilerCb = new CheckBox
            {
                Content = L10n.T("仅限已读"),
                IsChecked = _companionNoSpoiler,
                FontSize = 11.5,
                MinWidth = 0,
                MinHeight = 26,
                Padding = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            noSpoilerCb.Click += (_, _) => _companionNoSpoiler = noSpoilerCb.IsChecked == true;

            var rightScopeBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            if (_companionMessages.Any(m => !m.IsError))
            {
                var multiPickBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Content = L10n.T("多选"),
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T2),
                    Padding = new Thickness(5, 2, 5, 2),
                    MinHeight = 24,
                };
                ToolTipService.SetToolTip(multiPickBtn, L10n.T("多选存为想法"));
                multiPickBtn.Click += (_, _) =>
                {
                    _companionPicking = true;
                    _companionPickedIds.Clear();
                    _companionPreviewMerged = false;
                    RenderRightPane();
                };
                rightScopeBtns.Children.Add(multiPickBtn);
            }
            if (_companionMessages.Count > 0)
            {
                var clearBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Content = L10n.T("清空对话"),
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Padding = new Thickness(5, 2, 5, 2),
                    MinHeight = 24,
                };
                clearBtn.Click += (_, _) =>
                {
                    _companionMessages.Clear();
                    _companionPicking = false;
                    _companionPickedIds.Clear();
                    _companionPreviewMerged = false;
                    RenderRightPane();
                };
                rightScopeBtns.Children.Add(clearBtn);
            }
            rightScopeBtns.Children.Add(modelBtn);
            Grid.SetColumn(noSpoilerCb, 0);
            Grid.SetColumn(rightScopeBtns, 1);
            scopeBar.Children.Add(noSpoilerCb);
            scopeBar.Children.Add(rightScopeBtns);
        }
        Grid.SetRow(scopeBar, 0);
        g.Children.Add(scopeBar);

        // Chat transcript
        var chatStack = new StackPanel { Spacing = 10, Padding = new Thickness(12, 4, 12, 12) };
        if (_companionMessages.Count == 0)
        {
            chatStack.Children.Add(new TextBlock
            {
                Text = L10n.T("读到哪儿问到哪儿。回答标着出处，不剧透你还没读到的地方；也可点回答下方的「存为想法」收进笔记。"),
                FontSize = 12,
                LineHeight = 19,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
            });
            var quickRow = new StackPanel { Spacing = 7, Margin = new Thickness(0, 6, 0, 0) };
            var quickPrompts = new List<string>();
            var seenQuick = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cp in CompanionPromptsHelper.GetItems(_library.Prefs))
                if (!string.IsNullOrWhiteSpace(cp.Prompt) && seenQuick.Add(cp.Prompt))
                    quickPrompts.Add(cp.Prompt);
            foreach (var builtin in new[] { "前面发生了什么？", "这一章讲了什么？", "目前出场的几个主要人物是什么关系？" })
                if (seenQuick.Add(builtin))
                    quickPrompts.Add(builtin);

            foreach (var prompt in quickPrompts.Take(6))
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
                var msgCap = msg;
                bool pickable = _companionPicking && !msg.IsError;
                bool isPicked = pickable && _companionPickedIds.Contains(msg.Id);

                var bubble = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 9, 12, 9),
                    Background = ThemeHelper.Brush(msg.IsUser ? tc.Soft : p.Card),
                    BorderBrush = ThemeHelper.Brush(isPicked ? tc.Accent : (msg.IsUser ? tc.Edge : p.Line)),
                    BorderThickness = new Thickness(isPicked ? 1.5 : 1),
                };
                var bStack = new StackPanel { Spacing = 6 };
                if (msg.IsUser || msg.IsError)
                {
                    bStack.Children.Add(L10n.Skip(new TextBlock
                    {
                        Text = msg.Text,
                        FontSize = 12.5,
                        LineHeight = 20,
                        Foreground = ThemeHelper.Brush(msg.IsError ? p.Danger : p.T1),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = !pickable,
                    }));
                }
                else
                {
                    bStack.Children.Add(BuildCompanionRichContent(msg.Text, msg.Sources, p, tc, selectable: !pickable));
                }

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
                            IsHitTestVisible = !pickable,
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

                // Assistant answer action row: 存为想法 / 复制 / 多选 (1:1 macOS MacReaderCards.swift:1537)
                if (!msg.IsUser && !msg.IsError && !_companionPicking)
                {
                    var actRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
                    bool isSaved = !string.IsNullOrEmpty(msg.SavedMarkId);

                    var saveInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                    saveInner.Children.Add(YYIcons.Make(isSaved ? "checkmark" : "square.and.pencil", 10.5, ThemeHelper.Brush(isSaved ? p.Ok : p.T2)));
                    saveInner.Children.Add(new TextBlock
                    {
                        Text = L10n.T(isSaved ? "已存想法" : "存为想法"),
                        FontSize = 11,
                        FontWeight = isSaved ? FontWeights.SemiBold : FontWeights.Medium,
                        Foreground = ThemeHelper.Brush(isSaved ? p.Ok : p.T1),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    var saveBtn = new Button
                    {
                        Height = 25,
                        MinHeight = 25,
                        Padding = new Thickness(9, 0, 9, 0),
                        CornerRadius = new CornerRadius(5),
                        Background = isSaved ? ThemeHelper.Brush(Color.FromArgb(28, p.Ok.R, p.Ok.G, p.Ok.B)) : ThemeHelper.Brush(p.Inset),
                        BorderBrush = ThemeHelper.Brush(isSaved ? Color.FromArgb(90, p.Ok.R, p.Ok.G, p.Ok.B) : p.Line),
                        BorderThickness = new Thickness(1),
                        UseSystemFocusVisuals = false,
                        Content = saveInner,
                    };
                    ToolTipService.SetToolTip(saveBtn, L10n.T(isSaved ? "跳到这条想法" : "把这段回答存进划线想法"));
                    saveBtn.Click += (_, _) => SaveCompanionThought(msgCap);

                    var copyInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                    copyInner.Children.Add(YYIcons.Make("copy", 10, ThemeHelper.Brush(p.T2)));
                    copyInner.Children.Add(new TextBlock
                    {
                        Text = L10n.T("复制"),
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(p.T2),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    var copyBtn = new Button
                    {
                        Height = 25,
                        MinHeight = 25,
                        Padding = new Thickness(8, 0, 8, 0),
                        CornerRadius = new CornerRadius(5),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(1),
                        UseSystemFocusVisuals = false,
                        Content = copyInner,
                    };
                    copyBtn.Click += (_, _) =>
                    {
                        try
                        {
                            var dp = new DataPackage();
                            dp.SetText(StripCompanionMarkdownForNote(msgCap.Text));
                            Clipboard.SetContent(dp);
                            ShowReaderAlert("复制好了");
                        }
                        catch { }
                    };

                    var pickBtn = new Button
                    {
                        Height = 25,
                        MinHeight = 25,
                        Padding = new Thickness(8, 0, 8, 0),
                        CornerRadius = new CornerRadius(5),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(1),
                        UseSystemFocusVisuals = false,
                        Content = new TextBlock
                        {
                            Text = L10n.T("多选"),
                            FontSize = 11,
                            Foreground = ThemeHelper.Brush(p.T2),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    };
                    ToolTipService.SetToolTip(pickBtn, L10n.T("勾选多条对话合并存为想法"));
                    pickBtn.Click += (_, _) =>
                    {
                        _companionPicking = true;
                        _companionPickedIds.Clear();
                        _companionPickedIds.Add(msgCap.Id);
                        _companionPreviewMerged = false;
                        RenderRightPane();
                    };

                    actRow.Children.Add(saveBtn);
                    actRow.Children.Add(copyBtn);
                    actRow.Children.Add(pickBtn);
                    bStack.Children.Add(actRow);
                }

                bubble.Child = bStack;

                if (pickable)
                {
                    var pickRow = new Grid { ColumnSpacing = 8 };
                    pickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    pickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var ring = new Border
                    {
                        Width = 18,
                        Height = 18,
                        CornerRadius = new CornerRadius(9),
                        Margin = new Thickness(0, 8, 0, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                        Background = isPicked ? ThemeHelper.Brush(tc.Accent) : new SolidColorBrush(Colors.Transparent),
                        BorderBrush = ThemeHelper.Brush(isPicked ? tc.Accent : p.T3),
                        BorderThickness = new Thickness(1.5),
                        Child = isPicked ? YYIcons.Make("checkmark", 10, ThemeHelper.Brush(Colors.White)) : null,
                    };
                    Grid.SetColumn(ring, 0);
                    Grid.SetColumn(bubble, 1);
                    pickRow.Children.Add(ring);
                    pickRow.Children.Add(bubble);

                    var pickWrap = new Border
                    {
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(4),
                        Background = isPicked ? ThemeHelper.Brush(Color.FromArgb(45, tc.Accent.R, tc.Accent.G, tc.Accent.B)) : new SolidColorBrush(Colors.Transparent),
                        Child = pickRow,
                    };
                    pickWrap.PointerPressed += (_, ev) =>
                    {
                        if (!ev.GetCurrentPoint(pickWrap).Properties.IsLeftButtonPressed) return;
                        if (!_companionPickedIds.Add(msgCap.Id))
                            _companionPickedIds.Remove(msgCap.Id);
                        RenderRightPane();
                    };
                    chatStack.Children.Add(pickWrap);
                }
                else
                {
                    chatStack.Children.Add(bubble);
                }

                // Follow-up chips under the latest assistant message
                if (!_companionPicking && !msg.IsUser && mi == _companionMessages.Count - 1 && !_companionBusy && msg.FollowUps is { Count: > 0 })
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
        _companionStreamBubble = null;
        if (_companionBusy)
        {
            var streamBubble = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 9),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Child = BuildCompanionStreamingInner(_companionStreamText, _companionStreamThinking, p, tc),
            };
            _companionStreamBubble = streamBubble;
            chatStack.Children.Add(streamBubble);
        }

        var scroller = new ScrollViewer { Content = chatStack };
        _companionScroller = scroller;
        if (_companionMessages.Count > 0 || _companionBusy)
        {
            scroller.Loaded += (_, _) =>
            {
                DispatcherQueue.TryEnqueue(ScrollCompanionToBottom);
            };
        }
        Grid.SetRow(scroller, 1);
        g.Children.Add(scroller);

        if (_companionPicking)
        {
            var pickBarStack = new StackPanel { Spacing = 8 };
            var hintRow = new Grid { ColumnSpacing = 6 };
            hintRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hintRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hintRow.Children.Add(new TextBlock
            {
                Text = L10n.T("可同时勾选提问与多条回答，合并存为 1 条想法"),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (_companionPickedIds.Count > 0)
            {
                var prevBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Content = L10n.T(_companionPreviewMerged ? "收起预览" : "预览合并"),
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(tc.Accent),
                    Padding = new Thickness(4, 0, 4, 0),
                    MinHeight = 20,
                };
                prevBtn.Click += (_, _) => { _companionPreviewMerged = !_companionPreviewMerged; RenderRightPane(); };
                Grid.SetColumn(prevBtn, 1);
                hintRow.Children.Add(prevBtn);
            }
            pickBarStack.Children.Add(hintRow);

            if (_companionPreviewMerged && _companionPickedIds.Count > 0)
            {
                string composed = ComposeCompanionThought(_companionPickedIds);
                if (!string.IsNullOrWhiteSpace(composed))
                {
                    pickBarStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Background = ThemeHelper.Brush(p.Inset),
                        Padding = new Thickness(10, 7, 10, 7),
                        Child = new ScrollViewer
                        {
                            MaxHeight = 110,
                            Content = L10n.Skip(new TextBlock
                            {
                                Text = composed,
                                FontSize = 11.5,
                                LineHeight = 18,
                                Foreground = ThemeHelper.Brush(p.T2),
                                TextWrapping = TextWrapping.Wrap,
                                IsTextSelectionEnabled = true,
                            }),
                        },
                    });
                }
            }

            var pickFoot = new Grid { ColumnSpacing = 8 };
            pickFoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            pickFoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pickFoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cancelBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                Height = 30,
                MinHeight = 30,
                Content = L10n.T("取消"),
            };
            cancelBtn.Click += (_, _) => ExitCompanionPicking();
            int nPick = _companionPickedIds.Count;
            var mergeSaveBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"],
                Height = 30,
                MinHeight = 30,
                IsEnabled = nPick > 0,
                Content = nPick > 1 ? L10n.T($"合并存为想法 ({nPick})") : L10n.T($"存为想法 ({nPick})"),
            };
            mergeSaveBtn.Click += (_, _) =>
            {
                if (SaveCompanionPickedThoughts())
                    ExitCompanionPicking();
            };
            Grid.SetColumn(cancelBtn, 0);
            Grid.SetColumn(mergeSaveBtn, 2);
            pickFoot.Children.Add(cancelBtn);
            pickFoot.Children.Add(mergeSaveBtn);
            pickBarStack.Children.Add(pickFoot);

            var pickBar = new Border
            {
                Margin = new Thickness(10, 4, 10, 10),
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Child = pickBarStack,
            };
            Grid.SetRow(pickBar, 3);
            g.Children.Add(pickBar);
            return g;
        }

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
            VerticalAlignment = VerticalAlignment.Bottom,
            Content = new TextBlock { Text = "/", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center },
        };
        ToolTipService.SetToolTip(slashBtn, L10n.T("快捷指令 (/)"));

        var inputBox = new TextBox
        {
            PlaceholderText = L10n.T("问这本书，或输入 / …"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 32,
            MaxHeight = 88,
            FontSize = 12.5,
            Padding = new Thickness(10, 6, 10, 5),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Bottom,
            UseSystemFocusVisuals = false,
        };
        ToolTipService.SetToolTip(inputBox, L10n.T("Enter 发送 · Shift+Enter 换行"));
        var sendBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"],
            Height = 32,
            MinHeight = 32,
            VerticalAlignment = VerticalAlignment.Bottom,
            Content = L10n.T("发送"),
            IsEnabled = !_companionBusy,
        };

        void PopulateSlashPopup(string filterText)
        {
            slashListStack.Children.Clear();
            string q = filterText.Trim().TrimStart('/', '／');
            int spIdx = q.IndexOfAny([' ', '\t']);
            if (spIdx >= 0) q = q[..spIdx];

            var allCmds = GetAllCompanionSlashCommands();
            var matched = string.IsNullOrEmpty(q)
                ? allCmds
                : allCmds.Where(c =>
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

                var cmdNameTb = L10n.Skip(new TextBlock
                {
                    Text = $"/{cCap.Name}",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                Grid.SetColumn(cmdNameTb, 1);
                row.Children.Add(cmdNameTb);

                var cmdDescTb = L10n.Skip(new TextBlock
                {
                    Text = cCap.CustomPrompt is not null
                        ? $"{cCap.Title} · {L10n.T(cCap.Subtitle)}"
                        : $"{L10n.T(cCap.Title)} · {L10n.T(cCap.Subtitle)}",
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                });
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
        inputBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
                if (!shift)
                {
                    Submit();
                    e.Handled = true;
                }
            }
            else if (e.Key == VirtualKey.Escape && slashPopup.Visibility == Visibility.Visible)
            {
                slashPopup.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
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
                var grpHead = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Margin = new Thickness(6, 3, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (ThemeHelper.CreateBrandLogoImage(grp.ProviderId, dark, 14) is { } brandLogo)
                    grpHead.Children.Add(brandLogo);
                grpHead.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = grp.ProviderName,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                }));
                host.Children.Add(grpHead);

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

            var allCmds = GetAllCompanionSlashCommands();
            var matched = allCmds.FirstOrDefault(c =>
                string.Equals(c.Name, cmdToken, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Title, cmdToken, StringComparison.OrdinalIgnoreCase));
            if (matched?.CustomPrompt is string customPrompt)
            {
                await SendCompanionQuestionAsync(string.IsNullOrWhiteSpace(arg) ? customPrompt : $"{customPrompt}\n{arg}");
                return;
            }
            string cmdName = matched?.Name ?? cmdToken;

            switch (cmdName)
            {
                case "clear":
                    _companionMessages.Clear();
                    _companionPicking = false;
                    _companionPickedIds.Clear();
                    _companionPreviewMerged = false;
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

    private void ScrollCompanionToBottom()
    {
        if (_companionScroller is not { } sc) return;
        try
        {
            sc.UpdateLayout();
            sc.ChangeView(null, sc.ScrollableHeight, null, disableAnimation: true);
        }
        catch { }
    }

    private UIElement BuildCompanionStreamingInner(string visibleText, bool thinking, YYPalette p, YYTintColors tc)
    {
        if (string.IsNullOrWhiteSpace(visibleText))
        {
            return new TextBlock
            {
                Text = L10n.T(thinking ? "正在思考…" : "正在整理回答…"),
                FontSize = 12,
                Foreground = ThemeHelper.Brush(tc.Accent),
            };
        }

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(BuildCompanionRichContent(visibleText, null, p, tc, selectable: false, streaming: true));
        stack.Children.Add(new TextBlock
        {
            Text = L10n.T(thinking ? "正在思考…" : "生成中…"),
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(tc.Accent),
        });
        return stack;
    }

    private StackPanel BuildCompanionRichContent(
        string rawText,
        IReadOnlyList<CompanionClient.CompanionSource>? sources,
        YYPalette p,
        YYTintColors tc,
        bool selectable,
        bool streaming = false)
    {
        var container = new StackPanel { Spacing = 4 };
        L10n.Skip(container);

        var segments = CompanionClient.ParseRichSegments(rawText, streaming);
        if (segments.Count == 0)
        {
            container.Children.Add(new TextBlock
            {
                Text = rawText,
                FontSize = 12.5,
                LineHeight = 20,
                Foreground = ThemeHelper.Brush(p.T1),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = selectable,
            });
            return container;
        }

        var sourceMap = new Dictionary<int, CompanionClient.CompanionSource>();
        if (sources is not null)
        {
            foreach (var s in sources)
                sourceMap[s.Index] = s;
        }

        // Split flat segments into lines separated by Newline
        var lines = new List<List<CompanionClient.CompanionRichSegment>>();
        var curLine = new List<CompanionClient.CompanionRichSegment>();
        foreach (var seg in segments)
        {
            if (seg.Kind == CompanionClient.CompanionRichKind.Newline)
            {
                lines.Add(curLine);
                curLine = new List<CompanionClient.CompanionRichSegment>();
            }
            else
            {
                curLine.Add(seg);
            }
        }
        lines.Add(curLine);

        RichTextBlock BuildInlineRichBlock(IReadOnlyList<CompanionClient.CompanionRichSegment> inlineSegs, Color fgColor)
        {
            var rtb = new RichTextBlock
            {
                FontSize = 12.5,
                LineHeight = 20,
                Foreground = ThemeHelper.Brush(fgColor),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = selectable,
            };
            var para = new Paragraph();
            foreach (var seg in inlineSegs)
            {
                switch (seg.Kind)
                {
                    case CompanionClient.CompanionRichKind.Text:
                        para.Inlines.Add(new Run { Text = seg.Text });
                        break;
                    case CompanionClient.CompanionRichKind.Bold:
                        para.Inlines.Add(new Run { Text = seg.Text, FontWeight = FontWeights.SemiBold });
                        break;
                    case CompanionClient.CompanionRichKind.Italic:
                        para.Inlines.Add(new Run { Text = seg.Text, FontStyle = FontStyle.Italic });
                        break;
                    case CompanionClient.CompanionRichKind.Strike:
                        para.Inlines.Add(new Run
                        {
                            Text = seg.Text,
                            TextDecorations = TextDecorations.Strikethrough,
                            Foreground = ThemeHelper.Brush(p.T3),
                        });
                        break;
                    case CompanionClient.CompanionRichKind.Code:
                        para.Inlines.Add(new Run
                        {
                            Text = $" {seg.Text} ",
                            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                            FontSize = 11.5,
                            Foreground = ThemeHelper.Brush(tc.Accent),
                        });
                        break;
                    case CompanionClient.CompanionRichKind.Citation:
                        if (selectable && sourceMap.TryGetValue(seg.CitationIndex, out var srcCap))
                        {
                            var hl = new Hyperlink
                            {
                                UnderlineStyle = UnderlineStyle.None,
                                Foreground = ThemeHelper.Brush(tc.Accent),
                            };
                            hl.Inlines.Add(new Run
                            {
                                Text = $" [{seg.CitationIndex}]",
                                FontSize = 11,
                                FontWeight = FontWeights.SemiBold,
                            });
                            var jumpTarget = srcCap;
                            hl.Click += (_, _) => JumpTo(jumpTarget.Section, (int)jumpTarget.Sentence);
                            para.Inlines.Add(hl);
                        }
                        else
                        {
                            para.Inlines.Add(new Run
                            {
                                Text = $" [{seg.CitationIndex}]",
                                FontSize = 11,
                                FontWeight = FontWeights.SemiBold,
                                Foreground = ThemeHelper.Brush(tc.Accent),
                            });
                        }
                        break;
                }
            }
            rtb.Blocks.Add(para);
            return rtb;
        }

        foreach (var lineSegs in lines)
        {
            if (lineSegs.Count == 0)
            {
                if (container.Children.Count > 0)
                    container.Children.Add(new Border { Height = 2 });
                continue;
            }

            if (lineSegs[0].Kind == CompanionClient.CompanionRichKind.Heading)
            {
                container.Children.Add(new TextBlock
                {
                    Text = lineSegs[0].Text,
                    FontSize = 13.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, container.Children.Count > 0 ? 4 : 0, 0, 1),
                    IsTextSelectionEnabled = selectable,
                });
                continue;
            }

            if (lineSegs[0].Kind == CompanionClient.CompanionRichKind.Bullet)
            {
                var row = new Grid { ColumnSpacing = 6 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock
                {
                    Text = "•",
                    FontSize = 12.5,
                    LineHeight = 20,
                    FontWeight = FontWeights.Bold,
                    Foreground = ThemeHelper.Brush(tc.Accent),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                var body = BuildInlineRichBlock(lineSegs.Skip(1).ToList(), p.T1);
                Grid.SetColumn(body, 1);
                row.Children.Add(body);
                container.Children.Add(row);
                continue;
            }

            if (lineSegs[0].Kind == CompanionClient.CompanionRichKind.QuoteBar)
            {
                var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.5) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new Border
                {
                    Background = ThemeHelper.Brush(tc.Edge),
                    CornerRadius = new CornerRadius(1.5),
                });
                var body = BuildInlineRichBlock(lineSegs.Skip(1).ToList(), p.T2);
                Grid.SetColumn(body, 1);
                row.Children.Add(body);
                container.Children.Add(row);
                continue;
            }

            container.Children.Add(BuildInlineRichBlock(lineSegs, p.T1));
        }

        return container;
    }

    private async Task SendCompanionQuestionAsync(string question, CompanionAnchor? anchor = null)
    {
        if (_companionBusy) return;
        var uiQueue = DispatcherQueue;
        _companionBusy = true;
        _companionStreamText = "";
        _companionStreamThinking = true;

        var priorHistory = _companionMessages
            .Where(m => !m.IsError && !string.IsNullOrWhiteSpace(m.Text))
            .TakeLast(4)
            .Select(m => (Role: m.IsUser ? "user" : "assistant", Content: m.Text))
            .ToList();

        uint askedSec = _currentSection;
        int askedSent = CurrentReadingSentence();
        _companionMessages.Add(new CompanionChatMsg
        {
            IsUser = true,
            Text = question,
            Anchor = anchor,
            AskedSection = askedSec,
            AskedSentence = askedSent,
        });
        if (_rightPaneOpen && _rightPaneTab == "companion") RenderRightPane();

        long lastUiTick = 0;
        void OnStreamPreview(string visibleText, bool thinking)
        {
            _companionStreamText = visibleText;
            _companionStreamThinking = thinking;
            long now = Environment.TickCount64;
            if (now - lastUiTick < 55) return;
            lastUiTick = now;

            uiQueue.TryEnqueue(() =>
            {
                if (_disposed || !_companionBusy || _companionStreamBubble is null) return;
                bool dark = CurrentPaperChrome?.IsDark ?? ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
                var p = ThemeHelper.Palette(dark);
                var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
                _companionStreamBubble.Child = BuildCompanionStreamingInner(_companionStreamText, _companionStreamThinking, p, tc);
                ScrollCompanionToBottom();
            });
        }

        try
        {
            double frac = CalculateBookFraction(askedSec, askedSent);
            var ans = await _library.Companion.AskBookStructuredAsync(
                _bookId,
                _detail.Summary.Title,
                askedSec,
                askedSent,
                question,
                selectedQuote: anchor?.Text,
                wholeBook: !_companionNoSpoiler,
                history: priorHistory.Count > 0 ? priorHistory : null,
                detail: _detail,
                readingFraction: frac,
                onStreamPreview: OnStreamPreview);
            _companionMessages.Add(new CompanionChatMsg
            {
                IsUser = false,
                Text = ans.Text,
                Sources = ans.Sources,
                FollowUps = ans.NextQuestions,
                Anchor = anchor,
                AskedSection = askedSec,
                AskedSentence = askedSent,
            });
        }
        catch (Exception ex)
        {
            _companionMessages.Add(new CompanionChatMsg
            {
                IsUser = false,
                Text = $"书伴暂时无法回答：{CompanionClient.FormatErrorMessage(ex)}\n可在「设置 → 书伴」检查模型与密钥。",
                AskedSection = askedSec,
                AskedSentence = askedSent,
                IsError = true,
            });
        }
        finally
        {
            _companionBusy = false;
            _companionStreamText = "";
            _companionStreamThinking = false;
            _companionStreamBubble = null;
            if (!_disposed && _rightPaneOpen && _rightPaneTab == "companion") RenderRightPane();
        }
    }

    private static string StripCompanionMarkdownForNote(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string noCites = Regex.Replace(raw, @"[ \t\u00A0]*\[\d+\]", "");
        var lines = noCites.Replace("\r\n", "\n").Split('\n');
        var cleaned = new List<string>(lines.Length);
        foreach (var rawLine in lines)
        {
            string line = rawLine;
            if (Regex.IsMatch(line, @"^\s*([-*_])\1{2,}\s*$"))
            {
                cleaned.Add("");
                continue;
            }
            var hm = Regex.Match(line, @"^\s{0,3}#{1,6}\s+(.*)$");
            if (hm.Success) line = hm.Groups[1].Value;
            else
            {
                var bm = Regex.Match(line, @"^\s*[-*•]\s+(.*)$");
                if (bm.Success) line = "• " + bm.Groups[1].Value;
                else
                {
                    var qm = Regex.Match(line, @"^\s*>\s?(.*)$");
                    if (qm.Success) line = qm.Groups[1].Value;
                }
            }
            line = Regex.Replace(line, @"\*\*(.+?)\*\*", "$1");
            line = Regex.Replace(line, @"__(.+?)__", "$1");
            line = Regex.Replace(line, @"`([^`\n]+)`", "$1");
            line = Regex.Replace(line, @"~~(.+?)~~", "$1");
            line = Regex.Replace(line, @"(?<!\*)\*(?!\s)(.+?)(?<!\s)\*(?!\*)", "$1");
            cleaned.Add(line);
        }
        return Regex.Replace(string.Join("\n", cleaned), @"\n{3,}", "\n\n").Trim();
    }

    private static string MergeCompanionNote(string? existing, string addition)
    {
        string old = (existing ?? "").Trim();
        if (string.IsNullOrEmpty(old)) return addition;
        if (old.Contains(addition, StringComparison.Ordinal)) return old;
        return $"{old}\n\n{addition}";
    }

    private string ComposeCompanionThought(HashSet<string> pickedIds)
    {
        if (pickedIds.Count == 0) return "";
        var picked = _companionMessages.Where(m => !m.IsError && pickedIds.Contains(m.Id)).ToList();
        if (picked.Count == 0) return "";
        bool hasQ = picked.Any(m => m.IsUser && !string.IsNullOrWhiteSpace(m.Text));
        bool hasA = picked.Any(m => !m.IsUser && !string.IsNullOrWhiteSpace(StripCompanionMarkdownForNote(m.Text)));
        bool labelBoth = hasQ && hasA;

        var blocks = new List<string>();
        foreach (var m in picked)
        {
            string body = m.IsUser ? m.Text.Trim() : StripCompanionMarkdownForNote(m.Text);
            if (string.IsNullOrEmpty(body)) continue;
            if (labelBoth)
                blocks.Add((m.IsUser ? L10n.T("问：") : L10n.T("答：")) + body);
            else
                blocks.Add(body);
        }
        return string.Join("\n\n", blocks);
    }

    private MarkStyle PreferredHighlightStyle() => _library.Prefs.LastHighlightStyle switch
    {
        "Blue" => MarkStyle.Blue,
        "Pink" => MarkStyle.Pink,
        "Wave" => MarkStyle.Wave,
        _ => MarkStyle.Yellow,
    };

    private void SaveCompanionThought(CompanionChatMsg answerMsg)
    {
        if (answerMsg.IsUser || answerMsg.IsError) return;
        if (!string.IsNullOrEmpty(answerMsg.SavedMarkId))
        {
            var existing = _library.GetMarks(_bookId).FirstOrDefault(m => m.Id == answerMsg.SavedMarkId);
            if (existing is not null)
            {
                JumpTo(existing.Section, (int)existing.Start.Sentence);
                return;
            }
        }
        string clean = StripCompanionMarkdownForNote(answerMsg.Text);
        if (string.IsNullOrEmpty(clean)) return;
        var firstCited = answerMsg.Sources?.OrderBy(s => s.Index).FirstOrDefault();
        PersistCompanionThought(
            clean,
            answerMsg.Anchor,
            firstCited,
            answerMsg.AskedSection,
            answerMsg.AskedSentence,
            [answerMsg],
            merged: false);
    }

    private bool SaveCompanionPickedThoughts()
    {
        string clean = ComposeCompanionThought(_companionPickedIds);
        if (string.IsNullOrEmpty(clean)) return false;
        var picked = _companionMessages.Where(m => !m.IsError && _companionPickedIds.Contains(m.Id)).ToList();
        if (picked.Count == 0) return false;
        var first = picked[0];
        var anchor = picked.Select(m => m.Anchor).FirstOrDefault(a => a?.MarkId is not null)
            ?? picked.Select(m => m.Anchor).FirstOrDefault(a => a is not null);
        var firstCited = picked
            .Where(m => !m.IsUser && m.Sources is { Count: > 0 })
            .Select(m => m.Sources!.OrderBy(s => s.Index).FirstOrDefault())
            .FirstOrDefault(s => s is not null);
        var answerTargets = picked.Where(m => !m.IsUser).ToList();
        return PersistCompanionThought(
            clean,
            anchor,
            firstCited,
            first.AskedSection,
            first.AskedSentence,
            answerTargets,
            merged: picked.Count > 1);
    }

    private bool PersistCompanionThought(
        string clean,
        CompanionAnchor? anchor,
        CompanionClient.CompanionSource? fallbackSource,
        uint askedSection,
        int askedSentence,
        List<CompanionChatMsg> stampTargets,
        bool merged)
    {
        string toastTitle = merged ? L10n.T("已合并存进想法") : L10n.T("已存进想法");
        var prevSaved = stampTargets.ToDictionary(m => m, m => m.SavedMarkId);
        void Stamp(string? markId) { foreach (var m in stampTargets) m.SavedMarkId = markId; }
        void RestoreStamp() { foreach (var kv in prevSaved) kv.Key.SavedMarkId = kv.Value; }

        try
        {
            var marks = _library.GetMarks(_bookId);

            // 1. Existing highlight tapped via 问: append/update its thought
            if (anchor?.MarkId is string mid && marks.FirstOrDefault(m => m.Id == mid) is { } targetMark)
            {
                string? prevNote = targetMark.Note;
                _library.SetMarkNote(_bookId, mid, MergeCompanionNote(prevNote, clean));
                Stamp(mid);
                _ = RefreshSectionOverlaysAsync();
                if (_rightPaneOpen) RenderRightPane();
                ShowReaderAlert(toastTitle, "撤销", () =>
                {
                    try { _library.SetMarkNote(_bookId, mid, prevNote); } catch { }
                    RestoreStamp();
                    _ = RefreshSectionOverlaysAsync();
                    if (_rightPaneOpen) RenderRightPane();
                });
                return true;
            }

            // 2. Fresh text selection handed via 问: highlight that exact span with the thought
            if (anchor is not null && !string.IsNullOrWhiteSpace(anchor.Text))
            {
                var created = _library.AddMark(_bookId, anchor.Section, anchor.Start, anchor.End, anchor.Text, PreferredHighlightStyle(), clean);
                Stamp(created.Id);
                _ = RefreshSectionOverlaysAsync();
                if (_rightPaneOpen) RenderRightPane();
                ShowReaderAlert(toastTitle, "撤销", () =>
                {
                    try { _library.DeleteMark(_bookId, created.Id); } catch { }
                    RestoreStamp();
                    _ = RefreshSectionOverlaysAsync();
                    if (_rightPaneOpen) RenderRightPane();
                });
                return true;
            }

            // 3. No selection quote: anchor to first cited sentence or the reading sentence where asked
            uint targetSec = fallbackSource?.Section ?? askedSection;
            int targetSent = (int)(fallbackSource?.Sentence ?? (uint)Math.Max(0, askedSentence));
            var all = _library.GetSentences(_bookId, targetSec);
            if (all.Count == 0) return false;
            int clamped = Math.Clamp(targetSent, 0, all.Count - 1);
            int chosen = -1;
            for (int i = clamped; i < all.Count; i++) { if (!string.IsNullOrWhiteSpace(all[i])) { chosen = i; break; } }
            if (chosen < 0)
                for (int i = clamped - 1; i >= 0; i--) { if (!string.IsNullOrWhiteSpace(all[i])) { chosen = i; break; } }
            if (chosen < 0) chosen = clamped;

            string raw = all[chosen];
            string trimmed = raw.Trim();
            if (string.IsNullOrEmpty(trimmed)) return false;
            int offsetStart = raw.IndexOf(trimmed, StringComparison.Ordinal);
            if (offsetStart < 0) offsetStart = 0;
            var start = new TextPoint((uint)chosen, (uint)offsetStart);
            var end = new TextPoint((uint)chosen, (uint)(offsetStart + trimmed.Length));

            var existingSame = marks.FirstOrDefault(m => m.Section == targetSec && m.Start == start && m.End == end);
            if (existingSame is not null)
            {
                string? prevNote = existingSame.Note;
                _library.SetMarkNote(_bookId, existingSame.Id, MergeCompanionNote(prevNote, clean));
                Stamp(existingSame.Id);
                _ = RefreshSectionOverlaysAsync();
                if (_rightPaneOpen) RenderRightPane();
                ShowReaderAlert(toastTitle, "撤销", () =>
                {
                    try { _library.SetMarkNote(_bookId, existingSame.Id, prevNote); } catch { }
                    RestoreStamp();
                    _ = RefreshSectionOverlaysAsync();
                    if (_rightPaneOpen) RenderRightPane();
                });
                return true;
            }

            var added = _library.AddMark(_bookId, targetSec, start, end, trimmed, PreferredHighlightStyle(), clean);
            Stamp(added.Id);
            _ = RefreshSectionOverlaysAsync();
            if (_rightPaneOpen) RenderRightPane();
            ShowReaderAlert(toastTitle, "撤销", () =>
            {
                try { _library.DeleteMark(_bookId, added.Id); } catch { }
                RestoreStamp();
                _ = RefreshSectionOverlaysAsync();
                if (_rightPaneOpen) RenderRightPane();
            });
            return true;
        }
        catch (Exception ex)
        {
            ShowReaderAlert($"保存想法失败：{ex.Message}");
            return false;
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
            "广播剧模式",
            "按人物自动分声，带语气与场景音效，点了就听",
            _dramaModeOn,
            on =>
            {
                _dramaModeOn = on;
                try { _library.Core.DramaSetEnabled(_bookId, on); } catch { }
                DramaSynthService.SetDramaMode(_library, _bookId, on);
                _audio.SetSectionContext(_currentSection, dramaMode: _dramaModeOn);
                RefreshCastAndAudio();
            }));

        topCardStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });

        topCardStack.Children.Add(MakeToggleRow(
            "对话气泡",
            "将人物台词排成聊天气泡，点气泡可改认说话人",
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
        narMeta.Children.Add(new TextBlock { Text = L10n.T("旁白"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        narMeta.Children.Add(L10n.Skip(new TextBlock { Text = $"{_audio.VoiceName} · {_audio.Rate:0.##}×", FontSize = 11, Foreground = ThemeHelper.Brush(p.T2), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }));
        var pickNarBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 11,
            Content = L10n.T("换声音"),
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

        stack.Children.Add(MakeCastAction("cast", "待确认的台词", "逐句确认未写明说话人的台词", true, async () =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await VoiceCloneAndDramaDialogs.ShowUnsureSpeakerReviewAsync(xr, _library, _bookId, _currentSection, RefreshCastAndAudio);
        }));
        stack.Children.Add(MakeCastAction("people", "是谁说的", "搜索并查看全书已识别的所有人物", false, async () =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await VoiceCloneAndDramaDialogs.ShowPeoplePickerDialogAsync(xr, _library, _bookId, _currentSection, "是谁说的");
        }));
        stack.Children.Add(MakeCastAction("mic", "克隆一个声音", "录 10 秒自己的声音 · 不联网、不花钱", false, async () =>
        {
            if (RootGrid.XamlRoot is { } xr)
                await VoiceCloneAndDramaDialogs.ShowCloneVoiceDialogAsync(_ownerWindow, xr, _library, forBookId: _bookId);
        }));
        stack.Children.Add(MakeCastAction("pip", "迷你听书", "缩小为置顶浮窗，边办公边听书", false, OpenMiniPlayer));
        stack.Children.Add(MakeCastAction("headphones", "声音来源设置", "管理离线声音、云端声音与音效库", false, () => _ownerWindow.ShowSettings("listening")));

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

                bool canAct = dramaOn && DramaSynthService.CanAct(_library, bookLang, bookId: _bookId);
                bool preRunning = dramaOn && DramaSynthService.IsPreRendering(_bookId);
                bool isPreparing = dramaOn && DramaSynthService.IsPreparing(_library, _bookId);
                DramaLayer secLayer = dramaOn ? DramaSynthService.GetSectionLayer(_library, _bookId, targetSection) : DramaLayer.Live;
                string? borrowedFrom = dramaOn ? DramaSynthService.GetBorrowedFrom(_library, _bookId) : null;
                int sectionSpeakerCount = DramaSynthService.GetSectionRoles(_library, _bookId, targetSection)
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                return (cast, roles, unsureLines, suggestions, prog, sound, made, autoMap, allVoices, secLines, secKnown, worker, canAct, preRunning, isPreparing, secLayer, borrowedFrom, sectionSpeakerCount);
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
                ? L10n.T("正在认全书…")
                : (!string.IsNullOrWhiteSpace(lastErr) ? L10n.T("停下了") : L10n.T($"本章台词 · {snap.secKnown} / {snap.secLines}"));
            string subLine = running && snap.prog is { } pr
                ? L10n.T($"第 {pr.SectionsDone} / {Math.Max(1u, pr.Sections)} 章")
                : (!string.IsNullOrWhiteSpace(lastErr)
                    ? lastErr!
                    : (snap.unsureLines.Length > 0
                        ? L10n.T($"截至当前共有 {snap.unsureLines.Length} 句待确认")
                        : (missing > 0 ? L10n.T($"余 {missing} 句未写明说话人") : L10n.T("读到这里的台词都认好了"))));

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
                Content = L10n.T(running ? "暂停" : (_library.Companion.IsConfigured ? "开始认全书" : "去设置")),
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
                Text = $"{sg.A} {L10n.T("和")} {sg.B} {L10n.T("可能是同一个人")}",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T1),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            });
            var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            var mergeBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Height = 24, MinHeight = 24, Padding = new Thickness(8, 0, 8, 0), FontSize = 10.5, Content = L10n.T("合成一个") };
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

            AddRoleGroup("这一章", snap.cast.Where(m => m.LinesHere > 0).ToArray());
            AddRoleGroup("前面出场过", snap.cast.Where(m => m.LinesHere == 0).Take(12).ToArray());
        }

        // D. Drama Sound Effects & Background Pre-Render Studio Card (when drama mode is ON)
        if (_dramaModeOn)
        {
            var studioStack = new StackPanel { Spacing = 8 };
            if (snap.sound is { } initDs)
            {
                var ds = initDs;
                studioStack.Children.Add(makeToggleRow(
                    "带语气演绎",
                    snap.canAct ? "按原文语境表现笑、叹、低语等语气" : "需使用支持情感的云端音色或增强本地音色",
                    ds.Acting,
                    v => { try { ds = ds with { Acting = v }; _library.Core.DramaSetSound(_bookId, ds); _audio.InvalidateRolesAndSpeakers(); } catch { } },
                    snap.canAct));
                studioStack.Children.Add(makeToggleRow(
                    "环境音",
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
                    "音效",
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
                ? (snap.borrowedFrom is not null
                    ? L10n.T($"已完成 {md.Made} / {md.Sections} 章 · 来自「{snap.borrowedFrom}」")
                    : L10n.T($"已完成 {md.Made} / {md.Sections} 章"))
                : L10n.T("不开启也可即时听；开启后提前做好全书");
            _ = snap.secLayer;
            _ = snap.sectionSpeakerCount;
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
                    ? L10n.T($"导出成有声书（{m2.Made} 章）…")
                    : L10n.T("导出成有声书…"),
            };
            exportBtn.Click += async (_, _) =>
            {
                string? path = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, $"{_detail.Summary.Title}", [("M4B 有声书", [".m4b"])]);
                if (string.IsNullOrEmpty(path) || _disposed) return;
                exportBtn.IsEnabled = false;
                exportBtn.Content = L10n.T("正在混音和压缩，一章要几十秒…");
                bool ok = await DramaSynthService.ExportM4bAsync(_library, _bookId, path, (cur, tot) =>
                    DispatcherQueue.TryEnqueue(() => { if (!_disposed) exportBtn.Content = L10n.T($"正在导出（{cur}/{tot}）…"); }));
                if (_disposed) return;
                exportBtn.IsEnabled = true;
                ShowReaderAlert(ok ? "导出好了" : "当前还没有可导出的章节，请先开启广播剧模式或预合成");
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
