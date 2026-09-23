using System.Collections.Immutable;
using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Geometry;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;
using PD.Simple.Corridor;
using PD.Simple.LargeBoards;

internal static class LargeBoardCorridorRunnerChecks
{
    public static async Task<int> RunAsync()
    {
        await CheckReplayShapesBudgetsAndOrderAsync();
        await CheckKnownViaMetadataIsAdvisoryAsync();
        await CheckPartialReplayIsRejectedAsync();
        await CheckStructuredReplayFailureIsAttributedAsync();
        await CheckCancellationBeforeReplayAsync();
        int reportChecks = await CheckCompleteReportAsync();
        await CheckOrdinaryCaptureLifetimeAsync();
        await CheckPagedPlanningBeyondSceneLimitAsync();
        await CheckPlanningSubdivisionAndMinimumTileAsync();
        await CheckDenseAggressorSubdivisionAsync();
        await CheckAggressorReadBudgetSubdivisionAsync();
        await CheckPlanningBatchMatchesScalarAsync();
        int zeroPassChecks = await CheckZeroPassPlannedGroupsMatchOnePassAsync();
        int batchChecks = await CheckBatchedReplayAsync();
        int sourceOrderChecks = await CheckSourceTraversalProjectionAsync();
        return 56 + batchChecks + reportChecks + zeroPassChecks + sourceOrderChecks +
            await CorridorSealedReplayChecks.RunAsync();
    }

    private static async Task<int> CheckSourceTraversalProjectionAsync()
    {
        DesignScene full = Fixture();
        var traversal = new CorridorSourceTraversal(
            1, "all_board_copper_roots_v1", "session", 1, "capture-token-runner", 1_000);
        var originalStore = new FakeStore(full, sourceTraversal: traversal)
        {
            SourceOrdinal = ordinal => ordinal * 20 + 5,
            CaptureOrdinal = ordinal => ordinal * 30 + 4,
        };
        var compactedStore = new FakeStore(full, sourceTraversal: traversal)
        {
            SourceOrdinal = ordinal => ordinal * 20 + 5,
        };
        LargeBoardCorridorRunResult original = await LargeBoardCorridorRunner.RunAsync(
            originalStore, new(0, null, false), new(), new(new(), new()));
        LargeBoardCorridorRunResult compacted = await LargeBoardCorridorRunner.RunAsync(
            compactedStore, new(0, null, false), new(), new(new(), new()));
        Require(original.Scan.SourceTraversal == traversal && compacted.Scan.SourceTraversal == traversal &&
                original.Scan.Findings.Select(item => item.Finding)
                    .SequenceEqual(compacted.Scan.Findings.Select(item => item.Finding)) &&
                original.Scan.CoverageWarnings.SequenceEqual(compacted.Scan.CoverageWarnings),
            "The runner lost source-order evidence or changed findings after emitted ordinal compaction.");
        Require(compacted.Scan.Findings.Count > 0 && compacted.Scan.Findings.All(item =>
                item.Witnesses.PositiveIdentity.SourceTraversalOrdinal is not null &&
                item.Witnesses.NegativeIdentity.SourceTraversalOrdinal is not null &&
                item.Witnesses.AggressorIdentity.SourceTraversalOrdinal is not null &&
                item.Finding.PositiveViaIndex == 0 && item.Finding.NegativeViaIndex == 1 &&
                item.Finding.AggressorIndex == 2),
            "The runner projected source ordinals into local witness indexes or discarded them.");
        var mismatched = new FakeStore(full, sourceTraversal: traversal with { SessionId = "other-session" });
        await RequireThrowsAsync<InvalidDataException>(() => LargeBoardCorridorRunner.RunAsync(
            mismatched, new(0, null, false), new(), new(new(), new())));
        Require(mismatched.Requests.Count == 0,
            "A mismatched capture ordering declaration was used before validation.");
        await RequireThrowsAsync<InvalidDataException>(() => LargeBoardCorridorRunner.RunAsync(
            new FakeStore(full, sourceTraversal: traversal),
            new(0, null, false), new(), new(new(), new())));
        await RequireThrowsAsync<InvalidDataException>(() => LargeBoardCorridorRunner.RunAsync(
            new FakeStore(full) { SourceOrdinal = ordinal => ordinal },
            new(0, null, false), new(), new(new(), new())));
        return 6;
    }

    private static async Task CheckPlanningBatchMatchesScalarAsync()
    {
        DesignScene fixture = Fixture();
        DesignScene metadata = new(
            fixture.Identity,
            fixture.Document with
            {
                Bounds = new(new(-1_000, -1_000), new(24_000, 1_000)),
            },
            fixture.Query,
            fixture.Coverage,
            fixture.Data with { Copper = [] });
        ImmutableArray<CopperObject> copper =
        [
            Via("BUS_P", 0, "via:planning-positive"),
            Via("BUS_N", 50, "via:planning-negative"),
            Segment("CLOCK_TEST", 25, -20, 20, "trace:planning-aggressor"),
        ];
        var scalarStore = new FakeStore(metadata, capturedCopper: copper);
        LargeBoardCorridorRunResult scalar = await LargeBoardCorridorRunner.RunAsync(
            scalarStore, new(0, null, false), new(), new(new(), new()));

        var groupedStore = new FakeBatchStore(new FakeStore(metadata, capturedCopper: copper));
        LargeBoardCorridorRunResult grouped = await LargeBoardCorridorRunner.RunAsync(
            groupedStore, new(0, null, false), new(), new(new(), new()));
        Require(groupedStore.BatchQueries.Any(group =>
                group.Length == 5 && group.All(query =>
                    query.Kind == SceneReadKind.RegionGeometry &&
                    query.Families.SequenceEqual([DataFamily.Copper]) &&
                    query.CopperKinds.SequenceEqual([CopperKind.Via]) &&
                    query.Region is not null)) &&
                grouped.Counts.PlanningViaReplayCount == 5 &&
                grouped.Counts.PlanningSubdivisionCount == 0,
            "Multi-tile via planning did not share a bounded sealed replay pass.");
        Require(grouped.Counts.GlobalRecordsSelected == scalar.Counts.GlobalRecordsSelected &&
                grouped.Counts.RetainedSubjectViaCount == scalar.Counts.RetainedSubjectViaCount &&
                grouped.Scan.Findings.Select(static item => item.Finding)
                    .SequenceEqual(scalar.Scan.Findings.Select(static item => item.Finding)) &&
                grouped.Scan.Findings.Select(static item => (
                    item.Witnesses.PositiveIdentity,
                    item.Witnesses.NegativeIdentity,
                    item.Witnesses.AggressorIdentity))
                    .SequenceEqual(scalar.Scan.Findings.Select(static item => (
                        item.Witnesses.PositiveIdentity,
                        item.Witnesses.NegativeIdentity,
                        item.Witnesses.AggressorIdentity))),
            "Grouped via planning changed subject order, all-net findings, or replay accounting.");

        var misordered = new FakeBatchStore(new FakeStore(metadata, capturedCopper: copper))
        {
            ReverseResults = true,
        };
        LargeBoardCorridorReplayException failure =
            await RequireThrowsAsync<LargeBoardCorridorReplayException>(() =>
                LargeBoardCorridorRunner.RunAsync(
                    misordered, new(0, null, false), new(), new(new(), new())));
        Require(failure.Stage == "bounded-via-planning-group" &&
                failure.InnerException is InvalidDataException,
            "Misordered planning scenes were accepted or retried through scalar replay.");
    }

