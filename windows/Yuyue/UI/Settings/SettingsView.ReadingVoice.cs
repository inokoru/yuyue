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
            ("", "自动推荐（离线 Kokoro / Edge 在线）"),
            ("edge.zh-CN-XiaoxiaoNeural", "Edge · 晓晓（温暖自然）"),
            ("edge.zh-CN-YunxiNeural", "Edge · 云希（清朗男声）"),
            ("edge.zh-CN-YunjianNeural", "Edge · 云健（沉稳说书）"),
            ("edge.zh-CN-XiaoyiNeural", "Edge · 晓伊（柔和叙事）"),
        };
        foreach (var sp in _library.Kokoro.GetSpeakers("zh-Hans"))
            zhVoices.Add((sp.Id, $"Kokoro 离线 · {sp.Name}"));

        var zhCombo = new ComboBox { Width = 284 };
        foreach (var (_, lbl) in zhVoices) zhCombo.Items.Add(lbl);
        int zhIdx = zhVoices.FindIndex(v => v.Id == _library.Prefs.VoiceId);
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
        PageBodyStack.Children.Add(new FluentSettingsCard("中文默认朗读音色", "支持离线 Kokoro 与微软 Edge 神经语音", "headphones", zhCombo, dark: dark));

        var enVoices = new List<(string Id, string Label)>
        {
            ("", "自动推荐（离线 Kokoro / Edge 在线）"),
            ("edge.en-US-JennyNeural", "Edge · Jenny (US Natural)"),
            ("edge.en-US-AriaNeural", "Edge · Aria (US Expressive)"),
            ("edge.en-US-GuyNeural", "Edge · Guy (US Male)"),
            ("edge.en-GB-SoniaNeural", "Edge · Sonia (UK Female)"),
        };
        foreach (var sp in _library.Kokoro.GetSpeakers("en"))
            enVoices.Add((sp.Id, $"Kokoro 离线 · {sp.Name}"));

        var enCombo = new ComboBox { Width = 284 };
        foreach (var (_, lbl) in enVoices) enCombo.Items.Add(lbl);
        int enIdx = enVoices.FindIndex(v => v.Id == _library.Prefs.VoiceIdEn);
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
        PageBodyStack.Children.Add(new FluentSettingsCard("英文默认朗读音色", "读英文原版书或英汉双语时自动切换该音色", "globe", enCombo, dark: dark));

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
            "本地 / 云端扩展声源",
            liveLocalCount > 0 ? $"已连接 {liveLocalCount} 个本地神经语音引擎" : "支持探测本机 CosyVoice、IndexTTS、FishSpeech 与多家云端 TTS",
            "server",
            scanVoiceBtn,
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
}
