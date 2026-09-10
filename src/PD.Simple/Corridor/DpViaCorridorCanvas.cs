using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PD.Simple.Corridor;

namespace PD.Simple.Corridor;

/// <summary>Shows owned Allegro pixels or measured corridor geometry; never invents copper outlines.</summary>
public sealed class DpViaCorridorCanvas : FrameworkElement
{
    public static readonly DependencyProperty FindingProperty = DependencyProperty.Register(
        nameof(Finding), typeof(DpViaCorridorFinding), typeof(DpViaCorridorCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (sender, _) => ((DpViaCorridorCanvas)sender).ResetView()));
    public static readonly DependencyProperty HasAnalysisProperty = DependencyProperty.Register(
        nameof(HasAnalysis), typeof(bool), typeof(DpViaCorridorCanvas),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty NativeCaptureProperty = DependencyProperty.Register(
        nameof(NativeCapture), typeof(DpViaCorridorNativeCapture), typeof(DpViaCorridorCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            (sender, _) => ((DpViaCorridorCanvas)sender).ResetView()));
    public static readonly DependencyProperty ShowCorridorProperty = DependencyProperty.Register(
        nameof(ShowCorridor), typeof(bool), typeof(DpViaCorridorCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    private double _zoom = 1;
    private double _scale;
    private Point _worldCenter;
    private Point _screenCenter;
    internal Rect NativeImageBounds { get; private set; } = Rect.Empty;
    public DpViaCorridorFinding? Finding
    {
        get => (DpViaCorridorFinding?)GetValue(FindingProperty);
        set => SetValue(FindingProperty, value);
    }
    public bool HasAnalysis
    {
        get => (bool)GetValue(HasAnalysisProperty); set => SetValue(HasAnalysisProperty, value);
    }
    public DpViaCorridorNativeCapture? NativeCapture
    {
        get => (DpViaCorridorNativeCapture?)GetValue(NativeCaptureProperty);
        set => SetValue(NativeCaptureProperty, value);
    }
    public bool ShowCorridor
    {
        get => (bool)GetValue(ShowCorridorProperty); set => SetValue(ShowCorridorProperty, value);
    }
    public DpViaCorridorCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }
    public void ResetView()
    {
        _zoom = 1;
        InvalidateVisual();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.5, 8);
        InvalidateVisual();
        e.Handled = true;
    }
    internal Point Project(DpViaCorridorPoint point) => new(
        _screenCenter.X + (point.XMil - _worldCenter.X) * _scale,
        _screenCenter.Y - (point.YMil - _worldCenter.Y) * _scale);

    internal Point ProjectNative(DpViaCorridorPoint point)
    {
        var (capture, _) = RequireNativeImage();
        if (NativeImageBounds.IsEmpty)
        {
            throw new InvalidOperationException("The native image has not been laid out.");
        }

        return ProjectImage(point, capture, NativeImageBounds);
    }

    /// <summary>Exports native-resolution pixels, optionally annotated; never changes the raw capture.</summary>
    public BitmapSource ExportNativeImage(bool annotated)
    {
        var (capture, finding) = RequireNativeImage();
        if (!annotated)
        {
            return capture.Image;
        }

        var bounds = new Rect(0, 0, capture.Image.PixelWidth, capture.Image.PixelHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(capture.Image, bounds);
            dc.PushClip(new RectangleGeometry(bounds));
            DrawNativeAnnotations(dc, capture, finding, bounds, bounds,
                Math.Clamp(capture.Image.PixelWidth / 800d, 1, 3), 1);
            dc.Pop();
        }
        var image = new RenderTargetBitmap(capture.Image.PixelWidth, capture.Image.PixelHeight,
            96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }

    private (DpViaCorridorNativeCapture Capture, DpViaCorridorFinding Finding) RequireNativeImage()
    {
        if (NativeCapture is not { } capture || Finding is not { } finding ||
            capture.FindingId != finding.Id || capture.Layer != finding.Layer)
        {
            throw new InvalidOperationException("Select a crossing with a current native image before exporting.");
        }

        return (capture, finding);
    }

    private static Point ProjectImage(DpViaCorridorPoint point, DpViaCorridorNativeCapture capture, Rect destination)
    {
        var pixel = DpViaCorridorGeometry.ProjectToImage(point, capture.Bounds, destination.Width, destination.Height);
        return new Point(destination.Left + pixel.X, destination.Top + pixel.Y);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        NativeImageBounds = Rect.Empty;
        if (ActualWidth < 80 || ActualHeight < 80)
        {
            return;
        }

        dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(15, 37, 52),
            Color.FromRgb(7, 19, 30), new Point(0, 0), new Point(1, 1)),
            new Pen(Brush("#27495D"), 1), new Rect(RenderSize), 8, 8);
        if (NativeCapture is { } capture && Finding is { } selected &&
            capture.FindingId == selected.Id && capture.Layer == selected.Layer)
        {
            // Image and annotations share this exact destination rectangle.
            // Markers identify measured centers, not pad radii or copper paths.
            var fit = Math.Min((ActualWidth - 16) / capture.Image.PixelWidth,
                (ActualHeight - 62) / capture.Image.PixelHeight) * _zoom;
            var width = capture.Image.PixelWidth * fit;
            var height = capture.Image.PixelHeight * fit;
            NativeImageBounds = new Rect((ActualWidth - width) / 2,
                32 + (ActualHeight - 62 - height) / 2, width, height);
            var visible = Rect.Intersect(NativeImageBounds, new Rect(8, 32, ActualWidth - 16, ActualHeight - 62));
            dc.PushClip(new RectangleGeometry(visible));
            dc.DrawImage(capture.Image, NativeImageBounds);
            if (ShowCorridor)
            {
                DrawNativeAnnotations(dc, capture, selected, NativeImageBounds, visible, 1,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
            }

            dc.Pop();
            Label(dc, ShowCorridor ? "ALLEGRO IMAGE + MEASURED HIGHLIGHTS" : "CAPTURED ALLEGRO VIEW · RAW PIXELS", 12, 12, 9, "#9AAFC6");
            Label(dc, $"Selected finding layer: {capture.Layer} · captured {capture.CapturedAt.LocalDateTime:HH:mm:ss}",
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
        _screenCenter = new Point(ActualWidth / 2, ActualHeight / 2);
        _scale = Math.Min((ActualWidth - 88) / Math.Max(1, bounds.Width),
            (ActualHeight - 108) / Math.Max(1, bounds.Height)) * _zoom;
        Point Screen(Point point) => Project(new DpViaCorridorPoint(point.X, point.Y));
        var p = Project(finding.P);
        var n = Project(finding.N);
        // Only the explicitly labeled illustration may contain schematic
        // traces. Native captures supply centers and bounds, not copper paths.
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
            var upper = corners.Select(Screen).Min(point => point.Y);
            Label(dc, "DP CORRIDOR", Math.Max(12, ActualWidth / 2 - 42),
                Math.Max(38, upper - 27), 10, "#75CBFF");
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
            var callout = new Point(Math.Clamp(crossing.X + 22, 12, Math.Max(12, ActualWidth - 120)),
                Math.Clamp(crossing.Y + 30, 52, Math.Max(52, ActualHeight - 86)));
            dc.DrawLine(new Pen(Brush(risk), 1), crossing + new Vector(7, 7), callout);
            dc.DrawRoundedRectangle(Brush("#442C33"), new Pen(Brush(risk), 1),
                new Rect(callout, new Size(108, 23)), 4, 4);
            Label(dc, illustration ? "Example foreign net" : "Captured intrusion", callout.X + 7, callout.Y + 5, 9, risk);
        }
        Label(dc, illustration ? "Example foreign conductor" : finding.Intrusion is null ?
            "Intrusion location not supplied" : "× Captured intrusion location", 12, ActualHeight - 40, 10, "#BECEDE");
        Label(dc, illustration ? "Via centers and protected corridor" :
            "Via centers + bounds · not copper outlines", 12, ActualHeight - 24, 9, "#91A8C0");
    }

    private static void DrawNativeAnnotations(DrawingContext dc, DpViaCorridorNativeCapture capture,
        DpViaCorridorFinding finding, Rect image, Rect visible, double styleScale, double pixelsPerDip)
    {
        var points = DpViaCorridorGeometry.CorridorCorners(finding).Concat(new[] { finding.P, finding.N });
        if (finding.Intrusion is { } intrusion)
        {
            points = points.Append(intrusion);
        }

        DrawAnnotations(dc, finding, points.Select(point => ProjectImage(point, capture, image)).ToArray(),
            visible, styleScale, pixelsPerDip);
    }

    // Shared by the captured preview, PNG exports and the owned Allegro overlay.
    // Projection belongs to each surface: bitmap bounds here, SDK live-view
    // projection for the native window. This renderer only draws those points.
    internal static void DrawAnnotations(DrawingContext dc, DpViaCorridorFinding finding,
        IReadOnlyList<Point> points, Rect visible, double styleScale, double pixelsPerDip)
    {
        if (points.Count != (finding.Intrusion is null ? 6 : 7) ||
            points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            throw new ArgumentException("Every annotation must have a finite projected position.", nameof(points));
        }
        // These are measured analysis annotations. Never infer trace paths or
        // pad diameters from the sparse finding record or recolor raw copper.
        var corners = points.Take(4).ToArray();
        var p = points[4];
        var n = points[5];
        var polygon = new StreamGeometry();
        using (var geometry = polygon.Open())
        {
            geometry.BeginFigure(corners[0], true, true);
            geometry.PolyLineTo(corners.Skip(1).ToArray(), true, false);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(46, 34, 139, 255)),
            new Pen(Brush("#071522"), 3.5 * styleScale), polygon);
        dc.DrawGeometry(null, new Pen(Brush("#58BFFF"), 1.6 * styleScale)
        {
            DashStyle = DashStyles.Dash
        }, polygon);
        dc.DrawLine(new Pen(Brush("#79C9FF"), styleScale) { DashStyle = DashStyles.Dot }, p, n);

        var caption = Text("DP CORRIDOR", 9, "#8AD7FF");
        var top = new Point(corners.Min(point => point.X) - caption.Width - 16 * styleScale,
            corners.Min(point => point.Y) + 5 * styleScale);
        DrawBadge(caption, KeepInside(top, caption.Width + 10 * styleScale,
            caption.Height + 6 * styleScale), "#102A43", "#58BFFF");

        DrawCenter(p, n, "P");
        DrawCenter(n, p, "N");
        if (finding.Intrusion is null)
        {
            return;
        }

        var crossing = points[6];
        if (!visible.Contains(crossing))
        {
            return; // No clamping a board position onto the frame.
        }

        var amber = Brush("#FFD16A");
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(45, 255, 190, 62)), null,
            crossing, 14 * styleScale, 14 * styleScale);
        dc.DrawEllipse(null, new Pen(Brush("#15110B"), 5 * styleScale), crossing, 7 * styleScale, 7 * styleScale);
        dc.DrawEllipse(null, new Pen(amber, 2 * styleScale), crossing, 7 * styleScale, 7 * styleScale);
        dc.DrawLine(new Pen(amber, 2 * styleScale), crossing + new Vector(-3, -3) * styleScale,
            crossing + new Vector(3, 3) * styleScale);
        dc.DrawLine(new Pen(amber, 2 * styleScale), crossing + new Vector(-3, 3) * styleScale,
            crossing + new Vector(3, -3) * styleScale);
        var net = Text("INTERFERING NET\n" + finding.AggressorNet, 9, "#FFE0A0");
        net.MaxTextWidth = Math.Max(1, Math.Min(236 * styleScale, visible.Width - 26 * styleScale));
        net.MaxLineCount = 3;
        net.Trimming = TextTrimming.CharacterEllipsis;
        var label = KeepInside(new Point(visible.Right - net.Width - 18 * styleScale,
            visible.Bottom - net.Height - 14 * styleScale), net.Width + 10 * styleScale,
            net.Height + 6 * styleScale);
        var target = new Point(Math.Clamp(crossing.X, label.X, label.X + net.Width + 10 * styleScale), label.Y);
        // A callout identifies the marker; it must not paint over the X that
        // locates the reported intrusion. Start outside its seven-DIP ring.
        var leader = target - crossing;
        if (leader.Length > 9 * styleScale)
        {
            leader.Normalize();
            var start = crossing + leader * (9 * styleScale);
            dc.DrawLine(new Pen(Brush("#15110B"), 3 * styleScale), start, target);
            dc.DrawLine(new Pen(amber, styleScale), start, target);
        }
        DrawBadge(net, label, "#342815", "#FFD16A");

