using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CircuitHub.AllegroBridge;
using CircuitHub.AllegroBridge.Windows;
using CircuitHub.AllegroBridge.Wpf;
using PD.Simple.Corridor;

namespace PD.Simple;

public sealed record InteractiveRoutePickSummary(
    string ObjectType,
    string Location);

public sealed record InteractiveRouteInteractionState(
    bool IsActive,
    bool HasFirstPick,
    string PhaseTitle,
    string Detail,
    InteractiveRoutePickSummary? StartPick = null,
    InteractiveRoutePickSummary? EndPick = null)
{
    public static InteractiveRouteInteractionState Inactive
    {
        get;
    } =
        new(false, false, "Interactive Route inactive", string.Empty);
}

internal sealed class BoardOverlayController : IDisposable
{
    private static readonly TimeSpan PointerFreshness = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CompletionDuration = TimeSpan.FromMilliseconds(1800);

    private readonly AllegroDesktopBinding _binding;
    private readonly AllegroBridgeSession _session;
    private readonly BoardOverlayWindow _window;
    private readonly AllegroCanvasViewObserver _observer;
    private readonly DispatcherTimer _renderTimer;
    private readonly InteractiveRouteOverlayFeedbackState _feedback = new();
    private AllegroBoardPoint? _firstPick;
    private AllegroBoardPoint? _secondPick;
    private AllegroInteractionObjectKind _firstPickObjectKind =
        AllegroInteractionObjectKind.None;
    private AllegroInteractionObjectKind _secondPickObjectKind =
        AllegroInteractionObjectKind.None;
    private AllegroCanvasView? _canvasView;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private int _canvasObservationPauseDepth;
    private long _observationInvalidation = -1;
    private bool _renderingFailed;
    private readonly AllegroRenderRecovery _renderRecovery = new();
    private long _renderRetryAfter;
    private long _reportedObservationFailure = -1;
    private DateTimeOffset _completionVisibleUntil;
    private long _expectedBoardGeneration;
    private bool _active;
    private bool _disposed;
    private bool _ownerLossPublished;
    private DpViaCorridorBoardOverlay? _dpViaCorridorOverlay;
    private AllegroBoardPoint[] _overlayBoardPoints = [];
    private AllegroScreenPoint[] _overlayScreenPoints = [];
    private AllegroCanvasView? _overlayRenderedView;
    private AllegroCanvasDrawingFrame? _corridorFrame;
    private BitmapSource? _corridorHudBitmap;

