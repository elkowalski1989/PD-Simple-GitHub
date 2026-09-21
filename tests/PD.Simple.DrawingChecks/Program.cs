using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Drawing;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Wpf;
using CircuitHub.AllegroBridge.Wpf.Engine;
using PD.Simple;
using PD.Simple.Corridor;
using PD.Simple.Diagnostics;

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
            CheckSelectionLatestWins();
            CheckSelectionQuietPeriod();
            CheckSelectionDrawingBeforeCapture();
            PublicationTrackerChecks.Run();
            RecoveryHandoffChecks.RunAsync().GetAwaiter().GetResult();
            CheckFollowOffPerformsZeroNativeWork();
            CheckOutcomeAttributionMatrix();
            CheckExplicitClicksWhenNotReadyAreReported();
            CheckCanonicalCorridorDrawing(includeIntrusion: false);
            CheckCanonicalCorridorDrawing(includeIntrusion: true);
            CheckOneDrawingSourceAcrossSurfaces();
            CheckDrawingPolicyCannotMutateEngine();
            CheckSameSessionPresentationOwnership();
            CheckOverlayDebugReceiptRoundTrip();
            CheckOverlayDebugTransitionOnly();
            CheckOverlayDebugRateLimit();
            CheckOverlayDebugRetention();
            CheckOverlayDebugSafeFilenames();
            CheckOverlayDebugOffByDefault();
            CheckOverlayDebugPrunesOrphanImages();
            CheckOverlayDebugPrunesRealNames();
            ToolsBChecks.Run();
            ToolViewsInstantiateChecks.Run();
            if (args.Contains("--screenshot", StringComparer.Ordinal))
            {
                // The debug image check runs first: each check shuts down the
                // application it bootstrapped, so only the first one finds a
                // fresh dispatcher.
                CheckOverlayDebugWindowImage();
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

    private static string NewDebugCheckRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "pd-simple-debug-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteDebugCheckRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static EngineWpfOverlayState DebugOverlayState(
        EngineWpfOverlayAvailability availability,
        string? reason = null,
        string[]? diagnostics = null)
    {
        return new EngineWpfOverlayState(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            7,
            availability,
            reason,
            diagnostics is null
                ? ImmutableArray<EngineDiagnostic>.Empty
                : ImmutableArray.CreateRange(
                    diagnostics.Select((message, index) =>
                        new EngineDiagnostic($"D{index}", message))),
            DateTimeOffset.UtcNow);
    }

    private static void CheckOverlayDebugReceiptRoundTrip()
    {
        string root = NewDebugCheckRoot();
        try
        {
            var capture = new OverlayDebugCapture(directoryOverride: root, autoCaptureEnabled: true);
            OverlayDebugReceipt? receipt = capture.TryCaptureAuto(
                DebugOverlayState(EngineWpfOverlayAvailability.Visible),
                "F-1",
                "(0, 0, 100, 50)");
            if (receipt is null)
            {
                throw new InvalidOperationException(
                    "The overlay debug capture skipped a first availability transition.");
            }
            if (receipt.Schema != OverlayDebugCapture.Schema ||
                receipt.Trigger != "transition" ||
                receipt.Availability != nameof(EngineWpfOverlayAvailability.Visible) ||
                receipt.PreviousAvailability is not null ||
                receipt.FindingId != "F-1" ||
                receipt.ReviewBounds != "(0, 0, 100, 50)" ||
                receipt.ImageFileName is not null ||
                receipt.ImageSha256 is not null)
            {
                throw new InvalidOperationException(
                    "The overlay debug receipt does not carry the observed transition.");
            }
            if (string.IsNullOrWhiteSpace(receipt.PdVersion) ||
                string.IsNullOrWhiteSpace(receipt.BridgePackageVersion))
            {
                throw new InvalidOperationException(
                    "The overlay debug receipt does not identify its binaries.");
            }
            string[] receipts = Directory.GetFiles(root, "*.json");
            if (receipts.Length != 1)
            {
                throw new InvalidOperationException(
                    "The overlay debug capture did not write exactly one receipt file.");
            }
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(receipts[0]));
            foreach (string field in new[]
                {
                    "Schema", "TimestampUtc", "Trigger", "Availability",
                    "FindingId", "ReviewBounds", "CaptureId", "Revision",
                })
            {
                if (!document.RootElement.TryGetProperty(field, out _))
                {
                    throw new InvalidOperationException(
                        $"The overlay debug receipt JSON is missing '{field}'.");
                }
            }
        }
        finally
        {
            DeleteDebugCheckRoot(root);
        }
    }

    private static void CheckOverlayDebugTransitionOnly()
    {
        string root = NewDebugCheckRoot();
        try
        {
            DateTimeOffset now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            var capture = new OverlayDebugCapture(
                clock: () => now,
                directoryOverride: root, autoCaptureEnabled: true);
            if (capture.TryCaptureAuto(
                    DebugOverlayState(EngineWpfOverlayAvailability.Visible),
                    "F-1",
                    null) is null)
            {
                throw new InvalidOperationException(
                    "The overlay debug capture skipped a first availability transition.");
            }
            now += TimeSpan.FromSeconds(3);
            if (capture.TryCaptureAuto(
                    DebugOverlayState(EngineWpfOverlayAvailability.Visible),
                    "F-1",
                    null) is not null)
            {
                throw new InvalidOperationException(
                    "The overlay debug capture logged a repeated availability.");
            }
            now += TimeSpan.FromSeconds(3);
            OverlayDebugReceipt? second = capture.TryCaptureAuto(
                DebugOverlayState(
                    EngineWpfOverlayAvailability.Unavailable,
                    "test cover",
                    ["cover here"]),
                "F-1",
                null);
            if (second is null ||
                second.PreviousAvailability != nameof(EngineWpfOverlayAvailability.Visible) ||
                second.UnavailableReason != "test cover" ||
                !second.DiagnosticMessages.SequenceEqual(["cover here"]))
            {
                throw new InvalidOperationException(
                    "The overlay debug capture does not carry the transition reason.");
            }
            if (Directory.GetFiles(root, "*.json").Length != 2)
            {
                throw new InvalidOperationException(
                    "The overlay debug capture did not log exactly the transitions.");
            }
        }
        finally
        {
            DeleteDebugCheckRoot(root);
        }
    }

    private static void CheckOverlayDebugRateLimit()
    {
        string root = NewDebugCheckRoot();
        try
        {
            DateTimeOffset now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            var capture = new OverlayDebugCapture(
                clock: () => now,
                directoryOverride: root, autoCaptureEnabled: true);
            if (capture.TryCaptureAuto(
                    DebugOverlayState(EngineWpfOverlayAvailability.Visible),
                    null,
                    null) is null)
            {
                throw new InvalidOperationException(
                    "The overlay debug capture skipped a first availability transition.");
            }
            now += TimeSpan.FromMilliseconds(500);
            if (capture.TryCaptureAuto(
                    DebugOverlayState(EngineWpfOverlayAvailability.Unavailable, "burst"),
                    null,
                    null) is not null)
            {
                throw new InvalidOperationException(
                    "The overlay debug capture ignored its minimum auto interval.");
            }
            now += TimeSpan.FromMilliseconds(2000);
            if (capture.TryCaptureAuto(
                    DebugOverlayState(EngineWpfOverlayAvailability.Unavailable, "burst"),
                    null,
                    null) is null)
            {
                throw new InvalidOperationException(
                    "The overlay debug capture held the rate limit past its interval.");
            }
            if (Directory.GetFiles(root, "*.json").Length != 2)
            {
                throw new InvalidOperationException(
                    "The overlay debug rate limit did not bound the receipt files.");
            }
        }
        finally
        {
            DeleteDebugCheckRoot(root);
        }
    }

    private static void CheckOverlayDebugRetention()
    {
        string root = NewDebugCheckRoot();
        try
        {
            DateTimeOffset now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            var capture = new OverlayDebugCapture(
                clock: () => now,
                directoryOverride: root, autoCaptureEnabled: true);
            for (int step = 0; step < 55; step++)
            {
                now += TimeSpan.FromSeconds(3);
                EngineWpfOverlayAvailability availability =
                    step % 2 == 0
                        ? EngineWpfOverlayAvailability.Visible
                        : EngineWpfOverlayAvailability.Unavailable;
                capture.TryCaptureAuto(
                    DebugOverlayState(availability, "retention"),
                    null,
                    null);
            }
            string[] receipts = Directory.GetFiles(root, "*.json");
            if (receipts.Length != OverlayDebugCapture.RetainedReceipts)
            {
                throw new InvalidOperationException(
                    "The overlay debug log did not retain exactly its newest receipts.");
            }
        }
        finally
        {
            DeleteDebugCheckRoot(root);
        }
    }

    private static void CheckOverlayDebugSafeFilenames()
    {
        string root = NewDebugCheckRoot();
        try
        {
            DateTimeOffset now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
            var capture = new OverlayDebugCapture(
                clock: () => now,
                directoryOverride: root, autoCaptureEnabled: true);
            foreach (EngineWpfOverlayAvailability availability in new[]
                {
                    EngineWpfOverlayAvailability.Visible,
                    EngineWpfOverlayAvailability.Pending,
                    EngineWpfOverlayAvailability.Unavailable,
                    EngineWpfOverlayAvailability.Empty,
                    EngineWpfOverlayAvailability.Disposed,
                })
            {
                now += TimeSpan.FromSeconds(3);
                capture.TryCaptureAuto(
                    DebugOverlayState(availability, "../../evil_ünïcode"),
                    null,
                    null);
            }
            string[] receipts = Directory.GetFiles(root, "*.json");
            if (receipts.Length != 5)
            {
                throw new InvalidOperationException(
                    "The overlay debug capture did not log every availability.");
            }
            foreach (string path in receipts)
            {
                string name = Path.GetFileName(path);
                if (name.Any(character =>
                        !(char.IsLetterOrDigit(character) ||
                            character is '.' or '_' or '-')))
                {
                    throw new InvalidOperationException(
                        "The overlay debug receipt file name is not filesystem-safe.");
                }
                if (!Path.GetFullPath(path).StartsWith(
                        Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The overlay debug receipt escaped its directory.");
                }
            }
        }
        finally
        {
            DeleteDebugCheckRoot(root);
        }
    }

    private static void CheckOverlayDebugOffByDefault()
    {
        string root = NewDebugCheckRoot();
        try
        {
            var capture = new OverlayDebugCapture(directoryOverride: root);
            if (capture.AutoCaptureEnabled)
            {
                throw new InvalidOperationException(
                    "Automatic overlay debug capture is not off by default.");
            }
            if (capture.TryCaptureAuto(
                    DebugOverlayState(EngineWpfOverlayAvailability.Visible),
                    "F-1",
                    null) is not null)
            {
                throw new InvalidOperationException(
                    "Disabled automatic capture wrote a receipt.");
            }
            if (Directory.GetFiles(root, "*").Length != 0)
            {
                throw new InvalidOperationException(
                    "Disabled automatic capture touched the filesystem.");
            }
            capture.AutoCaptureEnabled = true;
            if (capture.TryCaptureAuto(
                    DebugOverlayState(EngineWpfOverlayAvailability.Visible),
                    "F-1",
                    null) is null)
            {
                throw new InvalidOperationException(
                    "Enabled automatic capture skipped a first availability transition.");
            }
        }
        finally
        {
            DeleteDebugCheckRoot(root);
        }
    }

    private static void CheckOverlayDebugPrunesOrphanImages()
    {
        string root = NewDebugCheckRoot();
        try
        {
            File.WriteAllBytes(
                Path.Combine(root, "overlay-debug_20260916_120000_000_orphan.png"),
                [0x89, 0x50, 0x4E, 0x47]);
            File.WriteAllText(
                Path.Combine(root, "overlay-debug_20260916_120000_000_kept.json"),
                "{\"ImageFileName\":\"overlay-debug_20260916_120000_000_kept.png\"}");
            File.WriteAllBytes(
                Path.Combine(root, "overlay-debug_20260916_120000_000_kept.png"),
                [0x89, 0x50, 0x4E, 0x47]);
            OverlayDebugCapture.PruneDirectory(root, OverlayDebugCapture.RetainedReceipts);
            if (File.Exists(Path.Combine(root, "overlay-debug_20260916_120000_000_orphan.png")))
            {
                throw new InvalidOperationException(
                    "Pruning kept a PNG with no receipt counterpart.");
            }
            if (!File.Exists(Path.Combine(root, "overlay-debug_20260916_120000_000_kept.png")))
            {
                throw new InvalidOperationException(
                    "Pruning deleted a PNG whose receipt was retained.");
            }
        }
        finally
        {
            DeleteDebugCheckRoot(root);
        }
    }

    private static void CheckOverlayDebugPrunesRealNames()
    {
        string root = NewDebugCheckRoot();
        try
        {
            // Real generator names: the PNG carries an extra timestamp
            // beyond its receipt stem, so stem matching cannot retain it.
            // Only the ImageFileName recorded in a retained receipt keeps
            // its PNG; stale and unreferenced images are pruned.
            File.WriteAllText(
                Path.Combine(root, "overlay-debug_20260916_120001_000_new.json"),
                "{\"ImageFileName\":\"overlay-debug_20260916_120001_000_new_120001123.png\"}");
            File.WriteAllBytes(
                Path.Combine(root, "overlay-debug_20260916_120001_000_new_120001123.png"),
                [0x89, 0x50, 0x4E, 0x47]);
            File.WriteAllText(
                Path.Combine(root, "overlay-debug_20260916_120000_000_old.json"),
                "{\"ImageFileName\":\"overlay-debug_20260916_120000_000_old_120000456.png\"}");
            File.WriteAllBytes(
                Path.Combine(root, "overlay-debug_20260916_120000_000_old_120000456.png"),
                [0x89, 0x50, 0x4E, 0x47]);
            File.WriteAllBytes(
                Path.Combine(root, "overlay-debug_20260916_120002_000_orphan.png"),
                [0x89, 0x50, 0x4E, 0x47]);
            OverlayDebugCapture.PruneDirectory(root, 1);
            if (!File.Exists(Path.Combine(root, "overlay-debug_20260916_120001_000_new.json")) ||
                !File.Exists(Path.Combine(root, "overlay-debug_20260916_120001_000_new_120001123.png")))
            {
                throw new InvalidOperationException(
                    "Pruning deleted a retained receipt's real-named image.");
            }
            if (File.Exists(Path.Combine(root, "overlay-debug_20260916_120000_000_old.json")) ||
                File.Exists(Path.Combine(root, "overlay-debug_20260916_120000_000_old_120000456.png")) ||
                File.Exists(Path.Combine(root, "overlay-debug_20260916_120002_000_orphan.png")))
            {
                throw new InvalidOperationException(
                    "Pruning kept a stale or unreferenced image.");
            }
        }
        finally
        {
            DeleteDebugCheckRoot(root);
        }
    }

    private static void CheckOverlayDebugWindowImage()
    {
        string root = NewDebugCheckRoot();
        bool ownsApplication = Application.Current is null;
        if (!ownsApplication &&
            (Application.Current.Dispatcher.HasShutdownStarted ||
                Application.Current.Dispatcher.HasShutdownFinished))
        {
            throw new InvalidOperationException(
                "The overlay debug image check needs a live dispatcher; it must run " +
                "before sibling checks that shut the application down.");
        }
        App application = Application.Current as App ?? new App();
        if (ownsApplication)
        {
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
        var window = new Window
        {
            Width = 240,
            Height = 120,
            Content = new TextBlock { Text = "overlay debug" },
        };
        try
        {
            window.Show();
            window.Dispatcher.Invoke(
                static () => { },
                DispatcherPriority.ApplicationIdle);
            var capture = new OverlayDebugCapture(
                () => window,
                directoryOverride: root, autoCaptureEnabled: true);
            OverlayDebugReceipt? receipt = capture.TryCaptureAuto(
                DebugOverlayState(EngineWpfOverlayAvailability.Visible),
                "F-9",
                null);
            if (receipt?.ImageFileName is null || receipt.ImageSha256 is null)
            {
                throw new InvalidOperationException(
                    "The overlay debug capture did not render the visible window.");
            }
            string path = Path.Combine(root, receipt.ImageFileName);
            byte[] png = File.ReadAllBytes(path);
            byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            if (!png.Take(signature.Length).SequenceEqual(signature))
            {
                throw new InvalidOperationException(
                    "The overlay debug image is not a PNG file.");
            }
            if (!string.Equals(
                    receipt.ImageSha256,
                    OverlayDebugCapture.ComputeFileSha256(path),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The overlay debug receipt hash does not match its PNG.");
            }

            Console.WriteLine(
                "PASS: overlay debug capture rendered a window PNG with a matching receipt hash.");
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
            // No application shutdown here: a second bootstrap/shutdown cycle
            // in one process poisons the dispatcher for sibling checks, and
            // the gate process exit reclaims the live dispatcher. Sibling
            // checks that own the application still shut it down themselves.
            DeleteDebugCheckRoot(root);
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
            StrokeWidth("corridor-shadow") != 20 ||
            StrokeWidth("corridor-outline") != 10 ||
            StrokeWidth("pair-axis") != 5 ||
            StrokeWidth("p-center") != 10 ||
            StrokeWidth("p-label") != 5 ||
            StrokeWidth("n-center") != 10 ||
            StrokeWidth("n-label") != 5 ||
            StrokeWidth("corridor-label") != 5 ||
            (includeIntrusion &&
                (StrokeWidth("intrusion") != 20 ||
                 StrokeWidth("intrusion-label") != 5)) ||
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

    private static void CheckSelectionLatestWins()
    {
        foreach (string parkStage in new[] { "read", "zoom", "capture", "publish" })
        {
            Task.Run(() => CheckSelectionLatestWinsAtAsync(parkStage))
                .GetAwaiter()
                .GetResult();
        }
    }

    private static async Task CheckSelectionLatestWinsAtAsync(string parkStage)
    {
        var pipeline = new DpViaCorridorSelectionPipeline();
        DpViaCorridorAnalysis analysis = CreateSelectionAnalysis(6);
        DpViaCorridorFinding[] findings = analysis.Result.Findings.ToArray();
        var bounds = new DpViaCorridorBounds(10, 20, 30, 40);
        var zoom = new DpViaCorridorZoomResult(
            DpViaCorridorZoomResult.CurrentSchema,
            "complete",
            7,
            @"C:\disposable\selection.brd",
            "unused.rpt",
            "crossing-final",
            "ETCH/TOP",
            "mils",
            bounds,
            bounds);
        TaskCompletionSource readGate = NewGate();
        TaskCompletionSource zoomGate = NewGate();
        TaskCompletionSource captureGate = NewGate();
        TaskCompletionSource publishGate = NewGate();
        // Pre-open every boundary before the park stage so all six selections
        // pile up exactly at the parked boundary.
        if (parkStage != "read")
        {
            readGate.TrySetResult();
        }
        if (parkStage is "capture" or "publish")
        {
            zoomGate.TrySetResult();
        }
        if (parkStage == "publish")
        {
            captureGate.TrySetResult();
        }
        string? currentId = null;
        var entries = new List<(long Epoch, string Stage)>();
        var published = new List<(long Epoch, string FindingId)>();
        TaskCompletionSource readArrived = NewGate();
        TaskCompletionSource zoomArrived = NewGate();
        TaskCompletionSource captureArrived = NewGate();
        TaskCompletionSource publishArrived = NewGate();
        void Enter(long epoch, string stage, TaskCompletionSource arrived)
        {
            lock (entries)
            {
                entries.Add((epoch, stage));
            }
            arrived.TrySetResult();
        }
        var operations = new DpViaCorridorSelectionOperations(
            NavigateAsync: async (selection, token) =>
            {
                Enter(selection.Epoch, "read", readArrived);
                await readGate.Task.WaitAsync(token);
                Enter(selection.Epoch, "zoom", zoomArrived);
                await zoomGate.Task.WaitAsync(token);
                return SelectionOutcome(zoom, selection.Finding.Id);
            },
            CaptureAsync: async (selection, token) =>
            {
                Enter(selection.Epoch, "capture", captureArrived);
                await captureGate.Task.WaitAsync(token);
                // Review content is opaque to epoch gating; the pipeline only
                // forwards it to the publication boundary.
                return null!;
            },
            PublishAsync: async (selection, selectionZoom, review, navigateMs, captureMs, token) =>
            {
                Enter(selection.Epoch, "publish", publishArrived);
                await publishGate.Task.WaitAsync(token);
                lock (published)
                {
                    published.Add((selection.Epoch, selection.Finding.Id));
                }
            },
            IsCurrent: selection =>
                selection.Epoch == pipeline.CurrentEpoch &&
                selection.Finding.Id == currentId);
        long[] epochs = new long[6];
        Task<DpViaCorridorSelectionOutcome?>[] runs = new Task<DpViaCorridorSelectionOutcome?>[6];
        for (int index = 0; index < 6; index++)
        {
            epochs[index] = pipeline.BeginSelection();
            currentId = findings[index].Id;
            runs[index] = pipeline.RunSelectionAsync(
                epochs[index],
                analysis,
                findings[index],
                operations,
                TimeSpan.Zero,
                CancellationToken.None);
        }
        int Count(string stage)
        {
            lock (entries)
            {
                return entries.Count(entry => entry.Stage == stage);
            }
        }
        // Every selection ran synchronously to the parked boundary; the first
        // five were then superseded there while the sixth stays parked.
        int parkedIndex = parkStage switch
        {
            "read" => 0,
            "zoom" => 1,
            "capture" => 2,
            _ => 3,
        };
        int[] expectedEntries = [6, parkedIndex >= 1 ? 6 : 0, parkedIndex >= 2 ? 6 : 0, parkedIndex >= 3 ? 6 : 0];
        string[] stages = ["read", "zoom", "capture", "publish"];
        for (int stage = 0; stage < 4; stage++)
        {
            if (Count(stages[stage]) != expectedEntries[stage])
            {
                throw new InvalidOperationException(
                    $"Parking at {parkStage} did not hold six selections at the {stages[stage]} boundary.");
            }
        }
        for (int index = 0; index < 5; index++)
        {
            if (await runs[index] is not null)
            {
                throw new InvalidOperationException(
                    $"A superseded selection completed instead of losing at the {parkStage} boundary.");
            }
        }
        if (runs[5].IsCompleted)
        {
            throw new InvalidOperationException(
                $"The final selection did not stay parked at the {parkStage} boundary.");
        }
        // Release the final selection one boundary at a time; nothing else may
        // advance or publish.
        TaskCompletionSource[] gates = [readGate, zoomGate, captureGate, publishGate];
        TaskCompletionSource[] arrivals = [readArrived, zoomArrived, captureArrived, publishArrived];
        for (int stage = parkedIndex; stage < 4; stage++)
        {
            gates[stage].TrySetResult();
            if (stage + 1 < 4)
            {
                await arrivals[stage + 1].Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        DpViaCorridorSelectionOutcome? outcome = await runs[5].WaitAsync(TimeSpan.FromSeconds(10));
        if (outcome is null || outcome.Epoch != epochs[5])
        {
            throw new InvalidOperationException(
                $"Parking at {parkStage} did not complete only the final epoch.");
        }
        lock (published)
        {
            if (published.Count != 1 ||
                published[0].Epoch != epochs[5] ||
                published[0].FindingId != findings[5].Id)
            {
                throw new InvalidOperationException(
                    $"Parking at {parkStage} published an epoch other than the final selection.");
            }
        }
    }

    private static void CheckSelectionQuietPeriod()
    {
        Task.Run(CheckSelectionQuietPeriodAsync).GetAwaiter().GetResult();
    }

    private static async Task CheckSelectionQuietPeriodAsync()
    {
        var pipeline = new DpViaCorridorSelectionPipeline();
        DpViaCorridorAnalysis analysis = CreateSelectionAnalysis(2);
        DpViaCorridorFinding[] findings = analysis.Result.Findings.ToArray();
        int navigateCalls = 0;
        var operations = new DpViaCorridorSelectionOperations(
            NavigateAsync: (selection, token) =>
            {
                Interlocked.Increment(ref navigateCalls);
                throw new InvalidOperationException("Unreachable in the quiet-period check.");
            },
            CaptureAsync: (selection, token) => throw new InvalidOperationException("Unreachable."),
            PublishAsync: (selection, selectionZoom, review, navigateMs, captureMs, token) =>
                throw new InvalidOperationException("Unreachable."),
            IsCurrent: selection => true);
        long first = pipeline.BeginSelection();
        Task<DpViaCorridorSelectionOutcome?> parked = pipeline.RunSelectionAsync(
            first,
            analysis,
            findings[0],
            operations,
            Timeout.InfiniteTimeSpan,
            CancellationToken.None);
        if (parked.IsCompleted || Volatile.Read(ref navigateCalls) != 0)
        {
            throw new InvalidOperationException(
                "A selection dispatched native work before its quiet period elapsed.");
        }
        long second = pipeline.BeginSelection();
        Task<DpViaCorridorSelectionOutcome?> winner = pipeline.RunSelectionAsync(
            second,
            analysis,
            findings[1],
            operations with
            {
                NavigateAsync = (selection, token) =>
                {
                    Interlocked.Increment(ref navigateCalls);
                    return Task.FromResult(SelectionOutcome(new DpViaCorridorZoomResult(
                        DpViaCorridorZoomResult.CurrentSchema,
                        "complete",
                        7,
                        @"C:\disposable\selection.brd",
                        "unused.rpt",
                        findings[1].Id,
                        "ETCH/TOP",
                        "mils",
                        new(10, 20, 30, 40),
                        new(10, 20, 30, 40)),
                        findings[1].Id));
                },
                CaptureAsync = (selection, token) => Task.FromResult<AllegroReviewFrame>(null!),
                PublishAsync = (selection, selectionZoom, review, navigateMs, captureMs, token) =>
                    Task.CompletedTask,
            },
            TimeSpan.Zero,
            CancellationToken.None);
        if (await parked.WaitAsync(TimeSpan.FromSeconds(10)) is not null)
        {
            throw new InvalidOperationException(
                "A quiet-period selection survived supersession without dispatching.");
        }
        DpViaCorridorSelectionOutcome? outcome =
            await winner.WaitAsync(TimeSpan.FromSeconds(10));
        if (outcome is null || outcome.Epoch != second)
        {
            throw new InvalidOperationException(
                "The selection after the quiet period did not complete alone.");
        }
        if (Volatile.Read(ref navigateCalls) != 1)
        {
            throw new InvalidOperationException(
                "Native work ran for a selection that never left its quiet period.");
        }
    }

    private static void CheckSelectionDrawingBeforeCapture()
    {
        Task.Run(CheckSelectionDrawingBeforeCaptureAsync).GetAwaiter().GetResult();
    }

    private static async Task CheckSelectionDrawingBeforeCaptureAsync()
    {
        var pipeline = new DpViaCorridorSelectionPipeline();
        DpViaCorridorAnalysis analysis = CreateSelectionAnalysis(1);
        DpViaCorridorFinding finding = analysis.Result.Findings.ToArray()[0];
        var bounds = new DpViaCorridorBounds(10, 20, 30, 40);
        var zoom = new DpViaCorridorZoomResult(
            DpViaCorridorZoomResult.CurrentSchema,
            "complete",
            7,
            @"C:\disposable\selection.brd",
            "unused.rpt",
            finding.Id,
            "ETCH/TOP",
            "mils",
            bounds,
            bounds);
        var entries = new List<string>();
        void Enter(string stage)
        {
            lock (entries)
            {
                entries.Add(stage);
            }
        }
        TaskCompletionSource captureGate = NewGate();
        TaskCompletionSource captureArrived = NewGate();
        var operations = new DpViaCorridorSelectionOperations(
            NavigateAsync: (selection, token) =>
            {
                Enter("navigate");
                return Task.FromResult(SelectionOutcome(zoom, selection.Finding.Id));
            },
            PublishDrawingAsync: (selection, selectionZoom, navigateMs, token) =>
            {
                Enter("drawing");
                if (selection.Epoch != pipeline.CurrentEpoch ||
                    !ReferenceEquals(selectionZoom.Zoom, zoom) ||
                    selection.Finding.Id != finding.Id ||
                    navigateMs < 0)
                {
                    throw new InvalidOperationException(
                        "The drawing hook did not receive the verified selection.");
                }
                return Task.CompletedTask;
            },
            CaptureAsync: async (selection, token) =>
            {
                Enter("capture");
                captureArrived.TrySetResult();
                await captureGate.Task.WaitAsync(token);
                return (AllegroReviewFrame)null!;
            },
            PublishAsync: (selection, selectionZoom, review, navigateMs, captureMs, token) =>
            {
                Enter("publish");
                return Task.CompletedTask;
            },
            IsCurrent: selection => selection.Epoch == pipeline.CurrentEpoch);
        long epoch = pipeline.BeginSelection();
        Task<DpViaCorridorSelectionOutcome?> run = pipeline.RunSelectionAsync(
            epoch,
            analysis,
            finding,
            operations,
            TimeSpan.Zero,
            CancellationToken.None);
        await captureArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        string[] parked;
        lock (entries)
        {
            parked = entries.ToArray();
        }
        if (!parked.SequenceEqual(new[] { "navigate", "drawing", "capture" }))
        {
            throw new InvalidOperationException(
                "The drawing was not published between navigation and capture.");
        }
        if (run.IsCompleted)
        {
            throw new InvalidOperationException(
                "The selection completed before its capture was released.");
        }
        captureGate.TrySetResult();
        DpViaCorridorSelectionOutcome? outcome =
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        if (outcome is null ||
            outcome.Epoch != epoch ||
            !ReferenceEquals(outcome.Navigation.Zoom, zoom) ||
            outcome.NavigateMilliseconds < 0 ||
            outcome.CaptureMilliseconds < 0)
        {
            throw new InvalidOperationException(
                "The selection did not complete with its verified zoom after capture.");
        }
        string[] finished;
        lock (entries)
        {
            finished = entries.ToArray();
        }
        if (!finished.SequenceEqual(new[] { "navigate", "drawing", "capture", "publish" }))
        {
            throw new InvalidOperationException(
                "The selection did not run navigate, drawing, capture, then publish in order.");
        }
    }

    private static void CheckFollowOffPerformsZeroNativeWork()
    {
        AllegroEngineSession session = AllegroEngineSession.Create();
        try
        {
            EngineWpfPresentation presentation = EngineWpfPresentation.Attach(
                session,
                Dispatcher.CurrentDispatcher);
            try
            {
                var workspace = new DpViaCorridorWorkspaceViewModel(session, presentation);
                try
                {
                    workspace.AdoptResultForTest(CreateSelectionAnalysis(6));
                    int navigateCalls = 0;
                    int captureCalls = 0;
                    workspace.NavigateOverride = (analysis, finding, request, token) =>
                    {
                        navigateCalls++;
                        throw new InvalidOperationException(
                            "Follow-off navigation must never dispatch.");
                    };
                    workspace.CaptureReviewOverride = (source, drawings, token) =>
                    {
                        captureCalls++;
                        throw new InvalidOperationException(
                            "Follow-off capture must never dispatch.");
                    };
                    workspace.FollowSelection = false;
                    workspace.FollowAttemptCountForTest = 0;
                    DpViaCorridorFinding[] findings = workspace.VisibleFindings.ToArray();
                    if (findings.Length != 6)
                    {
                        throw new InvalidOperationException(
                            "The follow-off check did not inject six selectable findings.");
                    }
                    foreach (DpViaCorridorFinding finding in findings)
                    {
                        workspace.SelectedFinding = finding;
                    }
                    if (navigateCalls != 0 || captureCalls != 0 ||
                        workspace.FollowAttemptCountForTest != 0 ||
                        workspace.RecentSelectionTimings.Count != 0 ||
                        workspace.CapturedReview is not null ||
                        workspace.VerifiedZoom is not null ||
                        workspace.NavigationError.Length != 0 ||
                        !ReferenceEquals(workspace.SelectedFinding, findings[5]))
                    {
                        throw new InvalidOperationException(
                            "Follow-off selections performed native work, published, or lost the final selection.");
                    }
                }
                finally
                {
                    workspace.Dispose();
                }
            }
            finally
            {
                presentation.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static DpViaCorridorNavigationOutcome SelectionOutcome(
        DpViaCorridorZoomResult zoom,
        string findingId) =>
        new(
            Guid.NewGuid(),
            DpViaCorridorNavigationOrigin.Follow,
            DpViaCorridorNavigationMode.Browse,
            DpViaCorridorNavigationMode.Browse,
            DpViaCorridorVerification.WitnessIdentityAtNavigation,
            null,
            findingId,
            Guid.NewGuid(),
            zoom,
            new DpViaCorridorNavigationPhases(0, 0, 0));

    private static void WaitForWorkspaceCondition(Func<bool> condition, string message)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException(message);
            }
            Thread.Sleep(25);
        }
    }

    private static DpViaCorridorZoomResult SelectionZoom(string findingId)
    {
        var bounds = new DpViaCorridorBounds(10, 20, 30, 40);
        return new DpViaCorridorZoomResult(
            DpViaCorridorZoomResult.CurrentSchema,
            "complete",
            7,
            @"C:\disposable\selection.brd",
            "unused.rpt",
            findingId,
            "ETCH/TOP",
            "mils",
            bounds,
            bounds);
    }

    private static void CheckOutcomeAttributionMatrix()
    {
        var operationId = Guid.NewGuid();
        DpViaCorridorZoomResult zoom = SelectionZoom("crossing-0");
        DpViaCorridorNavigationOutcome own = new(
            operationId,
            DpViaCorridorNavigationOrigin.Explicit,
            DpViaCorridorNavigationMode.Revalidate,
            DpViaCorridorNavigationMode.Revalidate,
            DpViaCorridorVerification.FreshRegionSelectedWitnessMatch,
            "region-opaque-id",
            "crossing-0",
            Guid.NewGuid(),
            zoom,
            new DpViaCorridorNavigationPhases(1, 2, 3));
        if (DpViaCorridorOutcomeAttribution.IsForeignOutcome(own, operationId, "crossing-0"))
        {
            throw new InvalidOperationException(
                "The attribution decision dropped the outcome that matches its request.");
        }
        if (!DpViaCorridorOutcomeAttribution.IsForeignOutcome(
            own with { OperationId = Guid.NewGuid() },
            operationId,
            "crossing-0"))
        {
            throw new InvalidOperationException(
                "A foreign Browse outcome was attributed to this request.");
        }
        if (!DpViaCorridorOutcomeAttribution.IsForeignOutcome(own, operationId, "crossing-1"))
        {
            throw new InvalidOperationException(
                "An outcome for another finding was attributed to this request.");
        }
        if (!DpViaCorridorOutcomeAttribution.IsForeignOutcome(
            own with { OperationId = Guid.NewGuid() },
            operationId,
            "crossing-1"))
        {
            throw new InvalidOperationException(
                "A fully foreign outcome was attributed to this request.");
        }
    }

    private static void CheckExplicitClicksWhenNotReadyAreReported()
    {
        AllegroEngineSession session = AllegroEngineSession.Create();
        try
        {
            EngineWpfPresentation presentation = EngineWpfPresentation.Attach(
                session,
                Dispatcher.CurrentDispatcher);
            try
            {
                var workspace = new DpViaCorridorWorkspaceViewModel(session, presentation);
                try
                {
                    workspace.AdoptResultForTest(CreateSelectionAnalysis(1));
                    workspace.FollowSelection = false;
                    workspace.SelectedFinding = workspace.VisibleFindings.ToArray()[0];
                    int navigateCalls = 0;
                    workspace.NavigateOverride = (analysis, target, request, token) =>
                    {
                        Interlocked.Increment(ref navigateCalls);
                        throw new InvalidOperationException(
                            "Navigation must never dispatch without a ready session.");
                    };
                    workspace.ZoomCommand.Execute(null);
                    if (!workspace.NavigationError.StartsWith(
                        "Navigation request (Browse) not started:",
                        StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The rejected Browse click was not reported.");
                    }
                    workspace.RevalidateCommand.Execute(null);
                    if (!workspace.NavigationError.StartsWith(
                        "Navigation request (Revalidate) not started:",
                        StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The rejected Revalidate click was not reported.");
                    }
                    if (Volatile.Read(ref navigateCalls) != 0 ||
                        workspace.VerifiedZoom is not null ||
                        workspace.CapturedReview is not null ||
                        workspace.RecentSelectionTimings.Count != 0 ||
                        workspace.SupersededOutcomeCountForTest != 0)
                    {
                        throw new InvalidOperationException(
                            "A rejected click dispatched or published navigation work.");
                    }
                }
                finally
                {
                    workspace.Dispose();
                }
            }
            finally
            {
                presentation.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static DpViaCorridorAnalysis CreateSelectionAnalysis(int findingCount)
    {
        var document = new WorkspaceDocumentIdentity(
            "selection-session",
            SessionGeneration: 1,
            BoardGeneration: 7,
            ProcessId: null,
            Design: @"C:\disposable\selection.brd",
            ProtocolVersion: "25");
        DpViaCorridorFinding[] findings = Enumerable.Range(0, findingCount)
            .Select(index => new DpViaCorridorFinding(
                $"crossing-{index}",
                $"PAIR_{index}",
                $"NET_{index}",
                "cline_segment",
                "ETCH/TOP",
                "Signal",
                "LOW",
                new(100 + index, 100),
                new(140 + index, 100),
                null,
                5 + index,
                12,
                30))
            .ToArray();
        var result = new DpViaCorridorResult(
            DpViaCorridorResult.CurrentSchema,
            "complete",
            7,
            "selection",
            "mils",
            "mils",
            "unused.rpt",
            null,
            true,
            findingCount,
            findingCount,
            findingCount,
            findingCount,
            0,
            0,
            findingCount,
            false,
            findings);
        return new(document, result);
    }

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

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
