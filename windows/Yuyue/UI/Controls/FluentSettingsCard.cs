using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yuyue.Core;
using Yuyue.Design;

namespace Yuyue.UI.Controls;

/// <summary>
/// Native Windows 11 Fluent 2 SettingsCard (warm stationery palette, 6px radius, 1px hairline border,
/// optional left icon, header + description stack, right control slot, and optional clickable row with chevron).
/// </summary>
public sealed class FluentSettingsCard : UserControl
{
    private readonly Border _shell;
    private readonly Grid _root;
    private readonly ContentPresenter _iconHost;
    private readonly TextBlock _headerText;
    private readonly TextBlock _descText;
    private readonly ContentPresenter _actionHost;
    private readonly ContentPresenter _chevronHost;
    private readonly Action? _onClick;
    private bool _dark;

    public string Header
    {
        get => _headerText.Text;
        set => _headerText.Text = L10n.T(value);
    }

    public string? Description
    {
        get => _descText.Text;
        set
        {
            _descText.Text = L10n.T(value ?? "");
            _descText.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public object? ActionContent
    {
        get => _actionHost.Content;
        set => _actionHost.Content = value;
    }

    public FluentSettingsCard(
        string header,
        string? description = null,
        string? iconName = null,
        UIElement? action = null,
        Action? onClick = null,
        bool dark = false,
        UIElement? customIcon = null)
    {
        _dark = dark;
        _onClick = onClick;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        _shell = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 13, 16, 13),
            MinHeight = 56,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        bool hasIcon = customIcon is not null || !string.IsNullOrEmpty(iconName);
        _root = new Grid { ColumnSpacing = 14 };
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = hasIcon ? (customIcon is not null ? GridLength.Auto : new GridLength(22)) : new GridLength(0) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _iconHost = new ContentPresenter
        {
            MinWidth = hasIcon ? 22 : 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed,
        };
        if (customIcon is not null)
        {
            _iconHost.Content = customIcon;
        }
        else if (!string.IsNullOrEmpty(iconName))
        {
            var p = ThemeHelper.Palette(dark);
            _iconHost.Content = YYIcons.Make(iconName, 17, ThemeHelper.Brush(p.T2));
        }

        var textStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _headerText = new TextBlock
        {
            Text = L10n.T(header),
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
        };
        _descText = new TextBlock
        {
            Text = L10n.T(description ?? ""),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible,
        };
        textStack.Children.Add(_headerText);
        textStack.Children.Add(_descText);

        _actionHost = new ContentPresenter
        {
            Content = action,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        _chevronHost = new ContentPresenter
        {
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = onClick is not null ? Visibility.Visible : Visibility.Collapsed,
        };

        Grid.SetColumn(_iconHost, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(_actionHost, 2);
        Grid.SetColumn(_chevronHost, 3);

        _root.Children.Add(_iconHost);
        _root.Children.Add(textStack);
        _root.Children.Add(_actionHost);
        _root.Children.Add(_chevronHost);
        _shell.Child = _root;
        Content = _shell;

        ApplyTheme(dark);

        if (onClick is not null)
        {
            _shell.PointerEntered += (_, _) =>
            {
                _shell.Background = ThemeHelper.Brush(_dark
                    ? Windows.UI.Color.FromArgb(255, 42, 40, 37)
                    : Windows.UI.Color.FromArgb(255, 250, 248, 244));
            };
            _shell.PointerExited += (_, _) => ApplyTheme(_dark);
            _shell.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(_shell).Properties.IsLeftButtonPressed)
                {
                    _onClick?.Invoke();
                    e.Handled = true;
                }
            };
        }
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
        if (_onClick is not null)
            _chevronHost.Content = YYIcons.Make("chevron-right", 13, ThemeHelper.Brush(p.T3));
    }
}