    private static async Task<int> CheckZeroPassPlannedGroupsMatchOnePassAsync()
    {
        DesignScene fixture = Fixture();
        DesignScene metadata = new(
            fixture.Identity,
            fixture.Document with
            {
                Bounds = new(new(-1_000, -1_000), new(24_000, 1_000)),
            },
            fixture.Query,
            fixture.Coverage,
            fixture.Data with { Copper = [] });
        ImmutableArray<CopperObject> copper =
        [
            Via("BUS_P", 0, "via:planning-positive"),
            Via("BUS_N", 50, "via:planning-negative"),
            Segment("CLOCK_TEST", 25, -20, 20, "trace:planning-aggressor"),
        ];

        var onePassStore = new FakeBatchStore(new FakeStore(metadata, capturedCopper: copper));
        LargeBoardCorridorRunResult onePass = await LargeBoardCorridorRunner.RunAsync(
            onePassStore, new(0, null, false), new(), new(new(), new()));

        var zeroPassStore = new FakeBatchStore(new FakeStore(metadata, capturedCopper: copper))
        {
            RootIndexPasses = 0,
        };
        LargeBoardCorridorRunResult zeroPass = await LargeBoardCorridorRunner.RunAsync(
            zeroPassStore, new(0, null, false), new(), new(new(), new()));

        Require(zeroPassStore.BatchCalls == onePassStore.BatchCalls &&
                zeroPassStore.BatchCalls > 0 &&
                onePassStore.BatchQueries.Any(group => group.Length == 5) &&
                zeroPassStore.BatchQueries.Any(group => group.Length == 5) &&
                zeroPass.Counts.SuccessfulBatchReplayGroups == onePass.Counts.SuccessfulBatchReplayGroups &&
                zeroPass.Counts.SharedRootIndexPasses == 0 &&
                onePass.Counts.SharedRootIndexPasses == onePass.Counts.SuccessfulBatchReplayGroups &&
                zeroPass.Counts with { SharedRootIndexPasses = onePass.Counts.SharedRootIndexPasses } == onePass.Counts,
            "Zero-pass planned groups changed replay grouping or logical counts.");
        Require(zeroPass.Scan.Findings.Select(static item => item.Finding)
                .SequenceEqual(onePass.Scan.Findings.Select(static item => item.Finding)) &&
                zeroPass.Scan.Findings.Select(static item => (
                    item.Witnesses.PositiveIdentity,
                    item.Witnesses.NegativeIdentity,
                    item.Witnesses.AggressorIdentity))
                    .SequenceEqual(onePass.Scan.Findings.Select(static item => (
                        item.Witnesses.PositiveIdentity,
                        item.Witnesses.NegativeIdentity,
                        item.Witnesses.AggressorIdentity))),
            "Zero-pass planned groups changed findings, witness order, or all-net coverage.");

        foreach (int invalidPasses in new[] { -1, 2 })
        {
            var invalidStore = new FakeBatchStore(new FakeStore(metadata, capturedCopper: copper))
            {
                RootIndexPasses = invalidPasses,
            };
            LargeBoardCorridorReplayException failure =
                await RequireThrowsAsync<LargeBoardCorridorReplayException>(() =>
                    LargeBoardCorridorRunner.RunAsync(
                        invalidStore, new(0, null, false), new(), new(new(), new())));
            Require(failure.InnerException is InvalidDataException,
                "RootIndexPasses=" + invalidPasses + " was accepted or retried instead of failing closed.");
        }
        return 4;
    }

    private static async Task<int> CheckBatchedReplayAsync()
    {
        DesignScene fixture = Fixture();
        var scalarStore = new FakeStore(fixture);
        LargeBoardCorridorRunResult scalar = await LargeBoardCorridorRunner.RunAsync(
            scalarStore, new(0, null, false), new(), new(new(), new()));

        var groupedInner = new FakeStore(fixture);
        var groupedStore = new FakeBatchStore(groupedInner);
        LargeBoardCorridorRunResult grouped = await LargeBoardCorridorRunner.RunAsync(
            groupedStore, new(0, null, false), new(), new(new(), new()));
        Require(groupedStore.BatchCalls == 1 &&
                grouped.Counts.SuccessfulBatchReplayGroups == 1 &&
                grouped.Counts.BatchReplayGroupFallbacks == 0 &&
                grouped.Counts.SharedRootIndexPasses == 1 &&
                grouped.Counts.SharedPagesRead == 2 &&
                grouped.Counts.SharedRecordsRead == 20 &&
                grouped.Counts.SharedLogicalPageMembershipBytes == 4_096 &&
                grouped.Counts.BatchPagesRead == scalar.Counts.BatchPagesRead &&
                grouped.Counts.BatchRecordsSelected == scalar.Counts.BatchRecordsSelected,
            "The optional batch store did not retain logical counts and separate shared-I/O metrics.");
        Require(grouped.Scan.Findings.Select(static item => item.Finding)
                .SequenceEqual(scalar.Scan.Findings.Select(static item => item.Finding)) &&
                grouped.Scan.Findings.Select(static item => item.Witnesses.AggressorIdentity)
                    .SequenceEqual(scalar.Scan.Findings.Select(static item => item.Witnesses.AggressorIdentity)) &&
                groupedInner.Requests.Skip(2).All(static request =>
                    request.Query.Families.SequenceEqual([DataFamily.Layers, DataFamily.Copper]) &&
                    request.Query.CopperKinds.SequenceEqual(
                        [CopperKind.Trace, CopperKind.Via, CopperKind.Shape])),
            "Batched replay changed all-net aggressor coverage, findings, or source order.");

        var limitedInner = new FakeStore(fixture);
        var limitedStore = new FakeBatchStore(limitedInner)
        {
            BatchFailure = new EngineBulkReplayResourceException(new(
                "batch_selected_records", 1, 2, "record_selection",
                CleanupComplete: true, PartialDataWithheld: true)),
        };
        LargeBoardCorridorRunResult recovered = await LargeBoardCorridorRunner.RunAsync(
            limitedStore, new(0, null, false), new(), new(new(), new()));
        Require(limitedStore.BatchCalls == 1 &&
                recovered.Counts.BatchReplayGroupFallbacks == 1 &&
                recovered.Counts.SuccessfulBatchReplayGroups == 0 &&
                recovered.Counts.CompletedBatches == scalar.Counts.CompletedBatches &&
                recovered.Scan.Findings.Select(static item => item.Finding)
                    .SequenceEqual(scalar.Scan.Findings.Select(static item => item.Finding)),
            "A batch resource limit did not split to scalar leaves without changing findings.");

        var unsupported = new FakeBatchStore(new FakeStore(fixture))
        {
            BatchFailure = new NotSupportedException("batched replay not available"),
        };
        LargeBoardCorridorRunResult unsupportedFallback = await LargeBoardCorridorRunner.RunAsync(
            unsupported, new(0, null, false), new(), new(new(), new()));
        Require(unsupported.BatchCalls == 1 &&
                unsupportedFallback.Counts.BatchReplayGroupFallbacks == 1 &&
                unsupportedFallback.Scan.Findings.Select(static item => item.Finding)
                    .SequenceEqual(scalar.Scan.Findings.Select(static item => item.Finding)),
            "An optional unsupported batch capability did not use the scalar fallback.");

        var reordered = new FakeBatchStore(new FakeStore(fixture))
        {
            ReverseResults = true,
        };
        LargeBoardCorridorReplayException reorderedFailure =
            await RequireThrowsAsync<LargeBoardCorridorReplayException>(() =>
                LargeBoardCorridorRunner.RunAsync(
                    reordered, new(0, null, false), new(), new(new(), new())));
        Require(reorderedFailure.InnerException is InvalidDataException &&
                reorderedFailure.Stage.StartsWith("bounded-batch-group-", StringComparison.Ordinal) &&
                reordered.BatchCalls == 1,
            "An out-of-order batch payload was retried or published instead of failing closed.");

        var corruptInner = new FakeStore(fixture);
        var corrupt = new FakeBatchStore(corruptInner)
        {
            BatchFailure = new InvalidDataException("selected sealed page failed its hash"),
        };
        LargeBoardCorridorReplayException corruptFailure =
            await RequireThrowsAsync<LargeBoardCorridorReplayException>(() =>
                LargeBoardCorridorRunner.RunAsync(
                    corrupt, new(0, null, false), new(), new(new(), new())));
        Require(corruptFailure.InnerException is InvalidDataException &&
                corrupt.BatchCalls == 1 && corruptInner.Requests.Count == 2,
            "A corrupt batch was retried through scalar replay or published.");

        var uncertainCleanupInner = new FakeStore(fixture);
        var uncertainCleanup = new FakeBatchStore(uncertainCleanupInner)
        {
            BatchFailure = new EngineBulkReplayResourceException(new(
                "batch_selected_records", 1, 2, "record_selection",
                CleanupComplete: false, PartialDataWithheld: true)),
        };
        LargeBoardCorridorReplayException uncertainCleanupFailure =
            await RequireThrowsAsync<LargeBoardCorridorReplayException>(() =>
                LargeBoardCorridorRunner.RunAsync(
                    uncertainCleanup, new(0, null, false), new(), new(new(), new())));
        Require(uncertainCleanupFailure.InnerException is EngineBulkReplayResourceException &&
                uncertainCleanup.BatchCalls == 1 && uncertainCleanupInner.Requests.Count == 2,
            "A batch with unconfirmed cleanup was retried instead of failing closed.");

        using var cancellation = new CancellationTokenSource();
        var cancelledInner = new FakeStore(fixture);
        var cancelled = new FakeBatchStore(cancelledInner)
        {
            AfterBatchQuery = index =>
            {
                if (index == 0)
                {
                    cancellation.Cancel();
                }
            },
        };
        await RequireThrowsAsync<OperationCanceledException>(() => LargeBoardCorridorRunner.RunAsync(
            cancelled, new(0, null, false), new(), new(new(), new()), cancellation.Token));
        Require(cancelled.BatchCalls == 1 && cancelledInner.Requests.Count == 3,
            "Cancellation inside a batch continued to another query or published a result.");
        return 8;
    }

