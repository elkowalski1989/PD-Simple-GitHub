using System.Collections.Immutable;
using System.Text;
using CircuitHub.AllegroBridge.Engine.Analysis;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.Simple.Tools.Analysis;

/// <summary>
/// T01 Crossing review workflow over public Engine APIs. Analysis always runs
/// against one explicitly attached <see cref="DesignScene"/> through
/// <see cref="CrossingAnalyzer"/>; missing scope is reported with an explicit
/// acquisition proposal and never acquired behind the caller's back. Result
/// counts keep Engine's declared semantics: nets, objects, locations, and
/// unassigned objects are distinct figures, never renamed into "route
/// crossings". This workspace is intentionally distinct from PD's historical
/// DP-via-corridor policy: it carries its own rule identity and never reuses
/// corridor findings as crossing evidence.
/// </summary>
public sealed class CrossingReviewWorkspace : LaneAWorkspaceBase
{
    public const string DefaultRuleId = "lane-a-crossing-review";
    public const int DefaultMaximumFindings = 200;
    public const int MaximumMaximumFindings = 10_000;
    public const int DefaultMaximumCandidates = 10_000;

    private readonly Func<SceneQuery, CancellationToken, Task<DesignScene>>? _acquireLiveScene;

    private DesignScene? _sourceScene;
    private string _sourceKind = "none";
    private string _layerName = string.Empty;
    private CrossingRepresentation _representation = CrossingRepresentation.TraceCenterline;
    private decimal _regionX1Mils;
    private decimal _regionY1Mils;
    private decimal _regionX2Mils = 1000m;
    private decimal _regionY2Mils = 1000m;
    private bool _hasRegion;
    private string _subjectNet = string.Empty;
    private bool _includeUnassigned = true;
    private bool _countTouches;
    private int _maximumFindings = DefaultMaximumFindings;
    private int _maximumCandidates = DefaultMaximumCandidates;
    private string _filterText = string.Empty;
    private bool _groupByNet;
    private CrossingFindingRow? _selectedFinding;
    private CrossingAnalysis? _lastAnalysis;
    private Guid? _lastRunCaptureId;
    private int _runSequence;
    private string _coverageSummary = "No input attached.";
    private ImmutableArray<string> _missingScope = ImmutableArray<string>.Empty;

    /// <param name="acquireLiveScene">
    /// Host-supplied explicit live acquisition (normally
    /// <c>AllegroWorkspace.ReadAsync</c> projected to its scene). Null means
    /// live acquisition is unavailable and actions report setup instead.
    /// </param>
    public CrossingReviewWorkspace(
        Func<SceneQuery, CancellationToken, Task<DesignScene>>? acquireLiveScene = null)
    {
        _acquireLiveScene = acquireLiveScene;
    }

    public bool HasScene => _sourceScene is not null;

    public string SourceKind => _sourceKind;

    public Guid? SourceCaptureId => _sourceScene?.Identity.CaptureId;

    public string SourceProvenance =>
        _sourceScene is null
            ? "none"
            : $"{_sourceScene.Identity.Provenance.Provider} " +
              $"{_sourceScene.Identity.Provenance.Version} " +
              $"({_sourceScene.Identity.Provenance.OriginalAcquisition}, " +
              $"offline={_sourceScene.Identity.Provenance.IsOffline})";

    public string CoverageSummary
    {
        get => _coverageSummary;
        private set => SetField(ref _coverageSummary, value);
    }

    public ImmutableArray<string> MissingScope
    {
        get => _missingScope;
        private set => SetField(ref _missingScope, value);
    }

    public string LayerName
    {
        get => _layerName;
        set
        {
            if (SetField(ref _layerName, (value ?? string.Empty).Trim()))
            {
                Raise(nameof(CanRun));
                Raise(nameof(RunBlockedReason));
            }
        }
    }

    public CrossingRepresentation Representation
    {
        get => _representation;
        set
        {
            if (SetField(ref _representation, value))
            {
                Raise(nameof(CanRun));
                Raise(nameof(RunBlockedReason));
            }
        }
    }

    public decimal RegionX1Mils
    {
        get => _regionX1Mils;
        set { if (SetField(ref _regionX1Mils, value)) { MarkRegion(); } }
    }

