using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PD.Simple.Corridor;

namespace PD.Simple;

/// <summary>
/// Tool-specific screen HUD. It receives already validated/projected positions;
/// it never acquires a canvas, derives board coordinates or grants drawing authority.
/// </summary>
internal sealed class BoardOverlayHud : FrameworkElement
{
    internal static readonly Brush ValidBrush = FrozenBrush("#35D07F");
    internal static readonly Brush InvalidBrush = FrozenBrush("#FF647C");
    internal static readonly Brush NeutralBrush = FrozenBrush("#8CA2B8");
    private static readonly Brush FirstPickBrush = FrozenBrush("#4DA3FF");
    private static readonly Brush TextBrush = FrozenBrush("#F4F7FA");
    private static readonly Brush BackgroundBrush = FrozenBrush("#EE0B121A");
    private static readonly Brush AmberBrush = FrozenBrush("#FFD16A");

    private enum HudMode
    {
        None,
        Picking,
        Completion
    }
    private HudMode _mode;
    private Point? _cursor;
    private Point? _firstPick;
    private Brush _cursorBrush = NeutralBrush;
    private string _cursorLabel = string.Empty;
    private string? _measurement;
    private string? _firstPickLabel;
    private bool _highContrast;
    private DpViaCorridorFinding? _finding;
    private Point[] _corridorPoints = [];

