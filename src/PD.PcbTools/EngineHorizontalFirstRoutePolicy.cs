using PcbEngine.Design;
using Length = PcbEngine.Length;
using PcbEngine.Advanced;

namespace PD.PcbTools;

/// <summary>
/// PD Simple's existing point-to-point routing policy expressed only in Engine
/// types. It is intentionally not a clearance-aware autorouter.
/// </summary>
public static class EngineHorizontalFirstRoutePolicy
{
    public static RoutingLayer SelectLayer(RoutingLayerCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        RoutingLayer[] visible = catalog.Layers.Where(layer => layer.IsVisible).ToArray();
        RoutingLayer[] active = visible.Where(layer => layer.IsActive).ToArray();
        if (active.Length > 1)
        {
            throw new InvalidDataException("More than one active visible ETCH layer was reported.");
        }
        return active.SingleOrDefault() ?? visible.FirstOrDefault()
            ?? throw new InvalidOperationException("Make an ETCH layer visible in Allegro before routing.");
    }

    public static TracePlan Plan(
        PickedEndpoints endpoints,
        decimal widthMils,
        RoutingLayer layer)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(layer);
        if (widthMils is < 0.1m or > 10_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(widthMils));
        }
        return Plan(TraceEndpoints.FromPicked(endpoints), widthMils, layer);
    }

    /// <summary>Pure policy overload used by tests/offline tooling; no edit authority is implied.</summary>
    public static TracePlan Plan(
        TraceEndpoints endpoints,
        decimal widthMils,
        RoutingLayer layer)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(layer);
        if (widthMils is < 0.1m or > 10_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(widthMils));
        }
        return TracePlanning.HorizontalFirst(
            endpoints,
            new Length(widthMils),
            layer.Id);
    }
}
