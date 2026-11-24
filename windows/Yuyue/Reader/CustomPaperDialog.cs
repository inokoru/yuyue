using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Yuyue.Core;
using Yuyue.Design;

namespace Yuyue.Reader;

/// <summary>
/// 1:1 implementation of macOS <c>CustomPapers.swift</c> (<c>调纸 PaperEditor</c> — 白天/夜里,
/// 6-column PaperTile swatches with 15pt serif "文" + grain texture + outer selection ring,
/// collapsible <c>微调颜色</c> HSL sliders with colored gradient tracks, 4 textures,
/// <c>字色</c> auto + palette inks, duplicate, and live reader page preview)
/// plus frameless warm-paper Book Record sheets.
/// </summary>
public static class CustomPaperDialog
{
    public static Color ParseHexColor(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string s = hex.Trim().TrimStart('#');
        try
        {
            if (s.Length == 6)
            {
                byte r = Convert.ToByte(s[..2], 16);
                byte g = Convert.ToByte(s.Substring(2, 2), 16);
                byte b = Convert.ToByte(s.Substring(4, 2), 16);
                return Color.FromArgb(255, r, g, b);
            }
            if (s.Length == 8)
            {
                byte c0 = Convert.ToByte(s[..2], 16);
                byte c1 = Convert.ToByte(s.Substring(2, 2), 16);
                byte c2 = Convert.ToByte(s.Substring(4, 2), 16);
                byte c3 = Convert.ToByte(s.Substring(6, 2), 16);
                return Color.FromArgb(c3, c0, c1, c2);
            }
        }
        catch { }
        return fallback;
    }

