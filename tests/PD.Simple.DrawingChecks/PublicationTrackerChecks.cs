using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Wpf.Engine;
using PD.Simple.Corridor;

internal static class PublicationTrackerChecks
{
    internal static void Run()
    {
        var document = new WorkspaceDocumentIdentity("session", 1, 2, null, "design", "v1");
        EngineWpfPublicationReceipt Receipt(long sequence, EngineWpfOverlayAvailability availability) =>
            new(document, Guid.NewGuid(), 7, sequence, availability, DateTimeOffset.UtcNow);

        var tracker = new DpViaCorridorPublicationTracker();
        if (tracker.PublishedEpoch != -1 || tracker.PublishedReceipt is not null)
        {
            throw new InvalidOperationException("A fresh tracker carried a publication.");
        }
        if (!tracker.Complete(3, 3, 9, Receipt(9, EngineWpfOverlayAvailability.Visible)) ||
            tracker.PublishedEpoch != 3 ||
            tracker.PublishedReceipt is not { Sequence: 9, Revision: 7 } ||
            !tracker.IsPublishedVisibleFor(3, 7))
        {
            throw new InvalidOperationException("A matching visible receipt did not mark its epoch published.");
        }

        foreach (EngineWpfOverlayAvailability outcome in new[]
        {
            EngineWpfOverlayAvailability.Empty,
            EngineWpfOverlayAvailability.Pending,
            EngineWpfOverlayAvailability.Unavailable,
            EngineWpfOverlayAvailability.Disposed,
        })
        {
            var cold = new DpViaCorridorPublicationTracker();
            if (cold.Complete(3, 3, 9, Receipt(9, outcome)) || cold.PublishedEpoch != -1)
            {
                throw new InvalidOperationException($"A {outcome} receipt marked an epoch published.");
            }
        }

        var stale = new DpViaCorridorPublicationTracker();
        if (stale.Complete(2, 3, 9, Receipt(9, EngineWpfOverlayAvailability.Visible)) || stale.PublishedEpoch != -1)
        {
            throw new InvalidOperationException("A stale-epoch receipt marked an epoch published.");
        }
        var superseded = new DpViaCorridorPublicationTracker();
        if (superseded.Complete(3, 3, 10, Receipt(9, EngineWpfOverlayAvailability.Visible)) ||
            superseded.PublishedEpoch != -1)
        {
            throw new InvalidOperationException("A superseded-sequence receipt marked an epoch published.");
        }
        var failed = new DpViaCorridorPublicationTracker();
        if (failed.Complete(3, 3, 9, null) || failed.PublishedEpoch != -1)
        {
            throw new InvalidOperationException("A failed (null-receipt) publication marked an epoch published.");
        }
        if (tracker.IsPublishedVisibleFor(3, 8) || tracker.IsPublishedVisibleFor(4, 7))
        {
            throw new InvalidOperationException("A revision or epoch mismatch read as visibly published.");
        }

        tracker.Supersede();
        if (tracker.PublishedEpoch != -1 || tracker.PublishedReceipt is not null ||
            tracker.IsPublishedVisibleFor(3, 7))
        {
            throw new InvalidOperationException("Supersede did not retire the retained publication.");
        }
    }
}
