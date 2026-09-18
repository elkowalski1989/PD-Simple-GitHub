using System.Diagnostics;
using CircuitHub.AllegroBridge.Wpf;

namespace PD.Simple.Corridor;

/// <summary>
/// One selection entering the navigation pipeline. The epoch is assigned by
/// <see cref="DpViaCorridorSelectionPipeline.BeginSelection"/> and is the only
/// latest-wins identity; findings carry no authority of their own.
/// </summary>
internal sealed record DpViaCorridorSelectionContext(
    long Epoch,
    DpViaCorridorAnalysis Analysis,
    DpViaCorridorFinding Finding);

/// <summary>
/// The selection boundaries: native navigation (region read + zoom), an
/// optional immediate drawing publication once navigation is verified,
/// review capture, and overlay publication, plus the currency predicate
/// that decides whether a finished stage may advance. Production wires
/// Engine delegates; tests wire gated fakes through the same path.
/// A null drawing hook preserves the legacy navigate-capture-publish order.
/// </summary>
internal sealed record DpViaCorridorSelectionOperations(
    Func<DpViaCorridorSelectionContext, CancellationToken, Task<DpViaCorridorZoomResult>> NavigateAsync,
    Func<DpViaCorridorSelectionContext, CancellationToken, Task<AllegroReviewFrame>> CaptureAsync,
    Func<DpViaCorridorSelectionContext, DpViaCorridorZoomResult, AllegroReviewFrame, long, long, CancellationToken, Task> PublishAsync,
    Func<DpViaCorridorSelectionContext, bool> IsCurrent,
    Func<DpViaCorridorSelectionContext, DpViaCorridorZoomResult, long, CancellationToken, Task>? PublishDrawingAsync = null);

/// <summary>
/// A selection that survived every boundary. Superseded selections complete
/// as null instead of throwing.
/// </summary>
internal sealed record DpViaCorridorSelectionOutcome(
    long Epoch,
    DpViaCorridorZoomResult Zoom,
    AllegroReviewFrame Review,
    long NavigateMilliseconds,
    long CaptureMilliseconds);

/// <summary>
/// Owns selection epochs and their cancellation. Each selection runs
/// navigate, an optional immediate drawing publication, capture, then
/// publish; a newer selection cancels the in-flight one at whatever
/// boundary holds it, and only a selection that stays current through
/// publish completes with an outcome. This class owns no dispatcher,
/// Engine, or WPF state; the caller supplies all stage delegates.
/// </summary>
internal sealed class DpViaCorridorSelectionPipeline
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private long _epoch;

    internal long CurrentEpoch
    {
        get
        {
            lock (_gate)
            {
                return _epoch;
            }
        }
    }

    /// <summary>
    /// Supersedes any in-flight selection and assigns the next epoch. The
    /// superseded run owns disposal of its own cancellation source.
    /// </summary>
    internal long BeginSelection()
    {
        lock (_gate)
        {
            _current?.Cancel();
            _current = null;
            return checked(++_epoch);
        }
    }

    internal void CancelCurrent()
    {
        lock (_gate)
        {
            _current?.Cancel();
            _current = null;
        }
    }

    internal async Task<DpViaCorridorSelectionOutcome?> RunSelectionAsync(
        long epoch,
        DpViaCorridorAnalysis analysis,
        DpViaCorridorFinding finding,
        DpViaCorridorSelectionOperations operations,
        TimeSpan quietPeriod,
        CancellationToken lifetimeToken)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(operations);
        var navigation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        lock (_gate)
        {
            if (epoch != _epoch)
            {
                navigation.Dispose();
                return null;
            }
            _current?.Cancel();
            _current = navigation;
        }

        var context = new DpViaCorridorSelectionContext(epoch, analysis, finding);
        var navigateTimer = Stopwatch.StartNew();
        try
        {
            if (quietPeriod != TimeSpan.Zero)
            {
                await Task.Delay(quietPeriod, navigation.Token);
            }
            DpViaCorridorZoomResult zoom =
                await operations.NavigateAsync(context, navigation.Token);
            navigateTimer.Stop();
            if (!operations.IsCurrent(context))
            {
                return null;
            }
            if (operations.PublishDrawingAsync is not null)
            {
                await operations.PublishDrawingAsync(
                    context,
                    zoom,
                    navigateTimer.ElapsedMilliseconds,
                    navigation.Token);
                if (!operations.IsCurrent(context))
                {
                    return null;
                }
            }

            var captureTimer = Stopwatch.StartNew();
            AllegroReviewFrame review =
                await operations.CaptureAsync(context, navigation.Token);
            captureTimer.Stop();
            if (!operations.IsCurrent(context))
            {
                return null;
            }

            await operations.PublishAsync(
                context,
                zoom,
                review,
                navigateTimer.ElapsedMilliseconds,
                captureTimer.ElapsedMilliseconds,
                navigation.Token);
            if (!operations.IsCurrent(context))
            {
                return null;
            }
            return new(
                context.Epoch,
                zoom,
                review,
                navigateTimer.ElapsedMilliseconds,
                captureTimer.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (navigation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            navigateTimer.Stop();
            lock (_gate)
            {
                if (ReferenceEquals(_current, navigation))
                {
                    _current = null;
                }
            }
            navigation.Dispose();
        }
    }
}
