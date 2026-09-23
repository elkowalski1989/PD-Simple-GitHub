using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;
using PD.Simple.LargeBoards;

namespace PD.Simple.Corridor;

internal sealed record DpViaCorridorCaptureResult(
    WorkspaceDocumentIdentity Document,
    LargeBoardCaptureStoreInfo Capture,
    LargeBoardCorridorRunResult Run)
{
    public AcquisitionDiagnosticReceipt? TerminalReceipt { get; init; }

    /// <summary>
    /// Stage A store info when planning used a separate capture from the
    /// scan. Null when planning and scanning shared one capture; <see
    /// cref="Capture"/> always identifies the scan capture.
    /// </summary>
    public LargeBoardCaptureStoreInfo? PlanningCapture { get; init; }

    /// <summary>
    /// Once-charged frozen-observation startup when a staged observation was
    /// used. Null for single-capture runs. Staged per-capture totals exclude
    /// this lease cost, so truthful acquisition accounting adds it once.
    /// </summary>
    public long? ObservationStartupMilliseconds { get; init; }
}

/// <summary>Owns one complete corridor run and the lifetime of its sealed store.</summary>
internal static class DpViaCorridorCaptureRunner
{
    public static async Task<DpViaCorridorCaptureResult> RunAsync(
        ILargeBoardCaptureGateway<DesignScene> gateway,
        IAcquisitionDiagnosticOwner diagnostics,
        Func<WorkspaceDocumentIdentity?> currentDocument,
        string applicationVersion,
        string engineVersion,
        CorridorOptions options,
        bool applicationVisible,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        SceneQuery query = CorridorAnalyzer.CreateSceneQuery(pairPolicy: options.PairPolicy);
        var budgets = new LargeBoardCaptureBudgets
        {
            Families = query.Families.Contains(DataFamily.Connectivity)
                ? query.Families
                : query.Families.Add(DataFamily.Connectivity),
            CopperKinds = query.CopperKinds,
            ViaPadMeasurements = query.ViaPadMeasurements,
            IncludeContours = false,
        };
        if (gateway is IStagedLargeBoardCaptureGateway stagedGateway)
        {
            return await RunStagedAsync(
                stagedGateway,
                diagnostics,
                currentDocument,
                applicationVersion,
                engineVersion,
                options,
                applicationVisible,
                cancellationToken,
                progress,
                query,
                budgets).ConfigureAwait(false);
        }
        var workflow = new LargeBoardCaptureWorkflow<DesignScene>(
            gateway, diagnostics, currentDocument, applicationVersion, engineVersion);
        var request = new LargeBoardCaptureRequest(
            "dp-via-corridor", "Run corridor check", applicationVisible, budgets);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        LargeBoardCaptureResult? capture = null;
        Exception? terminalFailure = null;
        bool? cleanupComplete = null;
        DpViaCorridorCaptureResult? result = null;
        AcquisitionDiagnosticReceipt? terminalReceipt = null;
        try
        {
            progress?.Report("Capturing the board for corridor screening…");
            capture = await workflow.CaptureAsync(request, cancellationToken).ConfigureAwait(false);
            WorkspaceDocumentIdentity document = LargeBoardPublicationFence.RequireCurrent(
                capture.Store.Identity, currentDocument());
            LargeBoardCorridorRunResult run = await workflow.RunWithCurrentCaptureAsync(
                async (store, token) => await LargeBoardCorridorRunner.RunAsync(
                    store,
                    options,
                    new CorridorBatchOptions(),
                    new(
                        new LargeBoardReplayBudgets { MaximumMaterializedObjects = 200_000 },
                        new LargeBoardReplayBudgets()),
                    token,
                    progress,
                    budgets.ViaPadMeasurements).ConfigureAwait(false),
                "Run corridor check",
                applicationVisible,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            LargeBoardPublicationFence.RequireCurrent(run.CaptureIdentity, currentDocument());
            result = new(document, capture.Store, run);
        }
        catch (Exception exception)
        {
            terminalFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                await workflow.DisposeAsync().ConfigureAwait(false);
                cleanupComplete = capture is not null
                    ? true
                    : terminalFailure is LargeBoardCaptureCancelledException cancelled
                        ? cancelled.CleanupComplete
                        : terminalFailure is not null
                            ? LargeBoardFailure.From(terminalFailure).CleanupComplete
                            : null;
            }
            catch (Exception cleanupFailure)
            {
                cleanupComplete = false;
                if (terminalFailure is null)
                {
                    terminalFailure = cleanupFailure;
                    throw;
                }
            }
            finally
            {
                AcquisitionTerminalState state = terminalFailure switch
                {
                    null => AcquisitionTerminalState.Complete,
                    OperationCanceledException => AcquisitionTerminalState.Cancelled,
                    _ => AcquisitionTerminalState.Failed,
                };
                long captureMilliseconds = capture?.Store.Timing?.TotalMilliseconds ?? 0;
                LargeBoardCorridorReplayException? replayFailure = FindReplayFailure(terminalFailure);
                terminalReceipt = AcquisitionDiagnosticReceiptFactory.Create(
                    request,
                    currentDocument(),
                    capture?.Store,
                    replayFailure?.Query ?? query,
                    replayFailure?.Budgets,
                    capture?.Store.Timing,
                    null,
                    applicationVersion,
                    engineVersion,
                    startedAt,
                    captureMilliseconds,
                    Math.Max(0, timer.ElapsedMilliseconds - captureMilliseconds),
                    state,
                    cleanupComplete,
                    terminalFailure is null ? null : LargeBoardFailure.From(
                        replayFailure?.InnerException ?? terminalFailure),
                    state == AcquisitionTerminalState.Complete
                        ? "Sealed corridor screening completed; the capture store was released."
                        : "Corridor screening stopped; no partial result was published.") with
                {
                    Feature = state == AcquisitionTerminalState.Complete
                        ? "dp-via-corridor-scan"
                        : "dp-via-corridor-run",
                    PartialDataWithheld = state != AcquisitionTerminalState.Complete,
                };
                await diagnostics.RetainAsync(terminalReceipt, CancellationToken.None).ConfigureAwait(false);
            }
        }
        return (result ?? throw new InvalidOperationException("The corridor scan produced no result.")) with
        {
            TerminalReceipt = terminalReceipt,
        };
    }

