using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PD.Simple.Corridor;

namespace PD.Simple.Corridor;

/// <summary>
/// Shows immutable Engine/WPF review pixels or measured corridor geometry.
/// Captured pixels are never treated as live board or edit authority.
/// </summary>
public sealed class DpViaCorridorCanvas : FrameworkElement
{
    public static readonly DependencyProperty FindingProperty = DependencyProperty.Register(
        nameof(Finding), typeof(DpViaCorridorFinding), typeof(DpViaCorridorCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (sender, _) => ((DpViaCorridorCanvas)sender).ResetView()));
    public static readonly DependencyProperty HasAnalysisProperty = DependencyProperty.Register(
        nameof(HasAnalysis), typeof(bool), typeof(DpViaCorridorCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ReviewCaptureProperty = DependencyProperty.Register(
        nameof(ReviewCapture), typeof(DpViaCorridorReviewCapture), typeof(DpViaCorridorCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (sender, _) => ((DpViaCorridorCanvas)sender).ResetView()));
    public static readonly DependencyProperty ShowCorridorProperty = DependencyProperty.Register(
        nameof(ShowCorridor), typeof(bool), typeof(DpViaCorridorCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(DpViaCorridorCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsPanEnabledProperty = DependencyProperty.Register(
        nameof(IsPanEnabled), typeof(bool), typeof(DpViaCorridorCanvas),
        new FrameworkPropertyMetadata(true));
    private double _zoom = 1;
    private double _scale;
    private Point _worldCenter;
    private Point _screenCenter;
    private Vector _panOffset;
    private Point? _panStart;
    internal Rect ReviewImageBounds { get; private set; } = Rect.Empty;
    public DpViaCorridorFinding? Finding
    {
        get => (DpViaCorridorFinding?)GetValue(FindingProperty);
        set => SetValue(FindingProperty, value);
    }
    public bool HasAnalysis
    {
        get => (bool)GetValue(HasAnalysisProperty); set => SetValue(HasAnalysisProperty, value);
    }
    public DpViaCorridorReviewCapture? ReviewCapture
    {
        get => (DpViaCorridorReviewCapture?)GetValue(ReviewCaptureProperty);
        set => SetValue(ReviewCaptureProperty, value);
    }
    public bool ShowCorridor
    {
        get => (bool)GetValue(ShowCorridorProperty); set => SetValue(ShowCorridorProperty, value);
    }
    public bool ShowLabels
    {
        get => (bool)GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value);
    }
    public bool IsPanEnabled
    {
        get => (bool)GetValue(IsPanEnabledProperty); set => SetValue(IsPanEnabledProperty, value);
    }
    public DpViaCorridorCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }
    public void ResetView()
    {
        _zoom = 1;
        _panOffset = new Vector(0, 0);
        InvalidateVisual();
    }
    public void ZoomIn() => StepZoom(1.25);
    public void ZoomOut() => StepZoom(1 / 1.25);
    private void StepZoom(double factor)
    {
        _zoom = Math.Clamp(_zoom * factor, 0.5, 8);
        InvalidateVisual();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        StepZoom(e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsPanEnabled)
        {
            _panStart = e.GetPosition(this);
            CaptureMouse();
            Cursor = Cursors.Hand;
            e.Handled = true;
        }
        base.OnMouseLeftButtonDown(e);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_panStart is { } start && IsMouseCaptured)
        {
            Point current = e.GetPosition(this);
            _panOffset += current - start;
            _panStart = current;
            InvalidateVisual();
        }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_panStart is not null)
        {
            _panStart = null;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            e.Handled = true;
        }
        base.OnMouseLeftButtonUp(e);
    }
    internal Point Project(DpViaCorridorPoint point) => new(
        _screenCenter.X + (point.XMil - _worldCenter.X) * _scale,
        _screenCenter.Y - (point.YMil - _worldCenter.Y) * _scale);

    /// <summary>
    /// Returns the facade-composed review image without reprojecting PD geometry.
    /// </summary>
    public BitmapSource ExportCapturedImage(bool annotated)
    {
        DpViaCorridorReviewCapture capture = RequireCapturedImage();
        return annotated ? capture.Annotated : capture.Raw;
    }

    private DpViaCorridorReviewCapture RequireCapturedImage()
    {
        if (ReviewCapture is not { } capture ||
            Finding is not { } finding ||
            capture.FindingId != finding.Id || capture.Layer != finding.Layer)
        {
            throw new InvalidOperationException(
                "Select a crossing with a current captured review before exporting.");
        }

        return capture;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        ReviewImageBounds = Rect.Empty;
        if (ActualWidth < 80 || ActualHeight < 80)
        {
            return;
        }

        dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(15, 37, 52),
            Color.FromRgb(7, 19, 30), new Point(0, 0), new Point(1, 1)),
            new Pen(Brush("#27495D"), 1), new Rect(RenderSize), 8, 8);
        if (ReviewCapture is { } capture && Finding is { } selected &&
            capture.FindingId == selected.Id && capture.Layer == selected.Layer)
        {
            BitmapSource image = ShowCorridor ? capture.Annotated : capture.Raw;
            var fit = Math.Min(
                (ActualWidth - 16) / image.PixelWidth,
                (ActualHeight - 62) / image.PixelHeight) * _zoom;
            var width = image.PixelWidth * fit;
            var height = image.PixelHeight * fit;
            ReviewImageBounds = new Rect((ActualWidth - width) / 2 + _panOffset.X,
                32 + (ActualHeight - 62 - height) / 2 + _panOffset.Y, width, height);
            var visible = Rect.Intersect(
                ReviewImageBounds,
                new Rect(8, 32, ActualWidth - 16, ActualHeight - 62));
            dc.PushClip(new RectangleGeometry(visible));
            dc.DrawImage(image, ReviewImageBounds);
            dc.Pop();
            Label(
                dc,
                ShowCorridor
                    ? "CAPTURED REVIEW + CANONICAL DRAWING"
                    : "CAPTURED ALLEGRO VIEW · RAW PIXELS",
                12,
                12,
                9,
                "#9AAFC6");
            Label(
                dc,
                $"Selected finding layer: {capture.Layer} · captured " +
                $"{capture.CapturedAt.LocalDateTime:HH:mm:ss}",
                12, ActualHeight - 23, 9, "#91A8C0");
            return;
        }
        var grid = new Pen(Brush("#203044"), 0.5);
        for (double x = 20; x < ActualWidth; x += 24)
        {
            dc.DrawLine(grid, new Point(x, 30), new Point(x, ActualHeight - 30));
        }

