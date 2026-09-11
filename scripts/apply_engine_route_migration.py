from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


def replace_between(text: str, start_marker: str, end_marker: str, replacement: str, label: str) -> str:
    start_count = text.count(start_marker)
    end_count = text.count(end_marker)
    if start_count != 1 or end_count < 1:
        raise SystemExit(
            f"{label}: expected one start and at least one end; found start={start_count}, end={end_count}")
    start = text.index(start_marker)
    end = text.index(end_marker, start)
    return text[:start] + replacement + text[end:]


# BridgeSession keeps connection/corridor SDK ownership while production route
# lifecycle and native edit authority move behind AllegroWorkspace.
path = Path("src/PD.Simple/BridgeSession.cs")
text = path.read_text(encoding="utf-8-sig")
text = replace_once(
    text,
    "using CircuitHub.AllegroBridge;\nusing CircuitHub.AllegroBridge.Windows;",
    "using CircuitHub.AllegroBridge;\nusing CircuitHub.AllegroBridge.Engine.Live;\nusing CircuitHub.AllegroBridge.Windows;",
    "BridgeSession Engine using",
)
text = replace_once(
    text,
    """    private IAllegroOperationHandle? _route;
    private TaskCompletionSource<IAllegroOperationHandle?>? _routeReady;
    private Task<AllegroOperationReceipt>? _routeTask;
    private readonly Dictionary<IAllegroOperationHandle, Task> _nativeCancellationTasks = new(ReferenceEqualityComparer.Instance);""",
    """    private IEngineOperationControl? _route;
    private TaskCompletionSource<IEngineOperationControl?>? _routeReady;
    private Task<InteractiveRouteResult>? _routeTask;
    private readonly Dictionary<IEngineOperationControl, Task> _nativeCancellationTasks = new(ReferenceEqualityComparer.Instance);""",
    "BridgeSession route fields",
)
text = replace_once(
    text,
    """    public bool CanClearFirstPick => HasRouteInProgress && _route is { Current.IsTerminal: false } current &&
        current.Current.CommandId == AllegroPcbContract.PickEndpointsCommand && RouteState.HasFirstPick;""",
    """    public bool CanClearFirstPick => HasRouteInProgress &&
        _route is EngineEndpointPick { IsTerminal: false } && RouteState.HasFirstPick;""",
    "BridgeSession clear-first-pick property",
)
text = replace_once(
    text,
    """    public bool CanUndoRoute => !IsBusy && !_outcomeUncertain && _undoBinding is { } binding &&
        SameBoard(binding) && _pcb is { IsCurrent: true };""",
    """    public bool CanUndoRoute => !IsBusy && !_outcomeUncertain &&
        _lastEngineEdit is { CanUndo: true } && _undoBinding is { } binding &&
        SameBoard(binding) && _pcb is { IsCurrent: true };""",
    "BridgeSession undo property",
)
if text.count("_lastEdit = null;") != 2:
    raise SystemExit(
        f"BridgeSession legacy edit reset: expected 2 matches, found {text.count('_lastEdit = null;')}")
text = text.replace("_lastEdit = null;", "_lastEngineEdit = null;")

