using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yuyue.Core;
using Yuyue.Design;

namespace Yuyue.UI.Controls;

/// <summary>
/// Native Windows 11 Fluent 2 SettingsExpander (header card + collapsible stacked child rows separated by 1px dividers).
/// </summary>
public sealed class FluentSettingsExpander : UserControl
{
    private readonly Border _shell;
    private readonly Border _headerBar;
    private readonly TextBlock _headerText;
    private readonly TextBlock _descText;
    private readonly ContentPresenter _chevronHost;
    private readonly StackPanel _itemsHost;
    private bool _expanded;
    private bool _dark;

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            _itemsHost.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
            UpdateChevron();
        }
    }

    public FluentSettingsExpander(
        string header,
        string? description = null,
        string? iconName = null,
        UIElement? headerAction = null,
        bool initiallyExpanded = false,
        bool dark = false,
        UIElement? customIcon = null)
    {
        _dark = dark;
        _expanded = initiallyExpanded;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        _shell = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var outer = new StackPanel { Spacing = 0 };

        _headerBar = new Border
        {
            Padding = new Thickness(16, 13, 16, 13),
            MinHeight = 56,
            CornerRadius = _expanded ? new CornerRadius(5, 5, 0, 0) : new CornerRadius(5),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        bool hasIcon = customIcon is not null || !string.IsNullOrEmpty(iconName);
        var hGrid = new Grid { ColumnSpacing = 14 };
        hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = hasIcon ? (customIcon is not null ? GridLength.Auto : new GridLength(22)) : new GridLength(0) });
        hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var p = ThemeHelper.Palette(dark);
        var iconHost = new ContentPresenter
        {
            MinWidth = hasIcon ? 22 : 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed,
            Content = customIcon ?? (string.IsNullOrEmpty(iconName) ? null : YYIcons.Make(iconName, 17, ThemeHelper.Brush(p.T2))),
        };

        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        _headerText = new TextBlock
        {
            Text = L10n.T(header),
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(p.T1),
            TextWrapping = TextWrapping.Wrap,
        };
        _descText = new TextBlock
        {
            Text = L10n.T(description ?? ""),
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible,
        };
        textStack.Children.Add(_headerText);
        textStack.Children.Add(_descText);

        var actionHost = new ContentPresenter
        {
            Content = headerAction,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _chevronHost = new ContentPresenter { VerticalAlignment = VerticalAlignment.Center };

        Grid.SetColumn(iconHost, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(actionHost, 2);
        Grid.SetColumn(_chevronHost, 3);

        hGrid.Children.Add(iconHost);
        hGrid.Children.Add(textStack);
        hGrid.Children.Add(actionHost);
        hGrid.Children.Add(_chevronHost);
        _headerBar.Child = hGrid;

        _headerBar.PointerEntered += (_, _) =>
        {
            _headerBar.Background = ThemeHelper.Brush(_dark
                ? Windows.UI.Color.FromArgb(14, 255, 255, 255)
                : Windows.UI.Color.FromArgb(10, 42, 43, 46));
        };
        _headerBar.PointerExited += (_, _) =>
        {
            _headerBar.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        };
        _headerBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(_headerBar).Properties.IsLeftButtonPressed)
            {
                IsExpanded = !IsExpanded;
                e.Handled = true;
            }
        };

        _itemsHost = new StackPanel
        {
            Spacing = 0,
            Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed,
        };

        outer.Children.Add(_headerBar);
        outer.Children.Add(_itemsHost);
        _shell.Child = outer;
        Content = _shell;

        ApplyTheme(dark);
    }

    public void AddItem(UIElement content)
    {
        var p = ThemeHelper.Palette(_dark);
        var divider = new Border
        {
            Height = 1,
            Background = ThemeHelper.Brush(p.Line),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var row = new Border
        {
            Padding = new Thickness(52, 12, 16, 12),
            Background = ThemeHelper.Brush(_dark
                ? Windows.UI.Color.FromArgb(10, 255, 255, 255)
                : Windows.UI.Color.FromArgb(8, 42, 43, 46)),
            Child = content,
        };
        _itemsHost.Children.Add(divider);
        _itemsHost.Children.Add(row);
    }

    public void AddKeyValueRow(string label, string? description, FrameworkElement rightControl, UIElement? leadingIcon = null)
    {
        var p = ThemeHelper.Palette(_dark);
        var g = new Grid { ColumnSpacing = 12 };
        if (leadingIcon is not null)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int textCol = 0;
        if (leadingIcon is not null)
        {
            var iconWrap = new ContentPresenter
            {
                Content = leadingIcon,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(iconWrap, 0);
            g.Children.Add(iconWrap);
            textCol = 1;
        }

        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock
        {
            Text = L10n.T(label),
            FontSize = 13,
            Foreground = ThemeHelper.Brush(p.T1),
        });
        if (!string.IsNullOrWhiteSpace(description))
        {
            left.Children.Add(new TextBlock
            {
                Text = L10n.T(description),
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        Grid.SetColumn(left, textCol);
        Grid.SetColumn(rightControl, textCol + 1);
        g.Children.Add(left);
        g.Children.Add(rightControl);
        AddItem(g);
    }

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        var p = ThemeHelper.Palette(dark);
        _shell.Background = ThemeHelper.Brush(dark
            ? Windows.UI.Color.FromArgb(255, 36, 34, 32)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255));
        _shell.BorderBrush = ThemeHelper.Brush(p.Line);
        _headerText.Foreground = ThemeHelper.Brush(p.T1);
        _descText.Foreground = ThemeHelper.Brush(p.T2);
        UpdateChevron();
    }

    private void UpdateChevron()
    {
        var p = ThemeHelper.Palette(_dark);
        _headerBar.CornerRadius = _expanded ? new CornerRadius(5, 5, 0, 0) : new CornerRadius(5);
        _chevronHost.Content = YYIcons.Make(_expanded ? "chevron-up" : "chevron-down", 13, ThemeHelper.Brush(p.T3));
    }
}
