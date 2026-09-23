using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.PcbTools;

/// <summary>
/// Creates and validates Engine-owned fresh-region observations before guarded
/// native navigation. Witness verification and canonical-to-native translation
/// live in Engine; no SDK region DTO, native state token, or native index
/// escapes Engine.
/// </summary>
public static class CorridorNavigation
{
    public static SceneQuery CreateQuery(
        ScalableCorridorScan scan,
        ScalableCorridorFinding finding)
    {
        (CorridorScan source, CorridorFinding witnessed) = Adapt(scan, finding);
        return CreateQuery(source, witnessed);
    }

    public static SceneQuery CreateQuery(CorridorScan scan, CorridorFinding finding)
    {
        RequireFinding(scan, finding);
        DesignBounds bounds = CreateScope(finding);
        ImmutableArray<LayerId> layers = new[] { new LayerId(finding.Layer), new LayerId(finding.WidthSourceLayer) }
            .Distinct().ToImmutableArray();
        return new SceneQuery
        {
            Kind = SceneReadKind.RegionGeometry,
            Families = [DataFamily.Layers, DataFamily.Copper],
            Region = bounds,
            Layers = layers,
            IncludeContours = true,
            MaximumObjects = 2048
        };
    }

    internal static DesignBounds CreateScope(CorridorFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        decimal padding = checked((decimal)Math.Max(40, finding.HalfWidthMils * 2));
        decimal minX = Math.Min(finding.P.X, finding.N.X) - padding;
        decimal minY = Math.Min(finding.P.Y, finding.N.Y) - padding;
        decimal maxX = Math.Max(finding.P.X, finding.N.X) + padding;
        decimal maxY = Math.Max(finding.P.Y, finding.N.Y) + padding;
        return new(new(minX, minY), new(maxX, maxY));
    }

    public static void ValidateFreshRead(
        CorridorScan scan,
        CorridorFinding finding,
        LiveRegionScene region,
        WorkspaceDocumentIdentity expectedDocument)
    {
        RequireFinding(scan, finding);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(expectedDocument);
        region.RequireCurrent();
        ValidateFreshScene(scan, finding, region.Scene, region.Document, expectedDocument);
    }

    public static void ValidateFreshRead(
        ScalableCorridorScan scan,
        ScalableCorridorFinding finding,
        LiveRegionScene region,
        WorkspaceDocumentIdentity expectedDocument)
    {
        (CorridorScan source, CorridorFinding witnessed) = Adapt(scan, finding);
        ValidateFreshRead(source, witnessed, region, expectedDocument);
    }

    /// <summary>
    /// Pure immutable-scene witness admission. Production callers additionally
    /// require LiveRegionScene.RequireCurrent before this check can authorize a
    /// native zoom; tests and offline analysis can exercise the matching rules
    /// without manufacturing native authority.
    /// </summary>
    public static void ValidateFreshScene(
        CorridorScan scan,
        CorridorFinding finding,
        DesignScene geometry,
        WorkspaceDocumentIdentity observedDocument,
        WorkspaceDocumentIdentity expectedDocument)
    {
        MatchWitnesses(scan, finding, geometry, observedDocument, expectedDocument);
    }

    public static void ValidateFreshScene(
        ScalableCorridorScan scan,
        ScalableCorridorFinding finding,
        DesignScene geometry,
        WorkspaceDocumentIdentity observedDocument,
        WorkspaceDocumentIdentity expectedDocument)
    {
        (CorridorScan source, CorridorFinding witnessed) = Adapt(scan, finding);
        ValidateFreshScene(
            source,
            witnessed,
            geometry,
            observedDocument,
            expectedDocument);
    }

    /// <summary>
    /// Live-region witness admission. Finding-membership and finding-coverage
    /// checks stay in PD; Engine verifies the capture witnesses against the
    /// fresh scene and translates them to native ordinals through its recorded
    /// conversion mapping. The returned handle is bound to
    /// <paramref name="region"/> and only it can authorize navigation.
    /// </summary>
    public static EngineWitnessMatch MatchFreshWitnesses(
        CorridorScan scan,
        CorridorFinding finding,
        LiveRegionScene region,
        WorkspaceDocumentIdentity expectedDocument)
    {
        RequireFinding(scan, finding);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(expectedDocument);
        region.RequireCurrent();
        SceneQuery query = CreateQuery(scan, finding);
        RequireFindingCoverage(finding, region.Scene);
        return EngineWitnessMatching.MatchWitnesses(
            scan.Scene,
            [finding.PositiveViaIndex, finding.NegativeViaIndex, finding.AggressorIndex],
            region,
            expectedDocument,
            query.Layers);
    }

    public static EngineWitnessMatch MatchFreshWitnesses(
        ScalableCorridorScan scan,
        ScalableCorridorFinding finding,
        LiveRegionScene region,
        WorkspaceDocumentIdentity expectedDocument)
    {
        (CorridorScan source, CorridorFinding witnessed) = Adapt(scan, finding);
        return MatchFreshWitnesses(source, witnessed, region, expectedDocument);
    }

