using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CircuitHub.AllegroBridge;
using CircuitHub.AllegroBridge.Windows;
using PD.Bridge;
using PD.Simple.Corridor;

namespace PD.Simple;

public sealed record SimpleSessionState(bool IsReady, string Design, long BoardGeneration,
    string SessionId, bool CanRunCorridor, string? UnavailableDetail);

// The application owns tool policy and presentation. The packaged SDK owns the
// connection, entitlement, native dispatch and session/catalog freshness checks.
public sealed class BridgeSession : IAsyncDisposable
{
    private const string ExtensionId = "pd.simple.controls";
    private const string CorridorCommand = ExtensionId + ".dp-via-corridor";
    private const string ZoomCommand = ExtensionId + ".dp-via-corridor-zoom";
    private const string RouteCommand = ExtensionId + ".interactive-route";
    private const string UndoCommand = ExtensionId + ".interactive-route-undo";
    private static readonly IReadOnlyDictionary<string, AllegroArgumentValue> NoArguments =
        new Dictionary<string, AllegroArgumentValue>();
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _operations = [];
    private AllegroBridgeSession? _session;
    private IAllegroSkillExtensionBinding? _extension;
    private AllegroDesktopBinding? _desktop;
    private BoardOverlayController? _overlay;
    private IAllegroOperationHandle? _route;
    private TaskCompletionSource<IAllegroOperationHandle?>? _routeReady;
    private Task<AllegroOperationReceipt>? _routeTask;
    private Task? _routeCancellationTask;
    private bool _routeCancelRequested;
    private long _requestId;
    private bool _busy;
    private bool _routeInProgress;
    private bool _connecting;
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
        "Open PD Simple from Allegro with a board open.");
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
    public bool CanConnect => !_disposed && !_connecting && _session is null;
    public bool CanRoute => HasLiveNativeSession && _desktop is { IsValid: true } &&
        _extension is { IsCurrent: true } && !_outcomeUncertain && !_recoveryRequired;
    public bool CanUndoRoute => !IsBusy && !_outcomeUncertain && _undoBinding is { } binding &&
        SameBoard(binding) && _extension is { IsCurrent: true };
    public InteractiveRouteInteractionState RouteState =>
        _overlay?.State ?? InteractiveRouteInteractionState.Inactive;
    internal BoardOverlayController? DpViaCorridorOverlayOwner => _overlay;

    public event EventHandler<SimpleSessionState>? StateChanged;
    public event EventHandler<BridgeOperationResult>? OperationChanged;
    public event EventHandler<InteractiveRouteInteractionState>? RouteStateChanged;
    public event EventHandler<string>? Faulted;
    public event EventHandler? FocusRequested;
    public event EventHandler? ShutdownRequested;

    public async Task ConnectAsync(string bridgeDirectory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session is not null || _connecting)
        {
            throw new InvalidOperationException("This connection has already been started. Reopen PD Simple to reconnect.");
        }
        if (string.IsNullOrWhiteSpace(bridgeDirectory) || !Directory.Exists(bridgeDirectory))
        {
            throw new ArgumentException("Launch PD Simple from Allegro; an explicit live bridge directory is required.");
        }
        _connecting = true;
        AllegroBridgeSession? connected = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            connected = await AllegroBridgeSession.ConnectAndWaitUntilReadyAsync(
                new AllegroBridgeConnectOptions(bridgeDirectory),
                new AllegroBridgeReadinessRequirements { MinimumCustomSkillContractVersion = 3 },
                timeout.Token);
            string[] files = ["pd_simple_controls.il", "pd_simple_route_adapter.il", "49_route_interactive.il",
                "pd_dp_via_corridor.il", "dp_via_corridor_check.il", "categories.il"];
            var extension = await connected.SkillExtensions.LoadAndBindAsync(
                new AllegroSkillExtensionSource(ExtensionId, new Version(1, 0, 0), "pd_simple_controls.il",
                    files.Select(file => Path.Combine(AppContext.BaseDirectory, "Skill", file)).ToArray())
                {
                    DisplayName = "PD Simple tools"
                },
                [CorridorCommand, ZoomCommand, RouteCommand, UndoCommand,
                    ExtensionId + ".interactive-route-self-test"], timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            _session = connected;
            _extension = extension;
            connected.ContextChanged += ContextChanged;
            connected.SkillExtensions.CatalogChanged += CatalogChanged;
            connected.ActivationRequested += ActivationRequested;
            connected.ResidentSessionEnding += ResidentSessionEnding;
            connected.SynchronizationInvalidated += SynchronizationInvalidated;
            _failure = null;
            PublishState();
        }
        catch (Exception exception)
        {
            if (connected is not null)
            {
                await connected.DisposeAsync();
            }
            _session = null;
            _extension = null;
            _failure = exception is OperationCanceledException && !_disposed
                ? "Connection or extension loading timed out. No command was replayed; reopen PD Simple to reconnect."
                : exception.Message;
            PublishState();
            throw;
        }
        finally
        {
            _connecting = false;
        }
    }

    public async Task BindMainWindowAsync(Window window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_desktop is not null)
        {
            throw new InvalidOperationException("The Allegro window is already bound.");
        }
        var session = RequireSession();
        var desktop = await session.BindLaunchingDesktopAsync(_lifetime.Token);
        try
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!desktop.IsValid)
            {
                throw new InvalidOperationException("The launching Allegro window is unavailable.");
            }
            new WindowInteropHelper(window).Owner = desktop.TopLevelWindowHandle;
            var overlay = new BoardOverlayController(desktop, session);
            overlay.StateChanged += OverlayStateChanged;
            overlay.OwnerLost += OverlayOwnerLost;
            overlay.FeedbackRejected += OverlayFeedbackRejected;
            _desktop = desktop;
            _overlay = overlay;
        }
        catch
        {
            desktop.Dispose();
            throw;
        }
        PublishState();
    }

    public long RunDpViaCorridor(decimal marginMils, string moduleFilter, bool includeUnused, string reportPath)
    {
        if (marginMils is < 0 or > 50 || moduleFilter.Length > 64 || moduleFilter.Any(char.IsControl))
        {
            throw new ArgumentException("Invalid corridor margin or component filter.");
        }
        ValidateReportPath(reportPath);
        var arguments = new Dictionary<string, AllegroArgumentValue>
        {
            ["margin_mils"] = AllegroArgumentValue.FromDecimal(marginMils),
            ["include_unused"] = AllegroArgumentValue.FromBoolean(includeUnused),
            ["report_path"] = AllegroArgumentValue.FromText(reportPath)
        };
        if (moduleFilter.Length != 0)
        {
            arguments["module_filter"] = AllegroArgumentValue.FromText(moduleFilter);
        }
        return QueueCorridor(CorridorCommand, arguments);
    }

    public long ZoomDpViaCorridor(string reportPath, string findingId)
    {
        ValidateReportPath(reportPath);
        if (string.IsNullOrWhiteSpace(findingId) || findingId.Length > 128 || findingId.Any(char.IsControl))
        {
            throw new ArgumentException("A captured finding identity is required.");
        }
        if (_desktop is not { IsValid: true })
        {
            throw new InvalidOperationException("The Allegro window must be bound before navigation.");
        }
        return QueueCorridor(ZoomCommand, new Dictionary<string, AllegroArgumentValue>
        {
            ["report_path"] = AllegroArgumentValue.FromText(reportPath),
            ["finding_id"] = AllegroArgumentValue.FromText(findingId)
        });
    }

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

    public Task<AllegroOperationReceipt> RouteAsync(decimal width, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width is < 0.1m or > 10_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }
        if (_overlay is null)
        {
            throw new InvalidOperationException("The launching Allegro window must be bound before routing.");
        }
        var binding = BeginOperation(RouteCommand, route: true);
        // This new attempt cannot inherit recovery evidence from a prior route.
        _undoBinding = null;
        var task = ExecuteRouteAsync(binding, width);
        lock (_gate)
        {
            _routeTask = task;
        }
        Track(task);
        // Cancelling a caller's wait does not claim native cancellation or
        // release admission. The retained task still tracks the native result.
        return task.WaitAsync(cancellationToken);
    }

    public async Task CancelRouteAsync()
    {
        Task<IAllegroOperationHandle?> ready;
        lock (_gate)
        {
            if (!_routeInProgress || _routeReady is null)
            {
                throw new InvalidOperationException("No route is active.");
            }
            _routeCancelRequested = true;
            ready = _routeReady.Task;
        }
        var handle = await ready.WaitAsync(_lifetime.Token);
        if (handle is not null)
        {
            await CancelNativeRouteOnceAsync(handle);
        }
    }

    public async Task ClearFirstPickAsync()
    {
        IAllegroOperationHandle handle;
        lock (_gate)
        {
            handle = _route ?? throw new InvalidOperationException("No active route is ready to clear its first pick.");
        }
        await handle.SendInteractionActionAsync("clear_first_pick", _lifetime.Token);
    }

    public Task<AllegroOperationReceipt> UndoRouteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanUndoRoute)
        {
            throw new InvalidOperationException("No verified recoverable route belongs to this board session.");
        }
        var binding = BeginOperation(UndoCommand);
        var task = ExecuteUndoAsync(binding);
        Track(task);
        return task.WaitAsync(cancellationToken);
    }

    private IAllegroSkillExtensionBinding BeginOperation(string command, bool route = false)
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
        var binding = _extension;
        if (binding is not { IsCurrent: true } || !binding.Commands.Any(item => item.Id == command && item.IsAvailable))
        {
            throw new InvalidOperationException("This tool is unavailable or its SDK binding is stale. Reopen PD Simple; no operation was replayed.");
        }
        lock (_gate)
        {
            if (_busy)
            {
                throw new InvalidOperationException("Finish the active operation before starting another tool.");
            }
            _busy = true;
            _routeInProgress = route;
            if (route)
            {
                _routeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _routeCancelRequested = false;
                _routeCancellationTask = null;
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
    }

    private long QueueCorridor(string command, IReadOnlyDictionary<string, AllegroArgumentValue> arguments)
    {
        var binding = BeginOperation(command);
        var id = Interlocked.Increment(ref _requestId);
        var generation = binding.SessionBinding.BoardGeneration;
        Track(DispatchCorridorAsync(binding, command, arguments, id, generation));
        return id;
    }

    private async Task DispatchCorridorAsync(IAllegroSkillExtensionBinding binding, string command,
        IReadOnlyDictionary<string, AllegroArgumentValue> arguments, long id, long generation)
    {
        // The caller records this local correlation ID before any result event.
        await Task.Yield();
        try
        {
            var terminal = await binding.ExecuteToTerminalAsync(command, arguments, _lifetime.Token);
            Post(() => OperationChanged?.Invoke(this, new BridgeOperationResult(id,
                terminal.Binding.BoardGeneration, Outcome(terminal.State), terminal.Message, true)
            {
                Code = terminal.Code,
                ResultPayloadJson = terminal.ResultPayloadJson
            }));
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException or TimeoutException)
            {
                _outcomeUncertain = true;
            }
            string message = FailureMessage(exception);
            Post(() => OperationChanged?.Invoke(this, new BridgeOperationResult(id, generation,
                BridgeOutcome.Failed, message, true)
            {
                Code = exception is AllegroSkillExtensionException skill ? skill.Code : "sdk_wait_failed"
            }));
            Post(() => Faulted?.Invoke(this, message));
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task<AllegroOperationReceipt> ExecuteRouteAsync(IAllegroSkillExtensionBinding binding, decimal width)
    {
        await Task.Yield();
        var generation = binding.SessionBinding.BoardGeneration;
        bool complete = false;
        try
        {
            await using var handle = await binding.ExecuteWithHandleAsync(RouteCommand,
                new Dictionary<string, AllegroArgumentValue> { ["width_mils"] = AllegroArgumentValue.FromDecimal(width) },
                _lifetime.Token);
            lock (_gate)
            {
                _route = handle;
            }
            bool cancelRequested;
            lock (_gate)
            {
                cancelRequested = _routeCancelRequested || _disposed;
                _routeReady!.TrySetResult(handle);
            }
            // A closing window or Cancel click can precede the native launch
            // acknowledgement. The late handle must honor that intent before
            // desktop freshness checks or local cancellation dispose it.
            if (cancelRequested)
            {
                await CancelNativeRouteOnceAsync(handle);
            }
            if (!SameBoard(binding.SessionBinding))
            {
                if (!_disposed)
                {
                    throw new InvalidOperationException("The Allegro board changed while starting the route. Inspect the native outcome.");
                }
            }
            else
            {
                _overlay?.Begin(generation);
            }
            using var feedbackLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var feedbackTask = ReadFeedbackAsync(handle, feedbackLifetime.Token);
            AllegroOperationReceipt terminal;
            try
            {
                terminal = await handle.WaitForTerminalAsync(_lifetime.Token);
                await feedbackTask;
            }
            finally
            {
                feedbackLifetime.Cancel();
                try
                {
                    await feedbackTask;
                }
                catch (OperationCanceledException) when (feedbackLifetime.IsCancellationRequested)
                {
                    // The matching feedback stream is no longer displayed.
                }
            }
            var admission = InteractiveRouteRecovery.AdmitRoute(terminal, generation, SameBoard(terminal.Binding));
            complete = admission == InteractiveRouteRecovery.Admission.Committed;
            _recoveryRequired = admission == InteractiveRouteRecovery.Admission.RecoveryRequired;
            _outcomeUncertain = admission == InteractiveRouteRecovery.Admission.Uncertain;
            if (complete || _recoveryRequired)
            {
                _undoBinding = terminal.Binding;
            }
            else
            {
                _undoBinding = null;
            }
            if (complete)
            {
                _overlay?.CompleteSuccessfully();
            }
            return terminal;
        }
        catch (Exception exception)
        {
            _outcomeUncertain = true;
            _recoveryRequired = false;
            _undoBinding = null;
            Post(() => Faulted?.Invoke(this, FailureMessage(exception)));
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _routeReady?.TrySetResult(null);
            }
            if (!complete)
            {
                _overlay?.End();
            }
            EndOperation();
        }
    }

    private async Task ReadFeedbackAsync(IAllegroOperationHandle handle, CancellationToken cancellationToken)
    {
        await foreach (var feedback in handle.ReadInteractionFeedbackAsync(cancellationToken))
        {
            _overlay?.Apply(feedback);
        }
    }

    private Task CancelNativeRouteOnceAsync(IAllegroOperationHandle handle)
    {
        lock (_gate)
        {
            return _routeCancellationTask ??= CancelNativeAsync();
        }
        async Task CancelNativeAsync()
        {
            if (handle.Current.IsTerminal)
            {
                return;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // Independent from the local wait lifetime: closing must still be
            // able to cancel a native launch whose handle arrived late.
            await handle.CancelAsync(timeout.Token);
        }
    }

    private async Task<AllegroOperationReceipt> ExecuteUndoAsync(IAllegroSkillExtensionBinding binding)
    {
        await Task.Yield();
        _undoBinding = null; // Never retry an expired/rejected/uncertain Undo.
        try
        {
            var terminal = await binding.ExecuteToTerminalAsync(UndoCommand, NoArguments, _lifetime.Token);
            if (InteractiveRouteRecovery.AdmitUndo(terminal, binding.SessionBinding.BoardGeneration, SameBoard(terminal.Binding)))
            {
                _recoveryRequired = false;
                _overlay?.End();
            }
            else
            {
                _recoveryRequired = false;
                _outcomeUncertain = true;
            }
            return terminal;
        }
        catch (Exception exception)
        {
            _outcomeUncertain = true;
            _recoveryRequired = false;
            Post(() => Faulted?.Invoke(this, FailureMessage(exception)));
            throw;
        }
        finally
        {
            EndOperation();
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

    private static BridgeOutcome Outcome(AllegroOperationState state) => state switch
    {
        AllegroOperationState.Complete => BridgeOutcome.Succeeded,
        AllegroOperationState.Superseded => BridgeOutcome.Rejected,
        AllegroOperationState.Cancelled => BridgeOutcome.Cancelled,
        _ => BridgeOutcome.Failed
    };

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
        bool bindingCurrent = _extension is { IsCurrent: true };
        bool canRunCorridor = boardReady && bindingCurrent && !_outcomeUncertain && !_recoveryRequired &&
            _extension!.Commands.Any(command => command.Id == CorridorCommand && command.IsAvailable);

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
                unavailableDetail = "The tool binding is stale. Reopen PD Simple to reconnect; no operation was replayed.";
            }
        }

        State = new SimpleSessionState(
            boardReady,
            context?.Design ?? "",
            context?.Binding.BoardGeneration ?? 0,
            context?.Binding.SessionId ?? "",
            canRunCorridor,
            unavailableDetail);
        StateChanged?.Invoke(this, State);
    }

    private void ContextChanged(object? sender, AllegroBoardContext context) => Post(() =>
    {
        if (!context.IsConnected || context.Binding.BoardGeneration != State.BoardGeneration ||
            context.Binding.SessionId != State.SessionId)
        {
            _overlay?.End();
        }
        PublishState();
    });
    private void CatalogChanged(object? sender, AllegroSkillCatalog catalog) => PublishState();
    private void ActivationRequested(object? sender, AllegroSessionActivationRequest request) =>
        Post(() => FocusRequested?.Invoke(this, EventArgs.Empty));
    private void ResidentSessionEnding(object? sender, AllegroResidentSessionEnding ending) => Post(() =>
    {
        _overlay?.End();
        _sessionEnded = true;
        _failure = "The native Allegro session ended. Reopen PD Simple from the current board.";
        PublishState();
        ShutdownRequested?.Invoke(this, EventArgs.Empty);
    });
    private void SynchronizationInvalidated(object? sender, AllegroSynchronizationReason reason) => Post(() =>
    {
        _overlay?.End();
        PublishState();
    });
    private void OverlayStateChanged(object? sender, InteractiveRouteInteractionState state) =>
        RouteStateChanged?.Invoke(this, state);
    private async void OverlayOwnerLost(object? sender, EventArgs args) => await CancelAfterOverlayLossAsync(
        "The owning Allegro window became unavailable.");
    private async void OverlayFeedbackRejected(object? sender, string message) => await CancelAfterOverlayLossAsync(message);
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
        Task<IAllegroOperationHandle?>? ready;
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
                Task<AllegroOperationReceipt>? routeTask;
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
        if (_overlay is { } overlay)
        {
            overlay.StateChanged -= OverlayStateChanged;
            overlay.OwnerLost -= OverlayOwnerLost;
            overlay.FeedbackRejected -= OverlayFeedbackRejected;
            overlay.Dispose();
        }
        _overlay = null;
        _desktop?.Dispose();
        _desktop = null;
        if (_session is { } session)
        {
            session.ContextChanged -= ContextChanged;
            session.SkillExtensions.CatalogChanged -= CatalogChanged;
            session.ActivationRequested -= ActivationRequested;
            session.ResidentSessionEnding -= ResidentSessionEnding;
            session.SynchronizationInvalidated -= SynchronizationInvalidated;
            await session.DisposeAsync();
        }
        _session = null;
        _extension = null;
        _lifetime.Dispose();
    }
}
