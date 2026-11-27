using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Shelf;
using Yuyue.UI.Controls;

namespace Yuyue.UI.Settings;

public sealed partial class SettingsView
{
    private void BuildGeneralPage(bool dark)
    {
        var p = ThemeHelper.Palette(dark);

        PageBodyStack.Children.Add(MakeSectionHeader("Windows 11 窗口与标签页", dark, first: true));

        var winExp = new FluentSettingsExpander(
            "窗口与多标签行为",
            "控制开书方式、会话恢复与点击右上角 × 时的托盘行为",
            "split-view",
            initiallyExpanded: true,
            dark: dark);

        var openInTabSw = new ToggleSwitch { IsOn = _library.Prefs.OpenBookInTab };
        openInTabSw.Toggled += (_, _) => { _library.Prefs.OpenBookInTab = openInTabSw.IsOn; _library.SavePrefs(); };
        winExp.AddKeyValueRow("默认在新标签页中打开书籍", "关闭则每次新开独立窗口（也随时支持拖拽标签撕出独立窗口）", openInTabSw);

        var restoreTabsSw = new ToggleSwitch { IsOn = _library.Prefs.RestoreLastTabs };
        restoreTabsSw.Toggled += (_, _) => { _library.Prefs.RestoreLastTabs = restoreTabsSw.IsOn; _library.SavePrefs(); };
        winExp.AddKeyValueRow("下次启动时恢复上次打开的标签页", "保留未关闭的书籍标签与左右分屏对照", restoreTabsSw);

        var closeCombo = new ComboBox { Width = 264 };
        (string Key, string Label)[] closeModes =
        [
            ("tray_when_playing", "听书时最小化到托盘，否则退出"),
            ("always_tray", "始终最小化到系统托盘"),
            ("exit", "直接退出语阅"),
        ];
        foreach (var (_, lbl) in closeModes) closeCombo.Items.Add(L10n.T(lbl));
        closeCombo.SelectedIndex = Math.Max(0, Array.FindIndex(closeModes, x => x.Key == _library.Prefs.CloseWindowBehavior));
        closeCombo.SelectionChanged += (_, _) =>
        {
            if (closeCombo.SelectedIndex >= 0)
            {
                _library.Prefs.CloseWindowBehavior = closeModes[closeCombo.SelectedIndex].Key;
                _library.SavePrefs();
            }
        };
        winExp.AddKeyValueRow("点击主窗口关闭按钮 (×) 时", "避免边办公边听书时误关导致朗读中断", closeCombo);
        PageBodyStack.Children.Add(winExp);

        PageBodyStack.Children.Add(MakeSectionHeader("鼠键习惯与快捷键", dark));

        var clickCombo = new ComboBox { Width = 264 };
        clickCombo.Items.Add(L10n.T("单击选中、双击打开（推荐）"));
        clickCombo.Items.Add(L10n.T("单击直接打开书籍"));
        clickCombo.SelectedIndex = _library.Prefs.ShelfSingleClickOpen ? 1 : 0;
        clickCombo.SelectionChanged += (_, _) =>
        {
            _library.Prefs.ShelfSingleClickOpen = clickCombo.SelectedIndex == 1;
            _library.SavePrefs();
            App.CurrentApp.NotifyShelfRefresh();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "书库封面点击习惯",
            "「单击选中 + 双击打开」贴合 Windows 资源管理器直觉，支持 Ctrl 多选批量整理",
            "shelf",
            clickCombo,
            dark: dark));

