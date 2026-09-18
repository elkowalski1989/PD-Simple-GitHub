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
        MatchWitnesses(scan, finding, geometry, observedDocument, expectedDocument);
    }

    /// <summary>
    /// Live-region witness admission that also returns the fresh-scene copper
    /// positions of the P via, N via, and aggressor witnesses, in that order.
    /// Positions double as native witness indices for scoped zoom only; the
    /// scene-level match fails closed on kind-filtered queries instead of
    /// yielding positions that no longer address the native object array.
    /// </summary>
    public static int[] MatchFreshWitnesses(
        CorridorScan scan,
        CorridorFinding finding,
        LiveRegionScene region,
        WorkspaceDocumentIdentity expectedDocument)
    {
        RequireFinding(scan, finding);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(expectedDocument);
        region.RequireCurrent();
        return MatchWitnesses(scan, finding, region.Scene, region.Document, expectedDocument);
    }

    /// <summary>
    /// Scene-level witness admission with matched fresh-copper positions. See
    /// <see cref="MatchFreshWitnesses"/> for the native-index contract.
    /// </summary>
    public static int[] MatchWitnesses(
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
        bool sameLayerScope = geometry.Query.Layers.Length == query.Layers.Length &&
            query.Layers.All(layer => geometry.Query.Layers.Contains(layer));
        if (!geometry.Coverage[DataFamily.Copper].IsComplete ||
            !geometry.Coverage[DataFamily.Layers].IsComplete)
        {
            throw new InvalidDataException(IncompleteGeometryMessage(geometry));
        }
        if (observedDocument != expectedDocument ||
            geometry.Document.NativeUnits != scan.Scene.Document.NativeUnits ||
            geometry.Document.NativePrecision != scan.Scene.Document.NativePrecision ||
            !geometry.Query.IncludeContours || !sameLayerScope ||
            !query.Layers.All(layer => geometry.Layers.RequireComplete().Any(item => item.Id == layer)) ||
            !geometry.Document.Bounds.Contains(finding.P) ||
            !geometry.Document.Bounds.Contains(finding.N))
        {
            throw new InvalidDataException("Navigation requires complete fresh Engine geometry for this exact board and finding. Run the analysis again if its context changed.");
        }
        if (geometry.Query.CopperKinds.Length != Enum.GetValues<CopperKind>().Length)
        {
            throw new InvalidDataException("Navigation requires an unfiltered region query so witness positions match native object indices.");
        }
        int[] indices = [finding.PositiveViaIndex, finding.NegativeViaIndex, finding.AggressorIndex];
        ImmutableArray<CopperObject> original = scan.Scene.Copper.RequireAvailable();
        ImmutableArray<CopperObject> fresh = geometry.Copper.RequireComplete();
        // Two-phase witness match. The cheap key is a necessary condition of
        // full-signature equality: every key field (Kind, NetName, Layer,
        // Bounds) is serialized verbatim into the signature under the same
        // equality, so an object whose key differs cannot signature-match.
        // Grouping the fresh objects once and comparing signatures only
        // inside the expected group therefore yields exactly the same match
        // count — and the same accept/reject/ambiguity outcome — as scanning
        // every object per witness.
        var groups = new Dictionary<(CopperKind Kind, string? Net, string? Layer, DesignBounds Bounds), List<int>>();
        for (int candidate = 0; candidate < fresh.Length; candidate++)
        {
            CopperObject item = fresh[candidate];
            var key = (item.Kind, item.NetName, item.Layer?.Value, item.Bounds);
            if (!groups.TryGetValue(key, out List<int>? group))
            {
                group = new List<int>(1);
                groups.Add(key, group);
            }
            group.Add(candidate);
        }
        int[] matched = new int[indices.Length];
        int witness = 0;
        foreach (int index in indices)
        {
            if ((uint)index >= (uint)original.Length)
            {
                throw new InvalidDataException("The finding's capture-local witness is no longer valid.");
            }
            CopperObject expected = original[index];
            // Compare through the captured object's acquired-field mask: a via whose
            // pad measurements were not requested carries Pads=[] by design, while a
            // fresh read returns full pads. Mask pad values on both sides only when
            // the expected via explicitly says they were not requested.
            bool includePads = expected.Via?.Analysis?.PadMeasurementsRequested != false;
            string signature = Signature(expected, query.Layers, includePads);
            var expectedKey = (expected.Kind, expected.NetName, expected.Layer?.Value, expected.Bounds);
            int matches = 0;
            int position = -1;
            if (groups.TryGetValue(expectedKey, out List<int>? candidates))
            {
                foreach (int candidate in candidates)
                {
                    if (Signature(fresh[candidate], query.Layers, includePads) == signature)
                    {
                        matches++;
                        position = candidate;
                    }
                }
            }
            if (matches != 1)
            {
                throw new InvalidDataException("A finding object changed, disappeared, or is ambiguous. Navigation was not dispatched; run the analysis again.");
            }
            matched[witness++] = position;
        }
        return matched;
    }

    private static string IncompleteGeometryMessage(DesignScene geometry)
    {
        IEnumerable<string> copperReasons = geometry.CopperScope.Details
            .Where(item => !item.IsComplete)
            .SelectMany(item => item.Reasons.Select(reason => $"{item.Kind}: {reason}"));
        IEnumerable<string> familyReasons = new[]
        {
            geometry.Coverage[DataFamily.Copper],
            geometry.Coverage[DataFamily.Layers],
        }
            .Where(item => !item.IsComplete)
            .SelectMany(item => item.Reasons);
        string[] reasons = copperReasons
            .Concat(familyReasons)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();
        string detail = reasons.Length == 0
            ? "The native provider did not supply complete Copper and Layers coverage."
            : string.Join("; ", reasons);
        string? unpouredLayer = UnpouredLayer(reasons);
        if (unpouredLayer is not null)
        {
            string where = unpouredLayer.Length == 0 ? "a copper shape" : "a copper shape on " + unpouredLayer;
            return "Navigation unavailable: " + where + " has no poured geometry (dynamic shape needs repour). " +
                "Repour dynamic shapes in Allegro and retry this finding; " +
                "findings without unpoured copper are unaffected. " +
                "Detail (fresh Engine region incomplete): " + detail;
        }
        return "Navigation unavailable because the fresh Engine region is incomplete. " + detail;
    }

    private static string? UnpouredLayer(string[] reasons)
    {
        const string marker = "surface_unavailable:";
        foreach (string reason in reasons)
        {
            int start = reason.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }
            string tail = reason.Substring(start + marker.Length);
            int end = tail.IndexOfAny([':', ';']);
            string layer = (end < 0 ? tail : tail.Substring(0, end)).Trim();
            return layer;
        }
        return null;
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

    private static string Signature(CopperObject item, IReadOnlyList<LayerId> layers, bool includePads)
    {
        ViaSpan? via = item.Via;
        ViaAnalysisEvidence? evidence = via?.Analysis;
        object? centerline = item.Centerline switch
        {
            LineGeometry line => new { Kind = "line", line.Start, line.End },
            ArcGeometry arc => new { Kind = "arc", arc.Start, arc.End, arc.Center, arc.Clockwise, arc.FullCircle },
            _ => null
        };
        object? pads = !includePads ? null : evidence?.Pads.Where(pad => layers.Contains(pad.RequestedLayer))
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
            WidthMils = item.Width?.Mils,
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
