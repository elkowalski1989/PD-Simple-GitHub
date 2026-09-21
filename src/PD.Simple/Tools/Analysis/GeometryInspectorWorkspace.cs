using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.Simple.Tools.Analysis;

/// <summary>
/// T02 Geometry inspector workflow over public Engine APIs. Search and
/// selection run through <see cref="SceneObjectBrowser"/>, which is local by
/// Engine contract: changing a filter or selecting a row never reads Allegro.
/// Typed details come from the attached scene's own collections and geometry
/// records (roles, fidelity, validity, error bounds, provenance); contour
/// detail is an explicit opt-in acquisition, never a hidden native read.
/// Pairwise measurements use <see cref="GeometryKernel"/> and refuse
/// unsupported fallback geometry instead of silently substituting bounds.
/// </summary>
public sealed class GeometryInspectorWorkspace : LaneAWorkspaceBase
{
    public const int DefaultMaximumSearchRows = 200;
    public const int MaximumMaximumSearchRows = 5000;

    private DesignScene? _scene;
    private string _sourceKind = "none";
    private SceneObjectBrowser? _browser;
    private ObjectFamily _family = ObjectFamily.Traces;
    private string _searchText = string.Empty;
    private int _maximumSearchRows = DefaultMaximumSearchRows;
    private InspectorHit? _selectedHit;
    private InspectionView? _inspection;
    private CopperDetail? _copperDetail;
    private string _coverageSummary = "No input attached.";
    private bool _contoursLoaded;
    private InspectorHit? _measureA;
    private InspectorHit? _measureB;
    private string _measureSummary = "No measurement.";
    private bool _recipeDetails;
    private InspectorRecipe? _currentRecipe;
    private string _vertexPageInfo = "No vertices sampled.";

    public bool HasScene => _scene is not null;

    public string SourceKind => _sourceKind;

    public Guid? SourceCaptureId => _scene?.Identity.CaptureId;

    public string SourceProvenance =>
        _scene is null
            ? "none"
            : $"{_scene.Identity.Provenance.Provider} {_scene.Identity.Provenance.Version} " +
              $"({_scene.Identity.Provenance.OriginalAcquisition}, offline={_scene.Identity.Provenance.IsOffline})";

    public string CoverageSummary
    {
        get => _coverageSummary;
        private set => SetField(ref _coverageSummary, value);
    }

    public bool ContoursLoaded
    {
        get => _contoursLoaded;
        private set
        {
            if (SetField(ref _contoursLoaded, value))
            {
                Raise(nameof(DetailBlockedReason));
            }
        }
    }

