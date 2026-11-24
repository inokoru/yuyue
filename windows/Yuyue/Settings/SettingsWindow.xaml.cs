using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinRT.Interop;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Reader;
using Yuyue.Speech;
using Path = System.IO.Path;
using TimeSpan = System.TimeSpan;

namespace Yuyue.Settings;

public sealed partial class SettingsWindow : Window
{
    private readonly LibraryService _library;
    private string _activePage;
    private bool _initializing = true;

    // 同步页状态
    private bool _editingWebDav;
    private bool _editingGDrive;
    // 朗读页状态 ("" = overview, "kokoro" | "free" | "pc" | "neural" | "system" | 16 cloud/local provider keys)
    private string _focusedVoiceSource = "";
    private bool _localVoiceProbeStarted;
    // 书伴页状态
    private string _focusedCompanionId;
    private bool _companionPickingModel;
    private readonly HashSet<string> _newlyAddedCompanionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _cachedCompanionModels = new();
    private Dictionary<string, IReadOnlyList<string>> _liveLocalCompanions = new();
    private bool _localCompanionProbeStarted;
    // 笔记导出页状态 ("obsidian" | "notion" | "flomo" | "xinzhi")
    private string _focusedExportTarget = "obsidian";
    // 数据快照分项勾选
    private bool _snapProgress = true;
    private bool _snapNotes = true;
    private bool _snapWords = true;
    private bool _snapSettings = true;
    private bool _snapFiles = false;

    // 最近修改记录与 1.8s 自动消隐保存提示（对齐 MacSettingsHistory.swift：持久化 + 10s 折叠 + 撤销/重做 + 分类筛选 + 跳转）
    private readonly SettingsHistoryStore _history;
    private DispatcherTimer? _flashTimer;

    public SettingsWindow(LibraryService library, string initialPage = "general")
    {
        _library = library;
        _history = new SettingsHistoryStore(library.DataDir);
        _activePage = string.IsNullOrWhiteSpace(initialPage) ? "general" : initialPage;
        if (_library.Prefs.ConfiguredAiProviders.Count == 0)
            _library.Prefs.ConfiguredAiProviders.Add(_library.Prefs.AiProvider);
        _focusedCompanionId = _library.Prefs.AiProvider;

        // 兼容迁移旧单 key 到当前 provider key
        string legacyKey = _library.Secrets.Get("ai.apiKey");
        if (!string.IsNullOrEmpty(legacyKey) && string.IsNullOrEmpty(_library.Secrets.Get($"ai.key.{_library.Prefs.AiProvider}")))
            _library.Secrets.Set($"ai.key.{_library.Prefs.AiProvider}", legacyKey);

        InitializeComponent();

        ThemeHelper.ResizeWindow(this, 980, 660);
        SetTitleBar(TitleDragRegion);

        InspectorResizeHandle.MinWidthLimit = 252.0;
        InspectorResizeHandle.DefaultWidth = 304.0;
        InspectorResizeHandle.GetCurrentWidth = () => InspectorColumn.Width.Value;
        InspectorResizeHandle.GetMaxWidth = () => Math.Max(252.0, Math.Min(480.0, (RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 980.0) - 604.0));
        InspectorResizeHandle.ApplyWidth = w => InspectorColumn.Width = new GridLength(w);
        InspectorResizeHandle.CommitWidth = w =>
        {
            _library.Prefs.SettingsInspectorWidth = w;
            _library.SavePrefs();
        };

        _library.Kokoro.StateChanged += OnLiveVoiceStateChanged;
        _library.LiveVoice.StateChanged += OnLiveVoiceStateChanged;
        _library.Fonts.Changed += OnFontsChanged;
        _library.Sync.StatusChanged += OnSyncStatusChanged;
        _library.Usage.Changed += OnUsageChanged;
        Closed += (_, _) =>
        {
            _flashTimer?.Stop();
            _library.Kokoro.StateChanged -= OnLiveVoiceStateChanged;
            _library.LiveVoice.StateChanged -= OnLiveVoiceStateChanged;
            _library.Fonts.Changed -= OnFontsChanged;
            _library.Sync.StatusChanged -= OnSyncStatusChanged;
            _library.Usage.Changed -= OnUsageChanged;
        };

        ApplyAppearance();
        NavigateTo(_activePage);
    }

