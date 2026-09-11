using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;

namespace PD.PcbTools;

/// <summary>
/// PD Simple's existing point-to-point routing policy expressed only in Engine
/// types. It is intentionally not a clearance-aware autorouter.
/// </summary>
public static class EngineHorizontalFirstRoutePolicy
{
    public static EngineRoutingLayer SelectLayer(EngineRoutingLayerCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        EngineRoutingLayer[] visible = catalog.Layers.Where(layer => layer.IsVisible).ToArray();
        EngineRoutingLayer[] active = visible.Where(layer => layer.IsActive).ToArray();
        if (active.Length > 1)
        {
            throw new InvalidDataException("More than one active visible ETCH layer was reported.");
        }
        return active.SingleOrDefault() ?? visible.FirstOrDefault()
            ?? throw new InvalidOperationException("Make an ETCH layer visible in Allegro before routing.");
    }

    public static EngineTracePlan Plan(
        EnginePickedEndpoints endpoints,
        decimal widthMils,
        EngineRoutingLayer layer)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(layer);
        if (widthMils is < 0.1m or > 10_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(widthMils));
        }
        return Plan(EngineTraceEndpoints.FromPicked(endpoints), widthMils, layer);
    }

    /// <summary>Pure policy overload used by tests/offline tooling; no edit authority is implied.</summary>
    public static EngineTracePlan Plan(
        EngineTraceEndpoints endpoints,
        decimal widthMils,
        EngineRoutingLayer layer)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(layer);
        if (widthMils is < 0.1m or > 10_000m)
        {
            throw new ArgumentOutOfRangeException(nameof(widthMils));
        }
        return EngineTracePlanning.HorizontalFirst(
            endpoints,
            new Length(widthMils),
            layer.Id);
    }
}
