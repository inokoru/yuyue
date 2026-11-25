using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using Yuyue.Core;

namespace Yuyue.Design;

/// <summary>
/// Shared macOS-aligned visual chrome builders: brand/service icon tiles, segmented bars,
/// borderless SettingsActionStyle buttons, keycaps, and the floating white BookMenuCard.
/// </summary>
public static class YYChrome
{
    private static string? ResolveBrandAssetFile(string key, bool dark) => key.Trim().ToLowerInvariant() switch
    {
        "obsidian" => "obsidian.png",
        "notion" => dark ? "notion-dark.png" : "notion.png",
        "flomo" => "flomo.png",
        "xinzhi" => "xinzhi.png",
        "azure" => "azure.png",
        "claude" or "anthropic" => "claude.png",
        "openai" => dark ? "openai-dark.png" : "openai.png",
        "deepseek" => "deepseek.png",
        "gemini" or "google" => "gemini.png",
        "openrouter" => dark ? "openrouter-dark.png" : "openrouter.png",
        "qwen" or "dashscope" or "aliyun" => "dashscope.png",
        "kimi" or "moonshot" => dark ? "moonshot-dark.png" : "moonshot.png",
        "glm" or "zhipu" => "zhipu.png",
        "doubao" or "volc" or "volcengine" => "volcengine.png",
        "siliconflow" => "siliconflow.png",
        "minimax" => "minimax.png",
        "grok" or "xai" => dark ? "xai-dark.png" : "xai.png",
        "mistral" => "mistral.png",
        "groq" => dark ? "groq-dark.png" : "groq.png",
        "ollama" => dark ? "ollama-dark.png" : "ollama.png",
        "lmstudio" => dark ? "lmstudio-dark.png" : "lmstudio.png",
        "magpie" => dark ? "magpie-dark.png" : "magpie.png",
        _ => null,
    };

    private static Image? TryCreateBrandImage(string fileName, double displaySize)
    {
        try
        {
            string full = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "Brands", fileName);
            if (!System.IO.File.Exists(full)) return null;
            var bmp = new BitmapImage
            {
                DecodePixelWidth = Math.Max(48, (int)Math.Round(displaySize * 3.0)),
                UriSource = new System.Uri(full, System.UriKind.Absolute),
            };
            return new Image
            {
                Width = displaySize,
                Height = displaySize,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Source = bmp,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Borderless bare brand/provider logo mark (used in 3-column picker sheets like <c>settings-companion-add/1.png</c>).
    /// </summary>
    public static FrameworkElement MakeBareServiceIcon(string key, bool dark, double size = 20)
    {
        if (ResolveBrandAssetFile(key, dark) is { } assetFile && TryCreateBrandImage(assetFile, size) is { } img)
            return img;

        var palette = ThemeHelper.Palette(dark);
        return key.Trim().ToLowerInvariant() switch
        {
            "mimo" or "xiaomi" => new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(Math.Round(size * 0.28)),
                Background = ThemeHelper.Brush(Color.FromArgb(255, 255, 105, 0)),
                Child = MakeGlyphMark("mi", Colors.Transparent, Colors.White, size * 0.92),
            },
            "fish" or "fishaudio" => YYIcons.Make("wave", size * 0.86, ThemeHelper.Brush(Color.FromArgb(255, 58, 134, 220))),
            "elevenlabs" => MakeGlyphMark("II", Colors.Transparent, dark ? Colors.White : Color.FromArgb(255, 28, 30, 34), size * 1.1),
            "local" or "voicestudio" or "indextts" or "gpt-sovits" or "cosyvoice" => YYIcons.Make("laptop", size * 0.86, ThemeHelper.Brush(palette.T1)),
            _ => YYIcons.Make("sparkle", size * 0.86, ThemeHelper.Brush(palette.T2)),
        };
    }

    /// <summary>
    /// Crisp macOS rounded-square service/brand icon tile (32×32 default, CornerRadius ~27%).
    /// Matches <c>MacListeningSettings.swift</c>, <c>MacCompanionSettings.swift</c>, and <c>MacNoteExportSettings.swift</c>.
    /// </summary>
    public static Border MakeServiceTile(string key, bool dark, double size = 32)
    {
        var palette = ThemeHelper.Palette(dark);
        double radius = Math.Round(size * 0.27);
        double iconSize = Math.Round(size * 0.50);
        double brandImgSize = Math.Round(size * 0.62);

        Border MakeTile(Color bg, Color border, UIElement child, double borderThickness = 0.75) => new()
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(radius),
            Background = ThemeHelper.Brush(bg),
            BorderBrush = ThemeHelper.Brush(border),
            BorderThickness = new Thickness(borderThickness),
            Child = child,
        };

        Color neutralBg = dark ? Color.FromArgb(255, 36, 37, 40) : Color.FromArgb(255, 250, 249, 246);
        Color neutralLine = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(20, 42, 43, 46);
        Color whiteTile = dark ? Color.FromArgb(255, 34, 35, 38) : Colors.White;

        string k = key.Trim().ToLowerInvariant();
        if (k == "xinzhi" && TryCreateBrandImage("xinzhi.png", size) is { } xinzhiImg)
        {
            // xinzhi.svg already includes its full-bleed warm gradient rounded square
            return MakeTile(Colors.Transparent, neutralLine, xinzhiImg, 0.5);
        }

        if (ResolveBrandAssetFile(k, dark) is { } brandAsset && TryCreateBrandImage(brandAsset, brandImgSize) is { } brandImg)
        {
            return MakeTile(whiteTile, neutralLine, brandImg);
        }

        switch (k)
        {
            case "kokoro":
                return MakeTile(neutralBg, neutralLine,
                    YYIcons.Make("wave", iconSize, ThemeHelper.Brush(dark ? Color.FromArgb(255, 214, 186, 152) : Color.FromArgb(255, 118, 92, 62))));

            case "edge":
            {
                var badge = new Border
                {
                    Width = Math.Round(size * 0.62),
                    Height = Math.Round(size * 0.62),
                    CornerRadius = new CornerRadius(Math.Round(size * 0.19)),
                    Background = ThemeHelper.Brush(Color.FromArgb(255, 35, 140, 156)),
                    Child = new TextBlock
                    {
                        Text = "e",
                        FontSize = Math.Round(size * 0.40),
                        FontWeight = FontWeights.Bold,
                        Foreground = ThemeHelper.Brush(Colors.White),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, -1, 0, 0),
                    },
                };
                return MakeTile(whiteTile, neutralLine, badge);
            }

            case "online" or "neural":
            {
                var badge = new Border
                {
                    Width = Math.Round(size * 0.64),
                    Height = Math.Round(size * 0.64),
                    CornerRadius = new CornerRadius(Math.Round(size * 0.32)),
                    Background = ThemeHelper.Brush(Color.FromArgb(255, 58, 146, 102)),
                    Child = YYIcons.Make("globe", Math.Round(size * 0.42), ThemeHelper.Brush(Colors.White)),
                };
                return MakeTile(whiteTile, neutralLine, badge);
            }

            case "system" or "pc" or "thispc":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("laptop", iconSize, ThemeHelper.Brush(palette.T1)));

