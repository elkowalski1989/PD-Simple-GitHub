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
    long? NativeSerializationMilliseconds = null);

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

    Task<DpViaCorridorZoomResult> NavigateAsync(DpViaCorridorAnalysis analysis,
        DpViaCorridorFinding finding, CancellationToken cancellationToken = default);

    DpViaCorridorNavigationPhases? LastNavigationPhases { get; }
}
