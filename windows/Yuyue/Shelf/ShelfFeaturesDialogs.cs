using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yuyue.Core;
using Yuyue.Design;

namespace Yuyue.Shelf;

/// <summary>
/// 1:1 port of <c>WifiTransfer.swift</c>: frameless warm-paper LAN book transfer sheet with structured per-file progress rows.
/// </summary>
public static class ShelfFeaturesDialogs
{
    private sealed class TransferRowItem
    {
        public required string Key { get; init; }
        public required TextBlock NameText { get; init; }
        public required TextBlock StatusText { get; init; }
        public required ProgressBar Bar { get; init; }
        public required ContentControl Badge { get; init; }
        public bool Finished { get; set; }
    }

    public static async Task ShowWifiTransferDialogAsync(
        XamlRoot xamlRoot, LibraryService library, Action onBookImported)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(library.Prefs.Tint, dark);
        var mono = new FontFamily(YYFontFamily.Mono.WindowsFamily());
        ContentDialog? dlgRef = null;

        using var service = new WifiTransferService(library);
        string url = service.Start();

        var urlBox = new TextBlock
        {
            Text = url,
            FontFamily = mono,
            FontSize = 18.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(tint.Accent),
            IsTextSelectionEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        Button? copyBtn = null;
        copyBtn = YYChrome.MakeSheetPillButton("复制传书地址", () =>
        {
            var dp = new DataPackage();
            dp.SetText(url);
            Clipboard.SetContent(dp);
            if (copyBtn?.Content is TextBlock tb)
                tb.Text = L10n.T("✓ 已复制");
        }, dark, library.Prefs.Tint, "secondary", height: 30, horizontalPadding: 14);
        copyBtn.HorizontalAlignment = HorizontalAlignment.Center;

        var urlCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(18, 16, 18, 14),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new Border
                    {
                        Width = 36,
                        Height = 36,
                        CornerRadius = new CornerRadius(10),
                        Background = ThemeHelper.Brush(tint.Soft),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Child = YYIcons.Make("wifi", 17, ThemeHelper.Brush(tint.Accent)),
                    },
                    urlBox,
                    copyBtn,
                    new TextBlock
                    {
                        Text = L10n.T("保持此窗口打开，在手机、iPad 或另一台电脑浏览器访问上方地址即可拖拽上传图书与有声书。"),
                        FontSize = 11.5,
                        Foreground = ThemeHelper.Brush(p.T3),
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                    },
                },
            },
        };

        var rowsStack = new StackPanel { Spacing = 0 };
        var rowsCard = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            Visibility = Visibility.Collapsed,
            Child = new ScrollViewer { MaxHeight = 210, Content = rowsStack },
        };
        var rowMap = new List<TransferRowItem>();

        TransferRowItem GetOrAddRow(string key, string displayName)
        {
            var existing = rowMap.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;

            if (rowsStack.Children.Count > 0)
                rowsStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(40, 0, 12, 0) });

            var badge = new ContentControl
            {
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
                Content = YYIcons.Make("book", 12.5, ThemeHelper.Brush(tint.Accent)),
            };
            var nameTb = new TextBlock
            {
                Text = displayName,
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = ThemeHelper.Brush(p.T1),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var statusTb = new TextBlock
            {
                Text = L10n.T("准备接收…"),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
            };
            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                IsIndeterminate = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 2, 0, 0),
            };

            var mid = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(nameTb);
            mid.Children.Add(statusTb);
            mid.Children.Add(bar);

            var g = new Grid { ColumnSpacing = 10, Padding = new Thickness(12, 9, 12, 9) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(badge);
            Grid.SetColumn(mid, 1);
            g.Children.Add(mid);

            rowsStack.Children.Add(g);
            rowsCard.Visibility = Visibility.Visible;

            var created = new TransferRowItem
            {
                Key = key,
                NameText = nameTb,
                StatusText = statusTb,
                Bar = bar,
                Badge = badge,
            };
            rowMap.Add(created);
            return created;
        }

        var root = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(22, 18, 22, 18),
            Children =
            {
                YYChrome.MakeSheetHeader("局域网无线传书", "同一 Wi-Fi 下浏览器直传", () => dlgRef?.Hide(), dark, titleSize: 17),
                urlCard,
                rowsCard,
                YYChrome.MakeSheetPillButton("关闭传书服务", () => dlgRef?.Hide(), dark, library.Prefs.Tint, "dark", height: 38, stretch: true),
            },
        };

        service.LogEvent += msg =>
        {
            root.DispatcherQueue.TryEnqueue(() =>
            {
                if (msg.StartsWith("准备接收：", StringComparison.Ordinal))
                {
                    string name = msg["准备接收：".Length..].Trim();
                    var row = GetOrAddRow(name, name);
                    row.Finished = false;
                    row.StatusText.Text = L10n.T("准备接收…");
                    row.StatusText.Foreground = ThemeHelper.Brush(p.T3);
                    row.Bar.Visibility = Visibility.Visible;
                    row.Bar.IsIndeterminate = true;
                }
                else if (msg.StartsWith("接收中：", StringComparison.Ordinal))
                {
                    string body = msg["接收中：".Length..].Trim();
                    int dotIdx = body.IndexOf(" · ", StringComparison.Ordinal);
                    string name = dotIdx > 0 ? body[..dotIdx].Trim() : body;
                    string detail = dotIdx > 0 ? body[(dotIdx + 3)..].Trim() : body;
                    var row = GetOrAddRow(name, name);
                    row.Finished = false;
                    row.StatusText.Text = detail;
                    row.StatusText.Foreground = ThemeHelper.Brush(p.T2);
                    row.Bar.Visibility = Visibility.Visible;
                    int pctEnd = detail.IndexOf('%');
                    if (pctEnd > 0 && int.TryParse(detail[..pctEnd].Trim(), out int pct))
                    {
                        row.Bar.IsIndeterminate = false;
                        row.Bar.Value = Math.Clamp(pct, 0, 100);
                    }
                }
                else if (msg.StartsWith("✓ 已导入", StringComparison.Ordinal))
                {
                    var active = rowMap.FirstOrDefault(r => !r.Finished)
                        ?? GetOrAddRow(Guid.NewGuid().ToString("N"), msg.TrimStart('✓', ' '));
                    active.Finished = true;
                    active.NameText.Text = msg.Replace("✓ 已导入", "").Trim(' ', '《', '》') is { Length: > 0 } t ? t : active.NameText.Text;
                    active.StatusText.Text = L10n.T("已导入书架");
                    active.StatusText.Foreground = ThemeHelper.Brush(p.Ok);
                    active.Bar.Visibility = Visibility.Collapsed;
                    active.Badge.Content = YYIcons.Make("check-circle", 14, ThemeHelper.Brush(p.Ok));
                }
                else if (msg.StartsWith("✕ 导入失败", StringComparison.Ordinal))
                {
                    int openP = msg.IndexOf('(');
                    int closeP = msg.IndexOf(")：", StringComparison.Ordinal);
                    string name = openP >= 0 && closeP > openP ? msg[(openP + 1)..closeP] : "文件";
                    string err = closeP > 0 ? msg[(closeP + 2)..].Trim() : msg;
                    var row = GetOrAddRow(name, name);
                    row.Finished = true;
                    row.StatusText.Text = err;
                    row.StatusText.Foreground = ThemeHelper.Brush(p.Danger);
                    row.Bar.Visibility = Visibility.Collapsed;
                    row.Badge.Content = YYIcons.Make("xmark-circle", 14, ThemeHelper.Brush(p.Danger));
                }
                else
                {
                    var row = GetOrAddRow(msg, msg);
                    row.StatusText.Text = "";
                    row.Bar.Visibility = Visibility.Collapsed;
                }
            });
        };
        service.BookImported += () =>
        {
            root.DispatcherQueue.TryEnqueue(() => onBookImported());
        };

        dlgRef = YYChrome.CreateMacSheet(xamlRoot, dark, 440, root, tint: library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(library.Prefs.Tint);
        service.Stop();
    }
}
