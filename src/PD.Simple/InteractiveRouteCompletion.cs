using CircuitHub.AllegroBridge.Engine.Live;

namespace PD.Simple;

internal sealed record InteractiveRouteCompletionResult(
    EngineOperationTerminal Terminal,
    Exception? FeedbackFailure);

/// <summary>
/// Keeps native terminal evidence independent from its optional feedback display.
/// The caller supplies Engine operations; this helper never owns or replays native
/// cancellation and never disposes the operation on the caller's behalf.
/// </summary>
internal static class InteractiveRouteCompletion
{
    internal static async Task<InteractiveRouteCompletionResult> WaitAsync(
        Func<CancellationToken, Task<EngineOperationTerminal>> waitForTerminal,
        Func<CancellationToken, Task> consumeFeedback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(waitForTerminal);
        ArgumentNullException.ThrowIfNull(consumeFeedback);
        using var feedbackLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task feedbackTask = ConsumeFeedbackAsync(consumeFeedback, feedbackLifetime.Token);

        EngineOperationTerminal terminal;
        try
        {
            terminal = await waitForTerminal(cancellationToken);
        }
        catch
        {
            // Native wait failure remains primary. A concurrent presentation
            // failure must not replace its exception or manufacture a result.
            await StopFeedbackAsync(feedbackLifetime, feedbackTask);
            throw;
        }

        Exception? feedbackFailure = await StopFeedbackAsync(feedbackLifetime, feedbackTask);
        return new InteractiveRouteCompletionResult(terminal, feedbackFailure);
    }

    private static async Task ConsumeFeedbackAsync(
        Func<CancellationToken, Task> consumeFeedback,
        CancellationToken cancellationToken)
    {
        // Also capture a synchronous callback failure as a feedback task fault;
        // it must not prevent awaiting the native operation's terminal result.
        await consumeFeedback(cancellationToken);
    }

    private static async Task<Exception?> StopFeedbackAsync(
        CancellationTokenSource lifetime,
        Task feedbackTask)
    {
        Exception? failure = null;
        try
        {
            lifetime.Cancel();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await feedbackTask;
        }
        catch (OperationCanceledException exception) when (
            lifetime.IsCancellationRequested && exception.CancellationToken == lifetime.Token)
        {
            // Stopping this reader is local cleanup, not native cancellation.
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }
        return failure;
    }
}
