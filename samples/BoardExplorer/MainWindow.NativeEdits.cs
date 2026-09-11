using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using CircuitHub.AllegroBridge;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Editing;
using CircuitHub.AllegroBridge.Engine.Scenes;
using CircuitHub.AllegroBridge.Wpf;

namespace BoardExplorer;

public partial class MainWindow
{
    private AllegroCanvasHandles? _handles;
    private ImmutableArray<SceneObjectReference> _handleSelection = [];
    private Guid _handleCapture;
    private AllegroPcbObjectEditOperation? _nativeOperation;
    private AllegroPcbObjectEditResult? _lastNativeEdit;
    private bool _nativeDispatchStarted;
    private bool _nativeOutcomeUnknown;
    private bool _rulerMode;
    private DesignPoint _rulerStart;
    private DesignPoint _rulerEnd;

    private void NativeApply_Click(object sender, RoutedEventArgs args)
    {
        if (Placement.Preview is { } plan) { Start(token => ApplyPlacementAsync(plan, token)); }
    }
    private void NativeUndo_Click(object sender, RoutedEventArgs args) => Start(async token =>
    {
        AllegroPcbObjectEditResult previous = _lastNativeEdit ?? throw new InvalidOperationException("No native edit has a retained recovery result.");
        AllegroPcbSession pcb = await CurrentPcbAsync(token);
        await TrackNativeEditAsync(() => pcb.UndoObjectEditAsync(previous, CancellationToken.None));
        // Read failure cannot revoke a verified native result or its recovery.
        await RefreshAfterEditAsync();
    });

    private async Task ApplyPlacementAsync(PlacementPlan plan, CancellationToken token)
    {
        RequireLiveScene();
        if (_nativeOutcomeUnknown) { throw new InvalidOperationException("A prior native outcome is unknown. Inspect Allegro before any further edit."); }
        SceneRead origin = _sceneRead ?? throw new InvalidOperationException("No live-origin scene remains.");
        AllegroPcbSession pcb = await CurrentPcbAsync(token);
        NativeEditStatus.Text = "Comparing fresh native component origins, angles, fixed state and document identity with this preview.";
        AllegroPcbPreparedObjectEdit prepared = await new AllegroPlacementEdits(pcb).PrepareAsync(origin, plan, token);
        token.ThrowIfCancellationRequested();
        await TrackNativeEditAsync(() => prepared.StartAsync(CancellationToken.None));
        await RefreshAfterEditAsync();
    }

    private async Task TrackNativeEditAsync(Func<ValueTask<AllegroPcbObjectEditOperation>> start)
    {
        if (_nativeDispatchStarted || _nativeOutcomeUnknown) { throw new InvalidOperationException("An earlier native outcome must be resolved first."); }
        DisposeHandles();
        _nativeDispatchStarted = true;
        _nativeOutcomeUnknown = true; // Starting may fail after dispatch; no implicit retry is safe.
        try
        {
            _nativeOperation = await start();
            NativeEditStatus.Text = $"Tracking native operation {_nativeOperation.Operation.OperationId}. Cancelling a managed wait cannot discard its result.";
            UpdateActions();
            AllegroPcbObjectEditResult result = await _nativeOperation.WaitForResultAsync(CancellationToken.None);
            // A rejected/cancelled no-op must not discard the preceding edit's
            // recovery receipt. Uncertain outcomes still block every new edit.
            if (result.Mutation != AllegroPcbObjectEditState.ConfirmedNoMutation || _lastNativeEdit is null)
            {
                _lastNativeEdit = result;
            } // Admit first, before any UI or cleanup can throw.
            _nativeOutcomeUnknown = result.Mutation == AllegroPcbObjectEditState.Uncertain;
            NativeEditStatus.Text = $"{result.Receipt.State} / {result.Mutation}: {result.Receipt.Message}\n" +
                (result.CanRecover ? "This exact edit has native Undo/recovery. " : string.Empty) +
                (result.EvidenceError is { } error ? "Evidence error: " + error : "Native readback is not placement or clearance signoff.");
            _sceneBinding = null; // All displayed scene geometry remains historical until reacquired.
            Placement.Clear();
        }
        finally
        {
            if (_nativeOperation is { } operation)
            {
                try { await operation.DisposeAsync(); }
                catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Edit cleanup failed without revoking its admitted result: {0}", error); }
            }
            _nativeOperation = null;
            _nativeDispatchStarted = false;
            UpdateActions();
        }
    }

    private async Task RefreshAfterEditAsync()
    {
        try { await ReadAsync(SceneQuery.Metadata, _lifetime.Token); }
        catch (Exception error)
        {
            Status.Text = "Native result retained. Scene refresh failed; do not replay the edit. " + error.Message;
            DescribeScene();
        }
    }

    private AllegroCanvasHandles CreateHandles()
    {
        RequireLiveScene();
        DisposeHandles();
        _handles = new(_attachment!.Desktop, Dispatcher);
        _handles.Cancelled += (_, reason) => NativeEditStatus.Text = reason;
        _handles.PresentationFailed += (_, error) => NativeEditStatus.Text = "Interactive presentation unavailable: " + error.Message;
        _handles.PreviewChanged += (_, gesture) => PreviewLiveGesture(gesture, completed: false);
        _handles.Completed += (_, gesture) => PreviewLiveGesture(gesture, completed: true);
        return _handles;
    }

