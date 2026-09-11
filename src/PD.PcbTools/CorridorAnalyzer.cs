using System.Globalization;
using CircuitHub.AllegroBridge;

namespace PD.PcbTools;

public sealed record CorridorOptions(double MarginMils, string? ModuleName, bool IncludeUnused);

public sealed record CorridorFinding(
    string Id, string PairName, string AggressorNet, string ObjectType, string Layer,
    string Category, string Risk, AllegroPcbPoint P, AllegroPcbPoint N,
    AllegroPcbPoint Intrusion, double DistanceMils, double HalfWidthMils, double HalfLengthMils,
    int PositiveViaIndex, int NegativeViaIndex, int AggressorIndex, string WidthSourceLayer);

/// <summary>
/// Managed screening of one immutable SDK capture. This retains the reference
/// checker's naming, greedy pairing, centerline/chord and shape-box policies.
/// It is not copper-clearance or signal-integrity simulation. Missing required
/// facts are visible in CoverageWarnings and never establish a clear result.
/// </summary>
public sealed record CorridorScan(
    AllegroPcbBoardInputs Inputs, CorridorOptions Options, int PairCount, int CorridorCount,
    IReadOnlyList<CorridorFinding> Findings, IReadOnlyList<string> CoverageWarnings)
{
    public bool HasCompleteInputs => CoverageWarnings.Count == 0;
}

public static class CorridorAnalyzer
{
    public const string Algorithm = "reference-screening-v1";
    public const string Limitations = "Screening, not a clearance or SI simulation. Pair discovery uses the tool's _P/_N convention, " +
        "not declared SDK pair metadata. Nearest-via pairing, first-target-layer pad width, native-unit padding, " +
        "arc chords, centerline/center-point tests and shape bounding boxes retain reference-policy approximations.";

    public static CorridorScan Analyze(AllegroPcbBoardInputs inputs, CorridorOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(options.MarginMils) || options.MarginMils is < 0 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (inputs.Units != "mils" || inputs.NativeUnits is not ("mils" or "millimeters"))
        {
            throw new InvalidDataException("The reference screening mode requires mils or millimeter native units and canonical mil data.");
        }
        double scale = HorizontalFirstPlanner.NativeScale(inputs.NativeUnits);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (string unavailable in inputs.Unavailable.Where(value => value != "contours_not_requested"))
        {
            warnings.Add($"Native capture reports unavailable data: {unavailable}. No clear conclusion is permitted.");
        }
        var nets = inputs.Nets.ToDictionary(net => net.Name, StringComparer.OrdinalIgnoreCase);
        var module = string.IsNullOrWhiteSpace(options.ModuleName) ? null : inputs.Modules.SingleOrDefault(item =>
            string.Equals(item.Name, options.ModuleName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(options.ModuleName) && module is null)
        {
            throw new InvalidOperationException($"Module '{options.ModuleName}' was not found; no analysis was run.");
        }
        string[] checkLayers = inputs.Stackup.Where(layer => !layer.IsNegative &&
            BareLayer(layer.Name) is not ("TOP" or "BOTTOM")).Select(layer => layer.Name).ToArray();
        var indexed = inputs.Objects.Select((item, index) => new IndexedObject(index, item)).ToArray();
        var viasByNet = indexed.Where(item => item.Object.Via is not null && item.Object.Net is not null)
            .GroupBy(item => item.Object.Net!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Reverse().ToArray(), StringComparer.OrdinalIgnoreCase);
        var spatial = indexed.OrderBy(item => item.Object.Bounds.Minimum.X).ToArray();
        var seen = new HashSet<int>();
        var all = new List<CorridorFinding>();
        int pairCount = 0;
        int corridorCount = 0;

        foreach (var positiveNet in inputs.Nets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!positiveNet.Name.EndsWith("_P", StringComparison.OrdinalIgnoreCase) || positiveNet.Name.Length <= 2)
            {
                continue;
            }
            string pairName = positiveNet.Name[..^2];
            if (!nets.TryGetValue(pairName + "_N", out var negativeNet) ||
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
                var found = InspectPair(inputs, options, pairName, pair.Positive, pair.Negative,
                    positiveNet.Name, negativeNet.Name, checkLayers, spatial, seen, warnings, scale, cancellationToken);
                foreach (var finding in found)
                {
                    pairFindings.Insert(0, finding);
                    seen.Add(finding.AggressorIndex);
                }
            }
            all.InsertRange(0, pairFindings);
        }

        var deduplicated = new Dictionary<(string Aggressor, string Pair, string Layer, string Positive, string Negative), CorridorFinding>();
        foreach (var finding in all)
        {
            var key = (finding.AggressorNet, finding.PairName, finding.Layer,
                NativeKey(finding.P, scale), NativeKey(finding.N, scale));
            if (!deduplicated.TryGetValue(key, out var previous) || finding.DistanceMils < previous.DistanceMils)
            {
                deduplicated[key] = finding;
            }
        }
        CorridorFinding[] results = deduplicated.Values.OrderBy(item => RiskOrder(item.Risk))
            .ThenBy(item => item.DistanceMils)
            .Select((item, index) => item with { Id = $"crossing-{index + 1}" }).ToArray();
        return new(inputs, options, pairCount, corridorCount, Array.AsReadOnly(results),
            Array.AsReadOnly(warnings.Order(StringComparer.Ordinal).ToArray()));
    }

