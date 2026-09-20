namespace PD.PcbTools.OverlayTools;

/// <summary>
/// PD-side projection of one Engine publication receipt. The WPF boundary maps
/// <c>EngineWpfPublicationReceipt</c> onto this record so non-UI logic and
/// Linux-run checks never reference the Windows-only presentation assembly.
/// </summary>
public sealed record ToolPublicationReceipt(
    string Document,
    Guid CaptureId,
    long Revision,
    long Sequence,
    ToolPublicationAvailability Availability,
    DateTimeOffset PublishedAt);

/// <summary>Mirrors the Engine overlay availability relevant to publication evidence.</summary>
public enum ToolPublicationAvailability
{
    Empty,
    Pending,
    Visible,
    Unavailable,
    Disposed,
}

/// <summary>Operation-bound publication outcome for one tool publication request.</summary>
public enum ToolPublicationOutcome
{
    /// <summary>The receipt proves the requested revision is visibly published.</summary>
    Visible,
    /// <summary>A queued/pending receipt arrived; pixels are not proven visible.</summary>
    Pending,
    /// <summary>The request published nothing (empty outcome).</summary>
    Empty,
    /// <summary>The presentation reports the overlay unavailable.</summary>
    Unavailable,
    /// <summary>The receipt belongs to a stale epoch or a superseded sequence.</summary>
    Superseded,
    /// <summary>The request was cancelled or failed before a receipt existed.</summary>
    Cancelled,
    /// <summary>The operation id is unknown (never started or already retired).</summary>
    UnknownOperation,
}

/// <summary>
/// Operation-bound publication evidence for overlay tools. Each publication
/// request owns an operation id; outcomes never flow through a shared
/// last-result field, and a ticket token is never treated as a receipt.
/// Only a matching visible receipt marks an operation published.
/// </summary>
public sealed class ToolPublicationTracker
{
    private readonly Dictionary<Guid, Entry> _entries = new();

    private sealed record Entry(long Epoch, long Revision, ToolPublicationReceipt? Receipt);

    /// <summary>Starts one publication request and returns its operation id.</summary>
    public Guid StartOperation(long epoch, long revision)
    {
        Guid operationId = Guid.NewGuid();
        _entries[operationId] = new Entry(epoch, revision, null);
        return operationId;
    }

    /// <summary>
    /// Records one completed publication request. A null receipt (cancelled or
    /// failed request), a non-visible outcome, a stale epoch, a revision
    /// mismatch, or a superseded sequence records nothing beyond the outcome.
    /// </summary>
    public ToolPublicationOutcome Complete(
        Guid operationId,
        long currentEpoch,
        long currentSequence,
        ToolPublicationReceipt? receipt)
    {
        if (!_entries.TryGetValue(operationId, out Entry? entry))
        {
            return ToolPublicationOutcome.UnknownOperation;
        }

        if (receipt is null)
        {
            _entries[operationId] = entry with { Receipt = null };
            return ToolPublicationOutcome.Cancelled;
        }

        if (entry.Epoch != currentEpoch || receipt.Revision != entry.Revision)
        {
            return ToolPublicationOutcome.Superseded;
        }

        if (receipt.Sequence != currentSequence)
        {
            return ToolPublicationOutcome.Superseded;
        }

        _entries[operationId] = entry with { Receipt = receipt };
        return receipt.Availability switch
        {
            ToolPublicationAvailability.Visible => ToolPublicationOutcome.Visible,
            ToolPublicationAvailability.Pending => ToolPublicationOutcome.Pending,
            ToolPublicationAvailability.Empty => ToolPublicationOutcome.Empty,
            ToolPublicationAvailability.Unavailable => ToolPublicationOutcome.Unavailable,
            ToolPublicationAvailability.Disposed => ToolPublicationOutcome.Unavailable,
            _ => ToolPublicationOutcome.Unavailable,
        };
    }

    /// <summary>True when the retained receipt proves this exact revision is visibly published.</summary>
    public bool IsPublishedVisibleFor(Guid operationId, long epoch, long revision) =>
        _entries.TryGetValue(operationId, out Entry? entry) &&
        entry.Epoch == epoch &&
        entry.Revision == revision &&
        entry.Receipt is { Availability: ToolPublicationAvailability.Visible };

    /// <summary>Retires one operation without touching any other operation's evidence.</summary>
    public bool Retire(Guid operationId) => _entries.Remove(operationId);

    /// <summary>Retires every operation owned by this tracker.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>Number of operations currently retained.</summary>
    public int RetainedOperations => _entries.Count;
}
