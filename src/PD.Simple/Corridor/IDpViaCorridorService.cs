using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

namespace PD.Simple.Corridor;

public sealed record DpViaCorridorOptions(decimal MarginMils, string ModuleFilter, bool IncludeUnused);

internal sealed record DpViaCorridorTimings(
    long AcquisitionMilliseconds,
    long AnalysisMilliseconds,
    long ReportMilliseconds,
    long? NativeCommandMilliseconds = null,
    long? SnapshotTransferMilliseconds = null,
    long? NativeReleaseMilliseconds = null,
    long? SnapshotReplayAndConversionMilliseconds = null,
    long? SceneConstructionMilliseconds = null,
    long? SnapshotDisposalMilliseconds = null,
    IReadOnlyList<EngineSceneAcquisitionResource>? NativeResources = null);

/// <summary>Captured browsing proves witness identity at navigation time; revalidation rechecks selected witnesses against a fresh region.</summary>
public enum DpViaCorridorNavigationMode
{
    Browse,
    Revalidate,
}

/// <summary>
/// The caller's identity for one navigation operation. The service echoes
/// it on the returned outcome so the caller can attribute completions to
/// the request that made them.
/// </summary>
public sealed record DpViaCorridorNavigationRequest(Guid OperationId, string Origin);

public sealed record DpViaCorridorNavigationPhases(
    long RegionMilliseconds,
    long WitnessValidationMilliseconds,
    long ZoomMilliseconds,
    long? NativeReadMilliseconds = null,
    long? RegionDecodingMilliseconds = null,
    long? RegionConversionMilliseconds = null,
    long? NativeEnumerationMilliseconds = null,
    long? NativeMetadataMilliseconds = null,
    long? NativePadMilliseconds = null,
    long? NativeContourMilliseconds = null,
    long? NativeObjectLoopMilliseconds = null,
    DpViaCorridorNavigationMode Mode = DpViaCorridorNavigationMode.Revalidate);

/// <summary>A verified Engine analysis and the live document identity required for navigation.</summary>
public sealed record DpViaCorridorAnalysis(
    WorkspaceDocumentIdentity Document,
    DpViaCorridorResult Result)
{
    internal CorridorScan? ManagedScan { get; init; }
    internal LiveDesignScene? LiveScene { get; init; }
    internal DpViaCorridorTimings? Timings { get; init; }

    public bool IsCurrentFor(WorkspaceDocumentIdentity? document) =>
        document == Document;
}

public interface IDpViaCorridorService
{
    Task<DpViaCorridorAnalysis> AnalyzeAsync(DpViaCorridorOptions options, string reportPath,
        CancellationToken cancellationToken = default);

    Task<DpViaCorridorNavigationOutcome> NavigateAsync(DpViaCorridorAnalysis analysis,
        DpViaCorridorFinding finding, DpViaCorridorNavigationRequest request,
        CancellationToken cancellationToken = default);

    Task<DpViaCorridorNavigationOutcome> BrowseAsync(DpViaCorridorAnalysis analysis,
        DpViaCorridorFinding finding, DpViaCorridorNavigationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience diagnostic for the most recent navigation only. Never the
    /// authority for what a particular request did; published results carry
    /// their own operation evidence.
    /// </summary>
    DpViaCorridorNavigationPhases? LastNavigationPhases { get; }
}
