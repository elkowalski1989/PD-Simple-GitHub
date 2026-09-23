using System.Collections.Immutable;
using System.Diagnostics;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;

namespace PD.Simple.LargeBoards;

internal sealed record LargeBoardCaptureBudgets
{
    public ImmutableArray<DataFamily> Families { get; init; } =
        SceneQuery.CompleteBoard().Families;
    public ImmutableArray<CopperKind> CopperKinds { get; init; } =
        Enum.GetValues<CopperKind>().ToImmutableArray();
    public ViaPadMeasurementSelection ViaPadMeasurements { get; init; } =
        ViaPadMeasurementSelection.All;
    public bool IncludeContours { get; init; }
    public int TargetNativePageCharacters { get; init; } = 190_000;
    public int TargetNativeContourFragmentVertices { get; init; } = 64;
    public int MaximumNativeObjectRecords { get; init; } = 2_000_000;
    public int MaximumNativeObjectVisits { get; init; } = 10_000_000;
    public int MaximumNativeContourVertices { get; init; } = 64_000_000;
    public int MaximumPages { get; init; } = 32_768;
    public long MaximumNativeSpoolCharacters { get; init; } = 1_000_000_000;
    public long MaximumNativeSnapshotBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaximumNativeSpoolBytes { get; init; } = 1_100_000_000;
    public long MaximumEngineStoreBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    public EngineBulkCaptureOptions ToEngineOptions() => new()
    {
        Families = Families,
        CopperKinds = CopperKinds,
        ViaPadMeasurements = ViaPadMeasurements,
        IncludeContours = IncludeContours,
        TargetNativePageCharacters = TargetNativePageCharacters,
        TargetNativeContourFragmentVertices = TargetNativeContourFragmentVertices,
        MaximumNativeObjectRecords = MaximumNativeObjectRecords,
        MaximumNativeObjectVisits = MaximumNativeObjectVisits,
        MaximumNativeContourVertices = MaximumNativeContourVertices,
        MaximumPages = MaximumPages,
        MaximumNativeSpoolCharacters = MaximumNativeSpoolCharacters,
        MaximumNativeSnapshotBytes = MaximumNativeSnapshotBytes,
        MaximumNativeSpoolBytes = MaximumNativeSpoolBytes,
        MaximumEngineStoreBytes = MaximumEngineStoreBytes,
    };
}

internal sealed record LargeBoardReplayBudgets
{
    public int MaximumMaterializedObjects { get; init; } = 100_000;
    public int MaximumPagesRead { get; init; } = 32_768;
    public long MaximumRecordsRead { get; init; } = 2_000_000;

    public EngineBulkReplayOptions ToEngineOptions() => new()
    {
        MaximumMaterializedObjects = MaximumMaterializedObjects,
        MaximumPagesRead = MaximumPagesRead,
        MaximumRecordsRead = MaximumRecordsRead,
    };
}

internal sealed record LargeBoardCaptureRequest(
    string Caller,
    string UserAction,
    bool ApplicationVisible,
    LargeBoardCaptureBudgets Budgets)
{
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");
}

internal sealed record LargeBoardCaptureIdentity(
    string CaptureToken,
    string SessionId,
    long SessionGeneration,
    long BoardGeneration,
    int? ProcessId,
    string? Design,
    string ProtocolVersion)
{
    // The positional identity always belongs to the capture host. Frozen-source
    // evidence binds that worker to the primary document without replacing it.
    public CorridorSourceTraversal? SourceTraversal { get; init; }
}

internal sealed record LargeBoardCaptureResource(
    string Kind,
    long Limit,
    long Observed,
    bool Exhausted);

internal sealed record LargeBoardCaptureStoreInfo(
    LargeBoardCaptureIdentity Identity,
    bool HasCompleteCoverage,
    int PageCount,
    long RecordCount,
    long StoredBytes,
    ImmutableArray<LargeBoardCaptureResource> Resources,
    ImmutableArray<string> IncompleteFamilies,
    LargeBoardCapturePhaseTiming? Timing = null)
{
    public CorridorSourceTraversal? SourceTraversal { get; init; }
    public LargeBoardReplayPlanPreparation? ReplayPlan { get; init; }
    public LargeBoardStorageAdmission? StorageAdmission { get; init; }
}

