using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

        PageBodyStack.Children.Add(MakeSectionHeader("默认纸张与排版", dark, first: true));

        var paperRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (preset, name, bg, ink) in new[]
        {
            (Paper.Plain, "素白", YYPapers.Plain.Bg, YYPapers.Plain.Ink),
            (Paper.Kami, "纸本", YYPapers.Kami.Bg, YYPapers.Kami.Ink),
            (Paper.Sage, "芦苇", YYPapers.Sage.Bg, YYPapers.Sage.Ink),
            (Paper.Eink, "铅纸", YYPapers.Eink.Bg, YYPapers.Eink.Ink),
            (Paper.Dark, "暗黑", YYPapers.Dark.Bg, YYPapers.Dark.Ink),
            (Paper.KamiNight, "纸夜", YYPapers.Kaminight.Bg, YYPapers.Kaminight.Ink),
        })
        {
            bool sel = activeRes.Custom is null && activeRes.Preset == preset;
            var prCap = preset;
            var btn = new Button
            {
                Width = 52,
                Height = 32,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(bg),
                BorderBrush = ThemeHelper.Brush(sel ? tc.Accent : p.Line),
                BorderThickness = new Thickness(sel ? 2 : 1),
                Content = new TextBlock { Text = L10n.T(name), FontSize = 11.5, Foreground = ThemeHelper.Brush(ink), FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal },
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
        PageBodyStack.Children.Add(new FluentSettingsCard("默认阅读纸张", "可在单本书排版菜单中单独覆写", "book-open", paperRow, dark: dark));

        var fontPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var fontCombo = new ComboBox { Width = 140 };
        (Font? F, string Lbl)[] fonts = [(null, "跟随纸张"), (Font.Sans, "无衬线黑体"), (Font.Serif, "衬线宋体"), (Font.Kai, "楷体")];
        foreach (var (_, l) in fonts) fontCombo.Items.Add(L10n.T(l));
        fontCombo.SelectedIndex = Math.Max(0, Array.FindIndex(fonts, x => x.F == rs.Font));
        fontCombo.SelectionChanged += (_, _) =>
        {
            if (fontCombo.SelectedIndex >= 0)
            {
                _library.SaveReaderSettings(_library.GetReaderSettings() with { Font = fonts[fontCombo.SelectedIndex].F, FontFace = null });
                BroadcastReaderSettingsWithoutSelfRefresh();
                RenderPaperLivePreview(dark);
            }
        };
        var importFontBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("导入字体…") };
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
        PageBodyStack.Children.Add(new FluentSettingsCard("正文字体", "支持系统字体与导入 .ttf / .otf 自定义字库", "char-book", fontPanel, dark: dark));

        var szSlider = new Slider { Width = 180, Minimum = 12, Maximum = 32, StepFrequency = 1, Value = rs.FontSize };
        var szCard = new FluentSettingsCard("默认字号", $"当前 {Math.Round(rs.FontSize)} px（阅读时也可按 Ctrl+滚轮实时缩放）", "sliders", szSlider, dark: dark);
        szSlider.ValueChanged += (_, args) =>
        {
            double nv = Math.Round(args.NewValue);
            _library.SaveReaderSettings(_library.GetReaderSettings() with { FontSize = nv });
            szCard.Description = $"当前 {nv} px（阅读时也可按 Ctrl+滚轮实时缩放）";
            BroadcastReaderSettingsWithoutSelfRefresh();
            RenderPaperLivePreview(dark);
        };
        PageBodyStack.Children.Add(szCard);

        var lhSlider = new Slider { Width = 180, Minimum = 1.4, Maximum = 2.6, StepFrequency = 0.1, Value = rs.LineHeight ?? 1.9 };
        var lhCard = new FluentSettingsCard("默认行距", $"当前 {(rs.LineHeight ?? 1.9):0.0} 倍行高", "list", lhSlider, dark: dark);
        lhSlider.ValueChanged += (_, args) =>
        {
            double nv = Math.Round(args.NewValue, 1);
            _library.SaveReaderSettings(_library.GetReaderSettings() with { LineHeight = nv });
            lhCard.Description = $"当前 {nv:0.0} 倍行高";
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
        PageBodyStack.Children.Add(new FluentSettingsCard("宽屏双栏对开排版", "窗口宽于 820px 时自动展开对开双栏；展开左右侧栏时自动退回单栏", "col-2", twoColSw, dark: dark));

        var modeCombo = new ComboBox { Width = 160 };
        modeCombo.Items.Add(L10n.T("左右分页"));
        modeCombo.Items.Add(L10n.T("上下连续滚动"));
        modeCombo.SelectedIndex = rs.Mode == LayoutMode.Scrolled ? 1 : 0;
        modeCombo.SelectionChanged += (_, _) =>
        {
            _library.SaveReaderSettings(_library.GetReaderSettings() with { Mode = modeCombo.SelectedIndex == 1 ? LayoutMode.Scrolled : LayoutMode.Paginated });
            BroadcastReaderSettingsWithoutSelfRefresh();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("默认翻页方式", "分页模式更似纸书，滚动模式适合长文扫读", "col-scroll", modeCombo, dark: dark));

        var noAnimSw = new ToggleSwitch { IsOn = _library.Prefs.ImmersiveNoAnimation };
        noAnimSw.Toggled += (_, _) =>
        {
            _library.Prefs.ImmersiveNoAnimation = noAnimSw.IsOn;
            _library.SavePrefs();
            BroadcastReaderSettingsWithoutSelfRefresh();
        };
        PageBodyStack.Children.Add(new FluentSettingsCard("关闭翻页过渡动画", "即时翻页零延迟", "sparkle", noAnimSw, dark: dark));

        // Advanced typography defaults expander (paragraph spacing / indent / justify / punctuation compression)
        var advExp = new FluentSettingsExpander("高级默认排版", "段距、首行缩进、两端对齐与标点挤压", "sliders", dark: dark);

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
        advExp.AddKeyValueRow("段落间距", "控制相邻段落之间的留白距离", paraSeg);

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
        advExp.AddKeyValueRow("首行缩进", "中文书籍通常首行缩进两字符，可按个人偏好覆盖", indentSeg);

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
        advExp.AddKeyValueRow("段落对齐", "控制左右两端对齐或自然左对齐", justSeg);

        var compSw = new ToggleSwitch { IsOn = rs.Compress };
        compSw.Toggled += (_, _) =>
        {
            _library.SaveReaderSettings(_library.GetReaderSettings() with { Compress = compSw.IsOn });
            BroadcastReaderSettingsWithoutSelfRefresh();
        };
        advExp.AddKeyValueRow("标点挤压", "优化中文引号与句末连续标点的排版间距", compSw);
        PageBodyStack.Children.Add(advExp);

        // Open-source curated Font Shelf (FontShelfService.Catalog) + System CJK Font Picker
        PageBodyStack.Children.Add(MakeSectionHeader("开源阅读字体架与本机字库", dark));

        int installedPacks = _library.Fonts.Catalog.Count(pk => _library.Fonts.IsPackInstalled(pk.Id));
        var fontShelfExp = new FluentSettingsExpander(
            $"开源精选阅读字体包（已安装 {installedPacks} / {_library.Fonts.Catalog.Count}）",
            "一键下载霞鹜文楷、思源宋体、思源黑体等免费可商用高质量字库",
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
                    Content = L10n.T(isCur ? "当前使用" : "设为正文"),
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
        var pickSysFontBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("选择本机字体…") };
        pickSysFontBtn.Click += async (_, _) => await ShowSystemCjkFontPickerDialogAsync(dark);
        PageBodyStack.Children.Add(new FluentSettingsCard("本机系统 CJK 字体", curLocalLabel, "char-book", pickSysFontBtn, dark: dark));

        // Custom imported fonts list
        var customFonts = _library.Fonts.GetCustomFontFiles();
        if (customFonts.Count > 0)
        {
            PageBodyStack.Children.Add(MakeSectionHeader($"已导入字体 ({customFonts.Count})", dark));
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
                    Content = L10n.T(isCur ? "当前使用" : "设为正文"),
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
        PageBodyStack.Children.Add(MakeSectionHeader("外文点词查词与行间释义", dark, first: true));

        var lookupSw = new ToggleSwitch { IsOn = _library.Prefs.ForeignLookup };
        lookupSw.Toggled += (_, _) => { _library.Prefs.ForeignLookup = lookupSw.IsOn; _library.SavePrefs(); BroadcastForeignPrefsWithoutSelfRefresh(); };
        PageBodyStack.Children.Add(new FluentSettingsCard("点按单词立即查词", "内置 70 万词条离线简明英汉词典（ECDICT），断网可用", "a-wen", lookupSw, dark: dark));

        var glossSw = new ToggleSwitch { IsOn = _library.Prefs.ForeignGlossEnabled };
        glossSw.Toggled += (_, _) => { _library.Prefs.ForeignGlossEnabled = glossSw.IsOn; _library.SavePrefs(); BroadcastForeignPrefsWithoutSelfRefresh(); };
        PageBodyStack.Children.Add(new FluentSettingsCard("难词行间中文释义", "自动在超过词汇等级的英文词上方标注精简中文", "translate", glossSw, dark: dark));

        var levelCombo = new ComboBox { Width = 196 };
        (byte Val, string Lbl)[] levels = [(2, "大学四级 (CET-4)"), (3, "大学六级 (CET-6)"), (4, "考研词汇"), (5, "雅思 (IELTS)"), (6, "GRE 高阶")];
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
        PageBodyStack.Children.Add(new FluentSettingsCard("行间释义词汇门槛", "仅对高于该等级的难词显示行间提示", "filter", levelCombo, dark: dark));

        var markVocabSw = new ToggleSwitch { IsOn = _library.Prefs.ForeignMarkVocab };
        markVocabSw.Toggled += (_, _) => { _library.Prefs.ForeignMarkVocab = markVocabSw.IsOn; _library.SavePrefs(); BroadcastForeignPrefsWithoutSelfRefresh(); };
        PageBodyStack.Children.Add(new FluentSettingsCard("在正文中标记生词本单词", "读外文书时用柔和虚线提醒已加入生词本的词", "bookmark", markVocabSw, dark: dark));

        var glossVocabSw = new ToggleSwitch { IsOn = _library.Prefs.GlossVocabOnly };
        glossVocabSw.Toggled += (_, _) => { _library.Prefs.GlossVocabOnly = glossVocabSw.IsOn; _library.SavePrefs(); BroadcastForeignPrefsWithoutSelfRefresh(); };
        PageBodyStack.Children.Add(new FluentSettingsCard("仅对生词本显示行间释义", "减少页面干扰，只为收藏过的生词显示行间提示", "filter", glossVocabSw, dark: dark));

        PageBodyStack.Children.Add(MakeSectionHeader("离线 MDict (.mdx) 专业词典", dark));
        var p = ThemeHelper.Palette(dark);
        var impDictBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("导入 .mdx 词典…") };
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
            $"已导入本地词典 ({dicts.Count})",
            dicts.Count == 0 ? "导入牛津、朗文等 .mdx 词典后优先展示富文本词条" : "按从上到下顺序优先查词，可启停或调整顺序",
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
            _storageModelsCard.Description = $"Kokoro 语音：{kokoroStatus} · LiveVoice 克隆模型：{liveStatus} · 音效库：{(SoundLibraryService.IsInstalled(_library.DataDir) ? "已安装" : "未安装")}";
        }
    }

    private void BuildListeningPage(bool dark)
    {
        PageBodyStack.Children.Add(MakeSectionHeader("朗读引擎、离线模型与语速", dark, first: true));

        var kokoroActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var kokoroBtn = new Button();
        kokoroBtn.Click += (_, _) =>
        {
            _ = _library.Kokoro.DownloadAsync();
            _ownerWindow.ShowToast("正在后台下载 Kokoro 离线高质量语音模型…");
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
            "Kokoro 本地离线神经语音",
            "免费、离线、接近真人的本地神经网络朗读模型",
            "wave",
            kokoroActs,
            dark: dark);
        _kokoroCard = kokoroCard;
        _kokoroBtn = kokoroBtn;
        _kokoroCancelBtn = kokoroCancelBtn;
        PageBodyStack.Children.Add(kokoroCard);

        var liveActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var liveBtn = new Button();
        liveBtn.Click += (_, _) =>
        {
            _ = _library.LiveVoice.DownloadAsync();
            _ownerWindow.ShowToast("正在后台下载 LiveVoice 离线自然人声模型…");
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
            "LiveVoice 本地自然人声与声音克隆模型",
            "免费、离线驱动声音克隆与高表现力情感朗读（模型 + 音色包共约 163 MB）",
            "mic",
            liveActs,
            dark: dark);
        _liveVoiceCard = liveCard;
        _liveVoiceBtn = liveBtn;
        _liveVoiceCancelBtn = liveCancelBtn;
        PageBodyStack.Children.Add(liveCard);
        UpdateVoiceModelCards();

        var zhVoices = new List<(string Id, string Label)>
        {
            ("", "自动推荐（Kokoro / Edge）"),
            ("cloud.edge.zh-CN-XiaoxiaoNeural", "Edge · 晓晓（温暖自然）"),
            ("cloud.edge.zh-CN-YunxiNeural", "Edge · 云希（清朗男声）"),
            ("cloud.edge.zh-CN-YunjianNeural", "Edge · 云健（沉稳说书）"),
            ("cloud.edge.zh-CN-XiaoyiNeural", "Edge · 晓伊（柔和叙事）"),
        };
        foreach (var sp in _library.Kokoro.GetSpeakers("zh-Hans"))
            zhVoices.Add((sp.Id, $"Kokoro 离线 · {sp.Name}"));

        var zhRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var zhCombo = new ComboBox { Width = 236 };
        foreach (var (_, lbl) in zhVoices) zhCombo.Items.Add(lbl);
        string normZhPref = DramaSynthService.NormalizeVoiceId(_library.Prefs.VoiceId) ?? "";
        int zhIdx = zhVoices.FindIndex(v => v.Id == normZhPref);
        zhCombo.SelectedIndex = Math.Max(0, zhIdx);
        zhCombo.SelectionChanged += (_, _) =>
        {
            if (zhCombo.SelectedIndex >= 0)
            {
                _library.Prefs.VoiceId = zhVoices[zhCombo.SelectedIndex].Id;
                _library.SavePrefs();
                BroadcastVoiceWithoutSelfRefresh();
            }
        };
        var zhPickAllBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("试听 / 全部…") };
        zhPickAllBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is not { } xr) return;
            await Yuyue.UI.Reader.ReaderPaneView.ShowVoicePickerDialogAsync(xr, _library, L10n.T("中文默认朗读音色"), _library.Prefs.VoiceId, vid =>
            {
                _library.Prefs.VoiceId = DramaSynthService.NormalizeVoiceId(vid) ?? vid;
                _library.SavePrefs();
                BroadcastVoiceWithoutSelfRefresh();
                RenderActivePage();
            }, darkOverride: dark);
        };
        zhRow.Children.Add(zhCombo);
        zhRow.Children.Add(zhPickAllBtn);
        PageBodyStack.Children.Add(new FluentSettingsCard("中文默认朗读音色", "支持离线 Kokoro、克隆音色、Edge 与全部云端声源", "headphones", zhRow, dark: dark));

        var enVoices = new List<(string Id, string Label)>
        {
            ("", "自动推荐（Kokoro / Edge）"),
            ("cloud.edge.en-US-JennyNeural", "Edge · Jenny (US Natural)"),
            ("cloud.edge.en-US-AriaNeural", "Edge · Aria (US Expressive)"),
            ("cloud.edge.en-US-GuyNeural", "Edge · Guy (US Male)"),
            ("cloud.edge.en-GB-SoniaNeural", "Edge · Sonia (UK Female)"),
        };
        foreach (var sp in _library.Kokoro.GetSpeakers("en"))
            enVoices.Add((sp.Id, $"Kokoro 离线 · {sp.Name}"));

        var enRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var enCombo = new ComboBox { Width = 236 };
        foreach (var (_, lbl) in enVoices) enCombo.Items.Add(lbl);
        string normEnPref = DramaSynthService.NormalizeVoiceId(_library.Prefs.VoiceIdEn) ?? "";
        int enIdx = enVoices.FindIndex(v => v.Id == normEnPref);
        enCombo.SelectedIndex = Math.Max(0, enIdx);
        enCombo.SelectionChanged += (_, _) =>
        {
            if (enCombo.SelectedIndex >= 0)
            {
                _library.Prefs.VoiceIdEn = enVoices[enCombo.SelectedIndex].Id;
                _library.SavePrefs();
                BroadcastVoiceWithoutSelfRefresh();
            }
        };
        var enPickAllBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("试听 / 全部…") };
        enPickAllBtn.Click += async (_, _) =>
        {
            if (RootGrid.XamlRoot is not { } xr) return;
            await Yuyue.UI.Reader.ReaderPaneView.ShowVoicePickerDialogAsync(xr, _library, L10n.T("英文默认朗读音色"), _library.Prefs.VoiceIdEn, vid =>
            {
                _library.Prefs.VoiceIdEn = DramaSynthService.NormalizeVoiceId(vid) ?? vid;
                _library.SavePrefs();
                BroadcastVoiceWithoutSelfRefresh();
                RenderActivePage();
            }, darkOverride: dark);
        };
        enRow.Children.Add(enCombo);
        enRow.Children.Add(enPickAllBtn);
        PageBodyStack.Children.Add(new FluentSettingsCard("英文默认朗读音色", "读英文原版书或英汉双语时自动切换该音色", "globe", enRow, dark: dark));

        var rateSlider = new Slider { Width = 180, Minimum = 0.7, Maximum = 2.0, StepFrequency = 0.05, Value = Math.Clamp(_library.Prefs.SpeechRate, 0.7, 2.0) };
        var rateCard = new FluentSettingsCard("默认朗读倍速", $"当前 {_library.Prefs.SpeechRate:0.##}×", "sliders", rateSlider, dark: dark);
        rateSlider.ValueChanged += (_, args) =>
        {
            double nv = Math.Round(args.NewValue, 2);
            _library.Prefs.SpeechRate = nv;
            _library.SavePrefs();
            rateCard.Description = $"当前 {nv:0.##}×";
            BroadcastVoiceWithoutSelfRefresh();
        };
        PageBodyStack.Children.Add(rateCard);

        var awakeSw = new ToggleSwitch { IsOn = _library.Prefs.KeepAwakeWhileListening };
        awakeSw.Toggled += (_, _) => { _library.Prefs.KeepAwakeWhileListening = awakeSw.IsOn; _library.SavePrefs(); };
        PageBodyStack.Children.Add(new FluentSettingsCard("听书时保持电脑唤醒", "朗读与播放有声书期间自动阻止系统休眠", "clock", awakeSw, dark: dark));

        PageBodyStack.Children.Add(MakeSectionHeader("克隆音色、云端声源与广播剧音效", dark));

        var cloneBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("录制 / 克隆专属音色…") };
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
            $"声音克隆与专属音色 ({clonedCards.Length})",
            "录制一段 10 秒样本即可打造专属个人朗读声音",
            "mic",
            cloneBtn,
            dark: dark));

        foreach (var vc in clonedCards)
        {
            var vcCap = vc;
            var detBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Height = 28, MinHeight = 28, Content = L10n.T("试听与管理") };
            detBtn.Click += async (_, _) =>
            {
                if (RootGrid.XamlRoot is { } xr)
                    await VoiceCloneAndDramaDialogs.ShowClonedCardDetailDialogAsync(_ownerWindow, xr, _library, vcCap.Id, () => RenderActivePage());
            };
            PageBodyStack.Children.Add(new FluentSettingsCard(vcCap.Name, string.IsNullOrWhiteSpace(vcCap.Description) ? vcCap.Lang : vcCap.Description, "wave", detBtn, dark: dark));
        }

        // Local & Cloud TTS source scan card
        var scanVoiceBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("扫描本地声源") };
        scanVoiceBtn.Click += async (_, _) =>
        {
            scanVoiceBtn.IsEnabled = false;
            try
            {
                await LocalVoiceProbe.Shared.ScanAllAsync(_library);
                if (_disposed) return;
                int found = CloudVoiceService.AllProviders.Count(pr => CloudVoiceService.IsLocalProvider(pr) && LocalVoiceProbe.Shared.IsLive(pr));
                _ownerWindow.ShowToast(found > 0 ? $"已探测到 {found} 个本地 TTS 服务" : "未探测到运行中的本地 TTS 服务（支持 CosyVoice / GPT-SoVITS / IndexTTS 等）");
                RenderActivePage();
            }
            finally
            {
                scanVoiceBtn.IsEnabled = true;
            }
        };
        int liveLocalCount = CloudVoiceService.AllProviders.Count(pr => CloudVoiceService.IsLocalProvider(pr) && LocalVoiceProbe.Shared.IsLive(pr));
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "扫描本地语音服务",
            liveLocalCount > 0 ? $"已探测到 {liveLocalCount} 个本机运行中的神经语音引擎" : "自动扫描本机 CosyVoice、IndexTTS、GPT-SoVITS、FishSpeech 等端口",
            "server",
            scanVoiceBtn,
            dark: dark));

        // Full Cloud & Local TTS Provider Manager Expander (1:1 macOS)
        int configuredCloudCnt = CloudVoiceService.AllProviders.Count(pr => _library.CloudVoices.IsConfigured(pr));
        var cloudExp = new FluentSettingsExpander(
            $"云端与本地 TTS 声源配置（已就绪 {configuredCloudCnt} 家）",
            "支持火山引擎、微软 Azure、硅基流动、阿里百炼、MiniMax、Fish Audio、OpenAI、ElevenLabs 及本机 OpenAI 兼容端点",
            "cloud",
            dark: dark);

        foreach (var prov in CloudVoiceService.AllProviders)
        {
            var prCap = prov;
            bool isCfg = _library.CloudVoices.IsConfigured(prCap);
            bool isLocal = CloudVoiceService.IsLocalProvider(prCap);
            bool isLive = isLocal && LocalVoiceProbe.Shared.IsLive(prCap);
            int voiceCnt = isCfg ? _library.CloudVoices.GetVoices(prCap).Count : 0;
            string badge = CloudVoiceService.ProviderBadge(prCap) is { Length: > 0 } b ? $" · {b}" : "";
            string sub = isCfg
                ? $"已就绪 · {voiceCnt} 个可用音色{badge}"
                : (isLive ? $"本机探测运行中{badge}" : $"{CloudVoiceService.ProviderHelp(prCap)}");

            var cfgBtn = new Button
            {
                Style = (Style)Application.Current.Resources[isCfg ? "YYFluentSoftAccentButtonStyle" : "YYFluentButtonStyle"],
                Height = 28,
                MinHeight = 28,
                Content = L10n.T(isCfg ? "已配置 · 管理…" : "配置…"),
            };
            cfgBtn.Click += async (_, _) => await ShowVoiceProviderConfigDialogAsync(prCap, dark);
            cloudExp.AddKeyValueRow(CloudVoiceService.ProviderDisplayName(prCap, _library.Prefs), sub, cfgBtn);
        }
        PageBodyStack.Children.Add(cloudExp);

        // Daily Cloud TTS Char Budget Cap
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
            var sfxDelBtn = new Button { Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"], Content = L10n.T("清理"), Foreground = ThemeHelper.Brush(ThemeHelper.Palette(dark).Danger) };
            sfxDelBtn.Click += (_, _) => { SoundLibraryService.DeleteLibrary(_library.DataDir); RenderActivePage(); };
            sfxPanel.Children.Add(sfxDelBtn);
        }
        PageBodyStack.Children.Add(new FluentSettingsCard(
            "广播剧环境音效库",
            sfxOk ? "已安装环境拟音库，多人演播时自动配声" : "可选扩展包：为广播剧演播提供翻书、雨声、风声等拟音",
            "cast",
            sfxPanel,
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
        var searchBox = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            PlaceholderText = L10n.T("筛选本机字体名称…"),
            Height = 32,
            MinHeight = 32,
        };
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
        body.Children.Add(YYChrome.MakeDialogHeader(title, CloudVoiceService.ProviderHelp(prov), () => dlgRef?.Hide(), dark, titleSize: 16));

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
            if (customNameBox is not null) _library.Prefs.CloudVoiceCustomNames["local"] = (customNameBox.Text ?? "").Trim();
            if (urlBox is not null) _library.Prefs.CloudVoiceBaseUrls[pKey] = (urlBox.Text ?? "").Trim();
            if (mdlBox is not null) _library.Prefs.CloudVoiceModels[pKey] = (mdlBox.Text ?? "").Trim();
            if (appBox is not null) _library.Secrets.Set("tts.volcengine.appid", (appBox.Text ?? "").Trim());
            if (regionBox is not null) _library.Prefs.CloudVoiceAzureRegion = (regionBox.Text ?? "").Trim();
            if (keyBox is not null) _library.CloudVoices.SetApiKey(prov, (keyBox.Password ?? "").Trim());
            _library.Prefs.CloudVoiceEnabled.Add(pKey);
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
}