        FormattedText Text(string text, double size, string color) => new(text,
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"),
            size * styleScale, Brush(color), pixelsPerDip);

        Point KeepInside(Point origin, double width, double height) => new(
            Math.Clamp(origin.X, visible.Left + 4 * styleScale,
                Math.Max(visible.Left + 4 * styleScale, visible.Right - width - 4 * styleScale)),
            Math.Clamp(origin.Y, visible.Top + 4 * styleScale,
                Math.Max(visible.Top + 4 * styleScale, visible.Bottom - height - 4 * styleScale)));

        void DrawBadge(FormattedText text, Point origin, string background, string border)
        {
            dc.DrawRoundedRectangle(Brush(background), new Pen(Brush(border), styleScale),
                new Rect(origin, new Size(text.Width + 10 * styleScale, text.Height + 6 * styleScale)),
                3 * styleScale, 3 * styleScale);
            dc.DrawText(text, origin + new Vector(5, 3) * styleScale);
        }

        void DrawCenter(Point point, Point other, string name)
        {
            if (!visible.Contains(point))
            {
                return;
            }

            dc.DrawEllipse(null, new Pen(Brush("#071522"), 5 * styleScale), point, 6 * styleScale, 6 * styleScale);
            dc.DrawEllipse(null, new Pen(Brush("#61C6FF"), 2 * styleScale), point, 6 * styleScale, 6 * styleScale);
            dc.DrawLine(new Pen(Brush("#BDEAFF"), styleScale), point + new Vector(-3, 0) * styleScale,
                point + new Vector(3, 0) * styleScale);
            dc.DrawLine(new Pen(Brush("#BDEAFF"), styleScale), point + new Vector(0, -3) * styleScale,
                point + new Vector(0, 3) * styleScale);
            var text = Text(name, 10, "#BDEAFF");
            var away = point - other;
            away.Normalize();
            var center = point + away * (19 * styleScale);
            DrawBadge(text, KeepInside(new Point(center.X - (text.Width + 10 * styleScale) / 2,
                center.Y - (text.Height + 6 * styleScale) / 2), text.Width + 10 * styleScale,
                text.Height + 6 * styleScale), "#102A43", "#58BFFF");
        }
    }

    private void DrawVia(DrawingContext dc, Point point, string name)
    {
        // Fixed-size glyphs identify centers; these are not measured pad radii.
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(35, 46, 152, 255)), null, point, 18, 18);
        dc.DrawEllipse(new LinearGradientBrush(Color.FromRgb(154, 203, 232),
            Color.FromRgb(37, 82, 108), 90), new Pen(Brush("#49B6FF"), 2), point, 13, 13);
        dc.DrawEllipse(Brush("#0B1924"), new Pen(Brush("#B1D2DE"), 1.4), point, 8, 8);
        Label(dc, name, point.X + 18, point.Y - 8, 11, "#78CAFF");
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
