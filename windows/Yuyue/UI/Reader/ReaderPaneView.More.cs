using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Reader;

namespace Yuyue.UI.Reader;

/// <summary>
/// macOS-parity reader「更多」card (MacReaderMorePopover.swift) and the 译 mode popover (MacReaderTranslatePopover):
/// Day/Night polarity, window brightness with Follow-System, Eye-Care warmth, page-turn animation, immersive &amp;
/// dialogue-bubble switches, and deduplicated book tools with right-aligned status hints.
/// </summary>
public sealed partial class ReaderPaneView
{
    private Flyout? _moreFlyout;

    private bool CanTranslateBook() => _detail.Summary.Format != BookFormat.Podcast;

    // ---------- Brightness & Eye-Care overlays ----------
    private void ApplyReaderLightOverlays(bool paperDark, bool eink)
    {
        bool eyeCare = _library.Prefs.ReaderEyeCare && !eink;
        EyeCareOverlay.Opacity = eyeCare ? (paperDark ? 0.08 : 0.12) : 0;

        double dim = 0;
        if (!_library.Prefs.ReaderBrightnessFollow && !eink)
            dim = Math.Max(0.0, (1.0 - Math.Clamp(_library.Prefs.ReaderBrightness, 0.35, 1.0)) * 0.62);
        DimOverlay.Opacity = dim;
    }

    private void RefreshReaderLightOverlays()
    {
        var resolved = PaperAppearance.Resolve(_library, _settings);
        bool eink = resolved.Preset == Paper.Eink || _library.Prefs.GlobalEinkMode;
        ApplyReaderLightOverlays(CurrentPaperChrome?.IsDark ?? false, eink);
    }

    // ---------- 更多 card ----------
    private void MoreBtn_Click(object sender, RoutedEventArgs e) => ShowMoreCard();

    private void ShowMoreCard()
    {
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void RebuildMore()
        {
            double prevY = (host.Content as ScrollViewer)?.VerticalOffset ?? 0;
            bool dark = CurrentPaperChrome?.IsDark ?? ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
            var p = ThemeHelper.Palette(dark);
            var nextScroller = BuildMoreCardContent(() => _moreFlyout?.Hide(), RebuildMore);
            host.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
            host.Content = nextScroller;
            if (VisualTreeHelper.GetParent(host) is FlyoutPresenter fp)
            {
                fp.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
                fp.Background = ThemeHelper.Brush(p.Card);
                fp.BorderBrush = ThemeHelper.Brush(p.Line);
            }
            if (prevY > 0.5)
            {
                nextScroller.Loaded += (_, _) =>
                {
                    nextScroller.UpdateLayout();
                    nextScroller.ChangeView(null, prevY, null, disableAnimation: true);
                };
            }
        }
        RebuildMore();
        _moreFlyout = new Flyout
        {
            ShouldConstrainToRootBounds = true,
            FlyoutPresenterStyle = (Style)Application.Current.Resources["YYFloatFlyoutPresenterStyle"],
            Content = host,
        };
        _moreFlyout.ShowAt(MoreBtn);
    }

