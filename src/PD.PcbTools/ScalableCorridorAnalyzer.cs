using System.Collections.Immutable;
using System.Globalization;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Scenes;

namespace PD.PcbTools;

/// <summary>
/// Capture-scoped identity supplied by the Engine bulk replay adapter. This
/// neutral contract keeps PcbTools independent from live workspace ownership.
/// </summary>
public sealed record CorridorSourceIdentity(
    SceneObjectId Id,
    long CaptureOrdinal,
    string SourceIdentity,
    CopperKind Kind)
{
    /// <summary>Physical-root order before filtering; never a scene index.</summary>
    public long? SourceTraversalOrdinal { get; init; }
}

/// <summary>
/// Ordering evidence supplied by an authenticated capture adapter. The complete
/// physical-root universe is separate from the capture's engineering coverage.
/// Matching source identities alone cannot establish cross-capture ordering.
/// </summary>
public sealed record CorridorSourceTraversal(
    int Version,
    string OrderingUniverse,
    string SessionId,
    long BoardGeneration,
    string ObservationToken,
    long RootCount)
{
    public CorridorFrozenSource? FrozenSource { get; init; }

    public bool IsSupported =>
        Version == 1 && OrderingUniverse == "all_board_copper_roots_v1" &&
        !string.IsNullOrWhiteSpace(SessionId) && BoardGeneration >= 0 &&
        !string.IsNullOrWhiteSpace(ObservationToken) && RootCount is >= 0 and <= 50_000_000 &&
        (FrozenSource is null || FrozenSource.IsValid && RootCount <= FrozenSource.MaximumSourceRoots);

    /// <summary>
    /// Permits comparison of source ordinals, not scene indexes or analysis
    /// equivalence. Separate observations need the same verified frozen source.
    /// </summary>
    public bool CanCompareSourceOrdinalsWith(CorridorSourceTraversal? other) =>
        IsSupported && other is { IsSupported: true } &&
        Version == other.Version && OrderingUniverse == other.OrderingUniverse &&
        SessionId == other.SessionId && BoardGeneration == other.BoardGeneration &&
        RootCount == other.RootCount &&
        (ObservationToken == other.ObservationToken ||
            FrozenSource is { SourceOrderVerified: true } source && source == other.FrozenSource);
}

public sealed record CorridorFrozenSource(
    string ObservationId,
    string InputSha256,
    int WorkerProcessId,
    int MaximumSourceRoots,
    bool SourceOrderVerified)
{
    public CorridorSourceDocument? SourceDocument { get; init; }
    public string? SourceSnapshotHash { get; init; }
    public DateTimeOffset? ExportedAt { get; init; }

    internal bool IsValid =>
        IsHex(ObservationId, 32) && IsHex(InputSha256, 64) && WorkerProcessId > 0 &&
        MaximumSourceRoots is > 0 and <= 10_000_000;

    private static bool IsHex(string? value, int length) => value?.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record CorridorSourceDocument(
    string SessionId,
    long SessionGeneration,
    long BoardGeneration,
    int? ProcessId,
    string? Design,
    string ProtocolVersion);

public sealed record CorridorReplayScene(
    DesignScene Scene,
    IReadOnlyList<CorridorSourceIdentity> ObjectIdentities)
{
    public CorridorSourceTraversal? SourceTraversal { get; init; }
}

public sealed record CorridorBatchOptions(
    decimal MaximumWidthMils = 5_000,
    decimal MaximumHeightMils = 5_000,
    int MaximumMaterializedObjects = 100_000)
{
    /// <summary>Bounds scalar identity evidence retained across overlapping replays.</summary>
    public int MaximumRetainedSourceIdentities { get; init; } = 2_000_000;

    internal void Validate()
    {
        if (MaximumWidthMils is <= 0 or > 100_000 ||
            MaximumHeightMils is <= 0 or > 100_000 ||
            MaximumMaterializedObjects is < 1 or > 200_000 ||
            MaximumRetainedSourceIdentities is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CorridorBatchOptions),
                "Corridor batch dimensions or materialization budget are outside their bounds.");
        }
    }
}

public sealed record CorridorReplayBatch(
    int Id,
    LayerId Layer,
    DesignBounds Region,
    int MaximumMaterializedObjects,
    IReadOnlyList<int> SeedIds,
    ViaPadMeasurementSelection ViaPadMeasurements)
{
    public SceneQuery CreateQuery() => new()
    {
        Kind = SceneReadKind.RegionGeometry,
        // Pair discovery already consumed the complete net catalog while
        // planning. Aggressor net names are carried by copper objects, so
        // replaying that catalog for every bounded batch adds no evidence.
        Families = [DataFamily.Layers, DataFamily.Copper],
        CopperKinds = [CopperKind.Trace, CopperKind.Via, CopperKind.Shape],
        ViaPadMeasurements = ViaPadMeasurements,
        Layers = [Layer],
        Region = Region,
        IncludeContours = false,
        MaximumObjects = MaximumMaterializedObjects,
    };
}

public sealed record CorridorFindingWitnesses(
    CopperObject PositiveVia,
    CorridorSourceIdentity PositiveIdentity,
    CopperObject NegativeVia,
    CorridorSourceIdentity NegativeIdentity,
    CopperObject Aggressor,
    CorridorSourceIdentity AggressorIdentity);

public sealed record ScalableCorridorFinding(
    CorridorFinding Finding,
    CorridorFindingWitnesses Witnesses);

/// <summary>
/// Validates all admitted identities without retaining their copper scenes.
/// Each scan adds an overlay to its completed plan's evidence, avoiding a copy
/// of the planning registry. Legacy captures keep their existing ordinal policy.
/// </summary>
internal sealed class CorridorSourceOrdering
{
    private readonly int _maximumIdentities;
    private readonly CorridorSourceOrdering? _planningEvidence;
    private readonly Dictionary<string, CorridorSourceIdentity> _bySource = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _byOrdinal = [];
    private readonly Dictionary<string, CorridorSourceIdentity> _byCrossSource = new(StringComparer.Ordinal);

    public CorridorSourceOrdering(CorridorSourceTraversal? traversal, int maximumIdentities)
    {
        if (traversal is { IsSupported: false })
        {
            throw new InvalidDataException("The source traversal declaration is incomplete or unsupported.");
        }
        Traversal = traversal;
        _maximumIdentities = maximumIdentities;
    }

    private CorridorSourceOrdering(CorridorSourceOrdering planningEvidence)
        : this(planningEvidence.Traversal, planningEvidence._maximumIdentities)
    {
        _planningEvidence = planningEvidence;
    }

    private CorridorSourceOrdering(
        CorridorSourceTraversal? scanTraversal,
        CorridorSourceOrdering planningEvidence)
        : this(scanTraversal, planningEvidence._maximumIdentities)
    {
        _planningEvidence = planningEvidence;
    }

    public CorridorSourceTraversal? Traversal { get; }
    private int Count => _bySource.Count + (_planningEvidence?.Count ?? 0);

    public CorridorSourceOrdering ForScan() => new(this);

