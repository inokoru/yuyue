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

    private static T? FindNamedDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            var deep = FindNamedDescendant<T>(child, name);
            if (deep is not null) return deep;
        }
        return null;
    }

    /// <summary>
    /// Connects the <c>YYClearButton</c> inside <c>YYSearchTextBoxStyle</c> so any non-empty search/filter box
    /// offers a one-click <c>×</c> reset even when unfocused.
    /// </summary>
    public static void WireClearButton(TextBox box, Action? onCleared = null)
    {
        Button? clearBtn = null;
        void UpdateVisibility()
        {
            if (clearBtn is not null)
                clearBtn.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        void TryAttach()
        {
            if (clearBtn is not null)
            {
                UpdateVisibility();
                return;
            }
            box.ApplyTemplate();
            clearBtn = FindNamedDescendant<Button>(box, "YYClearButton");
            if (clearBtn is null) return;
            clearBtn.Click += (_, _) =>
            {
                box.Text = "";
                onCleared?.Invoke();
                box.Focus(FocusState.Programmatic);
            };
            UpdateVisibility();
        }
        box.Loaded += (_, _) => TryAttach();
        box.TextChanged += (_, _) =>
        {
            if (clearBtn is null) TryAttach();
            else UpdateVisibility();
        };
        TryAttach();
    }

    /// <summary>
    /// Creates a vertically-centered single-line search/filter <see cref="TextBox"/> with an integrated one-click clear button.
    /// </summary>
    public static TextBox MakeSearchBox(
        string placeholder,
        string initialText = "",
        double? width = null,
        double height = 32,
        double fontSize = 12.5,
        Action? onCleared = null)
    {
        var box = new TextBox
        {
            Style = (Style)Application.Current.Resources["YYSearchTextBoxStyle"],
            Text = initialText,
            PlaceholderText = L10n.T(placeholder),
            Height = height,
            MinHeight = height,
            FontSize = fontSize,
        };
        if (width.HasValue) box.Width = width.Value;
        WireClearButton(box, onCleared);
        return box;
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
            okBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"],
                MinWidth = 88,
                Height = 32,
                MinHeight = 32,
                Padding = new Thickness(16, 0, 16, 0),
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.Brush(dangerBg),
                Foreground = ThemeHelper.Brush(Colors.White),
                Content = new TextBlock { Text = L10n.T(confirmText), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeHelper.Brush(Colors.White) },
            };
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
        Color bg, fg, edge;
        double borderW;
        FontWeight fw;
        switch (kind)
        {
            case "warm":
                bg = tc.Soft;
                fg = tc.Accent;
                edge = tc.Edge;
                borderW = 1;
                fw = FontWeights.SemiBold;
                break;
            case "primary":
            case "dark":
                bg = tc.Fill;
                fg = Colors.White;
                edge = bg;
                borderW = 0;
                fw = FontWeights.SemiBold;
                break;
            default:
                bg = p.Card;
                fg = p.T1;
                edge = p.Line;
                borderW = 1;
                fw = FontWeights.Normal;
                break;
        }

        UIElement child = content is string s
            ? new TextBlock { Text = L10n.T(s), FontSize = 13, FontWeight = fw, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
            : (UIElement)content;

        string styleKey = kind switch
        {
            "primary" or "dark" => "YYFluentAccentButtonStyle",
            "warm" => "YYFluentSoftAccentButtonStyle",
            _ => "YYFluentButtonStyle",
        };
        var btn = new Button
        {
            Style = (Style)Application.Current.Resources[styleKey],
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

    /// <summary>
    /// Interactive graphite pencil signature (<c>CTT</c>) for the About bookplate folio
    /// (1:1 <c>apple/Yuyue/Phone/PhoneSignature.swift</c> &amp; <c>MacUpdate.swift</c>).
    /// Writes out all three bezier strokes sequentially on load and whenever clicked.
    /// </summary>
    public static (FrameworkElement View, Action TriggerWrite) MakePencilSignature(double size, bool dark, bool autoWrite = true)
    {
        double k = size / 60.0;
        double w = size;
        double h = size * 56.0 / 60.0;
        Color lead = dark ? Color.FromArgb(255, 217, 213, 205) : Color.FromArgb(255, 47, 46, 45);

        Windows.Foundation.Point Pt(double x, double y) => new((x - 8.0) * k, (y - 6.0) * k);

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

        Microsoft.UI.Xaml.Shapes.Polyline MakeLine(double thickness, double opacity, double dx = 0, double dy = 0, bool grainDash = false)
        {
            var pl = new Microsoft.UI.Xaml.Shapes.Polyline
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
        AutomationProperties.SetName(canvas, "CTT");

        var mainLines = new[] { MakeLine(1.9 * k, 0.84), MakeLine(1.9 * k, 0.84), MakeLine(1.9 * k, 0.84) };
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

        void ApplyTrim(Microsoft.UI.Xaml.Shapes.Polyline target, (Windows.Foundation.Point Pt, double CumLen)[] table, double t)
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
                    target.Points.Add(new Windows.Foundation.Point(a.X + (b.X - a.X) * frac, a.Y + (b.Y - a.Y) * frac));
                    break;
                }
            }
        }

        void RenderInstant()
        {
            for (int i = 0; i < 3; i++)
            {
                ApplyTrim(mainLines[i], samples[i], 1.0);
                ApplyTrim(grainLines[i], samples[i], 1.0);
            }
            ApplyTrim(pressedLine, pressed, 1.0);
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

        if (autoWrite)
        {
            RenderInstant();
            bool playedInitial = false;
            canvas.Loaded += (_, _) =>
            {
                if (playedInitial) return;
                playedInitial = true;
                Write();
            };
        }

        canvas.PointerPressed += (_, e) =>
        {
            Write();
            e.Handled = true;
        };
        canvas.Unloaded += (_, _) => timer?.Stop();

        return (canvas, Write);
    }

    private static (Windows.Foundation.Point Pt, double CumLen)[] SampleMultiBezier(
        (Windows.Foundation.Point P0, Windows.Foundation.Point C1, Windows.Foundation.Point C2, Windows.Foundation.Point P3)[] segs)
    {
        const int stepsPerSeg = 36;
        var list = new List<(Windows.Foundation.Point Pt, double CumLen)>(segs.Length * stepsPerSeg + 1);
        double cum = 0;
        var prev = segs[0].P0;
        list.Add((prev, 0));
        foreach (var (p0, c1, c2, p3) in segs)
        {
            for (int s = 1; s <= stepsPerSeg; s++)
            {
                double t = (double)s / stepsPerSeg;
                double u = 1.0 - t;
                double x = u * u * u * p0.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * p3.X;
                double y = u * u * u * p0.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * p3.Y;
                var cur = new Windows.Foundation.Point(x, y);
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
}
