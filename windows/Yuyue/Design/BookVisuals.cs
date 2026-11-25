using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Yuyue.Core;

namespace Yuyue.Design;

/// <summary>
/// Shared book cover, cloth tone, Ex-Libris emblem, and highlighted quote visual builders.
/// Decoupled from any specific Window class so Library, Reader, Search, and Record pages share one pure implementation.
/// </summary>
public static class BookVisuals
{
    private static readonly (Color Light, Color Dark)[] PaperBookClothTones =
    [
        (Color.FromArgb(255, 233, 227, 215), Color.FromArgb(255, 50, 45, 39)), // warm linen
        (Color.FromArgb(255, 221, 229, 225), Color.FromArgb(255, 39, 48, 45)), // muted sage
        (Color.FromArgb(255, 222, 227, 236), Color.FromArgb(255, 40, 45, 54)), // slate mist
        (Color.FromArgb(255, 235, 224, 218), Color.FromArgb(255, 54, 43, 39)), // dusty terracotta
        (Color.FromArgb(255, 229, 225, 235), Color.FromArgb(255, 47, 43, 53)), // soft wisteria
        (Color.FromArgb(255, 235, 230, 214), Color.FromArgb(255, 52, 48, 37)), // pale ochre
    ];

    public static Color PaperClothColor(string title, bool dark)
    {
        ulong hash = 5381;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(title ?? ""))
            hash = ((hash << 5) + hash) + b;
        var pair = PaperBookClothTones[(int)(hash % (ulong)PaperBookClothTones.Length)];
        return dark ? pair.Dark : pair.Light;
    }

    public readonly record struct CollectionClothTone(
        Color WellTop, Color WellBottom, Color WellBorder, Color Ink,
        Color FolioFront, Color FolioBack, Color FolioBorder, Color FolioLine);

    public static CollectionClothTone ResolveCollectionClothTone(string id, string? iconKey, bool dark, bool eink)
    {
        CollectionClothTone Make(Color lt, Color lb, Color dt, Color db, Color li, Color di)
        {
            var ink = dark ? di : li;
            return new CollectionClothTone(
                dark ? dt : lt,
                dark ? db : lb,
                dark ? Color.FromArgb(15, 255, 255, 255) : Color.FromArgb(26, li.R, li.G, li.B),
                ink,
                dark ? Color.FromArgb(255, 46, 45, 42) : Color.FromArgb(255, 253, 252, 249),
                dark ? Color.FromArgb(10, 255, 255, 255) : Color.FromArgb(140, 255, 255, 255),
                dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(38, li.R, li.G, li.B),
                dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(26, li.R, li.G, li.B));
        }

        if (eink)
        {
            return Make(
                Color.FromArgb(255, 238, 237, 233), Color.FromArgb(255, 232, 231, 226),
                Color.FromArgb(255, 34, 34, 34), Color.FromArgb(255, 30, 30, 30),
                Color.FromArgb(255, 60, 60, 60), Color.FromArgb(255, 200, 200, 200));
        }

        int kind;
        if (!string.IsNullOrWhiteSpace(iconKey))
        {
            kind = iconKey switch
            {
                "globe" or "eye" => 1,
                "sparkle" or "clock" => 2,
                "moon" or "key" => 3,
                "heart" or "quote.opening" or "sun.max" => 4,
                "headphones" or "cup.and.saucer" => 0,
                _ => 5,
            };
        }
        else
        {
            ulong h = 5381;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(id ?? ""))
                h = ((h << 5) + h) + b;
            kind = (int)(h % 6);
        }

        return kind switch
        {
            1 => Make(Color.FromArgb(255, 237, 239, 231), Color.FromArgb(255, 230, 233, 222), Color.FromArgb(255, 32, 36, 32), Color.FromArgb(255, 28, 32, 28), Color.FromArgb(255, 78, 98, 76), Color.FromArgb(255, 162, 184, 158)),
            2 => Make(Color.FromArgb(255, 237, 239, 241), Color.FromArgb(255, 229, 232, 236), Color.FromArgb(255, 32, 35, 39), Color.FromArgb(255, 28, 31, 35), Color.FromArgb(255, 74, 92, 110), Color.FromArgb(255, 158, 178, 198)),
            3 => Make(Color.FromArgb(255, 239, 237, 239), Color.FromArgb(255, 232, 229, 233), Color.FromArgb(255, 35, 34, 39), Color.FromArgb(255, 30, 29, 34), Color.FromArgb(255, 90, 84, 106), Color.FromArgb(255, 174, 168, 192)),
            4 => Make(Color.FromArgb(255, 243, 237, 234), Color.FromArgb(255, 237, 229, 225), Color.FromArgb(255, 39, 33, 33), Color.FromArgb(255, 34, 29, 29), Color.FromArgb(255, 130, 88, 84), Color.FromArgb(255, 202, 162, 158)),
            5 => Make(Color.FromArgb(255, 241, 238, 232), Color.FromArgb(255, 234, 230, 222), Color.FromArgb(255, 35, 34, 32), Color.FromArgb(255, 31, 30, 28), Color.FromArgb(255, 96, 90, 80), Color.FromArgb(255, 182, 176, 166)),
            _ => Make(Color.FromArgb(255, 243, 239, 231), Color.FromArgb(255, 237, 231, 220), Color.FromArgb(255, 37, 34, 30), Color.FromArgb(255, 32, 29, 26), Color.FromArgb(255, 116, 92, 66), Color.FromArgb(255, 198, 174, 146)),
        };
    }

    public static (bool IsSymbol, string Value, double FontSize) ResolveExLibrisEmblem(string title, string? explicitIcon)
    {
        if (!string.IsNullOrWhiteSpace(explicitIcon)) return (true, explicitIcon, 15.5);
        string trimmed = (title ?? "").Trim();
        if (trimmed.Length == 0) return (true, "books", 15.5);
        if (trimmed.All(c => c <= 127))
        {
            if (trimmed.All(char.IsDigit))
            {
                if (trimmed.Length <= 2) return (false, trimmed, 15);
                if (trimmed.Length <= 4) return (false, trimmed, 12);
                return (true, "books", 15.5);
            }
            if (trimmed.Length <= 3) return (false, trimmed.ToUpperInvariant(), trimmed.Length == 1 ? 16 : 13.5);
            char firstLetter = trimmed.FirstOrDefault(char.IsLetter);
            return firstLetter != '\0' ? (false, char.ToUpperInvariant(firstLetter).ToString(), 16) : (true, "books", 15.5);
        }
        return (false, trimmed[..1], 16.5);
    }

    public static UIElement BuildHighlightedQuote(Mark mark, YYPalette palette, bool dark, double fontSize = 13.5)
    {
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        double lineHeight = Math.Round(fontSize * 1.65);

        if (mark.Style == MarkStyle.Wave)
        {
            var container = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            var tb = new TextBlock
            {
                Text = mark.Text,
                FontFamily = serif,
                FontSize = fontSize,
                LineHeight = lineHeight,
                Foreground = ThemeHelper.Brush(palette.T1),
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0, 0, 0, 3),
            };
            var wavePath = new Microsoft.UI.Xaml.Shapes.Path
            {
                Stroke = ThemeHelper.Brush(palette.HlR),
                StrokeThickness = 1.15,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
            };
            tb.SizeChanged += (_, e) =>
            {
                double w = e.NewSize.Width;
                double h = e.NewSize.Height;
                if (w < 4 || h < 4) return;
                int lines = Math.Max(1, (int)Math.Round((h - 3) / lineHeight));
                var geo = new PathGeometry();
                const double step = 3.6;
                const double amp = 2.0;
                for (int li = 0; li < lines; li++)
                {
                    double y = Math.Min(h - 1.5, (li + 1) * lineHeight - 1.5);
                    var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(0, y), IsClosed = false };
                    double x = 0;
                    bool up = true;
                    while (x < w - 0.5)
                    {
                        double next = Math.Min(x + step, w);
                        fig.Segments.Add(new QuadraticBezierSegment
                        {
                            Point1 = new Windows.Foundation.Point((x + next) / 2.0, y + (up ? -amp : amp)),
                            Point2 = new Windows.Foundation.Point(next, y),
                        });
                        x = next;
                        up = !up;
                    }
                    geo.Figures.Add(fig);
                }
                wavePath.Data = geo;
            };
            container.Children.Add(L10n.Skip(tb));
            container.Children.Add(wavePath);
            return container;
        }

        if (mark.Style is MarkStyle.Underline or MarkStyle.Dotted)
        {
            var container = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            var tb = new TextBlock
            {
                Text = mark.Text,
                FontFamily = serif,
                FontSize = fontSize,
                LineHeight = lineHeight,
                Foreground = ThemeHelper.Brush(palette.T1),
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0, 0, 0, 2),
            };
            var linePath = new Microsoft.UI.Xaml.Shapes.Path
            {
                Stroke = ThemeHelper.Brush(palette.T2),
                StrokeThickness = mark.Style == MarkStyle.Dotted ? 1.1 : 1.25,
                Opacity = mark.Style == MarkStyle.Dotted ? 0.65 : 0.85,
                IsHitTestVisible = false,
            };
            if (mark.Style == MarkStyle.Dotted)
                linePath.StrokeDashArray = new DoubleCollection { 1.5, 2.5 };
            tb.SizeChanged += (_, e) =>
            {
                double w = e.NewSize.Width;
                double h = e.NewSize.Height;
                if (w < 4 || h < 4) return;
                int lines = Math.Max(1, (int)Math.Round((h - 2) / lineHeight));
                var geo = new PathGeometry();
                for (int li = 0; li < lines; li++)
                {
                    double y = Math.Min(h - 1.0, (li + 1) * lineHeight - 1.5);
                    var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(0, y), IsClosed = false };
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(w, y) });
                    geo.Figures.Add(fig);
                }
                linePath.Data = geo;
            };
            container.Children.Add(L10n.Skip(tb));
            container.Children.Add(linePath);
            return container;
        }

        Color hlColor = mark.Style switch
        {
            MarkStyle.Blue => palette.MkB,
            MarkStyle.Pink => palette.MkP,
            _ => palette.MkY,
        };
        if (!dark) hlColor = Color.FromArgb(145, hlColor.R, hlColor.G, hlColor.B);

        return new Border
        {
            Background = ThemeHelper.Brush(hlColor),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1.5, 4, 1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = L10n.Skip(new TextBlock
            {
                Text = mark.Text,
                FontFamily = serif,
                FontSize = fontSize,
                LineHeight = lineHeight,
                Foreground = ThemeHelper.Brush(palette.T1),
                TextWrapping = TextWrapping.Wrap,
            }),
        };
    }

    /// <summary>
    /// Creates a crisp spine-lit book cover (portrait for ebooks/PDF, bottom-aligned square for audiobooks).
    /// </summary>
    public static Border CreateCoverView(
        BookSummary book,
        double width,
        double height,
        bool dark,
        bool hasPairedAudio = false,
        bool isPlaying = false,
        Color? playingAccent = null,
        UIElement? shadowReceiver = null,
        bool eink = false)
    {
        var palette = ThemeHelper.Palette(dark);
        bool isAudio = book.Format == BookFormat.Audio;
        bool isPaper = book.Record;
        double actualH = isAudio ? width : height;

        Color placeholderBg = isAudio
            ? palette.Inset
            : (isPaper
                ? PaperClothColor(book.Title, dark)
                : (dark ? Color.FromArgb(255, 44, 40, 36) : Color.FromArgb(255, 235, 230, 221)));

        var inner = new Border
        {
            Width = width,
            Height = actualH,
            VerticalAlignment = isAudio ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(isAudio ? Math.Max(6, width * 0.08) : 6),
            BorderBrush = ThemeHelper.Brush(dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(18, 42, 43, 46)),
            BorderThickness = new Thickness(0.5),
            Background = ThemeHelper.Brush(placeholderBg),
            Opacity = book.Available || isPaper ? 1.0 : 0.55,
        };
        if (shadowReceiver is not null && !eink)
            ThemeHelper.ApplyElevation(inner, shadowReceiver, 12f);

        var layer = new Grid();

        if (!string.IsNullOrEmpty(book.CoverPath) && File.Exists(book.CoverPath))
        {
            var bmp = new BitmapImage { DecodePixelWidth = Math.Max(48, (int)Math.Ceiling(width * 2)) };
            bmp.UriSource = new Uri(book.CoverPath);
            layer.Children.Add(new Image { Source = bmp, Stretch = Stretch.UniformToFill });

            // Subtle physical book spine light/crease on real covers (non-audio)
            if (!isAudio && width >= 44)
            {
                layer.Children.Add(new Border
                {
                    Width = Math.Max(4, width * 0.065),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    IsHitTestVisible = false,
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new Windows.Foundation.Point(0, 0.5),
                        EndPoint = new Windows.Foundation.Point(1, 0.5),
                        GradientStops =
                        {
                            new GradientStop { Color = Color.FromArgb(34, 0, 0, 0), Offset = 0.0 },
                            new GradientStop { Color = Color.FromArgb(40, 255, 255, 255), Offset = 0.35 },
                            new GradientStop { Color = Color.FromArgb(18, 0, 0, 0), Offset = 0.72 },
                            new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 1.0 },
                        },
                    },
                });
            }
        }
        else if (isAudio)
        {
            layer.Children.Add(new ContentControl
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Content = YYIcons.Make("headphones", Math.Max(14, width * 0.34), ThemeHelper.Brush(palette.T3)),
            });
        }
        else if (width <= 40)
        {
            // Miniature thumbnail (e.g. Explorer Details list): crisp spine bar + centered book/format mark (never broken text fragments)
            layer.Children.Add(new Border
            {
                Width = 3,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsHitTestVisible = false,
                Background = ThemeHelper.Brush(dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(24, 42, 43, 46)),
            });
            UIElement miniMark = book.Format == BookFormat.Pdf
                ? new TextBlock
                {
                    Text = "PDF",
                    FontSize = 7.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = ThemeHelper.Brush(palette.T2),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0),
                }
                : new ContentControl
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0),
                    Content = YYIcons.Make("book-open", 11.5, ThemeHelper.Brush(palette.T2)),
                };
            layer.Children.Add(miniMark);
        }
        else
        {
            double unit = width / 100.0;
            layer.Children.Add(new Border
            {
                Width = Math.Max(3, 8 * unit),
                HorizontalAlignment = HorizontalAlignment.Left,
                IsHitTestVisible = false,
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0.5),
                    EndPoint = new Windows.Foundation.Point(1, 0.5),
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(20, 0, 0, 0), Offset = 0.0 },
                        new GradientStop { Color = Color.FromArgb(56, 255, 255, 255), Offset = 0.35 },
                        new GradientStop { Color = Color.FromArgb(14, 0, 0, 0), Offset = 0.68 },
                        new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 1.0 },
                    },
                },
            });

            var face = L10n.Skip(new Grid { Padding = new Thickness((isPaper ? 14 : 12) * unit, 11 * unit, 10 * unit, 10 * unit) });
            face.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            face.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var topStack = new StackPanel { Spacing = Math.Max(2, 4.5 * unit), VerticalAlignment = VerticalAlignment.Top };
            topStack.Children.Add(new TextBlock
            {
                Text = book.Title,
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = Math.Max(8.5, 14 * unit),
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(palette.T1),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = width < 48 ? 2 : 4,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            if (book.Authors.Length > 0 && width >= 40)
            {
                topStack.Children.Add(new TextBlock
                {
                    Text = book.Authors[0],
                    FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                    FontSize = Math.Max(7.5, 9.5 * unit),
                    Foreground = ThemeHelper.Brush(palette.T2),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            face.Children.Add(topStack);

            if (book.Format == BookFormat.Pdf && width >= 38)
            {
                var pdfPill = new Border
                {
                    CornerRadius = new CornerRadius(Math.Max(2, 3 * unit)),
                    Background = ThemeHelper.Brush(Color.FromArgb(dark ? (byte)28 : (byte)16, palette.T1.R, palette.T1.G, palette.T1.B)),
                    Padding = new Thickness(Math.Max(3, 4.5 * unit), Math.Max(0.8, 1.4 * unit), Math.Max(3, 4.5 * unit), Math.Max(0.8, 1.4 * unit)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Child = new TextBlock
                    {
                        Text = "PDF",
                        FontSize = Math.Max(6.5, 8 * unit),
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(palette.T2),
                    },
                };
                Grid.SetRow(pdfPill, 1);
                face.Children.Add(pdfPill);
            }
            layer.Children.Add(face);
        }

        // Cloud / Audio / Playing bottom-right badge
        if (!book.Available && !book.Record)
        {
            double badgeSize = width < 30 ? 12 : (width < 56 ? 15 : 19);
            double iconSz = width < 30 ? 7.5 : (width < 56 ? 8.5 : 10.5);
            double margin = width < 30 ? 2 : (width < 56 ? 3 : 5);
            layer.Children.Add(new Border
            {
                Width = badgeSize,
                Height = badgeSize,
                CornerRadius = new CornerRadius(badgeSize / 2),
                Background = ThemeHelper.Brush(palette.Card),
                BorderBrush = ThemeHelper.Brush(palette.Line),
                BorderThickness = new Thickness(0.5),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, margin, margin),
                Child = YYIcons.Make("icloud.and.arrow.down", iconSz, ThemeHelper.Brush(palette.T2)),
            });
        }
        else if (isPlaying && width >= 44)
        {
            var accent = playingAccent ?? palette.Ok;
            layer.Children.Add(new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = ThemeHelper.Brush(accent),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 5, 5),
                Child = YYIcons.Make("wave", 11, ThemeHelper.Brush(Microsoft.UI.Colors.White)),
            });
        }
        else if (width >= 60 && (isAudio || hasPairedAudio))
        {
            layer.Children.Add(new Border
            {
                Width = 19,
                Height = 19,
                CornerRadius = new CornerRadius(9.5),
                Background = ThemeHelper.Brush(Color.FromArgb(220, palette.Card.R, palette.Card.G, palette.Card.B)),
                BorderBrush = ThemeHelper.Brush(palette.Line),
                BorderThickness = new Thickness(0.5),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 5, 5),
                Child = YYIcons.Make("headphones", 10.5, ThemeHelper.Brush(palette.T1)),
            });
        }

        inner.Child = layer;
        if (!isAudio) return inner;

        // Bottom-align square audiobook covers inside the standard portrait slot height so shelf baselines stay perfectly even
        return new Border
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = inner,
        };
    }
}
