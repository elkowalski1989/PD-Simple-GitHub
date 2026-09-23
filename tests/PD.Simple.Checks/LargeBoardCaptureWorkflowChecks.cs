using System.Collections.Immutable;
using System.Text.Json;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;
using PD.Simple.LargeBoards;

internal static class LargeBoardCaptureWorkflowChecks
{
    public static async Task<int> RunAsync()
    {
        await CheckExplicitCaptureAndReplayAsync();
        await CheckCancellationAsync(cleanupComplete: true);
        await CheckCancellationAsync(cleanupComplete: false);
        await CheckResourceFailureAsync();
        await CheckCorridorReplayFailureReceiptAsync();
        CheckOrdinarySceneResourceReceipt();
        await CheckIncompleteCaptureAsync();
        await CheckIdentityFencingAsync();
        CheckPublicationFence();
        return 34 + CheckStorageAdmission() + CheckNativeProductChoice() +
            await CheckFrozenSourcePublicationAsync() + CheckFrozenSourceRejections();
    }

    private static int CheckNativeProductChoice()
    {
        Require(
            EngineLargeBoardCaptureGateway.RequireNativeProduct("Allegro_performance") ==
                "Allegro_performance",
            "The explicit worker product token was changed.");
        RequireThrows<InvalidOperationException>(
            () => EngineLargeBoardCaptureGateway.RequireNativeProduct(null),
            "A missing product token was accepted for isolated capture.");
        RequireThrows<ArgumentException>(
            () => EngineLargeBoardCaptureGateway.RequireNativeProduct("Allegro-performance"),
            "An invalid product token reached the native worker.");
        RequireThrows<ArgumentException>(
            () => EngineLargeBoardCaptureGateway.RequireNativeProduct(new string('A', 129)),
            "An oversized product token reached the native worker.");
        return 4;
    }

    private static int CheckStorageAdmission()
    {
        var requested = new LargeBoardCaptureBudgets();
        const long mib = 1024L * 1024;
        long requestedPeak = requested.MaximumNativeSnapshotBytes +
            Math.Max(requested.MaximumNativeSpoolBytes, requested.MaximumEngineStoreBytes);
        LargeBoardStorageAdmission ample = LargeBoardStorageBudgets.Select(
            requested, requestedPeak + LargeBoardStorageBudgets.ReserveBytes + mib);
        Require(!ample.LimitsReduced && ReferenceEquals(ample.Effective, requested),
            "Adequate free space changed explicit capture limits.");

        long constrainedFreeBytes = 3L * 1024 * 1024 * 1024;
        LargeBoardStorageAdmission constrained = LargeBoardStorageBudgets.Select(
            requested, constrainedFreeBytes);
        Require(constrained.LimitsReduced &&
                constrained.Effective.MaximumNativeSnapshotBytes <= requested.MaximumNativeSnapshotBytes &&
                constrained.Effective.MaximumNativeSpoolBytes <= requested.MaximumNativeSpoolBytes &&
                constrained.Effective.MaximumEngineStoreBytes <= requested.MaximumEngineStoreBytes &&
                constrained.Effective.MaximumNativeSpoolCharacters <=
                    constrained.Effective.MaximumNativeSpoolBytes,
            "Constrained storage raised a limit or produced an invalid byte/character budget.");
        Require(constrained.EstimatedPeakBytes <=
                    constrainedFreeBytes - LargeBoardStorageBudgets.ReserveBytes &&
                constrained.Effective.Families.SequenceEqual(requested.Families) &&
                constrained.Effective.CopperKinds.SequenceEqual(requested.CopperKinds) &&
                constrained.Effective.ViaPadMeasurements == requested.ViaPadMeasurements,
            "Storage admission changed requested data or exceeded its planning allowance.");
        RequireThrows<IOException>(() => LargeBoardStorageBudgets.Select(
                requested, LargeBoardStorageBudgets.ReserveBytes + 2 * mib),
            "An unusably small temporary volume was admitted for native capture.");
        return 4;
    }