        var spaceCombo = new ComboBox { Width = 264 };
        spaceCombo.Items.Add(L10n.T("智能空格：静读翻页 / 听书暂停"));
        spaceCombo.Items.Add(L10n.T("始终控制朗读 / 暂停"));
        spaceCombo.SelectedIndex = _library.Prefs.ReaderSpaceAction == "listen" ? 1 : 0;
        spaceCombo.SelectionChanged += (_, _) =>
        {
            _library.Prefs.ReaderSpaceAction = spaceCombo.SelectedIndex == 1 ? "listen" : "smart";
            _library.SavePrefs();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "阅读器空格键行为",
            "静读时按空格自然下翻页；朗读或听书播放中按空格立即暂停/继续",
            "keyboard",
            spaceCombo,
            dark: dark));

        var scExp = new FluentSettingsExpander(
            "Windows 快捷键速查表",
            "浏览全部已内置的 Windows 11 桌面快捷键与鼠标手势",
            "keyboard",
            initiallyExpanded: false,
            dark: dark);
        (string Keys, string Desc)[] shortcuts =
        [
            ("Ctrl + 1..9 / Ctrl + Tab", "快速切换顶部标签页（Ctrl+1 回到书架）"),
            ("Ctrl + W / 鼠标中键", "关闭当前书籍标签页"),
            ("Ctrl + Shift + T", "恢复刚刚关闭的书籍标签页"),
            ("Ctrl + K", "全局搜索书名、原文、笔记或直接问书伴"),
            ("F11", "进入或退出无边框沉浸全屏（移至顶边唤出）"),
            ("Ctrl + 鼠标滚轮", "阅读器实时调整正文字号 / PDF 缩放"),
            ("Alt + ← / → 或鼠标侧键", "阅读跳转历史后退与前进"),
            ("Ctrl + Alt + T / N", "阅读器开关目录栏 / 笔记栏（Esc 关闭右侧栏）"),
            ("Ctrl + Alt + ↑ / ↓", "阅读器切换上一章 / 下一章"),
            ("Ctrl + [ / ]", "朗读中切换上一句 / 下一句"),
            ("Ctrl + I", "查看当前书籍信息"),
            ("Ctrl + F / F3 / Shift + F3", "书内全文搜索与跳转下一处 / 上一处"),
            ("Ctrl + B / Ctrl + E", "书架折叠侧栏导航 / 聚焦内联筛选框"),
            ("Ctrl + Z / Ctrl + Y", "在设置页撤销或还原最近改动"),
            ("F2 / Delete", "书架选中书后编辑信息 / 删除"),
        ];
        foreach (var (k, d) in shortcuts)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = ThemeHelper.Brush(p.Inset),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 3, 8, 3),
                Child = new TextBlock { Text = L10n.T(k), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T1) },
            };
            scExp.AddKeyValueRow(d, null, badge);
        }
        PageBodyStack.Children.Add(scExp);

        PageBodyStack.Children.Add(MakeSectionHeader("系统集成、语言与导入", dark));

        var assocBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Content = L10n.T("一键注册文件关联"),
        };
        assocBtn.Click += (_, _) => RegisterWindowsFileAssociations();
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "Windows 文件格式关联 (.epub / .pdf / .txt / .mobi / .azw3)",
            "注册当前用户打开方式，在资源管理器双击电子书可直接用语阅新标签打开",
            "folder",
            assocBtn,
            dark: dark));

        var autoStartSw = new ToggleSwitch { IsOn = _library.Prefs.AutoLaunchOnLogin };
        autoStartSw.Toggled += (_, _) =>
        {
            _library.Prefs.AutoLaunchOnLogin = autoStartSw.IsOn;
            _library.SavePrefs();
            SystemIntegrationService.SyncLoginStartup(autoStartSw.IsOn);
        };
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "登录 Windows 时静默启动",
            "开机后自动在系统托盘后台运行并保持同步与监视目录",
            "laptop",
            autoStartSw,
            dark: dark));

        var awakeSw = new ToggleSwitch { IsOn = _library.Prefs.KeepAwakeWhileListening };
        awakeSw.Toggled += (_, _) => { _library.Prefs.KeepAwakeWhileListening = awakeSw.IsOn; _library.SavePrefs(); };
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "朗读时防止系统休眠",
            "朗读或播放有声书期间保持电脑唤醒",
            "clock",
            awakeSw,
            dark: dark));

        var langCombo = new ComboBox { Width = 160 };
        (string Key, string Label)[] langs = [("system", "跟随系统"), ("zh-Hans", "中文"), ("en", "English")];
        foreach (var (_, lbl) in langs) langCombo.Items.Add(lbl == "跟随系统" ? L10n.T(lbl) : lbl);
        langCombo.SelectedIndex = Math.Max(0, Array.FindIndex(langs, x => x.Key == _library.Prefs.UiLanguage));
        langCombo.SelectionChanged += (_, _) =>
        {
            if (langCombo.SelectedIndex >= 0)
            {
                _library.Prefs.UiLanguage = langs[langCombo.SelectedIndex].Key;
                _library.SavePrefs();
                _library.ApplyUiLanguage();
                App.CurrentApp.BroadcastAppearanceChanged();
            }
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("语言", "即时切换简体中文与英文界面", "globe", langCombo, dark: dark));

        bool hasWatchedFolder = !string.IsNullOrWhiteSpace(_library.Prefs.WatchedFolder);
        var watchActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var watchBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Content = !hasWatchedFolder ? L10n.T("选一个文件夹") : Path.GetFileName(_library.Prefs.WatchedFolder),
        };
        watchBtn.Click += async (_, _) =>
        {
            string? folder = await NativePickerHelper.PickSingleFolderAsync(_ownerWindow);
            if (!string.IsNullOrEmpty(folder))
            {
                _library.Prefs.WatchedFolder = folder;
                _library.SavePrefs();
                _library.WatchedFolder.Reload();
                RenderActivePage();
            }
        };
        watchActs.Children.Add(watchBtn);
        if (hasWatchedFolder)
        {
            var clearWatchBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("停止监听"),
                Foreground = ThemeHelper.Brush(p.Danger),
            };
            clearWatchBtn.Click += (_, _) =>
            {
                _library.Prefs.WatchedFolder = "";
                _library.SavePrefs();
                _library.WatchedFolder.Reload();
                RenderActivePage();
            };
            watchActs.Children.Add(clearWatchBtn);
        }
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "监听一个文件夹",
            !hasWatchedFolder ? "放进这个文件夹的书，下次打开语阅时自动导入" : _library.Prefs.WatchedFolder,
            "folder-plus",
            watchActs,
            dark: dark));

        var wifiBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("打开") };
        wifiBtn.Click += (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                _ = ShelfFeaturesDialogs.ShowWifiTransferDialogAsync(xr, _library, () => App.CurrentApp.NotifyShelfRefresh());
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("Wi‑Fi 传书", "同 Wi-Fi 下从手机或平板浏览器直接传书到电脑", "wifi", wifiBtn, dark: dark));
    }

    private void RegisterWindowsFileAssociations()
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe)) return;
            const string progId = "Yuyue.Ebook";
            using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
            {
                k.SetValue("", "语阅电子书文档");
                using var ico = k.CreateSubKey("DefaultIcon");
                ico.SetValue("", $"\"{exe}\",0");
                using var cmd = k.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", $"\"{exe}\" \"%1\"");
            }
            foreach (var ext in new[] { ".epub", ".pdf", ".txt", ".mobi", ".azw3", ".yuyue" })
            {
                using var ek = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
                ek.SetValue(progId, "", RegistryValueKind.String);
            }
            _ownerWindow.ShowToast("已将 .epub / .pdf / .txt / .mobi / .azw3 / .yuyue 注册到 Windows 打开方式");
        }
        catch (Exception ex)
        {
            _ownerWindow.ShowToast($"注册文件关联失败：{ex.Message}");
        }
    }

    private void BuildAppearancePage(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        PageBodyStack.Children.Add(MakeSectionHeader("主题与背景材质", dark, first: true));

        var themeSeg = YYChrome.MakeSegmentedBar(
            [("light", "浅色"), ("dark", "深色"), ("system", "跟随系统")],
            _library.Prefs.ThemeMode,
            cap =>
            {
                _library.Prefs.ThemeMode = cap;
                _library.SavePrefs();
                App.CurrentApp.BroadcastAppearanceChanged();
            },
            dark,
            fontSize: 12,
            height: 32);
        PageBodyStack.Children.Add(new FluentSettingsCard("界面", "可随时跟随 Windows 11 浅色或深色主题", "sun", themeSeg, dark: dark));

        var micaSw = new ToggleSwitch { IsOn = _library.Prefs.UseWarmMica };
        micaSw.Toggled += (_, _) =>
        {
            _library.Prefs.UseWarmMica = micaSw.IsOn;
            _library.SavePrefs();
            App.CurrentApp.BroadcastAppearanceChanged();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "Warm Mica 暖云母外壳材质",
            "在标题栏与左侧导航栏叠加 Windows 11 原生暖调云母质感",
            "circles-venn",
            micaSw,
            dark: dark));

        // Refined Windows 11 circular color swatches (clean accent ring + checkmark + compact label)
        var tintRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        foreach (var tint in ThemeHelper.AllTints)
        {
            var c = ThemeHelper.TintColors(tint, dark);
            bool sel = string.Equals(_library.Prefs.TintId, tint.Id, StringComparison.OrdinalIgnoreCase);
            string capId = tint.Id;

            var swatchInner = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = ThemeHelper.Brush(c.Fill),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = sel ? YYIcons.Make("checkmark", 11, ThemeHelper.Brush(Microsoft.UI.Colors.White)) : null,
            };
            var swatchRing = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                BorderBrush = sel ? ThemeHelper.Brush(c.Accent) : ThemeHelper.Brush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = swatchInner,
            };

            var col = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(swatchRing);
            col.Children.Add(new TextBlock
            {
                Text = L10n.T(tint.Name),
                FontSize = 10.5,
                FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ThemeHelper.Brush(sel ? c.Accent : p.T2),
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var swatchBtn = new Button
            {
                Padding = new Thickness(4, 2, 4, 2),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = col,
            };
            ToolTipService.SetToolTip(swatchBtn, L10n.T(tint.Name));
            swatchBtn.Click += (_, _) =>
            {
                _library.Prefs.TintId = capId;
                _library.SavePrefs();
                App.CurrentApp.BroadcastAppearanceChanged();
            };
            tintRow.Children.Add(swatchBtn);
        }
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "主题色",
            "用于按钮、开关与进度标记",
            "sparkle",
            tintRow,
            dark: dark));

        // Visual App Icon preview selector (6 bundled PNG tiles matching the circular tint swatches above)
        string curIconId = ThemeHelper.NormalizeAppIconId(_library.Prefs.AppIconId);
        var iconRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (iconId, iconTitle) in ThemeHelper.AppIcons)
        {
            string capId = iconId;
            bool sel = string.Equals(curIconId, capId, StringComparison.OrdinalIgnoreCase);

            var previewImg = ThemeHelper.CreateAppIconPreviewImage(capId, dark, size: 36);
            var imgTile = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(8),
                BorderBrush = ThemeHelper.Brush(sel ? Microsoft.UI.Colors.Transparent : p.Line),
                BorderThickness = new Thickness(sel ? 0 : 1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = previewImg,
            };
            var iconRing = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(10),
                BorderBrush = ThemeHelper.Brush(sel ? tc.Accent : Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = imgTile,
            };

            var col = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(iconRing);
            col.Children.Add(new TextBlock
            {
                Text = L10n.T(iconTitle),
                FontSize = 10.5,
                FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ThemeHelper.Brush(sel ? tc.Accent : p.T2),
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var iconBtn = new Button
            {
                Padding = new Thickness(3, 2, 3, 2),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = col,
            };
            ToolTipService.SetToolTip(iconBtn, L10n.T(iconTitle));
            iconBtn.Click += (_, _) =>
            {
                if (string.Equals(_library.Prefs.AppIconId, capId, StringComparison.OrdinalIgnoreCase)) return;
                _library.Prefs.AppIconId = capId;
                _library.SavePrefs();
                App.CurrentApp.BroadcastAppearanceChanged();
            };
            iconRow.Children.Add(iconBtn);
        }
        PageBodyStack.Children.Add(new FluentSettingsCard("应用图标", "切换任务栏与标题栏图标色调", "photo", iconRow, dark: dark));

        var einkSw = new ToggleSwitch { IsOn = _library.Prefs.GlobalEinkMode };
        einkSw.Toggled += (_, _) =>
        {
            _library.Prefs.GlobalEinkMode = einkSw.IsOn;
            _library.SavePrefs();
            BroadcastReaderSettingsWithoutSelfRefresh();
            App.CurrentApp.BroadcastAppearanceChanged();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "全局墨水屏护眼模式",
            "减少动效与柔和灰阶显示，适合长时间深度文字阅读",
            "eye",
            einkSw,
            dark: dark));
    }
}
