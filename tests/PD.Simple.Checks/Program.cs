using CircuitHub.AllegroBridge;
using System.Runtime.CompilerServices;
using PD.Simple;
using Admission = PD.Simple.InteractiveRouteRecovery.Admission;

int checks = 0;
foreach (long generation in new long[] { 27, 9183 })
{
    var binding = new AllegroSessionBinding("changed-session-" + generation, generation, 2, "snapshot", "25");
    string RouteJson(string state, bool committed = true, bool recovery = true) =>
        $$"""{"schema":"pd-workflow-control-result-v1","action":"interactive-route","boardGeneration":{{generation}},"state":"{{state}}","committed":{{committed.ToString().ToLowerInvariant()}},"recoveryAvailable":{{recovery.ToString().ToLowerInvariant()}}} """;
    string UndoJson = $$"""{"schema":"pd-workflow-control-result-v1","action":"interactive-route-undo","boardGeneration":{{generation}},"state":"succeeded"}""";
    string preflightRejectedJson = RouteJson("rejected", false, false)
        .Replace("}", ",\"routePreflightRejected\":true}");
    AllegroOperationReceipt Receipt(AllegroOperationState state, string? json) =>
        new(Guid.NewGuid(), "pd.simple.controls.interactive-route", binding, state, "native_result", "test", DateTimeOffset.UtcNow)
        {
            ResultPayloadJson = json
        };
    void Route(string name, AllegroOperationState state, string? json, Admission expected, bool current = true)
    {
        var actual = InteractiveRouteRecovery.AdmitRoute(Receipt(state, json), generation, current);
        if (actual != expected)
        {
            throw new InvalidOperationException($"{name}: expected {expected}, received {actual}");
        }
        checks++;
    }
    void Undo(string name, AllegroOperationState state, string? json, bool expected, bool current = true)
    {
        if (InteractiveRouteRecovery.AdmitUndo(Receipt(state, json), generation, current) != expected)
        {
            throw new InvalidOperationException(name);
        }
        checks++;
    }

    Route("verified commit", AllegroOperationState.Complete, RouteJson("succeeded"), Admission.Committed);
    Route("failed committed geometry permits ONLY recovery", AllegroOperationState.Failed, RouteJson("failed"), Admission.RecoveryRequired);
    Route("failed without recovery is uncertain", AllegroOperationState.Failed, RouteJson("failed", true, false), Admission.Uncertain);
    Route("unproven rollback is uncertain", AllegroOperationState.Failed, RouteJson("failed", false, false), Admission.Uncertain);
    Route("SDK Failed does not make native rejection retryable", AllegroOperationState.Failed, RouteJson("rejected", false, false), Admission.Uncertain);
    Route("proven early preflight rejection allows retry", AllegroOperationState.Failed,
        preflightRejectedJson, Admission.NoMutation);
    Route("false preflight flag does not allow retry", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("\"routePreflightRejected\":true", "\"routePreflightRejected\":false"), Admission.Uncertain);
    Route("string preflight flag does not allow retry", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("\"routePreflightRejected\":true", "\"routePreflightRejected\":\"true\""), Admission.Uncertain);
    Route("wrong preflight property does not allow retry", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("routePreflightRejected", "preflightRejected"), Admission.Uncertain);
    Route("generic failure cannot claim preflight rejection", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("\"state\":\"rejected\"", "\"state\":\"failed\""), Admission.Uncertain);
    Route("preflight rejection cannot claim committed geometry", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("\"committed\":false", "\"committed\":true"), Admission.Uncertain);
    Route("preflight rejection cannot claim recovery", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("\"recoveryAvailable\":false", "\"recoveryAvailable\":true"), Admission.Uncertain);
    Route("wrong board rejects preflight evidence", AllegroOperationState.Failed,
        preflightRejectedJson, Admission.Uncertain, false);
    Route("wrong generation rejects preflight evidence", AllegroOperationState.Failed,
        preflightRejectedJson.Replace($"\"boardGeneration\":{generation}", $"\"boardGeneration\":{generation + 1}"), Admission.Uncertain);
    Route("completed SDK result contradicts preflight rejection", AllegroOperationState.Complete,
        preflightRejectedJson, Admission.Uncertain);
    Route("duplicate preflight flag does not allow retry", AllegroOperationState.Failed,
        preflightRejectedJson.Replace("\"routePreflightRejected\":true",
            "\"routePreflightRejected\":true,\"routePreflightRejected\":false"), Admission.Uncertain);
    Route("mismatched success state cannot certify failed mutation", AllegroOperationState.Failed, RouteJson("succeeded"), Admission.Uncertain);
    Route("cancelled with nonmutation proof", AllegroOperationState.Cancelled, RouteJson("cancelled", false, false), Admission.NoMutation);
    Route("cancelled still committed is uncertain", AllegroOperationState.Cancelled, RouteJson("cancelled"), Admission.Uncertain);
    Route("missing cancelled proof", AllegroOperationState.Cancelled, null, Admission.Uncertain);
    Route("malformed completed proof", AllegroOperationState.Complete, "{", Admission.Uncertain);
    Route("wrong board denies recovery", AllegroOperationState.Failed, RouteJson("failed"), Admission.Uncertain, false);
    Route("wrong generation denies recovery", AllegroOperationState.Failed,
        RouteJson("failed").Replace($"\"boardGeneration\":{generation}", $"\"boardGeneration\":{generation + 1}"), Admission.Uncertain);
    Route("duplicate state denies recovery", AllegroOperationState.Failed,
        RouteJson("failed").Replace("\"state\":\"failed\"", "\"state\":\"failed\",\"state\":\"succeeded\""), Admission.Uncertain);
    Route("wrong action denies recovery", AllegroOperationState.Failed,
        RouteJson("failed").Replace("interactive-route", "another-command"), Admission.Uncertain);
    Undo("verified Undo releases restriction", AllegroOperationState.Complete, UndoJson, true);
    Undo("failed Undo cannot retain retry", AllegroOperationState.Failed, UndoJson, false);
    Undo("superseded Undo cannot retain retry", AllegroOperationState.Superseded, UndoJson, false);
    Undo("cancelled Undo cannot retain retry", AllegroOperationState.Cancelled, UndoJson, false);
    Undo("missing Undo proof", AllegroOperationState.Complete, null, false);
    Undo("wrong Undo board", AllegroOperationState.Complete, UndoJson, false, false);
    Undo("route proof is not Undo proof", AllegroOperationState.Complete, RouteJson("succeeded"), false);
    Undo("rejected Undo proof", AllegroOperationState.Complete, UndoJson.Replace("succeeded", "rejected"), false);
}
Console.WriteLine($"PASS: {checks} mutation-result admission checks; verified recovery-only, uncertain failures, and guarded Undo across changed session/board identities.");
Console.WriteLine($"PASS: {ConnectionSwitchChecks.Run()} connection-switch admission and packaged-source identity checks.");
await CheckRouteCompletionAsync();

