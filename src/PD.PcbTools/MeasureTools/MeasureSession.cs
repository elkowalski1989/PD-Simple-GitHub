namespace PD.PcbTools.MeasureTools;

/// <summary>Two-point measurement progress. First/second picks, clear-first,
/// and cancel all move through this state; late native feedback can only
/// complete the operation it was issued for.</summary>
public enum MeasurePointState
{
    Empty,
    FirstHeld,
    Complete,
}

/// <summary>One persistent movable/removable ruler owned by the tool view.</summary>
public sealed record MeasureRuler(
    Guid Id,
    string Label,
    MeasuredSpan Span,
    long Revision,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Operation-bound two-point measurement session. Captured endpoints and
/// native click observations share the state machine but keep their
/// provenance on every endpoint. Each native pick owns an operation id bound
/// to the session epoch: feedback from a superseded pick is not current and
/// can never complete a newer measurement. Pure logic: no native I/O.
/// </summary>
public sealed class MeasureSession
{
    private readonly Dictionary<Guid, long> _pickEpochs = new();
    private readonly List<MeasureRuler> _rulers = new();
    private long _epoch;
    private long _rulerRevision;
    private int _rulerSequence;

    public MeasurePointState State { get; private set; } = MeasurePointState.Empty;

    public MeasureEndpoint? First { get; private set; }

    public MeasureEndpoint? Second { get; private set; }

    public MeasuredSpan? Completed { get; private set; }

    /// <summary>Monotonic epoch: every clear, cancel, or new first point retires older picks.</summary>
    public long Epoch => _epoch;

    public IReadOnlyList<MeasureRuler> Rulers => _rulers.AsReadOnly();

    public bool HasFirst => State is MeasurePointState.FirstHeld or MeasurePointState.Complete;

    /// <summary>Starts one native pick and binds its operation id to this epoch.</summary>
    public Guid StartPickOperation()
    {
        Guid operationId = Guid.NewGuid();
        _pickEpochs[operationId] = _epoch;
        return operationId;
    }

    /// <summary>True when the operation id belongs to the current epoch.</summary>
    public bool IsCurrentOperation(Guid operationId) =>
        _pickEpochs.TryGetValue(operationId, out long epoch) && epoch == _epoch;

    /// <summary>Retires one pick operation without touching measurement state.</summary>
    public bool RetirePickOperation(Guid operationId) => _pickEpochs.Remove(operationId);

    public void SetFirst(MeasureEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        First = endpoint;
        Second = null;
        Completed = null;
        State = MeasurePointState.FirstHeld;
        _epoch++;
    }

    public void SetSecond(
        MeasureEndpoint endpoint,
        Guid? captureId = null,
        string? document = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (First is null || State != MeasurePointState.FirstHeld)
        {
            throw new InvalidOperationException(
                "Hold a first point before setting the second point.");
        }

        Second = endpoint;
        Completed = MeasureMath.Compute(First, endpoint, captureId, document);
        State = MeasurePointState.Complete;
    }

    /// <summary>Clear-first: annuls the pending or completed un-kept
    /// measurement and retires its picks. Persisted rulers are unaffected.</summary>
    public void ClearFirst()
    {
        First = null;
        Second = null;
        Completed = null;
        State = MeasurePointState.Empty;
        _epoch++;
    }

    /// <summary>Cancel: same annulment as clear-first, reported as a
    /// cancellation rather than a fresh start.</summary>
    public void Cancel()
    {
        ClearFirst();
    }

    /// <summary>Persists the completed span as a movable ruler.</summary>
    public MeasureRuler KeepRuler(string? label = null)
    {
        if (Completed is null || State != MeasurePointState.Complete)
        {
            throw new InvalidOperationException(
                "Complete a two-point measurement before keeping its ruler.");
        }

        _rulerRevision++;
        _rulerSequence++;
        var ruler = new MeasureRuler(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(label) ? $"M{_rulerSequence}" : label.Trim(),
            Completed,
            _rulerRevision,
            DateTimeOffset.UtcNow);
        _rulers.Add(ruler);
        return ruler;
    }

    /// <summary>Moves a ruler to a new span on a fresh revision.</summary>
    public MeasureRuler MoveRuler(Guid rulerId, MeasuredSpan span)
    {
        ArgumentNullException.ThrowIfNull(span);
        int index = _rulers.FindIndex(ruler => ruler.Id == rulerId);
        if (index < 0)
        {
            throw new ArgumentException("The ruler is absent or expired.", nameof(rulerId));
        }

        _rulerRevision++;
        MeasureRuler moved = _rulers[index] with { Span = span, Revision = _rulerRevision };
        _rulers[index] = moved;
        return moved;
    }

    /// <summary>Removes one ruler. Other rulers and their revisions are untouched.</summary>
    public bool RemoveRuler(Guid rulerId)
    {
        int index = _rulers.FindIndex(ruler => ruler.Id == rulerId);
        if (index < 0)
        {
            return false;
        }

        _rulers.RemoveAt(index);
        return true;
    }

    public void ClearRulers() => _rulers.Clear();
}