    public ObjectFamily Family
    {
        get => _family;
        set
        {
            if (SetField(ref _family, value))
            {
                Raise(nameof(FamilyCoverageSummary));
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set => SetField(ref _searchText, value ?? string.Empty);
    }

    public int MaximumSearchRows
    {
        get => _maximumSearchRows;
        set => SetField(ref _maximumSearchRows, Math.Clamp(value, 1, MaximumMaximumSearchRows));
    }

    public ImmutableArray<InspectorHit> Hits { get; private set; } =
        ImmutableArray<InspectorHit>.Empty;

    public int TotalMatches { get; private set; }

    public bool ResultsLimited { get; private set; }

    public string FamilyCoverageSummary =>
        _browser is null
            ? "No input attached."
            : DescribeCoverage(_browser.CoverageFor(_family));

    public InspectorHit? SelectedHit
    {
        get => _selectedHit;
        private set
        {
            if (SetField(ref _selectedHit, value))
            {
                Raise(nameof(DetailBlockedReason));
            }
        }
    }

    public InspectionView? Inspection
    {
        get => _inspection;
        private set => SetField(ref _inspection, value);
    }

    public CopperDetail? CopperDetail
    {
        get => _copperDetail;
        private set => SetField(ref _copperDetail, value);
    }

    public string DetailBlockedReason
    {
        get
        {
            ToolReadiness readiness = DescribeDetailReadiness();
            return readiness.IsReady ? "Detailed geometry is available." : readiness.Reason + " Next: " + readiness.NextStep;
        }
    }

    public InspectorHit? MeasureA
    {
        get => _measureA;
        private set => SetField(ref _measureA, value);
    }

    public InspectorHit? MeasureB
    {
        get => _measureB;
        private set => SetField(ref _measureB, value);
    }

    public string InspectionText => Inspection?.Describe() ?? "No object selected.";

    public string CopperDetailText =>
        CopperDetail?.Describe() ?? "No per-layer copper detail for the selection.";

    public string StackupSummary => DescribeStackup();

    public bool RecipeDetails
    {
        get => _recipeDetails;
        set
        {
            if (SetField(ref _recipeDetails, value))
            {
                RefreshRecipe();
            }
        }
    }

    public InspectorRecipe? CurrentRecipe
    {
        get => _currentRecipe;
        private set => SetField(ref _currentRecipe, value);
    }

    public string RecipeText =>
        CurrentRecipe is null
            ? "No recipe: select an object first."
            : $"{CurrentRecipe.Title} (requires: {CurrentRecipe.Requirement}){Environment.NewLine}{CurrentRecipe.Code}";

    public string HighlightRequestText =>
        SelectedHit?.Reference is null
            ? "No highlight target: select an object first."
            : $"Highlight/zoom request for {SelectedHit.Family} '{SelectedHit.Name}' " +
              $"(object {SelectedHit.Reference.Value.ObjectId.Value}, capture {SelectedHit.Reference.Value.CaptureId}). " +
              "The shared Workbench executes native highlight/zoom; this workspace only supplies the typed reference.";

    public ImmutableArray<DesignPoint> VertexPage { get; private set; } =
        ImmutableArray<DesignPoint>.Empty;

    public string VertexPageInfo
    {
        get => _vertexPageInfo;
        private set => SetField(ref _vertexPageInfo, value);
    }

    public string MeasureSummary
    {
        get => _measureSummary;
        private set => SetField(ref _measureSummary, value);
    }

    public void AttachScene(DesignScene scene, string kind)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        _scene = scene;
        _sourceKind = kind.Trim();
        _browser = new SceneObjectBrowser(scene);
        Hits = ImmutableArray<InspectorHit>.Empty;
        TotalMatches = 0;
        ResultsLimited = false;
        SelectedHit = null;
        Inspection = null;
        CopperDetail = null;
        MeasureA = null;
        MeasureB = null;
        MeasureSummary = "No measurement.";
        ContoursLoaded = scene.Query.IncludeContours;
        RefreshCoverage();
        RefreshRecipe();
        Raise(nameof(HasScene));
        Raise(nameof(SourceKind));
        Raise(nameof(SourceCaptureId));
        Raise(nameof(SourceProvenance));
        Raise(nameof(Hits));
        Raise(nameof(TotalMatches));
        Raise(nameof(ResultsLimited));
        Raise(nameof(FamilyCoverageSummary));
        Raise(nameof(DetailBlockedReason));
        Raise(nameof(InspectionText));
        Raise(nameof(CopperDetailText));
        Raise(nameof(StackupSummary));
        Raise(nameof(RecipeText));
        Raise(nameof(HighlightRequestText));
        Raise(nameof(VertexPage));
        StatusMessage = $"Attached {_sourceKind} capture {scene.Identity.CaptureId}. " +
            (ContoursLoaded ? "Contour detail is present." : "Contour detail is not loaded; use Load detailed geometry.");
    }

    public void ClearScene()
    {
        _scene = null;
        _sourceKind = "none";
        _browser = null;
        Hits = ImmutableArray<InspectorHit>.Empty;
        TotalMatches = 0;
        ResultsLimited = false;
        SelectedHit = null;
        Inspection = null;
        CopperDetail = null;
        MeasureA = null;
        MeasureB = null;
        MeasureSummary = "No measurement.";
        ContoursLoaded = false;
        RefreshCoverage();
        RefreshRecipe();
        Raise(nameof(HasScene));
        Raise(nameof(SourceKind));
        Raise(nameof(SourceCaptureId));
        Raise(nameof(SourceProvenance));
        Raise(nameof(Hits));
        Raise(nameof(TotalMatches));
        Raise(nameof(ResultsLimited));
        Raise(nameof(FamilyCoverageSummary));
        Raise(nameof(InspectionText));
        Raise(nameof(CopperDetailText));
        Raise(nameof(StackupSummary));
        Raise(nameof(RecipeText));
        Raise(nameof(HighlightRequestText));
        Raise(nameof(VertexPage));
        StatusMessage = "Input cleared.";
    }

    private void RefreshCoverage()
    {
        if (_scene is null)
        {
            CoverageSummary = "No input attached.";
            return;
        }
        int complete = 0;
        int total = 0;
        var notes = new List<string>();
        foreach (DataFamily family in Enum.GetValues<DataFamily>())
        {
            FamilyCoverage coverage = _scene.Coverage[family];
            total++;
            if (coverage.IsComplete)
            {
                complete++;
            }
            else if (coverage.Availability != DataAvailability.NotRequested)
            {
                notes.Add($"{family}: {coverage.Availability}/{coverage.Completeness}.");
            }
        }
        CoverageSummary = $"Coverage: {complete}/{total} families complete. " + string.Join(" ", notes);
    }

    private static string DescribeCoverage(FamilyCoverage coverage) =>
        $"{coverage.Family}: availability {coverage.Availability}, completeness {coverage.Completeness}, " +
        $"fidelity {coverage.Fidelity}, truncated={coverage.Truncated}" +
        (coverage.Reasons.IsDefaultOrEmpty ? "." : ". " + string.Join(" ", coverage.Reasons));

    /// <summary>
    /// Bounded local search. Truncation and coverage are reported together:
    /// an empty result with unavailable coverage is not proof of absence.
    /// </summary>
    public Task<LaneAOperationResult> SearchAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        if (_scene is null || _browser is null)
        {
            return Task.FromResult(FailResult(correlation, "Search", "No input is attached.", started));
        }
        SceneObjectBrowser browser = _browser;
        ObjectFamily family = _family;
        string text = _searchText;
        int maxRows = _maximumSearchRows;
        IsBusy = true;
        StatusMessage = "Searching the attached capture…";
        return Task.Run(() =>
        {
            try
            {
                ObjectSearchResult result = browser.Search(family, text, maxRows, cancellationToken);
                var hits = ImmutableArray.CreateBuilder<InspectorHit>();
                int index = 0;
                foreach (ObjectEntry entry in result.Items)
                {
                    hits.Add(InspectorHit.From(index++, entry));
                }
                Hits = hits.ToImmutable();
                TotalMatches = result.TotalMatches;
                ResultsLimited = result.ResultsLimited;
                Raise(nameof(Hits));
                Raise(nameof(TotalMatches));
                Raise(nameof(ResultsLimited));
                StatusMessage = Hits.Length == 0 && result.Coverage.Availability == DataAvailability.Unavailable
                    ? "No rows and the family is unavailable: absence is not proven. Acquire the family first."
                    : $"Found {TotalMatches} match(es), showing {Hits.Length}" +
                      (ResultsLimited ? " (results limited; narrow the search)." : ".");
                return SuccessResult(
                    correlation, "Search", "Search", StatusMessage, started);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StatusMessage = "Search canceled.";
                return FailResult(correlation, "Search", "Canceled.", started);
            }
            catch (Exception error)
            {
                StatusMessage = "Search failed: " + error.Message;
                return FailResult(correlation, "Search", "Failed: " + error.Message, started);
            }
            finally
            {
                IsBusy = false;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Selects one listed hit and inspects it locally. Resolves the typed
    /// object from the attached scene without any native read.
    /// </summary>
    public LaneAOperationResult Select(InspectorHit? hit)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        if (_scene is null || _browser is null)
        {
            return FailResult(correlation, "Select", "No input is attached.", started);
        }
        if (hit is null)
        {
            return FailResult(correlation, "Select", "No row is selected.", started);
        }
        try
        {
            if (hit.Reference is null)
            {
                return FailResult(
                    correlation, "Select",
                    "The selected row carries no capture reference.", started);
            }
            ObjectEntry? entry = _browser.Find(hit.Reference.Value);
            if (entry is null)
            {
                return FailResult(
                    correlation, "Select",
                    "The browser could not resolve the selected reference in this capture.", started);
            }
            ObjectInspection inspection = _browser.Inspect(entry);
            SelectedHit = hit;
            Inspection = InspectionView.From(inspection);
            CopperDetail = TryDescribeCopper(entry);
            RefreshRecipe();
            VertexPage = ImmutableArray<DesignPoint>.Empty;
            VertexPageInfo = "No vertices sampled.";
            Raise(nameof(Inspection));
            Raise(nameof(InspectionText));
            Raise(nameof(CopperDetail));
            Raise(nameof(CopperDetailText));
            Raise(nameof(HighlightRequestText));
            Raise(nameof(VertexPage));
            StatusMessage = $"Inspecting {entry.Family} '{entry.Name}'.";
            return SuccessResult(correlation, "Select", "Select", StatusMessage, started);
        }
        catch (Exception error)
        {
            StatusMessage = "Selection failed: " + error.Message;
            return FailResult(correlation, "Select", "Failed: " + error.Message, started);
        }
    }

    private CopperDetail? TryDescribeCopper(ObjectEntry entry)
    {
        if (_scene is null || entry.Reference is null)
        {
            return null;
        }
        SceneObjectId id = entry.Reference.Value.ObjectId;
        foreach (CopperObject copper in _scene.Copper.Items)
        {
            if (copper.Id == id)
            {
                return CopperDetail.From(copper);
            }
        }
        return null;
    }

    public ToolReadiness DescribeDetailReadiness()
    {
        if (_scene is null)
        {
            return ToolReadiness.Blocked("No input is attached.", "Attach a captured scene first.");
        }
        if (SelectedHit is null)
        {
            return ToolReadiness.Blocked("No object is selected.", "Search, then select a row.");
        }
        if (!_contoursLoaded)
        {
            return ToolReadiness.Blocked(
                "Contour detail is not loaded for this capture.",
                "Use Load detailed geometry to acquire contours for an explicit scope first.");
        }
        if (CopperDetail is null)
        {
            return ToolReadiness.Blocked(
                "The selected object has no per-layer copper detail in this capture.",
                "Select a trace, via, shape, or pin-copper row; nets and components expose fields only.");
        }
        return ToolReadiness.Ready();
    }

    /// <summary>
    /// Explicit contour acquisition proposal for the selected object's
    /// bounds. Returned as data; the host executes it and reattaches.
    /// </summary>
    public SceneQuery? DescribeDetailAcquisition()
    {
        if (_scene is null)
        {
            return null;
        }
        DesignBounds? region = CopperDetail is not null
            ? CopperDetail.Bounds
            : _scene.Query.Region;
        return new SceneQuery
        {
            Families = ImmutableArray.Create(DataFamily.Copper),
            IncludeContours = true,
            MaximumObjects = _maximumSearchRows,
            Region = region,
        };
    }

    public LaneAOperationResult MarkContoursLoaded()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        if (_scene is null)
        {
            return FailResult(correlation, "LoadDetailedGeometry", "No input is attached.", started);
        }
        // The host reattaches the fresh contour-bearing scene; this records
        // that the currently attached capture was acquired with contours.
        // It never upgrades a bounds-only scene by declaration alone: the
        // caller passes the fresh scene back through AttachScene in practice.
        if (!_scene.Query.IncludeContours)
        {
            return FailResult(
                correlation, "LoadDetailedGeometry",
                "The attached capture was acquired without contours; reacquire with IncludeContours first.",
                started);
        }
        ContoursLoaded = true;
        StatusMessage = "Detailed geometry is marked loaded for the attached capture.";
        return SuccessResult(correlation, "LoadDetailedGeometry", "LoadDetailedGeometry", StatusMessage, started);
    }

    /// <summary>
    /// Copies the public Engine recipe for the selected object. Recipe names
    /// are escaped data, never executable fragments.
    /// </summary>
    public InspectorRecipe? DescribeRecipe(bool includeDetails)
    {
        if (_browser is null || SelectedHit?.Entry is null)
        {
            return null;
        }
        EngineRecipe recipe = SceneRecipes.Inspect(SelectedHit.Entry, includeDetails);
        return new InspectorRecipe(recipe.Title, recipe.Code, recipe.Requirement);
    }

    public void SetMeasureEndpoint(bool first, InspectorHit? hit)
    {
        if (first)
        {
            MeasureA = hit;
        }
        else
        {
            MeasureB = hit;
        }
        MeasureSummary = MeasureA is null || MeasureB is null
            ? "Select two measured objects to compute a qualified distance."
            : $"Endpoints: {MeasureA.Name} and {MeasureB.Name}. Use Measure selected pair.";
    }

    /// <summary>
    /// Qualified pairwise distance and intersection between two selected
    /// copper objects' centerlines. Unsupported or indeterminate geometry is
    /// refused with a reason; bounds are never substituted silently.
    /// </summary>
    public LaneAOperationResult MeasureSelectedPair()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        if (_scene is null || _browser is null)
        {
            return FailResult(correlation, "MeasurePair", "No input is attached.", started);
        }
        if (MeasureA?.Entry is null || MeasureB?.Entry is null)
        {
            return FailResult(correlation, "MeasurePair", "Two measured objects are required.", started);
        }
        GeometryShape? shapeA = FindCenterline(MeasureA.Entry);
        GeometryShape? shapeB = FindCenterline(MeasureB.Entry);
        if (shapeA is null || shapeB is null)
        {
            return FailResult(
                correlation, "MeasurePair",
                "Both endpoints must resolve to copper centerlines in this capture.", started);
        }
        try
        {
            var kernel = new GeometryKernel();
            DistanceResult distance = kernel.Distance(shapeA, shapeB);
            IntersectionResult intersection = kernel.Intersect(shapeA, shapeB);
            if (intersection.Qualification == RelationQualification.Indeterminate ||
                intersection.Relation == GeometricRelation.Indeterminate)
            {
                MeasureSummary = "Measurement indeterminate for this geometry pair; no distance is claimed.";
                return FailResult(
                    correlation, "MeasurePair",
                    "Indeterminate qualification: refusing to report a distance.", started);
            }
            var summary =
                $"Distance {distance.Distance.Mils} mil " +
                $"({distance.Distance.In(LengthUnit.Millimeters)} mm, error bound {distance.ErrorBound.Mils} mil), " +
                $"relation {intersection.Relation} qualified {intersection.Qualification}.";
            MeasureSummary = summary;
            StatusMessage = summary;
            return SuccessResult(correlation, "MeasurePair", "MeasurePair", summary, started);
        }
        catch (Exception error)
        {
            MeasureSummary = "Measurement failed: " + error.Message;
            return FailResult(correlation, "MeasurePair", "Failed: " + error.Message, started);
        }
    }

    private GeometryShape? FindCenterline(ObjectEntry entry)
    {
        if (_scene is null || entry.Reference is null)
        {
            return null;
        }
        SceneObjectId id = entry.Reference.Value.ObjectId;
        foreach (CopperObject copper in _scene.Copper.Items)
        {
            if (copper.Id == id)
            {
                return copper.Centerline;
            }
        }
        return null;
    }

    private void RefreshRecipe()
    {
        CurrentRecipe = SelectedHit?.Entry is null
            ? null
            : DescribeRecipe(includeDetails: _recipeDetails);
        Raise(nameof(RecipeText));
    }

    private string DescribeStackup()
    {
        if (_scene?.Data.Stackup is null)
        {
            return "Stackup: unavailable in this capture.";
        }
        StackupModel stackup = _scene.Data.Stackup;
        return $"Stackup region '{stackup.Region}': {stackup.Layers.Length} layer(s), " +
            $"availability {stackup.Availability}.";
    }

    /// <summary>
    /// Samples one explicit page of the selected copper centerline's
    /// vertices. Only the selected shape is sampled; requesting details must
    /// not convert the entire board.
    /// </summary>
    public LaneAOperationResult SampleSelectedVertices(int pageIndex, int pageSize = LaneAPaging.DefaultPageSize)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        if (SelectedHit?.Entry is null)
        {
            return FailResult(correlation, "SampleVertices", "No object is selected.", started);
        }
        GeometryShape? shape = FindCenterline(SelectedHit.Entry);
        if (shape is null)
        {
            return FailResult(
                correlation, "SampleVertices",
                "The selection has no sampled centerline in this capture.", started);
        }
        try
        {
            LaneAPage<DesignPoint> page = SampleVerticesPage(shape, pageIndex, pageSize);
            VertexPage = page.Items;
            VertexPageInfo = page.TotalCount == 0
                ? "The shape sampled to no vertices."
                : $"Page {page.PageIndex + 1}/{page.PageCount}: showing {page.Items.Length} of {page.TotalCount} sampled vertices.";
            Raise(nameof(VertexPage));
            StatusMessage = VertexPageInfo;
            return SuccessResult(correlation, "SampleVertices", "SampleVertices", VertexPageInfo, started);
        }
        catch (Exception error)
        {
            return FailResult(correlation, "SampleVertices", "Failed: " + error.Message, started);
        }
    }

