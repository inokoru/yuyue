using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Pdf;

namespace Yuyue.UI.Pdf;

public sealed partial class PdfTabView
{
    private readonly Dictionary<int, Canvas> _pageOverlays = [];
    private readonly Dictionary<int, PdfPageLayout> _layoutCache = [];
    private int _selPage = -1;
    private int _selEndPage = -1;
    private int _selStart = -1;
    private int _selEnd = -1;
    private int _selAnchorGlyph = -1;
    private bool _selDragging;
    private Canvas? _activeDragCanvas;
    private Mark? _editingMark;
    private MarkStyle _lastMarkStyle = MarkStyle.Yellow;
    private Point _lastReleasePointerInHost;
    private Point _lastSelectionAnchorInHost;
    private (int Page, int CharOffset, int MatchLen)? _activeSearchHighlight;
    private int _listeningPage = -1;
    private PageSentence[] _activePageSentences = [];

    private Grid BuildInteractivePageSurface(int pageIdx, BitmapImage bmp, double displayW, double displayH)
    {
        var img = new Image
        {
            Source = bmp,
            Width = displayW,
            Height = displayH,
            Stretch = Stretch.Uniform,
            Opacity = _nightMode ? 0.88 : 1.0,
            IsHitTestVisible = false,
        };

        var overlay = new Canvas
        {
            Width = displayW,
            Height = displayH,
            Background = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = true,
        };
        _pageOverlays[pageIdx] = overlay;
        AttachOverlayEvents(overlay, pageIdx);

        var grid = new Grid { Width = displayW, Height = displayH };
        grid.Children.Add(img);
        grid.Children.Add(overlay);

        RefreshPageOverlay(pageIdx);
        return grid;
    }

    private PdfPageLayout GetOrLoadPageLayoutSync(int pageIdx)
    {
        if (_layoutCache.TryGetValue(pageIdx, out var cached)) return cached;
        if (string.IsNullOrEmpty(_pdfPath)) return new PdfPageLayout(pageIdx, "", []);
        try
        {
            var layout = PdfInfoReader.ExtractPageLayout(_pdfPath, pageIdx);
            _layoutCache[pageIdx] = layout;
            return layout;
        }
        catch
        {
            var empty = new PdfPageLayout(pageIdx, "", []);
            _layoutCache[pageIdx] = empty;
            return empty;
        }
    }

    private void RefreshAllVisibleOverlays()
    {
        foreach (int pg in _pageOverlays.Keys.ToArray())
            RefreshPageOverlay(pg);
    }

