using System.IO;
using System.Windows;
using System.Windows.Threading;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.Simple.Corridor;

namespace PD.Simple;

public sealed record SimpleSessionState(
    bool IsReady,
    string Design,
    long BoardGeneration,
    string SessionId,
    bool CanRunCorridor,
    string? UnavailableDetail,
    long CatalogGeneration,
    long ConnectionGeneration = 0);

/// <summary>
/// Owns PD Simple's one stable Engine session. Engine owns discovery evidence,
/// lower connection resources, document switching, operation tracking, and
/// deterministic teardown; PD retains product workflow and UI policy.
/// </summary>
public sealed partial class BridgeSession : IAsyncDisposable, IDpViaCorridorService
{
    internal const string PresentationCandidateBoundary =
        "CANDIDATE: live connection/lifecycle migration is ready; " +
        "review capture and Allegro overlay wiring require PRESENTATION_API_READY.";

    private readonly Dispatcher _dispatcher =
        Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private readonly HashSet<Task> _operations = [];
    private IEngineOperationControl? _route;
    private TaskCompletionSource<IEngineOperationControl?>? _routeReady;
    private IEngineOperationControl? _routeCancellationOperation;
    private Task? _routeCancellationTask;
    private EngineSessionTarget? _currentTarget;
    private WorkspaceDocumentIdentity? _lastReadyDocument;
    private InteractiveRouteInteractionState _routeState =
        InteractiveRouteInteractionState.Inactive;
    private Task? _disposeTask;
    private bool _routeCancelRequested;
    private bool _busy;
    private bool _routeInProgress;
    private bool _connecting;
    private bool _disposeRequested;
    private bool _disposed;
    private long _connectionGeneration;
    private string? _failure;

    public BridgeSession()
    {
        EngineSession = AllegroEngineSession.Create(new EngineSessionOptions
        {
            ConnectionTimeout = TimeSpan.FromSeconds(90),
            DisposalTimeout = TimeSpan.FromSeconds(20),
        });
        EngineSession.StateChanged += EngineSession_StateChanged;
        PublishState(EngineSession.State);
    }

    public AllegroEngineSession EngineSession { get; }

    public AllegroWorkspace Workspace => EngineSession.Workspace;

    public SimpleSessionState State
    {
        get;
        private set;
    } = new(
        false,
        string.Empty,
        0,
        string.Empty,
        false,
        "Open PD Simple from Allegro with a board open.",
        0);

    public bool HasReadySession =>
        !_disposeRequested &&
        EngineSession.State.ConnectionState == EngineConnectionState.Ready;

    // Temporary Lane 06 compatibility. PRESENTATION_API_READY removes this
    // native-era name when the corridor view accepts Engine session/presentation.
    public bool HasLiveNativeSession => HasReadySession;

    public bool HasRouteInProgress
    {
        get
        {
            lock (_gate)
            {
                return _routeInProgress;
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                if (_busy)
                {
                    return true;
                }
            }
            return ConnectionSwitchPolicy.HasActiveOperation(EngineSession.State);
        }
    }

    public bool CanConnect =>
        !_disposeRequested &&
        !_connecting &&
        !IsBusy &&
        !HasPendingRouteRecovery &&
        ConnectionSwitchPolicy.CanChooseConnection(
            EngineSession.State,
            EngineSession.UnresolvedOperations);

    public bool CanReconnectCurrent =>
        CanConnect &&
        _currentTarget is not null &&
        EngineSession.State.ConnectionState == EngineConnectionState.Ready;

    public bool CanRoute =>
        !_connecting &&
        HasReadySession &&
        CanStartOperation(
            allowRouteRecovery: false,
            EngineCapabilities.Picking,
            EngineCapabilities.Routing);

    public bool CanClearFirstPick =>
        HasRouteInProgress &&
        _route is EngineEndpointPick { IsTerminal: false } &&
        RouteState.HasFirstPick;

    public string? RouteResultWarning
    {
        get
        {
            EngineSessionSnapshot state = EngineSession.State;
            if (ConnectionSwitchPolicy.HasUncertainOutcome(
                    state,
                    EngineSession.UnresolvedOperations))
            {
                return "The Engine outcome remains uncertain. Inspect Allegro and retain the session for recovery; no operation was replayed.";
            }
            if (ConnectionSwitchPolicy.HasRequiredRecovery(state) ||
                _lastEngineEdit is { State: EngineMutationState.RecoverableGeometry })
            {
                return "The Engine result retains operation-specific recovery. Use the enabled Undo before continuing this workflow.";
            }
            return null;
        }
    }

