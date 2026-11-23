using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Yuyue.Design;

/// <summary>
/// 1:1 port of <c>apple/Yuyue/Design/Parts.swift YYResizeHandle</c>:
/// vertical drag handle for resizing right-hand Settings inspector and Reader / PDF sidebars,
/// with native horizontal resize cursor, hover pill indicator, clamped live resizing, and double-tap reset.
/// </summary>
public sealed class YYResizeHandle : Grid
{
    private readonly Border _pill;
    private bool _dragging;
    private double _startPointerX;
    private double _startWidth;

    public Func<double>? GetCurrentWidth { get; set; }
    public Action<double>? ApplyWidth { get; set; }
    public Action<double>? CommitWidth { get; set; }
    public Func<double>? GetMaxWidth { get; set; }
    public double MinWidthLimit { get; set; } = 252.0;
    public double DefaultWidth { get; set; } = 304.0;

    public YYResizeHandle()
    {
        Width = 8;
        Background = new SolidColorBrush(Colors.Transparent);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Stretch;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        _pill = new Border
        {
            Width = 3,
            Height = 36,
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        Children.Add(_pill);

        PointerEntered += (_, _) => _pill.Opacity = 0.75;
        PointerExited += (_, _) => { if (!_dragging) _pill.Opacity = 0; };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
        DoubleTapped += OnDoubleTapped;
    }

    private double ClampWidth(double raw)
    {
        double max = GetMaxWidth?.Invoke() ?? 540.0;
        if (max < MinWidthLimit) max = MinWidthLimit;
        return Math.Clamp(Math.Round(raw), MinWidthLimit, max);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(null);
        if (!pt.Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _startPointerX = pt.Position.X;
        _startWidth = GetCurrentWidth?.Invoke() ?? DefaultWidth;
        _pill.Opacity = 1.0;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        double curX = e.GetCurrentPoint(null).Position.X;
        // Dragging left increases a right-hand panel's width; dragging right shrinks it.
        double next = ClampWidth(_startWidth + (_startPointerX - curX));
        ApplyWidth?.Invoke(next);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        FinishDrag(e.GetCurrentPoint(null).Position.X);
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _pill.Opacity = 0;
        double cur = ClampWidth(GetCurrentWidth?.Invoke() ?? DefaultWidth);
        CommitWidth?.Invoke(cur);
    }

    private void FinishDrag(double endX)
    {
        _dragging = false;
        _pill.Opacity = 0;
        double next = ClampWidth(_startWidth + (_startPointerX - endX));
        ApplyWidth?.Invoke(next);
        CommitWidth?.Invoke(next);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        double reset = ClampWidth(DefaultWidth);
        ApplyWidth?.Invoke(reset);
        CommitWidth?.Invoke(reset);
        e.Handled = true;
    }
}
