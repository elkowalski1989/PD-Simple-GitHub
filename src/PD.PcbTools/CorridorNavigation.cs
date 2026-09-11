using System.Text.Json;
using CircuitHub.AllegroBridge;

namespace PD.PcbTools;

/// <summary>Checks the computed finding against a fresh SDK read before guarded native navigation.</summary>
public static class CorridorNavigation
{
    public static AllegroPcbRegionQuery CreateQuery(CorridorScan scan, CorridorFinding finding)
    {
        RequireFinding(scan, finding);
        double padding = Math.Max(40, finding.HalfWidthMils * 2);
        double minX = Math.Min(finding.P.X, finding.N.X) - padding;
        double minY = Math.Min(finding.P.Y, finding.N.Y) - padding;
        double maxX = Math.Max(finding.P.X, finding.N.X) + padding;
        double maxY = Math.Max(finding.P.Y, finding.N.Y) + padding;
        string[] layers = new[] { finding.Layer, finding.WidthSourceLayer }.Distinct(StringComparer.Ordinal).ToArray();
        return new(new(new(minX, minY), new(maxX, maxY)), layers, 2048, 16384);
    }

    public static void ValidateFreshRead(CorridorScan scan, CorridorFinding finding, AllegroPcbRegionGeometry geometry)
    {
        RequireFinding(scan, finding);
        var query = CreateQuery(scan, finding);
        if (geometry.SessionId != scan.Inputs.SessionId || geometry.BoardGeneration != scan.Inputs.BoardGeneration ||
            geometry.NativeUnits != scan.Inputs.NativeUnits || geometry.NativePrecision != scan.Inputs.NativePrecision ||
            geometry.Truncated || geometry.MaximumContourVertices == 0 || geometry.Unavailable.Count > 0 ||
            geometry.Objects.Any(item => item.Unavailable.Count > 0) ||
            !query.Layers!.All(layer => geometry.Layers.Contains(layer, StringComparer.Ordinal)) ||
            !CorridorGeometry.Contains(geometry.Bounds, finding.P) ||
            !CorridorGeometry.Contains(geometry.Bounds, finding.N))
        {
            throw new InvalidDataException("Navigation requires complete fresh geometry for this exact board and finding. Run the analysis again if its context changed.");
        }
        int[] indices = [finding.PositiveViaIndex, finding.NegativeViaIndex, finding.AggressorIndex];
        foreach (int index in indices)
        {
            var expected = scan.Inputs.Objects[index];
            string signature = Signature(expected, query.Layers!);
            int matches = geometry.Objects.Count(item => Signature(item, query.Layers!) == signature);
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

    private static string Signature(AllegroPcbRegionObject item, IReadOnlyList<string> layers)
    {
        var via = item.Via;
        // Compare every scalar fact that affects this screening mode. Detailed
        // fresh contours are subsequently revalidated by the SDK's native owner.
        return JsonSerializer.Serialize(new
        {
            item.Kind, item.Net, item.Layer, item.Bounds, item.Segment, item.FillOutOfDate,
            Position = via?.Position,
            Padstack = via?.Padstack,
            IsThrough = via?.IsThrough,
            OriginalLayers = via?.OriginalLayers,
            ActiveLayers = via?.ActiveLayers,
            Backdrill = via?.Backdrill,
            Pads = via?.Pads.Where(pad => layers.Contains(pad.RequestedLayer, StringComparer.Ordinal))
                .OrderBy(pad => pad.RequestedLayer, StringComparer.Ordinal).ThenBy(pad => pad.Type, StringComparer.Ordinal).ToArray()
        });
    }
}
