using CircuitHub.AllegroBridge.Engine.Live;
using PD.PcbTools;

namespace PD.Simple;

public sealed record InteractiveRouteResult(
    string State,
    string Message,
    bool IsVerifiedSuccess);

public sealed partial class BridgeSession
{
    private EngineMutationResult? _lastEngineEdit;

    private async Task<InteractiveRouteResult> ExecuteEngineRouteAsync(decimal width)
    {
        await Task.Yield();
        bool mutationDispatchUnresolved = false;
        bool completed = false;
        IEngineOperationControl? operation = null;
        try
        {
            AllegroWorkspace workspace = await RequireEngineWorkspaceAsync(_lifetime.Token);
            EngineRoutingLayerCatalog layers = await workspace.Routing.ReadLayersAsync(_lifetime.Token);
            EngineRoutingLayer layer = EngineHorizontalFirstRoutePolicy.SelectLayer(layers);
            ThrowIfRouteCancelledBeforeEdit();

            EngineEndpointPick pick = await workspace.Picking.StartTwoPointPickAsync(_lifetime.Token);
            operation = pick;
            await AdmitEngineRouteOperationAsync(operation);
            PresentRoute(() => _overlay?.Begin(pick.Document.BoardGeneration));

            InteractiveRouteCompletionResult completion = await InteractiveRouteCompletion.WaitAsync(
                token => pick.WaitForTerminalAsync(token).AsTask(),
                token => ReadEngineFeedbackAsync(pick, token),
                _lifetime.Token);
            if (completion.FeedbackFailure is { } feedbackFailure)
            {
                ReportRoutePresentationFailure(feedbackFailure);
            }
            if (!completion.Terminal.IsComplete)
            {
                return new(completion.Terminal.State, completion.Terminal.Message, false);
            }

            EnginePickedEndpoints endpoints = await pick.WaitForResultAsync(_lifetime.Token);
            ThrowIfRouteCancelledBeforeEdit();
            EngineTracePlan plan = EngineHorizontalFirstRoutePolicy.Plan(endpoints, width, layer);
            EnginePreparedTrace prepared = await workspace.Routing.PrepareTraceAsync(
                endpoints, plan, _lifetime.Token);
            ThrowIfRouteCancelledBeforeEdit();

            await DisposeEngineRouteOperationAsync(operation);
            operation = null;
            PrepareNextEngineRouteStage();
            ThrowIfRouteCancelledBeforeEdit();

            // StartAsync retains the proposal owner's one-attempt guard. From
            // this point until terminal Engine evidence arrives, native effect
            // cannot safely be inferred from a managed exception.
            mutationDispatchUnresolved = true;
            EngineMutationOperation mutation = await prepared.StartAsync(_lifetime.Token);
            operation = mutation;
            await AdmitEngineRouteOperationAsync(operation);
            EngineMutationResult result = await mutation.WaitForResultAsync(_lifetime.Token);
            mutationDispatchUnresolved = false;
            AdmitEngineMutation(result, isUndo: false);
            completed = result.IsVerifiedSuccess && !_outcomeUncertain;
            if (completed)
            {
                PresentRoute(() => _overlay?.CompleteSuccessfully());
            }
            return new(result.State.ToString(), result.Message, result.IsVerifiedSuccess);
        }
        catch (Exception error)
        {
            if (mutationDispatchUnresolved)
            {
                _outcomeUncertain = true;
                _lastEngineEdit = null;
                _undoBinding = null;
            }
            Post(() => Faulted?.Invoke(this,
                mutationDispatchUnresolved ? FailureMessage(error) : error.Message));
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _routeReady?.TrySetResult(null);
            }
            if (!completed)
            {
                PresentRoute(() => _overlay?.End());
            }
            if (_disposed && operation is { IsTerminal: false })
            {
                try
                {
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await operation.CancelAsync(cancellation.Token);
                }
                catch (Exception cancellationError)
                {
                    System.Diagnostics.Trace.TraceWarning(
                        "Closing PD Simple could not confirm Engine native cancellation: {0}",
                        cancellationError.Message);
                }
            }
            await DisposeEngineRouteOperationAsync(operation);
            EndOperation();
        }
    }

    private async Task<InteractiveRouteResult> ExecuteEngineUndoAsync()
    {
        await Task.Yield();
        EngineMutationResult edit = _lastEngineEdit ??
            throw new InvalidOperationException("The edit-specific Engine recovery evidence is missing.");
        _lastEngineEdit = null;
        _undoBinding = null;
        bool admitted = false;
        try
        {
            // EngineMutationResult enforces the one-attempt edit-specific Undo.
            EngineMutationResult result = await edit.UndoAsync(_lifetime.Token);
            AdmitEngineMutation(result, isUndo: true);
            admitted = true;
            return new(result.State.ToString(), result.Message, result.IsVerifiedSuccess);
        }
        catch (Exception error)
        {
            if (!admitted)
            {
                _outcomeUncertain = true;
                _recoveryRequired = false;
            }
            Post(() => Faulted?.Invoke(this, FailureMessage(error)));
            throw;
        }
        finally
        {
            EndOperation();
        }
    }

    private void AdmitEngineMutation(EngineMutationResult result, bool isUndo)
    {
        ArgumentNullException.ThrowIfNull(result);
        WorkspaceDocumentIdentity current = _engineWorkspace?.Document ??
            throw new InvalidOperationException("The Engine workspace is unavailable while admitting mutation evidence.");
        bool same = result.Document == current;
        _outcomeUncertain = !same || result.State == EngineMutationState.Uncertain;

        if (isUndo)
        {
            _recoveryRequired = false;
            _lastEngineEdit = null;
            _undoBinding = null;
        }
        else
        {
            _recoveryRequired = same && result.CanUndo && !result.IsVerifiedSuccess;
            _lastEngineEdit = same && result.CanUndo ? result : null;
            _undoBinding = _lastEngineEdit is null ? null : RequireSession().Binding;
        }

        if (result.EvidenceError is { } error)
        {
            Post(() => Faulted?.Invoke(this, error));
        }
    }

    private async Task ReadEngineFeedbackAsync(
        EngineEndpointPick pick,
        CancellationToken cancellationToken)
    {
        await foreach (EngineInteractionFeedback feedback in
            pick.ReadInteractionFeedbackAsync(cancellationToken))
        {
            _overlay?.Apply(feedback);
        }
    }

    private void PrepareNextEngineRouteStage()
    {
        lock (_gate)
        {
            _route = null;
            _routeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private async Task AdmitEngineRouteOperationAsync(IEngineOperationControl operation)
    {
        bool cancel;
        lock (_gate)
        {
            _route = operation;
            _routeReady!.TrySetResult(operation);
            cancel = _routeCancelRequested || _disposed;
        }
        if (cancel)
        {
            await CancelNativeRouteOnceAsync(operation);
        }
    }

    private async Task DisposeEngineRouteOperationAsync(IEngineOperationControl? operation)
    {
        if (operation is null)
        {
            return;
        }
        try
        {
            await operation.DisposeAsync();
        }
        catch (Exception error)
        {
            ReportRoutePresentationFailure(error);
        }
    }
}