    /// <summary>
    /// One explicit page of sampled vertices for a shape. Only the requested
    /// shape is sampled; the rest of the board is never converted.
    /// </summary>
    public static LaneAPage<DesignPoint> SampleVerticesPage(
        GeometryShape shape, int pageIndex, int pageSize = LaneAPaging.DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var kernel = new GeometryKernel();
        ImmutableArray<DesignPoint> sampled = kernel.Sample(shape);
        return LaneAPaging.Page(sampled, pageIndex, pageSize);
    }

    /// <summary>
    /// Exports the selected inspection facts: fields, copper detail,
    /// coverage, provenance, and recipe requirement. Atomic write with hash.
    /// </summary>
    public async Task<LaneAOperationResult> ExportFactsAsync(
        string path,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid correlation = Guid.NewGuid();
        if (_scene is null || Inspection is null)
        {
            return FailResult(
                correlation, "ExportFacts",
                "Nothing to export: search and select an object first.", started);
        }
        var text = new System.Text.StringBuilder();
        text.Append(LaneAArtifacts.FormatHeader("T02 Geometry inspector facts", started));
        text.AppendLine($"Capture: {_scene.Identity.CaptureId} (acquired {_scene.Identity.CapturedAt:O})");
        text.AppendLine($"Provenance: {SourceProvenance}");
        text.AppendLine($"Coverage: {CoverageSummary}");
        text.AppendLine(Inspection.Describe());
        if (CopperDetail is not null)
        {
            text.AppendLine(CopperDetail.Describe());
        }
        InspectorRecipe? recipe = SelectedHit?.Entry is null
            ? null
            : DescribeRecipe(includeDetails: ContoursLoaded);
        if (recipe is not null)
        {
            text.AppendLine($"Recipe: {recipe.Title} (requires: {recipe.Requirement})");
            text.AppendLine(recipe.Code);
        }
        try
        {
            (string written, string sha256) = await LaneAArtifacts.WriteTextAsync(
                path, text.ToString(), overwrite, cancellationToken);
            StatusMessage = $"Facts exported to {written} (sha256 {sha256}).";
            return new LaneAOperationResult(
                correlation, "T02", "ExportFacts", "ExportFacts",
                _scene.Identity.CaptureId, SourceProvenance, true,
                $"Facts written to {written}.",
                started, DateTimeOffset.UtcNow,
                ImmutableArray<string>.Empty, written, sha256);
        }
        catch (Exception error)
        {
            StatusMessage = "Export failed: " + error.Message;
            return FailResult(correlation, "ExportFacts", "Failed: " + error.Message, started);
        }
    }

