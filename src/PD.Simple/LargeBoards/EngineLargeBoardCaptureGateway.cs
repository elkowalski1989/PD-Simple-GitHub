using System.Collections.Immutable;
using System.Diagnostics;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

namespace PD.Simple.LargeBoards;

/// <summary>
/// Staged capture contract for request-prioritized native capture. The gateway
/// freezes the in-memory board once; the caller then issues sequential captures
/// (for example a via catalog followed by planned-region detail) against that
/// one frozen observation. The single-capture gateway contract is unchanged.
/// </summary>
internal interface IStagedLargeBoardCaptureGateway
{
    ValueTask<IStagedLargeBoardCaptureObservation> BeginObservationAsync(
        LargeBoardCaptureBudgets budgets,
        CancellationToken cancellationToken);
}

/// <summary>
/// Owns one frozen native board for sequential staged captures. Exactly one
/// <see cref="CaptureAsync"/> may run at a time; overlapping calls are rejected
/// and the owner must await each stage before starting the next. Returned stores
/// are independent: the owner may dispose one stage before capturing the next.
/// <see cref="SourceDocument"/> is the frozen observation's own source identity,
/// never the primary editor identity. Disposal releases the isolated worker.
/// </summary>
internal interface IStagedLargeBoardCaptureObservation : IAsyncDisposable
{
    WorkspaceDocumentIdentity SourceDocument { get; }

    /// <summary>
    /// Frozen-export and worker-startup cost, charged once to this lease. Staged
    /// per-capture totals cover only that stage's capture and plan preparation.
    /// </summary>
    TimeSpan StartupElapsed { get; }

    /// <summary>
    /// Captures one stage from the frozen board. Demand is passed through
    /// untouched; null retains full-capture behavior. The caller skips stages
    /// with no work instead of issuing an empty capture.
    /// </summary>
    ValueTask<ILargeBoardCaptureStore<DesignScene>> CaptureAsync(
        LargeBoardCaptureBudgets budgets,
        EngineBulkCaptureDemand? demand,
        CancellationToken cancellationToken);
}

