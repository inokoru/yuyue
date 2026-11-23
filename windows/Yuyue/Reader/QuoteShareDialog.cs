using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using Yuyue.Design;

namespace Yuyue.Reader;

/// <summary>
/// 1:1 port of <c>apple/Yuyue/Library/QuoteShare.swift</c>:
/// 4 color looks (<c>封面色 / 纸本 / 素白 / 暗黑</c>), 3 aspect shapes (<c>竖图 / 方图 / 横图</c>),
/// <c>带上想法</c> switch, mini book cover in footer, and 3 actions (<c>纯文字 / 存图 / 复制图片</c>).
/// </summary>
public static class QuoteShareDialog
{
    public static async Task ShowAsync(
        Window ownerWindow, XamlRoot xamlRoot,
        string quote, string? thought, string bookTitle, string? author, string? coverPath = null)
    {
        bool darkUi = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var uiPalette = ThemeHelper.Palette(darkUi);

        string look = "kami";     // cover | kami | plain | dark
        string shape = "tall";    // tall | square | wide
        bool includeThought = !string.IsNullOrWhiteSpace(thought);

        Color coverTone = Color.FromArgb(255, 58, 72, 92);
        if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
        {
            try { coverTone = await CoverColorHelper.LoadLeadCardColorAsync(coverPath, dark: true); }
            catch { }
        }

        var previewHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Border currentCard = new();

        void RebuildCard()
        {
            previewHost.Children.Clear();
            (double w, double minH) = shape switch
            {
                "square" => (336.0, 336.0),
                "wide" => (420.0, 256.0),
                _ => (292.0, 376.0),
            };

            (Color bg, Color ink, Color ink2, Color rule, Color accent) = look switch
            {
                "cover" => (
                    coverTone,
                    Color.FromArgb(255, 248, 245, 239),
                    Color.FromArgb(196, 248, 245, 239),
                    Color.FromArgb(48, 255, 255, 255),
                    Color.FromArgb(220, 248, 245, 239)),
                "plain" => (
                    Color.FromArgb(255, 255, 255, 255),
                    Color.FromArgb(255, 30, 30, 32),
                    Color.FromArgb(255, 112, 112, 118),
                    Color.FromArgb(28, 0, 0, 0),
                    Color.FromArgb(255, 86, 102, 126)),
                "dark" => (
                    Color.FromArgb(255, 24, 24, 27),
                    Color.FromArgb(255, 234, 231, 224),
                    Color.FromArgb(255, 148, 145, 138),
                    Color.FromArgb(36, 255, 255, 255),
                    Color.FromArgb(255, 168, 184, 208)),
                _ => (
                    Color.FromArgb(255, 246, 241, 230),
                    Color.FromArgb(255, 42, 39, 35),
                    Color.FromArgb(255, 108, 101, 91),
                    Color.FromArgb(34, 92, 76, 54),
                    Color.FromArgb(255, 138, 101, 63)),
            };

            var card = new Border
            {
                Width = w,
                MinHeight = minH,
                CornerRadius = new CornerRadius(16),
                Background = ThemeHelper.Brush(bg),
                BorderBrush = ThemeHelper.Brush(rule),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(26, 26, 26, 22),
            };

            var grid = new Grid { RowSpacing = 14 };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            grid.Children.Add(new TextBlock
            {
                Text = "“",
                FontFamily = new FontFamily("Georgia, SimSun"),
                FontSize = 34,
                Foreground = ThemeHelper.Brush(accent),
                Opacity = 0.75,
                Margin = new Thickness(0, -6, 0, -16),
            });

            var bodyStack = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
            bodyStack.Children.Add(new TextBlock
            {
                Text = quote.Trim(),
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = shape == "wide" ? 14.5 : 15.5,
                LineHeight = shape == "wide" ? 24 : 26,
                Foreground = ThemeHelper.Brush(ink),
                TextWrapping = TextWrapping.Wrap,
            });

            if (includeThought && !string.IsNullOrWhiteSpace(thought))
            {
                var thGrid = new Grid { ColumnSpacing = 8 };
                thGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                thGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                thGrid.Children.Add(new Border { Background = ThemeHelper.Brush(accent), CornerRadius = new CornerRadius(1) });
                var thTb = new TextBlock
                {
                    Text = thought.Trim(),
                    FontFamily = new FontFamily(YYFontFamily.Kaiti.WindowsFamily()),
                    FontSize = 12.5,
                    LineHeight = 19,
                    Foreground = ThemeHelper.Brush(ink2),
                    TextWrapping = TextWrapping.Wrap,
                };
                Grid.SetColumn(thTb, 1);
                thGrid.Children.Add(thTb);
                bodyStack.Children.Add(thGrid);
            }
            Grid.SetRow(bodyStack, 1);
            grid.Children.Add(bodyStack);

            var footStack = new StackPanel { Spacing = 10 };
            footStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(rule) });

