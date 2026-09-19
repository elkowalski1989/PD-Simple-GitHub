using CircuitHub.AllegroBridge.Wpf.Engine;

namespace PD.Simple.Corridor;

/// <summary>
/// Owns the drawing-publication side of the selection lifecycle: prepared,
/// pending, and completed-with-receipt. The published epoch is set only from
/// a matching visible receipt; queued, empty, unavailable, superseded, and
/// failed outcomes never mark an epoch published. Review-image state stays
/// with the caller.
/// </summary>
internal sealed class DpViaCorridorPublicationTracker
{
    internal long PublishedEpoch { get; private set; } = -1;

    internal EngineWpfPublicationReceipt? PublishedReceipt { get; private set; }

    internal void Supersede()
    {
        PublishedEpoch = -1;
        PublishedReceipt = null;
    }

    /// <summary>
    /// Records one completed publication request. Returns true only when the
    /// receipt proves a visible publication for the still-current epoch and
    /// request sequence. A null receipt (cancelled or failed request), a
    /// non-visible outcome, a stale epoch, or a superseded sequence records
    /// nothing.
    /// </summary>
    internal bool Complete(
        long epoch,
        long currentEpoch,
        long currentSequence,
        EngineWpfPublicationReceipt? receipt)
    {
        if (receipt is not { Availability: EngineWpfOverlayAvailability.Visible } ||
            epoch != currentEpoch ||
            receipt.Sequence != currentSequence)
        {
            return false;
        }
        PublishedEpoch = epoch;
        PublishedReceipt = receipt;
        return true;
    }

    /// <summary>
    /// True when the retained receipt proves this exact drawing revision is
    /// visibly published for the epoch. Used to skip republishing an
    /// unchanged drawing when its review lands.
    /// </summary>
    internal bool IsPublishedVisibleFor(long epoch, long revision) =>
        PublishedEpoch == epoch &&
        PublishedReceipt is { Availability: EngineWpfOverlayAvailability.Visible, Revision: var published } &&
        published == revision;
}