    public CorridorSourceOrdering ForScan(CorridorSourceTraversal? scanTraversal)
    {
        if (scanTraversal is { IsSupported: false })
        {
            throw new InvalidDataException("The scan source traversal declaration is incomplete or unsupported.");
        }
        if (scanTraversal == Traversal)
        {
            return ForScan();
        }
        if (Traversal is null || scanTraversal is null)
        {
            throw new InvalidDataException("Scan source ordering changed after planning.");
        }
        if (!Traversal.CanCompareSourceOrdinalsWith(scanTraversal))
        {
            throw new InvalidDataException("Scan source ordering is not comparable with the planning observation.");
        }
        if (Traversal.FrozenSource is not { SourceOrderVerified: true } planningFrozen ||
            scanTraversal.FrozenSource is not { SourceOrderVerified: true } scanFrozen ||
            planningFrozen != scanFrozen)
        {
            throw new InvalidDataException("Scan source ordering requires the same verified frozen source as planning.");
        }
        return new(scanTraversal, this);
    }

    public long Ordinal(CorridorSourceIdentity identity) => Traversal is null
        ? identity.CaptureOrdinal
        : identity.SourceTraversalOrdinal ?? throw new InvalidDataException(
            "An admitted source identity is missing its physical-root ordinal.");

    public void ValidateReplay(CorridorReplayScene replay)
    {
        if (replay.SourceTraversal != Traversal)
        {
            throw new InvalidDataException("Replay source ordering changed after planning.");
        }
        var pendingBySource = new Dictionary<string, CorridorSourceIdentity>(StringComparer.Ordinal);
        var pendingByOrdinal = new Dictionary<long, string>();
        var pendingByCrossSource = new Dictionary<string, CorridorSourceIdentity>(StringComparer.Ordinal);
        bool isCrossCapture = _planningEvidence is not null && Traversal != _planningEvidence.Traversal;
        int planningCount = _planningEvidence?.Count ?? 0;
        for (int index = 0; index < replay.ObjectIdentities.Count; index++)
        {
            CorridorSourceIdentity identity = replay.ObjectIdentities[index];
            CopperObject copper = replay.Scene.Data.Copper[index];
            if (identity.Id != copper.Id || identity.Kind != copper.Kind ||
                identity.CaptureOrdinal < 0 || string.IsNullOrWhiteSpace(identity.SourceIdentity))
            {
                throw new InvalidDataException("Replay copper and capture identities are not aligned.");
            }
            if (Traversal is null)
            {
                if (identity.SourceTraversalOrdinal is not null)
                {
                    throw new InvalidDataException("A legacy capture cannot claim physical source ordinals.");
                }
                continue;
            }
            if (identity.SourceTraversalOrdinal is not { } ordinal || ordinal < 0 || ordinal >= Traversal.RootCount)
            {
                throw new InvalidDataException("A source traversal ordinal is missing or outside its declared universe.");
            }
            CorridorSourceIdentity? localPrevious = _bySource.GetValueOrDefault(identity.SourceIdentity) ??
                _byCrossSource.GetValueOrDefault(identity.SourceIdentity);
            if (localPrevious is null)
            {
                pendingBySource.TryGetValue(identity.SourceIdentity, out localPrevious);
            }
            if (localPrevious is null)
            {
                pendingByCrossSource.TryGetValue(identity.SourceIdentity, out localPrevious);
            }
            CorridorSourceIdentity? planningPrevious = _planningEvidence?.FindSource(identity.SourceIdentity);
            string? previousSource = FindOrdinal(ordinal);
            if (previousSource is null)
            {
                pendingByOrdinal.TryGetValue(ordinal, out previousSource);
            }
            if (localPrevious is not null && localPrevious != identity)
            {
                throw new InvalidDataException("Source identity and physical-root ordinal evidence conflict across replays.");
            }
            if (localPrevious is null && planningPrevious is not null)
            {
                bool matches;
                if (isCrossCapture)
                {
                    matches = planningPrevious.Kind == identity.Kind &&
                        planningPrevious.SourceTraversalOrdinal == identity.SourceTraversalOrdinal;
                }
                else
                {
                    matches = planningPrevious == identity;
                }
                if (!matches)
                {
                    throw new InvalidDataException("Source identity and physical-root ordinal evidence conflict across replays.");
                }
                if (isCrossCapture)
                {
                    if ((long)_byCrossSource.Count + pendingByCrossSource.Count >= planningCount)
                    {
                        throw new InvalidDataException(
                            $"Source ordering validation exceeds its {_maximumIdentities:N0}-identity retention budget.");
                    }
                    pendingByCrossSource.Add(identity.SourceIdentity, identity);
                }
            }
            if (previousSource is not null && previousSource != identity.SourceIdentity)
            {
                throw new InvalidDataException("Source identity and physical-root ordinal evidence conflict across replays.");
            }
            if (localPrevious is null && planningPrevious is null)
            {
                if ((long)Count + pendingBySource.Count >= _maximumIdentities)
                {
                    throw new InvalidDataException(
                        $"Source ordering validation exceeds its {_maximumIdentities:N0}-identity retention budget.");
                }
                pendingBySource.Add(identity.SourceIdentity, identity);
                pendingByOrdinal.Add(ordinal, identity.SourceIdentity);
            }
        }
        foreach ((string source, CorridorSourceIdentity identity) in pendingBySource)
        {
            _bySource.Add(source, identity);
            _byOrdinal.Add(identity.SourceTraversalOrdinal!.Value, source);
        }
        foreach ((string source, CorridorSourceIdentity identity) in pendingByCrossSource)
        {
            _byCrossSource.Add(source, identity);
        }
    }

    private CorridorSourceIdentity? FindSource(string source) =>
        _bySource.GetValueOrDefault(source) ?? _byCrossSource.GetValueOrDefault(source) ??
        _planningEvidence?.FindSource(source);

    private string? FindOrdinal(long ordinal) =>
        _byOrdinal.GetValueOrDefault(ordinal) ?? _planningEvidence?.FindOrdinal(ordinal);
}

