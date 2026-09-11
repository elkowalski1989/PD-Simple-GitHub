using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CircuitHub.AllegroBridge;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Windows;
using PD.Simple.Corridor;

namespace PD.Simple;

public sealed record SimpleSessionState(bool IsReady, string Design, long BoardGeneration,
    string SessionId, bool CanRunCorridor, string? UnavailableDetail, long CatalogGeneration,
    long ConnectionGeneration = 0);

// The application owns tool policy and presentation. The packaged SDK owns the
// connection, entitlement, native dispatch and session/catalog freshness checks.
public sealed partial class BridgeSession : IAsyncDisposable, IDpViaCorridorService
{
    private const string CorridorCommand = AllegroPcbContract.ReadBoardInputsCommand;
    private const string ZoomCommand = AllegroPcbContract.ZoomRegionCommand;
    private const string RouteCommand = AllegroPcbContract.CreateTraceCommand;
    private const string UndoCommand = AllegroPcbContract.UndoTraceCommand;
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _operations = [];
    private BoardConnection? _connection;
    private AllegroBridgeSession? _session => _connection?.Session;
    private AllegroPcbSession? _pcb;
    private AllegroDesktopBinding? _desktop => _connection?.Desktop;
    private BoardOverlayController? _overlay => _connection?.Overlay;
    private IEngineOperationControl? _route;
    private TaskCompletionSource<IEngineOperationControl?>? _routeReady;
    private Task<InteractiveRouteResult>? _routeTask;
    private readonly Dictionary<IEngineOperationControl, Task> _nativeCancellationTasks = new(ReferenceEqualityComparer.Instance);
    private bool _routeCancelRequested;
    private bool _busy;
    private bool _routeInProgress;
    private bool _connecting;
    private bool _rebindingTools;
    private (long Catalog, long Session, long Board)? _lastAutomaticRebind;
    private string? _bridgeDirectory;
    private long _connectionGeneration;
    private Window? _mainWindow;
    private bool _disposed;
    private bool _outcomeUncertain;
    private bool _recoveryRequired;
    private bool _sessionEnded;
    private AllegroSessionBinding? _undoBinding;
    private string? _failure = "Open PD Simple from Allegro with a board open.";