    /// <summary>
    /// Builds a 1:1 macOS <c>PaperTile</c> swatch (<c>46×38</c> or custom size) with 15pt serif "文",
    /// subtle grain dot overlay when <paramref name="textured"/> is true, and outer <c>tc.Accent</c> selection ring.
    /// </summary>
    public static FrameworkElement BuildPaperSwatchTile(
        Color bgCol,
        Color inkCol,
        Color lineCol,
        Color ringCol,
        bool selected,
        bool textured = false,
        double width = 46,
        double height = 38,
        double fontSize = 15)
    {
        var innerGrid = new Grid();
        if (textured)
        {
            var grainCanvas = new Canvas { IsHitTestVisible = false, Opacity = 0.20 };
            (double X, double Y)[] dots =
            [
                (7, 7), (18, 5), (31, 8), (39, 13),
                (11, 16), (24, 14), (35, 20), (8, 26),
                (19, 29), (29, 25), (38, 30), (14, 32),
            ];
            foreach (var (dx, dy) in dots)
            {
                var dot = new Ellipse
                {
                    Width = 1.4,
                    Height = 1.4,
                    Fill = ThemeHelper.Brush(inkCol),
                };
                Canvas.SetLeft(dot, dx * (width / 46.0));
                Canvas.SetTop(dot, dy * (height / 38.0));
                grainCanvas.Children.Add(dot);
            }
            innerGrid.Children.Add(grainCanvas);
        }

        innerGrid.Children.Add(new TextBlock
        {
            Text = "文",
            FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
            FontSize = fontSize,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(inkCol),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var innerTile = new Border
        {
            Width = width > 0 ? width : double.NaN,
            Height = height,
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(bgCol),
            BorderBrush = ThemeHelper.Brush(lineCol),
            BorderThickness = new Thickness(0.75),
            Child = innerGrid,
        };

        return new Border
        {
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(11.5),
            BorderBrush = selected ? ThemeHelper.Brush(ringCol) : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(1.6),
            HorizontalAlignment = width > 0 ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            Child = innerTile,
        };
    }

    public static CustomPaper? DuplicateCustomPaper(LibraryService library, CustomPaper mine)
    {
        try
        {
            var all = library.Core.CustomPapers();
            var taken = all.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            string baseName = Regex.Replace(mine.Name, @" \d+$", "");
            if (baseName.Length > 16) baseName = baseName[..16];
            string candidate = baseName;
            for (int n = 2; n < 200; n++)
            {
                string suffix = $" {n}";
                int maxBase = Math.Max(1, 20 - suffix.Length);
                string trimmedBase = baseName.Length > maxBase ? baseName[..maxBase] : baseName;
                string next = $"{trimmedBase}{suffix}";
                if (!taken.Contains(next))
                {
                    candidate = next;
                    break;
                }
            }
            if (candidate.Length > 20) candidate = candidate[..20];
            return library.Core.SaveCustomPaper(mine with { Id = "", Name = candidate });
        }
        catch
        {
            return null;
        }
    }

    public static CustomPaper NewDraftFromSettings(LibraryService library, ReaderSettings settings)
    {
        var resolved = PaperAppearance.Resolve(library, settings);
        if (resolved.Custom is { } mine)
        {
            return new CustomPaper("", "", mine.Bg, mine.Ink, mine.Layout, mine.Texture);
        }

        var preset = resolved.Preset;
        bool night = preset is Paper.Dark or Paper.KamiNight;
        var made = YuyueCoreMethods.PaperPalette(night).Swatches;
        int idx = night ? 0 : preset switch
        {
            Paper.Kami => Math.Min(1, made.Length - 1),
            Paper.Sage => Math.Min(2, made.Length - 1),
            Paper.Eink => Math.Min(5, made.Length - 1),
            _ => 0,
        };
        return new CustomPaper(
            Id: "",
            Name: "",
            Bg: made.Length > 0 ? made[Math.Max(0, idx)].Color : (night ? "#1C1B19" : "#F6F1E6"),
            Ink: null,
            Layout: preset is Paper.Plain or Paper.Dark ? Paper.Plain : Paper.Kami,
            Texture: preset == Paper.Eink ? PaperTexture.Grain : PaperTexture.None);
    }

    public sealed class PaperEditorHandle
    {
        public required StackPanel Root { get; init; }
        public required Func<CustomPaper> GetDraft { get; init; }
    }

    /// <summary>
    /// Reusable <c>PaperEditor</c> body used both inside <see cref="ReaderWindow"/>'s inline 308px <c>TypesetFlyout</c>
    /// (<paramref name="showReadingSample"/> = false, live-updating the reader page behind it) and inside the standalone
    /// 340px <see cref="ShowEditorAsync"/> sheet (<paramref name="showReadingSample"/> = true).
    /// </summary>
    public static PaperEditorHandle BuildPaperEditorBody(
        LibraryService library,
        bool darkUi,
        CustomPaper initial,
        bool showReadingSample,
        Action<CustomPaper>? onDraftChanged = null,
        Action<CustomPaper>? onDuplicatePicked = null,
        Action? onDeleteRequested = null,
        bool compactInline = false)
    {
        var draft = initial;
        bool night = YuyueCoreMethods.PaperColors(draft).Dark;
        var hsl = YuyueCoreMethods.PaperHsl(draft.Bg);
        bool tuningOpen = false;
        bool updatingSliders = false;

        var p = ThemeHelper.Palette(darkUi);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, darkUi);

        var root = new StackPanel { Spacing = 11 };

        TextBlock? previewTitle = null;
        TextBlock? previewSub = null;
        Border? previewBox = null;
        if (showReadingSample)
        {
            previewTitle = new TextBlock
            {
                Text = L10n.T("春江潮水连海平，海上明月共潮生。\n滟滟随波千万里，何处春江无月明。"),
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = 14,
                LineHeight = 23,
                TextWrapping = TextWrapping.Wrap,
            };
            previewSub = new TextBlock { FontSize = 10.5 };
            previewBox = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(0.75),
                Padding = new Thickness(14, 11, 14, 11),
                Child = new StackPanel { Spacing = 5, Children = { previewTitle, previewSub } },
            };
            root.Children.Add(previewBox);
        }

        // 1. 白天 | 夜里 segmented switch (1:1 CustomPapers.swift:229-230)
        var sideGrid = new Grid { ColumnSpacing = 3 };
        sideGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sideGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dayBtn = new Button { Content = L10n.T("白天"), HorizontalAlignment = HorizontalAlignment.Stretch, Height = 26, MinHeight = 26, FontSize = 11.5, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(7), UseSystemFocusVisuals = false };
        var nightBtn = new Button { Content = L10n.T("夜里"), HorizontalAlignment = HorizontalAlignment.Stretch, Height = 26, MinHeight = 26, FontSize = 11.5, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(7), UseSystemFocusVisuals = false };
        Grid.SetColumn(dayBtn, 0);
        Grid.SetColumn(nightBtn, 1);
        sideGrid.Children.Add(dayBtn);
        sideGrid.Children.Add(nightBtn);
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(p.Inset),
            Padding = new Thickness(2.5),
            Child = sideGrid,
        });

        // 2. 6-column PaperTile swatch grid
        var swatchGrid = new Grid { ColumnSpacing = 4, RowSpacing = 6 };
        for (int c = 0; c < 6; c++) swatchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(swatchGrid);

        // 3. Settings group card: 微调颜色 (collapsible) + 纹理 + 字色 + 版式
        var groupStack = new StackPanel { Spacing = 0 };

        var tuneChevTb = new TextBlock { Text = "›", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center };
        var tuneHeaderGrid = new Grid { Height = 36 };
        tuneHeaderGrid.Children.Add(new TextBlock { Text = L10n.T("微调颜色"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        var tuneRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        tuneRight.Children.Add(new TextBlock { Text = L10n.T("色调、浓淡、深浅"), FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), VerticalAlignment = VerticalAlignment.Center });
        tuneRight.Children.Add(tuneChevTb);
        tuneHeaderGrid.Children.Add(tuneRight);

        var tuneToggleBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = tuneHeaderGrid,
        };
        groupStack.Children.Add(tuneToggleBtn);

        var tunePanel = new StackPanel { Spacing = 6, Padding = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
        var hueTrack = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var satTrack = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var lightTrack = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };

        var hueSlider = new Slider { Minimum = 0, Maximum = 359, StepFrequency = 1 };
        var satSlider = new Slider { Minimum = 0, Maximum = 30, StepFrequency = 0.5 };
        var lightSlider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 0.5 };

        Grid MakeSliderRow(string label, Border trackBg, Slider slider)
        {
            var g = new Grid { ColumnSpacing = 10, Height = 30 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = L10n.T(label), FontSize = 11, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
            var trackWrap = new Grid();
            trackWrap.Children.Add(trackBg);
            trackWrap.Children.Add(slider);
            Grid.SetColumn(trackWrap, 1);
            g.Children.Add(trackWrap);
            return g;
        }
        tunePanel.Children.Add(MakeSliderRow("色调", hueTrack, hueSlider));
        tunePanel.Children.Add(MakeSliderRow("浓淡", satTrack, satSlider));
        tunePanel.Children.Add(MakeSliderRow("深浅", lightTrack, lightSlider));
        groupStack.Children.Add(tunePanel);

        groupStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
        var texRow = new Grid { MinHeight = 40 };
        texRow.Children.Add(new TextBlock { Text = L10n.T("纹理"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        var texHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        texRow.Children.Add(texHost);
        groupStack.Children.Add(texRow);

        groupStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
        var inkRow = new Grid { MinHeight = 40 };
        inkRow.Children.Add(new TextBlock { Text = L10n.T("字色"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
        var inkHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        inkRow.Children.Add(inkHost);
        groupStack.Children.Add(inkRow);

        var layoutHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (!compactInline)
        {
            groupStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            var layoutRow = new Grid { MinHeight = 38 };
            layoutRow.Children.Add(new TextBlock { Text = L10n.T("版式"), FontSize = 12, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center });
            layoutRow.Children.Add(layoutHost);
            groupStack.Children.Add(layoutRow);
        }

        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(12, 2, 12, 2),
            Child = groupStack,
        });

        if (!compactInline)
        {
            var nameBox = new TextBox
            {
                Text = draft.Name,
                MaxLength = 20,
                PlaceholderText = L10n.T("纸张名称（留空自动命名）"),
                FontSize = 12,
            };
            nameBox.TextChanged += (_, _) =>
            {
                string trimmed = nameBox.Text.Trim();
                if (trimmed.Length > 20) trimmed = trimmed[..20];
                draft = draft with { Name = trimmed };
            };
            root.Children.Add(nameBox);
        }

        if (!string.IsNullOrEmpty(initial.Id))
        {
            var existingActRow = new Grid { ColumnSpacing = 8 };
            existingActRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            existingActRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            existingActRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (onDuplicatePicked is not null)
            {
                var dupBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = L10n.T("复制一张"),
                    Height = 26,
                    MinHeight = 26,
                    FontSize = 11,
                };
                dupBtn.Click += (_, _) =>
                {
                    var duplicated = DuplicateCustomPaper(library, draft with { Name = string.IsNullOrWhiteSpace(draft.Name) ? initial.Name : draft.Name });
                    if (duplicated is not null) onDuplicatePicked(duplicated);
                };
                existingActRow.Children.Add(dupBtn);
            }

            if (onDeleteRequested is not null)
            {
                var delBtn = new Button
                {
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6, 2, 6, 2),
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.Danger),
                    Content = L10n.T("删除这张纸"),
                    UseSystemFocusVisuals = false,
                };
                delBtn.Click += (_, _) => onDeleteRequested();
                Grid.SetColumn(delBtn, 2);
                existingActRow.Children.Add(delBtn);
            }
            root.Children.Add(existingActRow);
        }

        static LinearGradientBrush MakeGradient(params Color[] stops)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            if (stops.Length == 1)
            {
                brush.GradientStops.Add(new GradientStop { Color = stops[0], Offset = 0 });
                brush.GradientStops.Add(new GradientStop { Color = stops[0], Offset = 1 });
                return brush;
            }
            for (int i = 0; i < stops.Length; i++)
                brush.GradientStops.Add(new GradientStop { Color = stops[i], Offset = (double)i / (stops.Length - 1) });
            return brush;
        }

        Color SampleHslColor(double? h = null, double? s = null, double? l = null)
        {
            string hex = YuyueCoreMethods.PaperColor(new PaperHsl(h ?? hsl.H, s ?? hsl.S, l ?? hsl.L), night);
            return ParseHexColor(hex, Colors.Beige);
        }

        void SyncSlidersFromHsl()
        {
            var pal = YuyueCoreMethods.PaperPalette(night);
            updatingSliders = true;
            satSlider.Maximum = Math.Max(1, pal.SaturationMax);
            hueSlider.Value = Math.Clamp(hsl.H, 0, 359);
            satSlider.Value = Math.Clamp(hsl.S, 0, pal.SaturationMax);

            double span = Math.Max(0.001, pal.LightnessMax - pal.LightnessMin);
            double norm = (hsl.L - pal.LightnessMin) / span;
            lightSlider.Value = Math.Clamp((1.0 - norm) * 100.0, 0, 100);
            updatingSliders = false;

            hueTrack.Background = MakeGradient(
                SampleHslColor(h: 0, s: Math.Max(hsl.S, 30)),
                SampleHslColor(h: 60, s: Math.Max(hsl.S, 30)),
                SampleHslColor(h: 120, s: Math.Max(hsl.S, 30)),
                SampleHslColor(h: 180, s: Math.Max(hsl.S, 30)),
                SampleHslColor(h: 240, s: Math.Max(hsl.S, 30)),
                SampleHslColor(h: 300, s: Math.Max(hsl.S, 30)),
                SampleHslColor(h: 359, s: Math.Max(hsl.S, 30)));
            satTrack.Background = MakeGradient(SampleHslColor(s: 0), SampleHslColor(s: pal.SaturationMax));
            lightTrack.Background = MakeGradient(SampleHslColor(l: pal.LightnessMax), SampleHslColor(l: pal.LightnessMin));
        }

        void RebuildControlsAndPreview()
        {
            var pal = YuyueCoreMethods.PaperPalette(night);
            var colors = YuyueCoreMethods.PaperColors(draft);
            var bgCol = ParseHexColor(colors.Bg, Color.FromArgb(255, 246, 241, 230));
            var inkCol = ParseHexColor(colors.Ink, Color.FromArgb(255, 42, 43, 46));
            var ink2Col = ParseHexColor(colors.Ink2, Color.FromArgb(180, 42, 43, 46));
            var lineCol = ParseHexColor(colors.Line, Color.FromArgb(35, 0, 0, 0));

            dayBtn.Background = !night ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent);
            dayBtn.FontWeight = !night ? FontWeights.SemiBold : FontWeights.Normal;
            dayBtn.Foreground = ThemeHelper.Brush(!night ? p.T1 : p.T2);
            nightBtn.Background = night ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent);
            nightBtn.FontWeight = night ? FontWeights.SemiBold : FontWeights.Normal;
            nightBtn.Foreground = ThemeHelper.Brush(night ? p.T1 : p.T2);

            if (previewBox is not null && previewTitle is not null && previewSub is not null)
            {
                previewBox.Background = ThemeHelper.Brush(bgCol);
                previewBox.BorderBrush = ThemeHelper.Brush(lineCol);
                previewTitle.Foreground = ThemeHelper.Brush(inkCol);
                previewSub.Foreground = ThemeHelper.Brush(ink2Col);
                string texLabel = draft.Texture switch
                {
                    PaperTexture.Grain => L10n.T("细颗粒"),
                    PaperTexture.Xuan => L10n.T("宣纸"),
                    PaperTexture.Linen => L10n.T("亚麻"),
                    _ => L10n.T("无纹理"),
                };
                previewSub.Text = $"{colors.Bg.ToUpperInvariant()} · {texLabel} · {L10n.T("对比度")} {colors.Contrast:0.1}:1";
            }

            swatchGrid.Children.Clear();
            swatchGrid.RowDefinitions.Clear();
            int rows = (pal.Swatches.Length + 5) / 6;
            for (int r = 0; r < rows; r++) swatchGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < pal.Swatches.Length; i++)
            {
                var sw = pal.Swatches[i];
                var swPaper = draft with { Id = "", Name = sw.Name, Bg = sw.Color };
                var swColors = YuyueCoreMethods.PaperColors(swPaper);
                bool selected = string.Equals(draft.Bg, sw.Color, StringComparison.OrdinalIgnoreCase);

                var tile = BuildPaperSwatchTile(
                    ParseHexColor(swColors.Bg, Colors.Beige),
                    ParseHexColor(swColors.Ink, Colors.Black),
                    p.Line,
                    tint.Accent,
                    selected,
                    textured: draft.Texture != PaperTexture.None,
                    width: 0,
                    height: 32,
                    fontSize: 14);

                var sp = new StackPanel { Spacing = 3 };
                sp.Children.Add(tile);
                sp.Children.Add(new TextBlock
                {
                    Text = sw.Name,
                    FontSize = 9.5,
                    FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(selected ? p.T1 : p.T2),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                var btn = new Button
                {
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = sp,
                };
                string swHex = sw.Color;
                btn.Click += (_, _) =>
                {
                    draft = draft with { Bg = swHex };
                    hsl = YuyueCoreMethods.PaperHsl(swHex);
                    SyncSlidersFromHsl();
                    RebuildControlsAndPreview();
                };
                Grid.SetRow(btn, i / 6);
                Grid.SetColumn(btn, i % 6);
                swatchGrid.Children.Add(btn);
            }

            texHost.Children.Clear();
            foreach (var (tx, lbl, mark) in new[]
            {
                (PaperTexture.None, "无纹理", ""),
                (PaperTexture.Grain, "细颗粒", "∴"),
                (PaperTexture.Xuan, "宣纸", "≈"),
                (PaperTexture.Linen, "亚麻", "⌗"),
            })
            {
                bool on = draft.Texture == tx;
                var tb = new Border
                {
                    Width = 34,
                    Height = 25,
                    CornerRadius = new CornerRadius(7),
                    Background = ThemeHelper.Brush(bgCol),
                    BorderBrush = on ? ThemeHelper.Brush(tint.Accent) : ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(on ? 1.6 : 0.75),
                    Child = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(mark) ? L10n.T(lbl)[..1] : mark,
                        FontSize = 10.5,
                        Foreground = ThemeHelper.Brush(ink2Col),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                var b = new Button
                {
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = tb,
                };
                ToolTipService.SetToolTip(b, L10n.T(lbl));
                var targetTex = tx;
                b.Click += (_, _) => { draft = draft with { Texture = targetTex }; RebuildControlsAndPreview(); };
                texHost.Children.Add(b);
            }

            inkHost.Children.Clear();
            bool autoInk = draft.Ink is null;
            var autoBtn = new Button
            {
                Height = 24,
                MinHeight = 24,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(p.Inset),
                BorderBrush = autoInk ? ThemeHelper.Brush(tint.Accent) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1.5),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(autoInk ? p.T1 : p.T2),
                UseSystemFocusVisuals = false,
                Content = L10n.T("自动"),
            };
            autoBtn.Click += (_, _) => { draft = draft with { Ink = null }; RebuildControlsAndPreview(); };
            inkHost.Children.Add(autoBtn);

            foreach (var inkSw in pal.Inks)
            {
                bool on = string.Equals(draft.Ink, inkSw.Color, StringComparison.OrdinalIgnoreCase);
                var circle = new Border
                {
                    Width = 23,
                    Height = 23,
                    CornerRadius = new CornerRadius(11.5),
                    Background = ThemeHelper.Brush(bgCol),
                    BorderBrush = on ? ThemeHelper.Brush(tint.Accent) : ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(on ? 1.6 : 0.75),
                    Child = new TextBlock
                    {
                        Text = inkSw.Name,
                        FontSize = 9.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(ParseHexColor(inkSw.Color, inkCol)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                var ib = new Button
                {
                    Padding = new Thickness(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = circle,
                };
                ToolTipService.SetToolTip(ib, inkSw.Name);
                string inkHex = inkSw.Color;
                ib.Click += (_, _) => { draft = draft with { Ink = inkHex }; RebuildControlsAndPreview(); };
                inkHost.Children.Add(ib);
            }

            layoutHost.Children.Clear();
            foreach (var (lk, ll) in new[] { (Paper.Kami, "纸本缩进"), (Paper.Plain, "素白段距") })
            {
                bool on = draft.Layout == lk;
                var lb = new Button
                {
                    Style = (Style)Application.Current.Resources[on ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"],
                    Content = L10n.T(ll),
                    Height = 24,
                    MinHeight = 24,
                    FontSize = 10.5,
                };
                var targetLayout = lk;
                lb.Click += (_, _) => { draft = draft with { Layout = targetLayout }; RebuildControlsAndPreview(); };
                layoutHost.Children.Add(lb);
            }

            onDraftChanged?.Invoke(draft);
        }

        void SwitchSide(bool toNight)
        {
            if (toNight == night) return;
            night = toNight;
            var first = YuyueCoreMethods.PaperPalette(night).Swatches.FirstOrDefault();
            if (first is not null)
            {
                draft = draft with { Bg = first.Color, Ink = null };
                hsl = YuyueCoreMethods.PaperHsl(first.Color);
            }
            SyncSlidersFromHsl();
            RebuildControlsAndPreview();
        }

        dayBtn.Click += (_, _) => SwitchSide(false);
        nightBtn.Click += (_, _) => SwitchSide(true);

        tuneToggleBtn.Click += (_, _) =>
        {
            tuningOpen = !tuningOpen;
            tunePanel.Visibility = tuningOpen ? Visibility.Visible : Visibility.Collapsed;
            tuneChevTb.Text = tuningOpen ? "⌄" : "›";
        };

        void OnHslSliderChanged()
        {
            if (updatingSliders) return;
            var pal = YuyueCoreMethods.PaperPalette(night);
            double span = Math.Max(0.001, pal.LightnessMax - pal.LightnessMin);
            double l = pal.LightnessMin + (1.0 - lightSlider.Value / 100.0) * span;
            hsl = new PaperHsl(hueSlider.Value, satSlider.Value, l);
            draft = draft with { Bg = YuyueCoreMethods.PaperColor(hsl, night) };
            SyncSlidersFromHsl();
            RebuildControlsAndPreview();
        }

        hueSlider.ValueChanged += (_, _) => OnHslSliderChanged();
        satSlider.ValueChanged += (_, _) => OnHslSliderChanged();
        lightSlider.ValueChanged += (_, _) => OnHslSliderChanged();

        SyncSlidersFromHsl();
        RebuildControlsAndPreview();

        return new PaperEditorHandle
        {
            Root = root,
            GetDraft = () => draft,
        };
    }

    public static Task<CustomPaper?> ShowAsync(
        XamlRoot xamlRoot,
        LibraryService library,
        CustomPaper? existing = null,
        Action<CustomPaper?>? onPreview = null,
        ReaderSettings? currentSettings = null) =>
        ShowEditorAsync(xamlRoot, library, existing, onPreview, currentSettings);

    public static async Task<CustomPaper?> ShowEditorAsync(
        XamlRoot xamlRoot,
        LibraryService library,
        CustomPaper? existing = null,
        Action<CustomPaper?>? onPreview = null,
        ReaderSettings? currentSettings = null)
    {
        var initial = existing ?? NewDraftFromSettings(library, currentSettings ?? library.GetReaderSettings());
        CustomPaper? duplicatedPaper = null;
        bool deletedPaper = false;
        bool confirmedSave = false;
        ContentDialog? dlgRef = null;

        bool darkUi = xamlRoot.Content is FrameworkElement fe && ThemeHelper.IsDark(fe.ActualTheme);

        var handle = BuildPaperEditorBody(
            library,
            darkUi,
            initial,
            showReadingSample: true,
            onDraftChanged: d => onPreview?.Invoke(d),
            onDuplicatePicked: dup =>
            {
                duplicatedPaper = dup;
                dlgRef?.Hide();
            },
            onDeleteRequested: () =>
            {
                deletedPaper = true;
                dlgRef?.Hide();
            });

        string sheetTitle = string.IsNullOrEmpty(initial.Id) ? L10n.T("调纸") : $"{L10n.T("调整")}“{initial.Name}”";
        var outer = new StackPanel { Spacing = 12, Padding = new Thickness(18, 16, 18, 16) };
        outer.Children.Add(YYChrome.MakeSheetHeader(sheetTitle, null, () => dlgRef?.Hide(), darkUi, titleSize: 16));
        outer.Children.Add(new ScrollViewer
        {
            MaxHeight = 500,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = handle.Root,
        });

        var footGrid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 0) };
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cancelBtn = YYChrome.MakeSheetPillButton(L10n.T("取消"), () => dlgRef?.Hide(), darkUi, library.Prefs.Tint, "secondary", height: 34, stretch: true);
        var saveBtn = YYChrome.MakeSheetPillButton(L10n.T("完成"), () => { confirmedSave = true; dlgRef?.Hide(); }, darkUi, library.Prefs.Tint, "warm", height: 34, stretch: true);
        Grid.SetColumn(cancelBtn, 0);
        Grid.SetColumn(saveBtn, 1);
        footGrid.Children.Add(cancelBtn);
        footGrid.Children.Add(saveBtn);
        outer.Children.Add(footGrid);

        dlgRef = YYChrome.CreateMacSheet(xamlRoot, darkUi, 340, outer, maxHeight: 740, tint: library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(library.Prefs.Tint);

        if (duplicatedPaper is not null)
        {
            onPreview?.Invoke(null);
            return duplicatedPaper;
        }

        if (deletedPaper && !string.IsNullOrEmpty(initial.Id))
        {
            try { library.Core.DeleteCustomPaper(initial.Id); } catch { }
            PaperAppearance.Forget(library, initial.Id);
            onPreview?.Invoke(null);
            return null;
        }

        if (!confirmedSave)
        {
            onPreview?.Invoke(null);
            return null;
        }

        try
        {
            var saved = library.Core.SaveCustomPaper(handle.GetDraft());
            onPreview?.Invoke(null);
            return saved;
        }
        catch
        {
            onPreview?.Invoke(null);
            return null;
        }
    }

    /// <summary>
    /// Adds a record-only book (e.g. a physical paper book or book read elsewhere).
    /// </summary>
    public static async Task<BookSummary?> ShowAddRecordBookDialogAsync(XamlRoot xamlRoot, LibraryService library)
    {
        bool dark = xamlRoot.Content is FrameworkElement fe && ThemeHelper.IsDark(fe.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        ContentDialog? dlgRef = null;
        bool confirmed = false;

        var titleBox = new TextBox { Header = L10n.T("书名"), PlaceholderText = L10n.T("例如：乡土中国") };
        var authorBox = new TextBox { Header = L10n.T("作者（可选）"), PlaceholderText = L10n.T("例如：费孝通") };
        ReadingStatus status = ReadingStatus.Reading;

        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var statusBtns = new List<(ReadingStatus St, Button Btn)>();
        foreach (var (st, lbl) in new[] { (ReadingStatus.Reading, "在读"), (ReadingStatus.Want, "想读"), (ReadingStatus.Finished, "读过") })
        {
            var s = st;
            var b = new Button { Content = L10n.T(lbl) };
            b.Click += (_, _) =>
            {
                status = s;
                foreach (var (itemSt, itemBtn) in statusBtns)
                    itemBtn.Style = (Style)Application.Current.Resources[itemSt == status ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];
            };
            statusBtns.Add((s, b));
            statusRow.Children.Add(b);
        }
        foreach (var (itemSt, itemBtn) in statusBtns)
            itemBtn.Style = (Style)Application.Current.Resources[itemSt == status ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];

        var foot = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cancelBtn = YYChrome.MakeSheetPillButton(L10n.T("取消"), () => dlgRef?.Hide(), dark, library.Prefs.Tint, "secondary", height: 36, stretch: true);
        var okBtn = YYChrome.MakeSheetPillButton(L10n.T("加入进度架"), () =>
        {
            if (!string.IsNullOrWhiteSpace(titleBox.Text))
            {
                confirmed = true;
                dlgRef?.Hide();
            }
        }, dark, library.Prefs.Tint, "warm", height: 36, stretch: true);
        Grid.SetColumn(cancelBtn, 0);
        Grid.SetColumn(okBtn, 1);
        foot.Children.Add(cancelBtn);
        foot.Children.Add(okBtn);

        var body = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(22, 18, 22, 18),
            Children =
            {
                YYChrome.MakeSheetHeader(L10n.T("记一本纸书 / 外部书"), null, () => dlgRef?.Hide(), dark, titleSize: 17),
                new TextBlock
                {
                    Text = L10n.T("无需电子书文件，也能在语阅里记录页码进度、评分与读后短评，并跨端同步。"),
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    TextWrapping = TextWrapping.Wrap,
                },
                titleBox,
                authorBox,
                statusRow,
                foot,
            },
        };

        dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 412, body, tint: library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(library.Prefs.Tint);
        if (!confirmed || string.IsNullOrWhiteSpace(titleBox.Text))
            return null;

        var authors = authorBox.Text.Split([',', '，', '/', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try { return library.Core.AddRecordBook(titleBox.Text.Trim(), authors, status); }
        catch { return null; }
    }

    /// <summary>
    /// Opens the Book Record card (1–5 star rating, review comment, tags, and manual page progress).
    /// </summary>
    public static async Task<bool> ShowBookRecordDialogAsync(XamlRoot xamlRoot, LibraryService library, BookSummary book)
    {
        bool dark = xamlRoot.Content is FrameworkElement fe && ThemeHelper.IsDark(fe.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        ContentDialog? dlgRef = null;
        bool confirmed = false;

        byte? rating = book.Rating;
        var root = new StackPanel { Spacing = 12, Padding = new Thickness(22, 18, 22, 18) };
        root.Children.Add(YYChrome.MakeSheetHeader(L10n.T("阅读档案"), book.Title, () => dlgRef?.Hide(), dark, titleSize: 17));

        var starRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var starBtns = new List<(byte Val, Button Btn)>();
        void RefreshStars()
        {
            foreach (var (v, b) in starBtns)
            {
                bool lit = rating is byte r && v <= r;
                b.Content = lit ? "★" : "☆";
                b.Style = (Style)Application.Current.Resources[lit ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];
            }
        }
        for (byte s = 1; s <= 5; s++)
        {
            byte val = s;
            var btn = new Button { Width = 36, Height = 30, Padding = new Thickness(0), FontSize = 15 };
            btn.Click += (_, _) => { rating = rating == val ? null : val; RefreshStars(); };
            starBtns.Add((val, btn));
            starRow.Children.Add(btn);
        }
        RefreshStars();
        root.Children.Add(new StackPanel { Spacing = 6, Children = { new TextBlock { Text = L10n.T("我的评分"), FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) }, starRow } });

        var curValBox = new NumberBox { Header = L10n.T("当前已读页数"), Value = book.Manual?.Value ?? 0, Minimum = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 186 };
        var totalValBox = new NumberBox { Header = L10n.T("总页数"), Value = book.Manual?.Total ?? 300, Minimum = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 186 };
        if (book.Record || book.Manual is not null)
        {
            root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { curValBox, totalValBox } });
        }

        var commentBox = new TextBox
        {
            Header = L10n.T("短评 / 读后记"),
            Text = book.Comment ?? "",
            PlaceholderText = L10n.T("写几句读这本书的感受…"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 76,
        };
        root.Children.Add(commentBox);

        var tagBox = new TextBox
        {
            Header = L10n.T("标签（用逗号分隔）"),
            Text = book.Tags is { Length: > 0 } ? string.Join("，", book.Tags) : "",
            PlaceholderText = L10n.T("例如：文学，年度十佳，社科"),
        };
        root.Children.Add(tagBox);

        var foot = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cancelBtn = YYChrome.MakeSheetPillButton(L10n.T("取消"), () => dlgRef?.Hide(), dark, library.Prefs.Tint, "secondary", height: 36, stretch: true);
        var saveBtn = YYChrome.MakeSheetPillButton(L10n.T("保存"), () => { confirmed = true; dlgRef?.Hide(); }, dark, library.Prefs.Tint, "warm", height: 36, stretch: true);
        Grid.SetColumn(cancelBtn, 0);
        Grid.SetColumn(saveBtn, 1);
        foot.Children.Add(cancelBtn);
        foot.Children.Add(saveBtn);
        root.Children.Add(foot);

        dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 440, root, tint: library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(library.Prefs.Tint);
        if (!confirmed) return false;

        try
        {
            library.Core.SetRating(book.Id, rating);
            library.Core.SetComment(book.Id, string.IsNullOrWhiteSpace(commentBox.Text) ? null : commentBox.Text.Trim());

            if (book.Record || book.Manual is not null)
            {
                uint v = (uint)Math.Max(0, double.IsNaN(curValBox.Value) ? 0 : (int)curValBox.Value);
                uint t = (uint)Math.Max(1, double.IsNaN(totalValBox.Value) ? 1 : (int)totalValBox.Value);
                library.Core.SetManualProgress(book.Id, ProgressUnit.Page, v, t);
            }

            var newTags = tagBox.Text
                .Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var oldTags = (book.Tags ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var removed in oldTags.Except(newTags, StringComparer.OrdinalIgnoreCase))
                library.Core.SetTag(book.Id, removed, false);
            foreach (var added in newTags.Except(oldTags, StringComparer.OrdinalIgnoreCase))
                library.Core.SetTag(book.Id, added, true);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