text = replace_between(
    text,
    "    public Task<AllegroOperationReceipt> RouteAsync(",
    "    public async Task CancelRouteAsync()",
    """    public Task<InteractiveRouteResult> RouteAsync(decimal width, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width is < 0.1m or > 10_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        if (_overlay is null)
        {
            throw new InvalidOperationException("The selected Allegro window must be bound before routing.");
        }
        _ = BeginOperation(RouteCommand, route: true);
        _undoBinding = null;
        _lastEngineEdit = null;
        var task = ExecuteEngineRouteAsync(width);
        lock (_gate)
        {
            _routeTask = task;
        }
        Track(task);
        // Cancelling this caller's wait does not claim native cancellation.
        return task.WaitAsync(cancellationToken);
    }

""",
    "BridgeSession RouteAsync",
)
text = replace_between(
    text,
    "    public async Task CancelRouteAsync()",
    "    public async Task ClearFirstPickAsync()",
    """    public async Task CancelRouteAsync()
    {
        Task<IEngineOperationControl?> ready;
        lock (_gate)
        {
            if (!_routeInProgress || _routeReady is null)
            {
                throw new InvalidOperationException("No route is active.");
            }
            _routeCancelRequested = true;
            ready = _routeReady.Task;
        }
        var operation = await ready.WaitAsync(_lifetime.Token);
        if (operation is not null)
        {
            await CancelNativeRouteOnceAsync(operation);
        }
    }

""",
    "BridgeSession CancelRouteAsync",
)
text = replace_between(
    text,
    "    public async Task ClearFirstPickAsync()",
    "    public Task<AllegroOperationReceipt> UndoRouteAsync(",
    """    public async Task ClearFirstPickAsync()
    {
        EngineEndpointPick pick;
        lock (_gate)
        {
            pick = _route as EngineEndpointPick ??
                throw new InvalidOperationException("No active endpoint pick is ready to clear its first selection.");
            if (pick.IsTerminal)
            {
                throw new InvalidOperationException("Endpoint input has ended. No clear-first-pick action was sent to an edit operation.");
            }
        }
        await pick.ClearFirstAsync(_lifetime.Token);
    }

""",
    "BridgeSession ClearFirstPickAsync",
)
text = replace_between(
    text,
    "    public Task<AllegroOperationReceipt> UndoRouteAsync(",
    "    private AllegroPcbSession BeginOperation(",
    """    public Task<InteractiveRouteResult> UndoRouteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanUndoRoute)
        {
            throw new InvalidOperationException("No verified recoverable route belongs to this board session.");
        }
        _ = BeginOperation(UndoCommand);
        var task = ExecuteEngineUndoAsync();
        Track(task);
        return task.WaitAsync(cancellationToken);
    }

""",
    "BridgeSession UndoRouteAsync",
)
text = replace_between(
    text,
    "    private async Task ReadFeedbackAsync(IAllegroOperationHandle handle, CancellationToken cancellationToken)",
    "    private Task CancelNativeRouteOnceAsync(",
    "",
    "legacy SDK route feedback reader",
)
text = replace_once(
    text,
    "    private Task CancelNativeRouteOnceAsync(IAllegroOperationHandle handle)",
    "    private Task CancelNativeRouteOnceAsync(IEngineOperationControl handle)",
    "BridgeSession cancellation signature",
)
text = replace_once(
    text,
    "            if (handle.Current.IsTerminal)\n",
    "            if (handle.IsTerminal)\n",
    "BridgeSession cancellation terminal check",
)
text = replace_once(
    text,
    "        Task<IAllegroOperationHandle?>? ready;\n",
    "        Task<IEngineOperationControl?>? ready;\n",
    "BridgeSession dispose ready type",
)
text = replace_once(
    text,
    "                Task<AllegroOperationReceipt>? routeTask;\n",
    "                Task<InteractiveRouteResult>? routeTask;\n",
    "BridgeSession dispose route task type",
)
text = replace_once(
    text,
    "        _connection = null;\n        _pcb = null;\n",
    "        _connection = null;\n        _pcb = null;\n        _engineWorkspace = null;\n",
    "BridgeSession release Engine workspace",
)
path.write_text(text, encoding="utf-8")


# The mixed PCB adapter retains the corridor implementation but no longer owns
# point-to-point trace lifecycle, SDK operation handles, or edit-specific Undo.
path = Path("src/PD.Simple/BridgeSession.PcbTools.cs")
text = path.read_text(encoding="utf-8-sig")
text = replace_once(
    text,
    "    private AllegroPcbMutationResult? _lastEdit;\n",
    "",
    "legacy SDK edit field",
)
text = replace_between(
    text,
    "    private async Task<AllegroOperationReceipt> ExecuteRouteAsync(",
    "    private static async Task WriteManagedReportAsync",
    "",
    "legacy low-level production route block",
)
path.write_text(text, encoding="utf-8")


# Presentation consumes Engine interaction evidence. SDK/Windows point types stay
# only at the canvas projection seam and the unrelated corridor drawing path.
path = Path("src/PD.Simple/BoardOverlayController.cs")
text = path.read_text(encoding="utf-8-sig")
text = replace_once(
    text,
    "using CircuitHub.AllegroBridge;\nusing CircuitHub.AllegroBridge.Windows;",
    "using CircuitHub.AllegroBridge;\nusing CircuitHub.AllegroBridge.Engine.Live;\nusing CircuitHub.AllegroBridge.Windows;",
    "overlay Engine using",
)
text = replace_once(text, "    private AllegroBoardPoint? _firstPick;\n", "    private EngineInteractionPoint? _firstPick;\n", "overlay first pick")
text = replace_once(text, "    private AllegroBoardPoint? _secondPick;\n", "    private EngineInteractionPoint? _secondPick;\n", "overlay second pick")
text = text.replace("AllegroInteractionFeedbackKind", "EngineInteractionFeedbackKind")
text = text.replace("AllegroPointerState", "EnginePointerState")
text = text.replace("AllegroInteractionFeedback", "EngineInteractionFeedback")
text = text.replace("AllegroInteractionObjectKind", "EnginePickedObjectKind")
text = text.replace("EnginePickedObjectKind.ClineSegment", "EnginePickedObjectKind.TraceSegment")
text = text.replace("EnginePickedObjectKind.Cline", "EnginePickedObjectKind.Trace")
text = text.replace("_firstPick.Units", "_firstPick.NativeUnits")
text = text.replace("pointerPoint.Units", "pointerPoint.NativeUnits")
text = text.replace("pointerPoint.X", "pointerPoint.NativeX")
text = text.replace("pointerPoint.Y", "pointerPoint.NativeY")
text = text.replace("_firstPick.X", "_firstPick.NativeX")
text = text.replace("_firstPick.Y", "_firstPick.NativeY")