    private static async Task<int> CheckFrozenSourcePublicationAsync()
    {
        int checks = 0;
        foreach (string design in new[] { "frozen-source.brd", "renamed-independent-source.brd" })
        {
            WorkspaceDocumentIdentity current = Document(Guid.NewGuid().ToString("N"), 41, design);
            LargeBoardCaptureStoreInfo info = FrozenStoreInfo(current);
            var diagnostics = new MemoryDiagnostics();
            var gateway = new FakeGateway(() => new FakeStore(info));
            await using var workflow = Create(gateway, diagnostics, () => current);
            LargeBoardCaptureResult capture = await workflow.CaptureAsync(Request("frozen-capture"));
            Require(workflow.HasCapture && capture.Store.Identity == info.Identity &&
                    info.Identity.SessionId != current.SessionId &&
                    info.Identity.ProcessId != current.ProcessId,
                "Frozen capture publication replaced the worker identity with the primary identity.");
            Require(ReferenceEquals(current,
                    LargeBoardPublicationFence.RequireCurrent(info.Identity, current)),
                "Complete frozen evidence did not bind to the exact primary source.");
            LargeBoardReplayResult<string> replay = await workflow.ReplayRegionAsync(
                RegionQuery("ETCH/FROZEN", 10_000), true);
            Require(replay.Scene == "typed-region" &&
                    replay.UserMessage.Contains("historical evidence", StringComparison.Ordinal),
                "A frozen replay was rejected or described as current evidence.");
            RequireThrows<InvalidOperationException>(workflow.RequireFreshLiveReadForNativeAction,
                "Frozen source binding was allowed to authorize a live native action.");
            AcquisitionDiagnosticReceipt receipt = diagnostics.Items.First();
            CorridorFrozenSource frozen = info.SourceTraversal!.FrozenSource!;
            Require(receipt.IsFrozenSource &&
                    receipt.SourceSessionFingerprint ==
                        AcquisitionDiagnosticReceiptFactory.Fingerprint(current.SessionId) &&
                    receipt.SourceDesignFingerprint ==
                        AcquisitionDiagnosticReceiptFactory.Fingerprint(current.Design) &&
                    receipt.SourceHostProcessId == current.ProcessId &&
                    receipt.SourceSessionGeneration == current.SessionGeneration &&
                    receipt.SourceBoardGeneration == current.BoardGeneration &&
                    receipt.SourceProtocolVersion == current.ProtocolVersion &&
                    receipt.CapturedSessionFingerprint ==
                        AcquisitionDiagnosticReceiptFactory.Fingerprint(info.Identity.SessionId) &&
                    receipt.CapturedHostProcessId == info.Identity.ProcessId &&
                    receipt.FrozenInputSha256 == frozen.InputSha256 &&
                    receipt.SourceSnapshotHash == frozen.SourceSnapshotHash &&
                    receipt.SourceExportedAt == frozen.ExportedAt,
                "Frozen capture diagnostics conflated worker and primary source provenance.");
            checks += 5;
            checks += await CheckFrozenReportAsync(info.Identity, current);

            await RequireThrowsAsync<LargeBoardCaptureStaleException>(() =>
                workflow.RunWithCurrentCaptureAsync(
                    (_, _) =>
                    {
                        current = current with { BoardGeneration = current.BoardGeneration + 1 };
                        return ValueTask.FromResult("must be withheld");
                    },
                    "historical scan", true));
            Require(!workflow.HasCapture && gateway.LastStore!.Disposed,
                "A frozen scan published after its primary source binding changed.");
            checks += 2;
        }

        WorkspaceDocumentIdentity primary = Document("coverage-source", 8, "coverage.brd");
        LargeBoardCaptureStoreInfo incomplete = FrozenStoreInfo(primary) with
        {
            HasCompleteCoverage = false,
            IncompleteFamilies = ["Copper.Trace", "Copper.Shape"],
        };
        var incompleteGateway = new FakeGateway(() => new FakeStore(incomplete));
        var incompleteDiagnostics = new MemoryDiagnostics();
        await using var incompleteWorkflow = Create(
            incompleteGateway, incompleteDiagnostics, () => primary);
        await RequireThrowsAsync<LargeBoardCaptureException>(() =>
            incompleteWorkflow.CaptureAsync(Request("frozen-incomplete")));
        Require(!incompleteWorkflow.HasCapture && incompleteGateway.LastStore!.Disposed &&
                incompleteDiagnostics.Items.Single().FailureCode == "capture_incomplete",
            "Frozen root ordering was used to certify incomplete engineering coverage.");
        LargeBoardCaptureStoreInfo validInfo = FrozenStoreInfo(primary);
        LargeBoardCaptureStoreInfo invalidInfo = validInfo with
        {
            Identity = validInfo.Identity with { ProcessId = validInfo.Identity.ProcessId + 1 },
        };
        var invalidGateway = new FakeGateway(() => new FakeStore(invalidInfo));
        var invalidDiagnostics = new MemoryDiagnostics();
        await using var invalidWorkflow = Create(invalidGateway, invalidDiagnostics, () => primary);
        await RequireThrowsAsync<LargeBoardCaptureException>(() =>
            invalidWorkflow.CaptureAsync(Request("frozen-worker-mismatch")));
        AcquisitionDiagnosticReceipt invalidReceipt = invalidDiagnostics.Items.Single();
        Require(!invalidWorkflow.HasCapture && invalidGateway.LastStore!.Disposed &&
                invalidReceipt.FailureCode == "capture_source_provenance_invalid" &&
                invalidReceipt.IdentityMismatchFields.Contains("frozen_worker_process_id") &&
                invalidReceipt.CapturedHostProcessId == invalidInfo.Identity.ProcessId &&
                invalidReceipt.SourceHostProcessId == primary.ProcessId,
            "Invalid frozen provenance was retained or lost its worker/source failure evidence.");
        return checks + 4;
    }