    internal BoardOverlayController(AllegroDesktopBinding binding, AllegroBridgeSession session)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _window = new BoardOverlayWindow(binding.TopLevelWindowHandle);
        _observer = binding.ObserveCanvasViews();
        _binding.Invalidated += BindingOnInvalidated;
        _renderTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(33),
            DispatcherPriority.Render,
            Render,
            Dispatcher.CurrentDispatcher);
    }

    internal event EventHandler<InteractiveRouteInteractionState>? StateChanged;
    internal event EventHandler? OwnerLost;
    internal event EventHandler<string>? FeedbackRejected;
    internal event EventHandler<Exception>? RenderingFailed;
    internal event EventHandler<Exception>? ObservationFailed;
    internal AllegroCanvasStatus? CanvasStatus { get; private set; }

    internal InteractiveRouteInteractionState State
    {
        get; private set;
    } =
        InteractiveRouteInteractionState.Inactive;

    internal bool DpViaCorridorOverlayVisible => _dpViaCorridorOverlay is not null && _window.IsVisible;
    internal nint OverlayWindowHandle => new WindowInteropHelper(_window).Handle;
    internal IReadOnlyList<AllegroScreenPoint> DpViaCorridorProjectedPoints =>
        DpViaCorridorOverlayVisible ? Array.AsReadOnly(_overlayScreenPoints) : Array.Empty<AllegroScreenPoint>();
    internal IReadOnlyList<Point> DpViaCorridorRenderedAnchorPoints =>
        DpViaCorridorOverlayVisible ? _window.DpViaCorridorRenderedAnchorPoints : Array.Empty<Point>();
    internal AllegroScreenRect? DpViaCorridorCanvasBounds =>
        DpViaCorridorOverlayVisible ? _overlayRenderedView?.Canvas?.Bounds : null;
    internal uint? DpViaCorridorCanvasDpi =>
        DpViaCorridorOverlayVisible ? _overlayRenderedView?.Canvas?.Dpi : null;

    internal async Task<T> WithCanvasObservationPausedAsync<T>(Func<Task<T>> capture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(capture);
        _window.Dispatcher.VerifyAccess();
        _canvasObservationPauseDepth++;
        _renderTimer.Stop();
        _window.HideOverlay();
        bool acquired = false;
        try
        {
            // Pause presentation, not the SDK's shared renewal worker. Raw
            // captures consume that worker's inspection rather than starting
            // a competing accessibility inspection.
            await _captureGate.WaitAsync();
            acquired = true;

            ObjectDisposedException.ThrowIf(_disposed, this);
            return await capture();
        }
        finally
        {
            if (acquired)
            {
                _captureGate.Release();
            }

            _canvasObservationPauseDepth--;
            if (_canvasObservationPauseDepth == 0)
            {
                if (_disposed)
                {
                    _captureGate.Dispose();
                }
                else
                {
                    // Never reuse projection evidence across another canvas
                    // observation, or restore a model that selection changes
                    // have already dismissed. The next tick uses current state.
                    _canvasView = null;
                    _overlayRenderedView = null;
                    if (_binding.IsValid && _binding.IsCurrentFor(_session.Binding) &&
                        _expectedBoardGeneration == _session.Binding.BoardGeneration &&
                        (_active || _dpViaCorridorOverlay is not null ||
                            DateTimeOffset.UtcNow <= _completionVisibleUntil))
                    {
                        _renderTimer.Start();
                    }
                }
            }
        }
    }

    internal void Begin(long boardGeneration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (boardGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(boardGeneration));
        }
        _active = true;
        _window.HideOverlay();
        _dpViaCorridorOverlay = null;
        _overlayBoardPoints = [];
        _overlayScreenPoints = [];
        _overlayRenderedView = null;
        _renderingFailed = false;
        _reportedObservationFailure = -1;
        _expectedBoardGeneration = boardGeneration;
        _feedback.Begin();
        _firstPick = null;
        _secondPick = null;
        _firstPickObjectKind = AllegroInteractionObjectKind.None;
        _secondPickObjectKind = AllegroInteractionObjectKind.None;
        _canvasView = null;
        _completionVisibleUntil = default;
        _ownerLossPublished = false;
        State = new(
            true,
            false,
            "Choose the first object",
            "Move over Allegro. Green can be selected; red cannot.");
        StateChanged?.Invoke(this, State);
        _renderTimer.Start();
    }

    internal void SetDpViaCorridorOverlay(DpViaCorridorBoardOverlay? overlay)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_dpViaCorridorOverlay, overlay))
        {
            return;
        }
        // Dismissing a DPVC annotation must never end another feature's overlay.
        if (overlay is null)
        {
            if (_dpViaCorridorOverlay is not null)
            {
                End();
            }

            return;
        }
        if (_active)
        {
            throw new InvalidOperationException("Finish Interactive Route before showing a captured DP corridor.");
        }

        var binding = _session.Binding;
        var snapshot = _session.ProtocolSnapshot;
        if (!overlay.IsCurrentFor(binding, snapshot) || !_binding.IsCurrentFor(binding))
        {
            throw new InvalidOperationException("The captured DP corridor does not belong to the connected Allegro canvas.");
        }

        var units = snapshot.Units;
        var multiplier = units.ToLowerInvariant() switch
        {
            "mils" => 1m,
            "millimeters" => 0.0254m,
            _ => throw new InvalidOperationException("DP corridor overlays require native mil or millimeter units.")
        };
        AllegroBoardPoint[] points;
        try
        {
            // Analysis coordinates are canonical mils. Convert exactly once to
            // the current SDK native point units; projection stays SDK-owned.
            points = overlay.GetPoints().Select(point => new AllegroBoardPoint(
                checked((decimal)point.XMil * multiplier), checked((decimal)point.YMil * multiplier), units)).ToArray();
        }
        catch (OverflowException error)
        {
            throw new InvalidOperationException("The captured coordinates exceed the native projection range.", error);
        }
        End();
        _dpViaCorridorOverlay = overlay;
        _overlayBoardPoints = points;
        _expectedBoardGeneration = overlay.Zoom.BoardGeneration;
        _renderingFailed = false;
        _reportedObservationFailure = -1;
        _renderTimer.Start();
        Render(this, EventArgs.Empty);
    }

    internal void Apply(AllegroInteractionFeedback feedback)
    {
        if (_disposed || !_active)
        {
            return;
        }
        if (feedback.BoardGeneration != _expectedBoardGeneration)
        {
            End();
            FeedbackRejected?.Invoke(
                this,
                "Interactive Route feedback did not match the active board generation.");
            return;
        }
        if (!_feedback.Apply(feedback))
        {
            return;
        }

        switch (feedback.FeedbackKind)
        {
            case AllegroInteractionFeedbackKind.ViewportChanged:
                // Keep the Windows surface for neutral pointer feedback. The
                // SDK's IsCurrentFor(view, viewport) rejects board projection
                // and classification until the new native view agrees.
                break;
            case AllegroInteractionFeedbackKind.SelectionAccepted:
                if (feedback.SelectionOrdinal == 1)
                {
                    _firstPick = feedback.Point;
                    _firstPickObjectKind = feedback.ObjectKind;
                    State = new(
                        true,
                        true,
                        "First object selected",
                        $"Start: {DescribeSelection(feedback.ObjectKind, feedback.Point)}. " +
                        "Choose the second object, or clear the first pick and try again.",
                        CreatePickSummary(feedback.ObjectKind, feedback.Point));
                    StateChanged?.Invoke(this, State);
                }
                else if (feedback.SelectionOrdinal == 2)
                {
                    _secondPick = feedback.Point;
                    _secondPickObjectKind = feedback.ObjectKind;
                    _renderTimer.Stop();
                    _window.HideOverlay();
                    State = new(
                        true,
                        false,
                        "Creating route",
                        "Both objects were accepted. Allegro is creating and verifying the trace.",
                        CreatePickSummary(_firstPickObjectKind, _firstPick),
                        CreatePickSummary(feedback.ObjectKind, feedback.Point));
                    StateChanged?.Invoke(this, State);
                }
                break;
            case AllegroInteractionFeedbackKind.SelectionRejected:
                State = State with
                {
                    Detail = InteractiveRouteOverlayFeedbackState.ExplainRejection(
                        feedback.ReasonCode).Detail
                };
                StateChanged?.Invoke(this, State);
                break;
            case AllegroInteractionFeedbackKind.SelectionCleared:
                _firstPick = null;
                _firstPickObjectKind = AllegroInteractionObjectKind.None;
                State = new(
                    true,
                    false,
                    "Choose the first object",
                    "The previous first pick was cleared.");
                StateChanged?.Invoke(this, State);
                break;
        }

        Render(this, EventArgs.Empty);
    }

    internal void CompleteSuccessfully()
    {
        if (_disposed)
        {
            return;
        }
        if (!_active || _firstPick is null || _secondPick is null)
        {
            End();
            return;
        }

        _active = false;
        State = new(
            false,
            false,
            "Route verified",
            "Allegro created and read back the route between these two accepted objects.",
            CreatePickSummary(_firstPickObjectKind, _firstPick),
            CreatePickSummary(_secondPickObjectKind, _secondPick));
        StateChanged?.Invoke(this, State);
        if (_feedback.Viewport is not null && _canvasView is not null)
        {
            _completionVisibleUntil = DateTimeOffset.UtcNow + CompletionDuration;
            _renderTimer.Start();
            Render(this, EventArgs.Empty);
        }
        else
        {
            _renderTimer.Stop();
            _window.HideOverlay();
        }
    }

    internal void End()
    {
        if (_disposed)
        {
            return;
        }

        _active = false;
        _renderTimer.Stop();
        _feedback.End();
        _firstPick = null;
        _secondPick = null;
        _firstPickObjectKind = AllegroInteractionObjectKind.None;
        _secondPickObjectKind = AllegroInteractionObjectKind.None;
        _canvasView = null;
        _expectedBoardGeneration = 0;
        _completionVisibleUntil = default;
        _dpViaCorridorOverlay = null;
        _overlayBoardPoints = [];
        _overlayScreenPoints = [];
        _overlayRenderedView = null;
        _corridorFrame = null;
        _corridorHudBitmap = null;
        _window.HideOverlay();
        State = InteractiveRouteInteractionState.Inactive;
        StateChanged?.Invoke(this, State);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        End();
        _disposed = true;
        _observer.Dispose();
        if (_canvasObservationPauseDepth == 0)
        {
            _captureGate.Dispose();
        }

        _binding.Invalidated -= BindingOnInvalidated;
        _window.Close();
    }

    private void Render(object? sender, EventArgs e)
    {
        if (_disposed || Stopwatch.GetTimestamp() < _renderRetryAfter)
        {
            return;
        }

        try
        {
            RenderFrame();
        }
        catch (Exception error)
        {
            // Preserve native result/feedback ownership. Only this local
            // compositor may get a bounded retry; no native action is replayed.
            _window.HideOverlay();
            _canvasView = null;
            _overlayRenderedView = null;
            _corridorFrame = null;
            _corridorHudBitmap = null;
            try
            {
                bool current = _binding.IsValid && _binding.IsCurrentFor(_session.Binding);
                _renderingFailed = !_renderRecovery.TryRecover(_window, error, current, out TimeSpan delay);
                _renderRetryAfter = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * delay.TotalSeconds);
            }
            catch (Exception recoveryError)
            {
                error = recoveryError;
                _renderingFailed = true;
            }
            if (_renderingFailed)
            {
                _renderTimer.Stop();
            }
            State = State with { Detail = "Owned overlay drawing unavailable: " + error.Message };
            try
            {
                StateChanged?.Invoke(this, State);
                RenderingFailed?.Invoke(this, error);
            }
            catch (Exception subscriberError)
            {
                Trace.TraceWarning("Overlay notification failed: {0}", subscriberError.Message);
            }
        }
    }

    private void RenderFrame()
    {
        if (_canvasObservationPauseDepth != 0 || _renderingFailed)
        {
            _renderTimer.Stop();
            _window.HideOverlay();
            return;
        }
        var completionVisible = !_active &&
            DateTimeOffset.UtcNow <= _completionVisibleUntil;
        if (!_active && !completionVisible && _dpViaCorridorOverlay is null)
        {
            _renderTimer.Stop();
            _window.HideOverlay();
            return;
        }
        if (!_binding.IsValid || !_binding.IsCurrentFor(_session.Binding))
        {
            HandleOwnerLost();
            return;
        }

        if (_dpViaCorridorOverlay is { } capturedCorridor &&
            !capturedCorridor.IsCurrentFor(_session.Binding, _session.ProtocolSnapshot))
        {
            End();
            return;
        }

        if (_active && !_feedback.CanRenderPicking)
        {
            _renderTimer.Stop();
            _window.HideOverlay();
            return;
        }

        // Observation renewal, coalescing and native evidence retirement belong
        // to the SDK. This timer schedules tool HUD presentation only.
        var update = _observer.Current;
        CanvasStatus = update.Status;
        _canvasView = update.View;
        if (_observationInvalidation != update.InvalidationSequence)
        {
            _observationInvalidation = update.InvalidationSequence;
            _corridorFrame = null;
            _corridorHudBitmap = null;
            _overlayRenderedView = null;
            _window.ClearDrawing();
        }
        if (update.IsTerminal)
        {
            HandleOwnerLost();
            return;
        }
        if (update.Error is { } observationError)
        {
            _window.HideOverlay();
            // Screen capture, composition and focus transitions can interrupt a
            // read without ending the session. The SDK already owns renewal.
            // Report this update once and resume only from a fresh valid view.
            if (_reportedObservationFailure != update.Sequence)
            {
                _reportedObservationFailure = update.Sequence;
                State = State with { Detail = "Canvas temporarily unavailable: " + observationError.Message };
                StateChanged?.Invoke(this, State);
                ObservationFailed?.Invoke(this, observationError);
            }
            return;
        }

        if (_dpViaCorridorOverlay is { } corridor)
        {
            // Drawing authority is separate from pointer/picking authority.
            // Keep renewing native evidence while another application is active.
            if (_canvasView is not { IsAvailable: true, Canvas: { } corridorCanvas } corridorView)
            {
                _corridorFrame = null;
                _window.HideOverlay();
                return;
            }
            var scale = _window.PrepareDrawingWindow(corridorCanvas.Bounds, corridorCanvas.Dpi);
            if (!ReferenceEquals(corridorView, _overlayRenderedView) ||
                _corridorFrame is null)
            {
                // Retain only a still-valid old frame while synchronously
                // constructing its replacement. Do not blank on every renewal.
                if (_corridorFrame is not null && !_binding.ValidateCanvasDrawingView(_corridorFrame.View).IsAvailable)
                {
                    _window.ClearDrawing();
                }
                _corridorFrame = AllegroCanvasDrawingFrame.TryCreate(
                    _binding, corridorView,
                    BoardOverlayDrawingPolicy.Corridor(_overlayBoardPoints, scale),
                    OverlayWindowHandle, status => CanvasStatus = status);
                if (_corridorFrame is null)
                {
                    _window.HideOverlay();
                    return;
                }
                AllegroCanvasPointsResult projection = _binding.ProjectDrawingPoints(
                    _corridorFrame.View, _overlayBoardPoints);
                if (!projection.IsAvailable)
                {
                    CanvasStatus = projection.Status;
                    _window.HideOverlay();
                    return;
                }
                AllegroScreenPoint[] points = projection.Points.ToArray();
                _overlayScreenPoints = points;
                _overlayRenderedView = corridorView;
                _corridorHudBitmap = _window.CreateDpViaCorridorHud(
                    corridor.Finding, points, corridorCanvas.Bounds, scale,
                    _corridorFrame.View.ClipRectangles);
            }
            // Revalidate on EVERY frame, even if this view's points were already
            // projected. An expired or changed canvas never prolongs a drawing.
            var frame = _corridorFrame;
            var hudBitmap = _corridorHudBitmap;
            if (frame is null || hudBitmap is null || !frame.TryPresent(bitmap => _window.PresentDpViaCorridor(
                    bitmap, hudBitmap, _overlayScreenPoints, corridorCanvas.Bounds),
                    status => CanvasStatus = status))
            {
                _corridorFrame = null;
                _window.HideOverlay();
            }
            return;
        }

        if (completionVisible)
        {
            var completionStart = TryMapBoardPoint(_firstPick);
            var completionEnd = TryMapBoardPoint(_secondPick);
            if (_canvasView?.Canvas is not { } completionCanvas ||
                completionStart is null || completionEnd is null)
            {
                _window.HideOverlay();
                return;
            }
            _window.RenderCompletion(
                completionCanvas.Bounds,
                completionCanvas.Dpi,
                completionStart.Value,
                completionEnd.Value,
                "Route verified",
                SystemParameters.HighContrast);
            return;
        }

        // The cursor is a physical Windows position, not an old native MOVE
        // sample. Its neutral presentation can remain while the pointer rests;
        // native classification and board graphics have their own stricter gates.
        if (_canvasView is not { Canvas: { } currentCanvas } currentView ||
            !_binding.TryGetCanvasPointer(currentView, out var cursor))
        {
            _window.HideOverlay();
            return;
        }
        Brush cursorBrush = ResolvePointerBrush(cursor);

        AllegroScreenPoint? marker = TryMapBoardPoint(_firstPick);
        _window.Render(
            currentCanvas.Bounds,
            currentCanvas.Dpi,
            cursor,
            cursorBrush,
            BuildPointerLabel(cursorBrush),
            BuildMeasurementLabel(cursorBrush),
            marker,
            marker is null
                ? null
                : $"1  Start · {DescribeObjectKind(_firstPickObjectKind)}",
            SystemParameters.HighContrast);
    }

    private string BuildPointerLabel(Brush? pointerBrush)
    {
        if (pointerBrush is null ||
            ReferenceEquals(pointerBrush, BoardOverlayWindow.NeutralBrush))
        {
            return _firstPick is null ? "Choose the first object" : "Choose the second object";
        }

        return _feedback.PointerState switch
        {
            AllegroPointerState.Valid =>
                $"{DescribeObjectKind(_feedback.ObjectKind)} · Selectable",
            AllegroPointerState.Invalid => _feedback.RejectionLabel,
            AllegroPointerState.OutsideCanvas => "Outside board canvas",
            _ => "Checking…"
        };
    }

    private string? BuildMeasurementLabel(Brush? pointerBrush)
    {
        var pointerPoint = _feedback.Point;
        if (_firstPick is null || pointerPoint is null ||
            pointerBrush is null ||
            ReferenceEquals(pointerBrush, BoardOverlayWindow.NeutralBrush) ||
            !string.Equals(
                _firstPick.Units,
                pointerPoint.Units,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var deltaX = Convert.ToDouble(pointerPoint.X) -
            Convert.ToDouble(_firstPick.X);
        var deltaY = Convert.ToDouble(pointerPoint.Y) -
            Convert.ToDouble(_firstPick.Y);
        var direct = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY) ||
            !double.IsFinite(direct))
        {
            return null;
        }

        var units = DisplayUnits(pointerPoint.Units);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"ΔX {deltaX:0.###}  ·  ΔY {deltaY:0.###}  ·  Direct {direct:0.###} {units}");
    }

    private static string DescribeSelection(
        AllegroInteractionObjectKind objectKind,
        AllegroBoardPoint? point)
    {
        var objectName = DescribeObjectKind(objectKind);
        if (point is null)
        {
            return objectName;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{objectName} at ({Convert.ToDouble(point.X):0.###}, " +
            $"{Convert.ToDouble(point.Y):0.###}) {DisplayUnits(point.Units)}");
    }

    private static InteractiveRoutePickSummary CreatePickSummary(
        AllegroInteractionObjectKind objectKind,
        AllegroBoardPoint? point)
    {
        var location = point is null
            ? "Board location unavailable"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"X {Convert.ToDouble(point.X):0.###}  ·  " +
                $"Y {Convert.ToDouble(point.Y):0.###} {DisplayUnits(point.Units)}");
        return new(DescribeObjectKind(objectKind), location);
    }

    private static string DescribeObjectKind(AllegroInteractionObjectKind objectKind) =>
        objectKind switch
        {
            AllegroInteractionObjectKind.Component => "Component",
            AllegroInteractionObjectKind.Symbol => "Symbol",
            AllegroInteractionObjectKind.Net => "Net",
            AllegroInteractionObjectKind.Pin => "Pin",
            AllegroInteractionObjectKind.Via => "Via",
            AllegroInteractionObjectKind.Cline => "Cline",
            AllegroInteractionObjectKind.ClineSegment => "Cline segment",
            AllegroInteractionObjectKind.Line => "Line",
            _ => "Object"
        };

    private static string DisplayUnits(string units) =>
        string.Equals(units, "mils", StringComparison.OrdinalIgnoreCase)
            ? "mil"
            : units;

    private Brush ResolvePointerBrush(AllegroScreenPoint cursor)
    {
        // A current Windows pointer does not renew a native verdict. Require
        // that verdict's own age, operation and ordered viewport before color.
        if (_feedback.SemanticFeedback is not { FeedbackKind: AllegroInteractionFeedbackKind.Pointer } semantic ||
            _canvasView?.Canvas is not { } canvas ||
            _feedback.Pointer?.Sequence != semantic.Sequence ||
            _feedback.Viewport is not { } viewport ||
            viewport.OperationId != semantic.OperationId || viewport.Sequence >= semantic.Sequence ||
            !_binding.IsCurrentFor(_canvasView, viewport) ||
            !_session.TryGetInteractionFeedbackAge(semantic, out var age) ||
            !_feedback.HasFreshSemanticFeedback(age, PointerFreshness))
        {
            return BoardOverlayWindow.NeutralBrush;
        }
        var observedPoint = TryMapBoardPoint(_feedback.Point);
        if (observedPoint is null)
        {
            return BoardOverlayWindow.NeutralBrush;
        }

        var maximumDistance = 12.0 * Math.Max(1.0, canvas.Dpi / 96.0);
        var deltaX = observedPoint.Value.X - cursor.X;
        var deltaY = observedPoint.Value.Y - cursor.Y;
        if ((double)deltaX * deltaX + (double)deltaY * deltaY >
            maximumDistance * maximumDistance)
        {
            return BoardOverlayWindow.NeutralBrush;
        }

        return _feedback.PointerState switch
        {
            AllegroPointerState.Valid => BoardOverlayWindow.ValidBrush,
            AllegroPointerState.Invalid => BoardOverlayWindow.InvalidBrush,
            _ => BoardOverlayWindow.NeutralBrush
        };
    }

    private AllegroScreenPoint? TryMapBoardPoint(AllegroBoardPoint? point)
    {
        if (point is null || _feedback.Viewport is not { } viewport || _canvasView is null ||
            !_binding.IsCurrentFor(_canvasView, viewport) ||
            !_binding.TryMapBoardPoint(
                _canvasView,
                point,
                out var screenPoint))
        {
            return null;
        }
        return screenPoint;
    }

    private void BindingOnInvalidated(
        object? sender,
        AllegroDesktopInvalidated invalidated)
    {
        _ = _window.Dispatcher.InvokeAsync(HandleOwnerLost);
    }

    private void HandleOwnerLost()
    {
        _window.HideOverlay();
        if (_dpViaCorridorOverlay is not null)
        {
            End();
            return;
        }
        if (_active && !_ownerLossPublished)
        {
            _ownerLossPublished = true;
            OwnerLost?.Invoke(this, EventArgs.Empty);
        }
    }

}

