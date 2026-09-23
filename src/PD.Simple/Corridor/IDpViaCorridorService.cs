using System.IO;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

namespace PD.Simple.Corridor;

public sealed record DpViaCorridorOptions(
    decimal MarginMils,
    string ModuleFilter,
    bool IncludeUnused,
    CorridorPairPolicy PairPolicy = CorridorPairPolicy.SuffixCompat)
{
    public IProgress<string>? Progress { get; init; }

    /// <summary>
    /// Provisional candidate net pairs published before the expensive
    /// capture starts. Candidates are net names only, never findings.
    /// Null disables the preflight read; the full scan still runs.
    /// </summary>
    public IProgress<DpViaCorridorCandidateSnapshot>? CandidateProgress { get; init; }

    public IProgress<string>? CandidateIssueProgress { get; init; }
}

/// <summary>
/// One provisional candidate differential net pair. Candidates name member
/// nets only; they are not via pairs, corridors, findings, Clear/Pass, or
/// proof of current copper.
/// </summary>
public sealed record DpViaCorridorCandidatePair(
    string PairName,
    string PositiveNet,
    string NegativeNet);

/// <summary>
/// Live candidate snapshot over one Engine metadata read. The retained
/// <see cref="LiveDesignScene"/> is the only navigation authority; once it
/// is no longer current, native zoom must stay disabled. A snapshot that
/// outlives a failed or cancelled full scan is explicitly historical.
/// </summary>
public sealed record DpViaCorridorCandidateSnapshot(
    WorkspaceDocumentIdentity Document,
    LiveDesignScene LiveScene,
    IReadOnlyList<DpViaCorridorCandidatePair> Pairs,
    IReadOnlyList<string> CoverageWarnings,
    CorridorPairPolicy Policy,
    bool IncludeUnused,
    DateTimeOffset CapturedAt)
{
    public Guid CaptureId => LiveScene.Scene.Identity.CaptureId;

    public bool IsCurrentFor(WorkspaceDocumentIdentity? document) =>
        document == Document && LiveScene.IsCurrent;
}

public sealed record DpViaCorridorCandidateNavigationRequest(Guid OperationId, string Origin);

/// <summary>
/// One candidate net navigation's own evidence. Net navigation only; it
/// proves nothing about vias, corridors, findings, or copper.
/// </summary>
public sealed record DpViaCorridorCandidateNavigationOutcome(
    Guid OperationId,
    string Origin,
    string PairName,
    string NetName,
    bool Positive,
    Guid CaptureId,
    long ZoomMilliseconds);

/// <summary>
/// Pure candidate net resolution over an immutable Engine scene. Production
/// callers additionally require the live scene to be current before the
/// returned reference can authorize native display; tests exercise the
/// name matching without manufacturing native authority.
/// </summary>
public static class DpViaCorridorCandidateNavigation
{
    public static SceneObjectReference ResolveNetReference(DesignScene scene, string netName)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (string.IsNullOrWhiteSpace(netName))
        {
            throw new ArgumentException("A net name is required.", nameof(netName));
        }

        NetObject? resolved = null;
        foreach (NetObject candidate in scene.Nets.RequireComplete())
        {
            if (string.Equals(candidate.Name, netName, StringComparison.OrdinalIgnoreCase))
            {
                resolved = candidate;
                break;
            }
        }

        if (resolved is null)
        {
            throw new InvalidDataException(
                $"Net '{netName}' is no longer present in the live Engine scene; candidate navigation is unavailable.");
        }

        return scene.ReferenceTo(resolved.Id);
    }
}

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
public enum DpViaCorridorNavigationStage
{
    ReadingRegion,
    MatchingWitnesses,
    Zooming,
    ReadingPresentation,
}

public sealed record DpViaCorridorNavigationRequest(Guid OperationId, string Origin)
{
    public IProgress<DpViaCorridorNavigationStage>? Progress { get; init; }
}

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
    internal ScalableCorridorScan? ScalableScan { get; init; }
    internal DpViaCorridorCaptureResult? CaptureEvidence { get; init; }
    internal LiveDesignScene? LiveScene { get; set; }
    internal Guid? CaptureId => ScalableScan?.PlanningScene.Identity.CaptureId ??
        LiveScene?.Scene.Identity.CaptureId;
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
    /// Zooms to one candidate member net through its live metadata scene.
    /// Net navigation only; never a via pair, corridor, finding, Clear/Pass,
    /// or proof of current copper. The snapshot scene must still be current
    /// and bound to the primary document.
    /// </summary>
    Task<DpViaCorridorCandidateNavigationOutcome> ZoomCandidateNetAsync(
        DpViaCorridorCandidateSnapshot snapshot,
        DpViaCorridorCandidatePair pair,
        bool positive,
        DpViaCorridorCandidateNavigationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience diagnostic for the most recent navigation only. Never the
    /// authority for what a particular request did; published results carry
    /// their own operation evidence.
    /// </summary>
    DpViaCorridorNavigationPhases? LastNavigationPhases { get; }
}