    private static async Task CheckOrdinaryCaptureLifetimeAsync()
    {
        DesignScene small = Fixture();
        ImmutableArray<CopperObject> many = small.Data.Copper.AddRange(
            Enumerable.Range(0, 100_001).Select(index =>
                Segment("UNRELATED", 2_000 + index, -20, 20, $"trace:outside:{index}")));
        DesignScene large = new(
            small.Identity,
            small.Document with { Bounds = new(new(-1000, -1000), new(200_000, 1000)) },
            small.Query with { MaximumObjects = 200_000 },
            small.Coverage,
            small.Data with
            {
                Copper = many,
                Nets = small.Data.Nets.Add(new(new("net:unrelated"), "UNRELATED", 100_001)),
            });
        WorkspaceDocumentIdentity document = new("session", 1, 1, 123, "fixture.brd", "PD_V25");
        foreach (DesignScene board in new[] { small, large })
        {
            var store = new FakeStore(board);
            var gateway = new FakeGateway(store);
            DpViaCorridorCaptureResult result = await DpViaCorridorCaptureRunner.RunAsync(
                gateway, new DiscardDiagnostics(), () => document, "test", "test",
                new(0, null, false), false, CancellationToken.None);
            Require(gateway.CaptureCalls == 1 && store.DisposeCalls == 1 &&
                    result.Run.Scan.Findings.Count == 1,
                "The ordinary corridor run did not use one sealed capture and release its store on both board sizes.");
            Require(store.Requests[0].Query.CopperKinds.Length == 0 &&
                    !store.Requests[0].Query.Families.Contains(DataFamily.Copper) &&
                    store.Requests.Skip(1).All(request => request.Query.Region is not null),
                "The ordinary run materialized whole-board copper before bounded replay.");
        }

        var partial = new FakeStore(small, makeFirstBatchPartial: true);
        await RequireThrowsAsync<LargeBoardCorridorIncompleteException>(() => RunCaptured(partial));
        Require(partial.DisposeCalls == 1, "An incomplete ordinary run retained its sealed store.");

        var corrupt = new FakeStore(small, replayFailure: new InvalidDataException("corrupt page"));
        await RequireThrowsAsync<LargeBoardCaptureException>(() => RunCaptured(corrupt));
        Require(corrupt.DisposeCalls == 1, "A corrupt ordinary replay retained its sealed store.");

        var resourceDiagnostics = new RecordingDiagnostics();
        var exhausted = new FakeStore(small, replayFailure: new EngineBulkReplayResourceException(new(
            "records_read", 100, 101, "page_decode", CleanupComplete: true, PartialDataWithheld: true)));
        await RequireThrowsAsync<LargeBoardCaptureException>(() => DpViaCorridorCaptureRunner.RunAsync(
            new FakeGateway(exhausted), resourceDiagnostics, () => document, "test", "test",
            new(0, null, false), false, CancellationToken.None));
        Require(resourceDiagnostics.Receipts.Last() is
            {
                ExhaustedResource: "records_read", ConfiguredLimit: 100, ObservedCount: 101,
                CleanupComplete: true, PartialDataWithheld: true, ReplayBudgets: not null,
            }, "The ordinary terminal receipt lost the structured replay failure or cleanup evidence.");

        using var cancellation = new CancellationTokenSource();
        var cancellationDiagnostics = new RecordingDiagnostics();
        var cancelled = new FakeStore(small)
        {
            AfterReplay = count =>
            {
                if (count == 4)
                {
                    cancellation.Cancel();
                }
            },
        };
        await RequireThrowsAsync<OperationCanceledException>(() => DpViaCorridorCaptureRunner.RunAsync(
            new FakeGateway(cancelled), cancellationDiagnostics, () => document, "test", "test",
            new(0, null, false), false, cancellation.Token));
        Require(cancelled.DisposeCalls == 1 && cancelled.Requests.Count == 4,
            "A cancelled ordinary run published or retained its store after a batch was already accepted.");
        Require(cancellationDiagnostics.Receipts.Last() is
            {
                Feature: "dp-via-corridor-run",
                TerminalState: AcquisitionTerminalState.Cancelled,
                CleanupComplete: true,
                PartialDataWithheld: true,
            }, "Cancelled ordinary screening omitted its structured cleanup/no-partial receipt.");

        var stale = new FakeStore(small)
        {
            AfterReplay = _ => document = document with { BoardGeneration = 2 },
        };
        await RequireThrowsAsync<LargeBoardCaptureStaleException>(() => RunCaptured(stale));
        Require(stale.DisposeCalls == 1, "A stale ordinary result retained its sealed store.");

        document = document with { BoardGeneration = 1 };
        var cleanupDiagnostics = new RecordingDiagnostics();
        var cleanupFailure = new FakeStore(small)
        {
            DisposeFailure = new IOException("store cleanup failed"),
        };
        await RequireThrowsAsync<IOException>(() => DpViaCorridorCaptureRunner.RunAsync(
            new FakeGateway(cleanupFailure), cleanupDiagnostics, () => document, "test", "test",
            new(0, null, false), false, CancellationToken.None));
        Require(cleanupDiagnostics.Receipts.Last() is
            {
                TerminalState: AcquisitionTerminalState.Failed,
                CleanupComplete: false,
                PartialDataWithheld: true,
            }, "Store cleanup failure was published as a successful ordinary run.");

        Task<DpViaCorridorCaptureResult> RunCaptured(
            FakeStore store,
            CancellationToken token = default) => DpViaCorridorCaptureRunner.RunAsync(
                new FakeGateway(store), new DiscardDiagnostics(), () => document, "test", "test",
                new(0, null, false), false, token);
    }

