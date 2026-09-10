using CircuitHub.AllegroBridge;

namespace PD.Simple;

internal sealed record InteractiveRouteCompletionResult(
    AllegroOperationReceipt Terminal,
    Exception? FeedbackFailure);

/// <summary>Keeps native completion evidence independent from its optional feedback display.</summary>
internal static class InteractiveRouteCompletion
{
    internal static async Task<InteractiveRouteCompletionResult> WaitAsync(
        IAllegroOperationHandle handle,
        Func<CancellationToken, Task> consumeFeedback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(consumeFeedback);
        using var feedbackLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task feedbackTask = ConsumeFeedbackAsync(consumeFeedback, feedbackLifetime.Token);

        AllegroOperationReceipt terminal;
        try
        {
            terminal = await handle.WaitForTerminalAsync(cancellationToken);
        }
        catch
        {
            // Native wait failure remains primary. A concurrent presentation
            // failure must not replace its exception or manufacture a receipt.
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