            case "add":
                return MakeTile(neutralBg, neutralLine,
                    YYIcons.Make("plus", Math.Round(size * 0.42), ThemeHelper.Brush(palette.T2)));

            case "mimo" or "xiaomi":
            {
                var badge = new Border
                {
                    Width = Math.Round(size * 0.62),
                    Height = Math.Round(size * 0.62),
                    CornerRadius = new CornerRadius(Math.Round(size * 0.19)),
                    Background = ThemeHelper.Brush(Color.FromArgb(255, 255, 105, 0)),
                    Child = MakeGlyphMark("mi", Colors.Transparent, Colors.White, size * 0.78),
                };
                return MakeTile(whiteTile, neutralLine, badge);
            }

            case "fish" or "fishaudio":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("wave", iconSize, ThemeHelper.Brush(Color.FromArgb(255, 58, 134, 220))));

            case "elevenlabs":
                return MakeTile(whiteTile, neutralLine,
                    MakeGlyphMark("II", Colors.Transparent, dark ? Colors.White : Color.FromArgb(255, 34, 36, 40), size));

            default:
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("sparkle", iconSize, ThemeHelper.Brush(palette.T2)));
        }
    }

    private static TextBlock MakeGlyphMark(string glyph, Color _, Color fg, double tileSize) => new()
    {
        Text = glyph,
        FontSize = Math.Round(tileSize * 0.42),
        FontWeight = FontWeights.Bold,
        Foreground = ThemeHelper.Brush(fg),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// Borderless <c>SettingsActionStyle</c> text button (MacSettings.swift:314-331).
    /// </summary>
    public static Button MakeSettingsActionButton(string text, Action onClick, bool dark, bool destructive = false, bool enabled = true)
    {
        var palette = ThemeHelper.Palette(dark);
        string label = L10n.T(text);
        var btn = new Button
        {
            Content = label,
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Foreground = ThemeHelper.Brush(destructive ? palette.Danger : palette.T1),
            IsEnabled = enabled,
            Opacity = enabled ? 1.0 : 0.36,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(btn, label);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>
    /// Exact macOS segmented bar (<c>YYInsetBrush</c> pill track + white active capsule).
    /// </summary>
    public static Border MakeSegmentedBar(
        IReadOnlyList<(string Id, string Label)> items,
        string activeId,
        Action<string> onSelect,
        bool dark,
        double fontSize = 12,
        double height = 30)
    {
        var palette = ThemeHelper.Palette(dark);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (id, label) in items)
        {
            bool active = id == activeId;
            var btn = new Button
            {
                Content = label,
                Height = height - 4,
                MinHeight = height - 4,
                Padding = new Thickness(11, 0, 11, 0),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(active ? 0.5 : 0),
                BorderBrush = active ? ThemeHelper.Brush(dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(18, 42, 43, 46)) : null,
                Background = active ? ThemeHelper.Brush(palette.Card) : ThemeHelper.Brush(Colors.Transparent),
                Foreground = ThemeHelper.Brush(active ? palette.T1 : palette.T2),
                FontSize = fontSize,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                UseSystemFocusVisuals = false,
            };
            string captured = id;
            btn.Click += (_, _) => onSelect(captured);
            row.Children.Add(btn);
        }

        return new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(palette.Inset),
            Padding = new Thickness(2.5),
            Child = row,
        };
    }

    /// <summary>
    /// Crisp macOS keyboard hint keycap (used in QuickSearch, Podcast Desk, History).
    /// </summary>
    public static Border MakeKeycap(string text, bool dark)
    {
        var palette = ThemeHelper.Palette(dark);
        return new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = ThemeHelper.Brush(dark ? Color.FromArgb(255, 42, 43, 46) : Color.FromArgb(255, 244, 242, 238)),
            BorderBrush = ThemeHelper.Brush(palette.Line),
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(5, 1.5, 5, 1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.Medium,
                Foreground = ThemeHelper.Brush(palette.T2),
            },
        };
    }

    /// <summary>
    /// Exact macOS <c>BookMenuCard</c> (BookMenu.swift:56-234, shelf-menu/1.png):
    /// pure white floating 214px card with title + remaining header, 5 status rows, and grouped action rows with right-aligned icons.
    /// </summary>
    public static Flyout MakeBookMenuFlyout(
        BookSummary book,
        bool dark,
        YYTint tint,
        bool hasAudiobook,
        Action<ReadingStatus> onSetStatus,
        Action onOpen,
        Action onListenFromStart,
        Action onAudiobook,
        Action onEditInfo,
        Action onAddToCollection,
        Action onRevealInExplorer,
        Action onDelete)
    {
        var palette = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(tint, dark);
        var flyout = new Flyout
        {
            FlyoutPresenterStyle = (Style)Application.Current.Resources["YYFloatFlyoutPresenterStyle"],
            ShouldConstrainToRootBounds = true,
        };

        var root = new StackPanel { Width = 214, Spacing = 0 };

        // Header: title + author + progress/remaining
        var header = new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(14, 11, 14, 10),
        };
        var titleRow = new Grid { ColumnSpacing = 6 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.Children.Add(new TextBlock
        {
            Text = book.Title,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = ThemeHelper.Brush(palette.T1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 140,
        });
        string? author = book.Authors.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(author))
        {
            var auth = new TextBlock
            {
                Text = author,
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(palette.T3),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            Grid.SetColumn(auth, 1);
            titleRow.Children.Add(auth);
        }
        header.Children.Add(titleRow);

        if (book.Record)
        {
            var tagRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            tagRow.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = ThemeHelper.Brush(tc.Soft),
                Padding = new Thickness(5, 1, 5, 1),
                Child = new TextBlock { Text = "纸书", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tc.Accent) },
            });
            tagRow.Children.Add(new TextBlock { Text = StatusDisplayName(book.Status), FontSize = 10.5, Foreground = ThemeHelper.Brush(palette.T2), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(tagRow);
        }
        else if (book.Progress > 0.001)
        {
            var progGrid = new Grid { ColumnSpacing = 8 };
            progGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            progGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var track = new Border
            {
                Height = 2.5,
                CornerRadius = new CornerRadius(1.25),
                Background = ThemeHelper.Brush(palette.Line),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Clamp(book.Progress, 0.04, 1.0) * 116,
                    CornerRadius = new CornerRadius(1.25),
                    Background = ThemeHelper.Brush(tc.Fill),
                },
            };
            progGrid.Children.Add(track);
            var rem = new TextBlock
            {
                Text = FormatRemaining(book),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(palette.T2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(rem, 1);
            progGrid.Children.Add(rem);
            header.Children.Add(progGrid);
        }
        else
        {
            header.Children.Add(new TextBlock
            {
                Text = "还没读",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(palette.T2),
            });
        }
        root.Children.Add(header);

        void AddDivider() => root.Children.Add(new Border
        {
            Height = 0.5,
            Margin = new Thickness(14, 0, 14, 0),
            Background = ThemeHelper.Brush(palette.Line),
        });

        Button MakeRow(string label, string icon, Color fg, bool bold, Action action)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ThemeHelper.Brush(fg),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var ic = YYIcons.Make(icon, 13, ThemeHelper.Brush(fg));
            ic.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(ic, 1);
            g.Children.Add(ic);

            var btn = new Button
            {
                Content = g,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(9, 0, 9, 0),
                Margin = new Thickness(5, 0, 5, 0),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(0),
                Background = ThemeHelper.Brush(Colors.Transparent),
                UseSystemFocusVisuals = false,
            };
            btn.PointerEntered += (_, _) => btn.Background = ThemeHelper.Brush(palette.Inset);
            btn.PointerExited += (_, _) => btn.Background = ThemeHelper.Brush(Colors.Transparent);
            btn.Click += (_, _) =>
            {
                flyout.Hide();
                action();
            };
            return btn;
        }

        // Group 0: 5 Reading statuses (在读 / 想读 / 读过 / 搁置 / 弃读)
        AddDivider();
        var statusBox = new StackPanel { Spacing = 0, Padding = new Thickness(0, 4, 0, 4) };
        (ReadingStatus St, string Name, string Sym)[] statuses =
        [
            (ReadingStatus.Reading, "在读", "book-open"),
            (ReadingStatus.Want, "想读", "bookmark"),
            (ReadingStatus.Finished, "读过", "check-circle"),
            (ReadingStatus.Paused, "搁置", "pause-circle"),
            (ReadingStatus.Dropped, "弃读", "xmark-circle"),
        ];
        foreach (var (st, name, sym) in statuses)
        {
            bool cur = book.Status == st;
            ReadingStatus captured = st;
            statusBox.Children.Add(MakeRow(name, cur ? "checkmark" : sym, cur ? tc.Accent : palette.T1, cur, () => onSetStatus(captured)));
        }
        root.Children.Add(statusBox);

        // Group 1: Open / Listen / Audiobook (only for file-backed books)
        if (!book.Record)
        {
            AddDivider();
            var playBox = new StackPanel { Spacing = 0, Padding = new Thickness(0, 4, 0, 4) };
            bool isAudio = book.Format == BookFormat.Audio;
            string openLabel = isAudio
                ? (book.Progress > 0 ? "接着听" : "开始听")
                : (book.Progress > 0 ? "接着读" : "开始读");
            playBox.Children.Add(MakeRow(openLabel, isAudio ? "headphones" : "book-open", tc.Accent, true, onOpen));
            playBox.Children.Add(MakeRow("从头听", "headphones", palette.T1, false, onListenFromStart));
            if (hasAudiobook)
                playBox.Children.Add(MakeRow("有声书…", "wave", palette.T1, false, onAudiobook));
            root.Children.Add(playBox);
        }

        // Group 2: Edit / Collection / Reveal / Delete
        AddDivider();
        var manageBox = new StackPanel { Spacing = 0, Padding = new Thickness(0, 4, 0, 4) };
        manageBox.Children.Add(MakeRow("编辑信息…", "pencil", palette.T1, false, onEditInfo));
        manageBox.Children.Add(MakeRow("加入合集…", "stack", palette.T1, false, onAddToCollection));
        if (!book.Record)
            manageBox.Children.Add(MakeRow("在资源管理器中显示", "folder", palette.T1, false, onRevealInExplorer));
        manageBox.Children.Add(MakeRow("删除", "trash", palette.Danger, false, onDelete));
        root.Children.Add(manageBox);

        flyout.Content = root;
        return flyout;
    }

    private static string FormatRemaining(BookSummary book)
    {
        if (book.Format == BookFormat.Pdf && book.Page > 0) return $"第 {book.Page} 页";
        if (book.Progress >= 0.995) return "已读完";
        uint totalMin = book.Format == BookFormat.Audio ? book.ListenMinutes : book.ReadMinutes;
        if (totalMin == 0) return $"{(int)Math.Round(book.Progress * 100)}%";
        int rem = Math.Max(1, (int)Math.Round(totalMin * (1.0 - Math.Clamp(book.Progress, 0, 1))));
        return rem >= 60
            ? (rem % 60 == 0 ? $"还剩 {rem / 60} 小时" : $"还剩 {rem / 60} 小时 {rem % 60} 分")
            : $"还剩 {rem} 分";
    }

    private static string StatusDisplayName(ReadingStatus status) => status switch
    {
        ReadingStatus.Reading => "在读",
        ReadingStatus.Want => "想读",
        ReadingStatus.Finished => "读过",
        ReadingStatus.Paused => "搁置",
        ReadingStatus.Dropped => "弃读",
        _ => "想读",
    };

    /// <summary>
    /// 1:1 port of <c>PhoneSignature.swift</c> (<c>PencilSignature</c>) used in <c>MacUpdate.swift</c>:
    /// invisible when the About page opens; clicking the signature area or the <c>by nowtin · GPL-3.0</c>
    /// line beneath it writes out all three graphite strokes ("CTT") one after another.
    /// </summary>
    public static (FrameworkElement View, Action TriggerWrite) MakePencilSignature(double size, bool dark)
    {
        double k = size / 60.0;
        double w = size;
        double h = size * 56.0 / 60.0;
        Color lead = dark ? Color.FromArgb(255, 217, 213, 205) : Color.FromArgb(255, 47, 46, 45);

        Point Pt(double x, double y) => new((x - 8.0) * k, (y - 6.0) * k);

        // Pre-sampled arc-length tables for exact smooth trim(0..t) at 60fps
        var s0 = SampleMultiBezier([
            (Pt(24, 9),    Pt(19, 15),    Pt(13.5, 25),  Pt(11.5, 33)),
            (Pt(11.5, 33), Pt(10, 39),    Pt(13, 41.5),  Pt(20, 41.5)),
            (Pt(20, 41.5), Pt(28, 41.5),  Pt(36, 40.5),  Pt(44, 39.5)),
            (Pt(44, 39.5), Pt(51, 38.5),  Pt(58, 37.5),  Pt(64, 37)),
        ]);
        var s1 = SampleMultiBezier([
            (Pt(21.5, 17), Pt(27, 18.5),  Pt(31.5, 18.8), Pt(35, 18.2)),
            (Pt(35, 18.2), Pt(31, 21),    Pt(27.5, 26),   Pt(26.3, 33)),
            (Pt(26.3, 33), Pt(25.3, 40),  Pt(24.6, 47),   Pt(25.2, 53)),
        ]);
        var s2 = SampleMultiBezier([
            (Pt(43, 17.5), Pt(48, 18.8),  Pt(52.5, 17.8), Pt(56.5, 15.5)),
            (Pt(56.5, 15.5), Pt(51, 19),  Pt(45.5, 24),   Pt(43.3, 31)),
            (Pt(43.3, 31), Pt(41.5, 39),  Pt(40.3, 49),   Pt(41, 59.5)),
        ]);
        var pressed = SampleMultiBezier([
            (Pt(40, 40.2), Pt(48, 39), Pt(56, 37.8), Pt(63.5, 37.2)),
        ]);

        Polyline MakeLine(double thickness, double opacity, double dx = 0, double dy = 0, bool grainDash = false)
        {
            var pl = new Polyline
            {
                Stroke = ThemeHelper.Brush(lead),
                StrokeThickness = thickness,
                Opacity = opacity,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
            };
            if (grainDash)
            {
                pl.StrokeDashArray = [2.4, 0.65];
                pl.StrokeDashCap = PenLineCap.Round;
            }
            if (dx != 0 || dy != 0)
            {
                Canvas.SetLeft(pl, dx);
                Canvas.SetTop(pl, dy);
            }
            return pl;
        }

        var canvas = new Canvas
        {
            Width = w,
            Height = h,
            Background = new SolidColorBrush(Colors.Transparent),
            Opacity = 0.86,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var mainLines = new[]
        {
            MakeLine(1.9 * k, 0.82),
            MakeLine(1.9 * k, 0.82),
            MakeLine(1.9 * k, 0.82),
        };
        var grainLines = new[]
        {
            MakeLine(1.0 * k, 0.54, 0.28 * k, 0.22 * k, grainDash: true),
            MakeLine(1.0 * k, 0.54, 0.28 * k, 0.22 * k, grainDash: true),
            MakeLine(1.0 * k, 0.54, 0.28 * k, 0.22 * k, grainDash: true),
        };
        var pressedLine = MakeLine(2.6 * k, 0.46);

        for (int i = 0; i < 3; i++)
        {
            canvas.Children.Add(mainLines[i]);
            canvas.Children.Add(grainLines[i]);
            if (i == 0) canvas.Children.Add(pressedLine);
        }

        canvas.PointerEntered += (_, _) => canvas.Opacity = 1.0;
        canvas.PointerExited += (_, _) => canvas.Opacity = 0.86;

        var samples = new[] { s0, s1, s2 };
        double[] durations = [0.62, 0.50, 0.56];
        double[] starts = [0.10, 0.10 + 0.62 * 0.82, 0.10 + 0.62 * 0.82 + 0.50 * 0.82];
        double totalEnd = starts[2] + durations[2] + 0.04;

        DispatcherTimer? timer = null;
        var sw = new System.Diagnostics.Stopwatch();

        void ApplyTrim(Polyline target, (Point Pt, double CumLen)[] table, double t)
        {
            target.Points.Clear();
            if (t <= 0.001 || table.Length < 2) return;
            double total = table[^1].CumLen;
            double targetLen = Math.Clamp(t, 0.0, 1.0) * total;
            target.Points.Add(table[0].Pt);
            for (int idx = 1; idx < table.Length; idx++)
            {
                if (table[idx].CumLen <= targetLen)
                {
                    target.Points.Add(table[idx].Pt);
                }
                else
                {
                    double prevLen = table[idx - 1].CumLen;
                    double seg = Math.Max(1e-6, table[idx].CumLen - prevLen);
                    double frac = (targetLen - prevLen) / seg;
                    var a = table[idx - 1].Pt;
                    var b = table[idx].Pt;
                    target.Points.Add(new Point(a.X + (b.X - a.X) * frac, a.Y + (b.Y - a.Y) * frac));
                    break;
                }
            }
        }

        void Write()
        {
            timer?.Stop();
            for (int i = 0; i < 3; i++)
            {
                mainLines[i].Points.Clear();
                grainLines[i].Points.Clear();
            }
            pressedLine.Points.Clear();

            sw.Restart();
            timer = new DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                double elapsed = sw.Elapsed.TotalSeconds;
                for (int i = 0; i < 3; i++)
                {
                    double raw = (elapsed - starts[i]) / durations[i];
                    double eased = raw <= 0 ? 0 : (raw >= 1 ? 1 : CubicBezierEase(raw, 0.45, 0.05, 0.30, 1.0));
                    ApplyTrim(mainLines[i], samples[i], eased);
                    ApplyTrim(grainLines[i], samples[i], eased);
                    if (i == 0)
                    {
                        double pTrim = Math.Max(0.0, eased * 2.1 - 1.1);
                        ApplyTrim(pressedLine, pressed, pTrim);
                    }
                }
                if (elapsed >= totalEnd)
                {
                    timer.Stop();
                }
            };
            timer.Start();
        }

        canvas.PointerPressed += (_, e) =>
        {
            Write();
            e.Handled = true;
        };
        canvas.Unloaded += (_, _) => timer?.Stop();

        return (canvas, Write);
    }

    private static (Point Pt, double CumLen)[] SampleMultiBezier((Point P0, Point C1, Point C2, Point P3)[] segs)
    {
        const int stepsPerSeg = 36;
        var list = new List<(Point Pt, double CumLen)>(segs.Length * stepsPerSeg + 1);
        double cum = 0;
        Point prev = segs[0].P0;
        list.Add((prev, 0));
        foreach (var (p0, c1, c2, p3) in segs)
        {
            for (int s = 1; s <= stepsPerSeg; s++)
            {
                double t = (double)s / stepsPerSeg;
                double u = 1.0 - t;
                double x = u * u * u * p0.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * p3.X;
                double y = u * u * u * p0.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * p3.Y;
                var cur = new Point(x, y);
                double dx = cur.X - prev.X;
                double dy = cur.Y - prev.Y;
                cum += Math.Sqrt(dx * dx + dy * dy);
                list.Add((cur, cum));
                prev = cur;
            }
        }
        return list.ToArray();
    }

    private static double CubicBezierEase(double x, double x1, double y1, double x2, double y2)
    {
        double lo = 0.0, hi = 1.0, t = x;
        for (int i = 0; i < 12; i++)
        {
            t = 0.5 * (lo + hi);
            double u = 1.0 - t;
            double bx = 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t;
            if (bx < x) lo = t; else hi = t;
        }
        double uFinal = 1.0 - t;
        return 3 * uFinal * uFinal * t * y1 + 3 * uFinal * t * t * y2 + t * t * t;
    }

    // ---------- Frameless macOS Sheet, Safe Dialog Host & Motion Helpers ----------

    private static readonly ConditionalWeakTable<XamlRoot, SemaphoreSlim> _dialogLocks = new();

    private static SemaphoreSlim GetDialogLock(XamlRoot xamlRoot) =>
        _dialogLocks.GetValue(xamlRoot, static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Returns true if a real modal dialog or flyout/menu is open on <paramref name="xamlRoot"/>,
    /// ignoring ephemeral <see cref="ToolTip"/> popups so hover tooltips never block keyboard shortcuts.
    /// </summary>
    public static bool HasBlockingModalOrFlyoutOpen(XamlRoot? xamlRoot)
    {
        if (xamlRoot is null) return false;
        try
        {
            foreach (var pop in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            {
                if (pop.Child is ContentDialog or FlyoutPresenter or MenuFlyoutPresenter)
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Strips WinUI 3's inner focus border and opaque focus/hover fill from a <see cref="TextBox"/>
    /// placed inside a custom rounded capsule or card <see cref="Border"/>.
    /// </summary>
    public static void StripTextBoxChrome(TextBox box)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        box.BorderThickness = new Thickness(0);
        box.Background = transparent;
        box.UseSystemFocusVisuals = false;
        box.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        box.Resources["TextControlBackground"] = transparent;
        box.Resources["TextControlBackgroundPointerOver"] = transparent;
        box.Resources["TextControlBackgroundFocused"] = transparent;
        box.Resources["TextControlBackgroundDisabled"] = transparent;
        box.Resources["TextControlBorderBrush"] = transparent;
        box.Resources["TextControlBorderBrushPointerOver"] = transparent;
        box.Resources["TextControlBorderBrushFocused"] = transparent;
        box.Resources["TextControlBorderBrushDisabled"] = transparent;
    }

    /// <summary>
    /// Strips WinUI 3's inner focus border and opaque focus/hover fill from a <see cref="PasswordBox"/>
    /// placed inside a custom rounded capsule or card <see cref="Border"/>.
    /// </summary>
    public static void StripPasswordBoxChrome(PasswordBox box)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        box.BorderThickness = new Thickness(0);
        box.Background = transparent;
        box.UseSystemFocusVisuals = false;
        box.Resources["TextControlBorderThemeThickness"] = new Thickness(0);
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        box.Resources["TextControlBackground"] = transparent;
        box.Resources["TextControlBackgroundPointerOver"] = transparent;
        box.Resources["TextControlBackgroundFocused"] = transparent;
        box.Resources["TextControlBackgroundDisabled"] = transparent;
        box.Resources["TextControlBorderBrush"] = transparent;
        box.Resources["TextControlBorderBrushPointerOver"] = transparent;
        box.Resources["TextControlBorderBrushFocused"] = transparent;
        box.Resources["TextControlBorderBrushDisabled"] = transparent;
    }

    /// <summary>
    /// Hides any currently open <see cref="ContentDialog"/> on <paramref name="xamlRoot"/> and awaits its closure
    /// so WinUI 3 never throws <c>COMException (0x80000019): Only a single ContentDialog can be open at any time</c>.
    /// </summary>
    public static async Task PrepareXamlRootForDialogAsync(XamlRoot? xamlRoot)
    {
        if (xamlRoot is null) return;
        for (int pass = 0; pass < 3; pass++)
        {
            List<Task>? closing = null;
            try
            {
                foreach (var pop in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
                {
                    if (pop.Child is ContentDialog openDlg)
                    {
                        closing ??= [];
                        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        Windows.Foundation.TypedEventHandler<ContentDialog, ContentDialogClosedEventArgs>? handler = null;
                        handler = (_, _) =>
                        {
                            try { openDlg.Closed -= handler; } catch { }
                            tcs.TrySetResult(true);
                        };
                        try
                        {
                            openDlg.Closed += handler;
                            openDlg.Hide();
                        }
                        catch
                        {
                            try { openDlg.Closed -= handler; } catch { }
                            tcs.TrySetResult(true);
                        }
                        closing.Add(tcs.Task);
                    }
                }
            }
            catch { }

            if (closing is null || closing.Count == 0)
                break;

            await Task.WhenAny(Task.WhenAll(closing), Task.Delay(260));
            await Task.Yield();
        }
    }

    /// <summary>
    /// Crash-safe wrapper around <see cref="ContentDialog.ShowAsync()"/> that awaits closure of any prior open dialog
    /// on the same <see cref="XamlRoot"/>, syncs <see cref="FrameworkElement.RequestedTheme"/> from the host window,
    /// and applies warm control theme resources.
    /// </summary>
    public static async Task<ContentDialogResult> ShowSafeAsync(this ContentDialog dlg, YYTint? tint = null)
    {
        if (dlg.XamlRoot is null)
            return ContentDialogResult.None;

        if (dlg.RequestedTheme == ElementTheme.Default && dlg.XamlRoot.Content is FrameworkElement rootFe)
            dlg.RequestedTheme = rootFe.ActualTheme != ElementTheme.Default ? rootFe.ActualTheme : rootFe.RequestedTheme;

        bool dark = ThemeHelper.IsDark(dlg.RequestedTheme);
        ThemeHelper.ApplyControlThemeResources(dlg.Resources, dark, tint ?? YYTints.Default);

        var sem = GetDialogLock(dlg.XamlRoot);
        await sem.WaitAsync();
        try
        {
            await PrepareXamlRootForDialogAsync(dlg.XamlRoot);
        }
        finally
        {
            sem.Release();
        }

        try
        {
            return await dlg.ShowAsync();
        }
        catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80000019)
        {
            await Task.Delay(180);
            await PrepareXamlRootForDialogAsync(dlg.XamlRoot);
            try { return await dlg.ShowAsync(); } catch { return ContentDialogResult.None; }
        }
    }

    /// <summary>
    /// Creates a frameless macOS-style sheet <see cref="ContentDialog"/> with warm paper surface,
    /// <c>18px</c> corner radius, zero built-in WinUI command bar, custom exact width, Esc-to-close,
    /// and smooth Composition entrance animation.
    /// </summary>
    public static ContentDialog CreateMacSheet(
        XamlRoot xamlRoot,
        bool dark,
        double width,
        FrameworkElement body,
        double maxHeight = 760,
        YYTint? tint = null)
    {
        var p = ThemeHelper.Palette(dark);
        Color edgeColor = dark ? Color.FromArgb(32, 255, 255, 255) : p.Line;

        var surface = new Border
        {
            Width = width,
            Background = ThemeHelper.Brush(p.Bg),
            CornerRadius = new CornerRadius(18),
            Child = body,
        };
        body.HorizontalAlignment = HorizontalAlignment.Stretch;
        L10n.ApplyTree(surface);

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
            Background = ThemeHelper.Brush(p.Bg),
            Foreground = ThemeHelper.Brush(p.T1),
            BorderBrush = ThemeHelper.Brush(edgeColor),
            BorderThickness = new Thickness(0.75),
            CornerRadius = new CornerRadius(18),
            Content = surface,
        };
        ThemeHelper.ApplyControlThemeResources(dlg.Resources, dark, tint ?? YYTints.Default);
        dlg.Resources["ContentDialogBackground"] = ThemeHelper.Brush(p.Bg);
        dlg.Resources["ContentDialogForeground"] = ThemeHelper.Brush(p.T1);
        dlg.Resources["ContentDialogBorderBrush"] = ThemeHelper.Brush(edgeColor);
        dlg.Resources["ContentDialogBorderWidth"] = new Thickness(0.75);
        dlg.Resources["ContentDialogPadding"] = new Thickness(0);
        dlg.Resources["ContentDialogTitleMargin"] = new Thickness(0);
        dlg.Resources["ContentDialogMinWidth"] = width;
        dlg.Resources["ContentDialogMaxWidth"] = width;
        dlg.Resources["ContentDialogMinHeight"] = 0.0;
        dlg.Resources["ContentDialogMaxHeight"] = maxHeight;
        dlg.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Colors.Transparent);
        dlg.Resources["ContentDialogSeparatorThickness"] = new Thickness(0);
        dlg.Resources["ContentDialogSeparatorBorderBrush"] = new SolidColorBrush(Colors.Transparent);
        dlg.Resources["OverlayCornerRadius"] = new CornerRadius(18);

        dlg.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                dlg.Hide();
                e.Handled = true;
            }
        };
        dlg.Opened += (_, _) =>
        {
            UnclampDialogTemplateBounds(dlg, width, maxHeight);
            AnimateEntrance(surface, fromY: 8f, durationMs: 170);
        };
        return dlg;
    }

    private static void UnclampDialogTemplateBounds(DependencyObject root, double width, double maxHeight)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && (fe.Name == "BackgroundElement" || fe.Name == "Container"))
            {
                fe.MaxWidth = Math.Max(width, fe.MaxWidth);
                fe.MaxHeight = Math.Max(maxHeight, fe.MaxHeight);
            }
            if (!ReferenceEquals(child, (root as ContentDialog)?.Content))
                UnclampDialogTemplateBounds(child, width, maxHeight);
        }
    }

    /// <summary>
    /// Frameless macOS <c>YYDialogCard</c> confirmation sheet (<c>Parts.swift:1278-1400</c>).
    /// </summary>
    public static async Task<bool> ShowConfirmAsync(
        XamlRoot? xamlRoot,
        bool dark,
        string title,
        string message,
        string confirmText = "确定",
        string cancelText = "取消",
        bool destructive = false,
        YYTint? tint = null)
    {
        if (xamlRoot is null) return false;
        var p = ThemeHelper.Palette(dark);
        var useTint = tint ?? YYTints.Default;
        bool confirmed = false;
        ContentDialog? dlgRef = null;

        var stack = new StackPanel { Spacing = 14, Padding = new Thickness(22, 20, 22, 18) };
        stack.Children.Add(new TextBlock
        {
            Text = L10n.T(title),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(message))
        {
            stack.Children.Add(new TextBlock
            {
                Text = L10n.T(message),
                FontSize = 13,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
            });
        }

        var btnRow = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cancelBtn = MakeSheetPillButton(cancelText, () => dlgRef?.Hide(), dark, useTint, kind: "secondary", height: 34, horizontalPadding: 16);
        Grid.SetColumn(cancelBtn, 1);

        Button okBtn;
        if (destructive)
        {
            Color dangerBg = dark ? Color.FromArgb(42, 232, 93, 78) : Color.FromArgb(26, 208, 66, 52);
            Color dangerFg = p.Danger;
            okBtn = new Button
            {
                Height = 34,
                MinHeight = 34,
                Padding = new Thickness(16, 0, 16, 0),
                CornerRadius = new CornerRadius(17),
                Background = ThemeHelper.Brush(dangerBg),
                Foreground = ThemeHelper.Brush(dangerFg),
                BorderBrush = ThemeHelper.Brush(Color.FromArgb(64, dangerFg.R, dangerFg.G, dangerFg.B)),
                BorderThickness = new Thickness(0.75),
                FontWeight = FontWeights.SemiBold,
                UseSystemFocusVisuals = false,
                Content = new TextBlock { Text = L10n.T(confirmText), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(dangerFg) },
            };
            okBtn.Click += (_, _) => { confirmed = true; dlgRef?.Hide(); };
        }
        else
        {
            okBtn = MakeSheetPillButton(confirmText, () => { confirmed = true; dlgRef?.Hide(); }, dark, useTint, kind: "dark", height: 34, horizontalPadding: 18);
        }
        Grid.SetColumn(okBtn, 2);

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        stack.Children.Add(btnRow);

        dlgRef = CreateMacSheet(xamlRoot, dark, 348, stack, tint: useTint);
        dlgRef.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                confirmed = true;
                dlgRef.Hide();
                e.Handled = true;
            }
        };
        await dlgRef.ShowSafeAsync(useTint);
        return confirmed;
    }

    /// <summary>
    /// Frameless macOS <c>yyNotice</c> single-button sheet (<c>Parts.swift:1278-1370</c>).
    /// </summary>
    public static async Task ShowNoticeAsync(
        XamlRoot? xamlRoot,
        bool dark,
        string title,
        string message,
        string okText = "知道了",
        YYTint? tint = null)
    {
        if (xamlRoot is null) return;
        var p = ThemeHelper.Palette(dark);
        var useTint = tint ?? YYTints.Default;
        ContentDialog? dlgRef = null;

        var stack = new StackPanel { Spacing = 14, Padding = new Thickness(22, 20, 22, 18) };
        stack.Children.Add(new TextBlock
        {
            Text = L10n.T(title),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(message))
        {
            stack.Children.Add(new TextBlock
            {
                Text = L10n.T(message),
                FontSize = 13,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
            });
        }

        var okBtn = MakeSheetPillButton(okText, () => dlgRef?.Hide(), dark, useTint, kind: "dark", height: 34, horizontalPadding: 20);
        okBtn.HorizontalAlignment = HorizontalAlignment.Right;
        stack.Children.Add(okBtn);

        dlgRef = CreateMacSheet(xamlRoot, dark, 340, stack, tint: useTint);
        dlgRef.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                dlgRef.Hide();
                e.Handled = true;
            }
        };
        await dlgRef.ShowSafeAsync(useTint);
    }

    /// <summary>
    /// Frameless macOS <c>yyPrompt</c> single-line text input sheet (<c>Parts.swift:1405-1455</c>).
    /// </summary>
    public static async Task<string?> ShowPromptAsync(
        XamlRoot? xamlRoot,
        bool dark,
        string title,
        string placeholder = "",
        string initialText = "",
        string confirmText = "好",
        string cancelText = "取消",
        string? subtitle = null,
        YYTint? tint = null)
    {
        if (xamlRoot is null) return null;
        var p = ThemeHelper.Palette(dark);
        var useTint = tint ?? YYTints.Default;
        string? submitted = null;
        ContentDialog? dlgRef = null;

        var stack = new StackPanel { Spacing = 12, Padding = new Thickness(22, 20, 22, 18) };
        stack.Children.Add(new TextBlock
        {
            Text = L10n.T(title),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            stack.Children.Add(new TextBlock
            {
                Text = L10n.T(subtitle),
                FontSize = 12.5,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, -6, 0, 0),
            });
        }

        var box = new TextBox
        {
            PlaceholderText = L10n.T(placeholder),
            Text = initialText,
            CornerRadius = new CornerRadius(10),
            FontSize = 13.5,
            Background = ThemeHelper.Brush(p.Card),
        };
        stack.Children.Add(box);

        void Submit()
        {
            string val = box.Text.Trim();
            if (val.Length == 0) return;
            submitted = val;
            dlgRef?.Hide();
        }

        var btnRow = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        btnRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cancelBtn = MakeSheetPillButton(cancelText, () => dlgRef?.Hide(), dark, useTint, kind: "secondary", height: 34, horizontalPadding: 16);
        var okBtn = MakeSheetPillButton(confirmText, Submit, dark, useTint, kind: "dark", height: 34, horizontalPadding: 18);
        Grid.SetColumn(cancelBtn, 1);
        Grid.SetColumn(okBtn, 2);
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);
        stack.Children.Add(btnRow);

        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };

        dlgRef = CreateMacSheet(xamlRoot, dark, 356, stack, tint: useTint);
        dlgRef.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        await dlgRef.ShowSafeAsync(useTint);
        return submitted;
    }

    /// <summary>
    /// Circular <c>28×28</c> recessed macOS sheet close button (<c>×</c>).
    /// </summary>
    public static Button MakeSheetCloseButton(Action onClose, bool dark, double size = 28)
    {
        var p = ThemeHelper.Palette(dark);
        var btn = new Button
        {
            Width = size,
            Height = size,
            MinWidth = size,
            MinHeight = size,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(size / 2.0),
            Background = ThemeHelper.Brush(dark ? Color.FromArgb(255, 44, 45, 49) : Color.FromArgb(255, 236, 233, 227)),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            VerticalAlignment = VerticalAlignment.Center,
            Content = YYIcons.Make("xmark", Math.Round(size * 0.39), ThemeHelper.Brush(p.T2)),
        };
        AutomationProperties.SetName(btn, L10n.T("关闭面板"));
        btn.Click += (_, _) => onClose();
        return btn;
    }

    /// <summary>
    /// Standard macOS sheet header row: optional leading tile + bold title + muted inline subtitle + trailing circular <c>×</c>.
    /// </summary>
    public static Grid MakeSheetHeader(
        string title,
        string? subtitle,
        Action onClose,
        bool dark,
        FrameworkElement? leading = null,
        double titleSize = 16.5,
        bool serifTitle = false)
    {
        var p = ThemeHelper.Palette(dark);
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (leading is not null)
        {
            leading.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(leading);
        }
        var titleTb = new TextBlock
        {
            Text = L10n.T(title),
            FontSize = titleSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (serifTitle) titleTb.FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        left.Children.Add(titleTb);

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            left.Children.Add(new TextBlock
            {
                Text = L10n.T(subtitle),
                FontSize = 12.5,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 340,
                Margin = new Thickness(2, 1, 0, 0),
            });
        }
        row.Children.Add(left);

        var closeBtn = MakeSheetCloseButton(onClose, dark);
        Grid.SetColumn(closeBtn, 1);
        row.Children.Add(closeBtn);
        return row;
    }

    /// <summary>
    /// Pill button used in macOS sheet footers (<c>"secondary"</c> white pill, <c>"warm"</c> soft tint wash, <c>"dark"</c> solid charcoal).
    /// </summary>
    public static Button MakeSheetPillButton(
        object content,
        Action onClick,
        bool dark,
        YYTint tint,
        string kind = "secondary",
        double height = 36,
        double horizontalPadding = 18,
        bool stretch = false)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(tint, dark);
        Color bg, bgHover, bgPress, bgDisabled, fg, fgDisabled, edge;
        double borderW;
        FontWeight fw;
        switch (kind)
        {
            case "warm":
                bg = tc.Soft;
                bgHover = Color.FromArgb(bg.A, (byte)Math.Clamp(bg.R + (dark ? 14 : -10), 0, 255), (byte)Math.Clamp(bg.G + (dark ? 14 : -10), 0, 255), (byte)Math.Clamp(bg.B + (dark ? 14 : -10), 0, 255));
                bgPress = Color.FromArgb(bg.A, (byte)Math.Clamp(bg.R + (dark ? 22 : -18), 0, 255), (byte)Math.Clamp(bg.G + (dark ? 22 : -18), 0, 255), (byte)Math.Clamp(bg.B + (dark ? 22 : -18), 0, 255));
                bgDisabled = Color.FromArgb(110, bg.R, bg.G, bg.B);
                fg = tc.Accent;
                fgDisabled = Color.FromArgb(130, fg.R, fg.G, fg.B);
                edge = tc.Edge;
                borderW = 0.75;
                fw = FontWeights.SemiBold;
                break;
            case "dark":
                bg = p.T1;
                bgHover = Color.FromArgb(255, (byte)Math.Clamp(bg.R + (dark ? -18 : 22), 0, 255), (byte)Math.Clamp(bg.G + (dark ? -18 : 22), 0, 255), (byte)Math.Clamp(bg.B + (dark ? -18 : 22), 0, 255));
                bgPress = Color.FromArgb(255, (byte)Math.Clamp(bg.R + (dark ? -32 : 38), 0, 255), (byte)Math.Clamp(bg.G + (dark ? -32 : 38), 0, 255), (byte)Math.Clamp(bg.B + (dark ? -32 : 38), 0, 255));
                bgDisabled = Color.FromArgb(dark ? (byte)48 : (byte)96, p.T1.R, p.T1.G, p.T1.B);
                fg = p.Card;
                fgDisabled = dark
                    ? Color.FromArgb(118, p.T1.R, p.T1.G, p.T1.B)
                    : Color.FromArgb(235, p.Card.R, p.Card.G, p.Card.B);
                edge = p.T1;
                borderW = 0;
                fw = FontWeights.SemiBold;
                break;
            default:
                bg = p.Card;
                bgHover = p.Inset;
                bgPress = Color.FromArgb(255, (byte)Math.Clamp(p.Inset.R + (dark ? 10 : -10), 0, 255), (byte)Math.Clamp(p.Inset.G + (dark ? 10 : -10), 0, 255), (byte)Math.Clamp(p.Inset.B + (dark ? 10 : -10), 0, 255));
                bgDisabled = Color.FromArgb(140, p.Card.R, p.Card.G, p.Card.B);
                fg = p.T1;
                fgDisabled = p.T3;
                edge = p.Line;
                borderW = 0.75;
                fw = FontWeights.Medium;
                break;
        }

        UIElement child = content is string s
            ? new TextBlock { Text = L10n.T(s), FontSize = 13, FontWeight = fw, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
            : (UIElement)content;

        var btn = new Button
        {
            Height = height,
            MinHeight = height,
            Padding = new Thickness(horizontalPadding, 0, horizontalPadding, 0),
            CornerRadius = new CornerRadius(height / 2.0),
            Background = ThemeHelper.Brush(bg),
            Foreground = ThemeHelper.Brush(fg),
            BorderBrush = ThemeHelper.Brush(edge),
            BorderThickness = new Thickness(borderW),
            FontWeight = fw,
            HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            UseSystemFocusVisuals = false,
            Content = child,
        };
        if (content is string labelStr)
            AutomationProperties.SetName(btn, L10n.T(labelStr));
        btn.Resources["ButtonBackground"] = ThemeHelper.Brush(bg);
        btn.Resources["ButtonBackgroundPointerOver"] = ThemeHelper.Brush(bgHover);
        btn.Resources["ButtonBackgroundPressed"] = ThemeHelper.Brush(bgPress);
        btn.Resources["ButtonBackgroundDisabled"] = ThemeHelper.Brush(bgDisabled);
        btn.Resources["ButtonForeground"] = ThemeHelper.Brush(fg);
        btn.Resources["ButtonForegroundPointerOver"] = ThemeHelper.Brush(fg);
        btn.Resources["ButtonForegroundPressed"] = ThemeHelper.Brush(fg);
        btn.Resources["ButtonForegroundDisabled"] = ThemeHelper.Brush(fgDisabled);
        btn.Resources["ButtonBorderBrush"] = ThemeHelper.Brush(edge);
        btn.Resources["ButtonBorderBrushPointerOver"] = ThemeHelper.Brush(edge);
        btn.Resources["ButtonBorderBrushPressed"] = ThemeHelper.Brush(edge);
        btn.Resources["ButtonBorderBrushDisabled"] = borderW > 0 ? ThemeHelper.Brush(Color.FromArgb(96, edge.R, edge.G, edge.B)) : new SolidColorBrush(Colors.Transparent);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>
    /// GPU Composition vertical fade+slide entrance (macOS spring curve, zero layout cost).
    /// </summary>
    public static void AnimateEntrance(UIElement element, float fromY = 8f, int durationMs = 170)
    {
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var comp = visual.Compositor;
            var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1.0f), new Vector2(0.36f, 1.0f));

            var offsetAnim = comp.CreateVector3KeyFrameAnimation();
            offsetAnim.InsertKeyFrame(0f, new Vector3(0f, fromY, 0f));
            offsetAnim.InsertKeyFrame(1f, Vector3.Zero, ease);
            offsetAnim.Duration = System.TimeSpan.FromMilliseconds(durationMs);
            visual.StartAnimation("Translation", offsetAnim);

            var opAnim = comp.CreateScalarKeyFrameAnimation();
            opAnim.InsertKeyFrame(0f, 0.0f);
            opAnim.InsertKeyFrame(1f, 1.0f, ease);
            opAnim.Duration = System.TimeSpan.FromMilliseconds(Math.Max(90, durationMs - 30));
            visual.StartAnimation("Opacity", opAnim);
        }
        catch { }
    }

    /// <summary>
    /// GPU Composition horizontal slide+fade entrance for side drawers/panels.
    /// </summary>
    public static void AnimateSlideX(UIElement element, float fromX = 20f, int durationMs = 190)
    {
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var comp = visual.Compositor;
            var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1.0f), new Vector2(0.36f, 1.0f));

            var offsetAnim = comp.CreateVector3KeyFrameAnimation();
            offsetAnim.InsertKeyFrame(0f, new Vector3(fromX, 0f, 0f));
            offsetAnim.InsertKeyFrame(1f, Vector3.Zero, ease);
            offsetAnim.Duration = System.TimeSpan.FromMilliseconds(durationMs);
            visual.StartAnimation("Translation", offsetAnim);

            var opAnim = comp.CreateScalarKeyFrameAnimation();
            opAnim.InsertKeyFrame(0f, 0.25f);
            opAnim.InsertKeyFrame(1f, 1.0f, ease);
            opAnim.Duration = System.TimeSpan.FromMilliseconds(Math.Max(90, durationMs - 40));
            visual.StartAnimation("Opacity", opAnim);
        }
        catch { }
    }
}
