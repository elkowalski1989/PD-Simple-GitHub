using System.Collections.Immutable;
using System.ComponentModel;
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
/// require the packaged Engine effective-read API (see PendingPackageReason).
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
public sealed class ConstraintsDrcViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// Engine API requirement for effective-value reads, typed constraint
    /// edits, and fresh native DRC execution, bound to the staged
    /// 1.13.0-preview.104 Engine package: constraint effective-read
    /// (AllegroWorkspaceConstraints.ReadEffectiveAsync), constraint
    /// mutation-preparation (PrepareChangeAsync with readback), and fresh
    /// native DRC execution (AllegroWorkspaceDrcRun / AllegroWorkspaceDrcReview).
    /// Execution authorization is operation-level Engine truth: a run needs
    /// the session catalog to report the lane-owned engine.drc.execute
    /// capability (AllegroWorkspaceDrcRun.CapabilityId), while existing-marker
    /// reads stay under engine.drc and never authorize execution.
    /// Gated reasons below always name these APIs so a disabled action never
    /// hides which Engine surface it needs.
    /// </summary>
    public const string PendingPackageReason =
        "Requires the 1.13.0-preview.104 Engine package APIs: constraint " +
        "effective-read (ReadEffectiveAsync), constraint mutation-preparation " +
        "(PrepareChangeAsync with readback), and fresh native DRC execution " +
        "(AllegroWorkspaceDrcRun / AllegroWorkspaceDrcReview) authorized by the " +
        "Engine-reported engine.drc.execute capability. " +
        "Snapshot observation and existing-marker reads run without them, but " +
        "they are not labeled assigned or effective and never claim execution.";

    private const int MaxDisplayRows = 200;

    private readonly AllegroEngineSession _session;
    private EngineSessionSnapshot _state;
    private CancellationTokenSource? _pending;
    private bool _disposed;

    private EngineConstraintSnapshot? _snapshot;
    private string _snapshotSummary = "No constraint snapshot acquired yet.";
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
    private readonly Dictionary<string, string> _reviewNotes = new(StringComparer.Ordinal);

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

    public ConstraintsDrcViewModel(AllegroEngineSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
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

    private void Session_StateChanged(object? sender, EngineSessionSnapshot snapshot)
    {
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
        return gate is null ? ready : gate + " " + PendingPackageReason;
    }

    public bool CanReadEffective =>
        RequireLive(EngineCapabilities.Constraints, "Effective constraint read") is null;

    public string ReadEffectiveReason => GateOrPending(
        EngineCapabilities.Constraints,
        "Effective constraint read",
        "Reads exact assigned and effective facts for the selected snapshot value. " +
        "Missing, conflicting, or unsupported facts are reported, never replaced.");

    public bool CanEditConstraints =>
        RequireLive(EngineCapabilities.Constraints, "Constraint edit") is null;

    public string EditConstraintsReason => GateOrPending(
        EngineCapabilities.Constraints,
        "Constraint edit",
        "Prepares a typed constraint change against a fresh effective read, then " +
        "executes it once with before/after readback. A refresh reprepares first.");

    public bool CanRunDrc =>
        RequireLive(AllegroWorkspaceDrcRun.CapabilityId, "DRC run") is null;

    public string RunDrcReason => GateOrPending(
        AllegroWorkspaceDrcRun.CapabilityId,
        "DRC run",
        "Runs fresh native DRC for the full-board scope and captures fresh markers " +
        "with completion and rule-scope evidence. Authorization is the Engine-reported " +
        "engine.drc.execute capability; marker-read availability never authorizes execution.");

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
    public async Task RefreshConstraintSnapshotAsync(CancellationToken callerToken = default)
    {
        string? gate = RequireLive(EngineCapabilities.Constraints, "Constraint refresh");
        if (gate is not null)
        {
            StatusDetail = gate;
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        SetBusy(true, "refreshing constraint snapshot");
        try
        {
            EngineConstraintSnapshot snapshot = await _session.Workspace.Constraints
                .AcquireAsync(token).ConfigureAwait(false);
            AcceptSnapshot(snapshot, wasRefresh: true);
        }
        catch (OperationCanceledException)
        {
            StatusDetail = "Constraint refresh cancelled; the previous snapshot, if any, is unchanged.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            StatusDetail = $"Constraint refresh failed: {exception.Message}";
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void AcceptSnapshot(EngineConstraintSnapshot snapshot, bool wasRefresh)
    {
        _snapshot = snapshot;
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

    public bool CanMarkReviewed => _selectedMarker is not null;

    /// <summary>
    /// Records a PD-local review note for the selected marker. This is PD
    /// review state only and never a native DRC waiver.
    /// </summary>
    public void MarkSelectedReviewed(string note)
    {
        if (_selectedMarker is null)
        {
            return;
        }
        _reviewNotes[_selectedMarker.Key] = note ?? string.Empty;
        ApplyMarkerFilter();
        RaiseChanged(nameof(SelectedMarkerDetail));
    }

    private string? ReviewedNote(string key) =>
        _reviewNotes.TryGetValue(key, out string? note) ? note : null;

    /// <summary>
    /// Enumerates Allegro's existing DRC markers. This is a read: it never
    /// runs DRC and a new timestamp is never presented as an execution.
    /// </summary>
    public async Task ReadDrcMarkersAsync(CancellationToken callerToken = default)
    {
        string? gate = RequireLive(EngineCapabilities.Drc, "DRC marker read");
        if (gate is not null)
        {
            StatusDetail = gate;
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        SetBusy(true, "reading DRC markers");
        try
        {
            AllegroWorkspaceDrcRead read = await _session.Workspace.Drc
                .ReadAsync(token).ConfigureAwait(false);
            AcceptMarkerRead(read);
        }
        catch (OperationCanceledException)
        {
            StatusDetail = "DRC marker read cancelled; the previous read, if any, is unchanged.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            StatusDetail = $"DRC marker read failed: {exception.Message}";
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    /// <summary>
    /// Runs fresh native DRC for the full-board scope through the staged
    /// Engine execution authority (gated on engine.drc.execute) and captures fresh markers with
    /// completion and rule-scope evidence. A marker read taken anywhere else
    /// keeps the existing-markers source and never counts as this execution.
    /// </summary>
    public async Task RunDrcAsync(CancellationToken callerToken = default)
    {
        string? gate = RequireLive(AllegroWorkspaceDrcRun.CapabilityId, "DRC run");
        if (gate is not null)
        {
            StatusDetail = gate + " " + PendingPackageReason;
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        SetBusy(true, "running native DRC");
        try
        {
            AllegroWorkspaceDrcRunResult result = await _session.Workspace.DrcRun
                .RunAsync(EngineDrcRunRequest.FullBoard, token).ConfigureAwait(false);
            AcceptDrcRun(result);
        }
        catch (OperationCanceledException)
        {
            StatusDetail = "Native DRC run cancelled; earlier marker reads, if any, are unchanged and historical.";
            DrcRunSummary = "The native DRC run was cancelled before a terminal receipt.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            StatusDetail = $"Native DRC run failed: {exception.Message}";
            DrcRunSummary = $"The native DRC run failed before a terminal receipt: {exception.Message}";
        }
        finally
        {
            SetBusy(false, string.Empty);
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
        set => SetField(ref _selectedValue, value);
    }

    public string QueryKindText
    {
        get => _queryKindText;
        set => SetField(ref _queryKindText, value);
    }

    public string QueryUnitText
    {
        get => _queryUnitText;
        set => SetField(ref _queryUnitText, value);
    }

    public string NewValueText
    {
        get => _newValueText;
        set => SetField(ref _newValueText, value);
    }

    public string EditChangeKindText
    {
        get => _editChangeKindText;
        set => SetField(ref _editChangeKindText, value);
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
        if (!Enum.TryParse<EngineConstraintDomain>(row.Domain, out EngineConstraintDomain domain))
        {
            throw new ArgumentException(
                $"Unknown constraint domain '{row.Domain}'.", nameof(row));
        }
        string? layer = string.IsNullOrWhiteSpace(row.Layer) || row.Layer == "(all layers)"
            ? null
            : row.Layer;
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
    /// change. Invalid text is rejected with the expected shape, never
    /// defaulted.
    /// </summary>
    public static EngineConstraintScalar BuildScalar(
        EngineConstraintScalarKind kind, EngineConstraintUnit unit, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        string value = text.Trim();
        return kind switch
        {
            EngineConstraintScalarKind.Number when decimal.TryParse(
                value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number) =>
                unit == EngineConstraintUnit.Mils
                    ? EngineConstraintScalar.FromMils(number)
                    : EngineConstraintScalar.FromUnitless(number),
            EngineConstraintScalarKind.Boolean when bool.TryParse(value, out bool flag) =>
                EngineConstraintScalar.FromBoolean(flag),
            EngineConstraintScalarKind.Symbol => EngineConstraintScalar.FromSymbol(value),
            EngineConstraintScalarKind.Text => EngineConstraintScalar.FromText(value),
            _ => throw new ArgumentException(
                $"Value '{value}' is not a {kind} scalar.", nameof(text)),
        };
    }

    /// <summary>Builds one typed constraint change for preparation.</summary>
    public static EngineConstraintChange BuildChange(
        EngineConstraintChangeKind kind,
        EngineConstraintScalar? value = null,
        string? constraintSet = null,
        EngineConstraintFact? expectedEffective = null) => kind switch
    {
        EngineConstraintChangeKind.SetValue =>
            value is null
                ? throw new ArgumentException("A set-value change needs a typed scalar.", nameof(value))
                : new(kind, value, constraintSet, expectedEffective),
        EngineConstraintChangeKind.ResetValue => new(kind, null, constraintSet),
        EngineConstraintChangeKind.AssignElectricalSet =>
            string.IsNullOrWhiteSpace(constraintSet)
                ? throw new ArgumentException("An electrical assignment needs a constraint set.", nameof(constraintSet))
                : new(kind, null, constraintSet),
        EngineConstraintChangeKind.ResetElectricalAssignment => new(kind),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>
    /// Reads exact assigned and effective facts for one query. Missing,
    /// conflicting, or unsupported facts travel as data and are reported, not
    /// replaced; a snapshot value is never presented as an effective fact.
    /// </summary>
    public async Task ReadEffectiveAsync(
        EngineConstraintQuery query, CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        string? gate = RequireLive(EngineCapabilities.Constraints, "Effective constraint read");
        if (gate is not null)
        {
            StatusDetail = gate + " " + PendingPackageReason;
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        SetBusy(true, "reading effective constraint facts");
        try
        {
            EngineConstraintRead read = await _session.Workspace.Constraints
                .ReadEffectiveAsync(query, token).ConfigureAwait(false);
            _lastEffective = read;
            EffectiveSummary = DescribeEffective(read);
            StatusDetail = "Effective facts acquired. Snapshot rows remain catalog facts; only this read carries assigned/effective evidence.";
        }
        catch (OperationCanceledException)
        {
            StatusDetail = "Effective constraint read cancelled; the previous read, if any, is unchanged.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            StatusDetail = $"Effective constraint read failed: {exception.Message}";
        }
        finally
        {
            SetBusy(false, string.Empty);
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
    public async Task PrepareEditAsync(
        EngineConstraintQuery query, EngineConstraintChange change,
        CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(change);
        string? gate = RequireLive(EngineCapabilities.Constraints, "Constraint edit");
        if (gate is not null)
        {
            StatusDetail = gate + " " + PendingPackageReason;
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        SetBusy(true, "preparing constraint change");
        try
        {
            EngineConstraintRead read = await _session.Workspace.Constraints
                .ReadEffectiveAsync(query, token).ConfigureAwait(false);
            EnginePreparedConstraintChange prepared = await _session.Workspace.Constraints
                .PrepareChangeAsync(read, change, token).ConfigureAwait(false);
            _prepared = prepared;
            _lastMutation = null;
            _preparedDescription =
                $"Prepared {change.Kind} for domain {query.Domain}, field '{query.Field}' " +
                $"(set '{query.ConstraintSet ?? "—"}') at revision {read.SnapshotRevision}.";
            EditSummary = _preparedDescription +
                " No mutation has run. Executing applies it once with before/after readback; a refresh reprepares first.";
            StatusDetail = "Constraint change prepared. No mutation has run.";
        }
        catch (OperationCanceledException)
        {
            StatusDetail = "Constraint preparation cancelled; no prepared edit is held.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or ArgumentException)
        {
            StatusDetail = $"Constraint preparation failed: {exception.Message}";
            EditSummary = $"Preparation failed: {exception.Message}";
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
        RaiseChanged(nameof(HasPreparedEdit));
        RaiseChanged(nameof(CanExecuteEdit));
    }

    public bool HasPreparedEdit => _prepared is not null;

    public bool CanExecuteEdit => HasPreparedEdit && !IsBusy && IsConnected;

    public bool CanRecoverEdit => _lastMutation?.CanRecover == true && !IsBusy && IsConnected;

    /// <summary>
    /// Executes the one held prepared change to its terminal result with
    /// before/after readback. The preparation is consumed exactly once and
    /// never replayed.
    /// </summary>
    public async Task ExecuteEditAsync(CancellationToken callerToken = default)
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
            StatusDetail = gate + " " + PendingPackageReason;
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        SetBusy(true, "executing constraint change");
        try
        {
            EngineConstraintMutationResult result =
                await prepared.ExecuteToTerminalAsync(token).ConfigureAwait(false);
            _prepared = null;
            _lastMutation = result;
            EditSummary = DescribeMutation(result);
            StatusDetail = result.IsVerifiedSuccess
                ? "Constraint change applied and verified against after readback."
                : "Constraint change reached a terminal result without verified success; see the edit summary.";
        }
        catch (OperationCanceledException)
        {
            StatusDetail = "Constraint execution cancelled; the terminal outcome is unknown and the preparation was consumed.";
            _prepared = null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            StatusDetail = $"Constraint execution failed: {exception.Message}";
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
        RaiseChanged(nameof(HasPreparedEdit));
        RaiseChanged(nameof(CanExecuteEdit));
        RaiseChanged(nameof(CanRecoverEdit));
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
    public async Task RecoverEditAsync(CancellationToken callerToken = default)
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
            StatusDetail = gate + " " + PendingPackageReason;
            return;
        }
        using CancellationTokenSource linked = LinkCaller(callerToken);
        CancellationToken token = linked.Token;
        SetBusy(true, "recovering constraint change");
        try
        {
            await using EngineConstraintMutationOperation operation =
                await mutation.StartRecoveryAsync(token).ConfigureAwait(false);
            EngineConstraintMutationResult recovery =
                await operation.WaitForResultAsync(token).ConfigureAwait(false);
            _lastMutation = recovery;
            EditSummary = "Recovery terminal: " + DescribeMutation(recovery);
            StatusDetail = "Constraint recovery reached a terminal result.";
        }
        catch (OperationCanceledException)
        {
            StatusDetail = "Constraint recovery cancelled; the terminal outcome is unknown.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            StatusDetail = $"Constraint recovery failed: {exception.Message}";
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
        RaiseChanged(nameof(CanRecoverEdit));
    }

    private void AcceptMarkerRead(AllegroWorkspaceDrcRead read)
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
        MarkerSummary = DescribeMarkerRead(read) +
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
        string key = ConstraintsDrcMarkerReview.StableKey(marker);
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

    private void RaiseChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _session.StateChanged -= Session_StateChanged;
        try
        {
            _pending?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _pending?.Dispose();
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
