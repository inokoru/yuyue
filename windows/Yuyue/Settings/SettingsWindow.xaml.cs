using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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
    // 朗读页状态 ("local" | "openai" | "azure" | "elevenlabs")
    private string _focusedVoiceSource = "neural";
    // 书伴页状态
    private string _focusedCompanionId;
    private bool _companionPickingModel;
    private readonly Dictionary<string, IReadOnlyList<string>> _cachedCompanionModels = new();
    // 笔记导出页状态 ("obsidian" | "notion" | "flomo" | "xinzhi")
    private string _focusedExportTarget = "obsidian";
    // 数据快照分项勾选
    private bool _snapProgress = true;
    private bool _snapNotes = true;
    private bool _snapWords = true;
    private bool _snapSettings = true;
    private bool _snapFiles = true;

    // 最近修改记录与 1.8s 自动消隐保存提示（对齐 MacSettingsHistory.swift）
    private readonly List<(DateTime Time, string Page, string Title, string Value, Action? Undo)> _history = [];
    private DispatcherTimer? _flashTimer;

    public SettingsWindow(LibraryService library, string initialPage = "general")
    {
        _library = library;
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

        _library.LiveVoice.StateChanged += OnLiveVoiceStateChanged;
        _library.Fonts.Changed += OnFontsChanged;
        Closed += (_, _) =>
        {
            _flashTimer?.Stop();
            _library.LiveVoice.StateChanged -= OnLiveVoiceStateChanged;
            _library.Fonts.Changed -= OnFontsChanged;
        };

        ApplyAppearance();
        NavigateTo(_activePage);
    }

    private void RecordChange(string title, string value, Action? undo = null)
    {
        _history.Insert(0, (DateTime.Now, _activePage, title, value, undo));
        if (_history.Count > 40) _history.RemoveAt(_history.Count - 1);

        string msg = $"✓ 已保存 · {title} {value}";
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

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.Z)
        {
            var undoEntry = _history.FirstOrDefault(h => h.Undo is not null);
            if (undoEntry.Undo is Action act)
            {
                _history.Remove(undoEntry);
                act();
                SavedFlashText.Text = $"↶ 已改回 · {undoEntry.Title}";
                CenterSavedFlashText.Text = SavedFlashText.Text;
                ApplyAppearance();
                NavigateTo(_activePage);
                e.Handled = true;
            }
        }
    }

    private void HistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        bool dark = IsDark;
        var p = CurrentPalette;
        var tint = CurrentTint;
        Flyout? flyoutRef = null;

        var root = new StackPanel { Width = 340, Spacing = 10 };
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = "最近改过", FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
        head.Children.Add(new TextBlock { Text = $"{_history.Count} 项记录", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
        root.Children.Add(head);

        if (_history.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "还没有修改记录。你在设置里的每一次改动都会记在这里，随时可以点「改回」或按 Ctrl+Z 撤销。",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
            });
        }
        else
        {
            var listStack = new StackPanel { Spacing = 4 };
            foreach (var h in _history.Take(10))
            {
                var entry = h;
                var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(8, 6, 8, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel { Spacing = 1 };
                left.Children.Add(new TextBlock { Text = $"{entry.Title} → {entry.Value}", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                left.Children.Add(new TextBlock { Text = $"{PageNameOf(entry.Page)} · {entry.Time:HH:mm}", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                row.Children.Add(left);

                if (entry.Undo is Action undoAct)
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
                        _history.Remove(entry);
                        undoAct();
                        flyoutRef?.Hide();
                        ApplyAppearance();
                        NavigateTo(_activePage);
                    };
                    Grid.SetColumn(uBtn, 1);
                    row.Children.Add(uBtn);
                }

                listStack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(p.Inset),
                    Child = row,
                });
            }
            root.Children.Add(new ScrollViewer { MaxHeight = 280, Content = listStack });

            var foot = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            foot.Children.Add(new TextBlock { Text = "按 Ctrl+Z 改回最近一项", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
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
            root.Children.Add(foot);
        }

        flyoutRef = new Flyout { Content = root };
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

    private static bool HasInspector(string page) =>
        page is "reading" or "foreign" or "listening" or "companion" or "export" or "storage" or "data";

    private void MountIcons()
    {
        bool dark = IsDark;
        var p = CurrentPalette;
        var accent = ThemeHelper.Brush(CurrentTint.Accent);
        var t2 = ThemeHelper.Brush(p.T2);
        Brush B(string id) => _activePage == id ? accent : t2;

        // 1:1 MacSettings.swift lines 250-264（阅读使用闭合书本图标 book）
        IconGeneral.Content = YYIcons.Make("sliders", 14, B("general"));
        IconAppearance.Content = YYIcons.Make("sun", 14.5, B("appearance"));
        IconReading.Content = YYIcons.Make("book", 14, B("reading"));
        IconForeign.Content = YYIcons.Make("char-book", 14, B("foreign"));
        IconListening.Content = YYIcons.Make("headphones", 14, B("listening"));
        IconCompanion.Content = YYIcons.Make("bubble-lines", 14, B("companion"));
        IconSync.Content = YYIcons.Make("sync-arrows", 14, B("sync"));
        IconExport.Content = YYIcons.Make("share-up", 14, B("export"));
        IconStorage.Content = YYIcons.Make("drive", 14, B("storage"));
        IconData.Content = YYIcons.Make("cube", 14, B("data"));
        IconAbout.Content = YYIcons.Make("info-circle", 14, B("about"));

        CornerHistoryBtn.Content = YYIcons.Make("clock-history", 14.5, t2);
        InspectorHistoryBtn.Content = YYIcons.Make("clock-history", 14, t2);
    }

    public void ApplyAppearance()
    {
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
    }

    public void NavigateTo(string page)
    {
        if (page == "speech") page = "listening";
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
        InspectorColumn.Width = inspector ? new GridLength(264) : new GridLength(0);
        InspectorBorder.Visibility = inspector ? Visibility.Visible : Visibility.Collapsed;
        PageHeaderGrid.Visibility = page == "about" ? Visibility.Collapsed : Visibility.Visible;
        // 1:1 MacSettings.swift:87-94：全部 11 页右上角均显示 SettingsCorner（含「关于」）
        CenterCornerStack.Visibility = inspector ? Visibility.Collapsed : Visibility.Visible;

        AppearancePage.Visibility = page == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        DynamicPageHost.Visibility = page == "appearance" ? Visibility.Collapsed : Visibility.Visible;

        if (page != "appearance") RenderDynamicPage(page);
        if (inspector) RenderInspector(page);
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
        var g = new Grid { Margin = new Thickness(14, 6, 14, -4) };
        g.Children.Add(new TextBlock { Text = text, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) });
        g.Children.Add(new TextBlock { Text = rightNote, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right });
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
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(16, 0, 16, 0) });
            stack.Children.Add(rows[i]);
        }
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
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
        var sw = new ToggleSwitch { IsOn = isOn, IsEnabled = enabled, MinWidth = 0, OnContent = "", OffContent = "" };
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
                Padding = new Thickness(8, 0, 8, 0),
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
                    ? rs.FontFace
                    : rs.Font switch { Font.Serif => "宋体", Font.Kai => "楷体", Font.WenKai => "霞鹜文楷", Font.Sans => "黑体", _ => "系统" };
                InspectorHost.Children.Add(InspectorHead("样张", $"{paperName} · {fontName} · {(int)Math.Round(rs.FontSize)}"));

                string fam = (rs.Font switch
                {
                    Font.Sans => YYFontFamily.Sans,
                    Font.Kai => YYFontFamily.Kaiti,
                    Font.WenKai => YYFontFamily.Wenkai,
                    _ => YYFontFamily.Serif,
                }).WindowsFamily();
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
                string lvlText = _library.Prefs.GlossLevel == 0
                    ? "不写释义"
                    : _library.Prefs.GlossVocabOnly
                        ? "只写生词本里的"
                        : _library.Prefs.GlossLevel switch { 2 => "四级及以上", 3 => "六级及以上", 4 => "考研及以上", 5 => "雅思及以上", _ => "GRE 及以上" };
                InspectorHost.Children.Add(InspectorHead("样张", lvlText));

                // 1:1 MacSettingsSide.swift:401-473：完整狄更斯《双城记》开篇 + 虚线生词下划线
                (string Word, string? Gloss, byte Level, bool InVocab)[] sampleWords =
                [
                    ("It was the best of times,", null, 0, false),
                    ("it was the worst of times,", null, 0, false),
                    ("it was the age of", null, 0, false),
                    ("wisdom,", "智慧", 2, false),
                    ("it was the age of", null, 0, false),
                    ("foolishness,", "愚蠢", 4, false),
                    ("it was the", null, 0, false),
                    ("epoch", "时代", 5, false),
                    ("of", null, 0, false),
                    ("belief,", "信仰", 3, false),
                    ("it was the epoch of", null, 0, false),
                    ("incredulity…", "怀疑", 6, true),
                ];

                var linesStack = new StackPanel { Spacing = 5 };
                var line1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                var line2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                var line3 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                var line4 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                var line5 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                var line6 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                for (int i = 0; i < sampleWords.Length; i++)
                {
                    var w = sampleWords[i];
                    bool showGloss = _library.Prefs.GlossLevel > 0
                        && w.Gloss is not null
                        && (_library.Prefs.GlossVocabOnly ? w.InVocab : w.Level >= _library.Prefs.GlossLevel);

                    var token = new StackPanel { Spacing = 0 };
                    token.Children.Add(new TextBlock
                    {
                        Text = showGloss ? w.Gloss! : " ",
                        FontSize = 9,
                        Foreground = ThemeHelper.Brush(ink2Col),
                    });
                    token.Children.Add(new TextBlock
                    {
                        Text = w.Word,
                        FontFamily = new FontFamily(YYFontFamily.LatinSerif.WindowsFamily()),
                        FontSize = 12.5,
                        Foreground = ThemeHelper.Brush(inkCol),
                    });
                    if (_library.Prefs.ForeignMarkVocab && w.InVocab)
                    {
                        // 虚线 [2,2] 下划线
                        var dashRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 1, 0, 0) };
                        for (int d = 0; d < 12; d++)
                            dashRow.Children.Add(new Border { Width = 3, Height = 1, Background = ThemeHelper.Brush(inkCol), Opacity = 0.62 });
                        token.Children.Add(dashRow);
                    }
                    (i == 0 ? line1 : i == 1 ? line2 : i < 4 ? line3 : i < 6 ? line4 : i < 10 ? line5 : line6).Children.Add(token);
                }
                linesStack.Children.Add(line1);
                linesStack.Children.Add(line2);
                linesStack.Children.Add(line3);
                linesStack.Children.Add(line4);
                linesStack.Children.Add(line5);
                linesStack.Children.Add(line6);

                InspectorHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(bgCol),
                    BorderBrush = ThemeHelper.Brush(isDarkPaper ? Colors.Transparent : p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(14),
                    Child = linesStack,
                });
                string cap = (_library.Prefs.GlossLevel == 0 ? "难词上方不写释义；" : "") + (_library.Prefs.ForeignMarkVocab ? "虚线是生词本里的词" : "生词不标");
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
                var openSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                openSp.Children.Add(YYIcons.Make("folder", 12, ThemeHelper.Brush(p.T1)));
                openSp.Children.Add(new TextBlock { Text = "在资源管理器中显示", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
                var openBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Margin = new Thickness(0, 4, 0, 0),
                    Content = openSp,
                };
                openBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_library.DataDir}\"") { UseShellExecute = true });
                cardStack.Children.Add(openBtn);

                InspectorHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12),
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

                var openSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                openSp.Children.Add(YYIcons.Make("folder", 12, ThemeHelper.Brush(p.T1)));
                openSp.Children.Add(new TextBlock { Text = "在资源管理器中显示", FontSize = 12 });
                var openBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Margin = new Thickness(0, 6, 0, 0), Content = openSp };
                openBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{snapDir}\"") { UseShellExecute = true });
                cardStack.Children.Add(openBtn);

                InspectorHost.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12),
                    Child = cardStack,
                });
                break;
            }
        }
    }

    private (string Name, Color Bg, Color Ink, bool Dark) ResolvePaperColors(ReaderSettings rs)
    {
        if (!string.IsNullOrEmpty(rs.CustomPaper))
        {
            try
            {
                var cp = _library.Core.CustomPapers().FirstOrDefault(x => x.Id == rs.CustomPaper);
                if (cp is not null)
                {
                    var pc = YuyueCoreMethods.PaperColors(cp);
                    return (cp.Name, CustomPaperDialog.ParseHexColor(pc.Bg, Colors.Beige), CustomPaperDialog.ParseHexColor(pc.Ink, Colors.Black), pc.Dark);
                }
            }
            catch { }
        }
        var tok = rs.Paper switch
        {
            Paper.Dark => YYPapers.Dark,
            Paper.Kami => YYPapers.Kami,
            Paper.KamiNight => YYPapers.Kaminight,
            Paper.Sage => YYPapers.Sage,
            Paper.Eink => YYPapers.Eink,
            _ => YYPapers.Plain,
        };
        string swiftName = rs.Paper switch
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
        (string seal, string langLabel, string subText) = curLang switch
        {
            "zh-Hans" => ("文", "中文", "当前：中文 · 切换需确认重开，并回到当前页"),
            "en" => ("Aa", "English", "当前：English · 切换需确认重开，并回到当前页"),
            _ => ("系", "跟随系统", "跟随系统（当前：中文）· 切换需确认重开，并回到当前页"),
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
                    Title = $"切换为「{name}」？",
                    Content = _library.SystemIntegration.IsAudioPlaying
                        ? "正在朗读音频，将在下次启动时生效。"
                        : "确认后将立即重开应用并回到当前设置页。",
                    PrimaryButtonText = _library.SystemIntegration.IsAudioPlaying ? "好" : "切换并重开",
                    SecondaryButtonText = _library.SystemIntegration.IsAudioPlaying ? "" : "稍后重开",
                    CloseButtonText = "取消",
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
                    RecordChange("语言", name, () => { _library.Prefs.UiLanguage = old; _library.SavePrefs(); _library.ApplyUiLanguage(); });
                    if (res == ContentDialogResult.Primary && !_library.SystemIntegration.IsAudioPlaying)
                    {
                        SystemIntegrationService.RelaunchApplication("--settings general");
                        return;
                    }
                    RenderDynamicPage("general");
                }
            };
            langMenu.Items.Add(mi);
        }

        // 180x32 暖纸印章下拉按钮（1:1 MacLanguageCard）
        var triggerGrid = new Grid { Width = 166, ColumnSpacing = 8 };
        triggerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        triggerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        triggerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        triggerGrid.Children.Add(new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(5),
            Background = ThemeHelper.Brush(tint.Soft),
            Child = new TextBlock { Text = seal, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(tint.Accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var langTb = new TextBlock { Text = langLabel, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(langTb, 1);
        triggerGrid.Children.Add(langTb);
        var chev = YYIcons.Make("updown", 10, ThemeHelper.Brush(p.T3));
        Grid.SetColumn(chev, 2);
        triggerGrid.Children.Add(chev);

        var langBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(7, 0, 9, 0),
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            UseSystemFocusVisuals = false,
            Content = triggerGrid,
            Flyout = langMenu,
        };

        var reopenSw = MakeSwitch(_library.Prefs.ReopenLast, on =>
        {
            _library.Prefs.ReopenLast = on;
            _library.SavePrefs();
            RecordChange("启动时继续上次阅读", on ? "开" : "关", () => { _library.Prefs.ReopenLast = !on; _library.SavePrefs(); });
        });

        var loginSw = MakeSwitch(_library.Prefs.AutoLaunchOnLogin, on =>
        {
            _library.Prefs.AutoLaunchOnLogin = on;
            _library.SavePrefs();
            SystemIntegrationService.SyncLoginStartup(on);
            _library.SystemIntegration.EnsureTrayVisible();
            RecordChange("登录时后台启动", on ? "开" : "关", () =>
            {
                _library.Prefs.AutoLaunchOnLogin = !on;
                _library.SavePrefs();
                SystemIntegrationService.SyncLoginStartup(!on);
                _library.SystemIntegration.EnsureTrayVisible();
            });
        });

        var awakeSw = MakeSwitch(_library.Prefs.KeepAwakeWhileListening, on =>
        {
            _library.Prefs.KeepAwakeWhileListening = on;
            _library.SavePrefs();
            _library.SystemIntegration.ApplyPowerStayAwake();
            RecordChange("朗读时防止休眠", on ? "开" : "关", () =>
            {
                _library.Prefs.KeepAwakeWhileListening = !on;
                _library.SavePrefs();
                _library.SystemIntegration.ApplyPowerStayAwake();
            });
        });

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("语言", subText, langBtn),
            MakeRow("启动时继续上次阅读", "关闭则进入书架", reopenSw),
            MakeRow("登录时后台启动", "常驻托盘，不弹出窗口，按时生成播客", loginSw),
            MakeRow("朗读时防止系统休眠", "合上屏幕仍会暂停朗读", awakeSw)));

        // 1:1 MacSettings.swift:631-636：导入仅单行说明
        DynamicPageHost.Children.Add(MakeHeading("导入"));
        var fmtBadge = new TextBlock
        {
            Text = "EPUB PDF MOBI AZW3 TXT",
            FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
        };
        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("拖入书架即可导入", "文件会复制到书库，移动或删除原文件不受影响", fmtBadge)));
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
            rs = edit(rs);
            _library.SaveReaderSettings(rs);
            App.CurrentApp.BroadcastReaderSettingsChanged();
            RecordChange(label, valText, () => { _library.SaveReaderSettings(prev); App.CurrentApp.BroadcastReaderSettingsChanged(); });
            RenderInspector("reading");
        }

        // 1. 纸张磁贴网格（YYPaper.all 顺序 + 自定义纸 + 新建，内绘两道墨水短线 26x2 & 18x2）
        var paperWrap = new StackPanel { Spacing = 10, Padding = new Thickness(16, 14, 16, 14) };
        paperWrap.Children.Add(new TextBlock { Text = "纸张", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });

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
        int cols = 7;
        int rows = (totalTiles + cols - 1) / cols;
        var paperGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (int c = 0; c < cols; c++) paperGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < rows; r++) paperGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Border MakePaperStrip(Color bg, Color ink, Color border, bool active)
        {
            var lines = new StackPanel { Spacing = 3.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(new Border { Width = 26, Height = 2, CornerRadius = new CornerRadius(1), Background = ThemeHelper.Brush(ink), Opacity = 0.72 });
            lines.Children.Add(new Border { Width = 18, Height = 2, CornerRadius = new CornerRadius(1), Background = ThemeHelper.Brush(ink), Opacity = 0.45, HorizontalAlignment = HorizontalAlignment.Left });
            return new Border
            {
                Height = 32,
                CornerRadius = new CornerRadius(9),
                Background = ThemeHelper.Brush(bg),
                BorderBrush = ThemeHelper.Brush(active ? tint.Accent : border),
                BorderThickness = new Thickness(active ? 2 : 1),
                Child = lines,
            };
        }

        int cellIdx = 0;
        foreach (var (kind, name, tok) in presets)
        {
            bool on = string.IsNullOrEmpty(rs.CustomPaper) && rs.Paper == kind;
            var tileSp = new StackPanel { Spacing = 4 };
            tileSp.Children.Add(MakePaperStrip(tok.Bg, tok.Ink, tok.Line, on));
            tileSp.Children.Add(new TextBlock { Text = name, FontSize = 10.5, FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2), HorizontalAlignment = HorizontalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });

            var btn = new Button { Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), UseSystemFocusVisuals = false, Content = tileSp };
            Paper pk = kind;
            string pName = name;
            btn.Click += (_, _) => { Update("纸张", pName, s => s with { Paper = pk, CustomPaper = null }); RenderDynamicPage("reading"); };
            Grid.SetRow(btn, cellIdx / cols);
            Grid.SetColumn(btn, cellIdx % cols);
            paperGrid.Children.Add(btn);
            cellIdx++;
        }

        foreach (var cp in customList)
        {
            var cpItem = cp;
            var pc = YuyueCoreMethods.PaperColors(cpItem);
            bool on = rs.CustomPaper == cpItem.Id;
            var tileSp = new StackPanel { Spacing = 4 };
            tileSp.Children.Add(MakePaperStrip(CustomPaperDialog.ParseHexColor(pc.Bg, Colors.Beige), CustomPaperDialog.ParseHexColor(pc.Ink, Colors.Black), p.Line, on));
            tileSp.Children.Add(new TextBlock { Text = cpItem.Name, FontSize = 10.5, FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2), HorizontalAlignment = HorizontalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });

            var ctx = new MenuFlyout();
            var editMi = new MenuFlyoutItem { Text = "调整颜色…" };
            editMi.Click += async (_, _) =>
            {
                var edited = await CustomPaperDialog.ShowEditorAsync(RootGrid.XamlRoot, _library, cpItem);
                if (edited is not null)
                {
                    var epc = YuyueCoreMethods.PaperColors(edited);
                    Update("纸张", edited.Name, s => s with { Paper = epc.Preset, CustomPaper = edited.Id });
                }
                RenderDynamicPage("reading");
            };
            var dupMi = new MenuFlyoutItem { Text = "复制一张" };
            dupMi.Click += (_, _) =>
            {
                var copy = CustomPaperDialog.DuplicateCustomPaper(_library, cpItem);
                if (copy is not null)
                {
                    var cpc = YuyueCoreMethods.PaperColors(copy);
                    Update("纸张", copy.Name, s => s with { Paper = cpc.Preset, CustomPaper = copy.Id });
                }
                RenderDynamicPage("reading");
            };
            var delMi = new MenuFlyoutItem { Text = "删除…", Foreground = ThemeHelper.Brush(p.Danger) };
            delMi.Click += (_, _) =>
            {
                try { _library.Core.DeleteCustomPaper(cpItem.Id); } catch { }
                if (rs.CustomPaper == cpItem.Id)
                    Update("纸张", "纸本", s => s with { Paper = Paper.Kami, CustomPaper = null });
                RenderDynamicPage("reading");
            };
            ctx.Items.Add(editMi);
            ctx.Items.Add(dupMi);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(delMi);

            var btn = new Button { Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), UseSystemFocusVisuals = false, Content = tileSp, ContextFlyout = ctx };
            btn.Click += (_, _) => { Update("纸张", cpItem.Name, s => s with { Paper = pc.Preset, CustomPaper = cpItem.Id }); RenderDynamicPage("reading"); };
            Grid.SetRow(btn, cellIdx / cols);
            Grid.SetColumn(btn, cellIdx % cols);
            paperGrid.Children.Add(btn);
            cellIdx++;
        }

        // + 新建自定义纸张磁贴（1:1 SettingsNewPaperTile 标签为「新建」）
        var addPaperSp = new StackPanel { Spacing = 4 };
        addPaperSp.Children.Add(new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(p.Inset),
            Child = YYIcons.Make("plus", 12.5, ThemeHelper.Brush(p.T2)),
        });
        addPaperSp.Children.Add(new TextBlock { Text = "新建", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Center });
        var addPaperBtn = new Button { Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), UseSystemFocusVisuals = false, Content = addPaperSp };
        addPaperBtn.Click += async (_, _) =>
        {
            var created = await CustomPaperDialog.ShowEditorAsync(RootGrid.XamlRoot, _library);
            if (created is not null)
            {
                var pc = YuyueCoreMethods.PaperColors(created);
                Update("纸张", created.Name, s => s with { Paper = pc.Preset, CustomPaper = created.Id });
                RenderDynamicPage("reading");
            }
        };
        Grid.SetRow(addPaperBtn, cellIdx / cols);
        Grid.SetColumn(addPaperBtn, cellIdx % cols);
        paperGrid.Children.Add(addPaperBtn);
        paperWrap.Children.Add(paperGrid);

        // 2. 字体「永」字磁贴网格 + 「更多字体」字样册 (FontBook)
        var fontWrap = new StackPanel { Spacing = 10, Padding = new Thickness(16, 14, 16, 14) };
        fontWrap.Children.Add(new TextBlock { Text = "字体", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });

        var fontGrid = new Grid { ColumnSpacing = 8 };
        for (int i = 0; i < 5; i++) fontGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        (string Id, string Label, Font? Kind, string Fam)[] faces =
        [
            ("system", "系统", null, YYFontFamily.Sans.WindowsFamily()),
            ("serif", "宋体", Font.Serif, YYFontFamily.Serif.WindowsFamily()),
            ("kai", "楷体", Font.Kai, YYFontFamily.Kaiti.WindowsFamily()),
            ("wenkai", _library.Fonts.IsDownloading("wenkai") ? "下载中…" : "霞鹜文楷", Font.WenKai, YYFontFamily.Wenkai.WindowsFamily()),
        ];

        for (int i = 0; i < faces.Length; i++)
        {
            var fc = faces[i];
            bool on = string.IsNullOrEmpty(rs.FontFace) && rs.Font == fc.Kind;
            var tile = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tile.Children.Add(new TextBlock { Text = "永", FontFamily = new FontFamily(fc.Fam), FontSize = 17, Foreground = ThemeHelper.Brush(p.T1), HorizontalAlignment = HorizontalAlignment.Center });
            tile.Children.Add(new TextBlock { Text = fc.Label, FontSize = 10, Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T2), HorizontalAlignment = HorizontalAlignment.Center });

            var fb = new Button
            {
                Height = 52,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(on ? p.Card : p.Inset),
                BorderBrush = ThemeHelper.Brush(on ? tint.Accent : p.Line),
                BorderThickness = new Thickness(on ? 1.5 : 0.5),
                UseSystemFocusVisuals = false,
                Content = tile,
            };
            var fCapture = fc;
            fb.Click += async (_, _) =>
            {
                if (fCapture.Id == "wenkai" && !_library.Fonts.IsPackInstalled("wenkai"))
                {
                    try { await _library.Fonts.DownloadPackAsync("wenkai"); }
                    catch (Exception ex) { await ShowInfoDialogAsync("字体下载失败", ex.Message); return; }
                }
                Update("字体", fCapture.Label, s => s with { Font = fCapture.Kind, FontFace = null });
                RenderDynamicPage("reading");
            };
            Grid.SetColumn(fb, i);
            fontGrid.Children.Add(fb);
        }

        // 第 5 格：更多字体（打开字样册 FontBook 弹窗）
        bool extraFontOn = !string.IsNullOrEmpty(rs.FontFace) || rs.Font == Font.Sans;
        var moreSp = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        moreSp.Children.Add(new TextBlock { Text = "永永", FontFamily = new FontFamily("SimSun"), FontSize = 14, Foreground = ThemeHelper.Brush(p.T2), HorizontalAlignment = HorizontalAlignment.Center });
        moreSp.Children.Add(new TextBlock { Text = "更多字体", FontSize = 10, Foreground = ThemeHelper.Brush(extraFontOn ? tint.Accent : p.T2), HorizontalAlignment = HorizontalAlignment.Center });

        var moreFontBtn = new Button
        {
            Height = 52,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(extraFontOn ? p.Card : p.Inset),
            BorderBrush = ThemeHelper.Brush(extraFontOn ? tint.Accent : p.Line),
            BorderThickness = new Thickness(extraFontOn ? 1.5 : 0.5),
            UseSystemFocusVisuals = false,
            Content = moreSp,
        };
        moreFontBtn.Click += async (_, _) => await ShowSettingsFontBookAsync(Update);
        Grid.SetColumn(moreFontBtn, 4);
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

        // 4. 翻页、双栏、对齐、缩进、标点挤压
        var modeSeg = MakeSegments(
            [(LayoutMode.Paginated, "左右翻页"), (LayoutMode.Scrolled, "上下滚动")],
            rs.Mode,
            m => { Update("翻页方式", m == LayoutMode.Scrolled ? "上下滚动" : "左右翻页", s => s with { Mode = m }); RenderDynamicPage("reading"); },
            width: 172);

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("翻页方式", null, modeSeg),
            MakeRow("宽窗口下双栏显示", "左右并排显示两页", MakeSwitch(_library.Prefs.TwoColumns, v => { _library.Prefs.TwoColumns = v; _library.SavePrefs(); App.CurrentApp.BroadcastReaderSettingsChanged(); RecordChange("双栏显示", v ? "开" : "关"); })),
            MakeRow("两端对齐", null, MakeSwitch(rs.Justify ?? true, v => Update("两端对齐", v ? "开" : "关", s => s with { Justify = v }))),
            MakeRow("首行缩进两字", null, MakeSwitch(rs.Indent ?? true, v => Update("首行缩进", v ? "开" : "关", s => s with { Indent = v }))),
            MakeRow("中文标点挤压", "收紧连续标点的留白", MakeSwitch(rs.Compress, v => Update("标点挤压", v ? "开" : "关", s => s with { Compress = v })))));

        // 5. 沉浸式阅读（补齐「隐藏划线与想法」「隐藏书伴提示与难词注释」，1:1 MacSettings.swift:1008-1023）
        DynamicPageHost.Children.Add(MakeHeading("沉浸式阅读"));
        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("进入与退出", "全屏隐藏界面，专注书页正文", new TextBlock { Text = "F11 或 Esc", FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) }),
            MakeRow("打开书籍时自动进入", null, MakeSwitch(_library.Prefs.ImmersiveAutoEnter, v => { _library.Prefs.ImmersiveAutoEnter = v; _library.SavePrefs(); RecordChange("自动沉浸阅读", v ? "开" : "关"); })),
            MakeRow("底部保留进度细线", null, MakeSwitch(_library.Prefs.ImmersiveKeepLine, v => { _library.Prefs.ImmersiveKeepLine = v; _library.SavePrefs(); RecordChange("沉浸进度细线", v ? "开" : "关"); })),
            MakeRow("隐藏划线与想法", "退出沉浸后恢复显示", MakeSwitch(_library.Prefs.ImmersiveHideMarks, v => { _library.Prefs.ImmersiveHideMarks = v; _library.SavePrefs(); RecordChange("隐藏划线与想法", v ? "开" : "关"); })),
            MakeRow("隐藏书伴提示与难词注释", null, MakeSwitch(_library.Prefs.ImmersiveHideHints, v => { _library.Prefs.ImmersiveHideHints = v; _library.SavePrefs(); RecordChange("隐藏提示与注释", v ? "开" : "关"); })),
            MakeRow("关闭翻页动画", "直接切换整页，无滑动过渡", MakeSwitch(_library.Prefs.ImmersiveNoAnimation, v => { _library.Prefs.ImmersiveNoAnimation = v; _library.SavePrefs(); RecordChange("关闭翻页动画", v ? "开" : "关"); }))));
    }

    private async Task ShowSettingsFontBookAsync(Action<string, string, Func<ReaderSettings, ReaderSettings>> updateFn)
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        var rs = _library.GetReaderSettings();
        ContentDialog? dlgRef = null;

        var list = new StackPanel { Spacing = 6, Width = 480 };
        foreach (var pack in _library.Fonts.Catalog)
        {
            var pk = pack;
            bool inst = _library.Fonts.IsPackInstalled(pk.Id);
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(9),
                Background = ThemeHelper.Brush(p.Inset),
                Child = new TextBlock { Text = "永", FontFamily = new FontFamily("SimSun"), FontSize = 18, Foreground = ThemeHelper.Brush(p.T1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var m = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            m.Children.Add(new TextBlock { Text = pk.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
            m.Children.Add(new TextBlock { Text = inst ? "已安装在本机" : "开源正文字体 · 点击自动下载并应用", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
            Grid.SetColumn(m, 1);
            row.Children.Add(m);

            var useBtn = new Button { Style = (Style)Application.Current.Resources[inst ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"], Content = inst ? "选用" : "下载并选用", Height = 26, MinHeight = 26 };
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
            Grid.SetColumn(useBtn, 2);
            row.Children.Add(useBtn);

            list.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = ThemeHelper.Brush(p.Card), BorderBrush = ThemeHelper.Brush(p.Line), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 12, 8), Child = row });
        }

        // 黑体与仿宋本机选项 + 导入本地字体
        foreach (var (name, desc, kind, face) in new (string, string, Font?, string?)[]
        {
            ("黑体（无衬线）", "Windows 现代清晰屏幕无衬线字体", Font.Sans, null),
            ("仿宋", "古典清朗刻本风味字体", Font.Serif, "FangSong, serif"),
        })
        {
            var useBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "选用", Height = 26, MinHeight = 26 };
            string fName = name; Font? fKind = kind; string? fFace = face;
            useBtn.Click += (_, _) =>
            {
                updateFn("字体", fName, s => s with { Font = fKind, FontFace = fFace });
                dlgRef?.Hide();
                RenderDynamicPage("reading");
            };
            list.Children.Add(MakeCard(MakeRow(name, desc, useBtn, compact: true)));
        }

        var importLocalBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "+ 导入本地字体文件 (.ttf / .otf)…" };
        importLocalBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
            picker.FileTypeFilter.Add(".ttf");
            picker.FileTypeFilter.Add(".otf");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var f = await picker.PickSingleFileAsync();
            if (f is null) return;
            try
            {
                string fn = _library.Fonts.ImportFontFile(f.Path);
                updateFn("字体", Path.GetFileNameWithoutExtension(fn), s => s with { FontFace = $"file:{fn}" });
                dlgRef?.Hide();
                RenderDynamicPage("reading");
            }
            catch (Exception ex) { dlgRef?.Hide(); await ShowInfoDialogAsync("导入字体失败", ex.Message); }
        };
        list.Children.Add(importLocalBtn);

        dlgRef = new ContentDialog
        {
            Title = "字样册",
            Content = new ScrollViewer { MaxHeight = 440, Content = list },
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
            MakeRow("查词时自动发音", null, MakeSwitch(_library.Prefs.ForeignSpeakOnLookup, v => { _library.Prefs.ForeignSpeakOnLookup = v; _library.SavePrefs(); RecordChange("自动发音", v ? "开" : "关"); })),
            MakeRow("英文发音", null, accentSeg)));

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
        var pickMdxBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "选择文件…" };
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
        bool glossOn = _library.Prefs.GlossLevel > 0;
        var glossSw = MakeSwitch(glossOn, on =>
        {
            _library.Prefs.GlossLevel = on ? (byte)3 : (byte)0;
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
                _library.Prefs.GlossLevel = lv;
                _library.SavePrefs();
                App.CurrentApp.BroadcastForeignPrefsChanged();
                RenderDynamicPage("foreign");
                RenderInspector("foreign");
            },
            width: 248);
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
            MakeRow("仅标注生词本中的词", null, vocabOnlySw)));

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

        DynamicPageHost.Children.Add(MakeHeading("声音来源", "点一个，在右栏试听或连接"));

        var srcStack = new StackPanel { Spacing = 2, Padding = new Thickness(4) };
        var curVoice = AudioPlayer.GetAllVoiceOptions(_library).FirstOrDefault(o => o.Id == _library.Prefs.VoiceId || o.Id == "sys:" + _library.Prefs.VoiceId);

        (string Key, string Badge, string Title, string? Tag, bool TagWarn, string Sub, string Status)[] sources =
        [
            ("neural", "语", "语阅离线语音", "推荐", false,
                lv.IsReady ? $"跨端离线自然人声 · 内置 {lv.GetBuiltinCards().Count} 个中英音色与声音克隆" : "一键下载（160 MB）· 本地 ONNX 离线自然人声，断网畅听",
                lv.IsReady ? "已就绪" : "一键下载"),
            ("free", "免", "在线自然语音", "免 Key", false,
                "0 MB 免下载、免注册免 Key，已读句子缓存在本机",
                _library.CloudVoices.IsConfigured(CloudTtsProvider.Free) ? "已开启" : "已隐藏"),
            ("volcengine", "豆", "火山引擎（豆包）", null, false,
                "豆包大模型语音，中文播客与小说自然度高",
                _library.CloudVoices.IsConfigured(CloudTtsProvider.Volcengine) ? "已连接" : "未连接"),
            ("fish", "FA", "Fish Audio", null, false,
                "OpenAudio S1 高表现力中英音色与社区声音库",
                _library.CloudVoices.IsConfigured(CloudTtsProvider.Fish) ? "已连接" : "未连接"),
            ("openai", "OA", "OpenAI TTS", null, false,
                "10 个音色，支持中英双语",
                !string.IsNullOrEmpty(_library.Secrets.Get("tts.openai.key")) ? "已连接" : "未连接"),
            ("azure", "AZ", "Microsoft Azure", null, false,
                "中文音色丰富，含免费额度",
                !string.IsNullOrEmpty(_library.Secrets.Get("tts.azure.key")) ? "已连接" : "未连接"),
            ("elevenlabs", "11", "ElevenLabs", null, false,
                "自然度高，适合英文朗读",
                !string.IsNullOrEmpty(_library.Secrets.Get("tts.elevenlabs.key")) ? "已连接" : "未连接"),
            ("system", "PC", "这台电脑 · 系统语音", "不推荐", true,
                "Windows 内置合成音机械感明显，仅建议在未下载离线语音且断网时兜底",
                curVoice is { IsNeural: false } ? $"📌 {curVoice.Label}" : "仅作兜底"),
        ];

        for (int i = 0; i < sources.Length; i++)
        {
            if (i > 0) srcStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
            var s = sources[i];
            bool sel = _focusedVoiceSource == s.Key;

            var g = new Grid { ColumnSpacing = 12, MinHeight = 48, Padding = new Thickness(12, 8, 12, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(MakeServiceLogoTile(s.Key, 30, s.Badge));

            var mid = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            titleRow.Children.Add(new TextBlock
            {
                Text = s.Title,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = ThemeHelper.Brush(s.TagWarn ? p.T2 : p.T1),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (!string.IsNullOrEmpty(s.Tag))
            {
                Color pillColor = s.TagWarn ? Color.FromArgb(255, 217, 119, 6) : p.Ok;
                titleRow.Children.Add(MakeStatusPill(s.Tag, pillColor));
            }
            mid.Children.Add(titleRow);
            mid.Children.Add(new TextBlock { Text = s.Sub, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3) });
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            bool dimStatus = s.Status is "未连接" or "已隐藏" or "仅作兜底";
            var stTb = new TextBlock { Text = s.Status, FontSize = 11, Foreground = ThemeHelper.Brush(dimStatus ? p.T3 : tint.Accent), MaxWidth = 140, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(stTb, 2);
            g.Children.Add(stTb);

            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(0),
                Background = sel ? ThemeHelper.Brush(Color.FromArgb(16, p.T1.R, p.T1.G, p.T1.B)) : new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = g,
            };
            string sk = s.Key;
            btn.Click += (_, _) =>
            {
                _focusedVoiceSource = sk;
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            srcStack.Children.Add(btn);
        }

        DynamicPageHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Child = srcStack,
        });
        DynamicPageHost.Children.Add(MakeNote("推荐下载「语阅离线语音」断网畅听，或直接使用免 Key 的「在线自然语音」。Windows 系统自带合成音机械感较重（不推荐），仅作断网兜底。"));

        // 广播剧工坊（1:1 MacVoicePicker.swift:72-84）
        DynamicPageHost.Children.Add(MakeHeading("广播剧工坊", "只在这台电脑上跑，不传云端"));
        var dramaRows = new List<UIElement>();
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
                try { await AudioPlayer.PreviewVoiceAsync(_library, "card:" + cc.Id, _library.Prefs.SpeechRate, "春天的风轻轻吹过窗台，翻开书页的声音格外安静。"); }
                finally { tryBtn.IsEnabled = true; }
            };
            var delClBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "删除", Height = 25, MinHeight = 25, FontSize = 11, Foreground = ThemeHelper.Brush(p.Danger) };
            delClBtn.Click += (_, _) =>
            {
                try { _library.Core.CardDelete(null, cc.Id); } catch { }
                App.CurrentApp.BroadcastVoiceChanged();
                RenderDynamicPage("listening");
                RenderInspector("listening");
            };
            rowActs.Children.Add(tryBtn);
            rowActs.Children.Add(delClBtn);
            cloneRows.Add(MakeRow($"✦ {cc.Name}", "本机克隆声音卡", rowActs, compact: true));
        }
        var newCloneBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "🎙 录一段声音…" };
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

        // 语速（慢 — Slider — 快 + 1.0x 菜单）
        DynamicPageHost.Children.Add(MakeHeading("语速", "读书窗口里调的也是这一个"));
        var speedRow = new Grid { ColumnSpacing = 10, Padding = new Thickness(16, 10, 16, 10) };
        speedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        speedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        speedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        speedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        speedRow.Children.Add(new TextBlock { Text = "慢", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        var rateSlider = new Slider { Minimum = 0.8, Maximum = 2.0, StepFrequency = 0.1, Value = Math.Clamp(_library.Prefs.SpeechRate, 0.8, 2.0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(rateSlider, 1);
        speedRow.Children.Add(rateSlider);

        var fastTb = new TextBlock { Text = "快", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(fastTb, 2);
        speedRow.Children.Add(fastTb);

        var rateBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = $"{_library.Prefs.SpeechRate:0.1}× ⌄", Height = 26, MinHeight = 26 };
        var rateMenu = new MenuFlyout();
        foreach (double rv in new[] { 0.8, 1.0, 1.2, 1.5, 2.0 })
        {
            double v = rv;
            var mi = new MenuFlyoutItem { Text = $"{v:0.1}×" };
            mi.Click += (_, _) => { rateSlider.Value = v; };
            rateMenu.Items.Add(mi);
        }
        rateBtn.Flyout = rateMenu;
        Grid.SetColumn(rateBtn, 3);
        speedRow.Children.Add(rateBtn);

        rateSlider.ValueChanged += (_, e) =>
        {
            double nv = Math.Round(e.NewValue, 1);
            rateBtn.Content = $"{nv:0.1}× ⌄";
            _library.Prefs.SpeechRate = nv;
            _library.SavePrefs();
            App.CurrentApp.BroadcastVoiceChanged();
        };

        DynamicPageHost.Children.Add(MakeCard(speedRow));
    }

    private void BuildListeningInspector(YYPalette p, YYTintColors tint)
    {
        if (_focusedVoiceSource is "neural" or "system" or "local")
        {
            bool isSys = _focusedVoiceSource == "system";
            var options = AudioPlayer.GetAllVoiceOptions(_library)
                .Where(o => isSys ? o.Id.StartsWith("sys:", StringComparison.Ordinal) : o.Id.StartsWith("card:", StringComparison.Ordinal))
                .ToList();
            if (options.Count == 0)
                options = AudioPlayer.GetAllVoiceOptions(_library).ToList();

            InspectorHost.Children.Add(new TextBlock
            {
                Text = isSys ? "这台电脑 · 系统语音（不推荐）" : "语阅离线语音（推荐）",
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
            });

            if (isSys)
            {
                InspectorHost.Children.Add(MakeNote("不推荐日常听书使用：系统内置合成音机械感较重，建议改用「语阅离线语音」或「在线自然语音（免 Key）」。"));
            }
            else if (!_library.LiveVoice.IsReady)
            {
                var dlBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = _library.LiveVoice.State == LiveVoiceStateKind.Downloading ? _library.LiveVoice.StatusMessage : "⬇ 一键下载语阅离线语音模型",
                };
                dlBtn.Click += (_, _) =>
                {
                    if (_library.LiveVoice.State != LiveVoiceStateKind.Downloading)
                        _ = _library.LiveVoice.DownloadAsync();
                };
                InspectorHost.Children.Add(dlBtn);
            }

            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            int selIdx = 0;
            for (int i = 0; i < options.Count; i++)
            {
                combo.Items.Add(new ComboBoxItem { Content = options[i].Label, Tag = options[i].Id });
                if (options[i].Id == _library.Prefs.VoiceId || options[i].Id == "sys:" + _library.Prefs.VoiceId) selIdx = i;
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = selIdx;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem ci && ci.Tag is string vid)
                {
                    _library.Prefs.VoiceId = vid;
                    _library.SavePrefs();
                    RecordChange("默认声音", ci.Content?.ToString() ?? "");
                    App.CurrentApp.BroadcastVoiceChanged();
                    RenderDynamicPage("listening");
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

            // 波形试听条
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
                playCircle.IsEnabled = false;
                try
                {
                    string previewId = (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? _library.Prefs.VoiceId;
                    string line = string.IsNullOrWhiteSpace(sampleBox.Text) ? DailyQuote.Current(_library).Text : sampleBox.Text;
                    await AudioPlayer.PreviewVoiceAsync(_library, previewId, _library.Prefs.SpeechRate, line);
                }
                finally { playCircle.IsEnabled = true; }
            };
            playRow.Children.Add(playCircle);

            var waveRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2.5, VerticalAlignment = VerticalAlignment.Center };
            int[] heights = [6, 12, 9, 16, 11, 18, 8, 14, 19, 10, 15, 7, 13, 17, 9, 12, 8, 15, 11, 7];
            foreach (int h in heights)
                waveRow.Children.Add(new Border { Width = 3, Height = h, CornerRadius = new CornerRadius(1.5), Background = ThemeHelper.Brush(tint.Accent), Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(waveRow, 1);
            playRow.Children.Add(waveRow);

            var cardStack = new StackPanel { Spacing = 10 };
            cardStack.Children.Add(combo);
            cardStack.Children.Add(playRow);
            cardStack.Children.Add(quoteHeader);
            cardStack.Children.Add(sampleBox);

            InspectorHost.Children.Add(new Border { CornerRadius = new CornerRadius(11), Background = ThemeHelper.Brush(p.Card), BorderBrush = ThemeHelper.Brush(p.Line), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = cardStack });
            InspectorHost.Children.Add(MakeCard(
                MakeRow("来自", null, new TextBlock { Text = isSys ? "Windows 系统自带（不推荐）" : "语阅离线引擎（推荐）", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2) }, compact: true),
                MakeRow("费用", null, new TextBlock { Text = isSys ? "免费 · 仅兜底" : "免费 · 不联网", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.Ok) }, compact: true)));
            return;
        }

        // 云端语音面板 (Free / Volcengine / Fish / OpenAI / Azure / ElevenLabs)
        CloudTtsProvider cloudProv = _focusedVoiceSource switch
        {
            "free" => CloudTtsProvider.Free,
            "volcengine" => CloudTtsProvider.Volcengine,
            "fish" => CloudTtsProvider.Fish,
            "azure" => CloudTtsProvider.Azure,
            "elevenlabs" => CloudTtsProvider.ElevenLabs,
            _ => CloudTtsProvider.OpenAi,
        };
        string title = _focusedVoiceSource switch
        {
            "free" => "在线自然语音（免 Key）",
            "volcengine" => "火山引擎（豆包）",
            "fish" => "Fish Audio",
            "azure" => "Microsoft Azure",
            "elevenlabs" => "ElevenLabs",
            _ => "OpenAI TTS",
        };
        string secretKey = $"tts.{_focusedVoiceSource}.key";
        InspectorHost.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });

        var stack = new StackPanel { Spacing = 10 };
        if (cloudProv == CloudTtsProvider.Free)
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
            if (cloudProv == CloudTtsProvider.Volcengine)
            {
                var appBox = new TextBox
                {
                    Header = "火山引擎 AppID",
                    Text = _library.Secrets.Get("tts.volcengine.appid"),
                    PlaceholderText = "填写应用 AppID…",
                };
                appBox.TextChanged += (_, _) =>
                {
                    _library.Secrets.Set("tts.volcengine.appid", appBox.Text.Trim());
                    RenderDynamicPage("listening");
                };
                stack.Children.Add(appBox);
            }

            var kb = new PasswordBox
            {
                Header = cloudProv == CloudTtsProvider.Volcengine ? "Access Token（本机加密存储）" : "API Key（本机加密存储）",
                Password = _library.Secrets.Get(secretKey),
                PlaceholderText = "粘贴密钥…",
            };
            kb.PasswordChanged += (_, _) =>
            {
                _library.Secrets.Set(secretKey, kb.Password.Trim());
                RenderDynamicPage("listening");
            };
            stack.Children.Add(kb);
        }

        if (_focusedVoiceSource == "azure")
        {
            var regCombo = new ComboBox { Header = "服务区域 (Region)", HorizontalAlignment = HorizontalAlignment.Stretch };
            var regions = new[] { ("eastasia", "东亚 (eastasia)"), ("southeastasia", "东南亚 (southeastasia)"), ("westus", "美西 (westus)"), ("japaneast", "日本东 (japaneast)") };
            int rIdx = 0;
            for (int i = 0; i < regions.Length; i++)
            {
                regCombo.Items.Add(new ComboBoxItem { Content = regions[i].Item2, Tag = regions[i].Item1 });
                if (regions[i].Item1 == _library.Prefs.CloudVoiceAzureRegion) rIdx = i;
            }
            regCombo.SelectedIndex = rIdx;
            regCombo.SelectionChanged += (_, _) =>
            {
                if (regCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string r)
                {
                    _library.Prefs.CloudVoiceAzureRegion = r;
                    _library.SavePrefs();
                }
            };
            stack.Children.Add(regCombo);
        }

        var checkStatus = new TextBlock { FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap };
        var refreshBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = "验证连接并刷新音色列表",
        };
        refreshBtn.Click += async (_, _) =>
        {
            refreshBtn.IsEnabled = false;
            checkStatus.Text = "正在连接服务商并拉取音色…";
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
            var vCombo = new ComboBox { Header = $"选用音色（共 {voices.Count} 个）", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
            int selV = 0;
            for (int i = 0; i < voices.Count; i++)
            {
                vCombo.Items.Add(new ComboBoxItem { Content = voices[i].DisplayName, Tag = voices[i].FullId });
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

            var auditionCloudBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "▶ 试听所选云端音色" };
            auditionCloudBtn.Click += async (_, _) =>
            {
                string fid = (vCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? voices[0].FullId;
                auditionCloudBtn.IsEnabled = false;
                try { await AudioPlayer.PreviewVoiceAsync(_library, fid, _library.Prefs.SpeechRate, DailyQuote.Current(_library).Text); }
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
        else if (!string.IsNullOrEmpty(_library.Secrets.Get(secretKey)))
        {
            var discBtn = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Foreground = ThemeHelper.Brush(p.Danger), Content = $"断开 {title}" };
            discBtn.Click += (_, _) =>
            {
                _library.Secrets.Set(secretKey, "");
                if (cloudProv == CloudTtsProvider.Volcengine) _library.Secrets.Set("tts.volcengine.appid", "");
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

    private Border MakeServiceLogoTile(string id, double size = 30, string? customMark = null)
    {
        string mark = customMark ?? id switch
        {
            "openrouter" => "OR",
            "claude" => "CL",
            "openai" => "OA",
            "gemini" => "G",
            "deepseek" => "DS",
            "dashscope" => "千",
            "moonshot" => "K",
            "zhipu" => "智",
            "volcengine" => "豆",
            "siliconflow" => "SF",
            "minimax" => "MM",
            "xai" => "X",
            "mistral" => "M",
            "groq" => "GQ",
            "magpie" => "MP",
            "ollama" => "OL",
            "lmstudio" => "LM",
            _ => ShortProviderTitle(id, "AI")[..1].ToUpperInvariant(),
        };
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.25),
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = ThemeHelper.Brush(Color.FromArgb(26, 0, 0, 0)),
            BorderThickness = new Thickness(0.5),
            Child = new TextBlock
            {
                Text = mark,
                FontSize = size * 0.38,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = ThemeHelper.Brush(Color.FromArgb(255, 42, 43, 46)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

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
    }

    private async Task ShowAddProviderSheetAsync()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        ContentDialog? dlgRef = null;

        var searchBox = new TextBox
        {
            PlaceholderText = "搜索服务商，比如 千问、Kimi、Claude、magpie…",
            FontSize = 12,
        };
        var groupsHost = new StackPanel { Spacing = 10 };

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

                var sec = new StackPanel { Spacing = 4 };
                sec.Children.Add(new TextBlock
                {
                    Text = grpTitle,
                    FontSize = 10.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(6, 0, 0, 0),
                });

                var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
                for (int c = 0; c < 3; c++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int rows = (list.Length + 2) / 3;
                for (int r = 0; r < rows; r++)
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (int i = 0; i < list.Length; i++)
                {
                    var pr = list[i];
                    bool already = _library.Prefs.ConfiguredAiProviders.Contains(pr.Id);
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.Children.Add(MakeServiceLogoTile(pr.Id, 20));

                    var lbl = new TextBlock
                    {
                        Text = already ? $"{pr.Name} ✓" : pr.Name,
                        FontSize = 11.5,
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
                        Padding = new Thickness(8, 6, 8, 6),
                        CornerRadius = new CornerRadius(8),
                        Background = ThemeHelper.Brush(p.Card),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(1),
                        UseSystemFocusVisuals = false,
                        Content = row,
                    };
                    if (!string.IsNullOrEmpty(pr.Blurb))
                        ToolTipService.SetToolTip(btn, pr.Blurb);

                    string pid = pr.Id;
                    btn.Click += (_, _) =>
                    {
                        if (!_library.Prefs.ConfiguredAiProviders.Contains(pid))
                            _library.Prefs.ConfiguredAiProviders.Add(pid);
                        _focusedCompanionId = pid;
                        _library.SavePrefs();
                        dlgRef?.Hide();
                        RenderDynamicPage("companion");
                        RenderInspector("companion");
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
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 24, 0, 24),
                });
            }
        }

        searchBox.TextChanged += (_, _) => RebuildGroups(searchBox.Text);
        RebuildGroups("");

        var customBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(p.Inset),
            BorderThickness = new Thickness(0),
            Content = "+ 自己添加  ·  任何 OpenAI 兼容接口",
            FontSize = 11.5,
        };
        customBtn.Click += (_, _) =>
        {
            string cid = "custom." + Guid.NewGuid().ToString("N")[..8];
            _library.Prefs.AiCustomProviderNames[cid] = "自定义服务";
            _library.Prefs.ConfiguredAiProviders.Add(cid);
            _focusedCompanionId = cid;
            _library.SavePrefs();
            dlgRef?.Hide();
            RenderDynamicPage("companion");
            RenderInspector("companion");
        };

        var outer = new StackPanel
        {
            Spacing = 10,
            Width = 430,
            Children =
            {
                searchBox,
                new ScrollViewer { MaxHeight = 320, Content = groupsHost },
                new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) },
                customBtn,
            },
        };

        dlgRef = new ContentDialog
        {
            Title = "添加书伴服务商",
            Content = outer,
            CloseButtonText = "关闭",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dlgRef.ShowAsync();
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

            var filterBox = new TextBox { PlaceholderText = "筛选模型 ID…", FontSize = 11.5 };
            var mStack = new StackPanel { Spacing = 4 };

            void PopulateModels(string filter)
            {
                mStack.Children.Clear();
                var merged = new List<string>();
                foreach (var r in recommended)
                    if (!merged.Contains(r, StringComparer.OrdinalIgnoreCase)) merged.Add(r);
                if (_cachedCompanionModels.TryGetValue(preset.Id, out var remoteList))
                {
                    foreach (var rm in remoteList)
                        if (!merged.Contains(rm, StringComparer.OrdinalIgnoreCase)) merged.Add(rm);
                }

                string fq = filter.Trim();
                var shown = string.IsNullOrEmpty(fq)
                    ? merged.Take(40).ToArray()
                    : merged.Where(x => x.Contains(fq, StringComparison.OrdinalIgnoreCase)).Take(50).ToArray();

                foreach (var mid in shown)
                {
                    bool on = string.Equals(curModel, mid, StringComparison.OrdinalIgnoreCase);
                    var mb = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Padding = new Thickness(10, 7, 10, 7),
                        CornerRadius = new CornerRadius(9),
                        BorderThickness = new Thickness(1),
                        Background = ThemeHelper.Brush(on ? tint.Soft : p.Card),
                        BorderBrush = ThemeHelper.Brush(on ? tint.Edge : p.Line),
                        Foreground = ThemeHelper.Brush(on ? tint.Accent : p.T1),
                        FontSize = 11.5,
                        Content = on ? $"✓ {mid}" : mid,
                    };
                    string nextM = mid;
                    mb.Click += (_, _) =>
                    {
                        _library.Prefs.AiProviderModels[preset.Id] = nextM;
                        if (inUse) ActivateCompanionProvider(preset.Id);
                        else _library.SavePrefs();
                        RecordChange("书伴模型", nextM);
                        _companionPickingModel = false;
                        RenderDynamicPage("companion");
                        RenderInspector("companion");
                    };
                    mStack.Children.Add(mb);
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
            InspectorHost.Children.Add(new ScrollViewer { MaxHeight = 250, Content = mStack });

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
        hMeta.Children.Add(new TextBlock { Text = inUse ? "当前使用" : "已添加 · 未设为当前", FontSize = 11, Foreground = ThemeHelper.Brush(inUse ? p.Ok : p.T3) });
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

        var useSw = MakeSwitch(inUse, on =>
        {
            if (!on) return;
            ActivateCompanionProvider(preset.Id);
            RecordChange("书伴服务", ShortProviderTitle(preset.Id, preset.Name));
            RenderDynamicPage("companion");
            RenderInspector("companion");
        });
        useSw.IsEnabled = !inUse;

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
        credStack.Children.Add(urlBox);
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
            ActivateCompanionProvider(preset.Id);
            checkBtn.IsEnabled = false;
            testResult.Text = "正在验证连接…";
            var sw = Stopwatch.StartNew();
            var (ok, msg) = await _library.Companion.TestConnectionAsync();
            sw.Stop();
            testResult.Text = ok ? $"✓ 正常 · {sw.Elapsed.TotalSeconds:0.1} 秒" : msg;
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
            rmBtn.Click += (_, _) =>
            {
                _library.Prefs.ConfiguredAiProviders.Remove(preset.Id);
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

        var statusOuter = new StackPanel { Spacing = 14, Padding = new Thickness(16) };
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
            Text = isOn ? _library.Sync.StatusSummary() : "在下方选择一种方式连接后，阅读进度、划线与生词自动在多设备间汇合",
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
                dGrid.Children.Add(YYIcons.Make("laptop", 14, ThemeHelper.Brush(p.T2)));

                var dText = new StackPanel { Spacing = 1 };
                dText.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(d.Name) ? Environment.MachineName : d.Name, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                string platLabel = d.Platform.ToLowerInvariant() switch { "ios" => "iPhone / iPad", "android" => "Android", "macos" => "Mac", _ => "Windows" };
                dText.Children.Add(new TextBlock { Text = d.ThisDevice ? "刚刚" : platLabel, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                Grid.SetColumn(dText, 1);
                dGrid.Children.Add(dText);

                if (d.ThisDevice)
                {
                    var thisTb = new TextBlock { Text = "这台", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(thisTb, 2);
                    dGrid.Children.Add(thisTb);
                }

                var pill = new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(10, 8, 10, 8), Child = dGrid };
                Grid.SetRow(pill, i / 3);
                Grid.SetColumn(pill, i % 3);
                devGrid.Children.Add(pill);
            }
            statusOuter.Children.Add(devGrid);
        }
        DynamicPageHost.Children.Add(MakeCard(statusOuter));

        // 2. 同步方式（激活卡片支持右键「停止同步」，1:1 MacSyncSettings.swift:257-259）
        DynamicPageHost.Children.Add(MakeHeading("同步方式"));
        var waysGrid = new Grid { ColumnSpacing = 12 };
        for (int c = 0; c < 3; c++) waysGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        UIElement BuildMethodTile(int col, string methodKey, string icon, string title, string detail, bool disabled = false)
        {
            bool on = !disabled && _library.Prefs.SyncMethod == methodKey;
            var stack = new StackPanel { Spacing = 2 };
            var iconLine = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            iconLine.Children.Add(new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = ThemeHelper.Brush(on ? tint.Soft : p.Inset),
                Child = YYIcons.Make(icon, 13.5, ThemeHelper.Brush(on ? tint.Accent : p.T1)),
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
                Padding = new Thickness(14, 12, 14, 12),
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
                    App.CurrentApp.NotifyShelfRefresh();
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
        else if (_library.Prefs.SyncMethod == "folder" && !string.IsNullOrEmpty(_library.Prefs.SyncLocalFolder))
        {
            var openFolderBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "在资源管理器中查看" };
            openFolderBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_library.Prefs.SyncLocalFolder}\"") { UseShellExecute = true });
            DynamicPageHost.Children.Add(MakeCard(MakeRow("同步文件夹", _library.Prefs.SyncLocalFolder, openFolderBtn, compact: true)));
        }

        // 3. 同步内容（未开启同步前也可预先调整各项开关，1:1 MacSyncSettings.swift:64-73）
        DynamicPageHost.Children.Add(MakeHeading("同步内容"));
        long booksBytes = DirSize(Path.Combine(_library.DataDir, "books"));
        string placeName = _library.Prefs.SyncMethod == "webdav" ? "网盘" : "个人云盘";

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("进度和书签", null, MakeSwitch(_library.Prefs.SyncReading, v => { _library.Prefs.SyncReading = v; _library.SavePrefs(); RecordChange("同步进度和书签", v ? "开" : "关"); }), compact: true),
            MakeRow("笔记和生词", null, MakeSwitch(_library.Prefs.SyncNotes, v => { _library.Prefs.SyncNotes = v; _library.SavePrefs(); RecordChange("同步笔记和生词", v ? "开" : "关"); }), compact: true),
            MakeRow("设置和替换规则", null, MakeSwitch(_library.Prefs.SyncSettings, v => { _library.Prefs.SyncSettings = v; _library.SavePrefs(); RecordChange("同步设置", v ? "开" : "关"); }), compact: true),
            MakeRow("书文件", $"{FormatBytes(booksBytes)} · 占用你的{placeName}空间；播客单集体积很小，始终同步", MakeSwitch(_library.Prefs.SyncFiles, v => { _library.Prefs.SyncFiles = v; _library.SavePrefs(); RecordChange("同步书文件", v ? "开" : "关"); }), compact: true),
            MakeRow("有声书录音", "同步完整音频文件；关闭时仅同步句级对齐信息，其他设备导入同名音频即可直接跟读", MakeSwitch(_library.Prefs.SyncRecordings, v => { _library.Prefs.SyncRecordings = v; _library.SavePrefs(); RecordChange("同步录音", v ? "开" : "关"); }), compact: true)));

        var lockFoot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(14, 2, 0, 0) };
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

            g.Children.Add(new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(7.5),
                Background = ThemeHelper.Brush(Color.FromArgb(28, badgeCol.R, badgeCol.G, badgeCol.B)),
                BorderBrush = ThemeHelper.Brush(Color.FromArgb(55, badgeCol.R, badgeCol.G, badgeCol.B)),
                BorderThickness = new Thickness(0.5),
                Child = new TextBlock { Text = badge, FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(badgeCol), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });

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

        bool obsConn = !string.IsNullOrEmpty(_library.Prefs.ObsidianFolder);
        bool notConn = !string.IsNullOrEmpty(_library.Secrets.Get("export.notion.token"));
        bool floConn = !string.IsNullOrEmpty(_library.Prefs.FlomoWebhookUrl);
        bool xinConn = !string.IsNullOrEmpty(_library.Secrets.Get("export.xinzhi.token"));

        AddTargetRow(0, "obsidian", "Ob", Color.FromArgb(255, 124, 58, 237), "Obsidian",
            obsConn ? _library.Prefs.ObsidianFolder : "未连接",
            obsConn && _library.Prefs.ObsidianExportOn,
            v => { _library.Prefs.ObsidianExportOn = v; _library.SavePrefs(); });

        AddTargetRow(1, "notion", "N", Color.FromArgb(255, 42, 43, 46), "Notion",
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
        var statusNote = new TextBlock
        {
            Text = _library.Prefs.ExportLastSummary.TryGetValue(_focusedExportTarget, out var lastSum) ? lastSum : "",
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
        };

        switch (_focusedExportTarget)
        {
            case "notion":
            {
                bool conn = !string.IsNullOrEmpty(_library.Secrets.Get("export.notion.token")) && !string.IsNullOrEmpty(_library.Prefs.NotionPageId);
                setupStack.Children.Add(new TextBlock { Text = conn ? $"Notion · 已连至「{_library.Prefs.NotionPageTitle}」" : "连接 Notion", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                var tkBox = new PasswordBox { Header = "Integration Token", Password = _library.Secrets.Get("export.notion.token"), PlaceholderText = "ntn_ 或 secret_..." };
                var pgBox = new TextBox { Header = "目标父页面链接或 32 位 ID", Text = !string.IsNullOrEmpty(_library.Prefs.NotionPageId) ? _library.Prefs.NotionPageId : _library.Prefs.NotionPageTitle, PlaceholderText = "粘贴 Notion 页面链接…" };
                var verifyBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = conn ? "重新验证连接" : "验证并连接 Notion" };
                verifyBtn.Click += async (_, _) =>
                {
                    verifyBtn.IsEnabled = false;
                    statusNote.Text = "正在校验 Notion Token 与父页面权限…";
                    var (ok, pageTitle, msg) = await _library.NoteExports.VerifyNotionAsync(tkBox.Password, pgBox.Text);
                    statusNote.Text = msg;
                    statusNote.Foreground = ThemeHelper.Brush(ok ? p.Ok : p.Danger);
                    verifyBtn.IsEnabled = true;
                    if (ok)
                    {
                        _library.Secrets.Set("export.notion.token", tkBox.Password.Trim());
                        _library.Prefs.NotionPageId = NoteExportService.ExtractNotionPageId(pgBox.Text) ?? "";
                        _library.Prefs.NotionPageTitle = pageTitle;
                        _library.Prefs.NotionExportOn = true;
                        _library.SavePrefs();
                        RenderDynamicPage("export");
                    }
                };
                setupStack.Children.Add(tkBox);
                setupStack.Children.Add(pgBox);
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
                            statusNote.Text = mCnt > 0 ? $"✓ 已同步 {mCnt} 条笔记（{bCnt} 本书）" : "✓ 所有划线均已同步过，无新增";
                            statusNote.Foreground = ThemeHelper.Brush(p.Ok);
                        }
                        catch (Exception ex) { statusNote.Text = ex.Message; statusNote.Foreground = ThemeHelper.Brush(p.Danger); }
                        finally { sendBtn.IsEnabled = true; }
                    };
                    var discBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = ThemeHelper.Brush(p.Danger), Content = "断开 Notion" };
                    discBtn.Click += (_, _) => { _library.NoteExports.DisconnectTarget("notion"); RenderDynamicPage("export"); RenderInspector("export"); };
                    setupStack.Children.Add(sendBtn);
                    setupStack.Children.Add(discBtn);
                }
                break;
            }
            case "flomo":
            {
                string curUrl = _library.Secrets.Get("export.flomo.url");
                if (string.IsNullOrEmpty(curUrl)) curUrl = _library.Prefs.FlomoWebhookUrl;
                bool conn = NoteExportService.ValidateFlomoWebhook(curUrl);
                setupStack.Children.Add(new TextBlock { Text = conn ? "flomo · 已连接" : "连接 flomo", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                var wbBox = new TextBox { Header = "专属 API Webhook 地址", Text = curUrl, PlaceholderText = "https://flomoapp.com/iwh/..." };
                var saveWbBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "保存 Webhook" };
                saveWbBtn.Click += (_, _) =>
                {
                    string u = wbBox.Text.Trim();
                    if (!NoteExportService.ValidateFlomoWebhook(u))
                    {
                        statusNote.Text = "请输入以 https://flomoapp.com/iwh/ 开头的专属 API 地址";
                        statusNote.Foreground = ThemeHelper.Brush(p.Danger);
                        return;
                    }
                    _library.Secrets.Set("export.flomo.url", u);
                    _library.Prefs.FlomoWebhookUrl = u;
                    _library.Prefs.FlomoExportOn = true;
                    _library.SavePrefs();
                    RenderDynamicPage("export");
                    RenderInspector("export");
                };
                setupStack.Children.Add(wbBox);
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
                            statusNote.Text = mCnt > 0 ? $"✓ 已发送 {mCnt} 条卡片到 flomo" : "✓ 没有未发过的新划线";
                            statusNote.Foreground = ThemeHelper.Brush(p.Ok);
                        }
                        catch (Exception ex) { statusNote.Text = ex.Message; statusNote.Foreground = ThemeHelper.Brush(p.Danger); }
                        finally { sendBtn.IsEnabled = true; }
                    };
                    var discBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = ThemeHelper.Brush(p.Danger), Content = "断开 flomo" };
                    discBtn.Click += (_, _) => { _library.NoteExports.DisconnectTarget("flomo"); RenderDynamicPage("export"); RenderInspector("export"); };
                    setupStack.Children.Add(sendBtn);
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
                    statusNote.Text = "正在验证新枝账号…";
                    var (ok, uName, msg) = await _library.NoteExports.VerifyXinzhiAsync(xzBox.Password);
                    statusNote.Text = msg;
                    statusNote.Foreground = ThemeHelper.Brush(ok ? p.Ok : p.Danger);
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
                            statusNote.Text = mCnt > 0 ? $"✓ 已发送 {mCnt} 条笔记到新枝" : "✓ 没有未发过的新划线";
                            statusNote.Foreground = ThemeHelper.Brush(p.Ok);
                        }
                        catch (Exception ex) { statusNote.Text = ex.Message; statusNote.Foreground = ThemeHelper.Brush(p.Danger); }
                        finally { sendBtn.IsEnabled = true; }
                    };
                    var discBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = ThemeHelper.Brush(p.Danger), Content = "断开新枝" };
                    discBtn.Click += (_, _) => { _library.NoteExports.DisconnectTarget("xinzhi"); RenderDynamicPage("export"); RenderInspector("export"); };
                    setupStack.Children.Add(sendBtn);
                    setupStack.Children.Add(discBtn);
                }
                break;
            }
            default:
            {
                bool conn = !string.IsNullOrEmpty(_library.Prefs.ObsidianFolder);
                setupStack.Children.Add(new TextBlock { Text = conn ? "Obsidian · 已连上" : "连接 Obsidian", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
                setupStack.Children.Add(new TextBlock { Text = conn ? _library.Prefs.ObsidianFolder : "选择 Obsidian 库里的一个文件夹，每本书自动生成带 YAML 头与 ^yy 块引用的 Markdown。", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap });
                var pickBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = conn ? "更换文件夹…" : "选择笔记库目录…" };
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
                if (conn)
                {
                    var sendBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = "立即生成全部书籍 Markdown" };
                    sendBtn.Click += async (_, _) =>
                    {
                        try
                        {
                            var (bCnt, mCnt) = await _library.NoteExports.ExportToObsidianAsync();
                            statusNote.Text = $"✓ 已写入 {bCnt} 本书共 {mCnt} 条笔记";
                            statusNote.Foreground = ThemeHelper.Brush(p.Ok);
                        }
                        catch (Exception ex) { statusNote.Text = ex.Message; statusNote.Foreground = ThemeHelper.Brush(p.Danger); }
                    };
                    var disc = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Foreground = ThemeHelper.Brush(p.Danger), Content = "断开 Obsidian" };
                    disc.Click += (_, _) => { _library.NoteExports.DisconnectTarget("obsidian"); RenderDynamicPage("export"); RenderInspector("export"); };
                    setupStack.Children.Add(sendBtn);
                    setupStack.Children.Add(disc);
                }
                break;
            }
        }
        if (!string.IsNullOrEmpty(statusNote.Text) || _focusedExportTarget is "notion" or "flomo" or "xinzhi" or "obsidian")
            setupStack.Children.Add(statusNote);
        InspectorHost.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = ThemeHelper.Brush(p.Card), BorderBrush = ThemeHelper.Brush(p.Line), BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = setupStack });
        InspectorHost.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 2, 0, 2) });

        // 下半区：4 目标专属 ExportSample 预览（1:1 MacNoteExportSettings.swift:700-809）
        string targetName = _focusedExportTarget switch { "notion" => "Notion", "flomo" => "flomo", "xinzhi" => "新枝", _ => "Obsidian" };
        InspectorHost.Children.Add(new TextBlock { Text = $"发到 {targetName} 是这样", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T2) });

        var sample = new StackPanel { Spacing = 7 };
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
                var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                foreach (var tg in new[] { "#语阅", "#鲁迅" })
                    tags.Children.Add(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1.5, 6, 1.5), Background = ThemeHelper.Brush(Color.FromArgb(26, 124, 58, 237)), Child = new TextBlock { Text = tg, FontSize = 10, Foreground = ThemeHelper.Brush(Color.FromArgb(255, 124, 58, 237)) } });
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

        var activeIds = _library.GetBooks().Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        long booksBytes = 0;
        long deletedBytes = 0;
        string booksRoot = Path.Combine(_library.DataDir, "books");
        if (Directory.Exists(booksRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(booksRoot))
            {
                string id = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(id) || id.StartsWith('.')) continue;
                long sz = DirSize(dir);
                if (activeIds.Contains(id)) booksBytes += sz;
                else deletedBytes += sz;
            }
        }
        long audioBytes = DirSize(Path.Combine(_library.DataDir, "audiobooks"));
        long voiceBytes = DirSize(Path.Combine(_library.DataDir, "models"));
        long totalBytes = DirSize(_library.DataDir);
        long otherBytes = Math.Max(0, totalBytes - booksBytes - audioBytes - voiceBytes - deletedBytes);

        long freeBytes = 0;
        try { freeBytes = new DriveInfo(Path.GetPathRoot(_library.DataDir) ?? "C:\\").AvailableFreeSpace; } catch { }

        var barWrap = new StackPanel { Spacing = 10, Padding = new Thickness(16) };
        var headLine = new Grid();
        var usedLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        usedLine.Children.Add(new TextBlock { Text = "已用空间", FontSize = 13, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) });
        usedLine.Children.Add(new TextBlock { Text = FormatBytes(totalBytes), FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
        headLine.Children.Add(usedLine);
        if (freeBytes > 0)
            headLine.Children.Add(new TextBlock { Text = $"本机可用 {FormatBytes(freeBytes)}", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
        barWrap.Children.Add(headLine);

        // 5 段彩条：书文件 / 有声书录音 / 语音模型 / 已删除 / 其他
        (string Label, long Bytes, Color Col)[] parts =
        [
            ("书文件", booksBytes, tint.Fill),
            ("有声书录音", audioBytes, Color.FromArgb(205, tint.Fill.R, tint.Fill.G, tint.Fill.B)),
            ("语音模型", voiceBytes, Color.FromArgb(145, tint.Fill.R, tint.Fill.G, tint.Fill.B)),
            ("已删除", deletedBytes, p.Danger),
            ("其他", otherBytes, p.T3),
        ];

        var barGrid = new Grid { Height = 8, ColumnSpacing = 2 };
        long sum = Math.Max(1, parts.Sum(x => x.Bytes));
        int colIdx = 0;
        foreach (var pt in parts.Where(x => x.Bytes > 0))
        {
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.03, (double)pt.Bytes / sum), GridUnitType.Star) });
            var seg = new Border { Background = ThemeHelper.Brush(pt.Col), CornerRadius = new CornerRadius(2) };
            Grid.SetColumn(seg, colIdx++);
            barGrid.Children.Add(seg);
        }
        if (colIdx == 0) barGrid.Children.Add(new Border { Background = ThemeHelper.Brush(p.Inset), CornerRadius = new CornerRadius(4) });
        barWrap.Children.Add(barGrid);

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var pt in parts)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            item.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = ThemeHelper.Brush(pt.Col), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = $"{pt.Label} {FormatBytes(pt.Bytes)}", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T2) });
            legend.Children.Add(item);
        }
        barWrap.Children.Add(legend);

        var purgeBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "立即清空", Foreground = ThemeHelper.Brush(p.Danger), FontSize = 12 };
        purgeBtn.Click += async (_, _) =>
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
        };

        string qaCacheDir = Path.Combine(_library.DataDir, "companion-cache");
        long qaBytes = DirSize(qaCacheDir);
        var clearQaBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "清理", Foreground = ThemeHelper.Brush(p.Danger), FontSize = 12, IsEnabled = qaBytes > 0 };
        clearQaBtn.Click += (_, _) =>
        {
            try { if (Directory.Exists(qaCacheDir)) Directory.Delete(qaCacheDir, recursive: true); } catch { }
            RenderDynamicPage("storage");
        };

        var cleanupRows = new List<UIElement>
        {
            barWrap,
            MakeRow("最近删除", "保留 30 天以便恢复，到期自动清理", purgeBtn),
        };
        if (voiceBytes > 0)
        {
            var rmModelBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "移除", Foreground = ThemeHelper.Brush(p.Danger), FontSize = 12 };
            rmModelBtn.Click += (_, _) => { _library.LiveVoice.DeleteInstalledModels(); RenderDynamicPage("storage"); };
            cleanupRows.Add(MakeRow("语音合成模型", FormatBytes(voiceBytes), rmModelBtn));
        }
        cleanupRows.Add(MakeRow($"书伴问答缓存（{FormatBytes(qaBytes)}）", "同章节重复提问直接读取本地缓存，不重复调用模型", clearQaBtn));
        DynamicPageHost.Children.Add(MakeCard(cleanupRows.ToArray()));

        // 各书占用空间排行（前 12 本，1:1 MacSettings.swift:1312-1334）
        var kept = _library.GetKeptBooks();
        var bookSizes = kept
            .Select(b => (b.Title, Bytes: DirSize(Path.Combine(_library.DataDir, "books", b.Id))))
            .Where(x => x.Bytes > 0)
            .OrderByDescending(x => x.Bytes)
            .Take(12)
            .ToArray();

        if (bookSizes.Length > 0)
        {
            DynamicPageHost.Children.Add(MakeHeading("各书占用空间"));
            DynamicPageHost.Children.Add(MakeCard(bookSizes.Select(b =>
                (UIElement)MakeRow(b.Title, null, new TextBlock { Text = FormatBytes(b.Bytes), FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) }, compact: true)).ToArray()));
        }
    }

    // ---------- 9. 数据（1:1 MacSettings.swift:1131-1244 & SnapshotSheets.swift） ----------

    private void BuildDataPage()
    {
        var p = CurrentPalette;
        var tint = CurrentTint;
        var counts = _library.GetSnapshotCounts();
        string snapDir = Path.Combine(_library.DataDir, "snapshots");
        Directory.CreateDirectory(snapDir);

        DynamicPageHost.Children.Add(MakeHeading("备份快照"));

        var estText = new TextBlock { FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        void RefreshEstimate()
        {
            long est = (_snapFiles ? (long)counts.FileBytes : 0) + 64 * 1024;
            estText.Text = $"预计快照大小 {FormatBytes(est)}";
        }
        RefreshEstimate();

        var makeBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
            Content = "生成快照",
        };
        makeBtn.Click += (_, _) =>
        {
            try
            {
                string filePath = Path.Combine(snapDir, $"语阅快照-{DateTime.Now:yyyyMMdd-HHmmss}.yuyue");
                var parts = new SnapshotParts(_snapProgress, _snapNotes, _snapWords, _snapSettings, _snapFiles);
                ulong bytes = _library.MakeSnapshot(filePath, parts);
                RecordChange("备份快照", FormatBytes((long)bytes));
                RenderDynamicPage("data");
                RenderInspector("data");
            }
            catch (Exception ex) { _ = ShowInfoDialogAsync("生成快照失败", ex.Message); }
        };

        var actionRow = new Grid { Padding = new Thickness(16, 10, 16, 10) };
        actionRow.Children.Add(estText);
        makeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        actionRow.Children.Add(makeBtn);

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("进度和书签", $"读过 {counts.Books} 本", MakeSwitch(_snapProgress, v => { _snapProgress = v; RefreshEstimate(); }), compact: true),
            MakeRow("笔记", $"{counts.Notes} 条", MakeSwitch(_snapNotes, v => { _snapNotes = v; RefreshEstimate(); }), compact: true),
            MakeRow("生词", $"{counts.Words} 个词", MakeSwitch(_snapWords, v => { _snapWords = v; RefreshEstimate(); }), compact: true),
            MakeRow("设置和替换规则", null, MakeSwitch(_snapSettings, v => { _snapSettings = v; RefreshEstimate(); }), compact: true),
            MakeRow("书文件", FormatBytes((long)counts.FileBytes), MakeSwitch(_snapFiles, v => { _snapFiles = v; RefreshEstimate(); }), compact: true),
            actionRow));

        // 从外部快照恢复 & Goodreads / StoryGraph 导出（1:1 SnapshotSheets.swift）
        var importExtBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "选择快照文件…" };
        importExtBtn.Click += async (_, _) =>
        {
            if (await SnapshotAndConflictDialogs.ShowSnapshotImportDialogAsync(this, RootGrid.XamlRoot, _library))
            {
                RenderDynamicPage("data");
                RenderInspector("data");
            }
        };

        var csvBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "导出…" };
        csvBtn.Click += async (_, _) => await SnapshotAndConflictDialogs.ShowReadingExportSheetAsync(this, RootGrid.XamlRoot, _library);

        DynamicPageHost.Children.Add(MakeCard(
            MakeRow("从其他文件导入", "打开任意设备生成的 .yuyue 快照文件合并或覆盖", importExtBtn),
            MakeRow("导出阅读记录", "导出想读、在读、读完状态与完成日期（CSV），可导入 Goodreads 或 StoryGraph", csvBtn)));

        // 历史快照列表（1:1 MacSettings.swift:1185-1208）
        var savedSnaps = Directory.GetFiles(snapDir, "*.yuyue")
            .Select(f => new FileInfo(f))
            .OrderByDescending(fi => fi.LastWriteTime)
            .ToArray();

        if (savedSnaps.Length > 0)
        {
            DynamicPageHost.Children.Add(MakeHeading("历史快照"));
            var snapRows = new List<UIElement>();
            foreach (var fi in savedSnaps.Take(8))
            {
                string fullPath = fi.FullName;
                var impBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "导入", Height = 26, MinHeight = 26 };
                impBtn.Click += async (_, _) =>
                {
                    if (await SnapshotAndConflictDialogs.ShowSnapshotImportDialogAsync(this, RootGrid.XamlRoot, _library, fullPath))
                    {
                        RenderDynamicPage("data");
                        RenderInspector("data");
                    }
                };

                var rowEl = MakeRow(fi.LastWriteTime.ToString("M 月 d 日 HH:mm"), $"{Environment.MachineName} · {FormatBytes(fi.Length)}", impBtn, compact: true);
                var ctx = new MenuFlyout();
                var revMi = new MenuFlyoutItem { Text = "在资源管理器中显示" };
                revMi.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"") { UseShellExecute = true });
                var delMi = new MenuFlyoutItem { Text = "删除…", Foreground = ThemeHelper.Brush(p.Danger) };
                delMi.Click += (_, _) => { try { File.Delete(fullPath); } catch { } RenderDynamicPage("data"); RenderInspector("data"); };
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

    private async Task ShowSnapshotImportSheetAsync(string path)
    {
        try
        {
            var about = _library.PeekSnapshot(path);
            var dialog = new ContentDialog
            {
                Title = "导入快照",
                Content = $"文件：{Path.GetFileName(path)}\n来自设备：{about.DeviceName}（{about.Platform}）\n包含：{about.Counts.Books} 本书 · {about.Counts.Notes} 条划线 · {about.Counts.Words} 个生词\n\n• 合并（推荐）：保留本机已有书籍与笔记，把快照增量汇入\n• 覆盖：先自动备份一份当前快照，再用该快照完全替换",
                PrimaryButtonText = "合并导入",
                SecondaryButtonText = "覆盖本机",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
            };
            var res = await dialog.ShowAsync();
            if (res is ContentDialogResult.Primary or ContentDialogResult.Secondary)
            {
                bool replace = res == ContentDialogResult.Secondary;
                if (replace)
                {
                    try
                    {
                        string autoBackup = Path.Combine(_library.DataDir, "snapshots", $"覆盖前自动备份-{DateTime.Now:yyyyMMdd-HHmmss}.yuyue");
                        _library.MakeSnapshot(autoBackup, new SnapshotParts(true, true, true, true, false));
                    }
                    catch { }
                }
                uint changed = _library.RestoreSnapshot(path, replace);
                App.CurrentApp.NotifyShelfRefresh();
                RenderDynamicPage("data");
                RenderInspector("data");
                await ShowInfoDialogAsync("导入完成", $"已应用 {changed} 条记录。");
            }
        }
        catch (Exception ex) { await ShowInfoDialogAsync("无法读取快照", ex.Message); }
    }

    private async Task ShowReadingExportSheetAsync()
    {
        var p = CurrentPalette;
        var kept = _library.GetKeptBooks();
        string format = "goodreads";
        ContentDialog? dlgRef = null;

        int wantCnt = kept.Count(b => b.Status == ReadingStatus.Want);
        int readCnt = kept.Count(b => b.Status == ReadingStatus.Reading);
        int doneCnt = kept.Count(b => b.Status == ReadingStatus.Finished);

        var previewTb = new TextBlock
        {
            FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()),
            FontSize = 10.5,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        List<string> BuildCsvLines()
        {
            var lines = new List<string>();
            if (format == "storygraph")
            {
                lines.Add("Title,Authors,Read Status,Star Rating");
                foreach (var b in kept)
                {
                    string st = b.Status switch { ReadingStatus.Finished => "read", ReadingStatus.Reading => "currently-reading", _ => "to-read" };
                    lines.Add($"\"{b.Title.Replace("\"", "\"\"")}\",\"{(b.Authors.FirstOrDefault() ?? "").Replace("\"", "\"\"")}\",{st},{b.Rating?.ToString() ?? ""}");
                }
            }
            else
            {
                lines.Add("Title,Author,Exclusive Shelf,My Rating");
                foreach (var b in kept)
                {
                    string st = b.Status switch { ReadingStatus.Finished => "read", ReadingStatus.Reading => "currently-reading", _ => "to-read" };
                    lines.Add($"\"{b.Title.Replace("\"", "\"\"")}\",\"{(b.Authors.FirstOrDefault() ?? "").Replace("\"", "\"\"")}\",{st},{b.Rating?.ToString() ?? ""}");
                }
            }
            return lines;
        }

        void RefreshPreview() => previewTb.Text = string.Join("\n", BuildCsvLines().Take(4));
        RefreshPreview();

        var segHost = new ContentControl();
        void RebuildSeg()
        {
            segHost.Content = MakeSegments(
                [("goodreads", "Goodreads"), ("storygraph", "StoryGraph")],
                format,
                f => { format = f; RebuildSeg(); RefreshPreview(); });
        }
        RebuildSeg();

        var stack = new StackPanel { Spacing = 10, Width = 380 };
        stack.Children.Add(segHost);
        stack.Children.Add(MakeCard(
            MakeRow("想读", null, new TextBlock { Text = $"{wantCnt} 本", FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) }, compact: true),
            MakeRow("在读", null, new TextBlock { Text = $"{readCnt} 本", FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) }, compact: true),
            MakeRow("读过", null, new TextBlock { Text = $"{doneCnt} 本", FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) }, compact: true)));
        stack.Children.Add(new Border { CornerRadius = new CornerRadius(9), Background = ThemeHelper.Brush(p.Inset), Padding = new Thickness(10, 8, 10, 8), Child = previewTb });

        dlgRef = new ContentDialog
        {
            Title = "导出阅读记录",
            Content = stack,
            PrimaryButtonText = $"导出 {kept.Count} 本 · CSV",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlgRef.ShowAsync() == ContentDialogResult.Primary)
        {
            var picker = new FileSavePicker { SuggestedFileName = $"语阅-{format}-{DateTime.Now:yyyyMMdd}" };
            picker.FileTypeChoices.Add("CSV", [".csv"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var f = await picker.PickSaveFileAsync();
            if (f is not null)
                await File.WriteAllLinesAsync(f.Path, BuildCsvLines(), new System.Text.UTF8Encoding(true));
        }
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
            Width = 68,
            Height = 68,
            CornerRadius = new CornerRadius(16),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = ThemeHelper.Brush(Color.FromArgb(255, 251, 250, 247)),
            BorderBrush = ThemeHelper.Brush(Color.FromArgb(28, 0, 0, 0)),
            BorderThickness = new Thickness(0.5),
            Child = markInner,
        };
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

        // 底部铅笔手写签名曲线 + by nowtin · GPL-3.0
        var sigWrap = new StackPanel { Spacing = 4, Margin = new Thickness(0, 20, 0, 4) };
        sigWrap.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = YYIcons.ParseGeometry("M2,18 C10,6 16,20 22,10 C27,3 30,17 37,11 C43,6 47,16 54,9 C61,3 68,15 78,12 C88,9 98,13 112,10"),
            Stroke = ThemeHelper.Brush(p.T2),
            StrokeThickness = 1.35,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0.68,
            HorizontalAlignment = HorizontalAlignment.Left,
        });

        var leftFoot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        leftFoot.Children.Add(new TextBlock { Text = "by", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        var authorBtn = new Button { Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(2, 0, 2, 0), Content = new TextBlock { Text = "nowtin", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) }, UseSystemFocusVisuals = false };
        authorBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri("https://github.com/nowtin"));
        leftFoot.Children.Add(authorBtn);
        leftFoot.Children.Add(new TextBlock { Text = "· GPL-3.0", FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        sigWrap.Children.Add(leftFoot);
        Grid.SetRow(sigWrap, 1);
        leftGrid.Children.Add(sigWrap);
        bookplate.Children.Add(leftGrid);

        // 右页：牌记与操作卡片
        var rightStack = new StackPanel { Spacing = 12, Margin = new Thickness(0, 22, 0, 0) };

        var statusLine = new TextBlock
        {
            Text = _library.Updates.LatestOffer is { } off ? $"发现新版本 v{off.Version}" : "尚未检查更新",
            FontSize = 12,
            Foreground = ThemeHelper.Brush(_library.Updates.LatestOffer is not null ? tint.Accent : p.T3),
        };
        var checkBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "检查更新" };
        checkBtn.Click += async (_, _) =>
        {
            checkBtn.IsEnabled = false;
            statusLine.Text = "正在检查更新…";
            try
            {
                await _library.Updates.ShowUpdateCheckDialogAsync(RootGrid.XamlRoot);
                statusLine.Text = _library.Updates.LatestOffer is { } latest ? $"发现新版本 v{latest.Version}" : "已是最新版本";
                statusLine.Foreground = ThemeHelper.Brush(_library.Updates.LatestOffer is not null ? tint.Accent : p.Ok);
            }
            finally { checkBtn.IsEnabled = true; }
        };

        var verRow = new Grid { Padding = new Thickness(16, 14, 16, 14) };
        var verLeft = new StackPanel { Spacing = 3 };
        var verHead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        verHead.Children.Add(new TextBlock { Text = "第 0.1.0 版", FontFamily = serif, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1) });
        verHead.Children.Add(new TextBlock { Text = "build 1", FontFamily = new FontFamily(YYFontFamily.Mono.WindowsFamily()), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) });
        verLeft.Children.Add(verHead);
        verLeft.Children.Add(statusLine);
        verRow.Children.Add(verLeft);
        checkBtn.HorizontalAlignment = HorizontalAlignment.Right;
        checkBtn.VerticalAlignment = VerticalAlignment.Center;
        verRow.Children.Add(checkBtn);

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

        string changelogPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Shared", "CHANGELOG.md");
        Release[] releases = [];
        try { if (File.Exists(changelogPath)) releases = YuyueCoreMethods.ParseChangelog(File.ReadAllText(changelogPath)); } catch { }
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
            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
            Content = (_library.Prefs.UpdateChannel == "beta" ? "测试版" : "正式版") + " ⌄",
            Height = 26,
            MinHeight = 26,
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

        var srcBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = "GitHub · GPL-3.0 ↗", FontSize = 12, Foreground = ThemeHelper.Brush(p.T1) };
        srcBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(new Uri("https://github.com/inokoru/yuyue"));

        rightStack.Children.Add(MakeCard(
            MakeRow("问题与建议", "可附截图或日志直达开发者，无需注册账号", feedbackRight),
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
        _library.Prefs.ThemeMode = mode;
        _library.SavePrefs();
        RecordChange("界面外观", mode switch { "light" => "浅色", "dark" => "深色", _ => "跟随系统" }, () => { _library.Prefs.ThemeMode = prev; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); });
        App.CurrentApp.BroadcastAppearanceChanged();
    }

    private void TintDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tintId) return;
        string prev = _library.Prefs.TintId;
        _library.Prefs.TintId = tintId;
        _library.SavePrefs();
        RecordChange("主题色", YYTints.All.FirstOrDefault(t => t.Id == tintId)?.Name ?? tintId, () => { _library.Prefs.TintId = prev; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); });
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
        RecordChange("封面大小", $"{(int)next}", () => { _library.Prefs.CoverWidth = prev; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); });
        App.CurrentApp.BroadcastAppearanceChanged();
    }

    private void ShowProgressToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool on = ShowProgressToggle.IsOn;
        _library.Prefs.ShowProgress = on;
        _library.SavePrefs();
        RecordChange("封面下显示进度", on ? "开" : "关", () => { _library.Prefs.ShowProgress = !on; _library.SavePrefs(); App.CurrentApp.BroadcastAppearanceChanged(); });
        App.CurrentApp.BroadcastAppearanceChanged();
    }
}
