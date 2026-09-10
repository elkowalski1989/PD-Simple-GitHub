using CircuitHub.AllegroBridge;

namespace PD.Simple.Corridor;

public sealed record DpViaCorridorOptions(decimal MarginMils, string ModuleFilter, bool IncludeUnused);

/// <summary>A verified analysis and the native publication identity required for navigation.</summary>
public sealed record DpViaCorridorAnalysis(AllegroSessionBinding Binding, DpViaCorridorResult Result,
    long CatalogGeneration)
{
    // Native navigation also binds its retained result to the catalog generation.
    // Rebinding commands cannot renew that historical analysis.
    public bool IsCurrentFor(string sessionId, long boardGeneration, long catalogGeneration) =>
        Binding.SessionId == sessionId && Binding.BoardGeneration == boardGeneration &&
        CatalogGeneration == catalogGeneration;
}

public interface IDpViaCorridorService
{
    Task<DpViaCorridorAnalysis> AnalyzeAsync(DpViaCorridorOptions options, string reportPath,
        CancellationToken cancellationToken = default);

    Task<DpViaCorridorZoomResult> NavigateAsync(DpViaCorridorAnalysis analysis,
        DpViaCorridorFinding finding, CancellationToken cancellationToken = default);
}