    public static CorridorNavigationEvidence MatchFreshWitnessesWithEvidence(
        ScalableCorridorScan scan,
        ScalableCorridorFinding finding,
        LiveRegionScene region,
        WorkspaceDocumentIdentity expectedDocument)
    {
        (CorridorScan source, CorridorFinding witnessed) = Adapt(scan, finding);
        return MatchFreshWitnessesWithEvidence(
            source,
            witnessed,
            region,
            expectedDocument);
    }

    public static CorridorNavigationEvidence MatchFreshWitnessesWithEvidence(
        CorridorScan scan,
        CorridorFinding finding,
        LiveRegionScene region,
        WorkspaceDocumentIdentity expectedDocument)
    {
        EngineWitnessMatch witnesses = MatchFreshWitnesses(
            scan,
            finding,
            region,
            expectedDocument);
        CorridorDetailedEvidence detailed = CorridorDetailedEvidenceFactory.Create(
            finding,
            region.Scene,
            witnesses,
            region.NativeOperationId,
            region.Timing);
        return new(witnesses, detailed);
    }

    /// <summary>
    /// Scene-level witness admission. See <see cref="MatchFreshWitnesses"/>
    /// for the Engine-verified handle contract.
    /// </summary>
    public static EngineWitnessMatch MatchWitnesses(
        CorridorScan scan,
        CorridorFinding finding,
        DesignScene geometry,
        WorkspaceDocumentIdentity observedDocument,
        WorkspaceDocumentIdentity expectedDocument)
    {
        RequireFinding(scan, finding);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(observedDocument);
        ArgumentNullException.ThrowIfNull(expectedDocument);
        SceneQuery query = CreateQuery(scan, finding);
        RequireFindingCoverage(finding, geometry);
        return EngineWitnessMatching.MatchWitnesses(
            scan.Scene,
            [finding.PositiveViaIndex, finding.NegativeViaIndex, finding.AggressorIndex],
            geometry,
            observedDocument,
            expectedDocument,
            query.Layers);
    }

    public static EngineWitnessMatch MatchWitnesses(
        ScalableCorridorScan scan,
        ScalableCorridorFinding finding,
        DesignScene geometry,
        WorkspaceDocumentIdentity observedDocument,
        WorkspaceDocumentIdentity expectedDocument)
    {
        (CorridorScan source, CorridorFinding witnessed) = Adapt(scan, finding);
        return MatchWitnesses(
            source,
            witnessed,
            geometry,
            observedDocument,
            expectedDocument);
    }

    public static CorridorNavigationEvidence MatchWitnessesWithEvidence(
        CorridorScan scan,
        CorridorFinding finding,
        DesignScene geometry,
        WorkspaceDocumentIdentity observedDocument,
        WorkspaceDocumentIdentity expectedDocument,
        Guid nativeOperationId,
        EngineRegionTiming? acquisitionTiming = null)
    {
        EngineWitnessMatch witnesses = MatchWitnesses(
            scan,
            finding,
            geometry,
            observedDocument,
            expectedDocument);
        CorridorDetailedEvidence detailed = CorridorDetailedEvidenceFactory.Create(
            finding,
            geometry,
            witnesses,
            nativeOperationId,
            acquisitionTiming);
        return new(witnesses, detailed);
    }

    public static CorridorNavigationEvidence MatchWitnessesWithEvidence(
        ScalableCorridorScan scan,
        ScalableCorridorFinding finding,
        DesignScene geometry,
        WorkspaceDocumentIdentity observedDocument,
        WorkspaceDocumentIdentity expectedDocument,
        Guid nativeOperationId,
        EngineRegionTiming? acquisitionTiming = null)
    {
        (CorridorScan source, CorridorFinding witnessed) = Adapt(scan, finding);
        return MatchWitnessesWithEvidence(
            source,
            witnessed,
            geometry,
            observedDocument,
            expectedDocument,
            nativeOperationId,
            acquisitionTiming);
    }

    private static (CorridorScan Scan, CorridorFinding Finding) Adapt(
        ScalableCorridorScan scan,
        ScalableCorridorFinding finding)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(finding);
        DesignScene witnesses = scan.CreateSourceWitnessScene(finding);
        CorridorFinding witnessed = finding.Finding with
        {
            PositiveViaIndex = 0,
            NegativeViaIndex = 1,
            AggressorIndex = 2,
        };
        var source = new CorridorScan(
            witnesses,
            scan.Options,
            scan.PairCount,
            scan.CorridorCount,
            Array.AsReadOnly([witnessed]),
            scan.CoverageWarnings);
        return (source, witnessed);
    }

    private static void RequireFindingCoverage(CorridorFinding finding, DesignScene geometry)
    {
        if (!geometry.Document.Bounds.Contains(finding.P) ||
            !geometry.Document.Bounds.Contains(finding.N))
        {
            throw new InvalidDataException("Navigation requires complete fresh Engine geometry for this exact board and finding. Run the analysis again if its context changed.");
        }
    }

    private static void RequireFinding(CorridorScan scan, CorridorFinding finding)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(finding);
        if (!scan.Findings.Contains(finding))
        {
            throw new ArgumentException("The finding does not belong to this captured analysis.");
        }
    }
}