internal sealed record LargeBoardReplayPlanPreparation(
    bool Active,
    long ElapsedMilliseconds,
    int? IndexedRoots,
    long? SerializedRootIndexBytes,
    int? RootIndexPasses,
    string? FallbackResource);

internal sealed record LargeBoardCapturePhaseTiming(
    long TotalMilliseconds,
    long? NativeCommandMilliseconds,
    long? PageTransferAndSealMilliseconds,
    long? NativeReleaseMilliseconds,
    long? EngineImportAndIndexMilliseconds,
    long? ReplayPlanPreparationMilliseconds = null,
    long? ObservationStartupMilliseconds = null);

internal sealed record LargeBoardReplayPhaseTiming(
    long TotalMilliseconds,
    long ManifestAndSealValidationMilliseconds,
    long IndexSelectionMilliseconds,
    long PageReadAndHashMilliseconds,
    long PageDecodeAndValidationMilliseconds,
    long RecordSelectionMilliseconds,
    long SceneMaterializationMilliseconds,
    long IdentityProjectionMilliseconds);

internal sealed record LargeBoardReplayPayload<TScene>(
    TScene Scene,
    int CandidatePages,
    int PagesRead,
    long RecordsRead,
    long RecordsSelected,
    int SpatialRootsSelected,
    ImmutableArray<LargeBoardReplayObjectIdentity> ObjectIdentities,
    LargeBoardReplayPhaseTiming? Timing = null);

internal sealed record LargeBoardReplayBatchPayload<TScene>(
    ImmutableArray<LargeBoardReplayPayload<TScene>> Results,
    int RootIndexPasses,
    int UniquePagesRead,
    long UniqueRecordsRead,
    long RootQueryMemberships,
    long PageQueryMemberships,
    long LogicalPageMembershipBytes,
    long ManifestValidationMilliseconds,
    long RootIndexSelectionMilliseconds,
    long PageReadAndDecodeMilliseconds,
    long SceneMaterializationMilliseconds);

internal sealed record LargeBoardReplayObjectIdentity(
    SceneObjectId Id,
    long CaptureOrdinal,
    string SourceIdentity,
    CopperKind Kind)
{
    public long? SourceTraversalOrdinal { get; init; }
}

internal interface ILargeBoardCaptureStore<TScene> : IAsyncDisposable
{
    LargeBoardCaptureStoreInfo Info { get; }

