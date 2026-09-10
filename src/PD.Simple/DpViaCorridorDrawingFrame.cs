using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CircuitHub.AllegroBridge;
using CircuitHub.AllegroBridge.Windows;
using PD.Simple.Corridor;

namespace PD.Simple;

/// <summary>A retained consumer raster whose visibility remains owned by the SDK.</summary>
internal sealed class DpViaCorridorDrawingFrame
{
    private const long MaximumPixels = 16_777_216;
    private const int MaximumClipRectangles = 4096;
    private readonly AllegroDesktopBinding _desktop;
    private readonly AllegroCanvasDrawingView _drawingView;

    private DpViaCorridorDrawingFrame(
        AllegroDesktopBinding desktop,
        AllegroCanvasDrawingView drawingView,
        BitmapSource bitmap)
    {
        _desktop = desktop;
        _drawingView = drawingView;
        Bitmap = bitmap;
    }

    internal BitmapSource Bitmap
    {
        get;
    }

    // Check immediately before presentation, and while retaining positional
    // pixels. Keeping the bitmap never renews the underlying native evidence.
    internal bool IsCurrent => _desktop.IsCurrentFor(_drawingView);

    internal static DpViaCorridorDrawingFrame? TryCreate(
        AllegroDesktopBinding desktop,
        AllegroCanvasDrawingView drawingView,
        DpViaCorridorFinding finding,
        IReadOnlyList<AllegroScreenPoint> points,
        double scale)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(drawingView);
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(points);
        if (!drawingView.IsAvailable || !desktop.IsCurrentFor(drawingView))
        {
            return null;
        }

        var bounds = drawingView.View.Canvas!.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 ||
            (long)bounds.Width * bounds.Height > MaximumPixels ||
            drawingView.ClipRectangles.Count > MaximumClipRectangles)
        {
            return null;
        }

        int width = checked((int)bounds.Width);
        int height = checked((int)bounds.Height);
        var localPoints = points.Select(point => new Point(
            (double)point.X - bounds.Left,
            (double)point.Y - bounds.Top)).ToArray();
        var clipRectangles = drawingView.ClipRectangles.Select(rectangle => new Int32Rect(
            checked(rectangle.Left - bounds.Left),
            checked(rectangle.Top - bounds.Top),
            checked((int)rectangle.Width),
            checked((int)rectangle.Height))).ToArray();

        BitmapSource bitmap = Rasterize(width, height, clipRectangles, finding, localPoints, scale);
        if (!desktop.IsCurrentFor(drawingView))
        {
            return null;
        }
        return new DpViaCorridorDrawingFrame(desktop, drawingView, bitmap);
    }

    /// <summary>Draws in physical pixels and masks every output pixel outside the clip union.</summary>
    internal static BitmapSource Rasterize(
        int width,
        int height,
        IReadOnlyList<Int32Rect> clipRectangles,
        DpViaCorridorFinding finding,
        IReadOnlyList<Point> localPoints,
        double scale)
    {
        ArgumentNullException.ThrowIfNull(clipRectangles);
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(localPoints);
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The physical raster exceeds the rendering resource bound.");
        }
        if (clipRectangles.Count > MaximumClipRectangles)
        {
            throw new ArgumentOutOfRangeException(nameof(clipRectangles));
        }
        int expectedPointCount = finding.Intrusion is null ? 6 : 7;
        if (localPoints.Count != expectedPointCount ||
            localPoints.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            throw new ArgumentException("Supply four corridor corners, P/N centers, and the optional measured intrusion.", nameof(localPoints));
        }
        if (!double.IsFinite(scale) || scale <= 0 || scale > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        var clipGeometry = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (Int32Rect rectangle in clipRectangles)
        {
            if (rectangle.X < 0 || rectangle.Y < 0 || rectangle.Width <= 0 || rectangle.Height <= 0 ||
                (long)rectangle.X + rectangle.Width > width || (long)rectangle.Y + rectangle.Height > height)
            {
                throw new ArgumentException("Every SDK clip rectangle must lie inside the physical canvas.", nameof(clipRectangles));
            }
            clipGeometry.Children.Add(new RectangleGeometry(new Rect(
                rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height)));
        }
        clipGeometry.Freeze();

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.PushClip(clipGeometry);
            // Original annotations are WPF-DIP sized. Rendering at 96 DPI
            // makes drawing units physical pixels, so scale only the styles;
            // SDK-projected positions already have their exact physical size.
            DpViaCorridorCanvas.DrawAnnotations(context, finding, localPoints,
                new Rect(0, 0, width, height), scale, 1);
            DrawCapturedStatus(context, width, height, scale);
            context.Pop();
        }

        int stride = checked(width * 4);
        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var source = new byte[checked(stride * height)];
        rendered.CopyPixels(source, stride, 0);
        var masked = new byte[source.Length];
        foreach (Int32Rect rectangle in clipRectangles)
        {
            int rowLength = checked(rectangle.Width * 4);
            for (int y = rectangle.Y; y < rectangle.Y + rectangle.Height; y++)
            {
                int offset = checked(y * stride + rectangle.X * 4);
                Buffer.BlockCopy(source, offset, masked, offset, rowLength);
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, masked, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void DrawCapturedStatus(DrawingContext context, int width, int height, double scale)
    {
        double availableWidth = width - 16 * scale;
        double availableHeight = height - 16 * scale;
        if (availableWidth <= 18 * scale || availableHeight <= 12 * scale)
        {
            return;
        }

        var text = new FormattedText(
            "Captured DP corridor · re-run after edits",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            11 * scale,
            new SolidColorBrush(Color.FromRgb(244, 247, 250)),
            1)
        {
            MaxTextWidth = Math.Min(availableWidth, 420 * scale) - 18 * scale
        };
        double badgeHeight = text.Height + 12 * scale;
        if (badgeHeight > availableHeight)
        {
            return;
        }

        var badge = new Rect(8 * scale, 8 * scale, text.Width + 18 * scale, badgeHeight);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(238, 11, 18, 26)),
            new Pen(new SolidColorBrush(Color.FromRgb(77, 163, 255)), scale),
            badge,
            10 * scale,
            10 * scale);
        context.DrawText(text, new Point(badge.Left + 9 * scale, badge.Top + 6 * scale));
    }
}
