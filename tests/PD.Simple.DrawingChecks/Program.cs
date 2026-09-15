using System.IO;
using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Wpf.Engine;
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
            CheckCanonicalCorridorDrawing(includeIntrusion: false);
            CheckCanonicalCorridorDrawing(includeIntrusion: true);
            CheckOneDrawingSourceAcrossSurfaces();
            CheckDrawingPolicyCannotMutateEngine();
            CheckSameSessionPresentationOwnership();
            Console.WriteLine(
                "PASS: PD owns corridor policy and one canonical drawing source; " +
                "Engine/WPF own same-session capture, review, live projection, and disposal.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception.Message}");
            return 1;
        }
    }

    private static void CheckAnalysisPublicationIdentity()
    {
        foreach (long generation in new long[] { 18, 739 })
        {
            string session = "publication-session-" + generation;
            string resultDesign = "changed-design-" + generation;
            string nativeDesign = Path.Combine("C:\\disposable", resultDesign + ".brd");
            var document = new WorkspaceDocumentIdentity(
                session,
                SessionGeneration: 1,
                BoardGeneration: generation,
                ProcessId: null,
                Design: nativeDesign,
                ProtocolVersion: "25");
            var result = new DpViaCorridorResult(
                DpViaCorridorResult.CurrentSchema,
                "complete",
                generation,
                resultDesign,
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
            var analysis = new DpViaCorridorAnalysis(document, result);
            if (!analysis.IsCurrentFor(document) ||
                analysis.Result.Design != resultDesign ||
                analysis.IsCurrentFor(document with
                {
                    BoardGeneration = generation + 1,
                }) ||
                analysis.IsCurrentFor(document with
                {
                    SessionId = session + "-replacement",
                }))
            {
                throw new InvalidOperationException(
                    "Historical Engine document identity was admitted as current.");
            }
        }
    }

    private static void CheckCanonicalCorridorDrawing(bool includeIntrusion)
    {
        DesignScene scene = EngineExamples.CreateBoard(
            "PD corridor drawing policy");
        DpViaCorridorFinding finding = CreateFinding(includeIntrusion);
        DpViaCorridorPoint[] geometryBefore =
            DpViaCorridorGeometry.CorridorCorners(finding).ToArray();

        DrawingScene drawings =
            BoardOverlayDrawingPolicy.CorridorScene(scene, finding, 11);
        DrawingGroup drawing = drawings.Groups.Single();

        int expectedElements = includeIntrusion ? 10 : 8;
        if (drawings.CaptureId != scene.Identity.CaptureId ||
            drawings.Revision != 11 ||
            drawing.Binding.Kind != DrawingBindingKind.ExplicitBoardPoint ||
            drawing.Frame.Origin != BoardPoint.Zero ||
            drawing.Elements.Length != expectedElements ||
            drawing.Elements
                .Select(static item => item.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != expectedElements)
        {
            throw new InvalidOperationException(
                "Corridor drawing lost capture identity, board-space binding, " +
                "or bounded element identity.");
        }

        DrawingPolyline[] outlines =
            drawing.Elements.OfType<DrawingPolyline>().ToArray();
        DrawingLine axis = drawing.Elements.OfType<DrawingLine>().Single();
        DrawingMarker[] markers =
            drawing.Elements.OfType<DrawingMarker>().ToArray();
        DrawingText[] labels =
            drawing.Elements.OfType<DrawingText>().ToArray();
        if (outlines.Length != 2 ||
            outlines.Any(static item => !item.Closed || item.Points.Length != 4) ||
            axis.Start != new LocalPoint(100.Mils(), 100.Mils()) ||
            axis.End != new LocalPoint(140.Mils(), 100.Mils()) ||
            markers.Length != (includeIntrusion ? 3 : 2) ||
            labels.Length != (includeIntrusion ? 4 : 3) ||
            labels.Any(static item =>
                item.OrientationPolicy !=
                    DrawingTextOrientationPolicy.ScreenUpright) ||
            includeIntrusion !=
                drawing.Elements.Any(static item => item.Id == "intrusion"))
        {
            throw new InvalidOperationException(
                "Corridor semantics were not retained in the canonical Engine model.");
        }

        BoardDrawingGroup projected = drawing.ProjectToBoard();
        DpViaCorridorPoint[] geometryAfter =
            DpViaCorridorGeometry.CorridorCorners(finding).ToArray();
        if (projected.Elements.Length != drawing.Elements.Length ||
            projected.Target is not null ||
            projected.Identity.CaptureId != scene.Identity.CaptureId ||
            !geometryBefore.SequenceEqual(geometryAfter))
        {
            throw new InvalidOperationException(
                "PD drawing style changed analysis geometry or capture identity.");
        }
    }

    private static void CheckOneDrawingSourceAcrossSurfaces()
    {
        DesignScene scene = EngineExamples.CreateBoard(
            "one-source-review-live");
        var source = BoardOverlayDrawingPolicy.CorridorSource(
            scene,
            CreateFinding(includeIntrusion: true),
            29);

        DrawingScene review = source.ForReview(scene);
        DrawingScene live = source.ForLive(scene);
        if (!ReferenceEquals(review, live) ||
            review.CaptureId != scene.Identity.CaptureId)
        {
            throw new InvalidOperationException(
                "Captured review and live presentation did not share one drawing object.");
        }

        DesignScene other = EngineExamples.CreateBoard(
            "changed-scene-negative-control");
        Reject<InvalidOperationException>(
            () => source.ForLive(other),
            "A corridor drawing crossed captured Engine scenes.");
    }

    private static void CheckDrawingPolicyCannotMutateEngine()
    {
        AllegroEngineSession session = AllegroEngineSession.Create();
        EngineSessionSnapshot before = session.State;
        AllegroWorkspace workspace = session.Workspace;
        DesignScene scene = EngineExamples.CreateBoard(
            "data-only-drawing-negative-control");

        _ = BoardOverlayDrawingPolicy.CorridorSource(
            scene,
            CreateFinding(includeIntrusion: true),
            31);

        if (session.State != before ||
            !ReferenceEquals(session.Workspace, workspace) ||
            session.State.Operations.Length != 0)
        {
            throw new InvalidOperationException(
                "Creating PD drawing intent changed Engine session state.");
        }
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void CheckSameSessionPresentationOwnership()
    {
        AllegroEngineSession session = AllegroEngineSession.Create();
        AllegroEngineSession other = AllegroEngineSession.Create();
        EngineWpfPresentation presentation = EngineWpfPresentation.Attach(
            session,
            Dispatcher.CurrentDispatcher);

        if (!ReferenceEquals(presentation.Session, session))
        {
            throw new InvalidOperationException(
                "WPF presentation did not retain the caller's exact Engine session.");
        }
        Reject<ArgumentException>(
            () => new BoardOverlayController(other, presentation),
            "PD admitted a presentation attached to another Engine session.");

        var controller = new BoardOverlayController(session, presentation);
        controller.Dispose();
        if (presentation.State.Availability ==
                EngineWpfPresentationAvailability.Disposed ||
            session.State.ConnectionState == EngineConnectionState.Disposed)
        {
            throw new InvalidOperationException(
                "Disposing PD overlay ownership disposed a shared facade or Engine session.");
        }

        presentation.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (presentation.State.Availability !=
                EngineWpfPresentationAvailability.Disposed ||
            session.State.ConnectionState == EngineConnectionState.Disposed)
        {
            throw new InvalidOperationException(
                "WPF disposal did not remain independent of Engine lifetime.");
        }

        other.DisposeAsync().AsTask().GetAwaiter().GetResult();
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static DpViaCorridorFinding CreateFinding(bool includeIntrusion) =>
        new(
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

    private static void Reject<TException>(
        Action action,
        string failure)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(failure);
    }
}