    private LaneAOperationResult SuccessResult(
        Guid correlation, string requested, string executed, string outcome, DateTimeOffset started) =>
        new(correlation, "T02", requested, executed,
            _scene?.Identity.CaptureId, SourceProvenance, true, outcome,
            started, DateTimeOffset.UtcNow,
            ImmutableArray<string>.Empty, null, null);

    private LaneAOperationResult FailResult(
        Guid correlation, string action, string reason, DateTimeOffset started) =>
        new(correlation, "T02", action, action + " (rejected)",
            _scene?.Identity.CaptureId, SourceProvenance, false, reason,
            started, DateTimeOffset.UtcNow,
            ImmutableArray<string>.Empty, null, null);
}

/// <summary>
/// One listed search hit with its capture-scoped reference.
/// </summary>
public sealed record InspectorHit(
    int Index,
    string Name,
    ObjectFamily Family,
    bool HasReference,
    SceneObjectReference? Reference,
    ObjectEntry? Entry)
{
    public static InspectorHit From(int index, ObjectEntry entry) =>
        new(index, entry.Name, entry.Family, entry.Reference is not null, entry.Reference, entry);
}

/// <summary>
/// Field-level inspection of one selected object.
/// </summary>
public sealed record InspectorField(string Name, string Value);

public sealed record InspectionView(
    string Name,
    ObjectFamily Family,
    ImmutableArray<InspectorField> Fields,
    ImmutableArray<string> Notes)
{
    public static InspectionView From(ObjectInspection inspection) =>
        new(
            inspection.Entry.Name,
            inspection.Entry.Family,
            inspection.Fields.Select(field => new InspectorField(field.Name, field.Value)).ToImmutableArray(),
            inspection.Notes.IsDefault ? ImmutableArray<string>.Empty : inspection.Notes);

    public string Describe()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Object: {Family} '{Name}'.");
        foreach (InspectorField field in Fields)
        {
            text.AppendLine($"  {field.Name}: {field.Value}");
        }
        foreach (string note in Notes)
        {
            text.AppendLine($"  note: {note}");
        }
        return text.ToString();
    }
}