    internal BoardOverlayHud()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
    }

    internal void ShowPicking(Point? cursor, Brush? color, string label,
        string? measurement, Point? firstPick, string? firstPickLabel, bool highContrast)
    {
        _mode = HudMode.Picking;
        Clip = null;
        _cursor = cursor;
        _cursorBrush = color ?? NeutralBrush;
        _cursorLabel = label;
        _measurement = measurement;
        _firstPick = firstPick;
        _firstPickLabel = firstPickLabel;
        _highContrast = highContrast;
        InvalidateVisual();
    }

    internal void ShowCompletion(Point start, Point end, string label, bool highContrast)
    {
        _mode = HudMode.Completion;
        Clip = null;
        _firstPick = start;
        _cursor = end;
        _cursorLabel = label + " · endpoints only";
        _highContrast = highContrast;
        InvalidateVisual();
    }

    internal BitmapSource RasterizeCorridor(int width, int height,
        DpViaCorridorFinding finding, Point[] points, double scale,
        IReadOnlyList<Int32Rect> clipRectangles)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(clipRectangles);
        if (points.Length != (finding.Intrusion is null ? 6 : 7) ||
            points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            throw new ArgumentException("Supply the SDK-projected corridor anchors.", nameof(points));
        }
        if (width <= 0 || height <= 0 || (long)width * height > 16_777_216 ||
            !double.IsFinite(scale) || scale <= 0 || scale > 16 || clipRectangles.Count > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The HUD raster exceeds its rendering bounds.");
        }
        foreach (Int32Rect rectangle in clipRectangles)
        {
            if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width <= 0 || rectangle.Height <= 0 ||
                (long)rectangle.X + rectangle.Width > width || (long)rectangle.Y + rectangle.Height > height)
            {
                throw new ArgumentException("SDK clipping must remain inside the physical raster.", nameof(clipRectangles));
            }
        }
        // Keep the FrameworkElement empty. Only the final masked bitmap may be
        // shown; a WPF geometry clip does not hard-mask antialiased edge pixels.
        _mode = HudMode.None;
        _finding = finding;
        _corridorPoints = points;
        InvalidateVisual();
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(scale, scale));
            DrawCorridor(context);
            context.Pop();
        }
        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        int stride = checked(width * 4);
        var source = new byte[checked(stride * height)];
        var masked = new byte[source.Length];
        rendered.CopyPixels(source, stride, 0);
        foreach (Int32Rect rectangle in clipRectangles)
        {
            int rowLength = checked(rectangle.Width * 4);
            for (int y = rectangle.Y; y < rectangle.Y + rectangle.Height; y++)
            {
                int offset = checked(y * stride + rectangle.X * 4);
                Buffer.BlockCopy(source, offset, masked, offset, rowLength);
            }
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Pbgra32, null, masked, stride);
        bitmap.Freeze();
        return bitmap;
    }

    internal void Clear()
    {
        _mode = HudMode.None;
        _finding = null;
        _corridorPoints = [];
        Clip = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 16 || ActualHeight <= 16)
        {
            return;
        }
        if (_mode is not (HudMode.Picking or HudMode.Completion))
        {
            return;
        }

        Brush current = _highContrast ? SystemColors.HighlightBrush :
            _mode == HudMode.Completion ? ValidBrush : _cursorBrush;
        Brush accepted = _highContrast ? SystemColors.HighlightBrush :
            _mode == HudMode.Completion ? ValidBrush : FirstPickBrush;
        double stroke = _highContrast ? 4 : 2.5;
        if (_firstPick is { } first)
        {
            drawing.DrawEllipse(null, new Pen(accepted, stroke), first, 12, 12);
            drawing.DrawEllipse(accepted, null, first, 3, 3);
        }
        if (_cursor is not { } cursor)
        {
            return;
        }
        drawing.DrawEllipse(null, new Pen(current, stroke), cursor, 17, 17);
        string label = _cursorLabel;
        if (_mode == HudMode.Picking && !string.IsNullOrWhiteSpace(_measurement))
        {
            label += "\n" + _measurement;
        }
        Rect card = DrawCard(drawing, label, cursor, new Vector(22, 18), current, 360);
        if (_mode == HudMode.Picking && _firstPick is { } anchor &&
            !string.IsNullOrWhiteSpace(_firstPickLabel))
        {
            Rect badge = CardBounds(_firstPickLabel, anchor, new Vector(16, -34), 260);
            if (!badge.IntersectsWith(card))
            {
                DrawCard(drawing, _firstPickLabel, anchor, new Vector(16, -34), accepted, 260);
            }
        }
    }

    private void DrawCorridor(DrawingContext drawing)
    {
        if (ActualWidth <= 16 || ActualHeight <= 16)
        {
            return;
        }
        Point p = _corridorPoints[4];
        Point n = _corridorPoints[5];
        Rect visible = new(0, 0, ActualWidth, ActualHeight);
        Brush blue = FrozenBrush("#61C6FF");
        DrawCenter(p, n, "P");
        DrawCenter(n, p, "N");
        Point corner = new(_corridorPoints.Take(4).Min(point => point.X),
            _corridorPoints.Take(4).Min(point => point.Y));
        DrawCard(drawing, "DP CORRIDOR", corner, new Vector(-116, 5), blue, 150);
        DrawCard(drawing, "Captured DP corridor · re-run after edits",
            new Point(8, 8), default, FirstPickBrush, 420);

        if (_finding!.Intrusion is not null && visible.Contains(_corridorPoints[6]))
        {
            Point crossing = _corridorPoints[6];
            drawing.DrawEllipse(null, new Pen(BackgroundBrush, 5), crossing, 7, 7);
            drawing.DrawEllipse(null, new Pen(AmberBrush, 2), crossing, 7, 7);
            drawing.DrawLine(new Pen(AmberBrush, 2), crossing + new Vector(-3, -3), crossing + new Vector(3, 3));
            drawing.DrawLine(new Pen(AmberBrush, 2), crossing + new Vector(-3, 3), crossing + new Vector(3, -3));
            string label = "INTERFERING NET\n" + _finding.AggressorNet;
            var labelAnchor = new Point(ActualWidth - 8, ActualHeight - 8);
            Rect card = CardBounds(label, labelAnchor, default, 236);
            if (card.IsEmpty)
            {
                return;
            }
            Point target = new(Math.Clamp(crossing.X, card.Left, card.Right), card.Top);
            Vector leader = target - crossing;
            if (leader.Length > 9)
            {
                leader.Normalize();
                drawing.DrawLine(new Pen(BackgroundBrush, 3), crossing + leader * 9, target);
                drawing.DrawLine(new Pen(AmberBrush, 1), crossing + leader * 9, target);
            }
            DrawCard(drawing, label, labelAnchor, default, AmberBrush, 236);
        }

        void DrawCenter(Point point, Point other, string label)
        {
            if (!visible.Contains(point))
            {
                return;
            }
            drawing.DrawEllipse(null, new Pen(BackgroundBrush, 5), point, 6, 6);
            drawing.DrawEllipse(null, new Pen(blue, 2), point, 6, 6);
            drawing.DrawLine(new Pen(blue, 1), point + new Vector(-3, 0), point + new Vector(3, 0));
            drawing.DrawLine(new Pen(blue, 1), point + new Vector(0, -3), point + new Vector(0, 3));
            Vector away = point - other;
            if (away.Length > 0)
            {
                away.Normalize();
            }
            DrawCard(drawing, label, point + away * 25, new Vector(-12, -12), blue, 40);
        }
    }

    private Rect DrawCard(DrawingContext drawing, string label, Point anchor,
        Vector offset, Brush border, double maximumWidth)
    {
        FormattedText text = FormatText(label, maximumWidth);
        Rect bounds = CardBounds(text, anchor, offset);
        if (bounds.IsEmpty)
        {
            return Rect.Empty;
        }
        drawing.DrawRoundedRectangle(BackgroundBrush, new Pen(border, 1), bounds, 6, 6);
        drawing.DrawText(text, bounds.TopLeft + new Vector(8, 6));
        return bounds;
    }

    private Rect CardBounds(string label, Point anchor, Vector offset, double maximumWidth) =>
        CardBounds(FormatText(label, maximumWidth), anchor, offset);

    private Rect CardBounds(FormattedText text, Point anchor, Vector offset)
    {
        double width = text.Width + 16;
        double height = text.Height + 12;
        if (height > ActualHeight - 16)
        {
            return Rect.Empty;
        }
        double left = anchor.X + offset.X;
        double top = anchor.Y + offset.Y;
        if (left + width > ActualWidth - 8)
        {
            left = anchor.X - width - Math.Abs(offset.X);
        }
        if (top + height > ActualHeight - 8)
        {
            top = anchor.Y - height - 18;
        }
        return new Rect(Math.Max(8, left), Math.Max(8, top), width, height);
    }

    private FormattedText FormatText(string text, double maximumWidth) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI Semibold"), 11, TextBrush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip)
    {
        MaxTextWidth = Math.Max(1, Math.Min(maximumWidth, ActualWidth - 32)),
        MaxLineCount = 4,
        Trimming = TextTrimming.CharacterEllipsis
    };

    private static Brush FrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
