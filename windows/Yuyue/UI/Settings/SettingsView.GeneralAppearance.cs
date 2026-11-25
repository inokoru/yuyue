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
            ("Ctrl + 1..9 / Ctrl + Tab", "快速切换顶部标签页（Ctrl+1 回到书库）"),
            ("Ctrl + W / 鼠标中键", "关闭当前书籍标签页"),
            ("Ctrl + Shift + T", "恢复刚刚关闭的书籍标签页"),
            ("Ctrl + K", "全局搜索书名、原文、笔记或直接问书伴"),
            ("F11", "进入或退出无边框沉浸全屏（移至顶边唤出）"),
            ("Ctrl + 鼠标滚轮", "阅读器实时调整正文字号 / PDF 缩放"),
            ("Alt + ← / → 或鼠标侧键", "阅读跳转历史后退与前进"),
            ("Ctrl + F / F3 / Shift + F3", "书内全文搜索与跳转下一处 / 上一处"),
            ("Ctrl + B / Ctrl + E", "书库折叠侧栏导航 / 聚焦内联筛选框"),
            ("F2 / Delete", "书库选中书籍后编辑信息 / 移出书库"),
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
                Child = new TextBlock { Text = k, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, Foreground = ThemeHelper.Brush(p.T1) },
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

        var langCombo = new ComboBox { Width = 160 };
        (string Key, string Label)[] langs = [("system", "跟随系统"), ("zh-Hans", "简体中文"), ("en", "English")];
        foreach (var (_, lbl) in langs) langCombo.Items.Add(lbl);
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
        PageBodyStack.Children.Add(new FluentSettingsCard("界面语言", "即时切换简体中文与英文界面", "globe", langCombo, dark: dark));

        var watchBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Content = string.IsNullOrWhiteSpace(_library.Prefs.WatchedFolder) ? L10n.T("选择文件夹…") : Path.GetFileName(_library.Prefs.WatchedFolder),
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
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "自动监视导入目录",
            string.IsNullOrWhiteSpace(_library.Prefs.WatchedFolder) ? "尚未设置监视文件夹" : _library.Prefs.WatchedFolder,
            "folder-plus",
            watchBtn,
            dark: dark));

        var wifiBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("打开 Wi-Fi 传书") };
        wifiBtn.Click += (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
                _ = ShelfFeaturesDialogs.ShowWifiTransferDialogAsync(xr, _library, () => App.CurrentApp.NotifyShelfRefresh());
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("局域网 Wi-Fi 传书", "同 Wi-Fi 下从手机或平板浏览器直接传书到电脑", "wifi", wifiBtn, dark: dark));
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
        PageBodyStack.Children.Add(new FluentSettingsCard("外观模式", "可随时跟随 Windows 11 浅色或深色主题", "sun", themeSeg, dark: dark));

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
            "主题强调色",
            "保留语阅 4 套东方低饱和纸墨色，同时提供 Windows 11 系统强调蓝",
            "sparkle",
            tintRow,
            dark: dark));

        var iconCombo = new ComboBox { Width = 150 };
        foreach (var (_, title) in ThemeHelper.AppIcons) iconCombo.Items.Add(L10n.T(title));
        iconCombo.SelectedIndex = Math.Max(0, Array.FindIndex(ThemeHelper.AppIcons, x => x.Id == _library.Prefs.AppIconId));
        iconCombo.SelectionChanged += (_, _) =>
        {
            if (iconCombo.SelectedIndex >= 0)
            {
                _library.Prefs.AppIconId = ThemeHelper.AppIcons[iconCombo.SelectedIndex].Id;
                _library.SavePrefs();
                App.CurrentApp.BroadcastAppearanceChanged();
            }
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("应用窗口图标", "切换任务栏与标题栏图标色调", "photo", iconCombo, dark: dark));

        PageBodyStack.Children.Add(MakeSectionHeader("纸张联动与护眼", dark));

        var followSw = new ToggleSwitch { IsOn = _library.Prefs.PaperFollowAppearance };
        followSw.Toggled += (_, _) =>
        {
            var rs = _library.GetReaderSettings();
            if (PaperAppearance.SetFollow(_library, followSw.IsOn, rs, out var updated))
                _library.SaveReaderSettings(updated);
            BroadcastReaderSettingsWithoutSelfRefresh();
            RenderPaperLivePreview(dark);
        };
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "阅读纸张跟随深浅外观",
            "浅色与深色模式切换时自动配对日间与夜间阅读纸，避免夜读刺眼",
            "moon",
            followSw,
            dark: dark));

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

        PageBodyStack.Children.Add(MakeSectionHeader("自定义阅读纸张", dark));
        var newPaperBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Content = L10n.T("+ 新建纸张…"),
        };
        newPaperBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is not { } xr) return;
            var created = await global::Yuyue.Reader.CustomPaperDialog.ShowEditorAsync(xr, _library, existing: null, currentSettings: _library.GetReaderSettings());
            if (created is not null)
            {
                if (PaperAppearance.PickCustom(_library, created, _library.GetReaderSettings(), out var updated))
                    _library.SaveReaderSettings(updated);
                BroadcastReaderSettingsWithoutSelfRefresh();
                RenderActivePage();
            }
        };
        var customPapers = _library.Core.CustomPapers();
        PageBodyStack.Children.Add(new FluentSettingsCard(
            $"我的纸张 ({customPapers.Length})",
            customPapers.Length == 0 ? "调配专属纸底色、墨色与纸纹材质，同步到全部阅读标签" : "点击下方任意自定义纸张可编辑、复制或删除",
            "book-open",
            newPaperBtn,
            dark: dark));

        foreach (var cp in customPapers)
        {
            var cpCap = cp;
            var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(5),
                Background = ThemeHelper.Brush(global::Yuyue.Reader.CustomPaperDialog.ParseHexColor(cpCap.Bg, YYPapers.Kami.Bg)),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var applyBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Height = 28, MinHeight = 28, Content = L10n.T("应用") };
            applyBtn.Click += (_, _) =>
            {
                if (PaperAppearance.PickCustom(_library, cpCap, _library.GetReaderSettings(), out var updated))
                    _library.SaveReaderSettings(updated);
                BroadcastReaderSettingsWithoutSelfRefresh();
                RenderPaperLivePreview(dark);
                _ownerWindow.ShowToast($"已应用纸张「{cpCap.Name}」");
            };
            var editBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Height = 28, MinHeight = 28, Content = L10n.T("编辑") };
            editBtn.Click += async (_, _) =>
            {
                if (RootGrid.XamlRoot is not { } xr) return;
                if (await global::Yuyue.Reader.CustomPaperDialog.ShowEditorAsync(xr, _library, existing: cpCap, currentSettings: _library.GetReaderSettings()) is not null)
                {
                    BroadcastReaderSettingsWithoutSelfRefresh();
                    RenderActivePage();
                }
            };
            var dupBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Height = 28, MinHeight = 28, Content = L10n.T("复制") };
            dupBtn.Click += (_, _) =>
            {
                global::Yuyue.Reader.CustomPaperDialog.DuplicateCustomPaper(_library, cpCap);
                RenderActivePage();
            };
            var delBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Height = 28, MinHeight = 28, Content = L10n.T("删除"), Foreground = ThemeHelper.Brush(p.Danger) };
            delBtn.Click += (_, _) =>
            {
                try { _library.Core.DeleteCustomPaper(cpCap.Id); } catch { }
                PaperAppearance.Forget(_library, cpCap.Id);
                var curRs = _library.GetReaderSettings();
                if (string.Equals(curRs.CustomPaper, cpCap.Id, StringComparison.Ordinal))
                    _library.SaveReaderSettings(curRs with { CustomPaper = null });
                BroadcastReaderSettingsWithoutSelfRefresh();
                RenderActivePage();
            };
            acts.Children.Add(swatch);
            acts.Children.Add(applyBtn);
            acts.Children.Add(editBtn);
            acts.Children.Add(dupBtn);
            acts.Children.Add(delBtn);
            PageBodyStack.Children.Add(new FluentSettingsCard(cpCap.Name, $"{cpCap.Bg} · {cpCap.Layout}", "book-open", acts, dark: dark));
        }
    }
}
