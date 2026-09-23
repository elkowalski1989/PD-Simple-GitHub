using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

namespace PD.Simple.LargeBoards;

internal sealed record LargeBoardCorridorReplayBudgets(
    LargeBoardReplayBudgets GlobalViaReplay,
    LargeBoardReplayBudgets BoundedBatchReplay);

internal sealed record LargeBoardCorridorPhaseTimings(
    long GlobalViaReplayMilliseconds,
    long PlanningMilliseconds,
    long BoundedReplayMilliseconds,
    long AnalysisMilliseconds,
    long TotalMilliseconds);

internal sealed record LargeBoardCorridorRunCounts(
    int PlannedBatches,
    int CompletedBatches,
    int GlobalCandidatePages,
    int GlobalPagesRead,
    long GlobalRecordsRead,
    long GlobalRecordsSelected,
    long BatchCandidatePages,
    long BatchPagesRead,
    long BatchRecordsRead,
    long BatchRecordsSelected,
    int GlobalMaterializedObjects = 0,
    int MaximumBatchMaterializedObjects = 0,
    int PlanningViaReplayCount = 0,
    int PlanningSubdivisionCount = 0,
    int RetainedSubjectViaCount = 0,
    int MaximumPlanningReplayMaterializedObjects = 0,
    int BatchReplayCount = 0,
    int BatchSubdivisionCount = 0,
    int SuccessfulBatchReplayGroups = 0,
    int BatchReplayGroupFallbacks = 0,
    int SharedRootIndexPasses = 0,
    int SharedPagesRead = 0,
    long SharedRecordsRead = 0,
    long SharedLogicalPageMembershipBytes = 0);

internal sealed record LargeBoardCorridorRunResult(
    LargeBoardCaptureIdentity CaptureIdentity,
    ScalableCorridorScan Scan,
    LargeBoardCorridorPhaseTimings Timings,
    LargeBoardCorridorRunCounts Counts)
{
    public LargeBoardCorridorReplayBudgets? ReplayBudgets { get; init; }
    public LargeBoardCaptureIdentity? PlanningCaptureIdentity { get; init; }
}

internal sealed record LargeBoardCorridorPlanResult(
    ScalableCorridorPlan Plan,
    LargeBoardCaptureIdentity PlanningCaptureIdentity,
    CorridorSourceTraversal? PlanningSourceTraversal,
    LargeBoardCorridorReplayBudgets ReplayBudgets,
    ImmutableArray<DesignBounds> BatchRegions,
    long GlobalViaReplayMilliseconds,
    long PlanningMilliseconds,
    long PlanningTotalMilliseconds,
    int GlobalCandidatePages,
    int GlobalPagesRead,
    long GlobalRecordsRead,
    long GlobalRecordsSelected,
    int PlanningViaReplayCount,
    int PlanningSubdivisionCount,
    int RetainedSubjectViaCount,
    int MaximumPlanningReplayMaterializedObjects)
{
    public DesignScene MetadataScene => Plan.PlanningScene;
    public int PlannedBatches => Plan.Batches.Count;
}

internal sealed class LargeBoardCorridorIncompleteException : InvalidOperationException
{
    public LargeBoardCorridorIncompleteException(string message)
        : base(message)
    {
    }
}

internal sealed class LargeBoardCorridorReplayException : InvalidOperationException
{
    public LargeBoardCorridorReplayException(
        string stage,
        SceneQuery query,
        LargeBoardReplayBudgets budgets,
        Exception innerException)
        : base($"Large-board corridor {stage} replay failed.", innerException)
    {
        Stage = stage;
        Query = query;
        Budgets = budgets;
    }

    public string Stage { get; }
    public SceneQuery Query { get; }
    public LargeBoardReplayBudgets Budgets { get; }
}