internal sealed class EngineLargeBoardCaptureGateway
    : ILargeBoardCaptureGateway<DesignScene>, IStagedLargeBoardCaptureGateway
{
    private readonly AllegroWorkspace _workspace;
    private readonly Func<string?> _nativeProductProvider;

    public EngineLargeBoardCaptureGateway(
        AllegroWorkspace workspace,
        Func<string?>? nativeProductProvider = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _nativeProductProvider = nativeProductProvider ??
            (() => Environment.GetEnvironmentVariable("PD_SIMPLE_NATIVE_PRODUCT"));
    }

    internal static string RequireNativeProduct(string? product)
    {
        if (string.IsNullOrWhiteSpace(product))
        {
            throw new InvalidOperationException(
                "Set Background capture product in the PD Simple header to the " +
                "Allegro -product token for the isolated read-only worker.");
        }

        if (product.Length > 128 ||
            product.Any(character => !(
                character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '_')))
        {
            throw new ArgumentException(
                "Background capture product must be 1–128 ASCII letters, digits, or underscores.",
                nameof(product));
        }

        return product;
    }

    public async ValueTask<ILargeBoardCaptureStore<DesignScene>> CaptureAsync(
        LargeBoardCaptureBudgets budgets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        var totalTimer = Stopwatch.StartNew();
        // Snapshot the explicit choice before starting the isolated worker.
        // The primary editor's Product Choices and board tier are untouched.
        string nativeProduct = RequireNativeProduct(_nativeProductProvider());
        await using EngineBoardObservation observation = await _workspace.BeginObservationAsync(
            new EngineBoardObservationOptions
            {
                NativeProduct = nativeProduct,
                MaximumSnapshotBytes = budgets.MaximumNativeSnapshotBytes,
            },
            cancellationToken);
        TimeSpan observationStartupElapsed = observation.StartupElapsed;
        return await CaptureFromObservationAsync(
            observation,
            budgets,
            demand: null,
            observationStartupElapsed,
            totalTimer,
            cancellationToken);
    }

    public async ValueTask<IStagedLargeBoardCaptureObservation> BeginObservationAsync(
        LargeBoardCaptureBudgets budgets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        // Snapshot the explicit choice before starting the isolated worker.
        // The primary editor's Product Choices and board tier are untouched.
        string nativeProduct = RequireNativeProduct(_nativeProductProvider());
        EngineBoardObservation observation = await _workspace.BeginObservationAsync(
            new EngineBoardObservationOptions
            {
                NativeProduct = nativeProduct,
                MaximumSnapshotBytes = budgets.MaximumNativeSnapshotBytes,
            },
            cancellationToken);
        return new EngineStagedLargeBoardCaptureObservation(observation);
    }

    /// <summary>
    /// Captures one sealed store from a frozen observation and prepares its
    /// replay plan, with the plan-index fallback and failure cleanup shared by
    /// the single-capture and staged paths. Demand passes through untouched; the
    /// single-capture path passes null and retains full-capture behavior.
    /// </summary>
    internal static async ValueTask<ILargeBoardCaptureStore<DesignScene>> CaptureFromObservationAsync(
        EngineBoardObservation observation,
        LargeBoardCaptureBudgets budgets,
        EngineBulkCaptureDemand? demand,
        TimeSpan? observationStartupElapsed,
        Stopwatch totalTimer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(budgets);
        ArgumentNullException.ThrowIfNull(totalTimer);
        LargeBoardStorageAdmission storage = LargeBoardStorageBudgets.ForCurrentTemporaryVolume(
            budgets);
        EngineBulkCapture capture = await observation.CaptureBulkSceneAsync(
            storage.Effective.ToEngineOptions() with { Demand = demand },
            cancellationToken);
        EngineBulkReplayPlan? plan = null;
        try
        {
            string? fallbackResource = null;
            var planTimer = Stopwatch.StartNew();
            try
            {
                plan = await capture.CreateReplayPlanAsync(
                    cancellationToken: cancellationToken);
            }
            catch (EngineBulkReplayResourceException error) when (
                error.Evidence.CleanupComplete && error.Evidence.PartialDataWithheld &&
                error.Evidence.Resource is "plan_indexed_roots" or "plan_root_index_bytes")
            {
                fallbackResource = error.Evidence.Resource;
            }
            planTimer.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            totalTimer.Stop();
            return new EngineLargeBoardCaptureStore(
                capture, plan, totalTimer.Elapsed, planTimer.Elapsed, fallbackResource,
                observationStartupElapsed, storage);
        }
        catch
        {
            try
            {
                if (plan is not null)
                {
                    await plan.DisposeAsync();
                }
            }
            finally
            {
                await capture.DisposeAsync();
            }
            throw;
        }
    }
}

