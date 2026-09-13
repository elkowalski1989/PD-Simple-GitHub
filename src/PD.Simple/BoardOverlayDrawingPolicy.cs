using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.Simple.Corridor;

namespace PD.Simple;

/// <summary>
/// PD corridor semantics expressed as canonical Engine drawing intent. Projection,
/// clipping, captured-view rendering, and live-overlay lifetime remain platform-owned.
/// </summary>
internal static class BoardOverlayDrawingPolicy
{
    private static readonly DrawingColor OutlineShadow = new(255, 7, 21, 34);
    private static readonly DrawingColor CorridorBlue = new(255, 88, 191, 255);
    private static readonly DrawingColor CenterBlue = new(255, 121, 201, 255);
    private static readonly DrawingColor FindingAmber = new(255, 255, 190, 96);

    internal static DrawingGroup Corridor(DesignScene scene, DpViaCorridorFinding finding)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(finding);
        IReadOnlyList<DpViaCorridorPoint> points = DpViaCorridorGeometry.CorridorCorners(finding);
        if (points.Count != 4)
        {
            throw new ArgumentException("A corridor needs four measured corners.", nameof(finding));
        }

        LocalPoint Local(DpViaCorridorPoint point) => new(point.XMil.Mils(), point.YMil.Mils());

        var builder = new DrawingFactory(scene)
            .At(BoardPoint.Zero)
            .Named("pd-corridor-" + finding.Id)
            .GroupZOrder(100)
            .Polyline(points.Select(Local), closed: true)
                .ElementId("corridor-shadow")
                .Stroke(OutlineShadow, new PhysicalPixels(4))
            .Polyline(points.Select(Local), closed: true)
                .ElementId("corridor-outline")
                .Stroke(CorridorBlue, new PhysicalPixels(2))
            .Line(Local(finding.P), Local(finding.N))
                .ElementId("pair-axis")
                .Stroke(CenterBlue, new PhysicalPixels(1))
            .Marker(Local(finding.P), DrawingMarkerKind.Cross, new PhysicalPixels(12))
                .ElementId("p-center")
                .Stroke(CenterBlue, new PhysicalPixels(2))
            .Text(Local(finding.P), "P", new PhysicalPixels(12),
                new(new PhysicalPixels(10), new PhysicalPixels(-18)))
                .ElementId("p-label")
                .Stroke(CenterBlue, new PhysicalPixels(1))
            .Marker(Local(finding.N), DrawingMarkerKind.Cross, new PhysicalPixels(12))
                .ElementId("n-center")
                .Stroke(CenterBlue, new PhysicalPixels(2))
            .Text(Local(finding.N), "N", new PhysicalPixels(12),
                new(new PhysicalPixels(10), new PhysicalPixels(-18)))
                .ElementId("n-label")
                .Stroke(CenterBlue, new PhysicalPixels(1))
            .Text(Local(points[0]), "DP CORRIDOR", new PhysicalPixels(12),
                new(new PhysicalPixels(8), new PhysicalPixels(8)))
                .ElementId("corridor-label")
                .Stroke(CorridorBlue, new PhysicalPixels(1));

        if (finding.Intrusion is { } intrusion)
        {
            builder
                .Marker(Local(intrusion), DrawingMarkerKind.Cross, new PhysicalPixels(14))
                    .ElementId("intrusion")
                    .Stroke(FindingAmber, new PhysicalPixels(2))
                .Text(Local(intrusion), "INTERFERING NET\n" + finding.AggressorNet,
                    new PhysicalPixels(12),
                    new(new PhysicalPixels(12), new PhysicalPixels(12)))
                    .ElementId("intrusion-label")
                    .Stroke(FindingAmber, new PhysicalPixels(1));
        }

        return builder.Build();
    }
}