internal static class ScalableCorridorCoveragePolicy
{
    private static readonly ImmutableHashSet<string> ConservativeViaReasons =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "via:active_via_layers",
            "via:backdrill");

    /// <summary>
    /// Separates coverage that can hide a candidate from exact via metadata
    /// gaps handled by the analyzer's conservative OriginalLayers fallback.
    /// The latter still requires review and never supports a clear/pass claim.
    /// </summary>
    public static void Classify(
        DesignScene scene,
        IReadOnlyCollection<CopperKind> requiredKinds,
        string scope,
        HashSet<string> warnings,
        HashSet<string> blockingWarnings)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(requiredKinds);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(blockingWarnings);

        FamilyCoverage family = scene.Coverage[DataFamily.Copper];
        if (family.Availability != DataAvailability.Available)
        {
            AddBlocking(
                $"{scope} does not provide the requested copper family.",
                warnings,
                blockingWarnings);
        }
        if (family.Truncated)
        {
            AddBlocking(
                $"{scope} truncated copper records.",
                warnings,
                blockingWarnings);
        }

        foreach (CopperKind kind in requiredKinds)
        {
            CopperKindCoverage? detail = scene.CopperScope.Details
                .SingleOrDefault(item => item.Kind == kind);
            if (detail is null || detail.Availability != DataAvailability.Available)
            {
                AddBlocking(
                    $"{scope} does not provide requested {kind.ToString().ToLowerInvariant()} coverage.",
                    warnings,
                    blockingWarnings);
                continue;
            }
            foreach (string reason in detail.Reasons)
            {
                ClassifyReason(scope, kind, reason, warnings, blockingWarnings);
            }
            if (!detail.IsComplete && !IsConservativeViaGap(kind, detail.Reasons))
            {
                AddBlocking(
                    $"{scope} has incomplete {kind.ToString().ToLowerInvariant()} coverage without an approved conservative fallback.",
                    warnings,
                    blockingWarnings);
            }
        }

        foreach (string reason in family.Reasons)
        {
            CopperKind? kind = RequiredKindFor(reason, requiredKinds);
            if (kind is { } requiredKind)
            {
                ClassifyReason(scope, requiredKind, reason, warnings, blockingWarnings);
            }
            else
            {
                AddBlocking(
                    $"{scope} reports unavailable copper data: {reason}. No clear conclusion is permitted.",
                    warnings,
                    blockingWarnings);
            }
        }
        if (!family.IsComplete &&
            !(family.Availability == DataAvailability.Available &&
              !family.Truncated &&
              family.Reasons.Length > 0 &&
              family.Reasons.All(ConservativeViaReasons.Contains)))
        {
            AddBlocking(
                $"{scope} has incomplete copper-family coverage without an approved conservative fallback.",
                warnings,
                blockingWarnings);
        }
    }

    private static CopperKind? RequiredKindFor(
        string reason,
        IReadOnlyCollection<CopperKind> requiredKinds)
    {
        foreach (CopperKind kind in requiredKinds)
        {
            string prefix = kind.ToString().ToLowerInvariant() + ":";
            if (reason.StartsWith(prefix, StringComparison.Ordinal))
            {
                return kind;
            }
        }
        return null;
    }

    private static void ClassifyReason(
        string scope,
        CopperKind kind,
        string reason,
        HashSet<string> warnings,
        HashSet<string> blockingWarnings)
    {
        if (kind == CopperKind.Via && ConservativeViaReasons.Contains(reason))
        {
            warnings.Add(
                $"{scope} reports {reason}; original via layers are screened conservatively. Review is required and no clear conclusion is permitted.");
            return;
        }
        AddBlocking(
            $"{scope} reports unavailable {kind.ToString().ToLowerInvariant()} data: {reason}. No clear conclusion is permitted.",
            warnings,
            blockingWarnings);
    }

    private static bool IsConservativeViaGap(
        CopperKind kind,
        ImmutableArray<string> reasons) =>
        kind == CopperKind.Via &&
        reasons.Length > 0 &&
        reasons.All(ConservativeViaReasons.Contains);

    private static void AddBlocking(
        string message,
        HashSet<string> warnings,
        HashSet<string> blockingWarnings)
    {
        warnings.Add(message);
        blockingWarnings.Add(message);
    }
}

public sealed record ScalableCorridorScan(
    DesignScene PlanningScene,
    CorridorOptions Options,
    int PairCount,
    int CorridorCount,
    IReadOnlyList<ScalableCorridorFinding> Findings,
    IReadOnlyList<string> CoverageWarnings,
    IReadOnlyList<string> BlockingCoverageWarnings)
{
    public CorridorSourceTraversal? SourceTraversal { get; init; }

    /// <summary>
    /// True when no missing fact can hide a crossing. Review warnings may
    /// still prohibit a clear/pass conclusion, for example when original via
    /// layers were screened conservatively because active-layer metadata was
    /// unavailable.
    /// </summary>
    public bool HasCompleteInputs => BlockingCoverageWarnings.Count == 0;

    public DesignScene CreateSourceWitnessScene(ScalableCorridorFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ScalableCorridorFinding admitted = Findings.SingleOrDefault(item =>
            ReferenceEquals(item, finding)) ?? throw new ArgumentException(
                "The finding does not belong to this scalable scan.", nameof(finding));
        return ScalableCorridorAnalyzer.CreateWitnessScene(PlanningScene, admitted);
    }
}

public sealed class ScalableCorridorPlan
{
    private readonly ImmutableArray<CorridorSeed> _seeds;
    private readonly int _corridorCount;
    private readonly CorridorSourceOrdering _sourceOrdering;

    internal ScalableCorridorPlan(
        DesignScene planningScene,
        CorridorOptions options,
        ImmutableArray<CorridorSeed> seeds,
        ImmutableArray<CorridorReplayBatch> batches,
        int pairCount,
        int corridorCount,
        ImmutableArray<string> planningWarnings,
        ImmutableArray<string> planningBlockingWarnings,
        CorridorSourceOrdering sourceOrdering)
    {
        PlanningScene = planningScene;
        Options = options;
        _seeds = seeds;
        Batches = batches;
        PairCount = pairCount;
        _corridorCount = corridorCount;
        PlanningWarnings = planningWarnings;
        PlanningBlockingWarnings = planningBlockingWarnings;
        _sourceOrdering = sourceOrdering;
    }

    public DesignScene PlanningScene { get; }
    public CorridorOptions Options { get; }
    public IReadOnlyList<CorridorReplayBatch> Batches { get; }
    public int PairCount { get; }
    public int CorridorCount => _corridorCount;
    public IReadOnlyList<string> PlanningWarnings { get; }
    public IReadOnlyList<string> PlanningBlockingWarnings { get; }
    public CorridorSourceTraversal? SourceTraversal => _sourceOrdering.Traversal;

    public ScalableCorridorScanSession BeginScan() => new(this, _seeds, _sourceOrdering.ForScan());

    /// <summary>
    /// Starts a scan from a second capture of the same verified frozen source.
    /// The scan keeps its own traversal token; planning roots are compared by
    /// stable source identity, kind, and physical-root ordinal only.
    /// </summary>
    public ScalableCorridorScanSession BeginScan(CorridorSourceTraversal? scanTraversal) =>
        new(this, _seeds, _sourceOrdering.ForScan(scanTraversal));
}

public sealed class ScalableCorridorScanSession
{
    private readonly ScalableCorridorPlan _plan;
    private readonly ImmutableArray<CorridorSeed> _seeds;
    private readonly HashSet<int> _acceptedBatches = [];
    private readonly Dictionary<int, List<DesignBounds>> _pendingBatchRegions;
    private readonly Dictionary<int, Dictionary<(string Source, string Layer), ScalableCorridorFinding>>
        _findingsBySeed = [];
    private readonly HashSet<string> _warnings;
    private readonly HashSet<string> _blockingWarnings;
    private readonly CorridorSourceOrdering _sourceOrdering;
    private bool _completed;

    internal ScalableCorridorScanSession(
        ScalableCorridorPlan plan,
        ImmutableArray<CorridorSeed> seeds,
        CorridorSourceOrdering sourceOrdering)
    {
        _plan = plan;
        _seeds = seeds;
        _sourceOrdering = sourceOrdering;
        _pendingBatchRegions = plan.Batches.ToDictionary(batch => batch.Id,
            batch => new List<DesignBounds> { batch.Region });
        _warnings = plan.PlanningWarnings.ToHashSet(StringComparer.Ordinal);
        _blockingWarnings = plan.PlanningBlockingWarnings.ToHashSet(StringComparer.Ordinal);
    }