static async Task CheckRouteCompletionAsync()
{
    var binding = new AllegroSessionBinding("completion-session", 31, 4, "snapshot", "25");
    string Payload(string state, long generation = 31, bool committed = true) =>
        $$"""{"schema":"pd-workflow-control-result-v1","action":"interactive-route","boardGeneration":{{generation}},"state":"{{state}}","committed":{{committed.ToString().ToLowerInvariant()}},"recoveryAvailable":{{committed.ToString().ToLowerInvariant()}}}""";
    var cases = new[]
    {
        ("committed", AllegroOperationState.Complete, Payload("succeeded"), true, Admission.Committed),
        ("recovery", AllegroOperationState.Failed, Payload("failed"), true, Admission.RecoveryRequired),
        ("cancelled without mutation", AllegroOperationState.Cancelled, Payload("cancelled", committed: false), true, Admission.NoMutation),
        ("preflight rejected", AllegroOperationState.Failed,
            Payload("rejected", committed: false).Replace("}", ",\"routePreflightRejected\":true}"), true, Admission.NoMutation),
        ("malformed", AllegroOperationState.Complete, "{", true, Admission.Uncertain),
        ("wrong generation", AllegroOperationState.Complete, Payload("succeeded", 32), true, Admission.Uncertain),
        ("stale board", AllegroOperationState.Complete, Payload("succeeded"), false, Admission.Uncertain)
    };
    foreach (var (name, state, payload, sameBoard, expectedAdmission) in cases)
    {
        var receipt = new AllegroOperationReceipt(Guid.NewGuid(), "pd.simple.controls.interactive-route",
            binding, state, "native_result", name, DateTimeOffset.UtcNow)
        {
            ResultPayloadJson = payload
        };
        var feedbackFailure = new InvalidOperationException("Feedback stream failed after terminal delivery: " + name);
        var handle = new CompletionTestHandle(receipt)
        {
            FeedbackFailureOnStop = feedbackFailure
        };
        Task<InteractiveRouteCompletionResult> completingCase = InteractiveRouteCompletion.WaitAsync(
            handle, token => ConsumeFeedbackAsync(handle, token));
        Require(!completingCase.IsCompleted && !handle.TerminalDelivered && !handle.FeedbackStopped,
            name + ": the scheduled operation or feedback reader completed before native terminal delivery.");
        handle.TerminalCompletion.SetResult(receipt);
        InteractiveRouteCompletionResult completion = await completingCase.WaitAsync(TimeSpan.FromSeconds(5));

        Require(ReferenceEquals(completion.Terminal, receipt), name + ": native receipt was replaced.");
        Require(ReferenceEquals(completion.FeedbackFailure, feedbackFailure), name + ": feedback failure was not retained separately.");
        Require(handle.TerminalDelivered && handle.FeedbackFailedAfterTerminal && handle.ReaderCancelled,
            name + ": the reader did not fail during cleanup after the native terminal was delivered.");
        Require(InteractiveRouteRecovery.AdmitRoute(completion.Terminal, 31, sameBoard) == expectedAdmission,
            name + ": feedback failure changed domain admission.");
        Require(handle.TerminalWaitCount == 1 && handle.FeedbackReadCount == 1 && handle.FeedbackStopped,
            name + ": native wait or feedback reader was replayed or not drained.");
        Require(handle.NativeCancelCount == 0 && handle.DisposeCount == 0,
            name + ": completion helper unexpectedly cancelled native work or disposed its caller's handle.");
    }

    var success = new AllegroOperationReceipt(Guid.NewGuid(), "pd.simple.controls.interactive-route",
        binding, AllegroOperationState.Complete, "", "Native success", DateTimeOffset.UtcNow)
    {
        ResultPayloadJson = Payload("succeeded")
    };
    var failedWait = new CompletionTestHandle(success);
    var nativeFailure = new IOException("Native terminal wait failed.");
    failedWait.TerminalCompletion.SetException(nativeFailure);
    failedWait.FeedbackCompletion.SetException(new InvalidOperationException("Independent feedback failure."));
    Exception? observed = null;
    try
    {
        await InteractiveRouteCompletion.WaitAsync(failedWait, token => ConsumeFeedbackAsync(failedWait, token));
    }
    catch (Exception exception)
    {
        observed = exception;
    }
    Require(ReferenceEquals(observed, nativeFailure), "Feedback cleanup replaced the primary native wait exception.");
    Require(failedWait.FeedbackStopped && failedWait.TerminalWaitCount == 1,
        "Native wait failure did not drain its reader exactly once.");

    var cooperative = new CompletionTestHandle(success);
    Task<InteractiveRouteCompletionResult> completing = InteractiveRouteCompletion.WaitAsync(
        cooperative, token => ConsumeFeedbackAsync(cooperative, token));
    Require(!completing.IsCompleted, "A live native operation was prematurely considered complete.");
    cooperative.TerminalCompletion.SetResult(success);
    var stopped = await completing.WaitAsync(TimeSpan.FromSeconds(5));
    Require(ReferenceEquals(stopped.Terminal, success) && stopped.FeedbackFailure is null,
        "Cooperative reader cancellation changed the native result.");
    Require(cooperative.FeedbackStopped && cooperative.ReaderCancelled && cooperative.NativeCancelCount == 0,
        "Stopping local feedback was not distinct from cancelling the native command.");

    var synchronous = new CompletionTestHandle(success);
    synchronous.TerminalCompletion.SetResult(success);
    var synchronousFailure = new InvalidOperationException("Synchronous presentation failure.");
    var synchronousResult = await InteractiveRouteCompletion.WaitAsync(synchronous, _ => throw synchronousFailure);
    Require(ReferenceEquals(synchronousResult.Terminal, success) &&
        ReferenceEquals(synchronousResult.FeedbackFailure, synchronousFailure),
        "A synchronous feedback callback prevented native-result admission.");

    Console.WriteLine("PASS: 10 production completion-helper cases; seven deliver native terminal before faulting feedback cleanup, preserving committed/recovery/nonmutation admission and malformed/stale rejection. Native wait failure stays primary; reader cancellation does not cancel native work. BridgeSession presentation/disposal integration is outside this check.");
}