    private void RefreshPageOverlay(int pageIndex)
    {
        if (_disposed || !_pageOverlays.TryGetValue(pageIndex, out var canvas)) return;
        canvas.Children.Clear();

        double w = canvas.Width > 0 ? canvas.Width : canvas.ActualWidth;
        double h = canvas.Height > 0 ? canvas.Height : canvas.ActualHeight;
        if (w <= 1 || h <= 1) return;

        bool hasSavedMarks = false;
        IReadOnlyList<Mark> allMarks = [];
        try
        {
            allMarks = _library.GetMarks(_bookId);
            hasSavedMarks = allMarks.Any(mk =>
            {
                int ps = ResolveMarkPageIndex(mk);
                int pe = Math.Max(ps, (int)mk.End.Sentence);
                return pageIndex >= ps && pageIndex <= pe;
            });
        }
        catch { }

        bool hasSearchHit = _activeSearchHighlight is { } sh && sh.Page == pageIndex && !string.IsNullOrEmpty(_searchQuery);
        bool hasSpeech = _audio.IsPlaying && pageIndex == _listeningPage && _speakingIdx >= 0 && _speakingIdx < _activePageSentences.Length;
        bool hasSel = _selPage >= 0 && _selStart >= 0 && pageIndex >= _selPage && pageIndex <= Math.Max(_selPage, _selEndPage);
        if (!hasSavedMarks && !hasSearchHit && !hasSpeech && !hasSel) return;

        var layout = GetOrLoadPageLayoutSync(pageIndex);
        if (layout.Glyphs.Length == 0) return;

        bool dark = IsEffectiveDark;
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        // 1. Saved marks
        if (hasSavedMarks)
        {
            foreach (var mk in allMarks)
            {
                int pStart = ResolveMarkPageIndex(mk);
                int pEnd = Math.Max(pStart, (int)mk.End.Sentence);
                if (pageIndex < pStart || pageIndex > pEnd) continue;

                var (c0, c1) = ResolveMarkCharRangeOnPage(layout, mk, pageIndex);
                if (c1 <= c0) continue;
                var rects = PdfInfoReader.MergeLineRects(layout, c0, c1);
                DrawMarkRects(canvas, rects, w, h, mk.Style, palette);
            }
        }

        // 2. Active search hit
        if (hasSearchHit && _activeSearchHighlight is var (_, hitOff, hitLen))
        {
            int spanStart = -1, spanEnd = -1;
            var normHits = PdfInfoReader.FindNormalizedMatches(layout.Text, _searchQuery);
            if (normHits.Count > 0)
            {
                var best = normHits.OrderBy(m => Math.Abs(m.Start - hitOff)).First();
                spanStart = best.Start;
                spanEnd = Math.Min(layout.Text.Length, best.Start + best.Length);
            }
            else if (hitLen > 0 && hitOff >= 0 && hitOff < layout.Text.Length)
            {
                spanStart = hitOff;
                spanEnd = Math.Min(layout.Text.Length, hitOff + hitLen);
            }
            if (spanStart >= 0 && spanEnd > spanStart)
            {
                var hitBrush = ThemeHelper.Brush(Color.FromArgb(128, 255, 196, 36));
                foreach (var r in PdfInfoReader.MergeLineRects(layout, spanStart, spanEnd))
                {
                    var box = new Rectangle
                    {
                        Width = Math.Max(3, r.W * w + 3),
                        Height = Math.Max(5, r.H * h + 2),
                        RadiusX = 3,
                        RadiusY = 3,
                        Fill = hitBrush,
                        Stroke = ThemeHelper.Brush(tint.Accent),
                        StrokeThickness = 1,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(box, Math.Max(0, r.X * w - 1.5));
                    Canvas.SetTop(box, Math.Max(0, r.Y * h - 1));
                    canvas.Children.Add(box);
                }
            }
        }

        // 3. Active TTS spoken sentence
        if (hasSpeech)
        {
            var sent = _activePageSentences[_speakingIdx];
            var speechBrush = ThemeHelper.Brush(Color.FromArgb(_nightMode ? (byte)64 : (byte)46, tint.Accent.R, tint.Accent.G, tint.Accent.B));
            foreach (var r in PdfInfoReader.MergeLineRects(layout, (int)sent.Start, (int)sent.End))
            {
                var box = new Rectangle
                {
                    Width = Math.Max(2, r.W * w + 2),
                    Height = Math.Max(4, r.H * h + 2),
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = speechBrush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(box, Math.Max(0, r.X * w - 1));
                Canvas.SetTop(box, Math.Max(0, r.Y * h - 1));
                canvas.Children.Add(box);
            }
        }

        // 4. Live selection range
        if (hasSel)
        {
            int sp = _selPage;
            int ep = Math.Max(sp, _selEndPage);
            int r0 = pageIndex == sp ? _selStart : 0;
            int r1 = pageIndex == ep ? _selEnd : layout.Text.Length;
            if (r1 > r0)
            {
                var selBrush = ThemeHelper.Brush(Color.FromArgb(_nightMode ? (byte)84 : (byte)62, tint.Accent.R, tint.Accent.G, tint.Accent.B));
                foreach (var r in PdfInfoReader.MergeLineRects(layout, r0, r1))
                {
                    var box = new Rectangle
                    {
                        Width = Math.Max(2, r.W * w),
                        Height = Math.Max(4, r.H * h),
                        RadiusX = 2,
                        RadiusY = 2,
                        Fill = selBrush,
                        IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(box, r.X * w);
                    Canvas.SetTop(box, r.Y * h);
                    canvas.Children.Add(box);
                }
            }
        }
    }

    private static (int Start, int End) ResolveMarkCharRangeOnPage(PdfPageLayout layout, Mark mk, int pageIndex)
    {
        int pStart = ResolveMarkPageIndex(mk);
        int pEnd = Math.Max(pStart, (int)mk.End.Sentence);
        int maxLen = layout.Text.Length;
        if (pStart != pEnd)
        {
            if (pageIndex == pStart) return (Math.Clamp((int)mk.Start.Offset, 0, maxLen), maxLen);
            if (pageIndex == pEnd) return (0, Math.Clamp((int)mk.End.Offset, 0, maxLen));
            return (0, maxLen);
        }
        return ResolveMarkCharRange(layout, mk);
    }

    private static (int Start, int End) ResolveMarkCharRange(PdfPageLayout layout, Mark mk)
    {
        int s = (int)mk.Start.Offset;
        int e = (int)mk.End.Offset;
        int maxLen = layout.Text.Length;
        if (s >= 0 && e > s && e <= maxLen)
        {
            string slice = layout.Text.Substring(s, e - s);
            if (string.IsNullOrWhiteSpace(mk.Text) || slice.Trim().Equals(mk.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                return (s, e);
        }
        if (!string.IsNullOrWhiteSpace(mk.Text))
        {
            int idx = layout.Text.IndexOf(mk.Text.Trim(), StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return (idx, Math.Min(maxLen, idx + mk.Text.Trim().Length));
        }
        return (Math.Clamp(s, 0, maxLen), Math.Clamp(e, 0, maxLen));
    }

    private void DrawMarkRects(Canvas canvas, List<PdfLineRect> rects, double w, double h, MarkStyle style, YYPalette p)
    {
        foreach (var r in rects)
        {
            double rx = r.X * w;
            double ry = r.Y * h;
            double rw = Math.Max(3, r.W * w);
            double rh = Math.Max(6, r.H * h);

            if (style is MarkStyle.Yellow or MarkStyle.Blue or MarkStyle.Pink)
            {
                Color baseCol = style switch
                {
                    MarkStyle.Blue => p.MkB,
                    MarkStyle.Pink => p.MkP,
                    _ => p.MkY,
                };
                byte alpha = _nightMode ? (byte)54 : (byte)76;
                var rect = new Rectangle
                {
                    Width = rw + 2,
                    Height = rh + 2,
                    RadiusX = 2.5,
                    RadiusY = 2.5,
                    Fill = ThemeHelper.Brush(Color.FromArgb(alpha, baseCol.R, baseCol.G, baseCol.B)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, Math.Max(0, rx - 1));
                Canvas.SetTop(rect, Math.Max(0, ry - 1));
                canvas.Children.Add(rect);
            }
            else if (style == MarkStyle.Wave)
            {
                var fig = new PathFigure { StartPoint = new Point(rx, ry + rh + 1.2) };
                double step = 4.0, amp = 1.65, cx = rx;
                bool up = true;
                while (cx < rx + rw)
                {
                    double nx = Math.Min(cx + step, rx + rw);
                    double mx = (cx + nx) * 0.5;
                    double cy = ry + rh + 1.2 + (up ? -amp : amp);
                    fig.Segments.Add(new QuadraticBezierSegment { Point1 = new Point(mx, cy), Point2 = new Point(nx, ry + rh + 1.2) });
                    cx = nx;
                    up = !up;
                }
                var geo = new PathGeometry();
                geo.Figures.Add(fig);
                canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = geo,
                    Stroke = ThemeHelper.Brush(p.HlR),
                    StrokeThickness = 1.35,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false,
                });
            }
            else if (style == MarkStyle.Underline)
            {
                canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = rx,
                    Y1 = ry + rh + 1,
                    X2 = rx + rw,
                    Y2 = ry + rh + 1,
                    Stroke = ThemeHelper.Brush(p.T1),
                    StrokeThickness = 1.6,
                    IsHitTestVisible = false,
                });
            }
            else if (style == MarkStyle.Dotted)
            {
                canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = rx,
                    Y1 = ry + rh + 1,
                    X2 = rx + rw,
                    Y2 = ry + rh + 1,
                    Stroke = ThemeHelper.Brush(p.T2),
                    StrokeThickness = 1.5,
                    StrokeDashArray = [2.2, 2.2],
                    IsHitTestVisible = false,
                });
            }
        }
    }

    private void AttachOverlayEvents(Canvas overlay, int pageIdx)
    {
        overlay.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(overlay);
            if (!pt.Properties.IsLeftButtonPressed) return;
            PdfInfoPopupCard.Visibility = Visibility.Collapsed;

            double w = overlay.Width > 0 ? overlay.Width : overlay.ActualWidth;
            double h = overlay.Height > 0 ? overlay.Height : overlay.ActualHeight;
            if (w <= 1 || h <= 1) return;

            var layout = GetOrLoadPageLayoutSync(pageIdx);
            if (layout.Glyphs.Length == 0)
            {
                HidePdfSelectionCard();
                return;
            }

            double nx = Math.Clamp(pt.Position.X / w, 0, 1);
            double ny = Math.Clamp(pt.Position.Y / h, 0, 1);
            int glyphIdx = PdfInfoReader.HitTestGlyph(layout, nx, ny);
            if (glyphIdx < 0)
            {
                HidePdfSelectionCard();
                return;
            }

            // Shift+click on a later page extends selection across pages
            if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift) && _selPage >= 0 && pageIdx > _selPage)
            {
                try { _lastReleasePointerInHost = overlay.TransformToVisual(CanvasHost).TransformPoint(pt.Position); } catch { }
                _editingMark = null;
                _selEndPage = pageIdx;
                _selEnd = layout.Glyphs[glyphIdx].CharEnd;
                for (int pi = _selPage; pi <= _selEndPage; pi++)
                {
                    GetOrLoadPageLayoutSync(pi);
                    RefreshPageOverlay(pi);
                }
                ShowPdfSelectionCard(overlay, pageIdx, layout);
                e.Handled = true;
                return;
            }

