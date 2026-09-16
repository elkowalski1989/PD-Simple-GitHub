using System.Collections.Immutable;
using System.Globalization;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.PcbTools;

public sealed record CorridorOptions(double MarginMils, string? ModuleName, bool IncludeUnused);

public sealed record CorridorFinding(
    string Id, string PairName, string AggressorNet, string ObjectType, string Layer,
    string Category, string Risk, DesignPoint P, DesignPoint N,
    DesignPoint Intrusion, double DistanceMils, double HalfWidthMils, double HalfLengthMils,
    int PositiveViaIndex, int NegativeViaIndex, int AggressorIndex, string WidthSourceLayer);

/// <summary>
/// Managed screening of one immutable Engine scene. This retains the reference
/// checker's naming, greedy pairing, centerline/chord and shape-box policies.
/// It is not copper-clearance or signal-integrity simulation. Missing required
/// facts are visible in CoverageWarnings and never establish a clear result.
/// </summary>
public sealed record CorridorScan(
    DesignScene Scene, CorridorOptions Options, int PairCount, int CorridorCount,
    IReadOnlyList<CorridorFinding> Findings, IReadOnlyList<string> CoverageWarnings)
{
    public bool HasCompleteInputs => CoverageWarnings.Count == 0;
}

public static class CorridorAnalyzer
{
    private static readonly CopperKind[] RequiredCopperKinds =
        [CopperKind.Trace, CopperKind.Via, CopperKind.Shape];

    private static readonly ImmutableArray<DataFamily> RequiredFamilies =
    [
        DataFamily.Nets,
        DataFamily.Modules,
        DataFamily.Layers,
        DataFamily.Copper,
    ];

    public const string Algorithm = "reference-screening-v1";
    public const string Limitations = "Screening, not a clearance or SI simulation. Pair discovery uses the tool's _P/_N convention, " +
        "not declared Engine pair metadata. Nearest-via pairing, first-target-layer pad width, native-unit padding, " +
        "arc chords, centerline/center-point tests and shape bounding boxes retain reference-policy approximations.";

    /// <summary>
    /// Requests one coherent whole-board scene containing only the families
    /// consumed by corridor screening. Trace, via and shape geometry retain the
    /// required pad dimensions; pin copper and unrelated metadata are not acquired.
    /// </summary>
    public static SceneQuery CreateSceneQuery(string? moduleName = null)
    {
        if (moduleName is not null &&
            (string.IsNullOrWhiteSpace(moduleName) ||
                moduleName.Length > 64 ||
                moduleName.Any(char.IsControl)))
        {
            throw new ArgumentException("A module name must be nonempty bounded text.", nameof(moduleName));
        }

        return SceneQuery.CompleteBoard(includeContours: false) with
        {
            Families = RequiredFamilies,
            CopperKinds = RequiredCopperKinds.ToImmutableArray(),
            ViaPadMeasurements =
                ViaPadMeasurementSelection.MatchingNetNameSuffixes("_P", "_N"),
            Module = moduleName,
        };
    }

