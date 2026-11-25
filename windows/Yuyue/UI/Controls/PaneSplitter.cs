using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Yuyue.Design;

namespace Yuyue.UI.Controls;

/// <summary>
/// Clean native WinUI 3 vertical drag splitter (5px hit area, 1px hairline visual indicator that highlights on hover/drag)
/// used between docked Reader side panes and Dual-Book Split View.
/// </summary>
public sealed class PaneSplitter : Grid
{
    private readonly Border _hairline;
    private bool _dragging;
    private bool _hovered;
    private double _lastScreenX;

    public event Action<double>? DragDelta;
    public event Action? DragCompleted;

    public PaneSplitter()
    {
        Width = 6;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Background = new SolidColorBrush(Colors.Transparent);

        _hairline = new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Children.Add(_hairline);
        ApplyHairlineVisual();

        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        Loaded += (_, _) => ApplyHairlineVisual();
        ActualThemeChanged += (_, _) => ApplyHairlineVisual();

        PointerEntered += (_, _) =>
        {
            _hovered = true;
            if (!_dragging) ApplyHairlineVisual();
        };
        PointerExited += (_, _) =>
        {
            _hovered = false;
            if (!_dragging) ApplyHairlineVisual();
        };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
    }

    private void ApplyHairlineVisual()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        if (_dragging)
        {
            var tc = ThemeHelper.TintColors(App.CurrentApp?.Library?.Prefs.Tint ?? YYTints.Default, dark);
            _hairline.Width = 2;
            _hairline.Background = ThemeHelper.Brush(tc.Accent);
        }
        else if (_hovered)
        {
            var tc = ThemeHelper.TintColors(App.CurrentApp?.Library?.Prefs.Tint ?? YYTints.Default, dark);
            _hairline.Width = 2;
            _hairline.Background = ThemeHelper.Brush(tc.Edge);
        }
        else
        {
            var p = ThemeHelper.Palette(dark);
            _hairline.Width = 1;
            _hairline.Background = ThemeHelper.Brush(p.Line);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _lastScreenX = e.GetCurrentPoint(null).Position.X;
        CapturePointer(e.Pointer);
        ApplyHairlineVisual();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        double curX = e.GetCurrentPoint(null).Position.X;
        double dx = curX - _lastScreenX;
        if (Math.Abs(dx) >= 0.5)
        {
            _lastScreenX = curX;
            DragDelta?.Invoke(dx);
        }
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        try { ReleasePointerCapture(e.Pointer); } catch { }
        ApplyHairlineVisual();
        DragCompleted?.Invoke();
        e.Handled = true;
    }
}
