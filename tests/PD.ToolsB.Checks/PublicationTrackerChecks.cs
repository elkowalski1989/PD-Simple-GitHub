using PD.PcbTools.OverlayTools;

internal static class PublicationTrackerChecks
{
    private static ToolPublicationReceipt Receipt(
        long sequence, long revision, ToolPublicationAvailability availability) =>
        new("session/1/design", Guid.NewGuid(), revision, sequence, availability, DateTimeOffset.UtcNow);

    internal static void Run()
    {
        CheckVisibleMarksPublished();
        CheckNonVisibleOutcomes();
        CheckStaleAndSuperseded();
        CheckUnknownAndRetire();
    }

    private static void CheckVisibleMarksPublished()
    {
        var tracker = new ToolPublicationTracker();
        Guid operation = tracker.StartOperation(epoch: 3, revision: 7);
        ToolPublicationOutcome outcome = tracker.Complete(
            operation, 3, 9, Receipt(9, 7, ToolPublicationAvailability.Visible));
        if (outcome != ToolPublicationOutcome.Visible || !tracker.IsPublishedVisibleFor(operation, 3, 7))
        {
            throw new InvalidOperationException("A matching visible receipt did not mark its operation published.");
        }

        if (tracker.IsPublishedVisibleFor(operation, 3, 8) || tracker.IsPublishedVisibleFor(operation, 4, 7))
        {
            throw new InvalidOperationException("A revision or epoch mismatch read as visibly published.");
        }
    }

    private static void CheckNonVisibleOutcomes()
    {
        foreach ((ToolPublicationAvailability availability, ToolPublicationOutcome expected) in new[]
        {
            (ToolPublicationAvailability.Pending, ToolPublicationOutcome.Pending),
            (ToolPublicationAvailability.Empty, ToolPublicationOutcome.Empty),
            (ToolPublicationAvailability.Unavailable, ToolPublicationOutcome.Unavailable),
            (ToolPublicationAvailability.Disposed, ToolPublicationOutcome.Unavailable),
        })
        {
            var tracker = new ToolPublicationTracker();
            Guid operation = tracker.StartOperation(1, 1);
            ToolPublicationOutcome outcome = tracker.Complete(operation, 1, 5, Receipt(5, 1, availability));
            if (outcome != expected)
            {
                throw new InvalidOperationException($"Availability {availability} reported {outcome}.");
            }

            if (availability != ToolPublicationAvailability.Pending && tracker.IsPublishedVisibleFor(operation, 1, 1))
            {
                throw new InvalidOperationException($"Availability {availability} marked an operation published.");
            }
        }

        // Cancelled/failed request: null receipt records nothing as published.
        var cancelled = new ToolPublicationTracker();
        Guid cancelledOp = cancelled.StartOperation(1, 1);
        if (cancelled.Complete(cancelledOp, 1, 5, null) != ToolPublicationOutcome.Cancelled ||
            cancelled.IsPublishedVisibleFor(cancelledOp, 1, 1))
        {
            throw new InvalidOperationException("A cancelled publication marked an operation published.");
        }
    }

    private static void CheckStaleAndSuperseded()
    {
        // Stale epoch.
        var stale = new ToolPublicationTracker();
        Guid staleOp = stale.StartOperation(2, 1);
        if (stale.Complete(staleOp, 3, 5, Receipt(5, 1, ToolPublicationAvailability.Visible)) !=
            ToolPublicationOutcome.Superseded ||
            stale.IsPublishedVisibleFor(staleOp, 2, 1))
        {
            throw new InvalidOperationException("A stale-epoch receipt marked an operation published.");
        }

        // Superseded sequence.
        var superseded = new ToolPublicationTracker();
        Guid supersededOp = superseded.StartOperation(3, 1);
        if (superseded.Complete(supersededOp, 3, 10, Receipt(9, 1, ToolPublicationAvailability.Visible)) !=
            ToolPublicationOutcome.Superseded ||
            superseded.IsPublishedVisibleFor(supersededOp, 3, 1))
        {
            throw new InvalidOperationException("A superseded-sequence receipt marked an operation published.");
        }

        // Revision mismatch after an update/replace: the old revision is stale.
        var revised = new ToolPublicationTracker();
        Guid revisedOp = revised.StartOperation(3, 8);
        if (revised.Complete(revisedOp, 3, 9, Receipt(9, 7, ToolPublicationAvailability.Visible)) !=
            ToolPublicationOutcome.Superseded)
        {
            throw new InvalidOperationException("A mismatched-revision receipt was not superseded.");
        }
    }

    private static void CheckUnknownAndRetire()
    {
        var tracker = new ToolPublicationTracker();
        if (tracker.Complete(Guid.NewGuid(), 1, 1, Receipt(1, 1, ToolPublicationAvailability.Visible)) !=
            ToolPublicationOutcome.UnknownOperation)
        {
            throw new InvalidOperationException("An unknown operation id was accepted.");
        }

        Guid first = tracker.StartOperation(1, 1);
        Guid second = tracker.StartOperation(1, 2);
        if (tracker.RetainedOperations != 2)
        {
            throw new InvalidOperationException("The tracker did not retain both operations.");
        }

        // Operations are independent: retiring one never touches the other.
        tracker.Complete(first, 1, 1, Receipt(1, 1, ToolPublicationAvailability.Visible));
        if (!tracker.Retire(first) || tracker.RetainedOperations != 1 ||
            tracker.IsPublishedVisibleFor(first, 1, 1))
        {
            throw new InvalidOperationException("Retire did not remove exactly one operation.");
        }

        tracker.Clear();
        if (tracker.RetainedOperations != 0 ||
            tracker.Complete(second, 1, 1, Receipt(1, 2, ToolPublicationAvailability.Visible)) !=
                ToolPublicationOutcome.UnknownOperation)
        {
            throw new InvalidOperationException("Clear did not retire every operation.");
        }
    }
}
