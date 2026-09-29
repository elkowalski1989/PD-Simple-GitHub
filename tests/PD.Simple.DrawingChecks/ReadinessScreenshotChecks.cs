using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Interactions;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Reviews;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Engine.Tools;
using CircuitHub.AllegroBridge.Wpf.Engine;
using PD.Simple;
using PD.Simple.Tools.ConstraintsDrc;

/// <summary>
/// Render application-owned controls with synthetic offline fixtures or an
/// explicitly supplied historical archive. No native Allegro work is run.
/// </summary>
internal static class ReadinessScreenshotChecks
{
    private const string FixtureLabel = "SYNTHETIC OFFLINE FIXTURE — no live Allegro or native edit evidence";

    internal static void Run(string directory, bool reviewsOnly = false)
    {
        if (Application.Current is null)
        {
            var application = new App();
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
        Directory.CreateDirectory(directory);
        string runDirectory = Path.Combine(directory, "synthetic-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        ReviewArchiveContent content = CreateReview();
        string reviewPath = Path.Combine(runDirectory, "synthetic-review.allegroreview");
        Pump(ReviewFile.SaveCopyAsync(reviewPath, content).AsTask());
        var window = new MainWindow([], new MemoryPreferences())
        {
            Width = 1400,
            Height = 1000,
            Title = FixtureLabel,
        };
        try
        {
            window.Show();
            PumpLayout(window);
            CapturePdReview(window, reviewPath, runDirectory);
            if (!reviewsOnly)
            {
                CaptureConstraints(window, runDirectory);
                CaptureWorkspace(window, runDirectory);
            }
            CaptureSharedReview(reviewPath, runDirectory);
            Console.WriteLine("PASS: retained actual WPF presentation images using synthetic offline fixtures only: " + runDirectory);
        }
        finally
        {
            window.Close();
            PumpUntil(() => !window.IsLoaded, window.Dispatcher);
        }
    }

    internal static void RunArchive(string path, string directory)
    {
        if (Application.Current is null)
        {
            var application = new App();
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
        Task<LoadedReviewFile> load = ReviewFile.LoadAsync(path).AsTask();
        Pump(load);
        LoadedReviewFile original = load.GetAwaiter().GetResult();
        string originalReview = JsonSerializer.Serialize(original.Content.Review);
        string runDirectory = Path.Combine(directory, "historical-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);
        const string label = "HISTORICAL ARCHIVE REOPEN — offline; no live Allegro session";
        var window = new MainWindow([], new MemoryPreferences())
        {
            Width = 1400,
            Height = 1000,
            Title = label,
        };
        try
        {
            window.Show();
            PumpLayout(window);
            window.ReviewMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var model = window.ReviewView.ViewModel!;
            Pump(model.LoadBundleAsync(path));
            if (model.CanAcquire || model.CanCapture || model.HasLiveScene || model.IsLegacyReview ||
                JsonSerializer.Serialize(model.PortableReview) != originalReview)
            {
                throw new InvalidOperationException("The archive did not reopen unchanged as an offline portable review.");
            }
            // Use the real view control so the captured center annotation is
            // visible instead of being below the image viewport at 100%.
            ((Slider)window.ReviewView.FindName("ZoomSlider")).Value = 0.5;
            PumpLayout(window);
            foreach (ReviewImageKind kind in new[] { ReviewImageKind.Annotated, ReviewImageKind.Raw })
            {
                ReviewImageVariant variant = original.Content.Review.Images.First(item =>
                    item.Kind == kind && item.Availability == ReviewImageAvailability.Available);
                string controlName = kind == ReviewImageKind.Annotated ? "AnnotatedRadio" : "RawRadio";
                var control = (RadioButton)window.ReviewView.FindName(controlName);
                if (!control.IsEnabled)
                {
                    throw new InvalidOperationException("The recorded image variant control is unavailable: " + kind);
                }
                control.IsChecked = true;
                PumpUntil(() => !model.IsBusy, window.Dispatcher);
                byte[] recordedBytes = original.Content.Images[variant.MemberName!].ToArray();
                using var imageSource = new MemoryStream(recordedBytes);
                BitmapSource expected = BitmapFrame.Create(imageSource, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                if (model.ShowAnnotated != (kind == ReviewImageKind.Annotated) ||
                    model.DisplayedImage is not BitmapSource displayed ||
                    displayed.PixelWidth != expected.PixelWidth || displayed.PixelHeight != expected.PixelHeight ||
                    !ImagePixels(displayed).AsSpan().SequenceEqual(ImagePixels(expected)))
                {
                    throw new InvalidOperationException("The selected control did not render the recorded image: " + kind);
                }
                Capture(window, runDirectory, "actual-review-" + kind.ToString().ToLowerInvariant(), label);
                Console.WriteLine($"ARCHIVE VARIANT: {kind}; {variant.Id}; {expected.PixelWidth}x{expected.PixelHeight}; " +
                    $"sha256 {Convert.ToHexString(SHA256.HashData(recordedBytes)).ToLowerInvariant()}");
            }
            if (JsonSerializer.Serialize(model.PortableReview) != originalReview || model.CanSaveRevision)
            {
                throw new InvalidOperationException("Reopening or selecting an image changed historical review data.");
            }
            using var source = File.OpenRead(path);
            string finalHash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
            if (finalHash != original.Sha256)
            {
                throw new InvalidOperationException("The source archive changed during the read-only reopen.");
            }
            Console.WriteLine("ARCHIVE PATH: " + original.Path);
            Console.WriteLine("ARCHIVE SHA256: " + original.Sha256);
            Console.WriteLine("ARCHIVE EVIDENCE: " + model.Evidence);
            Console.WriteLine("ARCHIVE STATUS: " + model.Status);
            Console.WriteLine("PASS: actual historical archive reopened offline; Raw/Annotated controls match recorded pixels; " +
                "review and source bytes unchanged. This does not exercise the OS file dialog.");
        }
        finally
        {
            window.Close();
            PumpUntil(() => !window.IsLoaded, window.Dispatcher);
        }
    }

    private static byte[] ImagePixels(BitmapSource source)
    {
        var normalized = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(normalized.PixelWidth * 4);
        var pixels = new byte[checked(stride * normalized.PixelHeight)];
        normalized.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void CapturePdReview(MainWindow window, string path, string directory)
    {
        window.ReviewMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = window.ReviewView.ViewModel!;
        Pump(model.LoadBundleAsync(path));
        model.SelectedFindingId = "synthetic-gap";
        model.AddDisposition("pd:confirmed", "Synthetic reviewer", "Fixture decision; no native clearance claim.");
        var annotated = (RadioButton)window.ReviewView.FindName("AnnotatedRadio");
        var raw = (RadioButton)window.ReviewView.FindName("RawRadio");
        annotated.IsChecked = true;
        PumpUntil(() => !model.IsBusy, window.Dispatcher);
        ImageSource annotatedImage = model.DisplayedImage ?? throw new InvalidOperationException("Annotated fixture image missing.");
        Capture(window, directory, "01-pd-review-annotated");
        raw.IsChecked = true;
        PumpUntil(() => !model.IsBusy, window.Dispatcher);
        if (model.ShowAnnotated || model.DisplayedImage is null || ReferenceEquals(annotatedImage, model.DisplayedImage))
        {
            throw new InvalidOperationException("The rendered PD Raw control did not select its independent fixture image.");
        }
        Capture(window, directory, "02-pd-review-raw");
    }

    private static void CaptureConstraints(MainWindow window, string directory)
    {
        window.ConstraintsDrcMenuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var model = (ConstraintsDrcViewModel)window.ConstraintsDrcView.DataContext;
        var sessionField = typeof(ConstraintsDrcViewModel).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var session = (AllegroEngineSession)sessionField.GetValue(model)!;
        var stateField = typeof(AllegroEngineSession).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        EngineSessionSnapshot original = session.State;
        var documentA = new WorkspaceDocumentIdentity("synthetic-view", 1, 1, null, "same-name-fixture.brd", "synthetic");
        var documentB = documentA with { BoardGeneration = 2 };
        var stateA = original with { ConnectionState = EngineConnectionState.Ready, Document = documentA };
        try
        {
            stateField.SetValue(session, stateA);
            model.ApplySessionState(stateA);
            model.AcceptSnapshot(new EngineConstraintSnapshot(documentA, 1,
                new(true, true, true, true, true, []), [],
                [new(EngineConstraintDomain.Spacing, "FIXTURE_SET", "ETCH/TOP", "MinLineWidth", EngineConstraintMode.On, "5")],
                [], [], [], [], [], []) { ConstraintFingerprint = "synthetic-first-fingerprint" }, true);
            model.SelectedValue = model.ValueRows[0];
            var value = (TextBox)window.ConstraintsDrcView.FindName("NewValueBox");
            value.Text = "not-a-number";
            PumpLayout(window);
            value.BringIntoView();
            PumpLayout(window);
            var edit = (Button)window.ConstraintsDrcView.FindName("EditConstraintButton");
            if (string.IsNullOrWhiteSpace(model.EditInputError) || edit.IsEnabled)
            {
                throw new InvalidOperationException("Invalid typed constraint input was not visible and refused.");
            }
            Capture(window, directory, "03-constraints-invalid-synthetic");
            EngineSessionSnapshot stateB = stateA with { Document = documentB };
            stateField.SetValue(session, stateB);
            model.ApplySessionState(stateB);
            if (model.SelectedValue is not null || model.CanPrepareEdit || !model.EditSummary.Contains("board_changed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Synthetic document replacement left historical constraint input actionable.");
            }
            value.BringIntoView();
            Capture(window, directory, "04-constraints-stale-synthetic");
        }
        finally
        {
            stateField.SetValue(session, original);
            model.ApplySessionState(original);
        }
    }

    private static void CaptureWorkspace(MainWindow window, string directory)
    {
        MenuItem menu = window.ToolsMenuButton.ContextMenu.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "Engine workspace (preview)"));
        menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        PumpLayout(window);
        EngineWorkspaceView workspace = window.EngineWorkspacePage.Workspace!;
        AllegroEngineSession session = workspace.Session!;
        string operationId = Guid.NewGuid().ToString("D");
        var document = new WorkspaceDocumentIdentity("synthetic-prior-session", 1, 1, null, "offline-fixture.brd", "synthetic");
        session.AdoptUnresolvedOperations([new(operationId, EngineOperationState.Uncertain, document,
            DateTimeOffset.UtcNow, [], EngineRecovery.None)]);
        workspace.Recovery.Refresh();
        try
        {
            SelectTab(workspace, "Recovery");
            var operations = Descendants<ListBox>(workspace).Single(list =>
                AutomationProperties.GetName(list) == "Tracked Engine operations and recovery");
            operations.SelectedIndex = 0;
            Capture(window, directory, "05-recovery-unknown-synthetic");
            SelectTab(workspace, "Support");
            EngineSupportPanel support = Descendants<EngineSupportPanel>(workspace).Single();
            support.RefreshPreview();
            if (support.Snapshot is null || string.IsNullOrWhiteSpace(support.PreviewText))
            {
                throw new InvalidOperationException("Offline support preview did not display the captured diagnostic snapshot.");
            }
            Capture(window, directory, "06-support-offline-synthetic");
        }
        finally
        {
            workspace.Recovery.RecordOperatorAttestation(operationId,
                new(EngineRecoveryResolutionKind.NotApplied,
                    "This is a synthetic test obligation; no native command was dispatched.", DateTimeOffset.UtcNow, "Fixture harness"));
        }
        SelectTab(workspace, "Recovery");
        Capture(window, directory, "07-recovery-attestation-synthetic");
    }

    private static void CaptureSharedReview(string path, string directory)
    {
        var view = new ReviewWorkspaceView();
        var root = new DockPanel();
        var label = new TextBlock { Text = FixtureLabel, Margin = new Thickness(10), Foreground = Brushes.DarkRed };
        DockPanel.SetDock(label, Dock.Top);
        root.Children.Add(label);
        root.Children.Add(view);
        var window = new Window { Title = FixtureLabel, Width = 1300, Height = 850, Content = root };
        try
        {
            window.Show();
            Pump(view.Model.OpenAsync(path));
            view.Model.SelectedFinding = view.Model.Findings[0];
            ComboBox variants = ((DockPanel)view.Content).Children.OfType<ComboBox>().Single();
            variants.SelectedItem = view.Model.Images.Single(item => item.Id == "annotated");
            PumpUntil(() => !view.Model.IsBusy, window.Dispatcher);
            Capture(window, directory, "08-shared-review-offline");
            BitmapSource heldImage = view.Model.Image!;
            variants.SelectedItem = view.Model.Images.Single(item => item.Id == "unavailable");
            PumpUntil(() => !view.Model.IsBusy, window.Dispatcher);
            if (!view.Model.Status.Contains("Synthetic omitted variant", StringComparison.Ordinal) ||
                variants.SelectedValue is not "annotated" || view.Model.ImageId != "annotated" ||
                !ReferenceEquals(heldImage, view.Model.Image))
            {
                throw new InvalidOperationException("Unavailable shared review variant did not display its reason.");
            }
            Capture(window, directory, "09-shared-review-unavailable-variant");
        }
        finally
        {
            Pump(view.DisposeAsync().AsTask());
            window.Close();
        }
    }

    private static ReviewArchiveContent CreateReview()
    {
        DesignScene scene = EngineExamples.CreateBoard("Synthetic offline presentation fixture");
        var tool = new ToolDescriptor("synthetic-review", "Synthetic review", new(1, 0), [DocumentKind.PcbBoard], []);
        var result = new ToolResult([new("synthetic-gap", "Synthetic spacing observation", null, [])],
            AnnotationScene.Empty(scene.Identity.CaptureId), []);
        ReviewArchiveContent content = ReviewDocumentBuilder.FromToolResult(scene, tool, result,
            JsonSerializer.SerializeToElement(new { synthetic = true }), new("PD screenshot fixture", "1", []), DateTimeOffset.UtcNow);
        return content with
        {
            Review = content.Review with
            {
                Findings = [content.Review.Findings[0] with { Measurements = [new("spacing", 12.5m, "mil")] }],
                Images =
                [
                    new("raw", scene.Identity.CaptureId, ReviewImageKind.Raw, ReviewImageAvailability.Available,
                        "images/0001.png", 640, 360, new(0, 0, 640, 360, "mils")),
                    new("annotated", scene.Identity.CaptureId, ReviewImageKind.Annotated, ReviewImageAvailability.Available,
                        "images/0002.png", 640, 360, new(0, 0, 640, 360, "mils")),
                    new("unavailable", scene.Identity.CaptureId, ReviewImageKind.Raw, ReviewImageAvailability.Unavailable,
                        null, null, null, null, "Synthetic omitted variant: this fixture did not record that image."),
                ],
            },
            Images = ImmutableDictionary<string, ImmutableArray<byte>>.Empty
                .Add("images/0001.png", ImageBytes(false)).Add("images/0002.png", ImageBytes(true)),
        };
    }

    private static ImmutableArray<byte> ImageBytes(bool annotated)
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 640, 360));
            var line = new Pen(Brushes.SteelBlue, 10);
            drawing.DrawLine(line, new Point(70, 110), new Point(550, 110));
            drawing.DrawLine(line, new Point(70, 220), new Point(550, 220));
            drawing.DrawText(new FormattedText("SYNTHETIC OFFLINE — " + (annotated ? "ANNOTATED" : "RAW"),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 22, Brushes.Black, 1), new Point(25, 24));
            if (annotated)
            {
                drawing.DrawEllipse(null, new Pen(Brushes.OrangeRed, 8), new Point(310, 165), 80, 75);
                drawing.DrawText(new FormattedText("Review marker: synthetic spacing observation", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 18, Brushes.DarkRed, 1), new Point(25, 300));
            }
        }
        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return [.. buffer.ToArray()];
    }

