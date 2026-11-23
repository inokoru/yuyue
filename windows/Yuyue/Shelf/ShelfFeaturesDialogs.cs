using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yuyue.Core;
using Yuyue.Speech;

namespace Yuyue.Shelf;

/// <summary>
/// Dialogs aligned with macOS:
/// 1. Wi-Fi LAN Book Transfer (<c>局域网传书</c>)
/// 2. Feedback &amp; Diagnostics (<c>反馈与建议</c>)
/// 3. Vocabulary Flashcard Review (<c>生词复习</c>)
/// </summary>
public static class ShelfFeaturesDialogs
{
    public static async Task ShowWifiTransferDialogAsync(
        XamlRoot xamlRoot, LibraryService library, Action onBookImported)
    {
        using var service = new WifiTransferService(library);
        string url = service.Start();

        var urlBox = new TextBlock
        {
            Text = url,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["YYAccentBrush"],
            IsTextSelectionEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var copyBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
            Content = "复制传书地址",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        copyBtn.Click += (_, _) =>
        {
            var dp = new DataPackage();
            dp.SetText(url);
            Clipboard.SetContent(dp);
            copyBtn.Content = "✓ 已复制";
        };

        var logStack = new StackPanel { Spacing = 4 };
        logStack.Children.Add(new TextBlock
        {
            Text = "在手机、iPad 或另一台电脑浏览器中打开上方地址，即可拖拽或多选上传图书与有声书。",
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["YYText2Brush"],
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        });

        var root = new StackPanel
        {
            Spacing = 12,
            MinWidth = 420,
            Children =
            {
                new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = (Brush)Application.Current.Resources["YYInsetBrush"],
                    Padding = new Thickness(18, 16, 18, 16),
                    Child = new StackPanel { Spacing = 10, Children = { urlBox, copyBtn } },
                },
                new ScrollViewer { MaxHeight = 160, Content = logStack },
            },
        };

        service.LogEvent += msg =>
        {
            root.DispatcherQueue.TryEnqueue(() =>
            {
                logStack.Children.Add(new TextBlock { Text = msg, FontSize = 12, Foreground = (Brush)Application.Current.Resources["YYText1Brush"] });
            });
        };
        service.BookImported += () =>
        {
            root.DispatcherQueue.TryEnqueue(() => onBookImported());
        };

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "局域网无线传书",
            Content = root,
            CloseButtonText = "关闭传书服务",
        };
        await dlg.ShowAsync();
        service.Stop();
    }
}
