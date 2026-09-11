using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CircuitHub.AllegroBridge;
using CircuitHub.AllegroBridge.Engine.Analysis;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Editing;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Interactions;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Windows;
using CircuitHub.AllegroBridge.Wpf;
using CircuitHub.AllegroBridge.Wpf.Engine;
using DesignLine = CircuitHub.AllegroBridge.Engine.Geometry.LineGeometry;

namespace BoardExplorer;

/// <summary>Public-package engine consumer. No custom native code, raw DBIDs, or authoring studio.</summary>
public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private AllegroDesktopAttachment? _attachment;
    private AllegroPcbSession? _pcb;
    private DesignScene? _scene;
    private AllegroSessionBinding? _sceneBinding;
    private DesignBounds? _analysisBounds;
    private bool _busy;
    private bool _closed;
    private bool _closeReady;
    private Task _pending = Task.CompletedTask;

    public MainWindow()
    {
        InitializeComponent();
        SceneView.SelectionChanged += (_, selection) =>
        {
            Placement.SetSelection(_scene, selection.Objects.Where(reference =>
                _scene?.Data.Components.Any(component => component.Id == reference.ObjectId) == true));
        };
        SceneView.GestureCompleted += SceneGestureCompleted;
        SceneView.AnnotationDragged += (_, gesture) =>
        {
            if (_scene is null || SceneView.Annotations is not { } annotations)
            {
                return;
            }
            DesignVector delta = gesture.Gesture.Current - gesture.Gesture.Start;
            Annotation[] moved = annotations.Items.Select(item => item.Id == gesture.AnnotationId
                ? item with { Geometry = GeometryTransforms.Translate(item.Geometry, delta) } : item).ToArray();
            SceneView.SetAnnotations(annotations.Replace(moved));
        };
        Placement.PreviewChanged += (_, preview) => SceneView.SetAnnotations(preview.Annotations);
        UpdateActions();
        Closing += async (_, args) =>
        {
            if (_closeReady)
            {
                return;
            }
            args.Cancel = true;
            if (_closed)
            {
                return;
            }
            _closed = true;
            _lifetime.Cancel();
            SceneView.CancelGesture();
            try
            {
                await _pending;
                if (_attachment is not null)
                {
                    _attachment.Session.ContextChanged -= ContextChanged;
                    await _attachment.DisposeAsync();
                }
            }
            finally
            {
                _lifetime.Dispose();
                _closeReady = true;
                Close();
            }
        };
    }

    private void Discover_Click(object sender, RoutedEventArgs args) => Start(async token =>
    {
        var candidates = await Task.Run(() => AllegroDesktop.DiscoverRunningInstances(cancellationToken: token), token);
        Boards.ItemsSource = candidates.Select(candidate => new BoardChoice(candidate)).ToArray();
        Status.Text = candidates.Count == 0
            ? "No compatible Bridge was discovered. Start the matching Bridge in the intended Allegro application."
            : "Select the intended board. Connect verifies the exact resident and native window.";
    });

    private void Connect_Click(object sender, RoutedEventArgs args) => Start(async token =>
    {
        if (Boards.SelectedItem is not BoardChoice selected || !selected.Candidate.CanAttemptAttach)
        {
            throw new InvalidOperationException("Select a board with a compatible Bridge resident.");
        }
        AllegroDesktopAttachment? next = await AllegroDesktop.AttachAsync(selected.Candidate, cancellationToken: token);
        try
        {
            AllegroPcbSession pcb = await next.Session.OpenPcbAsync(token);
            SceneRead read = await new AllegroDesignSource(pcb).AcquireAsync(SceneQuery.Metadata, token);
            DesignScene scene = read.RequireScene();
            token.ThrowIfCancellationRequested();
            AllegroDesktopAttachment? previous = _attachment;
            if (previous is not null)
            {
                previous.Session.ContextChanged -= ContextChanged;
            }
            _attachment = next;
            _pcb = pcb;
            next.Session.ContextChanged += ContextChanged;
            next = null;
            Adopt(scene, read.NativeReceipt?.Binding);
            if (previous is not null)
            {
                await previous.DisposeAsync();
            }
        }
        finally
        {
            if (next is not null)
            {
                await next.DisposeAsync();
            }
        }
    });

    private void Refresh_Click(object sender, RoutedEventArgs args) => Start(token => ReadAsync(SceneQuery.Metadata, token));
    private void Acquire_Click(object sender, RoutedEventArgs args) => Start(token =>
        ReadAsync(SceneQuery.CompleteBoard(IncludeContours.IsChecked == true), token));

    private async Task ReadAsync(SceneQuery query, CancellationToken token)
    {
        AllegroPcbSession pcb = await CurrentPcbAsync(token);
        Status.Text = query.Kind == SceneReadKind.Metadata ? "Reading lightweight metadata." :
            "Acquiring one bounded native scene. Pages are not independently mixed; expensive details are explicit.";
        SceneRead read = await new AllegroDesignSource(pcb).AcquireAsync(query, token);
        token.ThrowIfCancellationRequested();
        Adopt(read.RequireScene(), read.NativeReceipt?.Binding);
    }

    private async Task<AllegroPcbSession> CurrentPcbAsync(CancellationToken token)
    {
        AllegroBridgeSession session = _attachment?.Session ?? throw new InvalidOperationException("Connect to a board first.");
        if (!session.Context.IsConnected || !session.Context.IsCompatible)
        {
            throw new InvalidOperationException("The native connection is unavailable. Select and verify the intended board again.");
        }
        if (_pcb is null || !_pcb.IsCurrent)
        {
            _pcb = await session.OpenPcbAsync(token);
        }
        return _pcb;
    }

    private void ContextChanged(object? sender, AllegroBoardContext context) => Dispatcher.BeginInvoke(() =>
    {
        if (_closed || !ReferenceEquals(sender, _attachment?.Session))
        {
            return;
        }
        if (_sceneBinding is { } binding && (!context.IsConnected || !context.IsCompatible || !SameDocument(binding, context.Binding)))
        {
            _sceneBinding = null;
            SceneView.CancelGesture();
            Review.Clear("The native document or connection changed. The captured design scene remains historical.");
            DescribeScene();
            Status.Text = "Live targeting was invalidated. Saved results remain historical; no operation was replayed.";
            UpdateActions();
        }
    });

    private static bool SameDocument(AllegroSessionBinding first, AllegroSessionBinding second) =>
        first.SessionId == second.SessionId && first.SessionGeneration == second.SessionGeneration &&
        first.BoardGeneration == second.BoardGeneration && first.ProcessId == second.ProcessId &&
        first.ResidentPackage == second.ResidentPackage && first.ProtocolVersion == second.ProtocolVersion;

    private bool CanTargetCurrentScene => _sceneBinding is { } binding &&
        _attachment?.Session is { Context.IsConnected: true, Context.IsCompatible: true } session &&
        SameDocument(binding, session.Binding);

    private void Adopt(DesignScene scene, AllegroSessionBinding? binding)
    {
        _scene = scene;
        _sceneBinding = binding;
        _analysisBounds = null;
        SceneView.SetScene(scene);
        AnalysisLayer.ItemsSource = scene.Layers.Items;
        AnalysisLayer.SelectedItem = scene.Layers.Items.FirstOrDefault(layer => layer.IsActive == true && !layer.IsNegative)
            ?? scene.Layers.Items.FirstOrDefault(layer => !layer.IsNegative);
        Findings.ItemsSource = null;
        AnalysisStatus.Text = "Draw an analysis rectangle. No analysis has run.";
        Review.Clear("A different scene was selected.");
        DescribeScene();
        ApplyFilter();
        UpdateActions();
    }

    private void DescribeScene()
    {
        if (_scene is not { } scene)
        {
            return;
        }
        string origin = scene.Identity.Provenance.IsOffline ? "OFFLINE" : CanTargetCurrentScene ? "CAPTURED FROM CONNECTED DOCUMENT" : "HISTORICAL";
        FamilyCoverage copper = scene.Coverage[DataFamily.Copper];
        SceneStatus.Text = $"{origin} | {scene.Document.Name} | {scene.Identity.CapturedAt:O}\n" +
            $"Copper: {copper.Availability}, {copper.Completeness}. Kind coverage: {string.Join(", ", scene.CopperScope.Kinds)}. " +
            "A scene is immutable captured data, never live edit authority.";
    }

    private void Open_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Filter = "Allegro scene (*.allegroscene)|*.allegroscene", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            Start(async token => Adopt(await SceneArchive.LoadAsync(dialog.FileName, token), null));
        }
    }
    private void Save_Click(object sender, RoutedEventArgs args)
    {
        if (_scene is not { } scene)
        {
            return;
        }
        var dialog = new SaveFileDialog { Filter = "Allegro scene (*.allegroscene)|*.allegroscene", DefaultExt = ".allegroscene",
            AddExtension = true, FileName = "captured-scene.allegroscene", OverwritePrompt = true };
        if (dialog.ShowDialog(this) == true)
        {
            Start(async token =>
            {
                await SceneArchive.SaveAsync(dialog.FileName, scene, overwrite: true, cancellationToken: token);
                Status.Text = "Saved captured design data with coverage and units. No native witnesses or executable extensions are stored.";
            });
        }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs args) => ApplyFilter();
    private void Search_Changed(object sender, TextChangedEventArgs args) => ApplyFilter();
    private void ApplyFilter()
    {
        if (Objects is null || Search is null || _scene is not { } scene)
        {
            return;
        }
        (DataFamily family, IEnumerable<Entry> values) = Family.SelectedIndex switch
        {
            0 => (DataFamily.Components, scene.Components.Items.Select(item => new Entry(item.Refdes, item,
                new AllegroPcbDisplayTarget.Component(item.Refdes)))),
            1 => (DataFamily.Nets, scene.Nets.Items.Select(item => new Entry(item.Name, item, new AllegroPcbDisplayTarget.Net(item.Name)))),
            2 => (DataFamily.Connectivity, scene.DifferentialPairs.Items.Select(item => new Entry(item.Name, item,
                new AllegroPcbDisplayTarget.DifferentialPair(item.Name)))),
            3 => (DataFamily.Layers, scene.Layers.Items.Select(item => new Entry(item.Id.Value, item, null))),
            _ => (DataFamily.Pins, scene.Pins.Items.Select(item => new Entry(item.ComponentRefdes + "." + item.Number, item, null)))
        };
        FamilyCoverage coverage = scene.Coverage[family];
        Entry[] visible = values.Where(item => item.Name.Contains(Search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
        Objects.ItemsSource = visible;
        Details.Text = coverage.Availability == DataAvailability.Available
            ? "Select an object. All relationships shown here come from captured, explicitly acquired data."
            : $"This family is {coverage.Availability}. That does not mean there are no objects.";
        Status.Text = $"{visible.Length} matching objects | {coverage.Availability} | {coverage.Completeness}. " + string.Join(" ", coverage.Reasons);
        UpdateActions();
    }

    private void Object_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_scene is { } scene && Objects.SelectedItem is Entry entry)
        {
            var text = new StringBuilder(JsonSerializer.Serialize(entry.Value, entry.Value.GetType(), new JsonSerializerOptions { WriteIndented = true }));
            if (entry.Value is NetObject net && scene.Coverage[DataFamily.Connectivity].IsComplete)
            {
                foreach (XnetObject xnet in scene.Relations.XnetsOf(net.Name))
                {
                    text.AppendLine($"\nXnet {xnet.Name}: {string.Join(", ", xnet.PhysicalNets)}");
                }
                foreach (DifferentialPairObject pair in scene.Relations.PairsOf(net.Name))
                {
                    text.AppendLine($"Declared pair: {pair.Name}, sides {pair.SideAXnet} / {pair.SideBXnet}.");
                }
            }
            if (entry.Value is DifferentialPairObject selectedPair)
            {
                text.AppendLine("\nSide A/B does not establish positive/negative polarity.");
                if (scene.Coverage[DataFamily.Connectivity].IsComplete)
                {
                    text.AppendLine("Physical members: " + string.Join(", ", scene.Relations.PhysicalNetsOf(selectedPair)));
                }
            }
            if (entry.Value is ComponentObject component)
            {
                SceneView.Select([scene.ReferenceTo(component.Id)]);
            }
            Details.Text = text.ToString();
        }
        UpdateActions();
    }

    private void Pins_Click(object sender, RoutedEventArgs args) => Start(async token =>
    {
        if (_scene is not { } scene || Objects.SelectedItem is not Entry { Value: ComponentObject component })
        {
            return;
        }
        DesignScene detail = scene;
        if (!scene.Coverage[DataFamily.Pins].IsComplete ||
            !scene.Query.Components.IsEmpty && !scene.Query.Components.Contains(component.Refdes))
        {
            RequireLiveScene();
            AllegroPcbSession pcb = await CurrentPcbAsync(token);
            detail = (await new AllegroDesignSource(pcb).AcquireAsync(SceneQuery.InspectComponent(component.Refdes), token)).RequireScene();
        }
        var text = new StringBuilder($"{component.Refdes}: pins captured at {detail.Identity.CapturedAt:O}.\n");
        foreach (PinObject pin in detail.Relations.PinsOf(component.Refdes))
        {
            text.AppendLine($"{pin.Number}: {pin.NetName ?? "Disconnected"}");
            if (pin.NetName is { } net && detail.Coverage[DataFamily.Connectivity].IsComplete)
            {
                foreach (DifferentialPairObject pair in detail.Relations.PairsOf(net))
                {
                    text.AppendLine($"  Declared pair: {pair.Name}");
                }
            }
        }
        text.AppendLine("\nA fresh pin inspection is not merged with older copper as one coherent scene.");
        Details.Text = text.ToString();
    });

    private void Graph_Click(object sender, RoutedEventArgs args) => Start(async token =>
    {
        if (_scene is not { } scene || Objects.SelectedItem is not Entry { Value: NetObject net })
        {
            return;
        }
        RouteGraph graph = await Task.Run(() => RouteGraph.Build(scene, net.Name, token), token);
        Details.Text = $"{net.Name}\nStored centerline length: {graph.TotalTraceCenterlineLength}\n" +
            $"Nodes: {graph.Nodes.Length}; edges: {graph.Edges.Length}; branches: {graph.Branches.Length}\n\n" +
            string.Join("\n", graph.Diagnostics) + "\n\nUse ShortestStoredPath with explicitly selected graph nodes for path-specific length.";
    });

    private void Highlight_Click(object sender, RoutedEventArgs args) => Start(token => DisplayAsync(false, token));
    private void Zoom_Click(object sender, RoutedEventArgs args) => Start(token => DisplayAsync(true, token));
    private async Task DisplayAsync(bool zoom, CancellationToken token)
    {
        RequireLiveScene();
        AllegroPcbDisplayTarget target = (Objects.SelectedItem as Entry)?.Target ?? throw new InvalidOperationException("Select a named display target.");
        AllegroPcbSession pcb = await CurrentPcbAsync(token);
        ShowReceipt(zoom ? await pcb.ZoomAsync(target, token) : await pcb.HighlightAsync(target, token));
        Status.Text += " This is a fresh named lookup, not validation that historical geometry is unchanged.";
    }
    private void Clear_Click(object sender, RoutedEventArgs args) => Start(async token =>
    {
        RequireLiveScene();
        ShowReceipt(await (await CurrentPcbAsync(token)).ClearHighlightAsync(token));
    });
    private void RequireLiveScene()
    {
        if (!CanTargetCurrentScene)
        {
            throw new InvalidOperationException("The displayed scene has no current native binding. Reacquire from the intended board; offline IDs cannot target native work.");
        }
    }
    private void ShowReceipt(AllegroOperationReceipt receipt)
    {
        Status.Text = $"{receipt.State}: {receipt.Message}";
        if (receipt.State != AllegroOperationState.Complete)
        {
            throw new InvalidOperationException(Status.Text + " No action was replayed.");
        }
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (SceneView is not null)
        {
            SceneView.CancelGesture();
            SceneView.InteractionMode = (SceneInteractionMode)Math.Max(0, Mode.SelectedIndex);
        }
    }
    private void Snap_Changed(object sender, RoutedEventArgs args)
    {
        if (SceneView is not null)
        {
            SceneView.SnapGrid = SnapEnabled.IsChecked == true ? new Length(1) : null;
        }
    }
    private void Fit_Click(object sender, RoutedEventArgs args) => SceneView.Fit();
    private void ClearAnnotations_Click(object sender, RoutedEventArgs args)
    {
        Placement.Clear();
        SceneView.SetAnnotations(_scene is null ? null : AnnotationScene.Empty(_scene.Identity.CaptureId));
    }
    private void SceneGestureCompleted(object? sender, GesturePreview gesture)
    {
        if (_scene is not { } scene || scene.Identity.CaptureId != gesture.CaptureId)
        {
            return;
        }
        if (SceneView.InteractionMode == SceneInteractionMode.RectangleSelect)
        {
            _analysisBounds = DesignBounds.FromPoints([gesture.Start, gesture.Current]);
            if (_analysisBounds.Value.Width == 0 || _analysisBounds.Value.Height == 0)
            {
                _analysisBounds = null;
                AnalysisStatus.Text = "Draw a nonzero-area rectangle.";
                return;
            }
            SceneView.SetAnnotations(AnnotationScene.Empty(scene.Identity.CaptureId).Replace([
                new("analysis-region", Rectangle(_analysisBounds.Value), AnnotationRole.Guide, "Analysis region") ]));
            AnalysisStatus.Text = "Rectangle selected. Choose the layer and analysis representation.";
        }
        else if (SceneView.InteractionMode == SceneInteractionMode.TranslatePreview)
        {
            try
            {
                PlacementPlan plan = PlacementPlanner.Translate(scene, SceneView.Selection, gesture.Current - gesture.Start);
                SceneView.SetAnnotations(AnnotationScene.Empty(scene.Identity.CaptureId).Replace(plan.Changes.Select(change =>
                    new Annotation("move-" + change.Target.ObjectId.Value, new DesignLine(change.Before, change.After),
                        AnnotationRole.Preview, change.Refdes, Source: change.Target))));
                Status.Text = string.Join(" ", plan.Diagnostics) + " No native edit was dispatched.";
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                Status.Text = error.Message;
            }
        }
        else
        {
            SceneView.SetAnnotations(AnnotationScene.Empty(scene.Identity.CaptureId).Replace([
                new("ruler", new DesignLine(gesture.Start, gesture.Current), AnnotationRole.Guide,
                    gesture.Start.DistanceTo(gesture.Current).ToString(), AnnotationHitBehavior.Drag) ]));
            Status.Text = "Captured-scene ruler. Drag its handle or measure another span; no native operation is dispatched.";
        }
        UpdateActions();
    }

    private void Analyze_Click(object sender, RoutedEventArgs args) => Start(async token =>
    {
        DesignScene scene = _scene ?? throw new InvalidOperationException("Load a scene first.");
        DesignBounds region = _analysisBounds ?? throw new InvalidOperationException("Draw an analysis rectangle first.");
        LayerObject layer = AnalysisLayer.SelectedItem as LayerObject ?? throw new InvalidOperationException("Choose a captured layer.");
        var query = new CrossingQuery("rectangle-crossing-v1", layer.Id, Rectangle(region), [],
            UseCopperArea.IsChecked == true ? CrossingRepresentation.CopperArea : CrossingRepresentation.TraceCenterline);
        CrossingAnalysis result = await Task.Run(() => CrossingAnalyzer.Analyze(scene, query, token), token);
        if (!ReferenceEquals(scene, _scene))
        {
            return;
        }
        Findings.ItemsSource = result.Findings.Select(finding => new FindingEntry(finding)).ToArray();
        AnalysisStatus.Text = $"{(result.CompleteForRequestedRepresentation ? "COMPLETE FOR REPRESENTATION" : "INDETERMINATE")}\n" +
            $"Nets: {result.InterferingNetCount}; objects: {result.InterferingObjectCount}; locations: {result.CrossingLocationCount}; " +
            $"unassigned objects: {result.UnassignedObjectCount}.\n" +
            (result.FindingsTruncated ? "Finding list is limited; totals remain separate.\n" : string.Empty) +
            string.Join("\n", result.Diagnostics);
        Status.Text = result.IsClear ? "No intersections in the completed requested representation. This is not native DRC or SI signoff."
            : "Review the coverage-qualified findings. Incomplete input is never reported as clear.";
    });
    private void Finding_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_scene is not { } scene || Findings.SelectedItem is not FindingEntry selected)
        {
            return;
        }
        SceneView.SetAnnotations(AnnotationScene.Empty(scene.Identity.CaptureId).Replace(selected.Finding.Witnesses.Select((point, index) =>
            new Annotation("finding-" + index, new PointGeometry(point), AnnotationRole.Finding,
                selected.Finding.NetName ?? "Unassigned", AnnotationHitBehavior.Click, selected.Finding.Object))));
    }
    private static PolygonGeometry Rectangle(DesignBounds bounds) => new([
        bounds.Minimum, new(bounds.Maximum.X, bounds.Minimum.Y), bounds.Maximum, new(bounds.Minimum.X, bounds.Maximum.Y)], []);

    private void Capture_Click(object sender, RoutedEventArgs args) => Start(async token =>
    {
        RequireLiveScene();
        AllegroDesktopAttachment attachment = _attachment!;
        AllegroSessionBinding binding = attachment.Session.Binding;
        try
        {
            AllegroCanvasPixelCapture capture = await AllegroCanvasCapture.CaptureAsync(attachment.Session, attachment.Desktop, token);
            if (!CanTargetCurrentScene || !SameDocument(binding, attachment.Session.Binding))
            {
                throw new InvalidOperationException("The native document changed during review capture.");
            }
            AnnotationScene annotations = SceneView.Annotations ?? AnnotationScene.Empty(_scene!.Identity.CaptureId);
            Review.Present(AllegroReviewFrame.Compose(capture, _scene!, annotations));
            ReviewProvenance.Text = $"Annotations from scene {_scene!.Identity.CaptureId:N}, captured {_scene.Identity.CapturedAt:O}. " +
                "Their geometry is historical, not revalidated by a screenshot.";
        }
        catch (Exception error)
        {
            Review.ReportRefreshFailure(error.Message);
            throw;
        }
    });
    private void SaveImage_Click(object sender, RoutedEventArgs args)
    {
        if (Review.Frame is not { } frame)
        {
            return;
        }
        var dialog = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", DefaultExt = ".png", AddExtension = true,
            FileName = "allegro-review.png", OverwritePrompt = true };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                using var output = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
                frame.SavePng(output);
                Status.Text = "Saved the composed historical review image.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Status.Text = "Image export failed: " + error.Message;
            }
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs args)
    {
        if (_scene is null || Objects.SelectedItem is not Entry entry)
        {
            return;
        }
        string name = JsonSerializer.Serialize(entry.Name);
        string query = entry.Value switch
        {
            ComponentObject => $"SceneQuery.InspectComponent({name})",
            _ => "SceneQuery.Metadata"
        };
        string lookup = entry.Value switch
        {
            ComponentObject => $"var component = scene.Relations.FindComponent({name});",
            NetObject => $"var net = scene.Relations.FindNet({name});",
            DifferentialPairObject => $"var pair = scene.DifferentialPairs.RequireComplete().Single(value => value.Name == {name});",
            LayerObject => $"var layer = scene.Layers.RequireComplete().Single(value => value.Id.Value == {name});",
            PinObject pin => $"var pin = scene.Relations.PinsOf({JsonSerializer.Serialize(pin.ComponentRefdes)}).Single(value => value.Number == {JsonSerializer.Serialize(pin.Number)});",
            _ => throw new InvalidOperationException("No recipe for this object type.")
        };
        if (entry.Value is PinObject selectedPin)
        {
            query = $"SceneQuery.InspectComponent({JsonSerializer.Serialize(selectedPin.ComponentRefdes)})";
        }
        string code = "using CircuitHub.AllegroBridge;\nusing CircuitHub.AllegroBridge.Engine.Scenes;\nusing System.Linq;\n\n" +
            (CanTargetCurrentScene
                ? "// session is an explicitly connected SDK session.\nvar pcb = await session.OpenPcbAsync(cancellationToken);\n" +
                  $"var scene = (await new AllegroDesignSource(pcb).AcquireAsync({query}, cancellationToken)).RequireScene();\n"
                : "// scenePath is a saved capture chosen by the operator.\nvar scene = await SceneArchive.LoadAsync(scenePath, cancellationToken);\n") + lookup;
        try
        {
            Clipboard.SetText(code);
            Status.Text = "Copied a public Engine API recipe. Object names are escaped C# string literals, not executable instructions.";
        }
        catch (System.Runtime.InteropServices.COMException error)
        {
            Details.Text = code;
            Status.Text = "The clipboard is busy; the code is shown in the details pane. " + error.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs args) => _operation?.Cancel();
    private void Start(Func<CancellationToken, Task> action)
    {
        if (_busy || _closed)
        {
            return;
        }
        _busy = true;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        UpdateActions();
        _pending = RunAsync(action, _operation);
    }
    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationTokenSource operation)
    {
        try
        {
            await action(operation.Token);
        }
        catch (Exception error)
        {
            if (!_closed)
            {
                Status.Text = error is OperationCanceledException
                    ? "The client wait ended. Any already dispatched native work was not cancelled or replayed."
                    : error.Message;
            }
        }
        finally
        {
            operation.Dispose();
            _operation = null;
            _busy = false;
            if (!_closed)
            {
                UpdateActions();
            }
        }
    }
    private void UpdateActions()
    {
        if (ConnectButton is null)
        {
            return;
        }
        bool idle = !_busy && !_closed;
        bool connected = idle && _attachment?.Session is { Context.IsConnected: true, Context.IsCompatible: true };
        bool liveScene = idle && CanTargetCurrentScene;
        ConnectButton.IsEnabled = DiscoverButton.IsEnabled = Boards.IsEnabled = OpenButton.IsEnabled = idle;
        RefreshButton.IsEnabled = AcquireButton.IsEnabled = connected;
        SaveButton.IsEnabled = idle && _scene is not null;
        Family.IsEnabled = Search.IsEnabled = Objects.IsEnabled = idle;
        CancelButton.IsEnabled = _busy && !_closed;
        PinsButton.IsEnabled = idle && Objects.SelectedItem is Entry { Value: ComponentObject } &&
            (liveScene || _scene?.Coverage[DataFamily.Pins].IsComplete == true);
        HighlightButton.IsEnabled = ZoomButton.IsEnabled = liveScene && Objects.SelectedItem is Entry { Target: not null };
        ClearButton.IsEnabled = CaptureButton.IsEnabled = liveScene;
        SaveImageButton.IsEnabled = idle && Review.Frame is not null;
        GraphButton.IsEnabled = idle && Objects.SelectedItem is Entry { Value: NetObject } && _scene?.Coverage[DataFamily.Copper].IsComplete == true;
        CopyButton.IsEnabled = idle && Objects.SelectedItem is Entry;
        AnalyzeButton.IsEnabled = idle && _analysisBounds is not null && _scene?.Coverage[DataFamily.Copper].Availability == DataAvailability.Available;
    }
    private sealed record Entry(string Name, object Value, AllegroPcbDisplayTarget? Target);
    private sealed record FindingEntry(CrossingFinding Finding)
    {
        public string Label => $"{Finding.NetName ?? "Unassigned"} | {Finding.Object.ObjectId.Value} | {Finding.Witnesses.Length} witness(es)";
    }
    private sealed record BoardChoice(AllegroDesktopCandidate Candidate)
    {
        public string Label => $"{Candidate.Design ?? "Unknown board"} | process {Candidate.ProcessId} | {Candidate.UnavailableReason ?? "Verify on connection"}";
    }
}
