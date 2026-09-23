using CircuitHub.AllegroBridge.Engine.Design;
using CircuitHub.AllegroBridge.Engine.Live;
using CircuitHub.AllegroBridge.Engine.Scenes;
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
        return 8;
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