    private static async Task CheckDenseAggressorSubdivisionAsync()
    {
        DesignScene fixture = Fixture();
        ImmutableArray<CopperObject> copper = fixture.Data.Copper.Take(2)
            .Concat(Enumerable.Range(0, 100_001).Select(index =>
                Segment("CLOCK_TEST", index % 2 == 0 ? -30 : 30, 40, 50, $"trace:dense:{index}")))
            .Append(Segment("CLOCK_TEST", 0, -200, 200, "trace:long-boundary"))
            .Append(Segment(null, 10, -200, 200, "trace:disconnected-boundary"))
            .ToImmutableArray();
        DesignScene full = new(fixture.Identity, fixture.Document,
            fixture.Query with { MaximumObjects = 200_000 }, fixture.Coverage,
            fixture.Data with { Copper = copper });
        var store = new FakeStore(full);
        LargeBoardCorridorRunResult result = await LargeBoardCorridorRunner.RunAsync(
            store, new(0, null, false), new(), new(new(), new()));
        CorridorScan direct = CorridorAnalyzer.Analyze(full, new(0, null, false));
        Require(result.Counts.BatchSubdivisionCount > 0 &&
                result.Counts.BatchReplayCount > result.Counts.CompletedBatches &&
                result.Counts.MaximumBatchMaterializedObjects <= 100_000,
            "A dense aggressor batch did not split into bounded sealed replays.");
        Require(result.Scan.Findings.Select(item => item.Finding).SequenceEqual(direct.Findings.Select(item => item with
                {
                    PositiveViaIndex = 0,
                    NegativeViaIndex = 1,
                    AggressorIndex = 2,
                })) &&
                result.Scan.Findings.Zip(direct.Findings).All(pair =>
                    pair.First.Witnesses.PositiveVia.Id == full.Data.Copper[pair.Second.PositiveViaIndex].Id &&
                    pair.First.Witnesses.NegativeVia.Id == full.Data.Copper[pair.Second.NegativeViaIndex].Id &&
                    pair.First.Witnesses.Aggressor.Id == full.Data.Copper[pair.Second.AggressorIndex].Id),
            "Aggressor partitions changed direct-path findings, ordering, or global seen semantics.");
        Require(result.Scan.Findings.Count == 2 && result.Scan.Findings.Any(item =>
                item.Witnesses.Aggressor.NetName is null) &&
                result.Scan.Findings.Count(item => item.Witnesses.AggressorIdentity.SourceIdentity ==
                    "source/trace:long-boundary") == 1,
            "A long boundary object or disconnected crossing was omitted or duplicated.");
        ImmutableArray<CopperObject> moved = copper.Take(copper.Length - 2)
            .Append(Segment("CLOCK_TEST", 0, 100, 120, "trace:long-boundary"))
            .Append(Segment(null, 10, 100, 120, "trace:disconnected-boundary"))
            .ToImmutableArray();
        LargeBoardCorridorRunResult negative = await LargeBoardCorridorRunner.RunAsync(
            new FakeStore(fixture, capturedCopper: moved), new(0, null, false), new(), new(new(), new()));
        Require(negative.Scan.Findings.Count == 0,
            "Moved nonintersecting obstacles survived dense aggressor partitioning.");
    }

    private static async Task CheckAggressorReadBudgetSubdivisionAsync()
    {
        DesignScene fixture = Fixture();
        ImmutableArray<CopperObject> spread = fixture.Data.Copper.Take(2)
            .Concat(Enumerable.Range(0, 18).Select(index =>
                Segment("CLOCK_TEST", index % 2 == 0 ? -30 : 30, 40, 50, $"trace:read-budget:{index}")))
            .ToImmutableArray();
        foreach (string resource in new[] { "pages_read", "records_read" })
        {
            var store = new FakeStore(fixture, capturedCopper: spread) { SimulatedRecordsPerPage = 6 };
            var budgets = new LargeBoardReplayBudgets
            {
                MaximumPagesRead = resource == "pages_read" ? 2 : 100,
                MaximumRecordsRead = resource == "records_read" ? 22 : 1000,
            };
            LargeBoardCorridorRunResult result = await LargeBoardCorridorRunner.RunAsync(
                store, new(0, null, false), new(), new(new(), budgets));
            Require(result.Counts.BatchSubdivisionCount == 1 && result.Counts.CompletedBatches == 2,
                $"Structured {resource} exhaustion did not partition the aggressor replay.");
        }
        ImmutableArray<CopperObject> coincident = fixture.Data.Copper.Take(2)
            .Concat(Enumerable.Range(0, 9).Select(index =>
                Segment("CLOCK_TEST", 0, -20, 20, $"trace:coincident:{index}")))
            .ToImmutableArray();
        var dense = new FakeStore(fixture, capturedCopper: coincident);
        LargeBoardCorridorReplayException failure = await RequireThrowsAsync<LargeBoardCorridorReplayException>(() =>
            LargeBoardCorridorRunner.RunAsync(dense, new(0, null, false), new(),
                new(new(), new LargeBoardReplayBudgets { MaximumMaterializedObjects = 8 })));
        Require(failure.Stage.StartsWith("bounded-batch-", StringComparison.Ordinal) &&
                failure.Stage.EndsWith("-minimum-tile", StringComparison.Ordinal) &&
                failure.InnerException is EngineBulkReplayResourceException
                    { Evidence.Resource: "materialized_objects", Evidence.PartialDataWithheld: true },
            "An unsplittable aggressor tile lost its structured no-partial resource failure.");
        using var cancellation = new CancellationTokenSource();
        var cancelled = new FakeStore(fixture, capturedCopper: spread)
        {
            SimulatedRecordsPerPage = 6,
            AfterReplay = count =>
            {
                if (count >= 5)
                {
                    cancellation.Cancel();
                }
            },
        };
        await RequireThrowsAsync<OperationCanceledException>(() => LargeBoardCorridorRunner.RunAsync(
            cancelled, new(0, null, false), new(),
            new(new(), new LargeBoardReplayBudgets { MaximumPagesRead = 2 }), cancellation.Token));
        Require(cancelled.Requests.Count == 5,
            "Cancellation after an aggressor partition continued into other leaves.");
    }

