using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.Simple.Tools.ConstraintsDrc;

/// <summary>
/// One constraint-snapshot value row for display. Snapshot values are native
/// catalog facts; they are not labeled assigned or effective. Effective facts
/// require the packaged Engine effective-read API (see LiveEngineReason).
/// </summary>
public sealed record ConstraintsDrcValueRow(
    string Domain,
    string SetName,
    string Layer,
    string Name,
    string Mode,
    string RawValue)
{
    public string Display => $"{Domain} | {SetName} | {Layer} | {Name} = {RawValue} [{Mode}]";
}

/// <summary>One constraint-set row for display.</summary>
public sealed record ConstraintsDrcSetRow(
    string Domain,
    string Name,
    int ValueCount,
    string Flags,
    string NativeSource)
{
    public string Display =>
        $"{Domain} | {Name} | {ValueCount} values" +
        (Flags.Length > 0 ? $" | {Flags}" : string.Empty) +
        $" [{NativeSource}]";
}

/// <summary>One constraint-field row for display.</summary>
public sealed record ConstraintsDrcFieldRow(
    string Domain,
    string NativeName,
    string NativeSource)
{
    public string Display => $"{Domain} | {NativeName} [{NativeSource}]";
}

/// <summary>One constraint-assignment row for display.</summary>
public sealed record ConstraintsDrcAssignmentRow(
    string Subject,
    string Domain,
    string ConstraintSet,
    string NativeProperty,
    string NativeSource)
{
    public string Display =>
        $"{Subject} | {Domain} <- {ConstraintSet} ({NativeProperty}) [{NativeSource}]";
}

/// <summary>One net-class row for display.</summary>
public sealed record ConstraintsDrcNetClassRow(
    string Name,
    string Membership,
    string NativeSource)
{
    public string Display => $"{Name} | {Membership} [{NativeSource}]";
}

/// <summary>One class-class spacing row for display.</summary>
public sealed record ConstraintsDrcClassClassRow(
    string NetClassA,
    string NetClassB,
    string SpacingConstraintSet,
    bool IsReadOnly,
    string NativeSource)
{
    public string Display =>
        $"{NetClassA} x {NetClassB} -> {SpacingConstraintSet}" +
        (IsReadOnly ? " | readonly" : string.Empty) +
        $" [{NativeSource}]";
}

/// <summary>One DRC marker row for display. Violating objects travel only as a
/// count; PD never manufactures an object reference from coordinates.</summary>
public sealed record ConstraintsDrcMarkerRow(
    string Key,
    string Name,
    string Type,
    string Layer,
    string Position,
    string Expected,
    string Actual,
    string Source,
    bool Waived,
    int ViolatingObjectCount,
    bool Reviewed)
{
    public string Display =>
        $"{Name} | {Type} | {Layer} | ({Position}) | expected {Expected} | actual {Actual}" +
        (Waived ? " | waived" : string.Empty) +
        (Reviewed ? " | reviewed" : string.Empty);
}

/// <summary>One marker-count group row for display.</summary>
public sealed record ConstraintsDrcMarkerGroupRow(
    string Type,
    string Layer,
    int Count,
    int WaivedCount)
{
    public string Display => $"{Type} | {Layer} | {Count} markers ({WaivedCount} waived)";
}

/// <summary>
/// Constraints/DRC task state. The page opens while disconnected and every
/// action carries an explicit gate: only unsafe or unavailable actions are
/// disabled, each with a visible reason. The view model owns no Engine
/// session and creates no native authority; the caller supplies one shared
/// Engine session. All Engine access uses the public Engine package API.
/// </summary>
public sealed record ConstraintsDrcSelection(
    ConstraintsDrcValueRow Row,
    WorkspaceDocumentIdentity Document,
    long SnapshotRevision,
    string ConstraintFingerprint);

public sealed record ConstraintInputDiagnostic(string Field, string Code, string Message);

public sealed record ConstraintEditInput(EngineConstraintQuery Query, EngineConstraintChange Change);