    /// <summary>
    /// Runs corridor screening as two sealed captures under one frozen
    /// observation: stage A captures the whole-board via catalog for
    /// planning, stage B captures the planned batch areas for the detailed
    /// scan. The final result is published only after complete deterministic
    /// replays; failures and cancellations retain no partial result and
    /// release both stores and the observation worker.
    /// </summary>
    private static async Task<DpViaCorridorCaptureResult> RunStagedAsync(
        IStagedLargeBoardCaptureGateway gateway,
        IAcquisitionDiagnosticOwner diagnostics,
        Func<WorkspaceDocumentIdentity?> currentDocument,
        string applicationVersion,
        string engineVersion,
        CorridorOptions options,
        bool applicationVisible,
        CancellationToken cancellationToken,
        IProgress<string>? progress,
        SceneQuery query,
        LargeBoardCaptureBudgets budgets)
    {
        var request = new LargeBoardCaptureRequest(
            "dp-via-corridor", "Run corridor check", applicationVisible, budgets);
        LargeBoardCaptureBudgets planningBudgets = budgets with
        {
            CopperKinds = [CopperKind.Via],
        };
        LargeBoardCaptureBudgets scanBudgets = budgets with
        {
            CopperKinds = [CopperKind.Trace, CopperKind.Via, CopperKind.Shape],
        };
        var replayBudgets = new LargeBoardCorridorReplayBudgets(
            new LargeBoardReplayBudgets { MaximumMaterializedObjects = 200_000 },
            new LargeBoardReplayBudgets());
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        IStagedLargeBoardCaptureObservation? observation = null;
        ILargeBoardCaptureStore<DesignScene>? planningStore = null;
        ILargeBoardCaptureStore<DesignScene>? scanStore = null;
        LargeBoardCaptureStoreInfo? planningInfo = null;
        LargeBoardCaptureStoreInfo? scanInfo = null;
        bool observationStarted = false;
        long observationStartupMilliseconds = 0;
        // Measured wall time around each awaited acquisition call, including
        // failed and cancelled attempts. Replay, disposal, and reporting stay
        // outside these windows so the receipt attributes acquisition truthfully.
        long beginObservationMilliseconds = 0;
        long planningCaptureMilliseconds = 0;
        long scanCaptureMilliseconds = 0;
        Exception? terminalFailure = null;
        bool? cleanupComplete = null;
        DpViaCorridorCaptureResult? result = null;
        AcquisitionDiagnosticReceipt? terminalReceipt = null;
        try
        {
            progress?.Report("Freezing the board for staged corridor screening\u2026");
            try
            {
                Stopwatch beginTimer = Stopwatch.StartNew();
                try
                {
                    observation = await gateway.BeginObservationAsync(budgets, cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    beginTimer.Stop();
                    beginObservationMilliseconds = beginTimer.ElapsedMilliseconds;
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LargeBoardFailure beginFailure = LargeBoardFailure.From(exception);
                throw new LargeBoardCaptureException(beginFailure.UserMessage, exception);
            }
            observationStarted = true;
            observationStartupMilliseconds = (long)Math.Round(observation.StartupElapsed.TotalMilliseconds);

            progress?.Report("Capturing the board via catalog for corridor planning\u2026");
            try
            {
                Stopwatch planningTimer = Stopwatch.StartNew();
                try
                {
                    planningStore = await observation.CaptureAsync(
                        planningBudgets, demand: null, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    planningTimer.Stop();
                    planningCaptureMilliseconds = planningTimer.ElapsedMilliseconds;
                }
                cancellationToken.ThrowIfCancellationRequested();
                planningInfo = RequireCompleteCapture(planningStore.Info);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                LargeBoardFailure stageFailure = LargeBoardFailure.From(exception);
                throw new LargeBoardCaptureException(stageFailure.UserMessage, exception);
            }
            WorkspaceDocumentIdentity document = LargeBoardPublicationFence.RequireCurrent(
                planningInfo, currentDocument());

            LargeBoardCorridorPlanResult planning = await WithStagedReplayAttributionAsync(
                () => LargeBoardCorridorRunner.PlanAsync(
                    planningStore,
                    options,
                    new CorridorBatchOptions(),
                    replayBudgets,
                    cancellationToken,
                    progress,
                    planningBudgets.ViaPadMeasurements)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            LargeBoardPublicationFence.RequireCurrent(planningInfo, currentDocument());

            LargeBoardCorridorRunResult run;
            if (planning.Plan.Batches.Count == 0)
            {
                progress?.Report(
                    "Corridor planning found no bounded areas; finishing the scan from the via catalog\u2026");
                scanStore = planningStore;
                planningStore = null;
                scanInfo = planningInfo;
                planningInfo = null;
                run = await WithStagedReplayAttributionAsync(
                    () => LargeBoardCorridorRunner.ScanAsync(
                        scanStore, planning, cancellationToken, progress)).ConfigureAwait(false);
            }
            else
            {
                EngineBulkCaptureDemand? demand = BuildScanDemand(planning.Plan.Batches);
                progress?.Report(demand is null
                    ? "Capturing the full board for corridor detail\u2026"
                    : "Capturing planned corridor areas for detailed screening\u2026");
                await planningStore.DisposeAsync().ConfigureAwait(false);
                planningStore = null;
                try
                {
                    Stopwatch scanTimer = Stopwatch.StartNew();
                    try
                    {
                        scanStore = await observation.CaptureAsync(
                            scanBudgets, demand, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        scanTimer.Stop();
                        scanCaptureMilliseconds = scanTimer.ElapsedMilliseconds;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    scanInfo = RequireCompleteCapture(scanStore.Info);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LargeBoardFailure stageFailure = LargeBoardFailure.From(exception);
                    throw new LargeBoardCaptureException(stageFailure.UserMessage, exception);
                }
                LargeBoardPublicationFence.RequireCurrent(scanInfo, currentDocument());
                RequireSameFrozenSource(planningInfo, scanInfo);
                run = await WithStagedReplayAttributionAsync(
                    () => LargeBoardCorridorRunner.ScanAsync(
                        scanStore, planning, cancellationToken, progress)).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            LargeBoardPublicationFence.RequireCurrent(run.CaptureIdentity, currentDocument());
            result = new(document, scanInfo, run)
            {
                PlanningCapture = planningInfo,
                ObservationStartupMilliseconds = observationStartupMilliseconds,
            };
        }
        catch (Exception exception)
        {
            terminalFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                await ReleaseStagedAsync().ConfigureAwait(false);
                cleanupComplete = observationStarted
                    ? true
                    : terminalFailure is EngineBulkCaptureCancelledException engineCancelled
                        ? engineCancelled.CleanupComplete
                        : terminalFailure is not null
                            ? LargeBoardFailure.From(
                                UnwrapForReceipt(terminalFailure) ?? terminalFailure).CleanupComplete
                            : null;
            }
            catch (Exception cleanupFailure)
            {
                cleanupComplete = false;
                if (terminalFailure is null)
                {
                    terminalFailure = cleanupFailure;
                    throw;
                }
            }
            finally
            {
                Exception? receiptCause = UnwrapForReceipt(terminalFailure) ?? terminalFailure;
                AcquisitionTerminalState state = terminalFailure switch
                {
                    null => AcquisitionTerminalState.Complete,
                    OperationCanceledException => AcquisitionTerminalState.Cancelled,
                    _ => AcquisitionTerminalState.Failed,
                };
                // The receipt carries one truthful acquisition total: measured
                // wall time around observation startup and both stage captures,
                // including failed and cancelled attempts. Sealed per-stage
                // timings stay on their own store records instead; per-phase
                // values stay null because no measured breakdown exists for
                // the aggregate.
                long captureMilliseconds = beginObservationMilliseconds +
                    planningCaptureMilliseconds +
                    scanCaptureMilliseconds;
                LargeBoardCaptureStoreInfo? receiptCapture = scanInfo ?? planningInfo;
                // Stage A evidence attaches only when a distinct stage B sealed;
                // otherwise the positional capture already describes stage A.
                LargeBoardCaptureStoreInfo? stagedPlanning =
                    planningInfo is not null && scanInfo is not null ? planningInfo : null;
                LargeBoardCapturePhaseTiming? aggregateTiming =
                    receiptCapture is null && !observationStarted
                        ? null
                        : new LargeBoardCapturePhaseTiming(
                            captureMilliseconds,
                            NativeCommandMilliseconds: null,
                            PageTransferAndSealMilliseconds: null,
                            NativeReleaseMilliseconds: null,
                            EngineImportAndIndexMilliseconds: null,
                            ReplayPlanPreparationMilliseconds: null,
                            ObservationStartupMilliseconds: observationStarted
                                ? observationStartupMilliseconds
                                : null);
                LargeBoardCaptureStoreInfo? receiptStore = receiptCapture is null
                    ? null
                    : receiptCapture with { Timing = aggregateTiming };
                LargeBoardCorridorReplayException? replayFailure = FindReplayFailure(receiptCause);
                terminalReceipt = AcquisitionDiagnosticReceiptFactory.Create(
                    request,
                    currentDocument(),
                    receiptStore,
                    replayFailure?.Query ?? query,
                    replayFailure?.Budgets,
                    aggregateTiming,
                    null,
                    applicationVersion,
                    engineVersion,
                    startedAt,
                    captureMilliseconds,
                    Math.Max(0, timer.ElapsedMilliseconds - captureMilliseconds),
                    state,
                    cleanupComplete,
                    terminalFailure is null
                        ? null
                        : LargeBoardFailure.From(
                            replayFailure?.InnerException ?? receiptCause ?? terminalFailure),
                    state == AcquisitionTerminalState.Complete
                        ? planningInfo is null
                            ? "Sealed corridor screening completed; the capture store was released."
                            : "Staged corridor screening completed; both capture stores and the frozen observation were released."
                        : "Corridor screening stopped; no partial result was published.") with
                {
                    Feature = state == AcquisitionTerminalState.Complete
                        ? "dp-via-corridor-scan"
                        : "dp-via-corridor-run",
                    PartialDataWithheld = state != AcquisitionTerminalState.Complete,
                    PlanningCaptureTokenFingerprint = stagedPlanning is null
                        ? null
                        : AcquisitionDiagnosticReceiptFactory.Fingerprint(
                            stagedPlanning.Identity.CaptureToken),
                    PlanningCapturePageCount = stagedPlanning?.PageCount,
                    PlanningCaptureRecordCount = stagedPlanning?.RecordCount,
                    PlanningCaptureStoredBytes = stagedPlanning?.StoredBytes,
                    PlanningCapturePhaseTiming = stagedPlanning?.Timing,
                    ObservationStartupMilliseconds = observationStarted
                        ? observationStartupMilliseconds
                        : null,
                };
                await diagnostics.RetainAsync(terminalReceipt, CancellationToken.None).ConfigureAwait(false);
            }
        }
        return (result ?? throw new InvalidOperationException("The corridor scan produced no result.")) with
        {
            TerminalReceipt = terminalReceipt,
        };

        async Task ReleaseStagedAsync()
        {
            Exception? firstFailure = null;
            if (scanStore is not null)
            {
                try
                {
                    await scanStore.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    firstFailure ??= failure;
                }
                scanStore = null;
            }
            if (planningStore is not null)
            {
                try
                {
                    await planningStore.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    firstFailure ??= failure;
                }
                planningStore = null;
            }
            if (observation is not null)
            {
                try
                {
                    await observation.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    firstFailure ??= failure;
                }
                observation = null;
            }
            if (firstFailure is not null)
            {
                ExceptionDispatchInfo.Capture(firstFailure).Throw();
            }
        }
    }

    /// <summary>
    /// Builds the stage B detail demand from the plan's exact original batch
    /// rectangles. Each region requests discovery, scalar geometry, and pad
    /// measurements across all nets and layers. Identical rectangles are
    /// deduplicated with first-occurrence order preserved; batch identities
    /// are otherwise untouched. Returns null
    /// when stage B must use an explicit full capture instead of demand: either
    /// there is nothing to demand or the regions exceed the native budget, in
    /// which case the caller captures everything rather than truncating.
    /// Never returns an empty demand: empty native regions select the whole
    /// board, so callers skip stage B when the plan has no batches instead of
    /// issuing one.
    /// </summary>
    internal static EngineBulkCaptureDemand? BuildScanDemand(
        IReadOnlyList<CorridorReplayBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        List<DesignBounds> regions = batches
            .Select(static batch => batch.Region)
            .Distinct()
            .ToList();
        if (regions.Count == 0 || regions.Count > EngineBulkCaptureDemand.MaximumRegions)
        {
            return null;
        }
        EngineBulkCaptureFields fields = EngineBulkCaptureFields.Discovery |
            EngineBulkCaptureFields.ScalarGeometry |
            EngineBulkCaptureFields.PadMeasurements;
        return new EngineBulkCaptureDemand
        {
            Fields = fields,
            Regions = regions
                .Select(bounds => new EngineBulkCaptureRegion(bounds, fields))
                .ToImmutableArray(),
        };
    }

    /// <summary>
    /// Requires both staged captures to bind to the same verified frozen
    /// source before any stage B replay runs. This is the staged authorization
    /// boundary: each capture must carry one consistent supported traversal
    /// declaration on both its store and identity records, both declarations
    /// must share verified frozen provenance over one frozen source, and their
    /// source ordinals must be mutually comparable. Captures without frozen
    /// provenance predate staged production and are rejected here; the
    /// single-store path never calls this gate.
    /// </summary>
    internal static void RequireSameFrozenSource(
        LargeBoardCaptureStoreInfo planning,
        LargeBoardCaptureStoreInfo scan)
    {
        ArgumentNullException.ThrowIfNull(planning);
        ArgumentNullException.ThrowIfNull(scan);
        CorridorSourceTraversal? planningTraversal = ConsistentTraversal(planning);
        CorridorSourceTraversal? scanTraversal = ConsistentTraversal(scan);
        if (planningTraversal is null ||
            scanTraversal is null ||
            !planningTraversal.IsSupported ||
            !scanTraversal.IsSupported ||
            planningTraversal.FrozenSource is not { SourceOrderVerified: true } planningFrozen ||
            scanTraversal.FrozenSource is not { SourceOrderVerified: true } scanFrozen ||
            !planningFrozen.Equals(scanFrozen) ||
            !planningTraversal.CanCompareSourceOrdinalsWith(scanTraversal))
        {
            throw new InvalidDataException(
                "The staged corridor captures do not share one verified frozen source; no result was produced.");
        }
    }

    /// <summary>
    /// Returns the capture's traversal declaration only when its store and
    /// identity records agree on one declaration. Returns null when either
    /// side is missing or they disagree.
    /// </summary>
    private static CorridorSourceTraversal? ConsistentTraversal(LargeBoardCaptureStoreInfo info)
    {
        CorridorSourceTraversal? traversal = info.SourceTraversal;
        if (traversal is null || !traversal.Equals(info.Identity.SourceTraversal))
        {
            return null;
        }
        return traversal;
    }

    private static LargeBoardCaptureStoreInfo RequireCompleteCapture(
        LargeBoardCaptureStoreInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.HasCompleteCoverage || info.IncompleteFamilies.Length != 0)
        {
            throw new LargeBoardCaptureIncompleteException(info.IncompleteFamilies);
        }
        return info;
    }

    private static async Task<TResult> WithStagedReplayAttributionAsync<TResult>(
        Func<Task<TResult>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (LargeBoardCorridorReplayException exception)
        {
            LargeBoardFailure failure = LargeBoardFailure.From(
                exception.InnerException ?? exception);
            throw new LargeBoardCaptureException(failure.UserMessage, exception);
        }
    }

    private static Exception? UnwrapForReceipt(Exception? terminalFailure) =>
        terminalFailure is LargeBoardCaptureException wrapped && wrapped.InnerException is not null
            ? wrapped.InnerException
            : terminalFailure;

    private static LargeBoardCorridorReplayException? FindReplayFailure(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is LargeBoardCorridorReplayException replayFailure)
            {
                return replayFailure;
            }
            exception = exception.InnerException;
        }
        return null;
    }
}