    ValueTask<LargeBoardReplayPayload<TScene>> ReplayAsync(
        SceneQuery query,
        LargeBoardReplayBudgets budgets,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional sealed-store optimization. Callers retain the scalar contract as
/// the fallback for unsupported stores and resource-limited query groups.
/// </summary>
internal interface IBatchedLargeBoardCaptureStore<TScene> : ILargeBoardCaptureStore<TScene>
{
    ValueTask<LargeBoardReplayBatchPayload<TScene>> ReplayBatchAsync(
        IReadOnlyList<SceneQuery> queries,
        LargeBoardReplayBudgets budgets,
        CancellationToken cancellationToken);
}

internal interface ILargeBoardCaptureGateway<TScene>
{
    ValueTask<ILargeBoardCaptureStore<TScene>> CaptureAsync(
        LargeBoardCaptureBudgets budgets,
        CancellationToken cancellationToken);
}

internal enum AcquisitionTerminalState { Complete, Failed, Cancelled }

internal sealed record AcquisitionDiagnosticReceipt
{
    public required string CorrelationId { get; init; }
    public required string Feature { get; init; }
    public required string Caller { get; init; }
    public required string UserAction { get; init; }
    public required bool ApplicationVisible { get; init; }
    public required string ApplicationVersion { get; init; }
    public required string EngineVersion { get; init; }
    public required string CaptureTokenFingerprint { get; init; }
    public required string SessionFingerprint { get; init; }
    public required string DesignFingerprint { get; init; }
    public required long SessionGeneration { get; init; }
    public required long BoardGeneration { get; init; }
    public required int? HostProcessId { get; init; }
    public required string ProtocolVersion { get; init; }
    public string CapturedSessionFingerprint { get; init; } = string.Empty;
    public string CapturedDesignFingerprint { get; init; } = string.Empty;
    public long? CapturedSessionGeneration { get; init; }
    public long? CapturedBoardGeneration { get; init; }
    public int? CapturedHostProcessId { get; init; }
    public string CapturedProtocolVersion { get; init; } = string.Empty;
    public bool IsFrozenSource { get; init; }
    public string SourceSessionFingerprint { get; init; } = string.Empty;
    public string SourceDesignFingerprint { get; init; } = string.Empty;
    public long? SourceSessionGeneration { get; init; }
    public long? SourceBoardGeneration { get; init; }
    public int? SourceHostProcessId { get; init; }
    public string SourceProtocolVersion { get; init; } = string.Empty;
    public string FrozenObservationFingerprint { get; init; } = string.Empty;
    public string? FrozenInputSha256 { get; init; }
    public string? SourceSnapshotHash { get; init; }
    public DateTimeOffset? SourceExportedAt { get; init; }
    public string ObservedSessionFingerprint { get; init; } = string.Empty;
    public string ObservedDesignFingerprint { get; init; } = string.Empty;
    public long? ObservedSessionGeneration { get; init; }
    public long? ObservedBoardGeneration { get; init; }
    public int? ObservedHostProcessId { get; init; }
    public ImmutableArray<string> IdentityMismatchFields { get; init; } = [];
    public int? CapturePageCount { get; init; }
    public long? CaptureRecordCount { get; init; }
    public long? CaptureStoredBytes { get; init; }
    public ImmutableArray<LargeBoardCaptureResource> CaptureResources { get; init; } = [];
    public required string QueryKind { get; init; }
    public required ImmutableArray<string> Families { get; init; }
    public required ImmutableArray<string> CopperKinds { get; init; }
    public required bool IncludeContours { get; init; }
    public required bool HasRegion { get; init; }
    public required ImmutableArray<string> LayerFingerprints { get; init; }
    public required bool HasModuleSelector { get; init; }
    public required int? MaterializedSceneObjectBudget { get; init; }
    public LargeBoardCaptureBudgets? CaptureBudgets { get; init; }
    public LargeBoardCaptureBudgets? EffectiveCaptureBudgets { get; init; }
    public LargeBoardStorageAdmission? StorageAdmission { get; init; }
    public LargeBoardReplayBudgets? ReplayBudgets { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required long CaptureElapsedMilliseconds { get; init; }
    public required long ReplayElapsedMilliseconds { get; init; }
    public required AcquisitionTerminalState TerminalState { get; init; }
    public required bool PartialDataWithheld { get; init; }
    public required bool? CleanupComplete { get; init; }
    public required string CleanupDisposition { get; init; }
    public string? ExhaustedResource { get; init; }
    public long? ConfiguredLimit { get; init; }
    public long? ObservedCount { get; init; }
    public string? FailureCode { get; init; }
    public string? DiagnosticDetail { get; init; }
    public string? UserMessage { get; init; }
    public LargeBoardCapturePhaseTiming? CapturePhaseTiming { get; init; }
    public string? PlanningCaptureTokenFingerprint { get; init; }
    public int? PlanningCapturePageCount { get; init; }
    public long? PlanningCaptureRecordCount { get; init; }
    public long? PlanningCaptureStoredBytes { get; init; }
    public LargeBoardCapturePhaseTiming? PlanningCapturePhaseTiming { get; init; }
    public long? ObservationStartupMilliseconds { get; init; }
    public LargeBoardReplayPhaseTiming? ReplayPhaseTiming { get; init; }
    public EngineSceneAcquisitionTiming? ScenePhaseTiming { get; init; }
    public ImmutableArray<EngineSceneAcquisitionResource> SceneResources { get; init; } = [];
}

internal interface IAcquisitionDiagnosticOwner
{
    ValueTask RetainAsync(
        AcquisitionDiagnosticReceipt receipt,
        CancellationToken cancellationToken = default);
}

internal sealed record LargeBoardCaptureResult(
    string CorrelationId,
    LargeBoardCaptureStoreInfo Store,
    string UserMessage);

internal sealed record LargeBoardReplayResult<TScene>(
    string CorrelationId,
    TScene Scene,
    SceneQuery Query,
    int PagesRead,
    long RecordsSelected,
    string UserMessage);

internal sealed class LargeBoardCaptureWorkflow<TScene> : IAsyncDisposable
{
    private readonly ILargeBoardCaptureGateway<TScene> _gateway;
    private readonly IAcquisitionDiagnosticOwner _diagnostics;
    private readonly Func<WorkspaceDocumentIdentity?> _currentDocument;
    private readonly string _applicationVersion;
    private readonly string _engineVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ILargeBoardCaptureStore<TScene>? _store;
    private LargeBoardCaptureRequest? _request;
    private long _captureMilliseconds;
    private bool _disposed;

    public LargeBoardCaptureWorkflow(
        ILargeBoardCaptureGateway<TScene> gateway,
        IAcquisitionDiagnosticOwner diagnostics,
        Func<WorkspaceDocumentIdentity?> currentDocument,
        string applicationVersion,
        string engineVersion)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _currentDocument = currentDocument ?? throw new ArgumentNullException(nameof(currentDocument));
        _applicationVersion = RequireText(applicationVersion, nameof(applicationVersion));
        _engineVersion = RequireText(engineVersion, nameof(engineVersion));
    }

    public bool HasCapture => _store is not null;

    public async Task<LargeBoardCaptureResult> CaptureAsync(
        LargeBoardCaptureRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ILargeBoardCaptureStore<TScene>? candidate = null;
        WorkspaceDocumentIdentity? document = null;
        var timer = Stopwatch.StartNew();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            document = RequireCurrentDocument();
            candidate = await _gateway.CaptureAsync(request.Budgets, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            LargeBoardPublicationFence.RequireCurrent(candidate.Info, document);
            LargeBoardPublicationFence.RequireCurrent(candidate.Info, RequireCurrentDocument());
            if (!candidate.Info.HasCompleteCoverage || candidate.Info.IncompleteFamilies.Length != 0)
            {
                throw new LargeBoardCaptureIncompleteException(candidate.Info.IncompleteFamilies);
            }

            long captureMilliseconds = timer.ElapsedMilliseconds;
            ILargeBoardCaptureStore<TScene>? previous = _store;
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
                _store = null;
                _request = null;
            }
            await RetainAsync(request, document, candidate.Info,
                CaptureQuery(request.Budgets), null,
                candidate.Info.Timing, null, startedAt,
                captureMilliseconds, 0, AcquisitionTerminalState.Complete, true,
                null, null).ConfigureAwait(false);
            _store = candidate;
            candidate = null;
            _request = request;
            _captureMilliseconds = captureMilliseconds;
            return new(request.CorrelationId, _store.Info,
                $"Large-board capture complete: {_store.Info.RecordCount:N0} records in {_store.Info.PageCount:N0} sealed pages. Choose a bounded area to inspect.");
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            bool? cleaned = CombineCleanup(
                await DisposeCandidateAsync(candidate).ConfigureAwait(false),
                (exception as EngineBulkCaptureCancelledException)?.CleanupComplete);
            string message = cleaned == true
                ? "Large-board capture was cancelled after native and local cleanup completed."
                : "Large-board capture was cancelled, but cleanup could not be confirmed. Review this correlated receipt before retrying.";
            await RetainAsync(request, document, candidate?.Info,
                CaptureQuery(request.Budgets), null,
                null, null, startedAt,
                timer.ElapsedMilliseconds, 0, AcquisitionTerminalState.Cancelled,
                cleaned, null, message)
                .ConfigureAwait(false);
            throw new LargeBoardCaptureCancelledException(
                cleaned,
                cancellationToken,
                exception);
        }
        catch (Exception exception)
        {
            bool? cleaned = await DisposeCandidateAsync(candidate).ConfigureAwait(false);
            LargeBoardFailure failure = LargeBoardFailure.From(exception);
            await RetainAsync(request, document, candidate?.Info,
                CaptureQuery(request.Budgets), null,
                null, null, startedAt,
                timer.ElapsedMilliseconds, 0, AcquisitionTerminalState.Failed,
                CombineCleanup(cleaned, failure.CleanupComplete),
                failure,
                failure.UserMessage).ConfigureAwait(false);
            throw new LargeBoardCaptureException(failure.UserMessage, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LargeBoardReplayResult<TScene>> ReplayRegionAsync(
        SceneQuery query,
        bool applicationVisible,
        CancellationToken cancellationToken = default,
        LargeBoardReplayBudgets? budgets = null)
    {
        ValidateReplayQuery(query);
        LargeBoardReplayBudgets replayBudgets = budgets ?? new()
        {
            MaximumMaterializedObjects = query.MaximumObjects,
        };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ILargeBoardCaptureStore<TScene> store = _store ?? throw new InvalidOperationException(
                "Run the explicit large-board capture before inspecting an area.");
            LargeBoardCaptureRequest captureRequest = _request ?? throw new InvalidOperationException(
                "Capture request evidence is unavailable.");
            LargeBoardCaptureRequest request = captureRequest with
            {
                UserAction = "Inspect bounded area",
                ApplicationVisible = applicationVisible,
            };
            await RequireCurrentOrDiscardAsync(store).ConfigureAwait(false);
            LargeBoardReplayPayload<TScene> replay = await store.ReplayAsync(
                query,
                replayBudgets,
                cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await RequireCurrentOrDiscardAsync(store).ConfigureAwait(false);
            await RetainAsync(request, RequireCurrentDocument(), store.Info,
                query, replayBudgets,
                store.Info.Timing, replay.Timing, startedAt,
                _captureMilliseconds, timer.ElapsedMilliseconds,
                AcquisitionTerminalState.Complete, true, null, null).ConfigureAwait(false);
            return new(request.CorrelationId, replay.Scene, query, replay.PagesRead,
                replay.RecordsSelected,
                $"Area ready for inspection: {replay.RecordsSelected:N0} records selected from {replay.PagesRead:N0} sealed pages. This is historical evidence; live actions require a fresh Engine read.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RetainReplayFailureAsync(query, applicationVisible, replayBudgets,
                startedAt, timer.ElapsedMilliseconds,
                AcquisitionTerminalState.Cancelled,
                "Area replay was cancelled. No clear result was produced.", null).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            LargeBoardFailure failure = LargeBoardFailure.From(exception);
            await RetainReplayFailureAsync(query, applicationVisible, replayBudgets,
                startedAt, timer.ElapsedMilliseconds,
                AcquisitionTerminalState.Failed, failure.UserMessage, failure).ConfigureAwait(false);
            throw new LargeBoardCaptureException(failure.UserMessage, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void RequireFreshLiveReadForNativeAction()
    {
        ILargeBoardCaptureStore<TScene> store = _store ?? throw new InvalidOperationException(
            "No sealed large-board capture is retained.");
        LargeBoardPublicationFence.RequireCurrent(store.Info, RequireCurrentDocument());
        throw new InvalidOperationException(
            "A sealed replay is historical evidence. Acquire a fresh live Engine scene and revalidate the selected object before navigating or editing Allegro.");
    }

    public async Task<TResult> RunWithCurrentCaptureAsync<TResult>(
        Func<ILargeBoardCaptureStore<TScene>, CancellationToken, ValueTask<TResult>> operation,
        string userAction,
        bool applicationVisible,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        RequireText(userAction, nameof(userAction));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ILargeBoardCaptureStore<TScene> store = _store ?? throw new InvalidOperationException(
                "Run the explicit large-board capture before starting this analysis.");
            await RequireCurrentOrDiscardAsync(store).ConfigureAwait(false);
            TResult result = await operation(store, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await RequireCurrentOrDiscardAsync(store).ConfigureAwait(false);
            return result;
        }
        catch (LargeBoardCorridorReplayException exception)
        {
            Exception cause = exception.InnerException ?? exception;
            LargeBoardFailure failure = LargeBoardFailure.From(cause);
            if (_request is { } captureRequest && _store is { } store)
            {
                LargeBoardCaptureRequest request = captureRequest with
                {
                    UserAction = userAction,
                    ApplicationVisible = applicationVisible,
                };
                await RetainAsync(
                    request,
                    _currentDocument(),
                    store.Info,
                    exception.Query,
                    exception.Budgets,
                    store.Info.Timing,
                    null,
                    startedAt,
                    _captureMilliseconds,
                    timer.ElapsedMilliseconds,
                    AcquisitionTerminalState.Failed,
                    CombineCleanup(true, failure.CleanupComplete),
                    failure,
                    failure.UserMessage).ConfigureAwait(false);
            }
            throw new LargeBoardCaptureException(failure.UserMessage, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            ILargeBoardCaptureStore<TScene>? store = _store;
            _store = null;
            _request = null;
            if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask RetainReplayFailureAsync(SceneQuery query,
        bool applicationVisible,
        LargeBoardReplayBudgets replayBudgets,
        DateTimeOffset startedAt, long elapsed, AcquisitionTerminalState state,
        string message, LargeBoardFailure? failure)
    {
        if (_request is { } request)
        {
            LargeBoardCaptureRequest replayRequest = request with
            {
                UserAction = "Inspect bounded area",
                ApplicationVisible = applicationVisible,
            };
            await RetainAsync(replayRequest, _currentDocument(), _store?.Info,
                query, replayBudgets,
                _store?.Info.Timing, null, startedAt,
                _captureMilliseconds, elapsed, state,
                CombineCleanup(true, failure?.CleanupComplete), failure, message)
                .ConfigureAwait(false);
        }
    }

    private ValueTask RetainAsync(LargeBoardCaptureRequest request,
        WorkspaceDocumentIdentity? document,
        LargeBoardCaptureStoreInfo? captureInfo,
        SceneQuery query,
        LargeBoardReplayBudgets? replayBudgets,
        LargeBoardCapturePhaseTiming? captureTiming,
        LargeBoardReplayPhaseTiming? replayTiming,
        DateTimeOffset startedAt,
        long captureElapsed, long replayElapsed, AcquisitionTerminalState state,
        bool? cleaned, LargeBoardFailure? failure, string? message) =>
        _diagnostics.RetainAsync(AcquisitionDiagnosticReceiptFactory.Create(
            request, document, captureInfo, query, replayBudgets,
            captureTiming, replayTiming,
            _applicationVersion, _engineVersion,
            startedAt, captureElapsed, replayElapsed, state, cleaned, failure, message));

    private WorkspaceDocumentIdentity RequireCurrentDocument() =>
        _currentDocument() ?? throw new InvalidOperationException(
            "Connect PD Simple to an Allegro board before starting large-board capture.");

    private async ValueTask RequireCurrentOrDiscardAsync(
        ILargeBoardCaptureStore<TScene> store)
    {
        try
        {
            LargeBoardPublicationFence.RequireCurrent(store.Info, RequireCurrentDocument());
        }
        catch (LargeBoardCaptureStaleException)
        {
            await store.DisposeAsync().ConfigureAwait(false);
            if (ReferenceEquals(_store, store))
            {
                _store = null;
                _request = null;
            }
            throw;
        }
    }

    private static void ValidateRequest(LargeBoardCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireText(request.Caller, nameof(request.Caller));
        RequireText(request.UserAction, nameof(request.UserAction));
        RequireText(request.CorrelationId, nameof(request.CorrelationId));
        if (request.Budgets.IncludeContours)
        {
            throw new ArgumentException(
                "The first large-board workflow deliberately captures scalar geometry without contours.",
                nameof(request));
        }
    }

    private static void ValidateReplayQuery(SceneQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Kind != SceneReadKind.RegionGeometry || query.Region is null ||
            query.Module is not null || query.IncludeContours ||
            query.Region.Value.Width <= 0 || query.Region.Value.Height <= 0)
        {
            throw new ArgumentException(
                "Inspection requires a positive bounded RegionGeometry query with contours disabled and no module selector.",
                nameof(query));
        }
    }

    private static SceneQuery CaptureQuery(LargeBoardCaptureBudgets budgets) => new()
    {
        Kind = SceneReadKind.CompleteBoard,
        Families = budgets.Families,
        CopperKinds = budgets.CopperKinds,
        ViaPadMeasurements = budgets.ViaPadMeasurements,
        IncludeContours = budgets.IncludeContours,
        MaximumObjects = 1,
    };

    private static bool? CombineCleanup(bool? localCleanup, bool? engineCleanup)
    {
        if (localCleanup == false || engineCleanup == false)
        {
            return false;
        }
        if (localCleanup == true || engineCleanup == true)
        {
            return true;
        }
        return null;
    }

    private static async ValueTask<bool?> DisposeCandidateAsync(
        ILargeBoardCaptureStore<TScene>? candidate)
    {
        if (candidate is null) return null;
        try
        {
            await candidate.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string RequireText(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", parameter);
        return value;
    }
}

internal sealed class LargeBoardCaptureException : InvalidOperationException
{
    public LargeBoardCaptureException(string message, Exception inner) : base(message, inner) { }
}

internal sealed class LargeBoardCaptureStaleException : InvalidOperationException
{
    public LargeBoardCaptureStaleException(
        string message,
        LargeBoardCaptureIdentity captured,
        WorkspaceDocumentIdentity? observed,
        ImmutableArray<string> mismatchFields)
        : base(message)
    {
        Captured = captured;
        Observed = observed;
        MismatchFields = mismatchFields;
    }

    public LargeBoardCaptureIdentity Captured { get; }
    public WorkspaceDocumentIdentity? Observed { get; }
    public ImmutableArray<string> MismatchFields { get; }
}

internal static class LargeBoardPublicationFence
{
    public static WorkspaceDocumentIdentity RequireCurrent(
        LargeBoardCaptureStoreInfo captured,
        WorkspaceDocumentIdentity? current)
    {
        ArgumentNullException.ThrowIfNull(captured);
        if ((captured.SourceTraversal?.FrozenSource is not null ||
                captured.Identity.SourceTraversal?.FrozenSource is not null) &&
            captured.SourceTraversal != captured.Identity.SourceTraversal)
        {
            throw new LargeBoardCaptureStaleException(
                "The sealed capture has inconsistent frozen-source provenance. Run a new capture.",
                captured.Identity,
                current,
                ["frozen_store_provenance"]);
        }
        return RequireCurrent(captured.Identity, current);
    }

    /// <summary>
    /// Admits historical evidence bound to the current document. This does not
    /// establish a live edit epoch, authorize native actions, or certify Clear/Pass.
    /// </summary>
    public static WorkspaceDocumentIdentity RequireCurrent(
        LargeBoardCaptureIdentity captured,
        WorkspaceDocumentIdentity? current)
    {
        ArgumentNullException.ThrowIfNull(captured);
        var mismatches = ImmutableArray.CreateBuilder<string>();
        CorridorSourceTraversal? traversal = captured.SourceTraversal;
        CorridorFrozenSource? frozen = traversal?.FrozenSource;
        if (frozen is not null)
        {
            ValidateFrozenSource(captured, traversal!, frozen, mismatches);
        }
        if (current is null)
        {
            mismatches.Add("current_document");
        }
        else if (frozen is not null)
        {
            if (frozen.SourceDocument is { } source)
            {
                RequireSourceBinding(source, current, mismatches);
            }
        }
        else
        {
            if (!StringComparer.Ordinal.Equals(current.SessionId, captured.SessionId))
            {
                mismatches.Add("session_id");
            }
            if (current.SessionGeneration != captured.SessionGeneration)
            {
                mismatches.Add("session_generation");
            }
            if (current.BoardGeneration != captured.BoardGeneration)
            {
                mismatches.Add("board_generation");
            }
            if (current.ProcessId != captured.ProcessId)
            {
                mismatches.Add("process_id");
            }
        }
        if (mismatches.Count != 0)
        {
            throw new LargeBoardCaptureStaleException(
                frozen is null
                    ? "The Allegro session or board changed. Run a new explicit large-board capture."
                    : "The frozen capture's source binding or provenance could not be verified. Run a new capture; no live Clear/Pass result is available.",
                captured,
                current,
                mismatches.ToImmutable());
        }

        return current!;
    }

    private static void ValidateFrozenSource(
        LargeBoardCaptureIdentity captured,
        CorridorSourceTraversal traversal,
        CorridorFrozenSource frozen,
        ImmutableArray<string>.Builder mismatches)
    {
        if (!traversal.IsSupported || !frozen.SourceOrderVerified)
        {
            mismatches.Add("frozen_source_order");
        }
        if (!IsLowerHex(frozen.ObservationId, 32) ||
            !IsLowerHex(frozen.InputSha256, 64) ||
            !IsLowerHex(frozen.SourceSnapshotHash, 64))
        {
            mismatches.Add("frozen_source_hashes");
        }
        if (frozen.ExportedAt is not { } exportedAt ||
            exportedAt <= DateTimeOffset.UnixEpoch || exportedAt > DateTimeOffset.UtcNow ||
            exportedAt.Offset != TimeSpan.Zero)
        {
            mismatches.Add("frozen_exported_at");
        }
        if (frozen.WorkerProcessId <= 0 || captured.ProcessId != frozen.WorkerProcessId)
        {
            mismatches.Add("frozen_worker_process_id");
        }
        if (captured.SessionId != "observation-" + frozen.ObservationId ||
            captured.SessionGeneration != 1 || captured.BoardGeneration != 1 ||
            traversal.SessionId != captured.SessionId ||
            traversal.BoardGeneration != captured.BoardGeneration ||
            string.IsNullOrWhiteSpace(captured.CaptureToken) ||
            traversal.ObservationToken != captured.CaptureToken ||
            string.IsNullOrWhiteSpace(captured.Design) ||
            string.IsNullOrWhiteSpace(captured.ProtocolVersion))
        {
            mismatches.Add("frozen_worker_binding");
        }
        CorridorSourceDocument? source = frozen.SourceDocument;
        if (source is null || string.IsNullOrWhiteSpace(source.SessionId) ||
            source.SessionGeneration < 0 || source.BoardGeneration < 0 ||
            source.ProcessId is not > 0 || string.IsNullOrWhiteSpace(source.Design) ||
            string.IsNullOrWhiteSpace(source.ProtocolVersion) ||
            source.ProcessId == frozen.WorkerProcessId ||
            source.SessionId == captured.SessionId ||
            source.ProtocolVersion != captured.ProtocolVersion)
        {
            mismatches.Add("frozen_source_document");
        }
    }

    private static void RequireSourceBinding(
        CorridorSourceDocument source,
        WorkspaceDocumentIdentity current,
        ImmutableArray<string>.Builder mismatches)
    {
        if (source.SessionId != current.SessionId)
        {
            mismatches.Add("source_session_id");
        }
        if (source.SessionGeneration != current.SessionGeneration)
        {
            mismatches.Add("source_session_generation");
        }
        if (source.BoardGeneration != current.BoardGeneration)
        {
            mismatches.Add("source_board_generation");
        }
        if (source.ProcessId != current.ProcessId)
        {
            mismatches.Add("source_process_id");
        }
        if (source.Design != current.Design)
        {
            mismatches.Add("source_design");
        }
        if (source.ProtocolVersion != current.ProtocolVersion)
        {
            mismatches.Add("source_protocol_version");
        }
    }

    private static bool IsLowerHex(string? value, int length) =>
        value?.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed class LargeBoardCaptureCancelledException : OperationCanceledException
{
    public LargeBoardCaptureCancelledException(
        bool? cleanupComplete,
        CancellationToken cancellationToken,
        Exception innerException)
        : base(
            cleanupComplete == true
                ? "Large-board capture was cancelled after cleanup completed."
                : "Large-board capture was cancelled, but cleanup could not be confirmed. Review the correlated acquisition receipt before retrying.",
            innerException,
            cancellationToken)
    {
        CleanupComplete = cleanupComplete;
    }

    public bool? CleanupComplete { get; }
}

internal sealed class LargeBoardCaptureIncompleteException : InvalidOperationException
{
    public LargeBoardCaptureIncompleteException(ImmutableArray<string> families)
        : base(families.Length == 0 ? "The Engine bulk capture reported incomplete coverage."
            : "The Engine bulk capture reported incomplete coverage for: " + string.Join(", ", families)) { }
}
