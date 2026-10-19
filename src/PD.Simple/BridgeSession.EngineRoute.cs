using System.IO;
using PcbEngine.Advanced;
using PD.PcbTools;

namespace PD.Simple;

public sealed record InteractiveRouteResult(
    string State,
    string Message,
    bool IsVerifiedSuccess);

public sealed partial class BridgeSession
{
    private MutationResult? _lastEngineEdit;

    private async Task<InteractiveRouteResult> ExecuteEngineRouteAsync(decimal width)
    {
        await Task.Yield();
        IEngineOperationControl? operation = null;
        try
        {
            RequireReadyWorkspace();
            RoutingLayerCatalog layers =
                await Workspace.Routing.ReadLayersAsync(_lifetime.Token);
            RoutingLayer layer =
                EngineHorizontalFirstRoutePolicy.SelectLayer(layers);
            ThrowIfRouteCancelledBeforeEdit();

            EndpointPick pick =
                await Workspace.Picking.StartTwoPointPickAsync(_lifetime.Token);
            operation = pick;
            await AdmitEngineRouteOperationAsync(operation);

            InteractiveRouteCompletionResult completion =
                await InteractiveRouteCompletion.WaitAsync(
                    token => pick.WaitForTerminalAsync(token).AsTask(),
                    token => ReadEngineRouteStateAsync(pick, token),
                    _lifetime.Token);
            if (completion.FeedbackFailure is { } feedbackFailure)
            {
                ReportPresentationCandidateFailure(feedbackFailure);
            }
            if (!completion.Terminal.IsComplete)
            {
                return new(
                    completion.Terminal.State,
                    completion.Terminal.Message,
                    false);
            }

            PickedEndpoints endpoints =
                await pick.WaitForResultAsync(_lifetime.Token);
            ThrowIfRouteCancelledBeforeEdit();
            PublishRouteState(new(
                true,
                true,
                "Preparing route",
                "Both Engine endpoints were accepted; validating the trace proposal.",
                ToSummary(endpoints.First),
                ToSummary(endpoints.Second)));

            TracePlan plan =
                EngineHorizontalFirstRoutePolicy.Plan(endpoints, width, layer);
            PreparedTrace prepared =
                await Workspace.Routing.PrepareTraceAsync(
                    endpoints,
                    plan,
                    _lifetime.Token);
            ThrowIfRouteCancelledBeforeEdit();

            await DisposeEngineRouteOperationAsync(operation);
            operation = null;
            PrepareNextEngineRouteStage();
            ThrowIfRouteCancelledBeforeEdit();

            MutationOperation mutation =
                await prepared.StartAsync(_lifetime.Token);
            operation = mutation;
            await AdmitEngineRouteOperationAsync(operation);
            MutationResult result =
                await mutation.WaitForResultAsync(_lifetime.Token);
            AdmitEngineMutation(result, isUndo: false);
            return new(
                result.State.ToString(),
                result.Message,
                result.IsVerifiedSuccess);
        }
        catch (Exception exception)
        {
            Post(() => Faulted?.Invoke(this, exception.Message));
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _routeReady?.TrySetResult(null);
            }
            await DisposeEngineRouteOperationAsync(operation);
            EndOperation();
        }
    }

    private async Task<InteractiveRouteResult> ExecuteEngineUndoAsync()
    {
        await Task.Yield();
        MutationResult edit = _lastEngineEdit ??
            throw new InvalidOperationException(
                "The Engine route recovery authority is unavailable.");
        _lastEngineEdit = null;
        try
        {
            MutationResult result = await edit.UndoAsync(_lifetime.Token);
            AdmitEngineMutation(result, isUndo: true);
            return new(
                result.State.ToString(),
                result.Message,
                result.IsVerifiedSuccess);
        }
        catch (Exception exception)
        {
            Post(() => Faulted?.Invoke(this, exception.Message));
            throw;
        }
        finally
        {
            EndOperation();
        }
    }

    private void AdmitEngineMutation(MutationResult result, bool isUndo)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Document != EngineSession.State.Document)
        {
            _lastEngineEdit = null;
            throw new InvalidDataException(
                "The Engine mutation result belongs to another or historical document.");
        }

        _lastEngineEdit = !isUndo && result.CanUndo ? result : null;
        if (result.EvidenceError is { } error)
        {
            Post(() => Faulted?.Invoke(this, error));
        }
    }

    private async Task ReadEngineRouteStateAsync(
        EndpointPick pick,
        CancellationToken cancellationToken)
    {
        InteractiveRoutePickSummary? first = null;
        await foreach (InteractionFeedback feedback in
            pick.ReadInteractionFeedbackAsync(cancellationToken))
        {
            if (!feedback.IsCurrent)
            {
                continue;
            }

            switch (feedback.FeedbackKind)
            {
                case InteractionFeedbackKind.SelectionAccepted
                    when feedback.SelectionOrdinal == 1 && feedback.Point is not null:
                    first = ToSummary(feedback.ObjectKind, feedback.Point.Position);
                    PublishRouteState(new(
                        true,
                        true,
                        "Waiting for second endpoint",
                        "The first Engine endpoint is retained. Select the route end in Allegro.",
                        first));
                    break;
                case InteractionFeedbackKind.SelectionAccepted
                    when feedback.SelectionOrdinal == 2:
                    PublishRouteState(new(
                        true,
                        true,
                        "Endpoints accepted",
                        "Engine is admitting the selected endpoints.",
                        first,
                        feedback.Point is null
                            ? null
                            : ToSummary(feedback.ObjectKind, feedback.Point.Position)));
                    break;
                case InteractionFeedbackKind.SelectionCleared:
                    first = null;
                    PublishRouteState(new(
                        true,
                        false,
                        "Waiting for first endpoint",
                        "The first endpoint was cleared without dispatching an edit."));
                    break;
                case InteractionFeedbackKind.SelectionRejected:
                    PublishRouteState(new(
                        true,
                        first is not null,
                        "Selection rejected",
                        "Allegro rejected that endpoint; the rejected selection changed no geometry.",
                        first));
                    break;
            }
        }
    }

    private void ThrowIfRouteCancelledBeforeEdit()
    {
        lock (_gate)
        {
            if (_routeCancelRequested ||
                _disposeRequested ||
                _lifetime.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Point-to-point Trace was cancelled before an edit was dispatched.",
                    _lifetime.Token);
            }
        }
    }

    private void PrepareNextEngineRouteStage()
    {
        lock (_gate)
        {
            _route = null;
            _routeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _routeCancellationOperation = null;
            _routeCancellationTask = null;
        }
    }

    private async Task AdmitEngineRouteOperationAsync(
        IEngineOperationControl operation)
    {
        bool cancel;
        lock (_gate)
        {
            _route = operation;
            _routeReady!.TrySetResult(operation);
            cancel = _routeCancelRequested || _disposeRequested;
        }
        if (cancel && !_disposeRequested)
        {
            await RequestRouteCancellationOnceAsync(operation);
        }
    }

    private async Task DisposeEngineRouteOperationAsync(
        IEngineOperationControl? operation)
    {
        if (operation is null)
        {
            return;
        }
        try
        {
            await operation.DisposeAsync();
        }
        catch (Exception exception)
        {
            ReportPresentationCandidateFailure(exception);
        }
    }

    private void ReportPresentationCandidateFailure(Exception exception)
    {
        string message =
            "Route feedback cleanup failed; Engine operation tracking is unchanged: " +
            exception.Message;
        System.Diagnostics.Trace.TraceWarning(message);
        Post(() => Faulted?.Invoke(this, message));
    }

    private static InteractiveRoutePickSummary ToSummary(PickedPoint point) =>
        ToSummary(point.ObjectKind, point.Position);

    private static InteractiveRoutePickSummary ToSummary(
        PickedObjectKind objectKind,
        PcbEngine.Design.DesignPoint position) =>
        new(
            objectKind.ToString(),
            FormattableString.Invariant(
                $"({position.X:0.###}, {position.Y:0.###}) mil"));
}