    private static int CheckFrozenSourceRejections()
    {
        WorkspaceDocumentIdentity primary = Document("frozen-primary", 8, "primary.brd");
        LargeBoardCaptureStoreInfo info = FrozenStoreInfo(primary);
        LargeBoardCaptureIdentity captured = info.Identity;
        CorridorSourceTraversal traversal = captured.SourceTraversal!;
        CorridorFrozenSource frozen = traversal.FrozenSource!;
        int checks = 0;
        WorkspaceDocumentIdentity?[] changedSources =
        [
            primary with { SessionId = "wrong-primary" },
            primary with { SessionGeneration = primary.SessionGeneration + 1 },
            primary with { BoardGeneration = primary.BoardGeneration + 1 },
            primary with { ProcessId = primary.ProcessId + 1 },
            primary with { Design = "wrong-board.brd" },
            primary with { ProtocolVersion = "wrong-protocol" },
            null,
        ];
        foreach (WorkspaceDocumentIdentity? changed in changedSources)
        {
            RequireThrows<LargeBoardCaptureStaleException>(() =>
                    LargeBoardPublicationFence.RequireCurrent(captured, changed),
                "Frozen evidence was admitted with a changed primary source field.");
            checks++;
        }

        LargeBoardCaptureIdentity[] invalidIdentities =
        [
            captured with { SourceTraversal = null },
            captured with { ProcessId = captured.ProcessId + 1 },
            captured with { SessionId = "wrong-worker" },
            captured with { SessionGeneration = captured.SessionGeneration + 1 },
            captured with { BoardGeneration = captured.BoardGeneration + 1 },
            captured with { CaptureToken = "wrong-capture" },
            captured with { ProtocolVersion = "wrong-worker-protocol" },
            captured with { SourceTraversal = traversal with { FrozenSource = null } },
            captured with { SourceTraversal = traversal with { SessionId = "wrong-traversal" } },
            captured with { SourceTraversal = traversal with { BoardGeneration = 9 } },
            captured with { SourceTraversal = traversal with { ObservationToken = "wrong-token" } },
            captured with { SourceTraversal = traversal with { OrderingUniverse = "vias-only" } },
            captured with { SourceTraversal = traversal with { RootCount = frozen.MaximumSourceRoots + 1L } },
        ];
        foreach (LargeBoardCaptureIdentity invalid in invalidIdentities)
        {
            RequireThrows<LargeBoardCaptureStaleException>(() =>
                    LargeBoardPublicationFence.RequireCurrent(invalid, primary),
                "A changed worker identity or invalid traversal was admitted.");
            checks++;
        }

        CorridorFrozenSource[] invalidSources =
        [
            frozen with { SourceDocument = null },
            frozen with { SourceDocument = frozen.SourceDocument! with { Design = null } },
            frozen with { SourceSnapshotHash = null },
            frozen with { SourceSnapshotHash = "not-a-hash" },
            frozen with { InputSha256 = new string('z', 64) },
            frozen with { ObservationId = new string('g', 32) },
            frozen with { WorkerProcessId = frozen.WorkerProcessId + 1 },
            frozen with { MaximumSourceRoots = 0 },
            frozen with { SourceOrderVerified = false },
            frozen with { ExportedAt = null },
            frozen with { ExportedAt = DateTimeOffset.MinValue },
            frozen with { ExportedAt = DateTimeOffset.UtcNow.AddDays(1) },
        ];
        foreach (CorridorFrozenSource invalid in invalidSources)
        {
            LargeBoardCaptureIdentity invalidIdentity = captured with
            {
                SourceTraversal = traversal with { FrozenSource = invalid },
            };
            RequireThrows<LargeBoardCaptureStaleException>(() =>
                    LargeBoardPublicationFence.RequireCurrent(invalidIdentity, primary),
                "Incomplete or inconsistent frozen provenance was admitted.");
            checks++;
        }

        RequireThrows<LargeBoardCaptureStaleException>(() =>
                LargeBoardPublicationFence.RequireCurrent(info with
                {
                    Identity = captured with { SourceTraversal = null },
                }, primary),
            "Store-only provenance was silently omitted from the publication identity.");
        RequireThrows<LargeBoardCaptureStaleException>(() =>
                LargeBoardPublicationFence.RequireCurrent(info with { SourceTraversal = null }, primary),
            "Identity-only provenance was accepted despite conflicting store metadata.");
        return checks + 2;
    }