            int prevStart = _selPage, prevEnd = Math.Max(_selPage, _selEndPage);
            _editingMark = null;
            _selPage = pageIdx;
            _selEndPage = pageIdx;
            _selAnchorGlyph = glyphIdx;
            _selStart = layout.Glyphs[glyphIdx].CharStart;
            _selEnd = layout.Glyphs[glyphIdx].CharEnd;
            _selDragging = true;
            _activeDragCanvas = overlay;
            overlay.CapturePointer(e.Pointer);
            PdfSelectionCard.Visibility = Visibility.Collapsed;
            if (prevStart >= 0)
                for (int pi = prevStart; pi <= prevEnd; pi++)
                    if (pi != pageIdx) RefreshPageOverlay(pi);
            RefreshPageOverlay(pageIdx);
            e.Handled = true;
        };

        overlay.PointerMoved += (_, e) =>
        {
            if (!_selDragging || !ReferenceEquals(_activeDragCanvas, overlay)) return;
            if (pageIdx != _selPage || !_layoutCache.TryGetValue(pageIdx, out var layout)) return;
            double w = overlay.Width > 0 ? overlay.Width : overlay.ActualWidth;
            double h = overlay.Height > 0 ? overlay.Height : overlay.ActualHeight;
            if (w <= 1 || h <= 1) return;

            var pt = e.GetCurrentPoint(overlay).Position;
            int curGlyph = PdfInfoReader.HitTestGlyph(layout, Math.Clamp(pt.X / w, 0, 1), Math.Clamp(pt.Y / h, 0, 1));
            if (curGlyph < 0 || _selAnchorGlyph < 0) return;

            int g0 = Math.Min(_selAnchorGlyph, curGlyph);
            int g1 = Math.Max(_selAnchorGlyph, curGlyph);
            int nextStart = layout.Glyphs[g0].CharStart;
            int nextEnd = layout.Glyphs[g1].CharEnd;
            if (nextStart != _selStart || nextEnd != _selEnd)
            {
                _selStart = nextStart;
                _selEnd = nextEnd;
                RefreshPageOverlay(pageIdx);
            }
            e.Handled = true;
        };

        overlay.PointerReleased += (_, e) =>
        {
            if (!_selDragging || !ReferenceEquals(_activeDragCanvas, overlay)) return;
            _selDragging = false;
            _activeDragCanvas = null;
            try { overlay.ReleasePointerCapture(e.Pointer); } catch { }

            if (!_layoutCache.TryGetValue(pageIdx, out var layout)) return;
            double w = overlay.Width > 0 ? overlay.Width : overlay.ActualWidth;
            double h = overlay.Height > 0 ? overlay.Height : overlay.ActualHeight;
            var pt = e.GetCurrentPoint(overlay).Position;
            try { _lastReleasePointerInHost = overlay.TransformToVisual(CanvasHost).TransformPoint(pt); } catch { }

            if (_selEnd - _selStart <= 1)
            {
                var hitMark = FindMarkAtPoint(pageIdx, layout, pt.X / Math.Max(1, w), pt.Y / Math.Max(1, h));
                if (hitMark is not null)
                {
                    _editingMark = hitMark;
                    var (c0, c1) = ResolveMarkCharRange(layout, hitMark);
                    _selPage = pageIdx;
                    _selEndPage = pageIdx;
                    _selStart = c0;
                    _selEnd = c1;
                    RefreshPageOverlay(pageIdx);
                    ShowPdfSelectionCard(overlay, pageIdx, layout);
                    e.Handled = true;
                    return;
                }
                HidePdfSelectionCard();
                return;
            }

            _editingMark = null;
            RefreshPageOverlay(pageIdx);
            ShowPdfSelectionCard(overlay, pageIdx, layout);
            e.Handled = true;
        };

        overlay.PointerCaptureLost += (_, _) =>
        {
            if (ReferenceEquals(_activeDragCanvas, overlay))
            {
                _selDragging = false;
                _activeDragCanvas = null;
            }
        };
    }

    private Mark? FindMarkAtPoint(int pageIndex, PdfPageLayout layout, double normX, double normY)
    {
        try
        {
            foreach (var mk in _library.GetMarks(_bookId))
            {
                int pStart = ResolveMarkPageIndex(mk);
                int pEnd = Math.Max(pStart, (int)mk.End.Sentence);
                if (pageIndex < pStart || pageIndex > pEnd) continue;
                var (c0, c1) = ResolveMarkCharRangeOnPage(layout, mk, pageIndex);
                foreach (var r in PdfInfoReader.MergeLineRects(layout, c0, c1))
                {
                    if (normX >= r.X - 0.008 && normX <= r.X + r.W + 0.008
                        && normY >= r.Y - 0.01 && normY <= r.Y + r.H + 0.012)
                        return mk;
                }
            }
        }
        catch { }
        return null;
    }

    private void ShowPdfSelectionCard(Canvas overlay, int pageIndex, PdfPageLayout layout)
    {
        int r0 = (_selEndPage > _selPage && pageIndex == _selEndPage) ? 0 : _selStart;
        var rects = PdfInfoReader.MergeLineRects(layout, r0, _selEnd);
        if (rects.Count == 0) { HidePdfSelectionCard(); return; }

        bool hasNote = _editingMark is not null && !string.IsNullOrWhiteSpace(_editingMark.Note);
        PdfSelNotePreviewBox.Visibility = hasNote ? Visibility.Visible : Visibility.Collapsed;
        PdfSelNotePreviewText.Text = hasNote ? _editingMark!.Note! : "";
        PdfSelDeleteBtn.Visibility = _editingMark is not null ? Visibility.Visible : Visibility.Collapsed;

        double cardW = 352;
        double cardH = hasNote ? 148 : 96;
        double ow = overlay.Width > 0 ? overlay.Width : overlay.ActualWidth;
        double oh = overlay.Height > 0 ? overlay.Height : overlay.ActualHeight;
        var first = rects[0];
        var last = rects[^1];

        try
        {
            var xform = overlay.TransformToVisual(CanvasHost);
            var topPt = xform.TransformPoint(new Point((first.X + first.W * 0.5) * ow, first.Y * oh));
            var botPt = xform.TransformPoint(new Point((last.X + last.W * 0.5) * ow, (last.Y + last.H) * oh));

            double hostW = CanvasHost.ActualWidth > 0 ? CanvasHost.ActualWidth : 900;
            double hostH = CanvasHost.ActualHeight > 0 ? CanvasHost.ActualHeight : 640;
            double botSafe = Math.Max(14, hostH - cardH - 16);

            _lastSelectionAnchorInHost = new Point(Math.Clamp(topPt.X, 24, Math.Max(24, hostW - 24)), Math.Clamp(botPt.Y + 6, 14, Math.Max(14, hostH - 40)));
            double x = Math.Clamp(topPt.X - cardW * 0.5, 14, Math.Max(14, hostW - cardW - 14));
            double aboveY = topPt.Y - cardH - 10;
            double belowY = botPt.Y + 10;
            double y = aboveY >= 12 ? aboveY : (belowY <= botSafe ? belowY : Math.Clamp(_lastReleasePointerInHost.Y - cardH - 12, 12, botSafe));
            PdfSelectionCard.Margin = new Thickness(Math.Round(x), Math.Round(Math.Clamp(y, 12, botSafe)), 0, 0);
        }
        catch
        {
            _lastSelectionAnchorInHost = new Point(220, 140);
            PdfSelectionCard.Margin = new Thickness(24, 24, 0, 0);
        }
        PdfSelectionCard.Visibility = Visibility.Visible;
    }

    private void HidePdfSelectionCard()
    {
        int p0 = _selPage;
        int p1 = Math.Max(p0, _selEndPage);
        _selPage = -1;
        _selEndPage = -1;
        _selStart = -1;
        _selEnd = -1;
        _selAnchorGlyph = -1;
        _editingMark = null;
        PdfSelectionCard.Visibility = Visibility.Collapsed;
        if (p0 >= 0)
            for (int pi = p0; pi <= p1; pi++) RefreshPageOverlay(pi);
    }

    private string GetSelectedPdfText()
    {
        if (_editingMark is not null) return _editingMark.Text;
        if (_selPage < 0) return "";
        int endPg = Math.Max(_selPage, _selEndPage);
        if (endPg == _selPage)
        {
            if (!_layoutCache.TryGetValue(_selPage, out var layout)) return "";
            int s = Math.Clamp(_selStart, 0, layout.Text.Length);
            int e = Math.Clamp(_selEnd, s, layout.Text.Length);
            return layout.Text[s..e].Trim();
        }
        var sb = new System.Text.StringBuilder();
        for (int pi = _selPage; pi <= endPg; pi++)
        {
            var layout = GetOrLoadPageLayoutSync(pi);
            int r0 = pi == _selPage ? Math.Clamp(_selStart, 0, layout.Text.Length) : 0;
            int r1 = pi == endPg ? Math.Clamp(_selEnd, r0, layout.Text.Length) : layout.Text.Length;
            if (r1 > r0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(layout.Text[r0..r1].Trim());
            }
        }
        return sb.ToString().Trim();
    }

    private void ApplyPdfMarkStyle(MarkStyle style)
    {
        _lastMarkStyle = style;
        if (_editingMark is { } existing)
        {
            _library.SetMarkStyle(_bookId, existing.Id, style);
            int p0 = ResolveMarkPageIndex(existing);
            int p1 = Math.Max(p0, (int)existing.End.Sentence);
            HidePdfSelectionCard();
            for (int pi = p0; pi <= p1; pi++) RefreshPageOverlay(pi);
            if (_rightOpen) RenderRightNotesPane();
            App.CurrentApp.NotifyShelfRefresh();
            return;
        }

        string text = GetSelectedPdfText();
        int pageStart = _selPage;
        int pageEnd = Math.Max(_selPage, _selEndPage);
        int c0 = Math.Max(0, _selStart);
        int c1 = Math.Max(0, _selEnd);
        HidePdfSelectionCard();
        if (pageStart < 0 || string.IsNullOrWhiteSpace(text)) return;

        _library.AddMark(_bookId, 0, new TextPoint((uint)pageStart, (uint)c0), new TextPoint((uint)pageEnd, (uint)c1), text, style);
        for (int pi = pageStart; pi <= pageEnd; pi++) RefreshPageOverlay(pi);
        if (_rightOpen) RenderRightNotesPane();
        App.CurrentApp.NotifyShelfRefresh();
    }

    private void PdfMarkStyle_Click(object sender, RoutedEventArgs e)
    {
        string tag = (sender as FrameworkElement)?.Tag as string ?? "yellow";
        var style = tag switch
        {
            "blue" => MarkStyle.Blue,
            "pink" => MarkStyle.Pink,
            "wave" => MarkStyle.Wave,
            "underline" => MarkStyle.Underline,
            "dotted" => MarkStyle.Dotted,
            _ => MarkStyle.Yellow,
        };
        ApplyPdfMarkStyle(style);
    }

    private void PdfSelDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_editingMark is not { } mk) return;
        int p0 = ResolveMarkPageIndex(mk);
        int p1 = Math.Max(p0, (int)mk.End.Sentence);
        HidePdfSelectionCard();
        try { _library.DeleteMark(_bookId, mk.Id); } catch { }
        for (int pi = p0; pi <= p1; pi++) RefreshPageOverlay(pi);
        if (_rightOpen) RenderRightNotesPane();
        App.CurrentApp.NotifyShelfRefresh();
    }

    private void PdfSelNotePreview_Pressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        PdfSelNote_Click(sender, new RoutedEventArgs());
    }

    private async void PdfSelNote_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || RootGrid.XamlRoot is not { } xr) return;
        var existing = _editingMark;
        string quote = GetSelectedPdfText();
        int pageStart = existing is not null ? ResolveMarkPageIndex(existing) : _selPage;
        int pageEnd = existing is not null ? Math.Max(pageStart, (int)existing.End.Sentence) : Math.Max(_selPage, _selEndPage);
        int c0 = Math.Max(0, _selStart);
        int c1 = Math.Max(0, _selEnd);
        HidePdfSelectionCard();
        if (pageStart < 0 || string.IsNullOrWhiteSpace(quote)) return;

        bool dark = IsEffectiveDark;
        var noteBox = new TextBox
        {
            Text = existing?.Note ?? "",
            PlaceholderText = L10n.T("写下你对这段文字的想法…"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
        };
        var dlg = new ContentDialog
        {
            XamlRoot = xr,
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
            Title = L10n.T("想法"),
            PrimaryButtonText = L10n.T("保存"),
            CloseButtonText = L10n.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 10,
                MinWidth = 360,
                Children =
                {
                    L10n.Skip(new TextBlock { Text = quote, FontSize = 12, Opacity = 0.72, MaxLines = 3, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis }),
                    noteBox,
                },
            },
        };
        ThemeHelper.ApplyControlThemeResources(dlg.Resources, dark, _library.Prefs.Tint);
        if (await dlg.ShowSafeAsync(_library.Prefs.Tint) != ContentDialogResult.Primary || _disposed) return;

        string note = (noteBox.Text ?? "").Trim();
        if (existing is not null)
            _library.SetMarkNote(_bookId, existing.Id, string.IsNullOrEmpty(note) ? null : note);
        else
            _library.AddMark(_bookId, 0, new TextPoint((uint)pageStart, (uint)c0), new TextPoint((uint)pageEnd, (uint)c1), quote, _lastMarkStyle, string.IsNullOrEmpty(note) ? null : note);

        for (int pi = pageStart; pi <= pageEnd; pi++) RefreshPageOverlay(pi);
        if (_rightOpen) RenderRightNotesPane();
        App.CurrentApp.NotifyShelfRefresh();
    }

    private void PdfSelSpeak_Click(object sender, RoutedEventArgs e)
    {
        int pageIdx = _selPage >= 0 ? _selPage : _currentPage;
        int charAt = _editingMark is not null ? (int)_editingMark.Start.Offset : Math.Max(0, _selStart);
        HidePdfSelectionCard();
        if (pageIdx != _currentPage) JumpToPage(pageIdx);
        var layout = GetOrLoadPageLayoutSync(pageIdx);
        var sents = YuyueCoreMethods.PageSentences(layout.Text);
        int hitIdx = 0;
        for (int i = 0; i < sents.Length; i++)
        {
            if (charAt >= (int)sents[i].Start && charAt < (int)sents[i].End) { hitIdx = i; break; }
            if (charAt >= (int)sents[i].Start) hitIdx = i;
        }
        StartReadCurrentPage(hitIdx);
    }

    private void PdfSelLookup_Click(object sender, RoutedEventArgs e)
    {
        string text = GetSelectedPdfText();
        var anchor = _lastSelectionAnchorInHost;
        HidePdfSelectionCard();
        if (string.IsNullOrWhiteSpace(text)) return;

        var hit = _library.LookUpWordRich(text);
        string body = hit is not null
            ? $"{hit.Headword}{(string.IsNullOrWhiteSpace(hit.Phonetic) ? "" : $"  {hit.Phonetic}")}\n{hit.Summary}\n({hit.Source})"
            : L10n.T($"词典里没有「{text}」，可点击「翻译」查看译文。");
        ShowPdfInfoPopup("查词", body, anchor);
    }

    private async void PdfSelTranslate_Click(object sender, RoutedEventArgs e)
    {
        string text = GetSelectedPdfText();
        var anchor = _lastSelectionAnchorInHost;
        HidePdfSelectionCard();
        if (string.IsNullOrWhiteSpace(text)) return;

        if (!_library.Companion.IsConfigured)
        {
            ShowPdfInfoPopup("翻译", L10n.T("请先在「设置 · 书伴」中配好模型。"), anchor);
            return;
        }

        ShowPdfInfoPopup("翻译", L10n.T("正在翻译…"), anchor);
        try
        {
            string res = await _library.Companion.ChatAsync(text, "你是一位信达雅的文学与学术翻译。直接给出自然流畅的译文，不要额外寒暄。");
            if (_disposed) return;
            PdfInfoPopupBody.Text = res.Trim();
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            PdfInfoPopupBody.Text = $"{L10n.T("翻译失败：")}{CompanionClient.FormatErrorMessage(ex)}";
        }
    }

    private void PdfSelCopy_Click(object sender, RoutedEventArgs e)
    {
        string text = GetSelectedPdfText();
        HidePdfSelectionCard();
        if (string.IsNullOrWhiteSpace(text)) return;
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    private void ShowPdfInfoPopup(string title, string body, Point anchor)
    {
        PdfInfoPopupTitle.Text = L10n.T(title);
        PdfInfoPopupBody.Text = body;
        double hostW = CanvasHost.ActualWidth > 0 ? CanvasHost.ActualWidth : 900;
        double hostH = CanvasHost.ActualHeight > 0 ? CanvasHost.ActualHeight : 640;
        double x = Math.Clamp(anchor.X - 178, 14, Math.Max(14, hostW - 370));
        double y = Math.Clamp(anchor.Y, 14, Math.Max(14, hostH - 220));
        PdfInfoPopupCard.Margin = new Thickness(Math.Round(x), Math.Round(y), 0, 0);
        PdfInfoPopupCard.Visibility = Visibility.Visible;
    }

    private void ClosePdfInfoPopup_Click(object sender, RoutedEventArgs e) => PdfInfoPopupCard.Visibility = Visibility.Collapsed;
}
