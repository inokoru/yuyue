using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;
using WinRT.Interop;
using Yuyue.Core;
using Yuyue.Design;

namespace Yuyue.Reader;

/// <summary>
/// 1:1 port of <c>apple/Yuyue/Library/QuoteShare.swift</c>:
/// 4 color looks (<c>封面色 / 纸本 / 素白 / 暗黑</c>), 3 aspect shapes (<c>竖图 / 方图 / 横图</c>),
/// <c>带上想法</c> switch, chapter subtitle, persisted preferences, long-quote scaling,
/// mini book cover in footer, high-DPI crisp PNG export, and native Windows system Share sheet.
/// </summary>
public static class QuoteShareDialog
{
    private const double ExportScale = 3.0;

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow([In] IntPtr appWindow);
    }

    private static readonly Guid DataTransferManagerIid = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");
    public static async Task ShowAsync(
        Window ownerWindow, XamlRoot xamlRoot,
        string quote, string? thought, string bookTitle, string? author, string? coverPath = null,
        string? chapter = null, LibraryService? library = null)
    {
        bool darkUi = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var uiPalette = ThemeHelper.Palette(darkUi);
        var libSvc = library ?? App.CurrentApp.Library;
        var prefs = libSvc?.Prefs;
        var tint = prefs?.Tint ?? YYTints.Camel;

        string look = prefs?.QuoteShareLook is "cover" or "kami" or "plain" or "dark" ? prefs.QuoteShareLook : "cover";
        string shape = prefs?.QuoteShareShape is "tall" or "square" or "wide" ? prefs.QuoteShareShape : "tall";
        bool includeThought = !string.IsNullOrWhiteSpace(thought) && (prefs?.QuoteShareIncludeThought ?? true);
        ContentDialog? dlgRef = null;

        Color coverTone = Color.FromArgb(255, 58, 72, 92);
        if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
        {
            try { coverTone = await CoverColorHelper.LoadLeadCardColorAsync(coverPath, dark: true); }
            catch { }
        }

        var previewHost = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var exportCanvas = new Canvas { Width = 0, Height = 0, Opacity = 0.004, IsHitTestVisible = false };
        Border currentCard = new();

        Border BuildCardElement()
        {
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
            string trimmedQuote = quote.Trim();
            double baseQuoteSize = shape == "wide" ? 14.5 : 15.5;
            if (trimmedQuote.Length > 260) baseQuoteSize -= 2.5;
            else if (trimmedQuote.Length > 140) baseQuoteSize -= 1.5;
            bodyStack.Children.Add(new TextBlock
            {
                Text = trimmedQuote,
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = baseQuoteSize,
                LineHeight = Math.Round(baseQuoteSize * 1.65),
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
            string subCite = string.Join(" · ", new[] { author, chapter }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (!string.IsNullOrWhiteSpace(subCite))
            {
                citeStack.Children.Add(new TextBlock
                {
                    Text = subCite,
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

            var innerBorder = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderBrush = ThemeHelper.Brush(rule),
                BorderThickness = new Thickness(0.75),
                Padding = new Thickness(20, 20, 20, 16),
                Child = grid,
            };

            return new Border
            {
                Width = w,
                MinHeight = minH,
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(bg),
                Padding = new Thickness(9),
                Child = innerBorder,
            };
        }

        void RebuildCard()
        {
            previewHost.Children.Clear();
            exportCanvas.Children.Clear();
            currentCard = BuildCardElement();
            previewHost.Children.Add(exportCanvas);
            previewHost.Children.Add(currentCard);
        }

        async Task<InMemoryRandomAccessStream> RenderHighResCardStreamAsync()
        {
            double baseW = currentCard.ActualWidth > 0 ? currentCard.ActualWidth : currentCard.Width;
            double scaledW = Math.Round(baseW * ExportScale);
            var hiResCard = BuildCardElement();
            var vb = new Viewbox
            {
                Width = scaledW,
                Stretch = Stretch.Uniform,
                Child = hiResCard,
            };
            exportCanvas.Children.Clear();
            exportCanvas.Children.Add(vb);
            try
            {
                vb.UpdateLayout();
                await Task.Delay(24);
                var stream = await RenderElementToPngStreamAsync(vb, dpi: 96.0 * ExportScale);
                if (stream.Size > 0) return stream;
            }
            catch { }
            finally
            {
                exportCanvas.Children.Clear();
            }
            return await RenderElementToPngStreamAsync(currentCard, dpi: 144.0);
        }

        // 1:1 notes-quote/1.png: centered 4 color circles + active color name label, then full-width 3-segment shape bar
        var dotsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var dotBtns = new List<(string Key, string Label, Border Ring)>();
        var activeLookLabel = new TextBlock { FontSize = 12, Foreground = ThemeHelper.Brush(uiPalette.T2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        var shapeBtns = new List<(string Key, Button Btn)>();
        foreach (var (k, lbl, col) in new[]
        {
            ("cover", "封面色", coverTone),
            ("kami", "纸本", Color.FromArgb(255, 246, 241, 230)),
            ("plain", "素白", Color.FromArgb(255, 255, 255, 255)),
            ("dark", "暗黑", Color.FromArgb(255, 20, 20, 23)),
        })
        {
            var ring = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(2),
                BorderThickness = new Thickness(1.5),
                Child = new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(col),
                    BorderBrush = ThemeHelper.Brush(Color.FromArgb(30, 0, 0, 0)),
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
            b.Click += (_, _) =>
            {
                look = key;
                if (prefs is not null) { prefs.QuoteShareLook = key; libSvc?.SavePrefs(); }
                SyncOptionVisuals();
                RebuildCard();
            };
            dotBtns.Add((key, lbl, ring));
            dotsRow.Children.Add(b);
        }
        dotsRow.Children.Add(activeLookLabel);

        var shapeSeg = new Grid { ColumnSpacing = 3 };
        var shapeItems = new[] { ("tall", "竖图"), ("square", "方图"), ("wide", "横图") };
        for (int i = 0; i < shapeItems.Length; i++)
        {
            shapeSeg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (sk, sl) = shapeItems[i];
            var sb = new Button
            {
                Content = sl,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 30,
                MinHeight = 30,
                Padding = new Thickness(0),
                FontSize = 12.5,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                UseSystemFocusVisuals = false,
            };
            string key = sk;
            sb.Click += (_, _) =>
            {
                shape = key;
                if (prefs is not null) { prefs.QuoteShareShape = key; libSvc?.SavePrefs(); }
                SyncOptionVisuals();
                RebuildCard();
            };
            Grid.SetColumn(sb, i);
            shapeSeg.Children.Add(sb);
            shapeBtns.Add((key, sb));
        }
        var shapeWrap = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(uiPalette.Inset),
            Padding = new Thickness(3),
            Child = shapeSeg,
        };

        void SyncOptionVisuals()
        {
            foreach (var (k, lbl, ring) in dotBtns)
            {
                bool active = k == look;
                ring.BorderBrush = active ? ThemeHelper.Brush(uiPalette.T1) : new SolidColorBrush(Colors.Transparent);
                if (active) activeLookLabel.Text = L10n.T(lbl);
            }
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

        var rootStack = new StackPanel { Spacing = 12, Padding = new Thickness(22, 18, 22, 18) };
        rootStack.Children.Add(YYChrome.MakeSheetHeader("分享书摘", null, () => dlgRef?.Hide(), darkUi, titleSize: 16));
        rootStack.Children.Add(previewHost);
        rootStack.Children.Add(dotsRow);
        rootStack.Children.Add(shapeWrap);

        if (!string.IsNullOrWhiteSpace(thought))
        {
            var thRow = new Grid
            {
                Padding = new Thickness(14, 8, 14, 8),
                MinHeight = 42,
                Background = ThemeHelper.Brush(uiPalette.Card),
                BorderBrush = ThemeHelper.Brush(uiPalette.Line),
                BorderThickness = new Thickness(0.5),
                CornerRadius = new CornerRadius(11),
            };
            thRow.Children.Add(new TextBlock { Text = "带上想法", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(uiPalette.T1), VerticalAlignment = VerticalAlignment.Center });
            var sw = new ToggleSwitch { IsOn = includeThought, HorizontalAlignment = HorizontalAlignment.Right };
            sw.Toggled += (_, _) =>
            {
                includeThought = sw.IsOn;
                if (prefs is not null) { prefs.QuoteShareIncludeThought = includeThought; libSvc?.SavePrefs(); }
                RebuildCard();
            };
            thRow.Children.Add(sw);
            rootStack.Children.Add(thRow);
        }

        var statusText = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(uiPalette.T3), HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };

        StackPanel MakeActionLabel(string icon, string text, Color fg)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(YYIcons.Make(icon, 14, ThemeHelper.Brush(fg)));
            sp.Children.Add(new TextBlock { Text = L10n.T(text), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(fg), VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        string BuildPlainShareText()
        {
            string cite = string.IsNullOrWhiteSpace(author) ? $"——《{bookTitle}》" : $"——《{bookTitle}》 {author}";
            return includeThought && !string.IsNullOrWhiteSpace(thought)
                ? $"“{quote.Trim()}”\n{thought.Trim()}\n{cite}"
                : $"“{quote.Trim()}”\n{cite}";
        }

        var tc = ThemeHelper.TintColors(tint, darkUi);
        var textBtn = YYChrome.MakeSheetPillButton(MakeActionLabel("text-quote", "纯文字", uiPalette.T1), () =>
        {
            var dp = new DataPackage();
            dp.SetText(BuildPlainShareText());
            Clipboard.SetContent(dp);
            statusText.Text = "✓ 已复制纯文字书摘";
            statusText.Visibility = Visibility.Visible;
        }, darkUi, tint, "secondary", height: 40, stretch: true);

        var savePngBtn = YYChrome.MakeSheetPillButton(MakeActionLabel("share-down", "存图", uiPalette.T1), async () =>
        {
            try
            {
                var picker = new FileSavePicker { SuggestedFileName = $"{SanitizeFileName(bookTitle)}-书摘" };
                picker.FileTypeChoices.Add("PNG 图片", [".png"]);
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(ownerWindow));
                var file = await picker.PickSaveFileAsync();
                if (file is null) return;

                using var pngStream = await RenderHighResCardStreamAsync();
                pngStream.Seek(0);
                byte[] bytes = new byte[pngStream.Size];
                await pngStream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
                await File.WriteAllBytesAsync(file.Path, bytes);
                statusText.Text = $"✓ 已保存到 {file.Name}";
                statusText.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { statusText.Text = $"保存失败：{ex.Message}"; statusText.Visibility = Visibility.Visible; }
        }, darkUi, tint, "secondary", height: 40, stretch: true);

        var shareBtn = YYChrome.MakeSheetPillButton(MakeActionLabel("share-up", "分享", tc.Accent), async () =>
        {
            try
            {
                var stream = await RenderHighResCardStreamAsync();
                stream.Seek(0);
                byte[] pngBytes = new byte[stream.Size];
                await stream.ReadAsync(pngBytes.AsBuffer(), (uint)pngBytes.Length, InputStreamOptions.None);
                stream.Seek(0);

                string shareTitle = $"《{bookTitle}》书摘";
                string shareText = BuildPlainShareText();
                bool shownNative = await TryShowNativeShareSheetAsync(ownerWindow, shareTitle, shareText, bookTitle, pngBytes);
                if (!shownNative)
                {
                    var dp = new DataPackage();
                    dp.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                    Clipboard.SetContent(dp);
                    statusText.Text = "✓ 已复制书摘卡片图片，可直接粘贴分享";
                    statusText.Visibility = Visibility.Visible;
                }
                else
                {
                    // Also place bitmap on clipboard as convenience alongside system Share sheet
                    var dp = new DataPackage();
                    dp.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                    try { Clipboard.SetContent(dp); } catch { }
                    statusText.Text = "✓ 已唤起系统分享面板（同时已复制到剪贴板）";
                    statusText.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex) { statusText.Text = $"分享失败：{ex.Message}"; statusText.Visibility = Visibility.Visible; }
        }, darkUi, tint, "warm", height: 40, stretch: true);

        var actionGrid = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 2, 0, 0) };
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(savePngBtn, 1);
        Grid.SetColumn(shareBtn, 2);
        actionGrid.Children.Add(textBtn);
        actionGrid.Children.Add(savePngBtn);
        actionGrid.Children.Add(shareBtn);

        rootStack.Children.Add(actionGrid);
        rootStack.Children.Add(statusText);
        L10n.ApplyTree(rootStack);

        dlgRef = YYChrome.CreateMacSheet(xamlRoot, darkUi, 456, rootStack, maxHeight: 820);
        await dlgRef.ShowAsync();
    }

    private static string SanitizeFileName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(raw.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "语阅" : cleaned;
    }

    private static async Task<bool> TryShowNativeShareSheetAsync(
        Window ownerWindow,
        string title,
        string plainText,
        string bookTitle,
        byte[] pngBytes)
    {
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(ownerWindow);
            if (hwnd == IntPtr.Zero) return false;

            string tempDir = Path.Combine(Path.GetTempPath(), "YuyueShare");
            Directory.CreateDirectory(tempDir);
            string tempFilePath = Path.Combine(tempDir, $"{SanitizeFileName(bookTitle)}-书摘.png");
            await File.WriteAllBytesAsync(tempFilePath, pngBytes);
            StorageFile? storageFile = null;
            try { storageFile = await StorageFile.GetFileFromPathAsync(tempFilePath); } catch { }

            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            Guid iid = DataTransferManagerIid;
            IntPtr dtmPtr = interop.GetForWindow(hwnd, ref iid);
            if (dtmPtr == IntPtr.Zero) return false;

            var dtm = MarshalInterface<DataTransferManager>.FromAbi(dtmPtr);
            Marshal.Release(dtmPtr);

            TypedEventHandler<DataTransferManager, DataRequestedEventArgs>? handler = null;
            handler = async (sender, args) =>
            {
                if (handler is not null) sender.DataRequested -= handler;
                var deferral = args.Request.GetDeferral();
                try
                {
                    var data = args.Request.Data;
                    data.Properties.Title = title;
                    data.Properties.Description = bookTitle;
                    data.SetText(plainText);

                    var mem = new InMemoryRandomAccessStream();
                    await mem.WriteAsync(pngBytes.AsBuffer());
                    mem.Seek(0);
                    data.SetBitmap(RandomAccessStreamReference.CreateFromStream(mem));

                    if (storageFile is not null)
                        data.SetStorageItems([storageFile]);
                }
                catch { }
                finally
                {
                    deferral.Complete();
                }
            };
            dtm.DataRequested += handler;
            interop.ShowShareUIForWindow(hwnd);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static async Task SaveElementPngAsync(UIElement element, string filePath)
    {
        using var stream = await RenderElementToPngStreamAsync(element);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        await File.WriteAllBytesAsync(filePath, bytes);
    }

    private static async Task<InMemoryRandomAccessStream> RenderElementToPngStreamAsync(UIElement element, double dpi = 144.0)
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
            dpi, dpi,
            pixels);
        await encoder.FlushAsync();
        outStream.Seek(0);
        return outStream;
    }
}