    private void ShowFlashBanner(string msg)
    {
        SavedFlashText.Text = msg;
        CenterSavedFlashText.Text = msg;

        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            SavedFlashText.Text = "";
            CenterSavedFlashText.Text = "";
        };
        _flashTimer.Start();
    }

    private void RecordChange(string title, string value, Action? undo = null, Action? redo = null, string? oldValue = null)
    {
        _history.Record(_activePage, title, value, oldValue, undo, redo);
        ShowFlashBanner($"✓ 已保存 · {title} {value}");
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        if (!ctrl) return;

        if (e.OriginalSource is TextBox or PasswordBox or RichEditBox) return;
        if (RootGrid.XamlRoot is not null && FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox or PasswordBox or RichEditBox) return;

        if ((e.Key == VirtualKey.Z && shift) || e.Key == VirtualKey.Y)
        {
            if (_history.TryRedo() is { } redone)
            {
                ShowFlashBanner($"↷ 已重做 · {redone.Title} {redone.NewValue}");
                ApplyAppearance();
                NavigateTo(_activePage);
                e.Handled = true;
            }
            return;
        }

        if (e.Key == VirtualKey.Z)
        {
            if (_history.TryUndo() is { } undone)
            {
                ShowFlashBanner($"↶ 已改回 · {undone.Title}");
                ApplyAppearance();
                NavigateTo(_activePage);
                e.Handled = true;
            }
        }
    }

    private void HistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        var p = CurrentPalette;
        var tint = CurrentTint;
        Flyout? flyoutRef = null;
        string selectedCat = "全部";

        var root = new StackPanel { Width = 360, Spacing = 0, Padding = new Thickness(18, 16, 18, 12), Background = ThemeHelper.Brush(p.Bg) };
        var bodyHost = new StackPanel { Spacing = 10 };

        void RebuildHistoryFlyout()
        {
            bodyHost.Children.Clear();

            var head = new Grid();
            var headLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            headLeft.Children.Add(new TextBlock { Text = "最近改过", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            if (_history.Entries.Count == 0)
                headLeft.Children.Add(new TextBlock { Text = "暂无记录", FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) });
            head.Children.Add(headLeft);

            if (_history.Entries.Count > 0)
            {
                var headRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                if (_history.CanRedo)
                {
                    var redoBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                        Content = "↷ 重做",
                        FontSize = 11,
                        Height = 24,
                        MinHeight = 24,
                        Padding = new Thickness(8, 0, 8, 0),
                    };
                    redoBtn.Click += (_, _) =>
                    {
                        if (_history.TryRedo() is { } redone)
                        {
                            ShowFlashBanner($"↷ 已重做 · {redone.Title} {redone.NewValue}");
                            flyoutRef?.Hide();
                            ApplyAppearance();
                            NavigateTo(_activePage);
                        }
                    };
                    headRight.Children.Add(redoBtn);
                }
                headRight.Children.Add(new TextBlock { Text = $"{_history.Entries.Count} 项记录", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(headRight);
            }
            bodyHost.Children.Add(head);

            if (_history.Entries.Count == 0)
            {
                // 1:1 settings-history/1.png: centered 44x44 white clock tile + title/subtitle + bottom Ctrl+Z keycap bar
                var emptyCenter = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 24) };
                var clockTile = new Border
                {
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 2),
                    Child = YYIcons.Make("history", 20, ThemeHelper.Brush(p.T2)),
                };
                emptyCenter.Children.Add(clockTile);
                emptyCenter.Children.Add(new TextBlock
                {
                    Text = "还没有修改记录",
                    FontSize = 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                emptyCenter.Children.Add(new TextBlock
                {
                    Text = "调整过的设置会记在这里，\n每一项都能随时改回去。",
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    TextAlignment = TextAlignment.Center,
                    LineHeight = 19,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                bodyHost.Children.Add(emptyCenter);

                bodyHost.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(-4, 0, -4, 2) });
                var emptyFoot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Opacity = 0.68, Margin = new Thickness(0, 2, 0, 0) };
                emptyFoot.Children.Add(YYChrome.MakeKeycap("Ctrl+Z", IsDark));
                emptyFoot.Children.Add(new TextBlock { Text = "无可改回项", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
                bodyHost.Children.Add(emptyFoot);
                L10n.ApplyTree(bodyHost);
                return;
            }

            // Category filter chips
            var catChips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (string cat in new[] { "全部", "外观", "阅读", "朗读", "书伴", "同步" })
            {
                string cKey = cat;
                bool active = selectedCat == cKey;
                var chip = new Button
                {
                    Height = 22,
                    MinHeight = 22,
                    Padding = new Thickness(8, 0, 8, 0),
                    CornerRadius = new CornerRadius(11),
                    FontSize = 10.5,
                    BorderThickness = new Thickness(1),
                    Background = ThemeHelper.Brush(active ? tint.Soft : p.Inset),
                    BorderBrush = ThemeHelper.Brush(active ? tint.Accent : p.Line),
                    Foreground = ThemeHelper.Brush(active ? tint.Accent : p.T2),
                    UseSystemFocusVisuals = false,
                    Content = cKey,
                };
                chip.Click += (_, _) => { selectedCat = cKey; RebuildHistoryFlyout(); };
                catChips.Children.Add(chip);
            }
            bodyHost.Children.Add(catChips);

            var filtered = (selectedCat == "全部"
                ? _history.Entries
                : _history.Entries.Where(h => h.Category == selectedCat)).Take(25).ToArray();

            if (filtered.Length == 0)
            {
                bodyHost.Children.Add(new TextBlock
                {
                    Text = $"「{selectedCat}」分类下暂无修改记录。",
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(0, 8, 0, 8),
                });
            }
            else
            {
                var listStack = new StackPanel { Spacing = 4 };
                foreach (var h in filtered)
                {
                    var entry = h;
                    var row = new Grid { ColumnSpacing = 6, Padding = new Thickness(8, 6, 8, 6) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var left = new StackPanel { Spacing = 1 };
                    string valDesc = !string.IsNullOrEmpty(entry.OldValue)
                        ? $"{entry.Title}：{entry.OldValue} → {entry.NewValue}"
                        : $"{entry.Title} → {entry.NewValue}";
                    left.Children.Add(new TextBlock { Text = valDesc, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    string ts = entry.Time.LocalDateTime.Date == DateTime.Today
                        ? entry.Time.LocalDateTime.ToString("HH:mm")
                        : entry.Time.LocalDateTime.ToString("M/d HH:mm");
                    left.Children.Add(new TextBlock { Text = $"{PageNameOf(entry.Page)} · {ts}", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                    row.Children.Add(left);

                    var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                    if (entry.Page != _activePage)
                    {
                        var goBtn = new Button
                        {
                            Background = new SolidColorBrush(Colors.Transparent),
                            BorderThickness = new Thickness(0),
                            Padding = new Thickness(5, 2, 5, 2),
                            FontSize = 10.5,
                            Foreground = ThemeHelper.Brush(tint.Accent),
                            Content = "前往 ›",
                            UseSystemFocusVisuals = false,
                        };
                        goBtn.Click += (_, _) =>
                        {
                            flyoutRef?.Hide();
                            NavigateTo(entry.Page);
                        };
                        acts.Children.Add(goBtn);
                    }
                    if (entry.Undo is not null)
                    {
                        var uBtn = new Button
                        {
                            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                            Content = "↶ 改回",
                            FontSize = 11,
                            Height = 24,
                            MinHeight = 24,
                            Padding = new Thickness(8, 0, 8, 0),
                        };
                        uBtn.Click += (_, _) =>
                        {
                            _history.TryUndo(entry);
                            ShowFlashBanner($"↶ 已改回 · {entry.Title}");
                            flyoutRef?.Hide();
                            ApplyAppearance();
                            NavigateTo(_activePage);
                        };
                        acts.Children.Add(uBtn);
                    }
                    Grid.SetColumn(acts, 1);
                    row.Children.Add(acts);

                    listStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(9),
                        Background = ThemeHelper.Brush(p.Inset),
                        Child = row,
                    });
                }
                bodyHost.Children.Add(new ScrollViewer { MaxHeight = 290, Content = listStack });
            }

            var foot = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            foot.Children.Add(new TextBlock { Text = "Ctrl+Z 撤销 · Ctrl+Shift+Z 重做", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
            var clrBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.Danger),
                Content = "清空记录",
            };
            clrBtn.Click += (_, _) => { _history.Clear(); flyoutRef?.Hide(); };
            foot.Children.Add(clrBtn);
            bodyHost.Children.Add(foot);
        }

        RebuildHistoryFlyout();
        root.Children.Add(bodyHost);

        flyoutRef = new Flyout
        {
            FlyoutPresenterStyle = (Style)Application.Current.Resources["YYFloatFlyoutPresenterStyle"],
            Content = root,
        };
        flyoutRef.ShowAt(anchor);
    }

    private static string PageNameOf(string p) => p switch
    {
        "general" => "通用",
        "appearance" => "外观",
        "reading" => "阅读",
        "foreign" => "外文",
        "listening" => "朗读",
        "companion" => "书伴",
        "sync" => "同步",
        "export" => "笔记导出",
        "storage" => "存储",
        "data" => "数据",
        _ => "关于",
    };

    private void OnLiveVoiceStateChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activePage == "listening") { RenderDynamicPage("listening"); RenderInspector("listening"); }
        });
    }

    private void OnFontsChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activePage == "reading") { RenderDynamicPage("reading"); RenderInspector("reading"); }
        });
    }

    private void OnSyncStatusChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activePage == "sync" && !_editingWebDav && !_editingGDrive)
                RenderDynamicPage("sync");
        });
    }

    private void OnUsageChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_activePage == "companion")
                RenderDynamicPage("companion");
        });
    }

    private static bool HasInspector(string page) =>
        page is "reading" or "foreign" or "listening" or "companion" or "export" or "storage" or "data";

    private void MountIcons()
    {
        bool dark = IsDark;
        var p = CurrentPalette;
        var accent = ThemeHelper.Brush(CurrentTint.Accent);
        var t2 = ThemeHelper.Brush(p.T2);
        Brush B(string id) => _activePage == id ? accent : t2;

        // 1:1 MacSettings.swift lines 250-264（Apple SF Symbol "book" 为摊开的书本 book-open）
        IconGeneral.Content = YYIcons.Make("sliders", 14, B("general"));
        IconAppearance.Content = YYIcons.Make("sun", 14.5, B("appearance"));
        IconReading.Content = YYIcons.Make("book-open", 14.5, B("reading"));
        IconForeign.Content = YYIcons.Make("char-book", 14, B("foreign"));
        IconListening.Content = YYIcons.Make("headphones", 14, B("listening"));
        IconCompanion.Content = YYIcons.Make("bubble-lines", 14, B("companion"));
        IconSync.Content = YYIcons.Make("sync-arrows", 14, B("sync"));
        IconExport.Content = YYIcons.Make("share-up", 14, B("export"));
        IconStorage.Content = YYIcons.Make("drive", 14, B("storage"));
        IconData.Content = YYIcons.Make("cube", 14, B("data"));
        IconAbout.Content = YYIcons.Make("info-circle", 14, B("about"));
        AboutUpdateDot.Visibility = _library.Updates.HasUnreadBadge ? Visibility.Visible : Visibility.Collapsed;

        CornerHistoryBtn.Content = YYIcons.Make("clock-history", 14.5, t2);
        InspectorHistoryBtn.Content = YYIcons.Make("clock-history", 14, t2);
    }

    public void ApplyAppearance()
    {
        Title = L10n.T("设置");
        _initializing = true;
        RootGrid.RequestedTheme = _library.Prefs.ToElementTheme();
        bool dark = IsDark;
        ThemeHelper.ConfigureTitleBar(this, dark);
        MountIcons();

        var palette = CurrentPalette;
        var accent = ThemeHelper.Brush(CurrentTint.Accent);
        var clear = new SolidColorBrush(Colors.Transparent);
        var line = ThemeHelper.Brush(palette.Line);
        var t1 = ThemeHelper.Brush(palette.T1);
        var t2 = ThemeHelper.Brush(palette.T2);

        void PaintMode(Border box, TextBlock lbl, bool on)
        {
            box.BorderBrush = on ? accent : line;
            box.BorderThickness = new Thickness(on ? 2 : 1);
            lbl.Foreground = on ? t1 : t2;
            lbl.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        PaintMode(ThemeLightPreview, ThemeLightLabel, _library.Prefs.ThemeMode == "light");
        PaintMode(ThemeDarkPreview, ThemeDarkLabel, _library.Prefs.ThemeMode == "dark");
        PaintMode(ThemeSystemPreview, ThemeSystemLabel, _library.Prefs.ThemeMode == "system");
        ThemeSystemPreview.Padding = new Thickness(_library.Prefs.ThemeMode == "system" ? 1 : 0);

        void PaintDot(Border dot, TextBlock lbl, bool on)
        {
            dot.BorderBrush = on ? accent : clear;
            lbl.Foreground = on ? t1 : t2;
            lbl.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        PaintDot(TintDotCamel, TintLabelCamel, _library.Prefs.TintId == "camel");
        PaintDot(TintDotSage, TintLabelSage, _library.Prefs.TintId == "sage");
        PaintDot(TintDotMist, TintLabelMist, _library.Prefs.TintId == "mist");
        PaintDot(TintDotBlush, TintLabelBlush, _library.Prefs.TintId == "blush");

        CoverSizeSlider.Value = Math.Clamp(_library.Prefs.CoverWidth, 76, 140);
        ShowProgressToggle.IsOn = _library.Prefs.ShowProgress;
        _initializing = false;

        if (_activePage != "appearance") RenderDynamicPage(_activePage);
        if (HasInspector(_activePage)) RenderInspector(_activePage);
        L10n.ApplyTree(RootGrid);
    }

    public void NavigateTo(string page)
    {
        page = page switch
        {
            "speech" => "listening",
            "notes" => "export",
            "update" => "about",
            _ => page,
        };
        _activePage = page;
        _companionPickingModel = false;
        var selectedBg = (Brush)Application.Current.Resources["YYSidebarSelectedBrush"];
        var clear = new SolidColorBrush(Colors.Transparent);

        Button[] navs = [NavGeneral, NavAppearance, NavReading, NavForeign, NavListening, NavCompanion, NavSync, NavExport, NavStorage, NavData, NavAbout];
        foreach (var b in navs)
        {
            bool on = Equals(b.Tag, page);
            b.Background = on ? selectedBg : clear;
            b.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        MountIcons();

        (PageTitleText.Text, PageSubtitleText.Text) = page switch
        {
            "general" => ("通用", "语言、启动行为与导入方式"),
            "appearance" => ("外观", "应用界面的外观与配色；书页纸色在「阅读」中设置"),
            "reading" => ("阅读", "新书的默认排版；单本书可在阅读页 Aa 中单独调整"),
            "foreign" => ("外文", "查词、难词注释、生词与离线翻译"),
            "listening" => ("朗读", "朗读、查词发音与播客音色"),
            "companion" => ("书伴", "阅读问答与播客撰稿所用的大模型"),
            "sync" => ("同步", "通过个人云盘在多台设备间同步"),
            "export" => ("笔记导出", "将划线与想法发送到常用笔记软件"),
            "storage" => ("存储", "书库、有声书与本地缓存占用的空间"),
            "data" => ("数据", "本地备份快照与阅读记录导出"),
            _ => ("关于", "版本、更新和作者"),
        };

        bool inspector = HasInspector(page);
        double maxInsp = Math.Max(252.0, Math.Min(480.0, (RootGrid.ActualWidth > 0 ? RootGrid.ActualWidth : 980.0) - 604.0));
        double inspW = Math.Clamp(_library.Prefs.SettingsInspectorWidth > 0 ? _library.Prefs.SettingsInspectorWidth : 304.0, 252.0, maxInsp);
        InspectorColumn.Width = inspector ? new GridLength(inspW) : new GridLength(0);
        InspectorBorder.Visibility = inspector ? Visibility.Visible : Visibility.Collapsed;
        InspectorResizeHandle.Visibility = inspector ? Visibility.Visible : Visibility.Collapsed;
        PageHeaderGrid.Visibility = page == "about" ? Visibility.Collapsed : Visibility.Visible;
        // 1:1 MacSettings.swift:87-94：全部 11 页右上角均显示 SettingsCorner（含「关于」）
        CenterCornerStack.Visibility = inspector ? Visibility.Collapsed : Visibility.Visible;

        AppearancePage.Visibility = page == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        DynamicPageHost.Visibility = page == "appearance" ? Visibility.Collapsed : Visibility.Visible;

        if (page != "appearance") RenderDynamicPage(page);
        if (inspector) RenderInspector(page);
        L10n.ApplyTree(RootGrid);
    }

    private void RenderDynamicPage(string page)
    {
        DynamicPageHost.Children.Clear();
        switch (page)
        {
            case "general": BuildGeneralPage(); break;
            case "reading": BuildReadingPage(); break;
            case "foreign": BuildForeignPage(); break;
            case "listening": BuildListeningPage(); break;
            case "companion": BuildCompanionPage(); break;
            case "sync": BuildSyncPage(); break;
            case "export": BuildExportPage(); break;
            case "storage": BuildStoragePage(); break;
            case "data": BuildDataPage(); break;
            default: BuildAboutPage(); break;
        }
        L10n.ApplyTree(DynamicPageHost);
    }

    // ---------- Shared Mac-style UI Builders ----------

    private bool IsDark => ThemeHelper.IsDark(RootGrid.ActualTheme);
    private YYPalette CurrentPalette => ThemeHelper.Palette(IsDark);
    private YYTintColors CurrentTint => ThemeHelper.TintColors(_library.Prefs.Tint, IsDark);

    private FrameworkElement MakeHeading(string text, string? rightNote = null)
    {
        var p = CurrentPalette;
        if (string.IsNullOrEmpty(rightNote))
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T2),
                Margin = new Thickness(14, 6, 0, -4),
            };
        }
        var g = new Grid { ColumnSpacing = 12, Margin = new Thickness(14, 6, 14, -4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = text, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        var noteTb = new TextBlock { Text = rightNote, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(noteTb, 1);
        g.Children.Add(noteTb);
        return g;
    }

    private TextBlock MakeNote(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = ThemeHelper.Brush(CurrentPalette.T3),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 18,
        Margin = new Thickness(14, 0, 14, 0),
    };

    private Border MakeCard(params UIElement[] rows)
    {
        var p = CurrentPalette;
        var stack = new StackPanel { Spacing = 0 };
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(16, 0, 16, 0) });
            stack.Children.Add(rows[i]);
        }
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(IsDark ? Color.FromArgb(16, 255, 255, 255) : p.Line),
            BorderThickness = new Thickness(0.5),
            Child = stack,
        };
    }

    private Grid MakeRow(string title, string? subtitle, FrameworkElement? right = null, bool compact = false)
    {
        var p = CurrentPalette;
        var g = new Grid
        {
            ColumnSpacing = 16,
            Padding = new Thickness(16, compact ? 8 : 10, 16, compact ? 8 : 10),
            MinHeight = compact ? 38 : 46,
        };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
        if (!string.IsNullOrEmpty(subtitle))
            left.Children.Add(new TextBlock { Text = subtitle, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap, LineHeight = 16 });
        g.Children.Add(left);

        if (right is not null)
        {
            right.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
        }
        return g;
    }

    private ToggleSwitch MakeSwitch(bool isOn, Action<bool> onChanged, bool enabled = true)
    {
        var sw = new ToggleSwitch { IsOn = isOn, IsEnabled = enabled };
        sw.Toggled += (_, _) => { if (sw.IsEnabled) onChanged(sw.IsOn); };
        return sw;
    }

    private Border MakeSegments<T>((T Value, string Label)[] items, T current, Action<T> onSelect, double width = 0) where T : notnull
    {
        var p = CurrentPalette;
        var grid = new Grid { ColumnSpacing = 2 };
        if (width > 0) grid.Width = width;
        for (int i = 0; i < items.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (val, lbl) = items[i];
            bool on = EqualityComparer<T>.Default.Equals(val, current);
            var b = new Button
            {
                Content = lbl,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 24,
                MinHeight = 24,
                Padding = new Thickness(width > 0 ? 4 : 8, 0, width > 0 ? 4 : 8, 0),
                FontSize = 11,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                UseSystemFocusVisuals = false,
            };
            T captured = val;
            b.Click += (_, _) => onSelect(captured);
            Grid.SetColumn(b, i);
            grid.Children.Add(b);
        }
        return new Border
        {
            Background = ThemeHelper.Brush(p.Inset),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(2.5),
            Child = grid,
        };
    }

    private Grid MakeInlineInputRow(string label, string value, string placeholder, Action<string> onChanged, bool isPassword = false)
    {
        var p = CurrentPalette;
        var g = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 8, 16, 8), MinHeight = 42 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        g.Children.Add(new TextBlock { Text = label, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });

        if (isPassword)
        {
            var pb = new PasswordBox
            {
                Password = value,
                PlaceholderText = placeholder,
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeHelper.Brush(p.Line),
                Background = ThemeHelper.Brush(p.Bg),
                CornerRadius = new CornerRadius(7),
                FontSize = 12,
            };
            pb.PasswordChanged += (_, _) => onChanged(pb.Password);
            Grid.SetColumn(pb, 1);
            g.Children.Add(pb);
        }
        else
        {
            var tb = new TextBox
            {
                Text = value,
                PlaceholderText = placeholder,
                BorderThickness = new Thickness(1),
                BorderBrush = ThemeHelper.Brush(p.Line),
                Background = ThemeHelper.Brush(p.Bg),
                CornerRadius = new CornerRadius(7),
                FontSize = 12,
            };
            tb.TextChanged += (_, _) => onChanged(tb.Text);
            Grid.SetColumn(tb, 1);
            g.Children.Add(tb);
        }
        return g;
    }

    private Border MakeStatusPill(string text, Color fg, Color? bg = null) => new()
    {
        CornerRadius = new CornerRadius(9),
        Height = 18,
        Padding = new Thickness(7, 0, 7, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Background = ThemeHelper.Brush(bg ?? Color.FromArgb(34, fg.R, fg.G, fg.B)),
        Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(fg), VerticalAlignment = VerticalAlignment.Center },
    };

    // ---------- 右栏 SettingsInspector（1:1 MacSettingsSide.swift） ----------

    private void RenderInspector(string page)
    {
        InspectorHost.Children.Clear();
        var p = CurrentPalette;
        var tint = CurrentTint;

        Grid InspectorHead(string title, string trailing)
        {
            var g = new Grid();
            g.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            g.Children.Add(new TextBlock { Text = trailing, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
            return g;
        }

        switch (page)
        {
            case "reading":
            {
                var rs = _library.GetReaderSettings();
                var (paperName, bgCol, inkCol, isDarkPaper) = ResolvePaperColors(rs);
                string fontName = !string.IsNullOrEmpty(rs.FontFace)
                    ? rs.FontFace switch
                    {
                        "notoserif" => "思源宋体",
                        "notosans" => "思源黑体",
                        "wenkai" => "霞鹜文楷",
                        _ when rs.FontFace.StartsWith("file:", StringComparison.OrdinalIgnoreCase) => _library.Fonts.GetFontFileDisplayName(rs.FontFace[5..]),
                        _ when rs.FontFace.StartsWith("local:", StringComparison.OrdinalIgnoreCase) => FontShelfService.CleanRegistryFontFamilyName(rs.FontFace[6..]),
                        _ when rs.FontFace.StartsWith("sys:", StringComparison.OrdinalIgnoreCase) => FontShelfService.CleanRegistryFontFamilyName(rs.FontFace[4..]),
                        _ => rs.FontFace,
                    }
                    : rs.Font switch { Font.Serif => "宋体", Font.Kai => "楷体", Font.WenKai => "霞鹜文楷", Font.Sans => "黑体", _ => "系统" };
                InspectorHost.Children.Add(InspectorHead("样张", $"{paperName} · {fontName} · {(int)Math.Round(rs.FontSize)}"));

                string baseFallbackFam = (rs.Font switch
                {
                    Font.Sans => YYFontFamily.Sans,
                    Font.Kai => YYFontFamily.Kaiti,
                    Font.WenKai => YYFontFamily.Wenkai,
                    _ => YYFontFamily.Serif,
                }).WindowsFamily();
                string fam = !string.IsNullOrEmpty(rs.FontFace)
                    ? _library.Fonts.ResolveXamlFontSource(
                        rs.FontFace,
                        rs.FontFace == "notosans" ? YYFontFamily.Sans.WindowsFamily() : baseFallbackFam)
                    : (rs.Font == Font.WenKai
                        ? _library.Fonts.ResolveXamlFontSource("wenkai", YYFontFamily.Wenkai.WindowsFamily())
                        : baseFallbackFam);
                double samplePt = Math.Round(rs.FontSize * 0.68);
                double lh = (rs.LineHeight ?? 1.85) * samplePt;
                string indent = (rs.Indent ?? true) ? "　　" : "";

                var sampleStack = new StackPanel { Spacing = 8 };
                var headLine = new Grid();
                headLine.Children.Add(new TextBlock { Text = "故乡", FontSize = 9.5, Foreground = ThemeHelper.Brush(inkCol), Opacity = 0.45 });
                headLine.Children.Add(new TextBlock { Text = "12 / 31", FontSize = 9.5, Foreground = ThemeHelper.Brush(inkCol), Opacity = 0.45, HorizontalAlignment = HorizontalAlignment.Right });
                sampleStack.Children.Add(headLine);

                foreach (var para in new[]
                {
                    "我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。",
                    "时候既然是深冬；渐近故乡时，天气又阴晦了，冷风吹进船舱中，呜呜的响，从篷隙向外一望，苍黄的天底下，远近横着几个萧索的荒村，没有一些活气。",
                })
                {
                    sampleStack.Children.Add(new TextBlock
                    {
                        Text = indent + para,
                        FontFamily = new FontFamily(fam),
                        FontSize = samplePt,
                        LineHeight = lh,
                        TextAlignment = (rs.Justify ?? true) ? TextAlignment.Justify : TextAlignment.Left,
                        Foreground = ThemeHelper.Brush(inkCol),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                InspectorHost.Children.Add(new Border
                {
                    MinHeight = 228,
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(bgCol),
                    BorderBrush = ThemeHelper.Brush(isDarkPaper ? Colors.Transparent : p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(14, 12, 14, 12),
                    Child = sampleStack,
                });
                InspectorHost.Children.Add(new TextBlock { Text = "节选自鲁迅《故乡》", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
                break;
            }

            case "foreign":
            {
                var rs = _library.GetReaderSettings();
                var (_, bgCol, inkCol, isDarkPaper) = ResolvePaperColors(rs);
                var ink2Col = Color.FromArgb(165, inkCol.R, inkCol.G, inkCol.B);
                bool glossEnabled = _library.Prefs.ForeignGlossEnabled && _library.Prefs.GlossLevel > 0;
                string lvlText = !glossEnabled
                    ? "不写释义"
                    : _library.Prefs.GlossVocabOnly
                        ? "只写生词本里的"
                        : _library.Prefs.GlossLevel switch { 2 => "四级及以上", 3 => "六级及以上", 4 => "考研及以上", 5 => "雅思及以上", _ => "GRE 及以上" };
                InspectorHost.Children.Add(InspectorHead("样张", lvlText));

                // 1:1 settings-foreign/1.png & MacSettingsSide.swift:401-473：4 行自然排版的《双城记》开篇 + 虚线生词下划线
                (string Word, string? Gloss, byte Level, bool InVocab)[][] sampleLines =
                [
                    [("It was the best of times, it was the worst", null, 0, false)],
                    [("of times, it was the age of", null, 0, false), ("wisdom,", "智慧", 2, false), ("it was", null, 0, false)],
                    [("the age of", null, 0, false), ("foolishness,", "愚蠢", 4, false), ("it was the", null, 0, false), ("epoch", "时代", 5, false), ("of", null, 0, false)],
                    [("belief,", "信仰", 3, false), ("it was the epoch of", null, 0, false), ("incredulity…", "怀疑", 6, true)],
                ];

                var linesStack = new StackPanel { Spacing = 5 };
                foreach (var rowWords in sampleLines)
                {
                    bool lineHasGloss = rowWords.Any(w => glossEnabled
                        && w.Gloss is not null
                        && (_library.Prefs.GlossVocabOnly ? w.InVocab : w.Level >= _library.Prefs.GlossLevel));
                    var linePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    foreach (var w in rowWords)
                    {
                        bool showGloss = glossEnabled
                            && w.Gloss is not null
                            && (_library.Prefs.GlossVocabOnly ? w.InVocab : w.Level >= _library.Prefs.GlossLevel);

                        var token = new StackPanel { Spacing = 0 };
                        if (lineHasGloss)
                        {
                            token.Children.Add(new TextBlock
                            {
                                Text = showGloss ? w.Gloss! : " ",
                                FontSize = 9,
                                Foreground = ThemeHelper.Brush(ink2Col),
                            });
                        }
                        token.Children.Add(new TextBlock
                        {
                            Text = w.Word,
                            FontFamily = new FontFamily(YYFontFamily.LatinSerif.WindowsFamily()),
                            FontSize = 12,
                            Foreground = ThemeHelper.Brush(inkCol),
                        });
                        if (_library.Prefs.ForeignMarkVocab && w.InVocab)
                        {
                            var dashRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 1, 0, 0) };
                            for (int d = 0; d < 12; d++)
                                dashRow.Children.Add(new Border { Width = 3, Height = 1, Background = ThemeHelper.Brush(inkCol), Opacity = 0.62 });
                            token.Children.Add(dashRow);
                        }
                        linePanel.Children.Add(token);
                    }
                    linesStack.Children.Add(linePanel);
                }

                InspectorHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(bgCol),
                    BorderBrush = ThemeHelper.Brush(isDarkPaper ? Colors.Transparent : p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(14),
                    Child = linesStack,
                });
                string cap = (!glossEnabled ? "难词上方不写释义；" : "") + (_library.Prefs.ForeignMarkVocab ? "虚线是生词本里的词" : "生词不标");
                InspectorHost.Children.Add(new TextBlock { Text = cap, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
                break;
            }

            case "listening":
            {
                BuildListeningInspector(p, tint);
                break;
            }

            case "companion":
            {
                BuildCompanionInspector(p, tint);
                break;
            }

            case "export":
            {
                BuildExportInspector(p, tint);
                break;
            }

            case "storage":
            {
                InspectorHost.Children.Add(InspectorHead("书库", ""));
                var cardStack = new StackPanel { Spacing = 5 };
                cardStack.Children.Add(new TextBlock { Text = Path.GetFileName(_library.DataDir), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                cardStack.Children.Add(new TextBlock
                {
                    Text = _library.DataDir,
                    FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    TextWrapping = TextWrapping.Wrap,
                });
                var openSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                openSp.Children.Add(YYIcons.Make("folder", 12.5, ThemeHelper.Brush(p.T1)));
                openSp.Children.Add(new TextBlock { Text = "在资源管理器中显示", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                var openBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Padding = new Thickness(0, 4, 6, 2),
                    Margin = new Thickness(0, 4, 0, 0),
                    Content = openSp,
                };
                openBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_library.DataDir}\"") { UseShellExecute = true });
                cardStack.Children.Add(openBtn);

                InspectorHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(11),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(14, 12, 14, 12),
                    Child = cardStack,
                });
                break;
            }

            case "data":
            {
                // 1:1 MacSettingsSide.swift:169, 220-251：标题为「上次快照」
                InspectorHost.Children.Add(InspectorHead("上次快照", ""));
                string snapDir = Path.Combine(_library.DataDir, "snapshots");
                Directory.CreateDirectory(snapDir);
                var files = Directory.GetFiles(snapDir, "*.yuyue")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(fi => fi.LastWriteTime)
                    .ToArray();

                var cardStack = new StackPanel { Spacing = 5 };
                if (files.Length > 0)
                {
                    cardStack.Children.Add(new TextBlock { Text = files[0].LastWriteTime.ToString("M 月 d 日 HH:mm"), FontSize = 16.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                    cardStack.Children.Add(new TextBlock { Text = $"{Environment.MachineName} · {FormatBytes(files[0].Length)}", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
                }
                else
                {
                    cardStack.Children.Add(new TextBlock { Text = "暂无快照", FontSize = 14.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) });
                    cardStack.Children.Add(new TextBlock { Text = "勾选需要备份的内容后，点击「生成快照」", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
                }

                var openSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                openSp.Children.Add(YYIcons.Make("folder", 12.5, ThemeHelper.Brush(p.T1)));
                openSp.Children.Add(new TextBlock { Text = "在资源管理器中显示", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                var openBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Padding = new Thickness(0, 4, 6, 2),
                    Margin = new Thickness(0, 4, 0, 0),
                    Content = openSp,
                };
                openBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{snapDir}\"") { UseShellExecute = true });
                cardStack.Children.Add(openBtn);

                InspectorHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(11),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(14, 12, 14, 12),
                    Child = cardStack,
                });
                break;
            }
        }
        L10n.ApplyTree(InspectorHost);
    }

    private (string Name, Color Bg, Color Ink, bool Dark) ResolvePaperColors(ReaderSettings rs)
    {
        if (_library.Prefs.GlobalEinkMode)
            return ("墨水屏", Colors.White, Colors.Black, false);
        var resolved = PaperAppearance.Resolve(_library, rs);
        if (resolved.Custom is CustomPaper cp)
        {
            try
            {
                var pc = YuyueCoreMethods.PaperColors(cp);
                return (cp.Name, CustomPaperDialog.ParseHexColor(pc.Bg, Colors.Beige), CustomPaperDialog.ParseHexColor(pc.Ink, Colors.Black), pc.Dark);
            }
            catch { }
        }
        var tok = resolved.Preset switch
        {
            Paper.Dark => YYPapers.Dark,
            Paper.Kami => YYPapers.Kami,
            Paper.KamiNight => YYPapers.Kaminight,
            Paper.Sage => YYPapers.Sage,
            Paper.Eink => YYPapers.Eink,
            _ => YYPapers.Plain,
        };
        string swiftName = resolved.Preset switch
        {
            Paper.Dark => "暗黑",
            Paper.Kami => "纸本",
            Paper.KamiNight => "纸本 · 夜",
            Paper.Sage => "芦苇",
            Paper.Eink => "铅纸",
            _ => "素白",
        };
        return (swiftName, tok.Bg, tok.Ink, tok.IsDark);
    }

    // ---------- 1. 通用（1:1 MacSettings.swift:599-691 & AppLanguage.swift:224-536） ----------

    private void BuildGeneralPage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;

        string curLang = _library.Prefs.UiLanguage;
        string osLangName = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "English"
            : "中文";
        (string langIcon, string langLabel, string? langBadge, string subText) = curLang switch
        {
            "zh-Hans" => ("globe", "中文", null, "当前：中文 · 切换需确认重开，并回到当前页"),
            "en" => ("globe", "English", null, "当前：English · 切换需确认重开，并回到当前页"),
            _ => ("laptop", "跟随系统", osLangName, $"当前：{osLangName} · 切换需确认重开，并回到当前页"),
        };

        var langMenu = new MenuFlyout();
        foreach (var (k, lbl) in new[] { ("zh-Hans", "中文"), ("en", "English"), ("", "---"), ("system", "跟随系统") })
        {
            if (lbl == "---") { langMenu.Items.Add(new MenuFlyoutSeparator()); continue; }
            var mi = new MenuFlyoutItem { Text = k == curLang ? $"✓ {lbl}" : lbl };
            string key = k;
            string name = lbl;
            mi.Click += async (_, _) =>
            {
                if (key == _library.Prefs.UiLanguage) return;
                var dlg = new ContentDialog
                {
                    Title = L10n.T($"切换为「{name}」？"),
                    Content = L10n.T(_library.SystemIntegration.IsAudioPlaying
                        ? "正在朗读音频，将在下次启动时生效。"
                        : "确认后将立即重开应用并回到当前设置页。"),
                    PrimaryButtonText = L10n.T(_library.SystemIntegration.IsAudioPlaying ? "好" : "切换并重开"),
                    SecondaryButtonText = L10n.T(_library.SystemIntegration.IsAudioPlaying ? "" : "稍后重开"),
                    CloseButtonText = L10n.T("取消"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = RootGrid.XamlRoot,
                };
                var res = await dlg.ShowAsync();
                if (res is ContentDialogResult.Primary or ContentDialogResult.Secondary)
                {
                    string old = _library.Prefs.UiLanguage;
                    _library.Prefs.UiLanguage = key;
                    _library.SavePrefs();
                    _library.ApplyUiLanguage();
                    RecordChange("语言", name, () =>
                    {
                        _library.Prefs.UiLanguage = old;
                        _library.SavePrefs();
                        _library.ApplyUiLanguage();
                        App.CurrentApp.BroadcastAppearanceChanged();
                    });
                    if (res == ContentDialogResult.Primary && !_library.SystemIntegration.IsAudioPlaying)
                    {
                        SystemIntegrationService.RelaunchApplication("--settings general");
                        return;
                    }
                    App.CurrentApp.BroadcastAppearanceChanged();
                    RenderDynamicPage("general");
                }
            };
            langMenu.Items.Add(mi);
        }

        // 1:1 settings-general/2.png: [icon] 跟随系统 [中文 badge]  ⇕
        var triggerGrid = new Grid { MinWidth = 156, ColumnSpacing = 7 };
        triggerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        triggerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        triggerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconEl = YYIcons.Make(langIcon, 13, ThemeHelper.Brush(p.T2));
        iconEl.VerticalAlignment = VerticalAlignment.Center;
        triggerGrid.Children.Add(iconEl);

        var labelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        labelRow.Children.Add(new TextBlock { Text = langLabel, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(langBadge))
        {
            labelRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(5),
                Background = ThemeHelper.Brush(IsDark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(14, 42, 43, 46)),
                Padding = new Thickness(6, 1.5, 6, 1.5),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = langBadge, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T2) },
            });
        }
        Grid.SetColumn(labelRow, 1);
        triggerGrid.Children.Add(labelRow);
        var chev = YYIcons.Make("updown", 10, ThemeHelper.Brush(p.T3));
        Grid.SetColumn(chev, 2);
        triggerGrid.Children.Add(chev);

        var langBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            UseSystemFocusVisuals = false,
            Content = triggerGrid,
            Flyout = langMenu,
        };

        var reopenSw = MakeSwitch(_library.Prefs.ReopenLast, on =>
        {
            _library.Prefs.ReopenLast = on;
            _library.SavePrefs();
            RecordChange(
                "启动时继续上次阅读",
                on ? "开" : "关",
                undo: () => { _library.Prefs.ReopenLast = !on; _library.SavePrefs(); },
                redo: () => { _library.Prefs.ReopenLast = on; _library.SavePrefs(); },
                oldValue: on ? "关" : "开");
        });

        var loginSw = MakeSwitch(_library.Prefs.AutoLaunchOnLogin, on =>
        {
            _library.Prefs.AutoLaunchOnLogin = on;
            _library.SavePrefs();
            SystemIntegrationService.SyncLoginStartup(on);
            _library.SystemIntegration.EnsureTrayVisible();
            RecordChange(
                "登录时后台启动",
                on ? "开" : "关",
                undo: () =>
                {
                    _library.Prefs.AutoLaunchOnLogin = !on;
                    _library.SavePrefs();
                    SystemIntegrationService.SyncLoginStartup(!on);
                    _library.SystemIntegration.EnsureTrayVisible();
                },
                redo: () =>
                {
                    _library.Prefs.AutoLaunchOnLogin = on;
                    _library.SavePrefs();
                    SystemIntegrationService.SyncLoginStartup(on);
                    _library.SystemIntegration.EnsureTrayVisible();
                },
                oldValue: on ? "关" : "开");
        });

        var awakeSw = MakeSwitch(_library.Prefs.KeepAwakeWhileListening, on =>
        {
            _library.Prefs.KeepAwakeWhileListening = on;
            _library.SavePrefs();
            _library.SystemIntegration.ApplyPowerStayAwake();
            RecordChange(
                "朗读时防止休眠",
                on ? "开" : "关",
                undo: () =>
                {
                    _library.Prefs.KeepAwakeWhileListening = !on;
                    _library.SavePrefs();
                    _library.SystemIntegration.ApplyPowerStayAwake();
                },
                redo: () =>
                {
                    _library.Prefs.KeepAwakeWhileListening = on;
                    _library.SavePrefs();
                    _library.SystemIntegration.ApplyPowerStayAwake();
                },
                oldValue: on ? "关" : "开");
        });

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("语言", subText, langBtn),
            MakeRow("启动时继续上次阅读", "关闭则进入书架", reopenSw),
            MakeRow("登录时后台启动", "常驻托盘，不弹出窗口，按时生成播客", loginSw),
            MakeRow("朗读时防止系统休眠", "合上屏幕仍会暂停朗读", awakeSw)));

        // 导入 + 监听目录（自动递归导入新书与有声书）
        DynamicPageHost.Children.Add(MakeHeading("导入"));
        var fmtBadge = new TextBlock
        {
            Text = "EPUB PDF MOBI AZW3 TXT",
            FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
        };

        string watched = _library.Prefs.WatchedFolder;
        var watchActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var pickWatchBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Content = string.IsNullOrEmpty(watched) ? "选择文件夹…" : "更换…",
            Foreground = ThemeHelper.Brush(p.T1),
            FontSize = 12,
        };
        pickWatchBtn.Click += async (_, _) =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var f = await picker.PickSingleFolderAsync();
            if (f is null) return;
            string prev = _library.Prefs.WatchedFolder;
            string next = f.Path;
            _library.Prefs.WatchedFolder = next;
            _library.SavePrefs();
            _library.WatchedFolder.Reload();
            RecordChange(
                "自动导入文件夹",
                Path.GetFileName(next),
                undo: () => { _library.Prefs.WatchedFolder = prev; _library.SavePrefs(); _library.WatchedFolder.Reload(); },
                redo: () => { _library.Prefs.WatchedFolder = next; _library.SavePrefs(); _library.WatchedFolder.Reload(); });
            RenderDynamicPage("general");
        };
        watchActs.Children.Add(pickWatchBtn);
        if (!string.IsNullOrEmpty(watched))
        {
            var clearWatchBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYIconButtonStyle"],
                Content = "停用",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.Danger),
            };
            clearWatchBtn.Click += (_, _) =>
            {
                string prev = _library.Prefs.WatchedFolder;
                _library.Prefs.WatchedFolder = "";
                _library.SavePrefs();
                _library.WatchedFolder.Reload();
                RecordChange(
                    "自动导入文件夹",
                    "已停用",
                    undo: () => { _library.Prefs.WatchedFolder = prev; _library.SavePrefs(); _library.WatchedFolder.Reload(); },
                    redo: () => { _library.Prefs.WatchedFolder = ""; _library.SavePrefs(); _library.WatchedFolder.Reload(); });
                RenderDynamicPage("general");
            };
            watchActs.Children.Add(clearWatchBtn);
        }

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("拖入书架即可导入", "文件会复制到书库，移动或删除原文件不受影响", fmtBadge),
            MakeRow(
                "自动导入文件夹",
                string.IsNullOrEmpty(watched) ? "持续监听本地目录及子目录，放入新书或有声书自动入库" : watched,
                watchActs)));
    }

    // ---------- 2. 阅读（1:1 MacSettings.swift:897-1108：双横线纸色磁贴 + 字样册 + 完整沉浸式阅读） ----------

    private void BuildReadingPage()
    {
        var rs = _library.GetReaderSettings();
        var p = CurrentPalette;
        var tint = CurrentTint;

        void Update(string label, string valText, Func<ReaderSettings, ReaderSettings> edit)
        {
            var prev = rs;
            var next = edit(rs);
            rs = next;
            _library.SaveReaderSettings(next);
            App.CurrentApp.BroadcastReaderSettingsChanged();
            RecordChange(
                label,
                valText,
                undo: () => { _library.SaveReaderSettings(prev); App.CurrentApp.BroadcastReaderSettingsChanged(); },
                redo: () => { _library.SaveReaderSettings(next); App.CurrentApp.BroadcastReaderSettingsChanged(); });
            RenderInspector("reading");
        }

        // 1. 纸张磁贴网格（YYPaper.all 顺序 + 自定义纸 + 新建，内绘两道墨水短线 26x2 & 18x2）
        var resolvedPaper = PaperAppearance.Resolve(_library, rs);
        var paperWrap = new StackPanel { Spacing = 10, Padding = new Thickness(16, 14, 16, 14) };
        var paperHeader = new Grid();
        paperHeader.Children.Add(new TextBlock { Text = "纸色", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        var followBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Opacity = _library.Prefs.GlobalEinkMode ? 0.45 : 1.0 };
        followBox.Children.Add(new TextBlock { Text = "跟随外观", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        var followSwitch = MakeSwitch(_library.Prefs.PaperFollowAppearance, v =>
        {
            bool prevFollow = _library.Prefs.PaperFollowAppearance;
            var prevRs = rs;
            bool save = PaperAppearance.SetFollow(_library, v, rs, out var updated);
            var nextRs = save ? updated : rs;
            if (save)
            {
                rs = updated;
                _library.SaveReaderSettings(rs);
            }
            App.CurrentApp.BroadcastReaderSettingsChanged();
            RecordChange(
                "纸色跟随外观",
                v ? "开" : "关",
                undo: () =>
                {
                    _library.Prefs.PaperFollowAppearance = prevFollow;
                    _library.SavePrefs();
                    if (save) _library.SaveReaderSettings(prevRs);
                    App.CurrentApp.BroadcastReaderSettingsChanged();
                },
                redo: () =>
                {
                    _library.Prefs.PaperFollowAppearance = v;
                    _library.SavePrefs();
                    if (save) _library.SaveReaderSettings(nextRs);
                    App.CurrentApp.BroadcastReaderSettingsChanged();
                });
            RenderInspector("reading");
            RenderDynamicPage("reading");
        });
        followSwitch.IsEnabled = !_library.Prefs.GlobalEinkMode;
        followBox.Children.Add(followSwitch);
        paperHeader.Children.Add(followBox);
        paperWrap.Children.Add(paperHeader);
        if (_library.Prefs.GlobalEinkMode)
        {
            paperWrap.Children.Add(new TextBlock
            {
                Text = "墨水屏模式已接管纸色，关闭后恢复原纸色",
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T2),
            });
        }

        CustomPaper[] customList;
        try { customList = _library.Core.CustomPapers(); } catch { customList = []; }

        (Paper Kind, string Name, YYPaper Token)[] presets =
        [
            (Paper.Plain, "素白", YYPapers.Plain),
            (Paper.Dark, "暗黑", YYPapers.Dark),
            (Paper.Kami, "纸本", YYPapers.Kami),
            (Paper.KamiNight, "纸本 · 夜", YYPapers.Kaminight),
            (Paper.Sage, "芦苇", YYPapers.Sage),
            (Paper.Eink, "铅纸", YYPapers.Eink),
        ];

        int totalTiles = presets.Length + customList.Length + 1;
        int cols = 6;
        int rows = (totalTiles + cols - 1) / cols;
        var paperGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (int c = 0; c < cols; c++) paperGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++) paperGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 1:1 settings-reading/2.png: tall unified white card containing paper swatch + title inside, 1.5px accent border around the whole card
        Border MakePaperTileCard(Color bg, Color ink, Color border, string name, bool active)
        {
            var lines = new StackPanel { Spacing = 3.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(new Border { Width = 24, Height = 2, CornerRadius = new CornerRadius(1), Background = ThemeHelper.Brush(ink), Opacity = 0.72 });
            lines.Children.Add(new Border { Width = 16, Height = 2, CornerRadius = new CornerRadius(1), Background = ThemeHelper.Brush(ink), Opacity = 0.45, HorizontalAlignment = HorizontalAlignment.Left });
            var swatch = new Border
            {
                Height = 30,
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(bg),
                BorderBrush = ThemeHelper.Brush(border),
                BorderThickness = new Thickness(0.5),
                Child = lines,
            };
            var inner = new StackPanel { Spacing = 5 };
            inner.Children.Add(swatch);
            inner.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 10.5,
                FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ThemeHelper.Brush(active ? p.T1 : p.T2),
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            return new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(5, 6, 5, 6),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(active ? tint.Accent : p.Line),
                BorderThickness = new Thickness(active ? 1.5 : 0.75),
                Child = inner,
            };
        }

        int cellIdx = 0;
        foreach (var (kind, name, tok) in presets)
        {
            bool on = resolvedPaper.Custom is null && resolvedPaper.Preset == kind;
            var btn = new Button
            {
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = MakePaperTileCard(tok.Bg, tok.Ink, tok.Line, name, on),
            };
            Paper pk = kind;
            string pName = name;
            btn.Click += (_, _) =>
            {
                bool save = PaperAppearance.PickPreset(_library, pk, rs, out var updated);
                if (save) Update("纸色", pName, _ => updated);
                else { App.CurrentApp.BroadcastReaderSettingsChanged(); RenderInspector("reading"); }
                RenderDynamicPage("reading");
            };
            Grid.SetRow(btn, cellIdx / cols);
            Grid.SetColumn(btn, cellIdx % cols);
            paperGrid.Children.Add(btn);
            cellIdx++;
        }

        foreach (var cp in customList)
        {
            var cpItem = cp;
            var pc = YuyueCoreMethods.PaperColors(cpItem);
            bool on = resolvedPaper.Custom?.Id == cpItem.Id;
            var tileCard = MakePaperTileCard(CustomPaperDialog.ParseHexColor(pc.Bg, Colors.Beige), CustomPaperDialog.ParseHexColor(pc.Ink, Colors.Black), p.Line, cpItem.Name, on);

            var ctx = new MenuFlyout();
            var editMi = new MenuFlyoutItem { Text = "调整颜色…" };
            editMi.Click += async (_, _) =>
            {
                var edited = await CustomPaperDialog.ShowEditorAsync(RootGrid.XamlRoot, _library, cpItem);
                if (edited is not null)
                {
                    bool save = PaperAppearance.PickCustom(_library, edited, rs, out var updated);
                    if (save) Update("纸色", edited.Name, _ => updated);
                    else { App.CurrentApp.BroadcastReaderSettingsChanged(); RenderInspector("reading"); }
                }
                RenderDynamicPage("reading");
            };
            var dupMi = new MenuFlyoutItem { Text = "复制一张" };
            dupMi.Click += (_, _) =>
            {
                var copy = CustomPaperDialog.DuplicateCustomPaper(_library, cpItem);
                if (copy is not null)
                {
                    bool save = PaperAppearance.PickCustom(_library, copy, rs, out var updated);
                    if (save) Update("纸色", copy.Name, _ => updated);
                    else { App.CurrentApp.BroadcastReaderSettingsChanged(); RenderInspector("reading"); }
                }
                RenderDynamicPage("reading");
            };
            var delMi = new MenuFlyoutItem { Text = "删除…", Foreground = ThemeHelper.Brush(p.Danger) };
            delMi.Click += (_, _) =>
            {
                try { _library.Core.DeleteCustomPaper(cpItem.Id); } catch { }
                PaperAppearance.Forget(_library, cpItem.Id);
                if (rs.CustomPaper == cpItem.Id)
                    Update("纸色", "纸本", s => s with { Paper = Paper.Kami, CustomPaper = null });
                else
                {
                    App.CurrentApp.BroadcastReaderSettingsChanged();
                    RenderInspector("reading");
                }
                RenderDynamicPage("reading");
            };
            ctx.Items.Add(editMi);
            ctx.Items.Add(dupMi);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(delMi);

            var btn = new Button { Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), UseSystemFocusVisuals = false, Content = tileCard, ContextFlyout = ctx };
            btn.Click += (_, _) =>
            {
                bool save = PaperAppearance.PickCustom(_library, cpItem, rs, out var updated);
                if (save) Update("纸色", cpItem.Name, _ => updated);
                else { App.CurrentApp.BroadcastReaderSettingsChanged(); RenderInspector("reading"); }
                RenderDynamicPage("reading");
            };
            Grid.SetRow(btn, cellIdx / cols);
            Grid.SetColumn(btn, cellIdx % cols);
            paperGrid.Children.Add(btn);
            cellIdx++;
        }

        // + 新建自定义纸张磁贴（1:1 settings-reading/2.png unified white tile）
        var addPaperInner = new StackPanel { Spacing = 5 };
        addPaperInner.Children.Add(new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Inset),
            Child = YYIcons.Make("plus", 12, ThemeHelper.Brush(p.T2)),
        });
        addPaperInner.Children.Add(new TextBlock { Text = "新建", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Center });
        var addPaperCard = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(5, 6, 5, 6),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Child = addPaperInner,
        };
        var addPaperBtn = new Button { Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), UseSystemFocusVisuals = false, Content = addPaperCard };
        addPaperBtn.Click += async (_, _) =>
        {
            var created = await CustomPaperDialog.ShowEditorAsync(RootGrid.XamlRoot, _library);
            if (created is not null)
            {
                bool save = PaperAppearance.PickCustom(_library, created, rs, out var updated);
                if (save) Update("纸色", created.Name, _ => updated);
                else { App.CurrentApp.BroadcastReaderSettingsChanged(); RenderInspector("reading"); }
                RenderDynamicPage("reading");
            }
        };
        Grid.SetRow(addPaperBtn, cellIdx / cols);
        Grid.SetColumn(addPaperBtn, cellIdx % cols);
        paperGrid.Children.Add(addPaperBtn);
        if (_library.Prefs.GlobalEinkMode)
        {
            paperGrid.Opacity = 0.45;
            paperGrid.IsHitTestVisible = false;
        }
        paperWrap.Children.Add(paperGrid);

        // 2. 字体「永」字磁贴网格 + 已固定字体 + 「更多字体」字样册 (FontBook)
        var fontWrap = new StackPanel { Spacing = 10, Padding = new Thickness(16, 14, 16, 14) };
        fontWrap.Children.Add(new TextBlock { Text = "字体", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });

        var tileEntries = new List<(string Id, string Label, Font? Kind, string? Face, string Fam)>
        {
            ("system", "系统", Font.Sans, null, YYFontFamily.Sans.WindowsFamily()),
            ("serif", "宋体", Font.Serif, null, YYFontFamily.Serif.WindowsFamily()),
            ("kai", "楷体", Font.Kai, null, YYFontFamily.Kaiti.WindowsFamily()),
            ("wenkai", _library.Fonts.IsDownloading("wenkai") ? "下载中…" : "霞鹜文楷", Font.WenKai, null, _library.Fonts.ResolveXamlFontSource("wenkai", YYFontFamily.Wenkai.WindowsFamily())),
        };

        // Append user-pinned fonts from FontBook (up to 4 extra pinned tiles)
        foreach (string pinId in _library.Prefs.PinnedFontIds.Take(4))
        {
            if (string.IsNullOrWhiteSpace(pinId) || tileEntries.Any(e => e.Id == pinId)) continue;
            if (pinId.StartsWith("file:", StringComparison.Ordinal))
            {
                string fn = pinId[5..];
                string disp = _library.Fonts.GetFontFileDisplayName(fn);
                string fam = _library.Fonts.ResolveXamlFontSource(pinId, YYFontFamily.Serif.WindowsFamily());
                tileEntries.Add((pinId, disp, Font.Serif, pinId, fam));
            }
            else if (pinId.StartsWith("sys:", StringComparison.Ordinal))
            {
                string fam = FontShelfService.CleanRegistryFontFamilyName(pinId[4..]);
                if (string.IsNullOrEmpty(fam)) continue;
                tileEntries.Add((pinId, fam, Font.Serif, $"local:{fam}", $"{fam}, {YYFontFamily.Serif.WindowsFamily()}"));
            }
            else if (_library.Fonts.Catalog.FirstOrDefault(c => c.Id == pinId) is { } pk)
            {
                string fallbackFam = pk.Kind == FontKind.Song ? YYFontFamily.Serif.WindowsFamily() : YYFontFamily.Sans.WindowsFamily();
                string fam = _library.Fonts.ResolveXamlFontSource(pk.Id, fallbackFam);
                tileEntries.Add((pinId, _library.Fonts.IsDownloading(pk.Id) ? "下载中…" : pk.Name, pk.Kind == FontKind.Song ? Font.Serif : Font.Sans, pk.Id, fam));
            }
        }

        int fontCols = 5;
        int fontTotal = tileEntries.Count + 1;
        int fontRows = (fontTotal + fontCols - 1) / fontCols;
        var fontGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (int i = 0; i < fontCols; i++) fontGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < fontRows; r++) fontGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var resolvedFontPreset = PaperAppearance.Resolve(_library, rs).Preset;
        bool defaultFontSans = resolvedFontPreset is Paper.Plain or Paper.Dark;
        bool matchedTile = false;
        for (int i = 0; i < tileEntries.Count; i++)
        {
            var fc = tileEntries[i];
            bool on = string.IsNullOrEmpty(fc.Face)
                ? (string.IsNullOrEmpty(rs.FontFace) && (rs.Font == fc.Kind || (rs.Font is null && fc.Id == (defaultFontSans ? "system" : "serif"))))
                : (string.Equals(rs.FontFace, fc.Face, StringComparison.OrdinalIgnoreCase) ||
                   (fc.Face.StartsWith("local:", StringComparison.Ordinal) && string.Equals(rs.FontFace, fc.Face[6..], StringComparison.OrdinalIgnoreCase)));
            if (on) matchedTile = true;

            var tile = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            bool needsDownload = fc.Id == "wenkai" && !_library.Fonts.IsPackInstalled("wenkai") && !_library.Fonts.IsDownloading("wenkai");
            if (needsDownload)
                tile.Children.Add(new TextBlock { Text = "↓ 28 MB", FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, -2) });
            tile.Children.Add(new TextBlock { Text = "永", FontFamily = new FontFamily(fc.Fam), FontSize = 20, Foreground = ThemeHelper.Brush(needsDownload ? p.T3 : p.T1), HorizontalAlignment = HorizontalAlignment.Center });
            tile.Children.Add(new TextBlock { Text = fc.Label, FontSize = 10.5, FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2), HorizontalAlignment = HorizontalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });

            var fb = new Button
            {
                Height = 64,
                Padding = new Thickness(4, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(on ? tint.Accent : p.Line),
                BorderThickness = new Thickness(on ? 1.5 : 0.75),
                UseSystemFocusVisuals = false,
                Content = tile,
            };
            var fCapture = fc;
            fb.Click += async (_, _) =>
            {
                var catPack = _library.Fonts.Catalog.FirstOrDefault(c => c.Id == fCapture.Id);
                if (catPack is not null && !_library.Fonts.IsPackInstalled(catPack.Id))
                {
                    try { await _library.Fonts.DownloadPackAsync(catPack.Id); }
                    catch (Exception ex) { await ShowInfoDialogAsync("字体下载失败", ex.Message); return; }
                }
                Update("字体", fCapture.Label, s => s with { Font = fCapture.Kind, FontFace = fCapture.Face });
                RenderDynamicPage("reading");
            };
            Grid.SetRow(fb, i / fontCols);
            Grid.SetColumn(fb, i % fontCols);
            fontGrid.Children.Add(fb);
        }

        // 最后一格：更多字体（1:1 settings-reading/2.png unified pure white tile）
        bool extraFontOn = !matchedTile && !string.IsNullOrEmpty(rs.FontFace);
        var moreSp = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        moreSp.Children.Add(new Border
        {
            Width = 28,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Child = new TextBlock { Text = "永", FontFamily = new FontFamily("SimSun"), FontSize = 13, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        moreSp.Children.Add(new TextBlock { Text = "更多字体", FontSize = 10.5, FontWeight = extraFontOn ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = ThemeHelper.Brush(extraFontOn ? p.T1 : p.T2), HorizontalAlignment = HorizontalAlignment.Center });

        var moreFontBtn = new Button
        {
            Height = 64,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(extraFontOn ? tint.Accent : p.Line),
            BorderThickness = new Thickness(extraFontOn ? 1.5 : 0.75),
            UseSystemFocusVisuals = false,
            Content = moreSp,
        };
        moreFontBtn.Click += async (_, _) => await ShowSettingsFontBookAsync(Update);
        Grid.SetRow(moreFontBtn, tileEntries.Count / fontCols);
        Grid.SetColumn(moreFontBtn, tileEntries.Count % fontCols);
        fontGrid.Children.Add(moreFontBtn);
        fontWrap.Children.Add(fontGrid);

        // 3. 字号与行距
        var fsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var fsLabel = new TextBlock { Text = $"{(int)Math.Round(rs.FontSize)}", FontSize = 12.5, Width = 24, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        var fsSlider = new Slider { Minimum = 12, Maximum = 34, StepFrequency = 1, Value = Math.Clamp(rs.FontSize, 12, 34), Width = 176 };
        fsSlider.ValueChanged += (_, e) =>
        {
            int v = (int)Math.Round(e.NewValue);
            if ((int)Math.Round(rs.FontSize) == v) return;
            fsLabel.Text = v.ToString();
            Update("字号", v.ToString(), s => s with { FontSize = v });
        };
        fsPanel.Children.Add(fsSlider);
        fsPanel.Children.Add(fsLabel);

        string curLh = rs.LineHeight is <= 1.7 ? "tight" : rs.LineHeight is >= 2.05 ? "loose" : "normal";
        var lhSeg = MakeSegments(
            [("tight", "紧"), ("normal", "适中"), ("loose", "松")],
            curLh,
            key =>
            {
                double? lh = key switch { "tight" => 1.6, "loose" => 2.2, _ => null };
                Update("行距", key switch { "tight" => "紧", "loose" => "松", _ => "适中" }, s => s with { LineHeight = lh });
                RenderDynamicPage("reading");
            },
            width: 164);

        DynamicPageHost.Children.Add(MakeCard(
            paperWrap,
            fontWrap,
            MakeRow("字号", null, fsPanel),
            MakeRow("行距", null, lhSeg)));

        // 4. 翻页、双栏、对齐、缩进、标点挤压、全局墨水屏模式
        var modeSeg = MakeSegments(
            [(LayoutMode.Paginated, "左右翻页"), (LayoutMode.Scrolled, "上下滚动")],
            rs.Mode,
            m => { Update("翻页方式", m == LayoutMode.Scrolled ? "上下滚动" : "左右翻页", s => s with { Mode = m }); RenderDynamicPage("reading"); },
            width: 172);

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("翻页方式", null, modeSeg),
            MakeRow("宽窗口下双栏显示", "左右并排显示两页", MakeSwitch(_library.Prefs.TwoColumns, v =>
            {
                _library.Prefs.TwoColumns = v;
                _library.SavePrefs();
                App.CurrentApp.BroadcastReaderSettingsChanged();
                RecordChange("双栏显示", v ? "开" : "关",
                    undo: () => { _library.Prefs.TwoColumns = !v; _library.SavePrefs(); App.CurrentApp.BroadcastReaderSettingsChanged(); },
                    redo: () => { _library.Prefs.TwoColumns = v; _library.SavePrefs(); App.CurrentApp.BroadcastReaderSettingsChanged(); },
                    oldValue: v ? "关" : "开");
            })),
            MakeRow("两端对齐", null, MakeSwitch(rs.Justify ?? true, v => Update("两端对齐", v ? "开" : "关", s => s with { Justify = v }))),
            MakeRow("首行缩进两字", null, MakeSwitch(rs.Indent ?? true, v => Update("首行缩进", v ? "开" : "关", s => s with { Indent = v }))),
            MakeRow("中文标点挤压", "收紧连续标点的留白", MakeSwitch(rs.Compress, v => Update("标点挤压", v ? "开" : "关", s => s with { Compress = v }))),
            MakeRow("全局墨水屏模式", "改用纯白纯黑无纹理纸面并关闭动画，不改动同步纸色", MakeSwitch(_library.Prefs.GlobalEinkMode, v =>
            {
                _library.Prefs.GlobalEinkMode = v;
                _library.SavePrefs();
                App.CurrentApp.BroadcastReaderSettingsChanged();
                RecordChange("全局墨水屏模式", v ? "开" : "关",
                    undo: () => { _library.Prefs.GlobalEinkMode = !v; _library.SavePrefs(); App.CurrentApp.BroadcastReaderSettingsChanged(); RenderDynamicPage("reading"); RenderInspector("reading"); },
                    redo: () => { _library.Prefs.GlobalEinkMode = v; _library.SavePrefs(); App.CurrentApp.BroadcastReaderSettingsChanged(); RenderDynamicPage("reading"); RenderInspector("reading"); },
                    oldValue: v ? "关" : "开");
                RenderDynamicPage("reading");
                RenderInspector("reading");
            })),
            MakeRow("封面与插图转为灰度", "彩墨屏可关闭以保留原色", new ContentControl
            {
                Opacity = _library.Prefs.GlobalEinkMode ? 1.0 : 0.45,
                IsEnabled = _library.Prefs.GlobalEinkMode,
                Content = MakeSwitch(_library.Prefs.GlobalEinkGray, v =>
                {
                    _library.Prefs.GlobalEinkGray = v;
                    _library.SavePrefs();
                    App.CurrentApp.BroadcastReaderSettingsChanged();
                    RecordChange("墨水屏灰度", v ? "开" : "关",
                        undo: () => { _library.Prefs.GlobalEinkGray = !v; _library.SavePrefs(); App.CurrentApp.BroadcastReaderSettingsChanged(); },
                        redo: () => { _library.Prefs.GlobalEinkGray = v; _library.SavePrefs(); App.CurrentApp.BroadcastReaderSettingsChanged(); },
                        oldValue: v ? "关" : "开");
                }),
            })));

        // 5. 沉浸式阅读（补齐「隐藏划线与想法」「隐藏书伴提示与难词注释」，1:1 MacSettings.swift:1008-1023）
        DynamicPageHost.Children.Add(MakeHeading("沉浸式阅读"));
        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("进入与退出", "全屏隐藏界面，专注书页正文", new TextBlock { Text = "F11 或 Esc", FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) }),
            MakeRow("打开书籍时自动进入", null, MakeSwitch(_library.Prefs.ImmersiveAutoEnter, v =>
            {
                _library.Prefs.ImmersiveAutoEnter = v;
                _library.SavePrefs();
                RecordChange("自动沉浸阅读", v ? "开" : "关",
                    undo: () => { _library.Prefs.ImmersiveAutoEnter = !v; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.ImmersiveAutoEnter = v; _library.SavePrefs(); });
            })),
            MakeRow("底部保留进度细线", null, MakeSwitch(_library.Prefs.ImmersiveKeepLine, v =>
            {
                _library.Prefs.ImmersiveKeepLine = v;
                _library.SavePrefs();
                RecordChange("沉浸进度细线", v ? "开" : "关",
                    undo: () => { _library.Prefs.ImmersiveKeepLine = !v; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.ImmersiveKeepLine = v; _library.SavePrefs(); });
            })),
            MakeRow("隐藏划线与想法", "退出沉浸后恢复显示", MakeSwitch(_library.Prefs.ImmersiveHideMarks, v =>
            {
                _library.Prefs.ImmersiveHideMarks = v;
                _library.SavePrefs();
                RecordChange("隐藏划线与想法", v ? "开" : "关",
                    undo: () => { _library.Prefs.ImmersiveHideMarks = !v; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.ImmersiveHideMarks = v; _library.SavePrefs(); });
            })),
            MakeRow("隐藏书伴提示与难词注释", null, MakeSwitch(_library.Prefs.ImmersiveHideHints, v =>
            {
                _library.Prefs.ImmersiveHideHints = v;
                _library.SavePrefs();
                RecordChange("隐藏提示与注释", v ? "开" : "关",
                    undo: () => { _library.Prefs.ImmersiveHideHints = !v; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.ImmersiveHideHints = v; _library.SavePrefs(); });
            })),
            MakeRow("关闭翻页动画", "直接切换整页，无滑动过渡", MakeSwitch(_library.Prefs.ImmersiveNoAnimation, v =>
            {
                _library.Prefs.ImmersiveNoAnimation = v;
                _library.SavePrefs();
                RecordChange("关闭翻页动画", v ? "开" : "关",
                    undo: () => { _library.Prefs.ImmersiveNoAnimation = !v; _library.SavePrefs(); },
                    redo: () => { _library.Prefs.ImmersiveNoAnimation = v; _library.SavePrefs(); });
            }))));
    }

    private async Task ShowSettingsFontBookAsync(Action<string, string, Func<ReaderSettings, ReaderSettings>> updateFn)
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        ContentDialog? dlgRef = null;
        string activeTab = "catalog"; // catalog | system | custom
        string specimen = "天地玄黄，宇宙洪荒。明月松间照，清泉石上流。";
        var sysFonts = _library.Fonts.DiscoverSystemCjkFonts();

        var specimenBox = new TextBox
        {
            Text = specimen,
            PlaceholderText = "输入自定义试排样字…",
            FontSize = 12,
        };
        var tabHost = new ContentControl();
        var list = new StackPanel { Spacing = 8 };

        Button MakePinBtn(string pinId, Action refresh)
        {
            bool pinned = _library.Prefs.PinnedFontIds.Contains(pinId);
            var pb = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = pinned ? "已固定" : "固定",
                Height = 25,
                MinHeight = 25,
                FontSize = 11,
                Foreground = ThemeHelper.Brush(pinned ? tint.Accent : p.T2),
            };
            pb.Click += (_, _) =>
            {
                if (_library.Prefs.PinnedFontIds.Contains(pinId))
                    _library.Prefs.PinnedFontIds.Remove(pinId);
                else
                    _library.Prefs.PinnedFontIds.Add(pinId);
                _library.SavePrefs();
                RenderDynamicPage("reading");
                refresh();
            };
            return pb;
        }

        void RebuildFontBook()
        {
            tabHost.Content = MakeSegments(
                [("catalog", "推荐开源"), ("system", $"系统字体 ({sysFonts.Count})"), ("custom", $"已导入 ({_library.Fonts.GetCustomFontFiles().Count})")],
                activeTab,
                t => { activeTab = t; RebuildFontBook(); });

            list.Children.Clear();
            string sampleText = string.IsNullOrWhiteSpace(specimenBox.Text) ? "明月松间照，清泉石上流。" : specimenBox.Text.Trim();

            if (activeTab == "catalog")
            {
                foreach (var pack in _library.Fonts.Catalog)
                {
                    var pk = pack;
                    bool inst = _library.Fonts.IsPackInstalled(pk.Id);
                    bool dl = _library.Fonts.IsDownloading(pk.Id);
                    string fallbackPreviewFam = pk.Id switch
                    {
                        "wenkai" => YYFontFamily.Wenkai.WindowsFamily(),
                        "notosans" => YYFontFamily.Sans.WindowsFamily(),
                        _ => YYFontFamily.Serif.WindowsFamily(),
                    };
                    string previewFam = _library.Fonts.ResolveXamlFontSource(pk.Id, fallbackPreviewFam);

                    var card = new StackPanel { Spacing = 6 };
                    var top = new Grid { ColumnSpacing = 10 };
                    top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var meta = new StackPanel { Spacing = 1 };
                    meta.Children.Add(new TextBlock { Text = pk.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                    ulong totalMb = 0;
                    foreach (var f in pk.Files) totalMb += f.Bytes;
                    string sizeNote = totalMb > 0 ? $" · {totalMb / 1048576.0:0.1} MB" : "";
                    meta.Children.Add(new TextBlock { Text = (inst ? "已安装在本机" : $"开源正文字体 ({pk.License}){sizeNote}"), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                    top.Children.Add(meta);

                    var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                    if (pk.Id != "wenkai") acts.Children.Add(MakePinBtn(pk.Id, RebuildFontBook));
                    if (dl)
                    {
                        var cancelBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "取消", FontSize = 11, Foreground = ThemeHelper.Brush(p.Danger) };
                        cancelBtn.Click += (_, _) =>
                        {
                            _library.Fonts.CancelDownload(pk.Id);
                            RebuildFontBook();
                        };
                        acts.Children.Add(cancelBtn);
                    }
                    else if (inst)
                    {
                        var delBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "删除", FontSize = 11, Foreground = ThemeHelper.Brush(p.Danger) };
                        delBtn.Click += (_, _) =>
                        {
                            _library.Fonts.DeletePack(pk.Id);
                            RebuildFontBook();
                        };
                        acts.Children.Add(delBtn);
                    }
                    var useBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources[inst ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"],
                        Content = dl ? $"下载中 {(int)Math.Round(_library.Fonts.GetProgress(pk.Id) * 100)}%" : (inst ? "选用" : "下载并选用"),
                        IsEnabled = !dl,
                        Height = 25,
                        MinHeight = 25,
                        FontSize = 11,
                    };
                    useBtn.Click += async (_, _) =>
                    {
                        if (!inst)
                        {
                            useBtn.IsEnabled = false;
                            useBtn.Content = "下载中…";
                            try { await _library.Fonts.DownloadPackAsync(pk.Id); }
                            catch (Exception ex) { dlgRef?.Hide(); await ShowInfoDialogAsync("下载失败", ex.Message); return; }
                        }
                        updateFn("字体", pk.Name, s => pk.Id == "wenkai" ? s with { Font = Font.WenKai, FontFace = null } : s with { Font = pk.Kind == FontKind.Song ? Font.Serif : Font.Sans, FontFace = pk.Id });
                        dlgRef?.Hide();
                        RenderDynamicPage("reading");
                    };
                    acts.Children.Add(useBtn);
                    Grid.SetColumn(acts, 1);
                    top.Children.Add(acts);
                    card.Children.Add(top);

                    card.Children.Add(new TextBlock
                    {
                        Text = sampleText,
                        FontFamily = new FontFamily(previewFam),
                        FontSize = 14,
                        Foreground = ThemeHelper.Brush(p.T2),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1,
                    });

                    list.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(11),
                        Background = ThemeHelper.Brush(p.Card),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(12, 10, 12, 10),
                        Child = card,
                    });
                }
            }
            else if (activeTab == "system")
            {
                foreach (var sf in sysFonts)
                {
                    var entry = sf;
                    var card = new StackPanel { Spacing = 5 };
                    var top = new Grid { ColumnSpacing = 8 };
                    top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    top.Children.Add(new TextBlock { Text = entry.DisplayName, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });

                    var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    acts.Children.Add(MakePinBtn($"sys:{entry.FamilyName}", RebuildFontBook));
                    var useBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "选用", Height = 25, MinHeight = 25, FontSize = 11 };
                    useBtn.Click += (_, _) =>
                    {
                        updateFn("字体", entry.DisplayName, s => s with { Font = Font.Serif, FontFace = $"local:{entry.FamilyName}" });
                        dlgRef?.Hide();
                        RenderDynamicPage("reading");
                    };
                    acts.Children.Add(useBtn);
                    Grid.SetColumn(acts, 1);
                    top.Children.Add(acts);
                    card.Children.Add(top);

                    card.Children.Add(new TextBlock
                    {
                        Text = sampleText,
                        FontFamily = new FontFamily($"{entry.FamilyName}, {YYFontFamily.Serif.WindowsFamily()}"),
                        FontSize = 14,
                        Foreground = ThemeHelper.Brush(p.T2),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxLines = 1,
                    });

                    list.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(10),
                        Background = ThemeHelper.Brush(p.Card),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(12, 8, 12, 8),
                        Child = card,
                    });
                }
            }
            else
            {
                var customs = _library.Fonts.GetCustomFontFiles();
                if (customs.Count == 0)
                {
                    list.Children.Add(new TextBlock
                    {
                        Text = "尚未导入本地字体。点击下方按钮可导入任意 .ttf / .otf / .ttc / .woff2 字体文件。",
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.T3),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(4, 14, 4, 14),
                    });
                }
                else
                {
                    foreach (var fn in customs)
                    {
                        string fileName = fn;
                        string dispName = _library.Fonts.GetFontFileDisplayName(fileName);
                        string? realFam = _library.Fonts.GetFontFileFamily(fileName);
                        string faceKey = $"file:{fileName}";
                        string previewFam = _library.Fonts.ResolveXamlFontSource(faceKey, YYFontFamily.Serif.WindowsFamily());

                        var card = new StackPanel { Spacing = 5 };
                        var top = new Grid { ColumnSpacing = 8 };
                        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var meta = new StackPanel { Spacing = 1 };
                        meta.Children.Add(new TextBlock { Text = dispName, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                        string subLine = !string.IsNullOrEmpty(realFam) && !string.Equals(realFam, dispName, StringComparison.OrdinalIgnoreCase)
                            ? $"{realFam} · {fileName}"
                            : fileName;
                        meta.Children.Add(new TextBlock { Text = subLine, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                        top.Children.Add(meta);

                        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                        acts.Children.Add(MakePinBtn(faceKey, RebuildFontBook));
                        var delBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "删除", FontSize = 11, Foreground = ThemeHelper.Brush(p.Danger) };
                        delBtn.Click += (_, _) =>
                        {
                            _library.Fonts.DeleteCustomFont(fileName);
                            _library.Prefs.PinnedFontIds.Remove(faceKey);
                            _library.SavePrefs();
                            RenderDynamicPage("reading");
                            RebuildFontBook();
                        };
                        var useBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "选用", Height = 25, MinHeight = 25, FontSize = 11 };
                        useBtn.Click += (_, _) =>
                        {
                            updateFn("字体", dispName, s => s with { Font = Font.Serif, FontFace = faceKey });
                            dlgRef?.Hide();
                            RenderDynamicPage("reading");
                        };
                        acts.Children.Add(delBtn);
                        acts.Children.Add(useBtn);
                        Grid.SetColumn(acts, 1);
                        top.Children.Add(acts);
                        card.Children.Add(top);

                        card.Children.Add(new TextBlock
                        {
                            Text = sampleText,
                            FontFamily = new FontFamily(previewFam),
                            FontSize = 14,
                            Foreground = ThemeHelper.Brush(p.T2),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxLines = 1,
                        });

                        list.Children.Add(new Border
                        {
                            CornerRadius = new CornerRadius(10),
                            Background = ThemeHelper.Brush(p.Card),
                            BorderBrush = ThemeHelper.Brush(p.Line),
                            BorderThickness = new Thickness(1),
                            Padding = new Thickness(12, 8, 12, 8),
                            Child = card,
                        });
                    }
                }
            }
        }

        specimenBox.TextChanged += (_, _) => RebuildFontBook();
        RebuildFontBook();

        var importLocalBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "+ 导入本地字体文件 (.ttf / .otf / .ttc / .woff2)…" };
        importLocalBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
            picker.FileTypeFilter.Add(".ttf");
            picker.FileTypeFilter.Add(".otf");
            picker.FileTypeFilter.Add(".ttc");
            picker.FileTypeFilter.Add(".woff2");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            try
            {
                string fn = _library.Fonts.ImportFontFile(f.Path);
                string disp = _library.Fonts.GetFontFileDisplayName(fn);
                updateFn("字体", disp, s => s with { FontFace = $"file:{fn}" });
                dlgRef?.Hide();
                RenderDynamicPage("reading");
            }
            catch (Exception ex) { dlgRef?.Hide(); await ShowInfoDialogAsync("导入字体失败", ex.Message); }
        };

        var outer = new StackPanel
        {
            Width = 510,
            Spacing = 10,
            Children =
            {
                tabHost,
                specimenBox,
                new ScrollViewer { MaxHeight = 330, Content = list },
                importLocalBtn,
            },
        };

        dlgRef = new ContentDialog
        {
            Title = "字样册",
            Content = outer,
            CloseButtonText = "关闭",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dlgRef.ShowAsync();
    }

    // ---------- 3. 外文（1:1 MacForeignSettings.swift:10-191） ----------

    private void BuildForeignPage()
    {
        var p = CurrentPalette;

        DynamicPageHost.Children.Add(MakeHeading("查词"));
        var builtInLabel = new TextBlock
        {
            Text = _library.Ecdict.IsAvailable ? "✓ 已就绪" : "未就绪",
            FontSize = 12,
            Foreground = ThemeHelper.Brush(_library.Ecdict.IsAvailable ? p.Ok : p.T3),
        };
        var accentSeg = MakeSegments(
            [("uk", "英音"), ("us", "美音")],
            _library.Prefs.ForeignAccent,
            ac =>
            {
                _library.Prefs.ForeignAccent = ac;
                _library.SavePrefs();
                RecordChange("英文发音", ac == "uk" ? "英音" : "美音");
                RenderDynamicPage("foreign");
            },
            width: 124);

        // 1:1 MacForeignSettings.swift:24-34：查词卡仅 3 行（移除多余点词查译行）
        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("系统词典", "点英文词即查，无需联网", builtInLabel),
            MakeRow("查词时自动发音", null, MakeSwitch(_library.Prefs.ForeignSpeakOnLookup, v => { _library.Prefs.ForeignSpeakOnLookup = v; _library.SavePrefs(); RecordChange("自动发音", v ? "开" : "关"); }), compact: true),
            MakeRow("英文发音", null, accentSeg, compact: true)));

        DynamicPageHost.Children.Add(MakeHeading("MDict 词典"));
        var dicts = _library.GetDictionaries();
        var mdxRows = new List<UIElement>();
        for (int di = 0; di < dicts.Count; di++)
        {
            int curIdx = di;
            var d = dicts[di];
            string did = d.Id;
            string dTitle = d.Title;
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (dicts.Count > 1)
            {
                var upBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "↑", Width = 24, Height = 24, FontSize = 11, IsEnabled = curIdx > 0 };
                upBtn.Click += (_, _) =>
                {
                    var ids = dicts.Select(x => x.Id).ToList();
                    (ids[curIdx - 1], ids[curIdx]) = (ids[curIdx], ids[curIdx - 1]);
                    try { _library.OrderDictionaries(ids.ToArray()); } catch { }
                    RenderDynamicPage("foreign");
                };
                var downBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "↓", Width = 24, Height = 24, FontSize = 11, IsEnabled = curIdx < dicts.Count - 1 };
                downBtn.Click += (_, _) =>
                {
                    var ids = dicts.Select(x => x.Id).ToList();
                    (ids[curIdx + 1], ids[curIdx]) = (ids[curIdx], ids[curIdx + 1]);
                    try { _library.OrderDictionaries(ids.ToArray()); } catch { }
                    RenderDynamicPage("foreign");
                };
                right.Children.Add(upBtn);
                right.Children.Add(downBtn);
            }
            right.Children.Add(MakeSwitch(d.Enabled, on => _library.SetDictionaryEnabled(did, on)));
            var rowEl = MakeRow(d.Title, $"{d.Entries:N0} 个词条", right);
            var ctx = new MenuFlyout();
            var rmItem = new MenuFlyoutItem { Text = "移除…", Foreground = ThemeHelper.Brush(p.Danger) };
            rmItem.Click += async (_, _) =>
            {
                var dlg = new ContentDialog { Title = $"移除词典「{dTitle}」？", Content = "移除后可随时重新导入 .mdx 文件。", PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, XamlRoot = RootGrid.XamlRoot };
                if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                {
                    _library.RemoveDictionary(did);
                    RenderDynamicPage("foreign");
                }
            };
            ctx.Items.Add(rmItem);
            rowEl.ContextFlyout = ctx;
            mdxRows.Add(rowEl);
        }
        var pickMdxBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Content = "选择文件…" };
        pickMdxBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
            picker.FileTypeFilter.Add(".mdx");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            try { _library.ImportDictionary(f.Path); RenderDynamicPage("foreign"); }
            catch (Exception ex) { await ShowInfoDialogAsync("词典导入失败", ex.Message); }
        };
        mdxRows.Add(MakeRow("导入词典（.mdx）", "按列表顺序优先查词，右键可移除", pickMdxBtn));
        DynamicPageHost.Children.Add(MakeCard(mdxRows.ToArray()));

        DynamicPageHost.Children.Add(MakeHeading("难词注释"));
        bool glossOn = _library.Prefs.ForeignGlossEnabled && _library.Prefs.GlossLevel > 0;
        var glossSw = MakeSwitch(glossOn, on =>
        {
            _library.Prefs.ForeignGlossEnabled = on;
            if (on && _library.Prefs.GlossLevel == 0) _library.Prefs.GlossLevel = 3;
            _library.SavePrefs();
            App.CurrentApp.BroadcastForeignPrefsChanged();
            RecordChange("难词注释", on ? "开" : "关");
            RenderDynamicPage("foreign");
            RenderInspector("foreign");
        });
        var lvlSeg = MakeSegments<byte>(
            [(2, "四级"), (3, "六级"), (4, "考研"), (5, "雅思"), (6, "GRE")],
            _library.Prefs.GlossLevel == 0 ? (byte)3 : _library.Prefs.GlossLevel,
            lv =>
            {
                _library.Prefs.ForeignGlossEnabled = true;
                _library.Prefs.GlossLevel = lv;
                _library.SavePrefs();
                App.CurrentApp.BroadcastForeignPrefsChanged();
                RenderDynamicPage("foreign");
                RenderInspector("foreign");
            },
            width: 220);
        lvlSeg.IsHitTestVisible = glossOn && !_library.Prefs.GlossVocabOnly;
        lvlSeg.Opacity = glossOn && !_library.Prefs.GlossVocabOnly ? 1.0 : 0.45;

        var vocabOnlySw = MakeSwitch(_library.Prefs.GlossVocabOnly, on =>
        {
            _library.Prefs.GlossVocabOnly = on;
            _library.SavePrefs();
            App.CurrentApp.BroadcastForeignPrefsChanged();
            RenderDynamicPage("foreign");
            RenderInspector("foreign");
        });
        vocabOnlySw.IsEnabled = glossOn;

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("难词行间释义", "阅读英文书时，在较难词汇上方标注简短中文词义", glossSw),
            MakeRow("词汇难度", "等级越高，仅标注更生僻的词", lvlSeg),
            MakeRow("仅标注生词本中的词", null, vocabOnlySw, compact: true)));

        DynamicPageHost.Children.Add(MakeHeading("生词"));
        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("在书中标记生词", "「在学」生词在所有书中以虚线标出，标为「已掌握」后不再标记", MakeSwitch(_library.Prefs.ForeignMarkVocab, v =>
            {
                _library.Prefs.ForeignMarkVocab = v;
                _library.SavePrefs();
                App.CurrentApp.BroadcastForeignPrefsChanged();
                RenderInspector("foreign");
            }))));

        // 1:1 MacForeignSettings.swift:58-68：翻译卡片
        DynamicPageHost.Children.Add(MakeHeading("翻译"));
        var trStatus = new TextBlock
        {
            Text = _library.Companion.IsConfigured ? "✓ 已就绪" : "按需连接书伴或本机 Ollama",
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(_library.Companion.IsConfigured ? p.Ok : p.T3),
        };
        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("系统与本地翻译", "本地 Ollama 离线运行不上传原文；供整页译文与双击整句翻译使用", trStatus)));
    }

    // ---------- 4. 朗读（1:1 MacListeningSettings.swift & MacVoicePicker.swift） ----------

    private void BuildListeningPage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        var lv = _library.LiveVoice;

        if (!_localVoiceProbeStarted)
        {
            _localVoiceProbeStarted = true;
            _ = Task.Run(async () =>
            {
                bool changed = await LocalVoiceProbe.Shared.ScanAllAsync(_library);
                if (changed)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_activePage == "listening")
                        {
                            RenderDynamicPage("listening");
                            RenderInspector("listening");
                        }
                    });
                }
            });
        }

        DynamicPageHost.Children.Add(MakeHeading("声音来源", "点一个，进去听它的声音"));

        var srcStack = new StackPanel { Spacing = 2, Padding = new Thickness(4) };
        var allOpts = AudioPlayer.GetAllVoiceOptions(_library);
        var curZh = allOpts.FirstOrDefault(o => o.Id == _library.Prefs.VoiceId || o.Id == "sys:" + _library.Prefs.VoiceId);

        void AddSourceRow(string key, string tileKey, string title, string sub, string? status, bool connected, bool pinned, string? tagBadge = null, Color? tagCol = null, bool addDivider = true, Action? customClick = null)
        {
            if (addDivider && srcStack.Children.Count > 0)
                srcStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });

            bool sel = _focusedVoiceSource == key;
            var g = new Grid { ColumnSpacing = 12, MinHeight = 52, Padding = new Thickness(12, 8, 12, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(YYChrome.MakeServiceTile(tileKey, IsDark, 34));

            var mid = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            titleLine.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(customClick is not null ? p.T2 : p.T1) });
            if (!string.IsNullOrEmpty(tagBadge))
                titleLine.Children.Add(MakeStatusPill(tagBadge, tagCol ?? p.Ok));
            mid.Children.Add(titleLine);
            mid.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            var stWrap = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (pinned && !string.IsNullOrEmpty(status))
                stWrap.Children.Add(YYIcons.Make("pin", 10, ThemeHelper.Brush(p.T3), filled: true));
            if (!string.IsNullOrEmpty(status))
                stWrap.Children.Add(new TextBlock { Text = status, FontSize = 11, Foreground = ThemeHelper.Brush(connected ? p.T2 : p.T3), MaxWidth = 148, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            stWrap.Children.Add(YYIcons.Make("chevron-right", 10, ThemeHelper.Brush(p.T3)));
            Grid.SetColumn(stWrap, 2);
            g.Children.Add(stWrap);

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                Background = sel ? ThemeHelper.Brush(Color.FromArgb(14, p.T1.R, p.T1.G, p.T1.B)) : new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = g,
            };
            string sk = key;
            btn.Click += (_, _) =>
            {
                if (customClick is not null) { customClick(); return; }
                _focusedVoiceSource = sk;
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            srcStack.Children.Add(btn);
        }

        // 1:1 settings-listening/1.png: clean 5-row main card (Kokoro, Edge/Online, System, + configured cloud, + 添加声音来源)
        var ko = _library.Kokoro;
        string koSub = ko.IsInstalled
            ? $"{ko.GetAllSpeakers().Count} 个中英自然音色 · 跨端轻量离线 · 免费免联网"
            : (ko.State == KokoroStateKind.Downloading
                ? $"正在下载轻量离线语音 {(int)Math.Round(ko.DownloadProgress * 100)}% · 免费免联网"
                : $"跨端轻量中英自然人声 · 点进去一键下载（约 {KokoroVoiceModel.TotalMb} MB）");
        string? koStatus = ko.IsInstalled ? "已就绪" : (ko.State == KokoroStateKind.Downloading ? $"{(int)Math.Round(ko.DownloadProgress * 100)}%" : null);
        AddSourceRow("kokoro", "kokoro", "语阅离线语音 (Kokoro)", koSub, koStatus, ko.IsInstalled, ko.IsInstalled, "推荐", p.Ok, addDivider: false);

        bool edgeOn = _library.CloudVoices.IsConfigured(CloudTtsProvider.Edge);
        string? edgeStatus = edgeOn && curZh is not null && curZh.Id.StartsWith("cloud.edge.", StringComparison.OrdinalIgnoreCase)
            ? $"中文书 · {curZh.Label}"
            : null;
        AddSourceRow("edge", "edge", "微软 Edge", "22 个微软神经音色 · 免 Key 直连，已读缓存在本机",
            edgeStatus, edgeOn, edgeOn && !ko.IsInstalled && edgeStatus is not null, "免 Key", p.Ok, addDivider: true);

        bool freeOn = _library.CloudVoices.IsConfigured(CloudTtsProvider.Free);
        string? freeStatus = freeOn && curZh is not null && curZh.Id.StartsWith("cloud.free.", StringComparison.OrdinalIgnoreCase)
            ? $"中文书 · {curZh.Label}"
            : null;
        AddSourceRow("free", "online", "在线自然语音", "免 Key 直连中英神经人声 · 开箱即听，已读缓存在本机",
            freeStatus, freeOn, freeOn && !ko.IsInstalled && freeStatus is not null, "免 Key", p.Ok, addDivider: true);

        AddSourceRow("pc", "system", "这台电脑 · 系统语音", "系统自带合成机械音 · 体验较差，仅作断网未下载时的兜底",
            null, false, false, "不推荐", Color.FromArgb(255, 183, 121, 31), addDivider: true);

        // Show only configured, auto-discovered, or currently focused cloud/local providers inline; all others live cleanly in "+ 添加声音来源"
        foreach (var prov in CloudVoiceService.AllProviders.Where(x => x is not CloudTtsProvider.Edge and not CloudTtsProvider.Free))
        {
            string pk = CloudVoiceEntry.ProviderKey(prov);
            bool cfg = _library.CloudVoices.IsConfigured(prov);
            bool autoLive = _library.CloudVoices.IsAutoDiscovered(prov);
            if (!cfg && !autoLive && _focusedVoiceSource != pk) continue;
            string status = autoLive && !_library.Prefs.CloudVoiceEnabled.Contains(pk) ? "已自动发现" : (cfg ? "已连接" : "未连接");
            AddSourceRow(pk, pk, CloudVoiceService.ProviderDisplayName(prov, _library.Prefs), CloudVoiceService.ProviderHelp(prov), status, cfg, false, CloudVoiceService.ProviderBadge(prov), p.T3, addDivider: true);
        }

        AddSourceRow("__add__", "add", "添加声音来源", "火山豆包、Fish Audio、小米 MiMo（限时免费）、硅基流动、本机 VoiceStudio…",
            null, false, false, null, null, addDivider: true, customClick: () => _ = ShowAddVoiceSourceDialogAsync());

        DynamicPageHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(IsDark ? Color.FromArgb(16, 255, 255, 255) : p.Line),
            BorderThickness = new Thickness(0.5),
            Child = srcStack,
        });
        DynamicPageHost.Children.Add(MakeNote("推荐下载「语阅离线语音 (Kokoro)」跨端断网畅听，或直接使用免 Key 的「在线自然语音」。系统自带合成音机械感较重（不推荐），仅作断网兜底。"));

        // 1:1 settings-listening/1.png: 语速 card right under 声音来源
        DynamicPageHost.Children.Add(MakeHeading("语速", "读书窗口里调的也是这个"));
        var speedGrid = new Grid { ColumnSpacing = 10, Padding = new Thickness(16, 12, 16, 12) };
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        speedGrid.Children.Add(new TextBlock { Text = "慢", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
        var speedSlider = new Slider { Minimum = 0.7, Maximum = 2.0, StepFrequency = 0.1, Value = Math.Clamp(_library.Prefs.SpeechRate, 0.7, 2.0) };
        var speedValTb = new TextBlock { Text = $"{_library.Prefs.SpeechRate:0.0}× ⇕", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        speedSlider.ValueChanged += (_, e) =>
        {
            double r = Math.Round(e.NewValue, 1);
            if (Math.Abs(r - _library.Prefs.SpeechRate) < 0.01) return;
            _library.Prefs.SpeechRate = r;
            _library.SavePrefs();
            speedValTb.Text = $"{r:0.0}× ⇕";
            App.CurrentApp.BroadcastVoiceChanged();
        };
        Grid.SetColumn(speedSlider, 1);
        speedGrid.Children.Add(speedSlider);
        var fastTb = new TextBlock { Text = "快", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(fastTb, 2);
        speedGrid.Children.Add(fastTb);
        Grid.SetColumn(speedValTb, 3);
        speedGrid.Children.Add(speedValTb);
        DynamicPageHost.Children.Add(MakeCard(speedGrid));

        // 广播剧工坊与声音克隆（放在语速下方，滚动可见，首屏与 macOS 1:1 齐整）
        var dramaHead = MakeHeading("离线语音与广播剧工坊", "只在这台电脑上跑，不传云端");
        dramaHead.Margin = new Thickness(14, 136, 14, -4);
        DynamicPageHost.Children.Add(dramaHead);
        var dramaRows = new List<UIElement>();
        var koModel = _library.Kokoro;
        if (koModel.IsInstalled)
        {
            var delKoBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "移除", Foreground = ThemeHelper.Brush(p.Danger) };
            delKoBtn.Click += (_, _) => { koModel.DeleteInstalledModel(); App.CurrentApp.BroadcastVoiceChanged(); };
            dramaRows.Add(MakeRow("Kokoro 轻量离线旁白", $"已就绪 · {koModel.GetAllSpeakers().Count} 个中英自然旁白 · 约 {KokoroVoiceModel.TotalMb} MB", delKoBtn));
        }
        else if (koModel.State == KokoroStateKind.Downloading)
        {
            var cancelKoBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "取消" };
            cancelKoBtn.Click += (_, _) => koModel.CancelDownload();
            dramaRows.Add(MakeRow("Kokoro 轻量离线旁白", koModel.StatusMessage, cancelKoBtn));
        }
        else
        {
            var dlKoBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = koModel.State == KokoroStateKind.Failed ? "继续下载" : $"下载 ({KokoroVoiceModel.TotalMb} MB)" };
            dlKoBtn.Click += (_, _) => _ = koModel.DownloadAsync();
            dramaRows.Add(MakeRow("Kokoro 轻量离线旁白", "16 个中英自然旁白 · 低内存秒出声 · 完全离线（支持断点续传）", dlKoBtn));
        }
        if (lv.IsReady)
        {
            int cardCount = lv.GetBuiltinCards().Count;
            var delBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "移除", Foreground = ThemeHelper.Brush(p.Danger) };
            delBtn.Click += (_, _) => { lv.DeleteInstalledModels(); App.CurrentApp.BroadcastVoiceChanged(); };
            dramaRows.Add(MakeRow("精做的声音（离线自然人声）", $"已就绪 · 内置 {cardCount} 个中英双语自然音色", delBtn));
        }
        else if (lv.State == LiveVoiceStateKind.Downloading)
        {
            var cancelBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "取消" };
            cancelBtn.Click += (_, _) => lv.CancelDownload();
            dramaRows.Add(MakeRow("精做的声音（离线自然人声）", lv.StatusMessage, cancelBtn));
        }
        else
        {
            var dlBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "下载 (160 MB)" };
            dlBtn.Click += (_, _) => _ = lv.DownloadAsync();
            dramaRows.Add(MakeRow("精做的声音（离线自然人声）", "本地 ONNX 24kHz 离线模型，支持多角色广播剧朗读", dlBtn));
        }
        dramaRows.Add(MakeRow("给我的手机提前做好", "接着电源时，附近手机正在听的书自动往后精做 10 章", MakeSwitch(_library.Prefs.DramaPreRenderPhone, v => { _library.Prefs.DramaPreRenderPhone = v; _library.SavePrefs(); })));
        DynamicPageHost.Children.Add(MakeCard(dramaRows.ToArray()));

        // 我的克隆声音（1:1 CloneVoiceView.swift & ClonedVoice.swift）
        DynamicPageHost.Children.Add(MakeHeading("克隆的声音", "录 6–15 秒人声生成专属声音卡，仅保存在本机"));
        var cloneRows = new List<UIElement>();
        foreach (var cloned in lv.GetClonedCards())
        {
            var cc = cloned;
            var rowActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var tryBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "▶ 试听", Height = 25, MinHeight = 25, FontSize = 11 };
            tryBtn.Click += async (_, _) =>
            {
                tryBtn.IsEnabled = false;
                try { await AudioPlayer.PreviewVoiceAsync(_library, cc.Id, _library.Prefs.SpeechRate, "春天的风轻轻吹过窗台，翻开书页的声音格外安静。"); }
                finally { tryBtn.IsEnabled = true; }
            };
            var detailClBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "情绪 / 详情", Height = 25, MinHeight = 25, FontSize = 11 };
            detailClBtn.Click += async (_, _) =>
            {
                await VoiceCloneAndDramaDialogs.ShowClonedCardDetailDialogAsync(this, RootGrid.XamlRoot, _library, cc.Id, () =>
                {
                    RenderDynamicPage("listening");
                    RenderInspector("listening");
                });
            };
            var delClBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "删除", Height = 25, MinHeight = 25, FontSize = 11, Foreground = ThemeHelper.Brush(p.Danger) };
            delClBtn.Click += (_, _) =>
            {
                VoiceCloneAndDramaDialogs.DeleteClonedCard(_library, cc.Id);
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            rowActs.Children.Add(tryBtn);
            rowActs.Children.Add(detailClBtn);
            rowActs.Children.Add(delClBtn);
            int styleCount = Math.Max(0, cc.Clips.Length - 1);
            string subDesc = styleCount > 0 ? $"本机克隆声音卡 · 已录 {styleCount}/6 种情绪" : "本机克隆声音卡";
            cloneRows.Add(MakeRow($"✦ {cc.Name}", subDesc, rowActs, compact: true));
        }
        var cloneBtnSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        cloneBtnSp.Children.Add(YYIcons.Make("mic", 12, ThemeHelper.Brush(tint.Accent)));
        cloneBtnSp.Children.Add(new TextBlock { Text = "录一段声音…", VerticalAlignment = VerticalAlignment.Center });
        var newCloneBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = cloneBtnSp };
        newCloneBtn.Click += async (_, _) =>
        {
            var saved = await VoiceCloneAndDramaDialogs.ShowCloneVoiceDialogAsync(this, RootGrid.XamlRoot, _library);
            if (saved is not null)
            {
                RecordChange("克隆声音", saved.Name);
                RenderDynamicPage("listening");
                RenderInspector("listening");
            }
        };
        cloneRows.Add(MakeRow("新建声音卡", "用麦克风录音或导入音频文件克隆音色", newCloneBtn));
        DynamicPageHost.Children.Add(MakeCard(cloneRows.ToArray()));
    }

    private void BuildListeningInspector(YYPalette p, YYTintColors tint)
    {
        if (string.IsNullOrEmpty(_focusedVoiceSource))
        {
            // 1:1 settings-listening/1.png: active Chinese voice preview card + source/cost card
            var options = AudioPlayer.GetAllVoiceOptions(_library);
            var activeOpt = options.FirstOrDefault(o => o.Id == _library.Prefs.VoiceId || o.Id == "sys:" + _library.Prefs.VoiceId) ?? options.FirstOrDefault();
            static string CleanVoiceDisplayName(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return "知声 · 清晰播报";
                int idx = 0;
                while (idx < raw.Length && !char.IsLetterOrDigit(raw[idx])) idx++;
                string s = idx < raw.Length ? raw[idx..].Trim() : raw.Trim();
                int paren = s.IndexOfAny(['(', '（']);
                if (paren > 2) s = s[..paren].Trim();
                return string.IsNullOrEmpty(s) ? "知声 · 清晰播报" : s;
            }
            string voiceLabel = CleanVoiceDisplayName(activeOpt?.Label ?? "知声 · 清晰播报");
            string sourceLabel = activeOpt is not null && activeOpt.Id.StartsWith("kokoro:", StringComparison.OrdinalIgnoreCase)
                ? "语阅离线语音 (Kokoro)"
                : (activeOpt is not null && activeOpt.Id.StartsWith("cloud.edge.", StringComparison.OrdinalIgnoreCase)
                    ? "微软 Edge"
                    : (activeOpt is not null && activeOpt.Id.StartsWith("cloud.free.", StringComparison.OrdinalIgnoreCase)
                        ? "在线自然语音"
                        : (activeOpt is not null && activeOpt.Id.StartsWith("cloud.", StringComparison.OrdinalIgnoreCase)
                            ? "云端语音"
                            : "这台电脑 · 系统语音")));
            string costLabel = sourceLabel switch
            {
                "语阅离线语音 (Kokoro)" => "免费 · 不联网",
                "微软 Edge" => "免 Key 免费 · 微软 Edge 神经语音",
                "在线自然语音" => "免 Key 免费 · 语阅在线自然语音",
                "这台电脑 · 系统语音" => "免费 · 不联网",
                _ => "按服务商计费",
            };
            string initialChar = voiceLabel.Length > 0 ? voiceLabel[..1] : "知";

            InspectorHost.Children.Add(new TextBlock
            {
                Text = "在念你的中文书",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T2),
            });

            var headRow = new Grid { ColumnSpacing = 10 };
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            headRow.Children.Add(new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(18),
                Background = ThemeHelper.Brush(tint.AccentSoft),
                Child = new TextBlock
                {
                    Text = initialChar,
                    FontSize = 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });

            var headInfo = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            headInfo.Children.Add(new TextBlock { Text = voiceLabel, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            headInfo.Children.Add(new TextBlock { Text = sourceLabel, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(headInfo, 1);
            headRow.Children.Add(headInfo);

            var selPill = new Border
            {
                CornerRadius = new CornerRadius(12),
                Height = 24,
                Padding = new Thickness(9, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = ThemeHelper.Brush(Color.FromArgb(26, p.Ok.R, p.Ok.G, p.Ok.B)),
                Child = new TextBlock { Text = "✓ 已选择", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.Ok), VerticalAlignment = VerticalAlignment.Center },
            };
            Grid.SetColumn(selPill, 2);
            headRow.Children.Add(selPill);
            InspectorHost.Children.Add(headRow);

            // White audition card: dark play circle + sleek muted waveform + "3\"" + quote
            var sampleText = DailyQuote.Current(_library).Text;
            if (string.IsNullOrWhiteSpace(sampleText)) sampleText = "赌书消得泼茶香，当时只道是寻常。";

            var playErr = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.Danger), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var playRow = new Grid { ColumnSpacing = 10 };
            playRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            playRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            playRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var playCircle = new Button
            {
                Width = 34,
                Height = 34,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(17),
                Background = ThemeHelper.Brush(p.T1),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = YYIcons.Make("play", 12, ThemeHelper.Brush(p.Card)),
            };
            playCircle.Click += async (_, _) =>
            {
                playErr.Visibility = Visibility.Collapsed;
                playCircle.IsEnabled = false;
                try { await AudioPlayer.PreviewVoiceAsync(_library, _library.Prefs.GetVoiceForLanguage("zh"), _library.Prefs.SpeechRate, sampleText); }
                catch (Exception ex) { playErr.Text = ex.Message; playErr.Visibility = Visibility.Visible; }
                finally { playCircle.IsEnabled = true; }
            };
            playRow.Children.Add(playCircle);

            var waveRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, VerticalAlignment = VerticalAlignment.Center };
            int[] waveH = [4, 7, 5, 11, 8, 15, 6, 12, 17, 9, 13, 6, 11, 15, 8, 10, 6, 13, 9, 7, 5, 8, 4];
            var waveColor = IsDark ? Color.FromArgb(110, 232, 224, 212) : Color.FromArgb(255, 220, 217, 212);
            foreach (int h in waveH)
                waveRow.Children.Add(new Border { Width = 2.5, Height = h, CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(waveColor), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(waveRow, 1);
            playRow.Children.Add(waveRow);

            var durTb = new TextBlock { Text = "3\"", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(durTb, 2);
            playRow.Children.Add(durTb);

            var audStack = new StackPanel { Spacing = 12 };
            audStack.Children.Add(playRow);
            audStack.Children.Add(new TextBlock
            {
                Text = sampleText,
                FontSize = 12.5,
                Foreground = ThemeHelper.Brush(p.T1),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
            });
            audStack.Children.Add(playErr);

            InspectorHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(14, 12, 14, 12),
                Child = audStack,
            });

            InspectorHost.Children.Add(MakeCard(
                MakeRow("来自", null, new TextBlock { Text = sourceLabel, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2) }, compact: true),
                MakeRow("费用", null, new TextBlock { Text = costLabel, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2) }, compact: true)));
            return;
        }

        if (_focusedVoiceSource == "kokoro")
        {
            var ko = _library.Kokoro;
            var kHead = new Grid { ColumnSpacing = 10 };
            kHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            kHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            kHead.Children.Add(YYChrome.MakeServiceTile("kokoro", IsDark, 32));
            var kMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            kMeta.Children.Add(new TextBlock { Text = "语阅离线语音 (Kokoro)", FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            kMeta.Children.Add(new TextBlock { Text = "跨端轻量离线模型 · Apache-2.0", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
            Grid.SetColumn(kMeta, 1);
            kHead.Children.Add(kMeta);
            InspectorHost.Children.Add(kHead);

            if (!ko.IsInstalled && ko.State != KokoroStateKind.Downloading)
            {
                // 1:1 settings-voice-mac/2.png clean info card + full-width download button + note
                InspectorHost.Children.Add(MakeCard(
                    MakeRow("Kokoro 引擎", null, MakeStatusPill($"待下载 · {KokoroVoiceModel.TotalMb} MB", Color.FromArgb(255, 183, 121, 31)), compact: true),
                    MakeRow("角色与克隆", null, new TextBlock { Text = "按需准备", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2) }, compact: true),
                    MakeRow("费用", null, new TextBlock { Text = "免费 · 不联网", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2) }, compact: true)));

                var dlSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                dlSp.Children.Add(YYIcons.Make("arrow-down-circle", 14, ThemeHelper.Brush(p.Ok)));
                dlSp.Children.Add(new TextBlock { Text = $"一键下载 Kokoro 离线语音 ({KokoroVoiceModel.TotalMb} MB)", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.Ok) });
                var dlCardBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = 40,
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    UseSystemFocusVisuals = false,
                    Content = dlSp,
                };
                dlCardBtn.Click += (_, _) => _ = ko.DownloadAsync();
                InspectorHost.Children.Add(dlCardBtn);

                InspectorHost.Children.Add(new TextBlock
                {
                    Text = "Kokoro-82M v1.1 Int8 跨端统一使用 ONNX Runtime 离线合成，无需联网即可自然朗读中英文书籍。",
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T3),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 17,
                    Margin = new Thickness(4, 2, 4, 0),
                });
                return;
            }

            // KokoroPrepBanner when installed or downloading
            var bannerGrid = new Grid { ColumnSpacing = 8 };
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var bInfo = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            if (ko.IsInstalled)
            {
                bInfo.Children.Add(new TextBlock { Text = $"{ko.GetAllSpeakers().Count} 个中英自然旁白已就绪", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.Ok) });
                bInfo.Children.Add(new TextBlock { Text = "点下方任意音色设为默认，或直接试听", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                var rmBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "移除", Height = 26, MinHeight = 26, FontSize = 11, Foreground = ThemeHelper.Brush(p.Danger) };
                rmBtn.Click += (_, _) => { ko.DeleteInstalledModel(); App.CurrentApp.BroadcastVoiceChanged(); };
                Grid.SetColumn(rmBtn, 1);
                bannerGrid.Children.Add(rmBtn);
            }
            else if (ko.State == KokoroStateKind.Downloading)
            {
                int pct = (int)Math.Round(ko.DownloadProgress * 100);
                string? pending = ko.GetPendingVoiceName("zh") ?? ko.GetPendingVoiceName("en");
                bInfo.Children.Add(new TextBlock { Text = $"正在下载 Kokoro 轻量离线语音 {pct}%", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
                bInfo.Children.Add(new TextBlock
                {
                    Text = !string.IsNullOrEmpty(pending)
                        ? $"下好后将自动启用「{pending}」· 下载期间可先听在线自然语音"
                        : "下载完成后永久免联网朗读 · 期间可先听在线自然语音",
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    TextWrapping = TextWrapping.Wrap,
                });
                bInfo.Children.Add(new ProgressBar { Value = pct, Maximum = 100, Height = 4, Margin = new Thickness(0, 4, 0, 0) });
                var cancelBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "取消", Height = 26, MinHeight = 26, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                cancelBtn.Click += (_, _) => ko.CancelDownload();
                Grid.SetColumn(cancelBtn, 1);
                bannerGrid.Children.Add(cancelBtn);
            }
            else if (ko.State == KokoroStateKind.Failed)
            {
                bInfo.Children.Add(new TextBlock { Text = "Kokoro 语音包未完成下载（支持断点续传）", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                bInfo.Children.Add(new TextBlock { Text = "先用「免 Key 在线语音」同样好听", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                var retryBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "继续下载", Height = 26, MinHeight = 26, FontSize = 11, Foreground = ThemeHelper.Brush(p.Danger), VerticalAlignment = VerticalAlignment.Center };
                retryBtn.Click += (_, _) => _ = ko.DownloadAsync();
                Grid.SetColumn(retryBtn, 1);
                bannerGrid.Children.Add(retryBtn);
            }
            else
            {
                bInfo.Children.Add(new TextBlock { Text = $"下载 Kokoro 跨端轻量语音 · 约 {KokoroVoiceModel.TotalMb} MB", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                bInfo.Children.Add(new TextBlock { Text = "16 个中英自然旁白 · 低内存秒出声 · 完全离线", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                var dlBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "一键下载", Height = 26, MinHeight = 26, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                dlBtn.Click += (_, _) => _ = ko.DownloadAsync();
                Grid.SetColumn(dlBtn, 1);
                bannerGrid.Children.Add(dlBtn);
            }
            bannerGrid.Children.Add(bInfo);
            InspectorHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 10, 12, 10),
                Child = bannerGrid,
            });

            var speakers = ko.GetAllSpeakers();
            var listStack = new StackPanel { Spacing = 4 };
            foreach (var (langKey, headerText) in new[] { ("zh", "中文旁白"), ("en", "English Narrators") })
            {
                var group = speakers.Where(s => s.Lang.StartsWith(langKey, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (group.Length == 0) continue;
                listStack.Children.Add(new TextBlock
                {
                    Text = $"{headerText} ({group.Length})",
                    FontSize = 10.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(4, 6, 4, 2),
                });
                foreach (var sp in group)
                {
                    var capSp = sp;
                    string capLang = langKey;
                    string activeId = capLang == "en" ? _library.Prefs.VoiceIdEn : _library.Prefs.VoiceId;
                    bool isCur = activeId == capSp.Id;

                    var row = new Grid { ColumnSpacing = 6, Padding = new Thickness(8, 5, 6, 5) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                    meta.Children.Add(new TextBlock
                    {
                        Text = isCur ? $"✓ {capSp.Name}" : capSp.Name,
                        FontSize = 12,
                        FontWeight = isCur ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                        Foreground = ThemeHelper.Brush(isCur ? tint.Accent : p.T1),
                    });
                    meta.Children.Add(new TextBlock { Text = capSp.Summary, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    row.Children.Add(meta);

                    var pickBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources[isCur ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"],
                        Content = isCur ? "使用中" : (ko.IsInstalled ? "选用" : "下载并选用"),
                        Height = 24,
                        MinHeight = 24,
                        Padding = new Thickness(8, 0, 8, 0),
                        FontSize = 10.5,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    pickBtn.Click += (_, _) =>
                    {
                        if (ko.IsInstalled)
                        {
                            _library.Prefs.SetVoiceForLanguage(capLang, capSp.Id);
                            _library.SavePrefs();
                            RecordChange(capLang == "en" ? "英文默认声音" : "中文默认声音", capSp.Name);
                            App.CurrentApp.BroadcastVoiceChanged();
                            RenderDynamicPage("listening");
                            RenderInspector("listening");
                        }
                        else
                        {
                            ko.QueueSelection(capSp.Id, capLang);
                            ShowFlashBanner($"开始下载 Kokoro，完成后自动启用「{capSp.Name}」");
                        }
                    };
                    Grid.SetColumn(pickBtn, 1);
                    row.Children.Add(pickBtn);

                    if (ko.IsInstalled)
                    {
                        var tryBtn = new Button
                        {
                            Style = (Style)Application.Current.Resources["YYIconButtonStyle"],
                            Width = 24,
                            Height = 24,
                            Padding = new Thickness(0),
                            Content = YYIcons.Make("play", 10, ThemeHelper.Brush(p.T2)),
                            VerticalAlignment = VerticalAlignment.Center,
                        };
                        tryBtn.Click += async (_, _) =>
                        {
                            tryBtn.IsEnabled = false;
                            string sample = capLang == "en"
                                ? "It is a truth universally acknowledged, that a good book waits patiently on the desk."
                                : DailyQuote.Current(_library).Text;
                            try { await AudioPlayer.PreviewVoiceAsync(_library, capSp.Id, _library.Prefs.SpeechRate, sample); }
                            finally { tryBtn.IsEnabled = true; }
                        };
                        Grid.SetColumn(tryBtn, 2);
                        row.Children.Add(tryBtn);
                    }

                    listStack.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Background = isCur ? ThemeHelper.Brush(tint.AccentSoft) : new SolidColorBrush(Colors.Transparent),
                        Child = row,
                    });
                }
            }
            InspectorHost.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                Child = listStack,
            });
            return;
        }

        if (_focusedVoiceSource is "pc" or "neural" or "system")
        {
            var options = AudioPlayer.GetAllVoiceOptions(_library);
            InspectorHost.Children.Add(new TextBlock
            {
                Text = "在念你的书",
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
            });

            if (!_library.Kokoro.IsInstalled)
            {
                var dlBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = _library.Kokoro.State == KokoroStateKind.Downloading
                        ? _library.Kokoro.StatusMessage
                        : $"⬇ 一键下载 Kokoro 轻量离线语音 ({KokoroVoiceModel.TotalMb} MB)",
                };
                dlBtn.Click += (_, _) =>
                {
                    if (_library.Kokoro.State != KokoroStateKind.Downloading)
                        _ = _library.Kokoro.DownloadAsync();
                };
                InspectorHost.Children.Add(dlBtn);
            }

            // Separate Chinese default voice & English default voice (1:1 MacListeningSettings.swift)
            var zhCombo = new ComboBox { Header = "中文默认声音", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            var enCombo = new ComboBox { Header = "英文默认声音", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            int selZh = 0, selEn = 0;
            enCombo.Items.Add(new ComboBoxItem { Content = "自动匹配英文声线", Tag = "" });
            for (int i = 0; i < options.Count; i++)
            {
                zhCombo.Items.Add(new ComboBoxItem { Content = options[i].Label, Tag = options[i].Id });
                enCombo.Items.Add(new ComboBoxItem { Content = options[i].Label, Tag = options[i].Id });
                if (options[i].Id == _library.Prefs.VoiceId || options[i].Id == "sys:" + _library.Prefs.VoiceId) selZh = i;
                if (!string.IsNullOrEmpty(_library.Prefs.VoiceIdEn) && (options[i].Id == _library.Prefs.VoiceIdEn || options[i].Id == "sys:" + _library.Prefs.VoiceIdEn)) selEn = i + 1;
            }
            if (zhCombo.Items.Count > 0) zhCombo.SelectedIndex = selZh;
            enCombo.SelectedIndex = selEn;

            zhCombo.SelectionChanged += (_, _) =>
            {
                if (zhCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string vid)
                {
                    _library.Prefs.VoiceId = vid;
                    _library.SavePrefs();
                    RecordChange("中文默认声音", ci.Content?.ToString() ?? "");
                    App.CurrentApp.BroadcastVoiceChanged();
                    RenderDynamicPage("listening");
                }
            };
            enCombo.SelectionChanged += (_, _) =>
            {
                if (enCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string vid)
                {
                    _library.Prefs.VoiceIdEn = vid;
                    _library.SavePrefs();
                    RecordChange("英文默认声音", ci.Content?.ToString() ?? "");
                    App.CurrentApp.BroadcastVoiceChanged();
                }
            };

            var initialQuote = DailyQuote.Current(_library);
            var quoteHeader = new Grid { ColumnSpacing = 6 };
            quoteHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            quoteHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var quoteSourceText = new TextBlock
            {
                Text = string.IsNullOrEmpty(initialQuote.Source) ? "每日一言" : $"每日一言 · {initialQuote.Source}",
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            quoteHeader.Children.Add(quoteSourceText);

            var sampleBox = new TextBox
            {
                Text = initialQuote.Text,
                PlaceholderText = "想听他们念什么，写一句",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                CornerRadius = new CornerRadius(8),
                Background = ThemeHelper.Brush(p.Bg),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
            };

            var nextQuoteBtn = new Button
            {
                Content = "↻ 换一句",
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = ThemeHelper.Brush(p.T2),
            };
            nextQuoteBtn.Click += async (_, _) =>
            {
                nextQuoteBtn.IsEnabled = false;
                try
                {
                    var next = await DailyQuote.RefreshAsync(_library);
                    sampleBox.Text = next.Text;
                    quoteSourceText.Text = string.IsNullOrEmpty(next.Source) ? "每日一言" : $"每日一言 · {next.Source}";
                }
                finally { nextQuoteBtn.IsEnabled = true; }
            };
            Grid.SetColumn(nextQuoteBtn, 1);
            quoteHeader.Children.Add(nextQuoteBtn);
            _ = EnsureWindowsDailyQuoteAsync(sampleBox, quoteSourceText, initialQuote.Text);

            // Quick audition presets alongside DailyQuote
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            foreach (var (lbl, txt) in new[]
            {
                ("中文散文", "我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。"),
                ("古诗", "明月松间照，清泉石上流。竹喧归浣女，莲动下渔舟。"),
                ("English", "It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife."),
            })
            {
                string sampleTxt = txt;
                var pb = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Height = 22,
                    MinHeight = 22,
                    Padding = new Thickness(8, 0, 8, 0),
                    FontSize = 10.5,
                    Content = lbl,
                };
                pb.Click += (_, _) => sampleBox.Text = sampleTxt;
                pillRow.Children.Add(pb);
            }

            // 波形试听条
            var playErrText = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.Danger), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var playRow = new Grid { ColumnSpacing = 8 };
            playRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            playRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var playCircle = new Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(15),
                Background = ThemeHelper.Brush(tint.Black),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = YYIcons.Make("play", 11, ThemeHelper.Brush(tint.OnBlack)),
            };
            playCircle.Click += async (_, _) =>
            {
                string txt = string.IsNullOrWhiteSpace(sampleBox.Text) ? DailyQuote.Current(_library).Text : sampleBox.Text.Trim();
                bool isEn = txt.Length > 0 && txt.Count(c => c <= 127 && char.IsLetter(c)) > txt.Length / 2;
                string vid = isEn ? _library.Prefs.GetVoiceForLanguage("en") : _library.Prefs.GetVoiceForLanguage("zh");
                playErrText.Visibility = Visibility.Collapsed;
                playCircle.IsEnabled = false;
                try { await AudioPlayer.PreviewVoiceAsync(_library, vid, _library.Prefs.SpeechRate, txt); }
                catch (Exception ex) { playErrText.Text = ex.Message; playErrText.Visibility = Visibility.Visible; }
                finally { playCircle.IsEnabled = true; }
            };
            playRow.Children.Add(playCircle);

            var waveRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, VerticalAlignment = VerticalAlignment.Center };
            int[] heights = [6, 12, 9, 16, 11, 18, 8, 14, 19, 10, 15, 7, 13, 17, 9, 12, 8, 15, 11, 7];
            foreach (int h in heights)
                waveRow.Children.Add(new Border { Width = 3, Height = h, CornerRadius = new CornerRadius(1.5), Background = ThemeHelper.Brush(tint.Accent), Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(waveRow, 1);
            playRow.Children.Add(waveRow);

            var cardStack = new StackPanel { Spacing = 8 };
            cardStack.Children.Add(zhCombo);
            cardStack.Children.Add(enCombo);
            cardStack.Children.Add(playRow);
            cardStack.Children.Add(playErrText);
            cardStack.Children.Add(quoteHeader);
            cardStack.Children.Add(pillRow);
            cardStack.Children.Add(sampleBox);

            InspectorHost.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = ThemeHelper.Brush(p.Card), BorderBrush = ThemeHelper.Brush(p.Line), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = cardStack });
            InspectorHost.Children.Add(MakeCard(
                MakeRow("来自", null, new TextBlock { Text = "这台电脑", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2) }, compact: true),
                MakeRow("费用", null, new TextBlock { Text = "免费 · 不联网", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.Ok) }, compact: true)));
            return;
        }

        // All 16 Cloud & Local TTS Providers Inspector (1:1 CloudVoice.swift & MacListeningSettings.swift)
        if (!CloudVoiceEntry.TryParseProviderKey(_focusedVoiceSource, out var cloudProv))
            cloudProv = CloudTtsProvider.OpenAi;

        string pKey = CloudVoiceEntry.ProviderKey(cloudProv);
        string title = CloudVoiceService.ProviderDisplayName(cloudProv, _library.Prefs);
        bool isLocal = CloudVoiceService.IsLocalProvider(cloudProv);

        InspectorHost.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        InspectorHost.Children.Add(new TextBlock { Text = CloudVoiceService.ProviderHelp(cloudProv), FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });

        var stack = new StackPanel { Spacing = 10 };
        PasswordBox? kb = null;

        if (cloudProv == CloudTtsProvider.Edge)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "免注册免填 Key，直连微软 Edge 22 个中英日多语种神经音色（晓晓、云希、Ava 等）；已合成的句子自动缓存在本机。",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (cloudProv == CloudTtsProvider.Free)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "零配置免 Key 即用，自动调用魔搭 / Hugging Face 开源 Kokoro 在线推理服务；已合成的句子缓存在本机。",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            if (isLocal)
            {
                if (cloudProv == CloudTtsProvider.Local)
                {
                    var customNameBox = new TextBox
                    {
                        Header = "显示名称",
                        Text = _library.Prefs.CloudVoiceCustomNames.TryGetValue("local", out var cn) ? cn : "",
                        PlaceholderText = "例如：家里的 Kokoro",
                        FontSize = 12,
                    };
                    customNameBox.TextChanged += (_, _) =>
                    {
                        _library.Prefs.CloudVoiceCustomNames["local"] = customNameBox.Text.Trim();
                        _library.SavePrefs();
                        RenderDynamicPage("listening");
                    };
                    stack.Children.Add(customNameBox);
                }

                var urlBox = new TextBox
                {
                    Header = "服务地址 (OpenAI 兼容 /v1)",
                    Text = _library.CloudVoices.GetSavedUrl(cloudProv),
                    PlaceholderText = CloudVoiceService.DefaultLocalUrl(cloudProv),
                    FontSize = 12,
                };
                urlBox.TextChanged += (_, _) =>
                {
                    _library.Prefs.CloudVoiceBaseUrls[pKey] = urlBox.Text.Trim();
                    _library.SavePrefs();
                };
                stack.Children.Add(urlBox);

                var mdlBox = new TextBox
                {
                    Header = "模型名",
                    Text = _library.CloudVoices.GetSavedModel(cloudProv),
                    PlaceholderText = CloudVoiceService.DefaultLocalModel(cloudProv),
                    FontSize = 12,
                };
                mdlBox.TextChanged += (_, _) =>
                {
                    _library.Prefs.CloudVoiceModels[pKey] = mdlBox.Text.Trim();
                    _library.SavePrefs();
                };
                stack.Children.Add(mdlBox);
            }

            if (cloudProv == CloudTtsProvider.Volcengine)
            {
                var appBox = new TextBox
                {
                    Header = "火山引擎 AppID",
                    Text = _library.Secrets.Get("tts.volcengine.appid"),
                    PlaceholderText = "填写应用 AppID…",
                    FontSize = 12,
                };
                appBox.TextChanged += (_, _) =>
                {
                    _library.Secrets.Set("tts.volcengine.appid", appBox.Text.Trim());
                    RenderDynamicPage("listening");
                };
                stack.Children.Add(appBox);
            }

            kb = new PasswordBox
            {
                Header = isLocal
                    ? "API Key（局域网可选，本机可留空）"
                    : (cloudProv == CloudTtsProvider.Volcengine ? "Access Token（本机加密存储）" : "API Key（本机加密存储）"),
                Password = _library.CloudVoices.GetApiKey(cloudProv),
                PlaceholderText = CloudVoiceService.ProviderKeyHint(cloudProv),
            };
            kb.PasswordChanged += (_, _) =>
            {
                _library.CloudVoices.SetApiKey(cloudProv, kb.Password.Trim());
                RenderDynamicPage("listening");
            };
            stack.Children.Add(kb);
        }

        // 1-click Companion shared API key reuse (1:1 CloudVoice.swift companionSharedKey)
        if (!isLocal && kb is not null && string.IsNullOrEmpty(kb.Password) && _library.CloudVoices.GetCompanionSharedKey(cloudProv) is { Length: > 0 } sharedKey)
        {
            var reuseBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = $"一键复用「书伴」里已填的 {title} Key",
                FontSize = 11.5,
            };
            reuseBtn.Click += (_, _) =>
            {
                _library.CloudVoices.SetApiKey(cloudProv, sharedKey);
                kb.Password = sharedKey;
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            stack.Children.Add(reuseBtn);
        }

        if (cloudProv == CloudTtsProvider.Azure)
        {
            // Arbitrary Azure region input (instead of restricting to 4 regions)
            var regBox = new TextBox
            {
                Header = "服务区域 (Region)",
                Text = _library.Prefs.CloudVoiceAzureRegion,
                PlaceholderText = "例如：eastasia / southeastasia / eastus / japaneast",
                FontSize = 12,
            };
            regBox.TextChanged += (_, _) =>
            {
                _library.Prefs.CloudVoiceAzureRegion = regBox.Text.Trim();
                _library.SavePrefs();
            };
            stack.Children.Add(regBox);
        }

        var checkStatus = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap };
        var refreshBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = cloudProv is CloudTtsProvider.Edge or CloudTtsProvider.Free
                ? "测试连通并启用"
                : (isLocal ? "探测连接并启用" : "验证连接并刷新音色列表"),
        };
        refreshBtn.Click += async (_, _) =>
        {
            refreshBtn.IsEnabled = false;
            checkStatus.Text = "正在连接并拉取音色…";
            var (ok, _, msg) = await _library.CloudVoices.RefreshVoicesAsync(cloudProv);
            checkStatus.Text = msg;
            checkStatus.Foreground = ThemeHelper.Brush(ok ? p.Ok : p.Danger);
            refreshBtn.IsEnabled = true;
            if (ok)
            {
                App.CurrentApp.BroadcastVoiceChanged();
                RenderDynamicPage("listening");
                RenderInspector("listening");
            }
        };
        stack.Children.Add(refreshBtn);
        stack.Children.Add(checkStatus);

        var voices = _library.CloudVoices.GetVoices(cloudProv);
        if (voices.Count > 0 && _library.CloudVoices.IsConfigured(cloudProv))
        {
            var vCombo = new ComboBox { Header = $"设为中文默认音色（共 {voices.Count} 个）", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            int selV = 0;
            for (int i = 0; i < voices.Count; i++)
            {
                vCombo.Items.Add(new ComboBoxItem { Content = voices[i].Name, Tag = voices[i].FullId });
                if (voices[i].FullId == _library.Prefs.VoiceId) selV = i;
            }
            vCombo.SelectedIndex = selV;
            vCombo.SelectionChanged += (_, _) =>
            {
                if (vCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string fid)
                {
                    _library.Prefs.VoiceId = fid;
                    _library.SavePrefs();
                    App.CurrentApp.BroadcastVoiceChanged();
                    RenderDynamicPage("listening");
                }
            };
            stack.Children.Add(vCombo);

            var auditionCloudBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "▶ 试听所选音色" };
            auditionCloudBtn.Click += async (_, _) =>
            {
                string fid = (vCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? voices[0].FullId;
                auditionCloudBtn.IsEnabled = false;
                try { await AudioPlayer.PreviewVoiceAsync(_library, fid, _library.Prefs.SpeechRate, DailyQuote.Current(_library).Text); }
                catch (Exception ex) { checkStatus.Text = ex.Message; checkStatus.Foreground = ThemeHelper.Brush(p.Danger); }
                finally { auditionCloudBtn.IsEnabled = true; }
            };
            stack.Children.Add(auditionCloudBtn);
        }

        InspectorHost.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = ThemeHelper.Brush(p.Card), BorderBrush = ThemeHelper.Brush(p.Line), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = stack });

        if (cloudProv == CloudTtsProvider.Free && _library.CloudVoices.IsConfigured(CloudTtsProvider.Free))
        {
            var hideBtn = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Foreground = ThemeHelper.Brush(p.Danger), Content = "隐藏 在线自然语音" };
            hideBtn.Click += (_, _) =>
            {
                _library.Secrets.Set("tts.free.dismissed", "1");
                App.CurrentApp.BroadcastVoiceChanged();
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            InspectorHost.Children.Add(hideBtn);
        }
        else if (_library.CloudVoices.IsConfigured(cloudProv))
        {
            var discBtn = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Foreground = ThemeHelper.Brush(p.Danger), Content = $"断开 {title}" };
            discBtn.Click += (_, _) =>
            {
                _library.CloudVoices.SetApiKey(cloudProv, "");
                if (cloudProv == CloudTtsProvider.Volcengine) _library.Secrets.Set("tts.volcengine.appid", "");
                _library.Prefs.CloudVoiceEnabled.Remove(pKey);
                if (isLocal) _library.Prefs.CloudVoiceDismissed.Add(pKey);
                LocalVoiceProbe.Shared.Clear(cloudProv);
                _library.SavePrefs();
                App.CurrentApp.BroadcastVoiceChanged();
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            InspectorHost.Children.Add(discBtn);
        }
    }

    private async Task EnsureWindowsDailyQuoteAsync(TextBox sampleBox, TextBlock sourceLabel, string initialText)
    {
        var updated = await DailyQuote.EnsureTodayAsync(_library);
        if (updated.Text != initialText && sampleBox.Text == initialText)
        {
            sampleBox.Text = updated.Text;
            sourceLabel.Text = string.IsNullOrEmpty(updated.Source) ? "每日一言" : $"每日一言 · {updated.Source}";
        }
    }

    // ---------- 5. 书伴（1:1 MacCompanionSettings.swift & MacCompanionConnect.swift） ----------

    private string ShortProviderTitle(string id, string fallback) =>
        _library.Prefs.AiCustomProviderNames.TryGetValue(id, out var cname) && !string.IsNullOrWhiteSpace(cname)
            ? cname
            : id switch
            {
                "openrouter" => "OpenRouter",
                "claude" => "Claude (Anthropic)",
                "openai" => "OpenAI",
                "gemini" => "Gemini (Google)",
                "deepseek" => "DeepSeek",
                "dashscope" => "通义千问",
                "moonshot" => "Kimi",
                "zhipu" => "智谱 GLM",
                "volcengine" => "豆包",
                "siliconflow" => "硅基流动",
                "minimax" => "MiniMax",
                "xai" => "Grok",
                "mistral" => "Mistral",
                "groq" => "Groq",
                "magpie" => "magpie 网关",
                "ollama" => "Ollama（本机）",
                "lmstudio" => "LM Studio（本机）",
                "custom" => "自定义兼容服务",
                _ => fallback,
            };

    private AiProviderPreset ResolveCompanionPreset(string pid)
    {
        if (CompanionClient.FindPreset(pid) is { } found) return found;
        string cname = _library.Prefs.AiCustomProviderNames.TryGetValue(pid, out var n) && !string.IsNullOrWhiteSpace(n) ? n : "自定义服务";
        return new AiProviderPreset(pid, cname, "", "", false, Group: "key", KeyHint: "可不填");
    }

    private Border MakeServiceLogoTile(string id, double size = 30, string? customMark = null) =>
        YYChrome.MakeServiceTile(id == "free" ? "online" : id, IsDark, size);

    private string GetProviderModel(string pid, string defaultModel) =>
        _library.Prefs.AiProviderModels.TryGetValue(pid, out var m) && !string.IsNullOrWhiteSpace(m)
            ? m
            : (pid == _library.Prefs.AiProvider && !string.IsNullOrWhiteSpace(_library.Prefs.AiModel) ? _library.Prefs.AiModel : defaultModel);

    private string GetProviderUrl(string pid, string defaultUrl) =>
        _library.Prefs.AiProviderUrls.TryGetValue(pid, out var u) && !string.IsNullOrWhiteSpace(u)
            ? u
            : (pid == _library.Prefs.AiProvider && !string.IsNullOrWhiteSpace(_library.Prefs.AiBaseUrl) ? _library.Prefs.AiBaseUrl : defaultUrl);

    private void ActivateCompanionProvider(string pid)
    {
        var preset = ResolveCompanionPreset(pid);
        _library.Prefs.AiProvider = pid;
        _library.Prefs.AiModel = GetProviderModel(pid, preset.DefaultModel);
        _library.Prefs.AiBaseUrl = GetProviderUrl(pid, preset.BaseUrl);
        string pKey = _library.Secrets.Get($"ai.key.{pid}");
        _library.Secrets.Set("ai.apiKey", pKey);
        _library.SavePrefs();
    }

    private void BuildCompanionPage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        DynamicPageHost.Children.Add(MakeHeading("模型服务"));

        if (!_library.Prefs.ConfiguredAiProviders.Contains(_library.Prefs.AiProvider))
            _library.Prefs.ConfiguredAiProviders.Insert(0, _library.Prefs.AiProvider);

        var listStack = new StackPanel { Spacing = 2, Padding = new Thickness(4) };
        var configured = _library.Prefs.ConfiguredAiProviders
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ResolveCompanionPreset)
            .ToArray();

        for (int i = 0; i < configured.Length; i++)
        {
            if (i > 0) listStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
            var preset = configured[i];
            bool selected = _focusedCompanionId == preset.Id;
            bool inUse = _library.Prefs.AiProvider == preset.Id;

            string rawKey = _library.Secrets.Get($"ai.key.{preset.Id}");
            string masked = rawKey.Length > 8 ? $"{rawKey[..3]}••••{rawKey[^4..]}" : (rawKey.Length > 0 ? "••••" : "");
            string model = GetProviderModel(preset.Id, preset.DefaultModel);

            var rowGrid = new Grid { ColumnSpacing = 12, MinHeight = 46, Padding = new Thickness(12, 8, 12, 8) };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.Children.Add(MakeServiceLogoTile(preset.Id, 30));

            var mid = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            titleLine.Children.Add(new TextBlock { Text = ShortProviderTitle(preset.Id, preset.Name), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
            if (inUse) titleLine.Children.Add(MakeStatusPill("当前使用", p.Ok));
            mid.Children.Add(titleLine);

            string detail = preset.Id == "ollama"
                ? $"{model} · 本机离线运行"
                : $"{model}{(string.IsNullOrEmpty(masked) ? " · 未填 Key" : $" · Key {masked}")}";
            mid.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(mid, 1);
            rowGrid.Children.Add(mid);

            var chev = YYIcons.Make("chevron-right", 10.5, ThemeHelper.Brush(p.T3));
            Grid.SetColumn(chev, 2);
            rowGrid.Children.Add(chev);

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                Background = selected ? ThemeHelper.Brush(Color.FromArgb(16, p.T1.R, p.T1.G, p.T1.B)) : new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = rowGrid,
            };
            string pid = preset.Id;
            btn.Click += (_, _) =>
            {
                _focusedCompanionId = pid;
                _companionPickingModel = false;
                RenderDynamicPage("companion");
                RenderInspector("companion");
            };
            listStack.Children.Add(btn);
        }

        // + 添加服务商行（1:1 MacCompanionSettings.swift:56-68）
        listStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
        var addGrid = new Grid { ColumnSpacing = 12, MinHeight = 46, Padding = new Thickness(12, 8, 12, 8) };
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addGrid.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = ThemeHelper.Brush(tint.Soft), Child = YYIcons.Make("plus", 12.5, ThemeHelper.Brush(tint.Accent)) });
        var addMeta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        addMeta.Children.Add(new TextBlock { Text = "添加服务商…", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(tint.Accent) });
        addMeta.Children.Add(new TextBlock { Text = "Claude、OpenAI、DeepSeek、Gemini、Kimi、硅基流动、本机 Ollama…", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(addMeta, 1);
        addGrid.Children.Add(addMeta);

        var addBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            UseSystemFocusVisuals = false,
            Content = addGrid,
        };
        addBtn.Click += async (_, _) => await ShowAddProviderSheetAsync();
        listStack.Children.Add(addBtn);

        DynamicPageHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Child = listStack,
        });

        DynamicPageHost.Children.Add(MakeNote("API Key 仅保存在这台电脑上（Windows DPAPI 加密），不同步、不上传；调用费用由服务商直接结算。播客文稿生成同样使用此处所选模型。"));

        if (!_localCompanionProbeStarted)
        {
            _localCompanionProbeStarted = true;
            _ = Task.Run(async () =>
            {
                var found = await CompanionClient.ProbeLocalProvidersAsync();
                if (found.Count > 0)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _liveLocalCompanions = found;
                        if (_activePage == "companion") RenderDynamicPage("companion");
                    });
                }
            });
        }

        if (_liveLocalCompanions.Count > 0)
        {
            var localFoundRows = new List<UIElement>();
            foreach (var kv in _liveLocalCompanions)
            {
                string lpid = kv.Key;
                var lpreset = ResolveCompanionPreset(lpid);
                bool added = _library.Prefs.ConfiguredAiProviders.Contains(lpid);
                var useLocalBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = added ? "已添加" : "一键添加",
                    IsEnabled = !added,
                    Height = 25,
                    MinHeight = 25,
                    FontSize = 11,
                };
                useLocalBtn.Click += (_, _) =>
                {
                    bool wasNew = !_library.Prefs.ConfiguredAiProviders.Contains(lpid, StringComparer.OrdinalIgnoreCase);
                    if (wasNew)
                    {
                        _library.Prefs.ConfiguredAiProviders.Add(lpid);
                        _newlyAddedCompanionIds.Add(lpid);
                    }
                    if (kv.Value.Count > 0) _library.Prefs.AiProviderModels[lpid] = kv.Value[0];
                    _focusedCompanionId = lpid;
                    _companionPickingModel = false;
                    if (string.IsNullOrWhiteSpace(_library.Prefs.AiProvider) || !_library.Companion.IsConfigured)
                    {
                        _newlyAddedCompanionIds.Remove(lpid);
                        ActivateCompanionProvider(lpid);
                    }
                    else
                    {
                        _library.SavePrefs();
                    }
                    RenderDynamicPage("companion");
                    RenderInspector("companion");
                };
                string modelsSummary = kv.Value.Count > 0 ? $"检测到 {kv.Value.Count} 个本机模型（如 {kv.Value[0]}）" : "本机服务正在运行";
                localFoundRows.Add(MakeRow($"● {lpreset.Name} 在线", modelsSummary, useLocalBtn, compact: true));
            }
            DynamicPageHost.Children.Add(MakeHeading("本机已运行的服务", "完全离线，不花 API 费用"));
            DynamicPageHost.Children.Add(MakeCard(localFoundRows.ToArray()));
        }

        // 用量（1:1 MacCompanionSettings.swift CompanionTokenUsageView: 30-day daily bar chart + per-feature breakdown chips）
        var snap = _library.Usage.Snapshot30Days();
        var todayRec = _library.Usage.GetToday();
        DynamicPageHost.Children.Add(MakeHeading("用量"));

        var usageTop = new StackPanel { Spacing = 8, Padding = new Thickness(16, 14, 16, 14) };
        var usageHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        usageHead.Children.Add(new TextBlock { Text = "Token 用量", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        usageHead.Children.Add(new TextBlock { Text = "近 30 天", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Bottom });
        usageTop.Children.Add(usageHead);
        usageTop.Children.Add(new TextBlock
        {
            Text = CompanionUsageLedger.FormatCount(snap.TotalTokens),
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ThemeHelper.Brush(p.T1),
        });
        string hitStr = snap.CacheHitRate is double hr ? $" · 缓存命中 {Math.Round(hr * 100)}%" : "";
        usageTop.Children.Add(new TextBlock
        {
            Text = snap.TotalTokens == 0 ? "近 30 天暂无调用记录" : $"今日 {CompanionUsageLedger.FormatCount(todayRec.TotalTokens)}{hitStr}",
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
        });

        // 30-day daily bar chart
        if (snap.Days.Count > 0)
        {
            const double chartHeight = 84.0;
            long peak = Math.Max(1, snap.PeakTokens);
            var dayDetailText = new TextBlock
            {
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T2),
            };
            var dayDetailBox = new Border
            {
                CornerRadius = new CornerRadius(7),
                Background = ThemeHelper.Brush(p.Inset),
                Padding = new Thickness(10, 5, 10, 5),
                Visibility = Visibility.Collapsed,
                Child = dayDetailText,
            };

            var chartRow = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            chartRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            chartRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (snap.PeakTokens > 0)
            {
                var yAxis = new Grid { Height = chartHeight, MinWidth = 34 };
                yAxis.Children.Add(new TextBlock
                {
                    Text = CompanionUsageLedger.FormatCount(snap.PeakTokens),
                    FontSize = 9.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                });
                long avgRounded = (long)Math.Round(snap.AverageTokens);
                if (avgRounded > 0 && avgRounded < snap.PeakTokens * 0.85)
                {
                    double avgFrac = 1.0 - Math.Clamp(snap.AverageTokens / peak, 0.1, 0.9);
                    yAxis.Children.Add(new TextBlock
                    {
                        Text = CompanionUsageLedger.FormatCount(avgRounded),
                        FontSize = 9.5,
                        Foreground = ThemeHelper.Brush(p.T3),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, Math.Round(chartHeight * avgFrac) - 6, 0, 0),
                    });
                }
                yAxis.Children.Add(new TextBlock
                {
                    Text = "0",
                    FontSize = 9.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                });
                chartRow.Children.Add(yAxis);
            }

            var plotArea = new Grid { Height = chartHeight };
            plotArea.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), VerticalAlignment = VerticalAlignment.Top });
            plotArea.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), VerticalAlignment = VerticalAlignment.Bottom });
            if (snap.PeakTokens > 0 && snap.AverageTokens > 0)
            {
                double avgTop = Math.Round(chartHeight * (1.0 - Math.Clamp(snap.AverageTokens / peak, 0.05, 0.95)));
                plotArea.Children.Add(new Border
                {
                    Height = 0.5,
                    Background = ThemeHelper.Brush(p.Line),
                    Opacity = 0.65,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, avgTop, 0, 0),
                });
            }

            var barsGrid = new Grid { ColumnSpacing = 2.5, Height = chartHeight };
            var outBrush = ThemeHelper.Brush(tint.Accent);
            var missBrush = ThemeHelper.Brush(Color.FromArgb((byte)(IsDark ? 150 : 120), tint.Accent.R, tint.Accent.G, tint.Accent.B));
            var cacheBrush = ThemeHelper.Brush(Color.FromArgb((byte)(IsDark ? 175 : 145), p.Ok.R, p.Ok.G, p.Ok.B));
            var zeroTickBrush = ThemeHelper.Brush(p.Inset);

            for (int di = 0; di < snap.Days.Count; di++)
            {
                barsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var day = snap.Days[di];
                long dayTotal = day.TotalTokens;

                var colHost = new Grid
                {
                    Background = new SolidColorBrush(Colors.Transparent),
                    VerticalAlignment = VerticalAlignment.Stretch,
                };

                if (dayTotal > 0 && snap.PeakTokens > 0)
                {
                    double totalH = Math.Max(3.0, chartHeight * ((double)dayTotal / peak));
                    double outH = totalH * ((double)day.Output / dayTotal);
                    double missH = totalH * ((double)day.MissInput / dayTotal);
                    double cacheH = Math.Max(0, totalH - outH - missH);

                    var barStack = new StackPanel
                    {
                        Spacing = 0,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        CornerRadius = new CornerRadius(2, 2, 0, 0),
                    };
                    if (outH >= 0.5) barStack.Children.Add(new Border { Height = outH, Background = outBrush });
                    if (missH >= 0.5) barStack.Children.Add(new Border { Height = missH, Background = missBrush });
                    if (cacheH >= 0.5) barStack.Children.Add(new Border { Height = cacheH, Background = cacheBrush });
                    if (barStack.Children.Count == 0)
                        barStack.Children.Add(new Border { Height = 3, Background = outBrush });

                    colHost.Children.Add(barStack);
                    string tip = $"{day.Date} · 总 {dayTotal:N0}" +
                                 (day.CachedInput > 0 ? $" · 缓存 {day.CachedInput:N0}" : "") +
                                 $" · 输出 {day.Output:N0} · {day.Requests} 次";
                    ToolTipService.SetToolTip(colHost, tip);
                    colHost.PointerEntered += (_, _) =>
                    {
                        dayDetailText.Text = tip;
                        dayDetailBox.Visibility = Visibility.Visible;
                    };
                    colHost.PointerPressed += (_, _) =>
                    {
                        dayDetailText.Text = tip;
                        dayDetailBox.Visibility = dayDetailBox.Visibility == Visibility.Visible && dayDetailText.Text == tip
                            ? Visibility.Collapsed
                            : Visibility.Visible;
                    };
                }
                else
                {
                    colHost.Children.Add(new Border
                    {
                        Height = 2,
                        CornerRadius = new CornerRadius(1),
                        Background = zeroTickBrush,
                        VerticalAlignment = VerticalAlignment.Bottom,
                    });
                }

                Grid.SetColumn(colHost, di);
                barsGrid.Children.Add(colHost);
            }

            barsGrid.PointerExited += (_, _) => dayDetailBox.Visibility = Visibility.Collapsed;
            plotArea.Children.Add(barsGrid);
            Grid.SetColumn(plotArea, 1);
            chartRow.Children.Add(plotArea);
            usageTop.Children.Add(chartRow);

            // X-axis date labels (start, peak, end)
            var xAxisRow = new Grid { ColumnSpacing = 8 };
            xAxisRow.ColumnDefinitions.Add(new ColumnDefinition { Width = snap.PeakTokens > 0 ? new GridLength(34) : new GridLength(0) });
            xAxisRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var xLabels = new Grid();
            xLabels.Children.Add(new TextBlock { Text = snap.Days[0].ShortLabel, FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Left });
            if (snap.PeakTokens > 0 && snap.PeakIndex > 3 && snap.PeakIndex < snap.Days.Count - 4)
            {
                xLabels.Children.Add(new TextBlock
                {
                    Text = $"峰值 {snap.Days[snap.PeakIndex].ShortLabel}",
                    FontSize = 9.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }
            xLabels.Children.Add(new TextBlock { Text = snap.Days[^1].ShortLabel, FontSize = 9.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
            Grid.SetColumn(xLabels, 1);
            xAxisRow.Children.Add(xLabels);
            usageTop.Children.Add(xAxisRow);
            usageTop.Children.Add(dayDetailBox);

            if (snap.TotalTokens > 0)
            {
                var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
                void AddLegendItem(Brush swatch, string label)
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                    sp.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = swatch, VerticalAlignment = VerticalAlignment.Center });
                    sp.Children.Add(new TextBlock { Text = label, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
                    legend.Children.Add(sp);
                }
                if (snap.CachedInputTokens > 0) AddLegendItem(cacheBrush, "缓存命中输入");
                if (snap.MissInputTokens > 0) AddLegendItem(missBrush, "常规输入");
                if (snap.OutputTokens > 0) AddLegendItem(outBrush, "输出");
                usageTop.Children.Add(legend);
            }
        }

        // Per-feature breakdown chips (书伴问答 / 播客 / 广播剧 / 前情提要 / 翻译整理)
        var chipsScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var chipsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (snap.Sources.Count > 0)
        {
            foreach (var share in snap.Sources)
            {
                int pct = Math.Max(1, (int)Math.Round(share.Share * 100));
                string srcTitle = CompanionUsageLedger.SourceTitle(share.Source);
                var chipSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                chipSp.Children.Add(new TextBlock { Text = srcTitle, FontSize = 11, Foreground = ThemeHelper.Brush(p.T2) });
                chipSp.Children.Add(new TextBlock { Text = $"{pct}%", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                chipSp.Children.Add(new TextBlock { Text = CompanionUsageLedger.FormatCount(share.Tokens), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                chipsRow.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(11),
                    Background = ThemeHelper.Brush(p.Inset),
                    Padding = new Thickness(9, 3, 9, 3),
                    Child = chipSp,
                });
            }
        }
        else
        {
            foreach (string feat in new[] { "书伴问答", "播客", "广播剧认人", "前情提要", "翻译与错字整理" })
            {
                chipsRow.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(11),
                    Background = ThemeHelper.Brush(p.Inset),
                    Padding = new Thickness(9, 3, 9, 3),
                    Opacity = 0.72,
                    Child = new TextBlock { Text = $"{feat} 0%", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) },
                });
            }
        }
        chipsScroll.Content = chipsRow;
        usageTop.Children.Add(chipsScroll);

        string ttsText = snap.TtsSavedChars > 0
            ? $"{CompanionUsageLedger.FormatCount(snap.TtsChars)} 字 · 本地缓存省 {CompanionUsageLedger.FormatCount(snap.TtsSavedChars)} 字"
            : $"{CompanionUsageLedger.FormatCount(snap.TtsChars)} 字";
        var ttsUsageRow = MakeRow("云端朗读 · 近 30 天", null,
            new TextBlock { Text = ttsText, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) },
            compact: true);
        DynamicPageHost.Children.Add(MakeCard(usageTop, ttsUsageRow));

        DynamicPageHost.Children.Add(MakeHeading("省费"));
        var llmCapSeg = YYChrome.MakeSegmentedBar(
            [("0", "不限"), ("200000", "20 万"), ("500000", "50 万"), ("1000000", "100 万")],
            _library.Prefs.AiDailyLlmTokenCap.ToString(),
            s =>
            {
                long v = long.TryParse(s, out var parsed) ? parsed : 0;
                _library.Prefs.AiDailyLlmTokenCap = v;
                _library.SavePrefs();
                RecordChange("每日用量上限", v == 0 ? "不限" : CompanionUsageLedger.FormatCount(v));
                RenderDynamicPage("companion");
            },
            IsDark, fontSize: 11.5, height: 28);

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("批量任务自动用轻量模型", "认人、错字整理、提要优先用同厂轻量模型，并自动启用提示词缓存", MakeSwitch(_library.Prefs.AiTierRoutingEnabled, v => { _library.Prefs.AiTierRoutingEnabled = v; _library.SavePrefs(); })),
            MakeRow("每日用量上限", "达到后自动暂停后台批量任务", llmCapSeg),
            MakeRow("云端广播剧旁白保留本机免费声线", "选角时优先把大段旁白留在免费本机音色，显著省钱", MakeSwitch(_library.Prefs.DramaCloudSaveNarrator, v => { _library.Prefs.DramaCloudSaveNarrator = v; _library.SavePrefs(); }))));
        DynamicPageHost.Children.Add(MakeNote("统计为本机估算值，以服务商实际账单为准。书伴问答与前情提要的纯文本缓存会随同步自动共享。"));
    }

    private async Task ShowAddVoiceSourceDialogAsync()
    {
        var p = CurrentPalette;
        ContentDialog? dlgRef = null;
        var stageHost = new Grid();

        static (string Url, string Trail) ProviderKeyPortal(CloudTtsProvider prov) => prov switch
        {
            CloudTtsProvider.Volcengine => ("https://console.volcengine.com/speech/app", "console.volcengine.com › 语音技术 › 创建应用获取 AppID 与 Token。"),
            CloudTtsProvider.Xiaomi => ("https://platform.xiaomimimo.com", "platform.xiaomimimo.com › API Keys › 新建密钥。"),
            CloudTtsProvider.SiliconFlow => ("https://cloud.siliconflow.cn/account/ak", "cloud.siliconflow.cn › API 密钥 › 新建。"),
            CloudTtsProvider.DashScope => ("https://bailian.console.aliyun.com/", "bailian.console.aliyun.com › API-KEY › 创建密钥。"),
            CloudTtsProvider.MiniMax => ("https://platform.minimaxi.com/user-center/basic-information/interface-key", "platform.minimaxi.com › 接口密钥 › 新建。"),
            CloudTtsProvider.Fish => ("https://fish.audio/zh-CN/go-api/", "fish.audio › API › 新建 API Key。"),
            CloudTtsProvider.Azure => ("https://portal.azure.com/", "portal.azure.com › 语音服务 › 密钥和终结点。"),
            CloudTtsProvider.OpenAi => ("https://platform.openai.com/api-keys", "platform.openai.com › API keys › 新建，拷下来。"),
            CloudTtsProvider.ElevenLabs => ("https://elevenlabs.io/app/settings/api-keys", "elevenlabs.io › Developers › API Keys › 新建。"),
            CloudTtsProvider.Deepgram => ("https://console.deepgram.com/", "console.deepgram.com › API Keys › Create Key。"),
            _ => ("", ""),
        };

        void RenderPickerStep()
        {
            stageHost.Children.Clear();
            var wrap = new StackPanel { Spacing = 14, Padding = new Thickness(22, 20, 22, 18) };
            wrap.Children.Add(YYChrome.MakeSheetHeader("添加声音来源", "朗读", () => dlgRef?.Hide(), IsDark, titleSize: 18));

            var searchRow = new Grid
            {
                Height = 36,
                Padding = new Thickness(11, 0, 11, 0),
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(p.Inset),
                ColumnSpacing = 8,
            };
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.Children.Add(YYIcons.Make("search", 13.5, ThemeHelper.Brush(p.T3)));
            var sBox = new TextBox
            {
                PlaceholderText = "搜索语音服务，比如 豆包、OpenAI、MiMo、VoiceStudio…",
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0, 6, 0, 6),
            };
            Grid.SetColumn(sBox, 1);
            searchRow.Children.Add(sBox);
            wrap.Children.Add(searchRow);

            var groupsHost = new StackPanel { Spacing = 14 };
            void PopulateVoiceProviders(string qRaw)
            {
                groupsHost.Children.Clear();
                string q = qRaw.Trim().ToLowerInvariant();
                var all = CloudVoiceService.AllProviders
                    .Where(x => x is not CloudTtsProvider.Edge and not CloudTtsProvider.Free)
                    .Where(x => string.IsNullOrEmpty(q)
                        || CloudVoiceEntry.ProviderKey(x).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || CloudVoiceService.ProviderDisplayName(x, _library.Prefs).Contains(q, StringComparison.OrdinalIgnoreCase)
                        || CloudVoiceService.ProviderHelp(x).Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                foreach (var (grp, grpTitle) in new[]
                {
                    (CloudProviderGroup.Domestic, "国内服务"),
                    (CloudProviderGroup.Global, "国际服务"),
                    (CloudProviderGroup.Local, "本机与局域网"),
                })
                {
                    var items = all.Where(x => CloudVoiceService.ProviderGroup(x) == grp).ToArray();
                    if (items.Length == 0) continue;

                    var sec = new StackPanel { Spacing = 6 };
                    sec.Children.Add(new TextBlock { Text = grpTitle, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(4, 0, 0, 0) });

                    var grid = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
                    for (int c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    int rCount = (items.Length + 2) / 3;
                    for (int r = 0; r < rCount; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    for (int i = 0; i < items.Length; i++)
                    {
                        var pr = items[i];
                        string pk = CloudVoiceEntry.ProviderKey(pr);
                        bool cfg = _library.CloudVoices.IsConfigured(pr);
                        var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
                        cell.Children.Add(YYChrome.MakeBareServiceIcon(pk, IsDark, 20));
                        cell.Children.Add(new TextBlock
                        {
                            Text = cfg ? $"{CloudVoiceService.ProviderDisplayName(pr, _library.Prefs)} ✓" : CloudVoiceService.ProviderDisplayName(pr, _library.Prefs),
                            FontSize = 12.5,
                            Foreground = ThemeHelper.Brush(cfg ? p.T3 : p.T1),
                            VerticalAlignment = VerticalAlignment.Center,
                            MaxLines = 1,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        });
                        var cellBtn = new Button
                        {
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Padding = new Thickness(6, 7, 6, 7),
                            CornerRadius = new CornerRadius(8),
                            Background = new SolidColorBrush(Colors.Transparent),
                            BorderThickness = new Thickness(0),
                            UseSystemFocusVisuals = false,
                            Content = cell,
                        };
                        var captured = pr;
                        cellBtn.Click += (_, _) => RenderConnectStep(captured);
                        Grid.SetRow(cellBtn, i / 3);
                        Grid.SetColumn(cellBtn, i % 3);
                        grid.Children.Add(cellBtn);
                    }
                    sec.Children.Add(grid);
                    groupsHost.Children.Add(sec);
                }
            }

            sBox.TextChanged += (_, _) => PopulateVoiceProviders(sBox.Text);
            PopulateVoiceProviders("");
            wrap.Children.Add(new ScrollViewer { MaxHeight = 340, Content = groupsHost });
            L10n.ApplyTree(wrap);
            stageHost.Children.Add(wrap);
        }

        void RenderConnectStep(CloudTtsProvider prov)
        {
            stageHost.Children.Clear();
            string pk = CloudVoiceEntry.ProviderKey(prov);
            string dispName = CloudVoiceService.ProviderDisplayName(prov, _library.Prefs);
            bool isLocal = CloudVoiceService.IsLocalProvider(prov);
            int vCount = _library.CloudVoices.GetVoices(prov).Count;

            var wrap = new StackPanel { Spacing = 12, Padding = new Thickness(22, 20, 22, 20) };
            wrap.Children.Add(YYChrome.MakeSheetHeader(
                dispName,
                "朗读",
                () => dlgRef?.Hide(),
                IsDark,
                leading: YYChrome.MakeServiceTile(pk, IsDark, 28),
                titleSize: 18));

            string summary = isLocal
                ? CloudVoiceService.ProviderHelp(prov)
                : $"{Math.Max(vCount, 4)} 个音色，支持中英朗读与本机持久缓存";
            wrap.Children.Add(new TextBlock { Text = summary, FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap });

            // 1:1 settings-cloudvoice/1.png: merged white card with recessed Key/URL input(s) + "检查并添加" row
            var cardStack = new StackPanel { Spacing = 0 };
            TextBox? urlBox = null;
            TextBox? extraBox = null;
            PasswordBox? keyBox = null;

            Grid MakeRecessedFieldRow(string label, FrameworkElement editor)
            {
                var rg = new Grid
                {
                    Margin = new Thickness(5, 5, 5, 0),
                    Padding = new Thickness(12, 6, 10, 6),
                    MinHeight = 40,
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(p.Inset),
                    ColumnSpacing = 10,
                };
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rg.Children.Add(new TextBlock { Text = label, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(editor, 1);
                rg.Children.Add(editor);
                return rg;
            }

            if (isLocal)
            {
                urlBox = new TextBox
                {
                    Text = _library.CloudVoices.GetSavedUrl(prov),
                    PlaceholderText = CloudVoiceService.DefaultLocalUrl(prov),
                    BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    FontSize = 12.5,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                cardStack.Children.Add(MakeRecessedFieldRow("地址", urlBox));
            }
            else
            {
                if (prov == CloudTtsProvider.Volcengine)
                {
                    extraBox = new TextBox
                    {
                        Text = _library.Secrets.Get("tts.volcengine.appid"),
                        PlaceholderText = "AppID",
                        BorderThickness = new Thickness(0),
                        Background = new SolidColorBrush(Colors.Transparent),
                        FontSize = 12.5,
                        TextAlignment = TextAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    cardStack.Children.Add(MakeRecessedFieldRow("AppID", extraBox));
                }
                else if (prov == CloudTtsProvider.Azure)
                {
                    extraBox = new TextBox
                    {
                        Text = _library.Prefs.CloudVoiceAzureRegion,
                        PlaceholderText = "eastasia",
                        BorderThickness = new Thickness(0),
                        Background = new SolidColorBrush(Colors.Transparent),
                        FontSize = 12.5,
                        TextAlignment = TextAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    cardStack.Children.Add(MakeRecessedFieldRow("区域", extraBox));
                }

                string existingKey = _library.CloudVoices.GetApiKey(prov);
                if (string.IsNullOrEmpty(existingKey) && _library.CloudVoices.GetCompanionSharedKey(prov) is { Length: > 0 } shared)
                    existingKey = shared;

                keyBox = new PasswordBox
                {
                    Password = existingKey,
                    PlaceholderText = CloudVoiceService.ProviderKeyHint(prov),
                    BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                cardStack.Children.Add(MakeRecessedFieldRow("Key", keyBox));
            }

            var statusRight = new TextBlock
            {
                Text = isLocal ? "探测本机端口" : "先填 Key",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var verifyLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
            verifyLeft.Children.Add(YYIcons.Make("waveform-ecg", 13.5, ThemeHelper.Brush(p.Ok)));
            var verifyLabel = new TextBlock { Text = "检查并添加", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.Ok) };
            verifyLeft.Children.Add(verifyLabel);

            var verifyGrid = new Grid { Padding = new Thickness(12, 9, 12, 9) };
            verifyGrid.Children.Add(verifyLeft);
            verifyGrid.Children.Add(statusRight);

            var verifyBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                UseSystemFocusVisuals = false,
                Content = verifyGrid,
            };

            void SyncVerifyReady()
            {
                bool ready = isLocal
                    ? !string.IsNullOrWhiteSpace(urlBox?.Text)
                    : !string.IsNullOrWhiteSpace(keyBox?.Password);
                verifyBtn.Opacity = ready ? 1.0 : 0.62;
                if (statusRight.Foreground != ThemeHelper.Brush(p.Danger))
                    statusRight.Text = ready ? "点此验证" : (isLocal ? "先填服务地址" : "先填 Key");
            }
            if (urlBox is not null) urlBox.TextChanged += (_, _) => SyncVerifyReady();
            if (keyBox is not null) keyBox.PasswordChanged += (_, _) => SyncVerifyReady();
            SyncVerifyReady();

            verifyBtn.Click += async (_, _) =>
            {
                if (isLocal && urlBox is not null)
                {
                    _library.Prefs.CloudVoiceBaseUrls[pk] = urlBox.Text.Trim();
                    _library.SavePrefs();
                }
                if (prov == CloudTtsProvider.Volcengine && extraBox is not null)
                    _library.Secrets.Set("tts.volcengine.appid", extraBox.Text.Trim());
                if (prov == CloudTtsProvider.Azure && extraBox is not null)
                {
                    _library.Prefs.CloudVoiceAzureRegion = extraBox.Text.Trim();
                    _library.SavePrefs();
                }
                if (keyBox is not null)
                    _library.CloudVoices.SetApiKey(prov, keyBox.Password.Trim());

                verifyBtn.IsEnabled = false;
                statusRight.Text = "正在验证…";
                statusRight.Foreground = ThemeHelper.Brush(p.T2);
                var (ok, _, msg) = await _library.CloudVoices.RefreshVoicesAsync(prov);
                verifyBtn.IsEnabled = true;
                if (ok)
                {
                    _focusedVoiceSource = pk;
                    App.CurrentApp.BroadcastVoiceChanged();
                    dlgRef?.Hide();
                    RenderDynamicPage("listening");
                    RenderInspector("listening");
                    ShowFlashBanner($"✓ {msg}");
                }
                else
                {
                    statusRight.Text = msg;
                    statusRight.Foreground = ThemeHelper.Brush(p.Danger);
                }
            };
            cardStack.Children.Add(verifyBtn);

            wrap.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Child = cardStack,
            });

            var (portalUrl, portalTrail) = ProviderKeyPortal(prov);
            if (!string.IsNullOrEmpty(portalUrl))
            {
                var getSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                getSp.Children.Add(new TextBlock { Text = "↗", FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
                getSp.Children.Add(new TextBlock { Text = $"获取 {dispName} Key", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
                var getKeyCardBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 42,
                    Padding = new Thickness(14, 0, 14, 0),
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    UseSystemFocusVisuals = false,
                    Content = getSp,
                };
                getKeyCardBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri(portalUrl));
                wrap.Children.Add(getKeyCardBtn);
            }

            string footText = !string.IsNullOrEmpty(portalTrail)
                ? $"{portalTrail} API Key 仅保存在本机，调用费用由服务商直接结算。"
                : "连接成功后自动出现在「声音来源」列表，已朗读句子自动缓存在本机。";
            wrap.Children.Add(new TextBlock { Text = footText, FontSize = 11.5, LineHeight = 18, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 0) });

            var backBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = "‹ 返回全部声音来源",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                Padding = new Thickness(4, 2, 6, 2),
            };
            backBtn.Click += (_, _) => RenderPickerStep();
            wrap.Children.Add(backBtn);

            L10n.ApplyTree(wrap);
            stageHost.Children.Add(wrap);
            YYChrome.AnimateEntrance(wrap, fromY: 6f, durationMs: 140);
        }

        RenderPickerStep();
        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, IsDark, 480, stageHost);
        await dlgRef.ShowAsync();
    }

#if DEBUG
    internal void OpenDebugSheet(string kind)
    {
        if (kind == "add-provider") _ = ShowAddProviderSheetAsync();
        else if (kind == "add-voice") _ = ShowAddVoiceSourceDialogAsync();
    }
#endif

    private async Task ShowAddProviderSheetAsync()
    {
        var p = CurrentPalette;
        ContentDialog? dlgRef = null;

        // 1:1 settings-companion-add/1.png: frameless 480px sheet + recessed search pill + borderless 3-col brand grid + pinned footer
        var searchRow = new Grid
        {
            Height = 36,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(p.Inset),
            ColumnSpacing = 8,
        };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.Children.Add(YYIcons.Make("search", 13.5, ThemeHelper.Brush(p.T3)));
        var searchBox = new TextBox
        {
            PlaceholderText = "搜索服务商，比如 千问、Kimi、magpie",
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 6, 0, 6),
        };
        Grid.SetColumn(searchBox, 1);
        searchRow.Children.Add(searchBox);

        var groupsHost = new StackPanel { Spacing = 16 };
        string? pickedProviderId = null;

        void RebuildGroups(string rawQuery)
        {
            groupsHost.Children.Clear();
            string q = rawQuery.Trim().Replace(" ", "").Replace("-", "").Replace("_", "").ToLowerInvariant();
            var catalog = CompanionClient.Presets
                .Where(pr => pr.Id != "custom" && (string.IsNullOrEmpty(q)
                    || $"{pr.Id}{pr.Name}{pr.Aliases}".Replace(" ", "").ToLowerInvariant().Contains(q)))
                .ToArray();

            foreach (var (grpKey, grpTitle) in new[]
            {
                ("signIn", "登录账号"),
                ("key", "API Key"),
                ("local", "本机和局域网"),
            })
            {
                var list = catalog.Where(x => x.Group == grpKey).ToArray();
                if (list.Length == 0) continue;

                var sec = new StackPanel { Spacing = 6 };
                sec.Children.Add(new TextBlock
                {
                    Text = grpTitle,
                    FontSize = 11.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(4, 0, 0, 0),
                });

                var grid = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
                for (int c = 0; c < 3; c++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int rows = (list.Length + 2) / 3;
                for (int r = 0; r < rows; r++)
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (int i = 0; i < list.Length; i++)
                {
                    var pr = list[i];
                    bool already = _library.Prefs.ConfiguredAiProviders.Contains(pr.Id);
                    var row = new Grid { ColumnSpacing = 9 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(YYChrome.MakeBareServiceIcon(pr.Id, IsDark, 20));

                    var lbl = new TextBlock
                    {
                        Text = already ? $"{pr.Name} ✓" : pr.Name,
                        FontSize = 13,
                        Foreground = ThemeHelper.Brush(already ? p.T3 : p.T1),
                        VerticalAlignment = VerticalAlignment.Center,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    Grid.SetColumn(lbl, 1);
                    row.Children.Add(lbl);

                    var btn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(6, 7, 6, 7),
                        CornerRadius = new CornerRadius(8),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        UseSystemFocusVisuals = false,
                        Content = row,
                    };
                    if (!string.IsNullOrEmpty(pr.Blurb))
                        ToolTipService.SetToolTip(btn, pr.Blurb);

                    string pid = pr.Id;
                    btn.Click += (_, _) =>
                    {
                        bool wasNew = !_library.Prefs.ConfiguredAiProviders.Contains(pid, StringComparer.OrdinalIgnoreCase);
                        if (wasNew)
                        {
                            _library.Prefs.ConfiguredAiProviders.Add(pid);
                            _newlyAddedCompanionIds.Add(pid);
                        }
                        else if (string.IsNullOrWhiteSpace(_library.Prefs.AiProvider) || !_library.Companion.IsConfigured)
                        {
                            _newlyAddedCompanionIds.Add(pid);
                        }
                        pickedProviderId = pid;
                        _focusedCompanionId = pid;
                        _companionPickingModel = false;
                        _library.SavePrefs();
                        dlgRef?.Hide();
                    };
                    Grid.SetRow(btn, i / 3);
                    Grid.SetColumn(btn, i % 3);
                    grid.Children.Add(btn);
                }
                sec.Children.Add(grid);
                groupsHost.Children.Add(sec);
            }

            if (groupsHost.Children.Count == 0)
            {
                groupsHost.Children.Add(new TextBlock
                {
                    Text = "没找到。点下方「自己添加」，填上接口地址就行",
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 28, 0, 28),
                });
            }
        }

        searchBox.TextChanged += (_, _) => RebuildGroups(searchBox.Text);
        RebuildGroups("");

        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        customRow.Children.Add(YYIcons.Make("plus", 13, ThemeHelper.Brush(p.T2)));
        customRow.Children.Add(new TextBlock { Text = "自己添加", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        customRow.Children.Add(new TextBlock { Text = "任何 OpenAI 兼容接口", FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });

        var customBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(22, 12, 22, 12),
            CornerRadius = new CornerRadius(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = customRow,
        };
        customBtn.Click += (_, _) =>
        {
            string cid = "custom." + Guid.NewGuid().ToString("N")[..8];
            _library.Prefs.AiCustomProviderNames[cid] = "自定义服务";
            _library.Prefs.ConfiguredAiProviders.Add(cid);
            _newlyAddedCompanionIds.Add(cid);
            pickedProviderId = cid;
            _focusedCompanionId = cid;
            _companionPickingModel = false;
            _library.SavePrefs();
            dlgRef?.Hide();
        };

        var topPad = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(22, 20, 22, 12),
            Children =
            {
                YYChrome.MakeSheetHeader("添加服务商", "书伴", () => dlgRef?.Hide(), IsDark, titleSize: 18),
                searchRow,
                new ScrollViewer { MaxHeight = 330, Content = groupsHost },
            },
        };

        var outer = new StackPanel { Spacing = 0 };
        outer.Children.Add(topPad);
        outer.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
        outer.Children.Add(customBtn);
        L10n.ApplyTree(outer);

        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, IsDark, 480, outer);
        await dlgRef.ShowAsync();
        if (!string.IsNullOrEmpty(pickedProviderId))
        {
            _focusedCompanionId = pickedProviderId;
            _companionPickingModel = false;
            RenderDynamicPage("companion");
            RenderInspector("companion");
            L10n.ApplyTree(DynamicPageHost);
            L10n.ApplyTree(InspectorHost);
        }
    }

    private void BuildCompanionInspector(YYPalette p, YYTintColors tint)
    {
        var preset = ResolveCompanionPreset(_focusedCompanionId);
        bool inUse = _library.Prefs.AiProvider == preset.Id;
        string curModel = GetProviderModel(preset.Id, preset.DefaultModel);

        // ModelList 子页钻取（1:1 MacCompanionSettings.swift:296-543 + GET /models 动态拉取）
        if (_companionPickingModel)
        {
            var backRow = new Grid();
            var backLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var backBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "‹ 返回", Height = 25, MinHeight = 25, Padding = new Thickness(8, 0, 10, 0) };
            backBtn.Click += (_, _) => { _companionPickingModel = false; RenderInspector("companion"); };
            backLeft.Children.Add(backBtn);
            backLeft.Children.Add(new TextBlock { Text = "选择模型", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            backRow.Children.Add(backLeft);

            var fetchBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "↻ 拉取列表", Height = 25, MinHeight = 25, Padding = new Thickness(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11 };
            backRow.Children.Add(fetchBtn);
            InspectorHost.Children.Add(backRow);

            string[] recommended = preset.Id switch
            {
                "deepseek" => ["deepseek-chat", "deepseek-reasoner"],
                "claude" => ["claude-sonnet-4-5", "claude-haiku-4-5", "claude-opus-4-5"],
                "openai" => ["gpt-5-mini", "gpt-5", "gpt-4o-mini", "gpt-4o"],
                "gemini" => ["gemini-2.5-flash", "gemini-2.5-pro"],
                "openrouter" => ["openrouter/auto", "anthropic/claude-sonnet-4.5", "google/gemini-2.5-flash", "deepseek/deepseek-chat"],
                "dashscope" => ["qwen-plus", "qwen-max", "qwen-turbo"],
                "moonshot" => ["kimi-k2-turbo-preview", "moonshot-v1-32k"],
                "zhipu" => ["glm-4.6", "glm-4-flash"],
                "volcengine" => ["doubao-seed-1-6-250615"],
                "siliconflow" => ["deepseek-ai/DeepSeek-V3", "Qwen/Qwen2.5-72B-Instruct"],
                "ollama" => ["qwen2.5:7b", "llama3.1:8b", "deepseek-r1:7b"],
                "lmstudio" => ["qwen2.5-7b-instruct", "llama-3.1-8b-instruct"],
                _ => string.IsNullOrEmpty(preset.DefaultModel) ? [] : [preset.DefaultModel],
            };

            var filterBox = new TextBox { PlaceholderText = "筛选模型 ID 或厂商…", FontSize = 11.5 };
            var makerStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var makerScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                Content = makerStrip,
            };
            var mStack = new StackPanel { Spacing = 4 };
            string selectedMaker = "全部";

            List<string> BuildPool()
            {
                var merged = new List<string>();
                foreach (var r in recommended)
                    if (!merged.Contains(r, StringComparer.OrdinalIgnoreCase)) merged.Add(r);
                foreach (var persisted in _library.Companion.GetCachedModels(preset.Id))
                    if (!merged.Contains(persisted, StringComparer.OrdinalIgnoreCase)) merged.Add(persisted);
                if (_cachedCompanionModels.TryGetValue(preset.Id, out var remoteList))
                {
                    foreach (var rm in remoteList)
                        if (!merged.Contains(rm, StringComparer.OrdinalIgnoreCase)) merged.Add(rm);
                }
                return merged;
            }

            void SelectModelAndClose(string nextM)
            {
                _library.Prefs.AiProviderModels[preset.Id] = nextM;
                _library.Companion.RecordModelUsed(preset.Id, nextM);
                if (inUse) ActivateCompanionProvider(preset.Id);
                else _library.SavePrefs();
                RecordChange("书伴模型", nextM);
                _companionPickingModel = false;
                RenderDynamicPage("companion");
                RenderInspector("companion");
            }

            void PopulateModels(string filter)
            {
                mStack.Children.Clear();
                makerStrip.Children.Clear();
                string fq = filter.Trim();
                var pool = BuildPool();

                // Maker filter pills (全部 / ★ 收藏 / distinct makers when >= 2)
                var distinctMakers = pool.Select(CompanionClient.InferModelMaker).Distinct(StringComparer.Ordinal).ToList();
                bool hasStars = pool.Any(m => _library.Companion.IsModelStarred(preset.Id, m));
                var makerChoices = new List<string> { "全部" };
                if (hasStars) makerChoices.Add("★ 收藏");
                if (distinctMakers.Count >= 2) makerChoices.AddRange(distinctMakers);
                if (!makerChoices.Contains(selectedMaker)) selectedMaker = "全部";

                makerScroll.Visibility = makerChoices.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                foreach (string mk in makerChoices)
                {
                    string capMk = mk;
                    bool onMk = selectedMaker == capMk;
                    var pill = new Button
                    {
                        Height = 24,
                        MinHeight = 24,
                        Padding = new Thickness(9, 0, 9, 0),
                        CornerRadius = new CornerRadius(12),
                        FontSize = 11,
                        FontWeight = onMk ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                        Background = ThemeHelper.Brush(onMk ? tint.AccentSoft : p.Inset),
                        Foreground = ThemeHelper.Brush(onMk ? tint.Accent : p.T2),
                        BorderThickness = new Thickness(0),
                        UseSystemFocusVisuals = false,
                        Content = capMk,
                    };
                    pill.Click += (_, _) => { selectedMaker = capMk; PopulateModels(filterBox.Text); };
                    makerStrip.Children.Add(pill);
                }

                UIElement BuildModelRow(string mid)
                {
                    bool on = string.Equals(curModel, mid, StringComparison.OrdinalIgnoreCase);
                    bool starred = _library.Companion.IsModelStarred(preset.Id, mid);
                    string? badge = CompanionClient.InferRouteBadge(preset.Id, mid);

                    var rowGrid = new Grid { ColumnSpacing = 4 };
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var btnInner = new Grid { ColumnSpacing = 6 };
                    btnInner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    btnInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    btnInner.Children.Add(new TextBlock
                    {
                        Text = on ? $"✓ {mid}" : mid,
                        FontSize = 11.5,
                        FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                        Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T1),
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    if (!string.IsNullOrEmpty(badge))
                    {
                        var tag = MakeStatusPill(badge, badge is "免费路由" or "本机" ? p.Ok : p.T3);
                        Grid.SetColumn(tag, 1);
                        btnInner.Children.Add(tag);
                    }

                    var mb = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(9, 6, 8, 6),
                        CornerRadius = new CornerRadius(9),
                        BorderThickness = new Thickness(1),
                        Background = ThemeHelper.Brush(on ? tint.Soft : p.Card),
                        BorderBrush = ThemeHelper.Brush(on ? tint.Edge : p.Line),
                        UseSystemFocusVisuals = false,
                        Content = btnInner,
                    };
                    string nextM = mid;
                    mb.Click += (_, _) => SelectModelAndClose(nextM);
                    rowGrid.Children.Add(mb);

                    var starBtn = new Button
                    {
                        Width = 26,
                        Height = 26,
                        Padding = new Thickness(0),
                        CornerRadius = new CornerRadius(8),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        UseSystemFocusVisuals = false,
                        Content = new TextBlock { Text = starred ? "★" : "☆", FontSize = 12, Foreground = ThemeHelper.Brush(starred ? tint.Accent : p.T3) },
                    };
                    ToolTipService.SetToolTip(starBtn, starred ? "取消收藏模型" : "收藏常用模型");
                    starBtn.Click += (_, _) =>
                    {
                        _library.Companion.ToggleStarModel(preset.Id, nextM);
                        PopulateModels(filterBox.Text);
                    };
                    Grid.SetColumn(starBtn, 1);
                    rowGrid.Children.Add(starBtn);

                    return rowGrid;
                }

                if (string.IsNullOrEmpty(fq) && selectedMaker == "全部")
                {
                    var starredModels = pool.Where(m => _library.Companion.IsModelStarred(preset.Id, m)).Take(6).ToArray();
                    if (starredModels.Length > 0)
                    {
                        mStack.Children.Add(new TextBlock { Text = "已收藏", FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(4, 2, 0, 2) });
                        foreach (var sm in starredModels) mStack.Children.Add(BuildModelRow(sm));
                    }
                    if (_library.Prefs.AiRecentModels.TryGetValue(preset.Id, out var recentList) && recentList.Count > 0)
                    {
                        mStack.Children.Add(new TextBlock { Text = "最近用过", FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(4, 4, 0, 2) });
                        foreach (var rm in recentList.Take(3)) mStack.Children.Add(BuildModelRow(rm));
                    }
                    mStack.Children.Add(new TextBlock { Text = $"全部模型 ({pool.Count})", FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(4, 6, 0, 2) });
                }

                IEnumerable<string> seq = pool;
                if (selectedMaker == "★ 收藏")
                    seq = seq.Where(m => _library.Companion.IsModelStarred(preset.Id, m));
                else if (selectedMaker != "全部")
                    seq = seq.Where(m => CompanionClient.InferModelMaker(m) == selectedMaker);

                if (!string.IsNullOrEmpty(fq))
                    seq = seq.Where(x => x.Contains(fq, StringComparison.OrdinalIgnoreCase) ||
                                         CompanionClient.InferModelMaker(x).Contains(fq, StringComparison.OrdinalIgnoreCase));

                var shown = seq.Take(60).ToArray();
                foreach (var mid in shown)
                    mStack.Children.Add(BuildModelRow(mid));

                if (!string.IsNullOrEmpty(fq) && !shown.Contains(fq, StringComparer.OrdinalIgnoreCase))
                {
                    var useCustomBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        FontSize = 11.5,
                        Foreground = ThemeHelper.Brush(tint.Accent),
                        Content = $"在 {ShortProviderTitle(preset.Id, preset.Name)} 使用 ID：{fq}",
                        Margin = new Thickness(0, 4, 0, 0),
                    };
                    string customId = fq;
                    useCustomBtn.Click += (_, _) => SelectModelAndClose(customId);
                    mStack.Children.Add(useCustomBtn);
                }
            }

            filterBox.TextChanged += (_, _) => PopulateModels(filterBox.Text);
            PopulateModels("");

            fetchBtn.Click += async (_, _) =>
            {
                fetchBtn.IsEnabled = false;
                fetchBtn.Content = "拉取中…";
                try
                {
                    var found = await _library.Companion.ListModelsAsync(preset.Id);
                    if (found.Count > 0) _cachedCompanionModels[preset.Id] = found;
                    PopulateModels(filterBox.Text);
                }
                catch { }
                finally
                {
                    fetchBtn.IsEnabled = true;
                    fetchBtn.Content = "↻ 拉取列表";
                }
            };

            if (!_cachedCompanionModels.ContainsKey(preset.Id))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var found = await _library.Companion.ListModelsAsync(preset.Id);
                        if (found.Count > 0)
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                _cachedCompanionModels[preset.Id] = found;
                                if (_companionPickingModel && _focusedCompanionId == preset.Id)
                                    PopulateModels(filterBox.Text);
                            });
                        }
                    }
                    catch { }
                });
            }

            InspectorHost.Children.Add(filterBox);
            InspectorHost.Children.Add(makerScroll);
            InspectorHost.Children.Add(new ScrollViewer { MaxHeight = 270, Content = mStack });

            var customBox = new TextBox { Header = "或输入自定义模型 ID", Text = curModel, FontSize = 12 };
            customBox.TextChanged += (_, _) =>
            {
                _library.Prefs.AiProviderModels[preset.Id] = customBox.Text.Trim();
                if (inUse) ActivateCompanionProvider(preset.Id);
                else _library.SavePrefs();
                RenderDynamicPage("companion");
            };
            InspectorHost.Children.Add(customBox);
            return;
        }

        // ServicePanel 主视图
        var headRow = new Grid { ColumnSpacing = 10 };
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        headRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headRow.Children.Add(MakeServiceLogoTile(preset.Id, 30));
        var hMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        hMeta.Children.Add(new TextBlock { Text = ShortProviderTitle(preset.Id, preset.Name), FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        var hStatusTb = new TextBlock { Text = inUse ? "当前使用" : "已添加 · 未设为当前", FontSize = 11, Foreground = ThemeHelper.Brush(inUse ? p.Ok : p.T3) };
        hMeta.Children.Add(hStatusTb);
        Grid.SetColumn(hMeta, 1);
        headRow.Children.Add(hMeta);
        InspectorHost.Children.Add(headRow);

        var pickModelBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 0, 2),
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(tint.Accent),
            Content = $"{(string.IsNullOrEmpty(curModel) ? "未选择" : curModel)} ›",
        };
        pickModelBtn.Click += (_, _) => { _companionPickingModel = true; RenderInspector("companion"); };

        ToggleSwitch useSw = null!;
        useSw = MakeSwitch(inUse, on =>
        {
            if (inUse)
            {
                if (!on) useSw.IsOn = true;
                return;
            }
            if (!on) return;
            ActivateCompanionProvider(preset.Id);
            RecordChange("书伴服务", ShortProviderTitle(preset.Id, preset.Name));
            RenderDynamicPage("companion");
            RenderInspector("companion");
        });

        InspectorHost.Children.Add(MakeCard(
            MakeRow("模型", null, pickModelBtn, compact: true),
            MakeRow("设为当前服务", null, useSw, compact: true)));

        var credStack = new StackPanel { Spacing = 8 };
        if (preset.Id.StartsWith("custom.", StringComparison.OrdinalIgnoreCase))
        {
            var nameBox = new TextBox
            {
                Header = "服务名称",
                Text = ShortProviderTitle(preset.Id, preset.Name),
                PlaceholderText = "例如：公司网关",
                FontSize = 12,
            };
            nameBox.TextChanged += (_, _) =>
            {
                _library.Prefs.AiCustomProviderNames[preset.Id] = nameBox.Text.Trim();
                _library.SavePrefs();
                RenderDynamicPage("companion");
            };
            credStack.Children.Add(nameBox);
        }

        var keyBox = new PasswordBox
        {
            Header = "API Key（本机加密）",
            Password = _library.Secrets.Get($"ai.key.{preset.Id}"),
            PlaceholderText = preset.KeyHint,
            FontSize = 12,
        };
        keyBox.PasswordChanged += (_, _) =>
        {
            _library.Secrets.Set($"ai.key.{preset.Id}", keyBox.Password.Trim());
            if (inUse) _library.Secrets.Set("ai.apiKey", keyBox.Password.Trim());
            RenderDynamicPage("companion");
        };

        // 1:1 OpenRouterSignIn.swift：一键浏览器 PKCE 授权获取专属 Key
        if (preset.Id == "openrouter")
        {
            var orStatus = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap };
            var orBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = string.IsNullOrEmpty(keyBox.Password) ? "用 OpenRouter 登录…" : "重新用 OpenRouter 登录…",
            };
            orBtn.Click += async (_, _) =>
            {
                orBtn.IsEnabled = false;
                orStatus.Text = "已在浏览器打开授权页，登录完成后自动回到语阅…";
                try
                {
                    string gotKey = await CompanionClient.SignInWithOpenRouterAsync();
                    _library.Secrets.Set("ai.key.openrouter", gotKey);
                    _newlyAddedCompanionIds.Remove("openrouter");
                    ActivateCompanionProvider("openrouter");
                    RecordChange("OpenRouter", "已登录");
                    RenderDynamicPage("companion");
                    RenderInspector("companion");
                }
                catch (Exception ex)
                {
                    orStatus.Text = ex.Message;
                    orStatus.Foreground = ThemeHelper.Brush(p.Danger);
                    orBtn.IsEnabled = true;
                }
            };
            credStack.Children.Add(orBtn);
            credStack.Children.Add(orStatus);
        }

        var urlBox = new TextBox
        {
            Header = "接口地址",
            Text = GetProviderUrl(preset.Id, preset.BaseUrl),
            PlaceholderText = string.IsNullOrEmpty(preset.BaseUrl) ? "https://.../v1" : preset.BaseUrl,
            FontSize = 11.5,
        };
        urlBox.TextChanged += (_, _) =>
        {
            _library.Prefs.AiProviderUrls[preset.Id] = urlBox.Text.Trim();
            if (inUse) ActivateCompanionProvider(preset.Id);
            else _library.SavePrefs();
        };
        credStack.Children.Add(keyBox);
        if (!string.IsNullOrEmpty(preset.KeyUrl))
        {
            string kurl = preset.KeyUrl;
            var getKeyLink = new HyperlinkButton
            {
                Content = $"去 {preset.Name} 控制台获取 API Key ↗",
                FontSize = 11,
                Padding = new Thickness(0, 2, 0, 2),
                NavigateUri = new Uri(kurl),
            };
            credStack.Children.Add(getKeyLink);
        }
        credStack.Children.Add(urlBox);

        string autoFastHint = _library.Companion.ResolvedFastModel(preset.Id, curModel) ?? "与主模型相同";
        var fastBox = new TextBox
        {
            Header = "轻量任务模型（可选，留空自动匹配）",
            Text = _library.Prefs.AiProviderFastModels.TryGetValue(preset.Id, out var fm) ? fm : "",
            PlaceholderText = $"自动：{autoFastHint}",
            FontSize = 11.5,
        };
        fastBox.TextChanged += (_, _) =>
        {
            string val = fastBox.Text.Trim();
            if (string.IsNullOrEmpty(val)) _library.Prefs.AiProviderFastModels.Remove(preset.Id);
            else _library.Prefs.AiProviderFastModels[preset.Id] = val;
            _library.SavePrefs();
        };
        credStack.Children.Add(fastBox);
        InspectorHost.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = ThemeHelper.Brush(p.Card), BorderBrush = ThemeHelper.Brush(p.Line), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = credStack });

        var testResult = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap };
        var checkBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8),
            Foreground = ThemeHelper.Brush(p.Ok),
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            UseSystemFocusVisuals = false,
            Content = "检查连接",
        };
        checkBtn.Click += async (_, _) =>
        {
            checkBtn.IsEnabled = false;
            testResult.Text = "正在验证连接…";
            testResult.Foreground = ThemeHelper.Brush(p.T3);
            var sw = Stopwatch.StartNew();
            string testBase = urlBox.Text.Trim();
            if (string.IsNullOrEmpty(testBase)) testBase = preset.BaseUrl;
            string testModel = GetProviderModel(preset.Id, preset.DefaultModel);
            string trimmedKey = keyBox.Password.Trim();
            if (!string.IsNullOrEmpty(trimmedKey))
                _library.Secrets.Set($"ai.key.{preset.Id}", trimmedKey);
            if (!string.IsNullOrEmpty(urlBox.Text.Trim()))
                _library.Prefs.AiProviderUrls[preset.Id] = urlBox.Text.Trim();

            var (ok, msg) = await _library.Companion.TestProviderAsync(preset.Id, testBase, testModel, trimmedKey);
            sw.Stop();
            bool autoActivated = false;
            if (ok && !inUse &&
                (string.IsNullOrWhiteSpace(_library.Prefs.AiProvider) ||
                 !_library.Companion.IsConfigured ||
                 _newlyAddedCompanionIds.Remove(preset.Id)))
            {
                ActivateCompanionProvider(preset.Id);
                inUse = true;
                useSw.IsOn = true;
                hStatusTb.Text = "当前使用";
                hStatusTb.Foreground = ThemeHelper.Brush(p.Ok);
                autoActivated = true;
                RecordChange("书伴服务", ShortProviderTitle(preset.Id, preset.Name));
            }
            else if (ok)
            {
                _newlyAddedCompanionIds.Remove(preset.Id);
                if (inUse) ActivateCompanionProvider(preset.Id);
                else _library.SavePrefs();
            }

            testResult.Text = ok
                ? (autoActivated ? $"✓ 正常 · {sw.Elapsed.TotalSeconds:0.1} 秒 · 已设为当前服务" : $"✓ 正常 · {sw.Elapsed.TotalSeconds:0.1} 秒")
                : msg;
            testResult.Foreground = ThemeHelper.Brush(ok ? p.Ok : p.Danger);
            checkBtn.IsEnabled = true;
            RenderDynamicPage("companion");
        };
        InspectorHost.Children.Add(checkBtn);
        InspectorHost.Children.Add(testResult);

        if (_library.Prefs.ConfiguredAiProviders.Count > 1)
        {
            var rmBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = ThemeHelper.Brush(p.Danger),
                FontSize = 11.5,
                Content = $"移除 {ShortProviderTitle(preset.Id, preset.Name)}",
            };
            rmBtn.Click += async (_, _) =>
            {
                var confirm = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = $"移除 {ShortProviderTitle(preset.Id, preset.Name)}？",
                    Content = "会同时清除这台电脑上保存的 API Key，不会影响你的云端账号。",
                    PrimaryButtonText = "移除",
                    CloseButtonText = "取消",
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

                _library.Prefs.ConfiguredAiProviders.Remove(preset.Id);
                _library.Secrets.Set($"ai.key.{preset.Id}", "");
                if (inUse && _library.Prefs.ConfiguredAiProviders.Count > 0)
                    ActivateCompanionProvider(_library.Prefs.ConfiguredAiProviders[0]);
                _focusedCompanionId = _library.Prefs.AiProvider;
                _library.SavePrefs();
                RenderDynamicPage("companion");
                RenderInspector("companion");
            };
            InspectorHost.Children.Add(rmBtn);
        }
    }

    // ---------- 6. 同步（1:1 MacSyncSettings.swift） ----------

    private void BuildSyncPage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        bool isOn = _library.Sync.IsEnabled;

        // 笔记同步冲突提示条（1:1 MacSyncSettings.swift ConflictsBanner）
        try
        {
            var conflicts = _library.Core.NoteConflicts();
            if (conflicts.Length > 0)
            {
                var cfGrid = new Grid { Padding = new Thickness(14, 10, 14, 10) };
                cfGrid.Children.Add(new TextBlock
                {
                    Text = $"⚠️ 有 {conflicts.Length} 条笔记在两台设备上各有不同改动",
                    FontSize = 12.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var resBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                    Content = "查看并解决…",
                    Height = 26,
                    MinHeight = 26,
                    FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                resBtn.Click += async (_, _) => await SnapshotAndConflictDialogs.ShowNoteConflictsSheetAsync(RootGrid.XamlRoot, _library, () =>
                {
                    App.CurrentApp.NotifyShelfRefresh();
                    RenderDynamicPage("sync");
                });
                cfGrid.Children.Add(resBtn);
                DynamicPageHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(tint.Soft),
                    BorderBrush = ThemeHelper.Brush(tint.Edge),
                    BorderThickness = new Thickness(1),
                    Child = cfGrid,
                });
            }
        }
        catch { }

        var statusOuter = new StackPanel { Spacing = 8, Padding = new Thickness(14, 10, 14, 10) };
        var topLine = new Grid { ColumnSpacing = 12 };
        topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var (iconName, iconCol) = _library.Sync.State switch
        {
            SyncStateKind.Syncing => ("sync-arrows", tint.Accent),
            SyncStateKind.Failed => ("info-circle", p.Danger),
            _ when isOn => ("checkmark", p.Ok),
            _ => ("cloud-slash", p.T3),
        };
        topLine.Children.Add(new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = ThemeHelper.Brush(Color.FromArgb(26, iconCol.R, iconCol.G, iconCol.B)),
            Child = YYIcons.Make(iconName, 14, ThemeHelper.Brush(iconCol)),
        });

        var statusMid = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        statusMid.Children.Add(new TextBlock
        {
            Text = isOn ? "已开启同步" : "尚未开启同步",
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
        });
        statusMid.Children.Add(new TextBlock
        {
            Text = isOn ? _library.Sync.StatusSummary() : "选择下方任一方式，跨设备同步进度、笔记与设置",
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(statusMid, 1);
        topLine.Children.Add(statusMid);

        if (isOn)
        {
            var syncNowBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "立即同步" };
            syncNowBtn.Click += async (_, _) =>
            {
                syncNowBtn.IsEnabled = false;
                await _library.Sync.SyncNowAsync();
                App.CurrentApp.NotifyShelfRefresh();
                RenderDynamicPage("sync");
            };
            Grid.SetColumn(syncNowBtn, 2);
            topLine.Children.Add(syncNowBtn);
        }
        statusOuter.Children.Add(topLine);

        var devList = _library.GetDevices();
        if (devList.Count > 0)
        {
            var devGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            for (int c = 0; c < 3; c++) devGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int devRows = (devList.Count + 2) / 3;
            for (int r = 0; r < devRows; r++) devGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < devList.Count; i++)
            {
                var d = devList[i];
                var dGrid = new Grid { ColumnSpacing = 8 };
                dGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                dGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                dGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                string platLow = (d.Platform ?? "").ToLowerInvariant();
                string devIcon = platLow switch
                {
                    "ipados" or "tablet" => "tablet",
                    "ios" => (d.Name?.Contains("iPad", StringComparison.OrdinalIgnoreCase) == true) ? "tablet" : "phone",
                    "android" => (d.Name?.Contains("Pad", StringComparison.OrdinalIgnoreCase) == true || d.Name?.Contains("Tab", StringComparison.OrdinalIgnoreCase) == true) ? "tablet" : "phone",
                    _ => "laptop",
                };
                dGrid.Children.Add(YYIcons.Make(devIcon, 14, ThemeHelper.Brush(p.T2)));

                var dText = new StackPanel { Spacing = 1 };
                dText.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(d.Name) ? Environment.MachineName : d.Name, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                string platLabel = platLow switch { "ios" => "iPhone / iPad", "ipados" => "iPad", "android" => "Android", "macos" => "Mac", _ => "Windows" };
                string whenStr = d.ThisDevice ? "刚刚" : platLabel;
                if (!d.ThisDevice && d.LastAt is long lat && lat > 0)
                {
                    var ago = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(lat);
                    whenStr = ago.TotalMinutes < 2 ? "刚刚"
                        : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} 分钟前"
                        : ago.TotalDays < 1 ? $"{(int)ago.TotalHours} 小时前"
                        : $"{(int)ago.TotalDays} 天前";
                }
                string? resolvedBook = null;
                if (!string.IsNullOrWhiteSpace(d.LastBook))
                {
                    var matchBook = _library.GetKeptBooks().FirstOrDefault(b => b.Id == d.LastBook);
                    if (matchBook is not null) resolvedBook = matchBook.Title;
                    else if (d.LastBook.Length < 24 || !d.LastBook.All(c => char.IsAsciiHexDigit(c) || c == '-')) resolvedBook = d.LastBook;
                }
                string bookLine = !string.IsNullOrWhiteSpace(resolvedBook)
                    ? $"{whenStr} · 《{resolvedBook}》{(d.LastFraction is double lf ? $" {Math.Round(lf * 100)}%" : "")}"
                    : whenStr;
                dText.Children.Add(new TextBlock { Text = bookLine, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(dText, 1);
                dGrid.Children.Add(dText);

                if (d.ThisDevice)
                {
                    var thisTb = new TextBlock { Text = "这台", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(thisTb, 2);
                    dGrid.Children.Add(thisTb);
                }

                var pill = new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(10, 6, 10, 6), Child = dGrid };
                Grid.SetRow(pill, i / 3);
                Grid.SetColumn(pill, i % 3);
                devGrid.Children.Add(pill);
            }
            statusOuter.Children.Add(devGrid);
        }
        DynamicPageHost.Children.Add(MakeCard(statusOuter));

        // 2. 同步方式（激活卡片支持右键「停止同步」，1:1 MacSyncSettings.swift:257-259）
        var waysHeading = MakeHeading("同步方式");
        waysHeading.Margin = new Thickness(14, 2, 0, -6);
        DynamicPageHost.Children.Add(waysHeading);
        var waysGrid = new Grid { ColumnSpacing = 12 };
        for (int c = 0; c < 3; c++) waysGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        UIElement BuildMethodTile(int col, string methodKey, string icon, string title, string detail, bool disabled = false)
        {
            bool on = !disabled && _library.Prefs.SyncMethod == methodKey;
            var stack = new StackPanel { Spacing = 1.5 };
            var iconLine = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            iconLine.Children.Add(new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(7.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = ThemeHelper.Brush(on ? tint.Soft : p.Inset),
                Child = YYIcons.Make(icon, 13, ThemeHelper.Brush(on ? tint.Accent : p.T1)),
            });
            if (on)
            {
                var chk = YYIcons.Make("checkmark", 13, ThemeHelper.Brush(tint.Accent));
                chk.HorizontalAlignment = HorizontalAlignment.Right;
                iconLine.Children.Add(chk);
            }
            stack.Children.Add(iconLine);
            stack.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            stack.Children.Add(new TextBlock { Text = detail, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });

            var border = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(on ? tint.Accent : p.Line),
                BorderThickness = new Thickness(on ? 1.5 : 1),
                Padding = new Thickness(13, 9, 13, 9),
                Opacity = disabled ? 0.55 : 1.0,
                Child = stack,
            };

            if (disabled) { Grid.SetColumn(border, col); return border; }

            MenuFlyout? ctx = null;
            if (on)
            {
                ctx = new MenuFlyout();
                var stopMi = new MenuFlyoutItem { Text = "停止同步", Foreground = ThemeHelper.Brush(p.Danger) };
                stopMi.Click += (_, _) =>
                {
                    _library.Sync.StopSync();
                    RecordChange("同步", "已停止");
                    App.CurrentApp.NotifyShelfRefresh();
                    RenderDynamicPage("sync");
                };
                ctx.Items.Add(stopMi);
            }

            var btn = new Button
            {
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                UseSystemFocusVisuals = false,
                Content = border,
                ContextFlyout = ctx,
            };
            btn.Click += async (_, _) =>
            {
                if (methodKey == "folder")
                {
                    _editingWebDav = false;
                    _editingGDrive = false;
                    var picker = new FolderPicker();
                    picker.FileTypeFilter.Add("*");
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                    var folder = await picker.PickSingleFolderAsync();
                    if (folder is not null)
                    {
                        _library.Prefs.SyncMethod = "folder";
                        _library.Prefs.SyncLocalFolder = folder.Path;
                        _library.SavePrefs();
                        await _library.Sync.SyncNowAsync();
                        App.CurrentApp.NotifyShelfRefresh();
                        RenderDynamicPage("sync");
                    }
                }
                else if (methodKey == "webdav")
                {
                    _editingGDrive = false;
                    _editingWebDav = !_editingWebDav;
                    RenderDynamicPage("sync");
                }
                else if (methodKey == "gdrive")
                {
                    _editingWebDav = false;
                    _editingGDrive = !_editingGDrive;
                    RenderDynamicPage("sync");
                }
            };
            Grid.SetColumn(btn, col);
            return btn;
        }

        string gdriveSub = _library.Prefs.SyncMethod == "gdrive" && !string.IsNullOrWhiteSpace(_library.Prefs.GoogleEmail)
            ? _library.Prefs.GoogleEmail
            : "隐藏应用目录 · 跨 Android / iOS 汇合";
        waysGrid.Children.Add(BuildMethodTile(0, "folder", "cloud", "个人云盘", "OneDrive / iCloud 云盘同步文件夹"));
        waysGrid.Children.Add(BuildMethodTile(1, "webdav", "server", "WebDAV", "坚果云、群晖、Nextcloud…"));
        waysGrid.Children.Add(BuildMethodTile(2, "gdrive", "globe", "Google Drive", gdriveSub));
        DynamicPageHost.Children.Add(waysGrid);

        if (_editingGDrive)
        {
            var gStatus = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var gLoginBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Right,
                Content = _library.Prefs.SyncMethod == "gdrive" ? "重新登录 Google 账号" : "用 Google 账号登录并开启同步",
            };
            gLoginBtn.Click += async (_, _) =>
            {
                gLoginBtn.IsEnabled = false;
                gStatus.Text = "已在浏览器打开 Google 授权页，完成授权后自动开启同步…";
                try
                {
                    await _library.Sync.ConnectGoogleDriveAsync();
                    _editingGDrive = false;
                    RecordChange("同步", "Google Drive");
                    App.CurrentApp.NotifyShelfRefresh();
                    RenderDynamicPage("sync");
                }
                catch (Exception ex)
                {
                    gStatus.Text = ex.Message;
                    gStatus.Foreground = ThemeHelper.Brush(p.Danger);
                    gLoginBtn.IsEnabled = true;
                }
            };
            var gFoot = new Grid { Padding = new Thickness(16, 10, 16, 10) };
            gFoot.Children.Add(gStatus);
            gFoot.Children.Add(gLoginBtn);

            DynamicPageHost.Children.Add(MakeCard(
                MakeInlineInputRow("Client ID", _library.Sync.EffectiveGoogleClientId, "xxxx.apps.googleusercontent.com", v => { _library.Prefs.GoogleClientId = v.Trim(); _library.SavePrefs(); }),
                MakeInlineInputRow("Client Secret", _library.Secrets.Get("sync.google.secret"), "桌面端 OAuth 客户端密钥（如适用）", v => _library.Secrets.Set("sync.google.secret", v.Trim()), isPassword: true),
                gFoot));
        }
        else if (_editingWebDav)
        {
            var checkNote = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var checkCardHost = new ContentControl { Margin = new Thickness(0, 6, 0, 0) };
            var connectBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = _library.Prefs.SyncMethod == "webdav" ? "重新检查并连接" : "检查连接并开启同步" };
            connectBtn.Click += async (_, _) =>
            {
                connectBtn.IsEnabled = false;
                checkNote.Text = "正在依次检查服务器、TLS 证书、账号密码与读写权限…";
                var report = await WebDavCheckService.RunAsync(
                    _library.Prefs.WebDavUrl, _library.Prefs.WebDavUser,
                    _library.Secrets.Get("sync.webdav.password"), _library.Prefs.WebDavFolder);
                checkCardHost.Content = WebDavCheckService.BuildCheckCard(report, p, tint);
                checkNote.Text = report.AllPassed ? $"✓ 四项检查全部通过（{report.ElapsedSeconds:0.1} 秒）" : "请根据下方检查结果修正后重试";
                checkNote.Foreground = ThemeHelper.Brush(report.AllPassed ? p.Ok : p.Danger);
                connectBtn.IsEnabled = true;
                if (report.AllPassed)
                {
                    _library.Prefs.SyncMethod = "webdav";
                    _library.SavePrefs();
                    await _library.Sync.SyncNowAsync();
                    _editingWebDav = false;
                    App.CurrentApp.NotifyShelfRefresh();
                    RenderDynamicPage("sync");
                }
            };

            var footBar = new Grid { Padding = new Thickness(16, 10, 16, 10) };
            footBar.Children.Add(checkNote);
            connectBtn.HorizontalAlignment = HorizontalAlignment.Right;
            footBar.Children.Add(connectBtn);

            DynamicPageHost.Children.Add(MakeCard(
                MakeInlineInputRow("服务器地址", _library.Prefs.WebDavUrl, "https://dav.jianguoyun.com/dav/", v => { _library.Prefs.WebDavUrl = v.Trim(); _library.SavePrefs(); }),
                MakeInlineInputRow("账号", _library.Prefs.WebDavUser, "坚果云邮箱或用户名", v => { _library.Prefs.WebDavUser = v.Trim(); _library.SavePrefs(); }),
                MakeInlineInputRow("密码", _library.Secrets.Get("sync.webdav.password"), "填“应用密码”；仅加密存在本机", v => _library.Secrets.Set("sync.webdav.password", v), isPassword: true),
                MakeInlineInputRow("文件夹", _library.Prefs.WebDavFolder, "语阅", v => { _library.Prefs.WebDavFolder = v.Trim(); _library.SavePrefs(); }),
                footBar));
            DynamicPageHost.Children.Add(checkCardHost);
        }
        else if (_library.Prefs.SyncMethod == "webdav")
        {
            var editBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "修改" };
            editBtn.Click += (_, _) => { _editingWebDav = true; RenderDynamicPage("sync"); };
            DynamicPageHost.Children.Add(MakeCard(MakeRow("WebDAV 服务器", $"{_library.Prefs.WebDavUrl} · {_library.Prefs.WebDavUser}", editBtn, compact: true)));
        }
        else if (_library.Prefs.SyncMethod == "gdrive")
        {
            var gOutBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = "退出",
                Foreground = ThemeHelper.Brush(p.Danger),
            };
            gOutBtn.Click += (_, _) =>
            {
                _library.Sync.StopSync();
                RecordChange("Google 账号", "已退出");
                App.CurrentApp.NotifyShelfRefresh();
                RenderDynamicPage("sync");
            };
            string gAccountDetail = string.IsNullOrWhiteSpace(_library.Prefs.GoogleEmail)
                ? "存放于 Google Drive 应用专属目录"
                : $"{_library.Prefs.GoogleEmail} · 存放于 Google Drive 应用专属目录";
            DynamicPageHost.Children.Add(MakeCard(MakeRow("Google 账号", gAccountDetail, gOutBtn, compact: true)));
        }
        else if (_library.Prefs.SyncMethod == "folder" && !string.IsNullOrEmpty(_library.Prefs.SyncLocalFolder))
        {
            var folderBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var changeFolderBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "更换文件夹" };
            changeFolderBtn.Click += async (_, _) =>
            {
                var picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var picked = await picker.PickSingleFolderAsync();
                if (picked is not null)
                {
                    _library.Prefs.SyncLocalFolder = picked.Path;
                    _library.SavePrefs();
                    await _library.Sync.SyncNowAsync();
                    App.CurrentApp.NotifyShelfRefresh();
                    RenderDynamicPage("sync");
                }
            };
            var openFolderBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "在资源管理器中查看" };
            openFolderBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_library.Prefs.SyncLocalFolder}\"") { UseShellExecute = true });
            folderBtns.Children.Add(changeFolderBtn);
            folderBtns.Children.Add(openFolderBtn);
            DynamicPageHost.Children.Add(MakeCard(MakeRow("同步文件夹", _library.Prefs.SyncLocalFolder, folderBtns, compact: true)));
        }

        // 3. 同步内容（未开启同步时文本保持清晰、仅开关置灰禁用，1:1 05-sync.png）
        var contentHeading = MakeHeading("同步内容");
        contentHeading.Margin = new Thickness(14, 2, 0, -6);
        DynamicPageHost.Children.Add(contentHeading);
        string placeName = _library.Prefs.SyncMethod switch
        {
            "webdav" => "网盘",
            "gdrive" => "Google 云端硬盘",
            _ => "个人云盘",
        };
        var filesRowSub = new TextBlock
        {
            Text = $"正在计算书文件大小… · 占用你的{placeName}空间；播客单集体积很小，始终同步",
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 14,
        };
        var filesRowLeft = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        filesRowLeft.Children.Add(new TextBlock { Text = "书文件", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
        filesRowLeft.Children.Add(filesRowSub);
        var filesSw = MakeSwitch(_library.Prefs.SyncFiles, v =>
        {
            _library.Prefs.SyncFiles = v;
            _library.SavePrefs();
            RecordChange("同步书文件", v ? "开" : "关");
            if (_library.Sync.IsEnabled) _ = _library.Sync.SyncNowAsync();
        }, enabled: isOn);
        filesSw.VerticalAlignment = VerticalAlignment.Center;
        var filesRow = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 4, 16, 4), MinHeight = 36 };
        filesRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filesRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        filesRow.Children.Add(filesRowLeft);
        Grid.SetColumn(filesSw, 1);
        filesRow.Children.Add(filesSw);

        string booksDir = Path.Combine(_library.DataDir, "books");
        _ = Task.Run(() => DirSize(booksDir)).ContinueWith(t =>
        {
            long bytes = t.Status == TaskStatus.RanToCompletion ? t.Result : 0;
            DispatcherQueue.TryEnqueue(() =>
            {
                filesRowSub.Text = L10n.T($"{FormatBytes(bytes)} · 占用你的{placeName}空间；播客单集体积很小，始终同步");
            });
        });

        Grid MakeSyncRow(string title, string? sub, ToggleSwitch sw)
        {
            var g = MakeRow(title, sub, sw, compact: true);
            g.Padding = new Thickness(16, string.IsNullOrEmpty(sub) ? 2 : 4, 16, string.IsNullOrEmpty(sub) ? 2 : 4);
            g.MinHeight = 34;
            return g;
        }

        var syncContentCard = MakeCard(
            MakeSyncRow("进度和书签", null, MakeSwitch(_library.Prefs.SyncReading, v => { _library.Prefs.SyncReading = v; _library.SavePrefs(); RecordChange("同步进度和书签", v ? "开" : "关"); if (_library.Sync.IsEnabled) _ = _library.Sync.SyncNowAsync(); }, enabled: isOn)),
            MakeSyncRow("笔记和生词", null, MakeSwitch(_library.Prefs.SyncNotes, v => { _library.Prefs.SyncNotes = v; _library.SavePrefs(); RecordChange("同步笔记和生词", v ? "开" : "关"); if (_library.Sync.IsEnabled) _ = _library.Sync.SyncNowAsync(); }, enabled: isOn)),
            MakeSyncRow("设置和替换规则", null, MakeSwitch(_library.Prefs.SyncSettings, v => { _library.Prefs.SyncSettings = v; _library.SavePrefs(); RecordChange("同步设置", v ? "开" : "关"); if (_library.Sync.IsEnabled) _ = _library.Sync.SyncNowAsync(); }, enabled: isOn)),
            filesRow,
            MakeSyncRow("有声书录音", "同步完整音频文件；关闭时仅同步句级对齐信息，其他设备导入同名音频即可直接跟读", MakeSwitch(_library.Prefs.SyncRecordings, v => { _library.Prefs.SyncRecordings = v; _library.SavePrefs(); RecordChange("同步录音", v ? "开" : "关"); if (_library.Sync.IsEnabled) _ = _library.Sync.SyncNowAsync(); }, enabled: isOn)));
        DynamicPageHost.Children.Add(syncContentCard);

        var lockFoot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(14, -2, 0, 0) };
        lockFoot.Children.Add(YYIcons.Make("lock", 12, ThemeHelper.Brush(p.T3)));
        lockFoot.Children.Add(new TextBlock { Text = "无需注册账号，数据仅在你的设备与个人网盘之间传输", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        DynamicPageHost.Children.Add(lockFoot);
    }

    // ---------- 7. 笔记导出（1:1 MacNoteExportSettings.swift：4 目标开关 + 右栏上连接/下样张） ----------

    private void BuildExportPage()
    {
        var p = CurrentPalette;
        DynamicPageHost.Children.Add(MakeHeading("导出目标"));

        var rows = new StackPanel { Spacing = 2, Padding = new Thickness(4) };

        void AddTargetRow(int idx, string key, string badge, Color badgeCol, string title, string detail, bool isOn, Action<bool> onToggle)
        {
            if (idx > 0) rows.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
            bool sel = _focusedExportTarget == key;

            var g = new Grid { ColumnSpacing = 12, MinHeight = 48, Padding = new Thickness(12, 8, 12, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            g.Children.Add(YYChrome.MakeServiceTile(key, IsDark, 32));

            var mid = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
            mid.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            var sw = MakeSwitch(isOn, v =>
            {
                _focusedExportTarget = key;
                onToggle(v);
                RenderDynamicPage("export");
                RenderInspector("export");
            });
            sw.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(sw, 2);
            g.Children.Add(sw);

            var wrap = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = sel ? ThemeHelper.Brush(Color.FromArgb(16, p.T1.R, p.T1.G, p.T1.B)) : new SolidColorBrush(Colors.Transparent),
                Child = g,
            };
            wrap.PointerPressed += (_, _) =>
            {
                _focusedExportTarget = key;
                RenderDynamicPage("export");
                RenderInspector("export");
            };
            rows.Children.Add(wrap);
        }

        // Migrate legacy plaintext flomo webhook URL into encrypted SecretStore once
        if (string.IsNullOrEmpty(_library.Secrets.Get("export.flomo.url")) && !string.IsNullOrEmpty(_library.Prefs.FlomoWebhookUrl))
        {
            _library.Secrets.Set("export.flomo.url", _library.Prefs.FlomoWebhookUrl.Trim());
            _library.Prefs.FlomoWebhookUrl = "";
            _library.SavePrefs();
        }

        bool obsConn = !string.IsNullOrEmpty(_library.Prefs.ObsidianFolder);
        bool notConn = !string.IsNullOrEmpty(_library.Secrets.Get("export.notion.token"));
        bool floConn = NoteExportService.ValidateFlomoWebhook(_library.Secrets.Get("export.flomo.url"));
        bool xinConn = !string.IsNullOrEmpty(_library.Secrets.Get("export.xinzhi.token"));

        AddTargetRow(0, "obsidian", "Ob", Color.FromArgb(255, 124, 58, 237), "Obsidian",
            obsConn ? _library.Prefs.ObsidianFolder : "未连接",
            obsConn && _library.Prefs.ObsidianExportOn,
            v => { _library.Prefs.ObsidianExportOn = v; _library.SavePrefs(); });

        AddTargetRow(1, "notion", "N", p.T1, "Notion",
            notConn ? $"放在“{(string.IsNullOrEmpty(_library.Prefs.NotionPageTitle) ? "读书笔记" : _library.Prefs.NotionPageTitle)}”下面，每本书一页" : "未连接",
            notConn && _library.Prefs.NotionExportOn,
            v => { _library.Prefs.NotionExportOn = v; _library.SavePrefs(); });

        AddTargetRow(2, "flomo", "fl", Color.FromArgb(255, 46, 158, 91), "flomo",
            floConn ? "每条划线一条笔记" : "未连接",
            floConn && _library.Prefs.FlomoExportOn,
            v => { _library.Prefs.FlomoExportOn = v; _library.SavePrefs(); });

        AddTargetRow(3, "xinzhi", "枝", Color.FromArgb(255, 59, 130, 246), "新枝",
            xinConn ? "每条划线一条笔记" : "未连接",
            xinConn && _library.Prefs.XinzhiExportOn,
            v => { _library.Prefs.XinzhiExportOn = v; _library.SavePrefs(); });

        DynamicPageHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Child = rows,
        });
        DynamicPageHost.Children.Add(MakeNote("开启的项目会显示在笔记页「导出」菜单中。令牌与目录仅保存在本机，不同步、不上传。"));
    }

    private void BuildExportInspector(YYPalette p, YYTintColors tint)
    {
        // 上半区：连接与目录设置面板（1:1 MacNoteExportSettings.swift:185-695：实时验证、立即发送、保留去重的断开）
        var setupStack = new StackPanel { Spacing = 8 };
        string initialStatus = _library.Prefs.ExportLastSummary.TryGetValue(_focusedExportTarget, out var lastSum) ? lastSum : "";
        var statusNote = new TextBlock
        {
            Text = initialStatus,
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrEmpty(initialStatus) ? Visibility.Collapsed : Visibility.Visible,
        };
        void SetExportStatus(string msg, Color col)
        {
            statusNote.Text = msg;
            statusNote.Foreground = ThemeHelper.Brush(col);
            statusNote.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
        }

        switch (_focusedExportTarget)
        {
            case "notion":
            {
                bool conn = !string.IsNullOrEmpty(_library.Secrets.Get("export.notion.token")) && !string.IsNullOrEmpty(_library.Prefs.NotionPageId);
                setupStack.Children.Add(new TextBlock { Text = conn ? $"Notion · 已连至「{_library.Prefs.NotionPageTitle}」" : "连接 Notion", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                var tkBox = new PasswordBox { Header = "Integration Token", Password = _library.Secrets.Get("export.notion.token"), PlaceholderText = "ntn_ 或 secret_..." };
                var pgBox = new TextBox { Header = "目标父页面链接或 32 位 ID", Text = !string.IsNullOrEmpty(_library.Prefs.NotionPageId) ? _library.Prefs.NotionPageId : _library.Prefs.NotionPageTitle, PlaceholderText = "粘贴 Notion 页面链接…" };

                // Clipboard auto-detect for Notion Token or Page URL
                var clipBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    FontSize = 11,
                    Height = 25,
                    MinHeight = 25,
                    Visibility = Visibility.Collapsed,
                };
                _ = Task.Run(async () =>
                {
                    string? clipText = null;
                    try
                    {
                        var tcs = new TaskCompletionSource<string?>();
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            try
                            {
                                var pkg = Clipboard.GetContent();
                                if (pkg.Contains(StandardDataFormats.Text))
                                    tcs.TrySetResult((await pkg.GetTextAsync()).Trim());
                                else
                                    tcs.TrySetResult(null);
                            }
                            catch { tcs.TrySetResult(null); }
                        });
                        clipText = await tcs.Task;
                    }
                    catch { }
                    if (string.IsNullOrWhiteSpace(clipText)) return;
                    bool isTok = clipText.StartsWith("ntn_", StringComparison.Ordinal) || clipText.StartsWith("secret_", StringComparison.Ordinal);
                    bool isPage = clipText.Contains("notion.so/", StringComparison.OrdinalIgnoreCase) && NoteExportService.ExtractNotionPageId(clipText) is not null;
                    if (!isTok && !isPage) return;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        clipBtn.Content = isTok ? "✨ 填入剪贴板中的 Notion Token" : "✨ 填入剪贴板中的 Notion 页面链接";
                        clipBtn.Visibility = Visibility.Visible;
                        clipBtn.Click += (_, _) =>
                        {
                            if (isTok) tkBox.Password = clipText;
                            else pgBox.Text = clipText;
                            clipBtn.Visibility = Visibility.Collapsed;
                        };
                    });
                });

                var verifyBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = conn ? "重新验证连接" : "验证并连接 Notion" };
                verifyBtn.Click += async (_, _) =>
                {
                    verifyBtn.IsEnabled = false;
                    SetExportStatus("正在校验 Notion Token 与父页面权限…", p.T3);
                    var (ok, pageTitle, msg) = await _library.NoteExports.VerifyNotionAsync(tkBox.Password, pgBox.Text);
                    SetExportStatus(msg, ok ? p.Ok : p.Danger);
                    verifyBtn.IsEnabled = true;
                    if (ok)
                    {
                        _library.Secrets.Set("export.notion.token", tkBox.Password.Trim());
                        _library.Prefs.NotionPageId = NoteExportService.ExtractNotionPageId(pgBox.Text) ?? "";
                        _library.Prefs.NotionPageTitle = pageTitle;
                        _library.Prefs.NotionExportOn = true;
                        _library.SavePrefs();
                        RenderDynamicPage("export");
                        RenderInspector("export");
                    }
                };
                setupStack.Children.Add(clipBtn);
                setupStack.Children.Add(tkBox);
                setupStack.Children.Add(pgBox);
                setupStack.Children.Add(new HyperlinkButton
                {
                    Content = "前往 Notion Integrations 创建令牌 ↗",
                    FontSize = 11,
                    Padding = new Thickness(0, 1, 0, 1),
                    NavigateUri = new Uri("https://www.notion.so/my-integrations"),
                });
                setupStack.Children.Add(verifyBtn);
                if (conn)
                {
                    var sendBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "立即发送全库未同步笔记" };
                    sendBtn.Click += async (_, _) =>
                    {
                        sendBtn.IsEnabled = false;
                        try
                        {
                            var (bCnt, mCnt) = await _library.NoteExports.ExportToNotionAsync();
                            SetExportStatus(mCnt > 0 ? $"✓ 已同步 {mCnt} 条笔记（{bCnt} 本书）" : "✓ 所有划线均已同步过，已是最新", p.Ok);
                        }
                        catch (Exception ex) { SetExportStatus(ex.Message, p.Danger); }
                        finally { sendBtn.IsEnabled = true; }
                    };
                    var openAppBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "在 Notion 中打开目标页面 ↗" };
                    openAppBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri($"https://www.notion.so/{_library.Prefs.NotionPageId}"));
                    var discBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = ThemeHelper.Brush(p.Danger), Content = "断开 Notion" };
                    discBtn.Click += (_, _) => { _library.NoteExports.DisconnectTarget("notion"); RenderDynamicPage("export"); RenderInspector("export"); };
                    setupStack.Children.Add(sendBtn);
                    setupStack.Children.Add(openAppBtn);
                    setupStack.Children.Add(discBtn);
                }
                break;
            }
            case "flomo":
            {
                string curUrl = _library.Secrets.Get("export.flomo.url");
                bool conn = NoteExportService.ValidateFlomoWebhook(curUrl);
                setupStack.Children.Add(new TextBlock { Text = conn ? "flomo · 已连接" : "连接 flomo", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                var wbBox = new TextBox { Header = "专属 API Webhook 地址（本机加密）", Text = curUrl, PlaceholderText = "https://flomoapp.com/iwh/..." };

                var flomoClipBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    FontSize = 11,
                    Height = 25,
                    MinHeight = 25,
                    Content = "✨ 填入剪贴板中的 flomo Webhook",
                    Visibility = Visibility.Collapsed,
                };
                try
                {
                    var pkg = Clipboard.GetContent();
                    if (pkg.Contains(StandardDataFormats.Text))
                    {
                        _ = pkg.GetTextAsync().AsTask().ContinueWith(t =>
                        {
                            if (t.Status != TaskStatus.RanToCompletion) return;
                            string cand = (t.Result ?? "").Trim();
                            if (!NoteExportService.ValidateFlomoWebhook(cand) || string.Equals(cand, curUrl, StringComparison.OrdinalIgnoreCase)) return;
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                flomoClipBtn.Visibility = Visibility.Visible;
                                flomoClipBtn.Click += (_, _) =>
                                {
                                    wbBox.Text = cand;
                                    flomoClipBtn.Visibility = Visibility.Collapsed;
                                };
                            });
                        });
                    }
                }
                catch { }

                var saveWbBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "保存 Webhook" };
                saveWbBtn.Click += (_, _) =>
                {
                    string u = wbBox.Text.Trim();
                    if (!NoteExportService.ValidateFlomoWebhook(u))
                    {
                        SetExportStatus("请输入以 https://flomoapp.com/iwh/ 开头的专属 API 地址", p.Danger);
                        return;
                    }
                    _library.Secrets.Set("export.flomo.url", u);
                    _library.Prefs.FlomoWebhookUrl = "";
                    _library.Prefs.FlomoExportOn = true;
                    _library.SavePrefs();
                    RenderDynamicPage("export");
                    RenderInspector("export");
                };
                setupStack.Children.Add(flomoClipBtn);
                setupStack.Children.Add(wbBox);
                setupStack.Children.Add(new HyperlinkButton
                {
                    Content = "去 flomo 获取专属 API Webhook ↗",
                    FontSize = 11,
                    Padding = new Thickness(0, 1, 0, 1),
                    NavigateUri = new Uri("https://v.flomoapp.com/mine?source=incoming_webhook"),
                });
                setupStack.Children.Add(saveWbBtn);
                if (conn)
                {
                    var sendBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "立即发送全库未同步划线" };
                    sendBtn.Click += async (_, _) =>
                    {
                        sendBtn.IsEnabled = false;
                        try
                        {
                            var (_, mCnt) = await _library.NoteExports.ExportToFlomoAsync();
                            SetExportStatus(mCnt > 0 ? $"✓ 已发送 {mCnt} 条卡片到 flomo" : "✓ 已是最新，没有未发过的新划线", p.Ok);
                        }
                        catch (Exception ex) { SetExportStatus(ex.Message, p.Danger); }
                        finally { sendBtn.IsEnabled = true; }
                    };
                    var openFlomoBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "打开 flomo ↗" };
                    openFlomoBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri("https://v.flomoapp.com"));
                    var discBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = ThemeHelper.Brush(p.Danger), Content = "断开 flomo" };
                    discBtn.Click += (_, _) => { _library.NoteExports.DisconnectTarget("flomo"); RenderDynamicPage("export"); RenderInspector("export"); };
                    setupStack.Children.Add(sendBtn);
                    setupStack.Children.Add(openFlomoBtn);
                    setupStack.Children.Add(discBtn);
                }
                break;
            }
            case "xinzhi":
            {
                bool conn = !string.IsNullOrEmpty(_library.Secrets.Get("export.xinzhi.token"));
                setupStack.Children.Add(new TextBlock { Text = conn ? $"新枝 · 已连接（{_library.Prefs.XinzhiConnected}）" : "连接新枝", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                var xzBox = new PasswordBox { Header = "CLI Token", Password = _library.Secrets.Get("export.xinzhi.token"), PlaceholderText = "粘贴新枝 CLI Token…" };
                var verifyBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = conn ? "重新验证" : "验证并连接新枝" };
                verifyBtn.Click += async (_, _) =>
                {
                    verifyBtn.IsEnabled = false;
                    SetExportStatus("正在验证新枝账号…", p.T3);
                    var (ok, uName, msg) = await _library.NoteExports.VerifyXinzhiAsync(xzBox.Password);
                    SetExportStatus(msg, ok ? p.Ok : p.Danger);
                    verifyBtn.IsEnabled = true;
                    if (ok)
                    {
                        _library.Secrets.Set("export.xinzhi.token", xzBox.Password.Trim());
                        _library.Prefs.XinzhiConnected = uName;
                        _library.Prefs.XinzhiExportOn = true;
                        _library.SavePrefs();
                        RenderDynamicPage("export");
                        RenderInspector("export");
                    }
                };
                setupStack.Children.Add(xzBox);
                setupStack.Children.Add(new HyperlinkButton
                {
                    Content = "前往新枝官网获取 CLI Token ↗",
                    FontSize = 11,
                    Padding = new Thickness(0, 1, 0, 1),
                    NavigateUri = new Uri("https://xinzhi.zone"),
                });
                setupStack.Children.Add(verifyBtn);
                if (conn)
                {
                    var sendBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "立即发送全库未同步划线" };
                    sendBtn.Click += async (_, _) =>
                    {
                        sendBtn.IsEnabled = false;
                        try
                        {
                            var (_, mCnt) = await _library.NoteExports.ExportToXinzhiAsync();
                            SetExportStatus(mCnt > 0 ? $"✓ 已发送 {mCnt} 条笔记到新枝" : "✓ 已是最新，没有未发过的新划线", p.Ok);
                        }
                        catch (Exception ex) { SetExportStatus(ex.Message, p.Danger); }
                        finally { sendBtn.IsEnabled = true; }
                    };
                    var openXzBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "打开新枝 ↗" };
                    openXzBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri("https://xinzhi.zone"));
                    var discBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = ThemeHelper.Brush(p.Danger), Content = "断开新枝" };
                    discBtn.Click += (_, _) => { _library.NoteExports.DisconnectTarget("xinzhi"); RenderDynamicPage("export"); RenderInspector("export"); };
                    setupStack.Children.Add(sendBtn);
                    setupStack.Children.Add(openXzBtn);
                    setupStack.Children.Add(discBtn);
                }
                break;
            }
            default:
            {
                // 1:1 settings-notes/1.png: unboxed Obsidian header + full-width crisp folder picker button + caption
                bool conn = !string.IsNullOrEmpty(_library.Prefs.ObsidianFolder);
                var obsHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                obsHead.Children.Add(YYChrome.MakeServiceTile("obsidian", IsDark, 30));
                var obsMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                obsMeta.Children.Add(new TextBlock { Text = conn ? "Obsidian · 已连接" : "连接 Obsidian", FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                obsMeta.Children.Add(new TextBlock { Text = conn ? Path.GetFileName(_library.Prefs.ObsidianFolder) : "选择笔记库中的文件夹", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                obsHead.Children.Add(obsMeta);
                setupStack.Children.Add(obsHead);

                var pickSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                pickSp.Children.Add(YYIcons.Make("folder-plus", 14, ThemeHelper.Brush(p.T1)));
                pickSp.Children.Add(new TextBlock { Text = conn ? "更换文件夹…" : "选一个文件夹…", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
                var pickBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Height = 40,
                    Padding = new Thickness(12, 0, 12, 0),
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    UseSystemFocusVisuals = false,
                    Content = pickSp,
                };
                pickBtn.Click += async (_, _) =>
                {
                    var picker = new FolderPicker();
                    picker.FileTypeFilter.Add("*");
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                    var f = await picker.PickSingleFolderAsync();
                    if (f is null) return;
                    _library.Prefs.ObsidianFolder = f.Path;
                    _library.Prefs.ObsidianExportOn = true;
                    _library.SavePrefs();
                    RecordChange("Obsidian 目录", Path.GetFileName(f.Path));
                    RenderDynamicPage("export");
                    RenderInspector("export");
                };
                setupStack.Children.Add(pickBtn);
                setupStack.Children.Add(new TextBlock { Text = conn ? _library.Prefs.ObsidianFolder : "选 Obsidian 库，或库里的一个文件夹。每本书写成一篇 Markdown，文件名就是书名，带作者和标签。", FontSize = 11, LineHeight = 17, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
                if (conn)
                {
                    var sendBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "立即生成全部书籍 Markdown" };
                    sendBtn.Click += async (_, _) =>
                    {
                        try
                        {
                            var (bCnt, mCnt) = await _library.NoteExports.ExportToObsidianAsync();
                            SetExportStatus(mCnt > 0 ? $"✓ 已写入 {bCnt} 本书共 {mCnt} 条笔记" : "✓ 已是最新", p.Ok);
                        }
                        catch (Exception ex) { SetExportStatus(ex.Message, p.Danger); }
                    };
                    var openObsBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "在 Obsidian 中打开 ↗" };
                    openObsBtn.Click += async (_, _) =>
                    {
                        string folder = _library.Prefs.ObsidianFolder;
                        bool launched = false;
                        try
                        {
                            launched = await Launcher.LaunchUriAsync(new Uri($"obsidian://open?path={Uri.EscapeDataString(folder)}"));
                        }
                        catch { }
                        if (!launched && Directory.Exists(folder))
                            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                    };
                    var disc = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Foreground = ThemeHelper.Brush(p.Danger), Content = "断开 Obsidian" };
                    disc.Click += (_, _) => { _library.NoteExports.DisconnectTarget("obsidian"); RenderDynamicPage("export"); RenderInspector("export"); };
                    setupStack.Children.Add(sendBtn);
                    setupStack.Children.Add(openObsBtn);
                    setupStack.Children.Add(disc);
                }
                break;
            }
        }
        setupStack.Children.Add(statusNote);
        if (_focusedExportTarget == "obsidian")
        {
            InspectorHost.Children.Add(setupStack);
        }
        else
        {
            InspectorHost.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = ThemeHelper.Brush(p.Card), BorderBrush = ThemeHelper.Brush(p.Line), BorderThickness = new Thickness(0.5), Padding = new Thickness(12), Child = setupStack });
        }
        InspectorHost.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 4, 0, 2) });

        // 下半区：4 目标专属 ExportSample 预览（1:1 settings-notes/1.png）
        string targetName = _focusedExportTarget switch { "notion" => "Notion", "flomo" => "flomo", "xinzhi" => "新枝", _ => "Obsidian" };
        var sampleHead = new Grid();
        sampleHead.Children.Add(new TextBlock { Text = "样张", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        sampleHead.Children.Add(new TextBlock { Text = $"发到 {targetName} 是这样", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
        InspectorHost.Children.Add(sampleHead);

        var sample = new StackPanel { Spacing = 7 };
        if (_focusedExportTarget == "obsidian")
        {
            var mdMeta = new Grid();
            mdMeta.Children.Add(new TextBlock { Text = "读书笔记 / 故乡.md", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
            mdMeta.Children.Add(new TextBlock { Text = "Markdown", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
            sample.Children.Add(mdMeta);
        }
        if (_focusedExportTarget == "flomo")
        {
            sample.Children.Add(new TextBlock { Text = "#语阅 #故乡", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(Color.FromArgb(255, 46, 158, 91)) });
            sample.Children.Add(new TextBlock { Text = "希望本是无所谓有，无所谓无的。这正如地上的路；其实地上本没有路，走的人多了，也便成了路。", FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap });
            sample.Children.Add(new TextBlock { Text = "💭 这句是全篇的落点", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2) });
        }
        else if (_focusedExportTarget == "xinzhi")
        {
            sample.Children.Add(new TextBlock { Text = "希望本是无所谓有，无所谓无的。", FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()), FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap });
            sample.Children.Add(new TextBlock { Text = "——《故乡》 鲁迅", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
        }
        else
        {
            sample.Children.Add(new TextBlock { Text = "故乡", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            sample.Children.Add(new TextBlock { Text = "鲁迅 · 《呐喊》", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
            Color barCol = _focusedExportTarget == "obsidian" ? Color.FromArgb(175, 124, 58, 237) : tint.Accent;
            foreach (var q in new[] { "我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。", "希望本是无所谓有，无所谓无的。" })
            {
                var qRow = new Grid { ColumnSpacing = 8 };
                qRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.5) });
                qRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                qRow.Children.Add(new Border { CornerRadius = new CornerRadius(1), Background = ThemeHelper.Brush(barCol) });
                var qTb = new TextBlock { Text = q, FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(qTb, 1);
                qRow.Children.Add(qTb);
                sample.Children.Add(qRow);
            }
            if (_focusedExportTarget == "obsidian")
            {
                var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var tg in new[] { "#语阅", "#鲁迅" })
                    tags.Children.Add(new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 2, 7, 2), Background = ThemeHelper.Brush(Color.FromArgb(24, 124, 58, 237)), Child = new TextBlock { Text = tg, FontSize = 10.5, Foreground = ThemeHelper.Brush(Color.FromArgb(255, 124, 58, 237)) } });
                sample.Children.Add(tags);
            }
        }

        InspectorHost.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Card), BorderBrush = ThemeHelper.Brush(p.Line), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = sample });
        InspectorHost.Children.Add(new TextBlock { Text = "用《故乡》的两条划线和一条想法排的", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
    }

    // ---------- 8. 存储（1:1 MacSettings.swift:1248-1407） ----------

    private void BuildStoragePage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;

        var storageHost = new StackPanel { Spacing = 12 };
        storageHost.Children.Add(MakeCard(new TextBlock
        {
            Text = "正在统计本机空间占用…",
            FontSize = 12,
            Foreground = ThemeHelper.Brush(p.T3),
            Padding = new Thickness(16, 14, 16, 14),
        }));
        DynamicPageHost.Children.Add(storageHost);

        string dataDir = _library.DataDir;
        var kept = _library.GetKeptBooks();
        var keptIds = kept.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var audioIds = kept.Where(b => b.Format == BookFormat.Audio).Select(b => b.Id).ToHashSet(StringComparer.Ordinal);

        _ = Task.Run(() =>
        {
            string booksRoot = Path.Combine(dataDir, "books");
            long booksBytes = 0;
            long audioBytes = DirSize(Path.Combine(dataDir, "audiobooks")) + DirSize(Path.Combine(dataDir, "recordings"));
            long deletedBytes = DirSize(Path.Combine(dataDir, "trash"));
            int deletedCount = 0;
            var perBookSizes = new List<(string Title, long Bytes)>();

            if (Directory.Exists(booksRoot))
            {
                var titleMap = kept.ToDictionary(b => b.Id, b => b.Title, StringComparer.Ordinal);
                foreach (var sub in Directory.EnumerateDirectories(booksRoot))
                {
                    string id = Path.GetFileName(sub);
                    long sz = DirSize(sub);
                    if (keptIds.Contains(id))
                    {
                        if (audioIds.Contains(id)) audioBytes += sz;
                        else booksBytes += sz;
                        if (sz > 0 && titleMap.TryGetValue(id, out var ttl))
                            perBookSizes.Add((ttl, sz));
                    }
                    else
                    {
                        deletedBytes += sz;
                        deletedCount++;
                    }
                }
            }

            long voiceBytes = DirSize(Path.Combine(dataDir, "models")) + DirSize(Path.Combine(dataDir, "cache", "kokoro"));
            long qaBytes = DirSize(Path.Combine(dataDir, "companion")) + DirSize(Path.Combine(dataDir, "companion-cache"));
            long totalBytes = DirSize(dataDir);
            long otherBytes = Math.Max(0, totalBytes - booksBytes - audioBytes - voiceBytes - deletedBytes);
            long freeBytes = 0;
            try { freeBytes = new DriveInfo(Path.GetPathRoot(dataDir) ?? "C:\\").AvailableFreeSpace; } catch { }

            var topBooks = perBookSizes.OrderByDescending(x => x.Bytes).Take(12).ToArray();
            return (booksBytes, audioBytes, voiceBytes, deletedBytes, deletedCount, otherBytes, totalBytes, freeBytes, qaBytes, topBooks);
        }).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            var s = t.Result;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_activePage != "storage") return;
                storageHost.Children.Clear();
                var pal = CurrentPalette;
                var tnt = CurrentTint;

                var barWrap = new StackPanel { Spacing = 10, Padding = new Thickness(16) };
                var headLine = new Grid();
                var usedLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                usedLine.Children.Add(new TextBlock { Text = "已用空间", FontSize = 13, Foreground = ThemeHelper.Brush(pal.T2), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) });
                usedLine.Children.Add(new TextBlock { Text = FormatBytes(s.totalBytes), FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(pal.T1) });
                headLine.Children.Add(usedLine);
                if (s.freeBytes > 0)
                    headLine.Children.Add(new TextBlock { Text = $"本机可用 {FormatBytes(s.freeBytes)}", FontSize = 11.5, Foreground = ThemeHelper.Brush(pal.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
                barWrap.Children.Add(headLine);

                // 5 段彩条：书文件 / 有声书录音 / 语音模型 / 已删除 / 其他（1:1 settings-storage/1.png 圆角浅灰底槽）
                Color otherCol = IsDark ? Color.FromArgb(110, 180, 176, 170) : Color.FromArgb(128, 198, 194, 188);
                (string Label, long Bytes, Color Col)[] parts =
                [
                    ("书文件", s.booksBytes, tnt.Fill),
                    ("有声书录音", s.audioBytes, Color.FromArgb(205, tnt.Fill.R, tnt.Fill.G, tnt.Fill.B)),
                    ("语音模型", s.voiceBytes, Color.FromArgb(145, tnt.Fill.R, tnt.Fill.G, tnt.Fill.B)),
                    ("已删除", s.deletedBytes, pal.Danger),
                    ("其他", s.otherBytes, otherCol),
                ];

                var barGrid = new Grid { Height = 8, ColumnSpacing = 2 };
                long sum = Math.Max(1, parts.Sum(x => x.Bytes));
                int colIdx = 0;
                var activeParts = parts.Where(x => x.Bytes > 0).ToArray();
                for (int pi = 0; pi < activeParts.Length; pi++)
                {
                    var pt = activeParts[pi];
                    barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.03, (double)pt.Bytes / sum), GridUnitType.Star) });
                    double leftR = pi == 0 ? 4 : 1.5;
                    double rightR = pi == activeParts.Length - 1 ? 4 : 1.5;
                    var seg = new Border { Background = ThemeHelper.Brush(pt.Col), CornerRadius = new CornerRadius(leftR, rightR, rightR, leftR) };
                    Grid.SetColumn(seg, colIdx++);
                    barGrid.Children.Add(seg);
                }
                barWrap.Children.Add(new Border
                {
                    Height = 8,
                    CornerRadius = new CornerRadius(4),
                    Background = ThemeHelper.Brush(pal.Inset),
                    Child = barGrid,
                });

                var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
                foreach (var pt in activeParts)
                {
                    var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                    item.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = ThemeHelper.Brush(pt.Col), VerticalAlignment = VerticalAlignment.Center });
                    item.Children.Add(new TextBlock { Text = $"{pt.Label} {FormatBytes(pt.Bytes)}", FontSize = 10.5, Foreground = ThemeHelper.Brush(pal.T2) });
                    legend.Children.Add(item);
                }
                barWrap.Children.Add(legend);

                int deletedCount = s.deletedCount;
                var purgeBtn = YYChrome.MakeSettingsActionButton("立即清空", async () =>
                {
                    var dlg = new ContentDialog
                    {
                        Title = "清空最近删除？",
                        Content = "清空后无法再撤销找回已删的书籍文件。",
                        PrimaryButtonText = "立即清空",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = RootGrid.XamlRoot,
                    };
                    if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                    {
                        try { _library.Core.PurgeDeleted(0); RenderDynamicPage("storage"); } catch { }
                    }
                }, IsDark, destructive: true, enabled: s.deletedBytes > 0 || deletedCount > 0);

                var clearQaRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                clearQaRight.Children.Add(new TextBlock { Text = FormatBytes(s.qaBytes), FontSize = 12, Foreground = ThemeHelper.Brush(pal.T2), VerticalAlignment = VerticalAlignment.Center });
                clearQaRight.Children.Add(YYChrome.MakeSettingsActionButton("清理", async () =>
                {
                    var dlg = new ContentDialog
                    {
                        Title = "清理书伴问答缓存？",
                        Content = $"将释放约 {FormatBytes(s.qaBytes)}，不影响已保存的划线和笔记。",
                        PrimaryButtonText = "清理",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = RootGrid.XamlRoot,
                    };
                    if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
                    foreach (var dName in new[] { "companion", "companion-cache" })
                    {
                        try
                        {
                            string dir = Path.Combine(dataDir, dName);
                            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                        }
                        catch { }
                    }
                    RenderDynamicPage("storage");
                }, IsDark, destructive: true, enabled: s.qaBytes > 0));

                var cleanupRows = new List<UIElement>
                {
                    barWrap,
                    MakeRow($"最近删除 · {deletedCount} 本 ({FormatBytes(s.deletedBytes)})", "保留 30 天以便恢复，到期自动清理", purgeBtn),
                };
                if (s.voiceBytes > 0)
                {
                    var rmModelBtn = YYChrome.MakeSettingsActionButton("移除", async () =>
                    {
                        var dlg = new ContentDialog
                        {
                            Title = "移除本地语音模型？",
                            Content = $"将释放 {FormatBytes(s.voiceBytes)}，之后如需使用本地真人朗读或声音克隆需要重新下载。",
                            PrimaryButtonText = "移除",
                            CloseButtonText = "取消",
                            DefaultButton = ContentDialogButton.Close,
                            XamlRoot = RootGrid.XamlRoot,
                        };
                        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
                        _library.Kokoro.DeleteInstalledModel();
                        _library.LiveVoice.DeleteInstalledModels();
                        RenderDynamicPage("storage");
                    }, IsDark, destructive: true);
                    cleanupRows.Add(MakeRow("语音合成模型", FormatBytes(s.voiceBytes), rmModelBtn));
                }
                cleanupRows.Add(MakeRow("书伴问答缓存", "同章节重复提问直接读取本地缓存，不重复调用模型", clearQaRight));
                storageHost.Children.Add(MakeCard(cleanupRows.ToArray()));

                if (s.topBooks.Length > 0)
                {
                    storageHost.Children.Add(MakeHeading(L10n.T("各书占用空间")));
                    storageHost.Children.Add(MakeCard(s.topBooks.Select(b =>
                        (UIElement)L10n.Skip(MakeRow(b.Title, null, new TextBlock { Text = FormatBytes(b.Bytes), FontSize = 12, Foreground = ThemeHelper.Brush(pal.T2) }, compact: true))).ToArray()));
                }

                // 可选离线扩展包（放在「各书占用空间」之后，保持首屏 1:1 对齐 settings-storage/1.png）
                var extRows = new List<UIElement>();
                bool alignerInstalled = Aligner.IsInstalled(dataDir);
                var alignerBtn = new Button
                {
                    Style = (Style)Application.Current.Resources[alignerInstalled ? "YYIconButtonStyle" : "YYPillButtonStyle"],
                    Content = alignerInstalled ? "移除" : "下载 (~303 MB)",
                    Foreground = alignerInstalled ? ThemeHelper.Brush(pal.Danger) : ThemeHelper.Brush(pal.T1),
                    FontSize = 11.5,
                };
                alignerBtn.Click += async (_, _) =>
                {
                    if (Aligner.IsInstalled(dataDir))
                    {
                        Aligner.DeleteModel(dataDir);
                        RenderDynamicPage("storage");
                        return;
                    }
                    alignerBtn.IsEnabled = false;
                    try
                    {
                        await Aligner.EnsureAsync(dataDir, frac =>
                            DispatcherQueue.TryEnqueue(() => alignerBtn.Content = $"下载中 {Math.Round(frac * 100)}%"),
                            CancellationToken.None);
                        RenderDynamicPage("storage");
                    }
                    catch (Exception ex)
                    {
                        alignerBtn.IsEnabled = true;
                        alignerBtn.Content = "重试下载";
                        RecordChange("对齐模型", ex.Message);
                    }
                };
                extRows.Add(MakeRow(
                    "有声书强制对齐模型",
                    alignerInstalled ? "已安装 (mms_fa.int8.onnx · 约 303 MB)" : "本地离线的 MMS 听读自动对齐模型，按需下载",
                    alignerBtn));

                bool svInstalled = SenseVoiceListener.IsInstalled(dataDir);
                var svBtn = new Button
                {
                    Style = (Style)Application.Current.Resources[svInstalled ? "YYIconButtonStyle" : "YYPillButtonStyle"],
                    Content = svInstalled ? "移除" : "下载 (~228 MB)",
                    Foreground = svInstalled ? ThemeHelper.Brush(pal.Danger) : ThemeHelper.Brush(pal.T1),
                    FontSize = 11.5,
                };
                svBtn.Click += async (_, _) =>
                {
                    if (SenseVoiceListener.IsInstalled(dataDir))
                    {
                        SenseVoiceListener.DeleteModel(dataDir);
                        RenderDynamicPage("storage");
                        return;
                    }
                    svBtn.IsEnabled = false;
                    try
                    {
                        await SenseVoiceListener.EnsureAsync(dataDir, frac =>
                            DispatcherQueue.TryEnqueue(() => svBtn.Content = $"下载中 {Math.Round(frac * 100)}%"),
                            CancellationToken.None);
                        RenderDynamicPage("storage");
                    }
                    catch (Exception ex)
                    {
                        svBtn.IsEnabled = true;
                        svBtn.Content = "重试下载";
                        RecordChange("离线转写模型", ex.Message);
                    }
                };
                extRows.Add(MakeRow(
                    "离线语音转写模型 (SenseVoice)",
                    svInstalled ? "已安装 (sense_voice.int8.onnx · 约 228 MB)" : "中英日韩粤高精度本机逐字稿与有声书转文字模型，按需下载",
                    svBtn));

                bool soundsInstalled = SoundLibraryService.IsInstalled(dataDir);
                var soundsBtn = new Button
                {
                    Style = (Style)Application.Current.Resources[soundsInstalled ? "YYIconButtonStyle" : "YYPillButtonStyle"],
                    Content = soundsInstalled ? "移除" : "导入素材包…",
                    Foreground = soundsInstalled ? ThemeHelper.Brush(pal.Danger) : ThemeHelper.Brush(pal.T1),
                    FontSize = 11.5,
                };
                soundsBtn.Click += async (_, _) =>
                {
                    if (SoundLibraryService.IsInstalled(dataDir))
                    {
                        SoundLibraryService.DeleteLibrary(dataDir);
                        RenderDynamicPage("storage");
                        return;
                    }
                    var picker = new FileOpenPicker();
                    picker.FileTypeFilter.Add(".zip");
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                    var zip = await picker.PickSingleFileAsync();
                    if (zip is null) return;
                    try
                    {
                        uint count = await SoundLibraryService.ImportLocalZipAsync(_library.Core, dataDir, zip.Path);
                        RecordChange("音效素材库", $"已加载 {count} 条素材");
                        RenderDynamicPage("storage");
                    }
                    catch (Exception ex)
                    {
                        var errDlg = new ContentDialog
                        {
                            Title = "导入音效素材库失败",
                            Content = ex.Message,
                            CloseButtonText = "好",
                            XamlRoot = RootGrid.XamlRoot,
                        };
                        await errDlg.ShowAsync();
                    }
                };
                extRows.Add(MakeRow(
                    "广播剧音效素材库",
                    soundsInstalled ? "已安装 (sounds-1) · 未安装时自动使用核心内置合成环境音" : "可选扩展（未安装时使用内置环境音与音效混音）",
                    soundsBtn));

                storageHost.Children.Add(MakeHeading("离线扩展包"));
                storageHost.Children.Add(MakeCard(extRows.ToArray()));
                L10n.ApplyTree(storageHost);
            });
        });
    }

    // ---------- 9. 数据（1:1 MacSettings.swift:1131-1244 & SnapshotSheets.swift） ----------

    private void BuildDataPage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        var counts = _library.GetSnapshotCounts();
        string snapDir = Path.Combine(_library.DataDir, "snapshots");
        Directory.CreateDirectory(snapDir);

        DynamicPageHost.Children.Add(MakeHeading("生成快照"));

        var estText = new TextBlock { FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        void RefreshEstimate()
        {
            long est = (_snapFiles ? (long)counts.FileBytes : 0) + 64 * 1024;
            estText.Text = _snapFiles ? $"含书文件 · 约 {FormatBytes(est)}" : $"不含书文件 · 约 {FormatBytes(est)}";
        }
        RefreshEstimate();

        var importExtBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "导入快照…" };
        importExtBtn.Click += async (_, _) =>
        {
            if (await SnapshotAndConflictDialogs.ShowSnapshotImportDialogAsync(this, RootGrid.XamlRoot, _library))
            {
                RenderDynamicPage("data");
                RenderInspector("data");
            }
        };

        var makeBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
            Content = "生成快照",
        };
        makeBtn.Click += async (_, _) =>
        {
            makeBtn.IsEnabled = false;
            importExtBtn.IsEnabled = false;
            makeBtn.Content = "正在打包快照…";
            estText.Text = "正在打包快照…";
            try
            {
                string filePath = Path.Combine(snapDir, $"语阅快照-{DateTime.Now:yyyyMMdd-HHmmss}.yuyue");
                var parts = new SnapshotParts(_snapProgress, _snapNotes, _snapWords, _snapSettings, _snapFiles);
                ulong bytes = await Task.Run(() => _library.MakeSnapshot(filePath, parts));
                RecordChange("备份快照", FormatBytes((long)bytes));
                RenderDynamicPage("data");
                RenderInspector("data");
            }
            catch (Exception ex)
            {
                makeBtn.IsEnabled = true;
                importExtBtn.IsEnabled = true;
                makeBtn.Content = "生成快照";
                RefreshEstimate();
                await ShowInfoDialogAsync("生成快照失败", ex.Message);
            }
        };

        // 1:1 settings-data/1.png: 导入快照… + 生成快照 side-by-side in the snapshot card footer
        var actionRow = new Grid { Padding = new Thickness(16, 11, 16, 11) };
        actionRow.Children.Add(estText);
        var btnPair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        btnPair.Children.Add(importExtBtn);
        btnPair.Children.Add(makeBtn);
        actionRow.Children.Add(btnPair);

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("进度和书签", $"读过 {counts.Read} 本", MakeSwitch(_snapProgress, v => { _snapProgress = v; RefreshEstimate(); }), compact: true),
            MakeRow("笔记", $"{counts.Notes} 条", MakeSwitch(_snapNotes, v => { _snapNotes = v; RefreshEstimate(); }), compact: true),
            MakeRow("生词", $"{counts.Words} 个词", MakeSwitch(_snapWords, v => { _snapWords = v; RefreshEstimate(); }), compact: true),
            MakeRow("设置和替换规则", null, MakeSwitch(_snapSettings, v => { _snapSettings = v; RefreshEstimate(); }), compact: true),
            MakeRow("书文件", FormatBytes((long)counts.FileBytes), MakeSwitch(_snapFiles, v => { _snapFiles = v; RefreshEstimate(); }), compact: true),
            actionRow));

        var csvBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "导出…" };
        csvBtn.Click += async (_, _) => await SnapshotAndConflictDialogs.ShowReadingExportSheetAsync(this, RootGrid.XamlRoot, _library);

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("导出阅读记录", "导出想读、在读、读完状态与完成日期（CSV），可导入 Goodreads 或 StoryGraph", csvBtn)));

        // 历史快照列表（1:1 MacSettings.swift:1322-1346：PeekSnapshot 元数据 + 无数量上限 + 导出… + 删除确认）
        var savedSnaps = Directory.GetFiles(snapDir, "*.yuyue")
            .Select(f =>
            {
                var fi = new FileInfo(f);
                SnapshotAbout? about = null;
                try { about = _library.Core.PeekSnapshot(fi.FullName); } catch { }
                long sortMs = about?.MadeAt ?? new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                return (File: fi, About: about, SortMs: sortMs);
            })
            .OrderByDescending(x => x.SortMs)
            .ToArray();

        if (savedSnaps.Length > 0)
        {
            DynamicPageHost.Children.Add(MakeHeading("历史快照"));
            var snapRows = new List<UIElement>();
            foreach (var item in savedSnaps)
            {
                var fi = item.File;
                var about = item.About;
                string fullPath = fi.FullName;

                DateTime madeLocal = about is not null && about.MadeAt > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(about.MadeAt).LocalDateTime
                    : fi.LastWriteTime;
                string whenTitle = madeLocal.Year == DateTime.Now.Year
                    ? $"{madeLocal.Month} 月 {madeLocal.Day} 日 {madeLocal:HH:mm}"
                    : $"{madeLocal.Year} 年 {madeLocal.Month} 月 {madeLocal.Day} 日 {madeLocal:HH:mm}";

                string rawDevice = !string.IsNullOrWhiteSpace(about?.DeviceName) ? about!.DeviceName : Environment.MachineName;
                string devLabel = string.Equals(rawDevice, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                    ? $"这台电脑（{rawDevice}）"
                    : rawDevice;
                long byteCount = about is not null && about.Bytes > 0 ? (long)about.Bytes : fi.Length;
                bool hasFiles = about?.Parts.Files == true;
                string subDetail = about is not null
                    ? $"{devLabel} · 进度 {about.Counts.Read} 本 · 笔记 {about.Counts.Notes} 条 · {FormatBytes(byteCount)}"
                    : $"{devLabel} · {FormatBytes(byteCount)}";

                async Task ExportSnapshotAsync()
                {
                    var picker = new FileSavePicker
                    {
                        SuggestedFileName = Path.GetFileNameWithoutExtension(fullPath),
                    };
                    picker.FileTypeChoices.Add("语阅数据快照", [".yuyue"]);
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                    var dest = await picker.PickSaveFileAsync();
                    if (dest is null) return;
                    await Task.Run(() => File.Copy(fullPath, dest.Path, overwrite: true));
                    ShowFlashBanner("✓ 已导出快照");
                }

                async Task ConfirmDeleteSnapshotAsync()
                {
                    var confirmDlg = new ContentDialog
                    {
                        XamlRoot = RootGrid.XamlRoot,
                        Title = "删除这份快照？",
                        Content = $"{whenTitle} 生成的快照文件会从本机删除，当前书库里的书籍和笔记不受影响。",
                        PrimaryButtonText = "删除",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Close,
                    };
                    if (await confirmDlg.ShowAsync() != ContentDialogResult.Primary) return;
                    try { File.Delete(fullPath); } catch { }
                    RenderDynamicPage("data");
                    RenderInspector("data");
                }

                var impBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "导入", Height = 26, MinHeight = 26 };
                impBtn.Click += async (_, _) =>
                {
                    if (await SnapshotAndConflictDialogs.ShowSnapshotImportDialogAsync(this, RootGrid.XamlRoot, _library, fullPath))
                    {
                        RenderDynamicPage("data");
                        RenderInspector("data");
                    }
                };
                var expBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "导出…", Height = 26, MinHeight = 26 };
                expBtn.Click += async (_, _) => await ExportSnapshotAsync();

                var rightActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                if (hasFiles)
                    rightActs.Children.Add(MakeStatusPill("含书文件", tint.Accent));
                rightActs.Children.Add(impBtn);
                rightActs.Children.Add(expBtn);

                var rowEl = MakeRow(whenTitle, subDetail, rightActs, compact: true);
                var ctx = new MenuFlyout();
                var expMi = new MenuFlyoutItem { Text = "导出…" };
                expMi.Click += async (_, _) => await ExportSnapshotAsync();
                var revMi = new MenuFlyoutItem { Text = "在资源管理器中显示" };
                revMi.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
                var delMi = new MenuFlyoutItem { Text = "删除…", Foreground = ThemeHelper.Brush(p.Danger) };
                delMi.Click += async (_, _) => await ConfirmDeleteSnapshotAsync();
                ctx.Items.Add(expMi);
                ctx.Items.Add(revMi);
                ctx.Items.Add(new MenuFlyoutSeparator());
                ctx.Items.Add(delMi);
                rowEl.ContextFlyout = ctx;
                snapRows.Add(rowEl);
            }
            DynamicPageHost.Children.Add(MakeCard(snapRows.ToArray()));
        }

        DynamicPageHost.Children.Add(MakeNote("快照为单个 .yuyue 文件，在 Mac、iPhone、Android 与 Windows 上打开即可恢复书库。"));
    }

    // ---------- 10. 关于（1:1 MacUpdate.swift & UpdateUI.swift：68x68 YuyueMark + 语阅 + PencilSignature + 更新渠道） ----------

    private void BuildAboutPage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());

        var bookplate = new Grid { ColumnSpacing = 36, Margin = new Thickness(6, 18, 0, 8), MinHeight = 470 };
        bookplate.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(204) });
        bookplate.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左页：书扉（68x68 YuyueMark「阅」+ 靛蓝胶囊短横 + 语阅 + 铅笔手写签名 + by nowtin · GPL-3.0）
        var leftGrid = new Grid();
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        leftGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var leftTop = new StackPanel { Spacing = 0 };
        var markInner = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        markInner.Children.Add(new TextBlock
        {
            Text = "阅",
            FontFamily = serif,
            FontSize = 31,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ThemeHelper.Brush(Color.FromArgb(255, 28, 29, 32)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -2, 0, 0),
        });
        markInner.Children.Add(new Border
        {
            Width = 22,
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = ThemeHelper.Brush(Color.FromArgb(255, 43, 76, 126)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var yuyueMark = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(15),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemeHelper.Brush(Colors.White),
            BorderBrush = ThemeHelper.Brush(Color.FromArgb(14, 0, 0, 0)),
            BorderThickness = new Thickness(0.5),
            Child = markInner,
        };
        ThemeHelper.ApplyElevation(yuyueMark, RootGrid, 14f);
        leftTop.Children.Add(yuyueMark);

        leftTop.Children.Add(new TextBlock
        {
            Text = "语阅",
            FontFamily = serif,
            FontSize = 28,
            CharacterSpacing = 140,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            Margin = new Thickness(0, 16, 0, 4),
        });
        leftTop.Children.Add(new TextBlock
        {
            Text = "Yuyue for Windows",
            FontFamily = new FontFamily(YYFontFamily.LatinSerif.WindowsFamily()),
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            FontSize = 13,
            Foreground = ThemeHelper.Brush(p.T3),
        });
        leftTop.Children.Add(new Border
        {
            Width = 24,
            Height = 1.5,
            Background = ThemeHelper.Brush(p.Line),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 18, 0, 16),
        });
        leftTop.Children.Add(new TextBlock
        {
            Text = "读着听，听着读，进度接得上。\n没有服务器，不收集数据，\n书与笔记都留在你手里。",
            FontFamily = serif,
            FontSize = 13.5,
            LineHeight = 23,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
        });
        leftGrid.Children.Add(leftTop);

        // 底部 PencilSignature（点击签名区或底部版权行一笔笔写出 CTT 铅笔签名，1:1 PhoneSignature.swift & MacUpdate.swift:214-239）
        var (sigView, triggerSigWrite) = YYChrome.MakePencilSignature(58, IsDark);
        var sigWrap = new StackPanel { Spacing = 4, Margin = new Thickness(0, 20, 0, 4), Background = new SolidColorBrush(Colors.Transparent) };
        sigWrap.PointerPressed += (_, _) => triggerSigWrite();
        sigWrap.Children.Add(sigView);
        var leftFoot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Background = new SolidColorBrush(Colors.Transparent) };
        leftFoot.Children.Add(new TextBlock { Text = "by", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        var authorText = new TextBlock { Text = "nowtin", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) };
        var authorBtn = new Button { Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(2, 0, 2, 0), MinWidth = 0, MinHeight = 0, Content = authorText, UseSystemFocusVisuals = false };
        authorBtn.PointerEntered += (_, _) => authorText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        authorBtn.PointerExited += (_, _) => authorText.TextDecorations = Windows.UI.Text.TextDecorations.None;
        authorBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri("https://github.com/nowtin"));
        leftFoot.Children.Add(authorBtn);
        leftFoot.Children.Add(new TextBlock { Text = "· GPL-3.0", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        leftFoot.PointerPressed += (_, e) => { triggerSigWrite(); e.Handled = true; };
        sigWrap.Children.Add(leftFoot);
        Grid.SetRow(sigWrap, 1);
        leftGrid.Children.Add(sigWrap);
        bookplate.Children.Add(leftGrid);

        // 右页：牌记与操作卡片
        var rightStack = new StackPanel { Spacing = 12, Margin = new Thickness(0, 2, 0, 0) };

        string FormatLastChecked()
        {
            if (_library.Prefs.UpdateLastCheckedMs is not long ms || ms <= 0) return "尚未检查更新";
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            string when = dt.Date == DateTime.Today ? $"今天 {dt:HH:mm}" : $"{dt:M 月 d 日 HH:mm}";
            return $"{when} 检查过";
        }

        bool checkedCurrentBuild = _library.Prefs.UpdateCheckedBuild == UpdateAndFeedbackService.CurrentBuild &&
            _library.Prefs.UpdateLastCheckedMs is > 0;
        string initialStatus = _library.Updates.LatestOffer is { } off
            ? $"发现新版本 v{off.Version}"
            : (checkedCurrentBuild ? $"已是最新 · {FormatLastChecked()}" : "尚未检查更新");

        var statusLine = new TextBlock
        {
            Text = initialStatus,
            FontSize = 12,
            Foreground = ThemeHelper.Brush(_library.Updates.LatestOffer is not null ? tint.Accent : (checkedCurrentBuild ? p.Ok : p.T3)),
        };
        var checkBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "检查更新" };
        checkBtn.Click += async (_, _) =>
        {
            checkBtn.IsEnabled = false;
            statusLine.Text = "正在检查更新…";
            try
            {
                await _library.Updates.ShowUpdateCheckDialogAsync(RootGrid.XamlRoot);
                RenderDynamicPage("about");
                MountIcons();
            }
            finally { checkBtn.IsEnabled = true; }
        };

        Release[] releases = _library.Updates.LoadBundledChangelog();
        int? currentEdition = UpdateAndFeedbackService.ResolveEdition(UpdateAndFeedbackService.CurrentVersion, releases);
        var verRow = new Grid { Padding = new Thickness(16, 14, 16, 14) };
        var verLeft = new StackPanel { Spacing = 3 };
        var verHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (currentEdition is int ed)
        {
            verHead.Children.Add(new TextBlock { Text = $"第 {ed} 版", FontFamily = serif, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            verHead.Children.Add(new TextBlock { Text = $"v{UpdateAndFeedbackService.CurrentVersion} ({UpdateAndFeedbackService.CurrentBuild})", FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()), FontSize = 12, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) });
        }
        else
        {
            verHead.Children.Add(new TextBlock { Text = $"v{UpdateAndFeedbackService.CurrentVersion} ({UpdateAndFeedbackService.CurrentBuild})", FontFamily = serif, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        }
        verLeft.Children.Add(verHead);
        verLeft.Children.Add(statusLine);
        verRow.Children.Add(verLeft);
        checkBtn.HorizontalAlignment = HorizontalAlignment.Right;
        checkBtn.VerticalAlignment = VerticalAlignment.Center;
        verRow.Children.Add(checkBtn);

        // Skipped / Remind-Later quiet update banner with 1-click reopen
        if (_library.Updates.LatestOffer is { } suppressed && _library.Updates.IsOfferSuppressed)
        {
            bool skipped = suppressed.Version == _library.Prefs.UpdateSkippedVersion;
            var bannerGrid = new Grid { Padding = new Thickness(14, 9, 14, 9), ColumnSpacing = 10 };
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            bannerGrid.Children.Add(new TextBlock
            {
                Text = skipped ? $"已跳过新版本 v{suppressed.Version}" : $"已推迟提醒新版本 v{suppressed.Version}",
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T2),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var reopenBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                Content = "重新查看更新",
                Height = 25,
                MinHeight = 25,
                FontSize = 11,
            };
            reopenBtn.Click += async (_, _) =>
            {
                _library.Prefs.UpdateSkippedVersion = "";
                _library.Prefs.UpdateRemindVersion = "";
                _library.Prefs.UpdateRemindUntilMs = 0;
                _library.SavePrefs();
                await _library.Updates.ShowUpdateCheckDialogAsync(RootGrid.XamlRoot);
                RenderDynamicPage("about");
                MountIcons();
            };
            Grid.SetColumn(reopenBtn, 1);
            bannerGrid.Children.Add(reopenBtn);

            rightStack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = ThemeHelper.Brush(p.Inset),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Child = bannerGrid,
            });
        }

        Button MakeAboutNavRow(string title, string value, Action onClick)
        {
            var g = new Grid { Padding = new Thickness(16, 10, 16, 10), MinHeight = 40 };
            g.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            var trail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            trail.Children.Add(new TextBlock { Text = value, FontSize = 12, Foreground = ThemeHelper.Brush(p.T3) });
            trail.Children.Add(YYIcons.Make("chevron-right", 10, ThemeHelper.Brush(p.T3)));
            g.Children.Add(trail);
            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = g,
            };
            b.Click += (_, _) => onClick();
            return b;
        }

        int firstCount = releases.Length > 0 ? Math.Max(1, (releases[0].NotesZh.Length > 0 ? releases[0].NotesZh : releases[0].NotesEn).Length) : 1;
        string logSummary = releases.Length > 0 ? $"{releases[0].Date.Replace('-', '.')} · {firstCount} 项" : "2026.11.23 · 1 项";

        // 更新渠道下拉（正式版 / 测试版，1:1 MacUpdate.swift:275-294）
        var chMenu = new MenuFlyout();
        foreach (var (ck, cl) in new[] { ("stable", "正式版"), ("beta", "测试版") })
        {
            var mi = new MenuFlyoutItem { Text = _library.Prefs.UpdateChannel == ck ? $"✓ {cl}" : cl };
            string key = ck;
            string lbl = cl;
            mi.Click += (_, _) =>
            {
                _library.Prefs.UpdateChannel = key;
                _library.SavePrefs();
                RecordChange("更新渠道", lbl);
                RenderDynamicPage("about");
            };
            chMenu.Items.Add(mi);
        }
        var chBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Content = (_library.Prefs.UpdateChannel == "beta" ? "测试版" : "正式版") + " ⇕",
            Flyout = chMenu,
        };

        rightStack.Children.Add(MakeCard(
            verRow,
            MakeAboutNavRow("更新日志", logSummary, async () => await _library.Updates.ShowChangelogSheetAsync(RootGrid.XamlRoot)),
            MakeRow("自动检查更新", "每天启动时检查一次，仅读取版本号，不上传本地数据", MakeSwitch(_library.Prefs.AutoCheckUpdates, v => { _library.Prefs.AutoCheckUpdates = v; _library.SavePrefs(); RecordChange("自动检查更新", v ? "开" : "关"); })),
            MakeRow("更新渠道", "测试版先拿到新功能；换回正式版后随下个正式版恢复", chBtn)));

        var feedbackRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var ghIssuesBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "GitHub ↗", FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) };
        ghIssuesBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri("https://github.com/inokoru/yuyue/issues"));
        var writeFbBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "写反馈…", Height = 26, MinHeight = 26 };
        writeFbBtn.Click += async (_, _) => await _library.Updates.ShowFeedbackComposerAsync(this, RootGrid.XamlRoot);
        feedbackRight.Children.Add(ghIssuesBtn);
        feedbackRight.Children.Add(writeFbBtn);

        var srcBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Content = "github.com/inokoru/yuyue ↗", FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1) };
        srcBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri("https://github.com/inokoru/yuyue"));

        rightStack.Children.Add(MakeCard(
            MakeRow("问题与建议", "可附截图或文件，无需注册账号", feedbackRight),
            MakeAboutNavRow("隐私说明", "仅存本机 · 无统计", async () => await UpdateAndFeedbackService.ShowPrivacySheetAsync(RootGrid.XamlRoot)),
            MakeAboutNavRow("开源许可与致谢", "GPL-3.0 与组件清单", async () => await UpdateAndFeedbackService.ShowLicensesSheetAsync(RootGrid.XamlRoot)),
            MakeRow("源代码", null, srcBtn, compact: true)));

        Grid.SetColumn(rightStack, 1);
        bookplate.Children.Add(rightStack);
        DynamicPageHost.Children.Add(bookplate);
    }

    private async Task ShowInfoDialogAsync(string title, string message)
    {
        var tb = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 21, FontSize = 12.5 };
        var d = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { MaxHeight = 360, Content = tb },
            CloseButtonText = "好",
            XamlRoot = RootGrid.XamlRoot,
        };
        await d.ShowAsync();
    }

    private static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024L => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };

    // ---------- Appearance Page Event Handlers ----------

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string page) NavigateTo(page);
    }

    private void ThemeMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string mode) return;
        string prev = _library.Prefs.ThemeMode;
        if (prev == mode) return;
        string LabelOf(string m) => m switch { "light" => "浅色", "dark" => "深色", _ => "跟随系统" };
        _library.Prefs.ThemeMode = mode;
        _library.SavePrefs();
        RecordChange(
            "界面外观",
            LabelOf(mode),
            undo: () => { _library.Prefs.ThemeMode = prev; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); },
            redo: () => { _library.Prefs.ThemeMode = mode; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); },
            oldValue: LabelOf(prev));
        App.CurrentApp.BroadcastAppearanceChanged();
    }

    private void TintDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tintId) return;
        string prev = _library.Prefs.TintId;
        if (prev == tintId) return;
        string NameOf(string id) => YYTints.All.FirstOrDefault(t => t.Id == id)?.Name ?? id;
        _library.Prefs.TintId = tintId;
        _library.SavePrefs();
        RecordChange(
            "主题色",
            NameOf(tintId),
            undo: () => { _library.Prefs.TintId = prev; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); },
            redo: () => { _library.Prefs.TintId = tintId; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); },
            oldValue: NameOf(prev));
        App.CurrentApp.BroadcastAppearanceChanged();
    }

    private void CoverSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;
        double prev = _library.Prefs.CoverWidth;
        double next = Math.Round(e.NewValue);
        if (Math.Abs(prev - next) < 0.5) return;
        _library.Prefs.CoverWidth = next;
        _library.SavePrefs();
        RecordChange(
            "封面大小",
            $"{(int)next}",
            undo: () => { _library.Prefs.CoverWidth = prev; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); },
            redo: () => { _library.Prefs.CoverWidth = next; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); },
            oldValue: $"{(int)prev}");
        App.CurrentApp.BroadcastAppearanceChanged();
    }

    private void ShowProgressToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool on = ShowProgressToggle.IsOn;
        _library.Prefs.ShowProgress = on;
        _library.SavePrefs();
        RecordChange(
            "封面下显示进度",
            on ? "开" : "关",
            undo: () => { _library.Prefs.ShowProgress = !on; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); },
            redo: () => { _library.Prefs.ShowProgress = on; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); },
            oldValue: on ? "关" : "开");
        App.CurrentApp.BroadcastAppearanceChanged();
    }
}
