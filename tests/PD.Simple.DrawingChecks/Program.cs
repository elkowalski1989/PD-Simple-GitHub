using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Windows;
using PD.Simple;
using PD.Simple.Corridor;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            CheckAnalysisPublicationIdentity();
            CheckCapturedViewportAdmission();
            CheckCanonicalCorridorDrawing(includeIntrusion: false);
            CheckCanonicalCorridorDrawing(includeIntrusion: true);
            Console.WriteLine(
                "PASS: PD corridor policy publishes canonical Engine drawing intent; shared WPF owns captured/live projection and clipping.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception.Message}");
            return 1;
        }
    }

    private static void CheckCapturedViewportAdmission()
    {
        var bounds = new DpViaCorridorBounds(-10, -20, 100, 200);
        if (!DpViaCorridorNativeCapture.Matches(new(-10, -20, 100, 200, "mils", 1), bounds) ||
            !DpViaCorridorNativeCapture.Matches(new(-0.254m, -0.508m, 2.54m, 5.08m, "millimeters", 1), bounds) ||
            DpViaCorridorNativeCapture.Matches(new(-9, -20, 100, 200, "mils", 1), bounds) ||
            DpViaCorridorNativeCapture.Matches(new(-10, -20, 100, 200, "unknown", 1), bounds))
        {
            throw new InvalidOperationException(
                "Scoped capture lost exact serialized-viewport or unit admission.");
        }
    }

    private static void CheckAnalysisPublicationIdentity()
    {
        foreach (long generation in new long[] { 18, 739 })
        {
            string session = "publication-session-" + generation;
            string design = "changed-design-" + generation;
            var document = new WorkspaceDocumentIdentity(
                session,
                SessionGeneration: 1,
                BoardGeneration: generation,
                ProcessId: null,
                Design: design,
                ProtocolVersion: "25");
            var result = new DpViaCorridorResult(
                DpViaCorridorResult.CurrentSchema,
                "complete",
                generation,
                design,
                "mils",
                "mils",
                "unused.rpt",
                null,
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                false,
                []);
            var analysis = new DpViaCorridorAnalysis(document, result, 7);
            if (!analysis.IsCurrentFor(session, generation, 7) ||
                analysis.IsCurrentFor(session, generation, 8) ||
                analysis.IsCurrentFor(session, generation + 1, 7) ||
                analysis.IsCurrentFor(session + "-replacement", generation, 7))
            {
                throw new InvalidOperationException(
                    "Historical publication identity was admitted as current.");
            }
        }
    }

    private static void CheckCanonicalCorridorDrawing(bool includeIntrusion)
    {
        DesignScene scene = EngineExamples.CreateBoard("PD corridor drawing policy");
        var finding = new DpViaCorridorFinding(
            "finding-" + includeIntrusion,
            "PAIR_A",
            "AGGRESSOR_A",
            "trace",
            "ETCH/INNER1",
            "Signal",
            includeIntrusion ? "CRITICAL" : "LOW",
            new(100, 100),
            new(140, 100),
            includeIntrusion ? new(120, 100) : null,
            0,
            12,
            30);

        DrawingGroup drawing = BoardOverlayDrawingPolicy.Corridor(scene, finding);
        int expectedElements = includeIntrusion ? 10 : 8;
        if (drawing.Binding.Kind != DrawingBindingKind.ExplicitBoardPoint ||
            drawing.Frame.Origin != BoardPoint.Zero ||
            drawing.Elements.Length != expectedElements ||
            drawing.Elements.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != expectedElements)
        {
            throw new InvalidOperationException(
                "Corridor drawing lost its board-space binding or bounded element identity.");
        }

        DrawingPolyline[] outlines = drawing.Elements.OfType<DrawingPolyline>().ToArray();
        DrawingLine axis = drawing.Elements.OfType<DrawingLine>().Single();
        DrawingMarker[] markers = drawing.Elements.OfType<DrawingMarker>().ToArray();
        DrawingText[] labels = drawing.Elements.OfType<DrawingText>().ToArray();
        if (outlines.Length != 2 || outlines.Any(item => !item.Closed || item.Points.Length != 4) ||
            axis.Start != new LocalPoint(100.Mils(), 100.Mils()) ||
            axis.End != new LocalPoint(140.Mils(), 100.Mils()) ||
            markers.Length != (includeIntrusion ? 3 : 2) ||
            labels.Length != (includeIntrusion ? 4 : 3) ||
            labels.Any(item => item.OrientationPolicy != DrawingTextOrientationPolicy.ScreenUpright) ||
            includeIntrusion != drawing.Elements.Any(item => item.Id == "intrusion"))
        {
            throw new InvalidOperationException(
                "Corridor semantics were not retained in the canonical Engine model.");
        }

        BoardDrawingGroup projected = drawing.ProjectToBoard();
        if (projected.Elements.Length != drawing.Elements.Length ||
            projected.Target is not null ||
            projected.Identity.CaptureId != scene.Identity.CaptureId)
        {
            throw new InvalidOperationException(
                "Canonical board projection changed the corridor identity or element count.");
        }
    }
}
