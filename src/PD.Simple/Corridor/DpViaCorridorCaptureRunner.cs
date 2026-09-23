using System.Diagnostics;
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
