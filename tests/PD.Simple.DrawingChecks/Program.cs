using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
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
    private static int Main(string[] args)
    {
        try
        {
            CheckAnalysisPublicationIdentity();
            CheckCaptureResourceDiagnostics();
            CheckReviewWorkflowIdentity();
            CheckCanonicalCorridorDrawing(includeIntrusion: false);
            CheckCanonicalCorridorDrawing(includeIntrusion: true);
            CheckOneDrawingSourceAcrossSurfaces();
            CheckDrawingPolicyCannotMutateEngine();
            CheckSameSessionPresentationOwnership();
            if (args.Contains("--screenshot", StringComparer.Ordinal))
            {
                CheckWindowScreenshot();
            }
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

    private static void CheckCaptureResourceDiagnostics()
    {
        var timings = new DpViaCorridorTimings(
            AcquisitionMilliseconds: 10,
            AnalysisMilliseconds: 2,
            ReportMilliseconds: 1,
            NativeResources:
            [
                new("objects", 2_000_000, 6_956, false, true),
                new("object_visits", 10_000_000, 8_044, false, true),
                new("pad_queries", 3_840_000_000, 91_200, false, true),
                new("pages", 32_768, 37, false, true),
                new("spool_bytes", 1_100_000_000, 5_500_000, false, true),
                new("trace_objects", 2_000_000, 5_111, false, true),
                new("via_objects", 2_000_000, 1_700, false, true),
                new("shape_objects", 2_000_000, 145, false, true),
                new("discarded_scalar_surface_expansions", 1_920_000_000, 48_000, false, true),
            ]);
        string diagnostics =
            DpViaCorridorWorkspaceViewModel.CaptureResourceBreakdown(timings);
        const string expected =
            " Capture resources: objects 6956/2000000; object visits 8044/10000000; " +
            "pad queries 91200/3840000000; pages 37/32768; spool bytes " +
            "5500000/1100000000; traces 5111; vias 1700; shapes 145; " +
            "scalar surfaces skipped 48000.";
        if (!string.Equals(diagnostics, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Validated native capture resources were not retained in the operator diagnostics.");
        }
    }

    private static void CheckWindowScreenshot()
    {
        string screenshotRoot = Path.Combine(
            Path.GetTempPath(),
            "pd-simple-screenshot-check-" + Guid.NewGuid().ToString("N"));
        string? previousDirectory = Environment.GetEnvironmentVariable(
            WindowScreenshot.OutputDirectoryVariable);
        bool ownsApplication = Application.Current is null;
        App application = Application.Current as App ?? new App();
        if (ownsApplication)
        {
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
        var window = new MainWindow([]);

        try
        {
            Environment.SetEnvironmentVariable(
                WindowScreenshot.OutputDirectoryVariable,
                screenshotRoot);
            window.Show();
            window.Dispatcher.Invoke(
                static () => { },
                DispatcherPriority.ApplicationIdle);

            Button button = window.ScreenshotButton;
            if (!button.IsEnabled ||
                AutomationProperties.GetName(button) !=
                    WindowScreenshot.ButtonAutomationName)
            {
                throw new InvalidOperationException(
                    "The PD Simple header screenshot button is missing or unavailable.");
            }

            Clipboard.Clear();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Dispatcher.Invoke(
                static () => { },
                DispatcherPriority.ApplicationIdle);

            string clipboardPath = Clipboard.GetText();
            string path = Directory.EnumerateFiles(
                    screenshotRoot,
                    "*.png",
                    SearchOption.TopDirectoryOnly)
                .Single();
            IReadOnlyList<string> clipboardFiles =
                Clipboard.GetFileDropList().Cast<string>().ToArray();
            if (!File.Exists(path) ||
                !string.Equals(path, clipboardPath, StringComparison.OrdinalIgnoreCase) ||
                !clipboardFiles.Contains(path, StringComparer.OrdinalIgnoreCase) ||
                !string.Equals(
                    Path.GetDirectoryName(path),
                    screenshotRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The screenshot was not saved and copied as path and file payload.");
            }

            using FileStream stream = File.OpenRead(path);
            var decoder = new PngBitmapDecoder(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames.Single();
            DpiScale dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            int expectedWidth = (int)Math.Ceiling(
                window.ActualWidth * dpi.DpiScaleX);
            int expectedHeight = (int)Math.Ceiling(
                window.ActualHeight * dpi.DpiScaleY);
            if (frame.PixelWidth != expectedWidth ||
                frame.PixelHeight != expectedHeight)
            {
                throw new InvalidOperationException(
                    "The screenshot does not cover the complete window at its current DPI.");
            }
            if (!window.StatusText.Text.Contains(
                    Path.GetFileName(path),
                    StringComparison.Ordinal) ||
                button.ToolTip is not string tooltip ||
                !tooltip.Contains(path, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The screenshot button did not report the saved file to the operator.");
            }

            Console.WriteLine(
                "PASS: PD Simple screenshot saved a full-window PNG and copied " +
                "its path and file payload to the Windows clipboard.");
        }
        finally
        {
            window.Close();
            DateTime closeDeadline = DateTime.UtcNow.AddSeconds(5);
            while (window.IsVisible && DateTime.UtcNow < closeDeadline)
            {
                window.Dispatcher.Invoke(
                    static () => { },
                    DispatcherPriority.ApplicationIdle);
                Thread.Sleep(10);
            }
            if (ownsApplication)
            {
                application.Shutdown();
            }
            Environment.SetEnvironmentVariable(
                WindowScreenshot.OutputDirectoryVariable,
                previousDirectory);
            if (Directory.Exists(screenshotRoot))
            {
                Directory.Delete(screenshotRoot, recursive: true);
            }
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

    private static void CheckReviewWorkflowIdentity()
    {
        const string nativeDesign =
            @"C:\disposable\ingram9z-preview27-display-disposable.brd";
        var document = new WorkspaceDocumentIdentity(
            "review-session",
            SessionGeneration: 4,
            BoardGeneration: 18,
            ProcessId: 1234,
            Design: nativeDesign,
            ProtocolVersion: "25");
        Guid captureId = Guid.NewGuid();
        var bounds = new DpViaCorridorBounds(10, 20, 30, 40);
        var zoom = new DpViaCorridorZoomResult(
            DpViaCorridorZoomResult.CurrentSchema,
            "complete",
            document.BoardGeneration,
            nativeDesign,
            "unused.rpt",
            "crossing-2",
            "ETCH/S03",
            "mils",
            bounds,
            bounds);

        DpViaCorridorReviewCapture.RequireWorkflowIdentity(
            document,
            captureId,
            document,
            captureId,
            captureId,
            zoom);

        Reject<InvalidOperationException>(
            () => DpViaCorridorReviewCapture.RequireWorkflowIdentity(
                document with { ProcessId = 4321 },
                captureId,
                document,
                captureId,
                captureId,
                zoom),
            "A captured review from another verified process was admitted.");
        Reject<InvalidOperationException>(
            () => DpViaCorridorReviewCapture.RequireWorkflowIdentity(
                document,
                captureId,
                document,
                captureId,
                captureId,
                zoom with { Design = "another-board.brd" }),
            "A zoom result from another native design was admitted.");
        Reject<InvalidOperationException>(
            () => DpViaCorridorReviewCapture.RequireWorkflowIdentity(
                document,
                Guid.NewGuid(),
                document,
                captureId,
                captureId,
                zoom),
            "Captured pixels from another Engine scene were admitted.");
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
        double StrokeWidth(string id) =>
            drawing.Elements.Single(item => item.Id == id).Style.Stroke?.Width is
                ScreenStrokeWidth screen
                ? screen.Value.Value
                : throw new InvalidOperationException(
                    $"Drawing element {id} does not use a physical-pixel stroke.");
        if (outlines.Length != 2 ||
            outlines.Any(static item => !item.Closed || item.Points.Length != 4) ||
            axis.Start != new LocalPoint(100.Mils(), 100.Mils()) ||
            axis.End != new LocalPoint(140.Mils(), 100.Mils()) ||
            markers.Length != (includeIntrusion ? 3 : 2) ||
            labels.Length != (includeIntrusion ? 4 : 3) ||
            labels.Any(static item => item.FontSize.Value != 24) ||
            StrokeWidth("corridor-shadow") != 40 ||
            StrokeWidth("corridor-outline") != 20 ||
            StrokeWidth("pair-axis") != 10 ||
            StrokeWidth("p-center") != 20 ||
            StrokeWidth("p-label") != 10 ||
            StrokeWidth("n-center") != 20 ||
            StrokeWidth("n-label") != 10 ||
            StrokeWidth("corridor-label") != 10 ||
            (includeIntrusion &&
                (StrokeWidth("intrusion") != 20 ||
                 StrokeWidth("intrusion-label") != 10)) ||
            labels.Any(static item =>
                item.OrientationPolicy !=
                    DrawingTextOrientationPolicy.ScreenUpright) ||
            includeIntrusion !=
                drawing.Elements.Any(static item => item.Id == "intrusion"))
        {
            throw new InvalidOperationException(
                "Corridor semantics or requested physical-pixel styling were not retained " +
                "in the canonical Engine model.");
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