    private static async Task CheckPagedPlanningBeyondSceneLimitAsync()
    {
        DesignScene fixture = Fixture();
        DesignScene metadata = new(
            fixture.Identity,
            fixture.Document with { Bounds = new(new(-1_000, -1_000), new(24_000, 1_000)) },
            fixture.Query, fixture.Coverage, fixture.Data with { Copper = [] });
        CopperObject prototype = Via("BUS_P", 0, "prototype");
        var copper = ImmutableArray.CreateBuilder<CopperObject>();
        for (int index = 0; index < 200_000; index++)
        {
            decimal x = index / 40_000 * 5_000;
            copper.Add(prototype with
            {
                Id = new($"via:renamed-subject:{index}"),
                Bounds = new(new(x - 6, -6), new(x + 6, 6)),
                Via = prototype.Via! with { Position = new(x, 0) },
            });
        }
        copper.Add(Via("BUS_P", 4_000, "via:tile-edge"));
        copper.Add(Via("BUS_N", 50, "via:negative"));
        copper.Add(Segment("CLOCK_TEST", 25, -20, 20, "trace:renamed-crossing"));
        var store = new FakeStore(metadata, capturedCopper: copper.ToImmutable());
        var budgets = new LargeBoardReplayBudgets { MaximumMaterializedObjects = 200_000 };
        LargeBoardCorridorRunResult result = await LargeBoardCorridorRunner.RunAsync(
            store, new(0, null, false), new(), new(budgets, new()));

        Require(result.Counts.RetainedSubjectViaCount == 200_002 &&
                result.Counts.GlobalMaterializedObjects == 200_002,
            "Planning lost subject vias when their aggregate exceeded the Engine scene limit.");
        Require(result.Counts.PlanningViaReplayCount == 5 &&
                result.Counts.MaximumPlanningReplayMaterializedObjects < 50_000 &&
                result.Counts.PlanningSubdivisionCount == 0,
            "Large planning did not remain bounded per sealed replay.");
        Require(result.Counts.GlobalRecordsSelected == 200_003,
            "A via spanning the closed tile boundary was omitted or its repeated read was not observed.");
        Require(result.Scan.Findings.Count == 1 &&
                result.Scan.Findings[0].Witnesses.PositiveIdentity.CaptureOrdinal == 39_999 &&
                result.Scan.Findings[0].Witnesses.AggressorIdentity.SourceIdentity == "source/trace:renamed-crossing",
            "Tile order or boundary deduplication changed global capture-order pairing.");
        Require(store.Requests.Where(request => request.Query.Region is null)
                .All(request => !request.Query.Families.Contains(DataFamily.Copper)),
            "Large planning attempted a whole-board copper materialization before bounded replay.");
    }

    private static async Task CheckPlanningSubdivisionAndMinimumTileAsync()
    {
        DesignScene fixture = Fixture();
        ImmutableArray<CopperObject> spread = Enumerable.Range(0, 9)
            .Select(index => Via("BUS_P", -800 + index * 200, $"via:spread:{index}"))
            .Append(Via("BUS_N", 900, "via:spread:n"))
            .ToImmutableArray();
        var store = new FakeStore(fixture, capturedCopper: spread);
        var budgets = new LargeBoardReplayBudgets { MaximumMaterializedObjects = 8 };
        LargeBoardCorridorRunResult result = await LargeBoardCorridorRunner.RunAsync(
            store, new(0, null, false), new(), new(budgets, new()));
        Require(result.Counts.PlanningSubdivisionCount == 1 &&
                result.Counts.PlanningViaReplayCount == 2 &&
                result.Counts.MaximumPlanningReplayMaterializedObjects <= 8 &&
                result.Counts.RetainedSubjectViaCount == 10,
            "A structured materialization limit did not subdivide planning while retaining edge vias.");

        DesignScene tiny = new(
            fixture.Identity,
            fixture.Document with { Bounds = new(new(-0.01m, -0.01m), new(0.01m, 0.01m)) },
            fixture.Query, fixture.Coverage, fixture.Data with { Copper = [] });
        ImmutableArray<CopperObject> coincident = Enumerable.Range(0, 9)
            .Select(index => Via("BUS_P", 0, $"via:coincident:{index}"))
            .ToImmutableArray();
        var dense = new FakeStore(tiny, capturedCopper: coincident);
        LargeBoardCorridorReplayException failure =
            await RequireThrowsAsync<LargeBoardCorridorReplayException>(() =>
                LargeBoardCorridorRunner.RunAsync(dense, new(0, null, false), new(), new(budgets, new())));
        Require(failure.Stage == "bounded-via-planning-minimum-tile" &&
                failure.InnerException is EngineBulkReplayResourceException
                {
                    Evidence.Resource: "materialized_objects",
                    Evidence.Limit: 8,
                    Evidence.Observed: 9,
                    Evidence.PartialDataWithheld: true,
                }, "A pathological dense tile lost structured resource/no-partial evidence.");
        Require(dense.Requests.Count < 100 &&
                failure.Query.Region is { Width: <= 0.0001m, Height: <= 0.0001m },
            "Planning subdivision did not stop at the board's native coordinate quantum.");
    }

    private sealed class FakeGateway(FakeStore store) : ILargeBoardCaptureGateway<DesignScene>
    {
        public int CaptureCalls { get; private set; }

        public ValueTask<ILargeBoardCaptureStore<DesignScene>> CaptureAsync(
            LargeBoardCaptureBudgets budgets,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCalls++;
            Require(budgets.Families.Contains(DataFamily.Connectivity) &&
                    budgets.CopperKinds.SequenceEqual(new[] { CopperKind.Trace, CopperKind.Via, CopperKind.Shape }),
                "The ordinary sealed capture omitted required pair metadata or geometry.");
            return ValueTask.FromResult<ILargeBoardCaptureStore<DesignScene>>(store);
        }
    }