/// <summary>
/// Composes capture-scoped Engine replays with the scalable PcbTools corridor
/// analyzer. The runner returns only after every planned replay is complete;
/// failed, cancelled, or partial runs never publish a scan.
/// </summary>
internal static class LargeBoardCorridorRunner
{
    /// <summary>
    /// Stage A: replays metadata and all subject vias, then completes the
    /// scalable corridor plan. The returned plan holds no store reference;
    /// the caller may dispose the planning store before stage B.
    /// </summary>
    public static async Task<LargeBoardCorridorPlanResult> PlanAsync(
        ILargeBoardCaptureStore<DesignScene> store,
        CorridorOptions corridorOptions,
        CorridorBatchOptions batchOptions,
        LargeBoardCorridorReplayBudgets replayBudgets,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null,
        ViaPadMeasurementSelection? captureViaPadMeasurements = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(corridorOptions);
        ArgumentNullException.ThrowIfNull(batchOptions);
        ArgumentNullException.ThrowIfNull(replayBudgets);
        ArgumentNullException.ThrowIfNull(replayBudgets.GlobalViaReplay);
        ArgumentNullException.ThrowIfNull(replayBudgets.BoundedBatchReplay);
        cancellationToken.ThrowIfCancellationRequested();
        captureViaPadMeasurements ??= CorridorAnalyzer.CreateSceneQuery(
            pairPolicy: corridorOptions.PairPolicy).ViaPadMeasurements;

        CorridorSourceTraversal? sourceTraversal = ValidateStore(store);
        // Stage A may hold only vias while stage B adds trace/shape identities
        // from the same frozen source. Bound retention by the universe root
        // count when declared, not by one store's record count alone.
        long retentionCeiling = Math.Max(
            store.Info.RecordCount, sourceTraversal?.RootCount ?? 0);
        batchOptions = batchOptions with
        {
            MaximumRetainedSourceIdentities = (int)Math.Min(
                batchOptions.MaximumRetainedSourceIdentities, Math.Max(1L, retentionCeiling)),
        };

        var totalTimer = Stopwatch.StartNew();
        progress?.Report("Planning differential-pair corridors from the sealed board capture…");
        SceneQuery metadataQuery = CreateMetadataQuery(
            replayBudgets.GlobalViaReplay.MaximumMaterializedObjects, captureViaPadMeasurements);

        var phaseTimer = Stopwatch.StartNew();
        LargeBoardReplayPayload<DesignScene> metadataPayload = await ReplayAsync(
            store,
            "global-planning-metadata",
            metadataQuery,
            replayBudgets.GlobalViaReplay,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        long globalReplayMilliseconds = phaseTimer.ElapsedMilliseconds;

        phaseTimer.Restart();
        ScalableCorridorPlanningSession planning = ScalableCorridorAnalyzer.BeginPlanning(
            metadataPayload.Scene,
            corridorOptions,
            batchOptions,
            cancellationToken,
            sourceTraversal);
        long planningMilliseconds = phaseTimer.ElapsedMilliseconds;
        int globalCandidatePages = metadataPayload.CandidatePages;
        int globalPagesRead = metadataPayload.PagesRead;
        long globalRecordsRead = metadataPayload.RecordsRead;
        long globalRecordsSelected = metadataPayload.RecordsSelected;
        int planningViaReplayCount = 0;
        int planningSubdivisionCount = 0;
        int maximumPlanningReplayMaterializedObjects = 0;
        IBatchedLargeBoardCaptureStore<DesignScene>? batchedStore =
            store as IBatchedLargeBoardCaptureStore<DesignScene>;
        if (planning.HasSubjectNets)
        {
            decimal minimumTileSize = MinimumTileSize(metadataPayload.Scene.Document);

            void AcceptPlanningReplay(LargeBoardReplayPayload<DesignScene> payload)
            {
                cancellationToken.ThrowIfCancellationRequested();
                phaseTimer.Restart();
                planning.AcceptReplay(ToCorridorReplay(payload, sourceTraversal), cancellationToken);
                planningMilliseconds = checked(planningMilliseconds + phaseTimer.ElapsedMilliseconds);
                planningViaReplayCount++;
                maximumPlanningReplayMaterializedObjects = Math.Max(
                    maximumPlanningReplayMaterializedObjects, payload.Scene.Data.Copper.Length);
                globalCandidatePages = checked(globalCandidatePages + payload.CandidatePages);
                globalPagesRead = checked(globalPagesRead + payload.PagesRead);
                globalRecordsRead = checked(globalRecordsRead + payload.RecordsRead);
                globalRecordsSelected = checked(globalRecordsSelected + payload.RecordsSelected);
                progress?.Report($"Planned {planning.RetainedSubjectViaCount:N0} subject vias from {planningViaReplayCount:N0} bounded capture areas…");
            }

            async Task ReplayPlanningScalarAsync(DesignBounds tile)
            {
                var pending = new Stack<DesignBounds>();
                pending.Push(tile);
                while (pending.TryPop(out DesignBounds region))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SceneQuery viaQuery = CreatePlanningViaQuery(
                        region, captureViaPadMeasurements,
                        replayBudgets.GlobalViaReplay.MaximumMaterializedObjects);
                    phaseTimer.Restart();
                    LargeBoardReplayPayload<DesignScene> payload;
                    try
                    {
                        payload = await ReplayAsync(
                            store, "bounded-via-planning", viaQuery,
                            replayBudgets.GlobalViaReplay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (LargeBoardCorridorReplayException exception) when (
                        exception.InnerException is EngineBulkReplayResourceException
                        { Evidence.Resource: "materialized_objects" })
                    {
                        globalReplayMilliseconds = checked(
                            globalReplayMilliseconds + phaseTimer.ElapsedMilliseconds);
                        if (!TrySplit(region, minimumTileSize,
                                out DesignBounds first, out DesignBounds second))
                        {
                            throw new LargeBoardCorridorReplayException(
                                "bounded-via-planning-minimum-tile", viaQuery,
                                replayBudgets.GlobalViaReplay, exception.InnerException);
                        }
                        planningSubdivisionCount++;
                        progress?.Report($"Subdividing via planning area after a replay limit ({planningSubdivisionCount:N0} subdivisions)…");
                        // Closed regions overlap at the split. Capture identity
                        // deduplication retains touching and spanning vias once.
                        pending.Push(second);
                        pending.Push(first);
                        continue;
                    }
                    globalReplayMilliseconds = checked(
                        globalReplayMilliseconds + phaseTimer.ElapsedMilliseconds);
                    AcceptPlanningReplay(payload);
                }
            }

            async Task ReplayPlanningGroupAsync(
                DesignBounds[] tiles, int firstIndex, int count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (batchedStore is null || count == 1)
                {
                    for (int offset = 0; offset < count; offset++)
                    {
                        await ReplayPlanningScalarAsync(tiles[firstIndex + offset])
                            .ConfigureAwait(false);
                    }
                    return;
                }

                SceneQuery[] queries = Enumerable.Range(firstIndex, count)
                    .Select(index => CreatePlanningViaQuery(
                        tiles[index], captureViaPadMeasurements,
                        replayBudgets.GlobalViaReplay.MaximumMaterializedObjects))
                    .ToArray();
                LargeBoardReplayBatchPayload<DesignScene> group;
                phaseTimer.Restart();
                try
                {
                    group = await batchedStore.ReplayBatchAsync(
                        queries, replayBudgets.GlobalViaReplay,
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateBatchResult(group, queries);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (CanFallbackFromBatch(exception))
                {
                    globalReplayMilliseconds = checked(
                        globalReplayMilliseconds + phaseTimer.ElapsedMilliseconds);
                    int firstCount = count / 2;
                    await ReplayPlanningGroupAsync(
                        tiles, firstIndex, firstCount).ConfigureAwait(false);
                    await ReplayPlanningGroupAsync(
                        tiles, firstIndex + firstCount, count - firstCount)
                        .ConfigureAwait(false);
                    return;
                }
                catch (Exception exception)
                {
                    throw new LargeBoardCorridorReplayException(
                        "bounded-via-planning-group", queries[0],
                        replayBudgets.GlobalViaReplay, exception);
                }

                globalReplayMilliseconds = checked(
                    globalReplayMilliseconds + phaseTimer.ElapsedMilliseconds);
                foreach (LargeBoardReplayPayload<DesignScene> payload in group.Results)
                {
                    AcceptPlanningReplay(payload);
                }
            }

            foreach (DesignBounds[] tiles in PlanningTiles(
                         metadataPayload.Scene.Document.Bounds, batchOptions).Chunk(64))
            {
                await ReplayPlanningGroupAsync(tiles, 0, tiles.Length)
                    .ConfigureAwait(false);
            }
        }
        phaseTimer.Restart();
        ScalableCorridorPlan plan = planning.Complete(cancellationToken);
        planningMilliseconds = checked(planningMilliseconds + phaseTimer.ElapsedMilliseconds);
        totalTimer.Stop();

        ImmutableArray<DesignBounds> batchRegions = plan.Batches
            .Select(batch => batch.Region)
            .ToImmutableArray();
        return new(
            plan,
            store.Info.Identity,
            sourceTraversal,
            replayBudgets,
            batchRegions,
            globalReplayMilliseconds,
            planningMilliseconds,
            totalTimer.ElapsedMilliseconds,
            globalCandidatePages,
            globalPagesRead,
            globalRecordsRead,
            globalRecordsSelected,
            planningViaReplayCount,
            planningSubdivisionCount,
            planning.RetainedSubjectViaCount,
            maximumPlanningReplayMaterializedObjects);
    }

    /// <summary>
    /// Stage B: replays exactly the plan's bounded batches from a possibly
    /// different store of the same frozen observation, then finalizes the
    /// ordered scan. The scan keeps the actual scan capture identity and
    /// validates compatible source traversal through the plan.
    /// </summary>
    public static async Task<LargeBoardCorridorRunResult> ScanAsync(
        ILargeBoardCaptureStore<DesignScene> scanStore,
        LargeBoardCorridorPlanResult planning,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(scanStore);
        ArgumentNullException.ThrowIfNull(planning);
        ArgumentNullException.ThrowIfNull(planning.Plan);
        ArgumentNullException.ThrowIfNull(planning.ReplayBudgets);
        ArgumentNullException.ThrowIfNull(planning.ReplayBudgets.GlobalViaReplay);
        ArgumentNullException.ThrowIfNull(planning.ReplayBudgets.BoundedBatchReplay);
        cancellationToken.ThrowIfCancellationRequested();

        CorridorSourceTraversal? scanTraversal = ValidateStore(scanStore);
        ScalableCorridorPlan plan = planning.Plan;
        LargeBoardCorridorReplayBudgets replayBudgets = planning.ReplayBudgets;

        var totalTimer = Stopwatch.StartNew();
        var phaseTimer = Stopwatch.StartNew();
        ScalableCorridorScanSession session = plan.BeginScan(scanStore.Info.SourceTraversal);
        long batchCandidatePages = 0;
        long batchPagesRead = 0;
        long batchRecordsRead = 0;
        long batchRecordsSelected = 0;
        int completedBatches = 0;
        int batchReplayCount = 0;
        int batchSubdivisionCount = 0;
        int maximumBatchMaterializedObjects = 0;
        long boundedReplayMilliseconds = 0;
        long batchAnalysisMilliseconds = 0;
        int successfulBatchReplayGroups = 0;
        int batchReplayGroupFallbacks = 0;
        int sharedRootIndexPasses = 0;
        int sharedPagesRead = 0;
        long sharedRecordsRead = 0;
        long sharedLogicalPageMembershipBytes = 0;
        decimal minimumBatchTileSize = MinimumTileSize(planning.MetadataScene.Document);
        IBatchedLargeBoardCaptureStore<DesignScene>? batchedStore =
            scanStore as IBatchedLargeBoardCaptureStore<DesignScene>;
        LargeBoardReplayBudgets BudgetsFor(CorridorReplayBatch batch) =>
            replayBudgets.BoundedBatchReplay with
            {
                MaximumMaterializedObjects = Math.Min(
                    batch.MaximumMaterializedObjects,
                    replayBudgets.BoundedBatchReplay.MaximumMaterializedObjects),
            };

        void AcceptReplay(
            CorridorReplayBatch batch,
            LargeBoardReplayPayload<DesignScene> payload)
        {
            cancellationToken.ThrowIfCancellationRequested();
            phaseTimer.Restart();
            session.AcceptBatch(batch, ToCorridorReplay(payload, scanTraversal), cancellationToken);
            batchAnalysisMilliseconds = checked(batchAnalysisMilliseconds + phaseTimer.ElapsedMilliseconds);
            batchReplayCount++;
            maximumBatchMaterializedObjects = Math.Max(
                maximumBatchMaterializedObjects, payload.Scene.Data.Copper.Length);
            batchCandidatePages = checked(batchCandidatePages + payload.CandidatePages);
            batchPagesRead = checked(batchPagesRead + payload.PagesRead);
            batchRecordsRead = checked(batchRecordsRead + payload.RecordsRead);
            batchRecordsSelected = checked(batchRecordsSelected + payload.RecordsSelected);
            progress?.Report($"Screened {batchReplayCount:N0} bounded capture areas; corridor group {completedBatches + 1:N0} of {plan.Batches.Count:N0}…");
        }

        async Task ReplayScalarBatchAsync(CorridorReplayBatch batch)
        {
            LargeBoardReplayBudgets batchBudgets = BudgetsFor(batch);
            var pending = new Stack<DesignBounds>();
            pending.Push(batch.Region);
            while (pending.TryPop(out DesignBounds region))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SceneQuery batchQuery = batch.CreateQuery() with
                {
                    Region = region,
                    MaximumObjects = batchBudgets.MaximumMaterializedObjects,
                };
                phaseTimer.Restart();
                LargeBoardReplayPayload<DesignScene> batchPayload;
                try
                {
                    batchPayload = await ReplayAsync(scanStore, $"bounded-batch-{batch.Id}",
                        batchQuery, batchBudgets, cancellationToken).ConfigureAwait(false);
                }
                catch (LargeBoardCorridorReplayException exception) when (
                    exception.InnerException is EngineBulkReplayResourceException
                    { Evidence.Resource: "materialized_objects" or "pages_read" or "records_read" })
                {
                    boundedReplayMilliseconds = checked(boundedReplayMilliseconds + phaseTimer.ElapsedMilliseconds);
                    if (!TrySplit(region, minimumBatchTileSize,
                            out DesignBounds first, out DesignBounds second))
                    {
                        throw new LargeBoardCorridorReplayException(
                            $"bounded-batch-{batch.Id}-minimum-tile", batchQuery,
                            batchBudgets, exception.InnerException);
                    }
                    session.SplitBatchRegion(batch, region, first, second);
                    batchSubdivisionCount++;
                    progress?.Report($"Subdividing corridor group {completedBatches + 1:N0} after a replay limit ({batchSubdivisionCount:N0} subdivisions)…");
                    pending.Push(second);
                    pending.Push(first);
                    continue;
                }
                cancellationToken.ThrowIfCancellationRequested();
                boundedReplayMilliseconds = checked(boundedReplayMilliseconds + phaseTimer.ElapsedMilliseconds);
                AcceptReplay(batch, batchPayload);
            }
        }

        async Task ReplayGroupAsync(int firstIndex, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (batchedStore is null || count == 1)
            {
                await ReplayScalarBatchAsync(plan.Batches[firstIndex]).ConfigureAwait(false);
                completedBatches++;
                progress?.Report($"Screened corridor area {completedBatches:N0} of {plan.Batches.Count:N0}…");
                return;
            }

            LargeBoardReplayBudgets groupBudgets = BudgetsFor(plan.Batches[firstIndex]);
            SceneQuery[] queries = Enumerable.Range(firstIndex, count)
                .Select(index => plan.Batches[index].CreateQuery() with
                {
                    MaximumObjects = groupBudgets.MaximumMaterializedObjects,
                })
                .ToArray();
            LargeBoardReplayBatchPayload<DesignScene> group;
            phaseTimer.Restart();
            try
            {
                group = await batchedStore.ReplayBatchAsync(
                    queries, groupBudgets, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ValidateBatchResult(group, queries);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (CanFallbackFromBatch(exception))
            {
                boundedReplayMilliseconds = checked(boundedReplayMilliseconds + phaseTimer.ElapsedMilliseconds);
                batchReplayGroupFallbacks++;
                int firstCount = count / 2;
                await ReplayGroupAsync(firstIndex, firstCount).ConfigureAwait(false);
                await ReplayGroupAsync(firstIndex + firstCount, count - firstCount)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                throw new LargeBoardCorridorReplayException(
                    $"bounded-batch-group-{plan.Batches[firstIndex].Id}",
                    queries[0],
                    groupBudgets,
                    exception);
            }

            boundedReplayMilliseconds = checked(boundedReplayMilliseconds + phaseTimer.ElapsedMilliseconds);
            successfulBatchReplayGroups++;
            sharedRootIndexPasses = checked(sharedRootIndexPasses + group.RootIndexPasses);
            sharedPagesRead = checked(sharedPagesRead + group.UniquePagesRead);
            sharedRecordsRead = checked(sharedRecordsRead + group.UniqueRecordsRead);
            sharedLogicalPageMembershipBytes = checked(
                sharedLogicalPageMembershipBytes + group.LogicalPageMembershipBytes);
            for (int offset = 0; offset < count; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AcceptReplay(plan.Batches[firstIndex + offset], group.Results[offset]);
                completedBatches++;
                progress?.Report($"Screened corridor area {completedBatches:N0} of {plan.Batches.Count:N0}…");
            }
        }

        for (int firstIndex = 0; firstIndex < plan.Batches.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = 1;
            if (batchedStore is not null)
            {
                int groupObjectBudget = BudgetsFor(plan.Batches[firstIndex]).MaximumMaterializedObjects;
                while (count < 64 && firstIndex + count < plan.Batches.Count &&
                    BudgetsFor(plan.Batches[firstIndex + count]).MaximumMaterializedObjects ==
                        groupObjectBudget)
                {
                    count++;
                }
            }
            await ReplayGroupAsync(firstIndex, count).ConfigureAwait(false);
            firstIndex += count;
        }
        phaseTimer.Restart();
        ScalableCorridorScan scan = session.Complete(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!scan.HasCompleteInputs)
        {
            throw new LargeBoardCorridorIncompleteException(
                "One or more corridor replays reported blocking coverage gaps; no corridor result was produced.");
        }
        long analysisMilliseconds = checked(batchAnalysisMilliseconds + phaseTimer.ElapsedMilliseconds);

        totalTimer.Stop();
        return new(
            scanStore.Info.Identity,
            scan,
            new(
                planning.GlobalViaReplayMilliseconds,
                planning.PlanningMilliseconds,
                boundedReplayMilliseconds,
                analysisMilliseconds,
                checked(planning.PlanningTotalMilliseconds + totalTimer.ElapsedMilliseconds)),
            new(
                plan.Batches.Count,
                completedBatches,
                planning.GlobalCandidatePages,
                planning.GlobalPagesRead,
                planning.GlobalRecordsRead,
                planning.GlobalRecordsSelected,
                batchCandidatePages,
                batchPagesRead,
                batchRecordsRead,
                batchRecordsSelected,
                planning.RetainedSubjectViaCount,
                maximumBatchMaterializedObjects,
                planning.PlanningViaReplayCount,
                planning.PlanningSubdivisionCount,
                planning.RetainedSubjectViaCount,
                planning.MaximumPlanningReplayMaterializedObjects,
                batchReplayCount,
                batchSubdivisionCount,
                successfulBatchReplayGroups,
                batchReplayGroupFallbacks,
                sharedRootIndexPasses,
                sharedPagesRead,
                sharedRecordsRead,
                sharedLogicalPageMembershipBytes))
        {
            ReplayBudgets = planning.ReplayBudgets,
            PlanningCaptureIdentity = planning.PlanningCaptureIdentity,
        };
    }

    /// <summary>
    /// Single-store compatibility wrapper. Planning and scanning run against
    /// the same sealed store; staged callers use PlanAsync/ScanAsync directly.
    /// </summary>
    public static async Task<LargeBoardCorridorRunResult> RunAsync(
        ILargeBoardCaptureStore<DesignScene> store,
        CorridorOptions corridorOptions,
        CorridorBatchOptions batchOptions,
        LargeBoardCorridorReplayBudgets replayBudgets,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null,
        ViaPadMeasurementSelection? captureViaPadMeasurements = null)
    {
        LargeBoardCorridorPlanResult planning = await PlanAsync(
            store,
            corridorOptions,
            batchOptions,
            replayBudgets,
            cancellationToken,
            progress,
            captureViaPadMeasurements).ConfigureAwait(false);
        return await ScanAsync(
            store,
            planning,
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    private static CorridorSourceTraversal? ValidateStore(
        ILargeBoardCaptureStore<DesignScene> store)
    {
        if (!store.Info.HasCompleteCoverage || store.Info.IncompleteFamilies.Length != 0)
        {
            throw new LargeBoardCorridorIncompleteException(
                "The retained board capture is incomplete; no corridor result was produced.");
        }
        CorridorSourceTraversal? sourceTraversal = store.Info.SourceTraversal;
        if (sourceTraversal is not null &&
            (!sourceTraversal.IsSupported ||
             sourceTraversal.SessionId != store.Info.Identity.SessionId ||
             sourceTraversal.BoardGeneration != store.Info.Identity.BoardGeneration ||
             sourceTraversal.ObservationToken != store.Info.Identity.CaptureToken))
        {
            throw new InvalidDataException("The capture's source ordering declaration does not match its identity.");
        }
        return sourceTraversal;
    }

    private static SceneQuery CreateMetadataQuery(
        int maximumMaterializedObjects, ViaPadMeasurementSelection viaPadMeasurements) =>
        SceneQuery.CompleteBoard(includeContours: false) with
        {
            Families =
            [
                DataFamily.Nets,
                DataFamily.Modules,
                DataFamily.Layers,
                DataFamily.Connectivity,
            ],
            CopperKinds = [],
            ViaPadMeasurements = viaPadMeasurements,
            MaximumObjects = maximumMaterializedObjects,
        };

    private static SceneQuery CreatePlanningViaQuery(
        DesignBounds region, ViaPadMeasurementSelection viaPadMeasurements, int maximumMaterializedObjects) => new()
        {
            Kind = SceneReadKind.RegionGeometry,
            Families = [DataFamily.Copper],
            CopperKinds = [CopperKind.Via],
            ViaPadMeasurements = viaPadMeasurements,
            Region = region,
            IncludeContours = false,
            MaximumObjects = maximumMaterializedObjects,
        };

    private static IEnumerable<DesignBounds> PlanningTiles(DesignBounds bounds, CorridorBatchOptions options)
    {
        decimal x = bounds.Minimum.X;
        do
        {
            decimal right = Math.Min(bounds.Maximum.X, x + options.MaximumWidthMils);
            decimal y = bounds.Minimum.Y;
            do
            {
                decimal top = Math.Min(bounds.Maximum.Y, y + options.MaximumHeightMils);
                yield return new(new(x, y), new(right, top));
                y = top;
            }
            while (y < bounds.Maximum.Y);
            x = right;
        }
        while (x < bounds.Maximum.X);
    }

    private static decimal MinimumTileSize(DocumentContext document)
    {
        decimal nativeQuantum = 1;
        for (int digit = 0; digit < document.NativePrecision; digit++)
        {
            nativeQuantum /= 10;
        }
        // Engine scene coordinates are canonical mils, even for millimeter boards.
        return document.NativeUnits == "millimeters" ? nativeQuantum / 0.0254m : nativeQuantum;
    }

    private static bool TrySplit(
        DesignBounds region, decimal minimumSize, out DesignBounds first, out DesignBounds second)
    {
        first = region;
        second = region;
        bool splitX = region.Width >= region.Height;
        decimal extent = splitX ? region.Width : region.Height;
        if (extent <= minimumSize)
        {
            return false;
        }
        if (splitX)
        {
            decimal middle = (region.Minimum.X + region.Maximum.X) / 2;
            first = new(region.Minimum, new(middle, region.Maximum.Y));
            second = new(new(middle, region.Minimum.Y), region.Maximum);
        }
        else
        {
            decimal middle = (region.Minimum.Y + region.Maximum.Y) / 2;
            first = new(region.Minimum, new(region.Maximum.X, middle));
            second = new(new(region.Minimum.X, middle), region.Maximum);
        }
        return true;
    }

    private static void ValidateBatchResult(
        LargeBoardReplayBatchPayload<DesignScene> batch,
        IReadOnlyList<SceneQuery> queries)
    {
        // RootIndexPasses counts authenticated capture-scoped index passes for this batch.
        // Ordinary replay reports 1; a batch reused under an authenticated replay plan
        // reports 0 because the single pass is reported once at plan creation.
        if (batch is null || batch.Results.IsDefault ||
            batch.Results.Length != queries.Count ||
            batch.RootIndexPasses < 0 || batch.RootIndexPasses > 1 ||
            batch.UniquePagesRead < 0 ||
            batch.UniqueRecordsRead < 0 || batch.RootQueryMemberships < 0 ||
            batch.PageQueryMemberships < 0 || batch.LogicalPageMembershipBytes < 0)
        {
            throw new InvalidDataException(
                "The sealed-store batch replay returned incomplete or invalid metrics.");
        }
        for (int index = 0; index < queries.Count; index++)
        {
            LargeBoardReplayPayload<DesignScene> payload = batch.Results[index];
            SceneQuery requested = queries[index];
            SceneQuery actual = payload.Scene.Query;
            if (actual.Kind != requested.Kind || actual.Region != requested.Region ||
                actual.MaximumObjects != requested.MaximumObjects ||
                actual.IncludeContours != requested.IncludeContours ||
                !actual.Families.SequenceEqual(requested.Families) ||
                !actual.CopperKinds.SequenceEqual(requested.CopperKinds) ||
                !actual.Layers.SequenceEqual(requested.Layers) ||
                actual.ViaPadMeasurements.Mode != requested.ViaPadMeasurements.Mode ||
                !actual.ViaPadMeasurements.NetNameSuffixes.SequenceEqual(
                    requested.ViaPadMeasurements.NetNameSuffixes) ||
                payload.ObjectIdentities.IsDefault ||
                payload.ObjectIdentities.Length != payload.Scene.Data.Copper.Length ||
                !payload.ObjectIdentities.Select(static identity => identity.Id)
                    .SequenceEqual(payload.Scene.Data.Copper.Select(static copper => copper.Id)))
            {
                throw new InvalidDataException(
                    $"The sealed-store batch replay changed query {index} or its ordered copper identities.");
            }
        }
    }

    private static bool CanFallbackFromBatch(Exception exception) => exception switch
    {
        NotSupportedException => true,
        EngineBulkReplayResourceException resource =>
            resource.Evidence.CleanupComplete &&
            resource.Evidence.PartialDataWithheld &&
            resource.Evidence.Resource is
                "materialized_objects" or "pages_read" or "records_read" or
                "batch_root_memberships" or "batch_page_memberships" or
                "batch_logical_page_bytes" or "batch_selected_records",
        _ => false,
    };

    private static async ValueTask<LargeBoardReplayPayload<DesignScene>> ReplayAsync(
        ILargeBoardCaptureStore<DesignScene> store,
        string stage,
        SceneQuery query,
        LargeBoardReplayBudgets budgets,
        CancellationToken cancellationToken)
    {
        try
        {
            return await store.ReplayAsync(query, budgets, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LargeBoardCorridorReplayException(
                stage,
                query,
                budgets,
                exception);
        }
    }

    private static CorridorReplayScene ToCorridorReplay(
        LargeBoardReplayPayload<DesignScene> payload,
        CorridorSourceTraversal? sourceTraversal)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ImmutableArray<CorridorSourceIdentity> identities = payload.ObjectIdentities
            .Select(identity => new CorridorSourceIdentity(
                identity.Id,
                identity.CaptureOrdinal,
                identity.SourceIdentity,
                identity.Kind)
            {
                SourceTraversalOrdinal = identity.SourceTraversalOrdinal,
            })
            .ToImmutableArray();
        return new(payload.Scene, identities)
        {
            SourceTraversal = sourceTraversal,
        };
    }
}
