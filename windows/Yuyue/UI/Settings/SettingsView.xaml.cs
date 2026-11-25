using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.UI.Controls;
using Yuyue.UI.Shell;

namespace Yuyue.UI.Settings;

/// <summary>
/// Brand-new native Windows 11 in-window Settings view embedded cleanly inside <see cref="MainWindow"/>.
/// Features Global Settings Search, 11 Fluent 2 categories with 3px accent indicator,
/// native <see cref="FluentSettingsCard"/> / <see cref="FluentSettingsExpander"/> rows,
/// and a real-time Paper &amp; Typography Live Inspector.
/// </summary>
public sealed partial class SettingsView : UserControl, ITabContent
{
    public string TabId => "settings";
    public string TabKind => "settings";
    public string? BookId => null;
    public string TabTitle => L10n.T("设置");
    public string TabIconName => "gear";
    public bool IsPinned => false;
    public bool IsAudioPlaying => false;
    public PaperChromeTheme? ActivePaperChrome => null;
    public UIElement ViewElement => this;
    public event Action? TabMetaChanged;

    public void OnTabActivated() => ApplyAppearance();
    public void OnTabDeactivated() { }
    public void OnAppearanceChanged()
    {
        ApplyAppearance();
        TabMetaChanged?.Invoke();
    }
    public void OnReaderSettingsChanged() { if (!_suppressSelfRefresh) RenderActivePage(); }
    public void OnVoiceChanged() { if (!_suppressSelfRefresh) RenderActivePage(); }
    public void OnForeignPrefsChanged() { if (!_suppressSelfRefresh) RenderActivePage(); }
    public void OnSnapshotRestored() => ApplyAppearance();
    public void SetFullScreenChrome(bool fullScreen) { }
    public void SetOwnerWindow(MainWindow owner)
    {
        _ownerWindow = owner;
        _onClose = () => _ownerWindow.CloseSettings();
    }
    public bool HandleWindowKeyDown(Windows.System.VirtualKey key, bool ctrl, bool shift, bool alt) => false;
    public bool HandleMouseBackForward(bool isBack) { if (!isBack) return false; _onClose(); return true; }
    public Task<bool> PrepareCloseAsync() => Task.FromResult(true);
    public Task<bool> SaveCompositeScreenshotAsync(string path) => Task.FromResult(false);
    public SavedTabEntry? SerializeTabState() => null;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _library.Kokoro.StateChanged -= OnVoiceModelStateChanged;
        _library.LiveVoice.StateChanged -= OnVoiceModelStateChanged;
        _library.Fonts.Changed -= OnFontShelfChanged;
        try { _gdriveAuthCts?.Cancel(); _gdriveAuthCts?.Dispose(); } catch { }
        _gdriveAuthCts = null;
        try { _openRouterAuthCts?.Cancel(); _openRouterAuthCts?.Dispose(); } catch { }
        _openRouterAuthCts = null;
    }

    private MainWindow _ownerWindow;
    private readonly LibraryService _library;
    private Action _onClose;

    private string _activePage = "general";
    private string _searchQuery = "";
    private bool _suppressSelfRefresh;
    private bool _disposed;
    private CancellationTokenSource? _gdriveAuthCts;
    private CancellationTokenSource? _openRouterAuthCts;

    private FluentSettingsCard? _kokoroCard;
    private Button? _kokoroBtn;
    private Button? _kokoroCancelBtn;
    private FluentSettingsCard? _liveVoiceCard;
    private Button? _liveVoiceBtn;
    private Button? _liveVoiceCancelBtn;
    private FluentSettingsCard? _storageModelsCard;

    private static readonly (string? Group, string Id, string Title, string Subtitle, string Icon)[] NavSpec =
    [
        (null, "general", "通用与操控", "窗口标签、鼠键习惯、文件关联与启动方式", "sliders"),
        (null, "appearance", "外观与材质", "深浅模式、Warm Mica 云母、主题强调色与图标", "sun"),
        ("阅读与朗读", "reading", "阅读排版", "阅读纸张、字体字号、行距、分页与双栏默认项", "book-open"),
        (null, "foreign", "外文与词典", "查词发音、难词行间释义与离线 MDict 词典", "char-book"),
        (null, "listening", "朗读与音色", "离线 Kokoro / Edge / 克隆音色与倍速偏好", "headphones"),
        ("连接", "companion", "AI 书伴", "18 家大模型接入、防剧透书伴与用量统计", "bubble-lines"),
        (null, "sync", "多端同步", "WebDAV、本地同步目录、Google Drive 与冲突处理", "sync-arrows"),
        (null, "export", "笔记导出", "一键或自动导出至 Obsidian、Notion、flomo 与新枝", "share-up"),
        ("数据与关于", "storage", "本地存储", "书库位置、磁盘占用与离线模型管理", "drive"),
        (null, "data", "备份与数据", ".yuyue 完整快照备份恢复与设置变更历史撤销", "cube"),
        (null, "about", "关于语阅", "版本信息、检查更新、反馈与隐私说明", "info-circle"),
    ];

    public SettingsView(MainWindow ownerWindow, LibraryService library, string initialPage, Action onClose)
    {
        _ownerWindow = ownerWindow;
        _library = library;
        _onClose = onClose;
        _activePage = string.IsNullOrWhiteSpace(initialPage) ? "general" : initialPage;

        InitializeComponent();
        _library.Kokoro.StateChanged += OnVoiceModelStateChanged;
        _library.LiveVoice.StateChanged += OnVoiceModelStateChanged;
        _library.Fonts.Changed += OnFontShelfChanged;
        ApplyAppearance();
    }

    private void OnFontShelfChanged()
    {
        if (_disposed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_disposed || _activePage != "reading") return;
            RenderActivePage();
        });
    }

    private void BroadcastReaderSettingsWithoutSelfRefresh()
    {
        _suppressSelfRefresh = true;
        try { App.CurrentApp.BroadcastReaderSettingsChanged(); }
        finally { _suppressSelfRefresh = false; }
    }

    private void BroadcastVoiceWithoutSelfRefresh()
    {
        _suppressSelfRefresh = true;
        try { App.CurrentApp.BroadcastVoiceChanged(); }
        finally { _suppressSelfRefresh = false; }
    }

    private void BroadcastForeignPrefsWithoutSelfRefresh()
    {
        _suppressSelfRefresh = true;
        try { App.CurrentApp.BroadcastForeignPrefsChanged(); }
        finally { _suppressSelfRefresh = false; }
    }

    private void OnVoiceModelStateChanged()
    {
        if (_disposed) return;
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_disposed) return;
            UpdateVoiceModelCards();
        });
    }

    public void NavigateTo(string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) pageId = "general";
        _activePage = pageId;
        _searchQuery = "";
        GlobalSearchBox.Text = "";
        RebuildNav();
        RenderActivePage();
    }

    public void ApplyAppearance()
    {
        RebuildNav();
        RenderActivePage();
        L10n.ApplyTree(RootGrid);
    }

    private void RebuildNav()
    {
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        Windows.UI.Color selBg = dark
            ? Windows.UI.Color.FromArgb(24, 255, 255, 255)
            : Windows.UI.Color.FromArgb(16, 42, 43, 46);

        NavStack.Children.Clear();
        foreach (var (group, id, title, _, icon) in NavSpec)
        {
            if (group is not null)
            {
                NavStack.Children.Add(new TextBlock
                {
                    Text = L10n.T(group),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(10, 12, 8, 4),
                });
            }

            bool active = string.IsNullOrEmpty(_searchQuery) && _activePage == id;
            var ind = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-8, 0, 0, 0),
                Background = ThemeHelper.Brush(tc.Accent),
                Opacity = active ? 1.0 : 0.0,
            };
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(YYIcons.Make(icon, 14.5, ThemeHelper.Brush(active ? tc.Accent : p.T2)));
            var lbl = new TextBlock
            {
                Text = L10n.T(title),
                FontSize = 13,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ThemeHelper.Brush(active ? p.T1 : p.T2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lbl, 1);
            row.Children.Add(lbl);

            var host = new Grid();
            host.Children.Add(ind);
            host.Children.Add(row);

            string capId = id;
            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["YYSidebarItemStyle"],
                Background = active ? ThemeHelper.Brush(selBg) : new SolidColorBrush(Colors.Transparent),
                Padding = new Thickness(10, 0, 10, 0),
                Content = host,
            };
            btn.Click += (_, _) => NavigateTo(capId);
            NavStack.Children.Add(btn);
        }
    }

    private void RenderActivePage()
    {
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        _kokoroCard = null;
        _kokoroBtn = null;
        _kokoroCancelBtn = null;
        _liveVoiceCard = null;
        _liveVoiceBtn = null;
        _liveVoiceCancelBtn = null;
        _storageModelsCard = null;
        PageBodyStack.Children.Clear();

        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            PageTitleText.Text = L10n.T($"搜索设置：“{_searchQuery.Trim()}”");
            PageSubtitleText.Text = L10n.T("点击任意匹配项直接跳转到对应分类");
            SetInspectorVisible(false);
            RenderSearchResults(dark);
            return;
        }

        var meta = NavSpec.FirstOrDefault(x => x.Id == _activePage);
        PageTitleText.Text = L10n.T(meta.Title ?? "设置");
        PageSubtitleText.Text = L10n.T(meta.Subtitle ?? "");

        bool showPreview = _activePage is "appearance" or "reading";
        SetInspectorVisible(showPreview);
        if (showPreview) RenderPaperLivePreview(dark);

        switch (_activePage)
        {
            case "appearance": BuildAppearancePage(dark); break;
            case "reading": BuildReadingPage(dark); break;
            case "foreign": BuildForeignPage(dark); break;
            case "listening": BuildListeningPage(dark); break;
            case "companion": BuildCompanionPage(dark); break;
            case "sync": BuildSyncPage(dark); break;
            case "export": BuildExportPage(dark); break;
            case "storage": BuildStoragePage(dark); break;
            case "data": BuildDataPage(dark); break;
            case "about": BuildAboutPage(dark); break;
            default: BuildGeneralPage(dark); break;
        }
    }

    private void RenderSearchResults(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var hits = SettingsSearchIndex.Search(_searchQuery);
        if (hits.Count == 0)
        {
            PageBodyStack.Children.Add(new TextBlock
            {
                Text = L10n.T("未找到匹配的设置项，试试搜索：标签、托盘、空格、Mica、快捷键、WebDAV、DeepSeek、Obsidian"),
                FontSize = 13,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
            });
            return;
        }

        foreach (var hit in hits)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = ThemeHelper.Brush(p.Inset),
                Padding = new Thickness(8, 3, 8, 3),
                Child = new TextBlock { Text = L10n.T(hit.CategoryLabel), FontSize = 11, Foreground = ThemeHelper.Brush(p.T2) },
            };
            string targetPage = hit.PageId;
            PageBodyStack.Children.Add(new FluentSettingsCard(
                hit.Title,
                hit.Subtitle,
                "search",
                action: badge,
                onClick: () => NavigateTo(targetPage),
                dark: dark));
        }
    }

    private void SetInspectorVisible(bool visible)
    {
        InspectorBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InspectorColumn.Width = visible ? new GridLength(290) : new GridLength(0);
    }

    private void RenderPaperLivePreview(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var rs = _library.GetReaderSettings();
        var resolved = PaperAppearance.Resolve(_library, rs);
        var paper = YYPapers.FromId(PaperAppearance.PaperCssId(resolved.Preset));
        var paperBg = paper.Bg;
        var paperInk = paper.Ink;
        var paperInk2 = paper.Ink2;
        var paperLine = paper.Line;
        var paperHl = paper.Hl;
        string paperName = paper.Name;
        if (resolved.Custom is { } cp)
        {
            paperName = cp.Name;
            try
            {
                var cols = YuyueCoreMethods.PaperColors(cp);
                paperBg = global::Yuyue.Reader.CustomPaperDialog.ParseHexColor(cols.Bg, paperBg);
                paperInk = global::Yuyue.Reader.CustomPaperDialog.ParseHexColor(cols.Ink, paperInk);
                paperInk2 = global::Yuyue.Reader.CustomPaperDialog.ParseHexColor(cols.Ink2, paperInk2);
                paperLine = global::Yuyue.Reader.CustomPaperDialog.ParseHexColor(cols.Line, paperLine);
                paperHl = global::Yuyue.Reader.CustomPaperDialog.ParseHexColor(cols.Hl, paperHl);
            }
            catch { }
        }

        InspectorStack.Children.Clear();
        InspectorStack.Children.Add(new TextBlock
        {
            Text = L10n.T("实时样张预览"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T2),
        });

        var sampleCard = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(paperBg),
            BorderBrush = ThemeHelper.Brush(paperLine),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 18, 18, 20),
        };
        var bodyFont = new FontFamily((rs.Font == Font.Sans ? YYFontFamily.Sans : rs.Font == Font.Kai ? YYFontFamily.Kai : YYFontFamily.Serif).WindowsFamily());
        double fs = Math.Clamp(rs.FontSize * 0.82, 12, 20);

        var s = new StackPanel { Spacing = 10 };
        s.Children.Add(new TextBlock
        {
            Text = "第四回 · 薄命女偏逢薄命郎",
            FontFamily = bodyFont,
            FontSize = fs + 2,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(paperInk),
        });
        s.Children.Add(new TextBlock
        {
            Text = "却说黛玉同姊妹们至王夫人处，见王夫人与兄嫂处的来使计议家务，又说姨母家遭人命官司等语。因见王夫人事情冗杂，姊妹们遂出来，至寡嫂李氏房中来了。",
            FontFamily = bodyFont,
            FontSize = fs,
            LineHeight = Math.Round(fs * (rs.LineHeight ?? 1.9)),
            Foreground = ThemeHelper.Brush(paperInk),
            TextWrapping = TextWrapping.Wrap,
        });
        var hlBox = new Border
        {
            Background = ThemeHelper.Brush(paperHl),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 3, 6, 3),
            Child = new TextBlock
            {
                Text = "看 · 听 · 记对着同一句话",
                FontFamily = bodyFont,
                FontSize = fs - 0.5,
                Foreground = ThemeHelper.Brush(paperInk),
            },
        };
        s.Children.Add(hlBox);
        s.Children.Add(new TextBlock
        {
            Text = $"{paperName} · {Math.Round(rs.FontSize)}px · 行距 {(rs.LineHeight ?? 1.9):0.0} · {(_library.Prefs.TwoColumns ? "双栏" : "单栏")}",
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(paperInk2),
        });
        sampleCard.Child = s;
        InspectorStack.Children.Add(sampleCard);

        InspectorStack.Children.Add(new TextBlock
        {
            Text = L10n.T("打开书籍后，顶部标签栏与工具栏会自动染成与上方完全一致的纸色。"),
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private TextBlock MakeSectionHeader(string title, bool dark, bool first = false) => new()
    {
        Text = L10n.T(title),
        FontSize = 12.5,
        FontWeight = FontWeights.SemiBold,
        Foreground = ThemeHelper.Brush(ThemeHelper.Palette(dark).T2),
        Margin = new Thickness(2, first ? 0 : 12, 0, 2),
    };

    private void MainScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = Math.Min(760, Math.Max(360, e.NewSize.Width - 56));
        if (Math.Abs(PageBodyStack.Width - w) > 1)
            PageBodyStack.Width = w;
    }

    private void GlobalSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = GlobalSearchBox.Text ?? "";
        RebuildNav();
        RenderActivePage();
    }
}
