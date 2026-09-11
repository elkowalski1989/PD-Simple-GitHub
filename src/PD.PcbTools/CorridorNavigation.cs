using System.Collections.Immutable;
using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.PcbTools;

/// <summary>
/// Creates and validates Engine-owned fresh-region observations before guarded
/// native navigation. No SDK region DTO or native state token escapes Engine.
/// </summary>
public static class CorridorNavigation
{
    public static SceneQuery CreateQuery(CorridorScan scan, CorridorFinding finding)
    {
        RequireFinding(scan, finding);
        decimal padding = checked((decimal)Math.Max(40, finding.HalfWidthMils * 2));
        decimal minX = Math.Min(finding.P.X, finding.N.X) - padding;
        decimal minY = Math.Min(finding.P.Y, finding.N.Y) - padding;
        decimal maxX = Math.Max(finding.P.X, finding.N.X) + padding;
        decimal maxY = Math.Max(finding.P.Y, finding.N.Y) + padding;
        ImmutableArray<LayerId> layers = new[] { new LayerId(finding.Layer), new LayerId(finding.WidthSourceLayer) }
            .Distinct().ToImmutableArray();
        return new SceneQuery
        {
            Kind = SceneReadKind.RegionGeometry,
            Families = [DataFamily.Layers, DataFamily.Copper],
            Region = new(new(minX, minY), new(maxX, maxY)),
            Layers = layers,
            IncludeContours = true,
            MaximumObjects = 2048
        };
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
        RequireFinding(scan, finding);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(observedDocument);
        ArgumentNullException.ThrowIfNull(expectedDocument);
        SceneQuery query = CreateQuery(scan, finding);
        if (observedDocument != expectedDocument ||
            geometry.Document.NativeUnits != scan.Scene.Document.NativeUnits ||
            geometry.Document.NativePrecision != scan.Scene.Document.NativePrecision ||
            !geometry.Query.IncludeContours ||
            !geometry.Coverage[DataFamily.Copper].IsComplete ||
            !geometry.Coverage[DataFamily.Layers].IsComplete ||
            !query.Layers.All(layer => geometry.Layers.RequireComplete().Any(item => item.Id == layer)) ||
            !geometry.Document.Bounds.Contains(finding.P) ||
            !geometry.Document.Bounds.Contains(finding.N))
        {
            throw new InvalidDataException("Navigation requires complete fresh Engine geometry for this exact board and finding. Run the analysis again if its context changed.");
        }
        int[] indices = [finding.PositiveViaIndex, finding.NegativeViaIndex, finding.AggressorIndex];
        ImmutableArray<CopperObject> original = scan.Scene.Copper.RequireAvailable();
        ImmutableArray<CopperObject> fresh = geometry.Copper.RequireComplete();
        foreach (int index in indices)
        {
            if ((uint)index >= (uint)original.Length)
            {
                throw new InvalidDataException("The finding's capture-local witness is no longer valid.");
            }
            CopperObject expected = original[index];
            string signature = Signature(expected, query.Layers);
            int matches = fresh.Count(item => Signature(item, query.Layers) == signature);
            if (matches != 1)
            {
                throw new InvalidDataException("A finding object changed, disappeared, or is ambiguous. Navigation was not dispatched; run the analysis again.");
            }
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

    private static string Signature(CopperObject item, IReadOnlyList<LayerId> layers)
    {
        ViaSpan? via = item.Via;
        ViaAnalysisEvidence? evidence = via?.Analysis;
        object? centerline = item.Centerline switch
        {
            LineGeometry line => new { Kind = "line", line.Start, line.End },
            ArcGeometry arc => new { Kind = "arc", arc.Start, arc.End, arc.Center, arc.Clockwise, arc.FullCircle },
            _ => null
        };
        var pads = evidence?.Pads.Where(pad => layers.Contains(pad.RequestedLayer))
            .OrderBy(pad => pad.RequestedLayer.Value, StringComparer.Ordinal)
            .ThenBy(pad => pad.Type, StringComparer.Ordinal)
            .Select(pad => new
            {
                RequestedLayer = pad.RequestedLayer.Value,
                ResolvedLayer = pad.ResolvedLayer.Value,
                pad.Type,
                pad.Figure,
                ExtentX = pad.ExtentX.Mils,
                ExtentY = pad.ExtentY.Mils
            }).ToArray();
        return JsonSerializer.Serialize(new
        {
            Kind = item.Kind.ToString(),
            item.NetName,
            Layer = item.Layer?.Value,
            item.Bounds,
            Centerline = centerline,
            item.FillOutOfDate,
            Position = via?.Position,
            Padstack = via?.Padstack,
            IsThrough = via?.IsThrough,
            OriginalLayers = via?.OriginalLayers.Select(layer => layer.Value).ToArray(),
            ActiveLayers = via?.ActiveLayers.Select(layer => layer.Value).ToArray(),
            BackdrillStatus = via?.BackdrillStatus,
            ActiveLayersAvailable = evidence?.ActiveLayersAvailable,
            BackdrillExclusion = evidence?.BackdrillExclusion,
            TopStartLayer = evidence?.TopStartLayer?.Value,
            TopMustNotCutLayer = evidence?.TopMustNotCutLayer?.Value,
            BottomStartLayer = evidence?.BottomStartLayer?.Value,
            BottomMustNotCutLayer = evidence?.BottomMustNotCutLayer?.Value,
            Pads = pads
        });
    }
}