    public static CorridorScan Analyze(DesignScene scene, CorridorOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(options);
        if (scene.Document.Kind != DocumentKind.PcbBoard)
        {
            throw new ArgumentException("Corridor screening requires a PCB board scene.", nameof(scene));
        }
        if (!double.IsFinite(options.MarginMils) || options.MarginMils is < 0 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (scene.Document.NativeUnits is not ("mils" or "millimeters"))
        {
            throw new InvalidDataException("The reference screening mode requires mils or millimeter native units and canonical Engine mil data.");
        }

        double scale = NativeScale(scene.Document.NativeUnits);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        FamilyCoverage copperCoverage = scene.Coverage[DataFamily.Copper];
        if (copperCoverage.Availability != DataAvailability.Available)
        {
            throw new InvalidOperationException("Copper was not available in the Engine scene; no corridor analysis was run.");
        }
        CopperKindCoverage[] requiredCoverage = RequiredCopperKinds
            .Select(kind => scene.CopperScope.Details.SingleOrDefault(item => item.Kind == kind))
            .Where(static item => item is not null)
            .Cast<CopperKindCoverage>()
            .ToArray();
        string[] irrelevantReasons = scene.CopperScope.Details
            .Where(item => !RequiredCopperKinds.Contains(item.Kind))
            .SelectMany(static item => item.Reasons)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] requiredReasons = requiredCoverage
            .SelectMany(static item => item.Reasons)
            .Concat(copperCoverage.Reasons.Where(reason =>
                !irrelevantReasons.Contains(reason, StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        bool requiredCoverageComplete =
            requiredCoverage.Length == RequiredCopperKinds.Length &&
            requiredCoverage.All(static item => item.IsComplete);
        bool unexplainedFamilyPartial =
            !copperCoverage.IsComplete &&
            copperCoverage.Reasons.Length == 0;
        if (!requiredCoverageComplete || unexplainedFamilyPartial)
        {
            warnings.Add(
                "Engine trace, via, or shape coverage is partial for this capture. " +
                "No clear conclusion is permitted.");
        }
        foreach (string reason in requiredReasons)
        {
            warnings.Add($"Engine capture reports unavailable data: {reason}. No clear conclusion is permitted.");
        }

        var nets = scene.Nets.RequireComplete().ToDictionary(net => net.Name, StringComparer.OrdinalIgnoreCase);
        var modules = scene.Modules.RequireComplete();
        var module = string.IsNullOrWhiteSpace(options.ModuleName) ? null : modules.SingleOrDefault(item =>
            string.Equals(item.Name, options.ModuleName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(options.ModuleName) && module is null)
        {
            throw new InvalidOperationException($"Module '{options.ModuleName}' was not found; no analysis was run.");
        }
        string[] checkLayers = scene.Layers.RequireComplete().Where(layer => !layer.IsNegative &&
            BareLayer(layer.Id.Value) is not ("TOP" or "BOTTOM")).Select(layer => layer.Id.Value).ToArray();
        var indexed = scene.Copper.RequireAvailable().Select((item, index) => new IndexedObject(index, item)).ToArray();
        var viasByNet = indexed.Where(item => item.Object.Via is not null && item.Object.NetName is not null)
            .GroupBy(item => item.Object.NetName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Reverse().ToArray(), StringComparer.OrdinalIgnoreCase);
        var spatial = indexed.OrderBy(item => item.Object.Bounds.Minimum.X).ToArray();
        var seen = new HashSet<int>();
        var all = new List<CorridorFinding>();
        int pairCount = 0;
        int corridorCount = 0;

        foreach (NetObject positiveNet in scene.Nets.RequireComplete())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!positiveNet.Name.EndsWith("_P", StringComparison.OrdinalIgnoreCase) || positiveNet.Name.Length <= 2)
            {
                continue;
            }
            string pairName = positiveNet.Name[..^2];
            if (!nets.TryGetValue(pairName + "_N", out NetObject? negativeNet) ||
                !options.IncludeUnused && IsUnusedPair(pairName))
            {
                continue;
            }
            IndexedObject[] positive = SelectVias(viasByNet, positiveNet.Name, module?.Bounds);
            IndexedObject[] negative = SelectVias(viasByNet, negativeNet.Name, module?.Bounds);
            if (positive.Length == 0 || negative.Length == 0)
            {
                continue;
            }
            pairCount++;
            var pairs = PairVias(positive, negative);
            corridorCount += pairs.Count;
            var pairFindings = new List<CorridorFinding>();
            foreach (var pair in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var found = InspectPair(scene, options, pairName, pair.Positive, pair.Negative,
                    positiveNet.Name, negativeNet.Name, checkLayers, spatial, seen, warnings, scale, cancellationToken);
                foreach (CorridorFinding finding in found)
                {
                    pairFindings.Insert(0, finding);
                    seen.Add(finding.AggressorIndex);
                }
            }
            all.InsertRange(0, pairFindings);
        }

        var deduplicated = new Dictionary<(string Aggressor, string Pair, string Layer, string Positive, string Negative), CorridorFinding>();
        foreach (CorridorFinding finding in all)
        {
            var key = (finding.AggressorNet, finding.PairName, finding.Layer,
                NativeKey(finding.P, scale), NativeKey(finding.N, scale));
            if (!deduplicated.TryGetValue(key, out CorridorFinding? previous) || finding.DistanceMils < previous.DistanceMils)
            {
                deduplicated[key] = finding;
            }
        }
        CorridorFinding[] results = deduplicated.Values.OrderBy(item => RiskOrder(item.Risk))
            .ThenBy(item => item.DistanceMils)
            .Select((item, index) => item with { Id = $"crossing-{index + 1}" }).ToArray();
        return new(scene, options, pairCount, corridorCount, Array.AsReadOnly(results),
            Array.AsReadOnly(warnings.Order(StringComparer.Ordinal).ToArray()));
    }

    private static List<CorridorFinding> InspectPair(
        DesignScene scene, CorridorOptions options, string pairName,
        IndexedObject positive, IndexedObject negative, string positiveNet, string negativeNet,
        string[] checkLayers, IndexedObject[] spatial, HashSet<int> seen, HashSet<string> warnings,
        double scale, CancellationToken cancellationToken)
    {
        ViaSpan p = positive.Object.Via!;
        ViaSpan n = negative.Object.Via!;
        double span = CorridorGeometry.Distance(p.Position, n.Position);
        var findings = new List<CorridorFinding>();
        if (span * scale < 0.01)
        {
            return findings;
        }
        string[] pLayers = Layers(p, warnings, positive.Index);
        string[] nLayers = Layers(n, warnings, negative.Index);
        string[] targets = pLayers.Where(layer => nLayers.Contains(layer, StringComparer.OrdinalIgnoreCase) &&
            checkLayers.Contains(layer, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (targets.Length == 0)
        {
            return findings;
        }
        string widthLayer = targets[0];
        if (new[] { p, n }.Any(via =>
            via.Analysis is { PadMeasurementsRequested: false }))
        {
            warnings.Add(
                $"Pad and antipad measurements were not requested for one or both subject vias in pair {pairName}; its width cannot establish a clear result.");
        }
        double? anti = MaximumRadius(p, n, widthLayer, "antipad");
        double? pad = MaximumRadius(p, n, widthLayer, "regular");
        if (new[] { p, n }.Any(via => via.Analysis is null || !via.Analysis.Pads.Any(measurement => measurement.Type == "antipad" &&
            string.Equals(measurement.RequestedLayer.Value, widthLayer, StringComparison.OrdinalIgnoreCase))))
        {
            warnings.Add($"One or both antipad measurements are unavailable for pair {pairName} on {widthLayer}; the observed/fallback width cannot establish a clear result.");
        }
        if (anti is null && pad is null)
        {
            warnings.Add($"Pad and antipad measurements are unavailable for pair {pairName} on {widthLayer}; its width uses the reference estimate.");
        }
        double minimumWidth = scene.Document.NativeUnits == "millimeters" ? 0.025 / scale : 1;
        double halfWidth = Math.Max(minimumWidth, (anti ?? pad ?? span * 0.3) + options.MarginMils);
        double halfLength = span / 2;
        DesignPoint center = CorridorGeometry.Interpolate(p.Position, n.Position, 0.5);
        var box = new CorridorBox(center, (double)(n.Position.X - p.Position.X) / span,
            (double)(n.Position.Y - p.Position.Y) / span, halfLength, halfWidth);
        // Deliberately retains the old one-native-unit broad-phase policy.
        decimal extent = checked((decimal)(halfLength + halfWidth + 1 / scale));
        var bounds = new DesignBounds(new(center.X - extent, center.Y - extent), new(center.X + extent, center.Y + extent));
        var candidates = new List<IndexedObject>();
        foreach (IndexedObject item in spatial)
        {
            if (item.Object.Bounds.Minimum.X > bounds.Maximum.X)
            {
                break;
            }
            if (CorridorGeometry.Intersects(item.Object.Bounds, bounds))
            {
                candidates.Add(item);
            }
        }
        // Retain the reference's segment, via, shape processing order. Array
        // indices are capture-local witnesses, never native database identifiers.
        candidates.Sort((left, right) =>
        {
            int byKind = ObjectOrder(left.Object).CompareTo(ObjectOrder(right.Object));
            return byKind != 0 ? byKind : left.Index.CompareTo(right.Index);
        });
        foreach (string layer in targets)
        {
            foreach (IndexedObject candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopperObject item = candidate.Object;
                string net = item.NetName?.ToUpperInvariant() ?? "";
                if (seen.Contains(candidate.Index) || string.Equals(net, positiveNet, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(net, negativeNet, StringComparison.OrdinalIgnoreCase) || SignalClassifier.IsIgnoredAggressor(net))
                {
                    continue;
                }
                double? distance = null;
                DesignPoint intrusion = center;
                string kind = item.Kind.ToString().ToLowerInvariant();
                if (TraceEndpoints(item) is { } segment && item.Layer is { } segmentLayer &&
                    string.Equals(segmentLayer.Value, layer, StringComparison.OrdinalIgnoreCase))
                {
                    // Reference mode intentionally uses an arc's endpoint chord, not its curved copper boundary.
                    if (box.Clip(segment.Start, segment.End, scale) is { } clipped)
                    {
                        DesignPoint first = CorridorGeometry.Interpolate(segment.Start, segment.End, clipped.Entry);
                        DesignPoint last = CorridorGeometry.Interpolate(segment.Start, segment.End, clipped.Exit);
                        intrusion = CorridorGeometry.Interpolate(first, last, 0.5);
                        distance = new[] { first, last, intrusion }.Min(point =>
                            CorridorGeometry.DistanceToSegment(point, p.Position, n.Position, scale));
                        kind = "cline_segment";
                    }
                }
                else if (item.Via is { } via && box.Contains(via.Position) &&
                    Layers(via, warnings, candidate.Index).Contains(layer, StringComparer.OrdinalIgnoreCase))
                {
                    intrusion = via.Position;
                    distance = CorridorGeometry.DistanceToSegment(intrusion, p.Position, n.Position, scale);
                }
                else if (item.Kind == CopperKind.Shape && net.Length > 0 &&
                    item.Layer is { } shapeLayer && string.Equals(shapeLayer.Value, layer, StringComparison.OrdinalIgnoreCase) &&
                    !CorridorGeometry.Contains(item.Bounds, bounds))
                {
                    if (item.FillOutOfDate != false)
                    {
                        warnings.Add($"Shape fill freshness is not established for object {candidate.Index + 1}; review is required.");
                    }
                    var closest = new DesignPoint(Math.Clamp(center.X, item.Bounds.Minimum.X, item.Bounds.Maximum.X),
                        Math.Clamp(center.Y, item.Bounds.Minimum.Y, item.Bounds.Maximum.Y));
                    distance = CorridorGeometry.DistanceToSegment(closest, p.Position, n.Position, scale);
                }
                if (distance is not null)
                {
                    var classification = SignalClassifier.Default.Classify(net);
                    findings.Insert(0, new("", pairName, net, kind, layer, classification.Category, classification.Risk,
                        p.Position, n.Position, intrusion, distance.Value, halfWidth, halfLength,
                        positive.Index, negative.Index, candidate.Index, widthLayer));
                }
            }
        }
        return findings;
    }

    private static string[] Layers(ViaSpan via, HashSet<string> warnings, int index)
    {
        ViaAnalysisEvidence? analysis = via.Analysis;
        if (analysis is { ActiveLayersAvailable: true } && via.BackdrillStatus is "current" or "not_started")
        {
            return via.ActiveLayers.Select(layer => layer.Value).ToArray();
        }
        if (analysis?.BackdrillExclusion == "EXCLUDE_BOTH" || via.BackdrillStatus == "not_started")
        {
            return via.OriginalLayers.Select(layer => layer.Value).ToArray();
        }
        warnings.Add($"Backdrill/active layers are {via.BackdrillStatus} for via object {index + 1}; original layers are screened conservatively, not certified clear.");
        return via.OriginalLayers.Select(layer => layer.Value).ToArray();
    }

    private static double? MaximumRadius(ViaSpan positive, ViaSpan negative, string layer, string type)
    {
        ViaPadMeasurement[] pads = new[] { positive, negative }.Where(via => via.Analysis is not null)
            .SelectMany(via => via.Analysis!.Pads).Where(pad => pad.Type == type &&
                string.Equals(pad.RequestedLayer.Value, layer, StringComparison.OrdinalIgnoreCase)).ToArray();
        return pads.Length == 0 ? null : pads.Max(pad => (double)Math.Max(pad.ExtentX.Mils, pad.ExtentY.Mils) / 2);
    }

    private static IndexedObject[] SelectVias(Dictionary<string, IndexedObject[]> vias, string net, DesignBounds? bounds)
    {
        return !vias.TryGetValue(net, out IndexedObject[]? values) ? [] : values.Where(value => bounds is null ||
            CorridorGeometry.Contains(bounds.Value, value.Object.Via!.Position)).ToArray();
    }

    private static List<(IndexedObject Positive, IndexedObject Negative)> PairVias(IndexedObject[] positive, IndexedObject[] negative)
    {
        var remaining = negative.ToList();
        var result = new List<(IndexedObject, IndexedObject)>();
        foreach (IndexedObject p in positive)
        {
            IndexedObject? closest = null;
            double distance = double.PositiveInfinity;
            foreach (IndexedObject n in remaining)
            {
                double candidate = CorridorGeometry.Distance(p.Object.Via!.Position, n.Object.Via!.Position);
                if (candidate < distance)
                {
                    closest = n;
                    distance = candidate;
                }
            }
            if (closest is not null && distance < 500)
            {
                result.Insert(0, (p, closest));
                remaining.Remove(closest);
            }
        }
        return result;
    }

    private static (DesignPoint Start, DesignPoint End)? TraceEndpoints(CopperObject item) => item.Centerline switch
    {
        LineGeometry line => (line.Start, line.End),
        ArcGeometry arc => (arc.Start, arc.End),
        _ => null
    };

    private static double NativeScale(string units) => units switch
    {
        "mils" => 1,
        "millimeters" => 0.0254,
        _ => throw new InvalidDataException("Unsupported native coordinate units.")
    };

    private static string NativeKey(DesignPoint point, double scale) => string.Create(CultureInfo.InvariantCulture,
        $"{(double)point.X * scale:F2},{(double)point.Y * scale:F2}");

    private static bool IsUnusedPair(string name)
    {
        string upper = name.ToUpperInvariant();
        return upper.StartsWith("NC_", StringComparison.Ordinal) || upper.StartsWith("NU_", StringComparison.Ordinal) ||
            upper.Length > 2 && upper.StartsWith("NC", StringComparison.Ordinal) && char.IsAsciiDigit(upper[2]);
    }

    private static string BareLayer(string layer) => layer.Replace("ETCH/", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
    private static int ObjectOrder(CopperObject item) => item.Kind == CopperKind.Trace ? 0 : item.Kind == CopperKind.Via ? 1 : 2;
    private static int RiskOrder(string risk) => risk switch { "CRITICAL" => 0, "MEDIUM" => 1, _ => 2 };
    private sealed record IndexedObject(int Index, CopperObject Object);
}
