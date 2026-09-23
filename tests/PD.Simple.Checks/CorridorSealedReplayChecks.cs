using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
using PD.PcbTools;
using PD.Simple.Corridor;
using PD.Simple.LargeBoards;

internal static class CorridorSealedReplayChecks
{
    public static async Task<int> RunAsync()
    {
        // This authenticated fixture is exported by EngineBulkGate. PD only
        // consumes public Engine APIs and never constructs lower-layer records.
        string directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SelectiveCorridorCapture");
        await using EngineBulkCapture capture = await EngineBulkCapture.OpenAsync(directory);
        var gateway = new FixtureGateway(capture);
        DpViaCorridorCaptureResult result = await DpViaCorridorCaptureRunner.RunAsync(
            gateway, new DiscardDiagnostics(), () => gateway.Document,
            "test", "test", new(0, null, false), false, CancellationToken.None);
        Require(gateway.RejectedBroaderReplay,
            "The sealed Engine fixture did not reject the original All-via-pad replay regression.");
        Require(gateway.PlannedReplayEquivalent,
            "The capture-scoped replay plan changed the sealed fixture's ordered identities or root selection metrics.");
        Require(result.Run.Scan.Findings.Count == 1 && result.Run.Counts.CompletedBatches == 1,
            "The ordinary coordinator did not screen the selective sealed capture through real Engine replay.");
        Require(result.Run.Counts.PlanningViaReplayCount == 1 &&
                result.Run.Counts.MaximumPlanningReplayMaterializedObjects == 2,
            "The ordinary sealed run bypassed bounded via planning.");
        Require(result.Run.Scan.PlanningScene.Query.ViaPadMeasurements.NetNameSuffixes
                .SequenceEqual(new[] { "_P", "_N" }),
            "Planning did not retain the exact capture via-pad measurement selection.");
        Require(result.TerminalReceipt is
            { CleanupComplete: true, PartialDataWithheld: false, TerminalState: AcquisitionTerminalState.Complete },
            "The real sealed capture did not complete and release its store.");
        await RequireThrowsAsync<ObjectDisposedException>(async () =>
            await capture.ReplayAsync(gateway.CompatibleQuery));
        return 8 + CheckStagedCaptureContracts() + await CheckStagedAcquisitionTimingAsync();
    }

    private static int CheckStagedCaptureContracts()
    {
        const EngineBulkCaptureFields expectedFields =
            EngineBulkCaptureFields.Discovery |
            EngineBulkCaptureFields.ScalarGeometry |
            EngineBulkCaptureFields.PadMeasurements;
        Require(EngineBulkCaptureDemand.MaximumRegions == 8_192,
            "The staged scan demand no longer honors the 8192-region native budget.");
        var first = new DesignBounds(new(0, 0), new(10, 10));
        var second = new DesignBounds(new(20, 20), new(30, 30));
        var batches = new List<CorridorReplayBatch>
        {
            StagedBatch(1, first),
            StagedBatch(2, second),
            StagedBatch(3, first),
        };
        EngineBulkCaptureDemand? demand = DpViaCorridorCaptureRunner.BuildScanDemand(batches);
        Require(demand is not null,
            "The staged scan demand was not built from planned batch rectangles.");
        Require(demand!.Fields == expectedFields,
            "The staged scan demand omitted discovery, scalar geometry, or pad measurements.");
        Require(demand.Regions.Length == 2 &&
                demand.Regions[0].Bounds.Equals(first) &&
                demand.Regions[1].Bounds.Equals(second),
            "The staged scan demand changed the plan's exact batch rectangles or their order.");
        Require(demand.Regions.All(region => region.Fields == expectedFields),
            "A staged scan region narrowed the required detail fields.");
        Require(batches.Count == 3 && batches[0].Id == 1 && batches[2].Id == 3,
            "Building the staged scan demand mutated the plan batches.");
        Require(DpViaCorridorCaptureRunner.BuildScanDemand([]) is null,
            "An empty plan must skip stage B instead of issuing a demand.");
        var overflow = new List<CorridorReplayBatch>(EngineBulkCaptureDemand.MaximumRegions + 1);
        for (int index = 0; index <= EngineBulkCaptureDemand.MaximumRegions; index++)
        {
            overflow.Add(StagedBatch(index, new(new(index, 0), new(index + 1, 1))));
        }
        Require(DpViaCorridorCaptureRunner.BuildScanDemand(overflow) is null,
            "An over-budget plan must fall back to a full stage B capture, never a truncated demand.");

        CorridorFrozenSource frozen = StagedFrozen(ObservationA);
        LargeBoardCaptureStoreInfo planning = StagedStore("planning-token", frozen);
        LargeBoardCaptureStoreInfo scan = StagedStore("scan-token", frozen);
        Require(planning.SourceTraversal!.IsSupported && scan.SourceTraversal!.IsSupported,
            "The staged frozen-source fixture is not a supported traversal declaration.");
        DpViaCorridorCaptureRunner.RequireSameFrozenSource(planning, scan);
        RequireThrows<InvalidDataException>(
            () => DpViaCorridorCaptureRunner.RequireSameFrozenSource(
                planning, StagedStore("scan-token", StagedFrozen(ObservationB))),
            "Staged captures from different frozen sources were admitted.");
        RequireThrows<InvalidDataException>(
            () => DpViaCorridorCaptureRunner.RequireSameFrozenSource(
                planning, StagedStore("scan-token", null)),
            "A staged capture without frozen provenance was paired with a frozen plan.");
        RequireThrows<InvalidDataException>(
            () => DpViaCorridorCaptureRunner.RequireSameFrozenSource(
                StagedStore("planning-token", null), StagedStore("scan-token", null)),
            "Staged captures without verified frozen provenance were admitted.");
        CorridorFrozenSource unverified = StagedFrozen(ObservationA, verified: false);
        RequireThrows<InvalidDataException>(
            () => DpViaCorridorCaptureRunner.RequireSameFrozenSource(
                StagedStore("planning-token", unverified), StagedStore("scan-token", unverified)),
            "Staged captures with unverified frozen provenance were admitted.");
        LargeBoardCaptureStoreInfo reorderedScan = StagedStore("scan-token", frozen);
        CorridorSourceTraversal reorderedTraversal = reorderedScan.SourceTraversal! with { RootCount = 9 };
        reorderedScan = reorderedScan with
        {
            Identity = reorderedScan.Identity with { SourceTraversal = reorderedTraversal },
            SourceTraversal = reorderedTraversal,
        };
        RequireThrows<InvalidDataException>(
            () => DpViaCorridorCaptureRunner.RequireSameFrozenSource(planning, reorderedScan),
            "Staged captures with incompatible source ordering were admitted.");
        LargeBoardCaptureStoreInfo inconsistentScan = scan with
        {
            Identity = scan.Identity with { SourceTraversal = null },
        };
        RequireThrows<InvalidDataException>(
            () => DpViaCorridorCaptureRunner.RequireSameFrozenSource(planning, inconsistentScan),
            "A staged capture with inconsistent traversal declarations was admitted.");
        return 17;
    }

