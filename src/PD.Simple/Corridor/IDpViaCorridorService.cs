using CircuitHub.AllegroBridge.Engine.Live;
using PD.PcbTools;

namespace PD.Simple.Corridor;

public sealed record DpViaCorridorOptions(decimal MarginMils, string ModuleFilter, bool IncludeUnused);

/// <summary>A verified Engine analysis and the live document identity required for navigation.</summary>
public sealed record DpViaCorridorAnalysis(WorkspaceDocumentIdentity Document, DpViaCorridorResult Result,
    long CatalogGeneration)
{
    internal CorridorScan? ManagedScan { get; init; }

    // Native navigation also binds its retained result to the catalog generation.
    // Rebinding commands cannot renew that historical analysis.
    public bool IsCurrentFor(string sessionId, long boardGeneration, long catalogGeneration) =>
        Document.SessionId == sessionId && Document.BoardGeneration == boardGeneration &&
        CatalogGeneration == catalogGeneration;
}

public interface IDpViaCorridorService
{
    Task<DpViaCorridorAnalysis> AnalyzeAsync(DpViaCorridorOptions options, string reportPath,
        CancellationToken cancellationToken = default);

    Task<DpViaCorridorZoomResult> NavigateAsync(DpViaCorridorAnalysis analysis,
        DpViaCorridorFinding finding, CancellationToken cancellationToken = default);
}
