using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using CircuitHub.AllegroBridge;
using CircuitHub.AllegroBridge.Windows;
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
    private static readonly TimeSpan DrawingFreshness = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CompletionDuration = TimeSpan.FromMilliseconds(1800);

    private readonly AllegroDesktopBinding _binding;
    private readonly AllegroBridgeSession _session;
    private readonly BoardOverlayWindow _window;
    private readonly DispatcherTimer _renderTimer;
    private readonly InteractiveRouteOverlayFeedbackState _feedback = new();
    private AllegroBoardPoint? _firstPick;
    private AllegroBoardPoint? _secondPick;
    private AllegroInteractionObjectKind _firstPickObjectKind =
        AllegroInteractionObjectKind.None;
    private AllegroInteractionObjectKind _secondPickObjectKind =
        AllegroInteractionObjectKind.None;
    private AllegroCanvasView? _canvasView;
    private CancellationTokenSource _captureLifetime = new();
    private Task? _captureTask;
    private readonly SemaphoreSlim _canvasObservationGate = new(1, 1);
    private int _canvasObservationPauseDepth;
    private long _captureEpoch;
    private bool _captureFailed;
    private DateTimeOffset _completionVisibleUntil;
    private long _expectedBoardGeneration;
    private bool _active;
    private bool _disposed;
    private bool _ownerLossPublished;
    private DpViaCorridorBoardOverlay? _dpViaCorridorOverlay;
    private AllegroBoardPoint[] _overlayBoardPoints = [];
    private AllegroScreenPoint[] _overlayScreenPoints = [];
    private AllegroCanvasView? _overlayRenderedView;
    private DpViaCorridorDrawingFrame? _corridorFrame;

    internal BoardOverlayController(AllegroDesktopBinding binding, AllegroBridgeSession session)
    {
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _window = new BoardOverlayWindow(binding.TopLevelWindowHandle);
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
            // Serialize caller-owned observations as well as draining our one
            // in-flight SDK observation. Neither path races the SDK's canvas
            // inspection owner, and no native failure is retried here.
            await _canvasObservationGate.WaitAsync();
            acquired = true;
            if (_captureTask is { } pending)
            {
                await pending;
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            return await capture();
        }
        finally
        {
            if (acquired)
            {
                _canvasObservationGate.Release();
            }

            _canvasObservationPauseDepth--;
            if (_canvasObservationPauseDepth == 0)
            {
                if (_disposed)
                {
                    _canvasObservationGate.Dispose();
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
        _captureLifetime.Cancel();
        _captureLifetime.Dispose();
        _captureLifetime = new();
        _captureEpoch++;
        _captureFailed = false;
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
        _captureLifetime.Dispose();
        _captureLifetime = new();
        _captureFailed = false;
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
        _captureEpoch++;
        _captureLifetime.Cancel();
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
        _captureLifetime.Dispose();
        if (_canvasObservationPauseDepth == 0)
        {
            _canvasObservationGate.Dispose();
        }

        _binding.Invalidated -= BindingOnInvalidated;
        _window.Close();
    }

    private void Render(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            RenderFrame();
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or OverflowException)
        {
            // A window disappearing or losing its composition target is an
            // unavailable drawing, not an unhandled DispatcherTimer failure.
            // Stop this attempt and surface its cause; no render retry loop.
            _renderTimer.Stop();
            _window.HideOverlay();
            _canvasView = null;
            _overlayRenderedView = null;
            _captureFailed = true;
            State = State with
            {
                Detail = "Owned overlay drawing unavailable: " + error.Message
            };
            StateChanged?.Invoke(this, State);
        }
    }

    private void RenderFrame()
    {
        if (_canvasObservationPauseDepth != 0)
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

        // One in-flight SDK read; no UI-thread accessibility traversal or queued
        // capture backlog. The SDK owns identity, native view pairing and expiry.
        if (!_captureFailed && (_captureTask is null || _captureTask.IsCompleted))
        {
            _captureTask = CaptureCanvasAsync(_captureEpoch, _captureLifetime.Token);
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
                _corridorFrame is null || !_corridorFrame.IsCurrent)
            {
                _window.ClearDrawing();
                _corridorFrame = null;
                var drawingView = _binding.CaptureCanvasDrawingView(corridorView, OverlayWindowHandle);
                if (!drawingView.IsAvailable)
                {
                    _window.HideOverlay();
                    return;
                }
                var points = new AllegroScreenPoint[_overlayBoardPoints.Length];
                for (var index = 0; index < points.Length; index++)
                {
                    if (!_binding.TryProjectDrawingPoint(drawingView, _overlayBoardPoints[index], out points[index]))
                    {
                        _window.HideOverlay();
                        return;
                    }
                }
                _overlayScreenPoints = points;
                _overlayRenderedView = corridorView;
                _corridorFrame = DpViaCorridorDrawingFrame.TryCreate(
                    _binding, drawingView, corridor.Finding, points, scale);
            }
            // Revalidate on EVERY frame, even if this view's points were already
            // projected. An expired or changed canvas never prolongs a drawing.
            if (_corridorFrame is null || !_corridorFrame.IsCurrent)
            {
                _window.HideOverlay();
                return;
            }
            _window.PresentDpViaCorridor(_corridorFrame.Bitmap, _overlayScreenPoints, corridorCanvas.Bounds, scale);
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

    private async Task CaptureCanvasAsync(long epoch, CancellationToken cancellationToken)
    {
        try
        {
            // Establish native acquisition evidence even before pointer feedback
            // arrives. The SDK owns asynchronous UIA work, paired native reads,
            // identity and expiry; PD never calibrates from cursor coordinates.
            var view = await _binding.CaptureCanvasViewAsync(
                _session, _dpViaCorridorOverlay is not null ? DrawingFreshness : PointerFreshness, cancellationToken);
            if (_disposed || epoch != _captureEpoch)
            {
                return;
            }
            // View renewal does not require another mouse movement. Never renew
            // the age or classification of the native pointer sample here.
            // A native sample can expire without invalidating the Windows
            // surface. Physical pointer feedback revalidates that surface on
            // every render; board graphics still require an available view.
            _canvasView = view;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_disposed || epoch != _captureEpoch)
            {
                return;
            }

            _canvasView = null;
            // Surface the failure and stop this capture loop. Do not silently
            // discard a faulted background task or retry it on every render tick.
            _captureFailed = true;
            string detail = $"Canvas observation unavailable: {exception.Message}";
            if (State.Detail != detail)
            {
                State = State with
                {
                    Detail = detail
                };
                StateChanged?.Invoke(this, State);
            }
        }
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

internal sealed class BoardOverlayWindow : Window
{
    internal static readonly Brush ValidBrush = Freeze("#35D07F");
    internal static readonly Brush InvalidBrush = Freeze("#FF647C");
    internal static readonly Brush NeutralBrush = Freeze("#8CA2B8");
    private static readonly Brush FirstPickBrush = Freeze("#4DA3FF");
    private static readonly Brush CardBackgroundBrush = Freeze("#EE0B121A");
    private static readonly Brush CardTextBrush = Freeze("#F4F7FA");
    private static readonly Brush CardMutedTextBrush = Freeze("#AEBBC8");
    private static readonly Brush HighContrastBrush = SystemColors.HighlightBrush;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int GwlExStyle = -20;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoZOrder = 0x0004;
    private readonly nint _ownerHandle;

    private readonly Canvas _surface = new();
    private readonly Ellipse _cursorRing;
    private readonly Ellipse _firstPickRing;
    private readonly Ellipse _firstPickDot;
    private readonly Border _cursorCard;
    private readonly TextBlock _cursorText;
    private readonly TextBlock _measurementText;
    private readonly Border _firstPickBadge;
    private readonly TextBlock _firstPickText;
    private readonly Border _completionBadge;
    private readonly TextBlock _completionText;
    private readonly Image _corridorImage = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private Point[] _renderedDpViaCorridorLocalPoints = [];
    private bool _shown;
    private AllegroScreenRect? _windowBounds;
    private uint _windowDpi;
    private double _windowDeviceScale;

    internal IReadOnlyList<Point> DpViaCorridorRenderedAnchorPoints
    {
        get
        {
            if (!_shown || _corridorImage.Visibility != Visibility.Visible ||
                _corridorImage.Source is not BitmapSource bitmap ||
                _renderedDpViaCorridorLocalPoints.Length == 0)
            {
                return Array.Empty<Point>();
            }

            _corridorImage.UpdateLayout();
            var source = new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
            var size = _corridorImage.RenderSize;
            if (source.IsEmpty || source.Width <= 0 || source.Height <= 0 || size.Width <= 0 || size.Height <= 0)
            {
                return Array.Empty<Point>();
            }
            // Observe the positions actually sent to the annotation renderer
            // through the Image's real arranged size and WPF screen transform.
            // This deliberately does not reuse the SDK/model screen-point array.
            return Array.AsReadOnly(_renderedDpViaCorridorLocalPoints.Select(point =>
            {
                var local = new Point((point.X - source.Left) / source.Width * size.Width,
                    (point.Y - source.Top) / source.Height * size.Height);
                return _corridorImage.PointToScreen(local);
            }).ToArray());
        }
    }

    internal BoardOverlayWindow(IntPtr ownerHandle)
    {
        _ownerHandle = ownerHandle;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = false;
        Content = _surface;
        _surface.ClipToBounds = true;
        _surface.Children.Add(_corridorImage);
        new WindowInteropHelper(this).Owner = ownerHandle;

        _cursorRing = CreateRing(34, 2.5);
        _firstPickRing = CreateRing(24, 2.5);
        _firstPickDot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = FirstPickBrush,
            IsHitTestVisible = false
        };
        _surface.Children.Add(_cursorRing);
        _surface.Children.Add(_firstPickRing);
        _surface.Children.Add(_firstPickDot);

        _cursorText = new TextBlock
        {
            Name = "InteractiveRouteCursorLensText",
            Foreground = CardTextBrush,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false
        };
        _measurementText = new TextBlock
        {
            Name = "InteractiveRouteMeasurementText",
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = CardMutedTextBrush,
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false
        };
        _cursorCard = new Border
        {
            Name = "InteractiveRouteCursorLens",
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(6),
            Background = CardBackgroundBrush,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = new StackPanel
            {
                IsHitTestVisible = false,
                Children = { _cursorText, _measurementText }
            }
        };
        _firstPickText = new TextBlock
        {
            Name = "InteractiveRouteFirstPickText",
            Foreground = CardTextBrush,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false
        };
        _firstPickBadge = new Border
        {
            Name = "InteractiveRouteFirstPickBadge",
            Padding = new Thickness(7, 4, 7, 4),
            CornerRadius = new CornerRadius(10),
            Background = CardBackgroundBrush,
            BorderBrush = FirstPickBrush,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = _firstPickText
        };
        _completionText = new TextBlock
        {
            Name = "InteractiveRouteCompletionText",
            Text = "Route verified",
            Foreground = CardTextBrush,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false
        };
        _completionBadge = new Border
        {
            Name = "InteractiveRouteCompletionBadge",
            Padding = new Thickness(8, 5, 8, 5),
            CornerRadius = new CornerRadius(10),
            Background = CardBackgroundBrush,
            BorderBrush = ValidBrush,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = _completionText
        };
        _surface.Children.Add(_cursorCard);
        _surface.Children.Add(_firstPickBadge);
        _surface.Children.Add(_completionBadge);
        SourceInitialized += (_, _) => ApplyExtendedStyles();
    }

    internal void Render(
        AllegroScreenRect rectangle,
        uint dpi,
        AllegroScreenPoint? cursor,
        Brush? cursorBrush,
        string cursorLabel,
        string? measurementLabel,
        AllegroScreenPoint? marker,
        string? firstPickLabel,
        bool highContrast)
    {
        var scale = PrepareWindow(rectangle, dpi);

        _corridorImage.Visibility = Visibility.Collapsed;
        _completionBadge.Visibility = Visibility.Collapsed;

        SetElement(_cursorRing, cursor, rectangle, scale);
        _cursorRing.Stroke = highContrast
            ? HighContrastBrush
            : cursorBrush ?? NeutralBrush;
        _cursorRing.StrokeThickness = highContrast ? 4 : 2.5;
        _cursorText.Text = cursorLabel;
        _measurementText.Text = measurementLabel ?? string.Empty;
        _measurementText.Visibility = string.IsNullOrWhiteSpace(measurementLabel)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _cursorCard.BorderBrush = highContrast
            ? HighContrastBrush
            : cursorBrush ?? NeutralBrush;
        SetCallout(_cursorCard, cursor, rectangle, scale, 22, 18);
        SetElement(_firstPickRing, marker, rectangle, scale);
        SetElement(_firstPickDot, marker, rectangle, scale);
        _firstPickRing.Stroke = highContrast ? HighContrastBrush : FirstPickBrush;
        _firstPickRing.StrokeThickness = highContrast ? 4 : 2.5;
        _firstPickDot.Fill = highContrast ? HighContrastBrush : FirstPickBrush;
        _firstPickText.Text = firstPickLabel ?? string.Empty;
        _firstPickBadge.BorderBrush = highContrast
            ? HighContrastBrush
            : FirstPickBrush;
        SetCallout(_firstPickBadge, marker, rectangle, scale, 16, -34);
        if (_cursorCard.Visibility == Visibility.Visible &&
            _firstPickBadge.Visibility == Visibility.Visible &&
            new Rect(Canvas.GetLeft(_cursorCard), Canvas.GetTop(_cursorCard),
                _cursorCard.DesiredSize.Width, _cursorCard.DesiredSize.Height)
            .IntersectsWith(new Rect(Canvas.GetLeft(_firstPickBadge), Canvas.GetTop(_firstPickBadge),
                _firstPickBadge.DesiredSize.Width, _firstPickBadge.DesiredSize.Height)))
        {
            // Keep the accepted-point marker; prioritize the current explanation
            // over its duplicate text badge when there is not room for both.
            _firstPickBadge.Visibility = Visibility.Collapsed;
        }
    }

    internal void RenderCompletion(
        AllegroScreenRect rectangle,
        uint dpi,
        AllegroScreenPoint start,
        AllegroScreenPoint end,
        string label,
        bool highContrast)
    {
        var scale = PrepareWindow(rectangle, dpi);
        _corridorImage.Visibility = Visibility.Collapsed;
        _cursorCard.Visibility = Visibility.Collapsed;
        _firstPickBadge.Visibility = Visibility.Collapsed;

        // Completion proves the native operation, not a path reconstructed from
        // its selected endpoints. Never invent bends or copper geometry here.
        SetElement(_firstPickRing, start, rectangle, scale);
        SetElement(_firstPickDot, start, rectangle, scale);
        SetElement(_cursorRing, end, rectangle, scale);
        var brush = highContrast ? HighContrastBrush : ValidBrush;
        _firstPickRing.Stroke = brush;
        _firstPickDot.Fill = brush;
        _cursorRing.Stroke = brush;
        _firstPickRing.StrokeThickness = _cursorRing.StrokeThickness = highContrast ? 4 : 2.5;
        _completionText.Text = $"{label} · endpoints only";
        _completionBadge.BorderBrush = highContrast ? HighContrastBrush : ValidBrush;
        SetCallout(_completionBadge, end, rectangle, scale, 22, 22);
        if (_completionBadge.Visibility != Visibility.Visible)
        {
            return;
        }

        var badgeBounds = new Rect(Canvas.GetLeft(_completionBadge), Canvas.GetTop(_completionBadge),
            _completionBadge.DesiredSize.Width, _completionBadge.DesiredSize.Height);
        foreach (var endpoint in new[] { _firstPickRing, _cursorRing })
        {
            if (badgeBounds.IntersectsWith(new Rect(Canvas.GetLeft(endpoint), Canvas.GetTop(endpoint),
                    endpoint.Width, endpoint.Height)))
            {
                // The panel already owns terminal status. In a tight canvas,
                // preserve both accepted points instead of covering one with it.
                _completionBadge.Visibility = Visibility.Collapsed;
                break;
            }
        }
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

    internal void ClearDrawing()
    {
        foreach (UIElement child in _surface.Children)
        {
            child.Visibility = Visibility.Collapsed;
        }
        _corridorImage.Source = null;
        _renderedDpViaCorridorLocalPoints = [];
    }

    internal double PrepareDrawingWindow(AllegroScreenRect rectangle, uint dpi) => PrepareWindow(rectangle, dpi);

    internal void PresentDpViaCorridor(BitmapSource bitmap, IReadOnlyList<AllegroScreenPoint> points,
        AllegroScreenRect rectangle, double scale)
    {
        ClearDrawing();
        _corridorImage.Visibility = Visibility.Visible;
        _corridorImage.Width = rectangle.Width / scale;
        _corridorImage.Height = rectangle.Height / scale;
        _corridorImage.Stretch = Stretch.Fill;
        RenderOptions.SetBitmapScalingMode(_corridorImage, BitmapScalingMode.NearestNeighbor);
        _corridorImage.Source = bitmap;
        _renderedDpViaCorridorLocalPoints = points.Select(point => new Point(
            (double)point.X - rectangle.Left, (double)point.Y - rectangle.Top)).ToArray();
    }

    private static Ellipse CreateRing(double size, double thickness) => new()
    {
        Width = size,
        Height = size,
        StrokeThickness = thickness,
        Fill = Brushes.Transparent,
        IsHitTestVisible = false
    };

    private double PrepareWindow(AllegroScreenRect rectangle, uint dpi)
    {
        bool needsPlacement = !_shown || _windowBounds != rectangle || _windowDpi != dpi;
        if (needsPlacement)
        {
            ClearDrawing();
        }
        if (!_shown)
        {
            Show();
            _shown = true;
        }
        var handle = new WindowInteropHelper(this).Handle;
        if (needsPlacement && !PlaceWindow())
        {
            HideOverlay();
            throw new InvalidOperationException("The owned overlay could not be placed on the current Allegro canvas.");
        }
        // The native canvas DPI describes Allegro, not this WPF HWND. Moving
        // the overlay can put it on a monitor whose composition transform is
        // different. Sample the actual drawing owner AFTER physical placement.
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } composition)
        {
            HideOverlay();
            throw new InvalidOperationException("The owned overlay has no WPF device transform.");
        }
        var transform = composition.TransformToDevice;
        var scale = transform.M11;
        if (!double.IsFinite(scale) || scale <= 0 || transform.M22 != scale ||
            transform.M12 != 0 || transform.M21 != 0)
        {
            HideOverlay();
            throw new InvalidOperationException("The owned overlay has an unsupported WPF device transform.");
        }
        if (_windowDeviceScale != scale)
        {
            // All graphics in this shared window are expressed in its DIPs.
            // Invalidate their cached drawings when the actual owner DPI changes.
            ClearDrawing();
            _windowDeviceScale = scale;
            needsPlacement = true;
        }
        Width = rectangle.Width / scale;
        Height = rectangle.Height / scale;
        if (needsPlacement && !PlaceWindow())
        {
            HideOverlay();
            throw new InvalidOperationException("The owned overlay could not retain the current Allegro canvas bounds.");
        }
        _windowBounds = rectangle;
        _windowDpi = dpi;
        UpdateLayout();
        return scale;

        bool PlaceWindow()
        {
            // Stay immediately above the owning Allegro frame, not above the
            // unrelated application the user has brought to the foreground.
            var precedingWindow = GetWindow(_ownerHandle, 3); // GW_HWNDPREV
            var flags = SwpNoActivate | SwpShowWindow;
            if (precedingWindow == handle)
            {
                flags |= SwpNoZOrder;
            }
            else if (precedingWindow != 0 &&
                (GetWindowLongPtr(precedingWindow, GwlExStyle).ToInt64() & 0x00000008) != 0)
            {
                // Inserting after a topmost window would promote this window.
                // At that boundary, explicitly remain in the non-topmost band.
                precedingWindow = new IntPtr(-2); // HWND_NOTOPMOST
            }
            return SetWindowPos(handle, precedingWindow, rectangle.Left, rectangle.Top,
                checked((int)rectangle.Width), checked((int)rectangle.Height), flags);
        }
    }

    private static void SetElement(
        FrameworkElement element,
        AllegroScreenPoint? point,
        AllegroScreenRect rectangle,
        double scale)
    {
        element.Visibility = point is null ? Visibility.Collapsed : Visibility.Visible;
        if (point is null)
        {
            return;
        }

        Canvas.SetLeft(element, (point.Value.X - rectangle.Left) / scale - element.Width / 2);
        Canvas.SetTop(element, (point.Value.Y - rectangle.Top) / scale - element.Height / 2);
    }

    private static void SetCallout(
        FrameworkElement element,
        AllegroScreenPoint? point,
        AllegroScreenRect rectangle,
        double scale,
        double offsetX,
        double offsetY,
        double maximumWidth = double.PositiveInfinity)
    {
        element.Visibility = point is null ? Visibility.Collapsed : Visibility.Visible;
        if (point is null)
        {
            return;
        }

        var surfaceWidth = rectangle.Width / scale;
        var surfaceHeight = rectangle.Height / scale;
        if (surfaceWidth <= 16 || surfaceHeight <= 16)
        {
            element.Visibility = Visibility.Collapsed;
            return;
        }
        element.MaxWidth = Math.Min(surfaceWidth - 16, maximumWidth);
        element.Measure(new Size(element.MaxWidth, double.PositiveInfinity));
        var width = element.DesiredSize.Width;
        var height = element.DesiredSize.Height;
        if (height > surfaceHeight - 16)
        {
            element.Visibility = Visibility.Collapsed;
            return;
        }
        var anchorX = (point.Value.X - rectangle.Left) / scale;
        var anchorY = (point.Value.Y - rectangle.Top) / scale;
        var left = anchorX + offsetX;
        if (left + width > surfaceWidth - 8)
        {
            left = anchorX - width - Math.Abs(offsetX);
        }
        var top = anchorY + offsetY;
        if (top + height > surfaceHeight - 8)
        {
            top = anchorY - height - 18;
        }

        Canvas.SetLeft(element, Math.Max(8, left));
        Canvas.SetTop(element, Math.Max(8, top));
    }

    private static Brush Freeze(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void ApplyExtendedStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var styles = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        styles |= WsExTransparent | WsExToolWindow | WsExNoActivate;
        _ = SetWindowLongPtr(handle, GwlExStyle, new IntPtr(styles));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, uint command);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr windowHandle,
        int index,
        IntPtr newValue);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
