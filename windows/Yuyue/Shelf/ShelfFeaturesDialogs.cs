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

    public static async Task ShowFeedbackDialogAsync(XamlRoot xamlRoot, LibraryService library)
    {
        string category = "bug";
        var catRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var catBtns = new List<(string Id, Button Btn)>();

        var bodyBox = new TextBox
        {
            PlaceholderText = "哪本书、哪一页，做了什么，结果怎样",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 110,
        };
        var contactBox = new TextBox
        {
            Header = "联系方式（可选）",
            PlaceholderText = "邮箱或 GitHub ID，方便跟进回复",
        };

        foreach (var (id, label, hint) in new[]
        {
            ("bug", "问题", "哪本书、哪一页，做了什么，结果怎样"),
            ("idea", "建议", "想要什么功能，或者哪里可以更顺手"),
            ("other", "其他", "想说的都可以"),
        })
        {
            var cid = id;
            var ph = hint;
            var b = new Button { Content = label };
            b.Click += (_, _) =>
            {
                category = cid;
                bodyBox.PlaceholderText = ph;
                foreach (var (k, btn) in catBtns)
                    btn.Style = (Style)Application.Current.Resources[k == category ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];
            };
            catBtns.Add((cid, b));
            catRow.Children.Add(b);
        }
        foreach (var (k, btn) in catBtns)
            btn.Style = (Style)Application.Current.Resources[k == category ? "YYPrimaryPillButtonStyle" : "YYPillButtonStyle"];

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "反馈与建议",
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 420,
                Children =
                {
                    new TextBlock { Text = "语阅是开源阅读器，你可以直接在 GitHub 提交 Issue，或复制诊断文本。", FontSize = 12, Foreground = (Brush)Application.Current.Resources["YYText3Brush"], TextWrapping = TextWrapping.Wrap },
                    catRow,
                    bodyBox,
                    contactBox,
                },
            },
            PrimaryButtonText = "前往 GitHub 提交",
            SecondaryButtonText = "复制内容",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.None) return;

        string summary = $"[{category.ToUpperInvariant()}] {bodyBox.Text.Trim()}\n\n---\nPlatform: Windows ({Environment.OSVersion.VersionString})\nApp: Yuyue 0.1.0\nContact: {contactBox.Text.Trim()}";
        if (res == ContentDialogResult.Secondary)
        {
            var dp = new DataPackage();
            dp.SetText(summary);
            Clipboard.SetContent(dp);
        }
        else if (res == ContentDialogResult.Primary)
        {
            string title = Uri.EscapeDataString(bodyBox.Text.Trim().Split('\n')[0] is { Length: > 0 } l ? (l.Length > 50 ? l[..50] : l) : "Windows 端反馈");
            string body = Uri.EscapeDataString(summary);
            _ = Launcher.LaunchUriAsync(new Uri($"https://github.com/inokoru/yuyue/issues/new?title={title}&body={body}"));
        }
    }

    public static async Task ShowVocabReviewDialogAsync(
        XamlRoot xamlRoot, LibraryService library, Action onChanged)
    {
        var queue = library.GetWords().Where(w => !w.Known).ToList();
        if (queue.Count == 0)
        {
            queue = library.GetWords().ToList();
        }
        if (queue.Count == 0) return;

        int idx = 0;
        bool revealed = false;

        var progressText = new TextBlock { FontSize = 11.5, Foreground = (Brush)Application.Current.Resources["YYText3Brush"] };
        var wordText = new TextBlock { FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var phoneticText = new TextBlock { FontSize = 13, Foreground = (Brush)Application.Current.Resources["YYText3Brush"], HorizontalAlignment = HorizontalAlignment.Center };
        var contextText = new TextBlock { FontSize = 13, Foreground = (Brush)Application.Current.Resources["YYText2Brush"], TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
        var meaningBorder = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["YYInsetBrush"],
            Padding = new Thickness(16, 12, 16, 12),
            MinHeight = 68,
        };
        var meaningText = new TextBlock { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        meaningBorder.Child = meaningText;

        var speakBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "发音" };
        var revealBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "看释义" };
        var againBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "再记一次" };
        var knowBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "认识（标为掌握）" };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };

        void RenderCard()
        {
            if (idx >= queue.Count)
            {
                progressText.Text = "复习完成";
                wordText.Text = "✓ 本轮生词已全部过完";
                phoneticText.Text = "";
                contextText.Text = "";
                meaningText.Text = "点下方「完成」回到生词本。";
                btnRow.Children.Clear();
                return;
            }

            var w = queue[idx];
            progressText.Text = $"第 {idx + 1} / {queue.Count} 个生词";
            wordText.Text = w.Word;
            phoneticText.Text = !string.IsNullOrWhiteSpace(w.Phonetic) ? $"/{w.Phonetic}/" : "";
            contextText.Text = !string.IsNullOrWhiteSpace(w.Context) ? $"“{w.Context}”" : "";

            btnRow.Children.Clear();
            btnRow.Children.Add(speakBtn);

            if (!revealed)
            {
                meaningText.Text = "先结合原句回想词义，再点击「看释义」";
                btnRow.Children.Add(revealBtn);
            }
            else
            {
                string def = w.Meaning ?? library.LookUpWordRich(w.Word)?.Summary ?? "（暂无释义）";
                meaningText.Text = def;
                btnRow.Children.Add(againBtn);
                btnRow.Children.Add(knowBtn);
            }
        }

        speakBtn.Click += (_, _) =>
        {
            if (idx < queue.Count)
                _ = AudioPlayer.PreviewVoiceAsync(library, library.Prefs.VoiceId, 1.0, queue[idx].Word);
        };
        revealBtn.Click += (_, _) => { revealed = true; RenderCard(); };
        againBtn.Click += (_, _) => { idx++; revealed = false; RenderCard(); };
        knowBtn.Click += (_, _) =>
        {
            if (idx < queue.Count)
            {
                try { library.SetWordKnown(queue[idx].Word, true); onChanged(); } catch { }
                idx++;
                revealed = false;
                RenderCard();
            }
        };

        RenderCard();

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "生词闪卡复习",
            Content = new StackPanel
            {
                Spacing = 10,
                MinWidth = 420,
                Children = { progressText, wordText, phoneticText, contextText, meaningBorder, btnRow },
            },
            CloseButtonText = "完成",
        };
        await dlg.ShowAsync();
    }
}