/// <summary>
/// Typed per-layer copper detail: bounds, width, net, role records with
/// fidelity/validity/error/provenance, and surfaces. Bounding boxes are
/// labeled as previews, never as copper truth.
/// </summary>
public sealed record CopperDetail(
    string Kind,
    string Net,
    string? Layer,
    DesignBounds Bounds,
    string? WidthMils,
    string CenterlineKind,
    DesignBounds? CenterlineBoundsPreview,
    ImmutableArray<SurfaceDetail> Surfaces,
    ImmutableArray<RoleDetail> Roles,
    string PinSummary,
    ImmutableArray<string> Diagnostics)
{
    public static CopperDetail From(CopperObject copper)
    {
        var surfaces = ImmutableArray.CreateBuilder<SurfaceDetail>();
        foreach (CopperSurface surface in copper.Surfaces)
        {
            surfaces.Add(new SurfaceDetail(surface.Layer.Value, surface.Kind, surface.Fidelity));
        }
        var roles = ImmutableArray.CreateBuilder<RoleDetail>();
        if (copper.Geometry?.Geometry is not null)
        {
            foreach (LayerGeometryRecord layerRecord in copper.Geometry.Geometry)
            {
                GeometryRecord record = layerRecord.Geometry;
                roles.Add(new RoleDetail(
                    layerRecord.Layer.Value,
                    record.Role,
                    record.Origin,
                    record.Fidelity,
                    record.Validity,
                    record.ErrorBound.Mils,
                    record.Source,
                    record.Diagnostics.IsDefault
                        ? ImmutableArray<string>.Empty
                        : record.Diagnostics));
            }
        }
        return new CopperDetail(
            copper.Kind.ToString(),
            string.IsNullOrWhiteSpace(copper.NetName) ? "(unassigned)" : copper.NetName,
            copper.Layer?.Value,
            copper.Bounds,
            copper.Width?.Mils.ToString(System.Globalization.CultureInfo.InvariantCulture),
            copper.Centerline?.GetType().Name ?? "(no centerline)",
            copper.Centerline?.Bounds,
            surfaces.ToImmutable(),
            roles.ToImmutable(),
            copper.Pin is null || copper.Pin.ComponentRefdes is null
                ? "(no pin copper)"
                : $"{copper.Pin.ComponentRefdes}.{copper.Pin.Number} on {copper.Pin.Padstack}",
            copper.Diagnostics.IsDefault
                ? ImmutableArray<string>.Empty
                : copper.Diagnostics);
    }

    public string Describe()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Copper: {Kind}, net {Net}, layer {Layer ?? "(spans layers)"}, " +
            $"width {WidthMils ?? "(none)"} mil, centerline {CenterlineKind}.");
        text.AppendLine($"  Bounds (preview, not copper truth): {Bounds}.");
        foreach (SurfaceDetail surface in Surfaces)
        {
            text.AppendLine($"  Surface {surface.Layer} {surface.Kind} fidelity {surface.Fidelity}.");
        }
        foreach (RoleDetail role in Roles)
        {
            text.AppendLine($"  {role.Layer}: role {role.Role}, origin {role.Origin}, " +
                $"fidelity {role.Fidelity}, validity {role.Validity}, error bound {role.ErrorBoundMils} mil, " +
                $"source {role.Source?.ToString() ?? "(unknown)"}.");
            foreach (string diagnostic in role.Diagnostics)
            {
                text.AppendLine($"    - {diagnostic}");
            }
        }
        text.AppendLine($"  Pin: {PinSummary}.");
        return text.ToString();
    }
}

public sealed record SurfaceDetail(string Layer, string Kind, GeometryFidelity Fidelity);

public sealed record RoleDetail(
    string Layer,
    GeometryRole Role,
    GeometryOrigin Origin,
    GeometryFidelity Fidelity,
    GeometryValidity Validity,
    decimal ErrorBoundMils,
    NativeGeometrySource? Source,
    ImmutableArray<string> Diagnostics);

public sealed record InspectorRecipe(string Title, string Code, string Requirement);