    public decimal RegionY1Mils
    {
        get => _regionY1Mils;
        set { if (SetField(ref _regionY1Mils, value)) { MarkRegion(); } }
    }

    public decimal RegionX2Mils
    {
        get => _regionX2Mils;
        set { if (SetField(ref _regionX2Mils, value)) { MarkRegion(); } }
    }

    public decimal RegionY2Mils
    {
        get => _regionY2Mils;
        set { if (SetField(ref _regionY2Mils, value)) { MarkRegion(); } }
    }

    public bool HasRegion => _hasRegion;

    public string SubjectNet
    {
        get => _subjectNet;
        set => SetField(ref _subjectNet, (value ?? string.Empty).Trim());
    }

    public bool IncludeUnassigned
    {
        get => _includeUnassigned;
        set => SetField(ref _includeUnassigned, value);
    }

    public bool CountTouches
    {
        get => _countTouches;
        set => SetField(ref _countTouches, value);
    }

    public int MaximumFindings
    {
        get => _maximumFindings;
        set => SetField(ref _maximumFindings, Math.Clamp(value, 1, MaximumMaximumFindings));
    }

    public int MaximumCandidates
    {
        get => _maximumCandidates;
        set => SetField(ref _maximumCandidates, Math.Max(1, value));
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetField(ref _filterText, value ?? string.Empty))
            {
                Raise(nameof(VisibleFindings));
                Raise(nameof(VisibleGroups));
            }
        }
    }

    public bool GroupByNet
    {
        get => _groupByNet;
        set
        {
            if (SetField(ref _groupByNet, value))
            {
                Raise(nameof(VisibleFindings));
                Raise(nameof(VisibleGroups));
            }
        }
    }

    public ImmutableArray<CrossingFindingRow> Findings { get; private set; } =
        ImmutableArray<CrossingFindingRow>.Empty;

    public ImmutableArray<CrossingFindingRow> VisibleFindings =>
        _groupByNet ? ImmutableArray<CrossingFindingRow>.Empty : ApplyFilter(Findings);

    public ImmutableArray<CrossingFindingGroup> VisibleGroups =>
        _groupByNet
            ? CrossingFindingGroup.Group(ApplyFilter(Findings))
            : ImmutableArray<CrossingFindingGroup>.Empty;

    public CrossingFindingRow? SelectedFinding
    {
        get => _selectedFinding;
        set
        {
            if (SetField(ref _selectedFinding, value))
            {
                Raise(nameof(SelectedFindingDetail));
                Raise(nameof(BrowseRequestSummary));
            }
        }
    }

    public string SelectedFindingDetail => _selectedFinding?.Describe() ?? "No finding selected.";

    public string BrowseRequestSummary =>
        _selectedFinding is null
            ? "Select a finding to derive a browse scope."
            : $"Browse scope: {_selectedFinding.BrowseBounds} on layer {_selectedFinding.Layer} " +
              $"({_selectedFinding.WitnessCount} witness(es), capture {_sourceScene?.Identity.CaptureId}). " +
              "Navigation itself runs in the shared Workbench; this workspace only supplies the typed scope.";

    public CrossingAnalysis? LastAnalysis => _lastAnalysis;

    public string ResultSummary =>
        _lastAnalysis is null
            ? "No analysis has run against the attached input."
            : CrossingFindingRow.Summarize(_lastAnalysis);

    public bool FindingsTruncated => _lastAnalysis?.FindingsTruncated ?? false;

    public bool CompleteForRequestedRepresentation =>
        _lastAnalysis?.CompleteForRequestedRepresentation ?? false;

    public bool CanRun => DescribeRunReadiness().IsReady;

    public string RunBlockedReason =>
        DescribeRunReadiness() is { IsReady: true }
            ? "Ready to run."
            : DescribeRunReadiness().Reason + " Next: " + DescribeRunReadiness().NextStep;

    private void MarkRegion()
    {
        _hasRegion = true;
        Raise(nameof(HasRegion));
        Raise(nameof(CanRun));
        Raise(nameof(RunBlockedReason));
    }

    /// <summary>
    /// Attaches one immutable captured scene. The workspace never mutates,
    /// reacquires, or merges scenes; a new Run after same-path reopen
    /// requires the host to attach the fresh scene explicitly.
    /// </summary>
    public void AttachScene(DesignScene scene, string kind)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        _sourceScene = scene;
        _sourceKind = kind.Trim();
        _lastAnalysis = null;
        _lastRunCaptureId = null;
        Findings = ImmutableArray<CrossingFindingRow>.Empty;
        SelectedFinding = null;
        RefreshCoverage();
        Raise(nameof(HasScene));
        Raise(nameof(SourceKind));
        Raise(nameof(SourceCaptureId));
        Raise(nameof(SourceProvenance));
        Raise(nameof(Findings));
        Raise(nameof(ResultSummary));
        Raise(nameof(CanRun));
        Raise(nameof(RunBlockedReason));
        StatusMessage = $"Attached {_sourceKind} capture {scene.Identity.CaptureId} " +
            $"({scene.Identity.Provenance.OriginalAcquisition}). Choose a layer and region, then Run.";
    }

    public void ClearScene()
    {
        _sourceScene = null;
        _sourceKind = "none";
        _lastAnalysis = null;
        _lastRunCaptureId = null;
        Findings = ImmutableArray<CrossingFindingRow>.Empty;
        SelectedFinding = null;
        RefreshCoverage();
        Raise(nameof(HasScene));
        Raise(nameof(SourceKind));
        Raise(nameof(SourceCaptureId));
        Raise(nameof(SourceProvenance));
        Raise(nameof(Findings));
        Raise(nameof(ResultSummary));
        Raise(nameof(CanRun));
        Raise(nameof(RunBlockedReason));
        StatusMessage = "Input cleared.";
    }

    private void RefreshCoverage()
    {
        if (_sourceScene is null)
        {
            CoverageSummary = "No input attached.";
            MissingScope = ImmutableArray<string>.Empty;
            return;
        }
        RequirementEvaluation evaluation = _sourceScene.Coverage.Evaluate(
            [new DataRequirement(DataFamily.Copper, Complete: true, QualifiedGeometry: true)]);
        CoverageSummary =
            $"Copper coverage: {evaluation.State}." +
            (evaluation.Reasons.IsDefaultOrEmpty
                ? string.Empty
                : " " + string.Join(" ", evaluation.Reasons));
        MissingScope = evaluation.State == RequirementState.Satisfied
            ? ImmutableArray<string>.Empty
            : ImmutableArray.Create(
                "Copper scope is not complete with qualified geometry for the requested representation.",
                DescribeProposedAcquisitionSummary());
    }

    /// <summary>
    /// Explicit acquisition proposal for missing scope. Returned as data for
    /// the host to execute; this workspace never dispatches native work.
    /// </summary>
    public SceneQuery DescribeProposedAcquisition()
    {
        ImmutableArray<LayerId> layers = string.IsNullOrWhiteSpace(_layerName)
            ? ImmutableArray<LayerId>.Empty
            : ImmutableArray.Create(new LayerId(_layerName));
        DesignBounds? region = _hasRegion ? BuildRegion() : null;
        return new SceneQuery
        {
            Families = ImmutableArray.Create(DataFamily.Copper, DataFamily.Nets),
            IncludeContours = _representation == CrossingRepresentation.CopperArea,
            MaximumObjects = _maximumCandidates,
            Layers = layers,
            Region = region,
        };
    }

    private string DescribeProposedAcquisitionSummary()
    {
        SceneQuery query = DescribeProposedAcquisition();
        return "Proposed acquisition: families [" +
            string.Join(",", query.Families) + "], contours " +
            (query.IncludeContours ? "on" : "off") + ", budget " + query.MaximumObjects + ".";
    }

    public ToolReadiness DescribeRunReadiness()
    {
        if (_sourceScene is null)
        {
            return ToolReadiness.Blocked(
                "No captured input is attached.",
                _acquireLiveScene is null
                    ? "Attach a captured scene, example, or archive first."
                    : "Attach a captured scene or acquire the missing scope explicitly first.");
        }
        if (string.IsNullOrWhiteSpace(_layerName))
        {
            return ToolReadiness.Blocked(
                "No layer is selected.",
                "Enter the layer under review (for example ETCH/TOP).");
        }
        if (!_hasRegion)
        {
            return ToolReadiness.Blocked(
                "No region is entered.",
                "Enter the review rectangle in mils (X1/Y1/X2/Y2).");
        }
        if (!IsValidRegion(out string regionProblem))
        {
            return ToolReadiness.Blocked(regionProblem, "Correct the rectangle coordinates.");
        }
        RequirementEvaluation evaluation = _sourceScene.Coverage.Evaluate(
            [new DataRequirement(DataFamily.Copper, Complete: true, QualifiedGeometry: true)]);
        if (evaluation.State != RequirementState.Satisfied)
        {
            return ToolReadiness.Blocked(
                "Attached copper scope is incomplete for the requested representation.",
                DescribeProposedAcquisitionSummary());
        }
        return ToolReadiness.Ready();
    }

    private bool IsValidRegion(out string problem)
    {
        // Decimal coordinates are always finite; only degeneracy is rejected.
        if (_regionX1Mils == _regionX2Mils || _regionY1Mils == _regionY2Mils)
        {
            problem = "Region has zero area; a review rectangle needs nonzero width and height.";
            return false;
        }
        problem = string.Empty;
        return true;
    }

    private DesignBounds BuildRegion()
    {
        decimal x1 = Math.Min(_regionX1Mils, _regionX2Mils);
        decimal x2 = Math.Max(_regionX1Mils, _regionX2Mils);
        decimal y1 = Math.Min(_regionY1Mils, _regionY2Mils);
        decimal y2 = Math.Max(_regionY1Mils, _regionY2Mils);
        return new DesignBounds(
            DesignPoint.From(x1, y1, LengthUnit.Mils),
            DesignPoint.From(x2, y2, LengthUnit.Mils));
    }

    private GeometryShape BuildCorridor()
    {
        // Engine requires an explicit area corridor: a closed curved loop
        // forming the rectangle shell, with no holes.
        DesignBounds region = BuildRegion();
        DesignPoint min = region.Minimum;
        DesignPoint max = region.Maximum;
        DesignPoint topRight = DesignPoint.From(max.X, min.Y, LengthUnit.Mils);
        DesignPoint bottomLeft = DesignPoint.From(min.X, max.Y, LengthUnit.Mils);
        var shell = new CurveLoopGeometry(ImmutableArray.Create<GeometryShape>(
            new LineGeometry(min, topRight),
            new LineGeometry(topRight, max),
            new LineGeometry(max, bottomLeft),
            new LineGeometry(bottomLeft, min)));
        return new RegionGeometry(shell, ImmutableArray<CurveLoopGeometry>.Empty);
    }

    private CrossingQuery BuildQuery()
    {
        // Engine's query carries excluded nets only, so a subject net narrows
        // post-analysis rows instead of silently changing Engine semantics.
        return new CrossingQuery(
            DefaultRuleId,
            new LayerId(_layerName),
            BuildCorridor(),
            ImmutableArray<string>.Empty,
            _representation,
            _includeUnassigned,
            _countTouches,
            _maximumFindings,
            _maximumCandidates,
            GeometryPolicy.Default);
    }

    /// <summary>
    /// Runs qualified crossing analysis on a background thread. Only the
    /// latest dispatched Run publishes; a superseded Run reports itself as
    /// superseded instead of overwriting newer evidence. Cancellation is
    /// cooperative and never produces partial findings as success.
    /// </summary>
    public async Task<LaneAOperationResult> RunAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        ToolReadiness readiness = DescribeRunReadiness();
        if (!readiness.IsReady || _sourceScene is null)
        {
            return FailResult(correlation, "Run", readiness.Reason, started);
        }
        DesignScene scene = _sourceScene;
        CrossingQuery query = BuildQuery();
        int sequence = Interlocked.Increment(ref _runSequence);
        IsBusy = true;
        StatusMessage = $"Running crossing analysis ({correlation:D})…";
        try
        {
            CrossingAnalysis analysis = await Task.Run(
                () => CrossingAnalyzer.Analyze(scene, query, cancellationToken),
                cancellationToken);
            if (sequence != Volatile.Read(ref _runSequence))
            {
                return new LaneAOperationResult(
                    correlation, "T01", "Run", "Run (superseded)",
                    scene.Identity.CaptureId, SourceProvenance, false,
                    "Superseded: a newer Run replaced this request before it published.",
                    started, DateTimeOffset.UtcNow,
                    ImmutableArray.Create("A newer Run owns the published findings."),
                    null, null);
            }
            PublishAnalysis(scene, query, analysis);
            string outcome = analysis.IsClear && analysis.CompleteForRequestedRepresentation
                ? "Complete: no interfering objects for the requested layer and representation."
                : analysis.CompleteForRequestedRepresentation
                    ? $"Complete: {analysis.CrossingLocationCount} crossing location(s) " +
                      $"across {analysis.InterferingObjectCount} object(s) on {analysis.InterferingNetCount} net(s)."
                    : "Partial: coverage is incomplete for the requested representation; " +
                      "the result is not a clean bill and must not be read as one.";
            StatusMessage = outcome;
            return new LaneAOperationResult(
                correlation, "T01", "Run", "Run",
                scene.Identity.CaptureId, SourceProvenance, true, outcome,
                started, DateTimeOffset.UtcNow,
                analysis.Diagnostics.IsDefault ? ImmutableArray<string>.Empty : analysis.Diagnostics,
                null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Run canceled; no findings were published.";
            return new LaneAOperationResult(
                correlation, "T01", "Run", "Run (canceled)",
                scene.Identity.CaptureId, SourceProvenance, false,
                "Canceled before completion; previous findings (if any) are unchanged.",
                started, DateTimeOffset.UtcNow,
                ImmutableArray<string>.Empty, null, null);
        }
        catch (Exception error)
        {
            StatusMessage = "Run failed: " + error.Message;
            return new LaneAOperationResult(
                correlation, "T01", "Run", "Run (failed)",
                scene.Identity.CaptureId, SourceProvenance, false,
                "Failed: " + error.Message,
                started, DateTimeOffset.UtcNow,
                ImmutableArray.Create(error.GetType().FullName ?? error.GetType().Name),
                null, null);
        }
        finally
        {
            if (sequence == Volatile.Read(ref _runSequence))
            {
                IsBusy = false;
            }
        }
    }

    private void PublishAnalysis(DesignScene scene, CrossingQuery query, CrossingAnalysis analysis)
    {
        _lastAnalysis = analysis;
        _lastRunCaptureId = scene.Identity.CaptureId;
        var rows = ImmutableArray.CreateBuilder<CrossingFindingRow>();
        int index = 0;
        foreach (CrossingFinding finding in analysis.Findings)
        {
            if (!string.IsNullOrWhiteSpace(_subjectNet) &&
                !string.Equals(finding.NetName, _subjectNet, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            rows.Add(CrossingFindingRow.From(index++, finding));
        }
        Findings = rows.ToImmutable();
        if (SelectedFinding is not null &&
            !Findings.Any(row => row.Index == SelectedFinding.Index))
        {
            SelectedFinding = null;
        }
        Raise(nameof(Findings));
        Raise(nameof(VisibleFindings));
        Raise(nameof(VisibleGroups));
        Raise(nameof(LastAnalysis));
        Raise(nameof(ResultSummary));
        Raise(nameof(FindingsTruncated));
        Raise(nameof(CompleteForRequestedRepresentation));
    }

    /// <summary>
    /// Reruns against the currently attached scene. A rerun after same-path
    /// reopen only reflects fresh state when the host attached the new
    /// capture; rerunning on the identical capture ID is reported as such.
    /// </summary>
    public Task<LaneAOperationResult> RerunAsync(CancellationToken cancellationToken = default)
    {
        if (_sourceScene is not null &&
            _lastRunCaptureId.HasValue &&
            _lastRunCaptureId.Value == _sourceScene.Identity.CaptureId &&
            _lastAnalysis is not null)
        {
            StatusMessage =
                "Rerunning on the identical capture; for fresh authority attach a newly acquired scene first.";
        }
        return RunAsync(cancellationToken);
    }

    /// <summary>
    /// Explicitly acquires the missing scope through the host delegate and
    /// attaches the fresh scene. Old archives are never silently chosen:
    /// the new capture arrives with its own identity.
    /// </summary>
    public async Task<LaneAOperationResult> AcquireMissingScopeAsync(
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        if (_acquireLiveScene is null)
        {
            return FailResult(
                correlation, "AcquireMissingScope",
                "Live acquisition is unavailable in this context.", started);
        }
        SceneQuery query = DescribeProposedAcquisition();
        IsBusy = true;
        StatusMessage = "Acquiring the missing scope explicitly…";
        try
        {
            DesignScene fresh = await _acquireLiveScene(query, cancellationToken);
            AttachScene(fresh, "live");
            string outcome = $"Acquired fresh capture {fresh.Identity.CaptureId}.";
            return new LaneAOperationResult(
                correlation, "T01", "AcquireMissingScope", "AcquireMissingScope",
                fresh.Identity.CaptureId, SourceProvenance, true, outcome,
                started, DateTimeOffset.UtcNow,
                ImmutableArray<string>.Empty, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusMessage = "Acquisition canceled; the attached input is unchanged.";
            return FailResult(correlation, "AcquireMissingScope", "Canceled; input unchanged.", started);
        }
        catch (Exception error)
        {
            StatusMessage = "Acquisition failed: " + error.Message;
            return FailResult(correlation, "AcquireMissingScope", "Failed: " + error.Message, started);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Exports a self-describing report: rule, layer, representation,
    /// capture identity, counting semantics, coverage, findings with
    /// witnesses, and diagnostics. Never exports an empty success for a
    /// partial analysis without saying so.
    /// </summary>
    public async Task<LaneAOperationResult> ExportReportAsync(
        string path,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        if (_sourceScene is null || _lastAnalysis is null)
        {
            return FailResult(
                correlation, "ExportReport",
                "Nothing to export: run analysis against an attached input first.", started);
        }
        DesignScene scene = _sourceScene;
        CrossingAnalysis analysis = _lastAnalysis;
        var text = new StringBuilder();
        text.Append(LaneAArtifacts.FormatHeader("T01 Crossing review report", started));
        text.AppendLine($"Rule: {analysis.RuleId}");
        text.AppendLine($"Layer: {analysis.Layer.Value}");
        text.AppendLine($"Representation: {_representation}");
        text.AppendLine($"Capture: {scene.Identity.CaptureId} (acquired {scene.Identity.CapturedAt:O})");
        text.AppendLine($"Provenance: {SourceProvenance}");
        text.AppendLine($"Subject net filter: {(string.IsNullOrWhiteSpace(_subjectNet) ? "(none)" : _subjectNet)}");
        text.AppendLine("Counting semantics: InterferingNetCount counts distinct nets; " +
            "InterferingObjectCount counts distinct objects; CrossingLocationCount counts " +
            "locations; UnassignedObjectCount counts objects without a net. These are " +
            "analysis events, not route crossings.");
        text.AppendLine($"Interfering nets: {analysis.InterferingNetCount}");
        text.AppendLine($"Interfering objects: {analysis.InterferingObjectCount}");
        text.AppendLine($"Crossing locations: {analysis.CrossingLocationCount}");
        text.AppendLine($"Unassigned objects: {analysis.UnassignedObjectCount}");
        text.AppendLine($"Findings listed: {Findings.Length} (truncated: {analysis.FindingsTruncated})");
        text.AppendLine($"Complete for requested representation: {analysis.CompleteForRequestedRepresentation}");
        text.AppendLine($"Coverage: {CoverageSummary}");
        foreach (CrossingFindingRow row in Findings)
        {
            text.AppendLine($"- {row.Describe()}");
        }
        if (!analysis.Diagnostics.IsDefaultOrEmpty)
        {
            text.AppendLine("Diagnostics:");
            foreach (string diagnostic in analysis.Diagnostics)
            {
                text.AppendLine($"  - {diagnostic}");
            }
        }
        try
        {
            (string written, string sha256) = await LaneAArtifacts.WriteTextAsync(
                path, text.ToString(), overwrite, cancellationToken);
            StatusMessage = $"Report exported to {written} (sha256 {sha256}).";
            return new LaneAOperationResult(
                correlation, "T01", "ExportReport", "ExportReport",
                scene.Identity.CaptureId, SourceProvenance, true,
                $"Report written to {written}.",
                started, DateTimeOffset.UtcNow,
                ImmutableArray<string>.Empty, written, sha256);
        }
        catch (Exception error)
        {
            StatusMessage = "Export failed: " + error.Message;
            return FailResult(correlation, "ExportReport", "Failed: " + error.Message, started);
        }
    }

    private LaneAOperationResult FailResult(
        Guid correlation, string action, string reason, DateTimeOffset started) =>
        new(correlation, "T01", action, action + " (rejected)",
            _sourceScene?.Identity.CaptureId, SourceProvenance, false, reason,
            started, DateTimeOffset.UtcNow,
            ImmutableArray<string>.Empty, null, null);

    private ImmutableArray<CrossingFindingRow> ApplyFilter(ImmutableArray<CrossingFindingRow> rows)
    {
        if (string.IsNullOrWhiteSpace(_filterText))
        {
            return rows;
        }
        string needle = _filterText.Trim();
        return rows
            .Where(row =>
                row.NetName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                row.Layer.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
    }
}

/// <summary>
/// One listed crossing finding with explicit witness evidence.
/// </summary>
public sealed record CrossingFindingRow(
    int Index,
    string NetName,
    string Layer,
    int WitnessCount,
    DesignPoint? FirstWitness,
    bool RepresentationUncertain,
    SceneObjectReference? Object)
{
    public static CrossingFindingRow From(int index, CrossingFinding finding)
    {
        DesignPoint? first = finding.Witnesses.IsDefaultOrEmpty ? null : finding.Witnesses[0];
        return new CrossingFindingRow(
            index,
            string.IsNullOrWhiteSpace(finding.NetName) ? "(unassigned)" : finding.NetName,
            finding.Layer.Value,
            finding.Witnesses.IsDefault ? 0 : finding.Witnesses.Length,
            first,
            finding.RepresentationUncertain,
            finding.Object);
    }

    public DesignBounds BrowseBounds
    {
        get
        {
            DesignPoint witness = FirstWitness ?? DesignPoint.From(0m, 0m, LengthUnit.Mils);
            Length margin = Length.From(10m, LengthUnit.Mils);
            DesignPoint min = DesignPoint.From(
                witness.X - margin.Mils, witness.Y - margin.Mils, LengthUnit.Mils);
            DesignPoint max = DesignPoint.From(
                witness.X + margin.Mils, witness.Y + margin.Mils, LengthUnit.Mils);
            return new DesignBounds(min, max);
        }
    }

    public string Describe()
    {
        DesignPoint? first = FirstWitness;
        string witness = first is null
            ? "no witness point"
            : $"witness ({first.Value.X}, {first.Value.Y}) mil";
        string uncertain = RepresentationUncertain ? "; representation uncertain" : string.Empty;
        return $"[{Index}] net {NetName} on {Layer}: {WitnessCount} witness(es), {witness}{uncertain}.";
    }

    public static string Summarize(CrossingAnalysis analysis) =>
        analysis.IsClear && analysis.CompleteForRequestedRepresentation
            ? "Clear: no interfering objects for the requested layer and representation."
            : $"Nets {analysis.InterferingNetCount}, objects {analysis.InterferingObjectCount}, " +
              $"locations {analysis.CrossingLocationCount}, unassigned {analysis.UnassignedObjectCount}, " +
              $"truncated={analysis.FindingsTruncated}, complete={analysis.CompleteForRequestedRepresentation}.";
}

/// <summary>
/// Net-grouped view over finding rows; grouping never merges distinct nets.
/// </summary>
public sealed record CrossingFindingGroup(string NetName, ImmutableArray<CrossingFindingRow> Rows)
{
    public int Count => Rows.Length;

    public static ImmutableArray<CrossingFindingGroup> Group(ImmutableArray<CrossingFindingRow> rows) =>
        rows.GroupBy(row => row.NetName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CrossingFindingGroup(group.Key, group.ToImmutableArray()))
            .ToImmutableArray();
}
