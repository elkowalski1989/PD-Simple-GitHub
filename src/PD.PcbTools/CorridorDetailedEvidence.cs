using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.PcbTools;

public enum CorridorDetailedEvidenceStatus
{
    Complete,
    NotApplicable,
    Incomplete,
}

/// <summary>
/// Current, read-only geometry retained for one selected corridor finding.
/// This evidence is deliberately separate from the original scalar scan: it
/// describes the freshly observed aggressor geometry but does not rerun or
/// replace the historical crossing analysis.
/// </summary>
public sealed record CorridorDetailedEvidence(
    DesignScene FreshScene,
    Guid NativeOperationId,
    EngineRegionTiming? AcquisitionTiming,
    EngineWitnessVerificationScope WitnessVerificationScope,
    SceneObjectId AggressorId,
    CopperKind AggressorKind,
    LayerId Layer,
    CorridorDetailedEvidenceStatus Status,
    int ResolvedRegionCount,
    int HoleCount,
    ImmutableArray<string> Diagnostics)
{
    public bool HasCompleteShapeContours =>
        Status == CorridorDetailedEvidenceStatus.Complete;
}

/// <summary>
/// The Engine witness handle used for guarded navigation plus the distinct
/// current-geometry evidence that PD Simple may present to the user.
/// </summary>
public sealed record CorridorNavigationEvidence(
    EngineWitnessMatch Witnesses,
    CorridorDetailedEvidence DetailedEvidence);

internal static class CorridorDetailedEvidenceFactory
{
    public static CorridorDetailedEvidence Create(
        CorridorFinding finding,
        DesignScene freshScene,
        EngineWitnessMatch witnesses,
        Guid nativeOperationId,
        EngineRegionTiming? acquisitionTiming)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(freshScene);
        ArgumentNullException.ThrowIfNull(witnesses);
        if (witnesses.MatchedCount != 3 || witnesses.FreshPositions.Length != 3)
        {
            throw new InvalidDataException(
                "Detailed corridor evidence requires the two subject vias and aggressor to match uniquely.");
        }

        // Engine witness matching has already admitted this current region and
        // its selected objects. Keep conservative via metadata gaps visible in
        // the detailed-evidence status without discarding the matched copper.
        ImmutableArray<CopperObject> copper = freshScene.Copper.RequireAvailable();
        int aggressorPosition = witnesses.FreshPositions[2];
        if ((uint)aggressorPosition >= (uint)copper.Length)
        {
            throw new InvalidDataException(
                "The matched aggressor no longer addresses the fresh Engine scene.");
        }

        CopperObject aggressor = copper[aggressorPosition];
        var layer = new LayerId(finding.Layer);
        if (aggressor.Kind != CopperKind.Shape)
        {
            return new(
                freshScene,
                nativeOperationId,
                acquisitionTiming,
                witnesses.VerificationScope,
                aggressor.Id,
                aggressor.Kind,
                layer,
                CorridorDetailedEvidenceStatus.NotApplicable,
                0,
                0,
                ["Shape hole and island evidence does not apply to this aggressor kind."]);
        }

        var diagnostics = ImmutableArray.CreateBuilder<string>();
        FamilyCoverage copperCoverage = freshScene.Coverage[DataFamily.Copper];
        FamilyCoverage layerCoverage = freshScene.Coverage[DataFamily.Layers];
        CopperKindCoverage? shapeCoverage = freshScene.CopperScope.Details
            .SingleOrDefault(item => item.Kind == CopperKind.Shape);
        if (!freshScene.Query.IncludeContours)
        {
            diagnostics.Add("The fresh query did not request contours.");
        }
        if (!freshScene.Query.Layers.Contains(layer))
        {
            diagnostics.Add($"The fresh query did not include layer {layer.Value}.");
        }
        if (!copperCoverage.IsComplete || !layerCoverage.IsComplete)
        {
            diagnostics.Add("Fresh Copper and Layers coverage is incomplete.");
        }
        if (shapeCoverage is null || !shapeCoverage.IsComplete ||
            shapeCoverage.Fidelity != GeometryFidelity.NativeContour)
        {
            diagnostics.Add("Fresh shape coverage is not complete native-contour evidence.");
        }
        if (aggressor.FillOutOfDate == true)
        {
            diagnostics.Add("The selected shape fill is out of date.");
        }
        AddDiagnostics(diagnostics, aggressor.Diagnostics);

        LayerGeometryRecord[] resolved = aggressor.Geometry?.Geometry
            .Where(item => item.Layer == layer &&
                item.Geometry.Role == GeometryRole.ResolvedCopper)
            .ToArray() ?? [];
        AddDiagnostics(diagnostics, aggressor.Geometry?.Diagnostics ?? []);
        if (resolved.Length == 0)
        {
            diagnostics.Add(
                $"No current resolved-copper contour was captured on {layer.Value}.");
        }

        int regionCount = 0;
        int holeCount = 0;
        foreach (LayerGeometryRecord item in resolved)
        {
            GeometryRecord geometry = item.Geometry;
            if (geometry.Fidelity != GeometryFidelity.NativeContour ||
                geometry.Validity != GeometryValidity.Current ||
                geometry.Origin is not (GeometryOrigin.AcquiredNative or GeometryOrigin.NativeReadback))
            {
                diagnostics.Add(
                    "Resolved copper is not current, native-origin, native-contour geometry.");
            }
            AddDiagnostics(diagnostics, geometry.Diagnostics);
            if (geometry.Source is { } source)
            {
                AddDiagnostics(diagnostics, source.Diagnostics);
            }

            switch (geometry.Shape)
            {
                case RegionGeometry region:
                    regionCount++;
                    holeCount = checked(holeCount + region.Holes.Length);
                    break;
                case RegionSetGeometry set:
                    regionCount = checked(regionCount + set.Regions.Length);
                    holeCount = checked(holeCount + set.Regions.Sum(region => region.Holes.Length));
                    break;
                default:
                    diagnostics.Add(
                        "Resolved copper did not retain analytic region and hole topology.");
                    break;
            }
        }

        ImmutableArray<string> distinctDiagnostics = diagnostics
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        CorridorDetailedEvidenceStatus status = distinctDiagnostics.IsEmpty
            ? CorridorDetailedEvidenceStatus.Complete
            : CorridorDetailedEvidenceStatus.Incomplete;
        return new(
            freshScene,
            nativeOperationId,
            acquisitionTiming,
            witnesses.VerificationScope,
            aggressor.Id,
            aggressor.Kind,
            layer,
            status,
            regionCount,
            holeCount,
            distinctDiagnostics);
    }

    private static void AddDiagnostics(
        ImmutableArray<string>.Builder destination,
        IEnumerable<string> source)
    {
        foreach (string message in source)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                destination.Add(message);
            }
        }
    }
}