text = replace_between(
    text,
    "    private static string DescribeSelection(",
    "    private static InteractiveRoutePickSummary CreatePickSummary(",
    """    private static string DescribeSelection(
        EnginePickedObjectKind objectKind,
        EngineInteractionPoint? point)
    {
        var objectName = DescribeObjectKind(objectKind);
        if (point is null)
        {
            return objectName;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{objectName} at ({Convert.ToDouble(point.NativeX):0.###}, " +
            $"{Convert.ToDouble(point.NativeY):0.###}) {DisplayUnits(point.NativeUnits)}");
    }

""",
    "overlay selection description",
)
text = replace_between(
    text,
    "    private static InteractiveRoutePickSummary CreatePickSummary(",
    "    private static string DescribeObjectKind(",
    """    private static InteractiveRoutePickSummary CreatePickSummary(
        EnginePickedObjectKind objectKind,
        EngineInteractionPoint? point)
    {
        var location = point is null
            ? "Board location unavailable"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"X {Convert.ToDouble(point.NativeX):0.###}  ·  " +
                $"Y {Convert.ToDouble(point.NativeY):0.###} {DisplayUnits(point.NativeUnits)}");
        return new(DescribeObjectKind(objectKind), location);
    }

""",
    "overlay pick summary",
)
text = replace_between(
    text,
    "    private Brush ResolvePointerBrush(",
    "    private AllegroScreenPoint? TryMapBoardPoint(",
    """    private Brush ResolvePointerBrush(AllegroScreenPoint cursor)
    {
        // A current Windows pointer does not renew a native verdict. Require
        // Engine causal age plus the exact ordered viewport evidence before color.
        if (_feedback.SemanticFeedback is not { FeedbackKind: EngineInteractionFeedbackKind.Pointer } semantic ||
            _canvasView?.Canvas is not { } canvas ||
            _feedback.Pointer?.Sequence != semantic.Sequence ||
            _feedback.Viewport is not { } viewportFeedback ||
            viewportFeedback.OperationId != semantic.OperationId ||
            viewportFeedback.Sequence >= semantic.Sequence ||
            !semantic.IsCurrent || !viewportFeedback.IsCurrent ||
            viewportFeedback.Viewport is not { } viewport ||
            !_binding.ValidateCanvasView(_canvasView).IsAvailable ||
            !_canvasView.MatchesViewport(
                viewport.MinimumX, viewport.MinimumY,
                viewport.MaximumX, viewport.MaximumY, viewport.NativeUnits) ||
            !semantic.TryGetCaptureAge(out var age) ||
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
            EnginePointerState.Valid => BoardOverlayWindow.ValidBrush,
            EnginePointerState.Invalid => BoardOverlayWindow.InvalidBrush,
            _ => BoardOverlayWindow.NeutralBrush
        };
    }

""",
    "overlay pointer classification",
)
text = replace_between(
    text,
    "    private AllegroScreenPoint? TryMapBoardPoint(",
    "    private void BindingOnInvalidated(",
    """    private AllegroScreenPoint? TryMapBoardPoint(EngineInteractionPoint? point)
    {
        if (point is null || _feedback.Viewport is not { } viewportFeedback ||
            viewportFeedback.Viewport is not { } viewport || !viewportFeedback.IsCurrent ||
            _canvasView is null || !_binding.ValidateCanvasView(_canvasView).IsAvailable ||
            !_canvasView.MatchesViewport(
                viewport.MinimumX, viewport.MinimumY,
                viewport.MaximumX, viewport.MaximumY, viewport.NativeUnits) ||
            !_binding.TryMapBoardPoint(
                _canvasView,
                new AllegroBoardPoint(point.NativeX, point.NativeY, point.NativeUnits),
                out var screenPoint))
        {
            return null;
        }
        return screenPoint;
    }

""",
    "overlay Engine point projection",
)
path.write_text(text, encoding="utf-8")


