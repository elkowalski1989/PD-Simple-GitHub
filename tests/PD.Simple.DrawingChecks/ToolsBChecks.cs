using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Interactions;
using CircuitHub.AllegroBridge.Engine.Reviews;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Engine.Tools;
using CircuitHub.AllegroBridge.Wpf.Engine;
using EngineDrawingGroup = CircuitHub.AllegroBridge.Engine.Drawing.DrawingGroup;
using PD.PcbTools.OverlayTools;
using PD.PcbTools.Review;
using PD.Simple;
using PD.Simple.Tools.Overlay;
using PD.Simple.Tools.Review;

/// <summary>
/// Lane B Windows checks (STA, no native Allegro): both tool pages open
/// disconnected with setup guidance and disabled actions; the overlay recipe
/// path performs zero native work; a saved bundle reopens offline with hashes
/// verified. Live publication/capture gates need licensed native fixtures and
/// stay NOT_EXECUTED without them.
/// </summary>
internal static class ToolsBChecks
{
    internal static void Run()
    {
        CheckOverlayDisconnectedGates();
        CheckReviewDisconnectedGates();
        CheckOverlayRecipeZeroMutation();
        CheckReviewBundleReopenOffline();
        CheckReviewDefaultPlacement();
        CheckPortableReviewConsumer();
    }

