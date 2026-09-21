using System.Windows;
using System.Windows.Media;

namespace PD.Simple.Corridor;

public sealed class DpViaCorridorLayerTile : FrameworkElement
{
    public static readonly DependencyProperty FindingsProperty = DependencyProperty.Register(
        nameof(Findings),
        typeof(System.Collections.Generic.IReadOnlyList<DpViaCorridorFinding>),
        typeof(DpViaCorridorLayerTile),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public System.Collections.Generic.IReadOnlyList<DpViaCorridorFinding>? Findings
    {
        get => (System.Collections.Generic.IReadOnlyList<DpViaCorridorFinding>?)GetValue(FindingsProperty);
        set => SetValue(FindingsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        dc.DrawRectangle(
            new SolidColorBrush(Color.FromRgb(11, 22, 36)),
            null,
            new Rect(0, 0, ActualWidth, ActualHeight));
        var findings = Findings;
        if (findings is null || findings.Count == 0 || ActualWidth < 8 || ActualHeight < 8)
        {
            return;
        }

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        var polygons = new System.Collections.Generic.List<System.Collections.Generic.IReadOnlyList<DpViaCorridorPoint>>(findings.Count);
        foreach (DpViaCorridorFinding finding in findings)
        {
            var corners = DpViaCorridorGeometry.CorridorCorners(finding);
            polygons.Add(corners);
            foreach (DpViaCorridorPoint corner in corners)
            {
                minX = Math.Min(minX, corner.XMil);
                minY = Math.Min(minY, corner.YMil);
                maxX = Math.Max(maxX, corner.XMil);
                maxY = Math.Max(maxY, corner.YMil);
            }
        }
        double spanX = Math.Max(1e-6, maxX - minX);
        double spanY = Math.Max(1e-6, maxY - minY);
        double scale = Math.Min((ActualWidth - 8) / spanX, (ActualHeight - 8) / spanY);
        Point Screen(DpViaCorridorPoint point) => new(
            4 + (point.XMil - minX) * scale,
            4 + (point.YMil - minY) * scale);
        var corridorPen = new Pen(new SolidColorBrush(Color.FromRgb(100, 171, 255)), 1);
        for (int index = 0; index < findings.Count; index++)
        {
            var figure = new PathFigure(Screen(polygons[index][0]), [], true);
            for (int corner = 1; corner < polygons[index].Count; corner++)
            {
                figure.Segments.Add(new LineSegment(Screen(polygons[index][corner]), true));
            }
            dc.DrawGeometry(null, corridorPen, new PathGeometry([figure]));
            if (findings[index].Intrusion is { } intrusion)
            {
                dc.DrawEllipse(
                    new SolidColorBrush(findings[index].Risk switch
                    {
                        "CRITICAL" => Color.FromRgb(229, 72, 77),
                        "MEDIUM" => Color.FromRgb(232, 161, 61),
                        _ => Color.FromRgb(79, 179, 161),
                    }),
                    null,
                    Screen(intrusion),
                    2,
                    2);
            }
        }
    }
}