# Route-completion checks now use Engine terminal evidence instead of a fake SDK
# operation handle. This keeps the presentation/non-replay contract executable.
path = Path("tests/PD.Simple.Checks/Program.cs")
text = path.read_text(encoding="utf-8-sig")
if "using CircuitHub.AllegroBridge.Engine.Live;" not in text:
    text = replace_once(
        text,
        "using CircuitHub.AllegroBridge;\n",
        "using CircuitHub.AllegroBridge;\nusing CircuitHub.AllegroBridge.Engine.Live;\n",
        "checks Engine using",
    )
start = text.index("static async Task CheckRouteCompletionAsync()")
text = text[:start] + r'''static async Task CheckRouteCompletionAsync()
{
    var document = new WorkspaceDocumentIdentity(
        "completion-session", 4, 31, 1234, "fixture.brd", "PD_V25");
    var terminal = new EngineOperationTerminal(
        "Complete", true, "ok", "Native success", document);

    var completion = new TaskCompletionSource<EngineOperationTerminal>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    bool feedbackStopped = false;
    async Task Consume(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            feedbackStopped = true;
            throw;
        }
    }

    Task<InteractiveRouteCompletionResult> completing = InteractiveRouteCompletion.WaitAsync(
        token => completion.Task.WaitAsync(token), Consume);
    Require(!completing.IsCompleted, "A live Engine operation was prematurely considered complete.");
    completion.SetResult(terminal);
    InteractiveRouteCompletionResult result = await completing.WaitAsync(TimeSpan.FromSeconds(5));
    Require(result.Terminal == terminal && result.FeedbackFailure is null,
        "Engine terminal evidence was replaced or feedback cleanup failed.");
    Require(feedbackStopped, "Terminal completion did not stop the local feedback reader.");

    var cleanupFailure = new InvalidOperationException("Feedback cleanup failed after terminal delivery.");
    async Task FailOnStop(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw cleanupFailure;
        }
    }
    var cleanupCompletion = new TaskCompletionSource<EngineOperationTerminal>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    Task<InteractiveRouteCompletionResult> cleaning = InteractiveRouteCompletion.WaitAsync(
        token => cleanupCompletion.Task.WaitAsync(token), FailOnStop);
    cleanupCompletion.SetResult(terminal);
    InteractiveRouteCompletionResult cleaned = await cleaning.WaitAsync(TimeSpan.FromSeconds(5));
    Require(cleaned.Terminal == terminal && ReferenceEquals(cleaned.FeedbackFailure, cleanupFailure),
        "A presentation cleanup failure replaced Engine terminal evidence.");

    var nativeFailure = new IOException("Engine terminal wait failed.");
    Exception? observed = null;
    try
    {
        await InteractiveRouteCompletion.WaitAsync(
            _ => Task.FromException<EngineOperationTerminal>(nativeFailure),
            _ => Task.FromException(new InvalidOperationException("Independent feedback failure.")));
    }
    catch (Exception exception)
    {
        observed = exception;
    }
    Require(ReferenceEquals(observed, nativeFailure),
        "Feedback cleanup replaced the primary Engine terminal-wait exception.");

    var synchronousFailure = new InvalidOperationException("Synchronous presentation failure.");
    InteractiveRouteCompletionResult synchronous = await InteractiveRouteCompletion.WaitAsync(
        _ => Task.FromResult(terminal),
        _ => throw synchronousFailure);
    Require(synchronous.Terminal == terminal &&
        ReferenceEquals(synchronous.FeedbackFailure, synchronousFailure),
        "A synchronous feedback callback prevented Engine terminal admission.");

    Console.WriteLine("PASS: Engine route completion keeps terminal evidence primary, stops local feedback independently, and reports presentation failures without native replay.");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
'''
path.write_text(text, encoding="utf-8")


# Two route-only files no longer need direct SDK debt. Existing corridor/session
# seams remain explicit until their own migrations land.
path = Path("scripts/check-engine-boundary.py")
text = path.read_text(encoding="utf-8-sig")
for item in (
    '    "src/PD.Simple/InteractiveRouteCompletion.cs",\n',
    '    "src/PD.Simple/InteractiveRouteOverlayFeedbackState.cs",\n',
):
    if text.count(item) != 1:
        raise SystemExit(f"boundary allowlist entry missing: {item.strip()}")
    text = text.replace(item, "", 1)
path.write_text(text, encoding="utf-8")

print("Engine route migration transformations applied.")
