using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Settings;
using Yuyue.Speech;
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

    public void OnTabActivated()
    {
        CaptureSettingsSnapshots();
        ApplyAppearance();
    }
    public void OnTabDeactivated() { }
    public void OnAppearanceChanged()
    {
        ApplyAppearance();
        TabMetaChanged?.Invoke();
    }
    public void OnReaderSettingsChanged() { if (!_suppressSelfRefresh) RenderActivePage(); }
    public void OnVoiceChanged() { if (!_suppressSelfRefresh) RenderActivePage(); }
    public void OnForeignPrefsChanged() { if (!_suppressSelfRefresh) RenderActivePage(); }
    public void OnSnapshotRestored()
    {
        CaptureSettingsSnapshots();
        ApplyAppearance();
    }
    public void SetFullScreenChrome(bool fullScreen) { }
    public void SetOwnerWindow(MainWindow owner)
    {
        _ownerWindow = owner;
        _onClose = () => _ownerWindow.CloseSettings();
    }
    public bool HandleWindowKeyDown(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        if (ctrl && !alt)
        {
            if (!shift && key == VirtualKey.Z)
            {
                PerformSettingsUndo();
                return true;
            }
            if ((shift && key == VirtualKey.Z) || (!shift && key == VirtualKey.Y))
            {
                PerformSettingsRedo();
                return true;
            }
        }
        return false;
    }
    public bool HandleMouseBackForward(bool isBack) { if (!isBack) return false; _onClose(); return true; }
    public Task<bool> PrepareCloseAsync() => Task.FromResult(true);
    public Task<bool> SaveCompositeScreenshotAsync(string path) => Task.FromResult(false);
    public SavedTabEntry? SerializeTabState() => null;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _savedPillTimer?.Stop();
        _library.PrefsSaved -= OnLibraryPrefsSaved;
        _library.ReaderSettingsSaved -= OnLibraryReaderSettingsSaved;
        _library.Kokoro.StateChanged -= OnVoiceModelStateChanged;
        _library.LiveVoice.StateChanged -= OnVoiceModelStateChanged;
        _library.Fonts.Changed -= OnFontShelfChanged;
        try { _gdriveAuthCts?.Cancel(); _gdriveAuthCts?.Dispose(); } catch { }
        _gdriveAuthCts = null;
        try { _openRouterAuthCts?.Cancel(); _openRouterAuthCts?.Dispose(); } catch { }
        _openRouterAuthCts = null;
        try { Speech.AudioPlayer.StopActivePreview(); } catch { }
    }

    private MainWindow _ownerWindow;
    private readonly LibraryService _library;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _uiQueue;
    private Action _onClose;

    private string _activePage = "general";
    private string _searchQuery = "";
    private bool _suppressSelfRefresh;
    private bool _suppressHistoryCapture;
    private bool _disposed;
    private DispatcherTimer? _savedPillTimer;
    private readonly Dictionary<string, string> _prefsPropSnapshot = new(StringComparer.Ordinal);
    private ReaderSettings? _readerSettingsSnapshot;
    private string _readerSettingsJsonSnapshot = "";
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
        (null, "general", "通用", "语言、启动行为与导入方式", "sliders"),
        (null, "appearance", "外观", "应用界面的外观与配色；书页纸色在「阅读」中设置", "sun"),
        ("阅读与朗读", "reading", "阅读", "新书的默认排版；单本书可在阅读页 Aa 中单独调整", "book-open"),
        (null, "foreign", "外文", "查词、难词注释、生词与离线翻译", "char-book"),
        (null, "listening", "朗读", "朗读、查词发音与播客音色", "headphones"),
        ("连接", "companion", "书伴", "阅读问答与播客撰稿所用的大模型", "bubble-lines"),
        (null, "sync", "同步", "通过个人云盘在多台设备间同步", "sync-arrows"),
        (null, "export", "笔记导出", "将划线与想法发送到常用笔记软件", "share-up"),
        ("数据与关于", "storage", "存储", "书库、有声书与本地缓存占用的空间", "drive"),
        (null, "data", "数据", "本地备份快照与阅读记录导出", "cube"),
        (null, "about", "关于", "版本、更新和作者", "info-circle"),
    ];

    public SettingsView(MainWindow ownerWindow, LibraryService library, string initialPage, Action onClose)
    {
        _ownerWindow = ownerWindow;
        _library = library;
        _onClose = onClose;
        _activePage = string.IsNullOrWhiteSpace(initialPage) ? "general" : initialPage;

        InitializeComponent();
        YYChrome.WireClearButton(GlobalSearchBox);
        _uiQueue = DispatcherQueue;
        CaptureSettingsSnapshots();
        _library.PrefsSaved += OnLibraryPrefsSaved;
        _library.ReaderSettingsSaved += OnLibraryReaderSettingsSaved;
        _library.Kokoro.StateChanged += OnVoiceModelStateChanged;
        _library.LiveVoice.StateChanged += OnVoiceModelStateChanged;
        _library.Fonts.Changed += OnFontShelfChanged;
        ApplyAppearance();
    }

    private void OnFontShelfChanged()
    {
        if (_disposed) return;
        _uiQueue.TryEnqueue(() =>
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
        DramaSynthService.InvalidateAllCasts();
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
        _uiQueue.TryEnqueue(() =>
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

#if DEBUG
    public void OpenDebugDialog(string dialogId)
    {
        if (_disposed || string.IsNullOrWhiteSpace(dialogId)) return;
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        _uiQueue.TryEnqueue(async () =>
        {
            await Task.Delay(320);
            if (_disposed) return;
            if (string.Equals(dialogId, "companion-catalog", StringComparison.OrdinalIgnoreCase))
                await ShowCompanionProviderCatalogDialogAsync(dark);
            else if (string.Equals(dialogId, "voice-catalog", StringComparison.OrdinalIgnoreCase))
                await ShowVoiceSourceCatalogDialogAsync(dark);
            else if (string.Equals(dialogId, "voice-picker", StringComparison.OrdinalIgnoreCase) && RootGrid.XamlRoot is { } xr)
                await Yuyue.UI.Reader.ReaderPaneView.ShowVoicePickerDialogAsync(
                    xr,
                    _library,
                    L10n.T("中文书声音"),
                    _library.Prefs.VoiceId,
                    _ => { },
                    darkOverride: dark,
                    languageFilter: "zh");
        });
    }
#endif

    public void ApplyAppearance()
    {
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        SavedStatusIcon.Content = YYIcons.Make("check-circle", 12.5, ThemeHelper.Brush(p.Ok));
        HistoryQuickIcon.Content = YYIcons.Make("history", 12.5, ThemeHelper.Brush(p.T2));
        UpdateHistoryQuickButton();
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

        bool showPreview = _activePage is "reading";
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
            Text = L10n.T("样张"),
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
        string fallbackFam = (rs.Font == Font.Sans ? YYFontFamily.Sans : rs.Font == Font.Kai ? YYFontFamily.Kai : YYFontFamily.Serif).WindowsFamily();
        string? faceSpec = !string.IsNullOrWhiteSpace(rs.FontFace)
            ? rs.FontFace
            : (rs.Font == Font.WenKai ? "wenkai" : null);
        string resolvedFontSrc = !string.IsNullOrEmpty(faceSpec)
            ? _library.Fonts.ResolveXamlFontSource(faceSpec, fallbackFam)
            : fallbackFam;
        var bodyFont = new FontFamily(resolvedFontSrc);
        string activeFontName = ResolveActiveFontDisplayName(rs);
        double fs = Math.Clamp(rs.FontSize * 0.82, 12, 20);

        var s = new StackPanel { Spacing = 10 };
        s.Children.Add(new TextBlock
        {
            Text = "故乡",
            FontFamily = bodyFont,
            FontSize = fs + 2,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(paperInk),
        });
        s.Children.Add(new TextBlock
        {
            Text = "我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。时候既然是深冬；渐近故乡时，天气又阴晦了，冷风吹进船舱中，呜呜的响，从篷隙向外一望，苍黄的天底下，远近横着几个萧索的荒村，没有一些活气。",
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
                Text = "希望本是无所谓有，无所谓无的。",
                FontFamily = bodyFont,
                FontSize = fs - 0.5,
                Foreground = ThemeHelper.Brush(paperInk),
            },
        };
        s.Children.Add(hlBox);
        s.Children.Add(new TextBlock
        {
            Text = $"{paperName} · {activeFontName} · {Math.Round(rs.FontSize)} · 行距 {(rs.LineHeight ?? 1.9):0.0} · {(_library.Prefs.TwoColumns ? "双栏" : "单栏")}",
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(paperInk2),
            TextWrapping = TextWrapping.Wrap,
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

    private string ResolveActiveFontDisplayName(ReaderSettings rs)
    {
        if (!string.IsNullOrWhiteSpace(rs.FontFace))
        {
            string face = rs.FontFace.Trim();
            if (face.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
                return face[6..].Trim();
            if (face.StartsWith("sys:", StringComparison.OrdinalIgnoreCase))
                return face[4..].Trim();
            if (face.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return _library.Fonts.GetFontFileDisplayName(face[5..].Trim());
            var pack = _library.Fonts.Catalog.FirstOrDefault(pk => string.Equals(pk.Id, face, StringComparison.OrdinalIgnoreCase));
            if (pack is not null) return pack.Name;
            return face;
        }
        if (rs.Font == Font.WenKai)
        {
            return _library.Fonts.Catalog.FirstOrDefault(pk => string.Equals(pk.Id, "wenkai", StringComparison.OrdinalIgnoreCase))?.Name
                ?? "霞鹜文楷";
        }
        return rs.Font switch
        {
            Font.Sans => L10n.T("黑体"),
            Font.Serif => L10n.T("宋体"),
            Font.Kai => L10n.T("楷体"),
            _ => L10n.T("系统"),
        };
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

    // ---------- Settings history, automatic change capture, Ctrl+Z undo & "已保存" toast ----------
    private static readonly Dictionary<string, string> TrackedPrefLabels = new(StringComparer.Ordinal)
    {
        [nameof(DevicePrefs.OpenBookInTab)] = "默认新标签打开书籍",
        [nameof(DevicePrefs.RestoreLastTabs)] = "启动时恢复上次标签页",
        [nameof(DevicePrefs.CloseWindowBehavior)] = "关闭窗口行为",
        [nameof(DevicePrefs.ShelfSingleClickOpen)] = "书库封面点击习惯",
        [nameof(DevicePrefs.ReaderSpaceAction)] = "阅读器空格键行为",
        [nameof(DevicePrefs.AutoLaunchOnLogin)] = "开机静默启动",
        [nameof(DevicePrefs.KeepAwakeWhileListening)] = "朗读时防止休眠",
        [nameof(DevicePrefs.UiLanguage)] = "界面语言",
        [nameof(DevicePrefs.WatchedFolder)] = "监听文件夹",
        [nameof(DevicePrefs.ThemeMode)] = "外观主题",
        [nameof(DevicePrefs.UseWarmMica)] = "Warm Mica 材质",
        [nameof(DevicePrefs.TintId)] = "主题色",
        [nameof(DevicePrefs.AppIconId)] = "应用图标",
        [nameof(DevicePrefs.GlobalEinkMode)] = "全局墨水屏模式",
        [nameof(DevicePrefs.PaperFollowAppearance)] = "纸色跟随外观",
        [nameof(DevicePrefs.TwoColumns)] = "宽窗口双栏显示",
        [nameof(DevicePrefs.ImmersiveNoAnimation)] = "关闭翻页动画",
        [nameof(DevicePrefs.PageTurnSound)] = "翻页音效",
        [nameof(DevicePrefs.PageTurnSoundStyle)] = "翻页音色",
        [nameof(DevicePrefs.PageTurnSoundVolume)] = "翻页音效音量",
        [nameof(DevicePrefs.ForeignLookup)] = "点词查词",
        [nameof(DevicePrefs.ForeignSpeakOnLookup)] = "查词自动发音",
        [nameof(DevicePrefs.ForeignAccent)] = "英语发音口音",
        [nameof(DevicePrefs.ForeignMarkVocab)] = "在书中标记生词",
        [nameof(DevicePrefs.ForeignGlossEnabled)] = "难词行间释义",
        [nameof(DevicePrefs.GlossLevel)] = "词汇难度",
        [nameof(DevicePrefs.GlossVocabOnly)] = "仅标注生词本",
        [nameof(DevicePrefs.VoiceId)] = "中文朗读声音",
        [nameof(DevicePrefs.VoiceIdEn)] = "英文朗读声音",
        [nameof(DevicePrefs.SpeechRate)] = "朗读语速",
        [nameof(DevicePrefs.AiProvider)] = "书伴服务商",
        [nameof(DevicePrefs.AiModel)] = "书伴模型",
        [nameof(DevicePrefs.AiBaseUrl)] = "书伴接口地址",
        [nameof(DevicePrefs.AiTierRoutingEnabled)] = "批量任务用轻量模型",
        [nameof(DevicePrefs.AiDailyLlmTokenCap)] = "每日用量上限",
        [nameof(DevicePrefs.CompanionQuickPrompts)] = "书伴常用提问",
        [nameof(DevicePrefs.SyncMethod)] = "同步方式",
        [nameof(DevicePrefs.WebDavUrl)] = "WebDAV 地址",
        [nameof(DevicePrefs.WebDavUser)] = "WebDAV 账号",
        [nameof(DevicePrefs.SyncLocalFolder)] = "同步文件夹",
        [nameof(DevicePrefs.SyncReading)] = "同步阅读进度",
        [nameof(DevicePrefs.SyncNotes)] = "同步笔记与生词",
        [nameof(DevicePrefs.SyncSettings)] = "同步设置与规则",
        [nameof(DevicePrefs.SyncFiles)] = "同步书文件",
        [nameof(DevicePrefs.SyncRecordings)] = "同步朗读录音",
        [nameof(DevicePrefs.ObsidianExportOn)] = "Obsidian 导出",
        [nameof(DevicePrefs.ObsidianFolder)] = "Obsidian 目录",
        [nameof(DevicePrefs.NotionExportOn)] = "Notion 导出",
        [nameof(DevicePrefs.FlomoExportOn)] = "flomo 导出",
        [nameof(DevicePrefs.XinzhiExportOn)] = "新枝导出",
        [nameof(DevicePrefs.AnalyticsEnabled)] = "匿名使用统计",
    };

    private void CaptureSettingsSnapshots()
    {
        _prefsPropSnapshot.Clear();
        var prefsType = typeof(DevicePrefs);
        foreach (var propName in TrackedPrefLabels.Keys)
        {
            var pi = prefsType.GetProperty(propName);
            if (pi is null) continue;
            try { _prefsPropSnapshot[propName] = JsonSerializer.Serialize(pi.GetValue(_library.Prefs), pi.PropertyType); }
            catch { }
        }
        try
        {
            var rs = _library.GetReaderSettings();
            _readerSettingsSnapshot = rs;
            _readerSettingsJsonSnapshot = JsonSerializer.Serialize(rs);
        }
        catch { }
    }

    private static string FormatPrefDisplayValue(string propName, object? val)
    {
        if (val is bool b) return L10n.T(b ? "开" : "关");
        if (val is double d) return propName == nameof(DevicePrefs.SpeechRate) ? $"{d:0.##}×" : $"{d:0.##}";
        if (val is byte by && propName == nameof(DevicePrefs.GlossLevel))
            return by switch { 2 => L10n.T("四级"), 3 => L10n.T("六级"), 4 => L10n.T("考研"), 5 => L10n.T("雅思"), 6 => "GRE", _ => by.ToString() };
        if (val is List<CompanionPromptItem> list) return L10n.T($"{list.Count} 条");
        if (val is null && propName == nameof(DevicePrefs.CompanionQuickPrompts)) return L10n.T("默认");
        string s = val?.ToString() ?? "";
        return propName switch
        {
            nameof(DevicePrefs.ThemeMode) => s switch { "light" => L10n.T("浅色"), "dark" => L10n.T("深色"), _ => L10n.T("跟随系统") },
            nameof(DevicePrefs.AppIconId) => L10n.T(ThemeHelper.GetAppIconTitle(s)),
            nameof(DevicePrefs.ForeignAccent) => s == "us" ? L10n.T("美音 (US)") : L10n.T("英音 (UK)"),
            nameof(DevicePrefs.UiLanguage) => s switch { "zh-Hans" => "中文", "en" => "English", _ => L10n.T("跟随系统") },
            nameof(DevicePrefs.CloseWindowBehavior) => s switch { "always_tray" => L10n.T("最小化到托盘"), "exit" => L10n.T("直接退出"), _ => L10n.T("听书时到托盘") },
            nameof(DevicePrefs.ReaderSpaceAction) => s == "listen" ? L10n.T("朗读/暂停") : L10n.T("智能空格"),
            nameof(DevicePrefs.SyncMethod) => s switch { "webdav" => "WebDAV", "folder" => L10n.T("同步文件夹"), "gdrive" => "Google Drive", _ => L10n.T("关闭") },
            nameof(DevicePrefs.AiDailyLlmTokenCap) => s == "0" ? L10n.T("不限") : s,
            _ => string.IsNullOrWhiteSpace(s) ? L10n.T("空") : (s.Length > 24 ? s[..24] + "…" : s),
        };
    }

    private void OnLibraryPrefsSaved()
    {
        if (_disposed || _suppressHistoryCapture || !_uiQueue.HasThreadAccess) return;
        if (!ReferenceEquals(_ownerWindow.ActiveTab, this))
        {
            CaptureSettingsSnapshots();
            return;
        }
        var prefsType = typeof(DevicePrefs);
        bool anyRecorded = false;
        foreach (var (propName, label) in TrackedPrefLabels)
        {
            var pi = prefsType.GetProperty(propName);
            if (pi is null) continue;
            string newJson;
            object? newVal;
            try
            {
                newVal = pi.GetValue(_library.Prefs);
                newJson = JsonSerializer.Serialize(newVal, pi.PropertyType);
            }
            catch { continue; }

            if (_prefsPropSnapshot.TryGetValue(propName, out var oldJson) && string.Equals(oldJson, newJson, StringComparison.Ordinal))
                continue;

            object? oldVal = null;
            if (!string.IsNullOrEmpty(oldJson))
            {
                try { oldVal = JsonSerializer.Deserialize(oldJson, pi.PropertyType); } catch { }
            }
            _prefsPropSnapshot[propName] = newJson;

            _library.SettingsHistory.Record(
                _activePage,
                label,
                FormatPrefDisplayValue(propName, newVal),
                FormatPrefDisplayValue(propName, oldVal),
                prefKey: $"prefs:{propName}",
                oldRawJson: oldJson,
                newRawJson: newJson);
            anyRecorded = true;
        }
        if (anyRecorded)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_disposed) return;
                UpdateHistoryQuickButton();
                ShowSettingsSavedToast("已保存");
            });
        }
    }

    private void OnLibraryReaderSettingsSaved()
    {
        if (_disposed || _suppressHistoryCapture || !_uiQueue.HasThreadAccess) return;
        if (!ReferenceEquals(_ownerWindow.ActiveTab, this))
        {
            CaptureSettingsSnapshots();
            return;
        }
        try
        {
            var cur = _library.GetReaderSettings();
            string newJson = JsonSerializer.Serialize(cur);
            if (string.Equals(_readerSettingsJsonSnapshot, newJson, StringComparison.Ordinal)) return;
            var prev = _readerSettingsSnapshot;
            string oldJson = _readerSettingsJsonSnapshot;
            _readerSettingsSnapshot = cur;
            _readerSettingsJsonSnapshot = newJson;

            string title = "阅读排版";
            string oldDisp = "";
            string newDisp = "";
            if (prev is not null)
            {
                if (prev.Paper != cur.Paper || prev.CustomPaper != cur.CustomPaper)
                {
                    title = "默认纸色";
                    oldDisp = prev.CustomPaper ?? prev.Paper.ToString();
                    newDisp = cur.CustomPaper ?? cur.Paper.ToString();
                }
                else if (Math.Abs(prev.FontSize - cur.FontSize) > 0.01)
                {
                    title = "默认字号";
                    oldDisp = $"{Math.Round(prev.FontSize)}px";
                    newDisp = $"{Math.Round(cur.FontSize)}px";
                }
                else if (prev.LineHeight != cur.LineHeight)
                {
                    title = "默认行距";
                    oldDisp = $"{(prev.LineHeight ?? 1.9):0.0}";
                    newDisp = $"{(cur.LineHeight ?? 1.9):0.0}";
                }
                else if (prev.Font != cur.Font || prev.FontFace != cur.FontFace)
                {
                    title = "默认字体";
                    oldDisp = prev.FontFace ?? prev.Font?.ToString() ?? L10n.T("系统");
                    newDisp = cur.FontFace ?? cur.Font?.ToString() ?? L10n.T("系统");
                }
                else if (prev.Mode != cur.Mode)
                {
                    title = "翻页方式";
                    oldDisp = L10n.T(prev.Mode == LayoutMode.Scrolled ? "上下滚动" : "左右翻页");
                    newDisp = L10n.T(cur.Mode == LayoutMode.Scrolled ? "上下滚动" : "左右翻页");
                }
                else if (prev.ParagraphSpacing != cur.ParagraphSpacing)
                {
                    title = "段距";
                    oldDisp = $"{(prev.ParagraphSpacing ?? 0.5):0.0}";
                    newDisp = $"{(cur.ParagraphSpacing ?? 0.5):0.0}";
                }
                else if (prev.Indent != cur.Indent)
                {
                    title = "首行缩进";
                    oldDisp = prev.Indent is null ? L10n.T("跟书") : L10n.T(prev.Indent.Value ? "缩进" : "不缩");
                    newDisp = cur.Indent is null ? L10n.T("跟书") : L10n.T(cur.Indent.Value ? "缩进" : "不缩");
                }
                else if (prev.Justify != cur.Justify)
                {
                    title = "两端对齐";
                    oldDisp = prev.Justify is null ? L10n.T("跟书") : L10n.T(prev.Justify.Value ? "对齐" : "靠左");
                    newDisp = cur.Justify is null ? L10n.T("跟书") : L10n.T(cur.Justify.Value ? "对齐" : "靠左");
                }
                else if (prev.Compress != cur.Compress)
                {
                    title = "中文标点挤压";
                    oldDisp = L10n.T(prev.Compress ? "开" : "关");
                    newDisp = L10n.T(cur.Compress ? "开" : "关");
                }
            }

            _library.SettingsHistory.Record(
                _activePage,
                title,
                newDisp,
                oldDisp,
                prefKey: "reader:settings",
                oldRawJson: string.IsNullOrEmpty(oldJson) ? null : oldJson,
                newRawJson: newJson);

            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_disposed) return;
                UpdateHistoryQuickButton();
                ShowSettingsSavedToast("已保存");
            });
        }
        catch { }
    }

    private bool CanUndoSettings() =>
        _library.SettingsHistory.Entries.Any(e => !e.Undone && _library.SettingsHistory.CanActuate(e, applyUndo: true));

    private void UpdateHistoryQuickButton()
    {
        if (HistoryQuickLabel is null) return;
        int n = _library.SettingsHistory.Entries.Count;
        HistoryQuickLabel.Text = n > 0 ? $"{L10n.T("历史")} ({n})" : L10n.T("历史");
    }

    private void ShowSettingsSavedToast(string message = "已保存", bool showUndo = true)
    {
        if (_disposed || SavedStatusPill is null) return;
        SavedStatusText.Text = L10n.T(message);
        SavedUndoQuickBtn.Visibility = (showUndo && CanUndoSettings()) ? Visibility.Visible : Visibility.Collapsed;
        SavedStatusPill.Visibility = Visibility.Visible;
        _savedPillTimer?.Stop();
        _savedPillTimer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(3.2) };
        _savedPillTimer.Tick += (_, _) =>
        {
            _savedPillTimer.Stop();
            if (!_disposed) SavedStatusPill.Visibility = Visibility.Collapsed;
        };
        _savedPillTimer.Start();
    }

    private void ApplyAfterHistoryMutation(string toastMessage)
    {
        CaptureSettingsSnapshots();
        _library.ApplyUiLanguage();
        BroadcastReaderSettingsWithoutSelfRefresh();
        BroadcastVoiceWithoutSelfRefresh();
        BroadcastForeignPrefsWithoutSelfRefresh();
        App.CurrentApp.BroadcastAppearanceChanged();
        App.CurrentApp.NotifyShelfRefresh();
        UpdateHistoryQuickButton();
        RenderActivePage();
        ShowSettingsSavedToast(toastMessage, showUndo: false);
    }

    private void PerformSettingsUndo()
    {
        SettingsHistoryRecord? undone = null;
        _suppressHistoryCapture = true;
        try { undone = _library.SettingsHistory.TryUndo(); }
        finally { _suppressHistoryCapture = false; }

        if (undone is not null)
            ApplyAfterHistoryMutation($"已改回：{L10n.T(undone.Title)}");
        else
            ShowSettingsSavedToast("没有可撤销的改动", showUndo: false);
    }

    private void PerformSettingsRedo()
    {
        SettingsHistoryRecord? redone = null;
        _suppressHistoryCapture = true;
        try { redone = _library.SettingsHistory.TryRedo(); }
        finally { _suppressHistoryCapture = false; }

        if (redone is not null)
            ApplyAfterHistoryMutation($"已还原：{L10n.T(redone.Title)}");
    }

    private void ToggleHistoryRecordInPlace(SettingsHistoryRecord rec)
    {
        bool ok;
        _suppressHistoryCapture = true;
        try { ok = _library.SettingsHistory.ToggleInPlace(rec); }
        finally { _suppressHistoryCapture = false; }

        if (ok)
            ApplyAfterHistoryMutation(rec.Undone ? $"已改回：{L10n.T(rec.Title)}" : $"已还原：{L10n.T(rec.Title)}");
    }

    private void SavedUndoQuickBtn_Click(object sender, RoutedEventArgs e) => PerformSettingsUndo();

    private void HistoryQuickBtn_Click(object sender, RoutedEventArgs e)
    {
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        Flyout? fly = null;

        var root = new StackPanel
        {
            Spacing = 10,
            Width = 360,
            Padding = new Thickness(14, 12, 14, 12),
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
        };
        ThemeHelper.ApplyControlThemeResources(root.Resources, dark, _library.Prefs.Tint);

        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Text = L10n.T("最近改过"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var undoB = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Height = 26, MinHeight = 26, FontSize = 11, Content = L10n.T("撤销"), IsEnabled = CanUndoSettings() };
        undoB.Click += (_, _) => { fly?.Hide(); PerformSettingsUndo(); };
        var redoB = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Height = 26, MinHeight = 26, FontSize = 11, Content = L10n.T("还原"), IsEnabled = _library.SettingsHistory.CanRedo };
        redoB.Click += (_, _) => { fly?.Hide(); PerformSettingsRedo(); };
        acts.Children.Add(undoB);
        acts.Children.Add(redoB);
        Grid.SetColumn(acts, 1);
        head.Children.Add(acts);
        root.Children.Add(head);

        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 360,
            Content = BuildSettingsHistoryListPanel(dark, maxItems: 25, onMutated: () => fly?.Hide()),
        });

        fly = new Flyout
        {
            FlyoutPresenterStyle = (Style)Application.Current.Resources["YYFloatFlyoutPresenterStyle"],
            Content = root,
        };
        fly.ShowAt(HistoryQuickBtn);
    }

    private StackPanel BuildSettingsHistoryListPanel(bool dark, int maxItems = 30, Action? onMutated = null)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var stack = new StackPanel { Spacing = 6 };
        var entries = _library.SettingsHistory.Entries;

        if (entries.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = L10n.T("暂无设置改动记录。修改任何设置后都会自动保存，按 Ctrl+Z 可随时改回。"),
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 6, 2, 6),
            });
            return stack;
        }

        foreach (var rec in entries.Take(maxItems))
        {
            var rCap = rec;
            bool canToggle = _library.SettingsHistory.CanActuate(rCap, applyUndo: !rCap.Undone);
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 8, 10, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            titleLine.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = ThemeHelper.Brush(p.Inset),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = L10n.T(rCap.Category), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3) },
            });
            titleLine.Children.Add(new TextBlock
            {
                Text = L10n.T(rCap.Title),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(rCap.Undone ? p.T3 : p.T1),
                TextDecorations = rCap.Undone ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None,
                VerticalAlignment = VerticalAlignment.Center,
            });
            left.Children.Add(titleLine);

            string diffText = !string.IsNullOrEmpty(rCap.OldValue) && !string.IsNullOrEmpty(rCap.NewValue)
                ? $"{rCap.OldValue} → {rCap.NewValue}"
                : (string.IsNullOrEmpty(rCap.NewValue) ? rCap.OldValue : rCap.NewValue);
            string timeText = rCap.Time.ToLocalTime().Date == DateTime.Today
                ? rCap.Time.ToLocalTime().ToString("HH:mm")
                : rCap.Time.ToLocalTime().ToString("M月d日 HH:mm");
            left.Children.Add(L10n.Skip(new TextBlock
            {
                Text = string.IsNullOrEmpty(diffText) ? timeText : $"{diffText} · {timeText}",
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }));
            Grid.SetColumn(left, 0);
            row.Children.Add(left);

            if (canToggle)
            {
                var togBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Content = L10n.T(rCap.Undone ? "还原" : "改回"),
                    FontSize = 11.5,
                    MinHeight = 24,
                    Foreground = ThemeHelper.Brush(rCap.Undone ? tc.Accent : p.T2),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                togBtn.Click += (_, _) =>
                {
                    onMutated?.Invoke();
                    ToggleHistoryRecordInPlace(rCap);
                };
                Grid.SetColumn(togBtn, 1);
                row.Children.Add(togBtn);
            }

            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(7),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Child = row,
            });
        }
        return stack;
    }
}