        for (double y = 38; y < ActualHeight - 30; y += 24)
        {
            dc.DrawLine(grid, new Point(0, y), new Point(ActualWidth, y));
        }

        Label(dc, Finding is null && !HasAnalysis ? "ILLUSTRATION · NOT BOARD DATA" : "CAPTURED CORRIDOR · MILS", 12, 12, 9, "#9AAFC6");
        if (Finding is null && HasAnalysis)
        {
            Label(dc, "No crossing selected", 18, ActualHeight / 2 - 8, 12, "#A7BBD1");
            return;
        }
        var illustration = Finding is null;
        var finding = Finding ?? new DpViaCorridorFinding("illustration", "Example pair", "Example aggressor",
            "trace", "Example layer", "Illustration", "MEDIUM", new(-20, 0), new(20, 0), new(0, 2), 2, 11, 20);
        IReadOnlyList<DpViaCorridorPoint> measuredCorners;
        try
        {
            measuredCorners = DpViaCorridorGeometry.CorridorCorners(finding);
        }
        catch (ArgumentException)
        {
            Label(dc, "Corridor geometry unavailable", 18, ActualHeight / 2, 12, "#FFBE81");
            return;
        }
        var corners = measuredCorners.Select(point => new Point(point.XMil, point.YMil)).ToArray();
        var bounds = Rect.Empty;
        foreach (var corner in corners)
        {
            bounds.Union(corner);
        }

        bounds.Union(new Point(finding.P.XMil, finding.P.YMil));
        bounds.Union(new Point(finding.N.XMil, finding.N.YMil));
        if (finding.Intrusion is { } intrusion)
        {
            bounds.Union(new Point(intrusion.XMil, intrusion.YMil));
        }

