using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Speech;
using Yuyue.UI.Controls;

namespace Yuyue.UI.Settings;

public sealed partial class SettingsView
{
    private void BuildReadingPage(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var rs = _library.GetReaderSettings();
        var activeRes = PaperAppearance.Resolve(_library, rs);

        var paperRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (preset, name, bg, ink) in new[]
        {
            (Paper.Plain, "素白", YYPapers.Plain.Bg, YYPapers.Plain.Ink),
            (Paper.Kami, "纸本", YYPapers.Kami.Bg, YYPapers.Kami.Ink),
            (Paper.Sage, "芦苇", YYPapers.Sage.Bg, YYPapers.Sage.Ink),
            (Paper.Eink, "铅纸", YYPapers.Eink.Bg, YYPapers.Eink.Ink),
            (Paper.Dark, "暗黑", YYPapers.Dark.Bg, YYPapers.Dark.Ink),
            (Paper.KamiNight, "纸本 · 夜", YYPapers.Kaminight.Bg, YYPapers.Kaminight.Ink),
        })
        {
            bool sel = activeRes.Custom is null && activeRes.Preset == preset;
            var prCap = preset;
            var btn = new Button
            {
                MinWidth = 50,
                Height = 32,
                Padding = new Thickness(7, 0, 7, 0),
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(bg),
                BorderBrush = ThemeHelper.Brush(sel ? tc.Accent : p.Line),
                BorderThickness = new Thickness(sel ? 2 : 1),
                Content = new TextBlock { Text = L10n.T(name), FontSize = 11, Foreground = ThemeHelper.Brush(ink), FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal },
                UseSystemFocusVisuals = false,
            };
            btn.Click += (_, _) =>
            {
                if (PaperAppearance.PickPreset(_library, prCap, _library.GetReaderSettings(), out var updated))
                    _library.SaveReaderSettings(updated);
                BroadcastReaderSettingsWithoutSelfRefresh();
                RenderActivePage();
            };
            paperRow.Children.Add(btn);
        }
        PageBodyStack.Children.Add(new FluentSettingsCard("纸色", null, "book-open", paperRow, dark: dark));