    private void ShowHandles_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (_busy || _nativeOutcomeUnknown || _scene is not { } scene) { return; }
            SceneObjectReference[] selected = SceneView.Selection.Where(reference =>
                scene.Data.Components.Any(component => component.Id == reference.ObjectId)).ToArray();
            if (selected.Length is < 1 or > 32) { throw new InvalidOperationException("Select 1–32 placed components first."); }
            ComponentObject[] components = selected.Select(scene.Resolve<ComponentObject>).ToArray();
            if (components.Any(component => component.Position is null || component.Rotation is null || component.IsFixed == true))
            { throw new InvalidOperationException("Selected components must have an acquired, unfixed placement."); }
            AllegroCanvasHandles handles = CreateHandles();
            _rulerMode = false;
            _handleSelection = selected.ToImmutableArray();
            _handleCapture = scene.Identity.CaptureId;
            handles.SetHandles(components.Select(component => new AllegroCanvasHandle(component.Refdes,
                BoardPoint(component.Position!.Value), Label: "Move " + component.Refdes)).ToArray());
            NativeEditStatus.Text = "Drag a visible origin handle. The selected group previews together, snapped to the native grid. Escape cancels. Release applies once only when the checkbox is enabled.";
            UpdateActions();
        }
        catch (Exception error) { NativeEditStatus.Text = error.Message; }
    }

    private void LiveRuler_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (_busy || _scene is not { } scene) { return; }
            DesignPoint[] points = SceneView.Selection.Select(reference => scene.Resolve<ComponentObject>(reference).Position)
                .OfType<DesignPoint>().Take(2).ToArray();
            if (points.Length != 2 || points[0] == points[1]) { throw new InvalidOperationException("Select two placed components to seed the live ruler."); }
            AllegroCanvasHandles handles = CreateHandles();
            _rulerMode = true;
            _handleCapture = scene.Identity.CaptureId;
            _rulerStart = points[0]; _rulerEnd = points[1];
            SetRulerHandles(handles);
            UpdateActions();
        }
        catch (Exception error) { NativeEditStatus.Text = error.Message; }
    }

    private void SetRulerHandles(AllegroCanvasHandles handles)
    {
        handles.SetHandles([new("ruler-start", BoardPoint(_rulerStart), Label: "Ruler start"),
                            new("ruler-end", BoardPoint(_rulerEnd), Label: "Ruler end")]);
        handles.SetDrawing([new([BoardPoint(_rulerStart), BoardPoint(_rulerEnd)], Colors.DodgerBlue, 2)]);
        NativeEditStatus.Text = $"Live advisory ruler: {_rulerStart.DistanceTo(_rulerEnd)}. Move either endpoint. This is not native object snapping or an edit.";
    }

    private void PreviewLiveGesture(AllegroCanvasHandleGesture gesture, bool completed)
    {
        if (_busy || !CanTargetCurrentScene || _scene is not { } scene || scene.Identity.CaptureId != _handleCapture) { return; }
        try
        {
            if (_rulerMode)
            {
                DesignPoint current = Mils(gesture.Current);
                DesignPoint start = gesture.Id == "ruler-start" ? current : _rulerStart;
                DesignPoint end = gesture.Id == "ruler-end" ? current : _rulerEnd;
                _handles!.SetDrawing([new([BoardPoint(start), BoardPoint(end)], Colors.DodgerBlue, 2)]);
                NativeEditStatus.Text = $"Live advisory ruler: {start.DistanceTo(end)}. No edit.";
                if (completed) { _rulerStart = start; _rulerEnd = end; SetRulerHandles(_handles); }
                return;
            }
            DesignVector raw = Mils(gesture.Current) - Mils(gesture.Start);
            decimal quantum = AllegroPlacementEdits.NativeQuantum(scene.Document.NativeUnits, scene.Document.NativePrecision);
            DesignVector snapped = new(decimal.Round(raw.X / quantum, 0, MidpointRounding.AwayFromZero) * quantum,
                decimal.Round(raw.Y / quantum, 0, MidpointRounding.AwayFromZero) * quantum);
            PlacementPlan plan = PlacementPlanner.Translate(scene, _handleSelection, snapped);
            Placement.PresentPreview(plan);
            if (completed && (snapped.X != 0 || snapped.Y != 0) && CommitDrag.IsChecked == true)
            { Start(token => ApplyPlacementAsync(plan, token)); }
        }
        catch (Exception error) { NativeEditStatus.Text = error.Message; }
    }
    private void UpdatePlacementDrawing(PlacementPlan? plan)
    {
        if (_handles is null || _rulerMode) { return; }
        _handles.SetDrawing(plan is null ? [] : plan.Changes.Select(change =>
            new AllegroCanvasPolyline([BoardPoint(change.Before), BoardPoint(change.After)], Colors.DodgerBlue, 2)).ToArray());
    }
    private void HideHandles_Click(object sender, RoutedEventArgs args) { DisposeHandles(); UpdateActions(); }
    private void DisposeHandles()
    {
        _handles?.Dispose();
        _handles = null;
        _handleSelection = [];
        _handleCapture = Guid.Empty;
    }
    private static AllegroBoardPoint BoardPoint(DesignPoint point) => new(point.X, point.Y, "mils");
    private static DesignPoint Mils(AllegroBoardPoint point)
    {
        decimal scale = point.Units switch { "mils" => 1, "millimeters" => 1m / 0.0254m, "inches" => 1000, _ => throw new ArgumentException("Unsupported native units.") };
        return new(point.X * scale, point.Y * scale);
    }
}