            var footRow = new Grid { ColumnSpacing = 8 };
            footRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
            {
                footRow.Children.Add(new Border
                {
                    Width = 20,
                    Height = 28,
                    CornerRadius = new CornerRadius(2.5),
                    BorderBrush = ThemeHelper.Brush(rule),
                    BorderThickness = new Thickness(0.5),
                    Child = new Image { Source = new BitmapImage(new Uri(coverPath)), Stretch = Stretch.UniformToFill },
                });
            }

            var citeStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            citeStack.Children.Add(new TextBlock
            {
                Text = bookTitle,
                FontSize = 11.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(ink),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (!string.IsNullOrWhiteSpace(author))
            {
                citeStack.Children.Add(new TextBlock
                {
                    Text = author,
                    FontSize = 10,
                    Foreground = ThemeHelper.Brush(ink2),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            Grid.SetColumn(citeStack, 1);
            footRow.Children.Add(citeStack);

            var brand = new TextBlock
            {
                Text = "语阅",
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(ink2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(brand, 2);
            footRow.Children.Add(brand);
            footStack.Children.Add(footRow);

            Grid.SetRow(footStack, 2);
            grid.Children.Add(footStack);
            card.Child = grid;

            currentCard = card;
            previewHost.Children.Add(card);
        }

        // Controls row: 4 color dots + 3 aspect ratio segments
        var controlsBar = new Grid { ColumnSpacing = 12 };
        controlsBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controlsBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dotsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var dotBtns = new List<(string Key, Border Ring)>();
        var shapeBtns = new List<(string Key, Button Btn)>();
        foreach (var (k, lbl, col) in new[]
        {
            ("cover", "封面色", coverTone),
            ("kami", "纸本", Color.FromArgb(255, 246, 241, 230)),
            ("plain", "素白", Color.FromArgb(255, 255, 255, 255)),
            ("dark", "暗黑", Color.FromArgb(255, 28, 28, 32)),
        })
        {
            var ring = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Padding = new Thickness(2),
                BorderThickness = new Thickness(1.5),
                Child = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = ThemeHelper.Brush(col),
                    BorderBrush = ThemeHelper.Brush(Color.FromArgb(40, 0, 0, 0)),
                    BorderThickness = new Thickness(0.5),
                },
            };
            var b = new Button
            {
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = ring,
            };
            ToolTipService.SetToolTip(b, lbl);
            string key = k;
            b.Click += (_, _) => { look = key; SyncOptionVisuals(); RebuildCard(); };
            dotBtns.Add((key, ring));
            dotsRow.Children.Add(b);
        }
        controlsBar.Children.Add(dotsRow);

        var shapeSeg = new Grid { ColumnSpacing = 2, Width = 168, HorizontalAlignment = HorizontalAlignment.Right };
        var shapeItems = new[] { ("tall", "竖图"), ("square", "方图"), ("wide", "横图") };
        for (int i = 0; i < shapeItems.Length; i++)
        {
            shapeSeg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (sk, sl) = shapeItems[i];
            var sb = new Button
            {
                Content = sl,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                FontSize = 11,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                UseSystemFocusVisuals = false,
            };
            string key = sk;
            sb.Click += (_, _) => { shape = key; SyncOptionVisuals(); RebuildCard(); };
            Grid.SetColumn(sb, i);
            shapeSeg.Children.Add(sb);
            shapeBtns.Add((key, sb));
        }
        var shapeWrap = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(uiPalette.Inset),
            Padding = new Thickness(2),
            Child = shapeSeg,
        };
        Grid.SetColumn(shapeWrap, 1);
        controlsBar.Children.Add(shapeWrap);

        void SyncOptionVisuals()
        {
            foreach (var (k, ring) in dotBtns)
                ring.BorderBrush = k == look ? ThemeHelper.Brush(uiPalette.T1) : new SolidColorBrush(Colors.Transparent);
            foreach (var (k, btn) in shapeBtns)
            {
                bool on = k == shape;
                btn.Background = on ? ThemeHelper.Brush(uiPalette.Card) : new SolidColorBrush(Colors.Transparent);
                btn.Foreground = ThemeHelper.Brush(on ? uiPalette.T1 : uiPalette.T2);
                btn.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            }
        }

        SyncOptionVisuals();
        RebuildCard();

        var rootStack = new StackPanel { Spacing = 12, Width = 440 };
        rootStack.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(uiPalette.Inset),
            Padding = new Thickness(10, 14, 10, 14),
            Child = previewHost,
        });
        rootStack.Children.Add(controlsBar);

        if (!string.IsNullOrWhiteSpace(thought))
        {
            var thRow = new Grid
            {
                Padding = new Thickness(12, 6, 12, 6),
                Background = ThemeHelper.Brush(uiPalette.Card),
                BorderBrush = ThemeHelper.Brush(uiPalette.Line),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
            };
            thRow.Children.Add(new TextBlock { Text = "带上想法", FontSize = 12.5, Foreground = ThemeHelper.Brush(uiPalette.T1), VerticalAlignment = VerticalAlignment.Center });
            var sw = new ToggleSwitch { IsOn = includeThought, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 0, OnContent = "", OffContent = "" };
            sw.Toggled += (_, _) => { includeThought = sw.IsOn; RebuildCard(); };
            thRow.Children.Add(sw);
            rootStack.Children.Add(thRow);
        }

        var statusText = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(uiPalette.T3), HorizontalAlignment = HorizontalAlignment.Center };