internal sealed class EngineStagedLargeBoardCaptureObservation
    : IStagedLargeBoardCaptureObservation
{
    private readonly EngineBoardObservation _observation;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly object _sync = new();
    private bool _disposed;

    internal EngineStagedLargeBoardCaptureObservation(EngineBoardObservation observation)
    {
        _observation = observation ?? throw new ArgumentNullException(nameof(observation));
        StartupElapsed = observation.StartupElapsed;
    }

    public WorkspaceDocumentIdentity SourceDocument => _observation.SourceDocument;

    public TimeSpan StartupElapsed { get; }

    public async ValueTask<ILargeBoardCaptureStore<DesignScene>> CaptureAsync(
        LargeBoardCaptureBudgets budgets,
        EngineBulkCaptureDemand? demand,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        ThrowIfDisposed();
        bool entered;
        try
        {
            entered = await _captureGate.WaitAsync(0, cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            ThrowIfDisposed();
            throw;
        }
        if (!entered)
        {
            throw new InvalidOperationException(
                "Staged captures on one observation must run sequentially. " +
                "Await the in-flight capture before starting the next stage.");
        }
        try
        {
            ThrowIfDisposed();
            // Startup stays charged once to this lease; the staged total covers
            // only this stage's capture and plan preparation.
            var totalTimer = Stopwatch.StartNew();
            return await EngineLargeBoardCaptureGateway.CaptureFromObservationAsync(
                _observation,
                budgets,
                demand,
                observationStartupElapsed: null,
                totalTimer,
                cancellationToken);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        // Serialize with an in-flight stage so worker cleanup is reliable.
        await _captureGate.WaitAsync();
        try
        {
            await _observation.DisposeAsync();
        }
        finally
        {
            _captureGate.Release();
        }
        _captureGate.Dispose();
    }

    private void ThrowIfDisposed()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}

internal sealed class EngineLargeBoardCaptureStore
    : IBatchedLargeBoardCaptureStore<DesignScene>
{
    private readonly EngineBulkCapture _capture;
    private readonly EngineBulkReplayPlan? _plan;

    public EngineLargeBoardCaptureStore(EngineBulkCapture capture)
        : this(capture, null, null, null, null)
    {
    }

    internal EngineLargeBoardCaptureStore(
        EngineBulkCapture capture,
        EngineBulkReplayPlan? plan,
        TimeSpan? totalElapsed,
        TimeSpan? planPreparationElapsed,
        string? fallbackResource,
        TimeSpan? observationStartupElapsed = null,
        LargeBoardStorageAdmission? storageAdmission = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _plan = plan;
        Info = Map(capture.Info, capture.Timing, totalElapsed,
            planPreparationElapsed, plan?.Info, fallbackResource,
            observationStartupElapsed, storageAdmission);
    }

    public LargeBoardCaptureStoreInfo Info { get; }

    public async ValueTask<LargeBoardReplayPayload<DesignScene>> ReplayAsync(
        SceneQuery query,
        LargeBoardReplayBudgets budgets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        EngineBulkReplayOptions options = budgets.ToEngineOptions();
        EngineBulkReplayResult replay = _plan is { } plan
            ? await plan.ReplayAsync(query, options, cancellationToken)
            : await _capture.ReplayAsync(query, options, cancellationToken);
        return MapReplay(replay);
    }

    public async ValueTask<LargeBoardReplayBatchPayload<DesignScene>> ReplayBatchAsync(
        IReadOnlyList<SceneQuery> queries,
        LargeBoardReplayBudgets budgets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(budgets);
        EngineBulkReplayOptions options = budgets.ToEngineOptions();
        EngineBulkBatchReplayResult batch = _plan is { } plan
            ? await plan.ReplayBatchAsync(queries, options, cancellationToken)
            : await _capture.ReplayBatchAsync(queries, options, cancellationToken);
        return new(
            batch.Results.Select(MapReplay).ToImmutableArray(),
            batch.RootIndexPasses,
            batch.UniquePagesRead,
            batch.UniqueRecordsRead,
            batch.RootQueryMemberships,
            batch.PageQueryMemberships,
            batch.LogicalPageMembershipBytes,
            Milliseconds(batch.ManifestValidation),
            Milliseconds(batch.RootIndexSelection),
            Milliseconds(batch.PageReadAndDecode),
            Milliseconds(batch.SceneMaterialization));
    }

    private static LargeBoardReplayPayload<DesignScene> MapReplay(
        EngineBulkReplayResult replay)
    {
        return new(
            replay.Scene,
            replay.Summary.CandidatePages,
            replay.Summary.PagesRead,
            replay.Summary.RecordsRead,
            replay.Summary.RecordsSelected,
            replay.Summary.SpatialRootsSelected,
            replay.ObjectIdentities.Select(identity => new LargeBoardReplayObjectIdentity(
                identity.Id,
                identity.CaptureOrdinal,
                identity.SourceIdentity,
                identity.Kind)
            {
                SourceTraversalOrdinal = identity.SourceTraversalOrdinal,
            }).ToImmutableArray(),
            replay.Timing is null ? null : new LargeBoardReplayPhaseTiming(
                Milliseconds(replay.Timing.Total),
                Milliseconds(replay.Timing.ManifestAndSealValidation),
                Milliseconds(replay.Timing.IndexSelection),
                Milliseconds(replay.Timing.PageReadAndHash),
                Milliseconds(replay.Timing.PageDecodeAndValidation),
                Milliseconds(replay.Timing.RecordSelection),
                Milliseconds(replay.Timing.SceneMaterialization),
                Milliseconds(replay.Timing.IdentityProjection)));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_plan is not null)
            {
                await _plan.DisposeAsync();
            }
        }
        finally
        {
            await _capture.DisposeAsync();
        }
    }

    private static LargeBoardCaptureStoreInfo Map(
        EngineBulkCaptureInfo info,
        EngineBulkCaptureTiming? timing,
        TimeSpan? totalElapsed,
        TimeSpan? planPreparationElapsed,
        EngineBulkReplayPlanInfo? planInfo,
        string? fallbackResource,
        TimeSpan? observationStartupElapsed,
        LargeBoardStorageAdmission? storageAdmission)
    {
        ImmutableArray<string> incomplete = info.Coverage
            .Where(item => item.Status is EngineBulkCoverageStatus.Partial or
                EngineBulkCoverageStatus.Unavailable)
            .Select(item => item.Family)
            .ToImmutableArray();
        CorridorSourceTraversal? sourceTraversal = MapSourceTraversal(info.SourceTraversal);
        return new(
            new LargeBoardCaptureIdentity(
                info.Identity.CaptureToken,
                info.Identity.SessionId,
                info.Identity.SessionGeneration,
                info.Identity.BoardGeneration,
                info.Identity.ProcessId,
                info.Identity.Design,
                info.Identity.ProtocolVersion)
            {
                SourceTraversal = sourceTraversal,
            },
            info.HasCompleteCoverage,
            info.PageCount,
            info.RecordCount,
            info.StoredBytes,
            info.Resources.Select(resource => new LargeBoardCaptureResource(
                resource.Kind,
                resource.Limit,
                resource.Observed,
                resource.Exhausted)).ToImmutableArray(),
            incomplete,
            timing is null ? null : new LargeBoardCapturePhaseTiming(
                Milliseconds(totalElapsed ?? timing.Total),
                Milliseconds(timing.NativeCommandRoundTrip),
                Milliseconds(timing.SdkPageTransferAndSeal),
                Milliseconds(timing.NativeRelease),
                Milliseconds(timing.EngineImportAndIndex),
                Milliseconds(planPreparationElapsed),
                Milliseconds(observationStartupElapsed)))
        {
            SourceTraversal = sourceTraversal,
            StorageAdmission = storageAdmission,
            ReplayPlan = planPreparationElapsed is null ? null : new LargeBoardReplayPlanPreparation(
                planInfo is not null,
                Milliseconds(planPreparationElapsed.Value),
                planInfo?.IndexedRoots,
                planInfo?.SerializedRootIndexBytes,
                planInfo?.RootIndexPasses,
                fallbackResource),
        };
    }

    private static CorridorSourceTraversal? MapSourceTraversal(
        EngineBulkSourceTraversal? traversal)
    {
        if (traversal is null)
        {
            return null;
        }
        EngineBulkFrozenSource? frozen = traversal.FrozenSource;
        WorkspaceDocumentIdentity? sourceDocument = frozen?.SourceDocument;
        return new CorridorSourceTraversal(
            traversal.Version,
            traversal.OrderingUniverse,
            traversal.SessionId,
            traversal.BoardGeneration,
            traversal.ObservationToken,
            traversal.RootCount)
        {
            FrozenSource = frozen is null ? null : new CorridorFrozenSource(
                frozen.ObservationId,
                frozen.InputSha256,
                frozen.WorkerProcessId,
                frozen.MaximumSourceRoots,
                frozen.SourceOrderVerified)
            {
                SourceDocument = sourceDocument is null ? null : new CorridorSourceDocument(
                    sourceDocument.SessionId,
                    sourceDocument.SessionGeneration,
                    sourceDocument.BoardGeneration,
                    sourceDocument.ProcessId,
                    sourceDocument.Design,
                    sourceDocument.ProtocolVersion),
                SourceSnapshotHash = frozen.SourceSnapshotHash,
                ExportedAt = frozen.ExportedAt,
            },
        };
    }

    private static long Milliseconds(TimeSpan value) =>
        checked((long)Math.Round(value.TotalMilliseconds));

    private static long? Milliseconds(TimeSpan? value) => value is null
        ? null
        : Milliseconds(value.Value);
}