    private static async Task<int> CheckFrozenReportAsync(
        LargeBoardCaptureIdentity captured,
        WorkspaceDocumentIdentity primary)
    {
        SceneQuery query = RegionQuery("ETCH/REPORT", 100);
        var scene = new DesignScene(
            new(Guid.NewGuid(), DateTimeOffset.UtcNow, new("test", "1", false, "frozen-report")),
            new(DocumentKind.PcbBoard, primary.Design!, "mils", 2,
                new(new(0, 0), new(100, 100))),
            query,
            new CoverageReport([]),
            new SceneData());
        var scan = new ScalableCorridorScan(
            scene, new(0, null, false), 0, 0, [], ["Missing trace inputs"], ["Missing trace inputs"])
        {
            SourceTraversal = captured.SourceTraversal,
        };
        string directory = Path.Combine(Path.GetTempPath(), "pd-frozen-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            bool published = false;
            LargeBoardCorridorReport report = await LargeBoardCorridorReportWriter.PublishAsync(
                scan, primary.Design!, directory, captured, primary, () => primary,
                _ => published = true);
            using JsonDocument data = JsonDocument.Parse(await File.ReadAllTextAsync(report.DataPath));
            JsonElement root = data.RootElement;
            string text = await File.ReadAllTextAsync(report.ReportPath);
            Require(published && root.GetProperty("EvidenceKind").GetString() == "historical-sealed-capture" &&
                    !root.GetProperty("LiveClearOrPassVerified").GetBoolean() &&
                    !root.GetProperty("HasCompleteInputs").GetBoolean(),
                "Frozen report publication represented historical or incomplete inputs as live Clear/Pass.");
            Require(root.GetProperty("CaptureIdentity").GetProperty("ProcessId").GetInt32() == captured.ProcessId &&
                    root.GetProperty("SourceDocument").GetProperty("ProcessId").GetInt32() == primary.ProcessId &&
                    text.Contains("Live-current Clear/Pass is not verified", StringComparison.Ordinal) &&
                    text.Contains(captured.SessionId, StringComparison.Ordinal) &&
                    text.Contains(primary.SessionId, StringComparison.Ordinal),
                "Published report lost worker/primary provenance or the historical action boundary.");
            await RequireThrowsAsync<InvalidDataException>(() => LargeBoardCorridorReportWriter.PublishAsync(
                scan with { SourceTraversal = null }, primary.Design!, directory, captured, primary,
                () => primary, _ => throw new InvalidOperationException("Mismatched scan published.")));
            Require(Directory.GetFiles(directory).Length == 2,
                "A mismatched frozen report left partial artifacts or changed the earlier report.");
            return 4;
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static LargeBoardCaptureStoreInfo FrozenStoreInfo(WorkspaceDocumentIdentity primary)
    {
        string observation = Guid.NewGuid().ToString("N");
        const int workerProcessId = 4321;
        var frozen = new CorridorFrozenSource(observation, new string('a', 64), workerProcessId, 2_000_000, true)
        {
            SourceDocument = new(primary.SessionId, primary.SessionGeneration, primary.BoardGeneration,
                primary.ProcessId, primary.Design, primary.ProtocolVersion),
            SourceSnapshotHash = new string('b', 64),
            ExportedAt = DateTimeOffset.UtcNow.AddDays(-30),
        };
        var worker = new WorkspaceDocumentIdentity(
            "observation-" + observation, 1, 1, workerProcessId, "immutable-worker-input.brd", primary.ProtocolVersion);
        LargeBoardCaptureStoreInfo info = StoreInfo(worker);
        var traversal = new CorridorSourceTraversal(
            1, "all_board_copper_roots_v1", worker.SessionId, worker.BoardGeneration,
            info.Identity.CaptureToken, 240_000)
        {
            FrozenSource = frozen,
        };
        return info with
        {
            Identity = info.Identity with { SourceTraversal = traversal },
            SourceTraversal = traversal,
        };
    }

    private static async Task CheckExplicitCaptureAndReplayAsync()
    {
        WorkspaceDocumentIdentity document = Document("session-a", 7, "board-alpha.brd");
        var gateway = new FakeGateway(() => new FakeStore(StoreInfo(document)));
        var diagnostics = new MemoryDiagnostics();
        await using var workflow = Create(gateway, diagnostics, () => document);

        Require(gateway.CaptureCount == 0 && diagnostics.Items.Count == 0,
            "Constructing the workflow performed an implicit startup capture.");

        var budgets = new LargeBoardCaptureBudgets
        {
            MaximumNativeObjectRecords = 345_678,
            MaximumNativeObjectVisits = 900_000,
            MaximumPages = 456,
        };
        var request = new LargeBoardCaptureRequest(
            "explorer-region",
            "Capture large board",
            true,
            budgets)
        {
            CorrelationId = "correlation-a",
        };
        LargeBoardCaptureResult captured = await workflow.CaptureAsync(request);
        Require(gateway.CaptureCount == 1 && workflow.HasCapture,
            "The explicit capture did not retain exactly one sealed store.");
        Require(captured.CorrelationId == request.CorrelationId,
            "Capture lost its request correlation.");

        SceneQuery query = RegionQuery("ETCH/S12", 12_345);
        LargeBoardReplayResult<string> replay = await workflow.ReplayRegionAsync(query, true);
        Require(replay.Scene == "typed-region" && replay.Query == query,
            "The bounded typed replay did not preserve its scene and query.");
        Require(gateway.LastStore?.LastQuery == query,
            "The workflow replaced the caller's bounded query.");
        Require(gateway.LastStore?.LastReplayBudgets?.MaximumMaterializedObjects ==
                query.MaximumObjects,
            "The workflow did not send the explicit replay materialization budget.");
        Require(diagnostics.Items.Count == 2,
            "Capture and replay did not each retain a terminal receipt.");
        AcquisitionDiagnosticReceipt captureReceipt = diagnostics.Items[0];
        AcquisitionDiagnosticReceipt replayReceipt = diagnostics.Items[1];
        Require(captureReceipt.Caller == request.Caller &&
                captureReceipt.CorrelationId == request.CorrelationId &&
                captureReceipt.CaptureBudgets == budgets,
            "Caller, correlation, or native budgets were lost from diagnostics.");
        Require(replayReceipt.QueryKind == SceneReadKind.RegionGeometry.ToString() &&
                replayReceipt.MaterializedSceneObjectBudget == query.MaximumObjects &&
                replayReceipt.ReplayBudgets?.MaximumMaterializedObjects == query.MaximumObjects &&
                replayReceipt.HasRegion && replayReceipt.LayerFingerprints.Length == 1,
            "Replay query scope or materialization budget was lost from diagnostics.");
        Require(replayReceipt.DesignFingerprint != document.Design &&
                replayReceipt.SessionFingerprint != document.SessionId,
            "Diagnostics retained raw design or session identity.");
        Require(captureReceipt.CaptureTokenFingerprint.Length != 0 &&
                captureReceipt.CaptureTokenFingerprint !=
                    captured.Store.Identity.CaptureToken &&
                replayReceipt.CaptureTokenFingerprint ==
                    captureReceipt.CaptureTokenFingerprint,
            "Capture-scoped diagnostics lost the token fingerprint or retained the raw token.");
        Require(captureReceipt.CapturePageCount == captured.Store.PageCount &&
                captureReceipt.CaptureRecordCount == captured.Store.RecordCount &&
                captureReceipt.CaptureStoredBytes == captured.Store.StoredBytes &&
                captureReceipt.CaptureResources.SequenceEqual(captured.Store.Resources),
            "Capture diagnostics lost sealed-store scale or resource evidence.");
        RequireThrows<InvalidOperationException>(
            workflow.RequireFreshLiveReadForNativeAction,
            "Historical replay was allowed to authorize a native action.");
    }

    private static async Task CheckCancellationAsync(bool cleanupComplete)
    {
        WorkspaceDocumentIdentity document = Document("cancel-session", 3, "cancel.brd");
        var gateway = new FakeGateway(async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("unreachable");
            }
            catch (OperationCanceledException error) when (token.IsCancellationRequested)
            {
                throw new EngineBulkCaptureCancelledException(
                    cleanupComplete,
                    token,
                    error);
            }
        });
        var diagnostics = new MemoryDiagnostics();
        await using var workflow = Create(gateway, diagnostics, () => document);
        using var cancellation = new CancellationTokenSource();
        Task capture = workflow.CaptureAsync(Request("cancel-request"), cancellation.Token);
        await gateway.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        LargeBoardCaptureCancelledException cancellationFailure =
            await RequireThrowsAsync<LargeBoardCaptureCancelledException>(() => capture);
        AcquisitionDiagnosticReceipt receipt = diagnostics.Items.Single();
        Require(!workflow.HasCapture &&
                receipt.TerminalState == AcquisitionTerminalState.Cancelled &&
                receipt.PartialDataWithheld &&
                receipt.CleanupComplete == cleanupComplete &&
                receipt.CleanupDisposition == (cleanupComplete ? "confirmed" : "failed") &&
                cancellationFailure.CleanupComplete == cleanupComplete &&
                cancellationFailure.Message.Contains(
                    cleanupComplete ? "cleanup completed" : "could not be confirmed",
                    StringComparison.Ordinal),
            "Cancellation cleanup evidence was lost or presented with the wrong confidence.");
    }

