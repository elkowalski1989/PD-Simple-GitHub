using System.Collections.Immutable;
using System.Diagnostics;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

namespace PD.Simple.LargeBoards;

internal sealed class EngineLargeBoardCaptureGateway
    : ILargeBoardCaptureGateway<DesignScene>
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
        LargeBoardStorageAdmission storage = LargeBoardStorageBudgets.ForCurrentTemporaryVolume(
            budgets);
        EngineBulkCapture capture = await observation.CaptureBulkSceneAsync(
            storage.Effective.ToEngineOptions(),
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