static async Task ConsumeFeedbackAsync(IAllegroOperationHandle handle, CancellationToken cancellationToken)
{
    await foreach (var feedback in handle.ReadInteractionFeedbackAsync(cancellationToken))
    {
        _ = feedback;
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class CompletionTestHandle : IAllegroOperationHandle
{
    internal CompletionTestHandle(AllegroOperationReceipt receipt)
    {
        Current = receipt;
    }

    internal TaskCompletionSource<AllegroOperationReceipt> TerminalCompletion
    {
        get;
    } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource FeedbackCompletion
    {
        get;
    } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int TerminalWaitCount
    {
        get; private set;
    }
    internal int FeedbackReadCount
    {
        get; private set;
    }
    internal int NativeCancelCount
    {
        get; private set;
    }
    internal int DisposeCount
    {
        get; private set;
    }
    internal bool FeedbackStopped
    {
        get; private set;
    }
    internal bool ReaderCancelled
    {
        get; private set;
    }
    internal bool TerminalDelivered
    {
        get; private set;
    }
    internal bool FeedbackFailedAfterTerminal
    {
        get; private set;
    }
    internal Exception? FeedbackFailureOnStop
    {
        get; init;
    }
    public Guid OperationId => Current.OperationId;
    public AllegroOperationReceipt Current
    {
        get;
    }
    public bool IsTerminal => TerminalCompletion.Task.IsCompletedSuccessfully;
    public event EventHandler<AllegroOperationReceipt>? Changed
    {
        add
        {
        }
        remove
        {
        }
    }

    public async ValueTask<AllegroOperationReceipt> WaitForTerminalAsync(CancellationToken cancellationToken = default)
    {
        TerminalWaitCount++;
        AllegroOperationReceipt receipt = await TerminalCompletion.Task.WaitAsync(cancellationToken);
        TerminalDelivered = true;
        return receipt;
    }

    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
    {
        NativeCancelCount++;
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<AllegroInteractionFeedback> ReadInteractionFeedbackAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        FeedbackReadCount++;
        try
        {
            await FeedbackCompletion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReaderCancelled = true;
            if (FeedbackFailureOnStop is { } failure)
            {
                if (!TerminalDelivered)
                {
                    throw new InvalidOperationException("The fake feedback reader was stopped before terminal delivery.");
                }
                FeedbackFailedAfterTerminal = true;
                throw failure;
            }
            throw;
        }
        finally
        {
            FeedbackStopped = true;
        }
        yield break;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
