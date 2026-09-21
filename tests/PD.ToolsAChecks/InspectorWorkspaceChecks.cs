using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Exploration;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.Simple.Tools.Analysis;

namespace PD.ToolsAChecks;

/// <summary>
/// T02 offline checks over the inspector workspace: local search/select,
/// typed copper detail, qualified measurement, explicit contour loading,
/// vertex paging, recipes, and fact export.
/// </summary>
internal static class InspectorWorkspaceChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        checks += CheckLocalTransformsAndUnits();
        checks += await CheckSearchAndSelectAsync();
        checks += CheckQualifiedMeasurement();
        checks += await CheckContoursAndPagingAsync();
        checks += await CheckRecipeAndExportAsync();
        checks += CheckExampleBoard();
        Console.WriteLine($"PASS: {checks} T02 geometry inspector checks.");
        return checks;
    }

    private static int CheckLocalTransformsAndUnits()
    {
        // Mil/mm/inch equivalence for local/board coordinates.
        Length inch = Length.FromInches(1m);
        LaneACheck.Require(inch.Mils == 1000m, "One inch did not equal 1000 mil.");
        LaneACheck.Require(
            inch.In(LengthUnit.Millimeters) == 25.4m,
            "One inch did not convert to 25.4 mm.");
        Length mm = Length.From(25.4m, LengthUnit.Millimeters);
        LaneACheck.Require(mm.Mils == 1000m, "25.4 mm did not equal 1000 mil.");

        // Negative coordinates and mirrored deltas behave.
        DesignPoint neg = LaneACheck.Mil(-300m, -400m);
        DesignPoint origin = LaneACheck.Mil(0m, 0m);
        LaneACheck.Require(
            neg.DistanceTo(origin).Mils == 500m,
            "Negative-coordinate distance is not the expected 500 mil.");

        // Per-layer via geometry shape: an arc keeps analytic center data.
        var arc = new ArcGeometry(
            LaneACheck.Mil(10m, 0m), LaneACheck.Mil(0m, 10m), LaneACheck.Mil(0m, 0m),
            false, false, Length.From(0m, LengthUnit.Mils));
        LaneACheck.Require(
            Math.Abs(arc.Radius - 10.0) <= 0.001,
            $"Arc radius {arc.Radius} is not the expected 10 mil.");
        LaneACheck.Require(
            arc.Center == LaneACheck.Mil(0m, 0m),
            "Arc center moved.");
        return 6;
    }

    private static async Task<int> CheckSearchAndSelectAsync()
    {
        var workspace = new GeometryInspectorWorkspace();
        LaneAOperationResult noInput = await workspace.SearchAsync();
        LaneACheck.Require(!noInput.IsSuccess, "Search succeeded with no input.");

        workspace.AttachScene(LaneACheck.CrossingFixture(), "fixture");
        LaneACheck.Require(workspace.HasScene, "Fixture scene did not attach.");
        LaneACheck.Require(
            workspace.CoverageSummary.Contains("3/16", StringComparison.Ordinal),
            "Coverage summary is not explicit: " + workspace.CoverageSummary);

        workspace.Family = ObjectFamily.Traces;
        workspace.SearchText = string.Empty;
        workspace.MaximumSearchRows = 100;
        LaneAOperationResult searched = await workspace.SearchAsync();
        LaneACheck.Require(searched.IsSuccess, "Trace search failed: " + searched.Outcome);
        LaneACheck.Require(workspace.Hits.Length > 0, "Trace search found no rows.");
        LaneACheck.Require(!workspace.ResultsLimited, "Unbounded search reported limiting.");
        LaneACheck.Require(
            workspace.TotalMatches == workspace.Hits.Length,
            "Total matches disagree with listed rows.");

        // Bounded search reports truncation instead of silently dropping rows.
        workspace.MaximumSearchRows = 1;
        LaneAOperationResult limited = await workspace.SearchAsync();
        LaneACheck.Require(limited.IsSuccess, "Bounded search failed.");
        LaneACheck.Require(
            workspace.ResultsLimited && workspace.Hits.Length == 1 && workspace.TotalMatches > 1,
            "Bounded search did not report its truncation.");
        workspace.MaximumSearchRows = 100;
        await workspace.SearchAsync();

        // Selection resolves typed copper with per-layer detail.
        InspectorHit first = workspace.Hits[0];
        LaneAOperationResult selected = workspace.Select(first);
        LaneACheck.Require(selected.IsSuccess, "Selection failed: " + selected.Outcome);
        LaneACheck.Require(workspace.Inspection is not null, "No inspection was produced.");
        LaneACheck.Require(
            workspace.Inspection!.Fields.Length > 0,
            "Inspection carries no typed fields.");
        LaneACheck.Require(
            workspace.CopperDetail is not null,
            "Trace selection produced no copper detail.");
        LaneACheck.Require(
            workspace.CopperDetail!.Surfaces.Length == 0,
            "Fixture traces carry no surfaces, yet detail lists some.");
        LaneACheck.Require(
            workspace.CopperDetail.CenterlineKind.Contains("Line", StringComparison.Ordinal),
            "Trace centerline kind is not reported.");
        LaneACheck.Require(
            workspace.CopperDetailText.Contains(workspace.CopperDetail!.Net, StringComparison.Ordinal),
            "Copper detail text does not name the net.");
        LaneACheck.Require(
            workspace.HighlightRequestText.Contains("Highlight/zoom request", StringComparison.Ordinal),
            "No typed highlight request is derived from the selection.");

        // Unresolvable references stay explicit.
        var orphan = new InspectorHit(999, "orphan", ObjectFamily.Traces, false, null, null);
        LaneAOperationResult orphanResult = workspace.Select(orphan);
        LaneACheck.Require(!orphanResult.IsSuccess, "Selection succeeded for a reference-less row.");
        return 14;
    }

    private static int CheckQualifiedMeasurement()
    {
        var kernel = new GeometryKernel();
        // Parallel lines: qualified distance with witnesses and error bound.
        var lower = LaneACheck.MilLine(0m, 0m, 100m, 0m);
        var upper = LaneACheck.MilLine(0m, 10m, 100m, 10m);
        DistanceResult gap = kernel.Distance(lower, upper);
        LaneACheck.Require(gap.Distance.Mils == 10m, "Gap is not the expected 10 mil.");
        LaneACheck.Require(gap.ErrorBound.Mils >= 0m, "Error bound went negative.");
        LaneACheck.Require(
            gap.FirstWitness.DistanceTo(gap.SecondWitness).Mils == 10m,
            "Distance witnesses do not span the gap.");

        // Arc-line intersection stays honest: the kernel reports
        // Indeterminate rather than substituting bounds, while still
        // exposing its witness. Inspector pairwise measurement refuses
        // such pairs instead of claiming a distance.
        var arc = new ArcGeometry(
            LaneACheck.Mil(10m, 0m), LaneACheck.Mil(0m, 10m), LaneACheck.Mil(0m, 0m),
            false, false, Length.From(0m, LengthUnit.Mils));
        var crossing = LaneACheck.MilLine(-5m, 5m, 15m, 5m);
        IntersectionResult arcHit = kernel.Intersect(arc, crossing);
        LaneACheck.Require(
            arcHit.Relation == GeometricRelation.Indeterminate &&
            arcHit.Qualification == RelationQualification.Indeterminate,
            "Arc-line intersection did not stay explicitly indeterminate.");
        LaneACheck.Require(
            !arcHit.Witnesses.IsDefaultOrEmpty,
            "Indeterminate intersection dropped its witness point.");

        // Workspace-level pairwise measurement over the fixture.
        var workspace = new GeometryInspectorWorkspace();
        workspace.AttachScene(LaneACheck.CrossingFixture(), "fixture");
        workspace.Family = ObjectFamily.Traces;
        workspace.SearchText = string.Empty;
        workspace.MaximumSearchRows = 100;
        workspace.SearchAsync().GetAwaiter().GetResult();
        LaneACheck.Require(workspace.Hits.Length >= 2, "Fixture needs two traces for pairing.");
        workspace.SetMeasureEndpoint(true, workspace.Hits[0]);
        workspace.SetMeasureEndpoint(false, workspace.Hits[1]);
        LaneAOperationResult measured = workspace.MeasureSelectedPair();
        LaneACheck.Require(measured.IsSuccess, "Pairwise measurement failed: " + measured.Outcome);
        LaneACheck.Require(
            workspace.MeasureSummary.Contains("mil", StringComparison.OrdinalIgnoreCase) &&
            workspace.MeasureSummary.Contains("qualified", StringComparison.OrdinalIgnoreCase),
            "Measurement summary hides units or qualification: " + workspace.MeasureSummary);

        // Same-object pairing measures zero distance without claiming crossings.
        workspace.SetMeasureEndpoint(true, workspace.Hits[0]);
        workspace.SetMeasureEndpoint(false, workspace.Hits[0]);
        LaneAOperationResult self = workspace.MeasureSelectedPair();
        LaneACheck.Require(self.IsSuccess, "Self-pair measurement failed.");
        LaneACheck.Require(
            workspace.MeasureSummary.Contains("0", StringComparison.Ordinal),
            "Self-pair distance is not zero: " + workspace.MeasureSummary);
        return 10;
    }

    private static async Task<int> CheckContoursAndPagingAsync()
    {
        var workspace = new GeometryInspectorWorkspace();
        workspace.AttachScene(LaneACheck.CrossingFixture(), "fixture");
        LaneACheck.Require(!workspace.ContoursLoaded, "Contours claim to be loaded without opt-in.");
        workspace.Family = ObjectFamily.Traces;
        workspace.SearchText = string.Empty;
        await workspace.SearchAsync();
        workspace.Select(workspace.Hits[0]);
        LaneACheck.Require(
            workspace.DetailBlockedReason.Contains("Contour", StringComparison.OrdinalIgnoreCase),
            "The detail block does not name contours: " + workspace.DetailBlockedReason);
        SceneQuery? proposal = workspace.DescribeDetailAcquisition();
        LaneACheck.Require(
            proposal is not null && proposal.IncludeContours &&
            proposal.Families.Contains(DataFamily.Copper),
            "The detail acquisition proposal is wrong.");

        // Marking contours on a bounds-only capture is refused with guidance.
        LaneAOperationResult refused = workspace.MarkContoursLoaded();
        LaneACheck.Require(!refused.IsSuccess, "Contour load was declared on a contour-less capture.");

        // Vertex paging is explicit and bounded: only the selected shape.
        LaneAOperationResult page = workspace.SampleSelectedVertices(0, 10);
        LaneACheck.Require(page.IsSuccess, "Vertex sampling failed: " + page.Outcome);
        LaneACheck.Require(workspace.VertexPage.Length > 0, "No vertices were sampled.");
        LaneACheck.Require(
            workspace.VertexPageInfo.Contains("of", StringComparison.OrdinalIgnoreCase),
            "Vertex page hides its totals: " + workspace.VertexPageInfo);
        LaneAOperationResult far = workspace.SampleSelectedVertices(10_000, 10);
        LaneACheck.Require(far.IsSuccess, "Out-of-range page failed instead of returning empty.");
        LaneACheck.Require(
            workspace.VertexPage.Length == 0,
            "An out-of-range page returned vertices.");
        bool rejected = false;
        try
        {
            GeometryInspectorWorkspace.SampleVerticesPage(LaneACheck.MilLine(0m, 0m, 1m, 1m), 0, 10_000);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }
        LaneACheck.Require(rejected, "An oversized vertex page was admitted.");
        return 10;
    }

    private static async Task<int> CheckRecipeAndExportAsync()
    {
        var workspace = new GeometryInspectorWorkspace();
        workspace.AttachScene(LaneACheck.CrossingFixture(), "fixture");
        workspace.Family = ObjectFamily.Traces;
        workspace.SearchText = string.Empty;
        await workspace.SearchAsync();
        workspace.Select(workspace.Hits[0]);
        InspectorRecipe? recipe = workspace.DescribeRecipe(includeDetails: false);
        LaneACheck.Require(recipe is not null, "No recipe was produced for the selection.");
        LaneACheck.Require(
            !string.IsNullOrWhiteSpace(recipe!.Code) && !string.IsNullOrWhiteSpace(recipe.Requirement),
            "The recipe is missing code or its requirement.");
        LaneACheck.Require(
            workspace.RecipeText.Contains(recipe.Title, StringComparison.Ordinal),
            "Recipe text does not surface the recipe.");

        string directory = LaneACheck.NewTempDirectory("lane-a-t02-");
        try
        {
            string factsPath = Path.Combine(directory, "facts.txt");
            LaneAOperationResult exported = await workspace.ExportFactsAsync(factsPath, false);
            LaneACheck.Require(exported.IsSuccess, "Fact export failed: " + exported.Outcome);
            string content = await File.ReadAllTextAsync(factsPath);
            LaneACheck.Require(
                content.Contains(
                    workspace.SourceCaptureId?.ToString() ?? "missing-capture",
                    StringComparison.Ordinal),
                "Exported facts do not carry the capture identity.");
            LaneACheck.Require(
                content.Contains("Copper:", StringComparison.Ordinal),
                "Exported facts have no copper detail section.");
            return 5;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static int CheckExampleBoard()
    {
        // The labeled synthetic example is usable offline whatever it
        // contains; the test adapts to its coverage instead of assuming it.
        DesignScene example;
        try
        {
            example = EngineExamples.CreateBoard("lane-a-example");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("The synthetic example board failed: " + error.Message, error);
        }
        var workspace = new GeometryInspectorWorkspace();
        workspace.AttachScene(example, "example");
        LaneACheck.Require(workspace.HasScene, "Example board did not attach.");
        LaneACheck.Require(
            workspace.SourceProvenance.Length > 0 && workspace.CoverageSummary.Length > 0,
            "Example provenance or coverage is missing.");
        var browser = new SceneObjectBrowser(example);
        ObjectSearchResult nets = browser.Search(
            ObjectFamily.Nets, string.Empty, 10, CancellationToken.None);
        LaneACheck.Require(
            nets.Items.Length <= 10,
            "Example search ignored its row bound.");

        // Per-layer via geometry from real Engine evidence: one via exposes
        // a surface per layer with analytic fidelity and polygon geometry.
        ObjectSearchResult vias = browser.Search(
            ObjectFamily.Vias, string.Empty, 10, CancellationToken.None);
        LaneACheck.Require(vias.Items.Length > 0, "Example board has no via rows.");
        ObjectEntry? viaEntry = browser.Find(vias.Items[0].Reference!.Value);
        LaneACheck.Require(viaEntry is not null, "Example via reference did not resolve.");
        workspace.Family = ObjectFamily.Vias;
        workspace.SearchText = string.Empty;
        workspace.MaximumSearchRows = 10;
        workspace.SearchAsync().GetAwaiter().GetResult();
        LaneACheck.Require(workspace.Hits.Length > 0, "Example via search found no rows.");
        LaneAOperationResult viaSelected = workspace.Select(workspace.Hits[0]);
        LaneACheck.Require(viaSelected.IsSuccess, "Example via selection failed.");
        LaneACheck.Require(
            workspace.CopperDetail is not null && workspace.CopperDetail.Surfaces.Length >= 2,
            "Example via exposes no per-layer surfaces.");
        LaneACheck.Require(
            workspace.CopperDetail!.Surfaces.All(surface =>
                surface.Fidelity == GeometryFidelity.AnalyticPrimitive),
            "Example via surfaces are not analytic.");
        return 7;
    }
}
