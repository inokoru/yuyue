using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using Yuyue.Core;

namespace Yuyue.Design;

/// <summary>
/// Shared Windows 11 Fluent Design 2 dialog, button, segmented track, keycap, and safe ContentDialog helpers.
/// Uses 6px control radii, 10px overlay/dialog radii, crisp 1px hairlines, and Yuyue warm paper palettes.
/// </summary>
public static class YYChrome
{
    private static readonly ConditionalWeakTable<XamlRoot, SemaphoreSlim> _dialogLocks = new();

    private static SemaphoreSlim GetDialogLock(XamlRoot xamlRoot) =>
        _dialogLocks.GetValue(xamlRoot, static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Borderless Fluent subtle text action button.
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
    /// Native Windows 11 Fluent 2 segmented bar (6px outer track + 4px crisp elevated active thumb).
    /// </summary>
    public static Border MakeSegmentedBar(
        IReadOnlyList<(string Id, string Label)> items,
        string activeId,
        Action<string> onSelect,
        bool dark,
        double fontSize = 12,
        double height = 32)
    {
        var palette = ThemeHelper.Palette(dark);
        double innerH = Math.Max(22, height - 6);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        Color activeBorderColor = dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(20, 42, 43, 46);
        Color inactiveHoverColor = dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(12, 42, 43, 46);
        var entries = new List<(string Id, Action<bool> SetActive)>(items.Count);

        foreach (var (id, label) in items)
        {
            bool active = id == activeId;
            var bgBrush = new SolidColorBrush(active ? palette.Card : Colors.Transparent);
            var hoverBgBrush = new SolidColorBrush(active ? palette.Card : inactiveHoverColor);
            var fgBrush = new SolidColorBrush(active ? palette.T1 : palette.T2);
            var hoverFgBrush = new SolidColorBrush(palette.T1);

            var btn = new Button
            {
                Content = L10n.T(label),
                Height = innerH,
                MinHeight = innerH,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(active ? 1 : 0),
                BorderBrush = active ? ThemeHelper.Brush(activeBorderColor) : null,
                Background = bgBrush,
                Foreground = fgBrush,
                FontSize = fontSize,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                UseSystemFocusVisuals = false,
            };
            btn.Resources["ButtonBackground"] = bgBrush;
            btn.Resources["ButtonBackgroundPointerOver"] = hoverBgBrush;
            btn.Resources["ButtonBackgroundPressed"] = hoverBgBrush;
            btn.Resources["ButtonForeground"] = fgBrush;
            btn.Resources["ButtonForegroundPointerOver"] = hoverFgBrush;
            btn.Resources["ButtonForegroundPressed"] = hoverFgBrush;

            void SetActive(bool on)
            {
                bgBrush.Color = on ? palette.Card : Colors.Transparent;
                hoverBgBrush.Color = on ? palette.Card : inactiveHoverColor;
                fgBrush.Color = on ? palette.T1 : palette.T2;
                btn.BorderThickness = new Thickness(on ? 1 : 0);
                btn.BorderBrush = on ? ThemeHelper.Brush(activeBorderColor) : null;
                btn.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }

            entries.Add((id, SetActive));
            string captured = id;
            btn.Click += (_, _) =>
            {
                foreach (var (entryId, setActive) in entries)
                    setActive(entryId == captured);
                onSelect(captured);
            };
            row.Children.Add(btn);
        }

        return new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(palette.Inset),
            BorderBrush = ThemeHelper.Brush(palette.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = row,
        };
    }

    /// <summary>
    /// Crisp Windows 11 keyboard shortcut keycap badge.
    /// </summary>
    public static Border MakeKeycap(string text, bool dark)
    {
        var palette = ThemeHelper.Palette(dark);
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = ThemeHelper.Brush(dark ? Color.FromArgb(255, 42, 40, 38) : Color.FromArgb(255, 245, 242, 238)),
            BorderBrush = ThemeHelper.Brush(palette.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 1.5, 5, 1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10.5,
                FontWeight = FontWeights.Medium,
                Foreground = ThemeHelper.Brush(palette.T2),
            },
        };
    }

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
    /// placed inside a custom rounded container <see cref="Border"/>.
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
    /// and applies warm Fluent control theme resources.
    /// </summary>
    public static async Task<ContentDialogResult> ShowSafeAsync(this ContentDialog dlg, YYTint? tint = null)
    {
        if (dlg.XamlRoot is null)
            return ContentDialogResult.None;

        if (dlg.RequestedTheme == ElementTheme.Default && dlg.XamlRoot.Content is FrameworkElement rootFe)
            dlg.RequestedTheme = rootFe.ActualTheme != ElementTheme.Default ? rootFe.ActualTheme : rootFe.RequestedTheme;

        bool dark = ThemeHelper.IsDark(dlg.RequestedTheme);
        ThemeHelper.ApplyControlThemeResources(dlg.Resources, dark, tint ?? App.CurrentApp?.Library?.Prefs.Tint ?? YYTints.Default);

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
    /// Creates a Windows 11 Fluent 2 <see cref="ContentDialog"/> with warm paper surface,
    /// <c>10px</c> overlay radius, crisp <c>1px</c> elevation border, Esc-to-close, and subtle fade/slide motion.
    /// </summary>
    public static ContentDialog CreateFluentDialog(
        XamlRoot xamlRoot,
        bool dark,
        double width,
        FrameworkElement body,
        double maxHeight = 760,
        YYTint? tint = null)
    {
        var p = ThemeHelper.Palette(dark);
        Color surfaceBg = dark ? Color.FromArgb(255, 32, 30, 28) : Color.FromArgb(255, 250, 248, 244);
        Color edgeColor = dark ? Color.FromArgb(36, 255, 255, 255) : Color.FromArgb(28, 42, 43, 46);

        var surface = new Border
        {
            Width = width,
            Background = ThemeHelper.Brush(surfaceBg),
            CornerRadius = new CornerRadius(10),
            Child = body,
        };
        body.HorizontalAlignment = HorizontalAlignment.Stretch;
        L10n.ApplyTree(surface);

        var dlg = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
            Background = ThemeHelper.Brush(surfaceBg),
            Foreground = ThemeHelper.Brush(p.T1),
            BorderBrush = ThemeHelper.Brush(edgeColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Content = surface,
        };
        ThemeHelper.ApplyControlThemeResources(dlg.Resources, dark, tint ?? App.CurrentApp?.Library?.Prefs.Tint ?? YYTints.Default);
        dlg.Resources["ContentDialogBackground"] = ThemeHelper.Brush(surfaceBg);
        dlg.Resources["ContentDialogForeground"] = ThemeHelper.Brush(p.T1);
        dlg.Resources["ContentDialogBorderBrush"] = ThemeHelper.Brush(edgeColor);
        dlg.Resources["ContentDialogBorderWidth"] = new Thickness(1);
        dlg.Resources["ContentDialogPadding"] = new Thickness(0);
        dlg.Resources["ContentDialogTitleMargin"] = new Thickness(0);
        dlg.Resources["ContentDialogMinWidth"] = width;
        dlg.Resources["ContentDialogMaxWidth"] = width;
        dlg.Resources["ContentDialogMinHeight"] = 0.0;
        dlg.Resources["ContentDialogMaxHeight"] = maxHeight;
        dlg.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Colors.Transparent);
        dlg.Resources["ContentDialogSeparatorThickness"] = new Thickness(0);
        dlg.Resources["ContentDialogSeparatorBorderBrush"] = new SolidColorBrush(Colors.Transparent);
        dlg.Resources["OverlayCornerRadius"] = new CornerRadius(10);

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
            AnimateEntrance(surface, fromY: 6f, durationMs: 140);
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
    /// Native Windows 11 Fluent 2 confirmation dialog with recessed bottom command band.
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
        var useTint = tint ?? App.CurrentApp?.Library?.Prefs.Tint ?? YYTints.Default;
        bool confirmed = false;
        ContentDialog? dlgRef = null;

        var root = new StackPanel { Spacing = 0 };
        var body = new StackPanel { Spacing = 10, Padding = new Thickness(24, 22, 24, 20) };
        body.Children.Add(new TextBlock
        {
            Text = L10n.T(title),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(message))
        {
            body.Children.Add(new TextBlock
            {
                Text = L10n.T(message),
                FontSize = 13.5,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
            });
        }
        root.Children.Add(body);

        var footerBand = new Border
        {
            Background = ThemeHelper.Brush(dark ? Color.FromArgb(255, 25, 24, 22) : Color.FromArgb(255, 242, 239, 234)),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 10, 10),
            Padding = new Thickness(24, 14, 24, 14),
        };
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        Button okBtn;
        if (destructive)
        {
            Color dangerBg = p.Danger;
            Color dangerHover = Color.FromArgb(255, (byte)Math.Clamp(dangerBg.R - 14, 0, 255), (byte)Math.Clamp(dangerBg.G - 10, 0, 255), (byte)Math.Clamp(dangerBg.B - 10, 0, 255));
            okBtn = new Button
            {
                MinWidth = 88,
                Height = 32,
                MinHeight = 32,
                Padding = new Thickness(16, 0, 16, 0),
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(dangerBg),
                Foreground = ThemeHelper.Brush(Colors.White),
                BorderThickness = new Thickness(0),
                FontWeight = FontWeights.SemiBold,
                UseSystemFocusVisuals = false,
                Content = new TextBlock { Text = L10n.T(confirmText), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(Colors.White) },
            };
            okBtn.Resources["ButtonBackground"] = ThemeHelper.Brush(dangerBg);
            okBtn.Resources["ButtonBackgroundPointerOver"] = ThemeHelper.Brush(dangerHover);
            okBtn.Resources["ButtonBackgroundPressed"] = ThemeHelper.Brush(dangerHover);
            okBtn.Resources["ButtonForeground"] = ThemeHelper.Brush(Colors.White);
            okBtn.Resources["ButtonForegroundPointerOver"] = ThemeHelper.Brush(Colors.White);
            okBtn.Click += (_, _) => { confirmed = true; dlgRef?.Hide(); };
        }
        else
        {
            okBtn = MakeDialogButton(confirmText, () => { confirmed = true; dlgRef?.Hide(); }, dark, useTint, kind: "primary", height: 32, horizontalPadding: 18);
            okBtn.MinWidth = 88;
        }
        var cancelBtn = MakeDialogButton(cancelText, () => dlgRef?.Hide(), dark, useTint, kind: "secondary", height: 32, horizontalPadding: 16);
        cancelBtn.MinWidth = 84;

        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        footerBand.Child = btnRow;
        root.Children.Add(footerBand);

        dlgRef = CreateFluentDialog(xamlRoot, dark, 388, root, tint: useTint);
        dlgRef.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                if (ReferenceEquals(FocusManager.GetFocusedElement(xamlRoot), cancelBtn))
                    return;
                confirmed = true;
                dlgRef.Hide();
                e.Handled = true;
            }
        };
        await dlgRef.ShowSafeAsync(useTint);
        return confirmed;
    }

    /// <summary>
    /// Native Windows 11 Fluent 2 notice dialog.
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
        var useTint = tint ?? App.CurrentApp?.Library?.Prefs.Tint ?? YYTints.Default;
        ContentDialog? dlgRef = null;

        var root = new StackPanel { Spacing = 0 };
        var body = new StackPanel { Spacing = 10, Padding = new Thickness(24, 22, 24, 20) };
        body.Children.Add(new TextBlock
        {
            Text = L10n.T(title),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(message))
        {
            body.Children.Add(new TextBlock
            {
                Text = L10n.T(message),
                FontSize = 13.5,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20,
            });
        }
        root.Children.Add(body);

        var okBtn = MakeDialogButton(okText, () => dlgRef?.Hide(), dark, useTint, kind: "primary", height: 32, horizontalPadding: 20);
        okBtn.MinWidth = 88;
        okBtn.HorizontalAlignment = HorizontalAlignment.Right;

        root.Children.Add(new Border
        {
            Background = ThemeHelper.Brush(dark ? Color.FromArgb(255, 25, 24, 22) : Color.FromArgb(255, 242, 239, 234)),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 10, 10),
            Padding = new Thickness(24, 14, 24, 14),
            Child = okBtn,
        });

        dlgRef = CreateFluentDialog(xamlRoot, dark, 376, root, tint: useTint);
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
    /// Native Windows 11 Fluent 2 single-line input prompt dialog.
    /// </summary>
    public static async Task<string?> ShowPromptAsync(
        XamlRoot? xamlRoot,
        bool dark,
        string title,
        string placeholder = "",
        string initialText = "",
        string confirmText = "确定",
        string cancelText = "取消",
        string? subtitle = null,
        YYTint? tint = null)
    {
        if (xamlRoot is null) return null;
        var p = ThemeHelper.Palette(dark);
        var useTint = tint ?? App.CurrentApp?.Library?.Prefs.Tint ?? YYTints.Default;
        string? submitted = null;
        ContentDialog? dlgRef = null;

        var root = new StackPanel { Spacing = 0 };
        var body = new StackPanel { Spacing = 10, Padding = new Thickness(24, 22, 24, 20) };
        body.Children.Add(new TextBlock
        {
            Text = L10n.T(title),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            body.Children.Add(new TextBlock
            {
                Text = L10n.T(subtitle),
                FontSize = 12.5,
                Foreground = ThemeHelper.Brush(p.T3),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, -4, 0, 0),
            });
        }

        var box = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSingleLineTextBoxStyle"],
            PlaceholderText = L10n.T(placeholder),
            Text = initialText,
            FontSize = 13.5,
            Height = 34,
            MinHeight = 34,
            Background = ThemeHelper.Brush(p.Card),
        };
        body.Children.Add(box);
        root.Children.Add(body);

        void Submit()
        {
            string val = box.Text.Trim();
            if (val.Length == 0) return;
            submitted = val;
            dlgRef?.Hide();
        }

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var okBtn = MakeDialogButton(confirmText, Submit, dark, useTint, kind: "primary", height: 32, horizontalPadding: 18);
        okBtn.MinWidth = 88;
        var cancelBtn = MakeDialogButton(cancelText, () => dlgRef?.Hide(), dark, useTint, kind: "secondary", height: 32, horizontalPadding: 16);
        cancelBtn.MinWidth = 84;
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);

        root.Children.Add(new Border
        {
            Background = ThemeHelper.Brush(dark ? Color.FromArgb(255, 25, 24, 22) : Color.FromArgb(255, 242, 239, 234)),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 10, 10),
            Padding = new Thickness(24, 14, 24, 14),
            Child = btnRow,
        });

        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Submit();
                e.Handled = true;
            }
        };

        dlgRef = CreateFluentDialog(xamlRoot, dark, 396, root, tint: useTint);
        dlgRef.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        await dlgRef.ShowSafeAsync(useTint);
        return submitted;
    }

    /// <summary>
    /// Native Windows 11 Fluent subtle 6px-rounded dialog close button (<c>×</c>).
    /// </summary>
    public static Button MakeDialogCloseButton(Action onClose, bool dark, double size = 30)
    {
        var p = ThemeHelper.Palette(dark);
        Color hoverBg = dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(14, 42, 43, 46);
        Color pressBg = dark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(24, 42, 43, 46);
        var btn = new Button
        {
            Width = size,
            Height = size,
            MinWidth = size,
            MinHeight = size,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            VerticalAlignment = VerticalAlignment.Center,
            Content = YYIcons.Make("xmark", Math.Round(size * 0.40), ThemeHelper.Brush(p.T2)),
        };
        btn.Resources["ButtonBackground"] = ThemeHelper.Brush(Colors.Transparent);
        btn.Resources["ButtonBackgroundPointerOver"] = ThemeHelper.Brush(hoverBg);
        btn.Resources["ButtonBackgroundPressed"] = ThemeHelper.Brush(pressBg);
        AutomationProperties.SetName(btn, L10n.T("关闭面板"));
        btn.Click += (_, _) => onClose();
        return btn;
    }

    /// <summary>
    /// Standard Fluent 2 dialog header row: optional leading visual + semibold title + subtle subtitle + trailing Fluent <c>×</c>.
    /// </summary>
    public static Grid MakeDialogHeader(
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

        var closeBtn = MakeDialogCloseButton(onClose, dark);
        Grid.SetColumn(closeBtn, 1);
        row.Children.Add(closeBtn);
        return row;
    }

    /// <summary>
    /// Native Windows 11 Fluent 2 6px-rounded dialog button
    /// (<c>"primary"</c>/<c>"dark"</c> warm brand accent, <c>"warm"</c> soft tint wash, <c>"secondary"</c> crisp surface card).
    /// </summary>
    public static Button MakeDialogButton(
        object content,
        Action onClick,
        bool dark,
        YYTint tint,
        string kind = "secondary",
        double height = 32,
        double horizontalPadding = 16,
        bool stretch = false)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(tint, dark);
        double clampedH = Math.Min(height, 34);
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
                borderW = 1;
                fw = FontWeights.SemiBold;
                break;
            case "primary":
            case "dark":
                bg = tc.Fill;
                bgHover = Color.FromArgb(255, (byte)Math.Clamp(bg.R + (dark ? 12 : -14), 0, 255), (byte)Math.Clamp(bg.G + (dark ? 12 : -14), 0, 255), (byte)Math.Clamp(bg.B + (dark ? 12 : -14), 0, 255));
                bgPress = Color.FromArgb(255, (byte)Math.Clamp(bg.R + (dark ? -12 : -26), 0, 255), (byte)Math.Clamp(bg.G + (dark ? -12 : -26), 0, 255), (byte)Math.Clamp(bg.B + (dark ? -12 : -26), 0, 255));
                bgDisabled = Color.FromArgb(dark ? (byte)64 : (byte)110, bg.R, bg.G, bg.B);
                fg = Colors.White;
                fgDisabled = Color.FromArgb(180, 255, 255, 255);
                edge = bg;
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
                borderW = 1;
                fw = FontWeights.Normal;
                break;
        }

        UIElement child = content is string s
            ? new TextBlock { Text = L10n.T(s), FontSize = 13, FontWeight = fw, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
            : (UIElement)content;

        var btn = new Button
        {
            Height = clampedH,
            MinHeight = clampedH,
            Padding = new Thickness(horizontalPadding, 0, horizontalPadding, 0),
            CornerRadius = new CornerRadius(6),
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
    /// Smooth XAML RenderTransform vertical fade+slide entrance.
    /// </summary>
    public static void AnimateEntrance(UIElement element, float fromY = 6f, int durationMs = 140)
    {
        try
        {
            if (element.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                element.RenderTransform = tt;
            }
            tt.X = 0;
            tt.Y = fromY;
            element.Opacity = 0.0;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var sb = new Storyboard();

            var yAnim = new DoubleAnimation
            {
                From = fromY,
                To = 0,
                Duration = new Duration(System.TimeSpan.FromMilliseconds(durationMs)),
                EasingFunction = ease,
            };
            Storyboard.SetTarget(yAnim, tt);
            Storyboard.SetTargetProperty(yAnim, "Y");
            sb.Children.Add(yAnim);

            var opAnim = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(System.TimeSpan.FromMilliseconds(Math.Max(80, durationMs - 20))),
                EasingFunction = ease,
            };
            Storyboard.SetTarget(opAnim, element);
            Storyboard.SetTargetProperty(opAnim, "Opacity");
            sb.Children.Add(opAnim);

            sb.Begin();
        }
        catch
        {
            element.Opacity = 1.0;
        }
    }
}