        if (!double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
        {
            return;
        }

        _worldCenter = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        _screenCenter = new Point(ActualWidth / 2 + _panOffset.X, ActualHeight / 2 + _panOffset.Y);
        _scale = Math.Min((ActualWidth - 88) / Math.Max(1, bounds.Width),
            (ActualHeight - 108) / Math.Max(1, bounds.Height)) * _zoom;
        Point Screen(Point point) => Project(new DpViaCorridorPoint(point.X, point.Y));
        var p = Project(finding.P);
        var n = Project(finding.N);
        // Only the explicitly labeled illustration may contain schematic
        // traces. Engine captures supply pixels, not copper authority.
        if (illustration)
        {
            DrawIllustrationTrace(dc, p.X, p, "#1689FF", -1);
            DrawIllustrationTrace(dc, n.X, n, "#43B5FF", 1);
            var foreign = Project(finding.Intrusion!);
            var points = new[] { new Point(foreign.X - 10, 42),
                new Point(foreign.X, 66), foreign,
                new Point(foreign.X, ActualHeight - 105),
                new Point(foreign.X + 14, ActualHeight - 88),
                new Point(foreign.X + 14, ActualHeight - 58) };
            DrawPath(dc, points, new Pen(Brush("#542E36"), 10));
            DrawPath(dc, points, new Pen(Brush("#E67468"), 3));
        }
        if (ShowCorridor)
        {
            var shape = new StreamGeometry();
            using (var context = shape.Open())
            {
                context.BeginFigure(Screen(corners[0]), true, true);
                context.PolyLineTo(corners.Skip(1).Select(Screen).ToArray(), true, false);
            }
            var fill = new LinearGradientBrush(Color.FromArgb(18, 71, 141, 240),
                Color.FromArgb(65, 71, 141, 240), new Point(0, 0), new Point(0, 1));
            dc.DrawGeometry(fill, new Pen(Brush("#64ABFF"), 1.5) { DashStyle = DashStyles.Dash }, shape);
            if (ShowLabels)
            {
                var upper = corners.Select(Screen).Min(point => point.Y);
                Label(dc, "DP CORRIDOR", Math.Max(12, ActualWidth / 2 - 42),
                    Math.Max(38, upper - 27), 10, "#75CBFF");
            }
        }
        dc.DrawLine(new Pen(Brush("#617F9E"), 1) { DashStyle = DashStyles.Dot }, p, n);
        DrawVia(dc, p, "P");
        DrawVia(dc, n, "N");
        if (finding.Intrusion is { } captured)
        {
            var crossing = Project(captured);
            var risk = finding.Risk == "CRITICAL" ? "#FF8D80" : finding.Risk == "MEDIUM" ? "#FFD180" : "#79D6C3";
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(35, 255, 110, 98)), null, crossing, 14, 14);
            dc.DrawEllipse(Brush("#101C2A"), new Pen(Brush(risk), 2), crossing, 7, 7);
            dc.DrawLine(new Pen(Brush(risk), 2), crossing + new Vector(-3, -3), crossing + new Vector(3, 3));
            dc.DrawLine(new Pen(Brush(risk), 2), crossing + new Vector(-3, 3), crossing + new Vector(3, -3));
            if (ShowLabels)
            {
                var callout = new Point(Math.Clamp(crossing.X + 22, 12, Math.Max(12, ActualWidth - 120)),
                    Math.Clamp(crossing.Y + 30, 52, Math.Max(52, ActualHeight - 86)));
                dc.DrawLine(new Pen(Brush(risk), 1), crossing + new Vector(7, 7), callout);
                dc.DrawRoundedRectangle(Brush("#442C33"), new Pen(Brush(risk), 1),
                    new Rect(callout, new Size(108, 23)), 4, 4);
                Label(dc, illustration ? "Example foreign net" : "Captured intrusion", callout.X + 7, callout.Y + 5, 9, risk);
            }
        }
        Label(dc, illustration ? "Example foreign conductor" : finding.Intrusion is null ?
            "Intrusion location not supplied" : "× Captured intrusion location", 12, ActualHeight - 40, 10, "#BECEDE");
        Label(dc, illustration ? "Via centers and protected corridor" :
            "Via centers + bounds · not copper outlines", 12, ActualHeight - 24, 9, "#91A8C0");
    }

    private void DrawVia(DrawingContext dc, Point point, string name)
    {
        // Fixed-size glyphs identify centers; these are not measured pad radii.
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(35, 46, 152, 255)), null, point, 18, 18);
        dc.DrawEllipse(new LinearGradientBrush(Color.FromRgb(154, 203, 232),
            Color.FromRgb(37, 82, 108), 90), new Pen(Brush("#49B6FF"), 2), point, 13, 13);
        dc.DrawEllipse(Brush("#0B1924"), new Pen(Brush("#B1D2DE"), 1.4), point, 8, 8);
        if (ShowLabels)
        {
            Label(dc, name, point.X + 18, point.Y - 8, 11, "#78CAFF");
        }
    }
    private void DrawIllustrationTrace(DrawingContext dc, double x, Point via, string color, int direction)
    {
        var points = new[] { new Point(x, 45), via, new Point(x, ActualHeight - 115),
            new Point(x + direction * 15, ActualHeight - 95),
            new Point(x + direction * 15, ActualHeight - 58) };
        DrawPath(dc, points, new Pen(Brush("#163D60"), 14));
        DrawPath(dc, points, new Pen(Brush(color), 7));
    }
    private static void DrawPath(DrawingContext dc, Point[] points, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToArray(), true, false);
        }
        dc.DrawGeometry(null, pen, geometry);
    }
    private void Label(DrawingContext dc, string text, double x, double y, double size, string color)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brush(color), VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, ActualWidth - Math.Max(0, x) - 10),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        dc.DrawText(formatted, new Point(x, y));
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