    private static async Task<int> CheckStagedAcquisitionTimingAsync()
    {
        AcquisitionDiagnosticReceipt failed = await RunFailingStagedAsync<LargeBoardCaptureException>(
            new InvalidOperationException("The staged observation failed."));
        Require(failed.TerminalState == AcquisitionTerminalState.Failed,
            "A failed staged observation did not retain a failed receipt.");
        Require(failed.CaptureElapsedMilliseconds > 0 &&
                failed.ReplayElapsedMilliseconds < failed.CaptureElapsedMilliseconds,
            "Time spent in a failed staged acquisition was misreported as replay.");
        Require(failed.PlanningCaptureTokenFingerprint is null &&
                failed.PlanningCapturePhaseTiming is null &&
                failed.ObservationStartupMilliseconds is null,
            "A failed staged observation invented planning capture evidence.");
        AcquisitionDiagnosticReceipt cancelled = await RunFailingStagedAsync<OperationCanceledException>(
            new OperationCanceledException("The staged observation was cancelled."));
        Require(cancelled.TerminalState == AcquisitionTerminalState.Cancelled,
            "A cancelled staged observation did not retain a cancelled receipt.");
        Require(cancelled.CaptureElapsedMilliseconds > 0 &&
                cancelled.ReplayElapsedMilliseconds < cancelled.CaptureElapsedMilliseconds,
            "Time spent in a cancelled staged acquisition was misreported as replay.");
        Require(cancelled.PlanningCaptureTokenFingerprint is null &&
                cancelled.PlanningCapturePhaseTiming is null &&
                cancelled.ObservationStartupMilliseconds is null,
            "A cancelled staged observation invented planning capture evidence.");
        return 6;
    }

    private static async Task<AcquisitionDiagnosticReceipt> RunFailingStagedAsync<TException>(Exception failure)
        where TException : Exception
    {
        var gateway = new FailingStagedGateway(failure);
        var diagnostics = new MemoryDiagnostics();
        await RequireThrowsAsync<TException>(async () =>
        {
            await DpViaCorridorCaptureRunner.RunAsync(
                gateway, diagnostics, () => null, "test", "test",
                new(0, null, false), false, CancellationToken.None);
        });
        return diagnostics.Receipt ??
            throw new InvalidOperationException("The failed staged run retained no receipt.");
    }

    private static CorridorReplayBatch StagedBatch(int id, DesignBounds region) => new(
        id,
        new("ETCH/S03"),
        region,
        100_000,
        [],
        ViaPadMeasurementSelection.MatchingNetNameSuffixes("_P", "_N"));

    private const string ObservationA = "0123456789abcdef0123456789abcdef";
    private const string ObservationB = "abcdef0123456789abcdef0123456789";

    private static CorridorFrozenSource StagedFrozen(string observationId, bool verified = true) => new(
        observationId, new string('a', 64), 4242, 1_000_000, verified);