    private static async Task CheckResourceFailureAsync()
    {
        WorkspaceDocumentIdentity document = Document("limit-session", 5, "large.brd");
        var gateway = new FakeGateway(_ => throw new EngineBulkCaptureResourceException(new(
            "native-object-records",
            2_000_000,
            2_000_001,
            "native-traversal",
            CleanupComplete: true,
            PartialDataWithheld: true)));
        var diagnostics = new MemoryDiagnostics();
        await using var workflow = Create(gateway, diagnostics, () => document);
        LargeBoardCaptureException failure = await RequireThrowsAsync<LargeBoardCaptureException>(
            () => workflow.CaptureAsync(Request("limit-request")));
        AcquisitionDiagnosticReceipt receipt = diagnostics.Items.Single();
        Require(failure.Message.Contains("2,000,001 observed", StringComparison.Ordinal) &&
                receipt.FailureCode == "capture_resource_exhausted" &&
                receipt.ConfiguredLimit == 2_000_000 &&
                receipt.ObservedCount == 2_000_001 &&
                receipt.CleanupComplete == true &&
                receipt.PartialDataWithheld,
            "Structured resource exhaustion evidence was not retained exactly.");
    }

    private static async Task CheckIncompleteCaptureAsync()
    {
        WorkspaceDocumentIdentity document = Document("partial-session", 8, "partial.brd");
        var store = new FakeStore(StoreInfo(document) with
        {
            HasCompleteCoverage = false,
            IncompleteFamilies = ["Copper"],
        });
        var gateway = new FakeGateway(() => store);
        var diagnostics = new MemoryDiagnostics();
        await using var workflow = Create(gateway, diagnostics, () => document);
        await RequireThrowsAsync<LargeBoardCaptureException>(
            () => workflow.CaptureAsync(Request("partial-request")));
        Require(!workflow.HasCapture && store.Disposed &&
                diagnostics.Items.Single().FailureCode == "capture_incomplete",
            "Incomplete coverage was retained or presented as clear success.");
    }

