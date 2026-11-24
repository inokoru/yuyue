using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Yuyue.Core;

namespace Yuyue.Design;

/// <summary>
/// Shared macOS-aligned visual chrome builders: brand/service icon tiles, segmented bars,
/// borderless SettingsActionStyle buttons, keycaps, and the floating white BookMenuCard.
/// </summary>
public static class YYChrome
{
    /// <summary>
    /// Crisp macOS rounded-square service/brand icon tile (32×32 default, CornerRadius ~25%).
    /// Matches <c>MacListeningSettings.swift</c>, <c>MacCompanionSettings.swift</c>, and <c>MacNoteExportSettings.swift</c>.
    /// </summary>
    public static Border MakeServiceTile(string key, bool dark, double size = 32)
    {
        var palette = ThemeHelper.Palette(dark);
        double radius = Math.Round(size * 0.27);
        double iconSize = Math.Round(size * 0.50);

        Border MakeTile(Color bg, Color border, UIElement child) => new()
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(radius),
            Background = ThemeHelper.Brush(bg),
            BorderBrush = ThemeHelper.Brush(border),
            BorderThickness = new Thickness(0.75),
            Child = child,
        };

        Color neutralBg = dark ? Color.FromArgb(255, 36, 37, 40) : Color.FromArgb(255, 250, 249, 246);
        Color neutralLine = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(20, 42, 43, 46);
        Color whiteTile = dark ? Color.FromArgb(255, 34, 35, 38) : Colors.White;

        switch (key.ToLowerInvariant())
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

            // Note Export brands (settings-notes/1.png)
            case "obsidian":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("obsidian", iconSize + 1, ThemeHelper.Brush(Color.FromArgb(255, 130, 86, 214)), filled: false));

            case "notion":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("notion", iconSize + 1, ThemeHelper.Brush(dark ? Colors.White : Color.FromArgb(255, 34, 34, 36))));

            case "flomo":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("flomo", iconSize + 1, ThemeHelper.Brush(Color.FromArgb(255, 55, 178, 98))));

            case "xinzhi":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("xinzhi", iconSize + 1, ThemeHelper.Brush(Color.FromArgb(255, 86, 168, 92))));

            // Companion & Cloud Voice brands (settings-companion-add/1.png)
            case "claude" or "anthropic":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("claude", iconSize, ThemeHelper.Brush(Color.FromArgb(255, 217, 119, 87))));

            case "openai":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("openai", iconSize + 1, ThemeHelper.Brush(dark ? Colors.White : Color.FromArgb(255, 34, 36, 40))));

            case "deepseek":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("deepseek", iconSize + 1, ThemeHelper.Brush(Color.FromArgb(255, 77, 107, 254))));

            case "gemini" or "google":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("gemini", iconSize, ThemeHelper.Brush(Color.FromArgb(255, 66, 133, 244)), filled: true));

            case "openrouter":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("openrouter", iconSize, ThemeHelper.Brush(dark ? Colors.White : Color.FromArgb(255, 38, 40, 46))));

            case "ollama" or "lmstudio":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("laptop", iconSize, ThemeHelper.Brush(palette.T1)));

            case "kimi" or "moonshot":
                return MakeTile(whiteTile, neutralLine,
                    MakeGlyphMark("K", Color.FromArgb(255, 34, 36, 42), dark ? Colors.White : Color.FromArgb(255, 34, 36, 42), size));

            case "qwen" or "dashscope" or "aliyun":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("sparkle", iconSize, ThemeHelper.Brush(Color.FromArgb(255, 108, 92, 231))));

            case "glm" or "zhipu":
                return MakeTile(whiteTile, neutralLine,
                    MakeGlyphMark("Z", Color.FromArgb(255, 64, 114, 245), Color.FromArgb(255, 64, 114, 245), size));

            case "doubao" or "volc" or "volcengine":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("bubble-lines", iconSize, ThemeHelper.Brush(Color.FromArgb(255, 52, 132, 245))));

            case "siliconflow":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("sliders", iconSize, ThemeHelper.Brush(Color.FromArgb(255, 118, 75, 226))));

            case "minimax":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("wave", iconSize, ThemeHelper.Brush(Color.FromArgb(255, 224, 72, 118))));

            case "mimo" or "xiaomi":
                return MakeTile(whiteTile, neutralLine,
                    MakeGlyphMark("M", Color.FromArgb(255, 240, 110, 42), Color.FromArgb(255, 240, 110, 42), size));

            case "grok":
                return MakeTile(whiteTile, neutralLine,
                    MakeGlyphMark("G", dark ? Colors.White : Color.FromArgb(255, 28, 30, 34), dark ? Colors.White : Color.FromArgb(255, 28, 30, 34), size));

            case "mistral":
                return MakeTile(whiteTile, neutralLine,
                    MakeGlyphMark("M", Color.FromArgb(255, 232, 108, 36), Color.FromArgb(255, 232, 108, 36), size));

            case "groq":
                return MakeTile(whiteTile, neutralLine,
                    MakeGlyphMark("g", Color.FromArgb(255, 245, 80, 54), Color.FromArgb(255, 245, 80, 54), size));

            case "fish" or "fishaudio":
                return MakeTile(whiteTile, neutralLine,
                    YYIcons.Make("wave", iconSize, ThemeHelper.Brush(Color.FromArgb(255, 58, 134, 220))));

            case "elevenlabs":
                return MakeTile(whiteTile, neutralLine,
                    MakeGlyphMark("II", dark ? Colors.White : Color.FromArgb(255, 34, 36, 40), dark ? Colors.White : Color.FromArgb(255, 34, 36, 40), size));

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
        var btn = new Button
        {
            Content = text,
            Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
            Foreground = ThemeHelper.Brush(destructive ? palette.Danger : palette.T1),
            IsEnabled = enabled,
            Opacity = enabled ? 1.0 : 0.36,
            VerticalAlignment = VerticalAlignment.Center,
        };
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
}