    private static LargeBoardCaptureStoreInfo StagedStore(
        string captureToken, CorridorFrozenSource? frozen)
    {
        var traversal = new CorridorSourceTraversal(
            1, "all_board_copper_roots_v1", "session", 1, captureToken, 8)
        {
            FrozenSource = frozen,
        };
        return new(
            new(captureToken, "session", 1, 1, 123, "fixture.brd", "PD_V25")
            {
                SourceTraversal = traversal,
            },
            true,
            3,
            8,
            4_096,
            [],
            [])
        {
            SourceTraversal = traversal,
        };
    }

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private sealed class FixtureGateway(EngineBulkCapture capture) : ILargeBoardCaptureGateway<DesignScene>
    {
        public WorkspaceDocumentIdentity Document { get; } = new(
            capture.Info.Identity.SessionId, capture.Info.Identity.SessionGeneration,
            capture.Info.Identity.BoardGeneration, capture.Info.Identity.ProcessId,
            capture.Info.Identity.Design, capture.Info.Identity.ProtocolVersion);
        public bool RejectedBroaderReplay { get; private set; }
        public bool PlannedReplayEquivalent { get; private set; }
        public SceneQuery CompatibleQuery { get; private set; } = null!;

        public async ValueTask<ILargeBoardCaptureStore<DesignScene>> CaptureAsync(
            LargeBoardCaptureBudgets budgets, CancellationToken cancellationToken)
        {
            Require(budgets.ViaPadMeasurements.Mode ==
                    ViaPadMeasurementSelectionMode.MatchingNetNameSuffixes &&
                    budgets.ViaPadMeasurements.NetNameSuffixes.SequenceEqual(new[] { "_P", "_N" }),
                "The ordinary capture was broadened instead of fixing replay compatibility.");
            CompatibleQuery = new()
            {
                Kind = SceneReadKind.RegionGeometry,
                Families = [DataFamily.Nets, DataFamily.Layers, DataFamily.Copper],
                CopperKinds = [CopperKind.Trace, CopperKind.Via, CopperKind.Shape],
                ViaPadMeasurements = budgets.ViaPadMeasurements,
                Region = new(new(-100, -100), new(100, 100)),
                Layers = [new("ETCH/S03")],
                IncludeContours = false,
            };
            await RequireThrowsAsync<InvalidOperationException>(async () =>
                await capture.ReplayAsync(CompatibleQuery with
                {
                    ViaPadMeasurements = ViaPadMeasurementSelection.All,
                }, cancellationToken));
            RejectedBroaderReplay = true;
            EngineBulkReplayPlan plan = await capture.CreateReplayPlanAsync(
                cancellationToken: cancellationToken);
            try
            {
                EngineBulkReplayOptions options = new();
                EngineBulkBatchReplayResult ordinary = await capture.ReplayBatchAsync(
                    [CompatibleQuery], options, cancellationToken);
                EngineBulkBatchReplayResult planned = await plan.ReplayBatchAsync(
                    [CompatibleQuery], options, cancellationToken);
                PlannedReplayEquivalent = ordinary.RootIndexPasses == 1 &&
                    planned.RootIndexPasses == 0 &&
                    planned.Results.Length == ordinary.Results.Length &&
                    planned.Results[0].ObjectIdentities.SequenceEqual(
                        ordinary.Results[0].ObjectIdentities) &&
                    planned.Results[0].Scene.Data.Copper.Select(item => item.Id)
                        .SequenceEqual(ordinary.Results[0].Scene.Data.Copper.Select(item => item.Id));
                Require(PlannedReplayEquivalent,
                    "The replay plan did not preserve the ordinary sealed batch's ordered copper.");
                return new EngineLargeBoardCaptureStore(
                    capture, plan, null, plan.Info.Total, null);
            }
            catch
            {
                await plan.DisposeAsync();
                throw;
            }
        }
    }

    private sealed class DiscardDiagnostics : IAcquisitionDiagnosticOwner
    {
        public ValueTask RetainAsync(AcquisitionDiagnosticReceipt receipt,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class MemoryDiagnostics : IAcquisitionDiagnosticOwner
    {
        public AcquisitionDiagnosticReceipt? Receipt { get; private set; }

        public ValueTask RetainAsync(AcquisitionDiagnosticReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            Receipt = receipt;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingStagedGateway(Exception failure)
        : ILargeBoardCaptureGateway<DesignScene>, IStagedLargeBoardCaptureGateway
    {
        public ValueTask<ILargeBoardCaptureStore<DesignScene>> CaptureAsync(
            LargeBoardCaptureBudgets budgets, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The staged timing check never uses the single-capture contract.");

        public async ValueTask<IStagedLargeBoardCaptureObservation> BeginObservationAsync(
            LargeBoardCaptureBudgets budgets, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
            throw failure;
        }
    }

    private static async Task RequireThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name} from real sealed Engine replay.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
