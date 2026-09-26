using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
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
                    viewModel.AcquireSceneAsync().GetAwaiter().GetResult();
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

                    viewModel.AcquireSceneAsync().GetAwaiter().GetResult();
                    viewModel.CaptureAsync().GetAwaiter().GetResult();
                    viewModel.SavePngAsync(Path.Combine(Path.GetTempPath(), "no-frame.png"), true)
                        .GetAwaiter().GetResult();
                    viewModel.ExportBundleAsync(Path.GetTempPath(), "no frame").GetAwaiter().GetResult();
                    viewModel.LoadBundleAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"))
                        .GetAwaiter().GetResult();
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
                    RunWithPump(() => viewModel.LoadBundleAsync(manifestPath));
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

                    viewModel.RemoveRetained(viewModel.RetainedFrames[0]);
                    if (File.Exists(manifestPath) || viewModel.RetainedFrames.Count != 0)
                    {
                        throw new InvalidOperationException(
                            "Removing the retained bundle left files or records behind.");
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
        Task task = action();
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