    public bool CanUndoRoute =>
        !IsBusy &&
        HasReadySession &&
        _lastEngineEdit is { CanUndo: true } edit &&
        edit.Document == EngineSession.State.Document;

    public InteractiveRouteInteractionState RouteState => _routeState;

    public event EventHandler<SimpleSessionState>? StateChanged;

    public event EventHandler<InteractiveRouteInteractionState>? RouteStateChanged;

    public event EventHandler<string>? Faulted;

    public Task ConnectAsync(
        EngineSessionTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind != EngineSessionTargetKind.LaunchContext)
        {
            throw new ArgumentException(
                "ConnectAsync requires an Engine launch-context target.",
                nameof(target));
        }
        return ChangeConnectionAsync(target, cancellationToken);
    }

    public Task AttachAsync(
        EngineSessionTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind != EngineSessionTargetKind.RunningInstance)
        {
            throw new ArgumentException(
                "AttachAsync requires an explicitly selected running Engine target.",
                nameof(target));
        }
        return ChangeConnectionAsync(target, cancellationToken);
    }

    public Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        EngineSessionTarget target = _currentTarget ??
            throw new InvalidOperationException(
                "Choose an Engine target before refreshing the current connection.");
        return ChangeConnectionAsync(target, cancellationToken);
    }

    public Task<InteractiveRouteResult> RouteAsync(
        decimal width,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width is < 0.1m or > 10_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        BeginOperation(
            route: true,
            allowRouteRecovery: false,
            EngineCapabilities.Picking,
            EngineCapabilities.Routing);
        _lastEngineEdit = null;
        Task<InteractiveRouteResult> task = ExecuteEngineRouteAsync(width);
        Track(task);
        return task.WaitAsync(cancellationToken);
    }

    public async Task CancelRouteAsync()
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

        IEngineOperationControl? operation = await ready.WaitAsync(_lifetime.Token);
        if (operation is not null)
        {
            await RequestRouteCancellationOnceAsync(operation);
        }
    }

    public async Task ClearFirstPickAsync()
    {
        EngineEndpointPick pick;
        lock (_gate)
        {
            pick = _route as EngineEndpointPick ??
                throw new InvalidOperationException(
                    "No active endpoint pick is ready to clear its first selection.");
            if (pick.IsTerminal)
            {
                throw new InvalidOperationException(
                    "Endpoint input has ended. No clear-first-pick action was sent.");
            }
        }
        await pick.ClearFirstAsync(_lifetime.Token);
    }

    public Task<InteractiveRouteResult> UndoRouteAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanUndoRoute)
        {
            throw new InvalidOperationException(
                "No current Engine route result retains operation-specific Undo authority.");
        }

        BeginOperation(
            route: false,
            allowRouteRecovery: true,
            EngineCapabilities.Routing);
        Task<InteractiveRouteResult> task = ExecuteEngineUndoAsync();
        Track(task);
        return task.WaitAsync(cancellationToken);
    }

    // Temporary Lane 06 compatibility. Calls stop at the named checkpoint until
    // the WPF presentation owner is released by Session 01.
    public Task<DpViaCorridorNativeCapture> CaptureDpViaCorridorNativeAsync(
        DpViaCorridorZoomResult zoom)
    {
        ArgumentNullException.ThrowIfNull(zoom);
        return Task.FromException<DpViaCorridorNativeCapture>(
            new NotSupportedException(PresentationCandidateBoundary));
    }

    internal void SetDpViaCorridorOverlay(DpViaCorridorBoardOverlay? overlay)
    {
        if (overlay is not null)
        {
            throw new NotSupportedException(PresentationCandidateBoundary);
        }
    }

    private Task ChangeConnectionAsync(
        EngineSessionTarget target,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        EngineConnectionAction action = ConnectionSwitchPolicy.SelectAction(
            EngineSession.State,
            target);
        lock (_gate)
        {
            if (_connecting || _busy)
            {
                throw new InvalidOperationException(
                    "Finish the active operation or connection change first.");
            }
            if (HasPendingRouteRecovery)
            {
                throw new InvalidOperationException(
                    "Use the retained Engine route recovery before changing boards.");
            }
            _failure = null;
            _connecting = true;
        }

        Task task = ChangeConnectionCoreAsync(target, action, cancellationToken);
        Track(task);
        return task;
    }

    private async Task ChangeConnectionCoreAsync(
        EngineSessionTarget target,
        EngineConnectionAction action,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            cancellationToken);
        try
        {
            switch (action)
            {
                case EngineConnectionAction.ConnectLaunchTarget:
                    await EngineSession.ConnectAsync(target, linked.Token);
                    break;
                case EngineConnectionAction.AttachRunningTarget:
                    await EngineSession.AttachAsync(target, linked.Token);
                    break;
                case EngineConnectionAction.SwitchTarget:
                    await EngineSession.SwitchAsync(target, linked.Token);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }

            _currentTarget = target;
            _failure = null;
        }
        catch (Exception exception)
        {
            _failure = exception.Message;
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _connecting = false;
            }
            PublishState(EngineSession.State);
        }
    }

    private void BeginOperation(
        bool route,
        bool allowRouteRecovery,
        params EngineCapabilityId[] requiredCapabilities)
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        if (!CanStartOperation(allowRouteRecovery, requiredCapabilities))
        {
            throw new InvalidOperationException(
                State.UnavailableDetail ??
                "The required Engine capability is unavailable or the session retains unfinished work.");
        }

        lock (_gate)
        {
            if (_busy || _connecting)
            {
                throw new InvalidOperationException(
                    "Finish the active operation or connection change first.");
            }
            _busy = true;
            _routeInProgress = route;
            if (route)
            {
                _routeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _routeCancelRequested = false;
                _routeCancellationOperation = null;
                _routeCancellationTask = null;
                PublishRouteState(new(
                    true,
                    false,
                    "Waiting for first endpoint",
                    "Select the route start and end in Allegro."));
            }
        }
        PublishState(EngineSession.State);
    }

    private bool CanStartOperation(
        bool allowRouteRecovery,
        params EngineCapabilityId[] requiredCapabilities)
    {
        EngineSessionSnapshot state = EngineSession.State;
        return state.ConnectionState == EngineConnectionState.Ready &&
            (allowRouteRecovery || !HasPendingRouteRecovery) &&
            !ConnectionSwitchPolicy.HasActiveOperation(state) &&
            !ConnectionSwitchPolicy.HasUncertainOutcome(
                state,
                EngineSession.UnresolvedOperations) &&
            !ConnectionSwitchPolicy.HasRequiredRecovery(state) &&
            requiredCapabilities.All(state.Capabilities.Supports);
    }

    private bool HasPendingRouteRecovery =>
        _lastEngineEdit is
        {
            State: EngineMutationState.RecoverableGeometry,
            CanUndo: true,
        };

    private void EndOperation()
    {
        lock (_gate)
        {
            _busy = false;
            _routeInProgress = false;
            _route = null;
            _routeReady?.TrySetResult(null);
            _routeCancellationOperation = null;
            _routeCancellationTask = null;
        }
        PublishRouteState(InteractiveRouteInteractionState.Inactive);
        PublishState(EngineSession.State);
    }

    private Task RequestRouteCancellationOnceAsync(IEngineOperationControl operation)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_routeCancellationOperation, operation))
            {
                _routeCancellationOperation = operation;
                _routeCancellationTask = RequestAsync();
            }
            return _routeCancellationTask!;
        }

        async Task RequestAsync()
        {
            if (operation.IsTerminal)
            {
                return;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await operation.CancelAsync(timeout.Token);
        }
    }

    private void PublishRouteState(InteractiveRouteInteractionState state)
    {
        if (!_dispatcher.CheckAccess())
        {
            Post(() => PublishRouteState(state));
            return;
        }
        _routeState = state;
        RouteStateChanged?.Invoke(this, state);
    }

    private void EngineSession_StateChanged(
        object? sender,
        EngineSessionSnapshot state)
    {
        if (!ReferenceEquals(sender, EngineSession))
        {
            return;
        }
        Post(() => PublishState(state));
    }

    private void PublishState(EngineSessionSnapshot state)
    {
        if (_disposed)
        {
            return;
        }
        if (!_dispatcher.CheckAccess())
        {
            Post(() => PublishState(state));
            return;
        }

        WorkspaceDocumentIdentity? document = state.Document;
        if (state.ConnectionState == EngineConnectionState.Ready &&
            document is not null &&
            document != _lastReadyDocument)
        {
            bool changedDocument = _lastReadyDocument is not null;
            _lastReadyDocument = document;
            _connectionGeneration++;
            _managedAnalysis = null;
            if (changedDocument)
            {
                _lastEngineEdit = null;
            }
        }

        bool ready = state.ConnectionState == EngineConnectionState.Ready &&
            document is not null;
        bool uncertainOutcome = ConnectionSwitchPolicy.HasUncertainOutcome(
            state,
            EngineSession.UnresolvedOperations);
        bool requiredRecovery = ConnectionSwitchPolicy.HasRequiredRecovery(state) ||
            HasPendingRouteRecovery;
        bool canRunCorridor = ready &&
            state.Capabilities.Supports(EngineCapabilities.SceneRead) &&
            !ConnectionSwitchPolicy.HasActiveOperation(state) &&
            !uncertainOutcome &&
            !requiredRecovery;

        string? unavailableDetail;
        if (uncertainOutcome)
        {
            unavailableDetail =
                "The Engine session retains an uncertain operation. Inspect Allegro and retain this session for recovery.";
        }
        else if (requiredRecovery)
        {
            unavailableDetail =
                "The Engine session retains operation-specific recovery. Use the enabled recovery action before continuing.";
        }
        else if (!ready)
        {
            unavailableDetail = ConnectionSwitchPolicy.DescribeDiagnostics(
                state.Diagnostics,
                _failure ?? (state.ConnectionState switch
                {
                    EngineConnectionState.Disconnected =>
                        "Choose an available Allegro Engine target.",
                    EngineConnectionState.Connecting =>
                        "Connecting to the selected Allegro Engine target…",
                    EngineConnectionState.Switching =>
                        "Verifying the selected Allegro Engine target…",
                    EngineConnectionState.Recovering =>
                        "The Engine session is reconciling its current document.",
                    EngineConnectionState.Faulted =>
                        "The Allegro Engine session is unavailable. Review its diagnostics before continuing.",
                    EngineConnectionState.Disposing =>
                        "The Allegro Engine session is closing.",
                    EngineConnectionState.Disposed =>
                        "The Allegro Engine session is closed.",
                    _ => "The Allegro Engine session is unavailable.",
                }));
        }
        else
        {
            // A failed atomic switch keeps the previous Engine connection ready.
            unavailableDetail = _failure;
        }

        State = new(
            ready,
            document?.Design ?? string.Empty,
            document?.BoardGeneration ?? 0,
            document?.SessionId ?? string.Empty,
            canRunCorridor,
            unavailableDetail,
            _connectionGeneration,
            _connectionGeneration);
        StateChanged?.Invoke(this, State);
    }

    private void Post(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted)
        {
            return;
        }
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                action();
            }
        });
    }

    private void Track(Task task)
    {
        lock (_gate)
        {
            _operations.Add(task);
        }
        _ = task.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (_gate)
                {
                    _operations.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ValidateReportPath(string report)
    {
        if (!Path.IsPathFullyQualified(report) ||
            report.Length > 1024 ||
            report.Any(char.IsControl) ||
            !string.Equals(
                Path.GetExtension(report),
                ".rpt",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A fully qualified .rpt output path is required.");
        }
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            task = _disposeTask;
        }
        return new ValueTask(task);
    }

    private async Task DisposeCoreAsync()
    {
        _disposeRequested = true;
        try
        {
            _lifetime.Cancel();
        }
        catch (AggregateException exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                "PD operation cancellation callbacks failed during close: {0}",
                exception.Message);
        }

        try
        {
            await EngineSession.DisposeAsync();
        }
        finally
        {
            Task[] operations;
            lock (_gate)
            {
                operations = _operations.ToArray();
            }
            try
            {
                await Task.WhenAll(operations);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceInformation(
                    "PD operation wait ended during Engine shutdown: {0}",
                    exception.Message);
            }

            EngineSession.StateChanged -= EngineSession_StateChanged;
            _lifetime.Dispose();
            _disposed = true;
        }
    }
}