/// <summary>
/// Presentation HWND for the screen HUD not supported by the SDK polyline API.
/// Canvas discovery, projection, visibility and board raster lifetime are SDK-owned.
/// </summary>
internal sealed class BoardOverlayWindow : Window
{
    internal static Brush ValidBrush => BoardOverlayHud.ValidBrush;
    internal static Brush InvalidBrush => BoardOverlayHud.InvalidBrush;
    internal static Brush NeutralBrush => BoardOverlayHud.NeutralBrush;

    private readonly Image _boardImage = new()
    {
        IsHitTestVisible = false,
        Stretch = Stretch.Fill,
        SnapsToDevicePixels = true
    };
    private readonly Image _hudImage = new()
    {
        IsHitTestVisible = false,
        Stretch = Stretch.Fill,
        SnapsToDevicePixels = true
    };
    private readonly BoardOverlayHud _hud = new();
    private readonly Grid _surface = new() { ClipToBounds = true };
    private Point[] _renderedCorridorPoints = [];
    private bool _shown;
    private AllegroScreenRect? _bounds;
    private double _scale;

    internal BoardOverlayWindow(nint ownerHandle)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = false;
        UseLayoutRounding = true;
        _surface.Children.Add(_boardImage);
        _surface.Children.Add(_hudImage);
        _surface.Children.Add(_hud);
        Content = _surface;
        RenderOptions.SetBitmapScalingMode(_boardImage, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(_hudImage, BitmapScalingMode.NearestNeighbor);
        new WindowInteropHelper(this).Owner = ownerHandle;
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            long style = GetWindowLongPtr(handle, -20).ToInt64();
            _ = SetWindowLongPtr(handle, -20, (nint)((style | 0x080000A0) & ~0x8L));
        };
    }

    internal IReadOnlyList<Point> DpViaCorridorRenderedAnchorPoints
    {
        get
        {
            if (!_shown || _boardImage.Source is not BitmapSource bitmap ||
                _renderedCorridorPoints.Length == 0)
            {
                return Array.Empty<Point>();
            }
            _boardImage.UpdateLayout();
            Size size = _boardImage.RenderSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                return Array.Empty<Point>();
            }
            // Observe the actual arranged image transform, not the model's
            // screen-coordinate array. The SDK bitmap is physical-pixel sized.
            return Array.AsReadOnly(_renderedCorridorPoints.Select(point =>
                _boardImage.PointToScreen(new Point(
                    point.X / bitmap.PixelWidth * size.Width,
                    point.Y / bitmap.PixelHeight * size.Height))).ToArray());
        }
    }

    internal void Render(AllegroScreenRect rectangle, uint dpi,
        AllegroScreenPoint? cursor, Brush? cursorBrush, string cursorLabel,
        string? measurementLabel, AllegroScreenPoint? marker,
        string? firstPickLabel, bool highContrast)
    {
        double scale = PrepareWindow(rectangle, dpi);
        ClearDrawing();
        _hud.ShowPicking(Local(cursor, rectangle, scale), cursorBrush, cursorLabel,
            measurementLabel, Local(marker, rectangle, scale), firstPickLabel, highContrast);
    }

    internal void RenderCompletion(AllegroScreenRect rectangle, uint dpi,
        AllegroScreenPoint start, AllegroScreenPoint end, string label, bool highContrast)
    {
        double scale = PrepareWindow(rectangle, dpi);
        ClearDrawing();
        _hud.ShowCompletion(Local(start, rectangle, scale)!.Value,
            Local(end, rectangle, scale)!.Value, label, highContrast);
    }

    internal double PrepareDrawingWindow(AllegroScreenRect rectangle, uint dpi) =>
        PrepareWindow(rectangle, dpi);

    internal BitmapSource CreateDpViaCorridorHud(DpViaCorridorFinding finding,
        IReadOnlyList<AllegroScreenPoint> points, AllegroScreenRect rectangle, double scale,
        IReadOnlyList<AllegroScreenRect> visibleRectangles)
    {
        var localPoints = points.Select(point => new Point(
            ((double)point.X - rectangle.Left) / scale,
            ((double)point.Y - rectangle.Top) / scale)).ToArray();
        var clips = visibleRectangles.Select(visible => new Int32Rect(
            checked(visible.Left - rectangle.Left), checked(visible.Top - rectangle.Top),
            checked((int)visible.Width), checked((int)visible.Height))).ToArray();
        return _hud.RasterizeCorridor(checked((int)rectangle.Width), checked((int)rectangle.Height),
            finding, localPoints, scale, clips);
    }

    internal void PresentDpViaCorridor(BitmapSource boardBitmap, BitmapSource hudBitmap,
        IReadOnlyList<AllegroScreenPoint> points, AllegroScreenRect rectangle)
    {
        // Called synchronously inside SDK TryPresent, after all raster work.
        _boardImage.Source = boardBitmap;
        _hudImage.Source = hudBitmap;
        _renderedCorridorPoints = points.Select(point => new Point(
            (double)point.X - rectangle.Left, (double)point.Y - rectangle.Top)).ToArray();
    }

    internal void ClearDrawing()
    {
        _boardImage.Source = null;
        _hudImage.Source = null;
        _renderedCorridorPoints = [];
        _hud.Clear();
    }

    internal void HideOverlay()
    {
        ClearDrawing();
        if (_shown)
        {
            Hide();
            _shown = false;
        }
    }

    private double PrepareWindow(AllegroScreenRect rectangle, uint dpi)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0 || dpi == 0)
        {
            throw new InvalidOperationException("The SDK canvas has no usable physical bounds.");
        }
        bool moved = !_shown || _bounds != rectangle;
        if (moved)
        {
            ClearDrawing();
        }
        if (!_shown)
        {
            Show();
            _shown = true;
        }
        nint handle = new WindowInteropHelper(this).Handle;
        if (moved)
        {
            Place();
        }
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
        {
            throw new InvalidOperationException("The HUD has no WPF composition target.");
        }
        Matrix transform = target.TransformToDevice;
        double scale = transform.M11;
        if (!double.IsFinite(scale) || scale <= 0 || transform.M22 != scale ||
            transform.M12 != 0 || transform.M21 != 0)
        {
            throw new InvalidOperationException("The HUD has an unsupported WPF device transform.");
        }
        if (_scale != scale)
        {
            ClearDrawing();
            moved = true;
        }
        Width = rectangle.Width / scale;
        Height = rectangle.Height / scale;
        if (moved)
        {
            Place();
        }
        _bounds = rectangle;
        _scale = scale;
        UpdateLayout();
        return scale;

        void Place()
        {
            // Owned, non-topmost presentation only. Never raise Allegro or this
            // HUD above an unrelated foreground window.
            if (!SetWindowPos(handle, 0, rectangle.Left, rectangle.Top,
                    checked((int)rectangle.Width), checked((int)rectangle.Height),
                    0x0010 | 0x0004 | 0x0200))
            {
                throw new InvalidOperationException("The owned HUD could not retain the SDK canvas bounds.");
            }
        }
    }

    private static Point? Local(AllegroScreenPoint? point, AllegroScreenRect bounds, double scale) =>
        point is null ? null : new Point(
            ((double)point.Value.X - bounds.Left) / scale,
            ((double)point.Value.Y - bounds.Top) / scale);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter,
        int x, int y, int width, int height, uint flags);
}