    private ScrollViewer BuildMoreCardContent(Action closeFlyout, Action? refreshCard = null)
    {
        bool dark = CurrentPaperChrome?.IsDark ?? ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var resolved = PaperAppearance.Resolve(_library, _settings);
        bool eink = resolved.Preset == Paper.Eink || _library.Prefs.GlobalEinkMode;

        var root = new StackPanel
        {
            Spacing = 2,
            Width = 300,
            Padding = new Thickness(12, 10, 12, 10),
            Background = ThemeHelper.Brush(p.Card),
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
        };
        ThemeHelper.ApplyControlThemeResources(root.Resources, dark, _library.Prefs.Tint);

        Border Divider() => new() { Height = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(-2, 4, -2, 4) };

        Grid ToggleRow(string icon, string title, string? hint, bool isOn, Action<bool> onToggle)
        {
            var g = new Grid { ColumnSpacing = 8, Padding = new Thickness(8, 1, 4, 1), MinHeight = 30 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = YYIcons.Make(icon, 13.5, ThemeHelper.Brush(p.T2));
            ic.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(ic);
            var tb = new TextBlock { Text = L10n.T(title), FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tb, 1);
            g.Children.Add(tb);
            if (!string.IsNullOrEmpty(hint))
            {
                var h = new TextBlock { Text = hint, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(h, 2);
                g.Children.Add(h);
            }
            var sw = new ToggleSwitch { IsOn = isOn };
            sw.Toggled += (_, _) => onToggle(sw.IsOn);
            Grid.SetColumn(sw, 3);
            g.Children.Add(sw);
            return g;
        }

        Button ActionRow(string icon, string title, string? hint, Action onClick)
        {
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = YYIcons.Make(icon, 13.5, ThemeHelper.Brush(p.T2));
            ic.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(ic);
            var tb = new TextBlock { Text = L10n.T(title), FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(tb, 1);
            g.Children.Add(tb);
            if (!string.IsNullOrEmpty(hint))
            {
                var h = L10n.Skip(new TextBlock { Text = hint, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(h, 2);
                g.Children.Add(h);
            }
            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 5, 8, 5),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = g,
            };
            b.Click += (_, _) => { closeFlyout(); onClick(); };
            return b;
        }

        // Section 1: Day / Night + Brightness + Eye-Care
        if (!eink)
        {
            root.Children.Add(YYChrome.MakeSegmentedBar(
                [("day", "日间"), ("night", "夜间")],
                resolved.IsDark ? "night" : "day",
                v =>
                {
                    bool wantDark = v == "night";
                    if (PaperAppearance.Resolve(_library, _settings).IsDark != wantDark)
                    {
                        StatusPaperToggle_Click(StatusPaperBtn, new RoutedEventArgs());
                        refreshCard?.Invoke();
                    }
                },
                dark,
                fontSize: 12,
                height: 30));

            var brHead = new Grid { Margin = new Thickness(2, 8, 0, 0) };
            brHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            brHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            brHead.Children.Add(new TextBlock { Text = L10n.T("屏幕亮度"), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
            var followCb = new CheckBox
            {
                Content = L10n.T("跟随系统"),
                IsChecked = _library.Prefs.ReaderBrightnessFollow,
                FontSize = 11.5,
                MinWidth = 0,
                MinHeight = 24,
                Padding = new Thickness(6, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(followCb, 1);
            brHead.Children.Add(followCb);
            root.Children.Add(brHead);

            var brRow = new Grid { ColumnSpacing = 10, Margin = new Thickness(2, 0, 2, 0) };
            brRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            brRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            brRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            var sunIc = YYIcons.Make("sun.max", 14, ThemeHelper.Brush(p.T2));
            sunIc.VerticalAlignment = VerticalAlignment.Center;
            brRow.Children.Add(sunIc);
            var brSlider = new Slider
            {
                Minimum = 35,
                Maximum = 100,
                StepFrequency = 1,
                Value = Math.Round(Math.Clamp(_library.Prefs.ReaderBrightness, 0.35, 1.0) * 100),
                IsThumbToolTipEnabled = false,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = _library.Prefs.ReaderBrightnessFollow ? 0.55 : 1.0,
            };
            Grid.SetColumn(brSlider, 1);
            brRow.Children.Add(brSlider);
            var brVal = new TextBlock
            {
                Text = _library.Prefs.ReaderBrightnessFollow ? L10n.T("自动") : $"{brSlider.Value:0}%",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T1),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(brVal, 2);
            brRow.Children.Add(brVal);
            root.Children.Add(brRow);

            brSlider.ValueChanged += (_, args) =>
            {
                if (_library.Prefs.ReaderBrightnessFollow)
                {
                    _library.Prefs.ReaderBrightnessFollow = false;
                    followCb.IsChecked = false;
                    brSlider.Opacity = 1.0;
                }
                _library.Prefs.ReaderBrightness = Math.Clamp(args.NewValue / 100.0, 0.35, 1.0);
                brVal.Text = $"{args.NewValue:0}%";
                RefreshReaderLightOverlays();
            };
            brSlider.PointerCaptureLost += (_, _) => _library.SavePrefs();
            followCb.Click += (_, _) =>
            {
                _library.Prefs.ReaderBrightnessFollow = followCb.IsChecked == true;
                _library.SavePrefs();
                brSlider.Opacity = _library.Prefs.ReaderBrightnessFollow ? 0.55 : 1.0;
                brVal.Text = _library.Prefs.ReaderBrightnessFollow ? L10n.T("自动") : $"{brSlider.Value:0}%";
                RefreshReaderLightOverlays();
            };

            root.Children.Add(ToggleRow("eye", "护眼柔光", null, _library.Prefs.ReaderEyeCare, on =>
            {
                _library.Prefs.ReaderEyeCare = on;
                _library.SavePrefs();
                RefreshReaderLightOverlays();
            }));
            root.Children.Add(Divider());
        }

        // Section 2: Reading modes & switches
        if (_settings.Mode == LayoutMode.Paginated && !eink)
        {
            var ptRow = new Grid { ColumnSpacing = 8, Padding = new Thickness(8, 2, 0, 2), MinHeight = 34 };
            ptRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            ptRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ptRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ptIc = YYIcons.Make("book-open", 13.5, ThemeHelper.Brush(p.T2));
            ptIc.VerticalAlignment = VerticalAlignment.Center;
            ptRow.Children.Add(ptIc);
            var ptTb = new TextBlock { Text = L10n.T("翻页动画"), FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(ptTb, 1);
            ptRow.Children.Add(ptTb);
            var ptSeg = YYChrome.MakeSegmentedBar(
                [("slide", "滑动"), ("none", "无")],
                _library.Prefs.ImmersiveNoAnimation ? "none" : "slide",
                v =>
                {
                    _library.Prefs.ImmersiveNoAnimation = v == "none";
                    _library.SavePrefs();
                    PushGeometryToReader();
                },
                dark,
                fontSize: 11,
                height: 26);
            ptSeg.Width = 120;
            Grid.SetColumn(ptSeg, 2);
            ptRow.Children.Add(ptSeg);
            root.Children.Add(ptRow);
        }

        if (_settings.Mode == LayoutMode.Paginated)
        {
            var soundPickerWrap = new Border
            {
                Padding = new Thickness(8, 2, 4, 6),
                Visibility = _library.Prefs.PageTurnSound ? Visibility.Visible : Visibility.Collapsed,
                Child = PageTurnSoundUi.BuildSoundPicker(
                    _library,
                    dark,
                    showSubtitle: false,
                    compact: true,
                    showVolume: true,
                    getXamlRoot: () => RootGrid.XamlRoot,
                    eink: eink),
            };
            root.Children.Add(ToggleRow("speaker", "翻页音效", null, _library.Prefs.PageTurnSound, on =>
            {
                _library.Prefs.PageTurnSound = on;
                _library.SavePrefs();
                soundPickerWrap.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on) _library.PageTurnSounds.PreviewSelection(_library.Prefs.PageTurnSoundStyle);
            }));
            root.Children.Add(soundPickerWrap);
        }

        root.Children.Add(ToggleRow("fullscreen", "沉浸阅读", "F11", _isFullScreen, on =>
        {
            closeFlyout();
            if (on != _isFullScreen) _ownerWindow.ToggleFullScreen();
        }));
        root.Children.Add(ToggleRow("bubble-lines", "对话气泡", null, _bubblesOn, on =>
        {
            _bubblesOn = on;
            try { _library.Core.SetBookBubbles(_bookId, on); } catch { }
            _ = ApplySectionBubblesAsync();
        }));
        root.Children.Add(Divider());

        // Section 3: Book tools
        int activeRules = 0;
        try
        {
            activeRules = _library.Core.ReplaceRules(_bookId).Count(r => r.Enabled)
                + _library.Core.ReplaceRules(null).Count(r => r.Enabled);
        }
        catch { }
        int pct = (int)Math.Round(Math.Clamp(CalculateBookFraction(_currentSection, CurrentReadingSentence()), 0, 1) * 100);
        string formatName = _detail.Summary.Format.ToString().ToUpperInvariant();

        root.Children.Add(ActionRow("bookmark", "加书签 / 拿掉书签", "Ctrl+D", ToggleBookmarkAtCurrentPosition));
        root.Children.Add(ActionRow("list", "跳转章节与页码…", "Ctrl+G", () => StatusProgress_Click(StatusProgressBtn, new RoutedEventArgs())));
        root.Children.Add(ActionRow("clock-history", "前情提要", null, () => _ = ShowRecapDialogAsync()));
        root.Children.Add(ActionRow("filter", "替换规则…", activeRules > 0 ? L10n.T($"{activeRules} 条生效") : null, async () =>
        {
            if (RootGrid.XamlRoot is { } xr && await BookToolsDialogs.ShowReplaceRulesDialogAsync(xr, _library, _bookId, _detail.Summary.Title))
                LoadSection(_currentSection, forceReload: true);
        }));
        if (_detail.Summary.Format == BookFormat.Txt)
        {
            root.Children.Add(ActionRow("doc-lines", "编码和目录…", null, async () =>
            {
                if (RootGrid.XamlRoot is { } xr && await BookToolsDialogs.ShowTxtLayoutDialogAsync(xr, _library, _bookId, _detail.Summary.Title))
                {
                    _detail = _library.GetBookDetail(_bookId);
                    LoadSection(_currentSection, forceReload: true);
                }
            }));
        }
        if (_isTextVersion)
        {
            root.Children.Add(ActionRow("highlighter", "整理错字…", null, async () =>
            {
                if (RootGrid.XamlRoot is { } xr)
                    await global::Yuyue.Audiobook.AlignCheckAndPolishService.ShowTextPolishDialogAsync(xr, _library, _bookId, () => { if (!_disposed) LoadSection(_currentSection); });
            }));
        }
        root.Children.Add(ActionRow("share-up", "导出笔记 Markdown…", null, () => _ = ExportBookMarkdownAsync()));
        root.Children.Add(ActionRow("book", "导出带笔记 EPUB…", null, () => _ = ExportAnnotatedEpubAsync()));
        root.Children.Add(ActionRow("info-circle", "书的信息", $"{formatName} · {pct}%", OpenBookInfo));
        if (!_isSecondarySplitPane)
        {
            root.Children.Add(Divider());
            root.Children.Add(ActionRow("detach", "撕出到独立窗口", null, () => RequestDetachWindow?.Invoke()));
            root.Children.Add(ActionRow("gear", "阅读设置…", "Ctrl+,", () => _ownerWindow.ShowSettings("reading")));
        }

        return new ScrollViewer { MaxHeight = 640, Content = root };
    }

    private void OpenBookInfo()
    {
        App.CurrentApp.ShowShelf();
        App.CurrentApp.OpenBookRecordInPrimaryShelf(_bookId);
    }

    private async Task ExportBookMarkdownAsync()
    {
        string? path = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, $"{_detail.Summary.Title}（笔记）", [("Markdown", [".md"])]);
        if (string.IsNullOrEmpty(path) || _disposed) return;
        try
        {
            var book = _library.GetBooks().FirstOrDefault(b => b.Id == _bookId) ?? _detail.Summary;
            await File.WriteAllTextAsync(path, NoteExporter.BuildBookMarkdown(_library, book));
            ShowReaderAlert($"已导出：{Path.GetFileName(path)}");
        }
        catch (Exception ex) { ShowReaderAlert($"导出失败：{ex.Message}"); }
    }

    private async Task ExportAnnotatedEpubAsync()
    {
        string? path = await NativePickerHelper.PickSaveFileAsync(_ownerWindow, $"{_detail.Summary.Title}（带笔记）", [("EPUB", [".epub"])]);
        if (string.IsNullOrEmpty(path) || _disposed) return;
        try
        {
            await _library.NoteExports.ExportAnnotatedEpubAsync(_bookId, path);
            ShowReaderAlert("导出好了");
        }
        catch (Exception ex) { ShowReaderAlert($"导出失败：{ex.Message}"); }
    }

    // ---------- 译 mode popover ----------
    private void ShowTranslateModeFlyout()
    {
        Flyout? fly = null;
        var root = BuildTranslateModeContent(() => fly?.Hide());
        fly = new Flyout
        {
            ShouldConstrainToRootBounds = true,
            FlyoutPresenterStyle = (Style)Application.Current.Resources["YYFloatFlyoutPresenterStyle"],
            Content = root,
        };
        fly.ShowAt(TranslateBtn);
    }

    private StackPanel BuildTranslateModeContent(Action closeFlyout)
    {
        bool dark = CurrentPaperChrome?.IsDark ?? ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        string current = !_translationsActive ? "off" : (_translateOnly ? "only" : "bilingual");

        var root = new StackPanel
        {
            Spacing = 2,
            Width = 220,
            Padding = new Thickness(8),
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
        };
        ThemeHelper.ApplyControlThemeResources(root.Resources, dark, _library.Prefs.Tint);
        root.Children.Add(new TextBlock
        {
            Text = L10n.T(IsForeignBook() ? "外文译中文" : "文言 / 古文译白话"),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            Margin = new Thickness(8, 2, 8, 4),
        });

        foreach (var (mode, label, icon) in new[] { ("off", "原文", "doc-lines"), ("bilingual", "双语对照", "translate"), ("only", "仅译文", "text-quote") })
        {
            bool on = current == mode;
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = YYIcons.Make(icon, 13.5, ThemeHelper.Brush(on ? tc.Accent : p.T2));
            ic.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(ic);
            var tb = new TextBlock { Text = L10n.T(label), FontSize = 12.5, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = ThemeHelper.Brush(on ? tc.Accent : p.T1), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tb, 1);
            g.Children.Add(tb);
            if (on)
            {
                var chk = new TextBlock { Text = "✓", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = ThemeHelper.Brush(tc.Accent), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(chk, 2);
                g.Children.Add(chk);
            }
            var b = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(8, 7, 8, 7),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(0),
                Background = on ? ThemeHelper.Brush(tc.Soft) : new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = false,
                Content = g,
            };
            string modeCap = mode;
            b.Click += async (_, _) =>
            {
                closeFlyout();
                await SetTranslateModeAsync(modeCap, persist: true);
            };
            root.Children.Add(b);
        }

        return root;
    }

    /// <summary>Sets 原文 (off) / 双语对照 (bilingual) / 仅译文 (only) for this book, mirroring macOS setTranslateMode.</summary>
    private async Task SetTranslateModeAsync(string mode, bool persist)
    {
        bool wasActive = _translationsActive;
        bool wantActive = mode is "bilingual" or "only";
        bool wantOnly = mode == "only";

        if (wantActive && !_library.Companion.IsConfigured)
        {
            ShowReaderAlert("整章翻译需要先在「设置 → 书伴」配置模型服务", "去配置", () => _ownerWindow.ShowSettings("companion"), seconds: 6);
            return;
        }

        _translationsActive = wantActive;
        _translateOnly = wantOnly;
        if (persist)
        {
            _library.Prefs.BookTranslateModes[_bookId] = wantActive ? mode : "off";
            _library.Prefs.BookTranslationsActive[_bookId] = wantActive;
            _library.SavePrefs();
        }
        ApplyPaperAndChrome();

        if (!wantActive)
        {
            try { _translateCts?.Cancel(); } catch { }
            if (_pager.IsInitialized) await _pager.Current.SetTranslationsAsync(null);
            return;
        }

        if (wasActive && _pager.IsInitialized)
        {
            await _pager.Current.SetTranslationOnlyAsync(wantOnly);
            return;
        }
        await ApplyCurrentSectionTranslationsAsync();
    }
}
