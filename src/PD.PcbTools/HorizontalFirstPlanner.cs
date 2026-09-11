using CircuitHub.AllegroBridge;

namespace PD.PcbTools;

/// <summary>Existing clicked-coordinate routing policy, not a clearance-aware autorouter.</summary>
public static class HorizontalFirstPlanner
{
    public static AllegroPcbTracePlan Plan(AllegroPcbEndpoints endpoints, double widthMils, string layer)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!double.IsFinite(widthMils) || widthMils is < 0.1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(widthMils));
        }
        double nativeScale = NativeScale(endpoints.NativeUnits);
        var first = endpoints.First.Position;
        var second = endpoints.Second.Position;
        bool sameX = Math.Truncate(first.X * nativeScale * 10000) == Math.Truncate(second.X * nativeScale * 10000);
        bool sameY = Math.Truncate(first.Y * nativeScale * 10000) == Math.Truncate(second.Y * nativeScale * 10000);
        AllegroPcbPoint[] points = sameX || sameY ? [first, second] : [first, new(second.X, first.Y), second];
        string? firstNet = endpoints.First.Net;
        string? secondNet = endpoints.Second.Net;
        string? net = firstNet is null ? secondNet : secondNet is null || firstNet == secondNet ? firstNet : null;
        return new(points, widthMils, layer, net);
    }

    public static string SelectLayer(AllegroPcbCatalog catalog)
    {
        var visible = catalog.Layers.RequireAvailable().Where(layer => layer.IsVisible).ToArray();
        var active = visible.Where(layer => layer.IsActive).ToArray();
        if (active.Length > 1)
        {
            throw new InvalidDataException("More than one active visible ETCH layer was reported.");
        }
        return active.SingleOrDefault()?.Name ?? visible.FirstOrDefault()?.Name
            ?? throw new InvalidOperationException("Make an ETCH layer visible in Allegro before routing.");
    }

    internal static double NativeScale(string units) => units switch
    {
        "mils" => 1,
        "millimeters" => 0.0254,
        "inches" => 0.001,
        _ => throw new InvalidDataException("Unsupported native coordinate units.")
    };
}