    internal static void CheckPortableReviewConsumer()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pd-portable-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var bridge = new BridgeSession();
        EngineWpfPresentation presentation = EngineWpfPresentation.Attach(bridge.EngineSession, Dispatcher.CurrentDispatcher);
        var model = new ShareReviewToolViewModel(bridge, presentation);
        var view = new ShareReviewToolView { ViewModel = model };
        var evidence = (System.Windows.Controls.TextBlock)view.FindName("EvidenceText");
        try
        {
            DesignScene scene = EngineExamples.CreateBoard("Changed portable review subject");
            var tool = new ToolDescriptor("pd-review-fixture", "PD fixture", new(1, 0), [DocumentKind.PcbBoard], []);
            var finding = new ToolFinding("observed-gap", "Captured gap", null, []);
            var result = new ToolResult([finding], AnnotationScene.Empty(scene.Identity.CaptureId), []);
            JsonElement options = JsonSerializer.SerializeToElement(new { spacing = 10 });
            RunWithPump(() => model.PublishToolReviewAsync(scene, tool, result, options));
            if (model.Findings.Count != 1 || !model.CanExportBundle || model.CanSavePng)
            {
                throw new InvalidOperationException("A findings-only tool publication could not be exported without image pixels.");
            }
            model.SelectedFindingId = finding.Id;
            model.AddDisposition("pd:confirmed", "Fixture reviewer", "Historical observation only");
            if (model.PortableReview!.Revision != 2 || model.Dispositions.Count != 1 ||
                !evidence.Text.Contains("revision 2; 1 findings, 1 decisions.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The actual PD review summary retained its pre-decision revision or count.");
            }
            string path = Path.Combine(directory, "portable.allegroreview");
            RunWithPump(() => model.SavePortableCopyAsync(path, "PD portable fixture"));
            if (model.LastSaveResult?.Committed != true || !File.Exists(path) ||
                model.PortableReview!.Revision != 3 ||
                !evidence.Text.Contains("revision 3; 1 findings, 1 decisions.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The PD portable export did not retain its committed file result.");
            }
            model.ClearReview();
            RunWithPump(() => model.LoadBundleAsync(path));
            if (model.PortableReview is not { } reopened || reopened.Findings.Single().Id != finding.Id ||
                reopened.Dispositions.Single().StatusCode != "pd:confirmed" || model.IsLegacyReview ||
                !reopened.Captures.Single().Identity.Provenance.IsOffline)
            {
                throw new InvalidOperationException("PD offline portable reopening lost its findings, decisions or historical identity.");
            }
            model.SelectedFindingId = finding.Id;
            model.AddDisposition("pd:dismissed", "Second reviewer", "Documented local decision");
            if (!evidence.Text.Contains("revision 4; 1 findings, 2 decisions.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A reopened review decision did not refresh the actual PD summary.");
            }
            RunWithPump(model.SaveRevisionAsync);
            model.ClearReview();
            RunWithPump(() => model.LoadBundleAsync(path));
            if (model.PortableReview!.Dispositions.Length != 2 ||
                model.PortableReview.Dispositions[1].PreviousDispositionId != model.PortableReview.Dispositions[0].Id)
            {
                throw new InvalidOperationException("PD copy-on-write revision did not preserve decision history.");
            }
            var coverage = new AnalysisCoverage(tool.Id, tool.Version.ToString(), "fixture", "1", "default",
                [new("spacing", scene.Query, "distance", AnalysisCompletionState.Exhaustive, null, false, [])])
            { CaptureId = scene.Identity.CaptureId };
            var empty = new ToolResult([], AnnotationScene.Empty(scene.Identity.CaptureId), []) { Analysis = coverage };
            RunWithPump(() => model.PublishToolReviewAsync(scene, tool, empty, options));
            if (!model.SelectedFindingDetails.Contains("0 findings; coverage Exhaustive", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("PD hid available-empty analysis coverage when there was no finding to select.");
            }
            if (bridge.EngineSession.State.Operations.Length != 0 ||
                bridge.EngineSession.State.ConnectionState != EngineConnectionState.Disconnected)
            {
                throw new InvalidOperationException("Offline review publication or reopening performed native session work.");
            }
        }
        finally
        {
            RunWithPump(() => view.DisposeAsync().AsTask());
            RunWithPump(() => presentation.DisposeAsync().AsTask());
            RunWithPump(() => bridge.DisposeAsync().AsTask());
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckReviewDefaultPlacement()
    {
        DesignScene scene = EngineExamples.CreateBoard("Different annotation fixture");
        var viewport = new EngineWpfCanvasViewport(0, 0, 25.4m, 50.8m, "millimeters");
        var inputs = new ShareReviewToolViewModel.AnnotationInputs("Default title", "", "", false, "");
        var annotations = ShareReviewToolViewModel.ComposeAnnotations(scene, inputs, viewport);
        if (annotations.Items.Length != 1 ||
            annotations.Items[0].Geometry is not CircuitHub.AllegroBridge.Engine.Geometry.PointGeometry point ||
            point.Position != new DesignPoint(500m, 1000m))
        {
            throw new InvalidOperationException("Blank default coordinates did not place the title at the captured viewport center in typed units.");
        }
        try
        {
            ShareReviewToolViewModel.ValidateAnnotationInputs(scene, inputs with { X = "10" });
            throw new InvalidOperationException("Partial explicit coordinates were accepted.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("both stamp", StringComparison.Ordinal))
        {
        }
        try
        {
            ShareReviewToolViewModel.ValidateAnnotationInputs(scene, inputs with { LinkObject = true, ObjectId = "not-in-capture" });
            throw new InvalidOperationException("An absent linked object was accepted.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("must exist", StringComparison.Ordinal))
        {
        }
        var linked = inputs with { LinkObject = true, ObjectId = scene.Data.Components[0].Id.Value };
        if (ShareReviewToolViewModel.ComposeAnnotations(scene, linked, viewport).Items.Length != 2)
        {
            throw new InvalidOperationException("A real captured object could not receive a review marker.");
        }
    }

    private static void CheckOverlayDisconnectedGates()
    {
        var bridge = new BridgeSession();
        try
        {
            EngineWpfPresentation presentation = EngineWpfPresentation.Attach(
                bridge.EngineSession, Dispatcher.CurrentDispatcher);
            try
            {
                var viewModel = new LiveOverlayToolViewModel(bridge, presentation);
                try
                {
                    if (viewModel.CanAcquire || viewModel.CanBuild || viewModel.CanUseVisibleCenter ||
                        viewModel.CanPublish || viewModel.CanHide || viewModel.CanRemove ||
                        viewModel.CanCopyRecipe)
                    {
                        throw new InvalidOperationException(
                            "Overlay actions are available while disconnected.");
                    }

                    if (!viewModel.Status.Contains("offline", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The overlay tool does not explain its offline state.");
                    }

                    // Gated no-ops: none may throw or start native work while disconnected.
                    viewModel.BuildPreview();
                    viewModel.BuildPreviewAsync().GetAwaiter().GetResult();
                    viewModel.UseVisibleCanvasCenterAsync().GetAwaiter().GetResult();
                    if (viewModel.HasLiveScene || viewModel.HasBuiltDrawing)
                    {
                        throw new InvalidOperationException(
                            "Disconnected overlay calls adopted a scene or built a preview.");
                    }

                    if (!viewModel.Status.Contains("offline", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The overlay tool lost its offline explanation after gated calls.");
                    }

                    viewModel.CopyRecipe();
                    viewModel.Cancel();
                    viewModel.RequestExplorerNavigation();
                    RunWithPump(viewModel.AcquireSceneAsync);
                    viewModel.PublishAsync().GetAwaiter().GetResult();
                    viewModel.HideAsync().GetAwaiter().GetResult();
                    viewModel.RemoveAsync().GetAwaiter().GetResult();
                    if (bridge.EngineSession.State.Operations.Length != 0)
                    {
                        throw new InvalidOperationException(
                            "Disconnected overlay calls recorded Engine operations.");
                    }

                    var view = new LiveOverlayToolView { ViewModel = viewModel };
                    try
                    {
                        if (viewModel.CanAcquire)
                        {
                            throw new InvalidOperationException(
                                "Attaching the view enabled disconnected actions.");
                        }
                    }
                    finally
                    {
                        view.Dispose();
                    }
                }
                finally
                {
                    viewModel.Dispose();
                }
            }
            finally
            {
                presentation.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            if (bridge.EngineSession.State.ConnectionState == EngineConnectionState.Disposed)
            {
                throw new InvalidOperationException(
                    "Disposing the overlay tool disposed the shared Engine session.");
            }
        }
        finally
        {
            bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void CheckReviewDisconnectedGates()
    {
        var bridge = new BridgeSession();
        try
        {
            EngineWpfPresentation presentation = EngineWpfPresentation.Attach(
                bridge.EngineSession, Dispatcher.CurrentDispatcher);
            try
            {
                var viewModel = new ShareReviewToolViewModel(bridge, presentation);
                try
                {
                    if (viewModel.CanAcquire || viewModel.CanCapture || viewModel.CanSavePng ||
                        viewModel.CanExportBundle)
                    {
                        throw new InvalidOperationException(
                            "Review actions are available while disconnected.");
                    }

                    if (viewModel.DisplayedImage is not null)
                    {
                        throw new InvalidOperationException(
                            "A fresh review tool displays an image with no frame.");
                    }

                    RunWithPump(viewModel.AcquireSceneAsync);
                    RunWithPump(viewModel.CaptureAsync);
                    RunWithPump(() => viewModel.SavePngAsync(Path.Combine(Path.GetTempPath(), "no-frame.png"), true));
                    RunWithPump(() => viewModel.ExportBundleAsync(Path.GetTempPath(), "no frame"));
                    RunWithPump(() => viewModel.LoadBundleAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
                    if (!viewModel.Status.Contains("bundle", StringComparison.OrdinalIgnoreCase) &&
                        !viewModel.Status.Contains("capture", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The review tool does not explain its empty state.");
                    }

                    var view = new ShareReviewToolView { ViewModel = viewModel };
                    try
                    {
                        if (viewModel.CanCapture)
                        {
                            throw new InvalidOperationException(
                                "Attaching the view enabled disconnected capture.");
                        }
                    }
                    finally
                    {
                        view.Dispose();
                    }
                }
                finally
                {
                    viewModel.Dispose();
                }
            }
            finally
            {
                presentation.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void CheckOverlayRecipeZeroMutation()
    {
        AllegroEngineSession session = AllegroEngineSession.Create();
        try
        {
            EngineSessionSnapshot before = session.State;
            AllegroWorkspace workspace = session.Workspace;
            DesignScene scene = EngineExamples.CreateBoard("tools-b-zero-mutation");
            var recipe = new OverlayToolRecipe(
                "zero-mutation",
                new OverlayToolAnchor.Board(10, 10),
                new OverlayToolShape.Marker(0, 0, DrawingMarkerKind.Cross, 24),
                new(255, 32, 112, 220, 2));
            EngineDrawingGroup group = recipe.Build(scene, "tools-b-zero-mutation");
            _ = new DrawingScene(scene.Identity.CaptureId, 1, [group]);
            var tracker = new ToolPublicationTracker();
            Guid operation = tracker.StartOperation(1, 1);
            if (tracker.Complete(operation, 1, 1, null) != ToolPublicationOutcome.Cancelled)
            {
                throw new InvalidOperationException("A null receipt did not record cancellation.");
            }

            if (session.State != before || !ReferenceEquals(session.Workspace, workspace) ||
                session.State.Operations.Length != 0)
            {
                throw new InvalidOperationException(
                    "Building an overlay recipe changed Engine session state.");
            }
        }
        finally
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void CheckReviewBundleReopenOffline()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pd-tools-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        byte[] raw = PngBytes(240, 240, 240);
        byte[] annotated = PngBytes(32, 112, 220);
        var manifest = ReviewBundleManifest.Build(
            Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "session/1/design",
            "live-canvas", includeAnnotations: true, 2, 2, DateTimeOffset.UtcNow,
            "viewport", "qualified", null,
            [("raw", "reopen-raw.png", raw), ("annotated", "reopen-annotated.png", annotated)],
            "windows reopen check");
        File.WriteAllBytes(Path.Combine(directory, "reopen-raw.png"), raw);
        File.WriteAllBytes(Path.Combine(directory, "reopen-annotated.png"), annotated);
        string manifestPath = Path.Combine(directory, ReviewBundleManifest.ManifestFileName);
        File.WriteAllText(manifestPath, ReviewBundleManifest.Serialize(manifest));

        var bridge = new BridgeSession();
        try
        {
            EngineWpfPresentation presentation = EngineWpfPresentation.Attach(
                bridge.EngineSession, Dispatcher.CurrentDispatcher);
            try
            {
                var viewModel = new ShareReviewToolViewModel(bridge, presentation);
                try
                {
                    var list = new System.Windows.Controls.ListBox { ItemsSource = viewModel.RetainedFrames };
                    bool crossThread = false;
                    viewModel.RetainedFrames.CollectionChanged += (_, _) =>
                        crossThread |= !list.Dispatcher.CheckAccess();
                    viewModel.PropertyChanged += (_, _) =>
                        crossThread |= !list.Dispatcher.CheckAccess();
                    RunWithPump(() => viewModel.LoadBundleAsync(manifestPath));
                    if (crossThread)
                    {
                        throw new InvalidOperationException("Review adoption notified a bound control off its Dispatcher.");
                    }
                    if (viewModel.DisplayedImage is null)
                    {
                        throw new InvalidOperationException(
                            "The reopened bundle displays no image.");
                    }

                    if (!viewModel.Status.Contains("offline", StringComparison.OrdinalIgnoreCase) ||
                        !viewModel.Evidence.Contains(manifest.BundleId.ToString(), StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The reopened bundle is not attributed as offline historical evidence.");
                    }

                    if (viewModel.RetainedFrames.Count != 1)
                    {
                        throw new InvalidOperationException(
                            "The reopened bundle was not retained.");
                    }

                    var view = new ShareReviewToolView { ViewModel = viewModel };
                    var annotatedImage = viewModel.DisplayedImage;
                    var rawRadio = (System.Windows.Controls.RadioButton)view.FindName("RawRadio");
                    rawRadio.IsChecked = true;
                    if (viewModel.ShowAnnotated || ReferenceEquals(viewModel.DisplayedImage, annotatedImage) ||
                        viewModel.DisplayedImage is not BitmapSource rawImage)
                    {
                        throw new InvalidOperationException("The real Raw toggle did not select the independent raw variant.");
                    }
                    byte[] firstPixel = new byte[16];
                    rawImage.CopyPixels(firstPixel, 8, 0);
                    if (firstPixel[0] != 240)
                    {
                        throw new InvalidOperationException("The Raw variant contains annotated pixels.");
                    }
                    File.WriteAllBytes(Path.Combine(directory, "reopen-raw.png"), [1, 2, 3]);
                    var previousImage = viewModel.DisplayedImage;
                    RunWithPump(() => viewModel.LoadBundleAsync(manifestPath));
                    if (!ReferenceEquals(previousImage, viewModel.DisplayedImage) || viewModel.RetainedFrames.Count != 1)
                    {
                        throw new InvalidOperationException("A corrupt import replaced the previous review.");
                    }
                    string[] unrelated = ["changed-name.brd", "other.cs", "notes.txt", "unlinked.png"];
                    foreach (string name in unrelated)
                    {
                        File.WriteAllText(Path.Combine(directory, name), "keep");
                    }
                    viewModel.RemoveRetained(viewModel.RetainedFrames[0]);
                    if (!File.Exists(manifestPath) || viewModel.RetainedFrames.Count != 0 ||
                        unrelated.Any(name => File.ReadAllText(Path.Combine(directory, name)) != "keep"))
                    {
                        throw new InvalidOperationException(
                            "Removing imported recent evidence changed files or left its recent record.");
                    }
                }
                finally
                {
                    viewModel.Dispose();
                }
            }
            finally
            {
                presentation.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            bridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static byte[] PngBytes(byte r, byte g, byte b)
    {
        var bitmap = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[16];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, 2, 2), pixels, 8, 0);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Runs dispatcher-marshalled tool work from the STA harness thread by
    /// pumping a nested frame until the task completes.
    /// </summary>
    private static void RunWithPump(Func<Task> action)
    {
        // Enter through the Dispatcher so async view continuations capture
        // the same WPF context as an actual button or window-close callback.
        Task task = Dispatcher.CurrentDispatcher.InvokeAsync(action).Task.Unwrap();
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}