        var textBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "纯文字", HorizontalAlignment = HorizontalAlignment.Stretch };
        var savePngBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "存图", HorizontalAlignment = HorizontalAlignment.Stretch };
        var copyImgBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "复制图片", HorizontalAlignment = HorizontalAlignment.Stretch };

        textBtn.Click += (_, _) =>
        {
            string cite = string.IsNullOrWhiteSpace(author) ? $"——《{bookTitle}》" : $"——《{bookTitle}》 {author}";
            string txt = includeThought && !string.IsNullOrWhiteSpace(thought)
                ? $"“{quote.Trim()}”\n{thought.Trim()}\n{cite}"
                : $"“{quote.Trim()}”\n{cite}";
            var dp = new DataPackage();
            dp.SetText(txt);
            Clipboard.SetContent(dp);
            statusText.Text = "✓ 已复制纯文字书摘";
        };

        savePngBtn.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileSavePicker { SuggestedFileName = $"{bookTitle}-书摘" };
                picker.FileTypeChoices.Add("PNG 图片", [".png"]);
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(ownerWindow));
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;

                using var pngStream = await RenderElementToPngStreamAsync(currentCard);
                pngStream.Seek(0);
                byte[] bytes = new byte[pngStream.Size];
                await pngStream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
                await File.WriteAllBytesAsync(file.Path, bytes);
                statusText.Text = $"✓ 已保存到 {file.Name}";
            }
            catch (Exception ex) { statusText.Text = $"保存失败：{ex.Message}"; }
        };

        copyImgBtn.Click += async (_, _) =>
        {
            try
            {
                var stream = await RenderElementToPngStreamAsync(currentCard);
                var dp = new DataPackage();
                dp.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                Clipboard.SetContent(dp);
                statusText.Text = "✓ 已复制卡片图片，可直接粘贴发送";
            }
            catch (Exception ex) { statusText.Text = $"复制失败：{ex.Message}"; }
        };

        var actionGrid = new Grid { ColumnSpacing = 8 };
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        Grid.SetColumn(savePngBtn, 1);
        Grid.SetColumn(copyImgBtn, 2);
        actionGrid.Children.Add(textBtn);
        actionGrid.Children.Add(savePngBtn);
        actionGrid.Children.Add(copyImgBtn);

        rootStack.Children.Add(actionGrid);
        rootStack.Children.Add(statusText);

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "分享书摘",
            Content = rootStack,
            CloseButtonText = "关闭",
        };
        await dlg.ShowAsync();
    }

    internal static async Task SaveElementPngAsync(UIElement element, string filePath)
    {
        using var stream = await RenderElementToPngStreamAsync(element);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        await File.WriteAllBytesAsync(filePath, bytes);
    }

    private static async Task<InMemoryRandomAccessStream> RenderElementToPngStreamAsync(UIElement element)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);
        var pixelBuffer = await rtb.GetPixelsAsync();
        byte[] pixels = pixelBuffer.ToArray();

        var outStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth,
            (uint)rtb.PixelHeight,
            144, 144,
            pixels);
        await encoder.FlushAsync();
        outStream.Seek(0);
        return outStream;
    }
}