public sealed class ConstraintsDrcViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// Engine API requirement for effective-value reads, typed constraint
    /// edits, and fresh native DRC execution, referenced from the
    /// centrally pinned Engine package and called directly when live:
    /// constraint effective-read
    /// (AllegroWorkspaceConstraints.ReadEffectiveAsync), constraint
    /// mutation-preparation (PrepareChangeAsync with readback), and fresh
    /// native DRC execution (AllegroWorkspaceDrcRun / AllegroWorkspaceDrcReview).
    /// Gated reasons below always name these APIs so a disabled action never
    /// hides which Engine surface it needs.
    /// </summary>
    public const string LiveEngineReason =
        "Requires a live licensed Engine session with constraint/DRC capabilities: constraint " +
        "effective-read (ReadEffectiveAsync), constraint mutation-preparation " +
        "(PrepareChangeAsync with readback), and fresh native DRC execution " +
        "(AllegroWorkspaceDrcRun / AllegroWorkspaceDrcReview), referenced from the " +
        "the centrally pinned Engine package. " +
        "Snapshot observation and existing-marker reads run without them, but " +
        "they are not labeled assigned or effective and never claim execution.";

    private const int MaxDisplayRows = 200;

    private readonly AllegroEngineSession _session;
    private readonly Func<Func<Task>, Task> _publish;
    private long _requestRevision;
    private ConstraintsDrcSelection? _selection;
    private EngineConstraintMutationOperation? _mutationOperation;
    private readonly List<EngineConstraintMutationResult> _mutationHistory = [];

    public IReadOnlyList<EngineConstraintMutationResult> MutationHistory => _mutationHistory;
    public EngineConstraintMutationOperation? PendingMutation => _mutationOperation;
    public Action<EngineConstraintMutationOperation, Action<EngineConstraintMutationResult>>? MutationOwnerRetained { get; set; }
    public Action<EngineConstraintMutationOperation>? MutationOwnerReleasing { get; set; }
    public Func<Task>? MutationOwnerSettled { get; set; }
    public Func<CancellationToken, Task>? SharedRecovery { get; set; }
    public IReadOnlyList<string> ScalarKindChoices { get; } = Enum.GetNames<EngineConstraintScalarKind>();
    public IReadOnlyList<string> ScalarUnitChoices { get; } = Enum.GetNames<EngineConstraintUnit>();
    public IReadOnlyList<string> ChangeKindChoices { get; } = Enum.GetNames<EngineConstraintChangeKind>();
    private EngineSessionSnapshot _state;
    private CancellationTokenSource? _pending;
    private bool _disposed;

    private EngineConstraintSnapshot? _snapshot;
    private string _snapshotSummary = "No constraint snapshot acquired yet.";
    private bool _snapshotStale;
    private string _snapshotCoverage = "Coverage unknown: acquire a snapshot first.";
    private IReadOnlyList<ConstraintsDrcSetRow> _setRows = [];
    private IReadOnlyList<ConstraintsDrcValueRow> _valueRows = [];
    private IReadOnlyList<ConstraintsDrcFieldRow> _fieldRows = [];
    private IReadOnlyList<ConstraintsDrcAssignmentRow> _assignmentRows = [];
    private IReadOnlyList<ConstraintsDrcAssignmentRow> _contextAssignmentRows = [];
    private IReadOnlyList<ConstraintsDrcNetClassRow> _netClassRows = [];
    private IReadOnlyList<ConstraintsDrcClassClassRow> _classClassRows = [];
    private string _valueDomainFilter = "All";
    private string _valueTextFilter = string.Empty;

    private AllegroWorkspaceDrcRead? _markerRead;
    private AllegroWorkspaceDrcRead? _previousMarkerRead;
    private string _markerSummary = "No DRC marker read yet.";
    private IReadOnlyList<ConstraintsDrcMarkerRow> _markerRows = [];
    private IReadOnlyList<ConstraintsDrcMarkerGroupRow> _groupRows = [];
    private string _markerTypeFilter = string.Empty;
    private string _markerLayerFilter = string.Empty;
    private string _markerTextFilter = string.Empty;
    private bool _includeWaived = true;
    private ConstraintsDrcMarkerRow? _selectedMarker;
    private string _comparisonSummary = "Read markers twice to compare captures.";
    private string _exportText = string.Empty;
    private readonly Dictionary<(WorkspaceDocumentIdentity Document, string Subject), string> _reviewNotes = [];

    private bool _isBusy;
    private string _busyDetail = string.Empty;
    private string _statusTitle = "Constraints / DRC";
    private string _statusDetail = "Open PD Simple from Allegro with a board open to browse constraints and DRC markers.";

    private ConstraintsDrcValueRow? _selectedValue;
    private string _queryKindText = "Number";
    private string _queryUnitText = "Mils";
    private string _newValueText = string.Empty;
    private string _editChangeKindText = "SetValue";
    private string _effectiveSummary = "No effective read yet. Select a snapshot value first.";
    private string _editSummary = "No prepared edit. Select a snapshot value, enter a typed value, then prepare.";
    private string _drcRunSummary = "DRC has not been executed from this page.";
    private EngineConstraintRead? _lastEffective;
    private EnginePreparedConstraintChange? _prepared;
    private string _preparedDescription = string.Empty;
    private EngineConstraintMutationResult? _lastMutation;
    internal AllegroWorkspaceDrcRunResult? LastDrcRun { get; private set; }

    public ConstraintsDrcViewModel(AllegroEngineSession session, Func<Func<Task>, Task>? publish = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _publish = publish ?? (action => action());
        _state = session.State;
        session.StateChanged += Session_StateChanged;
        RefreshConnectionText();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsConnected => _state.ConnectionState == EngineConnectionState.Ready;

    public string StatusTitle
    {
        get => _statusTitle;
        private set => SetField(ref _statusTitle, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetField(ref _statusDetail, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public string BusyDetail
    {
        get => _busyDetail;
        private set => SetField(ref _busyDetail, value);
    }

    private async void Session_StateChanged(object? sender, EngineSessionSnapshot snapshot)
    {
        try
        {
            await _publish(() =>
            {
                ApplySessionState(snapshot);
                return Task.CompletedTask;
            });
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.TraceError("Constraint state publication failed: {0}", error);
        }
    }

    internal void ApplySessionState(EngineSessionSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }
        if (_state.Document != snapshot.Document || snapshot.ConnectionState != EngineConnectionState.Ready)
        {
            _requestRevision++;
            SelectedValue = null;
            SelectedMarker = null;
            _prepared = null;
            _lastEffective = null;
            NewValueText = string.Empty;
            EffectiveSummary = "board_changed: acquire a fresh snapshot and select a value for this document.";
            EditSummary = "board_changed: preparation inputs were retired; native operation evidence is retained.";
        }
        _state = snapshot;
        RefreshConnectionText();
        RaiseChanged(nameof(IsConnected));
        RaiseChanged(nameof(CanRefreshConstraints));
        RaiseChanged(nameof(RefreshConstraintsReason));
        RaiseChanged(nameof(CanReadMarkers));
        RaiseChanged(nameof(ReadMarkersReason));
        RaiseChanged(nameof(CanReadEffective));
        RaiseChanged(nameof(ReadEffectiveReason));
        RaiseChanged(nameof(CanEditConstraints));
        RaiseChanged(nameof(EditConstraintsReason));
        RaiseChanged(nameof(CanRunDrc));
        RaiseChanged(nameof(RunDrcReason));
        RaiseEditInputChanged();
    }

    private void RefreshConnectionText()
    {
        if (IsConnected)
        {
            WorkspaceDocumentIdentity? document = _state.Document;
            StatusTitle = "Connected" +
                (document?.Design is { Length: > 0 } design ? $" — {design}" : string.Empty);
            StatusDetail = document is null
                ? "Connected to Allegro. Acquire a constraint snapshot or read DRC markers."
                : $"Connected to Allegro. Document board generation {document.BoardGeneration.ToString(CultureInfo.InvariantCulture)}. " +
                  "Acquire a constraint snapshot or read DRC markers.";
        }
        else
        {
            StatusTitle = "Constraints / DRC — offline setup";
            StatusDetail = _session.State.ConnectionState switch
            {
                EngineConnectionState.Faulted =>
                    "The Engine session is faulted. Reconnect from Home, then return; browsing stays available offline.",
                EngineConnectionState.Disconnected =>
                    "Not connected to Allegro. This page opens offline: connect from Home to acquire live data.",
                _ => "The Engine session is not ready. This page opens offline: live actions are gated until it is ready.",
            };
        }
    }

    private bool HasCapability(EngineCapabilityId capability) =>
        _state.Capabilities.Items.Any(item =>
            item.Id == capability && item.Availability == EngineCapabilityAvailability.Available);

    private string? CapabilityReason(EngineCapabilityId capability)
    {
        EngineCapability? item = _state.Capabilities.Items
            .FirstOrDefault(candidate => candidate.Id == capability);
        if (item is null)
        {
            return $"Engine capability '{capability.Value}' was not reported by this Engine build.";
        }
        return item.Availability == EngineCapabilityAvailability.Available
            ? null
            : item.UnavailableReason ?? $"Engine capability '{capability.Value}' is unavailable.";
    }

    private string? RequireLive(EngineCapabilityId capability, string action)
    {
        if (_disposed)
        {
            return $"{action} unavailable: the view is closed.";
        }
        if (!IsConnected)
        {
            return $"{action} unavailable: the Engine session is not connected. Connect from Home first.";
        }
        if (IsBusy)
        {
            return $"{action} unavailable: another Constraints/DRC operation is in flight ({BusyDetail}).";
        }
        string? capabilityReason = CapabilityReason(capability);
        return capabilityReason is null ? null : $"{action} unavailable: {capabilityReason}";
    }

    public bool CanRefreshConstraints => RequireLive(EngineCapabilities.Constraints, "Constraint refresh") is null;

    public string RefreshConstraintsReason =>
        RequireLive(EngineCapabilities.Constraints, "Constraint refresh") ??
        "Reads a fresh whole-board constraint snapshot from Allegro.";

    public bool CanReadMarkers => RequireLive(EngineCapabilities.Drc, "DRC marker read") is null;

    public string ReadMarkersReason =>
        RequireLive(EngineCapabilities.Drc, "DRC marker read") ??
        "Enumerates Allegro's existing DRC markers without running DRC.";

    private string GateOrPending(EngineCapabilityId capability, string action, string ready)
    {
        string? gate = RequireLive(capability, action);
        return gate is null ? ready : gate + " " + LiveEngineReason;
    }

    public bool CanReadEffective =>
        RequireLive(EngineCapabilities.Constraints, "Effective constraint read") is null && SelectionIsCurrent;

    public string ReadEffectiveReason => GateOrPending(
        EngineCapabilities.Constraints,
        "Effective constraint read",
        "Reads exact assigned and effective facts for the selected snapshot value. " +
        "Missing, conflicting, or unsupported facts are reported, never replaced.");

    public bool CanEditConstraints =>
        RequireLive(EngineCapabilities.Constraints, "Constraint edit") is null && SelectionIsCurrent;

    public string EditConstraintsReason => GateOrPending(
        EngineCapabilities.Constraints,
        "Constraint edit",
        "Prepares a typed constraint change against a fresh effective read, then " +
        "executes it once with before/after readback. A refresh reprepares first.");

    public bool CanRunDrc =>
        RequireLive(EngineCapabilities.Drc, "DRC run") is null;

    public string RunDrcReason => GateOrPending(
        EngineCapabilities.Drc,
        "DRC run",
        "Runs fresh native DRC for the full-board scope and captures fresh markers " +
        "with completion and rule-scope evidence. Marker reads alone never claim execution.");

    public bool HasSnapshot => _snapshot is not null;

    public string SnapshotSummary
    {
        get => _snapshotSummary;
        private set => SetField(ref _snapshotSummary, value);
    }

    public string SnapshotCoverage
    {
        get => _snapshotCoverage;
        private set => SetField(ref _snapshotCoverage, value);
    }

    public IReadOnlyList<ConstraintsDrcSetRow> SetRows
    {
        get => _setRows;
        private set => SetField(ref _setRows, value);
    }

    public IReadOnlyList<ConstraintsDrcFieldRow> FieldRows
    {
        get => _fieldRows;
        private set => SetField(ref _fieldRows, value);
    }

    public IReadOnlyList<ConstraintsDrcAssignmentRow> AssignmentRows
    {
        get => _assignmentRows;
        private set => SetField(ref _assignmentRows, value);
    }

    public IReadOnlyList<ConstraintsDrcAssignmentRow> ContextAssignmentRows
    {
        get => _contextAssignmentRows;
        private set => SetField(ref _contextAssignmentRows, value);
    }

    public IReadOnlyList<ConstraintsDrcNetClassRow> NetClassRows
    {
        get => _netClassRows;
        private set => SetField(ref _netClassRows, value);
    }

    public IReadOnlyList<ConstraintsDrcClassClassRow> ClassClassRows
    {
        get => _classClassRows;
        private set => SetField(ref _classClassRows, value);
    }

    public IReadOnlyList<ConstraintsDrcValueRow> ValueRows
    {
        get => _valueRows;
        private set => SetField(ref _valueRows, value);
    }

    public string ValueDomainFilter
    {
        get => _valueDomainFilter;
        set
        {
            if (SetField(ref _valueDomainFilter, value))
            {
                ApplyValueFilter();
            }
        }
    }

    public string ValueTextFilter
    {
        get => _valueTextFilter;
        set
        {
            if (SetField(ref _valueTextFilter, value))
            {
                ApplyValueFilter();
            }
        }
    }

    /// <summary>
    /// Observes the already accepted session snapshot. No native request is
    /// made; the summary always names the observation as observed, never as
    /// a fresh refresh.
    /// </summary>
    public void ObserveConstraintSnapshot()
    {
        string? gate = RequireLive(EngineCapabilities.Constraints, "Constraint observation");
        if (gate is not null)
        {
            StatusDetail = gate;
            return;
        }
        try
        {
            AcceptSnapshot(_session.Workspace.Constraints.Read(), wasRefresh: false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            StatusDetail = $"Constraint observation failed: {exception.Message}";
        }
    }

    /// <summary>
    /// Explicitly asks the resident for a fresh whole-board snapshot. A new
    /// native request is issued; the previous snapshot is historical.
    /// </summary>
    public Task RefreshConstraintSnapshotAsync(CancellationToken callerToken = default) =>
        _publish(() => RefreshConstraintSnapshotCoreAsync(callerToken));

    private async Task RefreshConstraintSnapshotCoreAsync(CancellationToken callerToken = default)
    {
        string? gate = RequireLive(EngineCapabilities.Constraints, "Constraint refresh");
        if (gate is not null)
        {
            StatusDetail = gate;
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        WorkspaceDocumentIdentity? requestedDocument = _session.State.Document;
        long requestedRevision = ++_requestRevision;
        SetBusy(true, "refreshing constraint snapshot");
        try
        {
            EngineConstraintSnapshot snapshot = await _session.Workspace.Constraints
                .AcquireAsync(token);
            if (AcceptsResult(requestedDocument, snapshot.Document, requestedRevision, token))
            {
                AcceptSnapshot(snapshot, wasRefresh: true);
            }
        }
        catch (OperationCanceledException)
        {
            if (CanPublishRequest(requestedDocument, requestedRevision, CancellationToken.None))
            {
                StatusDetail = "Constraint refresh cancelled; the previous snapshot, if any, is unchanged.";
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Constraint refresh failed: {0}", exception);
            if (CanPublishRequest(requestedDocument, requestedRevision, token))
            {
                StatusDetail = $"Constraint refresh failed: {exception.Message}";
            }
        }
        finally
        {
            CompleteRequest(linked);
        }
    }

    internal void AcceptSnapshot(EngineConstraintSnapshot snapshot, bool wasRefresh)
    {
        SelectedValue = null;
        _prepared = null;
        _snapshot = snapshot;
        _snapshotStale = false;
        string rowCapNote = snapshot.Sets.Count > MaxDisplayRows ||
            snapshot.Values.Count > MaxDisplayRows
            ? $" Lists show the first {MaxDisplayRows} rows."
            : string.Empty;
        SnapshotSummary =
            $"{(wasRefresh ? "Fresh native refresh" : "Observed session snapshot")} — " +
            $"document board generation {snapshot.Document.BoardGeneration.ToString(CultureInfo.InvariantCulture)}, " +
            $"snapshot revision {snapshot.SnapshotRevision.ToString(CultureInfo.InvariantCulture)}, " +
            $"acquisition {snapshot.Acquisition}, fingerprint {Shorten(snapshot.ConstraintFingerprint)}. " +
            $"{snapshot.Sets.Count} sets, {snapshot.Values.Count} values, {snapshot.Fields.Count} fields, " +
            $"{snapshot.Assignments.Count} assignments, {snapshot.ContextAssignments.Count} context assignments, " +
            $"{snapshot.NetClasses.Count} net classes." + rowCapNote;
        SnapshotCoverage = DescribeCoverage(snapshot.Coverage);
        SetRows = snapshot.Sets
            .OrderBy(set => set.Domain).ThenBy(set => set.Name, StringComparer.Ordinal)
            .Take(MaxDisplayRows)
            .Select(set => new ConstraintsDrcSetRow(
                set.Domain.ToString(),
                set.Name,
                set.ValueCount,
                string.Join(", ", LockFlags(set)),
                set.NativeSource))
            .ToArray();
        FieldRows = snapshot.Fields
            .OrderBy(field => field.Domain)
            .ThenBy(field => field.NativeName, StringComparer.Ordinal)
            .Take(MaxDisplayRows)
            .Select(field => new ConstraintsDrcFieldRow(
                field.Domain.ToString(),
                field.NativeName,
                field.NativeSource))
            .ToArray();
        AssignmentRows = snapshot.Assignments
            .OrderBy(assignment => assignment.SubjectKind)
            .ThenBy(assignment => assignment.Subject, StringComparer.Ordinal)
            .Take(MaxDisplayRows)
            .Select(assignment => new ConstraintsDrcAssignmentRow(
                $"{assignment.SubjectKind} {assignment.Subject}",
                assignment.Domain.ToString(),
                assignment.ConstraintSet,
                assignment.NativeProperty,
                assignment.NativeSource))
            .ToArray();
        ContextAssignmentRows = snapshot.ContextAssignments
            .OrderBy(assignment => assignment.ContextKind, StringComparer.Ordinal)
            .ThenBy(assignment => assignment.Context, StringComparer.Ordinal)
            .Take(MaxDisplayRows)
            .Select(assignment => new ConstraintsDrcAssignmentRow(
                $"{assignment.ContextKind} {assignment.Context}",
                assignment.Domain.ToString(),
                assignment.ConstraintSet,
                assignment.NativeProperty,
                assignment.NativeSource))
            .ToArray();
        NetClassRows = snapshot.NetClasses
            .OrderBy(netClass => netClass.Name, StringComparer.Ordinal)
            .Take(MaxDisplayRows)
            .Select(netClass => new ConstraintsDrcNetClassRow(
                netClass.Name,
                $"members {netClass.DirectMemberCount.ToString(CultureInfo.InvariantCulture)}" +
                (netClass.FlattenedNetCount is { } flattened
                    ? $", flattened {flattened.ToString(CultureInfo.InvariantCulture)}"
                    : ", flattened unknown") +
                (netClass.Electrical ? ", electrical" : string.Empty) +
                (netClass.Physical ? ", physical" : string.Empty) +
                (netClass.Spacing ? ", spacing" : string.Empty),
                netClass.NativeSource))
            .ToArray();
        ClassClassRows = snapshot.ClassClassConstraints
            .OrderBy(item => item.NetClassA, StringComparer.Ordinal)
            .ThenBy(item => item.NetClassB, StringComparer.Ordinal)
            .Take(MaxDisplayRows)
            .Select(item => new ConstraintsDrcClassClassRow(
                item.NetClassA,
                item.NetClassB,
                item.SpacingConstraintSet,
                item.IsReadOnly,
                item.NativeSource))
            .ToArray();
        ApplyValueFilter();
        if (wasRefresh)
        {
            _prepared = null;
            _preparedDescription = string.Empty;
            EditSummary = "A fresh snapshot arrived; any prepared edit was invalidated and must be reprepared against the new fingerprint.";
            RaiseChanged(nameof(HasPreparedEdit));
            RaiseChanged(nameof(CanExecuteEdit));
            RaiseEditInputChanged();
        }
        StatusDetail = wasRefresh
            ? "Fresh constraint snapshot acquired. Prepared edits, if any existed, must be reprepared against the new fingerprint."
            : "Observed the accepted session snapshot. Use Refresh for a fresh native request.";
        RaiseChanged(nameof(HasSnapshot));
    }

    private static IEnumerable<string> LockFlags(EngineConstraintSet set)
    {
        if (set.IsLocked == true)
        {
            yield return "locked";
        }
        if (set.IsTopologyDerived == true)
        {
            yield return "topology-derived";
        }
    }

    private static string DescribeCoverage(EngineConstraintCoverage coverage)
    {
        var builder = new StringBuilder();
        builder.Append("Coverage — sets/values: ").Append(coverage.SetsAndValuesAvailable)
            .Append(", fields: ").Append(coverage.FieldsAvailable)
            .Append(", assignments: ").Append(coverage.AssignmentsAvailable)
            .Append(", context assignments: ").Append(coverage.ContextAssignmentsAvailable)
            .Append(", native DRC enumeration: ").Append(coverage.NativeDrcEnumerationAvailable)
            .Append('.');
        if (!coverage.IsConstraintComplete)
        {
            builder.Append(" Incomplete: empty collections are unknown, not empty.");
        }
        if (coverage.MissingCapabilities.Count > 0)
        {
            builder.Append(" Missing: ")
                .Append(string.Join(", ", coverage.MissingCapabilities.Order(StringComparer.Ordinal)))
                .Append('.');
        }
        return builder.ToString();
    }

    private void ApplyValueFilter()
    {
        if (_snapshot is null)
        {
            ValueRows = [];
            return;
        }
        string domainFilter = _valueDomainFilter?.Trim() ?? string.Empty;
        string textFilter = _valueTextFilter?.Trim() ?? string.Empty;
        IEnumerable<EngineConstraintValue> query = _snapshot.Values;
        if (!string.IsNullOrEmpty(domainFilter) &&
            !string.Equals(domainFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(value =>
                string.Equals(value.Domain.ToString(), domainFilter, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(textFilter))
        {
            query = query.Where(value =>
                value.SetName.Contains(textFilter, StringComparison.OrdinalIgnoreCase) ||
                value.Name.Contains(textFilter, StringComparison.OrdinalIgnoreCase) ||
                (value.Layer ?? string.Empty).Contains(textFilter, StringComparison.OrdinalIgnoreCase));
        }
        ConstraintsDrcValueRow[] rows = query
            .OrderBy(value => value.Domain)
            .ThenBy(value => value.SetName, StringComparer.Ordinal)
            .ThenBy(value => value.Layer, StringComparer.Ordinal)
            .ThenBy(value => value.Name, StringComparer.Ordinal)
            .Take(MaxDisplayRows)
            .Select(value => new ConstraintsDrcValueRow(
                value.Domain.ToString(),
                value.SetName,
                string.IsNullOrWhiteSpace(value.Layer) ? "(all layers)" : value.Layer,
                value.Name,
                value.Mode.ToString(),
                value.RawValue))
            .ToArray();
        ValueRows = rows;
    }

    private static string Shorten(string fingerprint) =>
        fingerprint.Length > 16 ? fingerprint.Substring(0, 16) : fingerprint;

    public bool HasMarkerRead => _markerRead is not null;

    public string MarkerSummary
    {
        get => _markerSummary;
        private set => SetField(ref _markerSummary, value);
    }

    public IReadOnlyList<ConstraintsDrcMarkerRow> MarkerRows
    {
        get => _markerRows;
        private set => SetField(ref _markerRows, value);
    }

    public IReadOnlyList<ConstraintsDrcMarkerGroupRow> GroupRows
    {
        get => _groupRows;
        private set => SetField(ref _groupRows, value);
    }

    public string MarkerTypeFilter
    {
        get => _markerTypeFilter;
        set
        {
            if (SetField(ref _markerTypeFilter, value))
            {
                ApplyMarkerFilter();
            }
        }
    }

    public string MarkerLayerFilter
    {
        get => _markerLayerFilter;
        set
        {
            if (SetField(ref _markerLayerFilter, value))
            {
                ApplyMarkerFilter();
            }
        }
    }

    public string MarkerTextFilter
    {
        get => _markerTextFilter;
        set
        {
            if (SetField(ref _markerTextFilter, value))
            {
                ApplyMarkerFilter();
            }
        }
    }

    public bool IncludeWaived
    {
        get => _includeWaived;
        set
        {
            if (SetField(ref _includeWaived, value))
            {
                ApplyMarkerFilter();
            }
        }
    }

    public ConstraintsDrcMarkerRow? SelectedMarker
    {
        get => _selectedMarker;
        set
        {
            if (SetField(ref _selectedMarker, value))
            {
                RaiseChanged(nameof(SelectedMarkerDetail));
                RaiseChanged(nameof(CanMarkReviewed));
            }
        }
    }

    public string SelectedMarkerDetail => _selectedMarker is null
        ? "Select a marker to inspect its expected and actual values."
        : $"Marker '{_selectedMarker.Name}' — type {_selectedMarker.Type}, layer {_selectedMarker.Layer}, " +
          $"position {_selectedMarker.Position}, expected {_selectedMarker.Expected}, actual {_selectedMarker.Actual}, " +
          $"source {_selectedMarker.Source}, waived {_selectedMarker.Waived.ToString(CultureInfo.InvariantCulture)}, " +
          $"violating objects (count only) {_selectedMarker.ViolatingObjectCount.ToString(CultureInfo.InvariantCulture)}." +
          (ReviewedNote(_selectedMarker.Key) is { } note
              ? $" PD review note: {note}"
              : " No PD review note. A PD review note is never a native DRC waiver.");

    public string ComparisonSummary
    {
        get => _comparisonSummary;
        private set => SetField(ref _comparisonSummary, value);
    }

    public string ExportText
    {
        get => _exportText;
        private set => SetField(ref _exportText, value);
    }

    public bool CanMarkReviewed => !_disposed && _selectedMarker is not null &&
        MarkerRows.Any(row => ReferenceEquals(row, _selectedMarker)) &&
        _markerRead is not null && _markerRead.Document == _session.State.Document && IsConnected;

    /// <summary>
    /// Records a PD-local review note for the selected marker. This is PD
    /// review state only and never a native DRC waiver.
    /// </summary>
    public void MarkSelectedReviewed(string note)
    {
        if (!CanMarkReviewed || _selectedMarker is null || _markerRead is null)
        {
            return;
        }
        _reviewNotes[(_markerRead.Document, _selectedMarker.Key)] = note ?? string.Empty;
        ApplyMarkerFilter();
        RaiseChanged(nameof(SelectedMarkerDetail));
    }

    private string? ReviewedNote(string key) =>
        _markerRead is not null && _reviewNotes.TryGetValue((_markerRead.Document, key), out string? note) ? note : null;

    /// <summary>
    /// Enumerates Allegro's existing DRC markers. This is a read: it never
    /// runs DRC and a new timestamp is never presented as an execution.
    /// </summary>
    public Task ReadDrcMarkersAsync(CancellationToken callerToken = default) =>
        _publish(() => ReadDrcMarkersCoreAsync(callerToken));

    private async Task ReadDrcMarkersCoreAsync(CancellationToken callerToken = default)
    {
        string? gate = RequireLive(EngineCapabilities.Drc, "DRC marker read");
        if (gate is not null)
        {
            StatusDetail = gate;
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        WorkspaceDocumentIdentity? requestedDocument = _session.State.Document;
        long requestedRevision = ++_requestRevision;
        SetBusy(true, "reading DRC markers");
        try
        {
            AllegroWorkspaceDrcRead read = await _session.Workspace.Drc
                .ReadAsync(token);
            if (AcceptsResult(requestedDocument, read.Document, requestedRevision, token))
            {
                AcceptMarkerRead(read);
            }
        }
        catch (OperationCanceledException)
        {
            if (CanPublishRequest(requestedDocument, requestedRevision, CancellationToken.None))
            {
                StatusDetail = "DRC marker read cancelled; the previous read, if any, is unchanged.";
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("DRC marker read failed: {0}", exception);
            if (CanPublishRequest(requestedDocument, requestedRevision, token))
            {
                StatusDetail = $"DRC marker read failed: {exception.Message}";
            }
        }
        finally
        {
            CompleteRequest(linked);
        }
    }

    /// <summary>
    /// Runs fresh native DRC for the full-board scope through the staged
    /// .94 Engine execution authority and captures fresh markers with
    /// completion and rule-scope evidence. A marker read taken anywhere else
    /// keeps the existing-markers source and never counts as this execution.
    /// </summary>
    public Task RunDrcAsync(CancellationToken callerToken = default) =>
        _publish(() => RunDrcCoreAsync(callerToken));

    private async Task RunDrcCoreAsync(CancellationToken callerToken = default)
    {
        string? gate = RequireLive(EngineCapabilities.Drc, "DRC run");
        if (gate is not null)
        {
            StatusDetail = gate + " " + LiveEngineReason;
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        WorkspaceDocumentIdentity? requestedDocument = _session.State.Document;
        long requestedRevision = ++_requestRevision;
        SetBusy(true, "running native DRC");
        try
        {
            token.ThrowIfCancellationRequested();
            AllegroWorkspaceDrcRunResult result = await _session.Workspace.DrcRun
                .RunAsync(EngineDrcRunRequest.FullBoard, CancellationToken.None);
            LastDrcRun = result;
            WorkspaceDocumentIdentity? returnedDocument = result.FreshMarkers?.Document ?? requestedDocument;
            if (AcceptsResult(requestedDocument, returnedDocument, requestedRevision, token))
            {
                AcceptDrcRun(result);
            }
        }
        catch (OperationCanceledException)
        {
            if (CanPublishRequest(requestedDocument, requestedRevision, CancellationToken.None))
            {
                StatusDetail = "Native DRC run cancelled; earlier marker reads, if any, are unchanged and historical.";
                DrcRunSummary = "The native DRC run was cancelled before a terminal receipt.";
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Native DRC run failed: {0}", exception);
            if (CanPublishRequest(requestedDocument, requestedRevision, token))
            {
                StatusDetail = $"Native DRC run failed: {exception.Message}";
                DrcRunSummary = $"The native DRC run failed before a terminal receipt: {exception.Message}";
            }
        }
        finally
        {
            CompleteRequest(linked);
        }
    }

    private void AcceptDrcRun(AllegroWorkspaceDrcRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        LastDrcRun = result;
        string diagnostics = result.Diagnostics.Length == 0
            ? "no Engine diagnostics"
            : string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message));
        switch (result.Execution)
        {
            case EngineDrcExecutionState.Completed when
                result.WasExecutedForThisEvidence && result.FreshMarkers is not null:
                AcceptMarkerRead(result.FreshMarkers);
                DrcRunSummary =
                    $"Fresh native DRC completed (scope {result.ExecutedScope}). " +
                    $"Post-run freshness {result.PostRunFreshness}; " +
                    $"{result.FreshMarkers.MarkerEvidence.Length} fresh markers; {diagnostics}.";
                StatusDetail =
                    "Fresh native DRC executed for the full-board scope and fresh markers were captured. " +
                    "Marker reads captured before this run are historical. " + DrcRunSummary;
                break;
            case EngineDrcExecutionState.Completed:
                DrcRunSummary =
                    $"Native DRC reported completion for scope {result.ExecutedScope}, but no fresh execution " +
                    $"evidence was captured (post-run freshness {result.PostRunFreshness}). " +
                    $"Earlier reads stay historical; {diagnostics}.";
                StatusDetail = DrcRunSummary;
                break;
            case EngineDrcExecutionState.Unsupported:
                DrcRunSummary =
                    "Native DRC execution is unsupported in this Allegro version: " +
                    (result.Detail ?? "no detail") +
                    ". DRC was not executed; existing-marker reads stay historical.";
                StatusDetail = DrcRunSummary;
                break;
            default:
                DrcRunSummary =
                    "Native DRC execution did not complete: " +
                    (result.Detail ?? "no detail") +
                    $"; {diagnostics}. Earlier reads stay historical.";
                StatusDetail = DrcRunSummary;
                break;
        }
        RaiseChanged(nameof(LastDrcRun));
    }

    public string DrcRunSummary
    {
        get => _drcRunSummary;
        private set => SetField(ref _drcRunSummary, value);
    }

    public ConstraintsDrcValueRow? SelectedValue
    {
        get => _selectedValue;
        set
        {
            if (!ReferenceEquals(_selectedValue, value))
            {
                _selectedValue = value;
                _selection = value is not null && _snapshot is not null &&
                    ValueRows.Any(row => ReferenceEquals(row, value))
                    ? new(value, _snapshot.Document, _snapshot.SnapshotRevision, _snapshot.ConstraintFingerprint)
                    : null;
                _prepared = null;
                _requestRevision++;
                RaiseChanged(nameof(SelectedValue));
                RaiseEditInputChanged();
            }
        }
    }

    public string QueryKindText
    {
        get => _queryKindText;
        set
        {
            if (SetField(ref _queryKindText, value))
            {
                InvalidatePreparationInput();
                RaiseEditInputChanged();
            }
        }
    }

    public string QueryUnitText
    {
        get => _queryUnitText;
        set
        {
            if (SetField(ref _queryUnitText, value))
            {
                InvalidatePreparationInput();
                RaiseEditInputChanged();
            }
        }
    }

    public string NewValueText
    {
        get => _newValueText;
        set
        {
            if (SetField(ref _newValueText, value))
            {
                InvalidatePreparationInput();
                RaiseEditInputChanged();
            }
        }
    }

    public string EditChangeKindText
    {
        get => _editChangeKindText;
        set
        {
            if (SetField(ref _editChangeKindText, value))
            {
                InvalidatePreparationInput();
                RaiseEditInputChanged();
            }
        }
    }

    /// <summary>
    /// Concise validation message for the typed-edit input row: scalar
    /// kind, scalar unit, change kind, and the new value for the parsed
    /// kind and unit. Empty when the row is valid. Changes that do not
    /// consume a value (anything but SetValue) never report a value error.
    /// </summary>
    public string EditInputError => ValidateEditInput(
        SelectedValue, QueryKindText, QueryUnitText, EditChangeKindText, NewValueText,
        out _, out ConstraintInputDiagnostic? diagnostic) ? string.Empty : diagnostic!.Message;

    public static bool ValidateEditInput(
        ConstraintsDrcValueRow? row, string kindText, string unitText, string changeText, string valueText,
        [NotNullWhen(true)] out ConstraintEditInput? input,
        [NotNullWhen(false)] out ConstraintInputDiagnostic? diagnostic)
    {
        input = null;
        diagnostic = null;
        try
        {
            if (!TryNamedEnum(kindText, out EngineConstraintScalarKind kind))
            {
                diagnostic = new("kind", "invalid_enum", $"Scalar kind '{kindText}' must be a documented name.");
                return false;
            }
            if (!TryNamedEnum(unitText, out EngineConstraintUnit unit))
            {
                diagnostic = new("unit", "invalid_enum", $"Scalar unit '{unitText}' must be a documented name.");
                return false;
            }
            if (!TryNamedEnum(changeText, out EngineConstraintChangeKind changeKind))
            {
                diagnostic = new("change", "invalid_enum", $"Change kind '{changeText}' must be a documented name.");
                return false;
            }
            if (row is null)
            {
                diagnostic = new("selection", "selection_required", "Select a snapshot value first.");
                return false;
            }
            EngineConstraintQuery query = BuildEffectiveQuery(row, kind, unit);
            EngineConstraintScalar? scalar = null;
            if (changeKind == EngineConstraintChangeKind.SetValue &&
                !TryBuildScalar(kind, unit, valueText, out scalar, out string error))
            {
                diagnostic = new("value", "invalid_scalar", error);
                return false;
            }
            // The selected CSet belongs to the query. Only a net assignment
            // carries a destination CSet in the change itself.
            string? assignedSet = changeKind == EngineConstraintChangeKind.AssignElectricalSet
                ? row.SetName
                : null;
            EngineConstraintChange change = BuildChange(changeKind, scalar, assignedSet);
            ValidateEngineChange(query, change);
            input = new(query, change);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            diagnostic = new("input", "invalid_input", error.Message);
            return false;
        }
    }

    private static bool TryNamedEnum<T>(string text, out T value) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value) &&
        Enum.GetNames<T>().Any(name => string.Equals(name, text?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the edit input row is valid and the Engine preparation
    /// gate is open. Invalid input disables preparation without calling
    /// Engine preparation.
    /// </summary>
    public bool CanPrepareEdit =>
        CanEditConstraints && !IsBusy && !HasPreparedEdit &&
        SelectedValue is not null && EditInputError.Length == 0;

    /// <summary>
    /// Gate for the dual-purpose Prepare/execute edit button: preparation
    /// needs valid input, execution needs the prepared edit and a live,
    /// idle session.
    /// </summary>
    public bool CanPrepareOrExecuteEdit => HasPreparedEdit ? CanExecuteEdit : CanPrepareEdit;

    public string EditActionReason =>
        HasPreparedEdit
            ? CanExecuteEdit
                ? "Executes the prepared edit once with before/after readback."
                : "The prepared edit cannot execute while busy or disconnected."
            : EditInputError.Length > 0
                ? EditInputError
                : SelectedValue is null
                    ? "Select a snapshot value first."
                    : EditConstraintsReason;

    /// <summary>
    /// Builds the exact preparation input from the current edit row without
    /// throwing, so the click path reports invalid input through
    /// <see cref="EditInputError"/> instead of escaping the dispatcher.
    /// Returns false and leaves Engine preparation uncalled when invalid.
    /// </summary>
    public bool TryBuildEditInput(
        [NotNullWhen(true)] out EngineConstraintQuery? query,
        [NotNullWhen(true)] out EngineConstraintChange? change)
    {
        query = null;
        change = null;
        if (!ValidateEditInput(SelectedValue, QueryKindText, QueryUnitText, EditChangeKindText,
                NewValueText, out ConstraintEditInput? input, out _))
        {
            return false;
        }
        query = input.Query;
        change = input.Change;
        return true;
    }

    public bool TryBuildEffectiveInput([NotNullWhen(true)] out EngineConstraintQuery? query)
    {
        query = null;
        if (!ValidateEditInput(SelectedValue, QueryKindText, QueryUnitText, "ResetValue", string.Empty,
                out ConstraintEditInput? input, out ConstraintInputDiagnostic? diagnostic))
        {
            StatusDetail = diagnostic.Message;
            return false;
        }
        query = input.Query;
        return true;
    }

    public string EffectiveSummary
    {
        get => _effectiveSummary;
        private set => SetField(ref _effectiveSummary, value);
    }

    public string EditSummary
    {
        get => _editSummary;
        private set => SetField(ref _editSummary, value);
    }

    /// <summary>
    /// Maps one snapshot value row to an exact effective-read query. Snapshot
    /// rows are catalog facts, not assigned/effective values; the query names
    /// the constraint-set value the Engine must resolve to both facts.
    /// </summary>
    public static EngineConstraintQuery BuildEffectiveQuery(
        ConstraintsDrcValueRow row,
        EngineConstraintScalarKind kind,
        EngineConstraintUnit unit)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!TryNamedEnum(row.Domain, out EngineConstraintDomain domain))
        {
            throw new ArgumentException(
                $"Selected value has an unknown constraint domain '{row.Domain}'.", nameof(row));
        }
        ValidateKindAndUnit(kind, unit);
        RequireBoundedText(row.Name, "constraint field");
        RequireBoundedText(row.SetName, "constraint set");
        if (!string.IsNullOrWhiteSpace(row.Layer))
        {
            RequireBoundedText(row.Layer, "layer");
        }
        string? layer = string.IsNullOrWhiteSpace(row.Layer) || row.Layer == "(all layers)"
            ? null
            : row.Layer;
        if (domain == EngineConstraintDomain.Electrical)
        {
            if (layer is not null)
            {
                throw new ArgumentException("Electrical constraint values do not accept a layer.", nameof(row));
            }
        }
        else
        {
            if (layer is null)
            {
                throw new ArgumentException("Select one exact etch layer for this constraint value.", nameof(row));
            }
            // Constraint snapshot rows enumerate paramLayerGroup:ETCH.groupMembers
            // and retain the bare subclass. Effective reads require the full layer
            // identity, which the native owner verifies with axlIsLayer.
            if (!layer.Contains('/'))
            {
                layer = "ETCH/" + layer;
            }
            else if (!layer.StartsWith("ETCH/", StringComparison.OrdinalIgnoreCase) || layer.Length == 5)
            {
                throw new ArgumentException("Constraint values require an exact ETCH layer.", nameof(row));
            }
        }
        return new(
            EngineConstraintTargetKind.ConstraintSetValue,
            domain,
            row.Name,
            kind,
            unit,
            row.SetName,
            layer,
            Subject: null);
    }

    /// <summary>
    /// Parses one typed scalar for an effective-read query or a set-value
    /// change without throwing. Invalid text is rejected with the expected
    /// shape in <paramref name="error"/>, never defaulted.
    /// </summary>
    public static bool TryBuildScalar(
        EngineConstraintScalarKind kind,
        EngineConstraintUnit unit,
        string text,
        [NotNullWhen(true)] out EngineConstraintScalar? scalar,
        out string error)
    {
        scalar = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter a value.";
            return false;
        }
        try
        {
            ValidateKindAndUnit(kind, unit);
            RequireBoundedText(text, "value");
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
        string value = text.Trim();
        switch (kind)
        {
            case EngineConstraintScalarKind.Number when decimal.TryParse(
                value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number):
                scalar = unit == EngineConstraintUnit.Mils
                    ? EngineConstraintScalar.FromMils(number)
                    : EngineConstraintScalar.FromUnitless(number);
                return true;
            case EngineConstraintScalarKind.Number:
                error = "Enter a decimal number, e.g. 5.0.";
                return false;
            case EngineConstraintScalarKind.Boolean when bool.TryParse(value, out bool flag):
                scalar = EngineConstraintScalar.FromBoolean(flag);
                return true;
            case EngineConstraintScalarKind.Boolean:
                error = "Enter true or false.";
                return false;
            case EngineConstraintScalarKind.Symbol:
                scalar = EngineConstraintScalar.FromSymbol(value);
                return true;
            case EngineConstraintScalarKind.Text:
                scalar = EngineConstraintScalar.FromText(value);
                return true;
            default:
                error = $"Value '{value}' is not a {kind} scalar.";
                return false;
        }
    }

    private static void ValidateKindAndUnit(EngineConstraintScalarKind kind, EngineConstraintUnit unit)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(unit))
        {
            throw new ArgumentException("Scalar kind and unit must be defined values.");
        }
        if (kind != EngineConstraintScalarKind.Number && unit != EngineConstraintUnit.Unitless)
        {
            throw new ArgumentException("Boolean, Symbol, and Text values require Unitless units.");
        }
    }

    private static void RequireBoundedText(string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1024 || text.Any(char.IsControl))
        {
            throw new ArgumentException($"The {field} must contain 1 to 1024 characters without control characters.");
        }
    }

    private static void ValidateEngineChange(EngineConstraintQuery query, EngineConstraintChange change)
    {
        ValidateKindAndUnit(query.ValueKind, query.Unit);
        ValidateChangeFields(change.Kind, change.Value, change.ConstraintSet, change.ExpectedEffective);
        bool supportedTarget = change.Kind switch
        {
            EngineConstraintChangeKind.SetValue =>
                query.TargetKind == EngineConstraintTargetKind.ConstraintSetValue &&
                query.ValueKind != EngineConstraintScalarKind.Boolean,
            EngineConstraintChangeKind.ResetValue =>
                query.TargetKind == EngineConstraintTargetKind.ConstraintSetValue &&
                query.Domain == EngineConstraintDomain.Electrical,
            EngineConstraintChangeKind.AssignElectricalSet or EngineConstraintChangeKind.ResetElectricalAssignment =>
                query.TargetKind == EngineConstraintTargetKind.ElectricalNetAssignment &&
                query.Domain == EngineConstraintDomain.Electrical &&
                !string.IsNullOrWhiteSpace(query.Subject) && query.ConstraintSet is null && query.Layer is null,
            _ => false,
        };
        if (!supportedTarget)
        {
            throw new ArgumentException("This change is unsupported for the selected constraint target and scalar kind.");
        }
        if (change.Kind != EngineConstraintChangeKind.SetValue)
        {
            if (change.Value is not null)
            {
                throw new ArgumentException("Only a set-value change accepts a scalar value.");
            }
            return;
        }
        EngineConstraintScalar value = change.Value ??
            throw new ArgumentException("A set-value change requires a scalar value.");
        ValidateKindAndUnit(value.Kind, value.Unit);
        if (value.Kind != query.ValueKind || value.Unit != query.Unit)
        {
            throw new ArgumentException("The scalar kind and unit must match the selected query.");
        }
        bool validShape = value.Kind switch
        {
            EngineConstraintScalarKind.Number => value.Number is not null && value.Text is null && value.Boolean is null,
            EngineConstraintScalarKind.Boolean => value.Boolean is not null && value.Text is null && value.Number is null,
            _ => value.Text is not null && value.Number is null && value.Boolean is null,
        };
        if (!validShape)
        {
            throw new ArgumentException("The scalar contains missing or conflicting typed values.");
        }
        if (value.Text is not null)
        {
            RequireBoundedText(value.Text, "value");
        }
    }

    /// <summary>
    /// Parses one typed scalar for an effective-read query or a set-value
    /// change. Invalid text is rejected with the expected shape, never
    /// defaulted.
    /// </summary>
    public static EngineConstraintScalar BuildScalar(
        EngineConstraintScalarKind kind, EngineConstraintUnit unit, string text)
    {
        if (!TryBuildScalar(kind, unit, text, out EngineConstraintScalar? scalar, out string error))
        {
            throw new ArgumentException(error, nameof(text));
        }
        return scalar;
    }

    /// <summary>Builds one typed constraint change for preparation.</summary>
    public static EngineConstraintChange BuildChange(
        EngineConstraintChangeKind kind,
        EngineConstraintScalar? value = null,
        string? constraintSet = null,
        EngineConstraintFact? expectedEffective = null)
    {
        ValidateChangeFields(kind, value, constraintSet, expectedEffective);
        return new(kind, value, constraintSet, expectedEffective);
    }

    private static void ValidateChangeFields(
        EngineConstraintChangeKind kind,
        EngineConstraintScalar? value,
        string? constraintSet,
        EngineConstraintFact? expectedEffective)
    {
        bool valid = kind switch
        {
            EngineConstraintChangeKind.SetValue =>
                value is not null && constraintSet is null && expectedEffective is null,
            EngineConstraintChangeKind.ResetValue =>
                value is null && constraintSet is null && expectedEffective is null,
            EngineConstraintChangeKind.AssignElectricalSet =>
                value is null && !string.IsNullOrWhiteSpace(constraintSet) &&
                expectedEffective is { State: EngineConstraintEvidenceState.Available, ConstraintSet: null, Value: not null },
            EngineConstraintChangeKind.ResetElectricalAssignment =>
                value is null && constraintSet is null && expectedEffective is { ConstraintSet: null } inherited &&
                (inherited.State == EngineConstraintEvidenceState.Missing ||
                 inherited.State == EngineConstraintEvidenceState.Available && inherited.Value is not null),
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException($"The fields required for {kind} are absent or inconsistent.");
        }
    }

    /// <summary>
    /// Reads exact assigned and effective facts for one query. Missing,
    /// conflicting, or unsupported facts travel as data and are reported, not
    /// replaced; a snapshot value is never presented as an effective fact.
    /// </summary>
    public Task ReadEffectiveAsync(EngineConstraintQuery query, CancellationToken callerToken = default) =>
        _publish(() => ReadEffectiveCoreAsync(query, callerToken));

    private async Task ReadEffectiveCoreAsync(EngineConstraintQuery query, CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        string? gate = RequireLive(EngineCapabilities.Constraints, "Effective constraint read");
        if (gate is not null)
        {
            StatusDetail = gate + " " + LiveEngineReason;
            return;
        }
        if (!RequireSelection(query))
        {
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        WorkspaceDocumentIdentity? requestedDocument = _session.State.Document;
        long requestedRevision = ++_requestRevision;
        SetBusy(true, "reading effective constraint facts");
        try
        {
            EngineConstraintRead read = await _session.Workspace.Constraints
                .ReadEffectiveAsync(query, token);
            if (!AcceptsResult(requestedDocument, read.Document, requestedRevision, token))
            {
                return;
            }
            _lastEffective = read;
            EffectiveSummary = DescribeEffective(read);
            StatusDetail = "Effective facts acquired. Snapshot rows remain catalog facts; only this read carries assigned/effective evidence.";
        }
        catch (OperationCanceledException)
        {
            if (CanPublishRequest(requestedDocument, requestedRevision, CancellationToken.None))
            {
                StatusDetail = "Effective constraint read cancelled; the previous read, if any, is unchanged.";
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Effective constraint read failed: {0}", exception);
            if (CanPublishRequest(requestedDocument, requestedRevision, token))
            {
                StatusDetail = $"Effective constraint read failed: {exception.Message}";
            }
        }
        finally
        {
            CompleteRequest(linked);
        }
    }

    internal static string DescribeEffective(EngineConstraintRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        static string Fact(EngineConstraintFact fact) =>
            $"{fact.State}" +
            (fact.Value is null ? " (no value)" : $" value {DescribeScalar(fact.Value)}") +
            $" [{fact.NativeSource}] {fact.Detail}";
        string conflicts = read.Evidence.Conflicts.Count == 0
            ? "no conflicts"
            : "conflicts: " + string.Join("; ", read.Evidence.Conflicts);
        return $"Effective read — domain {read.Evidence.Query.Domain}, field '{read.Evidence.Query.Field}' " +
            $"(set '{read.Evidence.Query.ConstraintSet ?? "—"}', layer '{read.Evidence.Query.Layer ?? "all"}'), " +
            $"revision {read.SnapshotRevision}, current {read.IsCurrent}. " +
            $"Assigned: {Fact(read.Evidence.Assigned)}. Effective: {Fact(read.Evidence.Effective)}. {conflicts}.";
    }

    internal static string DescribeScalar(EngineConstraintScalar scalar)
    {
        ArgumentNullException.ThrowIfNull(scalar);
        return scalar.Kind switch
        {
            EngineConstraintScalarKind.Number =>
                (scalar.Number?.ToString(CultureInfo.InvariantCulture) ?? "—") + " " + scalar.Unit,
            EngineConstraintScalarKind.Boolean =>
                (scalar.Boolean?.ToString(CultureInfo.InvariantCulture) ?? "—"),
            _ => scalar.Text ?? "—",
        };
    }

    /// <summary>
    /// Prepares one typed constraint change against a fresh effective read.
    /// Preparation performs no mutation; the prepared change executes exactly
    /// once and a refresh invalidates it first.
    /// </summary>
    public Task PrepareEditAsync(EngineConstraintQuery query, EngineConstraintChange change,
        CancellationToken callerToken = default) =>
        _publish(() => PrepareEditCoreAsync(query, change, callerToken));

    private async Task PrepareEditCoreAsync(EngineConstraintQuery query, EngineConstraintChange change,
        CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(change);
        string? gate = RequireLive(EngineCapabilities.Constraints, "Constraint edit");
        if (gate is not null)
        {
            StatusDetail = gate + " " + LiveEngineReason;
            return;
        }
        if (!RequireSelection(query))
        {
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        WorkspaceDocumentIdentity? requestedDocument = _session.State.Document;
        long requestedRevision = ++_requestRevision;
        SetBusy(true, "preparing constraint change");
        try
        {
            ValidateEngineChange(query, change);
            EngineConstraintRead read = await _session.Workspace.Constraints
                .ReadEffectiveAsync(query, token);
            if (!AcceptsResult(requestedDocument, read.Document, requestedRevision, token))
            {
                return;
            }
            _lastEffective = read;
            EffectiveSummary = DescribeEffective(read);
            EnginePreparedConstraintChange prepared = await _session.Workspace.Constraints
                .PrepareChangeAsync(read, change, token);
            if (!AcceptsResult(requestedDocument, prepared.Document, requestedRevision, token))
            {
                return;
            }
            _prepared = prepared;
            _preparedDescription =
                $"Prepared {change.Kind} for domain {query.Domain}, field '{query.Field}' " +
                $"(set '{query.ConstraintSet ?? "—"}') at revision {read.SnapshotRevision}.";
            EditSummary = _preparedDescription +
                " No mutation has run. Executing applies it once with before/after readback; a refresh reprepares first.";
            StatusDetail = "Constraint change prepared. No mutation has run.";
        }
        catch (OperationCanceledException)
        {
            if (CanPublishRequest(requestedDocument, requestedRevision, CancellationToken.None))
            {
                StatusDetail = "Constraint preparation cancelled; no prepared edit is held.";
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Constraint preparation failed: {0}", exception);
            if (CanPublishRequest(requestedDocument, requestedRevision, token))
            {
                StatusDetail = $"Constraint preparation failed: {exception.Message}";
                EditSummary = $"Preparation failed: {exception.Message}";
            }
        }
        finally
        {
            CompleteRequest(linked);
        }
        RaiseChanged(nameof(HasPreparedEdit));
        RaiseChanged(nameof(CanExecuteEdit));
        RaiseEditInputChanged();
    }

    public bool HasPreparedEdit => _prepared is not null;

    public bool CanExecuteEdit => HasPreparedEdit && !IsBusy && !_disposed && IsConnected &&
        _prepared!.Document == _session.State.Document;

    public bool CanRecoverEdit => _lastMutation?.CanRecover == true && !IsBusy && !_disposed && IsConnected;

    /// <summary>
    /// Executes the one held prepared change to its terminal result with
    /// before/after readback. The preparation is consumed exactly once and
    /// never replayed.
    /// </summary>
    public Task ExecuteEditAsync(CancellationToken callerToken = default) =>
        _publish(() => ExecuteEditCoreAsync(callerToken));

    private async Task ExecuteEditCoreAsync(CancellationToken callerToken = default)
    {
        EnginePreparedConstraintChange? prepared = _prepared;
        if (prepared is null)
        {
            EditSummary = "No prepared edit is held. Prepare a typed change first.";
            return;
        }
        string? gate = RequireLive(EngineCapabilities.Constraints, "Constraint edit");
        if (gate is not null)
        {
            StatusDetail = gate + " " + LiveEngineReason;
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        WorkspaceDocumentIdentity? requestedDocument = _session.State.Document;
        long requestedRevision = ++_requestRevision;
        SetBusy(true, "executing constraint change");
        try
        {
            token.ThrowIfCancellationRequested();
            if (prepared.Document != requestedDocument)
            {
                throw new InvalidOperationException("board_changed: the preparation belongs to another document.");
            }
            _prepared = null;
            await ReleaseRetainedOperationAsync();
            if (!AcceptsResult(requestedDocument, prepared.Document, requestedRevision, token))
            {
                return;
            }
            _mutationOperation = await prepared.StartAsync(CancellationToken.None);
            MutationOwnerRetained?.Invoke(_mutationOperation,
                CreateMutationResultObserver(requestedDocument, requestedRevision, token));
            // Once dispatched, a canceled view cannot discard the operation or its evidence.
            EngineConstraintMutationResult result =
                await _mutationOperation.WaitForResultAsync(CancellationToken.None);
            RetainMutation(result);
            if (MutationOwnerSettled is { } settled)
            {
                await settled();
            }
            if (!AcceptsResult(requestedDocument, result.Document, requestedRevision, token))
            {
                return;
            }
            EditSummary = DescribeMutation(result);
            StatusDetail = result.IsVerifiedSuccess
                ? "Constraint change applied and verified against after readback."
                : "Constraint change reached a terminal result without verified success; see the edit summary.";
        }
        catch (OperationCanceledException)
        {
            if (CanPublishRequest(requestedDocument, requestedRevision, CancellationToken.None))
            {
                StatusDetail = "Constraint execution wait cancelled; retained native evidence determines the terminal outcome.";
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Constraint execution failed: {0}", exception);
            if (CanPublishRequest(requestedDocument, requestedRevision, token))
            {
                StatusDetail = $"Constraint execution failed: {exception.Message}";
            }
        }
        finally
        {
            CompleteRequest(linked);
        }
        RaiseChanged(nameof(HasPreparedEdit));
        RaiseChanged(nameof(CanExecuteEdit));
        RaiseChanged(nameof(CanRecoverEdit));
        RaiseEditInputChanged();
    }

    internal static string DescribeMutation(EngineConstraintMutationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        static string Facts(EngineEffectiveConstraintEvidence evidence) =>
            $"assigned {evidence.Assigned.State}" +
            (evidence.Assigned.Value is null ? string.Empty : $" {DescribeScalar(evidence.Assigned.Value)}") +
            $" vs effective {evidence.Effective.State}" +
            (evidence.Effective.Value is null ? string.Empty : $" {DescribeScalar(evidence.Effective.Value)}");
        string readback = result is { Before: not null, After: not null }
            ? $"Readback before [{Facts(result.Before)}] after [{Facts(result.After)}]."
            : "Readback evidence is unavailable for this terminal result.";
        return $"Mutation {result.Mutation} (verified success: {result.IsVerifiedSuccess}). " +
            $"{result.Code}: {result.Message} " +
            (result.EvidenceError is null ? string.Empty : $"Evidence error: {result.EvidenceError} ") +
            $"DRC rerun requested natively: {result.DrcRun}. {readback} " +
            (result.CanRecover
                ? "Native recovery evidence is available; recovery, not replay, is the next step."
                : "No native recovery evidence is available for this result.");
    }

    /// <summary>
    /// Starts the Engine-owned recovery for the last mutation when the Engine
    /// reports recoverable evidence. Recovery is never a replay of the edit.
    /// </summary>
    public Task RecoverEditAsync(CancellationToken callerToken = default) =>
        _publish(() => RecoverEditCoreAsync(callerToken));

    private async Task RecoverEditCoreAsync(CancellationToken callerToken = default)
    {
        EngineConstraintMutationResult? mutation = _lastMutation;
        if (mutation is null || !mutation.CanRecover)
        {
            EditSummary = "No recoverable mutation is held. Recovery needs Engine recovery evidence from a terminal edit.";
            return;
        }
        string? gate = RequireLive(EngineCapabilities.Constraints, "Constraint recovery");
        if (gate is not null)
        {
            StatusDetail = gate + " " + LiveEngineReason;
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        WorkspaceDocumentIdentity? requestedDocument = _session.State.Document;
        long requestedRevision = ++_requestRevision;
        SetBusy(true, "recovering constraint change");
        try
        {
            token.ThrowIfCancellationRequested();
            if (SharedRecovery is { } shared)
            {
                await shared(token);
                if (_lastMutation is { } sharedResult &&
                    AcceptsResult(requestedDocument, sharedResult.Document, requestedRevision, token))
                {
                    EditSummary = "Recovery terminal: " + DescribeMutation(sharedResult);
                    StatusDetail = "Constraint recovery reached a terminal result.";
                }
                return;
            }
            await ReleaseRetainedOperationAsync();
            if (!AcceptsResult(requestedDocument, mutation.Document, requestedRevision, token))
            {
                return;
            }
            _mutationOperation = await mutation.StartRecoveryAsync(CancellationToken.None);
            MutationOwnerRetained?.Invoke(_mutationOperation,
                CreateMutationResultObserver(requestedDocument, requestedRevision, token));
            EngineConstraintMutationResult recovery =
                await _mutationOperation.WaitForResultAsync(CancellationToken.None);
            RetainMutation(recovery);
            if (MutationOwnerSettled is { } settled)
            {
                await settled();
            }
            if (!AcceptsResult(requestedDocument, recovery.Document, requestedRevision, token))
            {
                return;
            }
            EditSummary = "Recovery terminal: " + DescribeMutation(recovery);
            StatusDetail = "Constraint recovery reached a terminal result.";
        }
        catch (OperationCanceledException)
        {
            if (CanPublishRequest(requestedDocument, requestedRevision, CancellationToken.None))
            {
                StatusDetail = "Constraint recovery wait cancelled; retained native evidence determines the terminal outcome.";
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Constraint recovery failed: {0}", exception);
            if (CanPublishRequest(requestedDocument, requestedRevision, token))
            {
                StatusDetail = $"Constraint recovery failed: {exception.Message}";
            }
        }
        finally
        {
            CompleteRequest(linked);
        }
        RaiseChanged(nameof(CanRecoverEdit));
    }

    internal void AcceptMarkerRead(AllegroWorkspaceDrcRead read)
    {
        if (_markerRead is not null && read.Document != _markerRead.Document)
        {
            _previousMarkerRead = null;
            ComparisonSummary = "The document changed since the last read; earlier captures are historical and are not compared.";
        }
        else if (_markerRead is not null)
        {
            _previousMarkerRead = _markerRead;
        }
        _markerRead = read;
        SelectedMarker = null;
        MarkerSummary = $"Document {read.Document}. " + DescribeMarkerRead(read) +
            (read.MarkerEvidence.Length > MaxDisplayRows
                ? $" Visible list shows the first {MaxDisplayRows} rows; groups, comparison, and export cover all {read.MarkerEvidence.Length} captured markers."
                : string.Empty);
        ApplyMarkerFilter();
        RefreshGroupRows();
        RefreshComparison();
        RefreshExport();
        StatusDetail = read.State == EngineAcquisitionState.Complete
            ? "Existing DRC markers enumerated. This read did not execute DRC."
            : $"DRC marker enumeration is {read.State}; absence beyond the captured markers is unknown. This read did not execute DRC.";
        RaiseChanged(nameof(HasMarkerRead));
    }

    private static string DescribeMarkerRead(AllegroWorkspaceDrcRead read)
    {
        var builder = new StringBuilder();
        builder.Append("Capture ").Append(read.Evidence.CaptureIdentity)
            .Append(" — source ").Append(read.Evidence.Source)
            .Append(", acquisition ").Append(read.State)
            .Append(", freshness ").Append(read.Freshness)
            .Append(" (native run freshness ").Append(read.Evidence.RunFreshness).Append(')')
            .Append(", markers ").Append(read.MarkerEvidence.Length.ToString(CultureInfo.InvariantCulture))
            .Append(" of ").Append(read.Evidence.TotalMarkers.ToString(CultureInfo.InvariantCulture))
            .Append(read.Coverage.IsComplete ? ", enumeration complete." : ", enumeration PARTIAL.")
            .Append(read.Evidence.Truncated ? " Truncated at the native bound." : string.Empty);
        if (!read.UnavailableEvidence.IsEmpty)
        {
            builder.Append(" Unavailable: ")
                .Append(string.Join(", ", read.UnavailableEvidence.Order(StringComparer.Ordinal)))
                .Append('.');
        }
        foreach (EngineDiagnostic diagnostic in read.Diagnostics)
        {
            builder.Append(' ').Append(diagnostic.Message);
        }
        return builder.ToString();
    }

    private void ApplyMarkerFilter()
    {
        if (_markerRead is null)
        {
            MarkerRows = [];
            return;
        }
        string typeFilter = _markerTypeFilter?.Trim() ?? string.Empty;
        string layerFilter = _markerLayerFilter?.Trim() ?? string.Empty;
        string textFilter = _markerTextFilter?.Trim() ?? string.Empty;
        MarkerRows = _markerRead.MarkerEvidence
            .Where(marker =>
                (typeFilter.Length == 0 ||
                    string.Equals(marker.Type, typeFilter, StringComparison.OrdinalIgnoreCase)) &&
                (layerFilter.Length == 0 ||
                    string.Equals(marker.Layer?.Value ?? string.Empty, layerFilter, StringComparison.OrdinalIgnoreCase)) &&
                (textFilter.Length == 0 ||
                    marker.Name.Contains(textFilter, StringComparison.OrdinalIgnoreCase) ||
                    marker.Type.Contains(textFilter, StringComparison.OrdinalIgnoreCase)) &&
                (_includeWaived || !marker.Waived))
            .OrderBy(marker => marker.Type, StringComparer.Ordinal)
            .ThenBy(marker => marker.Layer?.Value, StringComparer.Ordinal)
            .ThenBy(marker => marker.Name, StringComparer.Ordinal)
            .Take(MaxDisplayRows)
            .Select(ToRow)
            .ToArray();
    }

    private ConstraintsDrcMarkerRow ToRow(AllegroWorkspaceDrcMarkerEvidence marker)
    {
        string key = ReviewSubjectKey(marker);
        return new(
            key,
            marker.Name,
            marker.Type,
            marker.Layer?.Value ?? "(no layer)",
            $"{marker.Position.X.ToString(CultureInfo.InvariantCulture)}, {marker.Position.Y.ToString(CultureInfo.InvariantCulture)}",
            string.IsNullOrEmpty(marker.Expected) ? "—" : marker.Expected,
            string.IsNullOrEmpty(marker.Actual) ? "—" : marker.Actual,
            string.IsNullOrEmpty(marker.Source) ? "—" : marker.Source,
            marker.Waived,
            marker.ViolatingObjectCount,
            ReviewedNote(key) is not null);
    }

    internal static string ReviewSubjectKey(AllegroWorkspaceDrcMarkerEvidence marker) =>
        System.Text.Json.JsonSerializer.Serialize(new[]
        {
            ConstraintsDrcMarkerReview.StableKey(marker), marker.Expected, marker.Actual,
        });

    internal bool SelectionIsCurrent => _selection is { } selection && _snapshot is { } snapshot &&
        selection.Document == _session.State.Document && selection.Document == snapshot.Document &&
        selection.SnapshotRevision == snapshot.SnapshotRevision &&
        selection.ConstraintFingerprint == snapshot.ConstraintFingerprint;

    private bool RequireSelection(EngineConstraintQuery query)
    {
        if (!SelectionIsCurrent || _selection is null)
        {
            StatusDetail = "board_changed: select a value from a fresh snapshot for the current document.";
            return false;
        }
        EngineConstraintQuery selectedQuery;
        try
        {
            selectedQuery = BuildEffectiveQuery(_selection.Row, query.ValueKind, query.Unit);
        }
        catch (ArgumentException error)
        {
            StatusDetail = "Invalid constraint input: " + error.Message;
            return false;
        }
        if (query != selectedQuery)
        {
            StatusDetail = "selection_changed: the query does not match the selected snapshot value.";
            return false;
        }
        if (_mutationOperation is { IsTerminal: false } || _lastMutation?.Mutation is
            EngineConstraintMutationState.Recoverable or EngineConstraintMutationState.Uncertain)
        {
            StatusDetail = "Resolve the retained native operation before preparing another constraint action.";
            return false;
        }
        return true;
    }

    internal bool AcceptsResult(WorkspaceDocumentIdentity? requested, WorkspaceDocumentIdentity? returned,
        long revision, CancellationToken cancellationToken)
    {
        if (!CanPublishRequest(requested, revision, cancellationToken))
        {
            return false;
        }
        if (requested != returned)
        {
            StatusDetail = "board_changed: the result was retained as historical evidence and was not adopted by this view.";
            return false;
        }
        return true;
    }

    private bool CanPublishRequest(WorkspaceDocumentIdentity? document, long revision, CancellationToken token) =>
        !_disposed && !token.IsCancellationRequested && revision == _requestRevision && document is not null &&
        document == _state.Document && _state.ConnectionState == EngineConnectionState.Ready &&
        document == _session.State.Document && _session.State.ConnectionState == EngineConnectionState.Ready;

    private void RetainMutation(EngineConstraintMutationResult result)
    {
        _lastMutation = result;
        if (_mutationHistory.Count == 0 || !ReferenceEquals(_mutationHistory[^1], result))
        {
            _mutationHistory.Add(result);
        }
        if (!_disposed)
        {
            RaiseChanged(nameof(MutationHistory));
            MarkSnapshotStale(result);
        }
    }

    /// <summary>
    /// The held constraint snapshot is a point-in-time read. A terminal result
    /// for its document that may have changed the board makes those values
    /// historical, so the summary says so; no further native read is issued.
    /// </summary>
    private void MarkSnapshotStale(EngineConstraintMutationResult result)
    {
        bool boardMayHaveChanged = result.Mutation is not
            (EngineConstraintMutationState.ConfirmedNoMutation or EngineConstraintMutationState.Unsupported);
        if (_snapshot is null || _snapshotStale || !boardMayHaveChanged || result.Document != _snapshot.Document)
        {
            return;
        }
        _snapshotStale = true;
        SnapshotSummary = $"Stale: a constraint mutation ({result.Mutation}) finished after this snapshot; " +
            "Refresh to re-read values. " + SnapshotSummary;
    }

    internal Action<EngineConstraintMutationResult> CreateMutationResultObserver(
        WorkspaceDocumentIdentity? document, long revision, CancellationToken token) =>
        result => ObserveSharedMutationResult(result, document, revision, token);

    private void ObserveSharedMutationResult(EngineConstraintMutationResult result,
        WorkspaceDocumentIdentity? document, long revision, CancellationToken token)
    {
        RetainMutation(result);
        if (AcceptsResult(document, result.Document, revision, token))
        {
            EditSummary = DescribeMutation(result);
            StatusDetail = "The retained constraint owner published a terminal result.";
        }
        if (!_disposed)
        {
            RaiseChanged(nameof(CanRecoverEdit));
            RaiseChanged(nameof(CanPrepareEdit));
            RaiseChanged(nameof(CanExecuteEdit));
        }
    }

    public async Task ReleaseRetainedOperationAsync()
    {
        if (_mutationOperation is not { } operation)
        {
            return;
        }
        if (!operation.IsTerminal)
        {
            throw new InvalidOperationException("The dispatched constraint operation still owns native work.");
        }
        MutationOwnerReleasing?.Invoke(operation);
        await operation.DisposeAsync();
        _mutationOperation = null;
    }

    internal Action<Exception> CaptureOperationFailureReporter()
    {
        EngineSessionSnapshot state = _state;
        long revision = _requestRevision;
        CancellationToken token = _pending?.Token ?? CancellationToken.None;
        return error =>
        {
            System.Diagnostics.Trace.TraceError("Constraint operation publication failed: {0}", error);
            if (!_disposed && !token.IsCancellationRequested && revision == _requestRevision &&
                ReferenceEquals(state, _state) && state.Document == _session.State.Document)
            {
                StatusDetail = "Operation failed: " + error.Message;
            }
        };
    }

    private void RefreshGroupRows()
    {
        if (_markerRead is null)
        {
            GroupRows = [];
            return;
        }
        GroupRows = ConstraintsDrcMarkerReview.Group(_markerRead.MarkerEvidence)
            .Select(group => new ConstraintsDrcMarkerGroupRow(
                group.Type,
                group.Layer ?? "(no layer)",
                group.Count,
                group.WaivedCount))
            .ToArray();
    }

    private void RefreshComparison()
    {
        if (_markerRead is null || _previousMarkerRead is null)
        {
            if (_markerRead is not null && _previousMarkerRead is null &&
                !ComparisonSummary.StartsWith("The document changed", StringComparison.Ordinal))
            {
                ComparisonSummary = "Read markers twice to compare captures.";
            }
            return;
        }
        ConstraintsDrcCaptureComparison comparison = ConstraintsDrcMarkerReview.Compare(
            _previousMarkerRead, _markerRead);
        ComparisonSummary =
            $"Compared {_previousMarkerRead.Evidence.CaptureIdentity} → {_markerRead.Evidence.CaptureIdentity}: " +
            $"{comparison.Added.Count} added, {comparison.Removed.Count} removed, " +
            $"{comparison.Persistent.Count} persistent. A waiver-flag change keeps a marker persistent.";
    }

    private void RefreshExport()
    {
        ExportText = _markerRead is null
            ? string.Empty
            : string.Join(Environment.NewLine, ConstraintsDrcMarkerReview.ExportLines(_markerRead));
    }

    public void CancelPending()
    {
        CancellationTokenSource? pending = _pending;
        if (pending is not null && !_isBusy)
        {
            return;
        }
        try
        {
            pending?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private CancellationTokenSource LinkCaller(CancellationToken callerToken)
    {
        CancelPending();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _pending = linked;
        return linked;
    }

    private void CompleteRequest(CancellationTokenSource pending)
    {
        if (ReferenceEquals(_pending, pending))
        {
            _pending = null;
            SetBusy(false, string.Empty);
        }
    }

    private void SetBusy(bool busy, string detail)
    {
        IsBusy = busy;
        BusyDetail = detail;
        if (busy)
        {
            StatusDetail = $"Working: {detail}…";
        }
        RaiseChanged(nameof(CanRefreshConstraints));
        RaiseChanged(nameof(RefreshConstraintsReason));
        RaiseChanged(nameof(CanReadMarkers));
        RaiseChanged(nameof(ReadMarkersReason));
        RaiseChanged(nameof(CanReadEffective));
        RaiseChanged(nameof(ReadEffectiveReason));
        RaiseChanged(nameof(CanEditConstraints));
        RaiseChanged(nameof(EditConstraintsReason));
        RaiseChanged(nameof(CanRunDrc));
        RaiseChanged(nameof(RunDrcReason));
        RaiseChanged(nameof(CanExecuteEdit));
        RaiseChanged(nameof(CanRecoverEdit));
        RaiseEditInputChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        RaiseChanged(name);
        return true;
    }

    private void RaiseChanged(string? name)
    {
        if (!_disposed)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private void RaiseEditInputChanged()
    {
        RaiseChanged(nameof(EditInputError));
        RaiseChanged(nameof(CanPrepareEdit));
        RaiseChanged(nameof(CanPrepareOrExecuteEdit));
        RaiseChanged(nameof(EditActionReason));
        RaiseChanged(nameof(CanReadEffective));
        RaiseChanged(nameof(CanEditConstraints));
        RaiseChanged(nameof(HasPreparedEdit));
        RaiseChanged(nameof(CanExecuteEdit));
    }

    private void InvalidatePreparationInput()
    {
        _requestRevision++;
        _prepared = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _requestRevision++;
        _session.StateChanged -= Session_StateChanged;
        try
        {
            _pending?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>
/// One capture-to-capture marker comparison for display.
/// </summary>
public sealed record ConstraintsDrcCaptureComparison(
    string BeforeToken,
    string AfterToken,
    IReadOnlyList<ConstraintsDrcMarkerRow> Added,
    IReadOnlyList<ConstraintsDrcMarkerRow> Removed,
    IReadOnlyList<ConstraintsDrcMarkerRow> Persistent);

/// <summary>
/// PD-side marker-review over Engine DRC reads from the staged
/// 1.13.0-preview.94 package. Grouping and capture comparison delegate to
/// the Engine-owned AllegroWorkspaceDrcReview, so PD and Engine share
/// identical comparison semantics; the stable key below is kept as a PD
/// wrapper and asserted equal to the Engine key in tests. PD keeps its own
/// row and deterministic export shapes. No native work happens here; unknown
/// violating-object identity stays a count, never a reference — PD never
/// manufactures an object reference from coordinates.
/// </summary>
public static class ConstraintsDrcMarkerReview
{
    public sealed record MarkerGroup(string Type, string? Layer, int Count, int WaivedCount);

    public static string StableKey(AllegroWorkspaceDrcMarkerEvidence marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        return string.Join(
            "",
            marker.Name,
            marker.Type,
            marker.Layer?.Value ?? "<no-layer>",
            marker.Position.X.ToString(CultureInfo.InvariantCulture),
            marker.Position.Y.ToString(CultureInfo.InvariantCulture),
            marker.Expected ?? "<no-expected>",
            marker.Actual ?? "<no-actual>");
    }

    public static IReadOnlyList<MarkerGroup> Group(
        IEnumerable<AllegroWorkspaceDrcMarkerEvidence> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        return AllegroWorkspaceDrcReview.Group(markers)
            .Select(group => new MarkerGroup(
                group.Type,
                group.Layer,
                group.Count,
                group.WaivedCount))
            .ToArray();
    }

    public static ConstraintsDrcCaptureComparison Compare(
        AllegroWorkspaceDrcRead before,
        AllegroWorkspaceDrcRead after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        EngineDrcCaptureComparison comparison =
            AllegroWorkspaceDrcReview.Compare(before, after);
        return new(
            comparison.BeforeToken,
            comparison.AfterToken,
            comparison.Added.Select(ToComparisonRow).ToArray(),
            comparison.Removed.Select(ToComparisonRow).ToArray(),
            comparison.Persistent.Select(ToComparisonRow).ToArray());
    }

    private static ConstraintsDrcMarkerRow ToComparisonRow(AllegroWorkspaceDrcMarkerEvidence marker) =>
        new(
            StableKey(marker),
            marker.Name,
            marker.Type,
            marker.Layer?.Value ?? "(no layer)",
            $"{marker.Position.X.ToString(CultureInfo.InvariantCulture)}, {marker.Position.Y.ToString(CultureInfo.InvariantCulture)}",
            marker.Expected ?? "—",
            marker.Actual ?? "—",
            marker.Source ?? "—",
            marker.Waived,
            marker.ViolatingObjectCount,
            Reviewed: false);

    /// <summary>
    /// Deterministic PD-owned report lines: header carries capture identity,
    /// source, freshness and completeness; one line per marker with
    /// expected/actual values. Violating objects appear only as a count.
    /// </summary>
    public static IReadOnlyList<string> ExportLines(AllegroWorkspaceDrcRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var lines = new List<string>
        {
            $"# PD Simple Constraints/DRC marker export; capture={read.Evidence.CaptureIdentity}; " +
            $"source={read.Evidence.Source}; freshness={read.Evidence.RunFreshness}; " +
            $"state={read.State}; complete={read.Coverage.IsComplete}; " +
            $"markers={read.MarkerEvidence.Length}",
            "# name\ttype\tlayer\tx_mils\ty_mils\texpected\tactual\tsource\twaived\tviolating_object_count",
        };
        lines.AddRange(read.MarkerEvidence
            .OrderBy(marker => marker.Type, StringComparer.Ordinal)
            .ThenBy(marker => marker.Layer?.Value, StringComparer.Ordinal)
            .ThenBy(marker => marker.Name, StringComparer.Ordinal)
            .Select(marker => string.Join(
                "\t",
                marker.Name,
                marker.Type,
                marker.Layer?.Value ?? "",
                marker.Position.X.ToString(CultureInfo.InvariantCulture),
                marker.Position.Y.ToString(CultureInfo.InvariantCulture),
                marker.Expected ?? "",
                marker.Actual ?? "",
                marker.Source ?? "",
                marker.Waived ? "true" : "false",
                marker.ViolatingObjectCount.ToString(CultureInfo.InvariantCulture))));
        return lines;
    }
}