    private static void CheckOrdinarySceneResourceReceipt()
    {
        SceneQuery query = SceneQuery.CompleteBoard(includeContours: false) with
        {
            MaximumObjects = 100_000,
        };
        var exception = new EngineSceneAcquisitionResourceException(new(
            "native-object-records",
            100_000,
            100_001,
            "native-traversal",
            CleanupComplete: true,
            PartialSceneWithheld: true));
        LargeBoardFailure failure = LargeBoardFailure.From(exception);
        AcquisitionDiagnosticReceipt receipt =
            AcquisitionDiagnosticReceiptFactory.CreateSceneReceipt(
                "ordinary-correlation",
                "dp-via-corridor",
                "Run corridor check",
                applicationVisible: true,
                Document("ordinary-session", 6, "ordinary-large.brd"),
                query,
                "1.0-test",
                "preview-test",
                DateTimeOffset.UtcNow,
                elapsedMilliseconds: 10,
                AcquisitionTerminalState.Failed,
                failure,
                failure.UserMessage);

        Require(receipt.Feature == "engine-scene-read" &&
                receipt.QueryKind == SceneReadKind.CompleteBoard.ToString() &&
                receipt.MaterializedSceneObjectBudget == 100_000 &&
                receipt.CaptureBudgets is null,
            "The ordinary scene receipt did not retain its exact request shape.");
        Require(receipt.FailureCode == "scene_resource_exhausted" &&
                receipt.ExhaustedResource == "native-object-records" &&
                receipt.ConfiguredLimit == 100_000 &&
                receipt.ObservedCount == 100_001 &&
                receipt.CleanupComplete == true &&
                receipt.PartialDataWithheld,
            "The ordinary scene receipt lost structured resource evidence.");
    }