        var followSw = new ToggleSwitch { IsOn = _library.Prefs.PaperFollowAppearance };
        followSw.Toggled += (_, _) =>
        {
            var curRs = _library.GetReaderSettings();
            if (PaperAppearance.SetFollow(_library, followSw.IsOn, curRs, out var updated))
                _library.SaveReaderSettings(updated);
            BroadcastReaderSettingsWithoutSelfRefresh();
            RenderPaperLivePreview(dark);
        };
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "纸色跟随外观",
            "浅色与深色模式切换时自动配对日间与夜间阅读纸",
            "moon",
            followSw,
            dark: dark));

        var newPaperBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Content = L10n.T("自己调一张…"),
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
            customPapers.Length == 0 ? "调配专属纸底色、墨色与纸纹" : "点击下方自定义纸张可应用、调整颜色、复制一张或删除",
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
            var applyBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Height = 28, MinHeight = 28, Content = L10n.T("选用") };
            applyBtn.Click += (_, _) =>
            {
                if (PaperAppearance.PickCustom(_library, cpCap, _library.GetReaderSettings(), out var updated))
                    _library.SaveReaderSettings(updated);
                BroadcastReaderSettingsWithoutSelfRefresh();
                RenderPaperLivePreview(dark);
                _ownerWindow.ShowToast($"已选用“{cpCap.Name}”");
            };
            var editBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Height = 28, MinHeight = 28, Content = L10n.T("调整颜色") };
            editBtn.Click += async (_, _) =>
            {
                if (RootGrid.XamlRoot is not { } xr) return;
                if (await global::Yuyue.Reader.CustomPaperDialog.ShowEditorAsync(xr, _library, existing: cpCap, currentSettings: _library.GetReaderSettings()) is not null)
                {
                    BroadcastReaderSettingsWithoutSelfRefresh();
                    RenderActivePage();
                }
            };
            var dupBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Height = 28, MinHeight = 28, Content = L10n.T("复制一张") };
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
            PageBodyStack.Children.Add(new FluentSettingsCard(cpCap.Name, cpCap.Bg, "book-open", acts, dark: dark));
        }

        var fontPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var fontCombo = new ComboBox { Width = 176 };
        var fontEntries = new List<(Font? F, string? Face, string Lbl)>
        {
            (null, null, L10n.T("系统")),
            (Font.Sans, null, L10n.T("黑体")),
            (Font.Serif, null, L10n.T("宋体")),
            (Font.Kai, null, L10n.T("楷体")),
        };
        bool hasCustomOrPackFont = !string.IsNullOrWhiteSpace(rs.FontFace) || rs.Font == Font.WenKai;
        if (hasCustomOrPackFont)
        {
            fontEntries.Insert(0, (rs.Font, rs.FontFace, ResolveActiveFontDisplayName(rs)));
        }
        foreach (var (_, _, l) in fontEntries) fontCombo.Items.Add(l);
        fontCombo.SelectedIndex = hasCustomOrPackFont
            ? 0
            : Math.Max(0, fontEntries.FindIndex(x => x.F == rs.Font && x.Face is null));
        fontCombo.SelectionChanged += (_, _) =>
        {
            if (fontCombo.SelectedIndex >= 0 && fontCombo.SelectedIndex < fontEntries.Count)
            {
                var chosen = fontEntries[fontCombo.SelectedIndex];
                var cur = _library.GetReaderSettings();
                if (cur.Font == chosen.F && string.Equals(cur.FontFace ?? "", chosen.Face ?? "", StringComparison.Ordinal))
                    return;
                _library.SaveReaderSettings(cur with { Font = chosen.F, FontFace = chosen.Face });
                BroadcastReaderSettingsWithoutSelfRefresh();
                RenderActivePage();
            }
        };
        var importFontBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("从文件添加…") };
        importFontBtn.Click += async (_, _) =>
        {
            string? file = await NativePickerHelper.PickSingleFileAsync(_ownerWindow, [".ttf", ".otf", ".ttc", ".woff2"]);
            if (!string.IsNullOrEmpty(file))
            {
                try
                {
                    string imported = _library.Fonts.ImportFontFile(file);
                    _library.SaveReaderSettings(_library.GetReaderSettings() with { FontFace = $"file:{imported}" });
                    BroadcastReaderSettingsWithoutSelfRefresh();
                    RenderActivePage();
                    _ownerWindow.ShowToast($"已导入字体 {imported}");
                }
                catch (Exception ex) { _ownerWindow.ShowToast($"导入字体失败：{ex.Message}"); }
            }
        };
        fontPanel.Children.Add(fontCombo);
        fontPanel.Children.Add(importFontBtn);
        PageBodyStack.Children.Add(new FluentSettingsCard("字体", "支持系统字体与导入 .ttf / .otf 字体文件", "char-book", fontPanel, dark: dark));

        var szSlider = new Slider { Width = 180, Minimum = 12, Maximum = 32, StepFrequency = 1, Value = rs.FontSize };
        var szCard = new FluentSettingsCard("字号", $"{Math.Round(rs.FontSize)}", "sliders", szSlider, dark: dark);
        szSlider.ValueChanged += (_, args) =>
        {
            double nv = Math.Round(args.NewValue);
            _library.SaveReaderSettings(_library.GetReaderSettings() with { FontSize = nv });
            szCard.Description = $"{nv}";
            BroadcastReaderSettingsWithoutSelfRefresh();
            RenderPaperLivePreview(dark);
        };
        PageBodyStack.Children.Add(szCard);

        var lhSlider = new Slider { Width = 180, Minimum = 1.4, Maximum = 2.6, StepFrequency = 0.1, Value = rs.LineHeight ?? 1.9 };
        var lhCard = new FluentSettingsCard("行距", $"{(rs.LineHeight ?? 1.9):0.0}", "list", lhSlider, dark: dark);
        lhSlider.ValueChanged += (_, args) =>
        {
            double nv = Math.Round(args.NewValue, 1);
            _library.SaveReaderSettings(_library.GetReaderSettings() with { LineHeight = nv });
            lhCard.Description = $"{nv:0.0}";
            BroadcastReaderSettingsWithoutSelfRefresh();
            RenderPaperLivePreview(dark);
        };
        PageBodyStack.Children.Add(lhCard);

        var twoColSw = new ToggleSwitch { IsOn = _library.Prefs.TwoColumns };
        twoColSw.Toggled += (_, _) =>
        {
            _library.Prefs.TwoColumns = twoColSw.IsOn;
            _library.SavePrefs();
            BroadcastReaderSettingsWithoutSelfRefresh();
            RenderPaperLivePreview(dark);
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("宽窗口下双栏显示", "左右并排显示两页", "col-2", twoColSw, dark: dark));

        var modeCombo = new ComboBox { Width = 160 };
        modeCombo.Items.Add(L10n.T("左右翻页"));
        modeCombo.Items.Add(L10n.T("上下滚动"));
        modeCombo.SelectedIndex = rs.Mode == LayoutMode.Scrolled ? 1 : 0;
        modeCombo.SelectionChanged += (_, _) =>
        {
            _library.SaveReaderSettings(_library.GetReaderSettings() with { Mode = modeCombo.SelectedIndex == 1 ? LayoutMode.Scrolled : LayoutMode.Paginated });
            BroadcastReaderSettingsWithoutSelfRefresh();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("翻页方式", "左右翻页更似纸书，上下滚动适合长文扫读", "col-scroll", modeCombo, dark: dark));

        var noAnimSw = new ToggleSwitch { IsOn = _library.Prefs.ImmersiveNoAnimation };
        noAnimSw.Toggled += (_, _) =>
        {
            _library.Prefs.ImmersiveNoAnimation = noAnimSw.IsOn;
            _library.SavePrefs();
            BroadcastReaderSettingsWithoutSelfRefresh();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("关闭翻页动画", "直接切换整页，无滑动过渡", "sparkle", noAnimSw, dark: dark));

        // 翻页音效（1:1 对齐 macOS ReadingSettings：开关 + 6 内置声学预设/自定义音效网格 + 音量滑块）
        var sfxCard = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12, 16, 12),
        };
        var sfxStack = new StackPanel { Spacing = 10 };
        var sfxHeader = new Grid { ColumnSpacing = 14 };
        sfxHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        sfxHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sfxHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var sfxIcon = YYIcons.Make("speaker", 16, ThemeHelper.Brush(p.T2));
        sfxIcon.VerticalAlignment = VerticalAlignment.Center;
        sfxHeader.Children.Add(sfxIcon);

        var sfxTextStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        sfxTextStack.Children.Add(new TextBlock
        {
            Text = L10n.T("翻页音效"),
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(p.T1),
        });
        sfxTextStack.Children.Add(new TextBlock
        {
            Text = L10n.T("左右翻页时轻声模拟翻纸声，朗读时自动静音"),
            FontSize = 12,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(sfxTextStack, 1);
        sfxHeader.Children.Add(sfxTextStack);

        var sfxDivider = new Border
        {
            Height = 1,
            Background = ThemeHelper.Brush(p.Line),
            Visibility = _library.Prefs.PageTurnSound ? Visibility.Visible : Visibility.Collapsed,
        };
        var sfxPickerHost = new Border
        {
            Visibility = _library.Prefs.PageTurnSound ? Visibility.Visible : Visibility.Collapsed,
            Child = Yuyue.UI.Reader.PageTurnSoundUi.BuildSoundPicker(
                _library,
                dark,
                showSubtitle: true,
                compact: false,
                showVolume: true,
                onChanged: () => BroadcastReaderSettingsWithoutSelfRefresh(),
                getXamlRoot: () => XamlRoot),
        };

        var sfxSw = new ToggleSwitch { IsOn = _library.Prefs.PageTurnSound, MinWidth = 0 };
        sfxSw.Toggled += (_, _) =>
        {
            _library.Prefs.PageTurnSound = sfxSw.IsOn;
            _library.SavePrefs();
            var vis = sfxSw.IsOn ? Visibility.Visible : Visibility.Collapsed;
            sfxDivider.Visibility = vis;
            sfxPickerHost.Visibility = vis;
            if (sfxSw.IsOn) _library.PageTurnSounds.PreviewSelection(_library.Prefs.PageTurnSoundStyle);
            BroadcastReaderSettingsWithoutSelfRefresh();
        };
        Grid.SetColumn(sfxSw, 2);
        sfxHeader.Children.Add(sfxSw);

        sfxStack.Children.Add(sfxHeader);
        sfxStack.Children.Add(sfxDivider);
        sfxStack.Children.Add(sfxPickerHost);
        sfxCard.Child = sfxStack;
        PageBodyStack.Children.Add(sfxCard);

        // Advanced typography defaults expander
        var advExp = new FluentSettingsExpander("更多排版", "段距、首行缩进两字、两端对齐与中文标点挤压", "sliders", dark: dark);

        var paraSeg = YYChrome.MakeSegmentedBar(
            [("0", "无"), ("0.5", "适中"), ("1.0", "宽")],
            rs.ParagraphSpacing switch { <= 0.1 => "0", >= 0.8 => "1.0", _ => "0.5" },
            val =>
            {
                double? ps = val switch { "0" => 0.0, "1.0" => 1.0, _ => 0.5 };
                _library.SaveReaderSettings(_library.GetReaderSettings() with { ParagraphSpacing = ps });
                BroadcastReaderSettingsWithoutSelfRefresh();
            },
            dark,
            fontSize: 12,
            height: 30);
        advExp.AddKeyValueRow("段距", "控制相邻段落之间的留白距离", paraSeg);

        var indentSeg = YYChrome.MakeSegmentedBar(
            [("auto", "跟书"), ("on", "缩进"), ("off", "不缩")],
            rs.Indent is null ? "auto" : (rs.Indent.Value ? "on" : "off"),
            val =>
            {
                bool? ind = val switch { "on" => true, "off" => false, _ => null };
                _library.SaveReaderSettings(_library.GetReaderSettings() with { Indent = ind });
                BroadcastReaderSettingsWithoutSelfRefresh();
            },
            dark,
            fontSize: 12,
            height: 30);
        advExp.AddKeyValueRow("首行缩进两字", "中文书籍通常首行缩进两字，可按个人偏好覆盖", indentSeg);

        var justSeg = YYChrome.MakeSegmentedBar(
            [("auto", "跟书"), ("on", "对齐"), ("off", "靠左")],
            rs.Justify is null ? "auto" : (rs.Justify.Value ? "on" : "off"),
            val =>
            {
                bool? jst = val switch { "on" => true, "off" => false, _ => null };
                _library.SaveReaderSettings(_library.GetReaderSettings() with { Justify = jst });
                BroadcastReaderSettingsWithoutSelfRefresh();
            },
            dark,
            fontSize: 12,
            height: 30);
        advExp.AddKeyValueRow("两端对齐", "控制左右两端对齐或自然左对齐", justSeg);

        var compSw = new ToggleSwitch { IsOn = rs.Compress };
        compSw.Toggled += (_, _) =>
        {
            _library.SaveReaderSettings(_library.GetReaderSettings() with { Compress = compSw.IsOn });
            BroadcastReaderSettingsWithoutSelfRefresh();
        };
        advExp.AddKeyValueRow("中文标点挤压", "收紧连续标点的留白", compSw);
        PageBodyStack.Children.Add(advExp);

        // Font Shelf + Local System Fonts
        PageBodyStack.Children.Add(MakeSectionHeader("字样册", dark));

        int installedPacks = _library.Fonts.Catalog.Count(pk => _library.Fonts.IsPackInstalled(pk.Id));
        var fontShelfExp = new FluentSettingsExpander(
            $"可以下载（已安装 {installedPacks} / {_library.Fonts.Catalog.Count}）",
            "下载霞鹜文楷、思源宋体、思源黑体等开源字体",
            "char-book",
            dark: dark);

        foreach (var pack in _library.Fonts.Catalog)
        {
            var pkCap = pack;
            bool installed = _library.Fonts.IsPackInstalled(pkCap.Id);
            bool downloading = _library.Fonts.IsDownloading(pkCap.Id);
            ulong totalBytes = 0;
            foreach (var f in pkCap.Files) totalBytes += f.Bytes;
            double mb = Math.Max(0.1, totalBytes / (1024.0 * 1024.0));
            bool isCur = (string.Equals(pkCap.Id, "wenkai", StringComparison.OrdinalIgnoreCase) && rs.Font == Font.WenKai && string.IsNullOrEmpty(rs.FontFace))
                || string.Equals(rs.FontFace, pkCap.Id, StringComparison.OrdinalIgnoreCase);

            var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (downloading)
            {
                int pct = (int)Math.Round(_library.Fonts.GetProgress(pkCap.Id) * 100);
                acts.Children.Add(new TextBlock
                {
                    Text = $"{L10n.T("下载中")} {pct}%",
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(tc.Accent),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var cancelBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Height = 28,
                    MinHeight = 28,
                    Content = L10n.T("取消"),
                };
                cancelBtn.Click += (_, _) => _library.Fonts.CancelDownload(pkCap.Id);
                acts.Children.Add(cancelBtn);
            }
            else if (installed)
            {
                var useBtn = new Button
                {
                    Style = (Style)Application.Current.Resources[isCur ? "YYFluentSoftAccentButtonStyle" : "YYFluentButtonStyle"],
                    Height = 28,
                    MinHeight = 28,
                    Content = L10n.T(isCur ? "正在用" : "选用"),
                };
                useBtn.Click += (_, _) =>
                {
                    if (string.Equals(pkCap.Id, "wenkai", StringComparison.OrdinalIgnoreCase))
                        _library.SaveReaderSettings(_library.GetReaderSettings() with { Font = Font.WenKai, FontFace = null });
                    else
                    {
                        Font fb = pkCap.Kind switch { FontKind.Hei => Font.Sans, FontKind.Kai or FontKind.Hand => Font.Kai, _ => Font.Serif };
                        _library.SaveReaderSettings(_library.GetReaderSettings() with { Font = fb, FontFace = pkCap.Id });
                    }
                    BroadcastReaderSettingsWithoutSelfRefresh();
                    RenderActivePage();
                };
                var delBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Height = 28,
                    MinHeight = 28,
                    Content = L10n.T("删除"),
                    Foreground = ThemeHelper.Brush(p.Danger),
                };
                delBtn.Click += (_, _) =>
                {
                    _library.Fonts.DeletePack(pkCap.Id);
                    var curRs = _library.GetReaderSettings();
                    if ((string.Equals(pkCap.Id, "wenkai", StringComparison.OrdinalIgnoreCase) && curRs.Font == Font.WenKai)
                        || string.Equals(curRs.FontFace, pkCap.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        _library.SaveReaderSettings(curRs with { Font = null, FontFace = null });
                        BroadcastReaderSettingsWithoutSelfRefresh();
                    }
                    RenderActivePage();
                };
                acts.Children.Add(useBtn);
                acts.Children.Add(delBtn);
            }
            else
            {
                var dlBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"],
                    Height = 28,
                    MinHeight = 28,
                    Content = $"{L10n.T("下载")} ({mb:0.#} MB)",
                };
                dlBtn.Click += (_, _) =>
                {
                    _ = _library.Fonts.DownloadPackAsync(pkCap.Id);
                    _ownerWindow.ShowToast($"正在下载开源字体：{pkCap.Name}…");
                };
                acts.Children.Add(dlBtn);
            }

            string packDesc = installed ? $"已安装 · {mb:0.#} MB" : $"开源免费 · {mb:0.#} MB";
            fontShelfExp.AddKeyValueRow(pkCap.Name, packDesc, acts);
        }
        PageBodyStack.Children.Add(fontShelfExp);

        // Windows Local System CJK Font Picker
        string curLocalLabel = !string.IsNullOrWhiteSpace(rs.FontFace) && rs.FontFace.StartsWith("local:", StringComparison.OrdinalIgnoreCase)
            ? $"当前选用本机字体：{rs.FontFace[6..].Trim()}"
            : "挑选 Windows 已安装的宋体、楷体、仿宋、思源等中文字体作为正文";
        var pickSysFontBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("更多字体…") };
        pickSysFontBtn.Click += async (_, _) => await ShowSystemCjkFontPickerDialogAsync(dark);
        PageBodyStack.Children.Add(new FluentSettingsCard("这台电脑的字体", curLocalLabel, "char-book", pickSysFontBtn, dark: dark));

        // Custom imported fonts list
        var customFonts = _library.Fonts.GetCustomFontFiles();
        if (customFonts.Count > 0)
        {
            PageBodyStack.Children.Add(MakeSectionHeader($"你添加的 ({customFonts.Count})", dark));
            foreach (var fn in customFonts)
            {
                string fnCap = fn;
                string faceKey = $"file:{fnCap}";
                bool isCur = string.Equals(rs.FontFace, faceKey, StringComparison.OrdinalIgnoreCase);
                var rowBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var useBtn = new Button
                {
                    Style = (Style)Application.Current.Resources[isCur ? "YYFluentSoftAccentButtonStyle" : "YYFluentButtonStyle"],
                    Height = 28,
                    MinHeight = 28,
                    Content = L10n.T(isCur ? "正在用" : "选用"),
                };
                useBtn.Click += (_, _) =>
                {
                    _library.SaveReaderSettings(_library.GetReaderSettings() with { FontFace = faceKey });
                    BroadcastReaderSettingsWithoutSelfRefresh();
                    RenderActivePage();
                };
                var rmBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                    Height = 28,
                    MinHeight = 28,
                    Content = L10n.T("移除"),
                    Foreground = ThemeHelper.Brush(p.Danger),
                };
                rmBtn.Click += (_, _) =>
                {
                    _library.Fonts.DeleteCustomFont(fnCap);
                    var curRs = _library.GetReaderSettings();
                    if (string.Equals(curRs.FontFace, faceKey, StringComparison.OrdinalIgnoreCase))
                    {
                        _library.SaveReaderSettings(curRs with { FontFace = null });
                        BroadcastReaderSettingsWithoutSelfRefresh();
                    }
                    RenderActivePage();
                };
                rowBtns.Children.Add(useBtn);
                rowBtns.Children.Add(rmBtn);
                PageBodyStack.Children.Add(new FluentSettingsCard(_library.Fonts.GetFontFileDisplayName(fnCap), fnCap, "char-book", rowBtns, dark: dark));
            }
        }
    }

    private void BuildForeignPage(bool dark)
    {
        PageBodyStack.Children.Add(MakeSectionHeader("查词", dark, first: true));

        var lookupSw = new ToggleSwitch { IsOn = _library.Prefs.ForeignLookup };
        lookupSw.Toggled += (_, _) => { _library.Prefs.ForeignLookup = lookupSw.IsOn; _library.SavePrefs(); BroadcastForeignPrefsWithoutSelfRefresh(); };
        PageBodyStack.Children.Add(new FluentSettingsCard("点词查词", "内置 70 万词条离线简明英汉词典（ECDICT），断网可用", "a-wen", lookupSw, dark: dark));

        var speakLookupSw = new ToggleSwitch { IsOn = _library.Prefs.ForeignSpeakOnLookup };
        speakLookupSw.Toggled += (_, _) => { _library.Prefs.ForeignSpeakOnLookup = speakLookupSw.IsOn; _library.SavePrefs(); BroadcastForeignPrefsWithoutSelfRefresh(); };
        PageBodyStack.Children.Add(new FluentSettingsCard("查词自动发音", "点击单词查词时自动朗读一遍标准读音", "wave", speakLookupSw, dark: dark));

        var accentSeg = YYChrome.MakeSegmentedBar(
            [("uk", "英音 (UK)"), ("us", "美音 (US)")],
            string.Equals(_library.Prefs.ForeignAccent, "us", StringComparison.OrdinalIgnoreCase) ? "us" : "uk",
            val =>
            {
                _library.Prefs.ForeignAccent = val;
                _library.SavePrefs();
                BroadcastForeignPrefsWithoutSelfRefresh();
            },
            dark,
            fontSize: 12,
            height: 30);
        PageBodyStack.Children.Add(new FluentSettingsCard("英语发音口音", "用于查词读音与生词本发声", "headphones", accentSeg, dark: dark));

        PageBodyStack.Children.Add(MakeSectionHeader("难词注释", dark));

        var glossSw = new ToggleSwitch { IsOn = _library.Prefs.ForeignGlossEnabled };
        glossSw.Toggled += (_, _) => { _library.Prefs.ForeignGlossEnabled = glossSw.IsOn; _library.SavePrefs(); BroadcastForeignPrefsWithoutSelfRefresh(); };
        PageBodyStack.Children.Add(new FluentSettingsCard("难词行间释义", "阅读英文书时，在较难词汇上方标注简短中文词义", "translate", glossSw, dark: dark));

        var levelCombo = new ComboBox { Width = 140 };
        (byte Val, string Lbl)[] levels = [(2, "四级"), (3, "六级"), (4, "考研"), (5, "雅思"), (6, "GRE")];
        foreach (var (_, l) in levels) levelCombo.Items.Add(L10n.T(l));
        levelCombo.SelectedIndex = Math.Max(0, Array.FindIndex(levels, x => x.Val == _library.Prefs.GlossLevel));
        levelCombo.SelectionChanged += (_, _) =>
        {
            if (levelCombo.SelectedIndex >= 0)
            {
                _library.Prefs.GlossLevel = levels[levelCombo.SelectedIndex].Val;
                _library.SavePrefs();
                BroadcastForeignPrefsWithoutSelfRefresh();
            }
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("词汇难度", "等级越高，仅标注更生僻的词", "filter", levelCombo, dark: dark));

        var glossVocabSw = new ToggleSwitch { IsOn = _library.Prefs.GlossVocabOnly };
        glossVocabSw.Toggled += (_, _) => { _library.Prefs.GlossVocabOnly = glossVocabSw.IsOn; _library.SavePrefs(); BroadcastForeignPrefsWithoutSelfRefresh(); };
        PageBodyStack.Children.Add(new FluentSettingsCard("仅标注生词本中的词", "减少页面干扰，只为收藏过的生词显示行间提示", "filter", glossVocabSw, dark: dark));

        PageBodyStack.Children.Add(MakeSectionHeader("生词", dark));

        var markVocabSw = new ToggleSwitch { IsOn = _library.Prefs.ForeignMarkVocab };
        markVocabSw.Toggled += (_, _) => { _library.Prefs.ForeignMarkVocab = markVocabSw.IsOn; _library.SavePrefs(); BroadcastForeignPrefsWithoutSelfRefresh(); };
        PageBodyStack.Children.Add(new FluentSettingsCard("在书中标记生词", "「在学」生词在所有书中以虚线标出，标为「已掌握」后不再标记", "bookmark", markVocabSw, dark: dark));

        PageBodyStack.Children.Add(MakeSectionHeader("MDict 词典", dark));
        var p = ThemeHelper.Palette(dark);
        var impDictBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("选择文件…") };
        impDictBtn.Click += async (_, _) =>
        {
            string? path = await NativePickerHelper.PickSingleFileAsync(_ownerWindow, [".mdx"]);
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    _library.ImportDictionary(path);
                    RenderActivePage();
                    _ownerWindow.ShowToast("已导入 MDict 词典");
                }
                catch (Exception ex) { _ownerWindow.ShowToast($"导入词典失败：{ex.Message}"); }
            }
        };
        var dicts = _library.GetDictionaries();
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "导入词典（.mdx）",
            dicts.Count == 0 ? "导入牛津、朗文等 .mdx 词典后优先展示完整词条" : $"已导入 {dicts.Count} 本 · 按列表顺序优先查词，可调整先后或移除",
            "char-book",
            impDictBtn,
            dark: dark));

        for (int di = 0; di < dicts.Count; di++)
        {
            var d = dicts[di];
            int idxCap = di;
            var dActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (di > 0)
            {
                var upBtn = new Button { Style = (Style)Application.Current.Resources["YYToolbarIconButtonStyle"], Content = "↑" };
                ToolTipService.SetToolTip(upBtn, L10n.T("上移优先级"));
                upBtn.Click += (_, _) =>
                {
                    var ids = dicts.Select(x => x.Id).ToList();
                    (ids[idxCap - 1], ids[idxCap]) = (ids[idxCap], ids[idxCap - 1]);
                    _library.OrderDictionaries([.. ids]);
                    RenderActivePage();
                };
                dActs.Children.Add(upBtn);
            }
            if (di < dicts.Count - 1)
            {
                var dnBtn = new Button { Style = (Style)Application.Current.Resources["YYToolbarIconButtonStyle"], Content = "↓" };
                ToolTipService.SetToolTip(dnBtn, L10n.T("下移优先级"));
                dnBtn.Click += (_, _) =>
                {
                    var ids = dicts.Select(x => x.Id).ToList();
                    (ids[idxCap + 1], ids[idxCap]) = (ids[idxCap], ids[idxCap + 1]);
                    _library.OrderDictionaries([.. ids]);
                    RenderActivePage();
                };
                dActs.Children.Add(dnBtn);
            }
            var enSw = new ToggleSwitch { IsOn = d.Enabled };
            enSw.Toggled += (_, _) => _library.SetDictionaryEnabled(d.Id, enSw.IsOn);
            var delBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Height = 28,
                MinHeight = 28,
                Content = L10n.T("移除"),
                Foreground = ThemeHelper.Brush(p.Danger),
            };
            delBtn.Click += (_, _) => { _library.RemoveDictionary(d.Id); RenderActivePage(); };
            dActs.Children.Add(enSw);
            dActs.Children.Add(delBtn);
            PageBodyStack.Children.Add(new FluentSettingsCard(d.Title, d.Enabled ? "已启用" : "已停用", "book", dActs, dark: dark));
        }
    }

    private void UpdateVoiceModelCards()
    {
        if (_kokoroCard is not null && _kokoroBtn is not null)
        {
            bool kokoroOk = _library.Kokoro.IsInstalled;
            bool kokoroDl = _library.Kokoro.State == KokoroStateKind.Downloading;
            bool kokoroFail = _library.Kokoro.State == KokoroStateKind.Failed;
            var desiredKokoroStyle = (Style)Application.Current.Resources[(kokoroOk || kokoroDl) ? "YYFluentButtonStyle" : "YYFluentAccentButtonStyle"];
            if (!ReferenceEquals(_kokoroBtn.Style, desiredKokoroStyle))
                _kokoroBtn.Style = desiredKokoroStyle;
            _kokoroBtn.Content = kokoroOk
                ? L10n.T("已就绪 (16 款离线音色)")
                : kokoroDl
                    ? $"{L10n.T("下载中")} {(int)Math.Round(_library.Kokoro.DownloadProgress * 100)}%"
                    : kokoroFail
                        ? L10n.T("重试下载 (125 MB)")
                        : L10n.T("下载离线模型 (125 MB)");
            _kokoroBtn.IsEnabled = !kokoroOk && !kokoroDl;
            if (_kokoroCancelBtn is not null)
                _kokoroCancelBtn.Visibility = kokoroDl ? Visibility.Visible : Visibility.Collapsed;
            _kokoroCard.Description = kokoroOk
                ? "已完成安装，12 款中文 + 4 款英文高自然度音色断网直用"
                : (kokoroDl || kokoroFail) && !string.IsNullOrWhiteSpace(_library.Kokoro.StatusMessage)
                    ? _library.Kokoro.StatusMessage
                    : "免费、离线、接近真人的本地神经网络朗读模型";
        }

        if (_liveVoiceCard is not null && _liveVoiceBtn is not null)
        {
            bool liveOk = _library.LiveVoice.IsReady;
            bool liveDl = _library.LiveVoice.State == LiveVoiceStateKind.Downloading;
            bool liveFail = _library.LiveVoice.State == LiveVoiceStateKind.Failed;
            var desiredLiveStyle = (Style)Application.Current.Resources[(liveOk || liveDl) ? "YYFluentButtonStyle" : "YYFluentAccentButtonStyle"];
            if (!ReferenceEquals(_liveVoiceBtn.Style, desiredLiveStyle))
                _liveVoiceBtn.Style = desiredLiveStyle;
            _liveVoiceBtn.Content = liveOk
                ? L10n.T("已就绪")
                : liveDl
                    ? $"{L10n.T("下载中")} {(int)Math.Round(_library.LiveVoice.DownloadProgress * 100)}%"
                    : liveFail
                        ? L10n.T("重试下载 (163 MB)")
                        : L10n.T("下载离线模型 (163 MB)");
            _liveVoiceBtn.IsEnabled = !liveOk && !liveDl;
            if (_liveVoiceCancelBtn is not null)
                _liveVoiceCancelBtn.Visibility = liveDl ? Visibility.Visible : Visibility.Collapsed;
            _liveVoiceCard.Description = liveOk
                ? "已完成安装，支持本地离线声音克隆合成与内置情感音色包"
                : (liveDl || liveFail) && !string.IsNullOrWhiteSpace(_library.LiveVoice.StatusMessage)
                    ? _library.LiveVoice.StatusMessage
                    : "免费、离线驱动声音克隆与高表现力情感朗读（模型 + 音色包共约 163 MB）";
        }

        if (_storageModelsCard is not null)
        {
            string kokoroStatus = _library.Kokoro.IsInstalled
                ? "已安装 (125 MB)"
                : _library.Kokoro.State == KokoroStateKind.Downloading
                    ? $"下载中 {(int)Math.Round(_library.Kokoro.DownloadProgress * 100)}%"
                    : "未下载";
            string liveStatus = _library.LiveVoice.IsReady
                ? "已安装 (163 MB)"
                : _library.LiveVoice.State == LiveVoiceStateKind.Downloading
                    ? $"下载中 {(int)Math.Round(_library.LiveVoice.DownloadProgress * 100)}%"
                    : "未下载";
            _storageModelsCard.Description = $"语阅离线语音：{kokoroStatus} · 角色与克隆：{liveStatus} · 音效库：{(SoundLibraryService.IsInstalled(_library.DataDir) ? "已安装" : "未安装")}";
        }
    }

    private string? _settingsPreviewingLang;
    private int _settingsPreviewSeq;

    private (string DisplayLabel, string? BrandId, string FallbackIcon, string EffectiveVoiceId, string? ActiveSourceKey) ResolveBookVoiceDisplay(string? prefVoiceId, bool isEnglish)
    {
        string raw = (prefVoiceId ?? "").Trim();
        string norm = DramaSynthService.NormalizeVoiceId(raw) ?? raw;
        var allOpts = AudioPlayer.GetAllVoiceOptions(_library);

        if (!string.IsNullOrEmpty(norm))
        {
            var hit = allOpts.FirstOrDefault(v =>
                string.Equals(v.Id, raw, StringComparison.OrdinalIgnoreCase)
                || string.Equals(DramaSynthService.NormalizeVoiceId(v.Id) ?? v.Id, norm, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                string? brand = hit.Kind == SpeechVoiceKind.Cloud && CloudVoiceService.TryParseFullId(hit.Id, out var cp, out _)
                    ? CloudVoiceEntry.ProviderKey(cp)
                    : null;
                string srcKey = hit.Kind is SpeechVoiceKind.Kokoro or SpeechVoiceKind.BuiltinCard or SpeechVoiceKind.BookCard
                    ? "kokoro"
                    : hit.Kind == SpeechVoiceKind.ClonedCard
                        ? "cloned"
                        : brand is not null ? $"cloud.{brand}" : "system";
                string glyph = hit.Kind == SpeechVoiceKind.ClonedCard ? "mic" : (brand is not null ? "cloud" : "wave");
                return (hit.Label.TrimStart('✦', '☁', ' '), brand, glyph, hit.Id, srcKey);
            }
        }

        // Auto fallback resolution matching AudioPlayer.SetVoice("") exact priority: Kokoro -> LiveVoice -> Free -> Edge -> System
        string lang = isEnglish ? "en" : "zh-Hans";
        if (_library.Kokoro.IsInstalled && _library.Kokoro.DefaultNarrator(lang) is { } kDef)
            return ($"{L10n.T("自动")} · {kDef.Name}（Kokoro 离线）", null, "wave", kDef.Id, "kokoro");
        if (_library.LiveVoice.IsReady && DramaSynthService.DefaultNarratorCard(_library, lang) is { } liveDef)
            return ($"{L10n.T("自动")} · {liveDef.Name}（{L10n.T("离线人声")}）", null, "wave", liveDef.Id, "kokoro");
        if (_library.CloudVoices.IsConfigured(CloudTtsProvider.Free))
        {
            bool preferUk = isEnglish && string.Equals(_library.Prefs.ForeignAccent, "uk", StringComparison.OrdinalIgnoreCase);
            string defFree = isEnglish ? (preferUk ? "cloud.free.en_uk" : "cloud.free.en_us") : "cloud.free.zh_baidu";
            string defName = isEnglish ? (preferUk ? "Clara" : "Emma") : "知声";
            return ($"{L10n.T("自动")} · {defName}（{L10n.T("在线自然语音")}）", null, "wave", defFree, "cloud.free");
        }
        if (_library.CloudVoices.IsConfigured(CloudTtsProvider.Edge))
        {
            string defEdge = isEnglish ? "cloud.edge.en-US-JennyNeural" : "cloud.edge.zh-CN-XiaoxiaoNeural";
            string defName = isEnglish ? "Jenny (US Natural)" : "晓晓 · 温暖自然女声";
            return ($"{L10n.T("自动")} · {defName}（{L10n.T("微软 Edge")}）", "edge", "cloud", defEdge, "cloud.edge");
        }
        return ($"{L10n.T("自动")} · {L10n.T("系统语音")}", null, "headphones", "", "system");
    }

    private void BuildListeningPage(bool dark)
    {
        var pVoice = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        // 1. Default Book Voices & Speech Rate (Primary actions right above the fold!)
        PageBodyStack.Children.Add(MakeSectionHeader("默认声音与语速", dark, first: true));

        var zhInfo = ResolveBookVoiceDisplay(_library.Prefs.VoiceId, isEnglish: false);
        var enInfo = ResolveBookVoiceDisplay(_library.Prefs.VoiceIdEn, isEnglish: true);

        FluentSettingsCard MakeDefaultBookVoiceCard(
            string cardTitle,
            bool isEnglish,
            (string DisplayLabel, string? BrandId, string FallbackIcon, string EffectiveVoiceId, string? ActiveSourceKey) info,
            string? curPrefId,
            Action<string> onSaveVoice)
        {
            string langTag = isEnglish ? "en" : "zh";
            bool isPrev = _settingsPreviewingLang == langTag;

            var rowActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

            var prevInner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            prevInner.Children.Add(YYIcons.Make(isPrev ? "stop" : "play", 10, ThemeHelper.Brush(isPrev ? Microsoft.UI.Colors.White : pVoice.T1), filled: true));
            prevInner.Children.Add(L10n.Skip(new TextBlock
            {
                Text = L10n.T(isPrev ? "停止" : "试听"),
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(isPrev ? Microsoft.UI.Colors.White : pVoice.T1),
                VerticalAlignment = VerticalAlignment.Center,
            }));
            var prevBtn = new Button
            {
                Style = (Style)Application.Current.Resources[isPrev ? "YYFluentAccentButtonStyle" : "YYFluentButtonStyle"],
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(10, 0, 10, 0),
                Content = prevInner,
            };
            prevBtn.Click += async (_, _) =>
            {
                if (_settingsPreviewingLang == langTag)
                {
                    _settingsPreviewSeq++;
                    _settingsPreviewingLang = null;
                    AudioPlayer.StopActivePreview();
                    RenderActivePage();
                    return;
                }
                int mySeq = ++_settingsPreviewSeq;
                _settingsPreviewingLang = langTag;
                RenderActivePage();
                try
                {
                    string sample = Yuyue.UI.Reader.ReaderPaneView.PickDailyAuditionQuote(isEnglish);
                    await AudioPlayer.PreviewVoiceAsync(_library, info.EffectiveVoiceId, _library.Prefs.SpeechRate, sample);
                }
                catch { }
                finally
                {
                    if (!_disposed && _settingsPreviewSeq == mySeq && _settingsPreviewingLang == langTag)
                    {
                        _settingsPreviewingLang = null;
                        if (_activePage == "listening") RenderActivePage();
                    }
                }
            };
            rowActs.Children.Add(prevBtn);

            var changeBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                Height = 28,
                MinHeight = 28,
                Content = L10n.T("换声音…"),
            };
            async Task OpenPicker()
            {
                if (RootGrid.XamlRoot is not { } xr) return;
                _settingsPreviewSeq++;
                _settingsPreviewingLang = null;
                AudioPlayer.StopActivePreview();
                await Yuyue.UI.Reader.ReaderPaneView.ShowVoicePickerDialogAsync(
                    xr,
                    _library,
                    L10n.T(isEnglish ? "英文书声音" : "中文书声音"),
                    curPrefId,
                    vid =>
                    {
                        onSaveVoice(DramaSynthService.NormalizeVoiceId(vid) ?? vid);
                        _library.SavePrefs();
                        BroadcastVoiceWithoutSelfRefresh();
                        RenderActivePage();
                    },
                    darkOverride: dark,
                    languageFilter: langTag);
            }
            changeBtn.Click += async (_, _) => await OpenPicker();
            rowActs.Children.Add(changeBtn);

            return new FluentSettingsCard(
                cardTitle,
                info.DisplayLabel,
                info.FallbackIcon,
                rowActs,
                dark: dark,
                customIcon: ThemeHelper.CreateBrandLogoImage(info.BrandId, dark, 20));
        }

        PageBodyStack.Children.Add(MakeDefaultBookVoiceCard(
            "中文书",
            isEnglish: false,
            zhInfo,
            _library.Prefs.VoiceId,
            vid => _library.Prefs.VoiceId = vid));

        PageBodyStack.Children.Add(MakeDefaultBookVoiceCard(
            "英文书",
            isEnglish: true,
            enInfo,
            _library.Prefs.VoiceIdEn,
            vid => _library.Prefs.VoiceIdEn = vid));

        // Speech Rate with Slider + Quick Speed Preset Flyout
        var rateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var rateSlider = new Slider { Width = 160, Minimum = 0.7, Maximum = 2.0, StepFrequency = 0.05, Value = Math.Clamp(_library.Prefs.SpeechRate, 0.7, 2.0) };
        var rateMenuBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Height = 28,
            MinHeight = 28,
            MinWidth = 66,
            Content = $"{_library.Prefs.SpeechRate:0.##}× ▾",
        };
        var rateCard = new FluentSettingsCard("语速", "读书窗口里调的也是这一个", "sliders", rateRow, dark: dark);
        void RebuildRateMenu()
        {
            var menu = new MenuFlyout();
            foreach (double r in new[] { 0.8, 1.0, 1.15, 1.25, 1.5, 1.75, 2.0 })
            {
                double rCap = r;
                var mi = new ToggleMenuFlyoutItem
                {
                    Text = $"{rCap:0.##}×",
                    IsChecked = Math.Abs(rCap - _library.Prefs.SpeechRate) < 0.04,
                };
                mi.Click += (_, _) =>
                {
                    _library.Prefs.SpeechRate = rCap;
                    _library.SavePrefs();
                    rateSlider.Value = rCap;
                    rateMenuBtn.Content = $"{rCap:0.##}× ▾";
                    BroadcastVoiceWithoutSelfRefresh();
                    RebuildRateMenu();
                };
                menu.Items.Add(mi);
            }
            rateMenuBtn.Flyout = menu;
        }
        RebuildRateMenu();
        rateSlider.ValueChanged += (_, args) =>
        {
            double nv = Math.Round(args.NewValue, 2);
            if (Math.Abs(nv - _library.Prefs.SpeechRate) < 0.005) return;
            _library.Prefs.SpeechRate = nv;
            _library.SavePrefs();
            rateMenuBtn.Content = $"{nv:0.##}× ▾";
            BroadcastVoiceWithoutSelfRefresh();
            RebuildRateMenu();
        };
        rateRow.Children.Add(rateSlider);
        rateRow.Children.Add(rateMenuBtn);
        PageBodyStack.Children.Add(rateCard);

        // 2. Unified Voice Sources Section (Offline Kokoro + Active Cloud/Local + Add Catalog)
        var activeSources = CloudVoiceService.AllProviders
            .Where(pr => _library.CloudVoices.IsConfigured(pr) || (CloudVoiceService.IsLocalProvider(pr) && LocalVoiceProbe.Shared.IsLive(pr)))
            .ToList();

        var voiceHeadRow = new Grid { Margin = new Thickness(2, 12, 0, 2) };
        voiceHeadRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        voiceHeadRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        voiceHeadRow.Children.Add(new TextBlock
        {
            Text = $"{L10n.T("声音来源")} ({activeSources.Count + 1})",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(pVoice.T2),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var headRightActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        bool anyFreeHidden = !_library.CloudVoices.IsConfigured(CloudTtsProvider.Edge)
            || !_library.CloudVoices.IsConfigured(CloudTtsProvider.Free);
        if (anyFreeHidden)
        {
            var restoreFreeBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T("恢复在线声音"),
                FontSize = 11.5,
                MinHeight = 24,
                Padding = new Thickness(8, 0, 8, 0),
                Foreground = ThemeHelper.Brush(tc.Accent),
            };
            restoreFreeBtn.Click += (_, _) =>
            {
                _library.Secrets.Set("tts.edge.dismissed", "");
                _library.Secrets.Set("tts.free.dismissed", "");
                _library.SavePrefs();
                BroadcastVoiceWithoutSelfRefresh();
                ShowSettingsSavedToast("已恢复微软 Edge 与在线自然语音");
                RenderActivePage();
            };
            headRightActs.Children.Add(restoreFreeBtn);
        }

        var scanVoiceBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Content = L10n.T("扫描本机声音"),
            FontSize = 11.5,
            MinHeight = 24,
            Padding = new Thickness(8, 0, 8, 0),
        };
        scanVoiceBtn.Click += async (_, _) =>
        {
            scanVoiceBtn.IsEnabled = false;
            try
            {
                await LocalVoiceProbe.Shared.ScanAllAsync(_library);
                if (_disposed) return;
                int found = CloudVoiceService.AllProviders.Count(pr => CloudVoiceService.IsLocalProvider(pr) && LocalVoiceProbe.Shared.IsLive(pr));
                _ownerWindow.ShowToast(found > 0 ? $"已探测到 {found} 个本机声音来源" : "未探测到运行中的本机声音来源（支持 VoiceStudio / Voicebox / Speaches / mlx-audio）");
                RenderActivePage();
            }
            finally
            {
                scanVoiceBtn.IsEnabled = true;
            }
        };
        headRightActs.Children.Add(scanVoiceBtn);
        Grid.SetColumn(headRightActs, 1);
        voiceHeadRow.Children.Add(headRightActs);
        PageBodyStack.Children.Add(voiceHeadRow);

        string FormatUsedByBooks(string sourceKey)
        {
            var used = new List<string>();
            if (zhInfo.ActiveSourceKey == sourceKey) used.Add(L10n.T("中文书"));
            if (enInfo.ActiveSourceKey == sourceKey) used.Add(L10n.T("英文书"));
            return used.Count > 0 ? $" · {L10n.T("当前用于")} {string.Join(" / ", used)}" : "";
        }

        async Task OpenSourceAuditionAsync(string sourceKey, string sourceTitle)
        {
            if (RootGrid.XamlRoot is not { } xr) return;
            await Yuyue.UI.Reader.ReaderPaneView.ShowVoicePickerDialogAsync(
                xr,
                _library,
                $"{sourceTitle} · {L10n.T("试听音色")}",
                _library.Prefs.VoiceId,
                vid =>
                {
                    string norm = DramaSynthService.NormalizeVoiceId(vid) ?? vid;
                    var hit = AudioPlayer.GetAllVoiceOptions(_library).FirstOrDefault(x => string.Equals(x.Id, vid, StringComparison.OrdinalIgnoreCase));
                    bool isEnVoice = (hit?.Language ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase);
                    if (isEnVoice) _library.Prefs.VoiceIdEn = norm;
                    else _library.Prefs.VoiceId = norm;
                    _library.SavePrefs();
                    BroadcastVoiceWithoutSelfRefresh();
                    ShowSettingsSavedToast(isEnVoice ? "已设为英文书默认声音" : "已设为中文书默认声音");
                    RenderActivePage();
                },
                darkOverride: dark,
                languageFilter: "all",
                initialSourceFilter: sourceKey);
        }

        // Kokoro offline neural voice source card
        var kokoroActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var kokoroBtn = new Button();
        kokoroBtn.Click += (_, _) =>
        {
            _ = _library.Kokoro.DownloadAsync();
            _ownerWindow.ShowToast("正在后台下载语阅离线语音…");
            UpdateVoiceModelCards();
        };
        var kokoroCancelBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Content = L10n.T("取消"),
            Visibility = Visibility.Collapsed,
        };
        kokoroCancelBtn.Click += (_, _) =>
        {
            _library.Kokoro.CancelDownload();
            UpdateVoiceModelCards();
        };
        kokoroActs.Children.Add(kokoroBtn);
        kokoroActs.Children.Add(kokoroCancelBtn);
        var kokoroCard = new FluentSettingsCard(
            "语阅离线语音（Kokoro）",
            "免费、离线、接近真人的本地神经网络朗读模型",
            "wave",
            kokoroActs,
            onClick: _library.Kokoro.IsInstalled ? () => _ = OpenSourceAuditionAsync("kokoro", L10n.T("语阅离线语音")) : null,
            dark: dark);
        _kokoroCard = kokoroCard;
        _kokoroBtn = kokoroBtn;
        _kokoroCancelBtn = kokoroCancelBtn;
        PageBodyStack.Children.Add(kokoroCard);

        // Active Cloud & Local Voice Sources
        foreach (var prov in activeSources)
        {
            var prCap = prov;
            string pKey = CloudVoiceEntry.ProviderKey(prCap);
            string sKey = $"cloud.{pKey}";
            string pName = CloudVoiceService.ProviderDisplayName(prCap, _library.Prefs);
            bool isCfg = _library.CloudVoices.IsConfigured(prCap);
            bool isLocal = CloudVoiceService.IsLocalProvider(prCap);
            bool isFree = CloudVoiceService.IsFreeOnline(prCap);
            int voiceCnt = _library.CloudVoices.GetVoices(prCap).Count;
            string badge = CloudVoiceService.ProviderBadge(prCap) is { Length: > 0 } b ? $" · {L10n.T(b)}" : "";
            string usedSuffix = FormatUsedByBooks(sKey);
            string sub = isCfg
                ? $"{L10n.T("已就绪")} · {voiceCnt} {L10n.T("个可用音色")}{badge}{usedSuffix}"
                : $"{L10n.T("本机探测运行中")}{badge}{usedSuffix}";

            var rowActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var listenBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                Height = 28,
                MinHeight = 28,
                Content = L10n.T("试听音色"),
            };
            listenBtn.Click += async (_, _) => await OpenSourceAuditionAsync(sKey, pName);
            rowActs.Children.Add(listenBtn);

            var manageBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Height = 28,
                MinHeight = 28,
                Content = L10n.T("管理…"),
            };
            manageBtn.Click += async (_, _) => await ShowVoiceProviderConfigDialogAsync(prCap, dark);
            rowActs.Children.Add(manageBtn);

            var disconnectBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Height = 28,
                MinHeight = 28,
                Content = L10n.T(isFree ? "隐藏" : "断开"),
                Foreground = ThemeHelper.Brush(pVoice.T3),
            };
            disconnectBtn.Click += (_, _) =>
            {
                DisconnectVoiceProvider(prCap);
                ShowSettingsSavedToast(isFree
                    ? $"已隐藏“{pName}”，随时可在添加列表重新开启"
                    : $"已断开“{pName}”");
                RenderActivePage();
            };
            rowActs.Children.Add(disconnectBtn);

            string fallbackIcon = isLocal ? "server" : (isFree ? "wave" : "cloud");
            PageBodyStack.Children.Add(new FluentSettingsCard(
                pName,
                sub,
                fallbackIcon,
                rowActs,
                dark: dark,
                customIcon: ThemeHelper.CreateBrandLogoImage(pKey, dark, 20)));
        }

        // Single scalable trigger card opening the searchable, grouped 3-column Voice Source Catalog
        var voiceCatalogHint = L10n.Skip(new TextBlock
        {
            Text = $"{CloudVoiceService.AllProviders.Length}",
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(pVoice.T3),
            VerticalAlignment = VerticalAlignment.Center,
        });
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "添加声音来源…",
            "按分类搜索微软 Edge、火山豆包、Azure、硅基流动、阿里百炼、MiniMax、OpenAI、本机与自定义接口",
            "plus",
            voiceCatalogHint,
            onClick: () => _ = ShowVoiceSourceCatalogDialogAsync(dark),
            dark: dark));

        // 3. Clones & Drama Cast Section
        PageBodyStack.Children.Add(MakeSectionHeader("克隆与演播", dark));

        var liveActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var liveBtn = new Button();
        liveBtn.Click += (_, _) =>
        {
            _ = _library.LiveVoice.DownloadAsync();
            _ownerWindow.ShowToast("正在后台下载角色与克隆语音包…");
            UpdateVoiceModelCards();
        };
        var liveCancelBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Content = L10n.T("取消"),
            Visibility = Visibility.Collapsed,
        };
        liveCancelBtn.Click += (_, _) =>
        {
            _library.LiveVoice.CancelDownload();
            UpdateVoiceModelCards();
        };
        liveActs.Children.Add(liveBtn);
        liveActs.Children.Add(liveCancelBtn);
        var liveCard = new FluentSettingsCard(
            "角色与克隆模型",
            "免费、离线驱动声音克隆与高表现力情感朗读（模型 + 音色包共约 163 MB）",
            "mic",
            liveActs,
            dark: dark);
        _liveVoiceCard = liveCard;
        _liveVoiceBtn = liveBtn;
        _liveVoiceCancelBtn = liveCancelBtn;
        PageBodyStack.Children.Add(liveCard);
        UpdateVoiceModelCards();

        var cloneBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("克隆一个声音…") };
        cloneBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is { } xr)
            {
                await VoiceCloneAndDramaDialogs.ShowCloneVoiceDialogAsync(_ownerWindow, xr, _library);
                RenderActivePage();
            }
        };
        var clonedCards = _library.Core.CardsCloned();
        PageBodyStack.Children.Add(new FluentSettingsCard(
            $"我的克隆 ({clonedCards.Length})",
            "念 8 秒，声音不出本机",
            "mic",
            cloneBtn,
            dark: dark));

        foreach (var vc in clonedCards)
        {
            var vcCap = vc;
            var detBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Height = 28, MinHeight = 28, Content = L10n.T("管理") };
            detBtn.Click += async (_, _) =>
            {
                if (RootGrid.XamlRoot is { } xr)
                    await VoiceCloneAndDramaDialogs.ShowClonedCardDetailDialogAsync(_ownerWindow, xr, _library, vcCap.Id, () => RenderActivePage());
            };
            PageBodyStack.Children.Add(new FluentSettingsCard(vcCap.Name, string.IsNullOrWhiteSpace(vcCap.Description) ? vcCap.Lang : vcCap.Description, "wave", detBtn, dark: dark));
        }

        // Drama Sound Effects Library card
        bool sfxOk = SoundLibraryService.IsInstalled(_library.DataDir);
        var sfxPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var sfxImportBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("导入本地音效包…") };
        sfxImportBtn.Click += async (_, _) =>
        {
            string? zip = await NativePickerHelper.PickSingleFileAsync(_ownerWindow, [".zip"]);
            if (!string.IsNullOrEmpty(zip))
            {
                try
                {
                    uint cnt = await SoundLibraryService.ImportLocalZipAsync(_library.Core, _library.DataDir, zip);
                    _ownerWindow.ShowToast($"已导入广播剧音效库（{cnt} 条音效）");
                    RenderActivePage();
                }
                catch (Exception ex) { _ownerWindow.ShowToast($"导入音效包失败：{ex.Message}"); }
            }
        };
        sfxPanel.Children.Add(sfxImportBtn);
        if (sfxOk)
        {
            var sfxDelBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Content = L10n.T("清理"), Foreground = ThemeHelper.Brush(pVoice.Danger) };
            sfxDelBtn.Click += (_, _) => { SoundLibraryService.DeleteLibrary(_library.DataDir); RenderActivePage(); };
            sfxPanel.Children.Add(sfxDelBtn);
        }
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "广播剧环境音效库",
            sfxOk ? "已安装环境拟音库，多人演播时自动配声" : "可选扩展包：为广播剧演播提供翻书、雨声、风声等拟音",
            "cast",
            sfxPanel,
            dark: dark));

        // 4. Cloud TTS Daily Budget Cap
        PageBodyStack.Children.Add(MakeSectionHeader("云端用量保护", dark));
        var ttsCapSeg = YYChrome.MakeSegmentedBar(
            [("0", "不限"), ("20000", "2 万字"), ("50000", "5 万字"), ("100000", "10 万字")],
            _library.Prefs.AiDailyTtsCharCap.ToString(),
            s =>
            {
                _library.Prefs.AiDailyTtsCharCap = long.TryParse(s, out long v) ? v : 0;
                _library.SavePrefs();
                RenderActivePage();
            },
            dark,
            fontSize: 11.5,
            height: 28);
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "每日云端语音字数上限",
            "达到后自动暂停付费云端 TTS 后台预合成，避免意外超额扣费",
            "sliders",
            ttsCapSeg,
            dark: dark));
    }

    private async Task ShowSystemCjkFontPickerDialogAsync(bool dark)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        ContentDialog? dlgRef = null;

        var fonts = await _library.Fonts.LoadSystemCjkFontsAsync();
        if (_disposed) return;

        string curFace = _library.GetReaderSettings().FontFace ?? "";
        var searchBox = YYChrome.MakeSearchBox("筛选本机字体名称…");
        var listStack = new StackPanel { Spacing = 5, Padding = new Thickness(0, 2, 4, 8) };

        void Rebuild(string q)
        {
            listStack.Children.Clear();
            string query = q.Trim();
            var items = string.IsNullOrEmpty(query)
                ? fonts
                : fonts.Where(f => f.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || f.FamilyName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var entry in items)
            {
                var eCap = entry;
                string faceKey = $"local:{eCap.FamilyName}";
                bool isSel = string.Equals(curFace, faceKey, StringComparison.OrdinalIgnoreCase);

                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var meta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                meta.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = eCap.DisplayName,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(eCap.FamilyName),
                    FontSize = 14,
                    Foreground = ThemeHelper.Brush(isSel ? tc.Accent : p.T1),
                }));
                meta.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = $"春风吹又生 · {eCap.FamilyName}",
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(eCap.FamilyName),
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T3),
                }));
                Grid.SetColumn(meta, 0);
                row.Children.Add(meta);

                var pickBtn = new Button
                {
                    Style = (Style)Application.Current.Resources[isSel ? "YYFluentAccentButtonStyle" : "YYFluentButtonStyle"],
                    Height = 28,
                    MinHeight = 28,
                    Content = L10n.T(isSel ? "当前" : "选用"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                pickBtn.Click += (_, _) =>
                {
                    _library.SaveReaderSettings(_library.GetReaderSettings() with { FontFace = faceKey });
                    BroadcastReaderSettingsWithoutSelfRefresh();
                    dlgRef?.Hide();
                    RenderActivePage();
                };
                Grid.SetColumn(pickBtn, 1);
                row.Children.Add(pickBtn);

                listStack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background = ThemeHelper.Brush(isSel ? tc.Soft : p.Card),
                    BorderBrush = ThemeHelper.Brush(isSel ? tc.Edge : p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 8, 12, 8),
                    Child = row,
                });
            }
        }

        searchBox.TextChanged += (_, _) => Rebuild(searchBox.Text ?? "");
        Rebuild("");

        var root = new StackPanel { Spacing = 10, Padding = new Thickness(20, 16, 20, 16) };
        root.Children.Add(YYChrome.MakeDialogHeader(L10n.T("选择本机中文字体"), L10n.T($"已发现 {fonts.Count} 款已安装的 CJK 字体"), () => dlgRef?.Hide(), dark, titleSize: 16));
        root.Children.Add(searchBox);
        root.Children.Add(new ScrollViewer { MaxHeight = 400, Content = listStack });

        dlgRef = YYChrome.CreateFluentDialog(xr, dark, 480, root, tint: _library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
    }

    private async Task ShowVoiceProviderConfigDialogAsync(CloudTtsProvider prov, bool dark)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var p = ThemeHelper.Palette(dark);
        ContentDialog? dlgRef = null;

        string pKey = CloudVoiceEntry.ProviderKey(prov);
        string title = CloudVoiceService.ProviderDisplayName(prov, _library.Prefs);
        bool isLocal = CloudVoiceService.IsLocalProvider(prov);
        bool isFree = CloudVoiceService.IsFreeOnline(prov);

        var body = new StackPanel { Spacing = 10, Padding = new Thickness(20, 16, 20, 16) };
        body.Children.Add(YYChrome.MakeDialogHeader(title, CloudVoiceService.ProviderHelp(prov), () => dlgRef?.Hide(), dark, leading: ThemeHelper.CreateBrandLogoImage(pKey, dark, 20), titleSize: 16));

        TextBox? customNameBox = null, urlBox = null, mdlBox = null, appBox = null, regionBox = null;
        PasswordBox? keyBox = null;

        if (!isFree)
        {
            if (isLocal)
            {
                if (prov == CloudTtsProvider.Local)
                {
                    body.Children.Add(new TextBlock { Text = L10n.T("显示名称"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) });
                    customNameBox = new TextBox
                    {
                        Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
                        Text = _library.Prefs.CloudVoiceCustomNames.TryGetValue("local", out var cn) ? cn : "",
                        PlaceholderText = L10n.T("例如：家里的 Kokoro"),
                    };
                    body.Children.Add(customNameBox);
                }
                body.Children.Add(new TextBlock { Text = L10n.T("服务地址 (OpenAI 兼容 /v1)"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) });
                urlBox = new TextBox
                {
                    Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
                    Text = _library.CloudVoices.GetSavedUrl(prov),
                    PlaceholderText = CloudVoiceService.DefaultLocalUrl(prov),
                };
                body.Children.Add(urlBox);

                body.Children.Add(new TextBlock { Text = L10n.T("模型名"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) });
                mdlBox = new TextBox
                {
                    Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
                    Text = _library.CloudVoices.GetSavedModel(prov),
                    PlaceholderText = CloudVoiceService.DefaultLocalModel(prov),
                };
                body.Children.Add(mdlBox);
            }

            if (prov == CloudTtsProvider.Volcengine)
            {
                body.Children.Add(new TextBlock { Text = L10n.T("火山引擎 AppID"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) });
                appBox = new TextBox
                {
                    Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
                    Text = _library.Secrets.Get("tts.volcengine.appid") ?? "",
                    PlaceholderText = L10n.T("填写应用 AppID…"),
                };
                body.Children.Add(appBox);
            }

            if (prov == CloudTtsProvider.Azure)
            {
                body.Children.Add(new TextBlock { Text = L10n.T("Azure 服务区域 (Region)"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T2) });
                regionBox = new TextBox
                {
                    Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
                    Text = _library.Prefs.CloudVoiceAzureRegion,
                    PlaceholderText = "eastasia / eastus",
                };
                body.Children.Add(regionBox);
            }

            body.Children.Add(new TextBlock
            {
                Text = L10n.T(isLocal ? "API Key（局域网可选，本机可留空）" : (prov == CloudTtsProvider.Volcengine ? "Access Token（DPAPI 本机加密）" : "API Key（DPAPI 本机加密）")),
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T2),
            });
            keyBox = new PasswordBox
            {
                Style = (Style)Application.Current.Resources["YYCompactPasswordBoxStyle"],
                Password = _library.CloudVoices.GetApiKey(prov),
                PlaceholderText = CloudVoiceService.ProviderKeyHint(prov),
            };
            body.Children.Add(keyBox);

            if (!isLocal && string.IsNullOrEmpty(keyBox.Password) && _library.CloudVoices.GetCompanionSharedKey(prov) is { Length: > 0 } sharedKey)
            {
                var reuseBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = L10n.T("一键复用「书伴」的同厂 API Key"),
                };
                reuseBtn.Click += (_, _) => { keyBox.Password = sharedKey; };
                body.Children.Add(reuseBtn);
            }
        }

        var statusTb = new TextBlock { FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        body.Children.Add(statusTb);

        void PersistFields()
        {
            if (prov == CloudTtsProvider.Edge) _library.Secrets.Set("tts.edge.dismissed", "");
            if (prov == CloudTtsProvider.Free) _library.Secrets.Set("tts.free.dismissed", "");
            if (customNameBox is not null) _library.Prefs.CloudVoiceCustomNames["local"] = (customNameBox.Text ?? "").Trim();
            if (urlBox is not null) _library.Prefs.CloudVoiceBaseUrls[pKey] = (urlBox.Text ?? "").Trim();
            if (mdlBox is not null) _library.Prefs.CloudVoiceModels[pKey] = (mdlBox.Text ?? "").Trim();
            if (appBox is not null) _library.Secrets.Set("tts.volcengine.appid", (appBox.Text ?? "").Trim());
            if (regionBox is not null) _library.Prefs.CloudVoiceAzureRegion = (regionBox.Text ?? "").Trim();
            if (keyBox is not null) _library.CloudVoices.SetApiKey(prov, (keyBox.Password ?? "").Trim());
            _library.Prefs.CloudVoiceEnabled.Add(pKey);
            _library.Prefs.CloudVoiceDismissed.Remove(pKey);
            _library.SavePrefs();
        }

        var foot = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var testBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 34,
            Content = L10n.T("测试并刷新音色"),
        };
        testBtn.Click += async (_, _) =>
        {
            PersistFields();
            testBtn.IsEnabled = false;
            statusTb.Text = L10n.T("正在探测并拉取可用音色…");
            statusTb.Foreground = ThemeHelper.Brush(p.T2);
            statusTb.Visibility = Visibility.Visible;
            try
            {
                var (ok, count, msg) = await _library.CloudVoices.RefreshVoicesAsync(prov);
                if (_disposed) return;
                statusTb.Text = ok ? L10n.T($"连接成功：已就绪 {count} 个音色") : $"{L10n.T("测试失败：")}{msg}";
                statusTb.Foreground = ThemeHelper.Brush(ok ? p.T1 : p.Danger);
            }
            catch (Exception ex)
            {
                if (!_disposed)
                {
                    statusTb.Text = $"{L10n.T("测试失败：")}{ex.Message}";
                    statusTb.Foreground = ThemeHelper.Brush(p.Danger);
                }
            }
            finally
            {
                testBtn.IsEnabled = true;
            }
        };

        var saveBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 34,
            Content = L10n.T("保存"),
        };
        saveBtn.Click += (_, _) =>
        {
            PersistFields();
            BroadcastVoiceWithoutSelfRefresh();
            dlgRef?.Hide();
            RenderActivePage();
        };

        Grid.SetColumn(testBtn, 0);
        Grid.SetColumn(saveBtn, 1);
        foot.Children.Add(testBtn);
        foot.Children.Add(saveBtn);
        body.Children.Add(foot);

        dlgRef = YYChrome.CreateFluentDialog(xr, dark, 460, body, tint: _library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
    }

    private void DisconnectVoiceProvider(CloudTtsProvider prov)
    {
        string pKey = CloudVoiceEntry.ProviderKey(prov);
        if (prov == CloudTtsProvider.Edge)
        {
            _library.Secrets.Set("tts.edge.dismissed", "1");
        }
        else if (prov == CloudTtsProvider.Free)
        {
            _library.Secrets.Set("tts.free.dismissed", "1");
        }
        else if (CloudVoiceService.IsLocalProvider(prov))
        {
            _library.Prefs.CloudVoiceEnabled.Remove(pKey);
            _library.Prefs.CloudVoiceDismissed.Add(pKey);
        }
        else
        {
            _library.CloudVoices.SetApiKey(prov, "");
            if (prov == CloudTtsProvider.Volcengine)
                _library.Secrets.Set("tts.volcengine.appid", "");
            _library.Prefs.CloudVoiceEnabled.Remove(pKey);
        }
        _library.SavePrefs();
        BroadcastVoiceWithoutSelfRefresh();
    }

    private static string VoiceProviderSearchAliases(CloudTtsProvider p) => p switch
    {
        CloudTtsProvider.Edge => "microsoft 微软 edge 晓晓 云希 免费 免key",
        CloudTtsProvider.Free => "free 在线 免费 公共 免key",
        CloudTtsProvider.Volcengine => "volcengine doubao 豆包 火山 字节 bytedance",
        CloudTtsProvider.Xiaomi => "xiaomi mimo 小米 限时免费",
        CloudTtsProvider.SiliconFlow => "siliconflow 硅基 cosyvoice",
        CloudTtsProvider.DashScope => "dashscope 阿里 百炼 千问 qwen aliyun",
        CloudTtsProvider.MiniMax => "minimax 海螺 稀宇",
        CloudTtsProvider.Fish => "fish audio openaudio",
        CloudTtsProvider.Azure => "microsoft azure 微软",
        CloudTtsProvider.OpenAi => "openai gpt chatgpt",
        CloudTtsProvider.ElevenLabs => "elevenlabs 11labs",
        CloudTtsProvider.Deepgram => "deepgram aura",
        CloudTtsProvider.VoiceStudio => "voicestudio omnivoice 本机 克隆",
        CloudTtsProvider.Voicebox => "voicebox 本机",
        CloudTtsProvider.MlxAudio => "mlx audio apple 本机 局域网",
        CloudTtsProvider.Speaches => "speaches docker kokoro 本机 局域网 nas",
        CloudTtsProvider.Local => "custom local gpt-sovits kokoro localai 自定义 本机 局域网",
        _ => "",
    };

    private async Task ShowVoiceSourceCatalogDialogAsync(bool dark)
    {
        if (RootGrid.XamlRoot is not { } xr) return;
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        ContentDialog? dlgRef = null;
        bool dialogOpen = true;
        CloudTtsProvider? chosenAfterClose = null;

        var searchBox = YYChrome.MakeSearchBox("搜索声音来源，如 Edge、火山、豆包、Azure、硅基、MiniMax、本机…");
        var catalogStack = new StackPanel { Spacing = 8, Padding = new Thickness(0, 2, 10, 4) };

        bool Matches(CloudTtsProvider prov, string[] terms)
        {
            if (terms.Length == 0) return true;
            string hay = $"{CloudVoiceService.ProviderDisplayName(prov, _library.Prefs)} {CloudVoiceEntry.ProviderKey(prov)} {CloudVoiceService.ProviderBadge(prov) ?? ""} {VoiceProviderSearchAliases(prov)} {CloudVoiceService.ProviderHelp(prov)}";
            return terms.All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        Button MakeTile(CloudTtsProvider prov)
        {
            var prCap = prov;
            string pKey = CloudVoiceEntry.ProviderKey(prCap);
            bool isCfg = _library.CloudVoices.IsConfigured(prCap);
            bool isFree = CloudVoiceService.IsFreeOnline(prCap);
            bool isLocal = CloudVoiceService.IsLocalProvider(prCap);
            bool isLive = isLocal && LocalVoiceProbe.Shared.IsLive(prCap);
            bool canReuseKey = !isLocal && !isFree && !isCfg && !string.IsNullOrEmpty(_library.CloudVoices.GetCompanionSharedKey(prCap));
            bool hasGreenDot = isCfg || isLive || canReuseKey;

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string fallbackGlyph = isLocal ? "server" : (isFree ? "wave" : "cloud");
            FrameworkElement icon = (FrameworkElement?)ThemeHelper.CreateBrandLogoImage(pKey, dark, 18)
                ?? YYIcons.Make(fallbackGlyph, 14, ThemeHelper.Brush(p.T2));
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(L10n.Skip(new TextBlock
            {
                Text = CloudVoiceService.ProviderDisplayName(prCap, _library.Prefs),
                FontSize = 12,
                FontWeight = isCfg ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ThemeHelper.Brush(p.T1),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }));

            string? subTag = isCfg
                ? L10n.T("已就绪")
                : isFree
                    ? L10n.T("已隐藏 · 点此开启")
                    : isLive
                        ? L10n.T("本机运行中")
                        : canReuseKey
                            ? L10n.T("可复用书伴 Key")
                            : CloudVoiceService.ProviderBadge(prCap) is { Length: > 0 } b ? L10n.T(b) : null;
            if (!string.IsNullOrEmpty(subTag))
            {
                nameStack.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = subTag,
                    FontSize = 10,
                    Foreground = ThemeHelper.Brush(isCfg || isLive || canReuseKey ? p.Ok : (isFree ? tc.Accent : p.T3)),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }));
            }
            Grid.SetColumn(nameStack, 1);
            row.Children.Add(nameStack);

            if (hasGreenDot)
            {
                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = ThemeHelper.Brush(p.Ok),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(dot, 2);
                row.Children.Add(dot);
            }

            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Height = 42,
                MinHeight = 42,
                Padding = new Thickness(10, 0, 10, 0),
                Content = row,
            };
            if (isCfg)
            {
                btn.Background = ThemeHelper.Brush(tc.Soft);
                btn.BorderBrush = ThemeHelper.Brush(tc.Edge);
            }
            ToolTipService.SetToolTip(btn, L10n.T(CloudVoiceService.ProviderHelp(prCap)));
            btn.Click += (_, _) =>
            {
                if (isFree && !isCfg)
                {
                    if (prCap == CloudTtsProvider.Edge) _library.Secrets.Set("tts.edge.dismissed", "");
                    if (prCap == CloudTtsProvider.Free) _library.Secrets.Set("tts.free.dismissed", "");
                    _library.SavePrefs();
                    BroadcastVoiceWithoutSelfRefresh();
                    dialogOpen = false;
                    dlgRef?.Hide();
                    ShowSettingsSavedToast($"已重新开启“{CloudVoiceService.ProviderDisplayName(prCap, _library.Prefs)}”");
                    RenderActivePage();
                    return;
                }
                chosenAfterClose = prCap;
                dialogOpen = false;
                dlgRef?.Hide();
            };
            return btn;
        }

        void RebuildCatalog()
        {
            if (!dialogOpen) return;
            catalogStack.Children.Clear();
            string[] terms = (searchBox.Text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            CloudTtsProvider[] domesticGroup =
            [
                CloudTtsProvider.Edge,
                CloudTtsProvider.Free,
                CloudTtsProvider.Volcengine,
                CloudTtsProvider.Xiaomi,
                CloudTtsProvider.SiliconFlow,
                CloudTtsProvider.DashScope,
                CloudTtsProvider.MiniMax,
            ];
            CloudTtsProvider[] globalGroup =
            [
                CloudTtsProvider.Fish,
                CloudTtsProvider.Azure,
                CloudTtsProvider.OpenAi,
                CloudTtsProvider.ElevenLabs,
                CloudTtsProvider.Deepgram,
            ];
            CloudTtsProvider[] localGroup =
            [
                CloudTtsProvider.VoiceStudio,
                CloudTtsProvider.Voicebox,
                CloudTtsProvider.MlxAudio,
                CloudTtsProvider.Speaches,
            ];

            int totalRendered = 0;
            void AddGridGroup(string title, CloudTtsProvider[] group)
            {
                var matched = group.Where(x => Matches(x, terms)).ToList();
                if (matched.Count == 0) return;
                totalRendered += matched.Count;

                catalogStack.Children.Add(new TextBlock
                {
                    Text = L10n.T(title),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T3),
                    Margin = new Thickness(2, catalogStack.Children.Count == 0 ? 0 : 2, 0, -2),
                });

                var grid = new Grid { ColumnSpacing = 7, RowSpacing = 6 };
                for (int c = 0; c < 3; c++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int rows = (matched.Count + 2) / 3;
                for (int r = 0; r < rows; r++)
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (int i = 0; i < matched.Count; i++)
                {
                    var tile = MakeTile(matched[i]);
                    Grid.SetRow(tile, i / 3);
                    Grid.SetColumn(tile, i % 3);
                    grid.Children.Add(tile);
                }
                catalogStack.Children.Add(grid);
            }

            AddGridGroup("免费在线与国内云端", domesticGroup);
            AddGridGroup("海外云端", globalGroup);
            AddGridGroup("本机与局域网", localGroup);

            if (totalRendered == 0)
            {
                catalogStack.Children.Add(new TextBlock
                {
                    Text = L10n.T("没找到匹配的声音来源。点下方「自定义语音接口」可接任意 OpenAI 兼容语音服务"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 28, 0, 28),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        searchBox.TextChanged += (_, _) => RebuildCatalog();
        RebuildCatalog();

        var customFooterRow = new Grid { ColumnSpacing = 8 };
        customFooterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        customFooterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        customFooterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var plusIcon = YYIcons.Make("plus", 14, ThemeHelper.Brush(tc.Accent));
        Grid.SetColumn(plusIcon, 0);
        customFooterRow.Children.Add(plusIcon);

        var customLbl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        customLbl.Children.Add(new TextBlock
        {
            Text = L10n.T("自定义语音接口"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
        });
        customLbl.Children.Add(new TextBlock
        {
            Text = L10n.T("GPT-SoVITS、Kokoro 或局域网 /v1/audio/speech"),
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(customLbl, 1);
        customFooterRow.Children.Add(customLbl);

        var chevron = YYIcons.Make("chevron-right", 12, ThemeHelper.Brush(p.T3));
        Grid.SetColumn(chevron, 2);
        customFooterRow.Children.Add(chevron);

        var customBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Height = 40,
            MinHeight = 40,
            Padding = new Thickness(12, 0, 12, 0),
            Content = customFooterRow,
        };
        customBtn.Click += (_, _) =>
        {
            chosenAfterClose = CloudTtsProvider.Local;
            dialogOpen = false;
            dlgRef?.Hide();
        };

        var root = new StackPanel { Spacing = 8, Padding = new Thickness(20, 14, 20, 14) };
        root.Children.Add(YYChrome.MakeDialogHeader(
            L10n.T("添加声音来源"),
            L10n.T("绿点表示免 Key、已就绪、本机运行或可复用书伴 Key"),
            () => { dialogOpen = false; dlgRef?.Hide(); },
            dark,
            titleSize: 16));
        root.Children.Add(searchBox);
        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 430,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = catalogStack,
        });
        root.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });
        root.Children.Add(customBtn);

        dlgRef = YYChrome.CreateFluentDialog(xr, dark, 560, root, tint: _library.Prefs.Tint);
        dlgRef.Closed += (_, _) => dialogOpen = false;
        dlgRef.Opened += (_, _) =>
        {
            searchBox.Focus(FocusState.Programmatic);
            _ = Task.Run(async () =>
            {
                try
                {
                    bool changed = await LocalVoiceProbe.Shared.ScanAllAsync(_library);
                    if (changed && !_disposed)
                    {
                        _uiQueue.TryEnqueue(() =>
                        {
                            if (!dialogOpen || _disposed) return;
                            RebuildCatalog();
                        });
                    }
                }
                catch { }
            });
        };
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);

        if (chosenAfterClose is { } picked && !_disposed)
            await ShowVoiceProviderConfigDialogAsync(picked, dark);
    }
}