    public SimpleSessionState State
    {
        get; private set;
    } = new(false, "", 0, "", false,
        "Open PD Simple from Allegro with a board open.", 0);
    public bool HasLiveNativeSession => !_disposed && _session is not null && State.IsReady;
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
                return _busy;
            }
        }
    }
    public bool CanConnect => !_disposed && !_connecting && !_rebindingTools && !IsBusy;
    public bool CanReconnectCurrent => CanConnect && _bridgeDirectory is not null;
    public bool CanRoute => !_connecting && HasLiveNativeSession && _desktop is { IsValid: true } &&
        _pcb is { IsCurrent: true } && !_outcomeUncertain && !_recoveryRequired;
    public bool CanClearFirstPick => HasRouteInProgress &&
        _route is EngineEndpointPick { IsTerminal: false } && RouteState.HasFirstPick;
    public string? RouteResultWarning => _outcomeUncertain
        ? "The native effect could not be verified. Inspect Allegro before any further work; no edit was replayed."
        : _recoveryRequired ? "The operation left verified recoverable geometry. Use the enabled edit-specific Undo before continuing." : null;
    public bool CanUndoRoute => !IsBusy && !_outcomeUncertain &&
        _lastEngineEdit is { CanUndo: true } && _undoBinding is { } binding &&
        SameBoard(binding) && _pcb is { IsCurrent: true };
    public InteractiveRouteInteractionState RouteState =>
        _overlay?.State ?? InteractiveRouteInteractionState.Inactive;
    internal BoardOverlayController? DpViaCorridorOverlayOwner => _overlay;

    public event EventHandler<SimpleSessionState>? StateChanged;
    public event EventHandler<InteractiveRouteInteractionState>? RouteStateChanged;
    public event EventHandler<string>? Faulted;
    public event EventHandler? FocusRequested;

    public Task ConnectAsync(string bridgeDirectory, Window window)
    {
        if (_connection is not null)
        {
            throw new InvalidOperationException("Choose Reconnect to change an existing connection.");
        }
        if (string.IsNullOrWhiteSpace(bridgeDirectory) || !Directory.Exists(bridgeDirectory))
        {
            throw new ArgumentException("A live Allegro Bridge directory is required.");
        }
        _bridgeDirectory = Path.GetFullPath(bridgeDirectory);
        return ChangeConnectionAsync(window, token =>
            PrepareConnectionAsync(_bridgeDirectory, token));
    }

    public Task AttachAsync(AllegroDesktopCandidate candidate, Window window)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ConnectionSwitchPolicy.RequireAttachmentAllowed(IsBusy, _outcomeUncertain, _recoveryRequired);
        return ChangeConnectionAsync(window, async token =>
        {
            AllegroDesktopAttachment attachment;
            try
            {
                attachment = await AllegroDesktop.AttachAsync(candidate, cancellationToken: token);
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(
                    "The selected Bridge session did not respond. It may have stopped or be busy. " +
                    "Run pd_simple in that Allegro window when idle, then refresh the chooser.", exception);
            }
            try
            {
                // The protected host supplies its own standard PCB operations; no consumer SKILL is loaded.
                var tools = await attachment.Session.OpenPcbAsync(token);
                return new BoardConnection(attachment.Session, attachment.Desktop, tools,
                    candidate.BridgeDirectory!, attachment);
            }
            catch
            {
                await attachment.DisposeAsync();
                throw;
            }
        }, isAttachment: true);
    }

    public Task ReconnectAsync(Window window)
    {
        if (_bridgeDirectory is null)
        {
            throw new InvalidOperationException("Choose an open board in the connection chooser first.");
        }
        string directory = _bridgeDirectory;
        return ChangeConnectionAsync(window, async token =>
        {
            if (_session is { Context.IsConnected: true, Context.IsCompatible: true } && !_sessionEnded)
            {
                await RefreshToolBindingAsync(manual: true);
                if (_desktop is not null && _desktop.IsCurrentFor(_session.Binding))
                {
                    return null;
                }
            }
            return await PrepareConnectionAsync(directory, token);
        });
    }

    private async Task<BoardConnection?> PrepareConnectionAsync(string directory,
        CancellationToken cancellationToken)
    {
        var session = await AllegroBridgeSession.ConnectAndWaitUntilReadyAsync(
            new AllegroBridgeConnectOptions(directory),
            new AllegroBridgeReadinessRequirements { MinimumCustomSkillContractVersion = 3 },
            cancellationToken);
        AllegroDesktopBinding? desktop = null;
        try
        {
            desktop = await AllegroDesktop.BindSessionAsync(session, cancellationToken);
            var tools = await session.OpenPcbAsync(cancellationToken);
            return new BoardConnection(session, desktop, tools, directory);
        }
        catch
        {
            desktop?.Dispose();
            await session.DisposeAsync();
            throw;
        }
    }



    private Task ChangeConnectionAsync(Window window,
        Func<CancellationToken, Task<BoardConnection?>> prepare, bool isAttachment = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanConnect)
        {
            throw new InvalidOperationException("Finish or cancel the active operation before reconnecting.");
        }
        _connecting = true;
        var task = ChangeConnectionCoreAsync(window, prepare, isAttachment);
        Track(task);
        return task;
    }

    private async Task ChangeConnectionCoreAsync(Window window,
        Func<CancellationToken, Task<BoardConnection?>> prepare, bool isAttachment)
    {
        // Register this task before its first asynchronous work so closing also
        // waits for a pending attachment to be cancelled and released.
        await Task.Yield();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        BoardConnection? next = null;
        try
        {
            next = await prepare(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (next is null)
            {
                return;
            }
            ConnectionSwitchPolicy.RequireSafeSwitch(_session?.Binding, next.Session.Binding,
                IsBusy, _outcomeUncertain, _recoveryRequired);
            if (!next.Desktop.IsCurrentFor(next.Session.Binding) || !next.Pcb.IsCurrent)
            {
                throw new InvalidOperationException("The selected board changed while attaching. Refresh and select it again.");
            }

            // No old connection is released until the exact target and its
            // tools, desktop and overlay are ready. Cancel/failure keeps it.
            var previous = _connection;
            bool retainUndo = !isAttachment || ConnectionSwitchPolicy.RetainsUndoAuthority(
                previous?.Desktop.IsCurrentFor(next.Session.Binding) == true,
                previous?.Desktop.AllegroProcessId, previous?.Desktop.TopLevelWindowHandle ?? 0,
                next.Desktop.AllegroProcessId, next.Desktop.TopLevelWindowHandle);
            new WindowInteropHelper(window).Owner = next.Desktop.TopLevelWindowHandle;
            if (previous is not null)
            {
                UnsubscribeConnection(previous);
            }
            _connection = next;
            if (!retainUndo)
            {
                // A directory/PID may be reused after Allegro exits. Old Undo
                // authority cannot follow a selection into a new native window.
                _undoBinding = null;
                _lastEngineEdit = null;
            }
            _pcb = next.Pcb;
            _bridgeDirectory = next.Directory;
            _mainWindow = window;
            _sessionEnded = false;
            _lastAutomaticRebind = null;
            _connectionGeneration++;
            _failure = null;
            SubscribeConnection(next);
            next = null; // Ownership has transferred to this session owner.
            PublishState();
            if (previous is not null)
            {
                try
                {
                    await previous.DisposeAsync();
                }
                catch (Exception exception)
                {
                    _failure = "Connected to the selected board, but the previous client did not fully close: " + exception.Message;
                }
            }
        }
        catch (Exception exception)
        {
            if (_connection is null)
            {
                _failure = exception is OperationCanceledException && !_disposed
                    ? "Connection timed out. Choose Reconnect to try an available board. No operation was replayed."
                    : exception.Message;
            }
            throw;
        }
        finally
        {
            try
            {
                if (next is not null)
                {
                    await next.DisposeAsync();
                }
            }
            finally
            {
                _connecting = false;
                PublishState();
            }
        }
    }

    private async Task RefreshToolBindingAsync(bool manual = false)
    {
        if (_disposed || _rebindingTools || IsBusy || (!manual && _connecting) ||
            _sessionEnded || _session is not { Context.IsConnected: true, Context.IsCompatible: true } session ||
            _pcb is not { } previous)
        {
            return;
        }
        if (previous.IsCurrent)
        {
            return;
        }
        var attempt = (session.SkillExtensions.Catalog.Generation,
            session.Binding.SessionGeneration, session.Binding.BoardGeneration);
        if (!manual && _lastAutomaticRebind == attempt)
        {
            return;
        }
        _lastAutomaticRebind = attempt;
        _rebindingTools = true;
        try
        {
            // Reacquire the protected package-owned facade. This does not replay
            // an operation, replace consumer tools, or reset edit evidence.
            var current = await session.OpenPcbAsync(_lifetime.Token);
            if (!ReferenceEquals(_session, session) || !current.IsCurrent)
            {
                throw new InvalidOperationException("The native session changed while refreshing its tool binding.");
            }
            _pcb = current;
            _failure = null;
        }
        catch (Exception exception)
        {
            _failure = "Tool binding refresh failed: " + exception.Message;
            if (manual)
            {
                throw;
            }
        }
        finally
        {
            _rebindingTools = false;
            PublishState();
        }
    }

    private void ScheduleToolBindingRefresh() => Post(async () => await RefreshToolBindingAsync());





    public AllegroBoardObservation CaptureObservation() => RequireSession().CaptureObservation();

    internal Task<AllegroNativeViewObservation> CaptureDpViaCorridorViewportAsync() =>
        RequireSession().CaptureNativeViewAsync(_lifetime.Token).AsTask();

    public async Task<DpViaCorridorNativeCapture> CaptureDpViaCorridorNativeAsync(DpViaCorridorZoomResult zoom)
    {
        var sdk = RequireSession();
        var desktop = _desktop ?? throw new InvalidOperationException("The Allegro window has not been bound.");
        Task<DpViaCorridorNativeCapture> Capture() =>
            DpViaCorridorNativeCapture.CaptureAsync(sdk, desktop, zoom, _lifetime.Token);
        return _overlay is { } overlay ? await overlay.WithCanvasObservationPausedAsync(Capture) : await Capture();
    }

    internal void SetDpViaCorridorOverlay(DpViaCorridorBoardOverlay? overlay)
    {
        if (overlay is null)
        {
            _overlay?.SetDpViaCorridorOverlay(null);
            return;
        }
        _ = RequireSession();
        if (HasRouteInProgress)
        {
            throw new InvalidOperationException("Finish Point-to-point Trace before showing corridor highlighting.");
        }
        (_overlay ?? throw new InvalidOperationException("The Allegro canvas owner is unavailable."))
            .SetDpViaCorridorOverlay(overlay);
    }

    public Task<InteractiveRouteResult> RouteAsync(decimal width, CancellationToken cancellationToken = default)
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
        var operation = await ready.WaitAsync(_lifetime.Token);
        if (operation is not null)
        {
            await CancelNativeRouteOnceAsync(operation);
        }
    }

    public async Task ClearFirstPickAsync()
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

    public Task<InteractiveRouteResult> UndoRouteAsync(CancellationToken cancellationToken = default)
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

    private AllegroPcbSession BeginOperation(string command, bool route = false)
    {
        _ = RequireSession();
        if (_outcomeUncertain)
        {
            throw new InvalidOperationException("A prior operation has an uncertain native outcome. Inspect Allegro and reopen PD Simple before starting another operation.");
        }
        if (_recoveryRequired && command != UndoCommand)
        {
            throw new InvalidOperationException("The failed route left verified recoverable geometry. Use guarded Undo before starting another tool.");
        }
        var binding = _pcb;
        if (binding is not { IsCurrent: true } || !binding.Capabilities.Any(item => item.Id == command && item.IsAvailable))
        {
            throw new InvalidOperationException("This standard PCB operation is unavailable or its binding is stale. Choose Reconnect; no operation was replayed.");
        }
        lock (_gate)
        {
            if (_busy || _connecting || _rebindingTools)
            {
                throw new InvalidOperationException("Finish the active operation or connection change before starting another tool.");
            }
            _busy = true;
            _routeInProgress = route;
            if (route)
            {
                _routeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _routeCancelRequested = false;
                _nativeCancellationTasks.Clear();
            }
        }
        PublishState();
        return binding;
    }

    private void EndOperation()
    {
        lock (_gate)
        {
            _busy = false;
            _routeInProgress = false;
            _route = null;
        }
        PublishState();
        ScheduleToolBindingRefresh();
    }



    private void RequireCorridorContext(AllegroSessionBinding expected, string design)
    {
        var session = RequireSession();
        if (!SameBoard(expected) || !SameNativeIdentity(session.Binding, expected) ||
            !string.Equals(session.CaptureObservation().Snapshot.Design, design, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The board changed during the corridor operation. Run again on the current board.");
        }
    }

    private static void RequireCorridorReceipt(AllegroOperationReceipt receipt,
        AllegroSessionBinding expected, string command)
    {
        if (receipt.CommandId != command || !SameNativeIdentity(receipt.Binding, expected))
        {
            throw new InvalidDataException("The native result belongs to another command, board, or session.");
        }
        if (receipt.State != AllegroOperationState.Complete)
        {
            throw new InvalidOperationException(receipt.Message);
        }
    }

    private static bool SameNativeIdentity(AllegroSessionBinding actual, AllegroSessionBinding expected) =>
        actual.SessionId == expected.SessionId && actual.SessionGeneration == expected.SessionGeneration &&
        actual.BoardGeneration == expected.BoardGeneration && actual.ProcessId == expected.ProcessId &&
        actual.ProtocolVersion == expected.ProtocolVersion && actual.ResidentPackage == expected.ResidentPackage;



    private void PresentRoute(Action present)
    {
        try
        {
            present();
        }
        catch (Exception exception)
        {
            ReportRoutePresentationFailure(exception);
        }
    }

    private void ReportRoutePresentationFailure(Exception exception)
    {
        string message = "Route feedback or local cleanup failed; native result tracking is unchanged: " + exception.Message;
        System.Diagnostics.Trace.TraceWarning(message);
        try
        {
            Post(() => Faulted?.Invoke(this, message));
        }
        catch (Exception notificationFailure)
        {
            // A closing dispatcher cannot revoke native readback or recovery.
            System.Diagnostics.Trace.TraceWarning("Route warning could not be displayed: {0}", notificationFailure.Message);
        }
    }

    private Task CancelNativeRouteOnceAsync(IEngineOperationControl handle)
    {
        lock (_gate)
        {
            if (!_nativeCancellationTasks.TryGetValue(handle, out var task))
            {
                task = CancelNativeAsync();
                _nativeCancellationTasks.Add(handle, task);
            }
            return task;
        }
        async Task CancelNativeAsync()
        {
            if (handle.IsTerminal)
            {
                return;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // Independent from the local wait lifetime: closing must still be
            // able to cancel a native launch whose handle arrived late.
            await handle.CancelAsync(timeout.Token);
        }
    }



    private AllegroBridgeSession RequireSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return HasLiveNativeSession && _session is { } session ? session :
            throw new InvalidOperationException(State.UnavailableDetail ?? "A current Allegro board connection is required.");
    }

    private bool SameBoard(AllegroSessionBinding binding) => !_disposed && !_sessionEnded && _session is { } session &&
        session.Context.IsConnected && session.Binding.SessionId == binding.SessionId &&
        session.Binding.BoardGeneration == binding.BoardGeneration;

    private static void ValidateReportPath(string report)
    {
        if (!Path.IsPathFullyQualified(report) || report.Length > 1024 || report.Any(char.IsControl) ||
            !string.Equals(Path.GetExtension(report), ".rpt", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A fully qualified .rpt output path is required.");
        }
    }

    private static string FailureMessage(Exception exception) => exception is OperationCanceledException or TimeoutException
        ? "The SDK wait ended without a confirmed native result. The outcome may be unknown; inspect Allegro before retrying."
        : exception.Message;

    private void PublishState()
    {
        if (_disposed)
        {
            return;
        }
        if (!_dispatcher.CheckAccess())
        {
            Post(PublishState);
            return;
        }
        var context = _session?.Context;
        bool boardReady = !_sessionEnded && context is { IsConnected: true, IsCompatible: true } &&
            context.Binding.BoardGeneration > 0;
        bool bindingCurrent = _pcb is { IsCurrent: true };
        bool canRunCorridor = boardReady && bindingCurrent && !_outcomeUncertain && !_recoveryRequired &&
            _pcb!.Capabilities.Any(command => command.Id == CorridorCommand && command.IsAvailable);

        string? unavailableDetail = _failure;
        if (unavailableDetail is null)
        {
            if (_outcomeUncertain)
            {
                unavailableDetail = "The prior native outcome is uncertain. Inspect Allegro before reopening PD Simple.";
            }
            else if (_recoveryRequired)
            {
                unavailableDetail = "The failed route left verified recoverable geometry. Only guarded Undo is available until it succeeds.";
            }
            else if (!boardReady)
            {
                unavailableDetail = "The Allegro board is unavailable.";
            }
            else if (!bindingCurrent)
            {
                unavailableDetail = "Refreshing the tool catalog. If it remains unavailable, choose Reconnect; no operation is replayed.";
            }
        }

        State = new SimpleSessionState(
            boardReady,
            context?.Design ?? "",
            context?.Binding.BoardGeneration ?? 0,
            context?.Binding.SessionId ?? "",
            canRunCorridor,
            unavailableDetail,
            _session?.SkillExtensions.Catalog.Generation ?? 0, _connectionGeneration);
        StateChanged?.Invoke(this, State);
    }

    private void ContextChanged(object? sender, AllegroBoardContext context) => Post(() =>
    {
        if (!ReferenceEquals(sender, _session))
        {
            return;
        }
        if (!context.IsConnected || context.Binding.BoardGeneration != State.BoardGeneration ||
            context.Binding.SessionId != State.SessionId)
        {
            _overlay?.End();
        }
        PublishState();
        ScheduleToolBindingRefresh();
    });
    private void CatalogChanged(object? sender, AllegroSkillCatalog catalog) => Post(() =>
    {
        if (!ReferenceEquals(sender, _session?.SkillExtensions))
        {
            return;
        }
        PublishState();
        ScheduleToolBindingRefresh();
    });
    private void ActivationRequested(object? sender, AllegroSessionActivationRequest request) =>
        Post(() =>
        {
            if (ReferenceEquals(sender, _session))
            {
                FocusRequested?.Invoke(this, EventArgs.Empty);
            }
        });
    private void ResidentSessionEnding(object? sender, AllegroResidentSessionEnding ending) => Post(() =>
    {
        if (!ReferenceEquals(sender, _session))
        {
            return;
        }
        _overlay?.End();
        _sessionEnded = true;
        _failure = "The native Allegro session ended. Choose Reconnect to select an available board.";
        if (_mainWindow is not null)
        {
            new WindowInteropHelper(_mainWindow).Owner = 0;
        }
        PublishState();
    });
    private void SynchronizationInvalidated(object? sender, AllegroSynchronizationReason reason) => Post(() =>
    {
        if (!ReferenceEquals(sender, _session))
        {
            return;
        }
        _overlay?.End();
        PublishState();
    });
    private void OverlayStateChanged(object? sender, InteractiveRouteInteractionState state)
    {
        if (ReferenceEquals(sender, _overlay))
        {
            RouteStateChanged?.Invoke(this, state);
        }
    }
    private async void OverlayOwnerLost(object? sender, EventArgs args)
    {
        if (ReferenceEquals(sender, _overlay))
        {
            await CancelAfterOverlayLossAsync("The owning Allegro window became unavailable.");
        }
    }
    private async void OverlayFeedbackRejected(object? sender, string message)
    {
        if (ReferenceEquals(sender, _overlay))
        {
            await CancelAfterOverlayLossAsync(message);
        }
    }
    private async Task CancelAfterOverlayLossAsync(string message)
    {
        Post(() => Faulted?.Invoke(this, message));
        if (!HasRouteInProgress)
        {
            return;
        }
        try
        {
            await CancelRouteAsync();
        }
        catch (Exception exception)
        {
            Post(() => Faulted?.Invoke(this, message + " Cancellation was not confirmed: " + exception.Message));
        }
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
        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception; // Public callers still receive the same fault.
            lock (_gate)
            {
                _operations.Remove(completed);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Task<IEngineOperationControl?>? ready;
        lock (_gate)
        {
            _routeCancelRequested = true;
            ready = _routeInProgress ? _routeReady?.Task : null;
        }
        if (ready is not null)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                var route = await ready.WaitAsync(cancellation.Token);
                if (route is not null)
                {
                    await CancelNativeRouteOnceAsync(route).WaitAsync(cancellation.Token);
                }
                // CancelAsync confirms admission of the request, not native
                // cleanup. Wait for this exact operation's terminal receipt.
                Task<InteractiveRouteResult>? routeTask;
                lock (_gate)
                {
                    routeTask = _routeTask;
                }
                if (routeTask is not null)
                {
                    await routeTask.WaitAsync(cancellation.Token);
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "Closing PD Simple did not confirm native route cancellation: {0}", exception.Message);
            }
        }
        _lifetime.Cancel();
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
            System.Diagnostics.Trace.TraceInformation("SDK operation wait ended during shutdown: {0}", exception.Message);
        }
        await ReleaseConnectionAsync();
        _lifetime.Dispose();
    }

    private async Task ReleaseConnectionAsync()
    {
        var connection = _connection;
        // Fence queued callbacks before awaiting teardown. Unsubscribing alone
        // cannot withdraw events already posted to the WPF dispatcher.
        _connection = null;
        _pcb = null;
        _engineWorkspace = null;
        if (_mainWindow is not null)
        {
            new WindowInteropHelper(_mainWindow).Owner = 0;
        }
        if (connection is not null)
        {
            UnsubscribeConnection(connection);
            await connection.DisposeAsync();
        }
    }

    private void SubscribeConnection(BoardConnection connection)
    {
        connection.Session.ContextChanged += ContextChanged;
        connection.Session.SkillExtensions.CatalogChanged += CatalogChanged;
        connection.Session.ActivationRequested += ActivationRequested;
        connection.Session.ResidentSessionEnding += ResidentSessionEnding;
        connection.Session.SynchronizationInvalidated += SynchronizationInvalidated;
        connection.Overlay.StateChanged += OverlayStateChanged;
        connection.Overlay.OwnerLost += OverlayOwnerLost;
        connection.Overlay.FeedbackRejected += OverlayFeedbackRejected;
    }

    private void UnsubscribeConnection(BoardConnection connection)
    {
        connection.Session.ContextChanged -= ContextChanged;
        connection.Session.SkillExtensions.CatalogChanged -= CatalogChanged;
        connection.Session.ActivationRequested -= ActivationRequested;
        connection.Session.ResidentSessionEnding -= ResidentSessionEnding;
        connection.Session.SynchronizationInvalidated -= SynchronizationInvalidated;
        connection.Overlay.StateChanged -= OverlayStateChanged;
        connection.Overlay.OwnerLost -= OverlayOwnerLost;
        connection.Overlay.FeedbackRejected -= OverlayFeedbackRejected;
    }

    private sealed class BoardConnection : IAsyncDisposable
    {
        private readonly AllegroDesktopAttachment? _attachment;

        internal BoardConnection(AllegroBridgeSession session, AllegroDesktopBinding desktop,
            AllegroPcbSession tools, string directory,
            AllegroDesktopAttachment? attachment = null)
        {
            Session = session;
            Desktop = desktop;
            Pcb = tools;
            Directory = directory;
            _attachment = attachment;
            Overlay = new BoardOverlayController(desktop, session);
        }

        internal AllegroBridgeSession Session { get; }
        internal AllegroDesktopBinding Desktop { get; }
        internal AllegroPcbSession Pcb { get; }
        internal BoardOverlayController Overlay { get; }
        internal string Directory { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                Overlay.Dispose();
            }
            finally
            {
                if (_attachment is not null)
                {
                    await _attachment.DisposeAsync();
                }
                else
                {
                    try
                    {
                        Desktop.Dispose();
                    }
                    finally
                    {
                        await Session.DisposeAsync();
                    }
                }
            }
        }
    }
}