    private sealed class DiscardDiagnostics : IAcquisitionDiagnosticOwner
    {
        public ValueTask RetainAsync(
            AcquisitionDiagnosticReceipt receipt,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class RecordingDiagnostics : IAcquisitionDiagnosticOwner
    {
        public List<AcquisitionDiagnosticReceipt> Receipts { get; } = [];

        public ValueTask RetainAsync(
            AcquisitionDiagnosticReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            Receipts.Add(receipt);
            return ValueTask.CompletedTask;
        }
    }

    private static async Task CheckReplayShapesBudgetsAndOrderAsync()
    {
        DesignScene completeBoard = Fixture();
        var store = new FakeStore(completeBoard);
        var globalBudgets = new LargeBoardReplayBudgets
        {
            MaximumMaterializedObjects = 321,
            MaximumPagesRead = 456,
            MaximumRecordsRead = 789,
        };
        var boundedBudgets = new LargeBoardReplayBudgets
        {
            MaximumMaterializedObjects = 6_789,
            MaximumPagesRead = 23,
            MaximumRecordsRead = 45,
        };
        var batchOptions = new CorridorBatchOptions(
            MaximumWidthMils: 5_000,
            MaximumHeightMils: 5_000,
            MaximumMaterializedObjects: 12_345);

        LargeBoardCorridorRunResult result = await LargeBoardCorridorRunner.RunAsync(
            store,
            new CorridorOptions(0, null, false),
            batchOptions,
            new(globalBudgets, boundedBudgets));

        Require(store.Requests.Count == 4,
            "The runner did not issue metadata, a bounded via tile, then two layer batches.");
        ReplayRequest global = store.Requests[0];
        Require(global.Query.Kind == SceneReadKind.CompleteBoard &&
                global.Query.Families.SequenceEqual(new[]
                {
                    DataFamily.Nets,
                    DataFamily.Modules,
                    DataFamily.Layers,
                    DataFamily.Connectivity,
                }) &&
                global.Query.CopperKinds.Length == 0 &&
                !global.Query.IncludeContours && global.Query.Region is null &&
                global.Query.Layers.Length == 0 && global.Query.Module is null,
            "The global planning replay was not metadata-only.");
        ReplayRequest viaTile = store.Requests[1];
        Require(viaTile.Query.Kind == SceneReadKind.RegionGeometry &&
                viaTile.Query.CopperKinds.SequenceEqual(new[] { CopperKind.Via }) &&
                viaTile.Query.ViaPadMeasurements.Mode ==
                    ViaPadMeasurementSelectionMode.MatchingNetNameSuffixes &&
                viaTile.Query.ViaPadMeasurements.NetNameSuffixes.SequenceEqual(
                    new[] { "_P", "_N" }) &&
                !viaTile.Query.IncludeContours && viaTile.Query.Region is not null &&
                viaTile.Query.Layers.Length == 0 && viaTile.Query.Module is null,
            "The planning tile was not bounded, via-only, and contour-free.");
        Require(global.Budgets == globalBudgets &&
                global.Query.MaximumObjects == globalBudgets.MaximumMaterializedObjects,
            "The global replay did not preserve its explicit materialization and read budgets.");

        ReplayRequest[] batches = store.Requests.Skip(2).ToArray();
        Require(batches.Select(request => request.Query.Layers.Single().Value)
                .SequenceEqual(new[] { "ETCH/S03", "ETCH/S04" }) &&
                batches.All(request => request.Query.Kind == SceneReadKind.RegionGeometry &&
                    request.Query.Region is not null &&
                    request.Query.CopperKinds.SequenceEqual(new[]
                    {
                        CopperKind.Trace,
                        CopperKind.Via,
                        CopperKind.Shape,
                    }) &&
                    request.Query.ViaPadMeasurements.NetNameSuffixes.SequenceEqual(new[] { "_P", "_N" }) &&
                    !request.Query.IncludeContours),
            "Bounded layer batches were replayed in the wrong order or with a widened shape.");
        Require(batches.All(request =>
                request.Query.MaximumObjects ==
                    boundedBudgets.MaximumMaterializedObjects &&
                request.Budgets.MaximumMaterializedObjects ==
                    boundedBudgets.MaximumMaterializedObjects &&
                request.Budgets.MaximumPagesRead == boundedBudgets.MaximumPagesRead &&
                request.Budgets.MaximumRecordsRead == boundedBudgets.MaximumRecordsRead),
            "A batch did not retain the stricter materialization cap and caller read budgets.");
        Require(result.Counts.PlannedBatches == 2 &&
                result.Counts.CompletedBatches == 2 &&
                result.Counts.GlobalRecordsSelected == 2 &&
                result.Counts.BatchRecordsSelected == 5 &&
                result.Counts.GlobalMaterializedObjects == 2 &&
                result.Counts.MaximumBatchMaterializedObjects == 3 &&
                result.Counts.MaximumPlanningReplayMaterializedObjects == 2 &&
                result.Counts.PlanningViaReplayCount == 1 &&
                result.ReplayBudgets == new LargeBoardCorridorReplayBudgets(globalBudgets, boundedBudgets),
            "Replay phase counts were not returned from the accepted payloads.");
        Require(result.CaptureIdentity == store.Info.Identity,
            "The runner dropped the capture identity required for publication fencing.");
        Require(result.Scan.Findings.Count == 1 &&
                result.Scan.Findings[0].Witnesses.AggressorIdentity.SourceIdentity ==
                    "source/trace:a",
            "Capture-scoped replay identities did not reach the typed corridor result.");
        Require(result.Timings.TotalMilliseconds >=
                result.Timings.GlobalViaReplayMilliseconds,
            "Phase timing metadata was internally inconsistent.");
    }

    private static async Task CheckPartialReplayIsRejectedAsync()
    {
        var store = new FakeStore(Fixture(), makeFirstBatchPartial: true);
        LargeBoardCorridorIncompleteException failure =
            await RequireThrowsAsync<LargeBoardCorridorIncompleteException>(() =>
                LargeBoardCorridorRunner.RunAsync(
                    store,
                    new CorridorOptions(0, null, false),
                    new CorridorBatchOptions(MaximumMaterializedObjects: 4_000),
                    new(
                        new LargeBoardReplayBudgets
                        {
                            MaximumMaterializedObjects = 4_000,
                        },
                        new LargeBoardReplayBudgets
                        {
                            MaximumMaterializedObjects = 4_000,
                        })));

        Require(failure.Message.Contains("blocking coverage gaps", StringComparison.Ordinal) &&
                store.Requests.Count == 4,
            "A partial batch was published as a scan or bypassed complete replay ordering.");
    }

    private static async Task CheckKnownViaMetadataIsAdvisoryAsync()
    {
        var store = new FakeStore(Fixture(), makeViaMetadataAdvisory: true);
        LargeBoardCorridorRunResult result = await LargeBoardCorridorRunner.RunAsync(
            store,
            new CorridorOptions(0, null, false),
            new CorridorBatchOptions(MaximumMaterializedObjects: 4_000),
            new(
                new LargeBoardReplayBudgets { MaximumMaterializedObjects = 4_000 },
                new LargeBoardReplayBudgets { MaximumMaterializedObjects = 4_000 }));

        Require(store.Requests.Count == 4,
            "Advisory via metadata gaps changed the bounded replay plan.");
        Require(result.Scan.HasCompleteInputs &&
                result.Scan.BlockingCoverageWarnings.Count == 0,
            "Known via metadata gaps were treated as blocking coverage loss.");
        Require(result.Scan.CoverageWarnings.Any(message =>
                message.Contains("original via layers are screened conservatively", StringComparison.Ordinal)),
            "The accepted conservative fallback was not exposed for review.");
        Require(result.Scan.Findings.Count == 1,
            "The advisory fallback discarded a complete geometric finding.");
    }

    private static async Task CheckCancellationBeforeReplayAsync()
    {
        var store = new FakeStore(Fixture());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await RequireThrowsAsync<OperationCanceledException>(() =>
            LargeBoardCorridorRunner.RunAsync(
                store,
                new CorridorOptions(0, null, false),
                new CorridorBatchOptions(),
                new(new LargeBoardReplayBudgets(), new LargeBoardReplayBudgets()),
                cancellation.Token));
        Require(store.Requests.Count == 0,
            "A cancelled run started replay work.");
    }

    private static async Task CheckStructuredReplayFailureIsAttributedAsync()
    {
        var resourceFailure = new EngineBulkReplayResourceException(new(
            "materialized_objects",
            321,
            322,
            "record-selection",
            CleanupComplete: true,
            PartialDataWithheld: true));
        var store = new FakeStore(Fixture(), replayFailure: resourceFailure);
        var globalBudgets = new LargeBoardReplayBudgets
        {
            MaximumMaterializedObjects = 321,
        };
        LargeBoardCorridorReplayException failure =
            await RequireThrowsAsync<LargeBoardCorridorReplayException>(() =>
                LargeBoardCorridorRunner.RunAsync(
                    store,
                    new CorridorOptions(0, null, false),
                    new CorridorBatchOptions(),
                    new(globalBudgets, new LargeBoardReplayBudgets())));

        Require(failure.Stage == "global-planning-metadata" &&
                failure.Query.Kind == SceneReadKind.CompleteBoard &&
                failure.Budgets == globalBudgets &&
                ReferenceEquals(failure.InnerException, resourceFailure),
            "A replay failure lost its exact stage, query, budget, or structured cause.");
    }

    private static async Task<int> CheckCompleteReportAsync()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "pd-large-corridor-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FakeStore(Fixture());
            LargeBoardCorridorRunResult result = await LargeBoardCorridorRunner.RunAsync(
                store,
                new CorridorOptions(0, null, false),
                new CorridorBatchOptions(MaximumMaterializedObjects: 4_000),
                new(
                    new LargeBoardReplayBudgets { MaximumMaterializedObjects = 4_000 },
                    new LargeBoardReplayBudgets { MaximumMaterializedObjects = 4_000 }));
            WorkspaceDocumentIdentity document = new("session", 1, 1, 123, "fixture.brd", "PD_V25");
            bool published = false;
            LargeBoardCorridorReport report = await LargeBoardCorridorReportWriter.PublishAsync(
                result.Scan,
                "fixture.brd",
                directory,
                result.CaptureIdentity,
                document,
                () => document,
                completed => published = File.Exists(completed.ReportPath) && File.Exists(completed.DataPath));
            string text = await File.ReadAllTextAsync(report.ReportPath);
            string data = await File.ReadAllTextAsync(report.DataPath);
            Require(published && File.Exists(report.ReportPath) && File.Exists(report.DataPath) &&
                    text.Contains("findings: 1", StringComparison.Ordinal) &&
                    text.Contains("crossing-1", StringComparison.Ordinal) &&
                    data.Contains("\"Schema\":\"pd-scalable-corridor-v1\"", StringComparison.Ordinal) &&
                    data.Contains("\"Findings\":[", StringComparison.Ordinal),
                "The scalable report did not retain the complete typed result in both artifacts.");
            return await LargeBoardCorridorReportChecks.RunAsync(result.Scan, result.CaptureIdentity);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static DesignScene Fixture()
    {
        SceneQuery query = CorridorAnalyzer.CreateSceneQuery();
        ImmutableArray<LayerObject> layers =
        [
            new(new("ETCH/TOP"), 0, false),
            new(new("ETCH/S03"), 1, false),
            new(new("ETCH/S04"), 2, false),
            new(new("ETCH/BOTTOM"), 3, false),
        ];
        var data = new SceneData
        {
            Nets =
            [
                new(new("net:p"), "BUS_P", 1),
                new(new("net:n"), "BUS_N", 1),
                new(new("net:a"), "CLOCK_TEST", 1),
            ],
            Layers = layers,
            Modules = [],
            Copper =
            [
                Via("BUS_P", -50, "via:p"),
                Via("BUS_N", 50, "via:n"),
                Segment("CLOCK_TEST", 0, -20, 20, "trace:a"),
            ],
            CopperScope = new([CopperKind.Trace, CopperKind.Via, CopperKind.Shape]),
        };
        return new(
            new(Guid.NewGuid(), DateTimeOffset.UtcNow,
                new("test-engine", "1", false, "large-board-runner-fixture")),
            new(DocumentKind.PcbBoard, "fixture.brd", "mils", 4,
                new(new(-1_000, -1_000), new(1_000, 1_000))),
            query,
            Coverage(query, complete: true),
            data);
    }

    private static CopperObject Via(string net, decimal x, string id)
    {
        DesignPoint position = new(x, 0);
        ImmutableArray<LayerId> layers =
        [
            new("ETCH/TOP"),
            new("ETCH/S03"),
            new("ETCH/S04"),
            new("ETCH/BOTTOM"),
        ];
        ImmutableArray<ViaPadMeasurement> pads =
        [
            Pad("ETCH/S03", "regular", 12),
            Pad("ETCH/S03", "antipad", 20),
            Pad("ETCH/S04", "regular", 12),
            Pad("ETCH/S04", "antipad", 20),
        ];
        var via = new ViaSpan(
            "PAD_SAMPLE",
            position,
            layers,
            layers,
            "not_started",
            true,
            new(true, null, null, null, null, null, pads));
        return new(new(id), CopperKind.Via, net, null,
            new(new(x - 6, -6), new(x + 6, 6)), null, null, via, [], null, []);
    }

    private static ViaPadMeasurement Pad(string layer, string usage, decimal diameter) =>
        new(new(layer), new(layer), usage, "CIRCLE", new(diameter), new(diameter));

    private static CopperObject Segment(
        string? net,
        decimal x,
        decimal startY,
        decimal endY,
        string id)
    {
        DesignPoint start = new(x, startY);
        DesignPoint end = new(x, endY);
        return new(
            new(id),
            CopperKind.Trace,
            net,
            new("ETCH/S03"),
            new(new(x - 2, startY - 2), new(x + 2, endY + 2)),
            new LineGeometry(start, end),
            new Length(4),
            null,
            [],
            null,
            []);
    }

    private static CoverageReport Coverage(
        SceneQuery query,
        bool complete,
        params string[] reasons) =>
        new(query.Families.Select(family => new FamilyCoverage(
            family,
            DataAvailability.Available,
            complete || family != DataFamily.Copper
                ? DataCompleteness.CompleteForRequestedScope
                : DataCompleteness.Partial,
            Reasons: family == DataFamily.Copper ? reasons.ToImmutableArray() : [])));

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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "Large-board corridor runner check failed: " + message);
        }
    }

    private sealed record ReplayRequest(
        SceneQuery Query,
        LargeBoardReplayBudgets Budgets);

    private sealed class FakeBatchStore(FakeStore inner)
        : IBatchedLargeBoardCaptureStore<DesignScene>
    {
        public LargeBoardCaptureStoreInfo Info => inner.Info;
        public int BatchCalls { get; private set; }
        public List<SceneQuery[]> BatchQueries { get; } = [];
        public Exception? BatchFailure { get; init; }
        public bool ReverseResults { get; init; }
        public Action<int>? AfterBatchQuery { get; init; }
        public int RootIndexPasses { get; init; } = 1;

        public ValueTask<LargeBoardReplayPayload<DesignScene>> ReplayAsync(
            SceneQuery query,
            LargeBoardReplayBudgets budgets,
            CancellationToken cancellationToken) =>
            inner.ReplayAsync(query, budgets, cancellationToken);

        public async ValueTask<LargeBoardReplayBatchPayload<DesignScene>> ReplayBatchAsync(
            IReadOnlyList<SceneQuery> queries,
            LargeBoardReplayBudgets budgets,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BatchCalls++;
            BatchQueries.Add(queries.ToArray());
            if (BatchFailure is not null)
            {
                throw BatchFailure;
            }
            var results = ImmutableArray.CreateBuilder<LargeBoardReplayPayload<DesignScene>>(
                queries.Count);
            for (int index = 0; index < queries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await inner.ReplayAsync(
                    queries[index], budgets, cancellationToken));
                AfterBatchQuery?.Invoke(index);
            }
            ImmutableArray<LargeBoardReplayPayload<DesignScene>> ordered = results.ToImmutable();
            if (ReverseResults)
            {
                ordered = ordered.Reverse().ToImmutableArray();
            }
            return new(
                ordered,
                RootIndexPasses: RootIndexPasses,
                UniquePagesRead: 2,
                UniqueRecordsRead: 20,
                RootQueryMemberships: ordered.Sum(static result => result.SpatialRootsSelected),
                PageQueryMemberships: ordered.Sum(static result => result.PagesRead),
                LogicalPageMembershipBytes: 4_096,
                ManifestValidationMilliseconds: 1,
                RootIndexSelectionMilliseconds: 1,
                PageReadAndDecodeMilliseconds: 1,
                SceneMaterializationMilliseconds: 1);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class FakeStore : ILargeBoardCaptureStore<DesignScene>
    {
        private readonly DesignScene _completeBoard;
        private readonly ImmutableArray<CopperObject> _capturedCopper;
        private readonly bool _makeFirstBatchPartial;
        private readonly bool _makeViaMetadataAdvisory;
        private readonly Exception? _replayFailure;
        private readonly Dictionary<SceneObjectId, long> _ordinals;
        private int _batchCount;

        public FakeStore(
            DesignScene completeBoard,
            bool makeFirstBatchPartial = false,
            bool makeViaMetadataAdvisory = false,
            Exception? replayFailure = null,
            ImmutableArray<CopperObject>? capturedCopper = null,
            CorridorSourceTraversal? sourceTraversal = null)
        {
            _completeBoard = completeBoard;
            _capturedCopper = capturedCopper ?? completeBoard.Data.Copper;
            _makeFirstBatchPartial = makeFirstBatchPartial;
            _makeViaMetadataAdvisory = makeViaMetadataAdvisory;
            _replayFailure = replayFailure;
            _ordinals = _capturedCopper
                .Select((item, index) => (item.Id, Ordinal: (long)index))
                .ToDictionary(item => item.Id, item => item.Ordinal);
            Info = new(
                new("capture-token-runner", "session", 1, 1, 123,
                    "fixture.brd", "PD_V25"),
                true,
                3,
                _capturedCopper.Length,
                4_096,
                [],
                [])
            {
                SourceTraversal = sourceTraversal,
            };
        }

        public LargeBoardCaptureStoreInfo Info { get; }
        public List<ReplayRequest> Requests { get; } = [];
        public int DisposeCalls { get; private set; }
        public Action<int>? AfterReplay { get; init; }
        public Exception? DisposeFailure { get; init; }
        public int? SimulatedRecordsPerPage { get; init; }
        public Func<long, long?>? SourceOrdinal { get; init; }
        public Func<long, long>? CaptureOrdinal { get; init; }

        public ValueTask<LargeBoardReplayPayload<DesignScene>> ReplayAsync(
            SceneQuery query,
            LargeBoardReplayBudgets budgets,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(query, budgets));
            if (_replayFailure is not null)
            {
                throw _replayFailure;
            }
            bool isGlobal = query.Kind == SceneReadKind.CompleteBoard;
            ImmutableArray<CopperObject> copper = !query.Families.Contains(DataFamily.Copper)
                ? []
                : _capturedCopper.Where(item =>
                    query.CopperKinds.Contains(item.Kind) &&
                    (query.Region is null || item.Bounds.Intersects(query.Region.Value)) &&
                    (item.Kind == CopperKind.Via ||
                        query.Layers.Length == 0 ||
                        item.Layer is { } layer && query.Layers.Contains(layer)))
                    .ToImmutableArray();
            if (copper.Length > budgets.MaximumMaterializedObjects)
            {
                throw new EngineBulkReplayResourceException(new(
                    "materialized_objects", budgets.MaximumMaterializedObjects, copper.Length,
                    "record-selection", CleanupComplete: true, PartialDataWithheld: true));
            }
            if (query.CopperKinds.Contains(CopperKind.Trace) && SimulatedRecordsPerPage is { } pageSize)
            {
                int pagesRead = (copper.Length + pageSize - 1) / pageSize;
                if (pagesRead > budgets.MaximumPagesRead)
                {
                    throw new EngineBulkReplayResourceException(new(
                        "pages_read", budgets.MaximumPagesRead, pagesRead,
                        "page_selection", CleanupComplete: true, PartialDataWithheld: true));
                }
                if (copper.Length + 10 > budgets.MaximumRecordsRead)
                {
                    throw new EngineBulkReplayResourceException(new(
                        "records_read", budgets.MaximumRecordsRead, copper.Length + 10,
                        "page_read", CleanupComplete: true, PartialDataWithheld: true));
                }
            }
            bool partial = query.CopperKinds.Contains(CopperKind.Trace) &&
                _makeFirstBatchPartial && _batchCount++ == 0;
            var data = new SceneData
            {
                Nets = query.Families.Contains(DataFamily.Nets) ? _completeBoard.Data.Nets : [],
                Modules = query.Families.Contains(DataFamily.Modules) ? _completeBoard.Data.Modules : [],
                Layers = query.Families.Contains(DataFamily.Layers) ? _completeBoard.Data.Layers : [],
                Xnets = query.Families.Contains(DataFamily.Connectivity) ? _completeBoard.Data.Xnets : [],
                DifferentialPairs = query.Families.Contains(DataFamily.Connectivity) ? _completeBoard.Data.DifferentialPairs : [],
                Copper = copper,
                CopperScope = _makeViaMetadataAdvisory
                    ? ViaMetadataAdvisoryScope(query.CopperKinds)
                    : new(query.CopperKinds),
            };
            CoverageReport coverage = partial
                ? Coverage(query, complete: false, "bounded_out")
                : _makeViaMetadataAdvisory
                    ? Coverage(
                        query,
                        complete: false,
                        "via:active_via_layers",
                        "via:backdrill")
                : Coverage(query, complete: true);
            DesignScene scene = new(
                new(Guid.NewGuid(), DateTimeOffset.UtcNow,
                    new("test-engine", "1", false, "fake-bulk-replay")),
                _completeBoard.Document with
                {
                    Bounds = query.Region ?? _completeBoard.Document.Bounds,
                },
                query,
                coverage,
                data);
            ImmutableArray<LargeBoardReplayObjectIdentity> identities = copper.Select(item =>
                new LargeBoardReplayObjectIdentity(
                    item.Id,
                    CaptureOrdinal?.Invoke(_ordinals[item.Id]) ?? _ordinals[item.Id],
                    "source/" + item.Id.Value,
                    item.Kind)
                {
                    SourceTraversalOrdinal = SourceOrdinal?.Invoke(_ordinals[item.Id]),
                }).ToImmutableArray();
            int pages = isGlobal ? 1 : 2;
            AfterReplay?.Invoke(Requests.Count);
            return ValueTask.FromResult(new LargeBoardReplayPayload<DesignScene>(
                scene,
                pages + 1,
                pages,
                copper.Length + 10,
                copper.Length,
                copper.Length,
                identities));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
            return ValueTask.CompletedTask;
        }

        private static CopperReadScope ViaMetadataAdvisoryScope(
            ImmutableArray<CopperKind> requestedKinds) => new(
            requestedKinds.Where(kind => kind != CopperKind.Via).ToImmutableArray(),
            Enum.GetValues<CopperKind>().Select(kind =>
            {
                if (!requestedKinds.Contains(kind))
                {
                    return new CopperKindCoverage(
                        kind,
                        DataAvailability.NotRequested,
                        DataCompleteness.Partial,
                        GeometryFidelity.Unknown,
                        []);
                }
                return kind == CopperKind.Via
                    ? new CopperKindCoverage(
                        kind,
                        DataAvailability.Available,
                        DataCompleteness.Partial,
                        GeometryFidelity.BoundsOnly,
                        ["via:active_via_layers", "via:backdrill"])
                    : new CopperKindCoverage(
                        kind,
                        DataAvailability.Available,
                        DataCompleteness.CompleteForRequestedScope,
                        GeometryFidelity.BoundsOnly,
                        []);
            }).ToImmutableArray());
    }
}
