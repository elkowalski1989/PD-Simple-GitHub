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
    /// Exact Engine API surface that the frozen Engine package does not yet
    /// publish. Effective-value reads, typed constraint edits, and fresh
    /// native DRC execution activate at integration once the coordinator
    /// publishes an Engine package containing AllegroWorkspaceDrcRun,
    /// AllegroWorkspaceDrcReview, and the constraint effective-read and
    /// mutation-preparation members. Marker review and snapshot browsing
    /// below already run against the frozen package.
    /// </summary>
    public const string PendingPackageReason =
        "Requires an Engine package containing the constraint effective-read, " +
        "constraint mutation-preparation, and fresh native DRC execution APIs " +
        "(AllegroWorkspaceDrcRun / AllegroWorkspaceDrcReview). " +
        "The frozen 1.13.0-preview.93 package exposes only snapshot observation " +
        "and existing-marker reads, so this action stays disabled until integration.";

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

    public bool CanReadEffective => false;

    public string ReadEffectiveReason => PendingPackageReason;

    public bool CanEditConstraints => false;

    public string EditConstraintsReason => PendingPackageReason;

    public bool CanRunDrc => false;

    public string RunDrcReason => PendingPackageReason;

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
/// Interim PD-side marker-review primitives over frozen Engine DRC reads:
/// grouping, filtering keys, capture comparison, and deterministic export
/// lines. No native work happens here; unknown violating-object identity
/// stays a count, never a reference. These migrate to the Engine-owned
/// AllegroWorkspaceDrcReview once the coordinator publishes an Engine
/// package containing it; the comparison semantics are kept identical so
/// the migration changes no user-visible result.
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
        return markers
            .GroupBy(marker => (marker.Type, Layer: marker.Layer?.Value))
            .Select(group => new MarkerGroup(
                group.Key.Type,
                group.Key.Layer,
                group.Count(),
                group.Count(marker => marker.Waived)))
            .OrderBy(group => group.Type, StringComparer.Ordinal)
            .ThenBy(group => group.Layer, StringComparer.Ordinal)
            .ToArray();
    }

    public static ConstraintsDrcCaptureComparison Compare(
        AllegroWorkspaceDrcRead before,
        AllegroWorkspaceDrcRead after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var remaining = new Dictionary<string, Queue<AllegroWorkspaceDrcMarkerEvidence>>(
            StringComparer.Ordinal);
        foreach (AllegroWorkspaceDrcMarkerEvidence marker in before.MarkerEvidence)
        {
            string key = StableKey(marker);
            if (!remaining.TryGetValue(key, out Queue<AllegroWorkspaceDrcMarkerEvidence>? queue))
            {
                queue = new();
                remaining[key] = queue;
            }
            queue.Enqueue(marker);
        }

        var added = new List<ConstraintsDrcMarkerRow>();
        var persistent = new List<ConstraintsDrcMarkerRow>();
        foreach (AllegroWorkspaceDrcMarkerEvidence marker in after.MarkerEvidence)
        {
            string key = StableKey(marker);
            if (remaining.TryGetValue(key, out Queue<AllegroWorkspaceDrcMarkerEvidence>? queue) &&
                queue.Count > 0)
            {
                queue.Dequeue();
                persistent.Add(ToComparisonRow(marker));
            }
            else
            {
                added.Add(ToComparisonRow(marker));
            }
        }

        var removed = new List<ConstraintsDrcMarkerRow>();
        foreach (Queue<AllegroWorkspaceDrcMarkerEvidence> queue in remaining.Values)
        {
            while (queue.Count > 0)
            {
                removed.Add(ToComparisonRow(queue.Dequeue()));
            }
        }

        return new(
            before.Evidence.CaptureIdentity,
            after.Evidence.CaptureIdentity,
            added,
            removed,
            persistent);
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