    private static List<CorridorFinding> InspectPair(
        AllegroPcbBoardInputs inputs, CorridorOptions options, string pairName,
        IndexedObject positive, IndexedObject negative, string positiveNet, string negativeNet,
        string[] checkLayers, IndexedObject[] spatial, HashSet<int> seen, HashSet<string> warnings,
        double scale, CancellationToken cancellationToken)
    {
        var p = positive.Object.Via!;
        var n = negative.Object.Via!;
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
        double? anti = MaximumRadius(p, n, widthLayer, "antipad");
        double? pad = MaximumRadius(p, n, widthLayer, "regular");
        if (new[] { p, n }.Any(via => !via.Pads.Any(measurement => measurement.Type == "antipad" &&
            string.Equals(measurement.RequestedLayer, widthLayer, StringComparison.OrdinalIgnoreCase))))
        {
            warnings.Add($"One or both antipad measurements are unavailable for pair {pairName} on {widthLayer}; the observed/fallback width cannot establish a clear result.");
        }
        if (anti is null && pad is null)
        {
            warnings.Add($"Pad and antipad measurements are unavailable for pair {pairName} on {widthLayer}; its width uses the reference estimate.");
        }
        double minimumWidth = inputs.NativeUnits == "millimeters" ? 0.025 / scale : 1;
        double halfWidth = Math.Max(minimumWidth, (anti ?? pad ?? span * 0.3) + options.MarginMils);
        double halfLength = span / 2;
        var center = CorridorGeometry.Interpolate(p.Position, n.Position, 0.5);
        var box = new CorridorBox(center, (n.Position.X - p.Position.X) / span,
            (n.Position.Y - p.Position.Y) / span, halfLength, halfWidth);
        // Deliberately retains the old one-native-unit broad-phase policy.
        double extent = halfLength + halfWidth + 1 / scale;
        var bounds = new AllegroPcbBounds(new(center.X - extent, center.Y - extent), new(center.X + extent, center.Y + extent));
        var candidates = new List<IndexedObject>();
        foreach (var item in spatial)
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
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = candidate.Object;
                string net = item.Net?.ToUpperInvariant() ?? "";
                if (seen.Contains(candidate.Index) || string.Equals(net, positiveNet, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(net, negativeNet, StringComparison.OrdinalIgnoreCase) || SignalClassifier.IsIgnoredAggressor(net))
                {
                    continue;
                }
                double? distance = null;
                AllegroPcbPoint intrusion = center;
                string kind = item.Kind;
                if (item.Segment is { } segment && string.Equals(item.Layer, layer, StringComparison.OrdinalIgnoreCase))
                {
                    // Reference mode intentionally uses an arc's endpoint chord, not its curved copper boundary.
                    if (box.Clip(segment.Start, segment.End, scale) is { } clipped)
                    {
                        var first = CorridorGeometry.Interpolate(segment.Start, segment.End, clipped.Entry);
                        var last = CorridorGeometry.Interpolate(segment.Start, segment.End, clipped.Exit);
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
                else if (item.Kind == "shape" && net.Length > 0 &&
                    string.Equals(item.Layer, layer, StringComparison.OrdinalIgnoreCase) &&
                    !CorridorGeometry.Contains(item.Bounds, bounds))
                {
                    if (item.FillOutOfDate != false)
                    {
                        warnings.Add($"Shape fill freshness is not established for object {candidate.Index + 1}; review is required.");
                    }
                    var closest = new AllegroPcbPoint(Math.Clamp(center.X, item.Bounds.Minimum.X, item.Bounds.Maximum.X),
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

    private static string[] Layers(AllegroPcbRegionVia via, HashSet<string> warnings, int index)
    {
        if (via.ActiveLayers is not null && via.Backdrill.Status is "current" or "not_started")
        {
            return via.ActiveLayers.ToArray();
        }
        if (via.Backdrill.Exclusion == "EXCLUDE_BOTH" || via.Backdrill.Status == "not_started")
        {
            return via.OriginalLayers.ToArray();
        }
        warnings.Add($"Backdrill/active layers are {via.Backdrill.Status} for via object {index + 1}; original layers are screened conservatively, not certified clear.");
        return via.OriginalLayers.ToArray();
    }

    private static double? MaximumRadius(AllegroPcbRegionVia positive, AllegroPcbRegionVia negative,
        string layer, string type)
    {
        var pads = positive.Pads.Concat(negative.Pads).Where(pad => pad.Type == type &&
            string.Equals(pad.RequestedLayer, layer, StringComparison.OrdinalIgnoreCase)).ToArray();
        return pads.Length == 0 ? null : pads.Max(pad => Math.Max(pad.ExtentX, pad.ExtentY) / 2);
    }

    private static IndexedObject[] SelectVias(Dictionary<string, IndexedObject[]> vias, string net, AllegroPcbBounds? bounds)
    {
        return !vias.TryGetValue(net, out var values) ? [] : values.Where(value => bounds is null ||
            CorridorGeometry.Contains(bounds, value.Object.Via!.Position)).ToArray();
    }

    private static List<(IndexedObject Positive, IndexedObject Negative)> PairVias(IndexedObject[] positive, IndexedObject[] negative)
    {
        var remaining = negative.ToList();
        var result = new List<(IndexedObject, IndexedObject)>();
        foreach (var p in positive)
        {
            IndexedObject? closest = null;
            double distance = double.PositiveInfinity;
            foreach (var n in remaining)
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

    private static string NativeKey(AllegroPcbPoint point, double scale) => string.Create(CultureInfo.InvariantCulture,
        $"{point.X * scale:F2},{point.Y * scale:F2}");

    private static bool IsUnusedPair(string name)
    {
        string upper = name.ToUpperInvariant();
        return upper.StartsWith("NC_", StringComparison.Ordinal) || upper.StartsWith("NU_", StringComparison.Ordinal) ||
            upper.Length > 2 && upper.StartsWith("NC", StringComparison.Ordinal) && char.IsAsciiDigit(upper[2]);
    }

    private static string BareLayer(string layer) => layer.Replace("ETCH/", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
    private static int ObjectOrder(AllegroPcbRegionObject item) => item.Segment is not null ? 0 : item.Via is not null ? 1 : 2;
    private static int RiskOrder(string risk) => risk switch { "CRITICAL" => 0, "MEDIUM" => 1, _ => 2 };
    private sealed record IndexedObject(int Index, AllegroPcbRegionObject Object);
}
