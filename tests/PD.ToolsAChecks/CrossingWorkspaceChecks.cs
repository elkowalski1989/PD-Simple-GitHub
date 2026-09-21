using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Analysis;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.Simple.Tools.Analysis;

namespace PD.ToolsAChecks;

/// <summary>
/// T01 offline checks: qualified kernel geometry plus the review workspace
/// over synthetic scenes. Engine's analyzer is the oracle for counting
/// behavior; exact hand-computed expectations live at kernel level.
/// </summary>
internal static class CrossingWorkspaceChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        checks += CheckKernelKnownGeometry();
        checks += CheckKernelArcsAndRegions();
        checks += await CheckWorkspaceRunAsync();
        checks += await CheckWorkspaceGatesAsync();
        checks += await CheckWorkspaceExportAsync();
        Console.WriteLine($"PASS: {checks} T01 crossing review checks.");
        return checks;
    }

    private static int CheckKernelKnownGeometry()
    {
        var kernel = new GeometryKernel();
        // Tangency: segments share exactly one endpoint.
        var first = LaneACheck.MilLine(0m, 0m, 10m, 0m);
        var second = LaneACheck.MilLine(10m, 0m, 10m, 10m);
        IntersectionResult touching = kernel.Intersect(first, second);
        LaneACheck.Require(
            touching.Relation == GeometricRelation.Intersects,
            "Endpoint tangency was not reported as an intersection.");
        LaneACheck.Require(
            touching.Qualification == RelationQualification.ExactLinear,
            "Tangency lost its exact linear qualification.");

        // Overlap: collinear spans share more than a point.
        var spanA = LaneACheck.MilLine(0m, 0m, 20m, 0m);
        var spanB = LaneACheck.MilLine(10m, 0m, 30m, 0m);
        IntersectionResult overlap = kernel.Intersect(spanA, spanB);
        LaneACheck.Require(
            overlap.Relation == GeometricRelation.Intersects,
            "Overlapping collinear spans were not reported as intersecting.");

        // Zero-length input measures zero and stays disjoint from a remote line.
        var point = new LineGeometry(LaneACheck.Mil(5m, 50m), LaneACheck.Mil(5m, 50m));
        LaneACheck.Require(
            kernel.MeasureLength(point).Length.Mils == 0m,
            "Zero-length input was not measured as zero.");
        LaneACheck.Require(
            kernel.Intersect(point, first).Relation == GeometricRelation.Disjoint,
            "A remote zero-length point was not reported disjoint.");

        // Arc geometry is analytic, not a chord substitution.
        var arc = new ArcGeometry(
            LaneACheck.Mil(10m, 0m), LaneACheck.Mil(0m, 10m), LaneACheck.Mil(0m, 0m),
            false, false, Length.From(0m, LengthUnit.Mils));
        GeometryLengthResult arcLength = kernel.MeasureLength(arc);
        double expectedQuarter = Math.PI * 10.0 / 2.0;
        LaneACheck.Require(
            Math.Abs((double)arcLength.Length.Mils - expectedQuarter) <= 0.05,
            $"Quarter arc length {arcLength.Length.Mils} mil is not the analytic {expectedQuarter:F3} mil.");
        LaneACheck.Require(
            arcLength.Qualification != RelationQualification.Indeterminate,
            "Analytic arc measurement came back indeterminate.");
        return 8;
    }

    private static int CheckKernelArcsAndRegions()
    {
        var kernel = new GeometryKernel();
        // Region with a hole: outer 20x20 square, inner 4x4 hole.
        CurveLoopGeometry Shell(ImmutableArray<DesignPoint> corners) =>
            new(ImmutableArray.Create<GeometryShape>(
                new LineGeometry(corners[0], corners[1]),
                new LineGeometry(corners[1], corners[2]),
                new LineGeometry(corners[2], corners[3]),
                new LineGeometry(corners[3], corners[0])));
        var outer = Shell(ImmutableArray.Create(
            LaneACheck.Mil(0m, 0m), LaneACheck.Mil(20m, 0m),
            LaneACheck.Mil(20m, 20m), LaneACheck.Mil(0m, 20m)));
        var hole = Shell(ImmutableArray.Create(
            LaneACheck.Mil(8m, 8m), LaneACheck.Mil(12m, 8m),
            LaneACheck.Mil(12m, 12m), LaneACheck.Mil(8m, 12m)));
        var withHole = new RegionGeometry(outer, ImmutableArray.Create(hole));
        LaneACheck.Require(withHole.Holes.Length == 1, "Region hole was not retained.");

        // A point inside the hole is not contained copper; a point in the
        // ring is. Holes are retained geometry, not generic bounds.
        ContainmentResult inHole = kernel.Contains(withHole, LaneACheck.Mil(10m, 10m));
        ContainmentResult inRing = kernel.Contains(withHole, LaneACheck.Mil(2m, 2m));
        LaneACheck.Require(
            inHole.Relation != ContainmentRelation.Inside,
            "A point inside a hole was reported as contained copper.");
        LaneACheck.Require(
            inRing.Relation == ContainmentRelation.Inside,
            "A point inside the copper ring was not reported contained.");

        // Island set: two disjoint squares stay two islands, never merged
        // into one bounds box.
        var islandA = new RegionGeometry(
            Shell(ImmutableArray.Create(
                LaneACheck.Mil(0m, 0m), LaneACheck.Mil(5m, 0m),
                LaneACheck.Mil(5m, 5m), LaneACheck.Mil(0m, 5m))),
            ImmutableArray<CurveLoopGeometry>.Empty);
        var islandB = new RegionGeometry(
            Shell(ImmutableArray.Create(
                LaneACheck.Mil(50m, 50m), LaneACheck.Mil(55m, 50m),
                LaneACheck.Mil(55m, 55m), LaneACheck.Mil(50m, 55m))),
            ImmutableArray<CurveLoopGeometry>.Empty);
        var islands = new RegionSetGeometry(ImmutableArray.Create(islandA, islandB));
        LaneACheck.Require(islands.Regions.Length == 2, "Two copper islands were not kept distinct.");
        IntersectionResult islandTouch = kernel.Intersect(islandA, islandB);
        LaneACheck.Require(
            islandTouch.Relation == GeometricRelation.Disjoint,
            "Separate islands were not reported disjoint.");
        return 5;
    }

    private static async Task<int> CheckWorkspaceRunAsync()
    {
        var workspace = new CrossingReviewWorkspace();
        LaneACheck.Require(!workspace.CanRun, "Run is available with no input attached.");
        workspace.AttachScene(LaneACheck.CrossingFixture(), "fixture");
        LaneACheck.Require(workspace.HasScene, "Fixture scene did not attach.");
        LaneACheck.Require(!workspace.CanRun, "Run is available with no layer or region.");
        LaneACheck.Require(
            workspace.RunBlockedReason.Contains("layer", StringComparison.OrdinalIgnoreCase),
            "The blocked reason does not name the missing layer.");

        workspace.LayerName = "ETCH/TOP";
        workspace.RegionX1Mils = 0m;
        workspace.RegionY1Mils = 0m;
        workspace.RegionX2Mils = 100m;
        workspace.RegionY2Mils = 100m;
        LaneACheck.Require(workspace.HasRegion, "Region entry was not recorded.");
        LaneACheck.Require(workspace.CanRun, "Run is blocked despite complete copper scope: " + workspace.RunBlockedReason);

        LaneAOperationResult run = await workspace.RunAsync();
        LaneACheck.Require(run.IsSuccess, "Offline run failed: " + run.Outcome);
        LaneACheck.Require(run.CaptureId == workspace.SourceCaptureId, "Run lost its capture identity.");
        LaneACheck.Require(
            workspace.LastAnalysis is not null && workspace.CompleteForRequestedRepresentation,
            "Complete copper scope did not yield a complete analysis: " + workspace.ResultSummary);
        // Counting semantics stay distinct: locations are connected
        // intersections per physical net, so the collinear duplicate on N4
        // counts as its own object, net, and location (observed Engine
        // behavior on this fixture: 3/3/3 on ETCH/TOP).
        CrossingAnalysis analysis = workspace.LastAnalysis!;
        LaneACheck.Require(
            analysis.InterferingNetCount == 3 &&
            analysis.InterferingObjectCount == 3 &&
            analysis.CrossingLocationCount == 3 &&
            analysis.UnassignedObjectCount == 0,
            "Fixture counts are not the expected 3 nets/objects/locations: " + workspace.ResultSummary);
        LaneACheck.Require(
            !analysis.FindingsTruncated || workspace.Findings.Length == workspace.MaximumFindings,
            "Truncated findings do not match the declared maximum.");
        LaneACheck.Require(
            workspace.Findings.Length == analysis.Findings.Length,
            "Published rows do not match Engine findings (subject filter is empty).");
        foreach (CrossingFindingRow row in workspace.Findings)
        {
            LaneACheck.Require(
                row.WitnessCount == 0 || row.FirstWitness is not null,
                "A finding with witnesses exposes no first witness point.");
        }

        // Layer separation: the lone BOT parallel has no crossings, and a
        // known complete no-crossing case differs from unknown/missing.
        workspace.LayerName = "ETCH/BOT";
        LaneAOperationResult botRun = await workspace.RunAsync();
        LaneACheck.Require(botRun.IsSuccess, "Layer-separated run failed: " + botRun.Outcome);
        LaneACheck.Require(
            workspace.LastAnalysis is not null && workspace.LastAnalysis.IsClear &&
            workspace.LastAnalysis.CompleteForRequestedRepresentation,
            "The no-crossing layer is not a complete clear: " + workspace.ResultSummary);

        // Filter and grouping preserve identity; selection derives a browse scope.
        workspace.LayerName = "ETCH/TOP";
        await workspace.RunAsync();
        workspace.FilterText = "N1";
        LaneACheck.Require(
            workspace.VisibleFindings.All(row =>
                row.NetName.Contains("N1", StringComparison.OrdinalIgnoreCase)),
            "Finding filter leaked a non-matching row.");
        workspace.FilterText = string.Empty;
        workspace.GroupByNet = true;
        LaneACheck.Require(
            workspace.VisibleGroups.Length > 0 &&
            workspace.VisibleGroups.Sum(group => group.Count) == workspace.Findings.Length,
            "Net grouping lost or duplicated findings.");
        workspace.GroupByNet = false;
        if (workspace.Findings.Length > 0)
        {
            workspace.SelectedFinding = workspace.Findings[0];
            LaneACheck.Require(
                workspace.SelectedFindingDetail.Contains(workspace.Findings[0].NetName, StringComparison.Ordinal),
                "Selected finding detail does not describe the selection.");
            LaneACheck.Require(
                workspace.BrowseRequestSummary.Contains("Browse scope", StringComparison.Ordinal),
                "No browse scope is derived from the selection.");
        }

        // Subject-net narrowing keeps Engine semantics: rows only, and the
        // report still carries the declared counts.
        workspace.SubjectNet = "N1";
        LaneAOperationResult narrowed = await workspace.RunAsync();
        LaneACheck.Require(narrowed.IsSuccess, "Subject-narrowed run failed.");
        LaneACheck.Require(
            workspace.Findings.All(row =>
                string.Equals(row.NetName, "N1", StringComparison.OrdinalIgnoreCase)),
            "Subject-net narrowing leaked another net.");
        workspace.SubjectNet = string.Empty;

        // Cancellation never publishes partial findings as success.
        int rowsBefore = workspace.Findings.Length;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        LaneAOperationResult cancelResult = await workspace.RunAsync(canceled.Token);
        LaneACheck.Require(!cancelResult.IsSuccess, "A canceled run reported success.");
        LaneACheck.Require(
            cancelResult.Outcome.Contains("Cancel", StringComparison.OrdinalIgnoreCase),
            "A canceled run did not say it was canceled.");
        LaneACheck.Require(
            workspace.Findings.Length == rowsBefore,
            "A canceled run replaced the previously published findings.");
        return 20;
    }

    private static async Task<int> CheckWorkspaceGatesAsync()
    {
        // Partial scope blocks the run with an explicit acquisition proposal.
        var workspace = new CrossingReviewWorkspace();
        workspace.AttachScene(LaneACheck.PartialFixture(), "fixture");
        workspace.LayerName = "ETCH/TOP";
        workspace.RegionX1Mils = 0m;
        workspace.RegionY1Mils = 0m;
        workspace.RegionX2Mils = 10m;
        workspace.RegionY2Mils = 10m;
        LaneACheck.Require(!workspace.CanRun, "Run is available on incomplete copper scope.");
        LaneAOperationResult blocked = await workspace.RunAsync();
        LaneACheck.Require(!blocked.IsSuccess, "Run succeeded on incomplete scope.");
        LaneACheck.Require(workspace.MissingScope.Length > 0, "Missing scope is not listed.");
        SceneQuery proposal = workspace.DescribeProposedAcquisition();
        LaneACheck.Require(
            proposal.Families.Contains(DataFamily.Copper),
            "The acquisition proposal lost the Copper family.");
        LaneACheck.Require(
            proposal.Region.HasValue,
            "The acquisition proposal lost the explicit region.");
        LaneACheck.Require(!proposal.IncludeContours, "Centerline acquisition must not opt into contours.");

        // Copper-area representation opts contours into the proposal only.
        workspace.Representation = CrossingRepresentation.CopperArea;
        LaneACheck.Require(
            workspace.DescribeProposedAcquisition().IncludeContours,
            "Copper-area acquisition does not request contours.");

        // Degenerate regions are rejected before dispatch.
        workspace.RegionX2Mils = 0m;
        LaneACheck.Require(!workspace.CanRun, "A zero-area region is runnable.");
        LaneAOperationResult degenerate = await workspace.RunAsync();
        LaneACheck.Require(!degenerate.IsSuccess, "A zero-area region ran.");

        // Missing region and missing layer report distinct reasons.
        var empty = new CrossingReviewWorkspace();
        empty.AttachScene(LaneACheck.CrossingFixture(), "fixture");
        LaneACheck.Require(
            empty.RunBlockedReason.Contains("layer", StringComparison.OrdinalIgnoreCase),
            "Empty layer reason is not reported first.");

        // Live acquisition without a delegate is a setup block, not success.
        // (T01 reacquisition for live work is AcquireMissingScopeAsync;
        // RerunAsync only reruns the attached capture.)
        LaneAOperationResult noDelegate = await empty.AcquireMissingScopeAsync();
        LaneACheck.Require(!noDelegate.IsSuccess, "Acquisition succeeded with no live delegate.");
        return 11;
    }

    private static async Task<int> CheckWorkspaceExportAsync()
    {
        var workspace = new CrossingReviewWorkspace();
        string directory = LaneACheck.NewTempDirectory("lane-a-t01-");
        try
        {
            string reportPath = Path.Combine(directory, "crossing-report.txt");
            LaneAOperationResult nothing = await workspace.ExportReportAsync(reportPath, false);
            LaneACheck.Require(!nothing.IsSuccess, "Export succeeded with no analysis.");

            workspace.AttachScene(LaneACheck.CrossingFixture(), "fixture");
            workspace.LayerName = "ETCH/TOP";
            workspace.RegionX1Mils = 0m;
            workspace.RegionY1Mils = 0m;
            workspace.RegionX2Mils = 100m;
            workspace.RegionY2Mils = 100m;
            LaneAOperationResult run = await workspace.RunAsync();
            LaneACheck.Require(run.IsSuccess, "Run failed before export: " + run.Outcome);
            LaneAOperationResult exported = await workspace.ExportReportAsync(reportPath, false);
            LaneACheck.Require(exported.IsSuccess, "Report export failed: " + exported.Outcome);
            LaneACheck.Require(
                exported.ArtifactSha256 == LaneAArtifacts.ComputeFileSha256(reportPath),
                "Exported report hash does not match the file.");
            string content = await File.ReadAllTextAsync(reportPath);
            LaneACheck.Require(
                content.Contains("Counting semantics", StringComparison.Ordinal),
                "The report does not declare its counting semantics.");
            LaneACheck.Require(
                content.Contains(
                    workspace.SourceCaptureId?.ToString() ?? "missing-capture",
                    StringComparison.Ordinal),
                "The report does not carry its capture identity.");

            LaneAOperationResult refusal = await workspace.ExportReportAsync(reportPath, false);
            LaneACheck.Require(!refusal.IsSuccess, "Export overwrote without approval.");
            LaneAOperationResult overwrite = await workspace.ExportReportAsync(reportPath, true);
            LaneACheck.Require(overwrite.IsSuccess, "Approved overwrite failed.");
            LaneACheck.Require(
                Directory.GetFiles(directory, "*.staging-*").Length == 0,
                "Staging files leaked beside the exported report.");
            return 9;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
