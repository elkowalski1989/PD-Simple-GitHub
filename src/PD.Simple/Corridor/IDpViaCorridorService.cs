using CircuitHub.AllegroBridge.Engine.Live;
using PD.PcbTools;

namespace PD.Simple.Corridor;

public sealed record DpViaCorridorOptions(decimal MarginMils, string ModuleFilter, bool IncludeUnused);

/// <summary>A verified Engine analysis and the live document identity required for navigation.</summary>
public sealed record DpViaCorridorAnalysis(
    WorkspaceDocumentIdentity Document,
    DpViaCorridorResult Result)
{
    internal CorridorScan? ManagedScan { get; init; }
    internal LiveDesignScene? LiveScene { get; init; }

    public bool IsCurrentFor(WorkspaceDocumentIdentity? document) =>
        document == Document;
}

public interface IDpViaCorridorService
{
    Task<DpViaCorridorAnalysis> AnalyzeAsync(DpViaCorridorOptions options, string reportPath,
        CancellationToken cancellationToken = default);

    Task<DpViaCorridorZoomResult> NavigateAsync(DpViaCorridorAnalysis analysis,
        DpViaCorridorFinding finding, CancellationToken cancellationToken = default);
}
