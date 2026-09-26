using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.Simple;

Console.WriteLine(
    $"PASS: {ConnectionSwitchChecks.Run()} Engine target-selection, connection-state, busy, uncertainty, recovery, and diagnostic checks.");
Console.WriteLine(
    $"PASS: {LaneAToolsChecks.Run()} Lane A navigation/forwarding/offline-geometry checks.");
Console.WriteLine(
    $"PASS: {CatalogSelectionChecks.Run()} typed catalog row/selection checks.");
Console.WriteLine(
    $"PASS: {CorridorFindingsChecks.Run()} complete corridor findings checks.");
Console.WriteLine(
    $"PASS: {RunStatusChecks.Run()} run status classification and badge-text checks.");
Console.WriteLine(
    $"PASS: {CatalogPublicationChecks.Run()} catalog publication scope, fence, and coverage checks.");
await CheckPublicEngineSessionLifetimeAsync();
await CheckRouteCompletionAsync();
Console.WriteLine(
    $"PASS: {await LargeBoardCaptureWorkflowChecks.RunAsync()} explicit large-board capture, diagnostics, fencing, cancellation, and bounded replay checks.");
Console.WriteLine(
    $"PASS: {await LargeBoardCorridorRunnerChecks.RunAsync()} scalable large-board corridor replay and identity checks.");
Console.WriteLine(
    $"PASS: {await CorridorSealedReplayChecks.RunAsync()} real sealed-store corridor and replay-plan checks.");
Console.WriteLine(
    $"PASS: {await ConstraintsDrcChecks.RunAsync()} Constraints/DRC offline gates, marker review, comparison, and export checks.");
Console.WriteLine(
    $"PASS: {EngineWorkspaceToolChecks.Run()} Engine workspace corridor-registration checks.");

static async Task CheckPublicEngineSessionLifetimeAsync()
{
    string targetDirectory = Path.Combine(
        Path.GetTempPath(),
        "pd-engine-target-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(targetDirectory);
    AllegroEngineSession? session = null;
    try
    {
        EngineTargetResolution missing =
            AllegroEngineDiscovery.ResolveLaunchTarget([]);
        Require(
            missing.Target is null &&
            missing.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "engine_launch_target_missing"),
            "A missing launch target was silently selected.");

        string[] arguments = ["--bridge-dir", targetDirectory];
        EngineTargetResolution first =
            AllegroEngineDiscovery.ResolveLaunchTarget(arguments);
        EngineTargetResolution second =
            AllegroEngineDiscovery.ResolveLaunchTarget(arguments);
        Require(
            first.Target is
            {
                Kind: EngineSessionTargetKind.LaunchContext,
                Availability: EngineSessionTargetAvailability.Available,
            } &&
            second.Target?.Id == first.Target.Id &&
            first.Diagnostics.IsEmpty,
            "Explicit launch-target resolution was not stable and diagnostic-free.");

        session = AllegroEngineSession.Create(new EngineSessionOptions
        {
            ConnectionTimeout = TimeSpan.FromMilliseconds(100),
            DisposalTimeout = TimeSpan.FromMilliseconds(250),
        });
        AllegroWorkspace workspace = session.Workspace;
        Require(
            ReferenceEquals(workspace, session.Workspace),
            "The Engine session created a second workspace owner.");

        await RequireThrowsAsync<ArgumentException>(
            () => session.AttachAsync(first.Target!).AsTask());
        Require(
            session.State.ConnectionState == EngineConnectionState.Disconnected,
            "Rejecting the wrong target kind changed Engine connection state.");

        await RequireThrowsAsync<Exception>(
            () => session.ConnectAsync(first.Target!).AsTask());
        Require(
            session.State.ConnectionState == EngineConnectionState.Faulted &&
            session.State.Diagnostics.Any(diagnostic =>
                diagnostic.Code == "engine_connection_failed"),
            "Connection failure did not retain Engine-owned diagnostics and fault state.");

        Task firstDisposal = session.DisposeAsync().AsTask();
        Task secondDisposal = session.DisposeAsync().AsTask();
        await Task.WhenAll(firstDisposal, secondDisposal);
        Require(
            session.State.ConnectionState == EngineConnectionState.Disposed &&
            ReferenceEquals(workspace, session.Workspace),
            "Concurrent disposal did not preserve the stable Engine owner.");
        _ = session.UnresolvedOperations;

        Console.WriteLine(
            "PASS: public Engine discovery is explicit, wrong-kind targets are rejected, connection failure retains diagnostics, and concurrent disposal preserves one session/workspace.");
    }
    finally
    {
        if (session is not null)
        {
            await session.DisposeAsync();
        }
        Directory.Delete(targetDirectory);
    }
}

static async Task CheckRouteCompletionAsync()
{
    var document = new WorkspaceDocumentIdentity(
        "completion-session",
        4,
        31,
        1234,
        "fixture.brd",
        "PD_V25");
    var terminal = new EngineOperationTerminal(
        "Complete",
        true,
        "ok",
        "Engine success",
        document);

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

    Task<InteractiveRouteCompletionResult> completing =
        InteractiveRouteCompletion.WaitAsync(
            token => completion.Task.WaitAsync(token),
            Consume);
    Require(
        !completing.IsCompleted,
        "A live Engine operation was prematurely considered complete.");
    completion.SetResult(terminal);
    InteractiveRouteCompletionResult result =
        await completing.WaitAsync(TimeSpan.FromSeconds(5));
    Require(
        result.Terminal == terminal &&
        result.FeedbackFailure is null &&
        feedbackStopped,
        "Engine terminal evidence was replaced or feedback did not stop independently.");

    var cleanupFailure =
        new InvalidOperationException("Feedback cleanup failed after terminal delivery.");
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
    Task<InteractiveRouteCompletionResult> cleaning =
        InteractiveRouteCompletion.WaitAsync(
            token => cleanupCompletion.Task.WaitAsync(token),
            FailOnStop);
    cleanupCompletion.SetResult(terminal);
    InteractiveRouteCompletionResult cleaned =
        await cleaning.WaitAsync(TimeSpan.FromSeconds(5));
    Require(
        cleaned.Terminal == terminal &&
        ReferenceEquals(cleaned.FeedbackFailure, cleanupFailure),
        "A presentation cleanup failure replaced Engine terminal evidence.");

    var operationFailure = new IOException("Engine terminal wait failed.");
    Exception? observed = null;
    try
    {
        await InteractiveRouteCompletion.WaitAsync(
            _ => Task.FromException<EngineOperationTerminal>(operationFailure),
            _ => Task.FromException(
                new InvalidOperationException("Independent feedback failure.")));
    }
    catch (Exception exception)
    {
        observed = exception;
    }
    Require(
        ReferenceEquals(observed, operationFailure),
        "Feedback cleanup replaced the primary Engine terminal-wait exception.");

    var synchronousFailure =
        new InvalidOperationException("Synchronous presentation failure.");
    InteractiveRouteCompletionResult synchronous =
        await InteractiveRouteCompletion.WaitAsync(
            _ => Task.FromResult(terminal),
            _ => throw synchronousFailure);
    Require(
        synchronous.Terminal == terminal &&
        ReferenceEquals(synchronous.FeedbackFailure, synchronousFailure),
        "A synchronous feedback callback prevented Engine terminal admission.");

    Console.WriteLine(
        "PASS: route completion keeps Engine terminal evidence primary and presentation cleanup independent.");
}

static async Task RequireThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
