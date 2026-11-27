using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.UI;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Reader;

namespace Yuyue.UI.Reader;

/// <summary>
/// Shared WinUI 3 Fluent 2 page-turn sound selector (3-column preset &amp; custom grid + volume slider)
/// and Custom Page-Turn Sound studio dialog (microphone capture + audio file import + waveform preview).
/// 1:1 port of macOS <c>PageTurnSoundStyleGrid</c> and <c>CustomPageTurnSoundSheet</c> (<c>PageTurn.swift</c>).
/// </summary>
public static class PageTurnSoundUi
{
    public static FrameworkElement BuildSoundPicker(
        LibraryService library,
        bool dark,
        bool showSubtitle,
        bool compact,
        bool showVolume,
        Action? onChanged = null,
        Func<XamlRoot?>? getXamlRoot = null,
        bool eink = false)
    {
        var host = new StackPanel { Spacing = compact ? 8 : 10 };
        bool disposed = false;

        void Rebuild()
        {
            if (disposed) return;
            host.Children.Clear();
            var p = ThemeHelper.Palette(dark);
            var tc = ThemeHelper.TintColors(library.Prefs.Tint, dark);
            var accent = eink ? p.T1 : tc.Accent;
            var accentBg = eink ? p.Inset : tc.AccentSoft;
            var corner = new CornerRadius(compact ? 6 : 6);
            double tileHeight = showSubtitle ? 46 : (compact ? 30 : 34);
            string activeKey = library.PageTurnSounds.ResolveActiveSelectionKey();

            // Collect all tiles: 6 built-ins + saved custom clips + "+ 自定义"
            var tiles = new List<FrameworkElement>();

            foreach (var info in PageTurnSoundSynthesizer.AllStyles)
            {
                bool on = string.Equals(activeKey, info.Key, StringComparison.Ordinal);
                var capInfo = info;
                var btn = MakeTileButton(
                    icon: capInfo.Icon,
                    title: L10n.T(capInfo.Title),
                    subtitle: showSubtitle ? L10n.T(capInfo.Subtitle) : null,
                    on: on,
                    dashed: false,
                    accent: accent,
                    accentBg: accentBg,
                    p: p,
                    corner: corner,
                    height: tileHeight,
                    compact: compact,
                    eink: eink);
                btn.Click += (_, _) =>
                {
                    library.Prefs.PageTurnSoundStyle = capInfo.Key;
                    library.SavePrefs();
                    library.PageTurnSounds.Preview(capInfo.Style);
                    Rebuild();
                    onChanged?.Invoke();
                };
                ToolTipService.SetToolTip(btn, $"{L10n.T(capInfo.Title)} · {L10n.T(capInfo.Subtitle)}");
                tiles.Add(btn);
            }

            foreach (var custom in library.PageTurnSounds.Store.Items)
            {
                bool on = string.Equals(activeKey, custom.SelectionKey, StringComparison.Ordinal);
                var capCustom = custom;
                var btn = MakeTileButton(
                    icon: "waveform-ecg",
                    title: capCustom.Name,
                    subtitle: showSubtitle ? FormatDurationLabel(capCustom.DurationMs) : null,
                    on: on,
                    dashed: false,
                    accent: accent,
                    accentBg: accentBg,
                    p: p,
                    corner: corner,
                    height: tileHeight,
                    compact: compact,
                    eink: eink);
                btn.Click += (_, _) =>
                {
                    library.Prefs.PageTurnSoundStyle = capCustom.SelectionKey;
                    library.SavePrefs();
                    library.PageTurnSounds.PreviewSelection(capCustom.SelectionKey);
                    Rebuild();
                    onChanged?.Invoke();
                };

                var ctx = new MenuFlyout();
                var renameMi = new MenuFlyoutItem { Text = L10n.T("重命名") };
                renameMi.Click += async (_, _) =>
                {
                    var xr = getXamlRoot?.Invoke() ?? host.XamlRoot;
                    if (xr is null) return;
                    string? next = await YYChrome.ShowPromptAsync(
                        xr,
                        dark,
                        "重命名",
                        placeholder: "名字",
                        initialText: capCustom.Name,
                        confirmText: "保存",
                        cancelText: "取消",
                        tint: library.Prefs.Tint);
                    if (!string.IsNullOrWhiteSpace(next) && library.PageTurnSounds.Store.Rename(capCustom.Id, next))
                    {
                        Rebuild();
                        onChanged?.Invoke();
                    }
                };
                var delMi = new MenuFlyoutItem { Text = L10n.T("删除") };
                delMi.Click += (_, _) =>
                {
                    bool wasSelected = string.Equals(library.Prefs.PageTurnSoundStyle, capCustom.SelectionKey, StringComparison.Ordinal);
                    library.PageTurnSounds.Store.Delete(capCustom.Id);
                    if (wasSelected)
                    {
                        library.Prefs.PageTurnSoundStyle = PageTurnSoundSynthesizer.ToKey(PageTurnSoundStyle.SoftPaper);
                        library.SavePrefs();
                    }
                    Rebuild();
                    onChanged?.Invoke();
                };
                ctx.Items.Add(renameMi);
                ctx.Items.Add(delMi);
                btn.ContextFlyout = ctx;
                ToolTipService.SetToolTip(btn, $"{capCustom.Name} ({FormatDurationLabel(capCustom.DurationMs)}) · {L10n.T("右键可重命名或删除")}");
                tiles.Add(btn);
            }

            var addBtn = MakeTileButton(
                icon: "plus",
                title: L10n.T("自定义"),
                subtitle: showSubtitle ? L10n.T("导入或实录") : null,
                on: false,
                dashed: true,
                accent: accent,
                accentBg: accentBg,
                p: p,
                corner: corner,
                height: tileHeight,
                compact: compact,
                eink: eink);
            addBtn.Click += async (_, _) =>
            {
                var xr = getXamlRoot?.Invoke() ?? host.XamlRoot;
                if (xr is null) return;
                await ShowCustomSoundDialogAsync(xr, library, dark, savedKey =>
                {
                    library.Prefs.PageTurnSoundStyle = savedKey;
                    library.SavePrefs();
                    Rebuild();
                    onChanged?.Invoke();
                });
                // Also refresh in case user deleted/renamed inside dialog
                Rebuild();
                onChanged?.Invoke();
            };
            ToolTipService.SetToolTip(addBtn, L10n.T("自定义翻页音效"));
            tiles.Add(addBtn);

            // 3-column grid
            var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
            for (int c = 0; c < 3; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int rows = (tiles.Count + 2) / 3;
            for (int r = 0; r < rows; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < tiles.Count; i++)
            {
                Grid.SetRow(tiles[i], i / 3);
                Grid.SetColumn(tiles[i], i % 3);
                grid.Children.Add(tiles[i]);
            }
            host.Children.Add(grid);

            if (showVolume)
            {
                host.Children.Add(BuildVolumeRow(library, p, compact, onChanged));
            }
        }

        void OnStoreChanged()
        {
            if (disposed) return;
            try
            {
                host.DispatcherQueue?.TryEnqueue(Rebuild);
            }
            catch { }
        }

        library.PageTurnSounds.Store.Changed += OnStoreChanged;
        host.Unloaded += (_, _) =>
        {
            disposed = true;
            library.PageTurnSounds.Store.Changed -= OnStoreChanged;
        };

        Rebuild();
        return host;
    }

    private static Button MakeTileButton(
        string icon,
        string title,
        string? subtitle,
        bool on,
        bool dashed,
        Color accent,
        Color accentBg,
        YYPalette p,
        CornerRadius corner,
        double height,
        bool compact,
        bool eink)
    {
        var inner = new StackPanel
        {
            Spacing = subtitle is not null ? 1 : 0,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var iconColor = on ? accent : p.T2;
        var titleColor = on ? (eink ? p.T1 : accent) : (dashed ? p.T2 : p.T1);
        var ic = YYIcons.Make(icon, compact ? 10.5 : 11.5, ThemeHelper.Brush(iconColor));
        ic.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(ic);

        var titleTb = L10n.Skip(new TextBlock
        {
            Text = title,
            FontSize = compact ? 11.5 : 12,
            FontWeight = on ? FontWeights.SemiBold : FontWeights.Medium,
            Foreground = ThemeHelper.Brush(titleColor),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
        });
        top.Children.Add(titleTb);
        inner.Children.Add(top);

        if (!string.IsNullOrEmpty(subtitle))
        {
            var subColor = on
                ? (eink ? p.T2 : Color.FromArgb(215, accent.R, accent.G, accent.B))
                : p.T3;
            inner.Children.Add(L10n.Skip(new TextBlock
            {
                Text = subtitle,
                FontSize = 10,
                Foreground = ThemeHelper.Brush(subColor),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
            }));
        }

        Color bg = on
            ? accentBg
            : (dashed ? Color.FromArgb(140, p.Inset.R, p.Inset.G, p.Inset.B) : p.Inset);
        Color borderCol = on
            ? (eink ? p.T1 : Color.FromArgb(110, accent.R, accent.G, accent.B))
            : (dashed ? Color.FromArgb(145, p.Line.R, p.Line.G, p.Line.B) : Color.FromArgb(90, p.Line.R, p.Line.G, p.Line.B));

        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Height = height,
            MinHeight = height,
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = corner,
            Background = ThemeHelper.Brush(bg),
            BorderBrush = ThemeHelper.Brush(borderCol),
            BorderThickness = new Thickness(on && eink ? 1.2 : 1),
            Content = inner,
        };
        return btn;
    }

    private static Grid BuildVolumeRow(
        LibraryService library,
        YYPalette p,
        bool compact,
        Action? onChanged)
    {
        double clamped = Math.Clamp(library.Prefs.PageTurnSoundVolume, PageTurnSoundService.MinVolume, PageTurnSoundService.MaxVolume);
        int pctInit = (int)Math.Round(clamped * 100.0);

        var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(2, 0, 2, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        var spk = YYIcons.Make("speaker", compact ? 11.5 : 12.5, ThemeHelper.Brush(p.T2));
        spk.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(spk);

        var pctTb = L10n.Skip(new TextBlock
        {
            Text = $"{pctInit}%",
            FontSize = compact ? 11 : 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(p.T2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var slider = new Slider
        {
            Minimum = 15,
            Maximum = 100,
            StepFrequency = 5,
            Value = pctInit,
            IsThumbToolTipEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };

        CancellationTokenSource? previewCts = null;
        slider.ValueChanged += (_, args) =>
        {
            int nextPct = Math.Clamp((int)Math.Round(args.NewValue), 15, 100);
            double nextVol = nextPct / 100.0;
            pctTb.Text = $"{nextPct}%";
            library.Prefs.PageTurnSoundVolume = nextVol;
            library.SavePrefs();
            onChanged?.Invoke();

            previewCts?.Cancel();
            previewCts?.Dispose();
            var cts = new CancellationTokenSource();
            previewCts = cts;
            _ = Task.Delay(170, cts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                    library.PageTurnSounds.PreviewSelection(library.Prefs.PageTurnSoundStyle, nextVol);
            }, TaskScheduler.Default);
        };

        Grid.SetColumn(slider, 1);
        Grid.SetColumn(pctTb, 2);
        row.Children.Add(slider);
        row.Children.Add(pctTb);
        return row;
    }

    public static string FormatDurationLabel(int durationMs)
    {
        double sec = Math.Max(10, durationMs) / 1000.0;
        return $"{sec:0.00} {L10n.T("秒")}";
    }

    /// <summary>
    /// Focused Fluent 2 dialog for recording a short desk/book page-turn clip or importing an audio file,
    /// previewing its normalized waveform, and saving it into <see cref="CustomPageTurnSoundStore"/>.
    /// </summary>
    public static async Task ShowCustomSoundDialogAsync(
        XamlRoot xr,
        LibraryService library,
        bool dark,
        Action<string> onSaved)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(library.Prefs.Tint, dark);

        ProcessedCustomSound? draft = null;
        string draftName = "";
        string? errorMsg = null;
        bool busy = false;
        bool isRecording = false;
        double recSeconds = 0;

        MediaCapture? capture = null;
        InMemoryRandomAccessStream? recStream = null;
        var recTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(50) };

        var bodyHost = new StackPanel { Spacing = 12, MinWidth = 376, MaxWidth = 420 };
        ContentDialog? dlgRef = null;

        void DisposeCapture()
        {
            recTimer.Stop();
            try { recStream?.Dispose(); } catch { }
            recStream = null;
            try { capture?.Dispose(); } catch { }
            capture = null;
            isRecording = false;
        }

        async Task StopRecordingAsync()
        {
            if (!isRecording || capture is null || recStream is null) return;
            recTimer.Stop();
            isRecording = false;
            busy = true;
            RebuildDialogBody();

            try
            {
                await capture.StopRecordAsync();
                byte[] rawWav;
                recStream.Seek(0);
                using (var reader = new DataReader(recStream.GetInputStreamAt(0)))
                {
                    uint len = (uint)recStream.Size;
                    await reader.LoadAsync(len);
                    rawWav = new byte[len];
                    reader.ReadBytes(rawWav);
                }

                DisposeCapture();

                var processed = await Task.Run(() =>
                {
                    if (!PageTurnSoundSynthesizer.TryDecodePcmWav(rawWav, out float[] samples, out int sr))
                        throw new CustomSoundException(CustomSoundErrorKind.Unreadable);
                    return PageTurnSoundSynthesizer.TrimAndNormalize(samples, sr);
                });

                draft = processed;
                draftName = $"{L10n.T("实录翻页")} {library.PageTurnSounds.Store.Items.Count + 1}";
                errorMsg = null;
                library.PageTurnSounds.PreviewWav(processed.WavData);
            }
            catch (CustomSoundException cex)
            {
                errorMsg = cex.UserMessage;
            }
            catch (Exception ex)
            {
                errorMsg = $"{L10n.T("录音处理失败")}：{ex.Message}";
            }
            finally
            {
                DisposeCapture();
                busy = false;
                RebuildDialogBody();
            }
        }

        recTimer.Tick += (_, _) =>
        {
            if (!isRecording) { recTimer.Stop(); return; }
            recSeconds += 0.05;
            if (recSeconds >= 2.5)
            {
                _ = StopRecordingAsync();
            }
            else
            {
                RebuildDialogBody();
            }
        };

        async Task StartRecordingAsync()
        {
            if (busy) return;
            errorMsg = null;
            try
            {
                capture = new MediaCapture();
                await capture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio,
                });
                recStream = new InMemoryRandomAccessStream();
                var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
                profile.Audio.SampleRate = PageTurnSoundSynthesizer.SampleRate;
                profile.Audio.ChannelCount = 1;
                profile.Audio.BitsPerSample = 16;
                await capture.StartRecordToStreamAsync(profile, recStream);
                recSeconds = 0;
                isRecording = true;
                recTimer.Start();
                RebuildDialogBody();
            }
            catch (Exception ex)
            {
                DisposeCapture();
                errorMsg = $"{L10n.T("无法打开麦克风")}：{ex.Message}";
                RebuildDialogBody();
            }
        }

        async Task ImportFileAsync()
        {
            if (busy || isRecording) return;
            string? path = await NativePickerHelper.PickSingleFileAsync(
                (Window?)null,
                [".wav", ".mp3", ".m4a", ".flac", ".ogg", ".wma"],
                Windows.Storage.Pickers.PickerLocationId.MusicLibrary);
            if (string.IsNullOrWhiteSpace(path)) return;

            busy = true;
            errorMsg = null;
            RebuildDialogBody();
            try
            {
                var processed = await library.PageTurnSounds.Store.ImportAudioFileAsync(path);
                draft = processed;
                string stem = Path.GetFileNameWithoutExtension(path).Trim();
                draftName = string.IsNullOrEmpty(stem)
                    ? $"{L10n.T("自定义")} {library.PageTurnSounds.Store.Items.Count + 1}"
                    : (stem.Length > CustomPageTurnSoundStore.MaxCustomNameLength ? stem[..CustomPageTurnSoundStore.MaxCustomNameLength] : stem);
                library.PageTurnSounds.PreviewWav(processed.WavData);
            }
            catch (CustomSoundException cex)
            {
                errorMsg = cex.UserMessage;
            }
            catch (Exception ex)
            {
                errorMsg = $"{L10n.T("导入失败")}：{ex.Message}";
            }
            finally
            {
                busy = false;
                RebuildDialogBody();
            }
        }

        void RebuildDialogBody()
        {
            bodyHost.Children.Clear();
            if (dlgRef is not null)
            {
                dlgRef.IsPrimaryButtonEnabled = draft is not null && !busy && !isRecording;
            }

            if (!string.IsNullOrWhiteSpace(errorMsg))
            {
                var errCard = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8, 10, 8),
                    Background = ThemeHelper.Brush(Color.FromArgb(24, p.Danger.R, p.Danger.G, p.Danger.B)),
                    BorderBrush = ThemeHelper.Brush(Color.FromArgb(64, p.Danger.R, p.Danger.G, p.Danger.B)),
                    BorderThickness = new Thickness(1),
                    Child = L10n.Skip(new TextBlock
                    {
                        Text = errorMsg,
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.Danger),
                        TextWrapping = TextWrapping.Wrap,
                    }),
                };
                bodyHost.Children.Add(errCard);
            }

            if (draft is { } cur)
            {
                // Draft preview studio
                var previewCard = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 12, 14, 12),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                };
                var prevStack = new StackPanel { Spacing = 10 };

                var topBar = new Grid();
                topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                topBar.Children.Add(new TextBlock
                {
                    Text = L10n.T("已自动裁去前后静音并均衡响度"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T2),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var durBadge = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 2, 7, 2),
                    Background = ThemeHelper.Brush(tc.AccentSoft),
                    Child = L10n.Skip(new TextBlock
                    {
                        Text = FormatDurationLabel(cur.DurationMs),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(tc.Accent),
                    }),
                };
                Grid.SetColumn(durBadge, 1);
                topBar.Children.Add(durBadge);
                prevStack.Children.Add(topBar);

                // Waveform bar visualization
                var waveGrid = new Grid { ColumnSpacing = 2, Height = 44 };
                for (int i = 0; i < cur.Waveform.Length; i++)
                {
                    waveGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    double frac = (i + 0.5) / cur.Waveform.Length;
                    bool inTrim = frac >= cur.TrimStart && frac <= cur.TrimEnd;
                    var bar = new Border
                    {
                        Height = Math.Clamp(cur.Waveform[i] * 40.0, 4.0, 40.0),
                        CornerRadius = new CornerRadius(1.5),
                        VerticalAlignment = VerticalAlignment.Center,
                        Background = ThemeHelper.Brush(inTrim ? tc.Accent : p.Line),
                    };
                    Grid.SetColumn(bar, i);
                    waveGrid.Children.Add(bar);
                }
                prevStack.Children.Add(waveGrid);

                var actRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var playBtn = new Button
                {
                    Content = MakeIconLabel("play", "试听翻页声", p.T1),
                    Padding = new Thickness(12, 6, 12, 6),
                    CornerRadius = new CornerRadius(6),
                };
                playBtn.Click += (_, _) => library.PageTurnSounds.PreviewWav(cur.WavData);
                var resetBtn = new Button
                {
                    Content = L10n.T("重选或重录"),
                    Padding = new Thickness(12, 6, 12, 6),
                    CornerRadius = new CornerRadius(6),
                };
                resetBtn.Click += (_, _) =>
                {
                    draft = null;
                    errorMsg = null;
                    RebuildDialogBody();
                };
                actRow.Children.Add(playBtn);
                actRow.Children.Add(resetBtn);
                prevStack.Children.Add(actRow);
                previewCard.Child = prevStack;
                bodyHost.Children.Add(previewCard);

                var nameBox = new TextBox
                {
                    Header = L10n.T("音效名称"),
                    PlaceholderText = L10n.T("例如：牛皮纸翻页"),
                    Text = draftName,
                    MaxLength = CustomPageTurnSoundStore.MaxCustomNameLength,
                };
                nameBox.TextChanged += (_, _) => draftName = nameBox.Text ?? "";
                bodyHost.Children.Add(nameBox);
            }
            else
            {
                // Capture & import studio
                var intro = new TextBlock
                {
                    Text = L10n.T("录下身边的翻书声、轻敲桌面声，或导入一段短音频（自动去静音并裁至 0.65 秒以内）。"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T2),
                    TextWrapping = TextWrapping.Wrap,
                };
                bodyHost.Children.Add(intro);

                var actionsGrid = new Grid { ColumnSpacing = 10 };
                actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                actionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var recCardBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Height = 82,
                    CornerRadius = new CornerRadius(6),
                    Background = ThemeHelper.Brush(isRecording ? Color.FromArgb(28, p.Danger.R, p.Danger.G, p.Danger.B) : p.Card),
                    BorderBrush = ThemeHelper.Brush(isRecording ? p.Danger : p.Line),
                    BorderThickness = new Thickness(1),
                    IsEnabled = !busy,
                    Content = new StackPanel
                    {
                        Spacing = 6,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            YYIcons.Make(isRecording ? "stop" : "mic", 18, ThemeHelper.Brush(isRecording ? p.Danger : tc.Accent)),
                            L10n.Skip(new TextBlock
                            {
                                Text = isRecording
                                    ? $"{L10n.T("停止录音")} ({recSeconds:0.0}s)"
                                    : L10n.T("实录翻书声"),
                                FontSize = 12.5,
                                FontWeight = FontWeights.SemiBold,
                                Foreground = ThemeHelper.Brush(isRecording ? p.Danger : p.T1),
                                HorizontalAlignment = HorizontalAlignment.Center,
                            }),
                        },
                    },
                };
                recCardBtn.Click += async (_, _) =>
                {
                    if (isRecording) await StopRecordingAsync();
                    else await StartRecordingAsync();
                };
                Grid.SetColumn(recCardBtn, 0);
                actionsGrid.Children.Add(recCardBtn);

                var importCardBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Height = 82,
                    CornerRadius = new CornerRadius(6),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    IsEnabled = !busy && !isRecording,
                    Content = new StackPanel
                    {
                        Spacing = 6,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            YYIcons.Make("folder", 18, ThemeHelper.Brush(tc.Accent)),
                            new TextBlock
                            {
                                Text = L10n.T("导入音频文件"),
                                FontSize = 12.5,
                                FontWeight = FontWeights.SemiBold,
                                Foreground = ThemeHelper.Brush(p.T1),
                                HorizontalAlignment = HorizontalAlignment.Center,
                            },
                        },
                    },
                };
                importCardBtn.Click += async (_, _) => await ImportFileAsync();
                Grid.SetColumn(importCardBtn, 1);
                actionsGrid.Children.Add(importCardBtn);
                bodyHost.Children.Add(actionsGrid);

                if (busy)
                {
                    bodyHost.Children.Add(new TextBlock
                    {
                        Text = L10n.T("正在处理音频…"),
                        FontSize = 12,
                        Foreground = ThemeHelper.Brush(p.T2),
                    });
                }

                // Existing custom sounds list
                var saved = library.PageTurnSounds.Store.Items;
                if (saved.Count > 0)
                {
                    bodyHost.Children.Add(new TextBlock
                    {
                        Text = L10n.T("已保存的自定义音效"),
                        FontSize = 11.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(p.T3),
                        Margin = new Thickness(0, 4, 0, 0),
                    });
                    var listStack = new StackPanel { Spacing = 6 };
                    foreach (var it in saved)
                    {
                        var cap = it;
                        var row = new Grid { ColumnSpacing = 8 };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                        infoPanel.Children.Add(L10n.Skip(new TextBlock
                        {
                            Text = cap.Name,
                            FontSize = 12.5,
                            FontWeight = FontWeights.Medium,
                            Foreground = ThemeHelper.Brush(p.T1),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        }));
                        infoPanel.Children.Add(L10n.Skip(new TextBlock
                        {
                            Text = FormatDurationLabel(cap.DurationMs),
                            FontSize = 11,
                            Foreground = ThemeHelper.Brush(p.T3),
                        }));
                        row.Children.Add(infoPanel);

                        var playOne = new Button
                        {
                            Content = YYIcons.Make("play", 12, ThemeHelper.Brush(p.T2)),
                            Padding = new Thickness(8, 6, 8, 6),
                            CornerRadius = new CornerRadius(6),
                        };
                        playOne.Click += (_, _) => library.PageTurnSounds.PreviewSelection(cap.SelectionKey);
                        Grid.SetColumn(playOne, 1);
                        row.Children.Add(playOne);

                        var delOne = new Button
                        {
                            Content = YYIcons.Make("trash", 12, ThemeHelper.Brush(p.Danger)),
                            Padding = new Thickness(8, 6, 8, 6),
                            CornerRadius = new CornerRadius(6),
                        };
                        delOne.Click += (_, _) =>
                        {
                            bool wasActive = string.Equals(library.Prefs.PageTurnSoundStyle, cap.SelectionKey, StringComparison.Ordinal);
                            library.PageTurnSounds.Store.Delete(cap.Id);
                            if (wasActive)
                            {
                                library.Prefs.PageTurnSoundStyle = PageTurnSoundSynthesizer.ToKey(PageTurnSoundStyle.SoftPaper);
                                library.SavePrefs();
                            }
                            RebuildDialogBody();
                        };
                        Grid.SetColumn(delOne, 2);
                        row.Children.Add(delOne);

                        listStack.Children.Add(new Border
                        {
                            CornerRadius = new CornerRadius(6),
                            Padding = new Thickness(10, 7, 8, 7),
                            Background = ThemeHelper.Brush(p.Card),
                            BorderBrush = ThemeHelper.Brush(p.Line),
                            BorderThickness = new Thickness(1),
                            Child = row,
                        });
                    }
                    bodyHost.Children.Add(listStack);
                }
            }
        }

        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            Title = L10n.T("自定义翻页音效"),
            PrimaryButtonText = L10n.T("保存"),
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
            Content = new ScrollViewer
            {
                MaxHeight = 460,
                Content = bodyHost,
            },
        };
        dlgRef = dlg;
        RebuildDialogBody();

        var result = await dlg.ShowAsync();
        DisposeCapture();

        if (result == ContentDialogResult.Primary && draft is { } finalDraft)
        {
            try
            {
                var savedItem = library.PageTurnSounds.Store.Save(finalDraft, draftName);
                library.PageTurnSounds.PreviewSelection(savedItem.SelectionKey);
                onSaved(savedItem.SelectionKey);
            }
            catch { }
        }
    }

    private static StackPanel MakeIconLabel(string icon, string text, Color fg)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(YYIcons.Make(icon, 12, ThemeHelper.Brush(fg)));
        sp.Children.Add(new TextBlock
        {
            Text = L10n.T(text),
            FontSize = 12,
            Foreground = ThemeHelper.Brush(fg),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return sp;
    }
}