    private static void Capture(Window window, string directory, string name, string label = FixtureLabel)
    {
        if (window is MainWindow main)
        {
            main.StatusText.Text = label;
        }
        PumpLayout(window);
        Console.WriteLine(label + " IMAGE: " + WindowScreenshot.RenderWindowToFile(window, directory, name));
    }

    private static void SelectTab(EngineWorkspaceView workspace, string header)
    {
        TabItem tab = Descendants<TabControl>(workspace).SelectMany(control => control.Items.OfType<TabItem>())
            .Single(item => Equals(item.Header, header));
        tab.IsSelected = true;
        PumpLayout(workspace);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (T descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Pump(Task task)
    {
        PumpUntil(() => task.IsCompleted, Dispatcher.CurrentDispatcher);
        task.GetAwaiter().GetResult();
    }

    private static void PumpLayout(FrameworkElement element)
    {
        element.UpdateLayout();
        element.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void PumpUntil(Func<bool> completed, Dispatcher dispatcher)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!completed())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The offline screenshot check did not settle within 30 seconds.");
            }
            dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(10);
        }
    }

    private sealed class MemoryPreferences : IPdSimplePreferenceStore
    {
        public PdSimplePreferences Load() => new();
        public void Save(PdSimplePreferences preferences) { }
    }
}