    private static async Task CheckCorridorReplayFailureReceiptAsync()
    {
        WorkspaceDocumentIdentity document = Document("corridor-session", 14, "corridor.brd");
        var diagnostics = new MemoryDiagnostics();
        await using var workflow = Create(
            new FakeGateway(() => new FakeStore(StoreInfo(document))),
            diagnostics,
            () => document);
        await workflow.CaptureAsync(Request("corridor-replay-request"));

        SceneQuery query = RegionQuery("ETCH/S03", 50_000);
        var budgets = new LargeBoardReplayBudgets
        {
            MaximumMaterializedObjects = 50_000,
            MaximumPagesRead = 200,
            MaximumRecordsRead = 400_000,
        };
        var resourceFailure = new EngineBulkReplayResourceException(new(
            "records-read",
            400_000,
            400_001,
            "page-decode",
            CleanupComplete: true,
            PartialDataWithheld: true));
        var attributed = new LargeBoardCorridorReplayException(
            "bounded-batch-2",
            query,
            budgets,
            resourceFailure);

        await RequireThrowsAsync<LargeBoardCaptureException>(() =>
            workflow.RunWithCurrentCaptureAsync(
                (_, _) => ValueTask.FromException<string>(attributed),
                "Run scalable corridor check",
                applicationVisible: true));
        AcquisitionDiagnosticReceipt receipt = diagnostics.Items.Last();
        Require(receipt.UserAction == "Run scalable corridor check" &&
                receipt.QueryKind == SceneReadKind.RegionGeometry.ToString() &&
                receipt.ReplayBudgets == budgets &&
                receipt.FailureCode == "replay_resource_exhausted" &&
                receipt.ExhaustedResource == "records-read" &&
                receipt.ConfiguredLimit == 400_000 &&
                receipt.ObservedCount == 400_001 &&
                receipt.CleanupComplete == true &&
                receipt.PartialDataWithheld,
            "The corridor replay receipt lost its exact request or structured failure evidence.");
    }

    private static async Task CheckIdentityFencingAsync()
    {
        WorkspaceDocumentIdentity current = Document("stable-session", 11, "first-name.brd");
        var gateway = new FakeGateway(() => new FakeStore(StoreInfo(current)));
        await using var workflow = Create(gateway, new MemoryDiagnostics(), () => current);
        await workflow.CaptureAsync(Request("identity-request"));

        current = current with { Design = "renamed-board-and-objects.brd" };
        LargeBoardReplayResult<string> renamed = await workflow.ReplayRegionAsync(
            RegionQuery("RENAMED_LAYER", 20_000), true);
        Require(renamed.Scene == "typed-region",
            "Changing descriptive identifiers changed equivalent bounded replay behavior.");

        current = current with { BoardGeneration = current.BoardGeneration + 1 };
        LargeBoardCaptureException stale = await RequireThrowsAsync<LargeBoardCaptureException>(
            () => workflow.ReplayRegionAsync(RegionQuery("RENAMED_LAYER", 20_000), true));
        Require(stale.InnerException is LargeBoardCaptureStaleException && !workflow.HasCapture,
            "A changed board generation did not reject the retained capture (negative control).");

        WorkspaceDocumentIdentity captureStart =
            Document("capture-fence-session", 31, "capture-fence.brd");
        LargeBoardCaptureStoreInfo staleInfo = StoreInfo(captureStart with
        {
            BoardGeneration = captureStart.BoardGeneration + 1,
        }) with
        {
            Timing = new(901, 701, 100, 40, 60),
        };
        var staleDiagnostics = new MemoryDiagnostics();
        await using var staleWorkflow = Create(
            new FakeGateway(() => new FakeStore(staleInfo)),
            staleDiagnostics,
            () => captureStart);
        await RequireThrowsAsync<LargeBoardCaptureException>(() =>
            staleWorkflow.CaptureAsync(Request("capture-fence-request")));
        AcquisitionDiagnosticReceipt staleReceipt = staleDiagnostics.Items.Single();
        Require(staleReceipt.FailureCode == "board_generation_changed" &&
                staleReceipt.IdentityMismatchFields.SequenceEqual(["board_generation"]) &&
                staleReceipt.CapturedBoardGeneration == staleInfo.Identity.BoardGeneration &&
                staleReceipt.ObservedBoardGeneration == captureStart.BoardGeneration &&
                staleReceipt.CapturedSessionFingerprint.Length != 0 &&
                staleReceipt.CapturedSessionFingerprint != staleInfo.Identity.SessionId &&
                staleReceipt.ObservedSessionFingerprint.Length != 0 &&
                staleReceipt.ObservedSessionFingerprint != captureStart.SessionId,
            "The publication-fence receipt lost exact, privacy-safe identity mismatch evidence.");
        Require(staleReceipt.CapturePageCount == staleInfo.PageCount &&
                staleReceipt.CaptureRecordCount == staleInfo.RecordCount &&
                staleReceipt.CaptureStoredBytes == staleInfo.StoredBytes &&
                staleReceipt.CaptureResources.SequenceEqual(staleInfo.Resources) &&
                staleReceipt.CapturePhaseTiming == staleInfo.Timing,
            "A withheld sealed candidate lost its scale, resource, or timing evidence.");
    }