    public void AcceptBatch(
        CorridorReplayBatch batch,
        CorridorReplayScene replay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(replay);
        cancellationToken.ThrowIfCancellationRequested();
        if (_completed)
        {
            throw new InvalidOperationException("The scalable corridor scan is already complete.");
        }
        CorridorReplayBatch expected = _plan.Batches.SingleOrDefault(item => item.Id == batch.Id) ??
            throw new ArgumentException("The replay batch does not belong to this plan.", nameof(batch));
        if (expected != batch)
        {
            throw new ArgumentException("The replay batch definition changed after planning.", nameof(batch));
        }
        if (_acceptedBatches.Contains(batch.Id))
        {
            throw new InvalidOperationException($"Corridor batch {batch.Id} was already supplied.");
        }
        List<DesignBounds> pendingRegions = _pendingBatchRegions[batch.Id];
        DesignBounds[] coveredRegions = pendingRegions.Where(region =>
            replay.Scene.Query.Region is { } observed && observed.Contains(region)).ToArray();
        if (coveredRegions.Length == 0)
        {
            throw new InvalidDataException("The replay does not cover any pending corridor partition.");
        }
        ValidateReplay(expected with { Region = coveredRegions[0] }, replay);
        _sourceOrdering.ValidateReplay(replay);

        // Reduce each replay against the original seed, never a clipped child
        // corridor. Only findings and their three witnesses survive admission;
        // complete scenes are not retained. Global ordering is deferred to Complete.
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var blockingWarnings = new HashSet<string>(StringComparer.Ordinal);
        ScalableCorridorCoveragePolicy.Classify(
            replay.Scene,
            [CopperKind.Trace, CopperKind.Via, CopperKind.Shape],
            $"Engine corridor batch {batch.Id}",
            warnings,
            blockingWarnings);
        var reduced = new List<(int SeedId, List<ScalableCorridorFinding> Findings)>();
        foreach (int seedId in batch.SeedIds)
        {
            CorridorSeed seed = _seeds[seedId];
            reduced.Add((seedId, InspectSeed(
                seed, replay, batch.Layer, warnings, _sourceOrdering, cancellationToken)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach ((int seedId, List<ScalableCorridorFinding> findings) in reduced)
        {
            if (!_findingsBySeed.TryGetValue(seedId, out var retained))
            {
                retained = [];
                _findingsBySeed.Add(seedId, retained);
            }
            foreach (ScalableCorridorFinding finding in findings)
            {
                retained.TryAdd(
                    (finding.Witnesses.AggressorIdentity.SourceIdentity, finding.Finding.Layer),
                    finding);
            }
        }
        _warnings.UnionWith(warnings);
        _blockingWarnings.UnionWith(blockingWarnings);
        pendingRegions.RemoveAll(region => coveredRegions.Contains(region));
        if (pendingRegions.Count == 0)
        {
            _acceptedBatches.Add(batch.Id);
        }
    }

    /// <summary>Registers an exact closed partition before either child is admitted.</summary>
    public void SplitBatchRegion(
        CorridorReplayBatch batch, DesignBounds region, DesignBounds first, DesignBounds second)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (_completed || _plan.Batches.SingleOrDefault(item => item.Id == batch.Id) != batch ||
            !_pendingBatchRegions.TryGetValue(batch.Id, out List<DesignBounds>? pending) ||
            !pending.Contains(region))
        {
            throw new InvalidOperationException("Only a pending corridor region can be subdivided.");
        }
        bool vertical = first.Minimum == region.Minimum && second.Maximum == region.Maximum &&
            first.Maximum.X == second.Minimum.X && first.Maximum.Y == region.Maximum.Y &&
            second.Minimum.Y == region.Minimum.Y && first.Width > 0 && second.Width > 0;
        bool horizontal = first.Minimum == region.Minimum && second.Maximum == region.Maximum &&
            first.Maximum.Y == second.Minimum.Y && first.Maximum.X == region.Maximum.X &&
            second.Minimum.X == region.Minimum.X && first.Height > 0 && second.Height > 0;
        if (!vertical && !horizontal)
        {
            throw new ArgumentException("Corridor child regions must exactly partition their parent without gaps.");
        }
        pending.Remove(region);
        pending.Add(first);
        pending.Add(second);
    }

    public ScalableCorridorScan Complete(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_completed)
        {
            throw new InvalidOperationException("The scalable corridor scan is already complete.");
        }
        CorridorReplayBatch? missing = _plan.Batches.FirstOrDefault(batch => !_acceptedBatches.Contains(batch.Id));
        if (missing is not null)
        {
            throw new InvalidOperationException(
                $"Corridor batch {missing.Id} on {missing.Layer.Value} is missing; no scan result was produced.");
        }

        _completed = true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<ScalableCorridorFinding>();
        int? currentGroup = null;
        var groupFindings = new List<ScalableCorridorFinding>();
        foreach (CorridorSeed seed in _seeds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (currentGroup is not null && currentGroup != seed.PairGroup)
            {
                all.InsertRange(0, groupFindings);
                groupFindings.Clear();
            }
            currentGroup = seed.PairGroup;
            // Apply the global seen rule in planned seed order, independent of
            // batch arrival order. A single seed can still report several layers.
            ScalableCorridorFinding[] found = _findingsBySeed.TryGetValue(seed.Id, out var retained)
                ? retained.Values
                    .Where(item => !seen.Contains(item.Witnesses.AggressorIdentity.SourceIdentity))
                    .OrderBy(item => seed.TargetLayers.IndexOf(item.Finding.Layer))
                    .ThenBy(item => ObjectOrder(item.Witnesses.Aggressor))
                    .ThenBy(item => _sourceOrdering.Ordinal(item.Witnesses.AggressorIdentity))
                    .Reverse()
                    .ToArray()
                : [];
            foreach (ScalableCorridorFinding finding in found)
            {
                groupFindings.Insert(0, finding);
                seen.Add(finding.Witnesses.AggressorIdentity.SourceIdentity);
            }
        }
        if (currentGroup is not null)
        {
            all.InsertRange(0, groupFindings);
        }

        var deduplicated = new Dictionary<
            (string Aggressor, string Pair, string Layer, string Positive, string Negative),
            ScalableCorridorFinding>();
        foreach (ScalableCorridorFinding finding in all)
        {
            CorridorFinding value = finding.Finding;
            var key = (value.AggressorNet, value.PairName, value.Layer,
                NativeKey(value.P, seedScale: NativeScale(_plan.PlanningScene.Document.NativeUnits)),
                NativeKey(value.N, seedScale: NativeScale(_plan.PlanningScene.Document.NativeUnits)));
            if (!deduplicated.TryGetValue(key, out ScalableCorridorFinding? previous) ||
                value.DistanceMils < previous.Finding.DistanceMils)
            {
                deduplicated[key] = finding;
            }
        }
        ScalableCorridorFinding[] results = deduplicated.Values
            .OrderBy(item => RiskOrder(item.Finding.Risk))
            .ThenBy(item => item.Finding.DistanceMils)
            .Select((item, index) => item with
            {
                Finding = item.Finding with { Id = $"crossing-{index + 1}" },
            })
            .ToArray();
        return new(
            _plan.PlanningScene,
            _plan.Options,
            _plan.PairCount,
            _plan.CorridorCount,
            Array.AsReadOnly(results),
            Array.AsReadOnly(_warnings.Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(_blockingWarnings.Order(StringComparer.Ordinal).ToArray()))
        {
            SourceTraversal = _sourceOrdering.Traversal,
        };
    }

    private static void ValidateReplay(CorridorReplayBatch batch, CorridorReplayScene replay)
    {
        DesignScene scene = replay.Scene;
        if (scene.Query.Kind != SceneReadKind.RegionGeometry ||
            scene.Query.Region is not { } region ||
            !region.Contains(batch.Region) ||
            !scene.Query.Layers.Contains(batch.Layer) ||
            scene.Query.IncludeContours ||
            replay.ObjectIdentities.Count != scene.Data.Copper.Length)
        {
            throw new InvalidDataException(
                $"Corridor batch {batch.Id} replay does not preserve its bounded region, layer, or copper identity map.");
        }
        for (int index = 0; index < replay.ObjectIdentities.Count; index++)
        {
            CorridorSourceIdentity identity = replay.ObjectIdentities[index];
            CopperObject copper = scene.Data.Copper[index];
            if (identity.Id != copper.Id || identity.Kind != copper.Kind ||
                string.IsNullOrWhiteSpace(identity.SourceIdentity))
            {
                throw new InvalidDataException(
                    $"Corridor batch {batch.Id} replay identity is incomplete or out of order.");
            }
        }
    }

    private static List<ScalableCorridorFinding> InspectSeed(
        CorridorSeed seed,
        CorridorReplayScene replay,
        LayerId batchLayer,
        HashSet<string> warnings,
        CorridorSourceOrdering sourceOrdering,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, ReplayCopper>(StringComparer.Ordinal);
        for (int index = 0; index < replay.Scene.Data.Copper.Length; index++)
        {
            CopperObject copper = replay.Scene.Data.Copper[index];
            CorridorSourceIdentity identity = replay.ObjectIdentities[index];
            if (CorridorGeometry.Intersects(copper.Bounds, seed.BroadPhaseBounds))
            {
                candidates.TryAdd(identity.SourceIdentity, new(copper, identity));
            }
        }
        ReplayCopper[] ordered = candidates.Values
            .OrderBy(item => ObjectOrder(item.Object))
            .ThenBy(item => sourceOrdering.Ordinal(item.Identity))
            .ToArray();
        var found = new List<ScalableCorridorFinding>();
        ViaSpan positive = seed.Positive.Object.Via!;
        ViaSpan negative = seed.Negative.Object.Via!;
        foreach (string layer in seed.TargetLayers.Where(layer =>
            string.Equals(layer, batchLayer.Value, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (ReplayCopper candidate in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopperObject item = candidate.Object;
                string net = item.NetName?.ToUpperInvariant() ?? string.Empty;
                if (string.Equals(net, seed.PositiveNet, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(net, seed.NegativeNet, StringComparison.OrdinalIgnoreCase) ||
                    SignalClassifier.IsIgnoredAggressor(net))
                {
                    continue;
                }
                double? distance = null;
                DesignPoint intrusion = seed.Center;
                string kind = item.Kind.ToString().ToLowerInvariant();
                if (TraceEndpoints(item) is { } segment && item.Layer is { } segmentLayer &&
                    string.Equals(segmentLayer.Value, layer, StringComparison.OrdinalIgnoreCase))
                {
                    if (seed.Box.Clip(segment.Start, segment.End, seed.NativeScale) is { } clipped)
                    {
                        DesignPoint first = CorridorGeometry.Interpolate(segment.Start, segment.End, clipped.Entry);
                        DesignPoint last = CorridorGeometry.Interpolate(segment.Start, segment.End, clipped.Exit);
                        intrusion = CorridorGeometry.Interpolate(first, last, 0.5);
                        distance = new[] { first, last, intrusion }.Min(point =>
                            CorridorGeometry.DistanceToSegment(
                                point, positive.Position, negative.Position, seed.NativeScale));
                        kind = "cline_segment";
                    }
                }
                else if (item.Via is { } via && seed.Box.Contains(via.Position) &&
                    Layers(via, warnings, sourceOrdering.Ordinal(candidate.Identity))
                        .Contains(layer, StringComparer.OrdinalIgnoreCase))
                {
                    intrusion = via.Position;
                    distance = CorridorGeometry.DistanceToSegment(
                        intrusion, positive.Position, negative.Position, seed.NativeScale);
                }
                else if (item.Kind == CopperKind.Shape && item.Layer is { } shapeLayer &&
                    string.Equals(shapeLayer.Value, layer, StringComparison.OrdinalIgnoreCase) &&
                    !CorridorGeometry.Contains(item.Bounds, seed.BroadPhaseBounds))
                {
                    if (item.FillOutOfDate != false)
                    {
                        warnings.Add(
                            $"Shape fill freshness is not established for capture object {sourceOrdering.Ordinal(candidate.Identity) + 1}; review is required.");
                    }
                    var closest = new DesignPoint(
                        Math.Clamp(seed.Center.X, item.Bounds.Minimum.X, item.Bounds.Maximum.X),
                        Math.Clamp(seed.Center.Y, item.Bounds.Minimum.Y, item.Bounds.Maximum.Y));
                    distance = CorridorGeometry.DistanceToSegment(
                        closest, positive.Position, negative.Position, seed.NativeScale);
                }
                if (distance is null)
                {
                    continue;
                }
                (string Category, string Risk) classification = SignalClassifier.Default.Classify(net);
                var value = new CorridorFinding(
                    string.Empty,
                    seed.PairName,
                    net.Length == 0 ? "(unassigned)" : net,
                    kind,
                    layer,
                    classification.Category,
                    classification.Risk,
                    positive.Position,
                    negative.Position,
                    intrusion,
                    distance.Value,
                    seed.HalfWidth,
                    seed.HalfLength,
                    0,
                    1,
                    2,
                    seed.WidthSourceLayer);
                found.Insert(0, new(value, new(
                    seed.Positive.Object,
                    seed.Positive.Identity,
                    seed.Negative.Object,
                    seed.Negative.Identity,
                    candidate.Object,
                    candidate.Identity)));
            }
        }
        return found;
    }

    private static (DesignPoint Start, DesignPoint End)? TraceEndpoints(CopperObject item) =>
        item.Centerline switch
        {
            LineGeometry line => (line.Start, line.End),
            ArcGeometry arc => (arc.Start, arc.End),
            _ => null,
        };

    private static string[] Layers(
        ViaSpan via,
        HashSet<string> warnings,
        long ordinal)
    {
        if (via.Analysis is { ActiveLayersAvailable: true } &&
            via.BackdrillStatus is "current" or "not_started")
        {
            return via.ActiveLayers.Select(layer => layer.Value).ToArray();
        }
        if (via.Analysis?.BackdrillExclusion == "EXCLUDE_BOTH" ||
            via.BackdrillStatus == "not_started")
        {
            return via.OriginalLayers.Select(layer => layer.Value).ToArray();
        }
        warnings.Add(
            $"Backdrill/active layers are {via.BackdrillStatus} for capture object {ordinal + 1}; original layers are screened conservatively, not certified clear.");
        return via.OriginalLayers.Select(layer => layer.Value).ToArray();
    }

    private static double NativeScale(string units) => units switch
    {
        "mils" => 1,
        "millimeters" => 0.0254,
        _ => throw new InvalidDataException("Unsupported native coordinate units."),
    };

    private static string NativeKey(DesignPoint point, double seedScale) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{(double)point.X * seedScale:F2},{(double)point.Y * seedScale:F2}");

    private static int ObjectOrder(CopperObject item) =>
        item.Kind == CopperKind.Trace ? 0 : item.Kind == CopperKind.Via ? 1 : 2;

    private static int RiskOrder(string risk) => risk switch
    {
        "CRITICAL" => 0,
        "MEDIUM" => 1,
        _ => 2,
    };

    private sealed record ReplayCopper(
        CopperObject Object,
        CorridorSourceIdentity Identity);
}

/// <summary>
/// Reduces bounded, possibly overlapping via replays to capture-ordered subject
/// witnesses. The metadata scene remains bounded independently of the total
/// number of subject vias in the sealed capture.
/// </summary>
public sealed class ScalableCorridorPlanningSession
{
    private readonly DesignScene _metadata;
    private readonly CorridorOptions _options;
    private readonly CorridorBatchOptions _batchOptions;
    private readonly IReadOnlyList<(string PairName, string PositiveNet, string NegativeNet)> _pairs;
    private readonly HashSet<string> _subjectNets;
    private readonly DesignBounds? _moduleBounds;
    private readonly Dictionary<string, PlannedVia> _vias = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blockingWarnings = new(StringComparer.Ordinal);
    private readonly CorridorSourceOrdering _sourceOrdering;
    private bool _completed;

    internal ScalableCorridorPlanningSession(
        DesignScene metadata,
        CorridorOptions options,
        CorridorBatchOptions batchOptions,
        CorridorSourceTraversal? sourceTraversal,
        CancellationToken cancellationToken)
    {
        if (metadata.Document.Kind != DocumentKind.PcbBoard ||
            metadata.Document.NativeUnits is not ("mils" or "millimeters"))
        {
            throw new InvalidDataException("Scalable corridor planning requires PCB metadata in supported units.");
        }
        metadata.Nets.RequireComplete();
        metadata.Modules.RequireComplete();
        metadata.Layers.RequireComplete();
        _metadata = metadata;
        _options = options;
        _batchOptions = batchOptions;
        _sourceOrdering = new(sourceTraversal, batchOptions.MaximumRetainedSourceIdentities);
        _pairs = CorridorAnalyzer.DiscoverNetPairs(metadata, options, _warnings, cancellationToken);
        _subjectNets = _pairs.SelectMany(pair => new[] { pair.PositiveNet, pair.NegativeNet })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(options.ModuleName))
        {
            ModuleObject module = metadata.Modules.RequireComplete().SingleOrDefault(item =>
                string.Equals(item.Name, options.ModuleName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Module '{options.ModuleName}' was not found; no scalable analysis was planned.");
            _moduleBounds = module.Bounds;
        }
    }

    public bool HasSubjectNets => _subjectNets.Count != 0;
    public int RetainedSubjectViaCount { get; private set; }

    public void AcceptReplay(CorridorReplayScene replay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replay);
        if (_completed)
        {
            throw new InvalidOperationException("The corridor plan has already been completed.");
        }
        DesignScene scene = replay.Scene;
        if (scene.Document.Kind != _metadata.Document.Kind ||
            scene.Document.Name != _metadata.Document.Name ||
            scene.Document.NativeUnits != _metadata.Document.NativeUnits ||
            scene.Document.NativePrecision != _metadata.Document.NativePrecision ||
            replay.ObjectIdentities.Count != scene.Data.Copper.Length)
        {
            throw new InvalidDataException("Planning replay metadata or source identity alignment changed.");
        }
        scene.Copper.RequireAvailable();
        _sourceOrdering.ValidateReplay(replay);
        ScalableCorridorCoveragePolicy.Classify(
            scene, [CopperKind.Via], "Engine bounded via planning replay", _warnings, _blockingWarnings);
        for (int index = 0; index < scene.Data.Copper.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopperObject item = scene.Data.Copper[index];
            CorridorSourceIdentity identity = replay.ObjectIdentities[index];
            if (item.Kind != CopperKind.Via || item.Via is null || identity.Id != item.Id ||
                identity.Kind != CopperKind.Via || identity.CaptureOrdinal < 0 ||
                string.IsNullOrWhiteSpace(identity.SourceIdentity))
            {
                throw new InvalidDataException("Planning replay requires via-only copper and aligned capture identities.");
            }
            if (item.NetName is null || !_subjectNets.Contains(item.NetName) ||
                _moduleBounds is { } bounds && !CorridorGeometry.Contains(bounds, item.Via.Position))
            {
                continue;
            }
            if (_vias.TryGetValue(identity.SourceIdentity, out PlannedVia? existing))
            {
                if (existing.Identity != identity || existing.Object.NetName != item.NetName ||
                    existing.Object.Bounds != item.Bounds || existing.Object.Via!.Position != item.Via.Position)
                {
                    throw new InvalidDataException("A capture source identity changed across planning tiles.");
                }
                continue;
            }
            _vias.Add(identity.SourceIdentity, new(item, identity, _sourceOrdering.Ordinal(identity)));
            RetainedSubjectViaCount++;
        }
    }

    public ScalableCorridorPlan Complete(CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The corridor plan has already been completed.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        ScalableCorridorPlan plan = ScalableCorridorAnalyzer.CreatePlan(
            _metadata, _vias.Values.OrderBy(item => item.OrderingOrdinal).ToArray(),
            _pairs, _options, _batchOptions, _warnings, _blockingWarnings, _sourceOrdering,
            cancellationToken);
        _completed = true;
        _vias.Clear();
        return plan;
    }
}

public static class ScalableCorridorAnalyzer
{
    public static ScalableCorridorPlanningSession BeginPlanning(
        DesignScene metadata,
        CorridorOptions options,
        CorridorBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default,
        CorridorSourceTraversal? sourceTraversal = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(options);
        batchOptions ??= new();
        batchOptions.Validate();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        return new(metadata, options, batchOptions, sourceTraversal, cancellationToken);
    }

    public static ScalableCorridorPlan CreatePlan(
        CorridorReplayScene globalViaReplay,
        CorridorOptions options,
        CorridorBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(globalViaReplay);
        ScalableCorridorPlanningSession planning = BeginPlanning(
            globalViaReplay.Scene, options, batchOptions, cancellationToken,
            globalViaReplay.SourceTraversal);
        planning.AcceptReplay(globalViaReplay, cancellationToken);
        return planning.Complete(cancellationToken);
    }

    internal static ScalableCorridorPlan CreatePlan(
        DesignScene scene,
        IReadOnlyList<PlannedVia> identities,
        IReadOnlyList<(string PairName, string PositiveNet, string NegativeNet)> discovered,
        CorridorOptions options,
        CorridorBatchOptions batchOptions,
        HashSet<string> warnings,
        HashSet<string> blockingWarnings,
        CorridorSourceOrdering sourceOrdering,
        CancellationToken cancellationToken)
    {
        double scale = NativeScale(scene.Document.NativeUnits);
        var viasByNet = identities
            .Where(item => item.Object.Via is not null && item.Object.NetName is not null)
            .GroupBy(item => item.Object.NetName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Reverse().ToArray(),
                StringComparer.OrdinalIgnoreCase);
        ModuleObject? module = string.IsNullOrWhiteSpace(options.ModuleName)
            ? null
            : scene.Modules.RequireComplete().SingleOrDefault(item => string.Equals(
                item.Name, options.ModuleName, StringComparison.OrdinalIgnoreCase));
        if (options.ModuleName is not null && module is null)
        {
            throw new InvalidOperationException(
                $"Module '{options.ModuleName}' was not found; no scalable analysis was planned.");
        }
        string[] checkLayers = scene.Layers.RequireComplete()
            .Where(layer => !layer.IsNegative && BareLayer(layer.Id.Value) is not ("TOP" or "BOTTOM"))
            .Select(layer => layer.Id.Value)
            .ToArray();

        var seeds = ImmutableArray.CreateBuilder<CorridorSeed>();
        int pairCount = 0;
        int corridorCount = 0;
        int pairGroup = 0;
        foreach ((string pairName, string positiveNet, string negativeNet) in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlannedVia[] positive = SelectVias(viasByNet, positiveNet, module?.Bounds);
            PlannedVia[] negative = SelectVias(viasByNet, negativeNet, module?.Bounds);
            if (positive.Length == 0 || negative.Length == 0)
            {
                continue;
            }
            pairCount++;
            List<(PlannedVia Positive, PlannedVia Negative)> pairs =
                PairVias(positive, negative, cancellationToken);
            corridorCount += pairs.Count;
            foreach ((PlannedVia p, PlannedVia n) in pairs)
            {
                CorridorSeed? seed = CreateSeed(
                    seeds.Count,
                    pairGroup,
                    scene,
                    options,
                    pairName,
                    positiveNet,
                    negativeNet,
                    p,
                    n,
                    checkLayers,
                    warnings,
                    scale);
                if (seed is not null)
                {
                    seeds.Add(seed);
                }
            }
            pairGroup++;
        }

        (ImmutableArray<CorridorSeed> assigned, ImmutableArray<CorridorReplayBatch> batches) =
            CreateBatches(seeds.ToImmutable(), batchOptions, scene.Query.ViaPadMeasurements);
        return new(
            scene,
            options,
            assigned,
            batches,
            pairCount,
            corridorCount,
            warnings.Order(StringComparer.Ordinal).ToImmutableArray(),
            blockingWarnings.Order(StringComparer.Ordinal).ToImmutableArray(),
            sourceOrdering);
    }

    internal static DesignScene CreateWitnessScene(
        DesignScene planningScene,
        ScalableCorridorFinding finding)
    {
        CorridorFindingWitnesses witnesses = finding.Witnesses;
        ImmutableArray<CopperObject> copper =
        [
            witnesses.PositiveVia,
            witnesses.NegativeVia,
            witnesses.Aggressor,
        ];
        // A ticket needs both its three captured witnesses and the padded
        // viewport that will be requested after native witness re-resolution.
        // Coverage remains explicitly partial: this extent does not claim
        // that other copper in the viewport was captured.
        DesignBounds bounds = DesignBounds.Union(
            copper.Select(item => item.Bounds).Aggregate(DesignBounds.Union),
            CorridorNavigation.CreateScope(finding.Finding));
        ImmutableArray<LayerId> requestedLayers = planningScene.Data.Layers
            .Select(item => item.Id)
            .ToImmutableArray();
        SceneQuery query = new()
        {
            Kind = SceneReadKind.RegionGeometry,
            Families = [DataFamily.Nets, DataFamily.Layers, DataFamily.Copper],
            CopperKinds = [CopperKind.Trace, CopperKind.Via, CopperKind.Shape],
            ViaPadMeasurements = planningScene.Query.ViaPadMeasurements,
            Layers = requestedLayers,
            Region = bounds,
            IncludeContours = false,
            MaximumObjects = Math.Max(32, requestedLayers.Length + 6),
        };
        string[] netNames = copper.Select(item => item.NetName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ImmutableArray<NetObject> nets = planningScene.Data.Nets
            .Where(net => netNames.Contains(net.Name, StringComparer.OrdinalIgnoreCase))
            .ToImmutableArray();
        ImmutableArray<LayerObject> layers = planningScene.Data.Layers;
        const string selectedWitnessReason = "selected_witness_subset";
        var coverage = new CoverageReport(query.Families.Select(family =>
            family == DataFamily.Layers ? planningScene.Coverage[family] : new FamilyCoverage(
                family,
                DataAvailability.Available,
                DataCompleteness.Partial,
                Reasons: [selectedWitnessReason])));
        var data = new SceneData
        {
            Nets = nets,
            Layers = layers,
            Copper = copper,
            CopperScope = new([], Enum.GetValues<CopperKind>().Select(kind => new CopperKindCoverage(
                kind,
                query.CopperKinds.Contains(kind) ? DataAvailability.Available : DataAvailability.NotRequested,
                DataCompleteness.Partial,
                GeometryFidelity.Unknown,
                [selectedWitnessReason])).ToImmutableArray()),
        };
        var identity = new SceneIdentity(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new("pd.pcbtools.scalable-corridor", CorridorAnalyzer.Algorithm, false,
                "capture-scoped-three-object-witness"));
        return new(
            identity,
            planningScene.Document with { Bounds = bounds },
            query,
            coverage,
            data);
    }

    private static CorridorSeed? CreateSeed(
        int id,
        int pairGroup,
        DesignScene scene,
        CorridorOptions options,
        string pairName,
        string positiveNet,
        string negativeNet,
        PlannedVia positive,
        PlannedVia negative,
        string[] checkLayers,
        HashSet<string> warnings,
        double scale)
    {
        ViaSpan p = positive.Object.Via!;
        ViaSpan n = negative.Object.Via!;
        double span = CorridorGeometry.Distance(p.Position, n.Position);
        if (span * scale < 0.01)
        {
            return null;
        }
        string[] targetLayers = Layers(p, warnings, positive.OrderingOrdinal)
            .Where(layer => Layers(n, warnings, negative.OrderingOrdinal)
                .Contains(layer, StringComparer.OrdinalIgnoreCase) &&
                checkLayers.Contains(layer, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (targetLayers.Length == 0)
        {
            return null;
        }
        string widthLayer = targetLayers[0];
        if (new[] { p, n }.Any(via => via.Analysis is { PadMeasurementsRequested: false }))
        {
            warnings.Add(
                $"Pad and antipad measurements were not requested for one or both subject vias in pair {pairName}; its width cannot establish a clear result.");
        }
        double? anti = MaximumRadius(p, n, widthLayer, "antipad");
        double? pad = MaximumRadius(p, n, widthLayer, "regular");
        if (new[] { p, n }.Any(via => via.Analysis is null ||
            !via.Analysis.Pads.Any(measurement => measurement.Type == "antipad" &&
                string.Equals(measurement.RequestedLayer.Value, widthLayer,
                    StringComparison.OrdinalIgnoreCase))))
        {
            warnings.Add(
                $"One or both antipad measurements are unavailable for pair {pairName} on {widthLayer}; the observed/fallback width cannot establish a clear result.");
        }
        if (anti is null && pad is null)
        {
            warnings.Add(
                $"Pad and antipad measurements are unavailable for pair {pairName} on {widthLayer}; its width uses the reference estimate.");
        }
        double minimumWidth = scene.Document.NativeUnits == "millimeters" ? 0.025 / scale : 1;
        double halfWidth = Math.Max(minimumWidth,
            (anti ?? pad ?? span * 0.3) + options.MarginMils);
        double halfLength = span / 2;
        DesignPoint center = CorridorGeometry.Interpolate(p.Position, n.Position, 0.5);
        var box = new CorridorBox(
            center,
            (double)(n.Position.X - p.Position.X) / span,
            (double)(n.Position.Y - p.Position.Y) / span,
            halfLength,
            halfWidth);
        decimal extent = checked((decimal)(halfLength + halfWidth + 1 / scale));
        var bounds = new DesignBounds(
            new(center.X - extent, center.Y - extent),
            new(center.X + extent, center.Y + extent));
        return new(
            id,
            pairGroup,
            pairName,
            positiveNet,
            negativeNet,
            positive,
            negative,
            targetLayers.ToImmutableArray(),
            widthLayer,
            center,
            box,
            halfWidth,
            halfLength,
            bounds,
            scale,
            []);
    }

    private static (ImmutableArray<CorridorSeed>, ImmutableArray<CorridorReplayBatch>)
        CreateBatches(
            ImmutableArray<CorridorSeed> seeds,
            CorridorBatchOptions options,
            ViaPadMeasurementSelection viaPadMeasurements)
    {
        var mutable = new List<MutableBatch>();
        foreach (CorridorSeed seed in seeds)
        {
            foreach (string layer in seed.TargetLayers)
            {
                MutableBatch? selected = mutable.FirstOrDefault(batch =>
                    string.Equals(batch.Layer.Value, layer, StringComparison.OrdinalIgnoreCase) &&
                    batch.Region.Intersects(seed.BroadPhaseBounds) &&
                    Fits(DesignBounds.Union(batch.Region, seed.BroadPhaseBounds), options));
                if (selected is null)
                {
                    if (!Fits(seed.BroadPhaseBounds, options))
                    {
                        throw new InvalidOperationException(
                            $"Corridor {seed.Id} exceeds the configured bounded replay dimensions.");
                    }
                    selected = new(mutable.Count, new(layer), seed.BroadPhaseBounds);
                    mutable.Add(selected);
                }
                else
                {
                    selected.Region = DesignBounds.Union(selected.Region, seed.BroadPhaseBounds);
                }
                selected.SeedIds.Add(seed.Id);
            }
        }
        ImmutableArray<CorridorReplayBatch> batches = mutable.Select(batch => new CorridorReplayBatch(
            batch.Id,
            batch.Layer,
            batch.Region,
            options.MaximumMaterializedObjects,
            Array.AsReadOnly(batch.SeedIds.ToArray()),
            viaPadMeasurements)).ToImmutableArray();
        ImmutableArray<CorridorSeed> assigned = seeds.Select(seed => seed with
        {
            BatchIds = batches.Where(batch => batch.SeedIds.Contains(seed.Id))
                .Select(batch => batch.Id)
                .ToImmutableArray(),
        }).ToImmutableArray();
        return (assigned, batches);
    }

    private static bool Fits(DesignBounds bounds, CorridorBatchOptions options) =>
        bounds.Width <= options.MaximumWidthMils &&
        bounds.Height <= options.MaximumHeightMils;

    private static void ValidateOptions(CorridorOptions options)
    {
        if (!double.IsFinite(options.MarginMils) || options.MarginMils is < 0 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static PlannedVia[] SelectVias(
        Dictionary<string, PlannedVia[]> vias,
        string net,
        DesignBounds? bounds) =>
        !vias.TryGetValue(net, out PlannedVia[]? values)
            ? []
            : values.Where(value => bounds is null ||
                CorridorGeometry.Contains(bounds.Value, value.Object.Via!.Position)).ToArray();

    private static List<(PlannedVia Positive, PlannedVia Negative)> PairVias(
        PlannedVia[] positive,
        PlannedVia[] negative,
        CancellationToken cancellationToken)
    {
        var remaining = negative.ToList();
        var result = new List<(PlannedVia, PlannedVia)>();
        foreach (PlannedVia p in positive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining.Count == 0)
            {
                break;
            }
            PlannedVia? closest = null;
            double distance = double.PositiveInfinity;
            foreach (PlannedVia n in remaining)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double candidate = CorridorGeometry.Distance(
                    p.Object.Via!.Position, n.Object.Via!.Position);
                if (candidate < distance)
                {
                    closest = n;
                    distance = candidate;
                }
            }
            if (closest is not null && distance < 500)
            {
                result.Add((p, closest));
                remaining.Remove(closest);
            }
        }
        result.Reverse();
        return result;
    }

    private static string[] Layers(ViaSpan via, HashSet<string> warnings, long ordinal)
    {
        if (via.Analysis is { ActiveLayersAvailable: true } &&
            via.BackdrillStatus is "current" or "not_started")
        {
            return via.ActiveLayers.Select(layer => layer.Value).ToArray();
        }
        if (via.Analysis?.BackdrillExclusion == "EXCLUDE_BOTH" ||
            via.BackdrillStatus == "not_started")
        {
            return via.OriginalLayers.Select(layer => layer.Value).ToArray();
        }
        warnings.Add(
            $"Backdrill/active layers are {via.BackdrillStatus} for capture object {ordinal + 1}; original layers are screened conservatively, not certified clear.");
        return via.OriginalLayers.Select(layer => layer.Value).ToArray();
    }

    private static double? MaximumRadius(
        ViaSpan positive,
        ViaSpan negative,
        string layer,
        string type)
    {
        ViaPadMeasurement[] pads = new[] { positive, negative }
            .Where(via => via.Analysis is not null)
            .SelectMany(via => via.Analysis!.Pads)
            .Where(pad => pad.Type == type && string.Equals(
                pad.RequestedLayer.Value, layer, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return pads.Length == 0
            ? null
            : pads.Max(pad => (double)Math.Max(pad.ExtentX.Mils, pad.ExtentY.Mils) / 2);
    }

    private static double NativeScale(string units) => units == "mils" ? 1 : 0.0254;

    private static string BareLayer(string layer) =>
        layer.Replace("ETCH/", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToUpperInvariant();

    private sealed class MutableBatch
    {
        public MutableBatch(int id, LayerId layer, DesignBounds region)
        {
            Id = id;
            Layer = layer;
            Region = region;
        }

        public int Id { get; }
        public LayerId Layer { get; }
        public DesignBounds Region { get; set; }
        public List<int> SeedIds { get; } = [];
    }
}

internal sealed record PlannedVia(
    CopperObject Object,
    CorridorSourceIdentity Identity,
    long OrderingOrdinal);

internal sealed record CorridorSeed(
    int Id,
    int PairGroup,
    string PairName,
    string PositiveNet,
    string NegativeNet,
    PlannedVia Positive,
    PlannedVia Negative,
    ImmutableArray<string> TargetLayers,
    string WidthSourceLayer,
    DesignPoint Center,
    CorridorBox Box,
    double HalfWidth,
    double HalfLength,
    DesignBounds BroadPhaseBounds,
    double NativeScale,
    ImmutableArray<int> BatchIds);
