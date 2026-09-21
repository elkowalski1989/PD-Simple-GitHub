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
    private static readonly PhysicalPixels ShadowStroke = new(20);
    private static readonly PhysicalPixels PrimaryStroke = new(10);
    private static readonly PhysicalPixels DetailStroke = new(5);
    private static readonly PhysicalPixels FindingStroke = new(20);
    private static readonly PhysicalPixels CenterMarkerSize = new(24);
    private static readonly PhysicalPixels IntrusionMarkerSize = new(28);
    private static readonly PhysicalPixels OverlayTextSize = new(24);

    internal static DrawingScene CorridorScene(
        DesignScene scene,
        DpViaCorridorFinding finding,
        long revision)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        DrawingGroup group = Corridor(scene, finding);
        return new DrawingScene(scene.Identity.CaptureId, revision, [group]);
    }

    internal static DpViaCorridorDrawingSource CorridorSource(
        DesignScene scene,
        DpViaCorridorFinding finding,
        long revision) =>
        new(scene, CorridorScene(scene, finding, revision));

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
                .Stroke(OutlineShadow, ShadowStroke)
            .Polyline(points.Select(Local), closed: true)
                .ElementId("corridor-outline")
                .Stroke(CorridorBlue, PrimaryStroke)
            .Line(Local(finding.P), Local(finding.N))
                .ElementId("pair-axis")
                .Stroke(CenterBlue, DetailStroke)
            .Marker(Local(finding.P), DrawingMarkerKind.Cross, CenterMarkerSize)
                .ElementId("p-center")
                .Stroke(CenterBlue, PrimaryStroke)
            .Text(Local(finding.P), "P", OverlayTextSize,
                new(new PhysicalPixels(16), new PhysicalPixels(-30)))
                .ElementId("p-label")
                .Stroke(CenterBlue, DetailStroke)
            .Marker(Local(finding.N), DrawingMarkerKind.Cross, CenterMarkerSize)
                .ElementId("n-center")
                .Stroke(CenterBlue, PrimaryStroke)
            .Text(Local(finding.N), "N", OverlayTextSize,
                new(new PhysicalPixels(16), new PhysicalPixels(-30)))
                .ElementId("n-label")
                .Stroke(CenterBlue, DetailStroke)
            .Text(Local(points[0]), "DP CORRIDOR", OverlayTextSize,
                new(new PhysicalPixels(12), new PhysicalPixels(-56)))
                .ElementId("corridor-label")
                .Stroke(CorridorBlue, DetailStroke);

        if (finding.Intrusion is { } intrusion)
        {
            builder
                .Marker(Local(intrusion), DrawingMarkerKind.Cross, IntrusionMarkerSize)
                    .ElementId("intrusion")
                    .Stroke(FindingAmber, FindingStroke)
                .Text(Local(intrusion), "INTERFERING NET\n" + finding.AggressorNet,
                    OverlayTextSize,
                    new(new PhysicalPixels(18), new PhysicalPixels(30)))
                    .ElementId("intrusion-label")
                    .Stroke(FindingAmber, DetailStroke);
        }

        return builder.Build();
    }
}

/// <summary>
/// Owns the one canonical drawing object used by both WPF review composition
/// and live presentation for a selected captured scene.
/// </summary>
internal sealed class DpViaCorridorDrawingSource
{
    private readonly DesignScene _scene;

    internal DpViaCorridorDrawingSource(
        DesignScene scene,
        DrawingScene drawings)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        Drawings = drawings ?? throw new ArgumentNullException(nameof(drawings));
        if (drawings.CaptureId != scene.Identity.CaptureId)
        {
            throw new ArgumentException(
                "The corridor drawing belongs to another captured scene.",
                nameof(drawings));
        }
    }

    internal DrawingScene Drawings { get; }

    internal DrawingScene ForReview(DesignScene scene) =>
        RequireScene(scene);

    internal DrawingScene ForLive(DesignScene scene) =>
        RequireScene(scene);

    private DrawingScene RequireScene(DesignScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!ReferenceEquals(scene, _scene) ||
            scene.Identity.CaptureId != Drawings.CaptureId)
        {
            throw new InvalidOperationException(
                "The corridor drawing cannot cross captured Engine scenes.");
        }
        return Drawings;
    }
}