    private static void CheckPublicationFence()
    {
        WorkspaceDocumentIdentity current =
            Document("publication-session", 21, "captured-name.brd");
        LargeBoardCaptureIdentity captured = StoreInfo(current).Identity;

        WorkspaceDocumentIdentity renamed = current with
        {
            Design = "renamed-descriptive-label.brd",
        };
        Require(ReferenceEquals(
                renamed,
                LargeBoardPublicationFence.RequireCurrent(captured, renamed)),
            "The publication fence rejected an equivalent renamed document.");

        WorkspaceDocumentIdentity changed = current with
        {
            BoardGeneration = current.BoardGeneration + 1,
        };
        RequireThrows<LargeBoardCaptureStaleException>(
            () => LargeBoardPublicationFence.RequireCurrent(captured, changed),
            "A stale result was admitted after the board generation changed.");
        RequireThrows<LargeBoardCaptureStaleException>(
            () => LargeBoardPublicationFence.RequireCurrent(captured, null),
            "A stale result was admitted after the board disconnected.");
    }

    private static LargeBoardCaptureWorkflow<string> Create(
        FakeGateway gateway,
        MemoryDiagnostics diagnostics,
        Func<WorkspaceDocumentIdentity?> current) =>
        new(gateway, diagnostics, current, "1.0-test", "preview.104-test");

    private static LargeBoardCaptureRequest Request(string correlation) => new(
        "large-board-test",
        "Explicit test capture",
        true,
        new LargeBoardCaptureBudgets { IncludeContours = false })
    {
        CorrelationId = correlation,
    };

    private static WorkspaceDocumentIdentity Document(
        string session,
        long boardGeneration,
        string design) =>
        new(session, 2, boardGeneration, 1234, design, "PD_V25");

    private static LargeBoardCaptureStoreInfo StoreInfo(WorkspaceDocumentIdentity document) =>
        new(
            new("capture-token-test", document.SessionId,
                document.SessionGeneration, document.BoardGeneration,
                document.ProcessId, document.Design, document.ProtocolVersion),
            true,
            12,
            240_000,
            8_000_000,
            [new LargeBoardCaptureResource("objects", 2_000_000, 240_000, false)],
            []);

    private static SceneQuery RegionQuery(string layer, int maximumObjects) => new()
    {
        Kind = SceneReadKind.RegionGeometry,
        Families = [DataFamily.Nets, DataFamily.Layers, DataFamily.Copper],
        Layers = [new LayerId(layer)],
        Region = new(new DesignPoint(100, 200), new DesignPoint(900, 1200)),
        IncludeContours = false,
        MaximumObjects = maximumObjects,
    };

    private static async Task<TException> RequireThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Large-board check failed: " + message);
    }

    private sealed class MemoryDiagnostics : IAcquisitionDiagnosticOwner
    {
        public List<AcquisitionDiagnosticReceipt> Items { get; } = [];

        public ValueTask RetainAsync(AcquisitionDiagnosticReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Items.Add(receipt);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeGateway : ILargeBoardCaptureGateway<string>
    {
        private readonly Func<CancellationToken, ValueTask<ILargeBoardCaptureStore<string>>> _capture;

        public FakeGateway(Func<FakeStore> capture)
            : this(_ => ValueTask.FromResult<ILargeBoardCaptureStore<string>>(capture())) { }

        public FakeGateway(Func<CancellationToken, ValueTask<ILargeBoardCaptureStore<string>>> capture)
        {
            _capture = capture;
        }

        public int CaptureCount { get; private set; }
        public FakeStore? LastStore { get; private set; }
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ILargeBoardCaptureStore<string>> CaptureAsync(
            LargeBoardCaptureBudgets budgets,
            CancellationToken cancellationToken)
        {
            CaptureCount++;
            Started.TrySetResult();
            ILargeBoardCaptureStore<string> store = await _capture(cancellationToken);
            LastStore = store as FakeStore;
            return store;
        }
    }

    private sealed class FakeStore : ILargeBoardCaptureStore<string>
    {
        public FakeStore(LargeBoardCaptureStoreInfo info) => Info = info;

        public LargeBoardCaptureStoreInfo Info { get; }
        public SceneQuery? LastQuery { get; private set; }
        public LargeBoardReplayBudgets? LastReplayBudgets { get; private set; }
        public bool Disposed { get; private set; }

        public ValueTask<LargeBoardReplayPayload<string>> ReplayAsync(
            SceneQuery query,
            LargeBoardReplayBudgets budgets,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastQuery = query;
            LastReplayBudgets = budgets;
            return ValueTask.FromResult(new LargeBoardReplayPayload<string>(
                "typed-region", 4, 3, 500, 120, 80, []));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
